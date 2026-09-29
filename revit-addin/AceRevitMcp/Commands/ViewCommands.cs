using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal static class ViewCommands
    {
        /// <summary>
        /// Opens a view in Revit (by name, optionally of a type) and zooms to elements or a room number.
        /// { name: "L3", view_type: "FloorPlan", room: "301", ids: [..] }
        /// </summary>
        public static JsonNode OpenView(UIApplication app, JsonObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new CommandException("No document is open in Revit.");
            var doc = uidoc.Document;
            var name = Args.Str(args, "name");
            var type = Args.Str(args, "view_type");
            var room = Args.Str(args, "room");
            var ids = Args.Ids(args, "ids");
            if (room != null)
            {
                var r = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>().FirstOrDefault(x => x.Number == room && x.Area > 0)
                    ?? throw new CommandException($"No placed room numbered {room}.");
                ids.Add(r.Id);
                if (name == null) name = doc.GetElement(r.LevelId)?.Name;
                type ??= "FloorPlan";
            }
            View view = null;
            if (name != null)
            {
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.CanBePrinted && (type == null || v.ViewType.ToString().Equals(type, StringComparison.OrdinalIgnoreCase))).ToList();
                view = views.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                       ?? views.FirstOrDefault(v => v.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? throw new CommandException($"No view named '{name}'{(type != null ? $" of type {type}" : "")}.");
                // Setting ActiveView directly works from the bridge (outside any transaction) even when Revit's window is not focused.
                if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
            }
            if (ids.Count > 0)
            {
                uidoc.Selection.SetElementIds(ids);
                try { uidoc.ShowElements(ids); } catch { }
            }
            return new JsonObject { ["view"] = uidoc.ActiveView.Name, ["viewType"] = uidoc.ActiveView.ViewType.ToString(), ["selected"] = ids.Count };
        }

        public static JsonNode ListViews(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var typeFilter = Args.Str(args, "view_type");
            var arr = new JsonArray();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate && v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser)
                         .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name))
            {
                if (typeFilter != null && !v.ViewType.ToString().Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var o = new JsonObject { ["id"] = v.Id.Value, ["name"] = v.Name, ["type"] = v.ViewType.ToString() };
                if (v.GenLevel != null) o["level"] = v.GenLevel.Name;
                if (v is ViewSheet sheet) o["sheetNumber"] = sheet.SheetNumber;
                arr.Add(o);
            }
            return new JsonObject { ["count"] = arr.Count, ["views"] = arr };
        }

        /// <summary>Exports a view to PNG and returns it base64-encoded so Claude can look at it.</summary>
        public static JsonNode ExportImage(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            View view;
            if (args["view_id"] is JsonValue idv && idv.TryGetValue<long>(out var vid))
                view = doc.GetElement(new ElementId(vid)) as View ?? throw new CommandException($"No view with id {vid}.");
            else if (Args.Str(args, "view_name") is string vname)
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                           .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(vname, StringComparison.OrdinalIgnoreCase))
                       ?? throw new CommandException($"View '{vname}' not found.");
            else
                view = doc.ActiveView;

            if (view == null || !view.CanBePrinted)
                throw new CommandException("This view cannot be exported as an image.");

            var size = Math.Clamp(Args.Int(args, "pixel_size", 1400), 256, 4000);
            var dir = Path.Combine(Path.GetTempPath(), "ACE-RevitMCP", "images");
            Directory.CreateDirectory(dir);
            var prefix = Path.Combine(dir, $"view_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = prefix,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = size,
            };
            options.SetViewsAndSheets(new[] { view.Id });
            doc.ExportImage(options);

            var file = Directory.GetFiles(dir, Path.GetFileName(prefix) + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                       ?? throw new CommandException("Revit did not produce an image file.");
            var bytes = File.ReadAllBytes(file);
            return new JsonObject
            {
                ["view"] = view.Name,
                ["viewType"] = view.ViewType.ToString(),
                ["path"] = file,
                ["mimeType"] = "image/png",
                ["base64"] = Convert.ToBase64String(bytes),
            };
        }
    }
}
