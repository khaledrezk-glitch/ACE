// view_membership_fill: one parameter set from what a view shows ("value_in" for the elements the view shows, "value_out"
// for the rest), e.g. an export status from the Navisworks view. The definition of a missing parameter comes only from
// the vault's shared parameter files (never invented); the name is checked against the vault's spelling; the impact says
// which rules, export settings and requirement files mention the parameter. Pure functions; index.js reads the files.
import fs from "node:fs";
import path from "node:path";
import { classifyName, editDistance, paramIndex, readSharedParameterFile } from "./paramsources.js";
import { blockLine } from "./paramexec.js";

/**
 * The definition and the spelling verdict from the vault's shared parameter files.
 * files: [{ file, kind: "project" | "client", buf }] -> { definition | null, verdict, use (the name to write), notes }
 */
export function definitionFor(name, files, mapping = {}) {
  const idx = paramIndex(files.map((f) => ({ file: f.file, kind: f.kind, params: readSharedParameterFile(f.buf).params })));
  const c = classifyName(name, idx, mapping ?? {});
  const notes = [];
  let use = name, def = null, near;
  const entry = (n) => idx.project.get(n) ?? idx.client.get(n);
  if (c.match === "exact" || c.match === "exact-client") def = entry(name);
  else if (c.match === "alias" && c.rule === "spelling") { use = c.name; def = entry(c.name); notes.push(`Spelling: the vault spells it "${c.name}"; the approved spelling rule resolves "${name}" to it, so "${c.name}" is used.`); }
  else if (c.match === "alias") { use = c.name; def = entry(c.name); notes.push(`Alias: "${name}" is an approved alias of "${c.name}" in the mapping; "${c.name}" is used.`); }
  else if (c.match === "spelling" || c.match === "prefix-drift") notes.push(`Likely spelling issue: the vault has "${c.candidate}"${c.candidates ? ` (also ${c.candidates.slice(1).join(", ")})` : ""}, not "${name}". Check the name with the user before writing; nothing is renamed.`);
  else if (files.length && (near = closeNames(name, [...idx.project.keys(), ...idx.client.keys()])).length) notes.push(`Likely spelling issue: "${name}" is in no shared parameter file of the vault; close names there: ${near.join(", ")}. Check the name with the user; ACE never invents a GUID.`);
  else if (!files.length) notes.push("No shared parameter file in the vault: if the parameter is missing in the model it cannot be created (ACE never invents a GUID).");
  else notes.push(`"${name}" is in none of the vault's shared parameter files: if it is missing in the model it cannot be created (ACE never invents a GUID).`);
  const definition = def ? { name: def.name, guid: def.guid, dataType: def.dataType, group: def.group, file: def.file } : null;
  return { definition, verdict: c.match, use, notes };
}

/** Names one or two letters away from the name (case, spaces and separators ignored), closest first. */
function closeNames(name, names) {
  const n = (s) => String(s).toLowerCase().replace(/[\s_-]+/g, "");
  if (n(name).length < 6) return [];
  return names.map((m) => [m, editDistance(n(m), n(name), 2)]).filter(([m, d]) => m !== name && d <= 2).sort((x, y) => x[1] - y[1]).map(([m]) => m).slice(0, 3);
}

/** Names in the model close to the parameter (case, spaces, or one or two letters): likely duplicates or typos. */
export function modelNearMisses(name, modelNames = []) {
  const n = (s) => String(s).toLowerCase().replace(/[\s_-]+/g, "");
  return modelNames.filter((m) => m !== name && (n(m) === n(name) || closeNames(name, [m]).length));
}

/** Files under a folder (depth 3) whose text mentions the name: rules, IFC settings, requirement lists. -> [relative paths] */
export function mentions(name, root, { exts = /\.(json|txt|csv|xml|md)$/i, max = 2_000_000 } = {}) {
  if (!root || !fs.existsSync(root)) return [];
  const out = [];
  const walk = (d, depth) => {
    let list = [];
    try { list = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of list) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) { if (depth < 3 && !e.name.startsWith(".")) walk(f, depth + 1); continue; }
      if (!exts.test(e.name)) continue;
      try { if (fs.statSync(f).size <= max && fs.readFileSync(f, "utf8").includes(name)) out.push(path.relative(root, f)); } catch { /* unreadable */ }
    }
  };
  walk(root, 0);
  return out;
}

const HOW = {
  "no-definition": "add the parameter to the project shared parameter file in the vault (a person decides the GUID), or bind it in Revit, then run again",
  "view-not-found": "check the view name (list_views)",
  "not-text": "the parameter is not a text parameter: pick a text parameter",
  datatype: "only text parameters are created; bind other types by hand",
  "guid-differs": "the model has this name with another GUID: decide which is right first (parameter_check)",
  "type-parameter": "the parameter is bound as a type parameter: bind it as an instance parameter, or set it per type by hand",
  borrowed: "ask the holder to synchronise and relinquish, then run the dry run again",
  central: "reload latest, then run the dry run again",
  "read-only": "the model is read-only",
};
export const blocksOf = (r) => (r?.blocked ?? []).map((b) => ({ ...b, how: b.how ?? HOW[b.code] }));
/**
 * The blocks, plus the one the server knows best: binding needs categories and none were given (a shared parameter file
 * holds no categories). It replaces the add-in's "the definition names no model category" block.
 */
export function blocksFor(r, { definition, categories } = {}) {
  const list = blocksOf(r);
  if (!(definition && r?.parameter?.bound === false && !categories?.length)) return list;
  const name = r?.parameter?.name ?? definition.name;
  return [...list.filter((b) => !/names no model category/i.test(b.why ?? "")),
    { what: `categories for ${name}`, why: `${name} is not bound in the model, and binding it needs categories: the shared parameter file holds no categories`, how: "give categories: the model categories to bind it to (e.g. Walls, Floors), then run again", code: "categories-required" }];
}
export const planOf = (r) => r?.plan ?? [];
export const planIdentity = (r) => ({ hash: r?.planHash ?? null, steps: planOf(r).map((p) => [p.id, p.kind, p.count ?? null]) });
export const rolledBackOf = (r) => r?.rolledBack === true || r?.verify?.ok === false;

const OUT = { sectionBox: "outside the section box", hidden: "hidden in the view", category: "category hidden in the view", filter: "hidden by a view filter", phase: "phase (not shown in the view's phase)", workset: "workset hidden in the view", other: "other reasons" };
const kindText = { bind: "bind the parameter", "set-in": "set value_in", "set-out": "set value_out" };

/** The reply: view, parameter, counts in / out, why elements are outside, the plan, blocks, self-check, done or would. */
export function planText(r, { model, mode, rolledBack = false, a = {}, extra = [], blocks: given }) {
  const head = mode === "check" ? "check, read-only, nothing changed" : mode === "dry run" ? "dry run" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  const v = r?.view ?? {}, p = r?.parameter ?? {}, c = r?.counts ?? {};
  const lines = [`VIEW MEMBERSHIP FILL in ${model} (${head}): ${p.name ?? a.parameter} = "${a.value_in}" for what view "${v.name ?? a.view}" shows, "${a.value_out}" for the rest.`];
  const blocks = given ?? blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing changed):\n${blocks.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  lines.push(`View: ${v.name ?? a.view}${v.viewType ? ` (${v.viewType})` : ""}${v.sectionBox ? ", section box on" : ""}${v.phase ? `, phase ${v.phase}` : ""}${v.filters?.length ? `, filters ${v.filters.join(", ")}` : ""}.`);
  lines.push(`Parameter: ${p.name ?? a.parameter} ${p.bound === false ? "is NOT bound in the model" : `bound${p.instance === false ? " (type)" : ""}${p.guid ? `, GUID ${p.guid}` : ""}`}${p.notBoundOn?.length ? `; not bound on ${p.notBoundOn.join(", ")}` : ""}.`);
  lines.push(`Elements: ${c.processed ?? "?"} in scope; ${c.in ?? "?"} shown in the view (in), ${c.out ?? "?"} not (out)${c.unchanged ? `, ${c.unchanged} already right` : ""}${c.skipped ? `, ${c.skipped} skipped` : ""}.`);
  const outside = Object.entries(r?.outside ?? {}).filter(([, n]) => n);
  if (outside.length) lines.push(`Why elements are out (first reason found):\n${outside.sort((x, y) => y[1] - x[1]).map(([k, n]) => `- ${OUT[k] ?? k}: ${n}${r?.outsideSamples?.[k]?.length ? ` (e.g. ${r.outsideSamples[k].slice(0, 5).join(", ")})` : ""}`).join("\n")}`);
  const plan = planOf(r);
  lines.push(`Plan: ${plan.length ? plan.map((s) => `${kindText[s.kind] ?? s.kind}${s.count != null ? ` (${s.count})` : ""}`).join(", ") : "nothing to do"}.`);
  if (r?.impact && (r.impact.schedules?.length || r.impact.viewFilters?.length)) lines.push(`In the model it is used by: ${[r.impact.schedules?.length ? `schedules ${r.impact.schedules.join(", ")}` : "", r.impact.viewFilters?.length ? `view filters ${r.impact.viewFilters.join(", ")}` : ""].filter(Boolean).join("; ")}.`);
  lines.push(...extra);
  if (r?.skipped?.length) lines.push(`Skipped: ${c.skipped ?? r.skipped.length} (e.g. ${r.skipped.slice(0, 5).map((s) => `${s.id} ${s.reason}`).join("; ")}).`);
  if (r?.notes?.length) lines.push(`Notes:\n${r.notes.map((n) => `- ${typeof n === "string" ? n : JSON.stringify(n)}`).join("\n")}`);
  if (mode !== "check") {
    const vf = r?.verify;
    lines.push(!vf ? "Self-check: not reported." : vf.ok ? `Self-check: ok (${vf.checked ?? "?"} element(s) read back; in + out = processed).`
      : `Self-check FAILED: ${(vf.mismatches ?? []).slice(0, 5).map((m) => m.id != null ? `#${m.id}: expected ${JSON.stringify(m.expected)}, found ${JSON.stringify(m.actual)}` : `${m.what}${m.expected != null ? `: expected ${m.expected}, found ${m.actual}` : ""}`).join("; ")}; rolled back, nothing changed.`);
    const w = r?.wouldDo ?? {}, d = r?.done ?? {};
    const t = (o) => [o.bound ? "bound 1" : "", o.setIn ? `set in ${o.setIn}` : "", o.setOut ? `set out ${o.setOut}` : ""].filter(Boolean).join(", ") || "nothing";
    if (mode === "dry run" || rolledBack) lines.push(`Changes: would have been ${t(Object.keys(w).length ? w : Object.fromEntries(plan.map((s) => [s.kind === "bind" ? "bound" : s.kind === "set-in" ? "setIn" : "setOut", s.count ?? 1])))}; rolled back, nothing changed.`);
    else lines.push(`Changes: ${t(d)} done.`);
  }
  return lines.join("\n");
}
