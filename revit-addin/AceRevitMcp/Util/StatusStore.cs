using System;
using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The latest result of each engine, per model: model check, clashes, changes. Engines publish here; the Companion,
    /// the dashboard and Claude read from here, so every surface shows the same numbers (concept D8). It is the
    /// in-session half of the Project Hub.
    /// </summary>
    internal static class StatusStore
    {
        internal sealed class ModelStatus
        {
            public Dashboard.Insights Health;
            public List<Coordination.Clash> Clashes;
            public DateTime? ClashesAt;
            public List<Tracking.ModelDiff> Changes;
            public DateTime? ChangesAt;

            public int OpenClashes => Clashes?.Count(Coordination.ClashLogic.IsOpen) ?? 0;
            public int NewClashes => Clashes?.Count(c => c.Status == "new") ?? 0;
            public int ClashIssues => Clashes == null ? 0 : Coordination.ClashLogic.Issues(Clashes).Count;
            public int ChangeCount => Changes?.Sum(d => d.Changes.Count) ?? 0;
        }

        private static readonly Dictionary<string, ModelStatus> ByModel = new Dictionary<string, ModelStatus>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when an engine publishes (on Revit's main thread).</summary>
        public static event Action Changed;

        public static ModelStatus For(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            return ByModel.TryGetValue(model, out var s) ? s : null;
        }

        private static ModelStatus Entry(string model)
        {
            if (!ByModel.TryGetValue(model, out var s)) ByModel[model] = s = new ModelStatus();
            return s;
        }

        public static void PublishHealth(string model, Dashboard.Insights x) { Entry(model).Health = x; Raise(); }

        public static void PublishClashes(string model, List<Coordination.Clash> clashes)
        {
            var e = Entry(model); e.Clashes = clashes; e.ClashesAt = DateTime.Now; Raise();
        }

        public static void PublishChanges(string model, List<Tracking.ModelDiff> diffs)
        {
            var e = Entry(model); e.Changes = diffs; e.ChangesAt = DateTime.Now; Raise();
        }

        private static void Raise() { try { Changed?.Invoke(); } catch { } }
    }
}
