using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Bridge
{
    /// <summary>
    /// One file per running Revit session with ACE, so the MCP server can find every session and its port:
    /// %APPDATA%\ACE-RevitMCP\instances\&lt;pid&gt;.json { port, pid, revitVersion, aceVersion, started, updated,
    /// activeDocument, openDocuments (project models; family documents are left out), user, machine }. Written when the bridge starts, rewritten when the active or open
    /// documents change (only then), removed when the bridge stops. A file left by a crash is stale: the server checks
    /// that the pid runs and the port answers /health with the same pid.
    /// </summary>
    internal static class InstanceRegistry
    {
        public static string Folder => Path.Combine(AceConfig.Directory, "instances");

        private static readonly int Pid = Process.GetCurrentProcess().Id;
        private static readonly DateTime Started = DateTime.Now;
        private static string FilePath => Path.Combine(Folder, Pid + ".json");

        private static readonly object Gate = new object();
        private static int _port;
        private static string _revitVersion;
        private static string _active;
        private static List<string> _open = new List<string>();
        private static UIControlledApplication _app;

        public static int ProcessId => Pid;

        /// <summary>Follow the active and open documents (cheap: a file is written only when they change).</summary>
        public static void Attach(UIControlledApplication app)
        {
            _app = app;
            app.ViewActivated += OnViewActivated;
            app.ControlledApplication.DocumentOpened += OnOpened;
            app.ControlledApplication.DocumentClosed += OnClosed;
        }

        public static void Detach()
        {
            if (_app == null) return;
            try
            {
                _app.ViewActivated -= OnViewActivated;
                _app.ControlledApplication.DocumentOpened -= OnOpened;
                _app.ControlledApplication.DocumentClosed -= OnClosed;
            }
            catch { /* shutting down */ }
            _app = null;
        }

        public static void Register(int port, string revitVersion)
        {
            lock (Gate) { _port = port; _revitVersion = revitVersion; }
            Write();
        }

        public static void Unregister()
        {
            lock (Gate) _port = 0;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception ex) { Log.Warn($"Instance file {FilePath} not removed: {ex.Message}"); }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e) => Refresh(e.Document?.Application, e.Document);

        private static void OnOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) => Refresh(e.Document?.Application, null);

        private static void OnClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e) =>
            Refresh(sender as Autodesk.Revit.ApplicationServices.Application, null, closing: true);

        /// <param name="active">The newly active document; null keeps the one known (unless it was closed).</param>
        private static void Refresh(Autodesk.Revit.ApplicationServices.Application app, Document active, bool closing = false)
        {
            try
            {
                var open = new List<string>();
                if (app != null)
                    foreach (Document d in app.Documents)
                        if (!d.IsLinked && !d.IsFamilyDocument) open.Add(d.Title);   // project models only (a family ACE opens to read changes nothing here)
                lock (Gate)
                {
                    var newActive = active != null && !active.IsFamilyDocument && !active.IsLinked ? active.Title : _active;   // the last project model stays active
                    if (closing && newActive != null && !open.Contains(newActive)) newActive = null;
                    if (newActive == _active && open.SequenceEqual(_open)) return;
                    _active = newActive;
                    _open = open;
                    if (_port == 0) return;
                }
                Write();
            }
            catch (Exception ex) { Log.Warn($"Instance file update: {ex.Message}"); }
        }

        private static void Write()
        {
            JsonObject json;
            lock (Gate)
            {
                if (_port == 0) return;
                json = new JsonObject
                {
                    ["port"] = _port,
                    ["pid"] = Pid,
                    ["revitVersion"] = _revitVersion,
                    ["aceVersion"] = typeof(InstanceRegistry).Assembly.GetName().Version?.ToString(3),
                    ["started"] = Started.ToString("o"),
                    ["updated"] = DateTime.Now.ToString("o"),
                    ["activeDocument"] = _active,
                    ["openDocuments"] = new JsonArray(_open.Select(t => (JsonNode)t).ToArray()),
                    ["user"] = Environment.UserName,
                    ["machine"] = Environment.MachineName,
                };
            }
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json.ToJsonString());
                Compat.MoveOverwrite(tmp, FilePath);
            }
            catch (Exception ex) { Log.Warn($"Instance file {FilePath} not written: {ex.Message}"); }
        }
    }
}
