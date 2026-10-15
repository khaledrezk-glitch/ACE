// Vault test (no Revit, no network): document readers, register, records, decisions, revalidation across revisions.
// Synthetic documents only; client documents never go in this repository.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import assert from "node:assert/strict";
import * as Vault from "../lib/vault.js";
import { readDocx, readXlsx, normalise, textOf, unzip } from "../lib/docs.js";
import * as Report from "../lib/report.js";

// A minimal zip writer (deflated entries), enough to make DOCX and XLSX files for the readers.
function zip(files) {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (b) => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const locals = [], centrals = []; let offset = 0;
  for (const [name, content] of Object.entries(files)) {
    const raw = Buffer.from(content), data = zlib.deflateRawSync(raw), n = Buffer.from(name);
    const h = Buffer.alloc(30); h.writeUInt32LE(0x04034b50, 0); h.writeUInt16LE(20, 4); h.writeUInt16LE(8, 8); h.writeUInt32LE(crc(raw), 14); h.writeUInt32LE(data.length, 18); h.writeUInt32LE(raw.length, 22); h.writeUInt16LE(n.length, 26);
    const c = Buffer.alloc(46); c.writeUInt32LE(0x02014b50, 0); c.writeUInt16LE(20, 4); c.writeUInt16LE(20, 6); c.writeUInt16LE(8, 10); c.writeUInt32LE(crc(raw), 16); c.writeUInt32LE(data.length, 20); c.writeUInt32LE(raw.length, 24); c.writeUInt16LE(n.length, 28); c.writeUInt32LE(offset, 42);
    locals.push(h, n, data); centrals.push(c, n); offset += 30 + n.length + data.length;
  }
  const cd = Buffer.concat(centrals), e = Buffer.alloc(22);
  e.writeUInt32LE(0x06054b50, 0); e.writeUInt16LE(centrals.length / 2, 8); e.writeUInt16LE(centrals.length / 2, 10); e.writeUInt32LE(cd.length, 12); e.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, e]);
}
const para = (t, style) => `<w:p>${style ? `<w:pPr><w:pStyle w:val="${style}"/></w:pPr>` : ""}<w:r><w:t xml:space="preserve">${t}</w:t></w:r></w:p>`;
const docx = (body) => zip({ "word/document.xml": `<?xml version="1.0"?><w:document xmlns:w="x"><w:body>${body}</w:body></w:document>` });

// ---- readers ----------------------------------------------------------------------------------------------------
const d = readDocx(docx(para("Information standard", "Heading1") + para("All containers shall be named using the fields &amp; codes.") +
  `<w:tbl><w:tr><w:tc>${para("Field")}</w:tc><w:tc>${para("Code")}</w:tc></w:tr><w:tr><w:tc>${para("Originator")}</w:tc><w:tc>${para("ACE")}</w:tc></w:tr></w:tbl>` +
  `<w:p><w:del><w:r><w:delText>old words</w:delText></w:r></w:del><w:ins><w:r><w:t>New words.</w:t></w:r></w:ins></w:p>`));
assert.equal(d.units[1].text, "All containers shall be named using the fields & codes.", "docx paragraph text, entities decoded");
assert.deepEqual(d.units[1].section, ["Information standard"], "docx heading path");
assert.equal(d.units.find((u) => u.table && /Originator/.test(u.text)).text, "Originator | ACE", "docx table row");
assert.ok(d.trackedChanges && !d.units.some((u) => /old words/.test(u.text)), "unaccepted deletions are flagged and not read as text");

const x = readXlsx(zip({
  "xl/workbook.xml": `<workbook><sheets><sheet name="LOIN" sheetId="1" r:id="rId1"/><sheet name="Old" sheetId="2" r:id="rId2" state="hidden"/></sheets></workbook>`,
  "xl/_rels/workbook.xml.rels": `<Relationships><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>`,
  "xl/sharedStrings.xml": `<sst><si><t>Doors</t></si><si><r><t>Fire </t></r><r><t>Rating</t></r></si></sst>`,
  "xl/worksheets/sheet1.xml": `<worksheet><sheetData><row r="4"><c r="A4" t="s"><v>0</v></c><c r="B4" t="s"><v>1</v></c><c r="C4"><v>2</v></c></row></sheetData></worksheet>`,
  "xl/worksheets/sheet2.xml": `<worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>legacy</t></is></c></row></sheetData></worksheet>`,
}));
assert.deepEqual(x.units[0], { sheet: "LOIN", row: 4, hiddenSheet: undefined, cells: { A: "Doors", B: "Fire Rating", C: "2" }, text: "Doors | Fire Rating | 2" }, "xlsx row with shared and rich strings");
assert.ok(x.sheets.find((s) => s.name === "Old").hidden && x.units[1].hiddenSheet, "hidden sheets are flagged");
assert.equal(textOf(Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from("# Revit shared parameters", "utf16le")])), "# Revit shared parameters", "UTF-16 text files (shared parameter files)");
assert.equal(normalise("The “Lead”  Appointed­Party – shall"), "the \"lead\" appointedparty - shall", "normalisation");

// ---- vault ------------------------------------------------------------------------------------------------------
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-vault-"));
const dir = Vault.vaultDir(tmp);
assert.equal(Vault.vaultDir(dir), dir, "a path that already ends in ACE Vault is the vault");
const init = Vault.init(dir, { project: "Test project" });
assert.ok(init.created && Vault.FOLDERS.every((f) => fs.existsSync(path.join(dir, f))) && fs.existsSync(path.join(dir, "project-requirements.json")), "init makes the folders, the files and a project requirements file");
const eir = path.join(dir, "01 Requirements", "EIR.docx");
fs.writeFileSync(eir, docx(para("Information standard", "Heading1") + para("All containers shall be named using the fields Project-Originator-Zone.") + para("Doors shall carry a Fire Rating.")));
let v = Vault.load(dir);
assert.equal(Vault.register(v, { key: "EIR", path: eir, type: "EIR", revision: "A" }).outcome, "registered");
assert.equal(Vault.register(v, { key: "EIR", path: eir, revision: "A" }).outcome, "unchanged", "the same bytes are not a new revision");
assert.throws(() => Vault.register(v, { key: "eir!", path: eir }), /capitals/);
Vault.save(v);
assert.deepEqual(Vault.scan(v), { unregistered: [], changed: [], missing: [] });

const rec = (id, quote, extra = {}) => ({ id, doc: { key: "EIR", revision: "A" }, source: { section_path: ["Information standard"], paragraph_index: 2, quote }, kind: "naming", severity: { value: "major", origin: "default" }, status: { value: "not assessed" }, normalized: quote, ...extra });
const up = Vault.upsert(v, [rec("REQ-EIR-0001", "All containers shall be named using the fields Project-Originator-Zone."), rec("REQ-EIR-0002", "Doors shall carry a Fire Rating.", { kind: "loin" }),
  { id: "REQ-EIR-0003", doc: { key: "EIR" }, source: { quote: "" }, kind: "naming" }, rec("REQ-BEP-0001", "x", { doc: { key: "BEP" } })]);
assert.equal(up.added, 2, "valid records are added");
assert.deepEqual(up.rejected.map((r) => r.id), ["REQ-EIR-0003", "REQ-BEP-0001"], "records without a quote, a location or a registered document are rejected");
const r1 = v.reqs.records.find((r) => r.id === "REQ-EIR-0001");
assert.equal(r1.severity.value, "error", "major -> error (severity scale info / warning / error, D5)");
assert.equal(r1.severity.as_extracted, "major", "the extraction's own word is kept");
assert.equal(r1.status, undefined, "status is not stored on a requirement (it is a run result, D5)");

const dec = Vault.decide(v, { ids: ["REQ-EIR-0001", "REQ-EIR-0002"], kind: "accept", by: "auditor" });
assert.equal(dec.id, "DEC-0001");
assert.ok(v.reqs.records.every((r) => r.review?.accepted_by === "auditor"), "accepting marks the records, with who");
assert.throws(() => Vault.decide(v, { ids: ["REQ-EIR-9999"] }), /Unknown/);
Vault.save(v);
assert.equal((await Vault.revalidate(v, "EIR")).atCitedPlace, 2, "both quotes found in revision A");

// Revision B: the naming clause is reworded, the door clause is unchanged.
fs.writeFileSync(eir, docx(para("Information standard", "Heading1") + para("Containers shall follow the naming convention in Appendix A.") + para("Doors shall carry a Fire Rating.")));
v = Vault.load(dir);
assert.deepEqual(Vault.scan(v).changed, ["EIR"], "scan sees the changed bytes");
const reg = Vault.register(v, { key: "EIR", path: eir, revision: "B" });
assert.equal(reg.outcome, "new revision"); assert.equal(reg.records, 2);
const rv = await Vault.revalidate(v, "EIR");
assert.deepEqual(rv.notFound, ["REQ-EIR-0001"], "the reworded clause is flagged, the unchanged one kept");
assert.ok(v.reqs.records.find((r) => r.id === "REQ-EIR-0001").needs_auditor_review, "flagged for the auditor");
assert.equal(v.reqs.records.find((r) => r.id === "REQ-EIR-0002").doc.revision, "B", "kept records move to the new revision (same id, D4)");
const re = Vault.upsert(v, [rec("REQ-EIR-0001", "Containers shall follow the naming convention in Appendix A.", { doc: { key: "EIR", revision: "B" } })]);
const r1b = v.reqs.records.find((r) => r.id === "REQ-EIR-0001");
assert.ok(re.updated === 1 && r1b.revisions.length === 1 && r1b.revisions[0].revision === "A", "a new quote for the same id keeps the old one as a revision");
assert.equal(r1b.review, undefined, "a changed requirement must be accepted again");
assert.equal(v.reqs.records.find((r) => r.id === "REQ-EIR-0002").review.accepted_by, "auditor", "an unchanged one stays accepted");
Vault.save(v);
const listed = Vault.list(Vault.load(dir), { kind: "loin" });
assert.ok(listed.matching === 1 && listed.summary.bySeverity.error === 2, "list filters and summarises");
const read = await Vault.read(Vault.load(dir), "EIR", { find: "fire rating" });
assert.ok(read.text.length === 1 && read.text[0].section === "Information standard", "read finds a phrase with its section");
assert.ok(fs.readFileSync(path.join(dir, "index.html"), "utf8").includes("Test project"), "index.html is written");
assert.ok(!Vault.trusted(tmp, dir)); Vault.trust(tmp, dir); assert.ok(Vault.trusted(tmp, dir), "trust is remembered per vault");

// Master report (V4): built from a run record; the second report says what closed since the first.
const runDir = path.join(dir, "04 Audits", "model-a", "runs");
fs.mkdirSync(runDir, { recursive: true });
const run1 = { run: { id: "20261015T080000Z-aaaa", model: "Tower A.rvt", time: "2026-10-15T08:00:00Z", user: "tester", score: 70 },
  results: [{ requirement: "REQ-EIR-0001", status: "not-met", severity: "error", evidence: "Doors narrower than 900 mm: 3 of 40 (fail)", binding: "C1:checks/doors" }],
  checks: [{ key: "doors", area: "Rooms and doors", name: "Doors narrower than 900 mm", count: 3, of: 40, status: "fail", detail: "3 doors under 900 mm & <check>" }] };
fs.writeFileSync(path.join(runDir, "20261015T080000Z-aaaa.json"), JSON.stringify(run1));
assert.equal(Report.runs(dir)[0].modelKey, "model-a", "run records are found per model");
let vr = Vault.load(dir);
const rep1 = Report.build(vr, JSON.parse(fs.readFileSync(Report.runs(dir)[0].file, "utf8")), { by: "auditor", date: new Date("2026-10-15T09:00:00Z") });
assert.equal(rep1.name, "Tower A_audit_2026-10-15_R01");
assert.ok(rep1.summary.notMet === 1 && rep1.summary.notEvaluated === 1 && rep1.summary.trend === null, "every vault requirement is in the matrix; one the run did not cover is 'not evaluated'");
assert.ok(rep1.html.includes("REQ-EIR-0002") && rep1.html.includes("&amp; &lt;check&gt;") && !rep1.html.includes("<check>"), "HTML lists every requirement and escapes text");
const parts = unzip(rep1.docx);
assert.ok(parts.names.includes("word/document.xml") && parts.read("word/document.xml").includes("Compliance matrix") && parts.read("word/document.xml").includes("&amp; &lt;check&gt;"), "the Word file is a valid zip with the report");
assert.ok(rep1.csv.startsWith("\uFEFFRequirement,Severity") && rep1.csv.includes("REQ-EIR-0001,error,Not met"), "actions CSV, owner and due left blank for the team");
const files = Report.save(vr, rep1);
assert.ok(fs.existsSync(files.docx) && fs.existsSync(files.record), "report files saved in 04 Audits");
Vault.writeIndex(vr);
assert.ok(fs.readFileSync(path.join(dir, "index.html"), "utf8").includes("Tower A_audit_2026-10-15_R0"), "the vault index links the latest report");
const run2 = { ...run1, run: { ...run1.run, id: "20261015T100000Z-bbbb", score: 80 }, results: [{ ...run1.results[0], status: "met" }] };
const rep2 = Report.build(vr, run2, { date: new Date("2026-10-15T11:00:00Z") });
assert.ok(rep2.rev === "R02" && rep2.summary.trend.closed === 1 && rep2.summary.trend.new === 0, "the next report counts what closed since the last one");
assert.ok(rep2.html.includes("last audit: 70"), "score trend");
fs.writeFileSync(path.join(dir, "04 Audits", "Tower A_audit_X_audit_2026-10-15_R07.json"), JSON.stringify({ statuses: {}, documents: {} }));
assert.equal(Report.build(vr, run2, {}).rev, "R02", "another model whose name starts the same is not counted");
fs.rmSync(tmp, { recursive: true, force: true });
console.log("vault test passed");
