using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The one place every rule file is found and merged (backlog C1 and T11). Two layers:
    /// <list type="bullet">
    /// <item>Office: the check set (checks, presentation), worksets.json and clash-rules.json, found as before
    /// (config.json "checkSet" / "worksetRules" / "clashRules", else %APPDATA%\ACE-RevitMCP).</item>
    /// <item>Project: <c>project-requirements.json</c> (schema "ace.project-requirements/1"), one per project, whose sections
    /// have the same shape as the office files. Found from config.json "projects" (key = the cloud model id, central path,
    /// model path or title; value = the project folder or the file), else beside the central model (or in its
    /// "ACE Vault" folder).</item>
    /// </list>
    /// The project merges into the office key by key and wins on a conflict (owner decision D3, 15 Oct 2026): a check's
    /// settings merge per key, so <c>"enabled": false</c> switches an office check off; project workset rules come before
    /// the office rules (the first matching rule wins); a section with <c>"inheritOffice": false</c> replaces the office one.
    /// Arrays (e.g. presentation text sizes) are replaced, not joined.
    /// </summary>
    public sealed class ProjectRules
    {
        public const string FileName = "project-requirements.json";
        public const string Schema = "ace.project-requirements/1";
        public static readonly string[] Sections = { "checks", "presentation", "worksets", "clash" };

        /// <summary>The project file in use, or null when the model has none (the office rules apply alone).</summary>
        public string ProjectFile { get; private set; }
        public string ProjectName { get; private set; }
        /// <summary>Why the project file was or was not found (shown to the user).</summary>
        public string FoundBy { get; private set; }

        private JsonObject _project;
        private readonly Dictionary<string, (JsonObject Json, string File)> _office = new Dictionary<string, (JsonObject, string)>();

        // ---- finding --------------------------------------------------------------------------------------------

        /// <summary>The rules in force for this model.</summary>
        public static ProjectRules For(Document doc) => ForFile(Locate(doc, out var how), how);

        /// <summary>The office rules only.</summary>
        public static ProjectRules Office() => ForFile(null, "office rules only");

        /// <summary>The rules with a given project file (null: office only). Used by tests and by For.</summary>
        public static ProjectRules ForFile(string projectFile, string foundBy = null)
        {
            var r = new ProjectRules { FoundBy = foundBy };
            if (!string.IsNullOrWhiteSpace(projectFile) && File.Exists(projectFile))
            {
                r._project = ReadJson(projectFile) ?? throw new InvalidDataException($"{projectFile} is not a JSON object.");
                var schema = r._project["schema"]?.ToString();
                if (schema != null && schema != Schema) Log.Warn($"{projectFile}: schema '{schema}', expected '{Schema}'; reading it anyway");
                r.ProjectFile = projectFile;
                r.ProjectName = (r._project["project"] as JsonObject)?["name"]?.ToString();
            }
            return r;
        }

        /// <summary>Identities a model can be mapped by in config.json "projects": cloud model id, central path, path, title.</summary>
        public static List<string> Keys(Document doc)
        {
            var keys = new List<string>();
            try
            {
                if (doc.IsModelInCloud)
                {
                    var cloud = doc.GetCloudModelPath();
                    keys.Add(cloud.GetModelGUID().ToString());
                    keys.Add(cloud.GetProjectGUID().ToString());
                }
            }
            catch { }
            try { if (doc.IsWorkshared) keys.Add(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath())); } catch { }
            if (!string.IsNullOrEmpty(doc.PathName)) keys.Add(doc.PathName);
            keys.Add(doc.Title);
            return keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The project file for a model: config.json "projects" first, then beside the (central) model.</summary>
        public static string Locate(Document doc, out string how) => LocateIn(Keys(doc), ModelFolders(doc), out how);

        /// <summary>The folders of the central model and of the model file (local and network models; none for cloud models).</summary>
        public static List<string> ModelFolders(Document doc)
        {
            var folders = new List<string>();
            foreach (var k in Keys(doc).Where(k => k.IndexOfAny(new[] { '\\', '/' }) >= 0 && !k.Contains("://")))
                try { var d = Path.GetDirectoryName(k); if (!string.IsNullOrEmpty(d) && !folders.Contains(d, StringComparer.OrdinalIgnoreCase)) folders.Add(d); } catch { }
            return folders;
        }

        /// <summary>Locate without Revit: <paramref name="keys"/> are looked up in config.json "projects", then each folder is searched.</summary>
        public static string LocateIn(IEnumerable<string> keys, IEnumerable<string> modelFolders, out string how)
        {
            if (Config()?["projects"] is JsonObject map)
                foreach (var key in keys)
                {
                    var hit = map.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
                    if (string.IsNullOrWhiteSpace(hit)) continue;
                    var file = Directory.Exists(hit) ? InFolder(hit) : hit;
                    if (file != null && File.Exists(file)) { how = $"config.json projects[\"{key}\"]"; return file; }
                    how = $"config.json maps this model to {hit}, but no {FileName} is there";
                    return null;
                }
            foreach (var folder in modelFolders)
                if (InFolder(folder) is string f) { how = "beside the model"; return f; }
            how = $"no {FileName} for this model (office rules only)";
            return null;
        }

        private static string InFolder(string folder)
        {
            foreach (var f in new[] { Path.Combine(folder, FileName), Path.Combine(folder, "ACE Vault", FileName) })
                if (File.Exists(f)) return f;
            return null;
        }

        // ---- the office layer -----------------------------------------------------------------------------------

        internal static JsonObject Config()
        {
            try { return File.Exists(AceConfig.FilePath) ? JsonNode.Parse(File.ReadAllText(AceConfig.FilePath)) as JsonObject : null; }
            catch { return null; }
        }

        /// <summary>An office file: config.json key (a path) if set, else %APPDATA%\ACE-RevitMCP\<paramref name="fileName"/>.</summary>
        internal static string OfficePath(string configKey, string fileName)
        {
            var configured = Config()?[configKey]?.ToString();
            return !string.IsNullOrWhiteSpace(configured) ? configured : Path.Combine(AceConfig.Directory, fileName);
        }

        /// <summary>The office JSON behind a section (without the project layer), and its file.</summary>
        private (JsonObject Json, string File) OfficeSection(string section)
        {
            if (_office.TryGetValue(section, out var cached)) return cached;
            (JsonObject, string) result;
            switch (section)
            {
                case "checks":
                case "presentation":
                {
                    var file = OfficePath("checkSet", "checkset.json");
                    result = (ReadJson(file)?[section] as JsonObject, File.Exists(file) ? file : null);
                    break;
                }
                case "worksets":
                {
                    var file = OfficePath("worksetRules", "worksets.json");
                    result = (ReadJson(file), File.Exists(file) ? file : null);
                    break;
                }
                case "clash":
                {
                    var file = OfficePath("clashRules", "clash-rules.json");
                    result = (ReadJson(file), File.Exists(file) ? file : null);
                    break;
                }
                default: result = (null, null); break;
            }
            if (result.Item1 != null) result.Item1 = (JsonObject)result.Item1.DeepClone();
            _office[section] = result;
            return result;
        }

        /// <summary>The office check set's display name (the file's "name").</summary>
        public string OfficeCheckSetName()
        {
            var file = OfficePath("checkSet", "checkset.json");
            var json = ReadJson(file);
            return json == null ? null : json["name"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);
        }

        // ---- merged sections ------------------------------------------------------------------------------------

        public JsonObject ProjectSection(string section) => _project?[section] as JsonObject;

        /// <summary>The section in force: office merged with project (project wins). Null when neither layer has it.</summary>
        public JsonObject Section(string section)
        {
            var office = OfficeSection(section).Json;
            var project = ProjectSection(section);
            if (project == null) return office?.DeepClone() as JsonObject;
            if (office == null || project["inheritOffice"] is JsonValue iv && iv.TryGetValue<bool>(out var inherit) && !inherit)
                return Strip((JsonObject)project.DeepClone());
            return section == "worksets" ? MergeWorksets(office, project) : Strip(Merge(office, project));
        }

        /// <summary>Where a section's rules come from, e.g. "office: C:\..\checkset.json + project: \\server\..\project-requirements.json".</summary>
        public string SourceOf(string section)
        {
            var office = OfficeSection(section);
            var project = ProjectSection(section);
            var replaces = project?["inheritOffice"] is JsonValue iv && iv.TryGetValue<bool>(out var inherit) && !inherit;
            var parts = new List<string>();
            if (office.Json != null && !replaces) parts.Add("office: " + office.File);
            if (project != null) parts.Add((replaces ? "project (replaces office): " : "project: ") + ProjectFile);
            return parts.Count == 0 ? "built-in" : string.Join(" + ", parts);
        }

        /// <summary>A short hash of the rules in force for a section: changes whenever either layer changes.</summary>
        public string HashOf(string section)
        {
            var s = Section(section)?.ToJsonString() ?? "";
            using var sha = SHA256.Create();   // works on .NET Framework 4.8 too (Revit 2024 target, backlog R24)
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        /// <summary>Deep merge: objects merge key by key, the overlay wins; arrays and values are replaced.</summary>
        public static JsonObject Merge(JsonObject baseObj, JsonObject overlay)
        {
            var result = (JsonObject)baseObj.DeepClone();
            foreach (var (key, value) in overlay)
            {
                if (value is JsonObject o && result[key] is JsonObject b) result[key] = Merge(b, o);
                else result[key] = value?.DeepClone();
            }
            return result;
        }

        /// <summary>
        /// Workset rules: the project's rules first (the first matching rule wins, so the project has priority), then the
        /// office rules, minus any office rule whose "id" the project redefines. Other keys merge as usual.
        /// </summary>
        public static JsonObject MergeWorksets(JsonObject office, JsonObject project)
        {
            var merged = Strip(Merge(office, project));
            var projectRules = (project["rules"] as JsonArray ?? new JsonArray()).Select(n => n?.DeepClone()).ToList();
            var ids = new HashSet<string>(projectRules.OfType<JsonObject>().Select(r => r["id"]?.ToString()).Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);
            var officeRules = (office["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(r => !ids.Contains(r["id"]?.ToString() ?? "")).Select(r => (JsonNode)r.DeepClone());
            merged["rules"] = new JsonArray(projectRules.Concat(officeRules).ToArray());
            return merged;
        }

        private static JsonObject Strip(JsonObject o) { o.Remove("inheritOffice"); return o; }

        // ---- writing (presentation standard window, later the vault) -------------------------------------------

        /// <summary>
        /// Writes a section to the project file when the project defines it, else returns false (the caller writes the
        /// office file). Other sections of the project file are kept.
        /// </summary>
        public bool TrySaveProjectSection(string section, JsonObject value)
        {
            if (ProjectFile == null || ProjectSection(section) == null) return false;
            var json = ReadJson(ProjectFile) ?? new JsonObject();
            if (ProjectSection(section)["inheritOffice"] is JsonNode inherit) value["inheritOffice"] = inherit.DeepClone();
            json[section] = value;
            File.WriteAllText(ProjectFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _project = json;
            return true;
        }

        internal static JsonObject ReadJson(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
            try { return JsonNode.Parse(File.ReadAllText(file).TrimStart('\uFEFF')) as JsonObject; }
            catch (Exception ex) { throw new InvalidDataException($"{file} is not valid JSON: {ex.Message}"); }
        }

        // ---- summary for Claude and the user --------------------------------------------------------------------

        public JsonObject Describe(bool includeRules)
        {
            var o = new JsonObject
            {
                ["projectFile"] = ProjectFile,
                ["project"] = ProjectName,
                ["foundBy"] = FoundBy,
                ["merge"] = "project over office, key by key; project workset rules first; a section with inheritOffice: false replaces the office one",
            };
            var sections = new JsonObject();
            foreach (var s in Sections)
            {
                var entry = new JsonObject { ["source"] = SourceOf(s), ["hash"] = HashOf(s) };
                if (includeRules) entry["rules"] = Section(s);
                sections[s] = entry;
            }
            o["sections"] = sections;
            if (_project != null)
                o["other"] = new JsonArray(_project.Select(p => p.Key).Where(k => !Sections.Contains(k) && k != "schema").Select(k => (JsonNode)k).ToArray());
            return o;
        }
    }
}
