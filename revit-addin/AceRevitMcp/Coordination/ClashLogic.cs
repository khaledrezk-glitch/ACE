using System;
using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Coordination
{
    internal sealed class Clash
    {
        public string Key, Test, Status = "new", Note;
        public string SourceA, SourceB, CatA, CatB, NameA, NameB, Level, Kind;   // Kind: hard | clearance
        public long IdA, IdB;
        public string UA, UB;
        public double X, Y, Z;          // host coordinates, mm
        public double DepthMm;           // penetration (hard) or gap (clearance)
        public string Responsible, Reason;
        public string Mover;             // "A" or "B": the side that gives way; null = coordinate (equal priority)
        public string Group;             // the issue this clash belongs to (see ClashLogic.Issues)
        public int Reopened;             // times it came back after being resolved
        public DateTime FirstSeen, LastSeen;
    }

    /// <summary>One coordination issue: an element and everything it clashes with (e.g. one duct run through 8 beams).</summary>
    internal sealed class ClashIssue
    {
        public string Key, Test, Title, Responsible, Levels, Model;
        public long ElementId;
        public int Count;
        public double MaxDepthMm, X, Y, Z;
        public List<Clash> Clashes = new List<Clash>();
    }

    /// <summary>
    /// The plain logic of clash coordination (no Revit types, so it is tested without Revit in tools/clash-test):
    /// who gives way, clash identity, status between runs and grouping into issues.
    /// </summary>
    internal static class ClashLogic
    {
        /// <summary>The same pair of elements is one clash, whichever side it was found from.</summary>
        public static string PairKey(string test, string ua, string ub) =>
            string.CompareOrdinal(ua, ub) <= 0 ? $"{test}|{ua}|{ub}" : $"{test}|{ub}|{ua}";

        /// <summary>
        /// Responsibility from priority ranks by category name: the element that is easier to move (lower rank) gives way.
        /// A rule discipline like "ARC/STR" is resolved by the discipline of the model the element is in.
        /// </summary>
        public static void Assign(Clash c, IReadOnlyDictionary<string, (int Rank, string Discipline)> rules, string discA, string discB)
        {
            var a = c.CatA != null && rules.TryGetValue(c.CatA, out var ra) ? ra : (50, discA ?? "unknown");
            var b = c.CatB != null && rules.TryGetValue(c.CatB, out var rb) ? rb : (50, discB ?? "unknown");
            // "ARC/STR" (either discipline may own walls or floors) is settled by the model the element is in;
            // a named discipline such as "MEP (plumbing / fire)" is kept as it is.
            string Who(string ruleDisc, string modelDisc)
            {
                if (modelDisc == null || modelDisc == "unknown") return ruleDisc;
                if (ruleDisc == "unknown") return modelDisc;
                var options = ruleDisc.Split('/').Select(o => o.Trim()).ToArray();
                var ambiguous = options.Length > 1 && options.All(o => o.Length > 0 && o.Length <= 4 && o.All(char.IsLetter));
                return ambiguous && options.Contains(modelDisc, StringComparer.OrdinalIgnoreCase) ? modelDisc : ruleDisc;
            }
            if (a.Item1 == b.Item1)
            {
                var wa = Who(a.Item2, discA); var wb = Who(b.Item2, discB);
                c.Responsible = wa == wb ? $"Coordinate ({wa})" : $"Coordinate ({wa} and {wb})";
                c.Reason = "equal priority"; c.Mover = null;
                return;
            }
            var aMoves = a.Item1 < b.Item1;
            c.Mover = aMoves ? "A" : "B";
            c.Responsible = aMoves ? Who(a.Item2, discA) : Who(b.Item2, discB);
            c.Reason = aMoves ? $"{c.CatA} gives way to {c.CatB}" : $"{c.CatB} gives way to {c.CatA}";
        }

        public static bool IsOpen(Clash c) => c.Status == "new" || c.Status == "active";

        /// <summary>
        /// Merges a new run with the stored one: new / active / resolved, keeping approvals and notes.
        /// Only stored clashes inside the run's scope can become resolved (a run on one level, or one cut short by
        /// max_elements, says nothing about the rest); out-of-scope ones are kept as they were.
        /// A resolved clash that comes back is new again (and counted as reopened).
        /// </summary>
        public static List<Clash> Merge(List<Clash> previous, List<Clash> now, Func<Clash, bool> inScope, DateTime time)
        {
            var before = new Dictionary<string, Clash>();
            foreach (var p in previous) if (p.Key != null && !before.ContainsKey(p.Key)) before[p.Key] = p;
            var result = new List<Clash>();
            var nowKeys = new HashSet<string>();
            foreach (var c in now)
            {
                if (!nowKeys.Add(c.Key)) continue;
                if (before.TryGetValue(c.Key, out var p))
                {
                    c.FirstSeen = p.FirstSeen;
                    c.Note = p.Note;
                    c.Reopened = p.Reopened;
                    if (p.Status == "approved") c.Status = "approved";
                    else if (p.Status == "resolved") { c.Status = "new"; c.Reopened++; }
                    else c.Status = "active";
                }
                c.LastSeen = time;
                result.Add(c);
            }
            foreach (var p in before.Values.Where(p => !nowKeys.Contains(p.Key)))
            {
                if (inScope != null && !inScope(p)) { result.Add(p); continue; }     // not looked at this time
                if (p.Status == "resolved")
                {
                    if (time - p.LastSeen < TimeSpan.FromDays(30)) result.Add(p);      // keep a while to show progress
                    continue;
                }
                p.Status = "resolved";
                p.LastSeen = time;
                result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Groups open clashes into issues: the element that has to move and everything it hits. With equal priority,
        /// the element involved in more clashes leads. Sets Clash.Group. Largest issues first.
        /// </summary>
        public static List<ClashIssue> Issues(IEnumerable<Clash> clashes)
        {
            var open = clashes.Where(IsOpen).ToList();
            var hits = new Dictionary<string, int>();
            foreach (var c in open)
                foreach (var u in new[] { c.UA, c.UB }) if (u != null) hits[u] = hits.TryGetValue(u, out var n) ? n + 1 : 1;
            int Hits(string u) => u != null && hits.TryGetValue(u, out var n) ? n : 0;

            var issues = new Dictionary<string, ClashIssue>();
            foreach (var c in open)
            {
                var leadA = c.Mover == "A" || (c.Mover == null && (Hits(c.UA) > Hits(c.UB) || (Hits(c.UA) == Hits(c.UB) && string.CompareOrdinal(c.UA, c.UB) <= 0)));
                var (u, id, cat, name, model, _) = leadA ? (c.UA, c.IdA, c.CatA, c.NameA, c.SourceA, c.CatB) : (c.UB, c.IdB, c.CatB, c.NameB, c.SourceB, c.CatA);
                var key = $"{c.Test}|{u}";
                c.Group = key;
                if (!issues.TryGetValue(key, out var i))
                    issues[key] = i = new ClashIssue { Key = key, Test = c.Test, ElementId = id, Model = model, Title = $"{cat}: {name}" };
                i.Clashes.Add(c);
            }
            foreach (var i in issues.Values)
            {
                i.Count = i.Clashes.Count;
                i.MaxDepthMm = i.Clashes.Max(c => c.DepthMm);
                i.X = Math.Round(i.Clashes.Average(c => c.X)); i.Y = Math.Round(i.Clashes.Average(c => c.Y)); i.Z = Math.Round(i.Clashes.Average(c => c.Z));
                i.Responsible = i.Clashes.GroupBy(c => c.Responsible).OrderByDescending(g => g.Count()).First().Key;
                i.Levels = string.Join(", ", i.Clashes.Select(c => c.Level).Where(l => l != null).Distinct().OrderBy(l => l));
                var others = i.Clashes.GroupBy(c => c.Group == c.Test + "|" + c.UA ? c.CatB : c.CatA).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {g.Key}");
                i.Title += $" hits {string.Join(", ", others)}";
            }
            return issues.Values.OrderByDescending(i => i.Count).ThenByDescending(i => i.MaxDepthMm).ToList();
        }
    }
}
