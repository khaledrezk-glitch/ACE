using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The model brief: what Claude should know about the building before acting. The active model, its
    /// Revit links and the other open models (each with a discipline guess), how they line up (levels, grids,
    /// coordinates), room types and which are furnished, and the families available for each purpose with
    /// their real footprint, origin and facing.
    /// </summary>
    internal static class BriefCommands
    {
        private static double Mm(double feet) => Math.Round(Lengths.Mm(feet));

        public static JsonNode ModelBrief(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var includeFamilies = Args.Bool(args, "include_families", true);
            var brief = new JsonObject
            {
                ["model"] = Describe(doc),
                ["levels"] = Levels(doc),
                ["rooms"] = Rooms(doc),
                ["disciplineContent"] = DisciplineContent(doc),
                ["naming"] = Naming(doc),
            };
            if (includeFamilies) brief["families"] = Families(doc, Args.Int(args, "types_per_category", 12));

            // Other models: Revit links (placed in this model) and other documents open in this session.
            var hostShared = SafeShared(doc);
            var others = new JsonArray();
            var linkedDocs = new HashSet<string>();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = RevitJson.Safe(() => li.GetLinkDocument());
                var type = doc.GetElement(li.GetTypeId()) as RevitLinkType;
                var o = new JsonObject
                {
                    ["relation"] = "link", ["name"] = type?.Name ?? li.Name, ["instanceId"] = li.Id.Value,
                    ["loaded"] = ld != null,
                };
                if (ld != null)
                {
                    linkedDocs.Add(ld.PathName ?? ld.Title);
                    Fill(o, ld, doc, li.GetTotalTransform());
                }
                others.Add(o);
            }
            foreach (var od in app.Application.Documents.Cast<Document>())
            {
                if (od.Equals(doc) || od.IsLinked || od.IsFamilyDocument || linkedDocs.Contains(od.PathName ?? od.Title)) continue;
                var o = new JsonObject { ["relation"] = "open in this session (not linked)", ["name"] = od.Title, ["loaded"] = true };
                // Not linked: relate the two models through their shared coordinates.
                var rel = hostShared != null && SafeShared(od) is Transform os ? hostShared.Inverse.Multiply(os) : Transform.Identity;
                Fill(o, od, doc, rel);
                others.Add(o);
            }
            brief["otherModels"] = others;
            brief["howToUse"] = "Read this before planning. Room names/levels tell you where things are; families list what can be placed " +
                                "(footprint in mm, origin offset from the footprint centre, facing = the family's local +Y unless noted). " +
                                "otherModels shows the discipline models and whether their levels/grids line up with this one.";
            return brief;
        }

        private static Transform SafeShared(Document d)
        {
            try { return d.ActiveProjectLocation?.GetTotalTransform(); } catch { return null; }
        }

        // ---- the active model ------------------------------------------------------------------

        private static JsonObject Describe(Document doc)
        {
            var info = doc.ProjectInformation;
            return new JsonObject
            {
                ["title"] = doc.Title, ["path"] = doc.PathName, ["discipline"] = Discipline(doc),
                ["workshared"] = doc.IsWorkshared,
                ["project"] = info == null ? null : $"{info.Name} ({info.Number})",
                ["client"] = info?.ClientName,
                ["phases"] = new JsonArray(doc.Phases.Cast<Phase>().Select(p => (JsonNode)p.Name).ToArray()),
                ["designOptions"] = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount(),
                ["warnings"] = RevitJson.Safe(() => doc.GetWarnings().Count),
                ["displayLengthUnit"] = RevitJson.Safe(() => LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId())),
            };
        }

        /// <summary>ARC / STR / MEP (or mixed), from the file name first, then from what the model contains.</summary>
        internal static string Discipline(Document d)
        {
            var name = (System.IO.Path.GetFileNameWithoutExtension(d.PathName ?? "") + " " + d.Title).ToUpperInvariant();
            if (Regex.IsMatch(name, @"(^|[^A-Z])(STR|STRUCT|STRUCTURAL|-S-)")) return "STR";
            if (Regex.IsMatch(name, @"(^|[^A-Z])(MEP|HVAC|MECH|ELEC|PLUMB|FIRE|-M-|-E-|-P-)")) return "MEP";
            if (Regex.IsMatch(name, @"(^|[^A-Z])(ARC|ARCH|ARCHITECTURAL|-A-)")) return "ARC";
            int C(BuiltInCategory c) => new FilteredElementCollector(d).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
            var str = C(BuiltInCategory.OST_StructuralColumns) + C(BuiltInCategory.OST_StructuralFraming) + C(BuiltInCategory.OST_StructuralFoundation);
            var mep = C(BuiltInCategory.OST_DuctCurves) + C(BuiltInCategory.OST_PipeCurves) + C(BuiltInCategory.OST_CableTray) + C(BuiltInCategory.OST_MechanicalEquipment);
            var arc = C(BuiltInCategory.OST_Rooms) + C(BuiltInCategory.OST_Doors) + C(BuiltInCategory.OST_Windows) + C(BuiltInCategory.OST_Furniture);
            var max = Math.Max(str, Math.Max(mep, arc));
            if (max == 0) return "unknown";
            return max == arc ? "ARC" : max == str ? "STR" : "MEP";
        }

        private static JsonArray Levels(Document doc)
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().Where(r => r.Area > 0).ToList();
            var arr = new JsonArray();
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
            {
                var onLevel = rooms.Where(r => r.LevelId == l.Id).ToList();
                arr.Add(new JsonObject
                {
                    ["name"] = l.Name, ["elevationMm"] = Mm(l.Elevation), ["rooms"] = onLevel.Count,
                    ["roomAreaM2"] = Math.Round(onLevel.Sum(r => r.Area) * 0.09290304),
                    ["roomTypes"] = string.Join(", ", onLevel.GroupBy(RoomType).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} x{g.Count()}")),
                });
            }
            return arr;
        }

        private static string RoomType(Room r)
        {
            var n = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            return string.IsNullOrWhiteSpace(n) ? "(unnamed)" : n.Trim();
        }

        private static JsonArray Rooms(Document doc)
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().Where(r => r.Area > 0).ToList();
            // What stands in each room (furniture, casework, fixtures, equipment).
            var contents = new Dictionary<long, int>();
            var movable = new[] { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_Casework, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_FurnitureSystems };
            foreach (var cat in movable)
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    Room room = null;
                    try { room = fi.Room; } catch { }
                    if (room != null) contents[room.Id.Value] = contents.TryGetValue(room.Id.Value, out var n) ? n + 1 : 1;
                }
            var arr = new JsonArray();
            foreach (var g in rooms.GroupBy(RoomType).OrderByDescending(g => g.Sum(r => r.Area)))
            {
                var list = g.ToList();
                var furnished = list.Count(r => contents.ContainsKey(r.Id.Value));
                arr.Add(new JsonObject
                {
                    ["type"] = g.Key, ["count"] = list.Count, ["totalAreaM2"] = Math.Round(list.Sum(r => r.Area) * 0.09290304),
                    ["areaRangeM2"] = $"{Math.Round(list.Min(r => r.Area) * 0.09290304)}-{Math.Round(list.Max(r => r.Area) * 0.09290304)}",
                    ["levels"] = string.Join(", ", list.Select(r => doc.GetElement(r.LevelId)?.Name).Distinct()),
                    ["numbers"] = string.Join(", ", list.Select(r => r.Number).OrderBy(n => n).Take(12)) + (list.Count > 12 ? ", ..." : ""),
                    ["furnished"] = furnished == 0 ? "none (empty)" : furnished == list.Count ? "all" : $"{furnished} of {list.Count}",
                });
            }
            return arr;
        }

        private static JsonObject DisciplineContent(Document doc)
        {
            var o = new JsonObject();
            foreach (var (label, cat) in new (string, BuiltInCategory)[]
            {
                ("Walls", BuiltInCategory.OST_Walls), ("Floors", BuiltInCategory.OST_Floors), ("Roofs", BuiltInCategory.OST_Roofs),
                ("Architectural columns", BuiltInCategory.OST_Columns), ("Structural columns", BuiltInCategory.OST_StructuralColumns),
                ("Structural framing", BuiltInCategory.OST_StructuralFraming), ("Foundations", BuiltInCategory.OST_StructuralFoundation),
                ("Doors", BuiltInCategory.OST_Doors), ("Windows", BuiltInCategory.OST_Windows), ("Rooms", BuiltInCategory.OST_Rooms),
                ("Furniture", BuiltInCategory.OST_Furniture), ("Ducts", BuiltInCategory.OST_DuctCurves), ("Pipes", BuiltInCategory.OST_PipeCurves),
                ("Cable trays", BuiltInCategory.OST_CableTray), ("Mechanical equipment", BuiltInCategory.OST_MechanicalEquipment),
                ("Grids", BuiltInCategory.OST_Grids),
            })
            {
                var n = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().GetElementCount();
                if (n > 0) o[label] = n;
            }
            return o;
        }

        private static JsonObject Naming(Document doc)
        {
            string Pattern(string s) => Regex.Replace(Regex.Replace(s, "[0-9]", "9"), "[A-Za-z]", "A");
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).Select(s => s.SheetNumber).ToList();
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).Select(v => v.Name).ToList();
            return new JsonObject
            {
                ["levels"] = string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).Take(20)),
                ["sheetNumberPatterns"] = string.Join(", ", sheets.GroupBy(Pattern).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} (e.g. {g.First()}, x{g.Count()})")),
                ["floorPlanNames"] = string.Join(", ", views.Take(12)) + (views.Count > 12 ? ", ..." : ""),
            };
        }

        // ---- families: what can be placed, and how ----------------------------------------------

        private static readonly (string Label, BuiltInCategory Cat)[] PlaceableCategories =
        {
            ("Furniture", BuiltInCategory.OST_Furniture), ("Furniture systems", BuiltInCategory.OST_FurnitureSystems),
            ("Casework", BuiltInCategory.OST_Casework), ("Plumbing fixtures", BuiltInCategory.OST_PlumbingFixtures),
            ("Specialty equipment", BuiltInCategory.OST_SpecialityEquipment), ("Lighting fixtures", BuiltInCategory.OST_LightingFixtures),
            ("Doors", BuiltInCategory.OST_Doors), ("Windows", BuiltInCategory.OST_Windows),
            ("Structural columns", BuiltInCategory.OST_StructuralColumns), ("Structural framing", BuiltInCategory.OST_StructuralFraming),
            ("Mechanical equipment", BuiltInCategory.OST_MechanicalEquipment), ("Generic models", BuiltInCategory.OST_GenericModel),
            ("Planting", BuiltInCategory.OST_Planting), ("Parking", BuiltInCategory.OST_Parking),
        };

        private static JsonObject Families(Document doc, int perCategory)
        {
            var o = new JsonObject();
            var placed = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.SuperComponent == null).GroupBy(f => f.Symbol.Id.Value).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var (label, cat) in PlaceableCategories)
            {
                var symbols = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
                if (symbols.Count == 0) continue;
                var arr = new JsonArray();
                foreach (var s in symbols.OrderByDescending(s => placed.TryGetValue(s.Id.Value, out var l) ? l.Count : 0).Take(perCategory))
                    arr.Add(FamilyEntry(s, placed.TryGetValue(s.Id.Value, out var inst) ? inst : null, brief: true));
                o[label] = arr;
                if (symbols.Count > perCategory) o[label + " (more types)"] = symbols.Count - perCategory;
            }
            return o;
        }

        internal static JsonObject FamilyEntry(FamilySymbol s, List<FamilyInstance> instances, bool brief)
        {
            var e = new JsonObject { ["name"] = $"{s.FamilyName} : {s.Name}", ["typeId"] = s.Id.Value, ["placed"] = instances?.Count ?? 0 };
            var extent = LocalExtent(s, instances?.FirstOrDefault());
            if (extent != null)
            {
                var (min, max) = extent.Value;
                e["footprintMm"] = $"{Mm(max.X - min.X)} x {Mm(max.Y - min.Y)} x {Mm(max.Z - min.Z)} (W x D x H, local)";
                var cx = (min.X + max.X) / 2; var cy = (min.Y + max.Y) / 2;
                e["originFromCentreMm"] = $"{Mm(-cx)}, {Mm(-cy)}";   // where the insertion point sits relative to the footprint centre
            }
            try
            {
                e["placement"] = s.Family.FamilyPlacementType.ToString();
                var first = instances?.FirstOrDefault();
                if (first != null && first.GetSubComponentIds().Count > 0)
                    e["nested"] = string.Join(", ", first.GetSubComponentIds().Select(i => (s.Document.GetElement(i) as FamilyInstance)?.Symbol.FamilyName).Where(n => n != null).GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"));
            }
            catch { }
            if (!brief)
            {
                var dims = new JsonObject();
                foreach (Parameter p in s.Parameters)
                    if (p.StorageType == StorageType.Double && p.Definition != null && p.Definition.GetDataType() == SpecTypeId.Length)
                        dims[p.Definition.Name] = Mm(p.AsDouble());
                e["typeDimensionsMm"] = dims;
                e["active"] = s.IsActive;
                e["hostRequired"] = s.Family.FamilyPlacementType.ToString().Contains("Host") || s.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased;
                var sample = instances?.FirstOrDefault();
                if (sample != null)
                {
                    e["sampleInstance"] = sample.Id.Value;
                    e["facingOfSample"] = $"({sample.FacingOrientation.X:0.00}, {sample.FacingOrientation.Y:0.00})";
                    if (sample.Location is LocationPoint lp) e["sampleRotationDeg"] = Math.Round(lp.Rotation * 180 / Math.PI);
                    e["sampleHost"] = sample.Host?.Name;
                    e["sampleLevel"] = sample.Document.GetElement(sample.LevelId)?.Name;
                }
                e["note"] = "Unrotated, the family's local +Y is its facing direction and +X its hand direction. Chairs in this project's families face +Y (toward what is in front of them).";
            }
            return e;
        }

        /// <summary>The family's extent in its own coordinates (from a placed instance's symbol geometry).</summary>
        internal static (XYZ Min, XYZ Max)? LocalExtent(FamilySymbol s, FamilyInstance sample)
        {
            try
            {
                GeometryElement geo = null;
                if (sample != null)
                {
                    var g = sample.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    var gi = g?.OfType<GeometryInstance>().FirstOrDefault();
                    geo = gi?.GetSymbolGeometry();
                }
                geo ??= s.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (geo == null) return null;
                var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
                var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
                var any = false;
                void Grow(XYZ p)
                {
                    min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z)); any = true;
                }
                void Walk(GeometryElement ge, Transform t)
                {
                    foreach (var obj in ge)
                    {
                        if (obj is Solid sol && sol.Volume > 1e-9)
                        {
                            var bb = sol.GetBoundingBox();
                            foreach (var c in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) })
                                Grow(t.OfPoint(bb.Transform.OfPoint(c)));
                        }
                        else if (obj is GeometryInstance inner) Walk(inner.GetSymbolGeometry(), t.Multiply(inner.Transform));
                    }
                }
                Walk(geo, Transform.Identity);
                return any ? (min, max) : ((XYZ, XYZ)?)null;
            }
            catch { return null; }
        }

        public static JsonNode DescribeFamily(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var q = Args.Str(args, "name") ?? throw new CommandException("Give 'name': a family or type name (partial is fine).");
            var matches = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => $"{s.FamilyName} : {s.Name}".IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(15).ToList();
            if (matches.Count == 0) throw new CommandException($"No loaded family or type matches '{q}'.");
            var placed = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.SuperComponent == null && matches.Any(m => m.Id == f.Symbol.Id)).GroupBy(f => f.Symbol.Id.Value).ToDictionary(g => g.Key, g => g.ToList());
            return new JsonArray(matches.Select(s => (JsonNode)FamilyEntry(s, placed.TryGetValue(s.Id.Value, out var l) ? l : null, brief: false)).ToArray());
        }

        // ---- other models ------------------------------------------------------------------------

        /// <summary>Summary of another model and how it lines up with the host (toHost maps its coordinates to the host's).</summary>
        private static void Fill(JsonObject o, Document other, Document host, Transform toHost)
        {
            o["title"] = other.Title;
            o["discipline"] = Discipline(other);
            o["content"] = DisciplineContent(other);
            var angle = Math.Atan2(toHost.BasisX.Y, toHost.BasisX.X) * 180 / Math.PI;
            o["placement"] = $"offset {Mm(toHost.Origin.X)}, {Mm(toHost.Origin.Y)}, {Mm(toHost.Origin.Z)} mm; rotation {Math.Round(angle, 2)} deg";
            o["alignment"] = Alignment(other, host, toHost);
        }

        /// <summary>Do the other model's levels and grids coincide with the host's (same names, same positions)?</summary>
        internal static JsonObject Alignment(Document other, Document host, Transform toHost)
        {
            const double tolMm = 5;
            var hostLevels = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().ToList();
            var otherLevels = new FilteredElementCollector(other).OfClass(typeof(Level)).Cast<Level>().ToList();
            var levelIssues = new JsonArray();
            var levelMatched = 0;
            foreach (var ol in otherLevels)
            {
                var z = toHost.OfPoint(new XYZ(0, 0, ol.ProjectElevation)).Z;
                var hl = hostLevels.FirstOrDefault(h => string.Equals(h.Name, ol.Name, StringComparison.OrdinalIgnoreCase));
                if (hl == null)
                {
                    var near = hostLevels.OrderBy(h => Math.Abs(h.ProjectElevation - z)).FirstOrDefault();
                    if (near != null && Lengths.Mm(Math.Abs(near.ProjectElevation - z)) <= tolMm) { levelMatched++; levelIssues.Add($"'{ol.Name}' is at the same height as host level '{near.Name}' but named differently"); }
                    else levelIssues.Add($"'{ol.Name}' ({Mm(z)} mm) has no matching level in this model");
                    continue;
                }
                var d = Lengths.Mm(z - hl.ProjectElevation);
                if (Math.Abs(d) > tolMm) levelIssues.Add($"'{ol.Name}' is {Math.Round(d)} mm {(d > 0 ? "higher" : "lower")} than in this model");
                else levelMatched++;
            }

            var hostGrids = new FilteredElementCollector(host).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var gridIssues = new JsonArray();
            var gridMatched = 0;
            foreach (var og in new FilteredElementCollector(other).OfClass(typeof(Grid)).Cast<Grid>())
            {
                var hg = hostGrids.FirstOrDefault(h => string.Equals(h.Name, og.Name, StringComparison.OrdinalIgnoreCase));
                if (hg == null) { gridIssues.Add($"grid '{og.Name}' has no matching grid in this model"); continue; }
                try
                {
                    var a = og.Curve; var b = hg.Curve;
                    var p = toHost.OfPoint(a.Evaluate(0.5, true));
                    var unbounded = b.Clone(); unbounded.MakeUnbound();
                    var dist = Lengths.Mm(unbounded.Distance(new XYZ(p.X, p.Y, unbounded.GetEndPoint(0).Z)));
                    var dirA = toHost.OfVector((a.GetEndPoint(1) - a.GetEndPoint(0)).Normalize());
                    var dirB = (b.GetEndPoint(1) - b.GetEndPoint(0)).Normalize();
                    var angle = Math.Acos(Math.Min(1, Math.Abs(dirA.DotProduct(dirB)))) * 180 / Math.PI;
                    if (dist > tolMm || angle > 0.01) gridIssues.Add($"grid '{og.Name}' is {Math.Round(dist)} mm off{(angle > 0.01 ? $" and {angle:0.00} deg rotated" : "")}");
                    else gridMatched++;
                }
                catch { }
            }
            return new JsonObject
            {
                ["levelsMatching"] = levelMatched, ["levelIssues"] = levelIssues.Count == 0 ? null : levelIssues,
                ["gridsMatching"] = gridMatched, ["gridIssues"] = gridIssues.Count == 0 ? null : gridIssues,
                ["verdict"] = levelIssues.Count == 0 && gridIssues.Count == 0 ? "aligned" : "check the issues listed",
            };
        }
    }
}
