# Linked models, multiple documents and worksharing

- All open documents: `app.Documents` (each `Document`). Only the ACTIVE one can have its view changed;
  others can be read and even edited (with their own transactions, mode `manual`).
- Links: `new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))`, then `link.GetLinkDocument()`
  (null if unloaded), `link.GetTotalTransform()` to map link coordinates to host coordinates.
- Cross-model checks (e.g. MEP fixtures vs architectural rooms): collect from the link document and transform points,
  then `hostDoc.GetRoomAtPoint(p)` or `room.IsPointInRoom(p)`.
- Worksharing: `doc.IsWorkshared`; workset of an element: `e.WorksetId`, `doc.GetWorksetTable().GetWorkset(id).Name`.
  Ownership: `WorksharingUtils.GetCheckoutStatus(doc, id)` and `GetWorksharingTooltipInfo(doc, id).Owner`.
- Before editing many elements in a workshared model, keep only the ones you can change:
  `var doors = ctx.Editable(ctx.Instances(BuiltInCategory.OST_Doors));` leaves out elements in use by colleagues or changed
  in central since the last Reload Latest (Revit would otherwise roll back the whole run) and reports them to you as
  `skippedUneditable`. For one element: `if (!ctx.CanEdit(e, out var why)) ctx.Log(why);`. Tell the user what was skipped.
- A preview in a workshared model may borrow the elements it touches; the result's `heldAfterPreview` says how many the
  user now holds. Unchanged borrowed elements are released at the next sync (or Collaborate > Relinquish All Mine).
- NEVER call SynchronizeWithCentral or RelinquishOwnership without an explicit request (they're blocked unless `allow_risky`).
- Cloud models: `doc.IsModelInCloud`; there's no local file to back up, so rely on the cloud version history.
