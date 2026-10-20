using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AceRevitMcp.Loader
{
    /// <summary>One payload build as named in current.json: { version, path, sha256, files }.</summary>
    [DataContract]
    internal sealed class PayloadRef
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        /// <summary>The build's folder, relative to the loader's folder (payload\&lt;version&gt;-&lt;hash&gt;) or absolute.</summary>
        [DataMember(Name = "path")] public string Path { get; set; }
        /// <summary>SHA-256 of the build's AceRevitMcp.dll, hex.</summary>
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        /// <summary>Every file of the build folder (relative path, either slash) -> SHA-256, hex. Written by install.ps1.</summary>
        [DataMember(Name = "files")] public Dictionary<string, string> Files { get; set; }
    }

    /// <summary>current.json, written by install.ps1: the build to load, and the one before it (the fallback).</summary>
    [DataContract]
    internal sealed class CurrentFile
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "files")] public Dictionary<string, string> Files { get; set; }
        [DataMember(Name = "previous")] public PayloadRef Previous { get; set; }

        public PayloadRef Current => new PayloadRef { Version = Version, Path = Path, Sha256 = Sha256, Files = Files };
    }

    /// <summary>signing.json (trust-ace.ps1): { enabled, thumbprint }.</summary>
    [DataContract]
    internal sealed class SigningFile
    {
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "thumbprint")] public string Thumbprint { get; set; }
    }

    /// <summary>
    /// The signing setting as the loader sees it. Fails closed: a signing.json that exists but can't be read, or turns
    /// signing on without a usable thumbprint, counts as "on" with Why set, and nothing is loaded until it is fixed.
    /// </summary>
    internal sealed class SigningState
    {
        public bool On;
        /// <summary>The pinned certificate thumbprint (upper case hex) every ACE DLL and the loader must be signed with.</summary>
        public string Thumbprint;
        /// <summary>Set when signing.json exists but is unusable: nothing may be loaded.</summary>
        public string Why;

        public static SigningState Read(string file)
        {
            if (!File.Exists(file)) return new SigningState();
            try
            {
                var s = PayloadSelection.ParseJson<SigningFile>(File.ReadAllText(file));
                if (s == null) throw new FormatException("the file is empty");
                if (!s.Enabled) return new SigningState();
                var thumb = (s.Thumbprint ?? "").Replace(" ", "").Trim();
                if (!Regex.IsMatch(thumb, "^[0-9A-Fa-f]{40}$"))
                    return new SigningState { On = true, Why = $"{file} turns signing on but has no valid certificate thumbprint" };
                return new SigningState { On = true, Thumbprint = thumb.ToUpperInvariant() };
            }
            catch (Exception ex)
            {
                return new SigningState { On = true, Why = $"{file} exists but can't be read ({ex.Message}), so signing counts as on" };
            }
        }
    }

    /// <summary>A payload build that passed (or failed) the checks: Why is null when it may be loaded.</summary>
    internal sealed class Candidate
    {
        public string Version, Directory, Dll, Sha256, Why;
        public bool FromPrevious;
        /// <summary>Why the build named current was not used (FromPrevious only).</summary>
        public string CurrentWhy;
        /// <summary>The verified bytes of AceRevitMcp.dll: the loader loads exactly these (never the file again).</summary>
        public byte[] Bytes;
        /// <summary>The verified bytes of each ACE DLL (AceRevitMcp*.dll), relative path -> bytes, for the signature check.</summary>
        public Dictionary<string, byte[]> AceDlls = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The manifest: relative path (forward slashes) -> SHA-256, lower case. Only these files may be loaded.</summary>
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Ok => Why == null;
    }

    /// <summary>
    /// Which payload to load, and whether it may be loaded. Pure logic (no Revit): tested offline in tools/tracker-test.
    /// The loader only ever loads from &lt;its own folder&gt;\payload\&lt;one folder&gt;, never from a path given over the
    /// bridge. Every file in that folder must be listed in current.json with its SHA-256 and match it (no extra files, no
    /// junctions or links), AceRevitMcp.dll must be the build's sha256, and when signing is on the signer check passes.
    /// The hash protects against corruption, incomplete copies and swapped files; it does not protect against a malicious
    /// program running as the same Windows user (that program could rewrite current.json too). Signing narrows this: such
    /// a program would also need the private key, which can't be exported from the certificate store.
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
                    return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(ms);
            }
            catch (Exception ex) when (ex is SerializationException || ex is InvalidCastException || ex is ArgumentException)
            {
                throw new FormatException("not valid JSON (" + ex.Message + ")");
            }
        }

        public static CurrentFile Parse(string json) => ParseJson<CurrentFile>(json) ?? throw new FormatException("the file is empty");

        /// <summary>A manifest key or a path below the build folder in one form: forward slashes, no leading "./" or slash.</summary>
        public static string NormalRel(string rel)
        {
            var r = (rel ?? "").Replace('\\', '/').Trim();
            while (r.StartsWith("./", StringComparison.Ordinal)) r = r.Substring(2);
            return r.TrimStart('/');
        }

        public static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }   // can't tell: treated as one (refused)
        }

        /// <summary>
        /// The build's folder as an absolute path, or null with the reason: it must be a folder directly inside
        /// &lt;root&gt;\payload (no "..", no other drive, no nested folders), and neither it nor payload\ may be a junction
        /// or symbolic link.
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
            if (Directory.Exists(payloadRoot) && IsReparsePoint(payloadRoot)) { why = $"{payloadRoot} is a junction or link (refused)"; return null; }
            if (Directory.Exists(full) && IsReparsePoint(full)) { why = $"the payload folder {full} is a junction or link (refused)"; return null; }
            return full;
        }

        public static string Sha256Of(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        public static string Sha256Of(string file) => Sha256Of(File.ReadAllBytes(file));

        public static bool SameHash(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads one file of a verified build and returns its bytes when they match the manifest; else throws. The
        /// resolvers (payload context, compiler context, Revit 2024 dependencies) load only through this, from the bytes.
        /// </summary>
        public static byte[] ReadVerified(string dir, IDictionary<string, string> files, string fullPath, Func<string, byte[]> read = null)
        {
            var baseDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{fullPath} is outside the ACE build folder");
            var rel = NormalRel(full.Substring(baseDir.Length));
            if (files == null || !files.TryGetValue(rel, out var expected)) throw new InvalidOperationException($"{rel} is not listed in current.json");
            var sub = Path.GetDirectoryName(full);
            while (sub != null && sub.Length >= baseDir.Length)
            {
                if (IsReparsePoint(sub)) throw new InvalidOperationException($"{sub} is a junction or link (refused)");
                sub = Path.GetDirectoryName(sub);
            }
            if (IsReparsePoint(full)) throw new InvalidOperationException($"{rel} is a link (refused)");
            var bytes = (read ?? File.ReadAllBytes)(full);
            if (!SameHash(Sha256Of(bytes), expected)) throw new InvalidOperationException($"{rel} does not match its sha256 in current.json (changed after install)");
            return bytes;
        }

        /// <summary>
        /// Checks one build: version text, folder inside root\payload, the manifest (every file listed, no extra files, no
        /// links, every hash right), AceRevitMcp.dll as the build's sha256, and (signerCheck, may be null) the signature on
        /// the verified bytes. read is File.ReadAllBytes (a test can pass its own). Each file is read once.
        /// </summary>
        public static Candidate Check(string root, PayloadRef r, Func<string, byte[]> read, Func<Candidate, string> signerCheck)
        {
            read = read ?? File.ReadAllBytes;
            var c = new Candidate { Version = r?.Version };
            if (r == null) { c.Why = "no payload is named"; return c; }
            if (!IsSafeVersion(r.Version)) { c.Why = $"the version '{r.Version}' is not a version number"; return c; }
            c.Directory = ResolveDir(root, r.Path, out var why);
            if (c.Directory == null) { c.Why = why; return c; }
            if (!Directory.Exists(c.Directory)) { c.Why = $"{c.Directory} is missing"; return c; }
            c.Dll = Path.Combine(c.Directory, PayloadDll);
            if (string.IsNullOrWhiteSpace(r.Sha256)) { c.Why = "current.json lists no sha256 for the payload"; return c; }
            if (r.Files == null || r.Files.Count == 0) { c.Why = "current.json lists no files for the payload (run install.ps1 again)"; return c; }
            foreach (var kv in r.Files)
            {
                var key = NormalRel(kv.Key);
                if (key.Length == 0 || key.Split('/').Any(p => p == ".." || p == "." || p.Length == 0)) { c.Why = $"current.json lists an invalid file name '{kv.Key}'"; return c; }
                c.Files[key] = (kv.Value ?? "").Trim().ToLowerInvariant();
            }
            if (!c.Files.TryGetValue(PayloadDll, out var mainHash) || !SameHash(mainHash, r.Sha256))
            { c.Why = "current.json: its files list and its sha256 of AceRevitMcp.dll disagree"; return c; }

            // Every file and folder on disk: no links, nothing unlisted; then nothing listed may be missing.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bad = Walk(c.Directory, c.Directory, c.Files, seen);
            if (bad != null) { c.Why = bad; return c; }
            var missing = c.Files.Keys.FirstOrDefault(k => !seen.Contains(k));
            if (missing != null) { c.Why = $"{missing} (listed in current.json) is missing from {c.Directory}"; return c; }

            foreach (var kv in c.Files)
            {
                var full = Path.Combine(c.Directory, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                byte[] bytes;
                try { bytes = read(full); }
                catch (Exception ex) { c.Why = $"{kv.Key} cannot be read ({ex.Message})"; return c; }
                if (!SameHash(Sha256Of(bytes), kv.Value)) { c.Why = $"{kv.Key} does not match its sha256 in current.json (changed or incomplete copy)"; return c; }
                if (string.Equals(kv.Key, PayloadDll, StringComparison.OrdinalIgnoreCase)) c.Bytes = bytes;
                var name = Path.GetFileName(kv.Key);
                if (name.StartsWith("AceRevitMcp", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) c.AceDlls[kv.Key] = bytes;
            }
            c.Sha256 = mainHash.ToLowerInvariant();
            if (signerCheck != null) c.Why = signerCheck(c);
            return c;
        }

        private static string Walk(string baseDir, string dir, IDictionary<string, string> listed, HashSet<string> seen)
        {
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                var rel = NormalRel(entry.Substring(baseDir.Length));
                if (IsReparsePoint(entry)) return $"{rel} in the payload folder is a junction or link (refused)";
                if (Directory.Exists(entry))
                {
                    var inner = Walk(baseDir, entry, listed, seen);
                    if (inner != null) return inner;
                    continue;
                }
                if (!listed.ContainsKey(rel)) return $"{rel} is in the payload folder but not listed in current.json (refused)";
                seen.Add(rel);
            }
            return null;
        }

        /// <summary>
        /// The build named current, else the previous one (FromPrevious, with CurrentWhy saying why current was not used),
        /// with both reasons when neither passes.
        /// </summary>
        public static Candidate Select(string root, CurrentFile file, Func<string, byte[]> read, Func<Candidate, string> signerCheck)
        {
            if (file == null) return new Candidate { Why = "no current.json" };
            var current = Check(root, file.Current, read, signerCheck);
            if (current.Ok || file.Previous == null) return current;
            var previous = Check(root, file.Previous, read, signerCheck);
            previous.FromPrevious = true;
            previous.CurrentWhy = $"current build {file.Version}: {current.Why}";
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
