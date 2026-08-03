using Ascon.Pilot.SDK;
using ClosedXML.Excel;
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

        public PilotFileOperations(IObjectsRepository repository, IFileProvider files, BimBridgeSettings settings, IBimBridgeLog log)
        {
            _repository = repository;
            _files = files;
            _settings = settings;
            _log = log;
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

            if (reference.FileId.HasValue && IsRelatedSourceFile(dataObject, reference.FileId.Value))
            {
                var sourceObject = await GetObjectAsync(reference.FileId.Value, token).ConfigureAwait(false);
                if (sourceObject != null)
                {
                    // Here FileId is the source-object id, not the id of IFile in its snapshot.
                    var sourceFile = SelectFile(sourceObject, reference, false);
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

        private static bool IsRelatedSourceFile(IDataObject dataObject, Guid fileObjectId)
        {
            try
            {
                return dataObject.RelatedSourceFiles != null && dataObject.RelatedSourceFiles.Contains(fileObjectId);
            }
            catch { return false; }
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
                using (var workbook = new XLWorkbook(sourcePath))
                using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                using (var text = new StreamWriter(stream, new UTF8Encoding(false)))
                using (var writer = new JsonTextWriter(text))
                {
                    var sheets = workbook.Worksheets.ToList();
                    var wanted = request?.SheetNames?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    if (wanted != null && wanted.Length > 0)
                    {
                        var missing = wanted.FirstOrDefault(name => sheets.All(sheet => !string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase)));
                        if (missing != null) throw new BimBridgeException(BimBridgeErrorCodes.ExcelSheetNotFound, "Requested worksheet was not found.");
                        sheets = sheets.Where(x => wanted.Any(name => string.Equals(name, x.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                    }
                    if (sheets.Count > _settings.FileOperations.MaxExcelSheets) LimitExceeded();
                    writer.WriteStartObject(); writer.WritePropertyName("ObjectId"); writer.WriteValue(objectId);
                    writer.WritePropertyName("FileName"); writer.WriteValue(SafeName(fileName, "workbook.xlsx"));
                    writer.WritePropertyName("ContentType"); writer.WriteValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    writer.WritePropertyName("Sheets"); writer.WriteStartArray(); long cells = 0;
                    foreach (var sheet in sheets)
                    {
                        token.ThrowIfCancellationRequested(); var range = sheet.RangeUsed();
                        writer.WriteStartObject(); writer.WritePropertyName("Name"); writer.WriteValue(sheet.Name);
                        writer.WritePropertyName("UsedRange"); writer.WriteValue(range == null ? null : range.RangeAddress.ToString()); writer.WritePropertyName("Rows"); writer.WriteStartArray();
                        if (range != null) foreach (var row in range.Rows())
                        {
                            if (row.RowNumber() > _settings.FileOperations.MaxExcelRowsPerSheet) LimitExceeded();
                            writer.WriteStartObject(); writer.WritePropertyName("RowNumber"); writer.WriteValue(row.RowNumber()); writer.WritePropertyName("Cells"); writer.WriteStartArray();
                            foreach (var cell in row.Cells())
                            {
                                token.ThrowIfCancellationRequested(); if (!request.IncludeEmptyCells && cell.IsEmpty()) continue; if (++cells > _settings.FileOperations.MaxExcelCells) LimitExceeded();
                                writer.WriteStartObject(); writer.WritePropertyName("Address"); writer.WriteValue(cell.Address.ToString()); writer.WritePropertyName("ColumnNumber"); writer.WriteValue(cell.Address.ColumnNumber);
                                WriteCell(writer, cell, request.IncludeFormulas); writer.WriteEndObject();
                            }
                            writer.WriteEndArray(); writer.WriteEndObject();
                        }
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WritePropertyName("Warnings"); writer.WriteStartArray(); writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                    if (stream.Length > _settings.FileOperations.MaxExcelResultBytes) LimitExceeded();
                }
            }
            catch (BimBridgeException) { throw; }
            catch (Exception ex) { throw new BimBridgeException(BimBridgeErrorCodes.ExcelOpenFailed, "Unable to read Excel workbook.", false, ex); }
        }

        private static void WriteCell(JsonWriter writer, IXLCell cell, bool includeFormulas)
        {
            writer.WritePropertyName("ValueType"); writer.WriteValue(cell.DataType.ToString()); writer.WritePropertyName("Value");
            switch (cell.DataType)
            {
                case XLDataType.Blank: writer.WriteNull(); break;
                case XLDataType.Number: writer.WriteValue(cell.GetDouble()); break;
                case XLDataType.Boolean: writer.WriteValue(cell.GetBoolean()); break;
                case XLDataType.DateTime: writer.WriteValue(cell.GetDateTime().ToUniversalTime()); break;
                case XLDataType.TimeSpan: writer.WriteValue(cell.GetTimeSpan().ToString()); break;
                case XLDataType.Error: writer.WriteValue(cell.GetError().ToString()); break;
                default: writer.WriteValue(cell.GetString()); break;
            }
            writer.WritePropertyName("Formula"); writer.WriteValue(includeFormulas && !string.IsNullOrWhiteSpace(cell.FormulaA1) ? cell.FormulaA1 : null);
        }

        private async Task<PreparedResult> CreateResultAsync(string path, string name, string contentType, string kind, CancellationToken token)
        {
            var length = new FileInfo(path).Length; string hash;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(stream).Select(x => x.ToString("x2")));
            await Task.CompletedTask.ConfigureAwait(false);
            return new PreparedResult { Path = path, FileName = SafeName(name, "result.bin"), ResultKind = kind, ContentType = contentType, ContentEncoding = "identity", Length = length, Sha256 = hash, DeleteOnDispose = _settings.FileOperations.DeleteTemporaryFiles };
        }

        private Task<IDataObject> GetObjectAsync(Guid id, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IDataObject>(); IDisposable subscription = null;
            subscription = _repository.SubscribeObjects(new[] { id }).Subscribe(new Observer<IDataObject>(item => { if (item != null && item.Id == id) { completion.TrySetResult(item); subscription?.Dispose(); } }, ex => completion.TrySetException(ex), () => completion.TrySetResult(null)));
            token.Register(() => { subscription?.Dispose(); completion.TrySetCanceled(); }); return completion.Task;
        }

        private bool EnsureDirectory() { try { Directory.CreateDirectory(_settings.FileOperations.TemporaryDirectory); return true; } catch { return false; } }
        private string NewPath(Guid taskId, string extension) { if (!EnsureDirectory()) throw new BimBridgeException(BimBridgeErrorCodes.TempFileIoError, "Temporary directory is not available."); return Path.Combine(_settings.FileOperations.TemporaryDirectory, taskId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + extension); }
        private static string SafeName(string value, string fallback) { var name = Path.GetFileName(value ?? string.Empty).Replace("..", string.Empty); return string.IsNullOrWhiteSpace(name) ? fallback : name; }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
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
