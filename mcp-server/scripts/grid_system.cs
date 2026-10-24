// @description: Create a rectangular structural grid from bay spacings. Vertical grids are numbered 1,2,3...; horizontal grids lettered A,B,C... (skipping I and O).
// @mode: auto
// @inputs: {"x_spacings_mm": [6000, 6000, 8000], "y_spacings_mm": [5000, 5000], "origin_x_mm": 0, "origin_y_mm": 0, "overhang_mm": 2000, "first_number": 1, "first_letter": "A"}

List<double> Spacings(string key, double[] fallback) =>
    args[key] is JsonArray a ? a.Select(v => v.GetValue<double>()).ToList() : fallback.ToList();

var xs = Spacings("x_spacings_mm", new[] { 6000.0, 6000.0, 6000.0 });
var ys = Spacings("y_spacings_mm", new[] { 6000.0, 6000.0 });
var ox = ctx.Mm(ctx.Num("origin_x_mm", 0));
var oy = ctx.Mm(ctx.Num("origin_y_mm", 0));
var overhang = ctx.Mm(ctx.Num("overhang_mm", 2000));

var xPos = new List<double> { 0 };
foreach (var s in xs) xPos.Add(xPos.Last() + ctx.Mm(s));
var yPos = new List<double> { 0 };
foreach (var s in ys) yPos.Add(yPos.Last() + ctx.Mm(s));

var existingNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Select(g => g.Name));
var created = new List<object>();

void Name(Grid g, string wanted)
{
    var name = wanted;
    for (var k = 2; existingNames.Contains(name); k++) name = $"{wanted}.{k}";
    g.Name = name;
    existingNames.Add(name);
}

string Letter(int index)
{
    var letters = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I or O
    var startAt = Math.Max(0, letters.IndexOf(ctx.Str("first_letter", "A").ToUpperInvariant()[0]));
    var i = index + startAt;
    return i < letters.Length ? letters[i].ToString() : letters[i / letters.Length - 1].ToString() + letters[i % letters.Length];
}

var firstNumber = (int)ctx.Num("first_number", 1);
for (var i = 0; i < xPos.Count; i++)
{
    var x = ox + xPos[i];
    var g = Grid.Create(doc, Line.CreateBound(new XYZ(x, oy - overhang, 0), new XYZ(x, oy + yPos.Last() + overhang, 0)));
    Name(g, (firstNumber + i).ToString());
    created.Add(new { id = g.Id.Value, name = g.Name, direction = "vertical", offsetMm = ctx.ToMm(xPos[i]) });
}
for (var j = 0; j < yPos.Count; j++)
{
    var y = oy + yPos[j];
    var g = Grid.Create(doc, Line.CreateBound(new XYZ(ox - overhang, y, 0), new XYZ(ox + xPos.Last() + overhang, y, 0)));
    Name(g, Letter(j));
    created.Add(new { id = g.Id.Value, name = g.Name, direction = "horizontal", offsetMm = ctx.ToMm(yPos[j]) });
}

return created;
