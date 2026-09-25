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

const HERE = path.dirname(fileURLToPath(import.meta.url));
const APPDATA = process.env.APPDATA || path.join(os.homedir(), ".config");
const CONFIG_PATH = process.env.ACE_REVIT_CONFIG || path.join(APPDATA, "ACE-RevitMCP", "config.json");
const BUILTIN_SCRIPTS = path.join(HERE, "scripts");
const USER_SCRIPTS = process.env.ACE_REVIT_SCRIPTS || path.join(APPDATA, "ACE-RevitMCP", "scripts");
const MAX_TEXT = 120_000;

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
// Server
// ---------------------------------------------------------------------------------------------

const INSTRUCTIONS = `
You are connected to a live Autodesk Revit 2025 session. The user asks for things in plain language
("renumber the rooms on level 2 clockwise", "make a sheet for every level", "check which doors don't
meet 900 mm clear width") and you do the whole job.

HOW TO HANDLE A REQUEST - especially one that is complex in concept even if simple in execution:
1. UNDERSTAND the model first. Call get_model_overview (levels, units, categories, active view), and
   find_elements / get_selection / get_element_details to learn the real names, types and parameters.
   Never guess family, type, level or parameter names - look them up.
2. PLAN. Break the request into concrete steps. If the request is ambiguous in a way that changes the
   result (which level? overwrite existing values?), ask the user one short question. Otherwise choose
   sensible defaults and say what you chose.
3. CHECK THE LIBRARY. list_saved_scripts may already have a tested script for the job.
4. BUILD with execute_revit_code (C#, full Revit API). One script can do an entire multi-step task.
   - First run anything that modifies the model with dry_run: true. It runs completely and is then
     rolled back, so you can check counts and results safely. Then run it for real.
   - For big or risky edits, tell the user what will change (e.g. "142 doors, Mark D-001..D-142")
     before the real run.
   - Return structured data (anonymous objects / lists) so you can verify the outcome.
5. VERIFY. Re-query the model or call view_image to look at the result, then report what changed
   (counts, element ids, anything skipped and why). If Revit reported warnings, mention them.
6. OFFER TO SAVE a reusable script with save_script when the task is likely to be repeated.

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
  use mode "readonly" or "manual" for that.

execute_revit_code SCRIPT SHAPE: write the BODY of a method (statements, local functions allowed; a
top-level 'using X;' line is fine). In scope: doc, uidoc, uiapp, app, ctx, args (inputs as
JsonObject), Log(obj). Common namespaces (Autodesk.Revit.DB, .Architecture, .Structure, .Mechanical,
.Plumbing, .Electrical, Autodesk.Revit.UI, System.Linq, ...) are already imported. 'return' whatever
you want back. Don't redeclare doc/uidoc/app/args/ctx.
ctx helpers: ctx.All<Wall>(), ctx.Instances(BuiltInCategory.OST_Doors), ctx.Types<WallType>(),
ctx.Level("Level 1"), ctx.Levels(), ctx.Selection(), ctx.El(id), ctx.Str/Num/Bool/Ids("input"),
ctx.Mm/M/Cm/ToMm/ToM/Deg, ctx.Transact("name", () => {...}) (mode "manual"), ctx.IsDryRun.
Modes: "auto" (default: whole script in one transaction), "manual" (you create transactions; all
wrapped in a group so dry_run still undoes everything), "readonly" (no transaction).
If a script fails, nothing is changed; read the error (line numbers match your code), fix, retry.
Warnings are auto-dismissed and returned; hard Revit errors roll the transaction back.
`.trim();

const server = new McpServer(
  { name: "ace-revit", version: "1.0.0" },
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
      "Set parameter values on many elements in one undoable transaction. Numbers are Revit internal units (feet); strings are parsed in project display units for length/area parameters (e.g. \"1200\" in a mm project). Yes/No parameters accept true/false. Use dry_run first for bulk changes.",
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
    },
    annotations: writes,
  },
  safe(async (a) => text(await callRevit("set_parameters", a, 300))),
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
};

async function runCode({ code, mode, dry_run, inputs, transaction_name, timeout_seconds }) {
  const timeout = timeout_seconds ?? 300;
  const result = await callRevit("execute_code", { code, mode, dry_run, inputs, transaction_name }, timeout);
  const out = text(result);
  if (result && result.success === false) out.isError = true;
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
    },
    annotations: writes,
  },
  safe(async ({ name, inputs, dry_run, timeout_seconds }) => {
    const script = findScript(name);
    return runCode({
      code: fs.readFileSync(script.file, "utf8"),
      mode: script.mode || "auto",
      dry_run,
      inputs,
      transaction_name: `Claude: ${script.name}`,
      timeout_seconds,
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
