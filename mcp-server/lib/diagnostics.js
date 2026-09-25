// Health checks, usage analysis and Markdown issue / improvement reports.
// Used by the MCP tools check_setup + report_issue, and from the command line by doctor.ps1:
//   node lib/diagnostics.js check [--json]
//   node lib/diagnostics.js report --kind issue|improvement|health --note "what happened" [--extra file.md]
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  APPDATA, CONFIG_PATH, LOG_DIR, REPORTS_DIR, VERSION, ROOT,
  callRevit, health, readConfig, teamScriptsDir, teamReportsDir,
} from "./core.js";
import { readCalls } from "./telemetry.js";
import { listScripts } from "./scripts.js";

const isWindows = process.platform === "win32";
const ADDIN_DIR = path.join(APPDATA, "Autodesk", "Revit", "Addins", "2025");
const ADDIN_LOG = path.join(LOG_DIR, "addin.log");

const check = (name, status, detail, fix) => ({ name, status, detail, ...(fix ? { fix } : {}) });

export async function runChecks({ deep = true } = {}) {
  const checks = [];
  const [major] = process.versions.node.split(".").map(Number);
  checks.push(check("Node.js", major >= 18 ? "ok" : "fail", `v${process.versions.node}`, major >= 18 ? null : "Install Node.js 18+ (install.ps1 does this with winget)."));
  checks.push(check("MCP server", "ok", `ace-revit-mcp ${VERSION} at ${ROOT}`));

  const cfg = readConfig();
  checks.push(
    cfg?.token
      ? check("Config", "ok", `${CONFIG_PATH} (port ${cfg.port})`)
      : check("Config", "fail", `${CONFIG_PATH} missing or has no token`, "Run install.ps1 (or doctor.ps1 -Fix), then restart Revit."),
  );

  if (isWindows) {
    const manifest = path.join(ADDIN_DIR, "AceRevitMcp.addin");
    const dll = path.join(ADDIN_DIR, "AceRevitMcp", "AceRevitMcp.dll");
    const compiler = path.join(ADDIN_DIR, "AceRevitMcp", "Compiler", "AceRevitMcp.Compiler.dll");
    const missing = [manifest, dll, compiler].filter((f) => !fs.existsSync(f));
    checks.push(
      missing.length === 0
        ? check("Revit add-in files", "ok", path.dirname(dll))
        : check("Revit add-in files", "fail", `Missing: ${missing.join(", ")}`, "Close Revit and run install.ps1 (or doctor.ps1 -Fix)."),
    );
    try {
      const others = fs.readdirSync(ADDIN_DIR).filter((f) => f.endsWith(".addin") && f !== "AceRevitMcp.addin");
      if (others.length) checks.push(check("Other Revit add-ins", "info", others.join(", ")));
    } catch {
      /* no add-ins folder */
    }
    for (const file of claudeDesktopConfigs()) {
      try {
        const conf = JSON.parse(fs.readFileSync(file, "utf8").replace(/^﻿/, ""));
        const has = conf?.mcpServers && Object.keys(conf.mcpServers).includes("ace-revit");
        checks.push(
          has
            ? check("Claude Desktop registration", "ok", file)
            : check("Claude Desktop registration", "fail", `No "ace-revit" server in ${file}`, "Run install.ps1 -SkipAddin (or doctor.ps1 -Fix), then fully quit and reopen Claude Desktop."),
        );
      } catch (err) {
        checks.push(check("Claude Desktop registration", "warn", `${file}: ${err.message}`, "Check the file is valid JSON."));
      }
    }
  }

  const h = await health();
  if (!h.ok) {
    checks.push(check("Revit connection", "fail", `No answer from the add-in (${h.error}).`,
      "Open Revit 2025 and a model. If it's open: ACE tab > MCP Status > Start/Restart. If there's no ACE tab: re-run install.ps1 and click 'Always Load' when Revit asks."));
  } else {
    checks.push(check("Revit connection", "ok", `Add-in listening (Revit ${h.revitVersion})`));
    try {
      const ping = await callRevit("ping", {}, 20);
      checks.push(check("Revit session", "ok", `${ping.revit}; add-in ${ping.addinVersion ?? "(old)"}; active document: ${ping.activeDocument ?? "none"}`));
      if (!ping.addinVersion) {
        checks.push(check("Version match", "warn", `Add-in is older than MCP server ${VERSION}.`, "Close Revit, run install.ps1, restart Revit."));
      } else if (!ping.addinVersion.startsWith(VERSION.split(".").slice(0, 2).join("."))) {
        checks.push(check("Version match", "warn", `Add-in ${ping.addinVersion} vs MCP server ${VERSION}.`, "Close Revit, run install.ps1, restart Revit."));
      }
      if (!ping.activeDocument) checks.push(check("Open model", "warn", "No model is open in Revit.", "Open a model before asking Claude to work on it."));
      if (deep) {
        const c = await callRevit("execute_code", { code: "return 6 * 7;", mode: "readonly", compile_only: true }, 60);
        checks.push(c.success
          ? check("C# compiler", "ok", "Scripts compile inside Revit.")
          : check("C# compiler", "fail", JSON.stringify(c.errors || c.error), "Close Revit, re-run install.ps1, restart Revit."));
      }
    } catch (err) {
      checks.push(check("Revit session", "fail", err.message,
        err.message.includes("token") ? "Restart Revit, or run doctor.ps1 -Fix." : "Revit may be busy: close dialogs, press Esc, then retry."));
    }
  }

  const team = teamScriptsDir();
  if (team) checks.push(fs.existsSync(team)
    ? check("Team script folder", "ok", team)
    : check("Team script folder", "warn", `${team} is not reachable`, "Connect to the network share / sync the folder, or fix teamScriptsDir in config.json."));
  const scripts = listScripts();
  const bySource = scripts.reduce((m, s) => ({ ...m, [s.source]: (m[s.source] || 0) + 1 }), {});
  checks.push(check("Script library", "ok", Object.entries(bySource).map(([k, v]) => `${v} ${k}`).join(", ") || "empty"));

  const recent = readCalls(7);
  const failed = recent.filter((c) => !c.ok);
  checks.push(check("Last 7 days", failed.length > recent.length * 0.3 && failed.length > 5 ? "warn" : "info",
    `${recent.length} tool calls, ${failed.length} failed`, failed.length > 5 ? "Run report_issue (or report.cmd) to get recommendations." : null));
  return checks;
}

export function claudeDesktopConfigs() {
  if (!isWindows) return [];
  const files = [path.join(APPDATA, "Claude", "claude_desktop_config.json")];
  const packages = path.join(process.env.LOCALAPPDATA || "", "Packages");
  try {
    for (const d of fs.readdirSync(packages).filter((d) => d.startsWith("Claude_"))) {
      files.push(path.join(packages, d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
    }
  } catch {
    /* not an MSIX install */
  }
  return files.filter((f) => fs.existsSync(f));
}

// ---------------------------------------------------------------------------------------------
// Usage analysis -> recommendations
// ---------------------------------------------------------------------------------------------

export function analyze(calls) {
  const recs = [];
  const add = (severity, title, detail, forMaintainer) => recs.push({ severity, title, detail, forMaintainer });
  const failed = calls.filter((c) => !c.ok);
  const matching = (re) => failed.filter((c) => re.test(c.error || c.runtimeError || ""));

  const unreachable = matching(/not reachable|ECONNREFUSED|Cannot find .*config/i);
  if (unreachable.length) add("high", `Revit was unreachable ${unreachable.length}×`,
    "Revit was closed, the add-in wasn't loaded, or the connection was stopped. Keep Revit open with a model; if the ACE tab is missing, re-run install.ps1 and click 'Always Load'.",
    "Consider auto-starting the bridge on document open and showing a status indicator.");

  const timeouts = matching(/did not run .* within|timed? ?out|AbortError|TimeoutError/i);
  if (timeouts.length) add("high", `Revit was busy / timed out ${timeouts.length}×`,
    "Revit only runs commands when idle: close open dialogs, press Esc to end active commands, wait for syncs and loads. Long scripts: raise timeout_seconds.",
    "Check whether long scripts need progress reporting or chunking.");

  const token = matching(/token/i);
  if (token.length) add("high", `Token mismatch ${token.length}×`, "Restart Revit, or run doctor.ps1 -Fix.", null);

  const compile = failed.filter((c) => c.stage === "compile");
  if (compile.length) {
    const codes = count(compile.flatMap((c) => c.compileErrors || []));
    const members = count(compile.flatMap((c) => (c.compileMessages || []).map((m) => (m.match(/'([^']+)' does not contain a definition for '([^']+)'/) || []).slice(1, 3).join(".")).filter(Boolean)));
    add("medium", `${compile.length} scripts failed to compile`,
      `Most common errors: ${top(codes)}. Claude corrects these automatically; frequent API mistakes are worth a guide entry.` +
        (Object.keys(members).length ? ` API members Claude got wrong: ${top(members)}.` : ""),
      "Add the misused members to guides/*.md with correct Revit 2025 usage, and encourage revit_api_lookup in instructions.md.");
  }

  const runtime = failed.filter((c) => c.stage === "runtime");
  if (runtime.length) {
    const kinds = count(runtime.map((c) => (c.runtimeError || "").split(":")[0]));
    add("medium", `${runtime.length} scripts failed while running (all rolled back)`, `Exception types: ${top(kinds)}.`,
      "Review the failing code in the appendix; recurring exceptions may need a helper in ScriptContext or a guide note.");
  }

  const revitErr = calls.flatMap((c) => c.revitErrors || []);
  if (revitErr.length) add("medium", `Revit rejected changes ${revitErr.length}×`, `Examples: ${[...new Set(revitErr)].slice(0, 3).join(" | ")}`,
    "Common model constraints (overlaps, joins, enclosures) could get pre-checks in scripts.");

  const unpreviewed = matching(/not been previewed/);
  if (unpreviewed.length > 2) add("low", `Claude tried to apply unpreviewed changes ${unpreviewed.length}×`,
    "The safety gate blocked them (as designed). Nothing unsafe happened.", "If this is frequent, strengthen step 3-5 wording in instructions.md.");

  const risky = matching(/Blocked for safety: this code/);
  if (risky.length) add("low", `Risky code was blocked ${risky.length}×`, "Claude must ask before touching files, other programs, or saving/closing models.",
    "If a blocked operation is routinely needed (e.g. exporting CSV), consider a dedicated safe tool for it.");

  const slow = calls.filter((c) => c.scriptMs > 20000);
  if (slow.length) add("medium", `${slow.length} scripts took over 20 s`, "Large models: ask Claude to optimise (revit_guide performance).",
    "Inspect slow scripts' collectors; consider a native command for the common heavy query.");

  const repeated = Object.entries(count(calls.filter((c) => c.ok && c.codeHash && c.tool === "execute_revit_code" && !c.dryRun).map((c) => c.codeHash))).filter(([, n]) => n >= 3);
  if (repeated.length) add("low", `${repeated.length} custom scripts were run 3+ times`, "Ask Claude to save them with save_script (scope \"team\" to share).",
    "Promote popular scripts into the built-in library.");

  const savedFails = count(failed.filter((c) => c.tool === "run_saved_script" && c.script).map((c) => c.script));
  if (Object.keys(savedFails).length) add("high", "Saved scripts failing", `Failures per script: ${top(savedFails)}.`, "Fix or update these scripts (see appendix for errors).");

  if (!recs.length) add("info", "No problems detected in the recent activity", "Everything that ran recently succeeded or failed safely.", null);
  return recs;
}

function count(items) {
  return items.reduce((m, k) => ((m[k] = (m[k] || 0) + 1), m), {});
}
function top(map, n = 5) {
  return Object.entries(map).sort((a, b) => b[1] - a[1]).slice(0, n).map(([k, v]) => `${k} (${v})`).join(", ");
}

// ---------------------------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------------------------

const icon = { ok: "✅", warn: "⚠️", fail: "❌", info: "ℹ️" };

function tail(file, lines) {
  try {
    return fs.readFileSync(file, "utf8").trimEnd().split(/\r?\n/).slice(-lines).join("\n");
  } catch {
    return "(not found)";
  }
}

export async function buildReport({ kind = "issue", note = "", expected = "", steps = "", extra = "", days = 14 } = {}) {
  const checks = await runChecks();
  const calls = readCalls(days);
  const recs = analyze(calls);
  const failures = calls.filter((c) => !c.ok).slice(-10);
  const byTool = Object.entries(calls.reduce((m, c) => {
    const s = (m[c.tool] ||= { n: 0, fail: 0, ms: 0 });
    s.n++; if (!c.ok) s.fail++; s.ms += c.ms || 0;
    return m;
  }, {})).sort((a, b) => b[1].n - a[1].n);

  const now = new Date();
  const title = { issue: "Issue report", improvement: "Improvement report", health: "Health report" }[kind] || "Report";
  const md = [];
  md.push(`# ACE Revit MCP - ${title}`, "");
  md.push(`| | |`, `|---|---|`);
  md.push(`| Date | ${now.toISOString()} |`);
  md.push(`| User / machine | ${os.userInfo().username} @ ${os.hostname()} |`);
  md.push(`| Team | ${readConfig()?.teamName || "-"} |`);
  md.push(`| OS | ${os.type()} ${os.release()} (${os.arch()}) |`);
  md.push(`| Node / MCP server | ${process.versions.node} / ${VERSION} |`);
  const session = checks.find((c) => c.name === "Revit session");
  md.push(`| Revit | ${session?.detail || "not reachable"} |`, "");

  if (kind !== "health") {
    md.push(`## ${kind === "improvement" ? "Suggested improvement" : "What happened"}`, "", note || "_(no description given)_", "");
    if (expected) md.push("**Expected:** " + expected, "");
    if (steps) md.push("**Steps / request that led to it:**", "", steps, "");
  }

  md.push("## Health checks", "", "| | Check | Result | Fix |", "|---|---|---|---|");
  for (const c of checks) md.push(`| ${icon[c.status]} | ${c.name} | ${String(c.detail).replace(/\|/g, "\\|")} | ${c.fix || ""} |`);
  md.push("");

  md.push("## Recommendations", "");
  for (const r of recs) {
    md.push(`- **[${r.severity}] ${r.title}.** ${r.detail}`);
    if (r.forMaintainer) md.push(`  - _Maintainer:_ ${r.forMaintainer}`);
  }
  for (const c of checks.filter((c) => c.fix && c.status !== "ok")) md.push(`- **[setup] ${c.name}:** ${c.fix}`);
  md.push("");

  if (extra) md.push(extra.trim(), "");

  md.push(`## Usage (last ${days} days)`, "", "| Tool | Calls | Failed | Avg ms |", "|---|---|---|---|");
  for (const [tool, s] of byTool) md.push(`| ${tool} | ${s.n} | ${s.fail} | ${Math.round(s.ms / s.n)} |`);
  if (!byTool.length) md.push("| (no calls recorded) | | | |");
  md.push("");

  md.push("## Recent activity", "", "| Time | Tool | OK | ms | Detail |", "|---|---|---|---|---|");
  for (const c of calls.slice(-25)) {
    const detail = (c.error || c.runtimeError || c.compileMessages?.[0] || c.script || (c.changes ? `changes ${c.changes.added}/${c.changes.modified}/${c.changes.deleted}` : "") || (c.dryRun ? "preview" : "")).toString().replace(/\|/g, "\\|").replace(/\n/g, " ").slice(0, 140);
    md.push(`| ${c.t.slice(5, 19).replace("T", " ")} | ${c.tool} | ${c.ok ? "✅" : "❌"} | ${c.ms ?? ""} | ${detail} |`);
  }
  md.push("");

  md.push("## Revit add-in log (last 60 lines)", "", "```", tail(ADDIN_LOG, 60), "```", "");

  md.push("## Appendix for maintainers: recent failures", "");
  if (!failures.length) md.push("_None._");
  for (const f of failures) {
    md.push(`### ${f.t} - ${f.tool}${f.stage ? ` (${f.stage})` : ""}`, "");
    md.push("```json", JSON.stringify({ ...f, code: undefined }, null, 1), "```");
    if (f.code) md.push("", "```csharp", f.code, "```");
    md.push("");
  }
  md.push("---", "_Generated by ACE Revit MCP. Send this file to the ACE tool maintainers, or give it to Claude Code with: \"Work on this ACE Revit MCP report and improve the tool.\"_");

  const content = md.join("\n");
  fs.mkdirSync(REPORTS_DIR, { recursive: true });
  const stamp = now.toISOString().replace(/[:T]/g, "-").slice(0, 19);
  const file = path.join(REPORTS_DIR, `${kind}-${os.hostname()}-${stamp}.md`);
  fs.writeFileSync(file, content);
  let shared = null;
  const teamDir = teamReportsDir();
  if (teamDir) {
    try {
      fs.mkdirSync(teamDir, { recursive: true });
      shared = path.join(teamDir, path.basename(file));
      fs.copyFileSync(file, shared);
    } catch {
      shared = null;
    }
  }
  return { file, shared, content, checks, recommendations: recs };
}

// ---------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [, , cmd, ...rest] = process.argv;
  const opt = (name) => {
    const i = rest.indexOf(`--${name}`);
    return i >= 0 ? rest[i + 1] : undefined;
  };
  if (cmd === "check") {
    const checks = await runChecks();
    if (rest.includes("--json")) console.log(JSON.stringify(checks, null, 1));
    else for (const c of checks) console.log(`${c.status.toUpperCase().padEnd(4)}  ${c.name}: ${c.detail}${c.fix ? `\n      -> ${c.fix}` : ""}`);
    process.exit(checks.some((c) => c.status === "fail") ? 1 : 0);
  } else if (cmd === "report") {
    const extraFile = opt("extra");
    const r = await buildReport({
      kind: opt("kind") || "issue",
      note: opt("note") || "",
      extra: extraFile && fs.existsSync(extraFile) ? fs.readFileSync(extraFile, "utf8") : "",
    });
    console.log(r.file);
    if (r.shared) console.log(r.shared);
  } else {
    console.log("usage: node lib/diagnostics.js check [--json] | report [--kind issue|improvement|health] [--note text] [--extra file.md]");
  }
}
