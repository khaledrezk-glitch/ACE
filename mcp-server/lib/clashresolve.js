// clash_resolve: the structural self-clash solver (ST vs ST). The add-in plans and applies only joins, switched join
// orders, steel end cutbacks and copes (never deletes, recreates or moves an element), guards every annotation that
// references a touched element, and compares the warnings. Pure functions here: the request, the project's rules from
// the vault (rules/clash-resolve.json), the reply text and the evidence; index.js calls Revit and writes the files.
import { blockLine, fileStem } from "./paramexec.js";
import { localDate } from "./core.js";
import { filteredText, materialArgs, materialScope } from "./interference.js";

export const DEFAULT_TEST = "ST vs ST";
export const ST_CATEGORIES = ["OST_StructuralColumns", "OST_StructuralFraming", "OST_Walls", "OST_Floors", "OST_StructuralFoundation", "OST_Stairs", "OST_Ramps"];
export const RULES_FILE = "clash-resolve.json";   // in the vault's rules folder
export const KINDS = ["join", "switch-join", "cutback", "cope"];
const DONE_FIELD = { join: "join", "switch-join": "switchJoin", cutback: "cutback", cope: "cope" };

// Words people write in the vault's order -> the Revit category the add-in reads. OST names pass unchanged.
const ROLE = {
  foundation: "OST_StructuralFoundation", foundations: "OST_StructuralFoundation",
  column: "OST_StructuralColumns", columns: "OST_StructuralColumns",
  wall: "OST_Walls", walls: "OST_Walls",
  beam: "OST_StructuralFraming", beams: "OST_StructuralFraming", framing: "OST_StructuralFraming",
  slab: "OST_Floors", slabs: "OST_Floors", floor: "OST_Floors", floors: "OST_Floors",
  stair: "OST_Stairs", stairs: "OST_Stairs", ramp: "OST_Ramps", ramps: "OST_Ramps",
};

/** The join order (strongest first) -> OST category names; an entry ACE cannot read is an error, never dropped. */
export function normaliseOrder(order, where = "order") {
  if (order == null) return undefined;
  if (!Array.isArray(order) || !order.length) throw new Error(`${where} must be a list of categories, strongest first (e.g. ["foundation", "column", "wall", "beam", "slab", "stair"]).`);
  const out = order.map((x) => {
    const s = String(x ?? "").trim();
    if (/^OST_\w+$/.test(s)) return s;
    const r = ROLE[s.toLowerCase()];
    if (!r) throw new Error(`${where}: "${s}" is not a category ACE knows (use foundation, column, wall, beam, slab, stair, ramp or an OST_ name).`);
    return r;
  });
  if (new Set(out).size !== out.length) throw new Error(`${where} names a category twice: ${order.join(", ")}.`);
  return out;
}

/** The vault's rules/clash-resolve.json ({ order, steel: { cutback, coping } }) -> { order?, steel? }; invalid -> error. */
export function readRules(text, file) {
  let o;
  try { o = JSON.parse(String(text).replace(/^﻿/, "")); } catch (e) { throw new Error(`${file} is not valid JSON (${e.message}): fix it or remove it.`); }
  if (!o || typeof o !== "object" || Array.isArray(o)) throw new Error(`${file} must hold an object { order, steel }.`);
  const steel = o.steel == null ? undefined : steelOf(o.steel, `${file} steel`);
  return { order: normaliseOrder(o.order, `${file} order`), steel };
}

function steelOf(s, where) {
  if (typeof s !== "object" || Array.isArray(s)) throw new Error(`${where} must be { cutback: true|false, coping: true|false, allowUnjoin: true|false }.`);
  const out = {};
  for (const k of ["cutback", "coping", "allowUnjoin"]) if (s[k] != null) {
    if (typeof s[k] !== "boolean") throw new Error(`${where}.${k} must be true or false.`);
    out[k] = s[k];
  }
  return out;
}

/**
 * The bridge request (without check_only / dry_run / expect_model): the tool's inputs over the vault's rules (the call
 * wins), the standard ST categories for the default test when none are given, borrowed elements always blocking.
 */
export function buildRequest(a, rules = {}, version) {
  const order = a.order ? normaliseOrder(a.order) : rules.order;
  const steel = { ...(rules.steel ?? {}), ...(a.steel ?? {}) };
  const test = a.test ?? DEFAULT_TEST;
  // The ST set only for the default test: another test name ("Arch vs ST") runs as the add-in defines it.
  const std = test.trim().toLowerCase() === DEFAULT_TEST.toLowerCase();
  const aCats = a.a_categories?.length ? a.a_categories : std ? ST_CATEGORIES : undefined;
  const bCats = a.b_categories?.length ? a.b_categories : a.a_categories?.length ? a.a_categories : std ? ST_CATEGORIES : undefined;
  return {
    test,
    ...(aCats ? { a_categories: aCats } : {}),
    ...(bCats ? { b_categories: bCats } : {}),
    ...(a.a_disciplines?.length ? { a_disciplines: a.a_disciplines } : {}),
    ...(a.b_disciplines?.length ? { b_disciplines: a.b_disciplines } : {}),
    ...materialArgs(a),   // part of the request, so part of the gate key
    ...(a.tolerance_mm != null ? { tolerance_mm: a.tolerance_mm } : {}),
    ...(a.level ? { level: a.level } : {}),
    ...(a.keys?.length ? { keys: a.keys } : {}),
    ...(order ? { order } : {}),
    ...(Object.keys(steel).length ? { steel } : {}),
    block_on_borrowed: true,
    expect_version: version,
    max_ids: a.max_ids ?? 20,
    ...(a.test_force_verify_mismatch ? { test_force_verify_mismatch: true } : {}),
  };
}

/** The plan's identity for the preview gate: the add-in's planHash and every step (ids are opaque, never parsed). */
export const planIdentity = (r) => ({ hash: r?.planHash ?? null, steps: (r?.plan ?? []).map((p) => [p.id, p.kind, p.a, p.b]) });
/** The add-in refused an apply because its recomputed plan differs from the previewed one (expect_plan). */
export const planChanged = (r) => r?.code === "plan-changed" || (r?.blocked ?? []).some((b) => b?.code === "plan-changed");
export const PLAN_CHANGED = { what: "the apply", why: "the model changed since the preview: run the dry run again", how: "run the dry run again, show the user the new plan, then apply", code: "plan-changed" };

export const countsByKind = (plan = []) => Object.fromEntries(KINDS.map((k) => [k, plan.filter((p) => p.kind === k).length]).filter(([, n]) => n));
const kindText = (c) => Object.entries(c).map(([k, n]) => `${k} ${n}`).join(", ") || "nothing";
/** What a rolled-back run would have done: the add-in's wouldDo when present, else the plan by kind. */
export const wouldCounts = (r) => r?.wouldDo ? doneCounts(r.wouldDo) : countsByKind(r?.plan);
export const doneCounts = (done = {}) => Object.fromEntries(KINDS.map((k) => [k, Number(done?.[DONE_FIELD[k]]) || 0]).filter(([, n]) => n));

/** Whether an apply was rolled back: the add-in says so, or the annotation guard or its self-check failed. */
export const rolledBackOf = (r) => r?.rolledBack === true || r?.guard?.ok === false || r?.verify?.ok === false;
/** Whether a dry run may be applied: it succeeded, nothing is blocked, the guard held and no new warning appeared. */
export const previewOk = (r) => r?.success === true && !(r?.blocked?.length) && r?.guard?.ok !== false && r?.verify?.ok !== false && !(r?.warnings?.new?.length);

const pairLine = (byPair) => (Array.isArray(byPair) ? byPair.map((p) => [p.pair ?? `${p.a ?? "?"} x ${p.b ?? "?"}`, p.clashes ?? p.count]) : Object.entries(byPair ?? {}))
  .sort((x, y) => (y[1] ?? 0) - (x[1] ?? 0)).map(([k, n]) => `${k} ${n}`).join(", ");
const ids = (x) => `${x.a} x ${x.b}`;

/** Report-only clashes grouped by reason: [{ why, recommendation, count, samples }]. */
export function groupReportOnly(list = []) {
  const by = new Map();
  for (const r of list) {
    const k = `${r.why ?? "needs a decision"}\0${r.recommendation ?? ""}`;
    const g = by.get(k) ?? { why: r.why ?? "needs a decision", recommendation: r.recommendation, count: 0, samples: [] };
    g.count++;
    if (g.samples.length < 3) g.samples.push(ids(r));
    by.set(k, g);
  }
  return [...by.values()].sort((x, y) => y.count - x.count);
}

const guardText = (g) => !g ? "not reported" : g.ok
  ? `ok (${g.checked ?? "every"} touched element(s): still there with the same type and location, the same tags, dimensions and spot elevations)`
  : `FAILED: ${(g.mismatches ?? []).length} difference(s)${(g.mismatches ?? []).length ? `: ${g.mismatches.slice(0, 5).map((m) => `#${m.element} ${m.what} ${JSON.stringify(m.before)} -> ${JSON.stringify(m.after)}`).join("; ")}` : ""}`;
/** Dimension values that changed on shortened (cut back) members while their references stayed: information, not a failure. */
const changedValues = (g) => g?.changedValues ?? [];
const valueLine = (c) => `#${c.element ?? "?"}${c.dimension != null ? ` dimension #${c.dimension}` : ""}${c.what ? ` ${c.what}` : ""}: ${JSON.stringify(c.before)} -> ${JSON.stringify(c.after)}`;
const warnText = (w) => !w ? "not reported" : `before ${w.before ?? "?"}, after ${w.after ?? "?"}, new ${(w.new ?? []).length ? (w.new ?? []).map((x) => typeof x === "string" ? x : x.text ?? x.message ?? JSON.stringify(x)).join("; ") : "none"}`;

/**
 * The reply in four stages: before, plan, report only, after (with the guard and the warnings). Blocks come first.
 * mode: "plan" (read-only check) | "dry run" | "apply".
 */
export function replyText(r, { mode, model, test, rolledBack = false, forced = false, scope = {} } = {}) {
  const lines = [];
  const plan = r?.plan ?? [];
  const counts = countsByKind(plan);
  const head = mode === "plan" ? "plan, read-only, nothing changed" : mode === "dry run" ? "dry run" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  lines.push(`CLASH RESOLVE ${test ?? r?.test ?? DEFAULT_TEST} in ${model ?? r?.model ?? "?"} (${head}).`);
  if (r?.blocked?.length) lines.push(`Blocked (${mode === "plan" ? "a fix stops here, before the dry run" : "nothing changed"}):\n${r.blocked.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  const ms = materialScope(scope);
  if (ms || filteredText(r?.filtered)) lines.push(`Material filter: ${ms || "none"}${filteredText(r?.filtered) ? `; left out: ${filteredText(r.filtered)} element(s)` : ""}.`);
  const b = r?.before ?? {};
  lines.push(`Before: ${b.clashes ?? "?"} clash(es) in ${b.issues ?? "?"} issue(s)${pairLine(b.byPair) ? `; by pair: ${pairLine(b.byPair)}` : ""}.`);
  lines.push(`Plan: ${plan.length} change(s): ${kindText(counts)}. Only joins, switched join orders, steel end cutbacks and copes; nothing is deleted, recreated or moved.`);
  // Steel ends: the method per step and what the probe found writable (read-only plan included), and any join-state change.
  const ends = plan.filter((p) => p.kind === "cutback" || p.kind === "cope");
  if (ends.length) lines.push(`Steel ends:\n${ends.slice(0, 10).map((p) => `- ${p.kind} ${p.a} at ${p.b}: ${p.method ?? "no method"}${p.changesJoinState ? " (CHANGES THE JOIN STATE: allowUnjoin)" : ""}${p.probe ? ` [writable: ${Object.entries(p.probe).filter(([k, v]) => v === true && k !== "joinedTo").map(([k]) => k).join(", ") || "none"}${p.probe.joinedTo ? `; joined to ${p.probe.joinedTo}` : ""}]` : ""}`).join("\n")}${ends.length > 10 ? `\n- ... ${ends.length - 10} more (run record)` : ""}`);
  const ro = groupReportOnly(r?.reportOnly);
  if (ro.length) lines.push(`Report only (needs a person, ${r.reportOnly.length}):\n${ro.slice(0, 8).map((g) => `- ${g.why} (${g.count}, e.g. ${g.samples.join("; ")})${g.recommendation ? `: ${g.recommendation}` : ""}`).join("\n")}${ro.length > 8 ? `\n- ... ${ro.length - 8} more reason(s) (run record)` : ""}`);
  if (mode !== "plan") {
    const after = r?.after;
    if (after) lines.push(`After: ${after.clashes ?? "?"} clash(es) ${mode === "dry run" || rolledBack ? "would remain" : "remain"}${after.issues != null ? ` in ${after.issues} issue(s)` : ""}${(after.remaining ?? []).length ? `:\n${after.remaining.slice(0, 10).map((x) => `- ${ids(x)}: ${x.why ?? "not auto-fixable"}`).join("\n")}${after.remaining.length > 10 ? `\n- ... ${after.remaining.length - 10} more (evidence)` : ""}` : "."}`);
    lines.push(forced && r?.guard?.ok === false ? "Annotation guard FAILED (forced by test flag) - rolled back, nothing changed." : `Annotation guard: ${guardText(r?.guard)}.`);
    if (changedValues(r?.guard).length) lines.push(`Information: ${changedValues(r.guard).length} dimension value(s) on shortened members changed, references intact (${changedValues(r.guard).slice(0, 5).map(valueLine).join("; ")}${changedValues(r.guard).length > 5 ? "; ..." : ""}).`);
    if (r?.verify && r.verify.ok === false) lines.push(`Self-check FAILED: ${r.verify.mismatchCount ?? r.verify.mismatches?.length ?? "?"} mismatch(es); rolled back, nothing changed.`);
    lines.push(`Warnings: ${warnText(r?.warnings)}.`);
    if (mode === "dry run" || rolledBack) lines.push(`Changes: would have been ${kindText(wouldCounts(r))}; rolled back, nothing changed.`);
    else lines.push(`Changes: ${kindText(doneCounts(r?.done))} done.`);
  }
  const skipped = r?.skipped ?? [];
  if (skipped.length) lines.push(`Skipped:\n${skipped.slice(0, 10).map((s) => `- ${s.id ? `${s.id} ` : ""}${ids(s)}: ${s.reason ?? "skipped"}`).join("\n")}${skipped.length > 10 ? `\n- ... ${skipped.length - 10} more (run record)` : ""}`);
  return lines.join("\n");
}

/** evidence-clash-<model>-<stamp>.md after an apply (kept or rolled back). */
export function evidenceMarkdown({ model, test, when = localDate(new Date(), { time: true }), result: r = {}, explanation, scope = {} }) {
  const rb = rolledBackOf(r);
  const plan = r.plan ?? [];
  const planned = countsByKind(plan), done = doneCounts(r.done), would = wouldCounts(r);
  const g = r.guard, w = r.warnings ?? {};
  const out = [`# Clash resolve evidence: ${model}`, "",
    `${when}. Test ${test ?? r.test ?? DEFAULT_TEST}. ${rb ? "Rolled back: nothing changed; the counts below say what would have been done." : r.success === false ? "Not applied: nothing changed." : "Applied as ONE undo step."}${explanation ? ` ${explanation}` : ""}`, "",
    "| | count |", "|---|---|",
    `| clashes before | ${r.before?.clashes ?? "?"} (${r.before?.issues ?? "?"} issue(s)) |`,
    `| clashes after | ${rb ? `${r.before?.clashes ?? "?"} (as before: rolled back${r.after?.clashes != null ? `; would have been ${r.after.clashes}` : ""})` : r.after?.clashes ?? "?"} |`,
    ...KINDS.map((k) => `| ${k} | ${rb ? `0 (${would[k] ?? 0} would have been done; rolled back)` : `${done[k] ?? 0} of ${planned[k] ?? 0}`} |`),
    `| material filter | ${materialScope(scope) || "none"}${filteredText(r.filtered) ? `; left out ${filteredText(r.filtered)}` : ""} |`,
    `| report only | ${(r.reportOnly ?? []).length} |`,
    `| skipped | ${(r.skipped ?? []).length} |`,
    `| annotation guard | ${!g ? "not reported" : g.ok ? `ok (${g.checked ?? "every"} element(s) checked)` : `FAILED: ${(g.mismatches ?? []).length} difference(s), rolled back`} |`,
    `| warnings | before ${w.before ?? "?"}, after ${w.after ?? "?"}, new ${(w.new ?? []).length} |`, ""];
  const section = (title, items) => { if (items.length) out.push(`## ${title}`, "", ...items.map((i) => `- ${i}`), ""); };
  if (r.before?.byPair) section("Clashes before by pair", [pairLine(r.before.byPair)]);
  // Only steps that were made (or would have been): a step Revit refused or a block held is listed under Skipped, not here.
  const skippedIds = new Set((r.skipped ?? []).map((x) => x.id).filter(Boolean));
  section(rb ? "Changes (would have been made; rolled back)" : "Changes", plan.filter((p) => !skippedIds.has(p.id)).map((p) => `${p.kind}: ${p.a} x ${p.b} (${[p.aCategory, p.bCategory].filter(Boolean).join(" x ")}${p.material ? `, ${p.material}` : ""}${p.level ? `, ${p.level}` : ""})${p.why ? `: ${p.why}` : ""}`));
  section("Annotation guard differences", (g?.mismatches ?? []).map((m) => `#${m.element} ${m.what}: ${JSON.stringify(m.before)} -> ${JSON.stringify(m.after)}`));
  section("Dimension values changed on shortened members (references intact; information, not a failure)", changedValues(g).map(valueLine));
  section("New warnings", (w.new ?? []).map((x) => typeof x === "string" ? x : x.text ?? x.message ?? JSON.stringify(x)));
  section("Remaining clashes", (rb ? [] : r.after?.remaining ?? []).map((x) => `${ids(x)}: ${x.why ?? "not auto-fixable"}`));
  section("Report only (needs a person)", (r.reportOnly ?? []).map((x) => `${ids(x)} (${[x.aCategory, x.bCategory].filter(Boolean).join(" x ")}): ${x.why ?? ""}${x.recommendation ? `; ${x.recommendation}` : ""}`));
  section("Skipped", (r.skipped ?? []).map((s) => `${s.id ? `${s.id} ` : ""}${ids(s)}: ${s.reason ?? ""}`));
  section("Blocked", (r.blocked ?? []).map(blockLine));
  if (!rb && r.success !== false) out.push("Next: re-run run_clash_test with the same test so the fixed clashes show as resolved.", "");
  return out.join("\n");
}

export const evidenceName = (model, stamp) => `${fileStem("evidence-clash", model, stamp)}.md`;
export const recordName = (model, stamp) => `${fileStem("clash-run", model, stamp)}.json`;
