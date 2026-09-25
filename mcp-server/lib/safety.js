// Safety: preview-before-apply gate, risky-code screening, activity journal.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { JOURNAL_DIR, RevitError } from "./core.js";

export const PREVIEW_VALID_MS = 60 * 60 * 1000;

export const previews = new Map(); // change fingerprint -> time of successful dry run

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${stable(value[k])}`).join(",")}}`;
  }
  return JSON.stringify(value ?? null);
}

export const fingerprint = (kind, payload) => createHash("sha256").update(kind + "\0" + stable(payload)).digest("hex");

export function requirePreview(key) {
  const at = previews.get(key);
  if (!at || Date.now() - at > PREVIEW_VALID_MS) {
    throw new RevitError(
      "Blocked for safety: this exact change has not been previewed. Run the same call with dry_run: true, " +
        "explain the preview result to the user, ask for confirmation, then repeat the identical call with dry_run: false.",
    );
  }
}

const RISKY = [
  [/\bProcess\b/, "starts other programs"],
  [/\b(File|Directory)\.(Delete|Move|Replace|Copy|Create\w*|WriteAll\w*|AppendAll\w*)\b|\bStreamWriter\b|\bFileStream\b/, "creates, changes or deletes files on disk"],
  [/\b(HttpClient|WebClient|WebRequest|Socket|TcpClient|UdpClient|SmtpClient)\b/, "uses the network"],
  [/\bRegistry\b/, "changes Windows settings"],
  [/\bEnvironment\.Exit\b|\bApplication\.Exit\b/, "closes programs"],
  [/\.(SaveAs|Save|SaveCloudModel|Close)\s*\(/, "saves or closes a model"],
  [/\b(SynchronizeWithCentral|RelinquishOwnership|ReloadLatest)\b/, "syncs with the central model"],
  [/\bPostCommand\b/, "triggers Revit commands"],
  [/\bAssembly\.Load|\bDllImport\b|\bMarshal\./, "loads external code"],
];

export function screenCode(code, allowRisky) {
  const found = RISKY.filter(([re]) => re.test(code)).map(([, why]) => why);
  if (found.length && !allowRisky) {
    throw new RevitError(
      `Blocked for safety: this code ${[...new Set(found)].join(", ")}. That goes beyond editing the model. ` +
        "Explain to the user exactly what it would do and why; only if they agree, call again with allow_risky: true.",
    );
  }
  return found;
}

export function journal(entry) {
  try {
    fs.mkdirSync(JOURNAL_DIR, { recursive: true });
    const now = new Date();
    const file = path.join(JOURNAL_DIR, `${now.toISOString().slice(0, 10)}.md`);
    const lines = [
      `## ${now.toLocaleTimeString()} - ${entry.title}`,
      entry.explanation ? `**What:** ${entry.explanation}` : null,
      `**Result:** ${entry.outcome}`,
      entry.details ? "```json\n" + JSON.stringify(entry.details, null, 1).slice(0, 4000) + "\n```" : null,
      entry.code ? "<details><summary>Code</summary>\n\n```csharp\n" + entry.code + "\n```\n</details>" : null,
      "",
    ].filter(Boolean);
    fs.appendFileSync(file, lines.join("\n\n") + "\n");
  } catch {
    // The journal is best effort; never fail a tool call because of it.
  }
}

export function outcomeOf(result, dryRun) {
  if (!result || result.success === false) return `FAILED - nothing changed (${result?.error || result?.errors?.[0] || "error"})`;
  const c = result.changed || result.wouldChange;
  const counts = c ? `added ${c.added}, modified ${c.modified}, deleted ${c.deleted}` : "no model changes";
  return dryRun ? `PREVIEW only, model untouched (would have: ${counts})` : `APPLIED (${counts})`;
}
