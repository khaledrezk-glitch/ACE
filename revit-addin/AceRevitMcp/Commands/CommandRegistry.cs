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
                ["open_view"] = ViewCommands.OpenView,
                ["execute_code"] = CodeRunner.Run,
                ["backup_model"] = BackupCommands.Backup,
                ["undo_last_claude_change"] = UndoCommands.UndoLast,
                ["api_lookup"] = InsightCommands.ApiLookup,
                ["describe_category"] = InsightCommands.DescribeCategory,
                ["list_types"] = InsightCommands.ListTypes,
                ["get_model_insights"] = Dashboard.DashboardCommands.Insights,
                ["model_brief"] = BriefCommands.ModelBrief,
                ["describe_family"] = BriefCommands.DescribeFamily,
                ["snapshot_model"] = Tracking.ChangeTracking.SnapshotCommand,
                ["list_snapshots"] = Tracking.ChangeTracking.ListCommand,
                ["model_changes"] = Tracking.ChangeTracking.ChangesCommand,
                ["run_clash_test"] = Coordination.ClashCommands.Run,
                ["set_clash_status"] = Coordination.ClashCommands.SetStatus,
                ["coordination_sources"] = Coordination.ClashCommands.Sources,
                ["pending_changes"] = Companion.ActivityHub.PendingCommand,
                ["coordination_report"] = Coordination.CoordinationReport.Build,
            };

        public JsonNode Execute(string command, UIApplication app, JsonObject args)
        {
            if (!_commands.TryGetValue(command, out var handler))
                throw new CommandException($"Unknown command '{command}'. Known: {string.Join(", ", _commands.Keys)}");
            return handler(app, args);
        }
    }
}
