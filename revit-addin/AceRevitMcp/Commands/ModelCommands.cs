using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal static class ModelCommands
    {
        public static JsonNode Ping(UIApplication app, JsonObject args)
        {
            var doc = app.ActiveUIDocument?.Document;
            return new JsonObject
            {
                ["revit"] = $"{app.Application.VersionName} ({app.Application.VersionBuild})",
                ["addinVersion"] = typeof(ModelCommands).Assembly.GetName().Version?.ToString(),
                ["user"] = app.Application.Username,
                ["language"] = app.Application.Language.ToString(),
                ["activeDocument"] = doc?.Title,
                ["openDocuments"] = new JsonArray(app.Application.Documents.Cast<Document>().Select(d => (JsonNode)d.Title).ToArray()),
            };
        }

        public static JsonNode DocumentInfo(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();

            var levels = new JsonArray();
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
            {
                levels.Add(new JsonObject
                {
                    ["id"] = l.Id.Value,
                    ["name"] = l.Name,
                    ["elevationFeet"] = Math.Round(l.Elevation, 6),
                    ["elevation"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, l.Elevation, false),
                });
            }

            // One pass over all instances to give Claude a map of what's in the model.
            var counts = new Dictionary<string, int>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model && cat.CategoryType != CategoryType.Annotation) continue;
                counts[cat.Name] = counts.TryGetValue(cat.Name, out var n) ? n + 1 : 1;
            }
            var categories = new JsonObject();
            foreach (var kv in counts.OrderByDescending(kv => kv.Value).Take(80))
                categories[kv.Key] = kv.Value;

            var active = doc.ActiveView;
            var info = doc.ProjectInformation;
            return new JsonObject
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["isFamilyDocument"] = doc.IsFamilyDocument,
                ["isWorkshared"] = doc.IsWorkshared,
                ["isModified"] = doc.IsModified,
                ["displayLengthUnit"] = LabelUtils.GetLabelForUnit(lengthUnit),
                ["internalUnitsNote"] = "Revit API lengths are decimal feet; angles radians; areas sq ft.",
                ["project"] = info == null ? null : new JsonObject
                {
                    ["name"] = info.Name,
                    ["number"] = info.Number,
                    ["client"] = info.ClientName,
                    ["address"] = info.Address,
                },
                ["activeView"] = active == null ? null : new JsonObject
                {
                    ["id"] = active.Id.Value,
                    ["name"] = active.Name,
                    ["type"] = active.ViewType.ToString(),
                    ["level"] = active.GenLevel?.Name,
                },
                ["levels"] = levels,
                ["phases"] = new JsonArray(doc.Phases.Cast<Phase>().Select(p => (JsonNode)p.Name).ToArray()),
                ["elementCountsByCategory"] = categories,
            };
        }

        public static JsonNode Selection(UIApplication app, JsonObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new CommandException("No document is open in Revit.");
            var doc = uidoc.Document;
            var ids = uidoc.Selection.GetElementIds();
            var withParams = Args.Bool(args, "include_parameters");
            var arr = new JsonArray();
            foreach (var id in ids.Take(500))
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                var s = RevitJson.Summary(e);
                if (withParams) s["parameters"] = RevitJson.Parameters(e);
                arr.Add(s);
            }
            return new JsonObject { ["count"] = ids.Count, ["elements"] = arr };
        }

        public static JsonNode QueryElements(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var limit = Math.Clamp(Args.Int(args, "limit", 200), 1, 5000);

            FilteredElementCollector collector;
            var viewName = Args.Str(args, "in_view");
            if (!string.IsNullOrEmpty(viewName))
            {
                var view = viewName.Equals("active", StringComparison.OrdinalIgnoreCase)
                    ? doc.ActiveView
                    : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new CommandException($"View '{viewName}' not found.");
                collector = new FilteredElementCollector(doc, view.Id);
            }
            else collector = new FilteredElementCollector(doc);

            collector = Args.Bool(args, "types_only") ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();

            var categories = Args.Strings(args, "categories").Select(c => Args.ResolveCategory(doc, c).Id).ToList();
            if (categories.Count > 0) collector = collector.WherePasses(new ElementMulticategoryFilter(categories));

            IEnumerable<Element> elements = collector;

            var level = Args.Str(args, "level");
            if (!string.IsNullOrEmpty(level))
                elements = elements.Where(e => doc.GetElement(e.LevelId) is Level l && l.Name.Equals(level, StringComparison.OrdinalIgnoreCase));

            var nameContains = Args.Str(args, "name_contains");
            if (!string.IsNullOrEmpty(nameContains))
                elements = elements.Where(e => NameOf(doc, e).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            if (args["parameter_filters"] is JsonArray filters)
            {
                foreach (var f in filters.OfType<JsonObject>())
                {
                    var pname = Args.Str(f, "name") ?? throw new CommandException("Each parameter filter needs 'name'.");
                    var op = (Args.Str(f, "op") ?? "equals").ToLowerInvariant();
                    var value = f["value"]?.ToString() ?? "";
                    elements = elements.Where(e => Matches(e, pname, op, value));
                }
            }

            var include = Args.Strings(args, "include_parameters");
            var result = new JsonArray();
            var total = 0;
            foreach (var e in elements)
            {
                total++;
                if (result.Count >= limit) continue;
                var s = RevitJson.Summary(e);
                if (include.Count > 0)
                {
                    var values = new JsonObject();
                    foreach (var pn in include)
                    {
                        var p = RevitJson.FindParameter(e, pn) ?? TypeParam(doc, e, pn);
                        values[pn] = p == null ? null : (p.AsValueString() ?? p.AsString() ?? RevitJson.ParameterValue(p)["value"]?.DeepClone()?.ToString());
                    }
                    s["values"] = values;
                }
                result.Add(s);
            }

            return new JsonObject
            {
                ["total"] = total,
                ["returned"] = result.Count,
                ["truncated"] = total > result.Count,
                ["elements"] = result,
            };
        }

        public static JsonNode ElementDetails(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var ids = Args.Ids(args, "ids");
            if (ids.Count == 0) throw new CommandException("'ids' is required (array of element ids).");
            var includeType = Args.Bool(args, "include_type_parameters", true);

            var arr = new JsonArray();
            foreach (var id in ids.Take(50))
            {
                var e = doc.GetElement(id);
                if (e == null) { arr.Add(new JsonObject { ["id"] = id.Value, ["error"] = "not found" }); continue; }

                var obj = RevitJson.Summary(e);
                var loc = RevitJson.Location(e);
                if (loc != null) obj["location"] = loc;
                var bb = e.get_BoundingBox(null);
                if (bb != null) obj["boundingBox"] = JsonConvert.ToNode(bb);
                if (e is FamilyInstance fi)
                {
                    if (fi.Host != null) obj["host"] = RevitJson.Summary(fi.Host);
                    if (fi.Room != null) obj["room"] = fi.Room.Name;
                    if (fi.Space != null) obj["space"] = fi.Space.Name;
                }
                obj["parameters"] = RevitJson.Parameters(e);
                if (includeType && doc.GetElement(e.GetTypeId()) is ElementType type)
                    obj["typeParameters"] = RevitJson.Parameters(type);
                arr.Add(obj);
            }
            return new JsonObject { ["elements"] = arr };
        }

        private static Parameter TypeParam(Document doc, Element e, string name) =>
            doc.GetElement(e.GetTypeId()) is ElementType t ? RevitJson.FindParameter(t, name) : null;

        private static string NameOf(Document doc, Element e)
        {
            var name = RevitJson.Safe(() => e.Name) ?? "";
            if (doc.GetElement(e.GetTypeId()) is ElementType t) name += " " + RevitJson.Safe(() => t.FamilyName) + " " + RevitJson.Safe(() => t.Name);
            return name;
        }

        private static bool Matches(Element e, string pname, string op, string expected)
        {
            var p = RevitJson.FindParameter(e, pname) ?? TypeParam(e.Document, e, pname);
            if (op == "exists") return p != null;
            if (p == null) return op == "missing";
            if (op == "missing") return false;

            var text = p.StorageType switch
            {
                StorageType.String => p.AsString() ?? "",
                StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.ElementId => p.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
                _ => p.AsValueString() ?? "",
            };
            var display = p.AsValueString() ?? text;

            switch (op)
            {
                case "equals": return text.Equals(expected, StringComparison.OrdinalIgnoreCase) || display.Equals(expected, StringComparison.OrdinalIgnoreCase);
                case "not_equals": return !text.Equals(expected, StringComparison.OrdinalIgnoreCase) && !display.Equals(expected, StringComparison.OrdinalIgnoreCase);
                case "contains": return display.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                case "empty": return string.IsNullOrEmpty(text);
                case "not_empty": return !string.IsNullOrEmpty(text);
                case "gt":
                case "lt":
                    if (p.StorageType != StorageType.Double && p.StorageType != StorageType.Integer) return false;
                    if (!double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit)) return false;
                    // Compare in display units so "gt 3000" means 3000 mm in a metric project.
                    var actual = p.StorageType == StorageType.Integer
                        ? p.AsInteger()
                        : UnitUtils.ConvertFromInternalUnits(p.AsDouble(), p.GetUnitTypeId());
                    return op == "gt" ? actual > limit : actual < limit;
                default:
                    throw new CommandException($"Unknown op '{op}'. Use equals, not_equals, contains, empty, not_empty, gt, lt, exists, missing.");
            }
        }
    }
}
