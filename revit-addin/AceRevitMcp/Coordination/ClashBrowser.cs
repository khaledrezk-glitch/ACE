using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Binding = System.Windows.Data.Binding;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Grid = System.Windows.Controls.Grid;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;
using WpfColor = System.Windows.Media.Color;

namespace AceRevitMcp.Coordination
{
    /// <summary>One row of the Clash Browser (properties, so the list can bind to them).</summary>
    public sealed class ClashRow
    {
        public string Issue { get; set; }
        public string Status { get; set; }
        public string Level { get; set; }
        public string ThisSide { get; set; }
        public string OtherSide { get; set; }
        public string Depth { get; set; }
        public string Responsible { get; set; }
        public string Cause { get; set; }
        internal string Key, Test, SourceA, SourceB, SearchText;
        internal long[] HostIds = new long[0];
        internal double DepthMm;
        internal int IssueIndex = int.MaxValue;   // order of the issue (largest first); MaxValue = not in an open issue
        internal bool Open => Status == "new" || Status == "active" || Status == "reopened";
    }

    /// <summary>
    /// The Clash Browser (after Navisworks Clash Detective and ACC Model Coordination): choose the model to compare
    /// with, run the test, browse the clashes grouped by issue, and click one to see it alone in the ACE Clash View:
    /// everything else dimmed, the two elements in their model colours, zoomed to the intersection.
    /// </summary>
    internal sealed class ClashBrowser : Window
    {
        private static ClashBrowser _open;
        private readonly ComboBox _primary = new ComboBox { MinWidth = 220, Margin = new Thickness(6, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _with = new ComboBox { MinWidth = 240, Margin = new Thickness(6, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private List<Clash> _clashes = new List<Clash>();
        private readonly ComboBox _filter = new ComboBox { Width = 120, Margin = new Thickness(6, 0, 10, 0) };
        private readonly TextBox _search = new TextBox { Width = 200, Margin = new Thickness(6, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ListView _list = new ListView { BorderThickness = new Thickness(0) };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _legendModels = new StackPanel { Orientation = Orientation.Horizontal };
        private List<ClashRow> _rows = new List<ClashRow>();
        private ListCollectionView _view;
        private readonly System.Windows.Threading.DispatcherTimer _typing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private string _host;
        private List<(string Name, string Label)> _models = new List<(string, string)>();
        private bool _busy, _loading;

        /// <summary>Opens (or brings back) the browser. Call in a Revit API context: the model list is read here.</summary>
        internal static void ShowFor(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            var models = Clashes.Sources(app, doc).Where(s => !s.IsHost)
                .Select(s => (s.Name, $"{s.Name} ({s.Discipline}, {s.Relation})")).ToList();

            if (_open == null)
            {
                _open = new ClashBrowser();
                new WindowInteropHelper(_open) { Owner = app.MainWindowHandle };
                _open.Closed += (s, e) => _open = null;
                _open.Show();
            }
            _open._host = doc.Title;
            _open._models = models;
            _open.FillModels();
            _open.Reload();
            _open.Activate();
        }

        private ClashBrowser()
        {
            Title = $"{Branding.Name} | Clash Browser";
            Width = 1180; Height = 720; MinWidth = 760; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily(Branding.FontFamily + ", Arial");
            FontSize = 13;
            Background = Brushes.White;
            ShowInTaskbar = true;

            var black = new SolidColorBrush(Branding.Primary);
            var grey = new SolidColorBrush(Branding.GreyDark);
            var light = new SolidColorBrush(Branding.GreyLight);
            var red = new SolidColorBrush(Branding.Accent);
            _status.Foreground = grey;

            Button Btn(string text, bool primary, RoutedEventHandler click)
            {
                var b = new Button
                {
                    Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
                    Background = primary ? red : Brushes.White, Foreground = primary ? Brushes.White : black,
                    BorderBrush = primary ? red : grey, Cursor = System.Windows.Input.Cursors.Hand,
                };
                b.Click += click;
                return b;
            }
            TextBlock Label(string t) => new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Foreground = black };
            StackPanel Swatch(WpfColor c, TextBlock text)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
                p.Children.Add(new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                text.VerticalAlignment = VerticalAlignment.Center; text.Foreground = black;
                p.Children.Add(text);
                return p;
            }

            // Row 1: compare with, run, show models, reset.
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 6) };
            top.Children.Add(Label("Primary:"));
            top.Children.Add(_primary);
            top.Children.Add(Label("Compare with:"));
            top.Children.Add(_with);
            top.Children.Add(Btn("Run clash test", true, async (s, e) => await Run()));
            top.Children.Add(Btn("Show both models", false, async (s, e) => await Call("clash_view", new JsonObject { ["primary_model"] = PrimaryModel(), ["with_model"] = WithModel() }, r => "Both models in their colours in the ACE Clash View.")));
            top.Children.Add(Btn("Coordination report", false, async (s, e) => await Call("coordination_report", new JsonObject { ["open"] = true }, r => $"Report saved: {r?["htmlReport"]}")));

            // Row 2: legend.
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            legend.Children.Add(_legendModels);
            legend.Children.Add(Swatch(ClashView.HitColour, new TextBlock { Text = "Intersection" }));
            legend.Children.Add(new TextBlock { Text = "Click a colour to change it. Click a clash: the rest is dimmed and the view zooms to where they meet.", Foreground = grey, VerticalAlignment = VerticalAlignment.Center });

            // Row 3: filters.
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            foreach (var f in new[] { "Open", "New", "Reopened", "Approved", "Resolved", "All" }) _filter.Items.Add(new ComboBoxItem { Content = f });
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += (s, e) => Apply();
            // Typing filters after a short pause, not on every keystroke.
            _typing.Tick += (s, e) => { _typing.Stop(); Apply(); };
            _search.TextChanged += (s, e) => { _typing.Stop(); _typing.Start(); };
            filters.Children.Add(Label("Show:"));
            filters.Children.Add(_filter);
            filters.Children.Add(Label("Search:"));
            filters.Children.Add(_search);

            // The list, grouped by issue.
            var grid = new GridView();
            void Col(string header, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(path), Width = width });
            Col("Status", nameof(ClashRow.Status), 80);
            Col("Level", nameof(ClashRow.Level), 70);
            Col("Primary", nameof(ClashRow.ThisSide), 250);
            Col("Compared with", nameof(ClashRow.OtherSide), 250);
            Col("Depth", nameof(ClashRow.Depth), 70);
            Col("Responsible", nameof(ClashRow.Responsible), 150);
            Col("Cause", nameof(ClashRow.Cause), 220);
            _list.View = grid;
            _list.GroupStyle.Add(new GroupStyle
            {
                HeaderTemplate = GroupHeader(black),
            });
            _list.SelectionMode = SelectionMode.Single;
            // Grouped lists only virtualise when asked to: thousands of clashes stay fast.
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(_list, true);
            _list.SelectionChanged += async (s, e) => { if (!_loading && _list.SelectedItem is ClashRow r) await Focus(r); };

            // Bottom: actions.
            var bottom = new DockPanel { Background = light, LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            actions.Children.Add(Btn("Previous", false, (s, e) => Move(-1)));
            actions.Children.Add(Btn("Next", false, (s, e) => Move(1)));
            actions.Children.Add(Btn("Approve", false, async (s, e) => await SetStatus("approved")));
            actions.Children.Add(Btn("Mark active", false, async (s, e) => await SetStatus("active")));
            actions.Children.Add(Btn("Select in Revit", false, async (s, e) => await SelectInRevit()));
            actions.Children.Add(Btn("Reset view", false, async (s, e) => await Call("clash_view", new JsonObject(), r => "Highlight and section box cleared.")));
            actions.Children.Add(_status);
            bottom.Children.Add(actions);

            var head = new StackPanel { Background = light };
            head.Children.Add(top); head.Children.Add(legend); head.Children.Add(filters);
            var rule = new Border { Height = 2, Background = red };
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(rule, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(rule); root.Children.Add(bottom); root.Children.Add(_list);
            Content = root;
        }

        private static DataTemplate GroupHeader(Brush fg)
        {
            var t = new DataTemplate();
            var f = new FrameworkElementFactory(typeof(TextBlock));
            f.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            f.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            f.SetValue(TextBlock.ForegroundProperty, fg);
            f.SetValue(TextBlock.MarginProperty, new Thickness(4, 10, 0, 4));
            t.VisualTree = f;
            return t;
        }

        /// <summary>The primary model's name: null = this model.</summary>
        private string PrimaryModel() => (_primary.SelectedItem as ComboBoxItem)?.Tag as string;
        private string PrimaryName() => PrimaryModel() ?? _host;
        private string WithModel() => (_with.SelectedItem as ComboBoxItem)?.Tag as string;

        private void FillModels()
        {
            _primary.SelectionChanged -= PrimaryChanged; _with.SelectionChanged -= WithChanged;
            var keepPrimary = PrimaryModel() ?? ClashView.Session(_host).Primary;
            _primary.Items.Clear();
            _primary.Items.Add(new ComboBoxItem { Content = $"This model ({_host})", Tag = null });
            foreach (var (name, label) in _models) _primary.Items.Add(new ComboBoxItem { Content = label + "  [BIM manager: link vs link]", Tag = name });
            _primary.SelectedIndex = Math.Max(0, _primary.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as string) == keepPrimary));
            FillWith();
            _primary.SelectionChanged += PrimaryChanged; _with.SelectionChanged += WithChanged;
            UpdateLegend();
        }

        /// <summary>The secondary list: every model except the primary (this model too, when a link is the primary).</summary>
        private void FillWith()
        {
            var keep = WithModel() ?? ClashView.Session(_host).With;
            var primary = PrimaryModel();
            _with.Items.Clear();
            _with.Items.Add(new ComboBoxItem { Content = primary == null ? "All loaded links" : "All other models", Tag = null });
            if (primary != null) _with.Items.Add(new ComboBoxItem { Content = $"This model ({_host})", Tag = _host });
            foreach (var (name, label) in _models.Where(m => m.Name != primary)) _with.Items.Add(new ComboBoxItem { Content = label, Tag = name });
            _with.SelectedIndex = Math.Max(0, _with.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as string) == keep));
        }

        private void PrimaryChanged(object s, SelectionChangedEventArgs e)
        {
            _with.SelectionChanged -= WithChanged;
            ClashView.Session(_host).Primary = PrimaryModel();
            FillWith();
            ClashView.Session(_host).With = WithModel();
            _with.SelectionChanged += WithChanged;
            UpdateLegend(); Build(_clashes);
        }

        private void WithChanged(object s, SelectionChangedEventArgs e) { ClashView.Session(_host).With = WithModel(); UpdateLegend(); Build(_clashes); }

        /// <summary>One colour square per model shown (the primary, and the compared model or every other link); click to choose its colour.</summary>
        private void UpdateLegend()
        {
            _legendModels.Children.Clear();
            var primary = PrimaryName();
            var others = new List<string> { _host }.Concat(_models.Select(m => m.Name)).Where(n => n != primary).ToList();
            var shown = new List<(string Name, string Role)> { (primary, "Primary") };
            if (WithModel() != null) shown.Add((WithModel(), "Compared"));
            else shown.AddRange(_models.Select(m => m.Name).Where(n => n != primary).Select(n => (n, "Link")));
            foreach (var (name, role) in shown)
            {
                var colour = ClashColours.For(name, primary, WithModel(), others);
                var chip = new Button
                {
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 0, 16, 0),
                    Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"{role}: {name}. Click to choose its colour.",
                };
                var p = new StackPanel { Orientation = Orientation.Horizontal };
                p.Children.Add(new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(colour), Stroke = Brushes.Black, StrokeThickness = 0.5, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                p.Children.Add(new TextBlock { Text = $"{role}: {Dashboard.DashboardHtml.Trim(name, 40)}", VerticalAlignment = VerticalAlignment.Center });
                chip.Content = p;
                var menu = new ContextMenu();
                foreach (var (label, c) in ClashColours.Palette)
                {
                    var item = new MenuItem { Header = label, Icon = new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(c) } };
                    item.Click += async (s, e) => { ClashColours.Set(name, c); UpdateLegend(); await Repaint(); };
                    menu.Items.Add(item);
                }
                var reset = new MenuItem { Header = "Default colour" };
                reset.Click += async (s, e) => { ClashColours.Reset(name); UpdateLegend(); await Repaint(); };
                menu.Items.Add(new Separator()); menu.Items.Add(reset);
                chip.ContextMenu = menu;
                chip.Click += (s, e) => { menu.PlacementTarget = chip; menu.IsOpen = true; };
                _legendModels.Children.Add(chip);
            }
        }

        /// <summary>Redraws the ACE Clash View with the new colours (the focused clash, or both models).</summary>
        private async System.Threading.Tasks.Task Repaint()
        {
            var focused = ClashView.Session(_host).FocusedKey;
            if (focused != null) await Call("focus_clash", new JsonObject { ["key"] = focused }, r => "Colours updated.");
            else await Call("clash_view", new JsonObject { ["primary_model"] = PrimaryModel(), ["with_model"] = WithModel() }, r => "Colours updated.");
        }

        /// <summary>Reads the clash results (the last run, else the stored ones) into rows.</summary>
        private void Reload()
        {
            var clashes = StatusStore.For(_host)?.Clashes;
            if (clashes == null)
            {
                _rows = new List<ClashRow>();
                _status.Text = "Loading results...";
                _ = LoadStored();
                return;
            }
            Build(clashes);
        }

        private async System.Threading.Tasks.Task LoadStored()
        {
            try
            {
                await App.Dispatcher.EnqueueAsync("clash_results", new JsonObject(), TimeSpan.FromSeconds(60));
                var loaded = StatusStore.For(_host)?.Clashes;
                if (loaded != null && loaded.Count > 0) Build(loaded);
                else { Build(new List<Clash>()); _status.Text = "No clash results yet: choose a model and click Run clash test."; }
            }
            catch (Exception ex) { _status.Text = "Could not load results: " + ex.Message; }
        }

        private void Build(List<Clash> clashes)
        {
            _clashes = clashes ?? new List<Clash>();
            var primary = PrimaryName();
            // The issues of the published results are already computed by the status store.
            var status = StatusStore.For(_host);
            var issues = status != null && ReferenceEquals(status.Clashes, _clashes) ? status.ClashIssues : ClashLogic.Issues(_clashes);
            var issueOf = new Dictionary<string, (int Index, string Title)>();
            var n = 0;
            foreach (var i in issues) { n++; foreach (var c in i.Clashes) issueOf[c.Key] = (n, $"Issue {n}: {Dashboard.DashboardHtml.Trim(i.Title, 110)}  |  {i.Responsible}"); }
            _rows = _clashes.Select(c =>
            {
                var thisSide = c.SourceA == primary || c.SourceB != primary;   // show the primary model's element first
                string Side(string cat, string name, long id, string src) => $"{cat}: {name} (id {id}{(src != _host ? ", " + src : "")})";
                var a = Side(c.CatA, c.NameA, c.IdA, c.SourceA); var b = Side(c.CatB, c.NameB, c.IdB, c.SourceB);
                var inIssue = issueOf.TryGetValue(c.Key, out var iss);
                var row = new ClashRow
                {
                    Key = c.Key, Test = c.Test, SourceA = c.SourceA, SourceB = c.SourceB,
                    IssueIndex = inIssue ? iss.Index : int.MaxValue,
                    Issue = inIssue ? iss.Title : (ClashLogic.IsOpen(c) ? "Other clashes" : $"{Cap(c.Status)} clashes"),
                    Status = c.Reopened > 0 && c.Status == "new" ? "reopened" : c.Status,
                    Level = c.Level, ThisSide = thisSide ? a : b, OtherSide = thisSide ? b : a,
                    Depth = c.Kind == "clearance" ? $"gap {c.DepthMm:0}" : $"{c.DepthMm:0} mm", DepthMm = c.DepthMm,
                    Responsible = c.Responsible, Cause = c.CausedBy != null ? $"{c.CausedBy}: {c.Cause}" : c.Cause,
                    HostIds = new[] { c.SourceA == _host ? c.IdA : 0, c.SourceB == _host ? c.IdB : 0 }.Where(x => x > 0).ToArray(),
                };
                row.SearchText = $"{row.Issue} {row.ThisSide} {row.OtherSide} {row.Level} {row.Responsible} {row.Cause}";
                return row;
            }).OrderBy(r => r.IssueIndex).ThenByDescending(r => r.DepthMm).ToList();
            // One grouped view; filters only change its Filter (no rebuild per keystroke).
            _view = new ListCollectionView(_rows);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClashRow.Issue)));
            _loading = true;
            _list.ItemsSource = _view;
            _loading = false;
            Apply();
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        private void Apply()
        {
            var f = (_filter.SelectedItem as ComboBoxItem)?.Content as string ?? "Open";
            var q = (_search.Text ?? "").Trim();
            var with = WithModel();
            var primary = PrimaryName();
            bool Pair(ClashRow r) => with == null
                ? (r.SourceA == primary || r.SourceB == primary)
                : ((r.SourceA == primary && r.SourceB == with) || (r.SourceA == with && r.SourceB == primary) || (primary == with && r.SourceA == primary && r.SourceB == primary));
            bool Show(ClashRow r) =>
                (f == "All" || (f == "Open" && r.Open) || string.Equals(r.Status, f, StringComparison.OrdinalIgnoreCase)) &&
                Pair(r) && (q.Length == 0 || r.SearchText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (_view == null) return;
            _loading = true;
            _view.Filter = o => Show((ClashRow)o);
            _loading = false;
            var shown = _rows.Where(Show).ToList();
            var issues = shown.Where(r => r.IssueIndex != int.MaxValue).Select(r => r.IssueIndex).Distinct().Count();
            _status.Text = $"{shown.Count} clashes in {issues} issues shown ({_rows.Count} in total).";
        }

        // ---- actions ------------------------------------------------------------------------------------------------

        private async System.Threading.Tasks.Task Call(string command, JsonObject args, Func<JsonNode, string> done, TimeSpan? timeout = null)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _status.Text = "Working in Revit...";
                var r = await App.Dispatcher.EnqueueAsync(command, args, timeout ?? TimeSpan.FromMinutes(2));
                _status.Text = done(r);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _busy = false; }
        }

        private async System.Threading.Tasks.Task Run()
        {
            await Call("run_clash_test", new JsonObject { ["test"] = "all", ["primary_model"] = PrimaryModel(), ["with_model"] = WithModel() },
                r => "Clash test finished.", TimeSpan.FromMinutes(15));
            var results = StatusStore.For(_host)?.Clashes;
            if (results != null) Build(results);
        }

        private async System.Threading.Tasks.Task Focus(ClashRow r)
        {
            await Call("focus_clash", new JsonObject { ["key"] = r.Key }, res => $"{r.Level}: {res?["a"]}  x  {res?["b"]}. Intersection {res?["intersectionMm"]} mm.");
        }

        private void Move(int step)
        {
            var items = _list.Items.Cast<object>().ToList();
            if (items.Count == 0) return;
            var i = _list.SelectedIndex < 0 ? (step > 0 ? -1 : items.Count) : _list.SelectedIndex;
            var next = Math.Max(0, Math.Min(items.Count - 1, i + step));
            _list.SelectedIndex = next;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private async System.Threading.Tasks.Task SetStatus(string status)
        {
            if (!(_list.SelectedItem is ClashRow r)) { _status.Text = "Choose a clash first."; return; }
            await Call("set_clash_status", new JsonObject { ["test"] = r.Test, ["keys"] = new JsonArray((JsonNode)r.Key), ["status"] = status },
                res => $"Marked {status}.");
            r.Status = status;
            var index = _list.SelectedIndex;
            _loading = true;
            _view?.Refresh();   // re-filter (an approved clash leaves the Open list) without rebuilding the rows
            _loading = false;
            _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        }

        private async System.Threading.Tasks.Task SelectInRevit()
        {
            if (!(_list.SelectedItem is ClashRow r)) { _status.Text = "Choose a clash first."; return; }
            if (r.HostIds.Length == 0) { _status.Text = "Both elements are in linked models: they cannot be selected here (shown in the ACE Clash View instead)."; return; }
            await Call("select_elements", new JsonObject { ["ids"] = new JsonArray(r.HostIds.Select(i => (JsonNode)i).ToArray()) },
                res => $"Selected {res?["selected"]} element(s) of this model.");
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ClashBrowserCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (commandData.Application.ActiveUIDocument?.Document == null) { TaskDialog.Show("ACE Clash Browser", "Open a model first."); return Result.Cancelled; }
                ClashBrowser.ShowFor(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Clash Browser: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }
}
