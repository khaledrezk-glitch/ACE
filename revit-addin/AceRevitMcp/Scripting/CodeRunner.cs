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

        /// <summary>The script assembly's load context of the current run, unloaded when the run ends (runs are one at a time).</summary>
        private static AssemblyLoadContext _scriptContext;

        public static JsonObject Run(UIApplication uiapp, JsonObject args)
        {
            try { return RunScript(uiapp, args); }
            finally
            {
                // Without Unload a collectible context is never collected: every run would stay in memory.
                var alc = _scriptContext;
                _scriptContext = null;
                try { alc?.Unload(); } catch (Exception ex) { Log.Warn($"Script unload: {ex.Message}"); }
            }
        }

        private static JsonObject RunScript(UIApplication uiapp, JsonObject args)
        {
            if (args["code"] != null && args["code"] is not JsonValue)
                throw new CommandException("'code' must be a string of C# (it arrived as a JSON object or array).");
            var code = Commands.Args.Str(args, "code");
            if (string.IsNullOrWhiteSpace(code)) throw new CommandException("'code' is required.");

            var mode = (Commands.Args.Str(args, "mode") ?? "auto").ToLowerInvariant();
            var dryRun = Commands.Args.Bool(args, "dry_run");
            var previewImage = Commands.Args.Bool(args, "preview_image");
            var name = Commands.Args.Str(args, "transaction_name") ?? "Claude: script";
            if (mode is not ("auto" or "manual" or "readonly"))
                throw new CommandException("mode must be 'auto', 'manual' or 'readonly'.");

            // Approvals made in the ACE Companion panel: a change the user already applied or cancelled
            // there must not be applied again by Claude.
            var fromPanel = Commands.Args.Bool(args, "_fromPanel");
            var compileOnlyRequested = Commands.Args.Bool(args, "compile_only");
            var tracked = mode != "readonly" && !compileOnlyRequested;
            var hash = tracked ? Companion.ActivityHub.Fingerprint("execute_code", args) : null;
            if (tracked && !dryRun && !fromPanel && Companion.ActivityHub.Decided(hash, "this change") is { } earlier)
                return earlier.Rejected
                    ? new JsonObject { ["success"] = false, ["stage"] = "rejected", ["error"] = earlier.Note }
                    : new JsonObject { ["success"] = true, ["alreadyApplied"] = true, ["note"] = earlier.Note, ["outcome"] = earlier.Outcome };
            var activeView = uiapp.ActiveUIDocument?.ActiveView?.Id.Value ?? 0;
            if (tracked && !dryRun && Companion.ActivityHub.WrongPlace(hash, uiapp.ActiveUIDocument?.Document, activeView) is string wrong)
                return new JsonObject { ["success"] = false, ["stage"] = "wrong_model", ["error"] = wrong };

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
            if (compileOnlyRequested)
                return new JsonObject { ["success"] = true, ["stage"] = "compile", ["note"] = "Compiled successfully. Nothing was run." };

            var ctx = new ScriptContext(uiapp, args["inputs"] as JsonObject, dryRun);
            var doc = ctx.Doc;
            if (mode != "readonly" && doc == null)
                throw new CommandException("No document is open in Revit. Use mode 'readonly' if the script opens one itself.");

            var entry = compiled.assembly.GetType("AceScript")!.GetMethod("Run")!;
            var response = new JsonObject { ["mode"] = mode, ["dryRun"] = dryRun };
            object result = null;
            var rollbackFailed = new List<string>();
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
                // Other open models (not links) are wrapped too: a script may edit them with its own transactions,
                // and a dry run or read-only run must leave EVERY model untouched.
                var others = new List<TransactionGroup>();
                try
                {
                    // readonly runs are also wrapped, and ALWAYS rolled back: even if the code opens its own
                    // transaction it cannot change the model (readonly skips the preview requirement).
                    if (doc != null && !doc.IsReadOnly)
                    {
                        group = new TransactionGroup(doc, name);
                        group.Start();
                    }
                    foreach (Document other in uiapp.Application.Documents)
                    {
                        if (doc != null && other.Equals(doc)) continue;
                        if (other.IsLinked || other.IsReadOnly) continue;
                        try { var g = new TransactionGroup(other, name); g.Start(); others.Add(g); }
                        catch { /* not modifiable right now: its changes cannot be made either */ }
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
                    // A picture of the previewed change, taken before the rollback (the temporary views go with it).
                    if (dryRun && group != null && mode != "readonly" && previewImage)
                    {
                        recording.Paused = true;
                        try { var imgs = PreviewImages.Capture(doc, recording.AddedIds, recording.ModifiedIds); if (imgs.Count > 0) response["previewImages"] = imgs; }
                        catch (Exception ex) { response["previewImageError"] = ex.Message; }
                        finally { recording.Paused = false; }
                    }
                    // Other models follow the active one: rolled back for previews and read-only runs, kept when applied.
                    foreach (var g in others)
                    {
                        if (!(dryRun || mode == "readonly")) g.Assimilate();
                        else if (g.RollBack() != TransactionStatus.RolledBack) rollbackFailed.Add("another open model");
                    }
                    if (group != null && mode == "readonly")
                    {
                        if (group.RollBack() != TransactionStatus.RolledBack) rollbackFailed.Add(doc.Title);
                        if (recording.Committed > 0)
                            response["note"] = "Read-only run: the script made changes, and they were all rolled back. Use mode 'auto' or 'manual' (with a preview) to change the model.";
                    }
                    else if (group != null)
                    {
                        if (dryRun)
                        {
                            transactionStatus = group.RollBack().ToString();
                            if (transactionStatus != TransactionStatus.RolledBack.ToString()) rollbackFailed.Add(doc.Title);
                        }
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
                    void RollBack(string what, Func<bool> open, Func<TransactionStatus> rollBack)
                    {
                        try { if (open() && rollBack() != TransactionStatus.RolledBack) rollbackFailed.Add(what); }
                        catch (Exception rex) { rollbackFailed.Add($"{what}: {rex.Message}"); }
                    }
                    if (tx != null) RollBack("transaction", () => tx.HasStarted() && !tx.HasEnded(), tx.RollBack);
                    if (group != null) RollBack(doc.Title, () => group.HasStarted() && !group.HasEnded(), group.RollBack);
                    foreach (var g in others) RollBack("another open model", () => g.HasStarted() && !g.HasEnded(), g.RollBack);
                    if (rollbackFailed.Count > 0) Log.Error($"Script rollback failed: {string.Join("; ", rollbackFailed)}");
                    transactionStatus = mode == "readonly" ? null : "RolledBack";
                }
                finally
                {
                    tx?.Dispose();
                    group?.Dispose();
                    foreach (var g in others) g.Dispose();
                    ChangeTracker.End(recording, keptChanges);
                }

                if (transactionStatus != null) response["transaction"] = transactionStatus;
                if (mode != "readonly" && failure == null)
                    response[dryRun ? "wouldChange" : "changed"] = recording.Summary();
                if (guard.Warnings.Count > 0) response["revitWarnings"] = Grouped(guard.Warnings);
                if (guard.Errors.Count > 0) response["revitErrors"] = Grouped(guard.Errors);
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
                if (rollbackFailed.Count > 0)
                {
                    response["rollbackFailed"] = new JsonArray(rollbackFailed.Select(x => (JsonNode)x).ToArray());
                    response["note"] = "WARNING: the run failed AND Revit could not roll everything back, so the model may be partly changed. Tell the user, check with a read-only query, and offer Ctrl+Z.";
                }
                else response["note"] = mode == "readonly" ? "Nothing was changed." : "All changes from this run were rolled back. The model is exactly as before.";
                return response;
            }

            if (rollbackFailed.Count > 0)
            {
                // The script worked, but its changes could not all be taken back: never present that as a clean preview.
                response["success"] = false;
                response["stage"] = "rollback";
                response["rollbackFailed"] = ToArray(rollbackFailed);
                response["error"] = "The script ran, but Revit could not roll all of its changes back, so the model may be partly changed. Tell the user, check with a read-only query, and offer Ctrl+Z.";
                return response;
            }

            response["success"] = true;
            if (tracked && dryRun) Companion.ActivityHub.AddPending("execute_code", args, response, doc, activeView);
            else if (tracked && !fromPanel) Companion.ActivityHub.MarkAppliedByClaude(hash);
            if (mode == "readonly") { /* keep any note set above */ }
            else if (dryRun)
                response["note"] = "Dry run: the script ran completely, then every change was rolled back. The model is unchanged.";
            else
                response["note"] = $"Applied as ONE undo step named '{name}' (Ctrl+Z in Revit, or undo_last_claude_change).";
            // The result comes first, so a long list of warnings or output can never push it out of a shortened reply.
            var ordered = new JsonObject { ["success"] = true, ["result"] = JsonConvert.ToNode(result) };
            var rest = response.ToList();
            response.Clear();
            foreach (var kv in rest) if (!ordered.ContainsKey(kv.Key)) ordered[kv.Key] = kv.Value;
            return ordered;
        }

        /// <summary>Revit messages grouped by text with a count (a large edit can raise the same warning thousands of times).</summary>
        private static JsonArray Grouped(IEnumerable<string> items) =>
            new JsonArray(items.GroupBy(i => i).OrderByDescending(g => g.Count()).Take(30)
                .Select(g => (JsonNode)(g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key)).ToArray());

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

        /// <summary>Compiled scripts by source, newest last: the preview and the apply of one change compile once.</summary>
        private static readonly List<(string Key, byte[] Bytes)> Compiled = new List<(string, byte[])>();
        private const int CompiledKept = 16;

        private static (Assembly assembly, string[] errors) Compile(string source)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
            byte[] bytes;
            lock (Compiled) bytes = Compiled.FirstOrDefault(c => c.Key == key).Bytes;
            if (bytes == null)
            {
                MethodInfo compile;
                lock (CompilerGate) compile = _compile ??= LoadCompiler();
                var assemblyName = $"AceScript_{Interlocked.Increment(ref _counter)}";
                var output = (object[])compile.Invoke(null, new object[] { source, ReferencePaths(), assemblyName })!;
                bytes = (byte[])output[0];
                if (bytes == null) return (null, (string[])output[1]);
                lock (Compiled)
                {
                    Compiled.Add((key, bytes));
                    if (Compiled.Count > CompiledKept) Compiled.RemoveAt(0);
                }
            }
            var assemblyNameOf = $"AceScript_{Interlocked.Increment(ref _counter)}";

            // Collectible context so thousands of scripts don't leak memory; Revit/our types resolve from the default context.
            var alc = _scriptContext = new AssemblyLoadContext(assemblyNameOf, isCollectible: true);
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

        private static string[] _referencePaths;
        private static int _referenceAssemblyCount;

        /// <summary>
        /// Compiles a trivial script on a background thread when Revit starts, so the first real script does not pay
        /// for loading Roslyn and reading the reference assemblies.
        /// </summary>
        public static void WarmUp() => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                MethodInfo compile;
                lock (CompilerGate) compile = _compile ??= LoadCompiler();
                compile.Invoke(null, new object[] { BuildSource("return null;"), ReferencePaths(), "AceScript_warmup" });
            }
            catch (Exception ex) { Log.Warn($"Script compiler warm-up: {ex.Message}"); }
        });

        /// <summary>The reference list, rebuilt only when assemblies have been loaded since the last time.</summary>
        private static string[] ReferencePaths()
        {
            var count = AppDomain.CurrentDomain.GetAssemblies().Length;
            var cached = _referencePaths;
            if (cached != null && count == _referenceAssemblyCount) return cached;
            var paths = BuildReferencePaths();
            _referenceAssemblyCount = count;
            return _referencePaths = paths;
        }

        private static string[] BuildReferencePaths()
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
