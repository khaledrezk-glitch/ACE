using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Company branding for the ribbon icons and the Companion panel, from
    /// %APPDATA%\ACE-RevitMCP\branding\brand.json (installed from the package's branding folder):
    /// { "name": "ACE", "primary": "#212121", "accent": "#EF3340", "font": "Poppins", "headingFont": "BW Gradual", "mark": "ace-mark.png" }
    /// Missing or invalid values fall back to neutral defaults.
    /// </summary>
    internal static class Branding
    {
        public static string Name { get; private set; } = "ACE";
        public static Color Primary { get; private set; } = (Color)ColorConverter.ConvertFromString("#1F4E79");
        public static Color Accent { get; private set; } = (Color)ColorConverter.ConvertFromString("#2F80ED");
        public static string FullName { get; private set; } = "";
        public static string FontFamily { get; private set; } = "Segoe UI";
        /// <summary>Headline typeface (ACE: BW Gradual); falls back to the body font where it is not installed.</summary>
        public static string HeadingFont { get; private set; }
        public static Color GreyDark { get; private set; } = (Color)ColorConverter.ConvertFromString("#5F6773");
        public static Color GreyLight { get; private set; } = (Color)ColorConverter.ConvertFromString("#F7F8FA");
        public static ImageSource Logo { get; private set; }
        /// <summary>The company lettermark (e.g. just "ACE"): dark version for light backgrounds, light version for dark ones.</summary>
        public static ImageSource Mark { get; private set; }
        public static ImageSource MarkOnDark { get; private set; }
        public static bool IsDarkTheme
        {
            get { try { return Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark; } catch { return false; } }
        }
        /// <summary>The mark that reads well on the current Revit theme.</summary>
        public static ImageSource MarkForTheme => IsDarkTheme ? (MarkOnDark ?? Mark) : (Mark ?? MarkOnDark);
        public static bool UseLogoOnRibbon { get; private set; }
        /// <summary>Ribbon icon style: "3d" (raised tile, default) or "flat".</summary>
        public static bool Icons3D { get; private set; } = true;

        public static string Folder => Path.Combine(AceConfig.Directory, "branding");

        public static void Load()
        {
            try
            {
                var file = Path.Combine(Folder, "brand.json");
                if (!File.Exists(file)) return;
                var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (json == null) return;
                if (json["name"]?.ToString() is string n && n.Trim().Length > 0) Name = n.Trim();
                Primary = ParseColor(json["primary"]?.ToString(), Primary);
                Accent = ParseColor(json["accent"]?.ToString(), Accent);
                GreyDark = ParseColor(json["greyDark"]?.ToString(), GreyDark);
                GreyLight = ParseColor(json["greyLight"]?.ToString(), GreyLight);
                if (json["fullName"]?.ToString() is string fn) FullName = fn.Trim();
                if (json["font"]?.ToString() is string font && font.Trim().Length > 0) FontFamily = font.Trim();
                if (json["headingFont"]?.ToString() is string hf && hf.Trim().Length > 0) HeadingFont = hf.Trim();
                UseLogoOnRibbon = json["useLogoOnRibbon"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                if (json["iconStyle"]?.ToString() is string style) Icons3D = !style.Trim().Equals("flat", StringComparison.OrdinalIgnoreCase);
                Logo = Image(json["logo"]?.ToString());
                Mark = Image(json["mark"]?.ToString());
                MarkOnDark = Image(json["markOnDark"]?.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn($"Branding not loaded: {ex.Message}");
            }
        }

        private static ImageSource Image(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            var path = Path.IsPathRooted(file) ? file : Path.Combine(Folder, file);
            return File.Exists(path) ? LoadImage(path) : null;
        }

        private static Color ParseColor(string value, Color fallback)
        {
            try { return string.IsNullOrWhiteSpace(value) ? fallback : (Color)ColorConverter.ConvertFromString(value); }
            catch { return fallback; }
        }

        private static ImageSource LoadImage(string path)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
    }

    /// <summary>
    /// Ribbon icons: a rounded tile in the brand's primary colour on light themes, or a light tile on dark
    /// themes (so a black brand stays visible), with a white/dark glyph. Companion shows the company mark.
    /// </summary>
    internal static class Icons
    {
        private const string ChatCheck = "M4,4 H20 A2,2 0 0 1 22,6 V16 A2,2 0 0 1 20,18 H8 L4,22 V6 A2,2 0 0 1 4,4 Z M9.5,13.6 L7,11.1 L8.1,10 L9.5,11.4 L15.9,5 L17,6.1 Z";
        private const string Pulse = "M2,12 H6 L8.5,5 L12.5,19 L15,10 L16.5,12 H22 V13.6 H15.6 L15,12.8 L12.5,21.5 L8.5,8.5 L7,13.6 H2 Z";

        // The company mark is not used on 16/32 px ribbon icons (brand minimum logo size is 72 px wide).
        public static ImageSource Companion(int size) => Render(size, ChatCheck, useMark: false);
        public static ImageSource Status(int size) => Render(size, Pulse, useMark: false);
        /// <summary>A line icon (24 × 24 stroked path). Planned tools get a hollow accent ring instead of a solid dot.</summary>
        public static ImageSource Line(int size, string glyph, bool planned) => Render(size, glyph, useMark: false, stroke: true, planned: planned);

        private static ImageSource Render(int size, string glyph, bool useMark, bool stroke = false, bool planned = false)
        {
            if (Branding.UseLogoOnRibbon && Branding.Logo != null) return Branding.Logo;
            var dark = Branding.IsDarkTheme;
            var tile = dark ? Colors.White : Branding.Primary;
            var ink = dark ? Branding.Primary : Colors.White;
            // A very light primary on a light theme would vanish: fall back to a dark tile.
            if (!dark && (0.299 * tile.R + 0.587 * tile.G + 0.114 * tile.B) > 200) { tile = Color.FromRgb(0x22, 0x22, 0x22); ink = Colors.White; }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var radius = size * 0.22;
                var full = new Rect(0, 0, size, size);
                var raised = Branding.Icons3D;
                // 3D: a darker base shows as a lip under the tile, so the tile looks raised off the ribbon.
                var depth = raised ? Math.Max(1.0, size * 0.07) : 0;
                var face = new Rect(0, 0, size, size - depth);
                if (raised)
                {
                    dc.DrawRoundedRectangle(new SolidColorBrush(Shade(tile, dark ? -0.30 : -0.45)), null, full, radius, radius);
                    var faceBrush = new LinearGradientBrush(Shade(tile, dark ? 0.0 : 0.28), Shade(tile, dark ? -0.10 : -0.06), 90);
                    dc.DrawRoundedRectangle(faceBrush, null, face, radius, radius);
                    // Light from above: a soft highlight over the upper half of the face.
                    var gloss = new LinearGradientBrush(Color.FromArgb(dark ? (byte)120 : (byte)80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
                    dc.DrawRoundedRectangle(gloss, null, new Rect(size * 0.06, size * 0.04, size * 0.88, face.Height * 0.5), radius * 0.8, radius * 0.8);
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Math.Max(0.6, size * 0.03)),
                        new Rect(size * 0.02, size * 0.02, size * 0.96, face.Height - size * 0.04), radius, radius);
                }
                else dc.DrawRoundedRectangle(new SolidColorBrush(tile), null, full, radius, radius);

                var mark = useMark ? (dark ? (Branding.Mark ?? Branding.MarkOnDark) : (Branding.MarkOnDark ?? Branding.Mark)) : null;
                if (mark != null)
                {
                    // Fit the lettermark inside the tile with a margin, keeping its aspect ratio.
                    var box = size * 0.78;
                    var ratio = mark.Width / mark.Height;
                    var w = ratio >= 1 ? box : box * ratio;
                    var h = ratio >= 1 ? box / ratio : box;
                    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                    dc.DrawImage(mark, new Rect((size - w) / 2, (face.Height - h) / 2, w, h));
                }
                else
                {
                    var scale = size * (stroke ? 0.64 : 0.72) / 24.0;
                    var offset = (size - 24 * scale) / 2;
                    var lift = depth / 2;   // centre the glyph on the raised face
                    Geometry Placed(double dx, double dy)
                    {
                        var g = Geometry.Parse(glyph).Clone();
                        g.Transform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(offset + dx, offset - lift + dy) } };
                        return g;
                    }
                    var inkBrush = new SolidColorBrush(ink);
                    var shadowBrush = new SolidColorBrush(Color.FromArgb(dark ? (byte)40 : (byte)110, 0, 0, 0));
                    var sh = Math.Max(0.7, size * 0.035);
                    if (stroke)
                    {
                        Pen P(Brush br) => new Pen(br, Math.Max(1.0, 2.0 * scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                        if (raised) dc.DrawGeometry(null, P(shadowBrush), Placed(0, sh));
                        dc.DrawGeometry(null, P(inkBrush), Placed(0, 0));
                    }
                    else
                    {
                        if (raised) dc.DrawGeometry(shadowBrush, null, Placed(0, sh));
                        dc.DrawGeometry(inkBrush, null, Placed(0, 0));
                    }
                }
                // Small accent punctuation (e.g. ACE Red), never a large fill.
                var accentColor = Branding.Accent;
                var dot = new Point(size * 0.84, size * 0.16);
                Brush accent = raised
                    ? new RadialGradientBrush(Shade(accentColor, 0.45), Shade(accentColor, -0.25)) { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.6, RadiusY = 0.6 }
                    : new SolidColorBrush(accentColor);
                if (planned) dc.DrawEllipse(new SolidColorBrush(Shade(tile, raised && !dark ? 0.2 : 0)), new Pen(new SolidColorBrush(accentColor), Math.Max(1.0, size * 0.05)), dot, size * 0.09, size * 0.09);
                else dc.DrawEllipse(accent, null, dot, size * 0.1, size * 0.1);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Lighter (amount &gt; 0, towards white) or darker (amount &lt; 0, towards black) version of a colour.</summary>
        private static Color Shade(Color c, double amount)
        {
            byte Mix(byte v) => (byte)Math.Round(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount));
            return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
        }
    }
}
