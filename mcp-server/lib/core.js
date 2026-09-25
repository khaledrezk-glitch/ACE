// Shared paths, configuration and the HTTP client for the Revit add-in bridge.
import fs from "node:fs";
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
export const MAX_TEXT = 120_000;

export class RevitError extends Error {}

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

export async function callRevit(command, args = {}, timeoutSeconds = 120) {
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
      signal: AbortSignal.timeout((timeoutSeconds + 15) * 1000),
    });
  } catch (err) {
    const reason = err?.cause?.code || err?.name || err?.message;
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

export function text(value) {
  let s = typeof value === "string" ? value : JSON.stringify(value, null, 1);
  if (s.length > MAX_TEXT) {
    s = s.slice(0, MAX_TEXT) + `\n... [truncated ${s.length - MAX_TEXT} characters - narrow the query or return less data]`;
  }
  return { content: [{ type: "text", text: s }] };
}

export function failure(err) {
  return { isError: true, content: [{ type: "text", text: err instanceof RevitError ? err.message : String(err?.stack || err) }] };
}
