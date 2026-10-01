using System;
using System.Collections.Generic;
using System.Linq;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Read-only audit of text notes and dimensions against the presentation standard: which have the wrong printed
    /// text height for their view's scale, and how many types are used for the same size and style. The model check
    /// reports it; the presentation_standard script fixes it with the same rules.
    /// </summary>
    internal static class AnnotationAudit
    {
        internal sealed class Result
        {
            public int Views, Checked, ExtraTypes;
            public List<ElementId> OffStandard = new List<ElementId>();
        }

        /// <summary>The views the standard applies to: graphical, not templates; only those on sheets when the standard says so.</summary>
        internal static Dictionary<long, View> Views(Document doc, PresentationStandard std)
        {
            var onSheets = std.ViewsOnSheetsOnly
                ? new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId.Value))
                : null;
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule) && RevitJson.Safe(() => v.Scale) > 0)
                .Where(v => onSheets == null || onSheets.Contains(v.Id.Value))
                .ToDictionary(v => v.Id.Value);
        }

        internal static double SizeMm(Element type) =>
            Lengths.Mm(type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0);

        public static Result Run(Document doc, PresentationStandard std)
        {
            var r = new Result();
            var views = Views(doc, std);
            r.Views = views.Count;
            if (views.Count == 0) return r;
            var sizes = new Dictionary<long, double>();
            double Size(ElementId typeId)
            {
                if (!sizes.TryGetValue(typeId.Value, out var mm)) sizes[typeId.Value] = mm = SizeMm(doc.GetElement(typeId));
                return mm;
            }
            var used = new Dictionary<string, HashSet<long>>();   // "kind|size" -> type ids in use

            void Check(Element e, string kind)
            {
                if (!views.TryGetValue(e.OwnerViewId.Value, out var view)) return;
                var target = std.TextMm(view.Scale);
                if (target == null) return;
                r.Checked++;
                var typeId = e.GetTypeId();
                if (Math.Abs(Size(typeId) - target.Value) > 0.05) r.OffStandard.Add(e.Id);
                var key = $"{kind}|{target.Value:0.##}";
                if (!used.TryGetValue(key, out var set)) used[key] = set = new HashSet<long>();
                set.Add(typeId.Value);
            }

            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>()) Check(t, "text");
            foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                         .Concat(new FilteredElementCollector(doc).OfClass(typeof(SpotDimension)).Cast<Dimension>()))
                Check(d, "dim:" + RevitJson.Safe(() => d.DimensionType?.StyleType.ToString()));
            r.ExtraTypes = used.Values.Sum(s => Math.Max(0, s.Count - 1));
            return r;
        }
    }
}
