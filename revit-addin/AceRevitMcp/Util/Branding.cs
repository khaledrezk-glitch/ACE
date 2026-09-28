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
    /// { "name": "ACE", "primary": "#0B3A6E", "accent": "#E4572E", "logo": "logo.png", "useLogoOnRibbon": false }
    /// Missing or invalid values fall back to neutral defaults.
    /// </summary>
    internal static class Branding
    {
        public static string Name { get; private set; } = "ACE";
        public static Color Primary { get; private set; } = (Color)ColorConverter.ConvertFromString("#1F4E79");
        public static Color Accent { get; private set; } = (Color)ColorConverter.ConvertFromString("#2F80ED");
        public static ImageSource Logo { get; private set; }
        public static bool UseLogoOnRibbon { get; private set; }

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
                UseLogoOnRibbon = json["useLogoOnRibbon"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                if (json["logo"]?.ToString() is string logo && logo.Length > 0)
                {
                    var path = Path.IsPathRooted(logo) ? logo : Path.Combine(Folder, logo);
                    if (File.Exists(path)) Logo = LoadImage(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Branding not loaded: {ex.Message}");
            }
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

    /// <summary>Ribbon icons drawn as vectors in the brand colour (no image files needed).</summary>
    internal static class Icons
    {
        // Simple 24x24 glyphs (Material-style paths).
        private const string ChatCheck = "M4,4 H20 A2,2 0 0 1 22,6 V16 A2,2 0 0 1 20,18 H8 L4,22 V6 A2,2 0 0 1 4,4 Z M9.5,13.6 L7,11.1 L8.1,10 L9.5,11.4 L15.9,5 L17,6.1 Z";
        private const string Pulse = "M2,12 H6 L8.5,5 L12.5,19 L15,10 L16.5,12 H22 V13.6 H15.6 L15,12.8 L12.5,21.5 L8.5,8.5 L7,13.6 H2 Z";

        public static ImageSource Companion(int size) => Render(size, ChatCheck);
        public static ImageSource Status(int size) => Render(size, Pulse);

        private static ImageSource Render(int size, string glyph)
        {
            if (Branding.UseLogoOnRibbon && Branding.Logo != null) return Branding.Logo;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var radius = size * 0.22;
                dc.DrawRoundedRectangle(new SolidColorBrush(Branding.Primary), null, new Rect(0, 0, size, size), radius, radius);
                var geometry = Geometry.Parse(glyph).Clone();
                var scale = size * 0.72 / 24.0;
                var offset = (size - 24 * scale) / 2;
                geometry.Transform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(offset, offset) } };
                dc.DrawGeometry(Brushes.White, null, geometry);
                // accent dot, top right
                dc.DrawEllipse(new SolidColorBrush(Branding.Accent), new Pen(Brushes.White, Math.Max(1, size / 32.0)), new Point(size * 0.82, size * 0.18), size * 0.13, size * 0.13);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
