using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        /// <summary>Area (m2) and volume (m3) changes below these are noise.</summary>
        public const double ToleranceM2 = 0.001, ToleranceM3 = 0.0001;

        /// <summary>Model-wide annotation counts at one moment.</summary>
        internal sealed class Annotation
        {
            public int Dimensions, SpotDimensions, Tags;
        }

        /// <summary>{ dimensions: { before, after }, spotDimensions: {...}, tags: {...} }.</summary>
        public static JsonObject AnnotationDiff(Annotation before, Annotation after)
        {
            JsonObject Pair(int? b, int? a) => new JsonObject { ["before"] = b, ["after"] = a };
            return new JsonObject
            {
                ["dimensions"] = Pair(before?.Dimensions, after?.Dimensions),
                ["spotDimensions"] = Pair(before?.SpotDimensions, after?.SpotDimensions),
                ["tags"] = Pair(before?.Tags, after?.Tags),
            };
        }

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
            public double? Area, Volume;      // m2, m3 (hosts: walls, floors, foundations, ...)
            public int Dimensions, Tags;      // dimensions (spot dimensions included) and tags that reference the element
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
            if (Differs(x.Area, y.Area, ToleranceM2)) changes.Add(Change("area", Round(x.Area), Round(y.Area)));
            if (Differs(x.Volume, y.Volume, ToleranceM3)) changes.Add(Change("volume", Round(x.Volume), Round(y.Volume)));
            if (x.Dimensions != y.Dimensions || x.Tags != y.Tags)
                changes.Add(Change("annotation", new JsonObject { ["dimensions"] = x.Dimensions, ["tags"] = x.Tags }, new JsonObject { ["dimensions"] = y.Dimensions, ["tags"] = y.Tags }));
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

        private static bool Differs(double? a, double? b, double tol) => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b.Value) > tol);
        private static JsonNode Round(double? v) => v.HasValue ? (JsonNode)Math.Round(v.Value, 3) : null;

        /// <summary>The journal lines to return: all of them up to max; above that, comment lines (') go first, then the rest is cut.</summary>
        public static (List<string> Lines, bool Truncated) JournalLines(IEnumerable<string> lines, int max)
        {
            var all = (lines ?? Enumerable.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.TrimEnd('\r')).ToList();
            if (all.Count <= max) return (all, false);
            var commands = all.Where(l => !l.TrimStart().StartsWith("'", StringComparison.Ordinal)).ToList();
            return (commands.Take(max).ToList(), true);
        }
        // ---- journal (Revit is still writing it: never trust FileInfo.Length or a directory listing) -----------------

        private static FileStream OpenShared(string file) => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        /// <summary>The journal's end as the number of bytes actually read to EOF through a shared stream (0 when it cannot be opened).</summary>
        public static long JournalEnd(string file)
        {
            if (string.IsNullOrEmpty(file)) return 0;
            try
            {
                using (var fs = OpenShared(file))
                {
                    var buf = new byte[81920]; long total = 0; int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) total += n;
                    return total;
                }
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        /// <summary>
        /// The journal lines from a byte offset to EOF, read through a shared stream. When the stream ends before the offset
        /// (the journal was rotated or replaced), the file is read from its beginning. UTF-16 (BOM FF FE) or UTF-8.
        /// Throws IOException when the file cannot be opened.
        /// </summary>
        public static List<string> JournalFrom(string file, long offset)
        {
            using (var fs = OpenShared(file))
            {
                var head = new byte[3]; int got = 0, n;
                while (got < head.Length && (n = fs.Read(head, got, head.Length - got)) > 0) got += n;
                var unicode = got >= 2 && head[0] == 0xFF && head[1] == 0xFE;
                var utf8Bom = got == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                if (offset > 0) { fs.Seek(offset - 1, SeekOrigin.Begin); if (fs.ReadByte() < 0) offset = 0; }   // shorter than the offset: rotated
                if (offset < 0) offset = 0;
                if (unicode && offset % 2 == 1) offset++;
                var start = unicode ? Math.Max(offset, 2) : utf8Bom ? Math.Max(offset, 3) : offset;
                fs.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs, unicode ? Encoding.Unicode : Encoding.UTF8, false))
                    return reader.ReadToEnd().Split('\n').ToList();
            }
        }
    }
}
