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
            };
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToDictionary(l => l.Id.Value, l => l.Name);
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
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
                        Type = TypeName(doc, e),
                        Lvl = e.LevelId != null && levels.TryGetValue(e.LevelId.Value, out var ln) ? ln : null,
                        Loc = Location(e),
                        P = Fingerprint(e),
                        K = KeyValues(doc, e),
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

        private static Dictionary<string, string> KeyValues(Document doc, Element e)
        {
            var k = new Dictionary<string, string>();
            void Add(string name, string value) { if (!string.IsNullOrWhiteSpace(value)) k[name] = value; }
            Add("Mark", e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString());
            Add("Comments", e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString());
            if (e is Autodesk.Revit.DB.Architecture.Room room) { Add("Number", room.Number); Add("Name", room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()); }
            if (e is Level || e is Grid) Add("Name", e.Name);
            try { if (doc.IsWorkshared) Add("Workset", doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name); } catch { }
            Add("Phase", (doc.GetElement(e.CreatedPhaseId) as Phase)?.Name);
            if (e.DemolishedPhaseId != null && e.DemolishedPhaseId != ElementId.InvalidElementId) Add("Demolished", (doc.GetElement(e.DemolishedPhaseId) as Phase)?.Name);
            if (e.DesignOption != null) Add("Option", e.DesignOption.Name);
            return k.Count == 0 ? null : k;
        }

        // ---- storage ---------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        public static string Save(Document doc, Snapshot snap)
        {
            var dir = System.IO.Path.Combine(Root, ModelKey(doc));
            Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"{snap.Time:yyyyMMdd-HHmmss}.json.gz");
            using (var fs = File.Create(file))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, snap, Json);
            foreach (var extra in List(doc).Skip(KeepPerModel)) try { File.Delete(extra.File); } catch { }
            return file;
        }

        public static Snapshot Load(string file)
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Snapshot>(gz, Json);
        }

        /// <summary>Snapshots of this model, newest first.</summary>
        public static List<(string File, DateTime Time)> List(Document doc)
        {
            var dir = System.IO.Path.Combine(Root, ModelKey(doc));
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
