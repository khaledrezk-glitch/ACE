// The "no new warnings" rule (G24, no Revit): warning keys, the before / after diff, named acceptance, the dry-run line
// and the targets of a warning-fix run.
using System; using System.Collections.Generic; using System.Linq; using AceRevitMcp.Util;
static class WarningTests {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + "warnings: " + what); if (!ok) failures++; }
  const string Overlap = "6e1efefe-c8e0-483d-8482-150b9f1da21a", Dup = "b4176cef-6086-45a8-a066-c3fd424c9412";
  static WarningItem W(string g, string t, long[] f, long[] a = null) => new WarningItem(g, t, f, a);
  static List<WarningItem> L(params WarningItem[] w) => w.ToList();
  public static void Run() {
    var wallsOverlap = W(Overlap, "Highlighted walls overlap.", new long[] { 12, 10 });
    var dup = W(Dup, "There are identical instances in the same place.", new long[] { 30, 31 });
    // Keys: GUID + sorted failing + sorted additional; ids in any order are the same warning; failing vs additional differ.
    Check(WarningLogic.Key(wallsOverlap) == WarningLogic.Key(W(Overlap.ToUpperInvariant(), "other text", new long[] { 10, 12 })), "key: GUID (any case) + sorted ids, the description is not part of it");
    Check(WarningLogic.Key(W(Overlap, "x", new long[] { 10 }, new long[] { 12 })) != WarningLogic.Key(W(Overlap, "x", new long[] { 10, 12 })), "key: failing and additional ids are kept apart");
    Check(WarningLogic.Key(W(null, "a", new long[] { 1 })) != WarningLogic.Key(W(null, "b", new long[] { 1 })), "key: no GUID falls back to the description");
    var before = L(wallsOverlap, dup);
    // Nothing new: the same warnings after, also re-posted during the change (ModelGuard deleted them): not counted twice.
    var same = WarningLogic.Compare(before, L(wallsOverlap, dup), L(W(Overlap, "Highlighted walls overlap.", new long[] { 10, 12 })), null, false);
    Check(same.Count == 0 && !same.Refuse && same.After == 2 && WarningLogic.Line(same, false) == "New warnings: 0.", "re-posted existing warning is not new; after = 2");
    // A cleared warning is fine.
    Check(WarningLogic.Compare(before, L(dup), null, null, false).Count == 0, "a cleared warning is fine");
    // New: posted only (deleted by ModelGuard, so not in the model list), and one on new elements in the model.
    var room = W("aaaaaaaa-0000-0000-0000-000000000001", "Room is not in a properly enclosed region.", new long[] { 99 });
    var walls2 = W(Overlap, "Highlighted walls overlap.", new long[] { 10, 40 });
    var fresh = WarningLogic.Compare(before, L(wallsOverlap, dup, walls2), L(room), null, false);
    Check(fresh.Count == 2 && fresh.Refuse && fresh.Refused == 2 && fresh.After == 4, "two new warnings (one posted only, one in the model), the apply refuses");
    var dry = WarningLogic.Line(fresh, true);
    Check(dry.StartsWith("Would add 2 new warnings: ") && dry.Contains("(elements: 10, 40)") && dry.Contains("(elements: 99)") && dry.Contains("The identical apply refuses (new-warnings)") && dry.Contains("accept_warnings"), "dry-run line: would add N, elements, refusal and how to accept");
    Check(WarningLogic.RefusalError(fresh).StartsWith("Rolled back: the change would add 2 new Revit warnings (") && WarningLogic.RefusalError(fresh).EndsWith("Nothing was changed."), "refusal error text");
    // The same warning twice more than before: counted as times.
    var twice = WarningLogic.Compare(L(room), L(room, room, room), null, null, false);
    Check(twice.Count == 2 && twice.New.Count == 1 && twice.New[0].Times == 2 && WarningLogic.Line(twice, false).Contains(" x2"), "a key that rises by two: one item, times 2");
    // Accepting: by GUID (braces, any case), by part of the description, all; a too short text never matches.
    var g = WarningLogic.Compare(before, L(wallsOverlap, dup, walls2), L(room), new List<string> { "{" + Overlap.ToUpperInvariant() + "}" }, false);
    Check(g.Count == 2 && g.Refused == 1 && g.Refuse && g.New.Single(n => n.Item == walls2).Accepted, "accept by GUID: that one accepted, the other still refuses");
    var t = WarningLogic.Compare(before, L(wallsOverlap, dup, walls2), L(room), new List<string> { "walls overlap", "properly enclosed" }, false);
    Check(t.Count == 2 && !t.Refuse && WarningLogic.Line(t, false).Contains("Accepted by this call (2)"), "accept by description parts: no refusal, the line says accepted");
    Check(!WarningLogic.Matches(room, "ro") && !WarningLogic.Matches(room, "  ") && WarningLogic.Matches(room, "ROOM IS"), "a text under 4 characters or blank never matches; case ignored");
    Check(!WarningLogic.Compare(before, L(walls2), L(room), null, true).Refuse, "accept_new_warnings: all accepted");
    // A warning that only lost elements (same GUID, ids a subset of one before) is the old one shrunk, not new.
    var three = W(Dup, "There are identical instances in the same place.", new long[] { 30, 31, 32 });
    Check(WarningLogic.Compare(L(three), L(W(Dup, "There are identical instances in the same place.", new long[] { 31, 30 })), null, null, false).Count == 0, "a warning that lost an element is not new");
    Check(WarningLogic.Compare(L(three), L(W(Overlap, "Highlighted walls overlap.", new long[] { 30, 31 })), null, null, false).Count == 1, "same ids under another definition is new");
    // JSON: capped list, counts, the line.
    var many = Enumerable.Range(1, 30).Select(i => W(Overlap, "Highlighted walls overlap.", new long[] { 1000 + i, 2000 + i })).ToList();
    var big = WarningLogic.ToJson(WarningLogic.Compare(new List<WarningItem>(), many, null, null, false), true);
    Check((int)big["count"] == 30 && big["items"].AsArray().Count == WarningLogic.MaxListed && (int)big["more"] == 10 && (bool)big["refuse"] && big["line"].ToString().Contains("25 more"), "JSON: 30 new, 20 listed, 10 more; the line lists 5");
    // Warning-fix runs: the named warnings the run touched must be gone.
    var dupB = W(Dup, "There are identical instances in the same place.", new long[] { 50, 51 });
    var named = new List<string> { "identical instances" };
    var gone = WarningLogic.Targeted(L(dup, dupB, wallsOverlap), L(wallsOverlap), null, named, new long[] { 31, 51 });
    Check(gone.Ok && gone.Named == 2 && gone.Targeted.Count == 2 && gone.Remaining.Count == 0 && WarningLogic.Line(gone, false) == "Targeted warnings: all 2 are gone.", "fix run: both targets gone");
    var part = WarningLogic.Targeted(L(dup, dupB), L(dupB), null, named, new long[] { 31 });
    Check(part.Ok && part.Targeted.Count == 1 && WarningLogic.Line(part, true).Contains("1 named warning not touched"), "fix run: an untouched named warning is reported, not a failure");
    var left = WarningLogic.Targeted(L(three), new List<WarningItem>(), L(W(Dup, "There are identical instances in the same place.", new long[] { 30, 31 })), named, new long[] { 32 });
    Check(!left.Ok && left.Remaining.Count == 1 && WarningLogic.Line(left, false).StartsWith("Targeted warnings: 1 of 1 remain: "), "fix run: a target that only shrank remains (posted), the run fails");
    var none = WarningLogic.Targeted(L(dup), L(dup), null, named, new long[] { 77 });
    Check(!none.Ok && WarningLogic.Line(none, false).Contains("touched none"), "fix run: named warnings exist but the run touched none: fails");
    Check(WarningLogic.Targeted(L(wallsOverlap), L(wallsOverlap), null, named, new long[0]).Ok, "fix run: no named warning in the model: ok (nothing to fix)");
    // The clash solver's own key (description + ids) through the shared counting.
    var c = WarningLogic.Fresh(new List<string> { "a", "b" }, new List<string> { "a" }, new List<string> { "b", "b", "c" }, s => s, out var total);
    Check(total == 4 && c.Count == 2 && c.Any(x => x.Item == "b" && x.Extra == 1) && c.Any(x => x.Item == "c" && x.Extra == 1), "shared counting: max of model and posted per key, rises only");
    if (failures > 0) { Console.WriteLine($"warning test FAILED ({failures})"); Environment.Exit(1); }
  }
}
