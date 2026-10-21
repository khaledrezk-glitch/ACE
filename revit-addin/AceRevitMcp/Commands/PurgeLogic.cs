using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The plain logic of purge_unused (no Revit types, tested in tools/checkset-test): the pass loop (Revit's unused list
    /// purged again until a pass finds nothing), the split of a set Revit refused to delete (halves, at most a fixed number
    /// of attempts, never one id at a time up front), the comparison of a pass with the dry run's same pass, and the plan hash.
    /// </summary>
    internal static class PurgeLogic
    {
        public sealed class Failure
        {
            public long Id;
            public string Error;
        }

        public sealed class SetResult
        {
            public List<long> Deleted = new List<long>();
            public List<Failure> Failed = new List<Failure>();
            /// <summary>Ids left untried when the attempts ran out (skipped like failures).</summary>
            public List<long> NotTried = new List<long>();
            public int Attempts;
        }

        public sealed class Pass
        {
            public int Number;
            public List<long> Found = new List<long>();
            public SetResult Result;
        }

        /// <summary>
        /// Deletes a set: the whole set first (ONE delete); when it is refused, each half on its own, and so on, until an id
        /// alone fails (listed) or maxAttempts deletes were tried (the rest is listed as not tried). tryDelete returns null
        /// when the chunk was deleted, else why not (the chunk is then unchanged: its own transaction was rolled back).
        /// </summary>
        public static SetResult DeleteSet(IList<long> ids, Func<List<long>, string> tryDelete, int maxAttempts)
        {
            var r = new SetResult();
            var queue = new Queue<List<long>>();
            if (ids != null && ids.Count > 0) queue.Enqueue(ids.ToList());
            while (queue.Count > 0)
            {
                var chunk = queue.Dequeue();
                if (r.Attempts >= maxAttempts) { r.NotTried.AddRange(chunk); continue; }
                r.Attempts++;
                var error = tryDelete(chunk);
                if (error == null) { r.Deleted.AddRange(chunk); continue; }
                if (chunk.Count == 1) { r.Failed.Add(new Failure { Id = chunk[0], Error = error }); continue; }
                var half = chunk.Count / 2;
                queue.Enqueue(chunk.GetRange(0, half));
                queue.Enqueue(chunk.GetRange(half, chunk.Count - half));
            }
            return r;
        }

        /// <summary>
        /// Up to maxPasses passes: findUnused() is Revit's unused list now; minus the excluded ids and the ids an earlier pass
        /// could not delete, it is deleted with purgeSet(pass number, set). Stops when a pass finds nothing ("nothing-left"),
        /// when a pass deletes nothing ("no-progress") or after maxPasses ("max-passes"). beforePass (the apply: each pass must
        /// find what the dry run's same pass found) sees every pass's set, an empty one too, before anything of that pass is
        /// deleted; a message stops the run ("plan-changed", the message in detail).
        /// </summary>
        public static List<Pass> RunPasses(Func<IEnumerable<long>> findUnused, Func<int, List<long>, SetResult> purgeSet, int maxPasses, ICollection<long> excluded,
            Func<int, List<long>, string> beforePass, out string stop, out string detail)
        {
            var passes = new List<Pass>();
            var skip = new HashSet<long>(excluded ?? new List<long>());
            stop = "max-passes";
            detail = null;
            for (var n = 1; n <= maxPasses; n++)
            {
                var set = NextSet(findUnused(), skip);
                var differs = beforePass?.Invoke(n, set);
                if (differs != null) { stop = "plan-changed"; detail = differs; break; }
                if (set.Count == 0) { stop = "nothing-left"; break; }
                var result = purgeSet(n, set);
                passes.Add(new Pass { Number = n, Found = set, Result = result });
                foreach (var f in result.Failed) skip.Add(f.Id);
                foreach (var id in result.NotTried) skip.Add(id);
                if (result.Deleted.Count == 0) { stop = "no-progress"; break; }
            }
            return passes;
        }

        /// <summary>The ids to purge in a pass: the unused ids not excluded, each once, in id order.</summary>
        public static List<long> NextSet(IEnumerable<long> unused, ICollection<long> excluded) =>
            (unused ?? Enumerable.Empty<long>()).Where(i => excluded == null || !excluded.Contains(i)).Distinct().OrderBy(i => i).ToList();

        /// <summary>Null when a pass found exactly what the dry run's same pass found; else what differs, for people.</summary>
        public static string Compare(int pass, ICollection<long> previewed, ICollection<long> found)
        {
            var p = new HashSet<long>(previewed ?? new List<long>());
            var f = new HashSet<long>(found ?? new List<long>());
            if (p.SetEquals(f)) return null;
            var added = f.Count(i => !p.Contains(i));
            var gone = p.Count(i => !f.Contains(i));
            return $"pass {pass} finds {f.Count} unused element{(f.Count == 1 ? "" : "s")}, the dry run's pass {pass} found {p.Count}"
                + $" ({added} not in the dry run, {gone} of the dry run's no longer unused)";
        }

        /// <summary>The change's fingerprint: what every pass purges (check_only knows pass 1 only; the dry run all) and max_passes.</summary>
        public static string PlanHash(IEnumerable<IEnumerable<long>> passes, int maxPasses)
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string> { "purge|passes=" + maxPasses.ToString(inv) };
            var n = 0;
            foreach (var pass in passes ?? Enumerable.Empty<IEnumerable<long>>())
            {
                n++;
                lines.AddRange((pass ?? Enumerable.Empty<long>()).Select(i => "p" + n.ToString(inv) + "|" + i.ToString(inv)));
            }
            return Util.Compat.Sha256Hex(string.Join("\n", lines.OrderBy(l => l, StringComparer.Ordinal))).ToLowerInvariant();
        }

        /// <summary>Counts per label, largest first, at most top labels (the rest summed as "other").</summary>
        public static List<KeyValuePair<string, int>> Top(IEnumerable<string> labels, int top)
        {
            var counts = (labels ?? Enumerable.Empty<string>()).GroupBy(l => l ?? "(none)").Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (counts.Count <= top) return counts;
            var rest = counts.Skip(top).Sum(kv => kv.Value);
            var list = counts.Take(top).ToList();
            list.Add(new KeyValuePair<string, int>("other", rest));
            return list;
        }
    }
}
