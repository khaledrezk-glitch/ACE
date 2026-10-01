// @description: Tag every element of a category that is visible but not yet tagged, in the active view, in named views, or in every floor plan in one run (doors by default). Uses the category's default tag type, which must be loaded.
// @mode: auto
// @inputs: {"category": "Doors | Windows | Rooms | Walls | ... (default Doors)", "leader": false, "views": ["optional view names"], "all_floor_plans": false}

var categoryName = ctx.Str("category", "Doors");
if (!Enum.TryParse<BuiltInCategory>("OST_" + categoryName.Replace(" ", ""), true, out var bic))
    throw new Exception($"Unknown category '{categoryName}'.");

bool CanHostTags(View v) => !v.IsTemplate && !(v is View3D || v is ViewSheet || v is ViewSchedule || v is ViewDrafting);

// Which views: named ones, every floor plan, or the active view.
var names = args["views"] is JsonArray va ? va.Select(x => x.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
List<View> views;
if (names != null && names.Count > 0)
{
    views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => CanHostTags(v) && names.Contains(v.Name)).ToList();
    var missing = names.Where(n => !views.Any(v => v.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
    if (missing.Count > 0) throw new Exception($"Views not found or unable to hold tags: {string.Join(", ", missing)}.");
}
else if (ctx.Bool("all_floor_plans"))
    views = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
        .Where(v => v.ViewType == ViewType.FloorPlan && CanHostTags(v)).Cast<View>().OrderBy(v => v.Name).ToList();
else
{
    var active = doc.ActiveView;
    if (!CanHostTags(active))
        throw new Exception($"Active view '{active.Name}' can't host tags this way; open a plan, section or elevation, or pass views / all_floor_plans.");
    views = new List<View> { active };
}

var perView = new List<object>();
var skipped = new List<object>();
var total = 0;
foreach (var view in views)
{
    ctx.ThrowIfCancelled();
    var alreadyTagged = new HashSet<long>(
        new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .SelectMany(t => t.GetTaggedLocalElementIds())
            .Select(id => id.Value));

    var targets = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType()
        .Where(e => !alreadyTagged.Contains(e.Id.Value))
        .ToList();

    var created = 0;
    foreach (var e in targets)
    {
        XYZ point = (e.Location as LocationPoint)?.Point ?? (e.Location as LocationCurve)?.Curve.Evaluate(0.5, true);
        if (point == null)
        {
            var bb = e.get_BoundingBox(view);
            if (bb != null) point = (bb.Min + bb.Max) / 2;
        }
        if (point == null) { if (skipped.Count < 50) skipped.Add(new { view = view.Name, id = e.Id.Value, reason = "no location" }); continue; }

        try
        {
            IndependentTag.Create(doc, view.Id, new Reference(e), ctx.Bool("leader"), TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
            created++;
        }
        catch (Exception ex)
        {
            if (skipped.Count < 50) skipped.Add(new { view = view.Name, id = e.Id.Value, reason = ex.Message });
        }
    }
    total += created;
    perView.Add(new { view = view.Name, alreadyTagged = alreadyTagged.Count, created });
}

return new { category = categoryName, views = views.Count, created = total, perView, skipped };
