using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Converts arbitrary script results (Revit objects, anonymous types, collections) into JSON
    /// without ever throwing: Revit objects have many properties that throw when read.
    /// </summary>
    internal static class JsonConvert
    {
        private const int MaxDepth = 6;
        private const int MaxItems = 5000;

        public static JsonNode ToNode(object value, int depth = 0)
        {
            switch (value)
            {
                case null: return null;
                case JsonNode node: return node.DeepClone();
                case string s: return JsonValue.Create(s);
                case bool b: return JsonValue.Create(b);
                case char c: return JsonValue.Create(c.ToString());
                case int or long or short or byte or uint or ulong or ushort or sbyte:
                    return JsonValue.Create(Convert.ToInt64(value));
                case double d: return double.IsFinite(d) ? JsonValue.Create(Math.Round(d, 9)) : JsonValue.Create(d.ToString());
                case float f: return JsonValue.Create((double)f);
                case decimal m: return JsonValue.Create(m);
                case Enum e: return JsonValue.Create(e.ToString());
                case DateTime dt: return JsonValue.Create(dt.ToString("o"));
                case Guid g: return JsonValue.Create(g.ToString());
                case ElementId id: return JsonValue.Create(id.Value);
                case XYZ p: return new JsonArray(Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9));
                case UV uv: return new JsonArray(Math.Round(uv.U, 9), Math.Round(uv.V, 9));
                case Element el: return RevitJson.Summary(el);
                case Parameter prm: return RevitJson.ParameterValue(prm);
                case BoundingBoxXYZ bb: return new JsonObject { ["min"] = ToNode(bb.Min), ["max"] = ToNode(bb.Max) };
                case Curve curve:
                    try
                    {
                        return new JsonObject
                        {
                            ["type"] = curve.GetType().Name,
                            ["start"] = ToNode(curve.GetEndPoint(0)),
                            ["end"] = ToNode(curve.GetEndPoint(1)),
                            ["length"] = Math.Round(curve.Length, 9),
                        };
                    }
                    catch { return JsonValue.Create(curve.GetType().Name); }
                case Category cat: return new JsonObject { ["id"] = cat.Id.Value, ["name"] = cat.Name };
            }

            if (depth >= MaxDepth) return JsonValue.Create(value.ToString());

            if (value is IDictionary dict)
            {
                var obj = new JsonObject();
                foreach (DictionaryEntry entry in dict)
                {
                    var key = entry.Key?.ToString() ?? "null";
                    if (!obj.ContainsKey(key)) obj[key] = ToNode(entry.Value, depth + 1);
                }
                return obj;
            }

            if (value is IEnumerable enumerable)
            {
                var arr = new JsonArray();
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (++count > MaxItems) { arr.Add($"... truncated after {MaxItems} items"); break; }
                    arr.Add(ToNode(item, depth + 1));
                }
                return arr;
            }

            var type = value.GetType();
            // Anonymous types, records, tuples and simple classes: read public properties/fields.
            if (type.IsValueType && type.FullName?.StartsWith("System.ValueTuple") == true)
            {
                var tuple = new JsonObject();
                foreach (var f in type.GetFields())
                    tuple[f.Name] = ToNode(f.GetValue(value), depth + 1);
                return tuple;
            }

            if (type.Namespace?.StartsWith("Autodesk") == true)
                return JsonValue.Create(value.ToString()); // unknown Revit object: avoid reflecting throwing props

            var result = new JsonObject();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                try { result[prop.Name] = ToNode(prop.GetValue(value), depth + 1); }
                catch (Exception ex) { result[prop.Name] = $"<error: {ex.GetBaseException().Message}>"; }
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { result[field.Name] = ToNode(field.GetValue(value), depth + 1); }
                catch { }
            }
            return result.Count > 0 ? result : JsonValue.Create(value.ToString());
        }
    }
}
