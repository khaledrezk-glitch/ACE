using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// The ACE Clash View: one 3D view per user in which ACE shows coordination.
    /// Overview: the primary model (this model, or a link) in green, the secondary in red (or each link in its own colour), other links hidden.
    /// Focus: one clash; everything else dimmed, the two elements drawn in their colours, the intersection in gold,
    /// a section box around the intersection (so a large slab is cut down to the area around the pipe) and zoomed to it.
    /// Only this view's own settings change; ACE never touches other views or elements.
    /// </summary>
    internal static class ClashView
    {
        // Colours by role: the primary model green, the compared model red, the intersection gold. With several links
        // each link has its own colour, which the user can choose (ClashColours).
        internal static readonly System.Windows.Media.Color HitColour = System.Windows.Media.Color.FromRgb(0xD1, 0xA1, 0x4A);

        /// <summary>This model's clash comparison (primary, compared, focused clash), kept per model in the status store
        /// and shared by the Clash Browser and Claude.</summary>
        internal static StatusStore.ClashSession Session(string model) => StatusStore.Entry(model).Session;

        internal static string PrimaryName(Document doc) => string.IsNullOrEmpty(Session(doc.Title).Primary) ? doc.Title : Session(doc.Title).Primary;
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>The models other than the primary, in link order (for default colours).</summary>
        internal static List<string> Others(List<Source> sources, string primary) =>
            sources.Where(x => !Same(x.Name, primary)).Select(x => x.Name).ToList();

        /// <summary>
        /// Colours of the two sides of a clash: each side in its model's colour (primary green, compared red, other links
        /// their own). Two elements of one model (e.g. MEP vs MEP): the model's colour and the next palette colour.
        /// </summary>
        internal static (System.Windows.Media.Color A, string NameA, System.Windows.Media.Color B, string NameB) Colours(string primary, string withModel, string sourceA, string sourceB, List<string> others)
        {
            var a = ClashColours.For(sourceA, primary, withModel, others);
            var b = ClashColours.For(sourceB, primary, withModel, others);
            if (Same(sourceA, sourceB) || a == b) b = ClashColours.Next(a);
            return (a, ClashColours.NameOf(a), b, ClashColours.NameOf(b));
        }

        private static ColorWithTransparency Dc(System.Windows.Media.Color c, uint transparency) => new ColorWithTransparency(c.R, c.G, c.B, transparency);

        private static string ViewName(UIApplication app) => $"ACE Clash View - {app.Application.Username}";

        private static View3D FindOrCreate(UIApplication app, Document doc)
        {
            var name = ViewName(app);
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == name);
            if (v != null) return v;
            var type = ViewTools.ViewType(doc, ViewFamily.ThreeDimensional) ?? throw new CommandException("This model has no 3D view type, so the clash view cannot be made.");
            v = View3D.CreateIsometric(doc, type.Id);
            v.Name = name;
            return v;
        }

        private static OverrideGraphicSettings Solid(Document doc, System.Windows.Media.Color c) => ViewTools.Highlight(doc, c);

        /// <summary>The look last applied to each clash view, so a click on the next clash does not repaint ~200 categories.</summary>
        private static readonly Dictionary<long, string> AppliedLook = new Dictionary<long, string>();

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
            var others = Others(sources, primary);
            var look = focus ? $"focus|{primary}|{withModel}"
                : $"overview|{primary}|{withModel}|" + string.Join(",", new[] { doc.Title }.Concat(others).Select(m => ClashColours.NameOf(ClashColours.For(m, primary, withModel, others))));
            if (AppliedLook.TryGetValue(v.Id.Value, out var applied) && applied == look) return v;
            AppliedLook[v.Id.Value] = look;
            // This model: its colour when it is the primary or the compared model, ghosted when a BIM manager compares two
            // links. In focus mode everything is dimmed (the clash is drawn on top).
            var hostLook = focus ? Dimmed()
                : Same(doc.Title, primary) || Same(doc.Title, withModel) ? Solid(doc, ClashColours.For(doc.Title, primary, withModel, others))
                : Dimmed();
            foreach (var c in ModelCategories(doc, v))
                try { v.SetCategoryOverrides(c.Id, hostLook); } catch { }
            // Links: the primary and the compared model in their colours (every other link in its own colour when none is
            // chosen); every other link hidden.
            var links = sources.Where(s => s.LinkInstanceId != null).ToList();
            var hide = new List<ElementId>(); var show = new List<ElementId>();
            foreach (var l in links)
            {
                var isPrimary = Same(l.Name, primary);
                var isSecondary = withModel == null ? !isPrimary : Same(l.Name, withModel);
                if (isPrimary || isSecondary)
                {
                    show.Add(l.LinkInstanceId);
                    try { v.SetElementOverrides(l.LinkInstanceId, focus ? Dimmed() : Solid(doc, ClashColours.For(l.Name, primary, withModel, others))); } catch { }
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
        /// the primary model green, the compared model red (or each link its own colour).
        /// </summary>
        public static JsonNode Overview(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var session = Session(doc.Title);
            if (args.ContainsKey("primary_model")) session.Primary = Args.Str(args, "primary_model");
            if (args["colours"] is JsonObject chosen)
                foreach (var (model, hex) in chosen)
                    if (ClashColours.Parse(hex?.ToString()) is System.Windows.Media.Color c) ClashColours.Set(model, c);
            if (Same(session.Primary, doc.Title)) session.Primary = null;
            if (args.ContainsKey("with_model")) session.With = Args.Str(args, "with_model");
            var withModel = session.With;
            var primary = PrimaryName(doc);
            session.FocusedKey = null;
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
            var other = sources.FirstOrDefault(s => withModel != null && Same(s.Name, withModel));
            var others = Others(sources, primary);
            string ColourOf(string m) => ClashColours.NameOf(ClashColours.For(m, primary, withModel, others));
            return new JsonObject
            {
                ["view"] = v.Name,
                ["primary"] = $"{primary}: {ColourOf(primary)}",
                ["secondary"] = withModel != null
                    ? $"{withModel}: {ColourOf(withModel)}" + (other != null && other.LinkInstanceId == null ? " (an open model, not a link: only the clash elements can be drawn here; link it to see it whole)" : "")
                    : string.Join(", ", sources.Where(x => x.LinkInstanceId != null && !Same(x.Name, primary)).Select(x => $"{x.Name}: {ColourOf(x.Name)}")),
                ["thisModel"] = Same(doc.Title, primary) ? "primary" : Same(doc.Title, withModel) ? "secondary" : "ghosted (a BIM manager view of two links)",
            };
        }

        /// <summary>Bridge "focus_clash": { key } - the one clash, zoomed to the intersection.</summary>
        public static JsonNode Focus(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var key = Args.Str(args, "key") ?? throw new CommandException("Give the clash 'key' (from run_clash_test or the Clash Browser).");
            var clash = Clashes.Current(doc).FirstOrDefault(c => c.Key == key)
                        ?? Clashes.LoadAll(doc).FirstOrDefault(c => c.Key == key)   // e.g. a custom test not in this session
                        ?? throw new CommandException("That clash is not in the stored results. Run the clash test again.");
            var sources = Clashes.Sources(app, doc);
            Source Find(string name) => sources.FirstOrDefault(s => s.Name == name);
            var sa = Find(clash.SourceA); var sb = Find(clash.SourceB);
            if (sa == null || sb == null) throw new CommandException($"The model {(sa == null ? clash.SourceA : clash.SourceB)} is not loaded or open.");
            var ea = sa.Doc.GetElement(clash.UA); var eb = sb.Doc.GetElement(clash.UB);
            if (ea == null || eb == null) throw new CommandException("One of the two elements no longer exists (the clash may be resolved). Run the clash test again.");

            // Both elements in the active model's coordinates, and their exact intersection.
            var solidsA = Clashes.Solids(ea, sa.ToHost); var solidsB = Clashes.Solids(eb, sb.ToHost);
            var hits = new List<Solid>();
            foreach (var x in solidsA)
                foreach (var y in solidsB)
                    try { var i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect); if (i != null && i.Volume > 1e-9) hits.Add(i); } catch { }

            // Focus box: the intersection (or the clash point for clearance clashes) plus a margin that scales with it.
            XYZ min, max;
            if (hits.Count > 0)
            {
                (min, max) = ViewTools.Union(hits.Select(h => ViewTools.Box(h, Transform.Identity)));
            }
            else { var p = new XYZ(Lengths.Ft(clash.X), Lengths.Ft(clash.Y), Lengths.Ft(clash.Z)); min = p; max = p; }
            var size = (max - min).GetLength();
            var pad = Math.Max(Lengths.Ft(Args.Int(args, "margin_mm", 800)), size * 0.75);
            var fmin = new XYZ(min.X - pad, min.Y - pad, min.Z - pad);
            var fmax = new XYZ(max.X + pad, max.Y + pad, max.Z + pad);

            var session = Session(doc.Title);
            var primaryName = PrimaryName(doc);
            var withModel = session.With ?? (Same(sa.Name, primaryName) ? sb.Name : sa.Name);
            View3D v;
            using (var t = new Transaction(doc, "ACE clash view"))
            {
                t.Start();
                v = Prepare(app, doc, sources, primaryName, withModel, focus: true);
                v.SetSectionBox(new BoundingBoxXYZ { Min = fmin, Max = fmax });
                v.IsSectionBoxActive = true;
                t.Commit();
            }

            // Highlight: element colours by role (primary green, compared red), cut to the focus box, and the intersection in gold.
            var box = BoxSolid(fmin, fmax);
            var colours = Colours(primaryName, session.With, sa.Name, sb.Name, Others(sources, primaryName));
            var off = Lengths.Ft(3);
            var shapes = new List<ClashHighlight.Shape>
            {
                ClashHighlight.FromSolids(Clip(solidsA, box), Dc(colours.A, 90), off),
                ClashHighlight.FromSolids(Clip(solidsB, box), Dc(colours.B, 90), off),
            };
            if (hits.Count > 0) shapes.Add(ClashHighlight.FromSolids(hits, Dc(HitColour, 0), Lengths.Ft(6)));
            var server = ClashHighlight.Ensure();
            server.Show(v.Id, shapes);
            session.FocusedKey = key;

            var zpad = Math.Max(Lengths.Ft(400), size * 0.6);
            Open(app, v, new XYZ(min.X - zpad, min.Y - zpad, min.Z - zpad), new XYZ(max.X + zpad, max.Y + zpad, max.Z + zpad));
            return new JsonObject
            {
                ["view"] = v.Name, ["clash"] = key, ["level"] = clash.Level,
                ["a"] = $"{clash.CatA}: {clash.NameA} ({clash.SourceA}, {colours.NameA})",
                ["b"] = $"{clash.CatB}: {clash.NameB} ({clash.SourceB}, {colours.NameB})",
                ["intersection"] = "gold",
                ["intersectionMm"] = hits.Count > 0 ? $"{Math.Round(Lengths.Mm(max.X - min.X))} x {Math.Round(Lengths.Mm(max.Y - min.Y))} x {Math.Round(Lengths.Mm(max.Z - min.Z))}" : "none (clearance clash)",
                ["responsible"] = clash.Responsible, ["cause"] = clash.Cause,
            };
        }

        /// <summary>Bridge "reset_clash_view": clears the highlight and the section box; colours stay per model.</summary>
        public static JsonNode Reset(UIApplication app, JsonObject args) => Overview(app, new JsonObject());

        // ---- geometry ----------------------------------------------------------------------------------------------------

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

    /// <summary>
    /// Model colours in the ACE Clash View: the primary green, the secondary red, other links from the brand palette in
    /// turn, or the colour the user chose for a model (saved in %APPDATA%\ACE-RevitMCP\clash-colours.json).
    /// </summary>
    internal static class ClashColours
    {
        private static System.Windows.Media.Color C(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);
        internal static readonly System.Windows.Media.Color Green = C(0x00, 0x8B, 0x45), Red = C(0xEF, 0x33, 0x40);
        /// <summary>ACE brand colours (primary and secondary palette); gold is kept for the intersection.</summary>
        internal static readonly (string Name, System.Windows.Media.Color Colour)[] Palette =
        {
            ("green", Green), ("red", Red), ("blue", C(0x2A, 0x9F, 0xBC)), ("berry", C(0xA6, 0x2E, 0x5C)),
            ("navy", C(0x02, 0x06, 0x6F)), ("lime", C(0x9B, 0xC8, 0x50)), ("grey", C(0x41, 0x40, 0x42)), ("silver", C(0xA0, 0xA0, 0xA0)),
        };

        private static Dictionary<string, System.Windows.Media.Color> _chosen;
        private static string File => System.IO.Path.Combine(AceConfig.Directory, "clash-colours.json");

        private static Dictionary<string, System.Windows.Media.Color> Chosen()
        {
            if (_chosen != null) return _chosen;
            _chosen = new Dictionary<string, System.Windows.Media.Color>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject o)
                    foreach (var (k, v) in o) if (Parse(v?.ToString()) is System.Windows.Media.Color c) _chosen[k] = c;
            }
            catch { }
            return _chosen;
        }

        internal static System.Windows.Media.Color For(string model, string primary, string withModel, IList<string> others)
        {
            if (model != null && Chosen().TryGetValue(model, out var c)) return c;
            if (string.Equals(model, primary, StringComparison.OrdinalIgnoreCase)) return Green;
            if (withModel != null && string.Equals(model, withModel, StringComparison.OrdinalIgnoreCase)) return Red;
            var i = others?.ToList().FindIndex(o => string.Equals(o, model, StringComparison.OrdinalIgnoreCase)) ?? -1;
            var cycle = Palette.Skip(1).ToArray();   // red, blue, berry, ... (green is the primary's)
            return cycle[Math.Max(0, i) % cycle.Length].Colour;
        }

        internal static void Set(string model, System.Windows.Media.Color c) { Chosen()[model] = c; Save(); }

        /// <summary>Back to the default colour for the model's role.</summary>
        internal static void Reset(string model) { if (Chosen().Remove(model)) Save(); }

        private static void Save()
        {
            try
            {
                var o = new JsonObject();
                foreach (var (k, v) in Chosen()) o[k] = $"#{v.R:X2}{v.G:X2}{v.B:X2}";
                System.IO.File.WriteAllText(File, o.ToJsonString());
            }
            catch (Exception ex) { Log.Warn($"Clash colours: {ex.Message}"); }
        }

        internal static System.Windows.Media.Color Next(System.Windows.Media.Color c)
        {
            var i = Array.FindIndex(Palette, p => p.Colour == c);
            return Palette[(i + 1 + Palette.Length) % Palette.Length].Colour;
        }

        internal static string NameOf(System.Windows.Media.Color c) =>
            Palette.Where(p => p.Colour == c).Select(p => p.Name).FirstOrDefault() ?? $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        internal static System.Windows.Media.Color? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var named = Palette.Where(p => p.Name.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase)).Select(p => (System.Windows.Media.Color?)p.Colour).FirstOrDefault();
            if (named != null) return named;
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.Trim()); } catch { return null; }
        }
    }
}
