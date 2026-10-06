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

export function bridgeUrl(cfg = readConfig()) {
  return process.env.ACE_REVIT_URL || `http://localhost:${cfg?.port || 48884}`;
}

export async function health(timeoutMs = 4000) {
  try {
    const r = await fetch(`${bridgeUrl()}/health`, { signal: AbortSignal.timeout(timeoutMs) });
    return await r.json();
  } catch (err) {
    return { ok: false, error: err?.cause?.code || err?.name || String(err) };
  }
}

export async function callRevit(command, args = {}, timeoutSeconds = 120, signal = undefined) {
  const cfg = readConfig();
  if (!cfg) {
    throw new RevitError(
      `Cannot find ${CONFIG_PATH}. Start Revit 2025 once with the ACE add-in installed (run install.ps1), then try again.`,
    );
  }
  const base = bridgeUrl(cfg);
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
      `Revit is not reachable at ${base} (${reason}). Make sure Revit 2025 is open with a model; ` +
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
  return payload.result;
}

/** Today's date (or the given time's) in local time, YYYY-MM-DD: journal and report files follow the user's day, not UTC. */
export function localDate(d = new Date()) {
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
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
