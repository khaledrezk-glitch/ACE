using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Companion
{
    /// <summary>
    /// The ACE Companion dockable pane: works alongside Claude Desktop (no AI of its own).
    /// Tabs: Approvals (Apply/Cancel Claude's previews), Activity (live feed), Results (click to select),
    /// Context & prompts (selection/view summary and ready-made prompts copied to the clipboard).
    /// Written in code (no XAML) so it builds everywhere.
    /// </summary>
    internal sealed class CompanionView : UserControl
    {
        private readonly Palette _c;
        private readonly TabControl _tabs = new TabControl();
        private readonly TabItem _approvalsTab, _activityTab, _resultsTab, _contextTab;
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _toast = new TextBlock();
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool _renderQueued;

        public CompanionView()
        {
            _c = Palette.Current();
            Background = _c.Background;
            FontFamily = new FontFamily(Branding.FontFamily + ", Arial");
            FontSize = 12;
            Foreground = _c.Text;

            var root = new DockPanel { LastChildFill = true };
            var header = new StackPanel { Margin = new Thickness(10, 10, 10, 6) };
            // ACE layout (as in the email signature): logo on a white field | thin ACE Red divider | text.
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (Branding.Mark != null)
            {
                const double markHeight = 44; // ~84 px wide: above the 72 px digital minimum
                titleRow.Children.Add(new Border
                {
                    Background = Brushes.White,
                    Padding = new Thickness(markHeight * 0.25), // clear space: 25% of logo height
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = MarkImage(markHeight),
                });
                titleRow.Children.Add(new Border { Width = 2, Background = _c.Rule, Margin = new Thickness(12, 4, 12, 4) });
            }
            var titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText.Children.Add(new TextBlock { Text = Branding.Mark != null ? "Companion" : $"{Branding.Name} Companion", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = _c.Text });
            if (!string.IsNullOrEmpty(Branding.FullName))
                titleText.Children.Add(new TextBlock { Text = Branding.FullName, FontSize = 11, Foreground = _c.Muted });
            titleRow.Children.Add(titleText);
            header.Children.Add(titleRow);
            if (Branding.Mark == null)
                header.Children.Add(new Border { Height = 2, Background = _c.Rule, Margin = new Thickness(0, 6, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, Width = 56 });
            _status.Foreground = _c.Muted;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 8, 0, 0);
            header.Children.Add(_status);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _toast.Foreground = _c.Text;
            _toast.Margin = new Thickness(10, 4, 10, 8);
            _toast.TextWrapping = TextWrapping.Wrap;
            DockPanel.SetDock(_toast, Dock.Bottom);
            root.Children.Add(_toast);

            _tabs.Background = _c.Background;
            _tabs.BorderBrush = _c.Border;
            _approvalsTab = Tab("Approvals");
            _activityTab = Tab("Activity");
            _resultsTab = Tab("Results");
            _contextTab = Tab("Context & prompts");
            root.Children.Add(_tabs);
            Content = root;

            ActivityHub.Changed += QueueRender;
            Ribbon.WorkModes.Changed += QueueRender;
            _clock.Tick += (s, e) => QueueRender();
            _clock.Start();
            Render();
        }

        private TabItem Tab(string header)
        {
            var t = new TabItem { Header = header };
            _tabs.Items.Add(t);
            return t;
        }

        private void QueueRender()
        {
            if (_renderQueued) return;
            _renderQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _renderQueued = false; Render(); }), DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------------------------------------

        private void Render()
        {
            var server = App.Server;
            var last = ActivityHub.Activity.FirstOrDefault();
            _status.Text = (server?.IsRunning == true ? "Connected. Claude can work in this Revit session." : "Connection stopped (ACE tab > MCP Status).") +
                           (last != null ? $"\nLast activity: {Ago(last.Time)}" : "\nWaiting for Claude, ask it something in Claude Desktop.");

            var waiting = ActivityHub.Waiting.ToList();
            _approvalsTab.Header = waiting.Count > 0 ? $"Approvals ({waiting.Count})" : "Approvals";
            _approvalsTab.Content = Scroll(RenderApprovals(waiting));
            _activityTab.Content = Scroll(RenderActivity());
            _resultsTab.Header = ActivityHub.Results.Count > 0 ? $"Results ({ActivityHub.Results.Count})" : "Results";
            _resultsTab.Content = Scroll(RenderResults());
            _contextTab.Content = Scroll(RenderContext());

            if (waiting.Count > 0 && _tabs.SelectedItem != _approvalsTab && waiting.Any(w => DateTime.Now - w.PreviewedAt < TimeSpan.FromSeconds(5)))
                _tabs.SelectedItem = _approvalsTab; // bring new previews to the user's attention
            if (_tabs.SelectedItem == null) _tabs.SelectedIndex = 0;
        }

        // The logo file is used as supplied (never recoloured or stretched); high-quality scaling keeps it crisp.
        private static Image MarkImage(double height)
        {
            var img = new Image { Source = Branding.Mark, Height = height, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private UIElement RenderApprovals(System.Collections.Generic.List<PendingChange> waiting)
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            if (waiting.Count == 0)
            {
                panel.Children.Add(Hint("No changes waiting.\n\nWhen Claude previews a change to your model, it appears here. Click Apply to make the change " +
                                        "(one undo step), or Cancel. You can also just answer Claude in the chat."));
            }
            foreach (var p in waiting)
            {
                var card = Card();
                var body = (StackPanel)card.Child;
                body.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text });
                body.Children.Add(new TextBlock { Text = p.Summary, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text, Margin = new Thickness(0, 4, 0, 0) });
                foreach (var bytes in p.Images.Take(2))
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new System.IO.MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
                        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 260, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        body.Children.Add(new Border { BorderBrush = _c.Border, BorderThickness = new Thickness(1), Background = Brushes.White, Child = img, Margin = new Thickness(0, 6, 0, 0) });
                    }
                    catch { }
                }
                body.Children.Add(new TextBlock { Text = $"Previewed {Ago(p.PreviewedAt)}. The model is unchanged until you apply.", Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var apply = ActionButton("Apply", true);
                var cancel = ActionButton("Cancel", false);
                apply.Click += async (s, e) =>
                {
                    apply.IsEnabled = cancel.IsEnabled = false;
                    apply.Content = "Applying...";
                    await ApplyAsync(p);
                };
                cancel.Click += (s, e) =>
                {
                    ActivityHub.Decide(p, PendingState.Rejected, "Cancelled by the user");
                    Journal.Append($"Cancelled in ACE panel: {p.Title}", null, "The user cancelled this previewed change. Nothing was changed.");
                    Toast("Cancelled. Nothing was changed. Claude will be told if it tries to apply it.");
                };
                buttons.Children.Add(apply);
                buttons.Children.Add(cancel);
                body.Children.Add(buttons);
                panel.Children.Add(card);
            }

            var decided = ActivityHub.Pending.Where(p => p.State != PendingState.Waiting && p.DecidedAt.HasValue).Take(5).ToList();
            if (decided.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Recent decisions", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 4), Foreground = _c.Text });
                foreach (var p in decided)
                {
                    var state = p.State switch
                    {
                        PendingState.AppliedByPanel => "Applied by you",
                        PendingState.AppliedByClaude => "Applied via Claude",
                        PendingState.Rejected => "Cancelled",
                        _ => "Failed",
                    };
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"{state} · {p.Title} · {p.DecidedAt:HH:mm}" + (string.IsNullOrEmpty(p.Outcome) ? "" : $"\n   {p.Outcome}"),
                        Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 2),
                    });
                }
            }
            return panel;
        }

        private async System.Threading.Tasks.Task ApplyAsync(PendingChange p)
        {
            var args = (JsonObject)p.Args.DeepClone();
            args["dry_run"] = false;
            args["_fromPanel"] = true;
            try
            {
                var result = await App.Dispatcher.EnqueueAsync(p.Kind, args, TimeSpan.FromMinutes(5));
                var r = result as JsonObject;
                bool ok;
                string outcome;
                if (p.Kind == "set_parameters")
                {
                    var failed = (int?)r?["failed"]?.GetValue<int>() ?? 0;
                    ok = failed == 0;
                    outcome = $"{r?["applied"]} value(s) changed" + (failed > 0 ? $", {failed} failed" : "");
                }
                else
                {
                    ok = r?["success"]?.GetValue<bool>() == true;
                    var c = r?["changed"] as JsonObject;
                    outcome = ok
                        ? (c != null ? $"added {c["added"]}, modified {c["modified"]}, deleted {c["deleted"]}" : "applied")
                        : (r?["error"]?.ToString() ?? (r?["errors"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "failed");
                }
                ActivityHub.Decide(p, ok ? PendingState.AppliedByPanel : PendingState.Failed, outcome);
                Journal.Append($"Change (applied in ACE panel): {p.Title}", null,
                    ok ? $"APPLIED by the user from the ACE Companion panel ({outcome})" : $"FAILED - nothing changed ({outcome})");
                Toast(ok ? $"Applied: {outcome}. One undo step (Ctrl+Z). Tell Claude \"done\" so it can verify."
                         : $"Not applied: {outcome}. Nothing was changed.");
            }
            catch (Exception ex)
            {
                ActivityHub.Decide(p, PendingState.Failed, ex.Message);
                Toast($"Could not apply: {ex.Message}");
            }
        }

        private UIElement RenderActivity()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            if (ActivityHub.Activity.Count == 0)
                panel.Children.Add(Hint("Everything Claude does in this Revit session appears here: what it read, previewed and changed."));
            foreach (var a in ActivityHub.Activity.Take(80))
            {
                // Failed rows get a thin ACE Red marker (accent, not red text).
                var row = new StackPanel();
                row.Children.Add(new TextBlock
                {
                    Text = $"{a.Time:HH:mm:ss}   {a.Title}{(a.Ok ? "" : "  (failed)")}",
                    Foreground = _c.Text, FontWeight = a.Ok ? FontWeights.Normal : FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrEmpty(a.Detail))
                    row.Children.Add(new TextBlock { Text = a.Detail, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
                panel.Children.Add(new Border
                {
                    BorderBrush = a.Ok ? Brushes.Transparent : _c.Rule, BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(6, 3, 0, 3), Margin = new Thickness(0, 1, 0, 1), Child = row,
                });
            }
            return panel;
        }

        private UIElement RenderResults()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            var results = ActivityHub.Results;
            if (results.Count == 0)
            {
                panel.Children.Add(Hint("Elements from Claude's latest answer appear here. Click one to select and zoom to it in Revit."));
                return panel;
            }
            panel.Children.Add(new TextBlock { Text = ActivityHub.ResultsTitle, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var selectAll = ActionButton($"Select all {results.Count} in Revit", true);
            selectAll.HorizontalAlignment = HorizontalAlignment.Left;
            selectAll.Click += async (s, e) => await SelectAsync(results.Select(r => r.Id).ToArray(), zoom: false);
            panel.Children.Add(selectAll);
            foreach (var r in results.Take(300))
            {
                var link = new Button
                {
                    Content = new TextBlock { Text = $"{r.Id}  {r.Label}", TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Underline },
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Foreground = _c.Link, Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(2, 3, 2, 3), ToolTip = "Select and zoom to this element",
                };
                var id = r.Id;
                link.Click += async (s, e) => await SelectAsync(new[] { id }, zoom: true);
                panel.Children.Add(link);
            }
            if (results.Count > 300) panel.Children.Add(Hint($"... and {results.Count - 300} more (use Select all)."));
            return panel;
        }

        private async System.Threading.Tasks.Task SelectAsync(long[] ids, bool zoom)
        {
            try
            {
                var args = new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["zoom"] = zoom };
                var r = await App.Dispatcher.EnqueueAsync("select_elements", args, TimeSpan.FromSeconds(30));
                Toast($"Selected {r?["selected"]} element(s) in Revit.");
            }
            catch (Exception ex) { Toast($"Could not select: {ex.Message}"); }
        }

        private UIElement RenderContext()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            var ctx = ActivityHub.Context;
            panel.Children.Add(new TextBlock { Text = "Right now in Revit", FontWeight = FontWeights.SemiBold, Foreground = _c.Text });
            panel.Children.Add(new TextBlock
            {
                Text = $"Model: {ctx.Document ?? "(none open)"}\nView: {ctx.View ?? "-"}{(ctx.ViewType != null ? $" ({ctx.ViewType})" : "")}\nSelection: {ctx.SelectionSummary ?? "nothing selected"}",
                Foreground = _c.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
            });
            var copyContext = ActionButton("Copy this context for Claude", true);
            copyContext.HorizontalAlignment = HorizontalAlignment.Left;
            copyContext.Click += (s, e) => Copy(ContextText(), "Context copied. Paste it into Claude (Ctrl+V).");
            panel.Children.Add(copyContext);

            panel.Children.Add(new TextBlock { Text = "Quick prompts (copied to the clipboard, paste into Claude)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4), Foreground = _c.Text, TextWrapping = TextWrapping.Wrap });
            var hasSelection = ctx.SelectedIds.Count > 0;
            foreach (var (group, prompts) in Prompts())
            {
                panel.Children.Add(new TextBlock { Text = group, Foreground = _c.Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
                foreach (var (label, prompt, needsSelection) in prompts)
                {
                    var available = !needsSelection || hasSelection;
                    var b = ActionButton(label, false);
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                    b.Margin = new Thickness(0, 2, 0, 2);
                    // Not disabled (WPF's disabled look ignores the theme and becomes unreadable): muted instead.
                    if (!available) { b.Foreground = _c.Muted; b.ToolTip = "Select elements in Revit first"; }
                    b.Click += (s, e) =>
                    {
                        if (needsSelection && ActivityHub.Context.SelectedIds.Count == 0) { Toast("Select elements in Revit first, then click this prompt again."); return; }
                        Copy(prompt(), $"Copied \"{label}\". Paste into Claude (Ctrl+V).");
                    };
                    panel.Children.Add(b);
                }
            }
            return panel;
        }

        private static (string, (string, Func<string>, bool)[])[] Prompts()
        {
            // The working mode's prompts come first.
            var mode = Ribbon.WorkModes.Current;
            var general = GeneralPrompts();
            if (mode.Prompts.Length == 0) return general;
            var first = ($"{mode.Label} mode", mode.Prompts.Select(p => (p.Label, (Func<string>)(() => p.Prompt), false)).ToArray());
            return new[] { first }.Concat(general).ToArray();
        }

        private static (string, (string, Func<string>, bool)[])[] GeneralPrompts()
        {
            string sel() => SelectionText();
            const string ro = " Read-only, don't change anything.";
            const string gated = " Explain your plan first and show me a preview of what would change before applying anything.";
            return new (string, (string, Func<string>, bool)[])[]
            {
                ("About my selection", new (string, Func<string>, bool)[]
                {
                    ("Explain my selection", () => $"Using ace-revit: explain what I have selected in Revit ({sel()}): what the elements are, their key parameters, and anything unusual or inconsistent.{ro}", true),
                    ("Check parameters of my selection", () => $"Using ace-revit: check the parameters of my selection ({sel()}): which important values are missing or inconsistent?{ro}", true),
                    ("Compare the selected elements", () => $"Using ace-revit: compare the selected elements ({sel()}) and show me, in a table, the parameters whose values differ between them.{ro}", true),
                    ("Find and select similar elements", () => $"Using ace-revit: find every element in the model with the same family and type as my selection ({sel()}), tell me how many there are per level, and select them.", true),
                }),
                ("About this view", new (string, Func<string>, bool)[]
                {
                    ("Describe this view", () => $"Using ace-revit: describe the active view: what it shows, its scale, view template, filters, crop and visibility settings, and anything that looks wrong. Look at it as an image too.{ro}", false),
                    ("Check this sheet", () => $"Using ace-revit: check the active sheet: title block data (number, name, revision, dates, drawn/checked by), which views are placed on it, and anything missing or inconsistent.{ro}", false),
                    ("Tag untagged elements in this view", () => $"Using ace-revit: find the untagged doors, windows and rooms in the active view and tag them.{gated}", false),
                }),
                ("Model checks", new (string, Func<string>, bool)[]
                {
                    ("Model QA check", () => $"Using ace-revit: run a QA check of the open model and explain the top issues simply, ranked by importance, with element ids and a suggested fix for each.{ro}", false),
                    ("Warnings summary", () => $"Using ace-revit: list the model's warnings grouped by type with counts, show the worst offenders, and say which ones could be fixed safely and how.{ro}", false),
                    ("Parameter completeness", () => $"Using ace-revit: check how complete the key parameters are for doors, windows and rooms and summarise the gaps by level.{ro}", false),
                    ("Rooms and doors check", () => $"Using ace-revit: find rooms without doors, unplaced or unenclosed rooms, and doors narrower than 900 mm, listed by level.{ro}", false),
                    ("Views and sheets housekeeping", () => $"Using ace-revit: list views not placed on any sheet, empty sheets, views without a view template, and duplicate or badly named views and sheets.{ro}", false),
                    ("Families and CAD imports", () => $"Using ace-revit: list in-place families, imported (not linked) CAD files, unused families and types, and the largest families by instance count.{ro}", false),
                    ("Model statistics by level", () => $"Using ace-revit: give me a table of element counts by level for walls, doors, windows, rooms, floors and furniture, plus total room area per level.{ro}", false),
                }),
                ("Common tasks", new (string, Func<string>, bool)[]
                {
                    ("Renumber rooms on a level", () => $"Using ace-revit: renumber the rooms on level [LEVEL NAME] in reading order (left to right, top to bottom), starting at [START NUMBER].{gated}", false),
                    ("Create sheets for levels", () => $"Using ace-revit: create a floor plan and a sheet for every level using the title block [TITLE BLOCK NAME], numbered [A-101] onwards.{gated}", false),
                    ("Fill empty parameters", () => $"Using ace-revit: for [CATEGORY], where [PARAMETER] is empty, set it to [VALUE]. Tell me how many elements that affects per level first.{gated}", false),
                }),
                ("Session and support", new (string, Func<string>, bool)[]
                {
                    ("What did you change today?", () => "Using ace-revit: what did you preview and change in Revit today? Use the activity log.", false),
                    ("Back up the model first", () => "Using ace-revit: make a backup copy of the open model now, before we make bigger changes, and tell me where it was saved.", false),
                    ("Undo your last change", () => "Using ace-revit: undo your last change in Revit, then confirm what was undone.", false),
                    ("Report a problem with ACE", () => "Using ace-revit: something isn't working as expected. Run check_setup, fix what you can, and create an issue report for the ACE tool maintainers. The problem: ", false),
                }),
            };
        }

        private static string SelectionText()
        {
            var ctx = ActivityHub.Context;
            if (ctx.SelectedIds.Count == 0) return "nothing selected";
            var ids = string.Join(", ", ctx.SelectedIds.Take(60));
            return $"{ctx.SelectionSummary}; element ids: {ids}{(ctx.SelectedIds.Count > 60 ? ", ..." : "")}";
        }

        private static string ContextText()
        {
            var ctx = ActivityHub.Context;
            var sb = new StringBuilder("Current Revit context (from the ACE panel):\n");
            sb.AppendLine($"- Model: {ctx.Document ?? "none"}");
            sb.AppendLine($"- Active view: {ctx.View ?? "-"} ({ctx.ViewType ?? "-"})");
            sb.AppendLine($"- Selection: {SelectionText()}");
            return sb.ToString();
        }

        // ---- small UI helpers ---------------------------------------------------------------------------

        private void Copy(string text, string message)
        {
            try { Clipboard.SetText(text); Toast(message); }
            catch (Exception ex) { Toast($"Could not copy: {ex.Message}"); }
        }

        private void Toast(string text) => _toast.Text = text;

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            return t.ToString("HH:mm");
        }

        private ScrollViewer Scroll(UIElement content) =>
            new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = _c.Background };

        private TextBlock Hint(string text) =>
            new TextBlock { Text = text, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 4) };

        private Border Card() => new Border
        {
            Background = _c.Card, BorderBrush = _c.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), Child = new StackPanel(),
        };

        private Button ActionButton(string text, bool primary) => new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 4, 8, 4),
            Background = primary ? _c.Accent : _c.Card,
            Foreground = primary ? _c.OnAccent : _c.Text,
            BorderBrush = primary ? _c.Accent : _c.Border,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        private sealed class Palette
        {
            public Brush Background, Card, Border, Text, Muted, Accent, OnAccent, Link, Error, Rule;

            public static Palette Current()
            {
                Brush b(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
                Brush c(Color col) => new SolidColorBrush(col);
                // Brand: black / white / greys, with the accent colour (ACE Red) for thin rules and the main action only.
                return Branding.IsDarkTheme
                    ? new Palette { Background = b("#212121"), Card = b("#414042"), Border = b("#A0A0A0"), Text = b("#E6E7E8"), Muted = b("#C4C4C4"),
                                    Accent = c(Branding.Accent), OnAccent = Brushes.White, Link = b("#E6E7E8"), Error = b("#E6E7E8"), Rule = c(Branding.Accent) }
                    : new Palette { Background = c(Branding.GreyLight), Card = Brushes.White, Border = b("#A0A0A0"), Text = c(Branding.Primary), Muted = c(Branding.GreyDark),
                                    Accent = c(Branding.Accent), OnAccent = Brushes.White, Link = c(Branding.Primary), Error = c(Branding.Primary), Rule = c(Branding.Accent) };
            }
        }
    }
}
