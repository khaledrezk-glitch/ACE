using System;
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

            // Already open: activate it instead of opening it twice.
            var open = app.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && SamePath(d, full));
            if (open != null)
                throw new CommandException($"{open.Title} is already open in this Revit session. Switch to it in Revit (or close it first): ACE does not open a model twice.");

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

            JsonNode closed = null;
            if (Args.Bool(args, "close_previous") && previous != null && !previous.Equals(doc))
                closed = CloseQuietly(previous);

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
                ["transmitted"] = transmitted, ["transmittedCleared"] = transmittedCleared, ["externalReferences"] = externalReferences,
                ["unresolvedLinks"] = unresolvedLinks, ["dialogs"] = dialogs, ["notes"] = notes,
            };
        }

        /// <summary>
        /// close_model { model }: closes an open model that is not the active one, without saving: { model, closed, why, openDocuments }.
        /// The active, a modified or a linked model is kept (closed: false, why); an unknown name is an error.
        /// </summary>
        public static JsonNode Close(UIApplication app, JsonObject args)
        {
            var name = Args.Str(args, "model");
            if (string.IsNullOrWhiteSpace(name)) throw new CommandException("Give model: the title (or path) of the open model to close.");
            var docs = app.Application.Documents.Cast<Document>().ToList();
            var doc = docs.FirstOrDefault(d => string.Equals(d.Title, name, StringComparison.OrdinalIgnoreCase))
                      ?? docs.FirstOrDefault(d => !string.IsNullOrEmpty(d.PathName) && string.Equals(d.PathName, name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new CommandException($"No open model is called '{name}'. Open: {string.Join(", ", docs.Where(d => !d.IsLinked).Select(d => d.Title))}.");
            JsonObject Kept(string why) => new JsonObject { ["model"] = doc.Title, ["closed"] = false, ["why"] = why, ["openDocuments"] = OpenDocuments(app) };
            if (doc.IsLinked) return Kept("a linked model: unload the link in the host instead");
            if (app.ActiveUIDocument?.Document?.Equals(doc) == true) return Kept("the active model: ACE never closes the active model; switch to another model first (open_model)");
            if (doc.IsModified) return Kept("it has unsaved changes: ACE does not close it (nothing is saved); save or discard them in Revit first");
            var title = doc.Title;
            bool done;
            try { done = doc.Close(false); }
            catch (Exception ex) { return Kept("Revit did not close it: " + ex.Message); }
            InstanceRegistry.Update(app);
            return new JsonObject { ["model"] = title, ["closed"] = done, ["why"] = done ? null : "Revit did not close it", ["openDocuments"] = OpenDocuments(app) };
        }

        /// <summary>Closes a model that is not active without saving; a modified one is kept open: { model, closed, why }.</summary>
        private static JsonObject CloseQuietly(Document d)
        {
            var title = d.Title;
            if (d.IsLinked || d.IsFamilyDocument) return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "not a project model opened on its own" };
            if (d.IsModified) return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "it has unsaved changes: kept open (ACE never saves or discards them)" };
            try { return new JsonObject { ["model"] = title, ["closed"] = d.Close(false), ["why"] = null }; }
            catch (Exception ex) { return new JsonObject { ["model"] = title, ["closed"] = false, ["why"] = "Revit did not close it: " + ex.Message }; }
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
