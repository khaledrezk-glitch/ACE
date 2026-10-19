// Stage 2 and 3 of the parameter requirements workflow (check > reports > actions > execution): the federated summary
// workbook (P9) and the ranked, read-only action list built from a check's findings. Pure functions; the xlsx is
// written with docx.js's zip (no dependency).
import { zip } from "./docx.js";
import { localDate } from "./core.js";
import { esc } from "./vault.js";
import { srcText, uniq } from "./paramsources.js";
import { actionId, bindProblem, groupApplies, isBuiltIn } from "./paramexec.js";
const sourcesText = (r) => (r?.sources ?? []).map(srcText).join("; ");
const fromChecker = (r) => (r?.sources ?? []).some((s) => s.checker != null);

// ---- minimal xlsx writer -------------------------------------------------------------------------------------------

const colName = (i) => { let s = ""; for (i++; i > 0; i = Math.floor((i - 1) / 26)) s = String.fromCharCode(65 + ((i - 1) % 26)) + s; return s; };
const cellXml = (v, ref, style) => {
  if (v == null || v === "") return "";
  if (typeof v === "number" && Number.isFinite(v)) return `<c r="${ref}"${style ? ` s="${style}"` : ""}><v>${v}</v></c>`;
  const t = typeof v === "boolean" ? (v ? "yes" : "no") : Array.isArray(v) ? v.join(", ") : String(v);
  return `<c r="${ref}" t="inlineStr"${style ? ` s="${style}"` : ""}><is><t xml:space="preserve">${esc(t)}</t></is></c>`;
};

/** sheets: [{ name, head: [..], rows: [[..]] }] -> xlsx Buffer with a bold, frozen header row and an autofilter. */
export function xlsxBuffer(sheets) {
  const parts = [];
  const names = sheets.map((s, i) => (String(s.name).replace(/[\\/?*[\]:]/g, " ").slice(0, 31) || `Sheet${i + 1}`));
  sheets.forEach((s, i) => {
    const all = [s.head, ...s.rows];
    const widths = s.head.map((_, c) => Math.min(60, Math.max(8, ...all.slice(0, 500).map((r) => String(Array.isArray(r[c]) ? r[c].join(", ") : r[c] ?? "").length + 2))));
    const rows = all.map((r, ri) => `<row r="${ri + 1}">${r.map((v, ci) => cellXml(v, `${colName(ci)}${ri + 1}`, ri === 0 ? 1 : 0)).join("")}</row>`).join("");
    const last = `${colName(Math.max(0, s.head.length - 1))}${all.length}`;
    parts.push({ name: `xl/worksheets/sheet${i + 1}.xml`, data:
      `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">` +
      `<sheetViews><sheetView workbookViewId="0"${i === 0 ? ' tabSelected="1"' : ""}><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>` +
      `<cols>${widths.map((w, c) => `<col min="${c + 1}" max="${c + 1}" width="${w}" customWidth="1"/>`).join("")}</cols>` +
      `<sheetData>${rows}</sheetData><autoFilter ref="A1:${last}"/></worksheet>` });
  });
  const wbRels = names.map((_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join("");
  return zip([
    { name: "[Content_Types].xml", data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">` +
      `<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>` +
      `<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>` +
      `<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>` +
      names.map((_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join("") + "</Types>" },
    { name: "_rels/.rels", data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>` },
    { name: "xl/workbook.xml", data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>` +
      names.map((n, i) => `<sheet name="${esc(n)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join("") + "</sheets>" +
      `<definedNames>${names.map((n, i) => `<definedName name="_xlnm._FilterDatabase" localSheetId="${i}" hidden="1">'${esc(n.replace(/'/g, "''"))}'!$A$1:$${colName(Math.max(0, sheets[i].head.length - 1))}$${sheets[i].rows.length + 1}</definedName>`).join("")}</definedNames></workbook>` },
    { name: "xl/_rels/workbook.xml.rels", data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${wbRels}` +
      `<Relationship Id="rId${names.length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>` },
    { name: "xl/styles.xml", data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">` +
      `<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>` +
      `<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>` +
      `<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>` +
      `<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>` +
      `<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>` +
      `<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>` },
    ...parts,
  ]);
}

// ---- P9: the federated summary -------------------------------------------------------------------------------------

export const SUMMARY_STATUSES = ["complete", "complete-with-na", "blanks", "partly-bound", "not-bound", "guid-differs", "not-available", "not text", "out-of-scope", "unmapped", "no-elements"];

/** Unmapped rows count against every model whose discipline their LOIN sheet names (or every model when it names none). */
const unmappedFor = (doc, unmapped) => unmapped.filter((r) => {
  const disc = uniq((r.sources ?? []).flatMap((s) => s.disciplines ?? []));
  return !disc.length || !doc.discipline || disc.includes(doc.discipline);
}).length;

/** discipline x model x status counts (one line per model, then a total). */
export function summaryTable(result, unmapped = []) {
  const lines = [];
  const total = Object.fromEntries(SUMMARY_STATUSES.map((s) => [s, 0]));
  let totalRows = 0;
  for (const d of result?.documents ?? []) {
    const c = Object.fromEntries(SUMMARY_STATUSES.map((s) => [s, 0]));
    for (const r of d.rows ?? []) {
      const st = r.status === "wrong-guid" ? "guid-differs" : r.status;   // a 1.10 add-in
      if (st && Object.hasOwn(c, st)) c[st]++;
      if (r.isText === false) c["not text"]++;
    }
    c.unmapped += unmappedFor(d, unmapped);   // the add-in's own "unmapped" rows plus the server's
    const rows = (d.rows ?? []).length;
    totalRows += rows;
    for (const s of SUMMARY_STATUSES) total[s] += c[s];
    lines.push({ discipline: d.discipline ?? "", model: d.model, rows, ...c });
  }
  if (lines.length > 1) lines.push({ discipline: "", model: "All models", rows: totalRows, ...total });
  return lines;
}

const ROW_HEAD = ["model", "discipline", "name", "group", "level", "categories", "status", "checked", "missing", "blank", "na", "filled", "storage type", "bound as", "guid match", "sources", "name match", "candidate"];

/** The workbook: Summary, Rows (model x requirement), Actions, Pre-flight. */
export function summaryWorkbook(result, rows, unmapped, actions) {
  const byKey = new Map(rows.map((r) => [r.key, r]));
  const summary = summaryTable(result, unmapped);
  const rowLines = [];
  for (const d of result?.documents ?? []) for (const x of d.rows ?? []) {
    const r = byKey.get(x.key) ?? x;
    rowLines.push([d.model, d.discipline, x.name, x.group, x.level ?? r.level, x.categories ?? r.categories ?? [], x.status, x.checked, x.missing, x.blank, x.na, x.filled, x.storageType, x.boundAs, x.guidMatch, sourcesText(r), r.match, r.candidate]);
  }
  for (const r of unmapped) rowLines.push(["", "", r.name, r.group, r.level, (r.sources ?? []).map((s) => s.category).filter(Boolean), "unmapped", "", "", "", "", "", "", "", "", sourcesText(r), r.match, r.candidate]);
  const pf = (result?.documents ?? []).map((d) => {
    const p = d.preflight ?? {};
    return [d.model, d.discipline, d.disciplineSource, p.revitVersion, p.aceVersion, p.workshared, p.isCentral, p.centralPath, p.worksets, p.links?.count, p.links?.notLoaded,
      p.phases, p.designOptions?.sets, p.groups?.instances, p.blanksInGroups, p.blanksInDesignOptions, p.blanksBorrowed,
      Object.entries(p.foreign ?? {}).map(([k, v]) => `${k}: ${v}`).join("; ")];
  });
  return xlsxBuffer([
    { name: "Summary", head: ["discipline", "model", "rows", ...SUMMARY_STATUSES], rows: summary.map((l) => [l.discipline, l.model, l.rows, ...SUMMARY_STATUSES.map((s) => l[s])]) },
    { name: "Rows", head: ROW_HEAD, rows: rowLines },
    { name: "Actions", head: ["rank", "id", "what", "why", "models", "elements", "owner", "type", "blockers", "how to clear"],
      rows: actions.map((a) => [a.rank, a.id, a.what, a.why, a.impact.models ?? [], a.impact.elements ?? a.impact.rows, a.owner, a.type, a.blockers.map((b) => b.what).join("; "), a.blockers.map((b) => b.how).join("; ")]) },
    { name: "Pre-flight", head: ["model", "discipline", "discipline from", "Revit", "ACE", "workshared", "central", "central path", "worksets", "links", "links not loaded", "phases", "design option sets", "group instances", "blanks in groups", "blanks in design options", "blanks borrowed", "foreign elements (not judged)"], rows: pf },
  ]);
}

// ---- stage 3: ranked actions (read-only) ---------------------------------------------------------------------------

export const TYPES = { auto: "auto-fixable", decision: "needs a decision", party: "needs another party" };

/** Plain-language blockers of a model's fill, from its pre-flight. */
function modelBlockers(d) {
  const p = d.preflight ?? {}, out = [];
  if (p.blanksBorrowed > 0) out.push({ what: `${p.blanksBorrowed} element(s) with blanks in ${d.model} are borrowed by other users`, how: "ask them to synchronise and relinquish, then re-run the check" });
  if (p.blanksInGroups > 0) out.push({ what: `${p.blanksInGroups} element(s) with blanks in ${d.model} are in groups`, how: "ACE skips them: fill them in the group editor (one instance fixes all), then re-run" });
  if (p.blanksInDesignOptions > 0) out.push({ what: `${p.blanksInDesignOptions} element(s) with blanks in ${d.model} are in design options`, how: "decide whether options count for the submission; ACE skips them" });
  if (p.workshared && p.isCentral === true) out.push({ what: `${d.model} is open as the central model`, how: "work in a local copy before any fill" });
  return out;
}

/**
 * Findings -> ranked actions { rank, id, what, why, impact, owner, type, blockers, run?, ready? }. Order: submission
 * blockers (not bound / GUID differs on rows the client's checker tests), other binding and GUID problems, blanks by
 * element count, decisions, then defects in the client's files. Every action has a stable id; an auto-fixable one has
 * `run` (what the execution stage, parameter_fill, does for it) and `ready` (false with a blocker while an approval or a
 * file it needs is missing). exec: { value, valueWhy, deriveApproved, deriveStatus, projectFile }.
 */
export function buildActions({ result, rows = [], unmappedRows = [], defects = {}, review = null, deriveSet = null, exec = null } = {}) {
  const byKey = new Map(rows.map((r) => [r.key, r]));
  const docs = result?.documents ?? [];
  const docBlockers = new Map(docs.map((d) => [d.model, modelBlockers(d)]));
  const acts = [];
  const add = (tier, weight, a) => acts.push({ tier, weight, blockers: [], ...a });
  // Runnable now? The approvals and files the execution stage needs, as blockers.
  const readiness = (kind) => {
    if (!exec) return [];
    if (kind === "fill" && !exec.value) return [{ what: "no approved fill value", how: exec.valueWhy ?? "approve the NA policy in the project mapping" }];
    if (kind === "derive" && !exec.deriveApproved) return [{ what: `the mapping's derive rules are not approved (${exec.deriveStatus ?? "draft"})`, how: "a person approves them in the mapping (status: approved)" }];
    if (kind === "bind" && !exec.projectFile) return [{ what: "no project shared parameter file given", how: "give project_parameters (or sources.projectSharedParameters in the mapping)" }];
    return [];
  };
  const auto = (kind, run, blockers = []) => {
    const r = readiness(kind);
    return { type: TYPES.auto, run: { kind, ...run }, ready: !r.length, blockers: [...blockers, ...r] };
  };

  // Per requirement row and problem, across models.
  const groups = new Map();
  for (const d of docs) for (const x of d.rows ?? []) {
    const r = byKey.get(x.key) ?? { ...x, sources: [] };
    const kinds = [];
    const builtIn = isBuiltIn(r, x);
    if (x.status === "not-available" || (builtIn && x.notAvailableCategories?.length)) kinds.push("not-available");
    else if (!builtIn && ["not-bound", "guid-differs", "wrong-guid", "partly-bound"].includes(x.status)) kinds.push(x.status === "wrong-guid" ? "guid-differs" : x.status);
    if (x.wrongLevel && !builtIn) kinds.push("wrong-level");
    if (x.blank > 0 && !["not-bound", "out-of-scope", "unmapped"].includes(x.status)) kinds.push(x.isText === false ? "blank-nontext" : "blank");
    for (const k of kinds) {
      // Binding problems are one job per model and binding target: group them (model x group x level x categories),
      // apart for the rows ACE can bind (a definition in a file) and those it cannot.
      const perModel = k === "not-bound";
      const why = k === "not-bound" ? bindProblem(r, x) : null;
      const id = perModel ? `${k}\0${d.model}\0${r.group}\0${r.level}\0${(r.categories ?? []).join("|")}\0${why ? "x" : "ok"}`
        : k === "guid-differs" ? `${k}\0${d.model}\0${x.key ?? x.name}` : `${k}\0${x.key ?? x.name}`;
      if (!groups.has(id)) groups.set(id, { kind: k, row: r, x, models: [], disciplines: [], elements: 0, unbound: [], notAvailable: [], names: [], keys: [], why: new Set(), tested: 0, sources: [] });
      const g = groups.get(id);
      if (why) g.why.add(why);
      if (!g.keys.includes(x.key)) g.keys.push(x.key);
      if (perModel && !g.names.includes(r.name)) { g.names.push(r.name); if (fromChecker(r)) g.tested++; g.sources.push(...(r.sources ?? [])); }
      if (!g.models.includes(d.model)) { g.models.push(d.model); if (d.discipline) g.disciplines.push(d.discipline); }
      g.elements += k.startsWith("blank") ? x.blank ?? 0 : k === "not-available" ? x.missing ?? 0 : x.checked ?? 0;
      g.unbound.push(...(x.unboundCategories ?? []));
      g.notAvailable.push(...(x.notAvailableCategories ?? []));
    }
  }
  for (const g of groups.values()) {
    const { row: r, x } = g;
    const where = r.categories?.length ? r.categories.join(", ") : x.group;
    const owner = uniq(g.disciplines).join(", ") || "model owner";
    const impact = { models: g.models, elements: g.elements };
    const why = sourcesText(r) || "listed requirement";
    const rowId = [r.name, r.group, r.level, r.categories ?? []];
    if (g.kind === "guid-differs") {
      // Owner decision DEC-0004: the client sometimes updates outside the files, so a differing GUID is not "wrong".
      const model = g.models[0], file = r.defFile ?? "the shared parameter file";
      const problem = r.guids?.length > 1 ? `the client file defines ${r.name} ${r.guids.length} times with different GUIDs: ACE cannot choose one` : !r.guid ? "no definition in the shared parameter files" : null;
      const rebindId = actionId("rebind", model, r.name, r.group, r.categories ?? []);
      add(1, (fromChecker(r) ? 1e6 : 0) + g.elements, { id: actionId("guid", model, ...rowId), what: `${r.name}: the model's GUID differs from ${file}; confirm which is current`, why, impact, owner, type: TYPES.decision,
        options: [`rebind the model to the file (explicit rebind action ${rebindId})`, "update the vault file from the model (a person edits the file)"],
        blockers: [{ what: `the model has ${x.modelGuid ?? "another GUID"}, ${file} has ${r.guid ?? "?"}`, how: `either rebind the model to the file (parameter_fill actions: ["${rebindId}"]) or update the vault file from the model` }] });
      const ready = auto("bind", { keys: g.keys, models: [model] });
      add(3, g.elements, { id: rebindId, what: `Replace the definition of ${r.name} in ${model} (old GUID ${x.modelGuid ?? "?"} -> ${file} GUID ${r.guid ?? "?"}), keeping values`, why, impact, owner,
        ...ready, run: { kind: "rebind", keys: g.keys, models: [model], oldGuid: x.modelGuid ?? null }, type: TYPES.decision, explicitOnly: true,
        ...(problem ? { ready: false, blockers: [...ready.blockers, { what: problem, how: "the client says which GUID is the standard" }] } : {}) });
      continue;
    }
    if (g.kind === "not-bound") {
      const n = g.names.length, list = g.names.slice(0, 10).join("; ") + (n > 10 ? `; ... ${n - 10} more` : "");
      const kind = r.level === "type" ? "type" : r.level === "instance" ? "instance" : "instance or type";
      const tested = g.tested ? ` - the client's checker tests ${g.tested} of them` : "";
      const gwhy = uniq(g.sources.map(srcText)).slice(0, 10).join("; ") + tested;
      const gimpact = { models: g.models, elements: g.elements, rows: n };
      // Weight: the checker-tested parameters it holds first, then its size.
      if (!g.why.size)
        add(g.tested ? 0 : 1, g.tested * 1e6 + n, { id: actionId("bind", g.models[0], r.group, r.level, r.categories ?? [], g.names), what: `Bind ${n} parameter(s) to ${where} as ${kind} parameters in ${g.models[0]}: ${list}`, why: gwhy, impact: gimpact, owner,
          ...auto("bind", { keys: g.keys, models: [g.models[0]] }) });
      else
        add(g.tested ? 0 : 1, g.tested * 1e6 + n, { id: actionId("unbound", g.models[0], r.group, r.level, r.categories ?? [], g.names), what: `Bind ${n} parameter(s) to ${where} as ${kind} parameters in ${g.models[0]}: ${list}`, why: gwhy, impact: gimpact, owner, type: TYPES.decision,
          blockers: [...g.why].map((w) => ({ what: `ACE cannot bind them: ${w}`, how: "resolve it, then re-run the check; ACE binds only definitions from the shared parameter files" })) });
    }
    else if (g.kind === "partly-bound") {
      const problem = bindProblem(r, x);
      const what = `Extend the binding of ${r.name} to ${uniq(g.unbound).join(", ") || "the missing categories"}`;
      if (!problem) add(1, g.elements, { id: actionId("bind", r.name, r.group, r.level, r.categories ?? []), what, why, impact, owner, ...auto("bind", { keys: g.keys, models: g.models }) });
      else add(1, g.elements, { id: actionId("extend", ...rowId), what, why, impact, owner, type: TYPES.decision, blockers: [{ what: `ACE cannot extend it: ${problem}`, how: "resolve it, then re-run the check" }] });
    }
    else if (g.kind === "not-available")
      add(3, g.elements, { id: actionId("builtin", ...rowId), what: `${r.name} is a built-in parameter, not available on categories ${uniq(g.notAvailable).join(", ") || "of some elements"} (${g.elements} element(s)): exclude those categories from the requirement or ask the client`,
        why, impact, owner: "BIM manager", type: TYPES.decision, blockers: [{ what: "a built-in parameter cannot be bound to other categories", how: "narrow the requirement's categories in the mapping, or agree the requirement with the client" }] });
    else if (g.kind === "wrong-level")
      add(3, g.elements, { id: actionId("level", ...rowId), what: `${r.name} is bound as the wrong kind (required ${r.level})`, why, impact, owner, type: TYPES.decision });
    else if (g.kind === "blank") {
      const a = auto("fill", { keys: g.keys, models: g.models }, uniq(g.models).flatMap((m) => docBlockers.get(m) ?? []));
      if (r.draftMapping) { a.ready = false; a.blockers.push({ what: `plan only: ${r.planOnly ?? "its category mapping is not approved"}`, how: "a person approves that part of the mapping, then re-run" }); }
      add(2, g.elements, { id: actionId("fill", ...rowId), what: `Fill ${g.elements} blank value(s) of ${r.name} (${where}): real values, or "NA" where it does not apply`, why, impact, owner, ...a });
    }
    else if (g.kind === "blank-nontext")
      add(3, g.elements, { id: actionId("nontext", ...rowId), what: `${g.elements} blank value(s) of ${r.name} are not text, so "NA" cannot be written`, why, impact, owner, type: TYPES.decision,
        blockers: [{ what: "a number, length or yes/no parameter cannot hold NA", how: "agree a policy with the client (leave blank, 0, or a text twin) before any fill" }] });
  }
  // Derive rules from the mapping. Twins are found generically (same attribute after a differing prefix); only a pair
  // the mapping lists is derived, and only into the blank side.
  const groupsDerive = deriveSet?.groups ?? [];
  const twinGroup = (a, b) => groupsDerive.find((g) => g.twin && g.twin.includes(a) && g.twin.includes(b));
  for (const t of defects.twins ?? []) {
    const g = twinGroup(t.a, t.b);
    const oneBlank = !t.valueA || !t.valueB;
    const base = { what: `Make ${t.a} and ${t.b} agree in ${t.model} ("${t.valueA}" vs "${t.valueB}")`, why: "Project Information twins", impact: { models: [t.model], elements: 1 }, owner: "model owner" };
    if (g && oneBlank) add(2, 1, { id: g.id, ...base, ...auto("derive", { rule: g.id, models: [t.model] }) });
    else add(3, 1, { id: actionId("twins", t.model, t.a, t.b), ...base, type: TYPES.decision,
      blockers: [g ? { what: "both are filled with different values; ACE never overwrites a value", how: "confirm the right value and correct the other one" }
        : { what: "the mapping does not say which one is derived from the other", how: "confirm the right value and list the pair in the mapping's derive section" }] });
  }
  for (const g of groupsDerive) {
    if (g.twin) continue;
    const models = docs.filter((d) => groupApplies(g, groupsDerive, d.model) && (d.rows ?? []).some((x) => g.targets.includes(x.name) && x.blank > 0)).map((d) => d.model);
    if (!models.length) continue;
    add(2, models.length, { id: g.id, what: `Derive ${g.text}`, why: "the mapping's derive rules", impact: { models, elements: models.length }, owner: "model owner", ...auto("derive", { rule: g.id, models }) });
  }
  for (const p of deriveSet?.problems ?? [])
    add(3, 1, { id: p.id, what: `Derive rule for ${p.target} is not machine-readable: ${p.text}`, why: "the mapping's derive rules", impact: { rows: 1 }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "rule not machine-readable", how: 'write it as { "target": "A", "copyFrom": "B" } or { "target": "A", "join": ["B", "C"], "separator": "-" } (or "concat with \'-\': B, C")' }] });
  for (const u of defects.unknownCategories ?? [])
    add(1, u.names.length, { id: actionId("category", u.category), what: `Fix the Revit category "${u.category}": the model does not know it (typo in the mapping?)`, why: `rows: ${u.names.slice(0, 5).join("; ")}${u.names.length > 5 ? "; ..." : ""}`, impact: { models: u.models, rows: u.names.length }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "those rows check nothing in that category", how: "correct the OST_ name in the mapping file, then re-run" }] });
  for (const g of defects.guidDiffers ?? [])
    add(1, Object.keys(g.models).length, { id: actionId("guids", g.name), what: `Use one GUID for ${g.name} in every model`, why: Object.entries(g.models).map(([m, v]) => `${m}: ${v}`).join("; "), impact: { models: Object.keys(g.models) }, owner: "BIM manager", type: TYPES.decision });

  // Decisions about the mapping and names (BIM manager), then defects in the client's files (client).
  for (const sheet of defects.sheetsWithoutDisciplines ?? [])
    add(3, 1, { id: actionId("scope", sheet), what: `LOIN sheet ${sheet} has no discipline scope: its rows are judged in every model; set disciplines in the mapping`, why: "mapping disciplines", impact: { rows: 0 }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "other disciplines' models get its binding and fill actions", how: `write "disciplines": { "${sheet}": ["<code>"] } into the mapping, then re-run` }] });
  for (const u of defects.unmappedCategories ?? [])
    add(3, u.rows, { id: actionId("map", u.category), what: `Map client category "${u.category}" to Revit categories in the mapping file`, why: `LOIN ${u.sheets.join(", ")}`, impact: { rows: u.rows }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: `${u.rows} requirement row(s) are not checked until it is mapped`, how: "write the Revit categories (OST_ names) into the mapping file, then re-run" }] });
  for (const c of defects.levelConflicts ?? [])
    add(3, 1, { id: actionId("kind", c.name, c.group), what: `Decide whether ${c.name} is a type or an instance parameter (sources disagree: ${c.kept} / ${c.other})`, why: c.source, impact: { rows: 1 }, owner: "BIM manager", type: TYPES.decision });
  for (const c of defects.guidConflicts ?? [])
    add(3, 1, { id: actionId("conflict", c.name), what: `${c.name} has different GUIDs in the shared parameter files`, why: Object.entries(c.guids).map(([g, f]) => `${f}: ${g}`).join("; "), impact: { rows: 1 }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "a model bound with the other GUID fails the client's checks", how: "agree with the client which GUID is the standard; the project file must use it" }] });
  for (const c of defects.duplicateGuids ?? [])
    add(4, c.guids.length, { id: actionId("dupguid", c.file, c.name), what: `The client file ${c.file} defines ${c.name} ${c.guids.length} times with different GUIDs`, why: c.guids.join("; "), impact: { rows: 1 }, owner: "client", type: TYPES.party,
      blockers: [{ what: "ACE accepts any of them in a check, but cannot choose one to bind", how: "ask the client whether one GUID per category is intended, or which one is the standard" }] });
  const drift = new Map();
  for (const c of defects.nameCandidates ?? []) {
    if (c.match === "spelling") {
      add(4, 1, { id: actionId("name", c.name), what: `Resolve the name "${c.name}": the shared parameter file has "${c.candidate}" (spelling)`, why: "name in the requirement list", impact: { rows: 1 }, owner: "client", type: TYPES.decision,
        blockers: [{ what: "checked under the listed name until resolved", how: "confirm, add an alias in the mapping file, and ask the client to correct the list" }] });
      continue;
    }
    const k = `${/^[A-Za-z]+_([A-Za-z0-9&]+)_/.exec(c.name)?.[1]}\0${/^[A-Za-z]+_([A-Za-z0-9&]+)_/.exec(c.candidate ?? "")?.[1]}`;
    if (!drift.has(k)) drift.set(k, []);
    drift.get(k).push(c);
  }
  for (const [k, list] of drift) {
    const [from, to] = k.split("\0");
    add(4, list.length, { id: actionId("prefix", from, to), what: `Resolve ${list.length} name(s) listed with the prefix code ${from} where the shared parameter files use ${to} (e.g. "${list[0].name}" -> "${list[0].candidate}")`, why: "prefix drift between the requirement list and the shared parameter files",
      impact: { rows: list.length }, owner: "client", type: TYPES.decision, blockers: [{ what: "checked under the listed names until resolved", how: "confirm with the client, then add aliases in the mapping file (or the client fixes the list)" }] });
  }
  if (defects.unknownNames?.length)
    add(4, defects.unknownNames.length, { id: actionId("unknown", defects.unknownNames.length), what: `${defects.unknownNames.length} listed name(s) are in no shared parameter file`, why: defects.unknownNames.slice(0, 10).join("; ") + (defects.unknownNames.length > 10 ? "; ..." : ""), impact: { rows: defects.unknownNames.length }, owner: "client", type: TYPES.party,
      blockers: [{ what: "built-in Revit names are fine; the others have no definition to bind", how: "ask the client for the missing definitions" }] });
  for (const [a, b] of defects.nearDuplicates ?? [])
    add(4, 1, { id: actionId("typo", a, b), what: `Typo twins in the shared parameter files: "${a}" / "${b}"`, why: "near-duplicate names", impact: { rows: 1 }, owner: "client", type: TYPES.party, blockers: [{ what: "both names may be required in different places", how: "ask the client which one is meant and to retire the other" }] });
  if (defects.pseudoNames?.length)
    add(4, defects.pseudoNames.length, { id: actionId("notes", defects.pseudoNames.length), what: `${defects.pseudoNames.length} list entr(ies) are not parameter names (notes in brackets, or cells with several names to check)`, why: defects.pseudoNames.slice(0, 5).map((p) => `${p.loin ?? p.name}${p.reason ? ` (${p.reason})` : ""}`).join("; "), impact: { rows: defects.pseudoNames.length }, owner: "client", type: TYPES.party });
  for (const c of (defects.splitNames ?? []).filter((x) => x.definedAsOne))
    add(4, c.names.length, { id: actionId("split", c.cell), what: `The LOIN cell "${c.cell}" lists ${c.names.length} names; ACE checks them one by one, but ${c.definedAsOne} also defines the whole cell as one parameter`, why: `LOIN ${c.loin}`, impact: { rows: c.names.length }, owner: "client", type: TYPES.party,
      blockers: [{ what: "the combined definition is likely an artefact", how: "ask the client which is meant and to retire the combined name" }] });
  for (const s of review?.scopeDefects ?? [])
    add(4, 1, { id: actionId("checker", s.check, s.param), what: `The client's checker tests ${s.param} only on Project Information${s.parked ? " (a parked check: it runs once moved out of Unused / Unplaced)" : ""}`, why: `checker "${s.check}": ${s.reason}`, impact: { rows: 1 }, owner: "client", type: TYPES.party,
      blockers: [{ what: "the checker will pass or fail it on the wrong element", how: "report it to the client; ACE checks the requirement where it belongs" }] });

  acts.sort((a, b) => a.tier - b.tier || b.weight - a.weight);
  // Ids stay unique when two findings hash alike (e.g. the same row in two groups).
  const seen = new Map();
  return acts.map(({ tier, weight, ...a }, i) => {
    const n = seen.get(a.id) ?? 0; seen.set(a.id, n + 1);
    return { rank: i + 1, ...a, id: n ? `${a.id}.${n + 1}` : a.id };
  });
}

const impactText = (i) => [i.models?.length ? `${uniq(i.models).length} model(s)` : "", i.elements != null ? `${i.elements} element(s)` : "", i.rows != null ? `${i.rows} row(s)` : ""].filter(Boolean).join(", ");

/** One line per action (the reply shows the first ones). */
export const actionLine = (a) => `${a.rank}. [${a.id}; ${a.type}${a.explicitOnly ? ", only when picked" : ""}${a.run && a.ready === false ? ", not ready" : ""}; ${a.owner}] ${a.what}${a.options ? ` (options: ${a.options.join(" | ")})` : ""} (${impactText(a.impact)})${a.blockers.length ? ` - blocker: ${a.blockers.map((b) => `${b.what}: ${b.how}`).join("; ")}` : ""}`;

export function actionsMarkdown(actions, title = "Parameter requirements: actions") {
  const out = [`# ${title}`, "", `${localDate(new Date(), { time: true })}. Read-only list; nothing has been changed. Order: submission blockers, then blanks by element count, then decisions, then defects in the client's files.`, ""];
  for (const a of actions) {
    out.push(`## ${a.rank}. ${a.what}`, "", `- Id: ${a.id}${a.run ? ` (parameter_fill can run it${a.ready === false ? " once its blockers are cleared" : ""}: actions: ["${a.id}"])` : ""}`, `- Why: ${a.why}`, `- Impact: ${impactText(a.impact)}${a.impact.models?.length ? ` (${uniq(a.impact.models).join(", ")})` : ""}`, `- Owner: ${a.owner}`, `- Type: ${a.type}`);
    for (const o of a.options ?? []) out.push(`- Option: ${o}`);
    for (const b of a.blockers) out.push(`- Blocker: ${b.what}. How to clear: ${b.how}.`);
    out.push("");
  }
  return out.join("\n");
}

/** Markdown table of the summary (for the .md report). */
export function summaryMarkdown(result, unmapped) {
  const lines = summaryTable(result, unmapped);
  if (!lines.length) return "";
  const head = ["discipline", "model", "rows", ...SUMMARY_STATUSES];
  return [`| ${head.join(" | ")} |`, `|${head.map(() => "---").join("|")}|`, ...lines.map((l) => `| ${head.map((h) => l[h] ?? "").join(" | ")} |`)].join("\n");
}
