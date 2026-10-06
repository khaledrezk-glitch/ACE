using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AceRevitMcp.Util;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace AceRevitMcp.Companion
{
    /// <summary>
    /// The ACE Companion dockable pane: works alongside Claude Desktop (no AI of its own).
    /// Home (what needs you, the model at a glance, quick actions, ask Claude), Approvals (Apply / Cancel Claude's
    /// previews), Activity (live feed), Results (click to select) and Prompts (ready-made, the work mode's first).
    /// Written in code (no XAML) so it builds everywhere.
    /// </summary>
    internal sealed class CompanionView : UserControl
    {
        private readonly Palette _c;
        private readonly WrapPanel _nav = new WrapPanel { Margin = new Thickness(8, 0, 8, 0) };
        private readonly ContentControl _page = new ContentControl();
        private readonly Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();
        private string _current = "Home";
        private readonly TextBlock _status = new TextBlock();
        private readonly Ellipse _light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _model = new TextBlock();
        private readonly ComboBox _mode = new ComboBox { MinWidth = 130, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _toast = new TextBlock();
        private readonly TextBox _ask = new TextBox();
        private readonly TextBox _promptSearch = new TextBox(), _resultSearch = new TextBox();
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool _renderQueued, _changesOnly, _modeUpdating;
        private readonly HashSet<string> _collapsed = new HashSet<string>();

        public CompanionView()
        {
            _c = Palette.Current();
            Background = _c.Background;
            FontFamily = new FontFamily(Branding.FontFamily + ", Arial");
            FontSize = 12;
            Foreground = _c.Text;

            var root = new DockPanel { LastChildFill = true };
            var header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            foreach (var name in new[] { "Home", "Approvals", "Activity", "Results", "Prompts" })
            {
                var b = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 6, 8, 5), Margin = new Thickness(0, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Hand, Foreground = _c.Text };
                var n = name;
                b.Click += (s, e) => Show(n);
                _navButtons[name] = b;
                _nav.Children.Add(b);
            }
            var navBar = new Border { BorderBrush = _c.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = _nav };
            DockPanel.SetDock(navBar, Dock.Top);
            root.Children.Add(navBar);

            _toast.Foreground = _c.Text;
            _toast.Margin = new Thickness(10, 6, 10, 8);
            _toast.TextWrapping = TextWrapping.Wrap;
            DockPanel.SetDock(_toast, Dock.Bottom);
            root.Children.Add(_toast);
            root.Children.Add(_page);
            Content = root;

            foreach (var t in new[] { _ask, _promptSearch, _resultSearch })
            {
                t.Background = _c.Card; t.Foreground = _c.Text; t.BorderBrush = _c.Border; t.Padding = new Thickness(6, 4, 6, 4);
            }
            _ask.AcceptsReturn = true; _ask.TextWrapping = TextWrapping.Wrap; _ask.MinHeight = 54; _ask.MaxHeight = 140;
            _ask.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _promptSearch.TextChanged += (s, e) => { if (_current == "Prompts") Render(); };
            _resultSearch.TextChanged += (s, e) => { if (_current == "Results") Render(); };

            ActivityHub.Changed += QueueRender;
            Ribbon.WorkModes.Changed += QueueRender;
            StatusStore.Changed += QueueRender;
            _clock.Tick += (s, e) => QueueRender();
            _clock.Start();
            Render();
        }

        private UIElement BuildHeader()
        {
            var header = new StackPanel { Margin = new Thickness(10, 10, 10, 8) };
            // ACE layout (as in the email signature): logo on a white field | thin ACE Red divider | text.
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (Branding.Mark != null)
            {
                const double markHeight = 36; // ~70 px wide: at the digital minimum and crisp
                titleRow.Children.Add(new Border
                {
                    Background = Brushes.White, Padding = new Thickness(markHeight * 0.25), CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center, Child = MarkImage(markHeight),
                });
                titleRow.Children.Add(new Border { Width = 2, Background = _c.Rule, Margin = new Thickness(10, 4, 10, 4) });
            }
            var titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText.Children.Add(new TextBlock { Text = Branding.Mark != null ? "Companion" : $"{Branding.Name} Companion", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = _c.Text });
            _model.Foreground = _c.Muted; _model.FontSize = 11; _model.TextTrimming = TextTrimming.CharacterEllipsis;
            titleText.Children.Add(_model);
            titleRow.Children.Add(titleText);
            header.Children.Add(titleRow);

            var statusRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var modeBox = new StackPanel { Orientation = Orientation.Horizontal };
            modeBox.Children.Add(new TextBlock { Text = "Mode", Foreground = _c.Muted, VerticalAlignment = VerticalAlignment.Center });
            foreach (var m in Ribbon.WorkModes.All) _mode.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Key, ToolTip = m.Summary });
            _mode.SelectionChanged += (s, e) =>
            {
                if (_modeUpdating || !(_mode.SelectedItem is ComboBoxItem item)) return;
                try { Ribbon.WorkModes.Apply((string)item.Tag); Toast($"{item.Content} mode: {Ribbon.WorkModes.Current.Summary}"); }
                catch (Exception ex) { Toast("Could not switch mode: " + ex.Message); }
            };
            modeBox.Children.Add(_mode);
            DockPanel.SetDock(modeBox, Dock.Right);
            statusRow.Children.Add(modeBox);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_light);
            _status.Foreground = _c.Muted; _status.TextWrapping = TextWrapping.Wrap; _status.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(_status);
            statusRow.Children.Add(left);
            header.Children.Add(statusRow);
            return header;
        }

        private void QueueRender()
        {
            if (_renderQueued) return;
            _renderQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _renderQueued = false; Render(); }), DispatcherPriority.Background);
        }

        private void Show(string page) { _current = page; Render(); }

        // ------------------------------------------------------------------------------------------------

        private void Render()
        {
            var server = App.Server;
            var running = server?.IsRunning == true;
            var last = ActivityHub.Activity.FirstOrDefault();
            _light.Fill = running ? new SolidColorBrush(Coordination.ClashColours.Green) : _c.Rule;
            _status.Text = running ? (last != null ? $"Connected · last activity {Ago(last.Time)}" : "Connected · waiting for Claude") : "Not connected (ACE tab > MCP Status)";
            _model.Text = ActivityHub.Context.Document ?? "No model open";
            _modeUpdating = true;
            _mode.SelectedIndex = Array.FindIndex(Ribbon.WorkModes.All, m => m.Key == Ribbon.WorkModes.Current.Key);
            _modeUpdating = false;

            var waiting = ActivityHub.Waiting.ToList();
            // Bring new previews to the user's attention.
            if (waiting.Any(w => DateTime.Now - w.PreviewedAt < TimeSpan.FromSeconds(5)) && _current != "Approvals") _current = "Approvals";
            foreach (var (name, b) in _navButtons)
            {
                var count = name == "Approvals" ? waiting.Count : name == "Results" ? ActivityHub.Results.Count : 0;
                b.Content = count > 0 ? $"{name} ({count})" : name;
                var on = name == _current;
                b.BorderBrush = on ? _c.Rule : Brushes.Transparent;
                b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                b.Foreground = on ? _c.Text : _c.Muted;
            }
            // The text boxes are kept between renders (so typing is not lost): detach them from the old page first.
            var focused = System.Windows.Input.Keyboard.FocusedElement as TextBox;
            foreach (var t in new[] { _ask, _promptSearch, _resultSearch })
                if (t.Parent is Panel owner) owner.Children.Remove(t);
            UIElement content = _current switch
            {
                "Approvals" => RenderApprovals(waiting),
                "Activity" => RenderActivity(),
                "Results" => RenderResults(),
                "Prompts" => RenderPrompts(),
                _ => RenderHome(waiting),
            };
            // Keep the scroll position when the same page re-renders.
            var old = _page.Content as ScrollViewer;
            var offset = old != null && (string)old.Tag == _current ? old.VerticalOffset : 0;
            var scroll = Scroll(content);
            scroll.Tag = _current;
            _page.Content = scroll;
            if (offset > 0) scroll.ScrollToVerticalOffset(offset);
            if (focused != null && (focused == _ask || focused == _promptSearch || focused == _resultSearch))
                Dispatcher.BeginInvoke(new Action(() => focused.Focus()), DispatcherPriority.Input);
        }

        // The logo file is used as supplied (never recoloured or stretched); high-quality scaling keeps it crisp.
        private static Image MarkImage(double height)
        {
            var img = new Image { Source = Branding.Mark, Height = height, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        // ---- Home ---------------------------------------------------------------------------------------

        private UIElement RenderHome(List<PendingChange> waiting)
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            var model = ActivityHub.Context.Document;

            // Needs you
            var needs = Card();
            var nb = (StackPanel)needs.Child;
            nb.Children.Add(Section("Needs you"));
            if (waiting.Count > 0)
            {
                nb.Children.Add(new TextBlock { Text = $"{waiting.Count} change{(waiting.Count == 1 ? "" : "s")} previewed by Claude, waiting for your decision.", TextWrapping = TextWrapping.Wrap, Foreground = _c.Text });
                var review = ActionButton("Review", true);
                review.HorizontalAlignment = HorizontalAlignment.Left;
                review.Click += (s, e) => Show("Approvals");
                nb.Children.Add(review);
            }
            else nb.Children.Add(new TextBlock { Text = "Nothing waiting. Claude's previews appear here for you to apply or cancel.", TextWrapping = TextWrapping.Wrap, Foreground = _c.Muted });
            panel.Children.Add(needs);

            // At a glance
            var glance = Card();
            var gb = (StackPanel)glance.Child;
            gb.Children.Add(Section("At a glance"));
            var tiles = new UniformGrid { Columns = 3 };
            // Every number here comes from the status store, which the engines publish to.
            var status = StatusStore.For(model);
            var health = status?.Health;
            if (health != null)
                tiles.Children.Add(Stat(health.Score.ToString(), $"Health · {health.Grade}", $"{health.Checks.Count(c => c.Status == "fail")} to act on · {health.Time:HH:mm}", health.Score < 65));
            else tiles.Children.Add(Stat("-", "Health", "Run the model check", false));
            if (status?.Clashes != null)
                tiles.Children.Add(Stat(status.ClashIssues.ToString(), "Clash issues", $"{status.OpenClashes} clashes · {status.NewClashes} new", status.OpenClashes > 0));
            else tiles.Children.Add(Stat("-", "Clash issues", "Open the Clash Browser", false));
            var diffs = status?.Changes;
            if (diffs != null && diffs.Count > 0)
                tiles.Children.Add(Stat(status.ChangeCount.ToString(), "Changes", diffs[0].Since.HasValue ? $"since {diffs[0].Since:d MMM HH:mm}" : "first snapshot taken", false));
            else tiles.Children.Add(Stat("-", "Changes", "See what changed", false));
            gb.Children.Add(tiles);
            panel.Children.Add(glance);

            // Quick actions (run in Revit straight away; no Claude needed)
            var actions = Card();
            var ab = (StackPanel)actions.Child;
            ab.Children.Add(Section("Quick actions"));
            var grid = new UniformGrid { Columns = 2 };
            void Act(string label, string glyph, string command, JsonObject args, Func<JsonNode, string> done, int minutes = 10)
            {
                var b = TileButton(label, glyph);
                b.Click += async (s, e) =>
                {
                    b.IsEnabled = false; Toast($"{label}: working in Revit...");
                    try { var r = await App.Dispatcher.EnqueueAsync(command, args, TimeSpan.FromMinutes(minutes)); Toast(done(r)); }
                    catch (Exception ex) { Toast($"{label}: {ex.Message}"); }
                    finally { b.IsEnabled = true; QueueRender(); }
                };
                grid.Children.Add(b);
            }
            Act("Model check", Ribbon.Glyphs.Dashboard, "get_model_insights", new JsonObject { ["show"] = true }, r => $"Model check: score {r?["score"]}/100 ({r?["grade"]}).");
            Act("Clash Browser", Ribbon.Glyphs.Search, "open_clash_browser", new JsonObject(), r => "Clash Browser opened.", 2);
            Act("What changed", Ribbon.Glyphs.History, "model_changes", new JsonObject { ["show"] = true }, r => "Change tracker opened.");
            Act("Clash view", Ribbon.Glyphs.Clash, "clash_view", new JsonObject(), r => "ACE Clash View: the models in their colours.", 2);
            Act("Coordination report", Ribbon.Glyphs.Checklist, "coordination_report", new JsonObject { ["open"] = true }, r => $"Report saved: {r?["htmlReport"]}", 15);
            Act("Take snapshot", Ribbon.Glyphs.Bookmark, "snapshot_model", new JsonObject { ["label"] = "Companion" }, r => "Snapshot saved: the change tracker compares with it later.", 5);
            ab.Children.Add(grid);
            panel.Children.Add(actions);

            // Ask Claude
            var ask = Card();
            var kb = (StackPanel)ask.Child;
            kb.Children.Add(Section("Ask Claude"));
            kb.Children.Add(new TextBlock { Text = $"Your view and selection are added. Selection: {SelectionText()}", Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontSize = 11 });
            kb.Children.Add(_ask);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var copy = ActionButton("Copy for Claude", true);
            copy.Click += (s, e) =>
            {
                var q = (_ask.Text ?? "").Trim();
                if (q.Length == 0) { Toast("Type what you want Claude to do first."); return; }
                Copy($"Using ace-revit: {q}\n\n{ContextText()}", "Copied with your context. Paste it into Claude Desktop (Ctrl+V).");
            };
            var openClaude = ActionButton("Open Claude", false);
            openClaude.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("claude://") { UseShellExecute = true }); }
                catch { Toast("Could not open Claude Desktop from here: open it from the Start menu."); }
            };
            row.Children.Add(copy); row.Children.Add(openClaude);
            kb.Children.Add(row);
            panel.Children.Add(ask);
            return panel;
        }

        private Border Stat(string value, string label, string note, bool key)
        {
            var p = new StackPanel { Margin = new Thickness(0, 2, 8, 2) };
            p.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = key ? _c.Rule : _c.Text });
            p.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Foreground = _c.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            p.Children.Add(new TextBlock { Text = note, Foreground = _c.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            return new Border { Child = p };
        }

        private Button TileButton(string label, string glyph)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            try { p.Children.Add(new Image { Source = Icons.Line(32, glyph, planned: false), Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) }); } catch { }
            p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text });
            return new Button
            {
                Content = p, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 6),
                Background = _c.Background, BorderBrush = _c.Border, Cursor = System.Windows.Input.Cursors.Hand,
            };
        }

        private TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = _c.Muted, Margin = new Thickness(0, 0, 0, 6) };

        // ---- Approvals ----------------------------------------------------------------------------------

        private UIElement RenderApprovals(List<PendingChange> waiting)
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            if (waiting.Count == 0)
                panel.Children.Add(Hint("No changes waiting.\n\nWhen Claude previews a change to your model, it appears here with pictures. Click Apply to make the change " +
                                        "(one undo step), or Cancel. You can also just answer Claude in the chat."));
            var n = 0;
            foreach (var p in waiting)
            {
                n++;
                var card = Card();
                var body = (StackPanel)card.Child;
                var left = PendingState.Waiting == p.State ? ActivityHub.PendingLifetime - (DateTime.Now - p.PreviewedAt) : TimeSpan.Zero;
                var stepText = p.Steps.Count > 0 && p.Step > 0 ? $" · STEP {p.Step} OF {p.Steps.Count}" : "";
                body.Children.Add(new TextBlock { Text = $"OPTION {n}{stepText} · PREVIEWED {Ago(p.PreviewedAt).ToUpperInvariant()} · EXPIRES IN {Math.Max(0, (int)left.TotalMinutes)} MIN", FontSize = 10, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text, Margin = new Thickness(0, 2, 0, 4) });
                // What Claude says it does and why, in plain words.
                body.Children.Add(new TextBlock
                {
                    Text = p.Explanation ?? "Claude gave no explanation for this change. Ask in the chat what it does before applying.",
                    TextWrapping = TextWrapping.Wrap, Foreground = p.Explanation == null ? _c.Rule : _c.Text, Margin = new Thickness(0, 0, 0, 6),
                });
                // A multi-step job: every step, with this one marked.
                if (p.Steps.Count > 0)
                {
                    var steps = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                    for (var i = 0; i < p.Steps.Count; i++)
                    {
                        var no = i + 1;
                        var state = p.Step == 0 ? "" : no < p.Step ? "  (done)" : no == p.Step ? "  (this step)" : "  (later)";
                        steps.Children.Add(new TextBlock
                        {
                            Text = $"{no}. {p.Steps[i]}{state}", TextWrapping = TextWrapping.Wrap, FontSize = 11,
                            Foreground = no == p.Step ? _c.Text : _c.Muted, FontWeight = no == p.Step ? FontWeights.SemiBold : FontWeights.Normal,
                        });
                    }
                    body.Children.Add(steps);
                }
                var m = System.Text.RegularExpressions.Regex.Match(p.Summary ?? "", @"add (\d+), modify (\d+), delete (\d+)");
                if (m.Success)
                {
                    var chips = new WrapPanel();
                    foreach (var (label, value) in new[] { ("Added", m.Groups[1].Value), ("Modified", m.Groups[2].Value), ("Deleted", m.Groups[3].Value) })
                        chips.Children.Add(Chip($"{label} {value}", label == "Deleted" && value != "0"));
                    body.Children.Add(chips);
                    var rest = (p.Summary ?? "").Substring(m.Index + m.Length).Trim(' ', '·');
                    if (rest.Length > 0) body.Children.Add(new TextBlock { Text = rest, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                }
                else body.Children.Add(new TextBlock { Text = p.Summary, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text });
                // Deletions and changes to shared data (parameters, types, families): shown plainly, before the buttons.
                if (p.Attention.Count > 0)
                {
                    var box = new StackPanel();
                    box.Children.Add(new TextBlock { Text = "Look at this before applying", FontWeight = FontWeights.SemiBold, Foreground = _c.Rule, FontSize = 11 });
                    foreach (var a in p.Attention) box.Children.Add(new TextBlock { Text = a, TextWrapping = TextWrapping.Wrap, Foreground = _c.Text, FontSize = 11 });
                    body.Children.Add(new Border { BorderBrush = _c.Rule, BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 0), Child = box });
                }
                // What exactly changes, per category, with names.
                if (p.Details != null && p.Details.Count > 0)
                {
                    var list = new StackPanel();
                    foreach (var (kind, cats) in p.Details)
                    {
                        if (cats is not JsonObject byCategory) continue;
                        list.Children.Add(new TextBlock { Text = kind switch { "added" => "Added", "modified" => "Changed", "deleted" => "Deleted", _ => kind }, FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = _c.Text, Margin = new Thickness(0, 4, 0, 0) });
                        foreach (var (cat, text) in byCategory)
                            list.Children.Add(new TextBlock { Text = $"{cat}: {text}", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = _c.Muted });
                    }
                    list.Children.Add(new TextBlock { Text = "Changed counts include elements Revit updates by itself (views, tags, joins).", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = _c.Muted, Margin = new Thickness(0, 4, 0, 0) });
                    body.Children.Add(new Expander { Header = "What exactly changes", Content = list, Margin = new Thickness(0, 6, 0, 0), Foreground = _c.Text, IsExpanded = p.Attention.Count > 0 });
                }
                foreach (var bytes in p.Images.Take(2))
                {
                    var bmp = Bitmap(bytes);
                    if (bmp == null) continue;
                    var img = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Click to enlarge" };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    img.MouseLeftButtonUp += (s, e) => Enlarge(bmp, p.Title);
                    body.Children.Add(new Border { BorderBrush = _c.Border, BorderThickness = new Thickness(1), Background = Brushes.White, Child = img, Margin = new Thickness(0, 6, 0, 0) });
                }
                body.Children.Add(new TextBlock { Text = "The model is unchanged until you apply.", Foreground = _c.Muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
                var buttons = new WrapPanel();
                var apply = ActionButton("Apply", true);
                var cancel = ActionButton("Cancel", false);
                var change = ActionButton("Ask for changes", false);
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
                var title = p.Title;
                change.Click += (s, e) => Copy($"Using ace-revit: about the previewed change \"{title}\": please change it so that ", "Copied. Paste into Claude and finish the sentence with what to change.");
                buttons.Children.Add(apply); buttons.Children.Add(cancel); buttons.Children.Add(change);
                if (p.ElementIds.Count > 0)
                {
                    var show = ActionButton("Show in model", false);
                    var ids = p.ElementIds;
                    show.Click += async (s, e) =>
                    {
                        try { await App.Dispatcher.EnqueueAsync("select_elements", new JsonObject { ["ids"] = new JsonArray(ids.Take(500).Select(i => (JsonNode)i).ToArray()), ["zoom"] = true }, TimeSpan.FromSeconds(30)); }
                        catch (Exception ex) { Toast($"Could not select: {ex.Message}"); }
                    };
                    buttons.Children.Add(show);
                }
                body.Children.Add(buttons);
                panel.Children.Add(card);
            }

            var decided = ActivityHub.Pending.Where(p => p.State != PendingState.Waiting && p.DecidedAt.HasValue).Take(6).ToList();
            if (decided.Count > 0)
            {
                panel.Children.Add(Section("Recent decisions"));
                foreach (var p in decided)
                {
                    var state = p.State switch
                    {
                        PendingState.AppliedByPanel => "Applied by you",
                        PendingState.AppliedByClaude => "Applied via Claude",
                        PendingState.Rejected => "Cancelled",
                        _ => "Failed",
                    };
                    panel.Children.Add(Row(p.State == PendingState.Failed ? _c.Rule : p.State == PendingState.Rejected ? _c.Border : _c.Text,
                        $"{p.DecidedAt:HH:mm}  {state}: {p.Title}", p.Outcome));
                }
            }
            return panel;
        }

        private static BitmapImage Bitmap(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new System.IO.MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void Enlarge(ImageSource img, string title)
        {
            var image = new Image { Source = img, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var w = new Window
            {
                Title = $"{Branding.Name} | {title}", Width = 1100, Height = 780, Background = Brushes.White,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new Border { Padding = new Thickness(10), Child = image },
            };
            w.Show();
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
                    ok = failed == 0 && r?["success"]?.GetValue<bool>() != false;
                    outcome = ok || failed > 0 ? $"{r?["applied"]} value(s) changed" + (failed > 0 ? $", {failed} failed" : "") : (r?["note"]?.ToString() ?? "Revit refused the change");
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

        // ---- Activity -----------------------------------------------------------------------------------

        private static bool IsChange(ActivityItem a) => a.Command is "execute_code" or "set_parameters" or "undo_last_claude_change" || (a.Title ?? "").StartsWith("Changed", StringComparison.OrdinalIgnoreCase);

        private UIElement RenderActivity()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            var filter = new CheckBox { Content = "Changes and failures only", IsChecked = _changesOnly, Foreground = _c.Text, Margin = new Thickness(0, 0, 0, 8) };
            filter.Click += (s, e) => { _changesOnly = filter.IsChecked == true; Render(); };
            panel.Children.Add(filter);
            var items = ActivityHub.Activity.Where(a => !_changesOnly || IsChange(a) || !a.Ok).Take(100).ToList();
            if (items.Count == 0)
                panel.Children.Add(Hint("Everything Claude does in this Revit session appears here: what it read, previewed and changed."));
            DateTime? day = null;
            foreach (var a in items)
            {
                if (day != a.Time.Date) { day = a.Time.Date; panel.Children.Add(Section(a.Time.Date == DateTime.Today ? "Today" : a.Time.ToString("ddd d MMM"))); }
                // Marker: black = a change, grey = a read or preview, red = failed.
                var marker = !a.Ok ? _c.Rule : IsChange(a) ? _c.Text : _c.Border;
                panel.Children.Add(Row(marker, $"{a.Time:HH:mm:ss}  {a.Title}{(a.Ok ? "" : "  (failed)")}{(a.Ms > 3000 ? $"  · {a.Ms / 1000.0:0.0} s" : "")}", a.Detail, bold: !a.Ok));
            }
            return panel;
        }

        private Border Row(Brush marker, string title, string detail, bool bold = false)
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = title, Foreground = _c.Text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(detail))
                row.Children.Add(new TextBlock { Text = detail, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) });
            return new Border { BorderBrush = marker, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 4, 0, 4), Margin = new Thickness(0, 1, 0, 3), Child = row };
        }

        // ---- Results ------------------------------------------------------------------------------------

        private UIElement RenderResults()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            var results = ActivityHub.Results;
            if (results.Count == 0)
            {
                panel.Children.Add(Hint("Elements from Claude's latest answer appear here. Click one to select and zoom to it in Revit."));
                return panel;
            }
            panel.Children.Add(new TextBlock { Text = ActivityHub.ResultsTitle, Foreground = _c.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(Labelled("Search", _resultSearch));
            var q = (_resultSearch.Text ?? "").Trim();
            var shown = results.Where(r => q.Length == 0 || $"{r.Id} {r.Label}".IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var selectAll = ActionButton($"Select {(q.Length > 0 ? "these" : "all")} {shown.Count} in Revit", true);
            selectAll.HorizontalAlignment = HorizontalAlignment.Left;
            selectAll.Click += async (s, e) => await SelectAsync(shown.Select(r => r.Id).ToArray(), zoom: false);
            panel.Children.Add(selectAll);
            foreach (var r in shown.Take(300))
            {
                var link = new Button
                {
                    Content = new TextBlock { Text = $"{r.Label}   ({r.Id})", TextWrapping = TextWrapping.Wrap },
                    HorizontalContentAlignment = HorizontalAlignment.Left, Background = _c.Card, BorderBrush = _c.Border, BorderThickness = new Thickness(0, 0, 0, 1),
                    Foreground = _c.Link, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(6, 5, 6, 5), ToolTip = "Select and zoom to this element",
                };
                var id = r.Id;
                link.Click += async (s, e) => await SelectAsync(new[] { id }, zoom: true);
                panel.Children.Add(link);
            }
            if (shown.Count > 300) panel.Children.Add(Hint($"... and {shown.Count - 300} more (use Select)."));
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

        // ---- Prompts ------------------------------------------------------------------------------------

        private UIElement RenderPrompts()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            var ctx = ActivityHub.Context;
            var now = Card();
            var nb = (StackPanel)now.Child;
            nb.Children.Add(Section("Right now in Revit"));
            nb.Children.Add(new TextBlock
            {
                Text = $"View: {ctx.View ?? "-"}{(ctx.ViewType != null ? $" ({ctx.ViewType})" : "")}\nSelection: {ctx.SelectionSummary ?? "nothing selected"}",
                Foreground = _c.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            });
            var copyContext = ActionButton("Copy this context for Claude", false);
            copyContext.HorizontalAlignment = HorizontalAlignment.Left;
            copyContext.Click += (s, e) => Copy(ContextText(), "Context copied. Paste it into Claude (Ctrl+V).");
            nb.Children.Add(copyContext);
            panel.Children.Add(now);

            panel.Children.Add(Labelled("Search prompts", _promptSearch));
            panel.Children.Add(new TextBlock { Text = "Click a prompt to copy it, then paste it into Claude Desktop.", Foreground = _c.Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 6) });
            var q = (_promptSearch.Text ?? "").Trim();
            var hasSelection = ctx.SelectedIds.Count > 0;
            foreach (var (group, prompts) in Prompts())
            {
                var matching = prompts.Where(p => q.Length == 0 || p.Item1.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matching.Count == 0) continue;
                var collapsed = q.Length == 0 && _collapsed.Contains(group);
                var head = new Button
                {
                    Content = $"{(collapsed ? "+" : "-")}  {group}  ({matching.Count})", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.SemiBold, Foreground = _c.Text, Padding = new Thickness(0, 6, 0, 4), Cursor = System.Windows.Input.Cursors.Hand,
                };
                var g = group;
                head.Click += (s, e) => { if (!_collapsed.Remove(g)) _collapsed.Add(g); Render(); };
                panel.Children.Add(head);
                if (collapsed) continue;
                foreach (var (label, prompt, needsSelection) in matching)
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

        private UIElement Labelled(string label, UIElement input)
        {
            var p = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var l = new TextBlock { Text = label, Foreground = _c.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(l, Dock.Left);
            p.Children.Add(l); p.Children.Add(input);
            return p;
        }

        private Border Chip(string text, bool key) => new Border
        {
            Background = key ? _c.Rule : _c.Background, BorderBrush = key ? _c.Rule : _c.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = key ? Brushes.White : _c.Text },
        };

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
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = new StackPanel(),
        };

        private Button ActionButton(string text, bool primary) => new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 6, 8, 2),
            Background = primary ? _c.Accent : _c.Card,
            Foreground = primary ? _c.OnAccent : _c.Text,
            BorderBrush = primary ? _c.Accent : _c.Border,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        private sealed class Palette
        {
            public Brush Background, Card, Border, Text, Muted, Accent, OnAccent, Link, Rule;

            public static Palette Current()
            {
                Brush b(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
                Brush c(Color col) => new SolidColorBrush(col);
                // Brand: black / white / greys, with the accent colour (ACE Red) for thin rules and the main action only.
                return Branding.IsDarkTheme
                    ? new Palette { Background = b("#212121"), Card = b("#414042"), Border = b("#A0A0A0"), Text = b("#E6E7E8"), Muted = b("#C4C4C4"),
                                    Accent = c(Branding.Accent), OnAccent = Brushes.White, Link = b("#E6E7E8"), Rule = c(Branding.Accent) }
                    : new Palette { Background = c(Branding.GreyLight), Card = Brushes.White, Border = b("#A0A0A0"), Text = c(Branding.Primary), Muted = c(Branding.GreyDark),
                                    Accent = c(Branding.Accent), OnAccent = Brushes.White, Link = c(Branding.Primary), Rule = c(Branding.Accent) };
            }
        }
    }
}
