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
    internal static class EditCommands
    {
        /// <summary>
        /// { "changes": [ { "id": 123, "parameter": "Comments", "value": "x", "target": "instance|type" } ], "dry_run": false }
        /// Numeric values are internal units (feet); string values for length/area parameters are parsed
        /// in project display units (e.g. "1200" in a mm project, or "1200 mm", "4' 6\"").
        /// </summary>
        public static JsonNode SetParameters(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            if (args["changes"] is not JsonArray changes || changes.Count == 0)
                throw new CommandException("'changes' is required: [{id, parameter, value}].");
            var dryRun = Args.Bool(args, "dry_run");

            var results = new JsonArray();
            var applied = 0;
            string status = null;
            var recording = ChangeTracker.Begin("Claude: set parameters");
            using (var guard = new ModelGuard(app))
            {
                using (var t = new Transaction(doc, "Claude: set parameters"))
                {
                    t.Start();
                    foreach (var c in changes.OfType<JsonObject>())
                    {
                        var r = new JsonObject { ["id"] = c["id"]?.DeepClone(), ["parameter"] = c["parameter"]?.DeepClone() };
                        try
                        {
                            var e = doc.GetElement(new ElementId(c["id"]!.GetValue<long>())) ?? throw new CommandException("element not found");
                            if ((Args.Str(c, "target") ?? "instance") == "type")
                                e = doc.GetElement(e.GetTypeId()) ?? throw new CommandException("element has no type");
                            var name = Args.Str(c, "parameter") ?? throw new CommandException("'parameter' missing");
                            var p = RevitJson.FindParameter(e, name) ?? throw new CommandException($"parameter '{name}' not found on element {e.Id.Value}");
                            if (p.IsReadOnly) throw new CommandException($"parameter '{name}' is read-only");
                            var before = p.AsValueString() ?? p.AsString();
                            Assign(p, c["value"]);
                            r["before"] = before;
                            r["after"] = p.AsValueString() ?? p.AsString();
                            r["ok"] = true;
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            r["ok"] = false;
                            r["error"] = ex.Message;
                        }
                        results.Add(r);
                    }
                    try { status = (dryRun ? t.RollBack() : t.Commit()).ToString(); }
                    finally { ChangeTracker.End(recording, !dryRun && status == nameof(TransactionStatus.Committed)); }
                }

                var response = new JsonObject
                {
                    ["applied"] = applied,
                    ["failed"] = results.Count - applied,
                    ["transaction"] = status,
                    ["dryRun"] = dryRun,
                    ["results"] = results,
                };
                if (!dryRun && applied > 0) response["note"] = "Applied as ONE undo step named 'Claude: set parameters'.";
                if (guard.Warnings.Count > 0) response["revitWarnings"] = new JsonArray(guard.Warnings.Select(w => (JsonNode)w).ToArray());
                if (guard.Errors.Count > 0) response["revitErrors"] = new JsonArray(guard.Errors.Select(w => (JsonNode)w).ToArray());
                return response;
            }
        }

        private static void Assign(Parameter p, JsonNode value)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(value?.ToString() ?? "");
                    return;
                case StorageType.Integer:
                    if (value is JsonValue b && b.TryGetValue<bool>(out var flag)) { p.Set(flag ? 1 : 0); return; }
                    if (value is JsonValue iv && iv.TryGetValue<int>(out var i)) { p.Set(i); return; }
                    var s = value?.ToString() ?? "";
                    if (s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase)) { p.Set(1); return; }
                    if (s.Equals("no", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)) { p.Set(0); return; }
                    if (!p.SetValueString(s)) throw new CommandException($"could not parse '{s}' as integer");
                    return;
                case StorageType.Double:
                    if (value is JsonValue dv && dv.TryGetValue<double>(out var d)) { p.Set(d); return; }
                    var text = value?.ToString() ?? "";
                    if (!p.SetValueString(text)) throw new CommandException($"could not parse '{text}' (uses project display units)");
                    return;
                case StorageType.ElementId:
                    p.Set(new ElementId(long.Parse(value!.ToString(), CultureInfo.InvariantCulture)));
                    return;
                default:
                    throw new CommandException("parameter has no value storage");
            }
        }

        public static JsonNode SelectElements(UIApplication app, JsonObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new CommandException("No document is open in Revit.");
            var ids = Args.Ids(args, "ids").Where(id => uidoc.Document.GetElement(id) != null).ToList();
            uidoc.Selection.SetElementIds(ids);
            if (ids.Count > 0 && Args.Bool(args, "zoom", true))
            {
                try { uidoc.ShowElements(ids); } catch { /* some elements are not visible in any view */ }
            }
            return new JsonObject { ["selected"] = ids.Count };
        }
    }
}
