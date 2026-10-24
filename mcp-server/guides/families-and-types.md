# Placing families and working with types

Always look up exact names first with `list_types` (category + name_contains).

```csharp
var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
    .First(s => s.FamilyName == "Desk" && s.Name == "1525 x 762mm");
if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
```

| Placement (`symbol.Family.FamilyPlacementType`) | Call |
|---|---|
| OneLevelBased (furniture, equipment, columns) | `doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)` |
| Wall hosted (doors, windows) | `doc.Create.NewFamilyInstance(point, symbol, hostWall, level, StructuralType.NonStructural)`; the point must lie on the wall line |
| Face based | `doc.Create.NewFamilyInstance(face.Reference, point, refDirection, symbol)` |
| Curve based (beams, braces) | `doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam)` |
| Work plane / view based (detail items) | `doc.Create.NewFamilyInstance(point, symbol, view)` |

- Rotate: `ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(p, p + XYZ.BasisZ), angleRad)`.
- Move or copy: `ElementTransformUtils.MoveElement(doc, id, vector)` or `CopyElement(...)`, which returns new ids.
- Mirror, array: `ElementTransformUtils.MirrorElements`, `LinearArray.Create`.
- System families: `Wall.Create(doc, line, wallTypeId, levelId, height, offset, flip: false, structural: false)`,
  `Floor.Create(doc, new List<CurveLoop>{ loop }, floorTypeId, levelId)`, `Ceiling.Create(...)`,
  `RoofBase`: `doc.Create.NewFootPrintRoof(...)`.
- New type: `var t = (WallType)existing.Duplicate("New name")`, then set its type parameters. The name must be unique.
- Change type: `e.ChangeTypeId(newTypeId)`.
- Load a family: `doc.LoadFamily(path, out Family fam)` (touches files, so it needs `allow_risky` plus user consent).
- Rooms: `doc.Create.NewRoom(level, new UV(x, y))`; tags: `doc.Create.NewRoomTag(new LinkElementId(room.Id), uv, viewId)`.
