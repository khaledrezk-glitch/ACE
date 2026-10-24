using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
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
            public bool Untouched = true;   // cleared by any change made outside an ACE request (ModelFileCommands.NoteChange)
            /// <summary>Every change since the open came from ACE runs that changed no element (QuietCloseLogic.AfterChange).</summary>
            public bool OnlyEmptyAceRuns = true;
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
                        ["detached"] = x.Detached, ["untouched"] = x.Untouched, ["onlyEmptyAceRuns"] = x.OnlyEmptyAceRuns,
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
                OpenedByAce.Add(new Opened { Handle = S("handle"), Source = S("source"), Doc = matches[0], Detached = B("detached"), Untouched = B("untouched"), OnlyEmptyAceRuns = B("onlyEmptyAceRuns") });
            }
            _pending.Clear();
        }

        /// <summary>
        /// Every DocumentChanged on a model ACE opened: a change outside an ACE request (the bridge is idle) means the user worked
        /// in it (Untouched cleared); QuietCloseLogic.AfterChange keeps OnlyEmptyAceRuns only for ACE commits that changed no
        /// element and for rollbacks. Anything unreadable clears both flags (the safe side).
        /// </summary>
        internal static void NoteChange(Autodesk.Revit.DB.Events.DocumentChangedEventArgs args, bool aceRequest, bool temporary)
        {
            Opened e = null;
            try
            {
                e = Entry(args.GetDocument());
                if (e == null) return;
                if (!aceRequest) e.Untouched = false;
                int added = -1, modified = -1, deleted = -1;
                try { added = args.GetAddedElementIds().Count; modified = args.GetModifiedElementIds().Count; deleted = args.GetDeletedElementIds().Count; }
                catch { /* not readable: counts stay unknown */ }
                e.OnlyEmptyAceRuns = QuietCloseLogic.AfterChange(e.OnlyEmptyAceRuns, aceRequest, temporary, args.Operation.ToString(), added, modified, deleted);
            }
            catch { if (e != null) { e.Untouched = false; e.OnlyEmptyAceRuns = false; } }
        }

        /// <summary>
        /// A modified model that may be closed without saving and without discard: ACE opened it, nobody else changed it, and
        /// its only changes were ACE runs that changed no element (same workshared boundary as CanDiscard).
        /// </summary>
        private static bool CanCloseQuietly(Document d)
        {
            var e = Entry(d);
            if (e == null) return false;
            bool workshared;
            try { workshared = d.IsWorkshared; } catch { workshared = true; }
            return QuietCloseLogic.MayClose(e.Untouched, e.OnlyEmptyAceRuns, workshared, e.Detached);
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
                // The model whose own file this is, or the one ACE opened from it; else a local whose central is this file. Exactly
                // one, or the call is refused: two documents from one file (a central and a copy of it) need the handle.
                var direct = new List<Document>();
                foreach (var d in docs.Where(d => !string.IsNullOrEmpty(d.PathName) && string.Equals(Normalize(d.PathName), full, StringComparison.OrdinalIgnoreCase))
                    .Concat(OpenedByAce.Where(x => string.Equals(Normalize(x.Source), full, StringComparison.OrdinalIgnoreCase)).Select(x => x.Doc)))
                    if (!direct.Any(x => x.Equals(d))) direct.Add(d);
                var matches = direct.Count > 0 ? direct : docs.Where(d => SamePath(d, full)).ToList();
                if (matches.Count == 1) return matches[0];
                error = matches.Count > 1
                    ? $"{matches.Count} open models come from {path} ({string.Join("; ", matches.Select(d => $"{d.Title}: {(string.IsNullOrEmpty(d.PathName) ? "no file path" : d.PathName)}"))}): give handle (from open_model)."
                    : $"No open model comes from {path}.";
                return null;
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
        /// close_previous, clear_transmitted, save_as_central_ok, close_idle_copies }: opens and activates the file. Before the open
        /// it checks Revit's memory and open models (MemoryBeforeOpen: a warning, memory; close_idle_copies closes ACE's idle copies). Refuses a missing file, a file
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
            // A workshared file opened attached gets a <name>_backup folder next to it (Revit's worksharing backups): a warning,
            // stronger when the folder looks like a deliverables folder. Never a block.
            var warnings = new JsonArray();
            var backupWarning = OpenDialogLogic.BackupFolderWarning(workshared, detached, full);
            if (backupWarning != null) warnings.Add(backupWarning);

            // Memory before the open (live: Revit crashed with 9 models open, ~15 GB): above openModelWarnGb (config.json,
            // default 10) or more than 6 open models, a warning that lists ACE's own copies close_model would close without
            // discard; close_idle_copies: true closes those first. Nothing else is ever closed.
            var memory = MemoryBeforeOpen(app, previous, Args.Bool(args, "close_idle_copies"), warnings);

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
            var transmittedClearedOnClose = ClearPendingTransmitted(app);

            JsonNode worksetsOpened = null;
            if (doc.IsWorkshared)
                try
                {
                    var user = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
                    worksetsOpened = new JsonObject { ["open"] = user.Count(w => w.IsOpen), ["total"] = user.Count };
                }
                catch { /* not readable */ }

            // #38: a central that shares its worksharing identity with another central (a plain file copy): a warning with the fix.
            JsonNode centralIdentity = null;
            if (doc.IsWorkshared && !detached)
            {
                foreach (var iw in CentralIdentity.Check(doc)) warnings.Add(iw);
                if (CentralIdentity.Of(doc) is CentralId ci)
                    centralIdentity = new JsonObject { ["central"] = ci.Path, ["centralGuid"] = ci.CentralGuid, ["episodeGuid"] = ci.EpisodeGuid };
            }

            InstanceRegistry.Update(app);
            return new JsonObject
            {
                ["model"] = doc.Title, ["path"] = doc.PathName, ["workshared"] = doc.IsWorkshared, ["detached"] = detached, ["audited"] = audit,
                ["worksetsOpened"] = worksetsOpened, ["closed"] = closed, ["openDocuments"] = OpenDocuments(app),
                ["active"] = app.ActiveUIDocument?.Document?.Title, ["previous"] = PreviousJson(previous), ["openedByAce"] = true, ["handle"] = handle,
                ["transmitted"] = transmitted, ["transmittedCleared"] = transmittedCleared, ["externalReferences"] = externalReferences,
                ["unresolvedLinks"] = unresolvedLinks, ["dialogs"] = dialogs, ["notes"] = notes, ["warnings"] = warnings, ["memory"] = memory,
                ["transmittedClearedOnClose"] = transmittedClearedOnClose, ["centralIdentity"] = centralIdentity,
            };
        }

        /// <summary>
        /// open_model's memory check, before the open: { workingSetGb, openModels, warnAtGb, warnAtModels, idleCopies,
        /// closedIdleCopies }. Idle copies are models ACE opened that close_model would close without discard (not active,
        /// nobody changed them, and unmodified or only empty ACE runs: QuietCloseLogic.IdleCopy); with closeIdle they are
        /// closed first (CloseQuietly without discard). Adds the warning when Revit is above the threshold.
        /// </summary>
        private static JsonObject MemoryBeforeOpen(UIApplication app, Document active, bool closeIdle, JsonArray warnings)
        {
            long bytes = -1;
            try { using (var p = System.Diagnostics.Process.GetCurrentProcess()) bytes = p.WorkingSet64; } catch { /* not readable */ }
            var models = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).ToList();
            var warnGb = QuietCloseLogic.WarnGb(AceRevitMcp.Rules.ProjectRules.Config()?["openModelWarnGb"]?.ToString());
            Entry(null);
            var idle = new List<Document>();
            foreach (var d in models)
            {
                var e = Entry(d);
                if (e == null || (active != null && active.Equals(d))) continue;
                bool workshared, modified;
                try { workshared = d.IsWorkshared; modified = d.IsModified; } catch { continue; }
                if (QuietCloseLogic.IdleCopy(e.Untouched, e.OnlyEmptyAceRuns, workshared, e.Detached, modified)) idle.Add(d);
            }
            var idleTitles = idle.Select(d => d.Title).ToList();
            var closedIdle = new JsonArray();
            if (closeIdle)
                foreach (var d in idle)
                {
                    var r = CloseQuietly(d, false);
                    closedIdle.Add(r);
                }
            if (closeIdle) InstanceRegistry.Update(app);
            var warning = QuietCloseLogic.MemoryWarning(bytes, models.Count, warnGb, idleTitles, closeIdle);
            if (warning != null) warnings.Add(warning);
            return new JsonObject
            {
                ["workingSetGb"] = bytes < 0 ? (JsonNode)null : Math.Round(bytes / QuietCloseLogic.Gb, 1), ["openModels"] = models.Count,
                ["warnAtGb"] = warnGb, ["warnAtModels"] = QuietCloseLogic.WarnModels,
                ["idleCopies"] = new JsonArray(idleTitles.Select(t => (JsonNode)t).ToArray()), ["closedIdleCopies"] = closeIdle ? closedIdle : null,
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
            var quiet = doc.IsModified && CanCloseQuietly(doc);
            if (doc.IsModified && !quiet)
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
            return new JsonObject
            {
                ["model"] = title, ["closed"] = done, ["why"] = done ? null : "Revit did not close it", ["discarded"] = done && discarded,
                ["note"] = done && quiet ? QuietCloseLogic.ClosedNote : null, ["openDocuments"] = OpenDocuments(app),
                ["transmittedClearedOnClose"] = done ? ClearPendingTransmitted(app) : null,
            };
        }

        /// <summary>
        /// Closes a model that is not active without saving; a modified one is kept open, unless discard and ACE opened it in
        /// this session or it is a detached copy: { model, closed, why, discarded }.
        /// </summary>
        private static JsonObject CloseQuietly(Document d, bool discard)
        {
            var title = d.Title;
            if (d.IsLinked || d.IsFamilyDocument) return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "not a project model opened on its own" };
            var quiet = d.IsModified && CanCloseQuietly(d);
            var discarded = d.IsModified && !quiet && discard && CanDiscard(d);
            if (d.IsModified && !quiet && !discarded)
                return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = discard ? "it has unsaved changes that ACE may not discard (not opened by ACE, changed by the user, or a workshared local): kept open" : "it has unsaved changes: kept open (ACE never saves or discards them)" };
            try { var ok = d.Close(false); return new JsonObject { ["model"] = title, ["closed"] = ok, ["why"] = null, ["discarded"] = ok && discarded, ["note"] = ok && quiet ? QuietCloseLogic.ClosedNote : null }; }
            catch (Exception ex) { return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "Revit did not close it: " + ex.Message }; }
        }

        private static JsonObject PreviousJson(Document d)
        {
            if (d == null) return null;
            try { return new JsonObject { ["model"] = d.Title, ["path"] = string.IsNullOrEmpty(d.PathName) ? null : d.PathName }; } catch { return null; }
        }

        /// <summary>
        /// save_model { handle | path | model, allow_risky, save_as? { path, overwrite, as_central, clear_transmitted (default: as_central) } }: saves an open model (active
        /// or not). Without save_as it saves to its own file (never Synchronize with Central): a local, detached-and-saved or
        /// non-workshared file and a workshared local copy are saved in place; a central opened directly is saved with
        /// SaveOptions. With save_as: Document.SaveAs to a new .rvt (SaveAsLogic checks the target before Revit is called); the
        /// model then IS that file, and a handle from open_model follows it. Refuses without allow_risky, in a dry run, and for
        /// read-only, cloud, linked or family documents (a plain save also a never-saved one). Runs in the bridge's handler, no
        /// transaction open (not a NativeChange). { model, path, saved, sizeBefore, sizeAfter, ms, kind }.
        /// </summary>
        public static JsonNode Save(UIApplication app, JsonObject args)
        {
            if (Args.Bool(args, "dry_run") || Args.Bool(args, "check_only") || Args.Bool(args, "readonly"))
                throw new CommandException("save_model cannot be previewed: saving is not undoable. Nothing was saved.");
            if (!Args.Bool(args, "allow_risky"))
                throw new CommandException("save_model writes the model file and cannot be undone: it needs allow_risky: true after the user agreed. Nothing was saved.");
            // Which model: handle (from open_model), path, or a title only one open model has; none: the active model. The model
            // need not be active: Document.Save saves any open project document.
            var named = new[] { "handle", "path", "model" }.Any(k => !string.IsNullOrWhiteSpace(Args.Str(args, k)));
            Document doc;
            if (!named) { doc = app.ActiveUIDocument?.Document; if (doc == null) throw new CommandException("No model is open. Nothing was saved."); }
            else
            {
                doc = FindDoc(app, args, out var which);
                if (doc == null) throw new CommandException(which + " Nothing was saved.");
            }
            return SaveDocument(app, doc, args);
        }

        /// <summary>
        /// save_model for a document already chosen (the bridge command above, or the self-test with its own blank project):
        /// the same refusals, plain save and save_as. allow_risky and dry_run are checked by the caller.
        /// </summary>
        /// <param name="clearPending">False for the self-test: the transmitted flags other files wait for are cleared at the next
        /// close ACE makes, never by the self-test (it writes no file but its own).</param>
        internal static JsonNode SaveDocument(UIApplication app, Document doc, JsonObject args, bool clearPending = true)
        {
            if (doc.IsFamilyDocument) throw new CommandException($"{doc.Title} is a family: save_model saves project models. Nothing was saved.");
            if (doc.IsLinked) throw new CommandException($"{doc.Title} is a linked model: save it from its own file. Nothing was saved.");
            if (doc.IsModelInCloud) throw new CommandException($"{doc.Title} is a cloud model: ACE does not save cloud models (use Synchronize in Revit). Nothing was saved.");
            var saveAs = args["save_as"] as JsonObject;
            var readOnly = SaveAsLogic.ReadOnlyRefusal(doc.Title, doc.IsReadOnly, doc.IsReadOnlyFile, saveAs != null);
            if (readOnly != null) throw new CommandException(readOnly + " Nothing was saved.");
            if (saveAs != null) return SaveAs(app, doc, saveAs, clearPending);
            if (string.IsNullOrEmpty(doc.PathName)) throw new CommandException($"{doc.Title} has never been saved (no file path; e.g. a detached copy): give save_as {{ path }} to save it to a new file. Nothing was saved.");
            long? Size() { try { return new FileInfo(doc.PathName).Length; } catch { return null; } }
            string kind;
            var central = IsCentralItself(doc);
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

        /// <summary>A workshared model opened directly from its central file (not a local copy, not detached).</summary>
        private static bool IsCentralItself(Document doc)
        {
            try { return doc.IsWorkshared && !doc.IsDetached && !string.IsNullOrEmpty(doc.PathName) && string.Equals(Normalize(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath())), Normalize(doc.PathName), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// Files saved by save_as that are still marked transmitted and should be cleared (clear_transmitted): Revit writes
        /// transmission data only into a closed file, so the flag is written off when the model is no longer open, checked after
        /// every close ACE makes (close_model, close_previous, close_idle_copies) and after a save_as. Kept in memory only (a
        /// reload_addin or a model closed in Revit leaves the flag: open_model clear_transmitted clears it before the next open).
        /// </summary>
        private static readonly List<string> PendingTransmittedClear = new List<string>();

        /// <summary>Clears the transmitted flag of every pending file no longer open in this session: [{ path, cleared, why }], null when none.</summary>
        private static JsonArray ClearPendingTransmitted(UIApplication app)
        {
            if (PendingTransmittedClear.Count == 0) return null;
            var open = app.Application.Documents.Cast<Document>().Where(d => !string.IsNullOrEmpty(d.PathName)).Select(d => Normalize(d.PathName)).ToList();
            var done = new JsonArray();
            foreach (var p in PendingTransmittedClear.ToList())
            {
                if (open.Any(o => string.Equals(o, Normalize(p), StringComparison.OrdinalIgnoreCase))) continue;
                PendingTransmittedClear.Remove(p);
                try
                {
                    var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(p);
                    var td = TransmissionData.ReadTransmissionData(mp);
                    if (td == null || !td.IsTransmitted) { done.Add(new JsonObject { ["path"] = p, ["cleared"] = false, ["why"] = "not marked transmitted any more" }); continue; }
                    td.IsTransmitted = false;
                    TransmissionData.WriteTransmissionData(mp, td);
                    done.Add(new JsonObject { ["path"] = p, ["cleared"] = true });
                }
                catch (Exception ex) { done.Add(new JsonObject { ["path"] = p, ["cleared"] = false, ["why"] = ex.Message + " (open_model clear_transmitted: true clears it before the next open)" }); }
            }
            return done.Count == 0 ? null : done;
        }

        /// <summary>
        /// save_model's save_as { path, overwrite, as_central }: Document.SaveAs to a new .rvt. The target is checked before Revit is
        /// called (SaveAsLogic.Refusal: .rvt, folder exists, an existing file only with overwrite, not the model's own file,
        /// as_central only for a workshared model). Afterwards the model is that file: ACE's handle record follows it.
        /// </summary>
        private static JsonNode SaveAs(UIApplication app, Document doc, JsonObject saveAs, bool clearPending = true)
        {
            var target = (Args.Str(saveAs, "path") ?? "").Trim().Trim('"');
            var overwrite = Args.Bool(saveAs, "overwrite");
            var asCentral = Args.Bool(saveAs, "as_central");
            var clearTransmitted = Args.Bool(saveAs, "clear_transmitted", asCentral);
            string full = null, folder = null;
            if (target.Length > 0)
                try { full = Path.GetFullPath(target); folder = Path.GetDirectoryName(full); } catch (Exception ex) { throw new CommandException($"save_as path is not a valid path ({ex.Message}). Nothing was saved."); }
            bool workshared;
            try { workshared = doc.IsWorkshared; } catch { workshared = false; }
            var oldPath = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName;
            var oldTitle = doc.Title;
            var existed = full != null && File.Exists(full);
            var refusal = SaveAsLogic.Refusal(target, existed, !string.IsNullOrEmpty(folder) && Directory.Exists(folder), overwrite, asCentral, workshared,
                oldPath != null && full != null && string.Equals(Normalize(oldPath), Normalize(full), StringComparison.OrdinalIgnoreCase));
            if (refusal != null) throw new CommandException(refusal + " Nothing was saved.");
            // SaveAs throws inside an open transaction or group; this command runs in the bridge's handler where none is open.
            if (doc.IsModifiable) throw new CommandException($"{doc.Title} has a transaction open: Save As needs none (finish or cancel it in Revit). Nothing was saved.");
            string oldCentral = null;
            if (workshared && !doc.IsDetached)
                try { oldCentral = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch { oldCentral = null; }
            var options = new SaveAsOptions { OverwriteExistingFile = overwrite };
            if (asCentral) options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { doc.SaveAs(full, options); }
            catch (Exception ex)
            {
                var hint = workshared && !asCentral ? " A workshared model may need as_central: true (a detached copy becomes a new central)." : "";
                throw new CommandException($"Revit did not save {doc.Title} as {full}: {ex.Message}.{hint}");
            }
            sw.Stop();
            // The model is now the new file: a handle from open_model points at it (by document), and its source follows, so
            // the old path no longer finds it.
            var e = Entry(doc);
            if (e != null) { e.Source = Normalize(doc.PathName ?? full); _live = true; }
            InstanceRegistry.Update(app);
            long? size; try { size = new FileInfo(doc.PathName ?? full).Length; } catch { size = null; }
            var warnings = new JsonArray();
            var notes = new JsonArray();
            var w = SaveAsLogic.CentralWarning(asCentral, oldCentral, doc.PathName ?? full);
            if (w != null) warnings.Add(w);
            // SaveAs keeps the transmitted flag of a transmitted source. It is read from the new file; Revit writes transmission
            // data only into a closed file (TransmissionData.WriteTransmissionData: "cannot be called on an opened document"), so a
            // clear is done when ACE closes this model (PendingTransmittedClear), never by closing it now.
            var newPath = doc.PathName ?? full;
            bool? transmitted = null;
            try { transmitted = TransmissionData.ReadTransmissionData(ModelPathUtils.ConvertUserVisiblePathToModelPath(newPath))?.IsTransmitted == true; }
            catch (Exception ex) { notes.Add("The transmitted flag of the new file could not be read (" + ex.Message + ")."); }
            var plan = SaveAsLogic.TransmittedPlan(transmitted, clearTransmitted, newPath);
            if (plan.Clear) PendingTransmittedClear.Add(newPath);
            if (plan.Warning != null) warnings.Add(plan.Warning);
            if (plan.Note != null) notes.Add(plan.Note);
            var clearedOld = clearPending ? ClearPendingTransmitted(app) : null;   // e.g. the file this model was before (no longer open)
            // #38: a new central has a new identity; record it (replacing a copy's old one) and say if it still clashes.
            if (asCentral) foreach (var iw in CentralIdentity.Check(doc)) warnings.Add(iw);
            return new JsonObject
            {
                ["model"] = doc.Title, ["previousModel"] = oldTitle, ["path"] = newPath, ["previousPath"] = oldPath, ["saved"] = true, ["kind"] = SaveAsLogic.Kind(asCentral),
                ["overwritten"] = existed, ["transmitted"] = transmitted, ["transmittedClear"] = plan.State,
                ["sizeAfter"] = size, ["ms"] = sw.ElapsedMilliseconds, ["handle"] = e?.Handle, ["warnings"] = warnings, ["notes"] = notes,
                ["transmittedClearedOnClose"] = clearedOld,
                ["note"] = "The open model is now this file: further saves go to it" + (oldPath != null ? $" (not {oldPath})." : "."),
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

    /// <summary>
    /// Pure rules for closing a model ACE opened whose only changes were ACE runs that changed no element (e.g. a check run in
    /// mode auto that committed an empty transaction): it is closed without saving and without discard/confirm.
    /// </summary>
    internal static class QuietCloseLogic
    {
        internal const string ClosedNote = "closed: its only changes were ACE runs that changed no element";

        /// <summary>
        /// The OnlyEmptyAceRuns flag after one DocumentChanged event (operation: the UndoOperation name; counts -1 when not
        /// readable). Once cleared it stays cleared. A change outside an ACE request clears it; inside one, a committed
        /// transaction keeps it only with 0 added, 0 modified and 0 deleted elements; a rolled-back transaction or group keeps
        /// it (nothing stays); ACE's temporary work (rolled back afterwards, ChangeTracker.Temporary) is not counted; undo,
        /// redo, unknown operations and unknown counts clear it.
        /// </summary>
        internal static bool AfterChange(bool onlyEmptyAceRuns, bool aceRequest, bool temporary, string operation, int added, int modified, int deleted)
        {
            if (!onlyEmptyAceRuns || !aceRequest) return false;
            switch (operation)
            {
                case "TransactionCommitted": return temporary || (added == 0 && modified == 0 && deleted == 0);
                case "TransactionRolledBack":
                case "TransactionGroupRolledBack": return true;
                default: return false;
            }
        }

        /// <summary>Whether a modified model may close quietly: nobody else touched it, only empty ACE runs, and (workshared) a detached copy.</summary>
        internal static bool MayClose(bool untouched, bool onlyEmptyAceRuns, bool workshared, bool detached) =>
            untouched && onlyEmptyAceRuns && (!workshared || detached);

        /// <summary>
        /// A copy ACE opened that close_model would close without discard and that nobody worked in: untouched, and either
        /// unmodified or modified only by empty ACE runs (MayClose). The caller leaves out the active model.
        /// </summary>
        internal static bool IdleCopy(bool untouched, bool onlyEmptyAceRuns, bool workshared, bool detached, bool modified) =>
            untouched && (!modified || MayClose(untouched, onlyEmptyAceRuns, workshared, detached));

        internal const double Gb = 1024d * 1024 * 1024;
        internal const double DefaultWarnGb = 10;
        /// <summary>More open models than this before an open: the warning (live crash: 9 models).</summary>
        internal const int WarnModels = 6;

        /// <summary>config.json openModelWarnGb: a positive number, else the default (10 GB).</summary>
        internal static double WarnGb(string configured) =>
            double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 && AceRevitMcp.Util.Compat.IsFinite(v) ? v : DefaultWarnGb;

        /// <summary>
        /// The warning before open_model opens another model: Revit's working set above warnGb or more than WarnModels project
        /// models open; null below both (or memory unknown and few models). Lists the idle ACE copies (closed already with
        /// closedIdle) and how to close them.
        /// </summary>
        internal static string MemoryWarning(long workingSetBytes, int openModels, double warnGb, IList<string> idleCopies, bool closedIdle)
        {
            var gb = workingSetBytes < 0 ? -1 : workingSetBytes / Gb;
            var overMemory = gb >= 0 && gb > warnGb;
            var overModels = openModels > WarnModels;
            if (!overMemory && !overModels) return null;
            var state = $"Revit uses {(gb < 0 ? "an unknown amount of" : gb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB")} memory with {openModels} model(s) open"
                + $" (warning above {warnGb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} GB, config.json openModelWarnGb, or above {WarnModels} models): another model may make Revit run out of memory and crash (live: 9 models, ~15 GB).";
            var idle = idleCopies ?? new List<string>();
            if (idle.Count == 0) return state + " ACE has no copies of its own that close quietly: ask the user which models can be closed first (ACE closes nothing else).";
            var names = string.Join(", ", idle);
            return closedIdle
                ? state + $" close_idle_copies closed ACE's own idle copies first: {names}."
                : state + $" ACE's own copies that close quietly (nobody changed them): {names}. Close them first (close_model each, or open_model again with close_idle_copies: true).";
        }
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

        /// <summary>Folder names that mark a deliverables folder (a segment of the path containing one, any case).</summary>
        internal static readonly string[] DeliverableFolderWords = { "submission", "deliverable", "issued", "outgoing", "transmittal", "export" };

        /// <summary>
        /// The warning for opening a workshared file attached (not detached): Revit creates a "&lt;name&gt;_backup" folder next to
        /// the file, which pollutes a submission folder. Stronger when a folder of the path contains a deliverables word
        /// (DeliverableFolderWords, case-insensitive). Null: not workshared, or opened detached (no backup folder is made).
        /// </summary>
        internal static string BackupFolderWarning(bool workshared, bool detached, string fullPath)
        {
            if (!workshared || detached || string.IsNullOrWhiteSpace(fullPath)) return null;
            var p = fullPath.Replace('/', '\\');
            var cut = p.LastIndexOf('\\');
            var folder = cut > 0 ? p.Substring(0, cut) : "";
            var name = Path.GetFileNameWithoutExtension(cut >= 0 ? p.Substring(cut + 1) : p);
            var segments = folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var hit = segments.FirstOrDefault(seg => DeliverableFolderWords.Any(w => seg.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
            var basic = $"{name} is workshared and opens attached: Revit creates a {name}_backup folder next to it ({(folder.Length > 0 ? folder : "its folder")}).";
            const string advice = " For read-only or export work open it with detach: \"preserve_worksets\" (detached, worksets kept): no backup folder is made and nothing can be synchronized by mistake.";
            return hit == null ? basic + advice
                : $"This looks like a deliverables folder (\"{hit}\"): " + basic + " It would sit among the issued files." + advice + " Delete the backup folder if one appears, or work on a copy outside this folder.";
        }

        /// <summary>The reference type as reported: RevitLink, CAD, or the API's name for the others.</summary>
        internal static string RefType(string apiName) => apiName == "CADLink" ? "CAD" : apiName;

        internal static string Clip(string s, int max) => s == null ? null : s.Length <= max ? s : s.Substring(0, max);
    }
    /// <summary>Pure rules for save_model's save_as: when the target is refused before Revit is called, the kind, the central warning.</summary>
    internal static class SaveAsLogic
    {
        /// <summary>
        /// Null when the target may be written, else why not (checked before Revit is called): a path is given and ends in .rvt;
        /// its folder exists; an existing file only with overwrite; not the model's own file (that is a plain save); as_central
        /// only for a workshared model.
        /// </summary>
        internal static string Refusal(string path, bool fileExists, bool folderExists, bool overwrite, bool asCentral, bool workshared, bool isOwnFile)
        {
            if (string.IsNullOrWhiteSpace(path)) return "save_as needs path: the new .rvt file.";
            if (!path.Trim().EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return $"save_as path must end in .rvt: {path}.";
            if (!folderExists) return $"The folder of {path} does not exist (or cannot be reached): ACE does not create folders.";
            if (isOwnFile) return $"{path} is the model's own file: save without save_as.";
            if (fileExists && !overwrite) return $"{path} exists already: give overwrite: true (after the user agreed to replace that file).";
            if (asCentral && !workshared) return "as_central is only for a workshared model: this one is not workshared (save_as without as_central saves it as a plain file).";
            return null;
        }

        internal static string Kind(bool asCentral) => asCentral ? "save_as central" : "save_as";

        /// <summary>
        /// Null when the document may be saved, else why not. IsReadOnly (Revit is processing failures, a read-only command)
        /// blocks every save. IsReadOnlyFile (opened from a read-only file, or a never-saved detached copy, where Revit reports
        /// true) blocks only a save into its own file: Save As to a new file is what Revit's documentation says to do then.
        /// </summary>
        internal static string ReadOnlyRefusal(string title, bool isReadOnly, bool isReadOnlyFile, bool saveAs)
        {
            if (isReadOnly) return $"{title} is read-only right now (Revit is processing failures or a read-only command runs): try again when Revit is idle.";
            if (isReadOnlyFile && !saveAs) return $"{title} was opened from a read-only file: it cannot be saved into that file; give save_as {{ path }} to save it to a new file.";
            return null;
        }

        /// <summary>
        /// After Save As: what happens to a transmitted flag the new file kept (transmitted null = not readable). State: null (not
        /// transmitted), "unknown", "on close" (clear: written off when ACE closes the model; Revit writes it only into a closed
        /// file), "kept" (no clear: a warning).
        /// </summary>
        internal static (string State, bool Clear, string Warning, string Note) TransmittedPlan(bool? transmitted, bool clear, string path)
        {
            if (transmitted == null) return ("unknown", false, null, null);
            if (transmitted == false) return (null, false, null, null);
            if (clear)
                return ("on close", true, null, $"{path} is still marked transmitted (Save As keeps the flag). Revit writes the flag only into a closed file, so ACE clears it when ACE closes this model (close_model, or open_model close_previous / close_idle_copies). Closed in Revit instead, the flag stays: then open_model with clear_transmitted: true clears it before the next open.");
            return ("kept", false, $"{path} is still marked transmitted (Save As keeps the flag): opening it asks how to treat a transmitted model. To clear it: close the model with close_model, then open_model with clear_transmitted: true (confirm), which clears it in the file before it opens.", null);
        }

        /// <summary>
        /// After Save As central: a central at a new path leaves other users' local copies pointing at the old central (they need
        /// new locals); none when the path did not change, or it was not saved as central.
        /// </summary>
        internal static string CentralWarning(bool asCentral, string oldCentral, string newPath)
        {
            if (!asCentral) return null;
            if (!string.IsNullOrEmpty(oldCentral) && string.Equals(Trim(oldCentral), Trim(newPath), StringComparison.OrdinalIgnoreCase)) return null;
            return string.IsNullOrEmpty(oldCentral)
                ? $"{newPath} is a new central model: everyone works on it through new local copies (Create New Local); links to the old file are not repointed."
                : $"The central moved from {oldCentral} to {newPath}: other users' local copies still point at the old central. They must make new locals from the new one; links to the old file are not repointed.";
        }

        private static string Trim(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\');
    }
}
