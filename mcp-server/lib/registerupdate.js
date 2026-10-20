// register_update: a deliverables register (MIDP and the like, xlsx) updated for one submission, without Revit. The
// register sheet is copied into a new workbook under the submission's label (formulas that name the sheet follow the new
// name; formulas into other tabs become their values, the other tabs are dropped), delivered rows get the submission date
// in the stage column, and dates later than the submission are cleared. The original is never written.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import ExcelJS from "exceljs";

const lc = (s) => String(s ?? "").trim().toLowerCase();
const cellText = (v) => v == null ? "" : typeof v === "object" ? (v.richText ? v.richText.map((t) => t.text).join("") : v.text ?? (v.result != null ? String(v.result) : v.formula ? "" : String(v))) : String(v);
export const sha = (buf) => createHash("sha256").update(buf).digest("hex");

/**
 * A header name or a column letter -> column number (1-based). The header text wins ("ID" or "IFC" as a header is that
 * column, not the letter); a letter is read only when no header matches, and only inside the used columns. 0 = none.
 */
export function columnOf(ws, headerRow, key) {
  const k = String(key ?? "").trim();
  const norm = (x) => lc(x).replace(/\s+/g, " ");
  const row = ws.getRow(headerRow);
  let found = 0;
  row.eachCell({ includeEmpty: false }, (c, col) => { if (!found && norm(cellText(c.value)) === norm(k)) found = col; });
  if (found) return found;
  if (/^[A-Z]{1,3}$/.test(k)) {
    const n = k.split("").reduce((x, c) => x * 26 + c.charCodeAt(0) - 64, 0);
    return n >= 1 && n <= ws.columnCount ? n : 0;
  }
  return 0;
}

/** A date from a cell value: Date, ISO / d-m-y text; null when it is not a date (empty and impossible dates included). */
export function asDate(v) {
  if (v instanceof Date && !isNaN(v)) return v;
  if (v && typeof v === "object" && v.result instanceof Date) return v.result;
  if (typeof v !== "string" || !v.trim()) return null;
  const make = (y, m, d) => { const x = new Date(Date.UTC(y, m - 1, d)); return x.getUTCFullYear() === y && x.getUTCMonth() === m - 1 && x.getUTCDate() === d ? x : null; };   // 31/02 -> null
  let m = /^(\d{4})-(\d{1,2})-(\d{1,2})/.exec(v.trim());
  if (m) return make(+m[1], +m[2], +m[3]);
  m = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/.exec(v.trim());   // d/m/yyyy (the office's format)
  if (m) return make(+m[3], +m[2], +m[1]);
  return null;
}
const day = (d) => d.toISOString().slice(0, 10);

/**
 * File names -> the register ids they deliver. An id is matched when it is contained in the file name (case-insensitive;
 * the longest id wins when several are contained), or, with idPattern (the vault naming regex), when the pattern's first
 * group (or whole match) equals the id. -> { matched: Map(id -> [files]), unmatched: [files], ambiguous: [{ file, ids }] }
 */
export function matchDelivered(idsIn, files, idPattern) {
  const ids = [...new Map(idsIn.map((id) => [lc(id), id])).values()];   // an id listed twice is one id here
  const re = idPattern ? new RegExp(idPattern, "i") : null;
  const byLc = new Map(ids.map((id) => [lc(id), id]));
  const matched = new Map(), unmatched = [], ambiguous = [];
  for (const f of files) {
    const name = path.basename(String(f));
    let hits;
    if (re) { const m = re.exec(name); const key = m ? lc(m[1] ?? m[0]) : null; hits = key && byLc.has(key) ? [byLc.get(key)] : []; }
    else {
      hits = ids.filter((id) => lc(name).includes(lc(id)));
      // "X-001" inside "X-0012-A": keep only ids that are not part of a longer matched id.
      hits = hits.filter((h) => !hits.some((o) => o !== h && o.length > h.length && lc(o).includes(lc(h))));
    }
    if (!hits.length) { unmatched.push(name); continue; }
    if (hits.length > 1) ambiguous.push({ file: name, ids: hits });
    for (const id of hits) matched.set(id, [...(matched.get(id) ?? []), name]);
  }
  return { matched, unmatched, ambiguous };
}

/** The delivered list: file names given, and / or the files in a folder (recursively). */
export function deliveredFiles({ delivered = [], delivered_dir } = {}) {
  const out = [...delivered];
  const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) e.isDirectory() ? walk(path.join(d, e.name)) : out.push(e.name); };
  if (delivered_dir) { if (!fs.existsSync(delivered_dir)) throw new Error(`The delivered folder does not exist: ${delivered_dir}`); walk(delivered_dir); }
  return out;
}

const quoteSheet = (n) => /^[A-Za-z_][A-Za-z0-9_.]*$/.test(n) ? n : `'${n.replace(/'/g, "''")}'`;
const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

const SEP = /[-_.]/;
/** A shape: letters A, digits 9, runs collapsed ("TST-ST-001" -> "A-A-9"). */
const shape = (id) => String(id).replace(/[A-Za-z]+/g, "A").replace(/[0-9]+/g, "9");

/**
 * Register rows -> { rows (ids), skipped (headings and other non-ids) }. With id_pattern a row is an id when the pattern
 * finds it; without, when it has a digit and a separator and no space, or the shape most rows share.
 */
export function splitIds(all, idPattern) {
  if (idPattern) { const re = new RegExp(idPattern, "i"); return { rows: all.filter((x) => re.test(x.id)), skipped: all.filter((x) => !re.test(x.id)) }; }
  const counts = new Map();
  for (const x of all) counts.set(shape(x.id), (counts.get(shape(x.id)) ?? 0) + 1);
  const [common, n] = [...counts].sort((a, b) => b[1] - a[1])[0] ?? [null, 0];
  const dominant = n >= 3 && n >= all.length / 2 ? common : null;
  // The field count most rows share ("TST-P1-AR-ZZ-DR-100001": 6): a heading like "000-GENERAL" has fewer.
  const nf = (id) => String(id).split(/[-_.]/).length;
  const fc = new Map();
  for (const x of all) fc.set(nf(x.id), (fc.get(nf(x.id)) ?? 0) + 1);
  const [fields, fn] = [...fc].sort((a, b) => b[1] - a[1])[0] ?? [null, 0];
  const major = fn >= 3 && fn >= all.length / 2 ? fields : null;
  const isId = (id) => ((/\d/.test(id) && SEP.test(id) && !/\s/.test(id)) || shape(id) === dominant)
    && !(major && nf(id) !== major)
    && !(major && /[A-Za-z]{4,}/.test(id) && nf(id) < major);
  return { rows: all.filter((x) => isId(x.id)), skipped: all.filter((x) => !isId(x.id)) };
}

/** Why the id column looks wrong: short ids, many duplicates, files that match several ids. [] = fine. */
export function idColumnDoubts(rows, dupIds, m) {
  const why = [];
  if (!rows.length) return ["no row holds an id"];
  const lens = rows.map((x) => x.id.length).sort((a, b) => a - b);
  const median = lens.length % 2 ? lens[(lens.length - 1) / 2] : (lens[lens.length / 2 - 1] + lens[lens.length / 2]) / 2;
  if (median < 6) why.push(`the ids are short (median ${median} characters)`);
  const dupSet = new Set(dupIds.map(lc));
  const dupRows = rows.filter((x) => dupSet.has(lc(x.id))).length;
  if (dupRows > rows.length * 0.2) why.push(`${dupRows} of ${rows.length} rows share an id with another row`);
  if (m.ambiguous.length) why.push(`${m.ambiguous.length} delivered file(s) match more than one id (e.g. ${m.ambiguous[0].file} -> ${m.ambiguous[0].ids.slice(0, 3).join(", ")})`);
  return why;
}

/**
 * Probably the same document: the id's final number block (3+ digits) is a field of the file name, and aligned from that
 * block leftwards exactly one naming field differs. -> [{ id, file, differs: "ST vs ZZ" }]
 */
export function nearMatches(ids, files) {
  const fields = (s) => String(s).split(/[-_. ]+/).filter(Boolean);
  const out = [];
  for (const file of files) {
    const ff = fields(path.basename(file).replace(/\.[^.]+$/, "")).map(lc);
    for (const id of ids) {
      const idf = fields(id).map(lc);
      const tail = idf.at(-1);
      if (!/^\d{3,}$/.test(tail ?? "")) continue;
      const at = ff.lastIndexOf(tail);
      if (at < 0) continue;
      const n = Math.min(at + 1, idf.length);
      if (n < 2) continue;
      const diff = [];
      for (let k = 1; k < n; k++) if (ff[at - k] !== idf[idf.length - 1 - k]) diff.push(`${ff[at - k].toUpperCase()} vs ${idf[idf.length - 1 - k].toUpperCase()}`);
      if (diff.length === 1) out.push({ id, file: path.basename(file), differs: diff[0] });
    }
  }
  return out;
}

/**
 * The update, on a workbook in memory (the caller decides whether to write it).
 * o: { sheet, header_row, id_column, stage_columns, stage (one or a list), submission_date, label, id_pattern, files,
 *      accept_near, force_id_column }
 * -> { wb, report }
 */
export async function updateRegister(buf, o) {
  const wb = new ExcelJS.Workbook();
  await wb.xlsx.load(buf);
  const ws = o.sheet ? wb.getWorksheet(o.sheet) : wb.worksheets[0];
  if (!ws) throw new Error(`No sheet "${o.sheet}" in the register (sheets: ${wb.worksheets.map((w) => w.name).join(", ")}).`);
  const oldName = ws.name;
  const label = String(o.label ?? "").trim();
  if (!label || /[\\/?*[\]:]/.test(label) || label.length > 31) throw new Error(`The label "${label}" cannot be a sheet name (1-31 characters, none of \\ / ? * [ ] :).`);
  const sub = asDate(o.submission_date);
  if (!sub) throw new Error(`submission_date "${o.submission_date}" is not a date (YYYY-MM-DD).`);
  const hr = o.header_row ?? 1;
  const idCol = columnOf(ws, hr, o.id_column);
  if (!idCol) throw new Error(`The id column "${o.id_column}" is not in header row ${hr} of ${oldName}.`);
  const stageCols = (o.stage_columns ?? []).map((k) => [k, columnOf(ws, hr, k)]);
  const missing = stageCols.filter(([, c]) => !c).map(([k]) => k);
  if (missing.length) throw new Error(`Stage column(s) not in header row ${hr} of ${oldName}: ${missing.join(", ")}.`);
  // stage: one column or a list (e.g. the stage's planned and actual columns): every one gets the submission date.
  const setKeys = [].concat(o.stage ?? (stageCols.length === 1 ? [stageCols[0][0]] : [])).filter((k) => String(k ?? "").trim());
  if (!setKeys.length) throw new Error("Give stage: which of the stage columns get the submission date (one or a list).");
  const setCols = setKeys.map((k) => [k, columnOf(ws, hr, k)]);
  const noSet = setCols.filter(([, c]) => !c).map(([k]) => k);
  if (noSet.length) throw new Error(`Stage column(s) not in header row ${hr} of ${oldName}: ${noSet.join(", ")}.`);
  const header = (c) => cellText(ws.getRow(hr).getCell(c).value) || `column ${c}`;

  // Rows with an id; section headings ("MODELS") and other non-ids are skipped (id_pattern, else the id shape).
  const all = [];
  for (let r = hr + 1; r <= ws.rowCount; r++) { const id = cellText(ws.getRow(r).getCell(idCol).value).trim(); if (id) all.push({ r, id }); }
  const { rows, skipped } = splitIds(all, o.id_pattern);
  const dupIds = rows.map((x) => x.id).filter((id, i, l) => l.findIndex((y) => lc(y) === lc(id)) !== i);
  const m = matchDelivered(rows.map((x) => x.id), o.files ?? [], o.id_pattern);
  if (!o.force_id_column) {
    const why = idColumnDoubts(rows, dupIds, m);
    if (why.length) {
      const sample = [...new Set(rows.map((x) => x.id))].slice(0, 6).join(", ");
      throw new Error(`The id column looks wrong (${header(idCol)}): ${why.join("; ")}. Sample values: ${sample || "none"}. Give the right id_column, or force_id_column: true if it is right.`);
    }
  }
  // Near matches: a delivered file and a register id with the same final number block, one naming field different.
  // Never matched by themselves: "probably the same, confirm"; accept_near: [ids] sets them.
  const uniqueIds = [...new Map(rows.map((x) => [lc(x.id), x.id])).values()];
  const near = nearMatches(uniqueIds.filter((id) => !m.matched.has(id)), m.unmatched);
  const accepted = new Set((o.accept_near ?? []).map(lc));
  for (const n of near) if (accepted.has(lc(n.id))) { m.matched.set(n.id, [...(m.matched.get(n.id) ?? []), n.file]); n.accepted = true; }
  const nearFiles = new Set(near.map((n) => n.file));
  m.unmatched = m.unmatched.filter((f) => !nearFiles.has(f));

  // Formulas: the sheet's own name follows the new label; references into other tabs (dropped) become values.
  const formulas = { renamed: 0, toValues: 0, cells: [] };
  const selfRe = new RegExp(`(^|[^A-Za-z0-9_'])(?:'${esc(oldName.replace(/'/g, "''"))}'|${esc(oldName)})!`, "gi");
  ws.eachRow({ includeEmpty: false }, (row) => row.eachCell({ includeEmpty: false }, (cell) => {
    const v = cell.value;
    if (!v || typeof v !== "object" || !(v.formula || v.sharedFormula)) return;
    const f = cell.formula ?? v.formula;
    if (!f) return;
    let nf = f.replace(selfRe, (all, pre) => `${pre}${quoteSheet(label)}!`);
    if (nf !== f) formulas.renamed++;
    if (/!/.test(nf.replace(new RegExp(`${esc(quoteSheet(label))}!`, "g"), ""))) {
      cell.value = v.result ?? null; formulas.toValues++; if (formulas.cells.length < 20) formulas.cells.push(cell.address);
      return;
    }
    cell.value = { formula: nf, result: v.result };
  }));

  // Dates: clear the ones later than the submission, then set the stage date on delivered rows.
  const cleared = [], set = [], notDates = [], formulaDates = [];
  const isFormula = (v) => v && typeof v === "object" && (v.formula || v.sharedFormula);
  for (const { r, id } of rows) {
    for (const [, c] of stageCols) {
      const cell = ws.getRow(r).getCell(c);
      const d = asDate(cell.value);
      if (cell.value != null && cell.value !== "" && !d && !isFormula(cell.value)) { if (notDates.length < 20) notDates.push(`${id} ${header(c)}: ${cellText(cell.value)}`); continue; }
      if (!d || day(d) <= day(sub)) continue;   // whole days: the submission day itself (any time) stays
      if (isFormula(cell.value)) { formulaDates.push({ id, column: header(c), was: day(d) }); continue; }   // computed: reported, never cleared silently
      cleared.push({ id, column: header(c), was: day(d) }); cell.value = null;
    }
    if (m.matched.has(id)) {
      for (const [, c] of setCols) {
        const cell = ws.getRow(r).getCell(c);
        const was = asDate(cell.value);
        cell.value = sub;
        if (!cell.numFmt) cell.numFmt = "dd/mm/yyyy";
        set.push({ id, column: header(c), was: was ? day(was) : null, files: m.matched.get(id), near: near.some((n) => n.accepted && n.id === id) || undefined });
      }
    }
  }

  // What the copy can break: merged cells in the id / stage columns (the write refuses unless allowed), defined names
  // and data validations that point into the tabs being dropped (warnings).
  const watched = new Set([idCol, ...stageCols.map(([, c]) => c), ...setCols.map(([, c]) => c)]);
  const colNum = (ref) => ref.replace(/[0-9$]/g, "").split("").reduce((x, ch) => x * 26 + ch.toUpperCase().charCodeAt(0) - 64, 0);
  const merged = (ws.model.merges ?? []).filter((rg) => {
    const [a, b = a] = rg.split(":");
    const lo = colNum(a), hi = colNum(b);
    return [...watched].some((c) => c >= lo && c <= hi);
  });
  const dropped = wb.worksheets.filter((w) => w.id !== ws.id).map((w) => w.name);
  const intoDropped = (f) => dropped.some((n) => new RegExp(`(^|[^A-Za-z0-9_])(?:'${esc(n.replace(/'/g, "''"))}'|${esc(n)})!`, "i").test(String(f ?? "")));
  const definedNames = [];
  for (const dn of wb.definedNames?.model ?? []) {
    const ranges = dn.ranges ?? [];
    if (!ranges.length || ranges.some(intoDropped)) definedNames.push(`${dn.name} (${ranges.join(", ") || "no range"})`);
  }
  const validations = [];
  for (const [addr, dv] of Object.entries(ws.dataValidations?.model ?? {}))
    if ((dv?.formulae ?? []).some(intoDropped)) validations.push(`${addr}: ${dv.formulae.join(", ")}`);

  // The copy: the register sheet only, under the new name.
  for (const other of [...wb.worksheets]) if (other.id !== ws.id) wb.removeWorksheet(other.id);
  ws.name = label;
  const report = {
    sheet: oldName, label, submissionDate: day(sub), rows: rows.length, stageColumn: setCols.map(([, c]) => header(c)).join(", "),
    matched: set, deliveredNotInRegister: m.unmatched, notDelivered: uniqueIds.filter((id) => !m.matched.has(id) && !near.some((n) => n.id === id)),
    timesInRegister: Object.fromEntries(uniqueIds.map((id) => [id, rows.filter((x) => lc(x.id) === lc(id)).length]).filter(([, n]) => n > 1)),
    near: near.filter((n) => !n.accepted).map(({ id, file, differs }) => ({ id, file, differs })), nearAccepted: near.filter((n) => n.accepted).map(({ id, file }) => ({ id, file })),
    skipped: skipped.map((x) => ({ row: x.r, value: x.id })),
    cleared, formulaDates, ambiguous: m.ambiguous, duplicateIds: [...new Set(dupIds)], formulas, notDates,
    merged, definedNames, validations,
  };
  return { wb, report };
}

/** The report in words (counts first, then the first items of each list). */
export function reportText(rep, { mode, output }) {
  const xn = (id) => rep.timesInRegister?.[id] > 1 ? `${id} (x${rep.timesInRegister[id]} in register)` : id;
  const few = (l, f = (x) => x) => l.length ? `: ${l.slice(0, 10).map(f).join("; ")}${l.length > 10 ? `; ... ${l.length - 10} more` : ""}` : "";
  return [`REGISTER UPDATE ${rep.sheet} -> "${rep.label}" for the submission of ${rep.submissionDate} (${mode === "write" ? `written to ${output}` : "check, nothing written"}).`,
    `${rep.rows} register row(s); stage column(s) ${rep.stageColumn}.`,
    `Matched (delivered, date set): ${new Set(rep.matched.map((x) => x.id)).size}${few(rep.matched.filter((x, i, l) => l.findIndex((y) => y.id === x.id) === i), (x) => `${xn(x.id)} <- ${x.files.join(", ")}`)}.`,
    rep.near.length ? `Probably the same, confirm (one naming field differs; set only with accept_near: [ids]): ${rep.near.length}${few(rep.near, (x) => `${xn(x.id)} ~ ${x.file} (${x.differs})`)}.` : "",
    rep.nearAccepted.length ? `Near matches accepted (date set): ${rep.nearAccepted.map((x) => `${x.id} <- ${x.file}`).join("; ")}.` : "",
    rep.skipped.length ? `Headings/skipped (not ids): ${rep.skipped.length}${few(rep.skipped, (x) => `row ${x.row} "${x.value}"`)}.` : "",
    `Delivered but not in the register: ${rep.deliveredNotInRegister.length}${few(rep.deliveredNotInRegister)}.`,
    `In the register but not delivered: ${rep.notDelivered.length}${few(rep.notDelivered, xn)}.`,
    `Dates later than the submission, cleared: ${rep.cleared.length}${few(rep.cleared, (x) => `${x.id} ${x.column} ${x.was}`)}.`,
    rep.ambiguous.length ? `Ambiguous file names (match several ids, all set): ${rep.ambiguous.map((a) => `${a.file} -> ${a.ids.join(", ")}`).join("; ")}.` : "",
    rep.duplicateIds.length ? `Ids listed twice in the register: ${rep.duplicateIds.join(", ")}.` : "",
    rep.formulaDates.length ? `Formula date later than submission, left (computed, not cleared): ${rep.formulaDates.length}${few(rep.formulaDates, (x) => `${x.id} ${x.column} ${x.was}`)}.` : "",
    rep.merged.length ? `Merged cells in the id or stage columns: ${rep.merged.join(", ")} (the write refuses unless allow_merged: unmerge them, or allow after the user checked).` : "",
    rep.definedNames.length ? `Warning: defined names that point into dropped tabs (broken in the copy): ${rep.definedNames.join("; ")}.` : "",
    rep.validations.length ? `Warning: data validations that point into dropped tabs (broken in the copy): ${rep.validations.join("; ")}.` : "",
    rep.notDates.length ? `Cells in the stage columns that are not dates (left as they are): ${rep.notDates.join("; ")}.` : "",
    `Formulas: ${rep.formulas.renamed} now name "${rep.label}"; ${rep.formulas.toValues} pointed into other tabs and became their values${rep.formulas.cells.length ? ` (${rep.formulas.cells.join(", ")})` : ""}. Other tabs are dropped from the copy.`,
  ].filter(Boolean).join("\n");
}
