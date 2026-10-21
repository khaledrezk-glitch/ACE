using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Workset rules from the BEP: category, function (wall function, structural, MEP system), family / type name, level,
    /// zone (scope box), room department or a parameter value; the first matching rule wins and names can use {level},
    /// {zone}, {category}. One engine for the model check (which reports elements on the wrong workset) and for
    /// assign_worksets (which moves them), so both always agree (concept D7).
    /// </summary>
    internal sealed class WorksetRules
    {
        internal sealed class Rule
        {
            public int Index;
            public string Workset;
            public List<string> Categories = new List<string>();
            public Regex Function, Family, Type, Level, Zone, Department, Value;
            public string Param;
            public string Material;      // "concrete" | "steel" | "other" (WorksetPlanLogic.MaterialClass): the rule skips other classes
        }

        public List<Rule> Items = new List<Rule>();
        public string DefaultWorkset;
        public string Name;
        public string Source;
        /// <summary>Rules left out because their models key (G9) does not name this model: (id or workset, why).</summary>
        public List<(string Rule, string Why)> Skipped = new List<(string Rule, string Why)>();
        /// <summary>Rules kept although their models key does not name this model: no modelsScope "only" (saved before 1.14.10).</summary>
        public List<string> Legacy = new List<string>();
        public int Total;

        /// <summary>The skipped-rules and legacy-models lines for this model (G9), one per line; null when neither applies.</summary>
        public string ModelsText(string model)
        {
            var lines = new[] { RuleModels.SkippedText(Skipped, model, Total), RuleModels.LegacyText(Legacy.Count) }.Where(l => l != null).ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>The names the open model is known by, for a rule's models key (title, central / file name, cloud ids).</summary>
        public static List<string> ModelNames(Document doc) => RuleModels.Names(doc.Title, ProjectRules.Keys(doc));

        public bool NeedsZone => Items.Any(r => r.Zone != null || r.Workset.Contains("{zone}"));
        public bool NeedsDepartment => Items.Any(r => r.Department != null);

        /// <summary>
        /// The rules; with <paramref name="modelNames"/>, a rule with modelsScope "only" whose models key does not name the
        /// model is skipped; one without the marker is kept (as before) and counted in <see cref="Legacy"/>.
        /// </summary>
        public static WorksetRules Parse(JsonArray rules, string defaultWorkset = null, IList<string> modelNames = null)
        {
            List<string> ListOf(JsonNode n) =>
                n == null ? new List<string>() : n is JsonArray a ? a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new List<string> { n.ToString() };
            Regex Rx(JsonNode n) => n == null || string.IsNullOrWhiteSpace(n.ToString()) ? null : new Regex(n.ToString(), RegexOptions.IgnoreCase);
            var r = new WorksetRules { DefaultWorkset = string.IsNullOrWhiteSpace(defaultWorkset) ? null : defaultWorkset };
            var i = 0;
            foreach (var o in (rules ?? new JsonArray()).OfType<JsonObject>())
            {
                i++;
                var ws = o["workset"]?.ToString();
                if (string.IsNullOrWhiteSpace(ws)) continue;
                r.Total++;
                if (modelNames != null && !RuleModels.Applies(o["models"], modelNames, out var why))
                {
                    if (RuleModels.Limits(o["modelsScope"])) { r.Skipped.Add((o["id"]?.ToString() ?? ws, why)); continue; }
                    r.Legacy.Add(o["id"]?.ToString() ?? ws);
                }
                r.Items.Add(new Rule
                {
                    Index = i, Workset = ws, Categories = ListOf(o["category"]),
                    Function = Rx(o["function"]), Family = Rx(o["family"]), Type = Rx(o["type"]), Level = Rx(o["level"]),
                    Zone = Rx(o["zone"]), Department = Rx(o["department"]), Param = o["parameter"]?.ToString(), Value = Rx(o["value"]),
                    Material = string.IsNullOrWhiteSpace(o["material"]?.ToString()) ? null : o["material"].ToString().Trim().ToLowerInvariant(),
                });
            }
            return r;
        }

        /// <summary>
        /// The rules in force: the project's "worksets" section first, then the office worksets.json (config.json
        /// "worksetRules", else %APPDATA%\ACE-RevitMCP\worksets.json), merged by <see cref="ProjectRules"/>. Null when
        /// there are none (the model check then skips the BEP check). Never the packaged example.
        /// </summary>
        public static WorksetRules Load(ProjectRules rules, IList<string> modelNames = null)
        {
            try
            {
                var json = (rules ?? ProjectRules.Office()).Section("worksets");
                if (json?["rules"] is not JsonArray list) return null;
                var r = Parse(list, json["default_workset"]?.ToString(), modelNames);
                r.Name = json["name"]?.ToString() ?? "worksets";
                r.Source = (rules ?? ProjectRules.Office()).SourceOf("worksets");
                return r.Items.Count == 0 ? null : r;
            }
            catch (Exception ex) { Log.Warn($"Workset rules: {ex.Message}"); return null; }
        }

        /// <summary>Where elements are: zones (scope boxes), rooms, functions; looked up once per run.</summary>
        internal sealed class Context
        {
            private readonly Document _doc;
            private readonly List<(string Name, BoundingBoxXYZ Box)> _zones;
            public Context(Document doc, bool zones)
            {
                _doc = doc;
                _zones = zones
                    ? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                        .Select(z => (z.Name, Box: z.get_BoundingBox(null))).Where(z => z.Box != null).ToList()
                    : new List<(string, BoundingBoxXYZ)>();
            }

            public static XYZ Centre(Element e)
            {
                if (e.Location is LocationPoint lp) return lp.Point;
                if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
                var bb = e.get_BoundingBox(null);
                return bb == null ? null : (bb.Min + bb.Max) / 2;
            }

            public string ZoneOf(XYZ p)
            {
                if (p == null) return null;
                foreach (var z in _zones)
                    if (p.X >= z.Box.Min.X && p.X <= z.Box.Max.X && p.Y >= z.Box.Min.Y && p.Y <= z.Box.Max.Y && p.Z >= z.Box.Min.Z - 1 && p.Z <= z.Box.Max.Z + 1) return z.Name;
                return null;
            }

            public string DepartmentOf(Element e, XYZ p)
            {
                if (p == null) return null;
                Autodesk.Revit.DB.Architecture.Room room = null;
                try { room = e is FamilyInstance fi ? fi.Room : _doc.GetRoomAtPoint(p); } catch { }
                return room?.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString();
            }

            public string FunctionOf(Element e)
            {
                var parts = new List<string>();
                var type = _doc.GetElement(e.GetTypeId());
                var fp = type?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                if (fp != null && fp.StorageType == StorageType.Integer)
                    parts.Add(e is Wall ? ((WallFunction)fp.AsInteger()).ToString() : fp.AsValueString() ?? "");
                if (e is Wall w && w.StructuralUsage != Autodesk.Revit.DB.Structure.StructuralWallUsage.NonBearing) parts.Add("Structural");
                if (e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1) parts.Add("Structural");
                if (e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1) parts.Add("Structural");
                if (e is FamilyInstance f && f.StructuralType != Autodesk.Revit.DB.Structure.StructuralType.NonStructural) parts.Add(f.StructuralType.ToString());
                var sys = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString();
                if (!string.IsNullOrEmpty(sys)) parts.Add(sys);
                var sysName = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();
                if (!string.IsNullOrEmpty(sysName)) parts.Add(sysName);
                return string.Join(" ", parts);
            }
        }

        private static bool CategoryMatches(Element e, List<string> cats)
        {
            if (cats.Count == 0) return true;
            if (e is ImportInstance && cats.Any(c => c.Equals(WorksetPlanLogic.CadImports, StringComparison.OrdinalIgnoreCase))) return true;   // as the workset facts name them
            var name = e.Category?.Name ?? "";
            var bic = e.Category != null ? ((BuiltInCategory)e.Category.Id.Value).ToString() : "";
            return cats.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Equals(bic, StringComparison.OrdinalIgnoreCase) || ("OST_" + c.Replace(" ", "")).Equals(bic, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The elements whose workset can be set: model elements, levels, grids, reference planes, scope boxes (not nested parts).</summary>
        public static List<Element> Candidates(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ElementOwnerViewFilter(ElementId.InvalidElementId))
                .Where(e => e.Category != null && !(e is View) && (e.Category.CategoryType == CategoryType.Model || e is Level || e is Grid || e is ReferencePlane || e.Category.Id.Value == (long)BuiltInCategory.OST_VolumeOfInterest))
                .Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null))   // nested parts follow their parent
                .Where(e => { var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM); return p != null && !p.IsReadOnly; })
                .ToList();

        /// <summary>The target workset name for an element and the rule that gave it (0 = default), or null when no rule applies.</summary>
        public (string Workset, int Rule, string TypeName)? TargetFor(Document doc, Element e, Context ctx)
        {
            string levelName = null;
            try { levelName = (doc.GetElement(e.LevelId) as Level)?.Name; } catch { }
            if (levelName == null) levelName = (doc.GetElement(e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Name;
            var centre = NeedsZone || NeedsDepartment ? Context.Centre(e) : null;
            var zone = NeedsZone ? ctx.ZoneOf(centre) : null;
            var type = doc.GetElement(e.GetTypeId()) as ElementType;
            var familyName = e is FamilyInstance fam ? fam.Symbol?.FamilyName : type?.FamilyName;
            var typeName = type?.Name;
            string function = null, department = null, material = null;
            string MaterialOf() { try { return WorksetPlanLogic.MaterialClass(Coordination.StructuralMaterial.Of(e)); } catch { return "other"; } }
            var rule = Items.FirstOrDefault(r =>
                CategoryMatches(e, r.Categories) &&
                (r.Material == null || r.Material == (material ??= e is RevitLinkInstance || e is ImportInstance ? "other" : MaterialOf())) &&
                (r.Function == null || r.Function.IsMatch(function ??= ctx.FunctionOf(e))) &&
                (r.Family == null || r.Family.IsMatch(familyName ?? "")) &&
                (r.Type == null || r.Type.IsMatch(typeName ?? "")) &&
                (r.Level == null || r.Level.IsMatch(levelName ?? "")) &&
                (r.Zone == null || r.Zone.IsMatch(zone ?? "")) &&
                (r.Department == null || r.Department.IsMatch(department ??= ctx.DepartmentOf(e, centre) ?? "")) &&
                (r.Param == null || (r.Value ?? new Regex(".+")).IsMatch(e.LookupParameter(r.Param)?.AsValueString() ?? e.LookupParameter(r.Param)?.AsString() ?? "")));
            var target = rule?.Workset ?? DefaultWorkset;
            if (target == null) return null;
            target = target.Replace("{level}", levelName ?? "No level").Replace("{zone}", zone ?? "No zone").Replace("{category}", e.Category?.Name ?? "");
            return (target, rule?.Index ?? 0, typeName);
        }

        internal sealed class Options
        {
            public bool PlanOnly, OnlyWorkset1, CreateMissing;
            public Func<Element, bool> CanEdit = _ => true;
        }

        /// <summary>Checks (plan only) or moves elements onto their workset. Inside a transaction when not plan only.</summary>
        public JsonObject Apply(Document doc, Options opt)
        {
            if (!doc.IsWorkshared) throw new Bridge.CommandException("This model is not workshared: there are no worksets to assign (Collaborate > Worksets enables worksharing).");
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToDictionary(w => w.Name, w => w, StringComparer.OrdinalIgnoreCase);
            var ws1 = worksets.Values.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase));
            var created = new List<string>(); var missing = new HashSet<string>();
            Workset Named(string name)
            {
                if (worksets.TryGetValue(name, out var w)) return w;
                // A real name with leading or trailing spaces ("NAME ") matches a rule's "NAME".
                w = worksets.Values.FirstOrDefault(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (w != null) { worksets[name] = w; return w; }
                if (!opt.CreateMissing || opt.PlanOnly || !WorksetTable.IsWorksetNameUnique(doc, name)) { missing.Add(name); return null; }
                w = Workset.Create(doc, name);
                worksets[name] = w; created.Add(name);
                return w;
            }
            var table = doc.GetWorksetTable();
            var ctx = new Context(doc, NeedsZone);
            var elements = Candidates(doc);
            var byTarget = new Dictionary<string, int>(); var noRule = new Dictionary<string, int>();
            var moved = new List<long>(); var wrong = new List<string>(); var blocked = new List<long>();
            int correct = 0, skippedNotWs1 = 0;
            foreach (var e in elements)
            {
                var current = e.WorksetId;
                if (opt.OnlyWorkset1 && (ws1 == null || current != ws1.Id)) { skippedNotWs1++; continue; }
                var t = TargetFor(doc, e, ctx);
                if (t == null) { var k = e.Category.Name; noRule[k] = noRule.TryGetValue(k, out var n0) ? n0 + 1 : 1; continue; }
                var (target, rule, typeName) = t.Value;
                byTarget[target] = byTarget.TryGetValue(target, out var n1) ? n1 + 1 : 1;
                var ws = Named(target);
                if (ws == null) continue;
                if (ws.Id == current) { correct++; continue; }
                if (wrong.Count < 60) wrong.Add($"{e.Category.Name} {e.Id.Value} ({typeName}): {table.GetWorkset(current)?.Name} -> {target}{(rule > 0 ? $" [rule {rule}]" : " [default]")}");
                if (opt.PlanOnly) { moved.Add(e.Id.Value); continue; }
                if (!opt.CanEdit(e)) { blocked.Add(e.Id.Value); continue; }
                try { e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM).Set(ws.Id.IntegerValue); moved.Add(e.Id.Value); }
                catch { blocked.Add(e.Id.Value); }
            }

            JsonObject Dict(Dictionary<string, int> d) => new JsonObject(d.OrderByDescending(kv => kv.Value).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));
            JsonArray Arr<T>(IEnumerable<T> items) => new JsonArray(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
            return new JsonObject
            {
                ["rules"] = $"{Name ?? "rules"} ({Source ?? "given in the request"}), {Items.Count} rules",
                ["rulesSkipped"] = ModelsText(doc.Title),
                ["mode"] = opt.PlanOnly ? "check only (nothing moved)" : opt.OnlyWorkset1 ? "assign (only elements on Workset1)" : "assign",
                ["elementsChecked"] = elements.Count - skippedNotWs1,
                ["alreadyOnTheRightWorkset"] = correct,
                [opt.PlanOnly ? "wrongWorkset" : "moved"] = moved.Count,
                ["byWorkset"] = Dict(byTarget),
                ["noMatchingRule"] = Dict(noRule),
                ["worksetsMissing"] = Arr(missing),
                ["worksetsCreated"] = Arr(created),
                ["inUseByOthers"] = Arr(blocked.Take(100)),
                ["examples"] = Arr(wrong),
                [opt.PlanOnly ? "wrongIds" : "ids"] = Arr(moved.Take(2000)),
                ["note"] = (missing.Count > 0 ? "Some target worksets do not exist: create them (create_missing: true) or fix the names in the rules. " : "") +
                           (blocked.Count > 0 ? "Elements in use by others, or changed in central since your last Reload Latest, were skipped: reload latest or ask them to relinquish, then run again. " : "") +
                           "The first matching rule wins; categories without a rule stay where they are unless default_workset is set.",
            };
        }
    }
}
