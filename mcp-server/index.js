#!/usr/bin/env node
// ACE Revit MCP server.
//
// Claude (Desktop or Code) starts this process over stdio. It forwards tool calls to the
// ACE add-in running inside Revit 2025 (http://localhost:<port>, token-protected), so Claude
// can inspect the model, change it, and - most importantly - write and run Revit API C#
// for any multi-step task.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const APPDATA = process.env.APPDATA || path.join(os.homedir(), ".config");
const CONFIG_PATH = process.env.ACE_REVIT_CONFIG || path.join(APPDATA, "ACE-RevitMCP", "config.json");
const BUILTIN_SCRIPTS = path.join(HERE, "scripts");
const USER_SCRIPTS = process.env.ACE_REVIT_SCRIPTS || path.join(APPDATA, "ACE-RevitMCP", "scripts");
const JOURNAL_DIR = process.env.ACE_REVIT_JOURNAL || path.join(APPDATA, "ACE-RevitMCP", "journal");
const MAX_TEXT = 120_000;
const PREVIEW_VALID_MS = 60 * 60 * 1000;

// ---------------------------------------------------------------------------------------------
// Bridge client
// ---------------------------------------------------------------------------------------------

function readConfig() {
  try {
    const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8").replace(/^\uFEFF/, ""));
    return { port: cfg.port || 48884, token: cfg.token || "" };
  } catch {
    return null; // Revit hasn't started the add-in yet (it creates the file) and installer didn't run.
  }
}

class RevitError extends Error {}

async function callRevit(command, args = {}, timeoutSeconds = 120) {
  const cfg = readConfig();
  if (!cfg) {
    throw new RevitError(
      `Cannot find ${CONFIG_PATH}. Start Revit 2025 once with the ACE add-in installed (run install.ps1), then try again.`,
    );
  }
  const base = process.env.ACE_REVIT_URL || `http://localhost:${cfg.port}`;
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
      `Revit is not reachable at ${base} (${reason}). Make sure Revit 2025 is open; ` +
        `the ACE tab > "MCP Status" button shows whether the connection is running.`,
    );
  }
  let payload;
  try {
    payload = await response.json();
  } catch {
    throw new RevitError(`Revit bridge returned HTTP ${response.status} with a non-JSON body.`);
  }
  if (!payload.ok) throw new RevitError(payload.error || `Revit bridge error (HTTP ${response.status})`);
  return payload.result;
}

function text(value) {
  let s = typeof value === "string" ? value : JSON.stringify(value, null, 1);
  if (s.length > MAX_TEXT) {
    s = s.slice(0, MAX_TEXT) + `\n... [truncated ${s.length - MAX_TEXT} characters - narrow the query or return less data]`;
  }
  return { content: [{ type: "text", text: s }] };
}

function failure(err) {
  return { isError: true, content: [{ type: "text", text: err instanceof RevitError ? err.message : String(err?.stack || err) }] };
}

/** Wraps a handler so any exception becomes a readable tool error instead of a protocol error. */
const safe = (fn) => async (args) => {
  try {
    return await fn(args ?? {});
  } catch (err) {
    return failure(err);
  }
};

// ---------------------------------------------------------------------------------------------
// Script library (.cs files with "// @key: value" headers)
// ---------------------------------------------------------------------------------------------

function parseScript(file, source) {
  const meta = { name: path.basename(file, ".cs"), file, source: file.startsWith(USER_SCRIPTS) ? "user" : "built-in" };
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*\/\/\s*@(\w+):\s*(.*)$/);
    if (m) meta[m[1]] = m[2].trim();
    else if (line.trim() && !line.trim().startsWith("//")) break;
  }
  if (source) meta.source = source;
  return meta;
}

function listScripts() {
  const out = new Map();
  for (const [dir, source] of [
    [BUILTIN_SCRIPTS, "built-in"],
    [USER_SCRIPTS, "user"],
  ]) {
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir).filter((f) => f.endsWith(".cs"))) {
      const meta = parseScript(path.join(dir, f), source);
      out.set(meta.name, meta); // user scripts override built-ins with the same name
    }
  }
  return [...out.values()].sort((a, b) => a.name.localeCompare(b.name));
}

function findScript(name) {
  const script = listScripts().find((s) => s.name.toLowerCase() === String(name).toLowerCase());
  if (!script) throw new RevitError(`No saved script named '${name}'. Call list_saved_scripts to see what exists.`);
  return script;
}

// ---------------------------------------------------------------------------------------------
// Safety: preview-before-apply gate, risky-code screening, activity journal
// ---------------------------------------------------------------------------------------------

const previews = new Map(); // change fingerprint -> time of successful dry run

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${stable(value[k])}`).join(",")}}`;
  }
  return JSON.stringify(value ?? null);
}

const fingerprint = (kind, payload) => createHash("sha256").update(kind + "\0" + stable(payload)).digest("hex");

function requirePreview(key) {
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

function screenCode(code, allowRisky) {
  const found = RISKY.filter(([re]) => re.test(code)).map(([, why]) => why);
  if (found.length && !allowRisky) {
    throw new RevitError(
      `Blocked for safety: this code ${[...new Set(found)].join(", ")}. That goes beyond editing the model. ` +
        "Explain to the user exactly what it would do and why; only if they agree, call again with allow_risky: true.",
    );
  }
  return found;
}

function journal(entry) {
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

function outcomeOf(result, dryRun) {
  if (!result || result.success === false) return `FAILED - nothing changed (${result?.error || result?.errors?.[0] || "error"})`;
  const c = result.changed || result.wouldChange;
  const counts = c ? `added ${c.added}, modified ${c.modified}, deleted ${c.deleted}` : "no model changes";
  return dryRun ? `PREVIEW only, model untouched (would have: ${counts})` : `APPLIED (${counts})`;
}

// ---------------------------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------------------------

const INSTRUCTIONS = `
You are connected to a live Autodesk Revit 2025 session. The user asks for things in plain language,
often complex in concept ("renumber the rooms on level 2 in reading order", "make a sheet for every
level", "flag every door narrower than 900 mm") and you do the whole job - carefully, transparently,
and without ever putting their model at risk. Reply in the user's language.

THE SAFETY PROTOCOL - follow it for EVERY request that changes the model:

1. UNDERSTAND. Call get_model_overview, then find_elements / get_selection / get_element_details to
   learn the real names, types, levels and parameters. Never guess names - look them up.
   If the request is ambiguous in a way that changes the result, ask ONE short question.

2. EXPLAIN THE PLAN in plain, non-programmer language BEFORE touching anything:
   - What you understood the goal to be.
   - The steps you will take, in order ("I will collect all rooms on Level 2, sort them by position,
     then give them numbers 201, 202, ...").
   - Exactly what will change and what will NOT change.
   - How it can be undone (one Ctrl+Z / undo_last_claude_change) and, for large edits, offer backup_model.

3. PREVIEW. Run the change with dry_run: true. The model is left untouched. Report the preview result:
   the "wouldChange" counts (added / modified / deleted by category), samples of new values, warnings,
   and anything that would be skipped. If something is surprising (e.g. unexpected deletions), stop
   and explain.

4. ASK. "Shall I apply this?" - and WAIT for the user's yes. Do not apply in the same turn as the
   preview unless the user already said something like "go ahead without asking" for this task.

5. APPLY the identical call with dry_run: false. (The server blocks real changes that were not
   previewed with the exact same code and inputs.) Always give the "explanation" argument in plain
   words; it goes into the user's activity journal.

6. VERIFY and REPORT: re-query or call view_image to check the result; report what changed
   (counts, examples, element ids), warnings, anything skipped and why, and how to undo it.
   Offer save_script if the task might be repeated.

Read-only questions (counts, checks, reports, pictures) need no preview or confirmation: just do them.
Code that touches files, other programs, the network, or saves/closes/syncs the model is blocked
unless you ask the user and they agree; then pass allow_risky: true.
If anything fails, nothing is changed - say so plainly, fix, and retry.

REVIT API ESSENTIALS (Revit 2025, .NET 8):
- Internal units: length = decimal FEET, angles = radians, area = sq ft. Use ctx.Mm(x) / ctx.M(x) to
  convert to feet, ctx.ToMm(ft) / ctx.ToM(ft) back. get_model_overview says the display unit.
- ElementId wraps a long: new ElementId(123L), id.Value. (IntegerValue is obsolete.)
- Collect with FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()
  or .OfClass(typeof(Wall)). Filter by view: new FilteredElementCollector(doc, viewId).
- Parameters: e.LookupParameter("Comments") or e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
  type parameters live on doc.GetElement(e.GetTypeId()). Set strings with p.Set(text); set lengths in
  display units with p.SetValueString("1200").
- Before placing a FamilySymbol call if (!symbol.IsActive) symbol.Activate();
- Call doc.Regenerate() when later steps need geometry or locations produced by earlier steps.
- Changing the active view (uidoc.ActiveView = ...) or exporting must happen OUTSIDE a transaction:
  use mode "manual" (without your own transaction around those lines). For just looking at a view,
  use view_image. "readonly" runs are always rolled back, even if the code opens a transaction.

execute_revit_code SCRIPT SHAPE: write the BODY of a method (statements, local functions allowed; a
top-level 'using X;' line is fine). In scope: doc, uidoc, uiapp, app, ctx, args (inputs as
JsonObject), Log(obj). Common namespaces (Autodesk.Revit.DB, .Architecture, .Structure, .Mechanical,
.Plumbing, .Electrical, Autodesk.Revit.UI, System.Linq, ...) are already imported. 'return' whatever
you want back. Don't redeclare doc/uidoc/app/args/ctx.
ctx helpers: ctx.All<Wall>(), ctx.Instances(BuiltInCategory.OST_Doors), ctx.Types<WallType>(),
ctx.Level("Level 1"), ctx.Levels(), ctx.Selection(), ctx.El(id), ctx.Str/Num/Bool/Ids("input"),
ctx.Mm/M/Cm/ToMm/ToM/Deg, ctx.Transact("name", () => {...}) (mode "manual"), ctx.IsDryRun.
Modes: "auto" (default: whole script in one transaction), "manual" (you create transactions), both
merged into ONE undo step; "readonly" (no transaction, model cannot be changed).
compile_only: true checks that code compiles without running it.
If a script fails, nothing is changed; read the error (line numbers match your code), fix, retry.
Warnings are auto-dismissed and returned; hard Revit errors roll the whole run back.
`.trim();

const server = new McpServer(
  { name: "ace-revit", version: "1.1.0" },
  { instructions: INSTRUCTIONS, capabilities: { logging: {} } },
);

const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const writes = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };

server.registerTool(
  "revit_status",
  {
    title: "Revit connection status",
    description: "Check that Revit 2025 is running and reachable, and which documents are open. Call this first if anything fails.",
    inputSchema: {},
    annotations: readOnly,
  },
  safe(async () => text(await callRevit("ping", {}, 20))),
);

server.registerTool(
  "get_model_overview",
  {
    title: "Model overview",
    description:
      "Summary of the active Revit model: title, path, display units, project info, active view, levels (with elevations), phases and element counts per category. Start every task here.",
    inputSchema: {},
    annotations: readOnly,
  },
  safe(async () => text(await callRevit("get_document_info", {}, 120))),
);

server.registerTool(
  "get_selection",
  {
    title: "Current selection",
    description: "Elements the user currently has selected in Revit. Use when they say 'these', 'the selected ...', etc.",
    inputSchema: { include_parameters: z.boolean().optional().describe("Also return every instance parameter") },
    annotations: readOnly,
  },
  safe(async (a) => text(await callRevit("get_selection", a))),
);

server.registerTool(
  "find_elements",
  {
    title: "Find elements",
    description:
      "Search the model. Filter by categories, level, view, name/type text and parameter conditions; optionally return chosen parameter values. Returns total count plus up to 'limit' element summaries (id, name, category, type, family, level).",
    inputSchema: {
      categories: z.array(z.string()).optional().describe('e.g. ["Walls"], ["Doors","Windows"], ["OST_StructuralColumns"], ["Rooms"]'),
      level: z.string().optional().describe("Level name, exact"),
      in_view: z.string().optional().describe('View name, or "active" for elements visible in the active view'),
      name_contains: z.string().optional().describe("Matches element name, family name or type name"),
      parameter_filters: z
        .array(
          z.object({
            name: z.string().describe('Parameter name, e.g. "Comments", "Mark", or BuiltInParameter like "ALL_MODEL_MARK"'),
            op: z.enum(["equals", "not_equals", "contains", "empty", "not_empty", "gt", "lt", "exists", "missing"]).optional(),
            value: z.union([z.string(), z.number()]).optional().describe("For gt/lt: number in project display units"),
          }),
        )
        .optional(),
      include_parameters: z.array(z.string()).optional().describe("Parameter values to return per element"),
      types_only: z.boolean().optional().describe("Return element TYPES instead of instances"),
      limit: z.number().int().optional().describe("Max elements returned (default 200, max 5000); 'total' is always exact"),
    },
    annotations: readOnly,
  },
  safe(async (a) => text(await callRevit("query_elements", a, 180))),
);

server.registerTool(
  "get_element_details",
  {
    title: "Element details",
    description: "Full details for up to 50 elements: every instance parameter (value + display string), type parameters, location, bounding box, host, room.",
    inputSchema: {
      ids: z.array(z.number().int()).min(1).max(50),
      include_type_parameters: z.boolean().optional().describe("Default true"),
    },
    annotations: readOnly,
  },
  safe(async (a) => text(await callRevit("get_element_details", a))),
);

server.registerTool(
  "set_parameters",
  {
    title: "Set parameter values",
    description:
      "Set parameter values on many elements in one undoable transaction. Numbers are Revit internal units (feet); strings are parsed in project display units for length/area parameters (e.g. \"1200\" in a mm project). Yes/No parameters accept true/false. Must be previewed with dry_run: true (and confirmed by the user) before applying.",
    inputSchema: {
      changes: z
        .array(
          z.object({
            id: z.number().int(),
            parameter: z.string(),
            value: z.union([z.string(), z.number(), z.boolean()]),
            target: z.enum(["instance", "type"]).optional().describe('"type" edits the element\'s type (affects all instances)'),
          }),
        )
        .min(1),
      dry_run: z.boolean().optional(),
      explanation: z.string().optional().describe("Plain-language description (required when applying)"),
    },
    annotations: writes,
  },
  safe(async ({ changes, dry_run, explanation }) => {
    const key = fingerprint("params", changes);
    if (!dry_run) {
      requirePreview(key);
      if (!explanation) throw new RevitError("Give 'explanation': a plain sentence describing this change for the activity journal.");
    }
    const result = await callRevit("set_parameters", { changes, dry_run }, 300);
    const ok = result.failed === 0;
    if (ok && dry_run) previews.set(key, Date.now());
    if (!dry_run) previews.delete(key);
    journal({
      title: `${dry_run ? "Preview" : "Change"}: set parameters`,
      explanation,
      outcome: dry_run ? `PREVIEW only, model untouched (${result.applied} value(s) would change, ${result.failed} failed)` : `APPLIED (${result.applied} value(s) changed, ${result.failed} failed)`,
      details: result.results?.slice(0, 30),
    });
    const out = text(result);
    if (dry_run) out.content.push({ type: "text", text: ok ? "PREVIEW ONLY - the model is unchanged. Show the user the before/after values and ask for confirmation." : "Some changes would fail - fix them before applying (a successful preview is required)." });
    return out;
  }),
);

server.registerTool(
  "backup_model",
  {
    title: "Back up the model file",
    description:
      "Copy the model's .rvt file to %APPDATA%\\ACE-RevitMCP\\backups (timestamped). The open model is not changed. Offer this before large changes. save_first: true saves the model first so unsaved work is included - only with the user's permission.",
    inputSchema: { save_first: z.boolean().optional() },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  safe(async (a) => {
    const r = await callRevit("backup_model", a, 600);
    journal({ title: "Backup", outcome: `Backup created at ${r.backup}${r.savedBeforeBackup ? " (model saved first)" : ""}` });
    return text(r);
  }),
);

server.registerTool(
  "undo_last_claude_change",
  {
    title: "Undo Claude's last change",
    description:
      "Undo the most recent change Claude applied, as one step. Refuses if the user changed anything afterwards, so the user's own work is never undone.",
    inputSchema: {},
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  safe(async () => {
    const r = await callRevit("undo_last_claude_change", {}, 60);
    journal({ title: "Undo", outcome: `Undid "${r.undoRequested}"` });
    return text(r);
  }),
);

server.registerTool(
  "get_activity_log",
  {
    title: "Activity journal",
    description: "Show what Claude previewed and changed in Revit today (plain-language entries with results and code). Use when the user asks what was done.",
    inputSchema: { date: z.string().optional().describe("YYYY-MM-DD, default today") },
    annotations: readOnly,
  },
  safe(async ({ date }) => {
    const day = date || new Date().toISOString().slice(0, 10);
    const file = path.join(JOURNAL_DIR, `${day}.md`);
    if (!fs.existsSync(file)) return text(`No activity recorded for ${day}. Journal folder: ${JOURNAL_DIR}`);
    const content = fs.readFileSync(file, "utf8").replace(/<details>[\s\S]*?<\/details>/g, "(code omitted)");
    return text(`Journal file: ${file}\n\n${content}`);
  }),
);

server.registerTool(
  "select_elements",
  {
    title: "Select & zoom",
    description: "Select elements in Revit's UI (and zoom to them) so the user can see what you're talking about. Empty list clears the selection.",
    inputSchema: { ids: z.array(z.number().int()), zoom: z.boolean().optional().describe("Default true") },
    annotations: { ...readOnly, idempotentHint: true },
  },
  safe(async (a) => text(await callRevit("select_elements", a))),
);

server.registerTool(
  "list_views",
  {
    title: "List views",
    description: "All views and sheets (id, name, type, level, sheet number). Optional filter by view type: FloorPlan, CeilingPlan, ThreeD, Section, Elevation, DrawingSheet, Schedule, Legend, AreaPlan, EngineeringPlan...",
    inputSchema: { view_type: z.string().optional() },
    annotations: readOnly,
  },
  safe(async (a) => text(await callRevit("list_views", a))),
);

server.registerTool(
  "view_image",
  {
    title: "Look at a view",
    description:
      "Export a Revit view (default: the active view) as a PNG and return it so you can SEE the model - use it to verify results of edits, check layouts, or understand a plan.",
    inputSchema: {
      view_name: z.string().optional(),
      view_id: z.number().int().optional(),
      pixel_size: z.number().int().optional().describe("Image width in pixels, 256-4000 (default 1400)"),
    },
    annotations: readOnly,
  },
  safe(async (a) => {
    const r = await callRevit("export_view_image", a, 180);
    return {
      content: [
        { type: "image", data: r.base64, mimeType: r.mimeType || "image/png" },
        { type: "text", text: `View "${r.view}" (${r.viewType}). Saved at ${r.path}` },
      ],
    };
  }),
);

const codeSchema = {
  code: z.string().describe("C# method body using the Revit API. See server instructions for what's in scope."),
  mode: z.enum(["auto", "manual", "readonly"]).optional().describe("auto (default) = one transaction; manual = you manage transactions; readonly = no changes"),
  dry_run: z.boolean().optional().describe("Run fully, then roll back every change. Use before real edits."),
  inputs: z.record(z.any()).optional().describe("Values available to the script as args / ctx.Str(...) / ctx.Num(...)"),
  transaction_name: z.string().optional().describe("Shown in Revit's Undo list, e.g. 'Claude: renumber rooms'"),
  timeout_seconds: z.number().optional().describe("Default 300"),
  explanation: z.string().optional().describe("Plain-language description of what this run does (required when applying changes; recorded in the activity journal)"),
  allow_risky: z.boolean().optional().describe("Only after the user explicitly agreed: allow code that touches files, programs, network, or saves/closes/syncs models"),
  compile_only: z.boolean().optional().describe("Only check that the code compiles; run nothing"),
};

async function runCode({ code, mode, dry_run, inputs, transaction_name, timeout_seconds, explanation, allow_risky, compile_only }) {
  mode = mode || "auto";
  const risky = screenCode(code, allow_risky);
  const modifies = mode !== "readonly" && !compile_only;
  const key = fingerprint("code", { code, mode, inputs: inputs || {} });
  if (modifies && !dry_run) {
    requirePreview(key);
    if (!explanation) throw new RevitError("Give 'explanation': one or two plain sentences saying what this change does. It is recorded in the user's activity journal.");
  }

  const result = await callRevit(
    "execute_code",
    { code, mode, dry_run, inputs, transaction_name, compile_only },
    timeout_seconds ?? 300,
  );

  if (modifies) {
    if (result?.success && dry_run) previews.set(key, Date.now());
    if (result?.success && !dry_run) previews.delete(key); // a repeat needs a fresh preview
    journal({
      title: `${dry_run ? "Preview" : "Change"}: ${transaction_name || "Claude: script"}`,
      explanation,
      outcome: outcomeOf(result, dry_run) + (risky.length ? ` [user-approved: ${risky.join(", ")}]` : ""),
      details: result?.changed || result?.wouldChange,
      code,
    });
  }

  const out = text(result);
  if (result && result.success === false) out.isError = true;
  else if (modifies && dry_run) {
    out.content.push({
      type: "text",
      text: "PREVIEW ONLY - the model is unchanged. Explain this result to the user in plain words and ask for confirmation before applying (same call with dry_run: false).",
    });
  }
  return out;
}

server.registerTool(
  "execute_revit_code",
  {
    title: "Run Revit API code",
    description:
      "The universal tool: compile and run C# against the live Revit 2025 API to do anything the other tools can't - create/modify/delete elements, place families, build views & sheets, analyse geometry, batch-process thousands of elements, cross-check rules. Always dry_run first for edits. Changes appear as one entry in Revit's Undo list. Returns the script's return value, its Log() output, Revit warnings and compile/runtime errors with line numbers.",
    inputSchema: codeSchema,
    annotations: writes,
  },
  safe(runCode),
);

server.registerTool(
  "list_saved_scripts",
  {
    title: "List saved scripts",
    description: "Reusable, tested Revit scripts (built-in and ones saved earlier) with their descriptions and inputs. Check here before writing a new script.",
    inputSchema: {},
    annotations: readOnly,
  },
  safe(async () =>
    text(listScripts().map(({ name, description, mode, inputs, source }) => ({ name, description, mode, inputs, source }))),
  ),
);

server.registerTool(
  "read_saved_script",
  {
    title: "Read a saved script",
    description: "Return the C# source of a saved script, e.g. to adapt it for a slightly different task.",
    inputSchema: { name: z.string() },
    annotations: readOnly,
  },
  safe(async ({ name }) => text(fs.readFileSync(findScript(name).file, "utf8"))),
);

server.registerTool(
  "run_saved_script",
  {
    title: "Run a saved script",
    description: "Run a saved script by name with inputs. Same behaviour and safety as execute_revit_code (use dry_run first).",
    inputSchema: {
      name: z.string(),
      inputs: z.record(z.any()).optional(),
      dry_run: z.boolean().optional(),
      timeout_seconds: z.number().optional(),
      explanation: z.string().optional().describe("Plain-language description (required when applying changes)"),
      allow_risky: z.boolean().optional(),
    },
    annotations: writes,
  },
  safe(async ({ name, inputs, dry_run, timeout_seconds, explanation, allow_risky }) => {
    const script = findScript(name);
    return runCode({
      code: fs.readFileSync(script.file, "utf8"),
      mode: script.mode || "auto",
      dry_run,
      inputs,
      transaction_name: `Claude: ${script.name}`,
      timeout_seconds,
      explanation,
      allow_risky,
    });
  }),
);

server.registerTool(
  "save_script",
  {
    title: "Save a reusable script",
    description:
      "Save a working execute_revit_code script to the user's library so it can be re-run later by name. Only save scripts that have run successfully. Parameterise with inputs (ctx.Str/Num/Bool) instead of hard-coding names.",
    inputSchema: {
      name: z.string().regex(/^[a-z0-9_]+$/).describe("snake_case file name"),
      description: z.string().describe("What it does, in one or two sentences"),
      code: z.string(),
      mode: z.enum(["auto", "manual", "readonly"]).optional(),
      inputs_example: z.record(z.any()).optional().describe("Example inputs, documented in the header"),
      overwrite: z.boolean().optional(),
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
  safe(async ({ name, description, code, mode, inputs_example, overwrite }) => {
    fs.mkdirSync(USER_SCRIPTS, { recursive: true });
    const file = path.join(USER_SCRIPTS, `${name}.cs`);
    if (fs.existsSync(file) && !overwrite) throw new RevitError(`A script named '${name}' already exists. Pass overwrite: true to replace it.`);
    const body = code.replace(/^\s*\/\/\s*@\w+:.*\r?\n/gm, "");
    const header = [
      `// @description: ${description.replace(/\r?\n/g, " ")}`,
      `// @mode: ${mode || "auto"}`,
      `// @inputs: ${JSON.stringify(inputs_example || {})}`,
      `// @saved: ${new Date().toISOString()}`,
      "",
    ].join("\n");
    fs.writeFileSync(file, header + "\n" + body.trimStart());
    return text(`Saved '${name}' to ${file}. Run it later with run_saved_script.`);
  }),
);

const transport = new StdioServerTransport();
await server.connect(transport);
