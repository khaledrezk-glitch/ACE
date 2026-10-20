using System;
using System.IO;

namespace AceRevitMcp.Loader
{
    /// <summary>%APPDATA%\ACE-RevitMCP\logs\loader.log (the payload keeps its own addin.log). Never throws.</summary>
    internal static class LoaderLog
    {
        private static readonly object Gate = new object();

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACE-RevitMCP", "logs", "loader.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            lock (Gate)
            {
                try
                {
                    var file = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        File.Copy(file, file + ".old", true);
                        File.Delete(file);
                    }
                    File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
                }
                catch { /* logging must never break Revit */ }
            }
        }
    }
}
