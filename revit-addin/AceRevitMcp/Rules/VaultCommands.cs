using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Ribbon Audit &gt; Vault and Master Report (backlog V5). Vault opens the project's ACE Vault (its index.html) and,
    /// once per vault, asks whether ACE may save audit records there (decision D21: the same trust list Claude's vault
    /// tool uses). Master Report opens the model's latest report, or says how to make one; without the vault it shows
    /// the summary kept in the model.
    /// </summary>
    internal static class VaultUi
    {
        public const string Title = "ACE Vault";

        internal static string VaultFor(Document doc, out string problem)
        {
            problem = null;
            try { return Compliance.VaultOf(ProjectRules.For(doc)); }
            catch (InvalidDataException ex) { problem = ex.Message; return null; }
        }

        internal static void Trust(string vault)
        {
            var f = Path.Combine(AceConfig.Directory, "trusted-vaults.json");
            JsonArray list;
            try { list = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonArray ?? new JsonArray() : new JsonArray(); }
            catch { list = new JsonArray(); }
            list.Add(Path.GetFullPath(vault));
            Directory.CreateDirectory(AceConfig.Directory);
            File.WriteAllText(f, list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>The report file name prefix the server uses (lib/report.js safe()): the model title without .rvt.</summary>
        internal static string ReportPrefix(string model)
        {
            var name = System.Text.RegularExpressions.Regex.Replace(model ?? "model", @"\.rvt$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = System.Text.RegularExpressions.Regex.Replace(name, "[\\\\/:*?\"<>|]+", "_").Trim();
            return (name.Length == 0 ? "model" : name) + "_audit_";
        }

        internal static string LatestReport(string vault, string model)
        {
            var dir = Path.Combine(vault, "04 Audits");
            if (!Directory.Exists(dir)) return null;
            var prefix = ReportPrefix(model);
            return Directory.GetFiles(dir, "*.html").Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).LastOrDefault();
        }

        internal static void Open(string file) => Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });

        /// <summary>A dialog that offers a prompt for Claude and copies it.</summary>
        internal static void AskClaude(string title, string text, string prompt)
        {
            var td = new TaskDialog(title) { MainInstruction = text, MainContent = $"Ask Claude:\n\"{prompt}\"" };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Copy the prompt");
            td.CommonButtons = TaskDialogCommonButtons.Close;
            if (td.Show() == TaskDialogResult.CommandLink1)
                try { System.Windows.Clipboard.SetText(prompt); } catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class VaultCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(VaultUi.Title, "Open a model first."); return Result.Cancelled; }
                var vault = VaultUi.VaultFor(doc, out var problem);
                if (problem != null) { TaskDialog.Show(VaultUi.Title, problem); return Result.Cancelled; }
                if (vault == null || !Directory.Exists(vault))
                {
                    VaultUi.AskClaude(VaultUi.Title, "This model has no requirements vault yet.",
                        "Create the requirements vault for this project and register the client's documents (EIR, BEP, LOIN).");
                    return Result.Succeeded;
                }
                if (!Compliance.Trusted(vault))
                {
                    var td = new TaskDialog(VaultUi.Title)
                    {
                        MainInstruction = "Allow ACE to save audit records in this vault?",
                        MainContent = $"{vault}\n\nWith your permission, each model check saves its compliance result in '04 Audits', so reports can show " +
                                      "what changed since the last audit. Asked once per vault; Claude's vault tool uses the same permission.",
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Allow and open the vault");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the vault only");
                    td.CommonButtons = TaskDialogCommonButtons.Cancel;
                    var answer = td.Show();
                    if (answer == TaskDialogResult.Cancel) return Result.Cancelled;
                    if (answer == TaskDialogResult.CommandLink1) VaultUi.Trust(vault);
                }
                var index = Path.Combine(vault, "index.html");
                VaultUi.Open(File.Exists(index) ? index : vault);
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Vault: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class MasterReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show("ACE Master report", "Open a model first."); return Result.Cancelled; }
                var vault = VaultUi.VaultFor(doc, out _);
                var report = vault != null ? VaultUi.LatestReport(vault, doc.Title) : null;
                if (report != null) { VaultUi.Open(report); return Result.Succeeded; }

                // No report reachable: the summary kept in the model (if any) still says what the last audit found.
                var stored = AuditSummary.Read(doc);
                var text = stored == null ? "There is no master report for this model yet."
                    : $"Last audit kept in this model: {stored["rev"]} of {stored["date"]}, {stored["notMet"]} not met, {stored["partly"]} partly met" +
                      (stored["score"] != null ? $", score {stored["score"]}" : "") + $".\nReport: {stored["reportPath"]} (not reachable from here).";
                VaultUi.AskClaude("ACE Master report", text, "Run the model check, then make the master audit report for this model.");
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Master report: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }
}
