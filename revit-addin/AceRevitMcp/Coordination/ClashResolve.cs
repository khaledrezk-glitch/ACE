using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// Bridge "clash_resolve": the structural self-clash solver. Runs a clash test on the active model only (the clash
    /// engine, Clashes.Run), sorts each clash into a fix that moves and replaces nothing (join or switch-join for concrete
    /// with concrete, a physical cutback or a coping for a steel member end running into a column or a supporting beam) or
    /// report only, checks the blockers first, then applies the plan as a native change (check_only / dry_run / apply,
    /// ONE undo step) with the clash test re-run inside the transaction and a guard before it is kept: locations, types,
    /// tags, dimensions and Revit warnings of every touched element must be unchanged, or the whole change rolls back.
    /// { test, a_categories, b_categories, tolerance_mm, level, keys, order, steel: { cutback, coping }, block_on_borrowed,
    /// scope (clashing | all-touching), selection, same_category (default true), max_pairs (default 500), chunk_size (default
    /// max_pairs), chunk (1-based), flips: [plan ids of wrong-order joins confirmed to switch], exclude_ids: [element ids whose
    /// steps are not planned (report-only "excluded")], drop_failing_steps (default true: a run that Revit's commit refuses, or
    /// whose guard fails only on new warnings, drops the steps behind the failure, by its element ids or by halving in trial
    /// runs, and runs again, at most 3 rounds; the dry run's drops are reused by its apply, see droppedSteps),
    /// trim_to_face: { allowed, max_mm } | true, check_only, dry_run, expect_model, expect_version, expect_plan, max_ids,
    /// max_elements, test_force_verify_mismatch }
    /// </summary>
    internal static class ClashResolve
    {
        private const string Title = "ACE: resolve structural clashes";

        /// <summary>The default test: the structural categories against themselves.</summary>
        private static readonly string[] StVsSt =
        {
            "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_StructuralFraming", "OST_Floors", "OST_Stairs", "OST_Ramps",
        };

        /// <summary>One planned change.</summary>
        private sealed class Step
        {
            public string Id, Kind, ClashKey;
            public long A, B;            // join: A cuts (when HasWinner); cutback / cope: A is the member, B the column or beam
            public bool HasWinner;
            public int End = -1;
            public double Cut;           // feet
            public double DepthMm;       // the clash depth before the fix
            public string Method;        // join | switch-join | extension | join-cutback | coping (null: a cutback with nothing writable)
            public bool Blocked;
            public bool Touching;        // scope all-touching: a join of a pair that touches but does not clash (no depth check)
            public Step Fallback;        // a join's trim to face: run only when the join does not clear the clash
            public double[] NewEnd;      // trim: the new end point (feet, internal coordinates)
            public double LengthBefore;  // trim: the location line's length before (feet)
            public string Face;          // trim: "<category> <id> <face>"
            public bool Excluded;        // left out after a run showed "joined but do not intersect" for it (dropped as not touching)
            public string Dropped;       // drop_failing_steps: left out, the warning or error behind it (null: runs)
        }

        /// <summary>
        /// drop_failing_steps: the steps a successful dry run dropped, by model and plan hash, so the apply of the same plan
        /// drops exactly the same steps (without trying again) and keeps the step the preview showed.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, (string Why, JsonArray Failures)>> DroppedByPreview =
            new Dictionary<string, Dictionary<string, (string, JsonArray)>>(StringComparer.Ordinal);

        /// <summary>Most trial runs drop_failing_steps spends finding the failing steps by halving (each re-runs the clash test).</summary>
        private const int MaxBisectTrials = 24;

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var aceVersion = global::AceRevitMcp.Rules.ParameterFill.AceVersion();
            var checkOnly = Args.Bool(args, "check_only");
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));
            JsonObject Refuse(string stage, string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = stage, ["error"] = error, ["blocked"] = blocked ?? new JsonArray(),
                ["model"] = doc.Title, ["aceVersion"] = aceVersion,
            };

            // Fail fast, before anything is read: the version and the model.
            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !global::AceRevitMcp.Rules.ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
            {
                var detail = $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}";
                return Refuse("blocked", $"Nothing was changed: {detail}.", new JsonArray(ClashResolveLogic.Block("the whole plan", "version", null, detail, 0, null)));
            }
            var expectModel = Args.Str(args, "expect_model");
            if (expectModel != null && !string.Equals(expectModel, doc.Title, StringComparison.Ordinal))
            {
                var detail = $"the preview was made on {expectModel}, the active model is {doc.Title}";
                return Refuse("wrong_model", $"Nothing was changed: {detail}. Preview again on this model.", new JsonArray(ClashResolveLogic.Block("the whole plan", "wrong-model", null, detail, 0, null)));
            }
            var modelBlocks = new JsonArray();
            if (doc.IsFamilyDocument) modelBlocks.Add(ClashResolveLogic.Block("the model", "model", null, "a family document: clash_resolve works on a project model", 0, null));
            else if (doc.IsReadOnly) modelBlocks.Add(ClashResolveLogic.Block("the model", "model", null, "the model was opened from a read-only file: the fix could not be saved", 0, null));

            var order = Order(args);
            var steel = args["steel"] as JsonObject;
            var opts = new ClashResolveLogic.Options
            {
                Order = order,
                Cutback = steel == null || Args.Bool(steel, "cutback", true),
                Coping = steel == null || Args.Bool(steel, "coping", true),
            };
            var test = PickTest(args);
            var level = Args.Str(args, "level");
            var maxElements = Args.Int(args, "max_elements", 5000);
            var notes = new List<string>();
            var host = new List<Source> { new Source { Doc = doc, Name = doc.Title, Discipline = BriefCommands.Discipline(doc), Relation = "this model" } };

            // Before: the clash test on this model only (links are never changed).
            var geometry = new Clashes.SolidCache();
            var filtered = new Clashes.FilterReport();
            var found = Clashes.Run(doc, host, geometry, test, level, maxElements, notes, out var complete, filtered: filtered);
            if (!complete) notes.Add("The clash test was cut short (max_elements or cancelled): the plan covers the clashes found.");
            var beforeIssues = ClashLogic.Issues(found);
            var before = new JsonObject { ["clashes"] = found.Count, ["issues"] = beforeIssues.Count, ["byPair"] = ByPair(found) };
            var beforeKeys = new HashSet<string>(found.Select(c => c.Key), StringComparer.Ordinal);

            var keys = new HashSet<string>(Args.Strings(args, "keys"), StringComparer.Ordinal);
            var targeted = keys.Count == 0 ? found : found.Where(c => keys.Contains(c.Key) || (c.Group != null && keys.Contains(c.Group))).ToList();
            var keysNotFound = keys.Where(k => !found.Any(c => c.Key == k || c.Group == k)).ToList();
            if (keysNotFound.Count > 0) notes.Add($"{keysNotFound.Count} of the keys given were not found in this run (fixed already, another test name, or another level).");

            // The plan: one decision per clash.
            var steps = new List<Step>();
            var planJson = new JsonArray();
            var reportOnly = new JsonArray();
            var reportWhy = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in targeted)
            {
                var ea = doc.GetElement(c.UA); var eb = doc.GetElement(c.UB);
                if (ea == null || eb == null)
                {
                    reportOnly.Add(ReportItem(c, c.IdA, c.IdB, c.CatA, c.CatB, "missing", "an element was not found in this model", "Run the clash test again."));
                    reportWhy[c.Key] = "an element was not found in this model";
                    continue;
                }
                var d = Decide(doc, c, ea, eb, geometry, opts);
                if (d.Kind == null)
                {
                    reportOnly.Add(ReportItem(c, ea.Id.Value, eb.Id.Value, ea.Category?.Name, eb.Category?.Name, d.Code, d.Why, d.Recommendation));
                    reportWhy[c.Key] = d.Why;
                    continue;
                }
                var (a, b) = d.Changed == "B" ? (eb, ea) : (ea, eb);
                var trim = d.Kind == "cutback" || d.Kind == "cope";
                // The id shows the direction: the cutter, then the element that is cut (a join: A cuts B; a trim: B cuts the member A).
                var id = ClashResolveLogic.PlanId(d.Kind, trim ? b.Id.Value : a.Id.Value, trim ? a.Id.Value : b.Id.Value, d.End);
                ids[id] = ids.TryGetValue(id, out var seenN) ? seenN + 1 : 1;
                if (ids[id] > 1) id += "#" + ids[id];
                var step = new Step
                {
                    Id = id, Kind = d.Kind, ClashKey = c.Key, DepthMm = c.DepthMm,
                    A = a.Id.Value, B = b.Id.Value, HasWinner = d.Changed != null, End = d.End, Cut = d.Cut,
                    Method = d.Kind == "cutback" ? CutbackMethod(a as FamilyInstance, d.End) : d.Kind == "cope" ? "coping" : d.Kind,
                };
                steps.Add(step);
                var item = new JsonObject
                {
                    ["id"] = step.Id, ["kind"] = step.Kind, ["a"] = step.A, ["b"] = step.B,
                    ["aCategory"] = a.Category?.Name, ["bCategory"] = b.Category?.Name, ["material"] = d.Material, ["level"] = c.Level, ["why"] = d.Why,
                    ["clashKey"] = c.Key, ["depthMm"] = c.DepthMm,
                };
                item["method"] = step.Method;
                // Who cuts whom, by element: a join's winner (from the categories, never the clash's A/B), a trim's support.
                var (cutter, cut) = trim ? (b, a) : step.HasWinner ? (a, b) : (null, null);
                item["cutter"] = cutter?.Id.Value; item["cut"] = cut?.Id.Value;
                item["cutterCategory"] = cutter?.Category?.Name; item["cutCategory"] = cut?.Category?.Name;
                if (d.Kind == "cutback" || d.Kind == "cope") { item["end"] = d.End; item["cutMm"] = Math.Round(Lengths.Mm(d.Cut), 1); }
                planJson.Add(item);
            }

            // R3 trim to face: a concrete beam or wall whose end runs into the element that cuts it, when the join alone does not
            // clear the clash. Planned now as the join's fallback (so the plan is the same for the dry run and the apply).
            var trimArgs = args["trim_to_face"] as JsonObject;
            var trimOn = trimArgs != null && Args.Bool(trimArgs, "allowed", true) || (args["trim_to_face"] is JsonValue tv && tv.TryGetValue<bool>(out var tb) && tb);
            var trimMaxMm = trimArgs?["max_mm"] is JsonValue mv && mv.TryGetValue<double>(out var mm) && mm > 0 ? Math.Min(mm, ClashResolveLogic.TrimCeilingMm) : ClashResolveLogic.DefaultTrimMaxMm;
            var blockCache = new Dictionary<long, (string Code, string Holder, string Detail)?>();
            (string Code, string Holder, string Detail)? Held(long id) =>
                blockCache.TryGetValue(id, out var bc) ? bc : (blockCache[id] = BlockOf(doc, doc.GetElement(new ElementId(id))));
            if (trimOn)
            {
                var cands = new List<(Step Join, Step Trim, Element Member, Element Cutter)>();
                foreach (var st in steps.Where(x => (x.Kind == "join" || x.Kind == "switch-join") && x.HasWinner).ToList())
                {
                    if (Held(st.A) != null || Held(st.B) != null) continue;   // a blocked join gets no trim either
                    var cutterEl = doc.GetElement(new ElementId(st.A)); var member = doc.GetElement(new ElementId(st.B));
                    var tr = TrimFor(st, cutterEl, member, geometry, Lengths.Ft(trimMaxMm), trimMaxMm, out var skip);
                    if (tr != null) cands.Add((st, tr, member, cutterEl));
                    else if (skip != null)
                        reportOnly.Add(ReportItem(targeted.First(x => x.Key == st.ClashKey), member.Id.Value, cutterEl.Id.Value, member.Category?.Name, cutterEl.Category?.Name,
                            skip.Value.Code, skip.Value.Why, skip.Value.Recommendation));
                }
                // At most one trim per member end: the smallest is kept (it is the face the end enters first).
                var keepTrims = new HashSet<int>(ClashResolveLogic.OneTrimPerEnd(cands.Select(x => (x.Trim.A, x.Trim.End, x.Trim.Cut)).ToList()));
                for (var i = 0; i < cands.Count; i++)
                {
                    var (st, tr, member, cutterEl) = cands[i];
                    if (keepTrims.Contains(i)) { st.Fallback = tr; planJson.Add(TrimJson(tr, member, cutterEl)); continue; }
                    reportOnly.Add(ReportItem(targeted.First(x => x.Key == st.ClashKey), member.Id.Value, cutterEl.Id.Value, member.Category?.Name, cutterEl.Category?.Name,
                        "one-trim-per-end", $"one trim per member end: a smaller trim of this {(tr.End == 0 ? "start" : "end")} is planned, this one ({Math.Round(Lengths.Mm(tr.Cut))} mm) is not",
                        "The join stays planned; run again after the fix if the clash remains."));
                }
            }

            // R2 scope all-touching: every touching pair of concrete elements in the priority categories gets the join order,
            // not only the clashing pairs (joins only; a pair Revit cannot join is dropped quietly as not touching).
            var scope = (Args.Str(args, "scope") ?? "clashing").Trim().ToLowerInvariant();
            if (scope != "clashing" && scope != "all-touching") throw new CommandException("scope must be clashing or all-touching.");
            JsonObject touching = null;
            if (scope == "all-touching" && keys.Count > 0)
                notes.Add("scope all-touching applies only without keys: only the clashes named by keys are planned.");
            else if (scope == "all-touching")
            {
                var clashPairs = new HashSet<(long, long)>(found.Select(c => (Math.Min(c.IdA, c.IdB), Math.Max(c.IdA, c.IdB))));
                ICollection<ElementId> selection = null;
                if (Args.Bool(args, "selection")) selection = app.ActiveUIDocument?.Selection?.GetElementIds() ?? new List<ElementId>();
                var maxPairs = Math.Max(1, Args.Int(args, "max_pairs", ClashResolveLogic.DefaultMaxPairs));
                var chunkSize = Math.Max(1, Args.Int(args, "chunk_size", maxPairs));
                var flips = new HashSet<string>(Args.Strings(args, "flips").Select(f => f.Trim()), StringComparer.Ordinal);
                touching = Touching(doc, order, selection, clashPairs, geometry, steps, planJson, reportOnly, ids, test, level, Args.Bool(args, "same_category", true),
                    chunkSize, Args.Int(args, "chunk", 0), flips);
            }

            // exclude_ids: no step that changes one of these elements is planned; each is listed as report-only ("excluded").
            var excludeIds = new HashSet<long>(Args.Ids(args, "exclude_ids").Select(i => i.Value));
            if (excludeIds.Count > 0)
            {
                var gone = steps.Where(s => ClashResolveLogic.TouchesExcluded(s.A, s.B, excludeIds) || (s.Fallback != null && ClashResolveLogic.TouchesExcluded(s.Fallback.A, s.Fallback.B, excludeIds))).ToList();
                var goneIds = new HashSet<string>(gone.SelectMany(s => s.Fallback == null ? new[] { s.Id } : new[] { s.Id, s.Fallback.Id }), StringComparer.Ordinal);
                foreach (var s in gone)
                {
                    steps.Remove(s);
                    var which = new[] { s.A, s.B }.Where(excludeIds.Contains).ToList();
                    if (which.Count == 0 && s.Fallback != null) which = new[] { s.Fallback.A, s.Fallback.B }.Where(excludeIds.Contains).ToList();
                    var item = StepReport(doc, s, "excluded", $"excluded: element {string.Join(", ", which)} is in exclude_ids", "Fix it by hand, or run again without it in exclude_ids.");
                    item["level"] = planJson.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.ToString() == s.Id)?["level"]?.DeepClone();
                    var clash = found.FirstOrDefault(c => c.Key == s.ClashKey);
                    if (clash != null) { item["level"] = clash.Level; item["depthMm"] = clash.DepthMm; reportWhy[s.ClashKey] = item["why"]?.ToString(); }
                    reportOnly.Add(item);
                }
                for (var i = planJson.Count - 1; i >= 0; i--)
                    if (planJson[i]?["id"]?.ToString() is string pid && goneIds.Contains(pid)) planJson.RemoveAt(i);
                notes.Add($"exclude_ids: {gone.Count} step{(gone.Count == 1 ? "" : "s")} changing {string.Join(", ", excludeIds.OrderBy(x => x))} not planned (report only, \"excluded\").");
            }

            // The plan's fingerprint: an apply (or the Apply card, which replays the same args) must find the plan it previewed.
            var planHash = ClashResolveLogic.PlanHash(steps.SelectMany(s => s.Fallback == null ? new[] { s } : new[] { s, s.Fallback }).Select(s =>
            {
                if (s.Kind == "trim") return ("trim", s.B, s.A, s.End, Lengths.Mm(s.Cut), 0L);
                var trim = s.Kind == "cutback" || s.Kind == "cope";
                return (s.Kind + "/" + s.Method + (s.Touching ? "/touching" : ""), trim ? s.B : s.A, trim ? s.A : s.B, s.End, trim ? Lengths.Mm(s.Cut) : 0.0, s.Kind == "cope" ? s.B : 0L);
            }));
            var expectPlan = Args.Str(args, "expect_plan");

            // Pre-flight: every element a step changes must be editable now (joins change both, a cutback or cope the member).
            var held = new List<(long Id, string Code, string Holder, string Detail)>();
            var heldIds = new Dictionary<long, string>();
            foreach (var id in steps.SelectMany(s => s.Kind == "join" || s.Kind == "switch-join" ? new[] { s.A, s.B } : new[] { s.A }).Distinct())
            {
                var block = Held(id);
                if (block == null) continue;
                held.Add((id, block.Value.Code, block.Value.Holder, block.Value.Detail));
                heldIds[id] = ClashResolveLogic.Explain(block.Value.Code, block.Value.Holder, block.Value.Detail).Why;
            }
            var blocked = ClashResolveLogic.Blocks(held, maxIds);
            foreach (var m in modelBlocks) blocked.Insert(0, m?.DeepClone());
            var droppedSteps = new JsonArray();   // drop_failing_steps: the steps left out for a warning or a commit error
            JsonObject Common(JsonObject r)
            {
                r["droppedSteps"] = droppedSteps.DeepClone();
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["test"] = test.Name; r["planHash"] = planHash;
                r["order"] = new JsonArray(order.Select(x => (JsonNode)x).ToArray());
                r["storeKey"] = Clashes.StoreKey(doc);
                r["orderSource"] = Args.Strings(args, "order").Count == 0 ? "default" : (Args.Str(args, "order_source") ?? "request");
                r["scope"] = scope; r["scopeSource"] = Args.Str(args, "scope_source") ?? (args["scope"] != null ? "request" : "default");
                r["touching"] = touching?.DeepClone();
                r["trimToFace"] = trimOn ? new JsonObject { ["allowed"] = true, ["maxMm"] = trimMaxMm } : null;
                r["before"] = before.DeepClone(); r["plan"] = planJson.DeepClone(); r["reportOnly"] = reportOnly.DeepClone(); r["blocked"] = blocked.DeepClone();
                r["reportGroups"] = ReportGroups(reportOnly);
                r["filtered"] = ClashCommands.Counts(filtered);   // { material: count } left out by the material filter ({} without one)
                if (test.A.FiltersMaterial || test.B.FiltersMaterial) r["materials"] = ClashCommands.MaterialFilter(test);
                r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                if (keysNotFound.Count > 0) r["keysNotFound"] = new JsonArray(keysNotFound.Take(Math.Max(1, maxIds)).Select(k => (JsonNode)k).ToArray());
                return r;
            }
            if (!checkOnly && !string.IsNullOrWhiteSpace(expectPlan) && !string.Equals(expectPlan.Trim(), planHash, StringComparison.OrdinalIgnoreCase))
            {
                const string changed = "the model changed since the preview: run the dry run again";
                var refused = Common(Refuse("blocked", $"Nothing was changed: {changed}.", null));
                ((JsonArray)refused["blocked"]).Insert(0, ClashResolveLogic.Block("the whole plan", "plan-changed", null, changed, steps.Count, null));
                refused["expectedPlan"] = expectPlan.Trim();
                return refused;
            }
            if (!checkOnly && modelBlocks.Count > 0)
                return Common(Refuse("blocked", $"Nothing was changed: {modelBlocks[0]?["why"]}.", null));
            if (!checkOnly && held.Count > 0 && Args.Bool(args, "block_on_borrowed", true))
                return Common(Refuse("blocked", $"Nothing was changed: {heldIds.Count} element{(heldIds.Count == 1 ? "" : "s")} the fix would change cannot be edited now ({blocked[0]?["why"]}). "
                    + "See blocked for who holds them and what clears it, or run with block_on_borrowed: false to fix the rest.", null));
            var preSkipped = new JsonArray();
            foreach (var s in steps)
            {
                var stop = new[] { s.A, s.B }.Where(heldIds.ContainsKey).Select(i => heldIds[i]).FirstOrDefault();
                if (stop == null || !(s.Kind == "join" || s.Kind == "switch-join" || heldIds.ContainsKey(s.A))) continue;
                s.Blocked = true;
                preSkipped.Add(SkipItem(s, "blocked: " + stop));
            }

            // Guard: the state of every touched element before the change (outside any transaction).
            var guardIds = steps.Where(s => !s.Blocked).SelectMany(s => new[] { s.A, s.B }).Distinct().ToList();
            // The fix-made-it-worse rule watches the clashing pairs' elements only (all-touching joins are not clash fixes).
            var touched = new HashSet<long>(steps.Where(s => !s.Blocked && !s.Touching).SelectMany(s => new[] { s.A, s.B }));
            var relocated = new Dictionary<long, List<Step>>();                                 // members trimmed to a face (R3), by member
            // Walls that may be trimmed: the elements they host are counted before and after (a trim must not lose one).
            var hostedFor = new HashSet<long>(steps.Where(s => !s.Blocked && s.Fallback != null && doc.GetElement(new ElementId(s.Fallback.A)) is Wall).Select(s => s.Fallback.A));
            var beforeStates = checkOnly ? new List<ClashResolveLogic.ElementState>() : Snapshot(doc, guardIds, hostedFor).Values.ToList();
            var existingWarnings = Warnings(doc);
            var quantitiesBefore = checkOnly ? null : Quantities(doc, guardIds);
            var retryDrop = new HashSet<string>(StringComparer.Ordinal);   // join steps a run showed "joined but do not intersect" for
            var forceMismatch = Args.Bool(args, "test_force_verify_mismatch") && !Args.Bool(args, "dry_run");
            var worse = new List<(long Element, string What, string Before, string After)>();   // set by the body's after-run
            var trimmed = new HashSet<long>();                                                  // members cut back or coped

            var dropFailing = Args.Bool(args, "drop_failing_steps", true);
            var dryRunMode = Args.Bool(args, "dry_run");
            var trialOff = new HashSet<string>(StringComparer.Ordinal);   // drop_failing_steps: steps left out of one trial run
            var lastOthersOk = true; var lastWarnOk = true;               // set by the guard of the last run
            var lastFresh = new List<(string Text, long[] Ids)>();
            bool Runs(Step s) => !s.Blocked && s.Dropped == null && !trialOff.Contains(s.Id);

            Func<Document, bool, JsonObject> body = (d, plan) =>
            {
                var r = Common(new JsonObject());
                var done = new JsonObject { ["join"] = 0, ["switchJoin"] = 0, ["cutback"] = 0, ["cope"] = 0, ["trim"] = 0 };
                var dropped = new JsonObject { ["join"] = 0, ["switchJoin"] = 0, ["cutback"] = 0, ["cope"] = 0, ["trim"] = 0, ["notTouching"] = 0, ["failing"] = 0 };
                var skipped = (JsonArray)preSkipped.DeepClone();
                r["done"] = done; r["dropped"] = dropped; r["skipped"] = skipped;
                r["warnings"] = new JsonObject { ["before"] = existingWarnings.Count, ["after"] = null, ["new"] = new JsonArray() };
                r["after"] = null; r["guard"] = null;
                if (plan) return r;

                worse.Clear(); trimmed.Clear(); relocated.Clear();
                var ran = new Dictionary<string, string>(StringComparer.Ordinal);   // clash key -> kind done, or the skip reason
                Execute(d, steps.Where(Runs).ToList(), done, dropped, skipped, ran, test.ToleranceMm, relocated);
                foreach (var id in relocated.Keys) trimmed.Add(id);
                foreach (var s in steps.Where(s => s.Blocked)) ran[s.ClashKey] = "skipped: blocked";
                foreach (var s in steps.Where(s => !s.Blocked && s.Dropped != null))
                {
                    skipped.Add(SkipItem(s, "dropped: " + s.Dropped)); ran[s.ClashKey] = "skipped: dropped (" + s.Dropped + ")";
                    dropped["failing"] = dropped["failing"].GetValue<int>() + 1;
                }
                foreach (var s in steps.Where(s => !s.Blocked && s.Dropped == null && trialOff.Contains(s.Id))) ran[s.ClashKey] = "skipped: left out of this trial run";
                foreach (var s in steps.Where(s => !s.Blocked && (s.Kind == "cutback" || s.Kind == "cope") && ran.TryGetValue(s.ClashKey, out var k) && k == s.Kind)) trimmed.Add(s.A);
                d.Regenerate();

                // After: the same test again, inside the transaction, on fresh geometry.
                var afterNotes = new List<string>();
                var now = Clashes.Run(d, host, new Clashes.SolidCache(), test, level, maxElements, afterNotes, out _);
                var remaining = new JsonArray();
                foreach (var c in now)
                {
                    string why;
                    if (ran.TryGetValue(c.Key, out var what))
                    {
                        var st = steps.FirstOrDefault(x => x.ClashKey == c.Key);
                        why = what.StartsWith("skipped") ? what
                            : st != null && (what == "cutback" || what == "cope") && c.DepthMm < st.DepthMm ? $"still overlaps after the {what} (depth {st.DepthMm} -> {c.DepthMm} mm)"
                            : st != null && (what == "cutback" || what == "cope") ? $"the {what} did not reduce the overlap ({st.DepthMm} -> {c.DepthMm} mm)"
                            : $"still overlaps after the {what}";
                    }
                    else if (reportWhy.TryGetValue(c.Key, out var rw)) why = "report only: " + rw;
                    else if (!beforeKeys.Contains(c.Key)) why = "new: appeared after the fix";
                    else why = "not in the requested keys";
                    remaining.Add(new JsonObject { ["a"] = c.IdA, ["b"] = c.IdB, ["why"] = why, ["key"] = c.Key, ["aCategory"] = c.CatA, ["bCategory"] = c.CatB, ["level"] = c.Level });
                }
                var nowByKey = now.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().DepthMm, StringComparer.Ordinal);
                worse.AddRange(ClashResolveLogic.Worse(found.Count, now.Count, now.Select(c => (c.Key, c.IdA, c.IdB)).ToList(), beforeKeys, touched,
                    steps.Where(s => trimmed.Contains(s.A) && ran.TryGetValue(s.ClashKey, out var k) && k == s.Kind)
                         .Select(s => (s.ClashKey, s.A, s.DepthMm, nowByKey.TryGetValue(s.ClashKey, out var dep) ? dep : (double?)null)).ToList()));
                r["after"] = new JsonObject { ["clashes"] = now.Count, ["issues"] = ClashLogic.Issues(now).Count, ["byPair"] = ByPair(now), ["remaining"] = remaining };
                return r;
            };
            Func<Document, JsonObject, IReadOnlyList<(string Text, long[] Ids)>, bool> verify = (d, r, posted) =>
            {
                var after = Snapshot(d, guardIds, hostedFor);
                var (mismatches, changedValues) = ClashResolveLogic.Compare(beforeStates, after, trimmed, relocated.Keys);
                // A member trimmed to a face: every trimmed end at its planned point, every other end unchanged (all trims together).
                foreach (var kv in relocated)
                {
                    var b0 = beforeStates.FirstOrDefault(x => x.Id == kv.Key)?.Location; after.TryGetValue(kv.Key, out var a0);
                    var a1 = a0?.Location;
                    if (b0 == null || a1 == null || b0.Length != 6 || a1.Length != 6) { mismatches.Add((kv.Key, "location", "a line", "not a line")); continue; }
                    if (!ClashResolveLogic.TrimmedEndsOk(b0, a1, kv.Value.Select(t => (t.End, t.NewEnd))))
                        mismatches.Add((kv.Key, "location (trim)", "only the trimmed ends move, each to its face", $"start {a1[0]:0.###},{a1[1]:0.###},{a1[2]:0.###} end {a1[3]:0.###},{a1[4]:0.###},{a1[5]:0.###}"));
                }
                mismatches.AddRange(worse);
                var othersOk = mismatches.Count == 0;
                var (warnAfter, fresh, warnOk) = ClashResolveLogic.WarningsWithIds(existingWarnings, Warnings(d), posted.ToList(), out var freshItems);
                // Each new warning with the plan items behind it (the steps whose elements it names).
                lastOthersOk = othersOk; lastWarnOk = warnOk; lastFresh = freshItems;
                var ranSteps = steps.Where(x => Runs(x) && !x.Excluded).SelectMany(x => x.Fallback == null ? new[] { x } : new[] { x, x.Fallback }).Select(x => (x.Id, x.A, x.B)).ToList();
                var byItem = freshItems.Select(w => (w.Text, w.Ids, Items: ClashResolveLogic.PlanItemsFor(w.Ids, ranSteps), Near: ClashResolveLogic.PlanItemsNear(w.Ids, ranSteps))).ToList();
                r["warnings"] = new JsonObject
                {
                    ["before"] = existingWarnings.Count, ["after"] = warnAfter, ["new"] = new JsonArray(fresh.Select(w => (JsonNode)w).ToArray()),
                    ["byPlanItem"] = new JsonArray(byItem.Select(w => (JsonNode)new JsonObject
                    {
                        ["warning"] = w.Text, ["elements"] = new JsonArray(w.Ids.Select(i => (JsonNode)i).ToArray()),
                        ["planItems"] = new JsonArray(w.Items.Select(i => (JsonNode)i).ToArray()),
                        // No step joins exactly these elements: the warning is reported against them, with the steps that change them.
                        ["nearPlanItems"] = w.Items.Count == 0 ? new JsonArray(w.Near.Select(i => (JsonNode)i).ToArray()) : null,
                    }).ToArray()),
                };
                // Only "joined but do not intersect" on join steps: those joins are left out and the change runs again.
                var joinIds = new HashSet<string>(steps.Where(x => x.Kind == "join" || x.Kind == "switch-join").Select(x => x.Id), StringComparer.Ordinal);
                if (othersOk && !warnOk && byItem.All(w => ClashResolveLogic.IsNotIntersecting(w.Text) && w.Items.Count > 0 && w.Items.All(joinIds.Contains)))
                    foreach (var w in byItem) foreach (var i in w.Items) retryDrop.Add(i);
                if (quantitiesBefore != null) r["quantities"] = QuantitiesJson(quantitiesBefore, Quantities(d, guardIds));
                if (!warnOk)
                    mismatches.Add((0, "warnings", existingWarnings.Count.ToString(), fresh.Count > 0 ? $"{warnAfter}: {string.Join("; ", fresh.Take(5))}" : warnAfter.ToString()));
                if (forceMismatch) mismatches.Add((0, "forced", null, "forced by test_force_verify_mismatch (acceptance test)"));
                r["guard"] = new JsonObject
                {
                    ["ok"] = mismatches.Count == 0, ["checked"] = guardIds.Count,
                    ["mismatches"] = new JsonArray(mismatches.Take(50).Select(m => (JsonNode)new JsonObject
                    {
                        ["element"] = m.Element == 0 ? null : (JsonNode)m.Element, ["what"] = m.What, ["before"] = m.Before, ["after"] = m.After,
                    }).ToArray()),
                    ["changedValues"] = new JsonArray(changedValues.Take(50).Select(v => (JsonNode)new JsonObject
                    {
                        ["element"] = v.Element, ["dimension"] = v.Dimension, ["before"] = v.Before, ["after"] = v.After,
                    }).ToArray()),
                };
                return mismatches.Count == 0;
            };
            JsonObject RunOnce(bool trial)
            {
                retryDrop.Clear(); lastOthersOk = true; lastWarnOk = true; lastFresh = new List<(string Text, long[] Ids)>();
                return NativeChange.Run(app, args, "clash_resolve", Title, body, verifyWithWarnings: verify, trial: trial);
            }
            // The failures of a run that drop_failing_steps can act on: Revit errors that rolled the commit back, or new warnings
            // that alone failed the guard (every other check passed). Null: none (the run passed, or failed for another reason).
            List<(string Text, long[] Ids)> StepFailures(JsonObject res)
            {
                if (res["guard"] == null && res["revitErrors"] is JsonArray errs && errs.Count > 0)
                    return errs.Select(e =>
                    {
                        var t = e?.ToString() ?? "";
                        var cut = t.LastIndexOf(" (elements:", StringComparison.Ordinal);
                        return (cut > 0 ? t.Substring(0, cut) : t, ClashResolveLogic.IdsInText(t));
                    }).ToList();
                if (res["transaction"]?.ToString() == "VerifyFailed" && lastOthersOk && !lastWarnOk && lastFresh.Count > 0) return lastFresh.ToList();
                return null;
            }
            JsonArray FailuresJson(IEnumerable<(string Text, long[] Ids)> f) => new JsonArray(f.Select(x => (JsonNode)new JsonObject
            {
                ["message"] = x.Text, ["elements"] = new JsonArray((x.Ids ?? new long[0]).Select(i => (JsonNode)i).ToArray()),
            }).ToArray());
            void Drop(Step s, string why, JsonArray failures, string foundBy, bool differs)
            {
                s.Dropped = why;
                var item = StepReport(doc, s, "dropped-failing", "dropped: " + why,
                    "Revit refuses this step together with the rest: fix the elements named by hand (e.g. a shape-edited floor), or leave them; the rest was run without it.");
                if (planJson.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.ToString() == s.Id) is JsonObject pj) { pj["dropped"] = why; item["level"] = pj["level"]?.DeepClone(); }
                item["failures"] = failures.DeepClone(); item["foundBy"] = foundBy;
                if (differs) item["differsFromPreview"] = true;
                reportOnly.Add(item);
                if (found.Any(c => c.Key == s.ClashKey)) reportWhy[s.ClashKey] = "dropped: " + why;
                droppedSteps.Add(new JsonObject
                {
                    ["id"] = s.Id, ["kind"] = s.Kind, ["a"] = s.A, ["b"] = s.B, ["why"] = why, ["failures"] = failures.DeepClone(), ["foundBy"] = foundBy,
                    ["differsFromPreview"] = differs ? true : null,
                });
            }
            string WhyOf(IList<(string Text, long[] Ids)> f) =>
                f.Count == 0 ? "Revit refused it" : $"Revit: \"{f[0].Text}\" (elements {string.Join(", ", f[0].Ids ?? new long[0])})" + (f.Count > 1 ? $" and {f.Count - 1} more" : "");

            // drop_failing_steps: the apply drops what its preview dropped, before it runs (the same steps, the same plan hash).
            var previewKey = doc.Title + "|" + planHash;
            var fromPreview = false;
            if (!checkOnly && !dryRunMode && dropFailing && DroppedByPreview.TryGetValue(previewKey, out var preview))
            {
                fromPreview = true;
                foreach (var s in steps) if (preview.TryGetValue(s.Id, out var pv)) Drop(s, pv.Why, pv.Failures, "the preview", false);
                if (preview.Count > 0) notes.Add($"drop_failing_steps: {preview.Count} step{(preview.Count == 1 ? "" : "s")} the preview dropped {(preview.Count == 1 ? "is" : "are")} left out up front.");
            }

            JsonObject result = null;
            var dropRounds = 0;
            for (var attempt = 1; ; attempt++)
            {
                result = RunOnce(false);
                if (checkOnly) break;
                if (attempt < 3 && retryDrop.Count > 0 && result["transaction"]?.ToString() == "VerifyFailed")
                {
                    foreach (var s in steps.Where(x => retryDrop.Contains(x.Id))) s.Excluded = true;
                    notes.Add($"Revit reported \"joined but do not intersect\" for {string.Join(", ", retryDrop.OrderBy(x => x, StringComparer.Ordinal))}: left out (dropped as not touching) and the change was run again without them.");
                    continue;
                }
                if (!dropFailing || dropRounds >= ClashResolveLogic.MaxDropRounds) break;
                var failures = StepFailures(result);
                if (failures == null) break;
                dropRounds++;
                var active = steps.Where(s => Runs(s) && !s.Excluded).ToList();
                var map = ClashResolveLogic.FailingSteps(failures.Select(f => f.Ids).ToList(), active.Select(s => (s.Id, s.A, s.B)).ToList());
                Dictionary<string, List<(string Text, long[] Ids)>> because = null;
                var foundBy = "the failure's elements";
                if (map != null) because = map.ToDictionary(kv => kv.Key, kv => kv.Value.Select(i => failures[i]).ToList(), StringComparer.Ordinal);
                else
                {
                    // The ids do not tell: halve the steps in trial runs (always rolled back) until the failing ones are found.
                    foundBy = "trial runs";
                    var singles = new Dictionary<string, List<(string Text, long[] Ids)>>(StringComparer.Ordinal);
                    var idx = ClashResolveLogic.Bisect(active.Count, subset =>
                    {
                        var keep = new HashSet<string>(subset.Select(i => active[i].Id), StringComparer.Ordinal);
                        trialOff.Clear();
                        foreach (var s in active) if (!keep.Contains(s.Id)) trialOff.Add(s.Id);
                        var f = StepFailures(RunOnce(true));
                        if (f != null && subset.Count == 1) singles[active[subset[0]].Id] = f;
                        return f != null;
                    }, MaxBisectTrials, out var trials);
                    trialOff.Clear();
                    notes.Add($"drop_failing_steps: Revit's failure named no planned step; {trials} trial run{(trials == 1 ? "" : "s")} (rolled back) searched for the failing steps.");
                    if (idx != null) because = idx.ToDictionary(i => active[i].Id, i => singles.TryGetValue(active[i].Id, out var f) ? f : failures, StringComparer.Ordinal);
                }
                if (because == null || because.Count == 0)
                {
                    notes.Add($"drop_failing_steps: the steps behind \"{failures[0].Text}\" could not be found (they fail only together, or more than {MaxBisectTrials} trial runs would be needed): nothing was dropped.");
                    break;
                }
                foreach (var s in active) if (because.TryGetValue(s.Id, out var f)) Drop(s, WhyOf(f), FailuresJson(f), foundBy, fromPreview);
                notes.Add($"drop_failing_steps: {because.Count} step{(because.Count == 1 ? "" : "s")} dropped for Revit's \"{failures[0].Text}\" (found by {foundBy}"
                    + (fromPreview ? "; differs from the preview: the preview did not meet this failure" : "") + "), and the change was run again without them (see droppedSteps).");
            }
            // The preview's drops, for its apply (in this session: an apply needs a dry run of exactly this change first).
            if (dryRunMode && !checkOnly && dropFailing && result["success"] is JsonValue sv && sv.TryGetValue<bool>(out var dryOk) && dryOk)
            {
                if (DroppedByPreview.Count > 50) DroppedByPreview.Clear();
                DroppedByPreview[previewKey] = steps.Where(s => s.Dropped != null).ToDictionary(s => s.Id,
                    s => (s.Dropped, (JsonArray)(droppedSteps.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.ToString() == s.Id)?["failures"]?.DeepClone() ?? new JsonArray())), StringComparer.Ordinal);
            }

            if (checkOnly) result["success"] = true;
            if (result["verify"] is JsonObject failedVerify && result["guard"] == null)   // the guard itself threw
                result["guard"] = new JsonObject { ["ok"] = false, ["checked"] = guardIds.Count, ["mismatches"] = new JsonArray(), ["error"] = failedVerify["error"]?.ToString() };
            foreach (var k in new[] { "guard", "after", "done", "dropped", "skipped", "warnings" }) if (!result.ContainsKey(k)) result[k] = null;
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled)
            {
                // Nothing was kept: nothing is reported as done.
                if (result["done"] is JsonObject dn) { result["wouldDo"] = dn.DeepClone(); result["done"] = new JsonObject { ["join"] = 0, ["switchJoin"] = 0, ["cutback"] = 0, ["cope"] = 0, ["trim"] = 0 }; }
                if (result["after"] != null) { result["afterIfKept"] = result["after"].DeepClone(); result["after"] = null; }
                if (result["transaction"]?.ToString() == "VerifyFailed")
                    result["note"] = "The guard found a change beyond the fix (see guard.mismatches: a moved or retyped element, a lost tag or dimension, or a new warning): the whole change was rolled back. Nothing was changed.";
            }
            return result;
        }

        // ---- request ----------------------------------------------------------------------------------------------

        private static List<string> Order(JsonObject args)
        {
            var given = Args.Strings(args, "order");
            if (given.Count == 0) return ClashResolveLogic.DefaultOrder.ToList();
            var list = new List<string>();
            foreach (var s in given)
            {
                var name = ClashResolveLogic.CategoryAlias(s) ?? (s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? s : "OST_" + s.Replace(" ", ""));
                if (!Enum.TryParse<BuiltInCategory>(name, true, out var bic) || bic == BuiltInCategory.INVALID)
                    throw new CommandException($"order: '{s}' is not a category name. Use OST_ names, e.g. [{string.Join(", ", ClashResolveLogic.DefaultOrder.Select(o => "\"" + o + "\""))}].");
                if (!list.Contains(bic.ToString())) list.Add(bic.ToString());
            }
            return list;
        }

        /// <summary>One test: a standard one by name, a custom one (a_categories / b_categories), or by default ST vs ST.</summary>
        private static Clashes.TestSpec PickTest(JsonObject args)
        {
            var name = Args.Str(args, "test");
            var custom = args["a_categories"] is JsonArray || args["b_categories"] is JsonArray;
            if (name != null && name.Equals("all", StringComparison.OrdinalIgnoreCase))
                throw new CommandException("clash_resolve works on one test: name it (e.g. \"ST vs ST\") or give a_categories / b_categories.");
            var a = (JsonObject)args.DeepClone();
            a["test"] = name ?? "ST vs ST";
            if (!custom && !Clashes.Standard.Any(s => s.Name.Equals(a["test"]?.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                a["a_categories"] = new JsonArray(StVsSt.Select(c => (JsonNode)c).ToArray());
                a["b_categories"] = new JsonArray(StVsSt.Select(c => (JsonNode)c).ToArray());
            }
            return ClashCommands.PickTests(a)[0];
        }

        private static JsonObject ByPair(IEnumerable<Clash> clashes)
        {
            var o = new JsonObject();
            foreach (var g in clashes.GroupBy(c => $"{c.CatA} x {c.CatB}").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)) o[g.Key] = g.Count();
            return o;
        }

        private static JsonObject ReportItem(Clash c, long a, long b, string catA, string catB, string code, string why, string recommendation) => new JsonObject
        {
            ["a"] = a, ["b"] = b, ["aCategory"] = catA, ["bCategory"] = catB, ["code"] = code, ["why"] = why, ["recommendation"] = recommendation,
            ["clashKey"] = c.Key, ["level"] = c.Level, ["depthMm"] = c.DepthMm,
        };

        /// <summary>The report-only items grouped by reason code: { code, why, recommendation, count, sampleKeys }, largest first.</summary>
        private static JsonArray ReportGroups(JsonArray reportOnly) => new JsonArray(reportOnly.OfType<JsonObject>()
            .GroupBy(o => o["code"]?.ToString() ?? "other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (JsonNode)new JsonObject
            {
                ["code"] = g.Key, ["why"] = g.First()["why"]?.ToString(), ["recommendation"] = g.First()["recommendation"]?.ToString(), ["count"] = g.Count(),
                ["sampleKeys"] = new JsonArray(g.Take(5).Select(o => (JsonNode)o["clashKey"]?.ToString()).ToArray()),
            }).ToArray());

        /// <summary>The parameters a cutback writes: the extension (an end that is not joined) and the join cutback (a joined end).</summary>
        private static (Parameter Extension, Parameter JoinCutback) CutbackParameters(FamilyInstance fi, int end) =>
            fi == null ? (null, null) : (
                fi.get_Parameter(end == 0 ? BuiltInParameter.START_EXTENSION : BuiltInParameter.END_EXTENSION),
                fi.get_Parameter(end == 0 ? BuiltInParameter.START_JOIN_CUTBACK : BuiltInParameter.END_JOIN_CUTBACK));

        private static string CutbackMethod(FamilyInstance fi, int end)
        {
            var (ext, join) = CutbackParameters(fi, end);
            return ClashResolveLogic.CutbackMethod(ext != null && !ext.IsReadOnly, join != null && !join.IsReadOnly);
        }

        /// <summary>The overlap of two elements now (fresh geometry, after Regenerate), as the clash test measures it: mm, rounded.</summary>
        private static double DepthMm(Element a, Element b)
        {
            double depth = 0;
            var sb = Clashes.Solids(b);
            foreach (var sa in Clashes.Solids(a))
                foreach (var s2 in sb)
                    try
                    {
                        var inter = BooleanOperationsUtils.ExecuteBooleanOperation(sa, s2, BooleanOperationsType.Intersect);
                        if (inter == null || inter.Volume < 1e-6) continue;
                        var (mn, mx) = ViewTools.Box(inter, Transform.Identity);
                        depth = Math.Max(depth, Math.Min(mx.X - mn.X, Math.Min(mx.Y - mn.Y, mx.Z - mn.Z)));
                    }
                    catch { /* degenerate pair of solids */ }
            return Math.Round(Lengths.Mm(depth));
        }

        /// <summary>A planned step listed as report-only instead (excluded, or dropped for a warning): the report item's fields.</summary>
        private static JsonObject StepReport(Document doc, Step s, string code, string why, string recommendation) => new JsonObject
        {
            ["id"] = s.Id, ["kind"] = s.Kind, ["a"] = s.A, ["b"] = s.B,
            ["aCategory"] = doc.GetElement(new ElementId(s.A))?.Category?.Name, ["bCategory"] = doc.GetElement(new ElementId(s.B))?.Category?.Name,
            ["code"] = code, ["why"] = why, ["recommendation"] = recommendation, ["clashKey"] = s.ClashKey, ["level"] = null, ["depthMm"] = s.DepthMm,
        };

        private static JsonObject SkipItem(Step s, string reason) => new JsonObject { ["id"] = s.Id, ["a"] = s.A, ["b"] = s.B, ["kind"] = s.Kind, ["reason"] = reason };

        // ---- classification -----------------------------------------------------------------------------------------

        private static ClashResolveLogic.Decision Decide(Document doc, Clash c, Element ea, Element eb, Clashes.SolidCache geometry, ClashResolveLogic.Options opts)
        {
            var solidsA = geometry.Of(ea); var solidsB = geometry.Of(eb);
            var inter = new List<Solid>();
            foreach (var sa in solidsA)
                foreach (var sb in solidsB)
                    try
                    {
                        var i = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                        if (i != null && i.Volume > 1e-9) inter.Add(i);
                    }
                    catch { /* degenerate pair of solids */ }
            var joined = false; var aCutsB = false;
            try
            {
                joined = JoinGeometryUtils.AreElementsJoined(doc, ea, eb);
                if (joined) aCutsB = JoinGeometryUtils.IsCuttingElementInJoin(doc, ea, eb);
            }
            catch { joined = false; }
            var pair = new ClashResolveLogic.Pair
            {
                A = SideOf(doc, ea, solidsA, inter), B = SideOf(doc, eb, solidsB, inter),
                ClashKind = c.Kind ?? "hard", Joined = joined, ACutsB = aCutsB, Overlap = inter.Sum(s => s.Volume),
            };
            return ClashResolveLogic.Classify(pair, opts);
        }

        private static ClashResolveLogic.Side SideOf(Document doc, Element e, List<Solid> solids, List<Solid> inter)
        {
            var cat = e.Category?.BuiltInCategory.ToString();
            var side = new ClashResolveLogic.Side { Id = e.Id.Value, Category = cat, Material = MaterialOf(doc, e, cat), Host = true, Volume = solids.Sum(s => s.Volume) };
            if (cat == nameof(BuiltInCategory.OST_StructuralFraming) && e.Location is LocationCurve lc && lc.Curve is Line line && line.Length > 1e-6 && inter.Count > 0)
            {
                var p0 = line.GetEndPoint(0);
                var u = (line.GetEndPoint(1) - p0).Normalize();
                (double Min, double Max) Extent(IEnumerable<Solid> list)
                {
                    double min = double.MaxValue, max = double.MinValue;
                    foreach (var s in list)
                        foreach (Edge edge in s.Edges)
                            foreach (var q in edge.Tessellate())
                            {
                                var t = (q - p0).DotProduct(u);
                                if (t < min) min = t;
                                if (t > max) max = t;
                            }
                    return (min, max);
                }
                var m = Extent(solids); var i = Extent(inter);
                var (end, cut) = ClashResolveLogic.EndOverlap(line.Length, m.Min, m.Max, i.Min, i.Max, Lengths.Ft(5));
                side.End = end; side.Cut = cut; side.Length = line.Length;
            }
            else if (cat == nameof(BuiltInCategory.OST_StructuralColumns) && inter.Count > 0 && solids.Count > 0)
            {
                // A column: is the overlap at its top or base (the column runs into the other element) or along its height?
                (double Min, double Max) Z(IEnumerable<Solid> list)
                {
                    double min = double.MaxValue, max = double.MinValue;
                    foreach (var s in list)
                        foreach (Edge edge in s.Edges)
                            foreach (var q in edge.Tessellate()) { if (q.Z < min) min = q.Z; if (q.Z > max) max = q.Z; }
                    return (min, max);
                }
                var m = Z(solids); var i = Z(inter);
                side.End = ClashResolveLogic.EndOverlap(m.Max - m.Min, m.Min, m.Max, i.Min, i.Max, Lengths.Ft(5)).End;
                side.Length = m.Max - m.Min;
            }
            return side;
        }

        /// <summary>The material of an element for the fix rules (the shared classification, StructuralMaterial).</summary>
        private static string MaterialOf(Document doc, Element e, string cat) => ClashResolveLogic.ForFix(StructuralMaterial.Of(e));

        // ---- pre-flight ---------------------------------------------------------------------------------------------

        private static (string Code, string Holder, string Detail)? BlockOf(Document doc, Element e)
        {
            if (e == null) return ("unreadable", null, "the element is gone");
            try { if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) return ("group", null, null); } catch { }
            try { if (e.DesignOption != null) return ("option", null, null); } catch { }
            if (!doc.IsWorkshared) return null;
            try
            {
                var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out var owner);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    if (string.IsNullOrWhiteSpace(owner)) { try { owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id).Owner; } catch { owner = null; } }
                    return ("borrowed", string.IsNullOrWhiteSpace(owner) ? "another user" : owner.Trim(), null);
                }
                var updates = WorksharingUtils.GetModelUpdatesStatus(doc, e.Id);
                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral) return ("central", null, null);
            }
            catch (Exception ex) { return ("unreadable", null, ex.Message); }
            return null;
        }

        // ---- the change -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Each fix in its own sub-transaction. After the fix: Regenerate and measure the pair's overlap again; a fix that Revit
        /// refuses, or that does not clear its clash (below the test's tolerance), is rolled back alone and skipped with the
        /// reason (counted in dropped), and the rest of the plan goes on. Deterministic: the dry run and the apply drop the same.
        /// </summary>
        private static void Execute(Document d, List<Step> steps, JsonObject done, JsonObject dropped, JsonArray skipped, Dictionary<string, string> ran, double toleranceMm,
            Dictionary<long, List<Step>> relocated)
        {
            void Count(JsonObject o, string key) => o[key] = o[key].GetValue<int>() + 1;
            string Key(string kind) => kind == "switch-join" ? "switchJoin" : kind;
            Element El(long id) => d.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"element {id} is gone");
            void Skip(IEnumerable<Step> group, string reason)
            {
                foreach (var s in group) { skipped.Add(SkipItem(s, reason)); ran[s.ClashKey] = "skipped: " + reason; }
            }
            // quiet: record nothing on failure (the caller decides); returns the reason it failed, or null when kept.
            string TryWhy(IList<Step> group, Action act, bool checkDepth, bool quiet, bool regenerate = true)
            {
                using (var st = new SubTransaction(d))
                {
                    string drop = null;
                    try
                    {
                        st.Start();
                        act();
                        if (regenerate || checkDepth) d.Regenerate();
                        if (checkDepth)
                            foreach (var s in group)
                            {
                                drop = ClashResolveLogic.DropReason(s.DepthMm, DepthMm(El(s.Kind == "trim" ? s.A : s.A), El(s.B)), toleranceMm);
                                if (drop != null) break;
                            }
                        if (drop != null)
                        {
                            st.RollBack();
                            if (!quiet) { Skip(group, drop); Count(dropped, Key(group[0].Kind)); }
                            return drop;
                        }
                        if (st.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the change");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                        if (!quiet) Skip(group, ex.Message);
                        return ex.Message ?? "Revit refused the change";
                    }
                }
            }
            bool Try(IList<Step> group, Action act) => TryWhy(group, act, true, false) == null;

            Action JoinAct(Step s) => () =>
            {
                var a = El(s.A); var b = El(s.B);
                if (s.Kind == "join")
                {
                    if (!JoinGeometryUtils.AreElementsJoined(d, a, b)) JoinGeometryUtils.JoinGeometry(d, a, b);
                    if (s.HasWinner && !JoinGeometryUtils.IsCuttingElementInJoin(d, a, b)) JoinGeometryUtils.SwitchJoinOrder(d, a, b);
                }
                else JoinGeometryUtils.SwitchJoinOrder(d, a, b);
                if (s.HasWinner && !JoinGeometryUtils.IsCuttingElementInJoin(d, a, b)) throw new InvalidOperationException("the join order did not take");
            };
            foreach (var s in steps.Where(s => (s.Kind == "join" || s.Kind == "switch-join") && !s.Touching))
            {
                if (s.Excluded) { Skip(new[] { s }, "joined but do not intersect: left out"); Count(dropped, "notTouching"); continue; }
                var joinAct = JoinAct(s);
                if (s.Fallback == null)
                {
                    if (Try(new[] { s }, joinAct)) { Count(done, Key(s.Kind)); ran[s.ClashKey] = s.Kind; }
                    continue;
                }
                var why = TryWhy(new[] { s }, joinAct, true, true);
                if (why == null) { Count(done, Key(s.Kind)); ran[s.ClashKey] = s.Kind; continue; }
                // The join did not clear (or was refused): the trim to face replaces it, and must clear the clash itself.
                var t = s.Fallback;
                var note = new List<string>();
                var trimWhy = TryWhy(new[] { t }, () => Trim(d, El(t.A), t, note), true, true);
                if (trimWhy == null)
                {
                    Count(done, "trim"); ran[s.ClashKey] = "trim";
                    if (!relocated.TryGetValue(t.A, out var mine)) relocated[t.A] = mine = new List<Step>();
                    mine.Add(t);
                    if (note.Count > 0) skipped.Add(new JsonObject { ["id"] = t.Id, ["a"] = t.A, ["b"] = t.B, ["kind"] = "trim", ["reason"] = "applied; " + string.Join("; ", note) });
                }
                else { Skip(new[] { s }, why + "; the trim to face did not help either: " + trimWhy); Count(dropped, Key(s.Kind)); Count(dropped, "trim"); }
            }
            // All-touching joins (not clash fixes, no depth to clear): each in its own sub-transaction, so a pair Revit cannot
            // join (it does not touch) is dropped quietly on its own; the model is regenerated once per batch, not per join.
            // Before each new join: the two must still really touch on the current geometry (an earlier join may have cut one
            // back), so Revit never leaves "joined but do not intersect"; the model is regenerated first only when an element of
            // the pair was changed since the last regeneration.
            const int batch = 50;
            var pending = 0;
            var dirty = new HashSet<long>();
            foreach (var s in steps.Where(s => (s.Kind == "join" || s.Kind == "switch-join") && s.Touching))
            {
                if (s.Excluded) { Count(dropped, "notTouching"); ran[s.ClashKey] = "skipped: not touching (joined but do not intersect)"; continue; }
                if (s.Kind == "join")
                {
                    if (dirty.Contains(s.A) || dirty.Contains(s.B)) { d.Regenerate(); dirty.Clear(); pending = 0; }
                    bool touches;
                    try { touches = RealContact(El(s.A), El(s.B), null); } catch { touches = false; }
                    if (!touches) { Count(dropped, "notTouching"); ran[s.ClashKey] = "skipped: not touching (after the earlier joins)"; continue; }
                }
                if (TryWhy(new[] { s }, JoinAct(s), false, true, regenerate: false) == null) { Count(done, Key(s.Kind)); ran[s.ClashKey] = s.Kind; dirty.Add(s.A); dirty.Add(s.B); }
                else { Count(dropped, "notTouching"); ran[s.ClashKey] = "skipped: not touching"; }
                if (++pending >= batch) { d.Regenerate(); pending = 0; dirty.Clear(); }
            }
            if (pending > 0) d.Regenerate();
            // One cutback per member end: the deepest overlap at that end. An end that is not joined takes a negative
            // extension; a joined end (Revit drives the extension) a larger join cutback. The location line never moves.
            foreach (var g in steps.Where(s => s.Kind == "cutback").GroupBy(s => (s.A, s.End)))
            {
                var group = g.ToList();
                var cut = group.Max(s => s.Cut);
                var ok = Try(group, () =>
                {
                    var fi = El(g.Key.A) as FamilyInstance ?? throw new InvalidOperationException("not a framing instance");
                    var (ext, join) = CutbackParameters(fi, g.Key.End);
                    var start = g.Key.End == 0;
                    switch (group[0].Method)
                    {
                        case "extension":
                            if (ext == null || ext.IsReadOnly) throw new InvalidOperationException($"{(start ? "Start" : "End")} Extension cannot be written now");
                            if (!ext.Set(ext.AsDouble() - cut)) throw new InvalidOperationException($"Revit refused the {(start ? "Start" : "End")} Extension value");
                            break;
                        case "join-cutback":
                            if (join == null || join.IsReadOnly) throw new InvalidOperationException($"{(start ? "Start" : "End")} Join Cutback cannot be written now");
                            if (!join.Set(join.AsDouble() + cut)) throw new InvalidOperationException($"Revit refused the {(start ? "Start" : "End")} Join Cutback value");
                            break;
                        default:
                            throw new InvalidOperationException($"neither {(start ? "Start" : "End")} Extension nor {(start ? "Start" : "End")} Join Cutback can be written on this member");
                    }
                });
                if (ok) { Count(done, "cutback"); foreach (var s in group) ran[s.ClashKey] = "cutback"; }
            }
            foreach (var s in steps.Where(s => s.Kind == "cope"))
            {
                var ok = Try(new[] { s }, () =>
                {
                    var fi = El(s.A) as FamilyInstance ?? throw new InvalidOperationException("not a framing instance");
                    var cutter = El(s.B) as FamilyInstance ?? throw new InvalidOperationException("the cutter is not a beam or column");
                    if (fi.GetCopingIds().Contains(cutter.Id)) throw new InvalidOperationException("already coped by this element");
                    if (!fi.AddCoping(cutter)) throw new InvalidOperationException("Revit did not add the coping");
                });
                if (ok) { Count(done, "cope"); ran[s.ClashKey] = "cope"; }
            }
        }

        // ---- R2 all-touching, R3 trim to face --------------------------------------------------------------------------

        private static readonly string[] TouchCategories =
            { "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming" };

        /// <summary>
        /// The touching pairs of concrete elements in the priority categories (in the model, or in the selection), narrowed like
        /// the clash test (its A / B categories, one element on each side; the level of the pair's contact) and without the
        /// clashing pairs (the clash part fixes those). Same-category pairs take part unless same_category is false.
        /// The pairs are cut into chunks (chunk_size, by level then id; ClashResolveLogic.Chunks) and only the requested chunk
        /// (1-based; default 1) is planned: a missing join when the two really touch (a solid overlap, or faces touching within
        /// 1 mm; near only = not touching, no step); an existing join in the wrong order is listed as "flip, confirm"
        /// (report-only) and switched only when its plan id is in flips.
        /// Answer: { candidates, pairs, right, join, switchJoin, flipConfirm, notTouching, byPair, chunk, chunks: [...], chunkSize, capped }.
        /// </summary>
        private static JsonObject Touching(Document doc, List<string> order, ICollection<ElementId> selection, HashSet<(long, long)> clashPairs,
            Clashes.SolidCache geometry, List<Step> steps, JsonArray planJson, JsonArray reportOnly, Dictionary<string, int> ids, Clashes.TestSpec test, string level,
            bool sameCategory, int chunkSize, int chunk, HashSet<string> flips)
        {
            var aCats = new HashSet<string>(test.A.Categories.Select(c => c.ToString()), StringComparer.OrdinalIgnoreCase);
            var bCats = new HashSet<string>(test.B.Categories.Select(c => c.ToString()), StringComparer.OrdinalIgnoreCase);
            var cats = TouchCategories.Where(c => ClashResolveLogic.Rank(order, c) >= 0 && (aCats.Contains(c) || bCats.Contains(c)))
                .Select(c => Enum.TryParse<BuiltInCategory>(c, out var b) ? new ElementId(b) : null).Where(x => x != null).ToList();
            var summary = new JsonObject
            {
                ["candidates"] = 0, ["pairs"] = 0, ["clashing"] = 0, ["right"] = 0, ["join"] = 0, ["switchJoin"] = 0, ["flipConfirm"] = 0, ["notTouching"] = 0,
                ["byPair"] = new JsonObject(), ["chunk"] = 1, ["chunkSize"] = chunkSize, ["chunks"] = new JsonArray(), ["capped"] = false,
            };
            if (cats.Count == 0) return summary;
            var collector = selection != null
                ? (selection.Count == 0 ? null : new FilteredElementCollector(doc, selection))
                : new FilteredElementCollector(doc);
            if (collector == null) { summary["note"] = "selection: nothing is selected"; return summary; }
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            (int Rank, string Name) LevelAt(double zFeet)
            {
                var i = levels.FindLastIndex(l => l.ProjectElevation <= zFeet + 0.01);
                if (i < 0) i = levels.Count > 0 ? 0 : -1;
                return i < 0 ? (0, null) : (i, levels[i].Name);
            }
            var els = new Dictionary<long, Element>();
            var boxes = new List<(long Id, double[] Min, double[] Max)>();
            foreach (var e in collector.WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                if (e.ViewSpecific || BlockOf(doc, e) is { } blk && (blk.Code == "group" || blk.Code == "option")) continue;
                if (ClashResolveLogic.ForFix(StructuralMaterial.Of(e)) != "concrete") continue;
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                els[e.Id.Value] = e;
                boxes.Add((e.Id.Value, new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, new[] { bb.Max.X, bb.Max.Y, bb.Max.Z }));
            }
            summary["candidates"] = els.Count;
            var boxOf = boxes.ToDictionary(x => x.Id);

            // The in-scope pairs, each with the level of its contact (the middle of the two boxes' common part).
            var scoped = new List<(int LevelRank, string Level, long A, long B)>();
            // Clashing pairs stay in the list (they get their clash step, not a touching one), so the chunks do not shift when
            // earlier fixes clear clashes.
            foreach (var (ia, ib) in ClashResolveLogic.TouchingPairs(boxes, Lengths.Ft(5)))
            {
                var catA = els[ia].Category.BuiltInCategory.ToString(); var catB = els[ib].Category.BuiltInCategory.ToString();
                if (!ClashResolveLogic.TouchPairAllowed(catA, catB, aCats, bCats, sameCategory)) continue;
                var za = boxOf[ia]; var zb = boxOf[ib];
                var lv = LevelAt((Math.Max(za.Min[2], zb.Min[2]) + Math.Min(za.Max[2], zb.Max[2])) / 2);
                if (level != null && !string.Equals(lv.Name, level, StringComparison.OrdinalIgnoreCase)) continue;
                scoped.Add((lv.Rank, lv.Name, ia, ib));
            }
            summary["pairs"] = scoped.Count;
            var chunks = ClashResolveLogic.Chunks(scoped, chunkSize);
            var current = chunks.Count == 0 ? 1 : Math.Min(Math.Max(1, chunk <= 0 ? 1 : chunk), chunks.Count);
            if (chunk > chunks.Count && chunks.Count > 0) summary["note"] = $"chunk {chunk} does not exist ({chunks.Count} chunk{(chunks.Count == 1 ? "" : "s")}): chunk {current} is planned.";
            summary["chunk"] = current; summary["capped"] = chunks.Count > 1;
            if (chunks.Count > 1) summary["advice"] = $"{scoped.Count} touching pairs in {chunks.Count} chunks of {chunkSize}: each chunk is its own dry run, apply and undo step (run again with chunk: 2, 3, ...).";

            var volume = new Dictionary<long, double>();
            double Vol(Element e) => volume.TryGetValue(e.Id.Value, out var v) ? v : (volume[e.Id.Value] = geometry.Of(e).Sum(x => x.Volume));
            (bool Joined, bool Right, string Winner) JoinState(long ia, long ib)
            {
                var ea = els[ia]; var eb = els[ib];
                var catA = ea.Category.BuiltInCategory.ToString(); var catB = eb.Category.BuiltInCategory.ToString();
                var sa = new ClashResolveLogic.Side { Id = ia, Category = catA, Material = "concrete" };
                var sb = new ClashResolveLogic.Side { Id = ib, Category = catB, Material = "concrete" };
                if (catA == catB) { sa.Volume = Vol(ea); sb.Volume = Vol(eb); }
                var winner = ClashResolveLogic.CutterOf(order, sa, sb);
                var joined = JoinGeometryUtils.AreElementsJoined(doc, ea, eb);
                var right = !joined || winner == null || (winner == "A") == JoinGeometryUtils.IsCuttingElementInJoin(doc, ea, eb);
                return (joined, right, winner);
            }
            // Every chunk's counts (join state only: cheap); the real-contact check only for the chunk planned.
            var chunkList = (JsonArray)summary["chunks"];
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                int notJoined = 0, wrong = 0, clashing = 0;
                foreach (var i in chunks[ci])
                {
                    if (clashPairs.Contains((Math.Min(scoped[i].A, scoped[i].B), Math.Max(scoped[i].A, scoped[i].B)))) { clashing++; continue; }
                    try { var js = JoinState(scoped[i].A, scoped[i].B); if (!js.Joined) notJoined++; else if (!js.Right) wrong++; } catch { }
                }
                chunkList.Add(new JsonObject
                {
                    ["chunk"] = ci + 1, ["pairs"] = chunks[ci].Count, ["clashing"] = clashing, ["notJoined"] = notJoined, ["wrongOrder"] = wrong,
                    ["levels"] = new JsonArray(chunks[ci].Select(i => scoped[i].Level ?? "").Distinct().Select(n => (JsonNode)n).ToArray()),
                });
            }
            if (chunks.Count == 0) return summary;

            var byPair = (JsonObject)summary["byPair"];
            void Inc(string k) => summary[k] = summary[k].GetValue<int>() + 1;
            foreach (var i in chunks[current - 1])
            {
                var (ia, ib) = (scoped[i].A, scoped[i].B);
                if (clashPairs.Contains((Math.Min(ia, ib), Math.Max(ia, ib)))) { Inc("clashing"); continue; }   // fixed by its clash step
                var ea = els[ia]; var eb = els[ib];
                (bool Joined, bool Right, string Winner) js;
                try { js = JoinState(ia, ib); } catch { continue; }
                var (a, b) = js.Winner == "B" ? (eb, ea) : (ea, eb);
                var flipId = ClashResolveLogic.PlanId("switch-join", a.Id.Value, b.Id.Value, -1);
                var touches = !js.Joined && RealContact(a, b, geometry);
                var decision = ClashResolveLogic.TouchDecision(js.Joined, js.Right, touches, clashes: false, flipConfirmed: flips.Contains(flipId));
                switch (decision)
                {
                    case "right": Inc("right"); continue;
                    case "not-touching": Inc("notTouching"); continue;
                    case "flip-confirm":
                        Inc("flipConfirm");
                        reportOnly.Add(new JsonObject
                        {
                            ["id"] = flipId, ["a"] = a.Id.Value, ["b"] = b.Id.Value, ["aCategory"] = a.Category?.Name, ["bCategory"] = b.Category?.Name,
                            ["code"] = "flip-confirm", ["why"] = $"joined, but the order says the {a.Category?.Name} should cut the {b.Category?.Name}: may be a deliberate exception",
                            ["recommendation"] = "Flip only if it is not deliberate: run again with this id in flips.", ["clashKey"] = flipId, ["level"] = scoped[i].Level, ["depthMm"] = 0,
                        });
                        continue;
                }
                var kind = decision;   // join | switch-join
                var id = ClashResolveLogic.PlanId(kind, a.Id.Value, b.Id.Value, -1);
                ids[id] = ids.TryGetValue(id, out var n) ? n + 1 : 1;
                if (ids[id] > 1) id += "#" + ids[id];
                var step = new Step { Id = id, Kind = kind, ClashKey = "touch:" + id, A = a.Id.Value, B = b.Id.Value, HasWinner = js.Winner != null, Method = kind, Touching = true };
                steps.Add(step);
                planJson.Add(new JsonObject
                {
                    ["id"] = id, ["kind"] = kind, ["scope"] = "touching", ["a"] = step.A, ["b"] = step.B, ["aCategory"] = a.Category?.Name, ["bCategory"] = b.Category?.Name,
                    ["cutter"] = js.Winner != null ? (JsonNode)step.A : null, ["cut"] = js.Winner != null ? (JsonNode)step.B : null,
                    ["cutterCategory"] = js.Winner != null ? a.Category?.Name : null, ["cutCategory"] = js.Winner != null ? b.Category?.Name : null, ["material"] = "concrete",
                    ["level"] = scoped[i].Level, ["chunk"] = current, ["confirmedFlip"] = kind == "switch-join" ? true : null,
                });
                Inc(kind == "join" ? "join" : "switchJoin");
                var pk = $"{a.Category?.Name} x {b.Category?.Name}";
                byPair[pk] = (byPair[pk]?.GetValue<int>() ?? 0) + 1;
            }
            return summary;
        }

        /// <summary>
        /// Whether two elements really touch: their solids overlap (more than a trace), or a planar face of one lies on a face
        /// of the other (opposite normals, within 1 mm, and the faces overlap). Fresh geometry when no cache is given.
        /// </summary>
        private static bool RealContact(Element a, Element b, Clashes.SolidCache geometry)
        {
            var sa = geometry != null ? geometry.Of(a) : Clashes.Solids(a).ToList();
            var sb = geometry != null ? geometry.Of(b) : Clashes.Solids(b).ToList();
            foreach (var x in sa)
                foreach (var y in sb)
                    try
                    {
                        var inter = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                        if (inter != null && inter.Volume > 1e-6) return true;   // about 28 mm3
                    }
                    catch { /* degenerate pair of solids: try the faces */ }
            var tol = Lengths.Ft(1);
            bool OnFace(PlanarFace target, Face from)
            {
                try
                {
                    foreach (EdgeArray loop in from.EdgeLoops)
                        foreach (Edge edge in loop)
                            foreach (var p in edge.Tessellate())
                            {
                                var ir = target.Project(p);
                                if (ir != null && ir.Distance <= tol) return true;
                            }
                }
                catch { }
                return false;
            }
            foreach (var x in sa)
                foreach (Face fa in x.Faces)
                {
                    if (!(fa is PlanarFace pa)) continue;
                    foreach (var y in sb)
                        foreach (Face fb in y.Faces)
                        {
                            if (!(fb is PlanarFace pb)) continue;
                            if (!ClashResolveLogic.FacesTouch(new[] { pa.FaceNormal.X, pa.FaceNormal.Y, pa.FaceNormal.Z }, new[] { pa.Origin.X, pa.Origin.Y, pa.Origin.Z },
                                    new[] { pb.FaceNormal.X, pb.FaceNormal.Y, pb.FaceNormal.Z }, new[] { pb.Origin.X, pb.Origin.Y, pb.Origin.Z }, tol)) continue;
                            if (OnFace(pa, pb) || OnFace(pb, pa)) return true;
                        }
                }
            return false;
        }

        /// <summary>
        /// The trim to face for a join step: the cut element (a beam or a wall on a straight location line) whose one end lies
        /// in the cutting element's solid, shortened at that end to the face its axis enters through. Null when not eligible;
        /// skip (report-only) when the trim would exceed the limit or a wall hosts an opening in the stretch it would remove.
        /// </summary>
        private static Step TrimFor(Step join, Element cutter, Element member, Clashes.SolidCache geometry, double maxFt, double maxMm,
            out (string Code, string Why, string Recommendation)? skip)
        {
            skip = null;
            if (cutter == null || member == null) return null;
            var cat = member.Category?.BuiltInCategory;
            if (cat != BuiltInCategory.OST_StructuralFraming && cat != BuiltInCategory.OST_Walls) return null;
            if (!(member.Location is LocationCurve lc) || !(lc.Curve is Line line) || line.Length < 1e-6) return null;
            var solids = geometry.Of(cutter);
            if (solids.Count == 0) return null;
            var p0 = line.GetEndPoint(0); var p1 = line.GetEndPoint(1); var u = (p1 - p0).Normalize();
            // Where the member's axis runs inside the cutter's solid (not its box): the end is in it when a stretch reaches it.
            var inside = new List<(double From, double To)>();
            var axis = Line.CreateBound(p0 - u * Lengths.Ft(10), p1 + u * Lengths.Ft(10));
            foreach (var sol in solids)
                try
                {
                    var hitsIn = sol.IntersectWithCurve(axis, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                    for (var i = 0; i < hitsIn.SegmentCount; i++)
                    {
                        var seg = hitsIn.GetCurveSegment(i);
                        inside.Add(((seg.GetEndPoint(0) - p0).DotProduct(u), (seg.GetEndPoint(1) - p0).DotProduct(u)));
                    }
                }
                catch { /* a solid Revit cannot intersect: no stretch */ }
            var in0 = ClashResolveLogic.EndInside(0, inside, Lengths.Ft(1)); var in1 = ClashResolveLogic.EndInside(line.Length, inside, Lengths.Ft(1));
            if (in0 == in1) return null;   // neither end, or both: not an end running into a face
            var end = in1 ? 1 : 0;
            var hits = new List<(double T, XYZ Normal)>();
            foreach (var sol in solids)
                foreach (Face f in sol.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    var t = ClashResolveLogic.AxisPlane(new[] { p0.X, p0.Y, p0.Z }, new[] { u.X, u.Y, u.Z }, new[] { pf.Origin.X, pf.Origin.Y, pf.Origin.Z }, new[] { pf.FaceNormal.X, pf.FaceNormal.Y, pf.FaceNormal.Z });
                    if (t == null) continue;
                    try { var ir = pf.Project(p0 + u * t.Value); if (ir != null && ir.Distance < 1e-4) hits.Add((t.Value, pf.FaceNormal)); } catch { }
                }
            var target = ClashResolveLogic.TrimTarget(line.Length, hits.Select(h => h.T), end, maxFt, Lengths.Ft(1), out var reason);
            if (target == null)
            {
                if (reason == "trim too long")
                {
                    var all = ClashResolveLogic.TrimTarget(line.Length, hits.Select(h => h.T), end, double.MaxValue, Lengths.Ft(1), out _);
                    if (all != null)
                        skip = ("trim-too-long", $"trim too long: the end would be shortened {Math.Round(Lengths.Mm(all.Value.Trim)).ToString(System.Globalization.CultureInfo.InvariantCulture)} mm (more than {maxMm} mm)",
                            "Check the member's end by hand; the join stays planned.");
                }
                return null;
            }
            if (member is Wall wall)
            {
                // A wall that hosts a door, window or opening in the stretch the trim removes is left for a person.
                var ranges = new List<(double From, double To)>();
                try
                {
                    foreach (var iid in wall.FindInserts(true, true, true, true))
                    {
                        var bb = member.Document.GetElement(iid)?.get_BoundingBox(null);
                        if (bb == null) continue;
                        var ts = new[] { bb.Min.X, bb.Max.X }.SelectMany(x => new[] { bb.Min.Y, bb.Max.Y }.SelectMany(y => new[] { bb.Min.Z, bb.Max.Z }.Select(z => (new XYZ(x, y, z) - p0).DotProduct(u)))).ToList();
                        ranges.Add((ts.Min(), ts.Max()));
                    }
                }
                catch { ranges.Add((0, line.Length)); }   // inserts could not be read: treat the whole wall as hosting
                if (ClashResolveLogic.InsertInTrimmedStretch(line.Length, target.Value.T, end, ranges))
                {
                    skip = ("hosts-openings", "hosts openings: the wall has a door, window or opening in the stretch the trim would remove",
                        "Check the wall's end by hand; the join stays planned.");
                    return null;
                }
            }
            var hit = hits.OrderBy(h => Math.Abs(h.T - target.Value.T)).First();
            var np = p0 + u * target.Value.T;
            var n = hit.Normal;
            var dir = Math.Abs(n.Z) > 0.7 ? (n.Z > 0 ? "top" : "bottom") : Math.Abs(n.X) >= Math.Abs(n.Y) ? (n.X > 0 ? "+X side" : "-X side") : (n.Y > 0 ? "+Y side" : "-Y side");
            return new Step
            {
                Id = ClashResolveLogic.TrimId(member.Id.Value, cutter.Id.Value, end), Kind = "trim", ClashKey = join.ClashKey, DepthMm = join.DepthMm,
                A = member.Id.Value, B = cutter.Id.Value, HasWinner = true, End = end, Cut = target.Value.Trim, Method = "trim",
                NewEnd = new[] { np.X, np.Y, np.Z }, LengthBefore = line.Length, Face = $"{cutter.Category?.Name} {cutter.Id.Value} {dir} face",
            };
        }

        private static JsonObject TrimJson(Step t, Element member, Element cutter) => new JsonObject
        {
            ["id"] = t.Id, ["kind"] = "trim", ["a"] = t.A, ["b"] = t.B, ["aCategory"] = member.Category?.Name, ["bCategory"] = cutter.Category?.Name,
            ["cutter"] = t.B, ["cut"] = t.A, ["cutterCategory"] = cutter.Category?.Name, ["cutCategory"] = member.Category?.Name,
            ["end"] = t.End == 0 ? "start" : "end", ["lengthBeforeMm"] = Math.Round(Lengths.Mm(t.LengthBefore), 1),
            ["lengthAfterMm"] = Math.Round(Lengths.Mm(t.LengthBefore - t.Cut), 1), ["trimMm"] = Math.Round(Lengths.Mm(t.Cut), 1),
            ["face"] = t.Face, ["fallbackFor"] = t.ClashKey, ["clashKey"] = t.ClashKey, ["material"] = "concrete",
            ["note"] = "runs only when the join does not clear the clash",
        };

        /// <summary>
        /// Shortens the member's location line at the planned end. A joined end that holds the old point is disallowed for the
        /// change and allowed again afterwards when the new end holds; otherwise it stays disallowed (noted).
        /// </summary>
        private static void Trim(Document d, Element member, Step t, List<string> note)
        {
            if (!(member.Location is LocationCurve lc) || !(lc.Curve is Line line)) throw new InvalidOperationException("the member has no straight location line");
            var p0 = line.GetEndPoint(0); var p1 = line.GetEndPoint(1);
            var np = new XYZ(t.NewEnd[0], t.NewEnd[1], t.NewEnd[2]);
            // Only a shortening: the new end must lie strictly inside the line as it is now (another trim may have run first).
            if (!ClashResolveLogic.StrictlyInside(new[] { p0.X, p0.Y, p0.Z }, new[] { p1.X, p1.Y, p1.Z }, t.NewEnd, 1e-6))
                throw new InvalidOperationException("the new end is not inside the member's current line: a trim only shortens");
            var target = t.End == 1 ? Line.CreateBound(p0, np) : Line.CreateBound(np, p1);
            bool Holds() => member.Location is LocationCurve now && now.Curve is Line l && l.GetEndPoint(t.End).IsAlmostEqualTo(np, 1e-4) && l.GetEndPoint(1 - t.End).IsAlmostEqualTo(t.End == 1 ? p0 : p1, 1e-6);
            void SetJoin(bool allow)
            {
                if (member is FamilyInstance fi)
                {
                    if (allow) StructuralFramingUtils.AllowJoinAtEnd(fi, t.End); else StructuralFramingUtils.DisallowJoinAtEnd(fi, t.End);
                }
                else if (member is Wall w)
                {
                    if (allow) WallUtils.AllowWallJoinAtEnd(w, t.End); else WallUtils.DisallowWallJoinAtEnd(w, t.End);
                }
            }
            bool JoinAllowed() => member is FamilyInstance f2 ? StructuralFramingUtils.IsJoinAllowedAtEnd(f2, t.End) : member is Wall w2 && WallUtils.IsWallJoinAllowedAtEnd(w2, t.End);
            ((LocationCurve)member.Location).Curve = target;
            d.Regenerate();
            if (Holds()) return;
            if (!JoinAllowed()) throw new InvalidOperationException("Revit did not take the trimmed end");
            SetJoin(false);
            ((LocationCurve)member.Location).Curve = target;
            d.Regenerate();
            if (!Holds()) throw new InvalidOperationException("Revit did not take the trimmed end, even with the end join disallowed");
            SetJoin(true);
            d.Regenerate();
            if (Holds()) { note.Add($"the {(t.End == 0 ? "start" : "end")} join was disallowed for the trim and allowed again"); return; }
            SetJoin(false);
            ((LocationCurve)member.Location).Curve = target;
            d.Regenerate();
            if (!Holds()) throw new InvalidOperationException("Revit did not keep the trimmed end");
            note.Add($"the {(t.End == 0 ? "start" : "end")} join stays disallowed (it pulled the end back)");
        }

        // ---- guard ------------------------------------------------------------------------------------------------------

        /// <summary>Concrete volume (m3) and area (m2) per category of the given elements: HOST_VOLUME_COMPUTED / HOST_AREA_COMPUTED sums.</summary>
        private static Dictionary<string, (double Volume, double Area)> Quantities(Document doc, IEnumerable<long> ids)
        {
            var d = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var e = doc.GetElement(new ElementId(id));
                if (e == null || ClashResolveLogic.ForFix(StructuralMaterial.Of(e)) != "concrete") continue;
                double P(BuiltInParameter bip) { try { var p = e.get_Parameter(bip); return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : 0; } catch { return 0; } }
                var cat = e.Category?.Name ?? "(no category)";
                var (v, a) = d.TryGetValue(cat, out var x) ? x : (0, 0);
                d[cat] = (v + UnitUtils.ConvertFromInternalUnits(P(BuiltInParameter.HOST_VOLUME_COMPUTED), UnitTypeId.CubicMeters),
                          a + UnitUtils.ConvertFromInternalUnits(P(BuiltInParameter.HOST_AREA_COMPUTED), UnitTypeId.SquareMeters));
            }
            return d;
        }

        private static JsonObject QuantitiesJson(Dictionary<string, (double Volume, double Area)> before, Dictionary<string, (double Volume, double Area)> after)
        {
            var o = new JsonObject();
            foreach (var cat in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                before.TryGetValue(cat, out var b); after.TryGetValue(cat, out var a);
                o[cat] = new JsonObject
                {
                    ["volumeM3"] = new JsonObject { ["before"] = Math.Round(b.Volume, 3), ["after"] = Math.Round(a.Volume, 3) },
                    ["areaM2"] = new JsonObject { ["before"] = Math.Round(b.Area, 3), ["after"] = Math.Round(a.Area, 3) },
                };
            }
            return o;
        }

        private static List<(string Text, long[] Ids)> Warnings(Document doc)
        {
            var list = new List<(string, long[])>();
            foreach (var w in doc.GetWarnings())
            {
                var ids = new List<long>();
                try { foreach (var id in w.GetFailingElements()) ids.Add(id.Value); foreach (var id in w.GetAdditionalElements()) ids.Add(id.Value); } catch { }
                list.Add((w.GetDescriptionText(), ids.ToArray()));
            }
            return list;
        }

        /// <summary>Existence, type, location, tag count, the dimensions referencing each element (tags and dimensions collected once), and for walls that may be trimmed the elements they host.</summary>
        private static Dictionary<long, ClashResolveLogic.ElementState> Snapshot(Document doc, IList<long> ids, ICollection<long> hostedFor = null)
        {
            var map = new Dictionary<long, ClashResolveLogic.ElementState>();
            if (ids.Count == 0) return map;
            foreach (var id in ids)
            {
                var e = doc.GetElement(new ElementId(id));
                var st = new ClashResolveLogic.ElementState { Id = id, Exists = e != null };
                if (e != null)
                {
                    st.TypeId = e.GetTypeId().Value;
                    st.Location = Location(e);
                    if (hostedFor != null && hostedFor.Contains(id) && e is Wall w)
                        try { st.Hosted = w.FindInserts(true, true, true, true).Count; } catch { st.Hosted = null; }
                }
                map[id] = st;
            }
            foreach (var tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                ICollection<ElementId> tagged;
                try { tagged = tag.GetTaggedLocalElementIds(); } catch { continue; }
                foreach (var id in tagged) if (map.TryGetValue(id.Value, out var st)) st.Tags++;
            }
            var dims = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SpotDimension)).Cast<Dimension>())
                .GroupBy(x => x.Id.Value).Select(g => g.First());
            foreach (var dim in dims)
            {
                List<long> refs;
                int count;
                try
                {
                    var ra = dim.References;
                    count = ra?.Size ?? 0;
                    refs = new List<long>();
                    if (ra != null) foreach (Reference r in ra) refs.Add(r.ElementId.Value);
                }
                catch { continue; }
                if (!refs.Any(map.ContainsKey)) continue;
                var values = new List<double?>();
                try
                {
                    if (dim.NumberOfSegments > 1) foreach (DimensionSegment seg in dim.Segments) values.Add(seg.Value);
                    else values.Add(dim.Value);
                }
                catch { values.Add(null); }
                var text = ClashResolveLogic.DimensionText(count, refs, values);
                foreach (var id in refs.Distinct()) if (map.TryGetValue(id, out var st)) st.Dimensions[dim.Id.Value] = text;
            }
            return map;
        }

        private static double[] Location(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp)
                {
                    double rot = 0;
                    try { rot = lp.Rotation; } catch { rot = 0; }
                    return new[] { lp.Point.X, lp.Point.Y, lp.Point.Z, rot };
                }
                if (e.Location is LocationCurve lc && lc.Curve is Curve c && c.IsBound)
                {
                    var p0 = c.GetEndPoint(0); var p1 = c.GetEndPoint(1);
                    return new[] { p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z };
                }
            }
            catch { }
            return null;
        }
    }
}
