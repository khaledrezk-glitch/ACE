// workset_plan: propose a workset list (one row per target workset, what it should hold read from its NAME), with the
// element groups as detail, let the user edit it (CSV / xlsx), read it back as rules and a plan per model, save the rules
// to the vault. Pure functions; index.js asks Revit (workset_admin facts / apply_plan) and writes the files.
import crypto from "node:crypto";
import fs from "node:fs";
import ExcelJS from "exceljs";
import { parseCsv } from "./paramlist.js";
import { toCsv } from "./report.js";
import { blockLine } from "./paramexec.js";
import * as W from "./worksets.js";

export const DECISION = "Your decision (OK / change to ... / skip)";
/** The rule-level sheet: one row per target workset (spelling twins merged). Rule is the parsed name (JSON, hidden). */
export const RULE_COLUMNS = ["Group", "Proposed workset", "Action", "Models", "What it holds (from its name)", "Holds now", "Would receive", "Open / visibility", "Note", DECISION, "Rule"];
/** The detail sheet: one row per element group that moves (or needs review). */
export const COLUMNS = ["Model", "Row", "Current workset", "Category", "Type / link / contents", "Level", "Elements", "Proposed workset", "Proposed action", "Reason", "Confidence", DECISION, "Key"];

const up = (s) => String(s ?? "").toUpperCase();
const words = (s) => up(s).split(/[^A-Z0-9]+/).filter(Boolean);
const num = (t) => /^\d+$/.test(t) ? String(Number(t)) : t;
const norm = (s) => String(s ?? "").trim().toLowerCase().replace(/\s+/g, " ");
const q = W.q;
/** A model title compared loosely: case, .rvt and a detached copy's suffix do not matter. */
export const modelKey = (m) => norm(m).replace(/\.rvt$/, "").replace(/_detached$/, "");
/** "Family : Type" and the prototype's "Family | Type" are the same here. */
const typeKey = (t) => norm(t).replace(/\s*[:|]\s*/g, " : ");
export const groupKey = (g) => [g.workset, g.category, g.type, g.level, g.material, g.zone, g.option].map((x) => x ?? "").join("|");
/** Spelling twins: case, spaces, "-" / "_" / " ", and plurals (COLUMN / COLUMNS) do not matter. */
export const twinKey = (n) => words(n).map((w) => w.length > 3 && w.endsWith("S") && !w.endsWith("SS") ? w.slice(0, -1) : w).join(" ");

export const MATERIAL_TOKENS = { STEEL: "steel", STL: "steel", MS: "steel", METAL: "steel", CONCRETE: "concrete", CONC: "concrete", RC: "concrete", RCC: "concrete", PCC: "concrete", PRECAST: "concrete" };
/** The add-in sends concrete | steel | other; "other" (timber, masonry, links, imports) is no evidence either way. */
const known = (m) => m && m !== "other";
const LEVEL_ALIASES = { GF: ["GROUND"], FF: ["FIRST"], SF: ["SECOND"], TF: ["THIRD"], RF: ["ROOF"], FN: ["FOUNDATION", "FOUNDATIONS", "FDN"], BASE: ["BASEMENT"], B: ["BASEMENT"] };
const LEVEL_NOISE = new Set(["FLOOR", "LEVEL", "LVL", "L", "STOREY", "STORY"]);
const STOP = new Set(["AND", "THE", "OF", "WORKSET", "ALL", "NEW", "OLD", "MODEL", "ELEMENTS", "IN"]);
const MANUAL = new Set(["HIDE", "HIDDEN", "TEMP", "TEMPORARY"]);
const DATUM = /^(levels|grids|reference planes|scope boxes|matchline)$/i;
const LINK_WORDS = new Set(["LINK", "LINKS", "RVT", "CAD", "DWG", "IMPORT", "IMPORTS"]);
/** Workset-plan words beyond the audit's table (stairs and their parts). */
const EXTRA_TOKENS = { STAIR: ["Stairs", "Runs", "Landings", "Stair Runs", "Stair Landings"], STAIRS: ["Stairs", "Runs", "Landings", "Stair Runs", "Stair Landings"] };

/** Each level's words (minus generic ones), numbers normalised, plus abbreviations it implies (GROUND -> GF). */
export function levelTokens(levels) {
  const seen = new Set();
  return (levels ?? []).map((l) => typeof l === "string" ? l : l?.name).filter((n) => n && !seen.has(n) && seen.add(n)).map((name) => {
    const set = new Set(words(name).filter((t) => t.length > 1 && !LEVEL_NOISE.has(t)).map(num));
    for (const [abbr, al] of Object.entries(LEVEL_ALIASES)) if (al.some((x) => set.has(x)) || set.has(abbr)) { set.add(abbr); al.forEach((x) => set.add(x)); }
    return { name, tokens: set };
  });
}

/** Tokens shared by most workset names (a project prefix such as "XX-ST"): noise for matching. */
export function commonTokens(names) {
  const n = names.length, count = new Map();
  for (const nm of names) for (const t of new Set(words(nm))) count.set(t, (count.get(t) ?? 0) + 1);
  return new Set(n >= 3 ? [...count].filter(([, c]) => c / n > 0.5).map(([t]) => t) : []);
}
/** The project prefix of the names ("XX-ST-"), when most names start with it; "" otherwise. */
export function commonPrefix(names) {
  const count = new Map();
  for (const n of names) for (let i = 0; i < n.length; i++) if (/[-_ ]/.test(n[i]) && i > 0) count.set(n.slice(0, i + 1), (count.get(n.slice(0, i + 1)) ?? 0) + 1);
  return [...count].filter(([, c]) => c / Math.max(1, names.length) > 0.5).sort((a, b) => b[0].length - a[0].length)[0]?.[0] ?? "";
}

/**
 * A workset name as a rule: the categories, levels, material, link / CAD and manual (visibility) words it carries, and the
 * remaining words (zone or asset). A word that names a level AND a category (ROOF) counts as the level only.
 */
export function parseName(name, { levels = [], table = W.CATEGORY_TOKENS, noise = new Set(), manualOnly = [], isProtected = false } = {}) {
  const t = { ...table, ...EXTRA_TOKENS };
  const out = { workset: name, categories: [], levels: [], words: [], material: null, links: false, cad: false, codes: [],
    manual: manualOnly.some((m) => norm(m) === norm(name)), isDefault: W.isDefaultName(name), protected: !!isProtected };
  for (const w of words(name)) {
    if (MANUAL.has(w)) out.manual = true;
    if (noise.has(w)) continue;
    if (LINK_WORDS.has(w)) { if (w === "CAD" || w === "DWG") out.cad = true; else out.links = true; continue; }
    if (MATERIAL_TOKENS[w]) { out.material = MATERIAL_TOKENS[w]; continue; }
    const lv = levels.filter((l) => l.tokens.has(num(w))).map((l) => l.name);
    if (lv.length) { lv.forEach((l) => out.levels.includes(l) || out.levels.push(l)); continue; }
    if (t[w]) { t[w].forEach((c) => out.categories.includes(c) || out.categories.push(c)); continue; }
    if (w.length >= 2 && w.length <= 3 && !STOP.has(w)) out.codes.push(w);   // a discipline code (EL, MV, PL ...) for links
    if (w.length >= 3 && !STOP.has(w) && !MANUAL.has(w) && !/^\d+$/.test(w)) out.words.push(w);
  }
  if (out.cad) out.links = false;
  return out;
}

const catIs = (cat, list) => list.some((c) => norm(c) === norm(cat)) || (W.isCad(cat) && list.some((c) => /^cad imports$/i.test(c)));
const wordsIn = (s) => new Set(words(s));
const usableRule = (r) => !r.manual && !r.isDefault && !r.protected;
/** Where a group is now counts as default (Workset1, or the datum workset for a non-datum element). */
const onDefault = (g, protectedNames) => W.isDefaultName(g.workset) || (protectedNames.has(norm(g.workset)) && !DATUM.test(g.category));

/** Does the workset's name contradict the group (another category, another level, another material)? */
export function contradicts(g, r) {
  if (!r || r.manual || r.isDefault || r.protected || r.links || r.cad) return false;
  if (r.categories.length && !catIs(g.category, r.categories)) return true;
  if (r.material && known(g.material) && r.material !== g.material) return true;
  const have = new Set([...wordsIn(g.zone), ...wordsIn(g.type)]);
  return !!(r.levels.length && g.level && !r.levels.includes(g.level) && !r.words.some((w) => have.has(w)));
}

/**
 * The home of a group among the rules (parsed workset names): { workset|null, confidence: high|medium|low, reason }.
 * Links go by the discipline code in their name before a generic links workset; CAD imports to a CAD (else LINKS)
 * workset. Otherwise the category token decides (a group never goes to a workset naming another category; with no
 * workset for its category only category-free names compete), then level and zone narrow (a name with a category and no
 * level is the catch-all for levels without their own workset), a material mismatch excludes, and where like elements
 * already sit only breaks a tie. Manual (visibility) worksets never receive anything.
 */
export function place(g, rules, groups = []) {
  const cands = rules.filter(usableRule);
  if (/^rvt links$/i.test(g.category)) {
    const lw = cands.filter((r) => r.links);
    const code = new Set(words(g.type));
    const hit = lw.filter((r) => r.codes.some((c) => code.has(c)));
    if (hit.length === 1) return { workset: hit[0].workset, confidence: "high", reason: `link, discipline code ${hit[0].codes.find((c) => code.has(c))}` };
    const plain = lw.filter((r) => !r.codes.length);
    if (!hit.length && plain.length === 1) return { workset: plain[0].workset, confidence: "medium", reason: "link without its own discipline workset: the generic links workset" };
    return { workset: null, confidence: "low", reason: hit.length ? `link: ${hit.length} worksets carry its code` : lw.length ? "link: no workset carries its discipline code and no single generic links workset" : "link: no links workset" };
  }
  if (W.isCad(g.category)) {
    const cw = cands.filter((r) => r.cad), lw = cands.filter((r) => r.links && !r.codes.length);
    const pick = cw.length ? cw : lw;
    if (pick.length === 1) return { workset: pick[0].workset, confidence: cw.length ? "high" : "medium", reason: cw.length ? "CAD import: the CAD workset" : "CAD import: the links workset" };
    return { workset: null, confidence: "low", reason: pick.length ? "CAD import: several CAD / links worksets" : "CAD import: no CAD or links workset" };
  }
  const plainRules = cands.filter((r) => !r.links && !r.cad);
  const byCat = plainRules.filter((r) => r.categories.length && catIs(g.category, r.categories));
  let pool = byCat.length ? byCat : plainRules.filter((r) => !r.categories.length && (r.levels.length || r.words.length || r.material));
  pool = pool.filter((r) => !(r.material && known(g.material) && r.material !== g.material));
  const have = new Set([...wordsIn(g.zone), ...wordsIn(g.type)]);
  const like = groups.filter((o) => o !== g && o.category === g.category && o.level === g.level && o.type === g.type);
  const likeAll = like.reduce((n, o) => n + Number(o.elements), 0);
  const share = (ws) => likeAll ? like.filter((o) => twinKey(o.workset) === twinKey(ws)).reduce((n, o) => n + Number(o.elements), 0) / likeAll : 0;
  const scored = pool.map((r) => {
    const hit = r.words.filter((w) => have.has(w));
    const lv = r.levels.length ? (g.level && r.levels.includes(g.level) ? 1 : -1) : 0;
    if (lv < 0 && !hit.length) return null;                     // it names another level
    if (!byCat.length && !hit.length && !(lv > 0)) return null;  // a category-free name must match the zone or the level
    let s = 0;
    const why = [];
    if (lv > 0) { s += 4; why.push(`level ${g.level}`); }
    if (hit.length) { s += 3 * Math.min(2, hit.length); why.push(`${g.zone && hit.some((w) => wordsIn(g.zone).has(w)) ? "zone" : "name"} ${hit.join(" ").toLowerCase()}`); }
    else if (r.words.length) s -= 2;
    if (r.material && known(g.material)) { s += 4; why.push(`material ${g.material}`); }
    if (!r.levels.length && !r.words.length && !r.material) { s += 1; why.push("catch-all for its category"); }
    return { r, s, why, total: s + 0.9 * share(r.workset) };
  }).filter(Boolean).sort((a, b) => b.total - a.total);
  const lead = byCat.length ? [`category ${g.category}`] : [];
  if (!scored.length) return { workset: null, confidence: "low", reason: byCat.length ? `no ${g.category} workset fits its level${g.level ? ` ${g.level}` : ""} (and none without a level)` : `no workset names ${g.category}${pool.length ? " and no zone / level matches" : ""}` };
  const [best, second] = scored;
  if (second && second.total === best.total) return { workset: null, confidence: "low", reason: `tie: ${q(best.r.workset)} and ${q(second.r.workset)}` };
  const tie = second && second.s === best.s ? `; tie broken by where like elements sit (${Math.round(share(best.r.workset) * 100)}%)` : "";
  const confidence = !byCat.length ? (best.s >= 4 ? "medium" : "low") : !second || best.s - second.s >= 3 ? "high" : "medium";
  if (confidence === "low") return { workset: null, confidence, reason: `weak match only: ${[...lead, ...best.why].join(" + ")} on ${q(best.r.workset)}` };
  return { workset: best.r.workset, confidence, reason: `${[...lead, ...best.why].join(" + ")}${tie}` };
}

/** A hash of a model's facts (groups and counts, worksets): two files with the same content propose once. */
export const factsHash = (f) => crypto.createHash("sha1").update(JSON.stringify([
  (f?.groups ?? []).map((g) => [groupKey(g), Number(g.elements)]).sort((a, b) => String(a[0]).localeCompare(String(b[0]))),
  (f?.worksets ?? []).map((w) => [w.name, Number(w.elements ?? 0)]).sort((a, b) => String(a[0]).localeCompare(String(b[0])))])).digest("hex").slice(0, 16);

const isProt = (w) => w?.protected === true || (w?.protected == null && W.isProtected(w?.name));

/** The visibility of a workset in a model: open, visible by default, per export view. */
const visOf = (w) => w ? { open: w.open, byDefault: w.visibleByDefault, views: w.views ?? {} } : null;
const visText = (vs) => {
  const list = vs.filter(Boolean);
  if (!list.length) return "";
  const one = (k, f) => { const v = [...new Set(list.map(f).filter((x) => x != null))]; return v.length === 1 ? (v[0] ? k[0] : k[1]) : v.length ? k[2] : ""; };
  const views = [...new Set(list.flatMap((v) => Object.keys(v.views)))].map((n) => { const v = [...new Set(list.map((x) => x.views[n]).filter((x) => x != null))]; return `${n}: ${v.length === 1 ? (v[0] ? "visible" : "HIDDEN") : "varies"}`; });
  return [one(["open", "closed", "open in some models"], (v) => v.open), one(["visible by default", "hidden by default", "default visibility varies"], (v) => v.byDefault), ...views].filter(Boolean).join("; ");
};

/** What a workset should hold, in words, from its name. */
export function describe(r) {
  if (r.protected) return "Levels and grids (the datum workset); levels and grids no other workset takes.";
  if (r.isDefault) return "Non-model items only (legends, zones, lines); model elements and links move out by the rules below.";
  if (r.manual) return "Visibility workset: assigned by hand only, never by rules.";
  if (r.links) return r.codes.length ? `RVT links whose name carries ${r.codes.join(" / ")}.` : "RVT links without a workset of their own discipline.";
  if (r.cad) return "CAD imports (DWG / DXF / DGN).";
  const cats = r.categories.length ? [...new Set(r.categories.filter((c) => !/^(columns|stair runs|stair landings|runs|landings)$/i.test(c) || r.categories.length === 1))].join(", ") : "";
  const parts = [cats || "Elements whose category has no workset of its own",
    r.levels.length ? `at level ${r.levels.join(" / ")}` : cats ? "on levels without their own workset (catch-all)" : "",
    r.words.length ? `${cats ? "in" : "matching"} zone / name ${r.words.join(" ").toLowerCase()}` : "",
    r.material ? `${r.material} only` : ""].filter(Boolean);
  return !cats && !r.levels.length && !r.words.length && !r.material ? "Its name says nothing ACE can match (no category, level, zone or material): review." : `${parts.join(", ")}.`;
}
const section = (r, empty) => r.protected || r.isDefault ? "General" : r.manual ? "Visibility" : r.links || r.cad ? "Links"
  : r.categories.some((c) => /^(levels|grids)$/i.test(c)) ? "Levels and grids" : r.material === "steel" ? "Steel" : empty ? "Delete"
  : r.categories.length && r.levels.length ? "By level" : r.words.length ? "Zones and assets" : r.categories.length ? "By category" : "Review";

/**
 * The proposal across models: twins (same facts hash) proposed once; worksets across models merged by spelling (one
 * canonical name, renames listed); one rule row per target workset with what it holds, how many elements it holds and
 * would receive, visibility; the moving groups as detail. -> { rules: [RULE_COLUMNS rows], detail: [COLUMNS rows], models, counts }
 */
export function propose(list) {
  const models = [], byHash = new Map();
  for (const { facts, naming } of list) {
    const hash = factsHash(facts), same = byHash.get(hash);
    models.push({ model: facts?.model ?? "model", facts, naming, hash, sameAs: same ?? null });
    if (!same) byHash.set(hash, facts?.model ?? "model");
  }
  const uniq = models.filter((m) => !m.sameAs);
  const naming = uniq[0]?.naming ?? null;
  const table = W.tokenTable(naming);
  // Worksets across models, by spelling twin; the canonical name is the most used spelling (trimmed).
  const fam = new Map();
  for (const m of uniq) for (const w of m.facts?.worksets ?? []) {
    const k = isProt(w) ? "\u0000datum" : twinKey(w.name) || w.name;
    const f = fam.get(k) ?? { names: new Map(), protected: false, vis: [] };
    const e = f.names.get(w.name) ?? { models: new Set(), elements: 0 };
    e.models.add(m.model); e.elements += Number(w.elements ?? 0);
    f.names.set(w.name, e); f.protected ||= isProt(w); f.vis.push(visOf(w));
    fam.set(k, f);
  }
  for (const f of fam.values()) {
    const ranked = [...f.names].sort((a, b) => b[1].elements - a[1].elements || b[1].models.size - a[1].models.size || (a[0] === a[0].trim() ? -1 : 1));
    f.canonical = f.protected ? ranked[0][0] : ranked[0][0].trim().replace(/\s+/g, " ");
  }
  const canonOf = (name, w) => (isProt(w ?? { name }) ? fam.get("\u0000datum") : fam.get(twinKey(name) || name))?.canonical ?? name;
  const levels = levelTokens(uniq.flatMap((m) => m.facts?.levels ?? []));
  const names = [...fam.values()].map((f) => f.canonical);
  const noise = commonTokens(names);
  const manualOnly = naming?.manualOnly ?? [];
  const rules = [...fam.values()].map((f) => ({ ...parseName(f.canonical, { levels, table, noise, manualOnly, isProtected: f.protected }), fam: f }));
  const detail = [], receives = new Map(), hides = new Map();
  for (const m of uniq) {
    const groups = (m.facts?.groups ?? []).filter((g) => g && Number(g.elements) > 0).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
    const wsByName = new Map((m.facts?.worksets ?? []).map((w) => [w.name, w]));
    const protectedNames = new Set((m.facts?.worksets ?? []).filter(isProt).map((w) => norm(w.name)));
    for (const g of groups) {
      if (DATUM.test(g.category)) continue;
      const cur = rules.find((r) => r.workset === canonOf(g.workset, wsByName.get(g.workset)));
      if (cur?.manual) continue;
      const dflt = onDefault(g, protectedNames), wrong = !dflt && contradicts(g, cur);
      if (!dflt && !wrong) continue;
      const pl = place(g, rules, groups);
      if (pl.workset && pl.workset === cur?.workset) continue;
      // Visibility: a move from a workset shown in an export view to one hidden there hides the elements.
      const from = wsByName.get(g.workset), to = pl.workset ? [...wsByName.values()].find((w) => canonOf(w.name, w) === pl.workset) : null;
      const hidden = from && to ? Object.entries(from.views ?? {}).filter(([v, on]) => on && to.views?.[v] === false).map(([v]) => v) : [];
      if (pl.workset) receives.set(pl.workset, (receives.get(pl.workset) ?? 0) + Number(g.elements));
      if (pl.workset && hidden.length) hides.set(pl.workset, [...(hides.get(pl.workset) ?? []), ...hidden.map((v) => ({ v, n: Number(g.elements) }))]);
      detail.push({ Model: m.model, Row: "MOVE", "Current workset": g.workset, Category: g.category, "Type / link / contents": g.type ?? "", Level: g.level ?? "", Elements: Number(g.elements),
        "Proposed workset": pl.workset ?? "", "Proposed action": pl.workset ? "move" : "review",
        Reason: `${dflt ? "on the default workset" : "its workset's name contradicts it"}; ${pl.reason}${known(g.material) ? ` (material ${g.material})` : ""}${hidden.length ? `; WOULD HIDE ${g.elements} element(s) in ${hidden.map(q).join(", ")}` : ""}${g.borrowed ? `; ${g.borrowed} borrowed by ${(g.holders ?? []).join(", ") || "others"} (skipped at apply)` : ""}`,
        Confidence: pl.confidence, [DECISION]: "", Key: g.key });
    }
  }
  const prefix = commonPrefix(names.filter((n) => !W.isDefaultName(n)));
  const all = uniq.map((m) => m.model);
  const ruleRows = rules.map((r) => {
    const f = r.fam;
    const where = [...new Set([...f.names.values()].flatMap((e) => [...e.models]))];
    const holds = [...f.names.values()].reduce((n, e) => n + e.elements, 0);
    const recv = receives.get(r.workset) ?? 0;
    const twins = [...f.names.keys()].filter((n) => n !== r.workset);
    const inModel = (mm) => (uniq.find((x) => x.model === mm)?.facts?.worksets ?? []).filter((w) => f.names.has(w.name));
    const emptyIn = where.filter((mm) => inModel(mm).every((w) => !Number(w.elements)));
    const expected = r.cad || r.links || r.categories.length ? uniq.some((m) => (m.facts?.groups ?? []).some((g) => (r.cad && W.isCad(g.category)) || (r.links && /^rvt links$/i.test(g.category)))) : false;
    let action = "keep", note = [];
    const defaultName = naming ? W.suggestedName("Workset1", naming) : null;
    if (r.protected) action = "keep (the datum workset: never renamed or deleted)";
    else if (r.isDefault) action = `rename from ${r.workset}`;
    else if (r.manual) action = "keep (manual only)";
    else if (!holds && !recv && !expected) action = "delete";
    else if (emptyIn.length && emptyIn.length < where.length && !recv && !expected) action = "keep where used, delete where empty";
    if (twins.length && !r.protected) note.push(`merge spelling twins into it: ${twins.map(q).join(", ")}`);
    if (r.workset !== [...f.names.keys()].sort((a, b) => f.names.get(b).elements - f.names.get(a).elements)[0]) note.push("spelling fixed");
    if (emptyIn.length && emptyIn.length < where.length) note.push(`empty in ${emptyIn.length} of ${where.length} models`);
    if (expected && !holds) note.push("empty, but elements are expected here (not deleted)");
    const types = uniq.reduce((n, m) => n + (m.facts?.worksets ?? []).filter((w) => f.names.has(w.name)).reduce((k, w) => k + Number(w.types ?? 0), 0), 0);
    if (types) note.push(`holds ${types} type(s): a plan moves instances only, so it is kept unless the types move by hand`);
    const hid = hides.get(r.workset) ?? [];
    if (hid.length) note.push(`WOULD HIDE ${hid.reduce((n, h) => n + h.n, 0)} element(s) in ${[...new Set(hid.map((h) => h.v))].map(q).join(", ")}`);
    const proposed = r.isDefault ? defaultName ?? (prefix ? `${prefix}GENERAL` : "") : r.workset;
    if (r.isDefault) note.push(defaultName ? "the naming rule's default name" : proposed ? "a new default workset name from the project prefix" : "name the default workset (change to ...)");
    const rule = { workset: r.workset, categories: r.categories, levels: r.levels, words: r.words, material: r.material, links: r.links, cad: r.cad, codes: r.codes, manual: r.manual, isDefault: r.isDefault, protected: r.protected, twins };
    return { Group: section(r, action === "delete"), "Proposed workset": proposed, Action: action, Models: where.length === all.length && all.length > 1 ? `All ${all.length}` : `${where.join(", ")}${where.length > 1 ? ` (${where.length})` : ""}`,
      "What it holds (from its name)": describe(r), "Holds now": holds, "Would receive": recv, "Open / visibility": visText(f.vis), Note: note.join("; "), [DECISION]: "", Rule: JSON.stringify(rule) };
  });
  const ORDER = ["General", "Levels and grids", "By level", "By category", "Zones and assets", "Steel", "Links", "Visibility", "Review", "Delete"];
  ruleRows.sort((a, b) => ORDER.indexOf(a.Group) - ORDER.indexOf(b.Group) || a["Proposed workset"].localeCompare(b["Proposed workset"]));
  const counts = { worksets: ruleRows.length, keep: ruleRows.filter((r) => /^keep/.test(r.Action)).length, delete: ruleRows.filter((r) => r.Action === "delete").length,
    twins: ruleRows.filter((r) => /spelling twins/.test(r.Note)).length, moves: detail.filter((d) => d["Proposed action"] === "move").length,
    moveElements: detail.filter((d) => d["Proposed action"] === "move").reduce((n, d) => n + d.Elements, 0), review: detail.filter((d) => d["Proposed action"] === "review").length,
    sameAs: models.filter((m) => m.sameAs).map((m) => ({ model: m.model, sameAs: m.sameAs })) };
  return { rules: ruleRows, detail, models: models.map(({ model, hash, sameAs }) => ({ model, hash, sameAs })), counts };
}

const tableOf = (cols, rows) => [cols, ...rows.map((r) => cols.map((c) => r[c] ?? ""))];
export const proposalCsv = (p) => toCsv(tableOf(RULE_COLUMNS, p.rules));
export const detailCsv = (p) => toCsv(tableOf(COLUMNS, p.detail));
/** The xlsx: "Worksets" (the rule rows, the Rule column hidden) and "Groups" (the detail, the Key hidden). */
export async function proposalXlsx(p, file) {
  const wb = new ExcelJS.Workbook();
  const sheet = (name, cols, rows, hide, widths) => {
    const sh = wb.addWorksheet(name);
    tableOf(cols, rows).forEach((r) => sh.addRow(r));
    sh.getRow(1).font = { bold: true };
    sh.views = [{ state: "frozen", ySplit: 1 }];
    sh.autoFilter = { from: { row: 1, column: 1 }, to: { row: 1, column: cols.length } };
    cols.forEach((c, i) => { sh.getColumn(i + 1).width = widths[c] ?? 16; });
    sh.getColumn(cols.indexOf(hide) + 1).hidden = true;
    sh.getColumn(cols.indexOf(DECISION) + 1).fill = { type: "pattern", pattern: "solid", fgColor: { argb: "FFFFF2CC" } };
  };
  sheet("Worksets", RULE_COLUMNS, p.rules, "Rule", { "Proposed workset": 32, "What it holds (from its name)": 60, Note: 50, "Open / visibility": 36, Action: 24, [DECISION]: 34 });
  sheet("Groups", COLUMNS, p.detail, "Key", { Reason: 60, "Type / link / contents": 36, [DECISION]: 34 });
  await wb.xlsx.writeFile(file);
  return file;
}

export function proposalText(p, files) {
  const c = p.counts;
  const lines = [`WORKSET PLAN proposal for ${p.models.map((m) => m.model).join(", ")} (read-only, nothing changed): ${c.worksets} worksets (keep ${c.keep}, delete ${c.delete}, ${c.twins} with spelling twins merged); ${c.moves} element group(s) move (${c.moveElements} elements); ${c.review} group(s) to review (no confident home: left blank).`];
  for (const s of c.sameAs) lines.push(`${s.model}: same as ${s.sameAs} (identical facts), proposed once.`);
  lines.push(...p.rules.map((r) => `- [${r.Group}] ${r["Proposed workset"] || "(name it)"}: ${r.Action}; ${r["What it holds (from its name)"]}${r["Would receive"] ? ` Receives ${r["Would receive"]}.` : ""}${r.Note ? ` (${r.Note})` : ""}`).slice(0, 60));
  if (files?.length) lines.push(`Files (edit "${DECISION}": OK / change to <name> / skip; the Groups sheet / -groups.csv is the detail):\n${files.map((f) => `- ${f}`).join("\n")}`);
  lines.push("Next: the user edits the file, then workset_plan action confirm with file.");
  return lines.join("\n");
}

// ---- confirm: the edited file back -> a plan per model ----------------------------------------------------------------

const cellText = (v) => v == null ? "" : typeof v === "object" ? String(v.text ?? v.result ?? v.richText?.map((x) => x.text).join("") ?? "") : String(v);
/**
 * The table of an edited file (CSV, or the first sheet of an xlsx) -> { layout: "rules" | "groups", rows }. The rule-level
 * file has "What it holds" / Rule and no Row column; the group-level and the hand-made files have Row. Headers match loosely.
 */
export async function readTables(file) {
  const raw = [];
  if (/\.xlsx$/i.test(file)) {
    const wb = new ExcelJS.Workbook();
    await wb.xlsx.readFile(file);
    for (const sh of wb.worksheets) {
      const rows = [];
      sh.eachRow({ includeEmpty: false }, (r) => rows.push(Array.from({ length: Math.max(r.cellCount, RULE_COLUMNS.length, COLUMNS.length) }, (_, i) => cellText(r.getCell(i + 1).value))));
      if (rows.length) raw.push({ sheet: sh.name, rows });
    }
  } else raw.push({ sheet: null, rows: parseCsv(fs.readFileSync(file, "utf8")) });
  if (!raw.length || !raw[0].rows.length) throw new Error(`${file} is empty.`);
  const tables = raw.map((t) => tableOf2(t, file)).filter(Boolean);
  if (!tables.length) throw new Error(`${file} has no sheet with the workset_plan columns (the decision header is "${DECISION}").`);
  return tables;
}
/** The first table (a CSV, or an xlsx's first sheet with the proposal's columns). */
export const readTable = async (file) => (await readTables(file))[0];
function tableOf2({ sheet, rows }, file) {
  const head = rows[0].map((h) => norm(h));
  const col = (re) => head.findIndex((h) => re.test(h));
  const at = { model: col(/^model$/), row: col(/^row$/), current: col(/^current workset/), category: col(/^category$/), type: col(/^type/), level: col(/^level$/),
    elements: col(/^elements$/), proposed: col(/^proposed workset/), action: col(/^(proposed )?action$/), decision: col(/decision/), key: col(/^(group )?key$/),
    group: col(/^group$/), models: col(/^models$/), holds: col(/^what it holds/), rule: col(/^rule$/) };
  const layout = at.row < 0 && at.proposed >= 0 ? "rules" : "groups";
  const missing = (layout === "rules" ? ["proposed", "decision"] : ["model", "row", "current", "decision"]).filter((k) => at[k] < 0);
  if (missing.length) {
    if (sheet != null) return null;   // another sheet of the workbook (notes, a pivot): not a proposal sheet
    throw new Error(`${file} has no ${missing.join(", ")} column (expected a workset_plan proposal; the decision header is "${DECISION}").`);
  }
  const get = (r, k) => at[k] < 0 ? "" : String(r[at[k]] ?? "").trim();
  return { sheet, layout, rows: rows.slice(1).map((r, i) => ({ line: i + 2, model: get(r, "model"), row: up(get(r, "row")), current: get(r, "current"), category: get(r, "category"), type: get(r, "type"), level: get(r, "level"),
    elements: get(r, "elements"), proposed: String(r[at.proposed] ?? "").replace(/^\s+/, ""), action: get(r, "action"), decision: get(r, "decision"), key: get(r, "key"), group: get(r, "group"), models: get(r, "models"), rule: get(r, "rule") })) };
}
export const decided = (t) => (t?.rows ?? []).filter((r) => decisionOf(r.decision).kind !== "none").length;

/** A decision cell -> { kind: ok|change|skip|none|unclear, to } ("change to X", "-> X", "move to X", "OK", "yes", "skip", "no"). */
export function decisionOf(cell) {
  const s = String(cell ?? "").trim();
  if (!s) return { kind: "none" };
  if (/^(ok|okay|yes|y|agree|accept|approved?|✓|✔|x)$/i.test(s)) return { kind: "ok" };
  if (/^(skip|no|n|keep as is|leave|reject|-)$/i.test(s)) return { kind: "skip" };
  const m = s.match(/^(?:change(?:\s+it)?\s*(?:to)?|move\s+to|rename\s+to|->|=>|to)\s*:?\s*(.+)$/i);
  if (m && m[1].trim()) return { kind: "change", to: m[1].trim().replace(/^["']|["']$/g, "") };
  return { kind: "unclear" };
}

const finalPlan = () => ({ create: [], rename: [], moves: [], delete: [] });
const moveOf = (g, from, to) => ({ key: g.key, from, to, category: g.category, type: g.type ?? "", level: g.level ?? "", material: g.material ?? "", zone: g.zone ?? "", option: g.option ?? "", elements: Number(g.elements) });

/**
 * Group-level (and hand-made) rows + live facts -> { model, plan, creates, flagged, undecided, skipped, confirmed }.
 * A row is re-found by its Key, else by Model + Current workset + Category + Type + Level; a row whose Elements count
 * differs from the model now is flagged "changed since the proposal" and left out.
 */
export function confirm(rowsIn, facts) {
  const model = facts?.model ?? "model";
  const mine = rowsIn.filter((r) => modelKey(r.model) === modelKey(model));
  const groups = (facts?.groups ?? []).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const existing = new Map((facts?.worksets ?? []).map((w) => [norm(w.name), w]));
  const plan = finalPlan();
  const flagged = [], undecided = [], skipped = [], confirmed = [], decidedKeys = new Set();
  const findGroups = (r) => r.key ? groups.filter((g) => g.key === r.key)
    : groups.filter((g) => norm(g.workset) === norm(r.current) && (norm(g.category) === norm(r.category) || (W.isCad(g.category) && W.isCad(r.category))) && typeKey(g.type) === typeKey(r.type) && norm(g.level) === norm(r.level));
  const flag = (r, why) => flagged.push({ line: r.line, what: `${r.row} ${r.current}${r.category ? ` ${r.category}` : ""}${r.level ? ` @ ${r.level}` : ""}`, why });
  const finalName = new Map();
  for (const r of mine) {
    const d = decisionOf(r.decision);
    if (d.kind === "none") { undecided.push(r.line); continue; }
    if (d.kind === "unclear") { flag(r, `decision "${r.decision}" not understood (OK / change to <workset> / skip)`); continue; }
    if (d.kind === "skip") { skipped.push(r.line); if (r.row === "MOVE") findGroups(r).forEach((g) => decidedKeys.add(g.key)); continue; }
    if (r.row === "RENAME" || (r.row === "WORKSET" && d.kind === "change")) {
      const to = d.kind === "change" ? d.to : r.proposed;
      const w = existing.get(norm(r.current));
      if (!w) { flag(r, "the workset is not in the model now"); continue; }
      if (!to || norm(to) === norm(r.current)) { if (r.row === "RENAME") flag(r, "no new name: write change to <name>"); continue; }
      if (isProt(w)) { flag(r, "the datum workset is never renamed"); continue; }
      plan.rename.push({ from: w.name, to }); finalName.set(norm(w.name), to); confirmed.push(r.line);
    }
  }
  const finalOf = (n) => finalName.get(norm(n)) ?? n;
  const names = new Set([...existing.values()].map((w) => norm(finalOf(w.name))));
  for (const r of mine) {
    const d = decisionOf(r.decision);
    if (!["ok", "change"].includes(d.kind) || r.row === "RENAME" || r.row === "WORKSET") continue;
    if (r.row === "DELETE") {
      const w = existing.get(norm(r.current));
      if (!w) { flag(r, "the workset is not in the model now"); continue; }
      if (isProt(w)) { flag(r, "the datum workset is never deleted"); continue; }
      plan.delete.push(finalOf(w.name)); confirmed.push(r.line); continue;
    }
    if (r.row !== "MOVE") { flag(r, `unknown row type "${r.row}"`); continue; }
    const to = d.kind === "change" ? d.to : r.proposed;
    if (!to) { flag(r, "no target workset (a review row): write change to <workset>"); continue; }
    const found = findGroups(r);
    const now = found.reduce((n, g) => n + Number(g.elements), 0);
    if (!found.length) { flag(r, "not found in the model now (changed since the proposal)"); continue; }
    if (String(r.elements).trim() !== "" && Number(r.elements) !== now) { flag(r, `changed since the proposal: ${r.elements} element(s) in the file, ${now} in the model now`); continue; }
    const target = finalOf(to);
    if (isProt(existing.get(norm(target))) && !DATUM.test(r.category)) { flag(r, "model elements do not go on the datum workset"); continue; }
    if (!names.has(norm(target)) && !plan.create.some((c) => norm(c) === norm(target))) plan.create.push(target);
    for (const g of found) { decidedKeys.add(g.key); if (norm(finalOf(g.workset)) !== norm(target)) plan.moves.push(moveOf(g, finalOf(g.workset), target)); }
    confirmed.push(r.line);
  }
  return { model, plan, creates: plan.create, flagged, undecided, skipped, confirmed, rowsForModel: mine.length, decidedKeys };
}

/**
 * Rule-level rows -> the confirmed rules (OK: as proposed; change to X: the same rule under X, the workset renamed;
 * skip: dropped; no decision: left out and counted). -> { rules, flagged, undecided, skipped, confirmed }
 */
export function rulesFromRows(rows, ctx = {}) {
  const rules = [], flagged = [], undecided = [], skipped = [], confirmed = [];
  for (const r of rows) {
    const d = decisionOf(r.decision);
    if (d.kind === "none") { undecided.push(r.line); continue; }
    if (d.kind === "unclear") { flagged.push({ line: r.line, what: r.proposed, why: `decision "${r.decision}" not understood (OK / change to <name> / skip)` }); continue; }
    if (d.kind === "skip") { skipped.push(r.line); continue; }
    let rule;
    try { rule = r.rule ? JSON.parse(r.rule) : null; } catch { rule = null; }
    if (!rule) rule = { ...parseName(r.proposed.trim(), ctx), twins: [] };
    const from = rule.workset;
    const name = d.kind === "change" ? d.to : (r.proposed.trim() || (rule.isDefault ? "" : rule.workset));
    if (!name) { flagged.push({ line: r.line, what: from, why: "no name: write change to <name>" }); continue; }
    if (rule.protected && norm(name) !== norm(from)) { flagged.push({ line: r.line, what: from, why: "the datum workset is never renamed" }); continue; }
    rules.push({ ...rule, workset: name, from, delete: /^delete$/i.test(r.action) || /delete where empty/i.test(r.action), line: r.line });
    confirmed.push(r.line);
  }
  return { rules, flagged, undecided, skipped, confirmed };
}

/**
 * The confirmed rules evaluated against one model's facts -> its plan: renames (Workset1 to the default name, a changed
 * name, a spelling twin when the canonical name is not in the model), moves (a twin's elements when both exist; groups on
 * the default workset or contradicting their workset's name, placed by the rules), creates, deletes (empty after the moves).
 */
export function planFromRules(R, facts) {
  const model = facts?.model ?? "model";
  const ws = facts?.worksets ?? [];
  const groups = (facts?.groups ?? []).filter((g) => Number(g.elements) > 0).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const plan = finalPlan(), notes = [];
  const has = (n) => ws.find((w) => norm(w.name) === norm(n));
  const finalName = new Map(), mergeInto = new Map();
  for (const r of R) {
    const own = [r.from, ...(r.twins ?? [])].map(has).filter(Boolean);
    if (r.protected) { own.forEach((w) => finalName.set(w.name, w.name)); continue; }
    const target = has(r.workset);
    if (target) own.filter((w) => w !== target).forEach((w) => mergeInto.set(w.name, target.name));
    else if (own.length) { const [first, ...rest] = own; plan.rename.push({ from: first.name, to: r.workset }); finalName.set(first.name, r.workset); rest.forEach((w) => mergeInto.set(w.name, r.workset)); }
  }
  const finalOf = (n) => mergeInto.get(n) ?? finalName.get(n) ?? n;
  const exists = new Set(ws.map((w) => norm(finalOf(w.name))));
  const protectedNames = new Set(ws.filter(isProt).map((w) => norm(w.name)));
  const ruleOf = (n) => R.find((r) => norm(r.workset) === norm(n));
  const left = new Map(ws.map((w) => [w.name, Number(w.elements ?? 0)]));
  for (const g of groups) {
    if (DATUM.test(g.category)) continue;
    const cur = finalOf(g.workset), curRule = ruleOf(cur);
    if (curRule?.manual) continue;
    let to = mergeInto.has(g.workset) ? mergeInto.get(g.workset) : null;
    if (onDefault({ ...g, workset: g.workset }, protectedNames) || contradicts(g, curRule)) {
      const pl = place(g, R, groups);
      if (pl.workset) to = pl.workset; else if (!to) { notes.push(`${g.elements} ${g.category}${g.level ? ` @ ${g.level}` : ""} on ${q(g.workset)}: ${pl.reason} (left where it is)`); continue; }
    }
    if (!to || norm(to) === norm(g.workset)) continue;
    if (!exists.has(norm(to)) && !plan.create.some((c) => norm(c) === norm(to))) plan.create.push(to);
    plan.moves.push(moveOf(g, cur, to));
    left.set(g.workset, (left.get(g.workset) ?? 0) - Number(g.elements));
  }
  for (const [n] of mergeInto) if (!isProt(has(n))) plan.delete.push(n);
  for (const r of R.filter((x) => x.delete && !x.protected)) for (const w of [r.from, ...(r.twins ?? [])].map(has).filter(Boolean))
    if ((left.get(w.name) ?? 0) <= 0 && !plan.delete.includes(finalOf(w.name)) && !plan.moves.some((m) => norm(m.to) === norm(finalOf(w.name)))) plan.delete.push(finalOf(w.name));
  return { model, plan, creates: plan.create, notes };
}

/**
 * Both sheets of the xlsx decided: the rules' plan for the model, with the Groups sheet winning for every group it
 * decided (OK, change to or skip). Group targets follow the rules' renames. -> the rules' result with merged plan and notes.
 */
export function combine(rp, gp) {
  const ren = new Map(rp.plan.rename.map((r) => [norm(r.from), r.to]));
  const fin = (n) => ren.get(norm(n)) ?? n;
  const keys = gp.decidedKeys ?? new Set();
  const both = rp.plan.moves.filter((m) => keys.has(m.key)).length;
  const moves = [...rp.plan.moves.filter((m) => !keys.has(m.key)), ...gp.plan.moves.map((m) => ({ ...m, from: fin(m.from), to: fin(m.to) }))].filter((m) => norm(m.from) !== norm(m.to));
  const create = [...rp.plan.create];
  for (const c of gp.plan.create.map(fin)) if (!create.some((x) => norm(x) === norm(c)) && !rp.plan.rename.some((r) => norm(r.to) === norm(c))) create.push(c);
  const del = rp.plan.delete.filter((d) => !moves.some((m) => norm(m.to) === norm(d)));
  const sheetNotes = both ? [`${both} group(s) decided on both sheets: the Groups sheet wins for them.`] : [];
  return { ...rp, plan: { create, rename: rp.plan.rename, moves, delete: del }, creates: create, flagged: [...(rp.flagged ?? []), ...gp.flagged.map((f) => ({ ...f, what: `Groups sheet: ${f.what}` }))], sheetNotes, both };
}

/** Models named in the file that are not among the checked ones. */
export const otherModels = (rows, checked) => [...new Set(rows.map((r) => r.model).filter((m) => m && !checked.some((c) => modelKey(c) === modelKey(m))))];

const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
const rx = (s) => String(s).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
/** A whole-word match that also works between underscores ("PRJ_MEP_EL"): \\b treats "_" as a word character. */
export const wordRegex = (list) => `(?i)(?<![A-Za-z0-9])(${list.map(rx).join("|")})(?![A-Za-z0-9])`;
/**
 * Confirmed rule rows -> project rules (assign_worksets format): category, level (regex), material, zone or type (regex
 * on the name words: zone when a model zone carries the word), links by their code. Category rules come first, the most
 * specific first; category-free rules (zone / level / material) last, so they only take what no category rule takes.
 */
export function vaultRules(R, zones = []) {
  const zoneWords = new Set(zones.flatMap((z) => words(z)));
  const out = [];
  for (const r of R) {
    if (r.manual || r.isDefault || r.protected) continue;
    const base = { id: `plan-${slug(r.workset)}`, workset: r.workset, source: "workset_plan" };
    if (r.links) { out.push({ ...base, category: ["RVT Links"], ...(r.codes.length ? { type: wordRegex(r.codes) } : {}), spec: r.codes.length ? 9 : 1, cat: 1 }); continue; }
    if (r.cad) { out.push({ ...base, category: ["CAD Imports"], spec: 1, cat: 1 }); continue; }
    if (!r.categories.length && !r.levels.length && !r.words.length && !r.material) continue;
    const z = r.words.filter((w) => zoneWords.has(w)), t = r.words.filter((w) => !zoneWords.has(w));
    out.push({ ...base, ...(r.categories.length ? { category: r.categories } : {}), ...(r.levels.length ? { level: `^(${r.levels.map(rx).join("|")})$` } : {}),
      ...(r.material ? { material: r.material } : {}), ...(z.length ? { zone: wordRegex(z) } : {}), ...(t.length ? { type: wordRegex(t) } : {}),
      spec: (r.levels.length ? 2 : 0) + (r.words.length ? 2 : 0) + (r.material ? 1 : 0), cat: r.categories.length ? 1 : 0 });
  }
  return out.sort((a, b) => b.cat - a.cat || b.spec - a.spec).map(({ spec, cat, ...r }) => r);
}

/**
 * Generalisable rules from confirmed group moves (group-level file): category + material -> workset when every level
 * agrees, else one rule per level (level-specific rules first).
 */
export function deriveRules(plans) {
  const by = new Map();
  for (const p of plans) for (const m of p.plan.moves) {
    const mat = known(m.material) ? m.material : "";
    const k = `${m.category}|${mat}`;
    if (!by.has(k)) by.set(k, { category: m.category, material: mat, levels: new Map() });
    const e = by.get(k).levels;
    e.set(m.level, new Set([...(e.get(m.level) ?? []), m.to]));
  }
  const rules = [], ambiguous = [];
  for (const { category, material, levels } of by.values()) {
    const targets = new Set([...levels.values()].flatMap((s) => [...s]));
    const mat = material ? { material } : {};
    if (targets.size === 1) { rules.push({ id: `plan-${slug(category)}${material ? `-${material}` : ""}`, workset: [...targets][0], category, ...mat, source: "workset_plan" }); continue; }
    for (const [level, ts] of levels) {
      if (ts.size !== 1 || !level) { ambiguous.push({ category, material, level, worksets: [...ts] }); continue; }
      rules.unshift({ id: `plan-${slug(category)}-${slug(level)}${material ? `-${material}` : ""}`, workset: [...ts][0], category, level: `^${rx(level)}$`, ...mat, source: "workset_plan" });
    }
  }
  return { rules, ambiguous };
}

const selector = (r) => JSON.stringify([[].concat(r.category ?? []).map(norm).sort(), r.level ?? "", r.material ?? "", r.zone ?? "", r.type ?? "", r.family ?? "", r.function ?? ""]);
/**
 * Merge derived rules into a project-requirements object (worksets.rules): new rules first; the same rule again is
 * "already there"; an existing rule with the same id or the same selectors but another workset is a conflict, kept as
 * it is and listed. -> { json, added, same, conflicts }
 */
export function mergeRules(json, rules, { overlapsOk = false } = {}) {
  const out = JSON.parse(JSON.stringify(json ?? {}));
  out.worksets ??= {};
  const have = Array.isArray(out.worksets.rules) ? out.worksets.rules : [];
  const added = [], same = [], conflicts = [], overlaps = [];
  const list = [...have];
  for (const r of rules) {
    const hit = have.find((h) => h.id === r.id) ?? have.find((h) => selector(h) === selector(r));
    if (hit) { if (hit.workset === r.workset && selector(hit) === selector(r)) same.push(r); else conflicts.push({ rule: r, existing: hit }); continue; }
    // First match wins: a category-free rule (material / zone only) goes after every rule; another before the first
    // existing rule that is less specific, never ahead of a category rule when it has no category itself.
    const at = !hasCategory(r) ? list.length : (() => { const k = list.findIndex((h) => specificity(h) < specificity(r)); return k < 0 ? list.length : k; })();
    const before = list.slice(0, at), after = list.slice(at);
    // Overlaps with the rules already in the file (not among the new ones); a category-free rule after a category rule is by design.
    const old = (h) => have.includes(h) && h.workset !== r.workset;
    const shadowedBy = before.filter((h) => old(h) && overlap(h, r) && !(!hasCategory(r) && hasCategory(h)));
    const shadows = after.filter((h) => old(h) && overlap(r, h) && !(!hasCategory(h) && hasCategory(r)));
    if ((shadowedBy.length || shadows.length) && !overlapsOk) { overlaps.push({ rule: r, shadowedBy, shadows }); continue; }
    list.splice(at, 0, r);
    added.push(r);
  }
  out.worksets.rules = list;
  return { json: out, added, same, conflicts, overlaps };
}
const hasCategory = (r) => [].concat(r.category ?? []).length > 0;
/** How narrow a rule is: category, then level, zone / type / family, material. */
export const specificity = (r) => (hasCategory(r) ? 8 : 0) + (r.level ? 4 : 0) + (r.zone || r.type || r.family || r.function || r.parameter ? 2 : 0) + (r.material ? 1 : 0);
/** Can two rules match the same element (another workset)? Unknown regexes count as overlapping only when equal or absent. */
export function overlap(a, b) {
  const ca = [].concat(a.category ?? []).map(norm), cb = [].concat(b.category ?? []).map(norm);
  if (ca.length && cb.length && !ca.some((c) => cb.includes(c))) return false;
  if (a.material && b.material && a.material !== b.material) return false;
  for (const k of ["level", "zone", "type", "family", "function"]) if (a[k] && b[k] && a[k] !== b[k]) return false;
  return true;
}


export const ruleText = (r) => `${[].concat(r.category ?? []).join("/") || "any category"}${r.level ? ` @ level ${r.level.replace(/^\^\(?|\)?\$$/g, "").replace(/\\(.)/g, "$1")}` : ""}${r.zone ? ` in zone ${r.zone}` : ""}${r.type ? ` type ${r.type}` : ""}${r.material ? ` (${r.material})` : ""} -> ${r.workset}`;

export function confirmText(results, { files = [], others = [], rules, merge, saved, file, layout, unused = [] } = {}) {
  const lines = [];
  for (const c of results) {
    const p = c.plan;
    lines.push(`WORKSET PLAN CONFIRMED for ${c.model} from ${file}${layout === "rules" ? " (rule-level: the rules evaluated against the model)" : ""}: create ${p.create.length}, rename ${p.rename.length}, move ${p.moves.length} group(s) (${p.moves.reduce((n, m) => n + m.elements, 0)} elements), delete ${p.delete.length}.${c.confirmed ? ` Rows: ${c.confirmed.length} confirmed, ${c.skipped.length} skipped, ${c.undecided.length} undecided (no decision: left out), ${c.flagged.length} flagged.` : ""}`);
    if (p.create.length) lines.push(`New worksets (a target the model does not have yet): ${p.create.map(W.q).join(", ")}.`);
    if (p.rename.length) lines.push(`Renames: ${p.rename.map((r) => `${W.q(r.from)} -> ${W.q(r.to)}`).join("; ")}.`);
    if (c.flagged?.length) lines.push(`Flagged, NOT in the plan:\n${c.flagged.slice(0, 20).map((f) => `- line ${f.line}: ${f.what}: ${f.why}`).join("\n")}${c.flagged.length > 20 ? `\n- ... ${c.flagged.length - 20} more` : ""}`);
    if (c.sheetNotes?.length) lines.push(...c.sheetNotes);
    if (c.notes?.length) lines.push(`Left where they are (no rule fits):\n${c.notes.slice(0, 15).map((n) => `- ${n}`).join("\n")}${c.notes.length > 15 ? `\n- ... ${c.notes.length - 15} more` : ""}`);
  }
  if (unused.length) lines.push(...unused);
  if (others.length) lines.push(`Rows for models not checked (open the model, or pass its path in paths, and confirm again): ${others.join(", ")}.`);
  if (files.length) lines.push(`Plan file(s):\n${files.map((f) => `- ${f}`).join("\n")}`);
  if (rules) {
    lines.push(rules.rules.length ? `Rules (category + level / zone + material -> workset; first match wins):\n${rules.rules.map((r) => `- ${ruleText(r)}`).join("\n")}` : "No rules derived (nothing confirmed).");
    if (rules.ambiguous?.length) lines.push(`Not generalised (the rows disagree): ${rules.ambiguous.map((a) => `${a.category}${a.level ? ` @ ${a.level}` : ""}${a.material ? ` (${a.material})` : ""} -> ${a.worksets.join(" / ")}`).join("; ")}.`);
  }
  if (merge) {
    lines.push(`${saved ? "Saved to" : "Would save to"} ${merge.file}: ${merge.added.length} new rule(s) (each before the first less specific rule; category-free ones last), ${merge.same.length} already there, ${merge.conflicts.length} conflict(s) (kept as they are)${merge.overlaps?.length ? `, ${merge.overlaps.length} overlap(s) to confirm` : ""}.`);
    if (merge.conflicts.length) lines.push(`Conflicts (not overwritten):\n${merge.conflicts.map((c) => `- ${ruleText(c.rule)}; the file has ${c.existing.id ? `${c.existing.id}: ` : ""}${ruleText(c.existing)}`).join("\n")}`);
    if (merge.overlaps?.length) lines.push(`Overlaps to confirm (first match wins; not saved unless the user agrees: overlaps_ok: true):\n${merge.overlaps.map((o) => `- ${ruleText(o.rule)}${o.shadows.length ? `; would shadow ${o.shadows.map((h) => `${h.id ? `${h.id}: ` : ""}${ruleText(h)}`).join(", ")}` : ""}${o.shadowedBy.length ? `; shadowed by ${o.shadowedBy.map((h) => `${h.id ? `${h.id}: ` : ""}${ruleText(h)}`).join(", ")}` : ""}`).join("\n")}`);
    if (!saved) lines.push("Show these rules to the user; save with the same call and confirm: true.");
  }
  lines.push("Next: workset_plan action apply with plan_file (check_only, then dry_run, then the identical call: ONE undo step).");
  return lines.join("\n");
}

// ---- apply: the add-in's answer to workset_admin apply_plan -----------------------------------------------------------

const stepText = (p) => p.kind === "create" ? `create ${p.workset}` : p.kind === "rename" ? `rename ${p.from ?? p.workset} -> ${p.to}` : p.kind === "move" ? `move ${p.elements ?? "?"} element(s) ${p.from ?? p.workset} -> ${p.to}${p.category ? ` (${p.category}${p.level ? ` @ ${p.level}` : ""})` : ""}` : `delete ${p.workset}`;
export const planOf = (r) => Array.isArray(r?.plan) ? r.plan : [];
/** Steps the dry run could not preview (worksets the user must borrow first): the add-in's counts, else from the step ids. */
export function notPreviewedOf(r) {
  const ids = new Set(r?.notPreviewed ?? []);
  const steps = planOf(r).filter((p) => ids.has(p.id)), moves = steps.filter((p) => p.kind === "move");
  const c = r?.notPreviewedCounts;
  return { steps: ids.size || Number(c?.moves ?? 0), moves: c?.moves ?? moves.length, elements: c?.elements ?? moves.reduce((n, p) => n + Number(p.elements ?? 0), 0), linkTypes: Number(c?.linkTypes ?? 0) };
}
export function applyText(r, { model, mode, rolledBack = false }) {
  const plan = planOf(r);
  const head = mode === "check" ? "check, read-only, nothing changed" : mode === "dry run" ? "dry run" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  const lines = [`WORKSET PLAN in ${model} (${head}).`];
  const blocks = W.blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing changed):\n${blocks.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  const k = (x) => plan.filter((p) => p.kind === x).length;
  lines.push(`Plan: ${plan.length} step(s) (create ${k("create")}, rename ${k("rename")}, move ${k("move")}, delete ${k("delete")})${plan.length ? `:\n${plan.slice(0, 40).map((p) => `- ${stepText(p)}`).join("\n")}${plan.length > 40 ? `\n- ... ${plan.length - 40} more` : ""}` : "."}`);
  if (r?.skipped?.length) lines.push(`Skipped (borrowed by others; ask them to relinquish, then run again for these):\n${r.skipped.map((s) => `- ${s.key}: ${(s.ids ?? []).length} element(s)${s.holder ? `, held by ${s.holder}` : ""}`).join("\n")}`);
  if (r?.changed?.length) lines.push(`Changed since the plan (the whole plan is refused; propose and confirm again):\n${r.changed.map((c) => `- ${c.key}: ${c.expected} planned, ${c.found} found`).join("\n")}`);
  if (r?.visibility?.length) lines.push(`Visibility in the export views (${(r.exportViews ?? []).map(W.q).join(", ") || "none"}):\n${r.visibility.slice(0, 20).map((v) => `- ${v.wouldHide ? `WOULD HIDE ${v.wouldHide}` : ""}${v.wouldHide && v.wouldShow ? ", " : ""}${v.wouldShow ? `would show ${v.wouldShow}` : ""} element(s) in ${W.q(v.view)}: ${v.key ?? v.move} -> ${v.to}`).join("\n")}`);
  if (r?.kept?.length) lines.push(`Kept (not empty after the moves): ${r.kept.map((x) => `${W.q(x.name)} ${x.elements}${x.why ? ` (${x.why})` : Number(x.types) ? ` (holds ${x.types} type(s))` : ""}`).join("; ")}.`);
  const np = notPreviewedOf(r);
  if (np.steps) lines.push(`Would move (not previewed: not yours yet): ${np.moves} group(s), ${np.elements} element(s)${np.linkTypes ? `, ${np.linkTypes} link type(s)` : ""}${np.steps > np.moves ? `; ${np.steps - np.moves} other step(s)` : ""}. The apply borrows those worksets first.`);
  if (r?.notes?.length) lines.push(`Notes:\n${r.notes.map((n) => `- ${typeof n === "string" ? n : n.text ?? JSON.stringify(n)}`).join("\n")}`);
  if (mode !== "check") {
    const v = r?.verify;
    lines.push(!v ? "Self-check: not reported." : v.ok ? `Self-check: ok (element count per category the same before and after${v.checked != null ? `, ${v.checked} checked` : ""}).`
      : `Self-check FAILED: ${(v.mismatches ?? []).slice(0, 5).map((m) => `${m.category ?? m.what}: ${m.before} -> ${m.after}`).join("; ")}; rolled back, nothing changed.`);
    const d = r?.done ?? {};
    lines.push(mode === "dry run" || rolledBack ? "Changes: rolled back, nothing changed." : `Changes: ${[d.created ? `created ${d.created}` : "", d.renamed ? `renamed ${d.renamed}` : "", d.moved ? `moved ${d.moved} element(s)` : "", d.deleted ? `deleted ${d.deleted}` : ""].filter(Boolean).join(", ") || "nothing"} done.`);
  }
  return lines.join("\n");
}
/** The plan file written by confirm -> its plan, checked. */
export function readPlanFile(json) {
  if (json?.schema !== "ace.workset-plan/1" || !json.plan) throw new Error("Not a workset_plan plan file (schema ace.workset-plan/1): run workset_plan action confirm first.");
  const p = json.plan;
  for (const k of ["create", "rename", "moves", "delete"]) if (!Array.isArray(p[k])) throw new Error(`The plan file has no ${k} list.`);
  return { model: json.model, plan: p };
}
