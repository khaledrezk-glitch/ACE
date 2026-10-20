// Shared paths, configuration and the HTTP client for the Revit add-in bridge.
import fs from "node:fs";
import http from "node:http";
import { randomUUID } from "node:crypto";
import { AsyncLocalStorage } from "node:async_hooks";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const VERSION = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8")).version;
export const APPDATA = process.env.APPDATA || path.join(os.homedir(), ".config");
export const DATA_DIR = path.join(APPDATA, "ACE-RevitMCP");
export const CONFIG_PATH = process.env.ACE_REVIT_CONFIG || path.join(DATA_DIR, "config.json");
export const BUILTIN_SCRIPTS = path.join(ROOT, "scripts");
export const GUIDES_DIR = path.join(ROOT, "guides");
export const USER_SCRIPTS = process.env.ACE_REVIT_SCRIPTS || path.join(DATA_DIR, "scripts");
export const JOURNAL_DIR = process.env.ACE_REVIT_JOURNAL || path.join(DATA_DIR, "journal");
export const LOG_DIR = process.env.ACE_REVIT_LOGS || path.join(DATA_DIR, "logs");
export const REPORTS_DIR = process.env.ACE_REVIT_REPORTS || path.join(DATA_DIR, "reports");
// About 15k tokens: one reply must not use up a large part of the conversation. Ask for less (limits, filters) instead.
export const MAX_TEXT = 60_000;

export class RevitError extends Error {}

/** The MCP request being handled (its cancel signal), so every bridge call it makes stops when the client cancels. */
export const currentCall = new AsyncLocalStorage();

/**
 * POST to the local bridge with node:http. Not fetch: Node's fetch gives up waiting for the answer after 300 s, and a
 * clash run or a large workset move can take far longer. Our own timeout and the caller's cancel signal end the wait;
 * every listener and timer is removed when the call ends.
 */
function post(url, headers, body, { timeoutMs, signal }) {
  return new Promise((resolve, reject) => {
    const fail = (name, message) => { const e = new Error(message); e.name = name; return e; };
    if (signal?.aborted) return reject(fail("AbortError", "cancelled"));
    const req = http.request(url, { method: "POST", headers: { ...headers, "Content-Length": Buffer.byteLength(body) } }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => { done(); resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString("utf8") }); });
      res.on("error", (err) => { done(); reject(err); });
    });
    const timer = setTimeout(() => req.destroy(fail("TimeoutError", "timed out")), timeoutMs);
    const onAbort = () => req.destroy(fail("AbortError", "cancelled"));
    signal?.addEventListener("abort", onAbort, { once: true });
    function done() { clearTimeout(timer); signal?.removeEventListener("abort", onAbort); }
    req.on("error", (err) => { done(); reject(Object.assign(err, { cause: { code: err.code } })); });
    req.end(body);
  });
}

/**
 * config.json (written by the installer / add-in):
 * { "port": 48884, "token": "...", "teamScriptsDir": "\\\\server\\share\\ACE\\scripts",
 *   "teamReportsDir": "...", "teamName": "ACE" }
 */
export function readConfig() {
  try {
    const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8").replace(/^﻿/, ""));
    return { port: cfg.port || 48884, token: cfg.token || "", ...cfg };
  } catch {
    return null;
  }
}

export const teamScriptsDir = () => process.env.ACE_TEAM_SCRIPTS || readConfig()?.teamScriptsDir || null;
export const teamReportsDir = () => process.env.ACE_TEAM_REPORTS || readConfig()?.teamReportsDir || null;

export const INSTANCES_DIR = process.env.ACE_REVIT_INSTANCES || path.join(DATA_DIR, "instances");
/** The ports the add-in tries, in order: config.port and the nine after it. */
export const portsFrom = (base) => Array.from({ length: 10 }, (_, i) => (Number(base) || 48884) + i);

/** ACE_REVIT_URL (override), else the chosen Revit session's port, else config.port. */
export function bridgeUrl(cfg = readConfig(), port = undefined) {
  return process.env.ACE_REVIT_URL || `http://localhost:${port || cfg?.port || 48884}`;
}

async function healthAt(base, timeoutMs) {
  try {
    const r = await fetch(`${base}/health`, { signal: AbortSignal.timeout(timeoutMs) });
    return await r.json();
  } catch (err) {
    const name = err?.name ?? "";
    return { ok: false, timeout: name === "TimeoutError" || name === "AbortError", error: err?.cause?.code || name || String(err) };
  }
}

/** The port the Revit session with that pid listens on now, from its instance file (null when unknown). */
export function portOfPid(pid) {
  if (pid == null) return null;
  try {
    const info = JSON.parse(fs.readFileSync(path.join(INSTANCES_DIR, `${Number(pid)}.json`), "utf8").replace(/^\uFEFF/, ""));
    return Number(info?.pid) === Number(pid) && info?.port ? info.port : null;
  } catch { return null; }
}

/** /health of the Revit session on that port (the configured one when port is empty). */
export const healthAtPort = (port, timeoutMs = 2000) => healthAt(bridgeUrl(readConfig(), port), timeoutMs);

export async function health(timeoutMs = 4000) {
  const s = process.env.ACE_REVIT_URL ? null : (await chooseInstance().catch(() => null))?.chosen;
  return healthAt(bridgeUrl(readConfig(), s?.port), timeoutMs);
}

const alive = (pid) => { try { process.kill(pid, 0); return true; } catch (err) { return err?.code === "EPERM"; } };

/**
 * The running Revit sessions with the ACE add-in -> { list, notes }. Each add-in writes instances/<pid>.json { port, pid,
 * machine, revitVersion, aceVersion, started, updated, activeDocument, openDocuments, user }; a file counts when /health on
 * its port answers the same pid. A file is deleted only when it is ours to judge (name <pid>.json, written on this
 * machine) and provably stale (the pid does not run here, or /health answers another pid); a slow answer only skips it
 * for this call. No live file (an older add-in): /health on config.port and the nine ports after it.
 */
export async function listInstances({ timeoutMs = 1500 } = {}) {
  const notes = [];
  let files = [];
  if (fs.existsSync(INSTANCES_DIR)) {
    if (!fs.statSync(INSTANCES_DIR).isDirectory()) notes.push(`${INSTANCES_DIR} is not a folder: Revit sessions are found by their ports only.`);
    else { try { files = fs.readdirSync(INSTANCES_DIR).filter((f) => /^\d+\.json$/.test(f)); } catch (err) { notes.push(`Cannot read ${INSTANCES_DIR}: ${err.message}`); } }
  }
  const found = [];
  await Promise.all(files.map(async (f) => {
    const file = path.join(INSTANCES_DIR, f);
    let info;
    try { info = JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/, "")); } catch { return; }
    if (!info?.port || !info?.pid) return;
    const local = info.machine != null && String(info.machine).toLowerCase() === os.hostname().toLowerCase();
    const remove = () => { if (local && f === `${Number(info.pid)}.json`) { try { fs.unlinkSync(file); } catch { /* removed meanwhile */ } } };
    if (local && !alive(info.pid)) return remove();
    const h = await healthAt(`http://localhost:${info.port}`, timeoutMs);
    if (h?.timeout) { notes.push(`Revit session pid ${info.pid} (port ${info.port}) did not answer in time; skipped for now.`); return; }
    if (h?.ok && (h.pid == null || Number(h.pid) === Number(info.pid))) { found.push({ ...info, source: "file" }); return; }
    if (h?.ok) remove();   // another process answers on that port: the file is stale
  }));
  if (!found.length) {
    await Promise.all(portsFrom(readConfig()?.port).map(async (port) => {
      const h = await healthAt(`http://localhost:${port}`, 600);
      if (h?.ok && h.service === "ace-revit-mcp") found.push({ port, pid: h.pid ?? null, revitVersion: h.revitVersion, machine: os.hostname(), source: "probe" });
    }));
  }
  return { list: found.sort((a, b) => String(b.updated ?? "").localeCompare(String(a.updated ?? "")) || a.port - b.port), notes };
}

let preferred = null;   // a model title, pid or port the user chose (revit_status instance), kept for this server process
export const preferInstance = (hint) => { preferred = hint ? String(hint) : null; };
export const preferredInstance = () => preferred;

const title = (d) => String(d ?? "").trim().toLowerCase().replace(/\.rvt$/, "");
/**
 * Sessions matching a hint: pid or port, else an exact model title (active document first, then any open one). Only
 * with substring: true a part of a title matches (a fallback, reported with a note; never for expect_model).
 */
export function matchInstances(list, hint, { substring = false } = {}) {
  if (!hint) return [];
  const h = title(hint);
  const byId = list.filter((s) => String(s.pid) === h || String(s.port) === h);
  if (byId.length) return byId;
  const active = list.filter((s) => title(s.activeDocument) === h);
  if (active.length) return active;
  const open = list.filter((s) => (s.openDocuments ?? []).some((d) => title(d) === h));
  if (open.length || !substring) return open;
  return list.filter((s) => [s.activeDocument, ...(s.openDocuments ?? [])].some((d) => d && title(d).includes(h)));
}

/**
 * Pure choice: a hint match (the most recently updated first), else the only session, else the only one with a model
 * open, else (no model anywhere) the most recent. Several sessions with a model and nothing to decide: no choice
 * (ambiguous: true), the note lists them; ACE asks rather than guesses.
 */
export function pickInstance(list, hint, { exact = false } = {}) {
  if (!list.length) return { chosen: null, list, note: null };
  let m = matchInstances(list, hint);
  let note = null;
  if (!m.length && hint && !exact) {
    m = matchInstances(list, hint, { substring: true });
    if (m.length) note = `No Revit session has a model titled exactly "${hint}"; using ${describeInstance(m[0])}, whose model title contains it.`;
  }
  if (m.length) return { chosen: m[0], list, note };
  if (list.length === 1) return { chosen: list[0], list, note: hint ? `No Revit session has "${hint}" open; using the only one (${describeInstance(list[0])}).` : null };
  const none = hint ? ` (none has "${hint}" open)` : "";
  const how = "To use another: revit_status with instance (its model title or pid).";
  const withModel = list.filter(hasModel);
  if (withModel.length > 1) return { chosen: null, list, ambiguous: true, note: `${withModel.length} Revit sessions have a model open${none}: ${withModel.map(describeInstance).join("; ")}. Which one? Say it with revit_status instance (a model title or pid); ACE does not guess.` };
  const [chosen, ...others] = withModel.length ? [withModel[0], ...list.filter((s) => s !== withModel[0])] : list;
  return { chosen, list, note: `${list.length} Revit sessions are running; using ${describeInstance(chosen)}${withModel.length ? ", the only one with a model open" : ""}${none}. Others: ${others.map(describeInstance).join("; ")}. ${how}` };
}
const hasModel = (s) => !!(s.activeDocument || s.openDocuments?.length);
export const describeInstance = (s) => s ? `Revit ${s.revitVersion ?? "?"} pid ${s.pid ?? "?"} port ${s.port}${s.activeDocument ? `, active "${s.activeDocument}"` : ""}${s.user ? `, ${s.user}` : ""}` : "the configured Revit";

let cached = null;
/** Drops the cached session listing (the next call lists again). */
export const forgetInstances = () => { cached = null; };
/** The session for a call (listing cached for 5 s). With ACE_REVIT_URL set there is no choice to make. */
export async function chooseInstance(hint = undefined, { exact = false } = {}) {
  if (process.env.ACE_REVIT_URL) return { chosen: null, list: [], note: null };
  let listNotes = [];
  try {
    if (!cached || Date.now() - cached.at > 5000) { const r = await listInstances(); cached = { at: Date.now(), list: r.list, notes: r.notes }; }
    listNotes = cached.notes ?? [];
  } catch (err) {
    return { chosen: null, list: [], note: `Could not list the Revit sessions (${err.message}); using config.json's port.` };
  }
  const pick = pickInstance(cached.list, hint ?? preferred, { exact: exact && hint != null });
  const note = [...listNotes, pick.note].filter(Boolean).join("\n") || null;
  return { ...pick, note };
}

/** A session's identity for the preview gate (machine, pid, port); the URL override or config port when none is chosen. */
export const sessionKey = (s) => s ? `${s.machine ?? os.hostname()}|${s.pid ?? "?"}|${s.port}` : `url|${bridgeUrl()}`;
export const sessionLabel = (s) => s ? `${s.pid ?? "?"} (${s.activeDocument ?? "no model"}, port ${s.port})` : bridgeUrl();

/** The session this tool call uses: the pinned one, else chosen now (a hint is matched exactly when exact). */
export async function resolveSession(hint = undefined, { exact = false } = {}) {
  const store = currentCall.getStore();
  if (store && Object.hasOwn(store, "pinned")) return store.pinned;
  const pick = await chooseInstance(hint, { exact });
  if (pick.ambiguous) throw new RevitError(pick.note);
  if (pick.note) store?.notes?.add(pick.note);
  return pick.chosen;
}
/** Pins one session for the rest of this tool call (check, dry run and apply go to the same Revit). */
export async function pinSession(hint = undefined, opts = {}) {
  const s = await resolveSession(hint, opts);
  const store = currentCall.getStore();
  if (store) store.pinned = s;
  return s;
}

export async function callRevit(command, args = {}, timeoutSeconds = 120, signal = undefined) {
  const cfg = readConfig();
  if (!cfg) {
    throw new RevitError(
      `Cannot find ${CONFIG_PATH}. Start Revit (2024 or 2025) once with the ACE add-in installed (run install.ps1), then try again.`,
    );
  }
  const chosen = await resolveSession(args?.expect_model, { exact: true });
  const base = bridgeUrl(cfg, chosen?.port);
  let response;
  const clientSignal = signal ?? currentCall.getStore()?.signal;
  const requestId = randomUUID();   // a cancel reaches exactly this request in Revit
  try {
    // The add-in answers within the timeout (it reports its own timeouts); this is only the backstop. The client's
    // cancel stops the wait here; a request Revit has not started yet is then skipped by the add-in.
    response = await post(`${base}/command`, { "Content-Type": "application/json", "X-Ace-Token": cfg.token },
      JSON.stringify({ command, args, timeoutSeconds, requestId }), { timeoutMs: (timeoutSeconds + 15) * 1000, signal: clientSignal });
  } catch (err) {
    const reason = err?.cause?.code || err?.name || err?.message;
    const cancelledByClient = clientSignal?.aborted;
    if (cancelledByClient) {
      // Tell Revit too: the running script or clash run stops where it checks, queued requests are dropped.
      post(`${base}/cancel`, { "Content-Type": "application/json", "X-Ace-Token": cfg.token }, JSON.stringify({ id: requestId }), { timeoutMs: 5000 }).catch(() => {});
      throw new RevitError(
        `'${command}' was cancelled. If Revit had not started it, nothing happened; if it was running, it stops where it ` +
          "can and rolls back. If it had already finished, it may have been applied: check with a read-only query.",
      );
    }
    if (err?.name === "TimeoutError" || err?.name === "AbortError") {
      throw new RevitError(
        `'${command}' did not answer in time. Revit may still be working on it: do NOT send it again. ` +
          "Call revit_status in a minute; it shows whether Revit is busy and with what.",
      );
    }
    throw new RevitError(
      `Revit is not reachable at ${base} (${reason}). Make sure Revit (2024 or 2025) is open with a model; ` +
        `the ACE tab > "MCP Status" shows whether the connection is running. Call check_setup for a full diagnosis.`,
    );
  }
  let payload;
  try {
    payload = JSON.parse(response.text);
  } catch {
    throw new RevitError(`Revit bridge returned HTTP ${response.status} with a non-JSON body.`);
  }
  if (response.status === 401) {
    throw new RevitError(`${payload.error} The token in ${CONFIG_PATH} does not match the running add-in: restart Revit, or run doctor.ps1 -Fix.`);
  }
  if (!payload.ok) throw new RevitError(payload.error || `Revit bridge error (HTTP ${response.status})`);
  // The ACE build that answered (the add-in's build id): a preview is bound to it (safety.js gated).
  const store = currentCall.getStore();
  if (store && payload.build) store.build = payload.build;
  return payload.result;
}

/** Today's date (or the given time's) in local time, YYYY-MM-DD: journal and report files follow the user's day, not UTC. */
/** YYYY-MM-DD in local time; with time: true, YYYY-MM-DD_HHMMSS (file name stamps). */
export function localDate(d = new Date(), { time = false } = {}) {
  const p = (n) => String(n).padStart(2, "0");
  const day = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  return time ? `${day}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}` : day;
}

/**
 * A tool reply. JSON goes out compact (indentation costs about a quarter more tokens and helps nobody). A reply that
 * is too long has its longest lists shortened, so it stays valid JSON and keeps every field; plain text is cut.
 */
export function text(value) {
  if (typeof value === "string") {
    const s = value.length > MAX_TEXT ? value.slice(0, MAX_TEXT) + `\n... [cut: ${value.length - MAX_TEXT} more characters]` : value;
    return { content: [{ type: "text", text: s }] };
  }
  return { content: [{ type: "text", text: fit(value) }] };
}

export function fit(value, max = MAX_TEXT) {
  let s = JSON.stringify(value ?? null);
  if (s.length <= max) return s;
  const box = { root: JSON.parse(s) };
  const notes = new Set();          // the "N more not shown" notes we add (never counted as data)
  const lost = new Map();           // shortened array -> items it has lost so far
  for (let round = 0; round < 60 && s.length > max; round++) {
    const lists = arrays(box, notes);
    if (lists.length === 0) break;
    // Shorten the lists that carry the most data of their own (not counting lists inside them), all of the largest
    // ones at once, so e.g. 20 warning types with 3000 ids each lose ids, not warning types.
    const top = Math.max(...lists.map((l) => l.own));
    for (const { owner, key, items } of lists.filter((l) => l.own >= top / 2)) {
      const data = items.filter((x) => !notes.has(x));
      const keep = Math.max(3, Math.floor(data.length / 2));
      const shortened = data.slice(0, keep);
      const gone = data.length - keep + (lost.get(items) || 0);
      lost.set(shortened, gone);
      const note = `${gone} more not shown - ask for less (limit, filter) to see them`;
      if (Array.isArray(owner) || owner === box) { shortened.push(note); notes.add(note); }
      else owner[`${key}Truncated`] = note;
      owner[key] = shortened;
    }
    s = JSON.stringify(box.root);
  }
  // Still too long (one huge text, not lists): send it as a value inside valid JSON rather than cut JSON.
  return s.length <= max ? s : JSON.stringify({ truncated: `the reply was ${s.length} characters; ask for less data`, start: s.slice(0, max - 200) });
}

/** Every array with more than 3 data items, with its own size (its JSON minus the arrays inside it). */
function arrays(box, notes) {
  const found = [];
  const visit = (owner, key) => {
    const v = owner[key];
    if (!v || typeof v !== "object") return 0;
    let nested = 0;
    for (const k of Object.keys(v)) nested += visit(v, k);
    if (!Array.isArray(v)) return nested;
    const size = JSON.stringify(v).length;
    if (v.filter((x) => !notes.has(x)).length > 3) found.push({ owner, key, items: v, own: size - nested });
    return size;
  };
  visit(box, "root");
  return found;
}

export function failure(err) {
  return { isError: true, content: [{ type: "text", text: err instanceof RevitError ? err.message : String(err?.message || err) }] };
}
