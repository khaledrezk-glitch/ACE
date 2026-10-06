// The development radar: a live, local monitor of how people use ACE, turned into development candidates.
// It costs no tokens and runs no process: it reads the local call log only when a learning report is made
// (report_issue kind "learning") or by the maintainers' tools. Nothing leaves this PC unless the report is shared.
import { readCalls } from "./telemetry.js";

const TASK_GAP_MS = 15 * 60_000;      // calls more than 15 minutes apart belong to different tasks
const CHARS_PER_TOKEN = 4;
const PER_CALL_OVERHEAD_TOKENS = 1_200; // Claude re-reads the conversation and the tool list on every call (rough)

/** Splits the call log into tasks: runs of calls without a long pause. */
export function tasks(calls) {
  const list = [];
  let cur = null;
  for (const c of [...calls].sort((a, b) => Date.parse(a.t) - Date.parse(b.t))) {
    const t = Date.parse(c.t);
    if (!cur || t - cur.end > TASK_GAP_MS) list.push((cur = { start: t, end: t, calls: [] }));
    cur.calls.push(c);
    cur.end = t + (c.ms || 0);
  }
  return list.map((k) => {
    const changes = k.calls.filter((c) => c.dryRun !== undefined && !c.checkOnly);
    const previews = changes.filter((c) => c.dryRun).length;
    const applies = changes.filter((c) => c.dryRun === false).length;
    const failed = k.calls.filter((c) => !c.ok).length;
    const replyTokens = Math.round(k.calls.reduce((n, c) => n + (c.chars || 0), 0) / CHARS_PER_TOKEN);
    const tools = [...new Set(k.calls.map((c) => c.tool))];
    return {
      when: new Date(k.start).toISOString().slice(0, 16).replace("T", " "),
      minutes: Math.round((k.end - k.start) / 60_000),
      calls: k.calls.length, previews, applies, failed, tools,
      tokens: replyTokens + k.calls.length * PER_CALL_OVERHEAD_TOKENS,
      firstTool: k.calls[0]?.tool,
    };
  });
}

/**
 * Development candidates from the tasks: each with why it matters and what would help. Thresholds are deliberately
 * simple; the maintainers' idea agents read this list rather than the raw log.
 */
export function radar(days = 30) {
  const calls = readCalls(days);
  const list = tasks(calls);
  const out = [];
  const add = (kind, title, evidence, suggestion, weight) => out.push({ kind, title, evidence, suggestion, weight });

  // Long tasks: many interactions for one job.
  const long = list.filter((t) => t.calls >= 15);
  if (long.length) {
    const top = [...long].sort((a, b) => b.calls - a.calls).slice(0, 3);
    add("fewer steps", "Tasks that need many calls",
      `${long.length} task(s) with 15+ calls; e.g. ${top.map((t) => `${t.when}: ${t.calls} calls, ${t.previews} previews, ~${Math.round(t.tokens / 1000)}k tokens (${t.tools.slice(0, 5).join(", ")})`).join("; ")}`,
      "A native command or built-in script for the job, or one script with plan/step instead of many small calls.", long.length * 3);
  }
  // Several previews before one apply: the user's request was not clear enough, or the tool needed retries.
  const retries = list.filter((t) => t.previews >= 3 && t.applies <= 1);
  if (retries.length)
    add("fewer steps", "Several previews for one change",
      `${retries.length} task(s) with 3+ previews and at most one apply`,
      "Check why previews were repeated (failed compile, wrong guess): better guide text, a helper, or ask one question first.", retries.length * 2);
  // Failure-heavy tasks.
  const failing = list.filter((t) => t.failed >= 3);
  if (failing.length)
    add("reliability", "Tasks with repeated failures", `${failing.length} task(s) with 3+ failed calls`,
      "Read the failing calls in the learning report (compile errors, runtime errors) and fix guides or scripts.", failing.length * 2);
  // Expensive tasks.
  const costly = list.filter((t) => t.tokens >= 60_000);
  if (costly.length)
    add("tokens", "Expensive tasks", `${costly.length} task(s) above ~60k tokens; the largest ~${Math.round(Math.max(...costly.map((t) => t.tokens)) / 1000)}k`,
      "Smaller replies (limits, summaries) or fewer calls for these jobs.", costly.length);
  // Big replies by tool.
  const byTool = {};
  for (const c of calls) { const s = (byTool[c.tool] ||= { n: 0, chars: 0 }); s.n++; s.chars += c.chars || 0; }
  for (const [tool, s] of Object.entries(byTool)) {
    const avgTokens = Math.round(s.chars / s.n / CHARS_PER_TOKEN);
    if (s.n >= 5 && avgTokens >= 4_000)
      add("tokens", `Large replies from ${tool}`, `${s.n} calls, about ${avgTokens} tokens each`, "A summary mode, smaller defaults or paging for this tool.", 1 + Math.floor(avgTokens / 4000));
  }
  // Code written again and again.
  const heads = {};
  for (const c of calls.filter((c) => c.codeHead && c.tool === "execute_revit_code")) (heads[c.codeHead.slice(0, 80)] ||= []).push(c);
  for (const [head, cs] of Object.entries(heads))
    if (cs.length >= 3)
      add("new script", "Code written again and again", `x${cs.length}: ${head}`, "Turn it into a tested built-in script or a native command.", cs.length);

  const summary = {
    days, tasks: list.length, calls: calls.length,
    avgCallsPerTask: list.length ? +(calls.length / list.length).toFixed(1) : 0,
    avgTokensPerTask: list.length ? Math.round(list.reduce((n, t) => n + t.tokens, 0) / list.length) : 0,
    previews: list.reduce((n, t) => n + t.previews, 0), applies: list.reduce((n, t) => n + t.applies, 0),
  };
  return { summary, candidates: out.sort((a, b) => b.weight - a.weight), longestTasks: [...list].sort((a, b) => b.calls - a.calls).slice(0, 5) };
}

/** The radar as Markdown, for the learning report. */
export function radarMarkdown(days = 30) {
  const r = radar(days);
  const s = r.summary;
  const md = [`## Development radar`, ``,
    `${s.tasks} tasks, ${s.calls} calls (${s.avgCallsPerTask} per task), about ${Math.round(s.avgTokensPerTask / 1000)}k tokens per task (estimate), ${s.previews} previews, ${s.applies} applies.`, ``];
  if (!r.candidates.length) md.push(`Nothing stands out yet.`, ``);
  else {
    md.push(`| Kind | Candidate | Evidence | What would help |`, `|---|---|---|---|`);
    for (const c of r.candidates.slice(0, 12)) md.push(`| ${c.kind} | ${c.title} | ${c.evidence.replace(/\|/g, "/")} | ${c.suggestion} |`);
    md.push(``);
  }
  if (r.longestTasks.length) {
    md.push(`### Longest tasks`, ``, `| When | Calls | Previews | Applies | Failed | Minutes | ~Tokens | Tools |`, `|---|---|---|---|---|---|---|---|`);
    for (const t of r.longestTasks) md.push(`| ${t.when} | ${t.calls} | ${t.previews} | ${t.applies} | ${t.failed} | ${t.minutes} | ${Math.round(t.tokens / 1000)}k | ${t.tools.slice(0, 6).join(", ")} |`);
    md.push(``);
  }
  return md;
}
