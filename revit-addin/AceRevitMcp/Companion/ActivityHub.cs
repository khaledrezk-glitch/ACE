using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Companion
{
    internal sealed class ActivityItem
    {
        public DateTime Time;
        public string Command;
        public string Title;
        public string Detail;
        public bool Ok;
        public long Ms;
    }

    internal enum PendingState { Waiting, AppliedByPanel, AppliedByClaude, Rejected, Failed }

    internal sealed class PendingChange
    {
        public string Hash;
        public string Kind;            // execute_code | set_parameters
        public JsonObject Args;        // the previewed request, without dry_run
        public string Title;
        public string Summary;
        public DateTime PreviewedAt;
        public PendingState State = PendingState.Waiting;
        public DateTime? DecidedAt;
        public string Outcome;
        /// <summary>Preview pictures of the change (plan, 3D), when Claude asked for them.</summary>
        public System.Collections.Generic.List<byte[]> Images = new System.Collections.Generic.List<byte[]>();
    }

    internal sealed class ElementRef
    {
        public long Id;
        public string Label;
    }

    internal sealed class RevitContext
    {
        public string Document;
        public string View;
        public string ViewType;
        public List<long> SelectedIds = new List<long>();
        public string SelectionSummary;
    }

    /// <summary>
    /// State shared by the bridge (commands from Claude) and the ACE Companion panel.
    /// All mutations happen on Revit's main thread (ExternalEvent handler or UI events).
    /// </summary>
    internal static class ActivityHub
    {
        public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(60);

        public static event Action Changed;

        public static List<ActivityItem> Activity { get; } = new List<ActivityItem>();
        public static List<PendingChange> Pending { get; } = new List<PendingChange>();
        public static List<ElementRef> Results { get; private set; } = new List<ElementRef>();
        public static string ResultsTitle { get; private set; } = "";
        public static RevitContext Context { get; private set; } = new RevitContext();

        public static IEnumerable<PendingChange> Waiting =>
            Pending.Where(p => p.State == PendingState.Waiting && DateTime.Now - p.PreviewedAt < PendingLifetime);

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { /* UI problems must never break a command */ }
        }

        // ---- Fingerprints: the same request from Claude must map to the same pending change ----------

        public static string Fingerprint(string kind, JsonObject args)
        {
            string part(string key) => args[key]?.ToJsonString() ?? "null";
            var material = kind == "set_parameters"
                ? $"{kind}\n{part("changes")}"
                : $"{kind}\n{part("code")}\n{(args["mode"]?.ToString() ?? "auto")}\n{part("inputs")}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        }

        // ---- Activity feed --------------------------------------------------------------------------

        public static void RecordCommand(string command, JsonObject args, JsonNode result, Exception error, long ms)
        {
            if (command is "ping") return; // too chatty for the feed
            var item = new ActivityItem { Time = DateTime.Now, Command = command, Ms = ms, Ok = error == null };
            (item.Title, item.Detail) = Describe(command, args, result, error);
            if (result is JsonObject ro && ro["success"] is JsonValue sv && sv.TryGetValue<bool>(out var success) && !success) item.Ok = false;
            Activity.Insert(0, item);
            if (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);

            if (error == null && result != null && command is not ("select_elements" or "export_view_image" or "api_lookup"))
            {
                var found = ExtractElements(result);
                if (found.Count > 0)
                {
                    Results = found;
                    ResultsTitle = $"{item.Title} · {item.Time:HH:mm}";
                }
            }
            Raise();
        }

        private static (string, string) Describe(string command, JsonObject args, JsonNode result, Exception error)
        {
            var friendly = command switch
            {
                "get_document_info" => "Read model overview",
                "get_selection" => "Read your selection",
                "query_elements" => "Searched elements",
                "get_element_details" => "Read element details",
                "describe_category" => $"Explored parameters of {args?["category"]}",
                "list_types" => "Listed families & types",
                "list_views" => "Listed views",
                "export_view_image" => "Looked at a view",
                "set_parameters" => IsDry(args) ? "Previewed parameter changes" : "Changed parameters",
                "select_elements" => "Selected elements",
                "backup_model" => "Backed up the model",
                "undo_last_claude_change" => "Undid the last change",
                "api_lookup" => $"Looked up Revit API: {args?["query"]}",
                "execute_code" => (args?["mode"]?.ToString()) == "readonly"
                    ? "Analysed the model (read-only)"
                    : IsDry(args) ? $"Previewed: {Name(args)}" : $"Applied: {Name(args)}",
                _ => command,
            };
            if (error != null) return (friendly, error.Message);

            var detail = "";
            if (result is JsonObject r)
            {
                var change = r["wouldChange"] as JsonObject ?? r["changed"] as JsonObject;
                if (change != null) detail = $"added {change["added"]}, modified {change["modified"]}, deleted {change["deleted"]}";
                else if (r["total"] != null) detail = $"{r["total"]} found";
                else if (r["applied"] != null) detail = $"{r["applied"]} value(s), {r["failed"]} failed";
                if (r["error"] != null) detail = r["error"].ToString();
                if (r["errors"] is JsonArray errs && errs.Count > 0) detail = "compile error: " + errs[0];
                if (r["scriptMs"] != null && string.IsNullOrEmpty(detail)) detail = $"{r["scriptMs"]} ms";
            }
            return (friendly, detail);
        }

        private static bool IsDry(JsonObject args) => args?["dry_run"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        private static string Name(JsonObject args) => (args?["transaction_name"]?.ToString() ?? "script").Replace("Claude: ", "");

        /// <summary>Finds element references in any result: objects with an "id" plus a label, and "...Ids" arrays.</summary>
        private static List<ElementRef> ExtractElements(JsonNode node)
        {
            var list = new List<ElementRef>();
            var seen = new HashSet<long>();
            void Walk(JsonNode n, string key, int depth)
            {
                if (n == null || depth > 8 || list.Count >= 500) return;
                switch (n)
                {
                    case JsonObject o:
                        if (o["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && id > 0 &&
                            (o["name"] != null || o["category"] != null || o["number"] != null || o["mark"] != null || o["family"] != null))
                        {
                            if (seen.Add(id))
                            {
                                var parts = new[] { o["category"], o["number"] ?? o["mark"], o["name"] ?? o["family"], o["level"] }
                                    .Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
                                list.Add(new ElementRef { Id = id, Label = string.Join(" · ", parts) });
                            }
                        }
                        foreach (var kv in o) Walk(kv.Value, kv.Key, depth + 1);
                        break;
                    case JsonArray a:
                        var idList = key != null && key.EndsWith("Ids", StringComparison.OrdinalIgnoreCase);
                        foreach (var x in a)
                        {
                            if (idList && x is JsonValue v && v.TryGetValue<long>(out var eid) && eid > 0)
                            {
                                if (seen.Add(eid)) list.Add(new ElementRef { Id = eid, Label = $"{key} · {eid}" });
                            }
                            else Walk(x, key, depth + 1);
                        }
                        break;
                }
            }
            Walk(node, null, 0);
            return list;
        }

        // ---- Approvals -------------------------------------------------------------------------------

        public static void AddPending(string kind, JsonObject args, JsonObject result)
        {
            var clean = (JsonObject)args.DeepClone();
            clean.Remove("dry_run");
            clean.Remove("_fromPanel");
            clean.Remove("preview_image");
            var hash = Fingerprint(kind, clean);
            Pending.RemoveAll(p => p.Hash == hash || DateTime.Now - p.PreviewedAt > PendingLifetime);

            string summary;
            if (kind == "set_parameters")
                summary = $"{result?["applied"]} parameter value(s) would change";
            else
            {
                var c = result?["wouldChange"] as JsonObject;
                var cats = c?["modifiedByCategory"] as JsonObject ?? c?["addedByCategory"] as JsonObject;
                summary = c == null ? "Preview succeeded"
                    : $"Would add {c["added"]}, modify {c["modified"]}, delete {c["deleted"]}" +
                      (cats != null && cats.Count > 0 ? " (" + string.Join(", ", cats.Take(4).Select(kv => $"{kv.Key} {kv.Value}")) + ")" : "");
                if (result?["revitWarnings"] is JsonArray w && w.Count > 0) summary += $" · {w.Count} Revit warning(s)";
            }

            var images = new System.Collections.Generic.List<byte[]>();
            if (result?["previewImages"] is JsonArray pics)
                foreach (var pic in pics)
                    try { if (pic?["base64"]?.ToString() is string b64) images.Add(Convert.FromBase64String(b64)); } catch { }
            Pending.Insert(0, new PendingChange
            {
                Images = images,
                Hash = hash,
                Kind = kind,
                Args = clean,
                Title = kind == "set_parameters" ? "Set parameter values" : Name(clean),
                Summary = summary,
                PreviewedAt = DateTime.Now,
            });
            if (Pending.Count > 20) Pending.RemoveAt(Pending.Count - 1);
            Raise();
        }

        /// <summary>Decision the user made in the panel for this exact change, if any (consumed once).</summary>
        public static PendingChange DecisionFor(string hash)
        {
            var p = Pending.FirstOrDefault(x => x.Hash == hash && x.State != PendingState.Waiting && x.DecidedAt.HasValue &&
                                                DateTime.Now - x.DecidedAt.Value < PendingLifetime &&
                                                (x.State == PendingState.AppliedByPanel || x.State == PendingState.Rejected));
            return p;
        }

        public static void MarkAppliedByClaude(string hash)
        {
            foreach (var p in Pending.Where(p => p.Hash == hash && p.State == PendingState.Waiting))
            {
                p.State = PendingState.AppliedByClaude;
                p.DecidedAt = DateTime.Now;
            }
            Raise();
        }

        public static void Decide(PendingChange p, PendingState state, string outcome)
        {
            p.State = state;
            p.DecidedAt = DateTime.Now;
            p.Outcome = outcome;
            Raise();
        }

        // ---- Context ---------------------------------------------------------------------------------

        public static void UpdateContext(RevitContext context)
        {
            Context = context;
            Raise();
        }
    }
}
