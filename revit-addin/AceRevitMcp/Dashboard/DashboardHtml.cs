using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using AceRevitMcp.Util;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Renders the insights as one self-contained HTML file (no scripts, no external files), in the ACE brand:
    /// black, white and greys, with ACE Red only for the key value and for items needing action.
    /// The CSS avoids variables and grid so the same file also displays inside Revit's embedded browser.
    /// </summary>
    internal static class DashboardHtml
    {
        private static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        private static string E(string s) => WebUtility.HtmlEncode(s ?? "");
        private static string N(double v, string f = "N0") => v.ToString(f, CultureInfo.InvariantCulture);

        public static string Render(Insights x)
        {
            var red = Hex(Branding.Accent);
            var black = Hex(Branding.Primary);
            var grey = Hex(Branding.GreyDark);
            var light = Hex(Branding.GreyLight);
            var font = E(Branding.FontFamily) + ", Arial, Helvetica, sans-serif";
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"en-GB\"><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append($"<title>{E(x.Model)} | Model insights</title><style>");
            sb.Append($@"
body{{margin:0;background:#FFFFFF;color:{black};font-family:{font};font-size:13px;line-height:1.45}}
.page{{max-width:1180px;margin:0 auto;padding:24px 28px 40px}}
.head{{border-bottom:2px solid {red};padding-bottom:14px;margin-bottom:22px}}
.head table{{width:100%;border-collapse:collapse}}
.logo{{height:52px;vertical-align:middle}}
.rule{{width:2px;background:{red};height:52px;display:inline-block;vertical-align:middle;margin:0 18px}}
h1{{font-size:26px;margin:0;font-weight:bold}}
h2{{font-size:17px;margin:28px 0 10px;font-weight:bold}}
.meta{{color:{grey};font-size:12px}}
.row{{width:100%;border-collapse:separate;border-spacing:0;margin:0 0 4px}}
.row td.cell{{vertical-align:top;padding:0 8px 0 0}}
.row td.cell:last-child{{padding-right:0}}
.card{{background:{light};padding:16px 18px;min-height:170px}}
.card h3{{margin:0 0 10px;font-size:14px}}
.big{{font-size:44px;font-weight:bold;line-height:1}}
.kpi{{display:inline-block;width:31%;margin:0 2% 12px 0;vertical-align:top}}
.kpi b{{display:block;font-size:22px}}
.kpi span{{color:{grey};font-size:11px}}
table.list{{width:100%;border-collapse:collapse;background:#FFFFFF}}
table.list th{{text-align:left;font-size:11px;color:{grey};font-weight:normal;border-bottom:1px solid #D9D9D9;padding:6px 8px}}
table.list td{{border-bottom:1px solid #EDEDED;padding:7px 8px;vertical-align:top}}
.num{{text-align:right;white-space:nowrap}}
.tag{{display:inline-block;padding:1px 7px;font-size:11px;white-space:nowrap;border:1px solid {grey};color:{grey}}}
.tag.fail{{border-color:{red};color:#FFFFFF;background:{red}}}
.tag.warn{{border-color:{black};color:{black}}}
.tag.live{{border-color:{black};color:#FFFFFF;background:{black}}}
.tag.plan{{border-color:{grey};color:{grey};background:#FFFFFF}}
.bar{{background:#E4E4E4;height:8px;margin-top:4px}}
.bar i{{display:block;height:8px;background:{black}}}
.bar i.key{{background:{red}}}
.muted{{color:{grey}}}
.soon{{border:1px dashed #BDBDBD;background:#FFFFFF;padding:14px 16px;min-height:90px}}
.foot{{margin-top:34px;border-top:1px solid #D9D9D9;padding-top:12px;color:{grey};font-size:11px}}
");
            sb.Append("</style></head><body><div class=\"page\">");

            // ---- header ----
            var logo = LogoDataUri();
            sb.Append("<div class=\"head\"><table><tr><td>");
            if (logo != null) sb.Append($"<img class=\"logo\" src=\"{logo}\" alt=\"{E(Branding.Name)}\"><span class=\"rule\"></span>");
            sb.Append("<span style=\"display:inline-block;vertical-align:middle\">");
            sb.Append($"<h1>Model insights</h1><div class=\"meta\">{E(x.Model)} &nbsp;·&nbsp; {x.Time:d MMMM yyyy, HH:mm} &nbsp;·&nbsp; {E(x.User)}</div></span></td>");
            sb.Append($"<td style=\"text-align:right;vertical-align:bottom\" class=\"meta\">{E(Branding.FullName)}<br>Read-only snapshot: nothing in the model was changed</td></tr></table></div>");

            // ---- row 1: score | key figures | trend ----
            sb.Append("<table class=\"row\"><tr>");
            sb.Append("<td class=\"cell\" style=\"width:30%\"><div class=\"card\"><h3>Model health</h3>");
            sb.Append($"<table style=\"border-collapse:collapse\"><tr><td>{Gauge(x.Score, black, red)}</td><td style=\"padding-left:14px;vertical-align:middle\">");
            sb.Append($"<div class=\"big\">{x.Score}</div><div class=\"muted\">out of 100</div><div style=\"margin-top:8px;font-weight:bold\">{E(x.Grade)}</div></td></tr></table>");
            var failing = x.Checks.Count(c => c.Status == "fail");
            var review = x.Checks.Count(c => c.Status == "warn");
            sb.Append($"<div class=\"muted\" style=\"margin-top:8px\">{failing} checks need action, {review} to review, {x.Checks.Count - failing - review} pass</div></div></td>");

            sb.Append("<td class=\"cell\" style=\"width:42%\"><div class=\"card\"><h3>Key figures</h3>");
            foreach (var k in new[] { "Warnings", "Model elements", "Walls", "Doors", "Windows", "Rooms", "Views", "Sheets", "Families" })
                if (x.Counts.TryGetValue(k, out var v))
                    sb.Append($"<div class=\"kpi\"><b{(k == "Warnings" && v > 0 ? $" style=\"color:{red}\"" : "")}>{N(v)}</b><span>{E(k)}</span></div>");
            sb.Append("</div></td>");

            sb.Append("<td class=\"cell\" style=\"width:28%\"><div class=\"card\"><h3>Health trend</h3>");
            sb.Append(Trend(x.History, black, red));
            sb.Append($"<div class=\"muted\" style=\"margin-top:6px\">{x.History.Count} {(x.History.Count == 1 ? "snapshot" : "snapshots")} of this model{(x.FileBytes > 0 ? $" &nbsp;·&nbsp; file {N(x.FileBytes / 1048576.0, "N0")} MB" : "")}</div></div></td>");
            sb.Append("</tr></table>");

            // ---- tools status ----
            sb.Append("<h2>Status of ACE tools</h2><table class=\"list\"><tr><th style=\"width:24%\">Tool</th><th style=\"width:15%\">Status</th><th>Latest result</th></tr>");
            foreach (var (tool, live, status, result) in ToolStatus(x))
                sb.Append($"<tr><td><b>{E(tool)}</b></td><td><span class=\"tag {(live ? "live" : "plan")}\">{E(status)}</span></td><td>{result}</td></tr>");
            sb.Append("</table>");

            // ---- audit checks ----
            sb.Append("<h2>Audit findings</h2><table class=\"list\"><tr><th style=\"width:12%\">Result</th><th style=\"width:22%\">Check</th><th>Finding</th><th class=\"num\" style=\"width:9%\">Score</th><th style=\"width:30%\">What to do</th></tr>");
            foreach (var c in x.Checks.OrderByDescending(c => c.Penalty).ThenBy(c => c.Area))
            {
                var label = c.Status == "fail" ? "Action needed" : c.Status == "warn" ? "Review" : "Pass";
                sb.Append($"<tr><td><span class=\"tag {c.Status}\">{label}</span></td><td><b>{E(c.Name)}</b><div class=\"muted\">{E(c.Area)}</div></td>");
                sb.Append($"<td>{E(c.Detail)}{(c.Ids.Count > 0 && c.Status != "ok" ? $"<div class=\"muted\">Element ids: {E(string.Join(", ", c.Ids.Take(8)))}{(c.Ids.Count > 8 ? ", ..." : "")}</div>" : "")}</td>");
                sb.Append($"<td class=\"num\">{(c.Penalty > 0.05 ? "&minus;" + N(c.Penalty, "0.0") : "0")}</td><td class=\"muted\">{(c.Status == "ok" ? "" : E(c.Hint))}</td></tr>");
            }
            sb.Append("</table>");

            // ---- warnings | completeness ----
            sb.Append("<table class=\"row\" style=\"margin-top:24px\"><tr><td class=\"cell\" style=\"width:55%\"><div class=\"card\"><h3>Most frequent warnings</h3>");
            if (x.WarningTypes.Count == 0) sb.Append("<div class=\"muted\">No warnings.</div>");
            var maxW = x.WarningTypes.Count == 0 ? 1 : x.WarningTypes.Max(w => w.Count);
            for (var i = 0; i < x.WarningTypes.Count; i++)
            {
                var w = x.WarningTypes[i];
                sb.Append($"<div style=\"margin-bottom:8px\"><table style=\"width:100%;border-collapse:collapse\"><tr><td>{E(Trim(w.Text, 110))}</td><td class=\"num\" style=\"width:50px\"><b>{w.Count}</b></td></tr></table>");
                sb.Append($"<div class=\"bar\"><i class=\"{(i == 0 ? "key" : "")}\" style=\"width:{N(100.0 * w.Count / maxW, "0")}%\"></i></div></div>");
            }
            sb.Append("</div></td><td class=\"cell\" style=\"width:45%\"><div class=\"card\"><h3>Key parameters filled</h3>");
            foreach (var c in x.Completeness)
            {
                sb.Append($"<div style=\"margin-bottom:8px\"><table style=\"width:100%;border-collapse:collapse\"><tr><td>{E(c.Category)}: {E(c.Parameter)} <span class=\"muted\">({N(c.Total)})</span></td><td class=\"num\"><b{(c.Percent < 50 && c.Total > 0 ? $" style=\"color:{red}\"" : "")}>{(c.Total == 0 ? "-" : c.Percent + "%")}</b></td></tr></table>");
                sb.Append($"<div class=\"bar\"><i style=\"width:{(c.Total == 0 ? 0 : c.Percent)}%\"></i></div></div>");
            }
            sb.Append("</div></td></tr></table>");

            // ---- per level ----
            if (x.Levels.Count > 0)
            {
                sb.Append("<h2>By level</h2><table class=\"list\"><tr><th>Level</th><th class=\"num\">Elevation (mm)</th><th class=\"num\">Walls</th><th class=\"num\">Doors</th><th class=\"num\">Windows</th><th class=\"num\">Rooms</th><th class=\"num\">Room area (m²)</th></tr>");
                foreach (var l in x.Levels)
                    sb.Append($"<tr><td><b>{E(l.Level)}</b></td><td class=\"num\">{N(l.Elevation)}</td><td class=\"num\">{N(l.Walls)}</td><td class=\"num\">{N(l.Doors)}</td><td class=\"num\">{N(l.Windows)}</td><td class=\"num\">{N(l.Rooms)}</td><td class=\"num\">{N(l.RoomAreaM2, "N1")}</td></tr>");
                sb.Append($"<tr><td><b>Total</b></td><td></td><td class=\"num\"><b>{N(x.Levels.Sum(l => l.Walls))}</b></td><td class=\"num\"><b>{N(x.Levels.Sum(l => l.Doors))}</b></td><td class=\"num\"><b>{N(x.Levels.Sum(l => l.Windows))}</b></td><td class=\"num\"><b>{N(x.Levels.Sum(l => l.Rooms))}</b></td><td class=\"num\"><b>{N(x.Levels.Sum(l => l.RoomAreaM2), "N1")}</b></td></tr></table>");
            }

            // ---- submission readiness | activity ----
            sb.Append("<table class=\"row\" style=\"margin-top:24px\"><tr><td class=\"cell\" style=\"width:50%\"><div class=\"card\"><h3>Submission readiness <span class=\"tag plan\" style=\"margin-left:6px\">Preview</span></h3>");
            sb.Append("<table class=\"list\"><tr><th>Project information</th><th>Value</th></tr>");
            foreach (var p in x.ProjectInfo)
                sb.Append($"<tr><td>{E(p.Field)}</td><td>{(p.Filled ? E(Trim(p.Value, 60)) : $"<span class=\"tag fail\">Missing</span>")}</td></tr>");
            sb.Append("</table><table class=\"list\" style=\"margin-top:10px\"><tr><th>Sheet title block field</th><th class=\"num\">Sheets missing it</th></tr>");
            var sheets = x.Counts.TryGetValue("Sheets", out var sc) ? sc : 0;
            foreach (var f in x.SheetFields)
                sb.Append($"<tr><td>{E(f.Field)}</td><td class=\"num\">{(f.Missing > 0 ? $"<b style=\"color:{red}\">{f.Missing}</b>" : "0")} <span class=\"muted\">of {sheets}</span></td></tr>");
            sb.Append("</table><div class=\"muted\" style=\"margin-top:8px\">The full submission check (client requirements, naming, exports, transmittal) is planned for Phase 5.</div></div></td>");

            sb.Append("<td class=\"cell\" style=\"width:50%\"><div class=\"card\"><h3>Claude activity, last 7 days</h3>");
            var a = x.Activity;
            sb.Append($"<div class=\"kpi\"><b>{a.Previews}</b><span>Previews</span></div><div class=\"kpi\"><b>{a.Changes}</b><span>Changes applied</span></div><div class=\"kpi\"><b>{a.PanelApplied}/{a.PanelCancelled}</b><span>Applied / cancelled in panel</span></div>");
            sb.Append(ActivityBars(a, black));
            if (a.Latest.Count > 0)
            {
                sb.Append("<div style=\"margin-top:10px\"><b>Latest</b>");
                foreach (var l in a.Latest) sb.Append($"<div class=\"muted\">{E(Trim(l, 90))}</div>");
                sb.Append("</div>");
            }
            sb.Append("</div></td></tr></table>");

            // ---- coming next ----
            sb.Append("<h2>Coming next</h2><table class=\"row\"><tr>");
            foreach (var (title, phase, text) in new[]
            {
                ("Clash detection", "Phase 4", "Clash tests between categories and linked models, with clash status tracked between runs. Results will appear here: open clashes by level, new and resolved since the last run."),
                ("Design and code compliance", "Phase 6", "Rule packs approved by ACE engineers: room sizes, door and corridor widths, stairs, travel distance. Pass and fail counts per rule will appear here."),
                ("Warning solver", "Phase 2", "Safe automatic fixes for common warnings, always previewed. Warnings fixed per run will appear here."),
            })
                sb.Append($"<td class=\"cell\" style=\"width:33%\"><div class=\"soon\"><b>{E(title)}</b> <span class=\"tag plan\">{E(phase)}</span><div class=\"muted\" style=\"margin-top:6px\">{E(text)}</div></div></td>");
            sb.Append("</tr></table>");

            // ---- footer ----
            sb.Append("<div class=\"foot\"><b>How the score is calculated.</b> Each check deducts points up to a limit: warnings up to 20; imported CAD 10; in-place families 8; ");
            sb.Append("rooms not enclosed 8; views not on sheets 8 (above 30%); key parameters 8; unplaced rooms, rooms without doors, duplicate marks and unloaded links 6 each; ");
            sb.Append("narrow doors and sheet title block data 5 each; views without templates and empty sheets 4 each; project information 3. 85 and above is Good, 65 to 84 Fair.<br>");
            sb.Append($"Generated by ACE Revit MCP {E(x.AddinVersion)} in {N(x.ElapsedMs / 1000.0, "0.0")} s &nbsp;·&nbsp; {E(x.Revit)} &nbsp;·&nbsp; {E(x.PathName)}</div>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static IEnumerable<(string, bool, string, string)> ToolStatus(Insights x)
        {
            Check C(string name) => x.Checks.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.Ordinal));
            int Cnt(string name) => C(name)?.Count ?? 0;
            var w = x.Counts.TryGetValue("Warnings", out var wv) ? wv : 0;
            var completeness = x.Completeness.Where(c => c.Total > 0).ToList();
            var avg = completeness.Count == 0 ? 100 : completeness.Average(c => c.Percent);
            yield return ("Model health audit", true, "Live", $"Score <b>{x.Score}</b>/100 ({E(x.Grade)}), {x.Checks.Count} checks");
            yield return ("Warnings", true, "Live", $"{N(w)} warnings{(x.WarningTypes.Count > 0 ? $"; most frequent: {E(Trim(x.WarningTypes[0].Text, 70))} ({x.WarningTypes[0].Count})" : "")}. Automatic solver: Phase 2");
            yield return ("Rooms and doors QA", true, "Live", $"{Cnt("Unplaced rooms")} unplaced, {Cnt("Rooms not enclosed")} not enclosed, {Cnt("Rooms without a door")} without a door, {Cnt("Doors narrower")} narrow doors, {Cnt("Duplicate")} duplicate marks");
            yield return ("Parameter check", true, "Live", $"{N(avg, "0")}% of key door, window and room parameters filled");
            yield return ("Views and sheets", true, "Live", $"{Cnt("Views not on sheets")} views not on sheets, {Cnt("Views without")} without a template, {Cnt("Empty sheets")} empty sheets");
            yield return ("Submission readiness", true, "Preview", $"{Cnt("Sheets with missing")} sheets with missing title block data, {Cnt("Project information")} project information fields empty. Full check: Phase 5");
            yield return ("Claude activity", true, "Live", $"{x.Activity.Previews} previews and {x.Activity.Changes} changes in the last 7 days");
            yield return ("Clash detection", false, "Planned, Phase 4", "<span class=\"muted\">No clash results yet</span>");
            yield return ("Design and code compliance", false, "Planned, Phase 6", "<span class=\"muted\">No rule packs yet</span>");
        }

        private static string Gauge(int score, string black, string red)
        {
            const double r = 42, c = 2 * Math.PI * r;
            var filled = c * score / 100.0;
            var colour = score < 65 ? red : black;
            return $"<svg width=\"104\" height=\"104\" viewBox=\"0 0 104 104\"><circle cx=\"52\" cy=\"52\" r=\"{N(r)}\" fill=\"none\" stroke=\"#DDDDDD\" stroke-width=\"10\"/>" +
                   $"<circle cx=\"52\" cy=\"52\" r=\"{N(r)}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"10\" stroke-dasharray=\"{N(filled, "0.0")} {N(c, "0.0")}\" transform=\"rotate(-90 52 52)\"/></svg>";
        }

        private static string Trend(List<(DateTime Time, int Score)> history, string black, string red)
        {
            const int w = 240, h = 90;
            if (history.Count < 2)
                return $"<svg width=\"{w}\" height=\"{h}\"><line x1=\"0\" y1=\"{h - 1}\" x2=\"{w}\" y2=\"{h - 1}\" stroke=\"#CCCCCC\"/></svg><div class=\"muted\">The trend appears after the next snapshot.</div>";
            var pts = history.Select((p, i) => (x: 6 + i * (w - 12.0) / (history.Count - 1), y: h - 8 - p.Score * (h - 16) / 100.0)).ToList();
            var line = string.Join(" ", pts.Select(p => $"{N(p.x, "0.0")},{N(p.y, "0.0")}"));
            var last = pts.Last();
            var first = history.First().Score;
            var now = history.Last().Score;
            return $"<svg width=\"{w}\" height=\"{h}\"><line x1=\"0\" y1=\"{h - 1}\" x2=\"{w}\" y2=\"{h - 1}\" stroke=\"#CCCCCC\"/>" +
                   $"<polyline points=\"{line}\" fill=\"none\" stroke=\"{black}\" stroke-width=\"2\"/><circle cx=\"{N(last.x, "0.0")}\" cy=\"{N(last.y, "0.0")}\" r=\"4\" fill=\"{red}\"/></svg>" +
                   $"<div class=\"muted\">From {first} to <b style=\"color:{black}\">{now}</b> since {history.First().Time:d MMM}</div>";
        }

        private static string ActivityBars(Activity a, string black)
        {
            var max = Math.Max(1, a.PerDay.Count == 0 ? 1 : a.PerDay.Max(d => d.Count));
            var sb = new StringBuilder("<table style=\"width:100%;border-collapse:collapse;margin-top:4px\"><tr style=\"height:70px\">");
            foreach (var d in a.PerDay)
                sb.Append($"<td style=\"vertical-align:bottom;text-align:center;padding:0 3px\"><div style=\"background:{black};height:{Math.Max(d.Count > 0 ? 3 : 0, (int)(64.0 * d.Count / max))}px\"></div></td>");
            sb.Append("</tr><tr>");
            foreach (var d in a.PerDay) sb.Append($"<td class=\"muted\" style=\"text-align:center;font-size:11px\">{d.Day:ddd}<br>{d.Count}</td>");
            sb.Append("</tr></table>");
            return sb.ToString();
        }

        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static string LogoDataUri()
        {
            try
            {
                var file = Path.Combine(Branding.Folder, "ace-mark.png");
                var brand = Path.Combine(Branding.Folder, "brand.json");
                if (File.Exists(brand) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(brand))?["mark"]?.ToString() is string m && m.Length > 0)
                    file = Path.IsPathRooted(m) ? m : Path.Combine(Branding.Folder, m);
                if (!File.Exists(file)) return null;
                var mime = file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
                return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
            }
            catch { return null; }
        }

        /// <summary>Saves the report to Documents\ACE Insights\&lt;model&gt;\ and returns the path.</summary>
        public static string Save(Insights x, string html)
        {
            var safe = string.Concat((x.Model ?? "Model").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ACE Insights", safe);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{safe} - Insights {x.Time:yyyy-MM-dd HHmm}.html");
            File.WriteAllText(path, html, new UTF8Encoding(false));
            return path;
        }
    }
}
