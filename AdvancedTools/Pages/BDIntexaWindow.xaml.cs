using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using AdvancedTools.Tools.DB_INTEXA;
using System.Windows.Input; // для Mouse/Cursors

namespace AdvancedTools.Pages
{
    public partial class BDIntexaWindow : Window
    {
        private readonly Tools.DB_INTEXA.dbIntexaTableAdapters.IntexaTableAdapter _ta;
        private readonly DataIntexa _loader;

        public BDIntexaWindow()
        {
            InitializeComponent();

            _ta = new Tools.DB_INTEXA.dbIntexaTableAdapters.IntexaTableAdapter();
            _loader = new DataIntexa(_ta);

            Status("Готово.");
        }

        // ---------- UI helpers ----------
        private void Status(string text) => StatusText.Text = text;

        private void Log(string message)
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            LogTextBox.ScrollToEnd();
        }

        private void SetBusy(bool isBusy, bool indeterminate = true)
        {
            BrowseButton.IsEnabled = !isBusy;
            ReplaceButton.IsEnabled = !isBusy;
            AppendButton.IsEnabled = !isBusy;
            ClearDbButton.IsEnabled = !isBusy;
            ShowDbButton.IsEnabled = !isBusy;
            ClearLogButton.IsEnabled = !isBusy;

            Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;

            Progress.IsIndeterminate = indeterminate && isBusy;
            if (!indeterminate) Progress.Value = 0;
        }

        private bool EnsureFileChosen(out string path)
        {
            path = FileTextBox.Text;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "Сначала выберите существующий Excel-файл.", "Нет файла",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        // ---------- Handlers ----------
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выберите Excel-файл",
                Filter = "Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dlg.ShowDialog(this) == true)
            {
                FileTextBox.Text = dlg.FileName;
                Log($"Выбран файл: {dlg.FileName}");
                Status("Файл выбран.");
            }
        }

        private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureFileChosen(out var path)) return;

            SetBusy(true);
            Status("Загрузка (замена) началась…");
            try
            {
                int total = 0;
                var progress = new Progress<int>(n =>
                {
                    total = n;
                    Progress.IsIndeterminate = false;
                    // Если знаешь количество строк заранее — можно ставить Maximum = knownTotal
                    // Здесь просто анимируем до 100 циклично:
                    Progress.Value = (n % 100);
                    Log($"Обработано строк: {n}");
                });

                int rows = await _loader.ReplaceWithExcelAsync(path, progress);
                Log($"✅ Заменено строк: {rows}");
                Status("Готово.");
                Progress.Value = 100;
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка: {ex.Message}");
                MessageBox.Show(this, ex.ToString(), "Ошибка загрузки", MessageBoxButton.OK, MessageBoxImage.Error);
                Status("Ошибка.");
            }
            finally
            {
                SetBusy(false, indeterminate: false);
            }
        }

        private async void AppendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureFileChosen(out var path)) return;

            SetBusy(true);
            Status("Дозагрузка началась…");
            try
            {
                var progress = new Progress<int>(n =>
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = (n % 100);
                    Log($"Обработано строк: {n}");
                });

                int rows = await _loader.LoadFromExcelAsync(path, progress);
                Log($"✅ Добавлено строк: {rows}");
                Status("Готово.");
                Progress.Value = 100;
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка: {ex.Message}");
                MessageBox.Show(this, ex.ToString(), "Ошибка загрузки", MessageBoxButton.OK, MessageBoxImage.Error);
                Status("Ошибка.");
            }
            finally
            {
                SetBusy(false, indeterminate: false);
            }
        }

        private async void ClearDbButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Очистить таблицу dbo.Intexa?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            SetBusy(true);
            Status("Очистка…");
            try
            {
                int n = await _loader.ClearAsync();
                Log($"✅ Удалено строк: {n}");
                Status("Готово.");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка: {ex.Message}");
                MessageBox.Show(this, ex.ToString(), "Ошибка очистки", MessageBoxButton.OK, MessageBoxImage.Error);
                Status("Ошибка.");
            }
            finally
            {
                SetBusy(false, indeterminate: false);
            }
        }

        private async void ShowDbButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            Status("Чтение БД…");
            try
            {
                var dt = await Task.Run(() => _ta.GetData());
                Log($"Всего строк в БД: {dt.Rows.Count}");

                foreach (dbIntexa.IntexaRow r in dt.Rows)
                {
                    string sd = r.IsStartDateNull() ? "-" : r.StartDate.ToString("yyyy-MM-dd");
                    string ed = r.IsEndDateNull() ? "-" : r.EndDate.ToString("yyyy-MM-dd");
                    string id = r.Table.Columns.Contains("Id") && !r.IsNull("Id") ? r["Id"].ToString() : "-";
                    Log($"• {r.Project?.Trim()} | {r.Place?.Trim()} | Id={id} | {sd}..{ed}");
                }

                Log("✅ Вывод завершён.");
                Status("Готово.");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка: {ex.Message}");
                MessageBox.Show(this, ex.ToString(), "Ошибка чтения БД", MessageBoxButton.OK, MessageBoxImage.Error);
                Status("Ошибка.");
            }
            finally
            {
                SetBusy(false, indeterminate: false);
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            Status("Лог очищен.");
            Progress.Value = 0;
        }
    }
}
