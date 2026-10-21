using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using L = AceRevitMcp.Rules.ViewMembershipLogic;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Bridge "export_deliverables": { out_dir, overwrite, check_only, nwc?: { view, file }, ifc?: { view?, setup?, config?,
    /// file }, dwfx?: { sheets?, sheet_set?, views?, file, allow_paper_fallback } }. DWFx: each sheet on the paper format of its title block
    /// (ISO / ANSI / ARCH within 2 mm, its orientation), one combined file per size, each file checked page by page after the
    /// export (a failed one is kept as <name>-FAILED.dwfx; sheetCheck: asked vs exported, sheets and views). views: 3D views by
    /// exact name, put into the same merged export as the sheets (inserted first, like the old "3D View: ..." page); with
    /// several paper groups they go into the largest group's file (a note says so). A 3D view has no title block: it takes
    /// its group's PaperFormat / PortraitLayout (DWFExportOptions has no setting per view; the format comes from the sheets,
    /// so the sheets stay on true paper) and its page is left out of the size check. Writes files, never changes the model (the IFC and DWFx exports run inside a
    /// transaction that is rolled back, as Revit requires a modifiable document: no undo step). check_only answers the pre-flight only; an export refuses
    /// when anything is blocked (no Navisworks exporter, a view or sheet missing, a file that exists without overwrite).
    /// </summary>
    internal static class ExportDeliverables
    {
        private sealed class Job
        {
            public string Format, File, Path;
            public View View;
            public List<ViewSheet> Sheets;
            public IFCExportOptions Ifc;
            public string Paper;          // dwfx: the ExportPaperFormat name of all its sheets ("Default": allow_paper_fallback)
            public bool Portrait;
            public double WidthMm, HeightMm;   // dwfx: the group's title block size
            public List<(double W, double H)> SheetSizes;   // dwfx: each sheet's title block size, as Sheets
            public List<View> Views = new List<View>();     // dwfx: the 3D views (dwfx.views) exported into this file with its sheets
        }

        /// <summary>A sheet's paper: its title block's Sheet Width / Height, else the sheet's outline (mm); 0 when neither can be read.</summary>
        private static (double W, double H, string From) SheetSize(Document doc, ViewSheet sh)
        {
            try
            {
                var tb = new FilteredElementCollector(doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
                var w = tb?.get_Parameter(BuiltInParameter.SHEET_WIDTH)?.AsDouble() ?? 0; var h = tb?.get_Parameter(BuiltInParameter.SHEET_HEIGHT)?.AsDouble() ?? 0;
                if (w > 0 && h > 0) return (Lengths.Mm(w), Lengths.Mm(h), "title block");
            }
            catch { /* no title block: the outline */ }
            try { var o = sh.Outline; var w = Lengths.Mm(o.Max.U - o.Min.U); var h = Lengths.Mm(o.Max.V - o.Min.V); if (w > 0 && h > 0) return (w, h, "sheet outline"); }
            catch { }
            return (0, 0, null);
        }

        /// <summary>
        /// Runs an export that needs a modifiable document inside a transaction that is always rolled back: the model is unchanged
        /// and no undo step is left. The files the export writes stay.
        /// </summary>
        private static bool RolledBack(Document doc, string name, Func<bool> export)
        {
            using (var t = new Transaction(doc, name))
            {
                t.Start();
                try { return export(); }
                finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
            }
        }

        /// <summary>
        /// The IFC setups the IFC exporter saved in the model (extensible storage; schemas named like IFCExportConfigurationMap,
        /// found by name, not by GUID): name, schema and every stored field as text.
        /// </summary>
        private static List<(string Name, string Schema, Dictionary<string, string> Fields)> SavedIfcSetups(Document doc, JsonArray notes)
        {
            var list = new List<(string, string, Dictionary<string, string>)>();
            List<Autodesk.Revit.DB.ExtensibleStorage.Schema> schemas;
            try { schemas = Autodesk.Revit.DB.ExtensibleStorage.Schema.ListSchemas().Where(x => x.SchemaName != null && x.SchemaName.IndexOf("IFCExportConfiguration", StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
            catch (Exception ex) { notes?.Add("The IFC setups saved in the model could not be listed: " + ex.Message); return list; }
            if (schemas.Count == 0) return list;
            var storages = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ExtensibleStorage.DataStorage)).ToElements();
            foreach (var schema in schemas)
            {
                if (!schema.ReadAccessGranted()) { notes?.Add($"The IFC setups in schema {schema.SchemaName} cannot be read (access restricted)."); continue; }
                var fields = schema.ListFields();
                foreach (var ds in storages)
                {
                    Autodesk.Revit.DB.ExtensibleStorage.Entity entity;
                    try { entity = ds.GetEntity(schema); } catch { continue; }
                    if (entity == null || !entity.IsValid()) continue;
                    var strings = new List<(string, string)>(); var map = new List<KeyValuePair<string, string>>();
                    foreach (var f in fields)
                        try
                        {
                            if (f.ContainerType == Autodesk.Revit.DB.ExtensibleStorage.ContainerType.Simple && f.ValueType == typeof(string)) strings.Add((f.FieldName, entity.Get<string>(f)));
                            else if (f.ContainerType == Autodesk.Revit.DB.ExtensibleStorage.ContainerType.Map && f.KeyType == typeof(string) && f.ValueType == typeof(string))
                                map.AddRange(entity.Get<IDictionary<string, string>>(f));
                        }
                        catch { /* a field of another type: not an exporter setting */ }
                    var stored = L.IfcStored(strings, map);
                    var name = stored.TryGetValue("Name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : ds.Name;
                    if (!string.IsNullOrWhiteSpace(name)) list.Add((name, schema.SchemaName, stored));
                }
            }
            return list;
        }

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var aceVersion = Rules.ParameterFill.AceVersion();
            var savedNotes = new JsonArray();
            var saved = SavedIfcSetups(doc, savedNotes);
            JsonArray SetupList() => new JsonArray(saved.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["schema"] = x.Schema, ["fields"] = x.Fields.Count,
                ["ifcVersion"] = x.Fields.TryGetValue("IFCVersion", out var iv) ? iv : null,
            }).ToArray());
            if (Args.Bool(args, "list_ifc_setups"))
                return new JsonObject { ["success"] = true, ["checkOnly"] = true, ["model"] = doc.Title, ["aceVersion"] = aceVersion, ["ifcSetups"] = SetupList(), ["notes"] = savedNotes };
            var checkOnly = Args.Bool(args, "check_only");
            var overwrite = Args.Bool(args, "overwrite");
            var blocks = new JsonArray();
            var notes = savedNotes;
            void Block(string what, string code, string why, string how) => blocks.Add(new JsonObject { ["what"] = what, ["why"] = why, ["how"] = how, ["code"] = code });

            var outDir = Args.Str(args, "out_dir");
            string dir = null;
            if (string.IsNullOrWhiteSpace(outDir)) Block("out_dir", "out-dir", "no output folder was given", "Give out_dir: the folder the files go to.");
            else
                try
                {
                    dir = System.IO.Path.GetFullPath(outDir.Trim().Trim('"'));
                    if (!Directory.Exists(dir)) notes.Add($"{dir} does not exist yet: the export creates it.");
                }
                catch (Exception ex) { Block("out_dir", "out-dir", $"'{outDir}' cannot be used: {ex.Message}", "Give a folder that exists or can be created."); dir = null; }

            var jobs = new List<Job>();
            var existing = new JsonArray();
            var navisworks = false;
            try { navisworks = OptionalFunctionalityUtils.IsNavisworksExporterAvailable(); } catch { navisworks = false; }
            var views = new JsonObject();
            JsonNode ifcSetup = null;
            var sheetCount = 0;
            var askedSheets = new List<string>();   // dwfx.sheets as sheet numbers (a name given is read as its sheet's number)
            var askedViews = new List<string>();    // dwfx.views as given (exact 3D view names)
            var dwfxViews = new List<View>();

            View FindView(string name) =>
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.Ordinal))
                ?? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            string FileFor(string format, JsonObject o, string ext, string fallback = null)
            {
                var name = L.FileName(Args.Str(o, "file") ?? fallback, ext, out var problem);
                if (name == null) { Block(format + " file", "out-dir", problem, "Give file: a plain file name, e.g. <base>" + ext + "."); return null; }
                if (dir == null) return null;
                var path = System.IO.Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    existing.Add(path);
                    if (!overwrite) Block(format + " " + name, "exists", $"{path} exists", "Pass overwrite: true to replace it, or choose another name.");
                }
                return name;
            }

            if (args["nwc"] is JsonObject nwc)
            {
                var file = FileFor("nwc", nwc, ".nwc");
                var name = Args.Str(nwc, "view");
                var v = string.IsNullOrWhiteSpace(name) ? null : FindView(name.Trim());
                views["nwc"] = v != null && v is View3D;
                if (!navisworks) Block("nwc", "no-exporter", "the Navisworks exporter is not installed for this Revit", "Install the Navisworks NWC Export Utility for this Revit version (Autodesk), restart Revit, then run again.");
                if (v == null) Block("nwc view " + name, "view-not-found", string.IsNullOrWhiteSpace(name) ? "no view was given" : $"no view is called {name}", "Give view: the name of a 3D view.");
                else if (!(v is View3D)) Block("nwc view " + name, "view-not-found", $"{name} is not a 3D view", "Give the name of a 3D view.");
                if (file != null && v is View3D) jobs.Add(new Job { Format = "nwc", File = file, View = v });
            }
            if (args["ifc"] is JsonObject ifc)
            {
                var file = FileFor("ifc", ifc, ".ifc");
                var name = Args.Str(ifc, "view");
                View v = null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    v = FindView(name.Trim());
                    if (v == null) Block("ifc view " + name, "view-not-found", $"no view is called {name}", "Give the name of a view, or leave view out for the whole model.");
                }
                views["ifc"] = string.IsNullOrWhiteSpace(name) ? null : (JsonNode)(v != null);
                var setup = Args.Str(ifc, "setup");
                IFCExportOptions options = null;
                // An explicit config from the user wins; then a setup saved in the model; then the vault's config (rules/ifc/<name>.json
                // or rules/export.json, sent by the server with config_from: "vault").
                var given = ifc["config"] as JsonObject;
                var givenFromVault = string.Equals(Args.Str(ifc, "config_from"), "vault", StringComparison.OrdinalIgnoreCase);
                var inModel = string.IsNullOrWhiteSpace(setup) || (given != null && !givenFromVault) ? default
                    : saved.FirstOrDefault(x => string.Equals(x.Name, setup.Trim(), StringComparison.Ordinal)) is var exact && exact.Name != null ? exact
                    : saved.FirstOrDefault(x => string.Equals(x.Name, setup?.Trim(), StringComparison.OrdinalIgnoreCase));
                JsonObject config = given != null && !givenFromVault ? given : inModel.Name != null ? L.IfcSavedConfig(inModel.Fields) : given;
                string from = given != null && !givenFromVault ? "config" : inModel.Name != null ? "model" : config != null ? "vault" : null;
                if (config != null)
                {
                    var (version, bq, split, sb, extra, problems) = L.IfcConfig(config);
                    options = new IFCExportOptions();
                    if (version != null)
                    {
                        // An enum name, the exporter's display name ("IFC 2x3 Coordination View 2.0"), or the number it stores.
                        var enumNames = Enum.GetNames(typeof(IFCVersion));
                        var known = L.IfcVersionName(version, enumNames);
                        if (known != null) options.FileVersion = (IFCVersion)Enum.Parse(typeof(IFCVersion), known, true);
                        else if (int.TryParse(version.Trim(), out var vn) && Enum.IsDefined(typeof(IFCVersion), vn)) options.FileVersion = (IFCVersion)vn;
                        else problems.Add($"IFC version '{version}' is not known. Accepted: {string.Join(", ", L.IfcVersionNames.Where(x => enumNames.Contains(x.Enum)).Select(x => x.Display).Concat(enumNames).Distinct())}");
                    }
                    if (bq.HasValue) options.ExportBaseQuantities = bq.Value;
                    if (split.HasValue) options.WallAndColumnSplitting = split.Value;
                    if (sb.HasValue) options.SpaceBoundaryLevel = sb.Value;
                    foreach (var kv in extra) options.AddOption(kv.Key, kv.Value);
                    foreach (var p in problems) Block("ifc config", "setup-not-found", p, from == "model" ? $"Fix the setup {inModel.Name} in Revit's IFC exporter." : "Fix the IFC configuration in the vault.");
                    ifcSetup = new JsonObject { ["name"] = inModel.Name ?? setup ?? "config", ["from"] = from };
                }
                else if (!string.IsNullOrWhiteSpace(setup))
                    Block("ifc setup " + setup, "setup-not-found", $"no IFC setup called {setup} is saved in the model ({(saved.Count == 0 ? "none is" : string.Join(", ", saved.Select(x => x.Name)))}) and the vault sent no config",
                        "Save the setup in Revit's IFC exporter, or put rules/ifc/<name>.json in the vault.");
                else { options = new IFCExportOptions(); ifcSetup = new JsonObject { ["name"] = null, ["from"] = "default" }; notes.Add("IFC: no setup or config was given; Revit's default IFC settings are used."); }
                if (options != null && v != null) options.FilterViewId = v.Id;
                if (file != null && options != null) jobs.Add(new Job { Format = "ifc", File = file, View = v, Ifc = options });
            }
            if (args["dwfx"] is JsonObject dwfx)
            {
                var sheets = new List<ViewSheet>();
                var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate && !s.IsPlaceholder).ToList();
                var setName = Args.Str(dwfx, "sheet_set");
                if (!string.IsNullOrWhiteSpace(setName))
                {
                    var set = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().FirstOrDefault(s => string.Equals(s.Name, setName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (set == null) Block("dwfx sheet set " + setName, "no-sheets", $"no sheet set is called {setName}", "Give the name of a saved sheet set, or sheets: [numbers].");
                    else foreach (View sv in set.Views) if (sv is ViewSheet vs && !sheets.Contains(vs)) sheets.Add(vs);
                }
                var numbers = Args.Strings(dwfx, "sheets");
                var missing = new List<string>();
                foreach (var n in numbers)
                {
                    var s = allSheets.FirstOrDefault(x => string.Equals(x.SheetNumber, n.Trim(), StringComparison.OrdinalIgnoreCase)) ?? allSheets.FirstOrDefault(x => L.SheetMatches(n, x.SheetNumber, x.Name));
                    askedSheets.Add(s?.SheetNumber ?? n.Trim());
                    if (s == null) missing.Add(n); else if (!sheets.Contains(s)) sheets.Add(s);
                }
                if (missing.Count > 0) Block("dwfx sheets", "no-sheets", $"no sheet has the number or name {string.Join(", ", missing)}", "Check the sheet numbers.");
                if (sheets.Count == 0 && missing.Count == 0 && string.IsNullOrWhiteSpace(setName)) Block("dwfx sheets", "no-sheets", "no sheets were given", "Give sheet_set or sheets.");
                else if (sheets.Count == 0 && blocks.All(b => b?["what"]?.ToString()?.StartsWith("dwfx sheet") != true)) Block("dwfx sheets", "no-sheets", "the sheet set holds no sheets", "Choose a set with sheets.");
                sheetCount = sheets.Count;
                // dwfx.views: 3D views by exact name (missing, ambiguous or not exportable names block).
                var viewNames = Args.Strings(dwfx, "views").Where(x => x != null).Distinct(StringComparer.Ordinal).ToList();
                if (viewNames.Count > 0)
                {
                    var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && !(v is ViewSheet)).ToList();
                    bool Printable(View v) { try { return v.CanBePrinted; } catch { return false; } }
                    var candidates = allViews.Select(v => (v.Name, v is View3D, Printable(v))).ToList();
                    foreach (var vn in viewNames)
                    {
                        askedViews.Add(vn);
                        var (idx, problem) = L.DwfxViewPick(vn, candidates);
                        if (problem != null) Block("dwfx view " + vn, "view-not-found", problem, "Give the exact name of a 3D view (list_views lists them), or leave it out of dwfx.views.");
                        else if (!dwfxViews.Contains(allViews[idx])) dwfxViews.Add(allViews[idx]);
                    }
                }
                // True size: every sheet on the paper format of its title block (PaperFormat Default came out on US Letter, scaled).
                // One export takes one format and orientation: one combined file per size (one file when all sheets share it).
                var fallback = Args.Bool(dwfx, "allow_paper_fallback");
                var groups = new List<Job>();
                var noFit = new List<string>();
                foreach (var sh in sheets)
                {
                    var (w, h, _) = SheetSize(doc, sh);
                    var pf = L.PaperFormatFor(w, h);
                    if (pf == null)
                    {
                        noFit.Add($"{sh.SheetNumber} ({(w > 0 ? $"{Math.Round(w)} x {Math.Round(h)} mm" : "size unreadable")})");
                        if (!fallback) continue;
                    }
                    var paper = pf?.Enum ?? "Default"; var portrait = pf?.Portrait ?? h > w;
                    if (!Enum.TryParse<ExportPaperFormat>(paper, out _)) { Block("dwfx paper " + sh.SheetNumber, "paper-size", $"this Revit has no paper format {paper}", "Pass allow_paper_fallback: true, or fix the title block size."); continue; }
                    var g = groups.FirstOrDefault(x => x.Paper == paper && x.Portrait == portrait && (paper != "Default" || L.SameSize(x.WidthMm, x.HeightMm, w, h)));
                    if (g == null) groups.Add(g = new Job { Format = "dwfx", Paper = paper, Portrait = portrait, WidthMm = w, HeightMm = h, Sheets = new List<ViewSheet>(), SheetSizes = new List<(double, double)>() });
                    g.Sheets.Add(sh); g.SheetSizes.Add((w, h));
                }
                if (noFit.Count > 0 && !fallback)
                    Block("dwfx paper", "paper-size", $"{noFit.Count} sheet(s) have a size no DWF paper format fits (ISO A/B, ANSI, ARCH, within 2 mm): {string.Join(", ", noFit.Take(20))}",
                        "Fix the title block size, or pass allow_paper_fallback: true to export them on Revit's default paper (scaled, not true size).");
                else if (noFit.Count > 0) notes.Add($"DWFx: {noFit.Count} sheet(s) fit no paper format and use Revit's default paper (scaled, not true size; allow_paper_fallback): {string.Join(", ", noFit.Take(20))}.");
                // The views go into one file: the only group's, else the largest group's (the first of equals).
                var viewsGroup = dwfxViews.Count > 0 ? L.DwfxViewsGroup(groups.Select(g => g.Sheets.Count).ToList()) : -1;
                if (viewsGroup >= 0)
                {
                    groups[viewsGroup].Views.AddRange(dwfxViews);
                    if (groups.Count > 1) notes.Add($"DWFx: the {dwfxViews.Count} view(s) ({string.Join(", ", dwfxViews.Select(v => v.Name))}) go into the file of the largest paper group ({groups[viewsGroup].Paper.Replace('_', ' ')}{(groups[viewsGroup].Portrait ? " portrait" : "")}, {groups[viewsGroup].Sheets.Count} sheet(s)); the other files hold sheets only.");
                }
                if (groups.Count == 1)
                {
                    var file = FileFor("dwfx", dwfx, ".dwfx", L.FileStem(doc.Title));   // one combined DWFx named as the model by default
                    if (file != null) { groups[0].File = file; jobs.Add(groups[0]); }
                }
                else if (groups.Count > 1)
                {
                    // One file per group, each name unique in the run (a Default group carries its size).
                    var stem = L.FileName(Args.Str(dwfx, "file") ?? L.FileStem(doc.Title), ".dwfx", out _);
                    stem = stem == null ? null : System.IO.Path.GetFileNameWithoutExtension(stem);
                    var names = L.DwfxGroupFiles(stem, null, groups.Select(g => (g.Paper, g.Portrait, g.WidthMm, g.HeightMm)).ToList());
                    for (var gi = 0; gi < groups.Count; gi++)
                    {
                        var file = stem == null ? FileFor("dwfx", dwfx, ".dwfx") : FileFor("dwfx", new JsonObject { ["file"] = names[gi] }, ".dwfx");
                        if (file != null) { groups[gi].File = file; jobs.Add(groups[gi]); }
                    }
                    notes.Add($"DWFx: the sheets have {groups.Count} paper sizes and one export takes one: one combined file per size ({string.Join(", ", groups.Select(g => $"{g.Paper.Replace('_', ' ')}{(g.Portrait ? " portrait" : "")}: {g.Sheets.Count}"))}).");
                }
            }
            if (!(args["nwc"] is JsonObject) && !(args["ifc"] is JsonObject) && !(args["dwfx"] is JsonObject))
                throw new CommandException("Give at least one of nwc, ifc or dwfx.");

            var answer = new JsonObject
            {
                ["model"] = doc.Title, ["aceVersion"] = aceVersion,
                ["preflight"] = new JsonObject
                {
                    ["navisworksExporter"] = navisworks, ["views"] = views, ["dwfxViews"] = args["dwfx"] is JsonObject ? new JsonArray(dwfxViews.Select(v => (JsonNode)v.Name).ToArray()) : null, ["ifcSetup"] = ifcSetup, ["ifcSetupsInModel"] = SetupList(), ["sheets"] = sheetCount,
                    ["sheetList"] = jobs.Any(j => j.Format == "dwfx") ? SheetList(doc, jobs.Where(j => j.Format == "dwfx").SelectMany(j => j.Sheets).ToList()) : null, ["existing"] = existing, ["outDir"] = dir,
                },
                ["blocked"] = blocks, ["files"] = new JsonArray(), ["notes"] = notes,
            };
            if (checkOnly) { answer["success"] = true; answer["checkOnly"] = true; return answer; }
            if (blocks.Count > 0)
            {
                answer["success"] = false; answer["stage"] = "blocked";
                answer["error"] = $"Nothing was exported: {blocks.Count} item{(blocks.Count == 1 ? " is" : "s are")} blocked ({blocks[0]?["why"]}).";
                return answer;
            }

            // The folder is created only now that nothing is blocked; each file is exported into a temporary folder in it and
            // moved over the target only after a successful export, so a failed export never loses an existing file.
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                blocks.Add(new JsonObject { ["what"] = "out_dir", ["why"] = $"{dir} could not be created: {ex.Message}", ["how"] = "Give a folder that exists or can be created.", ["code"] = "out-dir" });
                answer["success"] = false; answer["stage"] = "blocked"; answer["error"] = $"Nothing was exported: {dir} could not be created.";
                return answer;
            }
            var files = (JsonArray)answer["files"];
            var ok = true;
            var exportedSheets = new List<string>();
            var exportedViews = new List<string>();
            foreach (var job in jobs)
            {
                job.Path = System.IO.Path.Combine(dir, job.File);
                var baseName = System.IO.Path.GetFileNameWithoutExtension(job.File);
                var entry = new JsonObject { ["format"] = job.Format, ["file"] = job.Path, ["view"] = job.View?.Name };
                var warnings = new JsonArray();
                var watch = Stopwatch.StartNew();
                var temp = System.IO.Path.Combine(dir, ".ace-export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                var tempFile = System.IO.Path.Combine(temp, job.File);
                var written = false;
                try
                {
                    Directory.CreateDirectory(temp);
                    using (var guard = new ModelGuard(app))
                    {
                        switch (job.Format)
                        {
                            case "nwc":
                                doc.Export(temp, baseName, new NavisworksExportOptions { ExportScope = NavisworksExportScope.View, ViewId = job.View.Id });
                                break;
                            case "ifc":
                                if (!RolledBack(doc, "ACE: IFC export", () => doc.Export(temp, baseName, job.Ifc))) warnings.Add("Revit reported the IFC export as not successful.");
                                break;
                            case "dwfx":
                                var set = new ViewSet();
                                foreach (var v in job.Views) set.Insert(v);   // the 3D views first, like the old files (Revit decides the final page order)
                                foreach (var s in job.Sheets) set.Insert(s);
                                // One combined file of sheets of one size, on that paper format and orientation (true size), no crop box or areas.
                                // The 3D views take the same PaperFormat / PortraitLayout (no per-view setting exists; the format is the sheets').
                                var dwfxOptions = new DWFXExportOptions
                                {
                                    MergedViews = true, PaperFormat = (ExportPaperFormat)Enum.Parse(typeof(ExportPaperFormat), job.Paper), PortraitLayout = job.Portrait,
                                    ExportObjectData = true, CropBoxVisible = false, ExportingAreas = false,
                                    ImageQuality = DWFImageQuality.High, ImageFormat = DWFImageFormat.Lossless,
                                };
                                // Like the IFC exporter, the DWFx export needs a modifiable document: inside a transaction that is
                                // always rolled back (no undo step, the model is unchanged).
                                if (!RolledBack(doc, "ACE: DWFx export", () => doc.Export(temp, baseName, set, dwfxOptions))) warnings.Add("Revit reported the DWFx export as not successful.");
                                break;
                        }
                        foreach (var w in guard.Warnings.Concat(guard.Errors).Concat(guard.Dialogs).Distinct().Take(20)) warnings.Add(w);
                    }
                    if (!File.Exists(tempFile)) warnings.Add($"Revit wrote no {job.File} (the existing file, if any, is kept).");
                    else if (job.Format == "dwfx" && DwfxCheck(job, tempFile, entry, warnings) is List<string> wrong && wrong.Count > 0)
                    {
                        // Not true size: the file is kept beside the target as <name>-FAILED.dwfx (never replacing an older one:
                        // "-FAILED 2", ...); the existing target, if any, is kept.
                        var stemFailed = System.IO.Path.GetFileNameWithoutExtension(job.File) + "-FAILED";
                        var failed = System.IO.Path.Combine(dir, stemFailed + ".dwfx");
                        for (var n = 2; File.Exists(failed) && n < 1000; n++) failed = System.IO.Path.Combine(dir, $"{stemFailed} {n}.dwfx");
                        var kept = false;
                        try { if (!File.Exists(failed)) { File.Move(tempFile, failed); kept = true; entry["failedFile"] = failed; } } catch { kept = false; }
                        warnings.Add($"Self-check failed: {string.Join("; ", wrong.Take(10))}. "
                            + (kept ? $"The file is kept as {failed} for inspection" : "The failed file could not be kept") + $"; {job.File} was not written (the existing file, if any, is kept).");
                    }
                    else { Compat.MoveOverwrite(tempFile, job.Path); written = true; }   // overwrite: true was checked above
                }
                catch (Exception ex) { warnings.Add("The export failed (the existing file, if any, is kept): " + ex.Message); }
                finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
                watch.Stop();
                if (!written) ok = false;
                entry["bytes"] = written ? (JsonNode)new FileInfo(job.Path).Length : null;
                entry["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
                if (job.Format == "dwfx")
                {
                    entry["sheets"] = job.Sheets.Count; entry["sheetList"] = SheetList(doc, job.Sheets);
                    entry["views"] = new JsonArray(job.Views.Select(v => (JsonNode)v.Name).ToArray());
                    entry["paper"] = job.Paper.Replace('_', ' ') + (job.Portrait ? " portrait" : " landscape");
                    var layout = job.Paper == "Default"
                        ? $"one combined DWFx of the {job.Sheets.Count} sheet(s) no paper format fits, on Revit's default paper (scaled, not true size; allow_paper_fallback)"
                        : $"one combined DWFx of the {job.Sheets.Count} sheet(s) of this size, on {job.Paper.Replace('_', ' ')} {(job.Portrait ? "portrait" : "landscape")} at 1:1, no crop box (each page checked against its title block, size and orientation, after the export)";
                    if (job.Views.Count > 0) layout += $"; with the 3D view(s) {string.Join(", ", job.Views.Select(v => v.Name))} on the same paper (inserted first; no title block, so not size-checked)";
                    entry["layout"] = layout;
                    if (written) { exportedSheets.AddRange(job.Sheets.Select(x => x.SheetNumber)); exportedViews.AddRange(job.Views.Select(v => v.Name)); }
                }
                else entry["elements"] = ElementCount(doc, job.View);
                entry["warnings"] = warnings;
                files.Add(entry);
            }
            if (args["dwfx"] is JsonObject)
            {
                // The sheets asked for (e.g. the printed folder's, sent by the server) against the sheets in the DWFx files written.
                var asked = askedSheets.ToList();
                if (asked.Count == 0) asked = jobs.Where(j => j.Format == "dwfx").SelectMany(j => j.Sheets).Select(x => x.SheetNumber).ToList();
                var (matched, missingSheets, extra) = L.SheetCheck(asked, exportedSheets);
                JsonArray Arr(IEnumerable<string> l) => new JsonArray(l.Select(x => (JsonNode)x).ToArray());
                var (vMatched, vMissing, vExtra) = L.SheetCheck(askedViews, exportedViews);
                answer["sheetCheck"] = new JsonObject
                {
                    ["requested"] = asked.Count, ["matched"] = Arr(matched), ["missing"] = Arr(missingSheets), ["extra"] = Arr(extra),
                    ["views"] = new JsonObject { ["requested"] = askedViews.Count, ["matched"] = Arr(vMatched), ["missing"] = Arr(vMissing), ["extra"] = Arr(vExtra) },
                    ["ok"] = missingSheets.Count == 0 && extra.Count == 0 && vMissing.Count == 0,
                };
                if (missingSheets.Count > 0 || vMissing.Count > 0) ok = false;
            }
            answer["success"] = ok;
            if (!ok) { answer["stage"] = "export"; answer["error"] = "At least one export did not produce its file, or a DWFx failed its size check (see files[].warnings, files[].pageCheck and sheetCheck)."; }
            return answer;
        }

        /// <summary>
        /// The DWFx self-check (ViewMembershipLogic.PageCheckWithViews): per sheet, in export order, its page against its title
        /// block (size and orientation; a Default-paper group is only reported, with a warning); the 3D view pages (by order and
        /// count) are left out of the comparison. Writes pageCheck { number, titleBlockMm, pageMm, ok }, viewPages { name,
        /// pageMm }, viewsAt and eplotPaper into entry, warnings into warnings; returns the problems (none: passed).
        /// </summary>
        private static List<string> DwfxCheck(Job job, string file, JsonObject entry, JsonArray warnings)
        {
            var sheets = job.Sheets.Select((sh, i) => (sh.SheetNumber, job.SheetSizes[i].W, job.SheetSizes[i].H)).ToList();
            var viewNames = job.Views.Select(v => v.Name).ToList();
            (List<(string Number, string PageMm, bool Ok)> Sheets, List<(string Name, string PageMm)> Views, List<string> Problems, List<string> Warnings, string ViewsAt) check;
            try
            {
                using (var fs = File.OpenRead(file))
                {
                    var read = L.DwfxPages(fs);
                    check = L.PageCheckWithViews(sheets, viewNames, read.Pages, read.Papers, job.Paper == "Default");
                    if (read.Papers.Count > 0) entry["eplotPaper"] = new JsonArray(read.Papers.Distinct().Select(p => (JsonNode)$"{Math.Round(p.WidthMm, 1)} x {Math.Round(p.HeightMm, 1)} mm").ToArray());
                }
            }
            catch (Exception ex)
            {
                check = (sheets.Select(x => (x.SheetNumber, (string)null, false)).ToList(), viewNames.Select(v => (v, (string)null)).ToList(),
                    new List<string> { "the file could not be read as a DWFx package: " + ex.Message }, new List<string>(), null);
            }
            foreach (var w in check.Warnings) warnings.Add(w);
            entry["pageCheck"] = new JsonArray(check.Sheets.Select((r, i) => (JsonNode)new JsonObject
            {
                ["number"] = r.Number, ["titleBlockMm"] = $"{Math.Round(sheets[i].W, 1)} x {Math.Round(sheets[i].H, 1)}", ["pageMm"] = r.PageMm, ["ok"] = r.Ok,
            }).ToArray());
            if (viewNames.Count > 0)
            {
                entry["viewPages"] = new JsonArray(check.Views.Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["pageMm"] = v.PageMm }).ToArray());
                entry["viewsAt"] = check.ViewsAt;
            }
            return check.Problems;
        }

        /// <summary>Each sheet's number, name, paper (its title block, else its outline), the DWF paper format and scale.</summary>
        private static JsonArray SheetList(Document doc, List<ViewSheet> sheets) => new JsonArray(sheets.Select(sh =>
        {
            var (w, h, from) = SheetSize(doc, sh);
            var pf = L.PaperFormatFor(w, h);
            string scale = null;
            try { scale = sh.get_Parameter(BuiltInParameter.SHEET_SCALE)?.AsString(); } catch { }
            return (JsonNode)new JsonObject
            {
                ["number"] = sh.SheetNumber, ["name"] = sh.Name, ["paper"] = w > 0 ? L.PaperName(w, h) : null,
                ["widthMm"] = Math.Round(w), ["heightMm"] = Math.Round(h), ["sizeFrom"] = from, ["paperFormat"] = pf == null ? null : pf.Value.Enum + (pf.Value.Portrait ? " portrait" : " landscape"),
                ["scale"] = string.IsNullOrWhiteSpace(scale) ? null : scale,
            };
        }).ToArray());

        /// <summary>The model elements in the view (or in the whole model when no view).</summary>
        private static int ElementCount(Document doc, View view)
        {
            try
            {
                var c = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
                return c.WhereElementIsNotElementType().Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !e.ViewSpecific).Count();
            }
            catch { return -1; }
        }
    }
}
