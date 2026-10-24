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
            var names = WorksetRules.ModelNames(app.ActiveUIDocument.Document);   // a rule's models key (G9)
            var rules = args["rules"] is JsonArray given
                ? WorksetRules.Parse(given, Args.Str(args, "default_workset"), names)
                : WorksetRules.Load(ProjectRules.For(app.ActiveUIDocument.Document), names) ?? throw new Bridge.CommandException("No workset rules found. Put them in the project's project-requirements.json (section \"worksets\") or the office worksets.json (see bep/worksets.example.json), or pass rules.");
            if (args["rules"] is JsonArray && args["rules_name"] is JsonValue) { rules.Name = Args.Str(args, "rules_name"); rules.Source = Args.Str(args, "rules_source"); }
            if (rules.Items.Count == 0) throw new Bridge.CommandException(rules.Skipped.Count > 0
                ? "No workset rule applies to this model. " + RuleModels.SkippedText(rules.Skipped, app.ActiveUIDocument.Document.Title, rules.Total)
                : "The workset rules are empty: each rule needs at least a 'workset'.");
            if (Args.Str(args, "default_workset") is string dw && !string.IsNullOrWhiteSpace(dw)) rules.DefaultWorkset = dw;
            var title = "ACE: assign worksets per the BEP" + (Args.Bool(args, "only_workset1") ? " (Workset1 only)" : "");
            var dryRun = Args.Bool(args, "dry_run");
            var result = NativeChange.Run(app, args, "assign_worksets", title, (doc, plan) => rules.Apply(doc, new WorksetRules.Options
            {
                PlanOnly = plan,
                OnlyWorkset1 = Args.Bool(args, "only_workset1"),
                CreateMissing = Args.Bool(args, "create_missing"),
                CanEdit = e => !doc.IsWorkshared || (WorksharingUtils.GetCheckoutStatus(doc, e.Id) != CheckoutStatus.OwnedByOtherUser
                               && WorksharingUtils.GetModelUpdatesStatus(doc, e.Id) is not (ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral)),
            }), verify: (d, r) =>
            {
                // The after-check (register #37): read after the commit; it reports, it never rolls back.
                try { r["afterCheck"] = rules.AfterCheck(d, dryRun); }
                catch (System.Exception ex) { r["afterCheck"] = new JsonObject { ["ok"] = false, ["error"] = "the after-check could not read the model: " + ex.Message, ["targets"] = new JsonArray() }; }
                return true;
            });
            if (result["notes"] == null) result["notes"] = new JsonArray();
            Util.DeletionProbe.CheckPreview(app.ActiveUIDocument.Document, result, dryRun);   // the deletion line (#40, #42)
            WorksetPlanLogic.MarkComplete(result, dryRun);
            return result;
        }
    }
}
