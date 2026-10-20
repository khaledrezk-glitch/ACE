using System;
using System.Collections.Generic;
using System.Linq;

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
    }
}
