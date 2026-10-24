using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Commands.SelfTestLogic;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Bridge "ace_self_test" and the ribbon's Self-test button (G26, register #43): checks a new install without touching the
    /// owner's models. It creates its OWN blank project (Application.NewProjectDocument: the default template when it exists,
    /// else metric), builds TST_ fixtures in it, and runs each write tool's real engine on it end to end, through the same gate
    /// the server applies (check, dry run, identical apply with the dry run's planHash, the tool's self-check, then ACE's own
    /// read-back): save_model save_as and save_as central, assign_worksets (with its after-check), parameter_fill (bind + NA
    /// fill), purge_unused and the deletion probe. The blank project is saved only into %TEMP%\ACE-selftest\&lt;stamp&gt; (worksharing
    /// needs a saved file), closed without saving and the folder deleted. Before and after, every other open document's
    /// IsModified, element count and element type list hash are recorded: any difference is a FAIL line.
    /// Safety: it is not an apply on the user's model (it never edits, saves or closes another document), so the user-model
    /// gate does not apply; the Companion's cards and the "undo last Claude change" state are put back exactly as they were; the
    /// shared parameter file setting and the per-machine central identity record are restored. One line per tool goes to
    /// %APPDATA%\ACE-RevitMCP\self-test\self-test-&lt;stamp&gt;.txt and back to the caller.
    /// </summary>
    internal static class SelfTest
    {
        internal const string Name = "TST_ACE_SelfTest";
        internal const string ParamName = "TST_ACE_SelfTest_Note";
        internal static readonly Guid ParamGuid = new Guid("6c1f0e2a-4b3d-4c5e-9f70-8a1b2c3d4e5f");
        private static bool _running;

        internal sealed class Outcome
        {
            public List<L.ToolResult> Results = new List<L.ToolResult>();
            public List<string> Notes = new List<string>();
            public string ReportPath, Text, OverallLine;
            public bool Passed;
            public double Seconds;
            public int OpenDocuments;

            public JsonObject ToJson() => new JsonObject
            {
                ["passed"] = Passed,
                ["overall"] = OverallLine,
                ["lines"] = new JsonArray(Results.Select(r => (JsonNode)L.Line(r)).ToArray()),
                ["results"] = new JsonArray(Results.Select(r => (JsonNode)new JsonObject { ["tool"] = r.Tool, ["status"] = r.Status, ["reason"] = r.Reason, ["seconds"] = Math.Round(r.Seconds, 2) }).ToArray()),
                ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
                ["openDocumentsChecked"] = OpenDocuments,
                ["report"] = ReportPath,
                ["text"] = Text,
            };
        }

        /// <summary>Bridge command "ace_self_test" (no arguments).</summary>
        public static JsonNode Run(UIApplication app, JsonObject args) => Execute(app).ToJson();

        /// <summary>The self-test; throws CommandException (nothing started) when it must not run now.</summary>
        internal static Outcome Execute(UIApplication app)
        {
            var refusal = L.Refusal(RequestDispatcher.Busy?.Command, OpenTransactions(app), _running);
            if (refusal != null) throw new CommandException(refusal);
            _running = true;
            var total = Stopwatch.StartNew();
            var at = DateTime.Now;
            var o = new Outcome();
            var temp = Path.Combine(Path.GetTempPath(), "ACE-selftest", at.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            var identityFile = Path.Combine(AceConfig.Directory, CentralIdentityLogic.FileName);
            byte[] identities = null;
            var identitiesKnown = false;   // read before the test (also "there was none"): only then is it put back
            string sharedParams = null;
            var before = new List<L.DocPrint>();
            var template = "";
            try
            {
                before = Prints(app);
                o.OpenDocuments = before.Count;
                try { identities = File.Exists(identityFile) ? File.ReadAllBytes(identityFile) : null; identitiesKnown = true; } catch (Exception ex) { o.Notes.Add("The central identity record could not be read before the test: " + ex.Message); }
                try { sharedParams = app.Application.SharedParametersFilename ?? ""; } catch { sharedParams = null; }
                using (ChangeTracker.KeepLastChange())
                using (Companion.ActivityHub.Isolated())
                {
                    var blank = new Holder();
                    try { RunTools(app, o, temp, blank); }
                    finally { CloseAndClean(app, o, blank, temp); }
                    template = blank.Template;
                }
            }
            finally
            {
                // The per-machine central identity record and the shared parameter file setting, exactly as before.
                try
                {
                    if (identitiesKnown && identities != null) File.WriteAllBytes(identityFile, identities);
                    else if (identitiesKnown && File.Exists(identityFile)) File.Delete(identityFile);
                }
                catch (Exception ex) { o.Notes.Add("The central identity record could not be put back: " + ex.Message); }
                try { if (sharedParams != null && !string.Equals(app.Application.SharedParametersFilename ?? "", sharedParams, StringComparison.OrdinalIgnoreCase)) app.Application.SharedParametersFilename = sharedParams; }
                catch (Exception ex) { o.Notes.Add("The shared parameter file setting could not be put back: " + ex.Message); }
                try { InstanceRegistry.Update(app); } catch { /* the instance file follows on the next command */ }
                _running = false;
            }

            // The proof: every other open document exactly as before.
            var sw = Stopwatch.StartNew();
            var changes = L.Compare(before, Prints(app));
            o.Results.Add(new L.ToolResult
            {
                Tool = "open models untouched", Status = changes.Count == 0 ? L.Pass : L.Fail, Seconds = sw.Elapsed.TotalSeconds,
                Reason = changes.Count == 0 ? null : string.Join("; ", changes),
            });
            if (before.Count == 0) o.Notes.Add("No other document was open: the untouched check had nothing to compare (open a model to include it).");
            o.Seconds = total.Elapsed.TotalSeconds;
            o.Passed = L.Passed(o.Results);
            o.OverallLine = L.Overall(o.Results, o.Seconds);
            var header = new[]
            {
                "ACE self-test",
                $"ACE {Rules.ParameterFill.AceVersion()} in Revit {Safe(() => app.Application.VersionNumber)} ({Safe(() => app.Application.VersionBuild)})",
                $"Started {at:yyyy-MM-dd HH:mm:ss}; blank project {(string.IsNullOrEmpty(template) ? "from no template (metric)" : "from " + template)}; {before.Count} other open document(s) checked",
            };
            o.Text = L.Report(header, o.Results, o.Seconds, o.Notes);
            try
            {
                var dir = Path.Combine(AceConfig.Directory, "self-test");
                Directory.CreateDirectory(dir);
                o.ReportPath = Path.Combine(dir, L.FileName(at));
                File.WriteAllText(o.ReportPath, o.Text);
            }
            catch (Exception ex) { o.Notes.Add("The report file could not be written: " + ex.Message); o.ReportPath = null; }
            Log.Info($"Self-test: {o.OverallLine}" + (o.ReportPath != null ? $" ({o.ReportPath})" : ""));
            return o;
        }

        // ---- the tools ---------------------------------------------------------------------------------------------------

        private sealed class Fixtures
        {
            public ElementId Level0, Level1;
            public List<ElementId> Walls = new List<ElementId>();
            public ElementId Floor, Beam, Box, UnusedMaterial, UnusedType;
            /// <summary>Category (OST name) per fixture element, for the workset rules and the read-back.</summary>
            public Dictionary<ElementId, string> Category = new Dictionary<ElementId, string>();
        }

        /// <summary>The blank project, held from the moment Revit creates it, so it is always closed.</summary>
        private sealed class Holder
        {
            public Document Doc;
            public string Template = "";
        }

        private static void RunTools(UIApplication app, Outcome o, string temp, Holder blank)
        {
            Document doc = null;
            Fixtures fx = null;
            Tool(o, "blank project", () =>
            {
                Directory.CreateDirectory(temp);
                doc = blank.Doc = NewBlank(app, o, out blank.Template);
                fx = BuildFixtures(app, doc, o);
                return null;
            });
            const string noBlank = "the blank project could not be made (see the blank project line)";
            if (doc == null || fx == null)
            {
                foreach (var t in new[] { "save as", "save as central", "workset apply", "parameter fill", "purge unused", "deletion probe" })
                    o.Results.Add(new L.ToolResult { Tool = t, Status = L.Skip, Reason = noBlank });
                return;
            }

            // save_model save_as: its gate is the user's consent (allow_risky); it refuses a preview and a call without it.
            var plainPath = Path.Combine(temp, Name + ".rvt");
            var saved = false;
            Tool(o, "save as", () =>
            {
                var req = new JsonObject { ["save_as"] = new JsonObject { ["path"] = plainPath } };
                // The refusals are asked of the bridge command with a model name no document has, so even a broken refusal
                // could never save another model: it would stop at "no open model".
                var nowhere = With(req, ("model", Name + " (no such model) " + Guid.NewGuid().ToString("N")));
                if (Refuses(() => ModelFileCommands.Save(app, With(nowhere, ("dry_run", true))), "cannot be previewed") is string r1) return "a dry run was not refused: " + r1;
                if (Refuses(() => ModelFileCommands.Save(app, nowhere), "allow_risky") is string r2) return "a save without allow_risky was not refused: " + r2;
                JsonObject res;
                using (new ModelGuard(app)) res = ModelFileCommands.SaveDocument(app, doc, With(req, ("allow_risky", true)), clearPending: false) as JsonObject;
                if (res?["saved"]?.GetValue<bool>() != true) return "save_as did not report saved";
                if (!File.Exists(plainPath)) return "the file was not written: " + plainPath;
                if (!SamePath(doc.PathName, plainPath)) return $"the model is not that file afterwards ({doc.PathName})";
                saved = true;
                return null;
            });

            var central = false;
            var centralPath = Path.Combine(temp, Name + "_central.rvt");
            Tool(o, "save as central", () =>
            {
                using (new ModelGuard(app)) doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                if (!doc.IsWorkshared) return "worksharing could not be enabled on the blank project";
                JsonObject res;
                using (new ModelGuard(app)) res = ModelFileCommands.SaveDocument(app, doc, new JsonObject { ["allow_risky"] = true, ["save_as"] = new JsonObject { ["path"] = centralPath, ["as_central"] = true } }, clearPending: false) as JsonObject;
                if (res?["kind"]?.ToString() != "save_as central") return "save_as did not report a central";
                if (!File.Exists(centralPath)) return "the central file was not written: " + centralPath;
                string c;
                try { c = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch (Exception ex) { return "the central path could not be read: " + ex.Message; }
                if (!SamePath(c, centralPath)) return $"the model's central is {c}, not the saved file";
                central = true;
                return null;
            }, saved ? null : "needs the save as step (worksharing needs a saved file)");

            using (Args.Scoped(doc))
            {
                Tool(o, "workset apply", () => WorksetApply(app, doc, fx), central ? null : "needs a workshared blank project (the save as central step)");
                Tool(o, "parameter fill", () => ParameterFillTest(app, doc, fx, temp));
                Tool(o, "purge unused", () => PurgeTest(app, doc, fx));
            }
            Tool(o, "deletion probe", () => ProbeTest(doc, fx));
        }

        private static string WorksetApply(UIApplication app, Document doc, Fixtures fx)
        {
            var target = new Dictionary<string, string> { ["OST_Walls"] = "TST_Walls", ["OST_Floors"] = "TST_Floors", ["OST_StructuralFraming"] = "TST_Framing", ["OST_GenericModel"] = "TST_Generic" };
            var present = fx.Category.Values.Distinct().Where(target.ContainsKey).ToList();
            var rules = new JsonArray(present.Select(c => (JsonNode)new JsonObject { ["workset"] = target[c], ["category"] = c }).ToArray());
            var req = new JsonObject { ["rules"] = rules, ["create_missing"] = true, ["rules_name"] = "ACE self-test", ["rules_source"] = "self-test" };
            var (applied, fail) = Gated(a => Rules.WorksetCommands.Run(app, a), req, doc.Title, checkFirst: true);
            if (fail != null) return fail;
            if (applied["complete"] is JsonValue cv && cv.TryGetValue<bool>(out var complete) && !complete) return "the after-check says not done: " + (applied["error"] ?? "");
            if (!(applied["afterCheck"] is JsonObject ac) || !(ac["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var acOk) && acOk)) return "the after-check did not pass: " + (applied["afterCheck"]?["error"] ?? applied["error"] ?? "no after-check in the reply");
            // ACE's own read-back: every fixture element on its target workset.
            var table = doc.GetWorksetTable();
            var wrong = new List<string>();
            foreach (var kv in fx.Category.Where(k => target.ContainsKey(k.Value)))
            {
                var e = doc.GetElement(kv.Key);
                var name = e == null ? "(missing)" : table.GetWorkset(e.WorksetId)?.Name;
                if (!string.Equals(name, target[kv.Value], StringComparison.Ordinal)) wrong.Add($"{kv.Value} {kv.Key.Value} on {name ?? "?"}");
            }
            return wrong.Count == 0 ? null : "read-back: not on the target workset: " + string.Join(", ", wrong.Take(5));
        }

        private static string ParameterFillTest(UIApplication app, Document doc, Fixtures fx, string temp)
        {
            var cat = fx.Category.Values.Contains("OST_Walls") ? "OST_Walls" : "OST_GenericModel";
            var ids = fx.Category.Where(k => k.Value == cat).Select(k => k.Key).ToList();
            if (ids.Count == 0) return "no fixture element to fill";
            var file = Path.Combine(temp, Name + "_shared_parameters.txt");
            File.WriteAllText(file, string.Join("\r\n", new[]
            {
                "# This is a Revit shared parameter file.", "# Do not edit manually.",
                "*META\tVERSION\tMINVERSION", "META\t2\t1",
                "*GROUP\tID\tNAME", "GROUP\t1\tTST_ACE_SelfTest",
                "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE",
                $"PARAM\t{ParamGuid:D}\t{ParamName}\tTEXT\t\t1\t1\tACE self-test\t1\t0", "",
            }));
            var row = new JsonObject { ["key"] = "tst-1", ["name"] = ParamName, ["group"] = "elements", ["level"] = "instance", ["categories"] = new JsonArray(cat), ["guid"] = ParamGuid.ToString("D") };
            var bind = new JsonObject { ["name"] = ParamName, ["guid"] = ParamGuid.ToString("D"), ["file"] = file, ["dataType"] = "TEXT", ["group"] = "elements", ["level"] = "instance", ["categories"] = new JsonArray(cat) };
            var req = new JsonObject { ["rows"] = new JsonArray(row), ["bind"] = new JsonArray(bind), ["fill"] = true, ["value"] = "NA" };
            var (applied, fail) = Gated(a => Rules.ParameterFill.Run(app, a), req, doc.Title, checkFirst: true);
            if (fail != null) return fail;
            if (applied["verify"] is JsonObject v && v["ok"] is JsonValue vo && vo.TryGetValue<bool>(out var vok) && !vok) return "the self-check failed: " + v.ToJsonString();
            var notNa = ids.Select(doc.GetElement).Where(e => e?.get_Parameter(ParamGuid)?.AsString() != "NA").Select(e => e?.Id.Value.ToString() ?? "(missing)").ToList();
            return notNa.Count == 0 ? null : $"read-back: {ParamName} is not NA on {notNa.Count} element(s): {string.Join(", ", notNa.Take(5))}";
        }

        private static string PurgeTest(UIApplication app, Document doc, Fixtures fx)
        {
            var req = new JsonObject { ["max_passes"] = 3 };
            var (applied, fail) = Gated(a => PurgeUnused.Run(app, a), req, doc.Title, checkFirst: true);
            if (fail != null) return fail;
            var total = applied["totalDeleted"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 0;
            if (total <= 0) return "the apply deleted nothing, though the fixtures include unused elements";
            var left = new[] { fx.UnusedMaterial, fx.UnusedType }.Where(id => id != null && doc.GetElement(id) != null).Select(id => id.Value.ToString()).ToList();
            if (left.Count > 0) return "read-back: the unused fixtures are still there: " + string.Join(", ", left);
            var used = fx.Category.Keys.Where(id => doc.GetElement(id) == null).Select(id => id.Value.ToString()).ToList();
            return used.Count == 0 ? null : "read-back: placed elements were purged: " + string.Join(", ", used.Take(5));
        }

        private static string ProbeTest(Document doc, Fixtures fx)
        {
            if (doc.IsModifiable) return "a transaction is open in the blank project";
            var hosted = fx.Walls.Count > 0 || fx.Floor != null;
            var candidates = new List<long> { fx.Box.Value };
            if (hosted) candidates.Add(fx.Level0.Value);
            long Count() => new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
            var before = Count();
            var rep = DeletionProbe.Run(doc, candidates, 20);
            if (Count() != before) return $"the probe changed the model: {before} elements before, {Count()} after";
            if (candidates.Any(id => doc.GetElement(new ElementId(id)) == null)) return "a probed element is gone after the probe";
            var box = rep.Result.Outcomes.FirstOrDefault(x => x.Id == fx.Box.Value);
            if (box?.Status != DeletionProbeLogic.OnlyTarget) return $"the generic model box should delete only itself, the probe says {box?.Status ?? "nothing"}";
            if (hosted)
            {
                var level = rep.Result.Outcomes.FirstOrDefault(x => x.Id == fx.Level0.Value);
                if (level?.Status != DeletionProbeLogic.TakesOthers) return $"the level should take the elements on it along, the probe says {level?.Status ?? "nothing"}";
                var hostedIds = fx.Walls.Concat(new[] { fx.Floor }).Where(i => i != null).Select(i => i.Value).ToList();
                if (!hostedIds.Any(level.Others.Contains)) return "the level's others do not include the walls or floor on it";
            }
            return null;
        }

        // ---- the gate, as the server applies it ------------------------------------------------------------------------

        /// <summary>
        /// check_only (when the tool has one), the dry run (must succeed, rolled back, its self-check passed), then the
        /// identical request applied with the dry run's model and planHash (must commit with the same planHash). The
        /// tool's own preview, card and identity rules run unchanged: the self-test calls the bridge command itself.
        /// </summary>
        private static (JsonObject Applied, string Fail) Gated(Func<JsonObject, JsonNode> command, JsonObject request, string model, bool checkFirst)
        {
            if (checkFirst)
            {
                var pre = command(With(request, ("check_only", true), ("dry_run", false))) as JsonObject;
                if (pre == null || (pre["success"] is JsonValue ps && ps.TryGetValue<bool>(out var pok) && !pok)) return (null, "the check failed: " + Why(pre));
            }
            var dry = command(With(request, ("dry_run", true), ("expect_model", model))) as JsonObject;
            if (!Ok(dry)) return (null, "the dry run failed: " + Why(dry));
            if (dry["transaction"]?.ToString() != "RolledBack") return (null, $"the dry run was not rolled back ({dry["transaction"]})");
            if (dry["verify"] is JsonObject dv && dv["ok"] is JsonValue dvo && dvo.TryGetValue<bool>(out var dok) && !dok) return (null, "the dry run's self-check failed: " + dv.ToJsonString());
            var hash = dry["planHash"]?.ToString();
            var apply = With(request, ("dry_run", false), ("expect_model", model));
            if (!string.IsNullOrEmpty(hash)) apply["expect_plan"] = hash;
            var applied = command(apply) as JsonObject;
            if (applied?["alreadyApplied"] is JsonValue aa && aa.TryGetValue<bool>(out var already) && already) return (null, "the apply was refused as already applied: " + Why(applied));
            if (!Ok(applied)) return (null, "the identical apply failed: " + Why(applied));
            if (applied["transaction"]?.ToString() != "Committed") return (null, $"the apply did not commit ({applied["transaction"]})");
            var appliedHash = applied["planHash"]?.ToString();
            if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(appliedHash) && !string.Equals(hash, appliedHash, StringComparison.OrdinalIgnoreCase))
                return (null, $"the apply's plan ({appliedHash}) is not the dry run's ({hash})");
            return (applied, null);
        }

        private static bool Ok(JsonObject r) => r?["success"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private static string Why(JsonObject r)
        {
            if (r == null) return "no reply";
            var parts = new List<string>();
            foreach (var k in new[] { "error", "stage" }) if (r[k] != null) parts.Add(r[k].ToString());
            if (r["blocked"] is JsonArray b && b.Count > 0) parts.Add("blocked: " + b[0]?["why"]);
            if (r["revitErrors"] is JsonArray e && e.Count > 0) parts.Add("Revit: " + e[0]);
            if (r["verify"] is JsonObject v && v["error"] != null) parts.Add("self-check: " + v["error"]);
            if (parts.Count == 0 && r["note"] != null) parts.Add(r["note"].ToString());
            return parts.Count == 0 ? "no reason given" : string.Join("; ", parts);
        }

        private static JsonObject With(JsonObject request, params (string Key, JsonNode Value)[] set)
        {
            var copy = (JsonObject)request.DeepClone();
            foreach (var (k, v) in set) copy[k] = v;
            return copy;
        }

        /// <summary>Null when the call was refused with a message containing <paramref name="why"/>, else what happened.</summary>
        private static string Refuses(Func<JsonNode> call, string why)
        {
            try { return call()?.ToJsonString() ?? "no reply"; }
            catch (CommandException ex) { return ex.Message.IndexOf(why, StringComparison.OrdinalIgnoreCase) >= 0 ? null : "refused for another reason: " + ex.Message; }
        }

        private static void Tool(Outcome o, string name, Func<string> run, string skip = null)
        {
            if (skip != null) { o.Results.Add(new L.ToolResult { Tool = name, Status = L.Skip, Reason = skip }); return; }
            var sw = Stopwatch.StartNew();
            string fail;
            try { fail = run(); }
            catch (Exception ex) { fail = ex.Message; Log.Warn($"Self-test {name}: {ex}"); }
            o.Results.Add(new L.ToolResult { Tool = name, Status = fail == null ? L.Pass : L.Fail, Reason = fail, Seconds = sw.Elapsed.TotalSeconds });
        }

        // ---- the blank project and its fixtures ----------------------------------------------------------------------

        private static Document NewBlank(UIApplication app, Outcome o, out string template)
        {
            template = "";
            string tpl = null;
            try { tpl = app.Application.DefaultProjectTemplate; } catch { tpl = null; }
            if (!string.IsNullOrWhiteSpace(tpl) && File.Exists(tpl))
            {
                try
                {
                    Document d;
                    using (new ModelGuard(app)) d = app.Application.NewProjectDocument(tpl);
                    if (d != null) { template = Path.GetFileName(tpl); return d; }
                }
                catch (Exception ex) { o.Notes.Add($"The default template {tpl} could not be used ({ex.Message}): the blank project is metric with no template."); }
            }
            else o.Notes.Add(string.IsNullOrWhiteSpace(tpl) ? "No default project template is set in Revit: the blank project is metric with no template." : $"The default project template {tpl} does not exist: the blank project is metric with no template.");
            using (new ModelGuard(app)) return app.Application.NewProjectDocument(UnitSystem.Metric) ?? throw new CommandException("Revit did not create a blank project.");
        }

        private static double Mm(double mm) => mm / 304.8;

        private static Fixtures BuildFixtures(UIApplication app, Document doc, Outcome o)
        {
            var fx = new Fixtures();
            using (var guard = new ModelGuard(app))
            using (var t = new Transaction(doc, "ACE self-test fixtures"))
            {
                t.Start();
                var l0 = Level.Create(doc, 0);
                var l1 = Level.Create(doc, Mm(3000));
                try { l0.Name = "TST_Level 0"; l1.Name = "TST_Level 1"; } catch { /* names are cosmetic */ }
                fx.Level0 = l0.Id; fx.Level1 = l1.Id;
                var p = new[] { new XYZ(0, 0, 0), new XYZ(Mm(10000), 0, 0), new XYZ(Mm(10000), Mm(6000), 0), new XYZ(0, Mm(6000), 0) };
                var lines = Enumerable.Range(0, 4).Select(i => Line.CreateBound(p[i], p[(i + 1) % 4])).ToList();
                try
                {
                    foreach (var line in lines) { var w = Wall.Create(doc, line, l0.Id, false); fx.Walls.Add(w.Id); fx.Category[w.Id] = "OST_Walls"; }
                }
                catch (Exception ex) { o.Notes.Add("No walls in the blank project (" + ex.Message + ")."); }
                try
                {
                    var ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(f => !f.IsFoundationSlab);
                    if (ft == null) o.Notes.Add("No floor type in the blank project: no floor fixture.");
                    else
                    {
                        var loop = CurveLoop.Create(lines.Cast<Curve>().ToList());
                        var f = Floor.Create(doc, new List<CurveLoop> { loop }, ft.Id, l0.Id);
                        fx.Floor = f.Id; fx.Category[f.Id] = "OST_Floors";
                    }
                }
                catch (Exception ex) { o.Notes.Add("No floor in the blank project (" + ex.Message + ")."); }
                try
                {
                    var sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_StructuralFraming).Cast<FamilySymbol>().FirstOrDefault();
                    if (sym == null) o.Notes.Add("No structural framing family in the blank project: a generic model box stands in.");
                    else
                    {
                        if (!sym.IsActive) sym.Activate();
                        var beam = doc.Create.NewFamilyInstance(Line.CreateBound(new XYZ(0, Mm(3000), Mm(3000)), new XYZ(Mm(10000), Mm(3000), Mm(3000))), sym, l1, StructuralType.Beam);
                        fx.Beam = beam.Id; fx.Category[beam.Id] = "OST_StructuralFraming";
                    }
                }
                catch (Exception ex) { o.Notes.Add("No beam in the blank project (" + ex.Message + ")."); }
                // Always: a generic model box (DirectShape), the probe's "only the target" case.
                var box = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                var b = new[] { new XYZ(Mm(2000), Mm(2000), 0), new XYZ(Mm(3000), Mm(2000), 0), new XYZ(Mm(3000), Mm(3000), 0), new XYZ(Mm(2000), Mm(3000), 0) };
                var boxLoop = CurveLoop.Create(Enumerable.Range(0, 4).Select(i => (Curve)Line.CreateBound(b[i], b[(i + 1) % 4])).ToList());
                box.SetShape(new List<GeometryObject> { GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { boxLoop }, XYZ.BasisZ, Mm(1000)) });
                try { box.SetName("TST_SelfTest_Box"); } catch { /* cosmetic */ }
                fx.Box = box.Id; fx.Category[box.Id] = "OST_GenericModel";
                // Unused, for purge_unused: a material, and a wall type when there is one to copy.
                fx.UnusedMaterial = Material.Create(doc, "TST_SelfTest_Unused");
                try
                {
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(x => x.Kind == WallKind.Basic);
                    if (wt != null) fx.UnusedType = wt.Duplicate("TST_SelfTest_Unused_Wall").Id;
                }
                catch (Exception ex) { o.Notes.Add("No unused wall type fixture (" + ex.Message + ")."); }
                var status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new CommandException($"the fixtures were not created ({status}{(guard.Errors.Count > 0 ? ": " + guard.Errors[0] : "")})");
            }
            return fx;
        }

        // ---- closing, cleaning up, fingerprints --------------------------------------------------------------------------

        private static void CloseAndClean(UIApplication app, Outcome o, Holder blank, string temp)
        {
            var sw = Stopwatch.StartNew();
            var problems = new List<string>();
            var doc = blank.Doc;
            blank.Doc = null;
            if (doc != null)
            {
                try
                {
                    if (doc.IsValidObject)
                    {
                        bool closed;
                        using (new ModelGuard(app)) closed = doc.Close(false);
                        if (!closed) problems.Add("Revit did not close the blank project");
                    }
                }
                catch (Exception ex) { problems.Add("the blank project could not be closed: " + ex.Message); }
            }
            var deleted = DeleteFolder(temp);
            if (deleted != null) problems.Add(deleted);
            try
            {
                var root = Path.GetDirectoryName(temp);
                if (root != null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch { /* an empty %TEMP%\ACE-selftest may stay */ }
            o.Results.Add(new L.ToolResult
            {
                Tool = "clean-up (blank project closed unsaved, temp files deleted)", Status = problems.Count == 0 ? L.Pass : L.Fail,
                Reason = problems.Count == 0 ? null : string.Join("; ", problems), Seconds = sw.Elapsed.TotalSeconds,
            });
        }

        /// <summary>Null when the folder is gone (a few tries: Revit may release the files a moment after the close), else why not.</summary>
        private static string DeleteFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            Exception last = null;
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(folder, true); return null; }
                catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(200); }
            }
            return $"the temp folder {folder} could not be deleted ({last?.Message}); delete it by hand";
        }

        /// <summary>Every open document (taken before the blank project exists and after it is closed): identity, IsModified, counts, the type list hash.</summary>
        private static List<L.DocPrint> Prints(UIApplication app)
        {
            var list = new List<L.DocPrint>();
            foreach (Document d in app.Application.Documents)
            {
                var p = new L.DocPrint { Title = Safe(() => d.Title) ?? "(untitled)" };
                var path = Safe(() => d.PathName);
                p.Key = string.IsNullOrEmpty(path) ? p.Title : path;
                try { p.IsModified = d.IsModified; } catch { p.IsModified = true; }
                try { p.Elements = new FilteredElementCollector(d).WhereElementIsNotElementType().GetElementCount(); } catch { p.Elements = -1; }
                try
                {
                    var types = new FilteredElementCollector(d).WhereElementIsElementType().ToElements();
                    p.Types = types.Count;
                    p.TypeHash = L.TypeHash(types.Select(e => e.Id.Value + "|" + (Safe(() => e.Name) ?? "")));
                }
                catch { p.Types = -1; p.TypeHash = null; }
                list.Add(p);
            }
            return list;
        }

        private static IEnumerable<string> OpenTransactions(UIApplication app)
        {
            var list = new List<string>();
            foreach (Document d in app.Application.Documents)
                try { if (d.IsModifiable) list.Add(d.Title); } catch { /* unreadable: not counted */ }
            return list;
        }

        private static bool SamePath(string a, string b)
        {
            try { return !string.IsNullOrEmpty(a) && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
    }

    /// <summary>The ribbon's Self-test button: runs ace_self_test after a confirmation and shows one line per tool.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SelfTestCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var ask = new TaskDialog("ACE self-test")
            {
                MainInstruction = "Check every ACE write tool on a blank test project?",
                MainContent = "ACE creates its own blank project, runs each write tool on it (preview, then apply), closes it without saving and deletes its temp files. "
                    + "Your open models are not changed: ACE proves it before and after. It takes about a minute.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok,
            };
            if (ask.Show() != TaskDialogResult.Ok) return Result.Cancelled;
            try
            {
                var o = SelfTest.Execute(commandData.Application);
                var lines = string.Join("\n", o.Results.Select(SelfTestLogic.Line));
                new TaskDialog("ACE self-test")
                {
                    MainInstruction = o.OverallLine,
                    MainContent = lines + (o.ReportPath != null ? "\n\nReport: " + o.ReportPath : ""),
                    CommonButtons = TaskDialogCommonButtons.Close,
                }.Show();
                return Result.Succeeded;
            }
            catch (CommandException ex)
            {
                TaskDialog.Show("ACE self-test", ex.Message);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Error("Self-test: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
