// @description: Renumber door marks by the room they open into (room number + letter: 301A, 301B) or by level (prefix + level + sequence: D-L3-001) in reading order. Skips doors in use by colleagues (workshared) and reports doors without a room.
// @mode: auto
// @inputs: {"scheme": "room | level (default room)", "levels": ["optional level names; default all"], "prefix": "D-", "start": 1, "digits": 3}

var scheme = (ctx.Str("scheme", "room") ?? "room").ToLowerInvariant();
if (scheme != "room" && scheme != "level") throw new Exception("scheme must be 'room' or 'level'.");
var wanted = args["levels"] is JsonArray la ? la.Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
var prefix = ctx.Str("prefix", "D-");
var start = (int)ctx.Num("start", 1);
var digits = Math.Max(1, (int)ctx.Num("digits", 3));

// The last phase: rooms and doors are compared in the model as it will be built.
var phase = doc.Phases.Cast<Phase>().LastOrDefault();
var doors = ctx.Instances(BuiltInCategory.OST_Doors).OfType<FamilyInstance>()
    .Where(d => d.SuperComponent == null && (wanted == null || (doc.GetElement(d.LevelId) is Level lv && wanted.Contains(lv.Name))))
    .ToList();
doors = ctx.Editable(doors);   // workshared: leaves out doors in use by others (reported as skippedUneditable)

Room RoomOf(FamilyInstance d)
{
    // The room the door opens into, else the one it comes from.
    if (phase != null) return d.get_ToRoom(phase) ?? d.get_FromRoom(phase);
    return d.ToRoom ?? d.FromRoom;
}

XYZ PointOf(FamilyInstance d) => (d.Location as LocationPoint)?.Point ?? XYZ.Zero;
// Reading order: top to bottom (Y descending, in 1 m bands), then left to right.
IOrderedEnumerable<FamilyInstance> Reading(IEnumerable<FamilyInstance> list) =>
    list.OrderByDescending(d => Math.Round(PointOf(d).Y / ctx.M(1))).ThenBy(d => PointOf(d).X);

var changes = new List<(FamilyInstance Door, string Mark)>();
var noRoom = new List<object>();
if (scheme == "room")
{
    foreach (var group in doors.GroupBy(d => RoomOf(d)?.Number ?? ""))
    {
        if (group.Key == "") { foreach (var d in group) noRoom.Add(new { id = d.Id.Value, mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() }); continue; }
        var i = 0;
        foreach (var d in Reading(group))
        {
            var letter = i < 26 ? ((char)('A' + i)).ToString() : $"A{(char)('A' + i - 26)}";
            changes.Add((d, group.Count() == 1 ? group.Key : group.Key + letter));
            i++;
        }
    }
}
else
{
    foreach (var group in doors.GroupBy(d => (doc.GetElement(d.LevelId) as Level)?.Name ?? "XX").OrderBy(g => g.Key))
    {
        var n = start;
        foreach (var d in Reading(group))
            changes.Add((d, $"{prefix}{group.Key.Replace(" ", "")}-{n++.ToString(new string('0', digits))}"));
    }
}

var changed = 0;
var samples = new List<string>();
foreach (var (door, mark) in changes)
{
    ctx.ThrowIfCancelled();
    var p = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
    if (p == null || p.IsReadOnly || p.AsString() == mark) continue;
    if (samples.Count < 20) samples.Add($"{door.Id.Value}: '{p.AsString()}' -> '{mark}'");
    p.Set(mark);
    changed++;
}

return new { scheme, doors = doors.Count, changed, unchanged = changes.Count - changed, withoutRoom = noRoom.Count, noRoom = noRoom.Take(30), samples };
