using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AceRevitMcp.Loader
{
    /// <summary>One payload build as named in current.json: { version, path, sha256 }.</summary>
    [DataContract]
    internal sealed class PayloadRef
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        /// <summary>The build's folder, relative to the loader's folder (payload\&lt;version&gt;-&lt;hash&gt;) or absolute.</summary>
        [DataMember(Name = "path")] public string Path { get; set; }
        /// <summary>SHA-256 of the build's AceRevitMcp.dll, hex.</summary>
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    /// <summary>current.json, written by install.ps1: the build to load, and the one before it (the fallback).</summary>
    [DataContract]
    internal sealed class CurrentFile
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "previous")] public PayloadRef Previous { get; set; }

        public PayloadRef Current => new PayloadRef { Version = Version, Path = Path, Sha256 = Sha256 };
    }

    /// <summary>signing.json (trust-ace.ps1): { enabled, thumbprint }.</summary>
    [DataContract]
    internal sealed class SigningFile
    {
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "thumbprint")] public string Thumbprint { get; set; }
    }

    /// <summary>A payload build that passed (or failed) the checks: Why is null when it may be loaded.</summary>
    internal sealed class Candidate
    {
        public string Version, Directory, Dll, Sha256, Why;
        public bool FromPrevious;
        public bool Ok => Why == null;
    }

    /// <summary>
    /// Which payload to load, and whether it may be loaded. Pure logic (no Revit): tested offline in tools/tracker-test.
    /// The loader only ever loads from &lt;its own folder&gt;\payload\&lt;one folder&gt;\AceRevitMcp.dll, never from a path given
    /// over the bridge; the file must match the SHA-256 in current.json, and when signing is on, the signer check passes.
    /// </summary>
    internal static class PayloadSelection
    {
        public const string ManifestName = "current.json";
        public const string PayloadFolder = "payload";
        public const string PayloadDll = "AceRevitMcp.dll";

        private static readonly Regex VersionText = new Regex(@"^\d{1,5}(\.\d{1,5}){1,3}([-+][0-9A-Za-z.\-]{1,40})?$", RegexOptions.CultureInvariant);

        /// <summary>A version that is safe to show and to use in a folder name (1.13.4, 1.14.0-dev).</summary>
        public static bool IsSafeVersion(string version) => version != null && VersionText.IsMatch(version);

        public static T ParseJson<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("the file is empty");
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json.Trim().TrimStart('﻿'))))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
            }
            catch (Exception ex) when (ex is SerializationException || ex is InvalidCastException || ex is ArgumentException)
            {
                throw new FormatException("not valid JSON (" + ex.Message + ")");
            }
        }

        public static CurrentFile Parse(string json) => ParseJson<CurrentFile>(json) ?? throw new FormatException("the file is empty");

        /// <summary>
        /// The build's folder as an absolute path, or null with the reason: it must be a folder directly inside
        /// &lt;root&gt;\payload (no "..", no other drive, no nested folders).
        /// </summary>
        public static string ResolveDir(string root, string path, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(path)) { why = "current.json names no payload folder"; return null; }
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { why = "the payload folder name has invalid characters"; return null; }
            var normal = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string full, payloadRoot;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(normal) ? normal : Path.Combine(root, normal)).TrimEnd(Path.DirectorySeparatorChar);
                payloadRoot = Path.GetFullPath(Path.Combine(root, PayloadFolder)).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) { why = "the payload folder is not a valid path (" + ex.Message + ")"; return null; }
            var parent = Path.GetDirectoryName(full);
            if (parent == null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), payloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                why = $"the payload folder {path} is not inside {payloadRoot} (ACE loads only from its own install folder)";
                return null;
            }
            return full;
        }

        public static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }

        public static bool SameHash(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Checks one build: version text, folder inside root\payload, the DLL present, its SHA-256 as listed, and
        /// (signerCheck, may be null) the signature. hashOf is the file hash (Sha256Of; a test can pass its own).
        /// </summary>
        public static Candidate Check(string root, PayloadRef r, Func<string, string> hashOf, Func<string, string> signerCheck)
        {
            var c = new Candidate { Version = r?.Version };
            if (r == null) { c.Why = "no payload is named"; return c; }
            if (!IsSafeVersion(r.Version)) { c.Why = $"the version '{r.Version}' is not a version number"; return c; }
            c.Directory = ResolveDir(root, r.Path, out var why);
            if (c.Directory == null) { c.Why = why; return c; }
            c.Dll = Path.Combine(c.Directory, PayloadDll);
            if (!File.Exists(c.Dll)) { c.Why = $"{c.Dll} is missing"; return c; }
            if (string.IsNullOrWhiteSpace(r.Sha256)) { c.Why = "current.json lists no sha256 for the payload"; return c; }
            string actual;
            try { actual = hashOf(c.Dll); }
            catch (Exception ex) { c.Why = $"{c.Dll} cannot be read ({ex.Message})"; return c; }
            if (!SameHash(actual, r.Sha256)) { c.Why = $"{c.Dll} does not match the sha256 in current.json (changed or incomplete copy)"; return c; }
            c.Sha256 = actual.ToLowerInvariant();
            if (signerCheck != null) c.Why = signerCheck(c.Directory);
            return c;
        }

        /// <summary>The build named current, else the previous one (marked FromPrevious) with both reasons when neither passes.</summary>
        public static Candidate Select(string root, CurrentFile file, Func<string, string> hashOf, Func<string, string> signerCheck)
        {
            if (file == null) return new Candidate { Why = "no current.json" };
            var current = Check(root, file.Current, hashOf, signerCheck);
            if (current.Ok || file.Previous == null) return current;
            var previous = Check(root, file.Previous, hashOf, signerCheck);
            previous.FromPrevious = true;
            if (!previous.Ok) previous.Why = $"current build {file.Version}: {current.Why}; previous build {file.Previous.Version}: {previous.Why}";
            return previous;
        }

        /// <summary>Numeric comparison of dotted versions (1.13.10 &gt; 1.13.9); a pre-release tag sorts before its release.</summary>
        public static int CompareVersions(string a, string b)
        {
            (int[] Parts, string Tag) Split(string v)
            {
                v = (v ?? "").Trim();
                var cut = v.IndexOfAny(new[] { '-', '+' });
                var tag = cut >= 0 ? v.Substring(cut + 1) : null;
                var nums = (cut >= 0 ? v.Substring(0, cut) : v).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
                return (nums, tag);
            }
            var (pa, ta) = Split(a);
            var (pb, tb) = Split(b);
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var x = i < pa.Length ? pa[i] : 0;
                var y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (ta == null && tb == null) return 0;
            if (ta == null) return 1;
            if (tb == null) return -1;
            return string.CompareOrdinal(ta, tb);
        }

        /// <summary>How a swap from running to next reads: first load, upgrade, downgrade, rebuild of the same version.</summary>
        public static string Direction(string running, string next)
        {
            if (string.IsNullOrEmpty(running)) return "first load";
            var c = CompareVersions(next, running);
            return c > 0 ? "upgrade" : c < 0 ? "downgrade" : "same version, new build";
        }
    }
}
