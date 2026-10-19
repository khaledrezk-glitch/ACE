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
      { ""name"": ""TST_GEN_City Name"", ""group"": ""Project Information"" },
      { ""name"": ""X"", ""group"": ""Doors"" },
      { ""parameter_name"": ""Y"", ""categories"": ""Levels"", ""type_instance"": ""Instance"" } ]").AsArray();
    var pfSkipped = new System.Text.Json.Nodes.JsonArray();
    var parsed = (IList)pfT.GetMethod("ParseRows", flags).Invoke(null, new object[] { pfRows, pfSkipped });
    var rowT = pfT.GetNestedType("Row", flags);
    string Rw(int i, string f) => (string)rowT.GetField(f).GetValue(parsed[i]);
    Check(parsed.Count == 4 && Rw(0, "Group") == "rooms" && Rw(1, "Group") == "elements" && Rw(1, "Level") == "type" && Rw(2, "Name") == "TST_GEN_City Name" && Rw(3, "Group") == "levels",
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
    var sum = (System.Text.Json.Nodes.JsonObject)pfT.GetMethod("Summary", flags).Invoke(null, new object[] { results2, 0 });
    Check((int)sum["rows"] == 5 && (int)sum["complete"] == 1 && (int)sum["withBlanks"] == 2 && (int)sum["missingEverywhere"] == 1 && (int)sum["notText"] == 1 && (int)sum["notInGeneral"] == 1 && (int)sum["noElements"] == 1,
      "summary: complete, with blanks, missing everywhere, not text, not in General, no elements");
    Check((int)sum["withNA"] == 0 && (int)sum["outOfScope"] == 0 && (int)sum["guidDiffers"] == 0 && (int)sum["wrongLevel"] == 0 && (int)sum["foreign"] == 0 && (int)sum["notBound"] == 1,
      "summary of an old-style check: no NA, nothing out of scope, no GUID or level defects; missing everywhere is not bound");

    // ---- parameter requirements (1.10.0): NA, level any, categories, discipline scope, status rules ----
    var naDefault = (System.Collections.Generic.IList<string>)pfT.GetMethod("NaValues", flags).Invoke(null, new object[] { null });
    bool IsNA(string t) => (bool)pfT.GetMethod("IsNA", flags).Invoke(null, new object[] { t, naDefault });
    Check(IsNA("NA") && IsNA(" n/a ") && IsNA("na") && IsNA("N/A") && !IsNA("") && !IsNA(null) && !IsNA("NAME") && !IsNA("N.A.") && !IsNA("-"),
      "without na_values, NA and N/A (trimmed, any case) are declared missing; other text is not");
    var naProject = (System.Collections.Generic.IList<string>)pfT.GetMethod("NaValues", flags).Invoke(null, new object[] { System.Text.Json.Nodes.JsonNode.Parse(@"["" Not Applicable "", """", ""-""]").AsArray() });
    bool IsNAp(string t) => (bool)pfT.GetMethod("IsNA", flags).Invoke(null, new object[] { t, naProject });
    var naEmpty = (System.Collections.Generic.IList<string>)pfT.GetMethod("NaValues", flags).Invoke(null, new object[] { new System.Text.Json.Nodes.JsonArray() });
    Check(naProject.Count == 2 && IsNAp("not applicable") && IsNAp(" - ") && !IsNAp("NA") && !IsNAp("") && naEmpty.Count == 2 && naEmpty.Contains("N/A"),
      "na_values from the request replace the default (trimmed, blanks dropped); an empty list keeps the default");
    Check(Lk("Any") == "any" && Lk(" any ") == "any", "level any is accepted");
    var reqRows = System.Text.Json.Nodes.JsonNode.Parse(@"[
      { ""name"": ""TST_Grade"", ""group"": ""elements"", ""level"": ""any"", ""categories"": [""OST_StructuralColumns"", ""OST_Walls""], ""guid"": "" 11111111-2222-3333-4444-555555555555 "", ""disciplines"": [""ST""], ""key"": ""r1"" },
      { ""name"": ""TST_Grade"", ""group"": ""elements"", ""categories"": [""OST_Walls"", ""OST_StructuralColumns""], ""key"": ""r2"" },
      { ""name"": ""TST_Grade"", ""group"": ""elements"", ""categories"": [""OST_Floors""], ""key"": ""r3"" },
      { ""name"": ""TST_Grade"", ""group"": ""elements"" },
      { ""name"": ""TST_Grade"", ""group"": ""elements"", ""categories"": [] },
      { ""name"": ""TST_Code"", ""group"": ""elements"", ""level"": ""both"", ""key"": ""r6"" } ]").AsArray();
    var reqSkipped = new System.Text.Json.Nodes.JsonArray();
    var req = (IList)pfT.GetMethod("ParseRows", flags).Invoke(null, new object[] { reqRows, reqSkipped });
    object RF(int i, string f) => rowT.GetField(f).GetValue(req[i]);
    Check(req.Count == 3 && (string)RF(0, "Level") == "any" && (string)RF(0, "Key") == "r1" && (string)RF(0, "Guid") == "11111111-2222-3333-4444-555555555555"
      && ((System.Collections.Generic.List<string>)RF(0, "Categories")).Count == 2 && ((System.Collections.Generic.List<string>)RF(0, "Disciplines"))[0] == "ST"
      && (string)RF(1, "Key") == "r3" && ((System.Collections.Generic.List<string>)RF(2, "Categories")).Count == 0,
      "new row fields: categories, guid (trimmed), disciplines, key, level any; no categories means every element of the group");
    Check(reqSkipped.Count == 3 && ((string)reqSkipped[0]["reason"]).StartsWith("duplicate") && (string)reqSkipped[0]["key"] == "r2"
      && ((string)reqSkipped[1]["reason"]).StartsWith("duplicate") && ((string)reqSkipped[2]["reason"]).Contains("unknown type/instance value 'both'"),
      "the key is name + group + sorted categories: the same categories in another order are a duplicate, other categories are not");
    var scopeT = pfT.GetNestedType("Scope", flags);
    var scope = pfT.GetMethod("ParseScope", flags).Invoke(null, new object[] { System.Text.Json.Nodes.JsonNode.Parse(@"{
      ""codes"": [""AR"", ""ST"", ""ME""], ""models"": { ""Tower B"": ""ME"", ""Tower"": ""AR"", ""Blank"": """" },
      ""ownership"": { ""OST_Walls"": [""AR"", ""ST""], ""OST_DuctCurves"": [""ME""], ""OST_Furniture"": [] } }").AsObject() });
    (string, string) Disc(string title, object sc)
    {
      var args = new object[] { title, sc, null };
      var d = (string)pfT.GetMethod("ModelDiscipline", flags).Invoke(null, args);
      return (d, (string)args[2]);
    }
    Check(Disc("P100-XX-st_Main.rvt", scope) == ("ST", "title") && Disc("P100 ar-Main", scope) == ("AR", "title") && Disc("P100-MEP-Main", scope) == (null, null),
      "model discipline from the first title token equal to a code (split on - _ space ., any case); a longer token is not a code");
    Check(Disc("P100-ST-TOWER B", scope) == ("ME", "mapping") && Disc("P100-ST-Tower A", scope) == ("AR", "mapping"),
      "a models entry whose key is part of the title wins over the title code (the longest key first)");
    Check(Disc("Anything", Activator.CreateInstance(scopeT)) == (null, null) && Disc("P1-AR", null) == (null, null), "no scope: discipline unknown");
    bool InScope(string[] ds, string d) => (bool)pfT.GetMethod("InScope", flags).Invoke(null, new object[] { ds, d });
    Check(InScope(new[] { "ST" }, "st") && !InScope(new[] { "ST" }, "AR") && InScope(new string[0], "AR") && InScope(new[] { "ST" }, null),
      "a row naming disciplines is out of scope in another discipline's model; no disciplines or an unknown model discipline: in scope");
    bool Foreign(string cat, string d) => (bool)pfT.GetMethod("IsForeign", flags).Invoke(null, new object[] { scope, cat, d });
    Check(Foreign("OST_DuctCurves", "AR") && !Foreign("OST_DuctCurves", "me") && !Foreign("OST_Walls", "ST") && !Foreign("OST_Furniture", "AR") && !Foreign("OST_Doors", "AR") && !Foreign("OST_DuctCurves", null),
      "foreign: the category has owners and the model's discipline is not one of them (no owners or unknown discipline: judged)");
    bool? Gm(string a, string b) => (bool?)pfT.GetMethod("GuidMatches", flags).Invoke(null, new object[] { a, b });
    Check(Gm("11111111-2222-3333-4444-555555555555", "{11111111-2222-3333-4444-555555555555}") == true && Gm("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555556") == false
      && Gm(null, "11111111-2222-3333-4444-555555555555") == null && Gm("11111111-2222-3333-4444-555555555555", null) == null,
      "GUID match: compared as GUIDs; null without an expected GUID or a shared definition");
    var multi = (IList)pfT.GetMethod("ParseRows", flags).Invoke(null, new object[] { System.Text.Json.Nodes.JsonNode.Parse(@"[
      { ""name"": ""TST_Grade"", ""group"": ""elements"", ""guid"": ""11111111-2222-3333-4444-555555555555"", ""guids"": [""11111111-2222-3333-4444-555555555555"", ""aaaaaaaa-2222-3333-4444-555555555555""] },
      { ""name"": ""TST_Code"", ""group"": ""elements"", ""guids"": [""bbbbbbbb-2222-3333-4444-555555555555""] } ]").AsArray(), new System.Text.Json.Nodes.JsonArray() });
    var mg = (System.Collections.Generic.List<string>)rowT.GetField("Guids").GetValue(multi[0]);
    bool? Gany(object row, string model) => (bool?)pfT.GetMethod("GuidMatchesAny", flags).Invoke(null, new object[] { rowT.GetField("Guids").GetValue(row), model });
    Check(mg.Count == 2 && (string)rowT.GetField("Guid").GetValue(multi[1]) == "bbbbbbbb-2222-3333-4444-555555555555"
      && Gany(multi[0], "AAAAAAAA-2222-3333-4444-555555555555") == true && Gany(multi[0], "11111111-2222-3333-4444-555555555555") == true
      && Gany(multi[0], "cccccccc-2222-3333-4444-555555555555") == false && Gany(multi[0], null) == null && Gany(reqParsedNoGuid(), "cccccccc-2222-3333-4444-555555555555") == null,
      "guids: one name with several client GUIDs matches when the model's GUID is any of them (guid and guids merged, duplicates once)");
    object reqParsedNoGuid() => req[1];
    object R(Action<object> set) { var r = Activator.CreateInstance(resT2); set(r); return r; }
    void F(object r, string f, object v) => resT2.GetField(f).SetValue(r, v);
    string PfStatus(object r) => (string)pfT.GetMethod("Status", flags).Invoke(null, new object[] { r });
    var stOut = R(r => { F(r, "OutOfScope", true); F(r, "Checked", 0); });
    var stUnm = R(r => { F(r, "Unmapped", true); });
    var stNone = R(r => { });
    var stNotBound = R(r => { F(r, "Checked", 5); F(r, "Missing", 5); F(r, "GuidMatch", (bool?)false); });
    var stGuid = R(r => { F(r, "Checked", 5); F(r, "Missing", 1); F(r, "GuidMatch", (bool?)false); });
    var stPart = R(r => { F(r, "Checked", 5); F(r, "Missing", 1); F(r, "Blank", 2); });
    var stUnb = R(r => { F(r, "Checked", 5); F(r, "Filled", 5); F(r, "Unbound", 1); });
    var stBlank = R(r => { F(r, "Checked", 5); F(r, "Blank", 2); F(r, "Na", 1); F(r, "Filled", 2); });
    var stNA = R(r => { F(r, "Checked", 5); F(r, "Na", 1); F(r, "Filled", 4); F(r, "GuidMatch", (bool?)true); F(r, "WrongLevel", (bool?)true); });
    var stDone = R(r => { F(r, "Checked", 5); F(r, "Filled", 5); });
    Check(PfStatus(stOut) == "out-of-scope" && PfStatus(stUnm) == "unmapped" && PfStatus(stNone) == "no-elements" && PfStatus(stNotBound) == "not-bound" && PfStatus(stGuid) == "guid-differs"
      && PfStatus(stPart) == "partly-bound" && PfStatus(stUnb) == "partly-bound" && PfStatus(stBlank) == "blanks" && PfStatus(stNA) == "complete-with-na" && PfStatus(stDone) == "complete",
      "status: out-of-scope, unmapped, no-elements, not-bound, guid-differs, partly-bound (missing or unbound categories), blanks, complete-with-na, complete (first rule wins)");
    var stBuiltPart = R(r => { F(r, "BuiltIn", true); F(r, "Checked", 5); F(r, "Missing", 2); F(r, "Blank", 1); F(r, "Filled", 2); });
    var stBuiltDone = R(r => { F(r, "BuiltIn", true); F(r, "Checked", 5); F(r, "Filled", 5); });
    var stBuiltOut = R(r => { F(r, "BuiltIn", true); F(r, "OutOfScope", true); F(r, "Checked", 0); F(r, "Missing", 0); });
    Check(PfStatus(stBuiltPart) == "not-available" && PfStatus(stBuiltDone) == "complete" && PfStatus(stBuiltOut) == "out-of-scope",
      "a built-in parameter some checked elements lack is not-available (before not-bound / partly-bound); on every element it is judged as usual");
    var resultsB = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(resT2));
    foreach (var r in new[] { stBuiltPart, stBuiltDone, stPart }) { F(r, "Status", PfStatus(r)); resultsB.Add(r); }
    var sumB = (System.Text.Json.Nodes.JsonObject)pfT.GetMethod("Summary", flags).Invoke(null, new object[] { resultsB, 0 });
    Check((int)sumB["notAvailable"] == 1 && (int)sumB["notBound"] == 0, "summary: notAvailable counts built-in rows some elements lack; they are not counted as not bound");
    var results3 = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(resT2));
    foreach (var r in new[] { stOut, stUnm, stNone, stNotBound, stGuid, stPart, stUnb, stBlank, stNA, stDone }) { F(r, "Status", PfStatus(r)); results3.Add(r); }
    var sum3 = (System.Text.Json.Nodes.JsonObject)pfT.GetMethod("Summary", flags).Invoke(null, new object[] { results3, 7 });
    Check((int)sum3["withNA"] == 2 && (int)sum3["outOfScope"] == 1 && (int)sum3["guidDiffers"] == 2 && (int)sum3["notBound"] == 1 && (int)sum3["wrongLevel"] == 1 && (int)sum3["foreign"] == 7
      && (int)sum3["noElements"] == 1 && (int)sum3["complete"] == 2,
      "summary: with NA, out of scope, GUID differs, not bound, wrong level, foreign; out-of-scope and unmapped rows are not 'no elements'; complete agrees with status (complete or complete-with-na)");

    // ---- parameter plan (1.11.0): derive rules, bind entries, version, plain-language reasons, self-check, impact ----
    var ppT = a.GetType("AceRevitMcp.Rules.ParameterPlanLogic");
    System.Text.Json.Nodes.JsonArray JA(string json) => System.Text.Json.Nodes.JsonNode.Parse(json).AsArray();
    var dProblems = new System.Text.Json.Nodes.JsonArray();
    var dRules = (IList)ppT.GetMethod("ParseDerive", flags).Invoke(null, new object[] { JA(@"[
      { ""target"": ""TST_GEN_Site Name"", ""copyFrom"": ""TST_GN_Site Name"" },
      { ""target"": ""TST_Code"", ""join"": [""TST_Zone"", ""TST_Level"", ""TST_Number""], ""separator"": ""-"" },
      { ""copyFrom"": ""TST_A"" },
      { ""target"": ""TST_B"", ""copyFrom"": ""TST_A"", ""join"": [""TST_C""] },
      { ""target"": ""TST_B"" },
      { ""target"": ""TST_B"", ""join"": [] },
      { ""target"": ""TST_B"", ""join"": [""TST_B"", ""TST_C""] } ]"), dProblems });
    var drT = ppT.GetNestedType("DeriveRule", flags);
    Check(dRules.Count == 2 && (string)drT.GetField("CopyFrom").GetValue(dRules[0]) == "TST_GN_Site Name" && (string)drT.GetField("Separator").GetValue(dRules[1]) == "-"
      && (string)drT.GetField("Text").GetValue(dRules[1]) == "TST_Code = TST_Zone + '-' + TST_Level + '-' + TST_Number",
      "derive rules: copyFrom and join with a separator are read, with a text for people");
    Check(dProblems.Count == 5 && ((string)dProblems[0]["reason"]) == "no target parameter" && ((string)dProblems[1]["reason"]).StartsWith("both")
      && ((string)dProblems[2]["reason"]).StartsWith("neither") && ((string)dProblems[3]["reason"]).StartsWith("join needs") && ((string)dProblems[4]["reason"]).Contains("its own sources"),
      "derive rules that cannot be used come back with their reason (no target, both, neither, empty join, target among its sources)");
    var naList = new System.Collections.Generic.List<string> { "NA", "N/A" };
    (string, string, string) Derive(object rule, System.Collections.Generic.Dictionary<string, string> vals)
    {
      var ps = new object[] { rule, (Func<string, string>)(n => vals.TryGetValue(n, out var v) ? v : null), naList, null, null };
      var v = (string)ppT.GetMethod("DeriveValue", flags).Invoke(null, ps);
      return (v, (string)ps[3], (string)ps[4]);
    }
    var dv = new System.Collections.Generic.Dictionary<string, string> { ["TST_GN_Site Name"] = "TST Site", ["TST_Zone"] = "Z1", ["TST_Level"] = "L02", ["TST_Number"] = "" };
    Check(Derive(dRules[0], dv) == ("TST Site", null, null), "copyFrom: the source's value");
    Check(Derive(dRules[1], dv) == (null, "source-blank", "TST_Number"), "join: a blank part means no value (the part is named)");
    dv["TST_Number"] = "007";
    Check(Derive(dRules[1], dv) == ("Z1-L02-007", null, null), "join: all parts, in order, with the separator");
    dv["TST_Level"] = " n/a ";
    Check(Derive(dRules[1], dv) == (null, "source-na", "TST_Level"), "join: a part declared missing (NA) is not a value");
    dv.Remove("TST_Zone");
    Check(Derive(dRules[1], dv) == (null, "source-missing", "TST_Zone"), "join: a part the element does not have is reported");

    var litProblems = new System.Text.Json.Nodes.JsonArray();
    var lit = (IList)ppT.GetMethod("ParseDerive", flags).Invoke(null, new object[] { JA(@"[
      { ""target"": ""TST_Project Status"", ""value"": ""TST Stage 3"" },
      { ""target"": ""TST_A"", ""value"": ""x"", ""copyFrom"": ""TST_B"" },
      { ""target"": ""TST_A"", ""value"": ""  "" } ]"), litProblems });
    Check(lit.Count == 1 && (string)drT.GetField("Value").GetValue(lit[0]) == "TST Stage 3" && (string)drT.GetField("Text").GetValue(lit[0]) == "TST_Project Status = 'TST Stage 3'"
      && Derive(lit[0], new System.Collections.Generic.Dictionary<string, string>()) == ("TST Stage 3", null, null)
      && litProblems.Count == 2 && ((string)litProblems[0]["reason"]).StartsWith("more than one") && ((string)litProblems[1]["reason"]).Contains("non-empty"),
      "derive literal { target, value }: the text itself, no source needed; value with copyFrom/join or an empty value is refused");
    var bProblems = new System.Text.Json.Nodes.JsonArray();
    var bEntries = (IList)ppT.GetMethod("ParseBind", flags).Invoke(null, new object[] { JA(@"[
      { ""name"": ""TST_GEN_Site Name"", ""guid"": ""aaaaaaaa-1111-2222-3333-444444444444"", ""dataType"": ""TEXT"", ""group"": ""project"", ""level"": ""instance"", ""file"": ""C:/TST/shared.txt"" },
      { ""name"": ""TST_ST_Grade"", ""guid"": ""bbbbbbbb-1111-2222-3333-444444444444"", ""group"": ""elements"", ""level"": ""any"", ""categories"": [""OST_Walls"", ""OST_Walls"", ""OST_Floors""], ""file"": ""C:/TST/shared.txt"", ""groupTypeId"": ""IdentityData"" },
      { ""name"": ""TST_NoGuid"", ""group"": ""project"", ""file"": ""C:/TST/shared.txt"" },
      { ""name"": ""TST_NoFile"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"", ""group"": ""project"" },
      { ""name"": ""TST_NoCats"", ""guid"": ""dddddddd-1111-2222-3333-444444444444"", ""group"": ""elements"", ""file"": ""C:/TST/shared.txt"" },
      { ""name"": ""TST_Again"", ""guid"": ""AAAAAAAA-1111-2222-3333-444444444444"", ""group"": ""project"", ""file"": ""C:/TST/shared.txt"" } ]"), bProblems });
    var beT = ppT.GetNestedType("BindEntry", flags);
    object BE(int i, string f) => beT.GetField(f).GetValue(bEntries[i]);
    Check(bEntries.Count == 2 && ((System.Collections.Generic.List<string>)BE(0, "Categories")).Single() == "OST_ProjectInformation" && (string)BE(0, "Level") == "instance"
      && (string)BE(1, "Level") == "instance" && ((System.Collections.Generic.List<string>)BE(1, "Categories")).Count == 2 && (string)BE(1, "GroupTypeId") == "IdentityData"
      && (Guid)BE(0, "Guid") == Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
      "bind entries: Project Information defaults to OST_ProjectInformation, level any binds as instance, categories de-duplicated, group kept");
    Check(bProblems.Count == 4 && ((string)bProblems[0]["reason"]).Contains("never creates a GUID") && ((string)bProblems[1]["reason"]).Contains("only from a file")
      && ((string)bProblems[2]["reason"]).StartsWith("no categories") && ((string)bProblems[3]["reason"]).StartsWith("duplicate"),
      "bind entries without a GUID, without a file, without categories, or with a GUID already listed are refused with the reason");

    bool Ver(string e, string v) => (bool)ppT.GetMethod("VersionMatches", flags).Invoke(null, new object[] { e, v });
    Check(Ver("1.11.0", "1.11.0") && Ver(" v1.11.0 ", "1.11.0") && Ver("1.11.0.0", "1.11.0") && !Ver("1.10.0", "1.11.0") && !Ver("1.11.1", "1.11.0") && !Ver("1.11.0", null),
      "version: the same major.minor.patch matches (a leading v or a fourth part ignored); any other version does not");

    (string, string) Explain(string code, string holder, string detail) => ((string, string))ppT.GetMethod("Explain", flags).Invoke(null, new object[] { code, holder, detail });
    var exB = Explain("borrowed", "TST_User", null); var exC = Explain("central", null, null); var exAnon = Explain("borrowed", null, null);
    Check(exB.Item1 == "borrowed by TST_User" && exB.Item2.Contains("Ask TST_User") && exB.Item2.Contains("relinquish") && exAnon.Item1 == "borrowed by another user"
      && exC.Item2.Contains("Reload Latest") && Explain("source-blank", null, "TST_Zone").Item2.Contains("Fill 'TST_Zone' first") && Explain("version", null, null).Item2.Contains("same ACE version"),
      "block reasons in plain language: who holds the elements and what clears it (borrowed, central, blank source, version)");
    var codes = new[] { "borrowed", "central", "unreadable", "group", "option", "readonly", "nottext", "refused", "source-missing", "source-blank", "source-na", "derived-earlier", "no-target",
      "version", "other-guid", "guid-other-name", "not-in-file", "file", "datatype", "wrong-level", "category", "bind-entry", "derive-rule", "bind-failed", "built-in",
      "file-changed", "group-type", "rebind-entry", "old-missing", "rebind-level", "rebind-datatype", "rebind-held" };
    Check(codes.All(c => { var x = Explain(c, "TST_User", "TST detail"); return !string.IsNullOrWhiteSpace(x.Item1) && !string.IsNullOrWhiteSpace(x.Item2); }),
      "every block and skip code has a why and a how (never silent)");

    var skT = ppT.GetNestedType("Skip", flags);
    var skList = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(skT));
    void Sk(string name, string code, string holder, long id)
    {
      var s = Activator.CreateInstance(skT);
      skT.GetField("Name").SetValue(s, name); skT.GetField("Code").SetValue(s, code); skT.GetField("Holder").SetValue(s, holder); skT.GetField("Id").SetValue(s, id); skList.Add(s);
    }
    Sk("TST_Code", "borrowed", "TST_User", 11); Sk("TST_Code", "borrowed", "TST_User", 12); Sk("TST_Code", "borrowed", "TST_User", 13);
    Sk("TST_Code", "group", null, 14); Sk("TST_Code", "borrowed", "TST_Other", 15);
    var grouped = (System.Text.Json.Nodes.JsonArray)ppT.GetMethod("Group", flags).Invoke(null, new object[] { skList, "to write held", 2 });
    Check(grouped.Count == 3 && (string)grouped[0]["what"] == "TST_Code: 3 elements to write held" && (int)grouped[0]["elements"] == 3 && grouped[0]["sampleIds"].AsArray().Count == 2
      && (string)grouped[0]["holder"] == "TST_User" && ((string)grouped[0]["how"]).Contains("Ask TST_User") && grouped[1]["what"] != null && grouped.Any(g => (string)g["holder"] == null && ((string)g["why"]).Contains("model group"))
      && grouped.All(g => g["why"] != null && g["how"] != null && g["sampleIds"] != null && g.AsObject().ContainsKey("holder")),
      "skips grouped per parameter, reason and holder, largest first: { what, why, elements, sampleIds (max_ids), holder, how }");

    var exT = ppT.GetNestedType("Expectation", flags);
    var exList = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(exT));
    object Ex(string kind, long id, string expected, string actual, bool found = true)
    {
      var x = Activator.CreateInstance(exT);
      exT.GetField("Kind").SetValue(x, kind); exT.GetField("Key").SetValue(x, "r1"); exT.GetField("Name").SetValue(x, "TST_Code"); exT.GetField("Id").SetValue(x, id);
      exT.GetField("Expected").SetValue(x, expected); exT.GetField("Actual").SetValue(x, actual); exT.GetField("Found").SetValue(x, found); exList.Add(x); return x;
    }
    System.Text.Json.Nodes.JsonObject Verify() => (System.Text.Json.Nodes.JsonObject)ppT.GetMethod("Verify", flags).Invoke(null, new object[] { exList, 50 });
    Ex("fill", 21, "NA", "NA"); Ex("derived", 22, "Z1-L02-007", "Z1-L02-007"); Ex("binding", 0, "instance: OST_Floors, OST_Walls", "instance: OST_Floors, OST_Walls");
    var vOk = Verify();
    Check((bool)vOk["ok"] && (int)vOk["checked"] == 3 && vOk["mismatches"].AsArray().Count == 0, "self-check: every planned write shows -> ok");
    Ex("fill", 23, "NA", ""); Ex("binding", 0, "instance: OST_Floors, OST_Walls", "instance: OST_Walls"); Ex("fill", 24, "NA", null, false); Ex("fill", 25, "na", "NA");
    var vBad = Verify();
    var mm = vBad["mismatches"].AsArray();
    Check(!(bool)vBad["ok"] && (int)vBad["mismatchCount"] == 4 && (long)mm[0]["id"] == 23 && mm[1]["id"] == null && ((string)mm[2]["why"]).Contains("could not be read") && (string)mm[3]["actual"] == "NA",
      "self-check: a value that did not stick, a category not bound, a parameter gone and a case change are each a mismatch");

    // The acceptance test's forced mismatch rolls back like a real one.
    exList.Clear();
    Ex("fill", 21, "NA", "NA");
    exList.Add(ppT.GetMethod("ForcedMismatch", flags).Invoke(null, null));
    var vForced = Verify();
    Check(!(bool)vForced["ok"] && (int)vForced["mismatchCount"] == 1 && (string)vForced["mismatches"][0]["kind"] == "forced"
      && (string)vForced["mismatches"][0]["why"] == "forced by test_force_verify_mismatch (acceptance test)",
      "self-check: test_force_verify_mismatch adds one forced mismatch (kind forced), so the group is rolled back");
    Check(Explain("source-blank", null, "TST_Zone").Item2.Contains("blank or NA"), "derive skip text: a later run derives into targets that are blank or NA");

    // Shared parameter files read without Revit (look-only plans never touch the shared parameter file setting), and the preview hash.
    var spfText = "# This is a Revit shared parameter file.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t1\tTST\r\n"
      + "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n"
      + "PARAM\taaaaaaaa-1111-2222-3333-444444444444\tTST_GEN_Site Name\tTEXT\t\t1\t1\t\t1\t0\r\n"
      + "PARAM\tbbbbbbbb-1111-2222-3333-444444444444\tTST_ST_Length\tLENGTH\t\t1\t1\t\t1\t0\r\n";
    var spfReordered = "*PARAM\tNAME\tDATATYPE\tGUID\nPARAM\tTST_Code\tTEXT\tcccccccc-1111-2222-3333-444444444444\nPARAM\tTST_Bad\tTEXT\tnot-a-guid\n";
    var spfT = ppT.GetNestedType("SharedParam", flags);
    IList Spf(byte[] b) => (IList)ppT.GetMethod("ReadSharedParameterFile", flags).Invoke(null, new object[] { b });
    string SpfField(object x, string f) => spfT.GetField(f).GetValue(x)?.ToString();
    var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes(spfText)).ToArray();
    var utf16NoBom = System.Text.Encoding.Unicode.GetBytes(spfText);
    var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(spfText)).ToArray();
    var re = Spf(System.Text.Encoding.UTF8.GetBytes(spfReordered));
    Check(Spf(utf16).Count == 2 && Spf(utf16NoBom).Count == 2 && Spf(utf8).Count == 2 && SpfField(Spf(utf16)[1], "DataType") == "LENGTH" && SpfField(Spf(utf8)[0], "Name") == "TST_GEN_Site Name",
      "shared parameter file: UTF-16 with or without BOM and UTF-8 with BOM read alike");
    Check(re.Count == 1 && SpfField(re[0], "Name") == "TST_Code" && SpfField(re[0], "Guid") == "cccccccc-1111-2222-3333-444444444444",
      "shared parameter file: columns by the *PARAM header (any order); a line without a valid GUID is left out");
    var hash = (string)ppT.GetMethod("FileHash", flags).Invoke(null, new object[] { utf8 });
    bool HashOk(string e, byte[] b) => (bool)ppT.GetMethod("HashMatches", flags).Invoke(null, new object[] { e, b });
    Check(hash.Length == 64 && hash == hash.ToLowerInvariant() && HashOk(hash.ToUpperInvariant(), utf8) && !HashOk(hash, utf16) && HashOk(null, utf16),
      "file hash: sha256 hex (any case); another file differs; no hash sent means not checked");

    // Rebind entries (explicit only).
    var rbProblems = new System.Text.Json.Nodes.JsonArray();
    var rb = (IList)ppT.GetMethod("ParseRebind", flags).Invoke(null, new object[] { JA(@"[
      { ""name"": ""TST_Code"", ""oldGuid"": ""dddddddd-1111-2222-3333-444444444444"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"", ""file"": ""C:/TST/shared.txt"", ""fileHash"": ""ab"", ""categories"": [""OST_Doors""] },
      { ""name"": ""TST_Same"", ""oldGuid"": ""cccccccc-1111-2222-3333-444444444444"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"", ""file"": ""C:/TST/shared.txt"" },
      { ""name"": ""TST_NoOld"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"", ""file"": ""C:/TST/shared.txt"" },
      { ""name"": ""TST_NoFile"", ""oldGuid"": ""eeeeeeee-1111-2222-3333-444444444444"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"" },
      { ""name"": ""TST_Level"", ""oldGuid"": ""ffffffff-1111-2222-3333-444444444444"", ""guid"": ""cccccccc-1111-2222-3333-444444444444"", ""file"": ""C:/TST/shared.txt"", ""level"": ""both"" } ]"), rbProblems });
    var rbT = ppT.GetNestedType("RebindEntry", flags);
    Check(rb.Count == 1 && (string)rbT.GetField("FileHash").GetValue(rb[0]) == "ab" && rbT.GetField("Level").GetValue(rb[0]) == null
      && ((System.Collections.Generic.List<string>)rbT.GetField("Categories").GetValue(rb[0])).Single() == "OST_Doors",
      "rebind entries: old and new GUID, file, hash, extra categories; no level keeps the old binding's level");
    Check(rbProblems.Count == 4 && ((string)rbProblems[0]["reason"]).Contains("same") && ((string)rbProblems[1]["reason"]).Contains("oldGuid")
      && ((string)rbProblems[2]["reason"]).Contains("only from a file") && ((string)rbProblems[3]["reason"]).Contains("unknown level"),
      "rebind entries refused with the reason: same GUIDs, no old GUID, no file, unknown level");

    int Sheets(long[] views, System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>> map, long[] sheets) =>
      (int)ppT.GetMethod("SheetsShowing", flags).Invoke(null, new object[] { views, map, new System.Collections.Generic.HashSet<long>(sheets) });
    var vmap = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>> { [200] = new System.Collections.Generic.List<long> { 100, 101 }, [201] = new System.Collections.Generic.List<long> { 101 } };
    Check(Sheets(new long[] { 100, 100, 102 }, vmap, new long[] { 100, 101, 102 }) == 2 && Sheets(new long[] { 200, 201, 300 }, vmap, new long[] { 100, 101 }) == 2
      && Sheets(new long[] { 100, 200 }, vmap, new long[] { 100, 101 }) == 2 && Sheets(new long[0], vmap, new long[] { 100 }) == 0,
      "impact: title blocks count their sheet, tags the sheets their view is placed on, each sheet once; views on no sheet count nothing");
    bool Proxy(Guid? g, string n) => (bool)ppT.GetMethod("LabelProxy", flags).Invoke(null, new object[] { new System.Collections.Generic.HashSet<Guid> { Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444") }, new System.Collections.Generic.HashSet<string> { "TST_Sheet Title" }, g, n });
    Check(Proxy(Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"), "TST_Other") && Proxy(null, "TST_Sheet Title") && !Proxy(Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"), "TST_Other") && !Proxy(null, "tst_sheet title"),
      "label proxy: the family holds the shared GUID, or a parameter of that exact name");

    // The preview and the apply match only when the plan is identical; the model / version guards and read-only extras do not count.
    var hubT = a.GetType("AceRevitMcp.Companion.ActivityHub");
    string Fp(string json) => (string)hubT.GetMethod("Fingerprint", flags).Invoke(null, new object[] { "parameter_fill", System.Text.Json.Nodes.JsonNode.Parse(json).AsObject() });
    var basePlan = @"""rows"": [{ ""name"": ""TST_Code"", ""group"": ""elements"" }], ""fill"": true, ""value"": ""NA"", ""derive"": [{ ""target"": ""TST_Code"", ""copyFrom"": ""TST_A"" }]";
    var fp0 = Fp("{" + basePlan + "}");
    Check(fp0 == Fp("{" + basePlan + @", ""dry_run"": true, ""expect_model"": ""TST-AR"", ""expect_version"": ""1.11.0"", ""impact"": true, ""register"": true }"),
      "fingerprint: dry_run, expect_model, expect_version, impact and register do not change the plan's identity");
    Check(fp0 != Fp("{" + basePlan.Replace(@"""NA""", @"""N/A""") + "}") && fp0 != Fp("{" + basePlan.Replace("TST_A", "TST_B") + "}")
      && fp0 != Fp("{" + basePlan + @", ""bind"": [{ ""name"": ""TST_Code"" }] }") && fp0 != Fp("{" + basePlan.Replace("TST_Code\", \"group", "TST_Other\", \"group") + "}"),
      "fingerprint: another value, derive rule, binding or row is another plan");

    try { Directory.Delete(tmp, true); } catch { }
    if (failures > 0) { Console.WriteLine($"check set test FAILED ({failures})"); Environment.Exit(1); }
    Console.WriteLine("check set test passed");
  }
}
