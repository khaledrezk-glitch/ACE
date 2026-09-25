using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Watches Revit's DocumentChanged event so that:
    ///  - every Claude command reports exactly what it added / modified / deleted (also for dry runs,
    ///    because dry runs commit inside a TransactionGroup that is then rolled back), and
    ///  - "undo last Claude change" is only allowed when the top of Revit's undo stack is really
    ///    Claude's change (not something the user did afterwards).
    /// All access happens on Revit's main thread.
    /// </summary>
    internal static class ChangeTracker
    {
        private static Recording _current;

        /// <summary>True while the most recent committed change in the model came from Claude.</summary>
        public static bool LastChangeWasClaude { get; private set; }
        public static string LastClaudeChangeName { get; private set; }
        public static string LastClaudeChangeDocument { get; private set; }

        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var op = e.Operation;
            if (_current != null)
            {
                if (op == UndoOperation.TransactionCommitted)
                {
                    _current.Add(e);
                    LastChangeWasClaude = true;
                    LastClaudeChangeName = _current.Name;
                    LastClaudeChangeDocument = e.GetDocument().Title;
                }
                return;
            }

            // Anything the user does (commit, undo, redo) means the undo stack top is no longer ours.
            if (op == UndoOperation.TransactionCommitted || op == UndoOperation.TransactionUndone || op == UndoOperation.TransactionRedone)
                LastChangeWasClaude = false;
        }

        public static void MarkUndone() => LastChangeWasClaude = false;

        public static Recording Begin(string name)
        {
            _current = new Recording(name);
            return _current;
        }

        public static void End(Recording recording, bool keptChanges)
        {
            if (_current == recording) _current = null;
            // A rolled-back (dry) run leaves the undo stack exactly as it was before.
            if (!keptChanges && recording.Committed > 0)
            {
                LastChangeWasClaude = recording.PreviousWasClaude;
                LastClaudeChangeName = recording.PreviousName;
                LastClaudeChangeDocument = recording.PreviousDocument;
            }
        }

        internal sealed class Recording
        {
            private readonly HashSet<long> _added = new HashSet<long>();
            private readonly HashSet<long> _modified = new HashSet<long>();
            private readonly HashSet<long> _deleted = new HashSet<long>();
            private readonly Dictionary<long, string> _categories = new Dictionary<long, string>();

            public Recording(string name)
            {
                Name = name;
                PreviousWasClaude = LastChangeWasClaude;
                PreviousName = LastClaudeChangeName;
                PreviousDocument = LastClaudeChangeDocument;
            }

            public string Name { get; }
            public int Committed { get; private set; }
            internal bool PreviousWasClaude { get; }
            internal string PreviousName { get; }
            internal string PreviousDocument { get; }

            public void Add(DocumentChangedEventArgs e)
            {
                Committed++;
                var doc = e.GetDocument();
                foreach (var id in e.GetAddedElementIds()) { _added.Add(id.Value); Remember(doc, id); }
                foreach (var id in e.GetModifiedElementIds()) { if (!_added.Contains(id.Value)) _modified.Add(id.Value); Remember(doc, id); }
                foreach (var id in e.GetDeletedElementIds())
                {
                    // Created and deleted within the same run: net effect is nothing.
                    if (!_added.Remove(id.Value)) _deleted.Add(id.Value);
                    _modified.Remove(id.Value);
                }
            }

            private void Remember(Document doc, ElementId id)
            {
                if (_categories.ContainsKey(id.Value)) return;
                string label;
                try
                {
                    var el = doc.GetElement(id);
                    label = el?.Category?.Name ?? (el is ElementType ? "Types" : el?.GetType().Name) ?? "Other";
                }
                catch { label = "Other"; }
                _categories[id.Value] = label;
            }

            public JsonObject Summary()
            {
                JsonObject ByCategory(IEnumerable<long> ids)
                {
                    var obj = new JsonObject();
                    foreach (var g in ids.GroupBy(i => _categories.TryGetValue(i, out var c) ? c : "Other").OrderByDescending(g => g.Count()).Take(25))
                        obj[g.Key] = g.Count();
                    return obj;
                }

                var summary = new JsonObject
                {
                    ["added"] = _added.Count,
                    ["modified"] = _modified.Count,
                    ["deleted"] = _deleted.Count,
                };
                if (_added.Count > 0) summary["addedByCategory"] = ByCategory(_added);
                if (_modified.Count > 0) summary["modifiedByCategory"] = ByCategory(_modified);
                if (_deleted.Count > 0) summary["deletedIds"] = new JsonArray(_deleted.Take(200).Select(i => (JsonNode)i).ToArray());
                if (_added.Count > 0 && _added.Count <= 200) summary["addedIds"] = new JsonArray(_added.Select(i => (JsonNode)i).ToArray());
                summary["note"] = "Counts include elements Revit updates automatically (e.g. views, tags, joined walls).";
                return summary;
            }
        }
    }
}
