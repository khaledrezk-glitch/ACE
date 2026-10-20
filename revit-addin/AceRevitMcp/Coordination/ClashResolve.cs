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
    /// check_only, dry_run, expect_model, expect_version, max_ids, max_elements, test_force_verify_mismatch }
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
            public bool Blocked;
        }

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
            var found = Clashes.Run(doc, host, geometry, test, level, maxElements, notes, out var complete);
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
                    reportOnly.Add(ReportItem(c, c.IdA, c.IdB, c.CatA, c.CatB, "an element was not found in this model", "Run the clash test again."));
                    reportWhy[c.Key] = "an element was not found in this model";
                    continue;
                }
                var d = Decide(doc, c, ea, eb, geometry, opts);
                if (d.Kind == null)
                {
                    reportOnly.Add(ReportItem(c, ea.Id.Value, eb.Id.Value, ea.Category?.Name, eb.Category?.Name, d.Why, d.Recommendation));
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
                };
                steps.Add(step);
                var item = new JsonObject
                {
                    ["id"] = step.Id, ["kind"] = step.Kind, ["a"] = step.A, ["b"] = step.B,
                    ["aCategory"] = a.Category?.Name, ["bCategory"] = b.Category?.Name, ["material"] = d.Material, ["level"] = c.Level, ["why"] = d.Why,
                    ["clashKey"] = c.Key, ["depthMm"] = c.DepthMm,
                };
                if (d.Kind == "cutback" || d.Kind == "cope") { item["end"] = d.End; item["cutMm"] = Math.Round(Lengths.Mm(d.Cut), 1); }
                planJson.Add(item);
            }

            // The plan's fingerprint: an apply (or the Apply card, which replays the same args) must find the plan it previewed.
            var planHash = ClashResolveLogic.PlanHash(steps.Select(s =>
            {
                var trim = s.Kind == "cutback" || s.Kind == "cope";
                return (s.Kind, trim ? s.B : s.A, trim ? s.A : s.B, s.End, trim ? Lengths.Mm(s.Cut) : 0.0, s.Kind == "cope" ? s.B : 0L);
            }));
            var expectPlan = Args.Str(args, "expect_plan");

            // Pre-flight: every element a step changes must be editable now (joins change both, a cutback or cope the member).
            var held = new List<(long Id, string Code, string Holder, string Detail)>();
            var heldIds = new Dictionary<long, string>();
            foreach (var id in steps.SelectMany(s => s.Kind == "join" || s.Kind == "switch-join" ? new[] { s.A, s.B } : new[] { s.A }).Distinct())
            {
                var e = doc.GetElement(new ElementId(id));
                var block = BlockOf(doc, e);
                if (block == null) continue;
                held.Add((id, block.Value.Code, block.Value.Holder, block.Value.Detail));
                heldIds[id] = ClashResolveLogic.Explain(block.Value.Code, block.Value.Holder, block.Value.Detail).Why;
            }
            var blocked = ClashResolveLogic.Blocks(held, maxIds);
            foreach (var m in modelBlocks) blocked.Insert(0, m?.DeepClone());
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["test"] = test.Name; r["planHash"] = planHash;
                r["before"] = before.DeepClone(); r["plan"] = planJson.DeepClone(); r["reportOnly"] = reportOnly.DeepClone(); r["blocked"] = blocked.DeepClone();
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
            var touched = new HashSet<long>(guardIds);
            var beforeStates = checkOnly ? new List<ClashResolveLogic.ElementState>() : Snapshot(doc, guardIds).Values.ToList();
            var existingWarnings = Warnings(doc);
            var forceMismatch = Args.Bool(args, "test_force_verify_mismatch") && !Args.Bool(args, "dry_run");
            var worse = new List<(long Element, string What, string Before, string After)>();   // set by the body's after-run
            var trimmed = new HashSet<long>();                                                  // members cut back or coped

            var result = NativeChange.Run(app, args, "clash_resolve", Title, (d, plan) =>
            {
                var r = Common(new JsonObject());
                var done = new JsonObject { ["join"] = 0, ["switchJoin"] = 0, ["cutback"] = 0, ["cope"] = 0 };
                var skipped = (JsonArray)preSkipped.DeepClone();
                r["done"] = done; r["skipped"] = skipped;
                r["warnings"] = new JsonObject { ["before"] = existingWarnings.Count, ["after"] = null, ["new"] = new JsonArray() };
                r["after"] = null; r["guard"] = null;
                if (plan) return r;

                worse.Clear(); trimmed.Clear();
                var ran = new Dictionary<string, string>(StringComparer.Ordinal);   // clash key -> kind done, or the skip reason
                Execute(d, steps.Where(s => !s.Blocked).ToList(), done, skipped, ran);
                foreach (var s in steps.Where(s => s.Blocked)) ran[s.ClashKey] = "skipped: blocked";
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
            }, verifyWithWarnings: (d, r, posted) =>
            {
                var after = Snapshot(d, guardIds);
                var (mismatches, changedValues) = ClashResolveLogic.Compare(beforeStates, after, trimmed);
                mismatches.AddRange(worse);
                var (warnAfter, fresh, warnOk) = ClashResolveLogic.Warnings(existingWarnings, Warnings(d), posted.ToList());
                r["warnings"] = new JsonObject { ["before"] = existingWarnings.Count, ["after"] = warnAfter, ["new"] = new JsonArray(fresh.Select(w => (JsonNode)w).ToArray()) };
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
            });

            if (checkOnly) result["success"] = true;
            if (result["verify"] is JsonObject failedVerify && result["guard"] == null)   // the guard itself threw
                result["guard"] = new JsonObject { ["ok"] = false, ["checked"] = guardIds.Count, ["mismatches"] = new JsonArray(), ["error"] = failedVerify["error"]?.ToString() };
            foreach (var k in new[] { "guard", "after", "done", "skipped", "warnings" }) if (!result.ContainsKey(k)) result[k] = null;
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled)
            {
                // Nothing was kept: nothing is reported as done.
                if (result["done"] is JsonObject dn) { result["wouldDo"] = dn.DeepClone(); result["done"] = new JsonObject { ["join"] = 0, ["switchJoin"] = 0, ["cutback"] = 0, ["cope"] = 0 }; }
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
                var name = s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? s : "OST_" + s.Replace(" ", "");
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

        private static JsonObject ReportItem(Clash c, long a, long b, string catA, string catB, string why, string recommendation) => new JsonObject
        {
            ["a"] = a, ["b"] = b, ["aCategory"] = catA, ["bCategory"] = catB, ["why"] = why, ["recommendation"] = recommendation,
            ["clashKey"] = c.Key, ["level"] = c.Level, ["depthMm"] = c.DepthMm,
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
            var side = new ClashResolveLogic.Side { Category = cat, Material = MaterialOf(doc, e, cat), Host = true, Volume = solids.Sum(s => s.Volume) };
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
            return side;
        }

        /// <summary>The material of an element: the family's structural material type, then its structural material's class, then the category.</summary>
        private static string MaterialOf(Document doc, Element e, string cat)
        {
            string type = null, cls = null;
            try
            {
                ElementId mid = ElementId.InvalidElementId;
                if (e is FamilyInstance fi)
                {
                    type = fi.StructuralMaterialType.ToString();
                    mid = fi.StructuralMaterialId;
                }
                else if (doc.GetElement(e.GetTypeId()) is HostObjAttributes hoa && hoa.GetCompoundStructure() is CompoundStructure cs && cs.StructuralMaterialIndex >= 0)
                    mid = cs.GetMaterialId(cs.StructuralMaterialIndex);
                if (mid != ElementId.InvalidElementId && doc.GetElement(mid) is Material mat) cls = mat.MaterialClass;
            }
            catch { /* unknown: by category */ }
            return ClashResolveLogic.MaterialOf(cat, type, cls);
        }

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

        /// <summary>Each fix in its own sub-transaction: one that Revit refuses is skipped with the reason, the rest go on.</summary>
        private static void Execute(Document d, List<Step> steps, JsonObject done, JsonArray skipped, Dictionary<string, string> ran)
        {
            bool Try(IEnumerable<Step> group, Action act)
            {
                using (var st = new SubTransaction(d))
                {
                    try
                    {
                        st.Start();
                        act();
                        if (st.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the change");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                        foreach (var s in group) { skipped.Add(SkipItem(s, ex.Message)); ran[s.ClashKey] = "skipped: " + ex.Message; }
                        return false;
                    }
                }
            }
            void Count(string key) => done[key] = done[key].GetValue<int>() + 1;
            Element El(long id) => d.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"element {id} is gone");

            foreach (var s in steps.Where(s => s.Kind == "join" || s.Kind == "switch-join"))
            {
                var ok = Try(new[] { s }, () =>
                {
                    var a = El(s.A); var b = El(s.B);
                    if (s.Kind == "join")
                    {
                        if (!JoinGeometryUtils.AreElementsJoined(d, a, b)) JoinGeometryUtils.JoinGeometry(d, a, b);
                        if (s.HasWinner && !JoinGeometryUtils.IsCuttingElementInJoin(d, a, b)) JoinGeometryUtils.SwitchJoinOrder(d, a, b);
                    }
                    else JoinGeometryUtils.SwitchJoinOrder(d, a, b);
                    if (s.HasWinner && !JoinGeometryUtils.IsCuttingElementInJoin(d, a, b)) throw new InvalidOperationException("the join order did not take");
                });
                if (ok) { Count(s.Kind == "join" ? "join" : "switchJoin"); ran[s.ClashKey] = s.Kind; }
            }
            // One cutback per member end: the deepest overlap at that end.
            foreach (var g in steps.Where(s => s.Kind == "cutback").GroupBy(s => (s.A, s.End)))
            {
                var cut = g.Max(s => s.Cut);
                var ok = Try(g, () =>
                {
                    var fi = El(g.Key.A) as FamilyInstance ?? throw new InvalidOperationException("not a framing instance");
                    var which = g.Key.End == 0 ? "Start Extension" : "End Extension";
                    var p = fi.get_Parameter(g.Key.End == 0 ? BuiltInParameter.START_EXTENSION : BuiltInParameter.END_EXTENSION)
                            ?? throw new InvalidOperationException($"the member has no {which} parameter");
                    if (p.IsReadOnly) throw new InvalidOperationException($"{which} is read-only here (Revit drives it, e.g. the end is joined)");
                    if (!p.Set(p.AsDouble() - cut)) throw new InvalidOperationException($"Revit refused the {which} value");
                });
                if (ok) { Count("cutback"); foreach (var s in g) ran[s.ClashKey] = "cutback"; }
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
                if (ok) { Count("cope"); ran[s.ClashKey] = "cope"; }
            }
        }

        // ---- guard ------------------------------------------------------------------------------------------------------

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

        /// <summary>Existence, type, location, tag count and the dimensions referencing each element (tags and dimensions collected once).</summary>
        private static Dictionary<long, ClashResolveLogic.ElementState> Snapshot(Document doc, IList<long> ids)
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
