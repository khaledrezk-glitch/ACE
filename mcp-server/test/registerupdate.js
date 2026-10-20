// register_update: a synthetic register built here (TST_ data), checked, then written; the original never changes.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
import ExcelJS from "exceljs";
import { asDate, matchDelivered, reportText, sha, updateRegister } from "../lib/registerupdate.js";

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
  assert.match(t2, /Warning: defined names that point into dropped tabs \(broken in the copy\): TST_Choices/);
  assert.match(t2, /Warning: data validations that point into dropped tabs \(broken in the copy\): A2: TST_LISTS!\$A\$1:\$A\$3/);
  assert.ok(out2.worksheets[0].getCell("C2").value instanceof Date, "the same-day date stays");
  await assert.rejects(updateRegister(fs.readFileSync(f2), { ...o2, id_column: "ZZ" }), /The id column "ZZ" is not in header row 1/, "a letter outside the used columns is refused");
  assert.equal((await updateRegister(fs.readFileSync(f2), { ...o2, id_column: "B" })).report.rows, 4, "a letter inside the used columns works");
}
fs.rmSync(tmp, { recursive: true, force: true });
console.log("registerupdate test passed");
