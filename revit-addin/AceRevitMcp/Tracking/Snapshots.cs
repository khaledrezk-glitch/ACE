using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Tracking
{
    /// <summary>One element as it was when the snapshot was taken.</summary>
    internal sealed class ElementRecord
    {
        public string U { get; set; }           // UniqueId (stable across sessions)
        public long Id { get; set; }
        public string Cat { get; set; }
        public string Type { get; set; }         // family : type (or class name)
        public string Lvl { get; set; }
        public string Loc { get; set; }          // point + rotation, curve end points, or box centre (mm)
        public string P { get; set; }            // fingerprint of all instance parameter values
        public Dictionary<string, string> K { get; set; }   // key values shown in reports (Mark, Comments, Workset, ...)
    }

    internal sealed class Snapshot
    {
        public string Model { get; set; }
        public string Path { get; set; }
        public string Discipline { get; set; }
        public DateTime Time { get; set; }
        public string User { get; set; }
        public string Label { get; set; }
        /// <summary>The loaded version (version GUID and number of saves): an unchanged link reuses its snapshot.</summary>
        public string Version { get; set; }
        public List<ElementRecord> Elements { get; set; } = new List<ElementRecord>();
    }

    /// <summary>
    /// Snapshots of a model's elements, stored compressed per model in %APPDATA%\ACE-RevitMCP\snapshots, so any
    /// two moments can be compared: added, deleted, moved, retyped and changed elements, for the model and its links.
    /// </summary>
    internal static class Snapshots
    {
        private const int KeepPerModel = 40;
        public static string Root => System.IO.Path.Combine(AceConfig.Directory, "snapshots");

        public static string ModelKey(Document doc)
        {
            var id = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName.ToLowerInvariant();
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(id))).Substring(0, 8);
            var safe = string.Concat(doc.Title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return $"{safe}-{hash}";
        }

        // ---- capture --------------------------------------------------------------------------------

        private static readonly HashSet<BuiltInParameter> Volatile = new HashSet<BuiltInParameter>
        {
            BuiltInParameter.EDITED_BY, BuiltInParameter.ELEM_PARTITION_PARAM,
        };

        public static Snapshot Capture(Document doc, string user, string label = null)
        {
            var snap = new Snapshot
            {
                Model = doc.Title, Path = doc.PathName, Time = DateTime.Now, User = user, Label = label,
                Discipline = Commands.BriefCommands.Discipline(doc),
                Version = doc.IsModified ? null : VersionOf(doc),   // only a saved state can stand for that version later
            };
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToDictionary(l => l.Id.Value, l => l.Name);
            var names = new Names(doc);
            // Quick filter first: elements owned by a view (annotation, detail items) never reach the slower checks below.
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ElementOwnerViewFilter(ElementId.InvalidElementId))
                .Where(e => e.Category != null && !e.ViewSpecific
                            && (e.Category.CategoryType == CategoryType.Model || e is Level || e is Grid)
                            && !(e is FamilyInstance fi && fi.SuperComponent != null));
            foreach (var e in elements)
            {
                try
                {
                    var r = new ElementRecord
                    {
                        U = e.UniqueId, Id = e.Id.Value, Cat = e.Category.Name,
                        Type = names.Type(e),
                        Lvl = e.LevelId != null && levels.TryGetValue(e.LevelId.Value, out var ln) ? ln : null,
                        Loc = Location(e),
                        P = Fingerprint(e),
                        K = KeyValues(names, e),
                    };
                    snap.Elements.Add(r);
                }
                catch { /* one odd element never stops the snapshot */ }
            }
            return snap;
        }

        internal static string TypeName(Document doc, Element e)
        {
            if (e is FamilyInstance fi) return $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}";
            var t = doc.GetElement(e.GetTypeId());
            return t != null ? t.Name : e.GetType().Name;
        }

        private static string R(double feet) => Math.Round(Lengths.Mm(feet)).ToString(CultureInfo.InvariantCulture);

        internal static string Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    return $"{R(lp.Point.X)},{R(lp.Point.Y)},{R(lp.Point.Z)}@{Math.Round(RevitJson.Safe(() => lp.Rotation) * 180 / Math.PI, 1).ToString(CultureInfo.InvariantCulture)}";
                case LocationCurve lc:
                    var a = lc.Curve.GetEndPoint(0); var b = lc.Curve.GetEndPoint(1);
                    return $"{R(a.X)},{R(a.Y)},{R(a.Z)};{R(b.X)},{R(b.Y)},{R(b.Z)}";
            }
            if (e is Grid g) { var a = g.Curve.GetEndPoint(0); var b = g.Curve.GetEndPoint(1); return $"{R(a.X)},{R(a.Y)};{R(b.X)},{R(b.Y)}"; }
            if (e is Level l) return $"z={R(l.ProjectElevation)}";
            var bb = e.get_BoundingBox(null);
            if (bb == null) return null;
            // Box centre and size to the nearest 10 mm: stable, but catches real moves and reshapes.
            string T(double f) => (Math.Round(Lengths.Mm(f) / 10) * 10).ToString(CultureInfo.InvariantCulture);
            var c = (bb.Min + bb.Max) / 2; var s = bb.Max - bb.Min;
            return $"box {T(c.X)},{T(c.Y)},{T(c.Z)} size {T(s.X)},{T(s.Y)},{T(s.Z)}";
        }

        private static string Fingerprint(Element e)
        {
            var sb = new StringBuilder();
            foreach (Parameter p in e.Parameters)
            {
                if (p.Definition == null || !p.HasValue) continue;
                if (p.Definition is InternalDefinition d && Volatile.Contains(d.BuiltInParameter)) continue;
                sb.Append(p.Definition.Name).Append('=');
                switch (p.StorageType)
                {
                    case StorageType.String: sb.Append(p.AsString()); break;
                    case StorageType.Integer: sb.Append(p.AsInteger()); break;
                    case StorageType.Double: sb.Append(Math.Round(p.AsDouble(), 6).ToString(CultureInfo.InvariantCulture)); break;
                    case StorageType.ElementId: sb.Append(p.AsElementId()?.Value); break;
                }
                sb.Append('|');
            }
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 12);
        }

        /// <summary>Type, workset and phase names looked up once per capture, not once per element.</summary>
        private sealed class Names
        {
            private readonly Document _doc;
            private readonly Dictionary<long, string> _types = new Dictionary<long, string>();
            private readonly Dictionary<int, string> _worksets = new Dictionary<int, string>();
            private readonly Dictionary<long, string> _phases = new Dictionary<long, string>();
            public Names(Document doc) { _doc = doc; }
            public Document Doc => _doc;

            public string Type(Element e)
            {
                var id = e.GetTypeId();
                if (id == null || id == ElementId.InvalidElementId) return TypeName(_doc, e);
                if (!_types.TryGetValue(id.Value, out var name)) _types[id.Value] = name = TypeName(_doc, e);
                return name;
            }

            public string Workset(WorksetId id)
            {
                if (!_doc.IsWorkshared || id == null) return null;
                if (!_worksets.TryGetValue(id.IntegerValue, out var name))
                    _worksets[id.IntegerValue] = name = RevitJson.Safe(() => _doc.GetWorksetTable().GetWorkset(id)?.Name);
                return name;
            }

            public string Phase(ElementId id)
            {
                if (id == null || id == ElementId.InvalidElementId) return null;
                if (!_phases.TryGetValue(id.Value, out var name)) _phases[id.Value] = name = (_doc.GetElement(id) as Phase)?.Name;
                return name;
            }
        }

        private static Dictionary<string, string> KeyValues(Names names, Element e)
        {
            var k = new Dictionary<string, string>();
            void Add(string name, string value) { if (!string.IsNullOrWhiteSpace(value)) k[name] = value; }
            Add("Mark", e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString());
            Add("Comments", e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString());
            if (e is Autodesk.Revit.DB.Architecture.Room room) { Add("Number", room.Number); Add("Name", room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()); }
            if (e is Level || e is Grid) Add("Name", e.Name);
            try { Add("Workset", names.Workset(e.WorksetId)); } catch { }
            Add("Phase", names.Phase(e.CreatedPhaseId));
            Add("Demolished", names.Phase(e.DemolishedPhaseId));
            if (e.DesignOption != null) Add("Option", e.DesignOption.Name);
            return k.Count == 0 ? null : k;
        }

        // ---- storage ---------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        /// <summary>
        /// Saves a snapshot and returns its file. Serialising, compressing and writing happen on a background thread
        /// (a snapshot holds only plain data), so Revit is free again as soon as the capture is done. The file appears
        /// under its final name only when complete.
        /// </summary>
        public static string Save(Document doc, Snapshot snap)
        {
            var dir = Folder(doc);
            var file = System.IO.Path.Combine(dir, $"{snap.Time:yyyyMMdd-HHmmss}.json.gz");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var temp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
                    using (var fs = File.Create(temp))
                    using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                        JsonSerializer.Serialize(gz, snap, Json);
                    File.Move(temp, file, true);
                    foreach (var extra in List(dir).Skip(KeepPerModel)) try { File.Delete(extra.File); } catch { }
                }
                catch (Exception ex) { Log.Warn($"Saving snapshot {file}: {ex.Message}"); }
            });
            return file;
        }

        /// <summary>
        /// The latest snapshot of a linked model when the loaded link is still exactly that version (same version GUID and
        /// number of saves); null when it may have changed or cannot be told.
        /// </summary>
        public static Snapshot UnchangedSince(Document link)
        {
            try
            {
                var version = VersionOf(link);
                if (version == null) return null;
                var latest = List(link).FirstOrDefault();
                if (latest.File == null) return null;
                var snap = Load(latest.File);
                return snap?.Version == version ? snap : null;
            }
            catch { return null; }
        }

        /// <summary>The loaded version of a model (its version GUID and number of saves); changes on every save and reload.</summary>
        internal static string VersionOf(Document doc)
        {
            try { var v = Document.GetDocumentVersion(doc); return v == null ? null : $"{v.VersionGUID}:{v.NumberOfSaves}"; }
            catch { return null; }
        }

        private static string Folder(Document doc) => System.IO.Path.Combine(Root, ModelKey(doc));

        public static Snapshot Load(string file)
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Snapshot>(gz, Json);
        }

        /// <summary>Snapshots of this model, newest first.</summary>
        public static List<(string File, DateTime Time)> List(Document doc) => List(Folder(doc));

        private static List<(string File, DateTime Time)> List(string dir)
        {
            if (!Directory.Exists(dir)) return new List<(string, DateTime)>();
            return Directory.GetFiles(dir, "*.json.gz")
                .Select(f => (f, DateTime.TryParseExact(System.IO.Path.GetFileName(f).Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : File.GetLastWriteTime(f)))
                .OrderByDescending(x => x.Item2).ToList();
        }

        /// <summary>
        /// The baseline for "since": "last" (newest), "today", "yesterday", "week", "month", a weekday ("monday": the
        /// latest one), "3 days", a date (yyyy-MM-dd) or a snapshot file name. Anything else is an error, never a silent
        /// fallback to the newest snapshot (which would report "no changes").
        /// </summary>
        public static (string File, DateTime Time)? Baseline(Document doc, string since)
        {
            var all = List(doc);
            if (all.Count == 0) return null;
            since = (since ?? "last").Trim().ToLowerInvariant();
            if (since == "last") return all[0];
            var byName = all.FirstOrDefault(a => System.IO.Path.GetFileName(a.File).StartsWith(since, StringComparison.OrdinalIgnoreCase));
            if (byName.File != null) return byName;
            var before = SinceTime(since) ?? throw new Bridge.CommandException(
                $"'since' was not understood: '{since}'. Use last, today, yesterday, week, month, a weekday (monday), '3 days' or a date (yyyy-MM-dd).");
            // The latest snapshot taken at or before that moment, else the oldest one we have.
            var at = all.FirstOrDefault(a => a.Time <= before);
            return at.File != null ? at : all.Last();
        }

        /// <summary>The moment a "since" word stands for, or null when it is not understood.</summary>
        internal static DateTime? SinceTime(string since, DateTime? now = null)
        {
            var today = (now ?? DateTime.Now).Date;
            switch (since)
            {
                case "today": return today;
                case "yesterday": return today.AddDays(-1);
                case "week": case "last week": return today.AddDays(-7);
                case "month": case "last month": return today.AddMonths(-1);
            }
            var words = since.Replace("last ", "").Replace(" ago", "").Trim();
            if (Enum.TryParse<DayOfWeek>(words, true, out var day) && !int.TryParse(words, out _))
                return today.AddDays(-(((int)today.DayOfWeek - (int)day + 7) % 7));
            var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var n) && n >= 0)
            {
                if (parts[1].StartsWith("day")) return today.AddDays(-n);
                if (parts[1].StartsWith("week")) return today.AddDays(-7 * n);
                if (parts[1].StartsWith("hour")) return (now ?? DateTime.Now).AddHours(-n);
            }
            return DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }
    }
}
