// register_update: a synthetic register built here (TST_ data), checked, then written; the original never changes.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
import ExcelJS from "exceljs";
import { asDate, checkTemplate, matchDelivered, nearMatches, reportRows, reportText, resolveRegisterFile, sha, splitIds, summaryOf, templateArgs, updateRegister } from "../lib/registerupdate.js";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-reg-"));
const wb = new ExcelJS.Workbook();
const other = wb.addWorksheet("TST_LISTS");
other.getCell("A1").value = "Structure";
const ws = wb.addWorksheet("TST_REG");
ws.getCell("A1").value = "TST deliverables register";
ws.getRow(2).values = ["Doc No", "Title", "Discipline", "Stage 1", "Stage 2"];
const d = (s) => new Date(`${s}T00:00:00Z`);
const rows = [["TST-ST-001", "2026-09-01", "2026-12-01"], ["TST-ST-002", null, null], ["TST-ST-0021", null, null], ["TST-ST-003", "2026-11-15", null]];
rows.forEach(([id, s1, s2], i) => {
  const r = ws.getRow(3 + i);
  r.getCell(1).value = id;
  r.getCell(2).value = { formula: `CONCATENATE('TST_REG'!A${3 + i}," drawing")`, result: `${id} drawing` };
  r.getCell(3).value = { formula: "TST_LISTS!A1", result: "Structure" };
  if (s1) r.getCell(4).value = d(s1);
  if (s2) r.getCell(5).value = d(s2);
});
const src = path.join(tmp, "TST register.xlsx");
await wb.xlsx.writeFile(src);
const before = sha(fs.readFileSync(src));

assert.equal(asDate("20/10/2026").toISOString().slice(0, 10), "2026-10-20");
const m = matchDelivered(["TST-ST-001", "TST-ST-002", "TST-ST-0021"], ["tst-st-001_A.pdf", "TST-ST-0021.dwg", "OTHER-9.pdf"]);
assert.deepEqual([...m.matched.keys()], ["TST-ST-001", "TST-ST-0021"], "contained, case-insensitive; the longer id wins");
assert.deepEqual(m.unmatched, ["OTHER-9.pdf"]);
assert.deepEqual([...matchDelivered(["TST-ST-001"], ["P1_TST-ST-001_R02.pdf"], "_(TST-ST-\\d+)_").matched.keys()], ["TST-ST-001"], "the vault naming regex");

const o = { sheet: "TST_REG", header_row: 2, id_column: "Doc No", stage_columns: ["Stage 1", "Stage 2"], stage: "Stage 1", submission_date: "2026-10-20", label: "SUB 01",
  files: ["tst-st-001_A.pdf", "TST-ST-002 rev B.pdf", "UNKNOWN-9.pdf"] };
const { wb: out, report } = await updateRegister(fs.readFileSync(src), o);
assert.deepEqual(report.matched.map((x) => x.id), ["TST-ST-001", "TST-ST-002"]);
assert.deepEqual(report.deliveredNotInRegister, ["UNKNOWN-9.pdf"]);
assert.deepEqual(report.notDelivered, ["TST-ST-0021", "TST-ST-003"]);
assert.deepEqual(report.cleared, [{ id: "TST-ST-001", column: "Stage 2", was: "2026-12-01" }, { id: "TST-ST-003", column: "Stage 1", was: "2026-11-15" }]);
assert.deepEqual([report.formulas.renamed, report.formulas.toValues], [4, 4]);
const txt = reportText(report, { mode: "check" });
assert.match(txt, /^REGISTER UPDATE TST_REG -> "SUB 01" for the submission of 2026-10-20 \(check, nothing written\)\./);
assert.match(txt, /Matched \(delivered, date set\): 2: TST-ST-001 <- tst-st-001_A\.pdf; TST-ST-002 <- TST-ST-002 rev B\.pdf\./);
assert.match(txt, /Dates later than the submission, cleared: 2: TST-ST-001 Stage 2 2026-12-01; TST-ST-003 Stage 1 2026-11-15\./);
assert.match(txt, /Formulas: 4 now name "SUB 01"; 4 pointed into other tabs and became their values/);

const dst = path.join(tmp, "TST register SUB 01.xlsx");
await out.xlsx.writeFile(dst);
assert.equal(sha(fs.readFileSync(src)), before, "the original is never written");
const back = new ExcelJS.Workbook();
await back.xlsx.readFile(dst);
assert.deepEqual(back.worksheets.map((w) => w.name), ["SUB 01"], "one tab, renamed; the other tabs dropped");
const s = back.worksheets[0];
assert.equal(s.getCell("B3").formula, "CONCATENATE('SUB 01'!A3,\" drawing\")", "a formula naming the sheet follows the new name");
assert.equal(s.getCell("C3").value, "Structure", "a formula into a dropped tab became its value");
assert.equal(s.getCell("D3").value.toISOString().slice(0, 10), "2026-10-20");
assert.equal(s.getCell("D4").value.toISOString().slice(0, 10), "2026-10-20");
assert.equal(s.getCell("E3").value, null, "a later date cleared");
assert.equal(s.getCell("D6").value, null);
await assert.rejects(updateRegister(fs.readFileSync(src), { ...o, stage: undefined }), /Give stage/);
await assert.rejects(updateRegister(fs.readFileSync(src), { ...o, label: "a/b" }), /cannot be a sheet name/);

// Review fixes: header text before column letters ("ID", "IFC"), letters only inside the used columns, whole days,
// impossible dates, formula dates left and reported, merged cells / defined names / validations into dropped tabs.
{
  const w2 = new ExcelJS.Workbook();
  const lists = w2.addWorksheet("TST_LISTS");
  lists.getCell("A1").value = "Yes";
  const s2 = w2.addWorksheet("TST_REG2");
  s2.getRow(1).values = ["Title", "ID", "IFC", "Stage 2"];
  const rowsB = [["a", "TST-A-001", new Date("2026-10-20T15:30:00Z"), null], ["b", "TST-A-002", null, { formula: "TODAY()+30", result: new Date("2026-11-19T00:00:00Z") }],
    ["c", "TST-A-003", "31/02/2026", null], ["d", "TST-A-004", null, null]];
  rowsB.forEach((v, i) => { s2.getRow(2 + i).values = v; });
  s2.mergeCells("D5:D6");
  s2.getCell("A2").dataValidation = { type: "list", allowBlank: true, formulae: ["TST_LISTS!$A$1:$A$3"] };
  w2.definedNames.add("TST_LISTS!$A$1:$A$3", "TST_Choices");
  const f2 = path.join(tmp, "TST register 2.xlsx");
  await w2.xlsx.writeFile(f2);
  const o2 = { sheet: "TST_REG2", header_row: 1, id_column: "ID", stage_columns: ["IFC", "Stage 2"], stage: "IFC", submission_date: "2026-10-20", label: "SUB 02", files: ["TST-A-004.pdf"] };
  const { wb: out2, report: r2 } = await updateRegister(fs.readFileSync(f2), o2);
  assert.equal(r2.stageColumn, "IFC", "the header IFC, not column IFC");
  assert.deepEqual(r2.matched.map((x) => [x.id, x.column]), [["TST-A-004", "IFC"]], "the header ID, not column ID");
  assert.deepEqual(r2.cleared, [], "a same-day date with a time is not cleared; a formula date is not cleared");
  assert.deepEqual(r2.formulaDates, [{ id: "TST-A-002", column: "Stage 2", was: "2026-11-19" }]);
  assert.deepEqual(r2.notDates, ["TST-A-003 IFC: 31/02/2026"], "an impossible date is not a date");
  assert.equal(asDate("31/02/2026"), null);
  assert.equal(asDate("2026-02-30"), null);
  assert.deepEqual(r2.merged, ["D5:D6"]);
  assert.ok(r2.definedNames.some((x) => x.startsWith("TST_Choices")) && r2.validations.some((x) => x.startsWith("A2:")), "names and validations into dropped tabs");
  const t2 = reportText(r2, { mode: "check" });
  assert.match(t2, /Formula date later than submission, left \(computed, not cleared\): 1: TST-A-002 Stage 2 2026-11-19\./);
  assert.match(t2, /Merged cells in the id or stage columns: D5:D6 \(the write refuses unless allow_merged/);
  assert.match(t2, /Warning: defined names that point into dropped tabs \(broken in the copy\): 1: TST_Choices/);
  assert.match(t2, /Warning: data validations that point into dropped tabs \(broken in the copy\): 1: A2: TST_LISTS!\$A\$1:\$A\$3/);
  assert.ok(out2.worksheets[0].getCell("C2").value instanceof Date, "the same-day date stays");
  await assert.rejects(updateRegister(fs.readFileSync(f2), { ...o2, id_column: "ZZ" }), /The id column "ZZ" is not in header row 1/, "a letter outside the used columns is refused");
  assert.equal((await updateRegister(fs.readFileSync(f2), { ...o2, id_column: "B" })).report.rows, 4, "a letter inside the used columns works");
}

// 1.12.7: headings skipped, several stage columns, near matches (confirm), a wrong id column blocked.
{
  const w3 = new ExcelJS.Workbook();
  const s3 = w3.addWorksheet("TST_REG3");
  s3.getRow(1).values = ["Doc", "Code", "Planned IFC", "Actual IFC"];
  const ids = ["TST-P1-AR-ZZ-DR-100001", "TST-P1-AR-ZZ-DR-100002", "TST-P1-ST-ZZ-RP-900400", "TST-P1-ST-ZZ-DR-100004"];
  [["MODELS"], ...ids.slice(0, 2).map((id) => [id, "010"]), ["DOCUMENTS, SCHEDULES AND REPORTS"], ...ids.slice(2).map((id) => [id, "010"])]
    .forEach((v, i) => { s3.getRow(2 + i).values = v; });
  const f3 = path.join(tmp, "TST register 3.xlsx");
  await w3.xlsx.writeFile(f3);
  const o3 = { id_column: "Doc", stage_columns: ["Planned IFC", "Actual IFC"], stage: ["Planned IFC", "Actual IFC"], submission_date: "2026-10-20", label: "SUB 03",
    files: ["TST-P1-AR-ZZ-DR-100001_C01.pdf", "TST-P1-ST-XX1-RP-900400.pdf"] };
  const { wb: out3, report: r3 } = await updateRegister(fs.readFileSync(f3), o3);
  // 1. Headings are not ids.
  assert.equal(r3.rows, 4);
  assert.deepEqual(r3.skipped, [{ row: 2, value: "MODELS" }, { row: 5, value: "DOCUMENTS, SCHEDULES AND REPORTS" }]);
  assert.ok(!r3.notDelivered.includes("MODELS"));
  assert.deepEqual(splitIds([{ id: "TST-1" }, { id: "Section A" }, { id: "TSTX9" }], "^TST-\\d+$").rows.map((x) => x.id), ["TST-1"], "id_pattern decides");
  const shapeKept = splitIds(["ABC1", "ABD2", "ABE3", "Heading"].map((id) => ({ id }))).rows.map((x) => x.id);
  assert.deepEqual(shapeKept, ["ABC1", "ABD2", "ABE3"], "the shape most rows share keeps ids without a separator");
  // 2. One date into several columns.
  assert.deepEqual(r3.matched.map((x) => [x.id, x.column]), [["TST-P1-AR-ZZ-DR-100001", "Planned IFC"], ["TST-P1-AR-ZZ-DR-100001", "Actual IFC"]]);
  assert.ok(out3.worksheets[0].getCell("C3").value instanceof Date && out3.worksheets[0].getCell("D3").value instanceof Date);
  // 3. Near match: reported once as "probably the same", in neither unrelated list; set only with accept_near.
  assert.deepEqual(r3.near, [{ id: "TST-P1-ST-ZZ-RP-900400", file: "TST-P1-ST-XX1-RP-900400.pdf", differs: "XX1 vs ZZ" }]);
  assert.ok(!r3.notDelivered.includes("TST-P1-ST-ZZ-RP-900400") && !r3.deliveredNotInRegister.length);
  assert.match(reportText(r3, { mode: "check" }), /Probably the same, confirm \(one naming field differs; set only with accept_near: \[ids\]\): 1: TST-P1-ST-ZZ-RP-900400 ~ TST-P1-ST-XX1-RP-900400\.pdf \(XX1 vs ZZ\)\.\nHeadings\/skipped \(not ids\): 2: row 2 "MODELS"; row 5 "DOCUMENTS, SCHEDULES AND REPORTS"\./);
  assert.deepEqual(nearMatches(["TST-A-B-123456"], ["TST-X-Y-123456.pdf"]), [], "two fields differ: not near");
  const { report: r3b } = await updateRegister(fs.readFileSync(f3), { ...o3, accept_near: ["TST-P1-ST-ZZ-RP-900400"] });
  assert.ok(r3b.matched.some((x) => x.id === "TST-P1-ST-ZZ-RP-900400" && x.near) && !r3b.near.length && r3b.nearAccepted.length === 1, "accepted near matches are set");
  // 4. A wrong id column fails fast (check too): short, duplicated values; force_id_column overrides.
  await assert.rejects(updateRegister(fs.readFileSync(f3), { ...o3, id_column: "Code", files: ["TST-010-A.pdf"] }), /The id column looks wrong \(Code\): the ids are short \(median 3 characters\); 4 of 4 rows share an id with another row\. Sample values: 010\./);
  const forced = await updateRegister(fs.readFileSync(f3), { ...o3, id_column: "Code", files: ["TST-010-A.pdf"], force_id_column: true });
  assert.equal(forced.report.rows, 4, "force_id_column: the column is used as given");
  const w4 = new ExcelJS.Workbook();
  const s4 = w4.addWorksheet("TST_REG4");
  s4.getRow(1).values = ["Doc", "Stage"];
  ["TST-DOC-001", "TST-DOC-002", "TST-DOC-003"].forEach((id, i) => { s4.getRow(2 + i).values = [id]; });
  const f4 = path.join(tmp, "TST register 4.xlsx");
  await w4.xlsx.writeFile(f4);
  await assert.rejects(updateRegister(fs.readFileSync(f4), { id_column: "Doc", stage_columns: ["Stage"], submission_date: "2026-10-20", label: "S", files: ["TST-DOC-001+TST-DOC-002.pdf"] }),
    /looks wrong \(Doc\): 1 delivered file\(s\) match more than one id \(e\.g\. TST-DOC-001\+TST-DOC-002\.pdf -> TST-DOC-001, TST-DOC-002\)/);
}

// 1.12.8: a heading with an id-like number ("000-GENERAL") is skipped; an id listed twice is one entry everywhere.
{
  const w5 = new ExcelJS.Workbook();
  const s5 = w5.addWorksheet("TST_REG5");
  s5.getRow(1).values = ["Doc", "Stage"];
  ["000-GENERAL", "TST-P1-AR-ZZ-DR-100001", "TST-P1-AR-ZZ-DR-100002", "TST-P1-ST-ZZ-RP-900400", "TST-P1-ST-ZZ-RP-900400", "TST-P1-AR-ZZ-DR-100009", "TST-P1-AR-ZZ-DR-100009", "TST-P1-AR-ZZ-DR-100010",
    ...Array.from({ length: 20 }, (_, i) => `TST-P1-AR-ZZ-DR-2000${10 + i}`)]
    .forEach((id, i) => { s5.getRow(2 + i).values = [id]; });
  const f5 = path.join(tmp, "TST register 5.xlsx");
  await w5.xlsx.writeFile(f5);
  const { report: r5 } = await updateRegister(fs.readFileSync(f5), { id_column: "Doc", stage_columns: ["Stage"], submission_date: "2026-10-20", label: "S5",
    files: ["TST-P1-AR-ZZ-DR-100001.pdf", "TST-P1-ST-XX1-RP-900400.pdf"], force_id_column: false });
  assert.deepEqual(r5.skipped, [{ row: 2, value: "000-GENERAL" }], "a heading with fewer fields and a word is skipped");
  assert.deepEqual(r5.near.map((x) => x.id), ["TST-P1-ST-ZZ-RP-900400"], "listed once");
  assert.deepEqual(r5.notDelivered.slice(0, 3), ["TST-P1-AR-ZZ-DR-100002", "TST-P1-AR-ZZ-DR-100009", "TST-P1-AR-ZZ-DR-100010"], "listed once");
  assert.equal(r5.notDelivered.length, 23);
  const t5 = reportText(r5, { mode: "check" });
  assert.match(t5, /TST-P1-ST-ZZ-RP-900400 \(x2 in register\) ~ TST-P1-ST-XX1-RP-900400\.pdf/);
  assert.match(t5, /In the register but not delivered: 23: TST-P1-AR-ZZ-DR-100002; TST-P1-AR-ZZ-DR-100009 \(x2 in register\); TST-P1-AR-ZZ-DR-100010;/);
}

// 1.12.9: the project template (vault rules/register.json): discipline, stage name -> columns, validation.
{
  const ex = JSON.parse(fs.readFileSync(new URL("../bep/register.example.json", import.meta.url), "utf8"));
  checkTemplate(ex, "example");
  const tdir = path.join(tmp, "tpl", "ACE Vault");
  fs.mkdirSync(path.join(tmp, "tpl", "TST_Registers"), { recursive: true });
  fs.writeFileSync(path.join(tmp, "tpl", "TST_Registers", "TST-MIDP-R03.xlsx"), "x");
  fs.mkdirSync(tdir, { recursive: true });
  const t = templateArgs(ex, { discipline: "ar", stage: "ifc" }, { file: "register.json", baseDir: tdir });
  assert.equal(t.discipline, "AR");
  assert.equal(t.args.register, path.join(tmp, "tpl", "TST_Registers", "TST-MIDP-R03.xlsx"), "the glob finds the one register");
  assert.deepEqual([t.args.sheet, t.args.header_row, t.args.id_column], ["TST_ARCHITECTURE", 4, "Document Number"], "a discipline overrides headerRow");
  assert.deepEqual(t.args.stage, ["Planned Date#10", "Actual Date#10"], "a stage name becomes its columns");
  assert.equal(t.args.stage_columns.length, 4);
  assert.throws(() => templateArgs(ex, { discipline: "ME", stage: "IFC" }, { file: "register.json", baseDir: tdir }), /Discipline "ME" is not in register\.json: known ST, AR\./);
  assert.throws(() => templateArgs(ex, { discipline: "ST", stage: "TENDER" }, { file: "register.json", baseDir: tdir }), /Stage "TENDER" is not in register\.json for ST: known CONCEPT, IFC\./);
  assert.ok(!("stage" in templateArgs(ex, { discipline: "ST", stage: "Actual Date#10" }, { file: "f", baseDir: tdir }).args), "a column name stays the call's own");
  assert.throws(() => checkTemplate({ ...ex, sheets: 1 }, "f"), /unknown key "sheets"/);
  assert.throws(() => checkTemplate({ disciplines: { ST: {} } }, "f"), /disciplines\.ST needs sheet/);
  assert.throws(() => checkTemplate({ ...ex, stages: { IFC: "Actual" } }, "f"), /stages\.IFC must be a list of column names/);
  fs.writeFileSync(path.join(tmp, "tpl", "TST_Registers", "TST-MIDP-R04.xlsx"), "x");
  assert.throws(() => resolveRegisterFile("../TST_Registers/TST-MIDP-*.xlsx", tdir), /matches 2 files/);
}
// 1.14.2: title-block merges above the header row are ignored (counted); only merges in the data rows of the id / stage
// columns block. The reply is counts + the first 10 of each list; the report file holds every item.
{
  const w6 = new ExcelJS.Workbook();
  const s6 = w6.addWorksheet("TST_REG6");
  s6.getCell("A1").value = "TST MIDP";
  for (const rg of ["A2:B2", "B3:B10", "B11:E13", "AI11:AP11"]) s6.mergeCells(rg);
  s6.getRow(14).values = ["Doc", "Title", "Stage 1", "Stage 2"];
  const ids6 = Array.from({ length: 30 }, (_, i) => `TST-P1-AR-ZZ-DR-3000${10 + i}`);
  ids6.forEach((id, i) => { s6.getRow(15 + i).values = [id, "t", null, new Date("2026-12-01T00:00:00Z")]; });
  s6.mergeCells("C16:C17"); s6.mergeCells("E16:F16");
  const f6 = path.join(tmp, "TST register 6.xlsx");
  await w6.xlsx.writeFile(f6);
  const o6 = { header_row: 14, id_column: "Doc", stage_columns: ["Stage 1", "Stage 2"], stage: "Stage 1", submission_date: "2026-10-20", label: "S6",
    files: [...ids6.slice(0, 25).map((id) => `${id}.pdf`), ...Array.from({ length: 12 }, (_, i) => `TST-OTHER-${i}.pdf`)], force_id_column: true };
  const { report: r6 } = await updateRegister(fs.readFileSync(f6), o6);
  assert.deepEqual(r6.merged, ["C16:C17"], "only a merge in the data rows of an id / stage column blocks");
  assert.equal(r6.mergedIgnored, 5, "title-block merges above the header row (and other columns) are ignored");
  const t6 = reportText(r6, { mode: "check", reportFile: "TST check report.csv" });
  assert.match(t6, /Merged cells in the id or stage columns: C16:C17 \(the write refuses/);
  assert.match(t6, /Merged cells elsewhere \(title block, other columns\) ignored: 5\./);
  assert.match(t6, /Matched \(delivered, date set\): 25: (?:[^;]+; ){9}[^;]+; \.\.\. 15 more in TST check report\.csv\./, "the first 10, then N more in the report");
  assert.match(t6, /Dates later than the submission, cleared: 30: (?:[^;]+; ){9}[^;]+; \.\.\. 20 more in TST check report\.csv\./);
  assert.match(t6, /Delivered but not in the register: 12: (?:[^;]+; ){9}[^;]+; \.\.\. 2 more in TST check report\.csv\./);
  const sm = summaryOf(r6, "TST check report.csv");
  assert.ok(sm.counts.matched === 25 && sm.first.matched.length === 10 && sm.counts.cleared === 30 && sm.first.cleared.length === 10 && sm.report === "TST check report.csv" && !("matched" in sm && Array.isArray(sm.matched)), "the reply's data: counts and the first 10");
  assert.ok(JSON.stringify(sm).length < JSON.stringify(r6).length / 2, "the reply is much smaller than the full report");
  const rows6 = reportRows(r6);
  assert.equal(rows6.filter((x) => x[0] === "matched").length, 25, "the report file holds every item");
  assert.equal(rows6.filter((x) => x[0] === "date cleared (later than the submission)").length, 30);
  assert.ok(rows6.some((x) => x[0] === "merged cells in id / stage columns" && x[1] === "C16:C17"));
}
fs.rmSync(tmp, { recursive: true, force: true });
console.log("registerupdate test passed");
