// @description: List doors whose clear width is below a minimum (default 900 mm), grouped by level, reading width from the instance or its type. Read-only.
// @mode: readonly
// @inputs: {"min_width_mm": 900, "width_parameter": "optional; default tries DOOR_WIDTH, FAMILY_WIDTH_PARAM, then \"Width\" / \"Door Width\" / \"Opening Width\""}

var minMm = ctx.Num("min_width_mm", 900);
var paramName = ctx.Str("width_parameter");

double? Read(Parameter p) => p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 1e-6 ? p.AsDouble() : null;

double? WidthFt(FamilyInstance d)
{
    // Order: explicit parameter name, Revit's built-in width parameters, then the common family
    // parameter "Width" (many door families use a plain family parameter instead of DOOR_WIDTH).
    // Instance first, then type. Zero widths (e.g. curtain wall panel doors) count as unreadable.
    var names = paramName != null ? new[] { paramName } : new[] { "Width", "Door Width", "Opening Width" };
    if (paramName != null)
        return Read(d.LookupParameter(paramName)) ?? Read(d.Symbol.LookupParameter(paramName));
    return Read(d.get_Parameter(BuiltInParameter.DOOR_WIDTH)) ?? Read(d.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH))
        ?? Read(d.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)) ?? Read(d.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM))
        ?? names.Select(n => Read(d.LookupParameter(n)) ?? Read(d.Symbol.LookupParameter(n))).FirstOrDefault(v => v != null);
}

var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
var noWidth = new List<long>();
var narrow = new List<(FamilyInstance door, double mm)>();
foreach (var d in doors)
{
    var w = WidthFt(d);
    if (w == null) { noWidth.Add(d.Id.Value); continue; }
    var mm = ctx.ToMm(w.Value);
    if (mm < minMm - 0.5) narrow.Add((d, mm));
}

return new
{
    minimumMm = minMm,
    doorsChecked = doors.Count,
    belowMinimum = narrow.Count,
    byLevel = narrow.GroupBy(x => doc.GetElement(x.door.LevelId)?.Name ?? "(no level)").OrderBy(g => g.Key).Select(g => new
    {
        level = g.Key,
        count = g.Count(),
        doors = g.OrderBy(x => x.mm).Take(100).Select(x => new
        {
            id = x.door.Id.Value,
            mark = x.door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
            family = x.door.Symbol.FamilyName,
            type = x.door.Symbol.Name,
            widthMm = Math.Round(x.mm),
        }).ToList(),
    }).ToList(),
    doorsWithoutReadableWidth = noWidth.Count,
    sampleIdsWithoutWidth = noWidth.Take(20).ToList(),
    note = noWidth.Count > 0 ? "Doors without a readable width (often curtain-wall panel doors or families with a differently named width parameter): pass width_parameter, or check them with describe_category." : null,
};
