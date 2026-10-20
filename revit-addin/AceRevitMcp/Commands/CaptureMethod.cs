using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using S = AceRevitMcp.Commands.CaptureLogic.ElemState;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Bridge "capture_method" { action: start | status | stop, label, categories?, parameters? }: records how a person solves
    /// something. start snapshots the elements (location, offsets, extensions, joins and which element cuts, copes, type,
    /// material, workset, listed parameters) and notes the journal file and the bytes read to its end; stop snapshots again and returns the
    /// per-element diff and the journal lines written in between. Read-only: no transaction. The running capture is kept on
    /// disk (%APPDATA%\ACE-RevitMCP\captures\active.json) so a Revit restart does not lose it.
    /// </summary>
    internal static class CaptureMethod
    {
        private static readonly string[] DefaultCategories =
            { "OST_StructuralFraming", "OST_StructuralColumns", "OST_Floors", "OST_StructuralFoundation", "OST_Walls" };

        private const int MaxJournalLines = 5000;

        internal sealed class Record
        {
            public string Id, Label, Model, ModelPath, StartedAt, Journal;
            public long JournalOffset;
            public List<string> Categories = new List<string>(), Parameters = new List<string>();
            public List<S> Elements = new List<S>();
            public CaptureLogic.Annotation Annotation;
        }

        private static Record _current;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = false };
        private static string Folder => Path.Combine(AceConfig.Directory, "captures");
        private static string ActiveFile => Path.Combine(Folder, "active.json");

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var action = (Args.Str(args, "action") ?? "status").Trim().ToLowerInvariant();
            var aceVersion = Rules.ParameterFill.AceVersion();
            JsonObject Fail(string error) => new JsonObject { ["success"] = false, ["error"] = error, ["aceVersion"] = aceVersion };
            var current = Current();
            switch (action)
            {
                case "status":
                    return new JsonObject
                    {
                        ["success"] = true, ["aceVersion"] = aceVersion, ["active"] = current != null, ["model"] = current?.Model, ["label"] = current?.Label,
                        ["startedAt"] = current?.StartedAt, ["elements"] = current?.Elements.Count, ["journal"] = current?.Journal,
                    };
                case "start":
                {
                    if (current != null) return Fail($"a capture is running ({current.Label}, on {current.Model}, since {current.StartedAt}): stop it first");
                    var doc = Args.RequireDoc(app);
                    var label = Args.Str(args, "label");
                    if (string.IsNullOrWhiteSpace(label)) throw new CommandException("Give label: a short name for what is being solved.");
                    var cats = Categories(doc, args);
                    var parameters = Args.Strings(args, "parameters").Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
                    string journal = null; long offset = 0;
                    try { journal = app.Application.RecordingJournalFilename; offset = CaptureLogic.JournalEnd(journal); }   // bytes read to EOF, not Length
                    catch { /* no journal: the diff still works */ }
                    var rec = new Record
                    {
                        Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), Label = label.Trim(), Model = doc.Title, ModelPath = doc.PathName,
                        StartedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), Journal = journal, JournalOffset = offset,
                        Categories = cats.Select(c => c.Name).ToList(), Parameters = parameters,
                        Elements = Snapshot(doc, cats.Select(c => c.Id).ToList(), parameters, out var annotation),
                        Annotation = annotation,
                    };
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(ActiveFile, JsonSerializer.Serialize(rec, Json));
                    _current = rec;
                    return new JsonObject
                    {
                        ["success"] = true, ["aceVersion"] = aceVersion, ["model"] = rec.Model, ["label"] = rec.Label, ["startedAt"] = rec.StartedAt,
                        ["journal"] = rec.Journal, ["elements"] = rec.Elements.Count,
                        ["annotation"] = CaptureLogic.AnnotationDiff(rec.Annotation, null),
                        ["categories"] = new JsonArray(rec.Categories.Select(c => (JsonNode)c).ToArray()),
                        ["parameters"] = new JsonArray(rec.Parameters.Select(p => (JsonNode)p).ToArray()),
                    };
                }
                case "stop":
                {
                    if (current == null) return Fail("no capture running");
                    var doc = Args.RequireDoc(app);
                    if (!string.Equals(doc.Title, current.Model, StringComparison.Ordinal))
                        return Fail($"the capture was started on {current.Model}, the active model is {doc.Title}: switch to that model, then stop");
                    var cats = current.Categories.Select(n => Args.ResolveCategory(doc, n)).Where(c => c != null).ToList();
                    var after = Snapshot(doc, cats.Select(c => c.Id).ToList(), current.Parameters, out var annotationNow);
                    var diffs = CaptureLogic.Diff(current.Elements, after);
                    string journalNow = null;
                    try { journalNow = app.Application.RecordingJournalFilename; } catch { }
                    var (lines, truncated) = CaptureLogic.JournalLines(JournalSince(current.Journal, current.JournalOffset, journalNow), MaxJournalLines);
                    var stoppedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                    // Keep the case's raw state beside the others, then clear the running capture.
                    string archive = null;
                    try
                    {
                        var safe = string.Concat(current.Label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
                        var path = Path.Combine(Folder, $"{current.Id}-{safe}.json");
                        File.WriteAllText(path, JsonSerializer.Serialize(new { record = current, stoppedAt, after }, Json));
                        archive = path;
                        File.Delete(ActiveFile);
                    }
                    catch (Exception ex) { Log.Warn($"Capture not archived: {ex.Message}"); }
                    _current = null;
                    return new JsonObject
                    {
                        ["success"] = true, ["aceVersion"] = aceVersion, ["model"] = current.Model, ["label"] = current.Label,
                        ["startedAt"] = current.StartedAt, ["stoppedAt"] = stoppedAt, ["archive"] = archive, ["journal"] = journalNow ?? current.Journal,
                        ["elements"] = after.Count, ["elementsBefore"] = current.Elements.Count,
                        ["categories"] = new JsonArray(current.Categories.Select(c => (JsonNode)c).ToArray()),
                        ["journalLines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()), ["journalTruncated"] = truncated,
                        ["diffs"] = diffs,
                        ["annotation"] = CaptureLogic.AnnotationDiff(current.Annotation, annotationNow),
                    };
                }
                default: throw new CommandException("action must be start, status or stop.");
            }
        }

        /// <summary>The running capture: in memory, else from disk (after a Revit restart).</summary>
        private static Record Current()
        {
            if (_current != null) return _current;
            try { if (File.Exists(ActiveFile)) _current = JsonSerializer.Deserialize<Record>(File.ReadAllText(ActiveFile), Json); }
            catch (Exception ex) { Log.Warn($"The running capture could not be read: {ex.Message}"); }
            return _current;
        }

        private static List<Category> Categories(Document doc, JsonObject args)
        {
            var names = Args.Strings(args, "categories");
            if (names.Count == 0) names = DefaultCategories.ToList();
            var list = new List<Category>();
            foreach (var n in names)
            {
                var c = Args.ResolveCategory(doc, n) ?? throw new CommandException($"categories: '{n}' is not a category name.");
                if (!list.Any(x => x.Id == c.Id)) list.Add(c);
            }
            return list;
        }

        // ---- state -----------------------------------------------------------------------------------------------

        private static double[] Mm(XYZ p) => p == null ? null : new[] { Math.Round(Lengths.Mm(p.X), 1), Math.Round(Lengths.Mm(p.Y), 1), Math.Round(Lengths.Mm(p.Z), 1) };

        private static readonly (string Name, BuiltInParameter Bip)[] OffsetParams =
        {
            ("baseOffset", BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM), ("topOffset", BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM),
            ("baseOffset", BuiltInParameter.WALL_BASE_OFFSET), ("topOffset", BuiltInParameter.WALL_TOP_OFFSET),
            ("heightOffset", BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
            ("startLevelOffset", BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION), ("endLevelOffset", BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION),
            ("zOffset", BuiltInParameter.Z_OFFSET_VALUE),
        };

        private static readonly (string Name, BuiltInParameter Bip)[] ExtensionParams =
        {
            ("startExtension", BuiltInParameter.START_EXTENSION), ("endExtension", BuiltInParameter.END_EXTENSION),
            ("startJoinCutback", BuiltInParameter.START_JOIN_CUTBACK), ("endJoinCutback", BuiltInParameter.END_JOIN_CUTBACK),
        };

        private const double M2PerFt2 = 0.09290304, M3PerFt3 = 0.028316846592;

        /// <summary>
        /// Model-wide dimension, spot dimension and tag counts, and per element how many dimensions and tags reference it (one
        /// pass over each).
        /// </summary>
        private static CaptureLogic.Annotation Annotations(Document doc, Dictionary<long, int> dims, Dictionary<long, int> tags)
        {
            var a = new CaptureLogic.Annotation();
            foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                if (d is SpotDimension) a.SpotDimensions++; else a.Dimensions++;
                try
                {
                    var seen = new HashSet<long>();
                    if (d.References != null)
                        foreach (Reference r in d.References)
                            if (r != null && seen.Add(r.ElementId.Value)) dims[r.ElementId.Value] = dims.TryGetValue(r.ElementId.Value, out var n) ? n + 1 : 1;
                }
                catch { /* a dimension without readable references */ }
            }
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                a.Tags++;
                try { foreach (var id in t.GetTaggedLocalElementIds()) tags[id.Value] = tags.TryGetValue(id.Value, out var n) ? n + 1 : 1; }
                catch { }
            }
            return a;
        }

        private static List<S> Snapshot(Document doc, List<ElementId> categories, List<string> parameters, out CaptureLogic.Annotation annotation)
        {
            var list = new List<S>();
            var dims = new Dictionary<long, int>(); var tagged = new Dictionary<long, int>();
            annotation = null;
            try { annotation = Annotations(doc, dims, tagged); } catch (Exception ex) { Log.Warn($"Capture annotation counts: {ex.Message}"); }
            if (categories.Count == 0) return list;
            var table = doc.IsWorkshared ? doc.GetWorksetTable() : null;
            foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType())
            {
                if (e is FamilyInstance nested && nested.SuperComponent != null) continue;
                var s = new S { Id = e.Id.Value, UniqueId = e.UniqueId, Category = e.Category?.Name };
                try
                {
                    var type = doc.GetElement(e.GetTypeId()) as ElementType;
                    s.Type = type?.Name ?? e.Name;
                    s.Family = e is FamilyInstance fi ? fi.Symbol?.FamilyName : type?.FamilyName;
                }
                catch { }
                try { s.Level = (doc.GetElement(e.LevelId) as Level)?.Name; } catch { }
                if (s.Level == null)
                    foreach (var bip in new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
                    {
                        var id = e.get_Parameter(bip)?.AsElementId();
                        if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is Level lv) { s.Level = lv.Name; break; }
                    }
                try
                {
                    if (e.Location is LocationPoint lp) s.Point = Mm(lp.Point);
                    else if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound) { s.Start = Mm(lc.Curve.GetEndPoint(0)); s.End = Mm(lc.Curve.GetEndPoint(1)); }
                }
                catch { }
                if (s.Point == null && s.Start == null)
                    try { var bb = e.get_BoundingBox(null); if (bb != null) { s.BoxMin = Mm(bb.Min); s.BoxMax = Mm(bb.Max); } } catch { }
                foreach (var (name, bip) in OffsetParams) Length(e, bip, name, s.Offsets);
                try
                {
                    var ar = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    if (ar != null && ar.HasValue && ar.StorageType == StorageType.Double) s.Area = Math.Round(ar.AsDouble() * M2PerFt2, 4);
                    var vo = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                    if (vo != null && vo.HasValue && vo.StorageType == StorageType.Double) s.Volume = Math.Round(vo.AsDouble() * M3PerFt3, 5);
                }
                catch { }
                s.Dimensions = dims.TryGetValue(s.Id, out var dn) ? dn : 0;
                s.Tags = tagged.TryGetValue(s.Id, out var tn) ? tn : 0;
                foreach (var (name, bip) in ExtensionParams) Length(e, bip, name, s.Extensions);
                try
                {
                    foreach (var other in JoinGeometryUtils.GetJoinedElements(doc, e))
                    {
                        var o = doc.GetElement(other);
                        bool cuts;
                        try { cuts = JoinGeometryUtils.IsCuttingElementInJoin(doc, e, o); } catch { cuts = false; }
                        s.Joins.Add(new CaptureLogic.Join { Id = other.Value, Cuts = cuts, Category = o?.Category?.Name });
                    }
                }
                catch { /* not joinable */ }
                if (e is FamilyInstance f)
                    try { s.Copes = f.GetCopingIds().Select(i => i.Value).OrderBy(i => i).ToList(); } catch { }
                try
                {
                    var cls = Coordination.StructuralMaterial.Of(e);
                    var name = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsValueString();
                    s.Material = string.IsNullOrWhiteSpace(name) ? cls : $"{name} ({cls})";
                }
                catch { }
                try { s.Workset = table?.GetWorkset(e.WorksetId)?.Name; } catch { }
                foreach (var p in parameters)
                {
                    var prm = e.LookupParameter(p) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(p);
                    if (prm == null) continue;
                    s.Params[p] = prm.StorageType == StorageType.String ? prm.AsString() : prm.AsValueString() ?? (prm.StorageType == StorageType.ElementId ? prm.AsElementId()?.Value.ToString() : null);
                }
                list.Add(s);
            }
            return list;
        }

        private static void Length(Element e, BuiltInParameter bip, string name, Dictionary<string, double> into)
        {
            if (into.ContainsKey(name)) return;
            try
            {
                var p = e.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) into[name] = Math.Round(Lengths.Mm(p.AsDouble()), 1);
            }
            catch { }
        }

        // ---- journal ---------------------------------------------------------------------------------------------

        /// <summary>The journal text written since start: the start file from its offset, plus a new journal (Revit restarted) from its beginning.</summary>
        private static IEnumerable<string> JournalSince(string startFile, long offset, string nowFile)
        {
            var lines = new List<string>();
            lines.AddRange(ReadFrom(startFile, offset));
            if (!string.IsNullOrEmpty(nowFile) && !string.Equals(nowFile, startFile, StringComparison.OrdinalIgnoreCase)) lines.AddRange(ReadFrom(nowFile, 0));
            return lines;
        }

        /// <summary>Reads a file Revit is still writing (shared stream, to EOF), from a byte offset; see CaptureLogic.JournalFrom.</summary>
        private static IEnumerable<string> ReadFrom(string file, long offset)
        {
            if (string.IsNullOrEmpty(file)) return Enumerable.Empty<string>();
            try { return CaptureLogic.JournalFrom(file, offset); }
            catch (Exception ex) { Log.Warn($"Journal not read: {ex.Message}"); return Enumerable.Empty<string>(); }
        }
    }
}
