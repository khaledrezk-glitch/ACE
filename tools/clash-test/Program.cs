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

    Check((string)WP("KeptWhy").Invoke(null, new object[] { 3, 3 })! == "holds 3 types (a plan moves instances only)" && ((string)WP("KeptWhy").Invoke(null, new object[] { 5, 2 })!).StartsWith("not empty after the moves (5 elements, 2 of them types")
      && (string)WP("KeptWhy").Invoke(null, new object[] { 1, 0 })! == "not empty after the moves (1 element)", "workset plan: a workset holding only types is kept (\"holds N types\"), never a plan-wide rollback");

    if (failures > 0) { Console.WriteLine($"clash test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("clash test passed");
  }
}
