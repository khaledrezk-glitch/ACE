# MEP and structure quick reference

- Pipes: `Pipe.Create(doc, pipingSystemTypeId, pipeTypeId, levelId, start, end)`; ducts: `Duct.Create(doc, mechSystemTypeId, ductTypeId, levelId, start, end)`.
- Connectors: `((MEPCurve)e).ConnectorManager.Connectors` or `fi.MEPModel.ConnectorManager.Connectors`; `c.IsConnected`, `c.AllRefs`, `c.Origin`.
- Connect: `doc.Create.NewElbowFitting(c1, c2)`, `NewTeeFitting`, `c1.ConnectTo(c2)`.
- Systems: `MEPSystem` / `PipingSystem` / `MechanicalSystem`; the elements in a system are in `system.Elements`.
- Spaces: `Autodesk.Revit.DB.Mechanical.Space` (like rooms, for MEP). Electrical circuits: `ElectricalSystem.Create(...)`.
- Unconnected check: loop connectors of MEP curves and fittings with `!c.IsConnected` (report open ends).
- Structural columns: `NewFamilyInstance(point, symbol, level, StructuralType.Column)`; beams: `NewFamilyInstance(line, symbol, level, StructuralType.Beam)`.
- Framing on a grid: intersect grid curves (`grid.Curve.Intersect(other.Curve, out var results)`) to get column points.
- Rebar and analytical: `Autodesk.Revit.DB.Structure` (`Rebar`, `AnalyticalMember`); look members up with `revit_api_lookup`.
