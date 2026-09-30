using System;
using System.Collections.Generic;
using System.Linq;
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
    internal static class ClashCommands
    {
        /// <summary>{ test: "STR vs MEP" | "all" | custom, tolerance_mm, clearance_mm, level, max_elements, primary_model, with_model, show }</summary>
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            var tests = PickTests(args);
            var level = Args.Str(args, "level");
            var max = Args.Int(args, "max_elements", 5000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = new List<Clash>();
            var perTest = new JsonArray();
            var notes = new List<string>();
            foreach (var t in tests)
            {
                var testNotes = new List<string>();
                var withModel = Args.Str(args, "with_model");
                var primaryModel = Args.Str(args, "primary_model");
                var found = Clashes.Run(app, host, t, level, max, testNotes, out var complete, withModel, primaryModel);
                // Only what this run looked at can become resolved: one level, one compared model, or nothing if it was cut short.
                bool InLevel(Clash c) => level == null || string.Equals(c.Level, level, StringComparison.OrdinalIgnoreCase);
                var primaryName = primaryModel ?? host.Title;
                bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                bool InModel(Clash c) => withModel == null
                    ? (primaryModel == null || Is(c.SourceA, primaryName) || Is(c.SourceB, primaryName))
                    : ((Is(c.SourceA, withModel) || Is(c.SourceA, primaryName)) && (Is(c.SourceB, withModel) || Is(c.SourceB, primaryName)));
                Func<Clash, bool> scope = !complete ? (c => false) : (level != null || withModel != null || primaryModel != null) ? (c => InLevel(c) && InModel(c)) : null;
                if (!complete) testNotes.Add("Stored clashes were not marked resolved because the run was cut short.");
                var stored = Clashes.Load(host, t.Name);
                DateTime? previousRun = stored.Count > 0 ? stored.Max(c => c.LastSeen) : (DateTime?)null;
                var merged = ClashLogic.Merge(stored, found, scope, DateTime.Now);
                try { Clashes.Explain(app, host, merged.Where(c => c.Status == "new" && c.Cause == null).ToList(), previousRun, testNotes); }
                catch (Exception ex) { Log.Warn($"Clash causes: {ex.Message}"); }
                var testIssues = ClashLogic.Issues(merged);
                Clashes.Save(host, t.Name, merged);
                all.AddRange(merged);
                notes.AddRange(testNotes.Select(n => $"{t.Name}: {n}"));
                perTest.Add(new JsonObject
                {
                    ["test"] = t.Name, ["toleranceMm"] = t.ToleranceMm, ["clearanceMm"] = t.ClearanceMm,
                    ["open"] = merged.Count(c => c.Status == "new" || c.Status == "active"),
                    ["new"] = merged.Count(c => c.Status == "new"), ["active"] = merged.Count(c => c.Status == "active"),
                    ["resolved"] = merged.Count(c => c.Status == "resolved"), ["approved"] = merged.Count(c => c.Status == "approved"),
                    ["issues"] = testIssues.Count,
                    ["reopened"] = merged.Count(c => Open(c) && c.Reopened > 0),
                    ["byResponsible"] = Group(merged.Where(Open), c => c.Responsible),
                    ["byLevel"] = Group(merged.Where(Open), c => c.Level),
                    ["byPair"] = Group(merged.Where(Open), c => $"{c.CatA} x {c.CatB}"),
                    ["notes"] = new JsonArray(testNotes.Select(n => (JsonNode)n).ToArray()),
                });
            }
            var issues = ClashLogic.Issues(all);
            Clashes.Last = all;
            Clashes.LastIssues = issues;
            Clashes.LastTest = string.Join(", ", tests.Select(t => t.Name));
            Clashes.LastHost = host.Title;
            Clashes.LastPath = DashboardHtml.SaveAs(host.Title, "Clashes", DateTime.Now, ClashHtml.Render(host.Title, all, notes, sw.ElapsedMilliseconds));
            if (Args.Bool(args, "show")) ReportWindow.ShowOrRefresh(app.MainWindowHandle, "Clash results", "run_clash_test", State);
            return new JsonObject
            {
                ["tests"] = perTest,
                ["topIssues"] = new JsonArray(issues.Take(20).Select(i => (JsonNode)new JsonObject
                {
                    ["issue"] = i.Title, ["clashes"] = i.Count, ["responsible"] = i.Responsible, ["levels"] = i.Levels,
                    ["elementId"] = i.ElementId, ["model"] = i.Model, ["maxDepthMm"] = i.MaxDepthMm, ["pointMm"] = $"{i.X}, {i.Y}, {i.Z}",
                    ["keys"] = new JsonArray(i.Clashes.Select(c => (JsonNode)c.Key).ToArray()),
                    ["causedBy"] = i.Clashes.Select(c => c.CausedBy).FirstOrDefault(x => x != null),
                    ["cause"] = i.Clashes.Select(c => c.Cause).FirstOrDefault(x => x != null && !x.StartsWith("both elements are unchanged")),
                }).ToArray()),
                ["topOpen"] = new JsonArray(all.Where(Open).OrderByDescending(c => c.DepthMm).Take(30).Select(c => (JsonNode)new JsonObject
                {
                    ["key"] = c.Key, ["status"] = c.Status, ["reopened"] = c.Reopened > 0 ? c.Reopened : null, ["kind"] = c.Kind, ["level"] = c.Level, ["depthMm"] = c.DepthMm,
                    ["a"] = $"{c.CatA}: {c.NameA} (id {c.IdA}, {c.SourceA})", ["b"] = $"{c.CatB}: {c.NameB} (id {c.IdB}, {c.SourceB})",
                    ["pointMm"] = $"{c.X}, {c.Y}, {c.Z}", ["responsible"] = c.Responsible, ["reason"] = c.Reason,
                    ["cause"] = c.Cause, ["causedBy"] = c.CausedBy,
                }).ToArray()),
                ["htmlReport"] = Clashes.LastPath,
                ["seconds"] = Math.Round(sw.ElapsedMilliseconds / 1000.0, 1),
                ["note"] = "Ids are ids inside the model named in brackets; only ids of this model can be selected here. Status is kept between runs (new, active, resolved, approved; a resolved clash that comes back is new again and counted as reopened). Issues group the clashes of one element that has to move: coordinate issue by issue, not clash by clash.",
            };
        }

        internal static bool Open(Clash c) => ClashLogic.IsOpen(c);

        private static JsonObject Group(IEnumerable<Clash> clashes, Func<Clash, string> key)
        {
            var o = new JsonObject();
            foreach (var g in clashes.GroupBy(c => key(c) ?? "-").OrderByDescending(g => g.Count()).Take(15)) o[g.Key] = g.Count();
            return o;
        }

        private static List<Clashes.TestSpec> PickTests(JsonObject args)
        {
            var name = Args.Str(args, "test") ?? "all";
            var tol = args["tolerance_mm"] is JsonValue tv && tv.TryGetValue<double>(out var t) ? t : (double?)null;
            var clear = args["clearance_mm"] is JsonValue cv && cv.TryGetValue<double>(out var c) ? c : 0;
            List<Clashes.TestSpec> chosen;
            if (name.Equals("all", StringComparison.OrdinalIgnoreCase)) chosen = Clashes.Standard.ToList();
            else if (args["a_categories"] is JsonArray || args["b_categories"] is JsonArray)
            {
                BuiltInCategory[] Cats(string key) => Args.Strings(args, key).Select(s => Enum.TryParse<BuiltInCategory>(s.StartsWith("OST_") ? s : "OST_" + s.Replace(" ", ""), true, out var b) ? b : BuiltInCategory.INVALID).Where(b => b != BuiltInCategory.INVALID).ToArray();
                chosen = new List<Clashes.TestSpec> { new Clashes.TestSpec
                {
                    Name = name,
                    A = new Clashes.SetSpec { Disciplines = Args.Strings(args, "a_disciplines").ToArray(), Categories = Cats("a_categories") },
                    B = new Clashes.SetSpec { Disciplines = Args.Strings(args, "b_disciplines").ToArray(), Categories = Cats("b_categories") },
                } };
                if (chosen[0].A.Categories.Length == 0 || chosen[0].B.Categories.Length == 0)
                    throw new CommandException("Custom test: give a_categories and b_categories as category names, e.g. [\"OST_PipeCurves\"] and [\"OST_StructuralFraming\"].");
            }
            else
            {
                var t0 = Clashes.Standard.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? throw new CommandException($"Unknown test '{name}'. Standard tests: {string.Join(", ", Clashes.Standard.Select(s => s.Name))}, or 'all', or a custom test with a_categories / b_categories.");
                chosen = new List<Clashes.TestSpec> { t0 };
            }
            return chosen.Select(s => new Clashes.TestSpec { Name = s.Name, A = s.A, B = s.B, ToleranceMm = tol ?? s.ToleranceMm, ClearanceMm = clear }).ToList();
        }

        /// <summary>Bridge "clash_results": the stored results of this model (no re-run), for the Clash Browser and Claude.</summary>
        public static JsonNode Results(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            if (Clashes.Last == null || Clashes.LastHost != host.Title)
            {
                var stored = Clashes.LoadAll(host);
                if (stored.Count > 0) { Clashes.Last = stored; Clashes.LastHost = host.Title; Clashes.LastTest = "stored results"; }
            }
            var all = Clashes.Last != null && Clashes.LastHost == host.Title ? Clashes.Last : new List<Clash>();
            var issues = ClashLogic.Issues(all);
            return new JsonObject
            {
                ["clashes"] = all.Count, ["open"] = all.Count(Open), ["issues"] = issues.Count,
                ["issueList"] = new JsonArray(issues.Take(30).Select(i => (JsonNode)new JsonObject
                {
                    ["issue"] = i.Title, ["responsible"] = i.Responsible, ["levels"] = i.Levels, ["clashes"] = i.Count,
                    ["keys"] = new JsonArray(i.Clashes.Select(c => (JsonNode)c.Key).ToArray()),
                }).ToArray()),
            };
        }

        /// <summary>{ test, keys: [...], status: approved | active | new, note }</summary>
        public static JsonNode SetStatus(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            var test = Args.Str(args, "test") ?? throw new CommandException("Give 'test'.");
            var status = Args.Str(args, "status") ?? "approved";
            if (!new[] { "approved", "active", "new" }.Contains(status)) throw new CommandException("status must be approved, active or new.");
            var keys = new HashSet<string>(Args.Strings(args, "keys"));
            var clashes = Clashes.Load(host, test);
            var changed = 0;
            foreach (var c in clashes.Where(c => keys.Contains(c.Key)))
            {
                c.Status = status;
                if (Args.Str(args, "note") is string n) c.Note = n;
                changed++;
            }
            Clashes.Save(host, test, clashes);
            if (Clashes.Last != null)
                foreach (var c in Clashes.Last.Where(c => keys.Contains(c.Key))) { c.Status = status; if (Args.Str(args, "note") is string n2) c.Note = n2; }
            return new JsonObject { ["updated"] = changed, ["status"] = status };
        }

        public static JsonNode Sources(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            return new JsonObject
            {
                ["models"] = new JsonArray(Clashes.Sources(app, host).Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["discipline"] = s.Discipline, ["relation"] = s.Relation }).ToArray()),
                ["standardTests"] = new JsonArray(Clashes.Standard.Select(t => (JsonNode)$"{t.Name} (tolerance {t.ToleranceMm} mm)").ToArray()),
                ["rules"] = "Responsibility: the element that is easier to move gives way (structure first, then ARC walls/floors, ducts, pipes, cable trays, conduits). Override in %APPDATA%\\ACE-RevitMCP\\clash-rules.json: {\"ranks\": {\"Pipes\": {\"rank\": 60, \"discipline\": \"MEP (plumbing)\"}}}.",
            };
        }

        /// <summary>Only elements of the active model can be selected in it.</summary>
        private static long[] HostIds(IEnumerable<Clash> clashes) =>
            clashes.SelectMany(c => new[] { c.SourceA == Clashes.LastHost ? c.IdA : 0, c.SourceB == Clashes.LastHost ? c.IdB : 0 }).Where(i => i > 0).Distinct().ToArray();

        internal static ReportWindow.State State()
        {
            if (Clashes.Last == null) return null;
            var open = Clashes.Last.Where(Open).ToList();
            var st = new ReportWindow.State
            {
                Path = Clashes.LastPath,
                Status = $"{open.Count} open clashes ({Clashes.LastTest}). Saved to Documents\\ACE Insights.",
            };
            // Only elements of the active model can be selected.
            var issues = ClashLogic.Issues(Clashes.Last);
            st.Status = $"{issues.Count} issues ({open.Count} open clashes, {Clashes.LastTest}). Saved to Documents\\ACE Insights.";
            var caused = open.Where(c => c.CausedBy != null).GroupBy(c => c.CausedBy).OrderByDescending(g => g.Count());
            foreach (var g in caused)
                st.Findings.Add(($"New from changes by {g.Key}: {g.Count()} clashes", HostIds(g)));
            foreach (var i in issues.Take(15))
                st.Findings.Add(($"Issue: {DashboardHtml.Trim(i.Title, 70)} ({i.Responsible})", HostIds(i.Clashes)));
            foreach (var g in open.GroupBy(c => c.Responsible).OrderByDescending(g => g.Count()))
            {
                st.Findings.Add(($"{g.Key}: {g.Count()} clashes", HostIds(g)));
            }
            foreach (var g in open.GroupBy(c => c.Level).OrderBy(g => g.Key))
                st.Findings.Add(($"   Level {g.Key}: {g.Count()}", HostIds(g)));
            return st;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class RunClashCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (commandData.Application.ActiveUIDocument?.Document == null) { TaskDialog.Show("ACE Clash test", "Open a model first."); return Result.Cancelled; }
                ClashCommands.Run(commandData.Application, new JsonObject { ["test"] = "all", ["show"] = true });
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Clash test: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ClashResultsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show("ACE Clash results", "Open a model first."); return Result.Cancelled; }
            if (Clashes.Last == null)
            {
                // Show the stored results of the last runs without re-running.
                var stored = Clashes.Standard.SelectMany(t => Clashes.Load(doc, t.Name)).ToList();
                if (stored.Count == 0) { TaskDialog.Show("ACE Clash results", "No clash results yet for this model. Click Run Clash Test first."); return Result.Cancelled; }
                Clashes.Last = stored; Clashes.LastTest = "stored results"; Clashes.LastHost = doc.Title;
                Clashes.LastPath = DashboardHtml.SaveAs(doc.Title, "Clashes", DateTime.Now, ClashHtml.Render(doc.Title, stored, new List<string> { "Stored results of the last runs (not re-run)." }, 0));
            }
            ReportWindow.ShowOrRefresh(commandData.Application.MainWindowHandle, "Clash results", "run_clash_test", ClashCommands.State);
            return Result.Succeeded;
        }
    }

    internal static class ClashHtml
    {
        private static string E(string s) => DashboardHtml.E(s);

        public static string Render(string model, List<Clash> clashes, List<string> notes, long ms)
        {
            var open = clashes.Where(ClashCommands.Open).ToList();
            var sb = DashboardHtml.Begin($"{model} | Clashes", "Clash detection",
                $"{E(model)} &nbsp;·&nbsp; {DateTime.Now:d MMMM yyyy, HH:mm} &nbsp;·&nbsp; {string.Join(", ", clashes.Select(c => c.Test).Distinct())}",
                "The model, its links and the other open discipline models");
            var red = DashboardHtml.Hex(Branding.Accent);
            sb.Append("<table class=\"row\"><tr>");
            foreach (var (label, n, key) in new[] { ("Open issues", ClashLogic.Issues(open).Count, true), ("Open clashes", open.Count, false), ("New since last run", clashes.Count(c => c.Status == "new"), false), ("Resolved", clashes.Count(c => c.Status == "resolved"), false), ("Approved", clashes.Count(c => c.Status == "approved"), false) })
                sb.Append($"<td class=\"cell\" style=\"width:20%\"><div class=\"card\"><h3>{E(label)}</h3><div class=\"big\"{(key && n > 0 ? $" style=\"color:{red}\"" : "")}>{n}</div></div></td>");
            sb.Append("</tr></table>");

            sb.Append("<table class=\"row\" style=\"margin-top:18px\"><tr><td class=\"cell\" style=\"width:50%\"><div class=\"card\"><h3>Responsible discipline (open)</h3>");
            var maxR = Math.Max(1, open.GroupBy(c => c.Responsible).Select(g => g.Count()).DefaultIfEmpty(1).Max());
            var first = true;
            foreach (var g in open.GroupBy(c => c.Responsible).OrderByDescending(g => g.Count()))
            {
                sb.Append($"<div style=\"margin-bottom:8px\"><table style=\"width:100%;border-collapse:collapse\"><tr><td>{E(g.Key)}</td><td class=\"num\"><b>{g.Count()}</b></td></tr></table><div class=\"bar\"><i class=\"{(first ? "key" : "")}\" style=\"width:{100 * g.Count() / maxR}%\"></i></div></div>");
                first = false;
            }
            if (open.Count == 0) sb.Append("<div class=\"muted\">No open clashes.</div>");
            sb.Append("</div></td><td class=\"cell\" style=\"width:50%\"><div class=\"card\"><h3>By level (open)</h3><table class=\"list\"><tr><th>Level</th><th class=\"num\">Clashes</th><th>Most frequent pair</th></tr>");
            foreach (var g in open.GroupBy(c => c.Level).OrderBy(g => g.Key))
                sb.Append($"<tr><td>{E(g.Key)}</td><td class=\"num\">{g.Count()}</td><td class=\"muted\">{E(g.GroupBy(c => $"{c.CatA} x {c.CatB}").OrderByDescending(x => x.Count()).First().Key)}</td></tr>");
            sb.Append("</table></div></td></tr></table>");

            var issues = ClashLogic.Issues(clashes);
            sb.Append("<h2>Issues</h2><div class=\"muted\" style=\"margin-bottom:8px\">Each issue is one element that has to move and everything it hits. Coordinate issue by issue.</div>");
            sb.Append("<table class=\"list\"><tr><th>#</th><th>Issue</th><th class=\"num\">Clashes</th><th>Level</th><th>Responsible</th><th class=\"num\">Max depth</th><th>Location (mm)</th></tr>");
            var row = 0;
            foreach (var i in issues.Take(100))
                sb.Append($"<tr><td>{++row}</td><td><b>{E(DashboardHtml.Trim(i.Title, 110))}</b><div class=\"muted\">id {i.ElementId} · {E(i.Model)}{(i.Clashes.Any(c => c.Reopened > 0) ? " · <b>reopened</b>" : "")}</div></td>" +
                          $"<td class=\"num\">{i.Count}</td><td>{E(i.Levels)}</td><td>{E(i.Responsible)}{(i.Clashes.Select(c => c.CausedBy).FirstOrDefault(x => x != null) is string who ? $"<div class='muted'>changed by {E(who)}</div>" : "")}</td><td class=\"num\">{i.MaxDepthMm:0} mm</td><td class=\"muted\">{i.X:0}, {i.Y:0}, {i.Z:0}</td></tr>");
            if (issues.Count == 0) sb.Append("<tr><td colspan=\"7\" class=\"muted\">No open issues.</td></tr>");
            if (issues.Count > 100) sb.Append($"<tr><td colspan=\"7\" class=\"muted\">... and {issues.Count - 100} more.</td></tr>");
            sb.Append("</table>");

            sb.Append("<h2>Clashes</h2><table class=\"list\"><tr><th>Status</th><th>Level</th><th>Element A</th><th>Element B</th><th class=\"num\">Depth</th><th>Responsible</th><th>Location (mm)</th></tr>");
            foreach (var c in clashes.OrderBy(c => c.Status == "resolved" || c.Status == "approved" ? 1 : 0).ThenByDescending(c => c.DepthMm).Take(400))
            {
                var tag = c.Status == "new" ? "fail" : c.Status == "active" ? "warn" : "plan";
                sb.Append($"<tr><td><span class=\"tag {tag}\">{E(c.Status)}</span>{(c.Reopened > 0 ? "<div class='muted'><b>reopened</b></div>" : "")}<div class=\"muted\">{E(c.Kind)}</div></td><td>{E(c.Level)}</td>" +
                          $"<td><b>{E(c.CatA)}</b><div class=\"muted\">{E(DashboardHtml.Trim(c.NameA, 50))} · id {c.IdA} · {E(c.SourceA)}</div></td>" +
                          $"<td><b>{E(c.CatB)}</b><div class=\"muted\">{E(DashboardHtml.Trim(c.NameB, 50))} · id {c.IdB} · {E(c.SourceB)}</div></td>" +
                          $"<td class=\"num\">{c.DepthMm:0} mm</td><td>{E(c.Responsible)}<div class=\"muted\">{E(c.Reason)}</div>{(c.Cause != null ? $"<div class='muted'>Cause: {E(c.Cause)}</div>" : "")}</td><td class=\"muted\">{c.X:0}, {c.Y:0}, {c.Z:0}</td></tr>");
            }
            if (clashes.Count > 400) sb.Append($"<tr><td colspan=\"7\" class=\"muted\">... and {clashes.Count - 400} more.</td></tr>");
            sb.Append("</table>");
            if (notes.Count > 0) sb.Append("<h2>Test notes</h2>" + string.Join("", notes.Select(n => $"<div class=\"muted\">{E(n)}</div>")));
            sb.Append($"<div class=\"foot\">Hard clash = the solids overlap by at least the tolerance (depth = smallest overlap dimension); clearance = closer than the clearance distance (box distance, approximate). " +
                      "Responsibility follows ACE priority: the element that is easier to move gives way (structure first, then walls and floors, ducts, pipes, cable trays, conduits); equal priority = coordinate. " +
                      $"Status is kept between runs: new, active (still there), resolved (gone), approved (accepted). {(ms > 0 ? $"Run time {ms / 1000.0:0.0} s." : "")}</div>");
            sb.Append("</div></body></html>");
            return DashboardHtml.Ascii(sb.ToString());
        }
    }
}
