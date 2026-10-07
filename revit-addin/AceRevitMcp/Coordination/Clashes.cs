using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>A model taking part in coordination: the active model, a link, or another open model.</summary>
    internal sealed class Source
    {
        public Document Doc;
        public string Name, Discipline, Relation;
        public Transform ToHost = Transform.Identity;   // this model's coordinates -> the active model's
        public ElementId LinkInstanceId;                  // for links: the instance in the active model
        public bool IsHost => Relation == "this model";
    }

    /// <summary>
    /// Clash detection across the active model, its links and the other open models, with the responsible
    /// discipline for each clash (from ACE's priority rules) and status tracking between runs.
    /// </summary>
    internal static class Clashes
    {
        /// <summary>The current clash results of a model: this session's (StatusStore), else the stored ones (then published).</summary>
        internal static List<Clash> Current(Document host)
        {
            var known = StatusStore.For(host.Title)?.Clashes;
            if (known != null) return known;
            var stored = LoadAll(host);
            if (stored.Count > 0) StatusStore.PublishClashes(host.Title, stored);
            return stored;
        }

        /// <summary>Geometry read once per clash run, shared by every test and both sides (key: document, element id).</summary>
        internal sealed class SolidCache
        {
            private readonly Dictionary<(int, long), List<Solid>> _solids = new Dictionary<(int, long), List<Solid>>();
            public List<Solid> Of(Element e)
            {
                var k = (e.Document.GetHashCode(), e.Id.Value);
                if (!_solids.TryGetValue(k, out var list)) _solids[k] = list = Solids(e);
                return list;
            }
        }

        // ---- sources ------------------------------------------------------------------------------------------

        public static List<Source> Sources(UIApplication app, Document host)
        {
            var list = new List<Source> { new Source { Doc = host, Name = host.Title, Discipline = BriefCommands.Discipline(host), Relation = "this model" } };
            var seen = new HashSet<string> { host.PathName ?? host.Title };
            foreach (var li in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = RevitJson.Safe(() => li.GetLinkDocument());
                if (ld == null || !seen.Add((ld.PathName ?? ld.Title) + "#" + li.Id.Value)) continue;
                list.Add(new Source { Doc = ld, Name = ld.Title, Discipline = BriefCommands.Discipline(ld), Relation = "link", ToHost = li.GetTotalTransform(), LinkInstanceId = li.Id });
            }
            Transform shared(Document d) { try { return d.ActiveProjectLocation.GetTotalTransform(); } catch { return null; } }
            var hs = shared(host);
            foreach (var od in app.Application.Documents.Cast<Document>())
            {
                if (od.IsLinked || od.IsFamilyDocument || od.Equals(host) || list.Any(s => s.Doc.PathName == od.PathName && !string.IsNullOrEmpty(od.PathName))) continue;
                var os = shared(od);
                list.Add(new Source
                {
                    Doc = od, Name = od.Title, Discipline = BriefCommands.Discipline(od), Relation = "open model",
                    ToHost = hs != null && os != null ? hs.Inverse.Multiply(os) : Transform.Identity,
                });
            }
            return list;
        }

        // ---- tests --------------------------------------------------------------------------------------------

        internal static readonly BuiltInCategory[] Mep =
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_Sprinklers,
        };
        internal static readonly BuiltInCategory[] Str =
        {
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation,
        };
        internal static readonly BuiltInCategory[] StrSlabsWalls = { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls };
        internal static readonly BuiltInCategory[] ArcSpatial =
        {
            BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Roofs,
        };

        internal sealed class SetSpec { public string[] Disciplines; public BuiltInCategory[] Categories; }
        internal sealed class TestSpec { public string Name; public SetSpec A, B; public double ToleranceMm = 15, ClearanceMm; }

        public static readonly TestSpec[] Standard =
        {
            new TestSpec { Name = "STR vs MEP", A = new SetSpec { Disciplines = new[] { "MEP" }, Categories = Mep }, B = new SetSpec { Disciplines = new[] { "STR" }, Categories = Str.Concat(StrSlabsWalls).ToArray() }, ToleranceMm = 15 },
            new TestSpec { Name = "ARC vs STR", A = new SetSpec { Disciplines = new[] { "ARC" }, Categories = new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows } }, B = new SetSpec { Disciplines = new[] { "STR" }, Categories = Str }, ToleranceMm = 25 },
            new TestSpec { Name = "MEP vs ARC", A = new SetSpec { Disciplines = new[] { "MEP" }, Categories = Mep }, B = new SetSpec { Disciplines = new[] { "ARC" }, Categories = ArcSpatial }, ToleranceMm = 15 },
            new TestSpec { Name = "MEP vs MEP", A = new SetSpec { Disciplines = new[] { "MEP" }, Categories = new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_CableTray } }, B = new SetSpec { Disciplines = new[] { "MEP" }, Categories = new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_Conduit } }, ToleranceMm = 10 },
        };

        // ---- responsibility -------------------------------------------------------------------------------------

        /// <summary>Priority: the element that is harder to move keeps its place; the other side is responsible.</summary>
        internal static readonly Dictionary<BuiltInCategory, (int Rank, string Discipline)> DefaultRanks = new Dictionary<BuiltInCategory, (int, string)>
        {
            [BuiltInCategory.OST_StructuralFoundation] = (100, "STR"), [BuiltInCategory.OST_StructuralColumns] = (98, "STR"),
            [BuiltInCategory.OST_StructuralFraming] = (95, "STR"), [BuiltInCategory.OST_Floors] = (90, "STR/ARC"),
            [BuiltInCategory.OST_Walls] = (85, "ARC/STR"), [BuiltInCategory.OST_Stairs] = (85, "ARC"), [BuiltInCategory.OST_Roofs] = (85, "ARC"),
            [BuiltInCategory.OST_Doors] = (70, "ARC"), [BuiltInCategory.OST_Windows] = (70, "ARC"), [BuiltInCategory.OST_Ceilings] = (60, "ARC"),
            [BuiltInCategory.OST_MechanicalEquipment] = (58, "MEP (HVAC)"),
            [BuiltInCategory.OST_DuctCurves] = (55, "MEP (HVAC)"), [BuiltInCategory.OST_DuctFitting] = (55, "MEP (HVAC)"), [BuiltInCategory.OST_DuctAccessory] = (54, "MEP (HVAC)"), [BuiltInCategory.OST_FlexDuctCurves] = (30, "MEP (HVAC)"),
            [BuiltInCategory.OST_PipeCurves] = (45, "MEP (plumbing / fire)"), [BuiltInCategory.OST_PipeFitting] = (45, "MEP (plumbing / fire)"), [BuiltInCategory.OST_PipeAccessory] = (44, "MEP (plumbing / fire)"), [BuiltInCategory.OST_FlexPipeCurves] = (30, "MEP (plumbing / fire)"),
            [BuiltInCategory.OST_CableTray] = (40, "MEP (electrical)"), [BuiltInCategory.OST_CableTrayFitting] = (40, "MEP (electrical)"),
            [BuiltInCategory.OST_Conduit] = (35, "MEP (electrical)"), [BuiltInCategory.OST_ConduitFitting] = (35, "MEP (electrical)"),
            [BuiltInCategory.OST_Sprinklers] = (32, "MEP (fire)"),
        };

        private static Dictionary<string, (int Rank, string Discipline)> _rules;

        /// <summary>Ranks by category name: ACE defaults, overridden by %APPDATA%\ACE-RevitMCP\clash-rules.json if present.</summary>
        private static Dictionary<string, (int Rank, string Discipline)> Rules(Document doc)
        {
            if (_rules != null) return _rules;
            var r = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (bic, v) in DefaultRanks)
                try { var c = Category.GetCategory(doc, bic); if (c != null) r[c.Name] = v; } catch { }
            try
            {
                var file = Path.Combine(AceConfig.Directory, "clash-rules.json");
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["ranks"] is JsonObject ranks)
                    foreach (var (name, node) in ranks)
                        if (node?["rank"] is JsonValue rv && rv.TryGetValue<int>(out var rank))
                            r[name] = (rank, node["discipline"]?.ToString() ?? "");
            }
            catch (Exception ex) { Log.Warn($"clash-rules.json: {ex.Message}"); }
            return _rules = r;
        }

        internal static void Assign(Clash c, Document doc, string discA, string discB) => ClashLogic.Assign(c, Rules(doc), discA, discB);

        /// <summary>Pairs that are one piece of work, not a clash: MEP parts connected to each other, an element and its host.</summary>
        private static bool Related(Element a, Element b)
        {
            if (!a.Document.Equals(b.Document)) return false;
            if (a is FamilyInstance fa && fa.Host?.Id == b.Id) return true;
            if (b is FamilyInstance fb && fb.Host?.Id == a.Id) return true;
            var cm = Connectors(a);
            if (cm == null) return false;
            foreach (Connector c in cm.Connectors)
                try
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs) if (r.Owner?.Id == b.Id) return true;
                }
                catch { /* logical connectors can throw */ }
            return false;
        }

        private static ConnectorManager Connectors(Element e)
        {
            try
            {
                if (e is MEPCurve mc) return mc.ConnectorManager;
                if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager;
            }
            catch { }
            return null;
        }

        // ---- geometry -------------------------------------------------------------------------------------------

        private static readonly Options GeoOptions = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

        /// <summary>An element's solids (Fine detail, through nested instances), optionally moved into the host's coordinates.
        /// Used by the clash test and the clash view, so both see exactly the same geometry.</summary>
        internal static List<Solid> Solids(Element e, Transform toHost = null)
        {
            var list = new List<Solid>();
            void Walk(GeometryElement ge)
            {
                if (ge == null) return;
                foreach (var o in ge)
                {
                    if (o is Solid s && s.Volume > 1e-6) list.Add(s);
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                }
            }
            try { Walk(e.get_Geometry(GeoOptions)); } catch { }
            if (toHost == null || toHost.IsIdentity) return list;
            return list.Select(s => { try { return SolidUtils.CreateTransformed(s, toHost); } catch { return null; } }).Where(s => s != null).ToList();
        }

        private static (XYZ Min, XYZ Max) Box(Solid s, Transform t) => ViewTools.Box(s, t);

        private static List<Element> Collect(Source s, BuiltInCategory[] cats)
        {
            var ids = new List<ElementId>();
            foreach (var c in cats) ids.Add(new ElementId(c));
            return new FilteredElementCollector(s.Doc).WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(ids)).Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null)).ToList();
        }

        private static List<Source> Pick(List<Source> all, SetSpec spec)
        {
            var match = all.Where(s => spec.Disciplines.Contains(s.Discipline)).ToList();
            return match.Count > 0 ? match : all;   // disciplines unknown: search every model
        }

        // ---- run -----------------------------------------------------------------------------------------------

        /// <summary>Clash search grid cell: about 3 m, close to a typical duct run or beam span between joints.</summary>
        private const double GridCellFeet = 10;

        public static List<Clash> Run(Document host, List<Source> sources, SolidCache geometry, TestSpec test, string levelFilter, int maxElements, List<string> notes, out bool complete, string withModel = null, string primaryModel = null)
        {
            complete = true;
            var aSources = Pick(sources, test.A); var bSources = Pick(sources, test.B);
            // Compare two chosen models only (Clash Browser): the primary (this model, or a link for a BIM manager) and the secondary.
            var primary = string.IsNullOrEmpty(primaryModel) ? host.Title : primaryModel;
            var comparingSelf = withModel != null && string.Equals(withModel, primary, StringComparison.OrdinalIgnoreCase);
            if (withModel != null)
            {
                bool Allowed(Source s) => string.Equals(s.Name, primary, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, withModel, StringComparison.OrdinalIgnoreCase);
                aSources = aSources.Where(Allowed).ToList(); bSources = bSources.Where(Allowed).ToList();
                if (aSources.Count == 0 || bSources.Count == 0) { notes.Add($"Nothing to compare with {withModel} in this test."); return new List<Clash>(); }
            }
            var aItems = aSources.SelectMany(s => Collect(s, test.A.Categories).Select(e => (s, e))).ToList();
            notes.Add($"A: {aItems.Count} elements in {string.Join(", ", aSources.Select(s => $"{s.Name} ({s.Discipline})"))}");
            notes.Add($"B: {string.Join(", ", test.B.Categories.Length)} categories in {string.Join(", ", bSources.Select(s => $"{s.Name} ({s.Discipline})"))}");
            if (aItems.Count > maxElements) { notes.Add($"Only the first {maxElements} A elements were tested (max_elements)."); aItems = aItems.Take(maxElements).ToList(); complete = false; }

            var hostLevels = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            string LevelAt(double zFeet) => hostLevels.LastOrDefault(l => l.ProjectElevation <= zFeet + 0.01)?.Name ?? hostLevels.FirstOrDefault()?.Name;

            var tol = UnitUtils.ConvertToInternalUnits(test.ToleranceMm, UnitTypeId.Millimeters);
            var clear = UnitUtils.ConvertToInternalUnits(test.ClearanceMm, UnitTypeId.Millimeters);
            var clashes = new Dictionary<string, Clash>();

            // The B elements of each model, collected and indexed ONCE for this test (boxes in host coordinates), instead
            // of a new collector for every A solid. Element boxes are slightly larger than the solids: a superset.
            var indexes = new Dictionary<Source, (BoxGrid Grid, List<Element> Items)>();
            (BoxGrid Grid, List<Element> Items) IndexOf(Source sb)
            {
                if (indexes.TryGetValue(sb, out var ix)) return ix;
                var grid = new BoxGrid(GridCellFeet);
                var items = new List<Element>();
                foreach (var e in Collect(sb, test.B.Categories))
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    var (bmin, bmax) = ViewTools.Box(bb, sb.ToHost);
                    grid.Add(bmin.X, bmin.Y, bmin.Z, bmax.X, bmax.Y, bmax.Z);
                    items.Add(e);
                }
                return indexes[sb] = (grid, items);
            }
            var solidBoxes = new Dictionary<Solid, (XYZ Min, XYZ Max)>();
            (XYZ Min, XYZ Max) BoxOf(Solid s) => solidBoxes.TryGetValue(s, out var b) ? b : solidBoxes[s] = Box(s, Transform.Identity);
            static bool Overlap((XYZ Min, XYZ Max) a, (XYZ Min, XYZ Max) b, double grow) =>
                a.Min.X - grow <= b.Max.X && a.Max.X + grow >= b.Min.X && a.Min.Y - grow <= b.Max.Y && a.Max.Y + grow >= b.Min.Y && a.Min.Z - grow <= b.Max.Z && a.Max.Z + grow >= b.Min.Z;
            var touch = Math.Max(clear, Lengths.Ft(1));

            foreach (var (sa, ea) in aItems)
            {
                if (Bridge.RequestDispatcher.RunningToken.IsCancellationRequested)
                {
                    notes.Add("Cancelled by the user: the results are partial and nothing was marked resolved.");
                    complete = false;
                    break;
                }
                var solidsA = geometry.Of(ea);
                if (solidsA.Count == 0) continue;
                var hostBoxes = solidsA.Select(s => Box(s, sa.ToHost)).ToList();
                foreach (var sb in bSources)
                {
                    if (withModel != null && !comparingSelf && sa.Doc.Equals(sb.Doc)) continue;   // only across the two models
                    if (withModel == null && primaryModel != null && !string.Equals(sa.Name, primary, StringComparison.OrdinalIgnoreCase) && !string.Equals(sb.Name, primary, StringComparison.OrdinalIgnoreCase)) continue;   // a link as primary: only its clashes
                    var (grid, items) = IndexOf(sb);
                    if (grid.Count == 0) continue;
                    // A's geometry in B's coordinates: A -> host -> B (only for solids that have candidates).
                    var toB = sb.ToHost.Inverse.Multiply(sa.ToHost);
                    for (var k = 0; k < solidsA.Count; k++)
                    {
                        var (hmin, hmax) = hostBoxes[k];
                        var hits = grid.Query(hmin.X - clear, hmin.Y - clear, hmin.Z - clear, hmax.X + clear, hmax.Y + clear, hmax.Z + clear);
                        if (hits.Count == 0) continue;
                        Solid moved;
                        try { moved = toB.IsIdentity ? solidsA[k] : SolidUtils.CreateTransformed(solidsA[k], toB); } catch { continue; }
                        var (min, max) = Box(moved, Transform.Identity);
                        var candidates = hits.Select(h => items[h]);
                        foreach (var eb in candidates)
                        {
                            if (sb.Doc.Equals(sa.Doc) && eb.Id == ea.Id) continue;
                            if (eb is FamilyInstance fb && fb.SuperComponent != null) continue;
                            var key = ClashLogic.PairKey(test.Name, ea.UniqueId, eb.UniqueId);
                            if (clashes.ContainsKey(key)) continue;
                            if (Related(ea, eb)) continue;
                            double depth = 0; XYZ centre = null; var kind = (string)null;
                            foreach (var solidB in geometry.Of(eb))
                            {
                                if (!Overlap((min, max), BoxOf(solidB), touch)) continue;   // cannot intersect: skip the boolean
                                try
                                {
                                    var inter = BooleanOperationsUtils.ExecuteBooleanOperation(moved, solidB, BooleanOperationsType.Intersect);
                                    if (inter == null || inter.Volume < 1e-6) continue;
                                    var (imin, imax) = Box(inter, Transform.Identity);
                                    var d = Math.Min(imax.X - imin.X, Math.Min(imax.Y - imin.Y, imax.Z - imin.Z));
                                    if (d > depth) { depth = d; centre = (imin + imax) / 2; }
                                }
                                catch { /* degenerate geometry: skip this pair of solids */ }
                            }
                            if (depth >= tol) kind = "hard";
                            else if (clear > 0 && depth <= 0)
                            {
                                var bbB = eb.get_BoundingBox(null);
                                if (bbB == null) continue;
                                var gap = Math.Max(0, Math.Max(Math.Max(bbB.Min.X - max.X, min.X - bbB.Max.X), Math.Max(Math.Max(bbB.Min.Y - max.Y, min.Y - bbB.Max.Y), Math.Max(bbB.Min.Z - max.Z, min.Z - bbB.Max.Z))));
                                if (gap < clear) { kind = "clearance"; depth = gap; centre = (min + max) / 2; }
                            }
                            if (kind == null) continue;
                            var hostPoint = sb.ToHost.OfPoint(centre);
                            var level = LevelAt(hostPoint.Z);
                            if (levelFilter != null && !string.Equals(level, levelFilter, StringComparison.OrdinalIgnoreCase)) continue;
                            var c = new Clash
                            {
                                Key = key, Test = test.Name, Kind = kind,
                                SourceA = sa.Name, SourceB = sb.Name, CatA = ea.Category?.Name, CatB = eb.Category?.Name,
                                NameA = Name(ea), NameB = Name(eb), IdA = ea.Id.Value, IdB = eb.Id.Value, UA = ea.UniqueId, UB = eb.UniqueId,
                                X = Math.Round(Lengths.Mm(hostPoint.X)), Y = Math.Round(Lengths.Mm(hostPoint.Y)), Z = Math.Round(Lengths.Mm(hostPoint.Z)),
                                DepthMm = Math.Round(Lengths.Mm(depth)), Level = level, FirstSeen = DateTime.Now, LastSeen = DateTime.Now,
                            };
                            Assign(c, host, sa.Discipline, sb.Discipline);
                            clashes[key] = c;
                        }
                    }
                }
            }
            return clashes.Values.ToList();
        }

        internal static string Name(Element e) => e is FamilyInstance fi ? $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}" : (e.Document.GetElement(e.GetTypeId())?.Name ?? e.Name);

        // ---- cause: what changed since the snapshot before the previous run -----------------------------------

        private static readonly Dictionary<string, Dictionary<string, Tracking.ElementRecord>> SnapshotCache = new Dictionary<string, Dictionary<string, Tracking.ElementRecord>>();

        /// <summary>
        /// For new clashes: which side was added, moved or retyped since the latest snapshot taken at or before the
        /// previous clash run (or the latest one if there was no run), and in workshared models, by whom.
        /// </summary>
        public static void Explain(List<Source> allSources, List<Clash> fresh, DateTime? previousRun, List<string> notes)
        {
            if (fresh.Count == 0) return;
            var sources = allSources.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
            var baselines = new Dictionary<string, (Dictionary<string, Tracking.ElementRecord> Records, DateTime Time)?>();
            (Dictionary<string, Tracking.ElementRecord> Records, DateTime Time)? Baseline(Source s)
            {
                if (baselines.TryGetValue(s.Name, out var b)) return b;
                b = null;
                try
                {
                    var all = Tracking.Snapshots.List(s.Doc);
                    var pick = previousRun != null ? all.FirstOrDefault(x => x.Time <= previousRun.Value) : all.FirstOrDefault();
                    if (pick.File != null)
                    {
                        var cacheKey = pick.File;
                        if (!SnapshotCache.TryGetValue(cacheKey, out var records))
                        {
                            if (SnapshotCache.Count > 8) SnapshotCache.Clear();
                            records = Tracking.Snapshots.Load(pick.File).Elements.GroupBy(e => e.U).ToDictionary(g => g.Key, g => g.First());
                            SnapshotCache[cacheKey] = records;
                        }
                        b = (records, pick.Time);
                    }
                }
                catch (Exception ex) { Log.Warn($"Clash cause, snapshot of {s.Name}: {ex.Message}"); }
                return baselines[s.Name] = b;
            }

            ClashLogic.Side Side(string model, string u, string cat)
            {
                if (model == null || !sources.TryGetValue(model, out var src)) return null;
                var bl = Baseline(src);
                if (bl == null) return null;
                var e = src.Doc.GetElement(u);
                if (e == null) return null;
                var side = new ClashLogic.Side { Cat = cat, Model = src.IsHost ? null : model };
                if (!bl.Value.Records.TryGetValue(u, out var rec)) side.Change = "added";
                else if (rec.Loc != null && Tracking.Snapshots.Location(e) is string loc && loc != rec.Loc) side.Change = "moved";
                else if (rec.Type != null && Tracking.Snapshots.TypeName(src.Doc, e) != rec.Type) side.Change = "retyped";
                if (side.Change != null && src.Doc.IsWorkshared)
                    try
                    {
                        var info = WorksharingUtils.GetWorksharingTooltipInfo(src.Doc, e.Id);
                        var by = side.Change == "added" ? info.Creator : info.LastChangedBy;
                        if (!string.IsNullOrWhiteSpace(by)) side.By = by;
                    }
                    catch { }
                return side;
            }

            var explained = 0;
            foreach (var c in fresh.Take(500))
            {
                var a = Side(c.SourceA, c.UA, c.CatA);
                var b = Side(c.SourceB, c.UB, c.CatB);
                var time = new[] { c.SourceA, c.SourceB }.Where(m => m != null && sources.ContainsKey(m)).Select(m => Baseline(sources[m])).FirstOrDefault(x => x != null);
                var since = time != null ? $"the snapshot of {time.Value.Time:d MMM HH:mm}" : "the last snapshot";
                (c.Cause, c.CausedBy) = ClashLogic.Cause(a, b, since);
                if (c.Cause != null) explained++;
            }
            if (explained == 0) notes.Add("No snapshots to explain new clashes yet: the change tracker's snapshots (taken on open and save) let the next run say which change caused each new clash.");
        }

        // ---- status between runs -------------------------------------------------------------------------------

        private static string StoreFile(Document host, string test)
        {
            var dir = Path.Combine(AceConfig.Directory, "clashes", Tracking.Snapshots.ModelKey(host));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, string.Concat(test.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')) + ".json");
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = false, IncludeFields = true };

        /// <summary>Every stored result for this model (all standard tests and any custom ones).</summary>
        public static List<Clash> LoadAll(Document host)
        {
            var dir = Path.Combine(AceConfig.Directory, "clashes", Tracking.Snapshots.ModelKey(host));
            var all = new List<Clash>();
            if (!Directory.Exists(dir)) return all;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                try { all.AddRange(JsonSerializer.Deserialize<List<Clash>>(File.ReadAllText(f), Json) ?? new List<Clash>()); } catch { }
            return all;
        }

        public static List<Clash> Load(Document host, string test)
        {
            var f = StoreFile(host, test);
            try { return File.Exists(f) ? JsonSerializer.Deserialize<List<Clash>>(File.ReadAllText(f), Json) : new List<Clash>(); }
            catch { return new List<Clash>(); }
        }

        public static void Save(Document host, string test, List<Clash> clashes) => File.WriteAllText(StoreFile(host, test), JsonSerializer.Serialize(clashes, Json));
    }
}
