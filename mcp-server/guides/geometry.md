# Geometry and spatial reasoning

- Internal units are FEET. `ctx.Mm(1200)` converts to feet; `ctx.ToMm(ft)` converts back.
- Points: `new XYZ(x, y, z)`; vectors support `+ - * /`, `.Normalize()`, `.DotProduct()`, `.CrossProduct()`, `.DistanceTo()`.
- Curves: `Line.CreateBound(p0, p1)`, `Arc.Create(p0, p1, pointOnArc)`; `curve.Evaluate(0.5, true)` gives the midpoint.
- Walls: `((LocationCurve)wall.Location).Curve`; `wall.Orientation` is the exterior normal; `wall.Width`.
- Point elements: `((LocationPoint)fi.Location).Point` and `.Rotation`; `fi.FacingOrientation`, `fi.HandOrientation`.
- Bounding boxes: `e.get_BoundingBox(null)` (model) or `e.get_BoundingBox(view)`.
- Solids:
```csharp
var opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
foreach (var obj in e.get_Geometry(opts))
{
    if (obj is Solid s && s.Volume > 0) { /* faces: s.Faces */ }
    else if (obj is GeometryInstance gi) foreach (var inner in gi.GetInstanceGeometry()) { /* family geometry, in model coordinates */ }
}
```
- Room boundaries: `room.GetBoundarySegments(new SpatialElementBoundaryOptions())`; each segment has `.GetCurve()` and `.ElementId` (the bounding wall).
- Ray casting (clearances, what is above or below): `new ReferenceIntersector(filter, FindReferenceTarget.Element, view3D).FindNearest(origin, direction)`. Needs a non-template `View3D`.
- Intersections: `new ElementIntersectsElementFilter(e)` or `ElementIntersectsSolidFilter(solid)` in a collector (slow filters, so narrow first by category or bounding box: `new BoundingBoxIntersectsFilter(new Outline(min, max))`).
- Linked models: `link.GetLinkDocument()`, and transform points with `link.GetTotalTransform().OfPoint(p)`.
