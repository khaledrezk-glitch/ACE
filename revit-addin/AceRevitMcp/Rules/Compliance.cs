using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AceRevitMcp.Dashboard;
using AceRevitMcp.Util;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Compliance per requirement (backlog C1+): turns a model check run into one result per requirement in the vault, with
    /// a run record (id, rules hash, requirements hash) so "out of date" and "this audit vs the last one" can be computed.
    /// A requirement reaches a check through a binding: a rule in project-requirements.json that names it
    /// (<c>"requirement": "REQ-..."</c> on a check or a workset rule), or the record's own <c>test.binding</c>
    /// ("C1:checks/&lt;key&gt;"). Status and severity are separate scales (owner decision D5): met, partly (only when a
    /// check measured part of a set, e.g. 64 of 100), not-met, not-checkable; an accepted deviation is reported beside the
    /// status, never turned into "met".
    /// </summary>
    public static class Compliance
    {
        public sealed class Result
        {
            public string Requirement, Binding, Status, Evidence, Severity, Kind, Decision;
            /// <summary>Where the requirement comes from: document key and revision, place in it, and its words (short).</summary>
            public string Doc, Where, Text;
            /// <summary>The check keys it is bound to (to select the failing elements).</summary>
            public List<string> CheckKeys = new List<string>();
            public int? Count, Of;
        }

        public sealed class Run
        {
            public string Id, Model, Engine = "model-check", RulesHash, RequirementsHash, User, Time, ProjectFile, Vault;
            public List<Result> Results = new List<Result>();
            /// <summary>The model check this run was made from: score and every check's result (the report's model findings).</summary>
            public int? Score;
            internal List<Check> Checks = new List<Check>();
            public Dictionary<string, int> Summary = new Dictionary<string, int>();
            public Dictionary<string, int> NotMetBySeverity = new Dictionary<string, int>();
        }

        /// <summary>The workset rules' model check row: every workset rule's requirement binds to it.</summary>
        public const string WorksetCheckKey = "elements-on-the-wrong-workset-bep";

        /// <summary>
        /// Evaluates the requirements against the checks of one model check run. <paramref name="requirements"/> is the
        /// vault's requirements.json (records), <paramref name="decisions"/> its decisions.json; either may be null.
        /// </summary>
        internal static Run Evaluate(ProjectRules rules, JsonObject requirements, JsonObject decisions, IList<Check> checks, string model, string user)
        {
            var run = new Run
            {
                Model = model, User = user, Time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ProjectFile = rules?.ProjectFile,
                RulesHash = RulesHashOf(rules), RequirementsHash = RequirementsHashOf(requirements), Checks = checks.ToList(),
            };
            run.Id = run.Time.Replace(":", "").Replace("-", "") + "-" + Hash(model + run.RulesHash + run.RequirementsHash).Substring(0, 4);

            // Bindings from the rules: requirement id -> check keys.
            var bound = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Bind(JsonNode req, string checkKey)
            {
                foreach (var id in Ids(req))
                {
                    if (!bound.TryGetValue(id, out var keys)) bound[id] = keys = new List<string>();
                    if (!keys.Contains(checkKey)) keys.Add(checkKey);
                }
            }
            if (rules?.Section("checks") is JsonObject cs)
                foreach (var (key, rule) in cs) Bind(rule?["requirement"], key);
            if (rules?.Section("worksets")?["rules"] is JsonArray wr)
                foreach (var rule in wr.OfType<JsonObject>()) Bind(rule["requirement"], WorksetCheckKey);

            var records = (requirements?["records"] as JsonArray ?? requirements?["requirements"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(r => r["rejected"] == null).ToList();   // a rejected record is not a requirement
            foreach (var r in records)
            {
                var id = r["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                var binding = r["test"]?["binding"]?.ToString();
                if (binding != null && binding.StartsWith("C1:checks/", StringComparison.OrdinalIgnoreCase)) Bind(JsonValue.Create(id), binding.Substring("C1:checks/".Length));
            }

            var byKey = checks.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var recordsById = records.Where(r => r["id"] != null).GroupBy(r => r["id"].ToString()).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var ids = recordsById.Keys.Concat(bound.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal);
            foreach (var id in ids)
            {
                recordsById.TryGetValue(id, out var rec);
                var res = new Result
                {
                    Requirement = id, Kind = rec?["kind"]?.ToString(),
                    Severity = (rec?["severity"] as JsonObject)?["value"]?.ToString() ?? rec?["severity"]?.ToString() ?? "info",
                    Doc = (rec?["doc"]?["key"]?.ToString() + " " + rec?["doc"]?["revision"]?.ToString()).Trim(),
                    Where = LocationOf(rec?["source"] as JsonObject),
                    Text = Short(rec?["normalized"]?.ToString() ?? rec?["source"]?["quote"]?.ToString(), 300),
                };
                if (bound.TryGetValue(id, out var keys))
                {
                    res.Binding = string.Join(", ", keys.Select(k => "C1:checks/" + k));
                    res.CheckKeys = keys.ToList();
                    var found = keys.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
                    if (found.Count == 0) { res.Status = "not-checkable"; res.Evidence = $"the bound check ({string.Join(", ", keys)}) did not run on this model"; }
                    else
                    {
                        var failing = found.Sum(c => c.Count);
                        var of = found.All(c => c.Of.HasValue) ? found.Sum(c => c.Of.Value) : (int?)null;
                        res.Count = failing; res.Of = of;
                        res.Status = failing == 0 ? "met" : of.HasValue && failing < of.Value ? "partly" : "not-met";
                        res.Evidence = string.Join("; ", found.Select(c => $"{c.Name}: {c.Count}{(c.Of.HasValue ? $" of {c.Of}" : "")} ({c.Status})"));
                    }
                }
                else
                {
                    var checkability = rec?["test"]?["checkability"]?.ToString();
                    res.Status = "not-checkable";
                    res.Evidence = checkability == "M" ? "manual: needs evidence from the team" : "no ACE check is bound to this requirement yet";
                }
                ApplyDecision(res, rec, decisions);
                run.Results.Add(res);
            }
            foreach (var g in run.Results.GroupBy(r => r.Status)) run.Summary[g.Key] = g.Count();
            foreach (var g in run.Results.Where(r => r.Status == "not-met" || r.Status == "partly").GroupBy(r => r.Severity)) run.NotMetBySeverity[g.Key] = g.Count();
            return run;
        }

        // The place a record cites, in words (the same order as the vault's locationOf).
        private static string LocationOf(JsonObject s)
        {
            if (s == null) return null;
            var parts = new List<string>();
            if (s["clause_ref"] != null) parts.Add($"clause {s["clause_ref"]}");
            if (s["page"] != null) parts.Add($"page {s["page"]}");
            if (s["table"]?["sheet"] != null) parts.Add($"sheet '{s["table"]["sheet"]}' row {s["table"]["row_first"] ?? s["table"]["row"]}".Trim());
            if (parts.Count == 0 && s["section_path"] is JsonArray path && path.Count > 0) parts.Add(string.Join(" > ", path.Select(x => x?.ToString())));
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string Short(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }

        internal static string RulesHashOf(ProjectRules rules) => rules == null ? null : Hash(string.Join("|", ProjectRules.Sections.Select(rules.HashOf)));
        internal static string RequirementsHashOf(JsonObject requirements) => requirements == null ? null : Hash(requirements["records"]?.ToJsonString() ?? "");

        /// <summary>
        /// Why a stored run no longer describes the project's current rules or requirements (null = still current). The
        /// model itself may also have changed since; that is the change tracker's job, not this one.
        /// </summary>
        internal static string OutOfDate(Run run, ProjectRules rules)
        {
            if (run == null) return null;
            if (RulesHashOf(rules) != run.RulesHash) return "the project rules changed since this run";
            var vault = VaultOf(rules);
            JsonObject req = null;
            try { if (vault != null) req = ProjectRules.ReadJson(Path.Combine(vault, "requirements.json")); } catch { }
            return RequirementsHashOf(req) != run.RequirementsHash ? "the requirements in the vault changed since this run" : null;
        }

        // An accepted deviation is shown beside the status; it stays valid while the clause text is unchanged (D19 default).
        private static void ApplyDecision(Result res, JsonObject rec, JsonObject decisions)
        {
            var list = (decisions?["decisions"] as JsonArray)?.OfType<JsonObject>().Where(d => d["kind"]?.ToString() == "deviation"
                && (d["requirements"] as JsonArray)?.Any(x => string.Equals(x?.ToString(), res.Requirement, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            var d = list?.LastOrDefault();
            if (d == null || res.Status == "met") return;
            var quoteThen = d["quotes"]?[res.Requirement]?.ToString();
            var quoteNow = rec?["source"]?["quote_sha256"]?.ToString();
            res.Decision = quoteThen != null && quoteNow != null && quoteThen != quoteNow
                ? $"{d["id"]} (deviation made against an earlier text of this clause: confirm again)"
                : $"{d["id"]} (accepted deviation{(string.IsNullOrWhiteSpace(d["note"]?.ToString()) ? "" : ": " + d["note"])})";
        }

        private static IEnumerable<string> Ids(JsonNode n) =>
            n == null ? Enumerable.Empty<string>() : n is JsonArray a ? a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)) : new[] { n.ToString() };

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        // ---- the vault side: requirements in, run records out ----------------------------------------------------

        public const string VaultFolderName = "ACE Vault";

        /// <summary>
        /// The vault folder of a project file: its "vault" setting (relative to the file), else the file's folder when that
        /// is an "ACE Vault" folder, else the "ACE Vault" folder beside it. The MCP server's vault tool uses the same rule.
        /// </summary>
        public static string VaultOf(ProjectRules rules)
        {
            if (rules?.ProjectFile == null) return null;
            var dir = Path.GetDirectoryName(rules.ProjectFile);
            var rel = ProjectRules.ReadJson(rules.ProjectFile)?["vault"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rel)) return Path.GetFullPath(Path.Combine(dir, rel));
            return string.Equals(Path.GetFileName(dir.TrimEnd('\\', '/')), VaultFolderName, StringComparison.OrdinalIgnoreCase) ? dir : Path.Combine(dir, VaultFolderName);
        }

        /// <summary>Evaluates a model check against the project's vault; null when the model has no project file.</summary>
        internal static Run ForModel(ProjectRules rules, IList<Check> checks, string model, string modelKey, string user, int? score = null)
        {
            var vault = VaultOf(rules);
            if (vault == null) return null;
            JsonObject Read(string f) { try { return ProjectRules.ReadJson(Path.Combine(vault, f)); } catch (Exception ex) { Log.Warn($"Compliance: {ex.Message}"); return null; } }
            var run = Evaluate(rules, Read("requirements.json"), Read("decisions.json"), checks, model, user);
            run.Vault = vault; run.Score = score;
            // Run records travel with the project, but only into a vault the user has allowed ACE to write to (D21).
            if (Trusted(vault))
                try
                {
                    var dir = Path.Combine(vault, "04 Audits", modelKey, "runs");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, run.Id + ".json"), ToJson(run).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { Log.Warn($"Compliance run record: {ex.Message}"); }
            return run;
        }

        internal static bool Trusted(string vault)
        {
            try
            {
                var f = Path.Combine(AceConfig.Directory, "trusted-vaults.json");
                if (!File.Exists(f) || JsonNode.Parse(File.ReadAllText(f)) is not JsonArray list) return false;
                var wanted = Path.GetFullPath(vault).TrimEnd('\\', '/');
                // One bad entry must not untrust every vault.
                return list.Any(x =>
                {
                    try { return !string.IsNullOrWhiteSpace(x?.ToString()) && string.Equals(Path.GetFullPath(x.ToString()).TrimEnd('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        public static JsonObject ToJson(Run run, int maxResults = int.MaxValue) => new JsonObject
        {
            ["run"] = new JsonObject
            {
                ["id"] = run.Id, ["model"] = run.Model, ["engine"] = run.Engine, ["time"] = run.Time, ["user"] = run.User,
                ["rulesHash"] = run.RulesHash, ["requirementsHash"] = run.RequirementsHash, ["projectFile"] = run.ProjectFile, ["vault"] = run.Vault,
                ["score"] = run.Score,
            },
            ["checks"] = new JsonArray(run.Checks.Select(c => (JsonNode)new JsonObject
            {
                ["key"] = c.Key, ["area"] = c.Area, ["name"] = c.Name, ["count"] = c.Count, ["of"] = c.Of, ["status"] = c.Status, ["detail"] = c.Detail,
            }).ToArray()),
            ["summary"] = new JsonObject(run.Summary.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value))),
            ["openBySeverity"] = new JsonObject(run.NotMetBySeverity.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value))),
            ["results"] = new JsonArray(run.Results.OrderBy(r => r.Status == "not-met" ? 0 : r.Status == "partly" ? 1 : r.Status == "met" ? 2 : 3).Take(maxResults).Select(r => (JsonNode)new JsonObject
            {
                ["requirement"] = r.Requirement, ["status"] = r.Status, ["severity"] = r.Severity, ["kind"] = r.Kind, ["binding"] = r.Binding,
                ["count"] = r.Count, ["of"] = r.Of, ["evidence"] = r.Evidence, ["decision"] = r.Decision,
                ["doc"] = r.Doc, ["at"] = r.Where, ["text"] = r.Text,
            }).ToArray()),
        };
    }
}
