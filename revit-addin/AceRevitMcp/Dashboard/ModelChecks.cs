using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Group = Autodesk.Revit.DB.Group;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Model Checker style checks (after Autodesk's BIM Interoperability Tools Model Checker): file and project set-up,
    /// worksets, levels and grids, model content, views, annotation and naming, each with a pass rule that an office
    /// check set can change (checkset.json).
    /// </summary>
    internal static partial class ModelInsights
    {
        private static readonly Regex CopyName = new Regex(@"(^|\s)Copy(\s+of\b|\s*\d+\s*$|\s*$)", RegexOptions.IgnoreCase);

        private static void StandardChecks(Document doc, Insights x, List<(FailureMessage Message, string Text)> warnings)
        {
            void Safe(string what, Action a) { try { a(); } catch (Exception ex) { Log.Warn($"Model check '{what}': {ex.Message}"); } }

            // ---- File and project --------------------------------------------------------------------------------
            Safe("file size", () =>
            {
                var mb = (int)Math.Round(x.FileBytes / 1048576.0);
                var c = Add(x, "File and project", "File size", mb, 0, 5,
                    x.FileBytes == 0 ? "The model has not been saved yet" : $"{mb} MB on disk",
                    "Purge unused content, remove CAD imports and unused views; split very large models by zone or discipline.");
                c.Penalty = mb > 300 ? 5 : mb > 150 ? 2 : 0;
                c.Rule = "review above 150 MB, action above 300 MB";
            });
            Safe("starting view", () =>
            {
                var sv = StartingViewSettings.GetStartingViewSettings(doc);
                var set = sv != null && sv.ViewId != ElementId.InvalidElementId;
                Add(x, "File and project", "Starting view not set", set ? 0 : 1, 1, 1,
                    set ? "A starting view is set" : "No starting view: the model opens on the last view saved, which slows opening",
                    "Set a light starting view, e.g. a cover or information sheet (Manage > Starting View).");
            });

            // ---- Worksets (workshared models) ------------------------------------------------------------------------
            if (doc.IsWorkshared)
                Safe("worksets", () =>
                {
                    var user = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
                    var ws1 = user.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase));
                    if (user.Count > 1 && ws1 != null)
                    {
                        var onWs1 = new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(ws1.Id)).WhereElementIsNotElementType()
                            .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !e.ViewSpecific).ToList();
                        Add(x, "Worksets", "Elements on Workset1", onWs1.Count, 0.01, 3,
                            $"{onWs1.Count} model elements are still on the default Workset1 ({user.Count} user worksets exist)",
                            "Move them to the right discipline or zone workset.", onWs1.Select(e => e.Id));
                    }
                    x.Counts["Worksets"] = user.Count;
                });

            // ---- Levels, grids and links -----------------------------------------------------------------------------
            Safe("pinned", () =>
            {
                var datums = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Element>()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(Grid))).Where(e => !e.Pinned).ToList();
                Add(x, "Levels, grids and links", "Levels and grids not pinned", datums.Count, 0.2, 3,
                    $"{datums.Count} levels or grids can be moved by accident", "Pin levels and grids (Modify > Pin).", datums.Select(e => e.Id));
                var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Where(e => !e.Pinned).ToList();
                Add(x, "Levels, grids and links", "Revit links not pinned", links.Count, 1, 3,
                    $"{links.Count} link instances are not pinned", "Pin links after positioning them (shared coordinates).", links.Select(e => e.Id));
            });

            // ---- Warnings -----------------------------------------------------------------------------------------------
            Safe("critical warnings", () =>
            {
                var critical = warnings.Where(w =>
                {
                    var t = w.Text;
                    return t.IndexOf("identical instances", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           t.IndexOf("overlap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           t.IndexOf("Room separation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           t.IndexOf("same place", StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
                Add(x, "Warnings", "Duplicate and overlapping elements", critical.Count, 0.1, 5,
                    $"{critical.Count} warnings about duplicate instances, overlapping walls or room separation lines",
                    "Delete duplicates and overlaps first: they distort quantities, rooms and schedules.", critical.SelectMany(w => w.Message.GetFailingElements()));
            });

            // ---- Model content ------------------------------------------------------------------------------------------
            Safe("cad", () =>
            {
                var linkedCad3D = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => i.IsLinked && !i.ViewSpecific).ToList();
                Add(x, "Model content", "Linked CAD in 3D (not view-specific)", linkedCad3D.Count, 0.5, 3,
                    $"{linkedCad3D.Count} CAD links placed in all views", "Link CAD with 'Current view only' where it is only a background.", linkedCad3D.Select(i => i.Id));
            });
            Safe("images", () =>
            {
                var images = new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).ToList();
                Add(x, "Model content", "Raster images", images.Count, 0.2, 2,
                    $"{images.Count} images placed", "Remove images that are not needed on sheets; they add file size.", images.Select(i => i.Id));
            });
            Safe("groups", () =>
            {
                var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().Where(g => g.Category?.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups).ToList();
                var once = groups.GroupBy(g => g.GroupType.Id.Value).Where(g => g.Count() == 1).SelectMany(g => g).ToList();
                Add(x, "Model content", "Model groups used once", once.Count, 0.2, 3,
                    $"{once.Count} model groups have a single instance ({groups.Count} groups in total)", "Ungroup single-use groups; groups slow editing and cause warnings.", once.Select(g => g.Id));
            });
            Safe("unused types", () =>
            {
                var used = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Select(f => f.GetTypeId().Value));
                var unused = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(s => s.Category != null && s.Category.CategoryType == CategoryType.Model && !used.Contains(s.Id.Value)).ToList();
                var families = unused.Select(s => s.Family.Id.Value).Distinct().Count();
                Add(x, "Model content", "Unused family types", unused.Count, 0.01, 4,
                    $"{unused.Count} model family types in {families} families are loaded but not placed", "Purge unused (Manage > Purge Unused) before issue.", unused.Select(s => s.Id));
            });
            Safe("design options", () =>
            {
                var options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).ToList();
                Add(x, "Model content", "Design options", options.Count, 0.25, 1,
                    $"{options.Count} design options", "Accept or delete design options that are decided.", options.Select(o => o.Id));
            });
            Safe("generic models", () =>
            {
                var generic = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().ToList();
                var total = Math.Max(1, x.Counts.TryGetValue("Model elements", out var m) ? m : 1);
                var share = (double)generic.Count / total;
                var c = Add(x, "Model content", "Generic models", generic.Count, 0, 2,
                    $"{generic.Count} generic model elements ({share:P1} of model elements)", "Use the proper category (furniture, equipment, casework...) so schedules and filters work.", generic.Select(e => e.Id));
                c.Penalty = share > 0.05 ? 2 : share > 0.02 ? 1 : 0;
                c.Rule = "review above 2%, action above 5% of model elements";
            });

            // ---- Views ----------------------------------------------------------------------------------------------------
            Safe("views", () =>
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
                var templates = all.Where(v => v.IsTemplate).ToList();
                var usedTemplates = new HashSet<long>(all.Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId).Select(v => v.ViewTemplateId.Value));
                var unusedTemplates = templates.Where(t => !usedTemplates.Contains(t.Id.Value)).ToList();
                Add(x, "Views and sheets", "Unused view templates", unusedTemplates.Count, 0.1, 2,
                    $"{unusedTemplates.Count} of {templates.Count} view templates are not used by any view", "Delete templates that are not part of the office standard.", unusedTemplates.Select(v => v.Id));
                var copies = all.Where(v => !v.IsTemplate && CopyName.IsMatch(v.Name ?? "")).ToList();
                Add(x, "Naming", "Views named 'Copy'", copies.Count, 0.1, 3,
                    $"{copies.Count} views have 'Copy' in their name", "Rename views to the office naming standard, or delete leftovers.", copies.Select(v => v.Id));
            });

            // ---- Annotation -----------------------------------------------------------------------------------------------
            Safe("annotation", () =>
            {
                var detailLines = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>().Where(c => c.CurveElementType == CurveElementType.DetailCurve).ToList();
                var d = Add(x, "Annotation", "Detail lines", detailLines.Count, 0, 2,
                    $"{detailLines.Count} detail lines", "Model what is modelled; use detail components and filled regions instead of many loose lines.", detailLines.Select(e => e.Id));
                d.Penalty = detailLines.Count > 5000 ? 2 : detailLines.Count > 1000 ? 1 : 0;
                d.Rule = "review above 1000, action above 5000";

                var overridden = new List<ElementId>();
                foreach (var dim in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
                {
                    try
                    {
                        if (dim.NumberOfSegments > 1) { foreach (DimensionSegment seg in dim.Segments) if (!string.IsNullOrEmpty(seg.ValueOverride)) { overridden.Add(dim.Id); break; } }
                        else if (!string.IsNullOrEmpty(dim.ValueOverride)) overridden.Add(dim.Id);
                    }
                    catch { }
                }
                Add(x, "Annotation", "Dimensions with overridden values", overridden.Count, 0.2, 3,
                    $"{overridden.Count} dimensions show text instead of the measured value", "Remove the overrides so dimensions show the real distance.", overridden);

                var lineStyles = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines)?.SubCategories.Size ?? 0;
                var ls = Add(x, "Annotation", "Line styles", lineStyles, 0, 2, $"{lineStyles} line styles", "Keep to the office line styles; purge imported ones.");
                ls.Penalty = lineStyles > 80 ? 2 : lineStyles > 50 ? 1 : 0;
                ls.Rule = "review above 50, action above 80";
                var textTypes = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).GetElementCount();
                var tt = Add(x, "Annotation", "Text types", textTypes, 0, 1, $"{textTypes} text types", "Keep to the office text types.");
                tt.Penalty = textTypes > 30 ? 1 : 0;
                tt.Rule = "review above 30";
            });

            // ---- Presentation standard: text height by view scale, one type per size and style --------------------------
            Safe("presentation standard", () =>
            {
                var std = PresentationStandard.Load();
                var audit = AnnotationAudit.Run(doc, std);
                var off = Add(x, "Annotation", "Text off the presentation standard", audit.OffStandard.Count, 0.02, 4,
                    $"{audit.OffStandard.Count} of {audit.Checked} text notes and dimensions in {audit.Views} views have the wrong text height for their scale ({std.Describe()})",
                    "Apply the presentation standard (ask Claude: presentation_standard, previewed first).", audit.OffStandard);
                off.Rule = std.Describe();
                Add(x, "Annotation", "Mixed annotation types", audit.ExtraTypes, 0.2, 2,
                    $"{audit.ExtraTypes} more text or dimension types are used than one per size and style (across {audit.Views} views)",
                    "Unify them: the presentation standard gives each size and style one type.");
            });

            // ---- Naming -----------------------------------------------------------------------------------------------------
            Safe("type names", () =>
            {
                var copies = new FilteredElementCollector(doc).WhereElementIsElementType().Where(t => CopyName.IsMatch(t.Name ?? "") || Regex.IsMatch(t.Name ?? "", @"\s2$")).ToList();
                Add(x, "Naming", "Types named 'Copy' or ending in 2", copies.Count, 0.05, 2,
                    $"{copies.Count} types look like accidental duplicates (e.g. 'Copy 1', 'Wall 2')", "Rename or merge duplicate types.", copies.Select(t => t.Id));
            });
        }

        /// <summary>The latest stored clash results (from the Clash Browser and clash tests), for the tools status and the dashboard.</summary>
        private static void ClashStatus(Document doc, Insights x)
        {
            try
            {
                // The latest clash results in this session, else the stored ones (published, so issues are computed once).
                var all = Coordination.Clashes.Current(doc);
                if (all.Count == 0) return;
                var open = all.Where(Coordination.ClashLogic.IsOpen).ToList();
                var issues = StatusStore.For(doc.Title)?.ClashIssues ?? Coordination.ClashLogic.Issues(all);
                x.Clashes = new ClashSummary
                {
                    Open = open.Count, Issues = issues.Count, New = open.Count(c => c.Status == "new"),
                    Approved = all.Count(c => c.Status == "approved"), Resolved = all.Count(c => c.Status == "resolved"),
                    LastRun = all.Max(c => c.LastSeen),
                    ByResponsible = issues.GroupBy(i => i.Responsible).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).ToList(),
                };
            }
            catch (Exception ex) { Log.Warn($"Dashboard clash status: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The office check set: which checks run and their pass rules, like a Model Checker configuration file.
    /// config.json "checkSet" (a path, e.g. on the team share) or %APPDATA%\ACE-RevitMCP\checkset.json:
    /// { "name": "ACE Standard", "checks": { "in-place-families": { "enabled": true, "warnAt": 1, "failAt": 5, "maxImpact": 8 } } }
    /// Keys are the check keys shown in the dashboard JSON (or the check names).
    /// </summary>
    internal static class CheckSet
    {
        /// <summary>The office check set: config.json "checkSet" (e.g. on the team share), else %APPDATA%\ACE-RevitMCP\checkset.json.</summary>
        internal static string FilePath()
        {
            try
            {
                var cfg = File.Exists(AceConfig.FilePath) ? JsonNode.Parse(File.ReadAllText(AceConfig.FilePath)) as JsonObject : null;
                var configured = cfg?["checkSet"]?.ToString();
                return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : Path.Combine(AceConfig.Directory, "checkset.json");
            }
            catch { return Path.Combine(AceConfig.Directory, "checkset.json"); }
        }

        internal static void Apply(Insights x)
        {
            string file = null;
            try
            {
                file = FilePath();
                if (!File.Exists(file)) return;
                var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (json == null) return;
                x.CheckSetName = json["name"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);
                x.CheckSetSource = file;
                if (!(json["checks"] is JsonObject rules)) return;
                foreach (var c in x.Checks.ToList())
                {
                    var rule = rules[c.Key] as JsonObject ?? rules.FirstOrDefault(r => string.Equals(r.Key, c.Name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
                    if (rule == null) continue;
                    if (rule["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled) && !enabled) { x.Checks.Remove(c); continue; }
                    if (rule["maxImpact"] is JsonValue mv && mv.TryGetValue<double>(out var max)) { c.MaxPenalty = max; c.Penalty = Math.Min(c.Penalty, max); }
                    int? At(string k) => rule[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : (int?)null;
                    var warnAt = At("warnAt"); var failAt = At("failAt");
                    if (warnAt == null && failAt == null) continue;
                    c.StatusOverride = failAt != null && c.Count >= failAt ? "fail" : warnAt != null && c.Count >= warnAt ? "warn" : "ok";
                    if (c.StatusOverride == "ok") c.Penalty = 0;
                    else if (c.StatusOverride == "fail") c.Penalty = Math.Max(c.Penalty, c.MaxPenalty * 0.6);
                    c.Rule = string.Join(", ", new[] { warnAt != null ? $"review at {warnAt}" : null, failAt != null ? $"action at {failAt}" : null }.Where(s => s != null));
                }
            }
            catch (Exception ex) { Log.Warn($"Check set {file}: {ex.Message}"); x.CheckSetSource = "built-in (check set not readable: " + ex.Message + ")"; }
        }
    }
}
