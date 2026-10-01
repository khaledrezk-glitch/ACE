// @description: For each storey level, a sheet with that level's floor plan centred on it. Reuses an existing floor plan of the level that is not on a sheet yet (so tags and dimensions already placed appear on the sheet), else makes a new one. Levels that already have a sheet are skipped, so a re-run adds only what is missing.
// @mode: auto
// @inputs: {"levels": ["optional list of level names; default all storeys"], "titleblock": "optional text matched against 'Family Type' of loaded title blocks", "sheet_prefix": "A-1", "view_template": "optional view template name to apply to new plans", "use_existing_views": true, "storeys_only": true}

var wanted = args["levels"] is JsonArray la ? la.Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
var useExisting = ctx.Bool("use_existing_views", true);
var storeysOnly = ctx.Bool("storeys_only", true);

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

// Floor plans already on a sheet, and the levels they show.
var placedViews = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId.Value));
var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
    .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null).ToList();
var levelsWithSheet = new HashSet<long>(plans.Where(p => placedViews.Contains(p.Id.Value)).Select(p => p.GenLevel.Id.Value));

var prefix = ctx.Str("sheet_prefix", "A-1");
var usedNumbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet();
var usedViewNames = plans.Select(v => v.Name).ToHashSet();
var counter = 1;
var results = new List<object>();
var skipped = new List<object>();

foreach (var level in ctx.Levels())
{
    if (wanted != null && !wanted.Contains(level.Name)) continue;
    if (wanted == null && storeysOnly && level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 0)
    { skipped.Add(new { level = level.Name, reason = "not a building storey (storeys_only)" }); continue; }
    if (levelsWithSheet.Contains(level.Id.Value)) { skipped.Add(new { level = level.Name, reason = "already has a floor plan on a sheet" }); continue; }

    // An existing plan of this level that is not on a sheet (not dependent views), else a new plan.
    var plan = useExisting
        ? plans.Where(p => p.GenLevel.Id == level.Id && !placedViews.Contains(p.Id.Value) && p.GetPrimaryViewId() == ElementId.InvalidElementId)
               .OrderBy(p => p.Name.Length).FirstOrDefault()
        : null;
    var reused = plan != null;
    if (plan == null)
    {
        plan = ViewPlan.Create(doc, planType.Id, level.Id);
        var viewName = $"{level.Name} - Sheet";
        for (var k = 2; usedViewNames.Contains(viewName); k++) viewName = $"{level.Name} - Sheet ({k})";
        plan.Name = viewName;
        usedViewNames.Add(viewName);
        if (template != null) plan.ViewTemplateId = template.Id;
    }

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
    {
        Viewport.Create(doc, sheet.Id, plan.Id, centre);
        placedViews.Add(plan.Id.Value);
    }

    results.Add(new { level = level.Name, view = plan.Name, reusedExistingView = reused, viewId = plan.Id.Value, sheet = $"{sheet.SheetNumber} - {sheet.Name}", sheetId = sheet.Id.Value });
}

return new { created = results.Count, sheets = results, skipped };
