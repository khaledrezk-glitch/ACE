using System;
using System.IO;

namespace AceRevitMcp.Util
{
    /// <summary>Appends to the same daily activity journal the MCP server writes (%APPDATA%\ACE-RevitMCP\journal).</summary>
    internal static class Journal
    {
        public static void Append(string title, string explanation, string outcome)
        {
            try
            {
                var dir = Path.Combine(AceConfig.Directory, "journal");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.md");
                var text = $"## {DateTime.Now:h:mm:ss tt} - {title}\n\n" +
                           (string.IsNullOrEmpty(explanation) ? "" : $"**What:** {explanation}\n\n") +
                           $"**Result:** {outcome}\n\n";
                File.AppendAllText(file, text);
            }
            catch
            {
                // best effort
            }
        }
    }
}
