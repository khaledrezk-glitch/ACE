using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using AceRevitMcp.Dashboard;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Tracking
{
    internal sealed class Change
    {
        public string Kind;      // added | deleted | moved | retyped | changed
        public string Cat, Type, Detail, By;
        public long Id;
        public string U;
    }

    internal sealed class ModelDiff
    {
        public string Model, Discipline, Relation;
        public DateTime? Since;
        public DateTime Now;
        public int Elements;
        public List<Change> Changes = new List<Change>();
        public int Count(string kind) => Changes.Count(c => c.Kind == kind);
    }

    /// <summary>
    /// Change tracker: compares the model (and its loaded links) with an earlier snapshot. Snapshots are taken
    /// automatically (on open, once a day; after saves and syncs, at most every two hours) and on request.
    /// </summary>
    internal static class ChangeTracking
    {
        // ---- automatic snapshots --------------------------------------------------------------------

        public static void Attach(Autodesk.Revit.ApplicationServices.ControlledApplication app)
        {
            app.DocumentOpened += (s, e) => Auto(e.Document, TimeSpan.FromHours(20), "opened");
            app.DocumentSaved += (s, e) => Auto(e.Document, TimeSpan.FromHours(2), "saved");
            app.DocumentSynchronizedWithCentral += (s, e) => Auto(e.Document, TimeSpan.FromHours(2), "synced");
        }

        private static void Auto(Document doc, TimeSpan minGap, string reason)
        {
            try
            {
                if (App.Config?.AutoSnapshots == false || doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                var last = Snapshots.List(doc).FirstOrDefault();
                if (last.File != null && DateTime.Now - last.Time < minGap) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var snap = Snapshots.Capture(doc, doc.Application.Username, $"auto: {reason}");
                Snapshots.Save(doc, snap);
                Log.Info($"Change tracker: snapshot of '{doc.Title}' ({snap.Elements.Count} elements, {reason}) in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) { Log.Warn($"Change tracker snapshot failed: {ex.Message}"); }
        }

        // ---- documents to track: the model and its loaded links ---------------------------------------

        private static List<(Document Doc, string Relation)> Tracked(Document host, bool includeLinks)
        {
            var list = new List<(Document, string)> { (host, "this model") };
            if (!includeLinks) return list;
            var seen = new HashSet<string>();
            foreach (var li in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = RevitJson.Safe(() => li.GetLinkDocument());
                if (ld == null || !seen.Add(ld.PathName ?? ld.Title)) continue;
                list.Add((ld, "link"));
            }
            return list;
        }

        // ---- commands ------------------------------------------------------------------------------------

        public static JsonNode SnapshotCommand(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var label = Args.Str(args, "label");
            var arr = new JsonArray();
            foreach (var (d, rel) in Tracked(doc, Args.Bool(args, "include_links", true)))
            {
                var snap = Snapshots.Capture(d, app.Application.Username, label ?? "manual");
                var file = Snapshots.Save(d, snap);
                arr.Add(new JsonObject { ["model"] = d.Title, ["relation"] = rel, ["elements"] = snap.Elements.Count, ["file"] = System.IO.Path.GetFileName(file) });
            }
            return new JsonObject { ["snapshots"] = arr, ["note"] = "Compare later with model_changes (since: \"last\", \"today\", \"week\" or a date)." };
        }

        public static JsonNode ListCommand(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var arr = new JsonArray();
            foreach (var (d, rel) in Tracked(doc, true))
                arr.Add(new JsonObject
                {
                    ["model"] = d.Title, ["relation"] = rel,
                    ["snapshots"] = new JsonArray(Snapshots.List(d).Take(30).Select(s => (JsonNode)$"{System.IO.Path.GetFileName(s.File).Replace(".json.gz", "")} ({s.Time:ddd d MMM HH:mm})").ToArray()),
                });
            return arr;
        }

        public static JsonNode ChangesCommand(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var since = Args.Str(args, "since") ?? "last";
            var save = Args.Bool(args, "save_snapshot", true);
            var diffs = new List<ModelDiff>();
            foreach (var (d, rel) in Tracked(doc, Args.Bool(args, "include_links", true)))
            {
                var baseline = Snapshots.Baseline(d, since);
                var current = Snapshots.Capture(d, app.Application.Username, "compare");
                var diff = new ModelDiff { Model = d.Title, Discipline = current.Discipline, Relation = rel, Now = current.Time, Elements = current.Elements.Count };
                if (baseline != null)
                {
                    var old = Snapshots.Load(baseline.Value.File);
                    diff.Since = old.Time;
                    diff.Changes = Compare(old, current);
                    WhoChanged(d, diff.Changes);
                }
                if (save) Snapshots.Save(d, current);
                diffs.Add(diff);
            }
            var report = DashboardHtml.SaveAs(doc.Title, "Changes", DateTime.Now, ChangeHtml.Render(doc.Title, since, diffs));
            StatusStore.PublishChanges(doc.Title, diffs, report);
            var model = doc.Title;
            if (Args.Bool(args, "show")) ReportWindow.ShowOrRefresh(app.MainWindowHandle, "Change tracker", "model_changes", () => State(model));

            var models = new JsonArray();
            foreach (var d in diffs)
            {
                models.Add(new JsonObject
                {
                    ["model"] = d.Model, ["relation"] = d.Relation, ["discipline"] = d.Discipline,
                    ["since"] = d.Since?.ToString("s"), ["elements"] = d.Elements,
                    ["added"] = d.Count("added"), ["deleted"] = d.Count("deleted"), ["moved"] = d.Count("moved"),
                    ["retyped"] = d.Count("retyped"), ["changed"] = d.Count("changed"),
                    ["byCategory"] = ByCategory(d),
                    ["byUser"] = d.Changes.Any(c => c.By != null) ? string.Join(", ", d.Changes.Where(c => c.By != null).GroupBy(c => c.By).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")) : null,
                    ["examples"] = new JsonArray(d.Changes.Take(40).Select(c => (JsonNode)$"{c.Kind}: {c.Cat} {c.Type} (id {c.Id}){(c.Detail != null ? " - " + c.Detail : "")}{(c.By != null ? " [" + c.By + "]" : "")}").ToArray()),
                    ["note"] = d.Since == null ? "No earlier snapshot of this model yet: this one is the baseline; next time you will see the changes." : null,
                });
            }
            return new JsonObject { ["since"] = since, ["models"] = models, ["htmlReport"] = report,
                ["note"] = "Element ids of linked models are ids inside the link (not selectable in this model)." };
        }

        private static JsonObject ByCategory(ModelDiff d)
        {
            var o = new JsonObject();
            foreach (var g in d.Changes.GroupBy(c => c.Cat).OrderByDescending(g => g.Count()).Take(20))
                o[g.Key] = string.Join(", ", g.GroupBy(c => c.Kind).Select(k => $"{k.Count()} {k.Key}"));
            return o;
        }

        internal static ReportWindow.State State(string model)
        {
            var status = StatusStore.For(model);
            if (status?.Changes == null) return null;
            var host = status.Changes.FirstOrDefault(d => d.Relation == "this model");
            var st = new ReportWindow.State
            {
                Path = status.ChangesReport,
                Status = host?.Since == null ? "First snapshot saved: changes appear from the next check." : $"Changes since {host.Since:ddd d MMM HH:mm}. Saved to Documents\\ACE Insights.",
            };
            if (host != null)
                foreach (var kind in new[] { "added", "moved", "retyped", "changed" })
                {
                    var ids = host.Changes.Where(c => c.Kind == kind).Select(c => c.Id).Distinct().ToArray();
                    if (ids.Length > 0) st.Findings.Add(($"{char.ToUpper(kind[0]) + kind.Substring(1)} in this model ({ids.Length})", ids));
                    foreach (var g in host.Changes.Where(c => c.Kind == kind).GroupBy(c => c.Cat).OrderByDescending(g => g.Count()).Take(6))
                        st.Findings.Add(($"   {kind}: {g.Key} ({g.Count()})", g.Select(c => c.Id).Distinct().ToArray()));
                }
            return st;
        }

        // ---- comparison ------------------------------------------------------------------------------------

        internal static List<Change> Compare(Snapshot old, Snapshot now)
        {
            var before = old.Elements.GroupBy(e => e.U).ToDictionary(g => g.Key, g => g.First());
            var after = now.Elements.GroupBy(e => e.U).ToDictionary(g => g.Key, g => g.First());
            var changes = new List<Change>();
            foreach (var (u, n) in after)
            {
                if (!before.TryGetValue(u, out var o))
                {
                    changes.Add(new Change { Kind = "added", Cat = n.Cat, Type = n.Type, Id = n.Id, U = u, Detail = n.Lvl != null ? $"on {n.Lvl}" : null });
                    continue;
                }
                if (o.Type != n.Type)
                    changes.Add(new Change { Kind = "retyped", Cat = n.Cat, Type = n.Type, Id = n.Id, U = u, Detail = $"{o.Type} -> {n.Type}" });
                if (o.Loc != n.Loc && o.Loc != null && n.Loc != null)
                    changes.Add(new Change { Kind = "moved", Cat = n.Cat, Type = n.Type, Id = n.Id, U = u, Detail = Moved(o.Loc, n.Loc) + (o.Lvl != n.Lvl ? $"; level {o.Lvl} -> {n.Lvl}" : "") });
                else if (o.Lvl != n.Lvl)
                    changes.Add(new Change { Kind = "moved", Cat = n.Cat, Type = n.Type, Id = n.Id, U = u, Detail = $"level {o.Lvl} -> {n.Lvl}" });
                if (o.P != n.P && o.Type == n.Type)
                    changes.Add(new Change { Kind = "changed", Cat = n.Cat, Type = n.Type, Id = n.Id, U = u, Detail = Values(o.K, n.K) });
            }
            foreach (var (u, o) in before)
                if (!after.ContainsKey(u))
                    changes.Add(new Change { Kind = "deleted", Cat = o.Cat, Type = o.Type, Id = o.Id, U = u, Detail = o.K != null && o.K.TryGetValue("Mark", out var m) ? $"Mark {m}" : null });
            return changes.OrderBy(c => Order(c.Kind)).ThenBy(c => c.Cat).ToList();
        }

        private static int Order(string kind) => kind switch { "deleted" => 0, "added" => 1, "moved" => 2, "retyped" => 3, _ => 4 };

        private static string Moved(string a, string b)
        {
            double[] P(string s)
            {
                var head = s.StartsWith("box ") ? s.Substring(4).Split(' ')[0] : s.Split('@', ';')[0];
                var parts = head.Split(',');
                return parts.Length >= 2 ? parts.Select(x => double.TryParse(x.Replace("z=", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray() : null;
            }
            var pa = P(a); var pb = P(b);
            if (pa == null || pb == null || pa.Length != pb.Length) return "geometry changed";
            var dist = Math.Sqrt(pa.Zip(pb, (x, y) => (x - y) * (x - y)).Sum());
            var rotated = a.Contains('@') && b.Contains('@') && a.Split('@')[1] != b.Split('@')[1];
            if (dist < 1 && rotated) return $"rotated {a.Split('@')[1]} -> {b.Split('@')[1]} deg";
            if (dist < 1) return a.StartsWith("box ") ? "resized" : "reshaped";
            return $"moved {Math.Round(dist)} mm" + (rotated ? ", rotated" : "");
        }

        private static string Values(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            a ??= new Dictionary<string, string>(); b ??= new Dictionary<string, string>();
            var diffs = a.Keys.Union(b.Keys)
                .Where(k => (a.TryGetValue(k, out var x) ? x : null) != (b.TryGetValue(k, out var y) ? y : null))
                .Select(k => $"{k}: '{(a.TryGetValue(k, out var x) ? x : "")}' -> '{(b.TryGetValue(k, out var y) ? y : "")}'").ToList();
            return diffs.Count > 0 ? string.Join("; ", diffs) + " (and possibly other values)" : "parameter values changed";
        }

        /// <summary>In workshared models, who created (added) or last changed each element (first 300).</summary>
        private static void WhoChanged(Document doc, List<Change> changes)
        {
            if (!doc.IsWorkshared) return;
            foreach (var c in changes.Where(c => c.Kind != "deleted").Take(300))
            {
                try
                {
                    var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, new ElementId(c.Id));
                    c.By = c.Kind == "added" ? info.Creator : info.LastChangedBy;
                    if (string.IsNullOrWhiteSpace(c.By)) c.By = null;
                }
                catch { }
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ChangeTrackerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (commandData.Application.ActiveUIDocument?.Document == null) { TaskDialog.Show("ACE Change tracker", "Open a model first."); return Result.Cancelled; }
                ChangeTracking.ChangesCommand(commandData.Application, new JsonObject { ["show"] = true });
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Change tracker: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }

    /// <summary>The change report (same ACE layout as the dashboard).</summary>
    internal static class ChangeHtml
    {
        private static string E(string s) => DashboardHtml.E(s);

        public static string Render(string model, string since, List<ModelDiff> diffs)
        {
            var host = diffs.FirstOrDefault();
            var sb = DashboardHtml.Begin($"{model} | Changes", "Change tracker",
                $"{E(model)} &nbsp;·&nbsp; {(host?.Since != null ? $"since {host.Since:d MMMM yyyy, HH:mm}" : "first snapshot")} &nbsp;·&nbsp; {DateTime.Now:d MMMM yyyy, HH:mm}",
                "Compares the model and its links with an earlier snapshot");

            sb.Append("<h2>Summary</h2><table class=\"list\"><tr><th>Model</th><th>Discipline</th><th class=\"num\">Added</th><th class=\"num\">Deleted</th><th class=\"num\">Moved</th><th class=\"num\">Retyped</th><th class=\"num\">Changed</th><th>Compared with</th></tr>");
            foreach (var d in diffs)
                sb.Append($"<tr><td><b>{E(d.Model)}</b><div class=\"muted\">{E(d.Relation)}, {d.Elements:N0} elements</div></td><td>{E(d.Discipline)}</td>" +
                          $"{Num(d.Count("added"))}{Num(d.Count("deleted"), key: true)}{Num(d.Count("moved"))}{Num(d.Count("retyped"))}{Num(d.Count("changed"))}" +
                          $"<td class=\"muted\">{(d.Since != null ? $"{d.Since:ddd d MMM HH:mm}" : "no earlier snapshot (baseline saved now)")}</td></tr>");
            sb.Append("</table>");

            foreach (var d in diffs.Where(d => d.Changes.Count > 0))
            {
                sb.Append($"<h2>{E(d.Model)} <span class=\"tag plan\">{E(d.Discipline)}</span></h2>");
                sb.Append("<table class=\"row\"><tr><td class=\"cell\" style=\"width:55%\"><div class=\"card\"><h3>By category</h3><table class=\"list\"><tr><th>Category</th><th class=\"num\">Added</th><th class=\"num\">Deleted</th><th class=\"num\">Moved</th><th class=\"num\">Retyped</th><th class=\"num\">Changed</th></tr>");
                foreach (var g in d.Changes.GroupBy(c => c.Cat).OrderByDescending(g => g.Count()).Take(20))
                    sb.Append($"<tr><td>{E(g.Key)}</td>{Num(g.Count(c => c.Kind == "added"))}{Num(g.Count(c => c.Kind == "deleted"), key: true)}{Num(g.Count(c => c.Kind == "moved"))}{Num(g.Count(c => c.Kind == "retyped"))}{Num(g.Count(c => c.Kind == "changed"))}</tr>");
                sb.Append("</table></div></td><td class=\"cell\" style=\"width:45%\"><div class=\"card\"><h3>By person</h3>");
                var people = d.Changes.Where(c => c.By != null).GroupBy(c => c.By).OrderByDescending(g => g.Count()).ToList();
                if (people.Count == 0) sb.Append("<div class=\"muted\">Not a workshared model, so Revit does not record who changed what.</div>");
                else
                {
                    sb.Append("<table class=\"list\"><tr><th>Person</th><th class=\"num\">Elements</th></tr>");
                    foreach (var g in people) sb.Append($"<tr><td>{E(g.Key)}</td><td class=\"num\">{g.Count()}</td></tr>");
                    sb.Append("</table>");
                }
                sb.Append("</div></td></tr></table>");

                sb.Append("<table class=\"list\" style=\"margin-top:14px\"><tr><th style=\"width:10%\">Change</th><th style=\"width:18%\">Category</th><th style=\"width:26%\">Family / type</th><th>Detail</th><th class=\"num\" style=\"width:9%\">Id</th><th style=\"width:12%\">By</th></tr>");
                foreach (var c in d.Changes.Take(250))
                    sb.Append($"<tr><td><span class=\"tag {(c.Kind == "deleted" ? "fail" : c.Kind == "added" ? "live" : "warn")}\">{E(c.Kind)}</span></td><td>{E(c.Cat)}</td><td>{E(DashboardHtml.Trim(c.Type, 60))}</td><td class=\"muted\">{E(DashboardHtml.Trim(c.Detail, 140))}</td><td class=\"num\">{c.Id}</td><td class=\"muted\">{E(c.By)}</td></tr>");
                if (d.Changes.Count > 250) sb.Append($"<tr><td colspan=\"6\" class=\"muted\">... and {d.Changes.Count - 250} more.</td></tr>");
                sb.Append("</table>");
            }
            if (diffs.All(d => d.Changes.Count == 0))
                sb.Append($"<p class=\"muted\" style=\"margin-top:18px\">{(diffs.Any(d => d.Since != null) ? "No changes since the last snapshot." : "This is the first snapshot of these models. From now on, snapshots are taken automatically when a model is opened and after saves or syncs; check again later to see what changed.")}</p>");
            sb.Append("<div class=\"foot\">Moved = position, rotation or level changed; retyped = family or type changed; changed = parameter values changed. " +
                      "Snapshots are kept in %APPDATA%\\ACE-RevitMCP\\snapshots (the last 40 per model). Element ids of linked models are ids inside the link.</div>");
            sb.Append("</div></body></html>");
            return DashboardHtml.Ascii(sb.ToString());
        }

        private static string Num(int n, bool key = false) =>
            $"<td class=\"num\">{(n == 0 ? "<span class=\"muted\">0</span>" : key ? $"<b style=\"color:{DashboardHtml.Hex(Branding.Accent)}\">{n}</b>" : $"<b>{n}</b>")}</td>";
    }
}
