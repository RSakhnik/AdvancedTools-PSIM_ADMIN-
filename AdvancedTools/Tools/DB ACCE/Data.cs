using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using static AdvancedTools.Tools.DB_ACCE.dbACCEDataSet;

namespace DataACCE
{
    /// <summary>
    /// Результат загрузки: готовые таблицы и краткая статистика.
    /// Сохраняйте в БД на стороне вызывающего кода (EF Core, Dapper, TableAdapter и т.д.).
    /// </summary>
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

        /// <summary>произвольная сводка по кол-ву строк в таблицах</summary>
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

        // Путь к папке и логгер
        public static string ReportsFolder { get; set; } = null;
        public static Action<string> Log { get; set; } = null;

        private static void Report(string msg, int? progress = null)
        {
            try
            {
                Log?.Invoke(msg);
                if (_bW != null && progress.HasValue)
                    _bW.ReportProgress(progress.Value);
            }
            catch { /* игнор */ }
        }

        /// <summary>
        /// Синхронная загрузка без BackgroundWorker.
        /// Вернёт готовые DataTable в виде LoadResult.
        /// </summary>
        public static LoadResult GetData()
        {
            _bW = null;
            _progress = 0;

            InitTables();
            FillData();

            return BuildResult();
        }

        /// <summary>
        /// Загрузка с поддержкой прогресса (BackgroundWorker).
        /// Вернёт готовые DataTable в виде LoadResult.
        /// </summary>
        public static LoadResult GetData(BackgroundWorker bW, int startProgress = 0)
        {
            _bW = bW;
            _progress = startProgress;

            InitTables();
            FillData();

            return BuildResult();
        }

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
            // Используем ReportsFolder или "ACCE Reports" рядом с exe
            string folderName;
            if (!string.IsNullOrWhiteSpace(ReportsFolder))
            {
                folderName = ReportsFolder;
            }
            else
            {
                string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                folderName = Path.Combine(assemblyFolder, "ACCE Reports");
            }

            var folderPath = new DirectoryInfo(folderName);

            if (!folderPath.Exists || folderPath.GetFiles().Length == 0)
            {
                Report($"Папка пустая или не существует: {folderName}");
                return;
            }

            var files = folderPath.GetFiles().OrderBy(f => f.Name).ToArray();
            Report($"Найдено файлов: {files.Length} в \"{folderName}\"");

            foreach (var file in files)
            {
                try
                {
                    Report($"Обработка файла: {file.Name}");

                    var wb = new XLWorkbook(file.FullName);

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

                    FillMainData(wsMainSummary, wsEquipGrp, wsPipeGrp, file);
                    FillPipeCompData(wsPipeSum, wsPipeGrp, file);
                    FillDiamData(wsPipeItem, file);
                    _bW?.ReportProgress(++_progress);

                    FillInsuPaintData(wsPaintItem, wsInsuGrp, file);
                    FillSteelData(wsSteelGrp, wsSteelItem, file);
                    FillCableData(wsElecGrp, file);
                    FillInstData(wsInstItem, file);
                    FillConcreteData(wsConcrete, file);

                    _bW?.ReportProgress(++_progress);
                }
                catch (Exception ex)
                {
                    Report($"Ошибка при обработке {file.Name}: {ex.Message}");
                }
            }

            // НИКАКОЙ записи в БД тут нет. Возвращаем таблицы вверх по стеку.
            Report("Загрузка из Excel завершена.");
        }

        #region Fillers (как прежде, только на ClosedXML)

        private static void FillMainData(IXLWorksheet mainSummary, IXLWorksheet equiGrpSummary, IXLWorksheet pipeGrpSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            int equiQty = 0;
            double equiWeight = 0, pipeLength = 0, pipeWeight = 0, concrete = 0, steel = 0;
            int inst = 0, pile = 0;
            double cable = 0, insuPaint = 0, manHours = 0, area = 0;

            if (mainSummary != null)
            {
                for (int i = 7; i <= LastRow(mainSummary); i++)
                {
                    var text = mainSummary.Cell(i, 1).GetString();

                    if (text.Contains("Equipment")) { equiQty = GetInt(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("AG Pipe")) { pipeLength = GetDouble(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Concrete")) { concrete = GetDouble(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Steel") && !text.Contains("Steel FPR"))
                    { steel = GetDouble(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Instrumentation")) { inst = GetInt(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Piling")) { pile = GetInt(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Electrical")) { cable += GetDouble(mainSummary.Cell(i, 2)); continue; }
                    if (text.Contains("Insulation") || text.Contains("Paint"))
                    {
                        double k = text.Contains("Pipe") ? 0.7 : 1.0;
                        insuPaint += k * GetDouble(mainSummary.Cell(i, 2));
                        continue;
                    }
                    if (text.Contains("Totals"))
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
                    if (equiGrpSummary.Cell(i, 1).GetString().Contains("Totals"))
                    { equiWeight = GetDouble(equiGrpSummary.Cell(i, 11)) / 1000; break; }
            }

            if (pipeGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeGrpSummary); i++)
                    if (pipeGrpSummary.Cell(i, 1).GetString().Contains("Totals"))
                    { pipeWeight = GetDouble(pipeGrpSummary.Cell(i, 12)) / 1000; break; }
            }

            _mainTable.AddMainTableRow(proj, phase, equiQty, (float)equiWeight, (float)pipeLength, (float)pipeWeight,
                                       (float)concrete, (float)steel, inst, pile, (float)cable, (float)insuPaint, (float)manHours, area);
        }

        private static void FillPipeCompData(IXLWorksheet pipeSummary, IXLWorksheet pipeGrpSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            double pipeWeight = 0, valvFlanWeight = 0, valvNonFlanWeight = 0, suppWeight = 0;
            double carbLength = 0, stainLength = 0, Misc = 0;

            if (pipeSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeSummary); i++)
                {
                    var t3 = pipeSummary.Cell(i, 3).GetString();
                    var t1 = pipeSummary.Cell(i, 1).GetString();

                    if (t3.Contains("CS Field Mat'l") ||
                        t3.Contains("CS Remote Shop Mat'l") ||
                        t3.Contains("SS Field Mat'l") ||
                        t3.Contains("SS Remote Shop Mat'l") ||
                        t3.Contains("Process Ductwork"))
                    { pipeWeight += GetDouble(pipeSummary.Cell(i, 8)); continue; }

                    if (t3.Contains("CS Valves: Flanged") || t3.Contains("SS Valves: Flanged"))
                    { valvFlanWeight += GetDouble(pipeSummary.Cell(i, 8)); continue; }

                    if (t3.Contains("CS Valves: Non-Flanged") || t3.Contains("SS Valves: Non-Flanged"))
                    { valvNonFlanWeight += GetDouble(pipeSummary.Cell(i, 8)); continue; }

                    if (t3.Contains("Pipe Hangers, Shoes Etc."))
                    { suppWeight = GetDouble(pipeSummary.Cell(i, 8)); continue; }

                    if (t1.Contains("Totals")) break;
                }
            }

            if (pipeGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeGrpSummary); i++)
                {
                    var t2 = pipeGrpSummary.Cell(i, 2).GetString();
                    var t1 = pipeGrpSummary.Cell(i, 1).GetString();

                    if (t2.Contains("Carbon Stl Pipe/Fittings")) { carbLength = GetDouble(pipeGrpSummary.Cell(i, 3)); continue; }
                    if (t2.Contains("Stainless Pipe/Fittings")) { stainLength = GetDouble(pipeGrpSummary.Cell(i, 3)); continue; }
                    if (t2.Contains("Misc.Metal Pipe / Fittings")) { Misc = GetDouble(pipeGrpSummary.Cell(i, 3)); continue; }
                    if (t1.Contains("Totals")) break;
                }
            }

            _pipeCompTable.AddPipeComponentsRow(proj, phase, pipeWeight, valvFlanWeight, suppWeight, carbLength, stainLength, Misc, valvNonFlanWeight);
        }

        private static void FillDiamData(IXLWorksheet pipeItemSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];
            var diamLength = new Dictionary<int, double>();

            if (pipeItemSummary != null)
            {
                for (int i = 7; i <= LastRow(pipeItemSummary); i++)
                {
                    var diam = GetInt(pipeItemSummary.Cell(i, 3));
                    if (diam == 0) continue;

                    var len = GetDouble(pipeItemSummary.Cell(i, 5));
                    if (len == 0) continue;

                    if (diamLength.ContainsKey(diam)) diamLength[diam] += len;
                    else diamLength.Add(diam, len);

                    if (pipeItemSummary.Cell(i, 1).GetString().Contains("Totals")) break;
                }
            }

            foreach (var pair in diamLength)
                _diamTable.AddDiameterRow(proj, phase, pair.Key, pair.Value);
        }

        private static void FillInsuPaintData(IXLWorksheet paintItemSummary, IXLWorksheet insuGrpSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            double equiPaint = 0, pipePaint = 0, struPaint = 0, equiInsu = 0, pipeInsu = 0;

            if (paintItemSummary != null)
            {
                for (int i = 7; i <= LastRow(paintItemSummary); i++)
                {
                    var t2 = paintItemSummary.Cell(i, 2).GetString();
                    var t1 = paintItemSummary.Cell(i, 1).GetString();

                    if (t2.Contains("Paint - Equipment")) { equiPaint += GetDouble(paintItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Paint - Piping")) { pipePaint += GetDouble(paintItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Paint - Structures")) { struPaint += GetDouble(paintItemSummary.Cell(i, 4)); continue; }
                    if (t1.Contains("Totals")) break;
                }
            }

            if (insuGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(insuGrpSummary); i++)
                {
                    var t1 = insuGrpSummary.Cell(i, 1).GetString();

                    if (t1.Contains("Equip Insulation")) { equiInsu = GetDouble(insuGrpSummary.Cell(i, 2)); continue; }
                    if (t1.Contains("Pipe Insulation")) { pipeInsu = 0.7 * GetDouble(insuGrpSummary.Cell(i, 2)); continue; }
                    if (t1.Contains("Totals")) break;
                }
            }

            _insuPaintTable.AddInsuPaintRow(proj, phase, equiPaint, pipePaint, equiInsu, pipeInsu, struPaint);
        }

        private static void FillSteelData(IXLWorksheet steelGrpSummary, IXLWorksheet steelItemSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            double equiSteel = 0, struSteel = 0, otherSteel = 0;
            double ladders = 0, railings = 0, gratings = 0, plates = 0, beams = 0, columns = 0, bracings = 0;

            if (steelGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(steelGrpSummary); i++)
                {
                    var t1 = steelGrpSummary.Cell(i, 1).GetString();

                    if (t1.Contains("Equipment Steel")) { equiSteel = GetDouble(steelGrpSummary.Cell(i, 2)); continue; }
                    if (t1.Contains("Structural Steel")) { struSteel = GetDouble(steelGrpSummary.Cell(i, 2)); continue; }
                    if (t1.Contains("Other Steel Items")) { otherSteel = GetDouble(steelGrpSummary.Cell(i, 2)); continue; }
                    if (t1.Contains("Totals")) break;
                }
            }

            if (steelItemSummary != null)
            {
                for (int i = 7; i <= LastRow(steelItemSummary); i++)
                {
                    var t2 = steelItemSummary.Cell(i, 2).GetString();
                    var t3u = steelItemSummary.Cell(i, 3).GetString().ToUpperInvariant();
                    var t1 = steelItemSummary.Cell(i, 1).GetString();

                    if (t2.Contains("Ladders") || t2.Contains("Flooring & Stair Treads"))
                    { ladders += GetDouble(steelItemSummary.Cell(i, 4)); continue; }

                    if (t2.Contains("Handrail And Toe Plate"))
                    { railings += GetDouble(steelItemSummary.Cell(i, 4)); continue; }

                    if (t2.Contains("Platforms"))
                    { gratings += GetDouble(steelItemSummary.Cell(i, 4)); continue; }

                    if (t3u.Contains("BASE PLATES")) { plates += GetDouble(steelItemSummary.Cell(i, 4)); continue; }
                    if (t3u.Contains("BEAMS")) { beams += GetDouble(steelItemSummary.Cell(i, 4)); continue; }
                    if (t3u.Contains("COLUMN")) { columns += GetDouble(steelItemSummary.Cell(i, 4)); continue; }
                    if (t3u.Contains("BRACING")) { bracings += GetDouble(steelItemSummary.Cell(i, 4)); continue; }

                    if (t1.Contains("Totals")) break;
                }
            }

            _steelTable.AddSteelRow(proj, phase, equiSteel, struSteel, otherSteel, ladders, railings, gratings, plates, beams, columns, bracings);
        }

        private static void FillCableData(IXLWorksheet elecGrpSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            double grounding = 0, cable = 0, area = 0;

            if (elecGrpSummary != null)
            {
                for (int i = 7; i <= LastRow(elecGrpSummary); i++)
                {
                    var t2 = elecGrpSummary.Cell(i, 2).GetString();
                    var t1 = elecGrpSummary.Cell(i, 1).GetString();

                    if (t2.Contains("Grounding")) { grounding = GetDouble(elecGrpSummary.Cell(i, 3)); continue; }
                    if (t2.Contains("Wire, Cable")) { cable = GetDouble(elecGrpSummary.Cell(i, 3)); continue; }
                    if (t2.Contains("Bldg/Area")) { area = GetDouble(elecGrpSummary.Cell(i, 3)); continue; }
                    if (t1.Contains("Totals")) break;
                }
            }

            _cableTable.AddCableRow(proj, phase, grounding, cable, area);
        }

        private static void FillInstData(IXLWorksheet instItemSummary, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            int flow = 0, level = 0, press = 0, temp = 0, motion = 0, analyzer = 0, orifice = 0, cValves = 0, sValves = 0, rValves = 0;

            if (instItemSummary != null)
            {
                for (int i = 7; i <= LastRow(instItemSummary); i++)
                {
                    var t2 = instItemSummary.Cell(i, 2).GetString();
                    var t1 = instItemSummary.Cell(i, 1).GetString();

                    if (t2.Contains("Flow Instruments")) { flow += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Level Instruments")) { level += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Pressure Instruments")) { press += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Temperature Instruments")) { temp += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Motion Instruments")) { motion += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Analyzers")) { analyzer += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Orifice Plates")) { orifice += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Control Valves")) { cValves += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Safety Valves")) { sValves += GetInt(instItemSummary.Cell(i, 4)); continue; }
                    if (t2.Contains("Regulating Valves")) { rValves += GetInt(instItemSummary.Cell(i, 4)); continue; }

                    if (t1.Contains("Totals")) break;
                }
            }

            _instTable.AddInstrumentationRow(proj, phase, flow, level, press, temp, motion, analyzer, orifice, cValves, sValves, rValves);
        }

        private static void FillConcreteData(IXLWorksheet concreteDetails, FileInfo file)
        {
            string proj = file.Name.Split('.')[1];
            string phase = file.Name.Split('.')[0];

            double large = 0, small = 0, footing = 0, pour = 0, basin = 0;

            if (concreteDetails != null)
            {
                for (int i = 7; i <= LastRow(concreteDetails); i++)
                {
                    var t3 = concreteDetails.Cell(i, 3).GetString();
                    var t1 = concreteDetails.Cell(i, 1).GetString();

                    if (t3.Contains("Large Block")) { large += GetDouble(concreteDetails.Cell(i, 6)); continue; }
                    if (t3.Contains("Small Block")) { small += GetDouble(concreteDetails.Cell(i, 6)); continue; }
                    if (t3.Contains("Octagon") || t3.Contains("Footing") || t3.Contains("Pier"))
                    { footing += GetDouble(concreteDetails.Cell(i, 6)); continue; }
                    if (t3.Contains("Mass Pour")) { pour += GetDouble(concreteDetails.Cell(i, 6)); continue; }
                    if (t3.Contains("Basin")) { basin += GetDouble(concreteDetails.Cell(i, 6)); continue; }

                    if (t1.Contains("Totals")) break;
                }
            }

            _concreteTable.AddConcreteRow(proj, phase, large, small, footing, pour, basin);
        }

        #endregion

        #region ClosedXML helpers

        private static IXLWorksheet WS(IXLWorkbook wb, string name)
            => wb.Worksheets.TryGetWorksheet(name, out var ws) ? ws : null;

        private static int LastRow(IXLWorksheet ws)
            => ws?.LastRowUsed()?.RowNumber() ?? 0;

        private static int GetInt(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty()) return 0;

            try { return (int)Math.Round(cell.GetValue<double>()); } catch { }

            var s = cell.GetString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)) return iv;
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var dv))
                return (int)Math.Round(dv);
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out dv))
                return (int)Math.Round(dv);
            return 0;
        }

        private static double GetDouble(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty()) return 0;

            try { return cell.GetValue<double>(); } catch { }

            var s = cell.GetString();
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var dv))
                return dv;
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out dv))
                return dv;
            return 0;
        }

        #endregion
    }
}
