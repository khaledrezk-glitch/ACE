using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// Authenticode checks for the optional local signing (trust-ace.ps1): when signing.json says signing is on, every
    /// ACE DLL of a payload must carry a valid signature (WinVerifyTrust, no network) by the same certificate as the
    /// loader itself.
    /// </summary>
    internal static class Signature
    {
        public static string SigningFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACE-RevitMCP", "signing.json");

        /// <summary>True when trust-ace.ps1 turned signing on (a missing or unreadable file means off).</summary>
        public static bool SigningOn()
        {
            try
            {
                var file = SigningFilePath;
                if (!File.Exists(file)) return false;
                var s = PayloadSelection.ParseJson<SigningFile>(File.ReadAllText(file));
                return s != null && s.Enabled && !string.IsNullOrWhiteSpace(s.Thumbprint);
            }
            catch { return false; }
        }

        /// <summary>The signer's certificate thumbprint of a validly signed file, or null with the reason.</summary>
        public static string Signer(string file, out string why)
        {
            why = null;
            var status = Verify(file);
            if (status != 0) { why = $"{Path.GetFileName(file)} has no valid signature (WinVerifyTrust 0x{status:X8})"; return null; }
            try
            {
#pragma warning disable SYSLIB0057 // the documented way to read an Authenticode signer; the signature itself was verified above
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file)))
#pragma warning restore SYSLIB0057
                    return cert.Thumbprint;
            }
            catch (Exception ex) { why = $"{Path.GetFileName(file)}: signer not readable ({ex.Message})"; return null; }
        }

        /// <summary>
        /// null when every AceRevitMcp*.dll in the payload folder is validly signed by the loader's signer; else the reason.
        /// Only called when signing is on.
        /// </summary>
        public static string CheckPayload(string loaderDll, string payloadDir)
        {
            var mine = Signer(loaderDll, out var why);
            if (mine == null) return "signing is on, but the ACE loader itself is not validly signed: " + why + ". Run install.ps1 again (or trust-ace.ps1 -Remove to turn signing off)";
            foreach (var dll in Directory.GetFiles(payloadDir, "AceRevitMcp*.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var theirs = Signer(dll, out why);
                if (theirs == null) return "signing is on: " + why;
                if (!string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase))
                    return $"signing is on: {Path.GetFileName(dll)} is signed by {theirs}, not by the loader's certificate {mine}";
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

        private static int Verify(string file)
        {
            var info = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = file };
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
