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
        /// <summary>Where it was previewed: the model, and (for code) the active view, so it is never applied elsewhere.</summary>
        public string Document;
        public string DocumentTitle;
        public long ViewId;
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

        /// <summary>A model's identity for "same model" checks: its file path, or its title before the first save.</summary>
        public static string DocKey(Autodesk.Revit.DB.Document doc) =>
            doc == null ? "" : string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

        /// <summary>
        /// Why this change must not be applied here, or null: it was previewed in another model, or (code, which may
        /// work on the active view) in another view. The preview showed that model and view, so it applies only there.
        /// </summary>
        public static string WrongPlace(string hash, Autodesk.Revit.DB.Document doc, long viewId)
        {
            var p = Pending.FirstOrDefault(x => x.Hash == hash && x.Document != null);
            if (p == null) return null;
            if (!string.Equals(p.Document, DocKey(doc), StringComparison.OrdinalIgnoreCase))
                return $"This change was previewed in the model '{p.DocumentTitle}', but the active model is now '{doc?.Title}'. Nothing was changed. Preview it again in the model it is meant for.";
            if (p.ViewId != 0 && viewId != p.ViewId)
                return "This change was previewed with a different active view, and the code may work on the active view. Nothing was changed. Preview it again in the view it is meant for.";
            return null;
        }

        public static void AddPending(string kind, JsonObject args, JsonObject result, Autodesk.Revit.DB.Document doc = null, long viewId = 0)
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
                Document = doc == null ? null : DocKey(doc),
                DocumentTitle = doc?.Title,
                ViewId = viewId,
            });
            if (Pending.Count > 20) Pending.RemoveAt(Pending.Count - 1);
            Raise();
        }

        /// <summary>Decision the user made in the panel for this exact change, if any (consumed once).</summary>
        /// <summary>The user's earlier decision about one exact change, as the reply to give instead of applying it.</summary>
        internal sealed class Earlier
        {
            public bool Rejected;
            public string Note;
            public string Outcome;
        }

        /// <summary>
        /// Whether this exact change was already applied (in the panel or by Claude) or cancelled in the panel; null when
        /// it may be applied. Every applying command asks this first, so no change is applied twice.
        /// </summary>
        public static Earlier Decided(string hash, string what)
        {
            var d = DecisionFor(hash);
            if (d == null) return null;
            var at = $"{d.DecidedAt:HH:mm}";
            return d.State switch
            {
                PendingState.AppliedByPanel => new Earlier { Outcome = d.Outcome, Note = $"The user already applied {what} with the Apply button in the ACE Companion panel in Revit at {at}. Do NOT apply it again. Verify the result with a read-only query and report it." },
                PendingState.AppliedByClaude => new Earlier { Note = $"This exact change ({what}) was already applied at {at} (one undo step). It is not applied twice. Verify the result with a read-only query; to repeat it on purpose, preview it again first." },
                _ => new Earlier { Rejected = true, Note = $"The user cancelled {what} in the ACE Companion panel in Revit at {at}. Do not apply it. Ask what they would like instead." },
            };
        }

        public static PendingChange DecisionFor(string hash)
        {
            var p = Pending.FirstOrDefault(x => x.Hash == hash && x.State != PendingState.Waiting && x.DecidedAt.HasValue &&
                                                DateTime.Now - x.DecidedAt.Value < PendingLifetime &&
                                                (x.State == PendingState.AppliedByPanel || x.State == PendingState.AppliedByClaude || x.State == PendingState.Rejected));
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

        /// <summary>Bridge command "pending_changes": what the Companion panel shows right now (so Claude never guesses).</summary>
        public static JsonNode PendingCommand(Autodesk.Revit.UI.UIApplication app, JsonObject args)
        {
            JsonObject Row(PendingChange p, int n) => new JsonObject
            {
                ["card"] = n,
                ["title"] = p.Title,
                ["summary"] = p.Summary,
                ["state"] = p.State.ToString(),
                ["previewedMinutesAgo"] = Math.Round((DateTime.Now - p.PreviewedAt).TotalMinutes, 1),
                ["expiresInMinutes"] = Math.Max(0, Math.Round((PendingLifetime - (DateTime.Now - p.PreviewedAt)).TotalMinutes)),
                ["inputs"] = p.Args?["inputs"]?.DeepClone(),
                ["pictures"] = p.Images.Count,
                ["outcome"] = p.Outcome,
            };
            var waiting = Waiting.ToList();
            var decided = Pending.Where(p => p.State != PendingState.Waiting && p.DecidedAt.HasValue).Take(10).ToList();
            return new JsonObject
            {
                ["waiting"] = new JsonArray(waiting.Select((p, i) => (JsonNode)Row(p, i + 1)).ToArray()),
                ["recentlyDecided"] = new JsonArray(decided.Select((p, i) => (JsonNode)Row(p, 0)).ToArray()),
                ["note"] = waiting.Count == 0
                    ? "No cards are waiting in ACE > Companion > Approvals."
                    : $"{waiting.Count} card(s) wait in ACE > Companion > Approvals, newest first. Each one applies independently (one undo step each).",
            };
        }

        // ---- Context ---------------------------------------------------------------------------------

        public static void UpdateContext(RevitContext context)
        {
            Context = context;
            Raise();
        }
    }
}
