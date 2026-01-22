using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Controls;
using Path = System.IO.Path;
using MessageBox = System.Windows.MessageBox;

namespace AdvancedTools.Pages
{
    public partial class MoverWindow : Window
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // SI-приставки относительно метра
        private static readonly Dictionary<string, double> SiPrefixToMeterMultiplier =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["ATTO"] = 1e-18,
                ["FEMTO"] = 1e-15,
                ["PICO"] = 1e-12,
                ["NANO"] = 1e-9,
                ["MICRO"] = 1e-6,
                ["MILLI"] = 1e-3,
                ["CENTI"] = 1e-2,
                ["DECI"] = 1e-1,
                ["DECA"] = 1e1,
                ["HECTO"] = 1e2,
                ["KILO"] = 1e3,
                ["MEGA"] = 1e6,
                ["GIGA"] = 1e9,
                ["TERA"] = 1e12,
                ["PETA"] = 1e15,
                ["EXA"] = 1e18,
            };

        public MoverWindow()
        {
            InitializeComponent();
            BrowseInputButton.Click += BrowseInputButton_Click;
            BrowseOutputButton.Click += BrowseOutputButton_Click;
            MoveButton.Click += MoveButton_Click;
        }

        private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                InputFolderTextBox.Text = dialog.SelectedPath;
        }

        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                OutputFolderTextBox.Text = dialog.SelectedPath;
        }

        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            MoveButton.IsEnabled = false;
            LogTextBox.Clear();
            AppendLog("Начало обработки...");

            // Считываем UI только в UI-потоке
            string inputPath = InputFolderTextBox.Text;
            string outputPath = OutputFolderTextBox.Text;

            if (!double.TryParse(XCoordTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double xMm) ||
                !double.TryParse(YCoordTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double yMm) ||
                !double.TryParse(ZCoordTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zMm))
            {
                MessageBox.Show("Некорректные координаты", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                MoveButton.IsEnabled = true;
                return;
            }

            // В фоне только работа с данными, без UI
            await Task.Run(() => MoveModel(inputPath, outputPath, xMm, yMm, zMm));

            AppendLog("✅ Готово.");
            MoveButton.IsEnabled = true;
        }


        private void MoveModel(string inputFolder, string outputFolder, double xMm, double yMm, double zMm)
        {
            var files = Directory.GetFiles(inputFolder, "*.ifc", SearchOption.AllDirectories);
            int total = files.Length;
            int index = 0;

            foreach (var file in files)
            {
                index++;
                try
                {
                    var lines = File.ReadAllLines(file).ToList();
                    string rel = GetRelativePath(inputFolder, file);

                    // 1) Определяем единицы длины файла
                    if (!TryDetectLengthUnit(lines, out double mmToFileFactor, out string unitInfo, out bool isSi))
                    {
                        AppendLog($"[{index}/{total}] {rel} — ⚠ LENGTHUNIT не найден, считаю метры");
                        mmToFileFactor = 0.001; // мм -> м
                        unitInfo = "метры (fallback)";
                        isSi = true; // fallback по умолчанию считаем SI
                    }

                    if (!isSi)
                    {
                        AppendLog($"[{index}/{total}] {rel} — ❌ Длина измеряется не в единицах СИ (METRE не найден)");
                        continue;
                    }

                    // 2) Переводим смещение (ввод в мм) в единицы файла
                    double x = xMm * mmToFileFactor;
                    double y = yMm * mmToFileFactor;
                    double z = zMm * mmToFileFactor;

                    // 3) Быстрая проверка, что есть IFCLOCALPLACEMENT
                    bool hasLocalPlacement = lines.Any(l => l.IndexOf("=IFCLOCALPLACEMENT", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hasLocalPlacement)
                    {
                        AppendLog($"[{index}/{total}] {rel} — IFCLOCALPLACEMENT не найден");
                        continue;
                    }

                    // 4) Находим секцию DATA; ... ENDSEC; куда можно добавлять #...=
                    int dataIndex = lines.FindIndex(l => l.Trim().Equals("DATA;", StringComparison.OrdinalIgnoreCase));
                    if (dataIndex < 0)
                    {
                        AppendLog($"[{index}/{total}] {rel} — ⚠ DATA; не найден. Пропускаю файл.");
                        continue;
                    }

                    int endDataIndex = -1;
                    for (int i = dataIndex + 1; i < lines.Count; i++)
                    {
                        if (lines[i].Trim().Equals("ENDSEC;", StringComparison.OrdinalIgnoreCase))
                        {
                            endDataIndex = i;
                            break;
                        }
                    }

                    if (endDataIndex < 0)
                    {
                        AppendLog($"[{index}/{total}] {rel} — ⚠ ENDSEC; для секции DATA не найден. Пропускаю файл.");
                        continue;
                    }

                    // 5) Вычисляем последний ID (можно по всему файлу, но корректнее ограничиться DATA-секцией)
                    int lastId = 0;
                    for (int i = dataIndex; i < endDataIndex; i++)
                    {
                        var m = Regex.Match(lines[i], @"#\s*(\d+)\s*=");
                        if (m.Success)
                        {
                            int id = int.Parse(m.Groups[1].Value);
                            if (id > lastId) lastId = id;
                        }
                    }

                    int cartId = lastId + 1;
                    int axisId = lastId + 2;

                    string cartLine = $"#{cartId}=IFCCARTESIANPOINT(({Fmt(x)},{Fmt(y)},{Fmt(z)}));";
                    string axisLine = $"#{axisId}=IFCAXIS2PLACEMENT3D(#{cartId},$, $);";

                    // 6) Вставляем прямо перед ENDSEC секции DATA
                    lines.Insert(endDataIndex, cartLine);
                    lines.Insert(endDataIndex + 1, axisLine);

                    // 7) Меняем все IFCLOCALPLACEMENT($,#old) -> IFCLOCALPLACEMENT($,#axisId)
                    var lpRegex = new Regex(
                        @"=IFCLOCALPLACEMENT\(\s*\$\s*,\s*#\s*\d+\s*\)\s*;",
                        RegexOptions.IgnoreCase);

                    int replaced = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].IndexOf("=IFCLOCALPLACEMENT", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        if (lpRegex.IsMatch(lines[i]))
                        {
                            lines[i] = lpRegex.Replace(lines[i], $"=IFCLOCALPLACEMENT($, #{axisId});");
                            replaced++;
                        }
                    }

                    // 8) Сохраняем в выходную папку, сохраняя структуру
                    string outPath = Path.Combine(outputFolder, rel);
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllLines(outPath, lines);

                    AppendLog($"[{index}/{total}] {rel} — OK, единицы: {unitInfo}, вставка в DATA: #{cartId},#{axisId}, замен LOCALPLACEMENT: {replaced}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[{index}/{total}] {file} — ошибка: {ex.Message}");
                }
            }
        }


        /// <summary>
        /// Возвращает:
        /// - mmToFileFactor: множитель перевода мм → единицы файла
        /// - unitInfo: строка для лога
        /// - isSi: false, если LENGTHUNIT есть, но METRE нет
        /// </summary>
        private bool TryDetectLengthUnit(List<string> lines, out double mmToFileFactor, out string unitInfo, out bool isSi)
        {
            mmToFileFactor = 0;
            unitInfo = "";
            isSi = true;

            foreach (var line in lines)
            {
                if (!line.Contains("IFCSIUNIT")) continue;
                if (!line.Contains("LENGTH")) continue;

                bool hasMetre = line.Contains("METRE");
                if (!hasMetre)
                {
                    isSi = false;
                    return true;
                }

                foreach (var kv in SiPrefixToMeterMultiplier)
                {
                    if (line.Contains($".{kv.Key}."))
                    {
                        mmToFileFactor = 0.001 / kv.Value;
                        unitInfo = $"{kv.Key}-метры";
                        return true;
                    }
                }

                mmToFileFactor = 0.001;
                unitInfo = "метры";
                return true;
            }

            return false;
        }

        private static string Fmt(double v)
        {
            string s = v.ToString("0.############", Inv);
            return s.Contains('.') ? s : s + ".";
        }

        private void AppendLog(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(msg + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            var baseUri = new Uri(basePath.EndsWith(Path.DirectorySeparatorChar.ToString()) ? basePath : basePath + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(fullPath)).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
