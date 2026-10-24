using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal static class UndoCommands
    {
        /// <summary>
        /// Undoes Claude's most recent change, but ONLY if it is still the latest change in the model.
        /// If the user did anything afterwards, it refuses rather than undoing the user's work.
        /// </summary>
        public static JsonNode UndoLast(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            if (!ChangeTracker.LastChangeWasClaude || ChangeTracker.LastClaudeChangeDocument != doc.Title)
                throw new CommandException(
                    "Nothing undone: the latest change in this model was not made by Claude (or it was already undone). " +
                    "To protect your own work, only Claude's latest change can be undone automatically. Use Ctrl+Z in Revit if needed.");

            var undo = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (undo == null || !app.CanPostCommand(undo))
                throw new CommandException("Revit is not accepting an Undo right now. Press Ctrl+Z in Revit instead.");

            var name = ChangeTracker.LastClaudeChangeName;
            app.PostCommand(undo); // runs right after this call returns
            ChangeTracker.MarkUndone();
            return new JsonObject
            {
                ["undoRequested"] = name,
                ["note"] = "Revit performs the undo immediately after this call. Re-query the model to confirm.",
            };
        }
    }
}
