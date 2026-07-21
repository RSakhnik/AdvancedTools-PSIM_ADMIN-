using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using DataACCE;
using AdvancedTools.Tools.DB_ACCE.dbACCEDataSetTableAdapters;
using WinForms = System.Windows.Forms;

namespace AdvancedTools.Pages
{
    public partial class BDLoaderWindow : Window
    {
        private BackgroundWorker _bw;

        private MainTableTableAdapter _mainTableAdapter;
        private PipeComponentsTableAdapter _pipeCompAdapter;
        private DiameterTableAdapter _diamAdapter;
        private InsuPaintTableAdapter _insuPaintAdapter;
        private SteelTableAdapter _steelAdapter;
        private CableTableAdapter _cableAdapter;
        private InstrumentationTableAdapter _instAdapter;
        private ConcreteTableAdapter _concreteAdapter;

        private readonly string ConnectionString =
            global::AdvancedTools.Properties.Settings.Default.dbACCEConnectionString1;

        // Подстрой под реальные имена колонок в SQL-таблицах, если отличаются
        private const string ProjectColumnName = "Project";
        private const string StageColumnName = "Phase";

        public BDLoaderWindow()
        {
            InitializeComponent();
        }

        private sealed class FileIdentity
        {
            public string Stage { get; set; }
            public string Project { get; set; }
            public bool IsDelete { get; set; }
            public string FileName { get; set; }
        }

        #region UI

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку с отчётами ACCE";

                if (Directory.Exists(FolderTextBox.Text))
                    dlg.SelectedPath = FolderTextBox.Text;

                var result = dlg.ShowDialog();
                if (result == WinForms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                    FolderTextBox.Text = dlg.SelectedPath;
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FolderTextBox.Text) || !Directory.Exists(FolderTextBox.Text))
            {
                AppendLog("⚠ Не выбрана корректная папка с отчётами.");
                return;
            }

            ToggleUi(false);
            Progress.IsIndeterminate = true;
            Progress.Value = 0;
            StatusText.Text = "Загрузка…";

            _bw = new BackgroundWorker { WorkerReportsProgress = true };
            _bw.DoWork += Bw_DoWork;
            _bw.ProgressChanged += Bw_ProgressChanged;
            _bw.RunWorkerCompleted += Bw_RunWorkerCompleted;
            _bw.RunWorkerAsync(FolderTextBox.Text);
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        private void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            TestConnectionButton.IsEnabled = false;
            StatusText.Text = "Проверяю подключение…";
            Progress.IsIndeterminate = true;

            var bw = new BackgroundWorker();
            bw.DoWork += (s, args) =>
            {
                var cs = GetConnectionStringOrThrow();
                args.Result = TestSqlConnectionAndDumpMainTable(cs, timeoutSeconds: 7, log: AppendLog, dumpRows: 10);
            };

            bw.RunWorkerCompleted += (s, args) =>
            {
                Progress.IsIndeterminate = false;
                TestConnectionButton.IsEnabled = true;

                if (args.Error != null)
                {
                    AppendLog("❌ Ошибка проверки: " + args.Error.Message);
                    if (args.Error.InnerException != null)
                        AppendLog("   INNER: " + args.Error.InnerException.Message);
                    StatusText.Text = "Ошибка проверки";
                    return;
                }

                var ok = (bool)args.Result;
                if (ok)
                {
                    AppendLog("✔ Проверка пройдена: подключение и базовые права на запись/чтение есть.");
                    StatusText.Text = "Подключение ок";
                }
                else
                {
                    AppendLog("❌ Проверка не пройдена: см. лог.");
                    StatusText.Text = "Проблема с доступами/таблицей";
                }
            };

            try
            {
                var cs = GetConnectionStringOrThrow();
                AppendLog($"[DB] ConnectionString: {MaskPassword(cs)}");
            }
            catch (Exception ex)
            {
                AppendLog("❌ Не могу получить строку подключения: " + ex.Message);
                StatusText.Text = "Нет строки подключения";
                Progress.IsIndeterminate = false;
                TestConnectionButton.IsEnabled = true;
                return;
            }

            bw.RunWorkerAsync();
        }

        private void ToggleUi(bool enabled)
        {
            BrowseButton.IsEnabled = enabled;
            StartButton.IsEnabled = enabled;

            if (TestConnectionButton != null)
                TestConnectionButton.IsEnabled = enabled;

            if (ClearLogButton != null)
                ClearLogButton.IsEnabled = enabled;
        }

        private void AppendLog(string line)
        {
            if (Dispatcher.CheckAccess())
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                LogTextBox.ScrollToEnd();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
            }
        }

        private void Bw_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is string s && !string.IsNullOrWhiteSpace(s))
                AppendLog(s);

            if (e.ProgressPercentage >= 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Min(100, e.ProgressPercentage);
                StatusText.Text = $"Processing… {Progress.Value:0}%";
            }
        }

        private void Bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error == null)
            {
                AppendLog("✔ Готово.");
                StatusText.Text = "Готово";
                Progress.Value = 100;
            }
            else
            {
                AppendLog("❌ Завершено с ошибкой: " + e.Error.Message);
                if (e.Error.InnerException != null)
                    AppendLog("   INNER: " + e.Error.InnerException.Message);

                StatusText.Text = "Ошибка";
            }

            ToggleUi(true);
            Progress.IsIndeterminate = false;
        }

        #endregion

        #region Main workflow

        private void Bw_DoWork(object sender, DoWorkEventArgs e)
        {
            string folder = (string)e.Argument;
            var files = EnumerateExcelFiles(folder).ToList();

            if (files.Count == 0)
                throw new InvalidOperationException("В папке не найдено Excel-файлов.");

            ReportLog($"Найдено файлов: {files.Count}");

            string cs = GetConnectionStringOrThrow();

            using (var sqlConn = new SqlConnection(cs))
            {
                sqlConn.Open();

                for (int i = 0; i < files.Count; i++)
                {
                    string filePath = files[i];
                    var fileIdentity = ParseFileIdentity(filePath);

                    ReportLog(string.Empty);
                    ReportLog($"=== [{i + 1}/{files.Count}] {fileIdentity.FileName} ===");
                    ReportLog($"Stage = {fileIdentity.Stage}, Project = {fileIdentity.Project}, Delete = {fileIdentity.IsDelete}");

                    ProcessSingleFile(sqlConn, filePath, fileIdentity);

                    int progress = (int)Math.Round((i + 1) * 100.0 / files.Count);
                    ReportProgress(progress);
                }
            }
        }

        private void ProcessSingleFile(SqlConnection sqlConn, string filePath, FileIdentity fileIdentity)
        {
            using (var tx = sqlConn.BeginTransaction())
            {
                try
                {
                    InitAdapters(sqlConn, tx);

                    int deleted = DeleteRecordsByProjectStage(sqlConn, tx, fileIdentity.Project, fileIdentity.Stage);
                    ReportLog($"Удалено старых строк: {deleted}");

                    if (!fileIdentity.IsDelete)
                    {
                        var result = LoadSingleFile(filePath);

                        int u1 = _mainTableAdapter.Update(result.MainTable);
                        int u2 = _pipeCompAdapter.Update(result.PipeComponents);
                        int u3 = _diamAdapter.Update(result.Diameter);
                        int u4 = _insuPaintAdapter.Update(result.InsuPaint);
                        int u5 = _steelAdapter.Update(result.Steel);
                        int u6 = _cableAdapter.Update(result.Cable);
                        int u7 = _instAdapter.Update(result.Instrumentation);
                        int u8 = _concreteAdapter.Update(result.Concrete);

                        ReportLog(
                            $"Записано строк: Main={u1}, PipeComp={u2}, Diam={u3}, InsuPaint={u4}, Steel={u5}, Cable={u6}, Inst={u7}, Concrete={u8}");
                    }
                    else
                    {
                        ReportLog("Файл помечен как DELETE, загрузка новых данных не выполняется.");
                    }

                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
        }

        private LoadResult LoadSingleFile(string filePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "BDLoader_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var oldReportsFolder = Data.ReportsFolder;
            var oldLog = Data.Log;

            try
            {
                string copiedFile = Path.Combine(tempDir, Path.GetFileName(filePath));
                File.Copy(filePath, copiedFile, true);

                Data.ReportsFolder = tempDir;
                Data.Log = msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg) &&
                        msg.StartsWith("Найдено Excel-файлов:", StringComparison.OrdinalIgnoreCase))
                        return;

                    ReportLog(msg);
                };

                return Data.GetData(_bw, 0);
            }
            finally
            {
                Data.ReportsFolder = oldReportsFolder;
                Data.Log = oldLog;

                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        #endregion

        #region File parsing

        private static IEnumerable<string> EnumerateExcelFiles(string folder)
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    string ext = Path.GetExtension(path);
                    return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static FileIdentity ParseFileIdentity(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            string baseName = Path.GetFileNameWithoutExtension(fileName)?.Trim();

            if (string.IsNullOrWhiteSpace(baseName))
                throw new InvalidOperationException("Пустое имя файла.");

            bool isDelete = baseName.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0;

            string cleaned = baseName.Replace("DELETE", "").Trim();

            while (cleaned.Contains(".."))
                cleaned = cleaned.Replace("..", ".");

            cleaned = cleaned.Trim('.').Trim();

            var parts = cleaned.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
                throw new InvalidOperationException(
                    $"Имя файла '{fileName}' не соответствует формату '<STAGE>.<PROJECT>.xlsx'.");

            return new FileIdentity
            {
                Stage = parts[0].Trim(),
                Project = parts[1].Trim(),
                IsDelete = isDelete,
                FileName = fileName
            };
        }

        #endregion

        #region TableAdapters and transaction binding

        private void InitAdapters(SqlConnection conn, SqlTransaction tx)
        {
            _mainTableAdapter = new MainTableTableAdapter { Connection = conn };
            _pipeCompAdapter = new PipeComponentsTableAdapter { Connection = conn };
            _diamAdapter = new DiameterTableAdapter { Connection = conn };
            _insuPaintAdapter = new InsuPaintTableAdapter { Connection = conn };
            _steelAdapter = new SteelTableAdapter { Connection = conn };
            _cableAdapter = new CableTableAdapter { Connection = conn };
            _instAdapter = new InstrumentationTableAdapter { Connection = conn };
            _concreteAdapter = new ConcreteTableAdapter { Connection = conn };

            PrepareAdapterForTransaction(_mainTableAdapter, tx);
            PrepareAdapterForTransaction(_pipeCompAdapter, tx);
            PrepareAdapterForTransaction(_diamAdapter, tx);
            PrepareAdapterForTransaction(_insuPaintAdapter, tx);
            PrepareAdapterForTransaction(_steelAdapter, tx);
            PrepareAdapterForTransaction(_cableAdapter, tx);
            PrepareAdapterForTransaction(_instAdapter, tx);
            PrepareAdapterForTransaction(_concreteAdapter, tx);
        }

        private static void PrepareAdapterForTransaction(object adapter, SqlTransaction tx)
        {
            if (adapter == null)
                throw new ArgumentNullException(nameof(adapter));
            if (tx == null)
                throw new ArgumentNullException(nameof(tx));

            var type = adapter.GetType();

            var commandCollectionProp = type.GetProperty(
                "CommandCollection",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            SqlCommand[] commandCollection = null;
            if (commandCollectionProp != null)
                commandCollection = commandCollectionProp.GetValue(adapter, null) as SqlCommand[];

            var adapterProp = type.GetProperty(
                "Adapter",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var dataAdapter = adapterProp?.GetValue(adapter, null) as SqlDataAdapter;

            if (dataAdapter != null)
            {
                AttachCommandToTransaction(dataAdapter.SelectCommand, tx);
                AttachCommandToTransaction(dataAdapter.InsertCommand, tx);
                AttachCommandToTransaction(dataAdapter.UpdateCommand, tx);
                AttachCommandToTransaction(dataAdapter.DeleteCommand, tx);
            }

            if (commandCollection != null)
            {
                foreach (var cmd in commandCollection)
                    AttachCommandToTransaction(cmd, tx);
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (typeof(SqlCommand).IsAssignableFrom(field.FieldType))
                {
                    var cmd = field.GetValue(adapter) as SqlCommand;
                    AttachCommandToTransaction(cmd, tx);
                }
                else if (field.FieldType == typeof(SqlCommand[]))
                {
                    var cmds = field.GetValue(adapter) as SqlCommand[];
                    if (cmds != null)
                    {
                        foreach (var cmd in cmds)
                            AttachCommandToTransaction(cmd, tx);
                    }
                }
            }
        }

        private static void AttachCommandToTransaction(SqlCommand cmd, SqlTransaction tx)
        {
            if (cmd == null)
                return;

            cmd.Connection = tx.Connection;
            cmd.Transaction = tx;
        }

        #endregion

        #region Delete logic

        private int DeleteRecordsByProjectStage(SqlConnection conn, SqlTransaction tx, string project, string stage)
        {
            int total = 0;

            total += DeleteByProjectStage(conn, tx, "Concrete", project, stage);
            total += DeleteByProjectStage(conn, tx, "Instrumentation", project, stage);
            total += DeleteByProjectStage(conn, tx, "Cable", project, stage);
            total += DeleteByProjectStage(conn, tx, "Steel", project, stage);
            total += DeleteByProjectStage(conn, tx, "InsuPaint", project, stage);
            total += DeleteByProjectStage(conn, tx, "Diameter", project, stage);
            total += DeleteByProjectStage(conn, tx, "PipeComponents", project, stage);
            total += DeleteByProjectStage(conn, tx, "MainTable", project, stage);

            return total;
        }

        private int DeleteByProjectStage(SqlConnection conn, SqlTransaction tx, string tableName, string project, string stage)
        {
            string fullTableName = FindTableFullName(conn, tx, tableName, 10);
            if (string.IsNullOrWhiteSpace(fullTableName))
                throw new InvalidOperationException($"Таблица '{tableName}' не найдена.");

            string sql = $@"
DELETE FROM {fullTableName}
WHERE [{ProjectColumnName}] = @project
  AND [{StageColumnName}] = @stage;";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddWithValue("@project", project);
                cmd.Parameters.AddWithValue("@stage", stage);

                int n = cmd.ExecuteNonQuery();
                ReportLog($"  - {tableName}: удалено {n}");
                return n;
            }
        }

        #endregion

        #region DB helpers

        private string GetConnectionStringOrThrow()
        {
            if (!string.IsNullOrWhiteSpace(ConnectionString))
                return ConnectionString;

            throw new InvalidOperationException("Не найдена строка подключения (ConnectionString). Проверь Settings/app.config.");
        }

        private static string FindTableFullName(SqlConnection conn, string tableName, int timeoutSeconds)
        {
            return FindTableFullName(conn, null, tableName, timeoutSeconds);
        }

        private static string FindTableFullName(SqlConnection conn, SqlTransaction tx, string tableName, int timeoutSeconds)
        {
            const string sql = @"
SELECT TOP 1 QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name = @name
ORDER BY CASE WHEN s.name='dbo' THEN 0 ELSE 1 END, s.name;";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.CommandTimeout = Math.Max(1, timeoutSeconds);
                cmd.Parameters.AddWithValue("@name", tableName);
                return cmd.ExecuteScalar() as string;
            }
        }

        private static string MaskPassword(string connectionString)
        {
            try
            {
                var b = new SqlConnectionStringBuilder(connectionString);
                if (!string.IsNullOrEmpty(b.Password))
                    b.Password = "*****";
                return b.ConnectionString;
            }
            catch
            {
                return connectionString;
            }
        }

        #endregion

        #region Connection test

        private static bool TestSqlConnectionAndDumpMainTable(
            string connectionString,
            int timeoutSeconds,
            Action<string> log = null,
            int dumpRows = 10)
        {
            void Log(string s) => log?.Invoke(s);

            var b = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = Math.Max(1, timeoutSeconds)
            };

            using (var conn = new SqlConnection(b.ConnectionString))
            {
                try
                {
                    conn.Open();
                    Log("✔ CONNECT: соединение установлено");
                }
                catch (Exception ex)
                {
                    Log("✘ CONNECT: не удалось открыть соединение: " + ex.Message);
                    return false;
                }

                string mainFull;
                try
                {
                    mainFull = FindTableFullName(conn, "MainTable", timeoutSeconds);
                    if (string.IsNullOrWhiteSpace(mainFull))
                    {
                        Log("✘ TABLE [MainTable]: не найдена (или нет прав на metadata)");
                        return false;
                    }
                    Log($"✔ TABLE [MainTable] найдена как {mainFull}");
                }
                catch (Exception ex)
                {
                    Log("✘ TABLE [MainTable]: ошибка поиска: " + ex.Message);
                    return false;
                }

                try
                {
                    var (canSel, canIns, canUpd, canDel) = GetBasicObjectPermissions(conn, mainFull, timeoutSeconds);

                    Log($"{(canSel ? "✔" : "✘")} PERM SELECT {mainFull}: {(canSel ? "есть" : "нет")}");
                    Log($"{(canIns ? "✔" : "✘")} PERM INSERT {mainFull}: {(canIns ? "есть" : "нет")}");
                    Log($"{(canUpd ? "✔" : "✘")} PERM UPDATE {mainFull}: {(canUpd ? "есть" : "нет")}");
                    Log($"{(canDel ? "✔" : "✘")} PERM DELETE {mainFull}: {(canDel ? "есть" : "нет")}");
                }
                catch (Exception ex)
                {
                    Log("✘ PERM: ошибка проверки прав: " + ex.Message);
                    return false;
                }

                try
                {
                    DumpTableTop(conn, mainFull, timeoutSeconds, Log, dumpRows);
                }
                catch (Exception ex)
                {
                    Log("✘ DUMP MainTable: ошибка чтения: " + ex.Message);
                    if (ex.InnerException != null)
                        Log("  INNER: " + ex.InnerException.Message);
                    return false;
                }

                return true;
            }
        }

        private static (bool canSelect, bool canInsert, bool canUpdate, bool canDelete)
            GetBasicObjectPermissions(SqlConnection conn, string fullQuotedName, int timeoutSeconds)
        {
            var full = fullQuotedName.Replace("[", "").Replace("]", "");

            const string sql = @"
SELECT
    HAS_PERMS_BY_NAME(@obj, 'OBJECT', 'SELECT') AS CanSelect,
    HAS_PERMS_BY_NAME(@obj, 'OBJECT', 'INSERT') AS CanInsert,
    HAS_PERMS_BY_NAME(@obj, 'OBJECT', 'UPDATE') AS CanUpdate,
    HAS_PERMS_BY_NAME(@obj, 'OBJECT', 'DELETE') AS CanDelete;";

            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.CommandTimeout = Math.Max(1, timeoutSeconds);
                cmd.Parameters.AddWithValue("@obj", full);

                using (var rd = cmd.ExecuteReader())
                {
                    rd.Read();
                    bool cs = rd.GetInt32(0) == 1;
                    bool ci = rd.GetInt32(1) == 1;
                    bool cu = rd.GetInt32(2) == 1;
                    bool cd = rd.GetInt32(3) == 1;
                    return (cs, ci, cu, cd);
                }
            }
        }

        private static void DumpTableTop(SqlConnection conn, string fullQuotedName, int timeoutSeconds, Action<string> log, int dumpRows)
        {
            string sql = $"SELECT TOP ({Math.Max(1, dumpRows)}) * FROM {fullQuotedName};";

            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.CommandTimeout = Math.Max(1, timeoutSeconds);

                using (var rd = cmd.ExecuteReader())
                {
                    if (!rd.HasRows)
                    {
                        log($"[MainTable] {fullQuotedName}: таблица пустая");
                        return;
                    }

                    var header = new StringBuilder();
                    header.Append("[MainTable] Columns: ");
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        if (i > 0) header.Append(" | ");
                        header.Append(rd.GetName(i));
                    }
                    log(header.ToString());

                    int rowIdx = 0;
                    while (rd.Read())
                    {
                        rowIdx++;
                        var line = new StringBuilder();
                        line.Append($"[MainTable] Row {rowIdx}: ");

                        for (int i = 0; i < rd.FieldCount; i++)
                        {
                            if (i > 0) line.Append(" | ");

                            object val = rd.IsDBNull(i) ? null : rd.GetValue(i);
                            string s = val == null ? "NULL" : val.ToString();
                            s = TrimForLog(s, 80);
                            line.Append(s);
                        }

                        log(line.ToString());
                    }
                }
            }
        }

        private static string TrimForLog(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= maxLen)
                return s;

            return s.Substring(0, maxLen) + "…";
        }

        #endregion

        #region Logging helpers

        private void ReportLog(string message)
        {
            _bw?.ReportProgress(-1, message);
        }

        private void ReportProgress(int progress)
        {
            _bw?.ReportProgress(progress);
        }

        #endregion
    }
}