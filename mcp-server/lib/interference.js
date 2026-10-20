// The interference report (coordination_report interference: true): the clash engine's rows per clash, grouped by
// issue, written as a client-readable HTML laid out like Revit's Interference Report, a CSV and an xlsx. Pure
// functions; index.js asks Revit for the rows and writes the files.
import { esc } from "./vault.js";
import { toCsv } from "./report.js";
import { xlsxBuffer } from "./paramactions.js";
import { fileStem, safeName } from "./paramexec.js";

export const MATERIALS = ["steel", "concrete", "timber", "masonry", "precast", "other", "unknown"];
const MATERIAL_ARGS = ["materials", "exclude_materials", "a_materials", "b_materials"];

/** The material scope of a call, in words ("" when none): "only concrete; excluded steel; A: ...". */
export function materialScope(a = {}) {
  const parts = [];
  if (a.materials?.length) parts.push(`only ${a.materials.join(", ")}`);
  if (a.exclude_materials?.length) parts.push(`excluded ${a.exclude_materials.join(", ")}`);
  if (a.a_materials?.length) parts.push(`A: ${a.a_materials.join(", ")}`);
  if (a.b_materials?.length) parts.push(`B: ${a.b_materials.join(", ")}`);
  return parts.join("; ");
}
/** The material arguments of a call (those given), to pass to the add-in. */
export const materialArgs = (a = {}) => Object.fromEntries(MATERIAL_ARGS.filter((k) => a[k]?.length).map((k) => [k, a[k]]));
/** The add-in's filtered: { material: n } -> "steel 12, unknown 3" ("" when nothing was left out). */
export const filteredText = (f) => Object.entries(f ?? {}).filter(([, n]) => n).sort((x, y) => y[1] - x[1]).map(([m, n]) => `${m} ${n}`).join(", ");

/** The filters of a report in words, for its header. */
export function filterText(a = {}) {
  return [a.level ? `level ${a.level}` : "", a.tolerance_mm != null ? `tolerance ${a.tolerance_mm} mm` : "",
    a.a_categories?.length ? `A categories ${a.a_categories.join(", ")}` : "", a.b_categories?.length ? `B categories ${a.b_categories.join(", ")}` : "",
    a.a_disciplines?.length ? `A disciplines ${a.a_disciplines.join(", ")}` : "", a.b_disciplines?.length ? `B disciplines ${a.b_disciplines.join(", ")}` : "",
    materialScope(a) ? `materials: ${materialScope(a)}` : ""].filter(Boolean).join("; ") || "none";
}

const side = (r, s) => [r[`${s}Category`], r[`${s}Family`], r[`${s}Type`], r[`${s}Id`]].map((x) => x ?? "").join(" : ");
const pairOf = (r) => [r.aCategory ?? "?", r.bCategory ?? "?"].sort().join(" x ");
const num = (v, d = 0) => typeof v === "number" && Number.isFinite(v) ? Number(v.toFixed(d)) : v ?? "";

/** Counts by status and by category pair, and the issues in order (largest first, then by key). */
export function summarise(rows = []) {
  const byStatus = {}, byPair = {}, issues = new Map();
  for (const r of rows) {
    byStatus[r.status ?? "?"] = (byStatus[r.status ?? "?"] ?? 0) + 1;
    byPair[pairOf(r)] = (byPair[pairOf(r)] ?? 0) + 1;
    // Approved and resolved clashes carry no issue: one group each, after the open issues.
    const closed = r.issue == null && (r.status === "approved" || r.status === "resolved");
    const k = closed ? (r.status === "approved" ? "Approved" : "Resolved") : String(r.issue ?? r.clashKey ?? "?");
    const g = issues.get(k) ?? { issue: k, title: closed ? (r.status === "approved" ? "accepted, no action" : "no longer clashing") : r.issueTitle ?? "", closed, rows: [] };
    g.rows.push(r);
    issues.set(k, g);
  }
  const list = [...issues.values()].sort((x, y) => (x.closed - y.closed) || (x.closed ? x.issue.localeCompare(y.issue) : y.rows.length - x.rows.length || x.issue.localeCompare(y.issue, undefined, { numeric: true })));
  return { clashes: rows.length, issues: list.filter((g) => !g.closed).length, byStatus, byPair, list };
}

const HEAD = ["issue", "issue title", "clash key", "status", "A category", "A family", "A type", "A id", "B category", "B family", "B type", "B id", "level", "depth mm", "x", "y", "z", "responsible"];
const line = (r) => [r.issue, r.issueTitle, r.clashKey, r.status, r.aCategory, r.aFamily, r.aType, r.aId, r.bCategory, r.bFamily, r.bType, r.bId, r.level, num(r.depthMm), num(r.x, 3), num(r.y, 3), num(r.z, 3), r.responsible];
const ordered = (rows) => summarise(rows).list.flatMap((g) => g.rows);

export const csv = (rows) => toCsv([HEAD, ...ordered(rows).map(line)]);
export function workbook(rows, { model, test, when, filters } = {}) {
  const s = summarise(rows);
  return xlsxBuffer([
    { name: "Interference", head: HEAD, rows: ordered(rows).map(line) },
    { name: "Summary", head: ["", "count"], rows: [["model", model], ["test", test], ["date", when], ["filters", filters], ["clashes", s.clashes], ["issues", s.issues],
      ...Object.entries(s.byStatus).map(([k, n]) => [`status: ${k}`, n]), ...Object.entries(s.byPair).sort((x, y) => y[1] - x[1]).map(([k, n]) => [`pair: ${k}`, n])] },
  ]);
}

/** The client-readable HTML: header (model, test, date, filters, counts), then the table grouped by issue. */
export function html(rows, { model, test, when, run, filters, brand = {} } = {}) {
  const s = summarise(rows);
  const c = { primary: brand.primary ?? "#212121", accent: brand.accent ?? "#EF3340", dark: brand.greyDark ?? "#414042", light: brand.greyLight ?? "#E6E6E6" };
  const font = `'${brand.font ?? "Poppins"}', 'Segoe UI', Arial, sans-serif`, head = `'${brand.headingFont ?? brand.font ?? "Poppins"}', ${font}`;
  const counts = (o) => Object.entries(o).sort((x, y) => y[1] - x[1]).map(([k, n]) => `<span class="chip">${esc(k)} <b>${n}</b></span>`).join(" ") || "none";
  const body = s.list.map((g) => `<tbody class="issue"><tr class="ih"><th colspan="7">${g.closed ? "" : "Issue "}${esc(g.issue)}${g.title ? `: ${esc(g.title)}` : ""} <span class="n">(${g.rows.length} clash${g.rows.length === 1 ? "" : "es"})</span></th></tr>` +
    g.rows.map((r) => `<tr><td>${esc(side(r, "a"))}</td><td>${esc(side(r, "b"))}</td><td>${esc(r.level ?? "")}</td><td class="num">${esc(num(r.depthMm))}</td>` +
      `<td class="num">${[r.x, r.y, r.z].map((v) => esc(num(v, 3))).join(" / ")}</td><td>${esc(r.status ?? "")}</td><td>${esc(r.responsible ?? "")}</td></tr>`).join("") + "</tbody>").join("");
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Interference Report - ${esc(model)} - ${esc(test)}</title>
<style>
:root { --p: ${c.primary}; --a: ${c.accent}; --d: ${c.dark}; --l: ${c.light}; }
* { box-sizing: border-box; }
body { margin: 0; padding: 24px; font: 13px/1.45 ${font}; color: var(--p); background: #fff; }
header { border-bottom: 3px solid var(--a); padding-bottom: 12px; margin-bottom: 16px; }
h1 { font: 600 22px/1.2 ${head}; margin: 0 0 4px; }
.brand { color: var(--a); font-weight: 700; letter-spacing: .04em; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 2px 16px; margin: 8px 0 0; }
dt { color: var(--d); } dd { margin: 0; }
.chip { display: inline-block; background: var(--l); border-radius: 3px; padding: 1px 6px; margin: 1px 2px 1px 0; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; vertical-align: top; padding: 4px 6px; border-bottom: 1px solid var(--l); }
thead th { background: var(--p); color: #fff; font-weight: 600; }
tr.ih th { background: var(--l); color: var(--p); border-top: 2px solid var(--d); }
.n { color: var(--d); font-weight: 400; }
.num { text-align: right; white-space: nowrap; }
footer { margin-top: 16px; color: var(--d); font-size: 11px; }
@media print { body { padding: 0; font-size: 10px; } thead { display: table-header-group; } tbody.issue, tr { break-inside: avoid; } thead th { -webkit-print-color-adjust: exact; print-color-adjust: exact; } }
</style></head><body>
<header><div class="brand">${esc(brand.fullName ?? brand.name ?? "ACE")}</div><h1>Interference Report</h1>
<dl><dt>Model</dt><dd>${esc(model)}</dd><dt>Test</dt><dd>${esc(test)}</dd><dt>Date</dt><dd>${esc(when)}</dd>${run ? `<dt>Run</dt><dd>${esc(typeof run === "object" ? run.id ?? run.at ?? JSON.stringify(run) : run)}</dd>` : ""}
<dt>Filters</dt><dd>${esc(filters)}</dd><dt>Clashes</dt><dd>${s.clashes} in ${s.issues} issue(s)</dd><dt>By status</dt><dd>${counts(s.byStatus)}</dd><dt>By pair</dt><dd>${counts(s.byPair)}</dd></dl></header>
<table><thead><tr><th>A (category : family : type : id)</th><th>B (category : family : type : id)</th><th>Level</th><th class="num">Depth mm</th><th class="num">Location x / y / z</th><th>Status</th><th>Responsible</th></tr></thead>
${body || '<tbody><tr><td colspan="7">No clashes.</td></tr></tbody>'}</table>
<footer>Generated by ACE from the clash results of ${esc(model)}. Grouped by issue: one element to resolve and everything it hits.</footer>
</body></html>
`;
}

export const stem = (model, test, stamp) => fileStem("interference", `${safeName(model)}-${safeName(test)}`, stamp);
