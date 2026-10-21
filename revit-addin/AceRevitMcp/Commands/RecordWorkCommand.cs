using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TextBox = System.Windows.Controls.TextBox;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Ribbon "Record my work": Start asks for a short label and an optional note, Stop for an optional closing note,
    /// then writes the method case into the project's vault (cases/), else %APPDATA%\ACE-RevitMCP\cases. Read-only.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class RecordWorkCommand : IExternalCommand
    {
        private const string Title = "ACE Record my work";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var app = commandData.Application;
            try
            {
                var cur = CaptureMethod.Current();
                if (cur != null && !cur.IsRibbon)
                {
                    TaskDialog.Show(Title, $"Claude is capturing \"{cur.Label}\" on {cur.Model} since {cur.StartedAt}.\n\nAsk Claude to stop it (capture_method stop) before you start your own recording.");
                    return Result.Cancelled;
                }
                if (cur == null)
                {
                    if (app.ActiveUIDocument?.Document == null) { TaskDialog.Show(Title, "Open a model first."); return Result.Cancelled; }
                    var start = NoteDialog.Ask(app.MainWindowHandle, "Start recording my work",
                        "ACE watches what you change (elements, parameters, geometry, joins, sketches), the views you use and the Revit commands, in every model you work on, until you click Stop. Nothing in the model is changed.",
                        "Short label (e.g. slab edges at cores)", true, "What is the task? (optional)");
                    if (start == null) return Result.Cancelled;
                    WorkRecorder.Start(app, start.Value.Label, start.Value.Note);
                    return Result.Succeeded;
                }

                var stop = NoteDialog.Ask(app.MainWindowHandle, $"Stop recording \"{cur.Label}\"",
                    $"Recording since {cur.StartedAt}. The case is written to the project's vault (cases folder) for Claude to learn from; it becomes rules only after you confirm them.",
                    null, false, "What was the rule you followed? (optional)", okText: "Stop and save");
                if (stop == null) return Result.Cancelled;   // keep recording
                var c = WorkRecorder.Finish(app, stop.Value.Note, out var vaultDoc);
                var (dir, where) = CaseFolder(vaultDoc ?? app.ActiveUIDocument?.Document);
                (string Json, string Md) files;
                try { files = WorkRecorder.WriteAndEnd(c, dir); }
                catch (Exception ex) when (dir != OwnFolder)
                {
                    where = $"Could not write to {dir} ({ex.Message}): saved in your ACE folder instead.";
                    files = WorkRecorder.WriteAndEnd(c, OwnFolder);
                }
                var changed = (c["diffs"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
                var docs = (c["documents"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
                var prompt = $"Analyse my recorded work case \"{c["label"]}\" ({files.Json}) and propose rules for my approval.";
                var td = new TaskDialog(Title)
                {
                    MainInstruction = $"Recording \"{c["label"]}\" saved: {changed} changed element(s) in {docs} model(s).",
                    MainContent = $"{files.Json}\n{files.Md}" + (where != null ? $"\n\n{where}" : "") + $"\n\nAsk Claude:\n\"{prompt}\"",
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Copy the prompt for Claude");
                td.CommonButtons = TaskDialogCommonButtons.Close;
                if (td.Show() == TaskDialogResult.CommandLink1)
                    try { Clipboard.SetText(prompt); } catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"Record my work: {ex}");
                message = ex.Message;
                TaskDialog.Show(Title, ex.Message);
                return Result.Failed;
            }
        }

        private static string OwnFolder => Path.Combine(AceConfig.Directory, "cases");

        /// <summary>
        /// As capture_method stop: the project's vault (found from the model folder) when ACE may write there; a vault not
        /// yet allowed is asked about (once per vault, the same permission as the Vault button and Claude's vault tool);
        /// no vault: the ACE folder, and the dialog says so.
        /// </summary>
        private static (string Dir, string Note) CaseFolder(Document doc)
        {
            string vault = null;
            try { if (doc != null) vault = Rules.VaultUi.VaultFor(doc, out _); } catch { }
            if (vault == null || !Directory.Exists(vault)) return (OwnFolder, $"No project vault was found for this model: the case is in your ACE folder ({OwnFolder}). Claude finds it there too.");
            if (!Rules.Compliance.Trusted(vault))
            {
                var td = new TaskDialog(Title)
                {
                    MainInstruction = "Save the case in the project's vault?",
                    MainContent = $"{vault}\n\nThe case goes into its cases folder, so the team and Claude find it with the project. Asked once per vault; Claude's vault tool uses the same permission.",
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Allow and save it in the vault");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Save it in my ACE folder only");
                if (td.Show() != TaskDialogResult.CommandLink1) return (OwnFolder, $"Saved in your ACE folder ({OwnFolder}), not in the vault.");
                Rules.VaultUi.Trust(vault);
            }
            return (Path.Combine(vault, "cases"), null);
        }
    }

    /// <summary>A small input window (label and note), the same in Revit 2024 and 2025.</summary>
    internal static class NoteDialog
    {
        public static (string Label, string Note)? Ask(IntPtr owner, string heading, string text, string labelPrompt, bool labelRequired, string notePrompt, string okText = "Start recording")
        {
            var w = new Window
            {
                Title = "ACE Record my work", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap });
            TextBox label = null;
            if (labelPrompt != null)
            {
                panel.Children.Add(new TextBlock { Text = labelPrompt });
                label = new TextBox { MaxLength = 80, Margin = new Thickness(0, 2, 0, 10) };
                panel.Children.Add(label);
            }
            panel.Children.Add(new TextBlock { Text = notePrompt });
            var note = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80, MaxLength = WorkRecordLogic.MaxNote, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 2, 0, 12) };
            panel.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = okText, IsDefault = labelPrompt != null, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = labelPrompt != null ? "Cancel" : "Keep recording", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;
            if (label != null)
            {
                ok.IsEnabled = false;
                label.TextChanged += (s, e) => ok.IsEnabled = !labelRequired || !string.IsNullOrWhiteSpace(label.Text);
                w.Loaded += (s, e) => label.Focus();
            }
            else w.Loaded += (s, e) => note.Focus();
            ok.Click += (s, e) => w.DialogResult = true;
            try { new WindowInteropHelper(w) { Owner = owner }; } catch { }
            if (w.ShowDialog() != true) return null;
            return (label?.Text?.Trim(), string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim());
        }
    }
}
