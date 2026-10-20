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
    /// file }, dwfx?: { sheets?, sheet_set?, file } }. Writes files, never changes the model (the IFC export runs inside a
    /// transaction that is rolled back, as Revit requires one). check_only answers the pre-flight only; an export refuses
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
                var file = FileFor("dwfx", dwfx, ".dwfx", L.FileStem(doc.Title));   // one combined DWFx named as the model by default
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
                    if (s == null) missing.Add(n); else if (!sheets.Contains(s)) sheets.Add(s);
                }
                if (missing.Count > 0) Block("dwfx sheets", "no-sheets", $"no sheet has the number or name {string.Join(", ", missing)}", "Check the sheet numbers.");
                if (sheets.Count == 0 && missing.Count == 0 && string.IsNullOrWhiteSpace(setName)) Block("dwfx sheets", "no-sheets", "no sheets were given", "Give sheet_set or sheets.");
                else if (sheets.Count == 0 && blocks.All(b => b?["what"]?.ToString()?.StartsWith("dwfx sheet") != true)) Block("dwfx sheets", "no-sheets", "the sheet set holds no sheets", "Choose a set with sheets.");
                sheetCount = sheets.Count;
                if (file != null && sheets.Count > 0) jobs.Add(new Job { Format = "dwfx", File = file, Sheets = sheets });
            }
            if (!(args["nwc"] is JsonObject) && !(args["ifc"] is JsonObject) && !(args["dwfx"] is JsonObject))
                throw new CommandException("Give at least one of nwc, ifc or dwfx.");

            var answer = new JsonObject
            {
                ["model"] = doc.Title, ["aceVersion"] = aceVersion,
                ["preflight"] = new JsonObject
                {
                    ["navisworksExporter"] = navisworks, ["views"] = views, ["ifcSetup"] = ifcSetup, ["ifcSetupsInModel"] = SetupList(), ["sheets"] = sheetCount,
                    ["sheetList"] = jobs.FirstOrDefault(j => j.Format == "dwfx") is Job dj ? SheetList(dj.Sheets) : null, ["existing"] = existing, ["outDir"] = dir,
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
                                using (var t = new Transaction(doc, "ACE: IFC export"))
                                {
                                    t.Start();
                                    try { if (!doc.Export(temp, baseName, job.Ifc)) warnings.Add("Revit reported the IFC export as not successful."); }
                                    finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
                                }
                                break;
                            case "dwfx":
                                var set = new ViewSet();
                                foreach (var s in job.Sheets) set.Insert(s);
                                // One combined file, each sheet on its own paper size (PaperFormat Default) at true scale, no crop box or areas.
                                var dwfxOptions = new DWFXExportOptions
                                {
                                    MergedViews = true, PaperFormat = ExportPaperFormat.Default, ExportObjectData = true, CropBoxVisible = false, ExportingAreas = false,
                                    ImageQuality = DWFImageQuality.High, ImageFormat = DWFImageFormat.Lossless,
                                };
                                if (!doc.Export(temp, baseName, set, dwfxOptions)) warnings.Add("Revit reported the DWFx export as not successful.");
                                break;
                        }
                        foreach (var w in guard.Warnings.Concat(guard.Errors).Concat(guard.Dialogs).Distinct().Take(20)) warnings.Add(w);
                    }
                    if (!File.Exists(tempFile)) warnings.Add($"Revit wrote no {job.File} (the existing file, if any, is kept).");
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
                    entry["sheets"] = job.Sheets.Count; entry["sheetList"] = SheetList(job.Sheets);
                    entry["layout"] = "one combined DWFx; each sheet on its own paper size (PaperFormat Default), 1:1, no crop box (the DWF API has no zoom or margin setting: sheets export at 100%)";
                }
                else entry["elements"] = ElementCount(doc, job.View);
                entry["warnings"] = warnings;
                files.Add(entry);
            }
            answer["success"] = ok;
            if (!ok) { answer["stage"] = "export"; answer["error"] = "At least one export did not produce its file (see files[].warnings)."; }
            return answer;
        }

        /// <summary>Each sheet's number, name, paper (from its outline) and scale.</summary>
        private static JsonArray SheetList(List<ViewSheet> sheets) => new JsonArray(sheets.Select(sh =>
        {
            double w = 0, h = 0;
            try { var o = sh.Outline; w = Lengths.Mm(o.Max.U - o.Min.U); h = Lengths.Mm(o.Max.V - o.Min.V); } catch { }
            string scale = null;
            try { scale = sh.get_Parameter(BuiltInParameter.SHEET_SCALE)?.AsString(); } catch { }
            return (JsonNode)new JsonObject
            {
                ["number"] = sh.SheetNumber, ["name"] = sh.Name, ["paper"] = w > 0 ? L.PaperName(w, h) : null,
                ["widthMm"] = Math.Round(w), ["heightMm"] = Math.Round(h), ["scale"] = string.IsNullOrWhiteSpace(scale) ? null : scale,
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
