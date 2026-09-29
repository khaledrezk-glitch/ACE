// @description: Structure from ARC. Reads the architectural model (a link or another open model) and, in THIS (structural) model, creates structural columns for its columns and structural walls for its load-bearing walls, with matching sizes and mapped levels; skips what already exists. check_only: true only compares (ARC elements with no structural counterpart, and structural columns with no ARC column).
// @mode: auto
// @inputs: {"source": "optional part of the ARC model name; default: the first link/open model that looks architectural", "columns": true, "walls": true, "wall_pattern": "Concrete|Core|Shear|Structural|RC", "check_only": false, "match_tolerance_mm": 50}

const string Tag = "ACE from ARC";
var tolMm = ctx.Num("match_tolerance_mm", 50);
var tol = ctx.Mm(tolMm);
var checkOnly = ctx.Bool("check_only", false);
var wallPattern = new System.Text.RegularExpressions.Regex(ctx.Str("wall_pattern", "Concrete|Core|Shear|Structural|RC"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);

// ---- the ARC model: a link (with its placement) or another open model (through shared coordinates) ----
bool LooksArc(Document d) =>
    System.Text.RegularExpressions.Regex.IsMatch(d.Title, @"(^|[^A-Z])(ARC|ARCH|Architectural|-A-)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    || new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).GetElementCount() > 0;
var wanted = ctx.Str("source", null);
Document src = null; Transform toHere = Transform.Identity; string how = null;
foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
{
    var ld = li.GetLinkDocument();
    if (ld == null) continue;
    if (wanted != null ? ld.Title.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 : LooksArc(ld)) { src = ld; toHere = li.GetTotalTransform(); how = "link"; break; }
}
if (src == null)
    foreach (Document od in app.Documents)
    {
        if (od.Equals(doc) || od.IsLinked || od.IsFamilyDocument) continue;
        if (wanted != null ? od.Title.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 : LooksArc(od))
        {
            src = od; how = "open model (shared coordinates)";
            try { toHere = doc.ActiveProjectLocation.GetTotalTransform().Inverse.Multiply(od.ActiveProjectLocation.GetTotalTransform()); } catch { }
            break;
        }
    }
if (src == null) throw new Exception("No architectural model found: link it into this model or open it, or name it with 'source'.");
if (src.Equals(doc)) throw new Exception("Run this in the structural model, with the architectural model linked or open.");

// ---- levels: by name, else by height ----
var myLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
var unmatchedLevels = new HashSet<string>();
Level MapLevel(ElementId srcLevelId)
{
    if (!(src.GetElement(srcLevelId) is Level sl)) return null;
    var byName = myLevels.FirstOrDefault(l => string.Equals(l.Name, sl.Name, StringComparison.OrdinalIgnoreCase));
    if (byName != null) return byName;
    var z = toHere.OfPoint(new XYZ(0, 0, sl.ProjectElevation)).Z;
    var near = myLevels.OrderBy(l => Math.Abs(l.ProjectElevation - z)).FirstOrDefault();
    if (near != null && Math.Abs(near.ProjectElevation - z) <= tol) return near;
    unmatchedLevels.Add(sl.Name);
    return null;
}

double? Dim(Element e, params string[] names)
{
    foreach (var n in names)
    {
        var p = e.LookupParameter(n);
        if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
    }
    return null;
}

var created = new List<long>();
var report = new List<string>();
var missingTypes = new HashSet<string>();

// ---- columns ----
int colSrc = 0, colExisting = 0, colCreated = 0, colNoLevel = 0;
var myColumns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
var matchedMine = new HashSet<long>();
if (ctx.Bool("columns", true))
{
    var colTypes = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .Where(s => s.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns).ToList();
    FamilySymbol ColumnType(double b, double h)
    {
        bool Fits(FamilySymbol s)
        {
            var sb = Dim(s, "b", "Width", "Column Width"); var sh = Dim(s, "h", "Depth", "Column Depth");
            return sb != null && sh != null && ((Math.Abs(sb.Value - b) < ctx.Mm(5) && Math.Abs(sh.Value - h) < ctx.Mm(5)) || (Math.Abs(sb.Value - h) < ctx.Mm(5) && Math.Abs(sh.Value - b) < ctx.Mm(5)));
        }
        var fit = colTypes.FirstOrDefault(Fits);
        if (fit != null || checkOnly) return fit;
        // Duplicate a rectangular type that has b/h (or Width/Depth) parameters and size it.
        var template = colTypes.FirstOrDefault(s => Dim(s, "b", "Width") != null && Dim(s, "h", "Depth") != null);
        if (template == null) { missingTypes.Add($"structural column {Math.Round(ctx.ToMm(b))}x{Math.Round(ctx.ToMm(h))} mm (load a rectangular concrete column family)"); return null; }
        var name = $"{Math.Round(ctx.ToMm(b))} x {Math.Round(ctx.ToMm(h))}mm";
        var dup = (FamilySymbol)template.Duplicate(colTypes.Any(t => t.Name == name && t.Family.Id == template.Family.Id) ? name + " (ACE)" : name);
        (dup.LookupParameter("b") ?? dup.LookupParameter("Width"))?.Set(b);
        (dup.LookupParameter("h") ?? dup.LookupParameter("Depth"))?.Set(h);
        colTypes.Add(dup);
        return dup;
    }

    foreach (var ac in new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Columns).WhereElementIsNotElementType().OfType<FamilyInstance>())
    {
        if (!(ac.Location is LocationPoint lp)) continue;
        colSrc++;
        var p = toHere.OfPoint(lp.Point);
        var baseLevel = MapLevel(ac.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ac.LevelId);
        if (baseLevel == null) { colNoLevel++; continue; }
        var existing = myColumns.FirstOrDefault(m => m.Location is LocationPoint mp && Math.Abs(mp.Point.X - p.X) < tol && Math.Abs(mp.Point.Y - p.Y) < tol && m.LevelId == baseLevel.Id);
        if (existing != null) { colExisting++; matchedMine.Add(existing.Id.Value); continue; }
        var b = Dim(ac.Symbol, "Width", "b") ?? Dim(ac, "Width", "b"); var h = Dim(ac.Symbol, "Depth", "h") ?? Dim(ac, "Depth", "h");
        if (b == null || h == null)
        {
            var bb = ac.get_BoundingBox(null);
            if (bb == null) continue;
            b = bb.Max.X - bb.Min.X; h = bb.Max.Y - bb.Min.Y;
        }
        if (checkOnly) { report.Add($"ARC column {ac.Id.Value} ({ac.Symbol.Name}) on {baseLevel.Name} has no structural column"); continue; }
        var type = ColumnType(b.Value, h.Value);
        if (type == null) continue;
        if (!type.IsActive) type.Activate();
        var col = doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, baseLevel.ProjectElevation), type, baseLevel, Autodesk.Revit.DB.Structure.StructuralType.Column);
        var top = MapLevel(ac.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId);
        if (top != null) col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(top.Id);
        col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(ac.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
        col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(ac.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
        var angle = lp.Rotation + Math.Atan2(toHere.BasisX.Y, toHere.BasisX.X);
        if (Math.Abs(angle) > 1e-6) ElementTransformUtils.RotateElement(doc, col.Id, Line.CreateBound(new XYZ(p.X, p.Y, 0), new XYZ(p.X, p.Y, 1)), angle);
        col.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Tag);
        created.Add(col.Id.Value); colCreated++;
    }
}
var strOnly = myColumns.Where(m => !matchedMine.Contains(m.Id.Value)).ToList();

// ---- load-bearing walls ----
int wallSrc = 0, wallExisting = 0, wallCreated = 0, wallNoLevel = 0;
if (ctx.Bool("walls", true))
{
    var myWalls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
    var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
    WallType WallTypeFor(double width)
    {
        var fit = wallTypes.Where(t => Math.Abs(t.Width - width) < ctx.Mm(3)).OrderByDescending(t => wallPattern.IsMatch(t.Name)).FirstOrDefault();
        if (fit != null || checkOnly) return fit;
        var template = wallTypes.FirstOrDefault(t => wallPattern.IsMatch(t.Name) && t.GetCompoundStructure()?.LayerCount == 1);
        if (template == null) { missingTypes.Add($"structural wall {Math.Round(ctx.ToMm(width))} mm (no single-layer concrete wall type to copy)"); return null; }
        var dup = (WallType)template.Duplicate($"Concrete {Math.Round(ctx.ToMm(width))}mm (ACE)");
        var cs = dup.GetCompoundStructure();
        cs.SetLayerWidth(0, width);
        dup.SetCompoundStructure(cs);
        wallTypes.Add(dup);
        return dup;
    }
    foreach (var aw in new FilteredElementCollector(src).OfClass(typeof(Wall)).Cast<Wall>())
    {
        var significant = aw.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
        if (!significant && !wallPattern.IsMatch(aw.WallType.Name)) continue;
        if (!(aw.Location is LocationCurve lc) || aw.WallType.Kind != WallKind.Basic) continue;
        wallSrc++;
        var curve = lc.Curve.CreateTransformed(toHere);
        var level = MapLevel(aw.LevelId);
        if (level == null) { wallNoLevel++; continue; }
        var mid = curve.Evaluate(0.5, true);
        var exists = myWalls.Any(w => ((LocationCurve)w.Location).Curve is Curve c && c.Distance(new XYZ(mid.X, mid.Y, c.GetEndPoint(0).Z)) < tol && w.LevelId == level.Id);
        if (exists) { wallExisting++; continue; }
        if (checkOnly) { report.Add($"ARC wall {aw.Id.Value} ({aw.WallType.Name}) on {level.Name} has no structural wall"); continue; }
        var wt = WallTypeFor(aw.Width);
        if (wt == null) continue;
        var height = aw.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? ctx.Mm(3000);
        var offset = aw.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        var flat = curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - curve.GetEndPoint(0).Z)));
        var w = Wall.Create(doc, flat, wt.Id, level.Id, height, offset, false, true);
        var top = MapLevel(aw.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId);
        if (top != null) w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(top.Id);
        w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Tag);
        created.Add(w.Id.Value); wallCreated++;
    }
}

return new
{
    source = $"{src.Title} ({how})",
    mode = checkOnly ? "check only (nothing created)" : "create",
    columns = new { inArc = colSrc, alreadyInThisModel = colExisting, created = colCreated, levelNotFound = colNoLevel, structuralColumnsWithoutArcColumn = strOnly.Count },
    walls = new { loadBearingInArc = wallSrc, alreadyInThisModel = wallExisting, created = wallCreated, levelNotFound = wallNoLevel },
    unmatchedLevels = unmatchedLevels.ToList(),
    missingTypes = missingTypes.ToList(),
    differences = report.Take(60).ToList(),
    structuralOnlyColumnIds = strOnly.Take(60).Select(c => c.Id.Value).ToList(),
    ids = created,
    note = "New elements have Comments = 'ACE from ARC'. Matching tolerance " + tolMm + " mm. Preview with preview_image to see them before applying.",
};
