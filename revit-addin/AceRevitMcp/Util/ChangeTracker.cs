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
            // A change while the bridge runs no request is the user's: a model ACE opened is no longer safe to discard. An ACE
            // change to elements also ends "only empty ACE runs" (close_model then needs discard again).
            Commands.ModelFileCommands.NoteChange(e, Bridge.RequestDispatcher.Busy != null, _temporary > 0 && _current == null);
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

        private const string StoreKey = "changeTracker.lastClaudeChange";
        private static bool _live;

        /// <summary>
        /// Payload Stop (reload_addin): the last-run state is plain data (a flag, the run's name, the model's title), so it
        /// is left in the loader's store and the next build can still undo the run made before the reload. Nothing can
        /// change a model during the swap (it runs while Revit is idle), so the flag stays true to the undo stack.
        /// </summary>
        public static void Save(IDictionary<string, object> store)
        {
            if (!_live || store == null) return;
            _live = false;
            store[StoreKey] = new Dictionary<string, object>
            {
                ["wasClaude"] = LastChangeWasClaude, ["name"] = LastClaudeChangeName, ["document"] = LastClaudeChangeDocument,
            };
        }

        public static void Restore(IDictionary<string, object> store)
        {
            _live = true;
            if (store == null || !store.TryGetValue(StoreKey, out var v) || !(v is Dictionary<string, object> d)) return;
            LastChangeWasClaude = d.TryGetValue("wasClaude", out var w) && w is bool b && b;
            LastClaudeChangeName = d.TryGetValue("name", out var n) ? n as string : null;
            LastClaudeChangeDocument = d.TryGetValue("document", out var t) ? t as string : null;
            store.Remove(StoreKey);
        }

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
            private readonly HashSet<long> _types = new HashSet<long>();       // element types (family types, wall types...): reported apart from elements
            private readonly Dictionary<long, int> _typeInstances = new Dictionary<long, int>();   // a deleted type: its instances while it existed

            public Recording(string name)
            {
                Name = name;
                PreviousWasClaude = LastChangeWasClaude;
                PreviousName = LastClaudeChangeName;
                PreviousDocument = LastClaudeChangeDocument;
            }

            public string Name { get; }
            public int Committed { get; private set; }
            /// <summary>Elements added, modified or deleted (resolved warning records included): 0 means the run changed no element.</summary>
            public int ElementChanges => _added.Count + _modified.Count + _deleted.Count;
            internal bool PreviousWasClaude { get; }
            internal string PreviousName { get; }
            internal string PreviousDocument { get; }

            /// <summary>While true, changes are not recorded (e.g. temporary preview views).</summary>
            public bool Paused { get; set; }
            /// <summary>
            /// Revit keeps a record element per warning (no category, on the "Reviewable Warnings" workset) and deletes it when
            /// the warning is resolved: those deletions are reported as resolved warning records, never as deleted elements.
            /// The caller lists the record ids before the change (after a real apply they can no longer be inspected).
            /// </summary>
            public void NoteWarningRecords(Document doc) { foreach (var id in WarningRecordIds(doc)) _warningRecords.Add(id); }
            private readonly HashSet<long> _warningRecords = new HashSet<long>();

            /// <summary>The ids of the warning records (elements without a category on a warnings workset); empty when not workshared.</summary>
            internal static List<long> WarningRecordIds(Document doc)
            {
                var list = new List<long>();
                try
                {
                    if (doc == null || !doc.IsWorkshared) return list;
                    foreach (var ws in new FilteredWorksetCollector(doc).ToWorksets())
                    {
                        if (ws.Name == null || ws.Name.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(ws.Id)))
                            if (e.Category == null) list.Add(e.Id.Value);
                    }
                }
                catch { /* not readable: nothing known */ }
                return list;
            }

            internal const string WarningRecordLabel = "resolved warning records";

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
                        if (_deleted.Contains(id) && IsWarningRecord(doc, el)) { _categories[id] = WarningRecordLabel; continue; }
                        _categories[id] = Label(el);
                        _names[id] = NameOf(doc, el);
                        // Data many elements depend on (not something Revit updates on its own, like views or tags).
                        if (el is ElementType || el is ParameterElement || el is Family) _sensitive.Add(id);
                        if (el is ElementType) _types.Add(id);
                    }
                    catch { }
                }
                // A deleted type that exists again (a rolled-back preview): how many instances use it. An unused duplicate type that
                // Revit drops on commit has none: it must not read as deleted building elements.
                var count = _deleted.Where(i => _types.Contains(i) && !_typeInstances.ContainsKey(i)).ToList();
                if (count.Count == 0) return;
                try
                {
                    var set = new HashSet<long>(count.Where(i => doc.GetElement(new ElementId(i)) != null));
                    if (set.Count == 0) return;
                    foreach (var i in set) _typeInstances[i] = 0;
                    foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                    {
                        long t;
                        try { t = e.GetTypeId().Value; } catch { continue; }
                        if (set.Contains(t)) _typeInstances[t]++;
                    }
                }
                catch { /* not counted: reported without an instance count */ }
            }

            /// <summary>A Revit warning record: an element without a category on the warnings workset.</summary>
            private static bool IsWarningRecord(Document doc, Element el)
            {
                try
                {
                    if (el.Category != null || !doc.IsWorkshared) return false;
                    var ws = doc.GetWorksetTable().GetWorkset(el.WorksetId);
                    return ws != null && ws.Name != null && ws.Name.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }

            /// <summary>Splits deleted ids into warning records (labelled so, or known records) and real deletions.</summary>
            internal static (List<long> Records, List<long> Deleted) SplitWarningRecords(IEnumerable<long> deleted, IDictionary<long, string> categories, ICollection<long> knownRecords)
            {
                var records = new List<long>(); var rest = new List<long>();
                foreach (var id in (deleted ?? Enumerable.Empty<long>()).OrderBy(i => i))
                {
                    var isRecord = (knownRecords != null && knownRecords.Contains(id)) || (categories != null && categories.TryGetValue(id, out var c) && c == WarningRecordLabel);
                    (isRecord ? records : rest).Add(id);
                }
                return (records, rest);
            }

            internal static string WarningRecordText(int n) => $"{n} resolved warning record{(n == 1 ? "" : "s")}";

            private (List<long> Records, List<long> Deleted) Split() => SplitWarningRecords(_deleted, _categories, _warningRecords);

            /// <summary>Real deletions split into elements (instances) and element types.</summary>
            private (List<long> Records, List<long> Elements, List<long> Types) SplitTypes()
            {
                var (records, deleted) = Split();
                return (records, deleted.Where(i => !_types.Contains(i)).ToList(), deleted.Where(_types.Contains).ToList());
            }

            /// <summary>
            /// The deletion lines a person reads before applying (plain logic, tested in tools/workset-test): deleted elements first
            /// ("Deletes N Walls"), then deleted types apart, with their instance count: "Removes 10 unused Structural Framing types
            /// (0 instances)" is Revit dropping unused duplicate types, not data loss; a type with instances says how many.
            /// instances: null = not known (the type could not be read again, e.g. after a real apply).
            /// </summary>
            internal static List<string> DeletionLines(IEnumerable<(string Category, string Name, bool IsType, int? Instances)> deleted, int maxGroups = 6)
            {
                var lines = new List<string>();
                var list = (deleted ?? Enumerable.Empty<(string, string, bool, int?)>()).ToList();
                string Names(IEnumerable<string> n, int max) { var x = n.Where(v => v != null).Distinct().Take(max).ToList(); return x.Count > 0 ? ": " + string.Join(", ", x) : ""; }
                foreach (var g in list.Where(d => !d.IsType).GroupBy(d => d.Category ?? "elements").OrderByDescending(g => g.Count()).Take(maxGroups))
                    lines.Add($"Deletes {g.Count()} {g.Key} element{(g.Count() == 1 ? "" : "s")}{Names(g.Select(d => d.Name), 5)}");
                foreach (var g in list.Where(d => d.IsType).GroupBy(d => d.Category ?? "Types").OrderByDescending(g => g.Count()).Take(maxGroups))
                {
                    var n = g.Count(); var s = n == 1 ? "" : "s";
                    var kind = g.Key == "Types" ? $"type{s}" : $"{g.Key} type{s}";
                    var known = g.Where(d => d.Instances.HasValue).ToList();
                    var inst = known.Sum(d => d.Instances.Value);
                    if (known.Count == n && inst == 0) lines.Add($"Removes {n} unused {kind} (0 instances; no placed element is deleted){Names(g.Select(d => d.Name), 5)}");
                    else if (known.Count == n) lines.Add($"Deletes {n} {kind} with {inst} instance{(inst == 1 ? "" : "s")}{Names(g.Select(d => d.Name), 5)}");
                    else lines.Add($"Removes {n} {kind} (instance count not known){Names(g.Select(d => d.Name), 5)}");
                }
                return lines;
            }

            /// <summary>The types part of a change summary for one line (cards, outcomes): ", removes 10 unused type(s)" or "".</summary>
            internal static string TypesText(JsonObject summary)
            {
                var n = summary?["deletedTypes"] is JsonValue v && v.TryGetValue<int>(out var x) ? x : 0;
                if (n == 0) return "";
                var inst = summary?["deletedTypeInstances"] is JsonValue w && w.TryGetValue<int>(out var y) ? y : (int?)null;
                return inst == 0 ? $", removes {n} unused type{(n == 1 ? "" : "s")}" : $", removes {n} type{(n == 1 ? "" : "s")}";
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
                var (records, deleted, types) = SplitTypes();
                if (deleted.Count > 0) d["deleted"] = Part(deleted);
                if (types.Count > 0) d["deletedTypes"] = Part(types);
                if (records.Count > 0) d["resolvedWarningRecords"] = WarningRecordText(records.Count) + " (Revit removed the records of warnings the change resolved; no model element is deleted)";
                return d;
            }

            /// <summary>What a person should look at twice before applying: deletions, and changes to data that is not a building element.</summary>
            private JsonArray Attention()
            {
                var a = new JsonArray();
                var (records, deleted) = Split();
                foreach (var line in DeletionLines(deleted.Select(i => (_categories.TryGetValue(i, out var c) ? c : _types.Contains(i) ? "Types" : "elements",
                             _names.TryGetValue(i, out var n) ? n : null, _types.Contains(i), _typeInstances.TryGetValue(i, out var k) ? k : (int?)null))))
                    a.Add(line);
                if (records.Count > 0) a.Add(WarningRecordText(records.Count) + " (warnings the change resolved; no model element is deleted)");
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

                var (records, deleted, types) = SplitTypes();
                var summary = new JsonObject
                {
                    ["added"] = _added.Count,
                    ["modified"] = _modified.Count,
                    ["deleted"] = deleted.Count,   // elements (instances); element types are counted apart
                };
                if (types.Count > 0)
                {
                    summary["deletedTypes"] = types.Count;
                    summary["deletedTypesByCategory"] = ByCategory(types);
                    if (types.All(_typeInstances.ContainsKey)) summary["deletedTypeInstances"] = types.Sum(i => _typeInstances[i]);
                }
                if (records.Count > 0) summary["resolvedWarningRecords"] = records.Count;
                if (_added.Count > 0) summary["addedByCategory"] = ByCategory(_added);
                if (_modified.Count > 0) summary["modifiedByCategory"] = ByCategory(_modified);
                var details = Details();
                if (details.Count > 0) summary["details"] = details;
                var attention = Attention();
                if (attention.Count > 0) summary["attention"] = attention;
                if (deleted.Count > 0) summary["deletedIds"] = new JsonArray(deleted.Take(200).Select(i => (JsonNode)i).ToArray());
                if (types.Count > 0) summary["deletedTypeIds"] = new JsonArray(types.Take(200).Select(i => (JsonNode)i).ToArray());
                if (_modified.Count > 0) summary["modifiedIds"] = new JsonArray(_modified.Take(500).Select(i => (JsonNode)i).ToArray());
                if (_added.Count > 0 && _added.Count <= 200) summary["addedIds"] = new JsonArray(_added.Select(i => (JsonNode)i).ToArray());
                summary["note"] = "Counts include elements Revit updates automatically (e.g. views, tags, joined walls).";
                return summary;
            }
        }
    }
}
