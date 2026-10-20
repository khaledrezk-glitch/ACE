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

        /// <summary>The run's date: the latest time a clash was seen, as yyyy-MM-ddTHH:mm:ss.</summary>
        public static string RunDate(IEnumerable<Clash> clashes)
        {
            var list = (clashes ?? Enumerable.Empty<Clash>()).ToList();
            return list.Count == 0 ? null : list.Max(c => c.LastSeen).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
