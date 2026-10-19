// Stage 2 and 3 of the parameter requirements workflow (check > reports > actions > execution): the federated summary
// workbook (P9) and the ranked, read-only action list built from a check's findings. Pure functions; the xlsx is
// written with docx.js's zip (no dependency).
import { zip } from "./docx.js";
import { localDate } from "./core.js";
import { esc } from "./vault.js";
import { srcText, uniq } from "./paramsources.js";
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

export const SUMMARY_STATUSES = ["complete", "complete-with-na", "blanks", "partly-bound", "not-bound", "wrong-guid", "not text", "out-of-scope", "unmapped", "no-elements"];

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
      if (r.status && Object.hasOwn(c, r.status)) c[r.status]++;
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
    { name: "Actions", head: ["rank", "what", "why", "models", "elements", "owner", "type", "blockers", "how to clear"],
      rows: actions.map((a) => [a.rank, a.what, a.why, a.impact.models ?? [], a.impact.elements ?? a.impact.rows, a.owner, a.type, a.blockers.map((b) => b.what).join("; "), a.blockers.map((b) => b.how).join("; ")]) },
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
 * Findings -> ranked actions { rank, what, why, impact, owner, type, blockers }. Order: submission blockers (not bound /
 * wrong GUID on rows the client's checker tests), other binding and GUID problems, blanks by element count, decisions,
 * then defects in the client's files.
 */
export function buildActions({ result, rows = [], unmappedRows = [], defects = {}, review = null, derive = null } = {}) {
  const derived = (a, b) => { const t = JSON.stringify(derive ?? ""); return t.includes(JSON.stringify(a)) && t.includes(JSON.stringify(b)); };
  const byKey = new Map(rows.map((r) => [r.key, r]));
  const docs = result?.documents ?? [];
  const docBlockers = new Map(docs.map((d) => [d.model, modelBlockers(d)]));
  const acts = [];
  const add = (tier, weight, a) => acts.push({ tier, weight, blockers: [], ...a });

  // Per requirement row and problem, across models.
  const groups = new Map();
  for (const d of docs) for (const x of d.rows ?? []) {
    const r = byKey.get(x.key) ?? { ...x, sources: [] };
    const kinds = [];
    if (["not-bound", "wrong-guid", "partly-bound"].includes(x.status)) kinds.push(x.status);
    if (x.wrongLevel) kinds.push("wrong-level");
    if (x.blank > 0 && !["not-bound", "out-of-scope", "unmapped"].includes(x.status)) kinds.push(x.isText === false ? "blank-nontext" : "blank");
    for (const k of kinds) {
      // Binding problems are one job per model and binding target: group them (model x group x level x categories).
      const perModel = k === "not-bound" || k === "wrong-guid";
      const id = perModel ? `${k}\0${d.model}\0${r.group}\0${r.level}\0${(r.categories ?? []).join("|")}` : `${k}\0${x.key ?? x.name}`;
      if (!groups.has(id)) groups.set(id, { kind: k, row: r, x, models: [], disciplines: [], elements: 0, unbound: [], names: [], tested: 0, sources: [] });
      const g = groups.get(id);
      if (perModel && !g.names.includes(r.name)) { g.names.push(r.name); if (fromChecker(r)) g.tested++; g.sources.push(...(r.sources ?? [])); }
      if (!g.models.includes(d.model)) { g.models.push(d.model); if (d.discipline) g.disciplines.push(d.discipline); }
      g.elements += k.startsWith("blank") ? x.blank ?? 0 : x.checked ?? 0;
      g.unbound.push(...(x.unboundCategories ?? []));
    }
  }
  for (const g of groups.values()) {
    const { row: r, x } = g;
    const where = r.categories?.length ? r.categories.join(", ") : x.group;
    const owner = uniq(g.disciplines).join(", ") || "model owner";
    const impact = { models: g.models, elements: g.elements };
    const why = sourcesText(r) || "listed requirement";
    if (g.kind === "not-bound" || g.kind === "wrong-guid") {
      const n = g.names.length, list = g.names.slice(0, 10).join("; ") + (n > 10 ? `; ... ${n - 10} more` : "");
      const kind = r.level === "type" ? "type" : r.level === "instance" ? "instance" : "instance or type";
      const tested = g.tested ? ` - the client's checker tests ${g.tested} of them` : "";
      const gwhy = uniq(g.sources.map(srcText)).slice(0, 10).join("; ") + tested;
      const gimpact = { models: g.models, elements: g.elements, rows: n };
      // Weight: the checker-tested parameters it holds first, then its size.
      if (g.kind === "not-bound")
        add(g.tested ? 0 : 1, g.tested * 1e6 + n, { what: `Bind ${n} parameter(s) to ${where} as ${kind} parameters in ${g.models[0]}: ${list}`, why: gwhy, impact: gimpact, owner, type: TYPES.decision,
          blockers: [{ what: "these parameters are not bound in the model", how: "load them from the project shared parameter file and bind them (ACE's binding plan follows in 1.11)" }] });
      else
        add(g.tested ? 0 : 1, g.tested * 1e6 + n, { what: `Replace ${n} parameter definition(s) with the wrong GUID (${where}) in ${g.models[0]}: ${list}`, why: gwhy, impact: gimpact, owner, type: TYPES.decision,
          blockers: [{ what: "a wrong GUID fails exports and the client's checks even when the value is filled", how: "decide which definition is right, then rebind with the project file's GUID and move the values" }] });
    }
    else if (g.kind === "partly-bound")
      add(1, g.elements, { what: `Extend the binding of ${r.name} to ${uniq(g.unbound).join(", ") || "the missing categories"}`, why, impact, owner, type: TYPES.decision });
    else if (g.kind === "wrong-level")
      add(3, g.elements, { what: `${r.name} is bound as the wrong kind (required ${r.level})`, why, impact, owner, type: TYPES.decision });
    else if (g.kind === "blank")
      add(2, g.elements, { what: `Fill ${g.elements} blank value(s) of ${r.name} (${where}): real values, or "NA" where it does not apply`, why, impact, owner, type: TYPES.auto,
        blockers: uniq(g.models).flatMap((m) => docBlockers.get(m) ?? []) });
    else if (g.kind === "blank-nontext")
      add(3, g.elements, { what: `${g.elements} blank value(s) of ${r.name} are not text, so "NA" cannot be written`, why, impact, owner, type: TYPES.decision,
        blockers: [{ what: "a number, length or yes/no parameter cannot hold NA", how: "agree a policy with the client (leave blank, 0, or a text twin) before any fill" }] });
  }
  // Twins are found generically (same attribute after a differing prefix); only a pair the mapping's derive lists is auto-fixable.
  for (const t of defects.twins ?? [])
    add(derived(t.a, t.b) ? 2 : 3, 1, { what: `Make ${t.a} and ${t.b} agree in ${t.model} ("${t.valueA}" vs "${t.valueB}")`, why: "Project Information twins", impact: { models: [t.model], elements: 1 }, owner: "model owner",
      type: derived(t.a, t.b) ? TYPES.auto : TYPES.decision,
      blockers: derived(t.a, t.b) ? [] : [{ what: "the mapping does not say which one is derived from the other", how: "confirm the right value and list the pair in the mapping's derive section" }] });
  for (const u of defects.unknownCategories ?? [])
    add(1, u.names.length, { what: `Fix the Revit category "${u.category}": the model does not know it (typo in the mapping?)`, why: `rows: ${u.names.slice(0, 5).join("; ")}${u.names.length > 5 ? "; ..." : ""}`, impact: { models: u.models, rows: u.names.length }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "those rows check nothing in that category", how: "correct the OST_ name in the mapping file, then re-run" }] });
  for (const g of defects.guidDiffers ?? [])
    add(1, Object.keys(g.models).length, { what: `Use one GUID for ${g.name} in every model`, why: Object.entries(g.models).map(([m, v]) => `${m}: ${v}`).join("; "), impact: { models: Object.keys(g.models) }, owner: "BIM manager", type: TYPES.decision });

  // Decisions about the mapping and names (BIM manager), then defects in the client's files (client).
  for (const u of defects.unmappedCategories ?? [])
    add(3, u.rows, { what: `Map client category "${u.category}" to Revit categories in the mapping file`, why: `LOIN ${u.sheets.join(", ")}`, impact: { rows: u.rows }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: `${u.rows} requirement row(s) are not checked until it is mapped`, how: "write the Revit categories (OST_ names) into the mapping file, then re-run" }] });
  for (const c of defects.levelConflicts ?? [])
    add(3, 1, { what: `Decide whether ${c.name} is a type or an instance parameter (sources disagree: ${c.kept} / ${c.other})`, why: c.source, impact: { rows: 1 }, owner: "BIM manager", type: TYPES.decision });
  for (const c of defects.guidConflicts ?? [])
    add(3, 1, { what: `${c.name} has different GUIDs in the shared parameter files`, why: Object.entries(c.guids).map(([g, f]) => `${f}: ${g}`).join("; "), impact: { rows: 1 }, owner: "BIM manager", type: TYPES.decision,
      blockers: [{ what: "a model bound with the other GUID fails the client's checks", how: "agree with the client which GUID is the standard; the project file must use it" }] });
  const drift = new Map();
  for (const c of defects.nameCandidates ?? []) {
    if (c.match === "spelling") {
      add(4, 1, { what: `Resolve the name "${c.name}": the shared parameter file has "${c.candidate}" (spelling)`, why: "name in the requirement list", impact: { rows: 1 }, owner: "client", type: TYPES.decision,
        blockers: [{ what: "checked under the listed name until resolved", how: "confirm, add an alias in the mapping file, and ask the client to correct the list" }] });
      continue;
    }
    const k = `${/^[A-Za-z]+_([A-Za-z0-9&]+)_/.exec(c.name)?.[1]}\0${/^[A-Za-z]+_([A-Za-z0-9&]+)_/.exec(c.candidate ?? "")?.[1]}`;
    if (!drift.has(k)) drift.set(k, []);
    drift.get(k).push(c);
  }
  for (const [k, list] of drift) {
    const [from, to] = k.split("\0");
    add(4, list.length, { what: `Resolve ${list.length} name(s) listed with the prefix code ${from} where the shared parameter files use ${to} (e.g. "${list[0].name}" -> "${list[0].candidate}")`, why: "prefix drift between the requirement list and the shared parameter files",
      impact: { rows: list.length }, owner: "client", type: TYPES.decision, blockers: [{ what: "checked under the listed names until resolved", how: "confirm with the client, then add aliases in the mapping file (or the client fixes the list)" }] });
  }
  if (defects.unknownNames?.length)
    add(4, defects.unknownNames.length, { what: `${defects.unknownNames.length} listed name(s) are in no shared parameter file`, why: defects.unknownNames.slice(0, 10).join("; ") + (defects.unknownNames.length > 10 ? "; ..." : ""), impact: { rows: defects.unknownNames.length }, owner: "client", type: TYPES.party,
      blockers: [{ what: "built-in Revit names are fine; the others have no definition to bind", how: "ask the client for the missing definitions" }] });
  for (const [a, b] of defects.nearDuplicates ?? [])
    add(4, 1, { what: `Typo twins in the shared parameter files: "${a}" / "${b}"`, why: "near-duplicate names", impact: { rows: 1 }, owner: "client", type: TYPES.party, blockers: [{ what: "both names may be required in different places", how: "ask the client which one is meant and to retire the other" }] });
  if (defects.pseudoNames?.length)
    add(4, defects.pseudoNames.length, { what: `${defects.pseudoNames.length} list entr(ies) are notes, not parameter names (brackets)`, why: defects.pseudoNames.slice(0, 5).map((p) => p.loin ?? p.name).join("; "), impact: { rows: defects.pseudoNames.length }, owner: "client", type: TYPES.party });
  for (const s of review?.scopeDefects ?? [])
    add(4, 1, { what: `The client's checker tests ${s.param} only on Project Information`, why: `checker "${s.check}": ${s.reason}`, impact: { rows: 1 }, owner: "client", type: TYPES.party,
      blockers: [{ what: "the checker will pass or fail it on the wrong element", how: "report it to the client; ACE checks the requirement where it belongs" }] });

  acts.sort((a, b) => a.tier - b.tier || b.weight - a.weight);
  return acts.map(({ tier, weight, ...a }, i) => ({ rank: i + 1, ...a }));
}

const impactText = (i) => [i.models?.length ? `${uniq(i.models).length} model(s)` : "", i.elements != null ? `${i.elements} element(s)` : "", i.rows != null ? `${i.rows} row(s)` : ""].filter(Boolean).join(", ");

/** One line per action (the reply shows the first ones). */
export const actionLine = (a) => `${a.rank}. [${a.type}; ${a.owner}] ${a.what} (${impactText(a.impact)})${a.blockers.length ? ` - blocker: ${a.blockers.map((b) => `${b.what}: ${b.how}`).join("; ")}` : ""}`;

export function actionsMarkdown(actions, title = "Parameter requirements: actions") {
  const out = [`# ${title}`, "", `${localDate(new Date(), { time: true })}. Read-only list; nothing has been changed. Order: submission blockers, then blanks by element count, then decisions, then defects in the client's files.`, ""];
  for (const a of actions) {
    out.push(`## ${a.rank}. ${a.what}`, "", `- Why: ${a.why}`, `- Impact: ${impactText(a.impact)}${a.impact.models?.length ? ` (${uniq(a.impact.models).join(", ")})` : ""}`, `- Owner: ${a.owner}`, `- Type: ${a.type}`);
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
