using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The plain rules of workset_admin facts / apply_plan (no Revit types, tested in tools/clash-test): the group key of an
    /// element, the creates a request gives, the order of the steps, where elements are after the moves, and the plan's
    /// fingerprint over the whole plan.
    /// </summary>
    internal static class WorksetPlanLogic
    {
        /// <summary>The fields of a group key, in order (the server builds the same string).</summary>
        public static readonly string[] KeyFields = { "workset", "category", "type", "level", "material", "zone", "option" };

        /// <summary>The category name given to CAD imports (their own category is the file name).</summary>
        public const string CadImports = "CAD Imports";

        /// <summary>A group's key: [workset, category, type, level, material, zone, option] joined with "|" (null = "").</summary>
        public static string Key(string workset, string category, string type, string level, string material, string zone, string option) =>
            string.Join("|", new[] { workset, category, type, level, material, zone, option }.Select(f => (f ?? "").Trim()));

        /// <summary>A material class for the facts: concrete (precast included), steel or other.</summary>
        public static string MaterialClass(string structuralMaterial) =>
            structuralMaterial == "steel" ? "steel" : structuralMaterial == "concrete" || structuralMaterial == "precast" ? "concrete" : "other";

        /// <summary>A sketch category (&lt;Sketch&gt;, &lt;Stair/Ramp Sketch: Boundary&gt;, ...): Revit names them in angle brackets.</summary>
        public static bool IsSketchCategory(string category)
        {
            var c = (category ?? "").Trim();
            return c.Length > 2 && c[0] == '<' && c[c.Length - 1] == '>';
        }

        /// <summary>
        /// A level's code: the text before "(" with a short discipline prefix ("A-", "C-", "ST-") removed. "A-01(FIRST FLOOR)" gives
        /// 01, "C-GF(GROUND FLOOR)" gives GF, "Level 1" gives Level 1. Null when nothing is left.
        /// </summary>
        public static string LevelCode(string levelName)
        {
            var n = (levelName ?? "").Trim();
            var open = n.IndexOf('(');
            var head = (open >= 0 ? n.Substring(0, open) : n).Trim();
            var dash = head.IndexOf('-');
            if (dash >= 1 && dash <= 3 && dash < head.Length - 1 && head.Substring(0, dash).All(char.IsLetter)) head = head.Substring(dash + 1).Trim();
            return head.Length == 0 ? null : head;
        }

        /// <summary>The words in a level name's brackets: "A-01(FIRST FLOOR)" gives FIRST FLOOR; null without brackets.</summary>
        public static string LevelPhrase(string levelName)
        {
            var n = levelName ?? "";
            var open = n.IndexOf('(');
            if (open < 0) return null;
            var close = n.IndexOf(')', open + 1);
            var inner = (close > open ? n.Substring(open + 1, close - open - 1) : n.Substring(open + 1)).Trim();
            return inner.Length == 0 ? null : inner;
        }

        /// <summary>
        /// The reset method (reset_to): the elements a plan moves to the default workset. Everything not already claimed by a
        /// move, not on a kept workset (the datum workset and keep_worksets), not in a kept group (keep_keys) and not already on
        /// the target; elements borrowed
        /// by others or changed in central are skipped and reported.
        /// </summary>
        public static (List<long> Move, List<long> Borrowed, List<long> Central) ResetPick(IEnumerable<(long Id, long Ws, string Key, bool Borrowed, bool Central)> elements,
            ICollection<long> claimed, ICollection<long> keepWorksets, ICollection<string> keepKeys, long toId)
        {
            var move = new List<long>(); var borrowed = new List<long>(); var central = new List<long>();
            foreach (var (id, ws, key, b, c) in elements ?? Enumerable.Empty<(long, long, string, bool, bool)>())
            {
                if ((claimed != null && claimed.Contains(id)) || (keepWorksets != null && keepWorksets.Contains(ws)) || (toId != 0 && ws == toId)) continue;
                if (keepKeys != null && key != null && keepKeys.Contains(key)) continue;   // a group that stays where it is
                if (b) borrowed.Add(id);
                else if (c) central.Add(id);
                else move.Add(id);
            }
            return (move, borrowed, central);
        }

        /// <summary>
        /// What a view shows differently after the moves, among the moved elements only: hidden (shown before, not after) and shown
        /// (not before, shown after).
        /// </summary>
        public static (List<long> Hidden, List<long> Shown) VisibilityDiff(IEnumerable<long> moved, ICollection<long> before, ICollection<long> after)
        {
            var hidden = new List<long>(); var shown = new List<long>();
            foreach (var id in (moved ?? Enumerable.Empty<long>()).Distinct())
            {
                var b = before != null && before.Contains(id); var a = after != null && after.Contains(id);
                if (b && !a) hidden.Add(id); else if (!b && a) shown.Add(id);
            }
            return (hidden, shown);
        }

        /// <summary>
        /// Whether a visibility change is accepted (allow_visibility_change): true accepts every one; a list accepts a change in a
        /// view it names, or of an element whose source or target workset it names (names compared ignoring case and outer spaces).
        /// </summary>
        public static bool VisibilityAllowed(bool all, ICollection<string> names, string view, string fromWorkset, string toWorkset)
        {
            if (all) return true;
            if (names == null || names.Count == 0) return false;
            bool Named(string n) => n != null && names.Any(x => string.Equals((x ?? "").Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase));
            return Named(view) || Named(fromWorkset) || Named(toWorkset);
        }

        /// <summary>Why a workset named for deletion is kept: what it still holds after the moves (types are never moved by a plan).</summary>
        public static string KeptWhy(int elements, int types) =>
            types > 0 && elements == types ? $"holds {types} type{(types == 1 ? "" : "s")} (a plan moves instances only)"
            : $"not empty after the moves ({elements} element{(elements == 1 ? "" : "s")}{(types > 0 ? $", {types} of them types" : "")})";

        /// <summary>One planned step of apply_plan.</summary>
        internal sealed class Step
        {
            public string Id, Kind;          // create | rename | move | delete | move-and-delete
            public string Workset;           // create: the new name; rename: the old name; move: the target; delete: the workset
            public string From, To, Key;     // rename: from / to; move: the group key (or "ids") and the source workset (when one)
            public long WorksetId, ToId;     // existing ids (0 = created in this plan)
            public int Elements;             // move: elements that move; delete: elements on it after the moves
            public List<long> Ids = new List<long>();
            public List<long> TypeIds = new List<long>();   // move: link / CAD import types moved with their instances
            public string Row;               // move: the line in the decision file (information only, not in the hash)
            public List<long> Followers = new List<long>(); // move: sub-elements that go with the moved elements (not in the hash)
            public int NonModel;             // delete: sketches and other non-model items that go to the receiving workset (not in the hash)
            public Dictionary<string, int> ByCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            public string Safety; public long SafetyId;   // a delete: receives anything the count missed (not in the hash)
        }

        /// <summary>create, then rename, then move, then delete.</summary>
        public static int KindRank(string kind)
        {
            switch (kind)
            {
                case "create": return 0;
                case "rename": return 1;
                case "move": return 2;
                default: return 3;
            }
        }

        /// <summary>The steps in run order (create, rename, move, delete), keeping the request's order within a kind.</summary>
        public static List<Step> Ordered(IEnumerable<Step> steps) =>
            (steps ?? Enumerable.Empty<Step>()).Select((s, i) => (s, i)).OrderBy(x => KindRank(x.s.Kind)).ThenBy(x => x.i).Select(x => x.s).ToList();

        /// <summary>
        /// The creates of a request: one step per new name, a block for a name that is empty, not valid, already a workset
        /// (or the result of a rename in the same plan) or given twice.
        /// </summary>
        public static (List<Step> Steps, List<WorksetAdminLogic.Block> Blocks) PlanCreate(IList<WorksetAdminLogic.Workset> worksets, IEnumerable<string> names, ICollection<string> renameTargets)
        {
            var steps = new List<Step>(); var blocks = new List<WorksetAdminLogic.Block>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                var name = (raw ?? "").Trim();
                var what = "create " + name;
                if (name.Length == 0) { blocks.Add(new WorksetAdminLogic.Block { What = "create", Code = "naming", Detail = "an empty workset name" }); continue; }
                if (name.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' }) >= 0) { blocks.Add(new WorksetAdminLogic.Block { What = what, Code = "naming", Detail = "a workset name cannot contain { } [ ] | ; < > ? ` ~" }); continue; }
                if (WorksetAdminLogic.IsProtected(name) || (worksets ?? new List<WorksetAdminLogic.Workset>()).Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
                { blocks.Add(new WorksetAdminLogic.Block { What = what, Code = "exists", Detail = $"a workset called {name} already exists" }); continue; }
                if (renameTargets != null && renameTargets.Contains(name)) { blocks.Add(new WorksetAdminLogic.Block { What = what, Code = "exists", Detail = $"a rename in the same plan gives the name {name}" }); continue; }
                if (!seen.Add(name)) { blocks.Add(new WorksetAdminLogic.Block { What = what, Code = "exists", Detail = $"{name} is created twice in the request" }); continue; }
                steps.Add(new Step { Id = "create:" + name, Kind = "create", Workset = name });
            }
            return (steps, blocks);
        }

        /// <summary>
        /// The elements on each workset after the moves: now, minus what moves out, plus what moves in (moves: from workset
        /// id, to workset id, count; a created target has id 0 and is not in the result).
        /// </summary>
        public static Dictionary<long, int> AfterMoves(IDictionary<long, int> now, IEnumerable<(long From, long To, int Count)> moves)
        {
            var d = new Dictionary<long, int>(now ?? new Dictionary<long, int>());
            foreach (var (from, to, count) in moves ?? Enumerable.Empty<(long, long, int)>())
            {
                if (d.ContainsKey(from)) d[from] -= count;
                if (to != 0) d[to] = (d.TryGetValue(to, out var n) ? n : 0) + count;
            }
            return d;
        }

        /// <summary>
        /// The plan's fingerprint over the whole plan: every create, rename, move (its target, its elements by id) and delete
        /// (its workset, where its elements go, its count). The same plan on the same model gives the same hash.
        /// </summary>
        public static string PlanHash(IEnumerable<Step> steps)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = (steps ?? Enumerable.Empty<Step>()).Select(s => string.Join("|",
                    KindRank(s.Kind).ToString(inv), s.Kind, s.Workset ?? "", s.WorksetId.ToString(inv), s.From ?? "", s.To ?? "", s.ToId.ToString(inv), s.Elements.ToString(inv),
                    string.Join(",", (s.Ids ?? new List<long>()).OrderBy(i => i).Select(i => i.ToString(inv))),
                    "t:" + string.Join(",", (s.TypeIds ?? new List<long>()).OrderBy(i => i).Select(i => i.ToString(inv)))))
                .OrderBy(l => l, StringComparer.Ordinal);
            return Util.Compat.Sha256Hex(string.Join("\n", lines)).ToLowerInvariant();
        }

        // ---- the after-check (register #37): what the rules asked for, measured on the model after the commit -------------

        /// <summary>
        /// Does a type name start with one of the prefixes (the office method: rules match on category, type-name prefix and
        /// level)? The type part ("Family : SB1 300x600" gives "SB1 300x600") or the whole name, ignoring case and outer spaces.
        /// </summary>
        public static bool TypePrefixMatches(string typeName, IEnumerable<string> prefixes, string familyName = null)
        {
            var list = (prefixes ?? Enumerable.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).ToList();
            if (list.Count == 0) return true;
            var full = (typeName ?? "").Trim();
            var cut = full.LastIndexOf(" : ", StringComparison.Ordinal);
            var part = cut >= 0 ? full.Substring(cut + 3).Trim() : full;
            var fam = (familyName ?? "").Trim();
            return list.Any(p => part.StartsWith(p, StringComparison.OrdinalIgnoreCase) || full.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || (fam.Length > 0 && $"{fam} : {part}".StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        private static List<string> Strings(JsonNode n) =>
            n == null ? new List<string>() : n is JsonArray a ? a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList()
            : string.IsNullOrWhiteSpace(n.ToString()) ? new List<string>() : new List<string> { n.ToString().Trim() };

        /// <summary>
        /// Does an element (as the facts name it) match one selector of an after-check target: { category: [..], level: [exact
        /// names], type: [exact "Family : Type"], typePrefix: [..], material }? An empty field matches everything; a material
        /// only rules out an element whose own class is known (concrete / steel) and differs.
        /// </summary>
        public static bool SelectorMatches(JsonObject sel, string category, string type, string level, string material)
        {
            if (sel == null) return false;
            bool In(List<string> list, string v) => list.Count == 0 || list.Any(x => string.Equals(x, (v ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            var cats = Strings(sel["category"]);
            if (cats.Count == 0) return false;   // a target always names its categories: a category-free selector would claim everything
            if (!In(cats, category)) return false;
            if (!In(Strings(sel["level"]), level)) return false;
            if (!In(Strings(sel["type"]), type)) return false;
            if (!TypePrefixMatches(type, Strings(sel["typePrefix"]))) return false;
            var m = sel["material"]?.ToString();
            if (!string.IsNullOrWhiteSpace(m) && (material == "concrete" || material == "steel") && !string.Equals(m.Trim(), material, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>The after-check targets (the server's after_check.targets) an element matches, by workset name.</summary>
        public static List<string> TargetsOf(JsonArray targets, string category, string type, string level, string material) =>
            (targets ?? new JsonArray()).OfType<JsonObject>()
                .Where(t => !string.IsNullOrWhiteSpace(t["workset"]?.ToString()) && (t["selectors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(s => SelectorMatches(s, category, type, level, material)))
                .Select(t => t["workset"].ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>A workset by name after the change: exact, then ignoring case, then ignoring outer spaces (0 = none).</summary>
        public static long WorksetIdOf(IDictionary<long, string> names, string name)
        {
            if (names == null || string.IsNullOrWhiteSpace(name)) return 0;
            foreach (Func<string, bool> eq in new Func<string, bool>[] { n => n == name, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase), n => string.Equals((n ?? "").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) })
                foreach (var kv in names) if (eq(kv.Value)) return kv.Key;
            return 0;
        }

        /// <summary>
        /// The after-check, read after the commit (pure counting; the caller re-reads the model). Per target and category: on the
        /// target now, matching but still on a source / default workset (left behind), matching but on another workset, skipped
        /// for a stated reason (borrowed, pinned, held for visibility, read-only, not yours yet in a dry run) and excluded by a
        /// decision (skipped groups). A target fails with code target-missing (it does not exist and was to receive elements),
        /// target-empty (it was to receive N and holds none of them, or holds nothing while matching elements stay behind) or
        /// left-behind (matching elements still on a source workset without a stated reason). Also what is still on the default
        /// workset, by category and by category and type. preview: a dry run's forecast (same counts, same codes).
        /// </summary>
        public static JsonObject AfterCheck(IList<(string Workset, long WorksetId, IDictionary<string, int> Planned)> targets,
            IEnumerable<(long Id, long Ws, string Category, string Type, string Key, IList<string> Targets)> elements,
            IDictionary<long, string> worksetNames, ICollection<long> sources, ICollection<long> defaults, IDictionary<long, string> skips, ICollection<string> excludeKeys, bool preview)
        {
            const int OnT = 0, Left = 1, Other = 2, Skip = 3, Excl = 4;
            var tl = (targets ?? new List<(string, long, IDictionary<string, int>)>()).GroupBy(t => (t.Workset ?? "").Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var acc = tl.ToDictionary(t => (t.Workset ?? "").Trim(), t => new SortedDictionary<string, int[]>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            var skipAgg = new SortedDictionary<string, (int Count, List<long> Ids, SortedDictionary<string, int> Cats)>(StringComparer.Ordinal);
            var defCat = new SortedDictionary<string, int>(StringComparer.Ordinal); var defType = new Dictionary<(string, string), int>();
            var skipSeen = new HashSet<long>();
            int[] Row(string t, string cat) { var d = acc[t]; if (!d.TryGetValue(cat, out var r)) d[cat] = r = new int[5]; return r; }
            foreach (var e in elements ?? Enumerable.Empty<(long, long, string, string, string, IList<string>)>())
            {
                var cat = e.Category ?? "";
                if (defaults != null && defaults.Contains(e.Ws))
                {
                    defCat[cat] = defCat.TryGetValue(cat, out var c0) ? c0 + 1 : 1;
                    var tk = (cat, e.Type ?? ""); defType[tk] = defType.TryGetValue(tk, out var c1) ? c1 + 1 : 1;
                }
                if (e.Targets == null || e.Targets.Count == 0) continue;
                var excluded = excludeKeys != null && e.Key != null && excludeKeys.Contains(e.Key);
                foreach (var tn in e.Targets.Select(x => (x ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!acc.ContainsKey(tn)) continue;
                    var t = tl.First(x => string.Equals((x.Workset ?? "").Trim(), tn, StringComparison.OrdinalIgnoreCase));
                    var r = Row(tn, cat);
                    if (t.WorksetId != 0 && e.Ws == t.WorksetId) r[OnT]++;
                    else if (excluded) r[Excl]++;
                    else if (sources != null && sources.Contains(e.Ws))
                    {
                        if (skips != null && skips.TryGetValue(e.Id, out var why) && !string.IsNullOrWhiteSpace(why))
                        {
                            r[Skip]++;
                            if (skipSeen.Add(e.Id))
                            {
                                var a = skipAgg.TryGetValue(why, out var x) ? x : (Count: 0, Ids: new List<long>(), Cats: new SortedDictionary<string, int>(StringComparer.Ordinal));
                                if (a.Ids.Count < 20) a.Ids.Add(e.Id);
                                a.Cats[cat] = a.Cats.TryGetValue(cat, out var c2) ? c2 + 1 : 1;
                                skipAgg[why] = (a.Count + 1, a.Ids, a.Cats);
                            }
                        }
                        else r[Left]++;
                    }
                    else r[Other]++;
                }
            }
            JsonObject Counts(IDictionary<string, int> d) => new JsonObject(d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));
            var outT = new JsonArray(); var failures = 0;
            foreach (var t in tl)
            {
                var name = (t.Workset ?? "").Trim();
                var planned = t.Planned ?? new Dictionary<string, int>();
                var rows = acc[name];
                foreach (var cat in planned.Keys) Row(name, cat);
                int Sum(int k) => rows.Values.Sum(v => v[k]);
                var plannedTotal = planned.Values.Sum();
                int onT = Sum(OnT), left = Sum(Left), other = Sum(Other), skip = Sum(Skip), excl = Sum(Excl);
                string code = null, why = null;
                var verb = preview ? "would" : "did";
                if (t.WorksetId == 0 && (plannedTotal > 0 || left > 0)) { code = "target-missing"; why = $"the workset {name} does not exist{(plannedTotal > 0 ? $": {plannedTotal} element(s) were to go there" : "")}"; }
                else if (onT == 0 && (plannedTotal > 0 || left > 0)) { code = "target-empty"; why = $"{name} {(preview ? "would hold" : "holds")} no matching element{(plannedTotal > 0 ? $" (the plan moves {plannedTotal} there)" : "")}; {left} matching element(s) {(preview ? "would stay" : "are still")} on the source / default workset"; }
                else if (left > 0) { code = "left-behind"; why = $"{left} matching element(s) {(preview ? "would stay" : "are still")} on the source / default workset ({string.Join(", ", rows.Where(kv => kv.Value[Left] > 0).Select(kv => $"{kv.Value[Left]} {kv.Key}"))}): the plan {verb} not move them"; }
                if (code != null) failures++;
                outT.Add(new JsonObject
                {
                    ["workset"] = name, ["found"] = t.WorksetId != 0, ["planned"] = plannedTotal, ["onTarget"] = onT, ["leftOnSource"] = left, ["onOther"] = other, ["skipped"] = skip, ["excluded"] = excl,
                    ["byCategory"] = new JsonObject(rows.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, new JsonObject
                    {
                        ["planned"] = planned.TryGetValue(kv.Key, out var pn) ? pn : 0, ["onTarget"] = kv.Value[OnT], ["leftOnSource"] = kv.Value[Left], ["onOther"] = kv.Value[Other], ["skipped"] = kv.Value[Skip], ["excluded"] = kv.Value[Excl],
                    }))),
                    ["code"] = code, ["why"] = why,
                });
            }
            var defTotal = defCat.Values.Sum();
            return new JsonObject
            {
                ["ok"] = failures == 0, ["preview"] = preview, ["failures"] = failures,
                ["read"] = preview ? "inside the dry run, after the change and before it was rolled back" : "after the commit, by re-reading the model (read-only)",
                ["targets"] = outT,
                ["skips"] = new JsonArray(skipAgg.Select(kv => (JsonNode)new JsonObject
                {
                    ["reason"] = kv.Key, ["count"] = kv.Value.Count, ["byCategory"] = Counts(kv.Value.Cats), ["sampleIds"] = new JsonArray(kv.Value.Ids.Select(i => (JsonNode)i).ToArray()),
                }).ToArray()),
                ["stillOnDefault"] = new JsonObject
                {
                    ["workset"] = defaults == null ? null : string.Join(", ", defaults.Where(d => worksetNames != null && worksetNames.ContainsKey(d)).Select(d => worksetNames[d]).Distinct()),
                    ["total"] = defTotal, ["byCategory"] = Counts(defCat),
                    ["byType"] = new JsonArray(defType.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal).Take(60)
                        .Select(kv => (JsonNode)new JsonObject { ["category"] = kv.Key.Item1, ["type"] = kv.Key.Item2, ["elements"] = kv.Value }).ToArray()),
                },
            };
        }

        /// <summary>
        /// After a run: a real apply whose after-check failed is NOT done (stage incomplete, complete false, the reason as the
        /// error and the note; the change itself stays: ONE undo step, the after-check only read); a clean apply is complete; a
        /// dry run whose forecast fails says what the apply would leave undone. Returns whether the step is complete.
        /// </summary>
        public static bool MarkComplete(JsonObject result, bool dryRun)
        {
            if (result == null) return false;
            var ac = result["afterCheck"] as JsonObject;
            var applied = result["success"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s && !(result["rolledBack"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb);
            if (ac == null || !applied) return applied && !dryRun;
            var ok = ac["ok"] is JsonValue ov && ov.TryGetValue<bool>(out var o) && o;
            var fails = (ac["targets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(t => t["code"] != null).ToList();
            var text = string.Join("; ", fails.Take(6).Select(t => $"{t["workset"]}: {t["code"]} ({t["why"]})")) + (fails.Count > 6 ? $"; +{fails.Count - 6} more" : "");
            if (dryRun)
            {
                if (!ok && result["notes"] is JsonArray notes) notes.Add($"After-check forecast: the apply would leave work undone on {fails.Count} target(s): {text}. Resolve it (fix the rules or the plan) before applying, or the apply reports the step as not done.");
                return false;
            }
            result["complete"] = ok;
            if (ok) return true;
            result["stage"] = "incomplete";
            result["error"] = $"Applied as ONE undo step, but NOT done: the after-check found {fails.Count} target(s) not done: {text}.";
            result["note"] = result["error"].ToString() + " The change stays (undo it as one step if wanted); fix the rules or the plan and run again for what is left.";
            return false;
        }
    }
}
