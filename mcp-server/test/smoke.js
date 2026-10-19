// Smoke test: runs the MCP server against a fake Revit bridge and exercises every tool path.
// Usage: node test/smoke.js   (no Revit needed)
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-smoke-"));
const token = "test-token";
const seen = [];

const bridge = http.createServer((req, res) => {
  if (req.method === "GET" && req.url === "/health") { res.setHeader("Content-Type", "application/json"); return res.end(JSON.stringify({ ok: true, service: "ace-revit-mcp", revitVersion: "2025" })); }
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    const reply = (o) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(o)); };
    if (req.headers["x-ace-token"] !== token) return reply({ ok: false, error: "bad token" });
    const { command, args } = JSON.parse(body);
    seen.push({ command, args });
    switch (command) {
      case "ping": return reply({ ok: true, result: { revit: "Autodesk Revit 2025", addinVersion: "1.2.0.0", activeDocument: "Test.rvt" } });
      case "api_lookup": return reply({ ok: true, result: { query: args.query, results: [{ type: "Autodesk.Revit.DB.Wall", members: [{ signature: "static Wall Create(Document document, Curve curve, ElementId levelId, bool structural)" }] }] } });
      case "export_view_image": return reply({ ok: true, result: { view: "Level 1", viewType: "FloorPlan", path: "x.png", mimeType: "image/png", base64: "iVBORw0KGgo=" } });
      case "execute_code":
        if (args.code.includes("PANEL_APPLIED") && !args.dry_run) return reply({ ok: true, result: { success: true, alreadyApplied: true, note: "applied in panel" } });
        if (args.code.includes("PANEL_REJECTED") && !args.dry_run) return reply({ ok: true, result: { success: false, stage: "rejected", error: "The user cancelled this change in the ACE Companion panel" } });
        if (args.code.includes("PICTURE") && args.dry_run) return reply({ ok: true, result: { success: true, transaction: "RolledBack", wouldChange: { added: 2, modified: 0, deleted: 0 }, previewImages: [{ view: "Plan", mimeType: "image/png", base64: "iVBORw0KGgo=" }, { view: "3D", mimeType: "image/png", base64: "iVBORw0KGgo=" }], echoPreview: args.preview_image } });
        if (args.code.includes("SEMANTIC_RISK") && !args.allow_risky) return reply({ ok: true, result: { success: false, stage: "risky", error: "Blocked for safety: this code reads or changes files on disk." } });
        if (args.code.includes("boom")) return reply({ ok: true, result: { success: false, stage: "compile", errors: ["line 1: CS0103"] } });
        return reply({ ok: true, result: { success: true, transaction: args.dry_run ? "RolledBack" : "Committed", result: 42, echoMode: args.mode, inputs: args.inputs, [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 2, deleted: 0 } } });
      case "project_rules": {
        const ex = JSON.parse(fs.readFileSync(new URL("../bep/worksets.example.json", import.meta.url), "utf8"));
        return reply({ ok: true, result: { model: "Test", section: args.section, source: "project: C:/Projects/Test/ACE Vault/project-requirements.json", hash: "abc", rules: ex } });
      }
      case "compliance":
        return reply({ ok: true, result: { run: { id: "r1", model: "Test" }, summary: { met: 1, "not-met": 1 }, ranNow: !!args.run, echo: args, results: [{ requirement: "REQ-EIR-0001", status: "not-met", evidence: "Doors: 3 of 40" }] } });
      case "audit_summary":
        return reply({ ok: true, result: args.summary ? { success: true, dryRun: !!args.dry_run, after: args.summary } : { model: "Test", summary: null } });
      case "assign_worksets":
        return reply({ ok: true, result: { success: true, checkOnly: !!args.check_only, transaction: args.check_only ? undefined : args.dry_run ? "RolledBack" : "Committed", rules: `${args.rules_name} (${args.rules_source}), ${args.rules?.length} rules`, firstRule: args.rules?.[0]?.workset } });
      case "parameter_fill":
        if (args.expect_model && args.expect_model !== "Test") return reply({ ok: true, result: { success: false, stage: "wrong_model", error: `The preview ran in ${args.expect_model}; the active model is Test.` } });
        return reply({ ok: true, result: { success: true, model: "Test", dryRun: !!args.dry_run, checkOnly: !!args.check_only || undefined, echo: args,
          ...(args.fill && !args.dry_run && !args.check_only ? { after: { rows: args.rows.map((r) => ({ key: r.key, name: r.name, blank: 0, filled: 1, na: 1, status: "complete-with-na" })), summary: { rows: args.rows.length, complete: 0, withBlanks: 0, missingEverywhere: 0, notText: 0, notInGeneral: 0, withNA: args.rows.length } } } : {}),
          documents: [{ model: "Test", discipline: args.scope?.models?.Test ?? null, disciplineSource: args.scope?.models?.Test ? "mapping" : null, preflight: { workshared: false, foreign: {} },
            rows: args.rows.map((r) => ({ ...r, checked: 2, missing: 0, blank: 1, filled: 1, na: 0, onType: 0, storageType: "String", isText: true, status: "blanks", modelGuid: r.guid ?? null, value: r.group === "project" ? "x" : undefined })),
            summary: { rows: args.rows.length, complete: 0, withBlanks: args.rows.length, missingEverywhere: 0, notText: 0, notInGeneral: 0, withNA: 0, outOfScope: 0, wrongGuid: 0, notBound: 0, wrongLevel: 0, foreign: 0 } }],
          ...(args.fill ? { fill: { set: 2, skipped: [{ id: 9, name: "x", reason: "read-only" }] }, [args.dry_run ? "wouldChange" : "changed"]: { added: 0, modified: 2, deleted: 0 } } : {}) } });
      case "presentation_standard":
        return reply({ ok: true, result: { success: true, checkOnly: !!args.check_only, dryRun: !!args.dry_run, standard: "ACE default: 3 mm for 1:50 and larger, 2.5 mm for smaller", [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 40, deleted: 0 }, echo: args } });
      case "set_parameters":
        return reply({ ok: true, result: { applied: args.changes.length, failed: 0, dryRun: !!args.dry_run, results: [] } });
      case "coordination_report": return reply({ ok: true, result: { htmlReport: "C:/x/Model - Coordination.html", excelList: "C:/x/Model - Coordination.csv", issues: 7, withPictures: 7 } });
      case "focus_clash": return reply({ ok: true, result: { view: "ACE Clash View - khaled", clash: args.key, intersectionMm: "200 x 200 x 250", a: "Pipes (red)", b: "Floors (green)" } });
      case "working_mode": return reply({ ok: true, result: { mode: args.mode ? "Coordination" : "All tools", focus: ["Coordination"], leadWith: ["run_clash_test"] } });
      case "pending_changes": return reply({ ok: true, result: { waiting: [{ card: 1, title: "Claude: Test fit (301, 10 m2)" }, { card: 2, title: "Claude: Test fit (301, 8 m2)" }], recentlyDecided: [] } });
      case "model_brief":
        return reply({ ok: true, result: { model: { title: "Test", project: "Tower (P-01)", discipline: "ARC" }, rooms: [{ type: "Office Unit", count: 3 }], otherModels: [{ relation: "link", discipline: "STR", alignment: { verdict: "aligned" } }] } });
      case "describe_family":
        return reply({ ok: true, result: [{ name: "Chair-Breuer : Chair-Breuer", footprintMm: "560 x 600 x 800 (W x D x H, local)" }] });
      case "model_changes":
        return reply({ ok: true, result: { since: args.since || "last", models: [{ model: "Test", relation: "this model", added: 3, moved: 2 }, { model: "Test-STR", relation: "link", moved: 12 }] } });
      case "run_clash_test":
        return reply({ ok: true, result: { tests: [{ test: args.test || "all", open: 4, byResponsible: { "MEP (HVAC)": 3, "ARC": 1 } }] } });
      case "get_model_insights":
        return reply({ ok: true, result: { model: "Test", score: 81, grade: "Fair", checks: [{ check: "Revit warnings", status: "warn" }], htmlReport: "C:/Users/x/Documents/ACE Insights/Test/Test - Insights.html", shown: !!args.show } });
      case "backup_model":
        return reply({ ok: true, result: { backup: "C:/backups/model.rvt", savedBeforeBackup: false } });
      case "undo_last_claude_change":
        return reply({ ok: true, result: { undoRequested: "Claude: test" } });
      default: return reply({ ok: true, result: { command, args } });
    }
  });
});
await new Promise((r) => bridge.listen(0, "127.0.0.1", r));
const port = bridge.address().port;
fs.writeFileSync(path.join(tmp, "config.json"), JSON.stringify({ port, token }));

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [fileURLToPath(new URL("../index.js", import.meta.url))],   // a URL pathname is /D:/... on Windows
  env: { ...process.env, ACE_REVIT_CONFIG: path.join(tmp, "config.json"), ACE_REVIT_URL: `http://127.0.0.1:${port}`, ACE_REVIT_SCRIPTS: path.join(tmp, "scripts"), ACE_REVIT_JOURNAL: path.join(tmp, "journal"), ACE_REVIT_LOGS: path.join(tmp, "logs"), ACE_REVIT_REPORTS: path.join(tmp, "reports"), ACE_TEAM_SCRIPTS: path.join(tmp, "team"), ACE_TEAM_REPORTS: path.join(tmp, "team-reports"), ACE_REVIT_LESSONS: path.join(tmp, "lessons"), ACE_TEAM_LESSONS: path.join(tmp, "team-lessons") },
});
const client = new Client({ name: "smoke", version: "1.0.0" });
await client.connect(transport);

const call = async (name, args = {}) => client.callTool({ name, arguments: args });
const json = (r) => JSON.parse(r.content[0].text);

const tools = (await client.listTools()).tools.map((t) => t.name).sort();
assert.deepEqual(tools, ["assign_worksets", "backup_model", "check_setup", "clash_view", "coordination_report", "describe_category", "describe_family", "execute_revit_code", "find_elements", "get_activity_log", "get_element_details", "get_model_brief", "get_selection", "lessons", "list_types", "list_views", "list_warnings", "model_changes", "model_dashboard", "open_view", "parameter_check", "parameter_fill", "pending_changes", "presentation_standard", "project_rules", "report_issue", "revit_api_lookup", "revit_guide", "revit_status", "run_clash_test", "run_saved_script", "save_script", "saved_scripts", "select_elements", "set_clash_status", "set_parameters", "snapshots", "undo_last_claude_change", "vault", "view_image", "working_mode"]);
assert.match(client.getInstructions(), /SAFETY PROTOCOL/);
assert.match(client.getInstructions(), /revit_api_lookup/);
const prompts = (await client.listPrompts()).prompts.map((p) => p.name).sort();
assert.deepEqual(prompts, ["model-qa", "report-problem", "revit-task"]);
const pr = await client.getPrompt({ name: "revit-task", arguments: { task: "renumber rooms" } });
assert.match(pr.messages[0].content.text, /renumber rooms/);

// --- knowledge tools ---
assert.match((await call("revit_guide")).content[0].text, /performance/);
assert.match((await call("revit_guide", { topic: "perf" })).content[0].text, /quick filters/);
assert.equal((await call("revit_guide", { topic: "nope" })).isError, true);
assert.match(json(await call("revit_api_lookup", { query: "Wall.Create" })).results[0].members[0].signature, /Wall Create/);
assert.equal(json(await call("describe_category", { category: "Doors" })).command, "describe_category");
assert.equal(json(await call("list_types", { category: "Doors" })).command, "list_types");

assert.equal(json(await call("revit_status")).activeDocument, "Test.rvt");
// --- brief + lessons ---
let brief = json(await call("get_model_brief"));
assert.equal(brief.model.title, "Test");
assert.ok(brief.lessons.every((l) => l.source === "built-in"), "only built-in lessons at first");
const saved = json(await call("lessons", { action: "remember",  lesson: "The offices are rooms 301, 401 and 501 on L3 to L5.", model: "Test", share_with_team: true }));
assert.equal(saved.sharedWithTeam, true);
assert.match(json(await call("lessons", { action: "remember",  lesson: "The offices are rooms 301, 401 and 501 on L3 to L5.", model: "Test" })).note, /Already known/);
json(await call("lessons", { action: "remember",  lesson: "Chair-Breuer faces +Y when unrotated.", kind: "howto", scope: "ace" }));
json(await call("lessons", { action: "remember",  lesson: "Use 1.2 m aisles for Tower offices.", kind: "preference", scope: "project", project: "Tower" }));
json(await call("lessons", { action: "remember",  lesson: "Something about another model entirely.", model: "Other" }));
brief = json(await call("get_model_brief", { refresh: true }));
const myLessons = brief.lessons.filter((l) => l.source !== "built-in");
assert.equal(myLessons.length, 3, "model + project + ace lessons, not the other model's");
assert.ok(brief.lessons.some((l) => l.source === "built-in" && /Chair-Breuer/.test(l.lesson)), "built-in ACE lessons are served too");
assert.equal((await call("lessons", { action: "remember",  lesson: "x" })).isError, true);
assert.ok(json(await call("lessons", { action: "recall",  query: "chair" })).length >= 2);
json(await call("lessons", { action: "forget",  id: saved.id, reason: "test" }));
assert.equal(json(await call("get_model_brief", { refresh: true })).lessons.filter((l) => l.source !== "built-in").length, 2);
assert.match(json(await call("describe_family", { name: "Chair" }))[0].footprintMm, /560/);
const clash = json(await call("run_clash_test", { test: "STR vs MEP" }));
assert.equal(clash.tests[0].byResponsible["MEP (HVAC)"], 3);
const ch = json(await call("model_changes", { since: "week" }));
assert.equal(ch.since, "week");
assert.equal(ch.models[1].relation, "link");
assert.equal(json(await call("snapshots", { action: "take", label: "Stage 3" })).command, "snapshot_model");
const dash = json(await call("model_dashboard", { show: true }));
assert.equal(dash.score, 81);
assert.equal(dash.shown, true);
assert.equal(json(await call("find_elements", { categories: ["Walls"], limit: 5 })).command, "query_elements");

const img = await call("view_image", {});
assert.equal(img.content[0].type, "image");

// --- Safety gate: no real change without an identical preview ---
const edit = { code: "foreach (var w in ctx.All<Wall>()) w.LookupParameter(\"Comments\").Set(\"x\");", inputs: { a: 1 } };
const blocked = await call("execute_revit_code", { ...edit, explanation: "test" });
assert.equal(blocked.isError, true);
assert.match(blocked.content[0].text, /not been previewed/);
assert.match((await call("execute_revit_code", { ...edit, dry_run: true })).content[0].text, /Give 'explanation' with the preview/, "a preview that changes the model must explain itself");
await call("execute_revit_code", { ...edit, dry_run: true, explanation: "Step text", plan: ["Renumber doors", "Tag doors"], step: 1 });
const sent = seen.filter((x) => x.command === "execute_code").at(-1).args;
assert.ok(sent.explanation === "Step text" && sent.plan.length === 2 && sent.step === 1, "explanation, plan and step reach the Apply card");
const preview = await call("execute_revit_code", { ...edit, dry_run: true, explanation: "preview for the test" });
assert.equal(json(preview).transaction, "RolledBack");
assert.match(preview.content[1].text, /PREVIEW ONLY/);
const noExplanation = await call("execute_revit_code", edit);
assert.match(noExplanation.content[0].text, /explanation/);
const changedInputs = await call("execute_revit_code", { ...edit, inputs: { a: 2 }, explanation: "test" });
assert.match(changedInputs.content[0].text, /not been previewed/);
const applied = json(await call("execute_revit_code", { ...edit, explanation: "Set wall comments" }));
assert.equal(applied.transaction, "Committed");
const again = await call("execute_revit_code", { ...edit, explanation: "again" });
assert.match(again.content[0].text, /not been previewed/, "a repeat needs a fresh preview");
// --- Companion panel decisions come back from the add-in ---
const panelApplied = { code: "/*PANEL_APPLIED*/ return 1;", inputs: {} };
await call("execute_revit_code", { ...panelApplied, dry_run: true, explanation: "preview for the test" });
const pa = json(await call("execute_revit_code", { ...panelApplied, explanation: "x" }));
assert.equal(pa.alreadyApplied, true);
assert.match(pa.instruction, /Do not re-apply/);
assert.match((await call("execute_revit_code", { ...panelApplied, explanation: "x" })).content[0].text, /not been previewed/, "after a panel apply a repeat needs a fresh preview");
const panelRejected = { code: "/*PANEL_REJECTED*/ return 1;", inputs: {} };
await call("execute_revit_code", { ...panelRejected, dry_run: true, explanation: "preview for the test" });
const pr2 = await call("execute_revit_code", { ...panelRejected, explanation: "x" });
assert.equal(pr2.isError, true);
assert.match(pr2.content[0].text, /cancelled/);
assert.match(client.getInstructions(), /ACE COMPANION PANEL/);

// --- preview pictures come back as image blocks, not base64 in the text ---
const pic = await call("execute_revit_code", { code: "/*PICTURE*/ return 1;", dry_run: true, explanation: "preview for the test", preview_image: true });
assert.equal(pic.content.filter((c) => c.type === "image").length, 2);
assert.doesNotMatch(pic.content[0].text, /iVBORw0KGgo/);
assert.match(pic.content[0].text, /"echoPreview":true/);

// read-only runs need no preview
assert.equal(json(await call("execute_revit_code", { code: "return 1;", mode: "readonly" })).echoMode, "readonly");

// --- Risky code is blocked unless explicitly allowed ---
const risky = await call("execute_revit_code", { code: "System.IO.File.Delete(\"c:/x\");", mode: "readonly" });
assert.match(risky.content[0].text, /files on disk/);
const saving = await call("execute_revit_code", { code: "doc.Save();", mode: "readonly" });
assert.match(saving.content[0].text, /saves or closes/);
assert.equal((await call("execute_revit_code", { code: "doc.Save();", mode: "readonly", allow_risky: true })).isError, undefined);

// --- merged tools route to the right bridge commands ---
assert.equal(json(await call("run_clash_test", { stored: true })).command, "clash_results", "stored results without a run");
assert.equal(json(await call("run_clash_test", { sources: true })).command, "coordination_sources", "the models that take part");
const imported = json(await call("run_clash_test", { import_report: "C:/reports/STR vs MEP.xml", test: "STR vs MEP" }));
assert.equal(imported.command, "import_clash_report", "a Navisworks report is imported, not a new run");
assert.equal(imported.args.path, "C:/reports/STR vs MEP.xml", "the report path reaches the add-in");
assert.equal(imported.args.test, "STR vs MEP", "one Navisworks test can be picked");
assert.equal(json(await call("snapshots", {})).command, "list_snapshots", "snapshots lists by default");
assert.equal(json(await call("list_warnings", { contains: "identical" })).args.contains, "identical");

// --- presentation standard: check is free, a change needs the identical preview ---
assert.equal(json(await call("presentation_standard", { check_only: true })).checkOnly, true);
assert.match((await call("presentation_standard", { explanation: "x" })).content[0].text, /not been previewed/);
assert.equal(json(await call("presentation_standard", { dry_run: true, explanation: "preview for the test", text_sizes: [{ upToScale: 50, textMm: 3 }, { textMm: 2.5 }] })).dryRun, true);
assert.match((await call("presentation_standard", { explanation: "Text sizes", text_sizes: [{ upToScale: 50, textMm: 2 }, { textMm: 2.5 }] })).content[0].text, /not been previewed/, "other sizes need their own preview");
const pres = json(await call("presentation_standard", { explanation: "Text sizes per scale", text_sizes: [{ upToScale: 50, textMm: 3 }, { textMm: 2.5 }] }));
assert.equal(pres.dryRun, false);
assert.equal(pres.echo.text_sizes[1].textMm, 2.5, "the sizes reach the add-in");

// --- the add-in's semantic screen: a refusal is a tool error; consent is passed through ---
const sem = await call("execute_revit_code", { code: "var x = 1; // SEMANTIC_RISK", mode: "readonly" });
assert.ok(sem.isError && /Blocked for safety/.test(sem.content[0].text), "a risk found by the compiler is a tool error");
const semOk = await call("execute_revit_code", { code: "var x = 1; // SEMANTIC_RISK", mode: "readonly", allow_risky: true });
assert.equal(semOk.isError, undefined, "with consent the run goes ahead");
assert.equal(seen.filter((s) => s.command === "execute_code").at(-1).args.allow_risky, true, "consent reaches the add-in");

// --- set_parameters has the same gate ---
const changes = [{ id: 1, parameter: "Comments", value: "x" }];
assert.match((await call("set_parameters", { changes, explanation: "t" })).content[0].text, /not been previewed/);
await call("set_parameters", { changes, dry_run: true, explanation: "preview for the test" });
assert.equal(json(await call("set_parameters", { changes, explanation: "Set comment" })).applied, 1);

assert.equal(json(await call("backup_model", {})).backup, "C:/backups/model.rvt");
assert.equal(json(await call("undo_last_claude_change", {})).undoRequested, "Claude: test");
const log = (await call("get_activity_log", {})).content[0].text;
assert.match(log, /PREVIEW only/);
assert.match(log, /APPLIED \(added 1, modified 2, deleted 0\)/);
assert.match(log, /Set wall comments/);
assert.match(log, /Undid/);
const bad = await call("execute_revit_code", { code: "boom" });
assert.equal(bad.isError, true);

await call("run_saved_script", { name: "test_fit_out", inputs: { room_number: "301", m2_per_person: 8 }, dry_run: true, explanation: "preview for the test" });
assert.equal(seen.at(-1).args.transaction_name, "Claude: test_fit_out (room number 301, m2 per person 8)");
await call("run_saved_script", { name: "test_fit_out", inputs: { level: "L3", m2_per_person: 10, min_aisle_mm: 1500, desk_type: "60x30" }, dry_run: true, explanation: "preview for the test", option_label: "Option B" });
assert.match(seen.at(-1).args.transaction_name, /^Claude: Option B - test_fit_out \(.*desk type 60x30\)$/, "the option label leads the card title and every input is named");

const lib = json(await call("saved_scripts"));
assert.ok(lib.find((s) => s.name === "renumber_rooms" && s.mode === "auto"));
assert.ok(lib.find((s) => s.name === "parameter_completeness" && s.mode === "readonly"));
assert.ok(!lib.find((s) => s.name === "audit_model"), "audit_model was merged into the native model check");

const run = json(await call("run_saved_script", { name: "parameter_completeness" })); // readonly script: no preview needed
assert.equal(run.echoMode, "readonly");

await call("save_script", { name: "my_task", description: "Test", code: "return ctx.Num(\"n\");", mode: "readonly", inputs_example: { n: 1 } });
await call("save_script", { name: "team_task", description: "Shared", code: "return 1;", mode: "readonly", scope: "team" });
assert.equal(json(await call("saved_scripts")).find((s) => s.name === "team_task").source, "team");
assert.ok(json(await call("saved_scripts")).find((s) => s.name === "door_width_check"));
for (const s of json(await call("saved_scripts")).filter((s) => s.source === "built-in")) {
  assert.ok(s.description && ["auto", "manual", "readonly"].includes(s.mode), `built-in script ${s.name} needs @description and a valid @mode`);
}
assert.match((await call("saved_scripts", { name: "door_width_check" })).content[0].text, /"Width"/, "door width falls back to the family Width parameter");
const mine = json(await call("saved_scripts")).find((s) => s.name === "my_task");
assert.equal(mine.source, "user");
const mineRun = json(await call("run_saved_script", { name: "my_task", inputs: { n: 7 } }));
assert.equal(mineRun.echoMode, "readonly");
assert.equal(mineRun.inputs.n, 7);
// modifying saved scripts are gated too
assert.match((await call("run_saved_script", { name: "renumber_rooms", explanation: "t" })).content[0].text, /not been previewed/);
assert.equal((await call("save_script", { name: "my_task", description: "x", code: "return 1;" })).isError, true);

// --- support tools ---
const setup = (await call("check_setup")).content[0].text;
assert.match(setup, /OK   Revit connection/);
assert.match(setup, /OK   C# compiler/);
const rep = (await call("report_issue", { what_happened: "Door tagging failed on Level 3", request: "tag all doors" })).content[0].text;
const repFile = rep.match(/Report saved to:\n(.+)/)[1].trim();
const md = fs.readFileSync(repFile, "utf8");
assert.match(md, /# ACE Revit MCP - Issue report/);
assert.match(md, /Door tagging failed on Level 3/);
assert.match(md, /## Health checks/);
assert.match(md, /## Recommendations/);
assert.match(md, /Appendix for maintainers/);
assert.match(md, /boom/, "failing code is included for maintainers");
assert.match(rep, /Copied to the team folder/);
assert.ok(fs.readFileSync(path.join(tmp, "logs", "mcp-calls.jsonl"), "utf8").includes('"tool":"execute_revit_code"'));

// Revit down -> friendly error, not a crash
await call("execute_revit_code", { code: "boom", dry_run: true, explanation: "preview for the test" });   // a compile failure for the report
const lr = (await call("report_issue", { kind: "learning", days: 7 })).content[0].text;
assert.match(lr, /First-time right/);
assert.match(lr, /Repeated API mistakes/, "the failed compile in this test run shows up");
assert.match(lr, /Saved: /);
assert.match(lr, /Development radar/, "the learning report includes the development radar");
const { tasks: radarTasks } = await import("../lib/radar.js");
const tk = radarTasks([
  { t: "2026-10-06T08:00:00Z", tool: "get_model_brief", ok: true, chars: 4000 },
  { t: "2026-10-06T08:01:00Z", tool: "execute_revit_code", ok: true, dryRun: true, chars: 800 },
  { t: "2026-10-06T08:02:00Z", tool: "execute_revit_code", ok: true, dryRun: false, chars: 400 },
  { t: "2026-10-06T09:30:00Z", tool: "model_dashboard", ok: false, chars: 100 },
]);
assert.equal(tk.length, 2, "a long pause starts a new task");
assert.ok(tk[0].calls === 3 && tk[0].previews === 1 && tk[0].applies === 1 && tk[1].failed === 1, "tasks count calls, previews, applies and failures");
const cr = (await call("coordination_report", { max_issues: 5 })).content[0].text;
assert.match(cr, /Coordination\.csv/, "coordination report returns the CSV issue list");
const fc = (await call("clash_view", { key: "STR vs MEP|u1|u2" })).content[0].text;
assert.match(fc, /200 x 200 x 250/, "focus_clash passes the key and returns the intersection");
const wsCheck = await call("assign_worksets", { check_only: true });
const wsText = wsCheck.content.map((c) => c.text).join("\n");
assert.match(wsText, /"checkOnly":true/, "workset check is read-only");
assert.match(wsText, /Shared Levels and Grids/, "BEP rules are passed to the add-in");
assert.match(wsText, /ACE BEP worksets \(example\) \(project: /, "the rules in force are named with their source (the add-in resolves them)");
const wsBlocked = await call("assign_worksets", { explanation: "move to BEP worksets" });
assert.ok(wsBlocked.isError || /not been previewed/.test(wsBlocked.content[0].text), "applying needs a preview first");
await call("assign_worksets", { dry_run: true, explanation: "preview for the test" });
const wsApplied = (await call("assign_worksets", { explanation: "move to BEP worksets" })).content[0].text;
assert.match(wsApplied, /Committed/, "after the identical preview, applying works");
// --- parameter_fill: a check is free, a fill needs its own identical dry run in the same model ---
const plist = path.join(tmp, "params.csv");
fs.writeFileSync(plist, "﻿parameter_name,categories,type_instance\r\nTST_Site,Project Information,Instance\r\nTST_Area,\"Rooms, Areas, Spaces\",Type\r\nTST_Area,\"Rooms, Areas, Spaces\",Type\r\nTST_X,Furniture,Instance\r\n");
const pfCheck = await call("parameter_fill", { list: plist, all_documents: true });
const pfSent = seen.filter((x) => x.command === "parameter_fill").at(-1).args;
assert.deepEqual(pfSent.rows, [{ name: "TST_Site", group: "project", level: "instance", key: "r1" }, { name: "TST_Area", group: "rooms", level: "type", key: "r2" }], "CSV rows mapped, duplicate dropped, unknown category skipped");
assert.deepEqual(pfSent.scope, { codes: [], models: {}, ownership: {} }, "the scope travels with the rows");
assert.ok(pfSent.check_only === true && pfSent.fill === false && pfSent.all_documents === true && pfSent.dry_run === false && pfSent.value === undefined && pfSent.max_ids === 20, "a check is read-only across the open models and needs no value");
assert.match(pfCheck.content[1].text, /Test: 2 rows - 0 complete, 2 with blanks.*; 0 with NA, 0 out of scope/, "per-model summary text with the 1.10 counts");
assert.match(pfCheck.content[1].text, /1 row\(s\) skipped .*1 duplicate/, "skipped and duplicate rows are reported");
const pfJson = JSON.parse(pfCheck.content[0].text);
assert.ok(pfJson.documents[0].rows[0].key === "r1" && pfJson.documents[0].rows[0].status === "blanks" && pfJson.documents[0].rows[0].storageType === undefined, "compact JSON: key, name, status and counts per row");
assert.equal((await call("parameter_fill", { list: path.join(tmp, "nope.csv") })).isError, true);
const allSkipped = await call("parameter_check", { rows: [{ name: "A", categories: "Furniture" }, { name: "", categories: "Levels" }] });
assert.ok(allSkipped.isError && /No usable rows in the list \(2 skipped: 1 unknown category, 1 no parameter name\)/.test(allSkipped.content[0].text), "all rows skipped: the reasons are given");
assert.equal((await call("parameter_fill", { action: "fill", rows: [{ name: "A", categories: "Levels" }], all_documents: true, dry_run: true, value: "NA", explanation: "x" })).isError, true, "fill is the active model only");
const pfRows = [{ name: "TST_Site", categories: "Project Information" }];
assert.match((await call("parameter_fill", { action: "fill", rows: pfRows, dry_run: true, explanation: "Fill blanks" })).content[0].text, /No fill value: set the NA policy/, "no built-in fill value");
const pfBlocked = await call("parameter_fill", { action: "fill", rows: pfRows, value: "NA", explanation: "Fill blanks with NA" });
assert.ok(pfBlocked.isError && /not been previewed/.test(pfBlocked.content[0].text), "a fill needs a preview first");
const pfPrev = await call("parameter_fill", { action: "fill", rows: pfRows, value: "NA", dry_run: true, explanation: "Fill blanks with NA" });
assert.deepEqual(seen.at(-1).args.na_values, ["NA"], "the declared-missing texts travel with the fill");
assert.match(pfPrev.content[1].text, /Fill: 2 value\(s\) would be set, 1 skipped \(1 read-only\)/);
assert.match(pfPrev.content[2].text, /PREVIEW ONLY/);
assert.match((await call("parameter_fill", { action: "fill", rows: pfRows, value: "N/A", explanation: "Fill blanks with N/A" })).content[0].text, /not been previewed/, "another value needs its own preview");
const pfApplied = await call("parameter_fill", { action: "fill", rows: pfRows, value: "NA", explanation: "Fill blanks with NA" });
const pfApplySent = seen.filter((x) => x.command === "parameter_fill").at(-1).args;
assert.ok(!pfApplied.isError && pfApplySent.fill === true && pfApplySent.dry_run === false && pfApplySent.check_only === false && pfApplySent.all_documents === false, "after the identical preview the fill applies");
assert.equal(pfApplySent.expect_model, "Test", "the apply names the model the dry run ran in");
assert.match(pfApplied.content[1].text, /Before the fill:\nTest: 1 rows - 0 complete, 1 with blanks[\s\S]*After the fill:\nTest: 1 rows - 0 complete, 0 with blanks.*1 with NA/, "after an apply the state after the fill is reported, the old one labelled before");
assert.match((await call("parameter_fill", { action: "fill", rows: pfRows, value: "NA", explanation: "Fill blanks with NA" })).content[0].text, /not been previewed/, "a preview is used once");
// --- parameter_check: requirement sources, read-only actions without Revit, the fill plan, the project mapping in the vault ---
const pdir = path.join(tmp, "reqs");
fs.mkdirSync(pdir);
const pAis = path.join(pdir, "ais.csv"), pChecker = path.join(pdir, "checker.xml"), pSp = path.join(pdir, "client.txt"), pProj = path.join(pdir, "project.txt");
fs.writeFileSync(pAis, "parameter_name,categories,type_instance\r\nTST_PI_Site Name,Project Information,Instance\r\nTST_EL_Asset,\"Systems, Components, Elements\",Type\r\n");
const pcF = (cat, prop, cond, val, op = "And") => `<Filter Operator="${op}" Category="${cat}" Property="${prop}" Condition="${cond}" Value="${val}" />`;
fs.writeFileSync(pChecker, `<MCSettings Name="TST"><Heading HeadingText="Params" IsChecked="True"><Section SectionName="Have value" IsChecked="True">` +
  `<Check ID="c1" CheckName="TST_PI_Site Name" IsChecked="True">${pcF("Category", "OST_ProjectInformation", "Included", "True")}${pcF("Parameter", "TST_PI_Site Name", "HasValue", "True")}</Check>` +
  `<Check ID="c2" CheckName="TST_ST_Grade" IsChecked="True">${pcF("Category", "OST_ProjectInformation", "Included", "True")}${pcF("Parameter", "TST_ST_Grade", "HasValue", "True")}</Check>` +
  `</Section></Heading><Heading HeadingText="&lt;--Unused--&gt;" IsChecked="True"><Section SectionName="x" IsChecked="True"><Check ID="c3" CheckName="p" IsChecked="True">${pcF("Parameter", "TST_PI_Parked", "HasValue", "True")}</Check></Section></Heading></MCSettings>`);
fs.writeFileSync(pSp, "*GROUP\tID\tNAME\nGROUP\t1\tG\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\nPARAM\tc-1\tTST_PI_Site name\tTEXT\t\t1\n");
fs.writeFileSync(pProj, Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from("*GROUP\tID\tNAME\r\nGROUP\t1\tG\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\r\nPARAM\tp-1\tTST_EL_Asset\tTEXT\t\t1\r\n", "utf16le")]));
const pSources = { list: pAis, checker: pChecker, shared_parameters: [pSp], project_parameters: pProj };
const before = seen.length;
const req = await call("parameter_check", { action: "requirements", ...pSources, report_dir: path.join(tmp, "preq") });
assert.ok(!req.isError, req.content[0].text);
assert.match(req.content[1].text, /Requirements: 3 merged rows/, "AIS + checker rows merged on name + group");
assert.match(req.content[1].text, /TST_PI_Site Name -> TST_PI_Site name \(spelling\)/, "spelling candidates are reported");
assert.match(req.content[1].text, /No project mapping found .*generic checks only/, "no mapping: said plainly");
assert.ok(JSON.parse(req.content[0].text).rows.find((r) => r.name === "TST_EL_Asset").guid === "p-1", "GUID from the project file");
assert.ok(fs.readdirSync(path.join(tmp, "preq")).some((f) => /^parameter-requirements_.*\.csv$/.test(f)), "report_dir gets the CSV");
const rvw = await call("parameter_check", { action: "review_checker", ...pSources, report_dir: path.join(tmp, "preq") });
assert.match(rvw.content[1].text, /2 active, 1 parked/, "checker review");
assert.match(rvw.content[1].text, /Scope defects \(1\): TST_ST_Grade/);
assert.ok(fs.readdirSync(path.join(tmp, "preq")).some((f) => /^checker-review_.*\.md$/.test(f)));
assert.ok(seen.slice(before).every((x) => x.command === "project_rules"), "requirements and review_checker only ask where the vault is; they never check the model");
assert.equal((await call("parameter_check", { action: "review_checker", list: pAis })).isError, true, "review_checker needs the checker");
const pcCheck = await call("parameter_check", { ...pSources, all_documents: true });
const pcSent = seen.at(-1);
assert.ok(pcSent.command === "parameter_fill" && pcSent.args.check_only === true && pcSent.args.fill === false && pcSent.args.rows.length === 3, "the check sends the merged rows");
assert.ok(pcSent.args.rows.every((r) => r.key && !r.sources && !r.match), "server-only fields stay on the server");
assert.match(pcCheck.content[1].text, /Test: 3 rows/);
assert.match(pcCheck.content[1].text, /Actions \(top \d+ of \d+, read-only\):\n1\. \[/, "the reply carries the top actions");
const pcRep = await call("parameter_check", { ...pSources, report_dir: path.join(tmp, "prep") });
const repFiles = fs.readdirSync(path.join(tmp, "prep"));
assert.ok(["parameter-summary_", "parameter-check_", "actions_"].every((p) => repFiles.some((f) => f.startsWith(p))) && repFiles.some((f) => f.endsWith(".xlsx")), "a check with report_dir writes the summary workbook, CSV, summary and actions");
assert.match(pcRep.content[1].text, /Report: .*parameter-summary_.*\.xlsx/);
// The fill plan is read-only: the add-in counts without a transaction, and it is not a preview.
const pcPlan = await call("parameter_check", { ...pSources, fill_plan: true, value: "NA" });
assert.ok(seen.at(-1).args.fill === true && seen.at(-1).args.check_only === true && seen.at(-1).args.dry_run === false, "fill_plan: fill counted with check_only (no transaction)");
assert.match(pcPlan.content.at(-1).text, /FILL PLAN \(read-only/);
assert.equal((await call("parameter_check", { ...pSources, all_documents: true, fill_plan: true, value: "NA" })).isError, true, "the fill plan is the active model only");
assert.match((await call("parameter_fill", { action: "fill", ...pSources, value: "NA", explanation: "Fill blanks with NA" })).content[0].text, /not been previewed/, "a fill plan is not a preview");
await call("parameter_fill", { action: "fill", ...pSources, value: "NA", dry_run: true, explanation: "Fill blanks with NA" });
fs.appendFileSync(pSp, "# a comment: same rows, different bytes\n");
assert.match((await call("parameter_fill", { action: "fill", ...pSources, value: "NA", explanation: "Fill blanks with NA" })).content[0].text, /not been previewed/, "a changed source file blocks the apply, even when the rows are the same");
await call("parameter_fill", { action: "fill", ...pSources, value: "NA", dry_run: true, explanation: "Fill blanks with NA" });
const pcApplied = await call("parameter_fill", { action: "fill", ...pSources, value: "NA", max_ids: 3, explanation: "Fill blanks with NA" });
assert.ok(!pcApplied.isError && seen.at(-1).args.fill === true && seen.at(-1).args.dry_run === false && seen.at(-1).args.expect_model === "Test", "the apply after its dry run, in the same model");
// The project mapping lives in the vault: its NA policy is the fill value; create_mapping writes a draft there.
const pvault = path.join(tmp, "pv", "ACE Vault");
fs.mkdirSync(path.join(pvault, "rules"), { recursive: true });
fs.writeFileSync(path.join(pvault, "rules", "parameter-mapping.json"), JSON.stringify({ about: "ACE parameter requirement mapping. Edit freely.", categories: {}, disciplines: {}, aliases: {}, models: {}, fill: { value: "N/A", values: ["NA"] } }));
await call("parameter_check", { list: pAis, vault: pvault, fill_plan: true });
assert.ok(seen.at(-1).args.value === "N/A" && seen.at(-1).args.na_values.join() === "NA,N/A", "the vault's mapping gives the fill value and the NA texts");
assert.match((await call("parameter_check", { list: pAis, vault: pvault, fill_plan: true, value: "XX" })).content[0].text, /NA policy says "N\/A"/, "a value against the policy is refused");
const pvault2 = path.join(tmp, "pv2", "ACE Vault");
fs.mkdirSync(pvault2, { recursive: true });
assert.match((await call("parameter_check", { action: "requirements", list: pAis, vault: pvault2, create_mapping: true })).content[1].text, /Draft mapping written/);
assert.equal(JSON.parse(fs.readFileSync(path.join(pvault2, "rules", "parameter-mapping.json"), "utf8")).draft, true, "create_mapping writes a draft into the vault's rules folder");
// A large check goes to report files; the reply stays small.
const bigRows = Array.from({ length: 210 }, (_, i) => ({ name: `TST_P${i}`, categories: "Levels" }));
const pBig = await call("parameter_check", { rows: bigRows, report_dir: path.join(tmp, "pbig") });
assert.match(pBig.content[1].text, /full table \(210 model x row cells\) is in the report files/);
assert.ok(!JSON.parse(pBig.content[0].text).documents[0].rows && JSON.parse(pBig.content[0].text).documents[0].rowsInReport === 210, "rows left out of the reply");
// The requirements vault: writing needs consent once per vault, then the folder is trusted.
const vtmp = fs.mkdtempSync(path.join(os.tmpdir(), "ace-smoke-vault-"));
const vNo = await call("vault", { action: "init", folder: vtmp, project: "Smoke" });
assert.ok(vNo.isError && /permission/.test(vNo.content[0].text), "the first write to a vault asks for consent");
assert.match((await call("vault", { action: "init", folder: vtmp, project: "Smoke", consent: true })).content[0].text, /"created": ?true/, "init with consent");
fs.writeFileSync(path.join(vtmp, "ACE Vault", "01 Requirements", "eir.txt"), "Doors shall carry a fire rating.\n");
assert.match((await call("vault", { action: "register", folder: vtmp, key: "EIR", path: path.join(vtmp, "ACE Vault", "01 Requirements", "eir.txt"), revision: "A" })).content[0].text, /registered/, "a trusted vault needs no consent again");
assert.match((await call("vault", { action: "import", folder: vtmp, records: [{ id: "REQ-EIR-0001", doc: { key: "EIR" }, source: { quote: "Doors shall carry a fire rating.", section_path: ["1"] }, kind: "loin", severity: "minor" }] })).content[0].text, /"added": ?1/, "records import");
assert.match((await call("vault", { action: "revalidate", folder: vtmp, key: "EIR" })).content[0].text, /"atCitedPlace": ?1/, "revalidate finds the quote");
assert.match((await call("vault", { action: "status", folder: vtmp })).content[0].text, /"warning": ?1/, "status summarises the records (minor -> warning)");
const comp = (await call("vault", { action: "compliance", status: "not-met", limit: 5 })).content[0].text;
assert.match(comp, /"status": ?"not-met"/, "compliance passes the status filter to Revit");
assert.match(comp, /"max_results": ?5/, "compliance passes the result limit");
assert.match(comp, /"ranNow": ?false/, "compliance reads the last result unless run: true");
const vrep = (await call("vault", { action: "report", folder: vtmp, by: "smoke" })).content[0].text;
assert.match(vrep, /"from": ?"Revit \(last model check\)"/, "with no run in the vault, the report uses Revit's last result");
assert.match(vrep, /"notMet": ?1/, "report summary");
assert.ok(fs.existsSync(path.join(vtmp, "ACE Vault", "04 Audits", "Test_audit_" + new Date().toISOString().slice(0, 10) + "_R01.docx")), "report saved in 04 Audits");
assert.match((await call("vault", { action: "model_summary", folder: vtmp })).content[0].text, /"summary": ?null/, "model_summary without dry_run only reads");
const msNo = await call("vault", { action: "model_summary", folder: vtmp, dry_run: false, explanation: "Keep the audit result in the model" });
assert.ok(msNo.isError && /previewed/.test(msNo.content[0].text), "writing the summary into the model needs a preview first");
const msPrev = (await call("vault", { action: "model_summary", folder: vtmp, dry_run: true, explanation: "Keep the audit result in the model" })).content[0].text;
assert.match(msPrev, /"rev": ?"R01"/, "the preview carries the latest report's summary");
assert.match((await call("vault", { action: "model_summary", folder: vtmp, dry_run: false, explanation: "Keep the audit result in the model" })).content[0].text, /"dryRun": ?false/, "apply after the identical preview");
fs.rmSync(vtmp, { recursive: true, force: true });
const wm = (await call("working_mode", { mode: "coordination" })).content[0].text;
assert.match(wm, /Coordination/, "working mode can be set");
for (const harmless of ["ctx.Log(\"area m\\u00b2\");", "ctx.Log(\"dynamic\");"])
  assert.ok(!/Blocked for safety/.test((await call("execute_revit_code", { code: harmless, mode: "readonly" })).content[0].text), `harmless code is not flagged: ${harmless}`);
for (const risky of ["var u = \"http://x\"; File.Delete(p);", "var a = \"/*\"; File.Delete(p); var b = \"*/\";", "using F = System.IO.File; F.Delete(p);", "File /**/ . Delete(p);", "uidoc.SaveAndClose();",
  "Type.GetType(\"System.IO.\" + \"File\").GetMethod(\"Delete\");", "var s = File.ReadAllText(p);", "link.Unload(null);",
  "new System.IO.FileInfo(p).Delete();", "using var w = new System.IO.BinaryWriter(System.IO.File.OpenWrite(p));", "doc.Export(\"C:/x\", \"a\", new DWGExportOptions(), ids);"]) {
  const r = await call("execute_revit_code", { code: risky, dry_run: true, explanation: "preview for the test" });
  assert.ok(r.isError && /Blocked for safety/.test(r.content[0].text), `risky code is screened: ${risky}`);
}
const quick = await call("get_model_brief", { quick: true });
assert.ok(!quick.isError, "the brief's quick mode (formerly get_model_overview) works");
const pc = (await call("pending_changes")).content[0].text;
assert.match(pc, /8 m2/, "the panel's waiting cards reach Claude");
bridge.close();
const down = await call("revit_status");
assert.equal(down.isError, true);
assert.match(down.content[0].text, /not reachable/);

// --- a reply that is too long stays valid JSON: the longest lists are shortened, every field is kept ---
const { fit } = await import("../lib/core.js");
const big = { success: true, result: { rows: Array.from({ length: 5000 }, (_, i) => ({ id: i, name: `Door ${i}` })) }, revitWarnings: ["w"] };
const fitted = JSON.parse(fit(big, 20_000));
assert.ok(fitted.result.rows.length < 5000 && fitted.result.rowsTruncated, "long lists are shortened with a note");
assert.deepEqual(fitted.revitWarnings, ["w"], "short fields are kept");
assert.equal(fit({ a: 1 }), '{"a":1}', "replies are compact JSON");

await client.close();
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`smoke test passed (${seen.length} bridge calls)`);
