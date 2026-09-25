using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Scripting;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal sealed class CommandRegistry
    {
        private readonly Dictionary<string, Func<UIApplication, JsonObject, JsonNode>> _commands =
            new Dictionary<string, Func<UIApplication, JsonObject, JsonNode>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ping"] = ModelCommands.Ping,
                ["get_document_info"] = ModelCommands.DocumentInfo,
                ["get_selection"] = ModelCommands.Selection,
                ["query_elements"] = ModelCommands.QueryElements,
                ["get_element_details"] = ModelCommands.ElementDetails,
                ["set_parameters"] = EditCommands.SetParameters,
                ["select_elements"] = EditCommands.SelectElements,
                ["export_view_image"] = ViewCommands.ExportImage,
                ["list_views"] = ViewCommands.ListViews,
                ["execute_code"] = CodeRunner.Run,
                ["backup_model"] = BackupCommands.Backup,
                ["undo_last_claude_change"] = UndoCommands.UndoLast,
            };

        public JsonNode Execute(string command, UIApplication app, JsonObject args)
        {
            if (!_commands.TryGetValue(command, out var handler))
                throw new CommandException($"Unknown command '{command}'. Known: {string.Join(", ", _commands.Keys)}");
            return handler(app, args);
        }
    }
}
