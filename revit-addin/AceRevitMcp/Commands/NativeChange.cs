using System;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The one way a native command changes the model, exactly like a script run: check_only reads only; dry_run makes the
    /// change inside a transaction group and rolls it back (an Apply card in the Companion); the identical call applies
    /// it as ONE undo step, never twice, never in another model than the preview, and never after the user cancelled it.
    /// </summary>
    internal static class NativeChange
    {
        /// <param name="kind">The bridge command name (the change's identity for previews and Apply cards).</param>
        /// <param name="title">The undo step and card title, e.g. "ACE: presentation standard".</param>
        /// <param name="body">Does the work; plan = true means look only (check_only): change and create nothing.</param>
        /// <param name="rules">
        /// When the change depends on rules read from a file (not in the request): their fingerprint. It becomes part of the
        /// change's identity, so an apply after the rules changed is refused: the preview showed other rules.
        /// </param>
        public static JsonObject Run(UIApplication app, JsonObject args, string kind, string title, Func<Document, bool, JsonObject> body, string rules = null)
        {
            var doc = Args.RequireDoc(app);
            if (rules != null)
            {
                if (args["_rules"] is JsonValue given && given.ToString() != rules && !Args.Bool(args, "dry_run"))
                    return new JsonObject { ["success"] = false, ["stage"] = "rejected", ["error"] = "The standard changed since this was previewed. Nothing was changed. Preview it again." };
                args = (JsonObject)args.DeepClone();
                args["_rules"] = rules;
            }
            if (Args.Bool(args, "check_only"))
            {
                var plan = body(doc, true);
                plan["checkOnly"] = true;
                plan["note"] ??= "Nothing was changed. To apply: preview with dry_run: true, then confirm.";
                return plan;
            }

            var dryRun = Args.Bool(args, "dry_run");
            var fromPanel = Args.Bool(args, "_fromPanel");
            var hash = Companion.ActivityHub.Fingerprint(kind, args);
            if (!dryRun && !fromPanel && Companion.ActivityHub.Decided(hash, $"this change ({title})") is { } earlier)
                return earlier.Rejected
                    ? new JsonObject { ["success"] = false, ["stage"] = "rejected", ["error"] = earlier.Note }
                    : new JsonObject { ["success"] = true, ["alreadyApplied"] = true, ["note"] = earlier.Note };
            if (!dryRun && Companion.ActivityHub.WrongPlace(hash, doc, 0) is string wrong)
                return new JsonObject { ["success"] = false, ["stage"] = "wrong_model", ["error"] = wrong };
            if (!dryRun && !fromPanel && rules != null && !Companion.ActivityHub.HasPreview(hash))
                return new JsonObject { ["success"] = false, ["stage"] = "rejected", ["error"] = "No preview of exactly this change is waiting: the standard may have changed since the preview, or Revit was restarted. Nothing was changed. Preview it again." };

            JsonObject result;
            string status = null;
            var recording = ChangeTracker.Begin(title);
            using (var guard = new ModelGuard(app))
            using (var group = new TransactionGroup(doc, title))
            using (var t = new Transaction(doc, title))
            {
                var kept = false;
                try
                {
                    group.Start();
                    t.Start();
                    result = body(doc, false);
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
                var summary = recording.Summary();
                result[dryRun ? "wouldChange" : "changed"] = summary;
                if (guard.Warnings.Count > 0) result["revitWarnings"] = new JsonArray(guard.Warnings.Distinct().Take(20).Select(w => (JsonNode)w).ToArray());
                if (guard.Errors.Count > 0) result["revitErrors"] = new JsonArray(guard.Errors.Distinct().Take(20).Select(w => (JsonNode)w).ToArray());
                if (guard.Dialogs.Count > 0) result["dialogs"] = new JsonArray(guard.Dialogs.Take(10).Select(w => (JsonNode)w).ToArray());
                result["note"] = !ok ? "Revit rolled the change back (see revitErrors). Nothing was changed."
                    : dryRun ? "Preview only: the model is unchanged. Apply it from the Apply card in the ACE panel, or confirm to Claude."
                    : $"Applied as ONE undo step named '{title}'.";
                var anything = (summary?["modified"]?.ToString() ?? "0") + (summary?["added"]?.ToString() ?? "0") + (summary?["deleted"]?.ToString() ?? "0") != "000";
                if (ok && dryRun && anything && !fromPanel)   // a preview in an ACE window is applied there, not from a card
                {
                    // An Apply card in the Companion, titled for people (the title is not part of the change's identity).
                    var cardArgs = (JsonObject)args.DeepClone();
                    cardArgs["transaction_name"] = "Claude: " + title.Replace("ACE: ", "");
                    Companion.ActivityHub.AddPending(kind, cardArgs, result, doc);
                }
                else if (ok && !dryRun && !fromPanel) Companion.ActivityHub.MarkAppliedByClaude(hash);
            }
            return result;
        }
    }
}
