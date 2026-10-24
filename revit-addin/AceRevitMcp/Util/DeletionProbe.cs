using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using L = AceRevitMcp.Util.DeletionProbeLogic;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The deletion probe (register #42): deletes candidates in transactions that are ALWAYS rolled back and reports, per
    /// candidate, whether Revit deletes only it or takes other elements along (ids, categories, instances or types). The
    /// classification is DeletionProbeLogic (bulk first, halves only when the bulk takes something outside the list, capped).
    /// What Revit deletes comes from Document.Delete's returned ids: a rolled-back probe raises no DocumentChanged, and on a
    /// commit DocumentChanged lists the same deletions plus warning records Revit drops (resolved warnings, not dependents).
    /// The transaction kind follows the caller: inside an open transaction (a script in mode auto) a SubTransaction; with
    /// none open (a native command between its own transactions, or after a preview's group was rolled back) a Transaction.
    /// The model is exactly as before afterwards; the probe's rollbacks add no undo step.
    /// </summary>
    internal static class DeletionProbe
    {
        public sealed class Report
        {
            public L.Result Result;
            /// <summary>Candidates and the others they take, read after the rollback (they exist again): label, name, type or instance, instances of a type.</summary>
            public Dictionary<long, (string Category, string Name, bool IsType, int? Instances)> Info = new Dictionary<long, (string, string, bool, int?)>();
            /// <summary>"subtransaction" or "transaction".</summary>
            public string Kind;
        }

        /// <param name="notOthers">Ids that never count as others (see DeletionProbeLogic.Probe).</param>
        /// <param name="bulkTakesOthers">The whole list was already deleted once and took others: the probe starts with the halves.</param>
        public static Report Run(Document doc, IEnumerable<long> candidates, int maxDeletes = L.DefaultMaxDeletes, IEnumerable<long> notOthers = null, bool bulkTakesOthers = false)
        {
            var rep = new Report();
            var inTransaction = false;
            try { inTransaction = doc.IsModifiable; } catch { }
            rep.Kind = inTransaction ? "subtransaction" : "transaction";
            using (ChangeTracker.Temporary())
                rep.Result = L.Probe(candidates, chunk => inTransaction ? InSub(doc, chunk) : InTransaction(doc, chunk), maxDeletes, notOthers, bulkTakesOthers);
            Describe(doc, rep);
            return rep;
        }

        private static (ICollection<long>, string) InSub(Document doc, List<long> chunk)
        {
            using (var st = new SubTransaction(doc))
            {
                try
                {
                    st.Start();
                    return (Delete(doc, chunk), null);
                }
                catch (Exception ex) { return (null, Why(ex)); }
                finally { if (st.HasStarted() && !st.HasEnded()) st.RollBack(); }
            }
        }

        private static (ICollection<long>, string) InTransaction(Document doc, List<long> chunk)
        {
            using (var t = new Transaction(doc, "ACE: deletion probe (rolled back)"))
            {
                try
                {
                    t.Start();
                    return (Delete(doc, chunk), null);
                }
                catch (Exception ex) { return (null, Why(ex)); }
                finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
            }
        }

        private static ICollection<long> Delete(Document doc, List<long> chunk)
        {
            var live = chunk.Select(i => new ElementId(i)).Where(i => doc.GetElement(i) != null).ToList();
            if (live.Count == 0) return new List<long>();
            return (doc.Delete(live) ?? new List<ElementId>()).Select(i => i.Value).ToList();
        }

        private static string Why(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

        /// <summary>Labels the candidates and the others (after the rollback they exist again); counts instances of other types (one collector).</summary>
        private static void Describe(Document doc, Report rep)
        {
            var ids = rep.Result.Outcomes.Select(o => o.Id).Concat(rep.Result.AllOthers()).Distinct().Take(5000).ToList();
            var types = new HashSet<long>();
            foreach (var id in ids)
            {
                try
                {
                    var e = doc.GetElement(new ElementId(id));
                    if (e == null) { rep.Info[id] = ("elements", null, false, null); continue; }
                    var isType = e is ElementType;
                    rep.Info[id] = (Label(e), NameOf(e), isType, null);
                    if (isType) types.Add(id);
                }
                catch { rep.Info[id] = ("elements", null, false, null); }
            }
            var otherTypes = new HashSet<long>(rep.Result.AllOthers().Where(types.Contains));
            if (otherTypes.Count == 0) return;
            try
            {
                var n = otherTypes.ToDictionary(i => i, i => 0);
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    long t;
                    try { t = e.GetTypeId().Value; } catch { continue; }
                    if (n.ContainsKey(t)) n[t]++;
                }
                foreach (var kv in n) { var d = rep.Info[kv.Key]; rep.Info[kv.Key] = (d.Category, d.Name, true, kv.Value); }
            }
            catch { /* not counted: "instance count not known" */ }
        }

        private static string Label(Element e)
        {
            try { return e.Category?.Name ?? (e is ElementType ? "Types" : e is Family ? "Families" : e.GetType().Name); }
            catch { return "elements"; }
        }

        private static string NameOf(Element e)
        {
            try
            {
                if (e is FamilyInstance fi) return $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}";
                if (e is FamilySymbol fs) return $"{fs.FamilyName} : {fs.Name}";
                return string.IsNullOrWhiteSpace(e.Name) ? null : e.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// The probe as JSON: counts per status, the summary line, and one entry per held candidate (what it would take).
        /// allowName: the parameter that lets a candidate that takes others go (null: report only); allowed: the ids it names.
        /// </summary>
        public static JsonObject ToJson(Report rep, string allowName, ICollection<long> allowed, int maxIds = 50)
        {
            var r = rep.Result;
            (string Category, string Name, bool IsType, int? Instances) I(long id) => rep.Info.TryGetValue(id, out var d) ? d : ("elements", null, false, null);
            var held = r.Outcomes.Where(o => o.Status != L.OnlyTarget).ToList();
            JsonArray Ids(IEnumerable<long> ids) => new JsonArray(ids.Take(maxIds).Select(i => (JsonNode)i).ToArray());
            return new JsonObject
            {
                ["summary"] = L.Summary(r, allowName, allowed),
                ["candidates"] = r.Outcomes.Count,
                ["onlyTarget"] = r.Count(L.OnlyTarget),
                ["takesOthers"] = r.Count(L.TakesOthers),
                ["refused"] = r.Count(L.Refused),
                ["notProbed"] = r.Count(L.NotProbed),
                ["capped"] = r.Capped,
                ["deletes"] = r.Deletes,
                ["maxDeletes"] = r.MaxDeletes,
                ["transaction"] = rep.Kind,
                ["source"] = "Document.Delete in a rolled-back " + rep.Kind,
                ["held"] = new JsonArray(held.Take(maxIds).Select(o =>
                {
                    var t = I(o.Id);
                    var others = o.Others.Select(i => (Id: i, D: I(i))).ToList();
                    return (JsonNode)new JsonObject
                    {
                        ["id"] = o.Id, ["category"] = t.Category, ["name"] = t.Name, ["status"] = o.Status,
                        ["allowed"] = o.Status == L.TakesOthers && allowed != null && allowed.Contains(o.Id),
                        ["line"] = L.OthersLine(o, t.Category, t.Name, rep.Info),
                        ["error"] = o.Error,
                        ["others"] = o.Others.Count,
                        ["otherIds"] = Ids(o.Others),
                        ["otherInstances"] = others.Count(x => !x.D.IsType),
                        ["otherTypes"] = others.Count(x => x.D.IsType),
                        ["otherCategories"] = new JsonArray(others.GroupBy(x => x.D.Category ?? "elements").OrderByDescending(g => g.Count())
                            .Select(g => (JsonNode)$"{g.Key} {g.Count()}").ToArray()),
                    };
                }).ToArray()),
                ["notProbedIds"] = Ids(r.With(L.NotProbed).Select(o => o.Id)),
            };
        }

        /// <summary>
        /// The workset preview's deletion line (#40): after a dry run (the group is rolled back, so what it deleted exists again),
        /// the elements and types it reports deleted are probed (capped) and the result is added: wouldChange.deletionProbe and
        /// one attention line, e.g. "Deletion probe: all 10 delete only themselves." A preview that deletes nothing is untouched.
        /// Read-only and best effort: a probe that fails is reported, never fails the preview.
        /// </summary>
        public static void CheckPreview(Document doc, JsonObject result, bool dryRun, int maxDeletes = 50)
        {
            try
            {
                if (!dryRun || doc == null || !(result?["wouldChange"] is JsonObject wc)) return;
                if (!(result["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) && ok)) return;
                var ids = new List<long>();
                foreach (var key in new[] { "deletedIds", "deletedTypeIds" })
                    if (wc[key] is JsonArray a) ids.AddRange(a.Select(x => x is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0).Where(i => i != 0));
                if (ids.Count == 0) return;
                int N(string k) => wc[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
                if (ids.Count < N("deleted") + N("deletedTypes"))
                {
                    // The summary lists at most 200 ids per kind: a part would read the rest as "others". Not probed, said so.
                    wc["deletionProbe"] = new JsonObject { ["summary"] = $"Deletion probe: not run ({N("deleted") + N("deletedTypes")} deletions, more than the summary lists).", ["capped"] = true };
                    return;
                }
                var rep = Run(doc, ids, maxDeletes);
                var j = ToJson(rep, null, null, 20);
                wc["deletionProbe"] = j;
                if (!(wc["attention"] is JsonArray att)) wc["attention"] = att = new JsonArray();
                att.Add(j["summary"]?.ToString());
                foreach (var h in (j["held"] as JsonArray ?? new JsonArray()).Take(5)) att.Add(h?["line"]?.ToString());
            }
            catch (Exception ex) { Log.Warn("Deletion probe of the preview failed: " + ex.Message); }
        }
    }
}
