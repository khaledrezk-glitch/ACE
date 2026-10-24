// Tests lib/register.js against realistic configs in a temp "profile".
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-reg-"));
process.env.APPDATA = path.join(tmp, "Roaming");
process.env.LOCALAPPDATA = path.join(tmp, "Local");
process.env.ACE_CLAUDE_CODE_CONFIG_DIR = tmp;

const desktop = path.join(tmp, "Roaming", "Claude", "claude_desktop_config.json");
const store = path.join(tmp, "Local", "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
const code = path.join(tmp, ".claude.json");
fs.mkdirSync(path.dirname(desktop), { recursive: true });
fs.mkdirSync(path.dirname(store), { recursive: true });
const nonica = { command: "C:\\NONICA\\RevitMCPConnection.exe", args: [] };
fs.writeFileSync(desktop, "\uFEFF" + JSON.stringify({ mcpServers: { Revit: nonica }, preferences: { sidebarMode: "x" } }));
fs.writeFileSync(store, JSON.stringify({ mcpServers: { Revit: nonica } }));
const codeCfg = { numStartups: 12, projects: { "C:/x": { history: [{ display: "hi" }], mcpServers: {} } }, mcpServers: { other: { type: "stdio", command: "x", args: [] } } };
fs.writeFileSync(code, JSON.stringify(codeCfg));

const { add, remove, status } = await import("../lib/register.js");
const index = path.join(tmp, "index.js");
fs.writeFileSync(index, "");
const results = add(process.execPath, index);
assert.equal(results.length, 3);
assert.ok(results.every((r) => r.ok), JSON.stringify(results));

const d = JSON.parse(fs.readFileSync(desktop, "utf8"));
assert.deepEqual(d.mcpServers.Revit, nonica, "Nonica 'Revit' entry must survive");
assert.equal(d.mcpServers["ace-revit"].args[0], index);
assert.equal(d.preferences.sidebarMode, "x");
assert.deepEqual(JSON.parse(fs.readFileSync(store, "utf8")).mcpServers.Revit, nonica);
const c = JSON.parse(fs.readFileSync(code, "utf8"));
assert.equal(c.mcpServers["ace-revit"].type, "stdio");
assert.equal(c.mcpServers.other.command, "x");
assert.equal(c.numStartups, 12);
assert.equal(c.projects["C:/x"].history[0].display, "hi");
assert.ok(fs.existsSync(desktop + ".ace-backup"));

assert.ok(status().every((s) => s.registered && s.targetExists && s.nodeExists));
add(process.execPath, index); // idempotent
assert.equal(Object.keys(JSON.parse(fs.readFileSync(desktop, "utf8")).mcpServers).length, 2);

remove();
assert.ok(status().every((s) => !s.registered));
assert.deepEqual(JSON.parse(fs.readFileSync(desktop, "utf8")).mcpServers.Revit, nonica);
assert.equal(JSON.parse(fs.readFileSync(code, "utf8")).mcpServers.other.command, "x");

fs.writeFileSync(store, "{ broken json");
assert.equal(add(process.execPath, index).find((r) => r.file === store).ok, false, "invalid JSON is reported, not overwritten");
assert.equal(fs.readFileSync(store, "utf8"), "{ broken json");

fs.rmSync(tmp, { recursive: true, force: true });
console.log("register test passed");
