// ACE loader payload selection (no Revit): current.json with its files list, folder containment, links, hash checks,
// previous fallback, verified reads, signing.json read fail-closed, versions.
using System; using System.Collections.Generic; using System.IO; using System.Linq; using AceRevitMcp.Loader;
static class LoaderTests {
  static int failures;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + "loader: " + what); if (!ok) failures++; }
  static string Esc(string s) => s.Replace("\\", "\\\\");
  static string FilesJson(string dir) => "{" + string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f =>
      $"\"{Esc(Path.GetRelativePath(dir, f).Replace('/', '\\'))}\": \"{PayloadSelection.Sha256Of(f)}\"")) + "}";
  public static void Run() {
    var root = Path.Combine(Path.GetTempPath(), "ace-loader-" + Guid.NewGuid().ToString("N"));
    var good = Path.Combine(root, "payload", "1.13.4-aaaaaaaa"); var old = Path.Combine(root, "payload", "1.13.3-bbbbbbbb");
    Directory.CreateDirectory(Path.Combine(good, "Compiler")); Directory.CreateDirectory(old);
    File.WriteAllText(Path.Combine(good, "AceRevitMcp.dll"), "abc");
    File.WriteAllText(Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"), "compiler");
    File.WriteAllText(Path.Combine(good, "Compiler", "Microsoft.CodeAnalysis.dll"), "roslyn");
    File.WriteAllText(Path.Combine(old, "AceRevitMcp.dll"), "old build");
    const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // SHA-256 of "abc"
    try {
      Check(PayloadSelection.Sha256Of(Path.Combine(good, "AceRevitMcp.dll")) == abc, "SHA-256 of the payload file (lower-case hex)");
      var oldSha = PayloadSelection.Sha256Of(Path.Combine(old, "AceRevitMcp.dll"));
      // As install.ps1 writes it (Windows separators), with a BOM, every file listed, and the previous build.
      string Json(string goodFiles, string mainSha) => "\uFEFF{ \"version\": \"1.13.4\", \"path\": \"payload\\\\1.13.4-aaaaaaaa\", \"sha256\": \"" + mainSha + "\", \"files\": " + goodFiles + ", " +
                 "\"previous\": { \"version\": \"1.13.3\", \"path\": \"payload\\\\1.13.3-bbbbbbbb\", \"sha256\": \"" + oldSha + "\", \"files\": " + FilesJson(old) + " } }";
      var json = Json(FilesJson(good), abc.ToUpperInvariant());
      var cur = PayloadSelection.Parse(json);
      Check(cur.Version == "1.13.4" && cur.Previous?.Version == "1.13.3" && cur.Files?.Count == 3, "current.json with its files list and a previous build is read");
      var c = PayloadSelection.Select(root, cur, null, null);
      Check(c.Ok && !c.FromPrevious && c.Directory == good && c.Sha256 == abc, "the current build passes (hash compared without case)");
      Check(c.Bytes != null && System.Text.Encoding.ASCII.GetString(c.Bytes) == "abc", "the verified bytes of AceRevitMcp.dll are kept (loaded from these, not the file)");
      Check(c.AceDlls.Count == 2 && c.AceDlls.ContainsKey("Compiler/AceRevitMcp.Compiler.dll"), "the ACE DLLs (Compiler included) are kept for the signature check, third-party ones are not");
      string why;
      Check(PayloadSelection.ResolveDir(root, "payload/../../evil", out why) == null && why.Contains("not inside"), "a path out of the payload folder is refused");
      Check(PayloadSelection.ResolveDir(root, Path.Combine(Path.GetTempPath(), "elsewhere"), out why) == null, "an absolute path elsewhere is refused");
      Check(PayloadSelection.ResolveDir(root, "payload\\a\\b", out why) == null, "a nested folder is refused (one folder per build)");
      Check(PayloadSelection.ResolveDir(root, "", out why) == null && why != null, "an empty path is refused");
      Check(PayloadSelection.ResolveDir(root, good, out why) == good, "an absolute path inside the payload folder is accepted");

      // S2: the whole folder, not only AceRevitMcp.dll.
      File.WriteAllText(Path.Combine(good, "Compiler", "Microsoft.CodeAnalysis.dll"), "roslyn, changed");
      var t = PayloadSelection.Select(root, cur, null, null);
      Check(t.Ok && t.FromPrevious && t.CurrentWhy.Contains("Microsoft.CodeAnalysis.dll") && t.CurrentWhy.Contains("does not match"), "a changed dependency falls back to the previous build, and says which file");
      File.WriteAllText(Path.Combine(good, "Compiler", "Microsoft.CodeAnalysis.dll"), "roslyn");
      File.WriteAllText(Path.Combine(good, "Extra.dll"), "planted");
      var x = PayloadSelection.Check(root, cur.Current, null, null);
      Check(!x.Ok && x.Why.Contains("Extra.dll") && x.Why.Contains("not listed"), "a file not in the files list is refused");
      File.Delete(Path.Combine(good, "Extra.dll"));
      File.Move(Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"), Path.Combine(root, "moved.dll"));
      x = PayloadSelection.Check(root, cur.Current, null, null);
      Check(!x.Ok && x.Why.Contains("missing"), "a listed file that is missing is refused");
      File.Move(Path.Combine(root, "moved.dll"), Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"));
      Check(!PayloadSelection.Check(root, PayloadSelection.Parse(Json("{}", abc)).Current, null, null).Ok, "an empty files list is refused");
      Check(!PayloadSelection.Check(root, PayloadSelection.Parse(Json(FilesJson(good), oldSha)).Current, null, null).Ok, "sha256 and the files list disagreeing is refused");
      Check(!PayloadSelection.Check(root, PayloadSelection.Parse(Json("{ \"..\\\\x.dll\": \"" + abc + "\" }", abc)).Current, null, null).Ok, "a file name with .. is refused");

      // S5: links.
      var linkTarget = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(linkTarget);
      File.WriteAllText(Path.Combine(linkTarget, "x.dll"), "x");
      var link = Path.Combine(good, "Linked");
      var linked = false;
      try { Directory.CreateSymbolicLink(link, linkTarget); linked = true; } catch { }
      if (linked) {
        x = PayloadSelection.Check(root, cur.Current, null, null);
        Check(!x.Ok && x.Why.Contains("junction or link"), "a junction or link inside the build folder is refused");
        Directory.Delete(link);
        var linkedBuild = Path.Combine(root, "payload", "9.9.9-cccccccc");
        Directory.CreateSymbolicLink(linkedBuild, good);
        Check(PayloadSelection.ResolveDir(root, "payload\\9.9.9-cccccccc", out why) == null && why.Contains("junction or link"), "a build folder that is a link is refused");
        Directory.Delete(linkedBuild);
      } else Console.WriteLine("skip loader: links can't be made here");

      // ReadVerified: what the resolvers use.
      var files = c.Files;
      Check(PayloadSelection.ReadVerified(good, files, Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll")).Length == 8, "a listed, unchanged file is read");
      bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
      File.WriteAllText(Path.Combine(good, "Unlisted.dll"), "u");
      Check(Throws(() => PayloadSelection.ReadVerified(good, files, Path.Combine(good, "Unlisted.dll"))), "a resolver never loads a file that is not listed");
      File.Delete(Path.Combine(good, "Unlisted.dll"));
      File.WriteAllText(Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"), "swapped!");
      Check(Throws(() => PayloadSelection.ReadVerified(good, files, Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"))), "a file changed after the check is refused when it is loaded");
      File.WriteAllText(Path.Combine(good, "Compiler", "AceRevitMcp.Compiler.dll"), "compiler");
      Check(Throws(() => PayloadSelection.ReadVerified(good, files, Path.Combine(old, "AceRevitMcp.dll"))), "a file outside the build folder is refused");

      // Fallback reasons.
      var signed = PayloadSelection.Select(root, cur, null, cand => cand.Directory == good ? "signing is on: not signed" : null);
      Check(signed.Ok && signed.FromPrevious && signed.CurrentWhy.Contains("not signed"), "a failed signer check falls back, with the reason kept for the log and /health");
      var none = PayloadSelection.Select(root, cur, null, cand => "nope");
      Check(!none.Ok && none.Why.Contains("current build 1.13.4") && none.Why.Contains("previous build 1.13.3"), "both failing: both reasons are given");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.0;del", Path = "payload\\1.13.4-aaaaaaaa", Sha256 = abc, Files = new Dictionary<string, string> { ["AceRevitMcp.dll"] = abc } }, null, null).Ok, "a version that is not a version number is refused");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.13.5", Path = "payload\\1.13.5-cccccccc", Sha256 = abc, Files = new Dictionary<string, string> { ["AceRevitMcp.dll"] = abc } }, null, null).Ok, "a missing build folder is refused");
      Check(!PayloadSelection.Check(root, new PayloadRef { Version = "1.13.4", Path = "payload\\1.13.4-aaaaaaaa", Sha256 = "", Files = cur.Files }, null, null).Ok, "a missing sha256 is refused");
      var threw = false; try { PayloadSelection.Parse("{ not json"); } catch (FormatException) { threw = true; }
      Check(threw, "broken JSON is a FormatException (reported, never loaded)");

      // S4: signing.json fails closed.
      var sj = Path.Combine(root, "signing.json");
      Check(!SigningState.Read(sj).On, "no signing.json: signing off");
      File.WriteAllText(sj, "{ broken");
      var st = SigningState.Read(sj);
      Check(st.On && st.Why != null && st.Why.Contains("can't be read"), "an unreadable signing.json counts as on and blocks the load");
      File.WriteAllText(sj, "{ \"enabled\": true, \"thumbprint\": \"\" }");
      st = SigningState.Read(sj);
      Check(st.On && st.Why != null, "signing on without a thumbprint blocks the load");
      File.WriteAllText(sj, "{ \"enabled\": true, \"thumbprint\": \"ab cd" + new string('e', 36) + "\" }");
      st = SigningState.Read(sj);
      Check(st.On && st.Why == null && st.Thumbprint == "ABCD" + new string('E', 36), "the thumbprint is pinned (spaces dropped, upper case)");
      File.WriteAllText(sj, "{ \"enabled\": false, \"thumbprint\": \"x\" }");
      Check(!SigningState.Read(sj).On, "enabled: false is off");

      Check(PayloadSelection.CompareVersions("1.13.10", "1.13.9") > 0 && PayloadSelection.CompareVersions("1.14.0-dev", "1.14.0") < 0
            && PayloadSelection.CompareVersions("1.13.3", "1.13.3.0") == 0 && PayloadSelection.CompareVersions("2.0", "1.99.99") > 0, "versions compare numerically, pre-release first");
      Check(PayloadSelection.Direction(null, "1.13.4") == "first load" && PayloadSelection.Direction("1.13.3", "1.13.4") == "upgrade"
            && PayloadSelection.Direction("1.13.4", "1.13.3") == "downgrade" && PayloadSelection.Direction("1.13.4", "1.13.4").StartsWith("same"), "swap direction");
      Check(PayloadSelection.IsSafeVersion("1.13.4") && PayloadSelection.IsSafeVersion("1.14.0-dev.2") && !PayloadSelection.IsSafeVersion("..\\x") && !PayloadSelection.IsSafeVersion(null), "version text check");
    } finally { try { Directory.Delete(root, true); } catch { } }
    if (failures > 0) { Console.WriteLine($"tracker test FAILED (loader: {failures})"); Environment.Exit(1); }
  }
}
