using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal static class Args
    {
        public static Document RequireDoc(UIApplication app) =>
            app.ActiveUIDocument?.Document ?? throw new CommandException("No document is open in Revit. Open a model first.");

        public static string Str(JsonObject a, string name) =>
            a[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        public static int Int(JsonObject a, string name, int fallback) =>
            a[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

        public static bool Bool(JsonObject a, string name, bool fallback = false) =>
            a[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        public static List<string> Strings(JsonObject a, string name)
        {
            return a[name] switch
            {
                JsonArray arr => arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => new List<string> { s },
                _ => new List<string>(),
            };
        }

        public static List<ElementId> Ids(JsonObject a, string name)
        {
            if (a[name] is not JsonArray arr) return new List<ElementId>();
            return arr.Select(x => new ElementId(x.GetValue<long>())).ToList();
        }

        /// <summary>Accepts "Walls", "OST_Walls" or a localized category name.</summary>
        public static Category ResolveCategory(Document doc, string name)
        {
            var bicName = name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? name : "OST_" + name.Replace(" ", "");
            if (Enum.TryParse<BuiltInCategory>(bicName, true, out var bic))
            {
                var byBic = Category.GetCategory(doc, bic);
                if (byBic != null) return byBic;
            }
            foreach (Category c in doc.Settings.Categories)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
                foreach (Category sub in c.SubCategories)
                    if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase)) return sub;
            }
            throw new CommandException($"Unknown category '{name}'. Use names like Walls, Doors, Rooms, StructuralColumns or OST_ names.");
        }
    }
}
