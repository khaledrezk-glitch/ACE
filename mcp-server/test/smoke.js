// Smoke test: runs the MCP server against a fake Revit bridge and exercises every tool path.
// Usage: node test/smoke.js   (no Revit needed)
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-smoke-"));
const token = "test-token";
const seen = [];

const bridge = http.createServer((req, res) => {
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    const reply = (o) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(o)); };
    if (req.headers["x-ace-token"] !== token) return reply({ ok: false, error: "bad token" });
    const { command, args } = JSON.parse(body);
    seen.push({ command, args });
    switch (command) {
      case "ping": return reply({ ok: true, result: { revit: "Autodesk Revit 2025", activeDocument: "Test.rvt" } });
      case "export_view_image": return reply({ ok: true, result: { view: "Level 1", viewType: "FloorPlan", path: "x.png", mimeType: "image/png", base64: "iVBORw0KGgo=" } });
      case "execute_code":
        if (args.code.includes("boom")) return reply({ ok: true, result: { success: false, stage: "compile", errors: ["line 1: CS0103"] } });
        return reply({ ok: true, result: { success: true, transaction: args.dry_run ? "RolledBack" : "Committed", result: 42, echoMode: args.mode, inputs: args.inputs, [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 2, deleted: 0 } } });
      case "set_parameters":
        return reply({ ok: true, result: { applied: args.changes.length, failed: 0, dryRun: !!args.dry_run, results: [] } });
      case "backup_model":
        return reply({ ok: true, result: { backup: "C:/backups/model.rvt", savedBeforeBackup: false } });
      case "undo_last_claude_change":
        return reply({ ok: true, result: { undoRequested: "Claude: test" } });
      default: return reply({ ok: true, result: { command, args } });
    }
  });
});
await new Promise((r) => bridge.listen(0, "127.0.0.1", r));
const port = bridge.address().port;
fs.writeFileSync(path.join(tmp, "config.json"), JSON.stringify({ port, token }));

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(path.dirname(new URL(import.meta.url).pathname), "..", "index.js")],
  env: { ...process.env, ACE_REVIT_CONFIG: path.join(tmp, "config.json"), ACE_REVIT_URL: `http://127.0.0.1:${port}`, ACE_REVIT_SCRIPTS: path.join(tmp, "scripts"), ACE_REVIT_JOURNAL: path.join(tmp, "journal") },
});
const client = new Client({ name: "smoke", version: "1.0.0" });
await client.connect(transport);

const call = async (name, args = {}) => client.callTool({ name, arguments: args });
const json = (r) => JSON.parse(r.content[0].text);

const tools = (await client.listTools()).tools.map((t) => t.name).sort();
assert.deepEqual(tools, ["backup_model", "execute_revit_code", "find_elements", "get_activity_log", "get_element_details", "get_model_overview", "get_selection", "list_saved_scripts", "list_views", "read_saved_script", "revit_status", "run_saved_script", "save_script", "select_elements", "set_parameters", "undo_last_claude_change", "view_image"]);
assert.match(client.getInstructions(), /SAFETY PROTOCOL/);

assert.equal(json(await call("revit_status")).activeDocument, "Test.rvt");
assert.equal(json(await call("find_elements", { categories: ["Walls"], limit: 5 })).command, "query_elements");

const img = await call("view_image", {});
assert.equal(img.content[0].type, "image");

// --- Safety gate: no real change without an identical preview ---
const edit = { code: "foreach (var w in ctx.All<Wall>()) w.LookupParameter(\"Comments\").Set(\"x\");", inputs: { a: 1 } };
const blocked = await call("execute_revit_code", { ...edit, explanation: "test" });
assert.equal(blocked.isError, true);
assert.match(blocked.content[0].text, /not been previewed/);
const preview = await call("execute_revit_code", { ...edit, dry_run: true });
assert.equal(json(preview).transaction, "RolledBack");
assert.match(preview.content[1].text, /PREVIEW ONLY/);
const noExplanation = await call("execute_revit_code", edit);
assert.match(noExplanation.content[0].text, /explanation/);
const changedInputs = await call("execute_revit_code", { ...edit, inputs: { a: 2 }, explanation: "test" });
assert.match(changedInputs.content[0].text, /not been previewed/);
const applied = json(await call("execute_revit_code", { ...edit, explanation: "Set wall comments" }));
assert.equal(applied.transaction, "Committed");
const again = await call("execute_revit_code", { ...edit, explanation: "again" });
assert.match(again.content[0].text, /not been previewed/, "a repeat needs a fresh preview");
// read-only runs need no preview
assert.equal(json(await call("execute_revit_code", { code: "return 1;", mode: "readonly" })).echoMode, "readonly");

// --- Risky code is blocked unless explicitly allowed ---
const risky = await call("execute_revit_code", { code: "System.IO.File.Delete(\"c:/x\");", mode: "readonly" });
assert.match(risky.content[0].text, /files on disk/);
const saving = await call("execute_revit_code", { code: "doc.Save();", mode: "readonly" });
assert.match(saving.content[0].text, /saves or closes/);
assert.equal((await call("execute_revit_code", { code: "doc.Save();", mode: "readonly", allow_risky: true })).isError, undefined);

// --- set_parameters has the same gate ---
const changes = [{ id: 1, parameter: "Comments", value: "x" }];
assert.match((await call("set_parameters", { changes, explanation: "t" })).content[0].text, /not been previewed/);
await call("set_parameters", { changes, dry_run: true });
assert.equal(json(await call("set_parameters", { changes, explanation: "Set comment" })).applied, 1);

assert.equal(json(await call("backup_model", {})).backup, "C:/backups/model.rvt");
assert.equal(json(await call("undo_last_claude_change", {})).undoRequested, "Claude: test");
const log = (await call("get_activity_log", {})).content[0].text;
assert.match(log, /PREVIEW only/);
assert.match(log, /APPLIED \(added 1, modified 2, deleted 0\)/);
assert.match(log, /Set wall comments/);
assert.match(log, /Undid/);
const bad = await call("execute_revit_code", { code: "boom" });
assert.equal(bad.isError, true);

const lib = json(await call("list_saved_scripts"));
assert.ok(lib.find((s) => s.name === "renumber_rooms" && s.mode === "auto"));
assert.ok(lib.find((s) => s.name === "audit_model" && s.mode === "readonly"));

const run = json(await call("run_saved_script", { name: "audit_model" })); // readonly script: no preview needed
assert.equal(run.echoMode, "readonly");

await call("save_script", { name: "my_task", description: "Test", code: "return ctx.Num(\"n\");", mode: "readonly", inputs_example: { n: 1 } });
const mine = json(await call("list_saved_scripts")).find((s) => s.name === "my_task");
assert.equal(mine.source, "user");
const mineRun = json(await call("run_saved_script", { name: "my_task", inputs: { n: 7 } }));
assert.equal(mineRun.echoMode, "readonly");
assert.equal(mineRun.inputs.n, 7);
// modifying saved scripts are gated too
assert.match((await call("run_saved_script", { name: "renumber_rooms", explanation: "t" })).content[0].text, /not been previewed/);
assert.equal((await call("save_script", { name: "my_task", description: "x", code: "return 1;" })).isError, true);

// Revit down -> friendly error, not a crash
bridge.close();
const down = await call("revit_status");
assert.equal(down.isError, true);
assert.match(down.content[0].text, /not reachable/);

await client.close();
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`smoke test passed (${seen.length} bridge calls)`);
