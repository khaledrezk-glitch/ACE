using System;
using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The plain logic of the deletion probe (register #42, no Revit types, tested in tools/workset-test): which candidates
    /// delete only themselves and which take other elements with them, found by deleting them in rolled-back transactions.
    /// "Others" are elements OUTSIDE the candidate list (nobody asked to delete them): a family that takes its own types along
    /// is "only the target" when those types are candidates too. The probe is group testing, bulk first: the whole list in
    /// ONE delete; when that takes nothing outside the list, every candidate is "only the target" (one delete). Otherwise
    /// the list is split in halves, and so on, until a single candidate is left (its others are known exactly) or the cap
    /// of deletes is reached (the rest is "not probed"). A delete Revit refuses splits the same way; a single refused
    /// candidate is "refused". Assumes what a subset takes is part of what the whole set takes (a clean set has clean parts).
    /// </summary>
    internal static class DeletionProbeLogic
    {
        public const string OnlyTarget = "only-target";
        public const string TakesOthers = "takes-others";
        public const string NotProbed = "not-probed";
        public const string Refused = "refused";

        public const int DefaultMaxDeletes = 200;

        public sealed class Outcome
        {
            public long Id;
            public string Status;
            /// <summary>takes-others: the ids outside the candidate list Revit would delete with it.</summary>
            public List<long> Others = new List<long>();
            /// <summary>refused: why Revit would not delete it alone.</summary>
            public string Error;
        }

        public sealed class Result
        {
            public List<Outcome> Outcomes = new List<Outcome>();
            /// <summary>Probe deletes tried (each rolled back).</summary>
            public int Deletes;
            /// <summary>True when the cap stopped the probe before every candidate was classified.</summary>
            public bool Capped;
            public int MaxDeletes;

            public IEnumerable<Outcome> With(string status) => Outcomes.Where(o => o.Status == status);
            public int Count(string status) => Outcomes.Count(o => o.Status == status);
            /// <summary>Every id outside the list some candidate would take (each once).</summary>
            public List<long> AllOthers() => Outcomes.SelectMany(o => o.Others).Distinct().OrderBy(i => i).ToList();
        }

        /// <summary>
        /// Classifies each candidate. probeDelete(chunk) deletes the chunk in a transaction that is always rolled back and returns
        /// what Revit deleted (Document.Delete's ids) or an error (then Gone is ignored). At most maxDeletes probe deletes.
        /// </summary>
        /// <param name="notOthers">Ids that never count as others (e.g. the rest of a purge pass when a part of it is probed).</param>
        /// <param name="bulkTakesOthers">The caller already deleted the whole list once and saw others: start with the halves.</param>
        public static Result Probe(IEnumerable<long> candidates, Func<List<long>, (ICollection<long> Gone, string Error)> probeDelete, int maxDeletes = DefaultMaxDeletes,
            IEnumerable<long> notOthers = null, bool bulkTakesOthers = false)
        {
            var list = (candidates ?? Enumerable.Empty<long>()).Distinct().OrderBy(i => i).ToList();
            var all = new HashSet<long>(list);
            if (notOthers != null) all.UnionWith(notOthers);
            var r = new Result { MaxDeletes = Math.Max(0, maxDeletes) };
            var byId = new Dictionary<long, Outcome>();
            var queue = new Queue<List<long>>();
            if (list.Count > 1 && bulkTakesOthers) Split(queue, list);
            else if (list.Count > 0) queue.Enqueue(list);
            while (queue.Count > 0)
            {
                var chunk = queue.Dequeue();
                if (r.Deletes >= r.MaxDeletes)
                {
                    r.Capped = true;
                    foreach (var id in chunk) byId[id] = new Outcome { Id = id, Status = NotProbed };
                    continue;
                }
                r.Deletes++;
                (ICollection<long> Gone, string Error) got;
                try { got = probeDelete(chunk); }
                catch (Exception ex) { got = (null, string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message); }
                if (got.Error != null)
                {
                    if (chunk.Count == 1) byId[chunk[0]] = new Outcome { Id = chunk[0], Status = Refused, Error = got.Error };
                    else Split(queue, chunk);
                    continue;
                }
                var others = (got.Gone ?? new List<long>()).Where(i => !all.Contains(i)).Distinct().OrderBy(i => i).ToList();
                if (others.Count == 0) foreach (var id in chunk) byId[id] = new Outcome { Id = id, Status = OnlyTarget };
                else if (chunk.Count == 1) byId[chunk[0]] = new Outcome { Id = chunk[0], Status = TakesOthers, Others = others };
                else Split(queue, chunk);
            }
            r.Outcomes = list.Select(i => byId[i]).ToList();
            return r;
        }

        private static void Split(Queue<List<long>> queue, List<long> chunk)
        {
            var half = chunk.Count / 2;
            queue.Enqueue(chunk.GetRange(0, half));
            queue.Enqueue(chunk.GetRange(half, chunk.Count - half));
        }

        /// <summary>
        /// What goes forward: "only the target" always; "takes others" only when the call names the candidate in allowed.
        /// Held: takes others (not named), refused, not probed (the cap was reached: never deleted unseen).
        /// </summary>
        public static (List<long> Forward, List<Outcome> Held) Gate(Result r, ICollection<long> allowed)
        {
            var forward = new List<long>(); var held = new List<Outcome>();
            foreach (var o in r?.Outcomes ?? new List<Outcome>())
            {
                if (o.Status == OnlyTarget || (o.Status == TakesOthers && allowed != null && allowed.Contains(o.Id))) forward.Add(o.Id);
                else held.Add(o);
            }
            return (forward, held);
        }

        /// <summary>
        /// One candidate that takes others, for people, in the change summary's wording (ChangeTracker DeletionLines: elements
        /// apart from types, a type with its instance count): "id 12 (Furniture: TST Chair) takes 2 others: deletes 1
        /// Furniture Tags element: TST Tag; removes 1 unused Furniture type (0 instances; ...)".
        /// </summary>
        public static string OthersLine(Outcome o, string category, string name, IDictionary<long, (string Category, string Name, bool IsType, int? Instances)> info)
        {
            var target = $"id {o.Id}" + (category != null || name != null ? $" ({string.Join(": ", new[] { category, name }.Where(x => !string.IsNullOrWhiteSpace(x)))})" : "");
            if (o.Status == Refused) return $"{target}: Revit refuses to delete it alone ({o.Error})";
            if (o.Status == NotProbed) return $"{target}: not probed (the probe's cap was reached)";
            if (o.Status != TakesOthers) return $"{target}: deletes only itself";
            var parts = ChangeTracker.Recording.DeletionLines(o.Others.Select(i => info != null && info.TryGetValue(i, out var d) ? d : ("elements", (string)null, false, (int?)null)), 4)
                .Select(Lower).ToList();
            var n = o.Others.Count;
            return $"{target} takes {n} other{(n == 1 ? "" : "s")} with it: {string.Join("; ", parts)}";
        }

        private static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        /// <summary>
        /// The probe in one line: "Deletion probe: 12 of 14 delete only themselves; 2 take 3 other elements with them (2 held: name them in
        /// allow_takes_others to delete them anyway)". allowName: the parameter that lets a held candidate go (null: report only).
        /// </summary>
        public static string Summary(Result r, string allowName, ICollection<long> allowed = null)
        {
            if (r == null || r.Outcomes.Count == 0) return "Deletion probe: nothing to probe.";
            var total = r.Outcomes.Count;
            var only = r.Count(OnlyTarget); var takes = r.Count(TakesOthers); var refused = r.Count(Refused); var notProbed = r.Count(NotProbed);
            var named = allowed == null ? 0 : r.With(TakesOthers).Count(o => allowed.Contains(o.Id));
            var parts = new List<string> { only == total ? $"all {total} delete only themselves" : $"{only} of {total} delete{(only == 1 ? "s" : "")} only {(only == 1 ? "itself" : "themselves")}" };
            if (takes > 0)
            {
                var others = r.AllOthers().Count;
                var held = takes - named;
                var it = held == 1 ? "it" : "them";
                var tail = allowName == null ? "" : held == 0 ? $" (named in {allowName}: {(takes == 1 ? "it goes" : "they go")} ahead)"
                    : $" ({(named > 0 ? $"{named} named in {allowName} go{(named == 1 ? "es" : "")} ahead; " : "")}{held} held: name {it} in {allowName} to delete {it} anyway)";
                parts.Add($"{takes} take{(takes == 1 ? "s" : "")} {others} other element{(others == 1 ? "" : "s")} with {(takes == 1 ? "it" : "them")}{tail}");
            }
            if (refused > 0) parts.Add($"{refused} Revit refuses to delete alone{(allowName == null ? "" : " (held)")}");
            if (notProbed > 0) parts.Add($"{notProbed} not probed{(allowName == null ? "" : " (held)")}");
            var line = "Deletion probe: " + string.Join("; ", parts) + ".";
            if (r.Capped) line += $" The probe stopped at its cap of {r.MaxDeletes} delete{(r.MaxDeletes == 1 ? "" : "s")} (each rolled back).";
            return line;
        }
    }
}
