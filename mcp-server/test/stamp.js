// Run record stamp (T13): the rules hash is canonical (key order, CRLF / LF do not matter) and matches the add-in's.
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { canonical, rulesHash, short, stamp, vaultInputs, label } from "../lib/stamp.js";

const fx = JSON.parse(fs.readFileSync(new URL("./fixtures/rules-hash.json", import.meta.url), "utf8"));
for (const c of fx.cases) {
  assert.equal(canonical(c.input), c.canonical, `canonical form: ${c.name}`);
  assert.equal(rulesHash(c.input), c.sha256, `hash: ${c.name}`);
}
const a = { checks: { doors: { failAt: 1, warnAt: 0 }, rooms: { enabled: false } }, list: [1, 2] };
const b = { list: [1, 2], checks: { rooms: { enabled: false }, doors: { warnAt: 0, failAt: 1 } } };
assert.equal(rulesHash(a), rulesHash(b), "reordered keys: the same hash");
assert.notEqual(rulesHash(a), rulesHash({ ...a, list: [2, 1] }), "array order counts");
assert.equal(rulesHash({ q: "a\r\nb" }), rulesHash({ q: "a\nb" }), "CRLF inside a string: the same hash as LF");
assert.equal(short("0123456789abcdef"), "0123456789ab");

// The vault's inputs read from CRLF files and from LF files (keys in another order) give one hash.
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-stamp-"));
const write = (root, eol, reorder) => {
  const req = reorder ? { records: [{ source: { quote: "Doors 900 mm" }, id: "REQ-1" }], schema: "ace.requirements/1" } : { schema: "ace.requirements/1", records: [{ id: "REQ-1", source: { quote: "Doors 900 mm" } }] };
  const files = { "requirements.json": req, "decisions.json": { decisions: [{ id: "DEC-0001", note: "agreed\nwith the client" }] },
    "project-requirements.json": { schema: "ace.project-requirements/1", checks: { doors: { requirement: "REQ-1", failAt: 1 } } }, "rules/export.json": { nameTemplate: "{model}_{date}" } };
  for (const [f, j] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(root, f)), { recursive: true });
    fs.writeFileSync(path.join(root, f), (eol === "\r\n" ? "﻿" : "") + JSON.stringify(j, null, 2).replace(/\n/g, eol).replace(/\\n/g, eol === "\r\n" ? "\\r\\n" : "\\n"));
  }
  return root;
};
const lf = vaultInputs(write(path.join(tmp, "lf"), "\n", false)), crlf = vaultInputs(write(path.join(tmp, "crlf"), "\r\n", true));
assert.deepEqual(Object.keys(lf).sort(), ["decisions", "project-requirements", "requirements", "rules/export.json"], "the vault's rule inputs, with vault-relative names");
assert.equal(rulesHash(lf), rulesHash(crlf), "CRLF files with a BOM and reordered keys: the same rules hash as LF files");
const s = stamp(lf, { aceVersion: "1.15.0" });
assert.ok(s.aceVersion === "1.15.0" && /^\d+\.\d+\.\d+/.test(s.mcpVersion) && s.rulesHash === rulesHash(lf) && s.rulesInputs.length === 4, "stamp: versions, hash, input names");
assert.equal(label(s), `ACE 1.15.0 · rules ${s.rulesHash.slice(0, 12)}`);
assert.equal(label(null), "ACE (version not recorded) · rules (not recorded)", "an older record without a stamp says so");
assert.deepEqual(vaultInputs(path.join(tmp, "none")), {}, "no vault: no inputs");
fs.rmSync(tmp, { recursive: true, force: true });
console.log("stamp test passed");
