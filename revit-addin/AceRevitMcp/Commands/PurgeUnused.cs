using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Rules;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Commands.PurgeLogic;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Bridge "purge_unused": { max_passes? (default 3, at most 5), check_only, dry_run, expect_model, expect_version,
    /// expect_plan, max_ids }. A FULL Purge Unused, the owner's method (purge three times in a row to reach zero), without
    /// Revit's dialog: each pass deletes the list Revit's Purge Unused shows (Document.GetUnusedElements, all categories)
    /// with ONE Document.Delete in its own transaction; passes repeat until a pass finds nothing or max_passes. Not an
    /// element delete: only what Revit calls unused goes; Revit link types are left out. Specific types are purged by hand
    /// in Revit's own dialog. A set Revit refuses is split in halves, each half in a fresh transaction (never per-id
    /// SubTransactions: they crashed Revit 2025), at most 12 attempts per pass; ids that still fail are skipped and listed.
    /// All passes sit in one TransactionGroup through NativeChange: the dry run rolls it back and remembers what each pass
    /// found; the identical apply (expect_plan = the dry run's planHash over every pass) must find the same set pass by pass,
    /// else it stops before that pass deletes anything and the whole group is rolled back (plan-changed). One more
    /// GetUnusedElements after the last pass says how many unused elements are left.
    /// </summary>
    internal static class PurgeUnused
    {
        public const string Source = "Document.GetUnusedElements";
        private const int MaxAttempts = 12;

        private sealed class Previewed
        {
            public string PlanHash;
            public List<List<long>> Passes;
        }

        /// <summary>The last successful dry run per request and model in this Revit session: each pass's found set.</summary>
        private static readonly Dictionary<string, Previewed> Previews = new Dictionary<string, Previewed>(StringComparer.Ordinal);

        private sealed class PlanChangedException : Exception
        {
            public PlanChangedException(string message) : base(message) { }
        }

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var aceVersion = ParameterFill.AceVersion();
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));
            var maxPasses = Util.Compat.Clamp(Args.Int(args, "max_passes", 3), 1, 5);
            var checkOnly = Args.Bool(args, "check_only");
            var dryRun = Args.Bool(args, "dry_run");

            JsonObject Refuse(string stage, string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = stage, ["error"] = error, ["blocked"] = blocked ?? new JsonArray(), ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["source"] = Source,
            };
            JsonObject Block(string what, string code, string why, string how = null, IEnumerable<long> ids = null, int elements = 0) => new JsonObject
            {
                ["what"] = what, ["why"] = why, ["how"] = how, ["code"] = code, ["elements"] = elements,
                ["sampleIds"] = new JsonArray((ids ?? Enumerable.Empty<long>()).Take(maxIds).Select(i => (JsonNode)i).ToArray()),
            };

            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
                return Refuse("blocked", $"Nothing was changed: the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}.",
                    new JsonArray(Block("the whole plan", "version", $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}")));
            var expectModel = Args.Str(args, "expect_model");
            if (expectModel != null && !string.Equals(expectModel, doc.Title, StringComparison.Ordinal))
                return Refuse("wrong_model", $"Nothing was changed: the preview was made on {expectModel}, the active model is {doc.Title}. Preview again on this model.",
                    new JsonArray(Block("the whole plan", "wrong-model", $"the preview was made on {expectModel}, the active model is {doc.Title}")));
            // Only the active model's own elements: a linked model is another document and is never touched from the host.
            if (doc.IsFamilyDocument || doc.IsReadOnly || doc.IsLinked)
                return Refuse("blocked", "Nothing was changed: the model cannot be edited.", new JsonArray(Block("the model", "read-only", "a read-only, linked or family document")));

            // Revit link types are never purged (removing one removes the link), nor deleted along with something else.
            var linkTypes = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).ToElementIds().Select(i => i.Value));
            var kept = new HashSet<long>();
            List<long> Unused(Document d)
            {
                var list = new List<long>();
                foreach (var id in d.GetUnusedElements(new HashSet<ElementId>()).Select(i => i.Value))
                    if (linkTypes.Contains(id)) kept.Add(id); else list.Add(id);
                return list;
            }

            var first = L.NextSet(Unused(doc), null);
            var labels = new Dictionary<long, string>();
            var names = new Dictionary<long, string>();
            void Describe(Document d, IEnumerable<long> ids)
            {
                foreach (var id in ids)
                {
                    if (labels.ContainsKey(id)) continue;
                    var e = d.GetElement(new ElementId(id));
                    labels[id] = Label(e);
                    names[id] = NameOf(e);
                }
            }
            Describe(doc, first);
            // check_only knows pass 1 only; the dry run's planHash covers every pass.
            var planHash = L.PlanHash(new[] { first }, maxPasses);
            string LabelOf(long i) => labels.TryGetValue(i, out var l) ? l : "(unknown)";
            JsonArray ByCategory(IEnumerable<long> found, ICollection<long> deleted) => new JsonArray(L.Top(found.Select(LabelOf), 20)
                .Select(kv => (JsonNode)new JsonObject
                {
                    ["category"] = kv.Key, ["found"] = kv.Value,
                    ["deleted"] = deleted == null ? null : (JsonNode)found.Count(i => deleted.Contains(i) && LabelOf(i) == kv.Key),
                }).ToArray());
            JsonArray Names(IEnumerable<long> ids, int n) => new JsonArray(ids.Take(n).Select(i => (JsonNode)(names.TryGetValue(i, out var s) && s != null ? s : i.ToString(CultureInfo.InvariantCulture))).ToArray());
            JsonObject Step(int n, List<long> found) => new JsonObject { ["id"] = $"pass-{n}", ["kind"] = "purge", ["count"] = found.Count, ["byCategory"] = ByCategory(found, null), ["names"] = Names(found, 10) };

            // Elements the first pass deletes must be editable now (workshared: not borrowed, not changed in central).
            var blocks = new JsonArray();
            foreach (var g in ViewMembership.Held(doc, first).GroupBy(h => (h.Code, h.Holder)).OrderByDescending(g => g.Count()))
                blocks.Add(Block($"{g.Count()} unused element{(g.Count() == 1 ? "" : "s")}", g.Key.Code,
                    g.Key.Code == "borrowed" ? $"borrowed by {(string.IsNullOrWhiteSpace(g.Key.Holder) ? "another user" : g.Key.Holder.Trim())}" : "changed in central since this model was reloaded",
                    g.Key.Code == "borrowed" ? "ask the holder to synchronise and relinquish, then run the dry run again" : "reload latest, then run the dry run again",
                    g.Select(h => h.Id), g.Count()));

            var plan = new JsonArray();
            if (first.Count > 0) plan.Add(Step(1, first));
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["planHash"] = planHash; r["source"] = Source;
                r["maxPasses"] = maxPasses; r["found"] = first.Count;
                r["plan"] = plan.DeepClone(); r["blocked"] = blocks.DeepClone();
                r["kept"] = kept.Count; r["keptIds"] = new JsonArray(kept.OrderBy(i => i).Take(maxIds).Select(i => (JsonNode)i).ToArray());
                return r;
            }
            JsonObject PlanChanged(string changed)
            {
                var refused = Common(Refuse("plan-changed", $"Nothing was changed: {changed}. Run the dry run again, then apply.", null));
                refused["code"] = "plan-changed"; refused["rolledBack"] = true; refused["planChangedDetail"] = changed;
                ((JsonArray)refused["blocked"]).Insert(0, Block("the whole plan", "plan-changed", changed, "run the dry run again, then apply"));
                return refused;
            }

            if (checkOnly)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["checkOnly"] = true; r["unusedLeft"] = first.Count + kept.Count;
                r["note"] = blocks.Count > 0 ? "Nothing was changed. Some unused elements are blocked (see blocked): the dry run and the apply refuse until they are cleared."
                    : first.Count == 0 ? "Nothing was changed, and there is nothing to purge." : "Nothing was changed. This is pass 1 only: later passes (what becomes unused once these are gone) show in the dry run.";
                return r;
            }
            if (blocks.Count > 0)
                return Common(Refuse("blocked", $"Nothing was changed: {blocks.Count} item{(blocks.Count == 1 ? " is" : "s are")} blocked ({blocks[0]?["why"]}). See blocked for what clears each one.", null));

            // The apply: the dry run of this request on this model, and the planHash the server holds must be its own.
            var requestKey = Companion.ActivityHub.Fingerprint("purge_unused", args) + "\n" + SafePath(doc) + "\n" + doc.Title;
            var expectPlan = Args.Str(args, "expect_plan")?.Trim();
            Previewed previewed = null;
            if (!dryRun)
            {
                lock (Previews) Previews.TryGetValue(requestKey, out previewed);
                if (previewed == null)
                {
                    if (first.Count > 0) return PlanChanged("no dry run of this purge is waiting in this Revit session");
                }
                else if (!string.IsNullOrEmpty(expectPlan) && !string.Equals(expectPlan, previewed.PlanHash, StringComparison.OrdinalIgnoreCase))
                    return PlanChanged("the preview the server holds is not the dry run this Revit session last ran");
                else if (first.Count == 0 && previewed.Passes.Count > 0) return PlanChanged(L.Compare(1, previewed.Passes[0], first));
            }
            if (first.Count == 0)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["passes"] = new JsonArray(); r["totalDeleted"] = 0; r["unusedLeft"] = kept.Count; r["rolledBack"] = false;
                r["note"] = "Nothing to purge: Revit reports no unused elements" + (kept.Count > 0 ? $" besides {kept.Count} Revit link type(s), which are never purged." : ".") + " Nothing was changed.";
                return r;
            }

            var deletedAll = new HashSet<long>();
            var linkTypesBefore = new List<long>();
            Previewed pending = null;
            JsonObject result;
            try
            {
                result = NativeChange.Run(app, args, "purge_unused", "ACE: purge unused", (d, look) =>
                {
                    var r = Common(new JsonObject());
                    if (look) return r;
                    deletedAll.Clear(); linkTypesBefore.Clear();
                    linkTypesBefore.AddRange(linkTypes.Where(i => d.GetElement(new ElementId(i)) != null));
                    var goneByPass = new Dictionary<int, HashSet<long>>();
                    var passArr = new JsonArray();
                    Func<int, List<long>, string> sameAsPreview = previewed == null ? null
                        : (n, set) => L.Compare(n, n <= previewed.Passes.Count ? previewed.Passes[n - 1] : new List<long>(), set);
                    var passes = L.RunPasses(() => Unused(d), (n, set) =>
                    {
                        Describe(d, set);
                        var gone = goneByPass[n] = new HashSet<long>();
                        return L.DeleteSet(set, chunk => TryDelete(d, n, chunk, linkTypes, gone), MaxAttempts);
                    }, maxPasses, null, sameAsPreview, out var stop, out var stopDetail);
                    // A pass that differs from the dry run stops before it deletes anything: the whole group is rolled back.
                    if (stop == "plan-changed") throw new PlanChangedException(stopDetail);
                    var allHash = L.PlanHash(passes.Select(p => (IEnumerable<long>)p.Found), maxPasses);
                    r["planHash"] = allHash;
                    var steps = (JsonArray)r["plan"];
                    foreach (var p in passes.Skip(1)) steps.Add(Step(p.Number, p.Found));
                    if (dryRun) pending = new Previewed { PlanHash = allHash, Passes = passes.Select(p => p.Found.ToList()).ToList() };
                    var totalAlso = 0;
                    foreach (var p in passes)
                    {
                        var del = new HashSet<long>(p.Result.Deleted);
                        var found = new HashSet<long>(p.Found);
                        goneByPass.TryGetValue(p.Number, out var g);
                        var alsoDeleted = g?.Count(i => !found.Contains(i)) ?? 0;
                        if (g != null) deletedAll.UnionWith(g);
                        totalAlso += alsoDeleted;
                        var skipped = p.Result.Failed.Select(f => new { f.Id, f.Error })
                            .Concat(p.Result.NotTried.Select(i => new { Id = i, Error = $"not tried: the {MaxAttempts} attempts of this pass were used up" })).ToList();
                        passArr.Add(new JsonObject
                        {
                            ["pass"] = p.Number, ["found"] = p.Found.Count, ["deleted"] = p.Result.Deleted.Count, ["alsoDeleted"] = alsoDeleted,
                            ["failed"] = skipped.Count, ["attempts"] = p.Result.Attempts,
                            ["byCategory"] = ByCategory(p.Found, del), ["names"] = Names(p.Result.Deleted, 5),
                            ["skipped"] = new JsonArray(skipped.Take(maxIds).Select(f => (JsonNode)new JsonObject
                            {
                                ["id"] = f.Id, ["name"] = names.TryGetValue(f.Id, out var nm) ? nm : null, ["category"] = labels.TryGetValue(f.Id, out var lb) ? lb : null, ["error"] = f.Error,
                            }).ToArray()),
                        });
                    }
                    // One more look, read-only: what Revit still calls unused after the last pass (link types and skipped included).
                    var raw = d.GetUnusedElements(new HashSet<ElementId>()).Select(i => i.Value).ToList();
                    var skippedIds = new HashSet<long>(passes.SelectMany(p => p.Result.Failed.Select(f => f.Id).Concat(p.Result.NotTried)));
                    var total = passes.Sum(p => p.Result.Deleted.Count);
                    var leftLinks = raw.Count(linkTypes.Contains);
                    var leftSkipped = raw.Count(i => skippedIds.Contains(i) && !linkTypes.Contains(i));
                    r["passes"] = passArr; r["stoppedBecause"] = stop;
                    r["totalDeleted"] = total; r["totalAlsoDeleted"] = totalAlso;
                    r["unusedLeft"] = raw.Count;
                    r["unusedLeftDetail"] = new JsonObject { ["linkTypes"] = leftLinks, ["skipped"] = leftSkipped, ["other"] = raw.Count - leftLinks - leftSkipped };
                    var counts = new JsonObject { ["deleted"] = total, ["passes"] = passes.Count };
                    if (dryRun) { r["wouldDo"] = counts; r["done"] = new JsonObject { ["deleted"] = 0, ["passes"] = 0 }; }
                    else r["done"] = counts;
                    return r;
                }, verify: (d, r) =>
                {
                    // Every link type is still there; every element reported deleted is gone.
                    var mismatches = new JsonArray();
                    foreach (var id in linkTypesBefore.Where(i => d.GetElement(new ElementId(i)) == null).Take(50))
                        mismatches.Add(new JsonObject { ["id"] = id, ["what"] = "a Revit link type was deleted", ["expected"] = "present", ["actual"] = "deleted" });
                    foreach (var id in deletedAll.Where(i => d.GetElement(new ElementId(i)) != null).Take(50))
                        mismatches.Add(new JsonObject { ["id"] = id, ["what"] = "reported deleted but still in the model", ["expected"] = "deleted", ["actual"] = "present" });
                    r["verify"] = new JsonObject { ["ok"] = mismatches.Count == 0, ["checked"] = linkTypesBefore.Count + deletedAll.Count, ["mismatches"] = mismatches };
                    return mismatches.Count == 0;
                }, ownTransactions: true);
            }
            catch (PlanChangedException ex) { return PlanChanged(ex.Message); }

            var succeeded = result["success"] is JsonValue sv && sv.TryGetValue<bool>(out var okv) && okv;
            lock (Previews)
            {
                if (dryRun && succeeded && pending != null) { if (Previews.Count > 20) Previews.Clear(); Previews[requestKey] = pending; }
                else if (!dryRun && succeeded) Previews.Remove(requestKey);   // a dry run is applied once
            }
            if (!dryRun && result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled && result["done"] is JsonObject dn)
            {
                result["wouldDo"] = dn.DeepClone();
                result["done"] = new JsonObject { ["deleted"] = 0, ["passes"] = 0 };
            }
            if (result["transaction"]?.ToString() == "VerifyFailed")
                result["note"] = "The self-check found a difference (see verify.mismatches): the whole purge was rolled back. Nothing was changed.";
            return result;
        }

        /// <summary>
        /// One delete attempt in its own transaction: the ids still present, ONE Document.Delete, committed. Null when done;
        /// else why not, with the transaction rolled back (a Revit link type among what Revit would delete counts as a refusal).
        /// </summary>
        private static string TryDelete(Document d, int pass, List<long> chunk, ISet<long> linkTypes, ISet<long> passGone)
        {
            using (var t = NativeChange.OwnTransaction(d, $"ACE: purge unused, pass {pass}"))
            {
                try
                {
                    var live = chunk.Select(i => new ElementId(i)).Where(i => d.GetElement(i) != null).ToList();
                    if (live.Count == 0) return null;   // already gone with an earlier delete of this pass
                    t.Start();
                    var gone = d.Delete(live) ?? new List<ElementId>();
                    var hit = gone.Select(g => g.Value).FirstOrDefault(linkTypes.Contains);
                    if (hit != 0) { t.RollBack(); return $"it would also delete Revit link type {hit}"; }
                    var status = t.Commit();
                    if (status != TransactionStatus.Committed) return $"Revit rolled the delete back ({status})";
                    foreach (var g in gone) passGone.Add(g.Value);
                    return null;
                }
                catch (Exception ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                }
            }
        }

        private static string SafePath(Document doc) { try { return doc.PathName; } catch { return null; } }

        private static string Label(Element e)
        {
            if (e == null) return "(unknown)";
            try
            {
                if (e is Family) return "Families";
                if (e.Category != null) return e.Category.Name;
                return e.GetType().Name;
            }
            catch { return "(unknown)"; }
        }

        private static string NameOf(Element e)
        {
            if (e == null) return null;
            try { return e is FamilySymbol fs ? $"{fs.FamilyName}: {fs.Name}" : e.Name; }
            catch { return null; }
        }
    }
}
