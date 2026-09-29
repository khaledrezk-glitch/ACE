// ACE benchmark runner: node tools/bench/run.mjs [filter]
// Runs every case in cases.json against the open Revit session (through the ACE bridge), checks the
// expected results and timing, prints a table and appends the result to logs/bench-history.jsonl.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { callRevit, LOG_DIR, VERSION } from "../../mcp-server/lib/core.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const scriptsDir = path.join(here, "..", "..", "mcp-server", "scripts");
const { cases } = JSON.parse(fs.readFileSync(path.join(here, "cases.json"), "utf8"));
const filter = process.argv[2]?.toLowerCase();

// "rooms[type=Office Unit].count", "[0].footprintMm", "result.workstations"
function get(obj, p) {
  let cur = obj;
  for (const part of p.match(/[^.[\]]+|\[[^\]]*\]/g) || []) {
    if (cur == null) return undefined;
    const sel = part.startsWith("[") ? part.slice(1, -1) : part;
    if (/^\d+$/.test(sel)) cur = cur[Number(sel)];
    else if (sel.includes("=")) { const [k, v] = sel.split("="); cur = Array.isArray(cur) ? cur.find((x) => String(x?.[k]) === v) : undefined; }
    else cur = cur[sel];
  }
  return cur;
}

function check(result, e) {
  const v = get(result, e.path);
  switch (e.op) {
    case "exists": return v != null;
    case "==": return v === e.value;
    case ">=": return typeof v === "number" && v >= e.value;
    case "length>=": return (Array.isArray(v) ? v.length : v && typeof v === "object" ? Object.keys(v).length : -1) >= e.value;
    case "contains": return String(v ?? "").includes(e.value);
    case "between": return typeof v === "number" && v >= e.value[0] && v <= e.value[1];
    default: return false;
  }
}

const ping = await callRevit("ping", {}, 20);
console.log(`ACE benchmark ${VERSION} · ${ping.revit} · model: ${ping.activeDocument}\n`);
const rows = [];
for (const c of cases) {
  if (filter && !c.name.toLowerCase().includes(filter)) continue;
  if (c.model && c.model !== ping.activeDocument) { rows.push({ name: c.name, status: "skip", note: `needs ${c.model}` }); continue; }
  const started = Date.now();
  let result, error;
  try {
    if (c.script) {
      const code = fs.readFileSync(path.join(scriptsDir, `${c.script}.cs`), "utf8");
      const mode = (code.match(/@mode:\s*(\w+)/) || [])[1] || "auto";
      result = await callRevit("execute_code", { code, mode, dry_run: !!c.dryRun, preview_image: !!c.previewImage, inputs: c.inputs || {}, transaction_name: `ACE bench: ${c.name}` }, 600);
      if (mode !== "readonly" && !c.dryRun) throw new Error("benchmark cases must not change the model: set dryRun");
    } else result = await callRevit(c.command, c.args || {}, 600);
  } catch (err) { error = err.message; }
  const ms = Date.now() - started;
  const failed = error ? [error] : (c.expect || []).filter((e) => !check(result, e)).map((e) => `${e.path} ${e.op} ${JSON.stringify(e.value ?? "")} (got ${JSON.stringify(get(result, e.path))?.slice(0, 80)})`);
  if (!error && c.maxMs && ms > c.maxMs) failed.push(`too slow: ${ms} ms > ${c.maxMs} ms`);
  rows.push({ name: c.name, status: failed.length ? "FAIL" : "pass", ms, note: failed.join("; ") });
}
for (const r of rows) console.log(`${r.status.padEnd(5)} ${String(r.ms ?? "").padStart(7)} ms  ${r.name}${r.note ? `\n                 ${r.note}` : ""}`);
const ran = rows.filter((r) => r.status !== "skip");
const passed = ran.filter((r) => r.status === "pass").length;
console.log(`\n${passed}/${ran.length} passed${rows.length > ran.length ? `, ${rows.length - ran.length} skipped` : ""}`);
try {
  fs.mkdirSync(LOG_DIR, { recursive: true });
  fs.appendFileSync(path.join(LOG_DIR, "bench-history.jsonl"), JSON.stringify({ t: new Date().toISOString(), version: VERSION, model: ping.activeDocument, passed, ran: ran.length, rows }) + "\n");
} catch { /* best effort */ }
process.exit(passed === ran.length ? 0 : 1);
