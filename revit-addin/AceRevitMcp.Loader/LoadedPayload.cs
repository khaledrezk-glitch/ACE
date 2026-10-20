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
    /// Revit 2025: a collectible AssemblyLoadContext per build, so Unload() lets the old build go once nothing holds it.
    /// Revit 2024 (.NET Framework): loaded from bytes into the one AppDomain (a new copy per load, so the same version can
    /// be loaded again); an old copy can't be unloaded and stays in memory, inert after Stop(), until Revit closes.
    /// </summary>
    internal sealed class LoadedPayload
    {
        public string Version { get; private set; }
        public string Directory { get; private set; }
        public string Sha256 { get; private set; }
        public Assembly Assembly { get; private set; }
        private MethodInfo _start, _stop;
#if !NETFRAMEWORK
        private PayloadContext _context;
#endif

        public static LoadedPayload Load(Candidate c)
        {
            var p = new LoadedPayload { Directory = c.Directory, Sha256 = c.Sha256 };
#if NETFRAMEWORK
            var pdb = Path.ChangeExtension(c.Dll, ".pdb");
            p.Assembly = File.Exists(pdb) ? Assembly.Load(File.ReadAllBytes(c.Dll), File.ReadAllBytes(pdb)) : Assembly.Load(File.ReadAllBytes(c.Dll));
#else
            p._context = new PayloadContext("ACE payload " + c.Version + " " + (c.Sha256 ?? "").Substring(0, Math.Min(8, (c.Sha256 ?? "").Length)), c.Directory);
            p.Assembly = p._context.LoadFromAssemblyPath(c.Dll);
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
        /// Resolves the payload's own files from its folder; Revit, the loader, WPF and the framework always come from the
        /// default context, so the payload shares their types (IExternalCommand, UIControlledApplication, FrameworkElement).
        /// </summary>
        private sealed class PayloadContext : AssemblyLoadContext
        {
            private readonly string _dir;
            public PayloadContext(string name, string dir) : base(name, isCollectible: true) { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                var simple = name.Name ?? "";
                if (Shared(simple)) return null;
                var file = Path.Combine(_dir, simple + ".dll");
                return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
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
