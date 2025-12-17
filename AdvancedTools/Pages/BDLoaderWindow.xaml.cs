using System;
using System.ComponentModel;
using System.Data.SqlClient;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms; // FolderBrowserDialog
using DataACCE;
using AdvancedTools.Tools.DB_ACCE.dbACCEDataSetTableAdapters; // << НОВОЕ
using System.Configuration;
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

        private const string ConnectionString = "Data Source=SRV2024\\SQLEXPRESS;Initial Catalog=dbACCE;Integrated Security=True;TrustServerCertificate=True";

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
                {
                    FolderTextBox.Text = dlg.SelectedPath;
                }
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
                using (var sqlConn = new SqlConnection(ConnectionString))
                {
                    sqlConn.Open();

                    // 3) Очищаем таблицы, как в FormACCE
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

                    _bw.ReportProgress(-1, $"Записано строк: Main={u1}, PipeComp={u2}, Diam={u3}, InsuPaint={u4}, Steel={u5}, Cable={u6}, Inst={u7}, Concrete={u8}");
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
                // переброс в UI-поток
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
            }
        }


        // === БЛОК РАБОТЫ С БД (аналог метода в FormACCE) ===

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
            AppendLog("Очистка таблиц…");
            TruncateTable(sqlConn, "MainTable");
            TruncateTable(sqlConn, "PipeComponents");
            TruncateTable(sqlConn, "Diameter");
            TruncateTable(sqlConn, "InsuPaint");
            TruncateTable(sqlConn, "Steel");
            TruncateTable(sqlConn, "Cable");
            TruncateTable(sqlConn, "Instrumentation");
            TruncateTable(sqlConn, "Concrete");
        }

        private void TruncateTable(SqlConnection sqlConn, string tableName)
        {
            void Exec(string sql)
            {
                using (var cmd = new SqlCommand(sql, sqlConn))
                    cmd.ExecuteNonQuery();
            }

            Exec($"ALTER TABLE {tableName} NOCHECK CONSTRAINT ALL");
            Exec($"DELETE FROM {tableName}");
            Exec($"ALTER TABLE {tableName} CHECK CONSTRAINT ALL");
        }
    }
}
