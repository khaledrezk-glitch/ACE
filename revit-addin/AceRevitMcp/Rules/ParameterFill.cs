using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
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
            public string Level;   // instance | type | any (instance first, then the element's type)
            public List<string> Categories = new List<string>();    // Revit OST_ names as sent (empty: every element of the group)
            public string Guid;                                       // expected GUID (null: not checked)
            public List<string> Disciplines = new List<string>();   // discipline codes the row applies to (empty: every model)
            public string Key;                                        // the caller's row key, echoed
        }

        internal sealed class RowResult
        {
            public string Group;
            public int Checked, Missing, Blank, Filled, Na, OnType;
            public string StorageType;   // null when no element has the parameter
            public bool? InGeneral;      // Project Information rows only
            public bool OutOfScope;      // the row's disciplines exclude the model's discipline: not scanned
            public bool Unmapped;        // categories given, none of them a Revit category: not scanned
            public bool? GuidMatch;      // null: no expected GUID or no shared definition
            public int Unbound;          // row categories the binding does not cover
            public bool? WrongLevel;     // bound as type for an instance row, or the reverse
            public string Status;
        }

        /// <summary>The request's discipline scope: codes, model title part → discipline, category (OST_ name) → owners.</summary>
        internal sealed class Scope
        {
            public List<string> Codes = new List<string>();
            public List<KeyValuePair<string, string>> Models = new List<KeyValuePair<string, string>>();
            public Dictionary<string, List<string>> Ownership = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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

        /// <summary>"Type" reads the element's type; "Instance" or blank the element; "Any" the element, else its type. Null when unknown.</summary>
        public static string LevelKey(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "" || v == "instance") return "instance";
            if (v == "type") return "type";
            if (v == "any") return "any";
            return null;
        }

        /// <summary>Text: empty or whitespace; other storage types: no value. 0 counts as filled.</summary>
        public static bool IsBlank(bool isText, bool hasValue, string text) => isText ? string.IsNullOrWhiteSpace(text) : !hasValue;

        /// <summary>The generic declared-missing texts, used when the request sends no na_values (the server sends the project's list).</summary>
        public static readonly string[] DefaultNaValues = { "NA", "N/A" };

        /// <summary>The request's na_values (trimmed, blanks dropped); the generic default when absent or empty.</summary>
        public static IList<string> NaValues(JsonArray values)
        {
            var list = new List<string>();
            foreach (var n in values ?? new JsonArray())
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            return list.Count > 0 ? list : DefaultNaValues;
        }

        /// <summary>Declared missing: text equal to one of the na values (trimmed, any case). Counted apart from filled and blank.</summary>
        public static bool IsNA(string text, IList<string> naValues)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return false;
            return (naValues ?? DefaultNaValues).Any(na => string.Equals(t, na, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The request's rows: the key is name + group + sorted categories (a name such as Area can be in two groups); the
        /// first of a duplicate key wins. Rows with no name, or an unknown group or level, come back in <paramref name="skipped"/>.
        /// </summary>
        public static List<Row> ParseRows(JsonArray rows, JsonArray skipped)
        {
            var list = new List<Row>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in rows ?? new JsonArray())
            {
                if (node is not JsonObject o) continue;
                var name = (Text(o, "name") ?? Text(o, "parameter_name"))?.Trim();
                var groupText = Text(o, "group") ?? Text(o, "categories");   // the CSV's "categories" column is the group
                var levelText = Text(o, "level") ?? Text(o, "type_instance");
                var group = GroupKey(groupText);
                var level = LevelKey(levelText);
                var cats = (TextList(o, "categories") ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList();
                var sorted = cats.OrderBy(c => c, StringComparer.Ordinal);
                string reason = string.IsNullOrEmpty(name) ? "no parameter name"
                    : group == null ? $"unknown category group '{groupText}'"
                    : level == null ? $"unknown type/instance value '{levelText}'"
                    : !seen.Add(name + "\n" + group + "\n" + string.Join("|", sorted)) ? "duplicate of an earlier row (same name, group and categories)"
                    : null;
                if (reason != null) { skipped?.Add(new JsonObject { ["name"] = name, ["group"] = groupText, ["key"] = Text(o, "key"), ["reason"] = reason }); continue; }
                var guid = Text(o, "guid")?.Trim();
                list.Add(new Row
                {
                    Name = name, Group = group, Level = level, Categories = cats,
                    Guid = string.IsNullOrEmpty(guid) ? null : guid,
                    Disciplines = TextList(o, "disciplines") ?? new List<string>(),
                    Key = Text(o, "key"),
                });
            }
            return list;
        }

        /// <summary>The request's scope object (missing or malformed parts are empty).</summary>
        public static Scope ParseScope(JsonObject scope)
        {
            var s = new Scope();
            if (scope == null) return s;
            s.Codes = TextList(scope, "codes") ?? new List<string>();
            if (scope["models"] is JsonObject models)
                foreach (var kv in models)
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var d) && !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(d))
                        s.Models.Add(new KeyValuePair<string, string>(kv.Key.Trim(), d.Trim()));
            if (scope["ownership"] is JsonObject own)
                foreach (var kv in own)
                    if (kv.Value is JsonArray) s.Ownership[kv.Key.Trim()] = TextList(own, kv.Key) ?? new List<string>();
            return s;
        }

        /// <summary>
        /// The model's discipline: the models entry whose key is part of the title (case-insensitive; the longest key wins),
        /// source "mapping"; else the first title token (split on - _ space .) equal to a code, source "title"; else null.
        /// </summary>
        public static string ModelDiscipline(string title, Scope scope, out string source)
        {
            source = null;
            title = title ?? "";
            if (scope == null) return null;
            var hit = scope.Models.Where(m => title.IndexOf(m.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(m => m.Key.Length).Select(m => m.Value).FirstOrDefault();
            if (hit != null) { source = "mapping"; return hit; }
            foreach (var token in title.Split(new[] { '-', '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var code = scope.Codes.FirstOrDefault(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));
                if (code != null) { source = "title"; return code; }
            }
            return null;
        }

        /// <summary>A row applies to the model unless it names disciplines and the model's (known) discipline is not one of them.</summary>
        public static bool InScope(IList<string> rowDisciplines, string discipline) =>
            discipline == null || rowDisciplines == null || rowDisciplines.Count == 0
            || rowDisciplines.Any(d => string.Equals(d, discipline, StringComparison.OrdinalIgnoreCase));

        /// <summary>An element of a category whose owners (ownership) do not include the model's discipline belongs to another model.</summary>
        public static bool IsForeign(Scope scope, string categoryName, string discipline)
        {
            if (discipline == null || scope == null || categoryName == null) return false;
            if (!scope.Ownership.TryGetValue(categoryName, out var owners) || owners.Count == 0) return false;
            return !owners.Any(o => string.Equals(o, discipline, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The row's status, first rule that applies: out-of-scope, unmapped, no-elements, not-bound (no checked element has
        /// it), wrong-guid, partly-bound (some elements lack it, or the binding misses row categories), blanks,
        /// complete-with-na, complete.
        /// </summary>
        public static string Status(RowResult r)
        {
            if (r.OutOfScope) return "out-of-scope";
            if (r.Unmapped) return "unmapped";
            if (r.Checked == 0) return "no-elements";
            if (r.Missing == r.Checked) return "not-bound";
            if (r.GuidMatch == false) return "wrong-guid";
            if (r.Missing > 0 || r.Unbound > 0) return "partly-bound";
            if (r.Blank > 0) return "blanks";
            if (r.Na > 0) return "complete-with-na";
            return "complete";
        }

        public static JsonObject Summary(IList<RowResult> rows, int foreign) => new JsonObject
        {
            ["rows"] = rows.Count,
            ["complete"] = rows.Count(r => (r.Status ?? Status(r)) is "complete" or "complete-with-na"),
            ["withBlanks"] = rows.Count(r => r.Blank > 0),
            ["missingEverywhere"] = rows.Count(r => r.Checked > 0 && r.Missing == r.Checked),
            ["notText"] = rows.Count(r => r.StorageType != null && r.StorageType != "String"),
            ["notInGeneral"] = rows.Count(r => r.InGeneral == false),
            ["noElements"] = rows.Count(r => r.Checked == 0 && !r.OutOfScope && !r.Unmapped),
            ["withNA"] = rows.Count(r => r.Na > 0),
            ["outOfScope"] = rows.Count(r => r.OutOfScope),
            ["wrongGuid"] = rows.Count(r => r.GuidMatch == false),
            ["notBound"] = rows.Count(r => (r.Status ?? Status(r)) == "not-bound"),
            ["wrongLevel"] = rows.Count(r => r.WrongLevel == true),
            ["foreign"] = foreign,
        };

        /// <summary>Expected vs model GUID: null when either is missing; GUID text compared as GUIDs when both parse.</summary>
        public static bool? GuidMatches(string expected, string model)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(model)) return null;
            if (System.Guid.TryParse(expected.Trim(), out var a) && System.Guid.TryParse(model.Trim(), out var b)) return a == b;
            return string.Equals(expected.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        /// <summary>A JSON array of strings (trimmed, blanks dropped); null when the key is not an array.</summary>
        private static List<string> TextList(JsonObject o, string key)
        {
            if (o[key] is not JsonArray a) return null;
            var list = new List<string>();
            foreach (var n in a)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            return list;
        }
    }

    /// <summary>
    /// Bridge "parameter_fill": { rows: [{ name, group, level, categories?, guid?, disciplines?, key? }], scope?: { codes,
    /// models, ownership }, na_values (["NA", "N/A"]), all_documents, fill, value ("NA"), expect_model, max_ids (20), check_only, dry_run }. Without
    /// fill a read-only check of the active model (or every open project model; links excluded), with a pre-flight of each
    /// model. With fill a native change on the active model (preview, then ONE undo step) that writes the value into blank
    /// TEXT parameters of the listed rows, skipping read-only parameters, elements in groups or design options and
    /// elements that cannot be edited now; it answers the state before and (after) the writes.
    /// </summary>
    internal static class ParameterFill
    {
        private static readonly BuiltInCategory[] RoomCats = { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces };

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var fill = Args.Bool(args, "fill");
            var expect = Args.Str(args, "expect_model");
            if (fill && expect != null)
            {
                var activeTitle = Args.RequireDoc(app).Title;
                if (!string.Equals(expect, activeTitle, StringComparison.Ordinal))
                    throw new CommandException($"The preview was made on {expect}, the active model is {activeTitle}: preview again on this model.");
            }
            var skippedRows = new JsonArray();
            var rows = ParameterFillLogic.ParseRows(args["rows"] as JsonArray, skippedRows);
            if (rows.Count == 0 && skippedRows.Count == 0) throw new CommandException("No rows: pass rows [{ name, group (project | rooms | levels | elements), level (instance | type | any) }].");
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));
            var scope = ParameterFillLogic.ParseScope(args["scope"] as JsonObject);
            var na = ParameterFillLogic.NaValues(args["na_values"] as JsonArray);

            if (!fill)
            {
                var active = Args.RequireDoc(app);
                var docs = new List<Document> { active };
                if (Args.Bool(args, "all_documents"))
                    docs.AddRange(app.Application.Documents.Cast<Document>().Where(d => !d.Equals(active) && !d.IsLinked && !d.IsFamilyDocument));
                return new JsonObject
                {
                    ["documents"] = new JsonArray(docs.Select(d => (JsonNode)new Scan(d, rows, maxIds, scope, na).Report(d.Equals(active))).ToArray()),
                    ["skippedRows"] = skippedRows,
                };
            }

            if (Args.Bool(args, "all_documents")) throw new CommandException("fill works on the active model only: leave all_documents out (or false).");
            var value = Args.Str(args, "value") ?? "NA";
            if (string.IsNullOrWhiteSpace(value)) throw new CommandException("value must not be empty (it is what blank text parameters are filled with, e.g. \"NA\").");
            return NativeChange.Run(app, args, "parameter_fill", $"ACE: fill blank parameters with '{value}'", (doc, plan) =>
            {
                var scan = new Scan(doc, rows, maxIds, scope, na);
                var report = scan.Report(true);   // the state before the fill
                var result = new JsonObject
                {
                    ["model"] = doc.Title,
                    ["modelPath"] = doc.PathName,
                    ["documents"] = new JsonArray(report),
                    ["skippedRows"] = skippedRows.DeepClone(),
                };
                result["fill"] = scan.Fill(value, plan);
                return result;
            });
        }

        /// <summary>One model's answer for every row (elements per category, types and worksharing states read once).</summary>
        private sealed class Scan
        {
            private sealed class Entry
            {
                public ParameterFillLogic.Row Row;
                public ParameterFillLogic.RowResult Result;
                public JsonObject Json;
                public List<Parameter> Blanks;
            }

            private readonly Document _doc;
            private readonly ParameterFillLogic.Scope _scope;
            private readonly string _discipline, _disciplineSource;
            private readonly int _maxIds;
            private readonly IList<string> _na;
            private readonly bool _workshared;
            private readonly List<Entry> _rows = new List<Entry>();
            private readonly Dictionary<string, List<Element>> _targets = new Dictionary<string, List<Element>>();
            private readonly Dictionary<BuiltInCategory, List<Element>> _byCategory = new Dictionary<BuiltInCategory, List<Element>>();
            private readonly Dictionary<long, Element> _types = new Dictionary<long, Element>();
            private readonly Dictionary<string, Parameter> _typeParams = new Dictionary<string, Parameter>(StringComparer.Ordinal);
            private readonly Dictionary<BuiltInCategory, bool> _foreignCat = new Dictionary<BuiltInCategory, bool>();
            private readonly SortedDictionary<string, int> _foreign = new SortedDictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<long, (bool Ok, string Why, string Owner)> _edit = new Dictionary<long, (bool, string, string)>();
            private Dictionary<string, List<(InternalDefinition Def, ElementBinding Binding)>> _bindings;
            private readonly Dictionary<long, Element> _blankHolders = new Dictionary<long, Element>();
            private readonly HashSet<long> _scanned = new HashSet<long>();
            private int _scannedInOptions;

            public Scan(Document doc, List<ParameterFillLogic.Row> rows, int maxIds, ParameterFillLogic.Scope scope, IList<string> naValues)
            {
                _doc = doc;
                _na = naValues;
                _scope = scope;
                _maxIds = maxIds;
                try { _workshared = doc.IsWorkshared; } catch { _workshared = false; }
                _discipline = ParameterFillLogic.ModelDiscipline(doc.Title, scope, out _disciplineSource);
                foreach (var row in rows) _rows.Add(Check(row, true));
            }

            public JsonObject Report(bool active) => new JsonObject
            {
                ["model"] = _doc.Title,
                ["active"] = active,
                ["discipline"] = _discipline,
                ["disciplineSource"] = _disciplineSource,
                ["preflight"] = Preflight(),
                ["rows"] = new JsonArray(_rows.Select(r => (JsonNode)r.Json).ToArray()),
                ["summary"] = ParameterFillLogic.Summary(_rows.Select(r => r.Result).ToList(), _foreign.Values.Sum()),
            };

            /// <param name="record">The first scan records the pre-flight facts (scanned elements, blank holders); a re-scan does not.</param>
            private Entry Check(ParameterFillLogic.Row row, bool record)
            {
                var res = new ParameterFillLogic.RowResult { Group = row.Group };
                var blanks = new List<Parameter>();
                var blankIds = new JsonArray();
                var missingIds = new JsonArray();
                var json = new JsonObject { ["key"] = row.Key, ["name"] = row.Name, ["group"] = row.Group, ["level"] = row.Level };

                // Row categories: Revit OST_ names (never a numeric value or INVALID); the rest is reported, never guessed.
                var bics = new List<BuiltInCategory>();
                var unknown = new JsonArray();
                foreach (var c in row.Categories)
                {
                    if (c.StartsWith("OST_", StringComparison.Ordinal) && Enum.TryParse<BuiltInCategory>(c, false, out var bic)
                        && Enum.IsDefined(typeof(BuiltInCategory), bic) && bic != BuiltInCategory.INVALID)
                    { if (!bics.Contains(bic)) bics.Add(bic); }
                    else unknown.Add(c);
                }
                json["categories"] = new JsonArray(bics.Select(b => (JsonNode)b.ToString()).ToArray());
                json["unknownCategories"] = unknown;

                // The bound definition (when there is one) is read directly; by name only without a binding (family, built-in).
                var (def, binding) = BindingFor(row.Name, row.Group, bics);
                Guid? sharedGuid = null;
                if (def != null) { try { if (_doc.GetElement(def.Id) is SharedParameterElement spe) sharedGuid = spe.GuidValue; } catch { sharedGuid = null; } }
                Parameter Get(Element e) => def == null ? e.LookupParameter(row.Name) : sharedGuid.HasValue ? e.get_Parameter(sharedGuid.Value) : e.get_Parameter(def);
                var lookupKey = def == null ? "n:" + row.Name : "d:" + def.Id.Value;

                res.OutOfScope = !ParameterFillLogic.InScope(row.Disciplines, _discipline);
                res.Unmapped = !res.OutOfScope && row.Categories.Count > 0 && bics.Count == 0;
                Parameter first = null;
                var instancesAffected = 0;
                if (!res.OutOfScope && !res.Unmapped)
                {
                    var targets = bics.Count > 0 ? CategoryTargets(bics, row.Level, row.Disciplines.Count == 0) : Targets(row.Group, row.Level);
                    var typesSeen = new HashSet<long>();
                    foreach (var e in targets)
                    {
                        if (record && _scanned.Add(e.Id.Value) && InDesignOption(e)) _scannedInOptions++;
                        var p = Get(e);
                        if (p == null && row.Level == "any")
                        {
                            var tid = e.GetTypeId();
                            var tp = tid == ElementId.InvalidElementId ? null : TypeParameter(tid, lookupKey, Get);
                            if (tp != null)
                            {
                                instancesAffected++;
                                if (!typesSeen.Add(tid.Value)) continue;   // the type's value is counted once
                                res.OnType++;
                                p = tp;
                            }
                        }
                        res.Checked++;
                        if (p == null)
                        {
                            res.Missing++;
                            if (missingIds.Count < _maxIds) missingIds.Add(e.Id.Value);
                            continue;
                        }
                        first ??= p;
                        var isText = p.StorageType == StorageType.String;
                        var text = isText ? p.AsString() : null;
                        if (ParameterFillLogic.IsBlank(isText, p.HasValue, text))
                        {
                            var holder = p.Element ?? e;   // the type when the value comes from the type
                            res.Blank++;
                            blanks.Add(p);
                            if (record) _blankHolders[holder.Id.Value] = holder;
                            if (blankIds.Count < _maxIds) blankIds.Add(holder.Id.Value);
                        }
                        else if (isText && ParameterFillLogic.IsNA(text, _na)) res.Na++;
                        else res.Filled++;
                    }
                }

                string origin = null;
                if (first != null)
                {
                    origin = first.IsShared ? "shared"
                        : first.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID ? "built-in"
                        : def != null ? "project"
                        : "family";
                }
                else if (def != null) origin = sharedGuid.HasValue ? "shared" : "project";
                json["origin"] = origin;
                JsonArray unbound = null;
                if (origin == "built-in") { json["bound"] = null; json["boundAs"] = null; }
                else
                {
                    json["bound"] = binding != null && (bics.Count > 0 ? bics.Any(b => Binds(binding, b)) : GroupCategories(binding, row.Group).Count > 0);
                    json["boundAs"] = binding is InstanceBinding ? "instance" : binding is TypeBinding ? "type" : null;
                    if (binding != null)
                    {
                        json["boundTo"] = new JsonArray(GroupCategories(binding, row.Group).Take(50).Select(n => (JsonNode)n).ToArray());
                        if (bics.Count > 0) unbound = new JsonArray(bics.Where(b => !Binds(binding, b)).Select(b => (JsonNode)b.ToString()).ToArray());
                        if (row.Level != "any")
                            res.WrongLevel = (row.Level == "instance" && binding is TypeBinding) || (row.Level == "type" && binding is InstanceBinding);
                    }
                }
                res.Unbound = unbound?.Count ?? 0;
                json["unboundCategories"] = unbound;
                json["wrongLevel"] = res.WrongLevel;

                string modelGuid = null;
                try
                {
                    if (first != null && first.IsShared) modelGuid = first.GUID.ToString();
                    else if (sharedGuid.HasValue) modelGuid = sharedGuid.Value.ToString();
                }
                catch { modelGuid = null; }
                res.GuidMatch = ParameterFillLogic.GuidMatches(row.Guid, modelGuid);
                json["modelGuid"] = modelGuid;
                json["guidMatch"] = res.GuidMatch;
                json["definitions"] = Bindings().TryGetValue(row.Name, out var defs) ? defs.Count : 0;

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
                    string shown = null;
                    try { if (first != null) shown = first.AsValueString() ?? first.AsString(); } catch { shown = null; }
                    json["value"] = shown;
                }
                res.Status = ParameterFillLogic.Status(res);
                json["status"] = res.Status;
                json["checked"] = res.Checked;
                json["missing"] = res.Missing;
                json["blank"] = res.Blank;
                json["filled"] = res.Filled;
                json["na"] = res.Na;
                json["onType"] = res.OnType;
                json["instancesAffected"] = instancesAffected;
                json["blankIds"] = blankIds;
                json["missingIds"] = missingIds;
                return new Entry { Row = row, Result = res, Json = json, Blanks = blanks };
            }

            /// <summary>
            /// Writes the value into the blank text parameters (plan: count only) and, after real writes (dry run or apply,
            /// inside the transaction), re-scans the touched rows: "after" is the end state. Reports what it left and why.
            /// </summary>
            public JsonObject Fill(string value, bool plan)
            {
                var set = 0;
                var skipped = new JsonArray();
                var skippedCount = 0;
                var done = new HashSet<string>();
                var touched = new List<Entry>();
                void Skip(JsonNode id, string name, string reason)
                {
                    skippedCount++;
                    if (skipped.Count < 500) skipped.Add(new JsonObject { ["id"] = id, ["name"] = name, ["reason"] = reason });
                }
                foreach (var entry in _rows)
                {
                    var (row, res, blanks) = (entry.Row, entry.Result, entry.Blanks);
                    if (blanks.Count == 0) continue;
                    if (res.StorageType != "String")
                    {
                        // A number, length, area, Yes/No or date stored as a number cannot hold text.
                        Skip(null, row.Name, $"not a text parameter ({res.StorageType}): '{value}' cannot be stored ({blanks.Count} blank)");
                        continue;
                    }
                    var setBefore = set;
                    foreach (var p in blanks)
                    {
                        var e = p.Element;
                        if (p.StorageType != StorageType.String) { Skip(e.Id.Value, row.Name, $"not a text parameter here ({p.StorageType})"); continue; }
                        if (!done.Add(e.Id.Value + "\n" + p.Id.Value)) continue;   // the same parameter through a type shared by two rows
                        if (p.IsReadOnly) { Skip(e.Id.Value, row.Name, "read-only"); continue; }
                        if (InGroup(e)) { Skip(e.Id.Value, row.Name, "in a group"); continue; }
                        if (InDesignOption(e)) { Skip(e.Id.Value, row.Name, "in a design option"); continue; }
                        var state = EditState(e);
                        if (!state.Ok) { Skip(e.Id.Value, row.Name, state.Why); continue; }
                        if (plan) { set++; continue; }
                        if (p.Set(value)) set++;
                        else Skip(e.Id.Value, row.Name, "Revit refused the value");
                    }
                    if (set > setBefore) touched.Add(entry);
                }
                var result = new JsonObject { ["set"] = set, ["value"] = value, ["skipped"] = skipped, ["skippedCount"] = skippedCount };
                if (plan) { result["after"] = null; return result; }

                // A row is re-read when a write touched it, or shares a written parameter with one that was (a type).
                var now = touched.ToDictionary(t => t, t => Check(t.Row, false));
                foreach (var entry in _rows)
                    if (!now.ContainsKey(entry) && entry.Blanks.Count > 0 && entry.Result.StorageType == "String") now[entry] = Check(entry.Row, false);
                var afterRows = new JsonArray();
                foreach (var entry in _rows)
                {
                    if (!now.TryGetValue(entry, out var a)) continue;
                    afterRows.Add(new JsonObject
                    {
                        ["key"] = entry.Row.Key, ["name"] = entry.Row.Name, ["status"] = a.Result.Status,
                        ["blank"] = a.Result.Blank, ["na"] = a.Result.Na, ["filled"] = a.Result.Filled,
                    });
                }
                result["after"] = new JsonObject
                {
                    ["summary"] = ParameterFillLogic.Summary(_rows.Select(r => now.TryGetValue(r, out var a) ? a.Result : r.Result).ToList(), _foreign.Values.Sum()),
                    ["rows"] = afterRows,
                };
                return result;
            }

            // ---- pre-flight (read-only; each piece guarded so it never fails the check) -----------------------

            private JsonObject Preflight()
            {
                var doc = _doc;
                var pf = new JsonObject();
                void Try(string key, Func<JsonNode> f) { try { pf[key] = f(); } catch { pf[key] = null; } }
                Try("revitVersion", () => doc.Application.VersionNumber);
                Try("aceVersion", () => typeof(ParameterFill).Assembly.GetName().Version?.ToString(3));
                pf["workshared"] = _workshared;
                string central = null;
                try { if (_workshared) central = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch { central = null; }
                pf["centralPath"] = central;
                Try("isCentral", () => !_workshared || central == null ? null
                    : (JsonNode)string.Equals(System.IO.Path.GetFullPath(central), System.IO.Path.GetFullPath(doc.PathName ?? ""), StringComparison.OrdinalIgnoreCase));
                Try("worksets", () => _workshared ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count : 0);
                Try("links", () =>
                {
                    var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
                    var notLoaded = links.Count(l => { try { return !RevitLinkType.IsLoaded(doc, l.Id); } catch { return false; } });
                    return new JsonObject
                    {
                        ["count"] = links.Count,
                        ["notLoaded"] = notLoaded,
                        ["names"] = new JsonArray(links.Take(50).Select(l => (JsonNode)l.Name).ToArray()),
                    };
                });
                Try("phases", () => doc.Phases.Size);
                Try("designOptions", () => new JsonObject
                {
                    ["sets"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DesignOptionSets).GetElementCount(),
                    ["elements"] = _scannedInOptions,
                });
                Try("groups", () => new JsonObject { ["instances"] = new FilteredElementCollector(doc).OfClass(typeof(Group)).GetElementCount() });
                int inGroups = 0, inOptions = 0, borrowed = 0;
                var by = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var e in _blankHolders.Values)
                {
                    if (InGroup(e)) inGroups++;
                    if (InDesignOption(e)) inOptions++;
                    if (!_workshared) continue;
                    var owner = EditState(e).Owner;
                    if (owner == null) continue;
                    borrowed++;
                    by[owner] = by.TryGetValue(owner, out var n) ? n + 1 : 1;
                }
                pf["blanksInGroups"] = inGroups;
                pf["blanksInDesignOptions"] = inOptions;
                pf["blanksBorrowed"] = _workshared ? borrowed : (JsonNode)null;
                var borrowedBy = new JsonObject();
                foreach (var kv in by.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(20)) borrowedBy[kv.Key] = kv.Value;
                pf["borrowedBy"] = _workshared ? borrowedBy : null;
                var foreign = new JsonObject();
                foreach (var kv in _foreign) foreign[kv.Key] = kv.Value;
                pf["foreign"] = foreign;
                return pf;
            }

            private static bool InGroup(Element e) { try { return e.GroupId != null && e.GroupId != ElementId.InvalidElementId; } catch { return false; } }

            private static bool InDesignOption(Element e) { try { return e.DesignOption != null; } catch { return false; } }

            /// <summary>
            /// Whether the element can be edited now (as ScriptContext.CanEdit), read once per element and shared by the
            /// pre-flight and the fill. Owner: who has it borrowed, where Revit knows it.
            /// </summary>
            private (bool Ok, string Why, string Owner) EditState(Element e)
            {
                if (!_workshared) return (true, null, null);
                var id = e.Id.Value;
                if (_edit.TryGetValue(id, out var known)) return known;
                (bool, string, string) state;
                try
                {
                    var status = WorksharingUtils.GetCheckoutStatus(_doc, e.Id, out var owner);
                    if (status == CheckoutStatus.OwnedByOtherUser)
                    {
                        if (string.IsNullOrWhiteSpace(owner)) { try { owner = WorksharingUtils.GetWorksharingTooltipInfo(_doc, e.Id).Owner; } catch { owner = null; } }
                        owner = string.IsNullOrWhiteSpace(owner) ? "another user" : owner.Trim();
                        state = (false, "borrowed by " + owner, owner);
                    }
                    else
                    {
                        var updates = WorksharingUtils.GetModelUpdatesStatus(_doc, e.Id);
                        state = updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral
                            ? (false, "changed in the central model since your last Reload Latest", null)
                            : (true, null, null);
                    }
                }
                catch (Exception ex) { state = (false, "worksharing state unreadable: " + ex.Message, null); }
                _edit[id] = state;
                return state;
            }

            // ---- elements per group or category ---------------------------------------------------------------

            private List<Element> Targets(string group, string level)
            {
                if (level != "type") return Instances(group);
                var key = group + "/type";
                if (_targets.TryGetValue(key, out var cached)) return cached;
                var list = TypesOf(Instances(group));
                _targets[key] = list;
                return list;
            }

            private List<Element> TypesOf(List<Element> instances) =>
                instances.Select(e => e.GetTypeId()).Where(id => id != ElementId.InvalidElementId).Distinct()
                    .Select(TypeElement).Where(t => t != null).ToList();

            private Element TypeElement(ElementId id)
            {
                if (_types.TryGetValue(id.Value, out var t)) return t;
                t = _doc.GetElement(id);
                _types[id.Value] = t;
                return t;
            }

            /// <summary>The type's parameter for a row's definition (or name), looked up once per type.</summary>
            private Parameter TypeParameter(ElementId typeId, string lookupKey, Func<Element, Parameter> get)
            {
                var key = typeId.Value + "\n" + lookupKey;
                if (_typeParams.TryGetValue(key, out var p)) return p;
                var type = TypeElement(typeId);
                p = type == null ? null : get(type);
                _typeParams[key] = p;
                return p;
            }

            private List<Element> Instances(string group)
            {
                var key = group + "/instance";
                if (_targets.TryGetValue(key, out var cached)) return cached;
                List<Element> list;
                if (group == "project") list = _doc.ProjectInformation != null ? new List<Element> { _doc.ProjectInformation } : new List<Element>();
                else if (group == "rooms") list = new FilteredElementCollector(_doc).WherePasses(new ElementMulticategoryFilter(RoomCats)).WhereElementIsNotElementType().ToList();
                else if (group == "levels") list = new FilteredElementCollector(_doc).OfClass(typeof(Level)).ToList();
                else
                {
                    // Model categories that take bound parameters (not rooms or levels), filtered by Revit rather than element by element.
                    var cats = new List<BuiltInCategory>();
                    foreach (Category c in _doc.Settings.Categories)
                    {
                        var bic = c.BuiltInCategory;
                        if (c.CategoryType == CategoryType.Model && c.AllowsBoundParameters && bic != BuiltInCategory.INVALID
                            && bic != BuiltInCategory.OST_Levels && Array.IndexOf(RoomCats, bic) < 0) cats.Add(bic);
                    }
                    list = new List<Element>();
                    if (cats.Count > 0)
                    {
                        var skipped = new Dictionary<BuiltInCategory, int>();
                        foreach (var e in new FilteredElementCollector(_doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
                        {
                            if (!IsModelElement(e)) continue;
                            var bic = e.Category.BuiltInCategory;
                            if (ForeignCategory(bic)) { skipped[bic] = skipped.TryGetValue(bic, out var n) ? n + 1 : 1; continue; }
                            list.Add(e);
                        }
                        foreach (var kv in skipped) NoteForeign(kv.Key, kv.Value);
                    }
                }
                _targets[key] = list;
                return list;
            }

            /// <summary>The instances of one category, collected once.</summary>
            private List<Element> OfCategory(BuiltInCategory bic)
            {
                if (_byCategory.TryGetValue(bic, out var list)) return list;
                list = new FilteredElementCollector(_doc).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(e => e.Category != null && !e.ViewSpecific).ToList();
                _byCategory[bic] = list;
                return list;
            }

            /// <summary>
            /// Instances (or their types) of the row's categories. Without row disciplines (e.g. checker rows), categories
            /// another discipline owns are left out and counted as foreign.
            /// </summary>
            private List<Element> CategoryTargets(List<BuiltInCategory> bics, string level, bool skipForeign)
            {
                var set = string.Join("|", bics.Select(b => b.ToString()).OrderBy(n => n, StringComparer.Ordinal));
                var key = "cats:" + set + (skipForeign ? "/own" : "/all") + (level == "type" ? "/type" : "/instance");
                if (_targets.TryGetValue(key, out var cached)) return cached;
                List<Element> list;
                if (level == "type") list = TypesOf(CategoryTargets(bics, "instance", skipForeign));
                else
                {
                    list = new List<Element>();
                    foreach (var bic in bics)
                    {
                        var of = OfCategory(bic);
                        if (skipForeign && ForeignCategory(bic)) { NoteForeign(bic, of.Count); continue; }
                        list.AddRange(of);
                    }
                }
                _targets[key] = list;
                return list;
            }

            /// <summary>The category has owners (scope ownership) and the model's discipline is not one of them; once per category.</summary>
            private bool ForeignCategory(BuiltInCategory bic)
            {
                if (_discipline == null || _scope.Ownership.Count == 0) return false;
                if (_foreignCat.TryGetValue(bic, out var f)) return f;
                f = ParameterFillLogic.IsForeign(_scope, bic.ToString(), _discipline);
                _foreignCat[bic] = f;
                return f;
            }

            /// <summary>Foreign elements per category: the same category reached by several rows is counted once.</summary>
            private void NoteForeign(BuiltInCategory bic, int count)
            {
                var name = bic.ToString();
                if (count > 0) _foreign[name] = _foreign.TryGetValue(name, out var n) ? Math.Max(n, count) : count;
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

            /// <summary>The binding of that name, preferring one that binds a row category (or, without categories, a category of the group).</summary>
            private (InternalDefinition, ElementBinding) BindingFor(string name, string group, List<BuiltInCategory> bics)
            {
                if (!Bindings().TryGetValue(name, out var l) || l.Count == 0) return (null, null);
                if (bics.Count > 0) { foreach (var x in l) if (bics.Any(b => Binds(x.Item2, b))) return x; }
                else foreach (var x in l) if (GroupCategories(x.Item2, group).Count > 0) return x;
                return l[0];
            }

            private static bool Binds(ElementBinding b, BuiltInCategory bic)
            {
                foreach (Category c in b.Categories) if (c.BuiltInCategory == bic) return true;
                return false;
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
