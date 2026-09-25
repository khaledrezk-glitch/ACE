// Script library: built-in (shipped), team (shared folder from config), user (personal).
// Later sources override earlier ones with the same name: built-in < team < user.
import fs from "node:fs";
import path from "node:path";
import { BUILTIN_SCRIPTS, USER_SCRIPTS, RevitError, teamScriptsDir } from "./core.js";

function parseScript(file, source) {
  const meta = { name: path.basename(file, ".cs"), file, source };
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*\/\/\s*@(\w+):\s*(.*)$/);
    if (m) meta[m[1]] = m[2].trim();
    else if (line.trim() && !line.trim().startsWith("//")) break;
  }
  return meta;
}

export function scriptSources() {
  const team = teamScriptsDir();
  return [
    [BUILTIN_SCRIPTS, "built-in"],
    ...(team ? [[team, "team"]] : []),
    [USER_SCRIPTS, "user"],
  ];
}

export function listScripts() {
  const out = new Map();
  for (const [dir, source] of scriptSources()) {
    let files = [];
    try {
      files = fs.readdirSync(dir).filter((f) => f.endsWith(".cs"));
    } catch {
      continue; // missing or offline share
    }
    for (const f of files) {
      try {
        const meta = parseScript(path.join(dir, f), source);
        out.set(meta.name, meta);
      } catch {
        /* unreadable file */
      }
    }
  }
  return [...out.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export function findScript(name) {
  const script = listScripts().find((s) => s.name.toLowerCase() === String(name).toLowerCase());
  if (!script) throw new RevitError(`No saved script named '${name}'. Call list_saved_scripts to see what exists.`);
  return script;
}

export function saveScript({ name, description, code, mode, inputs_example, overwrite, scope }) {
  let dir = USER_SCRIPTS;
  if (scope === "team") {
    dir = teamScriptsDir();
    if (!dir) throw new RevitError("No team script folder is configured. Set \"teamScriptsDir\" in config.json (see TEAM-DEPLOYMENT.md), or save with scope \"user\".");
  }
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${name}.cs`);
  if (fs.existsSync(file) && !overwrite) throw new RevitError(`A script named '${name}' already exists in ${scope || "user"} scripts. Pass overwrite: true to replace it.`);
  const body = code.replace(/^\s*\/\/\s*@\w+:.*\r?\n/gm, "");
  const header = [
    `// @description: ${description.replace(/\r?\n/g, " ")}`,
    `// @mode: ${mode || "auto"}`,
    `// @inputs: ${JSON.stringify(inputs_example || {})}`,
    `// @saved: ${new Date().toISOString()} by ${process.env.USERNAME || process.env.USER || "unknown"}`,
    "",
  ].join("\n");
  fs.writeFileSync(file, header + "\n" + body.trimStart());
  return file;
}
