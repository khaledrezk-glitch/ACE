// Several Revit sessions: the instance files, the live check (/health answers the same pid) and the choice.
// Usage: node test/instances.js   (no Revit needed)
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";

const dir = fs.mkdtempSync(path.join(os.tmpdir(), "ace-inst-"));
process.env.ACE_REVIT_INSTANCES = dir;
delete process.env.ACE_REVIT_URL;
const core = await import("../lib/core.js");

const a = { port: 1, pid: 11, revitVersion: "2025", activeDocument: "TST-AR", openDocuments: ["TST-AR", "TST-ST"], updated: "2026-10-19T10:00:00Z" };
const b = { port: 2, pid: 22, revitVersion: "2024", activeDocument: "TST-ME", openDocuments: ["TST-ME"], updated: "2026-10-19T11:00:00Z" };
assert.equal(core.pickInstance([a, b], "TST-ME.rvt").chosen, b, "the active document matches");
assert.equal(core.pickInstance([a, b], "tst-st").chosen, a, "an open document matches");
assert.equal(core.pickInstance([a, b], "22").chosen, b, "a pid matches");
const none = core.pickInstance([b, a]);
assert.ok(none.chosen === null && none.ambiguous && /2 Revit sessions have a model open: Revit 2024 pid 22.*Revit 2025 pid 11.*does not guess/.test(none.note), "two sessions with a model and no hint: ask, never guess");
const empty = { port: 3, pid: 33, revitVersion: "2025", openDocuments: [], updated: "2026-10-19T12:00:00Z" };
const one = core.pickInstance([empty, a]);
assert.ok(one.chosen === a && /using Revit 2025 pid 11.*the only one with a model open.*Others: Revit 2025 pid 33/.test(one.note), "a session with no model is never the default when another has one");
const noModels = core.pickInstance([empty, { port: 4, pid: 44 }]);
assert.ok(noModels.chosen === empty && !noModels.ambiguous, "no model anywhere: the most recent");
const twice = core.pickInstance([b, { ...b, pid: 55, port: 5 }], "TST-ME", { exact: true });
assert.equal(twice.chosen.pid, 22, "the same model in two sessions: the most recent of the matching ones");
assert.ok(core.pickInstance([a]).note === null && core.pickInstance([], "x").chosen === null);
const sub = core.pickInstance([b, a], "TST-S");
assert.ok(sub.chosen === a && /titled exactly "TST-S".*contains it/.test(sub.note), "a part of a title is only a fallback, with a note");
const ex = core.pickInstance([b, a], "TST-S", { exact: true });
assert.ok(ex.chosen === null && ex.ambiguous && /none has "TST-S" open/.test(ex.note), "never a part of a title for expect_model: ask");

// Live files: a session whose /health answers its pid is kept; a dead pid's file is deleted.
const server = http.createServer((req, res) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify({ ok: true, service: "ace-revit-mcp", pid: process.pid, port: server.address().port })); });
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const port = server.address().port;
fs.writeFileSync(path.join(dir, `${process.pid}.json`), JSON.stringify({ port, pid: process.pid, revitVersion: "2025", activeDocument: "TST-LIVE", openDocuments: ["TST-LIVE"], updated: new Date().toISOString() }));
const host = os.hostname();
fs.writeFileSync(path.join(dir, "999999.json"), JSON.stringify({ port: 9, pid: 999999, machine: host, activeDocument: "TST-DEAD" }));
fs.writeFileSync(path.join(dir, "1.json"), JSON.stringify({ port, pid: 1, machine: host }));   // a running pid whose /health answers another pid
fs.writeFileSync(path.join(dir, "888888.json"), JSON.stringify({ port: 9, pid: 888888, machine: "TST-OTHER-PC" }));   // another machine's file
fs.writeFileSync(path.join(dir, "notes.json"), JSON.stringify({ port: 9, pid: 777777, machine: host }));   // not an instance file name
const { list: live, notes } = await core.listInstances();
assert.deepEqual(live.map((s) => s.activeDocument), ["TST-LIVE"], "only live sessions");
assert.ok(!fs.existsSync(path.join(dir, "999999.json")) && !fs.existsSync(path.join(dir, "1.json")), "stale files of this machine are deleted");
assert.ok(fs.existsSync(path.join(dir, "888888.json")) && fs.existsSync(path.join(dir, "notes.json")), "another machine's file and other names are left alone");
assert.deepEqual(notes, []);
const pick = await core.chooseInstance("TST-LIVE");
assert.equal(pick.chosen.port, port);
assert.equal(core.bridgeUrl({ port: 48884 }, pick.chosen.port), `http://localhost:${port}`, "the chosen session's port");
// A second live session with another model: no default, the call asks (a write never goes to a guessed Revit).
const server2 = http.createServer((req, res) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify({ ok: true, service: "ace-revit-mcp", pid: process.ppid })); });
await new Promise((r) => server2.listen(0, "127.0.0.1", r));
fs.writeFileSync(path.join(dir, `${process.ppid}.json`), JSON.stringify({ port: server2.address().port, pid: process.ppid, activeDocument: "TST-LIVE2", openDocuments: ["TST-LIVE2"] }));
core.forgetInstances();
await assert.rejects(core.currentCall.run({ notes: new Set() }, () => core.resolveSession()), /2 Revit sessions have a model open.*TST-LIVE2|2 Revit sessions have a model open.*TST-LIVE/);
assert.equal((await core.currentCall.run({ notes: new Set() }, () => core.resolveSession("TST-LIVE2", { exact: true }))).pid, process.ppid, "expect_model decides");
fs.unlinkSync(path.join(dir, `${process.ppid}.json`));
server2.close();
core.forgetInstances();
// The preview gate remembers the session: an apply that would reach another Revit is refused.
const { gated } = await import("../lib/safety.js");
const A1 = { pid: 11, port: 1, machine: "TST", activeDocument: "TST-AR" }, B1 = { pid: 22, port: 2, machine: "TST", activeDocument: "TST-AR" };
const run = (pinned, f) => core.currentCall.run({ notes: new Set(), pinned }, f);
await run(A1, () => gated("k", { apply: false }, async () => ({ success: true }), (r) => r.success));
await assert.rejects(run(B1, () => gated("k", { apply: true, explanation: "x" }, async () => ({ success: true }), () => true)), /preview was made in Revit session 11 \(TST-AR, port 1\), this call would go to 22/);
assert.deepEqual(await run(A1, () => gated("k", { apply: true, explanation: "x" }, async () => ({ success: true }), () => true)), { success: true }, "the same session applies");
// A file in place of the instances folder is said, not hidden.
fs.rmSync(dir, { recursive: true, force: true });
fs.writeFileSync(dir, "x");
assert.match((await core.listInstances()).notes.join(), /is not a folder/);
fs.rmSync(dir, { force: true });
server.close();
console.log("instances test passed");
