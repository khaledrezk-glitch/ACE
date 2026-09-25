// @description: List doors whose clear width is below a minimum (default 900 mm), grouped by level, reading width from the instance or its type. Read-only.
// @mode: readonly
// @inputs: {"min_width_mm": 900, "width_parameter": "optional, default Width (FAMILY_WIDTH_PARAM / DOOR_WIDTH)"}

var minMm = ctx.Num("min_width_mm", 900);
var paramName = ctx.Str("width_parameter");

double? WidthFt(FamilyInstance d)
{
    Parameter p = null;
    if (paramName != null) p = d.LookupParameter(paramName) ?? d.Symbol.LookupParameter(paramName);
    p ??= d.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? d.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)
        ?? d.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) ?? d.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
    return p != null && p.StorageType == StorageType.Double && p.HasValue ? p.AsDouble() : null;
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
};
