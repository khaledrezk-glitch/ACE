using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Applies the presentation standard: every text note and dimension in the standard's views gets the one type chosen
    /// for its kind (text, linear / angular / radial ... dimensions, spot dimensions) and for the text height of its view's
    /// scale; optionally every tag of one category gets one tag type. A missing type is made from the most used type of
    /// that kind, at the right height. Run it inside a transaction; with plan only, nothing is changed or created.
    /// </summary>
    internal static class PresentationFix
    {
        internal sealed class Options
        {
            public bool PlanOnly;
            public HashSet<string> ViewNames;
        }

        private sealed class Item
        {
            public Element Element;
            public string Kind;
            public double? TargetMm;
        }

        public static string KindOf(Element e) => e switch
        {
            TextNote _ => "text",
            Dimension d => "dim:" + (RevitJson.Safe(() => d.DimensionType?.StyleType.ToString()) ?? "Other"),
            IndependentTag t => "tag:" + (t.Category?.Name ?? "Tags"),
            _ => null,
        };

        public static string KindLabel(string kind) => kind == "text" ? "Text" : kind.StartsWith("dim:") ? $"Dimensions ({kind.Substring(4)})" : kind.StartsWith("tag:") ? kind.Substring(4) : kind;

        /// <summary>Text and dimension types of the model, by kind (for the panel's choices and for the fix).</summary>
        public static Dictionary<string, List<ElementType>> TypesByKind(Document doc)
        {
            var result = new Dictionary<string, List<ElementType>>();
            void Add(string kind, ElementType t) { if (!result.TryGetValue(kind, out var l)) result[kind] = l = new List<ElementType>(); l.Add(t); }
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<ElementType>()) Add("text", t);
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>())
                if (RevitJson.Safe(() => t.StyleType) is var style) Add("dim:" + style, t);
            return result;
        }

        public static JsonObject Run(Document doc, PresentationStandard std, Options opt)
        {
            var views = AnnotationAudit.Views(doc, std);
            if (opt.ViewNames != null && opt.ViewNames.Count > 0)
                views = views.Values.Where(v => opt.ViewNames.Contains(v.Name)).ToDictionary(v => v.Id.Value);

            // Everything to look at, with the text height its view's scale asks for.
            var items = new List<Item>();
            IEnumerable<Element> annotations = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).ToElements()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Dimension)).ToElements())
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SpotDimension)).ToElements());
            if (std.UnifyTagTypes) annotations = annotations.Concat(new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).ToElements());
            foreach (var e in annotations)
            {
                if (!views.TryGetValue(e.OwnerViewId.Value, out var view)) continue;
                var kind = KindOf(e);
                if (kind == null) continue;
                items.Add(new Item { Element = e, Kind = kind, TargetMm = kind.StartsWith("tag:") ? null : std.TextMm(view.Scale) });
            }

            // How often each type is used (in the whole model), to pick the type people already use most.
            var usage = items.GroupBy(i => i.Element.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
            int Used(ElementType t) => usage.TryGetValue(t.Id.Value, out var n) ? n : 0;
            var byKind = TypesByKind(doc);
            var created = new List<string>();
            var notes = new List<string>();
            var canonical = new Dictionary<string, ElementType>();

            ElementType Canonical(string kind, double? mm)
            {
                var key = kind + "|" + (mm == null ? "" : PresentationStandard.Band(mm.Value));
                if (canonical.TryGetValue(key, out var found)) return found;
                ElementType pick = null;
                if (kind.StartsWith("tag:"))
                {
                    var chosen = std.TypeFor("tags", kind.Substring(4));
                    var inUse = items.Where(i => i.Kind == kind).Select(i => doc.GetElement(i.Element.GetTypeId()) as ElementType).Where(t => t != null).GroupBy(t => t.Id.Value).OrderByDescending(g => g.Count()).Select(g => g.First()).ToList();
                    pick = (chosen != null ? inUse.FirstOrDefault(t => Label(t) == chosen || t.Name == chosen) : null) ?? inUse.FirstOrDefault();
                    return canonical[key] = pick;
                }
                var band = PresentationStandard.Band(mm.Value);
                var candidates = byKind.TryGetValue(kind, out var l) ? l : new List<ElementType>();
                var chosenName = std.TypeFor(band, kind);
                if (chosenName != null)
                {
                    pick = candidates.FirstOrDefault(t => t.Name == chosenName);
                    if (pick == null) notes.Add($"The chosen {KindLabel(kind)} type '{chosenName}' for {band} mm is not in this model; ACE picks one instead.");
                    else if (Math.Abs(AnnotationAudit.SizeMm(pick) - mm.Value) > 0.05)
                        notes.Add($"The chosen {KindLabel(kind)} type '{pick.Name}' is {AnnotationAudit.SizeMm(pick):0.##} mm, not {band} mm; it is used as chosen.");
                }
                pick ??= candidates.Where(t => Math.Abs(AnnotationAudit.SizeMm(t) - mm.Value) <= 0.05 && (std.Font == null || FontOf(t) == std.Font))
                                   .OrderByDescending(Used).ThenBy(t => t.Name).FirstOrDefault();
                if (pick == null)
                {
                    var from = candidates.OrderByDescending(Used).ThenBy(t => t.Name).FirstOrDefault();
                    if (from == null) return canonical[key] = null;
                    var name = Unique(candidates, kind == "text" ? $"ACE Text {band}mm{(std.Font != null ? " " + std.Font : "")}" : $"{from.Name} {band}mm");
                    if (opt.PlanOnly) { created.Add($"{name} (would be made from '{from.Name}')"); return canonical[key] = null; }
                    pick = from.Duplicate(name);
                    pick.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(Lengths.Ft(mm.Value));
                    if (std.Font != null) { var f = pick.get_Parameter(BuiltInParameter.TEXT_FONT); if (f != null && !f.IsReadOnly) f.Set(std.Font); }
                    candidates.Add(pick);
                    created.Add(name);
                }
                return canonical[key] = pick;
            }

            var changed = new Dictionary<string, int>();
            var off = new Dictionary<string, int>();
            var skipped = new List<string>();
            var samples = new List<string>();
            foreach (var item in items)
            {
                var target = Canonical(item.Kind, item.TargetMm);
                var current = item.Element.GetTypeId();
                var group = item.Kind.StartsWith("tag:") ? "tags" : item.Kind == "text" ? "text" : "dimensions";
                if (item.TargetMm != null && Math.Abs(AnnotationAudit.SizeMm(doc.GetElement(current)) - item.TargetMm.Value) > 0.05) Count(off, group);
                if (target == null || target.Id == current) continue;
                if (opt.PlanOnly) { Count(changed, group); continue; }
                if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, item.Element.Id) == CheckoutStatus.OwnedByOtherUser)
                { if (skipped.Count < 50) skipped.Add($"{item.Element.Id.Value} (in use by a colleague)"); continue; }
                try
                {
                    item.Element.ChangeTypeId(target.Id);
                    Count(changed, group);
                    if (samples.Count < 15) samples.Add($"{KindLabel(item.Kind)} {item.Element.Id.Value}: '{doc.GetElement(current)?.Name}' -> '{target.Name}'");
                }
                catch (Exception ex) { if (skipped.Count < 50) skipped.Add($"{item.Element.Id.Value}: {ex.Message}"); }
            }

            return new JsonObject
            {
                ["standard"] = $"{std.Name}: {std.Describe()}{(std.Font != null ? $", font {std.Font}" : "")}",
                ["source"] = std.Source,
                ["views"] = views.Count,
                ["viewsScope"] = opt.ViewNames != null && opt.ViewNames.Count > 0 ? "named views" : std.ViewsOnSheetsOnly ? "views on sheets" : "all views",
                ["checked"] = items.Count,
                ["wrongHeight"] = new JsonObject(off.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value))),
                [opt.PlanOnly ? "wouldChangeType" : "changedType"] = new JsonObject(changed.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value))),
                [opt.PlanOnly ? "typesToMake" : "typesMade"] = new JsonArray(created.Select(c => (JsonNode)c).ToArray()),
                ["typesUsed"] = new JsonArray(canonical.Where(c => c.Value != null).Select(c => (JsonNode)$"{KindLabel(c.Key.Split('|')[0])}{(c.Key.EndsWith("|") ? "" : $" {c.Key.Split('|')[1]} mm")}: {Label(c.Value)}").ToArray()),
                ["samples"] = new JsonArray(samples.Select(s => (JsonNode)s).ToArray()),
                ["skipped"] = skipped.Count == 0 ? null : new JsonArray(skipped.Select(s => (JsonNode)s).ToArray()),
                ["notes"] = notes.Count == 0 ? null : new JsonArray(notes.Distinct().Select(s => (JsonNode)s).ToArray()),
            };
        }

        private static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;

        internal static string Label(ElementType t) => t is FamilySymbol fs ? $"{fs.FamilyName} : {fs.Name}" : t.Name;

        private static string FontOf(ElementType t) => t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString();

        private static string Unique(IEnumerable<ElementType> existing, string name)
        {
            var names = new HashSet<string>(existing.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            var n = name;
            for (var i = 2; names.Contains(n); i++) n = $"{name} ({i})";
            return n;
        }
    }
}
