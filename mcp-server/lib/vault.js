// The requirements vault (backlog V1, V2): one folder per project ("ACE Vault", owner decision D2) holding the
// document register (vault.json), the requirement records (requirements.json), the auditor's decisions
// (decisions.json) and a browsable index.html. Records follow the extraction method's template: every record names its
// document, revision and location and quotes the document's own words; a record without them is rejected. Ids are
// stable across document revisions (D4). Severity (info / warning / error) and status are separate scales (D5); status
// is a result of a check run and does not live here.
import fs from "node:fs";
import path from "node:path";
import { sha256, normalise, readDocument, where } from "./docs.js";

export const VAULT = "ACE Vault";
export const FOLDERS = ["01 Requirements", "02 Plans", "03 Standards", "04 Audits", "05 Correspondence"];
export const SCHEMAS = { vault: "ace.vault/1", requirements: "ace.requirements/1", decisions: "ace.decisions/1" };
const SEVERITY = { info: "info", warning: "warning", error: "error", minor: "warning", major: "error", critical: "error" };
const KINDS_WITHOUT_MODEL_TEST = new Set(["process", "legal", "reference"]);

export const vaultDir = (folder) => path.basename(path.resolve(folder)).toLowerCase() === VAULT.toLowerCase() ? path.resolve(folder) : path.join(path.resolve(folder), VAULT);
const file = (dir, name) => path.join(dir, name);
const readJson = (f, fallback) => fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")) : fallback;
const writeJson = (f, o) => { fs.writeFileSync(f + ".tmp", JSON.stringify(o, null, 2)); fs.renameSync(f + ".tmp", f); };
const now = () => new Date().toISOString().replace(/\.\d+Z$/, "Z");

export function exists(dir) { return fs.existsSync(file(dir, "vault.json")); }

export function load(dir) {
  if (!exists(dir)) throw new Error(`No vault at ${dir}. Create it first (action "init").`);
  const register = readJson(file(dir, "vault.json"));
  const reqs = readJson(file(dir, "requirements.json"), { schema: SCHEMAS.requirements, records: [] });
  reqs.records ??= reqs.requirements ?? [];
  delete reqs.requirements;
  const decisions = readJson(file(dir, "decisions.json"), { schema: SCHEMAS.decisions, decisions: [] });
  return { dir, register, reqs, decisions };
}

function save(v, parts = ["register", "reqs", "decisions"]) {
  if (parts.includes("register")) { v.register.updated = now(); writeJson(file(v.dir, "vault.json"), v.register); }
  if (parts.includes("reqs")) { v.reqs.updated = now(); writeJson(file(v.dir, "requirements.json"), v.reqs); }
  if (parts.includes("decisions")) writeJson(file(v.dir, "decisions.json"), v.decisions);
  writeIndex(v);
}

// ---- trust: writing files is a risky action, so the first write to a vault asks once (decision D21 default) ------

export function trusted(dataDir, dir) {
  const list = readJson(path.join(dataDir, "trusted-vaults.json"), []);
  return list.some((d) => path.resolve(d).toLowerCase() === path.resolve(dir).toLowerCase());
}
export function trust(dataDir, dir) {
  const f = path.join(dataDir, "trusted-vaults.json");
  const list = readJson(f, []);
  if (!list.some((d) => path.resolve(d).toLowerCase() === path.resolve(dir).toLowerCase())) { list.push(path.resolve(dir)); fs.mkdirSync(dataDir, { recursive: true }); writeJson(f, list); }
}

// ---- V1: the vault and its document register ------------------------------------------------------------------------

export function init(dir, { project, number } = {}) {
  fs.mkdirSync(dir, { recursive: true });
  for (const f of FOLDERS) fs.mkdirSync(path.join(dir, f), { recursive: true });
  const created = !exists(dir);
  if (created) writeJson(file(dir, "vault.json"), { schema: SCHEMAS.vault, project: { name: project ?? "", number: number ?? "" }, created: now(), documents: [] });
  if (!fs.existsSync(file(dir, "requirements.json"))) writeJson(file(dir, "requirements.json"), { schema: SCHEMAS.requirements, records: [] });
  if (!fs.existsSync(file(dir, "decisions.json"))) writeJson(file(dir, "decisions.json"), { schema: SCHEMAS.decisions, decisions: [] });
  // The project requirements file lives in the vault, so the add-in finds it beside the model or through config.json.
  const pr = file(dir, "project-requirements.json");
  if (!fs.existsSync(pr)) writeJson(pr, { schema: "ace.project-requirements/1", project: { name: project ?? "", number: number ?? "" }, vault: "." });
  const v = load(dir);
  writeIndex(v);
  return { created, vault: dir, folders: FOLDERS, projectRequirements: pr };
}

const rel = (dir, f) => { const r = path.relative(dir, f); return r.startsWith("..") || path.isAbsolute(r) ? path.resolve(f) : r.split(path.sep).join("/"); };
const abs = (dir, f) => path.isAbsolute(f) ? f : path.join(dir, ...f.split("/"));

/** Registers a document (or a new revision of one). key: short code used in requirement ids (EIR, BEP, LOIN ...). */
export function register(v, { key, path: src, type, title, number, revision, status, date }) {
  if (!key || !/^[A-Z0-9]{2,12}$/.test(key)) throw new Error(`Document key '${key}' must be 2-12 capitals or digits (e.g. EIR, BEP, LOIN).`);
  if (!src || !fs.existsSync(src)) throw new Error(`File not found: ${src}`);
  const buf = fs.readFileSync(src);
  const hash = sha256(buf);
  const docs = v.register.documents;
  let d = docs.find((x) => x.key === key);
  const entry = { file: rel(v.dir, src), sha256: hash, size: buf.length, revision: revision ?? d?.revision ?? "", registered: now() };
  if (!d) {
    d = { key, type: type ?? "", title: title ?? path.basename(src), number: number ?? "", status: status ?? "", date: date ?? "", ...entry, history: [] };
    docs.push(d);
    return { document: d, outcome: "registered" };
  }
  Object.assign(d, Object.fromEntries(Object.entries({ type, title, number, status, date }).filter(([, x]) => x != null)));
  if (d.sha256 === hash) { d.file = entry.file; return { document: d, outcome: "unchanged" }; }
  d.history.push({ revision: d.revision, sha256: d.sha256, file: d.file, registered: d.registered });
  Object.assign(d, entry, { changed: true });
  return { document: d, outcome: "new revision", records: v.reqs.records.filter((r) => r.doc?.key === key).length };
}

/** Files in the vault folders not yet registered, and registered files whose bytes changed. */
export function scan(v) {
  const known = new Map(v.register.documents.map((d) => [path.resolve(abs(v.dir, d.file)).toLowerCase(), d]));
  const unregistered = [], changed = [], missing = [];
  const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else if (!known.has(p.toLowerCase())) unregistered.push(rel(v.dir, p)); } };
  for (const f of FOLDERS) if (fs.existsSync(path.join(v.dir, f))) walk(path.join(v.dir, f));
  for (const d of v.register.documents) {
    const p = abs(v.dir, d.file);
    if (!fs.existsSync(p)) missing.push(d.key);
    else if (sha256(fs.readFileSync(p)) !== d.sha256) changed.push(d.key);
  }
  return { unregistered: unregistered.filter((f) => !/04 Audits\//.test(f)), changed, missing };
}

export function documentPath(v, key) {
  const d = v.register.documents.find((x) => x.key === key);
  if (!d) throw new Error(`No document '${key}' in the vault. Registered: ${v.register.documents.map((x) => x.key).join(", ") || "none"}.`);
  return { doc: d, file: abs(v.dir, d.file) };
}

/** Text of a registered document: a range of units (pages, paragraphs or rows), or the units containing a phrase. */
export async function read(v, key, { from = 1, to, sheet, find, max_chars = 20000 } = {}) {
  const { doc, file: f } = documentPath(v, key);
  const content = await readDocument(f, sheet ? { sheets: [sheet] } : {});
  doc.lastRead = now();
  let units = content.units;
  if (find) { const q = normalise(find); units = units.filter((u) => normalise(u.text).includes(q)); }
  else units = units.filter((u) => { const n = u.page ?? u.row ?? u.paragraph ?? u.line; return n >= from && (to == null || n <= to); });
  const out = []; let size = 0, truncated = false;
  for (const u of units) { if (size + u.text.length > max_chars && out.length) { truncated = true; break; } out.push({ at: where(u), section: u.section?.length ? u.section.join(" > ") : undefined, text: u.text }); size += u.text.length; }
  return { key, revision: doc.revision, kind: content.kind, units: content.units.length, sheets: content.sheets, trackedChanges: content.trackedChanges || undefined,
    lowTextPages: content.units.filter((u) => u.lowText).map((u) => u.page), shown: out.length, truncated: truncated || undefined, text: out };
}

// ---- V2: requirement records and decisions --------------------------------------------------------------------------

/** Problems with one record; an empty list means it may be saved. */
export function validate(r, docs) {
  const e = [];
  if (!/^REQ(-[A-Za-z0-9]+){2,6}$/.test(r?.id ?? "")) e.push(`id '${r?.id}' must look like REQ-EIR-0042 (REQ-, the document code, a number)`);
  const key = r?.doc?.key ?? r?.doc?.vault_id;
  if (!key) e.push("doc.key missing: every record names its document");
  else if (docs && !docs.some((d) => d.key === key)) e.push(`document '${key}' is not in the vault register`);
  const s = r?.source ?? {};
  if (!s.quote || !String(s.quote).trim()) e.push("source.quote missing: the document's own words are required");
  if (s.page == null && !s.table && !s.clause_ref && !(s.section_path?.length) && s.paragraph_index == null) e.push("source location missing (page, table, clause_ref, section_path or paragraph_index)");
  if (!r?.kind) e.push("kind missing");
  const sev = typeof r?.severity === "string" ? r.severity : r?.severity?.value;
  if (sev && !SEVERITY[sev]) e.push(`severity '${sev}' must be info, warning or error`);
  return e;
}

// Severity on the D5 scale; the extraction's own word is kept beside it.
function normaliseSeverity(r) {
  const s = typeof r.severity === "string" ? { value: r.severity, origin: "document" } : { ...(r.severity ?? { value: "info", origin: "default" }) };
  const mapped = SEVERITY[s.value] ?? "info";
  if (mapped !== s.value) s.as_extracted = s.value;
  s.value = mapped;
  r.severity = s;
}

/**
 * Adds or updates records (by id; ids are stable, D4). A changed quote for an existing id is a new revision of the same
 * requirement: the old quote and revision are kept in r.revisions, and any acceptance is cleared so it is confirmed again.
 */
export function upsert(v, records) {
  const byId = new Map(v.reqs.records.map((r) => [r.id, r]));
  const out = { added: 0, updated: 0, unchanged: 0, rejected: [] };
  for (const r0 of records) {
    const errors = validate(r0, v.register.documents);
    if (errors.length) { out.rejected.push({ id: r0?.id, errors }); continue; }
    const r = structuredClone(r0);
    r.doc = { ...(r.doc ?? {}), key: r.doc.key ?? r.doc.vault_id };
    normaliseSeverity(r);
    delete r.status;   // status is a result of a run (status store), never part of the requirement (D5)
    r.source.quote_sha256 ??= sha256(normalise(r.source.quote));
    const old = byId.get(r.id);
    if (!old) { byId.set(r.id, r); out.added++; continue; }
    if (old.source?.quote_sha256 === r.source.quote_sha256 && JSON.stringify({ ...old, review: undefined, revisions: undefined }) === JSON.stringify({ ...r, review: undefined, revisions: undefined })) { out.unchanged++; continue; }
    r.revisions = [...(old.revisions ?? [])];
    if (old.source?.quote_sha256 !== r.source.quote_sha256) {
      r.revisions.push({ revision: old.doc?.revision, quote: old.source?.quote, quote_sha256: old.source?.quote_sha256, replaced: now() });
      delete r.review;
    } else if (old.review) r.review = old.review;
    byId.set(r.id, r); out.updated++;
  }
  v.reqs.records = [...byId.values()].sort((a, b) => a.id.localeCompare(b.id, "en", { numeric: true }));
  return out;
}

/** Imports an extraction file ({ meta: { documents }, records }): registers its documents, then upserts the records. */
export function importFile(v, src) {
  const data = readJson(src);
  if (!data) throw new Error(`Not found: ${src}`);
  const base = path.dirname(src);
  const registered = [];
  for (const [key, d] of Object.entries(data.meta?.documents ?? {})) {
    if (v.register.documents.some((x) => x.key === key)) continue;
    const p = d.path && (path.isAbsolute(d.path) ? d.path : path.join(base, d.path));
    if (!p || !fs.existsSync(p)) { registered.push({ key, outcome: "file not found", path: d.path }); continue; }
    const r = register(v, { key, path: p, type: d.type, title: d.title, number: d.number, revision: d.revision });
    registered.push({ key, outcome: r.outcome });
  }
  return { documents: registered, ...upsert(v, data.records ?? data.requirements ?? []) };
}

export function list(v, { kind, doc, checkability, review, accepted, text, ids, offset = 0, limit = 50 } = {}) {
  let rs = v.reqs.records;
  if (ids?.length) rs = rs.filter((r) => ids.includes(r.id));
  if (kind) rs = rs.filter((r) => r.kind === kind);
  if (doc) rs = rs.filter((r) => r.doc?.key === doc);
  if (checkability) rs = rs.filter((r) => r.test?.checkability === checkability);
  if (review != null) rs = rs.filter((r) => !!r.needs_auditor_review === review);
  if (accepted != null) rs = rs.filter((r) => !!r.review?.accepted_at === accepted);
  if (text) { const q = normalise(text); rs = rs.filter((r) => normalise(`${r.normalized} ${r.source?.quote}`).includes(q)); }
  const count = (f) => Object.entries(v.reqs.records.reduce((m, r) => (m[f(r)] = (m[f(r)] ?? 0) + 1, m), {})).sort((a, b) => b[1] - a[1]);
  return {
    total: v.reqs.records.length, matching: rs.length, offset,
    summary: offset === 0 ? {
      byKind: Object.fromEntries(count((r) => r.kind)), byDocument: Object.fromEntries(count((r) => r.doc?.key)),
      byCheckability: Object.fromEntries(count((r) => r.test?.checkability ?? "-")), bySeverity: Object.fromEntries(count((r) => r.severity?.value)),
      needReview: v.reqs.records.filter((r) => r.needs_auditor_review).length, accepted: v.reqs.records.filter((r) => r.review?.accepted_at).length,
    } : undefined,
    records: rs.slice(offset, offset + limit).map((r) => ({
      id: r.id, kind: r.kind, doc: `${r.doc?.key} ${r.doc?.revision ?? ""}`.trim(), at: locationOf(r), severity: r.severity?.value,
      test: r.test?.checkability, requirement: r.normalized || r.source?.quote, review: r.needs_auditor_review ? (r.review_reasons ?? []).join("; ") || "yes" : undefined,
      accepted: r.review?.accepted_at ? `${r.review.accepted_by} ${r.review.accepted_at.slice(0, 10)}` : undefined,
    })),
  };
}

export const locationOf = (r) => {
  const s = r.source ?? {};
  return [s.clause_ref && `clause ${s.clause_ref}`, s.page != null && `page ${s.page}`, s.table?.sheet && `sheet '${s.table.sheet}' row ${s.table.row_first ?? s.table.row ?? ""}`.trim(),
    !s.clause_ref && s.page == null && !s.table && s.section_path?.length && s.section_path.join(" > ")].filter(Boolean).join(", ");
};

/** Records a decision (DEC-0001 ...): accept, deviation, reject or note. Accepting marks the records accepted (D8 default: anyone, recorded). */
export function decide(v, { ids, kind = "accept", note = "", by }) {
  if (!ids?.length) throw new Error("Give the requirement ids the decision is about.");
  if (!["accept", "deviation", "reject", "note"].includes(kind)) throw new Error("kind: accept, deviation, reject or note");
  const found = v.reqs.records.filter((r) => ids.includes(r.id));
  const unknown = ids.filter((id) => !found.some((r) => r.id === id));
  if (unknown.length) throw new Error(`Unknown requirement ids: ${unknown.slice(0, 10).join(", ")}${unknown.length > 10 ? " ..." : ""}`);
  const n = v.decisions.decisions.length + 1;
  const d = { id: `DEC-${String(n).padStart(4, "0")}`, kind, requirements: ids, note, by: by || "unknown", at: now(),
    // A deviation stays valid while the clause text is unchanged (D19 default): the quotes it was made against.
    quotes: Object.fromEntries(found.map((r) => [r.id, r.source?.quote_sha256])) };
  v.decisions.decisions.push(d);
  if (kind === "accept") for (const r of found) r.review = { accepted_by: d.by, accepted_at: d.at, decision: d.id };
  if (kind === "reject") for (const r of found) r.rejected = d.id;
  return d;
}

/**
 * Checks every record of a document against the document's current text (D4: same ids across revisions): a quote found
 * at the cited place keeps the record; found elsewhere, the record is kept and its new place noted; not found, the record
 * is flagged for the auditor. Only this document is read: unchanged documents cost nothing.
 */
export async function revalidate(v, key) {
  const { doc, file: f } = documentPath(v, key);
  const content = await readDocument(f);
  // Spreadsheet cells are matched as one line of text, so a quote spanning two cells is still found.
  const units = content.units.map((u) => ({ u, n: normalise(u.cells ? Object.values(u.cells).join(" ") : u.text) }));
  const all = units.map((x) => x.n).join(" ");
  const res = { key, revision: doc.revision, checked: 0, atCitedPlace: 0, elsewhere: [], notFound: [] };
  for (const r of v.reqs.records.filter((x) => x.doc?.key === key)) {
    res.checked++;
    const q = normalise(r.source?.quote).replace(/\s*\.\.\.\s*/g, "...");
    const pieces = q.split("...").map((p) => p.trim()).filter((p) => p.length > 3);
    const inText = (t) => pieces.length > 0 && pieces.every((p) => t.includes(p));
    const s = r.source ?? {};
    const cited = units.filter(({ u }) => (s.page != null && u.page === s.page) || (s.table?.sheet && u.sheet === s.table.sheet && u.row >= (s.table.row_first ?? s.table.row ?? 0) && u.row <= (s.table.row_last ?? s.table.row_first ?? s.table.row ?? 0)));
    if (cited.some(({ n }) => inText(n)) || (!cited.length && inText(all) && s.page == null && !s.table)) { res.atCitedPlace++; mark(r, doc, null); continue; }
    const hit = units.find(({ n }) => inText(n));
    if (hit) { res.elsewhere.push({ id: r.id, cited: locationOf(r), found: where(hit.u) }); mark(r, doc, `quote found at ${where(hit.u)}, not at the cited ${locationOf(r)}`); continue; }
    res.notFound.push(r.id); mark(r, doc, `quote not found in ${key} ${doc.revision}`, false);
  }
  if (!res.notFound.length && !res.elsewhere.length) delete doc.changed;
  doc.lastRead = now();
  return res;
}

// A record whose quote is still in the document moves to the current revision; one whose quote is gone stays on the
// revision it was read from until the auditor or a new extraction replaces it.
function mark(r, doc, problem, found = true) {
  if (found) r.doc = { ...r.doc, revision: doc.revision, sha256: doc.sha256 };
  r.source.checked = { at: now(), revision: doc.revision, ok: !problem };
  if (problem) {
    r.needs_auditor_review = true;
    r.review_reasons = [...new Set([...(r.review_reasons ?? []).filter((x) => !/^quote (not )?found/.test(x)), problem])];
  }
}

// ---- index.html: the browsable vault (decided 14 Oct) ---------------------------------------------------------------

/** HTML / XML text: the five specials escaped, control characters XML 1.0 forbids removed. */
export const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]))
  .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, "");

export function writeIndex(v) {
  const docs = v.register.documents, recs = v.reqs.records;
  const count = (k) => recs.filter((r) => r.doc?.key === k).length;
  const audits = fs.existsSync(path.join(v.dir, "04 Audits")) ? fs.readdirSync(path.join(v.dir, "04 Audits")).filter((f) => /\.(html|docx)$/i.test(f)).sort().reverse() : [];
  const byKind = Object.entries(recs.reduce((m, r) => (m[r.kind] = (m[r.kind] ?? 0) + 1, m), {})).sort((a, b) => b[1] - a[1]);
  const link = (d) => { const p = path.isAbsolute(d.file) ? "file:///" + d.file.replace(/\\/g, "/") : d.file.split("/").map(encodeURIComponent).join("/"); return `<a href="${esc(p)}">${esc(d.title || d.file)}</a>`; };
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ACE Vault${v.register.project?.name ? " · " + esc(v.register.project.name) : ""}</title>
<style>body{font:14px/1.45 Segoe UI,Arial,sans-serif;color:#212121;margin:24px;max-width:1100px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;border-bottom:2px solid #EF3340;padding-bottom:4px}
table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #ddd;vertical-align:top}th{background:#f3f3f3}.m{color:#666}.ch{color:#EF3340;font-weight:600}</style></head><body>
<h1>ACE Vault${v.register.project?.name ? " · " + esc(v.register.project.name) : ""}</h1>
<div class="m">${docs.length} documents · ${recs.length} requirements (${recs.filter((r) => r.review?.accepted_at).length} accepted, ${recs.filter((r) => r.needs_auditor_review).length} to review) · ${v.decisions.decisions.length} decisions · updated ${esc(v.register.updated ?? v.register.created)}</div>
<h2>Latest master report</h2>${audits.length ? `<a href="04%20Audits/${encodeURIComponent(audits[0])}">${esc(audits[0])}</a>` : '<div class="m">No audit yet.</div>'}
<h2>Documents</h2><table><tr><th>Key</th><th>Document</th><th>Type</th><th>Number</th><th>Revision</th><th>Status</th><th>Requirements</th></tr>
${docs.map((d) => `<tr><td>${esc(d.key)}</td><td>${link(d)}${d.changed ? ' <span class="ch">changed: check its requirements</span>' : ""}</td><td>${esc(d.type)}</td><td>${esc(d.number)}</td><td>${esc(d.revision)}</td><td>${esc(d.status)}</td><td>${count(d.key)}</td></tr>`).join("\n")}</table>
<h2>Requirements by kind</h2><table><tr><th>Kind</th><th>Records</th></tr>${byKind.map(([k, n]) => `<tr><td>${esc(k)}</td><td>${n}</td></tr>`).join("")}</table>
<p class="m">Files: vault.json (register) · requirements.json (records) · decisions.json (decisions) · project-requirements.json (rules ACE checks)</p></body></html>`;
  fs.writeFileSync(file(v.dir, "index.html"), html);
}

export { save, KINDS_WITHOUT_MODEL_TEST };
