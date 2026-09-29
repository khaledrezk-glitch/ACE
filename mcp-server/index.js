#!/usr/bin/env node
// ACE Revit MCP server.
//
// Claude (Desktop or Code) starts this process over stdio. It forwards tool calls to the ACE add-in
// running inside Revit 2025 (http://localhost:<port>, token-protected), so Claude can understand the
// model, change it safely (explain -> preview -> confirm -> apply -> verify), and write and run
// Revit API C# for any multi-step task.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import fs from "node:fs";
import path from "node:path";
import { GUIDES_DIR, JOURNAL_DIR, ROOT, RevitError, VERSION, callRevit, failure, text } from "./lib/core.js";
import { fingerprint, journal, outcomeOf, previews, requirePreview, screenCode } from "./lib/safety.js";
import { findScript, listScripts, saveScript } from "./lib/scripts.js";
import { logCall, summarize } from "./lib/telemetry.js";
import { buildReport, runChecks } from "./lib/diagnostics.js";
import { lessonsFor, recordLesson, retireLesson } from "./lib/lessons.js";

const INSTRUCTIONS = fs.readFileSync(path.join(ROOT, "instructions.md"), "utf8");

const server = new McpServer(
  { name: "ace-revit", version: VERSION },
  { instructions: INSTRUCTIONS, capabilities: { logging: {} } },
);

/** Registers a tool whose failures become readable tool errors, and logs every call locally. */
function tool(name, config, handler) {
  server.registerTool(name, config, async (args) => {
    const started = Date.now();
    let result;
    let error;
    try {
      result = await handler(args ?? {});
    } catch (err) {
      error = err;
      result = failure(err);
    }
    logCall(summarize(name, args, result, Date.now() - started, error));
    return result;
  });
}

const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const writes = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };
const benign = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };

// ---------------------------------------------------------------------------------------------
// Understand the model
// ---------------------------------------------------------------------------------------------

tool("revit_status", {
  title: "Revit connection status",
  description: "Check that Revit 2025 is running and reachable, the add-in version, and which documents are open. If anything fails, call check_setup.",
  inputSchema: {},
  annotations: readOnly,
}, async () => text(await callRevit("ping", {}, 20)));

tool("get_model_overview", {
  title: "Model overview",
  description: "Summary of the active model: title, path, display units, project info, active view, levels (with elevations), phases, element counts per category. For understanding the building before a task, prefer get_model_brief.",
  inputSchema: {},
  annotations: readOnly,
}, async () => text(await callRevit("get_document_info", {}, 120)));

// The brief is cached per model for a few minutes (it reads the whole model and its links).
const briefCache = new Map();

tool("get_model_brief", {
  title: "Model brief (read this before planning)",
  description: "What you must know about the building before acting: the model and its discipline, levels with room types, room types with counts, areas, levels, numbers and whether they are furnished, the families available for each purpose (real footprint in mm, where the insertion point sits, facing), naming conventions, the linked and other open models (ARC/STR/MEP) and whether their levels and grids line up, and the LESSONS learned earlier about this model, project and ACE work. Call it at the start of any task that touches the model; it is cached for 10 minutes (refresh: true re-reads).",
  inputSchema: {
    refresh: z.boolean().optional(),
    include_families: z.boolean().optional().describe("Default true"),
  },
  annotations: readOnly,
}, async ({ refresh, include_families }) => {
  const status = await callRevit("ping", {}, 20).catch(() => null);
  const key = `${status?.activeDocument}|${include_families !== false}`;
  const hit = briefCache.get(key);
  let brief;
  if (!refresh && hit && Date.now() - hit.at < 10 * 60_000) brief = hit.brief;
  else {
    brief = await callRevit("model_brief", { include_families: include_families !== false }, 300);
    briefCache.set(key, { at: Date.now(), brief });
  }
  const project = String(brief?.model?.project || "").replace(/\s*\(.*\)$/, "");
  const lessons = lessonsFor({ model: brief?.model?.title, project });
  return text({
    ...brief,
    lessons: lessons.length ? lessons : "None yet. When you learn something non-obvious about this model or a technique that worked, save it with remember_lesson.",
  });
});

tool("describe_family", {
  title: "Describe a family",
  description: "Everything needed to place a family correctly: footprint (W x D x H in its own coordinates), where the insertion point sits relative to the footprint centre, placement type and whether it needs a host, type dimensions, nested families, and a sample instance's facing and rotation. Use before placing furniture, fixtures or equipment.",
  inputSchema: { name: z.string().describe("Family or type name, partial is fine") },
  annotations: readOnly,
}, async (a) => text(await callRevit("describe_family", a, 60)));

tool("remember_lesson", {
  title: "Remember a lesson",
  description: "Save something you learned so you (and, if shared, the whole ACE team) get it automatically next time in get_model_brief. Use it when you discover a non-obvious fact about the model (where things are, how it is organised), a technique that worked, a mistake and its fix, or a user preference. One or two plain sentences; be specific (names, numbers). Don't save things that are obvious from the brief.",
  inputSchema: {
    lesson: z.string(),
    kind: z.enum(["fact", "howto", "mistake", "preference"]).optional().describe("Default fact"),
    scope: z.enum(["model", "project", "ace"]).optional().describe("model (default): this model only; project: all its models; ace: everywhere"),
    model: z.string().optional().describe("Model title (from get_model_brief), for scope model"),
    project: z.string().optional().describe("Project name or number, for scope project"),
    tags: z.array(z.string()).optional(),
    share_with_team: z.boolean().optional().describe("Also save to the team folder so colleagues benefit"),
  },
  annotations: benign,
}, async (a) => text(recordLesson(a)));

tool("recall_lessons", {
  title: "Recall lessons",
  description: "Search the lessons learned (personal and team) for a model, project or topic, e.g. before a kind of task you have done before.",
  inputSchema: { query: z.string().optional(), model: z.string().optional(), project: z.string().optional() },
  annotations: readOnly,
}, async (a) => text(lessonsFor({ ...a, limit: 60 })));

tool("forget_lesson", {
  title: "Retire a lesson",
  description: "Retire a lesson that turned out wrong or outdated (by id from the brief or recall_lessons). Say why.",
  inputSchema: { id: z.string(), reason: z.string() },
  annotations: benign,
}, async ({ id, reason }) => text(retireLesson(id, reason)));

tool("model_changes", {
  title: "What changed (change tracker)",
  description: "Compare this model AND its loaded linked models with an earlier snapshot: added, deleted, moved (distance, rotation, level), retyped and changed elements (key values such as Mark and Comments), by category and, in workshared models, by person. since: \"last\" (default: the last snapshot, i.e. since the last check), \"today\", \"yesterday\", \"week\", \"month\" or a date (yyyy-MM-dd). Saves the current state as a new snapshot and an HTML report; show: true opens the Change tracker window in Revit. Use for 'what changed', 'what did the structure team change', coordination after a link update.",
  inputSchema: {
    since: z.string().optional(),
    include_links: z.boolean().optional().describe("Default true"),
    show: z.boolean().optional(),
    save_snapshot: z.boolean().optional().describe("Default true: the current state becomes the new baseline"),
  },
  annotations: benign,
}, async (a) => text(await callRevit("model_changes", a, 900)));

tool("snapshot_model", {
  title: "Take a change-tracker snapshot",
  description: "Save the current state of this model and its loaded links as a named snapshot (e.g. before an issue, or 'Stage 3 submission'), to compare against later with model_changes. Snapshots are also taken automatically when a model is opened and after saves or syncs.",
  inputSchema: { label: z.string().optional(), include_links: z.boolean().optional() },
  annotations: benign,
}, async (a) => text(await callRevit("snapshot_model", a, 900)));

tool("list_snapshots", {
  title: "List snapshots",
  description: "The change-tracker snapshots available for this model and its links (newest first).",
  inputSchema: {},
  annotations: readOnly,
}, async () => text(await callRevit("list_snapshots", {}, 60)));

tool("model_dashboard", {
  title: "Model insights dashboard",
  description: "Read-only health check of the open model: a 0-100 health score with every audit finding (warnings, imported CAD, in-place families, rooms not enclosed or without doors, narrow doors, duplicate marks, views not on sheets, view templates, parameter completeness, sheet title block data, project information), warnings by type, per-level statistics, Claude's activity and the status of every ACE tool. Saves an ACE-branded HTML report (path in htmlReport) in Documents\\ACE Insights. show: true also opens the dashboard inside Revit. Use for 'how healthy is this model', 'dashboard', 'status report', 'audit'.",
  inputSchema: { show: z.boolean().optional().describe("Also open the dashboard window in Revit") },
  annotations: readOnly,
}, async (a) => text(await callRevit("get_model_insights", a, 600)));

tool("get_selection", {
  title: "Current selection",
  description: "Elements the user has selected in Revit. Use when they say 'these', 'the selected ...'.",
  inputSchema: { include_parameters: z.boolean().optional().describe("Also return every instance parameter") },
  annotations: readOnly,
}, async (a) => text(await callRevit("get_selection", a)));

tool("find_elements", {
  title: "Find elements",
  description: "Search the model by categories, level, view, name/type text and parameter conditions; optionally return chosen parameter values. Returns the exact total plus up to 'limit' element summaries.",
  inputSchema: {
    categories: z.array(z.string()).optional().describe('e.g. ["Walls"], ["Doors","Windows"], ["OST_StructuralColumns"]'),
    level: z.string().optional().describe("Level name, exact"),
    in_view: z.string().optional().describe('View name, or "active"'),
    name_contains: z.string().optional().describe("Matches element, family or type name"),
    parameter_filters: z.array(z.object({
      name: z.string().describe('Parameter name, e.g. "Comments", or BuiltInParameter like "ALL_MODEL_MARK"'),
      op: z.enum(["equals", "not_equals", "contains", "empty", "not_empty", "gt", "lt", "exists", "missing"]).optional(),
      value: z.union([z.string(), z.number()]).optional().describe("For gt/lt: number in project display units"),
    })).optional(),
    include_parameters: z.array(z.string()).optional().describe("Parameter values to return per element"),
    types_only: z.boolean().optional().describe("Return element TYPES instead of instances"),
    limit: z.number().int().optional().describe("Max elements returned (default 200, max 5000); 'total' is always exact"),
  },
  annotations: readOnly,
}, async (a) => text(await callRevit("query_elements", a, 180)));

tool("get_element_details", {
  title: "Element details",
  description: "Full details for up to 50 elements: every instance parameter (value + display), type parameters, location, bounding box, host, room.",
  inputSchema: {
    ids: z.array(z.number().int()).min(1).max(50),
    include_type_parameters: z.boolean().optional().describe("Default true"),
  },
  annotations: readOnly,
}, async (a) => text(await callRevit("get_element_details", a)));

tool("describe_category", {
  title: "Parameters of a category",
  description: "Discover which parameters a category really has in THIS model (instance vs type, storage type, unit spec, % filled, sample values) by sampling its elements. Use before reading or writing parameters, and for data-quality questions.",
  inputSchema: {
    category: z.string().describe('e.g. "Doors", "Rooms", "Walls", "OST_MechanicalEquipment"'),
    sample: z.number().int().optional().describe("Elements to sample (default 300)"),
  },
  annotations: readOnly,
}, async (a) => text(await callRevit("describe_category", a, 180)));

tool("list_types", {
  title: "Loaded families & types",
  description: "Loaded family types and system types (optionally for one category / name filter) with ids, placement type, active state and instance counts. Use before placing elements or changing types.",
  inputSchema: {
    category: z.string().optional(),
    name_contains: z.string().optional(),
    count_instances: z.boolean().optional().describe("Default true"),
    limit: z.number().int().optional().describe("Default 300"),
  },
  annotations: readOnly,
}, async (a) => text(await callRevit("list_types", a, 180)));

tool("list_views", {
  title: "List views",
  description: "All views and sheets (id, name, type, level, sheet number). Optional filter by view type: FloorPlan, CeilingPlan, ThreeD, Section, Elevation, DrawingSheet, Schedule, Legend, AreaPlan, EngineeringPlan...",
  inputSchema: { view_type: z.string().optional() },
  annotations: readOnly,
}, async (a) => text(await callRevit("list_views", a)));

tool("view_image", {
  title: "Look at a view",
  description: "Export a view (default: active view) as a PNG and return it so you can SEE the model: verify edits, check layouts, understand a plan.",
  inputSchema: {
    view_name: z.string().optional(),
    view_id: z.number().int().optional(),
    pixel_size: z.number().int().optional().describe("Width in pixels, 256-4000 (default 1400)"),
  },
  annotations: readOnly,
}, async (a) => {
  const r = await callRevit("export_view_image", a, 180);
  return {
    content: [
      { type: "image", data: r.base64, mimeType: r.mimeType || "image/png" },
      { type: "text", text: `View "${r.view}" (${r.viewType}). Saved at ${r.path}` },
    ],
  };
});

// ---------------------------------------------------------------------------------------------
// Knowledge: the real API + expert guides
// ---------------------------------------------------------------------------------------------

tool("revit_api_lookup", {
  title: "Look up the Revit API",
  description: "Read the REAL Revit API installed on this machine by reflection: type members with exact signatures, overloads, enum values, obsolete flags. Use whenever you're not certain of a class, method, overload, BuiltInParameter or BuiltInCategory name. Examples: \"Wall.Create\", \"ViewSheet\", \"BuiltInParameter.DOOR\", \"ParameterFilterRuleFactory\", \"IsPointInRoom\".",
  inputSchema: { query: z.string() },
  annotations: readOnly,
}, async ({ query }) => text(await callRevit("api_lookup", { query }, 60)));

const guideTopics = () => fs.readdirSync(GUIDES_DIR).filter((f) => f.endsWith(".md")).map((f) => f.replace(/\.md$/, "")).sort();

tool("revit_guide", {
  title: "Expert guide",
  description: "Expert, verified Revit 2025 API guidance for hard tasks. Topics: planning, performance, transactions, geometry, families-and-types, views-and-sheets, parameters-and-units, mep-and-structure, links-and-worksharing. Call without a topic to list them.",
  inputSchema: { topic: z.string().optional() },
  annotations: readOnly,
}, async ({ topic }) => {
  const topics = guideTopics();
  if (!topic) return text(`Available guides: ${topics.join(", ")}`);
  const match = topics.find((t) => t === topic || t.startsWith(topic.toLowerCase()));
  if (!match) throw new RevitError(`No guide '${topic}'. Available: ${topics.join(", ")}`);
  return text(fs.readFileSync(path.join(GUIDES_DIR, `${match}.md`), "utf8"));
});

// ---------------------------------------------------------------------------------------------
// Change the model (all gated by the preview-before-apply protocol)
// ---------------------------------------------------------------------------------------------

tool("set_parameters", {
  title: "Set parameter values",
  description: "Set parameter values on many elements in one undoable transaction. Numbers are internal units (feet); strings for length/area parameters use project display units (e.g. \"1200\" in a mm project). Yes/No accepts true/false. Must be previewed with dry_run: true and confirmed by the user before applying.",
  inputSchema: {
    changes: z.array(z.object({
      id: z.number().int(),
      parameter: z.string(),
      value: z.union([z.string(), z.number(), z.boolean()]),
      target: z.enum(["instance", "type"]).optional().describe('"type" edits the element\'s type (affects all instances)'),
    })).min(1),
    dry_run: z.boolean().optional(),
    explanation: z.string().optional().describe("Plain-language description (required when applying)"),
  },
  annotations: writes,
}, async ({ changes, dry_run, explanation }) => {
  const key = fingerprint("params", changes);
  if (!dry_run) {
    requirePreview(key);
    if (!explanation) throw new RevitError("Give 'explanation': a plain sentence describing this change for the activity journal.");
  }
  const result = await callRevit("set_parameters", { changes, dry_run }, 300);
  if (result?.alreadyApplied) {
    previews.delete(key);
    return text(result);
  }
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
});

tool("select_elements", {
  title: "Select & zoom",
  description: "Select elements in Revit's UI (and zoom to them) so the user can see what you mean. Empty list clears the selection.",
  inputSchema: { ids: z.array(z.number().int()), zoom: z.boolean().optional().describe("Default true") },
  annotations: { ...readOnly, idempotentHint: true },
}, async (a) => text(await callRevit("select_elements", a)));

const codeSchema = {
  code: z.string().describe("C# method body using the Revit API (see server instructions for what's in scope)."),
  mode: z.enum(["auto", "manual", "readonly"]).optional().describe("auto (default) = one transaction; manual = your own transactions; readonly = always rolled back"),
  dry_run: z.boolean().optional().describe("Run fully, then roll back every change. Required before applying edits."),
  inputs: z.record(z.any()).optional().describe("Values available as args / ctx.Str(...) / ctx.Num(...)"),
  transaction_name: z.string().optional().describe("Shown in Revit's Undo list, e.g. 'Claude: renumber rooms'"),
  timeout_seconds: z.number().optional().describe("Default 300"),
  explanation: z.string().optional().describe("Plain-language description of what this run does (required when applying changes; recorded in the activity journal)"),
  allow_risky: z.boolean().optional().describe("Only after the user explicitly agreed: allow code touching files, programs, network, or saving/closing/syncing models"),
  compile_only: z.boolean().optional().describe("Only check that the code compiles; run nothing"),
  preview_image: z.boolean().optional().describe("With dry_run: also return pictures (plan + 3D) of the area that would change, with the changed elements highlighted. Show them to the user before asking for confirmation. Use it for anything visual: placing, moving or creating elements."),
};

async function runCode({ code, mode, dry_run, inputs, transaction_name, timeout_seconds, explanation, allow_risky, compile_only, preview_image }) {
  mode = mode || "auto";
  const risky = screenCode(code, allow_risky);
  const modifies = mode !== "readonly" && !compile_only;
  const key = fingerprint("code", { code, mode, inputs: inputs || {} });
  if (modifies && !dry_run) {
    requirePreview(key);
    if (!explanation) throw new RevitError("Give 'explanation': one or two plain sentences saying what this change does. It is recorded in the user's activity journal.");
  }

  const result = await callRevit("execute_code", { code, mode, dry_run, inputs, transaction_name, compile_only, preview_image: !!(preview_image && dry_run) }, timeout_seconds ?? 300);
  // Preview pictures travel as image blocks, not as base64 inside the JSON text.
  const pictures = Array.isArray(result?.previewImages) ? result.previewImages : [];
  if (result && pictures.length) result.previewImages = pictures.map((p) => `${p.view} image attached`);

  // The user may have decided in the ACE Companion panel inside Revit (Apply / Cancel) - the add-in reports it.
  if (result?.alreadyApplied) {
    previews.delete(key);
    return text({ ...result, instruction: "Already applied by the user in Revit. Do not re-apply. Verify with a read-only query and report the outcome." });
  }
  if (result?.stage === "rejected") {
    previews.delete(key);
    return { isError: true, content: [{ type: "text", text: result.error }] };
  }

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
  for (const p of pictures) {
    if (p?.base64) out.content.push({ type: "image", data: p.base64, mimeType: p.mimeType || "image/png" });
  }
  if (pictures.length) out.content.push({ type: "text", text: `The ${pictures.map((p) => p.view).join(" and ")} picture(s) above show the previewed change (changed elements highlighted in red). Describe what they show to the user.` });
  if (result && result.success === false) {
    out.isError = true;
    if (result.stage === "compile") out.content.push({ type: "text", text: "Tip: check uncertain members with revit_api_lookup before retrying." });
  } else if (modifies && dry_run) {
    out.content.push({ type: "text", text: "PREVIEW ONLY - the model is unchanged. Explain this result to the user in plain words and ask for confirmation before applying (same call with dry_run: false). This preview now also appears in the ACE Companion panel in Revit, where the user can click Apply or Cancel instead of answering here." });
  }
  return out;
}

tool("execute_revit_code", {
  title: "Run Revit API code",
  description: "The universal tool: compile and run C# against the live Revit 2025 API to do anything the other tools can't: create/modify/delete elements, place families, build views and sheets, analyse geometry, batch-process thousands of elements, cross-check rules, work across open documents. Edits must be previewed (dry_run) and confirmed first; each applied run is ONE undo step. Returns the return value, Log() output, what changed, timing, Revit warnings, and compile/runtime errors with line numbers.",
  inputSchema: codeSchema,
  annotations: writes,
}, runCode);

tool("backup_model", {
  title: "Back up the model file",
  description: "Copy the model's .rvt file to %APPDATA%\\ACE-RevitMCP\\backups (timestamped). The open model is not changed. Offer before large changes. save_first: true saves the model first so unsaved work is included (only with the user's permission).",
  inputSchema: { save_first: z.boolean().optional() },
  annotations: benign,
}, async (a) => {
  const r = await callRevit("backup_model", a, 600);
  journal({ title: "Backup", outcome: `Backup created at ${r.backup}${r.savedBeforeBackup ? " (model saved first)" : ""}` });
  return text(r);
});

tool("undo_last_claude_change", {
  title: "Undo Claude's last change",
  description: "Undo the most recent change Claude applied, as one step. Refuses if the user changed anything afterwards, so the user's own work is never undone.",
  inputSchema: {},
  annotations: benign,
}, async () => {
  const r = await callRevit("undo_last_claude_change", {}, 60);
  journal({ title: "Undo", outcome: `Undid "${r.undoRequested}"` });
  return text(r);
});

// ---------------------------------------------------------------------------------------------
// Script library (built-in < team < user)
// ---------------------------------------------------------------------------------------------

tool("list_saved_scripts", {
  title: "List saved scripts",
  description: "Reusable, tested Revit scripts (built-in, team-shared and personal) with descriptions and inputs. Check here before writing a new script.",
  inputSchema: {},
  annotations: readOnly,
}, async () => text(listScripts().map(({ name, description, mode, inputs, source, saved }) => ({ name, description, mode, inputs, source, saved }))));

tool("read_saved_script", {
  title: "Read a saved script",
  description: "Return the C# source of a saved script, e.g. to adapt it for a slightly different task.",
  inputSchema: { name: z.string() },
  annotations: readOnly,
}, async ({ name }) => text(fs.readFileSync(findScript(name).file, "utf8")));

tool("run_saved_script", {
  title: "Run a saved script",
  description: "Run a saved script by name with inputs. Same behaviour and safety as execute_revit_code (edits need a dry_run preview and confirmation first).",
  inputSchema: {
    name: z.string(),
    inputs: z.record(z.any()).optional(),
    dry_run: z.boolean().optional(),
    preview_image: z.boolean().optional().describe("With dry_run: also return plan + 3D pictures of the previewed change"),
    timeout_seconds: z.number().optional(),
    explanation: z.string().optional().describe("Plain-language description (required when applying changes)"),
    allow_risky: z.boolean().optional(),
  },
  annotations: writes,
}, async ({ name, inputs, dry_run, preview_image, timeout_seconds, explanation, allow_risky }) => {
  const script = findScript(name);
  return runCode({
    code: fs.readFileSync(script.file, "utf8"),
    mode: script.mode || "auto",
    dry_run, preview_image, inputs, timeout_seconds, explanation, allow_risky,
    // The name is the card title in the ACE panel: include the key inputs so several options can be told apart.
    transaction_name: `Claude: ${script.name}${inputs && Object.keys(inputs).length
      ? ` (${Object.entries(inputs).slice(0, 3).map(([k, v]) => `${k.replace(/_/g, " ")} ${typeof v === "object" ? JSON.stringify(v) : v}`).join(", ")})`
      : ""}`.slice(0, 100),
  });
});

tool("save_script", {
  title: "Save a reusable script",
  description: "Save a working script so it can be re-run by name. Only save scripts that ran successfully; parameterise with inputs (ctx.Str/Num/Bool) instead of hard-coding names. scope \"team\" shares it with colleagues via the team folder (if configured).",
  inputSchema: {
    name: z.string().regex(/^[a-z0-9_]+$/).describe("snake_case file name"),
    description: z.string().describe("What it does, in one or two sentences"),
    code: z.string(),
    mode: z.enum(["auto", "manual", "readonly"]).optional(),
    inputs_example: z.record(z.any()).optional().describe("Example inputs, documented in the header"),
    overwrite: z.boolean().optional(),
    scope: z.enum(["user", "team"]).optional().describe("Default user"),
  },
  annotations: { ...benign, idempotentHint: true },
}, async (a) => {
  const file = saveScript(a);
  return text(`Saved '${a.name}' to ${file}. Run it later with run_saved_script.`);
});

// ---------------------------------------------------------------------------------------------
// Transparency & support
// ---------------------------------------------------------------------------------------------

tool("get_activity_log", {
  title: "Activity journal",
  description: "What Claude previewed and changed in Revit on a day (plain-language entries with results). Use when the user asks what was done.",
  inputSchema: { date: z.string().optional().describe("YYYY-MM-DD, default today") },
  annotations: readOnly,
}, async ({ date }) => {
  const day = date || new Date().toISOString().slice(0, 10);
  const file = path.join(JOURNAL_DIR, `${day}.md`);
  if (!fs.existsSync(file)) return text(`No activity recorded for ${day}. Journal folder: ${JOURNAL_DIR}`);
  const content = fs.readFileSync(file, "utf8").replace(/<details>[\s\S]*?<\/details>/g, "(code omitted)");
  return text(`Journal file: ${file}\n\n${content}`);
});

tool("check_setup", {
  title: "Check the setup",
  description: "Diagnose the whole ACE Revit setup: config, add-in files, Claude registration, Revit connection, versions, C# compiler, script library, recent failures. Each problem comes with a fix. Use when anything fails.",
  inputSchema: {},
  annotations: readOnly,
}, async () => {
  const checks = await runChecks();
  const lines = checks.map((c) => `${{ ok: "OK  ", warn: "WARN", fail: "FAIL", info: "INFO" }[c.status]} ${c.name}: ${c.detail}${c.fix ? `\n     fix: ${c.fix}` : ""}`);
  const bad = checks.filter((c) => c.status === "fail").length;
  return text(`${bad ? `${bad} problem(s) found.` : "Everything essential works."}\n\n${lines.join("\n")}`);
});

tool("report_issue", {
  title: "Create an issue / improvement report",
  description: "Write a Markdown report (health checks, environment, recent activity and failures with code, add-in log, recommendations) that the user can send to the ACE tool maintainers. kind \"issue\" for problems, \"improvement\" for missing capabilities or better workflows, \"health\" for a status snapshot. Fill in what happened in plain words.",
  inputSchema: {
    kind: z.enum(["issue", "improvement", "health"]).optional(),
    what_happened: z.string().describe("The problem or the improvement idea, in the user's words plus your own diagnosis"),
    expected: z.string().optional(),
    request: z.string().optional().describe("What the user asked for when it happened"),
  },
  annotations: benign,
}, async ({ kind, what_happened, expected, request }) => {
  const r = await buildReport({ kind: kind || "issue", note: what_happened, expected, steps: request });
  const recs = r.recommendations.map((x) => `- [${x.severity}] ${x.title}: ${x.detail}`).join("\n");
  return text(`Report saved to:\n${r.file}${r.shared ? `\nCopied to the team folder:\n${r.shared}` : ""}\n\nTell the user where it is and that they can send it to the ACE tool maintainers.\n\nRecommendations:\n${recs}`);
});

// ---------------------------------------------------------------------------------------------
// Prompts (appear in Claude's prompt / attachment menu)
// ---------------------------------------------------------------------------------------------

server.registerPrompt("revit-task", {
  title: "Do a Revit task (safe, step by step)",
  description: "Plan and execute a Revit task with the full safety protocol.",
  argsSchema: { task: z.string().describe("What you want done in Revit") },
}, ({ task }) => ({
  messages: [{ role: "user", content: { type: "text", text:
    `In the open Revit model, please do this: ${task}\n\n` +
    "Work like a senior BIM manager: first read the revit_guide 'planning', inspect the model (overview, describe_category, list_types), " +
    "then explain your plan in plain words, preview it (dry run) and show me exactly what would change, and wait for my OK before applying. " +
    "Afterwards verify independently and tell me how to undo it." } }],
}));

server.registerPrompt("model-qa", {
  title: "Model QA check",
  description: "Run a read-only quality audit of the open model and explain it simply.",
  argsSchema: { focus: z.string().optional().describe("Optional focus, e.g. 'doors and rooms'") },
}, ({ focus }) => ({
  messages: [{ role: "user", content: { type: "text", text:
    `Run a read-only QA audit of the open Revit model${focus ? ` focusing on ${focus}` : ""}. Use the audit_model, parameter_completeness, ` +
    "rooms_without_doors and door_width_check scripts where relevant, then explain the top issues in simple words, ranked by importance, " +
    "with element ids and a suggested fix for each. Don't change anything." } }],
}));

server.registerPrompt("report-problem", {
  title: "Report a problem with the tool",
  description: "Diagnose the setup and write an issue report for the maintainers.",
  argsSchema: { problem: z.string().describe("What went wrong") },
}, ({ problem }) => ({
  messages: [{ role: "user", content: { type: "text", text:
    `Something went wrong with the ACE Revit tool: ${problem}\n\nRun check_setup, try to fix what you can, then call report_issue with a clear description ` +
    "(including your diagnosis) and tell me where the report file is so I can send it to the maintainers." } }],
}));

await server.connect(new StdioServerTransport());
