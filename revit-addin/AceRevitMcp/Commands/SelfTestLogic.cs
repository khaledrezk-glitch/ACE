using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The pure rules of ace_self_test (no Revit types, tested in tools/tracker-test): the fingerprint of an open document
    /// and its comparison before and after, the one-line result per tool, the overall line and the report text, and when
    /// the self-test must not start.
    /// </summary>
    internal static class SelfTestLogic
    {
        internal const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";

        /// <summary>An open document as the self-test sees it before and after: identity, IsModified, counts, the type list hash.</summary>
        internal sealed class DocPrint
        {
            public string Key;          // the file path, else the title (never-saved documents)
            public string Title;
            public bool IsModified;
            public long Elements = -1;  // elements that are not element types; -1 when it could not be read
            public long Types = -1;     // element types
            public string TypeHash;     // SHA-256 over the sorted "id|name" lines of every element type; null when not readable
        }

        /// <summary>One tool's outcome: PASS, FAIL (with the reason) or SKIP (with the reason), and how long it took.</summary>
        internal sealed class ToolResult
        {
            public string Tool;
            public string Status;
            public string Reason;
            public double Seconds;
        }

        /// <summary>The hash of an element type list: order does not matter, any added, removed or renamed type changes it.</summary>
        internal static string TypeHash(IEnumerable<string> typeLines)
        {
            var sorted = (typeLines ?? Enumerable.Empty<string>()).Select(l => l ?? "").OrderBy(l => l, StringComparer.Ordinal);
            return Util.Compat.Sha256Hex(string.Join("\n", sorted));
        }

        /// <summary>
        /// Every difference between the open documents before and after the self-test, one line each ("open model X changed:
        /// ..."); empty means untouched. A document closed during the test, or one still open that was not before, is a
        /// difference too, and so is one that could not be read (not proven unchanged). Documents are matched by key (path,
        /// else title).
        /// </summary>
        internal static List<string> Compare(IList<DocPrint> before, IList<DocPrint> after)
        {
            var lines = new List<string>();
            var now = (after ?? new List<DocPrint>()).GroupBy(d => d.Key ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var b in before ?? new List<DocPrint>())
            {
                if (!now.TryGetValue(b.Key ?? "", out var a)) { lines.Add($"open model {b.Title} is no longer open"); continue; }
                now.Remove(b.Key ?? "");
                var what = new List<string>();
                if (a.IsModified != b.IsModified) what.Add($"IsModified {Yes(b.IsModified)} -> {Yes(a.IsModified)}");
                if (a.Elements != b.Elements) what.Add($"elements {Count(b.Elements)} -> {Count(a.Elements)}");
                if (a.Types != b.Types) what.Add($"element types {Count(b.Types)} -> {Count(a.Types)}");
                else if (!string.Equals(a.TypeHash, b.TypeHash, StringComparison.Ordinal)) what.Add(a.TypeHash == null || b.TypeHash == null ? "element type list not readable" : "element type list differs (same count)");
                if (what.Count > 0) lines.Add($"open model {b.Title} changed: {string.Join("; ", what)}");
                else if (b.Elements < 0 || b.Types < 0 || b.TypeHash == null) lines.Add($"open model {b.Title} could not be read: not proven unchanged");
            }
            foreach (var extra in now.Values) lines.Add($"a document is open that was not before the test: {extra.Title}");
            return lines;
        }

        private static string Yes(bool b) => b ? "yes" : "no";
        private static string Count(long n) => n < 0 ? "unknown" : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>"PASS workset apply (0.8 s)", "FAIL parameter fill: reason", "SKIP purge unused: reason".</summary>
        internal static string Line(ToolResult r)
        {
            var status = r.Status == Pass || r.Status == Fail || r.Status == Skip ? r.Status : Fail;
            if (status == Pass) return $"{Pass} {r.Tool} ({Seconds(r.Seconds)})";
            var reason = OneLine(r.Reason);
            return $"{status} {r.Tool}: {(reason.Length == 0 ? (status == Fail ? "no reason given" : "not run") : reason)}";
        }

        /// <summary>"OVERALL PASS: 6 of 6 tools passed (12.3 s)" or "OVERALL FAIL: 1 of 6 tools failed, 1 skipped (12.3 s)"; no results is a FAIL.</summary>
        internal static string Overall(IList<ToolResult> results, double seconds)
        {
            var all = results ?? new List<ToolResult>();
            var failed = all.Count(r => r.Status != Pass && r.Status != Skip);
            var skipped = all.Count(r => r.Status == Skip);
            var passed = all.Count(r => r.Status == Pass);
            var skipText = skipped > 0 ? $", {skipped} skipped" : "";
            if (all.Count == 0) return $"OVERALL FAIL: no tool ran ({Seconds(seconds)})";
            return failed > 0
                ? $"OVERALL FAIL: {failed} of {all.Count} tools failed{skipText} ({Seconds(seconds)})"
                : $"OVERALL PASS: {passed} of {all.Count} tools passed{skipText} ({Seconds(seconds)})";
        }

        /// <summary>True when nothing failed (skips do not fail the run) and at least one tool ran.</summary>
        internal static bool Passed(IList<ToolResult> results) =>
            results != null && results.Count > 0 && results.All(r => r.Status == Pass || r.Status == Skip);

        /// <summary>The report file's text: a header, one line per tool, the overall line, then notes.</summary>
        internal static string Report(IEnumerable<string> header, IList<ToolResult> results, double seconds, IEnumerable<string> notes)
        {
            var sb = new StringBuilder();
            foreach (var h in header ?? Enumerable.Empty<string>()) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            foreach (var r in results ?? new List<ToolResult>()) sb.Append(Line(r)).Append("\r\n");
            sb.Append(Overall(results, seconds)).Append("\r\n");
            var n = (notes ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (n.Count > 0)
            {
                sb.Append("\r\nNotes:\r\n");
                foreach (var x in n) sb.Append("- ").Append(OneLine(x, 600)).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>"self-test-2026-10-24_153012.txt" (local time, sortable).</summary>
        internal static string FileName(DateTime at) => "self-test-" + at.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".txt";

        /// <summary>
        /// Why the self-test must not start now, or null: another ACE command is running, a document has a transaction open
        /// (someone is editing), or a self-test already runs.
        /// </summary>
        internal static string Refusal(string runningCommand, IEnumerable<string> modifiableDocuments, bool selfTestRunning)
        {
            if (selfTestRunning) return "A self-test is already running. Nothing was started.";
            if (!string.IsNullOrEmpty(runningCommand) && runningCommand != "ace_self_test")
                return $"ACE is running '{runningCommand}'. Wait for it to finish, then start the self-test again. Nothing was started.";
            var open = (modifiableDocuments ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrEmpty(t)).ToList();
            if (open.Count > 0)
                return $"A transaction is open in {string.Join(", ", open)} (a command or an edit is in progress). Finish or cancel it, then start the self-test again. Nothing was started.";
            return null;
        }

        /// <summary>One line, at most max characters (an ellipsis when cut).</summary>
        internal static string OneLine(string text, int max = 300)
        {
            var s = string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        internal static string Seconds(double s) => (s < 0 ? 0 : s).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }
}
