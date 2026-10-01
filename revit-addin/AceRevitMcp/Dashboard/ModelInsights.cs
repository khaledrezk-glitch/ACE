using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace AceRevitMcp.Dashboard
{
    /// <summary>One audit check: what was found, and how much it costs the health score.</summary>
    internal sealed class Check
    {
        public string Key, Area, Name, Detail, Hint;
        public int Count;
        public double Penalty, MaxPenalty;
        public List<long> Ids = new List<long>();
        /// <summary>The pass rule shown in reports, e.g. "warn at 1, fail at 5" (from the check set).</summary>
        public string Rule;
        /// <summary>Set by the check set's warnAt / failAt; otherwise the status follows the score impact.</summary>
        public string StatusOverride;
        /// <summary>ok, warn or fail.</summary>
        public string Status => StatusOverride ?? (Penalty <= 0.001 ? "ok" : Penalty >= MaxPenalty * 0.6 ? "fail" : "warn");
    }

    internal sealed class ClashSummary
    {
        public int Open, Issues, New, Approved, Resolved;
        public DateTime? LastRun;
        public List<(string Responsible, int Issues)> ByResponsible = new List<(string, int)>();
    }

    internal sealed class Completeness { public string Category, Parameter; public int Filled, Total; public int Percent => Total == 0 ? 100 : (int)Math.Round(100.0 * Filled / Total); }
    internal sealed class LevelStats { public string Level; public double Elevation; public int Rooms, Doors, Windows, Walls; public double RoomAreaM2; }
    internal sealed class Activity
    {
        public int Previews, Changes, PanelApplied, PanelCancelled, Other;
        public List<(DateTime Day, int Count)> PerDay = new List<(DateTime, int)>();
        public List<string> Latest = new List<string>();
    }

    /// <summary>A read-only snapshot of the model's health and status, used by the dashboard and by Claude.</summary>
    internal sealed class Insights
    {
        public string Model, PathName, User, Revit, AddinVersion;
        public DateTime Time = DateTime.Now;
        public long FileBytes;
        public bool Workshared;
        public int Score;
        public string Grade;
        public long ElapsedMs;
        public readonly List<Check> Checks = new List<Check>();
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        public readonly List<(string Text, int Count)> WarningTypes = new List<(string, int)>();
        public readonly List<Completeness> Completeness = new List<Completeness>();
        public readonly List<LevelStats> Levels = new List<LevelStats>();
        public readonly List<(string Field, bool Filled, string Value)> ProjectInfo = new List<(string, bool, string)>();
        public readonly List<(string Field, int Missing)> SheetFields = new List<(string, int)>();
        public Activity Activity = new Activity();
        public string CheckSetName = "ACE Standard";
        public string CheckSetSource = "built-in";
        public ClashSummary Clashes;
        public List<(DateTime Time, int Score)> History = new List<(DateTime, int)>();
    }

    internal static partial class ModelInsights
    {
        private const double NarrowDoorMm = 900;

        public static Insights Collect(Document doc, string user)
        {
            var sw = Stopwatch.StartNew();
            var x = new Insights
            {
                Model = doc.Title, PathName = doc.PathName, User = user, Workshared = doc.IsWorkshared,
                Revit = doc.Application.VersionName,
                AddinVersion = typeof(ModelInsights).Assembly.GetName().Version?.ToString(3),
            };
            try { if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName)) x.FileBytes = new FileInfo(doc.PathName).Length; } catch { }

            var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && e.ViewSpecific == false).ToList();
            x.Counts["Model elements"] = instances.Count;

            int Count(BuiltInCategory c) => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
            x.Counts["Walls"] = Count(BuiltInCategory.OST_Walls);
            x.Counts["Doors"] = Count(BuiltInCategory.OST_Doors);
            x.Counts["Windows"] = Count(BuiltInCategory.OST_Windows);
            x.Counts["Rooms"] = Count(BuiltInCategory.OST_Rooms);
            x.Counts["Levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
            x.Counts["Families"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
            x.Counts["Model groups"] = Count(BuiltInCategory.OST_IOSModelGroups);
            x.Counts["Revit links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount();

            Warnings(doc, x);
            ImportsAndFamilies(doc, x);
            RoomsAndDoors(doc, x);
            ViewsAndSheets(doc, x);
            ParameterCompleteness(doc, x);
            ProjectInformation(doc, x);
            PerLevel(doc, x);
            StandardChecks(doc, x);
            ClashStatus(doc, x);
            CheckSet.Apply(x);

            var penalty = x.Checks.Sum(c => c.Penalty);
            x.Score = (int)Math.Round(Math.Max(0, Math.Min(100, 100 - penalty)));
            x.Grade = x.Score >= 85 ? "Good" : x.Score >= 65 ? "Fair" : "Needs attention";
            x.Activity = ReadActivity();
            x.History = InsightsHistory.Record(x);
            x.ElapsedMs = sw.ElapsedMilliseconds;
            return x;
        }

        private static Check Add(Insights x, string area, string name, int count, double perItem, double max, string detail, string hint, IEnumerable<ElementId> ids = null)
        {
            var c = new Check
            {
                Key = KeyOf(name), Area = area, Name = name, Count = count, MaxPenalty = max, Penalty = Math.Min(max, count * perItem),
                Detail = detail, Hint = hint,
            };
            if (ids != null) c.Ids = ids.Select(i => i.Value).Distinct().Take(2000).ToList();
            x.Checks.Add(c);
            return c;
        }

        internal static string KeyOf(string name) =>
            new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-').Replace("--", "-");

        private static void Warnings(Document doc, Insights x)
        {
            var warnings = doc.GetWarnings();
            x.Counts["Warnings"] = warnings.Count;
            foreach (var g in warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(12))
                x.WarningTypes.Add((g.Key, g.Count()));
            var ids = warnings.SelectMany(w => w.GetFailingElements());
            Add(x, "Warnings", "Revit warnings", warnings.Count, 0.05, 20,
                $"{warnings.Count} warnings of {warnings.GroupBy(w => w.GetDescriptionText()).Count()} types",
                "Resolve the most frequent types first (Manage > Warnings). A warning solver is planned (Phase 2).", ids);
        }

        private static void ImportsAndFamilies(Document doc, Insights x)
        {
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => !i.IsLinked).ToList();
            Add(x, "Model content", "Imported CAD (not linked)", imports.Count, 2.5, 10,
                $"{imports.Count} CAD files imported into the model", "Link CAD instead of importing it, or delete imports that are no longer needed.", imports.Select(i => i.Id));

            var inPlace = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.Symbol?.Family?.IsInPlace == true).ToList();
            var inPlaceFamilies = inPlace.Select(f => f.Symbol.Family.Id).Distinct().Count();
            Add(x, "Model content", "In-place families", inPlaceFamilies, 1, 8,
                $"{inPlaceFamilies} in-place families ({inPlace.Count} instances)", "Replace repeated in-place families with loadable families.", inPlace.Select(i => i.Id));

            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var unloaded = links.Where(l => { try { return !RevitLinkType.IsLoaded(doc, l.Id); } catch { return false; } }).ToList();
            Add(x, "Model content", "Revit links not loaded", unloaded.Count, 3, 6,
                $"{unloaded.Count} of {links.Count} links not loaded", "Reload or remove unloaded links (Manage > Manage Links).", unloaded.Select(l => l.Id));
        }

        private static void RoomsAndDoors(Document doc, Insights x)
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().ToList();
            var unplaced = rooms.Where(r => r.Location == null).ToList();
            var notEnclosed = rooms.Where(r => r.Location != null && r.Area <= 0).ToList();
            Add(x, "Rooms and doors", "Unplaced rooms", unplaced.Count, 0.5, 6,
                $"{unplaced.Count} rooms exist in schedules but are not placed", "Place or delete them.", unplaced.Select(r => r.Id));
            Add(x, "Rooms and doors", "Rooms not enclosed", notEnclosed.Count, 1, 8,
                $"{notEnclosed.Count} placed rooms with no enclosed area", "Close the room boundaries, or delete redundant rooms.", notEnclosed.Select(r => r.Id));

            var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var served = new HashSet<long>();
            foreach (var d in doors)
            {
                try { if (d.FromRoom != null) served.Add(d.FromRoom.Id.Value); } catch { }
                try { if (d.ToRoom != null) served.Add(d.ToRoom.Id.Value); } catch { }
            }
            var noDoor = rooms.Where(r => r.Area > 0 && !served.Contains(r.Id.Value)).ToList();
            Add(x, "Rooms and doors", "Rooms without a door", noDoor.Count, 0.25, 6,
                $"{noDoor.Count} enclosed rooms with no door opening into them", "Check that access is intended (shafts and risers may be fine).", noDoor.Select(r => r.Id));

            var narrow = new List<FamilyInstance>();
            var unreadable = 0;
            foreach (var d in doors)
            {
                var w = DoorWidthMm(d);
                if (w == null) { unreadable++; continue; }
                if (w < NarrowDoorMm) narrow.Add(d);
            }
            Add(x, "Rooms and doors", $"Doors narrower than {NarrowDoorMm:0} mm", narrow.Count, 0.25, 5,
                $"{narrow.Count} doors under {NarrowDoorMm:0} mm (nominal width; {unreadable} doors without a readable width)",
                "Check against the accessibility and escape requirements for each door.", narrow.Select(d => d.Id));

            var marks = doors.Cast<Element>().Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType())
                .Select(e => (e, mark: e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), cat: e.Category?.Name))
                .Where(t => !string.IsNullOrWhiteSpace(t.mark))
                .GroupBy(t => t.cat + "|" + t.mark).Where(g => g.Count() > 1).ToList();
            Add(x, "Rooms and doors", "Duplicate door and window marks", marks.Count, 0.5, 6,
                $"{marks.Count} marks used more than once ({marks.Sum(g => g.Count())} elements)", "Renumber so each mark is unique.", marks.SelectMany(g => g.Select(t => t.e.Id)));
        }

        private static double? DoorWidthMm(FamilyInstance d)
        {
            Parameter p = d.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? d.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                          ?? d.LookupParameter("Width") ?? d.Symbol?.LookupParameter("Width");
            if (p == null || p.StorageType != StorageType.Double) return null;
            var mm = Lengths.Mm(p.AsDouble());
            return mm > 1 ? mm : (double?)null;
        }

        private static readonly ViewType[] SheetableViewTypes =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.ThreeD,
            ViewType.Detail, ViewType.DraftingView, ViewType.AreaPlan, ViewType.EngineeringPlan,
        };

        private static void ViewsAndSheets(Document doc, Insights x)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            var placed = new HashSet<long>();
            foreach (var s in sheets) foreach (var id in s.GetAllPlacedViews()) placed.Add(id.Value);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && SheetableViewTypes.Contains(v.ViewType) && v.CanBePrinted).ToList();
            x.Counts["Views"] = views.Count;
            x.Counts["Sheets"] = sheets.Count;

            var notOnSheet = views.Where(v => !placed.Contains(v.Id.Value)).ToList();
            var ratio = views.Count == 0 ? 0 : (double)notOnSheet.Count / views.Count;
            var c = Add(x, "Views and sheets", "Views not on sheets", notOnSheet.Count, 0, 8,
                $"{notOnSheet.Count} of {views.Count} views ({ratio:P0}) are not placed on a sheet", "Delete working views that are no longer needed, or place them.", notOnSheet.Select(v => v.Id));
            c.Penalty = Math.Min(8, Math.Max(0, ratio - 0.3) * 12);

            var noTemplate = views.Where(v => v.ViewType != ViewType.DraftingView && v.ViewTemplateId == ElementId.InvalidElementId).ToList();
            var tRatio = views.Count == 0 ? 0 : (double)noTemplate.Count / views.Count;
            var t = Add(x, "Views and sheets", "Views without a view template", noTemplate.Count, 0, 4,
                $"{noTemplate.Count} views ({tRatio:P0}) have no view template", "Apply the office view templates for consistent graphics.", noTemplate.Select(v => v.Id));
            t.Penalty = Math.Min(4, tRatio * 5);

            var empty = sheets.Where(s => s.GetAllPlacedViews().Count == 0 && new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).GetElementCount() == 0).ToList();
            Add(x, "Views and sheets", "Empty sheets", empty.Count, 0.5, 4, $"{empty.Count} sheets with no views or schedules", "Fill or delete them before issue.", empty.Select(s => s.Id));

            var fields = new (string, BuiltInParameter)[]
            {
                ("Sheet issue date", BuiltInParameter.SHEET_ISSUE_DATE), ("Drawn by", BuiltInParameter.SHEET_DRAWN_BY),
                ("Checked by", BuiltInParameter.SHEET_CHECKED_BY), ("Approved by", BuiltInParameter.SHEET_APPROVED_BY),
                ("Designed by", BuiltInParameter.SHEET_DESIGNED_BY),
            };
            var incomplete = new HashSet<long>();
            foreach (var (label, bip) in fields)
            {
                var missing = sheets.Where(s => string.IsNullOrWhiteSpace(s.get_Parameter(bip)?.AsString())).ToList();
                x.SheetFields.Add((label, missing.Count));
                foreach (var s in missing) incomplete.Add(s.Id.Value);
            }
            var sRatio = sheets.Count == 0 ? 0 : (double)incomplete.Count / sheets.Count;
            var sc = Add(x, "Submission readiness", "Sheets with missing title block data", incomplete.Count, 0, 5,
                $"{incomplete.Count} of {sheets.Count} sheets miss issue date, drawn, checked, approved or designed by",
                "Fill the title block fields before issue.", incomplete.Select(i => new ElementId(i)));
            sc.Penalty = Math.Min(5, sRatio * 5);
        }

        private static void ParameterCompleteness(Document doc, Insights x)
        {
            var specs = new (string Category, BuiltInCategory Bic, string Label, BuiltInParameter Bip)[]
            {
                ("Doors", BuiltInCategory.OST_Doors, "Mark", BuiltInParameter.ALL_MODEL_MARK),
                ("Windows", BuiltInCategory.OST_Windows, "Mark", BuiltInParameter.ALL_MODEL_MARK),
                ("Rooms", BuiltInCategory.OST_Rooms, "Number", BuiltInParameter.ROOM_NUMBER),
                ("Rooms", BuiltInCategory.OST_Rooms, "Name", BuiltInParameter.ROOM_NAME),
                ("Rooms", BuiltInCategory.OST_Rooms, "Department", BuiltInParameter.ROOM_DEPARTMENT),
                ("Rooms", BuiltInCategory.OST_Rooms, "Floor finish", BuiltInParameter.ROOM_FINISH_FLOOR),
            };
            var missingIds = new List<ElementId>();
            foreach (var s in specs)
            {
                var els = new FilteredElementCollector(doc).OfCategory(s.Bic).WhereElementIsNotElementType().ToList();
                if (s.Bic == BuiltInCategory.OST_Rooms) els = els.Where(e => e is Room r && r.Location != null).ToList();
                var missing = els.Where(e => string.IsNullOrWhiteSpace(e.get_Parameter(s.Bip)?.AsString())).ToList();
                x.Completeness.Add(new Completeness { Category = s.Category, Parameter = s.Label, Total = els.Count, Filled = els.Count - missing.Count });
                missingIds.AddRange(missing.Select(e => e.Id));
            }
            var withData = x.Completeness.Where(c => c.Total > 0).ToList();
            var avg = withData.Count == 0 ? 100 : withData.Average(c => c.Percent);
            var check = Add(x, "Parameters", "Key parameters filled", missingIds.Count, 0, 8,
                $"{avg:0}% of key door, window and room parameters are filled", "Fill the missing values (Claude can do this with a preview).", missingIds);
            check.Penalty = Math.Min(8, (100 - avg) * 0.08);
        }

        private static void ProjectInformation(Document doc, Insights x)
        {
            var info = doc.ProjectInformation;
            if (info == null) return;
            var fields = new (string, BuiltInParameter)[]
            {
                ("Project name", BuiltInParameter.PROJECT_NAME), ("Project number", BuiltInParameter.PROJECT_NUMBER),
                ("Client name", BuiltInParameter.CLIENT_NAME), ("Project address", BuiltInParameter.PROJECT_ADDRESS),
                ("Project status", BuiltInParameter.PROJECT_STATUS), ("Project issue date", BuiltInParameter.PROJECT_ISSUE_DATE),
            };
            foreach (var (label, bip) in fields)
            {
                var v = info.get_Parameter(bip)?.AsString();
                var filled = !string.IsNullOrWhiteSpace(v) && !v.Trim().Equals("Enter address here", StringComparison.OrdinalIgnoreCase);
                x.ProjectInfo.Add((label, filled, v?.Trim()));
            }
            var missing = x.ProjectInfo.Count(p => !p.Filled);
            Add(x, "Submission readiness", "Project information missing", missing, 0.6, 3,
                missing == 0 ? "All project information fields are filled" : $"{missing} of {fields.Length} project information fields are empty",
                "Fill Manage > Project Information.", new[] { info.Id });
        }

        private static void PerLevel(Document doc, Insights x)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var byLevel = levels.ToDictionary(l => l.Id.Value, l => new LevelStats { Level = l.Name, Elevation = Lengths.Mm(l.Elevation) });
            void Tally(BuiltInCategory c, Action<LevelStats, Element> add)
            {
                foreach (var e in new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType())
                    if (e.LevelId != null && byLevel.TryGetValue(e.LevelId.Value, out var s)) add(s, e);
            }
            Tally(BuiltInCategory.OST_Rooms, (s, e) => { if (e is Room r && r.Area > 0) { s.Rooms++; s.RoomAreaM2 += r.Area * 0.09290304; } });
            Tally(BuiltInCategory.OST_Doors, (s, e) => s.Doors++);
            Tally(BuiltInCategory.OST_Windows, (s, e) => s.Windows++);
            Tally(BuiltInCategory.OST_Walls, (s, e) => s.Walls++);
            x.Levels.AddRange(byLevel.Values.Where(s => s.Rooms + s.Doors + s.Windows + s.Walls > 0).OrderBy(s => s.Elevation));
        }

        /// <summary>Claude's activity in the last 7 days, from the shared journal (add-in and MCP server).</summary>
        private static Activity ReadActivity()
        {
            var a = new Activity();
            try
            {
                var dir = Path.Combine(AceConfig.Directory, "journal");
                var today = DateTime.Today;
                for (var i = 6; i >= 0; i--) a.PerDay.Add((today.AddDays(-i), 0));
                if (!Directory.Exists(dir)) return a;
                var entries = new List<(DateTime, string)>();
                foreach (var file in Directory.GetFiles(dir, "*.md"))
                {
                    if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(file), out var day) || day < today.AddDays(-7)) continue;
                    foreach (var line in File.ReadLines(file).Where(l => l.StartsWith("## ")))
                    {
                        var dash = line.IndexOf(" - ", StringComparison.Ordinal);
                        if (dash < 0) continue;
                        var title = line.Substring(dash + 3).Trim();
                        var when = DateTime.TryParse(line.Substring(3, dash - 3), out var t) ? day.Date + t.TimeOfDay : day;
                        entries.Add((when, title));
                    }
                }
                foreach (var (when, title) in entries.OrderBy(e => e.Item1))
                {
                    if (title.StartsWith("Preview", StringComparison.OrdinalIgnoreCase)) a.Previews++;
                    else if (title.StartsWith("Change (applied in ACE panel)", StringComparison.OrdinalIgnoreCase)) { a.PanelApplied++; a.Changes++; }
                    else if (title.StartsWith("Change", StringComparison.OrdinalIgnoreCase)) a.Changes++;
                    else if (title.StartsWith("Cancelled in ACE panel", StringComparison.OrdinalIgnoreCase)) a.PanelCancelled++;
                    else a.Other++;
                    var idx = a.PerDay.FindIndex(p => p.Day == when.Date);
                    if (idx >= 0) a.PerDay[idx] = (a.PerDay[idx].Day, a.PerDay[idx].Count + 1);
                }
                a.Latest = entries.OrderByDescending(e => e.Item1).Take(6).Select(e => $"{e.Item1:ddd HH:mm}  {e.Item2}").ToList();
            }
            catch (Exception ex) { Log.Warn($"Dashboard activity: {ex.Message}"); }
            return a;
        }

        public static JsonObject ToJson(Insights x, string htmlPath)
        {
            var checks = new JsonArray();
            foreach (var c in x.Checks)
                checks.Add(new JsonObject
                {
                    ["key"] = c.Key, ["area"] = c.Area, ["check"] = c.Name, ["status"] = c.Status, ["count"] = c.Count, ["rule"] = c.Rule,
                    ["scoreImpact"] = -Math.Round(c.Penalty, 1), ["maxImpact"] = -c.MaxPenalty, ["detail"] = c.Detail, ["hint"] = c.Hint,
                    ["sampleIds"] = new JsonArray(c.Ids.Take(25).Select(i => (JsonNode)i).ToArray()),
                });
            var counts = new JsonObject();
            foreach (var kv in x.Counts) counts[kv.Key] = kv.Value;
            return new JsonObject
            {
                ["model"] = x.Model, ["time"] = x.Time.ToString("s"), ["score"] = x.Score, ["grade"] = x.Grade,
                ["checkSet"] = $"{x.CheckSetName} ({x.CheckSetSource})",
                ["results"] = new JsonObject { ["pass"] = x.Checks.Count(c => c.Status == "ok"), ["warning"] = x.Checks.Count(c => c.Status == "warn"), ["fail"] = x.Checks.Count(c => c.Status == "fail") },
                ["clashes"] = x.Clashes == null ? null : new JsonObject { ["openIssues"] = x.Clashes.Issues, ["openClashes"] = x.Clashes.Open, ["new"] = x.Clashes.New, ["lastRun"] = x.Clashes.LastRun?.ToString("s") },
                ["scoreHistory"] = new JsonArray(x.History.Select(h => (JsonNode)new JsonObject { ["time"] = h.Time.ToString("s"), ["score"] = h.Score }).ToArray()),
                ["counts"] = counts,
                ["checks"] = checks,
                ["topWarnings"] = new JsonArray(x.WarningTypes.Select(w => (JsonNode)new JsonObject { ["warning"] = w.Text, ["count"] = w.Count }).ToArray()),
                ["parameterCompleteness"] = new JsonArray(x.Completeness.Select(c => (JsonNode)new JsonObject { ["category"] = c.Category, ["parameter"] = c.Parameter, ["percent"] = c.Percent, ["total"] = c.Total }).ToArray()),
                ["claudeActivity7Days"] = new JsonObject { ["previews"] = x.Activity.Previews, ["changes"] = x.Activity.Changes, ["appliedInPanel"] = x.Activity.PanelApplied, ["cancelledInPanel"] = x.Activity.PanelCancelled },
                ["notYetAvailable"] = new JsonArray("Code compliance (Phase 6)", "Full submission check (Phase 5)"),
                ["htmlReport"] = htmlPath,
                ["elapsedMs"] = x.ElapsedMs,
            };
        }
    }

    /// <summary>Stores each dashboard score per model, so trends are visible (%APPDATA%\ACE-RevitMCP\insights\history.jsonl).</summary>
    internal static class InsightsHistory
    {
        public static List<(DateTime, int)> Record(Insights x)
        {
            var result = new List<(DateTime, int)>();
            try
            {
                var dir = Path.Combine(AceConfig.Directory, "insights");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "history.jsonl");
                var key = string.IsNullOrEmpty(x.PathName) ? x.Model : x.PathName;
                if (File.Exists(file))
                    foreach (var line in File.ReadLines(file))
                    {
                        try
                        {
                            var o = JsonNode.Parse(line) as JsonObject;
                            if (o?["model"]?.ToString() == key && DateTime.TryParse(o["time"]?.ToString(), out var t))
                                result.Add((t, o["score"]!.GetValue<int>()));
                        }
                        catch { }
                    }
                var entry = new JsonObject { ["model"] = key, ["time"] = x.Time.ToString("s"), ["score"] = x.Score, ["warnings"] = x.Counts.TryGetValue("Warnings", out var w) ? w : 0 };
                File.AppendAllText(file, entry.ToJsonString() + "\n");
                result.Add((x.Time, x.Score));
            }
            catch (Exception ex) { Log.Warn($"Dashboard history: {ex.Message}"); result.Add((x.Time, x.Score)); }
            return result.OrderBy(r => r.Item1).TakeLast(20).ToList();
        }
    }
}
