using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Dashboard;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// Bridge "import_clash_report": a Navisworks Clash Detective report (XML or HTML) becomes ACE clashes. Each reported
    /// item is matched to the real element (its model by file name, then its element id), so issues, responsibility,
    /// causes, the Clash Browser and Clash View, approvals, the coordination report and the dashboard all work on it.
    /// Each Navisworks test is stored as its own ACE test ("NW: name"), so re-importing tracks new / active / resolved.
    /// Reads the model only; nothing in the model changes.
    /// </summary>
    internal static class ClashImport
    {
        internal const string Prefix = "NW: ";

        /// <summary>An item that is not in a loaded model gets a stable stand-in key instead of an element's UniqueId.</summary>
        internal static bool IsStandIn(string u) => u != null && u.StartsWith("nw:", StringComparison.Ordinal);

        /// <summary>{ path, test?: only this Navisworks test, show?: open the Clash Browser }</summary>
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var host = Args.RequireDoc(app);
            var path = Args.Str(args, "path") ?? throw new CommandException("Give 'path': the Navisworks clash report (.xml, or .html from the tabular HTML report).");
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(path)) throw new CommandException($"No file at {path}.");
            if (new FileInfo(path).Length > 200L * 1024 * 1024) throw new CommandException("The report is larger than 200 MB. Export one test at a time, or the XML report without images.");
            var reported = ClashReportParser.Parse(File.ReadAllText(path), out var format);
            var onlyTest = Args.Str(args, "test");
            if (onlyTest != null) reported = reported.Where(r => string.Equals(r.Test, onlyTest, StringComparison.OrdinalIgnoreCase) || string.Equals(Prefix + r.Test, onlyTest, StringComparison.OrdinalIgnoreCase)).ToList();
            if (reported.Count == 0)
                throw new CommandException(onlyTest != null ? $"No clashes of the test '{onlyTest}' in this report." :
                    "No clashes found in this report. In Navisworks Clash Detective, Report tab: format XML (best) or HTML (Tabular), with Item ID, Item Name, Item Type and the source file in the contents.");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sources = Clashes.Sources(app, host);
            var resolver = new Resolver(host, sources);
            var now = DateTime.Now;
            var all = new List<Clash>();
            var perTest = new JsonArray();
            var notes = new List<string>();
            var importedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in reported.GroupBy(r => r.Test ?? "Navisworks"))
            {
                var testName = Prefix + group.Key;
                importedTests.Add(testName);
                var found = new List<Clash>();
                var status = new Dictionary<string, string>();
                foreach (var r in group)
                {
                    var c = resolver.ToClash(r, testName, now);
                    if (status.ContainsKey(c.Key)) continue;     // the same pair twice in one test
                    status[c.Key] = r.Status;
                    found.Add(c);
                }
                var stored = Clashes.Load(host, testName);
                DateTime? previousRun = stored.Count > 0 ? stored.Max(c => c.LastSeen) : (DateTime?)null;
                // A report is the whole test: what is no longer in it is resolved.
                var merged = ClashLogic.Merge(stored, found, c => true, now);
                ClashLogic.ApplyReported(merged, status, new HashSet<string>(stored.Select(c => c.Key)));
                var testNotes = new List<string>();
                try { Clashes.Explain(sources, merged.Where(c => c.Status == "new" && c.Cause == null).ToList(), previousRun, testNotes); }
                catch (Exception ex) { Log.Warn($"Clash causes (import): {ex.Message}"); }
                Clashes.Save(host, testName, merged);
                all.AddRange(merged);
                var both = found.Count(c => !IsStandIn(c.UA) && !IsStandIn(c.UB));
                var none = found.Count(c => IsStandIn(c.UA) && IsStandIn(c.UB));
                notes.AddRange(testNotes.Select(n => $"{testName}: {n}"));
                perTest.Add(new JsonObject
                {
                    ["test"] = testName, ["reported"] = group.Count(),
                    ["matched"] = new JsonObject { ["both"] = both, ["one"] = found.Count - both - none, ["none"] = none },
                    ["open"] = merged.Count(ClashLogic.IsOpen), ["new"] = merged.Count(c => c.Status == "new"), ["active"] = merged.Count(c => c.Status == "active"),
                    ["resolved"] = merged.Count(c => c.Status == "resolved"), ["approved"] = merged.Count(c => c.Status == "approved"),
                });
            }
            if (resolver.MissingModels.Count > 0)
                notes.Add($"Not loaded here (link or open them to match their elements): {string.Join(", ", resolver.MissingModels.Take(10))}.");

            // Publish with the other tests of this model, so the browser, dashboard and report show everything.
            var everything = Clashes.Current(host).Where(c => !importedTests.Contains(c.Test ?? "")).Concat(all).ToList();
            var issues = ClashLogic.Issues(everything);
            var report = DashboardHtml.SaveAs(host.Title, "Clashes", now, ClashHtml.Render(host.Title, everything, issues, notes, sw.ElapsedMilliseconds));
            StatusStore.PublishClashes(host.Title, everything, report);
            if (Args.Bool(args, "show")) ClashBrowser.ShowFor(app);
            var mine = new HashSet<Clash>(all);
            return new JsonObject
            {
                ["format"] = format, ["file"] = Path.GetFileName(path),
                ["tests"] = perTest,
                ["topIssues"] = new JsonArray(issues.Where(i => i.Clashes.Any(mine.Contains)).Take(15).Select(i => (JsonNode)new JsonObject
                {
                    ["issue"] = i.Title, ["clashes"] = i.Count, ["responsible"] = i.Responsible, ["levels"] = i.Levels,
                    ["elementId"] = i.ElementId, ["model"] = i.Model, ["issueKey"] = i.Key,
                }).ToArray()),
                ["notes"] = new JsonArray(notes.Take(10).Select(n => (JsonNode)n).ToArray()),
                ["htmlReport"] = report,
                ["note"] = "Imported as ACE tests named 'NW: <test>'. Re-import a newer report to track new, active and resolved; Navisworks Approved and Resolved are kept, and approvals made in ACE survive a re-import. Items not in a loaded model keep their Navisworks name and id but cannot be shown or selected.",
            };
        }

        /// <summary>Matches reported items to elements of the loaded models and builds the ACE clash.</summary>
        private sealed class Resolver
        {
            private readonly Document _host;
            private readonly List<Source> _sources;
            private readonly List<Level> _levels;
            private readonly Transform _sharedToHost;
            private readonly Dictionary<string, Source> _byFile = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> MissingModels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            public Resolver(Document host, List<Source> sources)
            {
                _host = host;
                _sources = sources;
                _levels = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
                try { _sharedToHost = host.ActiveProjectLocation.GetTotalTransform().Inverse; } catch { _sharedToHost = Transform.Identity; }
            }

            private string LevelAt(double zFeet) => _levels.LastOrDefault(l => l.ProjectElevation <= zFeet + 0.01)?.Name ?? _levels.FirstOrDefault()?.Name;

            private Source ModelOf(string file)
            {
                if (_byFile.TryGetValue(file, out var s)) return s;
                s = _sources.FirstOrDefault(x => ClashReportParser.SameModel(file, x.Name) || ClashReportParser.SameModel(file, x.Doc.PathName));
                if (s == null) MissingModels.Add(Path.GetFileName(file));
                return _byFile[file] = s;
            }

            /// <summary>
            /// The element of a reported item: in the model named by its file; without a file, in the one model where that
            /// id is an element whose category or name fits the report (ids repeat between models, so a guess needs proof).
            /// </summary>
            private (Source, Element) Find(ReportItem item)
            {
                if (item.ElementId == null) return (null, null);
                var id = new ElementId(item.ElementId.Value);
                if (item.File != null)
                {
                    var s = ModelOf(item.File);
                    var e = s?.Doc.GetElement(id);
                    return e?.Category != null ? (s, e) : (null, null);
                }
                var fits = _sources.Select(s => (s, e: s.Doc.GetElement(id))).Where(x => x.e?.Category != null && Fits(item, x.e)).ToList();
                return fits.Count == 1 ? fits[0] : (null, null);
            }

            private static bool Fits(ReportItem item, Element e)
            {
                var said = new[] { item.Type, item.Name }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (said.Count == 0) return false;
                var have = $"{e.Category.Name} {Clashes.Name(e)} {e.Name}";
                return said.Any(x => have.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf(e.Category.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            public Clash ToClash(ReportClash r, string testName, DateTime now)
            {
                var (sa, ea) = Find(r.A);
                var (sb, eb) = Find(r.B);
                string Stand(ReportItem i, string side) =>
                    $"nw:{ClashReportParser.ModelKey(i.File)}:{(i.ElementId?.ToString() ?? (r.Name + side))}";
                var ua = ea?.UniqueId ?? Stand(r.A, "A");
                var ub = eb?.UniqueId ?? Stand(r.B, "B");
                var d = r.DistanceMm ?? 0;
                var c = new Clash
                {
                    Key = ClashLogic.PairKey(testName, ua, ub), Test = testName, UA = ua, UB = ub,
                    SourceA = sa?.Name ?? FileName(r.A), SourceB = sb?.Name ?? FileName(r.B),
                    CatA = ea?.Category?.Name ?? r.A.Type ?? "Item", CatB = eb?.Category?.Name ?? r.B.Type ?? "Item",
                    NameA = ea != null ? Clashes.Name(ea) : r.A.Name ?? r.A.Type, NameB = eb != null ? Clashes.Name(eb) : r.B.Name ?? r.B.Type,
                    IdA = ea?.Id.Value ?? r.A.ElementId ?? 0, IdB = eb?.Id.Value ?? r.B.ElementId ?? 0,
                    Kind = d > 0 ? "clearance" : "hard", DepthMm = Math.Round(Math.Abs(d)),
                    FirstSeen = now, LastSeen = now,
                    Note = string.Join(" · ", new[] { $"Navisworks {r.Name}", r.Grid != null ? $"grid {r.Grid}" : null, r.Description }.Where(x => !string.IsNullOrWhiteSpace(x))),
                };
                // Where they meet, in this model's coordinates: between the two elements' boxes; else one element; else the
                // reported point (Navisworks shared coordinates, approximate).
                var boxA = Box(sa, ea); var boxB = Box(sb, eb);
                XYZ p = null;
                if (boxA != null && boxB != null)
                    p = new XYZ(Mid(boxA.Value.Min.X, boxA.Value.Max.X, boxB.Value.Min.X, boxB.Value.Max.X),
                                Mid(boxA.Value.Min.Y, boxA.Value.Max.Y, boxB.Value.Min.Y, boxB.Value.Max.Y),
                                Mid(boxA.Value.Min.Z, boxA.Value.Max.Z, boxB.Value.Min.Z, boxB.Value.Max.Z));
                else if ((boxA ?? boxB) is { } one) p = (one.Min + one.Max) / 2;
                else if (r.Point != null && r.Point.Length == 3)
                {
                    var f = (ClashReportParser.ToMm("1", r.Units) ?? 1000) / Lengths.MmPerFoot;
                    p = _sharedToHost.OfPoint(new XYZ(r.Point[0] * f, r.Point[1] * f, r.Point[2] * f));
                }
                if (p != null) { c.X = Math.Round(Lengths.Mm(p.X)); c.Y = Math.Round(Lengths.Mm(p.Y)); c.Z = Math.Round(Lengths.Mm(p.Z)); }
                var reportedLevel = new[] { r.A.Layer, r.B.Layer }.FirstOrDefault(l => l != null && _levels.Any(x => string.Equals(x.Name, l, StringComparison.OrdinalIgnoreCase)));
                c.Level = p != null ? LevelAt(p.Z) : reportedLevel ?? r.A.Layer ?? r.B.Layer;
                Clashes.Assign(c, _host, sa?.Discipline, sb?.Discipline);
                return c;
            }

            private static string FileName(ReportItem i) => i.File != null ? Path.GetFileName(i.File) : "Navisworks";

            private static (XYZ Min, XYZ Max)? Box(Source s, Element e)
            {
                var bb = e?.get_BoundingBox(null);
                return bb == null ? ((XYZ, XYZ)?)null : ViewTools.Box(bb, s.ToHost);
            }

            /// <summary>The middle of the overlap of two ranges (or of the gap between them).</summary>
            private static double Mid(double minA, double maxA, double minB, double maxB) => (Math.Max(minA, minB) + Math.Min(maxA, maxB)) / 2;
        }
    }
}
