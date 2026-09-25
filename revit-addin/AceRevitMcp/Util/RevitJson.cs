using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Util
{
    /// <summary>Consistent JSON shapes for elements and parameters.</summary>
    internal static class RevitJson
    {
        public static JsonObject Summary(Element e)
        {
            var obj = new JsonObject
            {
                ["id"] = e.Id.Value,
                ["name"] = Safe(() => e.Name),
                ["category"] = Safe(() => e.Category?.Name),
                ["class"] = e.GetType().Name,
            };

            var doc = e.Document;
            var typeId = Safe(() => e.GetTypeId());
            if (typeId != null && typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is ElementType type)
            {
                obj["typeId"] = type.Id.Value;
                obj["typeName"] = Safe(() => type.Name);
                obj["familyName"] = Safe(() => type.FamilyName);
            }

            var levelId = Safe(() => e.LevelId);
            if (levelId != null && levelId != ElementId.InvalidElementId && doc.GetElement(levelId) is Level lvl)
                obj["level"] = lvl.Name;

            return obj;
        }

        public static JsonObject Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    var point = new JsonObject { ["point"] = JsonConvert.ToNode(lp.Point) };
                    try { point["rotationRadians"] = Math.Round(lp.Rotation, 9); } catch { }
                    return point;
                case LocationCurve lc:
                    return new JsonObject { ["curve"] = JsonConvert.ToNode(lc.Curve) };
                default:
                    return null;
            }
        }

        public static JsonObject ParameterValue(Parameter p)
        {
            var obj = new JsonObject
            {
                ["name"] = p.Definition?.Name,
                ["storage"] = p.StorageType.ToString(),
                ["readOnly"] = p.IsReadOnly,
            };
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: obj["value"] = p.AsString(); break;
                    case StorageType.Integer: obj["value"] = p.AsInteger(); break;
                    case StorageType.Double: obj["value"] = Math.Round(p.AsDouble(), 9); break;
                    case StorageType.ElementId: obj["value"] = p.AsElementId()?.Value; break;
                }
                var display = p.AsValueString();
                if (!string.IsNullOrEmpty(display)) obj["display"] = display;
            }
            catch (Exception ex) { obj["error"] = ex.Message; }
            if (p.IsShared) obj["shared"] = true;
            return obj;
        }

        public static JsonArray Parameters(Element e, ICollection<string> onlyNames = null)
        {
            var arr = new JsonArray();
            foreach (var p in e.Parameters.Cast<Parameter>().OrderBy(p => p.Definition?.Name))
            {
                if (p.Definition == null) continue;
                if (onlyNames != null && !onlyNames.Contains(p.Definition.Name)) continue;
                arr.Add(ParameterValue(p));
            }
            return arr;
        }

        /// <summary>Finds a parameter by display name (instance first), or by BuiltInParameter name like "ALL_MODEL_MARK".</summary>
        public static Parameter FindParameter(Element e, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var p = e.LookupParameter(name);
            if (p != null) return p;
            if (Enum.TryParse<BuiltInParameter>(name, true, out var bip))
                return e.get_Parameter(bip);
            return null;
        }

        public static T Safe<T>(Func<T> f)
        {
            try { return f(); } catch { return default; }
        }
    }
}
