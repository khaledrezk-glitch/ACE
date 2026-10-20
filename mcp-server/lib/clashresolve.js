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
export const KINDS = ["join", "switch-join", "cutback", "cope", "trim"];
const DONE_FIELD = { join: "join", "switch-join": "switchJoin", cutback: "cutback", cope: "cope", trim: "trim" };
export const SCOPES = ["clashing", "all-touching"];

// Words people write in the vault's order -> the Revit category the add-in reads. OST names pass unchanged.
const ROLE = {
  foundation: "OST_StructuralFoundation", foundations: "OST_StructuralFoundation",
  column: "OST_StructuralColumns", columns: "OST_StructuralColumns",
  wall: "OST_Walls", walls: "OST_Walls",
  beam: "OST_StructuralFraming", beams: "OST_StructuralFraming", framing: "OST_StructuralFraming",
  slab: "OST_Floors", slabs: "OST_Floors", floor: "OST_Floors", floors: "OST_Floors",
  stair: "OST_Stairs", stairs: "OST_Stairs", ramp: "OST_Ramps", ramps: "OST_Ramps",
  "structural foundation": "OST_StructuralFoundation", "structural foundations": "OST_StructuralFoundation",
  "structural column": "OST_StructuralColumns", "structural columns": "OST_StructuralColumns", "structural framing": "OST_StructuralFraming",
};
// The add-in's built-in join priority (strongest first), when neither the call nor the vault gives one.
export const DEFAULT_ORDER = ["OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming", "OST_Stairs"];
const NICE = { OST_StructuralFoundation: "Structural Foundations", OST_StructuralColumns: "Structural Columns", OST_Walls: "Walls", OST_Floors: "Floors", OST_StructuralFraming: "Structural Framing", OST_Stairs: "Stairs", OST_Ramps: "Ramps" };
export const niceCategory = (c) => NICE[c] ?? String(c).replace(/^OST_/, "");
/** Where the join order came from and what it is: the add-in's echo first, else what the request sent. */
export function orderLine(r, req, merged = {}) {
  const source = req?.order_source ? "vault" : req?.order ? "request" : (r?.orderSource ?? "default");
  const order = r?.order ?? req?.order ?? DEFAULT_ORDER;
  const from = source === "vault" ? whereText(merged.from?.order, merged.files ?? {}) ?? "the rules file" : source === "request" ? "the request" : "ACE's default";
  const scope = r?.scope ?? req?.scope ?? "clashing";
  const sSource = r?.scopeSource ?? req?.scope_source ?? (req?.scope ? "request" : "default");
  const sFrom = req?.scope_source ? whereText(merged.from?.scope, merged.files ?? {}) ?? "the rules file" : sSource === "request" ? "the request" : "ACE's default";
  return `Join order (one category priority list, strongest cuts; from ${from}): ${order.map(niceCategory).join(" > ")}. Same category: the larger element cuts.\n` +
    `Scope (from ${sFrom}): ${scope === "all-touching" ? "every touching pair of the categories" : "only the clashing pairs"}${req?.selection ? ", in the current selection" : ""}. Trim to face: ${req?.trim_to_face ? `on (allowed by ${whereText(merged.from?.trimAllowed, merged.files ?? {}) ?? "the rules"}${req.trim_to_face.max_mm ? `, at most ${req.trim_to_face.max_mm} mm` : ""})` : "off"}.`;
}

/** The plan per category pair and kind: "Floors x Structural Columns: join 25, switch-join 3" (all-touching can be large). */
export function pairSummary(plan = []) {
  const by = new Map();
  for (const p of plan) {
    const k = [p.cutterCategory ?? p.aCategory ?? "?", p.cutCategory ?? p.bCategory ?? "?"].map(niceCategory).sort().join(" x ");
    const g = by.get(k) ?? { n: 0, kinds: {} };
    g.n++; g.kinds[p.kind] = (g.kinds[p.kind] ?? 0) + 1; by.set(k, g);
  }
  return [...by].sort((x, y) => y[1].n - x[1].n).map(([k, g]) => `${k}: ${kindText(g.kinds)}`);
}
/** One trim: element, end, length before -> after, trim in mm, the face. */
/** One trim: element, end, length before -> after, trim in mm, the face (fields as the add-in sends them). */
export function trimLine(p) {
  const m = /^trim:(\d+)>(\d+):(\w+)/.exec(String(p.id ?? ""));
  const el = p.trimmed ?? p.cut ?? p.a ?? m?.[1], owner = p.faceOwner ?? p.cutter ?? p.b ?? m?.[2], end = p.end ?? m?.[3] ?? "end";
  const cat = p.cutCategory ?? p.aCategory;
  return `trim ${el}${cat ? ` (${niceCategory(cat)})` : ""} ${end}: ${p.lengthBefore ?? p.lengthBeforeMm ?? "?"} -> ${p.lengthAfter ?? p.lengthAfterMm ?? "?"} mm (trim ${p.trimMm ?? "?"} mm) to ${p.face ?? p.faceOf ?? `the face of ${owner}`}${p.fallbackFor ? ` (only if ${p.fallbackFor} does not clear)` : ""}`;
}
/** Trims: planned on their own, or carried by a join as its fallback (the add-in's 1.12.8 shape). */
export const trimsOf = (plan = []) => [...plan.filter((p) => p.kind === "trim"), ...plan.map((p) => p.trim).filter((t) => t && typeof t === "object")];
export const droppedOf = (r) => Number(r?.dropped?.notTouching ?? r?.droppedNotTouching ?? 0) || 0;
/** All-touching scope: what the add-in looked at. */
export function touchingLine(t) {
  if (!t) return "";
  const by = Array.isArray(t.byPair) ? t.byPair.map((x) => `${x.pair ?? `${x.a} x ${x.b}`} ${x.count ?? x.pairs ?? ""}`.trim()) : Object.entries(t.byPair ?? {}).map(([k, v]) => `${k} ${typeof v === "object" ? JSON.stringify(v) : v}`);
  return `All touching: ${t.candidates ?? "?"} candidate pair(s), ${t.pairs ?? "?"} touching, ${t.right ?? 0} already right; join ${t.join ?? 0}, switch join ${t.switchJoin ?? 0}${by.length ? ` (${by.slice(0, 8).join(", ")}${by.length > 8 ? ", ..." : ""})` : ""}.`;
}

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

/**
 * The vault's rules/clash-resolve.json ({ order, scope, trim_to_face, steel }) -> { order?, scope?, trim?, steel? }.
 * scope: "clashing" (default) | "all-touching"; trim_to_face: { allowed, preview_required, max_mm } (owner-approved
 * fix kind). An unknown value is an error, never ignored.
 */
export function readRules(text, file) {
  let o;
  try { o = JSON.parse(String(text).replace(/^\uFEFF/, "")); } catch (e) { throw new Error(`${file} is not valid JSON (${e.message}): fix it or remove it.`); }
  if (!o || typeof o !== "object" || Array.isArray(o)) throw new Error(`${file} must hold an object { order, scope, trim_to_face, steel }.`);
  const steel = o.steel == null ? undefined : steelOf(o.steel, `${file} steel`);
  return { order: normaliseOrder(o.order, `${file} order`), scope: scopeOf(o.scope, `${file} scope`), trim: trimOf(o.trim_to_face, `${file} trim_to_face`), steel };
}

/**
 * Office rules (clash-resolve.json beside config.json, or config.json "clashResolveRules") under the project vault's
 * rules/clash-resolve.json (D3): the project wins key by key; trim_to_face and steel merge per sub-key.
 * office / project: { rules, file } or null -> { rules, from: { order, scope, trim, steel: "office" | "project" }, files }
 */
export function mergeRules(office, project) {
  const o = office?.rules ?? {}, p = project?.rules ?? {};
  const from = {}, rules = {};
  for (const k of ["order", "scope"]) {
    if (p[k] != null) { rules[k] = p[k]; from[k] = "project"; } else if (o[k] != null) { rules[k] = o[k]; from[k] = "office"; }
  }
  for (const k of ["trim", "steel"]) {
    if (o[k] == null && p[k] == null) continue;
    rules[k] = { ...(o[k] ?? {}), ...(p[k] ?? {}) };
    from[k] = p[k] && o[k] ? (Object.keys(p[k]).length === Object.keys(rules[k]).length ? "project" : "project over office") : p[k] ? "project" : "office";
    if (k === "trim") from.trimAllowed = p.trim?.allowed != null ? "project" : "office";
  }
  return { rules, from, files: { office: office?.file ?? null, project: project?.file ?? null } };
}
const whereText = (src, files) => src === "office" ? `the office file (${files.office})` : src === "project" ? `the project vault (${files.project})`
  : src === "project over office" ? `the project vault over the office file (${files.project}; ${files.office})` : null;

export function scopeOf(v, where = "scope") {
  if (v == null) return undefined;
  if (!SCOPES.includes(v)) throw new Error(`${where}: "${v}" is not a scope ACE knows (use "clashing" or "all-touching").`);
  return v;
}

function trimOf(t, where) {
  if (t == null) return undefined;
  if (typeof t !== "object" || Array.isArray(t)) throw new Error(`${where} must be { allowed: true|false, preview_required: true|false, max_mm: <number> }.`);
  const out = {};
  for (const [k, v] of Object.entries(t)) {
    if (k.startsWith("_")) continue;
    if (k === "allowed" || k === "preview_required") { if (typeof v !== "boolean") throw new Error(`${where}.${k} must be true or false.`); out[k] = v; }
    else if (k === "max_mm") { if (typeof v !== "number" || !(v > 0)) throw new Error(`${where}.max_mm must be a positive number of millimetres.`); out.max_mm = v; }
    else throw new Error(`${where}: "${k}" is not a setting ACE knows (allowed, preview_required, max_mm).`);
  }
  if (out.allowed == null) throw new Error(`${where}.allowed is missing: true or false.`);
  return out;
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
  const scope = scopeOf(a.scope, "scope") ?? rules.scope;
  const scopeSource = a.scope ? null : rules.scope ? "vault" : null;
  // Trim to a face is approved in the vault only; a call can switch it off, never on.
  if (a.trim_to_face === true && !rules.trim?.allowed) {
    const w = rules.from?.trimAllowed === "office" ? `the office file ${rules.files?.office}` : rules.from?.trimAllowed === "project" ? `the vault's rules/${RULES_FILE}` : null;
    throw new Error(`trim_to_face is not allowed for this project: ${w ? `${w} has trim_to_face.allowed: false` : `neither the vault's rules/${RULES_FILE} nor the office file has trim_to_face`}. Only the owner switches it on there.`);
  }
  const trim = rules.trim?.allowed && a.trim_to_face !== false ? { allowed: true, preview_required: rules.trim.preview_required !== false, ...(rules.trim.max_mm ? { max_mm: rules.trim.max_mm } : {}) } : null;
  const orderSource = a.order ? null : rules.order ? "vault" : null;   // the add-in echoes "request" / "default" itself
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
    ...(orderSource ? { order_source: orderSource } : {}),
    ...(scope ? { scope } : {}),
    ...(scopeSource ? { scope_source: scopeSource } : {}),
    ...(a.selection ? { selection: true } : {}),
    ...(trim ? { trim_to_face: trim } : {}),
    ...(Object.keys(steel).length ? { steel } : {}),
    block_on_borrowed: true,
    expect_version: version,
    max_ids: a.max_ids ?? 20,
    ...(a.test_force_verify_mismatch ? { test_force_verify_mismatch: true } : {}),
  };
}

/** The plan's identity for the preview gate: the add-in's planHash and every step (ids are opaque, never parsed). */
export const planIdentity = (r) => ({ hash: r?.planHash ?? null, steps: (r?.plan ?? []).map((p) => p.kind === "trim" ? [p.id, p.kind, p.a, p.b, p.end ?? null, p.trimMm ?? null] : [p.id, p.kind, p.a, p.b]) });
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
  lines.push(`Plan: ${plan.length} change(s): ${kindText(counts)}. Only joins, switched join orders, steel end cutbacks and copes${counts.trim ? ", and trims to a face (approved in the vault)" : ""}; nothing is deleted, recreated or moved${counts.trim ? " (a trim changes only the member's end)" : ""}.`);
  const pairs = pairSummary(plan);
  if (pairs.length > 1 || plan.length > 10) lines.push(`By category pair:\n${pairs.slice(0, 15).map((x) => `- ${x}`).join("\n")}${pairs.length > 15 ? `\n- ... ${pairs.length - 15} more (run record)` : ""}`);
  if (touchingLine(r?.touching)) lines.push(touchingLine(r.touching));
  if (droppedOf(r)) lines.push(`Not touching, dropped: ${droppedOf(r)} pair(s) in scope that do not touch (no join needed).`);
  if (Number(r?.dropped?.trim)) lines.push(`Trims dropped: ${r.dropped.trim}.`);
  const trims = trimsOf(plan);
  if (trims.length) lines.push(`Trims to a face (${trims.length}; a trim on a join runs only if the join does not clear):\n${trims.slice(0, 50).map((p) => `- ${trimLine(p)}`).join("\n")}${trims.length > 50 ? `\n- ... ${trims.length - 50} more (run record)` : ""}`);
  const capMm = r?.trimToFace?.maxMm ?? r?.trimToFace?.max_mm;
  if (capMm && capMm < (scope.trim_to_face?.max_mm ?? 0)) lines.push(`Trim limit: the add-in caps a trim at ${capMm} mm (the rules ask ${scope.trim_to_face.max_mm} mm).`);
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
  section(rb ? "Trims to a face (would have been made; rolled back)" : "Trims to a face (planned; a join's trim runs only if the join does not clear)", trimsOf(plan).filter((p) => !skippedIds.has(p.id)).map(trimLine));
  if (doneCounts(r.done).trim) section("Trims made", [`${doneCounts(r.done).trim}`]);
  if (droppedOf(r)) section("Not touching, dropped", [`${droppedOf(r)} pair(s) in scope did not touch; no join made`]);
  section(rb ? "Changes (would have been made; rolled back)" : "Changes", plan.filter((p) => !skippedIds.has(p.id) && p.kind !== "trim").map((p) => `${p.kind}: ${p.a} x ${p.b} (${[p.aCategory, p.bCategory].filter(Boolean).join(" x ")}${p.material ? `, ${p.material}` : ""}${p.level ? `, ${p.level}` : ""})${p.why ? `: ${p.why}` : ""}`));
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

/** The clash store key: the central model's name as the add-in reports it, so runs of local copies connect. */
export const storeKeyOf = (r, model) => r?.storeKey ?? r?.centralName ?? model;
export const evidenceName = (model, stamp) => `${fileStem("evidence-clash", model, stamp)}.md`;
export const recordName = (model, stamp) => `${fileStem("clash-run", model, stamp)}.json`;
