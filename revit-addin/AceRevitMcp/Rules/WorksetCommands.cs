using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "assign_worksets": { rules?, default_workset?, only_workset1, create_missing, check_only, dry_run }. Rules
    /// given in the request win; otherwise the office / project rules file. Previewed and applied like every change.
    /// </summary>
    internal static class WorksetCommands
    {
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var rules = args["rules"] is JsonArray given
                ? WorksetRules.Parse(given, Args.Str(args, "default_workset"))
                : WorksetRules.LoadOffice() ?? throw new Bridge.CommandException("No workset rules found. Save the BEP rules as %APPDATA%\\ACE-RevitMCP\\worksets.json (see bep/worksets.example.json), or pass rules.");
            if (args["rules"] is JsonArray && args["rules_name"] is JsonValue) { rules.Name = Args.Str(args, "rules_name"); rules.Source = Args.Str(args, "rules_source"); }
            if (rules.Items.Count == 0) throw new Bridge.CommandException("The workset rules are empty: each rule needs at least a 'workset'.");
            if (Args.Str(args, "default_workset") is string dw && !string.IsNullOrWhiteSpace(dw)) rules.DefaultWorkset = dw;
            var title = "ACE: assign worksets per the BEP" + (Args.Bool(args, "only_workset1") ? " (Workset1 only)" : "");
            return NativeChange.Run(app, args, "assign_worksets", title, (doc, plan) => rules.Apply(doc, new WorksetRules.Options
            {
                PlanOnly = plan,
                OnlyWorkset1 = Args.Bool(args, "only_workset1"),
                CreateMissing = Args.Bool(args, "create_missing"),
                CanEdit = e => !doc.IsWorkshared || (WorksharingUtils.GetCheckoutStatus(doc, e.Id) != CheckoutStatus.OwnedByOtherUser
                               && WorksharingUtils.GetModelUpdatesStatus(doc, e.Id) is not (ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral)),
            }));
        }
    }
}
