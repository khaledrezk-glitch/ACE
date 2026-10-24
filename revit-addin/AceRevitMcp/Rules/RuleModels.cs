using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Which models a project rule applies to (G9): a rule's "models" key, a string or a list of patterns matched against
    /// the names the model is known by (title, central and file name without folder or .rvt, cloud ids), case-insensitive,
    /// exact or with * wildcards; "!pattern" excludes. No key (or no pattern): every model. The key limits the rule only with
    /// "modelsScope": "only" (<see cref="Limits"/>). Pure, no Revit types; the MCP
    /// server has the same logic (lib/worksets.js ruleAppliesTo, modelsLimit) for the rules it sends.
    /// </summary>
    internal static class RuleModels
    {
        private static string Norm(string s)
        {
            var n = (s ?? "").Trim();
            if (n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n.ToLowerInvariant();
        }

        /// <summary>
        /// The one name project data is recorded under for a model (e.g. its agreed NWC export view, #36): the central's file
        /// name for a workshared model (so a local and its central share it), else the file's name, else the title; without
        /// folder, ".rvt" or a detached copy's "_detached"; case kept. One of <see cref="Names"/> (lowercased).
        /// </summary>
        public static string RecordKey(string title, string centralPath, string pathName)
        {
            string Bare(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return null;
                var n = p.Trim();
                var i = n.LastIndexOfAny(new[] { '\\', '/' });
                if (i >= 0) n = n.Substring(i + 1);
                if (n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                if (n.EndsWith("_detached", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 9);
                n = n.Trim();
                return n.Length == 0 ? null : n;
            }
            return Bare(centralPath) ?? Bare(pathName) ?? Bare(title);
        }

        /// <summary>The names a model is known by, from its title and its project keys (paths, cloud ids, title).</summary>
        public static List<string> Names(string title, IEnumerable<string> keys)
        {
            var raw = new List<string> { title };
            foreach (var k in keys ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                raw.Add(k);
                if (k.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    try { raw.Add(k.Split('\\', '/').Last()); } catch { }
            }
            var names = new List<string>();
            foreach (var n in raw.Select(Norm).Where(n => n.Length > 0))
            {
                if (!names.Contains(n)) names.Add(n);
                if (n.EndsWith("_detached") && !names.Contains(n.Substring(0, n.Length - 9))) names.Add(n.Substring(0, n.Length - 9));
            }
            return names;
        }

        private static bool Hit(string pattern, IList<string> names)
        {
            var rx = new Regex("^" + string.Join(".*", Norm(pattern).Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase);
            return names.Any(n => rx.IsMatch(n));
        }

        /// <summary>Whether a rule with this models key applies to a model with these names; why not, in words.</summary>
        public static bool Applies(JsonNode models, IList<string> names, out string why)
        {
            why = null;
            var pats = (models == null ? new List<string>() : models is JsonArray a ? a.Select(x => x?.ToString()).ToList() : new List<string> { models.ToString() })
                .Select(p => (p ?? "").Trim()).Where(p => p.Length > 0 && p != "!").ToList();
            if (pats.Count == 0) return true;
            var ex = pats.Where(p => p.StartsWith("!")).Select(p => p.Substring(1).Trim()).FirstOrDefault(p => Hit(p, names));
            if (ex != null) { why = "excluded by !" + ex; return false; }
            var inc = pats.Where(p => !p.StartsWith("!")).ToList();
            if (inc.Count > 0 && !inc.Any(p => Hit(p, names))) { why = "models " + string.Join(", ", inc); return false; }
            return true;
        }

        /// <summary>
        /// Does the rule's models key limit it? Only with modelsScope "only" (written by workset_plan from 1.14.10, or by
        /// hand): rules saved before list where they were proposed (the first model only), so they keep applying everywhere.
        /// </summary>
        public static bool Limits(JsonNode scope) => string.Equals((scope?.ToString() ?? "").Trim(), "only", StringComparison.OrdinalIgnoreCase);

        /// <summary>The line for rules without the marker that list other models (applied as before); null when none. Same text as the server.</summary>
        public static string LegacyText(int count) => count == 0 ? null
            : $"{count} rule(s) list other models in `models` (saved before 1.14.10, read as where they were proposed, not a limit): set modelsScope: \"only\" to limit them.";

        /// <summary>One compact line: the rules skipped for this model and why (counts, up to 10 names); null when none.</summary>
        public static string SkippedText(IList<(string Rule, string Why)> skipped, string model, int total)
        {
            if (skipped == null || skipped.Count == 0) return null;
            var more = skipped.Count > 10 ? $", +{skipped.Count - 10} more" : "";
            return $"Rules skipped for {model ?? "this model"} (their models key does not name it): {skipped.Count} of {total}: {string.Join(", ", skipped.Take(10).Select(s => $"{s.Rule} ({s.Why})"))}{more}.";
        }
    }
}
