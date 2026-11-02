using Ascon.Pilot.SDK;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    public sealed class PilotFileReference
    {
        public Guid ObjectId { get; set; }
        public Guid? FileId { get; set; }
        public string FileName { get; set; }
        public DateTime? SnapshotCreatedAtUtc { get; set; }
    }

    public sealed class DownloadPilotFileRequest { public PilotFileReference FileReference { get; set; } }
    public sealed class ReadExcelContentRequest
    {
        public PilotFileReference FileReference { get; set; }
        public string[] SheetNames { get; set; }
        public bool IncludeFormulas { get; set; }
        public bool IncludeEmptyCells { get; set; }
    }

    internal sealed class ResolvedPilotFile
    {
        public Guid ObjectId { get; set; }
        public Guid? MountObjectId { get; set; }
        public IFile File { get; set; }
    }

    public sealed class PilotFileOperations
    {
        private readonly IObjectsRepository _repository;
        private readonly IFileProvider _files;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly string _temporaryDirectory;

        public PilotFileOperations(
            IObjectsRepository repository,
            IFileProvider files,
            BimBridgeSettings settings,
            IBimBridgeLog log,
            WorkerTemporaryWorkspace workspace = null)
        {
            _repository = repository;
            _files = files;
            _settings = settings;
            _log = log;
            _temporaryDirectory = workspace?.FilesDirectory ?? _settings.FileOperations.TemporaryDirectory;
        }

        public bool IsAvailable => _repository != null && _files != null && EnsureDirectory();

        public async Task<PreparedResult> DownloadAsync(Guid taskId, PilotFileReference reference, Action<int, string> progress, CancellationToken token)
        {
            var resolved = await ResolveAsync(reference, token).ConfigureAwait(false);
            var path = NewPath(taskId, ".bin");
            try
            {
                await CopySourceAsync(resolved, path, progress, token).ConfigureAwait(false);
                return await CreateResultAsync(path, SafeName(resolved.File.Name, "file.bin"), "application/octet-stream", "file", token).ConfigureAwait(false);
            }
            catch { TryDelete(path); throw; }
        }

        public async Task<PreparedResult> ReadExcelAsync(Guid taskId, ReadExcelContentRequest request, Action<int, string> progress, CancellationToken token)
        {
            var resolved = await ResolveAsync(request?.FileReference, token).ConfigureAwait(false);
            if (!string.Equals(Path.GetExtension(resolved.File.Name), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new BimBridgeException(BimBridgeErrorCodes.UnsupportedFileType, "Only .xlsx files are supported.");

            var sourcePath = NewPath(taskId, ".xlsx");
            var jsonPath = NewPath(taskId, ".json");
            try
            {
                await CopySourceAsync(resolved, sourcePath, progress, token).ConfigureAwait(false);
                await Task.Run(() => WriteExcelJson(resolved.ObjectId, resolved.File.Name, sourcePath, jsonPath, request, token), token).ConfigureAwait(false);
                return await CreateResultAsync(jsonPath, taskId.ToString("D") + ".json", "application/json", "json", token).ConfigureAwait(false);
            }
            catch { TryDelete(jsonPath); throw; }
            finally { TryDelete(sourcePath); }
        }

        private async Task<ResolvedPilotFile> ResolveAsync(PilotFileReference reference, CancellationToken token)
        {
            if (reference == null || reference.ObjectId == Guid.Empty)
                throw new BimBridgeException(BimBridgeErrorCodes.FileReferenceInvalid, "FileReference.ObjectId is required.");
            var dataObject = await GetObjectAsync(reference.ObjectId, token).ConfigureAwait(false);
            if (dataObject == null)
                throw new BimBridgeException(BimBridgeErrorCodes.FileNotFound, "Pilot object was not found.");

            // FileId in the public URL may identify a related Pilot object of type File,
            // while IFile.Id identifies a physical entry in a file snapshot. Support both.
            var ownFile = SelectFile(dataObject, reference, reference.FileId != dataObject.Id);
            if (ownFile != null)
                return new ResolvedPilotFile
                {
                    ObjectId = reference.ObjectId,
                    MountObjectId = IsMountable(dataObject) ? (Guid?)dataObject.Id : null,
                    File = ownFile
                };

            if (reference.FileId.HasValue && reference.FileId.Value != dataObject.Id)
            {
                var sourceObject = await GetObjectAsync(reference.FileId.Value, token).ConfigureAwait(false);
                if (sourceObject != null)
                {
                    // Public Pilot links use the GUID of a source-file object,
                    // while IFile.Id identifies an entry inside a snapshot.  If
                    // FileId did not match a physical entry above, resolve it as
                    // an accessible Pilot object regardless of RelatedSourceFiles:
                    // that collection is not consistently populated in 26.11.
                    var sourceReference = new PilotFileReference
                    {
                        ObjectId = sourceObject.Id,
                        FileId = null,
                        FileName = reference.FileName,
                        SnapshotCreatedAtUtc = reference.SnapshotCreatedAtUtc
                    };
                    var sourceFile = SelectFile(sourceObject, sourceReference, false);
                    if (sourceFile != null)
                        return new ResolvedPilotFile
                        {
                            ObjectId = reference.ObjectId,
                            // Mount the containing document only when the SDK marks it mountable.
                            // Objects of type File are explicitly not mountable in Pilot 26.13.
                            MountObjectId = IsMountable(dataObject) ? (Guid?)dataObject.Id : null,
                            File = sourceFile
                        };
                }
            }

            throw new BimBridgeException(BimBridgeErrorCodes.FileNotFound, "Requested file was not found.");
        }

        private static IFile SelectFile(IDataObject dataObject, PilotFileReference reference, bool matchFileId)
        {
            var snapshots = new List<IFilesSnapshot>();
            if (dataObject.ActualFileSnapshot != null) snapshots.Add(dataObject.ActualFileSnapshot);
            snapshots.AddRange(dataObject.PreviousFileSnapshots ?? Enumerable.Empty<IFilesSnapshot>());
            if (reference.SnapshotCreatedAtUtc.HasValue)
            {
                var expected = reference.SnapshotCreatedAtUtc.Value.ToUniversalTime();
                snapshots = snapshots.Where(x => x != null && x.Created.ToUniversalTime() == expected).ToList();
                if (snapshots.Count == 0)
                    return null;
            }

            var candidates = snapshots.SelectMany(x => x.Files ?? Enumerable.Empty<IFile>())
                .Where(x => !matchFileId || !reference.FileId.HasValue || x.Id == reference.FileId.Value)
                .Where(x => string.IsNullOrWhiteSpace(reference.FileName) || string.Equals(x.Name, reference.FileName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Id).Select(x => x.First()).ToList();
            if (candidates.Count > 1)
                throw new BimBridgeException(BimBridgeErrorCodes.FileReferenceAmbiguous, "File reference matches more than one file.");
            return candidates.SingleOrDefault();
        }

        private static bool IsMountable(IDataObject dataObject)
        {
            try { return dataObject != null && dataObject.Type != null && dataObject.Type.IsMountable; }
            catch { return false; }
        }

        private async Task CopySourceAsync(ResolvedPilotFile resolved, string path, Action<int, string> progress, CancellationToken token)
        {
            if (resolved.File.Size > _settings.FileOperations.MaxSourceFileSizeBytes)
                throw new BimBridgeException(BimBridgeErrorCodes.FileTooLarge, "Pilot file exceeds the configured limit.");
            if (resolved.MountObjectId.HasValue)
            {
                try { await _repository.MountAsync(resolved.MountObjectId.Value).ConfigureAwait(false); }
                catch (Exception ex) { throw new BimBridgeException(BimBridgeErrorCodes.FileMountFailed, "Unable to mount Pilot object.", true, ex); }
            }
            try
            {
                using (var input = _files.OpenRead(resolved.File))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > _settings.FileOperations.MaxSourceFileSizeBytes)
                            throw new BimBridgeException(BimBridgeErrorCodes.FileTooLarge, "Pilot file exceeds the configured limit.");
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        progress?.Invoke((int)Math.Min(90, total * 90 / Math.Max(1, resolved.File.Size)), "Копирование файла Pilot");
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                }
            }
            catch (BimBridgeException) { throw; }
            catch (Exception ex) { throw new BimBridgeException(BimBridgeErrorCodes.FileCopyFailed, "Unable to copy Pilot file.", true, ex); }
        }

        private void WriteExcelJson(Guid objectId, string fileName, string sourcePath, string jsonPath, ReadExcelContentRequest request, CancellationToken token)
        {
            try
            {
                using (var document = SpreadsheetDocument.Open(sourcePath, false))
                using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                using (var text = new StreamWriter(stream, new UTF8Encoding(false)))
                using (var writer = new JsonTextWriter(text))
                {
                    var workbookPart = document.WorkbookPart;
                    if (workbookPart?.Workbook == null)
                        throw new BimBridgeException(BimBridgeErrorCodes.ExcelOpenFailed, "Excel workbook structure is invalid.");
                    var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
                    var wanted = request?.SheetNames?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    if (wanted != null && wanted.Length > 0)
                    {
                        var missing = wanted.FirstOrDefault(name => sheets.All(sheet => !string.Equals(sheet.Name?.Value, name, StringComparison.OrdinalIgnoreCase)));
                        if (missing != null) throw new BimBridgeException(BimBridgeErrorCodes.ExcelSheetNotFound, "Requested worksheet was not found.");
                        sheets = sheets.Where(x => wanted.Any(name => string.Equals(name, x.Name?.Value, StringComparison.OrdinalIgnoreCase))).ToList();
                    }
                    if (sheets.Count > _settings.FileOperations.MaxExcelSheets) LimitExceeded();
                    var sharedStrings = ReadSharedStrings(workbookPart);
                    var styles = ReadCellStyles(workbookPart);
                    var date1904 = workbookPart.Workbook.WorkbookProperties?.Date1904?.Value == true;
                    writer.WriteStartObject(); writer.WritePropertyName("ObjectId"); writer.WriteValue(objectId);
                    writer.WritePropertyName("FileName"); writer.WriteValue(SafeName(fileName, "workbook.xlsx"));
                    writer.WritePropertyName("ContentType"); writer.WriteValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    writer.WritePropertyName("Sheets"); writer.WriteStartArray(); long cells = 0;
                    foreach (var sheet in sheets)
                    {
                        token.ThrowIfCancellationRequested();
                        var worksheetPart = workbookPart.GetPartById(sheet.Id) as WorksheetPart;
                        if (worksheetPart == null) continue;
                        writer.WriteStartObject(); writer.WritePropertyName("Name"); writer.WriteValue(sheet.Name?.Value);
                        writer.WritePropertyName("Rows"); writer.WriteStartArray();
                        string usedRange = null;
                        var minColumn = 1;
                        var maxColumn = 0;
                        var maxRow = 0;
                        var nextRow = 1;
                        using (var reader = OpenXmlReader.Create(worksheetPart))
                        {
                            while (reader.Read())
                            {
                                token.ThrowIfCancellationRequested();
                                if (reader.ElementType == typeof(SheetDimension) && reader.IsStartElement)
                                {
                                    var dimension = (SheetDimension)reader.LoadCurrentElement();
                                    usedRange = dimension.Reference?.Value;
                                    ParseRange(usedRange, out minColumn, out maxColumn, out _, out maxRow);
                                    continue;
                                }
                                if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
                                var row = (Row)reader.LoadCurrentElement();
                                var rowNumber = checked((int)(row.RowIndex?.Value ?? (uint)nextRow));
                                if (request?.IncludeEmptyCells == true && maxColumn >= minColumn)
                                {
                                    while (nextRow < rowNumber && nextRow <= maxRow)
                                    {
                                        WriteOpenXmlRow(writer, nextRow, Array.Empty<Cell>(), minColumn, maxColumn, true, request.IncludeFormulas, sharedStrings, styles, date1904, ref cells, token);
                                        nextRow++;
                                    }
                                }
                                WriteOpenXmlRow(writer, rowNumber, row.Elements<Cell>().ToArray(), minColumn, maxColumn, request?.IncludeEmptyCells == true, request?.IncludeFormulas == true, sharedStrings, styles, date1904, ref cells, token);
                                nextRow = rowNumber + 1;
                            }
                        }
                        if (request?.IncludeEmptyCells == true && maxColumn >= minColumn)
                        {
                            while (nextRow <= maxRow)
                            {
                                WriteOpenXmlRow(writer, nextRow, Array.Empty<Cell>(), minColumn, maxColumn, true, request.IncludeFormulas, sharedStrings, styles, date1904, ref cells, token);
                                nextRow++;
                            }
                        }
                        writer.WriteEndArray();
                        writer.WritePropertyName("UsedRange"); writer.WriteValue(usedRange);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WritePropertyName("Warnings"); writer.WriteStartArray(); writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                    if (stream.Length > _settings.FileOperations.MaxExcelResultBytes) LimitExceeded();
                }
            }
            catch (BimBridgeException) { throw; }
            catch (Exception ex) { throw new BimBridgeException(BimBridgeErrorCodes.ExcelOpenFailed, "Unable to read Excel workbook.", false, ex); }
        }

        private void WriteOpenXmlRow(
            JsonWriter writer,
            int rowNumber,
            Cell[] sourceCells,
            int minColumn,
            int maxColumn,
            bool includeEmpty,
            bool includeFormulas,
            IReadOnlyList<string> sharedStrings,
            IReadOnlyList<CellStyleInfo> styles,
            bool date1904,
            ref long totalCells,
            CancellationToken token)
        {
            if (rowNumber > _settings.FileOperations.MaxExcelRowsPerSheet) LimitExceeded();
            var byColumn = sourceCells
                .Select(cell => new { Cell = cell, Column = GetColumnNumber(cell.CellReference?.Value) })
                .Where(x => x.Column > 0)
                .GroupBy(x => x.Column)
                .ToDictionary(x => x.Key, x => x.First().Cell);
            var columns = includeEmpty && maxColumn >= minColumn
                ? Enumerable.Range(minColumn, maxColumn - minColumn + 1)
                : byColumn.Keys.OrderBy(x => x);
            writer.WriteStartObject(); writer.WritePropertyName("RowNumber"); writer.WriteValue(rowNumber);
            writer.WritePropertyName("Cells"); writer.WriteStartArray();
            foreach (var column in columns)
            {
                token.ThrowIfCancellationRequested();
                byColumn.TryGetValue(column, out var cell);
                if (!includeEmpty && IsOpenXmlCellEmpty(cell)) continue;
                if (++totalCells > _settings.FileOperations.MaxExcelCells) LimitExceeded();
                writer.WriteStartObject();
                writer.WritePropertyName("Address"); writer.WriteValue(GetColumnName(column) + rowNumber);
                writer.WritePropertyName("ColumnNumber"); writer.WriteValue(column);
                WriteOpenXmlCell(writer, cell, includeFormulas, sharedStrings, styles, date1904);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }

        private static void WriteOpenXmlCell(
            JsonWriter writer,
            Cell cell,
            bool includeFormulas,
            IReadOnlyList<string> sharedStrings,
            IReadOnlyList<CellStyleInfo> styles,
            bool date1904)
        {
            var raw = cell?.CellValue?.Text;
            var dataType = cell?.DataType?.Value;
            var formula = cell?.CellFormula?.Text;
            var style = cell?.StyleIndex?.Value < styles.Count ? styles[(int)cell.StyleIndex.Value] : CellStyleInfo.Number;
            string valueType;
            object value;
            if (cell == null || (string.IsNullOrEmpty(raw) && cell.InlineString == null && string.IsNullOrEmpty(formula)))
            {
                valueType = "Null"; value = null;
            }
            else if (dataType == CellValues.SharedString)
            {
                valueType = "String";
                value = int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : string.Empty;
            }
            else if (dataType == CellValues.InlineString)
            {
                valueType = "String"; value = cell.InlineString?.InnerText ?? string.Empty;
            }
            else if (dataType == CellValues.Boolean)
            {
                valueType = "Boolean"; value = raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (dataType == CellValues.Error)
            {
                valueType = "Error"; value = raw;
            }
            else if (dataType == CellValues.Date && DateTime.TryParse(raw, out var explicitDate))
            {
                valueType = "DateTime"; value = DateTime.SpecifyKind(explicitDate, DateTimeKind.Unspecified);
            }
            else if (style.IsDateOrTime && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var serial))
            {
                if (style.IsTimeOnly)
                {
                    valueType = "TimeSpan"; value = TimeSpan.FromDays(serial).ToString();
                }
                else
                {
                    valueType = "DateTime";
                    value = date1904 ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial);
                }
            }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                valueType = "Number"; value = number;
            }
            else
            {
                valueType = "String"; value = raw ?? cell?.InnerText ?? string.Empty;
            }
            writer.WritePropertyName("ValueType"); writer.WriteValue(valueType);
            writer.WritePropertyName("Value"); writer.WriteValue(value);
            writer.WritePropertyName("Formula"); writer.WriteValue(includeFormulas && !string.IsNullOrWhiteSpace(formula) ? formula : null);
        }

        private static List<string> ReadSharedStrings(WorkbookPart workbookPart)
        {
            var table = workbookPart.SharedStringTablePart?.SharedStringTable;
            return table == null ? new List<string>() : table.Elements<SharedStringItem>().Select(x => x.InnerText).ToList();
        }

        private static List<CellStyleInfo> ReadCellStyles(WorkbookPart workbookPart)
        {
            var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
            var customFormats = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
                .Where(x => x.NumberFormatId != null)
                .ToDictionary(x => x.NumberFormatId.Value, x => x.FormatCode?.Value ?? string.Empty)
                ?? new Dictionary<uint, string>();
            var result = new List<CellStyleInfo>();
            foreach (var format in stylesheet?.CellFormats?.Elements<CellFormat>() ?? Enumerable.Empty<CellFormat>())
            {
                var id = format.NumberFormatId?.Value ?? 0;
                customFormats.TryGetValue(id, out var code);
                result.Add(CellStyleInfo.From(id, code));
            }
            if (result.Count == 0) result.Add(CellStyleInfo.Number);
            return result;
        }

        private static bool IsOpenXmlCellEmpty(Cell cell) =>
            cell == null || (string.IsNullOrEmpty(cell.CellValue?.Text) && cell.InlineString == null && cell.CellFormula == null);

        private static int GetColumnNumber(string reference)
        {
            var result = 0;
            foreach (var character in reference ?? string.Empty)
            {
                if (!char.IsLetter(character)) break;
                result = checked(result * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
            }
            return result;
        }

        private static string GetColumnName(int column)
        {
            var result = string.Empty;
            while (column > 0)
            {
                column--;
                result = (char)('A' + column % 26) + result;
                column /= 26;
            }
            return result;
        }

        private static void ParseRange(string range, out int minColumn, out int maxColumn, out int minRow, out int maxRow)
        {
            minColumn = 1; maxColumn = 0; minRow = 1; maxRow = 0;
            if (string.IsNullOrWhiteSpace(range)) return;
            var parts = range.Split(':');
            minColumn = GetColumnNumber(parts[0]);
            minRow = GetRowNumber(parts[0]);
            maxColumn = GetColumnNumber(parts.Length > 1 ? parts[1] : parts[0]);
            maxRow = GetRowNumber(parts.Length > 1 ? parts[1] : parts[0]);
        }

        private static int GetRowNumber(string reference)
        {
            var digits = new string((reference ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var result) ? result : 0;
        }

        private sealed class CellStyleInfo
        {
            public static readonly CellStyleInfo Number = new CellStyleInfo(false, false);
            private CellStyleInfo(bool isDateOrTime, bool isTimeOnly)
            {
                IsDateOrTime = isDateOrTime;
                IsTimeOnly = isTimeOnly;
            }
            public bool IsDateOrTime { get; }
            public bool IsTimeOnly { get; }

            public static CellStyleInfo From(uint id, string code)
            {
                var builtInDate = id >= 14 && id <= 22;
                var builtInTime = id >= 18 && id <= 21 || id >= 45 && id <= 47;
                var normalized = (code ?? string.Empty).ToLowerInvariant();
                var hasDate = normalized.Contains("yy") || normalized.Contains("dd");
                var hasTime = normalized.Contains("hh") || normalized.Contains("ss") || normalized.Contains("[h]");
                return new CellStyleInfo(builtInDate || builtInTime || hasDate || hasTime, (builtInTime || hasTime) && !hasDate);
            }
        }

        private async Task<PreparedResult> CreateResultAsync(string path, string name, string contentType, string kind, CancellationToken token)
        {
            var length = new FileInfo(path).Length; string hash;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(stream).Select(x => x.ToString("x2")));
            await Task.CompletedTask.ConfigureAwait(false);
                return new PreparedResult { Path = path, FileName = SafeName(name, "result.bin"), ResultKind = kind, ContentType = contentType, ContentEncoding = "identity", Length = length, Sha256 = hash, DeleteOnDispose = true };
        }

        private async Task<IDataObject> GetObjectAsync(Guid id, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IDataObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable subscription = null;
            using (token.Register(() => completion.TrySetCanceled()))
            {
                try
                {
                    subscription = _repository.SubscribeObjects(new[] { id }).Subscribe(
                        new Observer<IDataObject>(
                            item =>
                            {
                                if (item != null && item.Id == id)
                                    completion.TrySetResult(item);
                            },
                            ex => completion.TrySetException(ex),
                            () => completion.TrySetResult(null)));
                    return await completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
        }

        private bool EnsureDirectory() { try { Directory.CreateDirectory(_temporaryDirectory); return true; } catch { return false; } }
        private string NewPath(Guid taskId, string extension) { if (!EnsureDirectory()) throw new BimBridgeException(BimBridgeErrorCodes.TempFileIoError, "Temporary directory is not available."); return Path.Combine(_temporaryDirectory, taskId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + extension); }
        private static string SafeName(string value, string fallback) { var name = Path.GetFileName(value ?? string.Empty).Replace("..", string.Empty); return string.IsNullOrWhiteSpace(name) ? fallback : name; }
        private static void TryDelete(string path) { TemporaryFileCleaner.TryDelete(path); }
        private static void LimitExceeded() { throw new BimBridgeException(BimBridgeErrorCodes.ExcelContentLimitExceeded, "Excel content exceeds the configured limit."); }

        private sealed class Observer<T> : IObserver<T>
        {
            private readonly Action<T> _next; private readonly Action<Exception> _error; private readonly Action _completed;
            public Observer(Action<T> next, Action<Exception> error, Action completed) { _next = next; _error = error; _completed = completed; }
            public void OnNext(T value) { _next(value); } public void OnError(Exception error) { _error(error); } public void OnCompleted() { _completed(); }
        }
    }

    public sealed class DownloadPilotFileOperationHandler : IBimOperationHandler
    {
        private readonly PilotFileOperations _files; public DownloadPilotFileOperationHandler(PilotFileOperations files) { _files = files; }
        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "download-pilot-file" }; public string Capability => "download-pilot-file"; public bool IsAvailable => _files.IsAvailable;
        public async Task<object> ExecuteAsync(TaskExecutionContext context) => await _files.DownloadAsync(context.Assignment.TaskId, BimOperationDispatcher.ReadPayload<DownloadPilotFileRequest>(context.Assignment).FileReference, context.ReportProgress, context.CancellationToken).ConfigureAwait(false);
    }

    public sealed class ReadExcelContentOperationHandler : IBimOperationHandler
    {
        private readonly PilotFileOperations _files; public ReadExcelContentOperationHandler(PilotFileOperations files) { _files = files; }
        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "read-excel-content" }; public string Capability => "read-excel-content"; public bool IsAvailable => _files.IsAvailable;
        public async Task<object> ExecuteAsync(TaskExecutionContext context) { var request = BimOperationDispatcher.ReadPayload<ReadExcelContentRequest>(context.Assignment); return await _files.ReadExcelAsync(context.Assignment.TaskId, request, context.ReportProgress, context.CancellationToken).ConfigureAwait(false); }
    }
}
