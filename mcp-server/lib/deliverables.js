// export_deliverables: NWC, IFC and DWFx from the open model in one call. The server picks the file names (the vault's
// naming rule rules/export.json nameTemplate, else the model name), resolves the IFC settings (a JSON file, or a named
// setup from the vault) and reports the add-in's pre-flight and per-file results as a table. Pure functions.
import fs from "node:fs";
import path from "node:path";
import { ruleAppliesTo } from "./worksets.js";

export const EXPORT_RULES = path.join("rules", "export.json");
const BAD = /[\\/:*?"<>|]/g;
const EXT = { nwc: ".nwc", ifc: ".ifc", dwfx: ".dwfx" };

/** The vault's export rules ({} when there are none); a broken file is an error, not silently ignored. */
export function readRules(vaultDir) {
  const f = vaultDir && path.join(vaultDir, EXPORT_RULES);
  if (!f || !fs.existsSync(f)) return { rules: {}, file: null };
  try { return { rules: JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")) ?? {}, file: f }; }
  catch (e) { throw new Error(`${f} is not valid JSON (${e.message}).`); }
}

/**
 * The base file name: the template's {model}, {date} (YYYYMMDD) and {format} filled, characters Windows refuses replaced.
 * -> { base, problem }
 */
export function baseName({ model, template, date = new Date(), format = "" }) {
  const t = String(template || "{model}");
  const unknown = [...t.matchAll(/\{([^}]*)\}/g)].map((m) => m[1]).filter((k) => !["model", "date", "format"].includes(k));
  if (unknown.length) return { base: null, problem: `the naming rule uses {${unknown.join("}, {")}}, which ACE cannot fill (it knows {model}, {date}, {format})` };
  const ymd = `${date.getFullYear()}${String(date.getMonth() + 1).padStart(2, "0")}${String(date.getDate()).padStart(2, "0")}`;
  const base = t.replace(/\{model\}/g, String(model ?? "model")).replace(/\{date\}/g, ymd).replace(/\{format\}/g, format).replace(BAD, "-").trim();
  return base ? { base, problem: null } : { base: null, problem: "the naming rule gives an empty name" };
}

/** A plain file name for one format: given (ext added) or base + ext. */
export const fileFor = (format, given, base) => {
  const name = String(given ?? "").trim() || base;
  return name.toLowerCase().endsWith(EXT[format]) ? name : `${name}${EXT[format]}`;
};

// Revit IFC exporter configuration keys (saved setups) -> the add-in's IFCExportOptions keys.
const EXPORTER_KEYS = { IFCVersion: "FileVersion", SplitWallsAndColumns: "WallAndColumnSplitting", SpaceBoundaries: "SpaceBoundaryLevel" };
const DROP = new Set(["Name", "IsBuiltIn", "IsInSession", "ActivePhaseId", "ActiveViewId"]);

/** An IFC settings object (ACE's keys or an exporter configuration) -> { config, renamed: [from -> to] }. */
export function ifcConfig(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) throw new Error("the IFC settings must be a JSON object");
  const config = {}, renamed = [];
  for (const [k, v] of Object.entries(raw)) {
    if (DROP.has(k) || v == null || k.startsWith("_")) continue;
    const to = EXPORTER_KEYS[k];
    if (to && !(to in raw)) { config[to] = v; renamed.push(`${k} -> ${to}`); }
    else if (!to) config[k] = v;
  }
  return { config, renamed };
}

/**
 * The IFC settings: config (a .json file path), or setup (a name) looked up in the vault's export rules (ifc.setups,
 * a path relative to the rules file or an inline object) and then rules/ifc/<setup>.json.
 * -> { config?, setup?, source?, renamed, problem? }
 */
export function resolveIfc({ config, setup }, { vaultDir, rules = {}, rulesFile = null } = {}) {
  const fromFile = (f) => {
    if (!/\.json$/i.test(f)) return { problem: `${f}: the IFC settings file must be JSON (the exporter's saved setup, or ACE's keys FileVersion, ExportBaseQuantities, WallAndColumnSplitting, SpaceBoundaryLevel)` };
    if (!fs.existsSync(f)) return { problem: `${f} does not exist` };
    let raw;
    try { raw = JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")); } catch (e) { return { problem: `${f} is not valid JSON (${e.message})` }; }
    try { return { ...ifcConfig(raw), source: f }; } catch (e) { return { problem: `${f}: ${e.message}` }; }
  };
  if (config) return { setup: setup ?? path.basename(config, path.extname(config)), ...fromFile(config) };
  if (!setup) return { renamed: [] };
  const named = rules?.ifc?.setups?.[setup];
  if (named && typeof named === "object") { try { return { setup, ...ifcConfig(named), source: `${rulesFile ?? EXPORT_RULES} ifc.setups.${setup}` }; } catch (e) { return { setup, problem: e.message }; } }
  if (typeof named === "string") return { setup, ...fromFile(path.resolve(path.dirname(rulesFile ?? path.join(vaultDir ?? ".", EXPORT_RULES)), named)) };
  const guess = vaultDir && path.join(vaultDir, "rules", "ifc", `${String(setup).replace(BAD, "-")}.json`);
  if (guess && fs.existsSync(guess)) return { setup, ...fromFile(guess) };
  // Not in the vault: the name goes to the add-in, which looks for the setup saved in the model (it blocks if none).
  return { setup, renamed: [], modelOnly: true, source: `the IFC setup "${setup}" saved in the model (not in the vault: ${EXPORT_RULES} ifc.setups or rules/ifc/${setup}.json)` };
}

const size = (n) => n == null ? "-" : n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : n >= 1024 ? `${Math.round(n / 1024)} KB` : `${n} B`;
const HOW = {
  "no-exporter": "install the Navisworks NWC Export Utility for this Revit version, restart Revit, run again",
  "view-not-found": "check the view name (list_views)",
  "setup-not-found": "check the name (export_deliverables list_ifc_setups: true): the setup saved in the model is used first, then the vault's (rules/export.json ifc.setups, rules/ifc/<setup>.json); or give ifc.config (a JSON file)",
  "no-sheets": "check the sheet numbers or the sheet set's name",
  exists: "pass overwrite: true after the user agreed, or another file name",
  "out-dir": "give out_dir: a folder that exists or can be created",
};
export const blocksOf = (r) => (r?.blocked ?? []).map((b) => ({ ...b, how: b.how ?? HOW[b.code] }));

/** The reply: pre-flight, blocks, the files table (file | size | time | count | warnings), notes. */
export function resultText(r, { mode, outDir, names = {}, notes = [] }) {
  const pf = r?.preflight ?? {};
  const lines = [`EXPORT DELIVERABLES from ${r?.model ?? "the model"} (${mode === "check" ? "pre-flight only, nothing written" : r?.success ? "exported" : "NOT all exported"}) to ${pf.outDir ?? outDir}.`];
  const want = Object.entries(names).map(([f, n]) => `${f.toUpperCase()} ${n}`);
  if (want.length) lines.push(`Files: ${want.join(", ")}.`);
  const pre = [];
  if ("nwc" in names) pre.push(`Navisworks exporter ${pf.navisworksExporter ? "installed" : "NOT installed"}`);
  // The add-in answers a name (older) or { name, from: model | vault | config | default }.
  if (pf.ifcSetup) pre.push(typeof pf.ifcSetup === "object" ? (pf.ifcSetup.name ? `IFC setup ${pf.ifcSetup.name}${pf.ifcSetup.from ? ` (from the ${pf.ifcSetup.from})` : ""}` : "IFC: Revit's default settings") : `IFC setup ${pf.ifcSetup}`);
  if ("dwfx" in names) pre.push(`${pf.sheets ?? 0} sheet(s)`);
  if (pf.existing?.length) pre.push(`existing: ${pf.existing.map((f) => path.basename(f)).join(", ")}`);
  if (pre.length) lines.push(`Pre-flight: ${pre.join("; ")}.`);
  const blocks = blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing exported):\n${blocks.map((b) => `- ${b.what}: ${b.why}${b.how ? `. How to clear: ${b.how}` : ""}`).join("\n")}`);
  const files = r?.files ?? [];
  if (files.length) {
    lines.push("| file | size | time | count | warnings |", "|---|---|---|---|---|");
    for (const f of files) lines.push(`| ${path.basename(f.file ?? "")} | ${size(f.bytes)} | ${f.seconds != null ? `${f.seconds} s` : "-"} | ${f.sheets != null ? `${f.sheets} sheet(s)` : f.elements != null ? `${f.elements} element(s)` : "-"} | ${(f.warnings ?? []).join("; ").replace(/\|/g, "/") || "-"} |`);
  }
  // #36: what the NWC view holds, so a reviewer sees which view the file came from and what is in it.
  const nwcFiles = files.filter((f) => f.format === "nwc" && f.view && f.bytes != null);
  if (nwcFiles.length) for (const f of nwcFiles) lines.push(nwcViewLine(f.view, f, true));
  else if (pf.nwcView?.view) lines.push(nwcViewLine(pf.nwcView.view, pf.nwcView, false));
  const all = [...notes, ...(r?.notes ?? []).map((n) => typeof n === "string" ? n : JSON.stringify(n))];
  if (all.length) lines.push(`Notes:\n${all.map((n) => `- ${n}`).join("\n")}`);
  return lines.join("\n");
}

/**
 * dwfx.same_as_folder: the .dwg / .pdf file stems of a folder matched to the model's sheets
 * -> { sheets, matched, unmatched, ambiguous: [{ stem, candidates, how }], extra }. Nothing is guessed:
 * 1. the vault's rules/export.json dwfx.stemPattern (a regex with (?<number>...)), when it matches;
 * 2. the sheet number as the whole stem, or its start followed by a separator (_, space, ., or - then a non-digit);
 *    then the same after the project prefix dwfx.stemPrefix (a regex at the start of the stem) when it is set;
 * 3. the sheet name as consecutive whole tokens of the stem (names whose latin key is under 4 characters are skipped).
 * More than one candidate at a step: ambiguous, reported, and counted as unmatched.
 */
export function matchStems(stems, sheets, { stemPattern, stemPrefix } = {}) {
  const key = (x) => String(x ?? "").toLowerCase().replace(/[^a-z0-9]+/g, "");
  const toks = (x) => String(x ?? "").toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
  const re = (src, what) => { try { return new RegExp(String(src).replace(/^\(\?i\)/, ""), "i"); } catch (e) { throw new Error(`dwfx.${what} ${src} is not a valid regular expression (${e.message})`); } };
  const rx = stemPattern ? re(stemPattern, "stemPattern") : null;
  const px = stemPrefix ? re(String(stemPrefix).startsWith("^") ? stemPrefix : `^(?:${stemPrefix})`, "stemPrefix") : null;
  const list = (sheets ?? []).filter((x) => x?.sheetNumber).map((x) => ({ number: String(x.sheetNumber), name: String(x.name ?? "") }));
  const startsWithNumber = (rest, n) => {
    const r = rest.toLowerCase(), m = n.toLowerCase();
    if (r === m) return true;
    if (!r.startsWith(m)) return false;
    const after = r.slice(m.length);
    return /^[_ .]/.test(after) || /^-\D/.test(after);
  };
  const matched = [], unmatched = [], ambiguous = [];
  for (const stem of stems) {
    let hit = null, how = "", cands = null;
    const num = rx ? stem.match(rx)?.groups?.number : null;
    if (num != null) { const c = list.filter((x) => key(x.number) === key(num)); if (c.length === 1) { hit = c[0]; how = "number (stemPattern)"; } else if (c.length > 1) cands = c; }
    if (!hit && !cands) {
      const rests = [stem.trim()];
      const pm = px && stem.match(px);
      if (pm && pm[0].length && pm.index === 0) rests.push(stem.slice(pm[0].length).trim());
      for (const [i, rest] of rests.entries()) {
        const c = list.filter((x) => startsWithNumber(rest, x.number));
        if (c.length === 1) { hit = c[0]; how = i ? "number (after the prefix)" : "number"; break; }
        if (c.length > 1) { cands = c; how = "number"; break; }
      }
    }
    if (!hit && !cands) {
      const st = toks(stem), seq = (nt) => st.some((_, i) => nt.every((t, j) => st[i + j] === t));
      const c = list.filter((x) => key(x.name).length >= 4 && seq(toks(x.name)));
      if (c.length === 1) { hit = c[0]; how = "name"; } else if (c.length > 1) { cands = c; how = "name"; }
    }
    if (hit) matched.push({ stem, number: hit.number, name: hit.name, how });
    else { unmatched.push(stem); if (cands) ambiguous.push({ stem, how, candidates: cands.map((x) => `${x.number} ${x.name}`.trim()) }); }
  }
  const used = new Set(matched.map((m) => m.number));
  return { sheets: list.filter((x) => used.has(x.number)).map((x) => x.number), matched, unmatched, ambiguous, extra: list.filter((x) => !used.has(x.number)).map((x) => `${x.number} ${x.name}`.trim()) };
}
/** The file stems of a folder's .dwg and .pdf files, its subfolders included (printed sets often sit in PDF\ and CAD\),
 * one per stem. Revit's <name>_backup folders and hidden folders are skipped; depth is capped at 4. */
export function folderStems(dir, { maxDepth = 4 } = {}) {
  if (!fs.existsSync(dir)) throw new Error(`${dir} does not exist`);
  const stems = new Set();
  const walk = (d, depth) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (e.isDirectory()) { if (depth < maxDepth && !/_backup$/i.test(e.name) && !e.name.startsWith(".")) walk(path.join(d, e.name), depth + 1); }
      else if (e.isFile() && /\.(dwg|pdf)$/i.test(e.name)) stems.add(e.name.replace(/\.(dwg|pdf)$/i, ""));
    }
  };
  walk(dir, 0);
  return [...stems].sort();
}
/** IFC setups the vault names (rules/export.json ifc.setups and rules/ifc/*.json). */
export function vaultIfcSetups(vaultDir, rules = {}) {
  const named = Object.keys(rules?.ifc?.setups ?? {});
  const d = vaultDir && path.join(vaultDir, "rules", "ifc");
  const files = d && fs.existsSync(d) ? fs.readdirSync(d).filter((f) => /\.json$/i.test(f)).map((f) => f.replace(/\.json$/i, "")) : [];
  return [...new Set([...named, ...files])];
}

// ---- #36: the agreed export view per model (the vault's rules/export.json nwc.agreedViews, dwfx.agreedViews) ----------
// A model's NWC is exported from one agreed 3D view; typing the view per call once exported the whole building instead of
// the agreed part. The view is project data: read from the vault at run time, recorded after the first successful export,
// and changed only with confirm_view_change: true. Keys: the model's record key (the add-in's modelKey: the central's file
// name, else the file's, else the title) or a pattern matched like a rule's models key (exact or *, case-insensitive).
const viewList = (v) => [].concat(v ?? []).map((x) => String(x ?? "").trim()).filter(Boolean);
const viewKey = (v) => viewList(v).map((x) => x.toLowerCase()).sort().join("\n");
const showViews = (v) => viewList(v).map((x) => `"${x}"`).join(", ") || "(none)";
const keyNorm = (s) => String(s ?? "").trim().replace(/\.rvt$/i, "").toLowerCase();

/**
 * The agreed view(s) of a model: rules[format].agreedViews { key: view (nwc) or [views] (dwfx) }. The model's own key
 * first, else the keys that match one of its names. -> { key, value } | { problem } | null (none agreed)
 */
export function agreedViewOf(rules, format, { key, names = [] } = {}) {
  const map = rules?.[format]?.agreedViews;
  if (!map || typeof map !== "object" || Array.isArray(map)) return null;
  const entries = Object.entries(map).filter(([, v]) => viewList(v).length);
  const own = entries.find(([k]) => key && keyNorm(k) === keyNorm(key));
  if (own) return { key: own[0], value: own[1] };
  const all = [...new Set([...names, keyNorm(key)].filter(Boolean))];
  const hits = entries.filter(([k]) => ruleAppliesTo({ models: [k] }, all).applies);
  if (!hits.length) return null;
  if (hits.some(([, v]) => viewKey(v) !== viewKey(hits[0][1])))
    return { problem: `${format}.agreedViews has ${hits.length} keys for this model with different views (${hits.map(([k, v]) => `${k}: ${showViews(v)}`).join("; ")})` };
  return { key: hits[0][0], value: hits[0][1] };
}

/**
 * Which view(s) this export uses, from the call (given) and the agreed one. NWC: no view given uses the agreed view; a
 * different one is refused (nwc-view-differs) unless confirm (then it is recorded). DWFx 3D views (dwfx.views): a different
 * non-empty list is refused (dwfx-views-differ) unless confirm; none given exports none (a note names the agreed ones).
 * The first view given for a model with none agreed is recorded after a successful export.
 * -> { views (nwc: a name; dwfx: a list or undefined), from: "agreed" | "call" | null, record, change?, block?, note? }
 */
export function viewDecision({ format, given, agreed, confirm = false, model, rulesFile = EXPORT_RULES }) {
  const where = `${rulesFile} ${format}.agreedViews`;
  if (agreed?.problem) return { block: { what: `${format} view`, why: `${agreed.problem} (${rulesFile})`, how: `keep one key per model in ${where}`, code: `${format}-view-ambiguous` } };
  const want = viewList(given), has = agreed ? viewList(agreed.value) : [];
  const label = format === "nwc" ? "NWC view" : "DWFx 3D view(s)";
  const out = (v) => format === "nwc" ? v[0] : v;
  if (!want.length) {
    if (format === "nwc") {
      if (has.length) return { views: has[0], from: "agreed", record: false, note: `NWC from the agreed view "${has[0]}" for ${agreed.key} (${where}).` };
      return { block: { what: "nwc view", why: `no view was given and none is agreed for ${model ?? "this model"} (${where})`, how: "give nwc.view: the 3D view agreed for the Navisworks export (ACE records it for this model after the first export)", code: "view-not-found" } };
    }
    return { views: undefined, from: null, record: false, note: has.length ? `DWFx: the agreed 3D view(s) ${showViews(has)} for ${agreed.key} are not in this export (dwfx.views not given).` : undefined };
  }
  if (!has.length) return { views: out(want), from: "call", record: true };
  if (viewKey(want) === viewKey(has)) return { views: out(has), from: "agreed", record: false };   // the agreed spelling
  const code = format === "nwc" ? "nwc-view-differs" : "dwfx-views-differ";
  if (!confirm) return { block: {
    what: `${format} view ${showViews(want)}`,
    why: `${code}: the agreed ${label} for ${agreed.key} is ${showViews(has)} (${where}); this call asks for ${showViews(want)}`,
    how: `export from the agreed view (${format === "nwc" ? "leave nwc.view out" : "dwfx.views as agreed"}), or, only when the user confirms the agreed view has changed, pass confirm_view_change: true (the new view is then recorded)`,
    code } };
  return { views: out(want), from: "call", record: true, change: { from: has, to: want }, note: `${label} changed with confirm_view_change: ${showViews(has)} -> ${showViews(want)} (recorded for ${agreed.key} after a successful export).` };
}

/** Records the agreed view(s) of a model in the vault's rules/export.json (created when missing; other keys kept). -> the file */
export function recordAgreedView(vaultDir, format, key, value) {
  const f = path.join(vaultDir, EXPORT_RULES);
  const { rules } = readRules(vaultDir);
  const next = { ...rules };
  next[format] = { ...(rules[format] ?? {}) };
  const map = { ...(next[format].agreedViews ?? {}) };
  for (const k of Object.keys(map)) if (keyNorm(k) === keyNorm(key)) delete map[k];
  map[key] = format === "nwc" ? viewList(value)[0] : viewList(value);
  next[format].agreedViews = map;
  fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(`${f}.tmp`, JSON.stringify(next, null, 2));
  fs.renameSync(`${f}.tmp`, f);
  return f;
}

/** One line per NWC: the view it was exported from, its model elements and top categories (#36 post-export check). */
export function nwcViewLine(view, { elements, categories } = {}, exported = true) {
  if (!view) return null;
  const cats = (categories ?? []).map((c) => `${c.name} ${c.count}`).join(", ");
  const n = elements == null ? "element count not read" : elements < 0 ? "elements could not be counted" : `${elements} model element(s)`;
  return `NWC ${exported ? "exported from" : "view"} "${view}": ${n}${cats ? `; categories: ${cats}` : ""}${elements === 0 ? " (EMPTY: check the view)" : ""}.`;
}
