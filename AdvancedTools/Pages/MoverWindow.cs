using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace AdvancedTools.Pages
{
    /// <summary>
    /// Логика взаимодействия для MoverWindow.xaml
    /// </summary>
    public partial class MoverWindow : Window
    {
        public MoverWindow()
        {
            InitializeComponent();

            BrowseInputButton.Click += BrowseInputButton_Click;
            BrowseOutputButton.Click += BrowseOutputButton_Click;
            MoveButton.Click += MoveButton_Click;
        }

        private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку с исходной моделью";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    InputFolderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите выходную папку";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutputFolderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            MoveButton.IsEnabled = false;

            Dispatcher.Invoke(() => LogTextBox.Clear());
            AppendLog("Начало обработки...");

            string inputPath = InputFolderTextBox.Text;
            string outputPath = OutputFolderTextBox.Text;

            if (!double.TryParse(XCoordTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x))
            {
                System.Windows.MessageBox.Show("Некорректное значение X", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                MoveButton.IsEnabled = true;
                return;
            }

            if (!double.TryParse(YCoordTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y))
            {
                System.Windows.MessageBox.Show("Некорректное значение Y", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                MoveButton.IsEnabled = true;
                return;
            }

            if (!double.TryParse(ZCoordTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                System.Windows.MessageBox.Show("Некорректное значение Z", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                MoveButton.IsEnabled = true;
                return;
            }

            await Task.Run(() => MoveModel(inputPath, outputPath, x, y, z));

            AppendLog("✅ Готово.");
            MoveButton.IsEnabled = true;
        }


        private void MoveModel(string inputFolder, string outputFolder, double x, double y, double z)
        {
            try
            {
                var ifcFiles = Directory.GetFiles(inputFolder, "*.ifc", SearchOption.AllDirectories);
                int totalFiles = ifcFiles.Length, processed = 0;

                foreach (var file in ifcFiles)
                {
                    try
                    {
                        var lines = File.ReadAllLines(file).ToList();

                        // Найти IFCLOCALPLACEMENT
                        int localPlacementIndex = -1;
                        Match localPlacementMatch = null;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (localPlacementIndex == -1 && lines[i].Contains("=IFCLOCALPLACEMENT"))
                            {
                                var m = Regex.Match(lines[i], @"#(\d+)=IFCLOCALPLACEMENT\(\$, ?#(\d+)\);");
                                if (m.Success)
                                {
                                    localPlacementIndex = i;
                                    localPlacementMatch = m;
                                }
                            }
                            if (localPlacementIndex != -1)
                                break;
                        }

                        if (localPlacementIndex < 0 || localPlacementMatch == null)
                        {
                            AppendLog($"[{processed + 1}/{totalFiles}] {file} — IFCLOCALPLACEMENT не найден.");
                            processed++;
                            continue;
                        }

                        // Вычисляем последний ID
                        int lastId = lines
                            .Select(l => Regex.Match(l, @"#(\d+)=.*"))
                            .Where(m => m.Success)
                            .Select(m => int.Parse(m.Groups[1].Value))
                            .DefaultIfEmpty(0)
                            .Max();

                        int cartesianId = lastId + 1;
                        int axisId = lastId + 2;

                        string cartesianLine = $"#{cartesianId}=IFCCARTESIANPOINT(({(int)x}.,{(int)y}.,{(int)z}.));";
                        string axisLine = $"#{axisId}=IFCAXIS2PLACEMENT3D(#{cartesianId},$, $);";

                        // Находим индекс строки с "#lastId="
                        int insertIndex = lines.FindIndex(l =>
                        {
                            var m = Regex.Match(l, $@"#\s*{lastId}\s*=");
                            return m.Success;
                        });

                        if (insertIndex < 0)
                        {
                            // Фолбэк на конец файла
                            insertIndex = lines.Count - 1;
                        }

                        // Вставляем новые строки сразу после insertIndex
                        lines.Insert(insertIndex + 1, cartesianLine);
                        lines.Insert(insertIndex + 2, axisLine);

                        // Обновляем строку IFCLOCALPLACEMENT
                        string oldLine = lines[localPlacementIndex];
                        string newLine = Regex.Replace(oldLine,
                            @"=IFCLOCALPLACEMENT\(\$, ?#\d+\);",
                            $"=IFCLOCALPLACEMENT($, #{axisId});");
                        lines[localPlacementIndex] = newLine;

                        // Сохраняем в выходную папку, сохраняя структуру
                        string rel = GetRelativePath(inputFolder, file);
                        string outPath = Path.Combine(outputFolder, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                        File.WriteAllLines(outPath, lines);

                        AppendLog($"[{processed + 1}/{totalFiles}] {rel} — успешно.");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[{processed + 1}/{totalFiles}] {file} — ошибка: {ex.Message}");
                    }

                    processed++;
                }

                AppendLog("Обработка завершена.");
            }
            catch (Exception ex)
            {
                AppendLog($"Глобальная ошибка: {ex.Message}");
            }
        }



        private void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(message + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }

        private string GetRelativePath(string basePath, string fullPath)
        {
            Uri baseUri = new Uri(AppendDirectorySeparatorChar(basePath));
            Uri fullUri = new Uri(fullPath);
            Uri relativeUri = baseUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relativeUri.ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        private string AppendDirectorySeparatorChar(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }
            return path;
        }

    }
}
