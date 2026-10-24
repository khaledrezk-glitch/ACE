using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// 1.11.0: one plan per model (bind, derive, fill) as ONE native change. Blockers are checked first, before the
    /// transaction group opens (version, target elements borrowed or updated in central); then bindings, Regenerate,
    /// derived values and the fill value; after the writes and before the change is kept, every planned write is read
    /// again and any difference rolls the whole group back. Every block and skip says what, why, which elements, who
    /// holds them and what clears it.
    /// </summary>
    internal static partial class ParameterFill
    {
        /// <summary>A write the self-check reads again after the transaction: a value on an element, or a binding.</summary>
        private sealed class PlannedWrite
        {
            public ParameterPlanLogic.Expectation X;
            public long Id;
            public Guid? Guid;
            public Definition Def;
            public List<BuiltInCategory> Cats;   // bindings: the categories planned
            public string Level;                 // bindings: instance | type
            public bool ByStorage;               // rebind: compare by storage type (text, integer, number, element id)
        }

        internal static string AceVersion() => typeof(ParameterFill).Assembly.GetName().Version?.ToString(3);

        private static JsonNode RunPlan(UIApplication app, JsonObject args, List<ParameterFillLogic.Row> rows, JsonArray skippedRows,
            int maxIds, ParameterFillLogic.Scope scope, IList<string> na)
        {
            if (Args.Bool(args, "all_documents")) throw new CommandException("A fill, derive or bind plan works on the active model only (one plan, one undo step per model): leave all_documents out (or false).");
            var doc = Args.RequireDoc(app);
            var aceVersion = AceVersion();
            var fill = Args.Bool(args, "fill");
            var value = Args.Str(args, "value") ?? "NA";
            if (fill && string.IsNullOrWhiteSpace(value)) throw new CommandException("value must not be empty (it is what blank text parameters are filled with, e.g. \"NA\").");
            var checkOnly = Args.Bool(args, "check_only");

            JsonObject Refuse(string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = "blocked", ["error"] = error, ["blocked"] = blocked,
                ["model"] = doc.Title, ["modelPath"] = doc.PathName, ["aceVersion"] = aceVersion,
            };

            // Blockers first (fail fast): the version, before anything is read.
            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
                return Refuse($"Nothing was changed: the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}.",
                    new JsonArray(ParameterPlanLogic.Entry("the whole plan", "version", null, $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}", 0, null)));

            var deriveProblems = new JsonArray();
            var rules = ParameterPlanLogic.ParseDerive(args["derive"] as JsonArray, deriveProblems);
            var bindProblems = new JsonArray();
            var binds = ParameterPlanLogic.ParseBind(args["bind"] as JsonArray, bindProblems);
            var deriveTargets = new HashSet<string>(rules.Select(r => r.Target), StringComparer.Ordinal);
            var rebindProblems = new JsonArray();
            var rebinds = ParameterPlanLogic.ParseRebind(args["rebind"] as JsonArray, rebindProblems);
            var bindNames = new HashSet<string>(binds.Select(b => b.Name).Concat(rebinds.Select(r => r.Name)), StringComparer.Ordinal);
            // Acceptance test (W10): only the apply's self-check is forced to fail, so the identical dry run can pass the gate.
            var forceMismatch = Args.Bool(args, "test_force_verify_mismatch") && !Args.Bool(args, "dry_run");

            // The state before any write (read-only, outside any transaction): blockers, impact (opens families), register.
            var pre = new Scan(doc, rows, maxIds, scope, na);
            var blocked = pre.Blockers(fill, deriveTargets, bindNames);
            // Rebinds: the old definition's values and usages, read now (outside the transaction); held values block.
            var rebindHeld = new List<ParameterPlanLogic.Skip>();
            var rebindPlans = rebinds.Select(r => pre.PrepareRebind(r, rebindHeld)).ToList();
            foreach (var b in ParameterPlanLogic.Group(rebindHeld, "holding values to carry over held", maxIds)) blocked.Add(b?.DeepClone());
            JsonObject oldImpact = null;
            if (rebindPlans.Any(rp => rp.Old != null))
            {
                oldImpact = ParameterImpact.Scan(doc, rebindPlans.Where(rp => rp.Old != null).Select(rp => new ParameterImpact.Target
                {
                    Name = rp.Entry.Name, Key = rp.Entry.Name + " (old definition)", Guid = rp.Entry.OldGuid, Ids = new HashSet<long> { rp.Old.Id.Value },
                    Note = "Old definition of a rebind: Revit removes these schedule fields and view filter rules when the old definition is deleted; labels keep the old GUID and show nothing until the families are updated.",
                }).ToList());
                foreach (var rp in rebindPlans.Where(rp => rp.Old != null)) rp.Dropped = oldImpact[rp.Entry.Name + " (old definition)"] as JsonObject;
            }
            var impact = Args.Bool(args, "impact") ? ParameterImpact.Scan(doc, pre.ImpactTargets(deriveTargets, binds)) : null;
            if (impact != null && oldImpact != null) foreach (var kv in oldImpact) if (!impact.ContainsKey(kv.Key)) impact[kv.Key] = kv.Value?.DeepClone();
            var register = Args.Bool(args, "register") ? pre.Register() : null;
            void Extras(JsonObject r)
            {
                r["blocked"] = blocked.DeepClone();
                if (impact != null) r["impact"] = impact.DeepClone();
                if (register != null) r["register"] = register.DeepClone();
                r["aceVersion"] = aceVersion;
            }
            if (blocked.Count > 0 && Args.Bool(args, "block_on_borrowed", true) && !checkOnly)
            {
                var n = blocked.Sum(b => b?["elements"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0);
                var refused = Refuse($"Nothing was changed: {n} element{(n == 1 ? "" : "s")} this plan would write cannot be edited now ({blocked[0]?["why"]}). "
                    + "See blocked for who holds them and what clears it, or run with block_on_borrowed: false to write the rest.", blocked);
                refused["documents"] = new JsonArray(pre.Report(true));
                Extras(refused);
                return refused;
            }

            var parts = new List<string>();
            if (binds.Count > 0) parts.Add($"bind {binds.Count}");
            if (rebinds.Count > 0) parts.Add($"rebind {rebinds.Count}");
            if (rules.Count > 0) parts.Add($"derive {rules.Count}");
            if (fill) parts.Add($"fill '{value}'");
            var title = binds.Count == 0 && rules.Count == 0 && rebinds.Count == 0 ? $"ACE: fill blank parameters with '{value}'" : $"ACE: parameter plan ({string.Join(", ", parts)})";

            var writes = new List<PlannedWrite>();
            var result = NativeChange.Run(app, args, "parameter_fill", title, (d, plan) =>
            {
                writes.Clear();
                var r = new JsonObject
                {
                    ["model"] = d.Title,
                    ["modelPath"] = d.PathName,
                    ["documents"] = new JsonArray(pre.Report(true)),   // the state before the writes
                    ["skippedRows"] = skippedRows.DeepClone(),
                };
                var fs = new FileSession(app.Application);
                JsonObject bindings, rebound;
                try
                {
                    bindings = Bind(app, d, binds, bindProblems, plan, writes, pre.BuiltInNames(), fs);
                    rebound = rebinds.Count > 0 || rebindProblems.Count > 0 ? Rebind(app, d, rebindPlans, rebindProblems, plan, writes, fs) : null;
                }
                finally { fs.Restore(); }
                r["bindings"] = bindings;
                if (rebound != null) r["rebound"] = rebound;
                if (fs.Restored.HasValue) r["sharedParameterFileRestored"] = fs.Restored.Value;
                int Done(JsonObject o) => o?["done"] is JsonValue dv && dv.TryGetValue<int>(out var n) ? n : 0;
                var work = pre;
                if (!plan && Done(bindings) + Done(rebound) > 0)
                {
                    d.Regenerate();
                    work = new Scan(d, rows, maxIds, scope, na);   // the newly bound parameters are there now (blank)
                }
                r["derived"] = work.Derive(rules, deriveProblems, plan, writes);
                if (fill) r["fill"] = work.Fill(value, plan, writes);
                var after = plan ? null : work.After(deriveTargets, bindNames);
                if (fill) r["fill"]["after"] = after?.DeepClone();
                r["after"] = after;
                if (plan && binds.Count > 0)
                    r["planNote"] = "Look-only plan: the derived and fill counts leave out elements that get a parameter only through the planned bindings; the dry run counts them.";
                return r;
            }, verify: (d, r) =>
            {
                var v = VerifyWrites(d, writes, forceMismatch);
                r["verify"] = v;
                return v["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && b;
            });
            if (!result.ContainsKey("verify")) result["verify"] = null;
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled) MarkRolledBack(result);
            Extras(result);
            return result;
        }

        /// <summary>
        /// A rolled-back plan reports nothing as done: set / done become 0 and the counts move to wouldSet / wouldDo; the
        /// end state read inside the transaction moves to afterIfKept (the model is as it was).
        /// </summary>
        private static void MarkRolledBack(JsonObject r)
        {
            void Move(JsonNode node, string from, string to)
            {
                if (node is JsonObject o && o[from] is JsonValue v && v.TryGetValue<int>(out var n)) { o[to] = n; o[from] = 0; }
            }
            Move(r["fill"], "set", "wouldSet");
            if (r["derived"] is JsonArray derived) foreach (var d in derived) Move(d, "set", "wouldSet");
            Move(r["bindings"], "done", "wouldDo");
            Move(r["rebound"], "done", "wouldDo");
            if (r["fill"] is JsonObject f && f["after"] != null) { f["afterIfKept"] = f["after"].DeepClone(); f["after"] = null; }
            if (r["after"] != null) { r["afterIfKept"] = r["after"].DeepClone(); r["after"] = null; }
        }

        /// <summary>Reads every planned write again (after the transaction committed, before it is kept).</summary>
        private static JsonObject VerifyWrites(Document doc, List<PlannedWrite> writes, bool forceMismatch)
        {
            var list = new List<ParameterPlanLogic.Expectation>();
            Dictionary<Guid, ElementBinding> byGuid = null;
            foreach (var w in writes)
            {
                var x = w.X;
                try
                {
                    if (x.Kind == "binding")
                    {
                        byGuid ??= BindingsByGuid(doc);
                        if (!w.Guid.HasValue || !byGuid.TryGetValue(w.Guid.Value, out var b)) { x.Found = false; x.Actual = "not bound"; }
                        else x.Actual = BindingText(b is InstanceBinding ? "instance" : "type", w.Cats.Where(c => Binds(b, c)));
                    }
                    else
                    {
                        var e = doc.GetElement(new ElementId(w.Id));
                        var p = e == null ? null : w.Guid.HasValue ? e.get_Parameter(w.Guid.Value) : e.get_Parameter(w.Def);
                        if (p == null) x.Found = false;
                        else x.Actual = w.ByStorage ? ValueText(p) : p.AsString() ?? "";
                    }
                }
                catch (Exception ex) { x.Found = false; x.Actual = "unreadable: " + ex.Message; }
                list.Add(x);
            }
            if (forceMismatch) list.Add(ParameterPlanLogic.ForcedMismatch());
            return ParameterPlanLogic.Verify(list);
        }

        private static string BindingText(string level, IEnumerable<BuiltInCategory> cats) =>
            level + ": " + string.Join(", ", cats.Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal));

        private static bool Binds(ElementBinding b, BuiltInCategory bic)
        {
            foreach (Category c in b.Categories) if (c.BuiltInCategory == bic) return true;
            return false;
        }

        /// <summary>Shared parameter bindings of the model by GUID (project parameters have none).</summary>
        private static Dictionary<Guid, ElementBinding> BindingsByGuid(Document doc)
        {
            var map = new Dictionary<Guid, ElementBinding>();
            foreach (var (def, b) in AllBindings(doc))
                if (doc.GetElement(def.Id) is SharedParameterElement spe && !map.ContainsKey(spe.GuidValue)) map[spe.GuidValue] = b;
            return map;
        }

        private static List<(InternalDefinition Def, ElementBinding Binding)> AllBindings(Document doc)
        {
            var list = new List<(InternalDefinition, ElementBinding)>();
            var it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
                if (it.Key is InternalDefinition d && it.Current is ElementBinding b) list.Add((d, b));
            return list;
        }

        // ---- P5 bindings ----------------------------------------------------------------------------------------

        /// <summary>
        /// The shared parameter file setting (an application setting the user sees): changed only for a real bind, never
        /// in a look-only plan, and restored afterwards; Restored says whether the restore took.
        /// </summary>
        internal sealed class FileSession
        {
            private readonly Autodesk.Revit.ApplicationServices.Application _app;
            private string _original;
            private bool _changed;
            public bool? Restored;

            public FileSession(Autodesk.Revit.ApplicationServices.Application app) { _app = app; }

            public DefinitionFile Open(string file)
            {
                if (!_changed) { _original = _app.SharedParametersFilename; _changed = true; }
                _app.SharedParametersFilename = file;
                return _app.OpenSharedParameterFile();
            }

            public void Restore()
            {
                if (!_changed) return;
                _changed = false;
                try { _app.SharedParametersFilename = _original ?? ""; } catch (Exception ex) { Util.Log.Warn($"Shared parameter file setting not restored: {ex.Message}"); }
                string now;
                try { now = _app.SharedParametersFilename ?? ""; } catch { now = null; }
                Restored = now != null && string.Equals(now, _original ?? "", StringComparison.OrdinalIgnoreCase);
                if (Restored == false) Util.Log.Warn($"Shared parameter file setting is '{now}', it was '{_original}' before ACE bound parameters.");
            }
        }

        /// <summary>
        /// The definition with this GUID from the given file only: the file must exist and (when the preview sent its
        /// hash) be unchanged; the GUID, name and (TEXT) data type are checked from the file's own lines, without Revit.
        /// Look-only plans stop there; otherwise Revit reads the file for the ExternalDefinition.
        /// </summary>
        private static bool DefinitionFromFile(FileSession fs, string file, string fileHash, Guid guid, string name, string dataType, bool plan,
            out ExternalDefinition ext, out string code, out string detail)
        {
            ext = null; code = null; detail = null;
            if (!System.IO.File.Exists(file)) { code = "file"; detail = $"'{file}' does not exist"; return false; }
            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(file); }
            catch (Exception ex) { code = "file"; detail = $"'{file}' could not be read: {ex.Message}"; return false; }
            if (!ParameterPlanLogic.HashMatches(fileHash, bytes)) { code = "file-changed"; detail = $"'{file}' changed since the preview (its sha256 differs)"; return false; }
            var line = ParameterPlanLogic.ReadSharedParameterFile(bytes).FirstOrDefault(x => x.Guid == guid);
            if (line == null) { code = "not-in-file"; detail = $"'{file}' has no definition with GUID {guid}"; return false; }
            if (name != null && !string.Equals(line.Name, name, StringComparison.Ordinal))
            { code = "guid-other-name"; detail = $"the file names GUID {guid} '{line.Name}', the requirement '{name}'"; return false; }
            if (string.Equals(dataType, "TEXT", StringComparison.OrdinalIgnoreCase) && !string.Equals(line.DataType, "TEXT", StringComparison.OrdinalIgnoreCase))
            { code = "datatype"; detail = $"the requirement says TEXT, the file defines '{line.Name}' as {line.DataType}"; return false; }
            if (plan) return true;
            try
            {
                var df = fs.Open(file);
                if (df == null) { code = "file"; detail = $"'{file}' could not be opened as a shared parameter file"; return false; }
                foreach (DefinitionGroup g in df.Groups)
                {
                    foreach (Definition d in g.Definitions)
                        if (d is ExternalDefinition x && x.GUID == guid) { ext = x; break; }
                    if (ext != null) break;
                }
            }
            catch (Exception ex) { code = "file"; detail = $"'{file}' could not be read by Revit: {ex.Message}"; return false; }
            if (ext == null) { code = "not-in-file"; detail = $"Revit did not read GUID {guid} from '{file}'"; return false; }
            return true;
        }

        /// <summary>OST names to categories that take bound parameters; the others in bad.</summary>
        private static List<Category> BindableCategories(Document doc, IEnumerable<string> names, List<string> bad)
        {
            var cats = new List<Category>();
            foreach (var name in names)
            {
                Category c = null;
                if (Enum.TryParse<BuiltInCategory>(name, false, out var bic) && bic != BuiltInCategory.INVALID) { try { c = Category.GetCategory(doc, bic); } catch { c = null; } }
                if (c != null && c.AllowsBoundParameters) { if (!cats.Any(x => x.Id == c.Id)) cats.Add(c); }
                else bad.Add(name);
            }
            return cats;
        }

        /// <summary>
        /// Binds each entry's definition from its shared parameter file (never a new GUID), or extends the categories of an
        /// existing binding of the same GUID. The same name bound with another GUID, the other level, a definition the
        /// file does not hold, a file changed since the preview or an unknown group are reported, not changed. plan:
        /// check everything, change nothing (the shared parameter file setting is not touched).
        /// </summary>
        private static JsonObject Bind(UIApplication app, Document doc, List<ParameterPlanLogic.BindEntry> binds, JsonArray problems, bool plan,
            List<PlannedWrite> writes, ICollection<string> builtIn, FileSession fs)
        {
            var skipped = new JsonArray();
            var items = new JsonArray();
            int planned = 0, done = 0;
            void Skip(string name, string code, string detail)
            {
                var entry = ParameterPlanLogic.Entry($"binding of '{name}'", code, null, detail, 0, null, name);
                entry["reason"] = entry["why"]?.ToString();
                skipped.Add(entry);
            }
            foreach (var p in problems) Skip(p?["name"]?.ToString(), "bind-entry", p?["reason"]?.ToString());
            if (binds.Count == 0) return new JsonObject { ["planned"] = 0, ["done"] = 0, ["skipped"] = skipped, ["items"] = items, ["note"] = null };

            var existing = AllBindings(doc).Select(x =>
            {
                Guid? g = null;
                try { if (doc.GetElement(x.Def.Id) is SharedParameterElement spe) g = spe.GuidValue; } catch { g = null; }
                return (x.Def, x.Binding, Guid: g);
            }).ToList();
            var application = app.Application;
            foreach (var entry in binds)
            {
                if (builtIn.Contains(entry.Name))
                { Skip(entry.Name, "built-in", $"'{entry.Name}' is a Revit built-in parameter: elements that lack it cannot get it from a binding"); continue; }
                var bad = new List<string>();
                var cats = BindableCategories(doc, entry.Categories, bad);
                if (bad.Count > 0) { Skip(entry.Name, "category", $"{string.Join(", ", bad)}: not a category that takes bound parameters in this model"); continue; }
                var wantInstance = entry.Level != "type";
                var level = wantInstance ? "instance" : "type";
                var bics = cats.Select(c => c.BuiltInCategory).ToList();

                var same = existing.FirstOrDefault(x => x.Guid == entry.Guid);
                if (same.Def == null)
                {
                    var other = existing.FirstOrDefault(x => string.Equals(x.Def.Name, entry.Name, StringComparison.Ordinal));
                    if (other.Def != null)
                    {
                        Skip(entry.Name, "other-guid", $"'{entry.Name}' is already bound with {(other.Guid.HasValue ? "GUID " + other.Guid.Value : "no GUID (a project parameter)")}; the requirement's GUID is {entry.Guid}");
                        continue;
                    }
                }
                else
                {
                    // The same GUID is bound: extend its categories (ReInsert), at the level it has.
                    if (!string.Equals(same.Def.Name, entry.Name, StringComparison.Ordinal))
                    { Skip(entry.Name, "guid-other-name", $"GUID {entry.Guid} is bound in this model as '{same.Def.Name}', the requirement calls it '{entry.Name}'"); continue; }
                    if ((same.Binding is InstanceBinding) != wantInstance)
                    { Skip(entry.Name, "wrong-level", $"bound as {(same.Binding is InstanceBinding ? "instance" : "type")}, the requirement needs {level}"); continue; }
                    var missing = cats.Where(c => !Binds(same.Binding, c.BuiltInCategory)).ToList();
                    if (missing.Count == 0) { items.Add(new JsonObject { ["name"] = entry.Name, ["guid"] = entry.Guid.ToString(), ["action"] = "none", ["note"] = "already bound to every category" }); continue; }
                    planned++;
                    items.Add(new JsonObject
                    {
                        ["name"] = entry.Name, ["guid"] = entry.Guid.ToString(), ["action"] = "extend", ["level"] = level,
                        ["categories"] = new JsonArray(missing.Select(c => (JsonNode)c.BuiltInCategory.ToString()).ToArray()),
                    });
                    if (plan) continue;
                    foreach (var c in missing) same.Binding.Categories.Insert(c);
                    bool ok;
                    try { ok = doc.ParameterBindings.ReInsert(same.Def, same.Binding); } catch (Exception ex) { Skip(entry.Name, "bind-failed", ex.Message); continue; }
                    if (!ok) { Skip(entry.Name, "bind-failed", null); continue; }
                    done++;
                    Expect(entry, level, bics);
                    continue;
                }

                // Not bound yet: the definition comes from the given file only.
                try
                {
                    if (SharedParameterElement.Lookup(doc, entry.Guid) is SharedParameterElement held && !string.Equals(held.Name, entry.Name, StringComparison.Ordinal))
                    { Skip(entry.Name, "guid-other-name", $"the model already holds GUID {entry.Guid} as '{held.Name}'"); continue; }
                }
                catch { /* lookup is a guard only */ }
                var group = GroupFor(entry.GroupTypeId);
                if (group == null) { Skip(entry.Name, "group-type", $"'{entry.GroupTypeId}' is not a Revit parameter group"); continue; }
                if (!DefinitionFromFile(fs, entry.File, entry.FileHash, entry.Guid, entry.Name, entry.DataType, plan, out var ext, out var code, out var detail))
                { Skip(entry.Name, code, detail); continue; }
                planned++;
                items.Add(new JsonObject
                {
                    ["name"] = entry.Name, ["guid"] = entry.Guid.ToString(), ["action"] = "bind", ["level"] = level, ["groupTypeId"] = group.Value.Label,
                    ["categories"] = new JsonArray(bics.Select(c => (JsonNode)c.ToString()).ToArray()), ["file"] = entry.File,
                });
                if (plan) continue;
                var set = application.Create.NewCategorySet();
                foreach (var c in cats) set.Insert(c);
                Binding nb = wantInstance ? application.Create.NewInstanceBinding(set) : (Binding)application.Create.NewTypeBinding(set);
                bool inserted;
                try { inserted = doc.ParameterBindings.Insert(ext, nb, group.Value.Id); } catch (Exception ex) { Skip(entry.Name, "bind-failed", ex.Message); continue; }
                if (!inserted) { Skip(entry.Name, "bind-failed", null); continue; }
                done++;
                Expect(entry, level, bics);
            }

            void Expect(ParameterPlanLogic.BindEntry entry, string level, List<BuiltInCategory> bics) => writes.Add(new PlannedWrite
            {
                X = new ParameterPlanLogic.Expectation { Kind = "binding", Key = entry.Name, Name = entry.Name, Expected = BindingText(level, bics) },
                Guid = entry.Guid, Cats = bics, Level = level,
            });

            return new JsonObject
            {
                ["planned"] = planned, ["done"] = done, ["skipped"] = skipped, ["items"] = items,
                ["note"] = IsWorkshared(doc) && planned > 0 ? "Workshared model: synchronize with central after applying, so the others get the bindings." : null,
            };
        }

        private static bool IsWorkshared(Document doc) { try { return doc.IsWorkshared; } catch { return false; } }

        /// <summary>
        /// The parameter group: General when not given; a GroupTypeId property name (IdentityData, Data, ...) or a
        /// ForgeTypeId string. Null when unknown (reported, never a silent General).
        /// </summary>
        internal static (ForgeTypeId Id, string Label)? GroupFor(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "General", StringComparison.OrdinalIgnoreCase)) return (GroupTypeId.General, "General");
            if (text.StartsWith("autodesk.", StringComparison.OrdinalIgnoreCase))
            {
                var id = new ForgeTypeId(text.Trim());
                try { LabelUtils.GetLabelForGroup(id); return (id, text.Trim()); } catch { return null; }
            }
            var prop = typeof(GroupTypeId).GetProperty(text.Replace(" ", ""), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (prop?.GetValue(null) is ForgeTypeId f) return (f, prop.Name);
            return null;
        }

        // ---- rebind (explicit only) ---------------------------------------------------------------------------

        /// <summary>One rebind, prepared before the transaction: the old definition, its binding and every value it holds.</summary>
        private sealed class RebindPlan
        {
            public ParameterPlanLogic.RebindEntry Entry;
            public SharedParameterElement Old;
            public InternalDefinition OldDef;
            public ElementBinding OldBinding;
            public List<Category> Cats = new List<Category>();
            public string Level;
            public List<(long Id, StorageType Storage, string S, int I, double D, long E, string Text)> Values = new List<(long, StorageType, string, int, double, long, string)>();
            public string Code, Detail;   // why it is not done (null: it can be)
            public JsonObject Dropped;    // the old definition's usages Revit removes with it
        }

        /// <summary>A value as text, for the self-check (exact: doubles round-trip).</summary>
        private static string ValueText(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString() ?? "";
                case StorageType.Integer: return p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double: return p.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.ElementId: return p.AsElementId().Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return "";
            }
        }

        /// <summary>
        /// Rebinds inside the plan's transaction: delete the old definition, bind the new one from the file (same checks as
        /// bind), Regenerate, write every value back. Every value is a planned write, so the self-check rolls the whole
        /// group back when one does not show. plan: check and count only.
        /// </summary>
        private static JsonObject Rebind(UIApplication app, Document doc, List<RebindPlan> plans, JsonArray problems, bool plan, List<PlannedWrite> writes, FileSession fs)
        {
            var skipped = new JsonArray();
            var items = new JsonArray();
            int planned = 0, done = 0, values = 0, dSchedules = 0, dFilters = 0, dLabels = 0;
            void Skip(string name, string code, string detail)
            {
                var entry = ParameterPlanLogic.Entry($"rebind of '{name}'", code, null, detail, 0, null, name);
                entry["reason"] = entry["why"]?.ToString();
                skipped.Add(entry);
            }
            foreach (var p in problems) Skip(p?["name"]?.ToString(), "rebind-entry", p?["reason"]?.ToString());
            var application = app.Application;
            foreach (var rp in plans)
            {
                var entry = rp.Entry;
                if (rp.Code != null) { Skip(entry.Name, rp.Code, rp.Detail); continue; }
                (ForgeTypeId Id, string Label)? group;
                if (string.IsNullOrWhiteSpace(entry.GroupTypeId))
                {
                    var keep = rp.OldDef.GetGroupTypeId();
                    group = (keep ?? GroupTypeId.General, keep == null || string.IsNullOrEmpty(keep.TypeId) ? "General" : keep.TypeId);
                }
                else group = GroupFor(entry.GroupTypeId);
                if (group == null) { Skip(entry.Name, "group-type", $"'{entry.GroupTypeId}' is not a Revit parameter group"); continue; }
                if (!DefinitionFromFile(fs, entry.File, entry.FileHash, entry.Guid, entry.Name, null, plan, out var ext, out var code, out var detail))
                { Skip(entry.Name, code, detail); continue; }
                if (!plan)
                {
                    var oldType = rp.OldDef.GetDataType();
                    var newType = ext.GetDataType();
                    if (oldType == null || newType == null || !newType.NameEquals(oldType))
                    { Skip(entry.Name, "rebind-datatype", $"the old definition is {SpecLabel(oldType)}, the new one {SpecLabel(newType)}"); continue; }
                }
                planned++;
                values += rp.Values.Count;
                dSchedules += (rp.Dropped?["schedules"] as JsonArray)?.Count ?? 0;
                dFilters += (rp.Dropped?["viewFilters"] as JsonArray)?.Count ?? 0;
                dLabels += (rp.Dropped?["labels"] as JsonArray)?.Count ?? 0;
                var bics = rp.Cats.Select(c => c.BuiltInCategory).ToList();
                items.Add(new JsonObject
                {
                    ["name"] = entry.Name, ["oldGuid"] = entry.OldGuid.ToString(), ["guid"] = entry.Guid.ToString(), ["level"] = rp.Level,
                    ["groupTypeId"] = group.Value.Label, ["categories"] = new JsonArray(bics.Select(c => (JsonNode)c.ToString()).ToArray()),
                    ["values"] = rp.Values.Count, ["file"] = entry.File, ["dropped"] = rp.Dropped?.DeepClone(),
                });
                if (plan) continue;

                // From here a failure must undo everything: throwing rolls the transaction and the group back.
                doc.Delete(rp.Old.Id);
                var set = application.Create.NewCategorySet();
                foreach (var c in rp.Cats) set.Insert(c);
                Binding nb = rp.Level == "instance" ? application.Create.NewInstanceBinding(set) : (Binding)application.Create.NewTypeBinding(set);
                bool inserted;
                try { inserted = doc.ParameterBindings.Insert(ext, nb, group.Value.Id) || doc.ParameterBindings.ReInsert(ext, nb, group.Value.Id); }
                catch (Exception ex) { throw new CommandException($"Rebind of '{entry.Name}': Revit did not accept the new binding ({ex.Message}). Everything was rolled back. Nothing was changed."); }
                if (!inserted) throw new CommandException($"Rebind of '{entry.Name}': Revit did not accept the new binding. Everything was rolled back. Nothing was changed.");
                doc.Regenerate();
                foreach (var v in rp.Values)
                {
                    var e = doc.GetElement(new ElementId(v.Id));
                    var p = e?.get_Parameter(entry.Guid);
                    try
                    {
                        if (p != null && !p.IsReadOnly)
                        {
                            switch (v.Storage)
                            {
                                case StorageType.String: p.Set(v.S); break;
                                case StorageType.Integer: p.Set(v.I); break;
                                case StorageType.Double: p.Set(v.D); break;
                                case StorageType.ElementId: p.Set(new ElementId(v.E)); break;
                            }
                        }
                    }
                    catch { /* the self-check reports it and rolls everything back */ }
                    writes.Add(new PlannedWrite
                    {
                        X = new ParameterPlanLogic.Expectation { Kind = "rebind", Key = entry.Name, Name = entry.Name, Id = v.Id, Expected = v.Text },
                        Id = v.Id, Guid = entry.Guid, ByStorage = true,
                    });
                }
                writes.Add(new PlannedWrite
                {
                    X = new ParameterPlanLogic.Expectation { Kind = "binding", Key = entry.Name, Name = entry.Name, Expected = BindingText(rp.Level, bics) },
                    Guid = entry.Guid, Cats = bics, Level = rp.Level,
                });
                done++;
            }
            return new JsonObject
            {
                ["planned"] = planned, ["done"] = done, ["values"] = values, ["skipped"] = skipped, ["items"] = items,
                ["dropped"] = new JsonObject { ["schedules"] = dSchedules, ["viewFilters"] = dFilters, ["labels"] = dLabels },
                ["note"] = planned == 0 ? null
                    : "Revit removes the old definition's schedule fields and view filter rules when it is deleted; tag and title block labels keep pointing at the old GUID. See dropped and the impact scan."
                      + (IsWorkshared(doc) ? " Workshared model: synchronize with central after applying." : ""),
            };
        }

        private static string SpecLabel(ForgeTypeId dt)
        {
            if (dt == null || string.IsNullOrEmpty(dt.TypeId)) return "no data type";
            try { return LabelUtils.GetLabelForSpec(dt); } catch { return dt.TypeId; }
        }

        // ---- the Scan's plan parts ------------------------------------------------------------------------------

        private sealed partial class Scan
        {
            private readonly Dictionary<List<Element>, Dictionary<long, int>> _perType = new Dictionary<List<Element>, Dictionary<long, int>>();
            /// <summary>Element + parameter keys written (or planned) by derive rules and the fill, so nothing is written twice.</summary>
            private readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>How many of the instances read each type (once per instance list).</summary>
            private Dictionary<long, int> InstancesPerType(List<Element> instances)
            {
                if (_perType.TryGetValue(instances, out var counts)) return counts;
                counts = new Dictionary<long, int>();
                foreach (var e in instances)
                {
                    var tid = e.GetTypeId();
                    if (tid == ElementId.InvalidElementId) continue;
                    counts[tid.Value] = counts.TryGetValue(tid.Value, out var n) ? n + 1 : 1;
                }
                _perType[instances] = counts;
                return counts;
            }

            /// <summary>Why this parameter of this element cannot be written now (code, holder, detail), or null.</summary>
            private (string Code, string Holder, string Detail)? WriteBlock(Parameter p, Element e)
            {
                if (p.StorageType != StorageType.String) return ("nottext", null, p.StorageType.ToString());
                if (p.IsReadOnly) return ("readonly", null, null);
                if (InGroup(e)) return ("group", null, null);
                if (InDesignOption(e)) return ("option", null, null);
                var state = EditState(e);
                if (!state.Ok) return (state.Code, state.Owner, state.Code == "unreadable" ? state.Why : null);
                return null;
            }

            private static string WriteKey(Element e, Parameter p) => e.Id.Value + "\n" + p.Id.Value;

            private void Record(List<PlannedWrite> writes, string kind, Entry entry, Element e, Parameter p, string value)
            {
                Guid? g = null;
                try { if (p.IsShared) g = p.GUID; } catch { g = null; }
                writes.Add(new PlannedWrite
                {
                    X = new ParameterPlanLogic.Expectation { Kind = kind, Key = entry.Row.Key ?? entry.Row.Name, Name = entry.Row.Name, Id = e.Id.Value, Expected = value },
                    Id = e.Id.Value, Guid = g, Def = p.Definition,
                });
            }

            /// <summary>A type written once: how many instances the change reaches.</summary>
            private static void NoteType(JsonArray types, HashSet<long> seen, Entry entry, Element e)
            {
                if (e is not ElementType || !seen.Add(e.Id.Value) || types.Count >= 500) return;
                types.Add(new JsonObject
                {
                    ["id"] = e.Id.Value, ["name"] = e.Name, ["parameter"] = entry.Row.Name,
                    ["instancesAffected"] = entry.TypeInstances.TryGetValue(e.Id.Value, out var n) ? n : 0,
                });
            }

            /// <summary>A source parameter's text on the element (or, for an instance without it, its type); null when neither has it.</summary>
            private string ReadText(Element e, string name)
            {
                var p = e.LookupParameter(name);
                if (p == null && e is not ElementType)
                {
                    var tid = e.GetTypeId();
                    if (tid != ElementId.InvalidElementId) p = TypeElement(tid)?.LookupParameter(name);
                }
                if (p == null) return null;
                if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                if (!p.HasValue) return "";
                try { return p.AsValueString() ?? ""; } catch { return ""; }
            }

            /// <summary>
            /// P6 derived values, rule by rule, into blank text targets only (never overwriting a value); all parts must have
            /// values. Answer per rule: { rule, text, target, set, skipped (plain language), types }.
            /// </summary>
            public JsonArray Derive(List<ParameterPlanLogic.DeriveRule> rules, JsonArray problems, bool plan, List<PlannedWrite> writes)
            {
                var answer = new JsonArray();
                foreach (var p in problems ?? new JsonArray())
                    answer.Add(new JsonObject
                    {
                        ["rule"] = p?["rule"]?.DeepClone(), ["text"] = null, ["target"] = p?["target"]?.ToString(), ["set"] = 0,
                        ["skipped"] = new JsonArray(ParameterPlanLogic.Entry("derive rule not used", "derive-rule", null, p?["reason"]?.ToString(), 0, null, p?["target"]?.ToString())),
                        ["types"] = new JsonArray(),
                    });
                foreach (var rule in rules)
                {
                    var set = 0;
                    var skips = new List<ParameterPlanLogic.Skip>();
                    var types = new JsonArray();
                    var typesSeen = new HashSet<long>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var entries = _rows.Where(x => string.Equals(x.Row.Name, rule.Target, StringComparison.Ordinal)).ToList();
                    if (entries.Count == 0) skips.Add(new ParameterPlanLogic.Skip { Name = rule.Target, Code = "no-target", Detail = rule.Target });
                    foreach (var entry in entries)
                    {
                        // Blank targets, and targets declared missing (an NA value): a real derived value may replace NA, nothing else.
                        // A literal value goes into truly blank targets only (never over NA or any text).
                        foreach (var p in rule.Value != null ? entry.Blanks : entry.Blanks.Concat(entry.NaParams))
                        {
                            var e = p.Element;
                            var key = WriteKey(e, p);
                            if (!seen.Add(key)) continue;   // the same type through two rows of the target
                            if (_written.Contains(key)) { skips.Add(new ParameterPlanLogic.Skip { Name = rule.Target, Code = "derived-earlier", Id = e.Id.Value }); continue; }
                            var block = WriteBlock(p, e);
                            if (block is { } b) { skips.Add(new ParameterPlanLogic.Skip { Name = rule.Target, Code = b.Code, Holder = b.Holder, Detail = b.Detail, Id = e.Id.Value }); continue; }
                            var value = ParameterPlanLogic.DeriveValue(rule, n => ReadText(e, n), _na, out var code, out var part);
                            if (value == null) { skips.Add(new ParameterPlanLogic.Skip { Name = rule.Target, Code = code, Detail = part, Id = e.Id.Value }); continue; }
                            if (!plan && !p.Set(value)) { skips.Add(new ParameterPlanLogic.Skip { Name = rule.Target, Code = "refused", Id = e.Id.Value }); continue; }
                            set++;
                            _written.Add(key);
                            NoteType(types, typesSeen, entry, e);
                            if (!plan) Record(writes, "derived", entry, e, p, value);
                        }
                    }
                    answer.Add(new JsonObject
                    {
                        ["rule"] = rule.Source.DeepClone(), ["text"] = rule.Text, ["target"] = rule.Target, ["set"] = set,
                        ["skipped"] = ParameterPlanLogic.Group(skips, "not derived", _maxIds), ["types"] = types,
                    });
                }
                return answer;
            }

            /// <summary>
            /// The fill value into the blank text parameters that remain (plan: count only). Answer as 1.10 (set, value,
            /// skipped per element, skippedCount) plus skips grouped in plain language and the types written once.
            /// </summary>
            public JsonObject Fill(string value, bool plan, List<PlannedWrite> writes)
            {
                var set = 0;
                var skipped = new JsonArray();
                var skippedCount = 0;
                var skips = new List<ParameterPlanLogic.Skip>();
                var types = new JsonArray();
                var typesSeen = new HashSet<long>();
                void Skip(Element e, string name, string code, string holder, string detail, string legacy = null)
                {
                    skippedCount++;
                    skips.Add(new ParameterPlanLogic.Skip { Name = name, Code = code, Holder = holder, Detail = detail, Id = e?.Id.Value ?? 0 });
                    if (skipped.Count < 500)
                        skipped.Add(new JsonObject { ["id"] = e == null ? null : (JsonNode)e.Id.Value, ["name"] = name, ["reason"] = legacy ?? ParameterPlanLogic.Explain(code, holder, detail).Why });
                }
                foreach (var entry in _rows)
                {
                    var (row, res, blanks) = (entry.Row, entry.Result, entry.Blanks);
                    if (blanks.Count == 0) continue;
                    if (res.StorageType != "String")
                    {
                        // A number, length, area, Yes/No or date stored as a number cannot hold text.
                        skippedCount++;
                        foreach (var p in blanks) skips.Add(new ParameterPlanLogic.Skip { Name = row.Name, Code = "nottext", Detail = res.StorageType, Id = p.Element.Id.Value });
                        if (skipped.Count < 500) skipped.Add(new JsonObject { ["id"] = null, ["name"] = row.Name, ["reason"] = $"not a text parameter ({res.StorageType}): '{value}' cannot be stored ({blanks.Count} blank)" });
                        continue;
                    }
                    foreach (var p in blanks)
                    {
                        var e = p.Element;
                        var key = WriteKey(e, p);
                        if (_written.Contains(key)) continue;   // derived, or the same parameter through a type shared by two rows
                        var block = WriteBlock(p, e);
                        if (block is { } b) { Skip(e, row.Name, b.Code, b.Holder, b.Detail); continue; }
                        if (!plan && !p.Set(value)) { Skip(e, row.Name, "refused", null, null); continue; }
                        set++;
                        _written.Add(key);
                        NoteType(types, typesSeen, entry, e);
                        if (!plan) Record(writes, "fill", entry, e, p, value);
                    }
                }
                return new JsonObject
                {
                    ["set"] = set, ["value"] = value, ["skipped"] = skipped, ["skippedCount"] = skippedCount,
                    ["skips"] = ParameterPlanLogic.Group(skips, "left blank", _maxIds), ["types"] = types, ["after"] = null,
                };
            }

            /// <summary>
            /// The end state of the rows a write touched (or could have: blank text rows, derive targets, bound names),
            /// read again inside the transaction: { summary, rows: [{ key, name, status, blank, na, filled }] }.
            /// </summary>
            public JsonObject After(ICollection<string> deriveTargets, ICollection<string> bindNames)
            {
                var now = new Dictionary<Entry, Entry>();
                foreach (var entry in _rows)
                {
                    var touched = (entry.Blanks.Count > 0 && entry.Result.StorageType == "String")
                        || deriveTargets.Contains(entry.Row.Name) || bindNames.Contains(entry.Row.Name);
                    if (touched) now[entry] = Check(entry.Row, false);
                }
                var afterRows = new JsonArray();
                foreach (var entry in _rows)
                {
                    if (!now.TryGetValue(entry, out var a)) continue;
                    afterRows.Add(new JsonObject
                    {
                        ["key"] = entry.Row.Key, ["name"] = entry.Row.Name, ["status"] = a.Result.Status,
                        ["blank"] = a.Result.Blank, ["na"] = a.Result.Na, ["filled"] = a.Result.Filled,
                    });
                }
                return new JsonObject
                {
                    ["summary"] = ParameterFillLogic.Summary(_rows.Select(r => now.TryGetValue(r, out var a) ? a.Result : r.Result).ToList(), _foreign.Values.Sum()),
                    ["rows"] = afterRows,
                };
            }

            /// <summary>
            /// Fail fast: the elements this plan would write that cannot be edited now (borrowed by others, changed in
            /// central, state unreadable), per parameter and holder. Elements in groups or design options are skips, not
            /// blockers. Elements that get a parameter only from a planned binding count when it would then be written.
            /// </summary>
            public JsonArray Blockers(bool fill, ICollection<string> deriveTargets, ICollection<string> bindNames)
            {
                var skips = new List<ParameterPlanLogic.Skip>();
                if (!_workshared) return new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                void Test(string name, Element e)
                {
                    if (e == null || !seen.Add(name + "\n" + e.Id.Value)) return;
                    var state = EditState(e);
                    if (!state.Ok) skips.Add(new ParameterPlanLogic.Skip { Name = name, Code = state.Code, Holder = state.Owner, Detail = state.Code == "unreadable" ? state.Why : null, Id = e.Id.Value });
                }
                foreach (var entry in _rows)
                {
                    var name = entry.Row.Name;
                    var derive = deriveTargets.Contains(name);
                    var writes = derive || (fill && entry.Result.StorageType == "String");
                    if (writes) foreach (var p in entry.Blanks) Test(name, p.Element);
                    if (derive) foreach (var p in entry.NaParams) Test(name, p.Element);
                    if (bindNames.Contains(name) && (fill || derive)) foreach (var e in entry.MissingElems) Test(name, e);
                }
                return ParameterPlanLogic.Group(skips, "to write held", _maxIds);
            }

            /// <summary>
            /// A rebind before the transaction: the old definition (by GUID) and its binding, the new GUID not held under
            /// another name, the level kept, the categories (old ∪ requested), and every value of the old definition on
            /// instances and types. Values on elements that cannot be edited now are held (blockers): the entry is not done.
            /// </summary>
            public RebindPlan PrepareRebind(ParameterPlanLogic.RebindEntry entry, List<ParameterPlanLogic.Skip> held)
            {
                var rp = new RebindPlan { Entry = entry };
                try { rp.Old = SharedParameterElement.Lookup(_doc, entry.OldGuid); } catch { rp.Old = null; }
                if (rp.Old == null) { rp.Code = "old-missing"; rp.Detail = $"no parameter with GUID {entry.OldGuid} in this model"; return rp; }
                try
                {
                    if (SharedParameterElement.Lookup(_doc, entry.Guid) is SharedParameterElement other && !string.Equals(other.Name, entry.Name, StringComparison.Ordinal))
                    { rp.Code = "guid-other-name"; rp.Detail = $"the model already holds the new GUID {entry.Guid} as '{other.Name}'"; return rp; }
                }
                catch { /* guard only */ }
                foreach (var (def, b) in AllBindings(_doc))
                    if (def.Id == rp.Old.Id) { rp.OldDef = def; rp.OldBinding = b; break; }
                if (rp.OldBinding == null) { rp.Code = "old-missing"; rp.Detail = $"GUID {entry.OldGuid} ('{rp.Old.Name}') is not bound in this model: no values to carry over"; return rp; }
                rp.Level = rp.OldBinding is InstanceBinding ? "instance" : "type";
                if (entry.Level != null && entry.Level != rp.Level) { rp.Code = "rebind-level"; rp.Detail = $"the old definition is bound as {rp.Level}, the entry asks for {entry.Level}"; return rp; }
                var oldNames = new List<string>();
                foreach (Category c in rp.OldBinding.Categories) oldNames.Add(c.BuiltInCategory.ToString());
                var bad = new List<string>();
                rp.Cats = BindableCategories(_doc, oldNames.Concat(entry.Categories), bad);
                if (bad.Any(n => entry.Categories.Contains(n))) { rp.Code = "category"; rp.Detail = $"{string.Join(", ", bad.Where(n => entry.Categories.Contains(n)))}: not a category that takes bound parameters in this model"; return rp; }
                var bics = rp.Cats.Select(c => c.BuiltInCategory).Where(b => b != BuiltInCategory.INVALID).ToList();
                if (bics.Count == 0) return rp;
                var heldBefore = held.Count;
                // Instances and types of the bound categories (no WhereElementIsNotElementType: types hold type values).
                foreach (var e in new FilteredElementCollector(_doc).WherePasses(new ElementMulticategoryFilter(bics)))
                {
                    Parameter p;
                    try { p = e.get_Parameter(entry.OldGuid); } catch { p = null; }
                    if (p == null || !p.HasValue) continue;
                    if (p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString())) continue;
                    rp.Values.Add((e.Id.Value, p.StorageType,
                        p.StorageType == StorageType.String ? p.AsString() : null,
                        p.StorageType == StorageType.Integer ? p.AsInteger() : 0,
                        p.StorageType == StorageType.Double ? p.AsDouble() : 0,
                        p.StorageType == StorageType.ElementId ? p.AsElementId().Value : 0,
                        ValueText(p)));
                    var state = EditState(e);
                    if (!state.Ok) held.Add(new ParameterPlanLogic.Skip { Name = entry.Name, Code = state.Code, Holder = state.Owner, Detail = state.Code == "unreadable" ? state.Why : null, Id = e.Id.Value });
                }
                if (held.Count > heldBefore)
                { rp.Code = "rebind-held"; rp.Detail = $"{held.Count - heldBefore} element(s) holding values of '{entry.Name}' cannot be edited now (see blocked)"; }
                return rp;
            }

            /// <summary>Row names that are Revit built-in parameters in this model (never bound).</summary>
            public HashSet<string> BuiltInNames() =>
                new HashSet<string>(_rows.Where(x => x.Result.BuiltIn).Select(x => x.Row.Name), StringComparer.Ordinal);

            /// <summary>The parameters a write would touch: blank text rows, derive targets and bound names (with their GUIDs).</summary>
            public List<ParameterImpact.Target> ImpactTargets(ICollection<string> deriveTargets, IList<ParameterPlanLogic.BindEntry> binds)
            {
                var targets = new Dictionary<string, ParameterImpact.Target>(StringComparer.Ordinal);
                ParameterImpact.Target Get(string name) => targets.TryGetValue(name, out var t) ? t : targets[name] = new ParameterImpact.Target { Name = name };
                foreach (var entry in _rows)
                {
                    var name = entry.Row.Name;
                    var hit = (entry.Result.Blank > 0 && entry.Result.StorageType == "String")
                        || (deriveTargets != null && deriveTargets.Contains(name))
                        || (binds != null && binds.Any(b => b.Name == name));
                    if (!hit || entry.Result.OutOfScope || entry.Result.Unmapped) continue;
                    var t = Get(name);
                    t.Guid ??= entry.SharedGuid;
                    t.Ids.UnionWith(entry.ParamIds);
                    if (entry.Result.BuiltIn && entry.Row.Group == "project") t.BuiltInProjectInfo = true;
                }
                foreach (var b in binds ?? new List<ParameterPlanLogic.BindEntry>())
                {
                    var t = Get(b.Name);
                    t.Guid ??= b.Guid;
                    try { if (SharedParameterElement.Lookup(_doc, b.Guid) is SharedParameterElement spe) t.Ids.Add(spe.Id.Value); } catch { /* not in the model yet */ }
                }
                return targets.Values.ToList();
            }

            /// <summary>
            /// P7: every blank value before the writes, per row × category × level of the element holding it:
            /// [{ key, name, category (OST name), categoryName, level, count, ids }] (all ids, not capped).
            /// </summary>
            public JsonArray Register()
            {
                var arr = new JsonArray();
                foreach (var entry in _rows)
                {
                    if (entry.Blanks.Count == 0) continue;
                    var groups = entry.Blanks.Select(p => p.Element).Where(e => e != null)
                        .GroupBy(e =>
                        {
                            Category c = null;
                            try { c = e.Category; } catch { c = null; }
                            return (Bic: c == null ? null : c.BuiltInCategory.ToString(), Name: c?.Name, Level: e is ElementType ? "type" : "instance");
                        });
                    foreach (var g in groups.OrderBy(g => g.Key.Bic ?? "", StringComparer.Ordinal).ThenBy(g => g.Key.Level, StringComparer.Ordinal))
                    {
                        var ids = g.Select(e => e.Id.Value).Distinct().ToList();
                        arr.Add(new JsonObject
                        {
                            ["key"] = entry.Row.Key, ["name"] = entry.Row.Name, ["category"] = g.Key.Bic, ["categoryName"] = g.Key.Name,
                            ["level"] = g.Key.Level, ["count"] = ids.Count, ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
                        });
                    }
                }
                return arr;
            }
        }
    }
}
