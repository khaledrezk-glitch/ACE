using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// Authenticode checks for the optional local signing (trust-ace.ps1). When signing.json turns signing on, the loader
    /// itself and every ACE DLL of a payload (AceRevitMcp*.dll, the Compiler included) must carry a valid signature
    /// (WinVerifyTrust, no network) by the certificate pinned in signing.json. Third-party DLLs (System.Text.Json, Roslyn)
    /// can't carry ACE's signature: they are covered by the manifest hash in current.json, like every file.
    /// The signature is checked on a private copy written from the verified bytes (held open, no writers), so the bytes
    /// checked are the bytes loaded.
    /// </summary>
    internal static class Signature
    {
        public static string SigningFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACE-RevitMCP", "signing.json");

        /// <summary>signing.json now (fails closed: unreadable = on, with a reason).</summary>
        public static SigningState Read() => SigningState.Read(SigningFilePath);

        /// <summary>The signer's certificate thumbprint of a validly signed file, or null with the reason.</summary>
        public static string Signer(string file, out string why) => Signer(file, IntPtr.Zero, Path.GetFileName(file), out why);

        private static string Signer(string file, IntPtr handle, string label, out string why)
        {
            why = null;
            var status = Verify(file, handle);
            if (status != 0) { why = $"{label} has no valid signature (WinVerifyTrust 0x{status:X8})"; return null; }
            try
            {
#pragma warning disable SYSLIB0057 // the documented way to read an Authenticode signer; the signature itself was verified above
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file)))
#pragma warning restore SYSLIB0057
                    return cert.Thumbprint;
            }
            catch (Exception ex) { why = $"{label}: signer not readable ({ex.Message})"; return null; }
        }

        /// <summary>The signer of these bytes, checked on a private copy that no one else can write while it is checked.</summary>
        private static string SignerOfBytes(byte[] bytes, string label, out string why)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ACE-loader-verify");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                    return Signer(tmp, fs.SafeFileHandle.DangerousGetHandle(), label, out why);
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>
        /// null when the loader and every ACE DLL of the verified build are validly signed by the pinned certificate;
        /// else the reason. Only called when signing is on.
        /// </summary>
        public static string CheckPayload(SigningState signing, string loaderDll, Candidate c)
        {
            if (signing.Why != null) return signing.Why + ". Nothing is loaded: fix signing.json (trust-ace.ps1) or remove it (trust-ace.ps1 -Remove)";
            var pinned = signing.Thumbprint;
            var mine = Signer(loaderDll, out var why);
            if (mine == null) return "signing is on, but the ACE loader itself is not validly signed: " + why + ". Run install.ps1 again (or trust-ace.ps1 -Remove to turn signing off)";
            if (!string.Equals(mine, pinned, StringComparison.OrdinalIgnoreCase))
                return $"signing is on: the ACE loader is signed by {mine}, not by the certificate in signing.json ({pinned})";
            if (c.AceDlls.Count == 0) return "signing is on, but the build has no ACE DLL to check";
            foreach (var kv in c.AceDlls.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var theirs = SignerOfBytes(kv.Value, kv.Key, out why);
                if (theirs == null) return "signing is on: " + why;
                if (!string.Equals(theirs, pinned, StringComparison.OrdinalIgnoreCase))
                    return $"signing is on: {kv.Key} is signed by {theirs}, not by the certificate in signing.json ({pinned})";
            }
            return null;
        }

        // ---- WinVerifyTrust (wintrust.dll) ------------------------------------------------------------------------

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, StateVerify = 1, StateClose = 2;
        private const uint CacheOnlyUrlRetrieval = 0x1000, DisableMd2Md4 = 0x2000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

        private static int Verify(string file, IntPtr handle)
        {
            var info = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = file, hFile = handle };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(info, pFile, false);
                var data = new TrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(TrustData)),
                    dwUIChoice = UiNone,
                    fdwRevocationChecks = RevokeNone,
                    dwUnionChoice = ChoiceFile,
                    pFile = pFile,
                    dwStateAction = StateVerify,
                    dwProvFlags = CacheOnlyUrlRetrieval | DisableMd2Md4,
                };
                try { return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data); }
                finally
                {
                    data.dwStateAction = StateClose;
                    WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                }
            }
            catch (Exception) { return -1; }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(FileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
