using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The plain part of capture_method (no Revit types, tested in tools/clash-test): an element's state as captured, and
    /// the per-element diff between two captures. Lengths in mm, rounded to 0.1.
    /// </summary>
    internal static class CaptureLogic
    {
        /// <summary>Changes below this (mm) are noise, not a change.</summary>
        public const double ToleranceMm = 0.5;

        internal sealed class Join
        {
            public long Id;
            public bool Cuts;          // this element cuts the other
            public string Category;    // the other element's category
        }

        internal sealed class ElemState
        {
            public long Id;
            public string UniqueId, Category, Family, Type, Level, Workset, Material;
            public double[] Point, Start, End, BoxMin, BoxMax;
            public Dictionary<string, double> Offsets = new Dictionary<string, double>(StringComparer.Ordinal);
            public Dictionary<string, double> Extensions = new Dictionary<string, double>(StringComparer.Ordinal);
            public List<Join> Joins = new List<Join>();
            public List<long> Copes = new List<long>();
            public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.Ordinal);
            public string TypeName => Family == null ? Type : $"{Family} : {Type}";
        }

        private static bool Same(double[] a, double[] b) =>
            a == null || b == null ? a == b : a.Length == b.Length && a.Zip(b, (x, y) => Math.Abs(x - y) <= ToleranceMm).All(ok => ok);

        private static JsonNode Xyz(double[] p) => p == null ? null : new JsonArray(p.Select(v => (JsonNode)Math.Round(v, 1)).ToArray());

        private static JsonNode Location(ElemState s) =>
            s.Point != null ? Xyz(s.Point) : s.Start != null ? new JsonObject { ["start"] = Xyz(s.Start), ["end"] = Xyz(s.End) } : null;

        private static JsonObject Change(string what, JsonNode before, JsonNode after) => new JsonObject { ["what"] = what, ["before"] = before, ["after"] = after };

        /// <summary>
        /// The diffs between two captures: [{ id, uniqueId, category, type, changes: [{ what, before, after, name?, end?, other?,
        /// otherCategory?, cutter? }] }], one entry per element that changed (created and deleted included), by id.
        /// </summary>
        public static JsonArray Diff(IList<ElemState> before, IList<ElemState> after)
        {
            var b = (before ?? new List<ElemState>()).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var a = (after ?? new List<ElemState>()).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var diffs = new JsonArray();
            foreach (var id in b.Keys.Union(a.Keys).OrderBy(i => i))
            {
                b.TryGetValue(id, out var x); a.TryGetValue(id, out var y);
                var s = y ?? x;
                var changes = new JsonArray();
                if (x == null) changes.Add(Change("created", null, y.TypeName));
                else if (y == null) changes.Add(Change("deleted", x.TypeName, null));
                else Compare(x, y, changes);
                if (changes.Count == 0) continue;
                diffs.Add(new JsonObject { ["id"] = id, ["uniqueId"] = s.UniqueId, ["category"] = s.Category, ["type"] = s.TypeName, ["changes"] = changes });
            }
            return diffs;
        }

        private static void Compare(ElemState x, ElemState y, JsonArray changes)
        {
            var hasLocation = x.Point != null || x.Start != null || y.Point != null || y.Start != null;
            if (hasLocation && !(Same(x.Point, y.Point) && Same(x.Start, y.Start) && Same(x.End, y.End))) changes.Add(Change("moved", Location(x), Location(y)));
            if (!hasLocation && !(Same(x.BoxMin, y.BoxMin) && Same(x.BoxMax, y.BoxMax)))
                changes.Add(Change("shape", new JsonObject { ["min"] = Xyz(x.BoxMin), ["max"] = Xyz(x.BoxMax) }, new JsonObject { ["min"] = Xyz(y.BoxMin), ["max"] = Xyz(y.BoxMax) }));

            foreach (var key in x.Offsets.Keys.Union(y.Offsets.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var hb = x.Offsets.TryGetValue(key, out var vb); var ha = y.Offsets.TryGetValue(key, out var va);
                if (hb == ha && (!hb || Math.Abs(va - vb) <= ToleranceMm)) continue;
                var c = Change("offset", hb ? (JsonNode)Math.Round(vb, 1) : null, ha ? (JsonNode)Math.Round(va, 1) : null); c["name"] = key; changes.Add(c);
            }
            foreach (var key in x.Extensions.Keys.Union(y.Extensions.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var hb = x.Extensions.TryGetValue(key, out var vb); var ha = y.Extensions.TryGetValue(key, out var va);
                if (hb == ha && (!hb || Math.Abs(va - vb) <= ToleranceMm)) continue;
                var c = Change("extension", hb ? (JsonNode)Math.Round(vb, 1) : null, ha ? (JsonNode)Math.Round(va, 1) : null);
                c["end"] = key.StartsWith("start", StringComparison.Ordinal) ? "start" : "end"; c["name"] = key; changes.Add(c);
            }
            if (!string.Equals(x.TypeName, y.TypeName, StringComparison.Ordinal)) changes.Add(Change("type", x.TypeName, y.TypeName));
            if (!string.Equals(x.Material, y.Material, StringComparison.Ordinal)) changes.Add(Change("material", x.Material, y.Material));
            if (!string.Equals(x.Workset, y.Workset, StringComparison.Ordinal)) changes.Add(Change("workset", x.Workset, y.Workset));
            if (!string.Equals(x.Level, y.Level, StringComparison.Ordinal)) { var c = Change("parameter", x.Level, y.Level); c["name"] = "Level"; changes.Add(c); }
            foreach (var key in x.Params.Keys.Union(y.Params.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                x.Params.TryGetValue(key, out var vb); y.Params.TryGetValue(key, out var va);
                if (string.Equals(vb, va, StringComparison.Ordinal)) continue;
                var c = Change("parameter", vb, va); c["name"] = key; changes.Add(c);
            }

            // Joins: added, removed, or the cutting order switched. cutter = the element that cuts (now, or before for a removed join).
            var jb = x.Joins.GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First());
            var ja = y.Joins.GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var other in jb.Keys.Union(ja.Keys).OrderBy(i => i))
            {
                jb.TryGetValue(other, out var was); ja.TryGetValue(other, out var now);
                JsonObject c;
                if (was == null) c = Change("join-added", null, now.Cuts ? "cuts" : "is cut");
                else if (now == null) c = Change("join-removed", was.Cuts ? "cuts" : "is cut", null);
                else if (was.Cuts != now.Cuts) c = Change("join-order", was.Cuts ? "cuts" : "is cut", now.Cuts ? "cuts" : "is cut");
                else continue;
                var j = now ?? was;
                c["other"] = other; c["otherCategory"] = j.Category; c["cutter"] = j.Cuts ? x.Id : other;
                changes.Add(c);
            }
            foreach (var other in y.Copes.Except(x.Copes).OrderBy(i => i)) { var c = Change("cope-added", null, other); c["other"] = other; changes.Add(c); }
            foreach (var other in x.Copes.Except(y.Copes).OrderBy(i => i)) { var c = Change("cope-removed", other, null); c["other"] = other; changes.Add(c); }
        }

        /// <summary>The journal lines to return: all of them up to max; above that, comment lines (') go first, then the rest is cut.</summary>
        public static (List<string> Lines, bool Truncated) JournalLines(IEnumerable<string> lines, int max)
        {
            var all = (lines ?? Enumerable.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.TrimEnd('\r')).ToList();
            if (all.Count <= max) return (all, false);
            var commands = all.Where(l => !l.TrimStart().StartsWith("'", StringComparison.Ordinal)).ToList();
            return (commands.Take(max).ToList(), true);
        }
    }
}
