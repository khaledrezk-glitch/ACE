// Parameter lists (e.g. a client's AIS / LOIN parameter list as CSV) -> rows for the add-in's parameter_fill command.
// Pure functions: CSV parsing, category -> group mapping, de-duplication, and the per-model text summary.

/** RFC 4180 CSV: quoted fields (commas, line breaks, "" inside quotes), BOM, CRLF / LF. `;` when the header has no comma. */
export function parseCsv(textIn) {
  const s = String(textIn ?? "").replace(/^﻿/, "");
  const firstLine = s.slice(0, s.search(/\r?\n|$/));
  const sep = !firstLine.includes(",") && firstLine.includes(";") ? ";" : ",";
  const rows = [];
  let row = [], field = "", quoted = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (quoted) {
      if (c === '"') {
        if (s[i + 1] === '"') { field += '"'; i++; } else quoted = false;
      } else field += c;
    } else if (c === '"') quoted = true;
    else if (c === sep) { row.push(field); field = ""; }
    else if (c === "\n" || c === "\r") {
      if (c === "\r" && s[i + 1] === "\n") i++;
      row.push(field); rows.push(row); row = []; field = "";
    } else field += c;
  }
  if (field !== "" || row.length) { row.push(field); rows.push(row); }
  return rows.filter((r) => r.some((f) => f.trim() !== ""));
}

const norm = (v) => String(v ?? "").trim().toLowerCase().replace(/\s+/g, " ");

/** The list's `categories` values (and the group keys themselves) -> group key. */
const GROUPS = {
  "project information": "project", project: "project",
  "rooms, areas, spaces": "rooms", rooms: "rooms",
  levels: "levels",
  "systems, components, elements": "elements", elements: "elements",
};
export const groupOf = (categories) => GROUPS[norm(categories).replace(/\s*,\s*/g, ", ")] ?? null;

/** `type_instance`: Type -> type; Instance or blank -> instance; anything else -> null. */
export function levelOf(v) {
  const n = norm(v);
  if (n === "" || n === "instance") return "instance";
  if (n === "type") return "type";
  return null;
}

/**
 * Raw rows ({ name, categories | group, type_instance | level, line? }) -> { rows: [{ name, group, level }], lines, skipped, duplicates }.
 * lines[i] is the list line of rows[i] (when given).
 * The key is name + group (one name such as Area can be in two groups); the first row of a key wins.
 */
export function mapRows(raw) {
  const rows = [], lines = [], skipped = [], duplicates = [], seen = new Set();
  for (const r of raw) {
    const name = String(r.name ?? "").trim();
    const cat = r.group ?? r.categories ?? "";
    const where = r.line ? { line: r.line } : {};
    if (!name) { skipped.push({ ...where, name, categories: cat, reason: "no parameter name" }); continue; }
    const group = groupOf(cat);
    if (!group) { skipped.push({ ...where, name, categories: cat, reason: "unknown category" }); continue; }
    const level = levelOf(r.level ?? r.type_instance);
    if (!level) { skipped.push({ ...where, name, categories: cat, reason: `unknown type_instance "${r.level ?? r.type_instance}"` }); continue; }
    const key = `${name}\0${group}`;
    if (seen.has(key)) { duplicates.push({ ...where, name, group }); continue; }
    seen.add(key);
    // The list's own origin column ("Built-In"): a Revit built-in parameter is never bound.
    rows.push(/^built[\s_-]?in$/i.test(String(r.origin ?? "").trim()) ? { name, group, level, origin: "built-in" } : { name, group, level });
    lines.push(r.line);
  }
  return { rows, lines, skipped, duplicates };
}

/** CSV text with columns parameter_name, categories, type_instance (others ignored) -> mapRows result. */
export function rowsFromCsv(csvText) {
  const [head, ...body] = parseCsv(csvText);
  if (!head) throw new Error("The parameter list is empty.");
  const cols = head.map((h) => norm(h).replace(/[\s-]+/g, "_"));
  const at = (n) => cols.indexOf(n);
  const [iName, iCat, iLevel, iOrigin] = [at("parameter_name"), at("categories"), at("type_instance"), at("origin")];
  if (iName < 0 || iCat < 0) {
    throw new Error(`The parameter list needs the columns parameter_name and categories (found: ${head.join(", ")}).`);
  }
  return mapRows(body.map((f, k) => ({ line: k + 2, name: f[iName], categories: f[iCat], type_instance: iLevel < 0 ? "" : f[iLevel], origin: iOrigin < 0 ? undefined : f[iOrigin] })));
}

/** One line per model (and one for the fill) from the bridge answer. */
export function summaryText(result, listInfo = {}) {
  const lines = [];
  for (const d of result?.documents ?? []) {
    const s = d.summary ?? summarise(d.rows ?? []);
    // 1.10 fields (requirement sources): shown only when the add-in sends them.
    const more = [["withNA", "with NA"], ["outOfScope", "out of scope"], ["notBound", "not bound"], ["notAvailable", "built-in not available on some elements"], ["guidDiffers", "GUID differs from the files"], ["wrongGuid", "GUID differs from the files"], ["wrongLevel", "wrong type/instance"], ["foreign", "foreign element(s) not judged"]]
      .filter(([k]) => s[k] != null).map(([k, t]) => `${s[k]} ${t}`).join(", ");
    lines.push(`${d.model}${d.discipline ? ` (${d.discipline})` : ""}: ${s.rows} rows - ${s.complete} complete, ${s.withBlanks} with blanks, ${s.missingEverywhere} missing everywhere, ` +
      `${s.notText} not text (cannot take NA), ${s.notInGeneral} Project Information row(s) not in General${more ? `; ${more}` : ""}.`);
  }
  const f = result?.fill;
  if (f) {
    const why = {};
    for (const k of f.skipped ?? []) why[k.reason] = (why[k.reason] ?? 0) + 1;
    const reasons = Object.entries(why).map(([r, n]) => `${n} ${r}`).join(", ");
    const set = listInfo.rolledBack ? `${f.wouldSet ?? f.set ?? 0} value(s) would have been set; rolled back, nothing changed` : `${f.set ?? 0} value(s) ${listInfo.dryRun ? "would be " : ""}set`;
    lines.push(`Fill: ${set}, ${(f.skipped ?? []).length} skipped${reasons ? ` (${reasons})` : ""}.`);
  }
  if (listInfo.skipped?.length || listInfo.duplicates?.length) {
    lines.push(`List: ${listInfo.skipped?.length ?? 0} row(s) skipped (unknown category or type), ${listInfo.duplicates?.length ?? 0} duplicate(s) dropped.`);
  }
  return lines.join("\n");
}

/** Fallback when the add-in sends no summary. */
function summarise(rows) {
  const s = { rows: rows.length, complete: 0, withBlanks: 0, missingEverywhere: 0, notText: 0, notInGeneral: 0 };
  for (const r of rows) {
    if (r.checked > 0 && r.missing === r.checked) s.missingEverywhere++;
    else if (r.blank > 0) s.withBlanks++;
    else if (r.missing === 0) s.complete++;
    if (r.storageType && r.isText === false) s.notText++;
    if (r.inGeneral === false) s.notInGeneral++;
  }
  return s;
}
