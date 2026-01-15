using System;
using System.ComponentModel;
using System.Data.SqlClient;
using System.IO;
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

        // TableAdapters как в FormACCE
        private MainTableTableAdapter _mainTableAdapter;
        private PipeComponentsTableAdapter _pipeCompAdapter;
        private DiameterTableAdapter _diamAdapter;
        private InsuPaintTableAdapter _insuPaintAdapter;
        private SteelTableAdapter _steelAdapter;
        private CableTableAdapter _cableAdapter;
        private InstrumentationTableAdapter _instAdapter;
        private ConcreteTableAdapter _concreteAdapter;

        // Строка подключения из Settings
        private string ConnectionString = global::AdvancedTools.Properties.Settings.Default.dbACCEConnectionString1;

        public BDLoaderWindow()
        {
            InitializeComponent();
        }

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

        // ====== Проверка подключения + вывод содержимого MainTable ======
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

        private void Bw_DoWork(object sender, DoWorkEventArgs e)
        {
            string folder = (string)e.Argument;

            // привязываем лог к UI
            Data.ReportsFolder = folder;
            Data.Log = msg => _bw.ReportProgress(-1, msg);

            try
            {
                // 1) Загружаем Excel в DataTables (без БД)
                var result = Data.GetData(_bw, startProgress: 0);

                // 2) Подключаемся к БД
                var cs = GetConnectionStringOrThrow();
                using (var sqlConn = new SqlConnection(cs))
                {
                    sqlConn.Open();

                    // 3) Очищаем таблицы (БЕЗ ALTER CONSTRAINT)
                    CleanUpTables(sqlConn);

                    // 4) Инициализируем TableAdapter’ы поверх этого подключения
                    InitAdapters(sqlConn);

                    // 5) Записываем данные
                    _bw.ReportProgress(-1, "Запись данных в БД…");

                    int u1 = _mainTableAdapter.Update(result.MainTable);
                    _bw.ReportProgress(15);

                    int u2 = _pipeCompAdapter.Update(result.PipeComponents);
                    _bw.ReportProgress(30);

                    int u3 = _diamAdapter.Update(result.Diameter);
                    _bw.ReportProgress(45);

                    int u4 = _insuPaintAdapter.Update(result.InsuPaint);
                    _bw.ReportProgress(60);

                    int u5 = _steelAdapter.Update(result.Steel);
                    _bw.ReportProgress(70);

                    int u6 = _cableAdapter.Update(result.Cable);
                    _bw.ReportProgress(80);

                    int u7 = _instAdapter.Update(result.Instrumentation);
                    _bw.ReportProgress(90);

                    int u8 = _concreteAdapter.Update(result.Concrete);
                    _bw.ReportProgress(95);

                    _bw.ReportProgress(-1,
                        $"Записано строк: Main={u1}, PipeComp={u2}, Diam={u3}, InsuPaint={u4}, Steel={u5}, Cable={u6}, Inst={u7}, Concrete={u8}");
                }

                _bw.ReportProgress(100);
            }
            catch (Exception ex)
            {
                _bw.ReportProgress(-1, "❌ Ошибка: " + ex.Message);
                throw;
            }
        }

        private void Bw_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is string s && !string.IsNullOrEmpty(s))
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
                AppendLog("❌ Завершено с ошибкой.");
                StatusText.Text = "Ошибка";
            }

            ToggleUi(true);
            Progress.IsIndeterminate = false;
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
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

        // === БЛОК РАБОТЫ С БД ===

        private void InitAdapters(SqlConnection conn)
        {
            _mainTableAdapter = new MainTableTableAdapter { Connection = conn };
            _pipeCompAdapter = new PipeComponentsTableAdapter { Connection = conn };
            _diamAdapter = new DiameterTableAdapter { Connection = conn };
            _insuPaintAdapter = new InsuPaintTableAdapter { Connection = conn };
            _steelAdapter = new SteelTableAdapter { Connection = conn };
            _cableAdapter = new CableTableAdapter { Connection = conn };
            _instAdapter = new InstrumentationTableAdapter { Connection = conn };
            _concreteAdapter = new ConcreteTableAdapter { Connection = conn };
        }

        private void CleanUpTables(SqlConnection sqlConn)
        {
            AppendLog("Очистка таблиц");

            // Обычно MainTable является "родительской", поэтому чистим её последней.
            // Порядок: дочерние -> родительские
            DeleteAll(sqlConn, "Concrete");
            DeleteAll(sqlConn, "Instrumentation");
            DeleteAll(sqlConn, "Cable");
            DeleteAll(sqlConn, "Steel");
            DeleteAll(sqlConn, "InsuPaint");
            DeleteAll(sqlConn, "Diameter");
            DeleteAll(sqlConn, "PipeComponents");
            DeleteAll(sqlConn, "MainTable");
        }

        private void DeleteAll(SqlConnection sqlConn, string tableName)
        {
            string full = FindTableFullName(sqlConn, tableName, timeoutSeconds: 10);
            if (string.IsNullOrWhiteSpace(full))
                throw new InvalidOperationException($"Таблица '{tableName}' не найдена (или нет прав на просмотр метаданных).");

            using (var cmd = new SqlCommand($"DELETE FROM {full};", sqlConn))
            {
                cmd.CommandTimeout = 60;
                int n = cmd.ExecuteNonQuery();
                AppendLog($"  - очищено {n} строк из {full}");
            }
        }

        // ====== helpers ======

        private string GetConnectionStringOrThrow()
        {
            if (!string.IsNullOrWhiteSpace(ConnectionString))
                return ConnectionString;

            throw new InvalidOperationException("Не найдена строка подключения (ConnectionString). Проверь Settings/app.config.");
        }

        /// <summary>
        /// Проверяет подключение, базовые права на MainTable и выводит TOP N строк MainTable в лог.
        /// </summary>
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

                // 1) Проверим, что MainTable существует и доступна
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

                // 2) Проверим основные права (то, что тебе реально нужно для “обновлять данные”)
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

                // 3) Вывод содержимого MainTable
                try
                {
                    DumpTableTop(conn, mainFull, timeoutSeconds, Log);
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

        private static void DumpTableTop(SqlConnection conn, string fullQuotedName, int timeoutSeconds, Action<string> log)
        {
            var sql = $"SELECT * FROM {fullQuotedName};";

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

                    // Заголовок
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

                            // чтобы лог не превращался в свалку
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
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "…";
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

        // Ищем таблицу по имени, возвращаем [schema].[table]
        private static string FindTableFullName(SqlConnection conn, string tableName, int timeoutSeconds)
        {
            const string sql = @"
SELECT TOP 1 QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name = @name
ORDER BY CASE WHEN s.name='dbo' THEN 0 ELSE 1 END, s.name;";

            using (var cmd = new SqlCommand(sql, conn))
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
    }
}
