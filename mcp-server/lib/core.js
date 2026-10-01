// Shared paths, configuration and the HTTP client for the Revit add-in bridge.
import fs from "node:fs";
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

/** A signal that fires when either does (AbortSignal.any needs Node 20; the installer allows 18). */
function either(a, b) {
  if (!a) return b;
  const both = new AbortController();
  for (const s of [a, b]) {
    if (s.aborted) both.abort(s.reason);
    else s.addEventListener("abort", () => both.abort(s.reason), { once: true });
  }
  return both.signal;
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
  try {
    response = await fetch(`${base}/command`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Ace-Token": cfg.token },
      body: JSON.stringify({ command, args, timeoutSeconds }),
      // The add-in answers within the timeout (it reports its own timeouts); this is only the backstop. The client's
      // cancel stops the wait here; a request Revit has not started yet is then skipped by the add-in.
      signal: either(signal ?? currentCall.getStore()?.signal, AbortSignal.timeout((timeoutSeconds + 15) * 1000)),
    });
  } catch (err) {
    const reason = err?.cause?.code || err?.name || err?.message;
    const cancelledByClient = (signal ?? currentCall.getStore()?.signal)?.aborted;
    if (cancelledByClient) {
      // Tell Revit too: the running script or clash run stops where it checks, queued requests are dropped.
      fetch(`${base}/cancel`, { method: "POST", headers: { "X-Ace-Token": cfg.token }, signal: AbortSignal.timeout(5000) }).catch(() => {});
      throw new RevitError(`'${command}' was cancelled. Revit stops it where it can; anything not finished is rolled back.`);
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
    payload = await response.json();
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
  const dropped = new Map(); // shortened array -> how many items it has lost so far
  for (let round = 0; round < 40 && s.length > max; round++) {
    const longest = longestArray(box);
    if (!longest || longest.items.length <= 3) break;
    const { owner, key, items } = longest;
    const keep = Math.max(3, Math.floor(items.length / 2));
    const shortened = items.slice(0, keep);
    const lost = items.length - keep + (dropped.get(items) || 0);
    dropped.set(shortened, lost);
    owner[key] = shortened;
    const note = `${lost} more not shown - ask for less (limit, filter) to see them`;
    if (Array.isArray(owner)) shortened.push(note);
    else owner[`${key}Truncated`] = note;
    s = JSON.stringify(box.root);
  }
  return s.length <= max ? s : s.slice(0, max) + ` ... [cut: ${s.length - max} more characters - ask for less data]`;
}

/** The array with the longest JSON anywhere in the value, with the object or array that holds it. */
function longestArray(box) {
  let best = null;
  const visit = (owner, key) => {
    const v = owner[key];
    if (!v || typeof v !== "object") return;
    if (Array.isArray(v)) {
      const size = JSON.stringify(v).length;
      if (!best || size > best.size) best = { owner, key, items: v, size };
    }
    for (const k of Object.keys(v)) visit(v, k);
  };
  visit(box, "root");
  return best;
}

export function failure(err) {
  return { isError: true, content: [{ type: "text", text: err instanceof RevitError ? err.message : String(err?.message || err) }] };
}
