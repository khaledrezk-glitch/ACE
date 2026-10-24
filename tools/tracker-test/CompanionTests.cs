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

    Deltas();
    Bands();
    if (failures > 0) { Console.WriteLine($"companion test FAILED ({failures})"); Environment.Exit(1); }
  }

  // DM2: the percentage, newly met / newly not met, and the words on Home, the tab header and each card.
  static void Deltas() {
    var before = ComplianceStoreLogic.ParseRun(Record("20261020T151000Z-0001", "2026-10-20T15:10:00Z",
      ("TST-REQ-01", "not-met", 2140, "elements-on-workset1"), ("TST-REQ-02", "met", 0, "levels-and-grids-not-pinned"),
      ("TST-REQ-03", "not-checkable", null, null), ("TST-REQ-04", "partly", 12, "views-named-copy"), ("TST-REQ-05", "not-met", 3, "revit-links-not-pinned")));
    var after = ComplianceStoreLogic.ParseRun(Record("20261020T153000Z-0002", "2026-10-20T15:30:00Z",
      ("TST-REQ-01", "met", 0, "elements-on-workset1"), ("TST-REQ-02", "not-met", 4, "levels-and-grids-not-pinned"),
      ("TST-REQ-03", "not-checkable", null, null), ("TST-REQ-04", "partly", 7, "views-named-copy"), ("TST-REQ-05", "not-met", 3, "revit-links-not-pinned"),
      ("TST-REQ-06", "met", 0, "file-size")));
    Check(ComplianceDeltaLogic.Percent(before) == 25 && ComplianceDeltaLogic.Percent(after) == 40, "percent: met of the checkable (1 of 4, then 2 of 5)");
    Check(ComplianceDeltaLogic.Percent(ComplianceStoreLogic.ParseRun(Record("x", "2026-10-20T15:30:00Z", ("R", "not-checkable", null, null)))) == null, "percent: none checkable gives none");
    var d = ComplianceDeltaLogic.Compare(before, after);
    Check(d.NewlyMet == 1 && d.NewlyNotMet == 1 && d.Net == 1, "delta: one newly met, one newly not met, one more met (a new requirement counts)");
    Check(d.For("TST-REQ-05") == null && d.For("TST-REQ-03") == null && d.For("TST-REQ-06") == null, "delta: unchanged and new requirements have no card line");
    Check(ComplianceDeltaLogic.CardText(d.For("TST-REQ-01")) == "Newly met since the last audit: was NOT MET (2,140), now 0", "card: newly met with the count it had");
    Check(ComplianceDeltaLogic.CardText(d.For("tst-req-02")) == "Newly not met since the last audit: was MET, now NOT MET (4)", "card: newly not met (requirement ids ignore case)");
    Check(ComplianceDeltaLogic.CardText(d.For("TST-REQ-04")) == "Since the last audit: was 12, now 7", "card: same status, fewer failing");
    Check(ComplianceDeltaLogic.Compare(null, after).Changes.Count == 0, "delta: no previous run, no changes");
    var now = new DateTime(2026, 10, 22, 9, 0, 0);   // a Thursday
    Check(ComplianceDeltaLogic.When(new DateTime(2026, 10, 22, 8, 5, 0), now) == "today 08:05", "when: today");
    Check(ComplianceDeltaLogic.When(new DateTime(2026, 10, 20, 17, 10, 0), now) == "Tue 17:10", "when: this week by weekday");
    Check(ComplianceDeltaLogic.When(new DateTime(2026, 10, 1, 17, 10, 0), now) == "1 Oct 17:10", "when: older by date");
    var tue = new DateTime(2026, 10, 20, 17, 10, 0);
    Check(ComplianceDeltaLogic.HeaderText(d, tue, now) == "Since the previous audit (Tue 17:10): 25% to 40% · 1 newly met · 1 newly not met", "tab header delta");
    Check(ComplianceDeltaLogic.HeaderText(ComplianceDeltaLogic.Compare(after, after), tue, now) == "Same result as the previous audit (Tue 17:10)", "tab header: nothing changed");
    Check(ComplianceDeltaLogic.HeaderText(d, null, now) == null, "tab header: no previous run, no line");
    Check(ComplianceDeltaLogic.TileNote(new List<string> { "TST-EIR rev B", "TST-BEP rev 2", "TST-LOIN rev 1" }, tue, now) == "against TST-EIR rev B +2 · last audit Tue 17:10", "Home tile note");
    Check(ComplianceDeltaLogic.TileNote(null, tue, now) == "last audit Tue 17:10", "Home tile note without a vault register");
    Check(ComplianceDeltaLogic.SinceAuditText(tue, now, 0, 9, false, false) == "Since last audit (Tue 17:10): 0 changes, 9 not met", "since last audit line");
    Check(ComplianceDeltaLogic.SinceAuditText(tue, now, 1, 2140, true, true) == "Since last audit (Tue 17:10): 1 change since the model opened, 2,140 not met · the file was saved after this audit: Re-run audit to be sure", "since last audit line for a run read back from disk, file saved since");
    Check(ComplianceDeltaLogic.LocalTime("2026-10-20T15:10:00Z") == new DateTime(2026, 10, 20, 15, 10, 0, DateTimeKind.Utc).ToLocalTime() && ComplianceDeltaLogic.LocalTime("junk") == null, "run time read as local time");
  }

  // DM3: the band after the automatic re-audit.
  static void Bands() {
    JsonObject R(string id, int met, int notMet) => Record(id, "2026-10-20T15:10:00Z",
      Enumerable.Range(1, met).Select(i => ($"TST-M{i}", "met", (int?)0, "k" + i)).Concat(Enumerable.Range(1, notMet).Select(i => ($"TST-N{i}", "not-met", (int?)5, "n" + i))).ToArray());
    var at = new DateTime(2026, 10, 20, 15, 30, 0);
    var b43 = ComplianceStoreLogic.ParseRun(Record("a", "2026-10-20T15:10:00Z", ("TST-1", "met", 0, "k1"), ("TST-2", "not-met", 9, "k2"), ("TST-3", "not-met", 4, "k3"), ("TST-4", "not-met", 1, "k4")));
    var b100 = ComplianceStoreLogic.ParseRun(Record("b", "2026-10-20T15:30:00Z", ("TST-1", "met", 0, "k1"), ("TST-2", "met", 0, "k2"), ("TST-3", "met", 0, "k3"), ("TST-4", "met", 0, "k4")));
    var better = ComplianceDeltaLogic.BandFor(b43, b100, "apply", at);
    Check(better.Text == "3 more requirements met since the last audit (25% to 100%) · 1 undo step (Ctrl+Z)" && better.Tone == "better" && better.RunId == "b", "band: more met after an apply, with the undo step");
    var undo = ComplianceDeltaLogic.BandFor(b100, b43, "undo", at);
    Check(undo.Text == "After undo (Ctrl+Z): 3 fewer requirements met since the last audit (100% to 25%)" && undo.Tone == "worse", "band: fewer met after undo, amber, no undo step");
    var redo = ComplianceDeltaLogic.BandFor(b43, b100, "redo", at);
    Check(redo.Text.StartsWith("After redo (Ctrl+Y): 3 more requirements met") && !redo.Text.Contains("undo step"), "band: redo");
    var one = ComplianceDeltaLogic.BandFor(ComplianceStoreLogic.ParseRun(R("c", 1, 1)), ComplianceStoreLogic.ParseRun(Record("d", "2026-10-20T15:30:00Z", ("TST-M1", "met", 0, "k1"), ("TST-N1", "met", 0, "n1"))), "apply", at);
    Check(one.Text.StartsWith("1 more requirement met since"), "band: one requirement, singular");
    var same = ComplianceDeltaLogic.BandFor(b43, b43, "apply", at);
    Check(same.Text == "Re-audited after your apply: no change in requirements met (25%) · 1 undo step (Ctrl+Z)" && same.Tone == "same", "band: no change");
    var mixed = ComplianceDeltaLogic.BandFor(b43, ComplianceStoreLogic.ParseRun(Record("e", "2026-10-20T15:30:00Z", ("TST-1", "not-met", 2, "k1"), ("TST-2", "met", 0, "k2"), ("TST-3", "met", 0, "k3"), ("TST-4", "not-met", 1, "k4"))), "apply", at);
    Check(mixed.Text.StartsWith("1 more requirement met") && mixed.Text.EndsWith(" · 1 newly not met") && mixed.Tone == "worse", "band: net better but one newly not met is said, amber");
    var first = ComplianceDeltaLogic.BandFor(null, b100, "apply", at);
    Check(first.Text == "Re-audited after your apply: 4 requirements met (100%) · 1 undo step (Ctrl+Z)", "band: no earlier run");
    var failed = ComplianceDeltaLogic.Failed("apply", "Revit is busy.", at);
    Check(failed.Text == "Re-audit after your apply failed: Revit is busy. The change itself stands; press Re-run audit." && failed.Tone == "failed" && failed.RunId == null, "band: failure says so, the change stands");
    Check(ComplianceDeltaLogic.Skipped("undo", 41.6, at).Text == "Not re-audited after the undo: the model check took 42 s last time. Press Re-run audit when ready.", "band: too slow to repeat by itself");
  }
}
