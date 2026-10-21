using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using S = AceRevitMcp.Commands.CaptureLogic.ElemState;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The owner's work record (ribbon "Record my work"): capture_method started and stopped by the person, on whatever
    /// model is active and on every document they switch to. Read-only: it only listens (DocumentChanged for touched,
    /// added and deleted ids and the transaction names, ViewActivated for the views used, DocumentClosing to keep a closing
    /// document's end state) and snapshots elements when Revit is idle; it never opens a transaction. The running record
    /// is capture_method's (CaptureMethod.Record, Source "ribbon"), kept on disk like a capture so a restart keeps it.
    /// </summary>
    internal static class WorkRecorder
    {
        private static UIControlledApplication _ui;
        private static bool _idlingAttached, _dirty;
        private static DateTime _lastSave = DateTime.MinValue;
        /// <summary>Snapshots to take when Revit is idle: document key and the view whose elements come first.</summary>
        private static readonly List<(string Key, long ViewId)> Due = new List<(string, long)>();

        /// <summary>The ribbon button (to show Recording / Stopped).</summary>
        internal static RibbonItem Button;

        internal static CaptureMethod.Record Active => CaptureMethod.Current() is CaptureMethod.Record r && r.IsRibbon ? r : null;

        private static string Now => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        public static void Attach(UIControlledApplication ui)
        {
            _ui = ui;
            ui.ControlledApplication.DocumentChanged += OnChanged;
            ui.ControlledApplication.DocumentClosing += OnClosing;
            ui.ViewActivated += OnViewActivated;
            if (Active != null) EnsureIdling();
            Publish();
        }

        public static void Detach()
        {
            if (_ui == null) return;
            try { if (_dirty && Active != null) CaptureMethod.Save(Active); } catch (Exception ex) { Log.Warn($"Work record: {ex.Message}"); }
            _ui.ControlledApplication.DocumentChanged -= OnChanged;
            _ui.ControlledApplication.DocumentClosing -= OnClosing;
            _ui.ViewActivated -= OnViewActivated;
            if (_idlingAttached) { _ui.Idling -= OnIdling; _idlingAttached = false; }
            Button = null;
            _ui = null;
        }

        private static void EnsureIdling()
        {
            if (_idlingAttached || _ui == null) return;
            try { _ui.Idling += OnIdling; _idlingAttached = true; }
            catch (Exception ex) { Log.Warn($"Work record: {ex.Message}"); }
        }

        /// <summary>The ribbon button text and the Companion line follow the running capture (ribbon or Claude's).</summary>
        internal static void Publish()
        {
            var cur = CaptureMethod.Current();
            var rec = cur?.IsRibbon == true ? cur : null;
            try
            {
                if (Button != null)
                {
                    Button.ItemText = rec != null ? "Recording\n(click to stop)" : "Record\nmy work";
                    if (Button is RibbonButton b)
                    {
                        b.LargeImage = Icons.Line(32, rec != null ? Ribbon.Glyphs.StopRecord : Ribbon.Glyphs.Record, planned: false);
                        b.Image = Icons.Line(16, rec != null ? Ribbon.Glyphs.StopRecord : Ribbon.Glyphs.Record, planned: false);
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"Work record button: {ex.Message}"); }
            Companion.ActivityHub.SetRecording(rec != null ? $"Recording \"{rec.Label}\" since {Time(rec.StartedAt)} (ACE tab > Recording to stop)"
                : cur != null ? $"Claude is capturing \"{cur.Label}\" since {Time(cur.StartedAt)}" : null);
        }

        private static string Time(string at) => DateTime.TryParse(at, out var t) ? t.ToString("HH:mm") : at;

        private static WorkRecordLogic.DocLog LogFor(CaptureMethod.Record rec, Document doc) =>
            rec.Documents.FirstOrDefault(d => d.Key == Companion.ActivityHub.DocKey(doc));

        // ---- start / stop -----------------------------------------------------------------------------------------

        public static void Start(UIApplication app, string label, string note)
        {
            if (CaptureMethod.Current() != null) throw new InvalidOperationException("A capture is already running.");
            var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a model first.");
            string journal = null; long offset = 0;
            try { journal = app.Application.RecordingJournalFilename; offset = CaptureLogic.JournalEnd(journal); } catch { }
            var rec = new CaptureMethod.Record
            {
                Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), Label = WorkRecordLogic.Clip(label, 80), Note = WorkRecordLogic.Clip(note, WorkRecordLogic.MaxNote),
                Model = doc.Title, ModelPath = doc.PathName, StartedAt = Now, Journal = journal, JournalOffset = offset,
                Source = WorkRecordLogic.Source, Categories = CaptureMethod.DefaultCategories.ToList(),
            };
            var log = NewLog(doc);
            rec.Documents.Add(log);
            TakeSnapshot(doc, log, app.ActiveUIDocument.ActiveView);   // now: the person starts working right after
            var view = app.ActiveUIDocument.ActiveView;
            if (view != null) WorkRecordLogic.AddView(rec.Views, doc.Title, view.Name, view.ViewType.ToString(), Now);
            CaptureMethod.Save(rec);
            _lastSave = DateTime.Now; _dirty = false;
            EnsureIdling();
            Publish();
        }

        private static WorkRecordLogic.DocLog NewLog(Document doc) =>
            new WorkRecordLogic.DocLog { Key = Companion.ActivityHub.DocKey(doc), Title = doc.Title, Path = doc.PathName, FirstSeenAt = Now };

        /// <summary>
        /// The case of the running recording (nothing written yet): each document compared now (or as it was when it
        /// closed), the journal since the start, the notes. Also returns the document to find the vault from.
        /// </summary>
        public static JsonObject Finish(UIApplication app, string closingNote, out Document vaultDoc)
        {
            var rec = Active ?? throw new InvalidOperationException("No recording is running.");
            vaultDoc = null;
            var open = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            var input = new WorkRecordLogic.CaseInput
            {
                Label = rec.Label, StartNote = rec.Note, ClosingNote = WorkRecordLogic.Clip(closingNote, WorkRecordLogic.MaxNote), StartedAt = rec.StartedAt, StoppedAt = Now,
                AceVersion = Rules.ParameterFill.AceVersion(), JournalFile = rec.Journal, Categories = rec.Categories, Steps = rec.Steps, Views = rec.Views,
            };
            var budget = WorkRecordLogic.MaxDiffsPerCase;
            foreach (var log in rec.Documents)
            {
                var r = new WorkRecordLogic.DocResult { Log = log };
                List<S> after = null;
                if (log.Closed) { after = log.After; r.AnnotationAfter = log.AnnotationAfter; r.SketchOwners = log.SketchOwners; r.Note = $"closed during the recording ({log.ClosedAt}): compared as it was then"; }
                else
                {
                    var doc = open.FirstOrDefault(d => Companion.ActivityHub.DocKey(d) == log.Key);
                    if (doc != null)
                    {
                        if (vaultDoc == null) vaultDoc = doc;
                        after = AfterState(doc, log, out var ann, out var sketches);
                        r.AnnotationAfter = ann; r.SketchOwners = sketches;
                    }
                    else { r.Compared = false; r.Note = "not open at stop and no closing state kept: not compared"; }
                }
                if (after != null)
                {
                    r.Diffs = WorkRecordLogic.DocumentDiffs(log, after, r.AnnotationAfter, r.SketchOwners, budget, out var cut);
                    r.Truncated = cut;
                    budget -= r.Diffs.Count;
                }
                if (log.SnapshotAfterChange) r.Note = (r.Note == null ? "" : r.Note + "; ") + "its start snapshot was taken after its first change";
                input.Documents.Add(r);
            }
            string journalNow = null;
            try { journalNow = app.Application.RecordingJournalFilename; } catch { }
            var (lines, truncated) = CaptureLogic.JournalLines(CaptureMethod.JournalSince(rec.Journal, rec.JournalOffset, journalNow), CaptureMethod.MaxJournalLines);
            input.JournalLines = lines; input.JournalTruncated = truncated; input.JournalFile = journalNow ?? rec.Journal;
            return WorkRecordLogic.BuildCase(input);
        }

        /// <summary>Writes the case (JSON and markdown) into dir, ends the recording and keeps its raw state; the two paths.</summary>
        public static (string Json, string Md) WriteAndEnd(JsonObject c, string dir)
        {
            var rec = Active;
            Directory.CreateDirectory(dir);
            var stem = WorkRecordLogic.FreeStem(dir, WorkRecordLogic.CaseStem(DateTime.Now, rec?.Label ?? c["label"]?.ToString() ?? "case"));
            if (rec != null) c["archive"] = CaptureMethod.Archive(rec, new { stoppedAt = c["stoppedAt"]?.ToString() });
            var json = Path.Combine(dir, stem + ".json");
            var md = Path.Combine(dir, stem + ".md");
            File.WriteAllText(json, c.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(md, WorkRecordLogic.CaseMarkdown(c));
            CaptureMethod.Save(null);
            Due.Clear(); _dirty = false;
            if (_idlingAttached && _ui != null) { _ui.Idling -= OnIdling; _idlingAttached = false; }
            Publish();
            return (json, md);
        }

        // ---- snapshots -------------------------------------------------------------------------------------------

        private static bool IsModelElement(Element e) =>
            e != null && !(e is ElementType) && e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is FamilyInstance fi && fi.SuperComponent != null);

        /// <summary>
        /// Adds to the document's start snapshot: the model elements of this view first, then (first time) the structural
        /// categories, up to the cap; elements already touched are left out (their state before is gone).
        /// </summary>
        private static void TakeSnapshot(Document doc, WorkRecordLogic.DocLog log, View view)
        {
            var have = new HashSet<long>(log.Before.Select(s => s.Id));
            var room = WorkRecordLogic.MaxElementsPerDocument - have.Count;
            if (room <= 0) { log.SnapshotCapped = true; return; }
            var pick = new List<Element>();
            void Take(IEnumerable<Element> xs)
            {
                foreach (var e in xs)
                {
                    if (pick.Count >= room) { log.SnapshotCapped = true; return; }
                    if (!IsModelElement(e) || have.Contains(e.Id.Value) || log.Touched.Contains(e.Id.Value)) continue;
                    have.Add(e.Id.Value); pick.Add(e);
                }
            }
            try { if (view != null && view.Document.Equals(doc) && !view.IsTemplate) Take(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()); }
            catch (Exception ex) { Log.Warn($"Work record view snapshot: {ex.Message}"); }   // schedules, sheets and such: no own elements
            var first = log.SnapshotAt == null;
            if (first)
            {
                var cats = CaptureMethod.DefaultCategories.Select(n => Args.ResolveCategory(doc, n)).Where(c => c != null).Select(c => c.Id).ToList();
                if (cats.Count > 0) Take(new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType());
            }
            if (pick.Count == 0 && !first) return;
            var states = CaptureMethod.Snapshot(doc, pick, null, allParameters: true, out var annotation);
            log.Before.AddRange(states);
            if (first) { log.Annotation = annotation; log.SnapshotAt = Now; }
            _dirty = true;
        }

        /// <summary>The document's state now: its snapshot elements plus the model elements touched since; sketch owners.</summary>
        private static List<S> AfterState(Document doc, WorkRecordLogic.DocLog log, out CaptureLogic.Annotation annotation, out List<long> sketchOwners)
        {
            sketchOwners = new List<long>();
            var taken = new HashSet<long>();
            var elements = new List<Element>();
            foreach (var id in log.Before.Select(s => s.Id).Concat(log.Touched))
            {
                Element e;
                try { e = doc.GetElement(new ElementId(id)); } catch { continue; }
                if (e is Sketch sk)
                {
                    try { var owner = sk.OwnerId; if (owner != null && owner != ElementId.InvalidElementId) sketchOwners.Add(owner.Value); } catch { }
                    continue;
                }
                if (!IsModelElement(e) || !taken.Add(id)) continue;
                elements.Add(e);
            }
            foreach (var o in sketchOwners)
                if (taken.Add(o))
                    try { var e = doc.GetElement(new ElementId(o)); if (IsModelElement(e)) elements.Add(e); } catch { }
            return CaptureMethod.Snapshot(doc, elements, null, allParameters: true, out annotation);
        }

        // ---- events ----------------------------------------------------------------------------------------------

        private static void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            var rec = Active;
            if (rec == null) return;
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;
                var log = LogFor(rec, doc);
                if (log == null)
                {
                    // Changed before any view of it was active (e.g. by an add-in): snapshot when idle, flagged as late.
                    log = NewLog(doc); log.SnapshotAfterChange = true;
                    rec.Documents.Add(log);
                    Due.Add((log.Key, -1));
                    EnsureIdling();
                }
                if (log.Closed) return;
                var ok = WorkRecordLogic.AddIds(log.Added, e.GetAddedElementIds().Select(i => i.Value), WorkRecordLogic.MaxTouchedPerDocument);
                ok &= WorkRecordLogic.AddIds(log.Touched, e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Select(i => i.Value), WorkRecordLogic.MaxTouchedPerDocument);
                ok &= WorkRecordLogic.AddIds(log.Deleted, e.GetDeletedElementIds().Select(i => i.Value), WorkRecordLogic.MaxTouchedPerDocument);
                if (!ok) log.TouchedCapped = true;
                foreach (var name in e.GetTransactionNames()) WorkRecordLogic.AddStep(rec.Steps, doc.Title, name, e.Operation.ToString(), Now);
                _dirty = true;
            }
            catch (Exception ex) { Log.Warn($"Work record (change): {ex.Message}"); }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            var rec = Active;
            if (rec == null) return;
            try
            {
                var doc = e.Document; var view = e.CurrentActiveView;
                if (doc == null || view == null || doc.IsLinked) return;
                WorkRecordLogic.AddView(rec.Views, doc.Title, view.Name, view.ViewType.ToString(), Now);
                var log = LogFor(rec, doc);
                if (log == null) { log = NewLog(doc); rec.Documents.Add(log); }
                if (!log.Closed && !log.SnapshotCapped) { Due.Add((log.Key, view.Id.Value)); EnsureIdling(); }
                _dirty = true;
            }
            catch (Exception ex) { Log.Warn($"Work record (view): {ex.Message}"); }
        }

        private static void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            var rec = Active;
            if (rec == null) return;
            try
            {
                var doc = e.Document;
                var log = doc == null ? null : LogFor(rec, doc);
                if (log == null || log.Closed) return;
                log.After = AfterState(doc, log, out var ann, out var sketches);
                log.AnnotationAfter = ann; log.SketchOwners = sketches; log.Closed = true; log.ClosedAt = Now;
                CaptureMethod.Save(rec);
                _lastSave = DateTime.Now; _dirty = false;
            }
            catch (Exception ex) { Log.Warn($"Work record (closing): {ex.Message}"); }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            var rec = Active;
            if (rec == null) { if (_ui != null) _ui.Idling -= OnIdling; _idlingAttached = false; return; }
            try
            {
                if (Due.Count > 0 && sender is UIApplication app)
                {
                    var due = Due.ToList(); Due.Clear();
                    foreach (var (key, viewId) in due.Distinct())
                    {
                        var log = rec.Documents.FirstOrDefault(d => d.Key == key);
                        var doc = app.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && Companion.ActivityHub.DocKey(d) == key);
                        if (log == null || doc == null || log.Closed) continue;
                        View view = null;
                        try { if (viewId > 0) view = doc.GetElement(new ElementId(viewId)) as View; } catch { }
                        TakeSnapshot(doc, log, view);
                    }
                }
                if (_dirty && (DateTime.Now - _lastSave).TotalSeconds > 20)
                {
                    CaptureMethod.Save(rec);
                    _lastSave = DateTime.Now; _dirty = false;
                }
            }
            catch (Exception ex) { Log.Warn($"Work record (idle): {ex.Message}"); }
        }
    }
}
