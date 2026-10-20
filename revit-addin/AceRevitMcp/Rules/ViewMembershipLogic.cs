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

        /// <summary>A file stem from a model title: characters a file name cannot hold become "_"; "model" when nothing is left.</summary>
        public static string FileStem(string title)
        {
            var bad = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var t = new string((title ?? "").Trim().Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return t.Length == 0 ? "model" : t;
        }

        /// <summary>
        /// One IFC setup saved in the model by the IFC exporter (extensible storage): its fields as text. A string field that holds a
        /// JSON object (the newer schemas) is expanded into its properties; a string map (the older schema) gives its entries; any
        /// other string field is kept under its field name.
        /// </summary>
        public static Dictionary<string, string> IfcStored(IEnumerable<(string Field, string Value)> strings, IEnumerable<KeyValuePair<string, string>> map)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in map ?? Enumerable.Empty<KeyValuePair<string, string>>()) if (kv.Key != null) d[kv.Key] = kv.Value;
            foreach (var (field, value) in strings ?? Enumerable.Empty<(string, string)>())
            {
                var v = (value ?? "").Trim();
                JsonObject o = null;
                if (v.StartsWith("{", StringComparison.Ordinal)) try { o = JsonNode.Parse(v) as JsonObject; } catch { o = null; }
                if (o == null) { d[field] = value; continue; }
                foreach (var p in o) d[p.Key] = p.Value is JsonValue jv ? jv.ToString() : p.Value?.ToJsonString();
            }
            return d;
        }

        /// <summary>
        /// A saved IFC setup as the config IfcConfig reads: IFCVersion > FileVersion, SpaceBoundaries > SpaceBoundaryLevel,
        /// SplitWallsAndColumns > WallAndColumnSplitting, ExportBaseQuantities as is; Name and the exporter's bookkeeping
        /// (IsBuiltIn, IsInSession) left out; every other field passes on by its stored name (IFCExportOptions.AddOption).
        /// </summary>
        public static JsonObject IfcSavedConfig(IDictionary<string, string> stored)
        {
            var o = new JsonObject();
            foreach (var kv in stored ?? new Dictionary<string, string>())
            {
                if (kv.Value == null) continue;
                switch (kv.Key)
                {
                    case "Name": case "IsBuiltIn": case "IsInSession": break;
                    case "IFCVersion": o["FileVersion"] = kv.Value; break;
                    case "SpaceBoundaries": o["SpaceBoundaryLevel"] = kv.Value; break;
                    case "SplitWallsAndColumns": o["WallAndColumnSplitting"] = kv.Value.ToLowerInvariant(); break;
                    case "ExportBaseQuantities": o["ExportBaseQuantities"] = kv.Value.ToLowerInvariant(); break;
                    default: o[kv.Key] = kv.Value; break;
                }
            }
            return o;
        }

        /// <summary>The IFC exporter's display names of the IFC versions, and the IFCVersion enum name each stands for.</summary>
        public static readonly (string Display, string Enum)[] IfcVersionNames =
        {
            ("IFC 2x2 Coordination View", "IFC2x2"), ("IFC 2x2 Singapore BCA e-Plan Check", "IFCBCA"),
            ("IFC 2x3 Coordination View", "IFC2x3"), ("IFC 2x3 Coordination View 2.0", "IFC2x3CV2"),
            ("IFC 2x3 GSA Concept Design BIM 2010", "IFCCOBIE"), ("IFC 2x3 Basic FM Handover View", "IFC2x3BFM"),
            ("IFC 2x3 Extended FM Handover View", "IFC2x3FM"), ("IFC 2x3 COBie 2.4 Design Deliverable", "IFC2x3FM"),
            ("IFC4 Reference View", "IFC4RV"), ("IFC4 Design Transfer View", "IFC4DTV"), ("IFC4", "IFC4"), ("IFC 4", "IFC4"),
            ("IFC4x3", "IFC4x3"), ("IFC 4x3", "IFC4x3"), ("IFC-SG", "IFCSG"), ("Default", "Default"),
        };

        /// <summary>
        /// An IFC version as the IFCVersion enum name: an enum name as is (known: the names this Revit has), a display name from
        /// the exporter (spaces, dots, dashes and case ignored), else null. A number is left to the caller (the enum value).
        /// </summary>
        public static string IfcVersionName(string text, ICollection<string> enumNames)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            var exact = enumNames?.FirstOrDefault(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            string Norm(string x) => new string((x ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var key = Norm(t);
            var hit = IfcVersionNames.FirstOrDefault(p => Norm(p.Display) == key);
            if (hit.Enum == null || (enumNames != null && !enumNames.Contains(hit.Enum, StringComparer.OrdinalIgnoreCase))) return null;
            return hit.Enum;
        }

        /// <summary>A sheet asked for by number (first) or by name.</summary>
        public static bool SheetMatches(string asked, string number, string name)
        {
            var a = (asked ?? "").Trim();
            return a.Length > 0 && (string.Equals(a, (number ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(a, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A paper size by name (ISO A0-A4, either orientation, within 2 mm), else "W x H mm".</summary>
        public static string PaperName(double widthMm, double heightMm)
        {
            var iso = new (string Name, double W, double H)[] { ("ISO A0", 1189, 841), ("ISO A1", 841, 594), ("ISO A2", 594, 420), ("ISO A3", 420, 297), ("ISO A4", 297, 210) };
            var big = Math.Max(widthMm, heightMm); var small = Math.Min(widthMm, heightMm);
            foreach (var p in iso) if (Math.Abs(big - p.W) <= 2 && Math.Abs(small - p.H) <= 2) return p.Name;
            return $"{Math.Round(widthMm)} x {Math.Round(heightMm)} mm";
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
