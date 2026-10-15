// Master audit report (backlog V4): one report per audit, built from the vault (documents, requirement records,
// decisions) and a compliance run record (the add-in writes one per model check into 04 Audits/<model>/runs). Saved in
// the vault's 04 Audits folder as HTML (opens anywhere), DOCX (the report format) and CSV (the actions, for a tracker),
// with a small JSON sidecar so the next audit can say what closed, what is new and which documents changed.
import fs from "node:fs";
import path from "node:path";
import { docx } from "./docx.js";
import { locationOf } from "./vault.js";

const AUDITS = "04 Audits";
const ORDER = { "not-met": 0, partly: 1, met: 2, "not-checkable": 3, "not evaluated": 4 };
const SEV = { error: 0, warning: 1, info: 2 };
const OPEN = new Set(["not-met", "partly"]);
const readJson = (f) => JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, ""));
const safe = (s) => String(s ?? "model").replace(/\.rvt$/i, "").replace(/[\\/:*?"<>|]+/g, "_").trim() || "model";
const clip = (s, n) => { s = String(s ?? "").replace(/\s+/g, " ").trim(); return s.length > n ? s.slice(0, n - 1) + "…" : s; };

/** Run records in the vault, newest first: { file, modelKey, id }. */
export function runs(vaultDir, modelKey) {
  const root = path.join(vaultDir, AUDITS);
  if (!fs.existsSync(root)) return [];
  const out = [];
  for (const m of fs.readdirSync(root, { withFileTypes: true })) {
    if (!m.isDirectory() || (modelKey && m.name !== modelKey)) continue;
    const dir = path.join(root, m.name, "runs");
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir)) if (f.endsWith(".json")) out.push({ file: path.join(dir, f), modelKey: m.name, id: f.slice(0, -5) });
  }
  return out.sort((a, b) => b.id.localeCompare(a.id));
}

/** The model a run record belongs to (its title). */
export function modelOf(file) { try { return readJson(file).run?.model ?? null; } catch { return null; } }

// <model>_audit_<yyyy-mm-dd>_R<nn>.<ext>: the whole pattern, so "Tower" never matches "Tower_audit_X"'s reports.
const reportPattern = (model, ext) => new RegExp(`^${safe(model).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}_audit_\\d{4}-\\d{2}-\\d{2}_R\\d+\\.${ext}$`);

/** The latest earlier report of this model (its sidecar), for "this audit vs the last one". */
function previousReport(vaultDir, model) {
  const root = path.join(vaultDir, AUDITS);
  if (!fs.existsSync(root)) return null;
  const re = reportPattern(model, "json");
  const sidecars = fs.readdirSync(root).filter((f) => re.test(f)).sort();
  return sidecars.length ? { ...readJson(path.join(root, sidecars.at(-1))), file: sidecars.at(-1), count: sidecars.length } : { count: 0 };
}

/**
 * Builds the report content (no files written). v: the loaded vault; run: a compliance run record ({ run, summary,
 * results, checks }). Returns { name, rev, html, docx, csv, sidecar, summary }.
 */
export function build(v, run, { by = "", brand = {}, date = new Date() } = {}) {
  const meta = run.run ?? {};
  const model = meta.model ?? "model";
  const prev = previousReport(v.dir, model);
  const rev = `R${String((prev?.count ?? 0) + 1).padStart(2, "0")}`;
  const day = date.toISOString().slice(0, 10);
  const name = `${safe(model)}_audit_${day}_${rev}`;
  const project = v.register.project ?? {};
  const recs = new Map(v.reqs.records.filter((r) => !r.rejected).map((r) => [r.id, r]));

  // Every requirement in the vault, with this run's result; records added after the run are listed as not evaluated.
  const results = new Map((run.results ?? []).map((r) => [r.requirement, r]));
  for (const id of recs.keys()) if (!results.has(id)) results.set(id, { requirement: id, status: "not evaluated", evidence: "added to the vault after this model check: run it again" });
  const rows = [...results.values()].map((r) => {
    const rec = recs.get(r.requirement);
    return { ...r, rec, severity: r.severity ?? rec?.severity?.value ?? "info", docKey: rec?.doc?.key ?? "", revision: rec?.doc?.revision ?? "",
      at: rec ? locationOf(rec) : "", text: rec?.normalized || rec?.source?.quote || "(not in the vault: named by a rule only)",
      test: r.binding || (rec?.test?.checkability ? `checkability ${rec.test.checkability}` : "") };
  }).sort((a, b) => (ORDER[a.status] ?? 9) - (ORDER[b.status] ?? 9) || (SEV[a.severity] ?? 9) - (SEV[b.severity] ?? 9) || a.requirement.localeCompare(b.requirement, "en", { numeric: true }));

  const count = (s) => rows.filter((r) => r.status === s).length;
  const open = rows.filter((r) => OPEN.has(r.status));
  const openBySev = Object.fromEntries(["error", "warning", "info"].map((s) => [s, open.filter((r) => r.severity === s).length]));
  const stale = v.reqs.updated && meta.time && v.reqs.updated > meta.time;

  // This audit vs the last one.
  let trend = null;
  if (prev?.statuses) {
    const was = prev.statuses;
    trend = {
      since: prev.file, closed: Object.keys(was).filter((id) => OPEN.has(was[id]) && results.get(id)?.status === "met"),
      newOpen: open.filter((r) => !OPEN.has(was[r.requirement])).map((r) => r.requirement),
      repeated: open.filter((r) => OPEN.has(was[r.requirement])).map((r) => r.requirement),
      scoreThen: prev.score,
    };
  }
  const docs = v.register.documents.map((d) => ({ ...d, changedSince: prev?.documents ? (prev.documents[d.key] ? prev.documents[d.key] !== d.sha256 : "new") : null }));
  const failing = (run.checks ?? []).filter((c) => c.status && c.status !== "ok");
  const decisions = v.decisions.decisions;
  const top = open.slice(0, 3);
  const statusLabel = { "not-met": "Not met", partly: "Partly met", met: "Met", "not-checkable": "Not checkable", "not evaluated": "Not evaluated" };

  // ---- one content model, rendered twice (HTML and DOCX) ----------------------------------------------------------
  const cover = [
    ["Project", [project.name, project.number].filter(Boolean).join(" · ") || "(not set in the vault)"],
    ["Model", model], ["Report", `${rev}, ${day}`], ["Auditor", by || "(not given)"],
    ["Model check", `${meta.time ?? "?"} by ${meta.user ?? "?"} (run ${meta.id ?? "?"})${meta.score != null ? `, model score ${meta.score}/100` : ""}`],
    ["Audited against", v.register.documents.map((d) => `${d.key} ${d.title ?? ""} rev ${d.revision || "?"}`.trim()).join("; ") || "(no documents registered)"],
  ];
  const summaryLines = [
    `${rows.length} requirements: ${count("met")} met, ${count("partly")} partly met, ${count("not-met")} not met, ${count("not-checkable")} not checkable by ACE${count("not evaluated") ? `, ${count("not evaluated")} not evaluated` : ""}.`,
    `Open findings by severity: ${openBySev.error} error, ${openBySev.warning} warning, ${openBySev.info} info.`,
    meta.score != null ? `Model health score: ${meta.score}/100${trend?.scoreThen != null ? ` (last audit: ${trend.scoreThen})` : ""}.` : null,
    trend ? `Since the last audit (${trend.since}): ${trend.closed.length} closed, ${trend.newOpen.length} new, ${trend.repeated.length} still open.` : "This is the first audit of this model in this vault.",
    stale ? "Note: requirements changed after this model check. Run the model check again for a current result." : null,
  ].filter(Boolean);
  const matrixHead = ["Requirement", "Source", "Requirement text", "Test", "Evidence", "Status", "Severity", "Decision"];
  const matrix = rows.map((r) => [r.requirement, [r.docKey, r.revision && `rev ${r.revision}`, r.at].filter(Boolean).join(", "), clip(r.text, 400), r.test ?? "", r.evidence ?? "", statusLabel[r.status] ?? r.status, r.severity, r.decision ?? ""]);
  const docHead = ["Key", "Document", "Type", "Number", "Revision", "Status", "Changed since last audit"];
  const docRows = docs.map((d) => [d.key, d.title ?? "", d.type ?? "", d.number ?? "", d.revision ?? "", d.status ?? "", d.changedSince === null ? "(first audit)" : d.changedSince === "new" ? "new document" : d.changedSince ? "yes: check its requirements" : "no"]);
  const findHead = ["Section", "Check", "Result", "Status", "Detail"];
  const findRows = failing.map((c) => [c.area ?? "", c.name ?? c.key, c.of != null ? `${c.count} of ${c.of}` : String(c.count), c.status, c.detail ?? ""]);
  const decHead = ["Decision", "Kind", "Requirements", "Note", "By", "Date"];
  const decRows = decisions.map((d) => [d.id, d.kind, d.requirements.join(", "), d.note ?? "", d.by ?? "", (d.at ?? "").slice(0, 10)]);
  // Owner and due date stay blank on purpose: the team fills them in.
  const actHead = ["Requirement", "Severity", "Status", "What to do", "Owner", "Due"];
  const actRows = open.map((r) => [r.requirement, r.severity, statusLabel[r.status], clip(r.evidence ?? r.text, 300), "", ""]);

  // ---- HTML -------------------------------------------------------------------------------------------------------
  const e = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const accent = brand.accent ?? "#EF3340", ink = brand.primary ?? "#212121";
  const table = (head, body, cls = "") => body.length
    ? `<div class="scroll"><table class="${cls}"><thead><tr>${head.map((h) => `<th>${e(h)}</th>`).join("")}</tr></thead><tbody>${body.map((r) => `<tr>${r.map((c) => `<td>${e(c)}</td>`).join("")}</tr>`).join("\n")}</tbody></table></div>`
    : '<p class="m">None.</p>';
  const badge = (s) => `<span class="b ${s.replace(/\s/g, "-")}">${e(statusLabel[s] ?? s)}</span>`;
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Master audit report · ${e(model)} · ${rev}</title>
<style>
body{font:14px/1.45 ${e(brand.font ?? "Poppins")},Segoe UI,Arial,sans-serif;color:${ink};margin:0;background:#fff}
.page{max-width:1200px;margin:0 auto;padding:24px 16px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:17px;margin:32px 0 10px;border-bottom:2px solid ${accent};padding-bottom:4px}
.m{color:#666}.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #ddd;vertical-align:top}
th{background:#f3f3f3;position:sticky;top:0}.cover td:first-child{width:180px;color:#666}
.b{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;white-space:nowrap}.b.not-met{background:${accent};color:#fff}.b.partly{background:#F6C343}
.b.met{background:#DFF0D8}.b.not-checkable,.b.not-evaluated{background:#E6E6E6}
.kpi{display:inline-block;margin:0 24px 8px 0}.kpi b{display:block;font-size:26px}
@media print{th{position:static}.page{max-width:none}}
</style></head><body><div class="page">
<h1>Master audit report</h1><div class="m">${e(project.name || "")} ${e(model)} · ${rev} · ${day}</div>
<h2>Cover</h2><table class="cover">${cover.map(([k, val]) => `<tr><td>${e(k)}</td><td>${e(val)}</td></tr>`).join("")}</table>
<h2>Summary</h2>
<div>${["met", "partly", "not-met", "not-checkable"].map((s) => `<span class="kpi"><b>${count(s)}</b>${badge(s)}</span>`).join("")}</div>
${summaryLines.map((l) => `<p>${e(l)}</p>`).join("")}
<h3>The actions that matter most</h3>${top.length ? `<ol>${top.map((r) => `<li><b>${e(r.requirement)}</b> (${e(r.severity)}): ${e(clip(r.evidence ?? r.text, 200))}</li>`).join("")}</ol>` : '<p class="m">No open findings.</p>'}
<h2>Document register</h2>${table(docHead, docRows)}
<h2>Compliance matrix</h2><p class="m">Requirement, source clause, test, evidence and status. The CSV beside this report has the actions for a tracker.</p>
<div class="scroll"><table><thead><tr>${matrixHead.map((h) => `<th>${e(h)}</th>`).join("")}</tr></thead><tbody>
${rows.map((r, i) => `<tr>${matrix[i].map((c, j) => `<td>${j === 5 ? badge(r.status) : e(c)}</td>`).join("")}</tr>`).join("\n")}</tbody></table></div>
<h2>Model findings</h2>${table(findHead, findRows)}
<h2>Decisions and deviations</h2>${table(decHead, decRows)}
<h2>Actions</h2>${table(actHead, actRows)}
<h2>Contradictions</h2><p class="m">Not part of this report yet: comparing documents with each other and with ACE's settings is planned (backlog V3).</p>
<p class="m">Made by ACE from the vault at ${e(v.dir)} and model check run ${e(meta.id ?? "?")}. Status and severity are separate scales: an accepted deviation is shown beside the status and never counted as met.</p>
</div></body></html>`;

  // ---- DOCX -------------------------------------------------------------------------------------------------------
  const docxBuf = docx([
    { h: 1, text: "Master audit report" }, { p: `${project.name || ""} ${model} · ${rev} · ${day}`.trim(), muted: true },
    { table: { rows: cover.map(([k, val]) => [{ text: k, bold: true }, val]) } },
    { h: 1, text: "Summary" }, ...summaryLines.map((l) => ({ p: l })),
    { h: 2, text: "The actions that matter most" }, top.length ? { bullets: top.map((r) => `${r.requirement} (${r.severity}): ${clip(r.evidence ?? r.text, 200)}`) } : { p: "No open findings.", muted: true },
    { h: 1, text: "Document register" }, { table: { head: docHead, rows: docRows } },
    { h: 1, text: "Compliance matrix" },
    { table: { head: matrixHead, rows: matrix.map((m, i) => m.map((c, j) => j === 5 ? { text: c, bold: OPEN.has(rows[i].status), color: rows[i].status === "not-met" ? accent : undefined } : c)) } },
    { h: 1, text: "Model findings" }, findRows.length ? { table: { head: findHead, rows: findRows } } : { p: "None.", muted: true },
    { h: 1, text: "Decisions and deviations" }, decRows.length ? { table: { head: decHead, rows: decRows } } : { p: "None.", muted: true },
    { h: 1, text: "Actions" }, actRows.length ? { table: { head: actHead, rows: actRows } } : { p: "None.", muted: true },
    { h: 1, text: "Contradictions" }, { p: "Not part of this report yet: comparing documents with each other and with ACE's settings is planned (backlog V3).", muted: true },
  ], { title: `Master audit report ${model} ${rev}`, font: brand.font ?? "Poppins", accent });

  const csvCell = (c) => /[",\n;]/.test(String(c ?? "")) ? `"${String(c).replace(/"/g, '""')}"` : String(c ?? "");
  const csv = "﻿" + [actHead, ...actRows].map((r) => r.map(csvCell).join(",")).join("\r\n") + "\r\n";

  const sidecar = {
    schema: "ace.audit/1", model, rev, date: day, by, run: meta.id, score: meta.score ?? null,
    statuses: Object.fromEntries(rows.map((r) => [r.requirement, r.status])),
    documents: Object.fromEntries(v.register.documents.map((d) => [d.key, d.sha256])),
  };
  const summary = { requirements: rows.length, met: count("met"), partly: count("partly"), notMet: count("not-met"), notCheckable: count("not-checkable"),
    notEvaluated: count("not evaluated"), openBySeverity: openBySev, score: meta.score ?? null, trend: trend && { closed: trend.closed.length, new: trend.newOpen.length, stillOpen: trend.repeated.length },
    staleRun: !!stale };
  sidecar.summary = summary;
  return { name, rev, html, docx: docxBuf, csv, sidecar, summary };
}

/** The newest report of a model (its sidecar with the summary), or null. */
export function latest(vaultDir, model) {
  const p = previousReport(vaultDir, model);
  return p?.file ? { ...p, html: path.join(vaultDir, AUDITS, p.file.replace(/\.json$/, ".html")) } : null;
}

/** Writes the report files into 04 Audits and returns their paths. */
export function save(v, report) {
  const dir = path.join(v.dir, AUDITS);
  fs.mkdirSync(dir, { recursive: true });
  const p = (ext) => path.join(dir, `${report.name}.${ext}`);
  fs.writeFileSync(p("html"), report.html);
  fs.writeFileSync(p("docx"), report.docx);
  fs.writeFileSync(p("csv"), report.csv);
  fs.writeFileSync(p("json"), JSON.stringify(report.sidecar, null, 2));
  return { html: p("html"), docx: p("docx"), csv: p("csv"), record: p("json") };
}
