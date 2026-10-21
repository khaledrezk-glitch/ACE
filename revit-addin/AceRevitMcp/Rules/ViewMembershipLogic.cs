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
        /// and every ePlot Paper element's size (its width / height attributes, in mm unless its units say inches); Extents: each
        /// page's drawn extent (PageExtentOf), in the order of Pages.
        /// </summary>
        public static (List<(string Part, double WidthMm, double HeightMm)> Pages, List<(double WidthMm, double HeightMm)> Papers, List<Extent> Extents) DwfxPages(System.IO.Stream package)
        {
            var pages = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            var extents = new Dictionary<string, Extent>(StringComparer.OrdinalIgnoreCase);
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
                        if (!double.IsNaN(w) && !double.IsNaN(h)) { pages[Norm(name)] = (w / 96 * 25.4, h / 96 * 25.4); extents[Norm(name)] = PageExtentOf(x.Root); }
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
            return (list, papers, list.Select(p => extents[p.Item1]).ToList());
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
        /// The self-check of a DWFx file that holds 3D views besides its sheets (dwfx.views). The views have no title block,
        /// so their pages are left out of the size / orientation comparison; the page count expected is sheets + views.
        /// Revit decides the page order of a merged export (a ViewSet is a set; ACE inserts the views first, as the old
        /// files had them), so the view pages are identified by order and count: the first views.Count pages (viewsAt
        /// "first"); when that split fails the sheets and the last views.Count pages pass instead, those (viewsAt "last",
        /// with a warning). Exactly as many pages as sheets: the views have no 2D page (a 3D view can be stored as a 3D
        /// section, which this check does not read): the sheets are checked, a warning says the views were not seen
        /// (viewsAt "no-page"). Any other count: "page count N for M sheets and K views" fails them all. ePlot papers:
        /// split the same way when there is one per page, else up to views.Count papers that fit no sheet are the views'.
        /// No views: exactly PageCheck (viewsAt null).
        /// </summary>
        public static (List<(string Number, string PageMm, bool Ok)> Sheets, List<(string Name, string PageMm)> Views, List<string> Problems, List<string> Warnings, string ViewsAt) PageCheckWithViews(
            IList<(string Number, double WidthMm, double HeightMm)> sheets, IList<string> views, IList<(string Part, double WidthMm, double HeightMm)> pages,
            IList<(double WidthMm, double HeightMm)> papers, bool defaultPaper, double tolMm = 2)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Mm(double w, double h) => $"{Math.Round(w, 1).ToString(inv)} x {Math.Round(h, 1).ToString(inv)}";
            var sh = sheets ?? new List<(string, double, double)>();
            var vw = views ?? new List<string>();
            var pg = pages ?? new List<(string, double, double)>();
            var pp = papers ?? new List<(double, double)>();
            var k = vw.Count;
            if (k == 0 || pg.Count == 0)
            {
                var plain = PageCheck(sh, pg, pp, defaultPaper, tolMm);
                return (plain.Sheets, vw.Select(v => (v, (string)null)).ToList(), plain.Problems, plain.Warnings, null);
            }
            List<(double, double)> PapersFor(bool first)
            {
                if (pp.Count == pg.Count) return (first ? pp.Skip(k) : pp.Take(sh.Count)).ToList();
                var rest = pp.ToList();
                for (var dropped = 0; dropped < k; dropped++)
                {
                    var i = rest.FindIndex(x => !sh.Any(s => SameSize(x.Item1, x.Item2, s.WidthMm, s.HeightMm, tolMm)));
                    if (i < 0) break;
                    rest.RemoveAt(i);
                }
                return rest;
            }
            if (pg.Count == sh.Count + k)
            {
                var first = PageCheck(sh, pg.Skip(k).ToList(), PapersFor(true), defaultPaper, tolMm);
                if (first.Problems.Count > 0)
                {
                    var last = PageCheck(sh, pg.Take(sh.Count).ToList(), PapersFor(false), defaultPaper, tolMm);
                    if (last.Problems.Count == 0)
                    {
                        last.Warnings.Add($"the {k} view page(s) came after the sheets, not first (Revit chose the page order)");
                        return (last.Sheets, vw.Select((v, i) => (v, Mm(pg[sh.Count + i].WidthMm, pg[sh.Count + i].HeightMm))).ToList(), last.Problems, last.Warnings, "last");
                    }
                }
                return (first.Sheets, vw.Select((v, i) => (v, Mm(pg[i].WidthMm, pg[i].HeightMm))).ToList(), first.Problems, first.Warnings, "first");
            }
            if (pg.Count == sh.Count)
            {
                var only = PageCheck(sh, pg, PapersFor(true), defaultPaper, tolMm);
                only.Warnings.Add($"the file has a page per sheet and none for the {k} view(s) ({string.Join(", ", vw)}): a 3D view may be stored as a 3D section, which this check does not read; open the file to confirm the view is in it");
                return (only.Sheets, vw.Select(v => (v, (string)null)).ToList(), only.Problems, only.Warnings, "no-page");
            }
            var sizes = string.Join(" | ", pg.Select(p => Mm(p.WidthMm, p.HeightMm)).Distinct());
            return (sh.Select(s => (s.Number, sizes, false)).ToList(), vw.Select(v => (v, (string)null)).ToList(),
                new List<string> { $"page count {pg.Count} for {sh.Count} sheets and {k} views" }, new List<string>(), null);
        }

        /// <summary>
        /// A 3D view for dwfx.views, by its exact name (case and spaces as in Revit) among the model's views (templates left
        /// out): (index into views, problem). Problems: no view of that name (a case-insensitive near match is named, never
        /// taken), more than one 3D view of that name (ambiguous), a view of that name that is not a 3D view, a 3D view that
        /// cannot be exported (it cannot be printed).
        /// </summary>
        public static (int Index, string Problem) DwfxViewPick(string name, IList<(string Name, bool Is3D, bool Printable)> views)
        {
            var n = name ?? "";
            var list = views ?? new List<(string, bool, bool)>();
            if (n.Trim().Length == 0) return (-1, "a view name is empty");
            var exact = Enumerable.Range(0, list.Count).Where(i => string.Equals(list[i].Name, n, StringComparison.Ordinal)).ToList();
            var exact3D = exact.Where(i => list[i].Is3D).ToList();
            if (exact3D.Count > 1) return (-1, $"{exact3D.Count} 3D views are called {n} (ambiguous)");
            if (exact3D.Count == 1)
                return list[exact3D[0]].Printable ? (exact3D[0], null) : (-1, $"the 3D view {n} cannot be exported (Revit cannot print it)");
            if (exact.Count > 0) return (-1, $"{n} is not a 3D view");
            var near = list.Where(v => v.Is3D && string.Equals((v.Name ?? "").Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase)).Select(v => v.Name).Distinct().ToList();
            return (-1, near.Count == 0 ? $"no 3D view is called {n}"
                : $"no 3D view is called exactly {n} (the name must match exactly; {(near.Count == 1 ? "did you mean" : "candidates:")} {string.Join(" | ", near)})");
        }

        /// <summary>The paper group (by its sheet count, in group order) that takes the dwfx.views: the largest, the first of equals; -1: none.</summary>
        public static int DwfxViewsGroup(IList<int> sheetCounts)
        {
            if (sheetCounts == null || sheetCounts.Count == 0) return -1;
            var best = 0;
            for (var i = 1; i < sheetCounts.Count; i++) if (sheetCounts[i] > sheetCounts[best]) best = i;
            return best;
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

        /// <summary>
        /// A page's drawn extent: the bounding box of what is drawn on it (page units, clamped to the page), the extent ratio in
        /// each direction ((max - min) / page size; null: nothing measurable), and Unresolved: geometry that could not be read
        /// (a resource from another part, a malformed Data), so the ratio may be too small.
        /// </summary>
        public sealed class Extent
        {
            public double PageWidth, PageHeight, MinX, MinY, MaxX, MaxY;
            public double? RatioX, RatioY;
            public int Measured, Unresolved;
        }

        /// <summary>The drawn extent of one FixedPage given as XAML text (PageExtentOf); unreadable XML: nothing measured.</summary>
        public static Extent PageExtent(string fixedPageXml)
        {
            try { return PageExtentOf(System.Xml.Linq.XElement.Parse(fixedPageXml ?? "")); }
            catch { return new Extent { Unresolved = 1 }; }
        }

        /// <summary>
        /// The drawn extent of one XPS FixedPage (a DWFx page): every Path's geometry (Data in abbreviated syntax M/L/H/V/C/Q/S/A/Z,
        /// relative too; a PathGeometry's Figures, or its PathFigure / segment points; a {StaticResource key} found in the page),
        /// every Glyphs' OriginX / OriginY, through the RenderTransform matrices of the nested Canvas / Path / Glyphs (and a
        /// PathGeometry's Transform). Clip is ignored. A white-filled, unstroked Path covering the whole page (the paper
        /// background) does not count. Bezier control points count (a slight over-estimate); an arc counts by its end point.
        /// </summary>
        public static Extent PageExtentOf(System.Xml.Linq.XElement page)
        {
            var ext = new Extent();
            if (page == null) return ext;
            double Num(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            ext.PageWidth = Num((string)page.Attribute("Width")); ext.PageHeight = Num((string)page.Attribute("Height"));
            if (!(ext.PageWidth > 0) || !(ext.PageHeight > 0)) { ext.Unresolved++; return ext; }
            // Resources defined in the page, by x:Key.
            var resources = new Dictionary<string, System.Xml.Linq.XElement>(StringComparer.Ordinal);
            foreach (var e in page.Descendants())
            {
                var key = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
                if (key != null && !resources.ContainsKey(key)) resources[key] = e;
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            System.Xml.Linq.XElement Prop(System.Xml.Linq.XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + name);
            double[] TransformOf(System.Xml.Linq.XElement e, string attr)
            {
                var text = (string)e.Attribute(attr);
                if (text == null)
                {
                    var mt = Prop(e, attr)?.Elements().FirstOrDefault(c => c.Name.LocalName == "MatrixTransform");
                    text = (string)mt?.Attribute("Matrix");
                }
                if (text != null && text.TrimStart().StartsWith("{"))
                {
                    var r = Resource(text);
                    text = r == null ? null : (string)r.Attribute("Matrix");
                }
                return text == null ? null : ParseMatrix(text);
            }
            System.Xml.Linq.XElement Resource(string text)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*\{\s*StaticResource\s+([^}\s]+)\s*\}\s*$");
                return m.Success && resources.TryGetValue(m.Groups[1].Value, out var r) ? r : null;
            }
            // The points of a geometry in its own coordinates (null: not readable).
            List<(double X, double Y)> GeometryOf(System.Xml.Linq.XElement owner, string dataText, out double[] geoTransform)
            {
                geoTransform = null;
                System.Xml.Linq.XElement geo = null;
                if (dataText != null)
                {
                    if (!dataText.TrimStart().StartsWith("{")) return GeometryPoints(dataText);
                    geo = Resource(dataText);
                    if (geo == null) return null;
                }
                else geo = Prop(owner, "Data")?.Elements().FirstOrDefault();
                if (geo == null || geo.Name.LocalName != "PathGeometry") return null;
                geoTransform = TransformOf(geo, "Transform");
                var pts = new List<(double, double)>();
                var figures = (string)geo.Attribute("Figures");
                if (figures != null)
                {
                    if (figures.TrimStart().StartsWith("{")) return null;
                    pts.AddRange(GeometryPoints(figures));
                }
                foreach (var a in geo.Descendants().SelectMany(d => d.Attributes()).Where(a => a.Name.LocalName == "StartPoint" || a.Name.LocalName == "Point" || a.Name.LocalName == "Points"))
                {
                    var nums = NumberTokens(a.Value);
                    for (var i = 0; i + 1 < nums.Count; i += 2) pts.Add((nums[i], nums[i + 1]));
                }
                return pts;
            }
            bool IsWhite(string c)
            {
                var v = (c ?? "").Trim();
                return string.Equals(v, "White", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "#FFFFFF", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "#FFFFFFFF", StringComparison.OrdinalIgnoreCase);
            }
            string Brush(System.Xml.Linq.XElement e, string name)
            {
                var a = (string)e.Attribute(name);
                if (a != null) return a.TrimStart().StartsWith("{") ? (string)Resource(a)?.Attribute("Color") ?? a : a;
                var b = Prop(e, name)?.Elements().FirstOrDefault();
                return b == null ? null : b.Name.LocalName == "SolidColorBrush" ? (string)b.Attribute("Color") ?? "?" : "?";
            }
            void Walk(System.Xml.Linq.XElement e, double[] parent)
            {
                var local = TransformOf(e, "RenderTransform");
                var m = local == null ? parent : Compose(local, parent);
                switch (e.Name.LocalName)
                {
                    case "Path":
                    {
                        var pts = GeometryOf(e, (string)e.Attribute("Data"), out var gt);
                        if (pts == null) { ext.Unresolved++; return; }
                        var gm = gt == null ? m : Compose(gt, m);
                        var world = pts.Select(p => Apply(gm, p)).ToList();
                        if (world.Count == 0) return;
                        var stroke = Brush(e, "Stroke");
                        if (IsWhite(Brush(e, "Fill")) && (stroke == null || IsWhite(stroke))
                            && world.Min(p => p.X) <= ext.PageWidth * 0.005 && world.Max(p => p.X) >= ext.PageWidth * 0.995
                            && world.Min(p => p.Y) <= ext.PageHeight * 0.005 && world.Max(p => p.Y) >= ext.PageHeight * 0.995) return;   // the paper background
                        foreach (var p in world) Add(p);
                        ext.Measured++;
                        return;
                    }
                    case "Glyphs":
                    {
                        var x = Num((string)e.Attribute("OriginX")); var y = Num((string)e.Attribute("OriginY"));
                        if (double.IsNaN(x) || double.IsNaN(y)) { ext.Unresolved++; return; }
                        Add(Apply(m, (x, y))); ext.Measured++;
                        return;
                    }
                }
                foreach (var c in e.Elements())
                    if (c.Name.LocalName == "Canvas" || c.Name.LocalName == "Path" || c.Name.LocalName == "Glyphs") Walk(c, m);
            }
            void Add((double X, double Y) p)
            {
                var x = Math.Max(0, Math.Min(ext.PageWidth, p.X)); var y = Math.Max(0, Math.Min(ext.PageHeight, p.Y));
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            Walk(page, new double[] { 1, 0, 0, 1, 0, 0 });
            if (ext.Measured > 0 && maxX >= minX)
            {
                ext.MinX = minX; ext.MinY = minY; ext.MaxX = maxX; ext.MaxY = maxY;
                ext.RatioX = (maxX - minX) / ext.PageWidth; ext.RatioY = (maxY - minY) / ext.PageHeight;
            }
            return ext;
        }

        /// <summary>An XPS matrix "m11,m12,m21,m22,dx,dy"; null when it is not six numbers.</summary>
        public static double[] ParseMatrix(string text)
        {
            var n = NumberTokens(text);
            return n.Count == 6 ? n.ToArray() : null;
        }

        /// <summary>The matrix that applies first, then second (XPS row-vector convention: p * first * second).</summary>
        public static double[] Compose(double[] first, double[] second) => new[]
        {
            first[0] * second[0] + first[1] * second[2], first[0] * second[1] + first[1] * second[3],
            first[2] * second[0] + first[3] * second[2], first[2] * second[1] + first[3] * second[3],
            first[4] * second[0] + first[5] * second[2] + second[4], first[4] * second[1] + first[5] * second[3] + second[5],
        };

        private static (double X, double Y) Apply(double[] m, (double X, double Y) p) => (p.X * m[0] + p.Y * m[2] + m[4], p.X * m[1] + p.Y * m[3] + m[5]);

        private static List<double> NumberTokens(string text)
        {
            var list = new List<double>();
            foreach (System.Text.RegularExpressions.Match t in System.Text.RegularExpressions.Regex.Matches(text ?? "", @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"))
                list.Add(double.Parse(t.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture));
            return list;
        }

        /// <summary>
        /// The points of an abbreviated XPS geometry ("F1 M 0,0 L 10,0 10,10 Z", relative commands too): every end point and
        /// control point in absolute coordinates (H / V use the current point; repeated coordinates repeat the command, after
        /// M as L). An arc gives its end point. Stops at the first malformed command.
        /// </summary>
        public static List<(double X, double Y)> GeometryPoints(string data)
        {
            var pts = new List<(double, double)>();
            var tokens = System.Text.RegularExpressions.Regex.Matches(data ?? "", @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?|[A-Za-z]").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
            bool IsCmd(int i) => i < tokens.Count && char.IsLetter(tokens[i][0]);
            var k = 0;
            if (tokens.Count >= 2 && (tokens[0] == "F" || tokens[0] == "f") && !IsCmd(1)) k = 2;
            char cmd = '\0'; double cx = 0, cy = 0, sx = 0, sy = 0;
            while (k < tokens.Count)
            {
                if (IsCmd(k))
                {
                    cmd = tokens[k][0]; k++;
                    if (cmd == 'Z' || cmd == 'z') { cx = sx; cy = sy; }
                    continue;
                }
                var up = char.ToUpperInvariant(cmd);
                var count = up == 'M' || up == 'L' ? 2 : up == 'H' || up == 'V' ? 1 : up == 'C' ? 6 : up == 'Q' || up == 'S' ? 4 : up == 'A' ? 7 : 0;
                if (count == 0) { k++; continue; }   // numbers after Z or before any command
                var v = new double[count];
                for (var j = 0; j < count; j++)
                {
                    if (k + j >= tokens.Count || IsCmd(k + j)) return pts;   // malformed: stop
                    v[j] = double.Parse(tokens[k + j], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                }
                k += count;
                var rel = char.IsLower(cmd);
                double ox = rel ? cx : 0, oy = rel ? cy : 0;
                switch (up)
                {
                    case 'M': case 'L':
                        cx = v[0] + ox; cy = v[1] + oy; pts.Add((cx, cy));
                        if (up == 'M') { sx = cx; sy = cy; cmd = rel ? 'l' : 'L'; }
                        break;
                    case 'H': cx = v[0] + ox; pts.Add((cx, cy)); break;
                    case 'V': cy = v[0] + oy; pts.Add((cx, cy)); break;
                    case 'A': cx = v[5] + ox; cy = v[6] + oy; pts.Add((cx, cy)); break;
                    default:   // C, Q, S: control point(s), then the end point, all relative to the segment's start
                        for (var j = 0; j < count; j += 2) pts.Add((v[j] + ox, v[j + 1] + oy));
                        cx = v[count - 2] + ox; cy = v[count - 1] + oy;
                        break;
                }
            }
            return pts;
        }

        /// <summary>
        /// Where the sheet pages start among a file's pages, after PageCheckWithViews placed the view pages (viewsAt "first":
        /// after the views; "last" / "no-page": at the start); -1 when the pages cannot be matched to the sheets.
        /// </summary>
        public static int SheetPageOffset(int sheets, int views, int pages, string viewsAt)
        {
            if (viewsAt == "first") return pages == sheets + views ? views : -1;
            if (viewsAt == "last") return pages == sheets + views ? 0 : -1;
            return pages == sheets ? 0 : -1;
        }

        /// <summary>
        /// The no-margin check: each sheet page's drawn extent against its page; below min (0.98) in either direction fails
        /// (the drawing was scaled down with a white margin). A page with nothing measurable, or (when it fails) geometry
        /// that could not be read, gives a warning ("extent not measured"), never a failure. Default paper
        /// (allow_paper_fallback, scaled anyway) only warns, as does oversize (dwfx.allow_oversize: content beyond a title block
        /// frame in this file, so Revit scales its pages; #37). Rows: (number, ratio X, ratio Y, ok).
        /// </summary>
        public static (List<(string Number, double? RatioX, double? RatioY, bool Ok)> Sheets, List<string> Problems, List<string> Warnings) ExtentCheck(
            IList<string> numbers, IList<Extent> extents, bool defaultPaper, double min = 0.98, bool oversize = false)
        {
            var rows = new List<(string, double?, double?, bool)>(); var problems = new List<string>(); var warnings = new List<string>();
            var nums = numbers ?? new List<string>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Pct(double r) => Math.Round(r * 100, 1).ToString(inv) + "%";
            if (extents == null || extents.Count != nums.Count)
            {
                warnings.Add("drawn extent not measured: the pages could not be matched to the sheets");
                rows.AddRange(nums.Select(n => (n, (double?)null, (double?)null, true)));
                return (rows, problems, warnings);
            }
            var notMeasured = new List<string>();
            for (var i = 0; i < nums.Count; i++)
            {
                var e = extents[i];
                if (e == null || e.RatioX == null || e.RatioY == null) { notMeasured.Add(nums[i]); rows.Add((nums[i], null, null, true)); continue; }
                var small = e.RatioX.Value < min || e.RatioY.Value < min;
                var text = $"sheet {nums[i]}: drawn over {Pct(e.RatioX.Value)} x {Pct(e.RatioY.Value)} of the page (below {Pct(min)}: scaled down with a margin)";
                if (small && (defaultPaper || oversize || e.Unresolved > 0))
                {
                    warnings.Add(text + (defaultPaper ? " (Revit's default paper, allow_paper_fallback)" : oversize ? " (content beyond a title block frame, allow_oversize: scaled as expected)" : $" ({e.Unresolved} drawing part(s) could not be read: extent not measured reliably)"));
                    rows.Add((nums[i], e.RatioX, e.RatioY, true));
                }
                else
                {
                    if (small) problems.Add(text);
                    rows.Add((nums[i], e.RatioX, e.RatioY, !small));
                }
            }
            if (notMeasured.Count > 0) warnings.Add($"drawn extent not measured for {notMeasured.Count} sheet page(s) (nothing readable drawn): {string.Join(", ", notMeasured.Take(20))}");
            return (rows, problems, warnings);
        }

        /// <summary>
        /// One element on a sheet for the sheet-extent pre-flight: its box in sheet coordinates (mm). A title block also carries
        /// its frame (Sheet Width / Height, mm) and its insertion point (mm).
        /// </summary>
        public sealed class SheetPart
        {
            public string Label;          // "Title Blocks Submittal Header: A1 (id 123)"
            public string Family;         // a title block's family name
            public bool TitleBlock;
            public double MinX, MinY, MaxX, MaxY;
            public double FrameW, FrameH, OriginX, OriginY;
        }

        /// <summary>
        /// The title block frame (W x H mm) in sheet coordinates: the box itself when it is the frame's size, else the frame with
        /// one of its corners on the insertion point (bottom-left, bottom-right, top-left, top-right, in that order of preference)
        /// that fits the box best. No box: bottom-left on the insertion point.
        /// </summary>
        public static (double MinX, double MinY, double MaxX, double MaxY) TitleBlockFrame(SheetPart tb, double tolMm = 1)
        {
            double w = tb.FrameW, h = tb.FrameH, ox = tb.OriginX, oy = tb.OriginY;
            var hasBox = tb.MaxX > tb.MinX && tb.MaxY > tb.MinY;
            if (hasBox && Math.Abs(tb.MaxX - tb.MinX - w) <= tolMm && Math.Abs(tb.MaxY - tb.MinY - h) <= tolMm) return (tb.MinX, tb.MinY, tb.MaxX, tb.MaxY);
            var candidates = new[] { (ox, oy, ox + w, oy + h), (ox - w, oy, ox, oy + h), (ox, oy - h, ox + w, oy), (ox - w, oy - h, ox, oy) };
            if (!hasBox) return candidates[0];
            double Misfit((double, double, double, double) f) =>
                Math.Abs(f.Item1 - tb.MinX) + Math.Abs(f.Item2 - tb.MinY) + Math.Abs(f.Item3 - tb.MaxX) + Math.Abs(f.Item4 - tb.MaxY);
            var best = candidates[0];
            foreach (var c in candidates.Skip(1)) if (Misfit(c) < Misfit(best) - 1e-6) best = c;
            return best;
        }

        /// <summary>
        /// The DWFx sheet-extent pre-flight (#37): a merged DWFx scales every page by the largest sheet's extents, so anything on a
        /// sheet beyond its title block frame shrinks all pages. Compares the sheet outline's size with the paper and each part's
        /// box with the frame (the first title block's), tolerance tolMm. Ok when nothing sticks out; Text names the sheet,
        /// outline vs paper and up to maxParts parts (how many mm beyond which edge); a title block whose own box exceeds its
        /// frame is named as a family to edit. Over: the parts beyond, as texts.
        /// </summary>
        public static (bool Ok, string Text, List<string> Over) SheetExtentCheck(string number, double paperW, double paperH, double? outlineW, double? outlineH,
            IList<SheetPart> parts, double tolMm = 1, int maxParts = 5)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => Math.Round(v, 1).ToString(inv);
            var over = new List<string>();
            var familyNotes = new List<string>();
            parts = parts ?? new List<SheetPart>();
            var tb = parts.FirstOrDefault(p => p.TitleBlock && p.FrameW > 0 && p.FrameH > 0);
            if (tb != null)
            {
                var (fx0, fy0, fx1, fy1) = TitleBlockFrame(tb, tolMm);
                foreach (var p in parts)
                {
                    if (!(p.MaxX >= p.MinX && p.MaxY >= p.MinY)) continue;
                    var edges = new List<string>();
                    void Edge(double d, string where) { if (d > tolMm) edges.Add($"{N(d)} mm {where}"); }
                    Edge(p.MaxY - fy1, "above the top edge"); Edge(fy0 - p.MinY, "below the bottom edge");
                    Edge(fx0 - p.MinX, "left of the left edge"); Edge(p.MaxX - fx1, "right of the right edge");
                    if (edges.Count > 0) over.Add($"{p.Label}: {string.Join(", ", edges)}");
                    if (p.TitleBlock && p.FrameW > 0 && p.FrameH > 0 && (p.MaxX - p.MinX > p.FrameW + tolMm || p.MaxY - p.MinY > p.FrameH + tolMm))
                    {
                        var note = $"the title block family {p.Family ?? p.Label} has geometry outside its frame (possibly invisible lines): edit the family";
                        if (!familyNotes.Contains(note)) familyNotes.Add(note);
                    }
                }
            }
            var outlineOver = outlineW != null && outlineH != null && paperW > 0 && paperH > 0 && (outlineW.Value > paperW + tolMm || outlineH.Value > paperH + tolMm);
            if (over.Count == 0 && !outlineOver) return (true, null, over);
            var text = $"sheet {number}: " + (outlineW != null && outlineH != null ? $"outline {N(outlineW.Value)} x {N(outlineH.Value)} mm on {N(paperW)} x {N(paperH)} mm paper" : $"paper {N(paperW)} x {N(paperH)} mm");
            if (over.Count > 0) text += $"; beyond the frame: {string.Join("; ", over.Take(maxParts))}{(over.Count > maxParts ? $" (+{over.Count - maxParts} more)" : "")}";
            else if (tb != null) text += $"; nothing measured on the sheet sticks out: most often invisible lines in the title block family {tb.Family ?? tb.Label}: edit the family";
            else text += "; the sheet has no title block to measure against";
            if (familyNotes.Count > 0) text += " (" + string.Join("; ", familyNotes) + ")";
            return (false, text, over);
        }

        /// <summary>
        /// A paper size printed in a printer's paper name ("ISO A1 (841.00 x 594.00 MM)", "11x17", "33.11 x 23.39 in"), in mm:
        /// mm when the name says mm, inches when it says in / " or both numbers are below 60; null when the name holds none.
        /// </summary>
        public static (double WidthMm, double HeightMm)? PaperNameSize(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"(\d+(?:[.,]\d+)?)\s*(?:mm|in|"")?\s*[xX×]\s*(\d+(?:[.,]\d+)?)\s*(mm|millimet\w*|in\b|inch\w*|"")?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double P(string v) => double.Parse(v.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var a = P(m.Groups[1].Value); var b = P(m.Groups[2].Value);
            var unit = m.Groups[3].Value.ToLowerInvariant();
            var inches = unit.StartsWith("in") || unit == "\"" || (unit.Length == 0 && a < 60 && b < 60);
            return inches ? (a * 25.4, b * 25.4) : (a, b);
        }

        /// <summary>
        /// The printer paper size (index into names, the printer's PaperSizes in order) for a DWF paper format (ExportPaperFormat
        /// name, e.g. ISO_A1): best first, the name itself ("A1", "ISO A1", "ISO_A1", case, spaces and _ ignored; ANSI / ARCH
        /// with their usual names, e.g. Tabloid for ANSI B), then the name with "full bleed" / "expand", then any name whose
        /// printed size (PaperNameSize) is the format's within 2 mm; a "rotated" / "transverse" paper last. A name whose printed
        /// size contradicts the format is never taken (nor a JIS B size for ISO B). -1: the printer has no such paper.
        /// </summary>
        public static int PrinterPaperFor(string paperEnum, IList<string> names, double tolMm = 2)
        {
            if (names == null || string.IsNullOrWhiteSpace(paperEnum)) return -1;
            var fmt = PaperFormats.FirstOrDefault(p => p.Enum == paperEnum);
            if (fmt.Enum == null) return -1;
            string Norm(string t) => new string((t ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            var code = paperEnum.Substring(paperEnum.IndexOf('_') + 1);   // A1, B, D, E1
            var family = paperEnum.Substring(0, paperEnum.IndexOf('_'));   // ISO, ANSI, ARCH
            var keys = new List<string> { Norm(paperEnum) };
            if (family == "ISO") keys.Add(Norm(code));
            if (family == "ANSI")
            {
                if (code == "A") keys.Add("LETTER");
                if (code == "B") keys.AddRange(new[] { "TABLOID", "LEDGER", "11X17" });
                if (code == "C" || code == "D" || code == "E") keys.Add(code + "SIZESHEET");
            }
            var best = -1; var bestScore = int.MaxValue;
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i] ?? "";
                var upper = name.ToUpperInvariant();
                if (family == "ISO" && code.StartsWith("B") && upper.Contains("JIS")) continue;
                var size = PaperNameSize(name);
                var sizeFits = size != null && SameSize(size.Value.WidthMm, size.Value.HeightMm, fmt.Short, fmt.Long, tolMm);
                if (size != null && !sizeFits) continue;
                var bare = Norm(System.Text.RegularExpressions.Regex.Replace(name, @"\([^)]*\)", " "));
                var plain = bare.Replace("FULLBLEED", "").Replace("EXPANDED", "").Replace("EXPAND", "");
                var turned = upper.Contains("ROTATED") || upper.Contains("TRANSVERSE");
                int score;
                if (keys.Contains(bare)) score = 0;
                else if (keys.Contains(plain)) score = 1;
                else if (sizeFits) score = 2;
                else continue;
                if (turned) score += 10;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
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
