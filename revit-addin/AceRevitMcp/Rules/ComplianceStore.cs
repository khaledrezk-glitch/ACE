using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Keeps the status store's compliance entry across sessions (W2 / DM1): every published run is saved per model to
    /// %APPDATA%\ACE-RevitMCP\companion-state\&lt;model file name&gt;__&lt;key&gt;.json (ComplianceStoreLogic), and read back when
    /// the model opens, so the Companion's Home and Compliance tab show the last result with its time. Without that file
    /// (another PC, a cleared state) the two newest run records in the vault (04 Audits/&lt;model&gt;/runs) are used. Reading
    /// never changes the model; a broken file is ignored.
    /// </summary>
    internal static class ComplianceStore
    {
        public static string Folder => Path.Combine(AceConfig.Directory, ComplianceStoreLogic.FolderName);

        private static string PathPart(Func<string> f) { try { return f(); } catch { return null; } }

        /// <summary>
        /// The model's state file. The key is the record key plus where the model lives: the central's path for a workshared
        /// model (so a fresh local of the same central finds its audit), else the file's path (so two copies of one file in
        /// different folders, e.g. a demo copy, keep their own state).
        /// </summary>
        public static string FileFor(Document doc)
        {
            var path = PathPart(() => doc.PathName);
            var central = PathPart(() => doc.IsWorkshared ? ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()) : null);
            var where = !string.IsNullOrWhiteSpace(central) ? central : !string.IsNullOrWhiteSpace(path) ? path : doc.Title;
            return Path.Combine(Folder, ComplianceStoreLogic.FileName(ComplianceStoreLogic.BaseName(doc.Title, path), ProjectRulesCommands.ModelKey(doc) + "|" + where));
        }

        /// <summary>Saves the model's last and previous run (best effort: a failure is logged, never thrown).</summary>
        public static void Save(Document doc, Compliance.Run last, Compliance.Run previous)
        {
            if (doc == null || last == null) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var file = FileFor(doc);
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, ComplianceStoreLogic.Serialize(last, previous, doc.Title, DateTime.UtcNow).ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception ex) { Log.Warn($"Companion state: could not save the compliance run: {ex.Message}"); }
        }

        private static readonly HashSet<string> Tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the model's last run into the status store, once per model and session, when the store has none yet
        /// (DocumentOpened, a view activated, or a reload of ACE with models open).
        /// </summary>
        public static void LoadInto(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument || doc.IsLinked) return;
                var title = doc.Title;
                if (StatusStore.For(title)?.Compliance != null) return;
                if (!Tried.Add(Companion.ActivityHub.DocKey(doc))) return;
                var (last, previous) = Read(doc);
                if (last == null) return;
                StatusStore.PublishCompliance(title, last, previous, fromDisk: true);
                Log.Info($"Companion state: last compliance run {last.Id} of '{title}' read back ({last.Results.Count} requirements)");
            }
            catch (Exception ex) { Log.Warn($"Companion state: could not read the last compliance run: {ex.Message}"); }
        }

        private static (Compliance.Run, Compliance.Run) Read(Document doc)
        {
            var file = FileFor(doc);
            if (File.Exists(file))
            {
                var got = ComplianceStoreLogic.Parse(JsonNode.Parse(File.ReadAllText(file)) as JsonObject);
                if (got.Last != null) return got;
            }
            // No state on this PC: the vault's run records (written only into a vault the user trusted).
            ProjectRules rules;
            try { rules = ProjectRules.For(doc); } catch { return (null, null); }
            var vault = Compliance.VaultOf(rules);
            if (vault == null) return (null, null);
            var dir = Path.Combine(vault, "04 Audits", Tracking.Snapshots.ModelKey(doc), "runs");
            if (!Directory.Exists(dir)) return (null, null);
            var runs = ComplianceStoreLogic.NewestRecords(Directory.GetFiles(dir, "*.json"))
                .Select(f => { try { return ComplianceStoreLogic.ParseRun(JsonNode.Parse(File.ReadAllText(f)) as JsonObject); } catch { return null; } })
                .Where(r => r != null).ToList();
            foreach (var r in runs) r.Vault ??= vault;
            return (runs.ElementAtOrDefault(0), runs.ElementAtOrDefault(1));
        }
    }
}
