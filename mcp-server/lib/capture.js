// capture_method: what a person did between start and stop, read from the add-in's per-element diff and the Revit
// journal. Groups the changes into actions by pair type ("Floors x Structural Columns: join added, Floors cut Structural
// Columns"), lists the journal commands, compares with a stored clash_resolve plan, and writes the case record. Never
// turns a case into rules: the record ends with proposed rules for the owner to confirm. Pure functions.
import { safeName } from "./paramexec.js";

// ---- the add-in's diff, in one shape --------------------------------------------------------------------------------

const FIELD = { location: "moved", startExtension: "extension", endExtension: "extension", baseOffset: "offset", topOffset: "offset", type: "type", family: "type", material: "material", workset: "workset", level: "level" };

/** Join lists [{ id, cuts, category? }] before / after -> join-added / join-removed / join-order changes. */
function joinChanges(before = [], after = []) {
  const b = new Map((before ?? []).map((j) => [String(j.id), j])), a = new Map((after ?? []).map((j) => [String(j.id), j]));
  const out = [];
  for (const [id, j] of a) {
    const o = b.get(id);
    if (!o) out.push({ what: "join-added", other: j.id, otherCategory: j.category, cuts: !!j.cuts });
    else if (!!o.cuts !== !!j.cuts) out.push({ what: "join-order", other: j.id, otherCategory: j.category ?? o.category, cuts: !!j.cuts });
  }
  for (const [id, j] of b) if (!a.has(id)) out.push({ what: "join-removed", other: j.id, otherCategory: j.category, cuts: !!j.cuts });
  return out;
}

/**
 * The stop answer -> diffs [{ id, category, type, changes: [{ what, before, after, other, otherCategory, cutter, end, name }] }].
 * Reads diffs (what) or changed / added / deleted (field, before, after; joins as lists).
 */
export function diffsOf(r) {
  if (Array.isArray(r?.diffs)) return r.diffs.map((d) => ({ ...d, changes: (d.changes ?? []).map((c) => ({ ...c, cutter: c.cutter ?? (c.cuts == null ? undefined : c.cuts ? d.id : c.other) })) }));
  const out = [];
  for (const e of r?.changed ?? []) {
    const changes = [];
    for (const c of e.changes ?? []) {
      if (c.field === "joins") { for (const j of joinChanges(c.before, c.after)) changes.push({ ...j, cutter: j.cuts ? e.id : j.other }); continue; }
      if (String(c.field).startsWith("params.")) { changes.push({ what: "parameter", name: c.field.slice(7), before: c.before, after: c.after }); continue; }
      changes.push({ what: FIELD[c.field] ?? c.field, before: c.before, after: c.after, ...(c.field?.endsWith("Extension") ? { end: c.field.startsWith("start") ? "start" : "end" } : {}), ...(/Offset$/.test(c.field) ? { name: c.field } : {}) });
    }
    out.push({ id: e.id, uniqueId: e.uniqueId, category: e.category, type: e.type, changes });
  }
  for (const e of r?.added ?? []) out.push({ id: e.id, category: e.category, type: e.type, changes: [{ what: "created" }] });
  for (const e of r?.deleted ?? []) out.push({ id: e.id, category: e.category, type: e.type, changes: [{ what: "deleted" }] });
  return out;
}

// ---- actions by pair type -------------------------------------------------------------------------------------------

const JOIN = { "join-added": "join added", "join-removed": "join removed", "join-order": "join order switched" };
const SINGLE = { moved: "moved", extension: "end extension changed", offset: "offset changed", type: "type changed", material: "material changed", workset: "workset changed", level: "level changed", parameter: "parameter changed", created: "created", deleted: "deleted", "cope-added": "cope added", "cope-removed": "cope removed" };

/**
 * Groups the changes into actions: joins per unordered element pair (counted once), by category pair; the rest per
 * category. Each: { key, label, what, count, ids (sample), cut: { "<cutter> cut <cut>": n }, rule, exceptions }.
 */
export function actionsOf(diffs = []) {
  const catOf = new Map(diffs.map((d) => [String(d.id), d.category]));
  const groups = new Map(), seen = new Set();
  const add = (key, base, item) => { const g = groups.get(key) ?? { key, ...base, count: 0, ids: [], pairs: [], cut: {} }; g.count++; if (g.ids.length < 5) g.ids.push(item.ids); g.pairs.push(item); groups.set(key, g); };
  for (const d of diffs) for (const c of d.changes ?? []) {
    if (JOIN[c.what]) {
      const other = String(c.other), pair = [String(d.id), other].sort().join("|");
      if (seen.has(`${c.what}|${pair}`)) continue;
      seen.add(`${c.what}|${pair}`);
      const oc = c.otherCategory ?? catOf.get(other) ?? `element ${other}`;
      const cats = [d.category ?? "?", oc].sort();
      const cutter = c.cutter != null ? String(c.cutter) : null;
      const cutterCat = cutter == null ? null : cutter === String(d.id) ? d.category : oc;
      const cutCat = cutter == null ? null : cutter === String(d.id) ? oc : d.category;
      add(`${cats.join(" x ")}|${c.what}`, { what: c.what, label: `${cats.join(" x ")}: ${JOIN[c.what]}`, categories: cats },
        { ids: `${d.id} x ${other}`, a: String(d.id), b: other, cutter, cutterCat, cutCat });
      continue;
    }
    const what = SINGLE[c.what] ? c.what : "other";
    const label = `${d.category ?? "?"}: ${SINGLE[c.what] ?? c.what}${c.what === "parameter" && c.name ? ` (${c.name})` : c.what === "offset" && c.name ? ` (${c.name})` : c.what === "extension" && c.end ? ` (${c.end})` : ""}`;
    add(label, { what, label, categories: [d.category] }, { ids: String(d.id), a: String(d.id), b: c.other != null ? String(c.other) : null });
  }
  for (const g of groups.values()) {
    if (!JOIN[g.what]) continue;
    for (const p of g.pairs) if (p.cutterCat) { const k = `${p.cutterCat} cut ${p.cutCat}`; g.cut[k] = (g.cut[k] ?? 0) + 1; }
    const ranked = Object.entries(g.cut).sort((x, y) => y[1] - x[1]);
    if (ranked.length && g.categories[0] !== g.categories[1]) {
      g.rule = { text: ranked[0][0], count: ranked[0][1], of: g.count };
      g.exceptions = g.pairs.filter((p) => p.cutterCat && `${p.cutterCat} cut ${p.cutCat}` !== ranked[0][0]).map((p) => `${p.ids} (${p.cutterCat} cuts)`);
    } else g.exceptions = [];
  }
  return [...groups.values()].sort((x, y) => y.count - x.count || x.label.localeCompare(y.label));
}
export const actionLine = (g) => `${g.label}: ${g.count}${g.rule ? `; ${g.rule.text} in ${g.rule.count} of ${g.rule.of}` : ""}${g.exceptions?.length ? `; exceptions: ${g.exceptions.slice(0, 5).join(", ")}${g.exceptions.length > 5 ? ", ..." : ""}` : ""} (e.g. ${g.ids.join(", ")})`;

// ---- the journal ----------------------------------------------------------------------------------------------------

const COMMANDS = {
  ID_JOIN_ELEMENTS_EDITOR: "Join Geometry", ID_UNJOIN_ELEMENTS_EDITOR: "Unjoin Geometry", ID_SWITCH_JOIN_ORDER: "Switch Join Order",
  ID_EDIT_MOVE: "Move", ID_EDIT_COPY: "Copy", ID_EDIT_ROTATE: "Rotate", ID_EDIT_MIRROR_PICK_AXIS: "Mirror", ID_EDIT_ALIGN: "Align",
  ID_EDIT_TRIM_EXTEND_TO_CORNER: "Trim/Extend to Corner", ID_EDIT_TRIM_EXTEND_SINGLE: "Trim/Extend Single", ID_EDIT_SPLIT: "Split Element",
  ID_EDIT_PROFILE: "Edit Profile", ID_EDIT_SKETCH: "Edit Sketch", ID_COPING: "Cope", ID_BEAM_COPING: "Cope", ID_EDIT_CUT_GEOMETRY: "Cut Geometry",
  ID_BUTTON_UNDO: "Undo", ID_BUTTON_REDO: "Redo", ID_BUTTON_DELETE: "Delete", ID_EDIT_PROPERTIES: "Properties", ID_EDIT_TYPE: "Edit Type",
  ID_FILE_SAVE: "Save", ID_FILE_SAVE_TO_MASTER: "Synchronize with Central", ID_ESC: null,
};

/**
 * Journal lines -> the commands in order: Jrn.Command "<source>" , "<Name> , ID_X" (the name Revit gives), else a known
 * ID_ name; an unknown ID_ is kept raw. -> { sequence: [{ command, id, times }], totals: { command: n } }
 */
export function journalCommands(lines = []) {
  const seq = [];
  for (const line of lines) {
    let name = null, id = null;
    const m = /Jrn\.Command\s+"[^"]*"\s*,\s*"([^"]*)"/.exec(line);
    if (m) {
      const parts = m[1].split(/\s*,\s*/);
      id = parts.find((p) => /^ID_[A-Z0-9_]+$/.test(p)) ?? null;
      name = parts.find((p) => p && !/^ID_[A-Z0-9_]+$/.test(p)) ?? null;
    } else {
      const i = /\b(ID_[A-Z0-9_]+)\b/.exec(line);
      if (!i || !/^\s*Jrn\./.test(line)) continue;
      id = i[1];
    }
    if (!name && id) name = Object.hasOwn(COMMANDS, id) ? COMMANDS[id] : id;
    if (!name) continue;
    if (!m && id && !Object.hasOwn(COMMANDS, id)) name = id;   // unknown ids stay raw
    const last = seq.at(-1);
    if (last && last.command === name) last.times++; else seq.push({ command: name, id, times: 1 });
  }
  const totals = {};
  for (const s of seq) totals[s.command] = (totals[s.command] ?? 0) + s.times;
  return { sequence: seq, totals };
}

// ---- compared with a clash_resolve plan -----------------------------------------------------------------------------

/**
 * A stored clash_resolve plan ([{ kind, a, b }], a = the element that cuts or is shortened / coped) against what the
 * person did. -> { rows: [{ a, b, plan, person, verdict: agree | differs | planned, not done | not planned }], counts }
 */
export function compareWithPlan(plan = [], diffs = []) {
  const byPair = new Map(), byElem = new Map();
  const pk = (x, y) => [String(x), String(y)].sort().join("|");
  for (const d of diffs) for (const c of d.changes ?? []) {
    if (JOIN[c.what] || c.what === "cope-added") {
      const k = pk(d.id, c.other);
      if (!byPair.has(k) || JOIN[c.what]) byPair.set(k, { what: c.what, cutter: c.cutter != null ? String(c.cutter) : null, a: String(d.id), b: String(c.other), copedAt: c.what === "cope-added" ? String(d.id) : undefined });
    } else byElem.set(String(d.id), [...(byElem.get(String(d.id)) ?? []), c.what]);
  }
  const rows = [], used = new Set();
  for (const p of plan) {
    const a = String(p.a), b = String(p.b), k = pk(a, b);
    const did = byPair.get(k);
    const elemActs = [...(byElem.get(a) ?? []), ...(byElem.get(b) ?? [])];
    const planText = p.kind === "cutback" ? `cut back ${a} at ${b}` : p.kind === "cope" ? `cope ${a} at ${b}` : `${a} cuts ${b} (${p.kind})`;
    let person = "nothing", verdict = "planned, not done";
    if (did) {
      used.add(k);
      person = JOIN[did.what] ? `${JOIN[did.what]}${did.cutter ? `, ${did.cutter} cuts` : ""}` : "cope added";
      if ((p.kind === "join" || p.kind === "switch-join") && JOIN[did.what] && did.what !== "join-removed") verdict = did.cutter === a ? "agree" : "differs";
      else if (p.kind === "cope" && did.what === "cope-added") verdict = "agree";
      else verdict = "differs";
    } else if (elemActs.length) {
      person = [...new Set(elemActs)].map((w) => SINGLE[w] ?? w).join(", ");
      verdict = p.kind === "cutback" && (byElem.get(a) ?? []).includes("extension") ? "agree" : "differs";
      used.add(`e|${a}`); used.add(`e|${b}`);
    }
    rows.push({ a, b, plan: planText, person, verdict });
  }
  for (const [k, did] of byPair) if (!used.has(k)) rows.push({ a: did.a, b: did.b, plan: "-", person: JOIN[did.what] ?? "cope added", verdict: "not planned" });
  const counts = {};
  for (const r of rows) counts[r.verdict] = (counts[r.verdict] ?? 0) + 1;
  return { rows, counts };
}

// ---- the case record ------------------------------------------------------------------------------------------------

export const caseStem = (date, label) => `${date}-${safeName(label).replace(/\s+/g, "-").toLowerCase()}`;

/** The case as markdown: actions, journal commands, the plan comparison, then proposed rules for the owner to confirm. */
export function caseMarkdown(c) {
  const out = [`# Method case: ${c.label}`, "", `Model ${c.model}; captured ${c.startedAt} to ${c.stoppedAt}${c.elements != null ? `; ${c.elements} element(s) in scope` : ""}${c.categories?.length ? ` (${c.categories.join(", ")})` : ""}.`,
    `Changed elements: ${c.diffs.length}. Recorded by ACE capture_method; this is a record of what a person did, not a rule.`, "", "## Actions by pair type", ""];
  out.push(...(c.actions.length ? c.actions.map((g) => `- ${actionLine(g)}`) : ["- No element changed."]), "");
  out.push("## Revit commands (journal)", "");
  if (c.journal.sequence.length) out.push(`- In order: ${c.journal.sequence.map((s) => `${s.command}${s.times > 1 ? ` x${s.times}` : ""}`).join(" > ")}`, `- Totals: ${Object.entries(c.journal.totals).sort((x, y) => y[1] - x[1]).map(([k, n]) => `${k} ${n}`).join(", ")}`);
  else out.push("- No commands found in the journal lines.");
  if (c.journalNote) out.push(`- ${c.journalNote}`);
  out.push("");
  if (c.comparison) {
    out.push(`## Compared with the clash_resolve plan (${c.comparison.file})`, "", `${Object.entries(c.comparison.counts).map(([k, n]) => `${k} ${n}`).join(", ") || "nothing to compare"}.`, "",
      "| A | B | plan | person | verdict |", "|---|---|---|---|---|", ...c.comparison.rows.slice(0, 60).map((r) => `| ${r.a} | ${r.b} | ${r.plan} | ${r.person} | ${r.verdict} |`), "");
    if (c.comparison.rows.length > 60) out.push(`${c.comparison.rows.length - 60} more row(s) in the JSON record.`, "");
  }
  out.push("## Proposed rules (need owner confirmation)", "",
    "Nothing below is applied. A person confirms each line; only then does it go into the vault (e.g. rules/clash-resolve.json order).", "");
  const rules = c.actions.filter((g) => g.rule).map((g) => `- ${g.rule.text.replace(" cut ", " before ")} in the join order (${g.label.split(":")[0]}: ${g.rule.text} in ${g.rule.count} of ${g.rule.of} pair(s)${g.exceptions.length ? `; ${g.exceptions.length} exception(s)` : ""})`);
  out.push(...(rules.length ? rules : ["- None proposed: no consistent join direction between two categories."]), "");
  return out.join("\n");
}
