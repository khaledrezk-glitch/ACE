// @description: Find placed rooms that no door opens into (checks each door's From/To room in the last phase). Useful QA for egress and room data. Read-only.
// @mode: readonly
// @inputs: {"level": "optional level name", "phase": "optional phase name; default = last phase"}

var phaseName = ctx.Str("phase");
var phase = phaseName == null
    ? doc.Phases.Cast<Phase>().Last()
    : doc.Phases.Cast<Phase>().FirstOrDefault(p => p.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception($"Phase '{phaseName}' not found.");
var levelName = ctx.Str("level");

var roomsWithDoors = new HashSet<long>();
foreach (var d in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
{
    var from = d.get_FromRoom(phase);
    var to = d.get_ToRoom(phase);
    if (from != null) roomsWithDoors.Add(from.Id.Value);
    if (to != null) roomsWithDoors.Add(to.Id.Value);
}

var rooms = ctx.Instances(BuiltInCategory.OST_Rooms).Cast<Room>()
    .Where(r => r.Area > 0)
    .Where(r => levelName == null || string.Equals(r.Level?.Name, levelName, StringComparison.OrdinalIgnoreCase))
    .ToList();

var without = rooms.Where(r => !roomsWithDoors.Contains(r.Id.Value))
    .Select(r => new { id = r.Id.Value, number = r.Number, name = r.Name, level = r.Level?.Name, areaM2 = Math.Round(ctx.SqmFromInternal(r.Area), 2) })
    .OrderBy(r => r.level).ThenBy(r => r.number).ToList();

return new
{
    phase = phase.Name,
    roomsChecked = rooms.Count,
    roomsWithoutDoors = without.Count,
    rooms = without,
    note = "Rooms reached only through openings, curtain wall doors placed as panels, or doors in links will also appear here; review before acting.",
};
