using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// One loaded payload build. Its entry is found by reflection (no shared contracts assembly to version):
    /// public static AceRevitMcp.PayloadEntry.Start(UIControlledApplication, IDictionary&lt;string, object&gt;) and Stop().
    /// Loaded from the bytes that passed the checks (never from the file again). Revit 2025: a collectible
    /// AssemblyLoadContext per build (LoadFromStream), so Unload() lets the old build go once nothing holds it; its own
    /// dependencies come only from files listed in current.json, verified as they are read. Revit 2024 (.NET Framework):
    /// Assembly.Load(bytes) into the one AppDomain (a new copy per load, so the same version can be loaded again); an old
    /// copy can't be unloaded and stays in memory, inert after Stop(), until Revit closes.
    /// </summary>
    internal sealed class LoadedPayload
    {
        public string Version { get; private set; }
        public string Directory { get; private set; }
        public string Sha256 { get; private set; }
        public Assembly Assembly { get; private set; }
        /// <summary>The build's manifest (relative path -> sha256): the only files that may be loaded from its folder.</summary>
        public IDictionary<string, string> Files { get; private set; }
        private MethodInfo _start, _stop;
#if !NETFRAMEWORK
        private PayloadContext _context;
#endif

        public static LoadedPayload Load(Candidate c)
        {
            if (c.Bytes == null) throw new InvalidOperationException("the build was not verified");
            var p = new LoadedPayload { Directory = c.Directory, Sha256 = c.Sha256, Files = new Dictionary<string, string>(c.Files, StringComparer.OrdinalIgnoreCase) };
#if NETFRAMEWORK
            p.Assembly = Assembly.Load(c.Bytes);
#else
            p._context = new PayloadContext("ACE payload " + c.Version + " " + (c.Sha256 ?? "").Substring(0, Math.Min(8, (c.Sha256 ?? "").Length)), p);
            using (var ms = new MemoryStream(c.Bytes)) p.Assembly = p._context.LoadFromStream(ms);
#endif
            p.Version = p.Assembly.GetName().Version?.ToString(3) ?? c.Version;
            var entry = p.Assembly.GetType("AceRevitMcp.PayloadEntry", throwOnError: false);
            p._start = entry?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UIControlledApplication), typeof(IDictionary<string, object>) }, null);
            p._stop = entry?.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (p._start == null || p._stop == null)
            {
                p.Unload();
                throw new InvalidOperationException($"{c.Dll} has no AceRevitMcp.PayloadEntry.Start/Stop (an ACE build older than the loader)");
            }
            return p;
        }

        public void Start(UIControlledApplication app, IDictionary<string, object> host) => Invoke(_start, app, host);

        public void Stop() => Invoke(_stop);

        /// <summary>The bytes of one file of this build, verified against its manifest (the host's readVerified).</summary>
        public byte[] ReadVerified(string fullPath) => PayloadSelection.ReadVerified(Directory, Files, fullPath);

        private static void Invoke(MethodInfo m, params object[] args)
        {
            try { m.Invoke(null, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        /// <summary>Lets the build go (2025). The returned reference shows later whether it was really collected.</summary>
        public WeakReference Unload()
        {
            _start = _stop = null;
#if NETFRAMEWORK
            return null;
#else
            var ctx = _context;
            _context = null;
            Assembly = null;
            if (ctx == null) return null;
            var weak = new WeakReference(ctx);
            try { ctx.Unload(); } catch (Exception ex) { LoaderLog.Warn($"Unload of payload {Version}: {ex.Message}"); }
            return weak;
#endif
        }

#if !NETFRAMEWORK
        /// <summary>
        /// Resolves the payload's own files from its folder, only those listed in current.json and only from verified
        /// bytes; Revit, the loader, WPF and the framework always come from the default context, so the payload shares
        /// their types (IExternalCommand, UIControlledApplication, FrameworkElement).
        /// </summary>
        private sealed class PayloadContext : AssemblyLoadContext
        {
            private readonly LoadedPayload _owner;
            public PayloadContext(string name, LoadedPayload owner) : base(name, isCollectible: true) { _owner = owner; }

            protected override Assembly Load(AssemblyName name)
            {
                var simple = name.Name ?? "";
                if (Shared(simple)) return null;
                var file = Path.Combine(_owner.Directory, simple + ".dll");
                if (!_owner.Files.ContainsKey(simple + ".dll")) return null;
                try
                {
                    using (var ms = new MemoryStream(_owner.ReadVerified(file))) return LoadFromStream(ms);
                }
                catch (Exception ex) { LoaderLog.Warn($"Payload {_owner.Version}: {simple} refused: {ex.Message}"); return null; }
            }

            private static bool Shared(string n) =>
                n.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AdWindows", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("UIFramework", StringComparison.OrdinalIgnoreCase) || n.Equals("AceRevitMcp.Loader", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || n.Equals("netstandard", StringComparison.OrdinalIgnoreCase) || n.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
                || n.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase);
        }
#endif
    }
}
