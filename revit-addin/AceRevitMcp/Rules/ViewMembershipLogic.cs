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

        /// <summary>The paper formats a DWF / DWFx export takes (ExportPaperFormat names), short and long side in mm.</summary>
        public static readonly (string Enum, double Short, double Long)[] PaperFormats =
        {
            ("ISO_A0", 841, 1189), ("ISO_A1", 594, 841), ("ISO_A2", 420, 594), ("ISO_A3", 297, 420), ("ISO_A4", 210, 297),
            ("ISO_B1", 707, 1000), ("ISO_B2", 500, 707), ("ISO_B3", 353, 500), ("ISO_B4", 250, 353),
            ("ANSI_A", 215.9, 279.4), ("ANSI_B", 279.4, 431.8), ("ANSI_C", 431.8, 558.8), ("ANSI_D", 558.8, 863.6), ("ANSI_E", 863.6, 1117.6),
            ("ARCH_A", 228.6, 304.8), ("ARCH_B", 304.8, 457.2), ("ARCH_C", 457.2, 609.6), ("ARCH_D", 609.6, 914.4), ("ARCH_E", 914.4, 1219.2),
            ("ARCH_E1", 762, 1066.8), ("ARCH_E2", 660.4, 965.2), ("ARCH_E3", 685.8, 990.6),
        };

        /// <summary>
        /// The paper format of a sheet of this size (mm, either orientation): the nearest format within tol on both sides, and
        /// whether it stands upright (height above width). Null when no format fits: the export would scale the sheet.
        /// </summary>
        public static (string Enum, bool Portrait)? PaperFormatFor(double widthMm, double heightMm, double tolMm = 2)
        {
            if (!(widthMm > 0) || !(heightMm > 0)) return null;
            var small = Math.Min(widthMm, heightMm); var big = Math.Max(widthMm, heightMm);
            var best = PaperFormats.Where(p => Math.Abs(p.Short - small) <= tolMm && Math.Abs(p.Long - big) <= tolMm)
                .OrderBy(p => Math.Abs(p.Short - small) + Math.Abs(p.Long - big)).Select(p => p.Enum).FirstOrDefault();
            return best == null ? ((string, bool)?)null : (best, heightMm > widthMm);
        }

        /// <summary>Whether two paper sizes (mm) are the same within tol, in either orientation.</summary>
        public static bool SameSize(double w1, double h1, double w2, double h2, double tolMm = 2) =>
            (Math.Abs(w1 - w2) <= tolMm && Math.Abs(h1 - h2) <= tolMm) || (Math.Abs(w1 - h2) <= tolMm && Math.Abs(h1 - w2) <= tolMm);

        /// <summary>
        /// The pages of a DWFx (an XPS package, a zip): every FixedPage part's Width and Height (1/96 inch) in mm, in reading
        /// order (the FixedDocument parts' PageContent order; pages no document lists follow, by part name in natural order);
        /// and every ePlot Paper element's size (its width / height attributes, in mm unless its units say inches).
        /// </summary>
        public static (List<(string Part, double WidthMm, double HeightMm)> Pages, List<(double WidthMm, double HeightMm)> Papers) DwfxPages(System.IO.Stream package)
        {
            var pages = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var papers = new List<(double, double)>();
            double Num(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            string Norm(string part) => "/" + part.Replace('\\', '/').TrimStart('/');
            using (var zip = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Read, true))
                foreach (var entry in zip.Entries.OrderBy(e => e.FullName, NaturalOrder))
                {
                    var name = entry.FullName;
                    var isPage = name.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase);
                    var isDoc = name.EndsWith(".fdoc", StringComparison.OrdinalIgnoreCase);
                    if (!isPage && !isDoc && !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dsd", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".desc", StringComparison.OrdinalIgnoreCase)) continue;
                    System.Xml.Linq.XDocument x;
                    try { using (var st = entry.Open()) x = System.Xml.Linq.XDocument.Load(st); } catch { continue; }
                    if (x.Root == null) continue;
                    if (isPage && x.Root.Name.LocalName == "FixedPage")
                    {
                        var w = Num((string)x.Root.Attribute("Width")); var h = Num((string)x.Root.Attribute("Height"));
                        if (!double.IsNaN(w) && !double.IsNaN(h)) pages[Norm(name)] = (w / 96 * 25.4, h / 96 * 25.4);
                    }
                    if (isDoc)
                    {
                        var baseDir = Norm(name); baseDir = baseDir.Substring(0, baseDir.LastIndexOf('/') + 1);
                        foreach (var pc in x.Descendants().Where(e => e.Name.LocalName == "PageContent"))
                        {
                            var src = ((string)pc.Attribute("Source") ?? "").Replace('\\', '/');
                            if (src.Length == 0) continue;
                            var full = src.StartsWith("/") ? src : baseDir + src;
                            var parts = new List<string>();
                            foreach (var seg in full.Split('/')) { if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); } else if (seg.Length > 0 && seg != ".") parts.Add(seg); }
                            order.Add("/" + string.Join("/", parts));
                        }
                    }
                    foreach (var el in x.Descendants().Where(e => e.Name.LocalName == "Paper" && e.Name.NamespaceName.IndexOf("eplot", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        string A(string n) => el.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, n, StringComparison.OrdinalIgnoreCase))?.Value;
                        var w = Num(A("width")); var h = Num(A("height"));
                        if (double.IsNaN(w) || double.IsNaN(h)) continue;
                        var f = (A("units") ?? "").StartsWith("in", StringComparison.OrdinalIgnoreCase) ? 25.4 : 1;
                        papers.Add((w * f, h * f));
                    }
                }
            var list = new List<(string, double, double)>();
            foreach (var k in order.Where(pages.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)) list.Add((k, pages[k].Item1, pages[k].Item2));
            foreach (var k in pages.Keys.Where(k => !order.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k, NaturalOrder)) list.Add((k, pages[k].Item1, pages[k].Item2));
            return (list, papers);
        }

        /// <summary>Names in natural order: "2.fpage" before "10.fpage" (digit runs compared as numbers).</summary>
        public static readonly IComparer<string> NaturalOrder = Comparer<string>.Create((a, b) =>
        {
            var ra = System.Text.RegularExpressions.Regex.Split(a ?? "", "([0-9]+)"); var rb = System.Text.RegularExpressions.Regex.Split(b ?? "", "([0-9]+)");
            for (var i = 0; i < Math.Min(ra.Length, rb.Length); i++)
            {
                int c;
                if (i % 2 == 1 && long.TryParse(ra[i], out var na) && long.TryParse(rb[i], out var nb)) c = na.CompareTo(nb);
                else c = string.Compare(ra[i], rb[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return ra.Length.CompareTo(rb.Length);
        });

        /// <summary>
        /// The self-check of one DWFx file holding the sheets of one paper group (sheets: number, title block size, in export
        /// order). True paper (defaultPaper false): every page (and ePlot paper) the sheet's size within tol, either way
        /// round, AND the same orientation (portrait / landscape) as the title block. With a page per sheet, page i is compared
        /// with sheet i, so only wrong sheets are flagged; otherwise "page count N for M sheets" fails them all. Default paper
        /// (allow_paper_fallback): the size is not checked, a warning says what paper the sheets were scaled onto.
        /// Problems: empty = passed.
        /// </summary>
        public static (List<(string Number, string PageMm, bool Ok)> Sheets, List<string> Problems, List<string> Warnings) PageCheck(
            IList<(string Number, double WidthMm, double HeightMm)> sheets, IList<(string Part, double WidthMm, double HeightMm)> pages,
            IList<(double WidthMm, double HeightMm)> papers, bool defaultPaper, double tolMm = 2)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Mm(double w, double h) => $"{Math.Round(w, 1).ToString(inv)} x {Math.Round(h, 1).ToString(inv)}";
            var sh = sheets ?? new List<(string, double, double)>();
            var pg = pages ?? new List<(string, double, double)>();
            var pp = papers ?? new List<(double, double)>();
            var problems = new List<string>(); var warnings = new List<string>();
            var rows = new List<(string, string, bool)>();
            bool Fits(double pw, double ph, double sw, double shh) => SameSize(pw, ph, sw, shh, tolMm) && (ph > pw) == (shh > sw);
            if (pg.Count == 0)
            {
                problems.Add("no page could be read from the file");
                rows.AddRange(sh.Select(s => (s.Number, (string)null, false)));
                return (rows, problems, warnings);
            }
            var sizes = pg.Select(p => Mm(p.WidthMm, p.HeightMm)).Distinct().ToList();
            if (defaultPaper) warnings.Add($"scaled onto {string.Join(" | ", sizes)} mm (Revit's default paper, allow_paper_fallback): not true size");
            if (pg.Count != sh.Count)
            {
                problems.Add($"page count {pg.Count} for {sh.Count} sheets");
                rows.AddRange(sh.Select(s => (s.Number, string.Join(" | ", sizes), false)));
                return (rows, problems, warnings);
            }
            for (var i = 0; i < sh.Count; i++)
            {
                var p = pg[i]; var s = sh[i];
                var ok = defaultPaper || Fits(p.WidthMm, p.HeightMm, s.WidthMm, s.HeightMm);
                if (ok && !defaultPaper && pp.Count == sh.Count) ok = SameSize(pp[i].WidthMm, pp[i].HeightMm, s.WidthMm, s.HeightMm, tolMm);
                if (!ok) problems.Add($"sheet {s.Number}: page {Mm(p.WidthMm, p.HeightMm)} mm, title block {Mm(s.WidthMm, s.HeightMm)} mm");
                rows.Add((s.Number, Mm(p.WidthMm, p.HeightMm), ok));
            }
            if (!defaultPaper && pp.Count > 0 && pp.Count != sh.Count)
                foreach (var x in pp.Where(x => !sh.Any(s => SameSize(x.WidthMm, x.HeightMm, s.WidthMm, s.HeightMm, tolMm))))
                    problems.Add($"ePlot paper {Mm(x.WidthMm, x.HeightMm)} mm matches no sheet's title block");
            return (rows, problems, warnings);
        }

        /// <summary>
        /// The file name of each DWFx paper group, unique within the run: "&lt;stem&gt; - ISO A1[ portrait].dwfx"; a Default
        /// (allow_paper_fallback) group carries its size ("&lt;stem&gt; - Default 900x600.dwfx"); a name that would repeat gets " 2",
        /// " 3", ... One group: the requested name itself.
        /// </summary>
        public static List<string> DwfxGroupFiles(string stem, string single, IList<(string Paper, bool Portrait, double WidthMm, double HeightMm)> groups)
        {
            var list = new List<string>();
            if (groups == null || groups.Count == 0) return list;
            if (groups.Count == 1) { list.Add(single); return list; }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var label = g.Paper == "Default" ? $"Default {Math.Round(g.WidthMm)}x{Math.Round(g.HeightMm)}" : g.Paper.Replace('_', ' ') + (g.Portrait ? " portrait" : "");
                var name = $"{stem} - {label}";
                for (var n = 2; !used.Add(name); n++) name = $"{stem} - {label} {n}";
                list.Add(name + ".dwfx");
            }
            return list;
        }

        /// <summary>The sheets asked for against the sheets in the files: matched, missing (asked, not exported), extra (exported, not asked).</summary>
        public static (List<string> Matched, List<string> Missing, List<string> Extra) SheetCheck(IList<string> requested, IList<string> exported)
        {
            var req = (requested ?? new List<string>()).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var exp = (exported ?? new List<string>()).ToList();
            bool In(IEnumerable<string> l, string x) => l.Any(y => string.Equals(y, x, StringComparison.OrdinalIgnoreCase));
            return (req.Where(r => In(exp, r)).ToList(), req.Where(r => !In(exp, r)).ToList(), exp.Where(e => !In(req, e)).ToList());
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
