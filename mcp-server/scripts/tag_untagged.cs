// @description: Tag every element of a category that is visible but not yet tagged in the ACTIVE view (doors by default). Uses the category's default tag type, which must be loaded.
// @mode: auto
// @inputs: {"category": "Doors | Windows | Rooms | Walls | ... (default Doors)", "leader": false}

var categoryName = ctx.Str("category", "Doors");
if (!Enum.TryParse<BuiltInCategory>("OST_" + categoryName.Replace(" ", ""), true, out var bic))
    throw new Exception($"Unknown category '{categoryName}'.");

var view = doc.ActiveView;
if (view is View3D || view is ViewSheet || view is ViewSchedule)
    throw new Exception($"Active view '{view.Name}' can't host tags this way; open a plan, section or elevation.");

var alreadyTagged = new HashSet<long>(
    new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
        .SelectMany(t => t.GetTaggedLocalElementIds())
        .Select(id => id.Value));

var targets = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType()
    .Where(e => !alreadyTagged.Contains(e.Id.Value))
    .ToList();

var created = 0;
var skipped = new List<object>();
foreach (var e in targets)
{
    XYZ point = (e.Location as LocationPoint)?.Point ?? (e.Location as LocationCurve)?.Curve.Evaluate(0.5, true);
    if (point == null)
    {
        var bb = e.get_BoundingBox(view);
        if (bb != null) point = (bb.Min + bb.Max) / 2;
    }
    if (point == null) { skipped.Add(new { id = e.Id.Value, reason = "no location" }); continue; }

    try
    {
        IndependentTag.Create(doc, view.Id, new Reference(e), ctx.Bool("leader"), TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
        created++;
    }
    catch (Exception ex)
    {
        skipped.Add(new { id = e.Id.Value, reason = ex.Message });
    }
}

return new { view = view.Name, category = categoryName, alreadyTagged = alreadyTagged.Count, created, skipped };
