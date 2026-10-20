using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AceRevitMcp.Coordination
{
    /// <summary>One row of an interference report: one clash, with its issue.</summary>
    internal sealed class InterferenceRow
    {
        public int? Issue;               // 1-based, open issues in ClashLogic.Issues order; null for approved / resolved clashes
        public string IssueTitle, IssueKey, ClashKey, Test, Status, Level, Responsible;
        public long AId, BId;
        public string AModel, ACategory, AFamily, AType, BModel, BCategory, BFamily, BType;
        public double DepthMm;
        public double X, Y, Z;           // project coordinates (relative to the project base point), metres
        public bool Rechecked = true;    // false: a stored result the latest run of its test did not look at (kept out of every open count)
    }

    /// <summary>
    /// The plain logic of the interference report (no Revit types, tested in tools/clash-test): one row per clash, grouped by
    /// issue (open issues first, largest first, deepest clash first within an issue; then approved, then resolved clashes),
    /// with the status people see (new / active / reopened / approved / resolved) and project coordinates in metres.
    /// </summary>
    internal static class InterferenceLogic
    {
        /// <summary>"Family : Type" (ACE's element name for family instances) into its parts; any other name is the type.</summary>
        public static (string Family, string Type) SplitName(string name)
        {
            if (string.IsNullOrEmpty(name)) return (null, null);
            var i = name.IndexOf(" : ", StringComparison.Ordinal);
            return i < 0 ? (null, name) : (name.Substring(0, i), name.Substring(i + 3));
        }

        /// <param name="originMm">The project base point in the host's internal coordinates (mm): subtracted from each clash point.</param>
        /// <param name="names">Optional: family and type by element unique id, read from the model; else from the stored names.</param>
        /// <param name="latestRuns">Optional: the latest run record per test (LatestRuns); clashes that run did not look at come last, rechecked: false.</param>
        public static List<InterferenceRow> Rows(IEnumerable<Clash> clashes, (double X, double Y, double Z) originMm,
            IDictionary<string, (string Family, string Type)> names = null, IDictionary<string, ClashRun> latestRuns = null)
        {
            var given = (clashes ?? Enumerable.Empty<Clash>()).ToList();
            var all = given.Where(c => Rechecked(c, latestRuns)).ToList();
            var stored = given.Where(c => !Rechecked(c, latestRuns)).ToList();
            var issues = ClashLogic.Issues(all);
            double M(double mm, double origin) => Math.Round((mm - origin) / 1000.0, 3);
            (string, string) Names(string uid, string name) =>
                uid != null && names != null && names.TryGetValue(uid, out var n) ? n : SplitName(name);
            InterferenceRow Row(Clash c, int? no, ClashIssue issue)
            {
                var (af, at) = Names(c.UA, c.NameA);
                var (bf, bt) = Names(c.UB, c.NameB);
                return new InterferenceRow
                {
                    Issue = no, IssueTitle = issue?.Title, IssueKey = issue?.Key, ClashKey = c.Key, Test = c.Test, Status = ClashLogic.DisplayStatus(c),
                    Level = c.Level, Responsible = c.Responsible,
                    AId = c.IdA, AModel = c.SourceA, ACategory = c.CatA, AFamily = af, AType = at,
                    BId = c.IdB, BModel = c.SourceB, BCategory = c.CatB, BFamily = bf, BType = bt,
                    DepthMm = c.DepthMm, X = M(c.X, originMm.X), Y = M(c.Y, originMm.Y), Z = M(c.Z, originMm.Z),
                };
            }
            var rows = new List<InterferenceRow>();
            for (var i = 0; i < issues.Count; i++)
                foreach (var c in issues[i].Clashes.OrderByDescending(c => c.DepthMm).ThenBy(c => c.Key, StringComparer.Ordinal))
                    rows.Add(Row(c, i + 1, issues[i]));
            foreach (var status in new[] { "approved", "resolved" })
                foreach (var c in all.Where(c => c.Status == status).OrderBy(c => c.Test, StringComparer.Ordinal).ThenBy(c => c.Level, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal))
                    rows.Add(Row(c, null, null));
            foreach (var c in stored.OrderBy(c => c.Test, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal))
            {
                var r = Row(c, null, null);
                r.Rechecked = false;
                rows.Add(r);
            }
            return rows;
        }

        /// <summary>The latest run record per test (test names case-insensitive).</summary>
        public static Dictionary<string, ClashRun> LatestRuns(IEnumerable<ClashRun> runs)
        {
            var d = new Dictionary<string, ClashRun>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in runs ?? Enumerable.Empty<ClashRun>())
                if (r?.Test != null && (!d.TryGetValue(r.Test, out var t) || r.Time > t.Time)) d[r.Test] = r;
            return d;
        }

        /// <summary>
        /// Whether the latest run of the clash's test looked at it: found by it, or in its scope (models, level, categories)
        /// and not found, so still resolved or approved as of that run. A run record from 1.12.9 on (Exact) matches the time
        /// it stamped exactly; an older record, saved a little after its merge, within 2 minutes. A stored result from another
        /// model, level or scope keeps an older time: it stays stored but is never counted. A test without a run record (an
        /// import, or no record yet) counts as rechecked.
        /// </summary>
        public static bool Rechecked(Clash c, IDictionary<string, ClashRun> latestRuns)
        {
            if (latestRuns == null || c.Test == null || !latestRuns.TryGetValue(c.Test, out var run) || run == null) return true;
            if (run.Exact) return c.Checked == run.Time || c.LastSeen == run.Time;
            return c.LastSeen >= run.Time.AddMinutes(-2) || (c.Checked.HasValue && c.Checked.Value >= run.Time.AddMinutes(-2));
        }

        /// <summary>The stored results a run did not look at: { count, open, models } (the models they come from).</summary>
        public static (int Count, int Open, List<string> Models) Stored(IEnumerable<Clash> notRechecked)
        {
            var list = (notRechecked ?? Enumerable.Empty<Clash>()).ToList();
            var models = list.SelectMany(c => new[] { c.SourceA, c.SourceB }).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            return (list.Count, list.Count(ClashLogic.IsOpen), models);
        }

        /// <summary>{ clashes, issues, open, new, active, reopened, approved, resolved } of the rows.</summary>
        public static Dictionary<string, int> Counts(IList<InterferenceRow> rows)
        {
            var re = rows.Where(r => r.Rechecked).ToList();
            var d = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["clashes"] = re.Count, ["issues"] = re.Where(r => r.Issue.HasValue).Select(r => r.Issue.Value).Distinct().Count(),
                ["open"] = re.Count(r => r.Status == "new" || r.Status == "active" || r.Status == "reopened"),
            };
            foreach (var s in new[] { "new", "active", "reopened", "approved", "resolved" }) d[s] = re.Count(r => r.Status == s);
            return d;   // stored results (rechecked: false rows) are counted once, in the answer's top-level stored

        }

        /// <summary>The run's date: the latest stored run of these tests, else the latest time a clash was seen (yyyy-MM-ddTHH:mm:ss).</summary>
        public static string RunDate(IEnumerable<Clash> clashes, IEnumerable<ClashRun> runs = null)
        {
            var times = (runs ?? Enumerable.Empty<ClashRun>()).Select(r => r.Time).ToList();
            if (times.Count == 0) times = (clashes ?? Enumerable.Empty<Clash>()).Select(c => c.LastSeen).ToList();
            return times.Count == 0 ? null : times.Max().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Why there is nothing to report, or null: an error only when the test (or any test) was never run. A stored run
        /// with zero clashes is a report with no rows.
        /// </summary>
        public static string NothingToReport(string test, int clashes, int runs, string model)
        {
            if (clashes > 0 || runs > 0) return null;
            return test == null
                ? $"No clash results for {model} yet. Run a clash test first (run_clash_test), or call again with rerun: true."
                : $"The test '{test}' was never run on {model}. Run it first (run_clash_test with the same test name and filters), or call again with rerun: true.";
        }

        /// <summary>The stored runs for a report: the named test's, or every test's when no test is named (latest first).</summary>
        public static List<ClashRun> RunsFor(IEnumerable<ClashRun> runs, string test) =>
            (runs ?? Enumerable.Empty<ClashRun>()).Where(r => test == null || string.Equals(r.Test, test, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Time).ToList();
    }
}
