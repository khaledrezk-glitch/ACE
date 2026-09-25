using System;
using System.Collections.Generic;
using System.IO;

namespace AceRevitMcp.Util
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();

        public static string FilePath => Path.Combine(AceConfig.Directory, "logs", "addin.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static IReadOnlyList<string> Tail()
        {
            lock (Gate) return new List<string>(Recent);
        }

        private static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            lock (Gate)
            {
                Recent.AddLast(line);
                while (Recent.Count > 30) Recent.RemoveFirst();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 5 * 1024 * 1024)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never break Revit.
                }
            }
        }
    }
}
