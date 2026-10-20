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
        public static List<InterferenceRow> Rows(IEnumerable<Clash> clashes, (double X, double Y, double Z) originMm,
            IDictionary<string, (string Family, string Type)> names = null)
        {
            var all = (clashes ?? Enumerable.Empty<Clash>()).ToList();
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
            return rows;
        }

        /// <summary>{ clashes, issues, open, new, active, reopened, approved, resolved } of the rows.</summary>
        public static Dictionary<string, int> Counts(IList<InterferenceRow> rows)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["clashes"] = rows.Count, ["issues"] = rows.Where(r => r.Issue.HasValue).Select(r => r.Issue.Value).Distinct().Count(),
                ["open"] = rows.Count(r => r.Status == "new" || r.Status == "active" || r.Status == "reopened"),
            };
            foreach (var s in new[] { "new", "active", "reopened", "approved", "resolved" }) d[s] = rows.Count(r => r.Status == s);
            return d;
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
