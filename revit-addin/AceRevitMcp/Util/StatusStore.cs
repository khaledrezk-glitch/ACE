using System;
using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The latest result of each engine, per model: model check, clashes, changes, plus the clash-view session. Engines
    /// publish here; the Companion, the dashboard, the Clash Browser and Claude read from here, so every surface shows
    /// the same numbers for the same model (concept D8). It is the in-session half of the Project Hub.
    /// </summary>
    internal static class StatusStore
    {
        /// <summary>The clash comparison a model is being reviewed with (shared by the Clash Browser and Claude).</summary>
        internal sealed class ClashSession
        {
            /// <summary>The primary model's name: null = this model; a link for a BIM manager comparing two links.</summary>
            public string Primary;
            /// <summary>The model compared with: null = all loaded links.</summary>
            public string With;
            public string FocusedKey;
        }

        internal sealed class ModelStatus
        {
            public Dashboard.Insights Health;
            public string HealthReport;

            public List<Coordination.Clash> Clashes;
            public List<Coordination.ClashIssue> ClashIssues = new List<Coordination.ClashIssue>();
            public string ClashReport;
            public int OpenClashes, NewClashes;

            public List<Tracking.ModelDiff> Changes;
            public string ChangesReport;
            public int ChangeCount;

            /// <summary>Compliance per requirement from the last model check (C1+); null when the model has no project file.</summary>
            public Rules.Compliance.Run Compliance;
            /// <summary>The run before <see cref="Compliance"/> (for "newly met / newly not met"); null when there is none.</summary>
            public Rules.Compliance.Run PreviousCompliance;
            /// <summary>True when <see cref="Compliance"/> was read back from disk when the model opened (not run in this session).</summary>
            public bool ComplianceFromDisk;
            /// <summary>For a run read back from disk: the model's file was saved after that audit (changes made then are not counted).</summary>
            public bool SavedAfterAudit;

            public readonly ClashSession Session = new ClashSession();
        }

        private static readonly Dictionary<string, ModelStatus> ByModel = new Dictionary<string, ModelStatus>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when an engine publishes (on Revit's main thread).</summary>
        public static event Action Changed;

        public static ModelStatus For(string model) =>
            !string.IsNullOrEmpty(model) && ByModel.TryGetValue(model, out var s) ? s : null;

        /// <summary>The entry for a model, created when missing (e.g. to hold its clash session).</summary>
        public static ModelStatus Entry(string model)
        {
            if (!ByModel.TryGetValue(model, out var s)) ByModel[model] = s = new ModelStatus();
            return s;
        }

        public static void PublishHealth(string model, Dashboard.Insights x, string report)
        {
            var e = Entry(model); e.Health = x; e.HealthReport = report; Raise();
        }

        /// <summary>Publishes clash results; the issues and counts are computed here once, not by every reader.</summary>
        public static void PublishClashes(string model, List<Coordination.Clash> clashes, string report = null)
        {
            var e = Entry(model);
            e.Clashes = clashes;
            e.ClashIssues = Coordination.ClashLogic.Issues(clashes);
            e.OpenClashes = clashes.Count(Coordination.ClashLogic.IsOpen);
            e.NewClashes = clashes.Count(c => c.Status == "new");
            if (report != null) e.ClashReport = report;
            Raise();
        }

        public static void PublishChanges(string model, List<Tracking.ModelDiff> diffs, string report)
        {
            var e = Entry(model); e.Changes = diffs; e.ChangesReport = report; e.ChangeCount = diffs.Sum(d => d.Changes.Count); Raise();
        }

        /// <summary>
        /// A new compliance run: the one it replaces becomes the previous run (the deltas compare with it), and the change
        /// count "since last audit" starts again. <paramref name="previous"/> and <paramref name="fromDisk"/> are for the run
        /// read back when a model opens (DM1): it keeps its own previous run and does not reset the count.
        /// </summary>
        public static void PublishCompliance(string model, Rules.Compliance.Run run, Rules.Compliance.Run previous = null, bool fromDisk = false)
        {
            var e = Entry(model);
            if (fromDisk) e.PreviousCompliance = previous;
            else if (run != null && e.Compliance != null && e.Compliance.Id != run.Id) e.PreviousCompliance = e.Compliance;
            e.Compliance = run;
            e.ComplianceFromDisk = fromDisk && run != null;
            if (!fromDisk) e.SavedAfterAudit = false;
            if (!fromDisk && run != null) ChangeTracker.ResetSinceAudit(model);
            Raise();
        }

        private static void Raise() { try { Changed?.Invoke(); } catch { } }
    }
}
