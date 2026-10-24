using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Rules.WorksetAdminLogic;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "workset_admin": { action: audit | facts | rename | delete_empty | apply_plan (WorksetPlan.cs), check_only, dry_run, expect_model, expect_version,
    /// expect_plan, max_ids, rules?, renames?: [{from, to}], worksets?: [names], move_to? }. audit reads only; rename and
    /// delete_empty run like clash_resolve: a plan with a fingerprint (planHash), a dry run and an identical apply as ONE undo
    /// step, and a self-check (element count per category the same before and after) that rolls the whole change back on a
    /// mismatch. Any block (protected, borrowed, not empty, name taken, closed) refuses the dry run and the apply.
    /// The naming rules live in the vault; the server sends the renames.
    /// </summary>
    internal static partial class WorksetAdmin
    {
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var action = (Args.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            var aceVersion = ParameterFill.AceVersion();
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));
            if (action == "audit") return Audit(doc, args, aceVersion, maxIds);
            if (action == "facts") return Facts(doc, args, aceVersion, maxIds);
            if (action == "apply_plan") return ApplyPlan(app, doc, args, aceVersion, maxIds);
            if (action != "rename" && action != "delete_empty") throw new CommandException("action must be audit, facts, rename, delete_empty or apply_plan.");

            var checkOnly = Args.Bool(args, "check_only");
            JsonObject Refuse(string stage, string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = stage, ["error"] = error, ["blocked"] = blocked ?? new JsonArray(),
                ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["action"] = action,
            };
            JsonObject OneBlock(string what, string code, string holder, string detail) => ToJson(new L.Block { What = what, Code = code, Holder = holder, Detail = detail }, maxIds);

            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
            {
                var detail = $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}";
                return Refuse("blocked", $"Nothing was changed: {detail}.", new JsonArray(OneBlock("the whole plan", "version", null, detail)));
            }
            var expectModel = Args.Str(args, "expect_model");
            if (expectModel != null && !string.Equals(expectModel, doc.Title, StringComparison.Ordinal))
            {
                var detail = $"the preview was made on {expectModel}, the active model is {doc.Title}";
                return Refuse("wrong_model", $"Nothing was changed: {detail}. Preview again on this model.", new JsonArray(OneBlock("the whole plan", "wrong-model", null, detail)));
            }
            if (!doc.IsWorkshared)
                return Refuse("blocked", "Nothing was changed: the model is not workshared.", new JsonArray(OneBlock("the model", "not-workshared", null, null)));
            if (doc.IsReadOnly || doc.IsFamilyDocument || doc.IsLinked)
                return Refuse("blocked", "Nothing was changed: the model cannot be edited.", new JsonArray(OneBlock("the model", "model", null, "a read-only, linked or family document")));

            // The plan.
            var worksets = Read(doc, maxIds, out var before, DatumName(doc, args));
            var me = app.Application.Username;
            var notes = new List<string>();
            List<L.Step> steps; List<L.Block> blocks;
            if (action == "rename")
            {
                if (args["renames"] is not JsonArray given) throw new CommandException("Give renames: [{from, to}] (the server builds them from the vault's naming rules).");
                var renames = given.OfType<JsonObject>().Select(o => (o["from"]?.ToString(), o["to"]?.ToString())).ToList();
                (steps, blocks) = L.PlanRename(worksets, renames, me);
            }
            else
            {
                var names = Args.Strings(args, "worksets");
                var (s, b, skipped) = L.PlanDelete(worksets, names, Args.Str(args, "move_to"), me);
                steps = s; blocks = b;
                if (skipped.Count > 0) notes.Add($"Empty worksets left alone (closed: open it to check; borrowed: ask the holder to relinquish it): {string.Join(", ", skipped)}.");
                // Moving elements: each one must be editable (not borrowed, not changed in central). One pass for all of them.
                var moving = steps.Where(x => x.Kind == "move-and-delete").ToList();
                var held = HeldElements(doc, new HashSet<long>(moving.Select(x => x.WorksetId)));
                foreach (var st in moving)
                {
                    var mine = held.Where(h => h.Workset == st.WorksetId).ToList();
                    if (mine.Count == 0) continue;
                    steps.Remove(st);
                    foreach (var g in mine.GroupBy(h => (h.Code, h.Holder)))
                        blocks.Add(new L.Block { What = $"{g.Count()} element{(g.Count() == 1 ? "" : "s")} on {st.Workset}", Code = g.Key.Code, Holder = g.Key.Holder, Elements = g.Count(), SampleIds = g.Select(h => h.Id).ToList() });
                }
            }
            foreach (var n in steps.Select(x => L.SpacesNote(x.Workset)).Where(n => n != null).Distinct()) notes.Add(n);
            var planHash = L.PlanHash(steps);
            var planJson = new JsonArray(steps.Select(st => (JsonNode)StepJson(st)).ToArray());
            var blockedJson = new JsonArray(blocks.Select(b => (JsonNode)ToJson(b, maxIds)).ToArray());
            var checkedOut = new JsonArray();
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["action"] = action; r["planHash"] = planHash;
                r["plan"] = planJson.DeepClone(); r["blocked"] = blockedJson.DeepClone(); r["checkedOut"] = checkedOut.DeepClone();
                r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                return r;
            }
            JsonObject Zero() => new JsonObject { ["renamed"] = 0, ["deleted"] = 0, ["moved"] = 0 };

            var expectPlan = Args.Str(args, "expect_plan");
            if (!checkOnly && !string.IsNullOrWhiteSpace(expectPlan) && !string.Equals(expectPlan.Trim(), planHash, StringComparison.OrdinalIgnoreCase))
            {
                const string changed = "the worksets changed since the preview: run the dry run again";
                var refused = Common(Refuse("blocked", $"Nothing was changed: {changed}.", null));
                refused["code"] = "plan-changed";
                ((JsonArray)refused["blocked"]).Insert(0, OneBlock("the whole plan", "plan-changed", null, changed));
                refused["expectedPlan"] = expectPlan.Trim();
                return refused;
            }
            if (checkOnly)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["checkOnly"] = true; r["done"] = Zero(); r["verify"] = null;
                r["note"] = blocks.Count > 0 ? "Nothing was changed. Some items are blocked (see blocked): the dry run and the apply refuse until they are cleared or left out of the request."
                    : steps.Count == 0 ? "Nothing was changed, and there is nothing to do." : "Nothing was changed. To apply: preview with dry_run: true, then confirm.";
                return r;
            }
            if (blocks.Count > 0)
                return Common(Refuse("blocked", $"Nothing was changed: {blocks.Count} item{(blocks.Count == 1 ? " is" : "s are")} blocked ({ToJson(blocks[0], 0)["why"]}). See blocked for what clears each one.", null));
            if (steps.Count == 0)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["done"] = Zero(); r["verify"] = null; r["rolledBack"] = false; r["note"] = "Nothing to do: nothing was changed.";
                return r;
            }

            // Worksets ACE changes must be editable by this user. Only the apply borrows them (outside any transaction; given
            // back at the next sync); the dry run previews the steps on worksets already editable and lists the rest.
            var dryRun = Args.Bool(args, "dry_run");
            string NameOf(long id) => worksets.First(w => w.Id == id).Name;
            var need = steps.SelectMany(st => st.Kind == "move-and-delete" ? new[] { st.WorksetId, st.MoveToId } : st.Kind == "delete" ? new[] { st.WorksetId, st.SafetyId } : new[] { st.WorksetId })
                .Distinct().Where(id => worksets.FirstOrDefault(w => w.Id == id)?.Editable != true).ToList();
            var notPreviewed = new HashSet<string>(StringComparer.Ordinal);   // dry run: steps on worksets this user would have to borrow
            if (need.Count > 0 && dryRun)
            {
                foreach (var st in steps.Where(st => need.Contains(st.WorksetId) || need.Contains(st.MoveToId) || need.Contains(st.SafetyId))) notPreviewed.Add(st.Id);
                notes.Add($"The apply would borrow {string.Join(", ", need.Select(NameOf))} (given back when you synchronize with central). The dry run did not borrow them, so it could not preview: {string.Join(", ", notPreviewed)}.");
            }
            else if (need.Count > 0)
            {
                var ownedBefore = worksets.Any(w => w.Editable);
                ICollection<WorksetId> got = null; string error = null;
                try { got = WorksharingUtils.CheckoutWorksets(doc, need.Select(id => new WorksetId((int)id)).ToList()); }
                catch (Exception ex) { error = ex.Message; }
                var missing = need.Where(id => got == null || !got.Any(g => g.IntegerValue == id)).ToList();
                foreach (var id in need.Except(missing)) checkedOut.Add(NameOf(id));
                if (missing.Count > 0)
                {
                    // Give back what this run borrowed. RelinquishOwnership gives back every user workset this user holds, so only
                    // when the user held none before this run; otherwise they stay borrowed until the next sync.
                    if (checkedOut.Count > 0)
                    {
                        if (!ownedBefore)
                            try { WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(false) { UserWorksets = true }, new TransactWithCentralOptions()); notes.Add("The worksets borrowed by this run were given back."); checkedOut.Clear(); }
                            catch (Exception ex) { notes.Add("The worksets borrowed by this run could not be given back (" + ex.Message + "): they are given back at your next sync."); }
                        else notes.Add("The worksets borrowed by this run stay borrowed until your next sync (you already held other worksets, which ACE does not give back for you).");
                    }
                    return Common(Refuse("blocked", $"Nothing was changed: {string.Join(", ", missing.Select(NameOf))} could not be borrowed" + (error == null ? "." : $" ({error})."),
                        new JsonArray(missing.Select(id => (JsonNode)OneBlock(NameOf(id), error == null ? "borrowed" : "central", null, error ?? "Revit did not give the workset to this user")).ToArray())));
                }
                notes.Add("ACE borrowed the worksets it changes (checkedOut); they are given back when you synchronize with central.");
            }
            foreach (var st in steps.Where(x => x.Kind != "rename" && !notPreviewed.Contains(x.Id)))
                if (!WorksetTable.CanDeleteWorkset(doc, new WorksetId((int)st.WorksetId), Settings(st)))
                    return Common(Refuse("blocked", $"Nothing was changed: Revit says {st.Workset} cannot be deleted now.",
                        new JsonArray(OneBlock("delete " + st.Workset, "borrowed", null, "Revit refuses to delete it now (the workset or some of its elements are owned by another user, or it is not editable)"))));

            // What each receiving workset must hold after the change: what it holds now plus what moves into it.
            var ran = new List<L.Step>();
            int OnWorkset(Document d, long id) => new FilteredElementCollector(d).WherePasses(new ElementWorksetFilter(new WorksetId((int)id), false)).GetElementCount();
            var title = action == "rename" ? "ACE: rename worksets" : "ACE: delete empty worksets";
            var result = NativeChange.Run(app, args, "workset_admin", title, (d, plan) =>
            {
                var r = Common(new JsonObject());
                var done = Zero();
                r["done"] = done; r["verify"] = null;
                if (plan) return r;
                ran.Clear();
                foreach (var st in steps)
                {
                    if (notPreviewed.Contains(st.Id)) continue;
                    var id = new WorksetId((int)st.WorksetId);
                    if (st.Kind == "rename") { WorksetTable.RenameWorkset(d, id, st.To); done["renamed"] = (int)done["renamed"] + 1; }
                    else
                    {
                        // Count again just before the delete: a change since the plan stops the whole run (rolled back).
                        var now = OnWorkset(d, st.WorksetId);
                        if (now != st.Elements)
                            throw new CommandException($"Nothing was changed: {st.Workset} holds {now} element{(now == 1 ? "" : "s")} now, the plan counted {st.Elements}. Run the dry run again.");
                        WorksetTable.DeleteWorkset(d, id, Settings(st));
                        done["deleted"] = (int)done["deleted"] + 1;
                        done["moved"] = (int)done["moved"] + st.Elements;
                    }
                    ran.Add(st);
                }
                if (notPreviewed.Count > 0) r["notPreviewed"] = new JsonArray(notPreviewed.Select(x => (JsonNode)x).ToArray());
                return r;
            }, verify: (d, r) =>
            {
                var after = CountByCategory(d);
                var mismatches = L.CompareCounts(before, after);
                var names = new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset).ToWorksets().ToDictionary(w => (long)w.Id.IntegerValue, w => w.Name);
                // Receiving worksets: exactly what they held plus what moved in (an "empty" delete moves nothing).
                foreach (var g in ran.Where(x => x.Kind != "rename").GroupBy(x => x.Kind == "delete" ? x.SafetyId : x.MoveToId))
                {
                    var expected = worksets.First(w => w.Id == g.Key).Elements + g.Sum(x => x.Elements);
                    var actual = OnWorkset(d, g.Key);
                    if (actual != expected) mismatches.Add(("elements on " + NameOf(g.Key), expected.ToString(), actual.ToString()));
                }
                foreach (var st in ran)
                {
                    names.TryGetValue(st.WorksetId, out var now);
                    if (st.Kind == "rename" && now != st.To) mismatches.Add(("name of " + st.From, st.To, now ?? "(gone)"));
                    if (st.Kind != "rename" && now != null) mismatches.Add(("workset " + st.Workset, "deleted", "still there"));
                }
                r["verify"] = new JsonObject
                {
                    ["ok"] = mismatches.Count == 0, ["checked"] = before.Count,
                    ["mismatches"] = new JsonArray(mismatches.Take(50).Select(m => (JsonNode)new JsonObject { ["what"] = m.What, ["before"] = m.Before, ["after"] = m.After }).ToArray()),
                };
                return mismatches.Count == 0;
            });
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled && result["done"] is JsonObject dn)
            {
                result["wouldDo"] = dn.DeepClone();
                result["done"] = Zero();
                if (result["transaction"]?.ToString() == "VerifyFailed")
                    result["note"] = "The self-check found a change beyond the worksets (see verify.mismatches): the whole change was rolled back. Nothing was changed.";
            }
            if (result["verify"] is JsonObject v && v["error"] != null && v["checked"] == null) v["checked"] = before.Count;
            return result;
        }

        // ---- audit ------------------------------------------------------------------------------------------------

        private static JsonObject Audit(Document doc, JsonObject args, string aceVersion, int maxIds)
        {
            var r = new JsonObject { ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["action"] = "audit", ["checkOnly"] = true, ["workshared"] = doc.IsWorkshared };
            if (!doc.IsWorkshared) { r["worksets"] = new JsonArray(); r["rules"] = null; r["note"] = "The model is not workshared: it has no worksets."; return r; }
            var worksets = Read(doc, maxIds, out _, DatumName(doc, args));

            // Elements on the wrong workset: the same rules engine as assign_worksets (check_only).
            Dictionary<long, int> wrong = null;
            WorksetRules rules = null;
            try
            {
                var names = WorksetRules.ModelNames(doc);   // a rule's models key (G9)
                rules = args["rules"] is JsonArray given ? WorksetRules.Parse(given, Args.Str(args, "default_workset"), names) : WorksetRules.Load(ProjectRules.For(doc), names);
                if (rules?.ModelsText(doc.Title) is string modelsLine) r["rulesSkipped"] = modelsLine;
                if (rules != null && rules.Items.Count == 0 && rules.DefaultWorkset == null) rules = null;
            }
            catch (Exception ex) { r["rulesError"] = ex.Message; rules = null; }
            if (rules != null)
            {
                wrong = worksets.ToDictionary(w => w.Id, w => 0);
                var byId = worksets.ToDictionary(w => w.Id, w => w.Name);
                var ctx = new WorksetRules.Context(doc, rules.NeedsZone);
                foreach (var e in WorksetRules.Candidates(doc))
                {
                    var t = rules.TargetFor(doc, e, ctx);
                    var current = (long)e.WorksetId.IntegerValue;
                    if (t == null || !byId.TryGetValue(current, out var name)) continue;
                    if (!string.Equals(name, t.Value.Workset, StringComparison.OrdinalIgnoreCase)) wrong[current]++;
                }
            }
            r["rules"] = rules == null ? null : $"{rules.Name ?? "rules"} ({rules.Source ?? "given in the request"})";
            r["worksets"] = new JsonArray(worksets.Select(w => (JsonNode)new JsonObject
            {
                ["id"] = w.Id, ["name"] = w.Name, ["elements"] = w.Elements, ["byCategory"] = Counts(w.ByCategory),
                ["owner"] = string.IsNullOrWhiteSpace(w.Owner) ? null : w.Owner, ["open"] = w.Open, ["editable"] = w.Editable, ["isDefault"] = w.IsDefault,
                ["protected"] = L.IsProtected(w), ["wrongWorkset"] = wrong == null ? null : (JsonNode)wrong[w.Id],
                ["sampleIds"] = new JsonArray(w.SampleIds.Take(maxIds).Select(i => (JsonNode)i).ToArray()),
            }).ToArray());
            if (worksets.Any(w => !w.Open)) r["note"] = "Closed worksets are listed with the elements Revit reports for them; open them for a reliable count.";
            return r;
        }

        // ---- reading ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Every user workset (Shared Levels and Grids included) with every element on it: view-specific, hidden, in groups and
        /// design options. One pass over the model, which also gives the element count per category (the self-check's measure).
        /// </summary>
        private static List<L.Workset> Read(Document doc, int maxIds, out Dictionary<string, int> totals, string datumName)
        {
            var list = new List<L.Workset>();
            var byId = new Dictionary<int, L.Workset>();
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            {
                var info = new L.Workset { Id = w.Id.IntegerValue, Name = w.Name, Owner = w.Owner, Open = w.IsOpen, Editable = w.IsEditable, IsDefault = w.IsDefaultWorkset };
                list.Add(info); byId[w.Id.IntegerValue] = info;
            }
            totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in AllElements(doc))
            {
                var cat = CategoryName(e);
                totals[cat] = totals.TryGetValue(cat, out var t) ? t + 1 : 1;
                int ws;
                try { ws = e.WorksetId.IntegerValue; } catch { continue; }
                if (!byId.TryGetValue(ws, out var info)) continue;
                info.Elements++;
                if (e is Level || e is Grid) info.LevelsAndGrids++;
                info.ByCategory[cat] = info.ByCategory.TryGetValue(cat, out var n) ? n + 1 : 1;
                if (info.SampleIds.Count < Math.Max(maxIds, 1)) info.SampleIds.Add(e.Id.Value);
            }
            L.MarkDatum(list, datumName);
            return list;
        }

        /// <summary>The datum workset's name from the request (datum_workset) or the vault (worksets.datumWorkset); null = find it.</summary>
        private static string DatumName(Document doc, JsonObject args)
        {
            var given = Args.Str(args, "datum_workset");
            if (!string.IsNullOrWhiteSpace(given)) return given;
            try { return ProjectRules.For(doc)?.Section("worksets")?["datumWorkset"]?.ToString(); } catch { return null; }
        }

        private static FilteredElementCollector AllElements(Document doc) =>
            new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true)));

        private static string CategoryName(Element e)
        {
            try { return e.Category?.Name ?? "(no category)"; } catch { return "(no category)"; }
        }

        /// <summary>Every model and annotation element (instances and types) by category: the self-check's measure.</summary>
        private static Dictionary<string, int> CountByCategory(Document doc)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in AllElements(doc))
            {
                string type, bic;
                try { type = e.Category?.CategoryType.ToString(); bic = e.Category?.BuiltInCategory.ToString(); } catch { type = null; bic = null; }
                // Model and annotation categories, plus views / sheets / schedules / viewports as their own count; never
                // Revit's side effects (new sketch planes, uncategorised items).
                var cat = L.CheckKey(type, e is SketchPlane, bic, CategoryName(e));
                if (cat == null) continue;
                counts[cat] = counts.TryGetValue(cat, out var n) ? n + 1 : 1;
            }
            return counts;
        }

        /// <summary>Elements on the given worksets that cannot be moved now (borrowed by another user, or changed in central): one pass.</summary>
        private static List<(long Workset, long Id, string Code, string Holder)> HeldElements(Document doc, HashSet<long> worksetIds)
        {
            var held = new List<(long, long, string, string)>();
            if (worksetIds.Count == 0) return held;
            foreach (var e in AllElements(doc))
            {
                long ws;
                try { ws = e.WorksetId.IntegerValue; } catch { continue; }
                if (!worksetIds.Contains(ws)) continue;
                try
                {
                    var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out var owner);
                    if (status == CheckoutStatus.OwnedByOtherUser) { held.Add((ws, e.Id.Value, "borrowed", string.IsNullOrWhiteSpace(owner) ? null : owner.Trim())); continue; }
                    var updates = WorksharingUtils.GetModelUpdatesStatus(doc, e.Id);
                    if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral) held.Add((ws, e.Id.Value, "central", null));
                }
                catch { /* not a worksharing element (internal): DeleteWorkset moves it with the rest */ }
            }
            return held;
        }

        private static DeleteWorksetSettings Settings(L.Step st) => st.Kind == "move-and-delete"
            ? new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, new WorksetId((int)st.MoveToId))
            // An empty workset: still "move to" (the default constructor deletes the elements), so anything the count missed
            // moves to the safety workset instead of being deleted, and the self-check reports it.
            : new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, new WorksetId((int)st.SafetyId));

        // ---- answers ----------------------------------------------------------------------------------------------

        private static JsonObject Counts(Dictionary<string, int> d) =>
            new JsonObject(d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));

        private static JsonObject StepJson(L.Step s)
        {
            var o = new JsonObject { ["id"] = s.Id, ["kind"] = s.Kind, ["workset"] = s.Workset };
            if (s.Kind == "rename") { o["from"] = s.From; o["to"] = s.To; }
            else { o["elements"] = s.Elements; o["byCategory"] = Counts(s.ByCategory ?? new Dictionary<string, int>()); }
            if (s.Kind == "move-and-delete") o["moveTo"] = s.MoveTo;
            if (s.Kind == "delete") o["safetyTarget"] = s.Safety;
            return o;
        }

        private static JsonObject ToJson(L.Block b, int maxIds)
        {
            var (why, how) = L.Explain(b.Code, b.Holder, b.Detail);
            return new JsonObject
            {
                ["what"] = b.What, ["why"] = why, ["elements"] = b.Elements,
                ["sampleIds"] = new JsonArray(b.SampleIds.Take(maxIds).Select(i => (JsonNode)i).ToArray()),
                ["holder"] = string.IsNullOrWhiteSpace(b.Holder) ? null : b.Holder.Trim(), ["how"] = how, ["code"] = b.Code,
            };
        }
    }
}
