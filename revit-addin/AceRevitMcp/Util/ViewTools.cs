using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using WpfColor = System.Windows.Media.Color;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Shared view primitives for pictures and highlighted views (preview pictures, coordination report, clash view,
    /// view export): view types, highlight graphics, bounding boxes and PNG export. The base of the planned issue view
    /// engine (concept D4).
    /// </summary>
    internal static class ViewTools
    {
        public static ViewFamilyType ViewType(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == family);

        private static readonly Dictionary<int, ElementId> SolidFills = new Dictionary<int, ElementId>();

        /// <summary>The model's solid fill pattern (looked up once per document).</summary>
        public static ElementId SolidFill(Document doc)
        {
            var k = doc.GetHashCode();
            if (SolidFills.TryGetValue(k, out var id) && doc.GetElement(id) != null) return id;
            id = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                     .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;
            SolidFills[k] = id;
            return id;
        }

        public static Color Revit(WpfColor c) => new Color(c.R, c.G, c.B);

        /// <summary>A highlight: coloured lines, and (with <paramref name="fill"/>) a solid surface fill, optionally transparent.</summary>
        public static OverrideGraphicSettings Highlight(Document doc, WpfColor colour, bool fill = true, int transparency = 0, int lineWeight = 0)
        {
            var c = Revit(colour);
            var o = new OverrideGraphicSettings().SetProjectionLineColor(c);
            if (lineWeight > 0) o = o.SetProjectionLineWeight(lineWeight).SetCutLineColor(c).SetCutLineWeight(lineWeight);
            if (transparency > 0) o = o.SetSurfaceTransparency(transparency);
            var solid = fill ? SolidFill(doc) : ElementId.InvalidElementId;
            if (solid != ElementId.InvalidElementId) o = o.SetSurfaceForegroundPatternId(solid).SetSurfaceForegroundPatternColor(c);
            return o;
        }

        /// <summary>The box around several boxes.</summary>
        public static (XYZ Min, XYZ Max) Union(IEnumerable<(XYZ Min, XYZ Max)> boxes)
        {
            var list = boxes.ToList();
            return (new XYZ(list.Min(b => b.Min.X), list.Min(b => b.Min.Y), list.Min(b => b.Min.Z)),
                    new XYZ(list.Max(b => b.Max.X), list.Max(b => b.Max.Y), list.Max(b => b.Max.Z)));
        }

        /// <summary>A solid's bounding box in another coordinate system (all eight corners, so rotations are right).</summary>
        public static (XYZ Min, XYZ Max) Box(Solid s, Transform t) => Box(s.GetBoundingBox(), t);

        /// <summary>A bounding box (with its own transform) in another coordinate system, from all eight corners.</summary>
        public static (XYZ Min, XYZ Max) Box(BoundingBoxXYZ bb, Transform t)
        {
            var pts = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z) }
                .Select(p => t.OfPoint(bb.Transform.OfPoint(p))).ToList();
            return (new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)), new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z)));
        }

        /// <summary>
        /// Exports one view to <paramref name="folder"/>\<paramref name="name"/>.png and returns the file (Revit adds its
        /// own suffix to the name; the file is renamed back). Null when Revit wrote nothing.
        /// </summary>
        public static string ExportPng(Document doc, ElementId viewId, string folder, string name, int pixels, bool fitHorizontal = false)
        {
            Directory.CreateDirectory(folder);
            var opt = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, name),
                HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150, ZoomType = ZoomFitType.FitToPage, PixelSize = pixels,
            };
            if (fitHorizontal) opt.FitDirection = FitDirectionType.Horizontal;
            opt.SetViewsAndSheets(new List<ElementId> { viewId });
            doc.ExportImage(opt);
            var file = Directory.GetFiles(folder, name + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (file == null) return null;
            var final = Path.Combine(folder, name + ".png");
            if (!string.Equals(file, final, StringComparison.OrdinalIgnoreCase)) { if (File.Exists(final)) File.Delete(final); File.Move(file, final); }
            return final;
        }
    }
}
