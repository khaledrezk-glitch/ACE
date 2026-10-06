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

        private static int _temporary;

        /// <summary>
        /// ACE's own temporary work that is rolled back afterwards (e.g. picture views): its commits must not count
        /// as "the user changed the model", because the undo stack is left exactly as it was.
        /// </summary>
        public static IDisposable Temporary() { _temporary++; return new Scope(); }
        private sealed class Scope : IDisposable { private bool _done; public void Dispose() { if (!_done) { _done = true; _temporary--; } } }

        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var op = e.Operation;
            if (_temporary > 0 && _current == null) return;
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
            private readonly Dictionary<long, string> _names = new Dictionary<long, string>();
            private readonly HashSet<long> _sensitive = new HashSet<long>();   // not building elements: parameters, types, views...

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

            /// <summary>While true, changes are not recorded (e.g. temporary preview views).</summary>
            public bool Paused { get; set; }
            public ICollection<long> AddedIds => _added;
            public ICollection<long> ModifiedIds => _modified;

            public void Add(DocumentChangedEventArgs e)
            {
                if (Paused) return;
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

            /// <summary>
            /// Names what changes, for people: call before a preview is rolled back (added elements still exist) and again
            /// after it (deleted elements exist again). After a real apply, deleted elements can no longer be named.
            /// </summary>
            public void CaptureNames(Document doc)
            {
                if (doc == null) return;
                foreach (var id in _added.Concat(_modified).Concat(_deleted).Where(i => !_names.ContainsKey(i)).Take(5000))
                {
                    try
                    {
                        var el = doc.GetElement(new ElementId(id));
                        if (el == null) continue;
                        _categories[id] = Label(el);
                        _names[id] = NameOf(doc, el);
                        // Data many elements depend on (not something Revit updates on its own, like views or tags).
                        if (el is ElementType || el is ParameterElement || el is Family) _sensitive.Add(id);
                    }
                    catch { }
                }
            }

            private static string Label(Element el) =>
                el.Category?.Name ?? el switch
                {
                    SharedParameterElement _ => "Shared parameters",
                    ParameterElement _ => "Project parameters",
                    ElementType _ => "Types",
                    Family _ => "Families",
                    View _ => "Views",
                    _ => el.GetType().Name,
                };

            private static string NameOf(Document doc, Element el)
            {
                string n = null;
                try
                {
                    if (el is Autodesk.Revit.DB.Architecture.Room r) n = $"{r.Number} {r.Name}".Trim();
                    else if (el is FamilyInstance fi) n = $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}";
                    else if (el is ParameterElement pe) n = pe.GetDefinition()?.Name ?? pe.Name;
                    else n = el.Name;
                    if (string.IsNullOrWhiteSpace(n)) n = doc.GetElement(el.GetTypeId())?.Name;
                }
                catch { }
                return string.IsNullOrWhiteSpace(n) ? $"id {el.Id.Value}" : n;
            }

            /// <summary>Per change kind and category: the count and up to 6 names with how often each occurs.</summary>
            private JsonObject Details()
            {
                JsonObject Part(IEnumerable<long> ids)
                {
                    var part = new JsonObject();
                    foreach (var g in ids.GroupBy(i => _categories.TryGetValue(i, out var c) ? c : "Other").OrderByDescending(g => g.Count()).Take(12))
                    {
                        var names = g.Where(i => _names.ContainsKey(i)).GroupBy(i => _names[i]).OrderByDescending(n => n.Count()).Take(6)
                                     .Select(n => n.Count() > 1 ? $"{n.Key} (x{n.Count()})" : n.Key).ToList();
                        part[g.Key] = $"{g.Count()}" + (names.Count > 0 ? ": " + string.Join(", ", names) + (g.Select(i => _names.TryGetValue(i, out var nm) ? nm : null).Distinct().Count() > 6 ? ", ..." : "") : "");
                    }
                    return part;
                }
                var d = new JsonObject();
                if (_added.Count > 0) d["added"] = Part(_added);
                if (_modified.Count > 0) d["modified"] = Part(_modified);
                if (_deleted.Count > 0) d["deleted"] = Part(_deleted);
                return d;
            }

            /// <summary>What a person should look at twice before applying: deletions, and changes to data that is not a building element.</summary>
            private JsonArray Attention()
            {
                var a = new JsonArray();
                foreach (var g in _deleted.GroupBy(i => _categories.TryGetValue(i, out var c) ? c : "elements").OrderByDescending(g => g.Count()).Take(6))
                {
                    var names = g.Select(i => _names.TryGetValue(i, out var n) ? n : null).Where(n => n != null).Distinct().Take(5).ToList();
                    a.Add($"Deletes {g.Count()} {g.Key}{(names.Count > 0 ? ": " + string.Join(", ", names) : "")}");
                }
                foreach (var g in _modified.Where(_sensitive.Contains).GroupBy(i => _categories.TryGetValue(i, out var c) ? c : "data").Take(4))
                {
                    var names = g.Select(i => _names.TryGetValue(i, out var n) ? n : null).Where(n => n != null).Distinct().Take(4).ToList();
                    a.Add($"Changes {g.Count()} {g.Key}{(names.Count > 0 ? ": " + string.Join(", ", names) : "")} (affects every element that uses them)");
                }
                return a;
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
                var details = Details();
                if (details.Count > 0) summary["details"] = details;
                var attention = Attention();
                if (attention.Count > 0) summary["attention"] = attention;
                if (_deleted.Count > 0) summary["deletedIds"] = new JsonArray(_deleted.Take(200).Select(i => (JsonNode)i).ToArray());
                if (_modified.Count > 0) summary["modifiedIds"] = new JsonArray(_modified.Take(500).Select(i => (JsonNode)i).ToArray());
                if (_added.Count > 0 && _added.Count <= 200) summary["addedIds"] = new JsonArray(_added.Select(i => (JsonNode)i).ToArray());
                summary["note"] = "Counts include elements Revit updates automatically (e.g. views, tags, joined walls).";
                return summary;
            }
        }
    }
}
