// @description: Renumber placed rooms per level in reading order (rows top-to-bottom, then left-to-right). Two-pass, so existing numbers never collide.
// @mode: auto
// @inputs: {"level": "optional level name; default all levels", "prefix": "optional; default = last word of the level name (\"Level 2\" -> \"2\")", "separator": "default \"\"", "start": 1, "digits": 2, "row_tolerance_mm": 3000}

var levelName = ctx.Str("level");
var prefixInput = ctx.Str("prefix");
var separator = ctx.Str("separator", "");
var start = (int)ctx.Num("start", 1);
var digits = (int)ctx.Num("digits", 2);
var rowTolerance = ctx.Mm(ctx.Num("row_tolerance_mm", 3000));

var rooms = ctx.Instances(BuiltInCategory.OST_Rooms).Cast<Room>()
    .Where(r => r.Location is LocationPoint && r.Area > 0)
    .Where(r => string.IsNullOrEmpty(levelName) || string.Equals(r.Level?.Name, levelName, StringComparison.OrdinalIgnoreCase))
    .ToList();

if (rooms.Count == 0) throw new Exception("No placed, enclosed rooms matched.");

var changes = new List<object>();
foreach (var group in rooms.GroupBy(r => r.LevelId))
{
    var level = doc.GetElement(group.Key) as Level;
    var ordered = group
        .Select(r => new { room = r, p = ((LocationPoint)r.Location).Point })
        .OrderByDescending(x => Math.Round(x.p.Y / rowTolerance))
        .ThenBy(x => x.p.X)
        .Select(x => x.room)
        .ToList();

    // Pass 1: temporary unique numbers so pass 2 never hits "number already in use".
    foreach (var r in ordered) r.Number = "tmp-" + r.Id.Value;

    var prefix = prefixInput ?? level?.Name.Split(' ').Last() ?? "";
    var n = start;
    foreach (var r in ordered)
    {
        var number = prefix + separator + n.ToString(new string('0', Math.Max(1, digits)));
        changes.Add(new { id = r.Id.Value, level = level?.Name, name = r.Name, number });
        r.Number = number;
        n++;
    }
}

Log($"Renumbered {changes.Count} rooms on {rooms.Select(r => r.LevelId).Distinct().Count()} level(s).");
return changes;
