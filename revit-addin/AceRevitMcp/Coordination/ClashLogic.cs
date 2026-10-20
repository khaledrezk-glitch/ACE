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
        public bool Approved;            // approved once: stays approved if it disappears and comes back
        public string Cause, CausedBy;   // for new clashes: what changed since the last snapshot, and by whom
        public DateTime FirstSeen, LastSeen;
        public DateTime? Checked;        // the time of the last run that looked at it (found, or in its scope and not found)
    }

    /// <summary>
    /// The record of one clash test run, kept even when it found nothing, so a report can tell "ran, 0 clashes" from "never
    /// ran": the test, when, its filters, what the material filter left out, and the counts.
    /// </summary>
    internal sealed class ClashRun
    {
        public string Test, Level;
        public DateTime Time;
        public double ToleranceMm, ClearanceMm;
        public string[] ACategories, BCategories, AMaterials, BMaterials, ExcludeMaterials;
        public Dictionary<string, int> Filtered = new Dictionary<string, int>();
        public int Clashes, Open;
        public bool Complete = true;
        public bool Exact;               // 1.12.9 on: the clashes this run looked at carry its time exactly (Clash.Checked)
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
        /// <summary>
        /// with_model / primary_model / models: which model a name or a path means, among the models of a run (sources: name, a
        /// number per document, the document's file path; a link placed twice is one document). By path when given, else by
        /// name. Chosen: a source index of the one document matched, or -1; candidates: one source index per document when a
        /// name or path matches several documents (the caller blocks and lists them), else empty.
        /// </summary>
        public static (int Chosen, List<int> Candidates) PickModel(IList<(string Name, int Doc, string Path)> sources, string name, string path)
        {
            string P(string x) => (x ?? "").Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            var list = sources ?? new List<(string, int, string)>();
            List<int> hits;
            if (!string.IsNullOrWhiteSpace(path))
                hits = Enumerable.Range(0, list.Count).Where(i => !string.IsNullOrWhiteSpace(list[i].Path) && string.Equals(P(list[i].Path), P(path), StringComparison.OrdinalIgnoreCase)).ToList();
            else if (!string.IsNullOrWhiteSpace(name))
                hits = Enumerable.Range(0, list.Count).Where(i => string.Equals(list[i].Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            else return (-1, new List<int>());
            var perDoc = hits.GroupBy(i => list[i].Doc).Select(g => g.First()).ToList();
            return perDoc.Count == 1 ? (perDoc[0], new List<int>()) : perDoc.Count == 0 ? (-1, new List<int>()) : (-1, perDoc);
        }

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

        /// <summary>What one side of a clash did since the baseline snapshot: added | moved | retyped | null (unchanged).</summary>
        internal sealed class Side { public string Change, By, Cat, Model; }

        /// <summary>
        /// Why a new clash appeared: the side that was added, moved or retyped since the snapshot before the last
        /// clash run caused it. Both changed = both named. Neither = it existed before (first run, a new test,
        /// a changed tolerance, or a model that was not loaded).
        /// </summary>
        public static (string Cause, string By) Cause(Side a, Side b, string since)
        {
            string Text(Side s) => $"{s.Cat} {s.Change}" + (s.Model != null ? $" in {s.Model}" : "") + (s.By != null ? $" by {s.By}" : "");
            var changed = new[] { a, b }.Where(s => s?.Change != null).ToList();
            if (a == null && b == null) return (null, null);
            if (changed.Count == 0) return ($"both elements are unchanged since {since}: the clash is not from a recent change", null);
            var by = string.Join(", ", changed.Select(s => s.By).Where(x => x != null).Distinct());
            return ($"{string.Join(" and ", changed.Select(Text))} since {since}", by.Length > 0 ? by : null);
        }

        public static bool IsOpen(Clash c) => c.Status == "new" || c.Status == "active";

        /// <summary>
        /// The status people see: a clash that was resolved and is found again (same key) is stored as "new" with a reopened
        /// count (it is open, and gets a fresh cause), but shown as "reopened", never as plain "new". Stays "active" after that.
        /// </summary>
        public static string DisplayStatus(Clash c) => c.Status == "new" && c.Reopened > 0 ? "reopened" : c.Status;

        private static string FileNameOf(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            var t = p.Trim().TrimEnd('/', '\\');
            var i = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\'));
            if (i >= 0) t = t.Substring(i + 1);
            return t.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? t.Substring(0, t.Length - 4) : t;
        }

        private static string SafeName(string name)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();   // Windows' set on every platform
            return string.Concat((name ?? "model").Select(ch => bad.Contains(ch) || ch < ' ' ? '_' : ch));
        }

        /// <summary>
        /// The clash store's key, "&lt;name&gt;-&lt;hash&gt;". A workshared model: the central model's file name and a short hash
        /// of the central path (a cloud model: of its project and model GUIDs, cloudId), so every local copy of one central
        /// shares the store and two centrals with the same name do not. Otherwise (or when the central is unknown): the title and
        /// a hash of the file path, as the model's snapshots are kept.
        /// </summary>
        public static string StoreKey(bool workshared, string centralPath, string cloudId, string pathName, string title)
        {
            if (workshared && (!string.IsNullOrWhiteSpace(cloudId) || !string.IsNullOrWhiteSpace(centralPath)))
            {
                var name = FileNameOf(centralPath) ?? title ?? "model";
                var id = !string.IsNullOrWhiteSpace(cloudId) ? "cloud:" + cloudId.Trim().ToLowerInvariant() : centralPath.Trim().ToLowerInvariant();
                return $"{SafeName(name)}-{Util.Compat.Sha1Hex(id).Substring(0, 8)}";
            }
            var local = string.IsNullOrEmpty(pathName) ? title : pathName.ToLowerInvariant();
            return $"{SafeName(title)}-{Util.Compat.Sha1Hex(local).Substring(0, 8)}";
        }

        /// <summary>The 1.12.8 store key (the central's or the file's name, no hash): read once to copy its results over.</summary>
        public static string LegacyStoreKey(bool workshared, string centralPath, string pathName, string title) =>
            SafeName((workshared ? FileNameOf(centralPath) : null) ?? FileNameOf(pathName) ?? FileNameOf(title) ?? "model");

        /// <summary>
        /// The files to copy from an old store into the new one: every relative path the old store has and the new one does
        /// not (an existing file in the new store is never overwritten).
        /// </summary>
        public static List<string> FilesToCopy(IEnumerable<string> oldFiles, ICollection<string> newFiles) =>
            (oldFiles ?? Enumerable.Empty<string>()).Where(f => !(newFiles?.Contains(f) ?? false)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();

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
                    c.Approved = p.Approved || p.Status == "approved";
                    if (c.Approved && (p.Status == "approved" || p.Status == "resolved")) c.Status = "approved";
                    else if (p.Status == "resolved") { c.Status = "new"; c.Reopened++; }
                    else c.Status = "active";
                    if (c.Status != "new") { c.Cause = p.Cause; c.CausedBy = p.CausedBy; }   // a reopened clash gets a fresh cause
                }
                c.LastSeen = time; c.Checked = time;
                result.Add(c);
            }
            foreach (var p in before.Values.Where(p => !nowKeys.Contains(p.Key)))
            {
                if (inScope != null && !inScope(p)) { result.Add(p); continue; }     // not looked at this time
                p.Checked = time;                                                     // looked at: still resolved / approved now
                if (p.Status == "resolved")
                {
                    if (time - p.LastSeen < TimeSpan.FromDays(30)) result.Add(p);      // keep a while to show progress
                    continue;
                }
                if (p.Status == "approved") p.Approved = true;
                p.Status = "resolved";
                p.LastSeen = time;
                result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// After merging an imported Navisworks test: the report's status wins where it says more. Approved or resolved in
        /// Navisworks is kept; a clash Navisworks already calls active is active (not new) on its first import; an approval
        /// made in ACE stays (Merge keeps it).
        /// </summary>
        public static void ApplyReported(List<Clash> merged, IReadOnlyDictionary<string, string> reported, ICollection<string> storedKeys)
        {
            foreach (var c in merged)
            {
                if (c.Key == null || !reported.TryGetValue(c.Key, out var s) || s == null) continue;
                if (s == "approved") { c.Status = "approved"; c.Approved = true; }
                else if (s == "resolved") c.Status = "resolved";
                else if (s == "active" && c.Status == "new" && !storedKeys.Contains(c.Key)) c.Status = "active";
            }
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
