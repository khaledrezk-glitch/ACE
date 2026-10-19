// Parameter requirement sources -> one merged list of rows for the add-in's parameter_fill command.
// Readers for Revit shared parameter files, Model Checker (MCSettings) XML and LOIN workbooks; name classification
// against the shared parameter files; the editable mapping file (client category -> Revit categories, sheet ->
// disciplines, aliases, model -> discipline); merge, scope, cross-model checks on the bridge answer, the client checker
// review (P8) and the CSV / markdown reports. Pure functions apart from the small file helpers at the end.
// Client data never belongs in this file: the keyword rules below are generic Revit knowledge.
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { attr, readXlsx } from "./docs.js";
import { groupOf, mapRows, rowsFromCsv } from "./paramlist.js";
import { toCsv } from "./report.js";
import { localDate } from "./core.js";

const lc = (v) => String(v ?? "").trim().toLowerCase();
export const uniq = (a) => [...new Set(a)];

// ---- shared parameter files ----------------------------------------------------------------------------------------

/** Text of a source file (shared parameters, CSV list, checker XML): UTF-16 LE/BE by byte order mark or by NUL bytes, else UTF-8 (BOM stripped). */
export function sharedFileText(buf) {
  if (buf[0] === 0xff && buf[1] === 0xfe) return buf.subarray(2).toString("utf16le");
  if (buf[0] === 0xfe && buf[1] === 0xff) { const b = Buffer.from(buf.subarray(2, 2 + ((buf.length - 2) & ~1))); b.swap16(); return b.toString("utf16le"); }
  const sample = buf.subarray(0, Math.min(buf.length, 400));
  let even = 0, odd = 0;
  for (let i = 0; i < sample.length; i++) if (sample[i] === 0) i % 2 ? odd++ : even++;
  if (odd > sample.length / 8) return buf.toString("utf16le");
  if (even > sample.length / 8) { const b = Buffer.from(buf.subarray(0, buf.length & ~1)); b.swap16(); return b.toString("utf16le"); }
  return buf.toString("utf8").replace(/^﻿/, "");
}

/** Revit shared parameter file -> { groups: { id: name }, params: [{ name, guid, dataType, group, description }] }. Columns by header name. */
export function readSharedParameterFile(buf) {
  const text = Buffer.isBuffer(buf) ? sharedFileText(buf) : String(buf);
  const heads = { GROUP: ["GROUP", "ID", "NAME"], PARAM: ["PARAM", "GUID", "NAME", "DATATYPE", "DATACATEGORY", "GROUP", "VISIBLE", "DESCRIPTION", "USERMODIFIABLE"] };
  const groups = {}, params = [];
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim() || line.startsWith("#")) continue;
    const f = line.split("\t");
    const kind = f[0].replace(/^\*/, "").trim().toUpperCase();
    if (f[0].startsWith("*")) { heads[kind] = f.map((h) => h.replace(/^\*/, "").trim().toUpperCase()); continue; }
    const h = heads[kind];
    if (!h) continue;
    const get = (col) => { const i = h.indexOf(col); return i < 0 ? "" : (f[i] ?? "").trim(); };
    if (kind === "GROUP") groups[get("ID")] = get("NAME");
    else if (kind === "PARAM") params.push({ name: get("NAME"), guid: get("GUID").toLowerCase(), dataType: get("DATATYPE"), groupId: get("GROUP"), description: get("DESCRIPTION") || undefined });
  }
  for (const p of params) { p.group = groups[p.groupId] ?? p.groupId; delete p.groupId; }
  return { groups, params: params.filter((p) => p.name) };
}

// ---- Model Checker XML ---------------------------------------------------------------------------------------------

const PARKED = (name) => /^<--\s*unused\s*-->$|^unplaced$/i.test(String(name ?? "").trim());

/** MCSettings XML -> { name, checks: [{ id, name, path, active, parked, enabled, filters }] }. Heading/Section nest at any depth. */
export function readCheckerXml(textIn) {
  const s = String(textIn ?? "").replace(/^﻿/, "").replace(/<!--[\s\S]*?-->/g, "").replace(/<!\[CDATA\[[\s\S]*?\]\]>/g, "").replace(/<![^>]*>/g, "").replace(/<\?[\s\S]*?\?>/g, "");
  const stack = [], checks = [];
  let name = null, check = null;
  for (const m of s.matchAll(/<(\/?)([A-Za-z_][\w:.-]*)((?:\s+[^\s=/>]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>/g)) {
    const [tag, close, el, , selfClose] = m;
    if (close) {
      if (el === "Check") { if (check) checks.push(check); check = null; }
      else if (el === "Heading" || el === "Section") stack.pop();
      continue;
    }
    if (el === "MCSettings") name = attr(tag, "Name");
    else if (el === "Heading" || el === "Section") {
      const node = { kind: el, name: attr(tag, el === "Heading" ? "HeadingText" : "SectionName") ?? "", checked: lc(attr(tag, "IsChecked")) === "true" };
      if (!selfClose) stack.push(node);
    } else if (el === "Check") {
      const own = lc(attr(tag, "IsChecked")) === "true";
      const parked = stack.some((n) => PARKED(n.name));
      const enabled = own && stack.every((n) => n.checked);
      check = { id: attr(tag, "ID"), name: attr(tag, "CheckName") ?? "", path: stack.map((n) => n.name), parked, enabled, active: enabled && !parked && stack.some((n) => n.kind === "Heading"), filters: [] };
      if (selfClose) { checks.push(check); check = null; }
    } else if (el === "Filter" && check) {
      check.filters.push({ operator: attr(tag, "Operator") ?? "And", category: attr(tag, "Category") ?? "", property: attr(tag, "Property") ?? "", condition: attr(tag, "Condition") ?? "", value: attr(tag, "Value") ?? "" });
    }
  }
  return { name, checks };
}

/**
 * What one check requires: the parameters it tests with HasValue / Defined, the categories it includes (Category
 * filters, Included, And/Or before the first Exclude) and the level (TypeOrInstance "Is Element Type").
 */
export function checkRequirement(check) {
  const params = [], categories = [], excluded = [];
  let level = null, excluding = false, otherParam = false, byClass = false;
  for (const f of check.filters) {
    const op = lc(f.operator);
    if (op === "exclude") excluding = true;
    const cat = lc(f.category);
    if (cat === "apitype") byClass = true;
    if (cat === "parameter" && /^(hasvalue|defined)$/i.test(f.condition) && lc(f.value) !== "false" && f.property) params.push(f.property);
    else if (cat === "parameter") otherParam = true;
    else if (cat === "category" && lc(f.condition) === "included" && /^(and|or)$/.test(op) && !excluding) categories.push(f.property);
    else if (cat === "category" && (excluding || op === "exclude")) excluded.push(f.property);
    else if (cat === "typeorinstance" && lc(f.property) === "is element type" && lc(f.condition) === "equal" && !excluding && level == null)
      level = lc(f.value) === "true" ? "type" : "instance";
  }
  // A requirement check only tests that parameters have a value: a value condition (Equal 0, Contains ...) or a scope
  // by API class (no category) makes it a model QA check, which no requirement row can express.
  const requirement = params.length > 0 && !otherParam && !(byClass && !categories.length);
  return { params: uniq(params), categories: uniq(categories), excluded: uniq(excluded), level: level ?? "any", requirement };
}

const ROOMISH = new Set(["OST_Rooms", "OST_Areas", "OST_MEPSpaces"]);
/** Revit categories -> group: only Project Information / only rooms, areas, spaces / only levels; else elements. */
export function groupOfCategories(cats) {
  if (!cats?.length) return "elements";
  if (cats.every((c) => c === "OST_ProjectInformation")) return "project";
  if (cats.every((c) => ROOMISH.has(c))) return "rooms";
  if (cats.every((c) => c === "OST_Levels")) return "levels";
  return "elements";
}

/** Active parameter checks -> raw rows { name, group, level, categories, source }. */
export function checkerRows(checker) {
  const rows = [];
  for (const c of checker.checks) {
    if (!c.active) continue;
    const r = checkRequirement(c);
    if (!r.requirement) continue;
    const group = groupOfCategories(r.categories);
    for (const name of r.params)
      rows.push({ name, group, level: r.level, categories: group === "elements" ? r.categories : [], source: { checker: c.name, id: c.id } });
  }
  return rows;
}

// ---- LOIN workbook -------------------------------------------------------------------------------------------------

const squash = (v) => String(v ?? "").replace(/\s+/g, "").toLowerCase();

/** Default LOIN header texts (matched ignoring case and spaces); a mapping's loinTable { name, category, group } overrides them. */
export const LOIN_TABLE = { name: ["shared parameter", "parameter", "parameter name"], category: ["category"], group: ["group"] };

/**
 * LOIN xlsx -> { rows: [{ sheet, row, group, category, name }], sheets: [names with a header] }. A sheet is read when one
 * of its first 10 rows holds the three header texts; other sheets (cover, document management) are skipped.
 */
export function readLoin(buf, table = {}) {
  const heads = Object.fromEntries(Object.entries(LOIN_TABLE).map(([k, d]) => [k, listOf(table?.[k] ?? d).map(squash)]));
  const x = readXlsx(buf);
  const bySheet = new Map();
  for (const u of x.units) { if (!bySheet.has(u.sheet)) bySheet.set(u.sheet, []); bySheet.get(u.sheet).push(u); }
  const rows = [], sheets = [];
  for (const { name: sheet } of x.sheets) {
    const units = bySheet.get(sheet) ?? [];
    let head = null;
    for (const u of units.slice(0, 10)) {
      const col = (test) => Object.keys(u.cells).find((k) => test(squash(u.cells[k])));
      const g = col((v) => heads.group.includes(v)), c = col((v) => heads.category.includes(v)), n = col((v) => heads.name.includes(v));
      if (g && c && n) { head = { row: u.row, g, c, n }; break; }
    }
    if (!head) continue;
    sheets.push(sheet);
    let group = "", category = "";
    for (const u of units) {
      if (u.row <= head.row) continue;
      // Blank GROUP and CATEGORY cells carry the one above (merged cells). Line breaks inside a cell (Alt+Enter)
      // become spaces, so the mapping file's keys stay one line.
      const gv = String(u.cells[head.g] ?? "").trim();
      const cv = String(u.cells[head.c] ?? "").replace(/\s+/g, " ").trim();
      const name = String(u.cells[head.n] ?? "").trim();
      if (!name && !cv) continue;
      if (gv) group = gv;
      if (cv) category = cv;
      rows.push({ sheet, row: u.row, group, category, name });
    }
  }
  return { rows, sheets };
}

// ---- best guesses (generic Revit knowledge; a person confirms them in the mapping file) ---------------------------

/** [pattern, Revit categories]; the first match wins, so specific rules come before general ones. [] = unsure. */
const CATEGORY_RULES = [
  [/\bfinish(es|ing)?\b|\bskirting|ground cover|gravel|paving|hardscape|treatments?|protection|\bgrates?\b|fire hose/i, []],
  [/curtain|fa[cç]ade|cladding|glazed partition|glazing/i, ["OST_Walls", "OST_CurtainWallPanels", "OST_CurtainWallMullions"]],
  [/\bdoors?\b/i, ["OST_Doors"]],
  [/\bwindows?\b/i, ["OST_Windows"]],
  [/\brebar\b|reinforc/i, ["OST_Rebar"]],
  [/pile caps?|\bpiles?\b|\brafts?\b|footings?|foundations?/i, ["OST_StructuralFoundation"]],
  [/architectural columns?/i, ["OST_Columns"]],
  [/\bcolumns?\b/i, ["OST_StructuralColumns"]],
  [/\bbeams?\b|framing|bracing|\btruss(es)?\b|lintels?/i, ["OST_StructuralFraming"]],
  [/lighting devices?|light switch(es)?/i, ["OST_LightingDevices"]],
  [/\blight(s|ing)?\b|\blamps?\b|luminaires?|uplights?|downlights?|floodlights?/i, ["OST_LightingFixtures"]],
  [/\bwalls?\b/i, ["OST_Walls"]],
  [/\bfloors?\b|\bslabs?\b/i, ["OST_Floors"]],
  [/\broofs?\b/i, ["OST_Roofs"]],
  [/ceilings?/i, ["OST_Ceilings"]],
  [/\bstairs?\b|staircase/i, ["OST_Stairs"]],
  [/\bramps?\b/i, ["OST_Ramps"]],
  [/railings?|balustrades?|handrails?|guardrails?/i, ["OST_StairsRailing"]],
  [/air terminals?|diffusers?|grilles?|\bregisters?\b/i, ["OST_DuctTerminal"]],
  [/duct accessor|dampers?/i, ["OST_DuctAccessory"]],
  [/\bduct(s|ing|work)?\b/i, ["OST_DuctCurves", "OST_DuctFitting", "OST_FlexDuctCurves"]],
  [/sprinklers?/i, ["OST_Sprinklers"]],
  [/\bvalves?\b|pipe accessor/i, ["OST_PipeAccessory"]],
  [/\bpumps?\b|chillers?|\bahus?\b|air handling|\bfcus?\b|fan coil|\bfans?\b|\btanks?\b|boilers?|cooling towers?|heat exchangers?|air conditioners?|condensing units?|water heaters?|mechanical equipment/i, ["OST_MechanicalEquipment"]],
  [/\bpip(e|es|ing)\b/i, ["OST_PipeCurves", "OST_PipeFitting", "OST_FlexPipeCurves"]],
  [/plumbing fixtures?|sanitary|\bwcs?\b|basins?|\bsinks?\b|toilets?/i, ["OST_PlumbingFixtures"]],
  [/switchboards?|distribution boards?|panel ?boards?|electrical panels?|transformers?|generators?|feeder pillars?|electrical equipment/i, ["OST_ElectricalEquipment"]],
  [/cable trays?|containment|\btrays?\b/i, ["OST_CableTray", "OST_CableTrayFitting"]],
  [/conduits?/i, ["OST_Conduit", "OST_ConduitFitting"]],
  [/fire alarm|detectors?|call points?|sounders?/i, ["OST_FireAlarmDevices"]],
  [/security|cctv|cameras?|access control/i, ["OST_SecurityDevices"]],
  [/nurse call/i, ["OST_NurseCallDevices"]],
  [/communication|telephone|\bdata\b|network/i, ["OST_CommunicationDevices", "OST_DataDevices"]],
  [/sockets?|outlets?|receptacles?|electrical fixtures?/i, ["OST_ElectricalFixtures"]],
  [/furniture systems?/i, ["OST_FurnitureSystems"]],
  [/furniture|ff&e/i, ["OST_Furniture"]],
  [/casework|cabinet|joinery/i, ["OST_Casework"]],
  [/speciality equipment|specialty equipment/i, ["OST_SpecialityEquipment"]],
  [/planting|\btrees?\b|shrubs?|vegetation|\bplants\b/i, ["OST_Planting"]],
  [/topograph|toposolid|terrain|earthworks?|grading/i, ["OST_Toposolid", "OST_Topography"]],
  [/parking/i, ["OST_Parking"]],
  [/\broads?\b|car parks?/i, ["OST_Roads"]],
  [/\bsite\b|bollards?|fenc(e|es|ing)|street furniture/i, ["OST_Site"]],
  [/^rooms?$/i, ["OST_Rooms"]],
  [/^(mep )?spaces?$/i, ["OST_MEPSpaces"]],
  [/^areas?$/i, ["OST_Areas"]],
  [/^levels?$/i, ["OST_Levels"]],
  [/generic models?/i, ["OST_GenericModel"]],
];
export const guessCategories = (label) => { const l = String(label).trim(); return [...(CATEGORY_RULES.find(([re]) => re.test(l))?.[1] ?? [])]; };

const SHEET_RULES = [[/^(ARC|ARCH|ARCHITECTURE)$/, "AR"], [/^(STR|STRUCT|STRUCTURE|STRUCTURAL)$/, "ST"], [/^ID$/, "ID"], [/^(LSC|LANDSCAPE)$/, "LA"], [/^(MECH|MECHANICAL)$/, "ME"]];
/** LOIN sheet name -> discipline codes by best guess; [] (all models) when unsure. */
export function guessDisciplines(sheet) {
  const tokens = String(sheet).toUpperCase().split(/[^A-Z0-9]+/).filter(Boolean);
  for (const [re, code] of SHEET_RULES) if (tokens.some((t) => re.test(t))) return [code];
  return [];
}

// ---- mapping file --------------------------------------------------------------------------------------------------

export const MAPPING_FILE = "ace-parameter-mapping.json";
const ABOUT = "ACE parameter requirement mapping. Edit freely; ACE never overwrites a value you set.";

/**
 * The mapping with keys for every category and sheet seen. A new mapping (existing null) gets best guesses and
 * draft: true; an existing one only gains keys it lacks (empty lists = unmapped). Values a person set are never changed.
 */
export function extendMapping(existing, { categories = [], sheets = [] } = {}) {
  const created = !existing;
  const m = existing ? JSON.parse(JSON.stringify(existing)) : { about: ABOUT, draft: true, fill: { value: null, values: [] } };
  for (const k of ["categories", "disciplines", "aliases", "models"]) if (!m[k] || typeof m[k] !== "object" || Array.isArray(m[k])) m[k] = {};
  const added = { categories: [], disciplines: [] };
  for (const c of uniq(categories.map((c) => String(c).trim()).filter(Boolean)))
    if (!Object.hasOwn(m.categories, c)) { m.categories[c] = created ? guessCategories(c) : []; added.categories.push(c); }
  for (const s of uniq(sheets))
    if (!Object.hasOwn(m.disciplines, s)) { m.disciplines[s] = created ? guessDisciplines(s) : []; added.disciplines.push(s); }
  return { mapping: m, created, added, changed: created || added.categories.length + added.disciplines.length > 0 };
}

/**
 * A mapping in the richer project shape (loinCategories per sheet with bic / filter / confidence, nameAliases with
 * prefixMap / explicit / typoDuplicates, derive, naPolicy, each with a status) -> the shape the merge reads. Sections
 * whose status is not "approved" keep their values but mark them proposed: proposed aliases are reported as candidates,
 * never used to pass a name (the decision rule), and a mapping that is not approved is reported as unconfirmed.
 * A mapping already in ACE's own shape comes back unchanged (aliases a person wrote there count as approved).
 */
export const approved = (s) => /^approved\b/i.test(String(s ?? "").trim());
export function normaliseMapping(raw) {
  if (!raw || typeof raw !== "object") return raw;
  if (!raw.loinCategories && !raw.nameAliases) return raw;
  const plainMap = (o) => Object.fromEntries(Object.entries(o ?? {}).filter(([k]) => k !== "status" && !k.startsWith("_")));
  const m = { about: raw._about ?? raw.about, foreign: true, draft: !approved(raw.status), status: raw.status ?? null,
    categories: { ...(raw.categories ?? {}) }, sheetCategories: {}, categoryMeta: {}, filters: [], disciplines: plainMap(raw.disciplines),
    sectionStatus: Object.fromEntries(["loinCategories", "disciplines", "groupDisciplines", "models", "ownership", "categories"]
      .filter((k) => raw[k] && typeof raw[k] === "object" && raw[k].status != null).map((k) => [k, raw[k].status])),
    groupDisciplines: raw.groupDisciplines ? plainMap(raw.groupDisciplines) : null,
    aliases: {}, proposedAliases: {}, prefixMap: {}, models: plainMap(raw.models), ownership: plainMap(raw.ownership),
    derive: raw.derive ?? null, naPolicy: raw.naPolicy ?? null, sources: raw.sources ?? null };
  for (const [sheet, cats] of Object.entries(raw.loinCategories ?? {})) {
    if (!cats || typeof cats !== "object") continue;
    m.sheetCategories[sheet] = {};
    m.categoryMeta[sheet] = {};
    for (const [label, v] of Object.entries(cats)) {
      if (label === "status" || label.startsWith("_")) continue;
      const bic = listOf(Array.isArray(v) || typeof v === "string" ? v : v?.bic);
      const key = label.replace(/\s+/g, " ").trim();
      m.sheetCategories[sheet][key] = bic;
      if (v && typeof v === "object" && !Array.isArray(v)) m.categoryMeta[sheet][key] = { status: v.status ?? null, confidence: v.confidence ?? null };
      if (v?.filter) m.filters.push({ sheet, category: label, filter: v.filter, confidence: v.confidence ?? null });
    }
  }
  const na = raw.nameAliases ?? {};
  const into = approved(na.status) ? m.aliases : m.proposedAliases;
  for (const k of ["explicit", "typoDuplicates"]) for (const [from, to] of Object.entries(na[k] ?? {})) if (to) into[from] = String(to);
  for (const [from, to] of Object.entries(na.prefixMap ?? {})) if (to) m.prefixMap[from] = String(to);
  m.prefixApproved = approved(na.status);
  // An approved spelling rule: case / whitespace-only differences resolve to the shared parameter file's spelling.
  if (approved(na.status) && typeof na.spellingRule === "string" && na.spellingRule.trim()) m.spellingRule = na.spellingRule.trim();
  m.notes = [];
  const auth = raw.nameAuthority;
  if (auth && approved(auth.status)) {
    if (/^(client|project)$/i.test(auth.source ?? "")) m.nameAuthority = auth.source.toLowerCase();
    else m.notes.push(`nameAuthority has no source ("client" or "project"): the project shared parameter file's GUIDs are expected.`);
  }
  return m;
}

/** The alias for a listed name: explicit first, then a prefix map entry; { to, approved } or null. */
export function aliasFor(mapping, name) {
  if (!mapping) return null;
  if (mapping.aliases && Object.hasOwn(mapping.aliases, name) && mapping.aliases[name]) return { to: String(mapping.aliases[name]), approved: true };
  if (mapping.proposedAliases && Object.hasOwn(mapping.proposedAliases, name)) return { to: mapping.proposedAliases[name], approved: false };
  for (const [from, to] of Object.entries(mapping.prefixMap ?? {}))
    if (name.startsWith(from)) return { to: to + name.slice(from.length), approved: !!mapping.prefixApproved };
  return null;
}

const listOf = (v) => (Array.isArray(v) ? v : typeof v === "string" && v ? [v] : []).map((x) => String(x).trim()).filter(Boolean);

// ---- shared parameter index and name classification ----------------------------------------------------------------

const normName = (n) => String(n).toLowerCase().replace(/[\s_]+/g, "");
const PREFIX = /^([A-Za-z]+)_([A-Za-z0-9&]+)_(.+)$/;

/** files: [{ file, kind: "project" | "client", params }] -> index for classify(); conflicts = same name, different GUIDs. */
export function paramIndex(files = []) {
  const idx = { project: new Map(), client: new Map(), norm: new Map(), rest: new Map(), conflicts: [], files: files.length };
  const guids = new Map();
  for (const f of files) {
    for (const p of f.params) {
      const entry = { ...p, file: f.file, kind: f.kind };
      const own = idx[f.kind === "project" ? "project" : "client"];
      const first = own.get(p.name);
      if (!first) own.set(p.name, entry);
      else if (first.file === f.file && p.guid && !(first.guids ?? [first.guid]).includes(p.guid)) {
        // One file listing a name several times with different GUIDs (e.g. one per category): every GUID is accepted.
        first.guids = [...(first.guids ?? [first.guid]), p.guid];
      }
      if (!guids.has(p.name)) guids.set(p.name, new Map());
      guids.get(p.name).set(p.guid, [...(guids.get(p.name).get(p.guid) ?? []), path.basename(f.file ?? "")]);
      const n = normName(p.name);
      if (!idx.norm.has(n)) idx.norm.set(n, []);
      if (!idx.norm.get(n).includes(p.name)) idx.norm.get(n).push(p.name);
      const pm = PREFIX.exec(p.name);
      if (pm) {
        const key = `${pm[1].toLowerCase()}\0${normName(pm[3])}`;
        if (!idx.rest.has(key)) idx.rest.set(key, []);
        if (!idx.rest.get(key).includes(p.name)) idx.rest.get(key).push(p.name);
      }
    }
  }
  idx.duplicateGuids = [];
  for (const own of [idx.project, idx.client]) for (const e of own.values())
    if (e.guids?.length > 1) idx.duplicateGuids.push({ name: e.name, file: path.basename(e.file ?? ""), kind: e.kind, guids: e.guids });
  // Between files only: a name repeated with other GUIDs inside one file is reported as duplicateGuids instead.
  for (const [name, g] of guids) if (g.size > 1 && new Set([...g.values()].flat()).size > 1)
    idx.conflicts.push({ name, guids: Object.fromEntries([...g].map(([k, v]) => [k, v.join(", ")])) });
  return idx;
}

// The name authority decides whose GUID and data type a row expects: the project file (default) or the client's files
// (mapping nameAuthority). A name in both with different GUIDs is flagged on the row.
const lookup = (idx, name) => idx.authority === "client" ? idx.client.get(name) ?? idx.project.get(name) : idx.project.get(name) ?? idx.client.get(name);
const expected = (idx, name, match) => {
  const p = lookup(idx, name), other = idx.authority === "client" ? idx.project.get(name) : idx.client.get(name);
  const out = { match, name, guid: p.guid, dataType: p.dataType, defFile: path.basename(p.file ?? "") || undefined };
  if (p.guids?.length > 1) out.guids = p.guids;
  if (other && other.guid && p.guid && other.guid.toLowerCase() !== p.guid.toLowerCase()) out.otherGuid = other.guid;
  return out;
};

/** A listed name -> { match, name (the one to check), guid?, dataType?, candidate?, candidates?, aliasOf? }. Never guesses. */
export function classifyName(name, idx, aliases = {}) {
  if (/[[\]]/.test(name)) return { match: "pseudo", name };
  // aliases: a plain { listed: target } object (approved), or a normalised mapping (explicit, prefix map, proposed).
  const al = aliases && (aliases.aliases || aliases.prefixMap || aliases.proposedAliases) ? aliasFor(aliases, name)
    : Object.hasOwn(aliases ?? {}, name) && aliases[name] ? { to: String(aliases[name]), approved: true } : null;
  if (al && al.to !== name && al.approved) {
    const p = idx && lookup(idx, al.to);
    return { match: "alias", name: al.to, aliasOf: name, guid: p?.guid, dataType: p?.dataType };
  }
  if (al && al.to !== name && !(idx && (idx.project.has(name) || idx.client.has(name)))) {
    // A proposed (not yet approved) alias: checked under the listed name, the alias reported as the candidate.
    return { match: "alias-proposed", name, candidate: al.to };
  }
  if (!idx || !idx.files) return { match: "unchecked", name };
  if (idx.project.has(name)) return expected(idx, name, "exact");
  if (idx.client.has(name)) return expected(idx, name, "exact-client");
  const spelled = (idx.norm.get(normName(name)) ?? []).filter((n) => n !== name);
  const caseSpace = (n) => String(n).toLowerCase().replace(/\s+/g, " ").trim();
  const ruled = aliases?.spellingRule ? spelled.filter((n) => caseSpace(n) === caseSpace(name)) : [];
  if (ruled.length === 1) return { ...expected(idx, ruled[0], "alias"), aliasOf: name, rule: "spelling" };
  if (spelled.length) return { match: "spelling", name, candidate: spelled[0], candidates: spelled.length > 1 ? spelled : undefined };
  const pm = PREFIX.exec(name);
  if (pm) {
    const code = pm[2].toLowerCase();
    // Codes that contain the listed code's letters in order (ME -> MECH) come first.
    const near = (c) => { let i = 0; for (const ch of c.toLowerCase()) if (ch === code[i]) i++; return i === code.length ? 0 : 1; };
    const drift = (idx.rest.get(`${pm[1].toLowerCase()}\0${normName(pm[3])}`) ?? []).filter((n) => PREFIX.exec(n)[2].toLowerCase() !== code)
      .sort((a, b) => near(PREFIX.exec(a)[2]) - near(PREFIX.exec(b)[2]));
    if (drift.length) return { match: "prefix-drift", name, candidate: drift[0], candidates: drift.length > 1 ? drift : undefined };
  }
  return { match: "unknown", name };
}

function editDistance(a, b, max) {
  if (Math.abs(a.length - b.length) > max) return max + 1;
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const cur = [i];
    let best = i;
    for (let j = 1; j <= b.length; j++) {
      cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
      best = Math.min(best, cur[j]);
    }
    if (best > max) return max + 1;
    prev = cur;
  }
  return prev[b.length];
}

/**
 * Names in the shared parameter files with the same prefix that differ in one word by one or two letters (likely
 * typo twins: one word misspelt). Short words (codes, Min / Max) and digits never count.
 */
export function nearDuplicates(idx) {
  const groups = new Map();
  for (const n of uniq([...idx.project.keys(), ...idx.client.keys()])) {
    const pm = PREFIX.exec(n);
    const k = pm ? `${pm[1]}_${pm[2]}`.toLowerCase() : "";
    if (!groups.has(k)) groups.set(k, []);
    groups.get(k).push({ n, words: (pm ? pm[3] : n).toLowerCase().split(/[\s_]+/).filter(Boolean) });
  }
  const out = [];
  for (const list of groups.values()) for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
    const [a, b] = [list[i], list[j]];
    if (a.words.length !== b.words.length) continue;
    const diff = a.words.map((w, k) => [w, b.words[k]]).filter(([x, y]) => x !== y);
    if (diff.length !== 1) continue;
    const [x, y] = diff[0];
    if (Math.min(x.length, y.length) < 5 || /\d/.test(x + y)) continue;
    if (editDistance(x, y, 2) <= 2) out.push([a.n, b.n]);
  }
  return out;
}

// ---- merge ---------------------------------------------------------------------------------------------------------

/** A source reference in words: "AIS line 12", "LOIN Sheet1!5", "checker: <check name>". */
export const srcText = (s) => s.ais != null ? `AIS line ${s.ais}` : s.loin != null ? `LOIN ${s.loin}` : s.checker != null ? `checker: ${s.checker}` : "";

/**
 * All sources -> { rows (merged, with server-only sources / match / candidate / dataType), bridgeRows, defects }.
 * ais: rows from rowsFromCsv / mapRows ({ name, group, level, line? }); loin: readLoin().rows; checker: checkerRows().
 */
export function mergeSources({ ais = [], loin = [], checker = [], mapping = null, index = null } = {}) {
  const aliases = mapping?.foreign ? mapping : mapping?.aliases ?? {};
  const raw = [];
  const byGroup = groupScope(mapping);
  // AIS and checker rows take their discipline scope from the mapping's groupDisciplines (none or "*" = every model).
  const scoped = (r) => {
    const d = byGroup[r.group];
    if (d?.length) {
      r.source.disciplines = d;
      if (!sectionApproved(mapping, "groupDisciplines")) Object.assign(r.source, { draftMapping: true, planOnly: "the group -> discipline scope (groupDisciplines) in the mapping is not approved" });
    }
    return r;
  };
  for (const r of ais) raw.push({ name: r.name, group: r.group, level: r.level, categories: [], ...(r.origin ? { origin: r.origin } : {}), source: { ais: r.line ?? "row" } });
  raw.forEach(scoped);
  for (const r of checker) raw.push(scoped({ ...r, categories: [...(r.categories ?? [])], source: { ...r.source } }));
  const unmapped = new Map(), pseudo = [], unmappedRows = [], noScope = new Set();
  for (const r of loin) {
    if (!r.name) continue;
    const label = r.category.trim();
    const perSheet = mapping?.sheetCategories?.[r.sheet];
    const cats = listOf(perSheet && Object.hasOwn(perSheet, label) ? perSheet[label] : mapping?.categories?.[label]);
    const disc = listOf(mapping?.disciplines?.[r.sheet]);
    if (mapping && !disc.length) noScope.add(r.sheet);
    const why = categoryHold(mapping, r.sheet, label) ?? (disc.length && !sectionApproved(mapping, "disciplines") ? "the LOIN sheet -> discipline scope in the mapping is not approved" : null);
    const source = { loin: `${r.sheet}!${r.row}`, category: label, disciplines: disc, ...(why ? { draftMapping: true, planOnly: why } : {}) };
    if (/[[\]]/.test(r.name)) { pseudo.push({ name: r.name, ...source }); continue; }
    if (!cats.length) {
      const u = unmapped.get(label) ?? { category: label, rows: 0, sheets: new Set(), names: new Set() };
      u.rows++; u.sheets.add(r.sheet); u.names.add(r.name); unmapped.set(label, u);
      unmappedRows.push({ name: r.name, group: "elements", level: "any", status: "unmapped", sources: [source], match: classifyName(r.name, index, aliases).match });
      continue;
    }
    const group = groupOfCategories(cats);
    raw.push({ name: r.name, group, level: "any", categories: group === "elements" ? cats : [], source });
  }
  const byKey = new Map(), levelConflicts = [], classes = new Map();
  for (const r of raw) {
    if (/[[\]]/.test(r.name)) { pseudo.push({ name: r.name, ...r.source }); continue; }
    if (!classes.has(r.name)) classes.set(r.name, classifyName(r.name, index, aliases));
    const cls = classes.get(r.name);
    const name = cls.name;
    const cats = uniq(r.categories).sort();
    const key = `${name}\0${r.group}\0${r.group === "elements" ? cats.join("|") : ""}`;
    let m = byKey.get(key);
    if (!m) {
      m = { name, group: r.group, level: r.level, categories: r.group === "elements" ? cats : [], disciplines: new Set(), allModels: false, sources: [], match: cls.match };
      for (const k of ["guid", "guids", "dataType", "candidate", "aliasOf", "otherGuid", "defFile"]) if (cls[k]) m[k] = cls[k];
      if (r.origin) m.origin = r.origin;
      byKey.set(key, m);
    } else if (r.origin && !m.origin) m.origin = r.origin;
    if (m.level !== r.level && m.sources.length) {
      if (m.level === "any") m.level = r.level;
      else if (r.level !== "any") levelConflicts.push({ name, group: r.group, kept: m.level, other: r.level, source: srcText(r.source) });
    }
    m.sources.push(r.source);
    if (r.source.disciplines?.length) r.source.disciplines.forEach((d) => m.disciplines.add(d));
    else m.allModels = true;
  }
  const rows = [...byKey.values()].map((m, i) => {
    const { disciplines, allModels, ...rest } = m;
    const out = { ...rest, key: `r${i + 1}` };
    // A row only an unconfirmed (draft) mapping put on its categories is checked but never filled.
    if (!allModels && disciplines.size) out.disciplines = [...disciplines].sort();
    // Rows whose scope or categories rest on an unapproved part of the mapping are plan-only: checked, never written.
    if (out.sources.every((s) => s.draftMapping)) { out.draftMapping = true; out.planOnly = out.sources.find((s) => s.planOnly)?.planOnly ?? "its categories come only from a draft (unconfirmed) mapping"; }
    else if (out.disciplines && Object.keys(mapping?.models ?? {}).length && !sectionApproved(mapping, "models")) { out.draftMapping = true; out.planOnly = "the model -> discipline map in the mapping is not approved"; }
    else if (out.group === "elements" && !out.categories.length && Object.keys(mapping?.ownership ?? {}).length && !sectionApproved(mapping, "ownership")) { out.draftMapping = true; out.planOnly = "the category ownership in the mapping is not approved"; }
    if (!out.categories.length) delete out.categories;
    return out;
  });
  const bridgeRows = rows.map(bridgeRow);
  const candidates = [];
  for (const [listed, c] of classes) if (c.match === "spelling" || c.match === "prefix-drift" || c.match === "alias-proposed") candidates.push({ name: listed, match: c.match, candidate: c.candidate, candidates: c.candidates });
  const defects = {
    unmappedCategories: [...unmapped.values()].map((u) => ({ category: u.category, rows: u.rows, sheets: [...u.sheets], names: u.names.size })),
    pseudoNames: pseudo,
    nameCandidates: candidates,
    unknownNames: [...classes].filter(([, c]) => c.match === "unknown").map(([n]) => n),
    levelConflicts,
    guidConflicts: index?.conflicts ?? [],
    duplicateGuids: index?.duplicateGuids ?? [],   // one client file defines a name several times with different GUIDs
    nearDuplicates: index ? nearDuplicates(index) : [],
    categoryFilters: mapping?.filters?.length ? mapping.filters : undefined,   // type / name filters the check does not apply yet
    sheetsWithoutDisciplines: [...noScope],   // LOIN sheets whose rows apply to every model until the mapping names disciplines
  };
  return { rows, bridgeRows, unmappedRows, defects };
}

/** A mapping section is approved by its own status, else by the mapping's (a draft mapping approves nothing). */
export function sectionApproved(mapping, name) {
  const st = mapping?.sectionStatus?.[name] ?? (mapping?.[name] && typeof mapping[name] === "object" ? mapping[name].status : undefined);
  return st != null ? approved(st) : !mapping?.draft;
}
/**
 * Why rows of one LOIN category entry are plan-only, or null: the entry's own status decides; an entry with a confidence
 * other than "high" waits for its approval (a pile category mapped to columns would write pile data on every column);
 * else the category section's (or the mapping's) status.
 */
export function categoryHold(mapping, sheet, label) {
  if (!mapping) return null;
  const meta = mapping.categoryMeta?.[sheet]?.[label];
  if (meta?.status != null) return approved(meta.status) ? null : `the category entry "${label}" is not approved (${meta.status})`;
  if (meta?.confidence && String(meta.confidence).toLowerCase() !== "high") return `the category entry "${label}" has confidence ${meta.confidence} and is not approved`;
  return sectionApproved(mapping, mapping.sheetCategories ? "loinCategories" : "categories") ? null : "its categories come only from a draft (unconfirmed) mapping";
}

/** 1.10 add-ins say "wrong-guid" / wrongGuid; since 1.11 the status is "guid-differs" (the files may be the stale side). */
export function normaliseAnswer(result) {
  for (const d of result?.documents ?? []) {
    for (const r of d.rows ?? []) if (r.status === "wrong-guid") r.status = "guid-differs";
    if (d.summary && d.summary.wrongGuid != null && d.summary.guidDiffers == null) { d.summary.guidDiffers = d.summary.wrongGuid; delete d.summary.wrongGuid; }
  }
  for (const r of result?.after?.rows ?? []) if (r.status === "wrong-guid") r.status = "guid-differs";
  return result;
}

/**
 * The mapping's groupDisciplines: { "<list group label or key>": ["ST"] | "*" } -> { project | rooms | levels | elements:
 * codes }; "*" (or no entry) = every model.
 */
export function groupScope(mapping) {
  const out = {};
  for (const [k, v] of Object.entries(mapping?.groupDisciplines ?? {})) {
    if (k === "status" || k.startsWith("_")) continue;
    const g = groupOf(k);
    const codes = listOf(v);
    if (g && codes.length && !codes.includes("*")) out[g] = codes;
  }
  return out;
}

/** The fields the add-in reads (server-only ones stay here). */
export function bridgeRow(r) {
  const b = { name: r.name, group: r.group, level: r.level, key: r.key };
  if (r.categories?.length) b.categories = r.categories;
  if (r.guid) b.guid = r.guid;
  if (r.guids?.length > 1) b.guids = r.guids;
  if (r.disciplines?.length) b.disciplines = r.disciplines;
  return b;
}

/**
 * Generic discipline (role) codes as they appear in ISO 19650 style file names (e.g. "PRJ-ORG-ZZ-ST-..."): used to read
 * a model's discipline from its title when the project mapping names none. Generic, never project data.
 */
export const ROLE_CODES = ["AR", "ST", "ME", "EL", "PL", "FP", "ID", "LA", "CV", "BM", "PS"];

/** scope for the bridge: discipline codes, model -> discipline, category -> owning disciplines (LOIN sheets unless the mapping says otherwise). */
export function buildScope(mapping, rows = []) {
  const models = { ...(mapping?.models ?? {}) };
  const ownership = {};
  for (const r of rows) {
    if (r.group !== "elements" || !r.categories?.length) continue;
    for (const s of r.sources) {
      if (s.loin == null || !s.disciplines?.length) continue;
      for (const c of r.categories) ownership[c] = uniq([...(ownership[c] ?? []), ...s.disciplines]).sort();
    }
  }
  for (const [c, v] of Object.entries(mapping?.ownership ?? {})) ownership[c] = listOf(v);
  let codes = uniq([...Object.values(mapping?.disciplines ?? {}).flatMap(listOf), ...Object.values(groupScope(mapping)).flat(), ...Object.values(models).flatMap(listOf), ...Object.values(ownership).flat()]).sort();
  // Without project codes the model's discipline still comes from its title, through the generic role codes.
  if (!codes.length) codes = [...ROLE_CODES];
  return { codes, models, ownership };
}

// ---- after the bridge answer ---------------------------------------------------------------------------------------

/** Cross-model checks: the same name with different GUIDs across models; Project Information twins whose values differ. */
export function crossModel(result) {
  const docs = result?.documents ?? [];
  const guids = new Map();
  for (const d of docs) for (const r of d.rows ?? []) {
    if (!r.modelGuid) continue;
    if (!guids.has(r.name)) guids.set(r.name, new Map());
    guids.get(r.name).set(d.model, String(r.modelGuid).toLowerCase());
  }
  const guidDiffers = [];
  for (const [name, byModel] of guids) if (new Set(byModel.values()).size > 1) guidDiffers.push({ name, models: Object.fromEntries(byModel) });
  const twins = [];
  for (const d of docs) {
    const byRest = new Map();
    for (const r of d.rows ?? []) {
      if (r.group !== "project") continue;
      const pm = PREFIX.exec(r.name);
      if (!pm) continue;
      const k = `${pm[1].toLowerCase()}\0${normName(pm[3])}`;
      if (!byRest.has(k)) byRest.set(k, []);
      byRest.get(k).push(r);
    }
    for (const list of byRest.values()) for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
      const [a, b] = [list[i], list[j]];
      if (PREFIX.exec(a.name)[2] === PREFIX.exec(b.name)[2]) continue;
      const va = String(a.value ?? "").trim(), vb = String(b.value ?? "").trim();
      if (va !== vb) twins.push({ model: d.model, a: a.name, b: b.name, valueA: va, valueB: vb });
    }
  }
  // Categories the add-in could not resolve (e.g. a mistyped OST_ name in the mapping): those rows check nothing there.
  const unknown = new Map();
  for (const d of docs) for (const r of d.rows ?? []) for (const c of r.unknownCategories ?? []) {
    const u = unknown.get(c) ?? { category: c, models: new Set(), names: new Set() };
    u.models.add(d.model); u.names.add(r.name); unknown.set(c, u);
  }
  const unknownCategories = [...unknown.values()].map((u) => ({ category: u.category, models: [...u.models], names: [...u.names] }));
  return { guidDiffers, twins, unknownCategories };
}

// ---- text ----------------------------------------------------------------------------------------------------------

const few = (list, n = 8) => list.slice(0, n).join("; ") + (list.length > n ? `; ... ${list.length - n} more` : "");

/** Short text for the defects (each list capped; the full lists are in the JSON / report). */
export function defectsText(d = {}) {
  const lines = [];
  if (d.unmappedCategories?.length) lines.push(`Unmapped client categories (${d.unmappedCategories.length}, rows not checked until a person maps them in the mapping file): ${few(d.unmappedCategories.map((u) => `${u.category} (${u.rows})`))}`);
  if (d.nameCandidates?.length) lines.push(`Name candidates to resolve (${d.nameCandidates.length}; checked under the listed name, add an alias once confirmed): ${few(d.nameCandidates.map((c) => `${c.name} -> ${c.candidate} (${c.match})`))}`);
  if (d.pseudoNames?.length) lines.push(`Not parameter names (bracketed, skipped): ${d.pseudoNames.length}`);
  if (d.unknownNames?.length) lines.push(`Not in the shared parameter files (built-in or missing): ${d.unknownNames.length}: ${few(d.unknownNames)}`);
  if (d.nearDuplicates?.length) lines.push(`Near-duplicate names in the shared parameter files (typo twins?): ${few(d.nearDuplicates.map(([a, b]) => `${a} / ${b}`))}`);
  if (d.duplicateGuids?.length) lines.push(`Names a client file defines several times with different GUIDs (every GUID is accepted): ${few(d.duplicateGuids.map((x) => `${x.name} x${x.guids.length} (${x.file})`))}`);
  if (d.guidConflicts?.length) lines.push(`Same name, different GUIDs in the shared parameter files: ${few(d.guidConflicts.map((c) => c.name))}`);
  if (d.levelConflicts?.length) lines.push(`Type/instance disagreements between sources: ${few(d.levelConflicts.map((c) => `${c.name} (${c.kept} vs ${c.other})`))}`);
  if (d.guidDiffers?.length) lines.push(`Different GUIDs across models: ${few(d.guidDiffers.map((g) => g.name))}`);
  if (d.sheetsWithoutDisciplines?.length) lines.push(`Discipline scope not set in the mapping for LOIN sheet(s) (their rows apply to every model): ${few(d.sheetsWithoutDisciplines)}`);
  if (d.categoryFilters?.length) lines.push(`Mapping filter hints not applied yet (${d.categoryFilters.length}; those rows are judged on the whole Revit category): ${few(d.categoryFilters.map((f) => `${f.category}: ${f.filter}`))}`);
  if (d.unknownCategories?.length) lines.push(`Revit categories the model does not know (typo in the mapping?): ${few(d.unknownCategories.map((u) => `${u.category} (${u.names.length} row(s))`))}`);
  if (d.twins?.length) lines.push(`Project Information twins with different values: ${few(d.twins.map((t) => `${t.model}: ${t.a}="${t.valueA}" vs ${t.b}="${t.valueB}"`))}`);
  return lines.join("\n");
}

/** One line per source and the match counts. */
export function requirementsText(rows, info = {}) {
  const by = (k) => { const o = {}; for (const r of rows) o[r[k]] = (o[r[k]] ?? 0) + 1; return Object.entries(o).map(([a, n]) => `${n} ${a}`).join(", "); };
  const lines = [`Requirements: ${rows.length} merged rows (${by("group")}); names: ${by("match")}.`];
  if (info.sources) lines.push(`Sources: ${Object.entries(info.sources).map(([k, v]) => `${k} ${v}`).join(", ")}.`);
  return lines.join("\n");
}

// ---- P8: client checker review -------------------------------------------------------------------------------------

const GENERIC_CODES = ["AR", "ARC", "ST", "STR", "ME", "MEP", "MECH", "EL", "ELE", "ELV", "EV", "PL", "PLU", "FP", "FF", "ID", "LA", "LS", "CV", "CIV"];

/** The checker as the client wrote it: active vs parked, per category, scope defects, unknown names, duplicates, no category. */
export function reviewChecker(checker, { ais = [], index = null, codes = [] } = {}) {
  const pc = checker.checks.map((c) => ({ c, r: checkRequirement(c) })).filter(({ r }) => r.requirement);
  const active = pc.filter(({ c }) => c.active), parked = pc.filter(({ c }) => c.parked), off = pc.filter(({ c }) => !c.active && !c.parked);
  const perCategory = {};
  for (const { r } of active) for (const cat of r.categories.length ? r.categories : ["(none)"]) perCategory[cat] = uniq([...(perCategory[cat] ?? []), ...r.params]).sort();
  const aisElements = new Set(ais.filter((r) => r.group === "elements").map((r) => r.name));
  const aisNames = new Set(ais.map((r) => r.name));
  const discCodes = new Set([...GENERIC_CODES, ...codes].map((c) => c.toUpperCase()));
  const scopeDefects = [], unknown = [], noCategory = [], dup = new Map();
  const scopeOf = (c, r) => {
    for (const p of r.params) {
      const code = PREFIX.exec(p)?.[2]?.toUpperCase();
      if (r.categories.length && r.categories.every((x) => x === "OST_ProjectInformation") && (aisElements.has(p) || (code && discCodes.has(code))))
        scopeDefects.push({ check: c.name, param: p, parked: !c.active || undefined, reason: aisElements.has(p) ? "an element parameter in the AIS, checked only on Project Information" : `a ${code} discipline parameter checked only on Project Information` });
    }
  };
  // Parked checks too: they run as soon as someone moves them out of Unused / Unplaced.
  for (const { c, r } of parked) scopeOf(c, r);
  for (const { c, r } of active) {
    scopeOf(c, r);
    if (!r.categories.length) noCategory.push({ check: c.name, params: r.params });
    for (const p of r.params) {
      if ((index?.files || ais.length) && !(index && lookup(index, p)) && !aisNames.has(p)) unknown.push(p);
      const k = `${p}\0${[...r.categories].sort().join("|")}\0${r.level}`;
      dup.set(k, [...(dup.get(k) ?? []), c.name]);
    }
  }
  const duplicates = [...dup].filter(([, v]) => v.length > 1).map(([k, v]) => ({ param: k.split("\0")[0], categories: k.split("\0")[1], checks: v }));
  const activeNames = new Set(active.flatMap(({ r }) => r.params));
  const parkedOnly = uniq(parked.flatMap(({ r }) => r.params)).filter((n) => !activeNames.has(n));
  return {
    checker: checker.name, checks: checker.checks.length,
    parameterChecks: { active: active.length, parked: parked.length, disabled: off.length },
    activeParameters: activeNames.size, parkedOnlyParameters: parkedOnly,
    perCategory: Object.fromEntries(Object.entries(perCategory).map(([k, v]) => [k, { count: v.length, params: v }])),
    scopeDefects, unknownNames: uniq(unknown), duplicates, noCategory,
  };
}

export function reviewText(rv) {
  const lines = [
    `Checker "${rv.checker ?? "?"}": ${rv.checks} checks; parameter checks ${rv.parameterChecks.active} active, ${rv.parameterChecks.parked} parked in Unused/Unplaced, ${rv.parameterChecks.disabled} switched off.`,
    `Active parameter checks per category: ${Object.entries(rv.perCategory).map(([k, v]) => `${k} ${v.count}`).join(", ") || "none"}.`,
  ];
  if (rv.parkedOnlyParameters.length) lines.push(`Parameters checked only in parked checks (not run): ${rv.parkedOnlyParameters.length}.`);
  if (rv.scopeDefects.length) {
    const parked = rv.scopeDefects.filter((d) => d.parked).length;
    lines.push(`Scope defects (${rv.scopeDefects.length}: ${rv.scopeDefects.length - parked} in active checks, ${parked} in parked checks): ${few(rv.scopeDefects.map((d) => `${d.param}${d.parked ? " (parked)" : ""}: ${d.reason}`), 5)}`);
  }
  if (rv.unknownNames.length) lines.push(`Names in neither the shared parameter files nor the AIS (${rv.unknownNames.length}): ${few(rv.unknownNames)}`);
  if (rv.duplicates.length) lines.push(`Duplicated checks (${rv.duplicates.length}): ${few(rv.duplicates.map((d) => `${d.param} x${d.checks.length}`))}`);
  if (rv.noCategory.length) lines.push(`Parameter checks with no category (${rv.noCategory.length}): ${few(rv.noCategory.map((d) => d.check))}`);
  return lines.join("\n");
}

export function reviewMarkdown(rv) {
  const out = [`# Model Checker review: ${rv.checker ?? ""}`, "", reviewText(rv).split("\n").map((l) => `- ${l}`).join("\n"), "", "## Active parameter checks per category", ""];
  for (const [k, v] of Object.entries(rv.perCategory)) out.push(`- **${k}** (${v.count}): ${v.params.join(", ")}`);
  const section = (title, items) => { if (items.length) out.push("", `## ${title}`, "", ...items.map((i) => `- ${i}`)); };
  section("Scope defects", rv.scopeDefects.map((d) => `${d.param} (${d.check}${d.parked ? ", parked" : ""}): ${d.reason}`));
  section("Names in neither the shared parameter files nor the AIS", rv.unknownNames);
  section("Duplicated checks", rv.duplicates.map((d) => `${d.param} [${d.categories || "no category"}]: ${d.checks.join("; ")}`));
  section("Parameter checks with no category", rv.noCategory.map((d) => `${d.check}: ${d.params.join(", ")}`));
  section("Parameters only in parked checks", rv.parkedOnlyParameters);
  return out.join("\n") + "\n";
}

// ---- reports -------------------------------------------------------------------------------------------------------

const REQ_COLS = ["key", "name", "group", "level", "categories", "disciplines", "guid", "dataType", "match", "candidate", "sources"];
const reqCells = (r) => [r.key, r.name, r.group, r.level, r.categories ?? [], r.disciplines ?? [], r.guid, r.dataType, r.match, r.candidate, r.sources.map(srcText).join("; ")];

/** Merged rows, then the unmapped ones (status "unmapped": reported, not checked). */
export const requirementsCsv = (rows, unmapped = []) => toCsv([["status", ...REQ_COLS], ...rows.map((r) => ["", ...reqCells(r)]), ...unmapped.map((r) => ["unmapped", ...reqCells(r)])]);

const CHECK_COLS = ["model", "discipline", "status", "checked", "filled", "blank", "na", "missing", "onType", "guidMatch", "modelGuid", "unboundCategories", "wrongLevel", "value"];
/** Every model x row: status, counts, sources and the row's defects. */
export function checkCsv(result, rows, unmapped = []) {
  const byKey = new Map(rows.map((r) => [r.key, r]));
  const out = [];
  for (const d of result?.documents ?? []) for (const x of d.rows ?? []) {
    const r = byKey.get(x.key) ?? { ...x, sources: x.sources ?? [] };
    out.push([d.model, d.discipline, x.status, x.checked, x.filled, x.blank, x.na, x.missing, x.onType, x.guidMatch, x.modelGuid, x.unboundCategories ?? [], x.wrongLevel, x.value, ...reqCells(r)]);
  }
  for (const r of unmapped) out.push(["", "", "unmapped", "", "", "", "", "", "", "", "", [], "", "", ...reqCells(r)]);
  return toCsv([[...CHECK_COLS, ...REQ_COLS], ...out]);
}

/** Writes { name: content } into dir (created), each name stamped with the local date and time to the second; returns the paths. */
export function writeReports(dir, files) {
  fs.mkdirSync(dir, { recursive: true });
  const at = localDate(new Date(), { time: true });
  return Object.entries(files).map(([name, content]) => {
    const ext = path.extname(name);
    const f = path.join(dir, `${name.slice(0, name.length - ext.length)}_${at}${ext}`);
    fs.writeFileSync(f, content);
    return f;
  });
}

// ---- file helpers --------------------------------------------------------------------------------------------------

/** A JSON object file: null when the file is missing; anything that is not a JSON object is an error (never rewritten). */
export function readJsonFile(file, buf) {
  let v;
  try { v = JSON.parse((buf ?? fs.readFileSync(file)).toString("utf8").replace(/^﻿/, "")); }
  catch (err) { if (err.code === "ENOENT") return null; throw new Error(`${file}: ${err.message}`); }
  if (!v || typeof v !== "object" || Array.isArray(v)) throw new Error(`${file} is not a JSON object; ACE leaves it as it is. Fix or remove it.`);
  return v;
}

const plainObject = (v) => !!v && typeof v === "object" && !Array.isArray(v);
/** ACE's own mapping shape: the about marker and the four sections as plain objects (only such a file is ever extended). */
export const isAceMapping = (m) => plainObject(m) && typeof m.about === "string" && m.about.startsWith(ABOUT.split(".")[0])
  && ["categories", "disciplines", "aliases", "models"].every((k) => plainObject(m[k]));

/** Writes through a temporary file and a rename, so a crash never leaves half a file. */
export function writeFileAtomic(file, content) {
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, content);
  fs.renameSync(tmp, file);
}

/**
 * The mapping for the sources: created with best guesses only when missing; extended (new keys only) only when it is
 * ACE's own shape; any other file is read, never written (a note says why). Returns { file, mapping, created, added, readOnly?, note? }.
 */
export function syncMappingFile(file, seen, existing = readJsonFile(file)) {
  const none = { categories: [], disciplines: [] };
  if (existing && !isAceMapping(existing))
    return { file, mapping: existing, created: false, added: none, readOnly: true, note: `${path.basename(file)} is not in ACE's mapping shape (about marker, categories, disciplines, aliases, models): read only, new categories and sheets are not added.` };
  const ext = extendMapping(existing, seen);
  if (ext.changed) writeFileAtomic(file, JSON.stringify(ext.mapping, null, 2) + "\n");
  return { file, ...ext };
}

/** sha256 over [path, bytes] pairs, in order (a changed source changes the hash). */
export function hashBytes(entries) {
  const h = crypto.createHash("sha256");
  for (const [f, buf] of entries) { h.update(f + "\0"); h.update(buf); h.update("\0"); }
  return h.digest("hex");
}
/** The same over files on disk. */
export const hashFiles = (files) => hashBytes(files.map((f) => [f, fs.readFileSync(f)]));

/**
 * A project-shaped mapping can name its sources (paths relative to the mapping file; "*" in a file name is a wildcard):
 * any source the call leaves out is taken from there, so one call with only `mapping` runs the whole check.
 */
export function sourcesFromMapping(args) {
  const out = { ...args };
  if (!args.mapping) return out;
  const src = readJsonFile(args.mapping)?.sources;
  if (!src || typeof src !== "object") return out;
  const dir = path.dirname(args.mapping);
  const resolve = (p) => {
    const full = path.resolve(dir, String(p));
    if (!path.basename(full).includes("*")) return [full];
    const re = new RegExp("^" + path.basename(full).split("*").map((x) => x.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$", "i");
    const d = path.dirname(full);
    return fs.existsSync(d) ? fs.readdirSync(d).filter((f) => re.test(f)).sort().map((f) => path.join(d, f)) : [];
  };
  const one = (k) => (src[k] ? resolve(src[k])[0] : undefined);
  if (!out.list && !out.rows) out.list = one("ais") ?? one("list");
  if (!out.loin) out.loin = one("loin");
  if (!out.checker) out.checker = one("checker");
  if (!out.project_parameters) out.project_parameters = one("projectSharedParameters") ?? one("project_parameters");
  if (!out.shared_parameters?.length) {
    const many = [].concat(src.clientSharedParameters ?? src.shared_parameters ?? []).flatMap(resolve);
    if (many.length) out.shared_parameters = many;
  }
  return out;
}

/**
 * Every source the tool was given -> { rows, bridgeRows, defects, scope, mapping, info, files, hash }. Each file is read
 * once (its bytes go into `hash`, the fill's source identity). The mapping file is created when missing (beside the first
 * source unless a path is given), extended only when it is ACE's own shape, and otherwise only read.
 */
export function loadSources(args) {
  const { list, rows: inlineRows, loin, checker, shared_parameters = [], project_parameters, mapping: mapFile, create_mapping } = sourcesFromMapping(args);
  const read = [];
  const bytes = (f) => { if (!fs.existsSync(f)) throw new Error(`File not found: ${f}`); const b = fs.readFileSync(f); read.push([f, b]); return b; };
  const info = { sources: {}, notes: [] };
  // The mapping first: it says how to read the LOIN (loinTable).
  const none = { categories: [], disciplines: [] };
  let existing = null;
  if (mapFile) {
    if (!/\.json$/i.test(mapFile)) throw new Error(`The mapping file must be a .json file: ${mapFile}`);
    existing = readJsonFile(mapFile);
    if (!existing && !create_mapping) throw new Error(`Mapping file not found: ${mapFile} (create_mapping: true writes a draft with best guesses).`);
  }
  let ais = [], listInfo = { skipped: [], duplicates: [] };
  if (list) {
    const m = rowsFromCsv(sharedFileText(bytes(list)));
    ais = m.rows.map((r, i) => ({ ...r, line: m.lines[i] })); listInfo = { skipped: m.skipped, duplicates: m.duplicates };
    info.sources.list = m.rows.length;
  } else if (inlineRows) {
    const m = mapRows(inlineRows);
    ais = m.rows; listInfo = { skipped: m.skipped, duplicates: m.duplicates };
    info.sources.rows = m.rows.length;
  }
  let loinRows = [], loinSheets = [];
  if (loin) { const l = readLoin(bytes(loin), existing?.loinTable); loinRows = l.rows; loinSheets = l.sheets; info.sources.loin = l.rows.length; }
  let checkerData = null, chRows = [];
  if (checker) {
    checkerData = readCheckerXml(sharedFileText(bytes(checker)));
    if (!checkerData.checks.length) throw new Error(`${checker} has no Check elements: is it a Model Checker settings file (MCSettings XML)?`);
    chRows = checkerRows(checkerData); info.sources.checker = chRows.length;
  }
  const spFiles = [];
  for (const f of [].concat(shared_parameters ?? [])) spFiles.push({ file: f, kind: "client", params: readSharedParameterFile(bytes(f)).params });
  if (project_parameters) spFiles.unshift({ file: project_parameters, kind: "project", params: readSharedParameterFile(bytes(project_parameters)).params });
  const index = spFiles.length ? paramIndex(spFiles) : null;
  let map = null, mapInfo = null;
  if (mapFile) {
    const seen = { categories: loinRows.map((r) => r.category), sheets: loinSheets };
    if (existing && (existing.loinCategories || existing.nameAliases)) {
      // A project-shaped mapping (see normaliseMapping) is read, never rewritten by ACE.
      mapInfo = { file: mapFile, mapping: normaliseMapping(existing), created: false, added: none, readOnly: true };
      info.notes.push(...(mapInfo.mapping.notes ?? []));
    } else if (existing && !loin) mapInfo = { file: mapFile, mapping: existing, created: false, added: none, readOnly: !isAceMapping(existing) };
    else mapInfo = syncMappingFile(mapFile, seen, existing);   // created only here (create_mapping), extended only in ACE's shape
    if (mapInfo.note) info.notes.push(mapInfo.note);
    map = mapInfo.mapping;
    read.push([mapFile, fs.readFileSync(mapFile)]);   // after a sync: the bytes as now on disk
  } else if (loin || checker) {
    info.notes.push("No project mapping found (vault rules/parameter-mapping.json, or pass mapping): generic checks only - LOIN categories are unmapped, no name aliases, no discipline scope, no NA policy. create_mapping: true writes a draft with best guesses for a person to confirm.");
  }
  if (index) {
    const a = map?.nameAuthority;
    index.authority = typeof a === "string" ? a.toLowerCase() : null;
    if (index.authority === "client" && !spFiles.some((f) => f.kind === "client")) index.authority = null;
  }
  const merged = mergeSources({ ais, loin: loinRows, checker: chRows, mapping: map, index });
  const files = read.map(([f]) => f);
  const fileHashes = Object.fromEntries(read.map(([f, b]) => [f, hashBytes([[f, b]])]));
  return { ...merged, scope: buildScope(map, merged.rows), mapping: mapInfo, naPolicy: naPolicy(map), listInfo, info, files, fileHashes, hash: read.length ? hashBytes(read) : null, checker: checkerData, ais, index, projectFile: project_parameters };
}

/** The project's NA policy from the mapping: naPolicy (project shape) or fill (ACE shape) { value, values }; nulls when unset. */
export function naPolicy(map) {
  const p = map?.naPolicy ?? map?.fill ?? null;
  const given = typeof p?.value === "string" && p.value.trim() ? p.value : null;
  const values = uniq([...listOf(p?.values), ...(given ? [given] : [])]);
  // A policy with a status counts only once approved: until then it names the NA texts (for counting) but fills nothing.
  if (given && p.status != null && !approved(p.status)) return { value: null, values, pending: given };
  return { value: given, values };
}
