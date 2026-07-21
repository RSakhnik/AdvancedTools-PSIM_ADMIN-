using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using static AdvancedTools.Tools.DB_ACCE.dbACCEDataSet;

namespace DataACCE
{
    public sealed class LoadResult
    {
        public MainTableDataTable MainTable { get; set; }
        public PipeComponentsDataTable PipeComponents { get; set; }
        public DiameterDataTable Diameter { get; set; }
        public InsuPaintDataTable InsuPaint { get; set; }
        public SteelDataTable Steel { get; set; }
        public CableDataTable Cable { get; set; }
        public InstrumentationDataTable Instrumentation { get; set; }
        public ConcreteDataTable Concrete { get; set; }

        public Dictionary<string, int> RowCounts { get; set; }
    }

    public static class Data
    {
        private static BackgroundWorker _bW;
        private static int _progress;

        #region Tables

        private static MainTableDataTable _mainTable;
        private static PipeComponentsDataTable _pipeCompTable;
        private static DiameterDataTable _diamTable;
        private static InsuPaintDataTable _insuPaintTable;
        private static SteelDataTable _steelTable;
        private static CableDataTable _cableTable;
        private static InstrumentationDataTable _instTable;
        private static ConcreteDataTable _concreteTable;

        #endregion

        public static string ReportsFolder { get; set; }
        public static Action<string> Log { get; set; }

        private sealed class FileIdentity
        {
            public string Stage { get; set; }
            public string Project { get; set; }
            public bool IsDelete { get; set; }
            public string SourceFileName { get; set; }
        }

        #region Public API

        public static LoadResult GetData()
        {
            _bW = null;
            _progress = 0;

            InitTables();
            FillData();

            return BuildResult();
        }

        public static LoadResult GetData(BackgroundWorker bW, int startProgress = 0)
        {
            _bW = bW;
            _progress = startProgress;

            InitTables();
            FillData();

            return BuildResult();
        }

        #endregion

        #region Core

        private static void InitTables()
        {
            _mainTable = new MainTableDataTable();
            _pipeCompTable = new PipeComponentsDataTable();
            _diamTable = new DiameterDataTable();
            _insuPaintTable = new InsuPaintDataTable();
            _steelTable = new SteelDataTable();
            _cableTable = new CableDataTable();
            _instTable = new InstrumentationDataTable();
            _concreteTable = new ConcreteDataTable();
        }

        private static LoadResult BuildResult()
        {
            return new LoadResult
            {
                MainTable = _mainTable,
                PipeComponents = _pipeCompTable,
                Diameter = _diamTable,
                InsuPaint = _insuPaintTable,
                Steel = _steelTable,
                Cable = _cableTable,
                Instrumentation = _instTable,
                Concrete = _concreteTable,
                RowCounts = new Dictionary<string, int>
                {
                    ["Main"] = _mainTable.Rows.Count,
                    ["PipeComponents"] = _pipeCompTable.Rows.Count,
                    ["Diameter"] = _diamTable.Rows.Count,
                    ["InsuPaint"] = _insuPaintTable.Rows.Count,
                    ["Steel"] = _steelTable.Rows.Count,
                    ["Cable"] = _cableTable.Rows.Count,
                    ["Instrumentation"] = _instTable.Rows.Count,
                    ["Concrete"] = _concreteTable.Rows.Count
                }
            };
        }

        private static void FillData()
        {
            string folderName = ResolveReportsFolder();
            var folderPath = new DirectoryInfo(folderName);

            if (!folderPath.Exists)
            {
                Report($"Папка не существует: {folderName}");
                return;
            }

            var files = folderPath
                .GetFiles()
                .Where(IsExcelFile)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                Report($"В папке нет Excel-файлов: {folderName}");
                return;
            }

            Report($"Найдено Excel-файлов: {files.Length} в \"{folderName}\"");

            foreach (var file in files)
            {
                ProcessFile(file);
            }

            Report("Загрузка из Excel завершена.");
        }

        private static void ProcessFile(FileInfo file)
        {
            FileIdentity identity;

            try
            {
                identity = ParseFileIdentity(file);
            }
            catch (Exception ex)
            {
                Report($"Ошибка разбора имени файла {file.Name}: {ex.Message}");
                return;
            }

            if (identity.IsDelete)
            {
                Report($"Файл {file.Name} помечен как DELETE и пропущен на этапе чтения Excel.");
                return;
            }

            try
            {
                Report($"Обработка файла: {file.Name} | Stage={identity.Stage} | Project={identity.Project}");

                using (var wb = new XLWorkbook(file.FullName))
                {
                    var wsMainSummary = WS(wb, "TDC Key Qty Sumry");
                    var wsEquipGrp = WS(wb, "Equip COA Grp Sumry");
                    var wsPipeGrp = WS(wb, "Pipe COA Grp Sumry");
                    var wsPipeSum = WS(wb, "Pipe COA Sumry");
                    var wsPipeItem = WS(wb, "Pipe Item Sumry");
                    var wsPaintItem = WS(wb, "Paint Item Sumry");
                    var wsInsuGrp = WS(wb, "Insul COA Grp Sumry");
                    var wsSteelGrp = WS(wb, "Steel COA Grp Sumry");
                    var wsSteelItem = WS(wb, "Steel Item Sumry");
                    var wsElecGrp = WS(wb, "Elec COA Grp Sumry");
                    var wsInstItem = WS(wb, "Inst Item Sumry");
                    var wsConcrete = WS(wb, "Civil Item Dets");

                    FillMainData(identity, wsMainSummary, wsEquipGrp, wsPipeGrp);
                    FillPipeCompData(identity, wsPipeSum, wsPipeGrp);
                    FillDiamData(identity, wsPipeItem);

                    ReportProgress(++_progress);

                    FillInsuPaintData(identity, wsPaintItem, wsInsuGrp);
                    FillSteelData(identity, wsSteelGrp, wsSteelItem);
                    FillCableData(identity, wsElecGrp);
                    FillInstData(identity, wsInstItem);
                    FillConcreteData(identity, wsConcrete);

                    ReportProgress(++_progress);
                }
            }
            catch (Exception ex)
            {
                Report($"Ошибка при обработке {file.Name}: {ex.Message}");
            }
        }

        #endregion

        #region Fillers

        private static void FillMainData(
            FileIdentity id,
            IXLWorksheet mainSummary,
            IXLWorksheet equiGrpSummary,
            IXLWorksheet pipeGrpSummary)
        {
            int equiQty = 0;
            double equiWeight = 0;
            double pipeLength = 0;
            double pipeWeight = 0;
            double concrete = 0;
            double steel = 0;
            int inst = 0;
            int pile = 0;
            double cable = 0;
            double insuPaint = 0;
            double manHours = 0;
            double area = 0;

            if (mainSummary != null)
            {
                for (int i = 7; i <= LastRow(mainSummary); i++)
                {
                    string text = mainSummary.Cell(i, 1).GetString();

                    if (Contains(text, "Equipment"))
                    {
                        equiQty = GetInt(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "AG Pipe"))
                    {
                        pipeLength = GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Concrete"))
                    {
                        concrete = GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Steel") && !Contains(text, "Steel FPR"))
                    {
                        steel = GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Instrumentation"))
                    {
                        inst = GetInt(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Piling"))
                    {
                        pile = GetInt(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Electrical"))
                    {
                        cable += GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Insulation") || Contains(text, "Paint"))
                    {
                        double factor = Contains(text, "Pipe") ? 0.7 : 1.0;
                        insuPaint += factor * GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(text, "Totals"))
                    {
                        manHours = GetDouble(mainSummary.Cell(i, 5));
                        area = GetDouble(mainSummary.Cell(i + 2, 2));
                        break;
                    }
                }
            }

            if (equiGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(equiGrpSummary); i++)
                {
                    if (Contains(equiGrpSummary.Cell(i, 1).GetString(), "Totals"))
                    {
                        equiWeight = GetDouble(equiGrpSummary.Cell(i, 11)) / 1000.0;
                        break;
                    }
                }
            }

            if (pipeGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeGrpSummary); i++)
                {
                    if (Contains(pipeGrpSummary.Cell(i, 1).GetString(), "Totals"))
                    {
                        pipeWeight = GetDouble(pipeGrpSummary.Cell(i, 12)) / 1000.0;
                        break;
                    }
                }
            }

            _mainTable.AddMainTableRow(
                id.Project,
                id.Stage,
                equiQty,
                (float)equiWeight,
                (float)pipeLength,
                (float)pipeWeight,
                (float)concrete,
                (float)steel,
                inst,
                pile,
                (float)cable,
                (float)insuPaint,
                (float)manHours,
                area);
        }

        private static void FillPipeCompData(
            FileIdentity id,
            IXLWorksheet pipeSummary,
            IXLWorksheet pipeGrpSummary)
        {
            double pipeWeight = 0;
            double valvFlanWeight = 0;
            double valvNonFlanWeight = 0;
            double suppWeight = 0;
            double carbLength = 0;
            double stainLength = 0;
            double misc = 0;

            if (pipeSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeSummary); i++)
                {
                    string t3 = pipeSummary.Cell(i, 3).GetString();
                    string t1 = pipeSummary.Cell(i, 1).GetString();

                    if (Contains(t3, "CS Field Mat'l") ||
                        Contains(t3, "CS Remote Shop Mat'l") ||
                        Contains(t3, "SS Field Mat'l") ||
                        Contains(t3, "SS Remote Shop Mat'l") ||
                        Contains(t3, "Process Ductwork"))
                    {
                        pipeWeight += GetDouble(pipeSummary.Cell(i, 8));
                        continue;
                    }

                    if (Contains(t3, "CS Valves: Flanged") || Contains(t3, "SS Valves: Flanged"))
                    {
                        valvFlanWeight += GetDouble(pipeSummary.Cell(i, 8));
                        continue;
                    }

                    if (Contains(t3, "CS Valves: Non-Flanged") || Contains(t3, "SS Valves: Non-Flanged"))
                    {
                        valvNonFlanWeight += GetDouble(pipeSummary.Cell(i, 8));
                        continue;
                    }

                    if (Contains(t3, "Pipe Hangers, Shoes Etc."))
                    {
                        suppWeight = GetDouble(pipeSummary.Cell(i, 8));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            if (pipeGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeGrpSummary); i++)
                {
                    string t2 = pipeGrpSummary.Cell(i, 2).GetString();
                    string t1 = pipeGrpSummary.Cell(i, 1).GetString();

                    if (Contains(t2, "Carbon Stl Pipe/Fittings"))
                    {
                        carbLength = GetDouble(pipeGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t2, "Stainless Pipe/Fittings"))
                    {
                        stainLength = GetDouble(pipeGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t2, "Misc.Metal Pipe / Fittings"))
                    {
                        misc = GetDouble(pipeGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _pipeCompTable.AddPipeComponentsRow(
                id.Project,
                id.Stage,
                pipeWeight,
                valvFlanWeight,
                suppWeight,
                carbLength,
                stainLength,
                misc,
                valvNonFlanWeight);
        }

        private static void FillDiamData(FileIdentity id, IXLWorksheet pipeItemSummary)
        {
            var diamLength = new Dictionary<int, double>();

            if (pipeItemSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeItemSummary); i++)
                {
                    if (Contains(pipeItemSummary.Cell(i, 1).GetString(), "Totals"))
                        break;

                    int diam = GetInt(pipeItemSummary.Cell(i, 3));
                    if (diam == 0)
                        continue;

                    double len = GetDouble(pipeItemSummary.Cell(i, 5));
                    if (len == 0)
                        continue;

                    if (diamLength.ContainsKey(diam))
                        diamLength[diam] += len;
                    else
                        diamLength.Add(diam, len);
                }
            }

            foreach (var pair in diamLength)
                _diamTable.AddDiameterRow(id.Project, id.Stage, pair.Key, pair.Value);
        }

        private static void FillInsuPaintData(
            FileIdentity id,
            IXLWorksheet paintItemSummary,
            IXLWorksheet insuGrpSummary)
        {
            double equiPaint = 0;
            double pipePaint = 0;
            double struPaint = 0;
            double equiInsu = 0;
            double pipeInsu = 0;

            if (paintItemSummary != null)
            {
                for (int i = 7; i <= LastRow(paintItemSummary); i++)
                {
                    string t2 = paintItemSummary.Cell(i, 2).GetString();
                    string t1 = paintItemSummary.Cell(i, 1).GetString();

                    if (Contains(t2, "Paint - Equipment"))
                    {
                        equiPaint += GetDouble(paintItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Paint - Piping"))
                    {
                        pipePaint += GetDouble(paintItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Paint - Structures"))
                    {
                        struPaint += GetDouble(paintItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            if (insuGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(insuGrpSummary); i++)
                {
                    string t1 = insuGrpSummary.Cell(i, 1).GetString();

                    if (Contains(t1, "Equip Insulation"))
                    {
                        equiInsu = GetDouble(insuGrpSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(t1, "Pipe Insulation"))
                    {
                        pipeInsu = 0.7 * GetDouble(insuGrpSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _insuPaintTable.AddInsuPaintRow(
                id.Project,
                id.Stage,
                equiPaint,
                pipePaint,
                equiInsu,
                pipeInsu,
                struPaint);
        }

        private static void FillSteelData(
            FileIdentity id,
            IXLWorksheet steelGrpSummary,
            IXLWorksheet steelItemSummary)
        {
            double equiSteel = 0;
            double struSteel = 0;
            double otherSteel = 0;
            double ladders = 0;
            double railings = 0;
            double gratings = 0;
            double plates = 0;
            double beams = 0;
            double columns = 0;
            double bracings = 0;

            if (steelGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(steelGrpSummary); i++)
                {
                    string t1 = steelGrpSummary.Cell(i, 1).GetString();

                    if (Contains(t1, "Equipment Steel"))
                    {
                        equiSteel = GetDouble(steelGrpSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(t1, "Structural Steel"))
                    {
                        struSteel = GetDouble(steelGrpSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(t1, "Other Steel Items"))
                    {
                        otherSteel = GetDouble(steelGrpSummary.Cell(i, 2));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            if (steelItemSummary != null)
            {
                for (int i = 7; i <= LastRow(steelItemSummary); i++)
                {
                    string t2 = steelItemSummary.Cell(i, 2).GetString();
                    string t3u = steelItemSummary.Cell(i, 3).GetString().ToUpperInvariant();
                    string t1 = steelItemSummary.Cell(i, 1).GetString();

                    if (Contains(t2, "Ladders") || Contains(t2, "Flooring & Stair Treads"))
                    {
                        ladders += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Handrail And Toe Plate"))
                    {
                        railings += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Platforms"))
                    {
                        gratings += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (t3u.Contains("BASE PLATES"))
                    {
                        plates += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (t3u.Contains("BEAMS"))
                    {
                        beams += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (t3u.Contains("COLUMN"))
                    {
                        columns += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (t3u.Contains("BRACING"))
                    {
                        bracings += GetDouble(steelItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _steelTable.AddSteelRow(
                id.Project,
                id.Stage,
                equiSteel,
                struSteel,
                otherSteel,
                ladders,
                railings,
                gratings,
                plates,
                beams,
                columns,
                bracings);
        }

        private static void FillCableData(FileIdentity id, IXLWorksheet elecGrpSummary)
        {
            double grounding = 0;
            double cable = 0;
            double area = 0;

            if (elecGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(elecGrpSummary); i++)
                {
                    string t2 = elecGrpSummary.Cell(i, 2).GetString();
                    string t1 = elecGrpSummary.Cell(i, 1).GetString();

                    if (Contains(t2, "Grounding"))
                    {
                        grounding = GetDouble(elecGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t2, "Wire, Cable"))
                    {
                        cable = GetDouble(elecGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t2, "Bldg/Area"))
                    {
                        area = GetDouble(elecGrpSummary.Cell(i, 3));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _cableTable.AddCableRow(id.Project, id.Stage, grounding, cable, area);
        }

        private static void FillInstData(FileIdentity id, IXLWorksheet instItemSummary)
        {
            int flow = 0;
            int level = 0;
            int press = 0;
            int temp = 0;
            int motion = 0;
            int analyzer = 0;
            int orifice = 0;
            int cValves = 0;
            int sValves = 0;
            int rValves = 0;

            if (instItemSummary != null)
            {
                for (int i = 7; i <= LastRow(instItemSummary); i++)
                {
                    string t2 = instItemSummary.Cell(i, 2).GetString();
                    string t1 = instItemSummary.Cell(i, 1).GetString();

                    if (Contains(t2, "Flow Instruments"))
                    {
                        flow += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Level Instruments"))
                    {
                        level += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Pressure Instruments"))
                    {
                        press += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Temperature Instruments"))
                    {
                        temp += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Motion Instruments"))
                    {
                        motion += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Analyzers"))
                    {
                        analyzer += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Orifice Plates"))
                    {
                        orifice += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Control Valves"))
                    {
                        cValves += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Safety Valves"))
                    {
                        sValves += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t2, "Regulating Valves"))
                    {
                        rValves += GetInt(instItemSummary.Cell(i, 4));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _instTable.AddInstrumentationRow(
                id.Project,
                id.Stage,
                flow,
                level,
                press,
                temp,
                motion,
                analyzer,
                orifice,
                cValves,
                sValves,
                rValves);
        }

        private static void FillConcreteData(FileIdentity id, IXLWorksheet concreteDetails)
        {
            double large = 0;
            double small = 0;
            double footing = 0;
            double pour = 0;
            double basin = 0;

            if (concreteDetails != null)
            {
                for (int i = 7; i <= LastRow(concreteDetails); i++)
                {
                    string t3 = concreteDetails.Cell(i, 3).GetString();
                    string t1 = concreteDetails.Cell(i, 1).GetString();

                    if (Contains(t3, "Large Block"))
                    {
                        large += GetDouble(concreteDetails.Cell(i, 6));
                        continue;
                    }

                    if (Contains(t3, "Small Block"))
                    {
                        small += GetDouble(concreteDetails.Cell(i, 6));
                        continue;
                    }

                    if (Contains(t3, "Octagon") || Contains(t3, "Footing") || Contains(t3, "Pier"))
                    {
                        footing += GetDouble(concreteDetails.Cell(i, 6));
                        continue;
                    }

                    if (Contains(t3, "Mass Pour"))
                    {
                        pour += GetDouble(concreteDetails.Cell(i, 6));
                        continue;
                    }

                    if (Contains(t3, "Basin"))
                    {
                        basin += GetDouble(concreteDetails.Cell(i, 6));
                        continue;
                    }

                    if (Contains(t1, "Totals"))
                        break;
                }
            }

            _concreteTable.AddConcreteRow(id.Project, id.Stage, large, small, footing, pour, basin);
        }

        #endregion

        #region File helpers

        private static string ResolveReportsFolder()
        {
            if (!string.IsNullOrWhiteSpace(ReportsFolder))
                return ReportsFolder;

            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyFolder, "ACCE Reports");
        }

        private static bool IsExcelFile(FileInfo file)
        {
            if (file == null)
                return false;

            string ext = file.Extension;
            return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);
        }

        private static FileIdentity ParseFileIdentity(FileInfo file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            string sourceName = file.Name;
            string baseName = Path.GetFileNameWithoutExtension(sourceName)?.Trim();

            if (string.IsNullOrWhiteSpace(baseName))
                throw new InvalidOperationException("Пустое имя файла.");

            bool isDelete = baseName.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0;

            string cleaned = baseName
                .Replace("DELETE", "")
                .Trim();

            while (cleaned.Contains(".."))
                cleaned = cleaned.Replace("..", ".");

            cleaned = cleaned.Trim('.').Trim();

            string[] parts = cleaned.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException(
                    $"Имя файла '{sourceName}' не соответствует формату '<STAGE>.<PROJECT>.xlsx'.");
            }

            return new FileIdentity
            {
                Stage = parts[0].Trim(),
                Project = parts[1].Trim(),
                IsDelete = isDelete,
                SourceFileName = sourceName
            };
        }

        #endregion

        #region ClosedXML helpers

        private static IXLWorksheet WS(IXLWorkbook wb, string name)
        {
            return wb.Worksheets.TryGetWorksheet(name, out var ws) ? ws : null;
        }

        private static int LastRow(IXLWorksheet ws)
        {
            return ws?.LastRowUsed()?.RowNumber() ?? 0;
        }

        private static int GetInt(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty())
                return 0;

            try
            {
                return (int)Math.Round(cell.GetValue<double>());
            }
            catch
            {
            }

            string s = cell.GetString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                return iv;

            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double dv))
                return (int)Math.Round(dv);

            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out dv))
                return (int)Math.Round(dv);

            return 0;
        }

        private static double GetDouble(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty())
                return 0;

            try
            {
                return cell.GetValue<double>();
            }
            catch
            {
            }

            string s = cell.GetString();
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double dv))
                return dv;

            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out dv))
                return dv;

            return 0;
        }

        private static bool Contains(string source, string value)
        {
            return !string.IsNullOrEmpty(source)
                && !string.IsNullOrEmpty(value)
                && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region Logging

        private static void Report(string msg, int? progress = null)
        {
            try
            {
                Log?.Invoke(msg);

                if (_bW != null && progress.HasValue)
                    _bW.ReportProgress(progress.Value);
            }
            catch
            {
            }
        }

        private static void ReportProgress(int progress)
        {
            if (_bW == null)
                return;

            try
            {
                _bW.ReportProgress(progress);
            }
            catch
            {
            }
        }

        #endregion
    }
}