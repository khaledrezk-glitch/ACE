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
import { esc } from "./vault.js";
import { brandedPage, chips } from "./interference.js";

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
const LEVEL_NOISE = new Set(["FLOOR", "LEVEL", "LVL", "L", "STOREY", "STORY"]);
const STOP = new Set(["AND", "THE", "OF", "WORKSET", "ALL", "NEW", "OLD", "MODEL", "ELEMENTS", "IN"]);
const MANUAL = new Set(["HIDE", "HIDDEN", "TEMP", "TEMPORARY"]);
const DATUM = /^(levels|grids|reference planes|scope boxes|matchline)$/i;
const LINK_WORDS = new Set(["LINK", "LINKS", "RVT", "CAD", "DWG", "IMPORT", "IMPORTS"]);
/** Workset-plan words beyond the audit's table (stairs and their parts). */
const EXTRA_TOKENS = { STAIR: ["Stairs", "Runs", "Landings", "Stair Runs", "Stair Landings"], STAIRS: ["Stairs", "Runs", "Landings", "Stair Runs", "Stair Landings"] };
/** Groups that never get a row: sketches and sub-elements ("<Sketch>", "<Stair/Ramp Sketch: ...>") and legend components. */
export const modelGroup = (g) => !!g && Number(g.elements) > 0 && !/^</.test(String(g.category ?? "").trim()) && !/^legend components$/i.test(String(g.category ?? "").trim());

/**
 * Each level's codes and phrases, matched as WHOLE words only: the code before "(" after an "X-" prefix ("A-01(FIRST
 * FLOOR)" -> 01, "C-GF(GROUND FLOOR)" -> GF; numbers compare as numbers), the bracketed words as one phrase ("GROUND
 * FLOOR"), a level name without either as its phrase ("LEVEL 1"); never a single letter. Vault aliases
 * (naming.levelAliases { TOKEN: [level names or regex] }) add codes or phrases. -> [{ name, codes: Set, phrases: [[words]] }]
 */
export function levelTokens(levels, aliases = {}) {
  const seen = new Set();
  const given = new Map((levels ?? []).filter((l) => l && typeof l === "object" && l.name).map((l) => [l.name, l]));
  const list = (levels ?? []).map((l) => typeof l === "string" ? l : l?.name).filter((n) => n && !seen.has(n) && seen.add(n)).map((name) => {
    const codes = new Set(), phrases = [];
    // The add-in's own code / phrase (1.13.3) when it sends them; else derived here the same way.
    const f = given.get(name);
    if (f && ("code" in f || "phrase" in f)) {
      const cw = words(f.code ?? "").map(num);
      if (cw.length === 1 && (cw[0].length > 1 || /^\d$/.test(cw[0]))) codes.add(cw[0]); else if (cw.length > 1) phrases.push(cw);
      if (f.phrase && words(f.phrase).length) phrases.push(words(f.phrase).map(num));
      return { name, codes, phrases };
    }
    const m = String(name).match(/^([^(]*)\((.*)\)\s*$/);
    // As the add-in: one leading prefix of 1-3 letters and "-" is dropped ("ST-L1-01" -> "L1-01", "C-GF" -> "GF").
    const head = (m ? m[1] : String(name)).trim().replace(/^[A-Za-z]{1,3}-/, "");
    const hw = words(head).map(num);
    if (hw.length === 1 && (hw[0].length > 1 || /^\d$/.test(hw[0]))) codes.add(hw[0]);
    else if (hw.length > 1 && hw.some((w) => !LEVEL_NOISE.has(w))) phrases.push(hw);
    if (m && words(m[2]).length) phrases.push(words(m[2]).map(num));
    return { name, codes, phrases };
  });
  for (const [tok, targets] of Object.entries(aliases ?? {})) {
    const ph = words(tok).map(num);
    if (!ph.length) continue;
    for (const t of [].concat(targets).map(String)) {
      let re = null;
      try { re = new RegExp(t, "i"); } catch { re = null; }
      for (const l of list) if (norm(l.name) === norm(t) || (re && re.test(l.name))) { if (ph.length === 1) l.codes.add(ph[0]); else l.phrases.push(ph); }
    }
  }
  return list;
}
/** The parse context of a proposal or a confirm: levels (with the vault's aliases), the token table, the vault's own tokens. */
export function nameCtx(levels, naming, names = []) {
  return { levels: levelTokens(levels, naming?.levelAliases), table: W.tokenTable(naming), strong: new Set(Object.keys(naming?.categoryTokens ?? {}).map((k) => up(k).trim())),
    noise: commonTokens(names), manualOnly: naming?.manualOnly ?? [], typeTokens: naming?.typeTokens ?? null, zoneWins: naming?.zoneWins !== false };
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

/** Category words that name an asset (a zone / asset workset keeps what sits on it) and are distinctive in type names. */
const ASSET_TOKENS = new Set(["PARAPET", "PARAPETS", "SOG", "RAFT", "PILE", "PILES", "FOOTING", "FOOTINGS"]);
/** Material abbreviations that are distinctive in type names ("PCC 100", "Floor : PCC"). */
const DISTINCT_MATERIAL = new Set(["PCC", "RCC", "PRECAST"]);
/** Category words that head a name without a plural (mass nouns and abbreviations). */
const MASS_HEADS = new Set(["FRAMING", "FURNITURE", "RAFT", "SOG", "CAD", "DWG", "RVT", "STEEL"]);
const isZoneWord = (x) => x.kind === "other" && x.w.length >= 3 && !STOP.has(x.w) && !MANUAL.has(x.w) && !/^\d+$/.test(x.w);
/**
 * A workset name as a rule: the categories, levels, material, link / CAD and manual (visibility) words it carries, and the
 * remaining words (zone or asset). Levels match whole codes and phrases only (levelTokens). A built-in category word
 * counts only as the head of the name (no zone word after it, or first in its part) and never inside a zone phrase joined
 * by "&" / "+" / AND ("POOL & Pump Room" is a zone); the vault's naming.categoryTokens always count, phrases included
 * ("GROUND BEAMS"). A word that is a level AND a category (ROOF) is the level when the name has another category or a
 * material, else the category (FOUNDATIONS).
 */
export function parseName(name, { levels = [], table = W.CATEGORY_TOKENS, strong = new Set(), noise = new Set(), manualOnly = [], isProtected = false } = {}) {
  const t = { ...table, ...EXTRA_TOKENS };
  for (const k of strong) if (table[k]) t[k] = table[k];
  const out = { workset: name, categories: [], levels: [], words: [], material: null, links: false, cad: false, codes: [], assets: [], distinct: [],
    manual: manualOnly.some((m) => norm(m) === norm(name)), isDefault: W.isDefaultName(name), protected: !!isProtected };
  // The name's parts (split at - _ ( ) / , ;) and its words, each with its part and whether the part is a "&" list.
  const parts = String(name ?? "").split(/[-_()\/,;|]+/);
  const ws = [];
  parts.forEach((p, seg) => words(p).forEach((w) => ws.push({ w, seg })));
  const listParts = new Map(parts.map((p, i) => [i, /&|\+|\bAND\b/i.test(p) ? p.split(/&|\+|\bAND\b/i).map((c) => words(c)) : null]));
  const strongPh = [...strong].map((k) => ({ k, ph: words(k) })).filter((x) => x.ph.length > 1 && t[x.k]).sort((a, b) => b.ph.length - a.ph.length);
  const levelPh = levels.flatMap((l) => l.phrases.filter((p) => p.length > 1).map((ph) => ({ l: l.name, ph }))).sort((a, b) => b.ph.length - a.ph.length);
  const at = (i, ph) => ph.every((x, j) => ws[i + j] && num(ws[i + j].w) === x);
  const items = [];
  for (let i = 0; i < ws.length;) {
    const { w, seg } = ws[i];
    const sp = strongPh.find((x) => at(i, x.ph));
    if (sp) { items.push({ w: sp.k, seg, kind: "strong", cats: t[sp.k] }); i += sp.ph.length; continue; }
    const lp = levelPh.filter((x) => at(i, x.ph));
    if (lp.length) { const n = lp[0].ph.length; items.push({ w: ws.slice(i, i + n).map((x) => x.w).join(" "), seg, kind: "level", levels: [...new Set(lp.filter((x) => x.ph.length === n).map((x) => x.l))] }); i += n; continue; }
    i++;
    if (MANUAL.has(w)) out.manual = true;
    if (noise.has(w)) continue;
    if (LINK_WORDS.has(w)) { if (w === "CAD" || w === "DWG") out.cad = true; else out.links = true; continue; }
    if (MATERIAL_TOKENS[w]) { out.material = MATERIAL_TOKENS[w]; if (DISTINCT_MATERIAL.has(w)) out.distinct.push(w); continue; }
    if (strong.has(w) && t[w]) { items.push({ w, seg, kind: "strong", cats: t[w] }); continue; }
    const lv = levels.filter((l) => l.codes.has(num(w)) || l.phrases.some((p) => p.length === 1 && p[0] === num(w))).map((l) => l.name);
    if (lv.length) { items.push({ w, seg, kind: t[w] ? "levelcat" : "level", levels: lv, cats: t[w] }); continue; }
    if (t[w]) { items.push({ w, seg, kind: "cat", cats: t[w] }); continue; }
    items.push({ w, seg, kind: "other" });
  }
  // A built-in category word inside a zone list, or followed by a zone word while not first in its part, is a zone word.
  const before = items.map((x) => ({ ...x }));
  items.forEach((x, i) => {
    if (x.kind !== "cat") return;
    const list = listParts.get(x.seg);
    const kindOf = (w) => before.find((y) => y.seg === x.seg && y.w === w)?.kind;
    if (list) { if (!list.every((c) => c.filter((w) => ["cat", "strong", "other"].includes(kindOf(w))).every((w) => ["cat", "strong"].includes(kindOf(w))))) x.kind = "other"; return; }
    // After a non-category word in its part, a category word is the head of a noun phrase naming a place ("PUMP ROOM",
    // "PLANT ROOM"): a zone word, unless it is a plural or mass element head ("BASE COLUMNS", "LOBBY WALLS", "TST FRAMING").
    const firstInPart = !before.slice(0, i).some((y) => y.seg === x.seg && isZoneWord(y));
    const elementHead = (/S$/.test(x.w) && x.w.length > 3) || MASS_HEADS.has(x.w);
    if (!firstInPart && (!elementHead || before.slice(i + 1).some(isZoneWord))) x.kind = "other";
  });
  // A singular category word right before a plural (or mass) element head in the same part qualifies it: "FOUNDATION
  // WALLS" holds walls (FOUNDATION is a word of its name, not Structural Foundations), "WALL FOOTINGS" holds footings.
  items.forEach((x, i) => {
    const nx = items[i + 1];
    if (x.kind === "cat" && nx && nx.seg === x.seg && (nx.kind === "cat" || nx.kind === "strong") && !(/S$/.test(x.w) && x.w.length > 3) && ((/S$/.test(nx.w) && nx.w.length > 3) || MASS_HEADS.has(nx.w))) x.kind = "other";
  });
  const otherCat = items.some((x) => x.kind === "cat" || x.kind === "strong") || !!out.material;
  for (const x of items) {
    if (x.kind === "levelcat") x.kind = otherCat ? "level" : "cat";
    if (x.kind === "level") x.levels.forEach((l) => out.levels.includes(l) || out.levels.push(l));
    else if (x.kind === "cat" || x.kind === "strong") { x.cats.forEach((c) => out.categories.includes(c) || out.categories.push(c)); if (ASSET_TOKENS.has(x.w)) { out.assets.push(x.w); out.distinct.push(x.w); } }
    else if (x.kind === "other") {
      if (x.w.length >= 2 && x.w.length <= 3 && !STOP.has(x.w)) out.codes.push(x.w);   // a discipline code (EL, MV, PL ...) for links
      if (isZoneWord(x)) { out.words.push(x.w); out.distinct.push(x.w); }
    }
  }
  if (out.cad) out.links = false;
  // The type-name prefix its name implies: the initials of the part that names the category ("STRAP BEAMS" -> SB, "NECK
  // COLUMNS" -> NC), two letters or more; a tie-breaker only (place), never a filter of a saved rule.
  const head = parts.map((p) => words(p).filter((w) => !noise.has(w) && !STOP.has(w) && !MATERIAL_TOKENS[w] && !LINK_WORDS.has(w) && !/^\d/.test(w)))
    .filter((ws0) => ws0.length >= 2 && ws0.some((w) => t[w])).at(-1);
  if (head && out.categories.length && !out.links && !out.cad) out.initials = head.map((w) => w[0]).join("");
  return out;
}

/** The singular of a name word (FOOTINGS -> FOOTING, COLUMNS -> COLUMN; GLASS stays). */
const singular = (w) => w.length > 3 && w.endsWith("S") && !w.endsWith("SS") ? w.slice(0, -1) : w;
/** The type part of "Family : Type" (or the whole name). */
const typePart = (t) => { const s0 = String(t ?? ""); const i = s0.lastIndexOf(" : "); return (i >= 0 ? s0.slice(i + 3) : s0).trim(); };
/**
 * Does a group's type name carry a rule's type-name prefix? An explicit typePrefix (a rule's own, or vault naming.typePrefixes)
 * matches the start of the type part or of the whole name; the initials a name implies (STRAP BEAMS -> SB) must be followed by
 * a non-letter ("SB1 300x600", "SB-2", "SB"). -> the prefix that matched, or null.
 */
export function prefixHit(type, r) {
  const part = up(typePart(type)), full = up(String(type ?? "").trim());
  for (const p of [].concat(r?.typePrefix ?? []).map((x) => up(String(x ?? "").trim())).filter(Boolean)) if (part.startsWith(p) || full.startsWith(p)) return { prefix: p, explicit: true };
  const i = r?.initials;
  if (i && i.length >= 2 && part.startsWith(i) && !/[A-Z]/.test(part.charAt(i.length))) return { prefix: i, explicit: false };
  return null;
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

/** A zone / asset workset: its name carries zone or asset words (BOUNDARY WALLS, STEEL FENCE, RF PARAPETS). */
export const isZoneWs = (r) => !!r && usableRule(r) && !r.links && !r.cad && ((r.words?.length ?? 0) + (r.assets?.length ?? 0)) > 0;
/**
 * zoneWins (vault naming.zoneWins, default true): a group its zone / asset workset's category contradicts stays there
 * (a boundary wall's columns belong to BOUNDARY WALLS), unless one rule names that zone / asset word AND its category.
 * -> null (not a zone / asset workset) | { stay: true, why } | { to, why }
 */
export function zoneHold(g, cur, rules, { zoneWins = true } = {}) {
  if (!zoneWins || !isZoneWs(cur)) return null;
  const zw = [...cur.words, ...(cur.assets ?? [])];
  const other = rules.filter((r) => r !== cur && r.workset !== cur.workset && usableRule(r) && r.categories.length && catIs(g.category, r.categories) && [...(r.words ?? []), ...(r.assets ?? [])].some((w) => zw.includes(w)));
  if (other.length === 1) return { to: other[0].workset, why: `zone / asset ${zw.filter((w) => [...other[0].words, ...(other[0].assets ?? [])].includes(w)).join(" ").toLowerCase()} + category ${g.category}` };
  return { stay: true, why: `kept on zone/asset workset ${q(cur.workset)}` };
}

/**
 * The home of a group among the rules (parsed workset names): { workset|null, confidence: high|medium|low, reason }.
 * Links go by the discipline code in their name before a generic links workset; CAD imports to a CAD (else LINKS)
 * workset. Otherwise the category token decides (a group never goes to a workset naming another category; with no
 * workset for its category only category-free names compete), then level and zone narrow (a name with a category and no
 * level is the catch-all for levels without their own workset), a material mismatch excludes, and where like elements
 * already sit only breaks a tie. Manual (visibility) worksets never receive anything.
 */
export function place(g, rules, groups = [], opts = {}) {
  const cands = rules.filter(usableRule);
  // A type token that names a workset beats level + category ("Floor : SOG 120mm" -> SOG): the vault's typeTokens first.
  if (!/^rvt links$/i.test(g.category) && !W.isCad(g.category)) {
    const tw = new Set(words(g.type).flatMap((w) => [w, singular(w)]));
    for (const [tok, ws] of Object.entries(opts.typeTokens ?? {})) {
      const r = words(tok).length && words(tok).every((t) => tw.has(t)) ? cands.find((x) => norm(x.workset) === norm(ws)) : null;
      if (r) return { workset: r.workset, confidence: "high", reason: `type ${up(tok)} (vault typeTokens)` };
    }
    let hits = cands.filter((r) => !r.links && !r.cad && !(r.levels?.length && !(g.level && r.levels.includes(g.level))) && (r.distinct ?? r.words ?? []).some((w) => tw.has(w) || tw.has(singular(w))) && (!r.categories.length || catIs(g.category, r.categories)) && !(r.material && known(g.material) && r.material !== g.material));
    if (hits.length > 1) { const lv = hits.filter((r) => g.level && r.levels.includes(g.level)); if (lv.length) hits = lv; }
    if (hits.length === 1) return { workset: hits[0].workset, confidence: "high", reason: `type name ${(hits[0].distinct ?? hits[0].words).filter((w) => tw.has(w) || tw.has(singular(w))).join(" ")} names the workset` };
    // The type-name prefix (the office method: category, type-name prefix, level): among the rules for its category, the one
    // whose prefix the type carries (an explicit typePrefix, else the initials its name implies: STRAP BEAMS -> SB1 ...).
    const pre = cands.filter((r) => !r.links && !r.cad && r.categories.length && catIs(g.category, r.categories) && !(r.levels?.length && !(g.level && r.levels.includes(g.level)))
      && !(r.material && known(g.material) && r.material !== g.material)).map((r) => ({ r, hit: prefixHit(g.type, r) })).filter((x) => x.hit);
    const preBest = pre.some((x) => x.hit.explicit) ? pre.filter((x) => x.hit.explicit) : pre;
    if (preBest.length === 1) return { workset: preBest[0].r.workset, confidence: preBest[0].hit.explicit ? "high" : "medium", reason: `type name prefix ${preBest[0].hit.prefix}${preBest[0].hit.explicit ? " (typePrefix)" : ` (the initials of ${q(preBest[0].r.workset)})`}` };
  }
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
  const keepNames = new Set(list.flatMap((x) => [...(x.keep ?? []), ...(x.savedRules ?? []).map((r) => r.workset)]).filter(Boolean).map(twinKey));
  for (const { facts, naming, records } of list) {
    const hash = factsHash(facts), same = byHash.get(hash);
    models.push({ model: facts?.model ?? "model", facts, naming, hash, sameAs: same ?? null, applied: appliedIndex(records) });
    if (!same) byHash.set(hash, facts?.model ?? "model");
  }
  const uniq = models.filter((m) => !m.sameAs);
  const naming = uniq[0]?.naming ?? null;
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
  const names = [...fam.values()].map((f) => f.canonical);
  const ctx = nameCtx(uniq.flatMap((m) => m.facts?.levels ?? []), naming, names);
  const rules = [...fam.values()].map((f) => ({ ...parseName(f.canonical, { ...ctx, isProtected: f.protected }), fam: f }));
  const detail = [], receives = new Map(), hides = new Map(), done = { groups: 0, elements: 0, changed: 0 };
  for (const m of uniq) {
    const groups = (m.facts?.groups ?? []).filter(modelGroup).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
    const wsByName = new Map((m.facts?.worksets ?? []).map((w) => [w.name, w]));
    const protectedNames = new Set((m.facts?.worksets ?? []).filter(isProt).map((w) => norm(w.name)));
    for (const g of groups) {
      if (DATUM.test(g.category)) continue;
      const cur = rules.find((r) => r.workset === canonOf(g.workset, wsByName.get(g.workset)));
      if (cur?.manual) continue;
      // Applied by ACE earlier: not proposed again; moved elsewhere since: flagged for review, never silently re-proposed.
      const as = appliedState(g, m.applied, groups), rec = as?.rec;
      if (as?.kind === "applied") { done.groups++; done.elements += Number(g.elements); continue; }
      if (as?.kind === "changed") {
        done.changed++;
        detail.push({ Model: m.model, Row: "MOVE", "Current workset": g.workset, Category: g.category, "Type / link / contents": g.type ?? "", Level: g.level ?? "", Elements: Number(g.elements), "Proposed workset": "", "Proposed action": "review",
          Reason: `changed after apply: ACE moved it to ${q(rec.to)} on ${rec.at}${rec.planId ? ` (plan ${rec.planId})` : ""}, now on ${q(g.workset)}`, Confidence: "low", [DECISION]: "", Key: g.key });
        continue;
      }
      const dflt = onDefault(g, protectedNames), wrong = !dflt && contradicts(g, cur);
      if (!dflt && !wrong) continue;
      // zoneWins: what sits on a zone / asset workset stays there (shown, so the user can still mark the row).
      const zh = wrong ? zoneHold(g, cur, rules, ctx) : null;
      if (zh?.stay) {
        detail.push({ Model: m.model, Row: "MOVE", "Current workset": g.workset, Category: g.category, "Type / link / contents": g.type ?? "", Level: g.level ?? "", Elements: Number(g.elements),
          "Proposed workset": g.workset, "Proposed action": "keep", Reason: `${zh.why} (zoneWins: change to <workset> to move it)`, Confidence: "high", [DECISION]: "", Key: g.key });
        continue;
      }
      const pl = zh?.to ? { workset: zh.to, confidence: "high", reason: zh.why } : place(g, rules, groups, ctx);
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
  const all = uniq.map((m) => m.model);
  const ruleRows = rules.map((r) => {
    const f = r.fam;
    const where = [...new Set([...f.names.values()].flatMap((e) => [...e.models]))];
    const holds = [...f.names.values()].reduce((n, e) => n + e.elements, 0);
    const recv = receives.get(r.workset) ?? 0;
    const twins = [...f.names.keys()].filter((n) => n !== r.workset);
    const inModel = (mm) => (uniq.find((x) => x.model === mm)?.facts?.worksets ?? []).filter((w) => f.names.has(w.name));
    const emptyIn = where.filter((mm) => inModel(mm).every((w) => !Number(w.elements)));
    // Expected: elements its name asks for exist in a model (wherever they sit now, Workset1 included): never deleted.
    const expected = uniq.some((m) => (m.facts?.groups ?? []).filter(modelGroup).some((g) => (r.cad && W.isCad(g.category)) || (r.links && /^rvt links$/i.test(g.category))
      || (!r.cad && !r.links && r.categories.length && catIs(g.category, r.categories) && (!r.levels.length || r.levels.includes(g.level)))));
    // A catch-all (a category, no level): kept even when empty.
    const catchAll = !r.cad && !r.links && r.categories.length > 0 && !r.levels.length;
    // Kept by an earlier confirmed decision (the vault's worksets.keep, its confirmed names, a saved rule's workset).
    const kept = [...f.names.keys()].some((n) => keepNames.has(twinKey(n)));
    let action = "keep", note = [];
    const defaultName = naming?.defaultName && !W.isDefaultName(naming.defaultName) ? naming.defaultName : null;
    if (r.protected) action = "keep (the datum workset: never renamed or deleted)";
    else if (r.isDefault) action = defaultName ? `rename from ${r.workset}` : `keep ${r.workset} (default)`;
    else if (r.manual) action = "keep (manual only)";
    else if (kept) action = "keep";
    else if (!holds && !recv && !expected && !catchAll) action = "delete";
    else if (emptyIn.length && emptyIn.length < where.length && !recv && !expected && !catchAll) action = "keep where used, delete where empty";
    if (kept && !r.protected && !r.isDefault) note.push("kept: confirmed in the vault (never proposed for deletion)");
    if (twins.length && !r.protected) note.push(`merge spelling twins into it: ${twins.map(q).join(", ")}`);
    if (r.workset !== [...f.names.keys()].sort((a, b) => f.names.get(b).elements - f.names.get(a).elements)[0]) note.push("spelling fixed");
    if (emptyIn.length && emptyIn.length < where.length) note.push(`empty in ${emptyIn.length} of ${where.length} models`);
    if (expected && !holds && (r.cad || r.links || !recv)) note.push("empty, but elements are expected here (not deleted)");
    else if (catchAll && !holds && !recv && !expected) note.push("empty, kept: the catch-all for its category");
    const types = uniq.reduce((n, m) => n + (m.facts?.worksets ?? []).filter((w) => f.names.has(w.name)).reduce((k, w) => k + Number(w.types ?? 0), 0), 0);
    if (types) note.push(`holds ${types} type(s): a plan moves instances only, so it is kept unless the types move by hand`);
    const hid = hides.get(r.workset) ?? [];
    if (hid.length) note.push(`WOULD HIDE ${hid.reduce((n, h) => n + h.n, 0)} element(s) in ${[...new Set(hid.map((h) => h.v))].map(q).join(", ")}`);
    const proposed = r.isDefault ? defaultName ?? r.workset : r.workset;
    if (r.isDefault) note.push(defaultName ? "the vault's default name (naming.defaultName)" : "the default workset keeps its name (renaming it is opt-in: naming.defaultName in the vault)");
    const rule = { workset: r.workset, categories: r.categories, levels: r.levels, words: r.words, material: r.material, links: r.links, cad: r.cad, codes: r.codes, manual: r.manual, isDefault: r.isDefault, protected: r.protected, twins,
      // The rule's models (G9: a saved rule applies only there) include the models proposed once as "same as" one of them.
      assets: r.assets, distinct: r.distinct, ...(r.initials ? { initials: r.initials } : {}), models: [...where, ...models.filter((m) => m.sameAs && where.includes(m.sameAs)).map((m) => m.model)] };
    return { Group: section(r, action === "delete"), "Proposed workset": proposed, Action: action, Models: where.length === all.length && all.length > 1 ? `All ${all.length}` : `${where.join(", ")}${where.length > 1 ? ` (${where.length})` : ""}`,
      "What it holds (from its name)": describe(r), "Holds now": holds, "Would receive": recv, "Open / visibility": visText(f.vis), Note: note.join("; "), [DECISION]: "", Rule: JSON.stringify(rule) };
  });
  const ORDER = ["General", "Levels and grids", "By level", "By category", "Zones and assets", "Steel", "Links", "Visibility", "Review", "Delete"];
  ruleRows.sort((a, b) => ORDER.indexOf(a.Group) - ORDER.indexOf(b.Group) || a["Proposed workset"].localeCompare(b["Proposed workset"]));
  const counts = { worksets: ruleRows.length, keep: ruleRows.filter((r) => /^keep/.test(r.Action)).length, delete: ruleRows.filter((r) => r.Action === "delete").length,
    twins: ruleRows.filter((r) => /spelling twins/.test(r.Note)).length, moves: detail.filter((d) => d["Proposed action"] === "move").length,
    moveElements: detail.filter((d) => d["Proposed action"] === "move").reduce((n, d) => n + d.Elements, 0), review: detail.filter((d) => d["Proposed action"] === "review").length,
    keptZone: detail.filter((d) => d["Proposed action"] === "keep").length,
    sameAs: models.filter((m) => m.sameAs).map((m) => ({ model: m.model, sameAs: m.sameAs })), applied: done };
  return { rules: ruleRows, detail, models: models.map(({ model, hash, sameAs }) => ({ model, hash, sameAs })), counts };
}

const tableOf = (cols, rows) => [cols, ...rows.map((r) => cols.map((c) => r[c] ?? ""))];
const colsOf = (p, cols) => p.refreshed ? [...cols, SINCE] : cols;
export const proposalCsv = (p) => toCsv(tableOf(colsOf(p, RULE_COLUMNS), p.rules));
export const detailCsv = (p) => toCsv(tableOf(colsOf(p, COLUMNS), p.detail));
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
  sheet("Worksets", colsOf(p, RULE_COLUMNS), p.rules, "Rule", { "Proposed workset": 32, "What it holds (from its name)": 60, Note: 50, "Open / visibility": 36, Action: 24, [DECISION]: 34 });
  sheet("Groups", colsOf(p, COLUMNS), p.detail, "Key", { Reason: 60, "Type / link / contents": 36, [DECISION]: 34 });
  await wb.xlsx.writeFile(file);
  return file;
}

export function proposalText(p, files) {
  const c = p.counts;
  const lines = [`WORKSET PLAN proposal for ${p.models.map((m) => m.model).join(", ")} (read-only, nothing changed): ${c.worksets} worksets (keep ${c.keep}, delete ${c.delete}, ${c.twins} with spelling twins merged); ${c.moves} element group(s) move (${c.moveElements} elements); ${c.review} group(s) to review (no confident home: left blank)${c.keptZone ? `; ${c.keptZone} group(s) kept on their zone / asset workset` : ""}.`];
  for (const s of c.sameAs) lines.push(`${s.model}: same as ${s.sameAs} (identical facts), proposed once.`);
  if (p.standard) lines.push(`Standard: ${p.standard}.`);
  if (c.applied?.groups || c.applied?.changed) lines.push(`Applied by ACE earlier (not proposed again): ${c.applied.groups} group(s), ${c.applied.elements} element(s)${c.applied.changed ? `; ${c.applied.changed} group(s) changed after apply: flagged for review` : ""}.`);
  if (p.refreshed) lines.push(`Refreshed against the previous file: ${p.refreshed.new} new, ${p.refreshed.changed} changed, ${p.refreshed.same} same, ${p.refreshed.gone} gone; ${p.refreshed.kept} earlier decision(s) kept (column "${SINCE}").`);
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
  if (/^(ok,?\s*)?create( it| where missing)?$/i.test(s)) return { kind: "ok", create: true };
  if (/^(skip|no|n|keep as is|leave|reject|-)$/i.test(s)) return { kind: "skip" };
  const m = s.match(/^(?:change(?:\s+it)?\s*(?:to)?|move\s+to|rename\s+to|->|=>|to)\s*:?\s*(.+)$/i);
  if (m && m[1].trim()) return { kind: "change", to: m[1].trim().replace(/^["']|["']$/g, "") };
  return { kind: "unclear" };
}

const finalPlan = () => ({ create: [], rename: [], moves: [], delete: [] });
const moveOf = (g, from, to, row) => ({ key: g.key, from, to, category: g.category, type: g.type ?? "", level: g.level ?? "", material: g.material ?? "", zone: g.zone ?? "", option: g.option ?? "", elements: Number(g.elements), ...(row != null ? { row } : {}) });

/**
 * Group-level (and hand-made) rows + live facts -> { model, plan, creates, flagged, undecided, skipped, confirmed }.
 * A row is re-found by its Key, else by Model + Current workset + Category + Type + Level; a row whose Elements count
 * differs from the model now is flagged "changed since the proposal" and left out.
 */
export function confirm(rowsIn, facts) {
  const model = facts?.model ?? "model";
  const mine = rowsIn.filter((r) => modelKey(r.model) === modelKey(model));
  const groups = (facts?.groups ?? []).filter(modelGroup).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const existing = new Map((facts?.worksets ?? []).map((w) => [norm(w.name), w]));
  const plan = finalPlan();
  const flagged = [], undecided = [], skipped = [], confirmed = [], decidedKeys = new Set(), skipKeys = new Set();
  const findGroups = (r) => r.key ? groups.filter((g) => g.key === r.key)
    : groups.filter((g) => norm(g.workset) === norm(r.current) && (norm(g.category) === norm(r.category) || (W.isCad(g.category) && W.isCad(r.category))) && typeKey(g.type) === typeKey(r.type) && norm(g.level) === norm(r.level));
  const flag = (r, why) => flagged.push({ line: r.line, what: `${r.row} ${r.current}${r.category ? ` ${r.category}` : ""}${r.level ? ` @ ${r.level}` : ""}`, why });
  const finalName = new Map();
  for (const r of mine) {
    const d = decisionOf(r.decision);
    if (d.kind === "none") { undecided.push(r.line); continue; }
    if (d.kind === "unclear") { flag(r, `decision "${r.decision}" not understood (OK / change to <workset> / skip)`); continue; }
    if (d.kind === "skip") { skipped.push(r.line); if (r.row === "MOVE") findGroups(r).forEach((g) => { decidedKeys.add(g.key); skipKeys.add(g.key); }); continue; }
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
    for (const g of found) { decidedKeys.add(g.key); if (norm(finalOf(g.workset)) !== norm(target)) plan.moves.push(moveOf(g, finalOf(g.workset), target, r.line)); }
    confirmed.push(r.line);
  }
  return { model, plan, creates: plan.create, flagged, undecided, skipped, confirmed, rowsForModel: mine.length, decidedKeys, skipKeys };
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
    // A file proposed before the type-name prefix (initials) was read: the name gives it now.
    else if (rule.initials === undefined && rule.workset) { const ini = parseName(rule.workset, ctx).initials; if (ini) rule.initials = ini; }
    const from = rule.workset;
    const name = d.kind === "change" ? d.to : (r.proposed.trim() || (rule.isDefault ? "" : rule.workset));
    if (!name) { flagged.push({ line: r.line, what: from, why: "no name: write change to <name>" }); continue; }
    if (rule.protected && norm(name) !== norm(from)) { flagged.push({ line: r.line, what: from, why: "the datum workset is never renamed" }); continue; }
    rules.push({ ...rule, workset: name, from, delete: /^delete$/i.test(r.action) || /delete where empty/i.test(r.action), line: r.line, ...(d.create ? { create: true } : {}) });
    confirmed.push(r.line);
  }
  return { rules, flagged, undecided, skipped, confirmed };
}

/**
 * The confirmed rules evaluated against one model's facts -> its plan: renames (Workset1 to the default name, a changed
 * name, a spelling twin when the canonical name is not in the model), moves (a twin's elements when both exist; groups on
 * the default workset or contradicting their workset's name, placed by the rules), creates, deletes (empty after the moves).
 */
export function planFromRules(Rall, facts, opts = {}) {
  const model = facts?.model ?? "model";
  const ws = facts?.worksets ?? [];
  const groups = (facts?.groups ?? []).filter(modelGroup).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const plan = finalPlan(), notes = [];
  const has = (n) => ws.find((w) => norm(w.name) === norm(n));
  // A rule applies only in a model that has its workset (or the workset it renames); it never creates one elsewhere
  // unless the row says "create" or the call says create_missing: naming schemes do not leak between models.
  const inModel = (r) => !!has(r.workset) || [r.from, ...(r.twins ?? [])].some((n) => n && has(n));
  const R = Rall.filter((r) => r.protected || r.manual || r.isDefault || inModel(r) || r.create || opts.createMissing);
  const outside = Rall.filter((r) => !R.includes(r) && usableRule(r)).map((r) => r.workset);
  if (outside.length) notes.push(`not used in ${model} (it has no such workset; a "create" decision or create_missing: true adds it): ${outside.map(q).join(", ")}`);
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
    const dflt = onDefault({ ...g, workset: g.workset }, protectedNames);
    if (dflt || contradicts(g, curRule)) {
      const zh = dflt ? null : zoneHold(g, curRule, R, opts);
      if (zh?.stay && !to) continue;
      const pl = zh?.to ? { workset: zh.to } : zh?.stay ? { workset: null } : place(g, R, groups, opts);
      if (pl.workset) to = pl.workset; else if (!to) { notes.push(`${g.elements} ${g.category}${g.level ? ` @ ${g.level}` : ""} on ${q(g.workset)}: ${pl.reason} (left where it is)`); continue; }
    }
    if (!to || norm(to) === norm(g.workset)) continue;
    if (!exists.has(norm(to)) && !plan.create.some((c) => norm(c) === norm(to))) plan.create.push(to);
    plan.moves.push(moveOf(g, cur, to, ruleOf(to)?.line));
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
  const moves = [...rp.plan.moves.filter((m) => !keys.has(m.key)), ...gp.plan.moves.map((m) => ({ ...m, from: fin(m.from), to: fin(m.to), ...(m.row != null ? { row: `Groups ${m.row}` } : {}) }))].filter((m) => norm(m.from) !== norm(m.to));
  const create = [...rp.plan.create];
  for (const c of gp.plan.create.map(fin)) if (!create.some((x) => norm(x) === norm(c)) && !rp.plan.rename.some((r) => norm(r.to) === norm(c))) create.push(c);
  const del = rp.plan.delete.filter((d) => !moves.some((m) => norm(m.to) === norm(d)));
  const sheetNotes = both ? [`${both} group(s) decided on both sheets: the Groups sheet wins for them.`] : [];
  return { ...rp, plan: { create, rename: rp.plan.rename, moves, delete: del }, creates: create, flagged: [...(rp.flagged ?? []), ...gp.flagged.map((f) => ({ ...f, what: `Groups sheet: ${f.what}` }))], sheetNotes, both, skipKeys: gp.skipKeys };
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
export function vaultRules(R, zones = [], { createMissing = false } = {}) {
  const zoneWords = new Set(zones.flatMap((z) => words(z)));
  const out = [];
  for (const r of R) {
    if (r.manual || r.isDefault || r.protected) continue;
    // models (G9): where the workset is; a rule that may create its workset (create, create_missing) applies everywhere.
    // modelsScope "only" marks the list as a limit (1.14.10); lists saved before it are read as where a rule was proposed.
    const base = { id: `plan-${slug(r.workset)}`, workset: r.workset, source: "workset_plan", ...(r.models?.length && !r.create && !createMissing ? { models: r.models, modelsScope: "only" } : {}) };
    if (r.links) { out.push({ ...base, category: ["RVT Links"], ...(r.codes.length ? { type: wordRegex(r.codes) } : {}), spec: r.codes.length ? 9 : 1, cat: 1 }); continue; }
    if (r.cad) { out.push({ ...base, category: ["CAD Imports"], spec: 1, cat: 1 }); continue; }
    if (!r.categories.length && !r.levels.length && !r.words.length && !r.material) continue;
    const z = r.words.filter((w) => zoneWords.has(w)), t = r.words.filter((w) => !zoneWords.has(w));
    out.push({ ...base, ...(r.categories.length ? { category: r.categories } : {}), ...(r.levels.length ? { level: `^(${r.levels.map(rx).join("|")})$` } : {}),
      ...(r.material ? { material: r.material } : {}), ...(z.length ? { zone: wordRegex(z) } : {}), ...(t.length && !r.typePrefix ? { type: wordRegex(t) } : {}),
      ...(r.typePrefix ? { typePrefix: [].concat(r.typePrefix) } : {}),
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

/**
 * Exact model names of a rule's models key (G9), or null when it has none, uses wildcards / exclusions (any model may
 * match) or has no modelsScope "only" marker (a list saved before 1.14.10 does not limit the rule).
 */
const exactModels = (r) => { const p = [].concat(r?.models ?? []).map((x) => String(x ?? "").trim()).filter(Boolean); return W.modelsLimit(r) && p.length && !p.some((x) => x.startsWith("!") || x.includes("*")) ? p : null; };
/** Can two rules apply in the same model? Only rules with exact, disjoint models lists cannot. */
export const modelsMeet = (a, b) => { const x = exactModels(a), y = exactModels(b); return !x || !y || x.some((m) => y.some((n) => modelKey(m) === modelKey(n))); };
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
    // The same rule again (for more models: its exact models list grows; for every model: the list goes).
    const twin = have.find((h) => h.workset === r.workset && selector(h) === selector(r));
    if (twin) {
      const hm = exactModels(twin), rm = exactModels(r);
      if (hm && rm) twin.models = [...hm, ...rm.filter((m) => !hm.some((n) => modelKey(n) === modelKey(m)))];
      else if (hm && !r.models) { delete twin.models; delete twin.modelsScope; }
      same.push(r); continue;
    }
    // Rules for other models (disjoint models lists, G9) never collide: one project file holds per-model rules.
    const hit = have.find((h) => h.id === r.id && modelsMeet(h, r)) ?? have.find((h) => selector(h) === selector(r) && modelsMeet(h, r));
    if (hit) { conflicts.push({ rule: r, existing: hit }); continue; }
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
  if (!modelsMeet(a, b)) return false;
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
/** Pin counts of an apply (the add-in's pinned object, else its top-level counts). */
export const pinsOf = (r) => ({ touched: Number(r?.pinned?.count ?? r?.pinnedTouched ?? 0), directOk: Number(r?.pinned?.directOk ?? r?.pinnedDirectOk ?? 0), unpinRepin: Number(r?.pinned?.unpinRepin ?? r?.pinnedUnpinRepin ?? r?.pinnedNeededUnpin ?? 0) });
/** Visibility changes the add-in measured inside the change: [{ view, sheet ("number - name", null for an export view), hidden, shown, held, accepted, why, categories }]. */
export const visChangesOf = (r) => Array.isArray(r?.visibilityChanges) ? r.visibilityChanges : [];
export function applyText(r, { model, mode, rolledBack = false }) {
  const plan = planOf(r);
  const head = mode === "check" ? "check, read-only, nothing changed" : mode === "dry run" ? "dry run" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  const lines = [`WORKSET PLAN in ${model} (${head}).`];
  const blocks = W.blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing changed):\n${blocks.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  const k = (x) => plan.filter((p) => p.kind === x).length;
  lines.push(`Plan: ${plan.length} step(s) (create ${k("create")}, rename ${k("rename")}, move ${k("move")}, delete ${k("delete")})${plan.length ? `:\n${plan.slice(0, 40).map((p) => `- ${stepText(p)}`).join("\n")}${plan.length > 40 ? `\n- ... ${plan.length - 40} more` : ""}` : "."}`);
  if (r?.skipped?.length) lines.push(`Skipped (left where they are; borrowed ones: ask the holder to relinquish, then run again):\n${r.skipped.map((s) => `- ${s.key}: ${Number(s.count ?? 0) || (s.ids ?? []).length} element(s)${s.holder ? `, held by ${s.holder}` : ""}${s.why && s.why !== "borrowed" ? `: ${s.why}` : ""}`).join("\n")}`);
  if (r?.changed?.length) lines.push(`Changed since the plan (the whole plan is refused; propose and confirm again):\n${r.changed.map((c) => `- ${c.key}: ${c.expected} planned, ${c.found} found`).join("\n")}`);
  if (r?.visibility?.length) lines.push(`Visibility in the export views (${(r.exportViews ?? []).map(W.q).join(", ") || "none"}):\n${r.visibility.slice(0, 20).map((v) => `- ${v.wouldHide ? `WOULD HIDE ${v.wouldHide}` : ""}${v.wouldHide && v.wouldShow ? ", " : ""}${v.wouldShow ? `would show ${v.wouldShow}` : ""} element(s) in ${W.q(v.view)}: ${v.key ?? v.move} -> ${v.to}`).join("\n")}`);
  const vc = visChangesOf(r);
  if (vc.length) {
    const sheetV = vc.filter((c) => c.sheet), exp = vc.filter((c) => !c.sheet);
    const one = (c) => `${c.hidden ? `${c.hidden} hidden` : ""}${c.hidden && c.shown ? ", " : ""}${c.shown ? `${c.shown} shown` : ""}${c.held ? `; HELD ${c.held}` : ""}${c.accepted ? `; accepted ${c.accepted}` : ""}${c.categories && typeof c.categories === "object" ? ` (${Object.entries(c.categories).slice(0, 4).map(([k, n]) => `${n} ${k}`).join(", ")})` : ""}${c.why ? `: ${c.why}` : ""}`;
    const bySheet = new Map();
    for (const c of sheetV) { if (!bySheet.has(c.sheet)) bySheet.set(c.sheet, []); bySheet.get(c.sheet).push(c); }
    const vh = r?.visibilityHeld;
    const heldN = Number(vh?.elements ?? sheetV.reduce((n, c) => n + Number(c.held ?? 0), 0));
    if (bySheet.size) lines.push(`Visibility on sheets (moved elements a view on a sheet would show differently)${heldN ? `: ${heldN} element(s) HELD${vh?.moves?.length ? ` in ${vh.moves.length} move(s)` : ""}, they stay where they are; after the user agreed: allow_visibility_change: true, or the view / workset names` : ""}:\n${[...bySheet].slice(0, 30).map(([sh, cs]) => `- sheet ${sh}:\n${cs.slice(0, 10).map((c) => `  - view ${W.q(c.view)}: ${one(c)}`).join("\n")}`).join("\n")}${bySheet.size > 30 ? `\n- ... ${bySheet.size - 30} more sheet(s)` : ""}`);
    if (exp.length) lines.push(`Export views (a move that would hide an element there is held, unless allowed):\n${exp.slice(0, 10).map((c) => `- ${W.q(c.view)}: ${one(c)}`).join("\n")}`);
  }
  const nc = r?.visibilityChecked?.notChecked ?? [];
  if (nc.length) lines.push(`Visibility not checked in: ${nc.slice(0, 10).map(W.q).join(", ")}${nc.length > 10 ? ", ..." : ""}.`);
  if (r?.kept?.length) lines.push(`Kept (not empty after the moves): ${r.kept.map((x) => `${W.q(x.name)} ${x.elements}${x.why ? ` (${x.why})` : Number(x.types) ? ` (holds ${x.types} type(s))` : ""}${Number(x.nonModel) ? `, ${x.nonModel} non-model item(s)` : ""}`).join("; ")}.`);
  const fh = plan.filter((p) => p.kind === "move").reduce((n, p) => n + Number(p.followsHost ?? 0), 0);
  if (fh) lines.push(`${fh} sub-element(s) (stair runs, rails, panels, nested parts) follow their host.`);
  if (r?.reset) lines.push(`Reset to ${W.q(r.reset.to)}: ${r.reset.elements ?? 0} element(s) no rule takes go to the default workset${r.reset.kept?.length ? `; kept as they are: ${r.reset.kept.map(W.q).join(", ")}` : ""}${r.reset.keptByKey ? `; ${r.reset.keptByKey} element(s) of groups that stay untouched` : ""}${r.reset.skippedBorrowed ? `; ${r.reset.skippedBorrowed} borrowed (pending)` : ""}${r.reset.skippedCentral ? `; ${r.reset.skippedCentral} changed in central (pending)` : ""}${r.reset.readOnly ? `; ${r.reset.readOnly} read-only` : ""}.`);
  const pin = pinsOf(r);
  if (pin.touched) lines.push(`Pinned elements: ${pin.touched} (moved directly ${pin.directOk}; unpinned, moved, re-pinned: ${pin.unpinRepin}; the self-check confirms every pin state).`);
  if (Array.isArray(r?.worksetCounts) && r.worksetCounts.length) lines.push(`Per workset (before -> after${mode === "check" ? ", planned" : r.worksetCounts.some((c) => c.measured != null) ? ", measured" : ""}):\n${r.worksetCounts.filter((c) => c.before || c.after || c.deleted).map((c) => `- ${W.q(c.workset)}: ${c.before} -> ${c.measured ?? c.after}${c.deleted ? " (deleted)" : ""}`).join("\n")}`);
  if (r?.pending?.length) lines.push(pendingText(r.pending) + (Number(r.pendingTotal) > r.pending.length ? `\n- ... ${r.pendingTotal - r.pending.length} more (capped)` : ""));
  const np = notPreviewedOf(r);
  if (np.steps) lines.push(`Would move (not previewed: not yours yet): ${np.moves} group(s), ${np.elements} element(s)${np.linkTypes ? `, ${np.linkTypes} link type(s)` : ""}${np.steps > np.moves ? `; ${np.steps - np.moves} other step(s)` : ""}. The apply borrows those worksets first.`);
  if (r?.notes?.length) lines.push(`Notes:\n${r.notes.map((n) => `- ${typeof n === "string" ? n : n.text ?? JSON.stringify(n)}`).join("\n")}`);
  if (mode !== "check") {
    const v = r?.verify;
    lines.push(!v ? "Self-check: not reported." : v.ok ? `Self-check: ok (element count per category the same before and after${v.checked != null ? `, ${v.checked} checked` : ""}).`
      : `Self-check FAILED: ${(v.mismatches ?? []).slice(0, 5).map((m) => `${m.category ?? m.what}: ${m.before} -> ${m.after}`).join("; ")}; rolled back, nothing changed.`);
    if (r?.afterCheck) lines.push(W.afterCheckText(r.afterCheck, { mode }));
    const d = r?.done ?? {};
    const did = [d.created ? `created ${d.created}` : "", d.renamed ? `renamed ${d.renamed}` : "", d.moved ? `moved ${d.moved} element(s)` : "", d.deleted ? `deleted ${d.deleted}` : ""].filter(Boolean).join(", ") || "nothing";
    lines.push(mode === "dry run" || rolledBack ? "Changes: rolled back, nothing changed." : W.notDoneLine(r) ? `Changes: ${did} (applied, ONE undo step).\n${W.notDoneLine(r)}` : `Changes: ${did} done.`);
  }
  return lines.join("\n");
}

/**
 * The after-check a workset plan asks the add-in for (register #37): every target the confirmed rules name (category, level,
 * material, type prefix; links, CAD and category-free rules are left to their moves) and every move's target with its groups
 * (category, type, level); the sources (the default workset and the worksets the plan deletes) and the groups that stay by
 * decision (skipKeys, reset keepKeys). only: just the targets the filtered plan moves elements to.
 * -> { targets: [{ workset, selectors }], sources, exclude_keys }
 */
export function afterCheckSpec(pf, plan, facts, { keepKeys = [], only = null } = {}) {
  const ws = facts?.worksets ?? [];
  const ren = new Map((plan?.rename ?? []).map((r) => [norm(r.from), r.to]));
  const fin = (n) => ren.get(norm(n)) ?? n;
  const exists = new Set([...ws.map((w) => norm(fin(w.name))), ...(plan?.create ?? []).map(norm)]);
  const gone = new Set((plan?.delete ?? []).map(norm));
  const targets = new Map();
  const at = (n) => targets.get(norm(n)) ?? targets.set(norm(n), { workset: n, selectors: [] }).get(norm(n));
  for (const r of pf?.rules ?? []) {
    if (!usableRule(r) || r.delete || r.links || r.cad || !r.categories?.length || r.categories.every((c) => DATUM.test(c))) continue;
    if (!exists.has(norm(r.workset)) || gone.has(norm(r.workset))) continue;
    const tp = [].concat(r.typePrefix ?? []).filter(Boolean);
    at(r.workset).selectors.push({ category: r.categories, ...(r.levels?.length ? { level: r.levels } : {}), ...(r.material ? { material: r.material } : {}), ...(tp.length ? { typePrefix: tp } : {}) });
  }
  for (const m of plan?.moves ?? []) at(m.to).selectors.push({ category: [m.category], type: [m.type ?? ""], level: [m.level ?? ""] });
  let list = [...targets.values()].filter((t) => t.selectors.length);
  if (only) { const to = new Set((plan?.moves ?? []).map((m) => norm(m.to))); list = list.filter((t) => to.has(norm(t.workset))); }
  const dflt = ws.find((w) => w.isDefault || W.isDefaultName(w.name))?.name;
  const sources = [...new Set([dflt ? fin(dflt) : null, ...(plan?.delete ?? [])].filter(Boolean))];
  return { targets: list, sources, exclude_keys: [...new Set([...(pf?.skipKeys ?? []), ...(keepKeys ?? [])])] };
}
/** The plan file written by confirm -> its plan, checked. */
export function readPlanFile(json) {
  if (json?.schema !== "ace.workset-plan/1" || !json.plan) throw new Error("Not a workset_plan plan file (schema ace.workset-plan/1): run workset_plan action confirm first.");
  const p = json.plan;
  for (const k of ["create", "rename", "moves", "delete"]) if (!Array.isArray(p[k])) throw new Error(`The plan file has no ${k} list.`);
  return { model: json.model, plan: p, rules: Array.isArray(json.rules) ? json.rules : null, skipKeys: Array.isArray(json.skipKeys) ? json.skipKeys : [], standard: json.standard ?? null };
}

// ---- 1.13.3: the standard, status, reset, partial apply, apply records, pending (borrowed) actions ---------------------

/** A rule regex from the vault ("(?i)..." allowed); matching is case-insensitive. */
const jsRx = (s) => { if (s == null || s === "") return null; const src = String(s).replace(/^\(\?i\)/, ""); try { return new RegExp(src, "i"); } catch { return null; } };
/**
 * A group's home by the standard's rules (assign_worksets format, first match wins): category, level, material, zone,
 * type / family (on "Family : Type"); {level} / {zone} / {category} in the workset name are filled in. Rules on
 * conditions a group does not carry (function, parameter, department) are skipped. -> { workset, rule, why } | null
 */
export function stdTarget(g, rules = []) {
  for (const r of rules ?? []) {
    if (!r?.workset || r.function || r.parameter || r.department) continue;
    const cats = [].concat(r.category ?? []);
    if (cats.length && !catIs(g.category, cats)) continue;
    if (r.level && !jsRx(r.level)?.test(g.level ?? "")) continue;
    if (r.material && r.material !== g.material) continue;
    if (r.zone && !jsRx(r.zone)?.test(g.zone ?? "")) continue;
    if (r.type && !jsRx(r.type)?.test(g.type ?? "")) continue;
    if (r.family && !jsRx(r.family)?.test(g.type ?? "")) continue;
    if ([].concat(r.typePrefix ?? []).filter(Boolean).length && !prefixHit(g.type, { typePrefix: r.typePrefix })) continue;
    const workset = String(r.workset).replace(/\{level\}/g, g.level ?? "").replace(/\{zone\}/g, g.zone ?? "").replace(/\{category\}/g, g.category ?? "");
    return { workset, rule: r, why: `standard rule ${r.id ?? ruleText(r)}${r.clause ? ` (${r.clause})` : ""}` };
  }
  return null;
}
/** The standard in words, for every reply. */
export const standardText = (std) => !std ? "the workset names (no standard rules)" : `${std.label}${std.file ? ` (${std.file})` : ""}, ${std.rules.length} rule(s)`;

/** Group identity without its workset: how an applied row is found again after it moved. */
const identity = (g) => [g.category, g.type, g.level, g.material, g.zone, g.option].map((x) => x ?? "").join("|");
/** The apply records of a model (vault worksets-applied.json) -> by identity, the record rows (latest first). */
export function appliedIndex(records = []) {
  const idx = new Map();
  for (const rec of records ?? []) for (const r of rec.rows ?? []) { const k = r.identity ?? identity(r); idx.set(k, [{ ...r, at: rec.at, planId: rec.planId }, ...(idx.get(k) ?? [])]); }
  return { get: (k) => idx.get(k) ?? null, size: idx.size };
}
/**
 * A group against the apply records: "applied" (it sits where ACE put it), "pending" (still where ACE found it: not
 * moved, e.g. borrowed), "changed" (ACE moved it, it sits elsewhere now), or null (ACE never moved it). -> { kind, rec }
 */
export function appliedState(g, idx, groups = []) {
  const recs = idx?.get(identity(g));
  if (!recs?.length) return null;
  const on = (n) => norm(n) === norm(g.workset);
  // Applied: the same identity on the workset ACE put it on.
  const hit = recs.find((r) => on(r.to));
  if (hit) return { kind: "applied", rec: hit };
  // Changed after apply: ACE's elements are here (ids), or that identity left ACE's target entirely (none on it now).
  const ids = new Set((g.sampleIds ?? []).map(String));
  const byId = recs.find((r) => (r.ids ?? []).some((id) => ids.has(String(id))));
  if (byId) return { kind: "changed", rec: byId };
  const gone = recs.find((r) => !on(r.origin ?? r.from) && !on(r.from) && !groups.some((o) => identity(o) === identity(g) && norm(o.workset) === norm(r.to)));
  if (gone) return { kind: "changed", rec: gone };
  return null;   // new elements of that type elsewhere, or still where ACE found them: classified as usual
}
/**
 * An apply record from the plan that ran and the add-in's answer: rows per group move (a partly skipped row keeps its
 * moved count and lists the skipped ids; only a fully skipped row is left out), reset and retry steps in their own
 * sections, per element pairs (movedElements) when given; counts from movedElements when present.
 */
export function applyRecord({ model, storeKey, plan, result, at, only }) {
  const all = planOf(result).filter((p) => p.kind === "move");
  const isReset = (p) => p.key === "reset" || /^reset:/.test(String(p.id ?? ""));
  const isRetry = (p) => /^retry:/.test(String(p.id ?? "")) || p.key === "retry";
  const steps = all.filter((p) => !isReset(p) && !isRetry(p));
  const skipped = new Map();
  for (const x of result?.skipped ?? []) skipped.set(x.key, [...(skipped.get(x.key) ?? []), ...(x.ids ?? [])].concat(x.ids ? [] : Array(Number(x.count ?? 0)).fill(null)));
  for (const x of result?.pending ?? []) if (x.key) skipped.set(x.key, [...(skipped.get(x.key) ?? []), x.id]);
  const byKey = new Map((plan?.moves ?? []).map((m) => [m.key, m]));
  const src = steps.length ? steps.map((p) => ({ ...byKey.get(p.key), ...p })) : (plan?.moves ?? []);
  const rows = src.filter((m) => m.key && identity(m) !== "|||||").map((m) => {
    const sk = [...new Set(skipped.get(m.key) ?? [])], total = Number(m.elements ?? 0), moved = Math.max(0, total - sk.length);
    return { key: m.key, identity: identity(m), row: m.row ?? null, origin: String(m.key).split("|")[0], from: m.from, to: m.to, category: m.category ?? "", level: m.level ?? "", elements: moved,
      ...(sk.length ? { skippedIds: sk.filter((id) => id != null) } : {}), ...(m.ids ? { ids: m.ids } : {}) };
  }).filter((r) => r.elements > 0);
  const elements = Array.isArray(result?.movedElements) ? result.movedElements.map((e) => ({ id: e.id, from: e.from, to: e.to })) : undefined;
  const reset = all.filter(isReset).map((p) => ({ from: p.from ?? p.workset, to: p.to, elements: Number(p.elements ?? 0) }));
  const retried = (result?.retried ?? []).filter((x) => typeof x !== "object" || x.ok !== false).map((x) => typeof x === "object" ? x.id : x);
  const moved = Number(result?.movedTotal ?? elements?.length ?? 0) || rows.reduce((n, r) => n + r.elements, 0) + reset.reduce((n, r) => n + r.elements, 0);
  return { at, model, storeKey, planId: result?.planHash ?? null, ...(only ? { only } : {}), moved, rows, ...(reset.length ? { reset } : {}), ...(retried.length ? { retry: retried } : {}), ...(elements ? { elements } : {}) };
}
/**
 * Read-only status of one model against the standard (else the workset names): per workset correct / wrong (with where
 * each group should go and why) / no rule / empty (expected or unused) / in the standard; standard worksets missing from
 * the model; visibility and borrowed counts; every group in one of four states: applied by ACE (date, plan id), changed
 * after apply, correct already, still to do. -> { model, pct, totals, rows, missing, groups, pending, standard }
 */
export function statusOf(facts, { std = null, ctx = {}, records = [], pending = [] } = {}) {
  const model = facts?.model ?? "model";
  const ws = facts?.worksets ?? [];
  const groups = (facts?.groups ?? []).filter(modelGroup).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const rules = ws.map((w) => parseName(w.name, { ...ctx, isProtected: isProt(w) }));
  const ruleOf = (n) => rules.find((r) => r.workset === n);
  const protectedNames = new Set(ws.filter(isProt).map((w) => norm(w.name)));
  const datum = ws.find(isProt)?.name ?? null;
  const applied = appliedIndex(records);
  const row = new Map(ws.map((w) => [w.name, { workset: w.name, elements: Number(w.elements ?? 0), correct: 0, wrong: 0, noRule: 0, manual: 0, to: [], open: w.open ?? null,
    visibleByDefault: w.visibleByDefault ?? null, views: w.views ?? {}, borrowed: 0, isDefault: !!w.isDefault || W.isDefaultName(w.name), protected: isProt(w), types: Number(w.types ?? 0) }]));
  const out = [];
  for (const g of groups) {
    const r = row.get(g.workset) ?? row.set(g.workset, { workset: g.workset, elements: 0, correct: 0, wrong: 0, noRule: 0, manual: 0, to: [], views: {}, borrowed: 0 }).get(g.workset);
    const n = Number(g.elements);
    r.borrowed += Number(g.borrowed ?? 0);
    let target = null, why = "";
    if (DATUM.test(g.category)) { target = datum ?? g.workset; why = "datum: the datum workset"; }
    else if (ruleOf(g.workset)?.manual) { r.manual += n; out.push({ ...g, state: "manual", target: g.workset }); continue; }
    else {
      const s = stdTarget(g, std?.rules);
      if (s) { target = s.workset; why = s.why; }
      else if (!std?.rules?.length || !std.strict) {
        const cur = ruleOf(g.workset), dflt = onDefault(g, protectedNames), zh = !dflt && contradicts(g, cur) ? zoneHold(g, cur, rules, ctx) : null;
        if (!dflt && !contradicts(g, cur)) { target = g.workset; why = "its workset's name fits it"; }
        else if (zh?.stay) { target = g.workset; why = zh.why; }
        else if (zh?.to) { target = zh.to; why = zh.why; }
        else { const pl = place(g, rules, groups, ctx); target = pl.workset; why = pl.reason; }
      }
    }
    const as = appliedState(g, applied, groups), rec = as?.rec;
    const ok = !!target && norm(target) === norm(g.workset);
    if (!target) r.noRule += n;
    else if (ok) r.correct += n;
    else { r.wrong += n; r.to.push({ to: target, elements: n, category: g.category, level: g.level ?? "", why, sampleIds: g.sampleIds ?? [] }); }
    // Four states: applied by ACE (it sits where ACE put it), changed after apply, correct already (not by ACE), still to do.
    const state = as?.kind === "applied" ? "applied" : as?.kind === "changed" ? "changed after apply" : ok ? "correct already" : "still to do";
    out.push({ ...g, target, why, state, ...(state === "applied" && target && !ok ? { differs: true } : {}), ...(rec ? { appliedAt: rec.at, planId: rec.planId, appliedTo: rec.to } : {}) });
  }
  const stdNames = new Set((std?.rules ?? []).filter((r) => r.workset && !/\{/.test(r.workset)).map((r) => norm(r.workset)));
  const wanted = new Set(out.filter((g) => g.target).map((g) => norm(g.target)));
  const rows = [...row.values()].map((r) => ({ ...r, empty: r.elements ? "" : wanted.has(norm(r.workset)) ? "empty, expected" : r.isDefault || r.protected ? "" : "empty, unused",
    inStandard: std?.rules?.length ? (stdNames.has(norm(r.workset)) || r.isDefault || r.protected) : null }));
  const missing = [...stdNames].filter((n) => !ws.some((w) => norm(w.name) === n)).map((n) => (std.rules.find((r) => norm(r.workset) === n) ?? {}).workset);
  const sum = (k) => rows.reduce((t, r) => t + Number(r[k] ?? 0), 0);
  const totals = { correct: sum("correct"), wrong: sum("wrong"), noRule: sum("noRule"), manual: sum("manual"), borrowed: sum("borrowed") };
  const state = (s) => out.filter((g) => g.state === s);
  const states = Object.fromEntries(["applied", "changed after apply", "correct already", "still to do"].map((s) => [s, { groups: state(s).length, elements: state(s).reduce((t, g) => t + Number(g.elements), 0) }]));
  const pct = totals.correct + totals.wrong ? Math.round((100 * totals.correct) / (totals.correct + totals.wrong)) : 100;
  return { model, pct, totals, states, rows, missing, groups: out, pending: (pending ?? []).filter((p) => !p.model || modelKey(p.model) === modelKey(model)), standard: standardText(std), records: records.length };
}
const lastApply = (s) => s.groups.filter((g) => g.state === "applied").sort((a, b) => String(b.appliedAt).localeCompare(String(a.appliedAt)))[0];
export function statusText(s, { files = [] } = {}) {
  const t = s.totals, st = s.states, la = lastApply(s);
  const lines = [`WORKSET STATUS for ${s.model} (read-only; standard: ${s.standard}): ${s.pct}% correctly assigned (${t.correct} of ${t.correct + t.wrong} element(s)); wrong ${t.wrong}; no rule ${t.noRule}; manual-only ${t.manual}; borrowed by others ${t.borrowed}; pending actions ${s.pending.length}.`];
  const differ = s.groups.filter((g) => g.differs);
  lines.push(`Groups: applied by ACE ${st.applied.groups}${la ? ` (last ${la.appliedAt}, plan ${la.planId ?? "?"})` : ""}${differ.length ? `, ${differ.length} of them now differ from the standard` : ""}; changed after apply ${st["changed after apply"].groups}; correct already ${st["correct already"].groups}; still to do ${st["still to do"].groups}.`);
  const wrong = s.rows.filter((r) => r.wrong);
  if (wrong.length) lines.push(`Wrong (where each should go):\n${wrong.slice(0, 30).map((r) => `- ${W.q(r.workset)}: ${r.wrong} wrong: ${r.to.slice(0, 6).map((x) => `${x.elements} ${x.category}${x.level ? ` @ ${x.level}` : ""} -> ${W.q(x.to)} (${x.why})`).join("; ")}`).join("\n")}`);
  if (wrong.length) lines.push("At apply, a moved element that a view on a sheet would show differently is held (stays) until the user agrees (allow_visibility_change).");
  if (differ.length) lines.push(`Applied by ACE, now differs from the standard:\n${differ.slice(0, 15).map((g) => `- ${g.elements} ${g.category}${g.level ? ` @ ${g.level}` : ""} on ${W.q(g.workset)} (ACE ${g.appliedAt}); the standard says ${W.q(g.target)} (${g.why})`).join("\n")}`);
  const changed = s.groups.filter((g) => g.state === "changed after apply");
  if (changed.length) lines.push(`Changed after apply (ACE moved them; flagged, not re-proposed):\n${changed.slice(0, 15).map((g) => `- ${g.elements} ${g.category}${g.level ? ` @ ${g.level}` : ""}: ACE moved to ${W.q(g.appliedTo)} on ${g.appliedAt}, now on ${W.q(g.workset)}`).join("\n")}`);
  const empty = s.rows.filter((r) => r.empty);
  if (empty.length) lines.push(`Empty: ${empty.map((r) => `${W.q(r.workset)} (${r.empty})`).join("; ")}.`);
  const notStd = s.rows.filter((r) => r.inStandard === false);
  if (notStd.length) lines.push(`Not in the standard: ${notStd.map((r) => W.q(r.workset)).join(", ")}.`);
  if (s.missing.length) lines.push(`In the standard, missing from the model: ${s.missing.map(W.q).join(", ")}.`);
  const hid = s.rows.filter((r) => r.elements && Object.values(r.views ?? {}).some((v) => v === false));
  if (hid.length) lines.push(`Hidden in an export view: ${hid.map((r) => `${W.q(r.workset)} (${Object.entries(r.views).filter(([, v]) => v === false).map(([v]) => v).join(", ")})`).join("; ")}.`);
  if (s.pending.length) lines.push(pendingText(s.pending));
  if (files.length) lines.push(`Files:\n${files.map((f) => `- ${f}`).join("\n")}`);
  return lines.join("\n");
}
export const STATUS_COLUMNS = ["Workset", "Elements", "Correct", "Wrong", "Should go to", "No rule", "Manual only", "Empty", "In the standard", "Open", "Visible by default", "Export views", "Borrowed"];
const yn = (v) => v == null ? "" : v ? "yes" : "no";
const statusRow = (r) => [r.workset, r.elements, r.correct, r.wrong, r.to.map((x) => `${x.elements} ${x.category} -> ${x.to}`).join(" | "), r.noRule, r.manual, r.empty, yn(r.inStandard), yn(r.open), yn(r.visibleByDefault),
  Object.entries(r.views ?? {}).map(([v, on]) => `${v}: ${on === false ? "HIDDEN" : on ? "visible" : "?"}`).join("; "), r.borrowed];
export const statusCsv = (s) => toCsv([STATUS_COLUMNS, ...s.rows.map(statusRow)]);
export function statusHtml(s, { when = "", brand = {} } = {}) {
  const body = `<tbody>${s.rows.map((r) => `<tr>${statusRow(r).map((c, i) => `<td${[1, 2, 3, 5, 6, 12].includes(i) ? ' class="num"' : ""}>${esc(c)}</td>`).join("")}</tr>`).join("")}</tbody>`;
  return brandedPage({ title: `Workset status - ${s.model}`, heading: "Workset Status", brand,
    meta: [["Model", esc(s.model)], ["Date", esc(when)], ["Standard", esc(s.standard)], ["Correctly assigned", `${s.pct}%`],
      ["Elements", chips({ correct: s.totals.correct, wrong: s.totals.wrong, "no rule": s.totals.noRule, borrowed: s.totals.borrowed })],
      ["Groups", chips(Object.fromEntries(Object.entries(s.states).map(([k, v]) => [k === "applied" ? "applied by ACE" : k, v.groups])))],
      ["Missing from the model", esc(s.missing.join(", ") || "none")], ["Pending actions", String(s.pending.length)]],
    head: STATUS_COLUMNS.map((c, i) => [1, 2, 3, 5, 6, 12].includes(i) ? `#${c}` : c), body: s.rows.length ? body : "", empty: "No worksets",
    footer: `Generated by ACE (read-only status of ${esc(s.model)}). Nothing was changed.` });
}
/** The dashboard tile (model_dashboard reads the latest one). */
export const statusTile = (s, at) => ({ id: "worksets", title: "Worksets", value: `${s.pct}% correct`, model: s.model, at, standard: s.standard, wrong: s.totals.wrong, pending: s.pending.length, states: s.states });

/**
 * reset: "default" (move everything to the default workset first, then the rules), computed as the END STATE so each
 * element moves at most once, straight from its current workset to its final one: a group the plan moves keeps that
 * target; a group the Groups sheet skipped stays; the datum workset, manual-only worksets and datum categories stay;
 * every other group goes where the confirmed rules place it, else to the default workset. Borrowed elements are
 * planned and skipped by the add-in (pending). -> { plan, defaultName, endState, toDefault, stay, borrowed }
 */
export function resetPlan(pf, facts, { manualOnly = [], keep = [], ...opts } = {}) {
  const keepSet = new Set((keep ?? []).map(twinKey));
  const R = pf.rules ?? [];
  const ws = facts?.worksets ?? [];
  const groups = (facts?.groups ?? []).filter(modelGroup).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const ren = new Map((pf.plan.rename ?? []).map((r) => [norm(r.from), r.to]));
  const fin = (n) => ren.get(norm(n)) ?? n;
  const dflt = ws.find((w) => w.isDefault || W.isDefaultName(w.name))?.name ?? "Workset1";
  const defaultName = fin(dflt);
  const manual = new Set([...R.filter((r) => r.manual).flatMap((r) => [r.from, r.workset, ...(r.twins ?? [])]), ...manualOnly, ...ws.filter((w) => words(w.name).some((x) => MANUAL.has(x))).map((w) => w.name)].filter(Boolean).map(norm));
  const planned = new Map((pf.plan.moves ?? []).map((m) => [m.key, m]));
  const skip = new Set(pf.skipKeys ?? []);
  const plan = { create: [...(pf.plan.create ?? [])], rename: [...(pf.plan.rename ?? [])], moves: [], delete: [] };
  const stay = { datum: 0, manual: 0, skipped: 0, onDefault: 0 };
  let toDefault = { groups: 0, elements: 0 }, borrowed = 0;
  const keepKeys = [];   // groups that stay where they are: the add-in's reset never touches them
  for (const g of groups) {
    const n = Number(g.elements), cur = fin(g.workset);
    if (manual.has(norm(g.workset))) { stay.manual += n; keepKeys.push(g.key); continue; }
    if (DATUM.test(g.category)) { stay.datum += n; keepKeys.push(g.key); continue; }
    if (skip.has(g.key)) { stay.skipped += n; keepKeys.push(g.key); continue; }
    let to = planned.get(g.key)?.to ?? null, row = planned.get(g.key)?.row;
    // zoneWins: what sits on a zone / asset workset stays (or goes to the one rule naming that zone and its category).
    const curR = R.find((r) => norm(r.workset) === norm(cur) || [r.from, ...(r.twins ?? [])].some((n) => n && norm(n) === norm(g.workset)));
    if (!to && curR && opts.zoneWins !== false && isZoneWs(curR)) {
      const zh = contradicts(g, curR) ? zoneHold(g, curR, R, opts) : { stay: true };
      if (zh?.stay) { stay.zone = (stay.zone ?? 0) + n; keepKeys.push(g.key); continue; }
      if (zh?.to) to = zh.to;
    }
    if (!to) { const pl = place(g, R, groups, opts); to = pl.workset; row = R.find((r) => norm(r.workset) === norm(to ?? ""))?.line; }
    if (!to) {
      if (isProt(ws.find((w) => w.name === g.workset))) { stay.datum += n; keepKeys.push(g.key); continue; }
      // A workset kept by a confirmed decision is never emptied by a reset: what no rule takes elsewhere stays on it.
      if (keepSet.has(twinKey(g.workset))) { stay.kept = (stay.kept ?? 0) + n; keepKeys.push(g.key); continue; }
      to = defaultName;
      if (norm(to) === norm(cur)) { stay.onDefault += n; keepKeys.push(g.key); continue; }
      toDefault.groups++; toDefault.elements += n;
    }
    if (norm(to) === norm(cur)) { keepKeys.push(g.key); continue; }
    if (Number(g.borrowed ?? 0)) borrowed += Number(g.borrowed);
    plan.moves.push(moveOf(g, cur, to, row));
  }
  const names = new Set(ws.map((w) => norm(fin(w.name))));
  for (const m of plan.moves) if (!names.has(norm(m.to)) && !plan.create.some((c) => norm(c) === norm(m.to))) plan.create.push(m.to);
  plan.delete = (pf.plan.delete ?? []).filter((d) => !plan.moves.some((m) => norm(m.to) === norm(d)));
  return { plan, defaultName, endState: endState(facts, plan), toDefault, stay, borrowed, keepKeys, manualWorksets: ws.filter((w) => manual.has(norm(w.name))).map((w) => w.name) };
}
/** Elements per workset before and after a plan (final names; renames applied). */
export function endState(facts, plan) {
  const ren = new Map((plan.rename ?? []).map((r) => [norm(r.from), r.to]));
  const fin = (n) => ren.get(norm(n)) ?? n;
  const m = new Map();
  const at = (n) => m.get(norm(n)) ?? m.set(norm(n), { workset: n, before: 0, after: 0 }).get(norm(n));
  for (const w of facts?.worksets ?? []) { const e = at(fin(w.name)); e.before += Number(w.elements ?? 0); e.after += Number(w.elements ?? 0); }
  for (const c of plan.create ?? []) at(c);
  for (const mv of plan.moves ?? []) { at(mv.from).after -= Number(mv.elements); at(mv.to).after += Number(mv.elements); }
  for (const d of plan.delete ?? []) at(d).deleted = true;
  return [...m.values()];
}
export const endStateText = (es) => `End state per workset (before -> after):\n${es.filter((e) => e.before || e.after || e.deleted).map((e) => `- ${W.q(e.workset)}: ${e.before} -> ${e.after}${e.deleted ? " (deleted)" : ""}${e.before !== e.after ? ` (${e.after > e.before ? "+" : ""}${e.after - e.before})` : ""}`).join("\n")}`;

/**
 * Partial apply: only { rows, worksets, categories, models, keys } of the confirmed plan. Moves are kept when they match
 * every given filter; renames a kept move needs (it names the final workset) stay, as do renames, creates and deletes of
 * the named worksets or rows. -> { plan, kept, dropped, outOfModel }
 */
export function filterPlan(plan, only, model) {
  if (!only) return { plan, kept: plan.moves.length, dropped: 0 };
  if (only.models?.length && !only.models.some((m) => modelKey(m) === modelKey(model))) return { plan: finalPlan(), kept: 0, dropped: plan.moves.length, outOfModel: true };
  const rows = only.rows?.length ? new Set(only.rows.map(String)) : null;
  const wsSet = only.worksets?.length ? new Set(only.worksets.map(norm)) : null;
  const cats = only.categories?.length ? only.categories : null;
  const keys = only.keys?.length ? new Set(only.keys) : null;
  const moves = plan.moves.filter((m) => (!rows || rows.has(String(m.row))) && (!wsSet || wsSet.has(norm(m.to)) || wsSet.has(norm(m.from)))
    && (!cats || catIs(m.category, cats)) && (!keys || keys.has(m.key)));
  const used = new Set(moves.flatMap((m) => [norm(m.from), norm(m.to)]));
  const named = (n) => !!wsSet?.has(norm(n));
  const rename = plan.rename.filter((r) => used.has(norm(r.to)) || named(r.from) || named(r.to));
  const create = plan.create.filter((c) => moves.some((m) => norm(m.to) === norm(c)) || named(c));
  const del = plan.delete.filter((d) => named(d) && !moves.some((m) => norm(m.to) === norm(d)));
  return { plan: { create, rename, moves, delete: del }, kept: moves.length, dropped: plan.moves.length - moves.length };
}
/**
 * Moves already done (the group's key is gone and its elements sit on the target now: an earlier apply) are dropped;
 * apply says so. -> { plan, already: [{ key, to, elements }] }
 */
export function skipDone(plan, facts) {
  const groups = (facts?.groups ?? []).map((g) => ({ ...g, key: g.key ?? groupKey(g) }));
  const keys = new Set(groups.map((g) => g.key));
  const already = [];
  const now = new Map(groups.map((g) => [g.key, Number(g.elements)]));
  const moves = plan.moves.map((m) => {
    if (keys.has(m.key) && now.get(m.key) < Number(m.elements)) {
      // Partly done (an earlier apply moved the rest; borrowed ones stayed): the move is for what is left.
      const there = groups.filter((g) => identity(g) === identity(m) && norm(g.workset) === norm(m.to)).reduce((n, g) => n + Number(g.elements), 0);
      if (there + now.get(m.key) >= Number(m.elements)) { already.push({ key: m.key, to: m.to, elements: Number(m.elements) - now.get(m.key), partly: true }); return { ...m, elements: now.get(m.key) }; }
    }
    return m;
  }).filter((m) => {
    if (keys.has(m.key)) return true;
    const there = groups.filter((g) => identity(g) === identity(m) && norm(g.workset) === norm(m.to)).reduce((n, g) => n + Number(g.elements), 0);
    if (there >= Number(m.elements)) { already.push({ key: m.key, to: m.to, elements: Number(m.elements) }); return false; }
    return true;
  });
  return { plan: { ...plan, moves, delete: plan.delete }, already };
}

/** Pending (borrowed) actions: the add-in's pending [{ id, owner, from, to }], merged by element id. */
export function mergePending(old = [], add = [], { model, done = [], at } = {}) {
  const m = new Map((old ?? []).map((p) => [String(p.id), p]));
  for (const id of done ?? []) m.delete(String(id));
  for (const p of add ?? []) if (p?.id != null) m.set(String(p.id), { ...m.get(String(p.id)), ...p, model: p.model ?? model, since: m.get(String(p.id))?.since ?? at });
  return [...m.values()];
}
export function pendingText(list, { planFile } = {}) {
  if (!list?.length) return "";
  const by = new Map();
  for (const p of list) { const k = `${p.owner ?? "?"}|${p.to ?? "?"}`; by.set(k, [...(by.get(k) ?? []), p]); }
  return `Pending actions (borrowed by others: move these once they are relinquished; the next apply retries them first):\n${[...by.values()].slice(0, 20).map((ps) => `- ${ps.length} element(s) held by ${ps[0].owner ?? "?"} -> ${W.q(ps[0].to ?? "?")}: ids ${ps.slice(0, 12).map((p) => p.id).join(", ")}${ps.length > 12 ? ", ..." : ""}`).join("\n")}${planFile ? `\nFollow-up: workset_plan { action: "apply", plan_file: ${JSON.stringify(planFile)}, dry_run: true } (pending ids first), then the identical call without dry_run.` : ""}`;
}

/**
 * Refresh: the new proposal against the previous file. Earlier decisions are carried over (rule rows by workset name,
 * group rows by key, else model + current workset + category + type + level); each row is marked new, changed (count
 * or target differs), same, or gone (appended, decision kept for the record). Applied groups are not proposed again.
 */
export const SINCE = "Since last proposal";
export function refresh(p, tables) {
  const oldR = tables.find((t) => t.layout === "rules")?.rows ?? [], oldG = tables.find((t) => t.layout === "groups")?.rows ?? [];
  const rk = (n) => twinKey(n);
  const oldRules = new Map(oldR.map((r) => [rk(r.proposed), r]));
  const gk = (r) => [modelKey(r.model ?? r.Model), norm(r.current ?? r["Current workset"]), norm(r.category ?? r.Category), typeKey(r.type ?? r["Type / link / contents"]), norm(r.level ?? r.Level)].join("|");
  const oldGroups = new Map(oldG.map((r) => [r.key || gk(r), r]));
  const oldByLoose = new Map(oldG.map((r) => [gk(r), r]));
  const seenR = new Set(), seenG = new Set();
  const counts = { new: 0, changed: 0, same: 0, gone: 0, kept: 0 };
  const mark = (row, old, changed) => { const s = !old ? "new" : changed ? "changed" : "same"; counts[s]++; if (old && old.decision) { row[DECISION] = old.decision; counts.kept++; } row[SINCE] = s + (changed ? `: ${changed}` : ""); };
  for (const row of p.rules) {
    const old = oldRules.get(rk(row["Proposed workset"])) ?? (row.Rule ? oldRules.get(rk(JSON.parse(row.Rule).workset)) : null);
    if (old) seenR.add(old);
    const diff = old ? [String(old.action) !== String(row.Action) ? `action ${old.action} -> ${row.Action}` : ""].filter(Boolean).join("; ") : "";
    mark(row, old, diff);
  }
  for (const row of p.detail) {
    const old = oldGroups.get(row.Key) ?? oldByLoose.get(gk(row));
    if (old) seenG.add(old);
    const diff = old ? [String(old.elements).trim() !== "" && Number(old.elements) !== Number(row.Elements) ? `count ${old.elements} -> ${row.Elements}` : "", norm(old.proposed) !== norm(row["Proposed workset"]) ? `target ${old.proposed || "(none)"} -> ${row["Proposed workset"] || "(none)"}` : ""].filter(Boolean).join("; ") : "";
    mark(row, old, diff);
  }
  for (const r of oldR.filter((x) => !seenR.has(x))) { counts.gone++; p.rules.push({ Group: r.group || "Gone", "Proposed workset": r.proposed, Action: r.action, Models: r.models, "What it holds (from its name)": "", "Holds now": "", "Would receive": "", "Open / visibility": "", Note: "no longer proposed", [DECISION]: r.decision, Rule: r.rule, [SINCE]: "gone" }); }
  for (const r of oldG.filter((x) => !seenG.has(x))) { counts.gone++; p.detail.push({ Model: r.model, Row: r.row, "Current workset": r.current, Category: r.category, "Type / link / contents": r.type, Level: r.level, Elements: r.elements, "Proposed workset": r.proposed, "Proposed action": r.action, Reason: "no longer proposed (moved, applied or deleted)", Confidence: "", [DECISION]: r.decision, Key: r.key, [SINCE]: "gone" }); }
  p.refreshed = counts;
  return p;
}

/** Pending entries in an only filter's scope (by target or origin workset, category, model); rows / keys alone: none. */
export function pendingInScope(list, only, model) {
  if (!only) return list;
  if (only.models?.length && !only.models.some((m) => modelKey(m) === modelKey(model))) return [];
  if (!only.worksets?.length && !only.categories?.length) return [];
  const ws = only.worksets?.length ? new Set(only.worksets.map(norm)) : null;
  return list.filter((p) => (!ws || ws.has(norm(p.to)) || ws.has(norm(p.from))) && (!only.categories?.length || (p.category && catIs(p.category, only.categories))));
}
