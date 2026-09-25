// @description: For each level, create a new floor plan view and a sheet with that plan centred on it.
// @mode: auto
// @inputs: {"levels": ["optional list of level names; default all"], "titleblock": "optional text matched against 'Family Type' of loaded title blocks", "sheet_prefix": "A-1", "view_template": "optional view template name to apply"}

var wanted = args["levels"] is JsonArray la ? la.Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
    .First(t => t.ViewFamily == ViewFamily.FloorPlan);

var titleblocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
    .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
var tbFilter = ctx.Str("titleblock");
var titleblock = tbFilter == null
    ? titleblocks.FirstOrDefault()
    : titleblocks.FirstOrDefault(t => $"{t.FamilyName} {t.Name}".IndexOf(tbFilter, StringComparison.OrdinalIgnoreCase) >= 0);
if (titleblock == null)
    throw new Exception("No matching title block type is loaded. Available: " + string.Join(", ", titleblocks.Select(t => $"{t.FamilyName} : {t.Name}")));

View template = null;
var templateName = ctx.Str("view_template");
if (templateName != null)
{
    template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
        .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase))
        ?? throw new Exception($"View template '{templateName}' not found.");
}

var prefix = ctx.Str("sheet_prefix", "A-1");
var usedNumbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet();
var usedViewNames = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Select(v => v.Name).ToHashSet();
var counter = 1;
var results = new List<object>();

foreach (var level in ctx.Levels())
{
    if (wanted != null && !wanted.Contains(level.Name)) continue;

    var plan = ViewPlan.Create(doc, planType.Id, level.Id);
    var viewName = $"{level.Name} - Sheet";
    for (var k = 2; usedViewNames.Contains(viewName); k++) viewName = $"{level.Name} - Sheet ({k})";
    plan.Name = viewName;
    usedViewNames.Add(viewName);
    if (template != null) plan.ViewTemplateId = template.Id;

    string number;
    do { number = prefix + counter.ToString("00"); counter++; } while (usedNumbers.Contains(number));
    usedNumbers.Add(number);

    var sheet = ViewSheet.Create(doc, titleblock.Id);
    sheet.SheetNumber = number;
    sheet.Name = $"{level.Name} Floor Plan";
    doc.Regenerate();

    var outline = sheet.Outline;
    var centre = new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
    if (Viewport.CanAddViewToSheet(doc, sheet.Id, plan.Id))
        Viewport.Create(doc, sheet.Id, plan.Id, centre);

    results.Add(new { level = level.Name, view = plan.Name, viewId = plan.Id.Value, sheet = $"{sheet.SheetNumber} - {sheet.Name}", sheetId = sheet.Id.Value });
}

return results;
