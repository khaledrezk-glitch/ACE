# Views, sheets, schedules and graphics

- Plan: `ViewPlan.Create(doc, floorPlanVftId, levelId)`, where the VFT is a `ViewFamilyType` with `ViewFamily.FloorPlan` (or `CeilingPlan`, `StructuralPlan`).
- Section: `ViewSection.CreateSection(doc, sectionVftId, boundingBoxXYZ)`. The box `Transform` sets direction (BasisZ = view direction).
- 3D: `View3D.CreateIsometric(doc, threeDVftId)`; section box: `view3d.SetSectionBox(bbox)`.
- Duplicate: `view.Duplicate(ViewDuplicateOption.WithDetailing)`. Template: `view.ViewTemplateId = template.Id`.
- Crop: `view.CropBoxActive = true; view.CropBox = bbox;`
- Sheets: `ViewSheet.Create(doc, titleBlockTypeId)`; place: `Viewport.Create(doc, sheet.Id, view.Id, centerXYZ)`,
  after checking `Viewport.CanAddViewToSheet`. A view can be on only ONE sheet (schedules and legends excepted:
  `ScheduleSheetInstance.Create`).
- Sheet centre: `sheet.Outline` (UV, feet) after `doc.Regenerate()`.
- Schedules: `var s = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Doors));`
  then `s.Definition.GetSchedulableFields()` and `s.Definition.AddField(field)`, plus
  `ScheduleSortGroupField` and `ScheduleFilter`. Read data: `s.GetTableData().GetSectionData(SectionType.Body)` and `s.GetCellText(SectionType.Body, r, c)`.
- Graphic overrides (colour elements):
```csharp
var solidFill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
    .First(f => f.GetFillPattern().IsSolidFill);
var ogs = new OverrideGraphicSettings().SetSurfaceForegroundPatternId(solidFill.Id).SetSurfaceForegroundPatternColor(new Color(255, 0, 0));
view.SetElementOverrides(id, ogs);
```
- View filters: `ParameterFilterElement.Create(doc, name, categoryIds, elementFilter)`, then `view.AddFilter(id)` and `view.SetFilterOverrides(id, ogs)`.
- Temporary isolate (no transaction needed in the UI sense, but it is a view change): `view.IsolateElementsTemporary(ids)`.
- Text and dimensions: `TextNote.Create(doc, viewId, point, text, textTypeId)`; `doc.Create.NewDimension(view, line, referenceArray)`.
- Tags: `IndependentTag.Create(doc, viewId, new Reference(e), addLeader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point)`.
- To show the user a result, prefer `view_image` over describing it.
