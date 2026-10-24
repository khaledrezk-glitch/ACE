// ace_self_test's pure rules (G26, register #43): the open-document fingerprint comparison, the one-line results, the
// overall line, the report text and the start refusal. SelfTestLogic is internal: reached by reflection, no Revit needed.
using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection;
static class SelfTestTests {
  static Type L;
  static object Call(string name, params object[] args) => L.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, args);
  static void Ok(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + "self-test: " + what); if (!ok) { Console.WriteLine("tracker test FAILED (self-test)"); Environment.Exit(1); } }
  static object Print(string key, string title, bool modified, long elements, long types, string hash) {
    var t = L.GetNestedType("DocPrint", BindingFlags.NonPublic | BindingFlags.Public); var p = Activator.CreateInstance(t);
    foreach (var (n, v) in new (string, object)[] { ("Key", key), ("Title", title), ("IsModified", modified), ("Elements", elements), ("Types", types), ("TypeHash", hash) }) t.GetField(n).SetValue(p, v);
    return p; }
  static IList Prints(params object[] ps) { var t = L.GetNestedType("DocPrint", BindingFlags.NonPublic | BindingFlags.Public); var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(t)); foreach (var p in ps) list.Add(p); return list; }
  static object Result(string tool, string status, string reason, double seconds) {
    var t = L.GetNestedType("ToolResult", BindingFlags.NonPublic | BindingFlags.Public); var r = Activator.CreateInstance(t);
    foreach (var (n, v) in new (string, object)[] { ("Tool", tool), ("Status", status), ("Reason", reason), ("Seconds", seconds) }) t.GetField(n).SetValue(r, v);
    return r; }
  static IList Results(params object[] rs) { var t = L.GetNestedType("ToolResult", BindingFlags.NonPublic | BindingFlags.Public); var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(t)); foreach (var r in rs) list.Add(r); return list; }
  static List<string> Compare(IList a, IList b) => ((IEnumerable<string>)Call("Compare", a, b)).ToList();

  public static void Run(Assembly a) {
    L = a.GetType("AceRevitMcp.Commands.SelfTestLogic");
    Ok(L != null, "SelfTestLogic is in the add-in");

    // The type list hash: order-free, any added, removed or renamed type changes it.
    string Hash(params string[] lines) => (string)Call("TypeHash", (IEnumerable<string>)lines);
    Ok(Hash("1|Wall A", "2|Floor B") == Hash("2|Floor B", "1|Wall A"), "type hash ignores order");
    Ok(Hash("1|Wall A", "2|Floor B") != Hash("1|Wall A", "2|Floor C"), "a renamed type changes the hash");
    Ok(Hash("1|Wall A") != Hash("1|Wall A", "3|New"), "an added type changes the hash");

    // The comparison: unchanged is empty; each kind of change is one line naming the model.
    var h = Hash("1|Wall A", "2|Floor B");
    var before = Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 100, 2, h), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x")));
    Ok(Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 100, 2, h), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x")))).Count == 0, "same fingerprints: no change");
    Ok(Compare(before, Prints(Print("c:/tst/tst_a.rvt", "TST_A", false, 100, 2, h), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x")))).Count == 0, "paths match without regard to case");
    var mod = Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", true, 100, 2, h), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x"))));
    Ok(mod.Count == 1 && mod[0] == "open model TST_A changed: IsModified no -> yes", "IsModified flipped: " + string.Join(" | ", mod));
    var cnt = Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 101, 3, Hash("1|Wall A", "2|Floor B", "3|New")), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x"))));
    Ok(cnt.Count == 1 && cnt[0] == "open model TST_A changed: elements 100 -> 101; element types 2 -> 3", "counts changed: " + string.Join(" | ", cnt));
    var ren = Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 100, 2, Hash("1|Wall A", "2|Floor C")), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x"))));
    Ok(ren.Count == 1 && ren[0] == "open model TST_A changed: element type list differs (same count)", "a renamed type at the same count: " + string.Join(" | ", ren));
    var unk = Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 100, 2, null), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x"))));
    Ok(unk.Count == 1 && unk[0].EndsWith("element type list not readable"), "an unreadable type list is a change, never a pass");
    var unread = Compare(Prints(Print("TST_C", "TST_C", false, -1, -1, null)), Prints(Print("TST_C", "TST_C", false, -1, -1, null)));
    Ok(unread.Count == 1 && unread[0] == "open model TST_C could not be read: not proven unchanged", "a model unreadable before and after is not proven unchanged");
    var gone = Compare(before, Prints(Print("TST_B", "TST_B", true, 5, 1, Hash("9|x"))));
    Ok(gone.Count == 1 && gone[0] == "open model TST_A is no longer open", "a closed model is reported");
    var extra = Compare(before, Prints(Print("C:/TST/TST_A.rvt", "TST_A", false, 100, 2, h), Print("TST_B", "TST_B", true, 5, 1, Hash("9|x")), Print("C:/Temp/TST_ACE_SelfTest_central.rvt", "TST_ACE_SelfTest_central", false, 1, 1, h)));
    Ok(extra.Count == 1 && extra[0] == "a document is open that was not before the test: TST_ACE_SelfTest_central", "a document left open (the blank one) is reported");
    Ok(Compare(Prints(), Prints()).Count == 0, "nothing open before and after: no change");

    // One line per tool, the overall line.
    string Line(object r) => (string)Call("Line", r);
    Ok(Line(Result("workset apply", "PASS", null, 0.84)) == "PASS workset apply (0.8 s)", "PASS line with seconds");
    Ok(Line(Result("parameter fill", "FAIL", "the dry run failed:\n  blocked", 1.2)) == "FAIL parameter fill: the dry run failed: blocked", "FAIL line: reason on one line, no time");
    Ok(Line(Result("purge unused", "FAIL", null, 0)) == "FAIL purge unused: no reason given", "FAIL without a reason says so");
    Ok(Line(Result("save as central", "SKIP", "needs save as", 0)) == "SKIP save as central: needs save as", "SKIP line");
    Ok(Line(Result("x", "weird", "r", 0)) == "FAIL x: r", "an unknown status is a FAIL");
    Ok(Line(Result("long", "FAIL", new string('a', 400), 0)).Length == "FAIL long: ".Length + 300 && Line(Result("long", "FAIL", new string('a', 400), 0)).EndsWith("..."), "a long reason is cut at 300 characters");
    string Overall(IList rs, double s) => (string)Call("Overall", rs, s);
    bool Passed(IList rs) => (bool)Call("Passed", rs);
    var allPass = Results(Result("a", "PASS", null, 1), Result("b", "PASS", null, 2));
    Ok(Overall(allPass, 3.04) == "OVERALL PASS: 2 of 2 tools passed (3.0 s)" && Passed(allPass), "overall PASS");
    var oneFail = Results(Result("a", "PASS", null, 1), Result("b", "FAIL", "x", 2), Result("c", "SKIP", "y", 0));
    Ok(Overall(oneFail, 12.34) == "OVERALL FAIL: 1 of 3 tools failed, 1 skipped (12.3 s)" && !Passed(oneFail), "overall FAIL counts failures and skips");
    var skipOnly = Results(Result("a", "PASS", null, 1), Result("c", "SKIP", "y", 0));
    Ok(Overall(skipOnly, 1) == "OVERALL PASS: 1 of 2 tools passed, 1 skipped (1.0 s)" && Passed(skipOnly), "a skip does not fail the run");
    Ok(Overall(Results(), 0) == "OVERALL FAIL: no tool ran (0.0 s)" && !Passed(Results()), "no results is a FAIL");

    // The report text and its file name.
    var text = (string)Call("Report", new[] { "ACE self-test", "ACE 1.15.0 in Revit 2025" }, oneFail, 12.34, new[] { "note one", " " });
    var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
    Ok(lines[0] == "ACE self-test" && lines[2] == "" && lines[3] == "PASS a (1.0 s)" && lines[4] == "FAIL b: x" && lines[5] == "SKIP c: y" && lines[6].StartsWith("OVERALL FAIL") && text.Contains("Notes:\r\n- note one\r\n") && !text.Contains("-  \r\n"), "report: header, lines, overall, notes");
    Ok((string)Call("FileName", new DateTime(2026, 10, 24, 15, 30, 12)) == "self-test-2026-10-24_153012.txt", "report file name");

    // Never starts while another ACE command runs, a transaction is open, or a self-test runs.
    string Refusal(string cmd, string[] open, bool running) => (string)Call("Refusal", cmd, (IEnumerable<string>)open, running);
    Ok(Refusal(null, new string[0], false) == null && Refusal("ace_self_test", new string[0], false) == null, "starts when idle (also as the bridge's own command)");
    Ok(Refusal("purge_unused", new string[0], false)?.Contains("'purge_unused'") == true, "refused while another ACE command runs");
    Ok(Refusal(null, new[] { "TST_A" }, false)?.Contains("TST_A") == true, "refused while a transaction is open, naming the model");
    Ok(Refusal(null, new string[0], true)?.Contains("already running") == true, "refused while a self-test runs");
  }
}
