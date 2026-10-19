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
      ""checks"": { ""about"": ""a note for people (the Al Nahda file has one)"", ""in-place-families"": { ""failAt"": 10, ""requirement"": ""REQ-EIR-0001"" }, ""Empty sheets"": { ""enabled"": false } },
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

    // Compliance per requirement (C1+): bindings from the rules and the records, status scale D5, deviations, run records.
    var compT = a.GetType("AceRevitMcp.Rules.Compliance");
    var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    IList Checks(params (string Key, int Count, int? Of)[] cs)
    {
      var l = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(chkT));
      foreach (var (k, n, of) in cs) { var c = Activator.CreateInstance(chkT); chkT.GetField("Key").SetValue(c, k); chkT.GetField("Name").SetValue(c, k); chkT.GetField("Count").SetValue(c, n); chkT.GetField("Of").SetValue(c, of); l.Add(c); }
      return l;
    }
    var vaultDir = Path.GetDirectoryName(projFile);
    File.WriteAllText(Path.Combine(vaultDir, "requirements.json"), @"{ ""records"": [
      { ""id"": ""REQ-EIR-0001"", ""severity"": { ""value"": ""error"" }, ""source"": { ""quote_sha256"": ""q1"" } },
      { ""id"": ""REQ-BEP-0010"", ""severity"": ""warning"" },
      { ""id"": ""REQ-LOIN-0003"", ""test"": { ""binding"": ""C1:checks/views-not-on-sheets"" } },
      { ""id"": ""REQ-EIR-0009"", ""test"": { ""checkability"": ""M"" } },
      { ""id"": ""REQ-EIR-0010"", ""test"": { ""binding"": ""C1:checks/no-such-check"" } } ] }");
    File.WriteAllText(Path.Combine(vaultDir, "decisions.json"), @"{ ""decisions"": [ { ""id"": ""DEC-0001"", ""kind"": ""deviation"", ""requirements"": [ ""REQ-EIR-0001"" ], ""quotes"": { ""REQ-EIR-0001"": ""q1"" }, ""note"": ""agreed with the client"" },
      { ""id"": ""DEC-0002"", ""kind"": ""note"", ""status"": ""decided"", ""requirements"": [ ""REQ-LOIN-0003"" ], ""label"": ""25 mm in force"" },
      { ""id"": ""DEC-0003"", ""kind"": ""note"", ""status"": ""open"", ""requirements"": [ ""REQ-BEP-0010"" ], ""label"": ""still open"" } ] }");
    var cks = Checks(("in-place-families", 2, null), ("elements-on-the-wrong-workset-bep", 3, 100), ("views-not-on-sheets", 0, 40));
    var forModel = compT.GetMethod("ForModel", flags);
    var run = forModel.Invoke(null, new object[] { pr2, cks, "Model A", "model-a", "tester", 72 });
    var runT = run.GetType(); var resT = compT.GetNestedType("Result");
    var results = ((IList)runT.GetField("Results").GetValue(run)).Cast<object>().ToDictionary(r => (string)resT.GetField("Requirement").GetValue(r), r => r);
    string Cs(string id) => (string)resT.GetField("Status").GetValue(results[id]);
    string Rf(string id, string f) => (string)resT.GetField(f).GetValue(results[id]);
    Check(Cs("REQ-EIR-0001") == "not-met" && Rf("REQ-EIR-0001", "Severity") == "error", "a failing check with no measured set -> not-met, severity kept from the record");
    Check(Cs("REQ-BEP-0010") == "partly" && Rf("REQ-BEP-0010", "Binding") == "C1:checks/elements-on-the-wrong-workset-bep", "a workset rule's requirement binds to the BEP workset check; 3 of 100 wrong -> partly");
    Check(Cs("REQ-LOIN-0003") == "met", "a record's own test.binding works; count 0 -> met");
    Check(Rf("REQ-EIR-0001", "Doc") == "" && ((System.Collections.Generic.List<string>)resT.GetField("CheckKeys").GetValue(results["REQ-BEP-0010"]))[0] == "elements-on-the-wrong-workset-bep",
      "results carry their source and bound check keys (the Companion selects the failing elements with them)");
    Check(Cs("REQ-EIR-0009") == "not-checkable" && Rf("REQ-EIR-0009", "Evidence").StartsWith("manual"), "a manual requirement is not-checkable, marked manual");
    Check(Cs("REQ-EIR-0010") == "not-checkable" && Rf("REQ-EIR-0010", "Evidence").Contains("did not run"), "a bound check that did not run -> not-checkable");
    Check(Rf("REQ-EIR-0001", "Decision")?.Contains("accepted deviation: agreed with the client") == true, "an accepted deviation is shown beside not-met, not turned into met");
    Check(Rf("REQ-LOIN-0003", "Decision") == "DEC-0002 (decided: 25 mm in force)" && Rf("REQ-BEP-0010", "Decision") == null,
      "a decided note shows beside the status (met included); an open note does not");
    var runsDir = Path.Combine(vaultDir, "04 Audits", "model-a", "runs");
    Check(!Directory.Exists(runsDir), "no run record is written into a vault the user has not trusted (D21)");
    File.WriteAllText(Path.Combine(dir, "trusted-vaults.json"), "[" + System.Text.Json.JsonSerializer.Serialize(vaultDir) + "]");
    var run2 = forModel.Invoke(null, new object[] { pr2, cks, "Model A", "model-a", "tester", 72 });
    Check(Directory.Exists(runsDir) && Directory.GetFiles(runsDir, "*.json").Length == 1 && File.ReadAllText(Directory.GetFiles(runsDir)[0]).Contains("REQ-BEP-0010"), "a trusted vault gets the run record in 04 Audits/<model>/runs");
    var rec = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Directory.GetFiles(runsDir)[0]));
    Check((int)rec["run"]["score"] == 72 && ((System.Text.Json.Nodes.JsonArray)rec["checks"]).Count == 3 && (string)rec["checks"][1]["key"] == "elements-on-the-wrong-workset-bep" && (int)rec["checks"][1]["of"] == 100,
      "the run record carries the model check (score and every check) for the master report");
    File.WriteAllText(Path.Combine(dir, "trusted-vaults.json"), "[\"\", null, " + System.Text.Json.JsonSerializer.Serialize(vaultDir) + "]");
    Check((bool)compT.GetMethod("Trusted", flags).Invoke(null, new object[] { vaultDir }), "a bad entry in the trust list does not untrust the other vaults");
    var beside = Path.Combine(tmp, "Beside"); Directory.CreateDirectory(beside);
    File.WriteAllText(Path.Combine(beside, "project-requirements.json"), @"{ ""schema"": ""ace.project-requirements/1"" }");
    Check((string)compT.GetMethod("VaultOf").Invoke(null, new[] { ForFile(Path.Combine(beside, "project-requirements.json")) }) == Path.Combine(beside, "ACE Vault")
      && (string)compT.GetMethod("VaultOf").Invoke(null, new[] { pr2 }) == vaultDir, "the vault is the ACE Vault folder beside a project file (as the server's vault tool), or the file's own ACE Vault folder");
    var outOfDate = compT.GetMethod("OutOfDate", flags);
    Check(outOfDate.Invoke(null, new object[] { run2, pr2 }) == null, "a fresh run is not out of date");
    File.WriteAllText(Path.Combine(vaultDir, "requirements.json"), File.ReadAllText(Path.Combine(vaultDir, "requirements.json")).Replace("REQ-EIR-0010", "REQ-EIR-0011"));
    Check(((string)outOfDate.Invoke(null, new object[] { run2, pr2 }))?.Contains("requirements") == true, "changed requirements make the stored run out of date");
    var reqNow = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(vaultDir, "requirements.json"))).AsObject();
    reqNow["records"][0]["source"]["quote_sha256"] = "q2";
    var run3 = compT.GetMethod("Evaluate", flags).Invoke(null, new object[] { pr2, reqNow, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(vaultDir, "decisions.json"))).AsObject(), cks, "Model A", "tester" });
    var r3 = ((IList)runT.GetField("Results").GetValue(run3)).Cast<object>().First(r => (string)resT.GetField("Requirement").GetValue(r) == "REQ-EIR-0001");
    Check(((string)resT.GetField("Decision").GetValue(r3)).Contains("confirm again"), "a deviation made against an earlier clause text asks to be confirmed again (D19)");

    // A measured check (file size, count = MB) follows its pass rule: under failAt -> met, at or over -> not-met.
    var sizeReq = System.Text.Json.Nodes.JsonNode.Parse(@"{ ""records"": [ { ""id"": ""REQ-SIZE"", ""test"": { ""binding"": ""C1:checks/file-size"" } } ] }").AsObject();
    string SizeStatus(int mb, string status)
    {
      var l = Checks(("file-size", mb, null)); chkT.GetField("Unit").SetValue(l[0], "MB"); chkT.GetField("StatusOverride").SetValue(l[0], status);
      var rr = compT.GetMethod("Evaluate", flags).Invoke(null, new object[] { pr2, sizeReq, null, l, "Model A", "tester" });
      var res = ((IList)runT.GetField("Results").GetValue(rr)).Cast<object>().First(r => (string)resT.GetField("Requirement").GetValue(r) == "REQ-SIZE");
      return (string)resT.GetField("Status").GetValue(res) + "|" + (string)resT.GetField("Evidence").GetValue(res);
    }
    Check(SizeStatus(280, "warn").StartsWith("met|") && SizeStatus(280, "warn").Contains("280 MB"), "a 280 MB model under a 300 MB limit is met (review noted), not 280 failing items");
    Check(SizeStatus(320, "fail").StartsWith("not-met|"), "a model over the size limit is not met");

    // ---- parameter_fill (completeness from a parameter list): groups, type/instance, blank rules, row de-duplication, summary ----
    var pfT = a.GetType("AceRevitMcp.Rules.ParameterFillLogic");
    string Gk(string v) => (string)pfT.GetMethod("GroupKey", flags).Invoke(null, new object[] { v });
    string Lk(string v) => (string)pfT.GetMethod("LevelKey", flags).Invoke(null, new object[] { v });
    bool Blank(bool isText, bool hasValue, string text) => (bool)pfT.GetMethod("IsBlank", flags).Invoke(null, new object[] { isText, hasValue, text });
    Check(Gk("Project Information") == "project" && Gk("Rooms, Areas, Spaces") == "rooms" && Gk("  levels ") == "levels" && Gk("Systems,  Components, Elements") == "elements" && Gk("elements") == "elements",
      "list category values (and group keys) map to project / rooms / levels / elements");
    Check(Gk("Doors") == null && Gk("") == null && Gk(null) == null, "an unknown category value is not guessed");
    Check(Lk("Type") == "type" && Lk("Instance") == "instance" && Lk("") == "instance" && Lk(null) == "instance" && Lk("both") == null, "Type reads the type; Instance or blank the element; anything else is unknown");
    Check(Blank(true, true, "") && Blank(true, true, "   ") && Blank(true, false, null) && !Blank(true, true, "NA"), "text: empty or whitespace is blank, NA is filled");
    Check(Blank(false, false, null) && !Blank(false, true, null), "numbers, Yes/No, ids: blank only without a value (0 counts as filled)");
    var pfRows = System.Text.Json.Nodes.JsonNode.Parse(@"[
      { ""name"": ""Area"", ""group"": ""rooms"" },
      { ""name"": ""Area"", ""group"": ""Systems, Components, Elements"", ""level"": ""Type"" },
      { ""name"": ""Area"", ""group"": ""Rooms, Areas, Spaces"", ""level"": ""Type"" },
      { ""name"": "" "", ""group"": ""project"" },
      { ""name"": ""MOD_GEN_City Name"", ""group"": ""Project Information"" },
      { ""name"": ""X"", ""group"": ""Doors"" },
      { ""parameter_name"": ""Y"", ""categories"": ""Levels"", ""type_instance"": ""Instance"" } ]").AsArray();
    var pfSkipped = new System.Text.Json.Nodes.JsonArray();
    var parsed = (IList)pfT.GetMethod("ParseRows", flags).Invoke(null, new object[] { pfRows, pfSkipped });
    var rowT = pfT.GetNestedType("Row", flags);
    string Rw(int i, string f) => (string)rowT.GetField(f).GetValue(parsed[i]);
    Check(parsed.Count == 4 && Rw(0, "Group") == "rooms" && Rw(1, "Group") == "elements" && Rw(1, "Level") == "type" && Rw(2, "Name") == "MOD_GEN_City Name" && Rw(3, "Group") == "levels",
      "the key is name + group: Area in two groups is two rows; the CSV column names are accepted too");
    Check(pfSkipped.Count == 3 && ((string)pfSkipped[0]["reason"]).StartsWith("duplicate") && ((string)pfSkipped[1]["reason"]) == "no parameter name" && ((string)pfSkipped[2]["reason"]).Contains("unknown category group 'Doors'"),
      "a duplicate name + group (first wins), a nameless row and an unknown category come back as skipped rows");
    var resT2 = pfT.GetNestedType("RowResult", flags);
    var results2 = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(resT2));
    void Res(int chk, int miss, int blank, int filled, string st, bool? gen)
    {
      var r = Activator.CreateInstance(resT2);
      resT2.GetField("Checked").SetValue(r, chk); resT2.GetField("Missing").SetValue(r, miss); resT2.GetField("Blank").SetValue(r, blank);
      resT2.GetField("Filled").SetValue(r, filled); resT2.GetField("StorageType").SetValue(r, st); resT2.GetField("InGeneral").SetValue(r, gen); results2.Add(r);
    }
    Res(1, 0, 0, 1, "String", true);    // complete
    Res(10, 2, 3, 5, "String", null);   // blanks (and some missing)
    Res(4, 4, 0, 0, null, null);        // missing everywhere
    Res(3, 0, 1, 2, "Double", false);   // not text, not in General
    Res(0, 0, 0, 0, null, null);        // no elements of the group
    var sum = (System.Text.Json.Nodes.JsonObject)pfT.GetMethod("Summary", flags).Invoke(null, new object[] { results2 });
    Check((int)sum["rows"] == 5 && (int)sum["complete"] == 1 && (int)sum["withBlanks"] == 2 && (int)sum["missingEverywhere"] == 1 && (int)sum["notText"] == 1 && (int)sum["notInGeneral"] == 1 && (int)sum["noElements"] == 1,
      "summary: complete, with blanks, missing everywhere, not text, not in General, no elements");

    try { Directory.Delete(tmp, true); } catch { }
    if (failures > 0) { Console.WriteLine($"check set test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("check set test passed");
  }
}
