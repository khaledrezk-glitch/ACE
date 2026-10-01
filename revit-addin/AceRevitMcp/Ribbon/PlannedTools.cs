using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using Autodesk.Revit.UI;
using WpfGrid = System.Windows.Controls.Grid;

namespace AceRevitMcp.Ribbon
{
    /// <summary>A planned ribbon tool: opens a card explaining what it will do (and what Claude can do today).</summary>
    public abstract class PlannedFeatureCommand : IExternalCommand
    {
        protected abstract string Key { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { RoadmapWindows.Show(RoadmapWindows.FeatureCard(Roadmap.Get(Key)), commandData.Application.MainWindowHandle); }
            catch (Exception ex) { Log.Error($"Roadmap card: {ex}"); TaskDialog.Show("ACE", Roadmap.Get(Key).Summary); }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class RoadmapCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { RoadmapWindows.Show(RoadmapWindows.Overview(), commandData.Application.MainWindowHandle); }
            catch (Exception ex) { Log.Error($"Roadmap window: {ex}"); message = ex.Message; return Result.Failed; }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_undo : PlannedFeatureCommand { protected override string Key => "undo"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_warnings : PlannedFeatureCommand { protected override string Key => "warnings"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_params : PlannedFeatureCommand { protected override string Key => "params"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_roomsdoors : PlannedFeatureCommand { protected override string Key => "roomsdoors"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_snoopsel : PlannedFeatureCommand { protected override string Key => "snoopsel"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_snoopdoc : PlannedFeatureCommand { protected override string Key => "snoopdoc"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_events : PlannedFeatureCommand { protected override string Key => "events"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_xlsout : PlannedFeatureCommand { protected override string Key => "xlsout"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_xlsin : PlannedFeatureCommand { protected override string Key => "xlsin"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_parammgr : PlannedFeatureCommand { protected override string Key => "parammgr"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_smartsel : PlannedFeatureCommand { protected override string Key => "smartsel"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_batchexport : PlannedFeatureCommand { protected override string Key => "batchexport"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_exportprofiles : PlannedFeatureCommand { protected override string Key => "exportprofiles"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_clash : PlannedFeatureCommand { protected override string Key => "clash"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_subcheck : PlannedFeatureCommand { protected override string Key => "subcheck"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_subprep : PlannedFeatureCommand { protected override string Key => "subprep"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_transmittal : PlannedFeatureCommand { protected override string Key => "transmittal"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_designcheck : PlannedFeatureCommand { protected override string Key => "designcheck"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_codepacks : PlannedFeatureCommand { protected override string Key => "codepacks"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_teamtools : PlannedFeatureCommand { protected override string Key => "teamtools"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_users : PlannedFeatureCommand { protected override string Key => "users"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_usage : PlannedFeatureCommand { protected override string Key => "usage"; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Planned_policy : PlannedFeatureCommand { protected override string Key => "policy"; }

    /// <summary>ACE-branded windows (light: white, black, greys, ACE Red as a thin accent only).</summary>
    internal static class RoadmapWindows
    {
        private static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Brush Black => B(Branding.Primary);
        private static Brush Grey => B(Branding.GreyDark);
        private static Brush Light => B(Branding.GreyLight);
        private static Brush Red => B(Branding.Accent);

        public static void Show(Window w, IntPtr owner)
        {
            new WindowInteropHelper(w) { Owner = owner };
            w.ShowDialog();
        }

        private static Window Frame(string title, UIElement body, double width, double height)
        {
            var w = new Window
            {
                Title = title, Width = width, Height = height, MinWidth = 420, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = Brushes.White, FontFamily = new FontFamily(Branding.FontFamily + ", Arial"), FontSize = 13, Foreground = Black,
            };
            var root = new DockPanel();
            var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 16) };
            var close = new Button { Content = "Close", Padding = new Thickness(18, 5, 18, 5), IsCancel = true, IsDefault = true,
                Background = Brushes.White, Foreground = Black, BorderBrush = Grey };
            close.Click += (s, e) => w.Close();
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(new TextBlock { Text = Branding.FullName, Foreground = Grey, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 0) });
            w.Content = root;
            return w;
        }

        /// <summary>ACE signature header: logo on a white field | thin red divider | title and subtitle.</summary>
        private static UIElement Header(string title, string subtitle)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            if (Branding.Mark != null)
            {
                var img = new Image { Source = Branding.Mark, Height = 44, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                row.Children.Add(img);
                row.Children.Add(new Border { Width = 2, Background = Red, Margin = new Thickness(16, 2, 16, 2) });
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Black, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Grey, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            return row;
        }

        private static TextBlock H(string text) =>
            new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Black, Margin = new Thickness(0, 14, 0, 6) };

        private static UIElement Bullet(string text)
        {
            var g = new WpfGrid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var mark = new Border { Width = 5, Height = 5, Background = Grey, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Black };
            WpfGrid.SetColumn(t, 1);
            g.Children.Add(mark); g.Children.Add(t);
            return g;
        }

        private static string PhaseLine(Feature f) => $"Coming soon  ·  Phase {f.Phase}: {Roadmap.Phases[f.Phase]}";

        public static Window FeatureCard(Feature f)
        {
            var body = new StackPanel();
            var name = f.Label.Replace("\n", " ").Replace("&&", "&");
            body.Children.Add(Header(name, PhaseLine(f)));
            body.Children.Add(new TextBlock { Text = f.Summary, TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Black });

            body.Children.Add(H("What it will do"));
            foreach (var item in f.WillDo) body.Children.Add(Bullet(item));

            body.Children.Add(H("How it will work"));
            body.Children.Add(Bullet("One click on the ACE ribbon, or ask Claude in plain words: the same engine serves both."));
            body.Children.Add(Bullet("Read-only by default. Any change is previewed, confirmed, applied as one undo step and recorded in the activity journal."));
            if (f.Note != null) body.Children.Add(Bullet(f.Note));

            if (f.TodayWithClaude != null)
            {
                var box = new StackPanel();
                box.Children.Add(new TextBlock { Text = "Available today through Claude", FontWeight = FontWeights.Bold, Foreground = Black });
                box.Children.Add(new TextBlock { Text = f.TodayWithClaude, TextWrapping = TextWrapping.Wrap, Foreground = Black, Margin = new Thickness(0, 4, 0, 0) });
                if (f.Prompt != null)
                {
                    var prompt = "Using ace-revit: " + f.Prompt;
                    box.Children.Add(new TextBlock { Text = "\u201C" + f.Prompt + "\u201D", TextWrapping = TextWrapping.Wrap, Foreground = Grey, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 6, 0, 8) });
                    var copy = new Button { Content = "Copy this prompt for Claude", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left,
                        Background = Red, Foreground = Brushes.White, BorderBrush = Red, Cursor = System.Windows.Input.Cursors.Hand };
                    copy.Click += (s, e) =>
                    {
                        try { Clipboard.SetText(prompt); copy.Content = "Copied. Paste it into Claude (Ctrl+V)"; }
                        catch (Exception ex) { copy.Content = "Could not copy: " + ex.Message; }
                    };
                    box.Children.Add(copy);
                }
                body.Children.Add(new Border
                {
                    Background = Light, BorderBrush = Red, BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 18, 0, 8), Child = box,
                });
            }
            return Frame($"{Branding.Name} | {name}", body, 560, 600);
        }

        public static Window Overview()
        {
            var body = new StackPanel();
            body.Children.Add(Header($"{Branding.Name} tools for Revit", "Roadmap: what is live today and what comes next"));
            body.Children.Add(new TextBlock
            {
                Text = "Every tool works two ways: one click on the ACE ribbon, or a plain-language request to Claude. " +
                       "Changes are always previewed, confirmed, applied as one undo step and recorded.",
                TextWrapping = TextWrapping.Wrap, Foreground = Black,
            });

            body.Children.Add(H("Live today"));
            body.Children.Add(Bullet("Claude in Revit: plain-language requests carried out with the full Revit API, with enforced preview, one-step undo, backups and an activity journal."));
            body.Children.Add(Bullet("ACE Companion panel: approve Claude's changes in Revit, follow its activity, click results to select them, and ready-made prompts."));
            body.Children.Add(Bullet("Built-in checks through Claude: model audit, parameter completeness, rooms without doors, door widths; plus team scripts, setup checks and issue reports."));

            foreach (var phase in Roadmap.Phases)
            {
                body.Children.Add(H($"Phase {phase.Key}: {phase.Value}"));
                foreach (var f in Roadmap.Features.Where(x => x.Phase == phase.Key))
                {
                    var name = f.Label.Replace("\n", " ").Replace("&&", "&");
                    var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Black };
                    t.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.Bold });
                    t.Inlines.Add(new System.Windows.Documents.Run($"  ({f.Panel}{(f.LiveCommand != null ? ", live now" : "")})") { Foreground = f.LiveCommand != null ? Red : Grey });
                    t.Inlines.Add(new System.Windows.Documents.Run(": " + f.Summary));
                    var g = new WpfGrid { Margin = new Thickness(0, 2, 0, 2) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.Children.Add(new Border { Width = 5, Height = 5, Background = Grey, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                    WpfGrid.SetColumn(t, 1); g.Children.Add(t);
                    body.Children.Add(g);
                }
            }
            body.Children.Add(new TextBlock
            {
                Text = "Buttons with a hollow red ring on the ACE ribbon are planned tools: click one to see what it will do.",
                TextWrapping = TextWrapping.Wrap, Foreground = Grey, FontSize = 12, Margin = new Thickness(0, 16, 0, 8),
            });
            return Frame($"{Branding.Name} | Roadmap", body, 680, 720);
        }
    }
}
