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
    /// elements, RVT links, CAD imports; not types, levels and grids, not sketches or view-only items ("nonModel"), not
    /// sub-elements such as stair runs, rail parts and curtain panels, which follow their host ("followsHost")) grouped by
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
        /// <summary>What a scan leaves out of the groups: sub-elements (with their host) and non-model items, by element id.</summary>
        private sealed class ScanInfo
        {
            public Dictionary<long, (long Ws, long Host)> Followers = new Dictionary<long, (long, long)>();
            public Dictionary<long, long> NonModel = new Dictionary<long, long>();   // id > workset
        }

        private static readonly HashSet<BuiltInCategory> ViewOnlyCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_LegendComponents, BuiltInCategory.OST_Cameras, BuiltInCategory.OST_Viewers, BuiltInCategory.OST_SectionBox,
            BuiltInCategory.OST_VolumeOfInterest, BuiltInCategory.OST_Matchline,
        };

        /// <summary>Never grouped: views and view-only items, reference planes, sketches (their planes and lines), non-model categories.</summary>
        private static bool IsNonModel(Element e, string category)
        {
            if (e.ViewSpecific) return true;   // 2D view-specific imports too: only model-space links and imports group
            if (e is RevitLinkInstance || e is ImportInstance) return false;
            if (e is View || e is ReferencePlane || e is Sketch || e is SketchPlane || P.IsSketchCategory(category)) return true;
            try
            {
                var c = e.Category;
                return c == null || c.CategoryType != CategoryType.Model || ViewOnlyCategories.Contains(c.BuiltInCategory);
            }
            catch { return true; }
        }

        /// <summary>The element a sub-element belongs to (0 = none): a nested part, a stair run or landing, a railing's top or hand rail.</summary>
        private static long HostOf(Element e)
        {
            try
            {
                switch (e)
                {
                    case FamilyInstance fi when fi.SuperComponent != null: return fi.SuperComponent.Id.Value;
                    case Autodesk.Revit.DB.Architecture.StairsRun run: return run.GetStairs()?.Id.Value ?? 0;
                    case Autodesk.Revit.DB.Architecture.StairsLanding landing: return landing.GetStairs()?.Id.Value ?? 0;
                    case Autodesk.Revit.DB.Architecture.ContinuousRail rail: return rail.HostRailingId == null || rail.HostRailingId == ElementId.InvalidElementId ? 0 : rail.HostRailingId.Value;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Sub-elements only their host names: a stair's supports, a curtain wall's panels and mullions.</summary>
        private static IEnumerable<ElementId> SubsOf(Element e)
        {
            try
            {
                if (e is Autodesk.Revit.DB.Architecture.Stairs st) return st.GetStairsSupports();
                if (e is Wall w && w.CurtainGrid is CurtainGrid g) return g.GetPanelIds().Concat(g.GetMullionIds()).ToList();
            }
            catch { }
            return Enumerable.Empty<ElementId>();
        }

        private static List<Fact> Scan(Document doc, List<L.Workset> worksets, bool checkout, ScanInfo info = null)
        {
            info = info ?? new ScanInfo();
            var subHost = new Dictionary<long, long>();
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
                if (e is Level || e is Grid) { w.LevelsAndGrids++; continue; }

                // Groupable: model elements, links and imports. Not grouped: non-model items (sketches, view-only, reference
                // planes) and sub-elements, which follow their host.
                if (IsNonModel(e, cat)) { info.NonModel[e.Id.Value] = ws; continue; }
                var host = HostOf(e);
                if (host != 0) { info.Followers[e.Id.Value] = (ws, host); continue; }
                foreach (var sub in SubsOf(e)) if (sub != null && sub != ElementId.InvalidElementId) subHost[sub.Value] = e.Id.Value;
                var isLink = e is RevitLinkInstance; var isImport = e is ImportInstance;

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
            // Sub-elements named by their host (stair supports, curtain panels and mullions) leave the groups too.
            foreach (var f in facts.Where(f => subHost.ContainsKey(f.Id) && f.Id != subHost[f.Id])) info.Followers[f.Id] = (f.WorksetId, subHost[f.Id]);
            facts.RemoveAll(f => info.Followers.ContainsKey(f.Id));
            // A part of a part follows the outermost host (the element a plan moves).
            foreach (var id in info.Followers.Keys.ToList())
            {
                var (ws0, h) = info.Followers[id];
                for (var i = 0; i < 10 && info.Followers.TryGetValue(h, out var up) && up.Host != id; i++) h = up.Host;
                info.Followers[id] = (ws0, h);
            }
            foreach (var x in info.Followers.Values) if (byId.TryGetValue(x.Ws, out var fw)) fw.FollowsHost++;
            foreach (var x in info.NonModel.Values) if (byId.TryGetValue(x, out var nw)) nw.NonModel++;
            return facts;
        }

        private static List<L.Workset> UserWorksets(Document doc) =>
            new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => new L.Workset { Id = w.Id.IntegerValue, Name = w.Name, Owner = w.Owner, Open = w.IsOpen, Editable = w.IsEditable, IsDefault = w.IsDefaultWorkset }).ToList();

        // ---- facts ------------------------------------------------------------------------------------------------

        /// <summary>The views to report workset visibility for: the names given, else the 3D views named like NAVISWORKS, NWC, IFC or {3D}.</summary>
        private static List<View> ExportViews(Document doc, JsonObject args, List<string> notes)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var given = Args.Strings(args, "views").Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            if (given.Count == 0)
                return all.OfType<View3D>().Where(v => new[] { "NAVISWORKS", "NWC", "IFC", "{3D}" }.Any(k => v.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
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
                .Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["code"] = P.LevelCode(l.Name), ["phrase"] = P.LevelPhrase(l.Name), ["elevation"] = Math.Round(Lengths.Mm(l.Elevation)) }).ToArray());
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
                    ["id"] = w.Id, ["name"] = w.Name, ["elements"] = w.Elements, ["types"] = w.Types, ["followsHost"] = w.FollowsHost, ["nonModel"] = w.NonModel,
                    ["byCategory"] = Counts(w.ByCategory), ["isDefault"] = w.IsDefault, ["protected"] = L.IsProtected(w), ["editable"] = w.Editable,
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
            var info = new ScanInfo();
            var facts = Scan(doc, worksets, checkout: true, info);
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
                // The exact name first (a real name may end in a space), then ignoring case, then ignoring leading/trailing spaces.
                var ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.Ordinal));
                if (ex.Value == null) ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
                if (ex.Value == null) ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value.Trim(), n, StringComparison.Ordinal));
                if (ex.Value == null) ex = finalName.FirstOrDefault(kv => string.Equals(kv.Value.Trim(), n, StringComparison.OrdinalIgnoreCase));
                if (ex.Value != null) return (ex.Key, ex.Value);
                var cr = createSteps.FirstOrDefault(s => string.Equals(s.Workset, n, StringComparison.OrdinalIgnoreCase));
                return cr != null ? (0L, cr.Workset) : ((long, string)?)null;
            }

            // 3. moves: by ids, by a group object, or by the group's fields (as the server sends them).
            var factById = facts.ToDictionary(f => f.Id);
            var byKey = facts.GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var moveSteps = new List<P.Step>();
            var claimed = new Dictionary<long, string>();
            var claimedTo = new Dictionary<long, string>();
            var typeClaims = new Dictionary<long, (string Step, string To)>();   // a link type moves once, to one workset
            var pinnedIds = new List<long>();
            var repinned = new List<(long Id, long To)>();   // pinned elements the move had to unpin, set and pin again
            var pinnedDirect = 0;                              // pinned elements Revit moved without unpinning
            var pinBefore = new Dictionary<long, bool>();      // the pin state of every element the run set: the same after (else roll back)
            var pinFailed = new List<long>();                  // pinned elements whose workset could not be set even unpinned: skipped, pinned again
            var visHeld = new HashSet<long>();                  // moves held back: they change what a view on a sheet shows
            var visTypes = new HashSet<long>();                 // link types put back with their held instances
            bool Left(long id) => pinFailed.Contains(id) || visHeld.Contains(id) || visTypes.Contains(id);   // elements the run leaves where they are
            var retried = new JsonArray();
            var retrySteps = new Dictionary<string, P.Step>(StringComparer.OrdinalIgnoreCase);   // pending_retry answers and steps
            var pending = new JsonArray(); var pendingTotal = 0; var pendingIds = new HashSet<long>();
            void PendingOne(long id, string category, bool borrowed, string holder, string from, string to)
            {
                if (!pendingIds.Add(id)) return;   // one entry per element, with the first (the group's) target
                pendingTotal++;
                if (pending.Count < 1000)
                    pending.Add(new JsonObject { ["id"] = id, ["category"] = category, ["owner"] = borrowed ? holder : null, ["holder"] = borrowed ? holder : null, ["why"] = borrowed ? "borrowed" : "changed in central", ["from"] = from, ["to"] = to });
            }
            void Pending(IEnumerable<Fact> held, string to)
            {
                foreach (var f in held) PendingOne(f.Id, f.Category, f.Borrowed, f.Holder, f.Workset, to);
            }
            // What stays where it is: the datum workset, keep_worksets (by name now or after the renames) and keep_keys groups.
            var keepNames = (planArg["keep_worksets"] as JsonArray ?? args["keep_worksets"] as JsonArray ?? new JsonArray()).Select(n => n?.ToString()?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var keep = new HashSet<long>(worksets.Where(w => L.IsProtected(w) || keepNames.Any(n => string.Equals(n, w.Name.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(n, finalName[w.Id].Trim(), StringComparison.OrdinalIgnoreCase))).Select(w => w.Id));
            var keepKeys = new HashSet<string>((planArg["keep_keys"] as JsonArray ?? args["keep_keys"] as JsonArray ?? new JsonArray()).Select(k => k?.ToString()).Where(k => k != null), StringComparer.Ordinal);
            var datumIds = new HashSet<long>(worksets.Where(L.IsProtected).Select(w => w.Id));
            void Take(P.Step step, Fact f, (long Id, string Name) to, string what, List<long> readOnly)
            {
                // An element named twice: the same target is one move (a retry and its group row), two targets are a conflict.
                if (claimed.TryGetValue(f.Id, out var other) && other.StartsWith("retry:", StringComparison.Ordinal) && !step.Id.StartsWith("retry:", StringComparison.Ordinal)
                    && !string.Equals(claimedTo[f.Id], to.Name, StringComparison.OrdinalIgnoreCase) && retrySteps.TryGetValue(claimedTo[f.Id], out var rs))
                {
                    // A stale retry: this plan puts the element elsewhere, and the plan wins.
                    rs.Ids.Remove(f.Id); rs.Elements = rs.Ids.Count;
                    foreach (var ro in retried.OfType<JsonObject>().Where(x => (long)x["id"] == f.Id)) { ro["ok"] = false; ro["why"] = $"superseded: this plan moves it to {to.Name}"; ro["drop"] = true; }
                    claimed.Remove(f.Id);
                }
                if (claimed.TryGetValue(f.Id, out other))
                {
                    if (!string.Equals(claimedTo[f.Id], to.Name, StringComparison.OrdinalIgnoreCase))
                        blocks.Add(new L.Block { What = what, Code = "naming", Detail = $"element {f.Id} is also moved by {other} (to {claimedTo[f.Id]})", SampleIds = new List<long> { f.Id } });
                    return;
                }
                claimed[f.Id] = step.Id; claimedTo[f.Id] = to.Name;   // claimed even when already there: reset leaves it alone
                if (to.Id != 0 && f.WorksetId == to.Id) return;   // already there
                var e = doc.GetElement(new ElementId(f.Id));
                var p = e?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                var isPinned = false;
                try { isPinned = e.Pinned; } catch { isPinned = false; }
                if (p == null || (p.IsReadOnly && !isPinned)) { readOnly.Add(f.Id); return; }   // pinned: the move unpins, sets and pins again if Revit refuses
                step.Ids.Add(f.Id);
                if (isPinned) pinnedIds.Add(f.Id);
                // A link or CAD import: its type moves with it (RevitLinkType / CADLinkType carry a workset of their own).
                if (f.TypeId != 0 && !(to.Id != 0 && f.TypeWorksetId == to.Id))
                {
                    if (!f.TypeEditable) skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(f.TypeId), ["count"] = 1, ["holder"] = null, ["why"] = "the link type's workset cannot be set: the instance moves, its type stays" });
                    else if (typeClaims.TryGetValue(f.TypeId, out var tc))
                    {
                        if (!string.Equals(tc.To, to.Name, StringComparison.OrdinalIgnoreCase))
                            blocks.Add(new L.Block { What = what, Code = "naming", Detail = $"the link type {f.TypeId} would go to {tc.To} and to {to.Name}: its instances must share one workset", SampleIds = new List<long> { f.TypeId } });
                    }
                    else { typeClaims[f.TypeId] = (step.Id, to.Name); step.TypeIds.Add(f.TypeId); }
                }
                step.ByCategory[f.Category] = step.ByCategory.TryGetValue(f.Category, out var c) ? c + 1 : 1;
            }
            JsonObject SkipJson(string key, IEnumerable<long> ids, int count, string holder, string why) =>
                new JsonObject { ["key"] = key, ["ids"] = new JsonArray(ids.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()), ["count"] = count, ["holder"] = holder, ["why"] = why };
            // 3a. pending_retry: elements borrowed on the last run go first, if this user can take them now; the rest stay pending.
            var retryArr = planArg["pending_retry"] as JsonArray ?? args["pending_retry"] as JsonArray;
            if (retryArr != null)
            {
                var retryReadOnly = new List<long>();
                foreach (var o in retryArr.OfType<JsonObject>())
                {
                    if (o["id"] == null || !long.TryParse(o["id"].ToString(), out var id)) continue;
                    var to = Target(o["to"]?.ToString());
                    // drop: a permanent answer (the server removes the id from its pending file); without it the id is retried again.
                    void Retried(bool ok, string why, bool drop = false)
                    {
                        var ro = new JsonObject { ["id"] = id, ["ok"] = ok, ["why"] = why };
                        if (drop) ro["drop"] = true;
                        retried.Add(ro);
                    }
                    if (to == null) { Retried(false, $"no workset is called {o["to"]} after the creates and renames", true); continue; }
                    if (!factById.TryGetValue(id, out var f)) { Retried(false, info.Followers.ContainsKey(id) ? "a sub-element: it follows its host" : "not found on a user workset (deleted, or no longer a model element)", true); continue; }
                    if (to.Value.Id != 0 && f.WorksetId == to.Value.Id) { Retried(true, "already there"); continue; }
                    if (keep.Contains(f.WorksetId) || keepKeys.Contains(f.Key) || datumIds.Contains(to.Value.Id)) { Retried(false, "kept", true); continue; }
                    if (f.Borrowed || f.Central) { Pending(new[] { f }, to.Value.Name); Retried(false, f.Borrowed ? "still borrowed" + (f.Holder != null ? " by " + f.Holder : "") : "changed in central: synchronize first"); continue; }
                    if (!retrySteps.TryGetValue(to.Value.Name, out var st))
                        retrySteps[to.Value.Name] = st = new P.Step { Id = "retry:" + to.Value.Name, Kind = "move", Workset = to.Value.Name, ToId = to.Value.Id, Key = "retry" };
                    var r0 = retryReadOnly.Count; var b0 = blocks.Count;
                    Take(st, f, to.Value, "retry " + id + " > " + to.Value.Name, retryReadOnly);
                    if (retryReadOnly.Count > r0) Retried(false, "its workset cannot be set (read-only: e.g. hosted by or part of another element)", true);
                    else if (blocks.Count > b0) Retried(false, "named twice in pending_retry with different targets", true);
                    else Retried(true, st.Ids.Contains(id) ? "moved" : "already there");
                }
                foreach (var st in retrySteps.Values) { st.Elements = st.Ids.Count; moveSteps.Add(st); }
            }
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
                        else skipped.Add(new JsonObject
                        {
                            ["key"] = "ids", ["ids"] = new JsonArray(id), ["holder"] = null,
                            ["why"] = info.Followers.ContainsKey(id) ? "a sub-element: it follows its host (move the host)"
                                : info.NonModel.ContainsKey(id) ? "not a model element (a sketch, view-only item or reference plane): never moved by a plan"
                                : "not found on a user workset (deleted, a type, view-specific or a datum)",
                        });
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
                var step = new P.Step { Id = "move:" + moveIndex + ":" + (key ?? "ids"), Kind = "move", Workset = to.Value.Name, ToId = to.Value.Id, Key = key ?? "ids", Row = mv["row"]?.ToString() };
                if (key != null) step.From = found.FirstOrDefault()?.Workset;
                foreach (var grpSkip in found.Where(f => f.Borrowed).GroupBy(f => f.Holder))
                    skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(grpSkip.Take(Math.Max(1, maxIds)).Select(f => (JsonNode)f.Id).ToArray()), ["count"] = grpSkip.Count(), ["holder"] = grpSkip.Key, ["why"] = "borrowed" });
                foreach (var grpSkip in found.Where(f => !f.Borrowed && f.Central).GroupBy(f => f.Workset))
                    skipped.Add(new JsonObject { ["key"] = step.Key, ["ids"] = new JsonArray(grpSkip.Take(Math.Max(1, maxIds)).Select(f => (JsonNode)f.Id).ToArray()), ["count"] = grpSkip.Count(), ["holder"] = null, ["why"] = "changed in central: synchronize first" });
                Pending(found.Where(f => (f.Borrowed || f.Central) && !(to.Value.Id != 0 && f.WorksetId == to.Value.Id)), to.Value.Name);
                // Held elements belong to this move (pending with its target): reset never picks them up again.
                foreach (var f in found.Where(f => (f.Borrowed || f.Central) && !claimed.ContainsKey(f.Id))) { claimed[f.Id] = step.Id; claimedTo[f.Id] = to.Value.Name; }
                var readOnly = new List<long>();
                foreach (var f in found.Where(f => !f.Borrowed && !f.Central)) Take(step, f, to.Value, what, readOnly);
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

            // 3b. reset (reset_to): every other instance on a user workset goes to the default workset, except on the datum workset
            // and keep_worksets; borrowed ones are skipped. One step per source workset, in the same undo step.
            var resetTo = Args.Str(planArg, "reset_to") ?? Args.Str(args, "reset_to");
            JsonObject resetJson = null;
            if (!string.IsNullOrWhiteSpace(resetTo))
            {
                var to = Target(resetTo);
                if (to == null) blocks.Add(new L.Block { What = "reset_to " + resetTo.Trim(), Code = "not-found", Detail = $"no workset is called {resetTo.Trim()} after the creates and renames" });
                else
                {
                    foreach (var n in keepNames.Where(n => !worksets.Any(w => string.Equals(n, w.Name.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(n, finalName[w.Id].Trim(), StringComparison.OrdinalIgnoreCase))))
                        notes.Add($"keep_worksets names {n}, which is not a workset of this model.");
                    // keep_keys: groups that stay where they are (already placed, skipped rows, datum categories).
                    var (pick, held, central) = P.ResetPick(facts.Select(f => (f.Id, f.WorksetId, f.Key, f.Borrowed, f.Central)), claimed.Keys.ToList(), keep, keepKeys, to.Value.Id);
                    foreach (var g in held.Select(id => factById[id]).GroupBy(f => f.Holder)) skipped.Add(SkipJson("reset", g.Select(f => f.Id), g.Count(), g.Key, "borrowed"));
                    if (central.Count > 0) skipped.Add(SkipJson("reset", central, central.Count, null, "changed in central: synchronize first"));
                    Pending(held.Concat(central).Select(id => factById[id]), to.Value.Name);
                    var readOnly = new List<long>();
                    var resetCount = 0;
                    foreach (var g in pick.Select(id => factById[id]).GroupBy(f => f.WorksetId).OrderBy(g => g.First().Workset, StringComparer.OrdinalIgnoreCase))
                    {
                        var src = g.First().Workset;
                        var step = new P.Step { Id = "reset:" + src, Kind = "move", Workset = to.Value.Name, ToId = to.Value.Id, Key = "reset", From = src };
                        foreach (var f in g) Take(step, f, to.Value, "reset " + src + " > " + to.Value.Name, readOnly);
                        step.Elements = step.Ids.Count;
                        resetCount += step.Elements;
                        if (step.Elements > 0) moveSteps.Add(step);
                    }
                    if (readOnly.Count > 0) skipped.Add(SkipJson("reset", readOnly, readOnly.Count, null, "its workset cannot be set (read-only: e.g. hosted by or part of another element)"));
                    resetJson = new JsonObject
                    {
                        ["to"] = to.Value.Name, ["elements"] = resetCount,
                        ["kept"] = new JsonArray(worksets.Where(w => keep.Contains(w.Id)).Select(w => (JsonNode)finalName[w.Id]).ToArray()),
                        ["keptByKey"] = facts.Count(f => keepKeys.Contains(f.Key)), ["skippedBorrowed"] = held.Count, ["skippedCentral"] = central.Count, ["readOnly"] = readOnly.Count,
                    };
                }
            }

            // Sub-elements go with their host: the move sets them too where Revit does not (and the self-check looks).
            // Retry steps the plan emptied (stale targets) are dropped; the rest count what they still move.
            moveSteps.RemoveAll(st => st.Key == "retry" && st.Ids.Count == 0);
            foreach (var st in moveSteps.Where(st => st.Key == "retry"))
                st.ByCategory = st.Ids.GroupBy(id => factById[id].Category).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var followersOf = info.Followers.GroupBy(kv => kv.Value.Host).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
            foreach (var st in moveSteps)
                foreach (var id in st.Ids)
                    if (followersOf.TryGetValue(id, out var subs))
                        foreach (var x in subs.Where(x => !claimed.ContainsKey(x)))
                        {
                            // A sub-element borrowed by someone else (or changed in central) cannot go with its host now: pending.
                            var held = false; var central = false; string holder = null;
                            try
                            {
                                var xe = new ElementId(x);
                                held = WorksharingUtils.GetCheckoutStatus(doc, xe, out var owner) == CheckoutStatus.OwnedByOtherUser;
                                if (held) holder = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
                                else { var up = WorksharingUtils.GetModelUpdatesStatus(doc, xe); central = up == ModelUpdatesStatus.UpdatedInCentral || up == ModelUpdatesStatus.DeletedInCentral; }
                            }
                            catch { }
                            if (held || central)
                            {
                                var fws = info.Followers[x].Ws;
                                PendingOne(x, CategoryName(doc.GetElement(new ElementId(x))), held, holder, worksets.FirstOrDefault(w => w.Id == fws)?.Name, st.Workset);
                                continue;
                            }
                            st.Followers.Add(x);
                        }

            // 4. delete: only what is empty after the moves (else kept), or moved to move_to_for_delete. Non-model items (sketches,
            // reference planes, view-only items) never block a delete: they go to the receiving workset with it.
            var now = worksets.ToDictionary(w => w.Id, w => w.Elements);
            var after = P.AfterMoves(now, moveSteps.SelectMany(s => s.Ids.Select(id => (factById[id].WorksetId, s.ToId, 1))
                .Concat(s.Followers.Select(id => (info.Followers[id].Ws, s.ToId, 1)))));
            var nonModelOn = info.NonModel.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());
            var afterWs = worksets.Select(w => new L.Workset
            {
                Id = w.Id, Name = w.Name, Owner = w.Owner, Open = w.Open, Editable = w.Editable, IsDefault = w.IsDefault, Protected = w.Protected, LevelsAndGrids = w.LevelsAndGrids, Types = w.Types,
                Elements = (after.TryGetValue(w.Id, out var a) ? a : w.Elements) - (nonModelOn.TryGetValue(w.Id, out var nm) ? nm : 0), ByCategory = w.ByCategory, SampleIds = w.SampleIds,
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
                        kept.Add(new JsonObject { ["name"] = w.Name, ["elements"] = w.Elements, ["types"] = w.Types, ["nonModel"] = nonModelOn.TryGetValue(w.Id, out var kn) ? kn : 0, ["byCategory"] = Counts(w.ByCategory), ["why"] = why });
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
                    NonModel = nonModelOn.TryGetValue(s.WorksetId, out var dnm) ? dnm : 0,
                })));
            var planHash = P.PlanHash(steps);
            foreach (var n in steps.Where(x => x.Kind != "create").Select(x => L.SpacesNote(x.Kind == "rename" ? x.From : x.Workset)).Where(n => n != null).Distinct()) notes.Add(n);

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
            // Elements per workset before and after the plan (all elements, as audit counts them); verify adds what it measured.
            var planned = new Dictionary<long, int>(after);
            foreach (var st in deleteSteps)
            {
                var left = planned.TryGetValue(st.WorksetId, out var l0) ? l0 : 0;
                planned.Remove(st.WorksetId);
                var rx = st.Kind == "move-and-delete" ? st.MoveToId : st.SafetyId;
                if (rx != 0) planned[rx] = (planned.TryGetValue(rx, out var r0) ? r0 : 0) + left;
            }
            var worksetCounts = new JsonArray(worksets.Select(w => (JsonNode)new JsonObject
                {
                    ["workset"] = finalName[w.Id], ["id"] = w.Id, ["before"] = w.Elements, ["after"] = planned.TryGetValue(w.Id, out var pa) ? pa : (int?)null,
                    ["deleted"] = !planned.ContainsKey(w.Id),
                })
                .Concat(createSteps.Select(c => (JsonNode)new JsonObject
                {
                    ["workset"] = c.Workset, ["id"] = null, ["before"] = 0,
                    ["after"] = moveSteps.Where(m => m.ToId == 0 && string.Equals(m.Workset, c.Workset, StringComparison.OrdinalIgnoreCase)).Sum(m => m.Ids.Count + m.Followers.Count), ["deleted"] = false,
                })).ToArray());
            var blockedJson = new JsonArray(blocks.Select(b => (JsonNode)ToJson(b, maxIds)).ToArray());
            var checkedOut = new JsonArray();
            JsonObject Common(JsonObject r)
            {
                r["model"] = doc.Title; r["aceVersion"] = aceVersion; r["action"] = action; r["planHash"] = planHash;
                r["plan"] = planJson.DeepClone(); r["blocked"] = blockedJson.DeepClone(); r["checkedOut"] = checkedOut.DeepClone();
                r["changed"] = changed.DeepClone(); r["skipped"] = skipped.DeepClone(); r["kept"] = kept.DeepClone();
                r["exportViews"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()); r["visibility"] = visibility.DeepClone();
                r["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                r["worksetCounts"] = worksetCounts.DeepClone();
                if (resetJson != null) r["reset"] = resetJson.DeepClone();
                r["pending"] = pending.DeepClone(); r["pendingTotal"] = pendingTotal;
                if (retryArr != null) r["retried"] = retried.DeepClone();   // borrowed / changed in central: the rest moves, these wait
                r["pinned"] = new JsonObject { ["count"] = pinnedIds.Count, ["sampleIds"] = new JsonArray(pinnedIds.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()), ["directOk"] = 0, ["unpinRepin"] = 0 };
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
                foreach (var id in moveSteps.SelectMany(s => s.Ids.Concat(s.TypeIds).Concat(s.Followers))) if (!Mine(id)) notYours.Add(id);
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
            // Sets an element's workset; a pinned element Revit refuses is unpinned, set and pinned again (same undo step).
            bool SetWorkset(Document d, long id, long toId)
            {
                var e = d.GetElement(new ElementId(id));
                var p = e?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p == null) return false;
                var wasPinned = e.Pinned;
                pinBefore[id] = wasPinned;
                bool TrySet()
                {
                    try { return !p.IsReadOnly && p.Set((int)toId); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
                    catch (InvalidOperationException) { return false; }
                    catch (ArgumentException) { return false; }
                }
                if (TrySet()) { if (wasPinned) pinnedDirect++; return true; }
                if (!wasPinned) return false;
                var ok = false;
                try { e.Pinned = false; ok = TrySet(); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { ok = false; }
                catch (InvalidOperationException) { ok = false; }
                catch (ArgumentException) { ok = false; }
                finally
                {
                    try { if (!e.Pinned) e.Pinned = true; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                    catch (InvalidOperationException) { }
                    catch (ArgumentException) { }
                }
                if (ok) repinned.Add((id, toId)); else pinFailed.Add(id);
                return ok;
            }
            var created = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int OnWorkset(Document d, long id) => new FilteredElementCollector(d).WherePasses(new ElementWorksetFilter(new WorksetId((int)id), false)).GetElementCount();
            // Visibility on sheets: every graphical view placed on a sheet, and the export 3D views, are compared before and after the
            // moves inside the change (dry run and apply alike), among the moved elements. A change on a sheet view holds those
            // elements back (they stay where they are, in both runs) unless allow_visibility_change accepts it (true, or names of
            // views or worksets). Export views are reported only.
            var allowVisArg = planArg["allow_visibility_change"] ?? args["allow_visibility_change"];
            var allowVisAll = allowVisArg is JsonValue avv && avv.TryGetValue<bool>(out var avb) && avb;
            var allowVisNames = allowVisArg is JsonArray ava ? ava.Select(n => n?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() : new List<string>();
            var sheetOf = new Dictionary<long, string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                try { if (!sheetOf.ContainsKey(vp.ViewId.Value) && doc.GetElement(vp.SheetId) is ViewSheet sh) sheetOf[vp.ViewId.Value] = $"{sh.SheetNumber} - {sh.Name}"; } catch { }
            bool Graphical(View v) => !v.IsTemplate && v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section or ViewType.ThreeD
                or ViewType.Detail or ViewType.EngineeringPlan or ViewType.AreaPlan;
            // One entry per view, in a fixed order (sheet views by sheet and name, then export views), so the dry run and the apply
            // check the same views; at most visibility_max_views views run the full compare (a view count, not a time, keeps the
            // two runs identical); the rest are reported as not checked and hold nothing.
            var checkViews = sheetOf.Keys.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null && Graphical(v))
                .Select(v => (Id: v.Id.Value, v.Name, Sheet: sheetOf[v.Id.Value])).OrderBy(v => v.Sheet, StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal)
                .Concat(views.Where(v => !sheetOf.ContainsKey(v.Id.Value)).Select(v => (Id: v.Id.Value, v.Name, Sheet: (string)null)).OrderBy(v => v.Name, StringComparer.Ordinal))
                .GroupBy(v => v.Id).Select(g => g.First()).ToList();
            var visMaxViews = Math.Max(1, Math.Min(5000, Args.Int(args, "visibility_max_views", 400)));
            // Views whose filters may test the workset: there the full compare always runs (filters can hide by workset).
            var wsParam = new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM);
            bool FilterUsesWorkset(ElementFilter f, int depth = 0)
            {
                if (f == null || depth > 20) return true;
                if (f is ElementParameterFilter pf) return pf.GetRules().Any(rule => { try { return rule.GetRuleParameter() == wsParam; } catch { return true; } });
                if (f is ElementLogicalFilter lf) return lf.GetFilters().Any(x => FilterUsesWorkset(x, depth + 1));
                return true;   // a filter kind not read here: assume it may
            }
            bool ViewFiltersUseWorkset(View v)
            {
                try
                {
                    var ids = v.GetFilters().ToList();
                    if (v.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(v.ViewTemplateId) is View tpl) ids.AddRange(tpl.GetFilters());
                    return ids.Distinct().Any(id => doc.GetElement(id) is ParameterFilterElement pfe ? FilterUsesWorkset(pfe.GetElementFilter()) : false);
                }
                catch { return true; }
            }
            var visBefore = new Dictionary<long, HashSet<long>>();
            HashSet<long> visMoved = null; List<ElementId> visIds = null;
            var visSkipped = new List<string>(); var visOverCap = new List<string>();
            bool visSnapped = false, visDone = false;
            var heldFrom = new HashSet<long>();
            HashSet<long> Visible(Document dd, long viewId)
            {
                try
                {
                    // Only the moved ids are tested (with or without a category).
                    return new HashSet<long>(new FilteredElementCollector(dd, new ElementId(viewId)).WherePasses(new ElementIdSetFilter(visIds))
                        .ToElementIds().Select(x => x.Value));
                }
                catch { return null; }
            }
            void VisibilitySnap(Document dd)
            {
                visSnapped = true;
                if (checkViews.Count == 0) return;
                visMoved = new HashSet<long>(steps.Where(x => x.Kind == "move" && !notPreviewed.Contains(x.Id)).SelectMany(x => x.Ids).Where(id => !notYours.Contains(id)));
                visIds = visMoved.Select(id => new ElementId(id)).ToList();
                if (visMoved.Count == 0) return;
                // The cheap pre-check: a view where every (source, target) workset pair shows alike and no filter tests the workset
                // cannot change for these moves: no collector runs there.
                WorksetDefaultVisibilitySettings defs = null;
                try { defs = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(dd); } catch { defs = null; }
                var pairs = steps.Where(x => x.Kind == "move" && !notPreviewed.Contains(x.Id)).SelectMany(x => x.Ids.Where(visMoved.Contains).Select(id => (From: factById[id].WorksetId, To: x.ToId))).Distinct().ToList();
                var full = 0;
                foreach (var v in checkViews)
                {
                    var view = dd.GetElement(new ElementId(v.Id)) as View;
                    if (view != null && !ViewFiltersUseWorkset(view) && pairs.All(pr => pr.To != 0 && SafeVisible(view, pr.From, defs) == SafeVisible(view, pr.To, defs))) { visSkipped.Add(v.Name); continue; }
                    if (full >= visMaxViews) { visOverCap.Add(v.Name); continue; }
                    full++;
                    visBefore[v.Id] = Visible(dd, v.Id);
                }
            }
            void VisibilityCheck(Document dd, JsonObject r)
            {
                visDone = true;
                if (visBefore.Count == 0) return;
                dd.Regenerate();
                WorksetDefaultVisibilitySettings defs = null;
                try { defs = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(dd); } catch { defs = null; }
                var changes = new JsonArray(); var unchecked_ = new List<string>();
                foreach (var v in checkViews.Where(x => visBefore.ContainsKey(x.Id)))
                {
                    var b = visBefore.TryGetValue(v.Id, out var bb) ? bb : null;
                    var a = b == null ? null : Visible(dd, v.Id);
                    if (b == null || a == null) { unchecked_.Add(v.Name); continue; }
                    var (hidden, shown) = P.VisibilityDiff(visMoved, b, a);
                    if (hidden.Count == 0 && shown.Count == 0) continue;
                    var affected = hidden.Concat(shown).ToList();
                    var held = v.Sheet == null ? new List<long>() : affected.Where(id => !P.VisibilityAllowed(allowVisAll, allowVisNames, v.Name, factById[id].Workset, claimedTo.TryGetValue(id, out var tn) ? tn : null)).ToList();
                    foreach (var id in held) visHeld.Add(id);
                    // The cheap reason: the target workset hidden in the view (its own setting or the default), else filters or other settings.
                    var view = dd.GetElement(new ElementId(v.Id)) as View;
                    var wsHidden = view != null && affected.Any(id => { var e = dd.GetElement(new ElementId(id)); return e != null && !SafeVisible(view, e.WorksetId.IntegerValue, defs); });
                    changes.Add(new JsonObject
                    {
                        ["view"] = v.Name, ["sheet"] = v.Sheet, ["exportView"] = v.Sheet == null, ["hidden"] = hidden.Count, ["shown"] = shown.Count,
                        ["held"] = held.Count, ["accepted"] = v.Sheet == null ? 0 : affected.Count - held.Count,
                        ["why"] = wsHidden ? "the target workset is hidden in this view" : "view filters, phases or other view settings",
                        ["categories"] = Counts(affected.GroupBy(id => factById[id].Category).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
                        ["sampleIds"] = new JsonArray(affected.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()),
                    });
                }
                // Held elements go back where they were (and their sub-elements, and a link type all of whose moved instances are held).
                var typesAllHeld = new HashSet<long>(visMoved.Where(x => factById[x].TypeId != 0).GroupBy(x => factById[x].TypeId).Where(g => g.All(visHeld.Contains)).Select(g => g.Key));
                foreach (var id in visHeld.Where(i => !pinFailed.Contains(i)).ToList())
                {
                    var f = factById[id];
                    SetWorkset(dd, id, f.WorksetId);
                    heldFrom.Add(f.WorksetId);
                    if (followersOf.TryGetValue(id, out var subs))
                        foreach (var sub in subs)
                        {
                            var se = dd.GetElement(new ElementId(sub));
                            if (se != null && se.WorksetId.IntegerValue != info.Followers[sub].Ws) { SetWorkset(dd, sub, info.Followers[sub].Ws); heldFrom.Add(info.Followers[sub].Ws); }
                        }
                    if (f.TypeId != 0 && f.TypeWorksetId != 0 && typesAllHeld.Contains(f.TypeId) && visTypes.Add(f.TypeId)) { SetWorkset(dd, f.TypeId, f.TypeWorksetId); heldFrom.Add(f.TypeWorksetId); }
                }
                if (r["done"] is JsonObject dn0) dn0["moved"] = (int)dn0["moved"] - visHeld.Count(i => !pinFailed.Contains(i) && !notYours.Contains(i));
                r["visibilityChanges"] = changes;
                r["visibilityChecked"] = new JsonObject
                {
                    ["sheetViews"] = checkViews.Count(v => v.Sheet != null), ["exportViews"] = checkViews.Count(v => v.Sheet == null),
                    ["compared"] = visBefore.Count, ["skippedSameVisibility"] = visSkipped.Count, ["maxViews"] = visMaxViews,
                    ["notChecked"] = new JsonArray(unchecked_.Concat(visOverCap).Select(n => (JsonNode)n).ToArray()),
                };
                if (visOverCap.Count > 0 && r["notes"] is JsonArray nc)
                    nc.Add($"visibility check incomplete: {visOverCap.Count} view{(visOverCap.Count == 1 ? "" : "s")} not checked (over visibility_max_views {visMaxViews}); nothing is held for them.");
                if (visHeld.Count > 0)
                {
                    var heldMoves = moveSteps.Where(x => x.Ids.Any(visHeld.Contains)).Select(x => x.Id).ToList();
                    r["visibilityHeld"] = new JsonObject
                    {
                        ["elements"] = visHeld.Count, ["moves"] = new JsonArray(heldMoves.Select(x => (JsonNode)x).ToArray()),
                        ["sampleIds"] = new JsonArray(visHeld.Take(Math.Max(1, maxIds)).Select(i => (JsonNode)i).ToArray()),
                    };
                    var sv = changes.OfType<JsonObject>().Where(c => (int)c["held"] > 0).Select(c => (string)c["view"]).ToList();
                    if (r["notes"] is JsonArray nn)
                        nn.Add($"{visHeld.Count} moved element{(visHeld.Count == 1 ? "" : "s")} held: visibility changes on sheet view{(sv.Count == 1 ? "" : "s")} {string.Join(", ", sv.Take(5))}{(sv.Count > 5 ? ", ..." : "")}. They stay where they are; allow_visibility_change (true, or the view or workset names) accepts the change.");
                }
            }
            var result = NativeChange.Run(app, args, "workset_admin", "ACE: apply the workset plan", (d, plan) =>
            {
                var r = Common(new JsonObject());
                var done = Zero();
                r["done"] = done; r["verify"] = null;
                if (plan) return r;
                ran.Clear(); created.Clear(); repinned.Clear(); pinBefore.Clear(); pinFailed.Clear(); pinnedDirect = 0;
                visBefore.Clear(); visHeld.Clear(); visTypes.Clear(); heldFrom.Clear(); visSkipped.Clear(); visOverCap.Clear(); visSnapped = false; visDone = false;
                void Count(string k, int n) => done[k] = (int)done[k] + n;
                foreach (var st in steps)
                {
                    if (notPreviewed.Contains(st.Id)) continue;
                    if (st.Kind == "move" && !visSnapped) VisibilitySnap(d);
                    if (st.Kind != "move" && st.Kind != "create" && st.Kind != "rename")
                    {
                        if (visSnapped && !visDone) VisibilityCheck(d, r);
                        if (heldFrom.Contains(st.WorksetId))
                        {
                            // Elements held back for visibility stay on this workset: it is kept (in both runs).
                            if (r["kept"] is JsonArray kj) kj.Add(new JsonObject { ["name"] = st.Workset, ["why"] = "holds elements held back for visibility on sheets", ["elements"] = visHeld.Count(i => factById[i].WorksetId == st.WorksetId) });
                            continue;
                        }
                    }
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
                                if (!SetWorkset(d, id, toId) && !pinFailed.Contains(id))
                                    throw new CommandException($"Nothing was changed: element {id} could not be moved to {st.Workset}. Run the dry run again.");
                            }
                            foreach (var id in st.TypeIds)
                            {
                                if (notYours.Contains(id)) continue;
                                if (!SetWorkset(d, id, toId) && !pinFailed.Contains(id))
                                    throw new CommandException($"Nothing was changed: the link type {id} could not be moved to {st.Workset}. Run the dry run again.");
                            }
                            // Sub-elements: Revit usually moves them with their host; set the ones it left (when their workset can be set).
                            foreach (var id in st.Followers)
                            {
                                if (notYours.Contains(id)) continue;
                                var e = d.GetElement(new ElementId(id));
                                if (e == null || e.WorksetId.IntegerValue == toId) continue;
                                SetWorkset(d, id, toId);   // direct, or unpin-set-repin when pinned; what stays behind the self-check reports
                            }
                            Count("moved", st.Ids.Count(id => !notYours.Contains(id) && !pinFailed.Contains(id)));   // held ones are taken off after the check
                            break;
                        }
                        default:
                        {
                            // Non-model items (sketches, reference planes, view-only items) go with the workset to the receiving one.
                            var nowCount = new FilteredElementCollector(d).WherePasses(new ElementWorksetFilter(new WorksetId((int)st.WorksetId), false)).ToElementIds().Count(id => !info.NonModel.ContainsKey(id.Value));
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
                if (visSnapped && !visDone) VisibilityCheck(d, r);
                // Which path Revit took for pinned elements: set directly, or unpin, set and pin again.
                // A real apply lists what it moved, per element (the server's apply record); dropped again if it rolls back.
                if (!dryRun)
                {
                    var moved = ran.Where(x => x.Kind == "move").SelectMany(x => x.Ids.Where(id => !Left(id)).Select(id => (id, x.Workset))).ToList();
                    r["movedElements"] = new JsonArray(moved.Take(20000).Select(m => (JsonNode)new JsonObject { ["id"] = m.id, ["from"] = factById[m.id].Workset, ["to"] = m.Workset }).ToArray());
                    r["movedTotal"] = moved.Count;
                }
                if (pinFailed.Count > 0 && r["skipped"] is JsonArray sk)
                    sk.Add(SkipJson("pinned", pinFailed, pinFailed.Count, null, "pinned, and its workset could not be set even unpinned: left where it is, pinned again"));
                if (r["pinned"] is JsonObject pj) { pj["directOk"] = pinnedDirect; pj["unpinRepin"] = repinned.Count; }
                r["pinnedTouched"] = pinnedDirect + repinned.Count; r["pinnedDirectOk"] = pinnedDirect; r["pinnedUnpinRepin"] = repinned.Count;
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
                            var checkIds = st.Ids.Concat(st.TypeIds).Where(id => !notYours.Contains(id) && !Left(id)).ToList();   // the dry run moved only these
                            var off = checkIds.Where(id => { var e = d.GetElement(new ElementId(id)); return e == null || e.WorksetId.IntegerValue != toId; }).ToList();
                            var all = checkIds.Count;
                            if (off.Count > 0) mismatches.Add(($"elements moved to {st.Workset}", all.ToString(), $"{all - off.Count} (not there: {string.Join(", ", off.Take(5))})"));
                            // Sub-elements end with their host (unless the host was not moved: a dry run leaves what is not yours).
                            var subsChecked = st.Followers.Where(id => !notYours.Contains(id) && !notYours.Contains(info.Followers[id].Host) && !Left(info.Followers[id].Host) && d.GetElement(new ElementId(id)) != null).ToList();
                            var subsOff = subsChecked.Where(id => d.GetElement(new ElementId(id)).WorksetId.IntegerValue != toId).ToList();
                            if (subsOff.Count > 0) mismatches.Add(($"sub-elements that follow their host to {st.Workset}", subsChecked.Count.ToString(), $"{subsChecked.Count - subsOff.Count} (not there: {string.Join(", ", subsOff.Take(5))})"));
                            break;
                        }
                        default:
                            if (names.ContainsKey(st.WorksetId)) mismatches.Add(("workset " + st.Workset, "deleted", "still there"));
                            break;
                    }
                }
                // Pins: every element the run set keeps its pin state; the ones it unpinned are pinned again, on their target.
                var pinLost = pinBefore.Where(kv => { var e = d.GetElement(new ElementId(kv.Key)); return e == null || e.Pinned != kv.Value; }).Select(kv => kv.Key).ToList();
                if (pinLost.Count > 0) mismatches.Add(("pin state of the moved elements", "unchanged", $"changed on {pinLost.Count} (e.g. {string.Join(", ", pinLost.Take(5))})"));
                var notRepinned = repinned.Where(x => { var e = d.GetElement(new ElementId(x.Id)); return e == null || !e.Pinned || e.WorksetId.IntegerValue != x.To; }).Select(x => x.Id).ToList();
                if (notRepinned.Count > 0) mismatches.Add(("pinned elements pinned again on their target", repinned.Count.ToString(), $"{repinned.Count - notRepinned.Count} (not: {string.Join(", ", notRepinned.Take(5))})"));
                // reset_to: every instance ends where the plan puts it (moved, or staying), nothing else moved.
                if (resetJson != null)
                {
                    var plannedWs = facts.ToDictionary(f => f.Id, f => f.WorksetId);
                    foreach (var st in ran.Where(x => x.Kind == "move"))
                    {
                        var toId = st.ToId != 0 ? st.ToId : created.TryGetValue(st.Workset, out var c3) ? c3 : -1;
                        foreach (var id in st.Ids.Where(i => !notYours.Contains(i) && !Left(i))) plannedWs[id] = toId;
                    }
                    foreach (var st in ran.Where(x => x.Kind == "move-and-delete"))
                        foreach (var k in plannedWs.Where(kv => kv.Value == st.WorksetId).Select(kv => kv.Key).ToList()) plannedWs[k] = st.ToId;
                    var wrong = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                    foreach (var kv in plannedWs)
                    {
                        var e = d.GetElement(new ElementId(kv.Key));
                        if (e == null || e.WorksetId.IntegerValue == kv.Value) continue;
                        var where = names.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : e.WorksetId.IntegerValue.ToString();
                        (wrong.TryGetValue(where, out var lst) ? lst : wrong[where] = new List<long>()).Add(kv.Key);
                    }
                    foreach (var w in wrong) mismatches.Add(($"instances on {w.Key} the plan did not put there", "0", $"{w.Value.Count} (e.g. {string.Join(", ", w.Value.Take(5))})"));
                }
                // What each workset holds now (all elements, as audit counts them).
                if (r["worksetCounts"] is JsonArray wc)
                    foreach (var o in wc.OfType<JsonObject>())
                    {
                        long wid = o["id"] is JsonValue iv && iv.TryGetValue<long>(out var x0) ? x0 : created.TryGetValue(o["workset"]?.ToString() ?? "", out var x1) ? x1 : 0;
                        o["measured"] = wid != 0 && names.ContainsKey(wid) ? OnWorkset(d, wid) : (int?)null;
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
                if (result["movedElements"] != null) { result.Remove("movedElements"); result["movedTotal"] = 0; }
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
                    if (s.Followers.Count > 0) o["followsHost"] = s.Followers.Count;
                    if (s.Row != null) o["row"] = s.Row;   // echoed from the request: information only
                    break;
                case "delete": case "move-and-delete":
                    o["elements"] = s.Elements; o["byCategory"] = Counts(s.ByCategory);
                    if (s.Kind == "move-and-delete") o["moveTo"] = s.To; else o["safetyTarget"] = s.Safety;
                    if (s.NonModel > 0) o["nonModel"] = s.NonModel;   // sketches and other non-model items: go to moveTo / safetyTarget
                    break;
            }
            return o;
        }
    }
}
