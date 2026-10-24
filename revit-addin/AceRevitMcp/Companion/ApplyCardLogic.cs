using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Companion
{
    /// <summary>
    /// The Apply card's safety lines (DM5), pure: what the preview's reply already says, printed for people. "New warnings:
    /// N" (G24, newWarnings / targetedWarnings), what deletions take along (G25, wouldChange.deletionProbe and each purge
    /// pass's deletionProbe), and the attention lines and after-check forecast a tool adds after the card was made (the
    /// workset tools add the deletion line after NativeChange made the card: the card now reads the reply when it is drawn).
    /// After an apply: the new-warnings line and the after-check verdict (G1 / #37).
    /// </summary>
    public static class ApplyCardLogic
    {
        public sealed class Line
        {
            public string Text;
            /// <summary>True for a line to look at (a new warning, a deletion that takes others along, a refusal).</summary>
            public bool Key;
        }

        private static string S(JsonNode n) => n is JsonValue ? n.ToString() : null;
        private static bool B(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        private static int I(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

        /// <summary>The safety lines of a preview reply, in order: warnings, then deletions.</summary>
        public static List<Line> Checks(JsonObject result)
        {
            var lines = new List<Line>();
            if (result == null) return lines;
            if (result["newWarnings"] is JsonObject nw && S(nw["line"]) is string wl)
                lines.Add(new Line { Text = wl, Key = I(nw["count"]) > 0 || B(nw["refuse"]) });
            if (result["targetedWarnings"] is JsonObject tw && S(tw["line"]) is string tl)
                lines.Add(new Line { Text = tl, Key = !B(tw["ok"]) });
            var wc = result["wouldChange"] as JsonObject ?? result["changed"] as JsonObject;
            if (wc?["deletionProbe"] is JsonObject dp) AddProbe(lines, dp, null);
            foreach (var pass in (result["passes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (pass["deletionProbe"] is JsonObject pp) AddProbe(lines, pp, $"Pass {S(pass["pass"]) ?? "?"}: ");
            return lines;
        }

        private static void AddProbe(List<Line> lines, JsonObject probe, string prefix)
        {
            var held = (probe["held"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(h => S(h["line"])).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (S(probe["summary"]) is string sum) lines.Add(new Line { Text = prefix + sum, Key = held.Count > 0 || B(probe["capped"]) });
            foreach (var h in held.Take(5)) lines.Add(new Line { Text = prefix + h, Key = true });
            if (held.Count > 5) lines.Add(new Line { Text = $"{prefix}... and {held.Count - 5} more (see the reply)", Key = true });
        }

        /// <summary>
        /// The "Look at this before applying" lines as the reply holds them now (attention added after the card was made
        /// included), plus the after-check forecast; without the lines already shown by <see cref="Checks"/>.
        /// </summary>
        public static List<string> Attention(JsonObject result, IEnumerable<string> atCardTime = null)
        {
            var shown = new HashSet<string>(Checks(result).Select(l => l.Text), StringComparer.Ordinal);
            var wc = result?["wouldChange"] as JsonObject;
            var all = (atCardTime ?? Enumerable.Empty<string>())
                .Concat((wc?["attention"] as JsonArray ?? new JsonArray()).Select(S))
                .Concat((result?["notes"] as JsonArray ?? new JsonArray()).Select(S).Where(n => n != null && n.StartsWith("After-check forecast", StringComparison.Ordinal)));
            var list = new List<string>();
            foreach (var a in all)
                if (!string.IsNullOrWhiteSpace(a) && !shown.Contains(a) && !list.Contains(a)) list.Add(a);
            return list;
        }

        /// <summary>The lines after an apply: new warnings, and the after-check verdict when the tool has one.</summary>
        public static List<Line> AfterApply(JsonObject result)
        {
            var lines = new List<Line>();
            if (result == null) return lines;
            if (result["newWarnings"] is JsonObject nw && S(nw["line"]) is string wl) lines.Add(new Line { Text = wl, Key = I(nw["count"]) > 0 });
            if (result["targetedWarnings"] is JsonObject tw && S(tw["line"]) is string tl) lines.Add(new Line { Text = tl, Key = !B(tw["ok"]) });
            if (result["complete"] is JsonValue)
            {
                if (B(result["complete"]))
                {
                    var left = result["afterCheck"]?["stillOnDefault"] as JsonObject;
                    var where = S(left?["workset"]);
                    lines.Add(new Line { Text = "Applied and checked: done" + (left != null && !string.IsNullOrEmpty(where) ? $", {I(left["total"]).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)} left on {where}" : "") + ".", Key = false });
                }
                else lines.Add(new Line { Text = S(result["error"]) ?? "Applied, but the after-check found work not done.", Key = true });
            }
            return lines;
        }
    }
}
