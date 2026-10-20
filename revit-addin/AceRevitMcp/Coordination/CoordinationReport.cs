using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Dashboard;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// The coordination meeting report: every open clash issue with a 3D picture (section box around the issue,
    /// this model's elements in ACE Red), the responsible discipline, what caused it and where it is, plus a CSV
    /// issue list for Excel. The pictures are taken in temporary views that are rolled back: the model is untouched.
    /// </summary>
    internal static class CoordinationReport
    {
        /// <summary>
        /// { max_issues: 12, test: "STR vs MEP" (default: all stored tests), rerun: false, pictures: true }, or with interference:
        /// true the per-clash rows of an interference report (see Interference).
        /// </summary>
        public static JsonNode Build(UIApplication app, JsonObject args)
        {
            if (Args.Bool(args, "interference")) return Interference(app, args);
            var host = Args.RequireDoc(app);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Args.Bool(args, "rerun")) ClashCommands.Run(app, new JsonObject { ["test"] = Args.Str(args, "test") ?? "all" });

            var test = Args.Str(args, "test");
            var current = Clashes.Current(host);
            if (test != null) current = current.Where(c => string.Equals(c.Test, test, StringComparison.OrdinalIgnoreCase)).ToList();
            if (current.Count == 0)
                throw new CommandException("No clash results for this model yet. Run a clash test first (run_clash_test), or call again with rerun: true.");
            // Only what the latest run of each test looked at counts; other stored results are reported apart (stored).
            var latestRuns = InterferenceLogic.LatestRuns(Clashes.LoadRuns(host));
            var clashes = current.Where(c => InterferenceLogic.Rechecked(c, latestRuns)).ToList();
            var notRechecked = current.Where(c => !InterferenceLogic.Rechecked(c, latestRuns)).ToList();

            var issues = ClashLogic.Issues(clashes);
            var max = Math.Max(1, Math.Min(40, Args.Int(args, "max_issues", 12)));
            var shown = issues.Take(max).ToList();

            var time = DateTime.Now;
            var safe = string.Concat(host.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ACE Insights", safe);
            var imgFolder = $"{safe} - Coordination {time:yyyy-MM-dd HHmm} images";
            var pictures = new Dictionary<ClashIssue, string>();
            var notes = new List<string>();
            if (Args.Bool(args, "pictures", true) && shown.Count > 0)
            {
                try { pictures = Pictures(host, shown, Path.Combine(dir, imgFolder), imgFolder, notes); }
                catch (Exception ex) { notes.Add("Pictures could not be made: " + ex.Message); Log.Warn($"Coordination pictures: {ex}"); }
            }

            var html = Render(host.Title, clashes, issues, shown, pictures, notes, time);
            var report = DashboardHtml.SaveAs(host.Title, "Coordination", time, html);
            var csv = Path.ChangeExtension(report, ".csv");
            File.WriteAllText(csv, Csv(issues), new UTF8Encoding(true));   // BOM: Excel opens UTF-8 correctly

            if (Args.Bool(args, "open", false))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report) { UseShellExecute = true }); } catch { }

            if (notRechecked.Count > 0) notes.Add($"{notRechecked.Count} stored result(s) the latest runs did not look at are not counted (see stored).");
            if (Clashes.TakeMigrationNote() is string moved) notes.Add(moved);
            return new JsonObject
            {
                ["storeKey"] = Clashes.StoreKey(host),
                ["stored"] = ClashCommands.StoredJson(notRechecked),
                ["htmlReport"] = report,
                ["excelList"] = csv,
                ["issues"] = issues.Count,
                ["openClashes"] = clashes.Count(ClashLogic.IsOpen),
                ["withPictures"] = pictures.Count,
                ["byResponsible"] = new JsonObject(issues.GroupBy(i => i.Responsible).OrderByDescending(g => g.Count())
                    .Select(g => new KeyValuePair<string, JsonNode>(g.Key ?? "-", g.Count()))),
                ["top"] = new JsonArray(shown.Select((i, n) => (JsonNode)new JsonObject
                {
                    ["no"] = n + 1, ["issue"] = i.Title, ["responsible"] = i.Responsible, ["levels"] = i.Levels, ["clashes"] = i.Count,
                    ["causedBy"] = i.Clashes.Select(c => c.CausedBy).FirstOrDefault(x => x != null),
                }).ToArray()),
                ["notes"] = new JsonArray(notes.Select(x => (JsonNode)x).ToArray()),
                ["seconds"] = Math.Round(sw.ElapsedMilliseconds / 1000.0, 1),
            };
        }

        // ---- interference report rows ------------------------------------------------------------------------------

        /// <summary>
        /// coordination_report { interference: true, test, rerun, a_categories, b_categories, materials, exclude_materials,
        /// a_materials, b_materials, tolerance_mm, level, pictures: false, max_issues }: one row per clash of the stored results of
        /// the test (or of a rerun with the same filters), grouped by issue, as JSON: { model, test, tests, run, coordinates,
        /// counts, rows, notes }. Pictures only with pictures: true. The server writes the files.
        /// </summary>
        private static JsonNode Interference(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var test = Args.Str(args, "test");
            JsonNode ran = null;
            if (Args.Bool(args, "rerun"))
            {
                var custom = args["a_categories"] is JsonArray || args["b_categories"] is JsonArray;
                if (custom && string.IsNullOrWhiteSpace(test)) throw new CommandException("A rerun with a_categories / b_categories needs the test's name (test), so its results are stored under it.");
                var run = new JsonObject { ["test"] = test ?? "all" };
                foreach (var k in new[] { "a_categories", "b_categories", "a_disciplines", "b_disciplines", "materials", "exclude_materials", "a_materials", "b_materials",
                                          "tolerance_mm", "clearance_mm", "level", "max_elements" })
                    if (args[k] != null) run[k] = args[k].DeepClone();
                ran = ClashCommands.Run(app, run);
            }
            if (!Args.Bool(args, "all_documents"))
            {
                var one = ModelRows(app, host, args, test, ran, true);
                one["seconds"] = Math.Round(sw.ElapsedMilliseconds / 1000.0, 1);
                return one;
            }
            // Every open project model with stored results (the rerun and the pictures are for the active model only).
            var models = new JsonArray();
            var skipped = new JsonArray();
            foreach (var d in new[] { host }.Concat(app.Application.Documents.Cast<Document>().Where(d => !d.Equals(host) && !d.IsLinked && !d.IsFamilyDocument)))
            {
                try { models.Add(ModelRows(app, d, args, test, d.Equals(host) ? ran : null, d.Equals(host))); }
                catch (CommandException ex) { skipped.Add(new JsonObject { ["model"] = d.Title, ["why"] = ex.Message }); }
            }
            return new JsonObject { ["models"] = models, ["skipped"] = skipped, ["seconds"] = Math.Round(sw.ElapsedMilliseconds / 1000.0, 1) };
        }

        private static JsonNode RunJson(ClashRun r)
        {
            JsonArray A(string[] v) => new JsonArray((v ?? new string[0]).Select(x => (JsonNode)x).ToArray());
            var f = new JsonObject();
            foreach (var kv in r.Filtered ?? new Dictionary<string, int>()) f[kv.Key] = kv.Value;
            return new JsonObject
            {
                ["test"] = r.Test, ["run"] = r.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ["toleranceMm"] = r.ToleranceMm, ["clearanceMm"] = r.ClearanceMm, ["level"] = r.Level,
                ["aCategories"] = A(r.ACategories), ["bCategories"] = A(r.BCategories), ["aMaterials"] = A(r.AMaterials), ["bMaterials"] = A(r.BMaterials),
                ["excludeMaterials"] = A(r.ExcludeMaterials), ["filtered"] = f, ["clashes"] = r.Clashes, ["open"] = r.Open, ["complete"] = r.Complete,
            };
        }

        /// <summary>One model's interference rows: { model, test, tests, run, coordinates, counts, rows (all, no cap), filtered?, notes }.</summary>
        private static JsonObject ModelRows(UIApplication app, Document host, JsonObject args, string test, JsonNode ran, bool active)
        {
            var notes = new List<string>();
            var clashes = Clashes.Current(host);
            if (test != null) clashes = clashes.Where(c => string.Equals(c.Test, test, StringComparison.OrdinalIgnoreCase)).ToList();
            // A stored run with zero clashes is a report with no rows; only a test that never ran is an error.
            var runs = InterferenceLogic.RunsFor(Clashes.LoadRuns(host), test);
            if (InterferenceLogic.NothingToReport(test, clashes.Count, runs.Count, host.Title) is string nothing) throw new CommandException(nothing);

            // Family and type from the model where the element can be found (this model and its loaded links), else from the stored names.
            var names = new Dictionary<string, (string Family, string Type)>(StringComparer.Ordinal);
            var sources = Clashes.Sources(app, host).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Doc);
            void Name(string model, string uid)
            {
                if (uid == null || names.ContainsKey(uid) || model == null || !sources.TryGetValue(model, out var d)) return;
                try
                {
                    var e = d.GetElement(uid);
                    if (e is FamilyInstance fi) names[uid] = (fi.Symbol.FamilyName, fi.Symbol.Name);
                    else if (e != null && d.GetElement(e.GetTypeId()) is ElementType et) names[uid] = (et.FamilyName, et.Name);
                }
                catch { /* stored names are used */ }
            }
            foreach (var c in clashes) { Name(c.SourceA, c.UA); Name(c.SourceB, c.UB); }

            var origin = (0.0, 0.0, 0.0);
            try { var p = BasePoint.GetProjectBasePoint(host).Position; origin = (Lengths.Mm(p.X), Lengths.Mm(p.Y), Lengths.Mm(p.Z)); }
            catch (Exception ex) { notes.Add("The project base point could not be read: coordinates are internal. " + ex.Message); }

            var latestRuns = InterferenceLogic.LatestRuns(runs);
            var rows = InterferenceLogic.Rows(clashes, origin, names, latestRuns);
            var notRechecked = clashes.Where(c => !InterferenceLogic.Rechecked(c, latestRuns)).ToList();
            var pictures = new Dictionary<string, string>(StringComparer.Ordinal);
            if (active && Args.Bool(args, "pictures", false))
            {
                var max = Math.Max(1, Math.Min(40, Args.Int(args, "max_issues", 12)));
                var shown = ClashLogic.Issues(clashes.Where(c => InterferenceLogic.Rechecked(c, latestRuns)).ToList()).Take(max).ToList();
                var time = DateTime.Now;
                var safe = string.Concat(host.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ACE Insights", safe);
                var imgFolder = $"{safe} - Interference {time:yyyy-MM-dd HHmm} images";
                try { foreach (var kv in Pictures(host, shown, Path.Combine(dir, imgFolder), imgFolder, notes)) pictures[kv.Key.Key] = Path.Combine(dir, kv.Value); }
                catch (Exception ex) { notes.Add("Pictures could not be made: " + ex.Message); Log.Warn($"Interference pictures: {ex}"); }
            }

            var counts = new JsonObject();
            foreach (var kv in InterferenceLogic.Counts(rows)) counts[kv.Key] = kv.Value;
            var tests = clashes.Select(c => c.Test).Concat(runs.Select(r => r.Test)).Where(t => t != null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal).ToList();
            var result = new JsonObject
            {
                ["model"] = host.Title, ["storeKey"] = Clashes.StoreKey(host), ["stored"] = ClashCommands.StoredJson(notRechecked),
                ["test"] = test ?? string.Join(", ", tests), ["tests"] = new JsonArray(tests.Select(t => (JsonNode)t).ToArray()),
                ["run"] = InterferenceLogic.RunDate(clashes, runs.Take(test == null ? int.MaxValue : 1)),
                ["runs"] = new JsonArray(runs.Select(RunJson).ToArray()),
                ["coordinates"] = "project coordinates (relative to the project base point), metres",
                ["counts"] = counts,
                ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["issue"] = r.Issue, ["issueTitle"] = r.IssueTitle, ["issueKey"] = r.IssueKey, ["clashKey"] = r.ClashKey, ["test"] = r.Test, ["status"] = r.Status,
                    ["aId"] = r.AId, ["aModel"] = r.AModel, ["aCategory"] = r.ACategory, ["aFamily"] = r.AFamily, ["aType"] = r.AType,
                    ["bId"] = r.BId, ["bModel"] = r.BModel, ["bCategory"] = r.BCategory, ["bFamily"] = r.BFamily, ["bType"] = r.BType,
                    ["level"] = r.Level, ["depthMm"] = r.DepthMm, ["x"] = r.X, ["y"] = r.Y, ["z"] = r.Z, ["responsible"] = r.Responsible, ["rechecked"] = r.Rechecked,
                    ["picture"] = r.IssueKey != null && pictures.TryGetValue(r.IssueKey, out var pic) ? pic : null,
                }).ToArray()),
                ["notes"] = new JsonArray(notes.Select(x => (JsonNode)x).ToArray()),
            };
            // What the material filter left out: from the stored run of each test reported (the latest per test), summed.
            var latest = runs.GroupBy(r => r.Test, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (latest.Any(r => r.Filtered != null && r.Filtered.Count > 0) || latest.Any(r => (r.AMaterials?.Length ?? 0) + (r.BMaterials?.Length ?? 0) + (r.ExcludeMaterials?.Length ?? 0) > 0))
            {
                var filtered = new JsonObject();
                foreach (var r in latest) foreach (var kv in r.Filtered ?? new Dictionary<string, int>()) filtered[kv.Key] = (filtered[kv.Key]?.GetValue<int>() ?? 0) + kv.Value;
                result["filtered"] = filtered;
            }
            if (test != null && latest.Count == 1) result["filters"] = RunJson(latest[0]);
            if (pictures.Count > 0) result["withPictures"] = pictures.Count;
            return result;
        }

        // ---- pictures ------------------------------------------------------------------------------------------

        private static double Ft(double mm) => Lengths.Ft(mm);

        private static Dictionary<ClashIssue, string> Pictures(Document doc, List<ClashIssue> issues, string folder, string relFolder, List<string> notes)
        {
            var result = new Dictionary<ClashIssue, string>();
            var type3D = ViewTools.ViewType(doc, ViewFamily.ThreeDimensional);
            if (type3D == null) { notes.Add("No 3D view type in this model: pictures skipped."); return result; }
            if (doc.IsReadOnly) { notes.Add("The model is read-only: pictures skipped."); return result; }
            Directory.CreateDirectory(folder);

            var highlight = ViewTools.Highlight(doc, Branding.Accent, lineWeight: 5);

            var stamp = DateTime.Now.ToString("HHmmssfff");
            // Everything happens in a group that is rolled back: the temporary views never reach the model.
            using (ChangeTracker.Temporary())
            using (var group = new TransactionGroup(doc, "ACE coordination pictures"))
            {
                group.Start();
                var views = new List<(ClashIssue Issue, View3D View)>();
                using (var t = new Transaction(doc, "ACE coordination views"))
                {
                    t.Start();
                    foreach (var issue in issues)
                    {
                        try
                        {
                            var pts = issue.Clashes.Select(c => new XYZ(Ft(c.X), Ft(c.Y), Ft(c.Z))).ToList();
                            var min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
                            var maxP = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
                            var mx = Ft(1500); var mz = Ft(1200);
                            // Very spread issues (a long duct run): keep the box readable.
                            var cap = Ft(30000);
                            if (maxP.X - min.X > cap || maxP.Y - min.Y > cap) { var c0 = pts[0]; min = c0; maxP = c0; }
                            var v = View3D.CreateIsometric(doc, type3D.Id);
                            // A unique name: the one picture export finds each view's file by its name.
                            try { v.Name = $"ACE issue {views.Count + 1:000} {stamp}"; } catch { }
                            v.DetailLevel = ViewDetailLevel.Fine;
                            v.DisplayStyle = DisplayStyle.ShadingWithEdges;
                            v.SetSectionBox(new BoundingBoxXYZ { Min = new XYZ(min.X - mx, min.Y - mx, min.Z - mz), Max = new XYZ(maxP.X + mx, maxP.Y + mx, maxP.Z + mz) });
                            foreach (var c in issue.Clashes)
                            {
                                if (c.SourceA == doc.Title) try { v.SetElementOverrides(new ElementId(c.IdA), highlight); } catch { }
                                if (c.SourceB == doc.Title) try { v.SetElementOverrides(new ElementId(c.IdB), highlight); } catch { }
                            }
                            views.Add((issue, v));
                        }
                        catch (Exception ex) { Log.Warn($"Coordination view for {issue.Title}: {ex.Message}"); }
                    }
                    t.Commit();
                }

                // All pictures in one export; the names are those of the views made above.
                var named = views.Select((v, i) => (Issue: v.Issue, View: v.View, Name: $"issue-{i + 1:00}")).ToList();
                try
                {
                    var files = ViewTools.ExportPngs(doc, named.Select(v => (v.View.Id, v.View.Name, v.Name)).ToList(), folder, 900);
                    foreach (var v in named) if (files.ContainsKey(v.Name)) result[v.Issue] = relFolder + "/" + v.Name + ".png";
                }
                catch (Exception ex)
                {
                    // One bad view must not cost every picture: fall back to one export per view.
                    Log.Warn($"Coordination pictures (one export): {ex.Message}; exporting one by one");
                    foreach (var v in named)
                        try { if (ViewTools.ExportPng(doc, v.View.Id, folder, v.Name, 900) != null) result[v.Issue] = relFolder + "/" + v.Name + ".png"; }
                        catch (Exception one) { Log.Warn($"Coordination picture {v.Name}: {one.Message}"); }
                }
                group.RollBack();
            }
            if (result.Count < issues.Count) notes.Add($"{issues.Count - result.Count} issue(s) have no picture (see the add-in log).");
            return result;
        }

        // ---- report --------------------------------------------------------------------------------------------

        private static string E(string s) => DashboardHtml.E(s);

        private static string Render(string model, List<Clash> clashes, List<ClashIssue> issues, List<ClashIssue> shown,
            Dictionary<ClashIssue, string> pictures, List<string> notes, DateTime time)
        {
            var red = DashboardHtml.Hex(Branding.Accent);
            var tests = string.Join(", ", clashes.Select(c => c.Test).Distinct());
            var sb = DashboardHtml.Begin($"{model} | Coordination", "Coordination report",
                $"{E(model)} &nbsp;·&nbsp; {time:d MMMM yyyy, HH:mm} &nbsp;·&nbsp; {E(tests)}",
                "Issues for the coordination meeting: who gives way, what caused it, where it is");
            sb.Append("<style>.issue{page-break-inside:avoid;margin-top:18px;border-top:2px solid #E6E6E6;padding-top:14px}" +
                      ".issue img{width:100%;border:1px solid #E6E6E6}.lines td{border-bottom:1px solid #A0A0A0;height:22px}" +
                      "@media print{.page{max-width:none}}</style>");

            var open = clashes.Where(ClashLogic.IsOpen).ToList();
            sb.Append("<table class=\"row\"><tr>");
            foreach (var (label, n, key) in new[] {
                ("Open issues", issues.Count, true), ("Open clashes", open.Count, false),
                ("New since last run", open.Count(c => c.Status == "new"), false), ("Reopened", open.Count(c => c.Reopened > 0), false) })
                sb.Append($"<td class=\"cell\" style=\"width:25%\"><div class=\"card\"><h3>{E(label)}</h3><div class=\"big\"{(key && n > 0 ? $" style=\"color:{red}\"" : "")}>{n}</div></div></td>");
            sb.Append("</tr></table>");

            sb.Append("<h2>Actions by discipline</h2><table class=\"list\"><tr><th>Responsible</th><th class=\"num\">Issues</th><th class=\"num\">Clashes</th><th>Levels</th><th>Caused by recent changes of</th></tr>");
            foreach (var g in issues.GroupBy(i => i.Responsible).OrderByDescending(g => g.Count()))
            {
                var who = string.Join(", ", g.SelectMany(i => i.Clashes).Select(c => c.CausedBy).Where(x => x != null).Distinct());
                var levels = string.Join(", ", g.SelectMany(i => i.Clashes).Select(c => c.Level).Where(l => l != null).Distinct().OrderBy(l => l));
                sb.Append($"<tr><td><b>{E(g.Key)}</b></td><td class=\"num\">{g.Count()}</td><td class=\"num\">{g.Sum(i => i.Count)}</td><td>{E(levels)}</td><td class=\"muted\">{E(who)}</td></tr>");
            }
            sb.Append("</table>");

            sb.Append($"<h2>Issues</h2><div class=\"muted\">The {shown.Count} largest of {issues.Count} open issues. Each issue is one element that has to move and everything it hits. " +
                      "This model's elements are shown in ACE Red; linked models in their own colours. The full list is in the CSV file next to this report.</div>");
            var no = 0;
            foreach (var i in shown)
            {
                no++;
                var cause = i.Clashes.Select(c => c.Cause).FirstOrDefault(x => x != null && !x.StartsWith("both elements are unchanged"));
                var reason = i.Clashes.Select(c => c.Reason).FirstOrDefault(x => x != null);
                var hits = i.Clashes.Select(c => c.Group == c.Test + "|" + c.UA ? $"{c.CatB}: {c.NameB} (id {c.IdB}, {c.SourceB})" : $"{c.CatA}: {c.NameA} (id {c.IdA}, {c.SourceA})").Distinct().Take(8).ToList();
                sb.Append("<div class=\"issue\"><table style=\"width:100%;border-collapse:collapse\"><tr>");
                if (pictures.TryGetValue(i, out var img))
                    sb.Append($"<td style=\"width:48%;vertical-align:top;padding-right:18px\"><img src=\"{E(img.Replace('\\', '/'))}\" alt=\"Issue {no}\"></td>");
                sb.Append("<td style=\"vertical-align:top\">");
                sb.Append($"<div class=\"muted\">Issue {no} &nbsp;·&nbsp; {E(i.Test)}{(i.Clashes.Any(c => c.Reopened > 0) ? " &nbsp;·&nbsp; <b>reopened</b>" : "")}{(i.Clashes.Any(c => c.Status == "new") ? " &nbsp;·&nbsp; <b>new</b>" : "")}</div>");
                sb.Append($"<h3 style=\"margin:4px 0 10px;font-size:16px\">{E(DashboardHtml.Trim(i.Title, 140))}</h3>");
                sb.Append("<table class=\"list\">");
                void Row(string k, string v) { if (!string.IsNullOrEmpty(v)) sb.Append($"<tr><td class=\"muted\" style=\"width:34%\">{E(k)}</td><td>{v}</td></tr>"); }
                Row("Responsible", $"<b style=\"color:{red}\">{E(i.Responsible)}</b>" + (reason != null ? $"<div class=\"muted\">{E(reason)}</div>" : ""));
                Row("Cause", E(cause));
                Row("Element to move", $"{E(i.Title.Split(new[] { " hits " }, StringSplitOptions.None)[0])} <span class=\"muted\">(id {i.ElementId}, {E(i.Model)})</span>");
                Row("It hits", string.Join("<br>", hits.Select(E)) + (i.Count > hits.Count ? $"<br><span class=\"muted\">and {i.Count - hits.Count} more</span>" : ""));
                Row("Level", E(i.Levels));
                Row("Clashes / deepest", $"{i.Count} &nbsp;/&nbsp; {i.MaxDepthMm:0} mm");
                Row("Location (mm)", E($"{i.X:0}, {i.Y:0}, {i.Z:0}"));
                sb.Append("</table>");
                sb.Append("<table class=\"lines\" style=\"width:100%;border-collapse:collapse;margin-top:12px\"><tr><td class=\"muted\" style=\"width:34%;border:none\">Action</td><td></td></tr>" +
                          "<tr><td class=\"muted\" style=\"border:none\">Owner / due</td><td></td></tr></table>");
                sb.Append("</td></tr></table></div>");
            }
            if (notes.Count > 0) sb.Append("<h2>Notes</h2>" + string.Join("", notes.Select(x => $"<div class=\"muted\">{E(x)}</div>")));
            sb.Append("<div class=\"foot\">Responsibility follows ACE priority: the element that is easier to move gives way (structure first, then walls and floors, ducts, pipes, cable trays, conduits). " +
                      "Cause: the side added, moved or retyped since the snapshot before the last clash run (change tracker). Pictures are taken in temporary views that were rolled back; the model was not changed.</div>");
            sb.Append("</div></body></html>");
            return DashboardHtml.Ascii(sb.ToString());
        }

        private static string Csv(List<ClashIssue> issues)
        {
            string Q(object v) { var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; return "\"" + s.Replace("\"", "\"\"") + "\""; }
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[] { "No", "Test", "Issue", "Responsible", "Caused by", "Cause", "Levels", "Clashes", "Deepest (mm)", "Element id", "Model", "X (mm)", "Y (mm)", "Z (mm)", "Status", "Action", "Owner", "Due" }.Select(Q)));
            var n = 0;
            foreach (var i in issues)
            {
                var status = i.Clashes.Any(c => c.Reopened > 0) ? "reopened" : i.Clashes.Any(c => c.Status == "new") ? "new" : "active";
                sb.AppendLine(string.Join(",", new object[] { ++n, i.Test, i.Title, i.Responsible, i.Clashes.Select(c => c.CausedBy).FirstOrDefault(x => x != null),
                    i.Clashes.Select(c => c.Cause).FirstOrDefault(x => x != null), i.Levels, i.Count, i.MaxDepthMm, i.ElementId, i.Model, i.X, i.Y, i.Z, status, "", "", "" }.Select(Q)));
            }
            return sb.ToString();
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class CoordinationReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show("ACE Coordination report", "Open a model first."); return Result.Cancelled; }
                var hasResults = Clashes.Current(doc).Count > 0;
                CoordinationReport.Build(commandData.Application, new JsonObject { ["rerun"] = !hasResults, ["open"] = true });
                return Result.Succeeded;
            }
            catch (CommandException ex) { TaskDialog.Show("ACE Coordination report", ex.Message); return Result.Cancelled; }
            catch (Exception ex) { Log.Error($"Coordination report: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }
}
