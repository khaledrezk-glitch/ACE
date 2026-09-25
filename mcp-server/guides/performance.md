# Fast, efficient Revit API code

Big models have 100k–1M elements. The difference between a 0.2 s and a 3 min script is almost always the collector.

## Collectors: narrow in Revit, not in LINQ
```csharp
// GOOD: quick filters run in native code before elements are expanded
var doors = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsNotElementType()
    .OfClass(typeof(FamilyInstance))
    .Cast<FamilyInstance>();

// BAD: expands every element in the model, then filters in managed code
var slow = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e.Category?.Name == "Doors");
```
Order: quick filters first (`OfCategory`, `OfClass`, `ElementLevelFilter`, `BoundingBoxIntersectsFilter`,
`ElementDesignOptionFilter`), then slow filters (`ElementParameterFilter`, `ElementIntersectsSolidFilter`),
and LINQ last. Pass a view id to limit to visible elements: `new FilteredElementCollector(doc, view.Id)`.

## Parameter filtering in native code
```csharp
var rule = ParameterFilterRuleFactory.CreateGreaterRule(new ElementId(BuiltInParameter.FAMILY_WIDTH_PARAM), ctx.Mm(900), 1e-6);
var wideTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsElementType().WherePasses(new ElementParameterFilter(rule));
```
Check the overloads with `revit_api_lookup "ParameterFilterRuleFactory"`; string rules changed in recent versions.

## Other rules
- `e.get_Parameter(BuiltInParameter.X)` is faster and language-proof; use `LookupParameter("Name")` for shared or project parameters.
- Count without loading: `collector.GetElementCount()`. Ids only: `collector.ToElementIds()`.
- Cache lookups in a `Dictionary<long, T>`, e.g. type id → type name, level id → level. Don't call `doc.GetElement` repeatedly for the same id.
- ONE transaction for all edits (mode auto), never one per element. Call `doc.Regenerate()` once, only when later steps need updated geometry.
- Geometry (`get_Geometry`) is expensive: bounding boxes (`get_BoundingBox(null)`) or `LocationPoint`/`LocationCurve` are often enough.
- Rooms: `doc.GetRoomAtPoint(xyz)` or `room.IsPointInRoom(xyz)`; for doors use `fi.FromRoom` / `fi.ToRoom`.
- Returning huge results slows everything: return counts + samples, not 50k objects.
- `scriptMs` in every result tells you how long it took. Over 20 s? Revisit the collector.
