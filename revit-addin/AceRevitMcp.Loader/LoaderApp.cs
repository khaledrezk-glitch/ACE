using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// The ACE loader: the IExternalApplication named in AceRevitMcp.addin. It owns what Revit can't hand over or take back
    /// (the ribbon commands, the one ExternalEvent, the Companion pane) and loads the ACE payload named in current.json
    /// next to it. A new build is swapped in while Revit runs: on request (bridge command reload_addin, through the payload)
    /// or by itself when current.json changes while no model is open. A swap runs only in Idling (no command, no open
    /// transaction, no modal dialog): payload.Stop(), load the new build, Start(); on any failure the previous build starts
    /// again.
    /// </summary>
    public sealed class LoaderApp : IExternalApplication
    {
        /// <summary>The host contract the payload reads (PayloadEntry.Start checks it).</summary>
        private const int Contract = 1;
        private const int SlotCount = 64;

        private static LoaderApp _current;

        private UIControlledApplication _app;
        private string _root, _loaderDll, _loaderVersion;
        private ExternalEvent _event;
        private readonly ForwardingHandler _forward = new ForwardingHandler();
        private PaneHost _pane;

        private LoadedPayload _active;
        private LoadedPayload _starting;   // the build being started (Revit 2024: assembly requests by name go to it)
        private string _previousVersion, _lastWhy;
        private WeakReference _previousContext;
        private Dictionary<string, object> _lastReload;

        private bool _reloadRequested, _reloadForce;
        private DateTime _notBefore, _nextFileCheck;
        private DateTime? _seenStamp;
        private bool _pending;
        private string _pendingVersion;
        private int _loadSeq;
        /// <summary>Set while the previous build runs because the current one failed its checks (integrity or signature).</summary>
        private string _fallbackNote;
        /// <summary>
        /// Plain data a build leaves for the next one in this Revit session (models ACE opened, the last undoable run, the
        /// ribbon panels): the loader keeps it, so a reload does not forget it. Never Revit documents or ACE types.
        /// </summary>
        private readonly Dictionary<string, object> _store = new Dictionary<string, object>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly string[] _slotClass = new string[SlotCount];

        public Result OnStartup(UIControlledApplication application)
        {
            _current = this;
            _app = application;
            _loaderDll = typeof(LoaderApp).Assembly.Location;
            _root = Path.GetDirectoryName(_loaderDll);
            _loaderVersion = typeof(LoaderApp).Assembly.GetName().Version?.ToString(3);
            LoaderLog.Info($"ACE loader {_loaderVersion} starting in Revit {application.ControlledApplication.VersionNumber} from {_root}");
#if NETFRAMEWORK
            AppDomain.CurrentDomain.AssemblyResolve += ResolvePayload;
#endif
            try
            {
                _pane = new PaneHost();
                application.RegisterDockablePane(PaneHost.Id, "ACE Companion", _pane);
            }
            catch (Exception ex) { LoaderLog.Error($"Companion pane: {ex}"); _pane = null; }
            try { _event = ExternalEvent.Create(_forward); }
            catch (Exception ex) { LoaderLog.Error($"ExternalEvent: {ex}"); }
            application.Idling += OnIdling;

            var result = Swap(null, force: true, reason: "Revit started");
            if (!true.Equals(result["ok"])) LoaderLog.Error($"ACE not loaded at startup: {result["why"]}");
            // The loader stays loaded either way, so a fixed install can be picked up without a restart.
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { application.Idling -= OnIdling; } catch { }
            StopActive();
            return Result.Succeeded;
        }

        // ---- the host the payload sees ------------------------------------------------------------------------------

        private Dictionary<string, object> Host(LoadedPayload p, bool reloaded, UIApplication ui) => new Dictionary<string, object>
        {
            ["contract"] = Contract,
            ["loaderVersion"] = _loaderVersion,
            ["loaderPath"] = _loaderDll,
            ["payloadDir"] = p.Directory,
            ["payloadSha256"] = p.Sha256,
            // This load of this build: a preview made on another one (another build, or the same build loaded again) is refused.
            ["buildId"] = $"{p.Version}-{(p.Sha256 ?? "").Substring(0, Math.Min(12, (p.Sha256 ?? "").Length))}#{_loadSeq}",
            ["reloaded"] = reloaded,
            // null at Revit startup; on a reload the session's UIApplication, so the build can read the open documents.
            ["uiApplication"] = ui,
            ["store"] = _store,
            // The only way the build reads more of its own files (compiler, dependencies): checked against current.json.
            ["readVerified"] = (Func<string, byte[]>)p.ReadVerified,
            ["bindCommand"] = (Func<string, string>)BindCommand,
            ["externalEvent"] = _event,
            ["setEventHandler"] = (Action<IExternalEventHandler>)(h => _forward.Target = h),
            ["setPaneContent"] = (Action<object>)(c => _pane?.SetContent(c, _lastWhy)),
            ["requestReload"] = (Func<bool, IDictionary<string, object>>)RequestReload,
            ["status"] = (Func<IDictionary<string, object>>)Status,
        };

        /// <summary>
        /// A ribbon button's command class in the payload -> the loader's proxy class for it (a fixed pool Cmd00..Cmd63).
        /// The mapping is kept for the whole Revit session, so a button made by one build runs the same command in the next.
        /// </summary>
        private string BindCommand(string payloadClass)
        {
            if (string.IsNullOrEmpty(payloadClass)) return null;
            lock (_slotOf)
            {
                if (!_slotOf.TryGetValue(payloadClass, out var slot))
                {
                    slot = Array.IndexOf(_slotClass, null);
                    if (slot < 0) { LoaderLog.Warn($"No free ribbon command slot for {payloadClass} (all {SlotCount} used)."); return null; }
                    _slotClass[slot] = payloadClass;
                    _slotOf[payloadClass] = slot;
                }
                return typeof(ProxyCommand).Namespace + ".Cmd" + slot.ToString("00");
            }
        }

        internal static Result RunCommand(int slot, ExternalCommandData data, ref string message, ElementSet elements)
        {
            var me = _current;
            string cls;
            lock (me._slotOf) cls = slot >= 0 && slot < SlotCount ? me._slotClass[slot] : null;
            var asm = me._active?.Assembly;
            if (asm == null)
            {
                var td = new TaskDialog("ACE")
                {
                    MainInstruction = "ACE is not loaded",
                    MainContent = (me._lastWhy ?? "No ACE build was loaded.") + $"\n\nLoader {me._loaderVersion}, folder {me._root}\nLog: {LoaderLog.FilePath}",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Load ACE again", "Reads current.json and loads the installed build.");
                if (td.Show() == TaskDialogResult.CommandLink1)
                {
                    var r = me.Swap(data.Application, force: true, reason: "ribbon: load again");
                    TaskDialog.Show("ACE", true.Equals(r["ok"]) ? $"ACE {r["payloadVersion"]} is loaded." : $"ACE could not be loaded: {r["why"]}");
                }
                return Result.Cancelled;
            }
            var type = cls == null ? null : asm.GetType(cls, throwOnError: false);
            if (type == null || !typeof(IExternalCommand).IsAssignableFrom(type))
            {
                TaskDialog.Show("ACE", $"This button belongs to a command the loaded ACE build ({me._active.Version}) no longer has ({cls ?? "slot " + slot}). Restart Revit to rebuild the ribbon.");
                return Result.Cancelled;
            }
            try
            {
                var command = (IExternalCommand)Activator.CreateInstance(type);
                return command.Execute(data, ref message, elements);
            }
            catch (Exception ex)
            {
                LoaderLog.Error($"{cls}: {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---- reload --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Called by the payload (bridge reload_addin) in API context. Checks the installed build now and schedules the
        /// swap for the next idle moment, so this request's answer is sent before the bridge stops. The outcome is in
        /// Status()["lastReload"] (the bridge's /health shows it once the new build listens).
        /// </summary>
        private IDictionary<string, object> RequestReload(bool force)
        {
            var answer = Base();
            var c = ReadCandidate(out var why);
            answer["candidate"] = c?.Version;
            if (c == null || !c.Ok)
            {
                answer["ok"] = false; answer["scheduled"] = false;
                answer["why"] = "nothing to load: " + (c?.Why ?? why) + ". The running build stays.";
                return answer;
            }
            if (!force && _active != null && PayloadSelection.SameHash(c.Sha256, _active.Sha256))
            {
                answer["ok"] = true; answer["scheduled"] = false;
                answer["why"] = $"ACE {_active.Version} is already the installed build (force reloads it anyway).";
                return answer;
            }
            _reloadRequested = true;
            _reloadForce = force;
            _notBefore = DateTime.UtcNow.AddSeconds(1.5);
            answer["ok"] = true; answer["scheduled"] = true;
            answer["direction"] = PayloadSelection.Direction(_active?.Version, c.Version);
            answer["why"] = $"ACE {c.Version} will be loaded in a moment (when Revit is idle); the bridge restarts on the same port.";
            return answer;
        }

        private Dictionary<string, object> Base() => new Dictionary<string, object>
        {
            ["loaderVersion"] = _loaderVersion,
            ["payloadVersion"] = _active?.Version,
            ["previous"] = _previousVersion,
        };

        private IDictionary<string, object> Status()
        {
            var s = Base();
            s["payloadPath"] = _active?.Directory;
            s["pending"] = _pending;
            s["pendingVersion"] = _pending ? _pendingVersion : null;
            s["lastReload"] = _lastReload;
            s["payloadSha256"] = _active?.Sha256;
            s["fallback"] = _fallbackNote;
            if (_previousContext != null) s["previousUnloaded"] = !_previousContext.IsAlive;
            return s;
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                var ui = sender as UIApplication;
                if (_reloadRequested)
                {
                    if (DateTime.UtcNow < _notBefore) { e.SetRaiseWithoutDelay(); return; }
                    _reloadRequested = false;
                    Swap(ui, _reloadForce, "reload_addin");
                    return;
                }
                if (DateTime.UtcNow < _nextFileCheck) return;
                _nextFileCheck = DateTime.UtcNow.AddSeconds(5);
                var file = Path.Combine(_root, PayloadSelection.ManifestName);
                DateTime? stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : (DateTime?)null;
                if (stamp != _seenStamp)
                {
                    // A new current.json: check the build it names (once per change; the files are hashed).
                    _seenStamp = stamp;
                    var c = ReadCandidate(out _);
                    _pending = c != null && c.Ok && (_active == null || !PayloadSelection.SameHash(c.Sha256, _active.Sha256));
                    _pendingVersion = c?.Version;
                    if (_pending) LoaderLog.Info($"current.json now names ACE {c.Version}; running {_active?.Version ?? "nothing"}.");
                }
                // A pending build waits while a model is open, and is swapped in at the first check with none open (or when
                // nothing is loaded). A failed swap is not retried until current.json changes again.
                if (!_pending) return;
                if (_active == null || (ui != null && ui.Application.Documents.IsEmpty))
                {
                    var r = Swap(ui, false, _active == null ? "current.json changed (nothing was loaded)" : "current.json changed, no model open");
                    if (!true.Equals(r["ok"])) _pending = false;
                }
            }
            catch (Exception ex) { LoaderLog.Error($"Idling: {ex}"); }
        }

        private Candidate ReadCandidate(out string why)
        {
            why = null;
            var file = Path.Combine(_root, PayloadSelection.ManifestName);
            if (!File.Exists(file)) { why = $"{file} is missing (run install.ps1)"; return null; }
            CurrentFile current;
            try { current = PayloadSelection.Parse(File.ReadAllText(file)); }
            catch (Exception ex) { why = $"{file} is not readable: {ex.Message}"; return null; }
            var signing = Signature.Read();
            Func<Candidate, string> signer = null;
            if (signing.On) signer = cand => Signature.CheckPayload(signing, _loaderDll, cand);
            else LoaderLog.Info($"Signing is off ({Signature.SigningFilePath} absent or disabled): the build is checked by its sha256 list only.");
            var c = PayloadSelection.Select(_root, current, null, signer);
            if (c != null && c.FromPrevious && c.Ok) LoaderLog.Warn($"Integrity: {c.CurrentWhy}. The previous build {c.Version} is used instead.");
            return c;
        }

        /// <summary>
        /// Stops the running build and starts the installed one; on failure the previous build starts again. Only from a
        /// Revit API context with no open transaction (startup, Idling, or a ribbon command). Returns {loaderVersion,
        /// payloadVersion, previous, ok, why, direction, reason, at}.
        /// </summary>
        private Dictionary<string, object> Swap(UIApplication ui, bool force, string reason)
        {
            var result = Base();
            result["reason"] = reason;
            result["at"] = DateTime.Now.ToString("o");
            try
            {
                var open = ui == null ? null : ui.Application.Documents.Cast<Document>().FirstOrDefault(d => d.IsModifiable);
                if (open != null) return Done(result, false, $"a transaction is open in {open.Title}; try again when Revit is idle");

                var c = ReadCandidate(out var why);
                if (c == null || !c.Ok) return Done(result, false, "nothing to load: " + (c?.Why ?? why) + (_active != null ? $". ACE {_active.Version} keeps running." : ""));
                result["direction"] = PayloadSelection.Direction(_active?.Version, c.Version);
                if (c.FromPrevious) result["note"] = $"the current build failed its checks ({c.CurrentWhy}), so the previous one ({c.Version}) is used";
                if (!force && _active != null && PayloadSelection.SameHash(c.Sha256, _active.Sha256))
                    return Done(result, true, $"ACE {_active.Version} is already the installed build");

                var old = _active;
                if (old != null) StopActive();

                LoadedPayload next = null;
                try
                {
                    _loadSeq++;
                    next = _starting = LoadedPayload.Load(c);
                    next.Start(_app, Host(next, reloaded: old != null, ui));
                }
                catch (Exception ex)
                {
                    _starting = null;
                    LoaderLog.Error($"Loading ACE {c.Version} from {c.Directory} failed: {ex}");
                    try { next?.Stop(); } catch { }
                    try { next?.Unload(); } catch { }
                    var failed = $"ACE {c.Version} did not start: {ex.GetType().Name}: {ex.Message}";
                    if (old == null) { _lastWhy = failed; _pane?.SetContent(null, _lastWhy); return Done(result, false, failed); }
                    try
                    {
                        _starting = old;
                        _loadSeq++;
                        try { old.Start(_app, Host(old, reloaded: true, ui)); }
                        finally { _starting = null; }
                        _active = old;
                        result["payloadVersion"] = old.Version;
                        return Done(result, false, failed + $". ACE {old.Version} was started again.");
                    }
                    catch (Exception again)
                    {
                        LoaderLog.Error($"Restarting ACE {old.Version} failed too: {again}");
                        _lastWhy = failed + $"; restarting {old.Version} failed too: {again.Message}";
                        _pane?.SetContent(null, _lastWhy);
                        result["payloadVersion"] = null;
                        return Done(result, false, _lastWhy + ". Restart Revit.");
                    }
                }

                _active = next;
                _starting = null;
                _lastWhy = null;
                _fallbackNote = c.FromPrevious ? $"ACE {c.Version} (the previous build) runs: the installed build failed its checks ({c.CurrentWhy}). Run install.ps1 again." : null;
                _pane?.SetNote(_fallbackNote);
                if (_fallbackNote != null) result["fallback"] = _fallbackNote;
                _pending = false;
                _seenStamp = File.GetLastWriteTimeUtc(Path.Combine(_root, PayloadSelection.ManifestName));
                if (old != null)
                {
                    _previousVersion = old.Version;
                    _previousContext = old.Unload();
                }
                result["payloadVersion"] = next.Version;
                result["previous"] = _previousVersion;
                LoaderLog.Info($"ACE {next.Version} started from {next.Directory} ({reason}).");
                return Done(result, true, old == null ? $"ACE {next.Version} loaded" : $"ACE {old.Version} replaced by {next.Version}");
            }
            catch (Exception ex)
            {
                LoaderLog.Error($"Swap: {ex}");
                return Done(result, false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private Dictionary<string, object> Done(Dictionary<string, object> result, bool ok, string why)
        {
            result["ok"] = ok;
            result["why"] = why;
            if (!ok) LoaderLog.Warn($"Reload ({result["reason"]}): {why}");
            _lastReload = result;
            return result;
        }

        private void StopActive()
        {
            var p = _active;
            if (p == null) return;
            try { p.Stop(); }
            catch (Exception ex) { LoaderLog.Error($"Stopping ACE {p.Version}: {ex}"); }
            _forward.Target = null;
            _active = null;
        }

#if NETFRAMEWORK
        /// <summary>A build loaded from bytes is not found by name: answer for it (and only for it).</summary>
        private Assembly ResolvePayload(object sender, ResolveEventArgs e)
        {
            var asm = (_starting ?? _active)?.Assembly;
            if (asm == null) return null;
            var wanted = new AssemblyName(e.Name);
            return string.Equals(wanted.Name, asm.GetName().Name, StringComparison.OrdinalIgnoreCase) ? asm : null;
        }
#endif

        /// <summary>The one ExternalEvent; its work is done by the running build's dispatcher.</summary>
        private sealed class ForwardingHandler : IExternalEventHandler
        {
            public volatile IExternalEventHandler Target;

            public void Execute(UIApplication app)
            {
                var t = Target;
                if (t == null) return;
                try { t.Execute(app); }
                catch (Exception ex) { LoaderLog.Error($"Bridge handler: {ex}"); }
            }

            public string GetName() => "ACE Revit MCP bridge";
        }
    }
}
