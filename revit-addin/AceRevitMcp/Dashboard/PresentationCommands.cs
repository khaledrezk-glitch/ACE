using System;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Bridge "presentation_standard": check or apply the office presentation standard (text height by view scale, one
    /// type per size and kind). { check_only, dry_run, views: [names], text_sizes: [...], unify_tags }. Like every change:
    /// a preview first (an Apply card in the Companion), then the identical call applies it as ONE undo step.
    /// </summary>
    internal static class PresentationCommands
    {
        public const string Name = "ACE: presentation standard";

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var std = PresentationStandard.Load(args["text_sizes"] as JsonArray);
            if (args["unify_tags"] is JsonValue ut && ut.TryGetValue<bool>(out var unify)) std.UnifyTagTypes = unify;
            var names = Args.Strings(args, "views").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = new PresentationFix.Options { ViewNames = names };

            if (Args.Bool(args, "check_only"))
            {
                options.PlanOnly = true;
                var plan = PresentationFix.Run(doc, std, options);
                plan["checkOnly"] = true;
                plan["note"] = "Nothing was changed. To apply: preview with dry_run: true, then confirm.";
                return plan;
            }

            var dryRun = Args.Bool(args, "dry_run");
            var fromPanel = Args.Bool(args, "_fromPanel");
            var hash = Companion.ActivityHub.Fingerprint("presentation_standard", args);
            if (!dryRun && !fromPanel && Companion.ActivityHub.Decided(hash, "this presentation standard change") is { } earlier)
                return earlier.Rejected
                    ? new JsonObject { ["success"] = false, ["stage"] = "rejected", ["error"] = earlier.Note }
                    : new JsonObject { ["success"] = true, ["alreadyApplied"] = true, ["note"] = earlier.Note };
            if (!dryRun && Companion.ActivityHub.WrongPlace(hash, doc, 0) is string wrong)
                return new JsonObject { ["success"] = false, ["stage"] = "wrong_model", ["error"] = wrong };

            JsonObject result;
            string status;
            var recording = ChangeTracker.Begin(Name);
            using (var guard = new ModelGuard(app))
            using (var group = new TransactionGroup(doc, Name))
            using (var t = new Transaction(doc, Name))
            {
                var kept = false;
                try
                {
                    group.Start();
                    t.Start();
                    result = PresentationFix.Run(doc, std, options);
                    var committed = t.Commit();
                    if (committed != TransactionStatus.Committed) { group.RollBack(); status = committed.ToString(); }
                    else if (dryRun) status = group.RollBack().ToString();
                    else { status = group.Assimilate().ToString(); kept = status == nameof(TransactionStatus.Committed); }
                }
                finally
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    ChangeTracker.End(recording, kept);
                }
                var ok = status == nameof(TransactionStatus.Committed) || (dryRun && status == nameof(TransactionStatus.RolledBack));
                result["success"] = ok;
                result["transaction"] = status;
                result["dryRun"] = dryRun;
                result[dryRun ? "wouldChange" : "changed"] = recording.Summary();
                if (guard.Warnings.Count > 0) result["revitWarnings"] = new JsonArray(guard.Warnings.Distinct().Take(20).Select(w => (JsonNode)w).ToArray());
                if (guard.Errors.Count > 0) result["revitErrors"] = new JsonArray(guard.Errors.Distinct().Take(20).Select(w => (JsonNode)w).ToArray());
                result["note"] = !ok ? "Revit rolled the change back (see revitErrors). Nothing was changed."
                    : dryRun ? "Preview only: the model is unchanged. Apply it from the Apply card in the ACE panel, or confirm to Claude."
                    : $"Applied as ONE undo step named '{Name}'.";
                var summary = result[dryRun ? "wouldChange" : "changed"] as JsonObject;
                var anything = summary != null && (summary["modified"]?.ToString() ?? "0") + (summary["added"]?.ToString() ?? "0") != "00";
                if (ok && dryRun && anything)
                {
                    // An Apply card in the Companion, titled for people (the title is not part of the change's identity).
                    var cardArgs = (JsonObject)args.DeepClone();
                    cardArgs["transaction_name"] = "Claude: presentation standard";
                    Companion.ActivityHub.AddPending("presentation_standard", cardArgs, result, doc);
                }
                else if (ok && !dryRun && !fromPanel) Companion.ActivityHub.MarkAppliedByClaude(hash);
            }
            return result;
        }
    }
}
