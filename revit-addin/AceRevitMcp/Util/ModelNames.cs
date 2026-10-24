using System;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Util
{
    /// <summary>Whether a document is the model a caller named: its title, its title without ".rvt", or its file path
    /// (case-insensitive). Used to refuse work when the user has switched Revit's active window to another model.</summary>
    internal static class ModelNames
    {
        public static bool Same(Document doc, string expected) => doc != null && Matches(doc.Title, SafePath(doc), expected);

        public static bool Matches(string title, string path, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            var e = expected.Trim();
            string Bare(string x) => x != null && x.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.Length - 4) : x;
            return string.Equals(Bare(title), Bare(e), StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(path) && (string.Equals(path, e, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Bare(System.IO.Path.GetFileName(path)), Bare(e), StringComparison.OrdinalIgnoreCase)));
        }

        private static string SafePath(Document doc) { try { return doc.PathName; } catch { return null; } }
    }
}
