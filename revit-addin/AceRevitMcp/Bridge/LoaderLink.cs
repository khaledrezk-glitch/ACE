using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Bridge
{
    /// <summary>
    /// The bridge side of the ACE loader: reload_addin and the loader status in /health. Kept out of PayloadEntry so that
    /// class names no System.Text.Json type (on Revit 2024 the resolver for it is registered in PayloadEntry.Start).
    /// </summary>
    internal static class LoaderLink
    {
        /// <summary>Bridge "reload_addin": { force } -> schedules the swap to the installed build (the loader does it when idle).</summary>
        internal static JsonNode ReloadCommand(UIApplication uiapp, JsonObject args)
        {
            var request = PayloadEntry.Get<Func<bool, IDictionary<string, object>>>("requestReload");
            if (request == null)
                throw new CommandException("This ACE was loaded directly by its .addin manifest (no ACE loader), so it can't be swapped while Revit runs. " +
                                               "Close Revit and start it again to load a new build; install.ps1 installs the loader, after which builds load without a restart.");
            return ToJson(request(Commands.Args.Bool(args, "force")));
        }

        /// <summary>The loader's view for /health: loaderVersion, payloadVersion, previous, pending, lastReload (null when loaded directly).</summary>
        internal static JsonNode LoaderStatus()
        {
            var status = PayloadEntry.Get<Func<IDictionary<string, object>>>("status");
            if (status == null) return null;
            try { return ToJson(status()); }
            catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
        }

        private static JsonNode ToJson(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return JsonValue.Create(s);
                case bool b: return JsonValue.Create(b);
                case int i: return JsonValue.Create(i);
                case long l: return JsonValue.Create(l);
                case double x: return JsonValue.Create(x);
                case IDictionary<string, object> map:
                    var o = new JsonObject();
                    foreach (var kv in map) o[kv.Key] = ToJson(kv.Value);
                    return o;
                case IEnumerable list:
                    var a = new JsonArray();
                    foreach (var item in list) a.Add(ToJson(item));
                    return a;
                default: return JsonValue.Create(value.ToString());
            }
        }
    }
}
