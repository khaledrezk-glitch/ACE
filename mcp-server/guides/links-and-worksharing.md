# Linked models, multiple documents and worksharing

- All open documents: `app.Documents` (each `Document`). Only the ACTIVE one can have its view changed;
  others can be read and even edited (with their own transactions, mode `manual`).
- Links: `new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))`, then `link.GetLinkDocument()`
  (null if unloaded), `link.GetTotalTransform()` to map link coordinates to host coordinates.
- Cross-model checks (e.g. MEP fixtures vs architectural rooms): collect from the link document and transform points,
  then `hostDoc.GetRoomAtPoint(p)` or `room.IsPointInRoom(p)`.
- Worksharing: `doc.IsWorkshared`; workset of an element: `e.WorksetId`, `doc.GetWorksetTable().GetWorkset(id).Name`.
  Ownership: `WorksharingUtils.GetCheckoutStatus(doc, id)` and `GetWorksharingTooltipInfo(doc, id).Owner`.
- NEVER call SynchronizeWithCentral or RelinquishOwnership without an explicit request (they're blocked unless `allow_risky`).
- Cloud models: `doc.IsModelInCloud`; there's no local file to back up, so rely on the cloud version history.
