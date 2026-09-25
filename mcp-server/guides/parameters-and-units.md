# Parameters and units

- Storage types: String (`AsString`/`Set(string)`), Integer (`AsInteger`; Yes/No is 0/1), Double (`AsDouble` in
  internal units), ElementId (`AsElementId`). `AsValueString()` is the display text in project units.
- Set a length in display units: `p.SetValueString("1200")` (mm project) or `p.Set(ctx.Mm(1200))`.
- Find: built-in `e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)`, or by name `e.LookupParameter("Fire Rating")`.
  Several parameters can share a name, in which case use `e.GetParameters("Name")`.
- Type vs instance: when `LookupParameter` returns null on the instance, check `doc.GetElement(e.GetTypeId())`.
  Editing a type parameter changes EVERY instance of that type, so say so in the plan.
- Common built-ins: ALL_MODEL_MARK, ALL_MODEL_INSTANCE_COMMENTS, ROOM_NUMBER, ROOM_NAME, ROOM_AREA, ROOM_FINISH_FLOOR,
  DOOR_WIDTH / FAMILY_WIDTH_PARAM, FAMILY_HEIGHT_PARAM, WALL_USER_HEIGHT_PARAM, CURVE_ELEM_LENGTH, HOST_AREA_COMPUTED,
  SHEET_NUMBER, VIEW_NAME, ELEM_PARTITION_PARAM (workset), PHASE_CREATED. Check any name with `revit_api_lookup "BuiltInParameter.WORD"`.
- Units: `UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters)` and `ConvertFromInternalUnits(ft, UnitTypeId.Meters)`.
  Specs: `SpecTypeId.Length`, `.Area`, `.Volume`, `.Angle`. Project format: `doc.GetUnits().GetFormatOptions(SpecTypeId.Length)`.
- Areas are sq ft internally (`ctx.SqmFromInternal(a)`); volumes cu ft.
- Shared or project parameters (adding a new parameter): `app.OpenSharedParameterFile()`, then a definition group, then
  `ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)`, then
  `doc.ParameterBindings.Insert(definition, app.Create.NewInstanceBinding(categorySet), GroupTypeId.Data)`.
  This modifies the shared parameter FILE, so it needs `allow_risky` and user consent.
- Global parameters: `GlobalParametersManager.FindByName(doc, name)`.
