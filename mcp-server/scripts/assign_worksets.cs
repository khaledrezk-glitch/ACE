// @description: Workset assignment per the BEP. Puts elements on worksets by rules: category, function (wall function, structural, MEP system, framing type), family / type name, level, zone (scope box) or room department, parameter value. The first matching rule wins; workset names can use {level}, {zone}, {category}. check_only: true only reports what is on the wrong workset. Elements borrowed by others are skipped. Usually run through the assign_worksets tool, which loads the BEP rules file.
// @mode: auto
// @inputs: {"rules": [{"workset": "Shared Levels and Grids", "category": ["Levels", "Grids"]}, {"workset": "ARC_Exterior", "category": ["Walls"], "function": "Exterior"}], "default_workset": "", "only_workset1": false, "create_missing": false, "check_only": false}

var worksetParam = BuiltInParameter.ELEM_PARTITION_PARAM;
if (!doc.IsWorkshared) throw new Exception("This model is not workshared: there are no worksets to assign (Collaborate > Worksets enables worksharing).");
var checkOnly = ctx.Bool("check_only", false);
var onlyWorkset1 = ctx.Bool("only_workset1", false);
var createMissing = ctx.Bool("create_missing", false);
var defaultWorkset = ctx.Str("default_workset", "");
var rulesNode = ctx.Args["rules"] as System.Text.Json.Nodes.JsonArray;
if (rulesNode == null || rulesNode.Count == 0) throw new Exception("No workset rules given. Pass 'rules' (from the BEP rules file, worksets.json).");

// ---- rules ----
List<string> ListOf(System.Text.Json.Nodes.JsonNode n) =>
    n == null ? new List<string>() : n is System.Text.Json.Nodes.JsonArray a ? a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new List<string> { n.ToString() };
System.Text.RegularExpressions.Regex Rx(System.Text.Json.Nodes.JsonNode n) =>
    n == null || string.IsNullOrWhiteSpace(n.ToString()) ? null : new System.Text.RegularExpressions.Regex(n.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var rules = rulesNode.OfType<System.Text.Json.Nodes.JsonObject>().Select((r, i) => new
{
    Index = i + 1,
    Workset = r["workset"]?.ToString(),
    Categories = ListOf(r["category"]),
    Function = Rx(r["function"]), Family = Rx(r["family"]), Type = Rx(r["type"]),
    Level = Rx(r["level"]), Zone = Rx(r["zone"]), Department = Rx(r["department"]),
    Param = r["parameter"]?.ToString(), Value = Rx(r["value"]),
}).Where(r => !string.IsNullOrWhiteSpace(r.Workset)).ToList();

// ---- worksets ----
var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToDictionary(w => w.Name, w => w, StringComparer.OrdinalIgnoreCase);
var ws1 = worksets.Values.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase));
var created = new List<string>(); var missing = new HashSet<string>();
Workset WorksetNamed(string name)
{
    if (worksets.TryGetValue(name, out var w)) return w;
    if (!createMissing || checkOnly) { missing.Add(name); return null; }
    if (!WorksetTable.IsWorksetNameUnique(doc, name)) { missing.Add(name); return null; }
    w = Workset.Create(doc, name);
    worksets[name] = w; created.Add(name);
    return w;
}

// ---- location: zones (scope boxes) and rooms ----
var zones = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
    .Select(z => (z.Name, Box: z.get_BoundingBox(null))).Where(z => z.Box != null).ToList();
XYZ Centre(Element e)
{
    if (e.Location is LocationPoint lp) return lp.Point;
    if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
    var bb = e.get_BoundingBox(null);
    return bb == null ? null : (bb.Min + bb.Max) / 2;
}
string ZoneOf(XYZ p)
{
    if (p == null) return null;
    foreach (var z in zones)
        if (p.X >= z.Box.Min.X && p.X <= z.Box.Max.X && p.Y >= z.Box.Min.Y && p.Y <= z.Box.Max.Y && p.Z >= z.Box.Min.Z - 1 && p.Z <= z.Box.Max.Z + 1) return z.Name;
    return null;
}
var needsDepartment = rules.Any(r => r.Department != null);
string DepartmentOf(Element e, XYZ p)
{
    if (!needsDepartment || p == null) return null;
    Autodesk.Revit.DB.Architecture.Room room = null;
    try { room = e is FamilyInstance fi ? fi.Room : doc.GetRoomAtPoint(p); } catch { }
    return room?.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString();
}

// ---- function: what the element does ----
string FunctionOf(Element e)
{
    var parts = new List<string>();
    var type = doc.GetElement(e.GetTypeId());
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

bool CategoryMatches(Element e, List<string> cats)
{
    if (cats.Count == 0) return true;
    var name = e.Category?.Name ?? "";
    var bic = e.Category != null ? ((BuiltInCategory)e.Category.Id.Value).ToString() : "";
    return cats.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Equals(bic, StringComparison.OrdinalIgnoreCase) || ("OST_" + c.Replace(" ", "")).Equals(bic, StringComparison.OrdinalIgnoreCase));
}

// ---- elements whose workset can be set ----
var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
    .Where(e => e.Category != null && !(e is View) && (e.Category.CategoryType == CategoryType.Model || e is Level || e is Grid || e is ReferencePlane || e.Category.Id.Value == (long)BuiltInCategory.OST_VolumeOfInterest))
    .Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null))   // nested parts follow their parent
    .Where(e => { var p = e.get_Parameter(worksetParam); return p != null && !p.IsReadOnly; })
    .ToList();

var byTarget = new Dictionary<string, int>(); var noRule = new Dictionary<string, int>();
var moved = new List<long>(); var wrong = new List<string>(); var blocked = new List<long>();
int correct = 0, skippedNotWs1 = 0;
foreach (var e in elements)
{
    var current = e.WorksetId;
    if (onlyWorkset1 && (ws1 == null || current != ws1.Id)) { skippedNotWs1++; continue; }
    string levelName = null;
    try { levelName = (doc.GetElement(e.LevelId) as Level)?.Name; } catch { }
    if (levelName == null) levelName = (doc.GetElement(e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Name;
    var centre = Centre(e);
    var zone = rules.Any(r => r.Zone != null) || rules.Any(r => r.Workset.Contains("{zone}")) ? ZoneOf(centre) : null;
    var familyName = e is FamilyInstance fam ? fam.Symbol?.FamilyName : doc.GetElement(e.GetTypeId()) is ElementType et ? et.FamilyName : null;
    var typeName = doc.GetElement(e.GetTypeId())?.Name;
    string function = null, department = null;
    var rule = rules.FirstOrDefault(r =>
        CategoryMatches(e, r.Categories) &&
        (r.Function == null || r.Function.IsMatch(function ??= FunctionOf(e))) &&
        (r.Family == null || r.Family.IsMatch(familyName ?? "")) &&
        (r.Type == null || r.Type.IsMatch(typeName ?? "")) &&
        (r.Level == null || r.Level.IsMatch(levelName ?? "")) &&
        (r.Zone == null || r.Zone.IsMatch(zone ?? "")) &&
        (r.Department == null || r.Department.IsMatch(department ??= DepartmentOf(e, centre) ?? "")) &&
        (r.Param == null || (r.Value ?? new System.Text.RegularExpressions.Regex(".+")).IsMatch(e.LookupParameter(r.Param)?.AsValueString() ?? e.LookupParameter(r.Param)?.AsString() ?? "")));
    var target = rule?.Workset ?? (string.IsNullOrWhiteSpace(defaultWorkset) ? null : defaultWorkset);
    if (target == null) { var k = e.Category.Name; noRule[k] = noRule.TryGetValue(k, out var n0) ? n0 + 1 : 1; continue; }
    target = target.Replace("{level}", levelName ?? "No level").Replace("{zone}", zone ?? "No zone").Replace("{category}", e.Category.Name);
    byTarget[target] = byTarget.TryGetValue(target, out var n1) ? n1 + 1 : 1;
    var ws = WorksetNamed(target);
    if (ws == null) continue;
    if (ws.Id == current) { correct++; continue; }
    if (wrong.Count < 60) wrong.Add($"{e.Category.Name} {e.Id.Value} ({typeName}): {doc.GetWorksetTable().GetWorkset(current)?.Name} -> {target}{(rule != null ? $" [rule {rule.Index}]" : " [default]")}");
    if (checkOnly) { moved.Add(e.Id.Value); continue; }
    if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) { blocked.Add(e.Id.Value); continue; }
    try { e.get_Parameter(worksetParam).Set(ws.Id.IntegerValue); moved.Add(e.Id.Value); }
    catch { blocked.Add(e.Id.Value); }
}

return new
{
    mode = checkOnly ? "check only (nothing moved)" : onlyWorkset1 ? "assign (only elements on Workset1)" : "assign",
    elementsChecked = elements.Count - skippedNotWs1,
    alreadyOnTheRightWorkset = correct,
    wrongWorkset = checkOnly ? moved.Count : (int?)null,
    moved = checkOnly ? (int?)null : moved.Count,
    byWorkset = byTarget.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
    noMatchingRule = noRule.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
    worksetsMissing = missing.ToList(),
    worksetsCreated = created,
    borrowedByOthers = blocked.Take(100).ToList(),
    examples = wrong,
    ids = checkOnly ? new List<long>() : moved,
    wrongIds = checkOnly ? moved.Take(2000).ToList() : new List<long>(),
    note = (missing.Count > 0 ? "Some target worksets do not exist: create them (create_missing: true) or fix the names in the rules. " : "") +
           (blocked.Count > 0 ? "Elements borrowed by other users were skipped: ask them to relinquish, then run again. " : "") +
           "The first matching rule wins; categories without a rule stay where they are unless default_workset is set.",
};
