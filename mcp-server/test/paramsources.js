// Parameter requirement sources test (no Revit): shared parameter files, Model Checker XML, LOIN workbook (built here),
// name classification, mapping file, merge, scope, cross-model checks, the checker review and the reports.
// Synthetic names only (TST_...); the client's real files never go in this repository.
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import * as S from "../lib/paramsources.js";
import * as A from "../lib/paramactions.js";
import * as X from "../lib/paramexec.js";
import { readXlsx, unzip } from "../lib/docs.js";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-paramsources-"));

// ---- shared parameter files --------------------------------------------------------------------------------------
const spText = "# This is a Revit shared parameter file.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t1\tIdentity\r\nGROUP\t2\tOther\r\n" +
  "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\r\n" +
  "PARAM\tAAAAAAAA-0000-0000-0000-000000000001\tTST_AR_Fire Rating\tTEXT\t\t1\t1\t\t1\r\n" +
  "PARAM\t00000000-0000-0000-0000-000000000002\tTST_PI_Site Name\tTEXT\t\t2\t1\tthe site\t1\r\n";
const le = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(spText, "utf16le")]);
const be = Buffer.from(le); be.swap16();
const leNoBom = Buffer.from(spText, "utf16le");
const beNoBom = Buffer.from(leNoBom); beNoBom.swap16();
for (const [label, buf] of [["UTF-16 LE BOM", le], ["UTF-16 BE BOM", be], ["UTF-16 LE no BOM", leNoBom], ["UTF-16 BE no BOM", beNoBom], ["UTF-8 BOM", Buffer.from("﻿" + spText)], ["UTF-8", Buffer.from(spText)]]) {
  const sp = S.readSharedParameterFile(buf);
  assert.equal(sp.params.length, 2, label);
  assert.deepEqual(sp.params[0], { name: "TST_AR_Fire Rating", guid: "aaaaaaaa-0000-0000-0000-000000000001", dataType: "TEXT", description: undefined, group: "Identity" }, label);
  assert.equal(sp.params[1].group, "Other", label);
}
const reordered = S.readSharedParameterFile(Buffer.from("*GROUP\tNAME\tID\nGROUP\tFirst\t7\n*PARAM\tNAME\tGROUP\tGUID\tDATATYPE\nPARAM\tTST_X\t7\tabc\tLENGTH\n"));
assert.deepEqual(reordered.params, [{ name: "TST_X", guid: "abc", dataType: "LENGTH", description: undefined, group: "First" }], "columns by header name, not position");

// ---- Model Checker XML -------------------------------------------------------------------------------------------
const F = (op, cat, prop, cond, val) => `<Filter ID="f" Operator="${op}" Category="${cat}" Property="${prop}" Condition="${cond}" Value="${val}" />`;
const hasValueCheck = (id, name, cat, level, extra = "") => `<Check ID="${id}" CheckName="${name}" IsChecked="True">` +
  F("And", "Category", cat, "Included", "True") + (level ? F("And", "TypeOrInstance", "Is Element Type", "Equal", level) : "") +
  F("Exclude", "Category", cat, "Included", "True") + F("And", "Parameter", name, "HasValue", "True") + extra + "</Check>";
const xml = `﻿<?xml version="1.0" encoding="UTF-8"?>
<!-- a comment <Check CheckName="not a check"/> -->
<MCSettings Name="TST &amp; Co checker">
  <Heading ID="h0" HeadingText="&lt;--Unused--&gt;" IsChecked="True">
    <Section ID="s0" SectionName="&lt;--Unused--&gt;" IsChecked="True">${hasValueCheck("c0", "TST_PI_Parked", "OST_ProjectInformation", "False")}</Section>
  </Heading>
  <Heading ID="h1" HeadingText="Parameters" IsChecked="True">
    <Section ID="s1" SectionName="Have value" IsChecked="True">
      <Section ID="s2" SectionName="General" IsChecked='True'>
        ${hasValueCheck("c1", "TST_PI_Site Name", "OST_ProjectInformation", "False")}
        ${hasValueCheck("c2", "TST_PI_Site Name", "OST_ProjectInformation", "False")}
        ${hasValueCheck("c3", "TST_ST_Grade", "OST_ProjectInformation", "False")}
        ${hasValueCheck("c4", "TST_RM_Zone", "OST_Rooms", "False")}
        ${hasValueCheck("c5", "TST_AR_Fire Rating", "OST_Doors", "True")}
        <Check ID="c6" CheckName="QA area zero" IsChecked="True">${F("And", "Category", "OST_Rooms", "Included", "True")}${F("And", "Parameter", "ROOM_LEVEL_ID", "HasValue", "True")}${F("And", "Parameter", "ROOM_AREA", "Equal", "0")}</Check>
        <Check ID="c7" CheckName="Type styles" IsChecked="True">${F("And", "APIType", "Full Class Name", "Equal", "X")}${F("And", "Parameter", "LEADER", "Defined", "True")}</Check>
        <Check ID="c8" CheckName="No category" IsChecked="True">${F("And", "Parameter", "TST_PI_Free", "Defined", "True")}</Check>
        <Check ID="c9" CheckName="Switched off" IsChecked="False">${F("And", "Parameter", "TST_PI_Off", "HasValue", "True")}</Check>
        <Check ID="c10" CheckName="Self closing" IsChecked="True" />
      </Section>
      <Section ID="s3" SectionName="Unplaced" IsChecked="True">${hasValueCheck("c11", "TST_PI_Unplaced", "OST_ProjectInformation", "")}</Section>
      <Section ID="s4" SectionName="Off section" IsChecked="False">${hasValueCheck("c12", "TST_PI_OffSection", "OST_ProjectInformation", "False")}</Section>
    </Section>
  </Heading>
</MCSettings>`;
const ch = S.readCheckerXml(xml);
assert.equal(ch.name, "TST & Co checker", "entities decoded");
assert.equal(ch.checks.length, 13, "the commented-out check is ignored; self-closing checks count");
const byId = Object.fromEntries(ch.checks.map((c) => [c.id, c]));
assert.deepEqual(byId.c1.path, ["Parameters", "Have value", "General"], "nested sections at any depth");
assert.ok(byId.c1.active && !byId.c0.active && byId.c0.parked && byId.c11.parked && !byId.c9.active && !byId.c12.active, "active: every level checked, nothing parked");
assert.deepEqual(S.checkRequirement(byId.c1), { params: ["TST_PI_Site Name"], categories: ["OST_ProjectInformation"], excluded: ["OST_ProjectInformation"], level: "instance", requirement: true });
assert.equal(S.checkRequirement(byId.c5).level, "type", "Is Element Type True -> type");
assert.equal(S.checkRequirement(byId.c11).level, "any", "no TypeOrInstance filter -> any");
assert.equal(S.checkRequirement(byId.c6).requirement, false, "a value condition makes it a QA check");
assert.equal(S.checkRequirement(byId.c7).requirement, false, "scoped by API class, no category");
assert.equal(S.checkRequirement(byId.c8).requirement, true, "Defined counts");
const chRows = S.checkerRows(ch);
assert.deepEqual(chRows.map((r) => [r.name, r.group, r.level, r.categories.join("|")]), [
  ["TST_PI_Site Name", "project", "instance", ""], ["TST_PI_Site Name", "project", "instance", ""], ["TST_ST_Grade", "project", "instance", ""],
  ["TST_RM_Zone", "rooms", "instance", ""], ["TST_AR_Fire Rating", "elements", "type", "OST_Doors"], ["TST_PI_Free", "elements", "any", ""],
]);
assert.equal(S.groupOfCategories(["OST_Rooms", "OST_MEPSpaces"]), "rooms");
assert.equal(S.groupOfCategories(["OST_Levels"]), "levels");
assert.equal(S.groupOfCategories(["OST_Rooms", "OST_Walls"]), "elements");

// ---- LOIN workbook (a minimal xlsx built here) ---------------------------------------------------------------------
function zip(files) {
  const locals = [], central = [];
  let offset = 0;
  for (const [name, text] of Object.entries(files)) {
    const data = zlib.deflateRawSync(Buffer.from(text)), nameBuf = Buffer.from(name);
    const lh = Buffer.alloc(30); lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(8, 8); lh.writeUInt32LE(data.length, 18); lh.writeUInt32LE(Buffer.byteLength(text), 22); lh.writeUInt16LE(nameBuf.length, 26);
    const ch = Buffer.alloc(46); ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(8, 10); ch.writeUInt32LE(data.length, 20); ch.writeUInt32LE(Buffer.byteLength(text), 24); ch.writeUInt16LE(nameBuf.length, 28); ch.writeUInt32LE(offset, 42);
    locals.push(lh, nameBuf, data); central.push(ch, nameBuf);
    offset += 30 + nameBuf.length + data.length;
  }
  const cd = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10); end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}
const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");
const sheetXml = (rows) => `<worksheet><sheetData>${rows.map(([r, cells]) => `<row r="${r}">${Object.entries(cells).map(([c, v]) => `<c r="${c}${r}" t="inlineStr"><is><t>${esc(v)}</t></is></c>`).join("")}</row>`).join("")}</sheetData></worksheet>`;
function xlsx(sheets) {
  const names = Object.keys(sheets);
  const files = {
    "xl/workbook.xml": `<workbook><sheets>${names.map((n, i) => `<sheet name="${esc(n)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join("")}</sheets></workbook>`,
    "xl/_rels/workbook.xml.rels": `<Relationships>${names.map((n, i) => `<Relationship Id="rId${i + 1}" Target="worksheets/sheet${i + 1}.xml"/>`).join("")}</Relationships>`,
  };
  names.forEach((n, i) => { files[`xl/worksheets/sheet${i + 1}.xml`] = sheetXml(sheets[n]); });
  return zip(files);
}
const loinBuf = xlsx({
  "Cover": [[1, { A: "Information requirements" }], [2, { A: "x", B: "Walls", C: "TST_AR_Nope" }]],
  "S1_ARC": [[1, { B: "Title" }], [3, { A: " Group ", B: "Category", C: "Shared  Parameter" }],
    [4, { A: "Enclosure", B: "Walls\r\n ", C: "TST_AR_Fire Rating" }], [5, { B: "Curtain Walls", C: "TST_AR_Fire rating" }],
    [6, { B: "Doors", C: "TST_AR_[Room Parameter]" }], [7, { A: "Spaces", B: "Rooms", C: "TST_AR_Room Use" }], [8, { B: "Kitchen Things", C: "TST_AR_Fire Rating" }]],
  "S6_MECH": [[1, { A: "GROUP", B: "CATEGORY", C: "SharedParameter" }], [2, { A: "HVAC", B: "Air Terminals", C: "TST_ME_Component ID" }], [3, { B: "Ducts", C: "TST_ME_Component ID" }]],
  "07_NOTES": [[1, { A: "no header here" }], [2, { A: "x", B: "y", C: "z" }]],
});
const loin = S.readLoin(loinBuf);
assert.deepEqual(loin.sheets, ["S1_ARC", "S6_MECH"], "sheets without the header row are skipped, whatever their name");
assert.deepEqual(loin.rows.map((r) => [r.sheet, r.row, r.group, r.category, r.name]), [
  ["S1_ARC", 4, "Enclosure", "Walls", "TST_AR_Fire Rating"], ["S1_ARC", 5, "Enclosure", "Curtain Walls", "TST_AR_Fire rating"],
  ["S1_ARC", 6, "Enclosure", "Doors", "TST_AR_[Room Parameter]"], ["S1_ARC", 7, "Spaces", "Rooms", "TST_AR_Room Use"], ["S1_ARC", 8, "Spaces", "Kitchen Things", "TST_AR_Fire Rating"],
  ["S6_MECH", 2, "HVAC", "Air Terminals", "TST_ME_Component ID"], ["S6_MECH", 3, "HVAC", "Ducts", "TST_ME_Component ID"],
], "header in any of the first 10 rows (spacing and case ignored); blank GROUP inherits; categories trimmed, line breaks as spaces");
{
  const merged2 = S.readLoin(xlsx({ Req: [[1, { A: "Kind", B: "Thing", C: "Field" }], [2, { A: "K1", B: "Doors", C: "TST_A" }], [3, { C: "TST_B" }], [4, { B: "Walls", C: "TST_C" }]] }), { group: "kind", category: "Thing", name: ["field"] });
  assert.deepEqual(merged2.rows.map((r) => [r.group, r.category, r.name]), [["K1", "Doors", "TST_A"], ["K1", "Doors", "TST_B"], ["K1", "Walls", "TST_C"]], "loinTable header texts; a blank CATEGORY carries the one above (merged cells)");
  assert.equal(S.readLoin(xlsx({ Req: [[1, { A: "Kind", B: "Thing", C: "Field" }]] })).sheets.length, 0, "default headers are generic");
}

// ---- best guesses and the mapping file ---------------------------------------------------------------------------
assert.deepEqual(S.guessCategories("Curtain wall cladding"), ["OST_Walls", "OST_CurtainWallPanels", "OST_CurtainWallMullions"], "curtain before wall");
assert.deepEqual(S.guessCategories("Air Terminals"), ["OST_DuctTerminal"], "air terminal before duct");
assert.deepEqual(S.guessCategories("Pile Caps"), ["OST_StructuralFoundation"]);
assert.deepEqual(S.guessCategories("Columns (Concrete)"), ["OST_StructuralColumns"]);
assert.deepEqual(S.guessCategories("Pumps"), ["OST_MechanicalEquipment"]);
assert.deepEqual(S.guessCategories("Valves"), ["OST_PipeAccessory"], "valves before pipes");
assert.deepEqual(S.guessCategories("Wall Finishes"), [], "finishes: unsure, left unmapped");
assert.deepEqual(S.guessCategories("Kitchen Things"), [], "unknown -> unmapped");
assert.deepEqual(S.guessCategories("Wall lamp"), ["OST_LightingFixtures"], "lighting before walls");
assert.deepEqual(S.guessCategories("Control room kit"), [], "room / space / area / level only as the whole label");
assert.deepEqual(S.guessCategories("Room divider panels"), [], "a bare 'panel' is not electrical equipment");
assert.deepEqual(S.guessCategories(" Rooms "), ["OST_Rooms"]);
assert.deepEqual(S.guessDisciplines("S1_ARC"), ["AR"]);
assert.deepEqual(S.guessDisciplines("LSC works"), ["LA"]);
assert.deepEqual(S.guessDisciplines("S6_MECH"), ["ME"]);
assert.deepEqual(S.guessDisciplines("ELV"), [], "unsure -> all models");
const made = S.extendMapping(null, { categories: ["Walls", "Kitchen Things", "Walls"], sheets: ["S1_ARC"] });
assert.ok(made.created && made.mapping.draft === true && /never overwrites/.test(made.mapping.about));
assert.deepEqual(made.mapping.categories, { Walls: ["OST_Walls"], "Kitchen Things": [] });
assert.deepEqual(made.mapping.disciplines, { "S1_ARC": ["AR"] });
const edited = { ...made.mapping, draft: false, categories: { Walls: ["OST_Walls", "OST_GenericModel"], "Kitchen Things": ["OST_SpecialityEquipment"] }, aliases: { "TST_ME_Component ID": "TST_MECH_Component ID" } };
const ext = S.extendMapping(edited, { categories: ["Walls", "Doors"], sheets: ["S1_ARC", "S6_MECH"] });
assert.ok(!ext.created && ext.changed);
assert.deepEqual(ext.mapping.categories, { Walls: ["OST_Walls", "OST_GenericModel"], "Kitchen Things": ["OST_SpecialityEquipment"], Doors: [] }, "values a person set are kept; new keys are empty (unmapped)");
assert.deepEqual(ext.mapping.disciplines, { "S1_ARC": ["AR"], "S6_MECH": [] });
assert.equal(ext.mapping.draft, false);
assert.equal(S.extendMapping(ext.mapping, { categories: ["Doors"], sheets: [] }).changed, false, "nothing new: unchanged");
const mapFile = path.join(tmp, S.MAPPING_FILE);
const createdMap = S.syncMappingFile(mapFile, { categories: ["Walls"], sheets: [] });
assert.ok(createdMap.created && S.isAceMapping(JSON.parse(fs.readFileSync(mapFile, "utf8"))), "created in ACE's shape");
assert.deepEqual(JSON.parse(fs.readFileSync(mapFile, "utf8")).fill, { value: null, values: [] }, "no NA value is assumed");
const edited2 = JSON.parse(fs.readFileSync(mapFile, "utf8")); edited2.categories.Walls = ["OST_Curtain"];
fs.writeFileSync(mapFile, JSON.stringify(edited2));
S.syncMappingFile(mapFile, { categories: ["Walls", "Roofs"], sheets: ["S1_ARC"] });
assert.deepEqual(JSON.parse(fs.readFileSync(mapFile, "utf8")).categories, { Walls: ["OST_Curtain"], Roofs: [] }, "ACE's own file is extended, never overwritten");
assert.ok(!fs.readdirSync(tmp).some((f) => f.endsWith(".tmp")), "written through a temporary file and a rename");
const foreignMap = path.join(tmp, "other.json");
fs.writeFileSync(foreignMap, JSON.stringify({ categories: { Walls: ["OST_Walls"] } }));
const ro = S.syncMappingFile(foreignMap, { categories: ["Roofs"], sheets: [] });
assert.ok(ro.readOnly && /read only/.test(ro.note) && fs.readFileSync(foreignMap, "utf8") === JSON.stringify({ categories: { Walls: ["OST_Walls"] } }), "a file in another shape is only read");
for (const bad of ["null", "false", "[1]", "3"]) {
  fs.writeFileSync(foreignMap, bad);
  assert.throws(() => S.syncMappingFile(foreignMap, { categories: ["Roofs"], sheets: [] }), /not a JSON object/, `JSON ${bad} is an error`);
  assert.equal(fs.readFileSync(foreignMap, "utf8"), bad, "and the file is left as it is");
}
assert.deepEqual(S.naPolicy({ fill: { value: "NA", values: ["N/A"] } }), { value: "NA", values: ["N/A", "NA"] });
assert.deepEqual(S.naPolicy({ naPolicy: { value: "Not applicable" } }), { value: "Not applicable", values: ["Not applicable"] });
assert.deepEqual(S.naPolicy(null), { value: null, values: [] }, "no default value");
assert.deepEqual(S.naPolicy({ naPolicy: { value: "NA", status: "pending review" } }), { value: null, values: ["NA"], pending: "NA" }, "an unapproved policy fills nothing");

// ---- name classification -----------------------------------------------------------------------------------------
const proj = { file: "project.txt", kind: "project", params: [{ name: "TST_AR_Fire Rating", guid: "g-proj", dataType: "TEXT" }] };
const client = { file: "client.txt", kind: "client", params: [
  { name: "TST_AR_Fire Rating", guid: "g-client", dataType: "TEXT" }, { name: "TST_AR_Room Use", guid: "g2", dataType: "TEXT" },
  { name: "TST_LSC_Component ID", guid: "g3", dataType: "TEXT" }, { name: "TST_MECH_Component ID", guid: "g4", dataType: "TEXT" },
  { name: "TST_AR_Insulation Rating", guid: "g5", dataType: "TEXT" }, { name: "TST_AR_Insulatoin Rating", guid: "g6", dataType: "TEXT" },
  { name: "TST_PI_Area AAA", guid: "g7", dataType: "TEXT" }, { name: "TST_PI_Area BBB", guid: "g8", dataType: "TEXT" },
] };
const idx = S.paramIndex([proj, client]);
assert.deepEqual(idx.conflicts, [{ name: "TST_AR_Fire Rating", guids: { "g-proj": "project.txt", "g-client": "client.txt" } }], "same name, different GUIDs");
assert.deepEqual(S.classifyName("TST_AR_Fire Rating", idx), { match: "exact", name: "TST_AR_Fire Rating", guid: "g-proj", dataType: "TEXT", defFile: "project.txt", otherGuid: "g-client" }, "the project file's GUID wins");
assert.equal(S.classifyName("TST_AR_Room Use", idx).match, "exact-client");
assert.deepEqual(S.classifyName("TST_AR_Fire rating", idx), { match: "spelling", name: "TST_AR_Fire rating", candidate: "TST_AR_Fire Rating", candidates: undefined });
assert.equal(S.classifyName("tst_ar_fire_rating", idx).match, "spelling", "case, space and _ normalised");
const drift = S.classifyName("TST_ME_Component ID", idx);
assert.equal(drift.match, "prefix-drift");
assert.equal(drift.candidate, "TST_MECH_Component ID", "a code containing the listed code (ME -> MECH) ranks first");
assert.deepEqual(drift.candidates, ["TST_MECH_Component ID", "TST_LSC_Component ID"]);
assert.equal(S.classifyName("TST_AR_[Room Parameter]", idx).match, "pseudo");
assert.equal(S.classifyName("TST_ZZ_Nothing", idx).match, "unknown");
assert.deepEqual(S.classifyName("TST_ME_Component ID", idx, { "TST_ME_Component ID": "TST_MECH_Component ID" }),
  { match: "alias", name: "TST_MECH_Component ID", aliasOf: "TST_ME_Component ID", guid: "g4", dataType: "TEXT", defFile: "client.txt" }, "an alias a person wrote is used");
{
  // An alias target a client file defines several times (one GUID per category): every GUID is accepted, as for a direct match.
  const many = S.paramIndex([{ file: "c/TST_STR.txt", kind: "client", params: [{ name: "TST_ST_Bar Size", guid: "ga" }, { name: "TST_ST_Bar Size", guid: "gb" }] }]);
  const viaAlias = S.classifyName("TST_ST_bar size", many, { "TST_ST_bar size": "TST_ST_Bar Size" });
  assert.deepEqual([viaAlias.match, viaAlias.guid, viaAlias.guids], ["alias", "ga", ["ga", "gb"]], "explicit alias: every GUID");
  const viaRule = S.classifyName("TST_ST_bar  size", many, { spellingRule: "case/space", aliases: {} });
  assert.deepEqual([viaRule.match, viaRule.guids], ["alias", ["ga", "gb"]], "spelling-rule alias: every GUID");
  const viaPrefix = S.classifyName("TST_SX_Bar Size", many, { aliases: {}, prefixMap: { TST_SX_: "TST_ST_" }, prefixApproved: true });
  assert.deepEqual([viaPrefix.match, viaPrefix.guids], ["alias", ["ga", "gb"]], "prefix alias: every GUID");
  assert.deepEqual(S.bridgeRow({ name: viaAlias.name, group: "elements", level: "any", key: "r1", guid: viaAlias.guid, guids: viaAlias.guids }).guids, ["ga", "gb"], "the add-in gets them all");
}
assert.equal(S.classifyName("Anything", null).match, "unchecked", "no shared parameter files given");
{
  // LOIN cells listing several names: one row per name (the first part's prefix carried), or a pseudo-name to check.
  assert.deepEqual(S.splitNames("TST_ST_Bar Flex, Bar Shear"), ["TST_ST_Bar Flex", "TST_ST_Bar Shear"]);
  assert.deepEqual(S.splitNames("TST_ST_A;TST_AR_B\nC"), ["TST_ST_A", "TST_AR_B", "TST_ST_C"]);
  assert.deepEqual(S.splitNames("TST_ST_One"), ["TST_ST_One"]);
  assert.deepEqual(S.splitNames("TST_KL_Size (width , depth, height)"), ["TST_KL_Size (width , depth, height)"], "separators inside parentheses belong to the name");
  assert.deepEqual(S.splitNames("TST_ID_[Note ,Other] Finish"), ["TST_ID_[Note ,Other] Finish"], "bracketed notes are never split");
  const ix = S.paramIndex([{ file: "c/TST_STR.txt", kind: "client", params: [{ name: "TST_ST_Bar Flex", guid: "f1" }, { name: "TST_ST_Bar Shear", guid: "s1" }, { name: "TST_ST_Bar Flex, Bar Shear", guid: "fs" }] }]);
  const mp = { categories: { Floors: ["OST_Floors"] }, disciplines: { "02_TST": ["ST"] }, aliases: {} };
  const m1 = S.mergeSources({ loin: [{ sheet: "02_TST", row: 7, category: "Floors", name: "TST_ST_Bar Flex, Bar Shear" }], mapping: mp, index: ix });
  assert.deepEqual(m1.rows.map((r) => r.name).sort(), ["TST_ST_Bar Flex", "TST_ST_Bar Shear"], "split into one row per name, never one parameter");
  assert.equal(m1.rows[0].sources[0].cell, "TST_ST_Bar Flex, Bar Shear", "the row says which cell it came from");
  assert.deepEqual(m1.defects.splitNames, [{ cell: "TST_ST_Bar Flex, Bar Shear", loin: "02_TST!7", names: ["TST_ST_Bar Flex", "TST_ST_Bar Shear"], definedAsOne: "TST_STR.txt" }]);
  assert.match(String(S.defectsText(m1.defects)), /LOIN cells listing several names, checked one row per name: 02_TST!7 -> TST_ST_Bar Flex \+ TST_ST_Bar Shear \(TST_STR.txt also defines/);
  const m2 = S.mergeSources({ loin: [{ sheet: "02_TST", row: 8, category: "Floors", name: "TST_ST_Bar Flex, Nothing Known" }], mapping: mp, index: ix });
  assert.equal(m2.rows.length, 0, "a split that does not classify cleanly is not counted as a parameter");
  assert.match(m2.defects.pseudoNames[0].reason, /several names in one cell; to check: TST_ST_Nothing Known/);
  const m3 = S.mergeSources({ loin: [{ sheet: "02_TST", row: 9, category: "Floors", name: "TST_ST_Bar Flex, Bar Ghost" }], mapping: { ...mp, aliases: { "TST_ST_Bar Ghost": "TST_ST_Not Defined" } }, index: ix });
  assert.equal(m3.rows.length, 0, "an alias to a name no file defines is not a clean split");
  assert.equal(S.mergeSources({ loin: [{ sheet: "02_TST", row: 9, category: "Floors", name: "TST_ST_Bar Flex, Bar Shear" }], mapping: mp }).defects.pseudoNames.length, 1, "no shared parameter files: a multi-name cell is to check");
}
assert.deepEqual(S.nearDuplicates(idx), [["TST_AR_Insulation Rating", "TST_AR_Insulatoin Rating"]], "typo twins; short codes (AAA / BBB) never count");

// ---- merge and scope ---------------------------------------------------------------------------------------------
const mapping = { categories: { Walls: ["OST_Walls"], "Curtain Walls": ["OST_Walls"], Doors: ["OST_Doors"], Rooms: ["OST_Rooms"], "Kitchen Things": [], "Air Terminals": ["OST_DuctTerminal"], "Ducts": ["OST_DuctCurves"] },
  disciplines: { "S1_ARC": ["AR"], "S6_MECH": ["ME"] }, aliases: { "TST_ME_Component ID": "TST_MECH_Component ID" }, models: { "X-ARC": "AR" }, ownership: { OST_DuctCurves: ["ME", "PL"] } };
const ais = [{ name: "TST_PI_Site Name", group: "project", level: "instance", line: 2 }, { name: "TST_AR_Room Use", group: "rooms", level: "type", line: 3 }];
const merged = S.mergeSources({ ais, loin: loin.rows, checker: chRows, mapping, index: idx });
const find = (name, group) => merged.rows.find((r) => r.name === name && r.group === group);
const site = find("TST_PI_Site Name", "project");
assert.deepEqual(site.sources, [{ ais: 2 }, { checker: "TST_PI_Site Name", id: "c1" }, { checker: "TST_PI_Site Name", id: "c2" }], "AIS and checker rows merge on name + group, every source kept");
const fire = merged.rows.filter((r) => r.name === "TST_AR_Fire Rating");
assert.deepEqual(fire.map((r) => [r.group, r.categories?.join("|"), r.level, r.sources.length]), [["elements", "OST_Doors", "type", 1], ["elements", "OST_Walls", "any", 1]], "elements rows merge on categories too");
assert.ok(merged.rows.some((r) => r.name === "TST_AR_Fire rating" && r.match === "spelling" && r.candidate === "TST_AR_Fire Rating"), "a spelling candidate is still checked under the listed name");
const room = find("TST_AR_Room Use", "rooms");
assert.equal(room.level, "type", "the AIS level beats LOIN's any");
assert.equal(room.sources.length, 2);
assert.equal(room.disciplines, undefined, "an AIS source applies to every model");
const mech = find("TST_MECH_Component ID", "elements");
assert.ok(mech && mech.aliasOf === "TST_ME_Component ID" && mech.match === "alias" && mech.guid === "g4", "aliased name checked under the alias");
assert.deepEqual(merged.rows.filter((r) => r.name === "TST_MECH_Component ID").map((r) => r.disciplines), [["ME"], ["ME"]], "LOIN rows carry the sheet's disciplines");
assert.deepEqual(merged.unmappedRows.map((r) => [r.name, r.status, r.sources[0].loin]), [["TST_AR_Fire Rating", "unmapped", "S1_ARC!8"]], "unmapped rows are reported, not sent");
assert.deepEqual(merged.defects.unmappedCategories, [{ category: "Kitchen Things", rows: 1, sheets: ["S1_ARC"], names: 1 }]);
assert.deepEqual(merged.defects.sheetsWithoutDisciplines, []);
assert.deepEqual(S.mergeSources({ loin: loin.rows, mapping: { ...mapping, disciplines: { "S1_ARC": ["AR"] } } }).defects.sheetsWithoutDisciplines, ["S6_MECH"], "no discipline guess: the sheet is reported");
assert.equal(merged.defects.pseudoNames.length, 1);
assert.deepEqual(merged.defects.nameCandidates.map((c) => c.name), ["TST_AR_Fire rating"]);
assert.ok(merged.defects.unknownNames.includes("TST_ST_Grade"));
assert.ok(merged.bridgeRows.every((b) => Object.keys(b).every((k) => ["name", "group", "level", "key", "categories", "guid", "disciplines"].includes(k))), "only bridge fields go to the add-in");
assert.ok(merged.bridgeRows.every((b) => !b.categories || b.categories.length), "never an empty categories list (it would mean every element)");
assert.deepEqual(merged.rows.map((r) => r.key), merged.rows.map((_, i) => `r${i + 1}`));
const scope = S.buildScope(mapping, merged.rows);
assert.deepEqual(scope.models, { "X-ARC": "AR" });
assert.equal(scope.ownership.OST_Walls.join(), "AR", "ownership from the LOIN sheets");
assert.equal(scope.ownership.OST_DuctCurves.join(), "ME,PL", "the mapping file overrides");
assert.deepEqual(scope.codes, ["AR", "ME", "PL"]);

// ---- after the bridge answer -------------------------------------------------------------------------------------
const answer = { documents: [
  { model: "X-ARC", discipline: "AR", rows: [{ key: "r1", name: "TST_PI_Site Name", group: "project", value: "North", modelGuid: "G1", status: "complete" }, { key: "x", name: "TST_PIX_Site Name", group: "project", value: "South" }, { name: "TST_PIX_Same", group: "project", value: "1" }, { name: "TST_PI_Same", group: "project", value: "1" }] },
  { model: "X-STR", rows: [{ key: "r1", name: "TST_PI_Site Name", group: "project", value: "North", modelGuid: "g2", status: "blanks" }] },
] };
const cross = S.crossModel(answer);
assert.deepEqual(cross.guidDiffers, [{ name: "TST_PI_Site Name", models: { "X-ARC": "g1", "X-STR": "g2" } }]);
assert.deepEqual(cross.twins, [{ model: "X-ARC", a: "TST_PI_Site Name", b: "TST_PIX_Site Name", valueA: "North", valueB: "South" }], "twins with equal values are fine");
const dtext = S.defectsText({ ...merged.defects, ...cross });
assert.match(dtext, /Unmapped client categories \(1.*Kitchen Things \(1\)/);
assert.match(dtext, /TST_AR_Fire rating -> TST_AR_Fire Rating \(spelling\)/);
assert.match(dtext, /Different GUIDs across models: TST_PI_Site Name/);
assert.match(dtext, /twins with different values: X-ARC: TST_PI_Site Name="North" vs TST_PIX_Site Name="South"/);
assert.match(dtext, /typo twins/);

// ---- P8: checker review ------------------------------------------------------------------------------------------
const rv = S.reviewChecker(ch, { ais: [...ais, { name: "TST_PI_Free", group: "elements", level: "instance" }], index: idx, codes: ["AR", "ST"] });
assert.deepEqual(rv.parameterChecks, { active: 6, parked: 2, disabled: 2 });
assert.deepEqual(rv.perCategory.OST_ProjectInformation, { count: 2, params: ["TST_PI_Site Name", "TST_ST_Grade"] });
assert.deepEqual(rv.scopeDefects.map((d) => d.param), ["TST_ST_Grade"], "a discipline parameter checked only on Project Information");
assert.deepEqual(rv.unknownNames, ["TST_ST_Grade", "TST_RM_Zone"], "in neither the shared parameter files nor the AIS");
assert.deepEqual(rv.duplicates, [{ param: "TST_PI_Site Name", categories: "OST_ProjectInformation", checks: ["TST_PI_Site Name", "TST_PI_Site Name"] }]);
assert.deepEqual(rv.noCategory.map((d) => d.check), ["No category"]);
assert.deepEqual(rv.parkedOnlyParameters, ["TST_PI_Parked", "TST_PI_Unplaced"]);
assert.match(S.reviewText(rv), /6 active, 2 parked in Unused\/Unplaced, 2 switched off/);
assert.match(S.reviewMarkdown(rv), /## Scope defects\n\n- TST_ST_Grade/);

// ---- reports and file helpers ------------------------------------------------------------------------------------
const csv = S.checkCsv(answer, merged.rows, merged.unmappedRows);
assert.ok(csv.startsWith("﻿model,discipline,status"), "CSV with a BOM for Excel");
assert.match(csv, /X-ARC,AR,complete,.*TST_PI_Site Name,project,instance,.*AIS line 2; checker: TST_PI_Site Name/);
assert.match(csv, /,,unmapped,.*LOIN S1_ARC!8/, "unmapped rows are in the report");
assert.match(S.requirementsCsv(merged.rows, merged.unmappedRows), /^﻿status,key,name/);
const files = S.writeReports(path.join(tmp, "out"), { "a.csv": "x", "b.md": "y" });
assert.ok(files.every((f) => fs.existsSync(f)) && /a_\d{4}-\d\d-\d\d_\d{6}\.csv$/.test(files[0]), "stamped to the second");

const csvFile = path.join(tmp, "ais.csv"), loinFile = path.join(tmp, "loin.xlsx"), chFile = path.join(tmp, "checker.xml"), spFile = path.join(tmp, "client.txt");
// The CSV list (UTF-16 without BOM) and the checker (UTF-16 LE with BOM) are read like the shared parameter files.
fs.writeFileSync(csvFile, Buffer.from("parameter_name,categories,type_instance\nTST_PI_Site Name,Project Information,Instance\nTST_AR_Room Use,\"Rooms, Areas, Spaces\",Type\n", "utf16le"));
fs.writeFileSync(loinFile, loinBuf); fs.writeFileSync(chFile, Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(xml, "utf16le")])); fs.writeFileSync(spFile, le);
const bare = S.loadSources({ list: csvFile, loin: loinFile, checker: chFile, shared_parameters: [spFile] });
assert.ok(bare.mapping === null && /No project mapping found.*generic checks only/.test(bare.info.notes.join()), "no mapping: generic checks only, said plainly");
assert.equal(bare.unmappedRows.length, 6, "without a mapping every LOIN row is unmapped");
assert.equal(bare.info.sources.checker, 6, "UTF-16 checker read");
assert.throws(() => S.loadSources({ loin: loinFile, mapping: path.join(tmp, "none.json") }), /Mapping file not found/, "a missing mapping is not created silently");
assert.throws(() => S.loadSources({ loin: loinFile, mapping: path.join(tmp, "map.txt"), create_mapping: true }), /must be a \.json file/);
const draftFile = path.join(tmp, "draft-mapping.json");
const src = S.loadSources({ list: csvFile, loin: loinFile, checker: chFile, shared_parameters: [spFile], mapping: draftFile, create_mapping: true });
assert.ok(src.mapping.created && src.mapping.mapping.draft === true, "create_mapping writes a draft");
assert.ok(src.rows.filter((r) => r.sources.every((x) => x.loin != null)).every((r) => r.draftMapping) && !src.rows.find((r) => r.name === "TST_PI_Site Name").draftMapping, "rows mapped only by the draft are marked (never filled)");
assert.deepEqual(src.files, [csvFile, loinFile, chFile, spFile, draftFile]);
assert.ok(S.loadSources({ loin: loinFile, mapping: draftFile }).mapping.created === false, "an existing mapping is never created again");
const noChecks = path.join(tmp, "empty.xml");
fs.writeFileSync(noChecks, "<MCSettings Name='x'><Heading HeadingText='h' IsChecked='True'/></MCSettings>");
assert.throws(() => S.loadSources({ checker: noChecks }), /no Check elements/);
assert.equal(src.rows.find((r) => r.name === "TST_PI_Site Name").sources[0].ais, 2, "AIS line numbers kept");
const h1 = S.hashFiles(src.files);
assert.equal(src.hash, h1, "the hash is taken from the bytes read once");
fs.appendFileSync(csvFile, Buffer.from("TST_LV_Code,Levels,Instance\n", "utf16le"));
assert.notEqual(S.hashFiles(src.files), h1, "a changed source changes the hash");
assert.throws(() => S.loadSources({ loin: path.join(tmp, "missing.xlsx") }), /File not found/);

// ---- stage 2 and 3: summary workbook (P9) and ranked actions --------------------------------------------------------
const key = (name, group) => merged.rows.find((r) => r.name === name && r.group === group)?.key;
const kSite = key("TST_PI_Site Name", "project"), kRoom = key("TST_AR_Room Use", "rooms"), kStGrade = key("TST_ST_Grade", "project");
const kDoor = merged.rows.find((r) => r.name === "TST_AR_Fire Rating" && r.categories?.includes("OST_Doors")).key;
const kWall = merged.rows.find((r) => r.name === "TST_AR_Fire Rating" && r.categories?.includes("OST_Walls")).key;
const checkAnswer = { documents: [
  { model: "X-ARC", discipline: "AR", disciplineSource: "mapping",
    preflight: { revitVersion: "2025", workshared: true, isCentral: false, worksets: 4, links: { count: 2, notLoaded: 1 }, phases: 2, designOptions: { sets: 0 }, groups: { instances: 3 }, blanksInGroups: 2, blanksInDesignOptions: 0, blanksBorrowed: 3, foreign: { OST_DuctCurves: 7 } },
    rows: [
      { key: kSite, name: "TST_PI_Site Name", group: "project", status: "not-bound", checked: 1, missing: 1, blank: 0 },
      { key: kRoom, name: "TST_AR_Room Use", group: "rooms", status: "blanks", checked: 40, blank: 12, na: 3, filled: 25, isText: true, storageType: "String", boundAs: "type" },
      { key: kWall, name: "TST_AR_Fire Rating", group: "elements", status: "blanks", checked: 100, blank: 60, filled: 40, isText: true, storageType: "String" },
      { key: kDoor, name: "TST_AR_Fire Rating", group: "elements", status: "blanks", checked: 5, blank: 5, isText: false, storageType: "Double" },
      { key: kStGrade, name: "TST_ST_Grade", group: "project", status: "wrong-guid", checked: 1, modelGuid: "x", guidMatch: false },
    ] },
  { model: "X-STR", discipline: "ST", preflight: { workshared: false },
    rows: [{ key: kSite, name: "TST_PI_Site Name", group: "project", status: "complete-with-na", checked: 1, na: 1 }, { key: kWall, name: "TST_AR_Fire Rating", group: "elements", status: "out-of-scope" }] },
] };
const allDefects = { ...merged.defects, twins: [{ model: "X-ARC", a: "TST_PI_A", b: "TST_PIX_A", valueA: "1", valueB: "2" }] };
const actions = A.buildActions({ result: checkAnswer, rows: merged.rows, unmappedRows: merged.unmappedRows, defects: allDefects, review: rv });
assert.deepEqual(actions.map((a) => a.rank), actions.map((_, i) => i + 1));
assert.match(actions[0].what, /^(Bind 1 parameter\(s\) to project as instance parameters in X-ARC: TST_PI_Site Name|TST_ST_Grade: the model's GUID differs from .*; confirm which is current)$/, "submission blockers on rows the client checker tests come first");
assert.ok(actions.slice(0, 2).every((a) => /checker/.test(a.why)));
const differs = actions.find((a) => /TST_ST_Grade: the model's GUID differs/.test(a.what));
assert.ok(differs.type === "needs a decision" && !/wrong|fails exports/.test(JSON.stringify(differs)) && /explicit rebind action rebind-[0-9a-f]{8}/.test(differs.options[0]) && /update the vault file from the model/.test(differs.options[1]), "DEC-0004: a differing GUID is a decision with both options, never called wrong");
const rebindAct = actions.find((a) => a.id === differs.options[0].match(/rebind-[0-9a-f]{8}/)[0]);
assert.ok(rebindAct.explicitOnly && rebindAct.run.kind === "rebind" && rebindAct.type === "needs a decision", "the rebind is explicit-only");
// Binding problems: one action per model x group x level, names inside (first 10 + count), checker-tested ones first.
const many = { documents: [{ model: "M", discipline: "AR", rows: [
  ...Array.from({ length: 12 }, (_, i) => ({ key: `u${i}`, name: `TST_U${i}`, group: "project", level: "instance", status: "not-bound", checked: 1 })),
  { key: "t1", name: "TST_T1", group: "rooms", level: "instance", status: "not-bound", checked: 4 },
  { key: "w1", name: "TST_W1", group: "project", level: "instance", status: "wrong-guid", checked: 1 },
  { key: "w2", name: "TST_W2", group: "project", level: "instance", status: "wrong-guid", checked: 1 },
] }] };
const manyRows = [...many.documents[0].rows.map((x) => ({ key: x.key, name: x.name, group: x.group, level: x.level, sources: [{ ais: 1 }] }))];
manyRows.find((r) => r.key === "t1").sources.push({ checker: "c", id: "1" });
const grouped = A.buildActions({ result: many, rows: manyRows });
assert.equal(grouped.length, 6, "13 unbound rows -> 2 actions; 2 differing GUIDs -> a decision and an explicit rebind each");
assert.match(grouped[0].what, /^Bind 1 parameter\(s\) to rooms .* in M: TST_T1$/, "the group with checker-tested parameters ranks first");
assert.match(grouped[1].what, /^Bind 12 parameter\(s\) to project as instance parameters in M: TST_U0; .*TST_U9; \.\.\. 2 more$/, "first 10 names + count");
assert.equal(grouped[1].impact.rows, 12);
assert.equal(grouped[1].impact.elements, 12, "element counts add up over the grouped parameters");
assert.deepEqual(grouped.filter((a) => /GUID differs/.test(a.what)).map((a) => a.what.split(":")[0]).sort(), ["TST_W1", "TST_W2"]);
const fillWall = actions.findIndex((a) => /Fill 60 blank/.test(a.what)), fillRoom = actions.findIndex((a) => /Fill 12 blank/.test(a.what));
assert.ok(fillWall > 1 && fillWall < fillRoom, "blanks after the blockers, the most elements first");
assert.equal(actions[fillWall].type, "auto-fixable");
assert.equal(actions[fillWall].owner, "AR");
assert.ok(actions[fillWall].blockers.some((b) => /3 element\(s\) .* borrowed by other users/.test(b.what) && /relinquish/.test(b.how)), "borrowed elements are a blocker with how to clear it");
assert.ok(actions[fillWall].blockers.some((b) => /in groups/.test(b.what)));
const nonText = actions.find((a) => /not text, so "NA" cannot be written/.test(a.what));
assert.equal(nonText.type, "needs a decision", "non-text NA policy is a decision");
const twin = actions.find((a) => /Make TST_PI_A and TST_PIX_A agree/.test(a.what));
assert.equal(twin.type, "needs a decision", "a twin pair the mapping does not list in derive is a decision");
const twinSet = X.deriveRules({ derive: { twins: { pairs: [["TST_PI_A", "TST_PIX_A"]] }, status: "approved" } });
const oneBlank = { ...allDefects, twins: [{ model: "X-ARC", a: "TST_PI_A", b: "TST_PIX_A", valueA: "1", valueB: "" }] };
const derivedTwin = A.buildActions({ result: checkAnswer, rows: merged.rows, defects: oneBlank, deriveSet: twinSet }).find((a) => /Make TST_PI_A/.test(a.what));
assert.ok(derivedTwin.type === "auto-fixable" && derivedTwin.id === twinSet.groups[0].id && derivedTwin.run.kind === "derive", "listed in derive, one side blank: auto-fixable, its id is the rule's");
assert.equal(A.buildActions({ result: checkAnswer, rows: merged.rows, defects: allDefects, deriveSet: twinSet }).find((a) => /Make TST_PI_A/.test(a.what)).type, "needs a decision", "both filled and different: ACE never overwrites, a person decides");
const unk = S.crossModel({ documents: [{ model: "M", rows: [{ name: "TST_X", unknownCategories: ["OST_Wals"] }] }] }).unknownCategories;
assert.deepEqual(unk, [{ category: "OST_Wals", models: ["M"], names: ["TST_X"] }]);
assert.match(S.defectsText({ unknownCategories: unk }), /OST_Wals \(1 row/);
const unkAct = A.buildActions({ result: { documents: [] }, defects: { unknownCategories: unk } })[0];
assert.ok(/Fix the Revit category "OST_Wals"/.test(unkAct.what) && unkAct.owner === "BIM manager");
const mapAct = actions.find((a) => /Map client category "Kitchen Things"/.test(a.what));
assert.ok(mapAct.owner === "BIM manager" && mapAct.type === "needs a decision");
const spell = actions.find((a) => /TST_AR_Fire rating/.test(a.what));
assert.ok(spell.owner === "client" && actions.indexOf(spell) > actions.indexOf(mapAct), "client defects come last");
assert.ok(actions.some((a) => a.owner === "client" && /only on Project Information/.test(a.what)), "checker scope defects from the review");
assert.ok(actions.some((a) => /Typo twins/.test(a.what) && a.type === "needs another party"));
assert.match(A.actionLine(actions[fillWall]), /^\d+\. \[fill-[0-9a-f]{8}; auto-fixable; AR\] Fill 60 blank/);
assert.equal(new Set(actions.map((a) => a.id)).size, actions.length, "every action has its own id");
assert.deepEqual(A.buildActions({ result: checkAnswer, rows: merged.rows, unmappedRows: merged.unmappedRows, defects: allDefects, review: rv }).map((a) => a.id), actions.map((a) => a.id), "ids are stable");
assert.match(A.actionsMarkdown(actions), /- Blocker: 3 element\(s\) with blanks in X-ARC are borrowed by other users\. How to clear: ask them/);

assert.equal(A.summaryTable({ documents: [{ model: "M", rows: [{ status: "unmapped" }] }] }, [{ sources: [] }])[0].unmapped, 2, "the add-in's unmapped rows plus the server's");
const table = A.summaryTable(checkAnswer, merged.unmappedRows);
assert.deepEqual(table.map((l) => [l.model, l.rows, l.blanks, l["not-bound"], l["guid-differs"], l["not text"], l.unmapped, l["complete-with-na"], l["out-of-scope"]]),
  [["X-ARC", 5, 3, 1, 1, 1, 1, 0, 0], ["X-STR", 2, 0, 0, 0, 0, 0, 1, 1], ["All models", 7, 3, 1, 1, 1, 1, 1, 1]], "unmapped rows count where their sheet's discipline is");
assert.match(A.summaryMarkdown(checkAnswer, merged.unmappedRows), /\| AR \| X-ARC \| 5 \| 0 \| 0 \| 3 \|/);
const book = A.summaryWorkbook(checkAnswer, merged.rows, merged.unmappedRows, actions);
const back = readXlsx(book);
assert.deepEqual(back.sheets.map((x) => x.name), ["Summary", "Rows", "Actions", "Pre-flight"]);
const sheetRows = (n) => back.units.filter((u) => u.sheet === n);
assert.deepEqual(Object.values(sheetRows("Summary")[0].cells).slice(0, 4), ["discipline", "model", "rows", "complete"]);
assert.equal(sheetRows("Summary")[1].cells.F, "3", "numbers stay numbers (blanks of X-ARC)");
assert.equal(sheetRows("Rows").length, 1 + 7 + merged.unmappedRows.length, "one row per model x requirement, plus the unmapped ones");
assert.ok(sheetRows("Rows").some((u) => u.cells.G === "unmapped" && /LOIN S1_ARC!8/.test(u.cells.P)));
assert.ok(sheetRows("Rows").some((u) => u.cells.A === "X-ARC" && u.cells.C === "TST_PI_Site Name" && /AIS line 2; checker/.test(u.cells.P)), "sources per row");
assert.equal(sheetRows("Actions").length, actions.length + 1);
assert.match(sheetRows("Pre-flight")[1].cells.R, /OST_DuctCurves: 7/);
const zipped = unzip(book);
assert.match(zipped.read("xl/worksheets/sheet1.xml"), /<pane ySplit="1"[^>]*state="frozen"\/>/, "frozen header row");
assert.match(zipped.read("xl/worksheets/sheet2.xml"), /<autoFilter ref="A1:R\d+"\/>/, "autofilter");
assert.match(zipped.read("xl/worksheets/sheet1.xml"), /<c r="A1" t="inlineStr" s="1">/, "bold header style");
assert.match(zipped.read("xl/styles.xml"), /<font><b\/>/);

fs.rmSync(tmp, { recursive: true, force: true });
console.log("paramsources test passed");

// ---- project-shaped mapping (per-sheet categories, alias sections with a status) ------------------------------------
{
  const raw = {
    status: "draft - not approved",
    loinCategories: { "01_TST": { "Walls": { bic: ["OST_Walls"], confidence: "high" }, "Louvres": { bic: ["OST_Windows"], filter: "name contains louv" } } },
    nameAliases: { prefixMap: { "TST_XA_": "TST_XAB_" }, explicit: { "TST_AR_Oddname": "TST_AR_Odd Name" }, status: "proposed" },
  };
  const m = S.normaliseMapping(raw);
  assert.equal(m.draft, true, "not approved -> draft");
  assert.deepEqual(m.sheetCategories["01_TST"].Walls, ["OST_Walls"]);
  assert.equal(m.filters.length, 1, "filter hints kept for the report");
  assert.deepEqual(S.aliasFor(m, "TST_XA_Size"), { to: "TST_XAB_Size", approved: false });
  const idx = S.paramIndex([{ file: "c.txt", kind: "client", params: [{ name: "TST_XAB_Size", guid: "g1", dataType: "TEXT" }] }]);
  const c = S.classifyName("TST_XA_Size", idx, m);
  assert.equal(c.match, "alias-proposed", "a proposed alias never passes a name");
  assert.equal(c.name, "TST_XA_Size"); assert.equal(c.candidate, "TST_XAB_Size");
  const ok = S.normaliseMapping({ ...raw, nameAliases: { ...raw.nameAliases, status: "approved 2026-10-19" } });
  const c2 = S.classifyName("TST_XA_Size", idx, ok);
  assert.equal(c2.match, "alias"); assert.equal(c2.name, "TST_XAB_Size"); assert.equal(c2.guid, "g1");
  assert.deepEqual(S.normaliseMapping({ categories: { A: [] } }), { categories: { A: [] } }, "ACE's own shape unchanged");
  const merged = S.mergeSources({ loin: [{ sheet: "01_TST", row: 2, category: "Walls", name: "TST_AR_Wallthing" }], mapping: m });
  assert.deepEqual(merged.rows[0].categories, ["OST_Walls"], "per-sheet category used");
  console.log("project-shaped mapping ok");
}

// ---- name authority: the client's files decide the expected GUID -----------------------------------------------------
{
  const idx = S.paramIndex([
    { file: "p.txt", kind: "project", params: [{ name: "TST_PI_Plot", guid: "11111111-0000-0000-0000-000000000001", dataType: "TEXT" }] },
    { file: "c.txt", kind: "client", params: [{ name: "TST_PI_Plot", guid: "22222222-0000-0000-0000-000000000002", dataType: "TEXT" }] },
  ]);
  assert.equal(S.classifyName("TST_PI_Plot", idx).guid, "11111111-0000-0000-0000-000000000001", "default: the project file");
  idx.authority = "client";
  const c = S.classifyName("TST_PI_Plot", idx);
  assert.equal(c.guid, "22222222-0000-0000-0000-000000000002", "client authority");
  assert.equal(c.otherGuid, "11111111-0000-0000-0000-000000000001", "the differing project GUID is flagged");
  const noSource = S.normaliseMapping({ nameAliases: {}, nameAuthority: { status: "approved" } });
  assert.ok(noSource.nameAuthority === undefined && /no source/.test(noSource.notes.join()), "an authority without a source is not assumed");
  assert.equal(S.normaliseMapping({ nameAliases: {}, nameAuthority: { status: "approved", source: "client" } }).nameAuthority, "client");
  assert.equal(S.normaliseMapping({ nameAliases: {}, nameAuthority: { status: "proposed" } }).nameAuthority, undefined, "only an approved authority counts");
  console.log("name authority ok");
}
