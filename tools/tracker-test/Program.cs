// Change tracker comparison test (no Revit needed): dotnet run --project tools/tracker-test
using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection;
class P {
  static void Main() {
    var a = Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "AceRevitMcp.dll"));
    var recT = a.GetType("AceRevitMcp.Tracking.ElementRecord"); var snapT = a.GetType("AceRevitMcp.Tracking.Snapshot");
    object Rec(string u, long id, string cat, string type, string lvl, string loc, string p, Dictionary<string,string> k=null) {
      var r = Activator.CreateInstance(recT);
      foreach (var (n,v) in new (string,object)[]{("U",u),("Id",id),("Cat",cat),("Type",type),("Lvl",lvl),("Loc",loc),("P",p),("K",k)}) recT.GetProperty(n).SetValue(r,v);
      return r; }
    object Snap(params object[] recs) { var s = Activator.CreateInstance(snapT); var list = (IList)snapT.GetProperty("Elements").GetValue(s); foreach (var r in recs) list.Add(r); return s; }
    var old = Snap(
      Rec("a",1,"Walls","Basic Wall : 200","L1","0,0,0;5000,0,0","h1"),
      Rec("b",2,"Doors","Door : 900","L1","100,200,0@0","h2", new(){{"Mark","D-01"}}),
      Rec("c",3,"Structural Columns","Col : 400","L1","1000,1000,0@0","h3"),
      Rec("d",4,"Furniture","Desk : 66","L3","0,0,5740@90","h4"));
    var now = Snap(
      Rec("a",1,"Walls","Basic Wall : 200","L1","0,0,0;5000,0,0","h1"),
      Rec("b",2,"Doors","Door : 900","L1","100,200,0@0","h2x", new(){{"Mark","D-02"}}),
      Rec("c",3,"Structural Columns","Col : 500","L1","1300,1400,0@0","h3"),
      Rec("e",5,"Rooms","Room","L2","box 0,0,0 size 10,10,10","h5"));
    var m = a.GetType("AceRevitMcp.Tracking.ChangeTracking").GetMethod("Compare", BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
    var changes = (IEnumerable)m.Invoke(null, new[]{old, now});
    var ct = a.GetType("AceRevitMcp.Tracking.Change");
    var lines = new List<string>();
    foreach (var c in changes) lines.Add($"{ct.GetField("Kind").GetValue(c)} | {ct.GetField("Cat").GetValue(c)} | {ct.GetField("Detail").GetValue(c)}");
    var expected = new[] {
      "deleted | Furniture | ",
      "added | Rooms | on L2",
      "moved | Structural Columns | moved 500 mm",
      "retyped | Structural Columns | Col : 400 -> Col : 500",
      "changed | Doors | Mark: 'D-01' -> 'D-02' (and possibly other values)" };
    foreach (var l in lines) Console.WriteLine(l);
    if (!lines.SequenceEqual(expected)) { Console.WriteLine("tracker test FAILED"); Environment.Exit(1); }
    Console.WriteLine("tracker test passed");
  }
}
