// ACE loader payload selection (no Revit): current.json, folder containment, hash check, previous fallback, versions.
using System; using System.IO; using AceRevitMcp.Loader;
static class LoaderTests {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + "loader: " + what); if (!ok) failures++; }
  public static void Run() {
    var root = Path.Combine(Path.GetTempPath(), "ace-loader-" + Guid.NewGuid().ToString("N"));
    var good = Path.Combine(root, "payload", "1.13.4-aaaaaaaa"); var old = Path.Combine(root, "payload", "1.13.3-bbbbbbbb");
    Directory.CreateDirectory(good); Directory.CreateDirectory(old);
    File.WriteAllText(Path.Combine(good, "AceRevitMcp.dll"), "abc");
    File.WriteAllText(Path.Combine(old, "AceRevitMcp.dll"), "old build");
    const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // SHA-256 of "abc"
    try {
      Check(PayloadSelection.Sha256Of(Path.Combine(good, "AceRevitMcp.dll")) == abc, "SHA-256 of the payload file (lower-case hex)");
      var oldSha = PayloadSelection.Sha256Of(Path.Combine(old, "AceRevitMcp.dll"));
      // As install.ps1 writes it (Windows separators), with a BOM and the previous build.
      var json = "﻿{ \"version\": \"1.13.4\", \"path\": \"payload\\\\1.13.4-aaaaaaaa\", \"sha256\": \"" + abc.ToUpperInvariant() + "\", " +
                 "\"previous\": { \"version\": \"1.13.3\", \"path\": \"payload\\\\1.13.3-bbbbbbbb\", \"sha256\": \"" + oldSha + "\" } }";
      var cur = PayloadSelection.Parse(json);
      Check(cur.Version == "1.13.4" && cur.Previous?.Version == "1.13.3", "current.json with a previous build is read");
      var c = PayloadSelection.Select(root, cur, PayloadSelection.Sha256Of, null);
      Check(c.Ok && !c.FromPrevious && c.Directory == good && c.Sha256 == abc, "the current build passes (hash compared without case)");
      string why;
      Check(PayloadSelection.ResolveDir(root, "payload/../../evil", out why) == null && why.Contains("not inside"), "a path out of the payload folder is refused");
      Check(PayloadSelection.ResolveDir(root, Path.Combine(Path.GetTempPath(), "elsewhere"), out why) == null, "an absolute path elsewhere is refused");
      Check(PayloadSelection.ResolveDir(root, "payload\\a\\b", out why) == null, "a nested folder is refused (one folder per build)");
      Check(PayloadSelection.ResolveDir(root, "", out why) == null && why != null, "an empty path is refused");
      Check(PayloadSelection.ResolveDir(root, good, out why) == good, "an absolute path inside the payload folder is accepted");
      var bad = PayloadSelection.Parse(json.Replace(abc.ToUpperInvariant(), new string('0', 64)));
      var f = PayloadSelection.Select(root, bad, PayloadSelection.Sha256Of, null);
      Check(f.Ok && f.FromPrevious && f.Version == "1.13.3", "a hash mismatch falls back to the previous build");
      var none = PayloadSelection.Parse(json.Replace(abc.ToUpperInvariant(), new string('0', 64)).Replace(oldSha, new string('1', 64)));
      var n = PayloadSelection.Select(root, none, PayloadSelection.Sha256Of, null);
      Check(!n.Ok && n.Why.Contains("current build 1.13.4") && n.Why.Contains("previous build 1.13.3"), "both failing: both reasons are given");
      var signed = PayloadSelection.Select(root, cur, PayloadSelection.Sha256Of, dir => dir == good ? "signing is on: not signed" : null);
      Check(signed.Ok && signed.FromPrevious, "a failed signer check falls back like a failed hash");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.0;del", Path = "payload\\1.13.4-aaaaaaaa", Sha256 = abc }, PayloadSelection.Sha256Of, null).Ok, "a version that is not a version number is refused");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.13.5", Path = "payload\\1.13.5-cccccccc", Sha256 = abc }, PayloadSelection.Sha256Of, null).Ok, "a missing DLL is refused");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.13.4", Path = "payload\\1.13.4-aaaaaaaa", Sha256 = "" }, PayloadSelection.Sha256Of, null).Ok, "a missing sha256 is refused");
      var threw = false; try { PayloadSelection.Parse("{ not json"); } catch (FormatException) { threw = true; }
      Check(threw, "broken JSON is a FormatException (reported, never loaded)");
      Check(PayloadSelection.CompareVersions("1.13.10", "1.13.9") > 0 && PayloadSelection.CompareVersions("1.14.0-dev", "1.14.0") < 0
            && PayloadSelection.CompareVersions("1.13.3", "1.13.3.0") == 0 && PayloadSelection.CompareVersions("2.0", "1.99.99") > 0, "versions compare numerically, pre-release first");
      Check(PayloadSelection.Direction(null, "1.13.4") == "first load" && PayloadSelection.Direction("1.13.3", "1.13.4") == "upgrade"
            && PayloadSelection.Direction("1.13.4", "1.13.3") == "downgrade" && PayloadSelection.Direction("1.13.4", "1.13.4").StartsWith("same"), "swap direction");
      Check(PayloadSelection.IsSafeVersion("1.13.4") && PayloadSelection.IsSafeVersion("1.14.0-dev.2") && !PayloadSelection.IsSafeVersion("..\\x") && !PayloadSelection.IsSafeVersion(null), "version text check");
    } finally { try { Directory.Delete(root, true); } catch { } }
    if (failures > 0) { Console.WriteLine($"tracker test FAILED (loader: {failures})"); Environment.Exit(1); }
  }
}
