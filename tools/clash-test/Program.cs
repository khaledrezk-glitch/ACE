// Clash logic test (no Revit needed): dotnet run --project tools/clash-test (after the add-in build)
using System; using System.Collections; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection;
class P {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }
  static void Main() {
    var a = Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "AceRevitMcp.dll"));
    var clashT = a.GetType("AceRevitMcp.Coordination.Clash"); var logic = a.GetType("AceRevitMcp.Coordination.ClashLogic");
    var issueT = a.GetType("AceRevitMcp.Coordination.ClashIssue");
    MethodInfo M(string n) => logic.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    object F(object o, string n) => o.GetType().GetField(n).GetValue(o);
    var listT = typeof(List<>).MakeGenericType(clashT);
    IList List(params object[] items) { var l = (IList)Activator.CreateInstance(listT); foreach (var i in items) l.Add(i); return l; }
    var t0 = new DateTime(2026, 9, 1);
    object C(string key, string status, string ua, string ub, string catA, string catB, string level, string mover = null, string resp = "MEP (HVAC)", double depth = 50) {
      var c = Activator.CreateInstance(clashT);
      foreach (var (n, v) in new (string, object)[] { ("Key", key), ("Test", "T"), ("Status", status), ("UA", ua), ("UB", ub), ("CatA", catA), ("CatB", catB),
               ("Level", level), ("Mover", mover), ("Responsible", resp), ("DepthMm", depth), ("NameA", ua), ("NameB", ub), ("SourceA", "M"), ("SourceB", "M"),
               ("FirstSeen", t0), ("LastSeen", t0) })
        clashT.GetField(n).SetValue(c, v);
      return c; }

    // 1. The same pair is one clash, whichever side found it.
    var pk = M("PairKey");
    Check((string)pk.Invoke(null, new object[] { "T", "x", "y" }) == (string)pk.Invoke(null, new object[] { "T", "y", "x" }), "pair key is symmetric");

    // 2. Responsibility: the easier element to move gives way; equal priority = coordinate; ARC/STR resolved by the model.
    var rules = new Dictionary<string, (int, string)> { ["Ducts"] = (55, "MEP (HVAC)"), ["Structural Framing"] = (95, "STR"), ["Walls"] = (85, "ARC/STR"), ["Pipes"] = (45, "MEP (plumbing / fire)") };
    var assign = M("Assign");
    var c1 = C("k", "new", "d", "b", "Ducts", "Structural Framing", "L3");
    assign.Invoke(null, new object[] { c1, rules, "MEP", "STR" });
    Check((string)F(c1, "Mover") == "A" && (string)F(c1, "Responsible") == "MEP (HVAC)", "duct gives way to beam (MEP responsible)");
    var c2 = C("k", "new", "w", "p", "Walls", "Pipes", "L3");
    assign.Invoke(null, new object[] { c2, rules, "ARC", "MEP" });
    Check((string)F(c2, "Mover") == "B" && (string)F(c2, "Responsible") == "MEP (plumbing / fire)", "pipe gives way to wall");
    var c3 = C("k", "new", "p1", "p2", "Pipes", "Pipes", "L3");
    assign.Invoke(null, new object[] { c3, rules, "MEP", "MEP" });
    Check(F(c3, "Mover") == null && ((string)F(c3, "Responsible")).StartsWith("Coordinate (MEP (plumbing / fire))"), "pipe vs pipe = coordinate, named once");
    var c4 = C("k", "new", "w", "b", "Walls", "Floors", "L3");   // Floors not in rules -> rank 50: wall (85) keeps, floor from the STR model gives way
    assign.Invoke(null, new object[] { c4, rules, "ARC", "STR" });
    Check((string)F(c4, "Responsible") == "STR", "unknown category falls back to its model's discipline");

    // 3. Merge in scope (a run on L3 only).
    var merge = M("Merge");
    IList Prev() => List(C("k1", "active", "a", "b", "Ducts", "Structural Framing", "L3"), C("k2", "resolved", "c", "d", "Ducts", "Structural Framing", "L3"),
                    C("k3", "approved", "e", "f", "Ducts", "Structural Framing", "L3"), C("k4", "active", "g", "h", "Ducts", "Structural Framing", "L2"),
                    C("k5", "new", "i", "j", "Ducts", "Structural Framing", "L3"));
    var prev = Prev();
    var now = List(C("k1", "new", "a", "b", "Ducts", "Structural Framing", "L3"), C("k2", "new", "c", "d", "Ducts", "Structural Framing", "L3"),
                   C("k3", "new", "e", "f", "Ducts", "Structural Framing", "L3"), C("k1", "new", "a", "b", "Ducts", "Structural Framing", "L3"));
    var scopeT = typeof(Func<,>).MakeGenericType(clashT, typeof(bool));
    var p = System.Linq.Expressions.Expression.Parameter(clashT, "c");
    var body = System.Linq.Expressions.Expression.Equal(System.Linq.Expressions.Expression.Field(p, "Level"), System.Linq.Expressions.Expression.Constant("L3"));
    var scope = System.Linq.Expressions.Expression.Lambda(scopeT, body, p).Compile();
    var t1 = t0.AddDays(2);
    var merged = ((IEnumerable)merge.Invoke(null, new object[] { prev, now, scope, t1 })).Cast<object>().ToList();
    string St(string k) => merged.Where(c => (string)F(c, "Key") == k).Select(c => (string)F(c, "Status")).FirstOrDefault() ?? "(missing)";
    Check(merged.Count(c => (string)F(c, "Key") == "k1") == 1, "a pair found twice in one run is kept once");
    Check(St("k1") == "active", "still there -> active");
    Check(St("k2") == "new" && (int)F(merged.First(c => (string)F(c, "Key") == "k2"), "Reopened") == 1, "resolved clash that comes back -> new, reopened");
    Check(St("k3") == "approved", "approval is kept");
    Check(St("k4") == "active", "clash on another level is not marked resolved by an L3 run");
    Check(St("k5") == "resolved", "gone from the tested level -> resolved");
    Check((DateTime)F(merged.First(c => (string)F(c, "Key") == "k1"), "FirstSeen") == t0, "first seen is kept");

    // Cut-short run: nothing may become resolved.
    var none = System.Linq.Expressions.Expression.Lambda(scopeT, System.Linq.Expressions.Expression.Constant(false), p).Compile();
    var partial = ((IEnumerable)merge.Invoke(null, new object[] { Prev(), List(), none, t1 })).Cast<object>().ToList();
    Check(partial.All(c => (string)F(c, "Status") != "resolved" || (string)F(c, "Key") == "k2"), "a cut-short run resolves nothing");
    // Full run with nothing found: everything open becomes resolved, old resolved kept for 30 days only.
    var old = List(C("k9", "resolved", "x", "y", "Ducts", "Pipes", "L1"));
    ((IList)old).Add(C("k8", "active", "x", "z", "Ducts", "Pipes", "L1"));
    var late = ((IEnumerable)merge.Invoke(null, new object[] { old, List(), null, t0.AddDays(40) })).Cast<object>().ToList();
    Check(late.Count == 1 && (string)F(late[0], "Key") == "k8" && (string)F(late[0], "Status") == "resolved", "resolved clashes drop off after 30 days");

    // 4. Issues: one duct through three beams = one issue; pipe P1 hitting P2 and P3 (equal priority) = one issue led by P1.
    var clashes = List(
      C("i1", "new", "duct", "b1", "Ducts", "Structural Framing", "L3", "A", "MEP (HVAC)", 120),
      C("i2", "active", "b2", "duct", "Structural Framing", "Ducts", "L3", "B", "MEP (HVAC)", 80),
      C("i3", "new", "duct", "b3", "Ducts", "Structural Framing", "L4", "A", "MEP (HVAC)", 60),
      C("i4", "new", "p2", "p1", "Pipes", "Pipes", "L3", null, "Coordinate (MEP)", 20),
      C("i5", "new", "p1", "p3", "Pipes", "Pipes", "L3", null, "Coordinate (MEP)", 30),
      C("i6", "resolved", "duct", "b9", "Ducts", "Structural Framing", "L3", "A"));
    var issues = ((IEnumerable)M("Issues").Invoke(null, new object[] { clashes })).Cast<object>().ToList();
    Check(issues.Count == 2, $"two issues (got {issues.Count})");
    var first = issues[0];
    Check((int)F(first, "Count") == 3 && ((string)F(first, "Title")).Contains("hits 3 Structural Framing") && (double)F(first, "MaxDepthMm") == 120, "duct issue: 3 beams, deepest 120 mm");
    Check((string)F(first, "Levels") == "L3, L4", "duct issue spans L3 and L4");
    Check(((string)F(issues[1], "Key")).EndsWith("|p1") && (int)F(issues[1], "Count") == 2, "pipe issue led by the pipe in both clashes");
    Check(F(clashes[5], "Group") == null, "resolved clashes are not grouped");

    // 5. Cause: which side changed since the baseline snapshot.
    var sideT = logic.GetNestedType("Side", BindingFlags.NonPublic | BindingFlags.Public);
    object Side(string change, string by, string cat, string model = null) {
      var x = Activator.CreateInstance(sideT);
      sideT.GetField("Change").SetValue(x, change); sideT.GetField("By").SetValue(x, by); sideT.GetField("Cat").SetValue(x, cat); sideT.GetField("Model").SetValue(x, model);
      return x; }
    var cause = M("Cause");
    (string, string) Cause(object sa, object sb) { var r = cause.Invoke(null, new[] { sa, sb, "the snapshot of 29 Sep 09:00" }); return ((string, string))r; }
    var r1 = Cause(Side("added", "Ahmed", "Ducts", "MEP.rvt"), Side(null, null, "Structural Framing"));
    Check(r1.Item1 == "Ducts added in MEP.rvt by Ahmed since the snapshot of 29 Sep 09:00" && r1.Item2 == "Ahmed", "new duct caused the clash, by Ahmed");
    var r2 = Cause(Side("moved", "Sara", "Structural Framing"), Side("retyped", null, "Pipes"));
    Check(r2.Item1.StartsWith("Structural Framing moved by Sara and Pipes retyped") && r2.Item2 == "Sara", "both sides changed: both named");
    var r3 = Cause(Side(null, null, "Ducts"), Side(null, null, "Walls"));
    Check(r3.Item1.StartsWith("both elements are unchanged") && r3.Item2 == null, "unchanged: not from a recent change");
    Check(Cause(null, null).Item1 == null, "no snapshot: no cause claimed");
    // Causes survive the next run while the clash stays; a reopened clash loses the old cause.
    var pc = C("q1", "new", "a", "b", "Ducts", "Walls", "L3"); clashT.GetField("Cause").SetValue(pc, "Ducts added"); clashT.GetField("CausedBy").SetValue(pc, "Ahmed");
    var pr = C("q2", "resolved", "c", "d", "Ducts", "Walls", "L3"); clashT.GetField("Cause").SetValue(pr, "old cause");
    var m2 = ((IEnumerable)merge.Invoke(null, new object[] { List(pc, pr), List(C("q1", "new", "a", "b", "Ducts", "Walls", "L3"), C("q2", "new", "c", "d", "Ducts", "Walls", "L3")), null, t1 })).Cast<object>().ToList();
    Check((string)F(m2.First(c => (string)F(c, "Key") == "q1"), "CausedBy") == "Ahmed", "cause kept while the clash stays");
    Check(F(m2.First(c => (string)F(c, "Key") == "q2"), "Cause") == null, "reopened clash gets a fresh cause");

    // Approval survives a clash disappearing (e.g. link unloaded and the run not scoped) and coming back.
    var ap = C("a1", "approved", "x", "y", "Ducts", "Walls", "L3");
    var gone = ((IEnumerable)merge.Invoke(null, new object[] { List(ap), List(), null, t1 })).Cast<object>().ToList();
    Check((string)F(gone[0], "Status") == "resolved" && (bool)F(gone[0], "Approved"), "an approved clash that disappears is resolved but remembers its approval");
    var back = ((IEnumerable)merge.Invoke(null, new object[] { gone.Cast<object>().Aggregate(List(), (l, x) => { l.Add(x); return l; }), List(C("a1", "new", "x", "y", "Ducts", "Walls", "L3")), null, t1.AddDays(1) })).Cast<object>().ToList();
    Check((string)F(back.First(c => (string)F(c, "Key") == "a1"), "Status") == "approved", "an approved clash that comes back is approved again, not new");

    // The clash search grid finds exactly the boxes a brute-force check finds (random boxes, small and very large).
    var gridT = a.GetType("AceRevitMcp.Coordination.BoxGrid");
    var grid = Activator.CreateInstance(gridT, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { 10.0, 64 }, null);
    var add = gridT.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    var query = gridT.GetMethod("Query", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    var rnd = new Random(7);
    var boxes = new List<double[]>();
    for (var i = 0; i < 3000; i++)
    {
        var size = i % 100 == 0 ? 400 : rnd.NextDouble() * 15;   // every 100th box is a slab-sized one
        var x = rnd.NextDouble() * 1000 - 500; var y = rnd.NextDouble() * 1000 - 500; var z = rnd.NextDouble() * 200;
        var bx = new[] { x, y, z, x + size, y + size * 0.7, z + Math.Min(size, 3) };
        boxes.Add(bx);
        add.Invoke(grid, bx.Cast<object>().ToArray());
    }
    var gridOk = true;
    for (var q = 0; q < 300 && gridOk; q++)
    {
        var x = rnd.NextDouble() * 1000 - 500; var y = rnd.NextDouble() * 1000 - 500; var z = rnd.NextDouble() * 200; var s = q % 50 == 0 ? 900 : rnd.NextDouble() * 20;
        var qb = new[] { x, y, z, x + s, y + s, z + s };
        var got = ((List<int>)query.Invoke(grid, qb.Cast<object>().ToArray())).OrderBy(i => i).ToList();
        var want = Enumerable.Range(0, boxes.Count).Where(i => boxes[i][0] <= qb[3] && boxes[i][3] >= qb[0] && boxes[i][1] <= qb[4] && boxes[i][4] >= qb[1] && boxes[i][2] <= qb[5] && boxes[i][5] >= qb[2]).ToList();
        gridOk = got.SequenceEqual(want);
    }
    Check(gridOk, "clash search grid returns exactly the touching boxes (small, slab-sized and very large queries)");

    // 9. Navisworks clash reports (XML and tabular HTML) are read into clashes with their two items.
    var parser = a.GetType("AceRevitMcp.Coordination.ClashReportParser");
    MethodInfo PM(string n) => parser.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    (IList List, string Format) Parse(string file) {
      var args = new object[] { System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", file)), null };
      var list = (IList)PM("Parse").Invoke(null, args);
      return (list, (string)args[1]); }
    object Item(object rc, string side) => F(rc, side);
    var (xml, xmlFormat) = Parse("navisworks-report.xml");
    Check(xmlFormat == "Navisworks XML" && xml.Count == 4, $"XML report: 4 clashes in 2 tests (got {xml.Count}, {xmlFormat})");
    var x1 = xml[0];
    Check((string)F(x1, "Test") == "STR vs MEP" && (string)F(x1, "Name") == "Clash1" && (string)F(x1, "Status") == "new", "XML: test, name and status");
    Check((double?)F(x1, "DistanceMm") == -120 && (string)F(x1, "Grid") == "B-3 : Level 2", "XML: distance in mm from the report's metres, grid");
    Check((long?)F(Item(x1, "A"), "ElementId") == 345678 && (string)F(Item(x1, "A"), "File") == "Tower-STR.nwc" && (string)F(Item(x1, "A"), "Type") == "Structural Framing", "XML: item 1 id, model file and type");
    Check((string)F(Item(x1, "B"), "File") == @"C:\Projects\Tower\Tower-MEP.nwc" && (string)F(Item(x1, "B"), "Name") == "Rectangular Duct" && (string)F(Item(x1, "B"), "Layer") == "Level 2", "XML: item 2 file path, name and layer");
    Check((string)F(xml[1], "Status") == "active" && (string)F(xml[2], "Status") == "approved" && (string)F(xml[3], "Status") == "resolved", "XML: Reviewed = active; Approved and Resolved kept");
    Check((string)F(xml[3], "Test") == "MEP vs ARC clearance" && (double?)F(xml[3], "DistanceMm") == 30, "XML: second test, positive distance (clearance)");

    var (html, htmlFormat) = Parse("navisworks-report.html");
    Check(htmlFormat == "Navisworks HTML" && html.Count == 2, $"HTML report: the clash table only, not the summary (got {html.Count})");
    var h1 = html[0];
    Check((string)F(h1, "Test") == "STR vs MEP" && (string)F(h1, "Name") == "Clash1" && (string)F(h1, "Status") == "new" && (double?)F(h1, "DistanceMm") == -120, "HTML: test from the heading, name, status, distance");
    Check((long?)F(Item(h1, "A"), "ElementId") == 345678 && (long?)F(Item(h1, "B"), "ElementId") == 912345 && (string)F(Item(h1, "B"), "Type") == "Ducts", "HTML: Item 1 / Item 2 columns read by their group header");
    Check(((double[])F(h1, "Point"))?.SequenceEqual(new[] { 10.5, 4.2, 3.1 }) == true, "HTML: clash point");
    Check(F(Item(html[1], "B"), "ElementId") == null && (string)F(html[1], "Status") == "active", "HTML: an entity handle is not taken as a Revit element id");

    var toMm = PM("ToMm");
    Check((double?)toMm.Invoke(null, new object[] { "-0.39 ft", null }) == -118.9 && (double?)toMm.Invoke(null, new object[] { "-35", null }) == -35 && (double?)toMm.Invoke(null, new object[] { "-12 mm", "m" }) == -12, "distances: feet, bare millimetres, the unit beats the report's units");
    var same = PM("SameModel");
    bool Same(string f, string m) => (bool)same.Invoke(null, new object[] { f, m });
    Check(Same("Tower-STR.nwc", "Tower-STR") && Same(@"C:\x\Tower_STR.nwc", "Tower STR") && Same("Tower-STR.nwc", "Tower-STR_jsmith") && !Same("Tower-STR.nwc", "Tower-MEP") && !Same("STR.nwc", "STRUCTURE"),
      "report file matched to a loaded model by name (extension, punctuation, local copy), not by a short prefix");

    // 10. A re-import keeps ACE approvals and takes Navisworks Approved / Resolved; Navisworks "active" is not new on first import.
    var stored = List(C("p1", "approved", "a", "b", "Ducts", "Structural Framing", "L2"), C("p2", "active", "c", "d", "Ducts", "Structural Framing", "L2"));
    var found = List(C("p1", "new", "a", "b", "Ducts", "Structural Framing", "L2"), C("p2", "new", "c", "d", "Ducts", "Structural Framing", "L2"),
                     C("p3", "new", "e", "f", "Ducts", "Structural Framing", "L2"), C("p4", "new", "g", "h", "Ducts", "Structural Framing", "L2"));
    var imp = (IList)merge.Invoke(null, new object[] { stored, found, null, t0.AddDays(1) });
    var reported = new Dictionary<string, string> { ["p1"] = "active", ["p2"] = "resolved", ["p3"] = "active", ["p4"] = "approved" };
    M("ApplyReported").Invoke(null, new object[] { imp, reported, new HashSet<string> { "p1", "p2" } });
    string Si(string k) => (string)F(imp.Cast<object>().First(c => (string)F(c, "Key") == k), "Status");
    Check(Si("p1") == "approved" && Si("p2") == "resolved" && Si("p3") == "active" && Si("p4") == "approved",
      $"import status: ACE approval kept, Navisworks resolved/approved taken, reported active is active ({Si("p1")}, {Si("p2")}, {Si("p3")}, {Si("p4")})");

    // 11. clash_resolve: material, join order, the fix per pair, the framing end, the guard comparison and the warnings rule.
    var rl = a.GetType("AceRevitMcp.Coordination.ClashResolveLogic");
    MethodInfo R(string n) => rl.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    var rSideT = rl.GetNestedType("Side", BindingFlags.NonPublic | BindingFlags.Public); var pairT = rl.GetNestedType("Pair", BindingFlags.NonPublic | BindingFlags.Public);
    var optT = rl.GetNestedType("Options", BindingFlags.NonPublic | BindingFlags.Public); var stateT = rl.GetNestedType("ElementState", BindingFlags.NonPublic | BindingFlags.Public);
    object Set(object o, params (string, object)[] vals) { foreach (var (n, v) in vals) o.GetType().GetField(n).SetValue(o, v); return o; }
    string Mat(string cat, string st, string mc) => (string)R("MaterialOf").Invoke(null, new object[] { cat, st, mc });
    Check(Mat("OST_StructuralColumns", "Steel", null) == "steel" && Mat("OST_StructuralFraming", "Concrete", null) == "concrete" && Mat("OST_StructuralFraming", "PrecastConcrete", null) == "concrete",
      "material: the family's structural material type first");
    Check(Mat("OST_Walls", null, null) == "concrete" && Mat("OST_Walls", null, "Masonry") == "other" && Mat("OST_Floors", null, "Generic") == "concrete" && Mat("OST_StructuralColumns", "Undefined", "Metal") == "steel" && Mat("OST_StructuralColumns", "Undefined", null) == "other",
      "material: then the material class, then the category (walls / floors concrete, unknown columns other)");
    var order = (string[])rl.GetField("DefaultOrder").GetValue(null);
    string Win(string x, string y) => (string)R("Winner").Invoke(null, new object[] { order, x, y });
    Check(Win("OST_StructuralColumns", "OST_StructuralFraming") == "A" && Win("OST_Floors", "OST_Walls") == "B" && Win("OST_Walls", "OST_Walls") == null && Win("OST_Ramps", "OST_Walls") == null,
      "join order: categories only; same category or not in the order = no category winner");
    Check(order.Take(5).SequenceEqual(new[] { "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming" }) && Win("OST_Floors", "OST_StructuralFraming") == "A",
      "1.12.8 default priority: foundation > column > wall > floor > beam (a floor cuts a beam)");
    (int, double) EO(double len, double mMin, double mMax, double iMin, double iMax) { var t = R("EndOverlap").Invoke(null, new object[] { len, mMin, mMax, iMin, iMax, 0.016 }); return ((int)t.GetType().GetField("Item1").GetValue(t), (double)t.GetType().GetField("Item2").GetValue(t)); }
    var e0 = EO(20, -0.1, 20, -0.1, 0.25); var e1 = EO(20, 0, 20.2, 19.6, 20.2); var em = EO(20, 0, 20, 9, 11); var ew = EO(20, 0, 20, 0, 20);
    Check(e0.Item1 == 0 && Math.Abs(e0.Item2 - 0.35) < 1e-9 && e1.Item1 == 1 && Math.Abs(e1.Item2 - 0.6) < 1e-9 && em.Item1 == -1 && ew.Item1 == -1,
      "framing end: the start or end overlap and its cut to the face; mid-span or the whole member = no end");
    object RSide(string cat, string mat, int end = -2, double cut = 0, double len = 0, double vol = 1, bool host = true) =>
      Set(Activator.CreateInstance(rSideT, true), ("Category", cat), ("Material", mat), ("End", end), ("Cut", cut), ("Length", len), ("Volume", vol), ("Host", host));
    object Pair(object sa, object sb, double overlap = 0.01, bool joined = false, bool aCutsB = false, string kind = "hard") =>
      Set(Activator.CreateInstance(pairT, true), ("A", sa), ("B", sb), ("Overlap", overlap), ("Joined", joined), ("ACutsB", aCutsB), ("ClashKind", kind));
    var opt = Activator.CreateInstance(optT, true);
    object Dec(object pr, object o = null) => R("Classify").Invoke(null, new object[] { pr, o ?? opt });
    string K(object d) => (string)F(d, "Kind"); string Ch(object d) => (string)F(d, "Changed");
    var dj = Dec(Pair(RSide("OST_StructuralFraming", "concrete"), RSide("OST_StructuralColumns", "concrete")));
    Check(K(dj) == "join" && Ch(dj) == "B" && (string)F(dj, "Material") == "concrete", "concrete beam x concrete column: join, the column cuts");
    var ds = Dec(Pair(RSide("OST_Floors", "concrete"), RSide("OST_Walls", "concrete"), joined: true, aCutsB: true));
    Check(K(ds) == "switch-join" && Ch(ds) == "B", "joined, but the slab cuts the wall: switch-join");
    Check(K(Dec(Pair(RSide("OST_Walls", "concrete"), RSide("OST_Floors", "concrete"), joined: true, aCutsB: true))) == null, "joined in the right order and still clashing: report only");
    var dc = Dec(Pair(RSide("OST_StructuralFraming", "steel", 1, 0.27, 20), RSide("OST_StructuralColumns", "concrete")));
    Check(K(dc) == "cutback" && Ch(dc) == "A" && (int)F(dc, "End") == 1 && (string)F(dc, "Material") == "mixed", "steel beam end into a concrete column: cutback of the beam end (mixed)");
    var dcope = Dec(Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 20), RSide("OST_StructuralFraming", "steel", 0, 0.27, 10)));
    Check(K(dcope) == "cope" && Ch(dcope) == "B", "steel member end into a steel beam mid-span: coping of the running member");
    var noCope = Set(Activator.CreateInstance(optT, true), ("Coping", false));
    Check(K(Dec(Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 20), RSide("OST_StructuralFraming", "steel", 0, 0.27, 10)), noCope)) == "cutback", "coping off: a cutback to the supporting beam's face instead");
    Check(K(Dec(Pair(RSide("OST_StructuralFraming", "steel", 0, 0.2, 10), RSide("OST_StructuralFraming", "steel", 1, 0.2, 10)))) == null, "two members ending at one node: report only");
    Check(K(Dec(Pair(RSide("OST_StructuralFraming", "steel", 0, 3.0, 20), RSide("OST_StructuralColumns", "steel")))) == null, "an overlap deeper than 600 mm is not a trim: report only");
    Check(K(Dec(Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 20), RSide("OST_Floors", "concrete")))) == null, "steel beam x concrete slab (mixed): report only");
    Check(K(Dec(Pair(RSide("OST_Walls", "concrete", vol: 2), RSide("OST_Walls", "concrete", vol: 2), overlap: 1.95))) == null, "near-identical geometry (duplicate): report only");
    Check(K(Dec(Pair(RSide("OST_Walls", "concrete", vol: 10), RSide("OST_StructuralColumns", "concrete", vol: 1), overlap: 1))) == "join", "a column inside a wall: join (the column cuts, it stays)");
    Check(K(Dec(Pair(RSide("OST_Walls", "concrete", vol: 1), RSide("OST_StructuralColumns", "concrete", vol: 10), overlap: 1))) == null, "a wall inside a column would be cut away: report only");
    Check(K(Dec(Pair(RSide("OST_Walls", "concrete", host: false), RSide("OST_Walls", "concrete")))) == null, "an element in a linked model: report only");
    string PId(string k, long cutter, long cut, int end) => (string)R("PlanId").Invoke(null, new object[] { k, cutter, cut, end });
    Check(PId("join", 42, 7, -1) == "join:42>7" && PId("join", 7, 42, -1) == "join:7>42" && PId("cutback", 9, 5, 1) == "cutback:9>5:1" && PId("cope", 9, 5, 0) == "cope:9>5:0",
      "plan id: kind, cutter > cut, and the end for a cutback or cope (direction shown, stable across preview and apply)");
    var uq = (List<string>)R("UniqueIds").Invoke(null, new object[] { new[] { "join:1>2", "cutback:3>4:0", "join:1>2" } });
    Check(uq.SequenceEqual(new[] { "join:1>2", "cutback:3>4:0", "join:1>2#2" }), "plan id: two steps on the same pair get distinct ids");
    // 1. Plan hash: the same steps in any order = the same hash; another cut, end, kind or cutter = another hash.
    string PH(params (string, long, long, int, double, long)[] st) => (string)R("PlanHash").Invoke(null, new object[] { st.ToList() });
    var ph1 = PH(("join", 1, 2, -1, 0, 0), ("cutback", 9, 5, 1, 82.64, 0));
    Check(ph1.Length == 64 && ph1 == PH(("cutback", 9, 5, 1, 82.61, 0), ("join", 1, 2, -1, 0, 0)), "plan hash: sha256, independent of step order, cut rounded to 0.1 mm");
    Check(ph1 != PH(("join", 1, 2, -1, 0, 0), ("cutback", 9, 5, 1, 90, 0)) && ph1 != PH(("join", 1, 2, -1, 0, 0), ("cutback", 9, 5, 0, 82.6, 0))
      && ph1 != PH(("join", 2, 1, -1, 0, 0), ("cutback", 9, 5, 1, 82.6, 0)) && ph1 != PH(("join", 1, 2, -1, 0, 0), ("cope", 9, 5, 1, 82.6, 9)) && ph1 != PH(("join", 1, 2, -1, 0, 0)),
      "plan hash: a changed cut, end, direction, kind or a missing step changes the hash (the apply is refused: plan-changed)");
    var expl = R("Explain").Invoke(null, new object[] { "plan-changed", null, "the model changed since the preview: run the dry run again" });
    Check(((string)expl.GetType().GetField("Item1").GetValue(expl)).StartsWith("the model changed since the preview"), "plan-changed block says the model changed since the preview");
    // 3. A fix must help.
    (long, string, string, string) Wz(object t) => ((long)t.GetType().GetField("Item1").GetValue(t), (string)t.GetType().GetField("Item2").GetValue(t), (string)t.GetType().GetField("Item3").GetValue(t), (string)t.GetType().GetField("Item4").GetValue(t));
    List<(long, string, string, string)> Worse(int b, int aft, List<(string, long, long)> now, List<(string, long, double, double?)> trims) =>
      ((IEnumerable)R("Worse").Invoke(null, new object[] { b, aft, now, new HashSet<string> { "k1", "k2" }, new HashSet<long> { 10, 20 }, trims })).Cast<object>().Select(Wz).ToList();
    Check(Worse(2, 1, new List<(string, long, long)> { ("k2", 10, 30) }, new List<(string, long, double, double?)> { ("k1", 10, 83, null), ("k2", 10, 83, 40) }).Count == 0,
      "fix helps: fewer clashes, one gone, one shallower = no mismatch");
    var wz = Worse(2, 3, new List<(string, long, long)> { ("k1", 10, 30), ("k2", 20, 30), ("k9", 99, 20) }, new List<(string, long, double, double?)> { ("k1", 10, 83, 83) });
    Check(wz.Count == 3 && wz.All(x => x.Item2.StartsWith("fix made it worse")) && wz.Any(x => x.Item1 == 20 && x.Item2.Contains("new clash")) && wz.Any(x => x.Item1 == 10 && x.Item2.Contains("as deeply")),
      $"fix made it worse: more clashes, a new clash on a touched element, a trimmed member as deep as before ({wz.Count})");
    Check(Worse(1, 1, new List<(string, long, long)> { ("k7", 55, 66) }, null).Count == 0, "a new clash between untouched elements is not blamed on the fix (count not risen)");

    // Guard: before vs after.
    var stateListT = typeof(List<>).MakeGenericType(stateT); var stateDictT = typeof(Dictionary<,>).MakeGenericType(typeof(long), stateT);
    object State(long id, double[] loc, int tags, long type = 5, bool exists = true, params (long, string)[] dims) {
      var st = Set(Activator.CreateInstance(stateT, true), ("Id", id), ("Location", loc), ("Tags", tags), ("TypeId", type), ("Exists", exists));
      var dd = (IDictionary)F(st, "Dimensions"); foreach (var (k, v) in dims) dd[k] = v; return st; }
    string DT(int n, long[] ids, double?[] vals) => (string)R("DimensionText").Invoke(null, new object[] { n, ids, vals });
    var dimA = DT(2, new long[] { 11, 3 }, new double?[] { 10.0 });
    var bl = (IList)Activator.CreateInstance(stateListT);
    bl.Add(State(1, new[] { 0.0, 0, 0, 10, 0, 0 }, 2, 5, true, (900L, dimA))); bl.Add(State(2, new[] { 1.0, 1, 0, 0 }, 1)); bl.Add(State(3, null, 0));
    object CmpT(ICollection<long> trimmed, params object[] after) { var dct = (IDictionary)Activator.CreateInstance(stateDictT); foreach (var st in after) dct[(long)F(st, "Id")] = st;
      return R("Compare").Invoke(null, new object[] { bl, dct, trimmed, null }); }
    IList Cmp(params object[] after) { var t = CmpT(null, after); return (IList)t.GetType().GetField("Item1").GetValue(t); }
    string W(object m) => (string)m.GetType().GetField("Item2").GetValue(m);
    Check(Cmp(State(1, new[] { 0.0, 0, 0, 10, 0, 0 }, 2, 5, true, (900L, DT(2, new long[] { 3, 11 }, new double?[] { 10.0000000001 }))), State(2, new[] { 1.0, 1, 0, 0 }, 1), State(3, null, 0)).Count == 0,
      "guard: nothing moved, same tags and dimensions = no mismatch");
    var mm = Cmp(State(1, new[] { 0.0, 0, 0, 10.001, 0, 0 }, 1, 5, true), State(2, new[] { 1.0, 1, 0, 0 }, 1, 6), State(3, null, 0, 5, false)).Cast<object>().Select(W).ToList();
    Check(mm.Contains("location") && mm.Contains("tags") && mm.Contains("dimension 900") && mm.Contains("type") && mm.Contains("element"),
      $"guard: a moved end, a lost tag, a lost dimension, a new type and a deleted element are mismatches ({string.Join(", ", mm)})");
    Check(!(bool)R("SameDimension").Invoke(null, new object[] { dimA, DT(2, new long[] { 3, 11 }, new double?[] { 10.1 }) }) && !(bool)R("SameDimension").Invoke(null, new object[] { dimA, DT(1, new long[] { 3 }, new double?[] { 10.0 }) }),
      "guard: a dimension whose value or references changed is a mismatch");
    // 4. A changed value on a dimension to a trimmed member is expected (changedValues); a lost reference or a join-only element is not.
    object S1(string dim) => State(1, new[] { 0.0, 0, 0, 10, 0, 0 }, 2, 5, true, (900L, dim));
    object S2() => State(2, new[] { 1.0, 1, 0, 0 }, 1); object S3() => State(3, null, 0);
    var tv = CmpT(new HashSet<long> { 3 }, S1(DT(2, new long[] { 3, 11 }, new double?[] { 9.7 })), S2(), S3());
    var tvM = (IList)tv.GetType().GetField("Item1").GetValue(tv); var tvC = (IList)tv.GetType().GetField("Item2").GetValue(tv);
    Check(tvM.Count == 0 && tvC.Count == 1 && (long)tvC[0].GetType().GetField("Item2").GetValue(tvC[0]) == 900, "guard: a cutback member's dimension value change goes to changedValues, not mismatches");
    var rel = R("Compare").Invoke(null, new object[] { bl, ((Func<IDictionary>)(() => { var d = (IDictionary)Activator.CreateInstance(stateDictT); foreach (var x in new[] { State(1, new[] { 0.0, 0, 0, 9.6, 0, 0 }, 2, 5, true, (900L, dimA)), State(2, new[] { 1.0, 1, 0, 0 }, 1), State(3, null, 0) }) d[(long)F(x, "Id")] = x; return d; }))(), new HashSet<long> { 1 }, new HashSet<long> { 1 } });
    Check(((IList)rel.GetType().GetField("Item1").GetValue(rel)).Count == 0, "guard: a member trimmed to a face may change its location (checked against the planned end instead)");
    var tl = CmpT(new HashSet<long> { 3 }, S1(DT(1, new long[] { 11 }, new double?[] { 9.7 })), S2(), S3());
    Check(((IList)tl.GetType().GetField("Item1").GetValue(tl)).Count == 1, "guard: a trimmed member's dimension that lost a reference is still a mismatch");
    var tj = CmpT(new HashSet<long>(), S1(DT(2, new long[] { 3, 11 }, new double?[] { 9.7 })), S2(), S3());
    Check(((IList)tj.GetType().GetField("Item1").GetValue(tj)).Count == 1 && ((IList)tj.GetType().GetField("Item2").GetValue(tj)).Count == 0, "guard: a changed value for join-only elements stays a mismatch");
    // 5. Warnings: per (text + ids) count, any rise fails; re-posted (deleted by ModelGuard) is not counted twice; cleared is fine.
    var wl = new List<(string Text, long[] Ids)> { ("Walls overlap", new long[] { 1, 2 }) };
    (int, List<string>, bool) Warn(List<(string Text, long[] Ids)> posted, List<(string Text, long[] Ids)> inModel) { var t = R("Warnings").Invoke(null, new object[] { wl, inModel, posted });
      return ((int)t.GetType().GetField("Item1").GetValue(t), (List<string>)t.GetType().GetField("Item2").GetValue(t), (bool)t.GetType().GetField("Item3").GetValue(t)); }
    var none0 = new List<(string Text, long[] Ids)>();
    var w1 = Warn(new List<(string Text, long[] Ids)> { ("Walls overlap", new long[] { 2, 1 }) }, wl);
    Check(w1.Item3 && w1.Item2.Count == 0 && w1.Item1 == 1, "warnings: a re-posted existing warning is not new (not counted twice)");
    var w2 = Warn(new List<(string Text, long[] Ids)> { ("Can't keep elements joined", new long[] { 7, 8 }) }, wl);
    Check(!w2.Item3 && w2.Item2.Count == 1 && w2.Item2[0].StartsWith("Can't keep elements joined") && w2.Item1 == 2, "warnings: a fresh warning on a touched element fails and is listed");
    var w3 = Warn(new List<(string Text, long[] Ids)> { ("Room not enclosed", new long[] { 50 }) }, none0);
    Check(!w3.Item3 && w3.Item2.Count == 1 && w3.Item1 == 1, "warnings: any fresh warning fails, even one on an untouched element while another was cleared");
    var w4 = Warn(none0, none0);
    Check(w4.Item3 && w4.Item1 == 0, "warnings: a cleared warning is fine");
    var w5 = Warn(none0, new List<(string Text, long[] Ids)> { ("Walls overlap", new long[] { 1, 2 }), ("Walls overlap", new long[] { 1, 2 }) });
    Check(!w5.Item3 && w5.Item2.Count == 1, "warnings: a second copy of the same warning is a rise");

    // 12. 1.12.1: cutback method on joined ends, a step must clear its clash, warning records, reopened status, report-only reasons.
    string CM(bool ext, bool join) => (string)R("CutbackMethod").Invoke(null, new object[] { ext, join });
    Check(CM(true, true) == "extension" && CM(false, true) == "join-cutback" && CM(false, false) == null,
      "cutback: the extension on an end that is not joined, the join cutback on a joined end (extension read-only), none when neither can be written");
    string Drop(double b, double aft) => (string)R("DropReason").Invoke(null, new object[] { b, aft, 15.0 });
    Check(Drop(83, 0) == null && Drop(83, 14) == null && Drop(83, 15) == "did not clear the clash (depth 83 -> 15 mm)" && Drop(29, 29) == "did not clear the clash (depth 29 -> 29 mm)",
      "a step is kept only when its overlap is below the tolerance after it; otherwise dropped with the depths");
    var recT = a.GetType("AceRevitMcp.Util.ChangeTracker+Recording");
    var split = recT.GetMethod("SplitWarningRecords", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    var label = (string)recT.GetField("WarningRecordLabel", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
    var sp = split.Invoke(null, new object[] { new long[] { 5, 7, 9, 11 }, new Dictionary<long, string> { [5] = "Walls", [9] = label }, new HashSet<long> { 7 } });
    var spRec = (List<long>)sp.GetType().GetField("Item1").GetValue(sp); var spDel = (List<long>)sp.GetType().GetField("Item2").GetValue(sp);
    Check(spRec.SequenceEqual(new long[] { 7, 9 }) && spDel.SequenceEqual(new long[] { 5, 11 }), "change summary: warning records (known before, or labelled) are not deleted elements");
    var wrt = (string)recT.GetMethod("WarningRecordText", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new object[] { 2 });
    Check(wrt == "2 resolved warning records" && !wrt.Contains("Deletes"), "change summary: \"2 resolved warning records\", never \"Deletes 2 elements\"");
    // Reopened: fixed (resolved by a re-test), then the undo brings it back: shown as reopened, not new; next run active.
    var st0 = List(C("r1", "active", "a", "b", "Structural Framing", "Structural Columns", "L1"), C("r2", "active", "c", "d", "Walls", "Floors", "L1"));
    var afterFix = (IList)merge.Invoke(null, new object[] { st0, List(C("r2", "new", "c", "d", "Walls", "Floors", "L1")), null, t0.AddDays(1) });
    var afterUndo = (IList)merge.Invoke(null, new object[] { afterFix, List(C("r1", "new", "a", "b", "Structural Framing", "Structural Columns", "L1"), C("r2", "new", "c", "d", "Walls", "Floors", "L1"), C("r3", "new", "e", "f", "Walls", "Walls", "L1")), null, t0.AddDays(2) });
    var disp = M("DisplayStatus");
    string Ds(IList l, string k) => (string)disp.Invoke(null, new object[] { l.Cast<object>().First(c => (string)F(c, "Key") == k) });
    Check(Ds(afterFix, "r1") == "resolved" && Ds(afterUndo, "r1") == "reopened" && Ds(afterUndo, "r2") == "active" && Ds(afterUndo, "r3") == "new",
      $"status: resolved then found again (same key) shows as reopened, not new ({Ds(afterUndo, "r1")}, {Ds(afterUndo, "r2")}, {Ds(afterUndo, "r3")})");
    var again = (IList)merge.Invoke(null, new object[] { afterUndo, List(C("r1", "new", "a", "b", "Structural Framing", "Structural Columns", "L1")), null, t0.AddDays(3) });
    Check(Ds(again, "r1") == "active" && (int)F(again.Cast<object>().First(c => (string)F(c, "Key") == "r1"), "Reopened") == 1, "status: a reopened clash still there next run is active and keeps its reopened count");
    // Report-only reasons, each with its own recommendation.
    string Code(object d) => (string)F(d, "Code");
    var reasons = new (object Pair, string Code)[] {
      (Pair(RSide("OST_StructuralFraming", "steel", 0, 0.2, 10), RSide("OST_StructuralFraming", "steel", 1, 0.2, 10)), "node"),
      (Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 10), RSide("OST_StructuralFraming", "steel", -1, 0, 10)), "crossing"),
      (Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 20), RSide("OST_StructuralColumns", "steel", -1)), "through-column"),
      (Pair(RSide("OST_StructuralColumns", "steel", 1, 0, 12), RSide("OST_StructuralFraming", "steel", -1, 0, 20)), "column-into-beam"),
      (Pair(RSide("OST_StructuralColumns", "steel", 1), RSide("OST_StructuralColumns", "steel", 0)), "columns"),
      (Pair(RSide("OST_StructuralFraming", "steel", -2, 0, 20), RSide("OST_StructuralFraming", "steel", -1, 0, 20)), "end-unknown"),
      (Pair(RSide("OST_StructuralFraming", "other", 1, 0.2, 20), RSide("OST_StructuralColumns", "concrete")), "material-unknown"),
      (Pair(RSide("OST_StructuralFraming", "steel", -1, 0, 20), RSide("OST_Floors", "concrete")), "steel-on-concrete"),
      (Pair(RSide("OST_StructuralColumns", "steel", -1), RSide("OST_StructuralFraming", "concrete", -1, 0, 20)), "mixed"),
      (Pair(RSide("OST_StructuralFraming", "steel", 0, 3.0, 20), RSide("OST_StructuralColumns", "steel")), "too-deep"),
      (Pair(RSide("OST_StructuralColumns", "steel", 0), RSide("OST_StructuralFoundation", "concrete")), "column-base"),
    };
    var gotR = reasons.Select(x => (Want: x.Code, D: Dec(x.Pair))).ToList();
    var bad = gotR.Where(x => K(x.D) != null || Code(x.D) != x.Want).Select(x => $"{x.Want}: {K(x.D) ?? Code(x.D)}").ToList();
    Check(bad.Count == 0, $"report only: each unfixable pair has its own reason code ({string.Join("; ", bad)})");
    Check(gotR.Select(x => (string)F(x.D, "Recommendation")).Distinct().Count() == gotR.Count && gotR.All(x => !((string)F(x.D, "Why")).Contains("no safe automatic fix")),
      "report only: every reason has its own recommendation, none says only \"no safe automatic fix\"");

    // 13. 1.12.2: structural material filter (shared classification) and interference report rows.
    var smT = a.GetType("AceRevitMcp.Coordination.StructuralMaterial");
    MethodInfo SM(string n) => smT.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    string Cls(string cat, string st, string mc) => (string)SM("Classify").Invoke(null, new object[] { cat, st, mc });
    Check(Cls("OST_StructuralFraming", "Steel", null) == "steel" && Cls("OST_StructuralColumns", "PrecastConcrete", null) == "precast" && Cls("OST_StructuralFraming", "Wood", null) == "timber"
      && Cls("OST_Walls", null, "Masonry") == "masonry" && Cls("OST_Walls", null, "Brick") == "masonry" && Cls("OST_StructuralColumns", "Undefined", "Metal") == "steel"
      && Cls("OST_Floors", null, "Generic") == "concrete" && Cls("OST_StructuralColumns", "Undefined", null) == "unknown" && Cls("OST_StructuralFraming", "Other", "Plastic") == "other",
      "material: steel, precast, timber, masonry, concrete by category, unknown when nothing says");
    Check(Mat("OST_StructuralColumns", "PrecastConcrete", null) == "concrete" && Mat("OST_Walls", null, "Masonry") == "other" && Mat("OST_StructuralColumns", "Undefined", null) == "other",
      "clash_resolve's fix material comes from the shared classification (precast = concrete, masonry / unknown = other)");
    bool Keep(string m, string[] inc, string[] exc) => (bool)SM("Keep").Invoke(null, new object[] { m, inc, exc });
    var noMat = new string[0];
    Check(!Keep("steel", noMat, new[] { "steel" }) && Keep("concrete", noMat, new[] { "steel" }) && Keep("unknown", noMat, new[] { "steel" }),
      "exclude_materials: [steel] leaves steel out and keeps concrete and unknown");
    Check(Keep("concrete", new[] { "concrete" }, noMat) && !Keep("steel", new[] { "concrete" }, noMat) && Keep("unknown", new[] { "concrete" }, noMat) && !Keep("unknown", new[] { "concrete" }, new[] { "unknown" }),
      "materials: [concrete] keeps concrete and unknown (unknown only goes when excluded)");
    Check(((string[])SM("Parse").Invoke(null, new object[] { new[] { "Steel", "wood", "steel" }, "materials" })).SequenceEqual(new[] { "steel", "timber" }), "material names: case-insensitive, wood = timber, each once");
    var parseErr = false; try { SM("Parse").Invoke(null, new object[] { new[] { "glass" }, "materials" }); } catch (TargetInvocationException) { parseErr = true; }
    Check(parseErr, "material names: an unknown name is refused");

    var il = a.GetType("AceRevitMcp.Coordination.InterferenceLogic");
    MethodInfo IL(string n) => il.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    var sn = IL("SplitName").Invoke(null, new object[] { "UC-Universal Columns : 254x254x73UC" }); var sn2 = IL("SplitName").Invoke(null, new object[] { "Generic - 200mm" });
    Check((string)sn.GetType().GetField("Item1").GetValue(sn) == "UC-Universal Columns" && (string)sn.GetType().GetField("Item2").GetValue(sn) == "254x254x73UC"
      && sn2.GetType().GetField("Item1").GetValue(sn2) == null && (string)sn2.GetType().GetField("Item2").GetValue(sn2) == "Generic - 200mm", "row names: \"Family : Type\" split; a system type name is the type");
    object CX(string key, string status, string ua, string ub, double depth, int reopened = 0, double x = 1000) { var c = C(key, status, ua, ub, "Walls", "Floors", "L1", null, "STR", depth); Set(c, ("Reopened", reopened), ("X", x), ("Y", 2500.0), ("Z", 3000.0), ("IdA", 11L), ("IdB", 22L), ("NameA", "Basic Wall : W200"), ("NameB", "F250")); return c; }
    var ic = List(CX("x1", "new", "w1", "f1", 40), CX("x2", "active", "w1", "f2", 90), CX("x3", "new", "w9", "f9", 20, 1), CX("x4", "approved", "w5", "f5", 30), CX("x5", "resolved", "w6", "f6", 10));
    var rowsT = (IList)IL("Rows").Invoke(null, new object[] { ic, (500.0, 500.0, 0.0), null, null });
    object RF(object r, string f) => r.GetType().GetField(f).GetValue(r);
    var rk = rowsT.Cast<object>().Select(r => (string)RF(r, "ClashKey")).ToList();
    Check(rowsT.Count == 5 && rk.SequenceEqual(new[] { "x2", "x1", "x3", "x4", "x5" }), $"rows: one per clash, by issue (largest first, deepest first), then approved, then resolved ({string.Join(",", rk)})");
    var r0 = rowsT[0];
    Check((int?)RF(r0, "Issue") == 1 && (int?)RF(rowsT[2], "Issue") == 2 && RF(rowsT[3], "Issue") == null && (string)RF(r0, "AFamily") == "Basic Wall" && (string)RF(r0, "AType") == "W200" && (string)RF(r0, "BType") == "F250"
      && (double)RF(r0, "X") == 0.5 && (double)RF(r0, "Y") == 2.0 && (double)RF(r0, "Z") == 3.0 && (long)RF(r0, "AId") == 11,
      "rows: issue numbers, family / type, project coordinates in metres from the base point");
    Check((string)RF(rowsT[2], "Status") == "reopened" && (string)RF(r0, "Status") == "active" && (string)RF(rowsT[1], "Status") == "new" && (string)RF(rowsT[3], "Status") == "approved" && (string)RF(rowsT[4], "Status") == "resolved",
      "rows: status new / active / reopened / approved / resolved");
    var rowListT = typeof(List<>).MakeGenericType(a.GetType("AceRevitMcp.Coordination.InterferenceRow"));
    var rl2 = (IList)Activator.CreateInstance(rowListT); foreach (var r in rowsT) rl2.Add(r);
    var cnt = (Dictionary<string, int>)IL("Counts").Invoke(null, new object[] { rl2 });
    Check(cnt["clashes"] == 5 && cnt["issues"] == 2 && cnt["open"] == 3 && cnt["new"] == 1 && cnt["active"] == 1 && cnt["reopened"] == 1 && cnt["approved"] == 1 && cnt["resolved"] == 1, "rows: counts by status and issues");
    var names2 = new Dictionary<string, (string, string)> { ["w1"] = ("Basic Wall", "RC 250") };
    var rowsN = (IList)IL("Rows").Invoke(null, new object[] { ic, (0.0, 0.0, 0.0), names2, null });
    Check((string)RF(rowsN[0], "AType") == "RC 250" && (string)IL("RunDate").Invoke(null, new object[] { ic, null }) == "2026-09-01T00:00:00", "rows: family / type read from the model win; run date from the clashes");

    // 14. 1.12.3: a stored run with zero clashes is a report with no rows; only a test that never ran is an error.
    var runT = a.GetType("AceRevitMcp.Coordination.ClashRun");
    var runListT = typeof(List<>).MakeGenericType(runT);
    object Run(string test, DateTime time) { var r = Activator.CreateInstance(runT, true); runT.GetField("Test").SetValue(r, test); runT.GetField("Time").SetValue(r, time); return r; }
    var runs = (IList)Activator.CreateInstance(runListT); runs.Add(Run("ST vs ST", t0.AddDays(3))); runs.Add(Run("STR vs MEP", t0.AddDays(1))); runs.Add(Run("ST vs ST", t0.AddDays(5)));
    var forSt = (IList)IL("RunsFor").Invoke(null, new object[] { runs, "st vs st" });
    Check(forSt.Count == 2 && (DateTime)runT.GetField("Time").GetValue(forSt[0]) == t0.AddDays(5), "runs: the named test's runs, latest first (name case-insensitive)");
    string Nothing(string test, int clashes, int nRuns) => (string)IL("NothingToReport").Invoke(null, new object[] { test, clashes, nRuns, "M" });
    Check(Nothing("ST vs ST", 0, 1) == null && Nothing("ST vs ST", 3, 0) == null && Nothing("ST vs ST", 0, 0).Contains("never run") && Nothing(null, 0, 0).StartsWith("No clash results"),
      "empty report: a run with 0 clashes is no error; a test never run is");
    var emptyRows = (IList)IL("Rows").Invoke(null, new object[] { List(), (0.0, 0.0, 0.0), null, null });
    var emptyList = (IList)Activator.CreateInstance(rowListT);
    var emptyCounts = (Dictionary<string, int>)IL("Counts").Invoke(null, new object[] { emptyList });
    Check(emptyRows.Count == 0 && emptyCounts.Values.All(v => v == 0) && emptyCounts.ContainsKey("clashes") && emptyCounts.ContainsKey("issues"), "empty report: rows [] and every count 0");
    Check((string)IL("RunDate").Invoke(null, new object[] { List(), forSt }) == t0.AddDays(5).ToString("yyyy-MM-ddTHH:mm:ss"), "empty report: the run date comes from the stored run");

    // 15. 1.12.4: open_model's dialog answers and transmitted handling.
    var odl = a.GetType("AceRevitMcp.Commands.OpenDialogLogic");
    MethodInfo OD(string n) => odl.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    object Ans(string id, string msg, bool task) => OD("Answer").Invoke(null, new object[] { id, msg, task });
    object AF(object t, string f) => t.GetType().GetField(f).GetValue(t);
    var unres = Ans("TaskDialog_Unresolved_References", "Revit could not find or read 2 references.", true);
    Check((int?)AF(unres, "Item1") == 1002 && ((string)AF(unres, "Item2")).StartsWith("Ignore and continue"), "dialogs: unresolved references -> Ignore and continue (CommandLink2)");
    var unresTxt = Ans("", "Revit could not find or read 3 references. What do you want to do?", true);
    var unresMb = Ans("", "Revit could not find or read 3 references.", false);
    Check((int?)AF(unresTxt, "Item1") == 1002 && AF(unresMb, "Item1") == null, "dialogs: message fallback only for a task dialog without an id");
    var trans = Ans("TaskDialog_Transmitted_File", "This file has been transmitted.", true);
    var transTxt = Ans("", "This model was transmitted.", true);
    Check(AF(trans, "Item1") == null && AF(trans, "Item2") == null && AF(transTxt, "Item1") == null && ((string)AF(trans, "Item3")).Contains("person"), "dialogs: the transmitted dialog is never answered automatically");
    var other = Ans("TaskDialog_Some_Other", "Something else.", true);
    var otherText = Ans("TaskDialog_Some_Other", "could not find or read references", true);
    Check(AF(other, "Item1") == null && AF(other, "Item2") == null && AF(otherText, "Item1") == null, "dialogs: an unknown dialog is left to a person (the text fallback never beats a known other id)");
    object TD(bool t, bool w, string d, bool ok) => OD("TransmittedDetach").Invoke(null, new object[] { t, w, d, ok });
    Check((string)AF(TD(true, true, "none", false), "Item1") == "preserve_worksets" && AF(TD(true, true, "none", false), "Item2") != null, "transmitted workshared model opens detached with worksets kept");
    Check((string)AF(TD(true, true, "none", true), "Item1") == "none" && (string)AF(TD(true, true, "discard_worksets", false), "Item1") == "discard_worksets"
      && (string)AF(TD(false, true, "none", false), "Item1") == "none" && AF(TD(false, true, "none", false), "Item2") == null && (string)AF(TD(true, false, "none", false), "Item1") == "none",
      "transmitted: save_as_central_ok keeps it attached; an explicit detach, a non-transmitted or non-workshared file is unchanged");
    Check((string)OD("RefType").Invoke(null, new object[] { "CADLink" }) == "CAD" && (string)OD("RefType").Invoke(null, new object[] { "RevitLink" }) == "RevitLink"
      && ((string)OD("Clip").Invoke(null, new object[] { new string('x', 300), 200 })).Length == 200, "unresolvedLinks type names; dialog messages clipped to 200 characters");
    // 1.14.8 #35: a workshared file opened attached gets a <name>_backup folder next to it: a warning (stronger in a deliverables folder).
    string? BW(bool w, bool d, string p) => (string?)OD("BackupFolderWarning").Invoke(null, new object[] { w, d, p });
    var bwPlain = BW(true, false, @"C:\TST_Projects\TST_Work\TST_Model.rvt");
    var bwSub = BW(true, false, @"C:\TST_Projects\TST 03 SUBMISSIONS\TST_Model.rvt");
    var bwExp = BW(true, false, "D:/TST/Exports/TST_Model.rvt");
    Check(bwPlain != null && bwPlain.Contains("TST_Model_backup") && bwPlain.Contains("detach: \"preserve_worksets\"") && !bwPlain.Contains("deliverables folder")
      && bwSub != null && bwSub.StartsWith("This looks like a deliverables folder (\"TST 03 SUBMISSIONS\")") && bwExp != null && bwExp.Contains("deliverables folder")
      && BW(true, true, @"C:\TST\Issued\TST_Model.rvt") == null && BW(false, false, @"C:\TST\Issued\TST_Model.rvt") == null
      && BW(true, false, @"C:\TST\Issued\TST_Model.rvt")!.Contains("(\"Issued\")") && BW(true, false, @"C:\TST_Issued_Model\x\TST_Model.rvt")!.Contains("deliverables folder"),
      "open_model: attached workshared file warns about <name>_backup and recommends detach preserve_worksets; deliverables folders (any case, any segment) warn harder; detached or not workshared: none");

    // 16. 1.12.5: workset_admin plans (rename, delete_empty, self-check) and capture_method diffs. Synthetic TST_ names only.
    var wal = a.GetType("AceRevitMcp.Rules.WorksetAdminLogic");
    var wsT = a.GetType("AceRevitMcp.Rules.WorksetAdminLogic+Workset");
    MethodInfo WA(string n) => wal.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    object WS(long id, string name, int elements = 0, string owner = null, bool open = true)
    {
      var w = Activator.CreateInstance(wsT, true);
      wsT.GetField("Id").SetValue(w, id); wsT.GetField("Name").SetValue(w, name); wsT.GetField("Elements").SetValue(w, elements);
      wsT.GetField("Owner").SetValue(w, owner); wsT.GetField("Open").SetValue(w, open);
      if (elements > 0) ((Dictionary<string, int>)wsT.GetField("ByCategory").GetValue(w))["Walls"] = elements;
      return w;
    }
    var wsList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(wsT));
    foreach (var w in new[] { WS(1, "Shared Levels and Grids", 12), WS(2, "TST_Columns", 5), WS(3, "TST-COLUMNS"), WS(4, "TST_Empty"), WS(5, "TST_Walls", 7),
                              WS(6, "TST_Held", 0, "other.user"), WS(7, "TST_Closed", 0, null, false), WS(8, "TST_Mine", 0, "me") }) wsList.Add(w);
    object FW(object o, string f) => o.GetType().GetField(f).GetValue(o);
    (IList Steps, IList Blocks) Ren(params (string, string)[] r)
    {
      var t = WA("PlanRename").Invoke(null, new object[] { wsList, r.ToList(), "me" });
      return ((IList)FW(t, "Item1"), (IList)FW(t, "Item2"));
    }
    string Codes(IList blocks) => string.Join(",", blocks.Cast<object>().Select(b => (string)FW(b, "Code")));
    var rn = Ren(("TST-COLUMNS", "TST_Beams"), ("Shared Levels and Grids", "TST_Grids"), ("TST_Walls", "TST_Columns"), ("TST_Nope", "TST_X"), ("TST_Held", "TST_Held2"), ("TST_Empty", "TST?Bad"), ("TST_Mine", "TST_Mine"));
    Check(rn.Steps.Count == 1 && (string)FW(rn.Steps[0], "Id") == "rename:3" && (string)FW(rn.Steps[0], "To") == "TST_Beams" && Codes(rn.Blocks) == "system,exists,not-found,borrowed,naming",
      $"rename: one step; protected, name taken, unknown, borrowed and a bad name are blocked; a no-op is neither ({Codes(rn.Blocks)})");
    var rn2 = Ren(("TST_Empty", "TST_A"), ("TST_Mine", "tst_a"), ("TST_Walls", "tst_walls"));
    Check(rn2.Steps.Count == 2 && Codes(rn2.Blocks) == "exists" && (string)FW(rn2.Steps[1], "To") == "tst_walls", "rename: two renames to one name are blocked; a case-only rename of itself is allowed");
    (IList Steps, IList Blocks, List<string> Skipped) Del(string[] names, string moveTo)
    {
      var t = WA("PlanDelete").Invoke(null, new object[] { wsList, names?.ToList(), moveTo, "me" });
      return ((IList)FW(t, "Item1"), (IList)FW(t, "Item2"), (List<string>)FW(t, "Item3"));
    }
    var dAll = Del(null, null);
    var dIds = dAll.Steps.Cast<object>().Select(x => (string)FW(x, "Id")).ToList();
    Check(dIds.SequenceEqual(new[] { "delete:3", "delete:4", "delete:8" }) && dAll.Blocks.Count == 0 && dAll.Skipped.SequenceEqual(new[] { "TST_Held (borrowed by other.user)", "TST_Closed (closed)" }),
      $"delete_empty: every open empty workset; borrowed and closed ones are skipped with a note, protected and non-empty never ({string.Join(",", dIds)})");
    Check(dAll.Steps.Cast<object>().All(x => (long)FW(x, "SafetyId") == 2L && (string)FW(x, "Safety") == "TST_Columns"),
      "delete_empty: each delete moves anything the count missed to an open workset that stays (protected last), never deletes it");
    var dHeld = Del(new[] { "TST_Held" }, null);
    Check(dHeld.Steps.Count == 0 && Codes(dHeld.Blocks) == "borrowed" && (string)FW(dHeld.Blocks[0], "Holder") == "other.user", "delete_empty: a named borrowed workset stays blocked, with its holder");
    var dNamed = Del(new[] { "TST_Walls", "Shared Levels and Grids", "TST_Closed", "TST_Nope" }, null);
    Check(dNamed.Steps.Count == 0 && Codes(dNamed.Blocks) == "not-found,not-empty,system,closed" && (int)FW(dNamed.Blocks[1], "Elements") == 7,
      $"delete_empty: a named workset with elements is not-empty without move_to ({Codes(dNamed.Blocks)})");
    var dMove = Del(new[] { "TST_Walls", "TST_Empty" }, "TST_Columns");
    Check(dMove.Steps.Count == 2 && (string)FW(dMove.Steps[0], "Kind") == "move-and-delete" && (long)FW(dMove.Steps[0], "MoveToId") == 2L && (string)FW(dMove.Steps[1], "Kind") == "delete" && dMove.Blocks.Count == 0,
      "delete_empty: move_to moves a named workset's elements, then deletes it");
    var dSelf = Del(new[] { "TST_Walls" }, "TST_Walls");
    Check(dSelf.Steps.Count == 0 && Codes(dSelf.Blocks).Split(',').Contains("naming") && Codes(dSelf.Blocks).Split(',').Contains("not-empty"), "delete_empty: move_to cannot be a workset being deleted");
    string WPH(IList steps) => (string)WA("PlanHash").Invoke(null, new object[] { steps });
    Check(WPH(dAll.Steps) == WPH(Del(null, null).Steps) && WPH(dAll.Steps) != WPH(dMove.Steps) && WPH(rn.Steps).Length == 64, "workset plan hash: stable for the same plan, different for another");
    var cmp = (IList)WA("CompareCounts").Invoke(null, new object[] { new Dictionary<string, int> { ["Walls"] = 3, ["Floors"] = 2 }, new Dictionary<string, int> { ["Walls"] = 3, ["Floors"] = 1, ["Doors"] = 1 } });
    Check(cmp.Count == 2 && (string)FW(cmp[0], "Item1") == "elements: Doors" && (string)FW(cmp[1], "Item3") == "1", "workset self-check: a category whose count changed is a mismatch");

    var cl = a.GetType("AceRevitMcp.Commands.CaptureLogic");
    var esT = a.GetType("AceRevitMcp.Commands.CaptureLogic+ElemState"); var jT = a.GetType("AceRevitMcp.Commands.CaptureLogic+Join");
    object ES(long id, string type, double[] start, double[] end, double ext = 0, params (long Id, bool Cuts)[] joins)
    {
      var e = Activator.CreateInstance(esT, true);
      esT.GetField("Id").SetValue(e, id); esT.GetField("Category").SetValue(e, "Structural Framing"); esT.GetField("Family").SetValue(e, "TST_Beam"); esT.GetField("Type").SetValue(e, type);
      esT.GetField("Start").SetValue(e, start); esT.GetField("End").SetValue(e, end); esT.GetField("Workset").SetValue(e, "TST_Framing");
      ((Dictionary<string, double>)esT.GetField("Extensions").GetValue(e))["startExtension"] = ext;
      var jl = (IList)esT.GetField("Joins").GetValue(e);
      foreach (var (jid, cuts) in joins) { var j = Activator.CreateInstance(jT, true); jT.GetField("Id").SetValue(j, jid); jT.GetField("Cuts").SetValue(j, cuts); jT.GetField("Category").SetValue(j, "Floors"); jl.Add(j); }
      return e;
    }
    IList EL(params object[] xs) { var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(esT)); foreach (var x in xs) l.Add(x); return l; }
    double[] P(double x) => new[] { x, 0.0, 3000.0 };
    var capBefore = EL(ES(10, "T1", P(0), P(5000), 0, (90, true)), ES(11, "T1", P(0), P(4000), 0, (91, true)), ES(12, "T1", P(0), P(3000)), ES(13, "T1", P(0), P(2000)));
    var capAfter = EL(ES(10, "T1", P(0), P(5000.3), -25, (90, false)), ES(11, "T2", P(100), P(4000), 0), ES(13, "T1", P(0), P(2000), 0, (92, false)), ES(14, "T1", P(0), P(1000)));
    var diffs = (System.Text.Json.Nodes.JsonArray)cl.GetMethod("Diff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { capBefore, capAfter, true });
    string Whats(int i) => string.Join(",", diffs[i]!["changes"]!.AsArray().Select(c => c!["what"]!.ToString()));
    Check(diffs.Count == 5 && (string)diffs[4]!["changes"]![0]!["what"]! == "created" && (long)diffs[0]!["id"]! == 10 && Whats(0) == "extension,join-order" && (long)diffs[0]!["changes"]![1]!["cutter"]! == 90 && diffs[0]!["changes"]![0]!["end"]!.ToString() == "start",
      $"capture: a 0.3 mm move is noise; an extension and a switched join order are changes, cutter = the element that cuts now ({Whats(0)})");
    Check(Whats(1) == "moved,type,join-removed" && (long)diffs[1]!["changes"]![2]!["cutter"]! == 11 && diffs[1]!["changes"]![2]!["otherCategory"]!.ToString() == "Floors" && diffs[1]!["type"]!.ToString() == "TST_Beam : T2",
      $"capture: moved, type changed, join removed (cutter = who cut before, other's category) ({Whats(1)})");
    Check(Whats(2) == "deleted" && (long)diffs[2]!["id"]! == 12 && Whats(3) == "join-added" && (long)diffs[3]!["changes"]![0]!["cutter"]! == 92 && (long)diffs[4 - 1]!["id"]! == 13,
      $"capture: deleted and join-added (cutter = the other element when it cuts) ({Whats(2)} / {Whats(3)})");
    var created = (System.Text.Json.Nodes.JsonArray)cl.GetMethod("Diff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { EL(), EL(ES(14, "T1", P(0), P(1000))), true });
    Check(created.Count == 1 && created[0]!["changes"]![0]!["what"]!.ToString() == "created", "capture: a new element is created");
    var jlines = cl.GetMethod("JournalLines", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    var jr = jlines.Invoke(null, new object[] { new[] { "' comment", "Jrn.Command \"Ribbon\" , \"x\"", "", "' 0:< timing", "Jrn.Data \"y\"" }, 2 });
    var jr2 = jlines.Invoke(null, new object[] { new[] { "a", "' b" }, 5 });
    Check(((List<string>)FW(jr, "Item1")).Count == 2 && (bool)FW(jr, "Item2") && ((List<string>)FW(jr, "Item1"))[0].StartsWith("Jrn.Command") && ((List<string>)FW(jr2, "Item1")).Count == 2 && !(bool)FW(jr2, "Item2"),
      "capture journal: all lines up to the cap; above it comments go first and truncated is set");

    // 17. 1.12.6: view_membership_fill (out reasons, section box, targets, plan hash, sample) and export_deliverables (names, IFC config).
    var vml = a.GetType("AceRevitMcp.Rules.ViewMembershipLogic");
    MethodInfo VM(string n) => vml.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    string Why(bool ph, bool cat, bool hid, bool fil, bool ws, bool box) => (string)VM("OutReason").Invoke(null, new object[] { ph, cat, hid, fil, ws, box });
    Check(Why(true, true, true, true, true, true) == "phase" && Why(false, true, true, false, false, true) == "category" && Why(false, false, true, true, false, false) == "hidden"
      && Why(false, false, false, true, true, true) == "filter" && Why(false, false, false, false, true, true) == "workset" && Why(false, false, false, false, false, true) == "sectionBox"
      && Why(false, false, false, false, false, false) == "other", "view membership: the first reason found counts (phase, category, hidden, filter, workset, section box, other)");
    bool Outside(double[][] corners) => (bool)VM("OutsideBox").Invoke(null, new object[] { corners.ToList(), new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 10.0, 10.0 }, 1e-6 });
    Check(!Outside(new[] { new[] { 9.0, 9.0, 9.0 }, new[] { 12.0, 12.0, 12.0 } }) && Outside(new[] { new[] { 11.0, 0.0, 0.0 }, new[] { 12.0, 5.0, 5.0 } })
      && Outside(new[] { new[] { 1.0, 1.0, -5.0 }, new[] { 2.0, 2.0, -1.0 } }) && !Outside(new[] { new[] { 10.0, 1.0, 1.0 }, new[] { 12.0, 2.0, 2.0 } }),
      "view membership: section box test (partly inside is inside, a touch is inside, wholly beyond one face is outside)");
    string Tgt(string cur, bool inV) => (string)VM("Target").Invoke(null, new object[] { cur, inV, "TST_IN", "TST_OUT" });
    Check(Tgt("TST_IN", true) == null && Tgt("TST_OUT", true) == "TST_IN" && Tgt(null, false) == "TST_OUT" && Tgt("TST_OUT", false) == null && Tgt("tst_out", false) == "TST_OUT",
      "view membership: set only where the value differs (case-sensitive), in -> value_in, out -> value_out");
    string VH(string bind, long[] i, long[] o) => (string)VM("PlanHash").Invoke(null, new object[] { bind, "TST_IN", "TST_OUT", i, o });
    Check(VH(null, new long[] { 1, 2 }, new long[] { 3 }) == VH(null, new long[] { 2, 1 }, new long[] { 3 }) && VH(null, new long[] { 1, 2 }, new long[] { 3 }) != VH(null, new long[] { 1 }, new long[] { 2, 3 })
      && VH("g|1", new long[] { 1 }, new long[0]) != VH(null, new long[] { 1 }, new long[0]), "view membership plan hash: order-free, changes with the side or the binding");
    var many = Enumerable.Range(1, 100).Select(i => (long)i).ToList();
    var s1 = (List<long>)VM("Sample").Invoke(null, new object[] { many, 20, "seed" }); var s2 = (List<long>)VM("Sample").Invoke(null, new object[] { many, 20, "seed" });
    var few = (List<long>)VM("Sample").Invoke(null, new object[] { new List<long> { 5, 3 }, 20, "seed" });
    Check(s1.Count == 20 && s1.SequenceEqual(s2) && s1.Distinct().Count() == 20 && few.SequenceEqual(new long[] { 3, 5 }), "view membership: a repeatable sample of 20 (all when fewer)");
    string FN(string g, string ext) { var args = new object[] { g, ext, null }; return (string)VM("FileName").Invoke(null, args); }
    Check(FN("TST-MODEL", ".nwc") == "TST-MODEL.nwc" && FN("TST-MODEL.IFC", ".ifc") == "TST-MODEL.IFC" && FN("..\\x", ".ifc") == null && FN("", ".dwfx") == null && FN("a:b", ".nwc") == null,
      "export: plain file names only, the extension added when missing");
    var cfg = System.Text.Json.Nodes.JsonNode.Parse("{\"FileVersion\":\"IFC2x3CV2\",\"ExportBaseQuantities\":true,\"SpaceBoundaryLevel\":3,\"TST_Option\":\"x\",\"WallAndColumnSplitting\":\"false\"}")!.AsObject();
    var ic2 = VM("IfcConfig").Invoke(null, new object[] { cfg });
    Check((string)FW(ic2, "Item1") == "IFC2x3CV2" && (bool?)FW(ic2, "Item2") == true && (bool?)FW(ic2, "Item3") == false && FW(ic2, "Item4") == null
      && ((Dictionary<string, string>)FW(ic2, "Item5"))["TST_Option"] == "x" && ((List<string>)FW(ic2, "Item6")).Count == 1,
      "export: IFC config keys map to options; an out-of-range space boundary level is reported");

    // 18. 1.12.8: the cutter depends on the two elements only, never on which one the clash listed first.
    object CSide(long id, string cat, double vol) => Set(Activator.CreateInstance(rSideT, true), ("Id", id), ("Category", cat), ("Material", "concrete"), ("Volume", vol), ("Host", true));
    var cats18 = new[] { "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming" };
    var asym = new List<string>();
    long CutterId(object a1, object b1)
    {
      var d = Dec(Pair(a1, b1, 0.01));
      if (K(d) != "join") return -1;
      var ch = Ch(d);
      return ch == null ? 0 : (long)FW(ch == "A" ? a1 : b1, "Id");
    }
    foreach (var k1 in cats18) foreach (var k2 in cats18)
      foreach (var (v1, v2) in new[] { (5.0, 3.0), (3.0, 5.0), (4.0, 4.0) })
      {
        var x = CSide(101, k1, v1); var y = CSide(202, k2, v2);
        var ab = CutterId(x, y); var ba = CutterId(y, x);
        if (ab != ba || ab <= 0) asym.Add($"{k1}/{k2} {v1}/{v2}: {ab} vs {ba}");
      }
    Check(asym.Count == 0, $"join cutter: (a,b) and (b,a) give the same cutter for every category pair, same-category pairs too ({string.Join("; ", asym.Take(3))})");
    var fb1 = CutterId(CSide(11, "OST_StructuralFoundation", 2), CSide(12, "OST_StructuralFraming", 9)); var fb2 = CutterId(CSide(12, "OST_StructuralFraming", 9), CSide(11, "OST_StructuralFoundation", 2));
    var same1 = CutterId(CSide(21, "OST_Floors", 2), CSide(22, "OST_Floors", 9)); var tie = CutterId(CSide(32, "OST_Walls", 4), CSide(31, "OST_Walls", 4));
    Check(fb1 == 11 && fb2 == 11 && same1 == 22 && tie == 31, "join cutter: the foundation cuts the beam both ways; same category: the larger cuts, then the lower id");

    // 19. 1.12.8: vault order aliases, trim to face (pure geometry), touching pairs, store key, stored results, capture area / annotation / journal.
    string Al(string n) => (string)R("CategoryAlias").Invoke(null, new object[] { n });
    Check(Al("foundation") == "OST_StructuralFoundation" && Al("Column") == "OST_StructuralColumns" && Al("wall") == "OST_Walls" && Al("slab") == "OST_Floors" && Al("floor") == "OST_Floors"
      && Al("beam") == "OST_StructuralFraming" && Al("framing") == "OST_StructuralFraming" && Al("Structural Foundations") == "OST_StructuralFoundation" && Al("OST_StructuralColumns") == "OST_StructuralColumns"
      && Al("TST_Nothing") == null && Al("") == null, "order aliases: foundation, column, wall, slab / floor, beam / framing, full and OST names; unknown is null");
    var vaultOrder = new List<string> { "foundation", "column", "wall", "slab", "beam" }.Select(Al).ToList();
    var aliasAsym = new List<string>();
    foreach (var k1 in cats18) foreach (var k2 in cats18)
    {
      var x = CSide(301, k1, 2); var y = CSide(302, k2, 2);
      var o1 = R("CutterOf").Invoke(null, new object[] { vaultOrder, x, y }); var o2 = R("CutterOf").Invoke(null, new object[] { vaultOrder, y, x });
      var q1 = (string)o1 == "A" ? 301 : (string)o1 == "B" ? 302 : 0; var q2 = (string)o2 == "A" ? 302 : (string)o2 == "B" ? 301 : 0;
      var expect = Array.IndexOf(cats18, k1) < Array.IndexOf(cats18, k2) ? 301 : Array.IndexOf(cats18, k1) > Array.IndexOf(cats18, k2) ? 302 : 301;
      if (q1 != q2 || q1 != expect) aliasAsym.Add($"{k1}/{k2}: {q1} vs {q2}");
    }
    Check(aliasAsym.Count == 0, $"vault order: the higher category cuts whichever side it is on; same category and size: the lower id ({string.Join("; ", aliasAsym.Take(3))})");
    double? AP(double[] p0, double[] u, double[] o, double[] n) => (double?)R("AxisPlane").Invoke(null, new object[] { p0, u, o, n });
    var tHit = AP(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 4000.0, 7, 9 }, new[] { -2.0, 0, 0 });
    var tSkew = AP(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 3000.0, 0, 0 }, new[] { 1.0, 1.0, 0 });
    Check(tHit.HasValue && Math.Abs(tHit.Value - 4000) < 1e-9 && tSkew.HasValue && Math.Abs(tSkew.Value - 3000) < 1e-9 && AP(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 500, 0 }, new[] { 0.0, 1, 0 }) == null,
      "trim: axis meets plane at t (normal length / direction free, skew plane); a parallel plane gives none");
    (double T, double Trim)? TT(double len, double[] hits, int end, double max, out string why)
    { var args = new object[] { len, hits, end, max, 1.0, null }; var r = R("TrimTarget").Invoke(null, args); why = (string)args[5]; return r == null ? null : ((double, double)?)r; }
    var te1 = TT(6000, new[] { 5600.0, 5900.0, 200.0 }, 1, 1000, out var tw1); var te0 = TT(6000, new[] { 300.0, 100.0, 5600.0 }, 0, 1000, out var tw0);
    var beyond = TT(6000, new[] { 6000.0, 6400.0, -50.0 }, 1, 1000, out var wB); var tooFar = TT(6000, new[] { 4500.0 }, 1, 1000, out var wF); var atMax = TT(6000, new[] { 5000.0 }, 1, 1000, out _);
    Check(te1.HasValue && te1.Value.T == 5600 && te1.Value.Trim == 400 && te0.HasValue && te0.Value.T == 300 && te0.Value.Trim == 300,
      "trim: the end's half only, the face nearest the middle (end 1 keeps 0..5600, end 0 keeps 300..6000)");
    Check(beyond == null && wB == "no face" && tooFar == null && wF == "trim too long" && atMax.HasValue && atMax.Value.Trim == 1000 && (double)rl.GetField("DefaultTrimMaxMm", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)! == 1000,
      "trim: shorten only (a face at or beyond the end is none); longer than max_mm is report-only; exactly max is allowed; default 1000 mm");
    Check((string)R("TrimId").Invoke(null, new object[] { 501L, 77L, 1 }) == "trim:501>77:1", "trim plan id: trim:<trimmed>><face owner>:<end>");
    var tboxes = new List<(long, double[], double[])> { (5, new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 10 }), (3, new[] { 10.004, 0, 0 }, new[] { 20.0, 10, 10 }), (9, new[] { 30.0, 0, 0 }, new[] { 40.0, 10, 10 }),
      (7, new[] { 5.0, 10.02, 0 }, new[] { 8.0, 12, 10 }), (8, new[] { 2.0, 2, 2 }, new[] { 3.0, 3, 3 }) };
    var tp = (List<(long A, long B)>)R("TouchingPairs").Invoke(null, new object[] { tboxes, 0.005 });
    Check(tp.SequenceEqual(new List<(long, long)> { (3, 5), (5, 8) }), $"touching pairs: overlap or within tol on every axis, each pair once (low id, high id), in id order ({string.Join(" ", tp)})");
    // 1.12.9: one trim per member end (the smallest), the combined trim check, shorten only, the end inside the solid,
    // openings in the trimmed stretch, the touching filters, and the hosted-elements guard.
    var kept1 = (List<int>)R("OneTrimPerEnd").Invoke(null, new object[] { new List<(long, int, double)> { (7, 1, 300), (7, 1, 120), (7, 0, 500), (8, 1, 50), (7, 1, 120) } })!;
    Check(kept1.SequenceEqual(new[] { 1, 2, 3 }), $"trim: at most one per member end, the smallest kept (ties: the first) ({string.Join(",", kept1)})");
    bool TEO(double[] b, double[] a, params (int, double[])[] tr) => (bool)R("TrimmedEndsOk").Invoke(null, new object[] { b, a, tr.ToList(), 1e-6, 1e-4 })!;
    var line0 = new[] { 0.0, 0, 0, 10, 0, 0 };
    Check(TEO(line0, new[] { 1.0, 0, 0, 9, 0, 0 }, (0, new[] { 1.0, 0, 0 }), (1, new[] { 9.0, 0, 0 })) && TEO(line0, new[] { 0.0, 0, 0, 9, 0, 0 }, (1, new[] { 9.0, 0, 0 }))
      && !TEO(line0, new[] { 0.5, 0, 0, 9, 0, 0 }, (1, new[] { 9.0, 0, 0 })) && !TEO(line0, new[] { 1.0, 0, 0, 9, 0, 0 }, (0, new[] { 1.0, 0, 0 })) && !TEO(line0, new[] { 0.0, 0, 0, 10, 0, 0 }, (1, new[] { 9.0, 0, 0 })),
      "trim verify: both trims of one member together; every trimmed end at its point, every other end unchanged");
    bool SI(double[] np) => (bool)R("StrictlyInside").Invoke(null, new object[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, np, 1e-6 })!;
    Check(SI(new[] { 4.0, 0, 0 }) && !SI(new[] { 10.0, 0, 0 }) && !SI(new[] { 11.0, 0, 0 }) && !SI(new[] { -1.0, 0, 0 }) && !SI(new[] { 4.0, 0.01, 0 }), "trim: the new end must lie strictly inside the current line (shorten only)");
    bool EI(double t, params (double, double)[] r) => (bool)R("EndInside").Invoke(null, new object[] { t, r.ToList(), 0.003 })!;
    Check(EI(10, (9.6, 10.4)) && EI(10, (10.002, 10.5)) && !EI(10, (9.0, 9.9)) && !EI(0, (9.6, 10.4)) && EI(0, (0.5, -0.4)), "trim: an end is inside the cutter when the axis runs inside its solid at that end (not its box)");
    bool IT(double len, double tn, int end, params (double, double)[] r) => (bool)R("InsertInTrimmedStretch").Invoke(null, new object[] { len, tn, end, r.ToList() })!;
    Check(IT(10, 9, 1, (8.5, 9.2)) && !IT(10, 9, 1, (2, 3)) && IT(10, 1, 0, (0.2, 0.6)) && !IT(10, 1, 0, (1.0, 2.0)) && !IT(10, 9, 1),
      "trim: a wall hosting an opening in the stretch the trim removes is report-only (hosts openings)");
    bool TPA(string x, string y, string[] aa, string[] bb, bool same) => (bool)R("TouchPairAllowed").Invoke(null, new object[] { x, y, new HashSet<string>(aa), new HashSet<string>(bb), same })!;
    var stAll = new[] { "OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming" };
    Check(TPA("OST_Walls", "OST_Walls", stAll, stAll, true) && TPA("OST_StructuralColumns", "OST_StructuralColumns", stAll, stAll, true) && !TPA("OST_Walls", "OST_Walls", stAll, stAll, false)
      && TPA("OST_Floors", "OST_Walls", new[] { "OST_Walls" }, new[] { "OST_Floors" }, true) && !TPA("OST_Floors", "OST_StructuralColumns", new[] { "OST_Walls" }, new[] { "OST_Floors" }, true),
      "all-touching: same-category pairs (walls too) unless same_category: false; one element on each side of the test");
    Check((int)rl.GetField("DefaultMaxPairs", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)! == 500, "all-touching: max_pairs defaults to 500");
    var hb = (IList)Activator.CreateInstance(stateListT)!; var hw = State(41, new[] { 0.0, 0, 0, 10, 0, 0 }, 0); Set(hw, ("Hosted", (int?)2)); hb.Add(hw);
    var ha = (IDictionary)Activator.CreateInstance(stateDictT)!; var hw2 = State(41, new[] { 0.0, 0, 0, 9, 0, 0 }, 0); Set(hw2, ("Hosted", (int?)1)); ha[41L] = hw2;
    var hres = R("Compare").Invoke(null, new object[] { hb, ha, new HashSet<long> { 41 }, new HashSet<long> { 41 } })!;
    var hm = ((IList)hres.GetType().GetField("Item1")!.GetValue(hres)!).Cast<object>().Select(W).ToList();
    Check(hm.SequenceEqual(new[] { "hosted" }), $"guard: a trimmed wall that lost a hosted element is a mismatch ({string.Join(",", hm)})");
    var clg = a.GetType("AceRevitMcp.Coordination.ClashLogic");
    MethodInfo CLG(string n) => clg.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    // 1.14.1: with_model by path first; a name two documents share is refused with the candidates (synthetic models).
    var pmSrc = new List<(string, int, string)> { ("TST_A", 0, "C:\\tst\\TST_A.rvt"), ("TST_A", 1, "D:\\copy\\TST_A.rvt"), ("TST_LINK", 2, "C:\\tst\\TST_LINK.rvt"), ("TST_LINK", 2, "C:\\tst\\TST_LINK.rvt"), ("TST_B", 3, null) };
    (int, List<int>) PickM(string? n, string? p) { var t = CLG("PickModel").Invoke(null, new object?[] { pmSrc, n, p })!; return ((int)t.GetType().GetField("Item1")!.GetValue(t)!, (List<int>)t.GetType().GetField("Item2")!.GetValue(t)!); }
    var pmDup = PickM("tst_a", null); var pmPath = PickM(null, "d:/copy/TST_A.rvt"); var pmLink = PickM("TST_LINK", null); var pmNone = PickM("TST_X", null); var pmB = PickM("TST_B", null); var pmNoPath = PickM(null, "E:\\x.rvt");
    Check(pmDup.Item1 == -1 && pmDup.Item2.SequenceEqual(new[] { 0, 1 }) && pmPath.Item1 == 1 && pmPath.Item2.Count == 0 && pmLink.Item1 == 2 && pmLink.Item2.Count == 0
      && pmNone.Item1 == -1 && pmNone.Item2.Count == 0 && pmB.Item1 == 4 && pmNoPath.Item1 == -1 && pmNoPath.Item2.Count == 0,
      "with_model: a name two open documents share is refused with both candidates; by path the copy is picked; a link placed twice is one model");
    string SK(bool ws, string central, string cloud, string path, string title) => (string)CLG("StoreKey").Invoke(null, new object[] { ws, central, cloud, path, title })!;
    string H8(string x) { using var h = System.Security.Cryptography.SHA1.Create(); return string.Concat(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(x)).Select(b => b.ToString("X2"))).Substring(0, 8); }
    var skC1 = SK(true, "\\\\srv\\TST\\TST_CENTRAL.rvt", null, "C:\\local\\TST_CENTRAL_user1.rvt", "TST_CENTRAL_user1");
    var skC2 = SK(true, "\\\\srv\\TST\\TST_CENTRAL.rvt", null, "D:\\other\\TST_CENTRAL_user2.rvt", "TST_CENTRAL_user2");
    var skC3 = SK(true, "\\\\srv2\\TST\\TST_CENTRAL.rvt", null, "C:\\local\\TST_CENTRAL_user1.rvt", "TST_CENTRAL_user1");
    var skCloud = SK(true, "Autodesk Docs://TST/TST_CENTRAL.rvt", "AAAA-1111/BBBB-2222", "C:\\cache\\TST_CENTRAL.rvt", "TST_CENTRAL");
    Check(skC1 == skC2 && skC1 != skC3 && skC1.StartsWith("TST_CENTRAL-") && skC1.Length == "TST_CENTRAL-".Length + 8 && skCloud == "TST_CENTRAL-" + H8("cloud:aaaa-1111/bbbb-2222"),
      $"store key: the central's name and a hash of its path (cloud: of its GUIDs), the same for every local copy, different for another central ({skC1} / {skC3})");
    Check(SK(false, null, null, "C:\\p\\TST_MODEL.rvt", "TST_MODEL") == "TST_MODEL-" + H8("c:\\p\\tst_model.rvt") && SK(false, null, null, "", "TST_Unsaved") == "TST_Unsaved-" + H8("TST_Unsaved")
      && SK(true, null, null, "C:\\p\\TST_DETACHED.rvt", "TST_DETACHED") == "TST_DETACHED-" + H8("c:\\p\\tst_detached.rvt") && SK(false, null, null, "C:\\p\\a.rvt", "a:b").StartsWith("a_b-"),
      "store key: otherwise the title and a hash of the file path (as the snapshots are kept); safe as a folder name");
    Check((string)CLG("LegacyStoreKey").Invoke(null, new object[] { true, "\\\\srv\\TST\\TST_CENTRAL.rvt", "C:\\x\\y.rvt", "y" })! == "TST_CENTRAL", "store key: the 1.12.8 key (name only) is still known, to copy its results");
    var toCopy = (List<string>)CLG("FilesToCopy").Invoke(null, new object[] { new[] { "ST_vs_ST.json", "runs/ST_vs_ST.json", "STR_vs_MEP.json" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "st_vs_st.json" } })!;
    Check(toCopy.SequenceEqual(new[] { "STR_vs_MEP.json", "runs/ST_vs_ST.json" }), $"store migration: file by file, never over a file the new store has ({string.Join(",", toCopy)})");
    var runsS = (IList)Activator.CreateInstance(runListT); runsS.Add(Run("T", t0.AddDays(2)));
    var latest = IL("LatestRuns").Invoke(null, new object[] { runsS });
    object SC(string key, string status, DateTime seen, string model) => Set(C(key, status, "u" + key, "v" + key, "Walls", "Floors", "L1"), ("LastSeen", seen), ("SourceA", model), ("SourceB", model));
    var mixed = List(SC("s1", "new", t0.AddDays(2), "TST_A"), SC("s2", "active", t0.AddDays(2).AddMinutes(-1), "TST_A"), SC("s3", "new", t0, "TST_B"), SC("s4", "resolved", t0, "TST_B"));
    var rowsS = (IList)IL("Rows").Invoke(null, new object[] { mixed, (0.0, 0.0, 0.0), null, latest });
    var rlS = (IList)Activator.CreateInstance(rowListT); foreach (var r in rowsS) rlS.Add(r);
    var cntS = (Dictionary<string, int>)IL("Counts").Invoke(null, new object[] { rlS });
    var notRe = List(mixed[2], mixed[3]);
    var st = IL("Stored").Invoke(null, new object[] { notRe });
    Check(cntS["clashes"] == 2 && cntS["open"] == 2 && !cntS.ContainsKey("stored") && rowsS.Count == 4 && !(bool)RF(rowsS[3], "Rechecked") && (bool)RF(rowsS[0], "Rechecked")
      && (int)FW(st, "Item1") == 2 && (int)FW(st, "Item2") == 1 && ((List<string>)FW(st, "Item3")).SequenceEqual(new[] { "TST_B" }),
      "stored results: only clashes the latest run looked at count as open; the others are listed last, rechecked: false, with their models");
    var noRuns = (IList)IL("Rows").Invoke(null, new object[] { mixed, (0.0, 0.0, 0.0), null, null });
    Check(noRuns.Cast<object>().All(r => (bool)RF(r, "Rechecked")), "stored results: without run records everything counts as rechecked");
    // 1.12.9: a run record from 1.12.9 on matches its time exactly; in-scope resolved clashes not found again are looked at too.
    var tRun = t0.AddDays(4).AddSeconds(0.25);
    var exactRun = Run("T", tRun); runT.GetField("Exact").SetValue(exactRun, true);
    var exRuns = (IList)Activator.CreateInstance(runListT); exRuns.Add(exactRun);
    var exLatest = IL("LatestRuns").Invoke(null, new object[] { exRuns });
    bool RC(object c, object lr) => (bool)IL("Rechecked").Invoke(null, new object[] { c, lr })!;
    var prevResolved = SC("m1", "resolved", t0.AddDays(3), "TST_A"); var prevApproved = SC("m2", "approved", t0.AddDays(3), "TST_A"); var prevActive = SC("m3", "active", t0.AddDays(3), "TST_A");
    var foundNow = SC("m4", "new", t0, "TST_A");
    var mergedM = (IList)CLG("Merge").Invoke(null, new object[] { List(prevResolved, prevApproved, prevActive), List(foundNow), null, tRun })!;
    object MK(string k) => mergedM.Cast<object>().First(c => (string)F(c, "Key") == k);
    Check(RC(MK("m1"), exLatest) && (string)F(MK("m1"), "Status") == "resolved" && RC(MK("m2"), exLatest) && (string)F(MK("m2"), "Status") == "resolved" && (bool)F(MK("m2"), "Approved")
      && RC(MK("m3"), exLatest) && (string)F(MK("m3"), "Status") == "resolved" && RC(MK("m4"), exLatest) && (DateTime)F(MK("m1"), "LastSeen") == t0.AddDays(3),
      "rechecked: an in-scope resolved or approved clash not found again counts in this run (resolved / approved), its last-seen time kept");
    var nearMiss = SC("m5", "new", tRun.AddSeconds(-1), "TST_A");
    var legacyRuns = (IList)Activator.CreateInstance(runListT); legacyRuns.Add(Run("T", tRun));
    Check(!RC(nearMiss, exLatest) && RC(nearMiss, IL("LatestRuns").Invoke(null, new object[] { legacyRuns })),
      "rechecked: exact time for 1.12.9 run records; the 2-minute window only for older records");
    object ESA(long id, double? area, double? vol, int dims, int tags) { var e = ES(id, "T1", P(0), P(1000)); esT.GetField("Area").SetValue(e, area); esT.GetField("Volume").SetValue(e, vol); esT.GetField("Dimensions").SetValue(e, dims); esT.GetField("Tags").SetValue(e, tags); return e; }
    var dA = (System.Text.Json.Nodes.JsonArray)cl.GetMethod("Diff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] {
      EL(ESA(1, 20.0, 5.0, 2, 1), ESA(2, 10.0, 2.0, 0, 0)), EL(ESA(1, 18.5, 4.6, 1, 1), ESA(2, 10.0004, 2.00005, 0, 0)), true });
    string WA2(int i) => string.Join(",", dA[i]!["changes"]!.AsArray().Select(c => c!["what"]!.ToString()));
    Check(dA.Count == 1 && WA2(0) == "area,volume,annotation" && (double)dA[0]!["changes"]![0]!["before"]! == 20 && (int)dA[0]!["changes"]![2]!["after"]!["dimensions"]! == 1,
      $"capture: area and volume changes (noise ignored) and a lost dimension are reported ({(dA.Count > 0 ? WA2(0) + " " + dA[0]!.ToJsonString() : "")})");
    var anT = a.GetType("AceRevitMcp.Commands.CaptureLogic+Annotation");
    object AN(int d, int s, int t) => Set(Activator.CreateInstance(anT, true)!, ("Dimensions", d), ("SpotDimensions", s), ("Tags", t));
    var ad = (System.Text.Json.Nodes.JsonObject)cl.GetMethod("AnnotationDiff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { AN(10, 2, 30), AN(9, 2, 31), true });
    var dNoAnn = (System.Text.Json.Nodes.JsonArray)cl.GetMethod("Diff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] {
      EL(ESA(1, 20.0, 5.0, 2, 1)), EL(ESA(1, 20.0, 5.0, 0, 0)), false });
    var adNull = (System.Text.Json.Nodes.JsonObject)cl.GetMethod("AnnotationDiff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { AN(1, 0, 1), null, true })!;
    var adStart = (System.Text.Json.Nodes.JsonObject)cl.GetMethod("AnnotationDiff", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { AN(1, 0, 1), null, false })!;
    Check(dNoAnn.Count == 0 && !(bool)adNull["available"]! && (bool)adStart["available"]!, "capture: when the annotation counts failed on either side, they are unavailable and no annotation diffs are made");
    Check((bool)ad["available"]! && (int)ad["dimensions"]!["before"]! == 10 && (int)ad["dimensions"]!["after"]! == 9 && (int)ad["tags"]!["after"]! == 31 && (int)ad["spotDimensions"]!["before"]! == 2, "capture: annotation counts at start and stop");
    // The journal is still open for writing by Revit: read through a shared stream to EOF, never from its length.
    var jPath = Path.Combine(Path.GetTempPath(), "TST_journal_" + Guid.NewGuid().ToString("N") + ".txt");
    try
    {
      long jEnd; List<string> jNew, jRot;
      using (var w = new FileStream(jPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
      {
        void Wr(string t) { var b = System.Text.Encoding.UTF8.GetBytes(t); w.Write(b, 0, b.Length); w.Flush(); }
        Wr("' TST before\nJrn.Command \"TST\" , \"old\"\n");
        jEnd = (long)cl.GetMethod("JournalEnd", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { jPath })!;
        Wr("Jrn.Command \"TST\" , \"new\"\n' TST after\n");
        jNew = (List<string>)cl.GetMethod("JournalFrom", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { jPath, jEnd })!;
        jRot = (List<string>)cl.GetMethod("JournalFrom", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { jPath, 100000L })!;
      }
      var nl = jNew.Where(l => l.Length > 0).ToList();
      Check(jEnd == System.Text.Encoding.UTF8.GetByteCount("' TST before\nJrn.Command \"TST\" , \"old\"\n") && nl.Count == 2 && nl[0].Contains("\"new\"") && nl[1] == "' TST after",
        "capture journal: the start offset is the bytes read to EOF while another stream writes; stop reads only what came after");
      Check(jRot.Count(l => l.Length > 0) == 4 && jRot[0] == "' TST before", "capture journal: a stream shorter than the offset (rotated) is read from its beginning");
      Check((long)cl.GetMethod("JournalEnd", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { jPath + ".missing" })! == 0, "capture journal: a missing journal has offset 0");
    }
    finally { try { File.Delete(jPath); } catch { } }

    // 20. 1.13.0: workset_plan (workset_admin facts / apply_plan): group key, creates, run order, counts after moves, plan hash.
    var wpl = a.GetType("AceRevitMcp.Rules.WorksetPlanLogic"); var wpsT = a.GetType("AceRevitMcp.Rules.WorksetPlanLogic+Step");
    MethodInfo WP(string n) => wpl.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    string GK(params string[] f) => (string)WP("Key").Invoke(null, f.Cast<object>().ToArray())!;
    Check(GK("TST_STR", "Structural Columns", "TST_Col : 400x400", "TST_GF", "concrete", "", "") == "TST_STR|Structural Columns|TST_Col : 400x400|TST_GF|concrete||"
      && GK(" TST_STR ", "Walls", null, null, "other", null, null) == "TST_STR|Walls|||other||"
      && string.Join(",", (string[])wpl.GetField("KeyFields")!.GetValue(null)!) == "workset,category,type,level,material,zone,option",
      "workset facts: the group key is [workset, category, type, level, material, zone, option] joined with |, empty for none");
    Check((string)WP("MaterialClass").Invoke(null, new object[] { "precast" })! == "concrete" && (string)WP("MaterialClass").Invoke(null, new object[] { "steel" })! == "steel"
      && (string)WP("MaterialClass").Invoke(null, new object[] { "timber" })! == "other", "workset facts: material class concrete (precast too) / steel / other");
    var cr = WP("PlanCreate").Invoke(null, new object[] { wsList, new[] { "TST_NEW", "tst_new", "TST_Walls", "", "TST{bad}", "TST_RENAMED" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TST_RENAMED" } })!;
    var crSteps = (IList)FW(cr, "Item1"); var crBlocks = (IList)FW(cr, "Item2");
    Check(crSteps.Count == 1 && (string)FW(crSteps[0], "Workset") == "TST_NEW" && crBlocks.Cast<object>().Select(b => (string)FW(b, "Code")).SequenceEqual(new[] { "exists", "exists", "naming", "naming", "exists" }),
      "workset plan: create blocks a taken name (case-insensitive), a name given twice, a rename's target, an empty or invalid name");
    object PS(string kind, string ws, long wsId = 0, long toId = 0, int n = 0, params long[] ids)
    { var st = Activator.CreateInstance(wpsT, true)!; Set(st, ("Id", kind + ":" + ws), ("Kind", kind), ("Workset", ws), ("WorksetId", wsId), ("ToId", toId), ("Elements", n)); var il = (List<long>)FW(st, "Ids"); il.AddRange(ids); return st; }
    IList PL(params object[] xs) { var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(wpsT))!; foreach (var x in xs) l.Add(x); return l; }
    var mixedSteps = PL(PS("delete", "TST_Old", 9), PS("move", "TST_A", 0, 2, 2, 11, 12), PS("rename", "TST_B", 3), PS("create", "TST_C"), PS("move-and-delete", "TST_D", 4, 2, 3), PS("create", "TST_E"));
    var ord = ((IList)WP("Ordered").Invoke(null, new object[] { mixedSteps })!).Cast<object>().Select(x => (string)FW(x, "Id")).ToList();
    Check(ord.SequenceEqual(new[] { "create:TST_C", "create:TST_E", "rename:TST_B", "move:TST_A", "delete:TST_Old", "move-and-delete:TST_D" }), $"workset plan: run order create, rename, move, delete (request order within a kind) ({string.Join(",", ord)})");
    string PHW(IList l) => (string)WP("PlanHash").Invoke(null, new object[] { l })!;
    var wh1 = PHW(mixedSteps);
    var wh2 = PHW(PL(PS("create", "TST_E"), PS("create", "TST_C"), PS("move-and-delete", "TST_D", 4, 2, 3), PS("rename", "TST_B", 3), PS("move", "TST_A", 0, 2, 2, 12, 11), PS("delete", "TST_Old", 9)));
    var wh3 = PHW(PL(PS("delete", "TST_Old", 9), PS("move", "TST_A", 0, 2, 2, 11, 13), PS("rename", "TST_B", 3), PS("create", "TST_C"), PS("move-and-delete", "TST_D", 4, 2, 3), PS("create", "TST_E")));
    var wh4 = PHW(PL(PS("delete", "TST_Old", 9), PS("move", "TST_A", 0, 5, 2, 11, 12), PS("rename", "TST_B", 3), PS("create", "TST_C"), PS("move-and-delete", "TST_D", 4, 2, 3), PS("create", "TST_E")));
    Check(wh1 == wh2 && wh1 != wh3 && wh1 != wh4 && wh1.Length == 64, "workset plan hash: over the whole plan, order-free; another element or another target is another plan");
    var am = (Dictionary<long, int>)WP("AfterMoves").Invoke(null, new object[] { new Dictionary<long, int> { [2] = 5, [3] = 2, [4] = 0 }, new List<(long, long, int)> { (3, 4, 1), (3, 0, 1), (2, 4, 2) } })!;
    Check(am[2] == 3 && am[3] == 0 && am[4] == 3 && !am.ContainsKey(0), "workset plan: elements per workset after the moves (a created target is not counted), so a delete sees what is left");

    // 21. 1.13.0 review: the datum workset by content (vault, levels and grids, then name), never renamed or deleted; link types in the hash.
    IList DW(params (long Id, string Name, int Lg, int El)[] xs)
    {
      var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(wsT))!;
      foreach (var x in xs) { var w = WS(x.Id, x.Name, x.El); wsT.GetField("LevelsAndGrids")!.SetValue(w, x.Lg); l.Add(w); }
      return l;
    }
    string DName(IList l, string vault) { var d = WA("DatumWorkset").Invoke(null, new object[] { l, vault }); return d == null ? null : (string)FW(d, "Name"); }
    var dws = DW((5, "TST_Shared Views, Levels, Grids", 0, 0), (2, "TST_Grids old", 0, 0), (9, "TST_STR", 14, 300), (11, "TST_ARC", 2, 10));
    Check(DName(dws, "TST_ARC") == "TST_ARC" && DName(dws, null) == "TST_STR" && DName(dws, "TST_missing") == "TST_STR"
      && DName(DW((5, "TST_Shared Views, Levels, Grids", 0, 0), (2, "TST_GRIDS old", 0, 0), (9, "TST_STR", 0, 3)), null) == "TST_GRIDS old"
      && DName(DW((9, "TST_STR", 0, 3)), null) == null,
      "datum workset: the vault's name, else the one holding the levels and grids, else the oldest named levels / grids; not by its exact name");
    var emptyDatum = DW((5, "TST_Shared Views, Levels, Grids", 0, 0), (9, "TST_STR", 0, 3));
    WA("MarkDatum").Invoke(null, new object[] { emptyDatum, null });
    var delDatum = WA("PlanDelete").Invoke(null, new object[] { emptyDatum, new List<string>(), null, "me" })!;
    var delNamed = WA("PlanDelete").Invoke(null, new object[] { emptyDatum, new List<string> { "TST_Shared Views, Levels, Grids" }, null, "me" })!;
    var renDatum = WA("PlanRename").Invoke(null, new object[] { emptyDatum, new List<(string, string)> { ("TST_Shared Views, Levels, Grids", "TST_X") }, "me" })!;
    Check(((IList)FW(delDatum, "Item1")).Count == 0 && ((IList)FW(delNamed, "Item1")).Count == 0 && (string)FW(((IList)FW(delNamed, "Item2"))[0], "Code") == "system"
      && ((IList)FW(renDatum, "Item1")).Count == 0 && (string)FW(((IList)FW(renDatum, "Item2"))[0], "Code") == "system" && (bool)wal.GetMethod("IsProtected", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { wsT }, null)!.Invoke(null, new object[] { emptyDatum[0] })!,
      "datum workset: protected, never deleted (even empty, even when named) or renamed");
    // 1.13.5: trailing-space workset names; the self-check counts model and annotation categories only.
    var spaced = DW((5, "TST_STEEL ROOF ", 0, 0), (9, "TST_STR", 0, 3));
    var renSp = WA("PlanRename").Invoke(null, new object[] { spaced, new List<(string, string)> { ("TST_STEEL ROOF", "TST_STEEL ROOF") }, "me" })!;
    var delSp = WA("PlanDelete").Invoke(null, new object[] { spaced, new List<string> { "TST_STEEL ROOF" }, null, "me" })!;
    Check(((IList)FW(renSp, "Item1")).Count == 1 && (string)FW(((IList)FW(renSp, "Item1"))[0], "From") == "TST_STEEL ROOF " && (string)FW(((IList)FW(renSp, "Item1"))[0], "To") == "TST_STEEL ROOF"
      && ((IList)FW(renSp, "Item2")).Count == 0 && ((IList)FW(delSp, "Item1")).Count == 1 && (string)FW(((IList)FW(delSp, "Item1"))[0], "Workset") == "TST_STEEL ROOF "
      && WA("SpacesNote").Invoke(null, new object[] { "TST_STEEL ROOF " }) != null && WA("SpacesNote").Invoke(null, new object[] { "TST_STR" }) == null,
      "workset names with trailing spaces: found by the trimmed name, renamed to drop the spaces (not 'already named'), deleted, and reported");
    bool CIC(string t, bool sp) => (bool)WA("CountedInCheck").Invoke(null, new object[] { t, sp })!;
    Check(CIC("Model", false) && CIC("Annotation", false) && !CIC(null, false) && !CIC("Model", true) && !CIC("Internal", false) && !CIC("AnalyticalModel", false),
      "self-check counts: model and annotation categories only; sketch planes and uncategorised side effects never trip it");
    string? CK(string? t, bool sp, string? bic, string n) => (string?)WA("CheckKey").Invoke(null, new object?[] { t, sp, bic, n });
    Check(CK("Internal", false, "OST_Sheets", "Sheets") == "views and sheets: Sheets" && CK("Internal", false, "OST_Viewports", "Viewports") == "views and sheets: Viewports"
      && CK("Model", false, "OST_Walls", "Walls") == "Walls" && CK("Internal", false, "OST_IOSSketchGrid", "x") == null && CK(null, false, null, "(no category)") == null && CK("Model", true, "OST_Walls", "x") == null,
      "self-check counts: views, sheets, schedules and viewports are a separate count that must stay equal");
    var lt1 = PS("move", "TST_LINKS", 0, 2, 1, 21); var lt2 = PS("move", "TST_LINKS", 0, 2, 1, 21); ((List<long>)FW(lt2, "TypeIds")).Add(22);
    Check(PHW(PL(lt1)) != PHW(PL(lt2)), "workset plan hash: a link type moved with its instance is part of the plan");

    // 22. 1.13.0 solver: all-touching in chunks, flips need a confirmation, faces that touch, warnings named by plan item.
    var chIn = new List<(int, string, long, long)> { (1, "TST_L1", 30, 31), (0, "TST_L0", 50, 51), (1, "TST_L1", 12, 40), (0, "TST_L0", 9, 70), (2, "TST_L2", 1, 2) };
    var ch = (List<List<int>>)R("Chunks").Invoke(null, new object[] { chIn, 2 })!;
    var chFlat = ch.Select(c => string.Join(",", c)).ToList();
    var chAgain = (List<List<int>>)R("Chunks").Invoke(null, new object[] { chIn.AsEnumerable().Reverse().ToList(), 2 })!;
    var chOne = (List<List<int>>)R("Chunks").Invoke(null, new object[] { chIn, 500 })!;
    Check(chFlat.SequenceEqual(new[] { "3,1", "2,0", "4" }) && chAgain.Count == 3 && chAgain[0].Count == 2 && chOne.Count == 1 && chOne[0].Count == 5
      && ((List<List<int>>)R("Chunks").Invoke(null, new object[] { new List<(int, string, long, long)>(), 500 })!).Count == 0,
      $"all-touching chunks: by level then id, cut by chunk size; one chunk below the size ({string.Join(" | ", chFlat)})");
    string TDec(bool j, bool r, bool t, bool c, bool f) => (string)R("TouchDecision").Invoke(null, new object[] { j, r, t, c, f })!;
    Check(TDec(false, true, true, false, false) == "join" && TDec(false, true, false, false, false) == "not-touching" && TDec(true, true, true, false, false) == "right"
      && TDec(true, false, true, false, false) == "flip-confirm" && TDec(true, false, true, false, true) == "switch-join" && TDec(true, false, true, true, false) == "switch-join",
      "all-touching: missing joins only where the two really touch; a wrong-order join is switched only when the pair clashes or its flip is confirmed");
    bool FT(double[] na, double[] oa, double[] nb, double[] ob) => (bool)R("FacesTouch").Invoke(null, new object[] { na, oa, nb, ob, 0.003 })!;
    Check(FT(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }, new[] { 0.0, 0, -2 }, new[] { 5.0, 5, 3.002 }) && !FT(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }, new[] { 0.0, 0, -1 }, new[] { 0.0, 0, 3.01 })
      && !FT(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }) && !FT(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 3 }),
      "real contact: faces touch when their normals are opposite and their planes within 1 mm; near only is not touching");
    var pis = new List<(string, long, long)> { ("join:1>2", 1, 2), ("join:3>4", 3, 4), ("trim:2>9:1", 2, 9) };
    var pi1 = (List<string>)R("PlanItemsFor").Invoke(null, new object[] { new long[] { 2, 1 }, pis })!;
    var pi2 = (List<string>)R("PlanItemsFor").Invoke(null, new object[] { new long[] { 4, 77 }, pis })!;
    var pn2 = (List<string>)R("PlanItemsNear").Invoke(null, new object[] { new long[] { 4, 77 }, pis })!;
    Check(pi1.SequenceEqual(new[] { "join:1>2" }) && pi2.Count == 0 && pn2.SequenceEqual(new[] { "join:3>4" }) && (bool)R("IsNotIntersecting").Invoke(null, new object[] { "Highlighted elements are joined but do not intersect." })!
      && !(bool)R("IsNotIntersecting").Invoke(null, new object[] { "Highlighted walls overlap." })!,
      "guard: a warning names only the steps joining both its elements (others are listed as near, never dropped); \"joined but do not intersect\" is recognised");

    // 1.13.7 drop_failing_steps / exclude_ids (synthetic ids).
    var fsSteps = new List<(string, long, long)> { ("join:10>11", 10, 11), ("join:10>12", 10, 12), ("join:20>21", 20, 21) };
    var fsExact = (Dictionary<string, List<int>>?)R("FailingSteps").Invoke(null, new object[] { new List<long[]> { new long[] { 11, 10 } }, fsSteps });
    var fsNear = (Dictionary<string, List<int>>?)R("FailingSteps").Invoke(null, new object[] { new List<long[]> { new long[] { 10 } }, fsSteps });
    var fsNone = R("FailingSteps").Invoke(null, new object[] { new List<long[]> { new long[] { 99 } }, fsSteps });
    var fsNoIds = R("FailingSteps").Invoke(null, new object[] { new List<long[]> { new long[0] }, fsSteps });
    Check(fsExact != null && fsExact.Keys.SequenceEqual(new[] { "join:10>11" }) && fsNear != null && fsNear.Keys.OrderBy(k => k).SequenceEqual(new[] { "join:10>11", "join:10>12" })
      && fsNone == null && fsNoIds == null,
      "drop failing steps: a failure's elements name the steps joining both, else the steps changing one; no ids or no step = find them by trial runs");
    List<int>? Bis(int n, HashSet<int> bad, int max, out int trials)
    {
      var args = new object?[] { n, new Func<List<int>, bool>(set => set.Any(bad.Contains)), max, 0 };
      var res = (List<int>?)R("Bisect").Invoke(null, args);
      trials = (int)args[3]!;
      return res;
    }
    var b1 = Bis(8, new HashSet<int> { 5 }, 24, out var bt1);
    var b2 = Bis(8, new HashSet<int> { 1, 6 }, 24, out var t2);
    var b3 = Bis(1, new HashSet<int> { 0 }, 24, out var t3);
    var b4 = Bis(500, new HashSet<int> { 7, 300, 499 }, 4, out _);
    var bTogether = (List<int>?)R("Bisect").Invoke(null, new object?[] { 4, new Func<List<int>, bool>(set => set.Count == 4), 24, 0 });
    var b2again = Bis(8, new HashSet<int> { 1, 6 }, 24, out var t2b);
    Check(b1 != null && b1.SequenceEqual(new[] { 5 }) && bt1 == 6 && b2 != null && b2.SequenceEqual(new[] { 1, 6 }) && b2again!.SequenceEqual(b2) && t2b == t2
      && b3 != null && b3.SequenceEqual(new[] { 0 }) && t3 == 0 && b4 == null && bTogether == null,
      $"drop failing steps: halving finds the failing steps deterministically ({bt1} and {t2} trial runs); null over the trial cap or when steps fail only together");
    long[] Ids(string t) => (long[])R("IdsInText").Invoke(null, new object[] { t })!;
    Check(Ids("Can't cut joined element. (elements: 101, 202)").SequenceEqual(new long[] { 101, 202 }) && Ids("Can't cut joined element.").Length == 0,
      "drop failing steps: the element ids of a Revit error as the model guard writes it");
    bool TE(long a, long b, ICollection<long> ex) => (bool)R("TouchesExcluded").Invoke(null, new object[] { a, b, ex })!;
    Check(TE(1, 2, new HashSet<long> { 2 }) && !TE(1, 2, new HashSet<long> { 3 }) && !TE(1, 2, new HashSet<long>()), "exclude_ids: a step changing an excluded element is not planned");

    Check((string)WP("KeptWhy").Invoke(null, new object[] { 3, 3 })! == "holds 3 types (a plan moves instances only)" && ((string)WP("KeptWhy").Invoke(null, new object[] { 5, 2 })!).StartsWith("not empty after the moves (5 elements, 2 of them types")
      && (string)WP("KeptWhy").Invoke(null, new object[] { 1, 0 })! == "not empty after the moves (1 element)", "workset plan: a workset holding only types is kept (\"holds N types\"), never a plan-wide rollback");

    // 1.13.3: sketches are never grouped; level code and phrase; the reset method's pick.
    Check((bool)WP("IsSketchCategory").Invoke(null, new object[] { "<Sketch>" })! && (bool)WP("IsSketchCategory").Invoke(null, new object[] { "<Stair/Ramp Sketch: Boundary>" })!
      && !(bool)WP("IsSketchCategory").Invoke(null, new object[] { "Walls" })! && !(bool)WP("IsSketchCategory").Invoke(null, new object[] { "<" })!, "workset plan: sketch categories (<...>) are recognised");
    string? LCode(string n) => (string?)WP("LevelCode").Invoke(null, new object[] { n });
    string? LPhrase(string n) => (string?)WP("LevelPhrase").Invoke(null, new object[] { n });
    Check(LCode("A-01(FIRST FLOOR)") == "01" && LCode("C-GF(GROUND FLOOR)") == "GF" && LCode("ST-B1 (BASEMENT)") == "B1" && LCode("Level 1") == "Level 1" && LCode("-01") == "-01" && LCode("(ROOF)") == null
      && LPhrase("A-01(FIRST FLOOR)") == "FIRST FLOOR" && LPhrase("C-GF ( GROUND FLOOR )") == "GROUND FLOOR" && LPhrase("Level 1") == null && LPhrase("A-02()") == null,
      "workset plan: level code (before \"(\", prefix removed) and phrase (the words in brackets)");
    var rpEls = new List<(long, long, string?, bool, bool)> { (1, 10, "TST_A|Walls", false, false), (2, 10, "TST_A|Walls", true, false), (3, 11, "TST_K|Floors", false, false), (4, 12, "TST_B|Doors", false, true),
        (5, 20, "TST_D|Doors", false, false), (6, 30, "TST_C|Columns", false, false), (7, 10, "TST_A|Walls", false, false), (8, 30, "TST_C|Beams", false, false), (9, 12, "TST_C|Beams", true, false) };
    object RP(ICollection<string> keys) => WP("ResetPick").Invoke(null, new object[] { rpEls, (ICollection<long>)new List<long> { 7 }, (ICollection<long>)new HashSet<long> { 11 }, keys, 20L })!;
    List<long> RpF(object r, string f) => (List<long>)r.GetType().GetField(f)!.GetValue(r)!;
    var rp = RP(new HashSet<string>());
    Check(RpF(rp, "Item1").SequenceEqual(new long[] { 1, 6, 8 }) && RpF(rp, "Item2").SequenceEqual(new long[] { 2, 9 }) && RpF(rp, "Item3").SequenceEqual(new long[] { 4 }),
      "workset plan: reset moves the rest to the default workset, leaves claimed, kept and already-there elements, skips borrowed and changed-in-central ones");
    var rpk = RP(new HashSet<string>(StringComparer.Ordinal) { "TST_C|Beams" });
    Check(RpF(rpk, "Item1").SequenceEqual(new long[] { 1, 6 }) && RpF(rpk, "Item2").SequenceEqual(new long[] { 2 }),
      "workset plan: reset with keep_keys leaves those groups' elements alone (borrowed ones too: they are not pending)");
    var rpc = WP("ResetPick").Invoke(null, new object[] { rpEls, (ICollection<long>)new List<long> { 7, 2, 9 }, (ICollection<long>)new HashSet<long> { 11 }, (ICollection<string>)new HashSet<string>(), 20L })!;
    Check(RpF(rpc, "Item1").SequenceEqual(new long[] { 1, 6, 8 }) && RpF(rpc, "Item2").Count == 0,
      "workset plan: borrowed elements a group move already claimed (pending with the group's target) are not picked up again by reset");
    var vd = WP("VisibilityDiff").Invoke(null, new object[] { new List<long> { 1, 2, 3, 4 }, (ICollection<long>)new HashSet<long> { 1, 2, 9 }, (ICollection<long>)new HashSet<long> { 2, 3, 8 } })!;
    Check(((List<long>)vd.GetType().GetField("Item1")!.GetValue(vd)!).SequenceEqual(new long[] { 1 }) && ((List<long>)vd.GetType().GetField("Item2")!.GetValue(vd)!).SequenceEqual(new long[] { 3 }),
      "workset plan visibility: among the moved elements only, hidden = shown before and not after, shown = the reverse");
    bool VA(bool all, string[]? names, string view, string from, string to) => (bool)WP("VisibilityAllowed").Invoke(null, new object?[] { all, names?.ToList(), view, from, to })!;
    Check(VA(true, null, "TST_KEY PLAN", "TST_A", "TST_B") && !VA(false, null, "TST_KEY PLAN", "TST_A", "TST_B") && VA(false, new[] { " tst_key plan " }, "TST_KEY PLAN", "TST_A", "TST_B")
      && VA(false, new[] { "TST_B" }, "TST_V", "TST_A", "TST_B") && VA(false, new[] { "TST_A" }, "TST_V", "TST_A", "TST_B") && !VA(false, new[] { "TST_C" }, "TST_V", "TST_A", "TST_B"),
      "workset plan visibility: allow_visibility_change true, or naming the view or either workset, accepts a change");
    var ifcStored = (Dictionary<string, string>)VM("IfcStored").Invoke(null, new object[] { new List<(string, string)> { ("MapField", "{\"Name\":\"TST Setup\",\"IFCVersion\":21,\"SpaceBoundaries\":1,\"SplitWallsAndColumns\":true,\"ExportUserDefinedPsets\":false}"), ("Other", "plain") }, new List<KeyValuePair<string, string>> { new("ExportBaseQuantities", "True") } })!;
    var ifcCfg = (System.Text.Json.Nodes.JsonObject)VM("IfcSavedConfig").Invoke(null, new object[] { ifcStored })!;
    Check(ifcStored["Name"] == "TST Setup" && ifcStored["Other"] == "plain" && (string?)ifcCfg["FileVersion"] == "21" && (string?)ifcCfg["SpaceBoundaryLevel"] == "1" && (string?)ifcCfg["WallAndColumnSplitting"] == "true"
      && (string?)ifcCfg["ExportBaseQuantities"] == "true" && (string?)ifcCfg["ExportUserDefinedPsets"] == "false" && !ifcCfg.ContainsKey("Name"),
      "IFC setups saved in the model: JSON and map fields read, exporter names mapped onto the IFC options, the rest passed by name");
    Check((string)VM("PaperName").Invoke(null, new object[] { 841.0, 594.0 })! == "ISO A1" && (string)VM("PaperName").Invoke(null, new object[] { 420.0, 297.5 })! == "ISO A3"
      && (string)VM("PaperName").Invoke(null, new object[] { 1000.0, 707.0 })! == "1000 x 707 mm" && (string)VM("FileStem").Invoke(null, new object[] { "TST:Model?" })! == "TST_Model_"
      && (bool)VM("SheetMatches").Invoke(null, new object[] { "tst-101", "TST-101", "GA" })! && (bool)VM("SheetMatches").Invoke(null, new object[] { "ga", "TST-101", "GA" })!,
      "DWFx: paper named from the sheet's size, file named as the model, sheets by number or name");
    var ifcEnums = new List<string> { "IFC2x2", "IFC2x3", "IFC2x3CV2", "IFC4RV", "IFC4DTV", "IFC4", "Default" };
    string? IV(string t) => (string?)VM("IfcVersionName").Invoke(null, new object[] { t, ifcEnums });
    Check(IV("IFC 2x3 Coordination View 2.0") == "IFC2x3CV2" && IV("ifc4 reference view") == "IFC4RV" && IV("IFC2x3") == "IFC2x3" && IV("IFC 2x3 Coordination View") == "IFC2x3"
      && IV("IFC-SG") == null && IV("IFC 9") == null && IV("") == null,
      "IFC version: the exporter's display names map to the enum; an unknown name (or one this Revit lacks) is refused");

    // 1.14.5 DWFx true size: title block size -> paper format; the page sizes read back from a synthetic DWFx (XPS zip).
    string? PF(double w, double h) { var r = VM("PaperFormatFor").Invoke(null, new object[] { w, h, 2.0 }); if (r == null) return null; var t = r.GetType(); return (string)t.GetField("Item1")!.GetValue(r)! + ((bool)t.GetField("Item2")!.GetValue(r)! ? " P" : " L"); }
    Check(PF(841, 594) == "ISO_A1 L" && PF(594, 841) == "ISO_A1 P" && PF(842.5, 593) == "ISO_A1 L" && PF(1189, 841) == "ISO_A0 L" && PF(279.4, 215.9) == "ANSI_A L"
      && PF(914.4, 609.6) == "ARCH_D L" && PF(431.8, 279.4) == "ANSI_B L" && PF(1000, 707) == "ISO_B1 L" && PF(845, 594) == null && PF(700, 500) == null && PF(0, 0) == null,
      "DWFx paper: a title block size maps to the nearest ISO / ANSI / ARCH format within 2 mm, with its orientation; no fit = null");
    // A synthetic DWFx: pages in parts 1..n; fdocOrder (1-based part numbers) lists them in a FixedDocument, in that order.
    byte[] Dwfx((double W, double H)[] pagesIn, int[]? fdocOrder = null, bool paper = true)
    {
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      using var ms = new MemoryStream();
      using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
      {
        void Put(string n, string x) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(x); }
        Put("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        for (var i = 0; i < pagesIn.Length; i++)
          Put($"dwf/documents/1/pages/{i + 1}.fpage", $"<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"{(pagesIn[i].W / 25.4 * 96).ToString(inv)}\" Height=\"{(pagesIn[i].H / 25.4 * 96).ToString(inv)}\" xml:lang=\"en-US\"/>");
        if (fdocOrder != null)
          Put("dwf/documents/1/doc.fdoc", "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\">" + string.Concat(fdocOrder.Select(n => $"<PageContent Source=\"pages/{n}.fpage\"/>")) + "</FixedDocument>");
        if (paper) Put("dwf/documents/1/descriptor.xml", "<ePlot:Section xmlns:ePlot=\"DWF-ePlot:7.0\"><ePlot:Paper units=\"mm\" width=\"279.4\" height=\"215.9\"/></ePlot:Section>");
      }
      return ms.ToArray();
    }
    (IList Pages, IList Papers) Read(byte[] b) { var r = VM("DwfxPages").Invoke(null, new object[] { new MemoryStream(b) })!; var t = r.GetType(); return ((IList)t.GetField("Item1")!.GetValue(r)!, (IList)t.GetField("Item2")!.GetValue(r)!); }
    double PgW(object p) => (double)p.GetType().GetField("Item2")!.GetValue(p)!;
    double PgH(object p) => (double)p.GetType().GetField("Item3")!.GetValue(p)!;
    // PageCheck: (sheets, pages, papers, defaultPaper) -> per-sheet ok flags, problems, warnings.
    (List<bool> Ok, List<string> Problems, List<string> Warnings) PC((string, double, double)[] sheetsIn, (IList Pages, IList Papers) r, bool def)
    {
      var res = VM("PageCheck").Invoke(null, new object[] { sheetsIn.ToList(), r.Pages, r.Papers, def, 2.0 })!; var t = res.GetType();
      var rows = (IList)t.GetField("Item1")!.GetValue(res)!;
      return (rows.Cast<object>().Select(x => (bool)x.GetType().GetField("Item3")!.GetValue(x)!).ToList(), (List<string>)t.GetField("Item2")!.GetValue(res)!, (List<string>)t.GetField("Item3")!.GetValue(res)!);
    }
    var a1L = ("A-101", 841.0, 594.0); var a1L2 = ("A-102", 841.0, 594.0); var a1L3 = ("A-103", 841.0, 594.0);
    var letter = Read(Dwfx(new[] { (279.4, 215.9), (279.4, 215.9) }));
    var badLetter = PC(new[] { a1L, a1L2 }, letter, false);
    var a1ok = PC(new[] { a1L, a1L2 }, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0) }, paper: false)), false);
    Check(letter.Pages.Count == 2 && Math.Abs(PgW(letter.Pages[0]!) - 279.4) < 0.01 && Math.Abs(PgH(letter.Pages[0]!) - 215.9) < 0.01 && letter.Papers.Count == 1
      && badLetter.Ok.All(x => !x) && badLetter.Problems.Count == 3 && badLetter.Problems[0].Contains("279.4 x 215.9") && badLetter.Problems[2].StartsWith("ePlot paper 279.4 x 215.9") && a1ok.Ok.All(x => x) && a1ok.Problems.Count == 0,
      "DWFx self-check: FixedPage sizes (1/96 in) and ePlot paper read from the package; US Letter pages for A1 sheets fail, A1 pages pass");
    // 3: orientation: an A1 portrait page for an A1 landscape title block fails.
    var turned = PC(new[] { a1L, a1L2 }, Read(Dwfx(new[] { (841.0, 594.0), (594.0, 841.0) }, paper: false)), false);
    Check(turned.Ok.SequenceEqual(new[] { true, false }) && turned.Problems.Count == 1 && turned.Problems[0].StartsWith("sheet A-102"),
      "DWFx self-check: a page in the other orientation than its title block fails (only that sheet)");
    // 4: page i against sheet i in reading order (the FixedDocument's order, else natural part order); a count mismatch fails all.
    var ordered = Read(Dwfx(new[] { (594.0, 420.0), (841.0, 594.0), (841.0, 594.0) }, new[] { 2, 3, 1 }, paper: false));
    var byOrder = PC(new[] { a1L, a1L2, ("A-103", 594.0, 420.0) }, ordered, false);
    var manyPages = Read(Dwfx(Enumerable.Range(0, 11).Select(i => i == 9 ? (594.0, 420.0) : (841.0, 594.0)).ToArray(), paper: false));
    var natural = PC(Enumerable.Range(0, 11).Select(i => ($"S{i + 1}", 841.0, 594.0)).ToArray(), manyPages, false);
    var fewer = PC(new[] { a1L, a1L2, a1L3 }, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0) }, paper: false)), false);
    Check(byOrder.Ok.All(x => x) && natural.Ok.Count(x => !x) == 1 && !natural.Ok[9] && fewer.Ok.All(x => !x) && fewer.Problems.SequenceEqual(new[] { "page count 2 for 3 sheets" }),
      "DWFx self-check: page i is compared with sheet i (FixedDocument order, else 2 before 10), so only the wrong sheet is flagged; page count N for M sheets fails the group");
    // 2: a Default-paper group (allow_paper_fallback) is not failed on size: it is reported as scaled.
    var deflt = PC(new[] { ("A-900", 900.0, 600.0), ("A-901", 0.0, 0.0) }, letter, true);
    Check(deflt.Ok.All(x => x) && deflt.Problems.Count == 0 && deflt.Warnings.Single().StartsWith("scaled onto 279.4 x 215.9 mm"),
      "DWFx self-check: Default paper is reported as scaled, not failed (sizes unreadable or unfit)");
    // 1: one file per paper group, names unique in the run; a Default group carries its size.
    List<string> GF(string stem, string single, params (string, bool, double, double)[] g) => (List<string>)VM("DwfxGroupFiles").Invoke(null, new object[] { stem, single, g.ToList() })!;
    var gf = GF("TST", "TST.dwfx", ("ISO_A1", false, 841, 594), ("ISO_A1", true, 594, 841), ("Default", false, 900, 600), ("Default", false, 1000, 700), ("Default", false, 900.2, 600.3));
    Check(GF("TST", "TST.dwfx", ("ISO_A1", false, 841, 594)).SequenceEqual(new[] { "TST.dwfx" })
      && gf.SequenceEqual(new[] { "TST - ISO A1.dwfx", "TST - ISO A1 portrait.dwfx", "TST - Default 900x600.dwfx", "TST - Default 1000x700.dwfx", "TST - Default 900x600 2.dwfx" }),
      $"DWFx files: one per paper group, unique names; Default groups named by size ({string.Join(" | ", gf)})");
    object ShChk(string[] r, string[] e) => VM("SheetCheck").Invoke(null, new object[] { r.ToList(), e.ToList() })!;
    var sc = ShChk(new[] { "A-101", "a-102", "A-103" }, new[] { "A-101", "A-102", "A-900" });
    List<string> ScF(object o, string f) => (List<string>)o.GetType().GetField(f)!.GetValue(o)!;
    Check(ScF(sc, "Item1").SequenceEqual(new[] { "A-101", "a-102" }) && ScF(sc, "Item2").SequenceEqual(new[] { "A-103" }) && ScF(sc, "Item3").SequenceEqual(new[] { "A-900" }),
      "DWFx sheet check: asked vs exported gives matched, missing and extra");

    // 1.14.8 #33: dwfx.views: 3D views in the combined DWFx with the sheets. View pages have no title block: they are left out
    // of the size check and identified by order and count (first, else last); the page count expected is sheets + views.
    (List<bool> Ok, List<string?> ViewPages, List<string> Problems, List<string> Warnings, string? At) PV((string, double, double)[] sheetsIn, string[] viewsIn, (IList Pages, IList Papers) r, bool def = false)
    {
      var res = VM("PageCheckWithViews").Invoke(null, new object[] { sheetsIn.ToList(), viewsIn.ToList(), r.Pages, r.Papers, def, 2.0 })!; var t = res.GetType();
      var rows = (IList)t.GetField("Item1")!.GetValue(res)!; var vrows = (IList)t.GetField("Item2")!.GetValue(res)!;
      return (rows.Cast<object>().Select(x => (bool)x.GetType().GetField("Item3")!.GetValue(x)!).ToList(), vrows.Cast<object>().Select(x => (string?)x.GetType().GetField("Item2")!.GetValue(x)).ToList(),
        (List<string>)t.GetField("Item3")!.GetValue(res)!, (List<string>)t.GetField("Item4")!.GetValue(res)!, (string?)t.GetField("Item5")!.GetValue(res));
    }
    var tA = ("TST-101", 841.0, 594.0); var tB = ("TST-102", 841.0, 594.0);
    var v3d = new[] { "TST_NAVISWORKS" };
    // The 3D view page on another paper (Letter) first, then two A1 sheets: the sheets pass, the view page is not compared.
    var vFirst = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (279.4, 215.9), (841.0, 594.0), (841.0, 594.0) }, paper: false)));
    var vLast = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0), (279.4, 215.9) }, paper: false)));
    var vSame = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0), (841.0, 594.0) }, paper: false)));
    Check(vFirst.Ok.All(x => x) && vFirst.Problems.Count == 0 && vFirst.At == "first" && vFirst.ViewPages.Single() == "279.4 x 215.9"
      && vLast.Ok.All(x => x) && vLast.Problems.Count == 0 && vLast.At == "last" && vLast.Warnings.Any(w => w.Contains("after the sheets"))
      && vSame.Ok.All(x => x) && vSame.At == "first" && vSame.ViewPages.Single() == "841 x 594",
      "DWFx with views: view pages (first, else last) are left out of the size check; sheets + views pages expected");
    // A wrong sheet is still caught with a view in the file; a missing page fails the count; no page for the view: sheets checked, warning.
    var vBad = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (841.0, 594.0), (594.0, 841.0), (841.0, 594.0) }, paper: false)));
    var vCount = PV(new[] { tA, tB }, new[] { "TST_NAVISWORKS", "TST_3D 2" }, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0), (841.0, 594.0) }, paper: false)));
    var vNoPage = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0) }, paper: false)));
    var vNone = PV(new[] { tA, tB }, new string[0], Read(Dwfx(new[] { (841.0, 594.0), (841.0, 594.0) }, paper: false)));
    Check(vBad.Problems.Count == 1 && vBad.Problems[0].StartsWith("sheet TST-101") && vBad.At == "first" && vBad.Ok.SequenceEqual(new[] { false, true })
      && vCount.Ok.All(x => !x) && vCount.Problems.SequenceEqual(new[] { "page count 3 for 2 sheets and 2 views" })
      && vNoPage.Ok.All(x => x) && vNoPage.Problems.Count == 0 && vNoPage.At == "no-page" && vNoPage.Warnings.Any(w => w.Contains("TST_NAVISWORKS") && w.Contains("3D section"))
      && vNone.Ok.All(x => x) && vNone.At == null && vNone.ViewPages.Count == 0,
      "DWFx with views: a wrong sheet page still fails; page count N for M sheets and K views fails all; no view page warns; no views = PageCheck");
    // ePlot papers: one per page are split like the pages; otherwise a paper that fits no sheet is taken as the view's.
    var vPaper = PV(new[] { tA, tB }, v3d, Read(Dwfx(new[] { (279.4, 215.9), (841.0, 594.0), (841.0, 594.0) })));
    Check(vPaper.Problems.Count == 0 && vPaper.Ok.All(x => x), "DWFx with views: a view's ePlot paper (fits no sheet) is not reported against the sheets");
    // View names: exact, among 3D views; missing, near-miss (case), ambiguous, not 3D, not printable all give a problem.
    object Pick(string n, params (string, bool, bool)[] vs) => VM("DwfxViewPick").Invoke(null, new object[] { n, vs.ToList() })!;
    int PIdx(object o) => (int)o.GetType().GetField("Item1")!.GetValue(o)!; string? PWhy(object o) => (string?)o.GetType().GetField("Item2")!.GetValue(o);
    var vs3 = new[] { ("TST_PLAN", false, true), ("TST_NAVISWORKS", true, true), ("TST_Coord", true, true), ("TST_COORD2", true, false), ("TST_DUP", true, true), ("TST_DUP", true, true), ("TST_PLAN2", false, true) };
    Check(PIdx(Pick("TST_NAVISWORKS", vs3)) == 1 && PWhy(Pick("TST_NAVISWORKS", vs3)) == null
      && PWhy(Pick("tst_navisworks", vs3))!.Contains("exactly") && PWhy(Pick("tst_navisworks", vs3))!.Contains("TST_NAVISWORKS") && PIdx(Pick("tst_navisworks", vs3)) == -1
      && PWhy(Pick("TST_NONE", vs3)) == "no 3D view is called TST_NONE" && PWhy(Pick("TST_DUP", vs3))!.Contains("ambiguous")
      && PWhy(Pick("TST_PLAN", vs3)) == "TST_PLAN is not a 3D view" && PWhy(Pick("TST_COORD2", vs3))!.Contains("cannot be exported") && PWhy(Pick(" ", vs3)) != null,
      "DWFx views: exact 3D view name; missing, case near-miss (named, not taken), ambiguous, not 3D and not exportable are refused");
    int VG(params int[] c) => (int)VM("DwfxViewsGroup").Invoke(null, new object[] { c.ToList() })!;
    Check(VG(24) == 0 && VG(3, 20, 20) == 1 && VG(5, 5) == 0 && VG() == -1, "DWFx views: the only group, else the largest (first of equals) takes the views");

    // 1.14.8 #36: no margin. The drawn extent of a DWFx page (FixedPage XAML: Path Data / PathGeometry, Glyphs origins,
    // nested RenderTransforms) against the page; below 98 % either way fails. Printer paper sizes matched by name or size.
    const string Xps = "xmlns=\"http://schemas.microsoft.com/xps/2005/06\" xmlns:x=\"http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key\"";
    string Page(string body, double w = 2383.5, double h = 1683.8) => $"<FixedPage {Xps} Width=\"{w.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" Height=\"{h.ToString(System.Globalization.CultureInfo.InvariantCulture)}\">{body}</FixedPage>";
    object Ext(string xml) => VM("PageExtent").Invoke(null, new object[] { xml })!;
    double? RX(object e) => (double?)e.GetType().GetField("RatioX")!.GetValue(e); double? RY(object e) => (double?)e.GetType().GetField("RatioY")!.GetValue(e);
    double EF(object e, string f) => Convert.ToDouble(e.GetType().GetField(f)!.GetValue(e)!);
    bool Near(double? v, double want, double tol = 1e-3) => v != null && Math.Abs(v.Value - want) < tol;
    const string White = "<Path Fill=\"#FFFFFFFF\" Data=\"M 0,0 L 2383.5,0 2383.5,1683.8 0,1683.8 Z\"/>";
    var edge = Ext(Page(White + "<Path Stroke=\"#FF000000\" StrokeThickness=\"1\" Data=\"F1 M 0,0 L 2383.5,0 2383.5,1683.8 0,1683.8 Z\"/>"));
    var margin = Ext(Page(White + "<Canvas><Path Stroke=\"#FF000000\" Data=\"M 108,40 L 2275,40 2275,1640 108,1640 Z\"/><Glyphs OriginX=\"500\" OriginY=\"800\" UnicodeString=\"TST\" FontUri=\"/f.odttf\" FontRenderingEmSize=\"12\"/></Canvas>"));
    var relative = Ext(Page("<Path Stroke=\"#FF000000\" Data=\"m 108,0 h 2167 v 1683.8 c 0,0 -10,0 -20,0 z\"/>"));
    var whiteOnly = Ext(Page(White));
    object EC(string[] n, object[] ex, bool def = false, bool over = false) { var lt = typeof(List<>).MakeGenericType(edge.GetType()); var l = (IList)Activator.CreateInstance(lt)!; foreach (var e in ex) l.Add(e); return VM("ExtentCheck").Invoke(null, new object[] { n.ToList(), l, def, 0.98, over })!; }
    List<string> ECP(object r) => (List<string>)r.GetType().GetField("Item2")!.GetValue(r)!; List<string> ECW(object r) => (List<string>)r.GetType().GetField("Item3")!.GetValue(r)!;
    List<bool> ECOk(object r) => ((IList)r.GetType().GetField("Item1")!.GetValue(r)!).Cast<object>().Select(x => (bool)x.GetType().GetField("Item4")!.GetValue(x)!).ToList();
    var ecPass = EC(new[] { "TST-101" }, new[] { edge }); var ecFail = EC(new[] { "TST-101", "TST-102" }, new[] { edge, margin });
    var ecWhite = EC(new[] { "TST-101" }, new[] { whiteOnly }); var ecDefault = EC(new[] { "TST-101" }, new[] { margin }, true);
    Check(Near(RX(edge), 1) && Near(RY(edge), 1) && ECP(ecPass).Count == 0 && ECOk(ecPass).All(x => x)
      && Near(RX(margin), (2275 - 108) / 2383.5) && Near(EF(margin, "MinX"), 108) && Near(EF(margin, "MaxX"), 2275) && Near(RY(margin), 1600 / 1683.8)
      && ECOk(ecFail).SequenceEqual(new[] { true, false }) && ECP(ecFail).Single().StartsWith("sheet TST-102: drawn over 90.9% x 95% of the page")
      && Near(RX(relative), (2275 - 108) / 2383.5) && Near(RY(relative), 1),
      $"DWFx no margin: edge to edge passes; drawn 108..2275 of 2383.5 fails (ratio {RX(margin):0.000}); relative commands (m, h, v, c, z) read");
    Check(RX(whiteOnly) == null && EF(whiteOnly, "Measured") == 0 && ECP(ecWhite).Count == 0 && ECOk(ecWhite).All(x => x) && ECW(ecWhite).Single().Contains("not measured")
      && ECP(ecDefault).Count == 0 && ECW(ecDefault).Single().Contains("default paper"),
      "DWFx no margin: a white full-page background alone does not count (extent not measured: warning, not failure); Default paper only warns");
    // Transforms: a half-scale Canvas holding a full-page path; nested Canvas with a MatrixTransform element and a translate;
    // a Glyphs' own RenderTransform; a PathGeometry (Figures / segment points) with its Transform; a StaticResource geometry.
    var half = Ext(Page("<Canvas RenderTransform=\"0.5,0,0,0.5,0,0\"><Path Stroke=\"#FF000000\" Data=\"M 0,0 L 2383.5,1683.8\"/></Canvas>"));
    var nested = Ext(Page("<Canvas RenderTransform=\"1,0,0,1,1000,0\"><Canvas><Canvas.RenderTransform><MatrixTransform Matrix=\"2,0,0,2,0,0\"/></Canvas.RenderTransform>"
      + "<Path Stroke=\"#FF000000\" Data=\"M 0,0 L 100,100\"/><Glyphs OriginX=\"0\" OriginY=\"0\" RenderTransform=\"1,0,0,1,300,400\" UnicodeString=\"x\"/></Canvas></Canvas>"));
    var geo = Ext(Page("<Canvas.Resources><ResourceDictionary><PathGeometry x:Key=\"G1\" Figures=\"M 0,0 L 2383.5,1683.8\"/></ResourceDictionary></Canvas.Resources>"
      + "<Path Stroke=\"#FF000000\" Data=\"{StaticResource G1}\"/><Path Stroke=\"#FF000000\"><Path.Data><PathGeometry Transform=\"1,0,0,1,10,0\"><PathFigure StartPoint=\"0,0\"><PolyLineSegment Points=\"100,0 100,100\"/></PathFigure></PathGeometry></Path.Data></Path>"));
    var unresolved = Ext(Page("<Path Stroke=\"#FF000000\" Data=\"{StaticResource Missing}\"/><Path Stroke=\"#FF000000\" Data=\"M 500,500 L 600,600\"/>"));
    var ecUnres = EC(new[] { "TST-101" }, new[] { unresolved });
    Check(Near(RX(half), 0.5) && Near(RY(half), 0.5) && Near(EF(nested, "MinX"), 1000) && Near(EF(nested, "MaxX"), 1600) && Near(EF(nested, "MaxY"), 800) && Near(EF(nested, "MinY"), 0)
      && Near(RX(geo), 1) && Near(RY(geo), 1) && EF(unresolved, "Measured") == 1 && ECP(ecUnres).Count == 0 && ECW(ecUnres).Single().Contains("could not be read"),
      "DWFx no margin: nested RenderTransforms (attribute and MatrixTransform), Glyphs transforms, PathGeometry and StaticResource honoured; unreadable geometry only warns");
    // Through the package: DwfxPages gives each page's extent in page order; sheet pages found after the view pages.
    byte[] DwfxWith(params string[] fpages)
    {
      using var ms = new MemoryStream();
      using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        for (var i = 0; i < fpages.Length; i++) { using var w = new StreamWriter(z.CreateEntry($"dwf/documents/1/pages/{i + 1}.fpage").Open()); w.Write(fpages[i]); }
      return ms.ToArray();
    }
    var pkg = VM("DwfxPages").Invoke(null, new object[] { new MemoryStream(DwfxWith(Page(White + "<Path Stroke=\"#FF000000\" Data=\"M 0,0 L 2383.5,1683.8\"/>"), Page("<Path Stroke=\"#FF000000\" Data=\"M 108,0 L 2275,1683.8\"/>"))) })!;
    var pkgExt = (IList)pkg.GetType().GetField("Item3")!.GetValue(pkg)!;
    int SPO(int s, int v, int p, string? at) => (int)VM("SheetPageOffset").Invoke(null, new object?[] { s, v, p, at })!;
    Check(pkgExt.Count == 2 && Near(RX(pkgExt[0]!), 1) && Near(RX(pkgExt[1]!), (2275 - 108) / 2383.5)
      && SPO(2, 1, 3, "first") == 1 && SPO(2, 1, 3, "last") == 0 && SPO(2, 1, 2, "no-page") == 0 && SPO(2, 0, 2, null) == 0 && SPO(2, 0, 3, null) == -1 && SPO(2, 2, 3, null) == -1,
      "DWFx no margin: each page's extent read from the package; sheet pages located after the view pages");
    // Printer paper: by name (A1, ISO A1, full bleed), by the size printed in the name, never a contradicting or JIS size.
    int PP(string fmt, params string[] names) => (int)VM("PrinterPaperFor").Invoke(null, new object[] { fmt, names.ToList(), 2.0 })!;
    object? PNS(string n) => VM("PaperNameSize").Invoke(null, new object[] { n });
    var pns = PNS("33.11 x 23.39 in"); var pnsW = pns == null ? 0 : (double)pns.GetType().GetField("Item1")!.GetValue(pns)!;
    Check(PP("ISO_A1", "Letter", "A1", "A10") == 1 && PP("ISO_A1", "Letter", "ISO full bleed A1 (841.00 x 594.00 MM)", "ISO A1 (841.00 x 594.00 MM)") == 2
      && PP("ISO_A1", "Letter", "iso_a1") == 1 && PP("ISO_A1", "Letter", "A10", "A1 (600 x 900 mm)") == -1 && PP("ISO_A1", "Custom 841x594mm") == 0
      && PP("ISO_A1", "ISO full bleed A1 (841.00 x 594.00 MM)") == 0 && PP("ISO_A1", "A1 Rotated", "ISO A1") == 1 && PP("ISO_A1") == -1
      && PP("ANSI_B", "Letter", "Tabloid") == 1 && PP("ANSI_B", "11x17") == 0 && PP("ISO_B4", "B4 (JIS)") == -1 && PP("ISO_B4", "B4 (JIS)", "B4 (ISO)") == 1
      && PP("ARCH_D", "ANSI D", "ARCH D (24.00 x 36.00 Inches)") == 1 && PP("Default", "A1") == -1 && Math.Abs(pnsW - 841) < 1 && PNS("A1") == null,
      "DWFx printer paper: matched by name (A1 / ISO A1 / full bleed), else by the size in the name; contradicting, JIS and missing sizes give none");

    // Sheet extents (#37): content beyond the title block frame blocks a merged DWFx (every page scales to the largest extents).
    var spT = vml.GetNestedType("SheetPart", BindingFlags.Public)!;
    object SP(string label, double x0, double y0, double x1, double y1, bool tb = false, double fw = 0, double fh = 0, double ox = 0, double oy = 0, string fam = null)
    {
      var o = Activator.CreateInstance(spT)!;
      void Set(string f, object v) => spT.GetField(f)!.SetValue(o, v);
      Set("Label", label); Set("Family", fam); Set("TitleBlock", tb); Set("MinX", x0); Set("MinY", y0); Set("MaxX", x1); Set("MaxY", y1);
      Set("FrameW", fw); Set("FrameH", fh); Set("OriginX", ox); Set("OriginY", oy);
      return o;
    }
    (bool Ok, string Text, List<string> Over) SEC(double? ow, double? oh, double tol, params object[] parts)
    {
      var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(spT))!; foreach (var p in parts) l.Add(p);
      var r = VM("SheetExtentCheck").Invoke(null, new object?[] { "TST-101", 841.0, 594.0, ow, oh, l, tol, 5 })!; var t = r.GetType();
      return ((bool)t.GetField("Item1")!.GetValue(r)!, (string)t.GetField("Item2")!.GetValue(r)!, (List<string>)t.GetField("Item3")!.GetValue(r)!);
    }
    var tbOk = SP("Title Blocks TST Header: A1 (id 11)", 0, 0, 841, 594, true, 841, 594, 0, 0, "TST Header");
    var vpIn = SP("Viewports Viewport: Title w Line (id 12)", 20, 20, 800, 580);
    var onFrame = SEC(841, 594, 1, tbOk, vpIn);
    var vpUp = SEC(841, 610, 1, tbOk, vpIn, SP("Lines Lines (id 13)", 100, 100, 300, 610));
    var inTol = SEC(841.6, 594.8, 1, tbOk, SP("Text Notes TST (id 14)", -0.5, 0, 200, 594.8));
    var tbBig = SEC(841, 610, 1, SP("Title Blocks TST Header: A1 (id 21)", 0, 0, 841, 610, true, 841, 594, 0, 0, "TST Header"), vpIn);
    // A title block whose origin is its bottom-right corner (frame at -841..0): the frame follows the insertion point.
    var tbRight = SEC(841, 610, 1, SP("Title Blocks TST Header: A1 (id 31)", -841, 0, 0, 610, true, 841, 594, 0, 0, "TST Header"), SP("Viewports Viewport (id 32)", -820, 20, -20, 580));
    var outlineOnly = SEC(841, 610, 1, tbOk, vpIn);
    var tooMany = SEC(null, null, 1, new object[] { tbOk }.Concat(Enumerable.Range(0, 8).Select(i => SP($"Lines L{i} (id {40 + i})", 0, 0, 845, 10))).ToArray());
    Check(onFrame.Ok && onFrame.Text == null && onFrame.Over.Count == 0 && inTol.Ok,
      "sheet extents: a sheet exactly on its frame passes; 0.5 / 0.8 mm beyond is within the 1 mm tolerance");
    Check(!vpUp.Ok && vpUp.Over.Single() == "Lines Lines (id 13): 16 mm above the top edge" && vpUp.Text.StartsWith("sheet TST-101: outline 841 x 610 mm on 841 x 594 mm paper; beyond the frame: Lines Lines (id 13)")
      && !SEC(841, 594, 1, tbOk, SP("Lines Lines (id 13)", 100, 100, 300, 595.5)).Ok && SEC(841, 594, 2, tbOk, SP("Lines Lines (id 13)", 100, 100, 300, 595.5)).Ok,
      $"sheet extents: an element 16 mm above the frame fails and is named with its edge; 1.5 mm fails at 1 mm tolerance, passes at 2 ({vpUp.Text})");
    Check(!tbBig.Ok && tbBig.Text.Contains("the title block family TST Header has geometry outside its frame (possibly invisible lines): edit the family") && tbBig.Over.Single().Contains("16 mm above the top edge")
      && !tbRight.Ok && tbRight.Over.Count == 1 && tbRight.Over[0].StartsWith("Title Blocks") && tbRight.Over[0].EndsWith("16 mm above the top edge"),
      $"sheet extents: a title block box larger than its Sheet Width / Height names the family to edit (origin bottom-left or bottom-right) ({tbBig.Text})");
    Check(!outlineOnly.Ok && outlineOnly.Over.Count == 0 && outlineOnly.Text.Contains("nothing measured on the sheet sticks out: most often invisible lines in the title block family TST Header")
      && !tooMany.Ok && tooMany.Over.Count == 8 && tooMany.Text.Contains("(+3 more)") && tooMany.Text.Contains("4 mm right of the right edge"),
      "sheet extents: an outline larger than the paper with nothing measured beyond still fails (title block named); the parts listed are capped at 5");
    var ecOver = EC(new[] { "TST-101" }, new[] { margin }, false, true);
    Check(ECP(ecOver).Count == 0 && ECOk(ecOver).All(x => x) && ECW(ecOver).Single().Contains("allow_oversize"),
      "DWFx allow_oversize: a page scaled down by content beyond the frame only warns in the drawn-extent check");

    if (failures > 0) { Console.WriteLine($"clash test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("clash test passed");
  }
}
