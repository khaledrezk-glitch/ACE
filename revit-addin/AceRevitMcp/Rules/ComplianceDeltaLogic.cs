using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// "This audit vs the last one" for people (DM2 / DM3), pure: the met percentage, what changed per requirement between
    /// two compliance runs (newly met, newly not met, counts), and the Companion's words for it (Home tile, the "since last
    /// audit" line, the Compliance tab header, each requirement card and the re-audit band). Times are passed in as local
    /// times, so the words are testable.
    /// </summary>
    public static class ComplianceDeltaLogic
    {
        private static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public static bool IsOpen(string status) => status == "not-met" || status == "partly";
        public static int Count(Compliance.Run run, string status) => run?.Results.Count(r => r.Status == status) ?? 0;
        public static int Checkable(Compliance.Run run) => Count(run, "met") + Count(run, "partly") + Count(run, "not-met");

        /// <summary>Met of the checkable requirements, in whole percent (as the Compliance tab shows it); null when none is checkable.</summary>
        public static int? Percent(Compliance.Run run)
        {
            var checkable = Checkable(run);
            return checkable == 0 ? (int?)null : 100 * Count(run, "met") / checkable;
        }

        public sealed class Change
        {
            public string Requirement, Before, After;
            public int? BeforeCount, AfterCount;
            public bool NewlyMet => After == "met" && Before != null && Before != "met";
            public bool NewlyNotMet => Before == "met" && IsOpen(After);
        }

        public sealed class Delta
        {
            public List<Change> Changes = new List<Change>();
            public int MetBefore, MetAfter;
            public int? PercentBefore, PercentAfter;
            public int NewlyMet => Changes.Count(c => c.NewlyMet);
            public int NewlyNotMet => Changes.Count(c => c.NewlyNotMet);
            /// <summary>Met now minus met before (requirements added or removed between the runs count too).</summary>
            public int Net => MetAfter - MetBefore;
            public Change For(string requirement) => Changes.FirstOrDefault(c => string.Equals(c.Requirement, requirement, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What changed from <paramref name="before"/> to <paramref name="after"/>: requirements whose status or count differ.</summary>
        public static Delta Compare(Compliance.Run before, Compliance.Run after)
        {
            var d = new Delta { MetBefore = Count(before, "met"), MetAfter = Count(after, "met"), PercentBefore = Percent(before), PercentAfter = Percent(after) };
            if (before == null || after == null) return d;
            var old = before.Results.Where(r => r.Requirement != null).GroupBy(r => r.Requirement, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var r in after.Results.Where(r => r.Requirement != null))
            {
                if (!old.TryGetValue(r.Requirement, out var o)) continue;   // a new requirement: no "before"
                if (o.Status == r.Status && o.Count == r.Count) continue;
                d.Changes.Add(new Change { Requirement = r.Requirement, Before = o.Status, After = r.Status, BeforeCount = o.Count, AfterCount = r.Count });
            }
            return d;
        }

        public static string StatusWord(string s) => s switch { "not-met" => "NOT MET", "partly" => "PARTLY", "met" => "MET", _ => "NOT CHECKABLE" };

        private static string N(int? n) => n.HasValue ? n.Value.ToString("#,0", En) : "-";

        /// <summary>The line on a requirement card, e.g. "Newly met since the last audit: was NOT MET (2,140), now 0"; null when unchanged.</summary>
        public static string CardText(Change c)
        {
            if (c == null) return null;
            string Was() => StatusWord(c.Before) + (c.BeforeCount.HasValue ? $" ({N(c.BeforeCount)})" : "");
            if (c.NewlyMet) return $"Newly met since the last audit: was {Was()}, now {N(c.AfterCount ?? 0)}";
            if (c.NewlyNotMet) return $"Newly not met since the last audit: was MET, now {StatusWord(c.After)}{(c.AfterCount.HasValue ? $" ({N(c.AfterCount)})" : "")}";
            if (c.Before == c.After) return $"Since the last audit: was {N(c.BeforeCount)}, now {N(c.AfterCount)}";
            return $"Since the last audit: was {Was()}, now {StatusWord(c.After)}{(c.AfterCount.HasValue ? $" ({N(c.AfterCount)})" : "")}";
        }

        /// <summary>"today 17:10", "Tue 17:10" (this week) or "20 Oct 17:10".</summary>
        public static string When(DateTime local, DateTime nowLocal)
        {
            if (local.Date == nowLocal.Date) return "today " + local.ToString("HH:mm", En);
            if (local.Date < nowLocal.Date && (nowLocal.Date - local.Date).TotalDays < 7) return local.ToString("ddd HH:mm", En);
            return local.ToString("d MMM HH:mm", En);
        }

        /// <summary>A run's time (UTC, "yyyy-MM-ddTHH:mm:ssZ") as a local time; null when unreadable.</summary>
        public static DateTime? LocalTime(string time) =>
            DateTime.TryParse(time, En, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : (DateTime?)null;

        /// <summary>The Compliance tab header's delta, e.g. "Since the previous audit (Mon 17:10): 43% to 61% · 5 newly met · 0 newly not met".</summary>
        public static string HeaderText(Delta d, DateTime? previousLocal, DateTime nowLocal)
        {
            if (d == null || previousLocal == null) return null;
            var when = When(previousLocal.Value, nowLocal);
            if (d.Changes.Count == 0 && d.MetBefore == d.MetAfter) return $"Same result as the previous audit ({when})";
            var pct = d.PercentBefore.HasValue && d.PercentAfter.HasValue ? $"{d.PercentBefore}% to {d.PercentAfter}% · " : "";
            return $"Since the previous audit ({when}): {pct}{d.NewlyMet} newly met · {d.NewlyNotMet} newly not met";
        }

        /// <summary>The Home tile's note: "against TST-EIR rev B +2 · last audit Tue 17:10".</summary>
        public static string TileNote(IList<string> documents, DateTime? auditLocal, DateTime nowLocal)
        {
            var docs = (documents ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var against = docs.Count == 0 ? null : "against " + docs[0] + (docs.Count > 1 ? $" +{docs.Count - 1}" : "");
            var when = auditLocal.HasValue ? "last audit " + When(auditLocal.Value, nowLocal) : null;
            return string.Join(" · ", new[] { against, when }.Where(x => x != null));
        }

        /// <summary>
        /// Home's line under the tiles: "Since last audit (Tue 17:10): 3 changes, 9 not met". For a run read back from disk the
        /// changes are counted from when the model opened (said so); when the file was saved after the audit, that is said too.
        /// </summary>
        public static string SinceAuditText(DateTime auditLocal, DateTime nowLocal, int changes, int notMet, bool fromDisk, bool savedAfterAudit)
        {
            var c = $"{N(changes)} change{(changes == 1 ? "" : "s")}{(fromDisk ? " since the model opened" : "")}";
            var line = $"Since last audit ({When(auditLocal, nowLocal)}): {c}, {N(notMet)} not met";
            return savedAfterAudit ? line + " · the file was saved after this audit: Re-run audit to be sure" : line;
        }
    
        // ---- the re-audit band (DM3) ------------------------------------------------------------------------

        /// <summary>The band after an automatic re-audit: its words and tone (better, worse, same, failed, skipped).</summary>
        public sealed class Band
        {
            public string Text, Tone, Reason, RunId;
            public DateTime At;
        }

        private static string Lead(string reason) => reason switch { "undo" => "After undo (Ctrl+Z): ", "redo" => "After redo (Ctrl+Y): ", _ => "" };
        private static string What(string reason) => reason switch { "undo" => "the undo", "redo" => "the redo", _ => "your apply" };
        private static string Reqs(int n) => n == 1 ? "requirement" : "requirements";

        /// <summary>
        /// "5 more requirements met since the last audit (43% to 61%) · 1 undo step (Ctrl+Z)", or "N fewer ..." (tone worse),
        /// or "no change"; after an undo or a redo it says so and has no undo step.
        /// </summary>
        public static Band BandFor(Compliance.Run before, Compliance.Run after, string reason, DateTime at)
        {
            var d = Compare(before, after);
            var pct = d.PercentBefore.HasValue && d.PercentAfter.HasValue ? (d.PercentBefore == d.PercentAfter ? $" ({d.PercentAfter}%)" : $" ({d.PercentBefore}% to {d.PercentAfter}%)") : "";
            var undo = reason == "apply" ? " · 1 undo step (Ctrl+Z)" : "";
            string text, tone;
            if (before == null) { text = $"{Lead(reason)}Re-audited after {What(reason)}: {Count(after, "met")} {Reqs(Count(after, "met"))} met{(d.PercentAfter.HasValue ? $" ({d.PercentAfter}%)" : "")}{undo}"; tone = "same"; }
            else if (d.Net > 0) { text = $"{Lead(reason)}{d.Net} more {Reqs(d.Net)} met since the last audit{pct}{undo}"; tone = "better"; }
            else if (d.Net < 0) { text = $"{Lead(reason)}{-d.Net} fewer {Reqs(-d.Net)} met since the last audit{pct}{undo}"; tone = "worse"; }
            else { text = $"{Lead(reason)}Re-audited after {What(reason)}: no change in requirements met{pct}{undo}"; tone = "same"; }
            if (d.Net >= 0 && d.NewlyNotMet > 0) { text += $" · {d.NewlyNotMet} newly not met"; tone = "worse"; }
            return new Band { Text = text, Tone = tone, Reason = reason, RunId = after?.Id, At = at };
        }

        /// <summary>The band when the re-audit could not run: the change itself stands.</summary>
        public static Band Failed(string reason, string error, DateTime at) => new Band
        {
            Text = $"Re-audit after {What(reason)} failed: {(string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim().TrimEnd('.'))}. The change itself stands; press Re-run audit.",
            Tone = "failed", Reason = reason, At = at,
        };

        /// <summary>The band when the model check is too slow to run by itself after every change.</summary>
        public static Band Skipped(string reason, double seconds, DateTime at) => new Band
        {
            Text = $"Not re-audited after {What(reason)}: the model check took {seconds.ToString("0", En)} s last time. Press Re-run audit when ready.",
            Tone = "skipped", Reason = reason, At = at,
        };
    }
}
