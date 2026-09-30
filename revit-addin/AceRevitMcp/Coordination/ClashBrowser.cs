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
        internal string Key, Test, SourceA, SourceB;
        internal long[] HostIds = new long[0];
        internal double DepthMm;
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
        private readonly TextBlock _legendHost = new TextBlock(), _legendOther = new TextBlock();
        private List<ClashRow> _rows = new List<ClashRow>();
        private string _host;
        private List<(string Name, string Label)> _models = new List<(string, string)>();
        private bool _busy, _loading;

        /// <summary>Opens (or brings back) the browser. Call in a Revit API context: the model list is read here.</summary>
        internal static void ShowFor(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            var models = Clashes.Sources(app, doc).Where(s => !s.IsHost)
                .Select(s => (s.Name, $"{s.Name} ({s.Discipline}, {s.Relation})")).ToList();
            if (_open != null && _open._host != doc.Title) ClashView.PrimaryModel = null;   // another model: start from it as the primary
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
            top.Children.Add(Label("Primary (red):"));
            top.Children.Add(_primary);
            top.Children.Add(Label("Compare with (green):"));
            top.Children.Add(_with);
            top.Children.Add(Btn("Run clash test", true, async (s, e) => await Run()));
            top.Children.Add(Btn("Show both models", false, async (s, e) => await Call("clash_view", new JsonObject { ["primary_model"] = PrimaryModel(), ["with_model"] = WithModel() }, r => "Primary red, secondary green in the ACE Clash View.")));
            top.Children.Add(Btn("Coordination report", false, async (s, e) => await Call("coordination_report", new JsonObject { ["open"] = true }, r => $"Report saved: {r?["htmlReport"]}")));

            // Row 2: legend.
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            legend.Children.Add(Swatch(ClashView.PrimaryColour, _legendHost));
            legend.Children.Add(Swatch(ClashView.SecondaryColour, _legendOther));
            legend.Children.Add(Swatch(ClashView.HitColour, new TextBlock { Text = "Intersection" }));
            legend.Children.Add(new TextBlock { Text = "Click a clash: everything else is dimmed and the view zooms to where the elements meet.", Foreground = grey, VerticalAlignment = VerticalAlignment.Center });

            // Row 3: filters.
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            foreach (var f in new[] { "Open", "New", "Reopened", "Approved", "Resolved", "All" }) _filter.Items.Add(new ComboBoxItem { Content = f });
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += (s, e) => Apply();
            _search.TextChanged += (s, e) => Apply();
            filters.Children.Add(Label("Show:"));
            filters.Children.Add(_filter);
            filters.Children.Add(Label("Search:"));
            filters.Children.Add(_search);

            // The list, grouped by issue.
            var grid = new GridView();
            void Col(string header, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(path), Width = width });
            Col("Status", nameof(ClashRow.Status), 80);
            Col("Level", nameof(ClashRow.Level), 70);
            Col("Primary (red)", nameof(ClashRow.ThisSide), 250);
            Col("Secondary (green)", nameof(ClashRow.OtherSide), 250);
            Col("Depth", nameof(ClashRow.Depth), 70);
            Col("Responsible", nameof(ClashRow.Responsible), 150);
            Col("Cause", nameof(ClashRow.Cause), 220);
            _list.View = grid;
            _list.GroupStyle.Add(new GroupStyle
            {
                HeaderTemplate = GroupHeader(black),
            });
            _list.SelectionMode = SelectionMode.Single;
            _list.SelectionChanged += async (s, e) => { if (!_loading && _list.SelectedItem is ClashRow r) await Focus(r); };

            // Bottom: actions.
            var bottom = new DockPanel { Background = light, LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            actions.Children.Add(Btn("Previous", false, (s, e) => Move(-1)));
            actions.Children.Add(Btn("Next", false, (s, e) => Move(1)));
            actions.Children.Add(Btn("Approve", false, async (s, e) => await SetStatus("approved")));
            actions.Children.Add(Btn("Mark active", false, async (s, e) => await SetStatus("active")));
            actions.Children.Add(Btn("Select in Revit", false, async (s, e) => await SelectInRevit()));
            actions.Children.Add(Btn("Reset view", false, async (s, e) => await Call("reset_clash_view", new JsonObject(), r => "Highlight and section box cleared.")));
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
            var keepPrimary = PrimaryModel() ?? ClashView.PrimaryModel;
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
            var keep = WithModel() ?? ClashView.WithModel;
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
            ClashView.PrimaryModel = PrimaryModel();
            FillWith();
            ClashView.WithModel = WithModel();
            _with.SelectionChanged += WithChanged;
            UpdateLegend(); Build(_clashes);
        }

        private void WithChanged(object s, SelectionChangedEventArgs e) { ClashView.WithModel = WithModel(); UpdateLegend(); Build(_clashes); }

        private void UpdateLegend()
        {
            _legendHost.Text = $"Primary: {PrimaryName()}";
            _legendOther.Text = WithModel() != null ? $"Secondary: {WithModel()}" : "Secondary: other models";
        }

        /// <summary>Reads the clash results (the last run, else the stored ones) into rows.</summary>
        private void Reload()
        {
            List<Clash> clashes = Clashes.Last != null && Clashes.LastHost == _host ? Clashes.Last : null;
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
                if (Clashes.Last != null) Build(Clashes.Last);
                else { _rows = new List<ClashRow>(); Apply(); _status.Text = "No clash results yet: choose a model and click Run clash test."; }
            }
            catch (Exception ex) { _status.Text = "Could not load results: " + ex.Message; }
        }

        private void Build(List<Clash> clashes)
        {
            _clashes = clashes ?? new List<Clash>();
            var primary = PrimaryName();
            var issues = ClashLogic.Issues(clashes);
            var issueOf = new Dictionary<string, string>();
            var n = 0;
            foreach (var i in issues) { n++; foreach (var c in i.Clashes) issueOf[c.Key] = $"Issue {n}: {DashboardHelpers.Trim(i.Title, 110)}  |  {i.Responsible}"; }
            _rows = clashes.Select(c =>
            {
                var thisSide = c.SourceA == primary || c.SourceB != primary;   // show the primary model's element first
                string Side(string cat, string name, long id, string src) => $"{cat}: {name} (id {id}{(src != _host ? ", " + src : "")})";
                var a = Side(c.CatA, c.NameA, c.IdA, c.SourceA); var b = Side(c.CatB, c.NameB, c.IdB, c.SourceB);
                return new ClashRow
                {
                    Key = c.Key, Test = c.Test, SourceA = c.SourceA, SourceB = c.SourceB,
                    Issue = issueOf.TryGetValue(c.Key, out var iss) ? iss : (ClashLogic.IsOpen(c) ? "Other clashes" : $"{Cap(c.Status)} clashes"),
                    Status = c.Reopened > 0 && c.Status == "new" ? "reopened" : c.Status,
                    Level = c.Level, ThisSide = thisSide ? a : b, OtherSide = thisSide ? b : a,
                    Depth = c.Kind == "clearance" ? $"gap {c.DepthMm:0}" : $"{c.DepthMm:0} mm", DepthMm = c.DepthMm,
                    Responsible = c.Responsible, Cause = c.CausedBy != null ? $"{c.CausedBy}: {c.Cause}" : c.Cause,
                    HostIds = new[] { c.SourceA == _host ? c.IdA : 0, c.SourceB == _host ? c.IdB : 0 }.Where(x => x > 0).ToArray(),
                };
            }).ToList();
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
            var rows = _rows.Where(r =>
                (f == "All" || (f == "Open" && (r.Status == "new" || r.Status == "active" || r.Status == "reopened")) || string.Equals(r.Status, f, StringComparison.OrdinalIgnoreCase)) &&
                Pair(r) &&
                (q.Length == 0 || $"{r.Issue} {r.ThisSide} {r.OtherSide} {r.Level} {r.Responsible} {r.Cause}".IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(r => r.Issue.StartsWith("Issue ") ? int.Parse(new string(r.Issue.Substring(6).TakeWhile(char.IsDigit).ToArray())) : int.MaxValue)
                .ThenByDescending(r => r.DepthMm).ToList();
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClashRow.Issue)));
            _loading = true;
            _list.ItemsSource = view;
            _loading = false;
            var issues = rows.Select(r => r.Issue).Distinct().Count(i => i.StartsWith("Issue "));
            _status.Text = $"{rows.Count} clashes in {issues} issues shown ({_rows.Count} in total).";
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
            if (Clashes.Last != null) Build(Clashes.Last);
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
            Apply();
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

    internal static class DashboardHelpers
    {
        internal static string Trim(string s, int max) => Dashboard.DashboardHtml.Trim(s, max);
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
