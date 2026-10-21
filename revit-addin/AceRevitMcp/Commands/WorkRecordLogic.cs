using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using S = AceRevitMcp.Commands.CaptureLogic.ElemState;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// The plain part of the owner's work record (ribbon "Record my work", an extension of capture_method; no Revit types,
    /// tested in tools/checkset-test): what is kept per document while recording (start snapshot, touched / added /
    /// deleted ids, Revit transactions, views), the per-document diff with its caps, and the case record (JSON in the
    /// capture_method case shape, plus a first markdown). Claude completes a case later (capture_method list with case:
    /// actions by pair type, journal commands, plan comparison) with the same server code as capture_method stop.
    /// </summary>
    internal static class WorkRecordLogic
    {
        public const string RecordedBy = "owner (ribbon)";
        public const string Source = "ribbon";
        /// <summary>Elements in one document's start snapshot (the active view's first, then the structural categories).</summary>
        public const int MaxElementsPerDocument = 5000;
        /// <summary>Ids remembered as touched (added or modified) per document.</summary>
        public const int MaxTouchedPerDocument = 20000;
        /// <summary>Changed elements written into one case (all documents together).</summary>
        public const int MaxDiffsPerCase = 5000;
        public const int MaxSteps = 2000, MaxViews = 500, MaxNote = 4000;

        internal sealed class Step
        {
            public string Model, Name, Op, At;
            public int Times = 1;
        }

        internal sealed class ViewUse
        {
            public string Model, View, ViewType, At;
            public int Times = 1;
        }

        /// <summary>One document seen during the recording.</summary>
        internal sealed class DocLog
        {
            public string Key, Title, Path, FirstSeenAt, SnapshotAt;
            /// <summary>The snapshot hit MaxElementsPerDocument; the snapshot was taken after the document's first change.</summary>
            public bool SnapshotCapped, SnapshotAfterChange, TouchedCapped;
            public List<S> Before = new List<S>();
            public CaptureLogic.Annotation Annotation;
            public HashSet<long> Touched = new HashSet<long>(), Added = new HashSet<long>(), Deleted = new HashSet<long>();
            /// <summary>Set when the document closed during the recording: its state then (compared at stop).</summary>
            public bool Closed;
            public string ClosedAt;
            public List<S> After;
            public CaptureLogic.Annotation AnnotationAfter;
            public List<long> SketchOwners = new List<long>();
        }

        public static string Clip(string s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim().Substring(0, max);

        /// <summary>Adds ids up to the cap; false when some did not fit.</summary>
        public static bool AddIds(HashSet<long> set, IEnumerable<long> ids, int cap)
        {
            var all = true;
            foreach (var id in ids ?? Enumerable.Empty<long>())
            {
                if (set.Contains(id)) continue;
                if (set.Count >= cap) { all = false; continue; }
                set.Add(id);
            }
            return all;
        }

        /// <summary>A Revit transaction in order; the same name repeated in the same document counts up.</summary>
        public static void AddStep(List<Step> steps, string model, string name, string op, string at, int max = MaxSteps)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var last = steps.LastOrDefault();
            if (last != null && last.Model == model && last.Name == name && last.Op == op) { last.Times++; return; }
            if (steps.Count >= max) return;
            steps.Add(new Step { Model = model, Name = name.Trim(), Op = op, At = at });
        }

        /// <summary>A view the person opened; the same view again right after counts up.</summary>
        public static void AddView(List<ViewUse> views, string model, string view, string viewType, string at, int max = MaxViews)
        {
            if (string.IsNullOrWhiteSpace(view)) return;
            var last = views.LastOrDefault();
            if (last != null && last.Model == model && last.View == view) { last.Times++; return; }
            if (views.Count >= max) return;
            views.Add(new ViewUse { Model = model, View = view, ViewType = viewType, At = at });
        }

        private static JsonObject Change(string what, JsonNode before, JsonNode after, string note = null)
        {
            var c = new JsonObject { ["what"] = what, ["before"] = before, ["after"] = after };
            if (note != null) c["note"] = note;
            return c;
        }

        /// <summary>
        /// One document's diffs, each with "model": capture_method's per-element diff, where an element that is new to the
        /// snapshot but was not added during the recording is "modified" with its state before unknown (outside the
        /// snapshot), a deleted id never snapshotted is still listed, and sketch edits are flagged. At most budget diffs.
        /// </summary>
        public static JsonArray DocumentDiffs(DocLog d, IList<S> after, CaptureLogic.Annotation annotationAfter, IEnumerable<long> sketchOwners, int budget, out bool truncated)
        {
            var before = d.Before ?? new List<S>();
            var beforeIds = new HashSet<long>(before.Select(s => s.Id));
            var afterById = (after ?? new List<S>()).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var raw = CaptureLogic.Diff(before, after, annotations: d.Annotation != null && annotationAfter != null);
            var byId = new SortedDictionary<long, JsonObject>();
            foreach (var n in raw)
            {
                var o = (JsonObject)n.DeepClone();
                var id = (long)o["id"];
                var changes = (JsonArray)o["changes"];
                if (!beforeIds.Contains(id) && !d.Added.Contains(id) && changes.Count == 1 && changes[0]?["what"]?.ToString() == "created")
                    o["changes"] = new JsonArray(Change("modified", null, changes[0]["after"]?.DeepClone(), "not in the start snapshot (cap or category): its state before is unknown"));
                byId[id] = o;
            }
            foreach (var id in d.Deleted.Where(i => !beforeIds.Contains(i) && !afterById.ContainsKey(i) && !d.Added.Contains(i)))
                byId[id] = new JsonObject { ["id"] = id, ["category"] = null, ["type"] = null, ["changes"] = new JsonArray(Change("deleted", null, null, "not in the start snapshot")) };
            foreach (var id in (sketchOwners ?? Enumerable.Empty<long>()).Distinct())
            {
                if (!byId.TryGetValue(id, out var o))
                {
                    afterById.TryGetValue(id, out var s);
                    if (s == null) continue;   // the owner is gone (deleted): its deletion says enough
                    o = new JsonObject { ["id"] = id, ["uniqueId"] = s.UniqueId, ["category"] = s.Category, ["type"] = s.TypeName, ["changes"] = new JsonArray() };
                    byId[id] = o;
                }
                ((JsonArray)o["changes"]).Add(Change("sketch-edited", null, null));
            }
            var outArr = new JsonArray();
            truncated = false;
            foreach (var o in byId.Values)
            {
                if (outArr.Count >= Math.Max(0, budget)) { truncated = true; break; }
                o["model"] = d.Title;
                outArr.Add(o);
            }
            return outArr;
        }

        // ---- the case record ---------------------------------------------------------------------------------------

        /// <summary>As the server's safeName (lib/paramexec.js), so a case stem is the same on both sides.</summary>
        public static string SafeName(string s)
        {
            var t = Regex.Replace(s ?? "model", "[\\\\/:*?\"<>|\\x00-\\x1f]+", "_");
            t = Regex.Replace(t, "\\.{2,}", "_");
            t = Regex.Replace(t, "^[.\\s]+|[.\\s]+$", "");
            if (t.Length > 120) t = t.Substring(0, 120);
            return t.Length == 0 ? "model" : t;
        }

        /// <summary>As lib/capture.js caseStem: "2026-10-21-my-label".</summary>
        public static string CaseStem(DateTime date, string label) =>
            $"{date:yyyy-MM-dd}-{Regex.Replace(SafeName(label), "\\s+", "-").ToLowerInvariant()}";

        /// <summary>The stem, or stem-2, -3 ... when a case of that name exists (a ribbon case never overwrites one).</summary>
        public static string FreeStem(string dir, string stem)
        {
            var s = stem;
            for (var i = 2; File.Exists(Path.Combine(dir, s + ".json")) || File.Exists(Path.Combine(dir, s + ".md")); i++) s = $"{stem}-{i}";
            return s;
        }

        internal sealed class DocResult
        {
            public DocLog Log;
            public JsonArray Diffs = new JsonArray();
            public bool Truncated, Compared = true;
            public CaptureLogic.Annotation AnnotationAfter;
            public List<long> SketchOwners = new List<long>();
            public string Note;
        }

        internal sealed class CaseInput
        {
            public string Label, StartNote, ClosingNote, StartedAt, StoppedAt, AceVersion, JournalFile;
            public List<string> Categories = new List<string>(), JournalLines = new List<string>();
            public bool JournalTruncated;
            public List<DocResult> Documents = new List<DocResult>();
            public List<Step> Steps = new List<Step>();
            public List<ViewUse> Views = new List<ViewUse>();
        }

        private static JsonArray Arr<T>(IEnumerable<T> xs, Func<T, JsonNode> f) => new JsonArray((xs ?? Enumerable.Empty<T>()).Select(f).ToArray());

        /// <summary>
        /// The case: capture_method's fields (label, model, startedAt, stoppedAt, elements, categories, annotation, diffs,
        /// journalFile, archive, recordedBy, aceVersion) plus notes, documents, views, transactions, journalLines and caps.
        /// actions / journal / comparison are added by the server (capture_method list with case), analysed then true.
        /// </summary>
        public static JsonObject BuildCase(CaseInput c)
        {
            var diffs = new JsonArray();
            foreach (var d in c.Documents) foreach (var x in d.Diffs) diffs.Add(x.DeepClone());
            var first = c.Documents.FirstOrDefault();
            var caps = new JsonArray();
            if (c.Documents.Any(d => d.Log.SnapshotCapped)) caps.Add($"start snapshot capped at {MaxElementsPerDocument} elements per document ({string.Join(", ", c.Documents.Where(d => d.Log.SnapshotCapped).Select(d => d.Log.Title))}): changes to elements outside it show as modified with no state before");
            if (c.Documents.Any(d => d.Log.TouchedCapped)) caps.Add($"more than {MaxTouchedPerDocument} touched elements in a document: the rest are not listed");
            if (c.Documents.Any(d => d.Truncated)) caps.Add($"more than {MaxDiffsPerCase} changed elements: the case lists the first {MaxDiffsPerCase} by document and id");
            if (c.Steps.Count >= MaxSteps) caps.Add($"transactions: the first {MaxSteps} are listed");
            if (c.Views.Count >= MaxViews) caps.Add($"views: the first {MaxViews} are listed");
            return new JsonObject
            {
                ["label"] = c.Label,
                ["model"] = first?.Log.Title,
                ["startedAt"] = c.StartedAt,
                ["stoppedAt"] = c.StoppedAt,
                ["elements"] = c.Documents.Sum(d => d.Log.Before.Count),
                ["categories"] = Arr(c.Categories, x => (JsonNode)x),
                ["annotation"] = first == null ? null : CaptureLogic.AnnotationDiff(first.Log.Annotation, first.AnnotationAfter),
                ["notes"] = new JsonObject { ["start"] = c.StartNote, ["closing"] = c.ClosingNote },
                ["diffs"] = diffs,
                ["documents"] = Arr(c.Documents, d => new JsonObject
                {
                    ["model"] = d.Log.Title, ["path"] = d.Log.Path, ["firstSeenAt"] = d.Log.FirstSeenAt, ["snapshotAt"] = d.Log.SnapshotAt,
                    ["elements"] = d.Log.Before.Count, ["changedElements"] = d.Diffs.Count, ["touched"] = d.Log.Touched.Count,
                    ["sketchEdited"] = Arr(d.SketchOwners.Distinct().OrderBy(i => i), i => (JsonNode)i),
                    ["snapshotCapped"] = d.Log.SnapshotCapped, ["snapshotAfterFirstChange"] = d.Log.SnapshotAfterChange,
                    ["closedDuringRecording"] = d.Log.Closed, ["compared"] = d.Compared, ["note"] = d.Note,
                    ["annotation"] = CaptureLogic.AnnotationDiff(d.Log.Annotation, d.AnnotationAfter),
                }),
                ["transactions"] = Arr(c.Steps, s => new JsonObject { ["model"] = s.Model, ["name"] = s.Name, ["op"] = s.Op, ["at"] = s.At, ["times"] = s.Times }),
                ["views"] = Arr(c.Views, v => new JsonObject { ["model"] = v.Model, ["view"] = v.View, ["viewType"] = v.ViewType, ["at"] = v.At, ["times"] = v.Times }),
                ["journalFile"] = c.JournalFile,
                ["journalLines"] = Arr(c.JournalLines, l => (JsonNode)l),
                ["journalTruncated"] = c.JournalTruncated,
                ["caps"] = caps,
                ["archive"] = null,   // set when written (WorkRecorder.WriteAndEnd)
                ["recordedBy"] = RecordedBy,
                ["source"] = Source,
                ["analysed"] = false,
                ["aceVersion"] = c.AceVersion,
            };
        }

        /// <summary>The first markdown of a ribbon case (Claude rewrites it when it analyses the case).</summary>
        public static string CaseMarkdown(JsonObject c)
        {
            var sb = new StringBuilder();
            void L(string s = "") => sb.Append(s).Append('\n');
            var docs = (c["documents"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var diffs = (c["diffs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            L($"# Method case: {c["label"]}");
            L();
            L($"Recorded by the owner from the ACE ribbon (Record my work), {c["startedAt"]} to {c["stoppedAt"]}; {docs.Count} document(s): {string.Join(", ", docs.Select(d => d["model"]?.ToString()))}.");
            L($"Changed elements: {diffs.Count}. This is a record of what a person did, not a rule.");
            L();
            L("## Owner's notes");
            L();
            L($"- Task: {c["notes"]?["start"]?.ToString() ?? "(none)"}");
            L($"- Rule followed (closing note): {c["notes"]?["closing"]?.ToString() ?? "(none)"}");
            L();
            L("## Changes by document and category");
            L();
            if (diffs.Count == 0) L("- No element changed.");
            foreach (var g in diffs.GroupBy(d => $"{d["model"]} / {d["category"]?.ToString() ?? "?"}").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var kinds = g.SelectMany(d => (d["changes"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    .GroupBy(x => x["what"]?.ToString() ?? "?").OrderByDescending(k => k.Count()).ThenBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => $"{k.Key} {k.Count()}");
                L($"- {g.Key}: {g.Count()} element(s): {string.Join(", ", kinds)} (e.g. {string.Join(", ", g.Take(5).Select(d => d["id"]?.ToString()))})");
            }
            L();
            L("## Revit transactions (in order)");
            L();
            var steps = (c["transactions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            if (steps.Count == 0) L("- None recorded.");
            else L("- " + string.Join(" > ", steps.Take(200).Select(s => $"{s["name"]}{((int?)s["times"] > 1 ? $" x{s["times"]}" : "")}{(s["op"]?.ToString() is string op && op != "TransactionCommitted" ? $" ({op})" : "")}")) + (steps.Count > 200 ? " > ..." : ""));
            L();
            L("## Views used");
            L();
            var views = (c["views"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            if (views.Count == 0) L("- None recorded.");
            foreach (var v in views.Take(60)) L($"- {v["at"]} {v["model"]}: {v["view"]} ({v["viewType"]})");
            if (views.Count > 60) L($"- ... {views.Count - 60} more (JSON record)");
            var caps = (c["caps"] as JsonArray)?.Select(x => x?.ToString()).ToList() ?? new List<string>();
            var notes = docs.Where(d => d["note"] != null).Select(d => $"{d["model"]}: {d["note"]}").ToList();
            if (caps.Count + notes.Count > 0)
            {
                L();
                L("## Limits");
                L();
                foreach (var x in caps.Concat(notes)) L($"- {x}");
            }
            L();
            L("## Next step");
            L();
            L("Ask Claude to analyse this case (capture_method list, then case: this file): it groups the changes into actions, reads the Revit commands from the journal and proposes rules. Nothing becomes a rule until the owner confirms it.");
            return sb.ToString();
        }
    }
}
