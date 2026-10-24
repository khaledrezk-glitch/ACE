// Safety: preview-before-apply gate, risky-code screening, activity journal.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { JOURNAL_DIR, RevitError, currentCall, healthAtPort, localDate, pinSession, sessionKey, sessionLabel } from "./core.js";

export const PREVIEW_VALID_MS = 60 * 60 * 1000;

export const previews = new Map(); // change fingerprint -> { at, session, label, build } of its successful dry run

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${stable(value[k])}`).join(",")}}`;
  }
  return JSON.stringify(value ?? null);
}

export const fingerprint = (kind, payload) => createHash("sha256").update(kind + "\0" + stable(payload)).digest("hex");

/**
 * The preview-before-apply gate around one bridge call. A preview (apply false) that `ok` accepts is remembered; an
 * apply needs the identical, recent preview and an explanation for the journal. An apply uses up its preview BEFORE it
 * is sent: if the call times out while Revit still finishes the change, a retry cannot apply it a second time; it needs
 * a fresh preview, which shows the model as it now is.
 */
export async function gated(key, { apply, explanation, why }, send, ok) {
  // One Revit session per call: the preview and the apply must reach the same Revit (machine, pid, port).
  const session = await pinSession();
  if (!apply) {
    const result = await send();
    if (ok(result)) previews.set(key, { at: Date.now(), session: sessionKey(session), label: sessionLabel(session), build: currentCall.getStore()?.build ?? null });
    return result;
  }
  requirePreview(key);
  if (!explanation) throw new RevitError(why);
  const p = previews.get(key);
  if (p.session !== sessionKey(session))
    throw new RevitError(`Blocked for safety: the preview was made in Revit session ${p.label}, this call would go to ${sessionLabel(session)}: preview again in the session to change.`);
  // A preview proves what the ACE build that ran it would do. If Revit now runs another build (install.ps1's reload, the
  // loader's own switch with no model open, "Load ACE again"), the preview no longer counts.
  if (p.build) {
    const h = await healthAtPort(session?.port, 3000);
    const now = h?.ok ? h.build ?? null : null;
    if (now !== p.build) {
      previews.delete(key);
      throw new RevitError(`Blocked for safety: the preview was made on ACE build ${p.build}, but Revit ${now ? `now runs ${now} (ACE was reloaded)` : "did not confirm which ACE build it runs"}. Nothing changed: preview again, then apply.`);
    }
  }
  previews.delete(key);
  try {
    return await send();
  } catch (err) {
    if (err instanceof RevitError) {
      err.message += " The change may still have been applied in Revit: check with a read-only query before trying again (a new preview is needed).";
    }
    throw err;
  }
}

export function requirePreview(key) {
  const at = previews.get(key)?.at;
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
  [/\bFile\.(Open\w*|SetAttributes|Encrypt|Decrypt)\b|\b(FileInfo|DirectoryInfo|BinaryWriter|TextWriter|ZipFile|ZipArchive)\b/, "creates, changes or deletes files on disk"],
  [/\.(Export|ExportImage)\s*\(/, "exports files from the model"],
  [/\b(HttpClient|WebClient|WebRequest|Socket|TcpClient|UdpClient|SmtpClient)\b/, "uses the network"],
  [/\bRegistry\b/, "changes Windows settings"],
  [/\bEnvironment\.Exit\b|\bApplication\.Exit\b/, "closes programs"],
  [/\.(SaveAs|Save|SaveAndClose|SaveCloudModel|Close)\s*\(/, "saves or closes a model"],
  [/\b(OpenAndActivateDocument|OpenDocumentFile)\s*\(|\.(Unload|UnloadLocally)\s*\(/, "opens other models or unloads links"],
  [/\bFile\.(Read\w*|Open\w*)\b|\bStreamReader\b/, "reads files on disk"],
  [/\b(Activator|InvokeMember|AssemblyLoadContext)\b|\bType\.GetType\b|\.GetMethod\s*\(/, "uses reflection to reach code beyond the Revit API"],
  [/\busing\s+\w+\s*=\s*(global::)?System\.(IO|Net|Diagnostics|Reflection|Runtime)\b/, "hides a system class behind an alias"],
  [/\b(SynchronizeWithCentral|RelinquishOwnership|ReloadLatest)\b/, "syncs with the central model"],
  [/\bPostCommand\b/, "triggers Revit commands"],
  [/\bAssembly\.Load|\bDllImport\b|\bMarshal\./, "loads external code"],
];

export function screenCode(code, allowRisky) {
  // A quick first check; the add-in's compiler screen (resolved symbols, also dynamic and escaped names) is the real one.
  // Checked as written AND without comments and spaces around dots ("File /**/ . Delete"): stripping alone could hide
  // a call behind a string such as "http://..." that looks like a comment.
  const plain = code.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/\/\/[^\n]*/g, " ").replace(/\s*\.\s*/g, ".");
  const found = RISKY.filter(([re]) => re.test(code) || re.test(plain)).map(([, why]) => why);
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
    const file = path.join(JOURNAL_DIR, `${localDate(now)}.md`);
    const lines = [
      `## ${now.toLocaleTimeString()} - ${entry.title}`,
      entry.explanation ? `**What:** ${entry.explanation}` : null,
      `**Result:** ${entry.outcome}`,
      entry.details ? "```json\n" + JSON.stringify(entry.details, null, 1).slice(0, 4000) + "\n```" : null,
      entry.reply ? "<details><summary>Reply</summary>\n\n```text\n" + String(entry.reply).slice(0, 8000) + "\n```\n</details>" : null,
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
  // Element types are counted apart from elements: unused duplicate types Revit drops on commit are not data loss.
  const types = c?.deletedTypes ? `, removed ${c.deletedTypes} type(s)${c.deletedTypeInstances === 0 ? " (unused, 0 instances)" : c.deletedTypeInstances != null ? ` with ${c.deletedTypeInstances} instance(s)` : ""}` : "";
  const counts = c ? `added ${c.added}, modified ${c.modified}, deleted ${c.deleted}${types}` : "no model changes";
  if (!dryRun && result.stage === "incomplete") return `APPLIED but NOT DONE (${counts}; ${result.error ?? "the after-check failed"})`;
  return dryRun ? `PREVIEW only, model untouched (would have: ${counts})` : `APPLIED (${counts})`;
}

// ---------------------------------------------------------------------------------------------
// No new warnings (G24): every apply is rolled back by the add-in when it adds a Revit warning the call did not accept.
// ---------------------------------------------------------------------------------------------

/**
 * The warning parameters of an apply tool, only those given: accept_warnings (descriptions or failure GUIDs),
 * accept_new_warnings (all), fixes_warnings (a warning-fix run). They are part of the change's identity, so an apply
 * that accepts warnings needs a preview that accepted the same ones.
 */
export function warningArgs(a = {}) {
  const out = {};
  const list = (v) => (Array.isArray(v) ? v.map((x) => String(x).trim()).filter(Boolean) : []);
  if (list(a.accept_warnings).length) out.accept_warnings = list(a.accept_warnings);
  if (a.accept_new_warnings === true) out.accept_new_warnings = true;
  if (list(a.fixes_warnings).length) out.fixes_warnings = list(a.fixes_warnings);
  return out;
}

/** A preview whose identical apply the add-in would refuse: new warnings not accepted, or a fix run's targets left. */
export const warningsRefuse = (r) => r?.newWarnings?.refuse === true || r?.targetedWarnings?.ok === false;

const WARNING_REFUSALS = new Set(["new-warnings", "warnings-remain"]);
export const warningRefused = (r) => WARNING_REFUSALS.has(r?.stage);

const warningText = (w) => `${w.description ?? "(no description)"}${[...(w.failing ?? []), ...(w.additional ?? [])].length ? ` (elements: ${[...new Set([...(w.failing ?? []), ...(w.additional ?? [])])].slice(0, 10).join(", ")})` : ""}${w.times > 1 ? ` x${w.times}` : ""}`;
export const ACCEPT_HOW = "If the user accepts them: run the dry run again with accept_warnings: [\"<description or GUID>\", ...] (or accept_new_warnings: true for all), then the identical call to apply.";

/** The reply lines about warnings: the refusal, "new warnings: N" (what a dry run would add) and a fix run's targets. */
export function warningLines(r, dryRun) {
  if (!r) return [];
  if (warningRefused(r)) return [`REFUSED (${r.stage}), rolled back, nothing changed: ${r.error ?? "the change added warnings"}${r.hint ? `\n${r.hint}` : ""}`];
  const lines = [];
  const n = r.newWarnings;
  if (n) {
    const items = (n.items ?? []).slice(0, 10);
    const list = items.map((w) => `- ${warningText(w)}${w.accepted ? " [accepted]" : ""}${w.guid ? ` {${w.guid}}` : ""}`);
    if ((n.items?.length ?? 0) + (n.more ?? 0) > items.length) list.push(`- ... ${(n.items?.length ?? 0) + (n.more ?? 0) - items.length} more`);
    if (!n.count) lines.push("New warnings: 0.");
    else if (dryRun) lines.push(`Would add ${n.count} new warning(s)${n.refused ? `, ${n.refused} not accepted` : " (all accepted by this call)"}:\n${list.join("\n")}${n.refuse ? `\nThe identical apply would refuse (new-warnings) and roll back. ${ACCEPT_HOW}` : ""}`);
    else lines.push(`New warnings: ${n.count} (accepted by this call):\n${list.join("\n")}`);
  }
  const t = r.targetedWarnings;
  if (t) lines.push(t.ok ? `${t.line ?? `Targeted warnings: ${t.targeted} ${dryRun ? "would be" : "are"} gone.`}`
    : `${t.line ?? `Targeted warnings: ${t.remaining} of ${t.targeted} remain.`}${dryRun ? " The identical apply would fail (warnings-remain) and roll back: fix the cause or limit the run, then preview again." : ""}`);
  return lines;
}
