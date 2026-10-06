using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Bridge "list_warnings": the model's Revit warnings grouped by type, with the elements of each and how to fix the
    /// common ones, so "fix the top warnings" starts from data instead of hand-written GetWarnings() code.
    /// { contains?, types (default 20), ids_per_type (default 200) }
    /// </summary>
    internal static class WarningCommands
    {
        private static readonly (string Text, string Fix)[] Fixes =
        {
            ("identical instances in the same place", "Delete the extra copies: saved script delete_duplicate_instances (preview first)."),
            ("Room separation line", "One of each overlapping pair of room separation lines (or the line over a wall) can be deleted."),
            ("Area is not in a properly enclosed region", "Close the area boundary, or delete the area."),
            ("is not in a properly enclosed region", "Close the room's boundary (walls or room separation lines), or delete the room if it is not needed."),
            ("Multiple Rooms are in the same enclosed region", "Delete the duplicate room, or split the region with a room separation line."),
            ("Highlighted walls overlap", "Shorten or remove one of the overlapping walls."),
            ("are joined but do not intersect", "Unjoin them: JoinGeometryUtils.UnjoinGeometry(doc, a, b) in one script (preview first)."),
            ("Elements have duplicate", "Give each element its own value (doors: saved script renumber_doors)."),
            ("is slightly off axis", "Rotate or redraw the element to the axis; usually from imported geometry."),
        };

        public static JsonNode List(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var contains = Args.Str(args, "contains");
            var types = Math.Clamp(Args.Int(args, "types", 20), 1, 200);
            var perType = Math.Clamp(Args.Int(args, "ids_per_type", 200), 1, 5000);

            var warnings = doc.GetWarnings().Select(w => (Message: w, Text: w.GetDescriptionText() ?? "")).ToList();
            var groups = warnings
                .Where(w => contains == null || w.Text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(w => w.Text).OrderByDescending(g => g.Count()).ToList();

            var arr = new JsonArray();
            foreach (var g in groups.Take(types))
            {
                var ids = g.SelectMany(w => w.Message.GetFailingElements()).Select(id => id.Value).Distinct().ToList();
                var categories = ids.Take(500).Select(id => doc.GetElement(new ElementId(id))?.Category?.Name).Where(c => c != null)
                    .GroupBy(c => c).OrderByDescending(c => c.Count()).Take(3).Select(c => (JsonNode)$"{c.Key} ({c.Count()})").ToArray();
                arr.Add(new JsonObject
                {
                    ["warning"] = g.Key,
                    ["count"] = g.Count(),
                    ["elements"] = ids.Count,
                    ["categories"] = new JsonArray(categories),
                    ["fix"] = Fixes.FirstOrDefault(f => g.Key.IndexOf(f.Text, StringComparison.OrdinalIgnoreCase) >= 0).Fix,
                    ["ids"] = new JsonArray(ids.Take(perType).Select(id => (JsonNode)id).ToArray()),
                });
            }
            return new JsonObject
            {
                ["total"] = warnings.Count,
                ["types"] = groups.Count,
                ["warnings"] = arr,
                ["note"] = "Fixes change the model: preview each one (dry_run) and confirm. Show the elements with select_elements.",
            };
        }
    }
}
