using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace AdvancedTools.Tools.DB_INTEXA
{
    /// <summary>
    /// Загрузка данных в dbo.Intexa из Excel с помощью TableAdapter из dbIntexa.xsd.
    /// </summary>
    internal class DataIntexa
    {
        private readonly dbIntexaTableAdapters.IntexaTableAdapter _adapter;

        public DataIntexa(dbIntexaTableAdapters.IntexaTableAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            ForceUnicodeParams();
        }

        private void ForceUnicodeParams()
        {
            var cmds = new[] { _adapter.Adapter.InsertCommand, _adapter.Adapter.UpdateCommand };
            foreach (var cmd in cmds.Where(c => c != null))
            {
                foreach (SqlParameter p in cmd.Parameters)
                {
                    if (p.SqlDbType == SqlDbType.VarChar)
                        p.SqlDbType = SqlDbType.NVarChar;  // критично
                }
            }
        }

        /// <summary>Полностью очищает таблицу dbo.Intexa (в транзакции).</summary>
        public async Task<int> ClearAsync()
        {
            var conn = (SqlConnection)_adapter.Connection;
            bool shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync().ConfigureAwait(false);
                shouldClose = true;
            }

            var tx = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM [dbo].[Intexa];";
                    int affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // Если IDENTITY есть – сбросим (ошибку проглатываем).
                    try
                    {
                        cmd.CommandText = "DBCC CHECKIDENT ('[dbo].[Intexa]', RESEED, 0);";
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    catch { /* ignore */ }

                    tx.Commit();
                    return affected;
                }
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }

        /// <summary>
        /// Полная замена данных: очистить и залить из Excel. Всё в одной транзакции.
        /// </summary>
        public async Task<int> ReplaceWithExcelAsync(string excelPath, IProgress<int> progress = null)
        {
            var rows = ReadExcelRows(excelPath).ToList();

            var conn = (SqlConnection)_adapter.Connection;
            bool shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync().ConfigureAwait(false);
                shouldClose = true;
            }

            var tx = conn.BeginTransaction();
            AssignTransactionToAdapter(tx);

            try
            {
                // 1) Очистка
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM [dbo].[Intexa];";
                    await del.ExecuteNonQueryAsync().ConfigureAwait(false);

                    try
                    {
                        del.CommandText = "DBCC CHECKIDENT ('[dbo].[Intexa]', RESEED, 0);";
                        await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    catch { /* ignore */ }
                }

                // 2) Заполнение DataTable и Update()
                int inserted = await InsertRowsAsync(rows, progress).ConfigureAwait(false);

                tx.Commit();
                return inserted;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }

        /// <summary>
        /// Дозагрузка из Excel (без очистки): просто добавляет строки.
        /// </summary>
        public async Task<int> LoadFromExcelAsync(string excelPath, IProgress<int> progress = null)
        {
            var rows = ReadExcelRows(excelPath).ToList();

            var conn = (SqlConnection)_adapter.Connection;
            bool shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync().ConfigureAwait(false);
                shouldClose = true;
            }

            var tx = conn.BeginTransaction();
            AssignTransactionToAdapter(tx);

            try
            {
                int inserted = await InsertRowsAsync(rows, progress).ConfigureAwait(false);
                tx.Commit();
                return inserted;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }

        // ====================== ВНУТРЕННИЕ МЕТОДЫ ======================

        /// <summary>
        /// Пакетная вставка через типизированный DataTable и TableAdapter.Update().
        /// </summary>
        private Task<int> InsertRowsAsync(List<IntexaExcelRow> rows, IProgress<int> progress)
        {
            // Важно: типы dbIntexa.* берутся из вашего xsd
            var table = new dbIntexa.IntexaDataTable();
            int count = 0;

            foreach (var r in rows)
            {
                var row = table.NewIntexaRow();

                // Строковые (Project/Place в БД nchar(10) → обрежем до 10 и Trim)
                row.Project = ToDbString(r.Project, 10);
                row.Place = ToDbString(r.Place, 10);

                // Если Id — НЕ identity, раскомментируйте следующую строку:
                if (r.Id.HasValue) row.Id = r.Id.Value; else row.SetIdNull();

                row.Category = ToDbString(r.Category);
                row.Type = ToDbString(r.Type);
                row.Status = ToDbString(r.Status);
                row.RespWorkManager = ToDbString(r.RespWorkManager);
                row.Giving = ToDbString(r.Giving);
                row.RespContractor = ToDbString(r.RespContractor);
                row.Concordant = ToDbString(r.Concordant);

                if (r.StartDate.HasValue) row.StartDate = r.StartDate.Value; else row.SetStartDateNull();
                if (r.EndDate.HasValue) row.EndDate = r.EndDate.Value; else row.SetEndDateNull();

                SetNullableDouble(row, nameof(row.X1), r.X1);
                SetNullableDouble(row, nameof(row.Y1), r.Y1);
                SetNullableDouble(row, nameof(row.Z1), r.Z1);

                SetNullableDouble(row, nameof(row.X2), r.X2);
                SetNullableDouble(row, nameof(row.Y2), r.Y2);
                SetNullableDouble(row, nameof(row.Z2), r.Z2);

                SetNullableDouble(row, nameof(row.X3), r.X3);
                SetNullableDouble(row, nameof(row.Y3), r.Y3);
                SetNullableDouble(row, nameof(row.Z3), r.Z3);

                SetNullableDouble(row, nameof(row.X4), r.X4);
                SetNullableDouble(row, nameof(row.Y4), r.Y4);
                SetNullableDouble(row, nameof(row.Z4), r.Z4);

                SetNullableDouble(row, nameof(row.X5), r.X5);
                SetNullableDouble(row, nameof(row.Y5), r.Y5);
                SetNullableDouble(row, nameof(row.Z5), r.Z5);

                SetNullableDouble(row, nameof(row.X6), r.X6);
                SetNullableDouble(row, nameof(row.Y6), r.Y6);
                SetNullableDouble(row, nameof(row.Z6), r.Z6);

                SetNullableDouble(row, nameof(row.X7), r.X7);
                SetNullableDouble(row, nameof(row.Y7), r.Y7);
                SetNullableDouble(row, nameof(row.Z7), r.Z7);

                SetNullableDouble(row, nameof(row.X8), r.X8);
                SetNullableDouble(row, nameof(row.Y8), r.Y8);
                SetNullableDouble(row, nameof(row.Z8), r.Z8);

                table.AddIntexaRow(row);
                count++;
                progress?.Report(count);
            }

            // TableAdapter сам вызовет INSERT для каждой строки
            _adapter.Update(table);
            return Task.FromResult(rows.Count);
        }

        /// <summary>
        /// Читает Excel. Ожидаются заголовки: Project, Place, (опц.) Id, Category, Type, Status, StartDate, EndDate, X1..X8, Y1..Y8.
        /// </summary>
        private IEnumerable<IntexaExcelRow> ReadExcelRows(string excelPath)
        {
            var wb = new XLWorkbook(excelPath);
            var ws = wb.Worksheets.First();

            var header = ws.FirstRowUsed();
            if (header == null) yield break;

            // Карта "имя столбца -> № колонки"
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in header.CellsUsed())
            {
                var name = cell.GetString().Trim();
                if (!string.IsNullOrEmpty(name))
                    map[name] = cell.Address.ColumnNumber;
            }

            int firstDataRow = header.RowNumber() + 1;
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? (firstDataRow - 1);
            if (lastRow < firstDataRow) yield break;

            for (int r = firstDataRow; r <= lastRow; r++)
            {
                bool allEmpty = AllBlank(ws, r, map, new[]
                    {
                        "Project","Place","Category","Type","Status","StartDate","EndDate",
                        "X1","Y1","Z1","X2","Y2","Z2","X3","Y3","Z3","X4","Y4","Z4",
                        "X5","Y5","Z5","X6","Y6","Z6","X7","Y7","Z7","X8","Y8","Z8",
                        "Id","ID","Номер НД","Номер","№","No","Идентификатор", "RespWorkManager", "Giving", "RespContractor", "Concordant"
                    });
                if (allEmpty) continue;

                yield return new IntexaExcelRow
                {
                    Project = GetString(ws, r, map, "Project"),
                    Place = GetString(ws, r, map, "Place"),
                    Id = GetIntAny(ws, r, map, "Id", "ID", "Номер НД", "Номер", "№", "No", "Идентификатор"),
                    Category = GetString(ws, r, map, "Category"),
                    Type = GetString(ws, r, map, "Type"),
                    Status = GetString(ws, r, map, "Status"),
                    StartDate = GetDate(ws, r, map, "StartDate"),
                    EndDate = GetDate(ws, r, map, "EndDate"),
                    RespWorkManager = GetString(ws, r, map, "RespWorkManager"),
                    Giving = GetString(ws, r, map, "Giving"),
                    RespContractor = GetString(ws, r, map, "RespContractor"),
                    Concordant = GetString(ws, r, map, "Concordant"),

                    X1 = GetDouble(ws, r, map, "X1"),
                    Y1 = GetDouble(ws, r, map, "Y1"),
                    Z1 = GetDouble(ws, r, map, "Z1"),
                    X2 = GetDouble(ws, r, map, "X2"),
                    Y2 = GetDouble(ws, r, map, "Y2"),
                    Z2 = GetDouble(ws, r, map, "Z2"),
                    X3 = GetDouble(ws, r, map, "X3"),
                    Y3 = GetDouble(ws, r, map, "Y3"),
                    Z3 = GetDouble(ws, r, map, "Z3"),
                    X4 = GetDouble(ws, r, map, "X4"),
                    Y4 = GetDouble(ws, r, map, "Y4"),
                    Z4 = GetDouble(ws, r, map, "Z4"),
                    X5 = GetDouble(ws, r, map, "X5"),
                    Y5 = GetDouble(ws, r, map, "Y5"),
                    Z5 = GetDouble(ws, r, map, "Z5"),
                    X6 = GetDouble(ws, r, map, "X6"),
                    Y6 = GetDouble(ws, r, map, "Y6"),
                    Z6 = GetDouble(ws, r, map, "Z6"),
                    X7 = GetDouble(ws, r, map, "X7"),
                    Y7 = GetDouble(ws, r, map, "Y7"),
                    Z7 = GetDouble(ws, r, map, "Z7"),
                    X8 = GetDouble(ws, r, map, "X8"),
                    Y8 = GetDouble(ws, r, map, "Y8"),
                    Z8 = GetDouble(ws, r, map, "Z8"),
                };
            }
        }

        // ---------- Helpers для Excel ----------

        private static bool AllBlank(IXLWorksheet ws, int row, Dictionary<string, int> map, IEnumerable<string> cols)
            => cols.All(c => !map.ContainsKey(c) || ws.Cell(row, map[c]).IsEmpty());

        private static string GetString(IXLWorksheet ws, int row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out int c)) return null;
            var s = ws.Cell(row, c).GetString();
            s = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return s;
        }

        private static int? GetInt(IXLWorksheet ws, int row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out int c)) return null;
            var cell = ws.Cell(row, c);
            if (cell.IsEmpty()) return null;

            if (cell.TryGetValue<int>(out var i)) return i;

            var s = cell.GetString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out i)) return i;
            return null;
        }

        private static DateTime? GetDate(IXLWorksheet ws, int row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out int c)) return null;
            var cell = ws.Cell(row, c);
            if (cell.IsEmpty()) return null;

            if (cell.TryGetValue<DateTime>(out var dt)) return dt.Date;

            // Excel serial
            if (cell.TryGetValue<double>(out var d))
            {
                try { return DateTime.FromOADate(d).Date; } catch { /* ignore */ }
            }

            var s = cell.GetString();
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d1)) return d1.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d2)) return d2.Date;
            return null;
        }

        private static double? GetDouble(IXLWorksheet ws, int row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out int c)) return null;
            var cell = ws.Cell(row, c);
            if (cell.IsEmpty()) return null;

            if (cell.TryGetValue<double>(out var d)) return d;

            var s = cell.GetString();
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d)) return d;
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out d)) return d;
            return null;
        }

        // ---------- Helpers для БД/Row ----------

        private static string ToDbString(string s, int? maxLen = null)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (maxLen.HasValue && s.Length > maxLen.Value)
                s = s.Substring(0, maxLen.Value);
            return s;
        }

        private static void SetNullableDouble(dbIntexa.IntexaRow row, string columnName, double? value)
        {
            if (!value.HasValue) row[columnName] = DBNull.Value;
            else row[columnName] = value.Value;
        }

        private void AssignTransactionToAdapter(SqlTransaction tx)
        {
            // CommandCollection у TableAdapter скрыт — трогаем только Insert/Update/Delete
            if (_adapter.Adapter.InsertCommand != null) _adapter.Adapter.InsertCommand.Transaction = tx;
            if (_adapter.Adapter.UpdateCommand != null) _adapter.Adapter.UpdateCommand.Transaction = tx;
            if (_adapter.Adapter.DeleteCommand != null) _adapter.Adapter.DeleteCommand.Transaction = tx;
        }

        /// <summary>Внутренний DTO для строки Excel.</summary>
        private sealed class IntexaExcelRow
        {
            public string Project { get; set; }
            public string Place { get; set; }
            public int? Id { get; set; }
            public string Category { get; set; }
            public string Type { get; set; }
            public string Status { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public double? X1 { get; set; }
            public double? Y1 { get; set; }
            public double? Z1 { get; set; }

            public double? X2 { get; set; }
            public double? Y2 { get; set; }
            public double? Z2 { get; set; }

            public double? X3 { get; set; }
            public double? Y3 { get; set; }
            public double? Z3 { get; set; }

            public double? X4 { get; set; }
            public double? Y4 { get; set; }
            public double? Z4 { get; set; }

            public double? X5 { get; set; }
            public double? Y5 { get; set; }
            public double? Z5 { get; set; }

            public double? X6 { get; set; }
            public double? Y6 { get; set; }
            public double? Z6 { get; set; }

            public double? X7 { get; set; }
            public double? Y7 { get; set; }
            public double? Z7 { get; set; }

            public double? X8 { get; set; }
            public double? Y8 { get; set; }
            public double? Z8 { get; set; }

            public string RespWorkManager { get; set; }
            public string Giving { get; set; }
            public string RespContractor { get; set; }
            public string Concordant { get; set; }
        }


        private static string ResolveColumn(Dictionary<string, int> map, params string[] candidates)
        {
            foreach (var c in candidates)
                if (map.ContainsKey(c)) return c;

            // Фазa 2: мягкий поиск по подстрокам
            foreach (var kv in map.Keys)
            {
                var k = kv.Trim().ToLowerInvariant();
                if (k == "id" || k == "ид" || k == "идентификатор" ||
                    k.Contains("номер нд") || k == "номер" || k == "no" || k == "№")
                    return kv;
            }
            return null;
        }

        private static int? GetIntAny(IXLWorksheet ws, int row, Dictionary<string, int> map, params string[] aliases)
        {
            var colName = ResolveColumn(map, aliases);
            if (colName == null) return null;

            // Дальше обычный GetInt-лайк, но по найденному имени
            int c = map[colName];
            var cell = ws.Cell(row, c);
            if (cell.IsEmpty()) return null;

            if (cell.TryGetValue<int>(out var i)) return i;

            var s = cell.GetString()?.Trim();
            if (string.IsNullOrEmpty(s)) return null;

            // выковыриваем цифры, если там "№ 1234"
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;

            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out i)) return i;

            return null;
        }
    }
}
