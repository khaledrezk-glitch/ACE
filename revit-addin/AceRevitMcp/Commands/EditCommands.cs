using System;
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
            var fromPanel = Args.Bool(args, "_fromPanel");
            var hash = Companion.ActivityHub.Fingerprint("set_parameters", args);
            if (!dryRun && !fromPanel && Companion.ActivityHub.Decided(hash, "these parameter values") is { } earlier)
            {
                if (earlier.Rejected) throw new CommandException(earlier.Note);
                return new JsonObject { ["alreadyApplied"] = true, ["applied"] = 0, ["failed"] = 0, ["note"] = earlier.Note };
            }
            if (!dryRun && Companion.ActivityHub.WrongPlace(hash, doc, 0) is string wrong) throw new CommandException(wrong);

            var results = new JsonArray();
            var applied = 0;
            string status = null;
            var recording = ChangeTracker.Begin("Claude: set parameters");
            using (var guard = new ModelGuard(app))
            {
                // Commit inside a group (so Revit's own checks run, exactly as in a real change), then roll the group
                // back for a preview or keep it as one undo step.
                using (var group = new TransactionGroup(doc, "Claude: set parameters"))
                using (var t = new Transaction(doc, "Claude: set parameters"))
                {
                    group.Start();
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
                    var kept = false;
                    try
                    {
                        var committed = t.Commit();
                        if (committed != TransactionStatus.Committed) { group.RollBack(); status = committed.ToString(); applied = 0; }
                        else if (dryRun) status = group.RollBack().ToString();
                        else { status = group.Assimilate().ToString(); kept = status == nameof(TransactionStatus.Committed); }
                    }
                    finally
                    {
                        if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                        ChangeTracker.End(recording, kept);
                    }
                    if (applied == 0 && status != nameof(TransactionStatus.Committed) && status != nameof(TransactionStatus.RolledBack))
                        foreach (var r in results.OfType<JsonObject>().Where(r => r["ok"]?.GetValue<bool>() == true))
                        { r["ok"] = false; r["error"] = "Revit refused the change when committing (see revitErrors); nothing was changed."; }
                }

                var response = new JsonObject
                {
                    ["success"] = status == nameof(TransactionStatus.Committed) || (dryRun && status == nameof(TransactionStatus.RolledBack)),
                    ["applied"] = applied,
                    ["failed"] = results.Count - applied,
                    ["transaction"] = status,
                    ["dryRun"] = dryRun,
                    ["results"] = results,
                };
                if (!dryRun && applied > 0 && status == nameof(TransactionStatus.Committed)) response["note"] = "Applied as ONE undo step named 'Claude: set parameters'.";
                if (response["success"]?.GetValue<bool>() == false) response["note"] = "Revit rolled the change back when committing it (see revitErrors). Nothing was changed.";
                if (dryRun && applied > 0 && applied == results.Count) Companion.ActivityHub.AddPending("set_parameters", args, response, doc);
                else if (!dryRun && !fromPanel && status == nameof(TransactionStatus.Committed)) Companion.ActivityHub.MarkAppliedByClaude(hash);
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
