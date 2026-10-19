// Unit tests for the execution stage (lib/paramexec.js) and the 1.11 additions to the sources and actions (duplicate
// GUIDs in a client file, generic role codes, group discipline scope, parked scope defects, built-ins).
// Synthetic TST_ data only. Usage: node test/paramexec.js
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import * as X from "../lib/paramexec.js";
import * as S from "../lib/paramsources.js";
import * as A from "../lib/paramactions.js";
import { rowsFromCsv } from "../lib/paramlist.js";

// ---- derive rules ------------------------------------------------------------------------------------------------
assert.deepEqual(X.parseFormula("TST_C", "concat with '-': TST_A, TST_B, TST_D (AIS B06 = A02+A04)"), { target: "TST_C", join: ["TST_A", "TST_B", "TST_D"], separator: "-" }, "a join formula with a trailing note");
assert.deepEqual(X.parseFormula("TST_C", "join: TST_A, TST_B"), { target: "TST_C", join: ["TST_A", "TST_B"], separator: "" });
assert.deepEqual(X.parseFormula("TST_C", "copy from TST_A"), { target: "TST_C", copyFrom: "TST_A" });
assert.equal(X.parseFormula("TST_C", "first three letters of TST_A"), null, "anything else is not machine-readable");
assert.equal(X.parseFormula("TST_C", "concat: TST_A + TST_B"), null);

const proj = X.deriveRules({ derive: { twins: { _rule: "x", pairs: [["TST_GN_Plot", "TST_GEN_Plot"]] }, "TST_GEN_Code": "concat with '-': TST_GEN_A, TST_GEN_B", "TST_GEN_Odd": "the plot's code in words", status: "proposed" } });
assert.equal(proj.approved, false, "proposed derive rules are not approved");
assert.equal(proj.groups.length, 2);
assert.deepEqual(proj.groups[0].rules, [{ target: "TST_GN_Plot", copyFrom: "TST_GEN_Plot" }, { target: "TST_GEN_Plot", copyFrom: "TST_GN_Plot" }], "a twin pair: the blank side takes the filled one");
assert.deepEqual(proj.groups[1].rules, [{ target: "TST_GEN_Code", join: ["TST_GEN_A", "TST_GEN_B"], separator: "-" }]);
assert.deepEqual(proj.problems.map((p) => [p.target, p.reason]), [["TST_GEN_Odd", "rule not machine-readable"]]);
assert.equal(X.deriveRules({ derive: { twins: [["TST_A", "TST_B"]], status: "Approved by BIM manager" } }).approved, true, "twins as a plain list; approved status");
const ace = X.deriveRules({ derive: [{ target: "TST_A", copyFrom: "TST_B" }, { target: "TST_A", join: [] }] });
assert.ok(ace.approved && ace.groups.length === 1 && ace.problems.length === 1, "ACE shape: approved unless the mapping is a draft");
assert.equal(X.deriveRules({ draft: true, derive: [{ target: "TST_A", copyFrom: "TST_B" }] }).approved, false, "a draft mapping's rules are not approved");
assert.equal(X.deriveRules({ derive: proj.groups }).groups.length, 0, "objects that are not rules are reported, not run");
assert.equal(X.deriveRules({ derive: { twins: { pairs: [["TST_GN_Plot", "TST_GEN_Plot"]] } } }).groups[0].id, proj.groups[0].id, "the same rule keeps its id");

// Literal Project Information values per model title glob: the most specific glob wins per parameter.
const piv = X.deriveRules({ derive: { status: "approved - test setting", projectInformationValues: { "*": { TST_PI_Country: "TST-Land", TST_PI_City: "Anywhere" }, "TST-*-0001": { TST_PI_City: "Glob City" }, "TST-ST-0001": { TST_PI_City: "Exact City" }, "TST-AR-*": { TST_PI_Bad: "" } } } });
assert.ok(piv.approved, '"approved - test setting" counts as approved');
assert.equal(piv.problems.length, 1, "an empty literal is reported");
const pivFor = (model) => piv.groups.filter((g) => X.groupApplies(g, piv.groups, model)).map((g) => g.rules[0]);
assert.deepEqual(pivFor("TST-ST-0001.rvt"), [{ target: "TST_PI_Country", value: "TST-Land" }, { target: "TST_PI_City", value: "Exact City" }], "exact beats glob beats *");
assert.deepEqual(pivFor("tst-ar-0001"), [{ target: "TST_PI_Country", value: "TST-Land" }, { target: "TST_PI_City", value: "Glob City" }], "case-insensitive glob");
assert.deepEqual(pivFor("OTHER"), [{ target: "TST_PI_Country", value: "TST-Land" }, { target: "TST_PI_City", value: "Anywhere" }]);
const pivDoc = { model: "TST-ST-0001", rows: [{ key: "c1", name: "TST_PI_City", blank: 1, status: "blanks", isText: true }, { key: "c2", name: "TST_PI_Country", blank: 0, na: 1, status: "complete-with-na", isText: true }] };
const pivRows = [{ key: "c1", name: "TST_PI_City", group: "project", level: "instance", sources: [] }, { key: "c2", name: "TST_PI_Country", group: "project", level: "instance", sources: [] }];
const pivActs = A.buildActions({ result: { documents: [pivDoc] }, rows: pivRows, deriveSet: piv, exec: { value: "NA", deriveApproved: true } }).filter((a) => a.run?.kind === "derive");
assert.deepEqual(pivActs.map((a) => a.what), ['Derive TST_PI_City = "Exact City" (models TST-ST-0001)'], "only the winning rule, only where the target is truly blank (NA is not blank)");
const pivPlan = X.buildPlan(pivDoc, pivActs, { rows: pivRows, bridgeRow: S.bridgeRow, derive: piv, value: "NA", tempDir: "x", readFile: () => Buffer.from("") });
assert.deepEqual(pivPlan.derive, [{ target: "TST_PI_City", value: "Exact City" }], "the bridge's literal rule");

// ---- IFC property sets ---------------------------------------------------------------------------------------------
const ifc = X.readIfcPropertySets(Buffer.from("# User Defined PropertySet Definition File\r\nPropertySet:\tTST_Pset_Asset\tI\tIfcWall,IfcSlab\r\n\tAssetCode\tText\tTST_EL_Asset\r\n\tTST_EL_Name\tText\r\nPropertySet:\tTST_Pset_Type\tT\tIfcDoorType\r\n\tTypeCode\tText\tTST_EL_Asset\r\n"));
assert.equal(ifc.psets.length, 2);
assert.deepEqual(ifc.byParameter.get("TST_EL_Asset").map((p) => [p.pset, p.property, p.level, p.entities.join()]), [["TST_Pset_Asset", "AssetCode", "instance", "IfcWall,IfcSlab"], ["TST_Pset_Type", "TypeCode", "type", "IfcDoorType"]]);
assert.ok(ifc.byParameter.has("TST_EL_Name"), "no Revit name: the property name is the parameter");
const flips = X.checkerFlips({ checks: [{ name: "c1", active: true, filters: [] }, { name: "c2", active: false, filters: [] }] }, ["TST_EL_Asset"], (c) => ({ requirement: true, params: ["TST_EL_Asset"] }));
assert.deepEqual(flips, { TST_EL_Asset: ["c1"] }, "only active checks flip");
const lines = X.impactLines({ impact: { TST_EL_Asset: { schedules: [{ name: "S", filters: [{ type: "HasValue" }] }, { name: "T", filters: [] }], viewFilters: ["F"], labels: [{ family: "Tag A", kind: "tag", sheets: 3 }] } }, ifc, flips, params: ["TST_EL_Asset"] });
assert.match(lines[0], /^TST_EL_Asset: 2 schedule\(s\) \(1 with a has-value filter: rows hidden now will show\), 1 view filter\(s\), label in Tag A \(tag\) will print NA on 3 sheet\(s\) \(inferred\), IFC TST_Pset_Asset\.AssetCode \(IfcWall, IfcSlab\), IFC TST_Pset_Type\.TypeCode \(IfcDoorType\), client checker: 1 check\(s\) turn from fail to pass$/);

// ---- shared parameter extract (exactly the client's lines) ---------------------------------------------------------
const spText = "# Revit\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t1\tA\r\nGROUP\t2\tB\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\r\nPARAM\tg-1\tTST_One\tTEXT\t\t2\r\nPARAM\tg-2\tTST_Two\tTEXT\t\t1\r\n";
const ex = X.sharedParameterExtract(spText, ["G-1"]);
assert.deepEqual(ex.lines, ["PARAM\tg-1\tTST_One\tTEXT\t\t2"]);
assert.equal(ex.text, "# Revit\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t2\tB\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\r\nPARAM\tg-1\tTST_One\tTEXT\t\t2\r\n", "headers, the group it uses and the line itself");
assert.deepEqual(S.readSharedParameterFile(X.encodeLike(Buffer.from([0xff, 0xfe, 0x23, 0]), ex.text)).params.map((p) => p.name), ["TST_One"], "written back as UTF-16 and readable");

// ---- the plan for one model ----------------------------------------------------------------------------------------
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-exec-"));
const cfile = path.join(tmp, "client.txt");
fs.writeFileSync(cfile, spText);
const idx = S.paramIndex([{ file: "project.txt", kind: "project", params: [{ name: "TST_P", guid: "p-1", dataType: "TEXT" }] }, { file: cfile, kind: "client", params: S.readSharedParameterFile(spText).params }]);
const rows = [
  { key: "r1", name: "TST_P", group: "project", level: "instance", guid: "p-1", match: "exact", sources: [] },
  { key: "r2", name: "TST_One", group: "elements", level: "type", categories: ["OST_Walls"], guid: "g-1", match: "exact-client", sources: [] },
  { key: "r3", name: "TST_Fill", group: "project", level: "instance", match: "unknown", sources: [] },
  { key: "r4", name: "Type Mark", group: "elements", level: "type", origin: "built-in", match: "unknown", sources: [] },
  { key: "r5", name: "TST_Any", group: "elements", level: "any", guid: "p-9", match: "exact", sources: [] },
];
const doc = { model: "TST-AR", rows: [
  { key: "r1", name: "TST_P", status: "not-bound", checked: 1, blank: 0 }, { key: "r2", name: "TST_One", status: "not-bound", checked: 5, blank: 0 },
  { key: "r3", name: "TST_Fill", status: "blanks", checked: 3, blank: 2, isText: true },
  { key: "r4", name: "Type Mark", status: "not-available", checked: 6, missing: 4, blank: 1, isText: true, origin: "built-in", notAvailableCategories: ["Furniture"] },
  { key: "r5", name: "TST_Any", status: "not-bound", checked: 2, blank: 0 },
] };
const acts = A.buildActions({ result: { documents: [doc] }, rows, exec: { value: "NA", deriveApproved: true, projectFile: "project.txt" } });
const bindAct = acts.filter((a) => a.run?.kind === "bind");
assert.deepEqual(bindAct.flatMap((a) => a.run.keys).sort(), ["r1", "r2"], "bindable rows: a definition in a file and categories");
assert.ok(acts.some((a) => /^unbound-/.test(a.id) && /no categories to bind to/.test(a.blockers[0].what)), "a general row has nothing to bind to: a decision with the reason");
assert.ok(acts.some((a) => /^builtin-/.test(a.id) && /Type Mark is a built-in parameter, not available on categories Furniture \(4 element\(s\)\)/.test(a.what) && a.owner === "BIM manager"), "built-in: information action");
assert.ok(!acts.some((a) => a.run?.kind === "bind" && a.run.keys.includes("r4")), "never a binding for a built-in");
const plan = X.buildPlan(doc, acts.filter((a) => a.run), { rows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: path.join(tmp, "t"), readFile: (f) => f === "project.txt" ? Buffer.from("TST project file") : fs.readFileSync(f) });
assert.equal(plan.bind[0].fileHash, X.sha256(Buffer.from("TST project file")), "every binding names the bytes of its file");
assert.equal(plan.bind[1].fileHash, X.sha256(plan.temp[0].bytes), "also the temporary file");
assert.deepEqual(plan.bind.map((b) => [b.name, b.file === "project.txt" ? "project" : path.basename(path.dirname(b.file)), b.level, b.categories.join()]), [["TST_P", "project", "instance", "OST_ProjectInformation"], ["TST_One", "t", "type", "OST_Walls"]]);
assert.ok(plan.temp.length === 1 && plan.temp[0].lines[0].includes("TST_One") && /Add this line to the project shared parameter file/.test(plan.notes.at(-1)), "client-only: a temporary file and the note");
assert.ok(plan.fill && plan.value === "NA" && plan.rows.map((r) => r.key).join() === "r1,r2,r3,r4", "fill rows: every blank auto-fixable row");
{
  // Discipline not read from the title: discipline-scoped rows are held (never written), the others still run.
  const scopedRows = rows.map((r) => r.key === "r3" || r.key === "r2" ? { ...r, disciplines: ["AR"] } : r);
  const ambDoc = { ...doc, discipline: null, disciplineSource: "ambiguous", disciplineCandidates: ["PL", "AR"] };
  const amb = X.buildPlan(ambDoc, acts.filter((a) => a.run), { rows: scopedRows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: path.join(tmp, "t"), readFile: (f) => f === "project.txt" ? Buffer.from("TST project file") : fs.readFileSync(f) });
  assert.deepEqual(amb.rows.map((r) => r.key), ["r1", "r4"], "discipline-scoped rows are not in the plan");
  assert.ok(amb.held.some((h) => /2 discipline-scoped row\(s\) not written/.test(h.what) && /candidates PL, AR/.test(h.why) && /titleField/.test(h.how)));
  assert.equal(X.buildPlan({ ...doc, disciplineUnknown: true }, acts.filter((a) => a.run), { rows: scopedRows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: path.join(tmp, "t"), readFile: (f) => f === "project.txt" ? Buffer.from("TST project file") : fs.readFileSync(f) }).rows.length, 2, "unknown discipline: the same");
  assert.match(X.disciplineWarning(ambDoc), /^Warning: TST-AR: discipline not read from the title: candidates PL, AR; set models or titleField in the mapping\. Until then this result judges every discipline's rows/);
  assert.match(X.disciplineWarning({ model: "M", disciplineUnknown: true }), /\(no discipline code in it\)/);
  assert.equal(X.disciplineWarning(doc), null);
  assert.deepEqual(S.buildScope({ titleField: 3, models: { "TST-X": "ST" } }).titleField, 3);
  const sc = S.buildScope({ models: { titleField: "2", "TST-X": "ST" } });
  assert.ok(sc.titleField === 2 && !("titleField" in sc.models), "titleField under models: moved to the scope");
  assert.equal(S.buildScope({ titleField: -1 }).titleField, undefined, "an invalid titleField is not sent");
  assert.deepEqual(X.unmatchedTitleRules({ models: { titleField: 2 } }, ["TST"]), [], "titleField is not a model title");
  assert.match(A.summaryMarkdown({ documents: [doc] }), /\| complete \| complete with NA \|/, "complete with NA is its own column");
}
const noProj = X.buildPlan(doc, acts.filter((a) => a.run), { rows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: null, valueProblem: "not approved", tempDir: tmp, readFile: fs.readFileSync });
// Plan-only rows (an unapproved mapping part) are never filled: held with the reason, and the counts match what is sent.
const draftRows = rows.map((r) => r.key === "r3" ? { ...r, draftMapping: true, planOnly: 'the category entry "Piles" has confidence medium and is not approved' } : r);
const dActs = A.buildActions({ result: { documents: [doc] }, rows: draftRows, exec: { value: "NA", deriveApproved: true, projectFile: "project.txt" } });
const dFill = dActs.find((a) => a.run?.kind === "fill" && a.run.keys.includes("r3"));
assert.ok(dFill.ready === false && /plan only: the category entry "Piles"/.test(dFill.blockers.at(-1).what), "the fill action says it is not ready, and why");
const dPlan = X.buildPlan(doc, [dFill, ...dActs.filter((a) => a.run?.kind === "fill" && a !== dFill)], { rows: draftRows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: tmp, readFile: () => Buffer.from("x") });
assert.ok(!dPlan.rows.some((r) => r.key === "r3") && dPlan.fillRows === dPlan.rows.length && /1 row\(s\) not filled/.test(dPlan.held[0].what) && /not approved/.test(dPlan.held[0].why), "held, not sent; planLine counts what is sent");
// An explicit rebind: old GUID from the model, the file's GUID and bytes.
const gdoc = { model: "TST-AR", rows: [{ key: "r1", name: "TST_P", status: "guid-differs", checked: 2, blank: 0, modelGuid: "old-1" }] };
const gActs = A.buildActions({ result: S.normaliseAnswer(gdoc) && { documents: [gdoc] }, rows: [{ ...rows[0], defFile: "project.txt" }], exec: { value: "NA", deriveApproved: true, projectFile: "project.txt" } });
assert.match(gActs.find((a) => a.id.startsWith("guid-")).what, /^TST_P: the model's GUID differs from project\.txt; confirm which is current$/);
const rb = gActs.find((a) => a.id.startsWith("rebind-"));
const rPlan = X.buildPlan(gdoc, [rb], { rows, bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: tmp, readFile: () => Buffer.from("p") });
assert.deepEqual(rPlan.rebind, [{ name: "TST_P", guid: "p-1", dataType: "TEXT", group: "project", level: "instance", categories: ["OST_ProjectInformation"], groupTypeId: "General", file: "project.txt", fileHash: X.sha256(Buffer.from("p")), oldGuid: "old-1" }]);
assert.match(X.planLine(rPlan), /rebind 1/);
const dupR = X.buildPlan(gdoc, [rb], { rows: [{ ...rows[0], guids: ["p-1", "p-2"] }], bridgeRow: S.bridgeRow, index: idx, derive: { groups: [], approved: true }, value: "NA", projectFile: "project.txt", tempDir: tmp, readFile: () => Buffer.from("p") });
assert.ok(!dupR.rebind.length && /2 times with different GUIDs/.test(dupR.held[0].why), "a rebind is refused when the client file has several GUIDs");
assert.equal(S.normaliseAnswer({ documents: [{ rows: [{ status: "wrong-guid" }], summary: { wrongGuid: 2 } }] }).documents[0].summary.guidDiffers, 2, "1.10 answers are read with the new name");
assert.ok(!/[\\/:*?]|\.\./.test(X.safeName("../..\\TST:AR*?")) && !X.safeName("..").includes(".."), "model names are safe in paths");
assert.ok(!X.hasWork(noProj) && noProj.held.length === 2 && /project's shared parameter file/.test(noProj.held.map((h) => h.why).join()), "no project file / no value: held, with reasons");
assert.match(X.planLine(plan), /^TST-AR: bind 2, fill "NA" on the blanks of 2 row\(s\)$/);

// ---- register and evidence -----------------------------------------------------------------------------------------
const reg = [{ key: "r3", name: "TST_Fill", category: "OST_ProjectInformation", categoryName: "Project Information", level: "instance", count: 2, ids: [5, 6] }];
assert.equal(X.registerCsv("TST-AR", reg).split(/\r?\n/).filter(Boolean).length, 3, "one line per blank element");
assert.match(X.registerMarkdown("TST-AR", reg), /\| TST_Fill \| Project Information \| instance \| 2 \|/);
const evid = X.evidenceMarkdown({ model: "TST-AR", register: reg, plan, result: { success: true, bindings: { planned: 2, done: 1, skipped: [{ name: "TST_One", reason: "category" }] }, derived: [{ text: "A = B", set: 1, skipped: [] }], fill: { set: 2, value: "NA", skips: [{ what: "fill", why: "in a group", elements: 3, sampleIds: [1] }] }, after: { rows: [{ name: "TST_Fill", blank: 1 }, { name: "TST_P", status: "not-bound" }] }, verify: { ok: true, checked: 4 } }, blocked: [{ what: "2 elements", why: "borrowed", holder: "jane", how: "relinquish" }] });
for (const re of [/missing before \(blank values\) \| 2/, /bound \| 1 of 2/, /derived \| 1/, /"NA" written \| 2/, /skipped \| 4/, /still blank \(rows\) \| 1/, /not bound \(rows\) \| 1/, /self-check \| ok/, /## Blocked\n\n- 2 elements: borrowed; held by jane\. How to clear: relinquish/])
  assert.match(evid, re);
assert.match(X.blockLine({ what: "3 element(s)", why: "borrowed", elements: 3, sampleIds: [1, 2], holder: "bob", how: "ask bob" }), /^3 element\(s\): borrowed \(3 element\(s\), e\.g\. 1, 2\); held by bob\. How to clear: ask bob$/);

// ---- B: a client file listing one name with several GUIDs ------------------------------------------------------------
const dupIdx = S.paramIndex([{ file: "c.txt", kind: "client", params: [{ name: "TST_D", guid: "d-1" }, { name: "TST_D", guid: "d-2" }, { name: "TST_D", guid: "d-2" }] }, { file: "o.txt", kind: "client", params: [{ name: "TST_E", guid: "e-1" }] }, { file: "p.txt", kind: "client", params: [{ name: "TST_E", guid: "e-2" }] }]);
assert.deepEqual(dupIdx.duplicateGuids, [{ name: "TST_D", file: "c.txt", kind: "client", guids: ["d-1", "d-2"] }], "every GUID of a name in one file is kept");
assert.deepEqual(dupIdx.conflicts.map((c) => c.name), ["TST_E"], "conflicts stay between files");
const dc = S.classifyName("TST_D", dupIdx);
assert.ok(dc.guid === "d-1" && dc.guids.join() === "d-1,d-2");
const dm = S.mergeSources({ ais: [{ name: "TST_D", group: "project", level: "instance" }], index: dupIdx });
assert.deepEqual(S.bridgeRow(dm.rows[0]).guids, ["d-1", "d-2"], "the bridge row accepts any of them");
assert.ok(A.buildActions({ defects: dm.defects }).some((a) => /client file c\.txt defines TST_D 2 times with different GUIDs/.test(a.what) && a.owner === "client"));
assert.ok(A.buildActions({ defects: { splitNames: [{ cell: "TST_X_A, B", loin: "S!2", names: ["TST_X_A", "TST_X_B"], definedAsOne: "c.txt" }], pseudoNames: [{ name: "TST_X_A, Q", loin: "S!3", reason: "several names in one cell; to check: TST_X_Q" }] } })
  .filter((a) => /lists 2 names.*c\.txt also defines the whole cell/.test(a.what) || /S!3 \(several names in one cell/.test(a.why)).length === 2, "split cells and multi-name pseudo-names become client actions");
assert.match(S.defectsText(dm.defects), /TST_D x2 \(c\.txt\)/);
assert.match(X.bindProblem(dm.rows[0]), /2 times with different GUIDs/, "ACE cannot pick one to bind");

// ---- C: role codes, disciplines per LOIN sheet ---------------------------------------------------------------------------
assert.deepEqual(S.buildScope(null, []).codes, S.ROLE_CODES, "no project codes: the generic role codes");
assert.ok(S.ROLE_CODES.includes("ST"));
assert.deepEqual(S.buildScope({ disciplines: { "02_TST": ["ST"] } }, []).codes, ["ST"], "project codes win");
assert.deepEqual(S.normaliseMapping({ loinCategories: {}, disciplines: { "02_TST": ["ST"] } }).disciplines, { "02_TST": ["ST"] }, "top-level disciplines in the project shape");
const noScope = S.mergeSources({ loin: [{ sheet: "05_TST", row: 3, category: "Walls", name: "TST_W" }], mapping: { categories: { Walls: ["OST_Walls"] }, disciplines: {} } });
assert.deepEqual(noScope.defects.sheetsWithoutDisciplines, ["05_TST"]);
assert.ok(A.buildActions({ defects: noScope.defects }).some((a) => a.what === "LOIN sheet 05_TST has no discipline scope: its rows are judged in every model; set disciplines in the mapping" && a.owner === "BIM manager"));

// ---- G: groupDisciplines scope AIS and checker rows ----------------------------------------------------------------------
const gMap = { categories: { Walls: ["OST_Walls"] }, disciplines: { "02_TST": ["ST"] }, groupDisciplines: { "Project Information": "*", "Systems, Components, Elements": ["AR"], levels: ["ST", "AR"] } };
const gm = S.mergeSources({ ais: [{ name: "TST_PI", group: "project", level: "instance" }, { name: "TST_EL", group: "elements", level: "type" }, { name: "TST_LV", group: "levels", level: "instance" }],
  checker: [{ name: "TST_EL", group: "elements", level: "type", categories: [], source: { checker: "c", id: "1" } }], loin: [{ sheet: "02_TST", row: 2, category: "Walls", name: "TST_ST_W" }], mapping: gMap });
const disc = Object.fromEntries(gm.rows.map((r) => [r.name, r.disciplines ?? "all"]));
assert.deepEqual(disc, { TST_PI: "all", TST_EL: ["AR"], TST_LV: ["AR", "ST"], TST_ST_W: ["ST"] }, "AIS and checker rows take the group's disciplines; * = every model");
assert.deepEqual(S.bridgeRow(gm.rows.find((r) => r.name === "TST_EL")).disciplines, ["AR"], "sent to the add-in");
const mixed = S.mergeSources({ ais: [{ name: "TST_X", group: "elements", level: "type" }], checker: [{ name: "TST_X", group: "elements", level: "type", categories: [], source: { checker: "c" } }], mapping: { groupDisciplines: { elements: ["ST"] } } });
assert.deepEqual(mixed.rows[0].disciplines, ["ST"]);
const allSrc = S.mergeSources({ ais: [{ name: "TST_X", group: "elements", level: "type" }], loin: [{ sheet: "S", row: 2, category: "Walls", name: "TST_X" }], mapping: { categories: { Walls: [] }, disciplines: { S: ["ST"] } } });
assert.equal(allSrc.rows[0].disciplines, undefined, "an all-models source keeps the merged row all-models");
assert.deepEqual(S.normaliseMapping({ loinCategories: {}, groupDisciplines: { Levels: ["ST"] } }).groupDisciplines, { Levels: ["ST"] }, "project shape keeps groupDisciplines");
assert.ok(S.buildScope(gMap, gm.rows).codes.includes("AR"), "group codes join the scope codes");

// ---- unapproved mapping parts: per entry, per section --------------------------------------------------------------------
const pm = S.normaliseMapping({ status: "approved", loinCategories: { "02_TST": { Piles: { bic: ["OST_StructuralColumns"], confidence: "medium" }, Walls: { bic: ["OST_Walls"], confidence: "high" }, Beams: { bic: ["OST_StructuralFraming"], confidence: "low", status: "approved" } } },
  disciplines: { "02_TST": ["ST"], status: "approved" }, groupDisciplines: { elements: ["ST"], status: "proposed" }, models: { "TST-ST": "ST" } });
assert.deepEqual(pm.disciplines, { "02_TST": ["ST"] }, "status keys are not sheets");
assert.match(S.categoryHold(pm, "02_TST", "Piles"), /confidence medium and is not approved/, "a medium-confidence entry waits for its approval");
assert.equal(S.categoryHold(pm, "02_TST", "Walls"), null);
assert.equal(S.categoryHold(pm, "02_TST", "Beams"), null, "an approved entry counts whatever its confidence");
const pmRows = S.mergeSources({ loin: [{ sheet: "02_TST", row: 2, category: "Piles", name: "TST_ST_Pile" }, { sheet: "02_TST", row: 3, category: "Walls", name: "TST_ST_Wall" }], ais: [{ name: "TST_EL", group: "elements", level: "type" }], mapping: pm }).rows;
const po = Object.fromEntries(pmRows.map((r) => [r.name, r.planOnly ?? null]));
assert.match(po.TST_ST_Pile, /Piles/);
assert.equal(po.TST_ST_Wall, null);
assert.match(po.TST_EL, /groupDisciplines\) in the mapping is not approved/, "scope from an unapproved section: plan-only");

// ---- spelling rule: case / whitespace-only differences resolve to the file's spelling once approved ---------------------
{
  const sidx = S.paramIndex([{ file: "client.txt", kind: "client", params: [{ name: "TST_AR_Fire Rating", guid: "f-1", dataType: "TEXT" }, { name: "TST_AR_Room_Use", guid: "r-1" }] }]);
  const on = S.normaliseMapping({ nameAliases: { status: "approved", spellingRule: "case/space-only differences resolve to the shared parameter file's spelling" } });
  const c = S.classifyName("TST_AR_fire  rating", sidx, on);
  assert.ok(c.match === "alias" && c.name === "TST_AR_Fire Rating" && c.aliasOf === "TST_AR_fire  rating" && c.guid === "f-1", "an approved spelling rule makes it an alias");
  assert.equal(S.classifyName("TST_AR_Room Use", sidx, on).match, "spelling", "an underscore difference is not case or space: still a warning");
  const off = S.normaliseMapping({ nameAliases: { status: "proposed", spellingRule: "x" } });
  assert.equal(S.classifyName("TST_AR_fire rating", sidx, off).match, "spelling", "not approved: still a warning");
  assert.equal(S.classifyName("TST_AR_fire rating", sidx, S.normaliseMapping({ nameAliases: { status: "approved" } })).match, "spelling", "no rule: still a warning");
}

// ---- D: parked checks' scope defects -------------------------------------------------------------------------------------
const f = (cat, prop, cond, val) => `<Filter Operator="And" Category="${cat}" Property="${prop}" Condition="${cond}" Value="${val}" />`;
const chk = S.readCheckerXml(`<MCSettings Name="T"><Heading HeadingText="H" IsChecked="True"><Section SectionName="S" IsChecked="True"><Check ID="1" CheckName="a" IsChecked="True">${f("Category", "OST_ProjectInformation", "Included", "True")}${f("Parameter", "TST_ME_Pump", "HasValue", "True")}</Check></Section></Heading>` +
  `<Heading HeadingText="&lt;--Unused--&gt;" IsChecked="True"><Section SectionName="S" IsChecked="True"><Check ID="2" CheckName="b" IsChecked="True">${f("Category", "OST_ProjectInformation", "Included", "True")}${f("Parameter", "TST_ME_Fan", "HasValue", "True")}</Check></Section></Heading></MCSettings>`);
const rv = S.reviewChecker(chk, {});
assert.deepEqual(rv.scopeDefects.map((d) => [d.param, !!d.parked]), [["TST_ME_Fan", true], ["TST_ME_Pump", false]], "parked checks are reviewed too, flagged");
assert.match(S.reviewText(rv), /Scope defects \(2: 1 in active checks, 1 in parked checks\): .*TST_ME_Fan \(parked\)/);

// ---- F: the AIS origin column --------------------------------------------------------------------------------------------
const csv = rowsFromCsv("parameter_name,categories,type_instance,origin\r\nType Mark,\"Systems, Components, Elements\",Type,Built-In\r\nTST_A,Project Information,Instance,Custom\r\n");
assert.deepEqual(csv.rows.map((r) => r.origin ?? null), ["built-in", null]);
assert.equal(S.mergeSources({ ais: csv.rows }).rows[0].origin, "built-in", "the origin reaches the merged row");
assert.match(X.bindProblem({ name: "Type Mark", origin: "built-in", guid: "g", match: "exact" }), /built-in/);

// 1.11.1: requirement items in the evidence, rolled-back wording, title rules, title blocks.
{
  const items = X.evidenceItems({ evidence: { status: "approved", items: [{ id: "TST-QA-1", text: "Codes", rows: ["TST_A", "TST_B"] }, { id: "TST-QA-2", check: "TST check" }, { id: "TST-QA-3", rows: "TST_C" }, { text: "no id: ignored" }] } });
  assert.deepEqual(items.map((i) => i.id), ["TST-QA-1", "TST-QA-2", "TST-QA-3"]);
  const st = X.evidenceStatus(items, { rows: [{ name: "TST_A", status: "complete" }, { name: "TST_B", status: "blanks" }, { name: "TST_D", status: "complete-with-na" }], checkerRows: [{ name: "TST_D", source: { checker: "TST check" } }] });
  assert.deepEqual(st.map((i) => i.status), ["partly met", "met with NA", "not checked in this model"]);
  assert.deepEqual(st[0].open, ["TST_B: blanks"]);
  assert.equal(X.evidenceStatus([{ id: "q", text: "", rows: ["TST_B"] }], { rows: [{ name: "tst_b", status: "not-bound" }] })[0].status, "not met", "names match case-insensitively");
  assert.deepEqual(X.evidenceItems({}), [], "no items: nothing invented");
  const ev = X.evidenceMarkdown({ model: "TST", result: { success: false, rolledBack: true, fill: { set: 0, wouldSet: 5 }, bindings: { planned: 1, done: 0, wouldDo: 1 }, derived: [{ text: "TST_A = TST_B", set: 0, wouldSet: 2 }], verify: { ok: false, mismatchCount: 1 }, after: null, afterIfKept: { rows: [{ name: "TST_A", blank: 3 }] } }, items: st });
  assert.match(ev, /Rolled back: nothing changed/);
  assert.match(ev, /bound \| 0 \(1 would have been made; rolled back\) of 1/);
  assert.match(ev, /derived \| 0 \(2 would have been set; rolled back\)/);
  assert.match(ev, /TST_A = TST_B: 2 would have been set; rolled back/);
  assert.match(ev, /still blank \(rows\) \| 0/, "the end state of a rolled-back plan is never reported as the model's");
  assert.match(ev, /## Requirement items \(as before: nothing changed\)/);
  assert.ok(X.wasRolledBack({ rolledBack: true }) && X.wasRolledBack({ verify: { ok: false } }) && !X.wasRolledBack({ rolledBack: false, verify: { ok: true } }));

  const groups = X.deriveRules({ derive: { projectInformationValues: { "TST-*-1": { TST_A: "a" }, "*": { TST_B: "b" } }, status: "approved" } });
  assert.deepEqual(X.unmatchedTitleRules({ derive: groups, models: { "TST-ST": "ST", "NOPE": "AR", status: "approved" } }, ["XX-TST-ST-1.rvt"]),
    ["Warning: rule 'TST-*-1' matches no open model: XX-TST-ST-1.rvt (a glob matches the full model title; its values are not written).", "Warning: models entry 'NOPE' matches no open model: XX-TST-ST-1.rvt."],
    "a glob matches the full title; models keys a part of it; '*' always matches");
  assert.deepEqual(X.unmatchedTitleRules({ derive: groups }, ["TST-X-1"]), []);
  assert.deepEqual(X.unmatchedTitleRules({ derive: groups }, []), [], "no titles known: no guess");

  const tb = X.impactLines({ impact: { "TST_Name": { titleBlocks: { families: 2, sheets: 45, instances: 45 }, labels: [{ family: "TST A1", kind: "titleblock", sheets: 40, instances: 40, proxy: "built-in" }, { family: "TST A3", kind: "titleblock", sheets: 5, instances: 5, proxy: "built-in" }] } }, value: "NA" });
  assert.deepEqual(tb, ["TST_Name: shown on 45 sheet(s) via title blocks: TST A1, TST A3 (45 instance(s)) will print NA (inferred: a built-in parameter leaves no trace in the family, so every title block on sheets is listed)"]);
  assert.match(X.impactLines({ impact: { P: { labels: [{ family: "TST tb", kind: "titleblock", sheets: 2 }] } } })[0], /label in TST tb \(titleblock\) will print NA on 2 sheet/, "an older add-in without titleBlocks: labels one by one");
}

fs.rmSync(tmp, { recursive: true, force: true });
console.log("paramexec test passed");
