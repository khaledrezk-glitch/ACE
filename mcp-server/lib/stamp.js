// Run record stamp (backlog T13): every run record and the master report say which ACE made them (the add-in and this
// server) and over which rules: a rules hash, SHA-256 over canonical JSON of the rule and requirement inputs the run
// used (object keys sorted, no whitespace, line breaks in strings as LF), so it is the same on every machine and for
// CRLF or LF files. The add-in's Util/RulesStamp.cs writes the same form; test/fixtures/rules-hash.json is the shared
// test vector.
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { VERSION, addinVersion } from "./core.js";

/** Canonical JSON text of a value (undefined object members are left out, as JSON.stringify does). */
export function canonical(v) {
  if (v === null || v === undefined || typeof v === "function") return "null";
  if (typeof v === "string") return JSON.stringify(v.replace(/\r\n?/g, "\n"));
  if (typeof v === "number") return Number.isFinite(v) ? String(v) : "null";
  if (typeof v === "boolean") return String(v);
  if (Array.isArray(v)) return `[${v.map(canonical).join(",")}]`;
  if (typeof v.toJSON === "function") return canonical(v.toJSON());
  const keys = Object.keys(v).filter((k) => v[k] !== undefined && typeof v[k] !== "function").sort();
  return `{${keys.map((k) => `${JSON.stringify(k.replace(/\r\n?/g, "\n"))}:${canonical(v[k])}`).join(",")}}`;
}

/** Lower-case SHA-256 hex of the canonical JSON of inputs. */
export const rulesHash = (inputs) => crypto.createHash("sha256").update(canonical(inputs), "utf8").digest("hex");

/** The first 12 hex of a rules hash, as the report shows it. */
export const short = (h) => (h ? String(h).slice(0, 12) : null);

const readJson = (f) => { try { return JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")); } catch { return undefined; } };

/**
 * The vault's rule and requirement inputs: project-requirements.json (the rules ACE checks and their bindings),
 * requirements.json (records), decisions.json (decisions) and every rules/**\/*.json (export, IFC setups, mappings),
 * keyed by vault-relative path with "/" so the names are the same on Windows and Linux. Missing files are left out.
 */
export function vaultInputs(vaultDir) {
  const out = {};
  if (!vaultDir || !fs.existsSync(vaultDir)) return out;
  const pr = readJson(path.join(vaultDir, "project-requirements.json"));
  if (pr !== undefined) out["project-requirements"] = pr;
  const req = readJson(path.join(vaultDir, "requirements.json"));
  if (req !== undefined) out.requirements = req.records ?? req.requirements ?? [];
  const dec = readJson(path.join(vaultDir, "decisions.json"));
  if (dec !== undefined) out.decisions = dec.decisions ?? [];
  const walk = (dir, rel) => {
    let entries = [];
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))) {
      const r = `${rel}/${e.name}`;
      if (e.isDirectory()) walk(path.join(dir, e.name), r);
      else if (/\.json$/i.test(e.name)) { const j = readJson(path.join(dir, e.name)); if (j !== undefined) out[r] = j; }
    }
  };
  walk(path.join(vaultDir, "rules"), "rules");
  return out;
}

/**
 * The stamp for a run record: { aceVersion, mcpVersion, rulesHash, rulesInputs }. inputs: name -> what the run used
 * (vaultInputs plus anything the run read elsewhere, e.g. a plan or a rules file). aceVersion: the add-in that answered
 * (default: the session last called).
 */
export function stamp(inputs = {}, { aceVersion = addinVersion() } = {}) {
  const used = Object.fromEntries(Object.entries(inputs).filter(([, v]) => v !== undefined));
  return { aceVersion: aceVersion ?? null, mcpVersion: VERSION, rulesHash: rulesHash(used), rulesInputs: Object.keys(used).sort() };
}

/** "ACE 1.15.0 · rules 0123456789ab" (the report's header and footer line). */
export function label(s) {
  if (!s) return "ACE (version not recorded) · rules (not recorded)";
  const v = s.aceVersion ?? s.mcpVersion;
  return `ACE ${v ?? "(version not recorded)"} · rules ${short(s.rulesHash) ?? "(not recorded)"}`;
}
