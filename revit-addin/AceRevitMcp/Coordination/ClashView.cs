using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// The ACE Clash View: one 3D view per user in which ACE shows coordination.
    /// Overview: the primary model (this model, or a link) in ACE Red, the secondary in UAE Flag Green, other links hidden.
    /// Focus: one clash; everything else dimmed, the two elements drawn in their colours, the intersection in gold,
    /// a section box around the intersection (so a large slab is cut down to the area around the pipe) and zoomed to it.
    /// Only this view's own settings change; ACE never touches other views or elements.
    /// </summary>
    internal static class ClashView
    {
        // Brand colours by role: ACE Red for the primary model, UAE Flag Green (secondary palette) for the secondary,
        // Bright Gold for the intersection.
        internal static System.Windows.Media.Color PrimaryColour => Branding.Accent;
        internal static readonly System.Windows.Media.Color SecondaryColour = System.Windows.Media.Color.FromRgb(0x00, 0x8B, 0x45);
        internal static readonly System.Windows.Media.Color HitColour = System.Windows.Media.Color.FromRgb(0xD1, 0xA1, 0x4A);

        /// <summary>The primary model: this model (null), or a link for a BIM manager comparing two links.</summary>
        internal static string PrimaryModel;
        /// <summary>The secondary model chosen to compare with (null = all loaded links).</summary>
        internal static string WithModel;

        internal static string PrimaryName(Document doc) => string.IsNullOrEmpty(PrimaryModel) ? doc.Title : PrimaryModel;
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Colours of the two sides of a clash: the side in the primary model red, the other green. Two elements of one
        /// model (e.g. MEP vs MEP): A red, B green.
        /// </summary>
        internal static (System.Windows.Media.Color A, string NameA, System.Windows.Media.Color B, string NameB) Colours(string primary, string sourceA, string sourceB)
        {
            var aPrimary = Same(sourceA, primary);
            var bPrimary = Same(sourceB, primary);
            if (bPrimary && !aPrimary) return (SecondaryColour, "green", PrimaryColour, "red");
            return (PrimaryColour, "red", SecondaryColour, "green");
        }

        internal static string FocusedKey;

        private static Color Rc(System.Windows.Media.Color c) => new Color(c.R, c.G, c.B);
        private static ColorWithTransparency Dc(System.Windows.Media.Color c, uint transparency) => new ColorWithTransparency(c.R, c.G, c.B, transparency);
        private static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static string ViewName(UIApplication app) => $"ACE Clash View - {app.Application.Username}";

        private static View3D FindOrCreate(UIApplication app, Document doc)
        {
            var name = ViewName(app);
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == name);
            if (v != null) return v;
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            v = View3D.CreateIsometric(doc, type.Id);
            v.Name = name;
            return v;
        }

        private static OverrideGraphicSettings Solid(Document doc, System.Windows.Media.Color c, int transparency)
        {
            var o = new OverrideGraphicSettings().SetProjectionLineColor(Rc(c)).SetSurfaceTransparency(transparency);
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            if (solid != null) o = o.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(Rc(c));
            return o;
        }

        private static OverrideGraphicSettings Dimmed() => new OverrideGraphicSettings().SetHalftone(true).SetSurfaceTransparency(85);

        private static IEnumerable<Category> ModelCategories(Document doc, View v) =>
            doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model && RevitJson.Safe(() => v.CanCategoryBeHidden(c.Id)));

        /// <summary>Prepares the view: no template, colours per model (overview) or dimmed (focus), other links hidden.</summary>
        private static View3D Prepare(UIApplication app, Document doc, List<Source> sources, string primary, string withModel, bool focus)
        {
            var v = FindOrCreate(app, doc);
            if (v.ViewTemplateId != ElementId.InvalidElementId) v.ViewTemplateId = ElementId.InvalidElementId;
            v.DetailLevel = ViewDetailLevel.Fine;
            v.DisplayStyle = DisplayStyle.ShadingWithEdges;
            // This model: red when it is the primary, green when it is the secondary, ghosted when a BIM manager
            // compares two links. In focus mode everything is dimmed (the clash is drawn on top).
            var hostLook = focus ? Dimmed()
                : Same(doc.Title, primary) ? Solid(doc, PrimaryColour, 0)
                : Same(doc.Title, withModel) ? Solid(doc, SecondaryColour, 0)
                : Dimmed();
            foreach (var c in ModelCategories(doc, v))
                try { v.SetCategoryOverrides(c.Id, hostLook); } catch { }
            // Links: the primary red, the secondary green (all links green when none is chosen), every other link hidden.
            var links = sources.Where(s => s.LinkInstanceId != null).ToList();
            var hide = new List<ElementId>(); var show = new List<ElementId>();
            foreach (var l in links)
            {
                var isPrimary = Same(l.Name, primary);
                var isSecondary = withModel == null ? !isPrimary : Same(l.Name, withModel);
                if (isPrimary || isSecondary)
                {
                    show.Add(l.LinkInstanceId);
                    try { v.SetElementOverrides(l.LinkInstanceId, focus ? Dimmed() : Solid(doc, isPrimary ? PrimaryColour : SecondaryColour, 0)); } catch { }
                }
                else hide.Add(l.LinkInstanceId);
            }
            var hidden = show.Where(id => doc.GetElement(id)?.IsHidden(v) == true).ToList();
            if (hidden.Count > 0) try { v.UnhideElements(hidden); } catch { }
            var toHide = hide.Where(id => doc.GetElement(id)?.IsHidden(v) == false && doc.GetElement(id).CanBeHidden(v)).ToList();
            if (toHide.Count > 0) try { v.HideElements(toHide); } catch { }
            return v;
        }

        private static void Open(UIApplication app, View3D v, XYZ zoomMin = null, XYZ zoomMax = null)
        {
            var uidoc = app.ActiveUIDocument;
            try { if (uidoc.ActiveView.Id != v.Id) uidoc.ActiveView = v; }
            catch { try { uidoc.RequestViewChange(v); } catch { } }
            if (zoomMin == null) return;
            if (!Zoom(uidoc, v, zoomMin, zoomMax))
            {
                // The view opens on the next idle moment: zoom then.
                void Once(object s, Autodesk.Revit.UI.Events.IdlingEventArgs e)
                {
                    app.Idling -= Once;
                    try { Zoom(app.ActiveUIDocument, v, zoomMin, zoomMax); app.ActiveUIDocument.RefreshActiveView(); } catch { }
                }
                app.Idling += Once;
            }
            try { uidoc.RefreshActiveView(); } catch { }
        }

        private static bool Zoom(UIDocument uidoc, View v, XYZ min, XYZ max)
        {
            var ui = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == v.Id);
            if (ui == null) return false;
            ui.ZoomAndCenterRectangle(min, max);
            return true;
        }

        // ---- commands ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Bridge "clash_view": { primary_model (default: this model), with_model: "MEP.rvt" (default: all links) } -
        /// the primary model red, the secondary green.
        /// </summary>
        public static JsonNode Overview(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            if (args.ContainsKey("primary_model")) PrimaryModel = Args.Str(args, "primary_model");
            if (Same(PrimaryModel, doc.Title)) PrimaryModel = null;
            var withModel = args.ContainsKey("with_model") ? Args.Str(args, "with_model") : WithModel;
            WithModel = withModel;
            var primary = PrimaryName(doc);
            FocusedKey = null;
            var sources = Clashes.Sources(app, doc);
            View3D v;
            using (var t = new Transaction(doc, "ACE clash view"))
            {
                t.Start();
                v = Prepare(app, doc, sources, primary, withModel, focus: false);
                if (v.IsSectionBoxActive) v.IsSectionBoxActive = false;
                t.Commit();
            }
            ClashHighlight.Instance?.Clear();
            Open(app, v);
            var other = sources.FirstOrDefault(s => withModel != null && string.Equals(s.Name, withModel, StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["view"] = v.Name,
                ["primary"] = $"{primary}: red",
                ["secondary"] = withModel == null ? "all other loaded links: green" : $"{withModel}: green" + (other != null && other.LinkInstanceId == null ? " (an open model, not a link: only the clash elements can be drawn here; link it to see it whole)" : ""),
                ["thisModel"] = Same(doc.Title, primary) ? "primary (red)" : Same(doc.Title, withModel) ? "secondary (green)" : "ghosted (a BIM manager view of two links)",
            };
        }

        /// <summary>Bridge "focus_clash": { key } - the one clash, zoomed to the intersection.</summary>
        public static JsonNode Focus(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var key = Args.Str(args, "key") ?? throw new CommandException("Give the clash 'key' (from run_clash_test or the Clash Browser).");
            var clash = (Clashes.Last ?? new List<Clash>()).Concat(Clashes.LoadAll(doc)).FirstOrDefault(c => c.Key == key)
                        ?? throw new CommandException("That clash is not in the stored results. Run the clash test again.");
            var sources = Clashes.Sources(app, doc);
            Source Find(string name) => sources.FirstOrDefault(s => s.Name == name);
            var sa = Find(clash.SourceA); var sb = Find(clash.SourceB);
            if (sa == null || sb == null) throw new CommandException($"The model {(sa == null ? clash.SourceA : clash.SourceB)} is not loaded or open.");
            var ea = sa.Doc.GetElement(clash.UA); var eb = sb.Doc.GetElement(clash.UB);
            if (ea == null || eb == null) throw new CommandException("One of the two elements no longer exists (the clash may be resolved). Run the clash test again.");

            // Both elements in the active model's coordinates, and their exact intersection.
            var solidsA = Solids(ea, sa.ToHost); var solidsB = Solids(eb, sb.ToHost);
            var hits = new List<Solid>();
            foreach (var x in solidsA)
                foreach (var y in solidsB)
                    try { var i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect); if (i != null && i.Volume > 1e-9) hits.Add(i); } catch { }

            // Focus box: the intersection (or the clash point for clearance clashes) plus a margin that scales with it.
            XYZ min, max;
            if (hits.Count > 0)
            {
                var boxes = hits.Select(h => { var bb = h.GetBoundingBox(); return (bb.Transform.OfPoint(bb.Min), bb.Transform.OfPoint(bb.Max)); }).ToList();
                min = new XYZ(boxes.Min(b => Math.Min(b.Item1.X, b.Item2.X)), boxes.Min(b => Math.Min(b.Item1.Y, b.Item2.Y)), boxes.Min(b => Math.Min(b.Item1.Z, b.Item2.Z)));
                max = new XYZ(boxes.Max(b => Math.Max(b.Item1.X, b.Item2.X)), boxes.Max(b => Math.Max(b.Item1.Y, b.Item2.Y)), boxes.Max(b => Math.Max(b.Item1.Z, b.Item2.Z)));
            }
            else { var p = new XYZ(Ft(clash.X), Ft(clash.Y), Ft(clash.Z)); min = p; max = p; }
            var size = (max - min).GetLength();
            var pad = Math.Max(Ft(Args.Int(args, "margin_mm", 800)), size * 0.75);
            var fmin = new XYZ(min.X - pad, min.Y - pad, min.Z - pad);
            var fmax = new XYZ(max.X + pad, max.Y + pad, max.Z + pad);

            View3D v;
            using (var t = new Transaction(doc, "ACE clash view"))
            {
                t.Start();
                var primaryName = PrimaryName(doc);
                var withModel = WithModel ?? (Same(sa.Name, primaryName) ? sb.Name : sa.Name);
                v = Prepare(app, doc, sources, primaryName, withModel, focus: true);
                v.SetSectionBox(new BoundingBoxXYZ { Min = fmin, Max = fmax });
                v.IsSectionBoxActive = true;
                t.Commit();
            }

            // Highlight: element colours by role (primary red, secondary green), cut to the focus box, and the intersection in gold.
            var box = BoxSolid(fmin, fmax);
            var colours = Colours(PrimaryName(doc), sa.Name, sb.Name);
            var colourA = colours.A; var colourB = colours.B;
            var off = Ft(3);
            var shapes = new List<ClashHighlight.Shape>
            {
                ClashHighlight.FromSolids(Clip(solidsA, box), Dc(colourA, 90), off),
                ClashHighlight.FromSolids(Clip(solidsB, box), Dc(colourB, 90), off),
            };
            if (hits.Count > 0) shapes.Add(ClashHighlight.FromSolids(hits, Dc(HitColour, 0), Ft(6)));
            var server = ClashHighlight.Ensure();
            server.Show(v.Id, shapes);
            FocusedKey = key;

            var zpad = Math.Max(Ft(400), size * 0.6);
            Open(app, v, new XYZ(min.X - zpad, min.Y - zpad, min.Z - zpad), new XYZ(max.X + zpad, max.Y + zpad, max.Z + zpad));
            return new JsonObject
            {
                ["view"] = v.Name, ["clash"] = key, ["level"] = clash.Level,
                ["a"] = $"{clash.CatA}: {clash.NameA} ({clash.SourceA}, {colours.NameA})",
                ["b"] = $"{clash.CatB}: {clash.NameB} ({clash.SourceB}, {colours.NameB})",
                ["intersection"] = "gold",
                ["intersectionMm"] = hits.Count > 0 ? $"{Math.Round((max.X - min.X) * 304.8)} x {Math.Round((max.Y - min.Y) * 304.8)} x {Math.Round((max.Z - min.Z) * 304.8)}" : "none (clearance clash)",
                ["responsible"] = clash.Responsible, ["cause"] = clash.Cause,
            };
        }

        /// <summary>Bridge "reset_clash_view": clears the highlight and the section box; colours stay per model.</summary>
        public static JsonNode Reset(UIApplication app, JsonObject args) => Overview(app, new JsonObject());

        // ---- geometry ----------------------------------------------------------------------------------------------------

        private static readonly Options GeoOptions = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };

        private static List<Solid> Solids(Element e, Transform toHost)
        {
            var list = new List<Solid>();
            void Walk(GeometryElement ge)
            {
                if (ge == null) return;
                foreach (var o in ge)
                {
                    if (o is Solid s && s.Volume > 1e-9) list.Add(s);
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                }
            }
            try { Walk(e.get_Geometry(GeoOptions)); } catch { }
            if (toHost == null || toHost.IsIdentity) return list;
            return list.Select(s => { try { return SolidUtils.CreateTransformed(s, toHost); } catch { return null; } }).Where(s => s != null).ToList();
        }

        private static Solid BoxSolid(XYZ min, XYZ max)
        {
            try
            {
                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z)),
                    Line.CreateBound(new XYZ(max.X, min.Y, min.Z), new XYZ(max.X, max.Y, min.Z)),
                    Line.CreateBound(new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, min.Z)),
                    Line.CreateBound(new XYZ(min.X, max.Y, min.Z), new XYZ(min.X, min.Y, min.Z)),
                });
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, max.Z - min.Z);
            }
            catch { return null; }
        }

        /// <summary>Only the part of each solid inside the focus box, so a large slab shows just the area around the clash.</summary>
        private static List<Solid> Clip(List<Solid> solids, Solid box)
        {
            if (box == null) return solids;
            var result = new List<Solid>();
            foreach (var s in solids)
            {
                try
                {
                    var c = BooleanOperationsUtils.ExecuteBooleanOperation(s, box, BooleanOperationsType.Intersect);
                    if (c != null && c.Volume > 1e-9) result.Add(c);
                }
                catch { result.Add(s); }
            }
            return result;
        }
    }
}
