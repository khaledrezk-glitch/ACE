// @description: Read-only health check: warnings (grouped), unplaced / unenclosed rooms, in-place families, CAD imports, unused view templates, views not on sheets.
// @mode: readonly
// @inputs: {}

var warnings = doc.GetWarnings();
var topWarnings = warnings
    .GroupBy(w => w.GetDescriptionText())
    .OrderByDescending(g => g.Count())
    .Take(15)
    .Select(g => new
    {
        warning = g.Key,
        count = g.Count(),
        sampleElementIds = g.SelectMany(w => w.GetFailingElements()).Take(5).Select(id => id.Value).ToList(),
    })
    .ToList();

var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType().Cast<Room>().ToList();

var inPlace = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
    .Where(fi => fi.Symbol?.Family?.IsInPlace == true)
    .Select(fi => new { id = fi.Id.Value, family = fi.Symbol.FamilyName, category = fi.Category?.Name })
    .ToList();

var cadImports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
    .Select(i => new { id = i.Id.Value, linked = i.IsLinked, name = i.Category?.Name, viewSpecific = i.ViewSpecific })
    .ToList();

var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
var usedTemplates = new HashSet<long>(views.Where(v => !v.IsTemplate).Select(v => v.ViewTemplateId.Value));
var unusedTemplates = views.Where(v => v.IsTemplate && !usedTemplates.Contains(v.Id.Value)).Select(v => v.Name).OrderBy(n => n).ToList();

var onSheets = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => vp.ViewId.Value));
var notOnSheets = views
    .Where(v => !v.IsTemplate && v.CanBePrinted && v is not ViewSheet && v.ViewType != ViewType.Schedule && !onSheets.Contains(v.Id.Value))
    .Select(v => $"{v.ViewType}: {v.Name}")
    .ToList();

return new
{
    totalWarnings = warnings.Count,
    topWarnings,
    rooms = rooms.Count,
    unplacedRoomIds = rooms.Where(r => r.Location == null).Select(r => r.Id.Value).ToList(),
    unenclosedRoomIds = rooms.Where(r => r.Location != null && r.Area <= 0).Select(r => r.Id.Value).ToList(),
    inPlaceFamilies = inPlace,
    cadImports,
    unusedViewTemplates = unusedTemplates,
    viewsNotOnSheetsCount = notOnSheets.Count,
    viewsNotOnSheetsSample = notOnSheets.Take(40).ToList(),
};
