using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace AceRevitMcp.Dashboard
{
    /// <summary>Bridge command: builds the insights (read-only), saves the HTML report, optionally shows it in Revit.</summary>
    internal static class DashboardCommands
    {
        internal static Insights Last { get; private set; }
        internal static string LastPath { get; private set; }

        public static JsonNode Insights(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var x = ModelInsights.Collect(doc, app.Application.Username);
            var path = DashboardHtml.Save(x, DashboardHtml.Render(x));
            Last = x; LastPath = path;
            if (Args.Bool(args, "show")) ReportWindow.ShowOrRefresh(app.MainWindowHandle, "Model insights", "get_model_insights", State);
            return ModelInsights.ToJson(x, path);
        }

        private static ReportWindow.State State()
        {
            var x = Last;
            if (x == null) return null;
            return new ReportWindow.State
            {
                Path = LastPath,
                Status = $"Score {x.Score}/100 ({x.Grade}), {x.Time:HH:mm}. Saved to Documents\\ACE Insights.",
                Findings = x.Checks.Where(c => c.Status != "ok" && c.Ids.Count > 0).OrderByDescending(c => c.Penalty)
                    .Select(c => ($"{c.Name} ({c.Count})", c.Ids.ToArray())).ToList(),
            };
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class DashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (commandData.Application.ActiveUIDocument?.Document == null)
                {
                    TaskDialog.Show("ACE Insights", "Open a model first.");
                    return Result.Cancelled;
                }
                DashboardCommands.Insights(commandData.Application, new JsonObject { ["show"] = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"Dashboard: {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// An ACE report shown inside Revit (modeless): the HTML report, Refresh (re-runs a bridge command),
    /// a list of findings whose elements can be selected and zoomed to, Open in browser, and Show file.
    /// One window per report kind.
    /// </summary>
    internal sealed class ReportWindow : Window
    {
        internal sealed class State
        {
            public string Path;
            public string Status;
            public List<(string Label, long[] Ids)> Findings = new List<(string, long[])>();
        }

        private static readonly Dictionary<string, ReportWindow> Open = new Dictionary<string, ReportWindow>();
        private readonly WebBrowser _browser = new WebBrowser();
        private readonly ComboBox _findings = new ComboBox { MinWidth = 300, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private readonly Func<State> _state;
        private string _path;

        public static void ShowOrRefresh(IntPtr owner, string title, string refreshCommand, Func<State> state)
        {
            if (!Open.TryGetValue(title, out var w))
            {
                w = new ReportWindow(title, refreshCommand, state);
                new WindowInteropHelper(w) { Owner = owner };
                w.Closed += (s, e) => Open.Remove(title);
                Open[title] = w;
                w.Show();
            }
            w.Load();
            w.Activate();
        }

        private ReportWindow(string title, string refreshCommand, Func<State> state)
        {
            _state = state;
            Title = $"{Branding.Name} | {title}";
            Width = 1240; Height = 860; MinWidth = 700; MinHeight = 400;
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

            Button Btn(string text, bool primary) => new Button
            {
                Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
                Background = primary ? red : Brushes.White, Foreground = primary ? Brushes.White : black,
                BorderBrush = primary ? red : grey, Cursor = System.Windows.Input.Cursors.Hand,
            };

            var refresh = Btn("Refresh", true);
            refresh.Click += async (s, e) =>
            {
                refresh.IsEnabled = false; _status.Text = "Reading the model...";
                try { await App.Dispatcher.EnqueueAsync(refreshCommand, new JsonObject(), TimeSpan.FromMinutes(10)); Load(); }
                catch (Exception ex) { _status.Text = "Could not refresh: " + ex.Message; }
                finally { refresh.IsEnabled = true; }
            };
            var select = Btn("Select in Revit", false);
            select.Click += async (s, e) =>
            {
                if (!(_findings.SelectedItem is ComboBoxItem item) || !(item.Tag is long[] ids) || ids.Length == 0) { _status.Text = "Choose a finding first."; return; }
                try
                {
                    var r = await App.Dispatcher.EnqueueAsync("select_elements",
                        new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["zoom"] = true }, TimeSpan.FromSeconds(60));
                    _status.Text = $"Selected {r?["selected"]} element(s) in Revit.";
                }
                catch (Exception ex) { _status.Text = "Could not select: " + ex.Message; }
            };
            var browser = Btn("Open in browser", false);
            browser.Click += (s, e) => OpenFile(_path, select: false);
            var folder = Btn("Show file", false);
            folder.Click += (s, e) => OpenFile(_path, select: true);

            var bar = new DockPanel { Background = light, LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            left.Children.Add(refresh);
            left.Children.Add(new TextBlock { Text = "Finding:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = black });
            left.Children.Add(_findings);
            left.Children.Add(select);
            left.Children.Add(browser);
            left.Children.Add(folder);
            left.Children.Add(_status);
            bar.Children.Add(left);
            var rule = new Border { Height = 2, Background = red };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(rule, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(rule);
            root.Children.Add(_browser);
            Content = root;
        }

        private void Load()
        {
            var st = _state();
            if (st?.Path == null) return;
            _path = st.Path;
            try { _browser.Navigate(new Uri(st.Path)); }
            catch (Exception ex) { _status.Text = "Could not show the report: " + ex.Message; }
            _findings.Items.Clear();
            foreach (var (label, ids) in st.Findings.Where(f => f.Ids != null && f.Ids.Length > 0))
                _findings.Items.Add(new ComboBoxItem { Content = label, Tag = ids });
            if (_findings.Items.Count > 0) _findings.SelectedIndex = 0;
            _status.Text = st.Status;
        }

        private void OpenFile(string path, bool select)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (select) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { _status.Text = "Could not open: " + ex.Message; }
        }
    }
}
