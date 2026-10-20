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
        // The worksets section carries the generic naming rule (pattern, allowed names, renames); synthetic TST_ names.
        const naming = { pattern: "^TST_[A-Za-z]+$", allowed: ["TST_Columns", "TST_Walls", "TST_Empty", "TST_Beams", "TST_Old", "TST_Verifyfail", "TST_Drift"], rename: [{ from: "Workset1", to: "TST_Walls" }] };
        return reply({ ok: true, result: { model: "Test", section: args.section, source: "project: C:/Projects/Test/ACE Vault/project-requirements.json", hash: "abc", rules: args.section === "worksets" ? { ...ex, naming } : ex } });
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
        // 1.12.8: a trim rides on its join as the fallback (runs only if the join does not clear).
        if (args.trim_to_face && plan[0]) plan[0] = { ...plan[0], trim: { kind: "trim", id: "trim:211>302:end", fallbackFor: plan[0].id, cutCategory: "Structural Framing", lengthBefore: 4833, lengthAfter: 3822, trimMm: 1011, face: "Structural Columns 302 near face" } };
        const reportOnly = [{ a: 401, b: 402, aCategory: "Walls", bCategory: "Walls", why: "duplicate or overlapping walls", recommendation: "decide which wall stays" }];
        const blocked = keys.includes("TST_BORROW") ? [{ what: "2 element(s) to join", why: "borrowed by another user", elements: 2, sampleIds: [201, 301], holder: "jane", how: "ask jane to synchronise and relinquish, then run again", code: "borrowed" }] : [];
        const filtered = args.materials?.length || args.exclude_materials?.length ? { filtered: { steel: 12, unknown: 1 } } : {};
        const base = { model: "Test", ...filtered, aceVersion: keys.includes("TST_OLDADDIN") ? "1.9.0" : SERVER_VERSION, before, plan, planHash: `ph-${keys.join(",")}-${args.test_force_verify_mismatch ? "f" : ""}`, reportOnly, blocked, echo: args,
          ...(args.scope === "all-touching" ? { dropped: { notTouching: 40, trim: 0 }, scope: "all-touching", scopeSource: args.scope_source ?? "request", touching: { candidates: 120, pairs: 80, right: 38, join: 30, switchJoin: 12, byPair: { "Floors x Structural Columns": 25 } } } : {}),
          storeKey: keys.includes("TST_STORE") ? "TST_Central" : "Test", orderSource: args.order_source ?? (args.order ? "request" : "default"),
          order: args.order ?? ["OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming", "OST_Stairs"] };
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
          done: args.dry_run || forced ? { join: 0, switchJoin: 0, cutback: 0, cope: 0, trim: 0 } : { join: 1, switchJoin: 1, cutback: 1, cope: 1, trim: args.trim_to_face ? 1 : 0 }, skipped: [],
          [args.dry_run ? "wouldChange" : forced ? "wouldHaveChanged" : "changed"]: { added: 0, modified: 8, deleted: 0 } } });
      }
      case "presentation_standard":
        return reply({ ok: true, result: { success: true, checkOnly: !!args.check_only, dryRun: !!args.dry_run, standard: "ACE default: 3 mm for 1:50 and larger, 2.5 mm for smaller", [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 40, deleted: 0 }, echo: args } });
      case "set_parameters":
        return reply({ ok: true, result: { applied: args.changes.length, failed: 0, dryRun: !!args.dry_run, results: [] } });
      case "workset_admin": {
        // The 1.12.5 contract: audit lists user worksets; rename / delete_empty plan (check_only), dry run, apply with expect_plan.
        if (args.expect_model && args.expect_model !== "Test") return reply({ ok: true, result: { success: false, stage: "wrong_model", error: "wrong model" } });
        const ws = [
          { id: 1, name: "Shared Levels and Grids", elements: 30, byCategory: { Levels: 10, Grids: 20 }, owner: "", open: true, editable: false, isDefault: false, wrongWorkset: 0 },
          { id: 2, name: "TST_Columns", elements: 10, byCategory: { "Structural Columns": 10 }, owner: "", open: true, editable: false, wrongWorkset: 0 },
          { id: 3, name: "TST-COLUMNS", elements: 2, byCategory: { "Structural Columns": 2 }, owner: "jane", open: true, editable: false, wrongWorkset: 0 },
          { id: 4, name: "TST_Walls", elements: 5, byCategory: { Walls: 4, Floors: 1 }, owner: "", open: true, editable: true, isDefault: true, wrongWorkset: 2 },
          { id: 5, name: "TST_Empty", elements: 0, byCategory: {}, owner: "", open: true, editable: false, wrongWorkset: 0 },
          { id: 6, name: "Workset1", elements: 3, byCategory: { Walls: 3 }, owner: "", open: false, editable: false, wrongWorkset: 3 },
          { id: 7, name: "tst_beams", elements: 4, byCategory: { "Structural Framing": 4 }, owner: "", open: true, editable: false, wrongWorkset: 0 }];
        if (args.action === "audit") return reply({ ok: true, result: { model: "Test", aceVersion: SERVER_VERSION, workshared: true, worksets: ws } });
        let plan = [], blocked = [];
        if (args.action === "rename") {
          plan = (args.renames ?? []).map((r) => ({ id: `rename:${ws.find((w) => w.name === r.from)?.id ?? 0}`, kind: "rename", workset: r.from, from: r.from, to: r.to }));
          blocked = (args.renames ?? []).filter((r) => r.from === "TST-COLUMNS").map((r) => ({ what: `rename ${r.from}`, why: "borrowed by another user", elements: 2, sampleIds: [31, 32], holder: "jane", code: "borrowed" }));
        } else {
          const names = args.worksets ?? ws.filter((w) => w.elements === 0 && w.id !== 1).map((w) => w.name);
          for (const n of names) {
            const w = ws.find((x) => x.name === n);
            if (!w) { blocked.push({ what: n, why: "no such workset", code: "not-found" }); continue; }
            if (w.elements === 0) plan.push({ id: `delete:${w.id}`, kind: "delete", workset: n, elements: 0 });
            else if (args.move_to) plan.push({ id: `move-and-delete:${w.id}`, kind: "move-and-delete", workset: n, elements: w.elements, byCategory: w.byCategory, moveTo: args.move_to });
            else blocked.push({ what: n, why: `it holds ${w.elements} element(s)`, elements: w.elements, sampleIds: [41], code: "not-empty" });
          }
        }
        const planHash = `wh-${plan.map((p) => p.id + (p.to ?? "")).join(",")}`;
        if (blocked.length && !args.check_only) return reply({ ok: true, result: { success: false, stage: "blocked", model: "Test", aceVersion: SERVER_VERSION, blocked, error: "Nothing was changed: blocked" } });
        const notes = args.action === "delete_empty" && !args.worksets ? ["skipped TST_Locked: empty but borrowed by jane"] : args.dry_run ? [`would borrow ${plan.map((p) => p.workset).join(", ")}`] : [];
        if (args.check_only) return reply({ ok: true, result: { success: true, checkOnly: true, model: "Test", aceVersion: SERVER_VERSION, planHash, plan, blocked, notes } });
        if (args.expect_plan && (args.expect_plan !== planHash || (args.renames ?? []).some((r) => r.to === "TST_Drift")))
          return reply({ ok: true, result: { success: false, stage: "blocked", code: "plan-changed", model: "Test", error: "plan differs", blocked: [{ what: "the plan", why: "differs", code: "plan-changed" }] } });
        const fail = !args.dry_run && (args.renames ?? []).some((r) => r.to === "TST_Verifyfail");
        const done = { renamed: plan.filter((p) => p.kind === "rename").length, deleted: plan.filter((p) => p.kind !== "rename").length, moved: plan.filter((p) => p.kind === "move-and-delete").reduce((n, p) => n + p.elements, 0) };
        return reply({ ok: true, result: { success: !fail, model: "Test", aceVersion: SERVER_VERSION, planHash, plan, blocked: [], notes, rolledBack: !!args.dry_run || fail, echo: args,
          verify: fail ? { ok: false, checked: 7, mismatches: [{ what: "Walls", before: 7, after: 6 }] } : { ok: true, checked: 7, mismatches: [] },
          done: args.dry_run || fail ? { renamed: 0, deleted: 0, moved: 0 } : done, ...(args.dry_run || fail ? { wouldDo: done } : {}) } });
      }
      case "view_membership_fill": {
        // The 1.12.6 contract: the view's elements -> value_in, the rest -> value_out; a missing parameter needs a definition.
        if (args.expect_model && args.expect_model !== "Test") return reply({ ok: true, result: { success: false, stage: "wrong_model", error: "wrong model" } });
        const bound = args.parameter === "TST_BOUND_STATUS";
        const blocked = [];
        if (args.view === "TST Missing") blocked.push({ what: `view ${args.view}`, why: "no such view", code: "view-not-found" });
        if (!bound && !args.definition) blocked.push({ what: args.parameter, why: "not bound in the model and no definition was given", code: "no-definition" });
        if (!bound && args.definition && !(args.categories ?? []).length) blocked.push({ what: `definition ${args.parameter}`, why: "the definition names no model category to bind to", code: "no-definition" });
        if (args.view === "TST Sheet") blocked.push({ what: `view ${args.view}`, why: `${args.view} is not a model view (a sheet, schedule, legend or other non-model view)`, how: "Check the view name (list_views), then run again.", code: "view-not-found" });
        if (args.view === "TST Empty") blocked.push({ what: `view ${args.view}`, why: "the view shows none of these 10 elements; check the view name", how: "Check the view name (list_views), then run again.", code: "view-not-found" });
        const plan = [...(bound ? [] : [{ id: "bind", kind: "bind", count: 1, categories: args.categories }]), { id: "set-in", kind: "set-in", count: 6 }, { id: "set-out", kind: "set-out", count: 4 }];
        const planHash = `vh-${args.parameter}-${args.value_in}-${args.value_out}`;
        const base = { model: "Test", aceVersion: SERVER_VERSION, planHash, view: { name: args.view, id: 900, viewType: "ThreeD", sectionBox: true, phase: "New Construction", filters: ["TST filter"], is3d: true },
          parameter: { name: args.parameter, bound, guid: bound ? "g-bound" : null, storage: "String", instance: true }, modelNames: ["TST_Export_Status", "TST_Other"],
          counts: { processed: 10, in: 6, out: 4, unchanged: 0, skipped: 1 }, outside: { sectionBox: 3, hidden: 1, category: 0 }, outsideSamples: { sectionBox: [11, 12, 13] },
          skipped: [{ id: 77, reason: "in a group" }], impact: { schedules: ["TST schedule"], viewFilters: [] }, echo: args };
        if (blocked.length && !args.check_only) return reply({ ok: true, result: { ...base, success: false, stage: "blocked", blocked, error: "Nothing was changed: blocked" } });
        if (args.check_only) return reply({ ok: true, result: { ...base, success: true, checkOnly: true, plan, blocked } });
        if (args.expect_plan && args.expect_plan !== planHash) return reply({ ok: true, result: { success: false, stage: "blocked", code: "plan-changed", model: "Test", error: "plan differs", blocked: [{ what: "the plan", why: "differs", code: "plan-changed" }] } });
        const fail = !args.dry_run && args.value_out === "TST_FAIL";
        const done = { bound: !bound, setIn: 6, setOut: 4 };
        return reply({ ok: true, result: { ...base, success: !fail, plan, blocked: [], rolledBack: !!args.dry_run || fail,
          verify: fail ? { ok: false, checked: 10, mismatches: [{ id: 12, expected: "TST_FAIL", actual: "" }] } : { ok: true, checked: 10, mismatches: [] },
          done: args.dry_run || fail ? { bound: false, setIn: 0, setOut: 0 } : done, ...(args.dry_run || fail ? { wouldDo: done } : {}) } });
      }
      case "export_deliverables": {
        // The 1.12.6 contract: pre-flight (exporter, views, IFC setup, sheets, existing files), blocks, then files[].
        const blocked = [], existing = [], files = [];
        const nav = !args.out_dir.includes("NONAVIS");
        const at = (f) => path.join(args.out_dir, f);
        for (const f of ["nwc", "ifc", "dwfx"]) if (args[f] && fs.existsSync(at(args[f].file))) { existing.push(at(args[f].file)); if (!args.overwrite) blocked.push({ what: `${f} ${args[f].file}`, why: `${at(args[f].file)} exists`, code: "exists" }); }
        if (args.nwc && !nav) blocked.push({ what: "nwc", why: "the Navisworks exporter is not installed for this Revit", code: "no-exporter" });
        if (args.nwc && args.nwc.view !== "TST 3D Navis") blocked.push({ what: `nwc view ${args.nwc.view}`, why: `no view is called ${args.nwc.view}`, code: "view-not-found" });
        if (args.ifc?.setup && !args.ifc.config) blocked.push({ what: `ifc setup ${args.ifc.setup}`, why: "the add-in cannot read the IFC exporter's named setups", code: "setup-not-found" });
        const preflight = { navisworksExporter: nav, views: { nwc: args.nwc ? args.nwc.view === "TST 3D Navis" : undefined }, ifcSetup: args.ifc ? args.ifc.setup ?? null : null, sheets: args.dwfx ? (args.dwfx.sheets ?? []).length : 0, existing, outDir: args.out_dir };
        const base = { model: "Test", aceVersion: SERVER_VERSION, preflight, blocked, notes: [], echo: args };
        if (args.check_only) return reply({ ok: true, result: { ...base, success: true, checkOnly: true, files: [] } });
        if (blocked.length) return reply({ ok: true, result: { ...base, success: false, stage: "blocked", error: "Nothing was exported", files: [] } });
        fs.mkdirSync(args.out_dir, { recursive: true });
        for (const f of ["nwc", "ifc", "dwfx"]) if (args[f]) { fs.writeFileSync(at(args[f].file), "x".repeat(2048)); files.push({ format: f, file: at(args[f].file), bytes: 2048, seconds: 1.5, ...(f === "dwfx" ? { sheets: args.dwfx.sheets.length } : { elements: 120 }), warnings: f === "ifc" ? ["2 elements | not exported"] : [], view: args[f].view }); }
        return reply({ ok: true, result: { ...base, success: true, files } });
      }
      case "capture_method": {
        if (args.action === "start" && args.label === "TST busy") return reply({ ok: true, result: { success: false, error: "A capture is already running: TST joins." } });
        if (args.action === "start") return reply({ ok: true, result: { model: "Test", label: args.label, startedAt: "2026-10-20 10:00", journal: "C:/journals/journal.0001.txt", elements: 50, echo: args } });
        if (args.action === "status") return reply({ ok: true, result: { active: true, model: "Test", label: "TST joins", startedAt: "2026-10-20 10:00", elements: 50 } });
        const j = (id, cat, other, oc, cutter, what = "join-added") => ({ id, category: cat, type: "TST type", changes: [{ what, other, otherCategory: oc, cutter }] });
        return reply({ ok: true, result: { model: "Test", label: "TST joins", startedAt: "2026-10-20 10:00", stoppedAt: "2026-10-20 10:30", elements: 50, journal: "C:/journals/journal.0001.txt",
          archive: "C:/Users/x/AppData/Roaming/ACE-RevitMCP/captures/c1-TST joins.json",
          annotation: { dimensions: { before: 120, after: 119 }, spotDimensions: { before: 4, after: 4 }, tags: { before: 30, after: 30 } },
          diffs: [j(301, "Floors", 201, "Structural Columns", 301), j(201, "Structural Columns", 301, "Floors", 301), j(302, "Floors", 202, "Structural Columns", 302), j(303, "Floors", 203, "Structural Columns", 203),
            j(205, "Walls", 206, "Floors", 205, "join-order"),
            { id: 210, category: "Structural Framing", type: "TST BU", changes: [{ what: "extension", end: "end", before: 0, after: -83 }] },
            { id: 211, category: "Structural Framing", type: "TST BU", changes: [{ what: "moved", before: [0, 0, 0], after: [0, 50, 0] }] }],
          journalLines: ["' 0:< comment", 'Jrn.Command "Ribbon" , "Join Geometry , ID_JOIN_ELEMENTS_EDITOR"', 'Jrn.Command "Ribbon" , "Join Geometry , ID_JOIN_ELEMENTS_EDITOR"',
            'Jrn.Command "Internal" , " , ID_SWITCH_JOIN_ORDER"', 'Jrn.Command "Internal" , "ID_TST_UNKNOWN_CMD"', 'Jrn.MouseMove 0 , 1 , 2'] } });
      }
      case "open_model": {
        const model = path.basename(args.path).replace(/\.rvt$/i, "");
        const extra = model.includes("TRANS") ? { transmitted: true, transmittedCleared: !!args.clear_transmitted,
          unresolvedLinks: Array.from({ length: 7 }, (_, i) => ({ type: i ? "RVT" : "CAD", path: `C:/links/L${i}.rvt`, exists: i !== 1 })),
          dialogs: [{ id: "TaskDialog_Unresolved_References", message: "Unresolved references", answer: "Ignore and continue opening", why: "links are reported, not fixed while opening" },
            { id: "TaskDialog_Unknown", message: "Something unusual", answer: null, why: "no rule for this dialog" }] } : { transmitted: false, transmittedCleared: false, unresolvedLinks: [], dialogs: [] };
        return reply({ ok: true, result: { model, path: args.path, workshared: true, detached: args.detach !== "none", audited: args.audit, worksetsOpened: args.worksets, echo: args, ...extra,
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
        const one = (model) => ({ model, test: args.test ?? "all", run: { id: "run-1", at: "2026-10-20T10:00:00Z" }, counts: { clashes: 3 },
          rows: [...rows(model), ...(args.test === "TST_STORED" ? [{ ...rows(model)[0], clashKey: "k9", rechecked: false }] : [])], ...(args.exclude_materials?.length ? { filtered: { steel: 12 } } : {}) });
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
        return reply({ ok: true, result: { storeKey: "Test", tests: [{ test: args.test || "all", open: 4, byResponsible: { "MEP (HVAC)": 3, "ARC": 1 }, ...(args.test === "TST_STORED" ? { stored: { count: 1340, models: ["TST_Other"] } } : {}) }] } });
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
assert.deepEqual(tools, ["assign_worksets", "backup_model", "capture_method", "check_setup", "clash_resolve", "clash_view", "close_model", "coordination_report", "describe_category", "describe_family", "execute_revit_code", "export_deliverables", "find_elements", "get_activity_log", "get_element_details", "get_model_brief", "get_selection", "lessons", "list_types", "list_views", "list_warnings", "model_changes", "model_dashboard", "open_model", "open_view", "parameter_check", "parameter_fill", "pending_changes", "presentation_standard", "project_rules", "register_update", "report_issue", "revit_api_lookup", "revit_guide", "revit_status", "run_clash_test", "run_saved_script", "save_script", "saved_scripts", "select_elements", "set_clash_status", "set_parameters", "snapshots", "undo_last_claude_change", "vault", "view_image", "view_membership_fill", "working_mode"]);
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
  assert.ok(vs.order_source === "vault", "the vault's order is sent as such (the add-in cannot tell it from a request)");
  assert.match(vr.content.at(-1).text, /Join order \(one category priority list, strongest cuts; from the project vault \(.*clash-resolve\.json\)\): Structural Foundations > Structural Columns > Walls > Structural Framing > Floors > Stairs\./);
  const ro = await call("clash_resolve", { vault: cv, order: ["column", "OST_Walls"] });
  assert.ok(crs().at(-1).args.order.join() === "OST_StructuralColumns,OST_Walls" && !("order_source" in crs().at(-1).args), "the call's order wins");
  assert.match(ro.content.at(-1).text, /from the request\): Structural Columns > Walls\./);
  const rdflt = await call("clash_resolve", {});
  assert.match(rdflt.content.at(-1).text, /from ACE's default\): Structural Foundations > Structural Columns > Walls > Floors > Structural Framing > Stairs\./, "the default order, said");
  const { normaliseOrder } = await import("../lib/clashresolve.js");
  assert.deepEqual(normaliseOrder(["Structural Foundations", "Structural Columns", "Walls", "Floors", "Structural Framing"]), ["OST_StructuralFoundation", "OST_StructuralColumns", "OST_Walls", "OST_Floors", "OST_StructuralFraming"], "the documented example reads");
  // R2 / R3 (owner-approved): scope all-touching and trim to face from the vault; a call can switch trim off, never on.
  const tv = path.join(tmp, "tv", "ACE Vault");
  fs.mkdirSync(path.join(tv, "rules"), { recursive: true });
  const tvRules = (o) => fs.writeFileSync(path.join(tv, "rules", "clash-resolve.json"), JSON.stringify(o));
  tvRules({ order: ["foundation", "column", "wall", "slab", "beam"], scope: "all-touching", trim_to_face: { allowed: true, preview_required: true } });
  const tp = await call("clash_resolve", { vault: tv });
  const ta = crs().at(-1).args, tt = tp.content.at(-1).text;
  assert.ok(ta.scope === "all-touching" && ta.scope_source === "vault" && JSON.stringify(ta.trim_to_face) === JSON.stringify({ allowed: true, preview_required: true }) && ta.order.join() === "OST_StructuralFoundation,OST_StructuralColumns,OST_Walls,OST_Floors,OST_StructuralFraming");
  assert.match(tt, /Plan: 4 change\(s\): join 1, switch-join 1, cutback 1, cope 1\./);
  assert.match(tt, /By category pair:\n- Structural Columns x Structural Framing: join 1, cutback 1\n/, "the summary per category pair");
  assert.match(tt, /All touching: 120 candidate pair\(s\), 80 touching, 38 already right; join 30, switch join 12 \(Floors x Structural Columns 25\)\./);
  assert.match(tt, /Not touching, dropped: 40 pair\(s\) in scope that do not touch/);
  assert.match(tt, /Trims to a face \(1; a trim on a join runs only if the join does not clear\):\n- trim 211 \(Structural Framing\) end: 4833 -> 3822 mm \(trim 1011 mm\) to Structural Columns 302 near face \(only if join:201>301 does not clear\)/, "each trim, one by one");
  assert.match(tt, /Scope \(from the project vault \(.*\)\): every touching pair of the categories\. Trim to face: on \(allowed by the project vault \(.*clash-resolve\.json\)\)\./);
  await call("clash_resolve", { vault: tv, trim_to_face: false, scope: "clashing", selection: true });
  assert.ok(!("trim_to_face" in crs().at(-1).args) && crs().at(-1).args.scope === "clashing" && !crs().at(-1).args.scope_source && crs().at(-1).args.selection === true, "the call switches trim off and narrows the scope");
  const tdr = await call("clash_resolve", { vault: tv, action: "fix", dry_run: true, explanation: "TST trims", report_dir: path.join(tmp, "trimrun") });
  assert.ok(!tdr.isError && /Changes: would have been join 1, switch-join 1, cutback 1, cope 1; rolled back/.test(tdr.content.at(-1).text));
  const tap = await call("clash_resolve", { vault: tv, action: "fix", explanation: "TST trims", report_dir: path.join(tmp, "trimrun") });
  assert.match(tap.content.at(-1).text, /Changes: join 1, switch-join 1, cutback 1, cope 1, trim 1 done\./, "the identical apply after the dry run");
  const tev = fs.readdirSync(path.join(tmp, "trimrun")).find((f) => f.startsWith("evidence-clash-"));
  assert.match(fs.readFileSync(path.join(tmp, "trimrun", tev), "utf8"), /## Trims to a face \(planned; a join's trim runs only if the join does not clear\)\n\n- trim 211 \(Structural Framing\) end: 4833 -> 3822 mm \(trim 1011 mm\)[\s\S]*## Trims made\n\n- 1\n[\s\S]*## Not touching, dropped\n\n- 40 pair\(s\)/);
  tvRules({ order: ["foundation"], trim_to_face: { allowed: false } });
  const n4 = crs().length;
  assert.ok(/trim_to_face is not allowed for this project: the vault's rules\/clash-resolve\.json has trim_to_face\.allowed: false/.test((await call("clash_resolve", { vault: tv, trim_to_face: true })).content[0].text) && crs().length === n4, "a call never switches trim on");
  assert.ok(!("trim_to_face" in (await call("clash_resolve", { vault: tv }), crs().at(-1).args)), "allowed: false sends no trim");
  tvRules({ scope: "everything" });
  assert.match((await call("clash_resolve", { vault: tv })).content[0].text, /scope: "everything" is not a scope ACE knows/);
  tvRules({ trim_to_face: { allowed: true, depth: 3 } });
  assert.match((await call("clash_resolve", { vault: tv })).content[0].text, /trim_to_face: "depth" is not a setting ACE knows/);
  // Office rules (clash-resolve.json beside config.json) under the project vault's, project first key by key (D3).
  const officeFile = path.join(tmp, "clash-resolve.json");
  const noVault = path.join(tmp, "novault");
  fs.writeFileSync(officeFile, JSON.stringify({ order: ["foundation", "column", "wall", "slab", "beam"], scope: "all-touching", trim_to_face: { allowed: false, max_mm: 50 } }));
  const oo = await call("clash_resolve", { vault: noVault });
  assert.ok(crs().at(-1).args.order.join() === "OST_StructuralFoundation,OST_StructuralColumns,OST_Walls,OST_Floors,OST_StructuralFraming" && crs().at(-1).args.scope === "all-touching" && !("trim_to_face" in crs().at(-1).args), "office only");
  assert.match(oo.content.at(-1).text, /Office rules: .*clash-resolve\.json\.\nJoin order \(one category priority list, strongest cuts; from the office file \(.*clash-resolve\.json\)\)[\s\S]*Scope \(from the office file/);
  tvRules({ scope: "clashing", trim_to_face: { allowed: true } });
  const po = await call("clash_resolve", { vault: tv });
  const pa = crs().at(-1).args;
  assert.ok(pa.scope === "clashing" && pa.order[0] === "OST_StructuralFoundation" && JSON.stringify(pa.trim_to_face) === JSON.stringify({ allowed: true, preview_required: true, max_mm: 50 }), "project over office per key; trim per sub-key");
  assert.match(po.content.at(-1).text, /from the office file \(.*\)\): Structural Foundations[\s\S]*Scope \(from the project vault \(.*\)\): only the clashing pairs\. Trim to face: on \(allowed by the project vault \(.*\), at most 50 mm\)\./);
  const pr = JSON.parse((await call("project_rules", { section: "clash-resolve", vault: tv })).content[0].text);
  assert.ok(pr.from.order === "office" && pr.from.scope === "project" && pr.rules.trim_to_face.max_mm === 50 && pr.files.office === officeFile, "project_rules shows the merged rules and their sources");
  fs.writeFileSync(officeFile, JSON.stringify({ trim_to_face: { allowed: true } }));
  tvRules({ trim_to_face: { allowed: false } });
  const n5 = crs().length;
  assert.ok(/the vault's rules\/clash-resolve\.json has trim_to_face\.allowed: false/.test((await call("clash_resolve", { vault: tv, trim_to_face: true })).content[0].text) && crs().length === n5, "the merged allowed: false refuses a request to trim");
  fs.writeFileSync(officeFile, JSON.stringify({ trim_to_face: { allowed: false } }));
  assert.match((await call("clash_resolve", { vault: noVault, trim_to_face: true })).content[0].text, /the office file .*clash-resolve\.json has trim_to_face\.allowed: false/);
  fs.rmSync(officeFile);
  // The clash store key (the central's name) names the run records, not the local file's title.
  await call("clash_resolve", { action: "fix", keys: ["TST_STORE"], dry_run: true, explanation: "store key", report_dir: path.join(tmp, "storekey") });
  assert.ok(fs.readdirSync(path.join(tmp, "storekey")).some((f) => /^clash-run-TST_Central-.+\.json$/.test(f)), "run record under the store key");
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
  assert.ok(oa.args.clear_transmitted === false && oa.args.save_as_central_ok === false, "both new flags default false");
  assert.match(om.content.at(-1).text, /^Opened P1-ST-0001 \(audited \/ worksets: all\); closed Test\. Open now: P1-ST-0001\.\nFlagged transmitted: no\.$/);
  const n0 = seen.length;
  const ct = await call("open_model", { path: "C:/Projects/P1/P1-TRANS.rvt", clear_transmitted: true });
  assert.ok(ct.isError && /writes into the file on disk/.test(ct.content[0].text) && /working copy/.test(ct.content[0].text) && seen.length === n0, "clear_transmitted needs the user's OK, before Revit is asked");
  const tr = (await call("open_model", { path: "C:/Projects/P1/P1-TRANS.rvt", clear_transmitted: true, save_as_central_ok: true, confirm: true })).content.at(-1).text;
  assert.ok(seen.at(-1).args.clear_transmitted === true && seen.at(-1).args.save_as_central_ok === true);
  assert.match(tr, /WARNING: 1 dialog\(s\) needed a person \(not answered\): TaskDialog_Unknown: Something unusual \(no rule for this dialog\)/);
  assert.match(tr, /Dialogs answered:\n- TaskDialog_Unresolved_References: Unresolved references -> Ignore and continue opening \(links are reported, not fixed while opening\)/);
  assert.match(tr, /7 unresolved link\(s\): CAD C:\/links\/L0\.rvt; RVT C:\/links\/L1\.rvt \(file not found\); RVT C:\/links\/L2\.rvt; RVT C:\/links\/L3\.rvt; RVT C:\/links\/L4\.rvt; \.\.\. 2 more\./);
  assert.match(tr, /Flagged transmitted: yes; the flag was cleared in the file on disk\./);
  assert.match((await call("open_model", { path: "C:/Projects/P1/P1-TRANS.rvt" })).content.at(-1).text, /Flagged transmitted: yes; the flag was kept \(clear_transmitted not asked\)\./);
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
// --- worksets: audit (read-only table and files), rename and delete_empty through the gate ---
{
  const wa = () => seen.filter((x) => x.command === "workset_admin");
  const aDir = path.join(tmp, "ws-audit");
  const au = await call("assign_worksets", { action: "audit", out_dir: aDir });
  assert.ok(!au.isError && wa().at(-1).args.action === "audit" && wa().at(-1).args.check_only === true);
  const at = au.content.at(-1).text;
  assert.match(at, /^WORKSET AUDIT Test \(read-only, nothing changed\): 7 user workset\(s\); 1 empty, 3 breaking the naming rule, 1 near-duplicate group\(s\), 2 with elements on the wrong workset\./);
  assert.match(at, /Naming rule: pattern \^TST_\[A-Za-z\]\+\$; 7 allowed name\(s\); 1 rename\(s\)\./);
  assert.match(at, /\nShared Levels and Grids \| 30 \| Grids 20, Levels 10 \| - \| yes \| ok \(protected\)/, "Shared Levels and Grids is exempt");
  assert.match(at, /\nTST-COLUMNS \| 2 \| Structural Columns 2 \| jane \| yes \| naming, near-duplicate -> TST_Columns\?/);
  assert.match(at, /\nTST_Empty \| 0 \| - \| - \| yes \| empty/);
  assert.match(at, /\nWorkset1 \| 3 \| Walls 3 \| - \| no \| naming, wrong-workset -> TST_Walls\?/);
  assert.match(at, /\nNear-duplicates: TST_Columns \/ TST-COLUMNS\.\n/);
  assert.match(at, /Rename suggestions \(none applied\): tst_beams -> TST_Beams \(breaks the naming rule\)\./, "a target that exists already is not suggested");
  const af = [...at.matchAll(/^- (.+)$/gm)].map((m) => m[1].trim());
  assert.deepEqual(af.map((f) => path.extname(f)), [".html", ".csv"]);
  const ah = fs.readFileSync(af[0], "utf8");
  assert.ok(/<h1>Workset Audit<\/h1>/.test(ah) && /<span class="flag">empty<\/span>/.test(ah) && ah.includes("#EF3340") && !/<script/i.test(ah));
  assert.match(fs.readFileSync(af[1], "utf8"), /\r\nTST-COLUMNS,2,Structural Columns 2,jane,yes,no,"naming, near-duplicate",TST_Columns,TST_Columns,0,3\r\n/);

  // rename: explicit; gate before Revit; a borrowed workset stops before the dry run.
  const rx = { action: "rename", renames: [{ from: "tst_beams", to: "TST_Beams" }], explanation: "Rename to the BEP names" };
  const n0 = wa().length;
  assert.ok((await call("assign_worksets", rx)).isError && wa().length === n0, "no apply without its dry run, before Revit is asked");
  const n1 = wa().length;
  const bad = await call("assign_worksets", { ...rx, renames: [{ from: "Shared Levels and Grids", to: "TST_Grids" }, { from: "Workset1", to: "Walls Misc" }], dry_run: true });
  assert.ok(bad.isError && /Shared Levels and Grids is never renamed/.test(bad.content[0].text) && /breaks the project's workset naming rule/.test(bad.content[0].text) && wa().length === n1, "protected and off-rule names refused before Revit is asked");
  const bor = await call("assign_worksets", { ...rx, renames: [{ from: "TST-COLUMNS", to: "TST_Old" }], dry_run: true });
  assert.ok(bor.isError && /stopped before the dry run/.test(bor.content[0].text) && /held by jane\. How to clear: ask the holder to synchronise/.test(bor.content[0].text));
  assert.ok(wa().slice(n1).every((x) => x.args.check_only === true), "only the read-only check ran");
  // check_only: the plan read-only, no explanation, no dry run, nothing remembered as a preview.
  const n2 = wa().length;
  const rco = await call("assign_worksets", { action: "rename", renames: rx.renames, check_only: true });
  assert.ok(!rco.isError && wa().length === n2 + 1 && wa().at(-1).args.check_only === true && /WORKSETS RENAME in Test \(check, read-only, nothing changed\)\.\nPlan: 1 step\(s\):\n- rename tst_beams -> TST_Beams/.test(rco.content.at(-1).text));
  assert.match((await call("assign_worksets", rx)).content[0].text, /not been previewed/, "a check is not a preview");
  const rd = await call("assign_worksets", { ...rx, dry_run: true });
  assert.match(rd.content.at(-1).text, /Notes:\n- would borrow tst_beams/, "the add-in's notes are printed");
  const [rc, rdd] = wa().slice(-2).map((x) => x.args);
  assert.ok(rc.check_only && rdd.dry_run === true && rdd.expect_model === "Test" && rdd.expect_version === SERVER_VERSION && !("expect_plan" in rdd));
  assert.match(rd.content.at(-1).text, /WORKSETS RENAME in Test \(dry run\)\.\nPlan: 1 step\(s\):\n- rename tst_beams -> TST_Beams\nNotes:\n- would borrow tst_beams\nSelf-check: ok \(element count per category the same before and after, 7 checked; only the workset changed\)\.\nChanges: would have been rename 1; rolled back, nothing changed\./);
  const ra = await call("assign_worksets", rx);
  assert.ok(!ra.isError && wa().at(-1).args.dry_run === false && wa().at(-1).args.expect_plan === "wh-rename:7TST_Beams" && wa().at(-1).args.expect_model === "Test");
  assert.match(ra.content.at(-1).text, /\(applied as ONE undo step\)[\s\S]*Changes: renamed 1 done\.[\s\S]*Run record: .*workset-admin-run-Test-/);
  assert.match((await call("assign_worksets", rx)).content[0].text, /not been previewed/, "a preview is used once");
  // Renames from the naming rule when none are given; the same suggestion again on the apply.
  await call("assign_worksets", { action: "rename", explanation: "Rename per the rule", dry_run: true });
  assert.deepEqual(wa().at(-1).args.renames, [{ from: "tst_beams", to: "TST_Beams" }]);
  assert.match((await call("assign_worksets", { action: "rename", explanation: "Rename per the rule" })).content.at(-1).text, /Renames from the naming rule's suggestions\./);
  // A failed self-check rolls back; a changed plan is refused plainly.
  const vf = { action: "rename", renames: [{ from: "tst_beams", to: "TST_Verifyfail" }], explanation: "x" };
  await call("assign_worksets", { ...vf, dry_run: true });
  const vr = await call("assign_worksets", vf);
  assert.ok(vr.isError && /NOT applied: rolled back/.test(vr.content.at(-1).text) && /Self-check FAILED: Walls: 7 -> 6; rolled back, nothing changed\./.test(vr.content.at(-1).text) && /Changes: would have been rename 1; rolled back, nothing changed\./.test(vr.content.at(-1).text));
  const dr = { action: "rename", renames: [{ from: "tst_beams", to: "TST_Drift" }], explanation: "x" };
  await call("assign_worksets", { ...dr, dry_run: true });
  assert.match((await call("assign_worksets", dr)).content[0].text, /the model changed since the preview: run the dry run again/);

  // delete_empty: only empty worksets by default; a non-empty one needs move_to and its name, never implicit.
  const de = await call("assign_worksets", { action: "delete_empty", dry_run: true, explanation: "Delete empty worksets" });
  assert.match(de.content.at(-1).text, /Plan: 1 step\(s\):\n- delete TST_Empty \(0 elements\)/);
  assert.match((await call("assign_worksets", { action: "delete_empty", check_only: true })).content.at(-1).text, /Notes:\n- skipped TST_Locked: empty but borrowed by jane/);
  // A suggested target that exists in another case is a merge, not a rename; a case-only fix of the name itself stays.
  const W = await import("../lib/worksets.js");
  const sg = W.audit({ worksets: [{ name: "tst-old", elements: 1 }, { name: "TST_OLD", elements: 1 }] }, W.namingOf({ naming: { allowed: ["TST_Old"] } })).suggestions;
  assert.deepEqual(sg, [{ from: "TST_OLD", to: "TST_Old", why: "breaks the naming rule" }]);
  const ne = await call("assign_worksets", { action: "delete_empty", worksets: ["TST_Walls"], dry_run: true, explanation: "x" });
  assert.ok(ne.isError && /TST_Walls: it holds 5 element\(s\).*How to clear: name a target with move_to/.test(ne.content[0].text));
  assert.ok((await call("assign_worksets", { action: "delete_empty", move_to: "TST_Columns", dry_run: true, explanation: "x" })).isError, "move_to without named worksets is refused");
  const mv = await call("assign_worksets", { action: "delete_empty", worksets: ["TST_Walls"], move_to: "TST_Columns", dry_run: true, explanation: "Empty and delete" });
  assert.match(mv.content.at(-1).text, /- move 5 element\(s\) of TST_Walls to TST_Columns \(Walls 4, Floors 1\), then delete TST_Walls/);
  assert.ok((await call("assign_worksets", { action: "delete_empty", worksets: ["Shared Levels and Grids"], dry_run: true, explanation: "x" })).isError);
}
// --- capture_method: start, status, stop -> case record (actions by pair, journal commands, plan comparison) ---
{
  assert.ok((await call("capture_method", { action: "start" })).isError, "a capture needs a label");
  const busy = await call("capture_method", { action: "start", label: "TST busy" });
  assert.ok(busy.isError && /already running/.test(busy.content[0].text) && !/started/.test(busy.content[0].text), "a refused start never reads as started");
  assert.match((await call("capture_method", { action: "start", label: "TST joins", categories: ["Floors", "Structural Columns"] })).content.at(-1).text, /Capture "TST joins" started in Test: 50 element\(s\) recorded\./);
  assert.deepEqual(seen.at(-1).args, { action: "start", label: "TST joins", categories: ["Floors", "Structural Columns"] });
  assert.match((await call("capture_method", { action: "status" })).content.at(-1).text, /running in Test since/);
  const cDir = path.join(tmp, "cases");
  const st = await call("capture_method", { action: "stop", out_dir: cDir, plan_dir: path.join(tmp, "clash") });
  const t = st.content.at(-1).text;
  assert.match(t, /Capture "TST joins" stopped in Test: 7 changed element\(s\)\./);
  assert.match(t, /- Floors x Structural Columns: join added: 3; Floors cut Structural Columns in 2 of 3; exceptions: 303 x 203 \(Structural Columns cuts\)/, "a join counted once per pair, with its exception");
  assert.match(t, /- Floors x Walls: join order switched: 1; Walls cut Floors in 1 of 1/);
  assert.match(t, /- Structural Framing: end extension changed \(end\): 1/);
  assert.match(t, /- Structural Framing: moved: 1/);
  assert.match(t, /Revit commands: Join Geometry 2, Switch Join Order 1, ID_TST_UNKNOWN_CMD 1\./, "readable names; unknown ids kept raw");
  assert.match(t, /Add-in archive of the raw diff: C:\/Users\/x\/AppData\/Roaming\/ACE-RevitMCP\/captures\/c1-TST joins\.json\./);
  assert.match(t, /Compared with the clash_resolve plan \(.*clash-run-Test-.*\.json\): differs 2, agree 1, planned, not done 1, not planned 1\./);
  const files = t.match(/Case: (.+)\n(.+)/).slice(1).map((f) => f.trim());
  assert.ok(files.every((f) => path.dirname(f) === cDir) && /\d{4}-\d\d-\d\d-tst-joins\.json$/.test(files[0]) && files[1].endsWith(".md"));
  const md = fs.readFileSync(files[1], "utf8");
  assert.match(md, /\| 201 \| 301 \| 201 cuts 301 \(join\) \| join added, 301 cuts \| differs \|/);
  assert.match(md, /\| 205 \| 206 \| 205 cuts 206 \(switch-join\) \| join order switched, 205 cuts \| agree \|/);
  assert.match(md, /- In order: Join Geometry x2 > Switch Join Order > ID_TST_UNKNOWN_CMD/);
  assert.match(md, /- Annotation: dimensions 120 -> 119\./, "the case says the annotation lost");
  assert.match(md, /## Proposed rules \(need owner confirmation\)\n\nNothing below is applied\.[\s\S]*- Floors before Structural Columns in the join order \(Floors x Structural Columns: Floors cut Structural Columns in 2 of 3 pair\(s\); 1 exception\(s\)\)\n- Walls before Floors in the join order \(Floors x Walls: Walls cut Floors in 1 of 1 pair\(s\)\)\n\nWould update office method structural-interference-solving \(v1\) once confirmed \(revit_guide methods\/structural-interference-solving\)\.\n$/, "ends with proposed rules, never applied");
  assert.equal(JSON.parse(fs.readFileSync(files[0], "utf8")).comparison.counts.agree, 1);
}
// --- view_membership_fill: check, dry run, identical apply; the definition only from the vault's shared parameter files ---
{
  const vDir = path.join(tmp, "vfill");
  fs.mkdirSync(path.join(vDir, "ACE Vault", "rules"), { recursive: true });
  const sp = path.join(vDir, "TST project parameters.txt");
  fs.writeFileSync(sp, "*GROUP\tID\tNAME\nGROUP\t1\tTST\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\nPARAM\taaaaaaaa-0000-0000-0000-000000000001\tTST_EXPORT_STATUS\tTEXT\t\t1\n");
  fs.writeFileSync(path.join(vDir, "ACE Vault", "rules", "ifc-export.json"), JSON.stringify({ propertySets: [{ name: "TST_Pset", properties: ["TST_EXPORT_STATUS"] }] }));
  const vm = () => seen.filter((x) => x.command === "view_membership_fill");
  const base = { view: "TST Navis", parameter: "TST_EXPORT_STATUS", value_in: "TST_IN", value_out: "TST_OUT", categories: ["Walls"], project_parameters: sp, vault: vDir, report_dir: path.join(tmp, "vfill-out") };
  assert.match((await call("view_membership_fill", { ...base, value_out: "TST_IN", check_only: true })).content[0].text, /value_in and value_out are the same/);
  const ck = await call("view_membership_fill", { ...base, check_only: true });
  const ct = ck.content.at(-1).text;
  assert.ok(!ck.isError && vm().at(-1).args.check_only === true && vm().at(-1).args.definition.guid === "aaaaaaaa-0000-0000-0000-000000000001", "the definition comes from the vault's file");
  assert.match(ct, /^VIEW MEMBERSHIP FILL in Test \(check, read-only, nothing changed\): TST_EXPORT_STATUS = "TST_IN" for what view "TST Navis" shows, "TST_OUT" for the rest\./);
  assert.match(ct, /Why elements are out \(first reason found\):\n- outside the section box: 3 \(e\.g\. 11, 12, 13\)\n- hidden in the view: 1/, "the reasons per kind");
  assert.match(ct, /Plan: bind the parameter \(1\), set value_in \(6\), set value_out \(4\)\./);
  assert.match(ct, /Near-duplicate names in the model: TST_Export_Status/);
  assert.match(ct, /Definition from the vault: TST project parameters\.txt \(GUID aaaaaaaa-0000-0000-0000-000000000001, TEXT\)\./);
  assert.match(ct, /Impact: "TST_EXPORT_STATUS" is named in .*ifc-export\.json \(IFC export settings\)/, "the IFC settings that name it");
  assert.match(ct, /In the model it is used by: schedules TST schedule\./);
  // A misspelled name: flagged against the vault, no definition, so the add-in blocks a missing parameter.
  const mis = await call("view_membership_fill", { ...base, parameter: "TST_EXPORT_STAUS", dry_run: true, explanation: "x" });
  assert.ok(mis.isError && !("definition" in vm().at(-1).args && vm().at(-1).args.definition) && vm().at(-1).args.check_only === true, "stopped at the check, before the dry run");
  const mt = mis.content.map((c) => c.text).join("\n");
  assert.match(mt, /TST_EXPORT_STAUS: not bound in the model and no definition was given\. How to clear: add the parameter to the project shared parameter file in the vault/, "blocked, never invented");
  assert.match(mt, /Likely spelling issue: "TST_EXPORT_STAUS" is in no shared parameter file of the vault; close names there: TST_EXPORT_STATUS\./, "a near name in the vault");
  const sp2 = await call("view_membership_fill", { ...base, parameter: "TST_Export_Status", check_only: true });
  assert.match(sp2.content.at(-1).text, /Likely spelling issue: the vault has "TST_EXPORT_STATUS", not "TST_Export_Status"/, "a spelling against the vault");
  const mp = path.join(vDir, "TST mapping.json");
  fs.writeFileSync(mp, JSON.stringify({ nameAliases: { status: "approved", spellingRule: "case and spaces follow the shared parameter file" } }));
  const sr = await call("view_membership_fill", { ...base, parameter: "TST_Export_Status", mapping: mp, check_only: true });
  assert.ok(vm().at(-1).args.parameter === "TST_EXPORT_STATUS" && /Spelling: the vault spells it "TST_EXPORT_STATUS"; the approved spelling rule resolves "TST_Export_Status" to it/.test(sr.content.at(-1).text), "an approved spelling rule writes the vault's spelling");
  // Gate: no apply without the dry run; dry run; identical apply with expect_plan; a failed self-check rolls back.
  const ex = { ...base, explanation: "Mark what the Navisworks view exports" };
  const n0 = vm().length;
  assert.ok((await call("view_membership_fill", ex)).isError && vm().length === n0, "no apply without its dry run, before Revit is asked");
  const dr = await call("view_membership_fill", { ...ex, dry_run: true });
  assert.ok(!dr.isError && vm().at(-1).args.dry_run === true && vm().at(-1).args.expect_model === "Test" && vm().at(-1).args.expect_version === SERVER_VERSION);
  assert.match(dr.content.at(-1).text, /Self-check: ok \(10 element\(s\) read back; in \+ out = processed\)\.\nChanges: would have been bound 1, set in 6, set out 4; rolled back, nothing changed\.[\s\S]*PREVIEW ONLY/);
  const ap = await call("view_membership_fill", ex);
  assert.ok(!ap.isError && vm().at(-1).args.dry_run === false && vm().at(-1).args.expect_plan === "vh-TST_EXPORT_STATUS-TST_IN-TST_OUT");
  assert.match(ap.content.at(-1).text, /\(applied as ONE undo step\)[\s\S]*Changes: bound 1, set in 6, set out 4 done\.[\s\S]*Run record: .*view-membership-fill-run-Test-/);
  assert.match((await call("view_membership_fill", ex)).content[0].text, /not been previewed/, "a preview is used once");
  const fx = { ...ex, value_out: "TST_FAIL" };
  await call("view_membership_fill", { ...fx, dry_run: true });
  const fr = await call("view_membership_fill", fx);
  assert.ok(fr.isError && /NOT applied: rolled back/.test(fr.content.at(-1).text) && /Self-check FAILED: #12: expected "TST_FAIL", found ""; rolled back, nothing changed\./.test(fr.content.at(-1).text));
  assert.match((await call("view_membership_fill", { ...base, include_links: true, check_only: true })).content[0].text, /include_links is not supported/);
  const nc = await call("view_membership_fill", { ...base, categories: undefined, dry_run: true, explanation: "x" });
  const nct = nc.content.map((c) => c.text).join("\n");
  assert.ok(nc.isError && vm().at(-1).args.check_only === true && /categories for TST_EXPORT_STATUS: TST_EXPORT_STATUS is not bound in the model, and binding it needs categories: the shared parameter file holds no categories\. How to clear: give categories/.test(nct) && !/names no model category/.test(nct), "binding without categories: said plainly, before the dry run");
  for (const [v, why] of [["TST Sheet", /view TST Sheet: TST Sheet is not a model view \(a sheet, schedule, legend or other non-model view\)\. How to clear: Check the view name/], ["TST Empty", /view TST Empty: the view shows none of these 10 elements; check the view name\. How to clear: Check the view name/]]) {
    const vb = await call("view_membership_fill", { ...base, view: v, check_only: true });
    assert.match(vb.content.at(-1).text, why, `the add-in's view block is printed: ${v}`);
  }
  const nv = await call("view_membership_fill", { ...base, view: "TST Missing", dry_run: true, explanation: "x" });
  assert.match(nv.content[0].text, /view TST Missing: no such view\. How to clear: check the view name/);
}
// --- export_deliverables: names from the vault's naming rule, IFC settings from the vault, blocks reported, a table ---
{
  const eDir = path.join(tmp, "exp");
  const vRules = path.join(eDir, "ACE Vault", "rules");
  fs.mkdirSync(path.join(vRules, "ifc"), { recursive: true });
  fs.writeFileSync(path.join(vRules, "export.json"), JSON.stringify({ nameTemplate: "TST_{model}_{format}", ifc: { setups: { "TST setup": "ifc/tst-setup.json" } } }));
  fs.writeFileSync(path.join(vRules, "ifc", "tst-setup.json"), JSON.stringify({ Name: "TST setup", IFCVersion: 21, ExportBaseQuantities: true, SpaceBoundaries: 1, TST_Option: "on" }));
  const ed = () => seen.filter((x) => x.command === "export_deliverables");
  const out = path.join(eDir, "out");
  const eb = { out_dir: out, vault: eDir, nwc: { view: "TST 3D Navis" }, ifc: { setup: "TST setup" }, dwfx: { sheets: ["TST-01", "TST-02"] } };
  assert.match((await call("export_deliverables", { out_dir: out })).content[0].text, /Give at least one of nwc, ifc or dwfx/);
  const n0 = ed().length;
  const bad = await call("export_deliverables", { ...eb, ifc: { setup: "TST unknown" } });
  assert.ok(bad.isError && ed().length === n0 && /no IFC setup "TST unknown" in the vault/.test(bad.content[0].text), "an unknown setup is blocked before Revit is asked");
  const ck = await call("export_deliverables", { ...eb, check_only: true });
  const ca = ed().at(-1).args;
  assert.ok(!ck.isError && ca.check_only === true && ca.nwc.file === "TST_Test_nwc.nwc" && ca.ifc.file === "TST_Test_ifc.ifc" && ca.dwfx.file === "TST_Test_dwfx.dwfx", "names from the vault's naming rule");
  assert.deepEqual(ca.ifc.config, { FileVersion: 21, ExportBaseQuantities: true, SpaceBoundaryLevel: 1, TST_Option: "on" }, "the setup's file, exporter keys read as the add-in's");
  assert.ok(ca.ifc.setup === "TST setup" && ca.overwrite === false);
  assert.match(ck.content.at(-1).text, /\(pre-flight only, nothing written\)[\s\S]*Pre-flight: Navisworks exporter installed; IFC setup TST setup; 2 sheet\(s\)\.[\s\S]*IFC settings: .*tst-setup\.json \(exporter keys read as IFCVersion -> FileVersion, SpaceBoundaries -> SpaceBoundaryLevel\)[\s\S]*Ready: the same call without check_only exports\./);
  const ex = await call("export_deliverables", eb);
  const et = ex.content.at(-1).text;
  assert.ok(!ex.isError && fs.existsSync(path.join(out, "TST_Test_nwc.nwc")));
  assert.match(et, /\| file \| size \| time \| count \| warnings \|\n\|---\|---\|---\|---\|---\|\n\| TST_Test_nwc\.nwc \| 2 KB \| 1\.5 s \| 120 element\(s\) \| - \|\n\| TST_Test_ifc\.ifc \| 2 KB \| 1\.5 s \| 120 element\(s\) \| 2 elements \/ not exported \|\n\| TST_Test_dwfx\.dwfx \| 2 KB \| 1\.5 s \| 2 sheet\(s\) \| - \|/, "the files table");
  assert.match(et, /Run record: .*export-run-Test-/);
  // Existing files and other blocks: reported, nothing exported; overwrite replaces.
  const again = await call("export_deliverables", { ...eb, nwc: { view: "TST Nope" } });
  const at = again.content.at(-1).text;
  assert.ok(again.isError && /Blocked \(nothing exported\):/.test(at) && /TST_Test_nwc\.nwc exists\. How to clear: pass overwrite: true/.test(at) && /nwc view TST Nope: no view is called TST Nope\. How to clear: check the view name/.test(at));
  const nn = await call("export_deliverables", { out_dir: path.join(eDir, "NONAVIS"), nwc: { view: "TST 3D Navis", file: "TST given" } });
  assert.ok(nn.isError && ed().at(-1).args.nwc.file === "TST given.nwc" && /Navisworks exporter NOT installed/.test(nn.content.at(-1).text) && /How to clear: install the Navisworks NWC Export Utility/.test(nn.content.at(-1).text));
  assert.ok(!(await call("export_deliverables", { ...eb, overwrite: true })).isError, "overwrite replaces");
  // Without a naming rule: the model name.
  await call("export_deliverables", { out_dir: path.join(eDir, "plain"), vault: path.join(eDir, "none"), ifc: { config: path.join(vRules, "ifc", "tst-setup.json") }, check_only: true });
  assert.ok(ed().at(-1).args.ifc.file === "Test.ifc" && ed().at(-1).args.ifc.setup === "tst-setup");
}
// --- 1.12.8: results this run did not re-check are counted on their own line, out of the open totals ---
{
  const st = await call("run_clash_test", { test: "TST_STORED" });
  assert.match(st.content.at(-1).text, /^Open in this run: 4\.\nStored, not re-checked in this run: 1340 \(TST_Other\); not in the open totals\.$/);
  assert.equal((await call("run_clash_test", { test: "all" })).content.length, 1, "nothing extra when every result was re-checked");
  const ir = await call("coordination_report", { interference: true, test: "TST_STORED", out_dir: path.join(tmp, "stored-int") });
  assert.match(ir.content.at(-1).text, /Interference report Test \(TST_STORED\): 3 clash\(es\) in 2 issue\(s\)[\s\S]*Stored, not re-checked in this run: 1; not in these totals\./, "the stored row stays out of the report totals");
}
// --- 1.12.8 capture: area / volume / annotation grouped; the plan's real cutter, agreement per category pair ---
{
  const Cap = await import("../lib/capture.js");
  const area = (id, b, a) => ({ id, category: "Floors", type: "TST slab", changes: [{ what: "area", before: b, after: a }] });
  const acts = Cap.actionsOf([area(501, 100, 94.5), area(502, 50, 50.2), area(503, 20, 19), { id: 601, category: "Walls", changes: [{ what: "volume", before: 3, after: 2.5 }] },
    { id: 701, category: "Floors", changes: [{ what: "annotation", before: { dimensions: 3, tags: 1 }, after: { dimensions: 2, tags: 1 } }] }]);
  const lines = acts.map(Cap.actionLine);
  assert.ok(lines.includes("Floors: area changed on 3 (sketch edits likely), total -6.300 m2 (e.g. 501, 502, 503)"), lines.join(" | "));
  assert.ok(lines.includes("Walls: volume changed on 1 (ends or profile changed), total -0.500 m3 (e.g. 601)"));
  assert.ok(lines.includes("Floors: annotation referencing them changed on 1 (dimensions -1) (e.g. 701)"));
  assert.equal(Cap.annotationLine({ dimensions: { before: 120, after: 119 }, tags: { before: 3, after: 3 } }), "Annotation: dimensions 120 -> 119.");
  // A plan step that names its cutter: the person's direction is compared with it, not with "a".
  const plan = [{ kind: "join", a: 11, b: 12, cutter: 12, cut: 11, cutterCategory: "Structural Columns", cutCategory: "Floors" },
    { kind: "join", a: 13, b: 14, aCategory: "Floors", bCategory: "Structural Columns" }];
  const j = (id, cat, other, oc, cutter) => ({ id, category: cat, changes: [{ what: "join-added", other, otherCategory: oc, cutter }] });
  const cmp = Cap.compareWithPlan(plan, [j(12, "Structural Columns", 11, "Floors", 12), j(14, "Structural Columns", 13, "Floors", 14)]);
  assert.deepEqual(cmp.rows.map((r) => [r.plan, r.verdict]), [["12 cuts 11 (join)", "agree"], ["13 cuts 14 (join)", "differs"]]);
  assert.deepEqual(cmp.byCategoryPair, { "Floors x Structural Columns": { agree: 1, differs: 1 } });
  assert.equal(Cap.pairAgreement(cmp.byCategoryPair), "Floors x Structural Columns: agree 1, differs 1");
}
// --- 1.12.8 office methods: the registry links methods to tools (descriptions, revit_guide, capture_method cases) ---
{
  const listed = (await client.listTools()).tools;
  const desc = (n) => listed.find((t) => t.name === n).description;
  for (const n of ["clash_resolve", "run_clash_test", "coordination_report", "capture_method"])
    assert.ok(desc(n).endsWith(" Office method: revit_guide methods/structural-interference-solving."), `${n} names its office method`);
  assert.ok(!/Office method:/.test(desc("register_update")), "only linked tools");
  assert.match((await call("revit_guide", { topic: "methods" })).content[0].text, /Office methods \(read one with revit_guide methods\/<name>\):\n- methods\/structural-interference-solving \(v1, 2026-10-20\): tools clash_resolve, run_clash_test, coordination_report, capture_method\./);
  assert.match((await call("revit_guide", { topic: "methods/structural-interference-solving" })).content[0].text, /Join priority Foundation > Column > Wall > Slab > Beam\. Join all touching concrete, not only clashing pairs\./);
  assert.match((await call("revit_guide", { topic: "methods/none" })).content[0].text, /No office method 'methods\/none'/);
  const Cap = await import("../lib/capture.js");
  const md = Cap.caseMarkdown({ label: "TST", model: "Test", startedAt: "a", stoppedAt: "b", diffs: [], journal: { sequence: [], totals: {} }, methods: [{ name: "structural-interference-solving", version: 1 }],
    actions: [{ label: "Floors x Structural Columns: join added", count: 3, ids: ["1 x 2"], rule: { text: "Structural Columns cut Floors", count: 3, of: 3 }, exceptions: [] }] });
  assert.match(md, /Would update office method structural-interference-solving \(v1\) once confirmed \(revit_guide methods\/structural-interference-solving\)\./);
}
// --- register_update (no Revit): check first, write with confirm after the identical check, never the register ---
{
  const ExcelJS = (await import("exceljs")).default;
  const rDir = path.join(tmp, "register");
  fs.mkdirSync(path.join(rDir, "cde"), { recursive: true });
  const wbk = new ExcelJS.Workbook();
  const sh = wbk.addWorksheet("TST_REG");
  sh.getRow(1).values = ["Doc No", "Stage 1"];
  sh.getRow(2).values = ["TST-001", null]; sh.getRow(3).values = ["TST-002", new Date("2026-12-01T00:00:00Z")];
  const reg = path.join(rDir, "TST register.xlsx");
  await wbk.xlsx.writeFile(reg);
  fs.writeFileSync(path.join(rDir, "cde", "TST-001_A.pdf"), "x"); fs.writeFileSync(path.join(rDir, "cde", "TST-999.pdf"), "x");
  const ru = { register: reg, id_column: "Doc No", stage_columns: ["Stage 1"], delivered_dir: path.join(rDir, "cde"), submission_date: "2026-10-20", label: "SUB 01", output: path.join(rDir, "out.xlsx") };
  assert.match((await call("register_update", { ...ru, output: reg })).content[0].text, /output is the register itself/, "never the register");
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /has not been checked/, "a write needs the identical check");
  const chk = (await call("register_update", ru)).content.at(-1).text;
  assert.match(chk, /\(check, nothing written\)[\s\S]*Matched \(delivered, date set\): 1: TST-001 <- TST-001_A\.pdf\.\nDelivered but not in the register: 1: TST-999\.pdf\.\nIn the register but not delivered: 1: TST-002\.\nDates later than the submission, cleared: 1: TST-002 Stage 1 2026-12-01\./);
  assert.ok(!fs.existsSync(ru.output), "a check writes nothing");
  fs.writeFileSync(path.join(rDir, "cde", "TST-002.pdf"), "x");
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /has not been checked/, "a changed delivered folder needs a new check");
  await call("register_update", ru);
  const wr = (await call("register_update", { ...ru, confirm: true })).content.at(-1).text;
  assert.ok(fs.existsSync(ru.output) && /written to .*out\.xlsx/.test(wr) && /Report: .*out - report\.csv/.test(wr));
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /has not been checked/, "a check is used once");
  await call("register_update", ru);
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /exists: give another output path, or overwrite: true/);
  // The report CSV follows the same rule, checked before the workbook is written.
  const ru2 = { ...ru, output: path.join(rDir, "out2.xlsx") };
  fs.writeFileSync(path.join(rDir, "out2 - report.csv"), "keep");
  await call("register_update", ru2);
  const csvNo = (await call("register_update", { ...ru2, confirm: true })).content[0].text;
  assert.ok(/out2 - report\.csv exists: .*Nothing was written\./.test(csvNo) && !fs.existsSync(ru2.output) && fs.readFileSync(path.join(rDir, "out2 - report.csv"), "utf8") === "keep", "an existing report CSV stops the write before the workbook");
  // Merged id / stage cells: reported by the check, the write refuses unless allow_merged.
  const wm = new ExcelJS.Workbook();
  const shm = wm.addWorksheet("TST_REG");
  shm.getRow(1).values = ["Doc No", "Stage 1"]; shm.getRow(2).values = ["TST-001", null]; shm.getRow(3).values = ["TST-002", null];
  shm.mergeCells("B2:B3");
  const regM = path.join(rDir, "TST merged.xlsx");
  await wm.xlsx.writeFile(regM);
  const rm = { ...ru, register: regM, output: path.join(rDir, "outm.xlsx") };
  assert.match((await call("register_update", rm)).content.at(-1).text, /Merged cells in the id or stage columns: B2:B3/);
  assert.ok(/Blocked: merged cells in the id or stage columns \(B2:B3\)/.test((await call("register_update", { ...rm, confirm: true })).content[0].text) && !fs.existsSync(rm.output));
  await call("register_update", rm);
  assert.ok(!(await call("register_update", { ...rm, confirm: true, allow_merged: true })).isError && fs.existsSync(rm.output), "allow_merged writes after the user checked");
  // 1.12.7: the vault naming rule's idPattern skips rows it does not find; stage takes a list; a wrong id column is blocked.
  const rv = path.join(rDir, "vault");
  fs.mkdirSync(path.join(rv, "ACE Vault", "rules"), { recursive: true });
  fs.writeFileSync(path.join(rv, "ACE Vault", "rules", "naming.json"), JSON.stringify({ idPattern: "^TST-00[1]$" }));
  const vt = (await call("register_update", { ...ru, vault: rv, stage: ["Stage 1"] })).content.at(-1).text;
  assert.ok(/Headings\/skipped \(not ids\): 1: row 3 "TST-002"/.test(vt) && /Id pattern from the vault: .*naming\.json/.test(vt), "the vault's id pattern");
  const wrong = await call("register_update", { ...ru, id_column: "Stage 1", force_id_column: false });
  assert.ok(wrong.isError && /The id column looks wrong/.test(wrong.content[0].text));
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
