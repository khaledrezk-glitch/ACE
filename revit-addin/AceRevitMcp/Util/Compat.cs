using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// One add-in for Revit 2024 (.NET Framework 4.8) and 2025 (.NET 8): helpers for the few .NET 8-only calls ACE uses,
    /// written once so both builds behave the same. New code uses these instead of Convert.ToHexString, X.HashData,
    /// Math.Clamp or double.IsFinite.
    /// </summary>
    internal static class Compat
    {
        /// <summary>Upper-case hex, like Convert.ToHexString.</summary>
        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        public static string Sha256Hex(string text) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))); }
        public static string Sha1Hex(string text) { using (var h = SHA1.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))); }
        public static string Md5Hex(string text) { using (var h = MD5.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))); }

        public static byte[] RandomBytes(int count)
        {
            var buf = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            return buf;
        }

        /// <summary>Constant-time comparison (CryptographicOperations.FixedTimeEquals).</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>File.Move with overwrite (the overload is .NET Core only); File.Replace keeps the target until the swap.</summary>
        public static void MoveOverwrite(string from, string to)
        {
            if (System.IO.File.Exists(to)) System.IO.File.Replace(from, to, null);
            else System.IO.File.Move(from, to);
        }

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

#if NETFRAMEWORK
        /// <summary>foreach (var (key, value) in dictionary) on .NET Framework.</summary>
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key; value = pair.Value;
        }
#endif
    }
}

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only setters compile for .NET Framework 4.8.</summary>
    internal static class IsExternalInit { }
}
#endif
