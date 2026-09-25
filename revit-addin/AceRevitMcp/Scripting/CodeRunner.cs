using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Scripting
{
    /// <summary>
    /// Compiles C# written by Claude and runs it on Revit's main thread.
    ///
    /// Modes:
    ///   auto     - whole script runs in ONE transaction (default; simplest for edits)
    ///   manual   - script manages its own transactions (ctx.Transact / new Transaction)
    ///   Both are wrapped in a TransactionGroup: merged into one undo step, or rolled back for dry_run.
    ///   readonly is wrapped too and always rolled back, so it can never change the model.
    ///   readonly - no transaction; any attempt to modify the model fails
    ///
    /// dry_run = true runs the script and then rolls every change back: a safe preview.
    /// </summary>
    internal static class CodeRunner
    {
        private const string Template = @"using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using AceRevitMcp.Scripting;
/*USINGS*/
public static class AceScript
{
    public static object Run(ScriptContext ctx)
    {
        var doc = ctx.Doc;
        var uidoc = ctx.UiDoc;
        var uiapp = ctx.UiApp;
        var app = ctx.App;
        var args = ctx.Args;
        void Log(object message) => ctx.Log(message);
#line 1
/*BODY*/
#line default
        return null;
    }
}
";

        private static readonly Regex UsingLine = new Regex(
            @"^\s*using\s+(static\s+)?[A-Za-z_][\w.]*(\s*=\s*[A-Za-z_][\w.<>, ]*)?\s*;\s*$",
            RegexOptions.Compiled);

        private static readonly object CompilerGate = new object();
        private static MethodInfo _compile;
        private static int _counter;

        public static JsonObject Run(UIApplication uiapp, JsonObject args)
        {
            if (args["code"] != null && args["code"] is not JsonValue)
                throw new CommandException("'code' must be a string of C# (it arrived as a JSON object or array).");
            var code = Commands.Args.Str(args, "code");
            if (string.IsNullOrWhiteSpace(code)) throw new CommandException("'code' is required.");

            var mode = (Commands.Args.Str(args, "mode") ?? "auto").ToLowerInvariant();
            var dryRun = args["dry_run"] is JsonValue dv && dv.TryGetValue<bool>(out var dr) && dr;
            var name = Commands.Args.Str(args, "transaction_name") ?? "Claude: script";
            if (mode is not ("auto" or "manual" or "readonly"))
                throw new CommandException("mode must be 'auto', 'manual' or 'readonly'.");

            var source = BuildSource(code);
            var compiled = Compile(source);
            if (compiled.assembly == null)
            {
                return new JsonObject
                {
                    ["success"] = false,
                    ["stage"] = "compile",
                    ["errors"] = new JsonArray(compiled.errors.Select(e => (JsonNode)e).ToArray()),
                    ["hint"] = "Line numbers refer to your code. Fix the errors and call again.",
                };
            }
            if (args["compile_only"] is JsonValue cv && cv.TryGetValue<bool>(out var compileOnly) && compileOnly)
                return new JsonObject { ["success"] = true, ["stage"] = "compile", ["note"] = "Compiled successfully. Nothing was run." };

            var ctx = new ScriptContext(uiapp, args["inputs"] as JsonObject, dryRun);
            var doc = ctx.Doc;
            if (mode != "readonly" && doc == null)
                throw new CommandException("No document is open in Revit. Use mode 'readonly' if the script opens one itself.");

            var entry = compiled.assembly.GetType("AceScript")!.GetMethod("Run")!;
            var response = new JsonObject { ["mode"] = mode, ["dryRun"] = dryRun };
            object result = null;
            Exception failure = null;
            string transactionStatus = null;
            var recording = ChangeTracker.Begin(name);
            var keptChanges = false;

            using (var guard = new ModelGuard(uiapp))
            {
                // Both modes run inside a TransactionGroup. Inner transactions really commit (so Revit's
                // own checks and warnings run and every change is recorded), then the group is either
                // merged into ONE undo step (real run) or rolled back completely (dry run / failure).
                TransactionGroup group = null;
                Transaction tx = null;
                try
                {
                    // readonly runs are also wrapped, and ALWAYS rolled back: even if the code opens its own
                    // transaction it cannot change the model (readonly skips the preview requirement).
                    if (doc != null && !doc.IsReadOnly)
                    {
                        group = new TransactionGroup(doc, name);
                        group.Start();
                    }
                    if (mode == "auto") { tx = new Transaction(doc, name); tx.Start(); }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    result = Invoke(entry, ctx);
                    response["scriptMs"] = sw.ElapsedMilliseconds;

                    if (tx != null)
                    {
                        if (tx.GetStatus() != TransactionStatus.Started)
                            throw new CommandException("The script ended the automatic transaction itself. Use mode 'manual' to manage transactions.");
                        var committed = tx.Commit();
                        if (committed != TransactionStatus.Committed)
                            throw new CommandException("Revit refused to commit the changes (see revitErrors).");
                    }
                    if (group != null && mode == "readonly")
                    {
                        group.RollBack();
                        if (recording.Committed > 0)
                            response["note"] = "Read-only run: the script made changes, and they were all rolled back. Use mode 'auto' or 'manual' (with a preview) to change the model.";
                    }
                    else if (group != null)
                    {
                        if (dryRun) transactionStatus = group.RollBack().ToString();
                        else
                        {
                            transactionStatus = group.Assimilate().ToString();
                            keptChanges = transactionStatus == TransactionStatus.Committed.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                    try { if (tx != null && tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
                    try { if (group != null && group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
                    transactionStatus = mode == "readonly" ? null : "RolledBack";
                }
                finally
                {
                    tx?.Dispose();
                    group?.Dispose();
                    ChangeTracker.End(recording, keptChanges);
                }

                if (transactionStatus != null) response["transaction"] = transactionStatus;
                if (mode != "readonly" && failure == null)
                    response[dryRun ? "wouldChange" : "changed"] = recording.Summary();
                if (guard.Warnings.Count > 0) response["revitWarnings"] = ToArray(guard.Warnings);
                if (guard.Errors.Count > 0) response["revitErrors"] = ToArray(guard.Errors);
                if (guard.Dialogs.Count > 0) response["dialogs"] = ToArray(guard.Dialogs);
            }

            if (ctx.Output.Count > 0) response["output"] = ToArray(ctx.Output);

            if (failure != null)
            {
                response["success"] = false;
                response["stage"] = "runtime";
                response["error"] = $"{failure.GetType().Name}: {failure.Message}";
                var line = ScriptLine(failure);
                if (line != null) response["line"] = line;
                response["note"] = mode == "readonly" ? "Nothing was changed." : "All changes from this run were rolled back. The model is exactly as before.";
                return response;
            }

            response["success"] = true;
            if (mode == "readonly")
            { /* keep any note set above */ }
            else if (dryRun)
                response["note"] = "Dry run: the script ran completely, then every change was rolled back. The model is unchanged.";
            else if (mode != "readonly")
                response["note"] = $"Applied as ONE undo step named '{name}' (Ctrl+Z in Revit, or undo_last_claude_change).";
            response["result"] = JsonConvert.ToNode(result);
            return response;
        }

        private static object Invoke(MethodInfo entry, ScriptContext ctx)
        {
            try { return entry.Invoke(null, new object[] { ctx }); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }

        private static int? ScriptLine(Exception ex)
        {
            var trace = new System.Diagnostics.StackTrace(ex, true);
            foreach (var frame in trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                if (frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name?.StartsWith("AceScript_") == true && frame.GetFileLineNumber() > 0)
                    return frame.GetFileLineNumber();
            }
            return null;
        }

        private static JsonArray ToArray(IEnumerable<string> items) => new JsonArray(items.Select(i => (JsonNode)i).ToArray());

        /// <summary>Hoists top-level "using X;" lines out of the body, keeping line numbers intact.</summary>
        internal static string BuildSource(string code)
        {
            var usings = new StringBuilder();
            var body = new StringBuilder();
            var inPreamble = true;
            foreach (var raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                if (inPreamble && UsingLine.IsMatch(raw))
                {
                    usings.AppendLine(raw.Trim());
                    body.AppendLine(); // keep line numbering aligned
                    continue;
                }
                if (inPreamble && raw.Trim().Length > 0 && !raw.TrimStart().StartsWith("//")) inPreamble = false;
                body.AppendLine(raw);
            }
            return Template.Replace("/*USINGS*/", usings.ToString()).Replace("/*BODY*/", body.ToString());
        }

        private static (Assembly assembly, string[] errors) Compile(string source)
        {
            MethodInfo compile;
            lock (CompilerGate) compile = _compile ??= LoadCompiler();

            var assemblyName = $"AceScript_{Interlocked.Increment(ref _counter)}";
            var output = (object[])compile.Invoke(null, new object[] { source, ReferencePaths(), assemblyName })!;
            var bytes = (byte[])output[0];
            var errors = (string[])output[1];
            if (bytes == null) return (null, errors);

            // Collectible context so thousands of scripts don't leak memory; Revit/our types resolve from the default context.
            var alc = new AssemblyLoadContext(assemblyName, isCollectible: true);
            using var ms = new MemoryStream(bytes);
            return (alc.LoadFromStream(ms), Array.Empty<string>());
        }

        private static MethodInfo LoadCompiler()
        {
            var addinDir = Path.GetDirectoryName(typeof(CodeRunner).Assembly.Location)!;
            var compilerDir = Path.Combine(addinDir, "Compiler");
            var path = Path.Combine(compilerDir, "AceRevitMcp.Compiler.dll");
            if (!File.Exists(path))
                throw new CommandException($"C# compiler not found at {path}. Re-run install.ps1.");

            var alc = new CompilerLoadContext(compilerDir);
            var asm = alc.LoadFromAssemblyPath(path);
            return asm.GetType("AceRevitMcp.Compiler.ScriptCompiler")!.GetMethod("Compile")!;
        }

        private static string[] ReferencePaths()
        {
            var paths = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                string location;
                try { location = asm.Location; } catch { continue; }
                if (string.IsNullOrEmpty(location)) continue;
                if (AssemblyLoadContext.GetLoadContext(asm) is CompilerLoadContext) continue;
                paths.Add(location);
            }

            // Framework facades scripts commonly need even if Revit hasn't loaded them yet.
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            foreach (var name in new[]
                     {
                         "System.Runtime.dll", "netstandard.dll", "System.Collections.dll", "System.Linq.dll",
                         "System.Linq.Expressions.dll", "System.Text.Json.dll", "System.Text.RegularExpressions.dll",
                         "System.Console.dll", "System.IO.dll", "System.Memory.dll", "Microsoft.CSharp.dll",
                         "System.ComponentModel.Primitives.dll", "System.ObjectModel.dll",
                     })
            {
                paths.Add(Path.Combine(runtimeDir, name));
            }

            // Ensure the core Revit + bridge assemblies are always present (first wins on duplicates).
            paths.Insert(0, typeof(ScriptContext).Assembly.Location);
            paths.Insert(0, typeof(UIApplication).Assembly.Location);
            paths.Insert(0, typeof(Document).Assembly.Location);
            return paths.ToArray();
        }

        private sealed class CompilerLoadContext : AssemblyLoadContext
        {
            private readonly string _dir;
            public CompilerLoadContext(string dir) : base("AceRevitMcp.Compiler") { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                var candidate = Path.Combine(_dir, name.Name + ".dll");
                return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
            }
        }
    }
}
