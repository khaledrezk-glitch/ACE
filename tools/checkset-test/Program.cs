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
    a.GetType("AceRevitMcp.Dashboard.CheckSet").GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new[] { x, null });
    Check((string)insT.GetField("CheckSetName").GetValue(x) == "Test office", "check set name is read");
    Check(St(inPlace) == "fail" && (string)chkT.GetField("Rule").GetValue(inPlace) == "review at 1, action at 5", "6 in-place families with failAt 5 -> action needed, rule shown");
    Check(!list.Contains(inPlaceOk), "disabled check is removed");
    Check(Pen(warnings) == 5, "maxImpact caps the score impact (matched by name)");
    Check(St(lines) == "ok" && Pen(lines) == 0, "below warnAt -> pass, no score impact");
    Check(St(untouched) == "warn" && Pen(untouched) == 1.5, "checks not in the set keep the defaults");
    // Presentation standard: text height by view scale, saved into the same check set and read back.
    var stdT = a.GetType("AceRevitMcp.Dashboard.PresentationStandard");
    var std = stdT.GetMethod("Load").Invoke(null, new object[] { null, null });
    double? Mm(object st, int scale) => (double?)stdT.GetMethod("TextMm").Invoke(st, new object[] { scale });
    Check(Mm(std, 20) == 3.0 && Mm(std, 50) == 3.0 && Mm(std, 75) == 2.5 && Mm(std, 100) == 2.5 && Mm(std, 500) == 2.5, "default: 3 mm at 1:50 and larger, 2.5 mm at 1:100 and smaller");
    stdT.GetMethod("SetType").Invoke(std, new object[] { "3", "text", "ACE Text 3mm" });
    stdT.GetField("Font").SetValue(std, "Arial");
    stdT.GetMethod("Save").Invoke(std, null);
    var back = stdT.GetMethod("Load").Invoke(null, new object[] { null, null });
    Check((string)stdT.GetMethod("TypeFor").Invoke(back, new object[] { "3", "text" }) == "ACE Text 3mm" && (string)stdT.GetField("Font").GetValue(back) == "Arial", "the chosen type and font are saved in the check set and read back");
    var x2 = Activator.CreateInstance(insT); var list2 = (IList)insT.GetField("Checks").GetValue(x2);
    a.GetType("AceRevitMcp.Dashboard.CheckSet").GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new[] { x2, null });
    Check((string)insT.GetField("CheckSetName").GetValue(x2) == "Test office", "saving the presentation standard keeps the rest of the check set");
    var rows = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["upToScale"] = 20, ["textMm"] = 3.5 }, new System.Text.Json.Nodes.JsonObject { ["upToScale"] = 100, ["textMm"] = 2.5 }, new System.Text.Json.Nodes.JsonObject { ["textMm"] = 2 });
    var custom = stdT.GetMethod("Load").Invoke(null, new object[] { rows, null });
    Check(Mm(custom, 10) == 3.5 && Mm(custom, 50) == 2.5 && Mm(custom, 200) == 2.0, "a project's own sizes override the office ones");

    // ---- Project requirements file (C1): office + project merged key by key, project wins (owner decision D3) ----
    var prT = a.GetType("AceRevitMcp.Rules.ProjectRules");
    var wsT = a.GetType("AceRevitMcp.Rules.WorksetRules");
    object Office() => prT.GetMethod("Office").Invoke(null, null);
    object ForFile(string f) => prT.GetMethod("ForFile").Invoke(null, new object[] { f, null });
    System.Text.Json.Nodes.JsonObject Sec(object r, string s) => (System.Text.Json.Nodes.JsonObject)prT.GetMethod("Section").Invoke(r, new object[] { s });
    string Src(object r, string s) => (string)prT.GetMethod("SourceOf").Invoke(r, new object[] { s });
    string Hash(object r, string s) => (string)prT.GetMethod("HashOf").Invoke(r, new object[] { s });
    object WsLoad(object r) => wsT.GetMethod("Load").Invoke(null, new[] { r });
    var locate = prT.GetMethod("LocateIn");
    string Locate(string[] keys, string[] folders, out string how) { var ps = new object[] { keys, folders, null }; var f = (string)locate.Invoke(null, ps); how = (string)ps[2]; return f; }

    Check(WsLoad(Office()) == null, "no office or project workset rules -> none (never the packaged example; T11)");
    File.WriteAllText(Path.Combine(dir, "worksets.json"), @"{ ""name"": ""Office worksets"", ""default_workset"": ""ARC_General"", ""rules"": [
      { ""id"": ""doors"", ""workset"": ""ARC_Doors"", ""category"": ""Doors"" },
      { ""workset"": ""STR_Frame"", ""category"": ""Structural Framing"" } ] }");
    File.WriteAllText(Path.Combine(dir, "clash-rules.json"), @"{ ""ranks"": { ""Walls"": { ""rank"": 80, ""discipline"": ""ARC"" }, ""Pipes"": { ""rank"": 40, ""discipline"": ""MEP"" } } }");
    var proj = Path.Combine(tmp, "Al Nahda"); Directory.CreateDirectory(Path.Combine(proj, "ACE Vault"));
    var projFile = Path.Combine(proj, "ACE Vault", "project-requirements.json");
    File.WriteAllText(projFile, @"{ ""schema"": ""ace.project-requirements/1"", ""project"": { ""name"": ""Test project"" },
      ""checks"": { ""in-place-families"": { ""failAt"": 10, ""requirement"": ""REQ-EIR-0001"" }, ""Empty sheets"": { ""enabled"": false } },
      ""presentation"": { ""textSizes"": [ { ""upToScale"": 50, ""textMm"": 3.5 }, { ""textMm"": 2.5 } ] },
      ""worksets"": { ""rules"": [ { ""id"": ""doors"", ""workset"": ""A-DOORS"", ""category"": ""Doors"", ""requirement"": ""REQ-BEP-0010"" },
                                { ""workset"": ""M-DUCTS"", ""category"": ""Ducts"" } ] },
      ""clash"": { ""ranks"": { ""Pipes"": { ""rank"": 50, ""discipline"": ""P"" } } } }");

    // Finding the project file: config.json "projects" (cloud model id, central path or title), else beside the model.
    Check(Locate(new[] { "Model A" }, new[] { proj }, out var how1) == projFile && how1 == "beside the model", "found in the model's folder (ACE Vault)");
    Check(Locate(new[] { "Model A" }, new[] { tmp }, out var how2) == null && how2.Contains("office rules only"), "no project file -> office rules only");
    File.WriteAllText(Path.Combine(dir, "config.json"), @"{ ""port"": 48884, ""projects"": { ""3f1c0a2e-0000-0000-0000-000000000001"": """ + proj.Replace("\\", "\\\\") + @""" } }");
    Check(Locate(new[] { "Model A", "3F1C0A2E-0000-0000-0000-000000000001" }, new string[0], out var how3) == projFile && how3.Contains("projects"), "a cloud model is mapped by its id in config.json (D2)");
    var pr = ForFile(projFile);

    // Checks: per key, project wins; the office keeps what the project does not say.
    var checks = Sec(pr, "checks");
    Check((int)checks["in-place-families"]["warnAt"] == 1 && (int)checks["in-place-families"]["failAt"] == 10, "a check merges key by key: office warnAt kept, project failAt wins");
    Check((bool)checks["design-options"]["enabled"] == false && checks["Empty sheets"] != null, "office-only and project-only checks are both kept");
    var x3 = Activator.CreateInstance(insT); var list3 = (IList)insT.GetField("Checks").GetValue(x3);
    var ip3 = Activator.CreateInstance(chkT);
    foreach (var (f, v) in new (string, object)[] { ("Key", "in-place-families"), ("Name", "In-place families"), ("Area", "A"), ("Count", 6), ("Penalty", 6.0), ("MaxPenalty", 8.0) }) chkT.GetField(f).SetValue(ip3, v);
    list3.Add(ip3);
    var es3 = Activator.CreateInstance(chkT);
    foreach (var (f, v) in new (string, object)[] { ("Key", "empty-sheets"), ("Name", "Empty sheets"), ("Area", "A"), ("Count", 3), ("Penalty", 1.5), ("MaxPenalty", 4.0) }) chkT.GetField(f).SetValue(es3, v);
    list3.Add(es3);
    a.GetType("AceRevitMcp.Dashboard.CheckSet").GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).First(m => m.Name == "Apply" && m.GetParameters().Length == 2).Invoke(null, new[] { x3, pr });
    Check(St(ip3) == "warn", "6 in-place families: the project's failAt 10 makes it a warning, not a failure");
    Check(!list3.Contains(es3), "the project switches an office check off (enabled: false)");
    Check(((string)insT.GetField("CheckSetName").GetValue(x3)) == "Test office + Test project" && ((string)insT.GetField("CheckSetSource").GetValue(x3)).Contains("project: "), "the dashboard names both layers");

    // Worksets: project rules first (first match wins), an office rule with the same id is replaced.
    var ws = Sec(pr, "worksets"); var wsRules = (System.Text.Json.Nodes.JsonArray)ws["rules"];
    Check(wsRules.Count == 3 && (string)wsRules[0]["workset"] == "A-DOORS" && (string)wsRules[1]["workset"] == "M-DUCTS" && (string)wsRules[2]["workset"] == "STR_Frame", "project workset rules come first; the office 'doors' rule is replaced, the rest kept");
    Check((string)ws["default_workset"] == "ARC_General", "the office default workset is kept when the project names none");
    var loaded = WsLoad(pr);
    Check(loaded != null && ((IList)wsT.GetField("Items").GetValue(loaded)).Count == 3 && ((string)wsT.GetField("Source").GetValue(loaded)).Contains("project: "), "the model check and assign_worksets load the merged rules");

    // Clash ranks merge; presentation sizes come from the project.
    var ranks = Sec(pr, "clash")["ranks"];
    Check((int)ranks["Pipes"]["rank"] == 50 && (int)ranks["Walls"]["rank"] == 80, "clash ranks merge per category, project wins");
    var pstd = stdT.GetMethod("Load").Invoke(null, new object[] { null, pr });
    Check(Mm(pstd, 50) == 3.5 && (string)stdT.GetField("Font").GetValue(pstd) == "Arial", "presentation: project text sizes, office font kept");
    var officeBefore = File.ReadAllText(Path.Combine(dir, "checkset.json"));
    stdT.GetField("Font").SetValue(pstd, "Calibri");
    var savedTo = (string)stdT.GetMethod("Save").Invoke(pstd, null);
    Check(savedTo == projFile && File.ReadAllText(Path.Combine(dir, "checkset.json")) == officeBefore && File.ReadAllText(projFile).Contains("Calibri") && File.ReadAllText(projFile).Contains("REQ-BEP-0010"),
      "saving a project's presentation standard writes the project file (other sections kept), never the office one");

    // inheritOffice: false replaces the office section; hashes change with the rules.
    var h1 = Hash(pr, "worksets");
    var pj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(projFile)).AsObject();
    pj["worksets"]["inheritOffice"] = false;
    File.WriteAllText(projFile, pj.ToJsonString());
    var pr2 = ForFile(projFile);
    Check(((System.Text.Json.Nodes.JsonArray)Sec(pr2, "worksets")["rules"]).Count == 2 && Sec(pr2, "worksets")["inheritOffice"] == null && Src(pr2, "worksets").StartsWith("project (replaces office)"), "inheritOffice: false -> the project's rules alone");
    Check(Hash(pr2, "worksets") != h1 && Hash(pr2, "checks") == Hash(pr, "checks"), "the rules hash changes only for the section that changed");
    Check(Src(Office(), "worksets").StartsWith("office: ") && Src(ForFile(null), "presentation").StartsWith("office: "), "without a project file the office rules apply alone");
    try { Directory.Delete(tmp, true); } catch { }
    if (failures > 0) { Console.WriteLine($"check set test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("check set test passed");
  }
}
