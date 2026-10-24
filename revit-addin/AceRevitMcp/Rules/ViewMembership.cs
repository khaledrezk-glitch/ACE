using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Rules.ViewMembershipLogic;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "view_membership_fill": { view, parameter, value_in, value_out, categories?, definition?, check_only, dry_run,
    /// expect_model, expect_version, expect_plan, max_ids }. Sets a text parameter to value_in on the elements a view shows
    /// (FilteredElementCollector(doc, view.Id): section box, hidden elements and categories, filters, phase) and to
    /// value_out on the rest, only where the value differs. A missing parameter is bound only from the definition the server
    /// sends (from the vault; never a new GUID). Like clash_resolve: planHash, a dry run and an identical apply as ONE undo
    /// step, and a self-check (in + out = processed, the in-view set unchanged, every written element and a sample of 20
    /// read again) that rolls everything back on a mismatch.
    /// </summary>
    internal static class ViewMembership
    {
        private sealed class Definition
        {
            public string Name, DataType, Group;
            public Guid Guid;
            public bool Instance = true;
            public List<string> Categories = new List<string>();
        }

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var aceVersion = ParameterFill.AceVersion();
            var maxIds = Math.Max(0, Args.Int(args, "max_ids", 20));
            var checkOnly = Args.Bool(args, "check_only");
            var viewName = Args.Str(args, "view");
            var paramName = Args.Str(args, "parameter")?.Trim();
            var valueIn = Args.Str(args, "value_in");
            var valueOut = Args.Str(args, "value_out");
            if (string.IsNullOrWhiteSpace(viewName) || string.IsNullOrWhiteSpace(paramName) || valueIn == null || valueOut == null)
                throw new CommandException("Give view, parameter, value_in and value_out.");

            JsonObject Refuse(string stage, string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = stage, ["error"] = error, ["blocked"] = blocked ?? new JsonArray(), ["model"] = doc.Title, ["aceVersion"] = aceVersion,
            };
            JsonObject Block(string what, string code, string holder, string detail, int elements = 0, IEnumerable<long> ids = null)
            {
                var (why, how) = Explain(code, holder, detail);
                return new JsonObject
                {
                    ["what"] = what, ["why"] = why, ["elements"] = elements,
                    ["sampleIds"] = new JsonArray((ids ?? Enumerable.Empty<long>()).Take(maxIds).Select(i => (JsonNode)i).ToArray()),
                    ["holder"] = string.IsNullOrWhiteSpace(holder) ? null : holder.Trim(), ["how"] = how, ["code"] = code,
                };
            }

            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
            {
                var detail = $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}";
                return Refuse("blocked", $"Nothing was changed: {detail}.", new JsonArray(Block("the whole plan", "version", null, detail)));
            }
            var expectModel = Args.Str(args, "expect_model");
            if (expectModel != null && !string.Equals(expectModel, doc.Title, StringComparison.Ordinal))
            {
                var detail = $"the preview was made on {expectModel}, the active model is {doc.Title}";
                return Refuse("wrong_model", $"Nothing was changed: {detail}. Preview again on this model.", new JsonArray(Block("the whole plan", "wrong-model", null, detail)));
            }
            if (doc.IsFamilyDocument || doc.IsReadOnly || doc.IsLinked)
                return Refuse("blocked", "Nothing was changed: the model cannot be edited.", new JsonArray(Block("the model", "model", null, "a read-only, linked or family document")));
            // A graphical model view only; several with the name: the exact spelling first, then a 3D view, then any model view.
            var named = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && string.Equals(v.Name, viewName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var view = named.Where(IsModelView)
                .OrderByDescending(v => string.Equals(v.Name, viewName.Trim(), StringComparison.Ordinal)).ThenByDescending(v => v is View3D).ThenBy(v => v.Id.Value)
                .FirstOrDefault();
            if (view == null)
                return Refuse("blocked", $"Nothing was changed: no model view is called {viewName}.", new JsonArray(Block("view " + viewName, "view-not-found", null,
                    named.Count == 0 ? $"no view (not a template) is called {viewName}" : $"{viewName} is not a model view (a sheet, schedule, legend or other non-model view)")));

            var blocks = new JsonArray();
            var notes = new List<string>();

            // The parameter: bound already, or bound from the server's definition.
            var bindings = Bindings(doc);
            var bound = bindings.FirstOrDefault(b => string.Equals(b.Def.Name, paramName, StringComparison.Ordinal));
            var def = ParseDefinition(args["definition"] as JsonObject, paramName, blocks, Block);
            List<Category> bindCats = null;
            if (bound.Def != null)
            {
                if (!SameSpec(bound.Def.GetDataType(), SpecTypeId.String.Text)) blocks.Add(Block("parameter " + paramName, "not-text", null, $"{paramName} is not a text parameter"));
                if (bound.Binding is TypeBinding) blocks.Add(Block("parameter " + paramName, "type-parameter", null, $"{paramName} is bound as a type parameter: one value per type, not per element"));
                if (def != null && bound.Guid.HasValue && bound.Guid.Value != def.Guid)
                    blocks.Add(Block("parameter " + paramName, "guid-differs", null, $"{paramName} is bound with GUID {bound.Guid.Value}; the vault's definition has {def.Guid}"));
                if (def != null && !bound.Guid.HasValue)
                    blocks.Add(Block("parameter " + paramName, "guid-differs", null, $"{paramName} is a project parameter (no GUID); the vault's definition has {def.Guid}"));
            }
            else if (def != null)
            {
                var bad = new List<string>();
                bindCats = new List<Category>();
                foreach (var n in def.Categories)
                {
                    var c = Args.ResolveCategory(doc, n);
                    if (c != null && c.AllowsBoundParameters && c.CategoryType == CategoryType.Model) { if (!bindCats.Any(x => x.Id == c.Id)) bindCats.Add(c); }
                    else bad.Add(n);
                }
                if (bad.Count > 0) blocks.Add(Block("definition " + paramName, "datatype", null, $"{string.Join(", ", bad)}: not a model category that takes bound parameters"));
                if (bindCats.Count == 0) blocks.Add(Block("definition " + paramName, "no-definition", null, "the definition names no model category to bind to"));
                if (!def.Instance) blocks.Add(Block("definition " + paramName, "type-parameter", null, "the definition is a type parameter: one value per type, not per element"));
                if (!string.Equals(def.DataType ?? "TEXT", "TEXT", StringComparison.OrdinalIgnoreCase)) blocks.Add(Block("definition " + paramName, "datatype", null, $"the definition's data type is {def.DataType}; view_membership_fill writes text"));
                // The Revit parameter group: param_group wins; a shared parameter file's own group ("Default Group") is not one, so
                // it falls back to General, and the answer says so.
                var paramGroup = Args.Str(args, "param_group");
                if (!string.IsNullOrWhiteSpace(paramGroup))
                {
                    if (ParameterFill.GroupFor(paramGroup) == null) blocks.Add(Block("param_group " + paramGroup, "datatype", null, $"'{paramGroup}' is not a Revit parameter group"));
                    else def.Group = paramGroup;
                }
                else if (ParameterFill.GroupFor(def.Group) == null)
                {
                    notes.Add($"'{def.Group}' (the shared parameter file's group) is not a Revit parameter group: {paramName} is bound under General (param_group sets another).");
                    def.Group = "General";
                }
                try
                {
                    if (SharedParameterElement.Lookup(doc, def.Guid) is SharedParameterElement held && !string.Equals(held.Name, def.Name, StringComparison.Ordinal))
                        blocks.Add(Block("definition " + paramName, "guid-differs", null, $"the model already holds GUID {def.Guid} as '{held.Name}'"));
                }
                catch { }
            }

            // Scope: the categories asked for, else those the parameter is bound to, else the definition's.
            var scope = new List<Category>();
            var asked = Args.Strings(args, "categories");
            foreach (var n in asked)
            {
                var c = Args.ResolveCategory(doc, n) ?? throw new CommandException($"categories: '{n}' is not a category name.");
                if (!scope.Any(x => x.Id == c.Id)) scope.Add(c);
            }
            if (scope.Count == 0 && bound.Def != null)
                foreach (Category c in bound.Binding.Categories) if (c.CategoryType == CategoryType.Model && !scope.Any(x => x.Id == c.Id)) scope.Add(c);
            if (scope.Count == 0 && bindCats != null) scope.AddRange(bindCats);
            if (bound.Def == null && def == null)
            {
                blocks.Add(Block("parameter " + paramName, "no-definition", null, $"{paramName} is not bound in this model and the vault sent no definition"));
                if (scope.Count == 0) scope.AddRange(doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model && c.AllowsBoundParameters));
            }
            var notBoundOn = bound.Def == null ? new List<string>() : scope.Where(c => !bound.Binding.Categories.Contains(c)).Select(c => c.Name).ToList();
            var willBind = bound.Def == null && def != null ? new HashSet<long>(bindCats.Select(c => c.Id.Value)) : new HashSet<long>();

            // The elements and the view's own set.
            var catFilter = scope.Count == 0 ? null : new ElementMulticategoryFilter(scope.Select(c => c.Id).ToList());
            var candidates = scope.Count == 0 ? new List<Element>() : new FilteredElementCollector(doc).WherePasses(catFilter).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.ViewSpecific == false).ToList();
            var inView = InView(doc, view, catFilter, scope.Count);
            if (inView.Count == 0 && candidates.Count > 0)   // never write value_out on the whole model
                blocks.Add(Block("view " + view.Name, "view-not-found", null, $"the view shows none of these {candidates.Count} elements; check the view name"));
            var setIn = new List<long>(); var setOut = new List<long>();
            var expected = new Dictionary<long, string>();
            var skipped = new List<(long Id, string Reason)>();
            var outside = L.Reasons.ToDictionary(r => r, r => 0);
            var outsideSamples = L.Reasons.ToDictionary(r => r, r => new List<long>());
            var why = new OutsideReasons(doc, view);
            int inCount = 0, outCount = 0, unchanged = 0;
            var byCatIn = new Dictionary<string, int>(); var byCatOut = new Dictionary<string, int>();
            foreach (var e in candidates)
            {
                var id = e.Id.Value;
                var isIn = inView.Contains(id);
                if (isIn) inCount++;
                else
                {
                    outCount++;
                    var r = why.Of(e);
                    outside[r]++;
                    if (outsideSamples[r].Count < maxIds) outsideSamples[r].Add(id);
                }
                var p = e.LookupParameter(paramName);
                string current;
                if (p == null)
                {
                    if (!willBind.Contains(e.Category.Id.Value)) { skipped.Add((id, "no-parameter")); continue; }
                    current = null;
                }
                else
                {
                    if (p.StorageType != StorageType.String) { skipped.Add((id, "not-text")); continue; }
                    if (p.IsReadOnly) { skipped.Add((id, "read-only")); continue; }
                    current = p.AsString();
                }
                var target = L.Target(current, isIn, valueIn, valueOut);
                expected[id] = isIn ? valueIn : valueOut;
                if (target == null) { unchanged++; continue; }
                (isIn ? setIn : setOut).Add(id);
                var cat = e.Category.Name; var d = isIn ? byCatIn : byCatOut;
                d[cat] = d.TryGetValue(cat, out var k) ? k + 1 : 1;
            }

            // Elements the writes need must be editable now.
            foreach (var g in Held(doc, setIn.Concat(setOut)).GroupBy(h => (h.Code, h.Holder)).OrderByDescending(g => g.Count()))
                blocks.Add(Block($"{g.Count()} element{(g.Count() == 1 ? "" : "s")} to write", g.Key.Code, g.Key.Holder, null, g.Count(), g.Select(h => h.Id)));

            var bindText = bound.Def == null && def != null ? $"{def.Guid}|{string.Join(",", bindCats.Select(c => c.Id.Value).OrderBy(i => i))}" : null;
            var planHash = L.PlanHash(bindText, valueIn, valueOut, setIn, setOut);
            var plan = new JsonArray();
            if (bindText != null) plan.Add(new JsonObject { ["id"] = "bind", ["kind"] = "bind", ["count"] = bindCats.Count, ["categories"] = new JsonArray(bindCats.Select(c => (JsonNode)c.Name).ToArray()), ["guid"] = def.Guid.ToString() });
            if (setIn.Count > 0) plan.Add(new JsonObject { ["id"] = "set-in", ["kind"] = "set-in", ["count"] = setIn.Count, ["value"] = valueIn, ["categories"] = Counts(byCatIn) });
            if (setOut.Count > 0) plan.Add(new JsonObject { ["id"] = "set-out", ["kind"] = "set-out", ["count"] = setOut.Count, ["value"] = valueOut, ["categories"] = Counts(byCatOut) });

            var viewJson = new JsonObject
            {
                ["name"] = view.Name, ["id"] = view.Id.Value, ["viewType"] = view.ViewType.ToString(), ["is3d"] = view is View3D,
                ["sectionBox"] = view is View3D v3 && v3.IsSectionBoxActive, ["phase"] = (doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name,
                ["filters"] = new JsonArray(Filters(doc, view).Select(f => (JsonNode)f.Name).ToArray()),
            };
            var paramJson = new JsonObject
            {
                ["name"] = paramName, ["bound"] = bound.Def != null, ["guid"] = bound.Guid?.ToString() ?? def?.Guid.ToString(),
                ["storage"] = bound.Def == null ? (def != null ? "String" : null) : (SameSpec(bound.Def.GetDataType(), SpecTypeId.String.Text) ? "String" : bound.Def.GetDataType()?.TypeId),
                ["instance"] = bound.Def != null ? !(bound.Binding is TypeBinding) : def?.Instance,
                ["categories"] = new JsonArray((bound.Def != null ? bound.Binding.Categories.Cast<Category>().Select(c => c.Name) : Enumerable.Empty<string>()).Select(n => (JsonNode)n).ToArray()),
                ["notBoundOn"] = new JsonArray(notBoundOn.Select(n => (JsonNode)n).ToArray()),
            };
            var counts = new JsonObject { ["processed"] = candidates.Count, ["in"] = inCount, ["out"] = outCount, ["unchanged"] = unchanged, ["skipped"] = skipped.Count, ["setIn"] = setIn.Count, ["setOut"] = setOut.Count };
            var impact = bound.Def != null ? Impact(doc, bound.Def) : null;
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["planHash"] = planHash;
                r["view"] = viewJson.DeepClone(); r["parameter"] = paramJson.DeepClone();
                r["modelNames"] = new JsonArray(bindings.Select(b => b.Def.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(500).Select(n => (JsonNode)n).ToArray());
                r["counts"] = counts.DeepClone();
                r["outside"] = new JsonObject(outside.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));
                r["outsideSamples"] = new JsonObject(outsideSamples.Where(kv => kv.Value.Count > 0).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, new JsonArray(kv.Value.Select(i => (JsonNode)i).ToArray()))));
                r["plan"] = plan.DeepClone(); r["blocked"] = blocks.DeepClone();
                r["skipped"] = new JsonArray(skipped.Take(200).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["reason"] = s.Reason }).ToArray());
                r["impact"] = impact?.DeepClone();
                r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                return r;
            }
            JsonObject Zero() => new JsonObject { ["bound"] = 0, ["setIn"] = 0, ["setOut"] = 0 };
            if (outside["other"] > 0) notes.Add("\"other\" covers what ACE does not test one by one: the crop region, the view range, design options and temporary hiding.");

            var expectPlan = Args.Str(args, "expect_plan");
            if (!checkOnly && !string.IsNullOrWhiteSpace(expectPlan) && !string.Equals(expectPlan.Trim(), planHash, StringComparison.OrdinalIgnoreCase))
            {
                const string changed = "the model or the view changed since the preview: run the dry run again";
                var refused = Common(Refuse("blocked", $"Nothing was changed: {changed}.", null));
                refused["code"] = "plan-changed";
                ((JsonArray)refused["blocked"]).Insert(0, Block("the whole plan", "plan-changed", null, changed));
                refused["expectedPlan"] = expectPlan.Trim();
                return refused;
            }
            if (checkOnly)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["checkOnly"] = true; r["done"] = Zero(); r["verify"] = null;
                r["note"] = blocks.Count > 0 ? "Nothing was changed. Some items are blocked (see blocked): the dry run and the apply refuse until they are cleared."
                    : plan.Count == 0 ? "Nothing was changed, and there is nothing to do: every element already has its value." : "Nothing was changed. To apply: preview with dry_run: true, then confirm.";
                return r;
            }
            if (blocks.Count > 0)
                return Common(Refuse("blocked", $"Nothing was changed: {blocks.Count} item{(blocks.Count == 1 ? " is" : "s are")} blocked ({blocks[0]?["why"]}). See blocked for what clears each one.", null));
            if (plan.Count == 0)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["done"] = Zero(); r["verify"] = null; r["rolledBack"] = false; r["note"] = "Nothing to do: every element already has its value. Nothing was changed.";
                return r;
            }

            var processedIds = candidates.Select(e => e.Id.Value).ToList();
            var written = new HashSet<long>(setIn.Concat(setOut));
            var sample = L.Sample(expected.Keys.ToList(), 20, planHash);
            var result = NativeChange.Run(app, args, "view_membership_fill", "ACE: fill " + paramName + " from view " + view.Name, (d, look) =>
            {
                var r = Common(new JsonObject());
                var done = Zero();
                r["done"] = done; r["verify"] = null;
                if (look) return r;
                if (bindText != null)
                {
                    BindDefinition(app, d, def, bindCats);
                    done["bound"] = 1;
                    d.Regenerate();
                }
                foreach (var (ids, value, key) in new[] { (setIn, valueIn, "setIn"), (setOut, valueOut, "setOut") })
                    foreach (var id in ids)
                    {
                        var p = d.GetElement(new ElementId(id))?.LookupParameter(paramName)
                                ?? throw new CommandException($"Nothing was changed: element {id} has no {paramName} after the binding.");
                        if (!p.Set(value)) throw new CommandException($"Nothing was changed: Revit refused {paramName} on element {id}.");
                        done[key] = (int)done[key] + 1;
                    }
                return r;
            }, verify: (d, r) =>
            {
                var mismatches = new JsonArray();
                // in + out = processed, read back from the model: every element the run covers holds value_in (in the view) or value_out.
                int readIn = 0, readOut = 0, wantIn = 0, wantOut = 0;
                foreach (var kv in expected)
                {
                    var isIn = inView.Contains(kv.Key);
                    if (isIn) wantIn++; else wantOut++;
                    var v = d.GetElement(new ElementId(kv.Key))?.LookupParameter(paramName)?.AsString();
                    if (isIn && string.Equals(v ?? "", valueIn ?? "", StringComparison.Ordinal)) readIn++;
                    if (!isIn && string.Equals(v ?? "", valueOut ?? "", StringComparison.Ordinal)) readOut++;
                }
                if (readIn != wantIn || readOut != wantOut || wantIn + wantOut + skipped.Count != processedIds.Count)
                    mismatches.Add(new JsonObject { ["id"] = null, ["what"] = "in+out", ["expected"] = $"{wantIn} in + {wantOut} out (+ {skipped.Count} skipped) = {processedIds.Count}", ["actual"] = $"{readIn} in + {readOut} out" });
                var nowIn = InView(d, view, catFilter, scope.Count);
                if (!nowIn.SetEquals(inView))
                    mismatches.Add(new JsonObject { ["id"] = null, ["what"] = "in-view set changed after the writes (a view filter may read this parameter)", ["expected"] = inView.Count, ["actual"] = nowIn.Count });
                var check = new HashSet<long>(sample); check.UnionWith(written);
                foreach (var id in check.OrderBy(i => i))
                {
                    var actual = d.GetElement(new ElementId(id))?.LookupParameter(paramName)?.AsString();
                    if (!string.Equals(actual ?? "", expected[id] ?? "", StringComparison.Ordinal))
                        mismatches.Add(new JsonObject { ["id"] = id, ["expected"] = expected[id], ["actual"] = actual });
                }
                r["verify"] = new JsonObject { ["ok"] = mismatches.Count == 0, ["checked"] = check.Count, ["mismatches"] = new JsonArray(mismatches.Take(50).Select(m => m.DeepClone()).ToArray()) };
                return mismatches.Count == 0;
            });
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled && result["done"] is JsonObject dn)
            {
                result["wouldDo"] = dn.DeepClone();
                result["done"] = Zero();
                if (result["transaction"]?.ToString() == "VerifyFailed")
                    result["note"] = "The self-check found a difference (see verify.mismatches): the whole change was rolled back. Nothing was changed.";
            }
            return result;
        }

        /// <summary>
        /// Workshared: the elements this user cannot change now, "borrowed" (owned by another user, with the holder) or
        /// "central" (changed or deleted in central since the last reload). Empty for a model that is not workshared.
        /// Also used by purge_unused.
        /// </summary>
        internal static List<(long Id, string Code, string Holder)> Held(Document doc, IEnumerable<long> ids)
        {
            var held = new List<(long Id, string Code, string Holder)>();
            if (!doc.IsWorkshared) return held;
            foreach (var id in ids)
                try
                {
                    var eid = new ElementId(id);
                    var status = WorksharingUtils.GetCheckoutStatus(doc, eid, out var owner);
                    if (status == CheckoutStatus.OwnedByOtherUser) { held.Add((id, "borrowed", string.IsNullOrWhiteSpace(owner) ? null : owner.Trim())); continue; }
                    var upd = WorksharingUtils.GetModelUpdatesStatus(doc, eid);
                    if (upd == ModelUpdatesStatus.UpdatedInCentral || upd == ModelUpdatesStatus.DeletedInCentral) held.Add((id, "central", null));
                }
                catch { }
            return held;
        }

        // ---- the view -------------------------------------------------------------------------------------------------

        private static bool IsModelView(View v)
        {
            if (v is ViewSheet || v is ViewSchedule) return false;
            switch (v.ViewType)
            {
                case ViewType.Legend: case ViewType.DrawingSheet: case ViewType.Internal: case ViewType.ProjectBrowser: case ViewType.SystemBrowser:
                case ViewType.Schedule: case ViewType.PanelSchedule: case ViewType.ColumnSchedule: case ViewType.Report: case ViewType.Undefined:
                    return false;
                default: return true;
            }
        }

        private static HashSet<long> InView(Document doc, View view, ElementFilter catFilter, int scopeCount) =>
            scopeCount == 0 ? new HashSet<long>()
                : new HashSet<long>(new FilteredElementCollector(doc, view.Id).WherePasses(catFilter).WhereElementIsNotElementType().ToElementIds().Select(i => i.Value));

        private static List<ParameterFilterElement> Filters(Document doc, View view)
        {
            var list = new List<ParameterFilterElement>();
            try { foreach (var id in view.GetFilters()) if (doc.GetElement(id) is ParameterFilterElement f) list.Add(f); } catch { }
            return list;
        }

        /// <summary>Why an element the view does not show is left out (approximate: the first reason found).</summary>
        private sealed class OutsideReasons
        {
            private readonly View _view;
            private readonly ElementId _phase;
            private readonly PhaseFilter _phaseFilter;
            private readonly List<(ElementFilter Filter, HashSet<long> Cats, SelectionFilterElement Selection)> _hidingFilters = new List<(ElementFilter, HashSet<long>, SelectionFilterElement)>();
            private readonly Transform _toBox;
            private readonly double[] _min, _max;

            public OutsideReasons(Document doc, View view)
            {
                _view = view;
                try { _phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId(); } catch { }
                try { _phaseFilter = doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId) as PhaseFilter; } catch { }
                try
                {
                    foreach (var id in view.GetFilters())
                    {
                        bool enabled;
                        try { enabled = view.GetIsFilterEnabled(id); } catch { enabled = true; }
                        if (!enabled || view.GetFilterVisibility(id)) continue;
                        var el = doc.GetElement(id);
                        if (el is ParameterFilterElement pf) _hidingFilters.Add((pf.GetElementFilter(), new HashSet<long>(pf.GetCategories().Select(c => c.Value)), null));
                        else if (el is SelectionFilterElement sf) _hidingFilters.Add((null, null, sf));
                    }
                }
                catch { }
                if (view is View3D v3 && v3.IsSectionBoxActive)
                    try
                    {
                        var box = v3.GetSectionBox();
                        _toBox = box.Transform.Inverse;
                        _min = new[] { box.Min.X, box.Min.Y, box.Min.Z }; _max = new[] { box.Max.X, box.Max.Y, box.Max.Z };
                    }
                    catch { _toBox = null; }
            }

            public string Of(Element e)
            {
                bool phase = false, category = false, hidden = false, filter = false, workset = false, box = false;
                try
                {
                    if (_phase != null && _phase != ElementId.InvalidElementId && _phaseFilter != null)
                        phase = _phaseFilter.GetPhaseStatusPresentation(e.GetPhaseStatus(_phase)) == PhaseStatusPresentation.DontShow;
                }
                catch { }
                try { category = e.Category != null && _view.GetCategoryHidden(e.Category.Id); } catch { }
                try { hidden = e.IsHidden(_view); } catch { }
                foreach (var f in _hidingFilters)
                {
                    try
                    {
                        if (f.Selection != null ? f.Selection.Contains(e.Id) : e.Category != null && f.Cats.Contains(e.Category.Id.Value) && (f.Filter == null || f.Filter.PassesFilter(e)))
                        { filter = true; break; }
                    }
                    catch { }
                }
                try { workset = e.Document.IsWorkshared && _view.GetWorksetVisibility(e.WorksetId) == WorksetVisibility.Hidden; } catch { }
                if (_toBox != null)
                    try
                    {
                        var bb = e.get_BoundingBox(null);
                        if (bb != null)
                        {
                            var corners = new List<double[]>();
                            foreach (var x in new[] { bb.Min.X, bb.Max.X }) foreach (var y in new[] { bb.Min.Y, bb.Max.Y }) foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                            { var p = _toBox.OfPoint(new XYZ(x, y, z)); corners.Add(new[] { p.X, p.Y, p.Z }); }
                            box = L.OutsideBox(corners, _min, _max);
                        }
                    }
                    catch { }
                return L.OutReason(phase, category, hidden, filter, workset, box);
            }
        }

        // ---- the parameter --------------------------------------------------------------------------------------------

        private static bool SameSpec(ForgeTypeId a, ForgeTypeId b)
        {
            try { return a != null && b != null && a.TypeId.Split('-')[0] == b.TypeId.Split('-')[0]; } catch { return false; }
        }

        private static List<(InternalDefinition Def, ElementBinding Binding, Guid? Guid)> Bindings(Document doc)
        {
            var list = new List<(InternalDefinition, ElementBinding, Guid?)>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is InternalDefinition d) || !(it.Current is ElementBinding b)) continue;
                Guid? g = null;
                try { if (doc.GetElement(d.Id) is SharedParameterElement spe) g = spe.GuidValue; } catch { }
                list.Add((d, b, g));
            }
            return list;
        }

        private static Definition ParseDefinition(JsonObject o, string paramName, JsonArray blocks, Func<string, string, string, string, int, IEnumerable<long>, JsonObject> block)
        {
            if (o == null) return null;
            var name = Args.Str(o, "name")?.Trim();
            var guidText = Args.Str(o, "guid")?.Trim();
            if (!string.Equals(name, paramName, StringComparison.Ordinal))
            { blocks.Add(block("definition", "no-definition", null, $"the definition is for '{name}', the request names '{paramName}'", 0, null)); return null; }
            if (string.IsNullOrEmpty(guidText) || !Guid.TryParse(guidText, out var guid) || guid == Guid.Empty)
            { blocks.Add(block("definition " + name, "no-definition", null, "the definition has no valid GUID (ACE never creates one)", 0, null)); return null; }
            var instanceNode = o["instance"];
            var instance = !(instanceNode is JsonValue iv && iv.TryGetValue<bool>(out var ib) && !ib);
            return new Definition
            {
                Name = name, Guid = guid, DataType = Args.Str(o, "dataType") ?? "TEXT", Group = Args.Str(o, "group"), Instance = instance,
                Categories = Args.Strings(o, "categories"),
            };
        }

        /// <summary>
        /// Binds the vault's definition: written to a temporary shared parameter file through Revit (the GUID is the vault's),
        /// bound as an instance parameter, and the application's shared parameter file setting restored at once.
        /// </summary>
        private static void BindDefinition(UIApplication app, Document doc, Definition def, List<Category> cats)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ACE-RevitMCP");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"sp-{def.Guid:N}-{DateTime.Now:yyyyMMddHHmmssfff}.txt");
            File.WriteAllText(file, "");
            var fs = new ParameterFill.FileSession(app.Application);
            try
            {
                var df = fs.Open(file) ?? throw new CommandException("Nothing was changed: Revit could not open a temporary shared parameter file.");
                var group = df.Groups.get_Item("ACE") ?? df.Groups.Create("ACE");
                var opts = new ExternalDefinitionCreationOptions(def.Name, SpecTypeId.String.Text) { GUID = def.Guid, Visible = true };
                var ext = group.Definitions.Create(opts) as ExternalDefinition ?? throw new CommandException($"Nothing was changed: Revit did not create the definition of {def.Name}.");
                var set = app.Application.Create.NewCategorySet();
                foreach (var c in cats) set.Insert(c);
                var groupId = ParameterFill.GroupFor(def.Group)?.Id ?? GroupTypeId.General;
                if (!doc.ParameterBindings.Insert(ext, app.Application.Create.NewInstanceBinding(set), groupId))
                    throw new CommandException($"Nothing was changed: Revit did not bind {def.Name}.");
            }
            finally
            {
                fs.Restore();
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>Where the parameter is read in the model: schedules (fields) and view filters (rules).</summary>
        private static JsonObject Impact(Document doc, InternalDefinition def)
        {
            var schedules = new List<string>(); var filters = new List<string>();
            try
            {
                foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
                    try
                    {
                        var sd = s.Definition;
                        for (var i = 0; i < sd.GetFieldCount(); i++) if (sd.GetField(i).ParameterId == def.Id) { schedules.Add(s.Name); break; }
                    }
                    catch { }
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                    try { if (f.GetElementFilterParameters().Contains(def.Id)) filters.Add(f.Name); } catch { }
            }
            catch { }
            return new JsonObject
            {
                ["schedules"] = new JsonArray(schedules.OrderBy(n => n).Select(n => (JsonNode)n).ToArray()),
                ["viewFilters"] = new JsonArray(filters.OrderBy(n => n).Select(n => (JsonNode)n).ToArray()),
            };
        }

        private static JsonObject Counts(Dictionary<string, int> d) =>
            new JsonObject(d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));

        private static (string Why, string How) Explain(string code, string holder, string detail)
        {
            switch (code)
            {
                case "view-not-found": return (detail ?? "the view does not exist", "Check the view name (list_views), then run again.");
                case "no-definition": return (detail ?? "the parameter is missing and there is no definition", "Add the parameter's shared definition (name, GUID, data type, categories) to the vault, or bind it in Revit, then run again.");
                case "not-text": return (detail ?? "not a text parameter", "Use a text parameter, or fix the binding in Revit.");
                case "type-parameter": return (detail ?? "a type parameter", "Bind it as an instance parameter (the same GUID), then run again.");
                case "guid-differs": return (detail ?? "the GUID differs from the vault's", "Make the model and the vault agree on one GUID (rebind in the model, or fix the vault), then run again.");
                case "datatype": return (detail ?? "the definition is not usable", "Fix the definition in the vault.");
                default: return WorksetAdminLogic.Explain(code, holder, detail);
            }
        }
    }
}
