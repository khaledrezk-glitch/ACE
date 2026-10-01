// Check set test (no Revit needed): dotnet run --project tools/checkset-test (after the add-in build)
using System; using System.Collections; using System.IO; using System.Linq; using System.Reflection;
class P {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }
  static void Main() {
    // A private settings folder for this test (ApplicationData follows XDG_CONFIG_HOME on Linux, APPDATA on Windows).
    var tmp = Path.Combine(Path.GetTempPath(), "ace-checkset-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);   // GetFolderPath returns "" for a folder that does not exist
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmp); Environment.SetEnvironmentVariable("APPDATA", tmp);
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (string.IsNullOrEmpty(appData) || !appData.StartsWith(tmp)) { Console.WriteLine("check set test FAILED (could not redirect the settings folder)"); Environment.Exit(1); }
    var dir = Path.Combine(appData, "ACE-RevitMCP");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "checkset.json"), @"{ ""name"": ""Test office"", ""checks"": {
      ""in-place-families"": { ""warnAt"": 1, ""failAt"": 5 },
      ""design-options"": { ""enabled"": false },
      ""Revit warnings"": { ""maxImpact"": 5 },
      ""detail-lines"": { ""warnAt"": 100000 } } }");
    var a = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "AceRevitMcp.dll"));
    var insT = a.GetType("AceRevitMcp.Dashboard.Insights"); var chkT = a.GetType("AceRevitMcp.Dashboard.Check");
    var keyOf = a.GetType("AceRevitMcp.Dashboard.ModelInsights").GetMethod("KeyOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    string Key(string n) => (string)keyOf.Invoke(null, new object[] { n });
    Check(Key("Linked CAD in 3D (not view-specific)") == "linked-cad-in-3d-not-view-specific", "key of a name with brackets");
    Check(Key("Types named 'Copy' or ending in 2") == "types-named-copy-or-ending-in-2", "key of a name with quotes");
    var x = Activator.CreateInstance(insT);
    var list = (IList)insT.GetField("Checks").GetValue(x);
    object C(string name, int count, double penalty, double max) {
      var c = Activator.CreateInstance(chkT);
      foreach (var (f, v) in new (string, object)[] { ("Key", Key(name)), ("Name", name), ("Area", "A"), ("Count", count), ("Penalty", penalty), ("MaxPenalty", max) }) chkT.GetField(f).SetValue(c, v);
      list.Add(c); return c; }
    string St(object c) => (string)chkT.GetProperty("Status").GetValue(c);
    double Pen(object c) => (double)chkT.GetField("Penalty").GetValue(c);
    var inPlace = C("In-place families", 6, 6, 8);
    var inPlaceOk = C("Design options", 2, 0.5, 1);
    var warnings = C("Revit warnings", 400, 20, 20);
    var lines = C("Detail lines", 2000, 1, 2);
    var untouched = C("Empty sheets", 3, 1.5, 4);
    a.GetType("AceRevitMcp.Dashboard.CheckSet").GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new[] { x });
    Check((string)insT.GetField("CheckSetName").GetValue(x) == "Test office", "check set name is read");
    Check(St(inPlace) == "fail" && (string)chkT.GetField("Rule").GetValue(inPlace) == "review at 1, action at 5", "6 in-place families with failAt 5 -> action needed, rule shown");
    Check(!list.Contains(inPlaceOk), "disabled check is removed");
    Check(Pen(warnings) == 5, "maxImpact caps the score impact (matched by name)");
    Check(St(lines) == "ok" && Pen(lines) == 0, "below warnAt -> pass, no score impact");
    Check(St(untouched) == "warn" && Pen(untouched) == 1.5, "checks not in the set keep the defaults");
    // Presentation standard: text height by view scale, saved into the same check set and read back.
    var stdT = a.GetType("AceRevitMcp.Dashboard.PresentationStandard");
    var std = stdT.GetMethod("Load").Invoke(null, new object[] { null });
    double? Mm(object st, int scale) => (double?)stdT.GetMethod("TextMm").Invoke(st, new object[] { scale });
    Check(Mm(std, 20) == 3.0 && Mm(std, 50) == 3.0 && Mm(std, 75) == 2.5 && Mm(std, 100) == 2.5 && Mm(std, 500) == 2.5, "default: 3 mm at 1:50 and larger, 2.5 mm at 1:100 and smaller");
    stdT.GetMethod("SetType").Invoke(std, new object[] { "3", "text", "ACE Text 3mm" });
    stdT.GetField("Font").SetValue(std, "Arial");
    stdT.GetMethod("Save").Invoke(std, null);
    var back = stdT.GetMethod("Load").Invoke(null, new object[] { null });
    Check((string)stdT.GetMethod("TypeFor").Invoke(back, new object[] { "3", "text" }) == "ACE Text 3mm" && (string)stdT.GetField("Font").GetValue(back) == "Arial", "the chosen type and font are saved in the check set and read back");
    var x2 = Activator.CreateInstance(insT); var list2 = (IList)insT.GetField("Checks").GetValue(x2);
    a.GetType("AceRevitMcp.Dashboard.CheckSet").GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new[] { x2 });
    Check((string)insT.GetField("CheckSetName").GetValue(x2) == "Test office", "saving the presentation standard keeps the rest of the check set");
    var rows = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["upToScale"] = 20, ["textMm"] = 3.5 }, new System.Text.Json.Nodes.JsonObject { ["upToScale"] = 100, ["textMm"] = 2.5 }, new System.Text.Json.Nodes.JsonObject { ["textMm"] = 2 });
    var custom = stdT.GetMethod("Load").Invoke(null, new object[] { rows });
    Check(Mm(custom, 10) == 3.5 && Mm(custom, 50) == 2.5 && Mm(custom, 200) == 2.0, "a project's own sizes override the office ones");
    try { Directory.Delete(tmp, true); } catch { }
    if (failures > 0) { Console.WriteLine($"check set test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("check set test passed");
  }
}
