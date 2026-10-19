// Parameter list test (no Revit): CSV parsing, category -> group mapping, duplicates, summary text.
// Synthetic rows only; the client's real parameter list never goes in this repository.
import assert from "node:assert/strict";
import { groupOf, levelOf, mapRows, parseCsv, rowsFromCsv, summaryText } from "../lib/paramlist.js";

// ---- CSV ---------------------------------------------------------------------------------------------------------
assert.deepEqual(parseCsv('a,b\r\n"x, y","say ""hi"""\r\n'), [["a", "b"], ["x, y", 'say "hi"']], "quotes, commas and doubled quotes");
assert.deepEqual(parseCsv('﻿a,b\n1,"two\nlines"'), [["a", "b"], ["1", "two\nlines"]], "BOM, LF, line break inside quotes, no final newline");
assert.deepEqual(parseCsv("a;b\r\n1;2\r\n\r\n"), [["a", "b"], ["1", "2"]], "semicolon lists (no comma in the header); blank lines dropped");
assert.deepEqual(parseCsv(""), []);

// ---- mapping -----------------------------------------------------------------------------------------------------
assert.equal(groupOf("Project Information"), "project");
assert.equal(groupOf(" rooms,areas,  Spaces "), "rooms", "spacing and case do not matter");
assert.equal(groupOf("Levels"), "levels");
assert.equal(groupOf("Systems, Components, Elements"), "elements");
assert.equal(groupOf("elements"), "elements", "group keys work too (inline rows)");
assert.equal(groupOf("Furniture"), null, "unknown categories are not guessed");
assert.equal(levelOf("Type"), "type");
assert.equal(levelOf(""), "instance");
assert.equal(levelOf(" instance "), "instance");
assert.equal(levelOf("Both"), null);

const csv = "﻿id,parameter_name,categories,type_instance,description\r\n" +
  '1,TST_GEN_Site Name,Project Information,Instance,"Name, as on the title"\r\n' +
  "2,TST_GEN_Area,\"Rooms, Areas, Spaces\",Instance,\r\n" +
  "3,TST_GEN_Area,Project Information,,second group: kept\r\n" +
  "4,TST_GEN_Area,\"Rooms, Areas, Spaces\",Type,duplicate key: dropped\r\n" +
  "5,TST_LVL_Code,Levels,Instance,\r\n" +
  "6,TST_SYS_Asset Type,\"Systems, Components, Elements\",Type,\r\n" +
  "7,TST_X,Furniture,Instance,unknown\r\n" +
  "8,,Levels,Instance,no name\r\n";
const m = rowsFromCsv(csv);
assert.deepEqual(m.rows, [
  { name: "TST_GEN_Site Name", group: "project", level: "instance" },
  { name: "TST_GEN_Area", group: "rooms", level: "instance" },
  { name: "TST_GEN_Area", group: "project", level: "instance" },
  { name: "TST_LVL_Code", group: "levels", level: "instance" },
  { name: "TST_SYS_Asset Type", group: "elements", level: "type" },
]);
assert.deepEqual(m.duplicates, [{ line: 5, name: "TST_GEN_Area", group: "rooms" }], "same name + group: first wins");
assert.deepEqual(m.skipped.map((s) => [s.line, s.reason]), [[8, "unknown category"], [9, "no parameter name"]]);
assert.throws(() => rowsFromCsv("name,group\nx,y\n"), /parameter_name and categories/);
assert.throws(() => rowsFromCsv(""), /empty/);
assert.equal(rowsFromCsv("Parameter Name;Categories\nA;Levels\n").rows[0].level, "instance", "header spacing / case and a missing type_instance column");

const inline = mapRows([{ name: "A", categories: "levels" }, { name: "A", categories: "Levels", type_instance: "Type" }, { name: "B", categories: "Doors" }]);
assert.equal(inline.rows.length, 1);
assert.equal(inline.duplicates.length, 1);
assert.equal(inline.skipped[0].reason, "unknown category");

// ---- summary -----------------------------------------------------------------------------------------------------
const given = summaryText({ documents: [{ model: "M1", summary: { rows: 5, complete: 2, withBlanks: 1, missingEverywhere: 1, notText: 1, notInGeneral: 0 } }],
  fill: { set: 3, skipped: [{ id: 1, reason: "read-only" }, { id: 2, reason: "read-only" }] } }, { dryRun: true, skipped: [{}], duplicates: [] });
assert.match(given, /M1: 5 rows - 2 complete, 1 with blanks, 1 missing everywhere, 1 not text/);
assert.match(given, /Fill: 3 value\(s\) would be set, 2 skipped \(2 read-only\)/);
assert.match(given, /1 row\(s\) skipped/);
const computed = summaryText({ documents: [{ model: "M2", rows: [
  { checked: 2, missing: 0, blank: 0, isText: true, storageType: "String" },
  { checked: 2, missing: 0, blank: 1, isText: true, storageType: "String", inGeneral: false },
  { checked: 3, missing: 3, blank: 0, storageType: null },
  { checked: 1, missing: 0, blank: 0, isText: false, storageType: "Double" },
] }] });
assert.match(computed, /M2: 4 rows - 2 complete, 0 complete with NA, 1 with blanks, 1 missing everywhere, 1 not text .*, 1 Project Information/, "computed when the add-in sends no summary");
// Complete with NA is its own state: from the add-in's completeWithNA, else from the row statuses (an older add-in).
assert.match(summaryText({ documents: [{ model: "M3", summary: { rows: 3, complete: 1, completeWithNA: 2, withBlanks: 0, missingEverywhere: 0, notText: 0, notInGeneral: 0 } }] }), /M3: 3 rows - 1 complete, 2 complete with NA,/);
assert.match(summaryText({ documents: [{ model: "M4", summary: { rows: 3, complete: 3, withBlanks: 0, missingEverywhere: 0, notText: 0, notInGeneral: 0 }, rows: [{ status: "complete" }, { status: "complete-with-na" }, { status: "complete-with-na" }] }] }), /M4: 3 rows - 1 complete, 2 complete with NA,/, "an older add-in's complete count is split");
assert.match(summaryText({ documents: [{ model: "M5", rows: [{ checked: 2, missing: 0, blank: 0, na: 2 }] }] }), /M5: 1 rows - 0 complete, 1 complete with NA/);

console.log("paramlist test passed");
