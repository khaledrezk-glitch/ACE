// Registers / unregisters the "ace-revit" MCP server everywhere Claude reads MCP servers from:
//   - Claude Desktop chats:  %APPDATA%\Claude\claude_desktop_config.json (+ the Microsoft Store app's copy)
//   - Claude Code (CLI and the desktop app's Code sessions): ~/.claude.json  (user scope "mcpServers")
// Everything else in those files is preserved; only the exact key "ace-revit" is touched.
//   node lib/register.js add [--node <node.exe>] [--index <index.js>]
//   node lib/register.js remove
//   node lib/register.js status        (exit code 1 if any existing config lacks the entry)
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const KEY = "ace-revit";
const HERE = path.dirname(fileURLToPath(import.meta.url));

export function configTargets() {
  const appData = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
  const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
  const targets = [{ kind: "Claude Desktop", file: path.join(appData, "Claude", "claude_desktop_config.json"), create: true }];
  try {
    for (const d of fs.readdirSync(path.join(localAppData, "Packages")).filter((d) => d.startsWith("Claude_"))) {
      targets.push({ kind: "Claude Desktop (Store app)", file: path.join(localAppData, "Packages", d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), create: true });
    }
  } catch {
    /* not a Store install */
  }
  targets.push({ kind: "Claude Code", file: path.join(process.env.ACE_CLAUDE_CODE_CONFIG_DIR || os.homedir(), ".claude.json"), create: true });
  return targets;
}

function readJson(file) {
  const raw = fs.readFileSync(file, "utf8").replace(/^﻿/, "");
  return raw.trim() ? JSON.parse(raw) : {};
}

function writeJson(file, data) {
  const tmp = `${file}.ace-tmp`;
  fs.writeFileSync(tmp, JSON.stringify(data, null, 2) + "\n");
  fs.renameSync(tmp, file); // atomic replace: never leaves a half-written config
}

export function entry(nodeExe, indexJs, kind) {
  const e = { command: nodeExe, args: [indexJs] };
  return kind === "Claude Code" ? { type: "stdio", ...e, env: {} } : e;
}

export function add(nodeExe, indexJs) {
  const results = [];
  for (const t of configTargets()) {
    try {
      let data = {};
      if (fs.existsSync(t.file)) {
        data = readJson(t.file);
        fs.copyFileSync(t.file, `${t.file}.ace-backup`);
      } else if (t.kind === "Claude Code" && !fs.existsSync(path.dirname(t.file))) {
        continue;
      } else {
        fs.mkdirSync(path.dirname(t.file), { recursive: true });
      }
      if (!data.mcpServers || typeof data.mcpServers !== "object") data.mcpServers = {};
      data.mcpServers[KEY] = entry(nodeExe, indexJs, t.kind);
      writeJson(t.file, data);
      results.push({ ...t, ok: true });
    } catch (err) {
      results.push({ ...t, ok: false, error: err.message });
    }
  }
  return results;
}

export function remove() {
  const results = [];
  for (const t of configTargets()) {
    if (!fs.existsSync(t.file)) continue;
    try {
      const data = readJson(t.file);
      if (data.mcpServers && KEY in data.mcpServers) {
        delete data.mcpServers[KEY];
        writeJson(t.file, data);
        results.push({ ...t, ok: true, removed: true });
      }
    } catch (err) {
      results.push({ ...t, ok: false, error: err.message });
    }
  }
  return results;
}

export function status() {
  return configTargets()
    .filter((t) => fs.existsSync(t.file))
    .map((t) => {
      try {
        const e = readJson(t.file)?.mcpServers?.[KEY];
        const target = e?.args?.[0];
        return { ...t, registered: !!e, entry: e || null, targetExists: target ? fs.existsSync(target) : false, nodeExists: e?.command ? fs.existsSync(e.command) : false };
      } catch (err) {
        return { ...t, registered: false, error: `not valid JSON: ${err.message}` };
      }
    });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [, , cmd, ...rest] = process.argv;
  const opt = (name, fallback) => {
    const i = rest.indexOf(`--${name}`);
    return i >= 0 ? rest[i + 1] : fallback;
  };
  if (cmd === "add") {
    const results = add(opt("node", process.execPath), opt("index", path.join(HERE, "..", "index.js")));
    for (const r of results) console.log(`${r.ok ? "OK  " : "FAIL"} ${r.kind}: ${r.file}${r.error ? ` (${r.error})` : ""}`);
    process.exit(results.some((r) => !r.ok) ? 1 : 0);
  } else if (cmd === "remove") {
    for (const r of remove()) console.log(`${r.ok ? "removed from" : "FAIL"} ${r.kind}: ${r.file}${r.error ? ` (${r.error})` : ""}`);
  } else if (cmd === "status") {
    const s = status();
    for (const r of s) console.log(`${r.registered && r.targetExists && r.nodeExists ? "OK  " : "FAIL"} ${r.kind}: ${r.file}${r.error ? ` (${r.error})` : r.registered ? (r.targetExists ? "" : " (points to a missing index.js)") : " (no ace-revit entry)"}`);
    process.exit(s.some((r) => !(r.registered && r.targetExists && r.nodeExists)) ? 1 : 0);
  } else {
    console.log("usage: node lib/register.js add [--node path] [--index path] | remove | status");
  }
}
