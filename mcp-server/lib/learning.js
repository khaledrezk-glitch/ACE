// Learning report: turns the local call log and the lessons into measurable improvement.
//   - first-time-right rate: of the tasks where Claude ran code, how often the first run worked
//   - the API mistakes that keep happening (compile error codes and messages) -> guide / helper candidates
//   - code that keeps being written -> saved-script (recipe) candidates
//   - slow calls, failing saved scripts, lessons learned
// Each report appends a line to logs/learning-history.jsonl, so the trend shows whether ACE is getting better.
// CLI (maintainers): node lib/learning.js [days]
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { LOG_DIR, REPORTS_DIR, VERSION, localDate, teamReportsDir } from "./core.js";
import { readCalls } from "./telemetry.js";
import { radarMarkdown } from "./radar.js";
import { allLessons, lessonStats } from "./lessons.js";

const HISTORY = () => path.join(LOG_DIR, "learning-history.jsonl");
const CODE_TOOLS = new Set(["execute_revit_code", "run_saved_script"]);

const COMPILE_HINTS = {
  CS1061: "a member that does not exist on that type: check it with revit_api_lookup first (Revit 2025 renamed or removed several)",
  CS0103: "a name that is not in scope (a helper or variable that does not exist): see the script shape in the instructions",
  CS0246: "a type that is not found: missing namespace or wrong class name",
  CS0029: "a type mismatch (often ElementId vs long, or double vs string)",
  CS1503: "a wrong argument type in a call",
  CS0618: "an obsolete API member (e.g. ElementId.IntegerValue: use .Value)",
  CS0266: "a missing cast",
  CS0019: "an operator used on the wrong types",
};

/** Splits code runs into tasks: runs less than 15 minutes apart belong to the same task. */
function tasks(calls) {
  const runs = calls.filter((c) => CODE_TOOLS.has(c.tool)).sort((a, b) => a.t.localeCompare(b.t));
  const out = [];
  let cur = null;
  for (const r of runs) {
    const t = Date.parse(r.t);
    if (!cur || t - cur.last > 15 * 60_000) { cur = { runs: [], last: t }; out.push(cur); }
    cur.runs.push(r);
    cur.last = t;
  }
  return out;
}

export function analyse(days = 30) {
  const calls = readCalls(days);
  const byTool = {};
  for (const c of calls) {
    const b = (byTool[c.tool] ||= { calls: 0, ok: 0, ms: 0 });
    b.calls++; if (c.ok) b.ok++; b.ms += c.ms || 0;
  }
  const t = tasks(calls);
  const firstRight = t.filter((x) => x.runs[0].ok).length;
  const retries = t.map((x) => x.runs.length);

  const compile = {};
  for (const c of calls) for (const [i, code] of (c.compileErrors || []).entries()) {
    const e = (compile[code] ||= { count: 0, samples: [] });
    e.count++;
    const msg = c.compileMessages?.[i];
    if (msg && e.samples.length < 3 && !e.samples.includes(msg)) e.samples.push(msg);
  }
  const runtime = {};
  for (const c of calls) if (c.runtimeError) {
    const key = c.runtimeError.replace(/\d+/g, "#").slice(0, 120);
    runtime[key] = (runtime[key] || 0) + 1;
  }
  const repeated = {};
  for (const c of calls) if (c.tool === "execute_revit_code" && c.ok && c.codeHead && c.mode !== "readonly") {
    const key = c.codeHead.slice(0, 90);
    (repeated[key] ||= { count: 0, head: c.codeHead }).count++;
  }
  const scripts = {};
  for (const c of calls) if (c.script) {
    const s = (scripts[c.script] ||= { runs: 0, failed: 0 });
    s.runs++; if (!c.ok) s.failed++;
  }
  const slow = calls.filter((c) => (c.ms || 0) > 30_000).map((c) => ({ tool: c.tool, s: Math.round(c.ms / 1000), when: c.t.slice(0, 16), script: c.script }));
  const since = Date.now() - days * 86400_000;
  const lessons = allLessons();
  const newLessons = lessons.filter((l) => l.source !== "built-in" && Date.parse(l.time) >= since);

  return {
    days, version: VERSION, generated: new Date().toISOString(),
    calls: calls.length, byTool,
    codeTasks: t.length,
    firstTimeRightPct: t.length ? Math.round((100 * firstRight) / t.length) : null,
    avgRunsPerTask: t.length ? Math.round((10 * retries.reduce((a, b) => a + b, 0)) / t.length) / 10 : null,
    compile: Object.entries(compile).sort((a, b) => b[1].count - a[1].count),
    runtime: Object.entries(runtime).sort((a, b) => b[1] - a[1]).slice(0, 8),
    recipeCandidates: Object.values(repeated).filter((r) => r.count >= 3).sort((a, b) => b.count - a.count).slice(0, 8),
    scripts: Object.entries(scripts).sort((a, b) => b[1].runs - a[1].runs),
    slow: slow.slice(-10),
    lessons: { ...lessonStats(), newInPeriod: newLessons.length, newest: newLessons.slice(0, 10).map((l) => `[${l.kind}/${l.scope}] ${l.lesson}`) },
  };
}

function recommendations(a) {
  const r = [];
  if (a.firstTimeRightPct != null && a.firstTimeRightPct < 70)
    r.push(`Only ${a.firstTimeRightPct}% of code tasks worked on the first run. Look at the compile and runtime errors below and turn the common ones into guide text, ScriptContext helpers or lessons.`);
  for (const [code, e] of a.compile.slice(0, 4))
    if (e.count >= 2) r.push(`${code} happened ${e.count} times: ${COMPILE_HINTS[code] || "see the samples"}. Add the correct usage to guides/*.md (or a built-in lesson) so it stops recurring.`);
  for (const c of a.recipeCandidates.slice(0, 3))
    r.push(`Similar change code ran ${c.count} times ("${c.head.slice(0, 70)}..."): make it a tested saved script (team scope) or a built-in recipe.`);
  for (const [name, s] of a.scripts) if (s.failed > 0 && s.failed / s.runs > 0.2) r.push(`Saved script '${name}' failed ${s.failed} of ${s.runs} runs: fix and re-test it.`);
  if (a.slow.length >= 3) r.push(`${a.slow.length} calls took over 30 s: check the slowest scripts for per-element API calls inside loops (see the performance guide).`);
  if (a.lessons.newInPeriod === 0 && a.codeTasks > 5) r.push("No new lessons were saved in this period although work was done: remind Claude (instructions) or the team to save what they learn.");
  if (a.lessons.bySource?.team) r.push(`Review the ${a.lessons.bySource.team} team lessons and promote the good ones to mcp-server/lessons/built-in.jsonl for the next release.`);
  if (!r.length) r.push("Nothing stands out. Keep saving lessons and run the benchmark before each release.");
  return r;
}

function trend() {
  try {
    // One damaged line (e.g. an interrupted write) must not hide the whole history.
    return fs.readFileSync(HISTORY(), "utf8").split("\n").filter(Boolean)
      .map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean).slice(-12);
  } catch { return []; }
}

export function learningReport({ days = 30, share_with_team = false } = {}) {
  const a = analyse(days);
  const hist = trend();
  const md = [];
  md.push(`# ACE learning report`, ``, `${localDate()} · last ${days} days · ACE Revit MCP ${a.version}`, ``);
  md.push(`## How well it is working`, ``,
    `| Measure | Value |`, `|---|---|`,
    `| Tool calls | ${a.calls} |`,
    `| Code tasks (Claude ran Revit code) | ${a.codeTasks} |`,
    `| First-time right | ${a.firstTimeRightPct ?? "-"}% |`,
    `| Runs per task (average) | ${a.avgRunsPerTask ?? "-"} |`,
    `| Lessons known (built-in / team / personal) | ${a.lessons.bySource?.["built-in"] || 0} / ${a.lessons.bySource?.team || 0} / ${a.lessons.bySource?.personal || 0} |`,
    `| New lessons in this period | ${a.lessons.newInPeriod} |`, ``);
  if (hist.length) {
    md.push(`### Trend`, ``, `| Date | Version | First-time right | Runs per task | Lessons |`, `|---|---|---|---|---|`);
    for (const h of hist) md.push(`| ${h.date} | ${h.version} | ${h.firstTimeRightPct ?? "-"}% | ${h.avgRunsPerTask ?? "-"} | ${h.lessons ?? "-"} |`);
    md.push(``);
  }
  md.push(`## Recommendations`, ``, ...recommendations(a).map((x) => `- ${x}`), ``);
  md.push(...radarMarkdown(days));
  if (a.compile.length) {
    md.push(`## Repeated API mistakes (compile errors)`, ``);
    for (const [code, e] of a.compile.slice(0, 8)) md.push(`- **${code}** x${e.count}: ${COMPILE_HINTS[code] || ""}${e.samples.map((s) => `\n  - \`${s.replace(/`/g, "'")}\``).join("")}`);
    md.push(``);
  }
  if (a.runtime.length) { md.push(`## Runtime errors`, ``, ...a.runtime.map(([m, n]) => `- x${n}: ${m}`), ``); }
  if (a.recipeCandidates.length) { md.push(`## Recipe candidates (change code written again and again)`, ``, ...a.recipeCandidates.map((c) => `- x${c.count}: \`${c.head.replace(/`/g, "'")}\``), ``); }
  if (a.scripts.length) { md.push(`## Saved scripts used`, ``, `| Script | Runs | Failed |`, `|---|---|---|`, ...a.scripts.map(([n, s]) => `| ${n} | ${s.runs} | ${s.failed} |`), ``); }
  if (a.slow.length) { md.push(`## Slow calls (> 30 s)`, ``, ...a.slow.map((s) => `- ${s.when}: ${s.tool}${s.script ? ` (${s.script})` : ""} ${s.s} s`), ``); }
  if (a.lessons.newest.length) { md.push(`## Lessons learned in this period`, ``, ...a.lessons.newest.map((l) => `- ${l}`), ``); }
  md.push(`## Tools`, ``, `| Tool | Calls | Success | Avg time |`, `|---|---|---|---|`,
    ...Object.entries(a.byTool).sort((x, y) => y[1].calls - x[1].calls).map(([n, b]) => `| ${n} | ${b.calls} | ${Math.round((100 * b.ok) / b.calls)}% | ${(b.ms / b.calls / 1000).toFixed(1)} s |`), ``);
  md.push(`_Maintainers: work through the recommendations, add or extend a benchmark case (tools/bench), then release. This report stays on this PC unless shared._`);
  const text = md.join("\n");

  fs.mkdirSync(REPORTS_DIR, { recursive: true });
  const file = path.join(REPORTS_DIR, `learning-${localDate()}.md`);
  fs.writeFileSync(file, text);
  let shared = null;
  const team = share_with_team ? teamReportsDir() : null;
  if (team) {
    try {
      fs.mkdirSync(team, { recursive: true });
      shared = path.join(team, `learning-${(process.env.USERNAME || process.env.USER || "user").replace(/\W/g, "")}-${localDate()}.md`);
      fs.copyFileSync(file, shared);
    } catch (e) { shared = `could not copy: ${e.message}`; }
  }
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    fs.appendFileSync(HISTORY(), JSON.stringify({ date: a.generated.slice(0, 10), version: a.version, firstTimeRightPct: a.firstTimeRightPct, avgRunsPerTask: a.avgRunsPerTask, lessons: a.lessons.total, codeTasks: a.codeTasks }) + "\n");
  } catch { /* best effort */ }
  return { file, shared, text };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const r = learningReport({ days: Number(process.argv[2]) || 30 });
  console.log(r.text);
  console.log(`\nSaved: ${r.file}`);
}
