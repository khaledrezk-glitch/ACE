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
const SERVER_VERSION = JSON.parse(fs.readFileSync(new URL("../package.json", import.meta.url), "utf8")).version;
const writesSeen = [];   // write plans the fake add-in received, with the missing registers already on disk at that moment
const regDir = path.join(tmp, "exec");
const regFiles = () => { try { return fs.readdirSync(regDir).filter((f) => f.startsWith("missing-register-")); } catch { return []; } };
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
      case "parameter_fill": {
        if (args.expect_model && args.expect_model !== "Test") return reply({ ok: true, result: { success: false, stage: "wrong_model", error: `The preview ran in ${args.expect_model}; the active model is Test.` } });
        const names = (args.rows ?? []).map((r) => r.name);
        const old = names.some((n) => n.startsWith("TST_OLDADDIN"));
        const writePlan = !args.check_only && (args.fill || args.derive?.length || args.bind?.length || args.rebind?.length);
        // The 1.11 contract: a write plan refuses a version mismatch and borrowed targets before any write.
        if (writePlan && args.expect_version && args.expect_version !== (old ? "1.9.0" : SERVER_VERSION))
          return reply({ ok: true, result: { success: false, stage: "blocked", error: "Nothing was changed: version", blocked: [{ what: "the whole plan", why: "version", elements: 0, how: "update" }], model: "Test" } });
        if (writePlan && names.some((n) => n.startsWith("TST_BORROW")))
          return reply({ ok: true, result: { success: false, stage: "blocked", error: "Nothing was changed: 3 elements are borrowed", model: "Test",
            blocked: [{ what: "3 element(s) of TST_BORROW_X", why: "borrowed by another user", elements: 3, sampleIds: [11, 12, 13], holder: "jane", how: "ask jane to relinquish, then re-run", code: "borrowed" }] } });
        if (writePlan) writesSeen.push({ args, registers: regFiles() });
        const amb = names.some((n) => n.startsWith("TST_AMB_"));
        const doc = (model) => ({ model, discipline: amb ? null : args.scope?.models?.[model] ?? null, disciplineSource: amb ? "ambiguous" : args.scope?.models?.[model] ? "mapping" : null, ...(amb ? { disciplineCandidates: ["PL", "AR"] } : {}),
          preflight: { workshared: false, foreign: {}, aceVersion: old ? "1.9.0" : SERVER_VERSION, links: { count: 2, notLoaded: 0, names: ["TST link A", "TST link B"] } },
          rows: args.rows.map((r) => r.name.startsWith("TST_GD_") ? { ...r, checked: 2, missing: 0, blank: 0, filled: 2, na: 0, status: "guid-differs", modelGuid: "dddddddd-0000-0000-0000-00000000000d", isText: true }
            : r.name.startsWith("TST_NB_") ? { ...r, checked: 3, missing: 3, blank: 0, filled: 0, na: 0, status: "not-bound", isText: true }
            : r.name.startsWith("TST_BI_") ? { ...r, checked: 6, missing: 4, blank: 0, filled: 2, na: 0, status: "not-available", origin: "built-in", notAvailableCategories: ["Furniture"], isText: true }
            : { ...r, checked: 2, missing: 0, blank: 1, filled: 1, na: 0, onType: 0, storageType: "String", isText: true, status: "blanks", modelGuid: r.guid ?? null, value: r.group === "project" ? "x" : undefined }),
          ...(args.register ? { register: args.rows.filter((r) => !r.name.startsWith("TST_NB_")).map((r) => ({ key: r.key, name: r.name, category: "OST_Walls", categoryName: "Walls", level: "instance", count: 1, ids: [101] })) } : {}),
          summary: { rows: args.rows.length, complete: 0, withBlanks: args.rows.length, missingEverywhere: 0, notText: 0, notInGeneral: 0, withNA: 0, outOfScope: 0, wrongGuid: 0, notBound: 0, wrongLevel: 0, foreign: 0 } });
        const docs = [doc("Test"), ...(args.all_documents && names.some((n) => n.startsWith("TST_M_")) ? [doc("Test-STR")] : [])];
        const mismatch = writePlan && !args.dry_run && (names.some((n) => n.startsWith("TST_VERIFY")) || args.test_force_verify_mismatch);   // assumed: the flag fails the apply's self-check, not the dry run's
        const res = { success: !mismatch, model: "Test", dryRun: !!args.dry_run, checkOnly: !!args.check_only || undefined, echo: args, rolledBack: !!args.dry_run || mismatch,
          ...(args.fill && !args.dry_run && !args.check_only && !mismatch ? { after: { rows: args.rows.map((r) => ({ key: r.key, name: r.name, blank: 0, filled: 1, na: 1, status: "complete-with-na" })), summary: { rows: args.rows.length, complete: 0, withBlanks: 0, missingEverywhere: 0, notText: 0, notInGeneral: 0, withNA: args.rows.length } } } : {}),
          documents: docs,
          ...(writePlan ? {
            transaction: mismatch ? "VerifyFailed" : args.dry_run ? "RolledBack" : "Committed",
            bindings: { planned: args.bind?.length ?? 0, done: args.bind?.length ?? 0, skipped: [], items: (args.bind ?? []).map((b) => ({ name: b.name, guid: b.guid, action: "bind", level: b.level, categories: b.categories, file: b.file })), note: null },
            ...(args.rebind?.length ? { rebound: { planned: args.rebind.length, done: args.rebind.length, restored: 2, items: args.rebind.map((e) => ({ name: e.name, oldGuid: e.oldGuid, guid: e.guid })),
              dropped: [{ what: "schedule field TST_GD_Code in TST schedule", why: "it belongs to the old definition", how: "add the field again after the rebind" }],
              impact: { [args.rebind[0].name]: { schedules: [{ name: "TST schedule", filters: [] }], viewFilters: [], labels: [] } } } } : {}),
            derived: (args.derive ?? []).map((r) => ({ rule: r, text: `${r.target} = ${r.value != null ? `"${r.value}"` : r.copyFrom ?? r.join.join(" + ")}`, target: r.target, set: 1, skipped: [], types: [] })),
            verify: mismatch ? { ok: false, checked: 2, mismatchCount: 1, mismatches: [{ kind: "value", key: "r1", name: names[0], id: 101, expected: "NA", actual: "", why: "not written" }] } : { ok: true, checked: 2, mismatchCount: 0, mismatches: [] },
            ...(args.impact ? { impact: { [names[0]]: { guid: null, schedules: [{ name: "TST schedule", id: 5, filters: [{ type: "HasValue", value: null }] }], viewFilters: ["TST filter"], labels: [{ family: "TST title block", kind: "titleblock", sheets: 41, instances: 41, proxy: "guid" }, { family: "TST tag", kind: "tag", sheets: 3, instances: 9, proxy: "guid" }], titleBlocks: { families: 1, sheets: 41, instances: 41 }, inferred: true, notes: [] } } } : {}),
            aceVersion: SERVER_VERSION,
          } : {}),
          ...(args.fill ? { fill: { set: 2, skipped: [{ id: 9, name: "x", reason: "read-only" }], types: writePlan ? [{ id: 7, name: "TST type", parameter: names[0], instancesAffected: 12 }] : undefined }, [args.dry_run ? "wouldChange" : mismatch ? "wouldHaveChanged" : "changed"]: { added: 0, modified: 2, deleted: 0 } } : {}) };
        if (mismatch) {
          // The add-in's rolled-back answer: nothing counted as done, the counts under wouldSet / wouldDo, no after block.
          const move = (o, from, to) => { if (o && typeof o[from] === "number") { o[to] = o[from]; o[from] = 0; } };
          move(res.fill, "set", "wouldSet"); (res.derived ?? []).forEach((d) => move(d, "set", "wouldSet")); move(res.bindings, "done", "wouldDo"); move(res.rebound, "done", "wouldDo");
          res.after = null; res.afterIfKept = { rows: args.rows.map((r) => ({ key: r.key, name: r.name, blank: 0, status: "complete-with-na" })) };
        }
        return reply({ ok: true, result: res });
      }
      case "clash_resolve": {
        // The clash_resolve contract: check_only plans (no transaction); a dry run and an apply name their model; keys trigger cases.
        if (args.expect_model && args.expect_model !== "Test") return reply({ ok: true, result: { success: false, stage: "wrong_model", error: `The preview ran in ${args.expect_model}; the active model is Test.` } });
        const keys = args.keys ?? [];
        const before = { clashes: 5, issues: 3, byPair: { "Structural Framing x Structural Columns": 3, "Walls x Walls": 1, "Floors x Floors": 1 } };
        const plan = keys.includes("TST_NOTHING") ? [] : [
          { id: "join:201>301", kind: "join", a: 201, b: 301, aCategory: "Structural Columns", bCategory: "Structural Framing", material: "Concrete", level: "L1", why: "column before beam" },
          { id: "switch-join:205>206", kind: "switch-join", a: 205, b: 206, aCategory: "Walls", bCategory: "Floors", material: "Concrete", level: "L1", why: "the slab won; the wall goes first" },
          { id: "cutback:302>202:end", kind: "cutback", a: 202, b: 302, aCategory: "Structural Framing", bCategory: "Structural Columns", material: "Steel", level: "Roof", why: "beam end runs into the column" },
          { id: "cope:204>203", kind: "cope", a: 203, b: 204, aCategory: "Structural Framing", bCategory: "Structural Framing", material: "Steel", level: "Roof", why: "secondary into primary" }];
        const reportOnly = [{ a: 401, b: 402, aCategory: "Walls", bCategory: "Walls", why: "duplicate or overlapping walls", recommendation: "decide which wall stays" }];
        const blocked = keys.includes("TST_BORROW") ? [{ what: "2 element(s) to join", why: "borrowed by another user", elements: 2, sampleIds: [201, 301], holder: "jane", how: "ask jane to synchronise and relinquish, then run again", code: "borrowed" }] : [];
        const filtered = args.materials?.length || args.exclude_materials?.length ? { filtered: { steel: 12, unknown: 1 } } : {};
        const base = { model: "Test", ...filtered, aceVersion: keys.includes("TST_OLDADDIN") ? "1.9.0" : SERVER_VERSION, before, plan, planHash: `ph-${keys.join(",")}-${args.test_force_verify_mismatch ? "f" : ""}`, reportOnly, blocked, echo: args };
        if (args.check_only) return reply({ ok: true, result: { success: true, checkOnly: true, ...base, after: null, guard: null, warnings: { before: 10, after: null, new: [] } } });
        if (args.expect_version !== SERVER_VERSION) return reply({ ok: true, result: { success: false, stage: "blocked", error: "Nothing was changed: version", model: "Test", blocked: [{ what: "the whole plan", why: "version", how: "update" }] } });
        // expect_plan: the apply's recomputed plan must be the previewed one (TST_DRIFT: the model changed in between).
        if (!args.dry_run && (keys.includes("TST_DRIFT") || (args.expect_plan && args.expect_plan !== base.planHash)))
          return reply({ ok: true, result: { success: false, stage: "blocked", code: "plan-changed", model: "Test", error: "Nothing was changed: the plan differs from the preview", blocked: [{ what: "the plan", why: "recomputed plan differs", code: "plan-changed" }] } });
        const forced = !args.dry_run && !!args.test_force_verify_mismatch;   // assumed, as parameter_fill: the flag fails the apply's guard, not the dry run's
        return reply({ ok: true, result: { success: !forced, ...base, rolledBack: !!args.dry_run || forced, transaction: forced ? "GuardFailed" : args.dry_run ? "RolledBack" : "Committed",
          after: forced ? null : { clashes: 1, issues: 1, remaining: [{ a: 401, b: 402, why: "duplicate or overlapping walls" }] },
          ...(forced ? { afterIfKept: { clashes: 2, issues: 2, remaining: [{ a: 401, b: 402, why: "duplicate or overlapping walls" }] }, wouldDo: { join: 1, switchJoin: 1, cutback: 1, cope: 0 } } : {}),
          guard: forced ? { ok: false, checked: 8, mismatches: [{ element: 202, what: "tags", before: 2, after: 1 }] }
            : { ok: true, checked: 8, mismatches: [], changedValues: [{ element: 202, dimension: 9001, before: "6000", after: "5917" }] },
          warnings: { before: 10, after: 10, new: keys.includes("TST_NEWWARN") ? ["Highlighted elements are joined but do not intersect."] : [] },
          done: args.dry_run || forced ? { join: 0, switchJoin: 0, cutback: 0, cope: 0 } : { join: 1, switchJoin: 1, cutback: 1, cope: 1 }, skipped: [],
          [args.dry_run ? "wouldChange" : forced ? "wouldHaveChanged" : "changed"]: { added: 0, modified: 8, deleted: 0 } } });
      }
      case "presentation_standard":
        return reply({ ok: true, result: { success: true, checkOnly: !!args.check_only, dryRun: !!args.dry_run, standard: "ACE default: 3 mm for 1:50 and larger, 2.5 mm for smaller", [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 40, deleted: 0 }, echo: args } });
      case "set_parameters":
        return reply({ ok: true, result: { applied: args.changes.length, failed: 0, dryRun: !!args.dry_run, results: [] } });
      case "open_model": {
        const model = path.basename(args.path).replace(/\.rvt$/i, "");
        return reply({ ok: true, result: { model, path: args.path, workshared: true, detached: args.detach !== "none", audited: args.audit, worksetsOpened: args.worksets, echo: args,
          closed: args.close_previous ? (model.includes("KEEP") ? { model: "Test", closed: false, why: "it has unsaved changes" } : { model: "Test", closed: true }) : null,
          openDocuments: args.close_previous && !model.includes("KEEP") ? [model] : ["Test", model] } });
      }
      case "close_model": return reply({ ok: true, result: { model: args.model, closed: true } });
      case "coordination_report": {
        if (!args.interference) return reply({ ok: true, result: { htmlReport: "C:/x/Model - Coordination.html", excelList: "C:/x/Model - Coordination.csv", issues: 7, withPictures: 7 } });
        // The interference rows: one per clash, grouped by issue; test "MULTI" answers for two open models.
        const rows = (model) => [
          { issue: "I-2", issueTitle: "Wall W1 overlaps wall W2", clashKey: "k3", status: "active", aId: 401, aCategory: "Walls", aFamily: "Basic Wall", aType: "RC 200", bId: 402, bCategory: "Walls", bFamily: "Basic Wall", bType: "RC 200", level: "L1", depthMm: 200, x: 1.5, y: 2.25, z: 0, responsible: "STR" },
          { issue: "I-1", issueTitle: `Beam B7 into columns (${model})`, clashKey: "k1", status: "new", aId: 202, aCategory: "Structural Framing", aFamily: "BU <250>", aType: "BU 250", bId: 302, bCategory: "Structural Columns", bFamily: "Concrete-Rectangular-Column", bType: "400x400", level: "Roof", depthMm: 83.4, x: 10.1234, y: 5, z: 12.5, responsible: "STR" },
          { issue: "I-1", issueTitle: `Beam B7 into columns (${model})`, clashKey: "k2", status: "new", aId: 202, aCategory: "Structural Framing", aFamily: "BU <250>", aType: "BU 250", bId: 303, bCategory: "Structural Columns", bFamily: "Concrete-Rectangular-Column", bType: "400x400", level: "Roof", depthMm: 80, x: 14, y: 5, z: 12.5, responsible: "STR" }];
        const one = (model) => ({ model, test: args.test ?? "all", run: { id: "run-1", at: "2026-10-20T10:00:00Z" }, counts: { clashes: 3 }, rows: rows(model), ...(args.exclude_materials?.length ? { filtered: { steel: 12 } } : {}) });
        if (args.test === "EMPTY") return reply({ ok: true, result: { model: "Test", test: "EMPTY", run: { id: "run-2" }, counts: { clashes: 0 }, rows: [], filtered: { steel: 40 } } });
        if (args.test === "OLDADDIN") return reply({ ok: true, result: { htmlReport: "C:/x/old.html", issues: 0 } });
        return reply({ ok: true, result: args.test === "MULTI" ? { models: [one("Test"), one("Test-ARC")] } : one("Test") });
      }
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
assert.deepEqual(tools, ["assign_worksets", "backup_model", "check_setup", "clash_resolve", "clash_view", "close_model", "coordination_report", "describe_category", "describe_family", "execute_revit_code", "find_elements", "get_activity_log", "get_element_details", "get_model_brief", "get_selection", "lessons", "list_types", "list_views", "list_warnings", "model_changes", "model_dashboard", "open_model", "open_view", "parameter_check", "parameter_fill", "pending_changes", "presentation_standard", "project_rules", "report_issue", "revit_api_lookup", "revit_guide", "revit_status", "run_clash_test", "run_saved_script", "save_script", "saved_scripts", "select_elements", "set_clash_status", "set_parameters", "snapshots", "undo_last_claude_change", "vault", "view_image", "working_mode"]);
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
assert.deepEqual(pfSent.scope, { codes: ["AR", "ST", "ME", "EL", "PL", "FP", "ID", "LA", "CV", "BM", "PS"], models: {}, ownership: {} }, "the scope travels with the rows; without project codes the generic role codes read the discipline from the title");
assert.match(pfCheck.content[1].text, /2 linked model\(s\) not checked \(they are links; open them as documents to check them\): TST link A, TST link B/, "linked models are never skipped silently");
assert.ok(pfSent.check_only === true && pfSent.fill === false && pfSent.all_documents === true && pfSent.dry_run === false && pfSent.value === undefined && pfSent.max_ids === 20, "a check is read-only across the open models and needs no value");
assert.match(pfCheck.content[1].text, /Test: 2 rows - 0 complete, 0 complete with NA, 2 with blanks.*; 0 with NA, 0 out of scope/, "per-model summary text with the 1.10 counts");
assert.match(pfCheck.content[1].text, /1 row\(s\) skipped .*1 duplicate/, "skipped and duplicate rows are reported");
const pfJson = JSON.parse(pfCheck.content[0].text);
assert.ok(pfJson.documents[0].rows[0].key === "r1" && pfJson.documents[0].rows[0].status === "blanks" && pfJson.documents[0].rows[0].storageType === undefined, "compact JSON: key, name, status and counts per row");
assert.equal((await call("parameter_fill", { list: path.join(tmp, "nope.csv") })).isError, true);
const allSkipped = await call("parameter_check", { rows: [{ name: "A", categories: "Furniture" }, { name: "", categories: "Levels" }] });
assert.ok(allSkipped.isError && /No usable rows in the list \(2 skipped: 1 unknown category, 1 no parameter name\)/.test(allSkipped.content[0].text), "all rows skipped: the reasons are given");
assert.ok(!(await call("parameter_fill", { action: "fill", rows: [{ name: "A", categories: "Levels" }], all_documents: true, dry_run: true, value: "NA", explanation: "x" })).isError, "all_documents: the active model's plan runs, the others are listed");
assert.equal(seen.filter((x) => x.command === "parameter_fill").at(-1).args.all_documents, false, "a write plan is always the active model only");
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
assert.match(pfApplied.content[1].text, /Before the fill:\nTest: 1 rows - 0 complete, 0 complete with NA, 1 with blanks[\s\S]*After the fill:\nTest: 1 rows - 0 complete, 1 complete with NA, 0 with blanks.*1 with NA/, "after an apply the state after the fill is reported, the old one labelled before");
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
assert.match(rvw.content[1].text, /Scope defects \(1: 1 in active checks, 0 in parked checks\): TST_ST_Grade/);
assert.ok(fs.readdirSync(path.join(tmp, "preq")).some((f) => /^checker-review_.*\.md$/.test(f)));
assert.ok(seen.slice(before).every((x) => x.command === "project_rules"), "requirements and review_checker only ask where the vault is; they never check the model");
assert.equal((await call("parameter_check", { action: "review_checker", list: pAis })).isError, true, "review_checker needs the checker");
const pcCheck = await call("parameter_check", { ...pSources, all_documents: true });
const pcSent = seen.at(-1);
assert.ok(pcSent.command === "parameter_fill" && pcSent.args.check_only === true && pcSent.args.fill === false && pcSent.args.rows.length === 3, "the check sends the merged rows");
assert.ok(pcSent.args.rows.every((r) => r.key && !r.sources && !r.match), "server-only fields stay on the server");
assert.match(pcCheck.content[1].text, /Test: 3 rows/);
assert.match(pcCheck.content[1].text, /Actions \(top \d+ of \d+, read-only; \d+ auto-fixable, \d+ ready for parameter_fill\):\n1\. \[fill-[0-9a-f]{8}; auto-fixable/, "the reply carries the top actions");
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
// --- 1.11 execution stage: one plan per model (bind, derive, fill), fail fast, register before, evidence after ---
{
  const xdir = path.join(tmp, "xsrc");
  fs.mkdirSync(xdir);
  const G1 = "aaaaaaaa-0000-0000-0000-000000000001", G2 = "bbbbbbbb-0000-0000-0000-000000000002";
  const xProj = path.join(xdir, "project.txt"), xClient = path.join(xdir, "client.txt"), xList = path.join(xdir, "ais.csv"), xMap = path.join(xdir, "mapping.json");
  const head = "# This is a Revit shared parameter file.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\n";
  fs.writeFileSync(xProj, `${head}GROUP\t1\tTST\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\r\nPARAM\t${G1}\tTST_NB_Proj\tTEXT\t\t1\t1\t\t1\r\n`);
  const clientLine = `PARAM\t${G2}\tTST_NB_Client\tTEXT\t\t7\t1\tfrom the client\t1`;
  fs.writeFileSync(xClient, Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(`${head}GROUP\t7\tClient group\r\nGROUP\t8\tOther\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\r\n${clientLine}\r\nPARAM\tcccccccc-0000-0000-0000-000000000003\tTST_Other\tTEXT\t\t8\t1\t\t1\r\n`, "utf16le")]));
  fs.writeFileSync(xList, "parameter_name,categories,type_instance,origin\r\nTST_NB_Proj,Project Information,Instance,Custom\r\nTST_NB_Client,Project Information,Instance,Custom\r\nTST_F_Code,Project Information,Instance,Custom\r\nTST_F_Name,Project Information,Instance,Custom\r\nTST_BI_Mark,\"Systems, Components, Elements\",Type,Built-In\r\n");
  const mapping = (o = {}) => JSON.stringify({ about: "ACE parameter requirement mapping. Edit freely.", categories: {}, disciplines: {}, aliases: {}, models: {}, fill: { value: "NA", values: ["NA"] }, derive: [{ target: "TST_F_Code", copyFrom: "TST_F_Name" }],
    evidence: { items: [{ id: "TST-QA-1", text: "Project parameters | filled", rows: ["TST_F_Code", "tst_f_name"] }, { id: "TST-QA-2", text: "Not in this model", rows: ["TST_NOPE"] }] }, ...o });
  fs.writeFileSync(xMap, mapping());
  const xs = { list: xList, project_parameters: xProj, shared_parameters: [xClient], mapping: xMap, report_dir: regDir };
  const fills = () => seen.filter((x) => x.command === "parameter_fill");

  // The action list marks what the execution stage can run, with stable ids.
  const chk = JSON.parse((await call("parameter_check", xs)).content[0].text);
  const ids = chk.actionIds.map((x) => x.id);
  assert.ok(ids.some((i) => i.startsWith("bind-")) && ids.some((i) => i.startsWith("fill-")) && ids.some((i) => i.startsWith("derive-")) && chk.actionIds.every((x) => x.ready), "bind, derive and fill actions are auto-fixable and ready");
  assert.deepEqual(JSON.parse((await call("parameter_check", xs)).content[0].text).actionIds, chk.actionIds, "action ids are stable between checks");
  const chkText = (await call("parameter_check", xs)).content[1].text;
  assert.match(chkText, /TST_BI_Mark is a built-in parameter, not available on categories Furniture \(4 element\(s\)\): exclude those categories/, "built-ins get an information action, never a binding");
  assert.ok(!/Extend the binding of TST_BI_Mark|Bind .*TST_BI_Mark/.test(chkText), "no binding is proposed for a built-in");

  // Dry run: the check (with the register) first, then ONE write plan with the version, impact and no expect_model.
  const n0 = fills().length;
  const dry = await call("parameter_fill", { action: "fill", ...xs, dry_run: true, explanation: "Bind, derive and fill the project parameters" });
  assert.ok(!dry.isError, dry.content.map((c) => c.text).join("\n"));
  const [ck, wr] = fills().slice(n0);
  assert.ok(ck.args.check_only === true && ck.args.register === true && !ck.args.bind, "the plan starts with a read-only check that asks for the register");
  assert.ok(wr.args.dry_run === true && wr.args.impact === true && wr.args.expect_version === SERVER_VERSION && wr.args.expect_model === "Test", "the dry run carries the server version, the model the check saw, and asks for the impact");
  assert.ok(writesSeen.at(-1).registers.some((f) => /^missing-register-Test-.*\.csv$/.test(f)), "the missing register is on disk before the fill call is sent");
  const pb = wr.args.bind.find((b) => b.name === "TST_NB_Proj"), cb = wr.args.bind.find((b) => b.name === "TST_NB_Client");
  assert.ok(pb.file === xProj && pb.guid === G1 && pb.groupTypeId === "General" && pb.categories.join() === "OST_ProjectInformation", "a project-file definition binds from the project file");
  assert.ok(cb.guid === G2 && cb.file !== xClient && cb.file.includes("shared-parameters"), "a client-only definition binds from a temporary file");
  const tmpText = fs.readFileSync(cb.file).subarray(2).toString("utf16le");
  assert.ok(tmpText.includes(clientLine) && !tmpText.includes("TST_Other") && tmpText.includes("GROUP\t7\tClient group") && !tmpText.includes("GROUP\t8"), "the temporary file holds exactly the client's line (and its group), same encoding");
  assert.deepEqual(wr.args.derive, [{ target: "TST_F_Code", copyFrom: "TST_F_Name" }], "derive rules from the mapping");
  assert.ok(wr.args.fill === true && wr.args.value === "NA" && !wr.args.rows.some((r) => r.name === "TST_BI_Mark"), "fill with the approved NA policy; the built-in row is not in the plan");
  const dryText = dry.content.map((c) => c.text).join("\n");
  assert.match(dryText, /PLAN for Test \(dry run, rolled back\): Test: bind 2, derive 1 rule\(s\), fill "NA"/);
  assert.match(dryText, /Add this line to the project shared parameter file:\n.*TST_NB_Client/, "the reply says which client line to add to the project file");
  assert.match(dryText, /shown on 41 sheet\(s\) via title blocks: TST title block \(41 instance\(s\)\) will print NA/, "title blocks: the sheets that will show the value");
  assert.match(dryText, /label in TST tag \(tag\) will print NA on 3 sheet\(s\) \(inferred\)/, "other labels in plain words");
  assert.ok(!/label in TST title block/.test(dryText), "title blocks are said once, not again as a label");
  assert.match(dryText, /1 schedule\(s\) \(1 with a has-value filter/);
  assert.match(dryText, /"NA" passes a HasValue check/, "the NA caveat");
  assert.match(dryText, /Type parameters: 1 type\(s\) would be written once each, reaching 12 instance\(s\)/, "type writes with the instances they reach");
  assert.match(dryText, /Missing register: .*missing-register-Test-.*\.csv/);
  assert.ok(fs.readdirSync(regDir).some((f) => /^missing-register-Test-.*\.md$/.test(f)), "register summary");
  assert.ok(fs.readdirSync(regDir).some((f) => /^fill-run-Test-.*\.json$/.test(f)), "run record");

  // One gate per plan: another selection is another plan; the identical call applies with expect_model; once.
  assert.match((await call("parameter_fill", { action: "fill", ...xs, actions: [ids.find((i) => i.startsWith("fill-"))], explanation: "Fill only" })).content[0].text, /not been previewed/, "a different selection needs its own dry run");
  const app = await call("parameter_fill", { action: "fill", ...xs, explanation: "Bind, derive and fill the project parameters" });
  assert.ok(!app.isError, app.content.map((c) => c.text).join("\n"));
  const wa = fills().at(-1).args;
  assert.ok(wa.dry_run === false && wa.expect_model === "Test" && wa.expect_version === SERVER_VERSION && !wa.impact && JSON.stringify(wa.bind) === JSON.stringify(wr.args.bind), "the apply sends the same plan, in the model the dry run ran in");
  const ev = fs.readdirSync(regDir).find((f) => /^evidence-Test-.*\.md$/.test(f));
  assert.ok(ev, "evidence after the applied fill");
  const evText = fs.readFileSync(path.join(regDir, ev), "utf8");
  assert.ok(/missing before \(blank values\) \| 3/.test(evText) && /bound \| 2 of 2/.test(evText) && /"NA" written \| 2/.test(evText) && /self-check \| ok/.test(evText), "evidence: missing before, bound, NA written, self-check");
  assert.match(app.content.map((c) => c.text).join("\n"), /Evidence: .*evidence-Test-/);
  assert.match(evText, /## Requirement items \(after the apply\)/);
  assert.match(evText, /\| TST-QA-1 \| Project parameters \/ filled \| met with NA \| 2 \|/, "a requirement item from the mapping with its status after the apply");
  assert.match(evText, /\| TST-QA-2 \| Not in this model \| not checked in this model \| 0 \|/);
  assert.match(app.content.map((c) => c.text).join("\n"), /Requirement items: TST-QA-1 met with NA; TST-QA-2 not checked in this model\./);
  assert.match((await call("parameter_fill", { action: "fill", ...xs, explanation: "Bind, derive and fill the project parameters" })).content[0].text, /not been previewed/, "a preview is used once");
  // A rolled-back plan with bindings and derive rules: every count says "would have been ...; rolled back".
  await call("parameter_fill", { action: "fill", ...xs, dry_run: true, test_force_verify_mismatch: true, explanation: "Rollback test" });
  const rbk = (await call("parameter_fill", { action: "fill", ...xs, test_force_verify_mismatch: true, explanation: "Rollback test" })).content.map((c) => c.text).join("\n");
  assert.match(rbk, /Bindings: 0 \(2 would have been made; rolled back\) of 2/);
  assert.match(rbk, /Derived TST_F_Code = TST_F_Name: 0 \(1 would have been set; rolled back\) value\(s\)/);
  assert.match(rbk, /Type parameters: 1 type\(s\) would have been written \(rolled back\)/);
  assert.ok(!/After the fill:/.test(rbk), "no after state for a rolled-back plan");

  // Model-title rules that match no open model are said in the plan, never silent.
  fs.writeFileSync(xMap, mapping({ derive: { rules: [{ target: "TST_F_Code", copyFrom: "TST_F_Name" }], projectInformationValues: { "TST-*-NOPE": { TST_F_Name: "x" }, "Tes*": { TST_F_Name: "y" } }, status: "approved" }, models: { "TST-NOPE": "ST" } }));
  const gw = (await call("parameter_fill", { action: "fill", ...xs, dry_run: true, explanation: "Globs" })).content.map((c) => c.text).join("\n");
  assert.match(gw, /Warning: rule 'TST-\*-NOPE' matches no open model: Test \(a glob matches the full model title/, "an unmatched derive glob is a warning");
  assert.match(gw, /Warning: models entry 'TST-NOPE' matches no open model: Test\./);
  assert.ok(!/rule 'Tes\*'/.test(gw), "a matching glob is not reported");
  fs.writeFileSync(xMap, mapping());

  // An ambiguous discipline: a warning first in the reply and the workbook; titleField reaches the scope.
  {
    const ambMap = path.join(xdir, "amb-mapping.json");
    fs.writeFileSync(ambMap, JSON.stringify({ about: "ACE parameter requirement mapping. Edit freely.", categories: {}, disciplines: {}, aliases: {}, models: { titleField: 3 }, fill: { value: "NA", values: ["NA"] } }));
    const amb = await call("parameter_check", { rows: [{ name: "TST_AMB_X", categories: "Project Information" }], mapping: ambMap, report_dir: path.join(xdir, "amb") });
    assert.equal(fills().at(-1).args.scope.titleField, 3, "titleField (under models) reaches the scope");
    assert.ok(!("titleField" in fills().at(-1).args.scope.models), "and is not sent as a model title");
    assert.match(amb.content[1].text, /^Warning: Test: discipline not read from the title: candidates PL, AR; set models or titleField in the mapping\. Until then this result judges every discipline's rows/, "the warning comes first");
    const wb = fs.readdirSync(path.join(xdir, "amb")).find((f) => f.endsWith(".xlsx"));
    const { execFileSync } = await import("node:child_process");
    assert.ok(wb && execFileSync("unzip", ["-p", path.join(xdir, "amb", wb), "xl/workbook.xml"]).toString().indexOf('name="Warnings"') < execFileSync("unzip", ["-p", path.join(xdir, "amb", wb), "xl/workbook.xml"]).toString().indexOf('name="Summary"'), "the workbook opens on the warnings");
  }

  // Fail fast before Revit is asked: an unapproved NA policy refuses a picked fill; the default plan holds it, never silently.
  fs.writeFileSync(xMap, mapping({ fill: { value: "NA", values: ["NA"], status: "proposed" } }));
  const n1 = seen.length;
  const na = await call("parameter_fill", { action: "fill", ...xs, actions: [ids.find((i) => i.startsWith("fill-"))], dry_run: true, explanation: "Fill" });
  assert.ok(na.isError && /Blocked before Revit was asked/.test(na.content[0].text) && /NA policy \("NA"\) is not approved/.test(na.content[0].text), "unapproved NA policy refused with the reason");
  assert.ok(seen.slice(n1).every((x) => x.command !== "parameter_fill"), "refused before any bridge call");
  const held = await call("parameter_fill", { action: "fill", ...xs, dry_run: true, explanation: "Run what is ready" });
  assert.ok(!fills().at(-1).args.check_only && fills().at(-1).args.fill === false && fills().at(-1).args.bind.length === 2, "the default plan runs what is ready");
  assert.match(held.content.at(-1).text, /Not in this plan:\n- action fill-[0-9a-f]{8} .*no approved fill value/, "the held fill is listed with its reason");
  fs.writeFileSync(xMap, mapping());

  // A changed source file blocks the apply, with the file named.
  await call("parameter_fill", { action: "fill", ...xs, dry_run: true, explanation: "Again" });
  fs.appendFileSync(xList, "TST_F_Name,Project Information,Instance,Custom\r\n");
  const changed = await call("parameter_fill", { action: "fill", ...xs, explanation: "Again" });
  assert.ok(changed.isError && /source files changed since the dry run \(ais\.csv\)/.test(changed.content[0].text), "the changed file is named");

  // Version mismatch: refused on the server from the pre-flight, before the write plan.
  const n2 = writesSeen.length;
  const old = await call("parameter_fill", { action: "fill", rows: [{ name: "TST_OLDADDIN_X", categories: "Project Information" }], value: "NA", report_dir: regDir, dry_run: true, explanation: "Fill" });
  assert.ok(old.isError && /the ACE server is .*, the add-in in Revit is 1\.9\.0/.test(old.content[0].text) && /build-package\.ps1/.test(old.content[0].text), "version mismatch refused in plain words");
  assert.equal(writesSeen.length, n2, "no write plan reaches the add-in");

  // Borrowed targets: the add-in's block is shown with who holds them and what clears it.
  const bor = await call("parameter_fill", { action: "fill", rows: [{ name: "TST_BORROW_X", categories: "Project Information" }], value: "NA", report_dir: regDir, dry_run: true, explanation: "Fill" });
  assert.ok(bor.isError && /3 element\(s\) of TST_BORROW_X: borrowed by another user \(3 element\(s\), e\.g\. 11, 12, 13\); held by jane\. How to clear: ask jane/.test(bor.content[0].text), "a block is never silent");

  // Self-check mismatch: the add-in rolls the whole plan back; the reply and the evidence say so.
  const vr = [{ name: "TST_VERIFY_X", categories: "Project Information" }];
  await call("parameter_fill", { action: "fill", rows: vr, value: "NA", report_dir: regDir, dry_run: true, explanation: "Fill" });
  const vm = await call("parameter_fill", { action: "fill", rows: vr, value: "NA", report_dir: regDir, explanation: "Fill" });
  assert.ok(vm.isError && /Self-check FAILED: 1 mismatch\(es\); the whole plan was rolled back, nothing changed\. TST_VERIFY_X #101: expected "NA", found ""/.test(vm.content.at(-1).text), "verify mismatch reported as rolled back");
  const vmText = vm.content.map((c) => c.text).join("\n");
  assert.match(vmText, /Fill: 2 value\(s\) would have been set; rolled back, nothing changed/, "a rolled-back apply never says values were set");
  assert.ok(!/Fill: \d+ value\(s\) set/.test(vmText) && !/Bindings: .* made/.test(vmText.replace(/would have been made/g, "")), "no 'set' / 'made' counts after a rollback");
  const vmBody = JSON.parse(vm.content[0].text);
  assert.ok(vmBody.changed === undefined && vmBody.rolledBack === true && vmBody.fill.set === 0 && vmBody.fill.wouldSet === 2 && vmBody.wouldHaveChanged, "no changed block; what would have changed instead");
  const vmEv = fs.readdirSync(regDir).filter((f) => f.startsWith("evidence-")).map((f) => fs.readFileSync(path.join(regDir, f), "utf8")).find((t) => /FAILED: 1 mismatch/.test(t));
  assert.ok(vmEv, "the evidence records the failed self-check");
  assert.ok(/Rolled back: nothing changed/.test(vmEv) && /"NA" written \| 0 \(2 would have been set; rolled back\)/.test(vmEv), "evidence: would have been set; rolled back");

  // All open models: the active model's plan runs; the others are listed as read-only plans.
  const multi = await call("parameter_fill", { action: "fill", rows: [{ name: "TST_M_A", categories: "Project Information" }], value: "NA", report_dir: regDir, all_documents: true, dry_run: true, explanation: "Fill" });
  assert.match(multi.content.at(-1).text, /Other open models \(read-only plans; switch to the model and run the same call, one approval each\): Test-STR: fill "NA"/);
  assert.equal(fills().at(-1).args.all_documents, false, "only the active model is written");

  // The acceptance-test flag: the add-in fails its self-check on purpose; part of the gate key; said plainly.
  const tf = { action: "fill", rows: [{ name: "TST_TF_X", categories: "Project Information" }], value: "NA", report_dir: regDir, explanation: "Fill" };
  await call("parameter_fill", { ...tf, dry_run: true, test_force_verify_mismatch: true });
  assert.equal(fills().at(-1).args.test_force_verify_mismatch, true, "the flag reaches the bridge on the dry run");
  assert.match((await call("parameter_fill", tf)).content[0].text, /not been previewed/, "the flag is part of the gate key");
  const forced = await call("parameter_fill", { ...tf, test_force_verify_mismatch: true });
  assert.ok(forced.isError && fills().at(-1).args.test_force_verify_mismatch === true && /Self-check FAILED \(forced by test flag\) - rolled back, nothing changed/.test(forced.content.at(-1).text));

  // guid-differs (DEC-0004): a decision, and an explicit-only rebind that keeps the values.
  fs.appendFileSync(xProj, `PARAM\teeeeeeee-0000-0000-0000-00000000000e\tTST_GD_Code\tTEXT\t\t1\t1\t\t1\r\n`);
  fs.writeFileSync(xList, "parameter_name,categories,type_instance\r\nTST_GD_Code,Project Information,Instance\r\n");
  const gd = JSON.parse((await call("parameter_check", xs)).content[0].text);
  const gdText = (await call("parameter_check", xs)).content[1].text;
  assert.match(gdText, /TST_GD_Code: the model's GUID differs from project\.txt; confirm which is current \(options: rebind the model to the file \(explicit rebind action (rebind-[0-9a-f]{8})\) \| update the vault file from the model/);
  assert.ok(!/wrong GUID|fails exports/.test(gdText), "never called wrong");
  const rbId = gdText.match(/explicit rebind action (rebind-[0-9a-f]{8})/)[1];
  assert.ok(gd.actionIds.some((x) => x.id === rbId), "listed among the runnable ids");
  const noRebind = await call("parameter_fill", { action: "fill", ...xs, dry_run: true, explanation: "Default plan" });
  assert.ok(noRebind.isError && /Nothing to run/.test(noRebind.content[0].text), "a rebind is never in the default plan");
  const rbDry = await call("parameter_fill", { action: "fill", ...xs, actions: [rbId], dry_run: true, explanation: "Rebind to the project file" });
  const rbSent = fills().at(-1).args;
  assert.deepEqual(rbSent.rebind.map((e) => [e.name, e.oldGuid, e.guid, e.file === xProj, /^[0-9a-f]{64}$/.test(e.fileHash)]), [["TST_GD_Code", "dddddddd-0000-0000-0000-00000000000d", "eeeeeeee-0000-0000-0000-00000000000e", true, true]]);
  const rbText = rbDry.content.at(-1).text;
  assert.match(rbText, /Rebind \(explicit\): TST_GD_Code dddddddd.* -> eeeeeeee.*; the values are kept: read before, written back under the new definition and verified/);
  assert.match(rbText, /Dropped with the old definition .*:\n- schedule field TST_GD_Code in TST schedule/);
  assert.match(rbText, /The old definition is used by:\n- TST_GD_Code: 1 schedule\(s\)/);
  assert.ok(!(await call("parameter_fill", { action: "fill", ...xs, actions: [rbId], explanation: "Rebind to the project file" })).isError, "applied after its own dry run");
  const vu = path.join(tmp, "vu", "ACE Vault");
  fs.mkdirSync(vu, { recursive: true });
  const { report_dir: _rd, ...noDir } = xs;
  const untrusted = await call("parameter_fill", { action: "fill", ...noDir, vault: vu, actions: [rbId], dry_run: true, explanation: "Rebind" });
  assert.match(untrusted.content.at(-1).text, /vault not trusted: files written to .*parameter-fill/, "an untrusted vault is said, the files go to the reports folder");
}
// --- clash_resolve: plan read-only; a fix is a check, a dry run, then the identical apply, each naming the model ---
{
  const crDir = path.join(tmp, "clash");
  const crs = () => seen.filter((x) => x.command === "clash_resolve");
  const ST = ["OST_StructuralColumns", "OST_StructuralFraming", "OST_Walls", "OST_Floors", "OST_StructuralFoundation", "OST_Stairs", "OST_Ramps"];
  const p = await call("clash_resolve", {});
  const ps = crs().at(-1).args;
  assert.ok(!p.isError && ps.check_only === true && ps.dry_run === false && ps.test === "ST vs ST" && ps.block_on_borrowed === true && ps.expect_version === SERVER_VERSION && !ps.order, "plan: a read-only check of the standard test, the add-in's default order");
  assert.deepEqual([ps.a_categories, ps.b_categories], [ST, ST], "ST vs ST: the same structural set on both sides");
  await call("clash_resolve", { test: "Arch vs ST", a_disciplines: ["ARC"], b_disciplines: ["STR"] });
  const os = crs().at(-1).args;
  assert.ok(os.test === "Arch vs ST" && !os.a_categories && !os.b_categories && os.a_disciplines[0] === "ARC" && os.b_disciplines[0] === "STR", "another test runs as the add-in defines it: no ST categories under its name");
  const pt = p.content.at(-1).text;
  assert.match(pt, /^CLASH RESOLVE ST vs ST in Test \(plan, read-only, nothing changed\)\./);
  assert.match(pt, /Before: 5 clash\(es\) in 3 issue\(s\); by pair: Structural Framing x Structural Columns 3, Walls x Walls 1, Floors x Floors 1\./);
  assert.match(pt, /Plan: 4 change\(s\): join 1, switch-join 1, cutback 1, cope 1\. Only joins, switched join orders, steel end cutbacks and copes; nothing is deleted, recreated or moved\./);
  assert.match(pt, /Report only \(needs a person, 1\):\n- duplicate or overlapping walls \(1, e\.g\. 401 x 402\): decide which wall stays/);
  assert.ok(!/After:/.test(pt), "a plan has no after stage");

  // The gate: an apply without its dry run is refused before Revit is asked; a dry run needs an explanation.
  const fx = { action: "fix", report_dir: crDir, explanation: "Join and cut back the structural clashes" };
  const n0 = crs().length;
  const refused = await call("clash_resolve", fx);
  assert.ok(refused.isError && /not been previewed/.test(refused.content[0].text) && crs().length === n0, "no apply without its identical dry run");
  assert.ok((await call("clash_resolve", { action: "fix", dry_run: true })).isError, "a dry run says what it does");

  const dry = await call("clash_resolve", { ...fx, dry_run: true });
  const [dc, dd] = crs().slice(-2).map((x) => x.args);
  assert.ok(dc.check_only === true && dd.check_only === false && dd.dry_run === true && dd.expect_model === "Test" && dd.expect_version === SERVER_VERSION, "check first, then the dry run naming the model and the version");
  const dt = dry.content.at(-1).text;
  assert.ok(!dry.isError);
  assert.match(dt, /\(dry run\)\./);
  assert.match(dt, /After: 1 clash\(es\) would remain in 1 issue\(s\):\n- 401 x 402: duplicate or overlapping walls/);
  assert.match(dt, /Annotation guard: ok \(8 touched element\(s\): still there with the same type and location, the same tags, dimensions and spot elevations\)\./);
  assert.match(dt, /Warnings: before 10, after 10, new none\./);
  assert.match(dt, /Information: 1 dimension value\(s\) on shortened members changed, references intact \(#202 dimension #9001: "6000" -> "5917"\)\./, "changed dimension values are information, not a failure");
  assert.match(dt, /Changes: would have been join 1, switch-join 1, cutback 1, cope 1; rolled back, nothing changed\./);
  assert.match(dt, /Apply with the identical call/);
  assert.match((await call("clash_resolve", { ...fx, level: "L1" })).content[0].text, /not been previewed/, "another request needs its own dry run");

  const app = await call("clash_resolve", fx);
  const aa = crs().at(-1).args;
  assert.ok(!app.isError && aa.dry_run === false && aa.check_only === false && aa.expect_model === "Test" && aa.expect_version === SERVER_VERSION, "the apply names the model the dry run ran in");
  assert.equal(aa.expect_plan, "ph--", "the apply names the plan its dry run showed");
  assert.ok(!("expect_plan" in dd), "a dry run sends no expect_plan");
  const at = app.content.at(-1).text;
  assert.match(at, /\(applied as ONE undo step\)\./);
  assert.match(at, /After: 1 clash\(es\) remain in 1 issue\(s\)/);
  assert.match(at, /Changes: join 1, switch-join 1, cutback 1, cope 1 done\./);
  assert.match(at, /Next: re-run run_clash_test with test "ST vs ST" \(the same categories\) so the fixed clashes show as resolved\./);
  const evFile = at.match(/Evidence: (.+)/)[1].trim();
  assert.match(path.basename(evFile), /^evidence-clash-Test-.+\.md$/);
  const ev = fs.readFileSync(evFile, "utf8");
  assert.match(ev, /Applied as ONE undo step\./);
  assert.match(ev, /\| clashes before \| 5 \(3 issue\(s\)\) \|\n\| clashes after \| 1 \|/);
  assert.match(ev, /\| cutback \| 1 of 1 \|/);
  assert.match(ev, /- cutback: 202 x 302 \(Structural Framing x Structural Columns, Steel, Roof\): beam end runs into the column/);
  assert.match(ev, /\| annotation guard \| ok \(8 element\(s\) checked\) \|\n\| warnings \| before 10, after 10, new 0 \|/);
  assert.match(ev, /## Remaining clashes\n\n- 401 x 402: duplicate or overlapping walls/);
  assert.match(ev, /## Dimension values changed on shortened members \(references intact; information, not a failure\)\n\n- #202 dimension #9001: "6000" -> "5917"/);
  assert.ok(fs.readdirSync(crDir).some((f) => /^clash-run-Test-.+\.json$/.test(f)), "a run record");
  assert.match((await call("clash_resolve", fx)).content[0].text, /not been previewed/, "a preview is used once");

  // The acceptance-test flag: part of the gate key; the apply's guard fails, everything is rolled back and said so.
  const ff = { ...fx, test_force_verify_mismatch: true };
  await call("clash_resolve", { ...ff, dry_run: true });
  assert.equal(crs().at(-1).args.test_force_verify_mismatch, true, "the flag reaches the bridge on the dry run");
  assert.match((await call("clash_resolve", fx)).content[0].text, /not been previewed/, "the flag is part of the gate key");
  const forced = await call("clash_resolve", ff);
  const ft = forced.content.at(-1).text;
  assert.ok(forced.isError && crs().at(-1).args.test_force_verify_mismatch === true);
  assert.match(ft, /\(NOT applied: rolled back\)\./);
  assert.match(ft, /Annotation guard FAILED \(forced by test flag\) - rolled back, nothing changed\./);
  assert.match(ft, /Changes: would have been join 1, switch-join 1, cutback 1; rolled back, nothing changed\./, "the add-in's wouldDo counts");
  assert.ok(!/re-run run_clash_test|Changes: join/.test(ft), "a rolled-back apply never reads as done");
  const fev = fs.readFileSync(ft.match(/Evidence: (.+)/)[1].trim(), "utf8");
  assert.match(fev, /Rolled back: nothing changed/);
  assert.match(fev, /\| cutback \| 0 \(1 would have been done; rolled back\) \|\n\| cope \| 0 \(0 would have been done; rolled back\) \|/);
  assert.match(fev, /\| clashes after \| 5 \(as before: rolled back; would have been 2\) \|/, "afterIfKept is what would have been");

  // The add-in recomputes the plan on the apply: a different one (expect_plan) is refused and said plainly.
  await call("clash_resolve", { ...fx, keys: ["TST_DRIFT"], dry_run: true });
  const drift = await call("clash_resolve", { ...fx, keys: ["TST_DRIFT"] });
  assert.ok(drift.isError && /the model changed since the preview: run the dry run again/.test(drift.content[0].text) && crs().at(-1).args.expect_plan === "ph-TST_DRIFT-");
  assert.match(fev, /## Annotation guard differences\n\n- #202 tags: 2 -> 1/);

  // Fail fast: a borrowed element or another add-in version stops the fix before the dry run.
  const n1 = crs().length;
  const bor = await call("clash_resolve", { ...fx, keys: ["TST_BORROW"], dry_run: true });
  assert.ok(bor.isError && /stopped before the dry run, nothing changed/.test(bor.content[0].text));
  assert.match(bor.content[0].text, /- 2 element\(s\) to join: borrowed by another user \(2 element\(s\), e\.g\. 201, 301\); held by jane\. How to clear: ask jane to synchronise and relinquish/);
  const old = await call("clash_resolve", { ...fx, keys: ["TST_OLDADDIN"], dry_run: true });
  assert.ok(old.isError && /the add-in in Revit is 1\.9\.0/.test(old.content[0].text));
  assert.ok(crs().slice(n1).every((x) => x.args.check_only === true), "only the read-only check ran");
  // A dry run that raises new warnings is not a preview to apply.
  const nw = await call("clash_resolve", { ...fx, keys: ["TST_NEWWARN"], dry_run: true });
  assert.ok(nw.isError && /This dry run cannot be applied: it raises new Revit warnings/.test(nw.content.at(-1).text) && /new Highlighted elements are joined/.test(nw.content.at(-1).text));
  assert.match((await call("clash_resolve", { ...fx, keys: ["TST_NEWWARN"] })).content[0].text, /not been previewed/);
  const n2 = crs().length;
  assert.match((await call("clash_resolve", { ...fx, keys: ["TST_NOTHING"], dry_run: true })).content.at(-1).text, /Nothing to fix automatically in Test: no dry run needed/);
  assert.equal(crs().length, n2 + 1, "nothing to fix: no dry run");

  // The project's order and steel rules come from the vault; the call wins; an unknown category fails closed.
  const cv = path.join(tmp, "cv", "ACE Vault");
  fs.mkdirSync(path.join(cv, "rules"), { recursive: true });
  fs.writeFileSync(path.join(cv, "rules", "clash-resolve.json"), JSON.stringify({ order: ["foundation", "column", "wall", "beam", "slab", "stair"], steel: { coping: false, cutback: false } }));
  const vr = await call("clash_resolve", { vault: cv, steel: { cutback: true } });
  const vs = crs().at(-1).args;
  assert.deepEqual(vs.order, ["OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_StructuralFraming", "OST_Floors", "OST_Stairs"]);
  assert.deepEqual(vs.steel, { coping: false, cutback: true }, "the call's steel setting over the vault's");
  assert.match(vr.content.at(-1).text, /Project rules: .*clash-resolve\.json \(join order OST_StructuralFoundation > OST_StructuralColumns/);
  await call("clash_resolve", { vault: cv, order: ["column", "OST_Walls"] });
  assert.deepEqual(crs().at(-1).args.order, ["OST_StructuralColumns", "OST_Walls"], "the call's order wins");
  const n3 = crs().length;
  assert.match((await call("clash_resolve", { order: ["roof"] })).content[0].text, /"roof" is not a category ACE knows/);
  fs.writeFileSync(path.join(cv, "rules", "clash-resolve.json"), "{ order: ");
  const bad = await call("clash_resolve", { vault: cv });
  assert.ok(bad.isError && /clash-resolve\.json is not valid JSON/.test(bad.content[0].text) && crs().length === n3, "bad rules: refused before Revit is asked");
}
// --- material filters, the interference report and clash_resolve scoped to concrete ---
{
  await call("run_clash_test", { test: "ST vs ST", exclude_materials: ["steel"], a_materials: ["concrete"] });
  const rs = seen.filter((x) => x.command === "run_clash_test").at(-1).args;
  assert.deepEqual([rs.exclude_materials, rs.a_materials], [["steel"], ["concrete"]], "run_clash_test passes the material filters");
  assert.ok((await call("run_clash_test", { materials: ["glass"] })).isError, "only the known materials");

  const n0 = seen.length;
  const noDir = await call("coordination_report", { interference: true });
  assert.ok(noDir.isError && /needs out_dir/.test(noDir.content[0].text) && seen.length === n0, "interference needs out_dir, checked before Revit is asked");
  const iDir = path.join(tmp, "interference");
  const ir = await call("coordination_report", { interference: true, out_dir: iDir, test: "ST vs ST", exclude_materials: ["steel"], level: "Roof" });
  const is = seen.at(-1);
  assert.ok(is.command === "coordination_report" && is.args.interference === true && !("out_dir" in is.args) && is.args.exclude_materials[0] === "steel" && is.args.level === "Roof", "the filters reach the add-in, the folder stays here");
  const it = ir.content.at(-1).text;
  assert.match(it, /Interference report Test \(ST vs ST\): 3 clash\(es\) in 2 issue\(s\); by status new 2, active 1; by pair Structural Columns x Structural Framing 2, Walls x Walls 1\. Left out by the material filter: steel 12\./);
  const files = [...it.matchAll(/^- (.+)$/gm)].map((m) => m[1].trim());
  assert.deepEqual(files.map((f) => path.extname(f)), [".html", ".csv", ".xlsx"]);
  assert.ok(files.every((f) => path.dirname(f) === iDir && /^interference-Test-ST vs ST-\d{4}-\d\d-\d\d_\d{6}\./.test(path.basename(f))), "interference-<model>-<test>-<stamp> in out_dir");
  const h = fs.readFileSync(files[0], "utf8");
  assert.match(h, /<h1>Interference Report<\/h1>/);
  assert.match(h, /<dt>Model<\/dt><dd>Test<\/dd><dt>Test<\/dt><dd>ST vs ST<\/dd>/);
  assert.match(h, /<dt>Filters<\/dt><dd>level Roof; materials: excluded steel<\/dd>/);
  assert.match(h, /Issue I-1: Beam B7 into columns \(Test\) <span class="n">\(2 clashes\)<\/span>[\s\S]*Issue I-2/, "grouped by issue, the largest first");
  assert.match(h, /<td>Structural Framing : BU &lt;250&gt; : BU 250 : 202<\/td><td>Structural Columns : Concrete-Rectangular-Column : 400x400 : 302<\/td><td>Roof<\/td><td class="num">83<\/td><td class="num">10.123 \/ 5 \/ 12.5<\/td><td>new<\/td><td>STR<\/td>/);
  assert.ok(h.includes("#EF3340") && h.includes("Poppins") && /@media print/.test(h), "brand colours and fonts, print-friendly");
  assert.ok(!/<script|<link|https?:\/\//i.test(h), "self-contained: no scripts, no external files");
  const cs = fs.readFileSync(files[1], "utf8");
  assert.match(cs, /^\ufeffissue,issue title,clash key,status,A category,A family,A type,A id,B category/);
  assert.match(cs, /\r\nI-1,Beam B7 into columns \(Test\),k1,new,Structural Framing,BU <250>,BU 250,202,Structural Columns/);
  assert.equal(fs.readFileSync(files[2]).subarray(0, 2).toString(), "PK", "an xlsx (zip)");
  const multi = (await call("coordination_report", { interference: true, out_dir: iDir, test: "MULTI" })).content.at(-1).text;
  assert.match(multi, /Interference report Test \(MULTI\)[\s\S]*Interference report Test-ARC \(MULTI\)/, "one file set per model");
  assert.equal([...multi.matchAll(/^- (.+)$/gm)].length, 6);

  // clash_resolve scoped to concrete: the filter is said, and it is part of the gate key.
  const cfx = { action: "fix", report_dir: path.join(tmp, "clash-mat"), explanation: "Join the concrete clashes", materials: ["concrete"] };
  const cp = await call("clash_resolve", { materials: ["concrete"] });
  assert.deepEqual(seen.at(-1).args.materials, ["concrete"]);
  assert.match(cp.content.at(-1).text, /Material filter: only concrete; left out: steel 12, unknown 1 element\(s\)\./);
  await call("clash_resolve", { ...cfx, dry_run: true });
  const { materials: _m, ...noMat } = cfx;
  assert.match((await call("clash_resolve", noMat)).content[0].text, /not been previewed/, "another material scope needs its own dry run");
  assert.match((await call("clash_resolve", { ...cfx, materials: ["steel"] })).content[0].text, /not been previewed/);
  const ca = await call("clash_resolve", cfx);
  assert.ok(!ca.isError && seen.at(-1).args.materials[0] === "concrete" && seen.at(-1).args.dry_run === false);
  assert.match(fs.readFileSync(ca.content.at(-1).text.match(/Evidence: (.+)/)[1].trim(), "utf8"), /\| material filter \| only concrete; left out steel 12, unknown 1 \|/);
}
// --- an empty interference result still gets its report; an answer without rows is an older add-in ---
{
  const eDir = path.join(tmp, "interference-empty");
  const er = await call("coordination_report", { interference: true, out_dir: eDir, test: "EMPTY", exclude_materials: ["steel"] });
  const et = er.content.at(-1).text;
  assert.ok(!er.isError && /Interference report Test \(EMPTY\): 0 interferences found \(the report is written all the same\)/.test(et));
  const ef = [...et.matchAll(/^- (.+)$/gm)].map((m) => m[1].trim());
  assert.equal(ef.length, 3, "html, csv and xlsx for an empty result");
  const eh = fs.readFileSync(ef[0], "utf8");
  assert.match(eh, /<dt>Result<\/dt><dd><b>0 interferences found<\/b><\/dd><dt>Left out by the material filter<\/dt><dd>steel 40<\/dd>/);
  assert.match(eh, /<dt>Filters<\/dt><dd>materials: excluded steel<\/dd>/);
  assert.match(eh, /<thead>[\s\S]*<td colspan="7">No interferences<\/td>/, "an empty table with one row saying so");
  assert.match(fs.readFileSync(ef[1], "utf8"), /^\ufeffissue,issue title,clash key,status,[^\n]*\r\n$/, "the CSV has its header only");
  assert.equal(fs.readFileSync(ef[2]).subarray(0, 2).toString(), "PK");
  const old = await call("coordination_report", { interference: true, out_dir: eDir, test: "OLDADDIN" });
  assert.ok(old.isError && /without interference rows: the add-in in Revit is older/.test(old.content[0].text), "no rows list: never an empty report by mistake");
}
// --- open_model / close_model: a batch of models in one Revit session ---
{
  const om = await call("open_model", { path: "C:/Projects/P1/P1-ST-0001.rvt", audit: true, close_previous: true });
  const oa = seen.at(-1);
  assert.ok(oa.command === "open_model" && oa.args.detach === "none" && oa.args.audit === true && oa.args.worksets === "all" && oa.args.close_previous === true, "defaults: no detach, all worksets");
  assert.match(om.content.at(-1).text, /^Opened P1-ST-0001 \(audited \/ worksets: all\); closed Test\. Open now: P1-ST-0001\.$/);
  const kept = await call("open_model", { path: "C:/Projects/P1/P1-KEEP.rvt", detach: "preserve_worksets", close_previous: true });
  assert.match(kept.content.at(-1).text, /^Opened P1-KEEP \(detached, worksets preserved \/ worksets: all\); kept Test open: it has unsaved changes\./);
  const n = seen.length;
  const disc = await call("open_model", { path: "C:/Projects/P1/P1-AR.rvt", detach: "discard_worksets" });
  assert.ok(disc.isError && /discards the workset information in the opened copy/.test(disc.content[0].text) && seen.length === n, "discard_worksets needs the user's OK, before Revit is asked");
  assert.match((await call("open_model", { path: "C:/Projects/P1/P1-AR.rvt", detach: "discard_worksets", confirm: true, worksets: "none" })).content.at(-1).text, /\(detached, worksets discarded \/ worksets: none\)/);
  assert.ok(!("confirm" in seen.at(-1).args), "confirm stays on this side");
  assert.equal(json(await call("close_model", { model: "Test" })).closed, true);
  assert.equal(seen.at(-1).command, "close_model");
}
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
