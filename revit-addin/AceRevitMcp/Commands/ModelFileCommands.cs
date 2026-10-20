using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Opening and closing models, so Revit stays open between models. Not a model change (no NativeChange, nothing saved).
    /// Runs in the bridge's ExternalEvent handler, where no transaction or transaction group is open (the dispatcher calls
    /// the command directly; the code runner's TransactionGroup is why OpenAndActivateDocument fails from a script).
    /// </summary>
    internal static class ModelFileCommands
    {
        /// <summary>A model open_model opened in this session: its handle, the file it came from, how it opened, and whether the user touched it.</summary>
        private sealed class Opened
        {
            public string Handle, Source;
            public Document Doc;
            public bool Detached;
            public bool Untouched = true;   // cleared by any change made outside an ACE request (ModelFileCommands.UserTouched)
        }
        private static readonly System.Collections.Generic.List<Opened> OpenedByAce = new System.Collections.Generic.List<Opened>();

        private static Opened Entry(Document d)
        {
            if (d != null) Bind(d.Application);
            OpenedByAce.RemoveAll(x => { try { return !x.Doc.IsValidObject; } catch { return true; } });
            return d == null ? null : OpenedByAce.FirstOrDefault(x => x.Doc.Equals(d));
        }

        // ---- across a reload (reload_addin): the list is kept in the loader's store as plain data ----------------------

        private const string StoreKey = "modelFile.openedByAce";
        /// <summary>Records left by the build before this one, not yet matched to an open document.</summary>
        private static List<Dictionary<string, object>> _pending = new List<Dictionary<string, object>>();
        private static bool _live;

        /// <summary>
        /// Payload Stop: writes the models this build opened to the loader's store as plain data (handle, source, the
        /// model's own path and title, detached, untouched; no Document), then forgets them so the old build holds no
        /// Document. The next build matches them to the open documents again (Restore, Bind).
        /// </summary>
        internal static void SaveAndForget(IDictionary<string, object> store)
        {
            if (!_live) return;   // nothing was restored or opened by this build since the last save (a second Stop)
            _live = false;
            var list = new List<object>();
            foreach (var x in OpenedByAce)
            {
                try
                {
                    if (x.Doc == null || !x.Doc.IsValidObject) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        ["handle"] = x.Handle, ["source"] = x.Source, ["path"] = x.Doc.PathName ?? "", ["title"] = x.Doc.Title ?? "",
                        ["detached"] = x.Detached, ["untouched"] = x.Untouched,
                    });
                }
                catch { /* a closed model: dropped */ }
            }
            list.AddRange(_pending);   // not matched yet (no command since the reload): passed on unchanged
            if (store != null) store[StoreKey] = list;
            OpenedByAce.Clear();
            _pending.Clear();
        }

        /// <summary>Payload Start: takes the records the build before this one left (matched to documents on first use).</summary>
        internal static void Restore(IDictionary<string, object> store)
        {
            _live = true;
            _pending = new List<Dictionary<string, object>>();
            if (store == null || !store.TryGetValue(StoreKey, out var v) || !(v is System.Collections.IEnumerable items)) return;
            foreach (var item in items) if (item is Dictionary<string, object> rec) _pending.Add(rec);
            store.Remove(StoreKey);
        }

        /// <summary>
        /// Matches the records from before the reload to open documents: by the model's own path, or (no path: a detached
        /// copy never saved) by a title only one open model has. A record that matches nothing is dropped (the model was
        /// closed), as is one that matches ambiguously: discard is then refused for it, the safe side.
        /// </summary>
        private static void Bind(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (_pending.Count == 0 || app == null) return;
            var docs = app.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            foreach (var rec in _pending)
            {
                string S(string k) => rec.TryGetValue(k, out var o) ? o as string : null;
                bool B(string k) => rec.TryGetValue(k, out var o) && o is bool b && b;
                var path = S("path");
                var title = S("title");
                var matches = !string.IsNullOrEmpty(path)
                    ? docs.Where(d => !string.IsNullOrEmpty(d.PathName) && string.Equals(Normalize(d.PathName), Normalize(path), StringComparison.OrdinalIgnoreCase)).ToList()
                    : docs.Where(d => string.IsNullOrEmpty(d.PathName) && string.Equals(d.Title, title, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1 || OpenedByAce.Any(x => x.Doc.Equals(matches[0]))) continue;
                OpenedByAce.Add(new Opened { Handle = S("handle"), Source = S("source"), Doc = matches[0], Detached = B("detached"), Untouched = B("untouched") });
            }
            _pending.Clear();
        }

        /// <summary>A change made outside an ACE request (DocumentChanged while the bridge is idle): the user worked in that model.</summary>
        internal static void UserTouched(Document d)
        {
            try { var e = Entry(d); if (e != null) e.Untouched = false; } catch { }
        }

        /// <summary>
        /// The safety boundary of discard: ACE opened the model in this session and the user has not changed it since; a
        /// workshared model only when ACE opened it detached (a workshared local holds the user's own work).
        /// </summary>
        private static bool CanDiscard(Document d)
        {
            var e = Entry(d);
            if (e == null || !e.Untouched) return false;
            bool workshared;
            try { workshared = d.IsWorkshared; } catch { workshared = true; }
            return !workshared || e.Detached;
        }

        /// <summary>An open model by handle (from open_model), by path (the file it came from, or its own), or by a title only one model has.</summary>
        private static Document FindDoc(UIApplication app, JsonObject args, out string error)
        {
            error = null;
            var docs = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            var handle = Args.Str(args, "handle");
            Bind(app.Application);
            if (!string.IsNullOrWhiteSpace(handle))
            {
                Entry(null);
                var e = OpenedByAce.FirstOrDefault(x => x.Handle == handle.Trim());
                if (e == null) error = $"No model opened by ACE has the handle {handle} (closed already?).";
                return e?.Doc;
            }
            var path = Args.Str(args, "path");
            var name = Args.Str(args, "model");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var full = Normalize(path.Trim().Trim('"'));
                Entry(null);
                var byPath = OpenedByAce.FirstOrDefault(x => string.Equals(Normalize(x.Source), full, StringComparison.OrdinalIgnoreCase))?.Doc
                             ?? docs.FirstOrDefault(d => SamePath(d, full));
                if (byPath == null) error = $"No open model comes from {path}.";
                return byPath;
            }
            if (string.IsNullOrWhiteSpace(name)) { error = "Give handle (from open_model), path or model (the title)."; return null; }
            var byTitle = docs.Where(d => string.Equals(d.Title, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byTitle.Count > 1) { error = $"{byTitle.Count} open models are called '{name}': give path or handle."; return null; }
            var doc = byTitle.FirstOrDefault() ?? docs.FirstOrDefault(d => !string.IsNullOrEmpty(d.PathName) && string.Equals(d.PathName, name, StringComparison.OrdinalIgnoreCase));
            if (doc == null) error = $"No open model is called '{name}'. Open: {string.Join(", ", docs.Select(d => d.Title))}.";
            return doc;
        }

        /// <summary>
        /// open_model { path, detach: none | preserve_worksets | discard_worksets, audit, worksets: all | last_viewed | none,
        /// close_previous, clear_transmitted, save_as_central_ok }: opens and activates the file. Refuses a missing file, a file
        /// already open (it is activated instead) and a file saved in a newer Revit. close_previous closes the previously active
        /// model only if it has no unsaved changes, never saving. Before the open it reads the transmitted flag (transmitted) and
        /// the external references (unresolvedLinks: files that do not exist); clear_transmitted: true writes the flag off in the
        /// file (transmittedCleared). A transmitted workshared model is opened detached (worksets kept) unless clear_transmitted or
        /// save_as_central_ok. Dialogs shown during the open are recorded in dialogs; only known ones are answered (answer null =
        /// a person was needed).
        /// </summary>
        public static JsonNode Open(UIApplication app, JsonObject args)
        {
            var path = Args.Str(args, "path");
            if (string.IsNullOrWhiteSpace(path)) throw new CommandException("Give path: the .rvt file to open.");
            path = path.Trim().Trim('"');
            var detach = (Args.Str(args, "detach") ?? "none").Trim().ToLowerInvariant();
            var worksets = (Args.Str(args, "worksets") ?? "all").Trim().ToLowerInvariant();
            if (!new[] { "none", "preserve_worksets", "discard_worksets" }.Contains(detach)) throw new CommandException("detach must be none, preserve_worksets or discard_worksets.");
            if (!new[] { "all", "last_viewed", "none" }.Contains(worksets)) throw new CommandException("worksets must be all, last_viewed or none.");
            var audit = Args.Bool(args, "audit");
            var notes = new JsonArray();

            if (!File.Exists(path)) throw new CommandException($"The file does not exist (or cannot be reached): {path}");
            var full = Path.GetFullPath(path);
            var previous = app.ActiveUIDocument?.Document;

            // Already open: activate: true makes it the active model; otherwise it is refused (never opened twice).
            var open = app.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && SamePath(d, full));
            if (open != null)
            {
                if (!Args.Bool(args, "activate"))
                    throw new CommandException($"{open.Title} is already open in this Revit session. Call open_model again with activate: true to make it the active model (or close it first): ACE does not open a model twice.");
                var wasActive = previous != null && previous.Equals(open);
                if (!wasActive)
                {
                    if (string.IsNullOrEmpty(open.PathName)) throw new CommandException($"{open.Title} has no file path (a detached copy never saved): switch to it in Revit.");
                    try { app.OpenAndActivateDocument(open.PathName); }
                    catch (Exception ex) { throw new CommandException($"Revit could not activate {open.Title}: {ex.Message}"); }
                }
                InstanceRegistry.Update(app);
                return new JsonObject
                {
                    ["model"] = open.Title, ["path"] = open.PathName, ["activated"] = !wasActive, ["alreadyOpen"] = true,
                    ["active"] = app.ActiveUIDocument?.Document?.Title, ["previous"] = PreviousJson(previous), ["openDocuments"] = OpenDocuments(app),
                    ["notes"] = new JsonArray((JsonNode)(wasActive ? "It was already the active model." : "It was already open: made it the active model (nothing reopened).")),
                };
            }

            BasicFileInfo info;
            try { info = BasicFileInfo.Extract(full); }
            catch (Exception ex) { throw new CommandException($"Revit cannot read the file's header ({ex.Message}): is it a Revit project file?"); }
            if (info.IsSavedInLaterVersion)
                throw new CommandException($"The file was saved in Revit {info.Format}, newer than this Revit {app.Application.VersionNumber}: it cannot be opened here.");
            if (!info.IsSavedInCurrentVersion) notes.Add($"The file is from Revit {info.Format}: Revit upgrades it in memory (nothing is saved).");

            var workshared = info.IsWorkshared;
            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(full);
            var clearTransmitted = Args.Bool(args, "clear_transmitted");
            var saveAsCentralOk = Args.Bool(args, "save_as_central_ok");

            // Transmitted flag and external references, read from the file before it opens.
            TransmissionData td = null;
            try { td = TransmissionData.ReadTransmissionData(modelPath); }
            catch (Exception ex) { notes.Add("The transmission data could not be read (" + ex.Message + "): transmitted and unresolvedLinks are unknown."); }
            var transmitted = td?.IsTransmitted == true;
            var transmittedCleared = false;
            if (clearTransmitted && transmitted)
            {
                try
                {
                    td.IsTransmitted = false;
                    TransmissionData.WriteTransmissionData(modelPath, td);
                    transmittedCleared = true;
                    notes.Add("clear_transmitted: the transmitted flag was cleared in the file itself (the file was written before the open).");
                }
                catch (Exception ex) { notes.Add("clear_transmitted: the flag could not be cleared (" + ex.Message + "); the file is opened as it is."); }
            }
            else if (clearTransmitted) notes.Add("clear_transmitted was ignored: the file is not marked as transmitted (nothing was written).");
            var plan = OpenDialogLogic.TransmittedDetach(transmitted && !transmittedCleared, workshared, detach, saveAsCentralOk);
            if (plan.Note != null) notes.Add(plan.Note);
            detach = plan.Detach;
            var unresolvedLinks = new JsonArray();
            var externalReferences = 0;
            if (td != null)
                foreach (var refId in td.GetAllExternalFileReferenceIds())
                {
                    ExternalFileReference r = null;
                    try { r = td.GetDesiredReferenceData(refId) ?? td.GetLastSavedReferenceData(refId); } catch { /* unreadable reference */ }
                    if (r == null) continue;
                    externalReferences++;
                    string refPath = null; bool local = true;
                    try
                    {
                        var abs = r.GetAbsolutePath() ?? r.GetPath();
                        if (abs != null) { local = !abs.ServerPath && !abs.CloudPath; refPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(abs); }
                    }
                    catch { /* no usable path */ }
                    if (string.IsNullOrWhiteSpace(refPath) || !local) continue; // a server or cloud path cannot be checked from here
                    bool exists;
                    try { exists = File.Exists(refPath); } catch { exists = false; }
                    if (!exists)
                        unresolvedLinks.Add(new JsonObject { ["type"] = OpenDialogLogic.RefType(r.ExternalFileReferenceType.ToString()), ["path"] = refPath, ["exists"] = false });
                }

            var options = new OpenOptions { Audit = audit };
            var detached = false;
            if (detach != "none")
            {
                if (!workshared) notes.Add("detach was ignored: the file is not workshared.");
                else
                {
                    options.DetachFromCentralOption = detach == "discard_worksets" ? DetachFromCentralOption.DetachAndDiscardWorksets : DetachFromCentralOption.DetachAndPreserveWorksets;
                    detached = true;
                }
            }
            if (workshared && !(detached && detach == "discard_worksets"))
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(
                    worksets == "none" ? WorksetConfigurationOption.CloseAllWorksets : worksets == "last_viewed" ? WorksetConfigurationOption.OpenLastViewed : WorksetConfigurationOption.OpenAllWorksets));
            if (workshared && !detached && info.IsCentral) notes.Add("This is a central model opened directly (not a local copy): do not save it from here.");

            // Dialogs during the open: a handler only for this call. Known ones are answered, every one is recorded.
            var dialogs = new JsonArray();
            void OnDialog(object sender, DialogBoxShowingEventArgs e)
            {
                var isTask = e is TaskDialogShowingEventArgs;
                var message = (e as TaskDialogShowingEventArgs)?.Message ?? (e as MessageBoxShowingEventArgs)?.Message;
                var answer = OpenDialogLogic.Answer(e.DialogId, message, isTask);
                var answered = false;
                if (answer.Result is int result)
                    try { answered = e.OverrideResult(result); } catch { answered = false; }
                dialogs.Add(new JsonObject
                {
                    ["id"] = e.DialogId, ["message"] = OpenDialogLogic.Clip(message, 200),
                    ["answer"] = answered ? answer.Answer : null,
                    ["why"] = answered ? answer.Why : (answer.Result != null ? "Revit did not accept the automatic answer: a person answered it in Revit" : answer.Why),
                });
            }

            UIDocument opened;
            app.DialogBoxShowing += OnDialog;
            try { opened = app.OpenAndActivateDocument(modelPath, options, false); }
            catch (Exception ex) { throw new CommandException($"Revit could not open {Path.GetFileName(full)}: {ex.Message}" + DialogText(dialogs)); }
            finally { app.DialogBoxShowing -= OnDialog; }
            var doc = opened?.Document ?? throw new CommandException($"Revit did not open {Path.GetFileName(full)}." + DialogText(dialogs));
            if (dialogs.Any(d => d?["answer"] == null)) notes.Add("A dialog during the open needed a person (see dialogs).");
            Entry(null);
            var handle = Guid.NewGuid().ToString("N").Substring(0, 12);
            Bind(app.Application);
            _live = true;
            OpenedByAce.Add(new Opened { Handle = handle, Source = full, Doc = doc, Detached = detached });

            JsonNode closed = null;
            if (Args.Bool(args, "close_previous") && previous != null && !previous.Equals(doc))
                closed = CloseQuietly(previous, Args.Bool(args, "discard_previous"));

            JsonNode worksetsOpened = null;
            if (doc.IsWorkshared)
                try
                {
                    var user = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
                    worksetsOpened = new JsonObject { ["open"] = user.Count(w => w.IsOpen), ["total"] = user.Count };
                }
                catch { /* not readable */ }

            InstanceRegistry.Update(app);
            return new JsonObject
            {
                ["model"] = doc.Title, ["path"] = doc.PathName, ["workshared"] = doc.IsWorkshared, ["detached"] = detached, ["audited"] = audit,
                ["worksetsOpened"] = worksetsOpened, ["closed"] = closed, ["openDocuments"] = OpenDocuments(app),
                ["active"] = app.ActiveUIDocument?.Document?.Title, ["previous"] = PreviousJson(previous), ["openedByAce"] = true, ["handle"] = handle,
                ["transmitted"] = transmitted, ["transmittedCleared"] = transmittedCleared, ["externalReferences"] = externalReferences,
                ["unresolvedLinks"] = unresolvedLinks, ["dialogs"] = dialogs, ["notes"] = notes,
            };
        }

        /// <summary>
        /// close_model { handle | path | model, discard }: closes an open model that is not the active one, without saving: { model,
        /// closed, why, discarded, openDocuments }. The active or a linked model is kept (closed: false, why); a modified one too,
        /// unless discard: true and CanDiscard; an unknown or ambiguous name is an error.
        /// </summary>
        public static JsonNode Close(UIApplication app, JsonObject args)
        {
            var doc = FindDoc(app, args, out var error) ?? throw new CommandException(error);
            JsonObject Kept(string why) => new JsonObject { ["model"] = doc.Title, ["closed"] = false, ["why"] = why, ["openDocuments"] = OpenDocuments(app) };
            if (doc.IsLinked) return Kept("a linked model: unload the link in the host instead");
            if (app.ActiveUIDocument?.Document?.Equals(doc) == true) return Kept("the active model: ACE never closes the active model; switch to another model first (open_model)");
            var discarded = false;
            if (doc.IsModified)
            {
                if (!Args.Bool(args, "discard")) return Kept("it has unsaved changes: ACE does not close it (nothing is saved); save or discard them in Revit first, or discard: true for a model ACE opened");
                if (!CanDiscard(doc)) return Kept("discard is only for a model ACE opened in this session and nobody changed since (a workshared one only when ACE opened it detached): this one may hold someone's work; save or discard it in Revit");
                discarded = true;
            }
            var title = doc.Title;
            bool done;
            try { done = doc.Close(false); }
            catch (Exception ex) { return Kept("Revit did not close it: " + ex.Message); }
            InstanceRegistry.Update(app);
            return new JsonObject { ["model"] = title, ["closed"] = done, ["why"] = done ? null : "Revit did not close it", ["discarded"] = done && discarded, ["openDocuments"] = OpenDocuments(app) };
        }

        /// <summary>
        /// Closes a model that is not active without saving; a modified one is kept open, unless discard and ACE opened it in
        /// this session or it is a detached copy: { model, closed, why, discarded }.
        /// </summary>
        private static JsonObject CloseQuietly(Document d, bool discard)
        {
            var title = d.Title;
            if (d.IsLinked || d.IsFamilyDocument) return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "not a project model opened on its own" };
            var discarded = d.IsModified && discard && CanDiscard(d);
            if (d.IsModified && !discarded)
                return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = discard ? "it has unsaved changes that ACE may not discard (not opened by ACE, changed by the user, or a workshared local): kept open" : "it has unsaved changes: kept open (ACE never saves or discards them)" };
            try { var ok = d.Close(false); return new JsonObject { ["model"] = title, ["closed"] = ok, ["why"] = null, ["discarded"] = ok && discarded }; }
            catch (Exception ex) { return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "Revit did not close it: " + ex.Message }; }
        }

        private static JsonObject PreviousJson(Document d)
        {
            if (d == null) return null;
            try { return new JsonObject { ["model"] = d.Title, ["path"] = string.IsNullOrEmpty(d.PathName) ? null : d.PathName }; } catch { return null; }
        }

        /// <summary>
        /// save_model { model, allow_risky }: saves an open model to its own file (never Save As, never Synchronize with Central):
        /// a local, detached-and-saved or non-workshared file and a workshared local copy are saved in place; a central opened
        /// directly is saved with SaveOptions. Refuses without allow_risky, in a dry run, and for read-only, cloud, linked or
        /// never-saved documents. { model, path, saved, sizeBefore, sizeAfter, ms, kind }.
        /// </summary>
        public static JsonNode Save(UIApplication app, JsonObject args)
        {
            if (Args.Bool(args, "dry_run") || Args.Bool(args, "check_only") || Args.Bool(args, "readonly"))
                throw new CommandException("save_model cannot be previewed: saving is not undoable. Nothing was saved.");
            if (!Args.Bool(args, "allow_risky"))
                throw new CommandException("save_model writes the model file and cannot be undone: it needs allow_risky: true after the user agreed. Nothing was saved.");
            var name = Args.Str(args, "model");
            var docs = app.Application.Documents.Cast<Document>().ToList();
            var doc = string.IsNullOrWhiteSpace(name) ? app.ActiveUIDocument?.Document
                : docs.FirstOrDefault(d => string.Equals(d.Title, name, StringComparison.OrdinalIgnoreCase))
                  ?? docs.FirstOrDefault(d => !string.IsNullOrEmpty(d.PathName) && string.Equals(d.PathName, name, StringComparison.OrdinalIgnoreCase));
            if (doc == null) throw new CommandException(string.IsNullOrWhiteSpace(name) ? "No model is open." : $"No open model is called '{name}'.");
            if (doc.IsLinked) throw new CommandException($"{doc.Title} is a linked model: save it from its own file. Nothing was saved.");
            if (doc.IsModelInCloud) throw new CommandException($"{doc.Title} is a cloud model: ACE does not save cloud models (use Synchronize in Revit). Nothing was saved.");
            if (doc.IsReadOnly || doc.IsReadOnlyFile) throw new CommandException($"{doc.Title} is read-only. Nothing was saved.");
            if (string.IsNullOrEmpty(doc.PathName)) throw new CommandException($"{doc.Title} has never been saved (no file path; e.g. a detached copy): it needs Save As, which ACE does not do. Nothing was saved.");
            long? Size() { try { return new FileInfo(doc.PathName).Length; } catch { return null; } }
            string kind;
            var central = false;
            try { central = doc.IsWorkshared && !doc.IsDetached && string.Equals(Normalize(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath())), Normalize(doc.PathName), StringComparison.OrdinalIgnoreCase); }
            catch { central = false; }
            kind = !doc.IsWorkshared ? "file" : doc.IsDetached ? "detached copy" : central ? "central (opened directly)" : "workshared local (saved locally; not synchronized with central)";
            var before = Size();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (central) doc.Save(new SaveOptions());
                else doc.Save();
            }
            catch (Exception ex) { throw new CommandException($"Revit did not save {doc.Title}: {ex.Message}"); }
            sw.Stop();
            return new JsonObject
            {
                ["model"] = doc.Title, ["path"] = doc.PathName, ["saved"] = true, ["kind"] = kind,
                ["sizeBefore"] = before, ["sizeAfter"] = Size(), ["ms"] = sw.ElapsedMilliseconds,
                ["note"] = doc.IsWorkshared && !central && !doc.IsDetached ? "Saved locally only: Synchronize with Central is not part of save_model." : null,
            };
        }

        private static bool SamePath(Document d, string full)
        {
            bool Eq(string p) => !string.IsNullOrEmpty(p) && string.Equals(Normalize(p), Normalize(full), StringComparison.OrdinalIgnoreCase);
            if (Eq(d.PathName)) return true;
            try
            {
                if (d.IsWorkshared && d.GetWorksharingCentralModelPath() is ModelPath central)
                    return Eq(ModelPathUtils.ConvertModelPathToUserVisiblePath(central));
            }
            catch { /* cloud or unreadable central path */ }
            return false;
        }

        private static string Normalize(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; }
        }

        private static string DialogText(JsonArray dialogs) =>
            dialogs.Count == 0 ? "" : " Dialogs during the open: " + string.Join("; ", dialogs.Select(d => $"{d?["id"]} ({d?["answer"] ?? "not answered"})")) + ".";

        private static JsonArray OpenDocuments(UIApplication app) =>
            new JsonArray(app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).Select(d => (JsonNode)d.Title).ToArray());
    }

    /// <summary>Pure rules for open_model: which dialog gets which answer, and when a transmitted model opens detached.</summary>
    internal static class OpenDialogLogic
    {
        /// <summary>TaskDialogResult.CommandLink2: "Ignore and continue opening the project" in the unresolved-references dialog.</summary>
        internal const int IgnoreAndContinue = 1002;

        /// <summary>
        /// The answer to a dialog shown while a model opens: (Result to pass to OverrideResult or null, Answer, Why). Matched by
        /// dialog id; the message text is a fallback for a task dialog only. Unknown dialogs and the transmitted-model dialog are
        /// never answered (the transmitted dialog's buttons include "save as central", and their order is not known here).
        /// </summary>
        internal static (int? Result, string Answer, string Why) Answer(string dialogId, string message, bool isTaskDialog)
        {
            var id = dialogId ?? "";
            var text = (message ?? "").ToLowerInvariant();
            if (string.Equals(id, "TaskDialog_Unresolved_References", StringComparison.OrdinalIgnoreCase)
                || (isTaskDialog && id.Length == 0 && text.Contains("could not find or read") && text.Contains("reference")))
                return (IgnoreAndContinue, "Ignore and continue opening the project", "unresolved references: the missing files are listed in unresolvedLinks");
            if (id.IndexOf("transmit", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("transmitted"))
                return (null, null, "the transmitted-model dialog: ACE never chooses here (one option saves the file as central); a person answered it in Revit");
            return (null, null, "not a dialog ACE knows: a person answered it in Revit");
        }

        /// <summary>
        /// How a model opens when it may be transmitted: a transmitted workshared model asked to open attached is opened detached
        /// (worksets kept), so Revit does not ask about saving it as central, unless save_as_central_ok leaves that to a person.
        /// </summary>
        internal static (string Detach, string Note) TransmittedDetach(bool transmitted, bool workshared, string detach, bool saveAsCentralOk)
        {
            if (!transmitted || !workshared || detach != "none") return (detach, null);
            if (saveAsCentralOk) return (detach, "The model is transmitted: opened attached (save_as_central_ok), so Revit may ask how to treat it; that dialog is left to a person.");
            return ("preserve_worksets", "The model is transmitted: opened detached with worksets kept, so nothing is saved as central (clear_transmitted or save_as_central_ok change this).");
        }

        /// <summary>The reference type as reported: RevitLink, CAD, or the API's name for the others.</summary>
        internal static string RefType(string apiName) => apiName == "CADLink" ? "CAD" : apiName;

        internal static string Clip(string s, int max) => s == null ? null : s.Length <= max ? s : s.Substring(0, max);
    }
}
