using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// The office presentation standard: printed text height by view scale (e.g. 3 mm at 1:50 and larger scales,
    /// 2.5 mm at 1:100 and smaller), an optional font, and which views it applies to. It lives in the "presentation"
    /// section of the office check set (checkset.json), so the model check that reports it and the fix that applies it
    /// read the same rules. Without a check set ACE's defaults apply.
    /// </summary>
    public sealed class PresentationStandard
    {
        /// <summary>One row: views at this scale or larger (1:upToScale, e.g. 50 = 1:50, 1:20, 1:10) use this text height.</summary>
        public sealed class Size
        {
            public int UpToScale;
            public double TextMm;
        }

        public string Name = "ACE default";
        public string Source = "built-in";
        public List<Size> Sizes = new List<Size> { new Size { UpToScale = 50, TextMm = 3.0 }, new Size { UpToScale = int.MaxValue, TextMm = 2.5 } };
        /// <summary>Font for text and dimension types (null: keep the type's font).</summary>
        public string Font;
        /// <summary>Only views placed on sheets (default): working views are left alone.</summary>
        public bool ViewsOnSheetsOnly = true;
        /// <summary>Also give every tag of one category the same tag type (off by default: tag types often differ on purpose).</summary>
        public bool UnifyTagTypes;

        /// <summary>
        /// Types chosen by the user per text height and kind ("3" -> "text" -> "ACE Text 3mm"; kinds: text, dim:Linear,
        /// dim:Angular, ...), and per tag category under "tags" ("Door Tags" -> "Door Tag : Standard"). Kinds without a
        /// choice use the most used type of the right size, or a new one made from the most used type.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Types = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A short fingerprint of everything the standard decides (sizes, font, scope, chosen types).</summary>
        public string Identity()
        {
            var text = string.Join(";", Sizes.OrderBy(s => s.UpToScale).Select(s => $"{s.UpToScale}:{Band(s.TextMm)}")) + $"|{Font}|{ViewsOnSheetsOnly}|{UnifyTagTypes}|" +
                       string.Join(";", Types.SelectMany(b => b.Value.Select(k => $"{b.Key}/{k.Key}={k.Value}")).OrderBy(x => x, StringComparer.Ordinal));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).Substring(0, 16);
        }

        public static string Band(double mm) => mm.ToString("0.##", CultureInfo.InvariantCulture);

        public string TypeFor(string band, string kind) =>
            Types.TryGetValue(band, out var kinds) && kinds.TryGetValue(kind, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;

        public void SetType(string band, string kind, string name)
        {
            if (!Types.TryGetValue(band, out var kinds)) Types[band] = kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(name)) kinds.Remove(kind); else kinds[kind] = name;
        }

        /// <summary>The printed text height for a view scale (1:scale), or null when no row covers it.</summary>
        public double? TextMm(int scale) => Sizes.Where(s => scale <= s.UpToScale).OrderBy(s => s.UpToScale).Select(s => (double?)s.TextMm).FirstOrDefault();

        public string Describe() => string.Join(", ", Sizes.OrderBy(s => s.UpToScale).Select((s, i) =>
        {
            var previous = i == 0 ? 0 : Sizes.OrderBy(x => x.UpToScale).ElementAt(i - 1).UpToScale;
            var range = s.UpToScale == int.MaxValue ? $"smaller than 1:{previous}" : i == 0 ? $"1:{s.UpToScale} and larger" : $"1:{previous + 1} to 1:{s.UpToScale}";
            return $"{s.TextMm.ToString("0.##", CultureInfo.InvariantCulture)} mm for {range}";
        }));

        /// <summary>The standard from the office check set, with sizes from <paramref name="overrideSizes"/> when given (e.g. a project's BEP).</summary>
        public static PresentationStandard Load(JsonArray overrideSizes = null)
        {
            var std = new PresentationStandard();
            try
            {
                var file = CheckSet.FilePath();
                if (file != null && File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject set && set["presentation"] is JsonObject p)
                {
                    std.Name = p["name"]?.ToString() ?? set["name"]?.ToString() ?? std.Name;
                    std.Source = file;
                    if (p["textSizes"] is JsonArray sizes) std.Sizes = Parse(sizes) ?? std.Sizes;
                    std.Font = string.IsNullOrWhiteSpace(p["font"]?.ToString()) ? null : p["font"].ToString();
                    if (p["viewsOnSheetsOnly"] is JsonValue vs && vs.TryGetValue<bool>(out var onSheets)) std.ViewsOnSheetsOnly = onSheets;
                    if (p["unifyTagTypes"] is JsonValue ut && ut.TryGetValue<bool>(out var unify)) std.UnifyTagTypes = unify;
                    if (p["types"] is JsonObject types)
                        foreach (var (band, kinds) in types)
                            if (kinds is JsonObject k)
                                foreach (var (kind, name) in k)
                                    std.SetType(double.TryParse(band, NumberStyles.Float, CultureInfo.InvariantCulture, out var bmm) ? Band(bmm) : band, kind, name?.ToString());   // "3.0" and "3" are one band
                }
            }
            catch (Exception ex) { Log.Warn($"Presentation standard: {ex.Message}"); }
            if (overrideSizes != null) { std.Sizes = Parse(overrideSizes) ?? std.Sizes; std.Source += " (sizes given in the request)"; }
            return std;
        }

        /// <summary>Writes the standard into the "presentation" section of the office check set (other sections are kept).</summary>
        public string Save()
        {
            var file = CheckSet.FilePath();
            var set = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject() : new JsonObject { ["name"] = "ACE Standard" };
            var types = new JsonObject();
            foreach (var (band, kinds) in Types.Where(t => t.Value.Count > 0))
                types[band] = new JsonObject(kinds.Select(k => new KeyValuePair<string, JsonNode>(k.Key, k.Value)));
            set["presentation"] = new JsonObject
            {
                ["name"] = Name,
                ["about"] = "Printed text height by view scale (upToScale 50 = 1:50 and larger scales; a row without upToScale covers the rest), optional font, and the type chosen per size and kind. Read by the model check and applied by ACE > Deliver > Presentation Standard or Claude's presentation_standard.",
                ["textSizes"] = new JsonArray(Sizes.OrderBy(s => s.UpToScale).Select(s => (JsonNode)(s.UpToScale == int.MaxValue
                    ? new JsonObject { ["textMm"] = s.TextMm } : new JsonObject { ["upToScale"] = s.UpToScale, ["textMm"] = s.TextMm })).ToArray()),
                ["font"] = Font,
                ["viewsOnSheetsOnly"] = ViewsOnSheetsOnly,
                ["unifyTagTypes"] = UnifyTagTypes,
                ["types"] = types,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, set.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Source = file;
            return file;
        }

        /// <summary>[{ "upToScale": 50, "textMm": 3 }, { "textMm": 2.5 }] (a row without upToScale covers every smaller scale).</summary>
        internal static List<Size> Parse(JsonArray rows)
        {
            var list = new List<Size>();
            foreach (var r in rows.OfType<JsonObject>())
            {
                // Read as text: a whole number (2) and a decimal (2.5) are both fine, from a file or from code.
                if (!double.TryParse(r["textMm"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || mm <= 0 || mm > 50) continue;
                var up = int.TryParse(r["upToScale"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : int.MaxValue;
                list.Add(new Size { UpToScale = up, TextMm = mm });
            }
            return list.Count == 0 ? null : list.OrderBy(s => s.UpToScale).ToList();
        }
    }
}
