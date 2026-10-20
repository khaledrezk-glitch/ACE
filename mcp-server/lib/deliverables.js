// export_deliverables: NWC, IFC and DWFx from the open model in one call. The server picks the file names (the vault's
// naming rule rules/export.json nameTemplate, else the model name), resolves the IFC settings (a JSON file, or a named
// setup from the vault) and reports the add-in's pre-flight and per-file results as a table. Pure functions.
import fs from "node:fs";
import path from "node:path";

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
/** The file stems of a folder's .dwg and .pdf files (one per stem). */
export function folderStems(dir) {
  if (!fs.existsSync(dir)) throw new Error(`${dir} does not exist`);
  return [...new Set(fs.readdirSync(dir).filter((f) => /\.(dwg|pdf)$/i.test(f)).map((f) => f.replace(/\.(dwg|pdf)$/i, "")))].sort();
}
/** IFC setups the vault names (rules/export.json ifc.setups and rules/ifc/*.json). */
export function vaultIfcSetups(vaultDir, rules = {}) {
  const named = Object.keys(rules?.ifc?.setups ?? {});
  const d = vaultDir && path.join(vaultDir, "rules", "ifc");
  const files = d && fs.existsSync(d) ? fs.readdirSync(d).filter((f) => /\.json$/i.test(f)).map((f) => f.replace(/\.json$/i, "")) : [];
  return [...new Set([...named, ...files])];
}
