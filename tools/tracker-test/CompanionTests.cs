// The Companion's persisted compliance state (DM1, no Revit): file names a reset script can match, the store round
// trip (ids, check keys, previous run), vault run records read back, and broken or newer files ignored.
using System; using System.Collections.Generic; using System.Linq; using System.Text.Json.Nodes; using AceRevitMcp.Rules;
static class CompanionTests {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + "companion: " + what); if (!ok) failures++; }

  // A run record as Compliance.ToJson writes it (the vault's 04 Audits/<model>/runs/<id>.json), optionally with ids.
  internal static JsonObject Record(string id, string time, params (string Req, string Status, int? Count, string Key)[] rs) {
    var checks = new JsonArray(rs.Where(r => r.Key != null).GroupBy(r => r.Key).Select(g => (JsonNode)new JsonObject {
      ["key"] = g.Key, ["name"] = g.Key, ["area"] = "Model", ["count"] = g.First().Count ?? 0, ["status"] = (g.First().Count ?? 0) == 0 ? "ok" : "fail" }).ToArray());
    var results = new JsonArray(rs.Select(r => (JsonNode)new JsonObject {
      ["requirement"] = r.Req, ["status"] = r.Status, ["severity"] = "major", ["kind"] = "worksets", ["count"] = r.Count,
      ["binding"] = r.Key == null ? null : "C1:checks/" + r.Key, ["evidence"] = r.Key == null ? "no ACE check is bound" : r.Key + ": " + r.Count,
      ["doc"] = "TST-EIR B", ["at"] = "clause 4.1", ["text"] = "No model element shall remain on Workset1." }).ToArray());
    return new JsonObject { ["run"] = new JsonObject { ["id"] = id, ["model"] = "TST_Model", ["engine"] = "model-check", ["time"] = time, ["score"] = 71, ["vault"] = "C:\\TST\\ACE Vault",
      ["rulesHash"] = "abc", ["requirementsHash"] = "def", ["aceVersion"] = "9.9.9" }, ["checks"] = checks, ["summary"] = new JsonObject { ["met"] = 1 }, ["results"] = results };
  }

  public static void Run() {
    // File names: start with the model's file name without .rvt (a reset script deletes "<name>__*.json").
    Check(ComplianceStoreLogic.BaseName("TST_Tower", @"D:\Demo\TST_Tower.rvt") == "TST_Tower", "base name: the file name without folder and .rvt");
    Check(ComplianceStoreLogic.BaseName("Example Model", null) == "Example Model", "base name: the title when the model was never saved");
    Check(ComplianceStoreLogic.BaseName("x", "/tmp/a:b.rvt") == "a_b", "base name: characters not allowed in a file name are replaced");
    var f1 = ComplianceStoreLogic.FileName("TST_Tower", "TST_Tower|D:\\Demo\\TST_Tower.rvt");
    var f2 = ComplianceStoreLogic.FileName("TST_Tower", "TST_Tower|D:\\Other\\TST_Tower.rvt");
    Check(f1.StartsWith("TST_Tower__") && f1.EndsWith(".json") && f1.Length == "TST_Tower__".Length + 8 + 5, "file name: <base>__<8 hex>.json");
    Check(f1 != f2, "file name: two copies of one file in different folders keep their own state");
    Check(f1 == ComplianceStoreLogic.FileName("TST_Tower", "tst_tower|d:\\demo\\tst_tower.rvt"), "file name: the key ignores case (Windows paths)");

    // A vault run record reads back: results, check keys from the binding, checks with their status.
    var rec = Record("20261020T081000Z-ab12", "2026-10-20T08:10:00Z", ("TST-REQ-01", "not-met", 2140, "elements-on-workset1"), ("TST-REQ-02", "met", 0, "levels-and-grids-not-pinned"), ("TST-REQ-03", "not-checkable", null, null));
    var run = ComplianceStoreLogic.ParseRun(rec);
    Check(run != null && run.Id == "20261020T081000Z-ab12" && run.Time == "2026-10-20T08:10:00Z" && run.Score == 71 && run.Vault == "C:\\TST\\ACE Vault", "run record: head read back");
    Check(run.Results.Count == 3 && run.Results[0].CheckKeys.SequenceEqual(new[] { "elements-on-workset1" }) && run.Results[2].CheckKeys.Count == 0, "run record: check keys from the binding");
    Check(run.Results[0].Count == 2140 && run.Results[0].Where == "clause 4.1" && run.Results[0].Doc == "TST-EIR B", "run record: count, place and document");
    Check(run.Summary["met"] == 1, "run record: summary");
    Check(ComplianceStoreLogic.ParseRun(new JsonObject { ["results"] = new JsonArray() }) == null, "not a run: null");

    // The store file: last + previous, ids kept (capped), check keys kept; round trip.
    rec["checks"][0]["ids"] = new JsonArray(Enumerable.Range(1, 6000).Select(i => (JsonNode)(long)i).ToArray());
    var withIds = ComplianceStoreLogic.ParseRun(rec);
    var prev = ComplianceStoreLogic.ParseRun(Record("20261019T170000Z-0001", "2026-10-19T17:00:00Z", ("TST-REQ-01", "not-met", 2500, "elements-on-workset1")));
    var file = ComplianceStoreLogic.Serialize(withIds, prev, "TST_Tower", new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
    Check(file["schema"]?.ToString() == "ace.companion-state/1" && file["savedAt"]?.ToString() == "2026-10-20T09:00:00Z", "store file: schema and time");
    var ids = file["last"]["checks"][0]["ids"] as JsonArray;
    Check(ids != null && ids.Count == ComplianceStoreLogic.MaxIdsPerCheck && (int)file["last"]["checks"][0]["idsCapped"] == 6000, "store file: failing ids capped at 5000 per check, the full count noted");
    var back = ComplianceStoreLogic.Parse(JsonNode.Parse(file.ToJsonString()) as JsonObject);
    Check(back.Last?.Id == withIds.Id && back.Previous?.Id == prev.Id, "store file: last and previous read back");
    Check(back.Last.Results.First(r => r.Requirement == "TST-REQ-01").CheckKeys.SequenceEqual(new[] { "elements-on-workset1" }), "store file: check keys read back");
    var again = ComplianceStoreLogic.RunJson(back.Last);
    Check((again["checks"][0]["ids"] as JsonArray)?.Count == ComplianceStoreLogic.MaxIdsPerCheck, "store file: ids survive a second round trip (Show in model after a reload)");
    Check(ComplianceStoreLogic.Parse(null).Last == null && ComplianceStoreLogic.Parse(new JsonObject { ["last"] = "broken" }).Last == null, "broken file: ignored");
    Check(ComplianceStoreLogic.Parse(new JsonObject { ["version"] = 99, ["last"] = rec }).Last == null, "a newer build's file: ignored, not misread");
    Check(ComplianceStoreLogic.Serialize(withIds, null, "m", DateTime.UtcNow)["previous"] == null, "no previous run: null");

    // Vault fallback: the two newest records by name (ids start with the UTC time).
    var newest = ComplianceStoreLogic.NewestRecords(new[] { "r/20261019T170000Z-0001.json", "r/20261020T081000Z-ab12.json", "r/20261018T090000Z-ffff.json", "r/notes.txt" });
    Check(newest.SequenceEqual(new[] { "r/20261020T081000Z-ab12.json", "r/20261019T170000Z-0001.json" }), "vault fallback: the two newest run records, newest first");

    if (failures > 0) { Console.WriteLine($"companion test FAILED ({failures})"); Environment.Exit(1); }
  }
}
