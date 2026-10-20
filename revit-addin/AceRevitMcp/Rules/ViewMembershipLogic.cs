using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The plain rules of view_membership_fill and export_deliverables (no Revit types, tested in tools/clash-test).
    /// </summary>
    internal static class ViewMembershipLogic
    {
        /// <summary>The order in which the reasons an element is not in the view are tested (the first that applies counts).</summary>
        public static readonly string[] Reasons = { "phase", "category", "hidden", "filter", "workset", "sectionBox", "other" };

        /// <summary>Why an element is not in the view: the first reason that applies, else "other" (crop, view range, design option, ...).</summary>
        public static string OutReason(bool phase, bool category, bool hidden, bool filter, bool workset, bool sectionBox) =>
            phase ? "phase" : category ? "category" : hidden ? "hidden" : filter ? "filter" : workset ? "workset" : sectionBox ? "sectionBox" : "other";

        /// <summary>
        /// Whether points (an element's bounding box corners, already in the section box's own coordinates) lie wholly outside
        /// the box [min, max]: their bounding box does not overlap it (a touch within tol counts as inside).
        /// </summary>
        public static bool OutsideBox(IList<double[]> corners, double[] min, double[] max, double tol = 1e-6)
        {
            if (corners == null || corners.Count == 0 || min == null || max == null) return false;
            for (var axis = 0; axis < 3; axis++)
            {
                var lo = corners.Min(c => c[axis]); var hi = corners.Max(c => c[axis]);
                if (hi < min[axis] - tol || lo > max[axis] + tol) return true;
            }
            return false;
        }

        /// <summary>The value to write (null: the element already has it). In the view: value_in, else value_out.</summary>
        public static string Target(string current, bool inView, string valueIn, string valueOut)
        {
            var want = inView ? valueIn : valueOut;
            return string.Equals(current ?? "", want ?? "", StringComparison.Ordinal) ? null : want;
        }

        /// <summary>The plan's fingerprint: the binding, both values and every element written with its side.</summary>
        public static string PlanHash(string bind, string valueIn, string valueOut, IEnumerable<long> setIn, IEnumerable<long> setOut)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { "bind|" + (bind ?? ""), "in=" + valueIn, "out=" + valueOut };
            lines.AddRange((setIn ?? Enumerable.Empty<long>()).Select(i => "i|" + i.ToString(inv)));
            lines.AddRange((setOut ?? Enumerable.Empty<long>()).Select(i => "o|" + i.ToString(inv)));
            return Util.Compat.Sha256Hex(string.Join("\n", lines.OrderBy(l => l, StringComparer.Ordinal))).ToLowerInvariant();
        }

        /// <summary>A repeatable sample of n ids (the same plan gives the same sample), in id order.</summary>
        public static List<long> Sample(IList<long> ids, int n, string seed)
        {
            if (ids == null || ids.Count <= n) return (ids ?? new List<long>()).OrderBy(i => i).ToList();
            var rnd = new Random(StableSeed(seed));
            return ids.OrderBy(i => i).OrderBy(_ => rnd.Next()).Take(n).OrderBy(i => i).ToList();
        }

        private static int StableSeed(string s)
        {
            unchecked { var h = 17; foreach (var ch in s ?? "") h = h * 31 + ch; return h; }
        }

        // ---- export_deliverables ----------------------------------------------------------------------------------

        /// <summary>A plain file name with the format's extension (added when missing), or null with a reason when it is not usable.</summary>
        public static string FileName(string given, string extension, out string problem)
        {
            problem = null;
            var name = (given ?? "").Trim();
            if (name.Length == 0) { problem = "no file name"; return null; }
            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) { problem = $"'{name}' is not a plain file name (no folders, no * ? : \" < > |)"; return null; }
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
            if (name.Length == extension.Length) { problem = "no file name"; return null; }
            return name;
        }

        /// <summary>
        /// An IFC configuration map from the vault: FileVersion (an IFCVersion name), ExportBaseQuantities,
        /// WallAndColumnSplitting (booleans), SpaceBoundaryLevel (0-2); every other key is passed on as an option string.
        /// </summary>
        public static (string Version, bool? BaseQuantities, bool? Split, int? SpaceBoundaries, Dictionary<string, string> Options, List<string> Problems) IfcConfig(JsonObject config)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var problems = new List<string>();
            string version = null; bool? bq = null, split = null; int? sb = null;
            foreach (var kv in config ?? new JsonObject())
            {
                var text = kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString();
                switch (kv.Key)
                {
                    case "FileVersion": version = text; break;
                    case "ExportBaseQuantities": if (bool.TryParse(text, out var b1)) bq = b1; else problems.Add($"ExportBaseQuantities: '{text}' is not true or false"); break;
                    case "WallAndColumnSplitting": if (bool.TryParse(text, out var b2)) split = b2; else problems.Add($"WallAndColumnSplitting: '{text}' is not true or false"); break;
                    case "SpaceBoundaryLevel": if (int.TryParse(text, out var n) && n >= 0 && n <= 2) sb = n; else problems.Add($"SpaceBoundaryLevel: '{text}' is not 0, 1 or 2"); break;
                    default: if (text != null) options[kv.Key] = text; break;
                }
            }
            return (version, bq, split, sb, options, problems);
        }
    }
}
