// @description: Office test fit. Fills a room with 4-desk pods (a desk and a chair per person), clear of walls, doors, columns and anything already in the room, for a target floor area per person. Spreads the pods evenly (wider aisles when there is room). Re-running replaces the previous test fit in that room. Reports seats and m2 per person.
// @mode: auto
// @inputs: {"room_number": "301", "m2_per_person": 10, "desk_type": "66\" x 30\"", "chair_family": "Chair-Breuer", "min_aisle_mm": 1200, "door_clearance_mm": 1500, "wall_clearance_mm": 300}

const string Tag = "ACE test fit";
var roomNo = ctx.Str("room_number", "301");
var room = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
    .Cast<Autodesk.Revit.DB.Architecture.Room>().FirstOrDefault(r => r.Number == roomNo && r.Area > 0)
    ?? throw new Exception($"No placed room numbered {roomNo}.");
var level = (Level)doc.GetElement(room.LevelId);
var z = level.ProjectElevation;
var areaM2 = ctx.SqmFromInternal(room.Area);

var desk = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
    .Where(s => s.Category?.Id.Value == (long)BuiltInCategory.OST_Furniture && s.FamilyName.StartsWith("Table-Rectangular"))
    .OrderByDescending(s => s.Name == ctx.Str("desk_type", "66\" x 30\"")).FirstOrDefault()
    ?? throw new Exception("No rectangular table/desk family is loaded.");
var chair = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
    .FirstOrDefault(s => s.FamilyName == ctx.Str("chair_family", "Chair-Breuer"))
    ?? throw new Exception("The chair family is not loaded.");
if (!desk.IsActive) desk.Activate();
if (!chair.IsActive) chair.Activate();
double L = desk.LookupParameter("Length")?.AsDouble() ?? ctx.Mm(1676);   // along the desk
double Wd = desk.LookupParameter("Width")?.AsDouble() ?? ctx.Mm(762);    // desk depth
double chairZone = ctx.Mm(700), chairOffset = Wd / 2 + ctx.Mm(100);

var zTest = z + ctx.Mm(300);
var rb = room.get_BoundingBox(null);
// Rasterise the room once (100 mm cells): point-in-room tests are then lookups, so the search takes about a second.
var cell = ctx.Mm(100);
int nx = (int)Math.Ceiling((rb.Max.X - rb.Min.X) / cell) + 1, ny = (int)Math.Ceiling((rb.Max.Y - rb.Min.Y) / cell) + 1;
var inside = new bool[nx, ny];
for (var i = 0; i < nx; i++)
    for (var j = 0; j < ny; j++)
        inside[i, j] = room.IsPointInRoom(new XYZ(rb.Min.X + i * cell, rb.Min.Y + j * cell, zTest));
bool In(double x, double y)
{
    // Inside only if all four surrounding cell corners are inside (conservative near walls).
    var fx = (x - rb.Min.X) / cell; var fy = (y - rb.Min.Y) / cell;
    int i0 = (int)Math.Floor(fx), j0 = (int)Math.Floor(fy);
    if (i0 < 0 || j0 < 0 || i0 + 1 >= nx || j0 + 1 >= ny) return false;
    return inside[i0, j0] && inside[i0 + 1, j0] && inside[i0, j0 + 1] && inside[i0 + 1, j0 + 1];
}

// 1. Remove a previous test fit in this room (so re-runs replace it).
var old = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Furniture).WhereElementIsNotElementType()
    .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == Tag
                && e.Location is LocationPoint lp && room.IsPointInRoom(new XYZ(lp.Point.X, lp.Point.Y, zTest)))
    .Select(e => e.Id).ToList();
if (old.Count > 0) doc.Delete(old);

// 2. What to keep clear of: doors, columns, and anything standing in the room.
var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().Cast<FamilyInstance>()
    .Where(d => d.FromRoom?.Id == room.Id || d.ToRoom?.Id == room.Id).Select(d => ((LocationPoint)d.Location).Point).ToList();
var skip = new HashSet<long> { (long)BuiltInCategory.OST_Doors, (long)BuiltInCategory.OST_Windows, (long)BuiltInCategory.OST_LightingFixtures };
var obstacles = new FilteredElementCollector(doc).WhereElementIsNotElementType()
    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(new XYZ(rb.Min.X, rb.Min.Y, z), new XYZ(rb.Max.X, rb.Max.Y, z + ctx.Mm(1800)))))
    .Where(e => e.Category != null && !skip.Contains(e.Category.Id.Value)
                && ((e is FamilyInstance fi && fi.SuperComponent == null && !(e is Panel) && !(e is Mullion))
                    || e is Autodesk.Revit.DB.Architecture.Railing || e is Autodesk.Revit.DB.Architecture.Stairs))
    .Select(e => e.get_BoundingBox(null))
    .Where(b => b != null && b.Min.Z < z + ctx.Mm(1800) && b.Max.Z > z && b.Max.X - b.Min.X < ctx.Mm(10000) && b.Max.Y - b.Min.Y < ctx.Mm(10000))
    .Where(b => In((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2))
    .ToList();

// 3. Find pod positions: 2 desks wide, back to back, chairs on both sides.
double podLong = 2 * L, podShort = 2 * Wd + 2 * chairZone;
var wall = ctx.Mm(ctx.Num("wall_clearance_mm", 300));
var doorClear = ctx.Mm(ctx.Num("door_clearance_mm", 1500));
var minAisle = ctx.Mm(ctx.Num("min_aisle_mm", 1200));
var step = ctx.Mm(150);

// Pods sit on a regular grid (rows and columns with equal aisles); the grid offset is chosen to fit the most pods.
List<(double x, double y)> Fit(bool alongX, double aisle)
{
    double sx = alongX ? podLong : podShort, sy = alongX ? podShort : podLong;
    bool Clear(double x0, double y0)
    {
        double x1 = x0 + sx, y1 = y0 + sy;
        for (double t = 0; t <= 1.0001; t += 0.125)
        {
            if (!In(x0 - wall, y0 + t * sy) || !In(x1 + wall, y0 + t * sy) || !In(x0 + t * sx, y0 - wall) || !In(x0 + t * sx, y1 + wall)) return false;
            if (!In(x0 + t * sx, y0 + sy / 2)) return false;
        }
        foreach (var d in doors)
        {
            var dx = Math.Max(Math.Max(x0 - d.X, 0), d.X - x1); var dy = Math.Max(Math.Max(y0 - d.Y, 0), d.Y - y1);
            if (Math.Sqrt(dx * dx + dy * dy) < doorClear) return false;
        }
        var m = ctx.Mm(300);
        return !obstacles.Any(b => x1 + m > b.Min.X && x0 - m < b.Max.X && y1 + m > b.Min.Y && y0 - m < b.Max.Y);
    }
    double px = sx + aisle, py = sy + aisle;
    var best = new List<(double x, double y)>();
    double cx = (rb.Min.X + rb.Max.X) / 2, cy = (rb.Min.Y + rb.Max.Y) / 2, bestSpread = double.MaxValue;
    for (var ox = 0.0; ox < px; ox += step)
        for (var oy = 0.0; oy < py; oy += step)
        {
            var pods = new List<(double x, double y)>();
            for (var y = rb.Min.Y + oy; y + sy <= rb.Max.Y; y += py)
                for (var x = rb.Min.X + ox; x + sx <= rb.Max.X; x += px)
                    if (Clear(x, y)) pods.Add((x, y));
            if (pods.Count == 0) continue;
            var spread = Math.Abs(pods.Average(q => q.x + sx / 2) - cx) + Math.Abs(pods.Average(q => q.y + sy / 2) - cy);
            if (pods.Count > best.Count || (pods.Count == best.Count && spread < bestSpread)) { best = pods; bestSpread = spread; }
        }
    return best;
}

var target = Math.Max(1, (int)Math.Floor(areaM2 / ctx.Num("m2_per_person", 10)));
var podsWanted = (int)Math.Ceiling(target / 4.0);
bool bestAlongX = true; var best = new List<(double x, double y)>(); double bestAisle = minAisle;
foreach (var alongX in new[] { true, false })
{
    var tight = Fit(alongX, minAisle);
    if (tight.Count > best.Count || (tight.Count == best.Count && alongX)) { best = tight; bestAlongX = alongX; bestAisle = minAisle; }
}
// Spread out: the widest aisle that still fits the pods we want.
if (best.Count > podsWanted)
    for (var aisle = minAisle + ctx.Mm(1200); aisle > minAisle; aisle -= ctx.Mm(200))
    {
        var wide = Fit(bestAlongX, aisle);
        if (wide.Count >= podsWanted) { best = wide; bestAisle = aisle; break; }
    }
// Keep the pods closest to the middle of the group, in reading order.
double gx = best.Count == 0 ? 0 : best.Average(q => q.x), gy = best.Count == 0 ? 0 : best.Average(q => q.y);
var pods = best.OrderBy(q => Math.Abs(q.x - gx) + Math.Abs(q.y - gy)).Take(podsWanted)
    .OrderByDescending(q => Math.Round(q.y, 1)).ThenBy(q => q.x).ToList();
var desksToPlace = Math.Min(target, pods.Count * 4);

// 4. Place desks and chairs (chair faces its desk).
var created = new List<long>();
void Place(FamilySymbol s, double x, double y, double angle)
{
    var p = new XYZ(x, y, z);
    var fi = doc.Create.NewFamilyInstance(p, s, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
    if (Math.Abs(angle) > 1e-6) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), angle);
    fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Tag);
    created.Add(fi.Id.Value);
}
var placedDesks = 0;
foreach (var (x0, y0) in pods)
    for (var j = 0; j < 2; j++)          // row: 0 = first side, 1 = back-to-back side
        for (var i = 0; i < 2; i++)      // position along the pod
        {
            if (placedDesks >= desksToPlace) break;
            double along = L / 2 + i * L, across = chairZone + Wd / 2 + j * Wd;
            double dx = bestAlongX ? x0 + along : x0 + across, dy = bestAlongX ? y0 + across : y0 + along;
            Place(desk, dx, dy, bestAlongX ? 0 : Math.PI / 2);
            var sign = j == 0 ? -1 : 1;  // chair outside the pod
            if (bestAlongX) Place(chair, dx, dy + sign * chairOffset, j == 0 ? 0 : Math.PI);
            else Place(chair, dx + sign * chairOffset, dy, j == 0 ? -Math.PI / 2 : Math.PI / 2);
            placedDesks++;
        }

var maxDesks = Fit(bestAlongX, minAisle).Count * 4;
return new
{
    room = $"{room.Number} {room.Name}", level = level.Name, roomAreaM2 = Math.Round(areaM2, 1),
    askedFor = $"{ctx.Num("m2_per_person", 10)} m2 per person = {target} people",
    workstations = placedDesks, pods = pods.Count, m2PerPerson = placedDesks == 0 ? 0 : Math.Round(areaM2 / placedDesks, 1),
    aisleMm = Math.Round(ctx.ToMm(bestAisle)), podOrientation = bestAlongX ? "desks along X" : "desks along Y",
    maxWorkstationsAtMinimumAisles = maxDesks,
    fitsTarget = placedDesks >= target,
    keptClearOf = new { doors = doors.Count, obstacles = obstacles.Count },
    removedPreviousTestFit = old.Count,
    elementsAdded = created.Count, ids = created,
    note = "Every desk and chair has Comments = 'ACE test fit'; re-run to replace, or undo as one step.",
};
