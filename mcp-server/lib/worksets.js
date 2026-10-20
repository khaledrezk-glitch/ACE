// Workset administration (assign_worksets actions audit / rename / delete_empty): the audit table with its flags (empty,
// naming, near-duplicate, wrong workset), rename suggestions from the vault's naming rule, the plan text and the audit
// files. Pure functions; index.js asks Revit (bridge command workset_admin) and writes the files.
import { esc } from "./vault.js";
import { toCsv } from "./report.js";
import { blockLine, fileStem, safeName } from "./paramexec.js";
import { brandedPage, chips } from "./interference.js";

export const PROTECTED = "shared levels and grids";
export const isProtected = (name) => String(name ?? "").trim().toLowerCase() === PROTECTED;
/** A name reduced to letters and digits, lower case: "ST_Columns" and "ST-COLUMNS" are the same here. */
export const normName = (n) => String(n ?? "").toLowerCase().replace(/[^a-z0-9]+/g, "");

/**
 * The naming rule from the worksets section of project-requirements.json (generic, optional):
 * naming: { pattern: "<regex>", allowed: [names], rename: [{ from, to }] } ("names" is read as allowed too).
 * -> { pattern: RegExp|null, source, allowed: [...], rename: [...] } or null. A pattern that does not compile is an error.
 */
export function namingOf(section) {
  const n = section?.naming;
  if (!n || typeof n !== "object") return null;
  // categoryTokens only (no pattern / allowed): still a naming object, the names themselves are not checked.
  let pattern = null;
  if (n.pattern != null && String(n.pattern).trim()) {
    try { pattern = new RegExp(String(n.pattern)); } catch (e) { throw new Error(`The workset naming pattern "${n.pattern}" is not a valid regular expression (${e.message}): fix it in project-requirements.json.`); }
  }
  const list = (x) => Array.isArray(x) ? x.map(String).filter((s) => s.trim()) : [];
  const rename = (Array.isArray(n.rename) ? n.rename : []).filter((r) => r && r.from && r.to).map((r) => ({ from: String(r.from), to: String(r.to) }));
  const tokens = n.categoryTokens == null ? undefined : n.categoryTokens;
  if (tokens != null && (typeof tokens !== "object" || Array.isArray(tokens) || Object.values(tokens).some((v) => !Array.isArray(v))))
    throw new Error("naming.categoryTokens must be { \"TOKEN\": [\"Category\", ...] } (e.g. { \"PCC\": [\"Structural Foundations\"] }).");
  // workset_plan: levelAliases { TOKEN: [level names or regex] }, manualOnly [names], defaultName (renaming Workset1 is opt-in).
  const aliases = n.levelAliases == null ? undefined : n.levelAliases;
  if (aliases != null && (typeof aliases !== "object" || Array.isArray(aliases) || Object.values(aliases).some((v) => !Array.isArray(v) && typeof v !== "string")))
    throw new Error("naming.levelAliases must be { \"TOKEN\": [\"level name or regex\", ...] } (e.g. { \"GF\": [\"C-GF(GROUND FLOOR)\"] }).");
  const defaultName = typeof n.defaultName === "string" && n.defaultName.trim() ? n.defaultName.trim() : null;
  const typeTokens = n.typeTokens == null ? undefined : n.typeTokens;
  if (typeTokens != null && (typeof typeTokens !== "object" || Array.isArray(typeTokens) || Object.values(typeTokens).some((v) => typeof v !== "string")))
    throw new Error("naming.typeTokens must be { \"TOKEN\": \"workset\" } (e.g. { \"SOG\": \"XX-ST-SOG\" }).");
  return { pattern, source: n.pattern ?? null, allowed: [...list(n.allowed), ...list(n.names)], rename, ...(tokens ? { categoryTokens: tokens } : {}),
    ...(aliases ? { levelAliases: aliases } : {}), ...(list(n.manualOnly).length ? { manualOnly: list(n.manualOnly) } : {}), ...(defaultName ? { defaultName } : {}),
    ...(typeTokens ? { typeTokens } : {}), ...(n.zoneWins === false ? { zoneWins: false } : {}) };
}

/** Does a name keep the rule: the pattern (when given) and the allowed list (when given). null without a rule. */
export function namingOk(name, naming) {
  if (!naming || isProtected(name)) return null;
  if (naming.pattern && !naming.pattern.test(name)) return false;
  if (naming.allowed.length && !naming.allowed.some((x) => x === name)) return false;
  return true;
}

/** A rename target for a name: the rule's rename list, else an allowed name spelled differently (same letters). */
export function suggestedName(name, naming) {
  if (!naming || isProtected(name)) return null;
  const r = naming.rename.find((x) => x.from === name) ?? naming.rename.find((x) => normName(x.from) === normName(name));
  if (r && r.to !== name) return r.to;
  const a = naming.allowed.find((x) => normName(x) === normName(name) && x !== name);
  return a ?? null;
}

// ---- what a workset's name says it should hold (name token -> categories) ------------------------------------------

const CAD = "CAD imports";   // a category ending .dwg / .dxf / .dgn, or Imports in Families
export const CATEGORY_TOKENS = {
  LINK: ["RVT Links", CAD], LINKS: ["RVT Links", CAD], RVT: ["RVT Links"], CAD: [CAD], DWG: [CAD],
  COLUMN: ["Structural Columns", "Columns"], COLUMNS: ["Structural Columns", "Columns"],
  BEAM: ["Structural Framing"], BEAMS: ["Structural Framing"], FRAMING: ["Structural Framing"],
  WALL: ["Walls"], WALLS: ["Walls"], FLOOR: ["Floors"], FLOORS: ["Floors"], SLAB: ["Floors"], SLABS: ["Floors"],
  FOUNDATION: ["Structural Foundations"], FOUNDATIONS: ["Structural Foundations"],
  GRID: ["Grids"], GRIDS: ["Grids"], GRDS: ["Grids"], LEVEL: ["Levels"], LEVELS: ["Levels"], LVLS: ["Levels"],
  STAIR: ["Stairs"], STAIRS: ["Stairs"], RAMP: ["Ramps"], RAMPS: ["Ramps"], ROOF: ["Roofs"], ROOFS: ["Roofs"],
  DOOR: ["Doors"], DOORS: ["Doors"], WINDOW: ["Windows"], WINDOWS: ["Windows"], CEILING: ["Ceilings"], CEILINGS: ["Ceilings"],
  ROOM: ["Rooms"], ROOMS: ["Rooms"], FURNITURE: ["Furniture"], DUCT: ["Ducts"], DUCTS: ["Ducts"], PIPE: ["Pipes"], PIPES: ["Pipes"],
  PARAPET: ["Walls"], PARAPETS: ["Walls"], FOOTING: ["Structural Foundations"], FOOTINGS: ["Structural Foundations"], PILE: ["Structural Foundations"], PILES: ["Structural Foundations"],
  RAFT: ["Structural Foundations"], SOG: ["Floors"], COL: ["Structural Columns", "Columns"], COLS: ["Structural Columns", "Columns"],
  CABLE: ["Cable Trays"], TRAY: ["Cable Trays"], CONDUIT: ["Conduits"], CONDUITS: ["Conduits"],
};
export const isCad = (cat) => /\.(dwg|dxf|dgn)$/i.test(cat) || /^(imports in families|cad imports)$/i.test(cat);
/** The token table: the built-in one, extended or overridden by naming.categoryTokens { TOKEN: [categories] }. */
export function tokenTable(naming) {
  const t = { ...CATEGORY_TOKENS };
  for (const [k, v] of Object.entries(naming?.categoryTokens ?? {})) if (Array.isArray(v)) t[String(k).toUpperCase()] = v.map(String);
  return t;
}
/** The categories a workset's name says it holds ([] when no token matches). CAD / RVT win over the generic LINKS. */
export function expectedCategories(name, table = CATEGORY_TOKENS) {
  const tokens = new Set(String(name ?? "").toUpperCase().split(/[^A-Z0-9]+/).filter(Boolean));
  if (tokens.has("CAD") || tokens.has("RVT")) { tokens.delete("LINK"); tokens.delete("LINKS"); }
  return [...new Set([...tokens].flatMap((k) => table[k] ?? []))];
}
const matches = (cat, wanted) => wanted.some((w) => w === CAD ? isCad(cat) : String(w).toLowerCase() === String(cat).toLowerCase());
export const isDefaultName = (name) => /^workset1$/i.test(String(name ?? "").trim());
const DATUM = /^(levels|grids|reference planes|scope boxes|matchline)$/i;
const structural = (cat) => /^structural /i.test(cat);

/**
 * The audit rows: the add-in's worksets with flags (the add-in's own flags are kept; the server's are added):
 * empty, naming, near-duplicate, wrong-workset. -> { rows, groups, counts, suggestions }
 */
export function audit(answer, naming) {
  const list = (answer?.worksets ?? []).filter(Boolean);
  const byNorm = new Map();
  for (const w of list) { const k = normName(w.name); byNorm.set(k, [...(byNorm.get(k) ?? []), w.name]); }
  const groups = [...byNorm.values()].filter((g) => g.length > 1);
  const table = tokenTable(naming);
  // Assignment rules: without them the add-in sends wrongWorkset null: "not checked", never 0.
  const rulesChecked = answer?.rules !== null && list.some((w) => typeof w.wrongWorkset === "number");
  const rows = list.map((w) => {
    const flags = new Set((w.flags ?? []).map((f) => f === "near_duplicate" ? "near-duplicate" : f === "wrong_elements" ? "wrong-workset" : f));
    const prot = w.protected ?? isProtected(w.name);
    let expected = null, defaultHolds = null;
    if (!prot && Number(w.elements) === 0) {
      // Empty, but the name says what it should hold: count those elements on other worksets (not on one named for them).
      const want = expectedCategories(w.name, table);
      const where = {};
      if (want.length) for (const o of list) {
        if (o === w || expectedCategories(o.name, table).some((c) => want.includes(c))) continue;
        const n = Object.entries(o.byCategory ?? {}).filter(([c]) => matches(c, want)).reduce((x, [, v]) => x + Number(v), 0);
        if (n) where[o.name] = n;
      }
      const total = Object.values(where).reduce((x, n) => x + n, 0);
      if (total) { flags.add("empty-but-expected"); expected = { elements: total, on: where, categories: want }; }
      else flags.add("empty");
    }
    const cats = Object.entries(w.byCategory ?? {});
    const model = isDefaultName(w.name) ? cats : prot ? cats.filter(([c]) => !DATUM.test(c)) : [];
    const held = model.reduce((x, [, v]) => x + Number(v), 0);
    if (held && (isDefaultName(w.name) || prot)) {
      flags.add("default-workset");
      defaultHolds = { elements: held, structural: model.filter(([c]) => structural(c)).reduce((x, [, v]) => x + Number(v), 0), top: model.sort((x, y) => y[1] - x[1]).slice(0, 5) };
    }
    const ok = w.namingOk ?? namingOk(w.name, naming);
    if (ok === false) flags.add("naming");
    const dup = groups.find((g) => g.includes(w.name));
    if (dup) flags.add("near-duplicate");
    if (Number(w.wrongWorkset) > 0) flags.add("wrong-workset");
    return { ...w, wrongWorkset: rulesChecked ? w.wrongWorkset : NOT_CHECKED, expected, defaultHolds, protected: prot, flags: [...flags], nearDuplicates: dup ? dup.filter((x) => x !== w.name) : [], suggestedName: w.suggestedName ?? suggestedName(w.name, naming) };
  });
  const count = (f) => rows.filter((r) => r.flags.includes(f)).length;
  const counts = { worksets: rows.length, empty: count("empty"), emptyButExpected: count("empty-but-expected"), naming: count("naming"), nearDuplicateGroups: groups.length,
    wrongWorkset: rulesChecked ? count("wrong-workset") : NOT_CHECKED, defaultWorkset: count("default-workset") };
  // A target that exists already (Revit compares names case-insensitively) is a merge, not a rename; a case-only fix of itself is fine.
  const suggestions = rows.filter((r) => r.suggestedName && !rows.some((o) => o !== r && String(o.name).toLowerCase() === String(r.suggestedName).toLowerCase()))
    .map((r) => ({ from: r.name, to: r.suggestedName, why: r.flags.includes("naming") ? "breaks the naming rule" : "near-duplicate of an allowed name" }));
  return { rows, groups, counts, suggestions };
}

export const NOT_CHECKED = "not checked: no rules";
/** A workset name quoted: names may hold commas ("Shared Views, Levels, Grids"), so a list never splits one. */
export const q = (n) => `"${n}"`;
/** Why a workset is flagged, in words, for the empty-but-expected and default-workset flags. */
export const expectedText = (r) => `empty, but ${r.expected.elements} element(s) look like they belong here (${Object.entries(r.expected.on).map(([k, n]) => `${n} on ${q(k)}`).join(", ")})`;
export const defaultText = (r) => `default workset holds ${r.defaultHolds.elements} element(s) (${r.defaultHolds.structural} structural); assign them by rules. Top: ${r.defaultHolds.top.map(([k, v]) => `${k} ${v}`).join(", ")}`;
const top = (byCategory, n = 3) => Object.entries(byCategory ?? {}).sort((x, y) => y[1] - x[1]).slice(0, n).map(([k, v]) => `${k} ${v}`).join(", ");
const yesNo = (v) => v == null ? "?" : v ? "yes" : "no";

/** The audit as a reviewable text table plus what to do next. */
export function auditText(model, a, naming) {
  const wrong = a.counts.wrongWorkset === NOT_CHECKED ? `elements on the wrong workset ${NOT_CHECKED}` : `${a.counts.wrongWorkset} with elements on the wrong workset`;
  const lines = [`WORKSET AUDIT ${model} (read-only, nothing changed): ${a.counts.worksets} user workset(s); ${a.counts.empty} empty, ${a.counts.emptyButExpected ? `${a.counts.emptyButExpected} empty but expected to hold elements, ` : ""}${a.counts.naming} breaking the naming rule, ${a.counts.nearDuplicateGroups} near-duplicate group(s), ${wrong}.`,
    naming ? `Naming rule: ${[naming.source ? `pattern ${naming.source}` : "", naming.allowed.length ? `${naming.allowed.length} allowed name(s)` : "", naming.rename.length ? `${naming.rename.length} rename(s)` : ""].filter(Boolean).join("; ") || "empty"}.` : "Naming rule: none in the worksets section of project-requirements.json (naming: { pattern, allowed }); names are not checked.",
    "workset | elements | top categories | owner | open | flags"];
  for (const r of a.rows) lines.push(`${r.name} | ${r.elements ?? "?"} | ${top(r.byCategory) || "-"} | ${r.owner || "-"} | ${yesNo(r.open)} | ${r.flags.join(", ") || "ok"}${r.protected ? " (protected)" : ""}${r.suggestedName ? ` -> ${r.suggestedName}?` : ""}`);
  for (const r of a.rows.filter((x) => x.expected)) lines.push(`${r.name}: ${expectedText(r)}. Move them here (assign_worksets rules) rather than delete it.`);
  for (const r of a.rows.filter((x) => x.defaultHolds)) lines.push(`${r.name}: ${defaultText(r)}.`);
  if (a.groups.length) lines.push(`Near-duplicates: ${a.groups.map((g) => g.map(q).join(" / ")).join("; ")}.`);
  if (a.suggestions.length) lines.push(`Rename suggestions (none applied): ${a.suggestions.map((s) => `${q(s.from)} -> ${q(s.to)} (${s.why})`).join("; ")}. To rename: assign_worksets action rename with these renames, dry_run: true first.`);
  if (a.counts.empty) lines.push(`Empty worksets can go with action delete_empty (dry_run: true first); a workset that still holds elements is never deleted unless the user names move_to${a.counts.emptyButExpected ? "; empty-but-expected ones are kept unless named" : ""}.`);
  return lines.join("\n");
}

const HEAD = ["workset", "elements", "categories", "owner", "open", "editable", "flags", "near-duplicates", "suggested name", "wrong workset", "id"];
const row = (r) => [r.name, r.elements, Object.entries(r.byCategory ?? {}).map(([k, v]) => `${k} ${v}`).join("; "), r.owner, yesNo(r.open), yesNo(r.editable),
  [r.flags.join(", "), r.expected ? expectedText(r) : "", r.defaultHolds ? defaultText(r) : ""].filter(Boolean).join("; "), r.nearDuplicates.join(" | "), r.suggestedName, r.wrongWorkset, r.id];
export const auditCsv = (a) => toCsv([HEAD, ...a.rows.map(row)]);
export function auditHtml(model, a, { when, naming, brand = {} } = {}) {
  const body = `<tbody>${a.rows.map((r) => `<tr><td>${esc(r.name)}${r.protected ? ' <span class="n">(protected)</span>' : ""}</td><td class="num">${esc(r.elements ?? "?")}</td><td>${esc(top(r.byCategory, 5) || "-")}</td>` +
    `<td>${esc(r.owner || "-")}</td><td>${yesNo(r.open)}</td><td>${r.flags.map((f) => `<span class="flag">${esc(f)}</span>`).join(" ") || "ok"}${r.expected ? `<br>${esc(expectedText(r))}` : ""}${r.defaultHolds ? `<br>${esc(defaultText(r))}` : ""}</td><td>${esc(r.suggestedName ?? "")}</td></tr>`).join("")}</tbody>`;
  return brandedPage({ title: `Workset audit - ${model}`, heading: "Workset Audit", brand,
    meta: [["Model", esc(model)], ["Date", esc(when)], ["Naming rule", esc(naming ? naming.source ?? `${naming.allowed.length} allowed name(s)` : "none")],
      ["Worksets", String(a.counts.worksets)], ["Flags", chips({ empty: a.counts.empty, "empty but expected": a.counts.emptyButExpected, naming: a.counts.naming, "near-duplicate groups": a.counts.nearDuplicateGroups, "default workset": a.counts.defaultWorkset, ...(a.counts.wrongWorkset === NOT_CHECKED ? {} : { "wrong workset": a.counts.wrongWorkset }) })],
      ["Wrong workset", a.counts.wrongWorkset === NOT_CHECKED ? NOT_CHECKED : String(a.counts.wrongWorkset)]],
    head: ["Workset", "#Elements", "Top categories", "Owner", "Open", "Flags", "Suggested name"], body: a.rows.length ? body : "", empty: "No user worksets",
    footer: `Generated by ACE (read-only audit of ${esc(model)}). Nothing was changed; renames and deletions need their own preview and approval.` });
}
export const auditStem = (model, stamp) => fileStem("worksets-audit", safeName(model), stamp);

// ---- rename / delete_empty plans ------------------------------------------------------------------------------------

/** The plan of an answer: its plan list, or one built from first-proposal fields (renames / delete / move). */
export function planOf(r) {
  if (Array.isArray(r?.plan)) return r.plan;
  return [...(r?.renames ?? []).filter((x) => x.ok !== false).map((x) => ({ id: `rename:${x.from}`, kind: "rename", workset: x.from, from: x.from, to: x.to })),
    ...(r?.delete ?? []).map((x) => ({ id: `delete:${x.name}`, kind: "delete", workset: x.name, elements: x.elements ?? 0 })),
    ...(r?.move ?? []).map((x) => ({ id: `move-and-delete:${x.name}`, kind: "move-and-delete", workset: x.name, elements: x.elements, byCategory: x.byCategory, moveTo: x.moveTo }))];
}
/** Blocks of an answer: blocked, plus first-proposal refused items. */
export const blocksOf = (r) => [...(r?.blocked ?? []), ...(r?.refused ?? []).map((x) => ({ what: x.name ?? x.from ?? "?", why: x.why, code: x.code, holder: x.holder, how: HOW[x.code] }))]
  .map((b) => ({ ...b, how: b.how ?? HOW[b.code] }));
/** Worksets kept (not deleted: they hold elements and no move_to was named). */
export const keptOf = (r) => r?.kept ?? [];
const HOW = {
  system: "Shared Levels and Grids and the system worksets are never renamed or deleted",
  protected: "Shared Levels and Grids and the system worksets are never renamed or deleted",
  "not-empty": "name a target with move_to (the elements move there first), or leave the workset",
  borrowed: "ask the holder to synchronise and relinquish, then run the dry run again",
  exists: "pick another name, or merge by moving the elements (assign_worksets) and deleting the empty one",
  "not-found": "check the name with assign_worksets action audit",
  "not-workshared": "the model has no worksets",
  naming: "pick a name the project's naming rule allows (project-requirements.json worksets.naming)",
  closed: "open the workset (Manage Worksets) so its elements can be counted, then run again",
  central: "reload latest, then run the dry run again",
  "plan-changed": "the model changed since the plan: propose and confirm again (or run the dry run again)",
};
export const blockWithHow = (b) => ({ ...b, how: b.how ?? HOW[b.code] });

export const planIdentity = (r) => ({ hash: r?.planHash ?? null, steps: planOf(r).map((p) => [p.id, p.kind, p.workset, p.to ?? null, p.moveTo ?? null]) });
const stepText = (p) => p.kind === "rename" ? `rename ${p.from ?? p.workset} -> ${p.to}` : p.kind === "move-and-delete" ? `move ${p.elements ?? "?"} element(s) of ${p.workset} to ${p.moveTo}${top(p.byCategory) ? ` (${top(p.byCategory)})` : ""}, then delete ${p.workset}` : `delete ${p.workset} (${p.elements ?? 0} elements${p.safetyTarget ? `; anything unseen moves to ${p.safetyTarget}, never deleted` : ""})`;

export const rolledBackOf = (r) => r?.rolledBack === true || r?.verify?.ok === false;

/** The reply for rename / delete_empty: plan, kept, blocks, self-check, done or would have been. */
export function planText(r, { action, model, mode, rolledBack = false }) {
  const plan = planOf(r);
  const head = mode === "check" ? "check, read-only, nothing changed" : mode === "dry run" ? "dry run" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  const lines = [`WORKSETS ${action === "rename" ? "RENAME" : "DELETE EMPTY"} in ${model} (${head}).`];
  const blocks = blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing changed):\n${blocks.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  lines.push(`Plan: ${plan.length} step(s)${plan.length ? `:\n${plan.map((p) => `- ${stepText(p)}`).join("\n")}` : "."}`);
  const kept = keptOf(r);
  if (kept.length) lines.push(`Kept (not deleted, it holds elements; name move_to to move them first):\n${kept.map((k) => `- ${k.name}: ${k.elements} element(s)${top(k.byCategory) ? ` (${top(k.byCategory)})` : ""}${k.why ? `; ${k.why}` : ""}`).join("\n")}`);
  if (r?.notes?.length) lines.push(`Notes:\n${r.notes.map((n) => `- ${typeof n === "string" ? n : n.text ?? JSON.stringify(n)}`).join("\n")}`);
  if (r?.notPreviewed?.length) lines.push(`Not run in this dry run (the workset is not yours yet; the apply borrows it first, then runs these): ${r.notPreviewed.join("; ")}`);
  if (mode !== "check") {
    const v = r?.verify;
    lines.push(!v ? "Self-check: not reported." : v.ok ? `Self-check: ok (element count per category the same before and after${v.checked != null ? `, ${v.checked} checked` : ""}; only the workset changed).`
      : `Self-check FAILED: ${(v.mismatches ?? v.categories ?? []).slice(0, 5).map((m) => `${m.what ?? m.category}: ${m.before} -> ${m.after}`).join("; ")}; rolled back, nothing changed.`);
    const n = (k) => plan.filter((p) => p.kind === k).length;
    const planned = [n("rename") ? `rename ${n("rename")}` : "", n("delete") ? `delete ${n("delete")}` : "", n("move-and-delete") ? `move and delete ${n("move-and-delete")}` : ""].filter(Boolean).join(", ") || "nothing";
    if (mode === "dry run" || rolledBack) lines.push(`Changes: would have been ${planned}; rolled back, nothing changed.`);
    else { const d = r?.done ?? {}; lines.push(`Changes: ${[d.renamed ? `renamed ${d.renamed}` : "", d.moved ? `moved ${d.moved} element(s)` : "", d.deleted ? `deleted ${d.deleted}` : ""].filter(Boolean).join(", ") || "nothing"} done.`); }
  }
  return lines.join("\n");
}
