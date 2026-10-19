using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Scripting;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The pure rules of parameter_fill (no Revit types, tested in tools/checkset-test): list rows, category groups,
    /// what counts as blank, and the per-model summary.
    /// </summary>
    internal static class ParameterFillLogic
    {
        internal sealed class Row
        {
            public string Name;
            public string Group;   // project | rooms | levels | elements
            public string Level;   // instance | type
        }

        internal sealed class RowResult
        {
            public string Group;
            public int Checked, Missing, Blank, Filled;
            public string StorageType;   // null when no element has the parameter
            public bool? InGeneral;      // Project Information rows only
        }

        /// <summary>The list's category value (or a group key) to the group key; null when unknown (never guessed).</summary>
        public static string GroupKey(string value)
        {
            var v = string.Join(" ", (value ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();
            switch (v)
            {
                case "project": case "project information": return "project";
                case "rooms": case "rooms, areas, spaces": case "rooms,areas,spaces": return "rooms";
                case "levels": return "levels";
                case "elements": case "systems, components, elements": case "systems,components,elements": return "elements";
                default: return null;
            }
        }

        /// <summary>"Type" reads the element's type; "Instance" or blank the element. Null when unknown.</summary>
        public static string LevelKey(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "" || v == "instance") return "instance";
            if (v == "type") return "type";
            return null;
        }

        /// <summary>Text: empty or whitespace; other storage types: no value. 0 counts as filled.</summary>
        public static bool IsBlank(bool isText, bool hasValue, string text) => isText ? string.IsNullOrWhiteSpace(text) : !hasValue;

        /// <summary>
        /// The request's rows: the key is name + group (a name such as Area can be in two groups); the first of a duplicate
        /// key wins. Rows with no name, or an unknown group or level, come back in <paramref name="skipped"/>.
        /// </summary>
        public static List<Row> ParseRows(JsonArray rows, JsonArray skipped)
        {
            var list = new List<Row>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in rows ?? new JsonArray())
            {
                if (node is not JsonObject o) continue;
                var name = (Text(o, "name") ?? Text(o, "parameter_name"))?.Trim();
                var groupText = Text(o, "group") ?? Text(o, "categories");
                var levelText = Text(o, "level") ?? Text(o, "type_instance");
                var group = GroupKey(groupText);
                var level = LevelKey(levelText);
                string reason = string.IsNullOrEmpty(name) ? "no parameter name"
                    : group == null ? $"unknown category group '{groupText}'"
                    : level == null ? $"unknown type/instance value '{levelText}'"
                    : !seen.Add(name + "\n" + group) ? "duplicate of an earlier row (same name and group)"
                    : null;
                if (reason != null) { skipped?.Add(new JsonObject { ["name"] = name, ["group"] = groupText, ["reason"] = reason }); continue; }
                list.Add(new Row { Name = name, Group = group, Level = level });
            }
            return list;
        }

        public static JsonObject Summary(IList<RowResult> rows) => new JsonObject
        {
            ["rows"] = rows.Count,
            ["complete"] = rows.Count(r => r.Checked > 0 && r.Missing == 0 && r.Blank == 0),
            ["withBlanks"] = rows.Count(r => r.Blank > 0),
            ["missingEverywhere"] = rows.Count(r => r.Checked > 0 && r.Missing == r.Checked),
            ["notText"] = rows.Count(r => r.StorageType != null && r.StorageType != "String"),
            ["notInGeneral"] = rows.Count(r => r.InGeneral == false),
            ["noElements"] = rows.Count(r => r.Checked == 0),
        };

        private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>
    /// Bridge "parameter_fill": { rows: [{ name, group, level }], all_documents, fill, value ("NA"), max_ids (20),
    /// check_only, dry_run }. Without fill a read-only check of the active model (or every open project model; links
    /// excluded). With fill a native change on the active model (preview, then ONE undo step) that writes the value into
    /// blank TEXT parameters of the listed rows, skipping read-only parameters and elements that cannot be edited now.
    /// </summary>
    internal static class ParameterFill
    {
        private static readonly BuiltInCategory[] RoomCats = { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces };

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var skippedRows = new JsonArray();
            var rows = ParameterFillLogic.ParseRows(args["rows"] as JsonArray, skippedRows);
            if (rows.Count == 0 && skippedRows.Count == 0) throw new CommandException("No rows: pass rows [{ name, group (project | rooms | levels | elements), level (instance | type) }].");
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));

            if (!Args.Bool(args, "fill"))
            {
                var active = Args.RequireDoc(app);
                var docs = new List<Document> { active };
                if (Args.Bool(args, "all_documents"))
                    docs.AddRange(app.Application.Documents.Cast<Document>().Where(d => !d.Equals(active) && !d.IsLinked && !d.IsFamilyDocument));
                return new JsonObject
                {
                    ["documents"] = new JsonArray(docs.Select(d => (JsonNode)new Scan(d, rows, maxIds).Report(d.Equals(active))).ToArray()),
                    ["skippedRows"] = skippedRows,
                };
            }

            if (Args.Bool(args, "all_documents")) throw new CommandException("fill works on the active model only: leave all_documents out (or false).");
            var value = Args.Str(args, "value") ?? "NA";
            if (string.IsNullOrWhiteSpace(value)) throw new CommandException("value must not be empty (it is what blank text parameters are filled with, e.g. \"NA\").");
            return NativeChange.Run(app, args, "parameter_fill", $"ACE: fill blank parameters with '{value}'", (doc, plan) =>
            {
                var scan = new Scan(doc, rows, maxIds);
                var report = scan.Report(true);   // the state before the fill
                var result = new JsonObject { ["documents"] = new JsonArray(report), ["skippedRows"] = skippedRows.DeepClone() };
                result["fill"] = scan.Fill(doc, value, plan);
                return result;
            });
        }

        /// <summary>One model's answer for every row (elements per group read once).</summary>
        private sealed class Scan
        {
            private readonly Document _doc;
            private readonly List<(ParameterFillLogic.Row Row, ParameterFillLogic.RowResult Result, JsonObject Json, List<Parameter> Blanks)> _rows = new List<(ParameterFillLogic.Row, ParameterFillLogic.RowResult, JsonObject, List<Parameter>)>();
            private readonly Dictionary<string, List<Element>> _targets = new Dictionary<string, List<Element>>();
            private Dictionary<string, List<(InternalDefinition Def, ElementBinding Binding)>> _bindings;

            public Scan(Document doc, List<ParameterFillLogic.Row> rows, int maxIds)
            {
                _doc = doc;
                foreach (var row in rows) _rows.Add(Check(row, maxIds));
            }

            public JsonObject Report(bool active) => new JsonObject
            {
                ["model"] = _doc.Title,
                ["active"] = active,
                ["rows"] = new JsonArray(_rows.Select(r => (JsonNode)r.Json).ToArray()),
                ["summary"] = ParameterFillLogic.Summary(_rows.Select(r => r.Result).ToList()),
            };

            private (ParameterFillLogic.Row, ParameterFillLogic.RowResult, JsonObject, List<Parameter>) Check(ParameterFillLogic.Row row, int maxIds)
            {
                var res = new ParameterFillLogic.RowResult { Group = row.Group };
                var blanks = new List<Parameter>();
                var blankIds = new JsonArray();
                var missingIds = new JsonArray();
                Parameter first = null;
                foreach (var e in Targets(row.Group, row.Level))
                {
                    res.Checked++;
                    var p = e.LookupParameter(row.Name);
                    if (p == null)
                    {
                        res.Missing++;
                        if (missingIds.Count < maxIds) missingIds.Add(e.Id.Value);
                        continue;
                    }
                    first ??= p;
                    var isText = p.StorageType == StorageType.String;
                    if (ParameterFillLogic.IsBlank(isText, p.HasValue, isText ? p.AsString() : null))
                    {
                        res.Blank++;
                        blanks.Add(p);
                        if (blankIds.Count < maxIds) blankIds.Add(e.Id.Value);
                    }
                    else res.Filled++;
                }

                var json = new JsonObject { ["name"] = row.Name, ["group"] = row.Group, ["level"] = row.Level };
                var (def, binding) = BindingFor(row.Name, row.Group);
                string origin = null;
                if (first != null)
                {
                    origin = first.IsShared ? "shared"
                        : first.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID ? "built-in"
                        : def != null ? "project"
                        : "family";
                }
                else if (def != null) origin = _doc.GetElement(def.Id) is SharedParameterElement ? "shared" : "project";
                json["origin"] = origin;
                if (origin == "built-in") { json["bound"] = null; json["boundAs"] = null; }
                else
                {
                    json["bound"] = binding != null && GroupCategories(binding, row.Group).Count > 0;
                    json["boundAs"] = binding is InstanceBinding ? "instance" : binding is TypeBinding ? "type" : null;
                    if (binding != null) json["boundTo"] = new JsonArray(GroupCategories(binding, row.Group).Take(50).Select(n => (JsonNode)n).ToArray());
                }
                res.StorageType = first?.StorageType.ToString();
                json["storageType"] = res.StorageType;
                json["isText"] = res.StorageType == null ? null : (JsonNode)(res.StorageType == "String");
                if (row.Group == "project")
                {
                    var gdef = (first?.Definition as InternalDefinition) ?? def;
                    if (gdef != null)
                    {
                        var gid = gdef.GetGroupTypeId();
                        json["groupId"] = GroupLabel(gid);
                        res.InGeneral = gid != null && gid.NameEquals(GroupTypeId.General);   // ForgeTypeId has no == (reference equality)
                        json["inGeneral"] = res.InGeneral;
                    }
                    else { json["groupId"] = null; json["inGeneral"] = null; }
                }
                json["checked"] = res.Checked;
                json["missing"] = res.Missing;
                json["blank"] = res.Blank;
                json["filled"] = res.Filled;
                json["blankIds"] = blankIds;
                json["missingIds"] = missingIds;
                return (row, res, json, blanks);
            }

            /// <summary>Writes the value into the blank text parameters (plan: count only). Reports what it left and why.</summary>
            public JsonObject Fill(Document doc, string value, bool plan)
            {
                var set = 0;
                var skipped = new JsonArray();
                var skippedCount = 0;
                var done = new HashSet<string>();
                void Skip(JsonNode id, string name, string reason)
                {
                    skippedCount++;
                    if (skipped.Count < 500) skipped.Add(new JsonObject { ["id"] = id, ["name"] = name, ["reason"] = reason });
                }
                foreach (var (row, res, _, blanks) in _rows)
                {
                    if (blanks.Count == 0) continue;
                    if (res.StorageType != "String")
                    {
                        // A number, length, area, Yes/No or date stored as a number cannot hold text.
                        Skip(null, row.Name, $"not a text parameter ({res.StorageType}): '{value}' cannot be stored ({blanks.Count} blank)");
                        continue;
                    }
                    foreach (var p in blanks)
                    {
                        var e = p.Element;
                        if (p.StorageType != StorageType.String) { Skip(e.Id.Value, row.Name, $"not a text parameter here ({p.StorageType})"); continue; }
                        if (!done.Add(e.Id.Value + "\n" + p.Id.Value)) continue;   // the same parameter through a type shared by two rows
                        if (p.IsReadOnly) { Skip(e.Id.Value, row.Name, "read-only"); continue; }
                        if (!ScriptContext.CanEdit(doc, e, out var why)) { Skip(e.Id.Value, row.Name, why); continue; }
                        if (plan) { set++; continue; }
                        if (p.Set(value)) set++;
                        else Skip(e.Id.Value, row.Name, "Revit refused the value");
                    }
                }
                return new JsonObject { ["set"] = set, ["value"] = value, ["skipped"] = skipped, ["skippedCount"] = skippedCount };
            }

            // ---- elements per group ----------------------------------------------------------------------------

            private List<Element> Targets(string group, string level)
            {
                if (level != "type") return Instances(group);
                var key = group + "/type";
                if (_targets.TryGetValue(key, out var cached)) return cached;
                var list = Instances(group).Select(e => e.GetTypeId()).Where(id => id != ElementId.InvalidElementId).Distinct()
                    .Select(id => _doc.GetElement(id)).Where(t => t != null).ToList();
                _targets[key] = list;
                return list;
            }

            private List<Element> Instances(string group)
            {
                var key = group + "/instance";
                if (_targets.TryGetValue(key, out var cached)) return cached;
                var list = group == "project" ? (_doc.ProjectInformation != null ? new List<Element> { _doc.ProjectInformation } : new List<Element>())
                    : group == "rooms" ? new FilteredElementCollector(_doc).WherePasses(new ElementMulticategoryFilter(RoomCats)).WhereElementIsNotElementType().ToList()
                    : group == "levels" ? new FilteredElementCollector(_doc).OfClass(typeof(Level)).ToList()
                    : new FilteredElementCollector(_doc).WhereElementIsNotElementType().Where(IsModelElement).ToList();
                _targets[key] = list;
                return list;
            }

            private static bool IsModelElement(Element e)
            {
                var c = e.Category;
                if (c == null || c.CategoryType != CategoryType.Model || !c.AllowsBoundParameters || e.ViewSpecific) return false;
                var bic = c.BuiltInCategory;
                return bic != BuiltInCategory.OST_Levels && Array.IndexOf(RoomCats, bic) < 0 && !(e is Level);
            }

            // ---- bindings ---------------------------------------------------------------------------------------

            private Dictionary<string, List<(InternalDefinition, ElementBinding)>> Bindings()
            {
                if (_bindings != null) return _bindings;
                _bindings = new Dictionary<string, List<(InternalDefinition, ElementBinding)>>(StringComparer.Ordinal);
                var it = _doc.ParameterBindings.ForwardIterator();
                it.Reset();
                while (it.MoveNext())
                {
                    if (it.Key is not InternalDefinition d || it.Current is not ElementBinding b) continue;
                    if (!_bindings.TryGetValue(d.Name, out var l)) _bindings[d.Name] = l = new List<(InternalDefinition, ElementBinding)>();
                    l.Add((d, b));
                }
                return _bindings;
            }

            /// <summary>The binding of that name, preferring one that binds a category of the group.</summary>
            private (InternalDefinition, ElementBinding) BindingFor(string name, string group)
            {
                if (!Bindings().TryGetValue(name, out var l) || l.Count == 0) return (null, null);
                foreach (var x in l) if (GroupCategories(x.Item2, group).Count > 0) return x;
                return l[0];
            }

            private static List<string> GroupCategories(ElementBinding b, string group)
            {
                var names = new List<string>();
                foreach (Category c in b.Categories)
                {
                    var bic = c.BuiltInCategory;
                    var inGroup = group == "project" ? bic == BuiltInCategory.OST_ProjectInformation
                        : group == "rooms" ? Array.IndexOf(RoomCats, bic) >= 0
                        : group == "levels" ? bic == BuiltInCategory.OST_Levels
                        : c.CategoryType == CategoryType.Model && bic != BuiltInCategory.OST_Levels && Array.IndexOf(RoomCats, bic) < 0;
                    if (inGroup) names.Add(c.Name);
                }
                return names;
            }

            private static string GroupLabel(ForgeTypeId gid)
            {
                if (gid == null || string.IsNullOrEmpty(gid.TypeId)) return "Other";
                if (gid.NameEquals(GroupTypeId.General)) return "General";
                try { return LabelUtils.GetLabelForGroup(gid); } catch { return gid.TypeId; }
            }
        }
    }
}
