#if NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Revit 2024 runs every add-in in one .NET Framework AppDomain and reads no binding redirects from an add-in, so a
    /// library ACE ships (System.Text.Json 8 and its dependencies) can be asked for in a version that is not the one on
    /// disk, or another add-in may have loaded it already. This serves such requests: an already loaded copy of the same
    /// or a newer version first, else the copy in ACE's folder. It never loads anything that is not in ACE's folder.
    /// Must be registered before any code that uses System.Text.Json runs. Not needed on Revit 2025 (.NET 8).
    /// </summary>
    internal static class DependencyResolver
    {
        private static string _dir;

        /// <param name="dir">ACE's folder (the ACE loader loads ACE from bytes, so the assembly has no Location).</param>
        public static void Register(string dir)
        {
            if (_dir != null) return;
            _dir = dir;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        /// <summary>Payload Stop: an old build stays loaded on Revit 2024, but no longer answers for its folder.</summary>
        public static void Unregister()
        {
            if (_dir == null) return;
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
            _dir = null;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            var dir = _dir;
            if (dir == null) return null;
            var wanted = new AssemblyName(e.Name);
            // ACE itself: always this copy (the loader loads ACE from bytes; another copy may be loaded too).
            var self = typeof(DependencyResolver).Assembly;
            if (string.Equals(wanted.Name, self.GetName().Name, StringComparison.OrdinalIgnoreCase)) return self;
            var file = Path.Combine(dir, wanted.Name + ".dll");
            if (!File.Exists(file)) return null;   // not one of ours: leave it to Revit and the other add-ins
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && string.Equals(a.GetName().Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.GetName().Version).FirstOrDefault();
            if (loaded != null && (wanted.Version == null || loaded.GetName().Version >= wanted.Version)) return loaded;
            // Under the ACE loader: only a file listed in current.json, from bytes checked against it.
            var read = PayloadEntry.ReadVerified;
            if (read != null)
            {
                try { return Assembly.Load(read(file)); }
                catch (Exception ex) { Log.Warn($"Refused {file} for {e.Name}: {ex.Message}"); return loaded; }
            }
            try { return Assembly.LoadFrom(file); }
            catch (Exception ex) { Log.Warn($"Could not load {file} for {e.Name}: {ex.Message}"); return loaded; }
        }
    }
}
#endif
