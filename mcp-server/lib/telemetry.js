// Local-only call log (never sent anywhere). Powers check_setup, report_issue and improvement reports.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { LOG_DIR } from "./core.js";

const CALLS = () => path.join(LOG_DIR, "mcp-calls.jsonl");
const MAX_BYTES = 4 * 1024 * 1024;

export const codeHash = (code) => createHash("sha1").update(String(code ?? "")).digest("hex").slice(0, 12);

/** Pulls the interesting, compact facts out of a tool result for later analysis. */
export function summarize(tool, args, result, ms, error) {
  const entry = { t: new Date().toISOString(), tool, ms, ok: !error && !result?.isError };
  if (error) entry.error = String(error.message || error).slice(0, 500);
  const firstText = result?.content?.find((c) => c.type === "text")?.text;
  if (!entry.ok && !entry.error && firstText) entry.error = firstText.slice(0, 500);

  if (tool === "execute_revit_code" || tool === "run_saved_script") {
    entry.mode = args?.mode;
    entry.dryRun = !!args?.dry_run;
    if (args?.code) entry.codeHash = codeHash(args.code);
    if (args?.name) entry.script = args.name;
    try {
      const r = JSON.parse(firstText);
      if (r.stage) entry.stage = r.stage;
      if (r.scriptMs != null) entry.scriptMs = r.scriptMs;
      if (r.errors) entry.compileErrors = r.errors.map((e) => (e.match(/CS\d{4}/) || ["?"])[0]);
      if (r.errors) entry.compileMessages = r.errors.slice(0, 3).map((e) => e.slice(0, 200));
      if (r.error) entry.runtimeError = r.error.slice(0, 300);
      if (r.revitErrors) entry.revitErrors = r.revitErrors.slice(0, 3);
      if (r.wouldChange || r.changed) entry.changes = r.wouldChange || r.changed;
      delete entry.changes?.note;
    } catch {
      /* not JSON */
    }
    // Keep the failing code so maintainers can reproduce it.
    if (!entry.ok && args?.code) entry.code = String(args.code).slice(0, 6000);
  }
  return entry;
}

export function logCall(entry) {
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    const file = CALLS();
    if (fs.existsSync(file) && fs.statSync(file).size > MAX_BYTES) fs.renameSync(file, file + ".old");
    fs.appendFileSync(file, JSON.stringify(entry) + "\n");
  } catch {
    /* never fail a tool call because of logging */
  }
}

export function readCalls(days = 14) {
  const since = Date.now() - days * 86400_000;
  const out = [];
  for (const file of [CALLS() + ".old", CALLS()]) {
    if (!fs.existsSync(file)) continue;
    for (const line of fs.readFileSync(file, "utf8").split("\n")) {
      if (!line.trim()) continue;
      try {
        const e = JSON.parse(line);
        if (Date.parse(e.t) >= since) out.push(e);
      } catch {
        /* skip corrupt line */
      }
    }
  }
  return out;
}
