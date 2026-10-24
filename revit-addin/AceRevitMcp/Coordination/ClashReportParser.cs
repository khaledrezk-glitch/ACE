using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AceRevitMcp.Coordination
{
    /// <summary>One clash as written in a Navisworks clash report (before it is matched to Revit elements).</summary>
    internal sealed class ReportClash
    {
        public string Test, Name, Status, Grid, Description, Found, Units;
        public double? DistanceMm;
        public double[] Point;          // as reported (Navisworks units and coordinates); not used for placement
        public ReportItem A = new ReportItem(), B = new ReportItem();
    }

    internal sealed class ReportItem
    {
        public long? ElementId;
        public string File, Name, Type, Layer;
        public override string ToString() => $"{Name ?? Type ?? "?"}{(ElementId != null ? $" (id {ElementId})" : "")}{(File != null ? $" in {File}" : "")}";
    }

    /// <summary>
    /// Reads Navisworks Clash Detective reports: the XML report (complete and exact) and the HTML tabular report
    /// (columns are found by their names, so the report settings may vary). Plain .NET, tested in tools/clash-test.
    /// </summary>
    internal static class ClashReportParser
    {
        public static List<ReportClash> Parse(string text, out string format)
        {
            var head = text.TrimStart().Substring(0, Math.Min(400, text.TrimStart().Length));
            if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.IndexOf("<clashresult", StringComparison.OrdinalIgnoreCase) >= 0)
            { format = "Navisworks XML"; return ParseXml(text); }
            if (head.IndexOf("<exchange", StringComparison.OrdinalIgnoreCase) >= 0) { format = "Navisworks XML"; return ParseXml(text); }
            format = "Navisworks HTML";
            return ParseHtml(text);
        }

        // ---- XML ---------------------------------------------------------------------------------------------------

        public static List<ReportClash> ParseXml(string xml)
        {
            var doc = XDocument.Parse(xml);
            var unitsAttr = doc.Root?.Attribute("units")?.Value;
            var list = new List<ReportClash>();
            foreach (var test in doc.Descendants().Where(e => e.Name.LocalName == "clashtest"))
            {
                var testName = test.Attribute("name")?.Value ?? "Navisworks";
                var units = test.Ancestors().Select(a => a.Attribute("units")?.Value).FirstOrDefault(u => u != null) ?? unitsAttr;
                foreach (var r in test.Descendants().Where(e => e.Name.LocalName == "clashresult"))
                {
                    var c = new ReportClash
                    {
                        Test = testName, Units = units,
                        Name = r.Attribute("name")?.Value,
                        Status = Status(Child(r, "resultstatus")?.Value ?? r.Attribute("status")?.Value),
                        DistanceMm = ToMm(r.Attribute("distance")?.Value, units),
                        Grid = Child(r, "gridlocation")?.Value?.Trim(),
                        Description = Child(r, "description")?.Value?.Trim(),
                    };
                    var pos = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "pos3f");
                    if (pos != null)
                        c.Point = new[] { "x", "y", "z" }.Select(a => double.TryParse(pos.Attribute(a)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                    var objects = r.Descendants().Where(e => e.Name.LocalName == "clashobject").Take(2).ToList();
                    if (objects.Count > 0) c.A = XmlItem(objects[0]);
                    if (objects.Count > 1) c.B = XmlItem(objects[1]);
                    list.Add(c);
                }
            }
            return list;
        }

        private static XElement Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

        private static ReportItem XmlItem(XElement o)
        {
            var item = new ReportItem { Layer = Child(o, "layer")?.Value?.Trim() };
            foreach (var attr in o.Descendants().Where(e => e.Name.LocalName == "objectattribute" || e.Name.LocalName == "smarttag"))
            {
                var name = Child(attr, "name")?.Value?.Trim();
                var value = Child(attr, "value")?.Value?.Trim();
                Apply(item, name, value);
            }
            var nodes = o.Descendants().Where(e => e.Name.LocalName == "pathlink").SelectMany(p => p.Elements()).Select(n => n.Value.Trim()).ToList();
            item.File ??= nodes.FirstOrDefault(IsModelFile);
            return item;
        }

        // ---- HTML (tabular) ------------------------------------------------------------------------------------------

        public static List<ReportClash> ParseHtml(string html)
        {
            var list = new List<ReportClash>();
            // Each table is one test; the test's name is the last heading or caption before it.
            var tables = Regex.Matches(html, @"<table\b.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var lastEnd = 0;
            var testName = "Navisworks";
            foreach (Match t in tables)
            {
                var between = html.Substring(lastEnd, t.Index - lastEnd);
                var heading = Regex.Matches(between, @"<(h\d|caption|title)[^>]*>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
                if (heading != null) testName = Clean(heading.Groups[2].Value);
                var caption = Regex.Match(t.Value, @"<caption[^>]*>(.*?)</caption>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (caption.Success) testName = Clean(caption.Groups[1].Value);
                lastEnd = t.Index + t.Length;
                list.AddRange(ParseTable(t.Value, testName));
            }
            return list;
        }

        private static IEnumerable<ReportClash> ParseTable(string table, string testName)
        {
            var rows = Regex.Matches(table, @"<tr\b.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Cast<Match>()
                .Select(r => Regex.Matches(r.Value, @"<(t[hd])\b([^>]*)>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Cast<Match>()
                    .Select(c => (Header: c.Groups[1].Value.Equals("th", StringComparison.OrdinalIgnoreCase), Span: Span(c.Groups[2].Value), Text: Clean(c.Groups[3].Value))).ToList())
                .Where(r => r.Count > 0).ToList();

            // Header rows: all cells are headers, or the row names "Clash" / "Status". A group row ("Item 1" spanning
            // several columns) prefixes the column names below it.
            var columns = new List<string>();
            var group = new List<string>();
            var i = 0;
            for (; i < rows.Count; i++)
            {
                var r = rows[i];
                var groupRow = r.Any(c => c.Span > 1 && Regex.IsMatch(c.Text, @"^item\s*[12]$", RegexOptions.IgnoreCase));
                var looksHeader = groupRow || r.All(c => c.Header) || r.Any(c => Regex.IsMatch(c.Text, @"^(clash name|clash|status)$", RegexOptions.IgnoreCase));
                if (!looksHeader) break;
                var expanded = r.SelectMany(c => Enumerable.Repeat(c.Text, c.Span)).ToList();
                if (groupRow) { group = expanded; continue; }
                columns = expanded.Select((name, k) => group.Count > k && Regex.IsMatch(group[k], @"item\s*[12]", RegexOptions.IgnoreCase) ? $"{group[k]} {name}" : name).ToList();
            }
            if (columns.Count == 0) yield break;

            int Col(params string[] patterns) => columns.FindIndex(c => patterns.Any(p => Regex.IsMatch(c, p, RegexOptions.IgnoreCase)));
            var nameCol = Col(@"^clash( name)?$", @"clash name");
            if (nameCol < 0) yield break;   // not a clash table
            var statusCol = Col(@"^status$", @"\bstatus\b");
            var distCol = Col(@"distance");
            var gridCol = Col(@"grid");
            var descCol = Col(@"description");
            var foundCol = Col(@"date found", @"found");
            var pointCol = Col(@"clash point", @"point");
            var units = Regex.Match(string.Join(" ", columns), @"distance\s*\(?\s*(mm|cm|m|ft|in)\b", RegexOptions.IgnoreCase).Groups[1].Value;

            for (; i < rows.Count; i++)
            {
                var cells = rows[i].SelectMany(c => Enumerable.Repeat(c.Text, c.Span)).ToList();
                string At(int k) => k >= 0 && k < cells.Count && cells[k].Length > 0 ? cells[k] : null;
                if (At(nameCol) == null) continue;
                var c = new ReportClash
                {
                    Test = testName, Name = At(nameCol), Status = Status(At(statusCol)), DistanceMm = ToMm(At(distCol), units),
                    Grid = At(gridCol), Description = At(descCol), Found = At(foundCol),
                };
                var pt = At(pointCol);
                if (pt != null)
                {
                    var nums = Regex.Matches(pt, @"-?\d+(?:[.,]\d+)?").Cast<Match>().Select(m => double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture)).ToArray();
                    if (nums.Length >= 3) c.Point = nums.Take(3).ToArray();
                }
                for (var k = 0; k < columns.Count && k < cells.Count; k++)
                {
                    var m = Regex.Match(columns[k], @"item\s*([12])\s*:?\s*(.*)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    Apply(m.Groups[1].Value == "1" ? c.A : c.B, m.Groups[2].Value, cells[k]);
                }
                yield return c;
            }
        }

        // ---- shared ------------------------------------------------------------------------------------------------

        private static void Apply(ReportItem item, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
            name = name.Trim();
            if (Regex.IsMatch(name, @"^(item\s*|element\s*)?id$", RegexOptions.IgnoreCase))
            {
                // "Element ID: 123456" or "123456"; other ids (entity handles, GUIDs) are not Revit element ids.
                var digits = Regex.Match(value, @"^(?:element\s*id\s*:?\s*)?(\d+)$", RegexOptions.IgnoreCase);
                if (digits.Success && long.TryParse(digits.Groups[1].Value, out var id)) item.ElementId = id;
            }
            else if (Regex.IsMatch(name, @"item name|^name$", RegexOptions.IgnoreCase)) item.Name ??= value;
            else if (Regex.IsMatch(name, @"item type|^type$|category", RegexOptions.IgnoreCase)) item.Type ??= value;
            else if (Regex.IsMatch(name, @"layer|level", RegexOptions.IgnoreCase)) item.Layer ??= value;
            else if (Regex.IsMatch(name, @"source file|file name|^file$|path", RegexOptions.IgnoreCase))
                item.File ??= value.Split(new[] { '>', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).FirstOrDefault(IsModelFile) ?? value;
        }

        /// <summary>A model's name for matching a report's file to a loaded model: no folder, extension or punctuation.</summary>
        internal static string ModelKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var n = name.Split('/', '\\').Last().Trim();
            n = Regex.Replace(n, @"\.(nwc|nwd|nwf|rvt|ifc|dwg)$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(n, @"[^A-Za-z0-9]", "").ToLowerInvariant();
        }

        /// <summary>
        /// Whether a report's file ("STR_Model.nwc") is a loaded model ("STR_Model", or a local copy "STR_Model_jsmith"):
        /// equal names, or one name starting with the other (at least 4 characters).
        /// </summary>
        internal static bool SameModel(string reportFile, string modelName)
        {
            var a = ModelKey(reportFile); var b = ModelKey(modelName);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            return Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b) || b.StartsWith(a));
        }

        private static bool IsModelFile(string s) => Regex.IsMatch(s ?? "", @"\.(nwc|nwd|nwf|rvt|ifc|dwg)$", RegexOptions.IgnoreCase);

        private static int Span(string attrs)
        {
            var m = Regex.Match(attrs, @"colspan\s*=\s*""?(\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 && n < 50 ? n : 1;
        }

        private static string Clean(string html) =>
            Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", " ")), @"\s+", " ").Trim();

        /// <summary>Navisworks status -> ACE status (Reviewed counts as active).</summary>
        internal static string Status(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.StartsWith("approved")) return "approved";
            if (s.StartsWith("resolved")) return "resolved";
            if (s.StartsWith("new")) return "new";
            if (s.Length == 0) return null;
            return "active";
        }

        /// <summary>A distance with or without its unit ("-0.120 m", "-120mm", "-0.39 ft") in mm; null when it can't be read.</summary>
        internal static double? ToMm(string text, string defaultUnits)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"(-?\d+(?:[.,]\d+)?)\s*(mm|cm|m|ft|in|"")?", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = (m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : defaultUnits ?? "").ToLowerInvariant();
            var factor = unit switch
            {
                "mm" or "millimeters" or "millimetres" => 1, "cm" or "centimeters" or "centimetres" => 10,
                "m" or "meters" or "metres" => 1000, "ft" or "feet" => 304.8, "in" or "\"" or "inches" => 25.4,
                _ => Math.Abs(v) < 5 ? 1000 : 1,   // no unit: small numbers are metres (Navisworks default), large ones mm
            };
            return Math.Round(v * factor, 1);
        }
    }
}
