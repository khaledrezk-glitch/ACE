using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Util
{
    /// <summary>One Revit warning as ACE compares it: the failure definition's GUID, its description and its element ids.</summary>
    internal sealed class WarningItem
    {
        public string Guid;
        public string Text;
        public long[] Failing = new long[0];
        public long[] Additional = new long[0];

        public WarningItem() { }
        public WarningItem(string guid, string text, IEnumerable<long> failing, IEnumerable<long> additional = null)
        {
            Guid = guid; Text = text;
            Failing = (failing ?? new long[0]).ToArray();
            Additional = (additional ?? new long[0]).ToArray();
        }

        /// <summary>Failing and additional ids together (the clash solver's view of a warning).</summary>
        public long[] Ids => Failing.Concat(Additional).ToArray();
    }

    /// <summary>
    /// The "no new warnings" rule shared by every apply (G24): no Revit types, so it is unit-tested (tools/tracker-test).
    /// A warning's key is its failure definition GUID + its sorted failing ids + its sorted additional ids. The warnings
    /// after a change are the model's list after the commit and the ones Revit posted during it (ModelGuard deletes those
    /// so no dialog shows), whichever has more of a key, so a re-posted existing warning is not counted twice. A key whose
    /// count rises is new. A warning that only lost elements (same GUID, its ids a subset of one that existed) is the old
    /// one shrunk by the change, not new.
    /// </summary>
    internal static class WarningLogic
    {
        /// <summary>How many new or remaining warnings a result lists (the count is always complete).</summary>
        public const int MaxListed = 20;
        /// <summary>A named acceptance shorter than this matches a GUID only, never part of a description.</summary>
        public const int MinTextMatch = 4;

        private static string Sorted(IEnumerable<long> ids) => string.Join(",", (ids ?? new long[0]).Distinct().OrderBy(i => i));
        private static string Def(WarningItem w) => string.IsNullOrEmpty(w.Guid) ? "text:" + (w.Text ?? "") : w.Guid.Trim().ToLowerInvariant();

        public static string Key(WarningItem w) => Def(w) + "|" + Sorted(w.Failing) + "|" + Sorted(w.Additional);

        /// <summary>
        /// The keys whose count after the change is higher than before, each with one sample and how many more there are.
        /// afterTotal: the warnings after (per key the higher of the model's count and the posted count). Shared with the
        /// clash solver, which keys warnings by description and ids.
        /// </summary>
        public static List<(T Item, int Extra)> Fresh<T>(IList<T> before, IList<T> afterInModel, IList<T> posted, Func<T, string> key, out int afterTotal)
        {
            Dictionary<string, int> Count(IList<T> l)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var w in l ?? new List<T>()) { var k = key(w); d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1; }
                return d;
            }
            var had = Count(before); var inModel = Count(afterInModel); var post = Count(posted);
            var sample = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var w in (afterInModel ?? new List<T>()).Concat(posted ?? new List<T>())) { var k = key(w); if (!sample.ContainsKey(k)) sample[k] = w; }
            afterTotal = 0;
            var fresh = new List<(T, int)>();
            foreach (var k in inModel.Keys.Union(post.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var n = Math.Max(inModel.TryGetValue(k, out var m) ? m : 0, post.TryGetValue(k, out var p) ? p : 0);
                afterTotal += n;
                var h = had.TryGetValue(k, out var x) ? x : 0;
                if (n > h) fresh.Add((sample[k], n - h));
            }
            return fresh;
        }

        /// <summary>Whether a warning existed before with the same definition and all of this one's ids (it only lost elements).</summary>
        public static bool Shrunk(WarningItem w, IEnumerable<WarningItem> before)
        {
            var ids = new HashSet<long>(w.Ids);
            if (ids.Count == 0) return false;
            return before.Any(b => Def(b) == Def(w) && ids.IsSubsetOf(b.Ids) && ids.Count < new HashSet<long>(b.Ids).Count);
        }

        /// <summary>
        /// A named warning: the entry is the failure definition GUID (braces and case ignored) or a part of the description
        /// (case ignored, at least MinTextMatch characters).
        /// </summary>
        public static bool Matches(WarningItem w, string entry)
        {
            var e = (entry ?? "").Trim();
            if (e.Length == 0) return false;
            var g = e.Trim('{', '}');
            if (!string.IsNullOrEmpty(w.Guid) && string.Equals(w.Guid.Trim().Trim('{', '}'), g, StringComparison.OrdinalIgnoreCase)) return true;
            return e.Length >= MinTextMatch && (w.Text ?? "").IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public sealed class Outcome
        {
            public int Before;
            public int After;
            public List<(WarningItem Item, int Times, bool Accepted)> New = new List<(WarningItem, int, bool)>();
            public int Count => New.Sum(n => n.Times);
            public int Refused => New.Where(n => !n.Accepted).Sum(n => n.Times);
            /// <summary>An apply refuses (rolls back): a new warning the call did not accept.</summary>
            public bool Refuse => Refused > 0;
        }

        /// <summary>The new warnings of a change, each marked accepted when the call named it (or accepted all).</summary>
        public static Outcome Compare(IList<WarningItem> before, IList<WarningItem> afterInModel, IList<WarningItem> posted, IList<string> accept, bool acceptAll)
        {
            before = before ?? new List<WarningItem>();
            var o = new Outcome { Before = before.Count };
            foreach (var (w, extra) in Fresh(before, afterInModel, posted, Key, out o.After))
            {
                if (Shrunk(w, before)) continue;
                o.New.Add((w, extra, acceptAll || (accept ?? new List<string>()).Any(a => Matches(w, a))));
            }
            return o;
        }

        private static string Describe(WarningItem w, int times = 1)
        {
            var ids = w.Ids.Distinct().ToArray();
            return (w.Text ?? "(no description)") + (ids.Length > 0 ? $" (elements: {string.Join(", ", ids.Take(10))}{(ids.Length > 10 ? ", ..." : "")})" : "") + (times > 1 ? $" x{times}" : "");
        }

        private static JsonObject ItemJson(WarningItem w, int times) => new JsonObject
        {
            ["description"] = w.Text, ["guid"] = w.Guid,
            ["failing"] = new JsonArray(w.Failing.Take(50).Select(i => (JsonNode)i).ToArray()),
            ["additional"] = new JsonArray(w.Additional.Take(50).Select(i => (JsonNode)i).ToArray()),
            ["times"] = times,
        };

        /// <summary>The result's newWarnings block: counts, whether the apply refuses, and the warnings (capped).</summary>
        public static JsonObject ToJson(Outcome o, bool dryRun)
        {
            var items = new JsonArray(o.New.Take(MaxListed).Select(n => { var j = ItemJson(n.Item, n.Times); j["accepted"] = n.Accepted; return (JsonNode)j; }).ToArray());
            return new JsonObject
            {
                ["count"] = o.Count, ["accepted"] = o.Count - o.Refused, ["refused"] = o.Refused,
                ["refuse"] = o.Refuse, ["before"] = o.Before, ["after"] = o.After,
                ["items"] = items, ["more"] = Math.Max(0, o.New.Count - MaxListed),
                ["line"] = Line(o, dryRun),
            };
        }

        /// <summary>"New warnings: N" for people, or what a dry run would add and that the identical apply refuses.</summary>
        public static string Line(Outcome o, bool dryRun)
        {
            if (o.Count == 0) return "New warnings: 0.";
            var list = string.Join("; ", o.New.Take(5).Select(n => Describe(n.Item, n.Times) + (n.Accepted ? " [accepted]" : "")));
            if (o.New.Count > 5) list += $"; ... {o.New.Count - 5} more";
            var lead = dryRun ? $"Would add {o.Count} new warning{(o.Count == 1 ? "" : "s")}" : $"New warnings: {o.Count}";
            return $"{lead}: {list}." + (o.Refuse ? (dryRun ? " " + RefuseNote : "") : $" Accepted by this call ({o.Count - o.Refused}).");
        }

        public const string AcceptHint = "If the user accepts them: run the dry run again with accept_warnings: [\"<description or GUID>\", ...] " +
            "(or accept_new_warnings: true for all), then the identical call to apply.";
        private const string RefuseNote = "The identical apply refuses (new-warnings). " + AcceptHint;

        /// <summary>The error of an apply rolled back for new warnings.</summary>
        public static string RefusalError(Outcome o)
        {
            var list = string.Join("; ", o.New.Where(n => !n.Accepted).Take(5).Select(n => Describe(n.Item, n.Times)));
            return $"Rolled back: the change would add {o.Refused} new Revit warning{(o.Refused == 1 ? "" : "s")} ({list}). Nothing was changed.";
        }

        public sealed class Targets
        {
            public int Named;                 // warnings in the model before that match fixes_warnings
            public List<WarningItem> Targeted = new List<WarningItem>();   // ... of which the run changed or deleted an element
            public List<WarningItem> Remaining = new List<WarningItem>();  // warnings after, of a targeted one's definition, on its elements
            public bool Ok => Named == 0 || (Targeted.Count > 0 && Remaining.Count == 0);
        }

        /// <summary>
        /// A warning-fix run (fixes_warnings): the named warnings whose elements the run changed or deleted are the targets;
        /// any warning after the change with a target's definition and one of its elements remains. Not ok when a target
        /// remains, or when named warnings exist and the run touched none of them.
        /// </summary>
        public static Targets Targeted(IList<WarningItem> before, IList<WarningItem> afterInModel, IList<WarningItem> posted, IList<string> entries, ICollection<long> touched)
        {
            var t = new Targets();
            var named = (before ?? new List<WarningItem>()).Where(w => (entries ?? new List<string>()).Any(e => Matches(w, e))).ToList();
            t.Named = named.Count;
            var touchedSet = new HashSet<long>(touched ?? new long[0]);
            t.Targeted = named.Where(w => w.Ids.Any(touchedSet.Contains)).ToList();
            var after = (afterInModel ?? new List<WarningItem>()).Concat(posted ?? new List<WarningItem>()).GroupBy(Key).Select(g => g.First());
            t.Remaining = after.Where(a => t.Targeted.Any(b => Def(a) == Def(b) && a.Ids.Any(b.Ids.Contains))).ToList();
            return t;
        }

        public static JsonObject ToJson(Targets t, bool dryRun) => new JsonObject
        {
            ["ok"] = t.Ok, ["named"] = t.Named, ["targeted"] = t.Targeted.Count, ["remaining"] = t.Remaining.Count,
            ["untouched"] = t.Named - t.Targeted.Count,
            ["items"] = new JsonArray(t.Remaining.Take(MaxListed).Select(w => (JsonNode)ItemJson(w, 1)).ToArray()),
            ["line"] = Line(t, dryRun),
        };

        public static string Line(Targets t, bool dryRun)
        {
            if (t.Named == 0) return "Targeted warnings: none of the named warnings is in the model.";
            var untouched = t.Named - t.Targeted.Count;
            var rest = untouched > 0 ? $" ({untouched} named warning{(untouched == 1 ? "" : "s")} not touched by this run)" : "";
            if (t.Targeted.Count == 0) return $"Targeted warnings: the run touched none of the {t.Named} named warning{(t.Named == 1 ? "" : "s")}: nothing fixed.";
            if (t.Remaining.Count == 0) return $"Targeted warnings: all {t.Targeted.Count} {(dryRun ? "would be" : "are")} gone{rest}.";
            return $"Targeted warnings: {t.Remaining.Count} of {t.Targeted.Count} {(dryRun ? "would remain" : "remain")}: " +
                   string.Join("; ", t.Remaining.Take(5).Select(w => Describe(w))) + $"{rest}.";
        }
    }
}
