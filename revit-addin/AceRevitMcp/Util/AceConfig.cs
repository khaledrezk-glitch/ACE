using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// Shared settings between the Revit add-in and the MCP server:
    /// %APPDATA%\ACE-RevitMCP\config.json  { "port": 48884, "token": "..." }
    /// </summary>
    internal sealed class AceConfig
    {
        public const int DefaultPort = 48884;

        public int Port { get; private set; } = DefaultPort;
        public string Token { get; private set; }

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACE-RevitMCP");

        public static string FilePath => Path.Combine(Directory, "config.json");

        public static AceConfig LoadOrCreate()
        {
            var config = new AceConfig();
            System.IO.Directory.CreateDirectory(Directory);

            JsonObject json = null;
            if (File.Exists(FilePath))
            {
                try { json = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject; }
                catch (Exception ex) { Log.Warn($"config.json is not valid JSON, regenerating it: {ex.Message}"); }
            }
            json ??= new JsonObject();

            var changed = false;
            if (json["port"] is JsonValue p && p.TryGetValue<int>(out var port) && port > 0 && port < 65536)
                config.Port = port;
            else { json["port"] = config.Port; changed = true; }

            if (json["token"] is JsonValue t && t.TryGetValue<string>(out var token) && !string.IsNullOrWhiteSpace(token))
                config.Token = token;
            else
            {
                config.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                json["token"] = config.Token;
                changed = true;
            }

            if (changed)
                File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return config;
        }
    }
}
