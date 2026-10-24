using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif
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
    ///   no_transaction - NO TransactionGroup and no transaction, for APIs that refuse any open transaction (SaveAs, Close of
    ///            another model, RevitLinkType.LoadFrom, TransmissionData on other files). Needs allow_risky; a dry run only
    ///            compiles and screens (nothing runs); the run cannot be rolled back and is not one undo step.
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

#if NETFRAMEWORK
        /// <summary>Revit 2024 (.NET Framework 4.8): the script compiler is not ported yet (backlog R24, second slice).</summary>
        internal const string NotOnThisRevit = "Running C# scripts (execute_revit_code, run_saved_script) is not available on Revit 2024 yet. " +
            "The other ACE tools work: model check, model brief, element search, warnings, clash tests, worksets, parameters.";

        public static JsonObject Run(UIApplication uiapp, JsonObject args) => throw new CommandException(NotOnThisRevit);
#else
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
#endif

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
            if (mode is not ("auto" or "manual" or "readonly" or "no_transaction"))
                throw new CommandException("mode must be 'auto', 'manual', 'readonly' or 'no_transaction'.");
            var noTransaction = mode == "no_transaction";

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
            // expect_model: the script runs on the ACTIVE document; refuse when the user switched Revit's window to another model
            // (title, title without .rvt, or file path; case-insensitive).
            if (Commands.Args.Str(args, "expect_model") is string expectModel && !string.IsNullOrWhiteSpace(expectModel))
            {
                var activeDoc = uiapp.ActiveUIDocument?.Document;
                if (!Util.ModelNames.Same(activeDoc, expectModel))
                    return new JsonObject { ["success"] = false, ["stage"] = "wrong_model",
                        ["error"] = $"The active model is \"{activeDoc?.Title ?? "(none)"}\", not \"{expectModel}\": nothing ran. Activate {expectModel} (open_model activate) and call again." };
            }
            var activeView = uiapp.ActiveUIDocument?.ActiveView?.Id.Value ?? 0;
            if (tracked && !dryRun && Companion.ActivityHub.WrongPlace(hash, uiapp.ActiveUIDocument?.Document, activeView) is string wrong)
                return new JsonObject { ["success"] = false, ["stage"] = "wrong_model", ["error"] = wrong };

            if (noTransaction && !Commands.Args.Bool(args, "allow_risky") && !compileOnlyRequested)
                return new JsonObject
                {
                    ["success"] = false, ["stage"] = "risky",
                    ["error"] = NoTransactionRefusal,
                };

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
            // Calls that escape the TransactionGroup rollback (saving, syncing, borrowing, renaming worksets, exporting...):
            // a dry run only compiles and screens the code (not executed, success: executed false, previewable false), so the
            // identical real run (the server binds the exact code) can follow with allow_risky; a readonly run refuses them.
            var notUndoable = compiled.risks.Where(x => x.EndsWith(" (not undoable)", StringComparison.Ordinal)).Select(x => x.Substring(0, x.Length - 15)).ToArray();
            if (notUndoable.Length > 0 && mode == "readonly" && !compileOnlyRequested)
                return new JsonObject
                {
                    ["success"] = false,
                    ["stage"] = "risky",
                    ["error"] = $"This code cannot be previewed: not undoable. It {string.Join(", ", notUndoable)}, which a readonly run cannot roll back. Nothing was run." +
                                (notUndoable.Any(x => x.Contains("files on disk")) ? " Write files with ctx.WriteOutput(name, text) (the ACE output folder, allowed in readonly runs and dry runs)." : ""),
                };
            if (noTransaction && dryRun && !compileOnlyRequested)
                return new JsonObject
                {
                    ["success"] = true, ["mode"] = mode, ["dryRun"] = true, ["executed"] = false, ["previewable"] = false,
                    ["notUndoable"] = new JsonArray(notUndoable.Select(x => (JsonNode)x).Prepend("runs with no transaction (mode no_transaction)").ToArray()),
                    ["risks"] = new JsonArray(compiled.risks.Select(x => (JsonNode)x).ToArray()),
                    ["warning"] = NoTransactionDryRun,
                };
            if (notUndoable.Length > 0 && dryRun && !compileOnlyRequested)
                return new JsonObject
                {
                    ["success"] = true, ["mode"] = mode, ["dryRun"] = true, ["executed"] = false, ["previewable"] = false,
                    ["notUndoable"] = new JsonArray(notUndoable.Select(x => (JsonNode)x).ToArray()),
                    ["risks"] = new JsonArray(compiled.risks.Select(x => (JsonNode)x).ToArray()),
                    ["warning"] = $"Not previewed: this script {string.Join(", ", notUndoable)}, which cannot be undone. The dry run only compiled and screened it. " +
                                  "A real run needs allow_risky and the user's consent, and it cannot be rolled back.",
                };
            // What the code really calls (resolved by the compiler): files, programs, network, reflection, saving or
            // syncing models need the user's consent, like the MCP server's quick text screen.
            if (compiled.risks.Length > 0 && !Commands.Args.Bool(args, "allow_risky"))
                return new JsonObject
                {
                    ["success"] = false,
                    ["stage"] = "risky",
                    ["error"] = $"Blocked for safety: this code {string.Join(", ", compiled.risks)}. That goes beyond editing the model. " +
                                "Explain to the user exactly what it would do and why; only if they agree, call again with allow_risky: true.",
                };
            if (compileOnlyRequested)
                return new JsonObject { ["success"] = true, ["stage"] = "compile", ["note"] = "Compiled successfully. Nothing was run." };

            var ctx = new ScriptContext(uiapp, args["inputs"] as JsonObject, dryRun);
            var doc = ctx.Doc;
            if (mode != "readonly" && !noTransaction && doc == null)
                throw new CommandException("No document is open in Revit. Use mode 'readonly' if the script opens one itself.");
            if (noTransaction) return RunWithoutTransaction(uiapp, args, ctx, compiled.assembly, name, hash, tracked, fromPanel);

            var entry = compiled.assembly.GetType("AceScript")!.GetMethod("Run")!;
            var response = new JsonObject { ["mode"] = mode, ["dryRun"] = dryRun };
            object result = null;
            var rollbackFailed = new List<string>();
            Exception failure = null;
            string transactionStatus = null;
            var recording = ChangeTracker.Begin(name);
            var keptChanges = false;
            WarningGate gate = null;   // "no new warnings" (G24): the active model's warnings before vs after, for every change run

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
                        if (mode != "readonly") gate = WarningGate.Begin(doc, args);
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
                    // New warnings (or a fix run that left its targets): an apply is rolled back whole, a dry run reports them.
                    if (gate != null && group != null && !gate.Check(doc, guard, recording) && !dryRun) throw new WarningsRefused();
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
                            recording.CaptureNames(doc);   // added elements still exist
                            transactionStatus = group.RollBack().ToString();
                            recording.CaptureNames(doc);   // deleted elements exist again
                            if (transactionStatus != TransactionStatus.RolledBack.ToString()) rollbackFailed.Add(doc.Title);
                        }
                        else
                        {
                            recording.CaptureNames(doc);
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
                // Workshared preview: which previewed elements this user now holds (a preview may borrow them from
                // central; borrowed, unchanged elements are released at the next sync or with Relinquish All Mine).
                if (dryRun && failure == null && doc != null && doc.IsWorkshared && recording.ModifiedIds.Count > 0)
                {
                    var held = recording.ModifiedIds.Take(2000).Count(id => RevitJson.Safe(() =>
                        WorksharingUtils.GetCheckoutStatus(doc, new ElementId(id)) == CheckoutStatus.OwnedByCurrentUser));
                    if (held > 0) response["heldAfterPreview"] = $"{held} of the previewed elements are held by you now (some may have been before). Unchanged ones are released at your next sync, or with Collaborate > Relinquish All Mine.";
                }
                if (guard.Warnings.Count > 0) response["revitWarnings"] = Grouped(guard.Warnings);
                if (guard.Errors.Count > 0) response["revitErrors"] = Grouped(guard.Errors);
                if (guard.Dialogs.Count > 0) response["dialogs"] = ToArray(guard.Dialogs);
                gate?.Report(response, dryRun);
            }

            if (ctx.Output.Count > 0) response["output"] = ToArray(ctx.Output);
            if (ctx.Skipped.Count > 0)
            {
                response["skippedUneditable"] = ToArray(ctx.Skipped.Take(50));
                response["skippedCount"] = ctx.Skipped.Count;
            }

            if (failure is WarningsRefused && rollbackFailed.Count == 0)
            {
                response["success"] = false;
                response["stage"] = gate.Stage;
                response["error"] = gate.Error;
                response["hint"] = gate.Hint;
                response["note"] = "All changes from this run were rolled back. The model is exactly as before.";
                return response;
            }
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
            // A preview whose identical apply would refuse (new warnings not accepted, targets left) gets no Apply card.
            if (tracked && dryRun && gate?.Refuse != true) Companion.ActivityHub.AddPending("execute_code", args, response, doc, activeView);
            else if (tracked && !fromPanel) Companion.ActivityHub.MarkAppliedByClaude(hash);
            if (mode == "readonly") { /* keep any note set above */ }
            else if (dryRun)
                response["note"] = "Dry run: the script ran completely, then every change was rolled back. The model is unchanged." + (gate?.Outcome != null ? " " + gate.Line(true) : "");
            else
            {
                response["note"] = $"Applied as ONE undo step named '{name}' (Ctrl+Z in Revit, or undo_last_claude_change)." + (gate?.Outcome != null ? " " + gate.Line(false) : "");
                // A committed run that changed nothing still marks the model modified (e.g. a health check in mode auto).
                if (mode == "auto" && keptChanges && recording.ElementChanges == 0)
                    response["hint"] = NoElementChangedHint;
            }
            // The result comes first, so a long list of warnings or output can never push it out of a shortened reply.
            var ordered = new JsonObject { ["success"] = true, ["result"] = JsonConvert.ToNode(result) };
            var rest = response.ToList();
            response.Clear();
            foreach (var kv in rest) if (!ordered.ContainsKey(kv.Key)) ordered[kv.Key] = kv.Value;
            return ordered;
        }

        /// <summary>Thrown inside the run when the warning gate refuses an apply: the normal failure path rolls everything back.</summary>
        private sealed class WarningsRefused : Exception { public WarningsRefused() : base("new warnings") { } }

        internal const string NoElementChangedHint = "no element changed: run checks like this in mode readonly (the committed transaction still marks the model modified; " +
            "close_model closes a model ACE opened whose only changes were such runs).";

        internal const string NoTransactionRefusal = "Blocked for safety: mode no_transaction runs the code with no transaction group, so nothing it does can be rolled back " +
            "and it is not one undo step. Explain to the user exactly what it would do (save, close, load links...); only if they agree, call again with allow_risky: true.";
        internal const string NoTransactionDryRun = "Not previewed: mode no_transaction runs with no transaction, so nothing could be rolled back. The dry run only compiled and screened the code; " +
            "nothing ran. A real run needs allow_risky and the user's consent; it cannot be rolled back and is not one undo step.";

        /// <summary>
        /// mode no_transaction, the real run: no TransactionGroup, no transaction, no other models wrapped, so APIs that need no open
        /// transaction work (SaveAs, Close, RevitLinkType.LoadFrom...). Transactions the script commits itself are each their own
        /// undo step, and nothing is rolled back on failure. The ChangeTracker still records what changed; afterwards
        /// undo_last_claude_change refuses (the run is not one step).
        /// </summary>
        private static JsonObject RunWithoutTransaction(UIApplication uiapp, JsonObject args, ScriptContext ctx, Assembly assembly, string name, string hash, bool tracked, bool fromPanel)
        {
            var entry = assembly.GetType("AceScript")!.GetMethod("Run")!;
            var response = new JsonObject { ["mode"] = "no_transaction", ["dryRun"] = false };
            object result = null;
            Exception failure = null;
            var recording = ChangeTracker.Begin(name);
            using (var guard = new ModelGuard(uiapp))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    result = Invoke(entry, ctx);
                    response["scriptMs"] = sw.ElapsedMilliseconds;
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    ChangeTracker.End(recording, true);
                    // Not one undo step: undo_last_claude_change must not undo only the last of its transactions.
                    if (recording.Committed > 0) ChangeTracker.MarkUndone();
                }
                try { if (ctx.Doc != null && ctx.Doc.IsValidObject) recording.CaptureNames(ctx.Doc); } catch { /* the model may have been closed */ }
                response["changed"] = recording.Summary();
                response["transactionsCommitted"] = recording.Committed;
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
                response["note"] = "WARNING: mode no_transaction: nothing was rolled back. Whatever the script did before the error stays (files saved, models closed, " +
                                   "transactions it committed). Tell the user and check with a read-only query.";
                return response;
            }
            if (tracked && !fromPanel) Companion.ActivityHub.MarkAppliedByClaude(hash);
            response["note"] = "Ran with no transaction (mode no_transaction): NOT one undo step and not undoable as a whole. Each transaction the script committed is its own " +
                               "undo step in Revit; saving, closing and loading links cannot be undone. undo_last_claude_change does not undo this run.";
            var ordered = new JsonObject { ["success"] = true, ["result"] = JsonConvert.ToNode(result) };
            foreach (var kv in response.ToList()) { response.Remove(kv.Key); if (!ordered.ContainsKey(kv.Key)) ordered[kv.Key] = kv.Value; }
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
        private static readonly List<(string Key, byte[] Bytes, string[] Risks)> Compiled = new List<(string, byte[], string[])>();
        private const int CompiledKept = 16;

        private static (Assembly assembly, string[] errors, string[] risks) Compile(string source)
        {
            var key = Compat.Sha256Hex(source);
            byte[] bytes;
            string[] risks;
            lock (Compiled) (_, bytes, risks) = Compiled.FirstOrDefault(c => c.Key == key);
            if (bytes == null)
            {
                MethodInfo compile;
                lock (CompilerGate) compile = _compile ??= LoadCompiler();
                var assemblyName = $"AceScript_{Interlocked.Increment(ref _counter)}";
                var output = (object[])compile.Invoke(null, new object[] { source, ReferencePaths(), assemblyName })!;
                bytes = (byte[])output[0];
                if (bytes == null) return (null, (string[])output[1], Array.Empty<string>());
                risks = output.Length > 3 ? (string[])output[3] : Array.Empty<string>();
                lock (Compiled)
                {
                    Compiled.Add((key, bytes, risks));
                    if (Compiled.Count > CompiledKept) Compiled.RemoveAt(0);
                }
            }
#if NETFRAMEWORK
            return (Assembly.Load(bytes), Array.Empty<string>(), risks ?? Array.Empty<string>());
#else
            var assemblyNameOf = $"AceScript_{Interlocked.Increment(ref _counter)}";

            // Collectible context so thousands of scripts don't leak memory; Revit types resolve from the default context and
            // ACE's own from the context ACE runs in (the ACE loader's payload context, or the default one).
            var alc = _scriptContext = new ScriptLoadContext(assemblyNameOf);
            using var ms = new MemoryStream(bytes);
            return (alc.LoadFromStream(ms), Array.Empty<string>(), risks ?? Array.Empty<string>());
#endif
        }

        private static MethodInfo LoadCompiler()
        {
            if (_stopped) throw new CommandException("ACE is stopping or loading a new build: the script compiler is not available. Send it again in a few seconds.");
            var addinDir = PayloadEntry.Directory ?? Path.GetDirectoryName(typeof(CodeRunner).Assembly.Location)!;
            var compilerDir = Path.Combine(addinDir, "Compiler");
            var path = Path.Combine(compilerDir, "AceRevitMcp.Compiler.dll");
            if (!File.Exists(path))
                throw new CommandException($"C# compiler not found at {path}. Re-run install.ps1.");
#if NETFRAMEWORK
            throw new CommandException(NotOnThisRevit);
#else
            var alc = _compilerContext = new CompilerLoadContext(compilerDir);
            var asm = alc.LoadVerified(path) ?? throw new CommandException($"The C# compiler at {path} was refused (see the add-in log). Re-run install.ps1.");
            return asm.GetType("AceRevitMcp.Compiler.ScriptCompiler")!.GetMethod("Compile")!;
#endif
        }

        /// <summary>
        /// Payload Stop: forget the compiler and, when ACE runs in the ACE loader's collectible context, unload the
        /// compiler's context too (a non-collectible one would keep this ACE build in memory for good).
        /// </summary>
        public static void Shutdown()
        {
            // Under the gate: a warm-up or a compile that has not loaded the compiler yet can't load it after this.
            lock (CompilerGate) { _stopped = true; _compile = null; }
            _references = null;
            lock (Compiled) Compiled.Clear();
#if !NETFRAMEWORK
            var alc = _compilerContext;
            _compilerContext = null;
            if (alc != null && alc.IsCollectible) alc.Unload();
#endif
        }

        /// <summary>Set by Shutdown (payload Stop), under CompilerGate: no compiler is loaded again by this build.</summary>
        private static bool _stopped;

        private static Tuple<int, string[]> _references;   // one reference: the count and the list always belong together

        /// <summary>
        /// Compiles a trivial script on a background thread when Revit starts, so the first real script does not pay
        /// for loading Roslyn and reading the reference assemblies.
        /// </summary>
        public static void WarmUp()
        {
#if !NETFRAMEWORK
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    MethodInfo compile;
                    lock (CompilerGate) compile = _compile ??= LoadCompiler();
                    compile.Invoke(null, new object[] { BuildSource("return null;"), ReferencePaths(), "AceScript_warmup" });
                }
                catch (Exception ex) { Log.Warn($"Script compiler warm-up: {ex.Message}"); }
            });
#endif
        }

        /// <summary>The reference list, rebuilt only when assemblies have been loaded since the last time.</summary>
        private static string[] ReferencePaths()
        {
            // Counted without script assemblies (collectible, unloaded later), so a script coming and going never hides a newly loaded assembly.
#if NETFRAMEWORK
            var count = AppDomain.CurrentDomain.GetAssemblies().Count(a => !a.IsDynamic);
#else
            var count = AppDomain.CurrentDomain.GetAssemblies().Count(a => !a.IsDynamic && !a.IsCollectible);
#endif
            if (_references is { } cached && cached.Item1 == count) return cached.Item2;
            var paths = BuildReferencePaths();
            _references = Tuple.Create(count, paths);
            return paths;
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
#if !NETFRAMEWORK
                var context = AssemblyLoadContext.GetLoadContext(asm);
                if (context is CompilerLoadContext) continue;
                // Another collectible context's assembly (a previous ACE build after reload_addin, an old script) would
                // give the script two copies of the same types.
                if (asm.IsCollectible && context != OwnContext) continue;
#endif
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
            paths.Insert(0, PayloadEntry.AssemblyPath);   // empty Location when the ACE loader loaded ACE from bytes
            paths.Insert(0, typeof(UIApplication).Assembly.Location);
            paths.Insert(0, typeof(Document).Assembly.Location);
            return paths.ToArray();
        }

#if !NETFRAMEWORK
        private static CompilerLoadContext _compilerContext;

        /// <summary>The context ACE runs in: the default one (loaded directly) or the ACE loader's collectible one.</summary>
        private static AssemblyLoadContext OwnContext => AssemblyLoadContext.GetLoadContext(typeof(CodeRunner).Assembly) ?? AssemblyLoadContext.Default;

        /// <summary>A script's context: ACE's own assemblies come from the context ACE runs in, the rest from the default.</summary>
        private sealed class ScriptLoadContext : AssemblyLoadContext
        {
            public ScriptLoadContext(string name) : base(name, isCollectible: true) { }

            protected override Assembly Load(AssemblyName name)
            {
                var own = OwnContext;
                if (own == Default) return null;
                return own.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Roslyn, isolated. Collectible when ACE itself is (under the ACE loader), so a reload can let both go.</summary>
        private sealed class CompilerLoadContext : AssemblyLoadContext
        {
            private readonly string _dir;
            public CompilerLoadContext(string dir) : base("AceRevitMcp.Compiler", isCollectible: OwnContext.IsCollectible) { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                var candidate = Path.Combine(_dir, name.Name + ".dll");
                return File.Exists(candidate) ? LoadVerified(candidate) : null;
            }

            /// <summary>
            /// Under the ACE loader only a file listed in current.json, from bytes checked against it (null when refused);
            /// loaded directly, the file as it is.
            /// </summary>
            public Assembly LoadVerified(string file)
            {
                var read = PayloadEntry.ReadVerified;
                if (read == null) return LoadFromAssemblyPath(file);
                byte[] bytes;
                try { bytes = read(file); }
                catch (Exception ex) { Log.Warn($"Script compiler: {Path.GetFileName(file)} refused: {ex.Message}"); return null; }
                using var ms = new MemoryStream(bytes);
                return LoadFromStream(ms);
            }
        }
#endif
    }
}
