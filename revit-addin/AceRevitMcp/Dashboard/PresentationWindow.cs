using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// ACE > Deliver > Presentation Standard: set the printed text height per view scale and pick the type for each kind
    /// (text, each dimension style, tags per category) and size; save it to the office check set; check the model,
    /// preview and apply (one undo step). Modal, so it works directly in the Revit API context of its command.
    /// </summary>
    internal sealed class PresentationWindow : Window
    {
        private const string Auto = "Automatic: the most used type at this size, else a new one";
        private readonly UIApplication _app;
        private readonly Document _doc;
        private readonly PresentationStandard _std;
        private readonly Dictionary<string, List<ElementType>> _types;
        private readonly Dictionary<string, List<ElementType>> _tagTypes;
        private readonly StackPanel _sizeRows = new StackPanel();
        private readonly Grid _typeGrid = new Grid();
        private readonly StackPanel _tagRows = new StackPanel();
        private readonly TextBox _font = new TextBox { Width = 180 };
        private readonly CheckBox _onSheets = new CheckBox { Content = "Only views placed on sheets", Margin = new Thickness(0, 0, 18, 0) };
        private readonly CheckBox _unifyTags = new CheckBox { Content = "Also give every tag of a category one tag type" };
        private readonly TextBox _result = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 120, FontFamily = new FontFamily("Consolas, Courier New") };
        private readonly Button _apply;
        private readonly List<(TextBox Scale, TextBox Mm)> _sizes = new List<(TextBox, TextBox)>();
        private readonly Dictionary<(string Band, string Kind), ComboBox> _choices = new Dictionary<(string, string), ComboBox>();
        private readonly Dictionary<string, ComboBox> _tagChoices = new Dictionary<string, ComboBox>();
        private string _previewed;   // the settings the last preview was made with: Apply only for exactly those

        internal PresentationWindow(UIApplication app)
        {
            _app = app;
            _doc = app.ActiveUIDocument.Document;
            _std = PresentationStandard.Load();
            _types = PresentationFix.TypesByKind(_doc);
            _tagTypes = new FilteredElementCollector(_doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Where(t => t.Category != null)
                .GroupBy(t => t.Category.Name)
                .ToDictionary(g => g.Key, g => g.GroupBy(t => t.GetTypeId().Value).OrderByDescending(x => x.Count())
                    .Select(x => _doc.GetElement(new ElementId(x.Key)) as ElementType).Where(x => x != null).ToList());

            Title = $"{Branding.Name} | Presentation Standard";
            Width = 900; Height = 760; MinWidth = 640; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily(Branding.FontFamily + ", Arial");
            FontSize = 13;
            Background = Brushes.White;
            var black = new SolidColorBrush(Branding.Primary);
            var grey = new SolidColorBrush(Branding.GreyDark);
            var red = new SolidColorBrush(Branding.Accent);

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
            TextBlock Heading(string t) => new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = black, Margin = new Thickness(0, 14, 0, 4) };
            TextBlock Note(string t) => new TextBlock { Text = t, Foreground = grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

            var body = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };
            body.Children.Add(Note($"Standard: {_std.Name}. Saved in {(_std.Source == "built-in" ? "the office check set (not created yet)" : _std.Source)}. The model check reports it and Claude applies it the same way."));

            body.Children.Add(Heading("Text height by view scale"));
            body.Children.Add(Note("Printed height on the sheet. A row covers its scale and every larger one down to the row before (e.g. up to 1:50 = 1:50, 1:20, 1:10). Leave the last scale empty for all smaller scales."));
            body.Children.Add(_sizeRows);
            foreach (var s in _std.Sizes) AddSizeRow(s.UpToScale == int.MaxValue ? "" : s.UpToScale.ToString(CultureInfo.InvariantCulture), s.TextMm.ToString("0.##", CultureInfo.InvariantCulture));
            var sizeButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            sizeButtons.Children.Add(Btn("Add a scale row", false, (s, e) => { AddSizeRow("", ""); }));
            sizeButtons.Children.Add(Btn("Update the type table", false, (s, e) => BuildTypeGrid()));
            body.Children.Add(sizeButtons);

            body.Children.Add(Heading("Options"));
            var options = new StackPanel { Orientation = Orientation.Horizontal };
            options.Children.Add(new TextBlock { Text = "Font (empty = keep):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _font.Text = _std.Font ?? "";
            _font.Margin = new Thickness(0, 0, 18, 0);
            options.Children.Add(_font);
            _onSheets.IsChecked = _std.ViewsOnSheetsOnly;
            _onSheets.VerticalAlignment = VerticalAlignment.Center;
            options.Children.Add(_onSheets);
            _unifyTags.IsChecked = _std.UnifyTagTypes;
            _unifyTags.VerticalAlignment = VerticalAlignment.Center;
            _unifyTags.Checked += (s, e) => _tagRows.Visibility = System.Windows.Visibility.Visible;
            _unifyTags.Unchecked += (s, e) => _tagRows.Visibility = System.Windows.Visibility.Collapsed;
            options.Children.Add(_unifyTags);
            body.Children.Add(options);

            body.Children.Add(Heading("Type for each kind and size"));
            body.Children.Add(Note("Pick the type every annotation of that kind gets in views of that size. Automatic keeps the most used type that already has the right height, or makes one from the most used type."));
            body.Children.Add(_typeGrid);
            BuildTypeGrid();

            _tagRows.Visibility = _std.UnifyTagTypes ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _tagRows.Children.Add(Heading("Tag type per category"));
            foreach (var (category, types) in _tagTypes.OrderBy(k => k.Key))
            {
                var combo = new ComboBox { MinWidth = 320 };
                combo.Items.Add(new ComboBoxItem { Content = "Most used (" + PresentationFix.Label(types[0]) + ")", Tag = "" });
                foreach (var t in types) combo.Items.Add(new ComboBoxItem { Content = PresentationFix.Label(t), Tag = PresentationFix.Label(t) });
                var chosen = _std.TypeFor("tags", category);
                combo.SelectedIndex = Math.Max(0, combo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == chosen));
                _tagChoices[category] = combo;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = category, Width = 200, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(combo);
                _tagRows.Children.Add(row);
            }
            if (_tagTypes.Count == 0) _tagRows.Children.Add(Note("No tags in this model yet."));
            body.Children.Add(_tagRows);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 6) };
            buttons.Children.Add(Btn("Save standard", false, (s, e) => Save()));
            buttons.Children.Add(Btn("Check model", false, (s, e) => Run(check: true)));
            buttons.Children.Add(Btn("Preview", false, (s, e) => Run(check: false, apply: false)));
            _apply = Btn("Apply", true, (s, e) => Run(check: false, apply: true));
            _apply.IsEnabled = false;
            buttons.Children.Add(_apply);
            buttons.Children.Add(Btn("Close", false, (s, e) => Close()));
            body.Children.Add(buttons);
            body.Children.Add(Note("Apply is available after a preview of the same settings. It is one undo step (Ctrl+Z)."));
            body.Children.Add(_result);

            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void AddSizeRow(string scale, string mm)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var scaleBox = new TextBox { Text = scale, Width = 70 };
            var mmBox = new TextBox { Text = mm, Width = 60 };
            row.Children.Add(new TextBlock { Text = "Up to 1:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            row.Children.Add(scaleBox);
            row.Children.Add(new TextBlock { Text = "text height", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) });
            row.Children.Add(mmBox);
            row.Children.Add(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) });
            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 1, 8, 1), Background = Brushes.White };
            var entry = (scaleBox, mmBox);
            remove.Click += (s, e) => { _sizeRows.Children.Remove(row); _sizes.Remove(entry); BuildTypeGrid(); };
            row.Children.Add(remove);
            _sizes.Add(entry);
            _sizeRows.Children.Add(row);
        }

        /// <summary>The sizes as typed; null (with the reason in <paramref name="problem"/>) when a row is not valid.</summary>
        private List<PresentationStandard.Size> ReadSizes(out string problem)
        {
            problem = null;
            var rows = new JsonArray();
            foreach (var (scale, mm) in _sizes)
            {
                if (string.IsNullOrWhiteSpace(mm.Text) && string.IsNullOrWhiteSpace(scale.Text)) continue;
                if (!double.TryParse(mm.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || m <= 0)
                { problem = $"'{mm.Text}' is not a text height in mm."; return null; }
                var o = new JsonObject { ["textMm"] = m };
                var sc = scale.Text.Trim();
                if (sc.StartsWith("1:")) sc = sc.Substring(2).Trim();   // "1:50" means 50
                if (sc.Length > 0)
                {
                    if (!int.TryParse(sc, out var n) || n <= 0) { problem = $"'{scale.Text}' is not a scale (write 50 for 1:50, or leave it empty for all smaller scales)."; return null; }
                    o["upToScale"] = n;
                }
                rows.Add(o);
            }
            var parsed = PresentationStandard.Parse(rows);
            if (parsed == null) problem = "Give at least one text height (mm).";
            return parsed;
        }

        private void BuildTypeGrid()
        {
            var keep = _choices.ToDictionary(c => c.Key, c => (string)(c.Value.SelectedItem as ComboBoxItem)?.Tag);
            _typeGrid.Children.Clear(); _typeGrid.RowDefinitions.Clear(); _typeGrid.ColumnDefinitions.Clear(); _choices.Clear();
            var bands = (ReadSizes(out _) ?? _std.Sizes).Select(s => PresentationStandard.Band(s.TextMm)).Distinct().ToList();
            _typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            foreach (var _ in bands) _typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _typeGrid.RowDefinitions.Add(new RowDefinition());
            for (var c = 0; c < bands.Count; c++)
            {
                var h = new TextBlock { Text = bands[c] + " mm", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 4) };
                Grid.SetColumn(h, c + 1); _typeGrid.Children.Add(h);
            }
            var kinds = _types.Keys.OrderBy(k => k == "text" ? "0" : k).ToList();
            for (var r = 0; r < kinds.Count; r++)
            {
                var kind = kinds[r];
                _typeGrid.RowDefinitions.Add(new RowDefinition());
                var label = new TextBlock { Text = PresentationFix.KindLabel(kind), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, r + 1); _typeGrid.Children.Add(label);
                for (var c = 0; c < bands.Count; c++)
                {
                    var combo = new ComboBox { Margin = new Thickness(4, 2, 4, 2) };
                    combo.Items.Add(new ComboBoxItem { Content = Auto, Tag = "" });
                    foreach (var t in _types[kind].OrderBy(t => AnnotationAudit.SizeMm(t)).ThenBy(t => t.Name))
                        combo.Items.Add(new ComboBoxItem { Content = $"{t.Name}  ({AnnotationAudit.SizeMm(t):0.##} mm)", Tag = t.Name });
                    var chosen = keep.TryGetValue((bands[c], kind), out var k) && k != null ? k : _std.TypeFor(bands[c], kind) ?? "";
                    combo.SelectedIndex = Math.Max(0, combo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == chosen));
                    _choices[(bands[c], kind)] = combo;
                    Grid.SetRow(combo, r + 1); Grid.SetColumn(combo, c + 1); _typeGrid.Children.Add(combo);
                }
            }
        }

        /// <summary>The window's settings into the standard; false (with a message) when the sizes are not valid.</summary>
        private bool Collect()
        {
            var sizes = ReadSizes(out var problem);
            if (sizes == null) { Show(problem); return false; }
            _std.Sizes = sizes;
            _std.Font = string.IsNullOrWhiteSpace(_font.Text) ? null : _font.Text.Trim();
            _std.ViewsOnSheetsOnly = _onSheets.IsChecked == true;
            _std.UnifyTagTypes = _unifyTags.IsChecked == true;
            // Merged into the loaded choices: kinds or tag categories this model does not have keep the office's choice.
            foreach (var ((band, kind), combo) in _choices) _std.SetType(band, kind, (string)(combo.SelectedItem as ComboBoxItem)?.Tag);
            foreach (var (category, combo) in _tagChoices) _std.SetType("tags", category, (string)(combo.SelectedItem as ComboBoxItem)?.Tag);
            return true;
        }

        private void Save()
        {
            if (!Collect()) return;
            try { Show($"Saved to {_std.Save()}.\nThe model check and Claude now use this standard."); }
            catch (Exception ex) { Show($"Could not save: {ex.Message}"); }
        }

        /// <summary>Checks, previews or applies what is on screen. Nothing is saved here: only Save changes the office standard.</summary>
        private void Run(bool check, bool apply = false)
        {
            if (!Collect()) return;
            var fingerprint = _std.Identity();
            if (apply && fingerprint != _previewed) { _apply.IsEnabled = false; Show("The settings changed since the preview. Preview again first."); return; }
            var args = new JsonObject();
            if (check) args["check_only"] = true;
            else { args["dry_run"] = !apply; args["_fromPanel"] = true; }
            try
            {
                var r = PresentationCommands.RunWith(_app, args, _std);
                Show(Describe(r, check ? "Check (nothing changed)" : apply ? "Applied" : "Preview (nothing changed)"));
                var ok = r["success"]?.GetValue<bool>() != false;
                _previewed = !check && !apply && ok ? fingerprint : null;
                _apply.IsEnabled = _previewed != null;
                if (apply && ok)
                    Journal.Append("Change (ACE Presentation Standard window)", null, $"APPLIED by the user: {r["changedType"]?.ToJsonString()}");
            }
            catch (Exception ex) { Log.Error($"Presentation standard: {ex}"); Show($"Failed: {ex.Message}. Nothing was changed."); }
        }

        private static string Describe(JsonObject r, string what)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{what}: {r["standard"]}");
            sb.AppendLine($"Views: {r["views"]} ({r["viewsScope"]}), annotations checked: {r["checked"]}");
            void Counts(string key, string label) { if (r[key] is JsonObject o && o.Count > 0) sb.AppendLine($"{label}: " + string.Join(", ", o.Select(kv => $"{kv.Key} {kv.Value}"))); }
            Counts("wrongHeight", "Wrong text height");
            Counts("wouldChangeType", "Would get another type");
            Counts("changedType", "Type changed");
            void List(string key, string label) { if (r[key] is JsonArray a && a.Count > 0) { sb.AppendLine(label + ":"); foreach (var x in a) sb.AppendLine("  " + x); } }
            List("typesToMake", "Types to make");
            List("typesMade", "Types made");
            List("typesUsed", "Types used");
            List("notes", "Notes");
            List("skipped", "Skipped");
            if (r["note"] != null) sb.AppendLine(r["note"].ToString());
            if (r["error"] != null) sb.AppendLine(r["error"].ToString());
            return sb.ToString();
        }

        private void Show(string text) => _result.Text = text;
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class PresentationStandardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var app = commandData.Application;
                if (app.ActiveUIDocument?.Document == null) { TaskDialog.Show("ACE Presentation Standard", "Open a model first."); return Result.Cancelled; }
                var w = new PresentationWindow(app);
                new WindowInteropHelper(w).Owner = app.MainWindowHandle;
                w.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex) { Log.Error($"Presentation Standard: {ex}"); message = ex.Message; return Result.Failed; }
        }
    }
}
