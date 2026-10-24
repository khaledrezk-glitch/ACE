// Compile-checks C# scripts against the Revit 2025 API exactly as the add-in would run them (no Revit needed):
//   dotnet publish revit-addin/AceRevitMcp.Compiler -c Release -o tools/script-check/compiler
//   dotnet run --project tools/script-check -- mcp-server/scripts [more folders...]
// Files containing EXPECT_COMPILE_ERROR must fail to compile (used for negative tests).
using System.Reflection;
using System.Runtime.Loader;
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var addin = Assembly.LoadFrom(Path.Combine(repo, "revit-addin/AceRevitMcp/bin/Release/net8.0-windows/AceRevitMcp.dll"));
var compilerDir = Path.Combine(repo, "tools/script-check/compiler");
var build = addin.GetType("AceRevitMcp.Scripting.CodeRunner")!.GetMethod("BuildSource", BindingFlags.NonPublic|BindingFlags.Static)!;
var alc = new DirCtx(compilerDir);
var comp = alc.LoadFromAssemblyPath(Path.Combine(compilerDir, "AceRevitMcp.Compiler.dll")).GetType("AceRevitMcp.Compiler.ScriptCompiler")!.GetMethod("Compile")!;
var rt = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var refs = new List<string>{
 Path.Combine(nuget, "nice3point.revit.api.revitapi/2025.0.2/ref/net8.0/RevitAPI.dll"),
 Path.Combine(nuget, "nice3point.revit.api.revitapiui/2025.0.2/ref/net8.0/RevitAPIUI.dll"),
 addin.Location };
refs.AddRange(Directory.GetFiles(rt,"*.dll").Where(f=>!Path.GetFileName(f).StartsWith("Microsoft.VisualBasic")));
int fails=0;
foreach (var f in args.SelectMany(a => Directory.GetFiles(Path.GetFullPath(a), "*.cs")).OrderBy(x=>x)) {
  var code = File.ReadAllText(f);
  var expectFail = code.Contains("EXPECT_COMPILE_ERROR");
  var src = (string)build.Invoke(null, new object[]{code})!;
  var r = (object[])comp.Invoke(null, new object[]{src, refs.ToArray(), "AceScript_t"})!;
  var errs=(string[])r[1];
  var ok = r[0]!=null;
  // The semantic risk screen: built-in scripts must not need allow_risky; files marked EXPECT_RISK must be caught.
  var risks = r.Length > 3 ? (string[])r[3] : Array.Empty<string>();
  var expectRisk = code.Contains("EXPECT_RISK");
  var pass = ok != expectFail && (expectFail || (risks.Length > 0) == expectRisk);
  if(!pass) fails++;
  Console.WriteLine($"{(pass?"PASS":"FAIL")} {Path.GetFileName(f)} compiled={ok}{(risks.Length > 0 ? " risks=" + string.Join("; ", risks) : "")}");
  foreach(var e in errs.Take(6)) Console.WriteLine("    "+e);
}
return fails;

class DirCtx : AssemblyLoadContext { string d; public DirCtx(string d):base("c"){this.d=d;}
 protected override Assembly Load(AssemblyName n){ var c=Path.Combine(d,n.Name+".dll"); return File.Exists(c)?LoadFromAssemblyPath(c):null; } }
