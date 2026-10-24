using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The stamp on every run record (backlog T13): which ACE add-in and MCP server made it, and a rules hash, a SHA-256 over
    /// the rule and requirement inputs the run used. The hash is taken over canonical JSON so it is the same on every machine:
    /// object keys sorted (ordinal), no whitespace, line breaks inside strings as LF, numbers written as JavaScript writes
    /// them. The MCP server's <c>lib/stamp.js</c> implements the same form; <c>mcp-server/test/fixtures/rules-hash.json</c>
    /// holds the shared test vector both must agree on.
    /// </summary>
    public static class RulesStamp
    {
        /// <summary>The MCP server version of the last bridge request (header X-Ace-Mcp-Version); null before any.</summary>
        public static volatile string McpVersion;

        public static string AceVersion => typeof(RulesStamp).Assembly.GetName().Version?.ToString(3);

        /// <summary>Lower-case SHA-256 hex of the canonical JSON of <paramref name="inputs"/>.</summary>
        public static string Hash(JsonNode inputs) => Compat.Sha256Hex(Canonical(inputs)).ToLowerInvariant();

        /// <summary>{ aceVersion, mcpVersion, rulesHash, rulesInputs } for a run record; inputs: name -> what the run used.</summary>
        public static JsonObject Of(JsonObject inputs) => new JsonObject
        {
            ["aceVersion"] = AceVersion,
            ["mcpVersion"] = McpVersion,
            ["rulesHash"] = Hash(inputs),
            ["rulesInputs"] = new JsonArray((inputs ?? new JsonObject()).Where(kv => kv.Value != null).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode)k).ToArray()),
        };

        /// <summary>Canonical JSON text (see the class summary). A null node is "null".</summary>
        public static string Canonical(JsonNode node)
        {
            var sb = new StringBuilder();
            if (node == null) sb.Append("null");
            else using (var doc = JsonDocument.Parse(node.ToJsonString())) Write(doc.RootElement, sb);
            return sb.ToString();
        }

        private static void Write(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var first = true;
                    // Duplicate keys: the last wins, as in JSON.parse.
                    var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject()) props[p.Name] = p.Value;
                    foreach (var k in props.Keys.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(k, sb);
                        sb.Append(':');
                        Write(props[k], sb);
                    }
                    sb.Append('}');
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    var i = 0;
                    foreach (var x in e.EnumerateArray()) { if (i++ > 0) sb.Append(','); Write(x, sb); }
                    sb.Append(']');
                    break;
                case JsonValueKind.String: WriteString(e.GetString(), sb); break;
                case JsonValueKind.Number: sb.Append(Number(double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture))); break;
                case JsonValueKind.True: sb.Append("true"); break;
                case JsonValueKind.False: sb.Append("false"); break;
                default: sb.Append("null"); break;
            }
        }

        // JSON.stringify's escaping, after CRLF / CR -> LF.
        private static void WriteString(string s, StringBuilder sb)
        {
            s = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            sb.Append('"');
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // A surrogate pair is kept; a lone surrogate is escaped, as JSON.stringify does.
                        var pairHigh = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                        if (pairHigh) { sb.Append(c).Append(s[++i]); break; }
                        if (c < 0x20 || char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>A double as JavaScript's Number.prototype.toString writes it (ECMAScript Number::toString).</summary>
        public static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == 0) return "0";
            var neg = d < 0;
            // Shortest round-trip digits and exponent from the "R" form, e.g. "1.5E-07" or "123.45".
            var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
            var exp = 0;
            var ei = r.IndexOfAny(new[] { 'E', 'e' });
            if (ei >= 0) { exp = int.Parse(r.Substring(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); r = r.Substring(0, ei); }
            var dot = r.IndexOf('.');
            var digits = dot >= 0 ? r.Remove(dot, 1) : r;
            var n = (dot >= 0 ? dot : r.Length) + exp;   // position of the decimal point within digits
            var lead = digits.Length - digits.TrimStart('0').Length;
            digits = digits.TrimStart('0'); n -= lead;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) return "0";
            var k = digits.Length;
            string s;
            if (k <= n && n <= 21) s = digits + new string('0', n - k);
            else if (0 < n && n <= 21) s = digits.Substring(0, n) + "." + digits.Substring(n);
            else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
            else
            {
                var e = n - 1;
                s = digits.Substring(0, 1) + (k > 1 ? "." + digits.Substring(1) : "") + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            }
            return neg ? "-" + s : s;
        }
    }
}
