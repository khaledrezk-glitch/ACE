using System;
using System.IO;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    internal static class BackupCommands
    {
        /// <summary>
        /// Copies the model file as it is on disk to %APPDATA%\ACE-RevitMCP\backups. The open model is not
        /// touched. With save_first the model is saved (Ctrl+S) before copying so unsaved work is included.
        /// </summary>
        public static JsonNode Backup(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            if (string.IsNullOrEmpty(doc.PathName))
                throw new CommandException("This model has never been saved, so there is no file to back up. Ask the user to save it first (File > Save As).");
            if (doc.IsWorkshared && !File.Exists(doc.PathName))
                throw new CommandException($"The model file '{doc.PathName}' is not a local file (cloud / server model). Use the model's own versioning instead.");

            var saved = false;
            if (Args.Bool(args, "save_first") && doc.IsModified)
            {
                doc.Save();
                saved = true;
            }

            var dir = Path.Combine(AceConfig.Directory, "backups");
            Directory.CreateDirectory(dir);
            var name = Path.GetFileNameWithoutExtension(doc.PathName);
            var ext = Path.GetExtension(doc.PathName);
            var target = Path.Combine(dir, $"{name}_{DateTime.Now:yyyy-MM-dd_HHmmss}{ext}");

            // Revit keeps the file open; share read/write so the copy never interferes with it.
            using (var source = new FileStream(doc.PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                source.CopyTo(dest);

            var response = new JsonObject
            {
                ["backup"] = target,
                ["sizeMB"] = Math.Round(new FileInfo(target).Length / 1048576.0, 1),
                ["savedBeforeBackup"] = saved,
            };
            if (doc.IsModified)
                response["warning"] = "The model has unsaved changes that are NOT in this backup (it copies the last saved file). Call again with save_first: true to include them.";
            if (doc.IsWorkshared)
                response["note"] = "Workshared model: this is a copy of your LOCAL file. Open it detached if you ever need to restore from it.";
            Log.Info($"Backup created: {target}");
            return response;
        }
    }
}
