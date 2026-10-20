using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// The plain rules of clash_resolve (no Revit types, tested in tools/clash-test): the material of an element, the join
    /// order, which fix a clash pair gets (join, switch-join, cutback, cope, or report only), where a framing member's end
    /// runs into the other element, and the guard's before / after comparison.
    /// </summary>
    internal static class ClashResolveLogic
    {
        /// <summary>
        /// The default join priority: an earlier category cuts a later one (common structural practice: foundation > column >
        /// wall > floor > beam). The vault's rules/clash-resolve.json order, passed by the server, replaces it.
        /// </summary>
        public static readonly string[] DefaultOrder =
        {
            "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming", "OST_Stairs",
        };

        /// <summary>A cutback longer than this is not a trim but a modelling question (reported, not fixed).</summary>
        public const double MaxCutbackMm = 600;

        /// <summary>A cutback may take at most this share of the member's length.</summary>
        public const double MaxCutbackShare = 0.25;

        /// <summary>Location tolerance of the guard (feet): nothing may move.</summary>
        public const double LocationTolerance = 1e-9;

        /// <summary>A dimension value may not change by more than this (feet).</summary>
        public const double DimensionTolerance = 1e-6;

        /// <summary>
        /// "concrete" | "steel" | "other" for the fix rules, from the shared classification (StructuralMaterial): precast counts
        /// as concrete; timber, masonry, other and unknown are "other" (no automatic fix).
        /// </summary>
        public static string MaterialOf(string category, string structuralType, string materialClass) => ForFix(StructuralMaterial.Classify(category, structuralType, materialClass));

        /// <summary>The fix rules' material for a shared material name.</summary>
        public static string ForFix(string material) => material == "steel" ? "steel" : material == "concrete" || material == "precast" ? "concrete" : "other";

        /// <summary>The plan's material label of a pair.</summary>
        public static string PairMaterial(string a, string b) => a == b && (a == "concrete" || a == "steel") ? a : "mixed";

        /// <summary>
        /// An order entry as an OST name: a short alias (foundation, column, wall, slab / floor, beam / framing, stair), a
        /// category name ("Structural Foundations") or an OST name; null when it is none of these.
        /// </summary>
        public static string CategoryAlias(string name)
        {
            var k = (name ?? "").Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (k.StartsWith("ost")) k = k.Substring(3);
            switch (k)
            {
                case "foundation": case "foundations": case "structuralfoundation": case "structuralfoundations": return "OST_StructuralFoundation";
                case "column": case "columns": case "structuralcolumn": case "structuralcolumns": return "OST_StructuralColumns";
                case "wall": case "walls": return "OST_Walls";
                case "slab": case "slabs": case "floor": case "floors": return "OST_Floors";
                case "beam": case "beams": case "framing": case "structuralframing": return "OST_StructuralFraming";
                case "stair": case "stairs": return "OST_Stairs";
                case "ramp": case "ramps": return "OST_Ramps";
                default: return null;
            }
        }

        // ---- trim to face (R3) -----------------------------------------------------------------------------------

        /// <summary>Default and hard limit of a trim to a face (mm): a longer one is a modelling question.</summary>
        public const double DefaultTrimMaxMm = 1000;
        /// <summary>The largest trim a rules file may allow (max_mm above this is held to it).</summary>
        public const double TrimCeilingMm = 3000;

        /// <summary>
        /// Where a member's axis (p0 + t * u, u a unit vector) meets a plane (origin, normal): t, or null when the axis runs
        /// (nearly) parallel to the plane.
        /// </summary>
        public static double? AxisPlane(double[] p0, double[] u, double[] origin, double[] normal)
        {
            double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            var nl = Math.Sqrt(Dot(normal, normal));
            if (nl < 1e-12) return null;
            var den = Dot(u, normal) / nl;
            if (Math.Abs(den) < 0.1) return null;
            var d = new[] { origin[0] - p0[0], origin[1] - p0[1], origin[2] - p0[2] };
            return Dot(d, normal) / nl / den;
        }

        /// <summary>
        /// The new end of a member trimmed to the face its end runs into: among the axis/face hits in that end's half, the one
        /// nearest the middle (the face the end enters through). Only shortens: hits at or beyond the ends are ignored. Returns
        /// (t of the new end, trim length) or null with a reason ("no face", "trim too long").
        /// </summary>
        public static (double T, double Trim)? TrimTarget(double length, IEnumerable<double> hits, int end, double maxTrim, double minTrim, out string reason)
        {
            reason = null;
            var inside = (hits ?? Enumerable.Empty<double>()).Where(t => t > minTrim && t < length - minTrim).ToList();
            var half = end == 1 ? inside.Where(t => t > length / 2).ToList() : inside.Where(t => t < length / 2).ToList();
            if (half.Count == 0) { reason = "no face"; return null; }
            var t0 = end == 1 ? half.Min() : half.Max();
            var trim = end == 1 ? length - t0 : t0;
            if (trim > maxTrim + 1e-9) { reason = "trim too long"; return null; }
            return (t0, trim);
        }

        /// <summary>
        /// Pairs of boxes (min, max) that overlap or come within tol of each other on every axis: a sweep along X, each pair
        /// once as (lower id, higher id), in id order.
        /// </summary>
        public static List<(long A, long B)> TouchingPairs(IList<(long Id, double[] Min, double[] Max)> boxes, double tol)
        {
            var list = new List<(long, long)>();
            var sorted = (boxes ?? new List<(long, double[], double[])>()).OrderBy(b => b.Min[0]).ThenBy(b => b.Id).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                var a = sorted[i];
                for (var j = i + 1; j < sorted.Count && sorted[j].Min[0] <= a.Max[0] + tol; j++)
                {
                    var b = sorted[j];
                    if (b.Min[1] > a.Max[1] + tol || a.Min[1] > b.Max[1] + tol || b.Min[2] > a.Max[2] + tol || a.Min[2] > b.Max[2] + tol) continue;
                    list.Add(a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id));
                }
            }
            return list.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
        }

        /// <summary>
        /// At most one trim per member end: of the trims planned for the same (member, end), the smallest is kept. Returns the
        /// indices of the trims kept, in input order.
        /// </summary>
        public static List<int> OneTrimPerEnd(IList<(long Member, int End, double Trim)> trims)
        {
            var keep = new List<int>();
            if (trims == null) return keep;
            foreach (var g in Enumerable.Range(0, trims.Count).GroupBy(i => (trims[i].Member, trims[i].End)))
                keep.Add(g.OrderBy(i => trims[i].Trim).ThenBy(i => i).First());
            keep.Sort();
            return keep;
        }

        /// <summary>
        /// Whether a straight member's location line (before, after: start x, y, z, end x, y, z) shows exactly the planned
        /// trims: every trimmed end at its planned point (within endTol), every other end unchanged (within keepTol).
        /// </summary>
        public static bool TrimmedEndsOk(double[] before, double[] after, IEnumerable<(int End, double[] NewEnd)> trims, double keepTol = 1e-6, double endTol = 1e-4)
        {
            if (before == null || after == null || before.Length != 6 || after.Length != 6) return false;
            var planned = new Dictionary<int, double[]>();
            foreach (var (end, p) in trims ?? Enumerable.Empty<(int, double[])>()) if (end == 0 || end == 1) planned[end] = p;
            for (var end = 0; end <= 1; end++)
            {
                var o = end * 3;
                var want = planned.TryGetValue(end, out var np) ? np : new[] { before[o], before[o + 1], before[o + 2] };
                var tol = planned.ContainsKey(end) ? endTol : keepTol;
                for (var i = 0; i < 3; i++) if (want == null || Math.Abs(want[i] - after[o + i]) > tol) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a new end point lies strictly inside the line p0..p1 (on the line within tol, and more than tol from both
        /// ends): a trim may only shorten.
        /// </summary>
        public static bool StrictlyInside(double[] p0, double[] p1, double[] np, double tol)
        {
            var d = new[] { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] };
            var len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (len < 1e-12) return false;
            var u = new[] { d[0] / len, d[1] / len, d[2] / len };
            var w = new[] { np[0] - p0[0], np[1] - p0[1], np[2] - p0[2] };
            var t = w[0] * u[0] + w[1] * u[1] + w[2] * u[2];
            var off = new[] { w[0] - t * u[0], w[1] - t * u[1], w[2] - t * u[2] };
            var dist = Math.Sqrt(off[0] * off[0] + off[1] * off[1] + off[2] * off[2]);
            return dist <= tol && t > tol && t < len - tol;
        }

        /// <summary>
        /// Whether a member's end (t = 0 or length along its axis) lies in one of the stretches of the axis inside a solid
        /// (from / to along the axis, any order), within tol.
        /// </summary>
        public static bool EndInside(double tEnd, IEnumerable<(double From, double To)> insideStretches, double tol) =>
            (insideStretches ?? Enumerable.Empty<(double, double)>()).Any(r => tEnd >= Math.Min(r.From, r.To) - tol && tEnd <= Math.Max(r.From, r.To) + tol);

        /// <summary>
        /// Whether a hosted element (its extent along the wall's axis, from / to) lies, even in part, in the stretch a trim
        /// removes: t from newEnd to length (end 1), or 0 to newEnd (end 0).
        /// </summary>
        public static bool InsertInTrimmedStretch(double length, double newEndT, int end, IEnumerable<(double From, double To)> inserts)
        {
            var (lo, hi) = end == 1 ? (newEndT, length) : (0.0, newEndT);
            return (inserts ?? Enumerable.Empty<(double, double)>()).Any(r => Math.Max(r.From, r.To) > lo && Math.Min(r.From, r.To) < hi);
        }

        /// <summary>
        /// Whether a touching pair takes part in all-touching: one element in the test's A categories and the other in its B
        /// categories (either way round), and a same-category pair only when same_category is on (the default).
        /// </summary>
        public static bool TouchPairAllowed(string catA, string catB, ICollection<string> aCats, ICollection<string> bCats, bool sameCategory)
        {
            if (!sameCategory && string.Equals(catA, catB, StringComparison.OrdinalIgnoreCase)) return false;
            bool In(ICollection<string> set, string c) => set == null || set.Count == 0 || set.Contains(c);
            return (In(aCats, catA) && In(bCats, catB)) || (In(aCats, catB) && In(bCats, catA));
        }

        /// <summary>Default chunk size of all-touching (max_pairs): the touching pairs are planned and run one chunk at a time.</summary>
        public const int DefaultMaxPairs = 500;

        /// <summary>
        /// The chunks of all-touching: the in-scope touching pairs in a fixed order (by level, lowest first, then by the two
        /// ids), cut into chunks of size. Returns the pairs' indices per chunk. The order does not depend on which pairs need a
        /// change, so the chunks stay the same after an earlier chunk was applied.
        /// </summary>
        public static List<List<int>> Chunks(IList<(int LevelRank, string Level, long A, long B)> pairs, int size)
        {
            var order = Enumerable.Range(0, pairs?.Count ?? 0)
                .OrderBy(i => pairs[i].LevelRank).ThenBy(i => pairs[i].Level ?? "", StringComparer.Ordinal)
                .ThenBy(i => Math.Min(pairs[i].A, pairs[i].B)).ThenBy(i => Math.Max(pairs[i].A, pairs[i].B)).ToList();
            var chunks = new List<List<int>>();
            var n = Math.Max(1, size);
            for (var i = 0; i < order.Count; i += n) chunks.Add(order.Skip(i).Take(n).ToList());
            return chunks;
        }

        /// <summary>
        /// What all-touching does with a touching pair: "join" (not joined, and the two really touch), "not-touching" (only
        /// near: no step), "right" (joined in the right order), "switch-join" (joined in the wrong order and the pair clashes,
        /// or its flip id was confirmed in flips) or "flip-confirm" (joined in the wrong order: listed, run only when confirmed).
        /// </summary>
        public static string TouchDecision(bool joined, bool rightOrder, bool reallyTouch, bool clashes, bool flipConfirmed)
        {
            if (!joined) return reallyTouch ? "join" : "not-touching";
            if (rightOrder) return "right";
            return clashes || flipConfirmed ? "switch-join" : "flip-confirm";
        }

        /// <summary>
        /// Whether two planar faces touch: opposite normals (within about 2 degrees) and planes no further apart than tol
        /// (normals need not be unit vectors). Whether they also overlap is checked on the faces themselves.
        /// </summary>
        public static bool FacesTouch(double[] nA, double[] oA, double[] nB, double[] oB, double tol)
        {
            double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            var la = Math.Sqrt(Dot(nA, nA)); var lb = Math.Sqrt(Dot(nB, nB));
            if (la < 1e-12 || lb < 1e-12) return false;
            if (Dot(nA, nB) / (la * lb) > -0.9994) return false;
            var d = new[] { oB[0] - oA[0], oB[1] - oA[1], oB[2] - oA[2] };
            return Math.Abs(Dot(d, nA) / la) <= tol;
        }

        /// <summary>The Revit warning a join of two elements that do not intersect leaves ("joined but do not intersect").</summary>
        public static bool IsNotIntersecting(string warning) =>
            (warning ?? "").IndexOf("do not intersect", StringComparison.OrdinalIgnoreCase) >= 0 || (warning ?? "").IndexOf("don't intersect", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The plan items a warning is about: only the steps whose two elements are both in it (none: an empty list).</summary>
        public static List<string> PlanItemsFor(long[] warningIds, IEnumerable<(string Id, long A, long B)> steps)
        {
            var ids = new HashSet<long>(warningIds ?? new long[0]);
            return (steps ?? Enumerable.Empty<(string, long, long)>()).Where(s => ids.Contains(s.A) && ids.Contains(s.B)).Select(s => s.Id).Distinct().ToList();
        }

        /// <summary>The steps that change an element of the warning (for the report only: never dropped on this basis).</summary>
        public static List<string> PlanItemsNear(long[] warningIds, IEnumerable<(string Id, long A, long B)> steps)
        {
            var ids = new HashSet<long>(warningIds ?? new long[0]);
            return (steps ?? Enumerable.Empty<(string, long, long)>()).Where(s => ids.Contains(s.A) || ids.Contains(s.B)).Select(s => s.Id).Distinct().ToList();
        }

        /// <summary>drop_failing_steps: at most this many rounds of dropping the failing steps and running the chunk again.</summary>
        public const int MaxDropRounds = 3;

        /// <summary>
        /// drop_failing_steps: the steps behind the warnings or errors that failed a run, by their element ids. Per failure, the
        /// steps changing both of its elements, else the steps changing one of them. Answer: step id -> the failures (indices)
        /// that named it; null when a failure has no ids or names no step (then the steps are found by bisecting).
        /// </summary>
        public static Dictionary<string, List<int>> FailingSteps(IList<long[]> failureIds, IList<(string Id, long A, long B)> steps)
        {
            if (failureIds == null || failureIds.Count == 0) return null;
            var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < failureIds.Count; i++)
            {
                var ids = failureIds[i];
                if (ids == null || ids.Length == 0) return null;
                var items = PlanItemsFor(ids, steps);
                if (items.Count == 0) items = PlanItemsNear(ids, steps);
                if (items.Count == 0) return null;
                foreach (var it in items) { if (!map.TryGetValue(it, out var l)) map[it] = l = new List<int>(); l.Add(i); }
            }
            return map;
        }

        /// <summary>
        /// drop_failing_steps when the ids do not tell: the failing items of a set that fails as a whole (0 .. count-1), found
        /// by halving: each half is tried alone (fails(subset) = true when that run fails) and a failing half is halved again
        /// down to single items. Null when no half fails alone (the items fail only together) or more than maxTrials runs would
        /// be needed. Deterministic: the same items, in the same order, give the same runs.
        /// </summary>
        public static List<int> Bisect(int count, Func<List<int>, bool> fails, int maxTrials, out int trials)
        {
            trials = 0;
            if (count <= 0 || fails == null) return null;
            var found = new List<int>();
            var ok = true;
            var used = 0;
            void Go(List<int> set)
            {
                if (!ok) return;
                if (set.Count == 1) { found.Add(set[0]); return; }
                if (used + 2 > maxTrials) { ok = false; return; }
                var left = set.Take(set.Count / 2).ToList(); var right = set.Skip(set.Count / 2).ToList();
                used += 2;
                var fl = fails(left); var fr = fails(right);
                if (!fl && !fr) { ok = false; return; }
                if (fl) Go(left);
                if (fr) Go(right);
            }
            Go(Enumerable.Range(0, count).ToList());
            trials = used;
            return ok ? found : null;
        }

        /// <summary>The element ids in a Revit failure text as ModelGuard writes it ("... (elements: 12, 34)"); empty when none.</summary>
        public static long[] IdsInText(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\(elements:\s*([0-9,\s]+)\)\s*$");
            if (!m.Success) return new long[0];
            return m.Groups[1].Value.Split(',').Select(s => long.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToArray();
        }

        /// <summary>exclude_ids: whether a step (its two elements) touches an excluded element.</summary>
        public static bool TouchesExcluded(long a, long b, ICollection<long> excluded) =>
            excluded != null && excluded.Count > 0 && (excluded.Contains(a) || excluded.Contains(b));

        /// <summary>A trim's plan id: "trim:&lt;trimmed id&gt;&gt;&lt;face owner id&gt;:&lt;end&gt;".</summary>
        public static string TrimId(long trimmed, long faceOwner, int end) => $"trim:{trimmed}>{faceOwner}:{end}";

        /// <summary>The position of a category in the join order, or -1 when it is not in it.</summary>
        public static int Rank(IList<string> order, string category)
        {
            if (order == null || category == null) return -1;
            for (var i = 0; i < order.Count; i++) if (string.Equals(order[i], category, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>"A" or "B": the side that cuts (earlier in the order); null for the same rank or a category not in the order.</summary>
        public static string Winner(IList<string> order, string catA, string catB)
        {
            var ra = Rank(order, catA); var rb = Rank(order, catB);
            if (ra < 0 || rb < 0 || ra == rb) return null;
            return ra < rb ? "A" : "B";
        }

        /// <summary>
        /// The side that cuts, from the elements alone, never from which one the clash test listed first: the category order,
        /// and within one category the larger volume, then the lower element id. Null only when a category is not in the order.
        /// </summary>
        public static string CutterOf(IList<string> order, Side a, Side b)
        {
            var ra = Rank(order, a.Category); var rb = Rank(order, b.Category);
            if (ra < 0 || rb < 0) return null;
            if (ra != rb) return ra < rb ? "A" : "B";
            var big = Math.Max(Math.Abs(a.Volume), Math.Abs(b.Volume));
            if (Math.Abs(a.Volume - b.Volume) > 1e-9 * Math.Max(1, big)) return a.Volume > b.Volume ? "A" : "B";
            if (a.Id != b.Id) return a.Id < b.Id ? "A" : "B";
            return null;
        }

        /// <summary>
        /// Where the overlap sits on a straight framing member, along its axis (0 = the location line's start, length = its end):
        /// end 0 or 1 with the cut that ends the member's physical geometry at the other element's face, or -1 (mid-span, the
        /// whole member, or both ends). memberMin / memberMax: the member's physical extent; interMin / interMax: the overlap's.
        /// </summary>
        public static (int End, double Cut) EndOverlap(double length, double memberMin, double memberMax, double interMin, double interMax, double tolerance)
        {
            if (length <= 0 || interMax < interMin) return (-1, 0);
            var atStart = interMin <= memberMin + tolerance;
            var atEnd = interMax >= memberMax - tolerance;
            var mid = (memberMin + memberMax) / 2;
            if (atStart && !atEnd && interMax < mid) return (0, Math.Max(0, interMax - memberMin));
            if (atEnd && !atStart && interMin > mid) return (1, Math.Max(0, memberMax - interMin));
            return (-1, 0);
        }

        /// <summary>One side of a clash pair, as the classification needs it.</summary>
        internal sealed class Side
        {
            public long Id;              // the element id (the last tie-break of the join order)
            public string Category;      // OST_ name
            public string Material;      // concrete | steel | other
            public bool Host = true;     // in the active model (links are never changed)
            public double Volume;        // cubic feet
            public int End = -2;         // straight framing (along its line) or a column (vertically): 0 / 1 = the overlap is at that end, -1 = not at an end; -2 = not measured
            public double Cut;           // feet, with End 0 / 1
            public double Length;        // feet, the location line
        }

        internal sealed class Pair
        {
            public Side A, B;
            public string ClashKind = "hard";
            public bool Joined;          // JoinGeometryUtils.AreElementsJoined
            public bool ACutsB;          // when joined: JoinGeometryUtils.IsCuttingElementInJoin(a, b)
            public double Overlap;       // cubic feet, the intersection
        }

        internal sealed class Options
        {
            public IList<string> Order = DefaultOrder;
            public bool Cutback = true, Coping = true;
            public double MaxCutFeet = MaxCutbackMm / 304.8;
        }

        /// <summary>
        /// The fix: Kind join | switch-join | cutback | cope, or null (report only, with Why and Recommendation). Changed: the
        /// side that is changed ("A" / "B"): for a join the side that cuts (null = Revit's choice), for a cutback or cope
        /// the framing member (Other is the column or supporting beam it runs into).
        /// </summary>
        internal sealed class Decision
        {
            public string Kind, Changed, Material, Why, Recommendation;
            public string Code;          // report only: the reason code (groups the report)
            public int End = -1;
            public double Cut;
        }

        private static Decision Report(string material, string code, string why, string recommendation) =>
            new Decision { Kind = null, Material = material, Code = code, Why = why, Recommendation = recommendation };

        private static bool IsColumn(Side s) => s.Category == "OST_StructuralColumns";
        private static bool IsFraming(Side s) => s.Category == "OST_StructuralFraming";

        public static Decision Classify(Pair p, Options o)
        {
            o = o ?? new Options();
            var material = PairMaterial(p.A.Material, p.B.Material);
            if (!p.A.Host || !p.B.Host)
                return Report(material, "linked", "an element is in a linked model: ACE changes the active model only", "Fix it in the linked model, or approve the clash.");
            if (p.ClashKind != "hard")
                return Report(material, "clearance", "a clearance clash, not an overlap: nothing to cut", "Move one element by hand if the clearance matters.");

            // Near-identical geometry: one of the two is redundant (a person decides which one goes).
            var big = Math.Max(p.A.Volume, p.B.Volume); var small = Math.Min(p.A.Volume, p.B.Volume);
            if (big > 0 && p.Overlap >= 0.9 * big)
                return Report(material, "duplicate", "duplicate: the two elements occupy nearly the same space", "Delete one of the two by hand, keeping the one that is tagged, dimensioned or scheduled.");
            var insideA = p.A.Volume > 0 && p.Overlap >= 0.9 * p.A.Volume;
            var insideB = p.B.Volume > 0 && p.Overlap >= 0.9 * p.B.Volume;

            if (p.A.Material == "concrete" && p.B.Material == "concrete")
            {
                if (Rank(o.Order, p.A.Category) < 0 || Rank(o.Order, p.B.Category) < 0)
                    return Report(material, "not-in-order", "a category that is not in the join order", "Add the category to order, or resolve the clash by hand.");
                var winner = CutterOf(o.Order, p.A, p.B);
                if ((insideA && winner != "A") || (insideB && winner != "B"))
                    return Report(material, "inside", "one element lies (almost) entirely inside the other: a join would cut it away", "Check which element is intended and delete or resize the other by hand.");
                if (p.Joined)
                {
                    var wrong = winner != null && (winner == "A") != p.ACutsB;
                    if (wrong) return new Decision { Kind = "switch-join", Changed = winner, Material = material, Why = $"joined, but the {(winner == "A" ? "second" : "first")} element cuts: the order says the other one wins" };
                    return Report(material, "joined-still", "already joined in the right order but still overlapping", "Check the geometry by hand (an edited profile or a sketch that runs past the joined face).");
                }
                return new Decision
                {
                    Kind = "join", Changed = winner, Material = material,
                    Why = winner == null ? "concrete with concrete, two identical elements: join geometry (Revit's order)"
                        : p.A.Category == p.B.Category ? $"concrete with concrete of the same category: join geometry, the larger element cuts"
                        : $"concrete with concrete: join geometry, the {Short(winner == "A" ? p.A.Category : p.B.Category)} cuts the {Short(winner == "A" ? p.B.Category : p.A.Category)} (order)",
                };
            }

            // Steel framing running into a column or a supporting beam: a cutback or a coping of the running member.
            foreach (var (m, other, side) in new[] { (p.A, p.B, "A"), (p.B, p.A, "B") })
            {
                if (!IsFraming(m) || m.Material != "steel") continue;
                if (!IsColumn(other) && !IsFraming(other)) continue;
                if (m.End < 0) continue;
                if (IsFraming(other) && other.End >= 0) continue;   // both members end here: a node, not a member running into a support
                if (m.Cut > o.MaxCutFeet || (m.Length > 0 && m.Cut > MaxCutbackShare * m.Length))
                    return Report(material, "too-deep", $"the steel member runs {Math.Round(m.Cut * 304.8)} mm into the {(IsColumn(other) ? "column" : "beam")}: more than a trim", "Check the member's end point and connection by hand.");
                var steelCutter = other.Material == "steel";
                string kind = null;
                if (IsColumn(other)) kind = o.Cutback ? "cutback" : (o.Coping && steelCutter ? "cope" : null);
                else kind = o.Coping && steelCutter ? "cope" : (o.Cutback ? "cutback" : null);
                if (kind == null)
                    return Report(material, "switched-off", "a steel member end runs into a support, but cutback and coping are switched off", "Allow steel.cutback or steel.coping, or trim it by hand.");
                var into = IsColumn(other) ? "column" : "supporting beam";
                return new Decision
                {
                    Kind = kind, Changed = side, Material = material, End = m.End, Cut = m.Cut,
                    Why = kind == "cope"
                        ? $"steel member {(m.End == 0 ? "start" : "end")} runs {Math.Round(m.Cut * 304.8)} mm into a steel {into}: coping to fit"
                        : $"steel member {(m.End == 0 ? "start" : "end")} runs {Math.Round(m.Cut * 304.8)} mm into a {(other.Material == "steel" ? "steel" : other.Material == "concrete" ? "concrete" : "")} {into}: physical cutback to its face (the location line stays)".Replace("  ", " "),
                };
            }

            return Unfixable(p, material);
        }

        private static string Short(string category)
        {
            switch (category)
            {
                case "OST_StructuralFraming": return "beam";
                case "OST_StructuralColumns": return "column";
                case "OST_StructuralFoundation": return "foundation";
                case "OST_Walls": return "wall";
                case "OST_Floors": return "slab";
                case "OST_Stairs": return "stair";
                case "OST_Ramps": return "ramp";
                default: return (category ?? "element").Replace("OST_", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// The pairs no fix covers, by reason, each with its own recommendation (so the report groups them): a node, a crossing,
        /// a member passing a column, a column into a beam, stacked columns, an end that cannot be measured, an unknown or
        /// non-structural material, steel bearing on concrete, a steel column base, other mixed pairs.
        /// </summary>
        private static Decision Unfixable(Pair p, string material)
        {
            var a = p.A; var b = p.B;
            bool Both(Func<Side, bool> f) => f(a) && f(b);
            bool Either(Func<Side, bool> f, out Side hit, out Side other)
            {
                if (f(a)) { hit = a; other = b; return true; }
                if (f(b)) { hit = b; other = a; return true; }
                hit = other = null; return false;
            }
            if (Both(IsFraming))
            {
                if (a.End >= 0 && b.End >= 0)
                    return Report(material, "node", "two members end at the same node: no member clearly supports the other", "Decide the connection (which member runs through) and cope or trim by hand.");
                if (a.End == -1 && b.End == -1)
                    return Report(material, "crossing", "two members cross mid-span: neither ends at the other", "A level or design question: move one member or change its offset by hand.");
                if (a.End == -2 || b.End == -2)
                    return Report(material, "end-unknown", "a member's end could not be measured (curved location line or no overlap solid)", "Trim the member by hand, or check its geometry.");
            }
            if (Either(s => s.Material == "other", out var unknown, out _))
                return Report(material, "material-unknown", $"the {Short(unknown.Category)}'s material is not steel or concrete (or its family has no structural material)",
                    "Set the family's structural material (steel or concrete) so ACE can decide, or fix it by hand.");
            if (Either(IsColumn, out var col, out var on) && IsFraming(on) && on.End == -1 && col.End >= 0)
                return Report(material, "column-into-beam", "the column's top or base runs into the beam: ACE does not cut columns back", "Adjust the column's top or base offset (or level) by hand.");
            if (Either(s => IsFraming(s) && s.Material == "steel", out var beam, out var partner) && IsColumn(partner))
            {
                if (beam.End == -1)
                    return Report(material, "through-column", "the beam passes the column mid-span instead of ending at it", "Split the beam at the column, or move one of them by hand: a cutback would cut the span.");
                if (beam.End == -2)
                    return Report(material, "end-unknown", "the beam's end could not be measured (curved location line or no overlap solid)", "Trim the beam by hand, or check its geometry.");
            }
            if (Both(IsColumn))
                return Report(material, "columns", "two columns overlap (a splice or a stacked or duplicated column)", "Check the columns' base and top levels and offsets by hand.");
            if (Either(s => s.Category == "OST_StructuralColumns" && s.Material == "steel", out _, out var footing) && footing.Category == "OST_StructuralFoundation")
                return Report(material, "column-base", "a steel column base in a foundation (usually intended: base plate, embedment)", "Approve the clash if the embedment is intended.");
            if (Either(s => s.Material == "steel", out var st, out var concrete) && concrete.Material == "concrete" && !IsColumn(concrete) && !IsFraming(concrete))
                return Report(material, "steel-on-concrete", $"a steel {Short(st.Category)} bears on or runs into a concrete {Short(concrete.Category)}", "Approve it if the bearing or embedment is intended, else lower or shift the steel member by hand.");
            if (material == "mixed")
                return Report(material, "mixed", "steel with concrete: Revit cannot join them and a cut would change the design", "Resolve it by hand (offset, profile or connection), or approve it if intended.");
            return Report(material, "other", $"no automatic fix for a {Short(a.Category)} with a {Short(b.Category)} of this material", "Resolve it by hand, or approve it if intended.");
        }

        /// <summary>
        /// A step is kept only when it clears its clash: the overlap measured after the step is below the test's tolerance.
        /// Null = keep; otherwise the skip reason (the step's sub-transaction is rolled back, the rest of the plan goes on).
        /// </summary>
        public static string DropReason(double beforeMm, double afterMm, double toleranceMm) =>
            afterMm < toleranceMm ? null : $"did not clear the clash (depth {Math.Round(beforeMm)} -> {Math.Round(afterMm)} mm)";

        /// <summary>
        /// How a cutback is made: "extension" (Start / End Extension, when the end is not joined), else "join-cutback" (Start /
        /// End Join Cutback, when the end is joined and Revit drives the extension), else null (neither can be written).
        /// </summary>
        public static string CutbackMethod(bool extensionWritable, bool joinCutbackWritable) =>
            extensionWritable ? "extension" : joinCutbackWritable ? "join-cutback" : null;

        // ---- pre-flight: blocks in plain language ----------------------------------------------------------------

        /// <summary>
        /// A plan item's id: "&lt;kind&gt;:&lt;cutterId&gt;&gt;&lt;cutId&gt;[:&lt;end&gt;]" (the direction shows who cuts whom; the end for a
        /// cutback or cope). The same for check_only, the dry run and the apply on the same model state.
        /// </summary>
        public static string PlanId(string kind, long cutter, long cut, int end) =>
            $"{kind}:{cutter}>{cut}" + ((kind == "cutback" || kind == "cope") && end >= 0 ? $":{end}" : "");

        /// <summary>Makes ids unique within one plan: a repeated id gets "#2", "#3", ... in plan order.</summary>
        public static List<string> UniqueIds(IEnumerable<string> ids)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var id in ids ?? Enumerable.Empty<string>())
            {
                seen[id] = seen.TryGetValue(id, out var n) ? n + 1 : 1;
                list.Add(seen[id] == 1 ? id : $"{id}#{seen[id]}");
            }
            return list;
        }

        /// <summary>
        /// The plan's fingerprint (sha256, lower-case hex) over its steps: kind, cutter id, cut id, end, cut in mm (0.1) and the
        /// coping cutter, one line each, sorted so the collector's order does not matter. An apply must see the same hash as
        /// its preview (expect_plan).
        /// </summary>
        public static string PlanHash(IEnumerable<(string Kind, long Cutter, long Cut, int End, double CutMm, long CopingCutter)> steps)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = (steps ?? Enumerable.Empty<(string, long, long, int, double, long)>())
                .Select(s => string.Join("|", s.Kind, s.Cutter.ToString(inv), s.Cut.ToString(inv), s.End.ToString(inv),
                    Math.Round(s.CutMm, 1).ToString("0.0", inv), s.CopingCutter.ToString(inv)))
                .OrderBy(l => l, StringComparer.Ordinal);
            return Util.Compat.Sha256Hex(string.Join("\n", lines)).ToLowerInvariant();
        }

        /// <summary>
        /// A fix must help: the clash count may not rise, no new clash may involve a touched element, and a trimmed member
        /// (cutback or cope) may not overlap the same partner at least as deeply as before. Each failure is a mismatch
        /// "fix made it worse" { element, what, before, after }.
        /// </summary>
        public static List<(long Element, string What, string Before, string After)> Worse(int beforeCount, int afterCount,
            IList<(string Key, long A, long B)> afterClashes, ICollection<string> beforeKeys, ICollection<long> touched,
            IList<(string Key, long Member, double Before, double? After)> trims)
        {
            var list = new List<(long, string, string, string)>();
            const string what = "fix made it worse";
            if (afterCount > beforeCount) list.Add((0, what + ": more clashes", beforeCount.ToString(), afterCount.ToString()));
            foreach (var c in afterClashes ?? new List<(string, long, long)>())
            {
                if (beforeKeys != null && beforeKeys.Contains(c.Key)) continue;
                var who = touched != null && touched.Contains(c.A) ? c.A : touched != null && touched.Contains(c.B) ? c.B : 0;
                if (who != 0) list.Add((who, what + ": a new clash", "none", $"{c.A} x {c.B}"));
            }
            foreach (var t in trims ?? new List<(string, long, double, double?)>())
                if (t.After.HasValue && t.After.Value >= t.Before)
                    list.Add((t.Member, what + ": still overlaps as deeply", $"{t.Before} mm", $"{t.After.Value} mm"));
            return list;
        }

        /// <summary>Why something blocks the fix, and what clears it.</summary>
        public static (string Why, string How) Explain(string code, string holder, string detail)
        {
            var who = string.IsNullOrWhiteSpace(holder) ? "another user" : holder.Trim();
            switch (code)
            {
                case "borrowed": return ($"borrowed by {who}", $"Ask {who} to synchronize with central and relinquish them, then run again (or block_on_borrowed: false to fix the rest).");
                case "central": return ("changed in the central model since your last Reload Latest", "Reload Latest (Collaborate tab), then preview again.");
                case "unreadable": return ("their worksharing state could not be read" + (string.IsNullOrEmpty(detail) ? "" : ": " + detail), "Check the connection to the central model, then run again.");
                case "group": return ("in a model group: a join or cutback fails there or reaches every copy of the group", "Fix it in the group editor by hand; ACE leaves groups alone.");
                case "option": return ("in a design option: joins with the main model fail there", "Fix it in the design option by hand, or make the option primary first.");
                case "model": return (detail ?? "the model cannot be edited", "Open an editable copy of the model (not read-only, not a family), then run again.");
                case "wrong-model": return (detail ?? "the preview was made on another model", "Preview again on the active model.");
                case "plan-changed": return (detail ?? "the model changed since the preview: the plan is not the one previewed", "Run the dry run again, then apply that preview.");
                case "version": return (detail ?? "the add-in and the server versions differ", "Install the same ACE version for the add-in and the server (build-package.ps1, then the installer), restart Revit, then run again.");
                default: return (detail ?? code, "");
            }
        }

        /// <summary>One block: { what, why, elements, sampleIds, holder, how, code }.</summary>
        public static JsonObject Block(string what, string code, string holder, string detail, int elements, IEnumerable<long> sampleIds)
        {
            var (why, how) = Explain(code, holder, detail);
            return new JsonObject
            {
                ["what"] = what, ["why"] = why, ["elements"] = elements,
                ["sampleIds"] = new JsonArray((sampleIds ?? Enumerable.Empty<long>()).Select(i => (JsonNode)i).ToArray()),
                ["holder"] = string.IsNullOrWhiteSpace(holder) ? null : holder.Trim(), ["how"] = how, ["code"] = code,
            };
        }

        /// <summary>Blocked elements grouped by reason and holder, largest first, with up to maxIds sample ids each.</summary>
        public static JsonArray Blocks(IEnumerable<(long Id, string Code, string Holder, string Detail)> held, int maxIds)
        {
            var arr = new JsonArray();
            foreach (var g in (held ?? Enumerable.Empty<(long, string, string, string)>())
                         .GroupBy(h => (h.Code ?? "", h.Holder ?? "", h.Detail ?? ""))
                         .Select(g => (g.Key, Ids: g.Select(h => h.Id).Distinct().ToList()))
                         .OrderByDescending(g => g.Ids.Count).ThenBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal))
            {
                var n = g.Ids.Count;
                arr.Add(Block($"{n} element{(n == 1 ? "" : "s")} the fix would change", g.Key.Item1, g.Key.Item2, g.Key.Item3.Length == 0 ? null : g.Key.Item3, n, g.Ids.Take(Math.Max(0, maxIds))));
            }
            return arr;
        }

        // ---- guard -----------------------------------------------------------------------------------------------

        /// <summary>What the guard records of a touched element, before and after the fix.</summary>
        internal sealed class ElementState
        {
            public long Id;
            public bool Exists = true;
            public long TypeId;
            public double[] Location;    // point x, y, z, rotation, or curve start x, y, z, end x, y, z; null = no location
            public int Tags;             // tags referencing it
            public int? Hosted;          // a trimmed wall: the elements it hosts (doors, windows, openings); null = not counted
            public Dictionary<long, string> Dimensions = new Dictionary<long, string>();   // dimension id -> "refs|element ids|values"
        }

        /// <summary>
        /// Before vs after for every touched element: { element, what, before, after } for each difference (location moved
        /// beyond 1e-9 ft, type, existence, tag count, a dimension lost, its references or count changed, or its value changed).
        /// A value change with the references intact is expected on a dimension that references a trimmed member (cutback or
        /// cope): it goes to changedValues (once per dimension), not to the mismatches.
        /// </summary>
        public static (List<(long Element, string What, string Before, string After)> Mismatches, List<(long Element, long Dimension, string Before, string After)> ChangedValues)
            Compare(IList<ElementState> before, IDictionary<long, ElementState> after, ICollection<long> trimmed = null, ICollection<long> relocated = null)
        {
            var list = new List<(long, string, string, string)>();
            var changed = new List<(long, long, string, string)>();
            var reported = new HashSet<long>();
            foreach (var b in before ?? new List<ElementState>())
            {
                if (after == null || !after.TryGetValue(b.Id, out var a) || a == null || !a.Exists)
                {
                    list.Add((b.Id, "element", "exists", "deleted"));
                    continue;
                }
                if (a.TypeId != b.TypeId) list.Add((b.Id, "type", b.TypeId.ToString(), a.TypeId.ToString()));
                if (!SameLocation(b.Location, a.Location) && !(relocated != null && relocated.Contains(b.Id))) list.Add((b.Id, "location", Text(b.Location), Text(a.Location)));
                if (a.Tags != b.Tags) list.Add((b.Id, "tags", b.Tags.ToString(), a.Tags.ToString()));
                if (b.Hosted.HasValue && a.Hosted != b.Hosted) list.Add((b.Id, "hosted", b.Hosted.ToString(), a.Hosted?.ToString() ?? "unknown"));
                foreach (var kv in b.Dimensions)
                {
                    if (!a.Dimensions.TryGetValue(kv.Key, out var now)) list.Add((b.Id, $"dimension {kv.Key}", kv.Value, "lost (deleted or no longer references the element)"));
                    else if (SameDimension(kv.Value, now)) continue;
                    else if (SameReferences(kv.Value, now) && trimmed != null && DimensionIds(kv.Value).Any(trimmed.Contains))
                    { if (reported.Add(kv.Key)) changed.Add((b.Id, kv.Key, kv.Value, now)); }
                    else list.Add((b.Id, $"dimension {kv.Key}", kv.Value, now));
                }
                foreach (var kv in a.Dimensions.Where(kv => !b.Dimensions.ContainsKey(kv.Key)))
                    list.Add((b.Id, $"dimension {kv.Key}", "none", kv.Value));
            }
            return (list, changed);
        }

        /// <summary>The same reference count and element ids (the value may differ).</summary>
        public static bool SameReferences(string before, string after)
        {
            var b = (before ?? "").Split('|'); var a = (after ?? "").Split('|');
            return b.Length == 3 && a.Length == 3 && b[0] == a[0] && b[1] == a[1];
        }

        private static IEnumerable<long> DimensionIds(string text)
        {
            var parts = (text ?? "").Split('|');
            if (parts.Length < 2) yield break;
            foreach (var p in parts[1].Split(',')) if (long.TryParse(p, out var id)) yield return id;
        }

        public static bool SameLocation(double[] a, double[] b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > LocationTolerance) return false;
            return true;
        }

        /// <summary>A dimension's fingerprint: "&lt;reference count&gt;|&lt;element ids, sorted&gt;|&lt;values in feet&gt;".</summary>
        public static string DimensionText(int references, IEnumerable<long> elementIds, IEnumerable<double?> values) =>
            $"{references}|{string.Join(",", (elementIds ?? Enumerable.Empty<long>()).OrderBy(i => i))}|{string.Join(",", (values ?? Enumerable.Empty<double?>()).Select(v => v.HasValue ? v.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-"))}";

        /// <summary>Same references and element ids, and values within 1e-6 ft.</summary>
        public static bool SameDimension(string before, string after)
        {
            if (before == after) return true;
            var b = (before ?? "").Split('|'); var a = (after ?? "").Split('|');
            if (b.Length != 3 || a.Length != 3 || b[0] != a[0] || b[1] != a[1]) return false;
            var bv = b[2].Split(','); var av = a[2].Split(',');
            if (bv.Length != av.Length) return false;
            for (var i = 0; i < bv.Length; i++)
            {
                if (bv[i] == av[i]) continue;
                if (!double.TryParse(bv[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(av[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                    || Math.Abs(x - y) > DimensionTolerance) return false;
            }
            return true;
        }

        private static string Text(double[] v) => v == null ? "none" : string.Join(", ", v.Select(x => Math.Round(x * 304.8, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        /// <summary>
        /// The warnings rule: per warning (text + element ids) the count after may not exceed the count before. After = the
        /// model's warnings after the change, or the ones Revit posted during it (ModelGuard deletes those), whichever is
        /// more, so a re-posted existing warning is not counted twice. Any rise is new and fails; a cleared warning is fine.
        /// </summary>
        public static (int After, List<string> New, bool Ok) Warnings(IList<(string Text, long[] Ids)> existing, IList<(string Text, long[] Ids)> afterInModel,
            IList<(string Text, long[] Ids)> posted) => WarningsWithIds(existing, afterInModel, posted, out _);

        /// <summary>As Warnings, also giving each new warning with its element ids (for naming the plan item behind it).</summary>
        public static (int After, List<string> New, bool Ok) WarningsWithIds(IList<(string Text, long[] Ids)> existing, IList<(string Text, long[] Ids)> afterInModel,
            IList<(string Text, long[] Ids)> posted, out List<(string Text, long[] Ids)> freshItems)
        {
            freshItems = new List<(string, long[])>();
            string Key((string Text, long[] Ids) w) => (w.Text ?? "") + "|" + string.Join(",", (w.Ids ?? new long[0]).OrderBy(i => i));
            Dictionary<string, int> Count(IList<(string Text, long[] Ids)> l)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var w in l ?? new List<(string, long[])>()) d[Key(w)] = d.TryGetValue(Key(w), out var n) ? n + 1 : 1;
                return d;
            }
            var before = Count(existing); var inModel = Count(afterInModel); var post = Count(posted);
            var sample = new Dictionary<string, (string Text, long[] Ids)>(StringComparer.Ordinal);
            foreach (var w in (afterInModel ?? new List<(string, long[])>()).Concat(posted ?? new List<(string, long[])>())) if (!sample.ContainsKey(Key(w))) sample[Key(w)] = w;
            var total = 0;
            var fresh = new List<string>();
            foreach (var k in inModel.Keys.Union(post.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var n = Math.Max(inModel.TryGetValue(k, out var m) ? m : 0, post.TryGetValue(k, out var p) ? p : 0);
                total += n;
                var had = before.TryGetValue(k, out var h) ? h : 0;
                if (n <= had) continue;
                var w = sample[k];
                freshItems.Add((w.Text, w.Ids ?? new long[0]));
                fresh.Add(w.Text + (w.Ids != null && w.Ids.Length > 0 ? $" (elements: {string.Join(", ", w.Ids)})" : "") + (n - had > 1 ? $" x{n - had}" : ""));
            }
            return (total, fresh, fresh.Count == 0);
        }
    }
}
