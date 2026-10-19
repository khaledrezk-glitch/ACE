// Stage 4 of the parameter requirements workflow (check > reports > actions > execution): ONE plan per model from the
// selected auto-fixable actions (bindings, derived values, the NA fill), the derive rules read from the project mapping,
// the IFC property set file (impact), the missing register (P7) and the evidence after an applied fill. Pure functions:
// index.js reads and writes the files.
import crypto from "node:crypto";
import path from "node:path";
import { approved, sharedFileText, srcText, uniq } from "./paramsources.js";
import { toCsv } from "./report.js";
import { localDate } from "./core.js";

export const sha256 = (buf) => crypto.createHash("sha256").update(buf).digest("hex");
const short = (s) => crypto.createHash("sha1").update(s).digest("hex").slice(0, 8);
/** A stable action id: kind + a short hash of what the action is about (the same finding gets the same id next time). */
export const actionId = (kind, ...parts) => `${kind}-${short([kind, ...parts].map((p) => Array.isArray(p) ? p.join("|") : String(p ?? "")).join("\0"))}`;
export const kindOf = (id) => String(id).split("-")[0];

// ---- derive rules from the mapping ---------------------------------------------------------------------------------

/** A string formula -> a rule, when it reads as a join ("concat with '-': A, B, C") or a copy ("copy from A"); else null. */
export function parseFormula(target, text) {
  const t = String(text ?? "").trim().replace(/\s*\([^()]*\)\s*$/, "");   // a trailing note in brackets
  const plain = (p) => p && !/[()=+'"]/.test(p);
  let m = /^(?:concat(?:enate)?|join)\s*(?:with\s*(?:'([^']*)'|"([^"]*)"))?\s*:\s*(.+)$/i.exec(t);
  if (m) {
    const parts = m[3].split(",").map((p) => p.trim());
    if (parts.length >= 2 && parts.every(plain)) return { target, join: parts, separator: m[1] ?? m[2] ?? "" };
    return null;
  }
  m = /^(?:copy(?:\s+from)?|same\s+as|=)\s*:?\s*(.+)$/i.exec(t);
  if (m && plain(m[1].trim()) && !m[1].includes(",")) return { target, copyFrom: m[1].trim() };
  return null;
}

const ruleText = (r) => r.value != null ? `${r.target} = "${r.value}"` : r.copyFrom ? `${r.target} = ${r.copyFrom}` : `${r.target} = ${r.join.join(r.separator ? ` + '${r.separator}' + ` : " + ")}`;

/** A model title glob ("*" = any run, case-insensitive) -> does it match; and how specific it is (exact 2, glob 1, "*" 0). */
export const globMatch = (glob, title) => new RegExp("^" + String(glob).trim().split("*").map((x) => x.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$", "i").test(String(title ?? "").trim().replace(/\.rvt$/i, ""));
export const globRank = (glob) => String(glob).trim() === "*" ? 0 : String(glob).includes("*") ? 1 : 2;
/**
 * Model-title rules in the mapping that match no open model: derive globs (projectInformationValues keys; a glob matches
 * the FULL title) and models keys (matched as part of the title, as the add-in does, or as a glob). -> warning lines.
 */
export function unmatchedTitleRules({ derive = null, models = null } = {}, titles = []) {
  const t = titles.filter(Boolean);
  if (!t.length) return [];
  const list = t.slice(0, 6).join(", ") + (t.length > 6 ? `, ... ${t.length - 6} more` : "");
  const out = [];
  for (const glob of uniq((derive?.groups ?? []).map((g) => g.glob).filter((g) => g != null && String(g).trim() !== "*")))
    if (!t.some((x) => globMatch(glob, x))) out.push(`Warning: rule '${glob}' matches no open model: ${list} (a glob matches the full model title; its values are not written).`);
  for (const key of Object.keys(models ?? {}).filter((k) => k !== "status" && k !== "titleField" && !k.startsWith("_")))
    if (!t.some((x) => String(x).toLowerCase().includes(key.toLowerCase()) || (key.includes("*") && globMatch(key, x))))
      out.push(`Warning: models entry '${key}' matches no open model: ${list}.`);
  return out;
}
/** Whether a derive group applies to a model: no glob, or its glob matches and no more specific glob sets the same target. */
export function groupApplies(g, groups, model) {
  if (g.glob == null) return true;
  if (!globMatch(g.glob, model)) return false;
  return !groups.some((o) => o !== g && o.glob != null && o.targets[0] === g.targets[0] && globMatch(o.glob, model) && globRank(o.glob) > globRank(g.glob));
}
function validRule(o) {
  if (!o || typeof o !== "object" || typeof o.target !== "string" || !o.target.trim()) return null;
  const target = o.target.trim();
  if (typeof o.copyFrom === "string" && o.copyFrom.trim() && o.join == null) return { target, copyFrom: o.copyFrom.trim() };
  if (typeof o.value === "string" && o.copyFrom == null && o.join == null) return { target, value: o.value };
  if (Array.isArray(o.join) && o.join.length && o.join.every((p) => typeof p === "string" && p.trim()) && o.copyFrom == null)
    return { target, join: o.join.map((p) => p.trim()), separator: typeof o.separator === "string" ? o.separator : "" };
  return null;
}

/**
 * The mapping's derive section, in ACE's shape (an array of { target, copyFrom } / { target, join, separator }, or
 * { rules: [...], status }) or the project shape ({ twins: { pairs: [[A, B]] }, "<target>": "<formula>" | {...}, status }).
 * -> { groups: [{ id, text, rules, targets, twin? }], problems: [{ id, target, text, reason }], approved, status, present }.
 * A twin pair is two copy rules (the blank side takes the filled side; the add-in never overwrites a value).
 */
export function deriveRules(mapping) {
  const d = mapping?.derive;
  const out = { groups: [], problems: [], approved: false, status: null, present: !!d && (Array.isArray(d) ? d.length > 0 : Object.keys(d).length > 0) };
  if (!out.present) return out;
  out.status = Array.isArray(d) ? null : d.status ?? null;
  out.approved = out.status != null ? approved(out.status) : !mapping.draft;
  const group = (rules, text, extra = {}) => out.groups.push({ id: actionId("derive", text), text, rules, targets: uniq(rules.map((r) => r.target)), ...extra });
  const problem = (target, text) => out.problems.push({ id: actionId("derive", target, text), target, text: String(text), reason: "rule not machine-readable" });
  const one = (target, v) => {
    const r = typeof v === "string" ? parseFormula(target, v) : validRule({ target, ...v });
    if (r) group([r], ruleText(r)); else problem(target, typeof v === "string" ? v : JSON.stringify(v));
  };
  for (const r of Array.isArray(d) ? d : Array.isArray(d.rules) ? d.rules : []) {
    const v = validRule(r);
    if (v) group([v], ruleText(v)); else problem(r?.target ?? "?", JSON.stringify(r));
  }
  if (!Array.isArray(d)) {
    // Literal Project Information values per model title glob ("*" = every model); the most specific glob wins per parameter.
    for (const [glob, values] of Object.entries(d.projectInformationValues ?? {})) {
      if (glob.startsWith("_")) continue;
      if (!values || typeof values !== "object") { problem(glob, JSON.stringify(values)); continue; }
      for (const [target, value] of Object.entries(values)) {
        if (target.startsWith("_")) continue;
        if (typeof value !== "string" || !value.trim()) { problem(target, JSON.stringify(value)); continue; }
        group([{ target, value }], `${target} = "${value}" (models ${glob})`, { glob });
      }
    }
    const pairs = Array.isArray(d.twins) ? d.twins : Array.isArray(d.twins?.pairs) ? d.twins.pairs : [];
    for (const p of pairs) {
      if (!Array.isArray(p) || p.length !== 2 || !p.every((x) => typeof x === "string" && x.trim())) { problem("?", JSON.stringify(p)); continue; }
      const [a, b] = p.map((x) => x.trim());
      group([{ target: a, copyFrom: b }, { target: b, copyFrom: a }], `${a} <-> ${b} (twins: the blank one takes the filled one)`, { twin: [a, b] });
    }
    for (const [k, v] of Object.entries(d)) {
      if (["rules", "twins", "status", "projectInformationValues"].includes(k) || k.startsWith("_")) continue;
      one(k, v);
    }
  }
  return out;
}

// ---- IFC user-defined property sets (P4) ---------------------------------------------------------------------------

/**
 * The IFC export's user-defined property set file: "PropertySet:\t<name>\t<I|T>\t<entities>" then "\t<prop>\t<type>[\t<revit
 * param>]" lines (# comments). -> { psets: [...], byParameter: Map(revit name -> [{ pset, property, type, level, entities }]) }.
 */
export function readIfcPropertySets(input) {
  const text = Buffer.isBuffer(input) ? sharedFileText(input) : String(input ?? "");
  const psets = [], byParameter = new Map();
  let pset = null;
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim() || raw.trim().startsWith("#")) continue;
    if (/^\s*PropertySet:/i.test(raw)) {
      const f = raw.replace(/^\s*PropertySet:\s*/i, "").split("\t").map((x) => x.trim());
      pset = { name: f[0], level: /^t/i.test(f[1] ?? "") ? "type" : "instance", entities: (f[2] ?? "").split(",").map((x) => x.trim()).filter(Boolean), properties: [] };
      psets.push(pset);
      continue;
    }
    if (!pset || !/^\s/.test(raw)) continue;
    const f = raw.replace(/^\s+/, "").split("\t").map((x) => x.trim());
    if (!f[0]) continue;
    const revit = f[2] || f[0];
    pset.properties.push({ property: f[0], type: f[1] ?? "", revit });
    if (!byParameter.has(revit)) byParameter.set(revit, []);
    byParameter.get(revit).push({ pset: pset.name, property: f[0], type: f[1] ?? "", level: pset.level, entities: pset.entities });
  }
  return { psets, byParameter };
}

/** Active client checks per written parameter (they turn from fail to pass once its blanks are filled). */
export function checkerFlips(checker, params, checkRequirement) {
  const out = {};
  if (!checker) return out;
  const want = new Set(params);
  for (const c of checker.checks ?? []) {
    if (!c.active) continue;
    const r = checkRequirement(c);
    if (!r.requirement) continue;
    for (const p of r.params) if (want.has(p)) (out[p] ??= []).push(c.name);
  }
  return out;
}

/**
 * Impact lines per parameter: the add-in's scan (schedules with their filters, view filters, labels on sheets: inferred),
 * the IFC property sets and the client checks that flip. value: what the fill writes ("NA").
 */
export function impactLines({ impact = {}, ifc = null, flips = {}, params = [], value = "NA", derive = [] }) {
  const lines = [];
  // What a derived target will actually print: the literal, or the source(s); the fill value only where no rule applies.
  const printed = new Map(derive.map((r) => [r.target, r.value != null ? `"${r.value}"`
    : r.copyFrom ? `the value of ${r.copyFrom} (${value} if that is empty)` : `${r.join.join(r.separator ? ` + '${r.separator}' + ` : " + ")} (${value} if a part is empty)`]));
  for (const p of uniq([...params, ...Object.keys(impact ?? {})])) {
    const i = impact?.[p] ?? {};
    const shows = printed.get(p) ?? value;
    const bits = [];
    const sch = i.schedules ?? [];
    if (sch.length) {
      const hv = sch.filter((s) => (s.filters ?? []).some((f) => /hasvalue/i.test(String(f.type ?? "")))).length;
      bits.push(`${sch.length} schedule(s)${hv ? ` (${hv} with a has-value filter: rows hidden now will show)` : ""}`);
    }
    if (i.viewFilters?.length) bits.push(`${i.viewFilters.length} view filter(s)`);
    const tb = i.titleBlocks;
    const tbLabels = (i.labels ?? []).filter((l) => l.kind === "titleblock");
    // Title blocks: one line for all of them (sheets counted once); other labels one by one.
    if (tb?.sheets) bits.push(`shown on ${tb.sheets} sheet(s) via title blocks: ${uniq(tbLabels.map((l) => l.family)).join(", ") || `${tb.families} family(ies)`} (${tb.instances ?? 0} instance(s)) will print ${shows}${tbLabels.some((l) => l.proxy === "built-in") ? " (inferred: a built-in parameter leaves no trace in the family, so every title block on sheets is listed)" : ""}`);
    for (const l of tb?.sheets ? (i.labels ?? []).filter((x) => x.kind !== "titleblock") : i.labels ?? []) bits.push(`label in ${l.family}${l.kind ? ` (${l.kind})` : ""} will print ${shows} on ${l.sheets ?? 0} sheet(s) (inferred)`);
    for (const s of ifc?.byParameter?.get(p) ?? []) bits.push(`IFC ${s.pset}.${s.property} (${s.entities.join(", ") || "all entities"})`);
    if (flips[p]?.length) bits.push(`client checker: ${flips[p].length} check(s) turn from fail to pass`);
    if (bits.length) lines.push(`${p}: ${bits.join(", ")}`);
  }
  return lines;
}
export const NA_CAVEAT = "Note: \"NA\" passes a HasValue check, so the client's checker turns green without the data; the missing register keeps what is really missing.";

// ---- shared parameter definitions for bindings (P5) ----------------------------------------------------------------

const GROUP_CATEGORIES = { project: ["OST_ProjectInformation"], rooms: ["OST_Rooms", "OST_Areas", "OST_MEPSpaces"], levels: ["OST_Levels"] };
/** The categories a binding of the row covers: its own, else its group's. */
export const bindCategories = (r) => r.categories?.length ? r.categories : GROUP_CATEGORIES[r.group] ?? [];
const NAME_OK = new Set(["exact", "exact-client", "alias"]);
export const isBuiltIn = (r, x) => r?.origin === "built-in" || x?.origin === "built-in" || x?.status === "not-available";

/** Why a row cannot be bound by ACE (plain words), or null when it can. */
export function bindProblem(r, x) {
  if (isBuiltIn(r, x)) return "a Revit built-in parameter: it cannot be bound";
  if (r.draftMapping) return "its categories come only from a draft (unconfirmed) mapping";
  if (r.guids?.length > 1) return `the client file defines it ${r.guids.length} times with different GUIDs: say which one to bind`;
  if (!r.guid) return "no definition in the shared parameter files (ACE never creates a GUID)";
  if (!NAME_OK.has(r.match)) return `the name is not confirmed (${r.match}): resolve it first`;
  if (!bindCategories(r).length) return "no categories to bind to (the row covers every element): list them in the mapping";
  return null;
}

/** Where the row's definition is: the project file (same GUID) or a client file. */
export function definitionFor(r, index) {
  const g = String(r.guid ?? "").toLowerCase();
  const has = (e) => e && [e.guid, ...(e.guids ?? [])].some((x) => String(x).toLowerCase() === g);
  const p = index?.project?.get(r.name);
  if (has(p)) return { kind: "project", file: p.file, entry: p };
  const c = index?.client?.get(r.name);
  if (has(c)) return { kind: "client", file: c.file, entry: c, projectGuid: p?.guid };
  return null;
}

/**
 * The lines of a shared parameter file that define the given GUIDs, with the file's own headers (comments, META, the
 * GROUP lines they use): exactly the client's lines, never rewritten. -> { text, lines }.
 */
export function sharedParameterExtract(text, guids) {
  const want = new Set([...guids].map((g) => String(g).toLowerCase()));
  const all = String(text).split(/\r?\n/);
  let paramHead = null, groupHead = null;
  const head = [], groups = new Map(), params = [];
  for (const line of all) {
    const f = line.split("\t");
    const kind = f[0].replace(/^\*/, "").trim().toUpperCase();
    if (line.startsWith("#") || kind === "META") { head.push(line); continue; }
    if (f[0] === "*GROUP") { groupHead = f.map((h) => h.replace(/^\*/, "").trim().toUpperCase()); continue; }
    if (f[0] === "*PARAM") { paramHead = f.map((h) => h.replace(/^\*/, "").trim().toUpperCase()); continue; }
    if (kind === "GROUP" && groupHead) groups.set((f[groupHead.indexOf("ID")] ?? "").trim(), line);
    if (kind === "PARAM" && paramHead && want.has((f[paramHead.indexOf("GUID")] ?? "").trim().toLowerCase()))
      params.push({ line, group: (f[paramHead.indexOf("GROUP")] ?? "").trim() });
  }
  const rawHead = (name) => all.find((l) => l.startsWith(`*${name}\t`));
  const out = [...head, rawHead("GROUP"), ...uniq(params.map((p) => p.group)).map((g) => groups.get(g)).filter(Boolean), rawHead("PARAM"), ...params.map((p) => p.line)].filter((l) => l != null);
  return { text: out.join("\r\n") + "\r\n", lines: params.map((p) => p.line) };
}

/** Writes text back in the encoding of the source bytes (UTF-16 LE with BOM when the source is UTF-16, else UTF-8). */
export function encodeLike(srcBuf, text) {
  const utf16 = (srcBuf[0] === 0xff && srcBuf[1] === 0xfe) || (srcBuf[0] === 0xfe && srcBuf[1] === 0xff) || srcBuf.subarray(0, 200).filter((b) => b === 0).length > 20;
  return utf16 ? Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(text, "utf16le")]) : Buffer.from(text, "utf8");
}

// ---- the plan for one model ----------------------------------------------------------------------------------------

/**
 * One model's plan from the selected actions. Returns { model, rows (bridge rows), bind, derive, fill, value, held,
 * notes, temp: [{ file, bytes, lines }] } — held: what the selection asked for but cannot run here, with why and how.
 * ctx: { rows (merged), bridgeRow, index, derive (deriveRules()), value, valueProblem, projectFile, tempDir, readFile }.
 */
/** The model's discipline could not be read from its title (several codes in it, or none while rows name disciplines). */
export const disciplineUnsure = (d) => d?.disciplineSource === "ambiguous" || d?.disciplineUnknown === true;
/** The warning for a model whose discipline is not known, or null. */
export function disciplineWarning(d) {
  if (!disciplineUnsure(d)) return null;
  const c = d.disciplineCandidates ?? [];
  return `Warning: ${d.model}: discipline not read from the title${c.length ? `: candidates ${c.join(", ")}` : " (no discipline code in it)"}; set models or titleField in the mapping. Until then this result judges every discipline's rows, and discipline-scoped rows are plan-only (never filled).`;
}

export function buildPlan(doc, selected, ctx) {
  const byKey = new Map(ctx.rows.map((r) => [r.key, r]));
  const docRows = new Map((doc.rows ?? []).map((x) => [x.key ?? x.name, x]));
  const fillKeys = new Set(), bindKeys = new Set(), rebindKeys = new Map(), deriveIds = new Set(), held = [], notes = [];
  const planOnly = new Map();   // rows the selection asked to fill that rest on an unapproved part of the mapping
  for (const a of selected) {
    const run = a.run;
    if (!run || (run.models?.length && !run.models.includes(doc.model))) continue;
    if (run.kind === "fill") for (const k of run.keys) {
      const x = docRows.get(k), r = byKey.get(k);
      if (!x || !(x.blank > 0) || !r) continue;
      if (r.draftMapping) planOnly.set(k, r.planOnly ?? "its category mapping is not approved");
      else fillKeys.add(k);
    }
    if (run.kind === "rebind") for (const k of run.keys) if (docRows.has(k)) rebindKeys.set(k, docRows.get(k).modelGuid ?? run.oldGuid ?? null);
    if (run.kind === "bind") for (const k of run.keys) if (docRows.has(k)) bindKeys.add(k);
    if (run.kind === "derive") deriveIds.add(run.rule);
  }
  if (disciplineUnsure(doc)) {
    // Rows scoped to disciplines cannot be judged for this model while its discipline is unknown: never written.
    const scoped = (k) => byKey.get(k)?.disciplines?.length > 0;
    const out = [...fillKeys, ...bindKeys, ...rebindKeys.keys()].filter(scoped);
    for (const k of out) { fillKeys.delete(k); bindKeys.delete(k); rebindKeys.delete(k); }
    if (out.length) held.push({ what: `${new Set(out).size} discipline-scoped row(s) not written`, why: `the discipline of ${doc.model} is not known${doc.disciplineCandidates?.length ? ` (candidates ${doc.disciplineCandidates.join(", ")})` : ""}`, how: "set models or titleField in the mapping, then run the dry run again" });
  }
  if (planOnly.size) held.push({ what: `${planOnly.size} row(s) not filled`, why: `their category mapping is not approved (${[...new Set(planOnly.values())].slice(0, 3).join("; ")})`, how: "a person approves those mapping entries, then run the dry run again" });
  if (fillKeys.size && !ctx.value) {
    held.push({ what: `the NA fill of ${fillKeys.size} row(s)`, why: ctx.valueProblem, how: "approve the NA policy in the project mapping (or pass value when the mapping has none), then run the dry run again" });
    fillKeys.clear();
  }
  // Bindings: one entry per GUID, the definition only from a file (the project file, else exactly the client's line).
  const bind = [], perClient = new Map();
  if ((bindKeys.size || rebindKeys.size) && !ctx.projectFile) {
    held.push({ what: `${bindKeys.size + rebindKeys.size} binding(s)`, why: "bindings need the project's shared parameter file, and none was given", how: "give project_parameters (or sources.projectSharedParameters in the mapping), then run the dry run again" });
    bindKeys.clear(); rebindKeys.clear();
  }
  const rebind = [];
  const definition = (k, verb) => {
    const r = byKey.get(k), x = docRows.get(k);
    const why = bindProblem(r, x) ?? (definitionFor(r, ctx.index) ? null : "its definition is in no shared parameter file given");
    if (why) { held.push({ what: `${verb} of ${r.name}`, why, how: "resolve it in the mapping or the shared parameter files" }); return null; }
    const def = definitionFor(r, ctx.index);
    const entry = { name: r.name, guid: r.guid, dataType: r.dataType ?? def.entry.dataType, group: r.group, level: r.level === "type" ? "type" : "instance", categories: [...bindCategories(r)] };
    if (r.group === "project") entry.groupTypeId = "General";
    if (def.kind === "project") { entry.file = def.file; entry.fileHash = sha256(ctx.readFile(def.file)); }
    else {
      if (!perClient.has(def.file)) perClient.set(def.file, { guids: new Set(), entries: [] });
      perClient.get(def.file).guids.add(r.guid);
      perClient.get(def.file).entries.push(entry);
      if (def.projectGuid) notes.push(`The project shared parameter file has ${r.name} with another GUID (${def.projectGuid}); the binding uses the client's (${r.guid}): correct the project file.`);
    }
    return entry;
  };
  for (const k of [...bindKeys]) {
    const r = byKey.get(k);
    const same = bind.find((b) => b.guid.toLowerCase() === String(r?.guid ?? "").toLowerCase());
    if (same) { same.categories = uniq([...same.categories, ...bindCategories(r)]); continue; }
    const e = definition(k, "binding");
    if (e) bind.push(e); else bindKeys.delete(k);
  }
  for (const [k, oldGuid] of [...rebindKeys]) {
    const r = byKey.get(k);
    if (!oldGuid) { held.push({ what: `rebind of ${r.name}`, why: "the model's current GUID is not known", how: "re-run the check" }); rebindKeys.delete(k); continue; }
    const e = definition(k, "rebind");
    if (e) rebind.push({ ...e, oldGuid }); else rebindKeys.delete(k);
  }
  const temp = [];
  for (const [file, { guids, entries }] of perClient) {
    const buf = ctx.readFile(file);
    const ex = sharedParameterExtract(sharedFileText(buf), guids);
    const bytes = encodeLike(buf, ex.text);
    const tmp = path.join(ctx.tempDir, `ace-shared-parameters-${crypto.createHash("sha1").update(bytes).digest("hex").slice(0, 12)}.txt`);
    for (const e of entries) { e.file = tmp; e.fileHash = sha256(bytes); }
    temp.push({ file: tmp, bytes, lines: ex.lines, from: file });
    notes.push(`${entries.length} parameter(s) are bound from a temporary file holding exactly the client's line(s) (${tmp}). Add this line to the project shared parameter file:\n${ex.lines.join("\n")}`);
  }
  // Derived values: rules whose target is a requirement row of this model.
  const derive = [], deriveKeys = new Set();
  if (deriveIds.size && !ctx.derive.approved) {
    held.push({ what: `${deriveIds.size} derive rule(s)`, why: `the mapping's derive section is not approved (status: ${ctx.derive.status ?? "draft mapping"})`, how: "a person approves the derive rules in the mapping (status: approved), then run the dry run again" });
    deriveIds.clear();
  }
  for (const g of ctx.derive.groups) {
    if (!deriveIds.has(g.id) || !groupApplies(g, ctx.derive.groups, doc.model)) continue;
    for (const rule of g.rules) {
      const keys = (doc.rows ?? []).filter((x) => x.name === rule.target && !byKey.get(x.key)?.draftMapping && !(disciplineUnsure(doc) && byKey.get(x.key)?.disciplines?.length)).map((x) => x.key ?? x.name);
      if (!keys.length) { held.push({ what: `derive ${ruleText(rule)}`, why: `${rule.target} is not a requirement row of ${doc.model}`, how: "add it to the requirement sources, or leave the rule out" }); continue; }
      keys.forEach((k) => deriveKeys.add(k));
      derive.push(rule.value != null ? { target: rule.target, value: rule.value } : rule.copyFrom ? { target: rule.target, copyFrom: rule.copyFrom } : { target: rule.target, join: rule.join, separator: rule.separator });
    }
  }
  const keys = new Set([...fillKeys, ...bindKeys, ...rebindKeys.keys(), ...deriveKeys]);
  const rows = ctx.rows.filter((r) => keys.has(r.key)).map(ctx.bridgeRow);
  return { model: doc.model, rows, bind, rebind, derive, fill: fillKeys.size > 0, value: fillKeys.size ? ctx.value : undefined, fillRows: fillKeys.size, held, notes, temp };
}

/** One line for a plan (the other models' plans are listed read-only). */
export const planLine = (p) => `${p.model}: ${[p.bind.length ? `bind ${p.bind.length}` : "", p.rebind?.length ? `rebind ${p.rebind.length}` : "", p.derive.length ? `derive ${p.derive.length} rule(s)` : "", p.fill ? `fill "${p.value}" on the blanks of ${p.fillRows} row(s)` : ""].filter(Boolean).join(", ") || "nothing to run"}${p.held.length ? `; held: ${p.held.map((h) => h.what).join("; ")}` : ""}`;
export const hasWork = (p) => p.fill || p.bind.length > 0 || p.derive.length > 0 || p.rebind?.length > 0;

// ---- plain-language blocks and skips -------------------------------------------------------------------------------

const ids = (e) => (e.sampleIds ?? e.ids ?? []).slice(0, 5);
/** { what, why, elements, sampleIds, holder, how } -> one sentence. */
export const blockLine = (b) => `${b.what ?? b.name ?? "?"}: ${b.why ?? b.reason ?? "blocked"}${b.elements ? ` (${b.elements} element(s)${ids(b).length ? `, e.g. ${ids(b).join(", ")}` : ""})` : ""}${b.holder ? `; held by ${b.holder}` : ""}${b.how ? `. How to clear: ${b.how}` : ""}`;

/** Every skip of an answer, in one list: bindings, derived (per rule), fill (grouped skips, else the 1.10 list grouped by reason). */
export function skipsOf(result) {
  const out = [];
  for (const s of result?.bindings?.skipped ?? []) out.push({ part: "bind", ...s, what: s.what ?? `binding of ${s.name}`, why: s.why ?? s.reason });
  for (const d of Array.isArray(result?.derived) ? result.derived : []) for (const s of d.skipped ?? []) out.push({ part: "derive", rule: d.text, ...s });
  const f = result?.fill;
  if (f?.skips?.length) for (const s of f.skips) out.push({ part: "fill", ...s });
  else if (f?.skipped?.length) {
    const by = new Map();
    for (const s of f.skipped) { const g = by.get(s.reason) ?? { part: "fill", what: "fill", why: s.reason, elements: 0, sampleIds: [] }; g.elements++; if (s.id != null && g.sampleIds.length < 5) g.sampleIds.push(s.id); by.set(s.reason, g); }
    out.push(...by.values());
  }
  return out;
}

// ---- P7: missing register and evidence -----------------------------------------------------------------------------

/** A model title as a file or folder name: no separators, reserved characters, control characters or "..". */
export const safeName = (s) => String(s ?? "model").replace(/[\\/:*?"<>|\x00-\x1f]+/g, "_").replace(/\.{2,}/g, "_").replace(/^[.\s]+|[.\s]+$/g, "").slice(0, 120) || "model";
const safe = safeName;
export const fileStem = (kind, model, stamp = localDate(new Date(), { time: true })) => `${kind}-${safe(model)}-${stamp}`;

/** The add-in's register ([{ key, name, category, categoryName, level, count, ids }]) -> CSV, one line per blank element. */
export function registerCsv(model, register = []) {
  const lines = [["model", "parameter", "category", "category name", "level", "element id", "type name"]];
  for (const r of register ?? []) for (const id of r.ids ?? []) lines.push([model, r.name, r.category, r.categoryName, r.level, typeof id === "object" ? id.id : id, typeof id === "object" ? id.type ?? id.typeName : ""]);
  return toCsv(lines);
}

export function registerMarkdown(model, register = [], { when = localDate(new Date(), { time: true }), dryRun } = {}) {
  const total = (register ?? []).reduce((n, r) => n + (r.count ?? r.ids?.length ?? 0), 0);
  const out = [`# Missing register: ${model}`, "", `${when}. Genuinely blank values before ${dryRun ? "the dry run" : "the fill"}: ${total} in ${(register ?? []).length} parameter x category group(s). ` +
    "A value later filled with the NA policy is still missing data: this register is the evidence of what was really missing.", "",
    "| parameter | category | level | blank |", "|---|---|---|---|"];
  for (const r of register ?? []) out.push(`| ${r.name} | ${r.categoryName ?? r.category ?? ""} | ${r.level ?? ""} | ${r.count ?? r.ids?.length ?? 0} |`);
  return out.join("\n") + "\n";
}

/** evidence-<model>-<stamp>.md after an applied fill. */
/** Whether an apply was rolled back (the add-in's rolledBack, or a failed self-check). */
export const wasRolledBack = (result) => result?.rolledBack === true || result?.verify?.ok === false;
/** "N" for a kept apply, "N would have been ...; rolled back" for a rolled-back one (counts from wouldSet / wouldDo). */
export function doneText(o, { field = "set", would = "wouldSet", rolledBack = false, verb = "set" } = {}) {
  if (!rolledBack) return `${o?.[field] ?? 0}`;
  return `0 (${o?.[would] ?? o?.[field] ?? 0} would have been ${verb}; rolled back)`;
}

/**
 * The QA / requirement items the evidence answers, from the mapping: evidence.items [{ id, text, rows?: [names], check?:
 * checker check name }] (a section with a status). Ids are the project's own; nothing is invented.
 */
export function evidenceItems(mapping) {
  const list = mapping?.evidence?.items;
  if (!Array.isArray(list)) return [];
  return list.filter((x) => x && typeof x === "object" && x.id != null).map((x) => ({
    id: String(x.id), text: x.text != null ? String(x.text) : "",
    rows: (Array.isArray(x.rows) ? x.rows : typeof x.rows === "string" ? [x.rows] : []).map(String),
    check: x.check != null ? String(x.check) : undefined,
  }));
}

const DONE = new Set(["complete"]);
/**
 * Each item's status in this model after the apply (or as it was, when rolled back): its rows are the names it lists plus
 * the parameters its checker check tests. met | met with NA (values are "NA", the data is still missing) | partly met |
 * not met | not checked in this model (no row of it in scope).
 */
export function evidenceStatus(items, { rows = [], checkerRows = [] } = {}) {
  return items.map((it) => {
    const names = new Set([...it.rows, ...checkerRows.filter((c) => it.check && c.source?.checker === it.check).map((c) => c.name)].map((n) => n.toLowerCase()));
    const mine = rows.filter((r) => names.has(String(r.name).toLowerCase()) && !["out-of-scope", "unmapped"].includes(r.status));
    const ok = mine.filter((r) => DONE.has(r.status)).length, na = mine.filter((r) => r.status === "complete-with-na").length;
    const open = mine.filter((r) => !DONE.has(r.status) && r.status !== "complete-with-na");
    const status = !names.size || !mine.length ? "not checked in this model" : !open.length ? (na ? "met with NA" : "met") : ok + na ? "partly met" : "not met";
    return { ...it, status, rows: mine.length, open: open.map((r) => `${r.name}: ${r.status}`) };
  });
}

export function evidenceMarkdown({ model, when = localDate(new Date(), { time: true }), register = [], result = {}, plan = {}, blocked = [], held = [], registerFile, items = [] }) {
  const sum = (list, f) => (list ?? []).reduce((n, x) => n + (Number(f(x)) || 0), 0);
  const before = sum(register, (r) => r.count ?? r.ids?.length);
  const b = result.bindings ?? {};
  const derived = Array.isArray(result.derived) ? result.derived : [];
  const rb = wasRolledBack(result);
  // Rolled back: the model is as it was, so nothing is "still blank after" (the end state was never kept).
  const after = rb ? [] : result.after?.rows ?? [];
  const stillBlank = after.filter((r) => r.blank > 0);
  const notBound = after.filter((r) => r.status === "not-bound");
  const v = result.verify;
  const skips = skipsOf(result);
  const out = [`# Parameter fill evidence: ${model}`, "", `${when}. ${result.success && !rb ? "Applied as ONE undo step." : rb ? "Rolled back: nothing changed; the counts below say what would have been written." : "Not applied: nothing changed."}${registerFile ? ` Missing register: ${registerFile}` : ""}`, "",
    "| | count |", "|---|---|",
    `| missing before (blank values) | ${before} |`,
    `| bound | ${doneText(b, { field: "done", would: "wouldDo", rolledBack: rb, verb: "made" })} of ${b.planned ?? plan.bind?.length ?? 0} |`,
    `| derived | ${rb ? `0 (${sum(derived, (d) => d.wouldSet ?? d.set)} would have been set; rolled back)` : sum(derived, (d) => d.set)} (${derived.length} rule(s)) |`,
    ...(result.rebound ? [`| rebound (explicit) | ${doneText(result.rebound, { field: "done", would: "wouldDo", rolledBack: rb, verb: "replaced" })} of ${result.rebound.planned ?? plan.rebind?.length ?? 0}${!rb && (result.rebound.values ?? result.rebound.restored) != null ? `, ${result.rebound.values ?? result.rebound.restored} value(s) restored` : ""} |`] : []),
    `| "${result.fill?.value ?? plan.value ?? "NA"}" written | ${doneText(result.fill, { rolledBack: rb })} |`,
    `| skipped | ${sum(skips, (s) => s.elements ?? 1)} |`,
    `| still blank (rows) | ${stillBlank.length} |`,
    `| not bound (rows) | ${notBound.length} |`,
    `| self-check | ${v ? (v.ok ? `ok (${v.checked ?? "all"} write(s) read back)` : `FAILED: ${v.mismatchCount ?? v.mismatches?.length ?? "?"} mismatch(es), rolled back`) : "not reported"} |`, ""];
  const section = (title, items) => { if (items.length) out.push(`## ${title}`, "", ...items.map((i) => `- ${i}`), ""); };
  section("Bindings", (b.items ?? []).map((i) => `${i.name} (${i.guid}): ${i.action}${i.categories?.length ? ` ${i.categories.join(", ")}` : ""}${i.file ? ` from ${i.file}` : ""}`));
  section("Derived", derived.map((d) => `${d.text ?? d.target}: ${rb ? `${d.wouldSet ?? d.set ?? 0} would have been set; rolled back` : `${d.set ?? 0} set`}`));
  section("Skipped", skips.map((s) => `[${s.part}${s.rule ? `: ${s.rule}` : ""}] ${blockLine(s)}`));
  section("Blocked", [...blocked, ...held].map(blockLine));
  section("Still blank", stillBlank.map((r) => `${r.name}: ${r.blank}`));
  section("Not bound", notBound.map((r) => r.name));
  section("Self-check mismatches", (v?.mismatches ?? []).map((m) => `${m.kind ?? ""} ${m.name ?? m.key ?? ""}${m.id != null ? ` #${m.id}` : ""}: expected ${JSON.stringify(m.expected)}, found ${JSON.stringify(m.actual)}${m.why ? ` (${m.why})` : ""}`));
  if (items.length) {
    out.push(`## Requirement items${rb ? " (as before: nothing changed)" : " (after the apply)"}`, "", "| id | item | status | rows | open |", "|---|---|---|---|---|",
      ...items.map((i) => `| ${i.id} | ${String(i.text).replace(/\|/g, "/")} | ${i.status} | ${i.rows} | ${i.open.slice(0, 5).join("; ").replace(/\|/g, "/")}${i.open.length > 5 ? `; ${i.open.length - 5} more` : ""} |`), "");
    if (items.some((i) => i.status === "met with NA")) out.push("\"met with NA\": the values are \"NA\"; a HasValue check passes, the data itself is still missing (missing register).", "");
  }
  out.push(`Sources of the requirements: ${uniq((plan.sources ?? []).map(srcText)).slice(0, 20).join("; ") || "see the check report"}`, "");
  return out.join("\n");
}
