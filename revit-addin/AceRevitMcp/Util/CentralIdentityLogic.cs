using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Util
{
    /// <summary>One central model's worksharing identity as ACE saw it: its central path and the GUIDs Revit gives it.</summary>
    internal sealed class CentralId
    {
        public string Path, Model;
        /// <summary>Document.WorksharingCentralGUID ("D" format); null when Revit gives none (Guid.Empty).</summary>
        public string CentralGuid;
        /// <summary>BasicFileInfo.LatestCentralEpisodeGUID of the model's file; null when none.</summary>
        public string EpisodeGuid;
        /// <summary>When ACE last saw it (yyyy-MM-dd HH:mm), for a stored record.</summary>
        public string Seen;
    }

    /// <summary>
    /// Two centrals sharing one worksharing identity (#38): a central that is a plain file copy of another keeps its identity,
    /// and after one is saved every edit to the other fails with "deleted in the Central Model". Pure rules (no Revit types):
    /// which centrals clash, the warning with the fix, and the per-machine record (%APPDATA%\ACE-RevitMCP\central-identities.json)
    /// of central path to identity. Two different central paths clash when they share a central GUID or an episode GUID
    /// (never true of two independently created centrals). The Revit side is <see cref="CentralIdentity"/>.
    /// </summary>
    internal static class CentralIdentityLogic
    {
        public const string FileName = "central-identities.json";
        public const int MaxRecords = 500;

        /// <summary>A path compared case-insensitively, with either slash, without a trailing one.</summary>
        public static string NormPath(string p) => (p ?? "").Trim().Trim('"').Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        /// <summary>A GUID in "D" format, or null for none, Guid.Empty or text that is no GUID.</summary>
        public static string Guid(string g) => System.Guid.TryParse((g ?? "").Trim(), out var x) && x != System.Guid.Empty ? x.ToString("D") : null;

        /// <summary>What two centrals share ("central GUID" or "episode GUID"), or null: the same path, or nothing in common.</summary>
        public static string SharedBy(CentralId a, CentralId b)
        {
            if (a == null || b == null || string.IsNullOrWhiteSpace(a.Path) || string.IsNullOrWhiteSpace(b.Path) || NormPath(a.Path) == NormPath(b.Path)) return null;
            var ac = Guid(a.CentralGuid); var bc = Guid(b.CentralGuid);
            if (ac != null && ac == bc) return "central GUID " + ac;
            var ae = Guid(a.EpisodeGuid); var be = Guid(b.EpisodeGuid);
            if (ae != null && ae == be) return "episode GUID " + ae;
            return null;
        }

        /// <summary>
        /// The centrals that share this one's identity: the other open models first ("open in this Revit"), then the stored
        /// records ("seen on this machine on ..."), one per central path.
        /// </summary>
        public static List<(CentralId Other, string Shared, string Where)> Clashes(CentralId me, IEnumerable<CentralId> open, IEnumerable<CentralId> stored)
        {
            var hits = new List<(CentralId, string, string)>();
            var done = new HashSet<string>();
            void Look(IEnumerable<CentralId> list, Func<CentralId, string> where)
            {
                foreach (var o in list ?? Enumerable.Empty<CentralId>())
                {
                    var shared = SharedBy(me, o);
                    if (shared == null || !done.Add(NormPath(o.Path))) continue;
                    hits.Add((o, shared, where(o)));
                }
            }
            Look(open, o => "open in this Revit");
            Look(stored, o => "seen on this machine" + (string.IsNullOrWhiteSpace(o.Seen) ? "" : " on " + o.Seen));
            return hits;
        }

        /// <summary>The warning: both paths, what they share, why it breaks, and the fix with ACE's tools.</summary>
        public static string Warning(CentralId me, CentralId other, string shared, string where) =>
            $"Two centrals share one worksharing identity ({shared}): {me.Path} and {other.Path} ({where}). One is a plain file copy of the other: "
            + "after one is saved, every edit to the other fails with \"deleted in the Central Model\". Fix the copy (the central that should be separate, "
            + "usually the newer file), with the user's OK: 1. close it and any local of it; 2. open_model { path: <the copy>, detach: \"preserve_worksets\" }; "
            + "3. save_model { save_as: { path: <the copy's own path>, as_central: true, overwrite: true } } (allow_risky after the user agreed): a new identity, "
            + "worksets kept; 4. everyone makes a new local from it. If both paths are one file (a mapped drive and its UNC path), ignore this.";

        /// <summary>The stored records ({ "records": [{ path, model, centralGuid, episodeGuid, seen }] }); empty when unreadable.</summary>
        public static List<CentralId> Parse(string json)
        {
            var list = new List<CentralId>();
            JsonNode root;
            try { root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json); } catch { return list; }
            foreach (var r in (root?["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var p = r["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(p)) continue;
                list.Add(new CentralId { Path = p, Model = r["model"]?.ToString(), CentralGuid = Guid(r["centralGuid"]?.ToString()), EpisodeGuid = Guid(r["episodeGuid"]?.ToString()), Seen = r["seen"]?.ToString() });
            }
            return list;
        }

        public static string Serialize(IEnumerable<CentralId> records) => new JsonObject
        {
            ["about"] = "ACE: the worksharing identity of each central model seen on this machine (#38). Safe to delete.",
            ["records"] = new JsonArray((records ?? Enumerable.Empty<CentralId>()).Select(r => (JsonNode)new JsonObject
            {
                ["path"] = r.Path, ["model"] = r.Model, ["centralGuid"] = r.CentralGuid, ["episodeGuid"] = r.EpisodeGuid, ["seen"] = r.Seen,
            }).ToArray()),
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        /// <summary>
        /// The records with these centrals as seen now (one per path: what is seen now replaces the stored record, so a central
        /// fixed by Save As central gets its new identity), newest first, at most <see cref="MaxRecords"/>.
        /// </summary>
        public static List<CentralId> Remember(IEnumerable<CentralId> stored, IEnumerable<CentralId> seenNow, DateTime now)
        {
            var stamp = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var byPath = new Dictionary<string, CentralId>();
            var order = new List<string>();
            foreach (var n in seenNow ?? Enumerable.Empty<CentralId>())
            {
                if (n == null || string.IsNullOrWhiteSpace(n.Path)) continue;
                var k = NormPath(n.Path);
                if (byPath.ContainsKey(k)) continue;
                byPath[k] = new CentralId { Path = n.Path, Model = n.Model, CentralGuid = Guid(n.CentralGuid), EpisodeGuid = Guid(n.EpisodeGuid), Seen = stamp };
                order.Add(k);
            }
            foreach (var s in stored ?? Enumerable.Empty<CentralId>())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Path)) continue;
                var k = NormPath(s.Path);
                if (byPath.ContainsKey(k)) continue;
                byPath[k] = s;
                order.Add(k);
            }
            return order.Take(MaxRecords).Select(k => byPath[k]).ToList();
        }
    }
}
