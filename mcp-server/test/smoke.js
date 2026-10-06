// Smoke test: runs the MCP server against a fake Revit bridge and exercises every tool path.
// Usage: node test/smoke.js   (no Revit needed)
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
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
      case "assign_worksets":
        return reply({ ok: true, result: { success: true, checkOnly: !!args.check_only, transaction: args.check_only ? undefined : args.dry_run ? "RolledBack" : "Committed", rules: `${args.rules_name} (${args.rules_source}), ${args.rules?.length} rules`, firstRule: args.rules?.[0]?.workset } });
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
  args: [path.join(path.dirname(new URL(import.meta.url).pathname), "..", "index.js")],
  env: { ...process.env, ACE_REVIT_CONFIG: path.join(tmp, "config.json"), ACE_REVIT_URL: `http://127.0.0.1:${port}`, ACE_REVIT_SCRIPTS: path.join(tmp, "scripts"), ACE_REVIT_JOURNAL: path.join(tmp, "journal"), ACE_REVIT_LOGS: path.join(tmp, "logs"), ACE_REVIT_REPORTS: path.join(tmp, "reports"), ACE_TEAM_SCRIPTS: path.join(tmp, "team"), ACE_TEAM_REPORTS: path.join(tmp, "team-reports"), ACE_REVIT_LESSONS: path.join(tmp, "lessons"), ACE_TEAM_LESSONS: path.join(tmp, "team-lessons") },
});
const client = new Client({ name: "smoke", version: "1.0.0" });
await client.connect(transport);

const call = async (name, args = {}) => client.callTool({ name, arguments: args });
const json = (r) => JSON.parse(r.content[0].text);

const tools = (await client.listTools()).tools.map((t) => t.name).sort();
assert.deepEqual(tools, ["assign_worksets", "backup_model", "check_setup", "clash_view", "coordination_report", "describe_category", "describe_family", "execute_revit_code", "find_elements", "get_activity_log", "get_element_details", "get_model_brief", "get_selection", "lessons", "list_types", "list_views", "list_warnings", "model_changes", "model_dashboard", "open_view", "pending_changes", "presentation_standard", "report_issue", "revit_api_lookup", "revit_guide", "revit_status", "run_clash_test", "run_saved_script", "save_script", "saved_scripts", "select_elements", "set_clash_status", "set_parameters", "snapshots", "undo_last_claude_change", "view_image", "working_mode"]);
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
const cr = (await call("coordination_report", { max_issues: 5 })).content[0].text;
assert.match(cr, /Coordination\.csv/, "coordination report returns the CSV issue list");
const fc = (await call("clash_view", { key: "STR vs MEP|u1|u2" })).content[0].text;
assert.match(fc, /200 x 200 x 250/, "focus_clash passes the key and returns the intersection");
const wsCheck = await call("assign_worksets", { check_only: true });
const wsText = wsCheck.content.map((c) => c.text).join("\n");
assert.match(wsText, /"checkOnly":true/, "workset check is read-only");
assert.match(wsText, /Shared Levels and Grids/, "BEP rules are passed to the add-in");
assert.match(wsText, /ACE BEP worksets/, "the rules file is named");
const wsBlocked = await call("assign_worksets", { explanation: "move to BEP worksets" });
assert.ok(wsBlocked.isError || /not been previewed/.test(wsBlocked.content[0].text), "applying needs a preview first");
await call("assign_worksets", { dry_run: true, explanation: "preview for the test" });
const wsApplied = (await call("assign_worksets", { explanation: "move to BEP worksets" })).content[0].text;
assert.match(wsApplied, /Committed/, "after the identical preview, applying works");
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
