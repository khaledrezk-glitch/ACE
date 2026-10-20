using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Coordination;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Rules.WorksetAdminLogic;
using P = AceRevitMcp.Rules.WorksetPlanLogic;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// workset_admin "facts" and "apply_plan" (workset_plan). facts reads, in one pass, every element on a user workset (model
    /// elements, RVT links, CAD imports; not view-specific, not types, not levels and grids) grouped by
    /// [workset, category, type, level, material, zone, option], with the worksets, levels and zones. apply_plan runs
    /// { create, rename, moves, delete } as ONE native change in that order, with the same gate as rename / delete_empty
    /// (check_only, dry_run, expect_model, expect_version, expect_plan) and a self-check that rolls everything back.
    /// </summary>
    internal static partial class WorksetAdmin
    {
        /// <summary>One element as the facts see it.</summary>
        private sealed class Fact
        {
            public long Id, WorksetId;
            public string Workset, Category, Type, Level, Material, Zone, Option, Key, Holder;
            public bool Borrowed, Central;
            public long TypeId;          // a link or CAD import: its type (RevitLinkType / CADLinkType), which has a workset of its own
            public long TypeWorksetId;   // that type's user workset (0 = none / not a user workset)
            public bool TypeEditable;    // the type's workset parameter can be set
        }

        private static readonly BuiltInParameter[] LevelParams =
        {
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM,
        };

        /// <summary>
        /// One pass over the model's instances: the elements per user workset (as audit counts them) and the facts of every
        /// element that can be grouped. Types, levels, materials and zones are looked up once each.
        /// </summary>
        private static List<Fact> Scan(Document doc, List<L.Workset> worksets, bool checkout)
        {
            var byId = worksets.ToDictionary(w => w.Id);
            var facts = new List<Fact>();
            var typeNames = new Dictionary<long, string>();
            var levelNames = new Dictionary<long, string>();
            var materials = new Dictionary<(long, string), string>();
            var zoneCtx = new WorksetRules.Context(doc, true);
            var hasZones = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().GetElementCount() > 0;
            string LevelName(ElementId id)
            {
                if (id == null || id == ElementId.InvalidElementId) return null;
                if (levelNames.TryGetValue(id.Value, out var n)) return n;
                return levelNames[id.Value] = (doc.GetElement(id) as Level)?.Name;
            }
            string TypeName(Element e)
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return e.Name ?? "";
                if (typeNames.TryGetValue(tid.Value, out var n)) return n;
                var t = doc.GetElement(tid) as ElementType;
                string name;
                if (e is RevitLinkInstance || e is ImportInstance || t == null) name = t?.Name ?? e.Name ?? "";
                else name = string.IsNullOrEmpty(t.FamilyName) ? t.Name : $"{t.FamilyName} : {t.Name}";
                return typeNames[tid.Value] = name;
            }

            // Every element, types included: the same filter as audit and as the delete check (ElementWorksetFilter counts types).
            foreach (var e in AllElements(doc))
            {
                long ws;
                try { ws = e.WorksetId.IntegerValue; } catch { continue; }
                if (!byId.TryGetValue(ws, out var w)) continue;
                var cat = CategoryName(e);
                w.Elements++;
                w.ByCategory[cat] = w.ByCategory.TryGetValue(cat, out var c0) ? c0 + 1 : 1;
                if (e is ElementType) { w.Types++; continue; }   // counted, never grouped or moved
                if (e is Level || e is Grid) w.LevelsAndGrids++;

                // Groupable: model elements, links and imports; not view-specific, not datums, not nested family parts.
                if (e is View || e.ViewSpecific || e is Level || e is Grid || e is ReferencePlane) continue;
                if (e is FamilyInstance nested && nested.SuperComponent != null) continue;
                var isLink = e is RevitLinkInstance; var isImport = e is ImportInstance;
                if (!isLink && !isImport && (e.Category == null || e.Category.CategoryType != CategoryType.Model)) continue;

                var f = new Fact { Id = e.Id.Value, WorksetId = ws, Workset = w.Name };
                f.Category = isImport ? P.CadImports : cat;
                f.Type = TypeName(e);
                string level = null;
                try { level = LevelName(e.LevelId); } catch { }
                if (level == null)
                    foreach (var bip in LevelParams)
                    {
                        try { level = LevelName(e.get_Parameter(bip)?.AsElementId()); } catch { level = null; }
                        if (level != null) break;
                    }
                f.Level = level ?? "";
                if (isLink || isImport) f.Material = "other";
                else
                {
                    var mk = (e is FamilyInstance ? e.Id.Value : -e.GetTypeId().Value, cat);
                    if (!materials.TryGetValue(mk, out var m)) { try { m = P.MaterialClass(StructuralMaterial.Of(e)); } catch { m = "other"; } if (!(e is FamilyInstance)) materials[mk] = m; }
                    f.Material = m;
                }
                if (hasZones)
                    try { var bb = e.get_BoundingBox(null); f.Zone = bb == null ? "" : zoneCtx.ZoneOf((bb.Min + bb.Max) / 2) ?? ""; } catch { f.Zone = ""; }
                else f.Zone = "";
                try { f.Option = e.DesignOption?.Name ?? ""; } catch { f.Option = ""; }
                f.Key = P.Key(f.Workset, f.Category, f.Type, f.Level, f.Material, f.Zone, f.Option);
                if ((isLink || isImport) && doc.GetElement(e.GetTypeId()) is ElementType lt)
                    try
                    {
                        f.TypeId = lt.Id.Value;
                        var tws = (long)lt.WorksetId.IntegerValue;
                        f.TypeWorksetId = byId.ContainsKey(tws) ? tws : 0;
                        var tp = lt.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        f.TypeEditable = f.TypeWorksetId != 0 && tp != null && !tp.IsReadOnly;
                    }
                    catch { f.TypeId = 0; }
                if (checkout && doc.IsWorkshared)
                    try
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, e.Id, out var owner) == CheckoutStatus.OwnedByOtherUser) { f.Borrowed = true; f.Holder = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim(); }
                        else
                        {
                            var up = WorksharingUtils.GetModelUpdatesStatus(doc, e.Id);
                            f.Central = up == ModelUpdatesStatus.UpdatedInCentral || up == ModelUpdatesStatus.DeletedInCentral;
                        }
                    }
                    catch { /* not a worksharing element */ }
                facts.Add(f);
            }
            return facts;
        }

        private static List<L.Workset> UserWorksets(Document doc) =>
            new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => new L.Workset { Id = w.Id.IntegerValue, Name = w.Name, Owner = w.Owner, Open = w.IsOpen, Editable = w.IsEditable, IsDefault = w.IsDefaultWorkset }).ToList();

        // ---- facts ------------------------------------------------------------------------------------------------

        /// <summary>The views to report workset visibility for: the names given, else the 3D views named NAVISWORKS or NWC.</summary>
        private static List<View> ExportViews(Document doc, JsonObject args, List<string> notes)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var given = Args.Strings(args, "views").Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            if (given.Count == 0)
                return all.OfType<View3D>().Where(v => v.Name.IndexOf("NAVISWORKS", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("NWC", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Cast<View>().OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
            var list = new List<View>();
            foreach (var n in given.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var v = all.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.Ordinal)) ?? all.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (v == null) notes.Add($"View {n} was not found: its workset visibility is not reported.");
                else list.Add(v);
            }
            return list;
        }

        /// <summary>Whether a workset's elements show in a view: the view's own setting, or the global default (Use Global Setting).</summary>
        private static bool VisibleIn(View v, long worksetId, WorksetDefaultVisibilitySettings defaults)
        {
            var id = new WorksetId((int)worksetId);
            var setting = v.GetWorksetVisibility(id);
            if (setting == WorksetVisibility.Visible) return true;
            if (setting == WorksetVisibility.Hidden) return false;
            try { return defaults?.IsWorksetVisible(id) ?? true; } catch { return true; }
        }

        private static bool SafeVisible(View v, long worksetId, WorksetDefaultVisibilitySettings defaults)
        {
            try { return VisibleIn(v, worksetId, defaults); } catch { return true; }
        }

        private static JsonObject Facts(Document doc, JsonObject args, string aceVersion, int maxIds)
        {
            var r = new JsonObject { ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["action"] = "facts", ["checkOnly"] = true, ["workshared"] = doc.IsWorkshared };
            r["levels"] = new JsonArray(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["elevation"] = Math.Round(Lengths.Mm(l.Elevation)) }).ToArray());
            r["zones"] = new JsonArray(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                .Select(z => z.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Select(n => (JsonNode)n).ToArray());
            if (!doc.IsWorkshared) { r["worksets"] = new JsonArray(); r["groups"] = new JsonArray(); r["note"] = "The model is not workshared: it has no worksets."; return r; }
            var worksets = UserWorksets(doc);
            var facts = Scan(doc, worksets, checkout: true);
            var datum = L.MarkDatum(worksets, DatumName(doc, args));
            var notes = new List<string>();
            var views = ExportViews(doc, args, notes);
            WorksetDefaultVisibilitySettings defaults = null;
            try { defaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc); } catch { defaults = null; }
            r["datumWorkset"] = datum?.Name;
            r["exportViews"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray());
            r["worksets"] = new JsonArray(worksets.Select(w =>
            {
                bool? byDefault = null;
                try { byDefault = defaults?.IsWorksetVisible(new WorksetId((int)w.Id)); } catch { byDefault = null; }
                var inViews = new JsonObject();
                foreach (var v in views) { try { inViews[v.Name] = VisibleIn(v, w.Id, defaults); } catch { inViews[v.Name] = null; } }
                return (JsonNode)new JsonObject
                {
                    ["id"] = w.Id, ["name"] = w.Name, ["elements"] = w.Elements, ["types"] = w.Types, ["byCategory"] = Counts(w.ByCategory),
                    ["isDefault"] = w.IsDefault, ["protected"] = L.IsProtected(w), ["editable"] = w.Editable,
                    ["owner"] = string.IsNullOrWhiteSpace(w.Owner) ? null : w.Owner, ["open"] = w.Open,
                    ["visibleByDefault"] = byDefault, ["views"] = inViews,
                };
            }).ToArray());
            var wsName = worksets.ToDictionary(w => w.Id, w => w.Name);
            r["groups"] = new JsonArray(facts.GroupBy(f => f.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var f = g.First();
                return (JsonNode)new JsonObject
                {
                    ["key"] = g.Key, ["workset"] = f.Workset, ["category"] = f.Category, ["type"] = f.Type, ["level"] = f.Level,
                    ["material"] = f.Material, ["zone"] = f.Zone, ["option"] = f.Option, ["elements"] = g.Count(),
                    ["borrowed"] = g.Count(x => x.Borrowed),
                    ["holders"] = new JsonArray(g.Where(x => x.Holder != null).Select(x => x.Holder).Distinct(StringComparer.OrdinalIgnoreCase).Select(h => (JsonNode)h).ToArray()),
                    ["sampleIds"] = new JsonArray(g.Take(Math.Max(1, maxIds)).Select(x => (JsonNode)x.Id).ToArray()),
                    // A link or CAD import: its type has a workset of its own, moved with the instances.
                    ["types"] = g.Any(x => x.TypeId != 0)
                        ? new JsonArray(g.Where(x => x.TypeId != 0).GroupBy(x => x.TypeId).Select(t => (JsonNode)new JsonObject
                        {
                            ["id"] = t.Key, ["workset"] = t.First().TypeWorksetId != 0 && wsName.TryGetValue(t.First().TypeWorksetId, out var tn) ? tn : null, ["editable"] = t.First().TypeEditable,
                        }).ToArray())
                        : null,
                };
            }).ToArray());
            r["elements"] = facts.Count;
            if (notes.Count > 0) r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
            if (worksets.Any(w => !w.Open)) r["note"] = "Closed worksets are listed with the elements Revit reports for them; open them for reliable facts.";
            return r;
        }

        // ---- apply_plan -----------------------------------------------------------------------------------------------

        private static JsonObject ApplyPlan(UIApplication app, Document doc, JsonObject args, string aceVersion, int maxIds)
        {
            const string action = "apply_plan";
            var checkOnly = Args.Bool(args, "check_only");
            JsonObject Refuse(string stage, string error, JsonArray blocked) => new JsonObject
            {
                ["success"] = false, ["stage"] = stage, ["error"] = error, ["blocked"] = blocked ?? new JsonArray(),
                ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["action"] = action,
            };
            JsonObject OneBlock(string what, string code, string holder, string detail) => ToJson(new L.Block { What = what, Code = code, Holder = holder, Detail = detail }, maxIds);
            var expectVersion = Args.Str(args, "expect_version");
            if (!string.IsNullOrWhiteSpace(expectVersion) && !ParameterPlanLogic.VersionMatches(expectVersion, aceVersion))
            {
                var detail = $"the server expects ACE {expectVersion.Trim()}, the add-in in Revit is {aceVersion}";
                return Refuse("blocked", $"Nothing was changed: {detail}.", new JsonArray(OneBlock("the whole plan", "version", null, detail)));
            }
            var expectModel = Args.Str(args, "expect_model");
            if (expectModel != null && !string.Equals(expectModel, doc.Title, StringComparison.Ordinal))
            {
                var detail = $"the preview was made on {expectModel}, the active model is {doc.Title}";
                return Refuse("wrong_model", $"Nothing was changed: {detail}. Preview again on this model.", new JsonArray(OneBlock("the whole plan", "wrong-model", null, detail)));
            }
            if (!doc.IsWorkshared)
                return Refuse("blocked", "Nothing was changed: the model is not workshared.", new JsonArray(OneBlock("the model", "not-workshared", null, null)));
            if (doc.IsReadOnly || doc.IsFamilyDocument || doc.IsLinked)
                return Refuse("blocked", "Nothing was changed: the model cannot be edited.", new JsonArray(OneBlock("the model", "model", null, "a read-only, linked or family document")));
            // The plan object: workset_plan (1.13.1); "plan" as an object is still read for one release.
            var planArg = args["workset_plan"] as JsonObject ?? args["plan"] as JsonObject ?? args;
            JsonArray Arr(params string[] names) => names.Select(n => planArg[n] as JsonArray).FirstOrDefault(a => a != null) ?? new JsonArray();

            var worksets = UserWorksets(doc);
            var facts = Scan(doc, worksets, checkout: true);
            L.MarkDatum(worksets, DatumName(doc, args));
            var before = CountByCategory(doc);
            var me = app.Application.Username;
            var notes = new List<string>();
            var blocks = new List<L.Block>();
            var changed = new JsonArray(); var skipped = new JsonArray(); var kept = new JsonArray();

            // 1. rename, 2. create (checked against the renames' targets).
            var renames = Arr("rename", "renames").OfType<JsonObject>().Select(o => (o["from"]?.ToString(), o["to"]?.ToString())).ToList();
            var (renameSteps, renameBlocks) = L.PlanRename(worksets, renames, me);
            blocks.AddRange(renameBlocks);
            var renameTo = new HashSet<string>(renameSteps.Select(s => s.To), StringComparer.OrdinalIgnoreCase);
            var (createSteps, createBlocks) = P.PlanCreate(worksets, Arr("create").Select(n => n?.ToString()), renameTo);
            blocks.AddRange(createBlocks);

            // Names after the plan: an existing workset (renamed or not) by id, a created one by its name (id 0).
            var finalName = worksets.ToDictionary(w => w.Id, w => renameSteps.FirstOrDefault(s => s.WorksetId == w.Id)?.To ?? w.Name);
            (long Id, string Name)? Target(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                var n = name.Trim();
                var ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value, n, StringComparison.Ordinal));
                if (ex.Value == null) ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value, n, StringComparison.OrdinalIgnoreCase));
                if (ex.Value != null) return (ex.Key, ex.Value);
                var cr = createSteps.FirstOrDefault(s => string.Equals(s.Workset, n, StringComparison.OrdinalIgnoreCase));
                return cr != null ? (0L, cr.Workset) : ((long, string)?)null;
            }

            // 3. moves: by ids, by a group object, or by the group's fields (as the server sends them).
            var factById = facts.ToDictionary(f => f.Id);
            var byKey = facts.GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var moveSteps = new List<P.Step>();
            var claimed = new Dictionary<long, string>();
            var typeClaims = new Dictionary<long, (string Step, string To)>();   // a link type moves once, to one workset
            var moveIndex = 0;
            foreach (var mv in Arr("moves").OfType<JsonObject>())
            {
                moveIndex++;
                var g = mv["group"] as JsonObject ?? mv;
                var to = Target(mv["to"]?.ToString());
                string F(string field) => g[field]?.ToString();
                var key = g["key"]?.ToString() ?? (P.KeyFields.Any(k => g[k] != null) ? P.Key(F("workset"), F("category"), F("type"), F("level"), F("material"), F("zone"), F("option")) : null);
                var what = $"move {(key ?? "ids")} > {mv["to"]}";
                if (to == null) { blocks.Add(new L.Block { What = what, Code = "not-found", Detail = $"no workset is called {mv["to"]} after the creates and renames" }); continue; }
                List<Fact> found;
                if (mv["ids"] is JsonArray idArr)
                {
                    found = new List<Fact>();
                    foreach (var n in idArr)
                    {
                        if (n == null || !long.TryParse(n.ToString(), out var id)) continue;
                        if (factById.TryGetValue(id, out var fx)) found.Add(fx);
                        else skipped.Add(new JsonObject { ["key"] = "ids", ["ids"] = new JsonArray(id), ["holder"] = null, ["why"] = "not found on a user workset (deleted, a type, view-specific or a datum)" });
                    }
                }
                else if (key != null) found = byKey.TryGetValue(key, out var list) ? list : new List<Fact>();
                else { blocks.Add(new L.Block { What = what, Code = "naming", Detail = "a move needs ids, a group or the group's key fields" }); continue; }
                var expected = (g["elements"] ?? g["count"] ?? mv["elements"] ?? mv["count"]) is JsonValue ev && ev.TryGetValue<int>(out var exn) ? exn : (int?)null;
                if (key != null && mv["ids"] == null && expected.HasValue && expected.Value != found.Count)
                {
                    // The group changed since the facts: the whole plan stops (plan-changed), with the group named.
                    changed.Add(new JsonObject { ["key"] = key, ["expected"] = expected.Value, ["found"] = found.Count });
                    blocks.Add(new L.Block { What = what, Code = "plan-changed", Detail = $"the group holds {found.Count} element{(found.Count == 1 ? "" : "s")} now, the plan counted {expected.Value}" });
                    continue;
                }
                var step = new P.Step { Id = "move:" + moveIndex + ":" + (key ?? "ids"), Kind = "move", Workset = to.Value.Name, ToId = to.Value.Id, Key = key ?? "ids" };
                if (key != null) step.From = found.FirstOrDefault()?.Workset;
                foreach (var grpSkip in found.Where(f => f.Borrowed).GroupBy(f => f.Holder))
                    skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(grpSkip.Take(Math.Max(1, maxIds)).Select(f => (JsonNode)f.Id).ToArray()), ["count"] = grpSkip.Count(), ["holder"] = grpSkip.Key, ["why"] = "borrowed" });
                foreach (var grpSkip in found.Where(f => !f.Borrowed && f.Central).GroupBy(f => f.Workset))
                    skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(grpSkip.Take(Math.Max(1, maxIds)).Select(f => (JsonNode)f.Id).ToArray()), ["count"] = grpSkip.Count(), ["holder"] = null, ["why"] = "changed in central: synchronize first" });
                var readOnly = new List<long>();
                foreach (var f in found.Where(f => !f.Borrowed && !f.Central))
                {
                    if (to.Value.Id != 0 && f.WorksetId == to.Value.Id) continue;   // already there
                    var e = doc.GetElement(new ElementId(f.Id));
                    var p = e?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) { readOnly.Add(f.Id); continue; }
                    if (claimed.TryGetValue(f.Id, out var other)) { blocks.Add(new L.Block { What = what, Code = "naming", Detail = $"element {f.Id} is also moved by {other}", SampleIds = new List<long> { f.Id } }); continue; }
                    claimed[f.Id] = step.Id;
                    step.Ids.Add(f.Id);
                    // A link or CAD import: its type moves with it (RevitLinkType / CADLinkType carry a workset of their own).
                    if (f.TypeId != 0 && !(to.Value.Id != 0 && f.TypeWorksetId == to.Value.Id))
                    {
                        if (!f.TypeEditable) skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(f.TypeId), ["count"] = 1, ["holder"] = null, ["why"] = "the link type's workset cannot be set: the instance moves, its type stays" });
                        else if (typeClaims.TryGetValue(f.TypeId, out var tc))
                        {
                            if (!string.Equals(tc.To, to.Value.Name, StringComparison.OrdinalIgnoreCase))
                                blocks.Add(new L.Block { What = what, Code = "naming", Detail = $"the link type {f.TypeId} would go to {tc.To} and to {to.Value.Name}: its instances must share one workset", SampleIds = new List<long> { f.TypeId } });
                        }
                        else { typeClaims[f.TypeId] = (step.Id, to.Value.Name); step.TypeIds.Add(f.TypeId); }
                    }
                    var cat = f.Category;
                    step.ByCategory[cat] = step.ByCategory.TryGetValue(cat, out var c) ? c + 1 : 1;
                }
                if (readOnly.Count > 0)
                    skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(readOnly.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()), ["count"] = readOnly.Count, ["holder"] = null, ["why"] = "its workset cannot be set (read-only: e.g. hosted by or part of another element)" });
                step.Elements = step.Ids.Count;
                if (key != null && mv["ids"] == null && found.Count == 0 && expected != 0)
                {
                    blocks.Add(new L.Block { What = what, Code = "not-found", Detail = "no element matches this group now (moved, deleted or renamed since the facts)" });
                    continue;
                }
                if (step.Elements > 0) moveSteps.Add(step);
            }

            // 4. delete: only what is empty after the moves (else kept), or moved to move_to_for_delete.
            var now = worksets.ToDictionary(w => w.Id, w => w.Elements);
            var after = P.AfterMoves(now, moveSteps.SelectMany(s => s.Ids.Select(id => (factById[id].WorksetId, s.ToId, 1))));
            var afterWs = worksets.Select(w => new L.Workset
            {
                Id = w.Id, Name = w.Name, Owner = w.Owner, Open = w.Open, Editable = w.Editable, IsDefault = w.IsDefault, Protected = w.Protected, LevelsAndGrids = w.LevelsAndGrids, Types = w.Types,
                Elements = after.TryGetValue(w.Id, out var a) ? a : w.Elements, ByCategory = w.ByCategory, SampleIds = w.SampleIds,
            }).ToList();
            var deleteNames = Arr("delete").Select(n => n?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            var moveToForDelete = Args.Str(planArg, "move_to_for_delete") ?? Args.Str(args, "move_to_for_delete");
            var deleteSteps = new List<L.Step>();
            if (deleteNames.Count > 0)
            {
                var (ds, db, _) = L.PlanDelete(afterWs, deleteNames, moveToForDelete, me);
                foreach (var b in db)
                {
                    if (b.Code == "not-empty")
                    {
                        var w = afterWs.First(x => "delete " + x.Name == b.What);
                        var why = P.KeptWhy(w.Elements, w.Types);
                        kept.Add(new JsonObject { ["name"] = w.Name, ["elements"] = w.Elements, ["types"] = w.Types, ["byCategory"] = Counts(w.ByCategory), ["why"] = why });
                    }
                    else blocks.Add(b);
                }
                var targets = new HashSet<long>(moveSteps.Select(s => s.ToId));
                foreach (var st in ds)
                {
                    if (targets.Contains(st.WorksetId)) { blocks.Add(new L.Block { What = "delete " + st.Workset, Code = "naming", Detail = $"{st.Workset} receives elements in the same plan" }); continue; }
                    if (renameSteps.Any(r => r.WorksetId == st.WorksetId)) { blocks.Add(new L.Block { What = "delete " + st.Workset, Code = "naming", Detail = $"{st.Workset} is also renamed in the same plan" }); continue; }
                    deleteSteps.Add(st);
                }
            }

            // The whole plan, in run order, and its fingerprint.
            var steps = P.Ordered(createSteps
                .Concat(renameSteps.Select(s => new P.Step { Id = s.Id, Kind = "rename", Workset = s.From, From = s.From, To = s.To, WorksetId = s.WorksetId, Elements = s.Elements }))
                .Concat(moveSteps)
                .Concat(deleteSteps.Select(s => new P.Step
                {
                    Id = s.Id, Kind = s.Kind, Workset = s.Workset, WorksetId = s.WorksetId, To = s.MoveTo, ToId = s.MoveToId, Elements = s.Elements,
                    ByCategory = s.ByCategory ?? new Dictionary<string, int>(), Safety = s.Safety, SafetyId = s.SafetyId,
                })));
            var planHash = P.PlanHash(steps);

            // Visibility in the export views: elements whose target workset shows differently there than their own does now.
            var visibility = new JsonArray();
            var views = ExportViews(doc, args, notes);
            if (views.Count > 0 && moveSteps.Count > 0)
            {
                WorksetDefaultVisibilitySettings defaults = null;
                try { defaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc); } catch { defaults = null; }
                foreach (var ev in views)
                {
                    var cache = new Dictionary<long, bool>();
                    bool Vis(long wsId) => wsId == 0 || (cache.TryGetValue(wsId, out var b) ? b : (cache[wsId] = SafeVisible(ev, wsId, defaults)));   // a created workset shows (Use Global, visible)
                    foreach (var st in moveSteps)
                    {
                        var to = Vis(st.ToId);
                        var hide = st.Ids.Where(id => Vis(factById[id].WorksetId) && !to).ToList();
                        var show = st.Ids.Where(id => !Vis(factById[id].WorksetId) && to).ToList();
                        if (hide.Count == 0 && show.Count == 0) continue;
                        visibility.Add(new JsonObject
                        {
                            ["move"] = st.Id, ["key"] = st.Key, ["to"] = st.Workset, ["view"] = ev.Name, ["wouldHide"] = hide.Count, ["wouldShow"] = show.Count,
                            ["sampleIds"] = new JsonArray(hide.Concat(show).Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()),
                        });
                    }
                }
            }
            var planJson = new JsonArray(steps.Select(s => (JsonNode)PlanStepJson(s, maxIds)).ToArray());
            var blockedJson = new JsonArray(blocks.Select(b => (JsonNode)ToJson(b, maxIds)).ToArray());
            var checkedOut = new JsonArray();
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["action"] = action; r["planHash"] = planHash;
                r["plan"] = planJson.DeepClone(); r["blocked"] = blockedJson.DeepClone(); r["checkedOut"] = checkedOut.DeepClone();
                r["changed"] = changed.DeepClone(); r["skipped"] = skipped.DeepClone(); r["kept"] = kept.DeepClone();
                r["exportViews"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()); r["visibility"] = visibility.DeepClone();
                r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                return r;
            }
            JsonObject Zero() => new JsonObject { ["created"] = 0, ["renamed"] = 0, ["moved"] = 0, ["deleted"] = 0 };

            var expectPlan = Args.Str(args, "expect_plan");
            if (!checkOnly && !string.IsNullOrWhiteSpace(expectPlan) && !string.Equals(expectPlan.Trim(), planHash, StringComparison.OrdinalIgnoreCase))
            {
                const string why = "the model changed since the preview: run the dry run again";
                var refused = Common(Refuse("blocked", $"Nothing was changed: {why}.", null));
                refused["code"] = "plan-changed";
                ((JsonArray)refused["blocked"]).Insert(0, OneBlock("the whole plan", "plan-changed", null, why));
                refused["expectedPlan"] = expectPlan.Trim();
                return refused;
            }
            if (checkOnly)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["checkOnly"] = true; r["done"] = Zero(); r["verify"] = null;
                r["note"] = blocks.Count > 0 ? "Nothing was changed. Some items are blocked (see blocked): the dry run and the apply refuse until they are cleared or left out of the plan."
                    : steps.Count == 0 ? "Nothing was changed, and there is nothing to do." : "Nothing was changed. To apply: preview with dry_run: true, then confirm.";
                return r;
            }
            if (blocks.Count > 0)
            {
                var refused = Common(Refuse("blocked", $"Nothing was changed: {blocks.Count} item{(blocks.Count == 1 ? " is" : "s are")} blocked ({ToJson(blocks[0], 0)["why"]}). See blocked for what clears each one.", null));
                if (blocks.Any(b => b.Code == "plan-changed")) refused["code"] = "plan-changed";
                return refused;
            }
            if (steps.Count == 0)
            {
                var r = Common(new JsonObject());
                r["success"] = true; r["done"] = Zero(); r["verify"] = null; r["rolledBack"] = false; r["note"] = "Nothing to do: nothing was changed.";
                return r;
            }

            // Worksets that a rename or delete changes must be editable by this user: only the apply borrows them; the dry run
            // previews the rest and lists what it would borrow. Moves borrow their elements when the change is kept.
            var dryRun = Args.Bool(args, "dry_run");
            string NameOf(long id) => worksets.First(w => w.Id == id).Name;
            var need = steps.SelectMany(s => s.Kind == "rename" ? new[] { s.WorksetId } : s.Kind == "move-and-delete" ? new[] { s.WorksetId, s.ToId } : s.Kind == "delete" ? new[] { s.WorksetId, s.SafetyId } : new long[0])
                .Where(id => id != 0).Distinct().Where(id => worksets.FirstOrDefault(w => w.Id == id)?.Editable != true).ToList();
            var notPreviewed = new HashSet<string>(StringComparer.Ordinal);
            var notYours = new HashSet<long>();   // dry run: elements it would move but does not (not owned by this user yet)
            if (dryRun)
            {
                if (need.Count > 0)
                {
                    foreach (var st in steps.Where(st => need.Contains(st.WorksetId) || (st.Kind != "move" && need.Contains(st.ToId)) || need.Contains(st.SafetyId))) notPreviewed.Add(st.Id);
                    notes.Add($"The apply would borrow {string.Join(", ", need.Select(NameOf))} (given back when you synchronize with central). The dry run did not borrow them, so it could not preview: {string.Join(", ", notPreviewed)}.");
                }
                // Elements this user does not own yet (nor their workset): setting them would borrow them, and a rolled-back dry run
                // does not give them back. The dry run leaves them alone; only the apply borrows them.
                var editable = new HashSet<long>(worksets.Where(w => w.Editable).Select(w => w.Id));
                bool Mine(long id)
                {
                    try
                    {
                        var e = doc.GetElement(new ElementId(id));
                        return e != null && (editable.Contains(e.WorksetId.IntegerValue) || WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByCurrentUser);
                    }
                    catch { return false; }
                }
                foreach (var id in moveSteps.SelectMany(s => s.Ids.Concat(s.TypeIds))) if (!Mine(id)) notYours.Add(id);
                if (notYours.Count > 0)
                {
                    // A delete whose workset still holds elements the dry run did not move cannot be previewed either.
                    var stillOn = new HashSet<long>(moveSteps.SelectMany(s => s.Ids).Where(notYours.Contains).Select(id => factById[id].WorksetId));
                    foreach (var st in steps.Where(st => (st.Kind == "delete" || st.Kind == "move-and-delete") && stillOn.Contains(st.WorksetId))) notPreviewed.Add(st.Id);
                    notes.Add($"{notYours.Count} element{(notYours.Count == 1 ? "" : "s")} the plan moves {(notYours.Count == 1 ? "is" : "are")} not yours yet: the dry run did not move them (would move, not previewed); the apply borrows them (given back when you synchronize with central).");
                }
            }
            else if (need.Count > 0)
            {
                var ownedBefore = worksets.Any(w => w.Editable);
                ICollection<WorksetId> got = null; string error = null;
                try { got = WorksharingUtils.CheckoutWorksets(doc, need.Select(id => new WorksetId((int)id)).ToList()); }
                catch (Exception ex) { error = ex.Message; }
                var missing = need.Where(id => got == null || !got.Any(g => g.IntegerValue == id)).ToList();
                foreach (var id in need.Except(missing)) checkedOut.Add(NameOf(id));
                if (missing.Count > 0)
                {
                    if (checkedOut.Count > 0)
                    {
                        if (!ownedBefore)
                            try { WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(false) { UserWorksets = true }, new TransactWithCentralOptions()); notes.Add("The worksets borrowed by this run were given back."); checkedOut.Clear(); }
                            catch (Exception ex) { notes.Add("The worksets borrowed by this run could not be given back (" + ex.Message + "): they are given back at your next sync."); }
                        else notes.Add("The worksets borrowed by this run stay borrowed until your next sync (you already held other worksets, which ACE does not give back for you).");
                    }
                    return Common(Refuse("blocked", $"Nothing was changed: {string.Join(", ", missing.Select(NameOf))} could not be borrowed" + (error == null ? "." : $" ({error})."),
                        new JsonArray(missing.Select(id => (JsonNode)OneBlock(NameOf(id), error == null ? "borrowed" : "central", null, error ?? "Revit did not give the workset to this user")).ToArray())));
                }
                notes.Add("ACE borrowed the worksets it renames or deletes (checkedOut); they are given back when you synchronize with central.");
            }

            var ran = new List<P.Step>();
            var created = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int OnWorkset(Document d, long id) => new FilteredElementCollector(d).WherePasses(new ElementWorksetFilter(new WorksetId((int)id), false)).GetElementCount();
            var result = NativeChange.Run(app, args, "workset_admin", "ACE: apply the workset plan", (d, plan) =>
            {
                var r = Common(new JsonObject());
                var done = Zero();
                r["done"] = done; r["verify"] = null;
                if (plan) return r;
                ran.Clear(); created.Clear();
                void Count(string k, int n) => done[k] = (int)done[k] + n;
                foreach (var st in steps)
                {
                    if (notPreviewed.Contains(st.Id)) continue;
                    switch (st.Kind)
                    {
                        case "create":
                            created[st.Workset] = Workset.Create(d, st.Workset).Id.IntegerValue;
                            Count("created", 1);
                            break;
                        case "rename":
                            WorksetTable.RenameWorkset(d, new WorksetId((int)st.WorksetId), st.To);
                            Count("renamed", 1);
                            break;
                        case "move":
                        {
                            var toId = st.ToId != 0 ? st.ToId : created.TryGetValue(st.Workset, out var cid) ? cid : throw new CommandException($"Nothing was changed: the workset {st.Workset} was not created.");
                            foreach (var id in st.Ids)
                            {
                                if (notYours.Contains(id)) continue;
                                var p = d.GetElement(new ElementId(id))?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                if (p == null || p.IsReadOnly || !p.Set((int)toId))
                                    throw new CommandException($"Nothing was changed: element {id} could not be moved to {st.Workset}. Run the dry run again.");
                            }
                            foreach (var id in st.TypeIds)
                            {
                                if (notYours.Contains(id)) continue;
                                var p = d.GetElement(new ElementId(id))?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                if (p == null || p.IsReadOnly || !p.Set((int)toId))
                                    throw new CommandException($"Nothing was changed: the link type {id} could not be moved to {st.Workset}. Run the dry run again.");
                            }
                            Count("moved", st.Ids.Count(id => !notYours.Contains(id)));
                            break;
                        }
                        default:
                        {
                            var nowCount = OnWorkset(d, st.WorksetId);
                            if (nowCount != st.Elements)
                                throw new CommandException($"Nothing was changed: {st.Workset} holds {nowCount} element{(nowCount == 1 ? "" : "s")} after the moves, the plan counted {st.Elements}. Run the dry run again.");
                            var settings = st.Kind == "move-and-delete"
                                ? new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, new WorksetId((int)st.ToId))
                                : new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, new WorksetId((int)st.SafetyId));
                            WorksetTable.DeleteWorkset(d, new WorksetId((int)st.WorksetId), settings);
                            Count("deleted", 1);
                            break;
                        }
                    }
                    ran.Add(st);
                }
                if (notPreviewed.Count > 0) r["notPreviewed"] = new JsonArray(notPreviewed.Select(x => (JsonNode)x).ToArray());
                if (notYours.Count > 0)
                    r["notPreviewedCounts"] = new JsonObject
                    {
                        ["moves"] = steps.Count(x => x.Kind == "move" && x.Ids.Any(notYours.Contains)),
                        ["elements"] = steps.Where(x => x.Kind == "move").Sum(x => x.Ids.Count(notYours.Contains)),
                        ["linkTypes"] = steps.Where(x => x.Kind == "move").Sum(x => x.TypeIds.Count(notYours.Contains)),
                    };
                return r;
            }, verify: (d, r) =>
            {
                var mismatches = L.CompareCounts(before, CountByCategory(d));
                var names = new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset).ToWorksets().ToDictionary(w => (long)w.Id.IntegerValue, w => w.Name);
                foreach (var st in ran)
                {
                    switch (st.Kind)
                    {
                        case "create":
                            if (!created.TryGetValue(st.Workset, out var cid) || !names.TryGetValue(cid, out var cn) || cn != st.Workset) mismatches.Add(("workset " + st.Workset, "created", "missing"));
                            break;
                        case "rename":
                            names.TryGetValue(st.WorksetId, out var rn);
                            if (rn != st.To) mismatches.Add(("name of " + st.From, st.To, rn ?? "(gone)"));
                            break;
                        case "move":
                        {
                            var toId = st.ToId != 0 ? st.ToId : created.TryGetValue(st.Workset, out var c2) ? c2 : -1;
                            var checkIds = st.Ids.Concat(st.TypeIds).Where(id => !notYours.Contains(id)).ToList();   // the dry run moved only these
                            var off = checkIds.Where(id => { var e = d.GetElement(new ElementId(id)); return e == null || e.WorksetId.IntegerValue != toId; }).ToList();
                            var all = checkIds.Count;
                            if (off.Count > 0) mismatches.Add(($"elements moved to {st.Workset}", all.ToString(), $"{all - off.Count} (not there: {string.Join(", ", off.Take(5))})"));
                            break;
                        }
                        default:
                            if (names.ContainsKey(st.WorksetId)) mismatches.Add(("workset " + st.Workset, "deleted", "still there"));
                            break;
                    }
                }
                r["verify"] = new JsonObject
                {
                    ["ok"] = mismatches.Count == 0, ["checked"] = before.Count,
                    ["mismatches"] = new JsonArray(mismatches.Take(50).Select(m => (JsonNode)new JsonObject
                    {
                        ["what"] = m.What, ["category"] = m.What.StartsWith("elements: ", StringComparison.Ordinal) ? m.What.Substring(10) : null, ["before"] = m.Before, ["after"] = m.After,
                    }).ToArray()),
                };
                return mismatches.Count == 0;
            });
            if (result["rolledBack"] is JsonValue rb && rb.TryGetValue<bool>(out var rolled) && rolled && result["done"] is JsonObject dn)
            {
                result["wouldDo"] = dn.DeepClone();
                result["done"] = Zero();
                if (result["transaction"]?.ToString() == "VerifyFailed")
                    result["note"] = "The self-check found a change beyond the plan (see verify.mismatches): the whole change was rolled back. Nothing was changed.";
            }
            if (result["verify"] is JsonObject v && v["error"] != null && v["checked"] == null) v["checked"] = before.Count;
            return result;
        }

        private static JsonObject PlanStepJson(P.Step s, int maxIds)
        {
            var o = new JsonObject { ["id"] = s.Id, ["kind"] = s.Kind == "move-and-delete" ? "delete" : s.Kind, ["workset"] = s.Workset };
            switch (s.Kind)
            {
                case "rename": o["from"] = s.From; o["to"] = s.To; break;
                case "move":
                    o["key"] = s.Key; o["from"] = s.From; o["to"] = s.Workset; o["elements"] = s.Elements; o["byCategory"] = Counts(s.ByCategory);
                    o["sampleIds"] = new JsonArray(s.Ids.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray());
                    if (s.TypeIds.Count > 0) o["linkTypes"] = new JsonArray(s.TypeIds.Select(i => (JsonNode)i).ToArray());
                    break;
                case "delete": case "move-and-delete":
                    o["elements"] = s.Elements; o["byCategory"] = Counts(s.ByCategory);
                    if (s.Kind == "move-and-delete") o["moveTo"] = s.To; else o["safetyTarget"] = s.Safety;
                    break;
            }
            return o;
        }
    }
}
