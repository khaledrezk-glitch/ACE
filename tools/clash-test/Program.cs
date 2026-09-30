// Clash logic test (no Revit needed): dotnet run --project tools/clash-test (after the add-in build)
using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection;
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

    if (failures > 0) { Console.WriteLine($"clash test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("clash test passed");
  }
}
