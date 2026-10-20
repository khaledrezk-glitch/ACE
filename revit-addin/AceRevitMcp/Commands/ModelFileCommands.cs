using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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
        /// close_previous }: opens and activates the file. Refuses a missing file, a file already open (it is activated
        /// instead) and a file saved in a newer Revit. close_previous closes the previously active model only if it has no
        /// unsaved changes, never saving.
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

            var options = new OpenOptions { Audit = audit };
            var workshared = info.IsWorkshared;
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

            UIDocument opened;
            try { opened = app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(full), options, false); }
            catch (Exception ex) { throw new CommandException($"Revit could not open {Path.GetFileName(full)}: {ex.Message}"); }
            var doc = opened?.Document ?? throw new CommandException($"Revit did not open {Path.GetFileName(full)}.");

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
                ["worksetsOpened"] = worksetsOpened, ["closed"] = closed, ["openDocuments"] = OpenDocuments(app), ["notes"] = notes,
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

        private static JsonArray OpenDocuments(UIApplication app) =>
            new JsonArray(app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).Select(d => (JsonNode)d.Title).ToArray());
    }
}
