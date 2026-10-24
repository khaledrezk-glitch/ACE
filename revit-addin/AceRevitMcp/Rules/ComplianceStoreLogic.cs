using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AceRevitMcp.Dashboard;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The persisted half of the status store's compliance entry (W2 / DM1), pure (no Revit types): the last compliance run
    /// and the one before it, so the Companion shows the last result, with its time and its deltas, as soon as a model
    /// opens. The run is written in the run record's own format (<see cref="Compliance.ToJson"/>), plus what only the
    /// Companion needs (the failing element ids per check, capped, and each requirement's check keys); <see cref="ParseRun"/>
    /// reads both that and a plain run record from the vault (04 Audits/&lt;model&gt;/runs). One file per model under
    /// %APPDATA%\ACE-RevitMCP\companion-state\, named "&lt;model file name without .rvt&gt;__&lt;key&gt;.json" so a demo
    /// reset can clear one model's state by its file name.
    /// </summary>
    public static class ComplianceStoreLogic
    {
        public const string FolderName = "companion-state";
        public const int Version = 1;
        /// <summary>Failing element ids kept per check (the "Show in model" button after a reload): enough to look, not a dump.</summary>
        public const int MaxIdsPerCheck = 5000;

        /// <summary>The model's file name without folder and ".rvt" (else the title), safe as a file name.</summary>
        public static string BaseName(string title, string pathName)
        {
            string n = null;
            if (!string.IsNullOrWhiteSpace(pathName))
            {
                n = pathName.Trim();
                var i = n.LastIndexOfAny(new[] { '\\', '/' });
                if (i >= 0) n = n.Substring(i + 1);
            }
            if (string.IsNullOrWhiteSpace(n)) n = title ?? "model";
            if (n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            n = Safe(n.Trim());
            return n.Length == 0 ? "model" : n;
        }

        /// <summary>
        /// "&lt;base&gt;__&lt;8 hex&gt;.json": the base is the model's file name (what a reset script matches), the key hashes the
        /// record key (RuleModels.RecordKey: the central's name for a workshared model, so a fresh local finds its audit)
        /// and the file name, so two files of the same name in different projects do not share a state.
        /// </summary>
        public static string FileName(string baseName, string recordKey) =>
            $"{baseName}__{Hash8((recordKey ?? "").ToLowerInvariant())}.json";

        private static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            return string.Concat((s ?? "").Select(c => bad.Contains(c) ? '_' : c));
        }

        private static string Hash8(string s)
        {
            // FNV-1a, 32 bit: stable across runtimes and builds (string.GetHashCode is not).
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
            return h.ToString("x8");
        }

        /// <summary>The file's JSON: the last run and the previous one (either may be null).</summary>
        public static JsonObject Serialize(Compliance.Run last, Compliance.Run previous, string model, DateTime savedUtc) => new JsonObject
        {
            ["schema"] = "ace.companion-state/1",
            ["version"] = Version,
            ["model"] = model,
            ["savedAt"] = savedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["last"] = last == null ? null : RunJson(last),
            ["previous"] = previous == null ? null : RunJson(previous),
        };

        /// <summary>A run as JSON: the run record's format plus check ids and requirement check keys.</summary>
        public static JsonObject RunJson(Compliance.Run run)
        {
            var o = Compliance.ToJson(run);
            if (o["checks"] is JsonArray checks)
                for (var i = 0; i < checks.Count && i < run.Checks.Count; i++)
                    if (checks[i] is JsonObject c && run.Checks[i].Ids.Count > 0)
                    {
                        c["ids"] = new JsonArray(run.Checks[i].Ids.Take(MaxIdsPerCheck).Select(x => (JsonNode)x).ToArray());
                        if (run.Checks[i].Ids.Count > MaxIdsPerCheck) c["idsCapped"] = run.Checks[i].Ids.Count;
                    }
            if (o["results"] is JsonArray results)
            {
                var keys = run.Results.GroupBy(r => r.Requirement ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().CheckKeys, StringComparer.OrdinalIgnoreCase);
                foreach (var r in results.OfType<JsonObject>())
                    if (keys.TryGetValue(r["requirement"]?.ToString() ?? "", out var k) && k.Count > 0)
                        r["checkKeys"] = new JsonArray(k.Select(x => (JsonNode)x).ToArray());
            }
            return o;
        }

        /// <summary>Reads the file's JSON; a broken or foreign file gives (null, null), never an exception.</summary>
        public static (Compliance.Run Last, Compliance.Run Previous) Parse(JsonObject file)
        {
            try
            {
                if (file == null) return (null, null);
                if (file["version"] is JsonValue v && v.TryGetValue<int>(out var ver) && ver > Version) return (null, null);   // a newer build's format
                return (ParseRun(file["last"] as JsonObject), ParseRun(file["previous"] as JsonObject));
            }
            catch { return (null, null); }
        }

        /// <summary>A run from its JSON (this store's, or a vault run record); null when it is not one.</summary>
        public static Compliance.Run ParseRun(JsonObject o)
        {
            if (o?["run"] is not JsonObject head || head["id"] == null) return null;
            string S(JsonNode n) => n is JsonValue ? n.ToString() : null;
            int? I(JsonNode n) => n is JsonValue jv && jv.TryGetValue<int>(out var x) ? x : n is JsonValue jd && jd.TryGetValue<double>(out var d) ? (int)d : (int?)null;
            var run = new Compliance.Run
            {
                Id = S(head["id"]), Model = S(head["model"]), Engine = S(head["engine"]) ?? "model-check", Time = S(head["time"]), User = S(head["user"]),
                RulesHash = S(head["rulesHash"]), RequirementsHash = S(head["requirementsHash"]), ProjectFile = S(head["projectFile"]), Vault = S(head["vault"]),
                Score = I(head["score"]),
            };
            foreach (var c in (o["checks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var check = new Check
                {
                    Key = S(c["key"]), Area = S(c["area"]), Name = S(c["name"]), Count = I(c["count"]) ?? 0, Of = I(c["of"]), Unit = S(c["unit"]),
                    Detail = S(c["detail"]), StatusOverride = S(c["status"]) ?? "ok",
                };
                if (c["ids"] is JsonArray ids) check.Ids.AddRange(ids.Select(x => x is JsonValue xv && xv.TryGetValue<long>(out var id) ? id : 0).Where(id => id != 0));
                run.Checks.Add(check);
            }
            foreach (var r in (o["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var res = new Compliance.Result
                {
                    Requirement = S(r["requirement"]), Status = S(r["status"]) ?? "not-checkable", Severity = S(r["severity"]), Kind = S(r["kind"]), Binding = S(r["binding"]),
                    Count = I(r["count"]), Of = I(r["of"]), Evidence = S(r["evidence"]), Decision = S(r["decision"]), Doc = S(r["doc"]), Where = S(r["at"]), Text = S(r["text"]),
                };
                if (string.IsNullOrEmpty(res.Requirement)) continue;
                // A run record has no checkKeys: they are its binding's "C1:checks/<key>" parts.
                if (r["checkKeys"] is JsonArray ks) res.CheckKeys = ks.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                else if (!string.IsNullOrEmpty(res.Binding))
                    res.CheckKeys = res.Binding.Split(',').Select(x => x.Trim()).Where(x => x.StartsWith("C1:checks/", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Substring("C1:checks/".Length)).Where(x => x.Length > 0).ToList();
                run.Results.Add(res);
            }
            foreach (var kv in o["summary"] as JsonObject ?? new JsonObject()) if (I(kv.Value) is int n) run.Summary[kv.Key] = n;
            foreach (var kv in o["openBySeverity"] as JsonObject ?? new JsonObject()) if (I(kv.Value) is int n) run.NotMetBySeverity[kv.Key] = n;
            return run;
        }

        /// <summary>
        /// The two newest run records of a vault folder's file names ("&lt;id&gt;.json", ids start with the UTC time, so they sort
        /// by name): newest first.
        /// </summary>
        public static List<string> NewestRecords(IEnumerable<string> files, int take = 2) =>
            (files ?? Enumerable.Empty<string>()).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(take).ToList();
    }
}
