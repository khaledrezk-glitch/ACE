using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;

namespace AceRevitMcp
{
    /// <summary>
    /// Host-agnostic entry of the ACE payload. The ACE loader (AceRevitMcp.Loader, the DLL in the .addin manifest) finds
    /// Start and Stop by reflection and passes a host dictionary of plain framework and Revit types (no shared contracts
    /// assembly to version): contract, loaderVersion, loaderPath, payloadDir, reloaded, bindCommand, externalEvent,
    /// setEventHandler, setPaneContent, requestReload, status. Loaded directly by a plain manifest (App, fallback / dev),
    /// the host is null and ACE registers its own ExternalEvent, pane and ribbon commands as before.
    /// Stop() releases everything Start() took (bridge port, Revit events, ExternalEvent target, pane content, open
    /// windows, the script compiler), so a new build can start in the same Revit session.
    /// </summary>
    public static class PayloadEntry
    {
        /// <summary>The oldest loader contract this build works with.</summary>
        private const int MinContract = 1;

        private static IDictionary<string, object> _host;
        private static UIControlledApplication _app;

        /// <summary>The payload's folder (Location is empty for a build loaded from bytes on Revit 2024).</summary>
        internal static string Directory { get; private set; }

        internal static bool Hosted => _host != null;

        internal static string LoaderVersion => Get<string>("loaderVersion");

        internal static T Get<T>(string key) where T : class =>
            _host != null && _host.TryGetValue(key, out var v) ? v as T : null;

        public static void Start(UIControlledApplication app, IDictionary<string, object> host)
        {
            if (host != null && !(host.TryGetValue("contract", out var c) && c is int n && n >= MinContract))
                throw new InvalidOperationException($"This ACE build needs ACE loader contract {MinContract} or later; install the loader that comes with it (restart Revit).");
            _host = host;
            _app = app;
            var dir = host != null && host.TryGetValue("payloadDir", out var d) ? d as string : null;
            Directory = !string.IsNullOrEmpty(dir) ? dir : Path.GetDirectoryName(typeof(PayloadEntry).Assembly.Location);
#if NETFRAMEWORK
            // Revit 2024: serve System.Text.Json and its dependencies from ACE's folder before anything uses them.
            Util.DependencyResolver.Register(Directory);
#endif
            try { StartCore(app); }
            catch
            {
                try { Stop(); } catch { /* the original failure is what matters */ }
                throw;
            }
        }

        // Separate so the libraries it uses are loaded only after the resolver above is in place.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StartCore(UIControlledApplication app) => App.StartServices(app);

        public static void Stop()
        {
            var app = _app;
            App.StopServices(app);
#if NETFRAMEWORK
            Util.DependencyResolver.Unregister();
#endif
            _host = null;
            _app = null;
        }
    }
}
