// Workset after-check test (no Revit needed): dotnet run --project tools/workset-test (after the add-in build)
// Register #37 (an apply said done while elements stayed on Workset1), #40 (unused types read as deleted elements) and #42 (deletion probe).
using System; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Text.Json.Nodes;
class P {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }
  const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
  static void Main() {
    var a = Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "AceRevitMcp.dll"));
    var L = a.GetType("AceRevitMcp.Rules.WorksetPlanLogic");
    object Call(string m, params object[] args) => L.GetMethod(m, S).Invoke(null, args);

    // The office method: category, type-name prefix, level.
    bool Pre(string type, params string[] p) => (bool)Call("TypePrefixMatches", type, p, null);
    Check(Pre("TST Beam : SB1 300x600", "SB") && Pre("SB1", "sb") && !Pre("TST Beam : GB1 250x500", "SB") && Pre("TST Beam : GB1", "TST Beam") && Pre("anything"), "type-name prefix: the type part or the whole name, ignoring case; no prefix matches all");
    var sel = JsonNode.Parse("{\"category\":[\"Structural Framing\"],\"level\":[\"ST-B1(BASEMENT)\"],\"typePrefix\":[\"SB\"],\"material\":\"concrete\"}").AsObject();
    bool Sel(string c, string t, string l, string m) => (bool)Call("SelectorMatches", sel, c, t, l, m);
    Check(Sel("Structural Framing", "TST Beam : SB1 300x600", "ST-B1(BASEMENT)", "concrete"), "selector: category + level + type prefix + material");
    Check(!Sel("Structural Framing", "TST Beam : GB1", "ST-B1(BASEMENT)", "concrete") && !Sel("Structural Framing", "TST Beam : SB1", "ST-GF", "concrete") && !Sel("Walls", "TST Beam : SB1", "ST-B1(BASEMENT)", "concrete"), "selector: another prefix, level or category does not match");
    Check(Sel("Structural Framing", "TST Beam : SB1", "ST-B1(BASEMENT)", "other") && !Sel("Structural Framing", "TST Beam : SB1", "ST-B1(BASEMENT)", "steel"), "selector: an unknown material is no evidence, a known other one rules out");
    Check(!(bool)Call("SelectorMatches", JsonNode.Parse("{\"level\":[\"L1\"]}").AsObject(), "Walls", "x", "L1", "other"), "a category-free selector claims nothing");
    var targets = JsonNode.Parse("[{\"workset\":\"TST-ST-STRAP BEAMS\",\"selectors\":[{\"category\":[\"Structural Framing\"]}]},{\"workset\":\"TST-ST-GROUND BEAMS\",\"selectors\":[{\"category\":[\"Structural Framing\"],\"typePrefix\":[\"GB\"]}]}]").AsArray();
    var to = (List<string>)Call("TargetsOf", targets, "Structural Framing", "TST Beam : GB1", "", "concrete");
    Check(to.Count == 2 && to.Contains("TST-ST-STRAP BEAMS") && to.Contains("TST-ST-GROUND BEAMS"), "an element matches every target whose selector takes it");
    var names = new Dictionary<long, string> { [1] = "Workset1", [2] = "TST-ST-STRAP BEAMS ", [3] = "TST-ST-GROUND BEAMS", [4] = "TST-ST-OTHER" };
    Check((long)Call("WorksetIdOf", names, "tst-st-strap beams") == 2 && (long)Call("WorksetIdOf", names, "TST-ST-NONE") == 0, "a target is found by name (case and outer spaces do not matter)");

    // The #37 case: the plan moved nothing for the beams (a tie), the targets stay empty, 127 beams stay on Workset1.
    var tl = new List<(string, long, IDictionary<string, int>)> { ("TST-ST-STRAP BEAMS", 2, new Dictionary<string, int>()), ("TST-ST-GROUND BEAMS", 3, new Dictionary<string, int>()), ("TST-ST-SOG", 0, new Dictionary<string, int> { ["Floors"] = 17 }) };
    var els = new List<(long, long, string, string, string, IList<string>)>();
    for (var i = 0; i < 94; i++) els.Add((100 + i, 1, "Structural Framing", "TST Beam : SB1 300x600", "k-sb", new List<string> { "TST-ST-STRAP BEAMS" }));
    for (var i = 0; i < 33; i++) els.Add((300 + i, 1, "Structural Framing", "TST Beam : GB1 250x500", "k-gb", new List<string> { "TST-ST-GROUND BEAMS" }));
    els.Add((900, 1, "Generic Models", "TST Thing : A", "k-x", new List<string>()));
    var src = new HashSet<long> { 1 }; var dflt = new HashSet<long> { 1 };
    var skips = new Dictionary<long, string> { [100] = "borrowed by jane", [101] = "borrowed by jane" };
    var ac = (JsonObject)Call("AfterCheck", tl, els, names, src, dflt, skips, new HashSet<string>(), false);
    JsonObject T(JsonObject r, string w) => r["targets"].AsArray().OfType<JsonObject>().First(t => t["workset"].ToString() == w);
    Check(!(bool)ac["ok"] && (int)ac["failures"] == 3, "the after-check fails: two beam targets left empty, one target missing");
    var sb = T(ac, "TST-ST-STRAP BEAMS");
    Check((string)sb["code"] == "target-empty" && (int)sb["leftOnSource"] == 92 && (int)sb["skipped"] == 2 && (int)sb["onTarget"] == 0, "STRAP BEAMS: target-empty, 92 left on Workset1, 2 borrowed skipped (not failed)");
    Check((int)sb["byCategory"]["Structural Framing"]["leftOnSource"] == 92, "counted per category");
    Check((string)T(ac, "TST-ST-SOG")["code"] == "target-missing" && (int)T(ac, "TST-ST-SOG")["planned"] == 17, "a target that does not exist but was to receive 17: target-missing");
    var skipJ = ac["skips"].AsArray().OfType<JsonObject>().Single();
    Check((string)skipJ["reason"] == "borrowed by jane" && (int)skipJ["count"] == 2, "skips are listed once per element with their reason");
    var sod = ac["stillOnDefault"].AsObject();
    Check((int)sod["total"] == 128 && (string)sod["workset"] == "Workset1" && (int)sod["byCategory"]["Structural Framing"] == 127 && (int)sod["byCategory"]["Generic Models"] == 1, "still on Workset1, by category");
    Check(sod["byType"].AsArray().OfType<JsonObject>().First()["type"].ToString() == "TST Beam : SB1 300x600" && (int)sod["byType"][0]["elements"] == 94, "still on Workset1, by category and type (largest first)");

    // After a good apply: everything on its target; a kept group (excluded by decision) and one elsewhere do not fail.
    var els2 = els.Select(e => e.Item3 == "Structural Framing" ? (e.Item1, e.Item5 == "k-sb" ? 2L : 3L, e.Item3, e.Item4, e.Item5, e.Item6) : e).ToList();
    els2.Add((950, 1, "Structural Framing", "TST Beam : SB9", "k-kept", new List<string> { "TST-ST-STRAP BEAMS" }));
    els2.Add((951, 4, "Structural Framing", "TST Beam : SB8", "k-zone", new List<string> { "TST-ST-STRAP BEAMS" }));
    var tl2 = new List<(string, long, IDictionary<string, int>)> { ("TST-ST-STRAP BEAMS", 2, new Dictionary<string, int> { ["Structural Framing"] = 94 }), ("TST-ST-GROUND BEAMS", 3, new Dictionary<string, int> { ["Structural Framing"] = 33 }) };
    var ok = (JsonObject)Call("AfterCheck", tl2, els2, names, src, dflt, new Dictionary<long, string>(), new HashSet<string> { "k-kept" }, false);
    Check((bool)ok["ok"] && (int)T(ok, "TST-ST-STRAP BEAMS")["onTarget"] == 94 && (int)T(ok, "TST-ST-STRAP BEAMS")["excluded"] == 1 && (int)T(ok, "TST-ST-STRAP BEAMS")["onOther"] == 1, "a done apply: on target 94, the kept group excluded, one elsewhere reported, ok");
    // Planned N, none arrived: target-empty even with nothing left on the source.
    var tl3 = new List<(string, long, IDictionary<string, int>)> { ("TST-ST-GROUND BEAMS", 3, new Dictionary<string, int> { ["Structural Framing"] = 5 }) };
    var e3 = new List<(long, long, string, string, string, IList<string>)> { (1, 4, "Structural Framing", "x", "k", new List<string> { "TST-ST-GROUND BEAMS" }) };
    var r3 = (JsonObject)Call("AfterCheck", tl3, e3, names, src, dflt, new Dictionary<long, string>(), null, true);
    Check((string)T(r3, "TST-ST-GROUND BEAMS")["code"] == "target-empty" && (bool)r3["preview"], "planned 5, none on the target: target-empty (a dry run's forecast too)");
    // Some moved, some left: left-behind.
    var e4 = new List<(long, long, string, string, string, IList<string>)> { (1, 3, "Structural Framing", "x", "k", new List<string> { "TST-ST-GROUND BEAMS" }), (2, 1, "Structural Framing", "x", "k", new List<string> { "TST-ST-GROUND BEAMS" }) };
    var r4 = (JsonObject)Call("AfterCheck", tl3, e4, names, src, dflt, new Dictionary<long, string>(), null, false);
    Check((string)T(r4, "TST-ST-GROUND BEAMS")["code"] == "left-behind" && T(r4, "TST-ST-GROUND BEAMS")["why"].ToString().Contains("1 Structural Framing"), "some moved, one left on Workset1: left-behind, per category");

    // The verdict: an apply whose after-check failed is NOT done (stage incomplete), the change stays (no rollback here).
    var applied = new JsonObject { ["success"] = true, ["rolledBack"] = false, ["afterCheck"] = ac.DeepClone(), ["notes"] = new JsonArray() };
    Check(!(bool)Call("MarkComplete", applied, false) && (string)applied["stage"] == "incomplete" && (bool)applied["complete"] == false && applied["error"].ToString().StartsWith("Applied as ONE undo step, but NOT done: the after-check found 3 target(s) not done") && (bool)applied["success"], "a failed after-check: NOT done, stage incomplete, still ONE undo step (success = committed)");
    var clean = new JsonObject { ["success"] = true, ["rolledBack"] = false, ["afterCheck"] = ok.DeepClone() };
    Check((bool)Call("MarkComplete", clean, false) && (bool)clean["complete"] && clean["stage"] == null, "a clean after-check: complete");
    var dry = new JsonObject { ["success"] = true, ["rolledBack"] = false, ["afterCheck"] = ac.DeepClone(), ["notes"] = new JsonArray() };
    Call("MarkComplete", dry, true);
    Check(dry["stage"] == null && dry["notes"].AsArray().Count == 1 && dry["notes"][0].ToString().StartsWith("After-check forecast: the apply would leave work undone on 3 target(s)"), "a dry run: the forecast is a note, the preview stays appliable");
    var rolled = new JsonObject { ["success"] = false, ["rolledBack"] = true, ["afterCheck"] = ac.DeepClone() };
    Check(!(bool)Call("MarkComplete", rolled, false) && rolled["stage"] == null, "a rolled-back run is not marked (nothing changed)");

    // Register #40: deleted types apart from deleted elements, with their instance count.
    var rec = a.GetType("AceRevitMcp.Util.ChangeTracker+Recording");
    var dl = rec.GetMethod("DeletionLines", S);
    List<string> Lines(params (string, string, bool, int?)[] d) => (List<string>)dl.Invoke(null, new object[] { d.ToList(), 6 });
    var ten = Enumerable.Range(1, 10).Select(i => ("Structural Framing", "TST Beam : 300x600", true, (int?)0)).ToArray();
    var l1 = Lines(ten);
    Check(l1.Count == 1 && l1[0] == "Removes 10 unused Structural Framing types (0 instances; no placed element is deleted): TST Beam : 300x600", l1.FirstOrDefault() ?? "(none)");
    var l2 = Lines(("Walls", "Basic Wall : 200", false, null), ("Walls", "Basic Wall : 200", false, null), ("Structural Framing", "TST Beam : A", true, 3));
    Check(l2.Count == 2 && l2[0] == "Deletes 2 Walls elements: Basic Wall : 200" && l2[1] == "Deletes 1 Structural Framing type with 3 instances: TST Beam : A", string.Join(" | ", l2));
    var l3 = Lines(("Types", null, true, null));
    Check(l3.Single() == "Removes 1 type (instance count not known)", l3.Single());
    var tt = rec.GetMethod("TypesText", S);
    Check((string)tt.Invoke(null, new object[] { JsonNode.Parse("{\"deletedTypes\":10,\"deletedTypeInstances\":0}").AsObject() }) == ", removes 10 unused types"
      && (string)tt.Invoke(null, new object[] { JsonNode.Parse("{\"deleted\":2}").AsObject() }) == "", "a card line names removed types apart");

    // Register #42: the deletion probe (bulk first, halves only where something else goes, capped) and its gateP.
    var DP = a.GetType("AceRevitMcp.Util.DeletionProbeLogic");
    object F(object o, string n) => o.GetType().GetField(n).GetValue(o);
    // A fake model: what Revit deletes with each id; 99 is refused alone; deletes are counted.
    var deps = new Dictionary<long, long[]> { [3] = new long[] { 100 }, [6] = new long[] { 200 }, [5] = new long[] { 4 } };
    var probeCalls = 0;
    Func<List<long>, ValueTuple<ICollection<long>, string>> fake = chunk => {
      probeCalls++;
      if (chunk.Contains(99)) return (null, "TST cannot delete");
      var gone = new HashSet<long>(chunk); foreach (var i in chunk) if (deps.TryGetValue(i, out var d)) gone.UnionWith(d);
      return (gone, null);
    };
    object Probe(IEnumerable<long> c, int max, IEnumerable<long> notOthers = null, bool bulk = false) { probeCalls = 0; return DP.GetMethod("Probe", S).Invoke(null, new object[] { c, fake, max, notOthers, bulk }); }
    string Status(object r, long id) => (string)F(((System.Collections.IEnumerable)F(r, "Outcomes")).Cast<object>().First(o => (long)F(o, "Id") == id), "Status");
    List<long> Others(object r, long id) => (List<long>)F(((System.Collections.IEnumerable)F(r, "Outcomes")).Cast<object>().First(o => (long)F(o, "Id") == id), "Others");
    var cleanP = Probe(new long[] { 1, 2, 4, 7, 8 }, 200);
    Check(probeCalls == 1 && (int)F(cleanP, "Deletes") == 1 && new long[] { 1, 2, 4, 7, 8 }.All(i => Status(cleanP, i) == "only-target"), "probe: a clean list costs ONE bulk delete, every candidate only the target");
    var mixed = Probe(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 200);
    Check(Status(mixed, 3) == "takes-others" && Others(mixed, 3).SequenceEqual(new long[] { 100 }) && Status(mixed, 6) == "takes-others" && Others(mixed, 6).SequenceEqual(new long[] { 200 }), "probe: the candidates that take others are found with exactly what they take");
    Check(new long[] { 1, 2, 4, 5, 7, 8 }.All(i => Status(mixed, i) == "only-target") && probeCalls < 8 + 8, "probe: taking another candidate is not 'others' (5 takes 4); halves, not one delete per id up front");
    var capped = Probe(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2);
    Check((bool)F(capped, "Capped") && (int)F(capped, "Deletes") == 2 && new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }.Count(i => Status(capped, i) == "not-probed") > 0, "probe: the cap stops it, the rest is not probed");
    var refused = Probe(new long[] { 1, 99 }, 200);
    Check(Status(refused, 99) == "refused" && Status(refused, 1) == "only-target", "probe: a candidate Revit refuses alone is 'refused', the others still classified");
    var nother = Probe(new long[] { 3 }, 200, new long[] { 100 });
    Check(Status(nother, 3) == "only-target", "probe: ids named not-others (the rest of a purge pass) never count as others");
    Probe(new long[] { 1, 2, 3, 4 }, 200, null, true);
    var withBulk = probeCalls;
    Probe(new long[] { 1, 2, 3, 4 }, 200, null, false);
    Check(withBulk == probeCalls - 1, "probe: a bulk delete the caller already saw take others is not repeated (starts with the halves)");
    var gateP = DP.GetMethod("Gate", S).Invoke(null, new object[] { mixed, new HashSet<long> { 3 } });
    var fwd = (List<long>)gateP.GetType().GetField("Item1").GetValue(gateP);
    var heldP = ((System.Collections.IEnumerable)gateP.GetType().GetField("Item2").GetValue(gateP)).Cast<object>().Select(o => (long)F(o, "Id")).ToList();
    Check(fwd.SequenceEqual(new long[] { 1, 2, 3, 4, 5, 7, 8 }) && heldP.SequenceEqual(new long[] { 6 }), "gate: only the target goes; a candidate that takes others goes only when the call names it (3), else held (6)");
    var gatePCap = DP.GetMethod("Gate", S).Invoke(null, new object[] { capped, new HashSet<long> { 1, 2, 3, 4, 5, 6, 7, 8 } });
    var heldPCap = ((System.Collections.IEnumerable)gatePCap.GetType().GetField("Item2").GetValue(gatePCap)).Cast<object>().Count(o => (string)F(o, "Status") == "not-probed");
    Check(heldPCap > 0, "gate: not probed (the cap) is held even when named: never deleted unseen");
    var sum = (string)DP.GetMethod("Summary", S).Invoke(null, new object[] { mixed, "allow_takes_others", new HashSet<long> { 3 } });
    Check(sum == "Deletion probe: 6 of 8 delete only themselves; 2 take 2 other elements with them (1 named in allow_takes_others goes ahead; 1 held: name it in allow_takes_others to delete it anyway).", sum);
    Check((string)DP.GetMethod("Summary", S).Invoke(null, new object[] { cleanP, null, null }) == "Deletion probe: all 5 delete only themselves.", "summary: a clean probe in one line");
    var capSum = (string)DP.GetMethod("Summary", S).Invoke(null, new object[] { capped, null, null });
    Check(capSum.Contains("not probed") && capSum.EndsWith("The probe stopped at its cap of 2 deletes (each rolled back)."), capSum);
    // The others in the change summary's #40 wording: elements apart from types, a type with its instance count.
    var o3 = ((System.Collections.IEnumerable)F(mixed, "Outcomes")).Cast<object>().First(o => (long)F(o, "Id") == 6);
    var info = new Dictionary<long, (string, string, bool, int?)> { [200] = ("Furniture", "TST Chair : 600", true, 0) };
    var lineP = (string)DP.GetMethod("OthersLine", S).Invoke(null, new object[] { o3, "Furniture", "TST Chair : 600", info });
    Check(lineP == "id 6 (Furniture: TST Chair : 600) takes 1 other with it: removes 1 unused Furniture type (0 instances; no placed element is deleted): TST Chair : 600", lineP);
    var o1 = ((System.Collections.IEnumerable)F(mixed, "Outcomes")).Cast<object>().First(o => (long)F(o, "Id") == 3);
    var lineP2 = (string)DP.GetMethod("OthersLine", S).Invoke(null, new object[] { o1, "Furniture", null, new Dictionary<long, (string, string, bool, int?)> { [100] = ("Furniture Tags", "TST Tag", false, null) } });
    Check(lineP2 == "id 3 (Furniture) takes 1 other with it: deletes 1 Furniture Tags element: TST Tag", lineP2);

    Console.WriteLine(failures == 0 ? "workset test passed" : $"workset test FAILED ({failures})");
    Environment.Exit(failures == 0 ? 0 : 1);
  }
}
