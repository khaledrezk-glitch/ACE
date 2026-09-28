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
            if (Args.Bool(args, "show")) DashboardWindow.ShowOrRefresh(app.MainWindowHandle);
            return ModelInsights.ToJson(x, path);
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

    /// <summary>Shows the HTML dashboard inside Revit (modeless), with refresh, select-in-Revit and open-in-browser.</summary>
    internal sealed class DashboardWindow : Window
    {
        private static DashboardWindow _open;
        private readonly WebBrowser _browser = new WebBrowser();
        private readonly ComboBox _checks = new ComboBox { MinWidth = 280, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        public static void ShowOrRefresh(IntPtr owner)
        {
            if (_open == null)
            {
                _open = new DashboardWindow();
                new WindowInteropHelper(_open) { Owner = owner };
                _open.Closed += (s, e) => _open = null;
                _open.Show();
            }
            _open.Load();
            _open.Activate();
        }

        private DashboardWindow()
        {
            Title = $"{Branding.Name} | Model insights";
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

            Button Btn(string text, bool primary)
            {
                return new Button
                {
                    Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
                    Background = primary ? red : Brushes.White, Foreground = primary ? Brushes.White : black,
                    BorderBrush = primary ? red : grey, Cursor = System.Windows.Input.Cursors.Hand,
                };
            }

            var refresh = Btn("Refresh", true);
            refresh.Click += async (s, e) =>
            {
                refresh.IsEnabled = false; _status.Text = "Reading the model...";
                try { await App.Dispatcher.EnqueueAsync("get_model_insights", new JsonObject(), TimeSpan.FromMinutes(5)); Load(); }
                catch (Exception ex) { _status.Text = "Could not refresh: " + ex.Message; }
                finally { refresh.IsEnabled = true; }
            };
            var select = Btn("Select in Revit", false);
            select.Click += async (s, e) =>
            {
                if (!(_checks.SelectedItem is ComboBoxItem item) || !(item.Tag is long[] ids) || ids.Length == 0) { _status.Text = "Choose a finding first."; return; }
                try
                {
                    var r = await App.Dispatcher.EnqueueAsync("select_elements",
                        new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["zoom"] = true }, TimeSpan.FromSeconds(60));
                    _status.Text = $"Selected {r?["selected"]} element(s) in Revit.";
                }
                catch (Exception ex) { _status.Text = "Could not select: " + ex.Message; }
            };
            var browser = Btn("Open in browser", false);
            browser.Click += (s, e) => Open(DashboardCommands.LastPath, select: false);
            var folder = Btn("Show file", false);
            folder.Click += (s, e) => Open(DashboardCommands.LastPath, select: true);

            var bar = new DockPanel { Background = light, LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            left.Children.Add(refresh);
            left.Children.Add(new TextBlock { Text = "Finding:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = black });
            left.Children.Add(_checks);
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
            var x = DashboardCommands.Last;
            var path = DashboardCommands.LastPath;
            if (x == null || path == null) return;
            try { _browser.Navigate(new Uri(path)); }
            catch (Exception ex) { _status.Text = "Could not show the report: " + ex.Message; }
            _checks.Items.Clear();
            foreach (var c in x.Checks.Where(c => c.Status != "ok" && c.Ids.Count > 0).OrderByDescending(c => c.Penalty))
                _checks.Items.Add(new ComboBoxItem { Content = $"{c.Name} ({c.Count})", Tag = c.Ids.ToArray() });
            if (_checks.Items.Count > 0) _checks.SelectedIndex = 0;
            _status.Text = $"Score {x.Score}/100 ({x.Grade}), {x.Time:HH:mm}. Saved to Documents\\ACE Insights.";
        }

        private void Open(string path, bool select)
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
