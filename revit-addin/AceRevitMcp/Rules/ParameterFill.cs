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
            public string Guid;                                       // expected GUID (null: not checked); the first of Guids
            public List<string> Guids = new List<string>();          // every accepted GUID (guid + guids: a client file may list one name with several)
            public List<string> Disciplines = new List<string>();   // discipline codes the row applies to (empty: every model)
            public string Key;                                        // the caller's row key, echoed
        }

        internal sealed class RowResult
        {
            public string Group;
            public int Checked, Missing, Blank, Filled, Na, OnType;
            public int WhitespaceOnly;   // blank text that holds only spaces (still blank; counted so the plan can say it)
            public string StorageType;   // null when no element has the parameter
            public bool? InGeneral;      // Project Information rows only
            public bool OutOfScope;      // the row's disciplines exclude the model's discipline: not scanned
            public bool Unmapped;        // categories given, none of them a Revit category: not scanned
            public bool? GuidMatch;      // null: no expected GUID or no shared definition
            public int Unbound;          // row categories the binding does not cover
            public bool? WrongLevel;     // bound as type for an instance row, or the reverse
            public bool BuiltIn;         // a Revit built-in parameter: elements without it cannot get it by a binding
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
                var guids = new List<string>();
                var one = Text(o, "guid")?.Trim();
                if (!string.IsNullOrEmpty(one)) guids.Add(one);
                foreach (var g in TextList(o, "guids") ?? new List<string>())
                    if (!guids.Contains(g, StringComparer.OrdinalIgnoreCase)) guids.Add(g);
                list.Add(new Row
                {
                    Name = name, Group = group, Level = level, Categories = cats,
                    Guid = guids.Count > 0 ? guids[0] : null,
                    Guids = guids,
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
        /// The row's status, first rule that applies: out-of-scope, unmapped, no-elements, not-available (a built-in
        /// parameter some checked elements do not have: no binding can add it), not-bound (no checked element has it),
        /// guid-differs (the model's GUID differs from the expected one), partly-bound (some elements lack it, or the binding misses row categories), blanks,
        /// complete-with-na, complete.
        /// </summary>
        public static string Status(RowResult r)
        {
            if (r.OutOfScope) return "out-of-scope";
            if (r.Unmapped) return "unmapped";
            if (r.Checked == 0) return "no-elements";
            if (r.BuiltIn && r.Missing > 0) return "not-available";
            if (r.Missing == r.Checked) return "not-bound";
            if (r.GuidMatch == false) return "guid-differs";
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
            ["guidDiffers"] = rows.Count(r => r.GuidMatch == false),
            ["notBound"] = rows.Count(r => (r.Status ?? Status(r)) == "not-bound"),
            ["wrongLevel"] = rows.Count(r => r.WrongLevel == true),
            ["foreign"] = foreign,
            ["notAvailable"] = rows.Count(r => (r.Status ?? Status(r)) == "not-available"),
        };

        /// <summary>Any of the expected GUIDs matches the model's: null when there is none or the model has no GUID.</summary>
        public static bool? GuidMatchesAny(IList<string> expected, string model)
        {
            bool? result = null;
            foreach (var e in expected ?? new List<string>())
            {
                var m = GuidMatches(e, model);
                if (m == true) return true;
                if (m == false) result = false;
            }
            return result;
        }

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
    /// models, ownership }, na_values (["NA", "N/A"]), all_documents, fill, value ("NA"), derive, bind, impact, register,
    /// block_on_borrowed (true), expect_model, expect_version, max_ids (20), check_only, dry_run }. Without fill, derive or
    /// bind a read-only check of the active model (or every open project model; links excluded), with a pre-flight of each
    /// model (and, when asked, the impact scan and the missing register per model). With any of them one plan on the
    /// active model (ParameterFill.RunPlan): blockers first, then bindings, derived values and the fill value, as a native
    /// change (preview, then ONE undo step) with a self-check before it is kept.
    /// </summary>
    internal static partial class ParameterFill
    {
        private static readonly BuiltInCategory[] RoomCats = { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces };

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            // A write: the NA fill, derive rules or bindings (one plan, ONE undo step); anything else reads only.
            var write = Args.Bool(args, "fill") || (args["derive"] as JsonArray)?.Count > 0 || (args["bind"] as JsonArray)?.Count > 0
                || (args["rebind"] as JsonArray)?.Count > 0;
            var expect = Args.Str(args, "expect_model");
            if (write && expect != null)
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

            if (!write)
            {
                var active = Args.RequireDoc(app);
                var docs = new List<Document> { active };
                if (Args.Bool(args, "all_documents"))
                    docs.AddRange(app.Application.Documents.Cast<Document>().Where(d => !d.Equals(active) && !d.IsLinked && !d.IsFamilyDocument));
                var withImpact = Args.Bool(args, "impact");
                var withRegister = Args.Bool(args, "register");
                JsonNode Doc(Document d)
                {
                    var scan = new Scan(d, rows, maxIds, scope, na);
                    var report = scan.Report(d.Equals(active));
                    if (withImpact) report["impact"] = ParameterImpact.Scan(d, scan.ImpactTargets(null, null));
                    if (withRegister) report["register"] = scan.Register();
                    return report;
                }
                return new JsonObject
                {
                    ["documents"] = new JsonArray(docs.Select(Doc).ToArray()),
                    ["skippedRows"] = skippedRows,
                };
            }

            return RunPlan(app, args, rows, skippedRows, maxIds, scope, na);
        }

        /// <summary>One model's answer for every row (elements per category, types and worksharing states read once).</summary>
        private sealed partial class Scan
        {
            private sealed class Entry
            {
                public ParameterFillLogic.Row Row;
                public ParameterFillLogic.RowResult Result;
                public JsonObject Json;
                public List<Parameter> Blanks;
                public List<Parameter> NaParams;                   // text equal to an NA value (declared missing): derive may replace it
                public List<Element> MissingElems;                 // every checked element without the parameter
                public Dictionary<long, int> TypeInstances;        // type id -> instances that read the row's value from it
                public HashSet<long> ParamIds;                     // the parameter's element ids (or built-in id) in this model
                public Guid? SharedGuid;
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
            private readonly Dictionary<long, (bool Ok, string Why, string Owner, string Code)> _edit = new Dictionary<long, (bool, string, string, string)>();
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
                var missingElems = new List<Element>();
                var naParams = new List<Parameter>();
                var typeInstances = new Dictionary<long, int>();
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
                    if (row.Level == "type")
                        foreach (var kv in InstancesPerType(bics.Count > 0 ? CategoryTargets(bics, "instance", row.Disciplines.Count == 0) : Instances(row.Group))) typeInstances[kv.Key] = kv.Value;
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
                                typeInstances[tid.Value] = typeInstances.TryGetValue(tid.Value, out var ti) ? ti + 1 : 1;
                                if (!typesSeen.Add(tid.Value)) continue;   // the type's value is counted once
                                res.OnType++;
                                p = tp;
                            }
                        }
                        res.Checked++;
                        if (p == null)
                        {
                            res.Missing++;
                            missingElems.Add(e);
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
                            if (isText && !string.IsNullOrEmpty(text)) res.WhitespaceOnly++;
                            blanks.Add(p);
                            if (record) _blankHolders[holder.Id.Value] = holder;
                            if (blankIds.Count < _maxIds) blankIds.Add(holder.Id.Value);
                        }
                        else if (isText && ParameterFillLogic.IsNA(text, _na)) { res.Na++; naParams.Add(p); }
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
                res.BuiltIn = origin == "built-in";
                json["notAvailableCategories"] = res.BuiltIn && missingElems.Count > 0
                    ? new JsonArray(missingElems.Select(m => { try { return m.Category?.Name; } catch { return null; } })
                        .Where(n => n != null).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(20).Select(n => (JsonNode)n).ToArray())
                    : null;
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
                res.GuidMatch = ParameterFillLogic.GuidMatchesAny(row.Guids, modelGuid);
                json["modelGuid"] = modelGuid;
                json["guidMatch"] = res.GuidMatch;
                json["guidNote"] = res.GuidMatch == false ? "differs from the expected GUID in the requirement source; confirm which is current" : null;
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
                json["whitespaceOnly"] = res.WhitespaceOnly;
                json["filled"] = res.Filled;
                json["na"] = res.Na;
                json["onType"] = res.OnType;
                json["instancesAffected"] = instancesAffected;
                json["blankIds"] = blankIds;
                json["missingIds"] = missingIds;
                var paramIds = new HashSet<long>();
                if (def != null) paramIds.Add(def.Id.Value);
                Guid? guid = sharedGuid;
                try
                {
                    if (first != null) { paramIds.Add(first.Id.Value); if (guid == null && first.IsShared) guid = first.GUID; }
                }
                catch { /* ids are best effort, for the impact scan */ }
                if (guid == null && System.Guid.TryParse(row.Guid ?? "", out var rg)) guid = rg;
                foreach (var g in row.Guids.Select(t => System.Guid.TryParse(t, out var x) ? x : (Guid?)null).Where(x => x.HasValue).Concat(new[] { guid }).Where(x => x.HasValue).Distinct())
                    try { if (SharedParameterElement.Lookup(_doc, g.Value) is SharedParameterElement spe2) paramIds.Add(spe2.Id.Value); } catch { /* none */ }
                return new Entry
                {
                    Row = row, Result = res, Json = json, Blanks = blanks, NaParams = naParams, MissingElems = missingElems,
                    TypeInstances = typeInstances, ParamIds = paramIds, SharedGuid = guid,
                };
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
            private (bool Ok, string Why, string Owner, string Code) EditState(Element e)
            {
                if (!_workshared) return (true, null, null, null);
                var id = e.Id.Value;
                if (_edit.TryGetValue(id, out var known)) return known;
                (bool, string, string, string) state;
                try
                {
                    var status = WorksharingUtils.GetCheckoutStatus(_doc, e.Id, out var owner);
                    if (status == CheckoutStatus.OwnedByOtherUser)
                    {
                        if (string.IsNullOrWhiteSpace(owner)) { try { owner = WorksharingUtils.GetWorksharingTooltipInfo(_doc, e.Id).Owner; } catch { owner = null; } }
                        owner = string.IsNullOrWhiteSpace(owner) ? "another user" : owner.Trim();
                        state = (false, "borrowed by " + owner, owner, "borrowed");
                    }
                    else
                    {
                        var updates = WorksharingUtils.GetModelUpdatesStatus(_doc, e.Id);
                        state = updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral
                            ? (false, "changed in the central model since your last Reload Latest", null, "central")
                            : (true, null, null, null);
                    }
                }
                catch (Exception ex) { state = (false, "worksharing state unreadable: " + ex.Message, null, "unreadable"); }
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
