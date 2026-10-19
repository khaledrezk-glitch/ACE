using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// P4 impact scan (read-only, never inside a transaction): for each parameter a write would touch, the schedules with a
    /// field of it (and their filters on that field), the view filters with a rule on it, and the tag and title block
    /// families whose parameters stand for a label of it (Revit cannot read a label's contents: inferred), with the number
    /// of sheets that print them.
    /// </summary>
    internal static class ParameterImpact
    {
        internal sealed class Target
        {
            public string Name;
            public Guid? Guid;
            public HashSet<long> Ids = new HashSet<long>();   // parameter element ids (shared, project) or built-in parameter ids
            public string Key;                                // the answer's key (default: the name)
            public string Note;                               // an extra note for this parameter (e.g. a rebind drops these usages)
        }

        private sealed class FamilyParams
        {
            public HashSet<Guid> Guids = new HashSet<Guid>();
            public HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
            public string Error;
        }

        /// <summary>What a family holds, per model, family and family version: EditFamily runs once per loaded version.</summary>
        private static readonly Dictionary<string, FamilyParams> FamilyCache = new Dictionary<string, FamilyParams>(StringComparer.Ordinal);
        private const int MaxFamilies = 300;

        /// <summary>{ "<parameter>": { guid, schedules: [{ name, id, filters: [{ type, value }] }], viewFilters: [names], labels: [{ family, kind, sheets }], inferred: true, notes } }.</summary>
        public static JsonObject Scan(Document doc, IList<Target> targets)
        {
            var answer = new JsonObject();
            if (targets == null || targets.Count == 0) return answer;
            var schedules = Schedules(doc, targets);
            var filters = ViewFilters(doc, targets);
            var (labels, notes) = Labels(doc, targets);
            foreach (var t in targets)
            {
                var key = t.Key ?? t.Name;
                if (answer.ContainsKey(key)) continue;
                var tnotes = notes.ToList();
                if (t.Note != null) tnotes.Insert(0, t.Note);
                answer[key] = new JsonObject
                {
                    ["guid"] = t.Guid?.ToString(),
                    ["schedules"] = schedules.TryGetValue(key, out var s) ? s : new JsonArray(),
                    ["viewFilters"] = filters.TryGetValue(key, out var f) ? new JsonArray(f.Select(n => (JsonNode)n).ToArray()) : new JsonArray(),
                    ["labels"] = labels.TryGetValue(key, out var l) ? l : new JsonArray(),
                    ["inferred"] = true,
                    ["notes"] = new JsonArray(tnotes.Select(n => (JsonNode)n).ToArray()),
                };
            }
            return answer;
        }

        // ---- schedules ------------------------------------------------------------------------------------------

        private static Dictionary<string, JsonArray> Schedules(Document doc, IList<Target> targets)
        {
            var hits = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
            foreach (var vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                try
                {
                    if (vs.IsTemplate || vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) continue;
                    var def = vs.Definition;
                    var fieldCount = def.GetFieldCount();
                    for (var i = 0; i < fieldCount; i++)
                    {
                        var field = def.GetField(i);
                        var pid = field.ParameterId;
                        if (pid == null || pid == ElementId.InvalidElementId) continue;
                        foreach (var t in targets.Where(t => t.Ids.Contains(pid.Value)))
                        {
                            var fl = new JsonArray();
                            for (var j = 0; j < def.GetFilterCount(); j++)
                            {
                                var f = def.GetFilter(j);
                                if (f.FieldId != field.FieldId) continue;
                                string value = null;
                                try { if (f.IsStringValue) value = f.GetStringValue(); } catch { value = null; }
                                fl.Add(new JsonObject { ["type"] = f.FilterType.ToString(), ["value"] = value });
                            }
                            if (!hits.TryGetValue(t.Key ?? t.Name, out var list)) hits[t.Key ?? t.Name] = list = new JsonArray();
                            if (list.Count < 200) list.Add(new JsonObject { ["name"] = vs.Name, ["id"] = vs.Id.Value, ["filters"] = fl });
                        }
                    }
                }
                catch { /* one unreadable schedule never fails the scan */ }
            }
            return hits;
        }

        // ---- view filters ---------------------------------------------------------------------------------------

        private static Dictionary<string, List<string>> ViewFilters(Document doc, IList<Target> targets)
        {
            var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pfe in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
            {
                try
                {
                    var rules = new List<FilterRule>();
                    Walk(pfe.GetElementFilter(), rules, 0);
                    foreach (var t in targets)
                        if (rules.Any(r => Mentions(r, t, 0)))
                        {
                            if (!hits.TryGetValue(t.Key ?? t.Name, out var l)) hits[t.Key ?? t.Name] = l = new List<string>();
                            if (!l.Contains(pfe.Name)) l.Add(pfe.Name);
                        }
                }
                catch { /* as above */ }
            }
            return hits;
        }

        /// <summary>The rules of a filter, through And / Or filters at any depth.</summary>
        private static void Walk(ElementFilter f, List<FilterRule> rules, int depth)
        {
            if (f == null || depth > 20) return;
            if (f is ElementLogicalFilter lf) foreach (var sub in lf.GetFilters()) Walk(sub, rules, depth + 1);
            else if (f is ElementParameterFilter pf) rules.AddRange(pf.GetRules());
        }

        private static bool Mentions(FilterRule r, Target t, int depth)
        {
            if (r == null || depth > 5) return false;
            if (r is FilterInverseRule inv) return Mentions(inv.GetInnerRule(), t, depth + 1);
            if (r is SharedParameterApplicableRule spa) return string.Equals(spa.ParameterName, t.Name, StringComparison.Ordinal);
            try { return t.Ids.Contains(r.GetRuleParameter().Value); } catch { return false; }
        }

        // ---- labels in tag and title block families -------------------------------------------------------------

        private static (Dictionary<string, JsonArray>, List<string>) Labels(Document doc, IList<Target> targets)
        {
            var hits = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
            var notes = new List<string> { "Labels are inferred: Revit cannot read a label's contents, so a family that holds the parameter (by shared GUID or name) is taken to show it." };
            if (doc.IsModifiable)
            {
                notes.Add("Labels not scanned: the model is inside a transaction (families can only be opened outside one).");
                return (hits, notes);
            }

            // Annotation instances per family, with their owner views.
            var cats = new List<BuiltInCategory> { BuiltInCategory.OST_TitleBlocks };
            foreach (Category c in doc.Settings.Categories)
            {
                try { if (c.IsTagCategory && c.BuiltInCategory != BuiltInCategory.INVALID && !cats.Contains(c.BuiltInCategory)) cats.Add(c.BuiltInCategory); }
                catch { /* skip */ }
            }
            var familyOf = new Dictionary<long, Family>();
            var perFamily = new Dictionary<long, (Family Family, string Kind, List<long> Views)>();
            foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                var tid = e.GetTypeId();
                if (tid == ElementId.InvalidElementId) continue;
                if (!familyOf.TryGetValue(tid.Value, out var fam)) familyOf[tid.Value] = fam = (doc.GetElement(tid) as FamilySymbol)?.Family;
                if (fam == null) continue;
                if (!perFamily.TryGetValue(fam.Id.Value, out var entry))
                {
                    var kind = e.Category?.BuiltInCategory == BuiltInCategory.OST_TitleBlocks ? "titleblock" : "tag";
                    perFamily[fam.Id.Value] = entry = (fam, kind, new List<long>());
                }
                var owner = e.OwnerViewId;
                if (owner != null && owner != ElementId.InvalidElementId) entry.Views.Add(owner.Value);
            }
            if (perFamily.Count == 0) return (hits, notes);

            // Sheets, and the sheets each view is placed on.
            var sheets = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).ToElementIds().Select(i => i.Value));
            var viewToSheets = new Dictionary<long, List<long>>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (!viewToSheets.TryGetValue(vp.ViewId.Value, out var l)) viewToSheets[vp.ViewId.Value] = l = new List<long>();
                l.Add(vp.SheetId.Value);
            }

            var docKey = Companion.ActivityHub.DocKey(doc);
            // Family documents open in the family editor are not scanned (and never closed: ACE did not open them).
            var openFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Document d in doc.Application.Documents)
                if (d.IsFamilyDocument) openFamilies.Add(System.IO.Path.GetFileNameWithoutExtension(d.Title));
            var opened = 0;
            var skipped = new List<string>();
            foreach (var (family, kind, views) in perFamily.Values.OrderBy(v => v.Family.Name, StringComparer.Ordinal))
            {
                if (openFamilies.Contains(family.Name)) { notes.Add($"{family.Name}: open in the family editor: not scanned"); continue; }
                var fp = FamilyParamsOf(doc, docKey, family, ref opened);
                if (fp == null) { skipped.Add(family.Name); continue; }
                if (fp.Error != null) { notes.Add($"{family.Name}: not read ({fp.Error})"); continue; }
                int? sheetCount = null;
                foreach (var t in targets)
                {
                    if (!ParameterPlanLogic.LabelProxy(fp.Guids, fp.Names, t.Guid, t.Name)) continue;
                    sheetCount ??= ParameterPlanLogic.SheetsShowing(views, viewToSheets, sheets);
                    if (!hits.TryGetValue(t.Key ?? t.Name, out var list)) hits[t.Key ?? t.Name] = list = new JsonArray();
                    list.Add(new JsonObject { ["family"] = family.Name, ["kind"] = kind, ["sheets"] = sheetCount.Value, ["instances"] = views.Count });
                }
            }
            if (skipped.Count > 0)
                notes.Add($"{skipped.Count} famil{(skipped.Count == 1 ? "y" : "ies")} not opened (more than {MaxFamilies} per scan): {string.Join(", ", skipped.Take(10))}{(skipped.Count > 10 ? ", ..." : "")}");
            return (hits, notes);
        }

        /// <summary>The family's shared GUIDs and parameter names, from the cache or by opening it (closed without saving).</summary>
        private static FamilyParams FamilyParamsOf(Document doc, string docKey, Family family, ref int opened)
        {
            string version;
            try { version = family.VersionGuid.ToString(); } catch { version = ""; }
            var key = docKey + "\n" + family.UniqueId + "\n" + version;
            if (FamilyCache.TryGetValue(key, out var cached)) return cached;
            if (opened >= MaxFamilies) return null;
            var fp = new FamilyParams();
            if (!family.IsEditable) fp.Error = "the family cannot be opened for editing";
            else
            {
                Document fdoc = null;
                var mine = false;
                opened++;
                try
                {
                    var before = new List<Document>();
                    foreach (Document d in doc.Application.Documents) before.Add(d);
                    fdoc = doc.EditFamily(family);
                    mine = !before.Any(d => d.Equals(fdoc));   // an already open family document is the user's
                    foreach (FamilyParameter p in fdoc.FamilyManager.Parameters)
                    {
                        if (p.Definition?.Name is string n) fp.Names.Add(n);
                        try { if (p.IsShared) fp.Guids.Add(p.GUID); } catch { /* not shared */ }
                    }
                    foreach (var spe in new FilteredElementCollector(fdoc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>())
                    {
                        fp.Guids.Add(spe.GuidValue);
                        if (spe.Name != null) fp.Names.Add(spe.Name);
                    }
                }
                catch (Exception ex) { fp.Error = ex.Message; }
                finally { if (mine) { try { fdoc?.Close(false); } catch { /* already closed */ } } }
            }
            if (fp.Error != null) return fp;   // only successful scans are cached
            if (FamilyCache.Count > 5000) FamilyCache.Clear();
            FamilyCache[key] = fp;
            return fp;
        }
    }
}
