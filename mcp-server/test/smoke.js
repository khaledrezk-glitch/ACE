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
let fakeOpened = null;   // workset_plan paths: the model open_model opened last (facts answer for it)
const notUndoableRuns = [];   // execute_code runs of not-undoable code the fake bridge executed
// The ACE loader's view in /health (reload_addin): a swap is reported there once the new build listens.
const fakeLoader = { build: "1.13.3-aaaaaaaaaaaa#1", loaderVersion: "1.0.0", payloadVersion: "1.13.3", previous: null, pending: true, pendingVersion: "1.13.4", lastReload: { at: "startup", ok: true, why: "ACE 1.13.3 loaded" } };

const bridge = http.createServer((req, res) => {
  if (req.method === "GET" && req.url === "/health") { res.setHeader("Content-Type", "application/json"); return res.end(JSON.stringify({ ok: true, service: "ace-revit-mcp", revitVersion: "2025", build: fakeLoader.build, loader: fakeLoader })); }
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    const reply = (o) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(o?.ok ? { ...o, build: fakeLoader.build } : o)); };
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
        if (args.code.includes("NOT_UNDOABLE")) {
          // A not-undoable call: the dry run only compiles and screens (records no execution); the real run executes.
          if (args.dry_run) return reply({ ok: true, result: { success: true, dryRun: true, executed: false, previewable: false, notUndoable: ["renames a workset"], warning: "Not previewed: this script renames a workset, which cannot be undone. The dry run only compiled and screened it. A real run needs allow_risky and the user's consent, and it cannot be rolled back." } });
          notUndoableRuns.push(args.code);
          return reply({ ok: true, result: { success: true, transaction: "Committed", executed: true } });
        }
        if (args.code.includes("boom")) return reply({ ok: true, result: { success: false, stage: "compile", errors: ["line 1: CS0103"] } });
        return reply({ ok: true, result: { success: true, transaction: args.dry_run ? "RolledBack" : "Committed", result: 42, echoMode: args.mode, inputs: args.inputs, [args.dry_run ? "wouldChange" : "changed"]: { added: 1, modified: 2, deleted: 0 } } });
      case "project_rules": {
        const ex = JSON.parse(fs.readFileSync(new URL("../bep/worksets.example.json", import.meta.url), "utf8"));
        // The worksets section carries the generic naming rule (pattern, allowed names, renames); synthetic TST_ names.
        const naming = { pattern: "^TST_[A-Za-z]+$", allowed: ["TST_Columns", "TST_Walls", "TST_Empty", "TST_Beams", "TST_Old", "TST_Verifyfail", "TST_Drift"], rename: [{ from: "Workset1", to: "TST_Walls" }], defaultName: "TST_Walls" };
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
        const reportOnly = [{ a: 401, b: 402, aCategory: "Walls", bCategory: "Walls", why: "duplicate or overlapping walls", recommendation: "decide which wall stays" },
          ...(args.max_pairs === 5 && !(args.flips ?? []).includes("switch-join:501>502") ? [{ id: "switch-join:501>502", clashKey: "switch-join:501>502", code: "flip-confirm", a: 501, b: 502, aCategory: "Walls", bCategory: "Floors", level: "L1", why: "joined, but the order says the Walls should cut the Floors: may be a deliberate exception" }] : [])];
        if ((args.flips ?? []).includes("switch-join:501>502")) plan.push({ id: "switch-join:501>502", kind: "switch-join", a: 501, b: 502, aCategory: "Walls", bCategory: "Floors", level: "L1", chunk: 1, confirmedFlip: true });
        const blocked = keys.includes("TST_BORROW") ? [{ what: "2 element(s) to join", why: "borrowed by another user", elements: 2, sampleIds: [201, 301], holder: "jane", how: "ask jane to synchronise and relinquish, then run again", code: "borrowed" }] : [];
        const filtered = args.materials?.length || args.exclude_materials?.length ? { filtered: { steel: 12, unknown: 1 } } : {};
        const base = { model: "Test", ...filtered, aceVersion: keys.includes("TST_OLDADDIN") ? "1.9.0" : SERVER_VERSION, before, plan, planHash: `ph-${keys.join(",")}-${args.test_force_verify_mismatch ? "f" : ""}`, reportOnly, blocked, echo: args,
          ...(keys.includes("TST_SKIPTRIM") ? { skipped: [{ id: "trim:211>302:end", a: 211, b: 302, code: "hosts-openings" }, { id: "trim:212>302:start", a: 212, b: 302, code: "one-trim-per-end" }] } : {}),
          ...(args.scope === "all-touching" && args.max_pairs === 5 ? { touching: { candidates: 120, pairs: 12, right: 2, join: 3, switchJoin: 1, flipConfirm: 1, notTouching: 7, byPair: { "Walls x Floors": 4 }, chunk: args.chunk ?? 1, chunkSize: args.chunk_size ?? 5, capped: true,
            clashing: 1, chunks: [{ chunk: 1, pairs: 5, clashing: 1, notJoined: 3, wrongOrder: 2, levels: ["L1"] }, { chunk: 2, pairs: 5, notJoined: 4, wrongOrder: 0, levels: ["L1", "L2"] }, { chunk: 3, pairs: 2, notJoined: 2, wrongOrder: 0, levels: ["L2"] }] },
            quantities: { Floors: { volumeM3: { before: 12.5, after: 12.25 }, areaM2: { before: 50, after: 49.5 } } } } : {}),
          ...(args.scope === "all-touching" && args.max_pairs !== 5 ? { dropped: { notTouching: 40, trim: 0 }, scope: "all-touching", scopeSource: args.scope_source ?? "request", touching: { candidates: 120, pairs: 80, right: 38, join: 30, switchJoin: 12, byPair: { "Floors x Structural Columns": 25 } } } : {}),
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
          warnings: { before: 10, after: 10, new: keys.includes("TST_NEWWARN") ? ["Highlighted elements are joined but do not intersect."] : [],
            ...(args.max_pairs === 5 ? { byPlanItem: [{ warning: "TST wall and floor overlap.", elements: [205, 206], planItems: ["switch-join:205>206"] }, { warning: "TST near only.", elements: [207, 208], planItems: [], nearPlanItems: ["join:207>209"] }] } : {}) },
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
        // 1.13.0 workset_plan: grouped facts, and apply_plan (create, rename, moves, delete; borrowed skipped; a changed count blocks).
        if (args.action === "facts" || args.action === "apply_plan") {
          const G = (workset, category, type, level, material, elements, extra = {}) => ({ workset, category, type, level, material, zone: "", option: "", elements, borrowed: 0, holders: [], sampleIds: [elements], ...extra });
          const groups = [
            G("Workset1", "Structural Framing", "TST Beam : 300x600", "TST-RF(ROOF)", "concrete", 12),
            G("TST-ST-ROOF STEEL", "Structural Framing", "TST Beam : 300x600", "TST-RF(ROOF)", "concrete", 3),
            G("TST-ST-ROOF STEEL", "Structural Framing", "TST UB : 254x146", "TST-RF(ROOF)", "steel", 6),
            G("TST-ST-RF FRAMING", "Structural Framing", "TST Beam : 300x600", "TST-RF(ROOF)", "concrete", 20),
            G("Workset1", "Structural Columns", "TST Col : 400x400", "TST-GF(GROUND FLOOR)", "concrete", 30, { borrowed: 2, holders: ["jane"] }),
            G("TST-ST-GF COLUMNS", "Structural Columns", "TST Col : 400x400", "TST-GF(GROUND FLOOR)", "concrete", 10),
            G("TST-ST-GF COLUMN ", "Structural Columns", "TST Col : 300x300", "TST-GF(GROUND FLOOR)", "concrete", 2),
            G("Workset1", "Stairs", "TST Stair : A", "TST-GF(GROUND FLOOR)", "concrete", 2),
            G("Workset1", "Runs", "TST Run : A", "TST-GF(GROUND FLOOR)", "concrete", 4),
            G("Workset1", "Stairs", "TST Stair : B", "TST-01", "steel", 1),
            G("Workset1", "RVT Links", "TST_EL_Model.rvt", "", "other", 1),
            G("Workset1", "RVT Links", "TST_XX_Model.rvt", "", "other", 1),
            G("Workset1", "CAD Imports", "TST_ROOF.dwg", "", "other", 1),
            G("TST-ST-HIDE ALL", "Generic Models", "TST Mask : A", "", "other", 5),
            G("Workset1", "Generic Models", "TST Thing : A", "", "other", 3)];
          groups.forEach((g) => { g.key = [g.workset, g.category, g.type, g.level, g.material, g.zone, g.option].join("|"); });
          const names = ["TST Shared Views, Levels, Grids", "TST-ST-GF COLUMNS", "TST-ST-GF COLUMN ", "TST-ST-RF FRAMING", "TST-ST-ROOF STEEL", "TST-ST-STAIRS", "TST-ST-GF STAIRS", "TST-ST-EL RVT LINKS", "TST-ST-RVT LINKS", "TST-ST-CAD LINKS", "TST-ST-HIDE ALL", "TST-ST-OLD", "Workset1"];
          const wsF = names.map((name, i) => { const mine = groups.filter((g) => g.workset === name); const byCategory = {}; mine.forEach((g) => { byCategory[g.category] = (byCategory[g.category] ?? 0) + g.elements; });
            return { id: i + 1, name, elements: i === 0 ? 12 : mine.reduce((n, g) => n + g.elements, 0), byCategory: i === 0 ? { Levels: 4, Grids: 8 } : byCategory, isDefault: name === "Workset1", protected: i === 0, editable: true, owner: "", open: true,
              visibleByDefault: true, views: { "TST NAVISWORKS": !["TST-ST-HIDE ALL", "TST-ST-STAIRS"].includes(name) }, types: name === "TST-ST-OLD" ? 2 : 0 }; });
          const factsModel = fakeOpened ?? "Test";
          if (args.action === "facts") return reply({ ok: true, result: { model: factsModel, aceVersion: SERVER_VERSION, action: "facts", checkOnly: true, workshared: true, datumWorkset: names[0], exportViews: args.views ?? ["TST NAVISWORKS"],
            levels: [{ name: "TST-GF(GROUND FLOOR)", elevation: 0 }, { name: "TST-01", elevation: 3000 }, { name: "TST-RF(ROOF)", elevation: 6000 }], zones: ["TST ZONE A"], worksets: wsF, groups, echo: args } });
          const p = args.workset_plan ?? {};   // 1.13.1: the plan travels as workset_plan (plan is the panel checklist)
          const steps = [...(p.create ?? []).map((n) => ({ id: `create:${n}`, kind: "create", workset: n })), ...(p.rename ?? []).map((r) => ({ id: `rename:${r.from}`, kind: "rename", workset: r.from, from: r.from, to: r.to })),
            // max_ids 3: the borrowed groups are wholly borrowed (no step left for them; only skipped).
            ...(p.moves ?? []).filter((m) => !(args.max_ids === 3 && groups.find((x) => x.key === m.key)?.borrowed)).map((m) => ({ id: `move:${m.key}`, kind: "move", workset: m.from, key: m.key, from: m.from, to: m.to, elements: m.elements })), ...(p.delete ?? []).map((n) => ({ id: `delete:${n}`, kind: "delete", workset: n, elements: 0 }))];
          const changed = (p.moves ?? []).filter((m) => { const g = groups.find((x) => x.key === m.key); return !g || g.elements !== m.elements; }).map((m) => ({ key: m.key, expected: m.elements, found: groups.find((x) => x.key === m.key)?.elements ?? 0 }));
          const blockedP = changed.map((c) => ({ what: `move ${c.key}`, why: `${c.expected} planned, ${c.found} found`, code: "plan-changed" }));
          const retriedOk = (args.check_only ? [] : args.pending_retry ?? []).filter((r) => r.id === 501).map((r) => r.id);   // 501 is free now, 502 still held
          const skipped = (p.moves ?? []).filter((m) => groups.find((x) => x.key === m.key)?.borrowed).map((m) => { const ids = [501, 502].filter((id) => !retriedOk.includes(id)); return { key: m.key, ids, count: ids.length, holder: "jane", why: "borrowed" }; });
          const planHash = `wp-${steps.map((s) => s.id + (s.to ?? "")).join(",")}`;
          const visibility = (p.moves ?? []).filter((m) => m.to === "TST-ST-STAIRS").map((m) => ({ move: `move:${m.key}`, key: m.key, to: m.to, view: "TST NAVISWORKS", wouldHide: m.elements, wouldShow: 0, sampleIds: [7] }));
          // 1.13.6: a moved element a sheet view would show differently is held unless allow_visibility_change (true, or a view / workset name).
          const allowV = (m) => args.allow_visibility_change === true || (Array.isArray(args.allow_visibility_change) && (args.allow_visibility_change.includes("TST L1 Plan") || args.allow_visibility_change.includes(m.to)));
          const vm = (p.moves ?? []).filter((m) => m.to === "TST-ST-STAIRS"), vHeld = vm.filter((m) => !allowV(m));
          const visX = args.check_only || !vm.length ? {} : { visibilityChanges: [{ view: "TST L1 Plan", sheet: "TST-01 - TST Ground Plan", exportView: false, hidden: vm.length, shown: 0, held: vHeld.length, accepted: vm.length - vHeld.length, why: "the target workset is hidden in this view", categories: { Stairs: vm.length }, sampleIds: [7] },
            { view: "TST NAVISWORKS", sheet: null, exportView: true, hidden: vm.length, shown: 0, held: 0, accepted: 0, why: "the target workset is hidden in this view", categories: { Stairs: vm.length }, sampleIds: [7] }],
            visibilityChecked: { sheetViews: 1, exportViews: 1, notChecked: ["TST Broken View"] }, ...(vHeld.length ? { visibilityHeld: { elements: vHeld.length, moves: vHeld.map((m) => `move:${m.key}`), sampleIds: [7] } } : {}) };
          const baseP = { model: "Test", aceVersion: SERVER_VERSION, planHash, plan: steps, skipped, changed, exportViews: ["TST NAVISWORKS"], visibility, ...visX, echo: args };
          if (blockedP.length) return reply({ ok: true, result: { ...baseP, success: false, stage: "blocked", blocked: blockedP, error: "Nothing was changed: blocked" } });
          if (args.check_only) return reply({ ok: true, result: { ...baseP, success: true, checkOnly: true, blocked: [] } });
          if (args.expect_plan && args.expect_plan !== planHash) return reply({ ok: true, result: { success: false, stage: "blocked", code: "plan-changed", model: "Test", blocked: [{ what: "the plan", why: "differs", code: "plan-changed" }] } });
          const moved = (p.moves ?? []).reduce((n, m) => n + m.elements, 0) - skipped.length * 2;
          const np = args.dry_run ? { notPreviewed: steps.filter((x) => x.kind === "move" && x.to === "TST-ST-CAD LINKS").map((x) => x.id) } : {};
          const keptP = args.dry_run ? { kept: [{ name: "TST-ST-OLD", elements: 0, types: 2, why: "holds 2 types (a plan moves instances only)" }], notPreviewedCounts: { moves: 1, elements: 1, linkTypes: 1 } } : {};
          // 1.13.3: borrowed elements -> pending [{ id, owner, from, to }]; pins; reset_to; pending_retry (501 moves now, 502 is still held).
          const pend = skipped.flatMap((x) => { const m = (p.moves ?? []).find((y) => y.key === x.key); return x.ids.map((id) => ({ id, category: m.category, owner: "jane", holder: "jane", why: "borrowed", from: m.from, to: m.to })); });
          const retryP = args.pending_retry ?? [];
          const x13 = { pending: [...pend, ...retryP.filter((r) => r.id === 502 && !pend.some((q) => q.id === 502)).map((r) => ({ id: 502, owner: "jane", why: "borrowed", from: "Workset1", to: r.to }))], pendingTotal: pend.length,
            pinned: { count: 3, sampleIds: [9], directOk: 2, unpinRepin: 1 }, ...(retryP.length ? { retried: retryP.map((r) => r.id === 502 ? { id: 502, ok: false, why: "still borrowed by jane" } : r.id === 503 ? { id: 503, ok: false, drop: true, why: "not found" } : { id: r.id, ok: true, why: "moved" }) } : {}),
            ...(!args.dry_run && !args.check_only ? { movedElements: [{ id: 501, from: "Workset1", to: "TST-ST-GF COLUMNS" }], movedTotal: 1 } : {}),
            ...(args.reset_to ? { reset: { to: args.reset_to, elements: 4, kept: args.keep_worksets ?? [], skippedBorrowed: 2, skippedCentral: 0, readOnly: 0 } } : {}),
            ...(args.dry_run ? { worksetCounts: [{ workset: "Workset1", id: 13, before: 54, after: 3 }, { workset: "TST-ST-GF COLUMNS", id: 2, before: 10, after: 42 }] } : {}) };
          return reply({ ok: true, result: { ...baseP, ...np, ...keptP, ...x13, success: true, blocked: [], rolledBack: !!args.dry_run, verify: { ok: true, checked: 9, mismatches: [] },
            done: args.dry_run ? { created: 0, renamed: 0, moved: 0, deleted: 0 } : { created: (p.create ?? []).length, renamed: (p.rename ?? []).length, moved, deleted: (p.delete ?? []).length } } });
        }
        const ws = [
          { id: 1, name: "Shared Levels and Grids", elements: 30, byCategory: { Levels: 10, Grids: 20 }, owner: "", open: true, editable: false, isDefault: false, wrongWorkset: 0 },
          { id: 2, name: "TST_Columns", elements: 10, byCategory: { "Structural Columns": 10 }, owner: "", open: true, editable: false, wrongWorkset: 0 },
          { id: 3, name: "TST-COLUMNS", elements: 2, byCategory: { "Structural Columns": 2 }, owner: "jane", open: true, editable: false, wrongWorkset: 0 },
          { id: 4, name: "TST_Walls", elements: 5, byCategory: { Walls: 4, Floors: 1 }, owner: "", open: true, editable: true, isDefault: true, wrongWorkset: 2 },
          { id: 5, name: "TST_Empty", elements: 0, byCategory: {}, owner: "", open: true, editable: false, wrongWorkset: 0 },
          { id: 6, name: "Workset1", elements: 3, byCategory: { Walls: 3 }, owner: "", open: false, editable: false, wrongWorkset: 3 },
          { id: 7, name: "tst_beams", elements: 4, byCategory: { "Structural Framing": 4 }, owner: "", open: true, editable: false, wrongWorkset: 0 }];
        // max_ids 7: a model without assignment rules whose links sit on Workset1 (1.12.9 audit gaps).
        if (args.max_ids === 7) {
          ws.length = 0;
          ws.push({ id: 1, name: "Shared Levels and Grids", elements: 12, byCategory: { Levels: 10, Walls: 2 }, wrongWorkset: null },
            { id: 2, name: "TST RVT LINKS", elements: 0, byCategory: {}, wrongWorkset: null }, { id: 3, name: "TST CAD LINKS", elements: 0, byCategory: {}, wrongWorkset: null },
            { id: 4, name: "TST OLD", elements: 0, byCategory: {}, wrongWorkset: null }, { id: 6, name: "TST PCC", elements: 0, byCategory: {}, wrongWorkset: null },
            { id: 5, name: "Workset1", elements: 60, byCategory: { "RVT Links": 4, "TST site.dwg": 2, "Structural Columns": 30, Floors: 20, "Structural Foundations": 4 }, wrongWorkset: null });
          if (args.action === "audit") return reply({ ok: true, result: { model: "Test", aceVersion: SERVER_VERSION, workshared: true, rules: null, worksets: ws } });
        }
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
      case "list_views": {
        if (args.view_type !== "DrawingSheet") return reply({ ok: true, result: { command, args } });
        return reply({ ok: true, result: { count: 3, views: [{ id: 801, name: "TST Ground Plan", type: "DrawingSheet", sheetNumber: "TST-01" }, { id: 802, name: "TST Sections", type: "DrawingSheet", sheetNumber: "TST-02" }, { id: 803, name: "TST Roof Plan", type: "DrawingSheet", sheetNumber: "TST-10" }] } });
      }
      case "export_deliverables": {
        // 1.13.6: the add-in lists the IFC setups saved in the model, and looks a setup up there first.
        if (args.list_ifc_setups) return reply({ ok: true, result: { model: "Test", ifcSetups: ["TST model setup", "TST setup"] } });
        // The 1.12.6 contract: pre-flight (exporter, views, IFC setup, sheets, existing files), blocks, then files[].
        const blocked = [], existing = [], files = [];
        const nav = !args.out_dir.includes("NONAVIS");
        const at = (f) => path.join(args.out_dir, f);
        for (const f of ["nwc", "ifc", "dwfx"]) if (args[f] && fs.existsSync(at(args[f].file))) { existing.push(at(args[f].file)); if (!args.overwrite) blocked.push({ what: `${f} ${args[f].file}`, why: `${at(args[f].file)} exists`, code: "exists" }); }
        if (args.nwc && !nav) blocked.push({ what: "nwc", why: "the Navisworks exporter is not installed for this Revit", code: "no-exporter" });
        if (args.nwc && args.nwc.view !== "TST 3D Navis") blocked.push({ what: `nwc view ${args.nwc.view}`, why: `no view is called ${args.nwc.view}`, code: "view-not-found" });
        if (args.ifc?.setup && !args.ifc.config && args.ifc.setup !== "TST model setup") blocked.push({ what: `ifc setup ${args.ifc.setup}`, why: "no IFC setup by that name in the model, and no config", code: "setup-not-found" });
        const preflight = { navisworksExporter: nav, views: { nwc: args.nwc ? args.nwc.view === "TST 3D Navis" : undefined }, ifcSetup: args.ifc ? (args.ifc.setup === "TST model setup" ? { name: "TST model setup", from: "model" } : args.ifc.setup ?? null) : null, sheets: args.dwfx ? (args.dwfx.sheets ?? []).length : 0, existing, outDir: args.out_dir };
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
        if (model.startsWith("TST_WP")) fakeOpened = model;
        const extra = model.includes("TRANS") ? { transmitted: true, transmittedCleared: !!args.clear_transmitted,
          unresolvedLinks: Array.from({ length: 7 }, (_, i) => ({ type: i ? "RVT" : "CAD", path: `C:/links/L${i}.rvt`, exists: i !== 1 })),
          dialogs: [{ id: "TaskDialog_Unresolved_References", message: "Unresolved references", answer: "Ignore and continue opening", why: "links are reported, not fixed while opening" },
            { id: "TaskDialog_Unknown", message: "Something unusual", answer: null, why: "no rule for this dialog" }] } : { transmitted: false, transmittedCleared: false, unresolvedLinks: [], dialogs: [] };
        return reply({ ok: true, result: { model, path: args.path, workshared: true, detached: args.detach !== "none", audited: args.audit, worksetsOpened: args.worksets, echo: args, ...extra,
          closed: args.close_previous ? (model.includes("KEEP") ? { model: "Test", closed: false, why: "it has unsaved changes" } : { model: "Test", closed: true }) : null,
          openDocuments: args.close_previous && !model.includes("KEEP") ? [model] : ["Test", model] } });
      }
      case "close_model": return reply({ ok: true, result: { model: args.model, closed: true } });
      case "reload_addin": {
        const base = { loaderVersion: fakeLoader.loaderVersion, payloadVersion: fakeLoader.payloadVersion, previous: fakeLoader.previous };
        if (!fakeLoader.pending && !args.force) return reply({ ok: true, result: { ...base, ok: true, scheduled: false, why: `ACE ${fakeLoader.payloadVersion} is already the installed build (force reloads it anyway).` } });
        // The swap happens later (Revit idle), after this answer: /health then shows it.
        setTimeout(() => Object.assign(fakeLoader, { build: "1.13.4-bbbbbbbbbbbb#2", previous: fakeLoader.payloadVersion, payloadVersion: "1.13.4", pending: false,
          lastReload: { at: `swap ${seen.length}`, ok: true, loaderVersion: "1.0.0", payloadVersion: "1.13.4", previous: fakeLoader.payloadVersion, direction: "upgrade", why: `ACE ${fakeLoader.payloadVersion} replaced by 1.13.4` } }), 120);
        return reply({ ok: true, result: { ...base, ok: true, scheduled: true, candidate: "1.13.4", why: "ACE 1.13.4 will be loaded in a moment" } });
      }
      case "coordination_report": {
        if (!args.interference) return reply({ ok: true, result: { htmlReport: "C:/x/Model - Coordination.html", excelList: "C:/x/Model - Coordination.csv", issues: 7, withPictures: 7 } });
        // The interference rows: one per clash, grouped by issue; test "MULTI" answers for two open models.
        const rows = (model) => [
          { issue: "I-2", issueTitle: "Wall W1 overlaps wall W2", clashKey: "k3", status: "active", aId: 401, aCategory: "Walls", aFamily: "Basic Wall", aType: "RC 200", bId: 402, bCategory: "Walls", bFamily: "Basic Wall", bType: "RC 200", level: "L1", depthMm: 200, x: 1.5, y: 2.25, z: 0, responsible: "STR" },
          { issue: "I-1", issueTitle: `Beam B7 into columns (${model})`, clashKey: "k1", status: "new", aId: 202, aCategory: "Structural Framing", aFamily: "BU <250>", aType: "BU 250", bId: 302, bCategory: "Structural Columns", bFamily: "Concrete-Rectangular-Column", bType: "400x400", level: "Roof", depthMm: 83.4, x: 10.1234, y: 5, z: 12.5, responsible: "STR" },
          { issue: "I-1", issueTitle: `Beam B7 into columns (${model})`, clashKey: "k2", status: "new", aId: 202, aCategory: "Structural Framing", aFamily: "BU <250>", aType: "BU 250", bId: 303, bCategory: "Structural Columns", bFamily: "Concrete-Rectangular-Column", bType: "400x400", level: "Roof", depthMm: 80, x: 14, y: 5, z: 12.5, responsible: "STR" }];
        const one = (model) => ({ model, test: args.test ?? "all", run: { id: "run-1", at: "2026-10-20T10:00:00Z" }, counts: { clashes: 3 }, ...(args.test === "TST_STORED" ? { stored: { count: 1, models: [] } } : {}),
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
        // 1.12.9: the top-level stored is the only total; each test carries storedInTest (part of it).
        return reply({ ok: true, result: { storeKey: "Test", ...(args.test === "TST_STORED" ? { stored: { count: 1340, open: 1200, models: ["TST_Other"] } } : {}),
          tests: [{ test: args.test || "all", open: 4, byResponsible: { "MEP (HVAC)": 3, "ARC": 1 }, ...(args.test === "TST_STORED" ? { storedInTest: { count: 1340 } } : {}) }] } });
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
  env: { ...process.env, ACE_REVIT_CONFIG: path.join(tmp, "config.json"), ACE_REVIT_URL: `http://127.0.0.1:${port}`, ACE_REVIT_SCRIPTS: path.join(tmp, "scripts"), ACE_REVIT_JOURNAL: path.join(tmp, "journal"), ACE_REVIT_LOGS: path.join(tmp, "logs"), ACE_REVIT_REPORTS: path.join(tmp, "reports"), ACE_TEAM_SCRIPTS: path.join(tmp, "team"), ACE_TEAM_REPORTS: path.join(tmp, "team-reports"), ACE_REVIT_LESSONS: path.join(tmp, "lessons"), ACE_TEAM_LESSONS: path.join(tmp, "team-lessons"), ACE_RELOAD_POLL_MS: "40", ACE_RELOAD_WAIT_MS: "5000", ACE_REVIT_JOBS: path.join(tmp, "jobs") },
});
const client = new Client({ name: "smoke", version: "1.0.0" });
await client.connect(transport);

const call = async (name, args = {}) => client.callTool({ name, arguments: args });
const json = (r) => JSON.parse(r.content[0].text);

const tools = (await client.listTools()).tools.map((t) => t.name).sort();
assert.deepEqual(tools, ["assign_worksets", "backup_model", "capture_method", "check_setup", "clash_resolve", "clash_view", "close_model", "coordination_report", "describe_category", "describe_family", "execute_revit_code", "export_deliverables", "find_elements", "get_activity_log", "get_element_details", "get_model_brief", "get_selection", "job_status", "lessons", "list_types", "list_views", "list_warnings", "model_changes", "model_dashboard", "open_model", "open_view", "parameter_check", "parameter_fill", "pending_changes", "presentation_standard", "project_rules", "register_update", "reload_addin", "report_issue", "revit_api_lookup", "revit_guide", "revit_status", "run_clash_test", "run_saved_script", "save_model", "save_script", "saved_scripts", "select_elements", "set_clash_status", "set_parameters", "snapshots", "undo_last_claude_change", "vault", "view_image", "view_membership_fill", "working_mode", "workset_plan"]);
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

// --- not-undoable calls (RenameWorkset, SaveAs...): the dry run compiles and screens only; the identical real run needs consent ---
const nuCode = 'WorksetTable.RenameWorkset(doc, new WorksetId(1), "TST_X"); // NOT_UNDOABLE';
const nuDry = await call("execute_revit_code", { code: nuCode, dry_run: true, explanation: "Rename a workset", allow_risky: true });
assert.equal(nuDry.isError, undefined, "a dry run of not-undoable code is a successful (compile-only) preview");
assert.ok(nuDry.content.some((c) => /Not previewed: this script renames a workset/.test(c.text)) && !nuDry.content.some((c) => /PREVIEW ONLY/.test(c.text)), "the reply says it was not previewed");
assert.equal(notUndoableRuns.length, 0, "the dry run executed nothing");
const nuReal = await call("execute_revit_code", { code: nuCode, dry_run: false, explanation: "Rename a workset", allow_risky: true });
assert.equal(nuReal.isError, undefined, "the identical real run with allow_risky goes through the preview gate");
assert.equal(notUndoableRuns.length, 1, "the real run executed once");

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
{ // 1.14.8: a long-lived server with older code than the add-in is named on every Revit call.
  const { versionMismatch } = await import("../lib/core.js");
  assert.equal(versionMismatch("1.14.7.0", "1.14.7"), null);
  assert.equal(versionMismatch(undefined, "1.14.7"), null);
  assert.match(versionMismatch("1.14.7", "1.14.6"), /ACE server is 1\.14\.6, the add-in in Revit is 1\.14\.7/);
}
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
// --- G9: a workset rule's models key: rules for other models are left out before the request; the reply says which and why ---
{
  const prCalls = () => seen.filter((x) => x.command === "project_rules").length;
  const n0 = prCalls();
  await call("assign_worksets", { check_only: true, rules: [{ workset: "TST_A", category: "Walls" }] });
  assert.equal(prCalls(), n0, "G9: rules without a models key need no extra question to Revit");
  const g9 = await call("assign_worksets", { check_only: true, rules: [
    { id: "g9-inc", workset: "TST_A", models: "Test" }, { id: "g9-other", workset: "TST_B", models: ["TST_ARC", "TST_MEP"] }, { workset: "TST_C" },
    { id: "g9-wild", workset: "TST_D", models: "T*" }, { id: "g9-ex", workset: "TST_E", models: ["*", "!test.rvt"] }] });
  assert.deepEqual(seen.filter((x) => x.command === "assign_worksets").at(-1).args.rules.map((r) => r.workset), ["TST_A", "TST_C", "TST_D"],
    "G9: include (exact, case-insensitive), no key and wildcard rules are sent; another model's rule and an excluded one are not");
  assert.equal(g9.content.at(-1).text, "Rules skipped for Test (their models key does not name it): 2 of 5: g9-other (models TST_ARC, TST_MEP), g9-ex (excluded by !test.rvt).");
  const nA = seen.filter((x) => x.command === "assign_worksets").length;
  const g9none = await call("assign_worksets", { check_only: true, rules: [{ workset: "TST_B", models: "TST_ARC" }] });
  assert.ok(g9none.isError && /No workset rule applies to the open model\. Rules skipped for Test .*: 1 of 1: TST_B \(models TST_ARC\)\. Nothing was checked or changed\./.test(g9none.content[0].text)
    && seen.filter((x) => x.command === "assign_worksets").length === nA, "G9: no rule left for the model: said, Revit not asked to assign");
  const Wg = await import("../lib/worksets.js"), WPg = await import("../lib/worksetplan.js");
  assert.deepEqual(Wg.modelNamesOf({ model: "TST_STR_detached.rvt" }), ["tst_str_detached", "tst_str"], "G9: a detached copy is known by its own name too");
  assert.deepEqual(Wg.modelNamesOf({ model: "TST_STR_jdoe", modelNames: ["TST_STR_jdoe", "tst_str"] }), ["tst_str_jdoe", "tst_str"], "G9: the add-in's names (title, central) are used when sent");
  assert.ok(Wg.ruleAppliesTo({ models: [] }, ["x"]).applies && Wg.ruleAppliesTo({ models: "!TST_ARC" }, ["tst_str"]).applies && !Wg.ruleAppliesTo({ models: "!TST_*" }, ["tst_str"]).applies, "G9: an empty list applies everywhere; an exclusion alone keeps the others");
  // Per-model rules in one project file: disjoint models lists never conflict or overlap; the same rule for another model grows its list.
  const mj = { worksets: { rules: [{ id: "plan-tst-arc-walls", workset: "TST-ARC-WALLS", category: ["Walls"], models: ["TST_ARC"] }] } };
  const mg = WPg.mergeRules(mj, [{ id: "plan-tst-st-walls", workset: "TST-ST-WALLS", category: ["Walls"], models: ["TST_STR"] }, { id: "plan-tst-arc-walls", workset: "TST-ARC-WALLS", category: ["Walls"], models: ["TST_ARC2"] }]);
  assert.ok(mg.added.length === 1 && !mg.conflicts.length && !mg.overlaps.length && mg.same.length === 1 && mg.json.worksets.rules.find((r) => r.workset === "TST-ARC-WALLS").models.join() === "TST_ARC,TST_ARC2", "G9: per-model rules merge without conflicts");
  assert.equal(WPg.mergeRules({ worksets: { rules: [{ id: "a", workset: "TST-ARC-WALLS", category: ["Walls"] }] } }, [{ id: "b", workset: "TST-ST-WALLS", category: ["Walls"], models: ["TST_STR"] }]).conflicts.length, 1, "G9: a rule for every model still conflicts");
  const vr = (o) => WPg.vaultRules([{ workset: "TST-A", categories: ["Walls"], levels: [], words: [], models: ["Test"], ...o }], [], o.opts)[0];
  assert.ok(vr({}).models.join() === "Test" && !vr({ create: true }).models && !WPg.vaultRules([{ workset: "TST-A", categories: ["Walls"], levels: [], words: [], models: ["Test"] }], [], { createMissing: true })[0].models,
    "G9: a saved rule names its models, unless it may create its workset (create, create_missing)");
}
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
  // G8: a reload between the dry run and the apply (the add-in's dropped-steps store is then empty, as is every other
  // in-memory state of the old build): the build-id check refuses the apply before Revit is asked to change anything.
  {
    await call("clash_resolve", { ...fx, dry_run: true });
    const keep = fakeLoader.build;
    fakeLoader.build = "1.13.3-aaaaaaaaaaaa#5";
    const nApplies = crs().filter((x) => x.args.dry_run === false && !x.args.check_only).length;
    const across = await call("clash_resolve", fx);
    fakeLoader.build = keep;
    assert.ok(across.isError && /preview was made on ACE build .*now runs 1\.13\.3-aaaaaaaaaaaa#5/.test(across.content.at(-1).text), "clash_resolve: a dry run from another ACE build is refused at apply");
    assert.equal(crs().filter((x) => x.args.dry_run === false && !x.args.check_only).length, nApplies, "clash_resolve: the refused apply never reached Revit");
  }

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
  assert.match(tt, /All touching: 120 candidate pair\(s\), 80 touching in scope, 38 already right; join 30, switch join 12 \(Floors x Structural Columns 25\)\./);
  assert.match(tt, /Not touching, dropped at run time: 40 pair\(s\) \(no longer touching, or Revit said "joined but do not intersect"\)/);
  assert.match(tt, /Trims to a face \(1; a trim on a join runs only if the join does not clear\):\n- trim 211 \(Structural Framing\) end: 4833 -> 3822 mm \(trim 1011 mm\) to Structural Columns 302 near face \(only if join:201>301 does not clear\)/, "each trim, one by one");
  assert.match(tt, /Scope \(from the project vault \(.*\)\): every touching pair of the categories\. Trim to face: on \(allowed by the project vault \(.*clash-resolve\.json\)\)\./);
  // 1.12.9: keys narrow all-touching to the clashing pairs; a level narrows the touching; max_pairs / same_category.
  const kn = await call("clash_resolve", { vault: tv, keys: ["k1"] });
  assert.ok(crs().at(-1).args.scope === "clashing" && !crs().at(-1).args.scope_source, "keys: clashing pairs only");
  assert.match(kn.content.at(-1).text, /Scope \(from the call's keys\): only the clashing pairs \(the rules say all-touching; the call names clash keys, so only those clashing pairs\)/);
  const ln = await call("clash_resolve", { vault: tv, level: "L1", max_pairs: 5, same_category: false });
  const la = crs().at(-1).args;
  assert.ok(la.scope === "all-touching" && la.narrowed === "level" && la.level === "L1" && la.max_pairs === 5 && la.same_category === false);
  assert.match(ln.content.at(-1).text, /every touching pair of the categories \(all-touching, narrowed to the call's level L1\); same-category joins off; chunks of 5 touching pairs\./);
  // 1.13.0: chunks (the list, which one is planned, the next), flips only from the "flip, confirm" list, quantities, warnings by plan item.
  const lt = ln.content.at(-1).text;
  assert.match(lt, /Chunks \(5 pairs each, by level then id; each chunk is its own dry run, apply and undo step\): chunk 1 of 3 is planned\.\n- chunk 1 of 3: 5 pair\(s\) \(1 clashing: solved as clashes, no touching step\), 3 to join, 2 in the wrong order \(L1\) <- planned\n- chunk 2 of 3: 5 pair\(s\), 4 to join, 0 in the wrong order \(L1, L2\)\n- chunk 3 of 3: 2 pair\(s\), 2 to join, 0 in the wrong order \(L2\)\nNext: run chunk 2 with the same args \(chunk: 2\)/);
  assert.match(lt, /Near, not touching: 7 pair\(s\) \(no contact within 1 mm\): no step\./);
  assert.match(lt, /Flip, confirm \(1; not automatic: an existing join, not clashing, may be deliberate; pass the ids in flips to switch\):\n- switch-join:501>502: 501 \(Walls\) x 502 \(Floors\) @ L1; current cutter 502 \(Floors\), the rule's cutter 501 \(Walls\)/);
  assert.ok(!/Report only \(needs a person, 2\)/.test(lt), "flip items are not counted as report only");
  const l2 = await call("clash_resolve", { vault: tv, level: "L1", max_pairs: 5, same_category: false, chunk: 2, chunk_size: 5 });
  assert.ok(crs().at(-1).args.chunk === 2 && crs().at(-1).args.chunk_size === 5 && /chunk 2 of 3 is planned/.test(l2.content.at(-1).text));
  const fxf = { vault: tv, level: "L1", max_pairs: 5, same_category: false, action: "fix", explanation: "TST flips", report_dir: path.join(tmp, "flipruns") };
  const badf = await call("clash_resolve", { ...fxf, flips: ["switch-join:999>998"], dry_run: true });
  assert.ok(badf.isError && /flip switch-join:999>998.*not on the plan.s "Flip, confirm" list/.test(badf.content[0].text), "flips only from the list");
  const fd = await call("clash_resolve", { ...fxf, flips: ["switch-join:501>502"], dry_run: true });
  assert.ok(!fd.isError && crs().at(-1).args.flips[0] === "switch-join:501>502", fd.content.at(-1).text);
  assert.match(fd.content.at(-1).text, /New warnings by plan item \(steps that join both of its elements\):\n- TST wall and floor overlap\.: switch-join:205>206\n- TST near only\.: no step joins both \(near: join:207>209, for information\)/);
  assert.match(fd.content.at(-1).text, /Volume and area \(concrete, the elements the plan touches; before -> would be\):\n- Floors: volume 12\.500 -> 12\.250 m3, area 50\.000 -> 49\.500 m2/);
  await call("run_clash_test", { test: "ST vs ST", models: ["TST_Other"] });
  assert.deepEqual(seen.filter((x) => x.command === "run_clash_test").at(-1).args.models, ["TST_Other"], "other open models only when named");
  const CRL = await import("../lib/clashresolve.js");
  assert.throws(() => CRL.readRules(JSON.stringify({ chunk_size: 0 }), "TST.json"), /chunk_size must be a whole number/);
  assert.equal(CRL.readRules(JSON.stringify({ chunk_size: 50 }), "TST.json").chunkSize, 50);
  await call("clash_resolve", { vault: tv });
  assert.ok(!("same_category" in crs().at(-1).args) && !("max_pairs" in crs().at(-1).args), "same_category defaults to true (not sent)");
  const sk = await call("clash_resolve", { vault: tv, keys: ["TST_SKIPTRIM"] });
  assert.match(sk.content.at(-1).text, /Skipped:\n- trim:211>302:end 211 x 302: hosts openings\n- trim:212>302:start 212 x 302: one trim per end, the smallest kept/);
  assert.match((await call("clash_resolve", { vault: tv, max_pairs: 0 })).content[0].text, /max_pairs|Too small|>=1/i);
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
  tvRules({ trim_to_face: { allowed: true, preview_required: false } });
  assert.match((await call("clash_resolve", { vault: tv })).content[0].text, /preview_required cannot be false: every apply already needs the identical dry run first/);
  tvRules({ max_pairs: 2.5 });
  assert.match((await call("clash_resolve", { vault: tv })).content[0].text, /max_pairs must be a whole number of pairs from 1 to 100000/);
  tvRules({ same_category: "yes" });
  assert.match((await call("clash_resolve", { vault: tv })).content[0].text, /same_category must be true \(default: same-category joins are part of the method\) or false \(opt out\)/);
  tvRules({ max_pairs: 300, same_category: false });
  await call("clash_resolve", { vault: tv });
  assert.ok(crs().at(-1).args.max_pairs === 300 && crs().at(-1).args.same_category === false, "both from the rules file");
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
  const nBefore = seen.length;
  const cmDisc = await call("close_model", { model: "Test", discard: true });
  assert.ok(cmDisc.isError && /confirm: true/.test(cmDisc.content[0].text) && seen.length === nBefore, "discard without the user's OK is refused before Revit is asked");
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
  assert.match(at, /\nWorkset1 \| 3 \| Walls 3 \| - \| no \| default-workset, naming, wrong-workset -> TST_Walls\?/);
  assert.match(at, /\nNear-duplicates: "TST_Columns" \/ "TST-COLUMNS"\.\n/);
  assert.match(at, /Rename suggestions \(none applied\): "tst_beams" -> "TST_Beams" \(breaks the naming rule\)\./, "a target that exists already is not suggested");
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
  // 1.13.6: a setup not in the vault goes to the add-in (the model's saved setups come first); unknown there too: blocked.
  const bad = await call("export_deliverables", { ...eb, ifc: { setup: "TST unknown" } });
  assert.ok(bad.isError && ed().length === n0 + 1 && ed().at(-1).args.ifc.setup === "TST unknown" && !ed().at(-1).args.ifc.config, "a setup not in the vault is asked of the model");
  assert.match(bad.content.at(-1).text, /ifc setup TST unknown: no IFC setup by that name in the model, and no config\. How to clear: check the name \(export_deliverables list_ifc_setups: true\): the setup saved in the model is used first, then the vault's/);
  const ms = await call("export_deliverables", { ...eb, ifc: { setup: "TST model setup" }, check_only: true });
  assert.ok(!ms.isError && ed().at(-1).args.ifc.setup === "TST model setup" && !ed().at(-1).args.ifc.config_from, "a setup saved only in the model is used");
  assert.match(ms.content.at(-1).text, /Pre-flight: .*IFC setup TST model setup \(from the model\)/, "the add-in's setup object is shown with its source");
  const ls = await call("export_deliverables", { out_dir: out, vault: eDir, list_ifc_setups: true });
  assert.ok(ed().at(-1).args.list_ifc_setups === true && ed().at(-1).args.check_only === true, "list_ifc_setups is read-only");
  assert.match(ls.content.at(-1).text, /IFC setups \(read-only; ifc\.setup looks in the model first, then the vault\):\n- in the model Test: TST model setup, TST setup\n- in the vault .*: TST setup, tst-setup/);
  const ck = await call("export_deliverables", { ...eb, check_only: true });
  const ca = ed().at(-1).args;
  assert.ok(!ck.isError && ca.check_only === true && ca.nwc.file === "TST_Test_nwc.nwc" && ca.ifc.file === "TST_Test_ifc.ifc" && ca.dwfx.file === "TST_Test_dwfx.dwfx", "names from the vault's naming rule");
  assert.deepEqual(ca.ifc.config, { FileVersion: 21, ExportBaseQuantities: true, SpaceBoundaryLevel: 1, TST_Option: "on" }, "the setup's file, exporter keys read as the add-in's");
  assert.ok(ca.ifc.setup === "TST setup" && ca.ifc.config_from === "vault" && ca.overwrite === false, "a vault setup is marked config_from: vault");
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
  assert.ok(ed().at(-1).args.ifc.file === "Test.ifc" && ed().at(-1).args.ifc.setup === "tst-setup" && !ed().at(-1).args.ifc.config_from, "a given config is not marked from the vault");
  // 1.13.6 dwfx.same_as_folder: the issued .dwg / .pdf stems matched to sheets (number first, then name).
  const issued = path.join(eDir, "issued");
  fs.mkdirSync(issued, { recursive: true });
  for (const f of ["TST-01_Rev A.dwg", "TST-01_Rev A.pdf", "TST Sections.pdf", "TST unknown sheet.dwg", "notes.txt"]) fs.writeFileSync(path.join(issued, f), "x");
  const lv = () => seen.filter((x) => x.command === "list_views");
  const n1 = ed().length;
  const sf = await call("export_deliverables", { out_dir: out, vault: eDir, dwfx: { same_as_folder: issued }, check_only: true });
  assert.ok(sf.isError && ed().length === n1 && lv().at(-1).args.view_type === "DrawingSheet", "an unmatched file blocks before the export");
  assert.match(sf.content[0].text, /^DWFx sheets as in .*issued: 2 of 3 file\(s\) matched[\s\S]*dwfx same_as_folder: 1 file\(s\) match no sheet: TST unknown sheet\. How to clear: rename the files or the sheets, set dwfx\.stemPattern/);
  const sfa = await call("export_deliverables", { out_dir: path.join(eDir, "out-sf"), vault: eDir, dwfx: { same_as_folder: issued, allow_unmatched: true, combined: true, name: "TST set", paper: "sheet" }, check_only: true });
  const sa = ed().at(-1).args.dwfx;
  assert.ok(!sfa.isError && ed().length === n1 + 1, "allow_unmatched exports the matched sheets");
  assert.deepEqual(sa.sheets, ["TST-01", "TST-02"]);
  assert.ok(sa.combined === true && sa.file === "TST set.dwfx" && !("name" in sa) && sa.paper === "sheet", "dwfx.name names the file; combined and paper are passed on");
  // Options given but not honoured: said, or refused.
  const cf = await call("export_deliverables", { out_dir: out, vault: eDir, dwfx: { sheets: ["TST-01"], combined: false } });
  assert.ok(cf.isError && /dwfx\.combined: false is not supported: the add-in always writes ONE combined DWFx/.test(cf.content[0].text) && ed().length === n1 + 1, "combined: false is refused before Revit is asked");
  const both = await call("export_deliverables", { out_dir: path.join(eDir, "out-sf2"), vault: eDir, dwfx: { same_as_folder: issued, allow_unmatched: true, sheets: ["TST-10"], sheet_set: "TST set", name: "TST n", file: "TST f" }, check_only: true });
  const ba = ed().at(-1).args.dwfx;
  assert.ok(!both.isError && ba.sheets.join() === "TST-01,TST-02" && !ba.sheet_set && ba.file === "TST f.dwfx", "same_as_folder wins over sheets and sheet_set; file wins over name");
  assert.match(both.content.at(-1).text, /DWFx: dwfx\.sheets \(1\) IGNORED: same_as_folder wins[\s\S]*DWFx: dwfx\.sheet_set TST set IGNORED[\s\S]*/);
  assert.match(both.content.at(-1).text, /DWFx: dwfx\.name TST n ignored: dwfx\.file TST f names the file\./);
  assert.match(sfa.content.at(-1).text, /DWFx sheets as in .*issued: 2 of 3 file\(s\) matched \(1 by number, 1 by name\); NOT matched: TST unknown sheet; sheets in the model not in the folder: 1 \(TST-10 TST Roof Plan\)\./);
  fs.writeFileSync(path.join(vRules, "export.json"), JSON.stringify({ nameTemplate: "TST_{model}_{format}", ifc: { setups: { "TST setup": "ifc/tst-setup.json" } }, dwfx: { stemPattern: "^X(?<number>TST-\\d+)" } }));
  const sp = await call("export_deliverables", { out_dir: path.join(eDir, "out-sf"), vault: eDir, dwfx: { same_as_folder: issued, allow_unmatched: true }, check_only: true });
  assert.ok(!sp.isError && ed().at(-1).args.dwfx.sheets.includes("TST-01"), "the vault's stemPattern is a first try, the number in the stem still matches");
  // 1.14.8: background: true answers with a job id at once; job_status gives the same reply once the run ends.
  const bg = await call("export_deliverables", { out_dir: path.join(eDir, "out-bg"), vault: eDir, dwfx: { same_as_folder: issued, allow_unmatched: true }, check_only: true, background: true });
  const jobId = bg.content[0].text.match(/as job (\S+)\./)?.[1];
  assert.ok(jobId && !bg.isError, `a background call answers with a job id: ${bg.content[0].text}`);
  let js = "";
  for (let i = 0; i < 100 && !/ done at /.test(js); i++) { await new Promise((r) => setTimeout(r, 50)); js = (await call("job_status", { job_id: jobId })).content[0].text; }
  assert.match(js, / done at [\s\S]*Ready: the same call without check_only exports\./, "job_status returns the finished reply");
  assert.match((await call("job_status", {})).content[0].text, new RegExp(`${jobId}: done`), "job_status lists the jobs");
  assert.match((await call("job_status", { job_id: "../x" })).content[0].text, /not a job id/);
  const { matchStems, folderStems } = await import("../lib/deliverables.js");
  // 1.14.7 #34: printed sets in PDF\ and CAD\ subfolders count; one stem per sheet; Revit's _backup folders are skipped.
  const fsDir = path.join(eDir, "issued-sub");
  for (const [d, f] of [["PDF", "TST-DG-000001.pdf"], ["CAD", "TST-DG-000001.dwg"], ["CAD", "TST-DG-000002.dwg"], ["TST_backup", "TST-DG-000009.pdf"]]) { fs.mkdirSync(path.join(fsDir, d), { recursive: true }); fs.writeFileSync(path.join(fsDir, d, f), ""); }
  assert.deepEqual(folderStems(fsDir), ["TST-DG-000001", "TST-DG-000002"], "subfolders are read, stems de-duplicated, _backup skipped");
  const mN = (stems, sh, o) => matchStems(stems, sh.map(([n, name]) => ({ sheetNumber: n, name: name ?? "" })), o);
  const got = (m) => m.matched.map((x) => `${x.stem}=${x.number}`);
  assert.deepEqual(got(mN(["A-102_Rev-1", "1"], [["1"], ["A-102"]])), ["A-102_Rev-1=A-102", "1=1"], "\"1\" never matches A-102_Rev-1; a whole stem matches");
  assert.deepEqual(mN(["A-102_Rev-1"], [["1"]]).unmatched, ["A-102_Rev-1"]);
  assert.deepEqual(mN(["A-101"], [["101"]]).unmatched, ["A-101"], "101 does not match A-101 without the vault's prefix rule");
  assert.deepEqual(got(mN(["A-101"], [["101"]], { stemPrefix: "[A-Z]-" })), ["A-101=101"], "after the vault's project prefix");
  assert.deepEqual(got(mN(["A-101"], [["101"], ["A-101"]], { stemPrefix: "[A-Z]-" })), ["A-101=A-101"], "the whole number wins over the prefix reading");
  assert.deepEqual(got(mN(["A-101-2"], [["A-101"]])), [], "a separator - must be followed by a non-digit");
  assert.deepEqual(got(mN(["A-101-R2", "A-101.pdf x"], [["A-101"]])), ["A-101-R2=A-101", "A-101.pdf x=A-101"]);
  const amb = mN(["A1.1 Plan"], [["A1"], ["A1.1"]]);
  assert.ok(!amb.matched.length && amb.unmatched[0] === "A1.1 Plan" && amb.ambiguous[0].candidates.join() === "A1,A1.1", "two numbers fit: ambiguous, not guessed");
  assert.deepEqual(mN(["TST anything", "x"], [["Z-901", "مخطط الطابق"], ["Z-902", "---"], ["Z-903", "Ab"], ["Z-904", "平面図"]]).unmatched, ["TST anything", "x"], "empty or short name keys (Arabic, CJK, punctuation) match nothing");
  assert.deepEqual(got(mN(["Plantroom layout"], [["Z-1", "Plan"]])), [], "a name matches whole tokens only");
  assert.deepEqual(got(mN(["TST 03 ground plan issue"], [["Z-1", "Ground Plan"]])), ["TST 03 ground plan issue=Z-1"]);
  const ambN = mN(["Ground Plan and Roof Plan"], [["Z-1", "Ground Plan"], ["Z-2", "Roof Plan"]]);
  assert.ok(!ambN.matched.length && ambN.ambiguous[0].how === "name" && ambN.ambiguous[0].candidates.length === 2, "two names fit: ambiguous");
  assert.deepEqual(got(mN(["TST0101x"], [["101", "TST Other"]], { stemPattern: "^TST0(?<number>\\d{3})" })), ["TST0101x=101"], "the vault's stemPattern places the number");
  const empty = path.join(eDir, "empty-issued");
  fs.mkdirSync(empty, { recursive: true });
  assert.match((await call("export_deliverables", { out_dir: out, vault: eDir, dwfx: { same_as_folder: empty } })).content[0].text, /no \.dwg or \.pdf in /);
  assert.match((await call("export_deliverables", { out_dir: out, vault: eDir, dwfx: { same_as_folder: path.join(eDir, "nope") } })).content[0].text, /does not exist/);
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
  assert.match(Cap.annotationLine({ available: false }), /^Annotation: not available/);
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
  assert.ok(!/Office method:/.test(desc("set_parameters")), "only linked tools");
  assert.ok(desc("register_update").endsWith(" Office method: revit_guide methods/midp-register-update.") && desc("assign_worksets").endsWith(" Office method: revit_guide methods/workset-cleanup."));
  for (const m of ["structural-interference-solving", "midp-register-update", "workset-cleanup"])
    assert.match((await call("revit_guide", { topic: `methods/${m}` })).content[0].text, m === "structural-interference-solving" ? /\nScope: office-wide\.\n/ : /\nScope: general method in the tool, template per project in the vault\.\n/);
  assert.match((await call("revit_guide", { topic: "methods" })).content[0].text, /Office methods \(read one with revit_guide methods\/<name>\):\n- methods\/structural-interference-solving \(v1, 2026-10-20\): tools clash_resolve, run_clash_test, coordination_report, capture_method\.[\s\S]*\n- methods\/midp-register-update \(v1[\s\S]*\n- methods\/workset-cleanup \(v2, 2026-10-20\): tools assign_worksets, workset_plan\./);
  assert.match((await call("revit_guide", { topic: "methods/structural-interference-solving" })).content[0].text, /Join priority Foundation > Column > Wall > Slab > Beam\. Join all touching concrete, not only clashing pairs\./);
  assert.match((await call("revit_guide", { topic: "methods/none" })).content[0].text, /No office method 'methods\/none'/);
  const Cap = await import("../lib/capture.js");
  const md = Cap.caseMarkdown({ label: "TST", model: "Test", startedAt: "a", stoppedAt: "b", diffs: [], journal: { sequence: [], totals: {} }, methods: [{ name: "structural-interference-solving", version: 1 }],
    actions: [{ label: "Floors x Structural Columns: join added", count: 3, ids: ["1 x 2"], rule: { text: "Structural Columns cut Floors", count: 3, of: 3 }, exceptions: [] }] });
  assert.match(md, /Would update office method structural-interference-solving \(v1\) once confirmed \(revit_guide methods\/structural-interference-solving\)\./);
}
// --- 1.12.9 workset audit: empty but expected, the default workset, no rules = not checked; delete_empty keeps the expected ---
{
  const au = await call("assign_worksets", { action: "audit", max_ids: 7, out_dir: path.join(tmp, "wsa") });
  const at = au.content.at(-1).text;
  assert.match(at, /; 2 empty, 2 empty but expected to hold elements, .* elements on the wrong workset not checked: no rules\./);
  assert.match(at, /TST RVT LINKS: empty, but 4 element\(s\) look like they belong here \(4 on "Workset1"\)\. Move them here/);
  assert.match(at, /TST CAD LINKS: empty, but 2 element\(s\) look like they belong here \(2 on "Workset1"\)/, "CAD: the imports only");
  assert.match(at, /Workset1: default workset holds 60 element\(s\) \(34 structural\); assign them by rules\. Top: Structural Columns 30, Floors 20, RVT Links 4, Structural Foundations 4, TST site\.dwg 2\./);
  assert.match(at, /Shared Levels and Grids: default workset holds 2 element\(s\) \(0 structural\)/, "non-datum elements on Shared Levels and Grids");
  const csv = fs.readFileSync(fs.readdirSync(path.join(tmp, "wsa")).filter((f) => f.endsWith(".csv")).map((f) => path.join(tmp, "wsa", f))[0], "utf8");
  assert.ok(/not checked: no rules/.test(csv) && /empty, but 4 element/.test(csv));
  const W = await import("../lib/worksets.js");
  const tok = W.audit({ rules: null, worksets: [{ name: "TST PCC", elements: 0, byCategory: {} }, { name: "Workset1", elements: 4, byCategory: { "Structural Foundations": 4 } }] }, W.namingOf({ naming: { categoryTokens: { PCC: ["Structural Foundations"] } } }));
  assert.ok(tok.rows[0].flags.includes("empty-but-expected"), "naming.categoryTokens extends the table");
  assert.deepEqual(W.expectedCategories("TST EL RVT LINKS"), ["RVT Links"]);
  const de = await call("assign_worksets", { action: "delete_empty", max_ids: 7, explanation: "x", check_only: true });
  assert.deepEqual(seen.filter((x) => x.command === "workset_admin").at(-1).args.worksets, ["TST OLD", "TST PCC"], "only the truly empty ones are sent");
  assert.match(de.content.at(-1).text, /Kept \(empty, but elements look like they belong there; move them rather than delete\): "TST RVT LINKS": 4 on "Workset1"; "TST CAD LINKS": 2 on "Workset1"\./);
}
// --- 1.13.0 workset_plan: a rule-level proposal from the names, confirm (rule-level, group-level, hand-made), apply gated ---
{
  const WP = await import("../lib/worksetplan.js");
  const parseCsvSmoke = (await import("../lib/paramlist.js")).parseCsv;
  const Methods = await import("../lib/methods.js");
  const wpDir = path.join(tmp, "wplan");
  const pr = await call("workset_plan", { action: "propose", out_dir: wpDir });
  const pt = pr.content.at(-1).text;
  assert.ok(!pr.isError, pt);
  const files = JSON.parse(pr.content[0].text).files;
  const [xlsxF, csvF, detF] = files;
  assert.ok(/\.xlsx$/.test(xlsxF) && /\.csv$/.test(csvF) && /-groups\.csv$/.test(detF), "xlsx, the rule-level CSV and the detail CSV");
  assert.equal(seen.filter((x) => x.command === "workset_admin").at(-1).args.action, "facts");
  const rt = await WP.readTable(csvF), dt = await WP.readTable(detF);
  assert.ok(rt.layout === "rules" && dt.layout === "groups");
  const rule = (n) => rt.rows.find((r) => r.proposed === n);
  const mv = (cur, type) => dt.rows.find((r) => r.current === cur && r.type === type);
  // Rule level: one row per target workset, what it holds from its name, counts, visibility.
  assert.match(pt, /- \[By level\] TST-ST-GF COLUMNS: keep; Structural Columns, at level TST-GF\(GROUND FLOOR\)\. Receives 30\. \(merge spelling twins into it: "TST-ST-GF COLUMN "\)/, "spelling twins merged into one canonical name");
  assert.match(pt, /- \[By category\] TST-ST-STAIRS: keep; Stairs, on levels without their own workset \(catch-all\)\. Receives 1\. \(WOULD HIDE 1 element\(s\) in "TST NAVISWORKS"\)/, "a move that hides elements in an export view is flagged");
  assert.match(pt, /- \[Visibility\] TST-ST-HIDE ALL: keep \(manual only\); Visibility workset: assigned by hand only, never by rules\./);
  assert.match(pt, /- \[General\] TST Shared Views, Levels, Grids: keep \(the datum workset: never renamed or deleted\)/, "the datum workset by the add-in's flag, a name with commas kept whole");
  assert.match(pt, /- \[General\] TST_Walls: rename from Workset1;.*the vault's default name \(naming\.defaultName\)/, "1.13.3: Workset1 is renamed only through the vault's naming.defaultName");
  assert.match(pt, /- \[Links\] TST-ST-CAD LINKS: keep; CAD imports .* Receives 1\. \(empty, but elements are expected here \(not deleted\)\)/, "an empty but expected CAD LINKS is never deleted");
  assert.match(pt, /- \[Delete\] TST-ST-OLD: delete;.*\(holds 2 type\(s\): a plan moves instances only, so it is kept unless the types move by hand\)/, "facts types: said on the row");
  assert.match(rule("TST-ST-STAIRS").rule, /"categories":\["Stairs"/);
  assert.ok(!rt.rows.some((r) => /^(rename|delete)/.test(r.action) && r.proposed === "TST Shared Views, Levels, Grids"));
  // Detail: the category decides, then level; material never mismatched; links by code first; CAD never stays on Workset1.
  assert.equal(mv("Workset1", "TST Beam : 300x600").proposed, "TST-ST-RF FRAMING");
  assert.equal(mv("TST-ST-ROOF STEEL", "TST Beam : 300x600").proposed, "TST-ST-RF FRAMING", "concrete beams never stay on (or go to) a STEEL workset");
  assert.ok(!dt.rows.some((r) => r.proposed === "TST-ST-ROOF STEEL"), "nothing concrete goes to ROOF STEEL");
  assert.ok(!mv("TST-ST-ROOF STEEL", "TST UB : 254x146"), "steel beams on the STEEL workset stay");
  assert.equal(mv("Workset1", "TST Stair : A").proposed, "TST-ST-GF STAIRS", "stairs to the level-matched STAIRS workset");
  assert.equal(mv("Workset1", "TST Run : A").proposed, "TST-ST-GF STAIRS", "runs too");
  assert.equal(mv("Workset1", "TST Stair : B").proposed, "TST-ST-STAIRS", "a level without its own stairs workset: the catch-all, never STEEL or FRAMING");
  assert.equal(mv("Workset1", "TST_EL_Model.rvt").proposed, "TST-ST-EL RVT LINKS", "a link by its discipline code first");
  assert.equal(mv("Workset1", "TST_XX_Model.rvt").proposed, "TST-ST-RVT LINKS", "a link without its own discipline: the generic links workset");
  assert.equal(mv("Workset1", "TST_ROOF.dwg").proposed, "TST-ST-CAD LINKS", "CAD imports go to the CAD workset, never stay on Workset1");
  assert.ok(mv("Workset1", "TST Thing : A").proposed === "" && mv("Workset1", "TST Thing : A").action === "review", "no confident home: review");
  assert.ok(!dt.rows.some((r) => r.current === "TST-ST-HIDE ALL" || r.proposed === "TST-ST-HIDE ALL"), "visibility worksets: manual only");
  assert.equal(WP.twinKey("TST-ST-GF COLUMN "), WP.twinKey("tst_st gf columns"));
  const ExcelJS = (await import("exceljs")).default;
  const wbx = new ExcelJS.Workbook(); await wbx.xlsx.readFile(xlsxF);
  assert.ok(wbx.worksheets[0].name === "Worksets" && wbx.worksheets[1].name === "Groups" && wbx.worksheets[0].getColumn(WP.RULE_COLUMNS.indexOf("Rule") + 1).hidden, "two sheets, the rule hidden");
  // Confirm the rule-level file: OK, change to (a rename), skip; the rules are evaluated against the model.
  const raw = parseCsvSmoke(fs.readFileSync(csvF, "utf8"));
  const H = raw[0], dec = H.indexOf(WP.DECISION), pw = H.indexOf("Proposed workset");
  for (const r of raw.slice(1)) r[dec] = r[pw] === "TST-ST-RF FRAMING" ? "change to TST-ST-RF BEAMS" : r[pw] === "TST-ST-ROOF STEEL" ? "skip" : "OK";
  const edited = path.join(wpDir, "edited.csv");
  fs.writeFileSync(edited, raw.map((r) => r.map((c) => /[",\n]/.test(c) ? `"${c.replace(/"/g, '""')}"` : c).join(",")).join("\n"));
  const cf = await call("workset_plan", { action: "confirm", file: edited });
  const ct = cf.content.at(-1).text;
  assert.match(ct, /WORKSET PLAN CONFIRMED for Test from .*\(rule-level: the rules evaluated against the model\): create 0, rename 2, move 9 group\(s\) \(54 elements\), delete 2\./, ct);
  assert.match(ct, /Renames: "Workset1" -> "TST_Walls"; "TST-ST-RF FRAMING" -> "TST-ST-RF BEAMS"\./);
  assert.match(ct, /Left where they are \(no rule fits\):\n- 3 Generic Models on "Workset1"/);
  assert.match(ct, /- Structural Columns\/Columns @ level TST-GF\(GROUND FLOOR\) -> TST-ST-GF COLUMNS\n/, "rule rows become rules");
  assert.match(ct, /- RVT Links type \(\?i\)\(\?<!\[A-Za-z0-9\]\)\(EL\)\(\?!\[A-Za-z0-9\]\) -> TST-ST-EL RVT LINKS/);
  // 1.13.1: the saved regexes match between underscores (\b would not); the merge keeps first-match order and lists overlaps.
  const elRx = new RegExp(WP.wordRegex(["EL"]).replace("(?i)", ""), "i");
  assert.ok(elRx.test("TST_PRJ_MEP_EL.rvt") && elRx.test("TST-EL Model") && !elRx.test("TST_PRJ_MEPEL.rvt") && !elRx.test("TST_ELEC.rvt"), "a link code between underscores");
  const mr = WP.mergeRules({ worksets: { rules: [{ id: "x-cols", workset: "TST-X", category: "Structural Columns" }, { id: "x-steel", workset: "TST-S", material: "steel" }] } },
    [{ id: "n-gf", workset: "TST-GF", category: ["Structural Columns"], level: "^(GF)$" }, { id: "n-zone", workset: "TST-Z", zone: "(?i)(POOL)" }, { id: "n-walls", workset: "TST-W", category: ["Walls"] }]);
  assert.deepEqual(mr.json.worksets.rules.map((r) => r.id), ["x-cols", "n-walls", "x-steel"], "a category rule before the less specific material rule; the overlapping one held back");
  assert.deepEqual(mr.overlaps.map((o) => [o.rule.id, o.shadows.map((h) => h.id), o.shadowedBy.map((h) => h.id)]), [["n-gf", ["x-cols"], []], ["n-zone", [], ["x-steel"]]], "overlaps listed to confirm, both ways");
  assert.deepEqual(WP.mergeRules({ worksets: { rules: [{ id: "x-cols", workset: "TST-X", category: "Structural Columns" }] } }, [{ id: "n-steel", workset: "TST-S", material: "steel" }]).json.worksets.rules.map((r) => r.id), ["x-cols", "n-steel"], "a material-only rule never goes ahead of a category rule");
  assert.deepEqual(WP.mergeRules({ worksets: { rules: [{ id: "x-cols", workset: "TST-X", category: "Structural Columns" }] } }, [{ id: "n-gf", workset: "TST-GF", category: ["Structural Columns"], level: "^(GF)$" }], { overlapsOk: true }).json.worksets.rules.map((r) => r.id), ["n-gf", "x-cols"], "after the user agreed: before the less specific rule");
  const planFile = JSON.parse(cf.content[0].text).files[0];
  const pj0 = JSON.parse(fs.readFileSync(planFile, "utf8"));
  assert.ok(pj0.plan.delete.includes("TST-ST-GF COLUMN ") && pj0.plan.delete.includes("TST-ST-OLD") && !pj0.plan.delete.includes("TST-ST-CAD LINKS"), "twin merged then deleted; empty-but-expected kept");
  assert.ok(pj0.plan.moves.every((m) => m.to !== "TST-ST-ROOF STEEL" && m.to !== "TST-ST-HIDE ALL") && !pj0.plan.moves.some((m) => m.from === "TST-ST-ROOF STEEL"), "a skipped workset row: its elements are left alone");
  // The group-level file and the hand-made prototype (no key, "|" in the type; a count that differs is flagged).
  const rawD = parseCsvSmoke(fs.readFileSync(detF, "utf8"));
  const dd = rawD[0].indexOf(WP.DECISION);
  for (const r of rawD.slice(1)) r[dd] = r[rawD[0].indexOf("Proposed workset")] ? "OK" : "skip";
  const editedD = path.join(wpDir, "edited-groups.csv");
  fs.writeFileSync(editedD, rawD.map((r) => r.map((c) => /[",\n]/.test(c) ? `"${c.replace(/"/g, '""')}"` : c).join(",")).join("\n"));
  assert.match((await call("workset_plan", { action: "confirm", file: editedD })).content.at(-1).text, /CONFIRMED for Test from .*: create 0, rename 0, move 9 group\(s\) \(55 elements\)/);
  const proto = path.join(wpDir, "TST hand-made.csv");
  fs.writeFileSync(proto, "\ufeffModel,Row,Current workset,Category,Type / link / contents,Level,Elements,Proposed workset,Proposed action,Reason,Your decision (OK / change to ... / skip)\n" +
    "Test,MOVE,Workset1,Structural Framing,TST Beam | 300x600,TST-RF(ROOF),12,TST-ST-RF FRAMING,move,x,OK\n" +
    "Test,MOVE,Workset1,Structural Columns,TST Col | 400x400,TST-GF(GROUND FLOOR),29,TST-ST-GF COLUMNS,move,x,OK\n" +
    "TST Other,MOVE,Workset1,Floors,TST Floor | A,TST-GF(GROUND FLOOR),1,TST-ST-GF FLOORS,move,x,OK\n");
  const pc = (await call("workset_plan", { action: "confirm", file: proto })).content.at(-1).text;
  assert.match(pc, /move 1 group\(s\) \(12 elements\)/, "matched by model + workset + category + type + level");
  assert.match(pc, /line 3: MOVE Workset1 Structural Columns @ TST-GF\(GROUND FLOOR\): changed since the proposal: 29 element\(s\) in the file, 30 in the model now/);
  assert.match(pc, /Rows for models not checked .*: TST Other\./);
  // Rules into the vault: shown first, conflicts listed and never overwritten, saved on confirm: true.
  const wv = path.join(tmp, "wplan-vault");
  await call("vault", { action: "init", folder: wv, project: "TST", consent: true });
  const prf = path.join(wv, "ACE Vault", "project-requirements.json");
  const prj = JSON.parse(fs.readFileSync(prf, "utf8"));
  prj.worksets = { rules: [{ id: "plan-tst-st-gf-columns", workset: "TST-OTHER", category: "Structural Columns" }] };
  fs.writeFileSync(prf, JSON.stringify(prj));
  const sv = (await call("workset_plan", { action: "confirm", file: edited, save_rules: true, vault: wv })).content.at(-1).text;
  assert.match(sv, /Would save to .*project-requirements\.json: \d+ new rule\(s\) \(each before the first less specific rule; category-free ones last\), 0 already there, 1 conflict\(s\)/);
  assert.match(sv, /Conflicts \(not overwritten\):\n- Structural Columns\/Columns @ level TST-GF\(GROUND FLOOR\) -> TST-ST-GF COLUMNS; the file has plan-tst-st-gf-columns: Structural Columns -> TST-OTHER/);
  assert.equal(JSON.parse(fs.readFileSync(prf, "utf8")).worksets.rules.length, 1, "nothing written before confirm: true");
  await call("workset_plan", { action: "confirm", file: edited, save_rules: true, vault: wv, confirm: true });
  const after = JSON.parse(fs.readFileSync(prf, "utf8")).worksets.rules;
  const io = after.findIndex((r) => r.workset === "TST-OTHER");
  assert.ok(after.length > 1 && io >= 0 && after[0].source === "workset_plan" && after.findIndex((r) => r.id === "plan-tst-st-stairs") > io, "the conflicting rule kept; specific rules before it, catch-alls after it");
  // Apply: no apply without its dry run; check, dry run, identical apply; borrowed skipped; visibility; another model or a changed count blocks.
  const wadm = () => seen.filter((x) => x.command === "workset_admin");
  const n0 = wadm().length;
  assert.ok((await call("workset_plan", { action: "apply", plan_file: planFile, explanation: "TST plan" })).isError && wadm().slice(n0).every((x) => x.args.action === "facts"), "no apply without its dry run: only the read-only facts are asked");
  const ck = (await call("workset_plan", { action: "apply", plan_file: planFile, check_only: true })).content.at(-1).text;
  assert.match(ck, /WORKSET PLAN in Test \(check, read-only, nothing changed\)\.\nPlan: 13 step\(s\) \(create 0, rename 2, move 9, delete 2\)/, ck);
  assert.match(ck, /Skipped \(borrowed by others.*\n- Workset1\|Structural Columns.*: 2 element\(s\), held by jane/);
  assert.match(ck, /Visibility in the export views \("TST NAVISWORKS"\):\n- WOULD HIDE 1 element\(s\) in "TST NAVISWORKS": Workset1\|Stairs\|TST Stair : B.* -> TST-ST-STAIRS/);
  const dr = await call("workset_plan", { action: "apply", plan_file: planFile, dry_run: true, explanation: "TST plan" });
  assert.ok(!dr.isError && /PREVIEW ONLY/.test(dr.content.at(-1).text) && wadm().at(-1).args.action === "apply_plan" && wadm().at(-1).args.dry_run === true);
  assert.ok(wadm().at(-1).args.workset_plan?.moves?.length && !("plan" in wadm().at(-1).args), "1.13.1: the plan travels as workset_plan, never as the panel's plan");
  assert.match(dr.content.at(-1).text, /Kept \(not empty after the moves\): "TST-ST-OLD" 0 \(holds 2 types \(a plan moves instances only\)\)/);
  // 1.13.6: moves that would hide elements are held, grouped by sheet and view; status names the switch.
  assert.match(dr.content.at(-1).text, /Visibility on sheets \(moved elements a view on a sheet would show differently\): 1 element\(s\) HELD in 1 move\(s\), they stay where they are; after the user agreed: allow_visibility_change: true, or the view \/ workset names:\n- sheet TST-01 - TST Ground Plan:\n  - view "TST L1 Plan": 1 hidden; HELD 1 \(1 Stairs\): the target workset is hidden in this view\nExport views \(reported, not held\):\n- "TST NAVISWORKS": 1 hidden \(1 Stairs\)/, "held moves grouped by sheet and view");
  assert.match(dr.content.at(-1).text, /Visibility not checked in: "TST Broken View"\./);
  assert.ok(!("allow_visibility_change" in wadm().at(-1).args), "nothing sent without allow_visibility_change");
  assert.match(dr.content.at(-1).text, /Would move \(not previewed: not yours yet\): 1 group\(s\), 1 element\(s\), 1 link type\(s\)\. The apply borrows those worksets first\./);
  const ap = await call("workset_plan", { action: "apply", plan_file: planFile, explanation: "TST plan" });
  const apT = ap.content.slice(1).map((c) => c.text).join("\n");
  assert.ok(!ap.isError && /applied as ONE undo step/.test(apT) && /Changes: renamed 2, moved 52 element\(s\), deleted 2 done\./.test(apT), apT);
  // 1.13.3: every apply writes a record (what ACE moved, by row) and shows the status again.
  const recF = apT.match(/Apply record: (\S+worksets-applied\.json)/)?.[1];
  const recJ = recF && JSON.parse(fs.readFileSync(recF, "utf8"));
  assert.ok(recJ?.records?.length === 1 && recJ.records[0].planId && recJ.records[0].rows.length === 9 && recJ.records[0].rows.every((r) => r.from && r.to && r.identity && r.origin && r.identity !== "|||||"), "the apply record: date, plan id, rows with from / to");
  const brow = recJ.records[0].rows.find((r) => r.key.startsWith("Workset1|Structural Columns|TST Col : 400x400"));
  assert.ok(brow.elements === 28 && brow.skippedIds.join() === "501,502", "a partly borrowed row records the moved ones and lists the skipped ids");
  assert.match(apT, /Status after the apply:\nWORKSET STATUS for Test \(read-only; standard: /, "status runs again after the apply");
  assert.ok(wadm().filter((x) => x.args.action === "apply_plan").at(-1).args.expect_plan && wadm().filter((x) => x.args.action === "apply_plan").at(-1).args.expect_model === "Test", "the apply names its plan and model");
  const other = path.join(wpDir, "other.json"); fs.writeFileSync(other, JSON.stringify({ ...pj0, model: "TST Other" }));
  assert.match((await call("workset_plan", { action: "apply", plan_file: other, check_only: true })).content[0].text, /it is for TST Other, the active model is Test/);
  const drift = path.join(wpDir, "drift.json"); const pj = JSON.parse(JSON.stringify(pj0)); pj.plan.moves[0].elements += 1; fs.writeFileSync(drift, JSON.stringify(pj));
  const dtr = await call("workset_plan", { action: "apply", plan_file: drift, dry_run: true, explanation: "TST plan" });
  assert.ok(dtr.isError && /planned, \d+ found/.test(dtr.content.map((c) => c.text).join("\n")), "a changed count blocks the whole plan");
  // 1.13.6: allow_visibility_change is passed on and is part of the change identity.
  const avd = await call("workset_plan", { action: "apply", plan_file: planFile, dry_run: true, allow_visibility_change: ["TST L1 Plan"], explanation: "TST plan" });
  assert.ok(!avd.isError && JSON.stringify(wadm().at(-1).args.allow_visibility_change) === '["TST L1 Plan"]' && /view "TST L1 Plan": 1 hidden; accepted 1/.test(avd.content.at(-1).text) && !/HELD/.test(avd.content.at(-1).text), "an allowed view: accepted, not held");
  const nApply = wadm().filter((x) => x.args.action === "apply_plan" && !x.args.dry_run && !x.args.check_only).length;
  const avx = await call("workset_plan", { action: "apply", plan_file: planFile, allow_visibility_change: true, explanation: "TST plan" });
  assert.ok(avx.isError && wadm().filter((x) => x.args.action === "apply_plan" && !x.args.dry_run && !x.args.check_only).length === nApply, "another allow_visibility_change needs its own dry run");
  const wst = (await call("workset_plan", { action: "status", vault: wv })).content.map((c) => c.text).join("\n");
  assert.match(wst, /At apply, a moved element that a view on a sheet would show differently is held \(stays\) until the user agrees \(allow_visibility_change\)\./);
  assert.ok(after.filter((r) => r.source === "workset_plan").every((r) => r.models?.join() === "Test") && !/Rules skipped/.test(wst), "G9: rules saved by workset_plan name their models; in that model none is skipped");
  // G9: the standard's rules for this model only (status and propose); the reply names the skipped ones.
  const g9v = path.join(tmp, "TST_G9", "ACE Vault"); fs.mkdirSync(g9v, { recursive: true });
  fs.writeFileSync(path.join(g9v, "project-requirements.json"), JSON.stringify({ worksets: { rules: [{ id: "g9-beams", workset: "TST-ST-BEAMS", category: ["Structural Framing"], models: "Test" }, { id: "g9-arc", workset: "TST-ARC-DOORS", category: ["Doors"], models: ["TST_ARC*"] }] } }));
  const g9s = (await call("workset_plan", { action: "status", vault: g9v, out_dir: path.join(tmp, "g9stat") })).content.at(-1).text;
  assert.ok(/Rules skipped for Test \(their models key does not name it\): 1 of 2: g9-arc \(models TST_ARC\*\)\./.test(g9s) && !/TST-ARC-DOORS/.test(g9s), g9s);
  const g9p = (await call("workset_plan", { action: "propose", vault: g9v, out_dir: path.join(tmp, "g9prop") })).content.at(-1).text;
  assert.match(g9p, /Rules skipped for Test \(their models key does not name it\): 1 of 2: g9-arc \(models TST_ARC\*\)\./);
  // 1.13.1: an xlsx is read on both sheets; on both, the Groups sheet wins per group; unused decisions are said.
  const wbe = new ExcelJS.Workbook(); await wbe.xlsx.readFile(xlsxF);
  const [shW, shG] = wbe.worksheets;
  const hdr = (sh, name) => sh.getRow(1).values.indexOf(name);
  shW.eachRow((row, i) => { if (i > 1) row.getCell(hdr(shW, WP.DECISION)).value = "OK"; });
  shG.eachRow((row, i) => {
    if (i === 1) return;
    const type = row.getCell(hdr(shG, "Type / link / contents")).value, cur = row.getCell(hdr(shG, "Current workset")).value;
    if (cur === "Workset1" && type === "TST Beam : 300x600") row.getCell(hdr(shG, WP.DECISION)).value = "skip";
    if (cur === "Workset1" && type === "TST Stair : B") row.getCell(hdr(shG, WP.DECISION)).value = "maybe";
  });
  const both = path.join(wpDir, "TST both sheets.xlsx");
  await wbe.xlsx.writeFile(both);
  const bt = (await call("workset_plan", { action: "confirm", file: both })).content.at(-1).text;
  assert.match(bt, /1 group\(s\) decided on both sheets: the Groups sheet wins for them\./, bt);
  assert.match(bt, /Groups sheet: 1 decision\(s\) not used \(flagged above, or for models not open\)\./);
  const bplan = JSON.parse(fs.readFileSync(bt.match(/Plan file\(s\):\n- (.*\.json)/)[1], "utf8"));
  assert.ok(!bplan.plan.moves.some((m) => m.key === "Workset1|Structural Framing|TST Beam : 300x600|TST-RF(ROOF)|concrete||") && bplan.layout === "both sheets", "the Groups sheet's skip wins over the rule");
  // Twin models (the same facts): proposed once, "same as".
  const tw = (await call("workset_plan", { action: "propose", paths: ["C:/TST/a/TST_WP1.rvt", "C:/TST/b/TST_WP2.rvt"], out_dir: path.join(tmp, "wplan2"), views: ["TST NWC"] })).content.at(-1).text;
  fakeOpened = null;
  assert.match(tw, /TST_WP2: same as TST_WP1 \(identical facts\), proposed once\./);
  assert.ok(seen.filter((x) => x.command === "open_model").slice(-2).every((x) => x.args.detach === "preserve_worksets") && seen.filter((x) => x.command === "open_model").at(-2).args.close_previous === false, "the user's model is never closed");
  assert.deepEqual(seen.filter((x) => x.command === "workset_admin").at(-1).args.views, ["TST NWC"]);
  assert.match(Methods.descriptionLine("workset_plan"), /methods\/workset-cleanup/);
  // --- 1.13.3: the name parser (whole level codes, head category words, vault tokens), deletes, Workset1, sketches ---
  {
    const lvls = ["A-01(FIRST FLOOR)", "B-01(TST PODIUM)", "C-GF(GROUND FLOOR)", "C-FDN(FOUNDATION)", "C-RF(ROOF)", "C-B1(BASEMENT)"];
    const cx = WP.nameCtx(lvls, { categoryTokens: { FRAMING: ["Structural Framing", "Floors"], "GROUND BEAMS": ["Structural Framing"] }, levelAliases: { L1: ["A-01(FIRST FLOOR)"] } });
    const pn = (n, c = cx) => WP.parseName(n, c);
    assert.deepEqual(pn("TST-ST-BASE COLUMNS").levels, [], "BASE is no level (no level code BASE)");
    assert.deepEqual(pn("TST-ST-GRID-B").levels, [], "a single letter is never a level");
    assert.ok(!pn("TST-ST-GROUND BEAMS").levels.length && pn("TST-ST-GROUND BEAMS").categories.join() === "Structural Framing", "GROUND BEAMS: the vault's category phrase, not the level GROUND FLOOR");
    assert.ok(!WP.parseName("TST-ST-GROUND BEAMS", WP.nameCtx(lvls, null)).levels.length, "GROUND alone never matches the phrase GROUND FLOOR");
    assert.deepEqual(pn("TST-ST-01 COLUMNS").levels, ["A-01(FIRST FLOOR)", "B-01(TST PODIUM)"], "01 is a level when a level code 01 exists (both buildings)");
    assert.deepEqual(pn("TST-ST-GROUND FLOOR WALLS").levels, ["C-GF(GROUND FLOOR)"], "the bracketed words as a phrase");
    assert.deepEqual(pn("TST-ST-L1 WALLS").levels, ["A-01(FIRST FLOOR)"], "a vault level alias");
    const pool = pn("TST-POOL & Pump Room");
    assert.ok(!pool.categories.includes("Rooms") && pool.words.includes("ROOM"), "a category word inside a zone phrase is a zone word");
    assert.ok(pn("TST-ST-WALLS & COLUMNS").categories.includes("Walls") && pn("TST-ST-WALLS & COLUMNS").categories.includes("Structural Columns"), "a list of categories stays categories");
    assert.deepEqual(pn("TST-ST-FRAMING").categories, ["Structural Framing", "Floors"], "vault categoryTokens: one token, several categories");
    assert.deepEqual(pn("TST-ST-FOUNDATIONS").categories, ["Structural Foundations"], "FOUNDATIONS is the category even with a FOUNDATION level");
    assert.deepEqual(pn("TST-ST-FOUNDATION").categories, ["Structural Foundations"], "a level word that is also the only category word: the category");
    assert.ok(pn("TST-ST-ROOF STEEL").levels.includes("C-RF(ROOF)") && pn("TST-ST-ROOF STEEL").material === "steel", "ROOF with a material: the level");
    assert.deepEqual(WP.levelTokens([{ name: "TST level", code: "07", phrase: "TST TOP" }])[0].codes, new Set(["7"]), "the add-in's level code is used when sent");
    const W13 = await import("../lib/worksets.js");
    const nm = W13.namingOf({ naming: { categoryTokens: { FRAMING: ["Structural Framing", "Floors"] }, levelAliases: { L1: ["A-01(FIRST FLOOR)"] }, manualOnly: ["TST-MASK"], defaultName: "TST-GENERAL" } });
    assert.ok(nm.categoryTokens.FRAMING.length === 2 && nm.levelAliases.L1 && nm.manualOnly[0] === "TST-MASK" && nm.defaultName === "TST-GENERAL", "the vault naming keeps categoryTokens, levelAliases, manualOnly, defaultName");
    assert.throws(() => W13.namingOf({ naming: { levelAliases: ["x"] } }), /levelAliases must be/);
    // FOUNDATIONS through propose: the foundations go to it (not "category has no workset of its own").
    const G = (workset, category, type, level, elements, extra = {}) => ({ workset, category, type, level, material: "concrete", zone: "", option: "", elements, borrowed: 0, ...extra });
    const fx13 = { model: "TST_P13", levels: lvls.map((name) => ({ name })), worksets: [{ name: "Workset1", elements: 25, isDefault: true }, { name: "TST-ST-FOUNDATIONS", elements: 0 }, { name: "TST-ST-WALLS", elements: 0 },
      { name: "TST-ST-GF STAIRS", elements: 0 }, { name: "TST-ST-RF COLUMNS", elements: 0 }, { name: "TST-POOL & Pump Room", elements: 0 }, { name: "TST-ST-CAD LINKS", elements: 0 }],
      groups: [G("Workset1", "Structural Foundations", "TST Pad : A", "C-FDN(FOUNDATION)", 4), G("Workset1", "CAD Imports", "TST_site.dwg", "", 1), G("Workset1", "<Sketch>", "", "", 9), G("Workset1", "<Stair/Ramp Sketch: Boundary>", "", "", 3), G("Workset1", "Legend Components", "", "", 8)] };
    const p13 = WP.propose([{ facts: fx13, naming: { rename: [{ from: "Workset1", to: "TST-X" }] } }]);
    const rr = (n) => p13.rules.find((r) => r["Proposed workset"] === n);
    assert.match(rr("TST-ST-FOUNDATIONS")["What it holds (from its name)"], /^Structural Foundations/, "FOUNDATIONS -> Structural Foundations");
    assert.equal(p13.detail.find((d) => d.Category === "Structural Foundations")["Proposed workset"], "TST-ST-FOUNDATIONS");
    assert.ok(rr("TST-ST-WALLS").Action === "keep" && /catch-all for its category/.test(rr("TST-ST-WALLS").Note), "an empty catch-all (category, no level) is kept");
    assert.equal(rr("TST-ST-RF COLUMNS").Action, "delete", "an empty level workset nothing is expected for may go");
    assert.ok(rr("TST-ST-CAD LINKS").Action === "keep", "empty but expected (imports on Workset1): never deleted");
    assert.ok(rr("Workset1").Action === "keep Workset1 (default)" && rr("Workset1")["Proposed workset"] === "Workset1", "no rename of Workset1 without naming.defaultName (a rename list is not enough)");
    assert.ok(!p13.detail.some((d) => /^</.test(d.Category) || d.Category === "Legend Components"), "sketches and legend components never get a row");
    const p13b = WP.propose([{ facts: fx13, naming: { defaultName: "TST-GENERAL" } }]);
    assert.ok(p13b.rules.find((r) => r["Proposed workset"] === "TST-GENERAL")?.Action === "rename from Workset1", "naming.defaultName opts in to the rename");
    // Decision "skip" on the default row keeps Workset1.
    assert.equal(WP.rulesFromRows([{ line: 2, proposed: "Workset1", action: "keep Workset1 (default)", decision: "skip", rule: JSON.stringify({ workset: "Workset1", isDefault: true, categories: [], levels: [], words: [], codes: [] }) }]).rules.length, 0);
  }
  // --- 1.13.3: status (four states), apply records, reset end state, partial apply, already done, pending, refresh, standard ---
  const Wk13 = await import("../lib/worksets.js");
  {
    const G = (workset, category, type, level, elements, extra = {}) => ({ workset, category, type, level, material: "concrete", zone: "", option: "", elements, borrowed: 0, ...extra });
    const fs13 = { model: "TST_S13", levels: [{ name: "TST-GF(GROUND FLOOR)" }], worksets: [{ name: "Workset1", elements: 5, isDefault: true }, { name: "TST-ST-GF COLUMNS", elements: 10 }, { name: "TST-ST-WALLS", elements: 6 }, { name: "TST-ST-BEAMS", elements: 0 }],
      groups: [G("TST-ST-GF COLUMNS", "Structural Columns", "TST Col : A", "TST-GF(GROUND FLOOR)", 10), G("TST-ST-WALLS", "Walls", "TST Wall : A", "TST-GF(GROUND FLOOR)", 4), G("TST-ST-WALLS", "Walls", "TST Wall : B", "TST-GF(GROUND FLOOR)", 2),
        G("Workset1", "Structural Framing", "TST Beam : A", "TST-GF(GROUND FLOOR)", 5)] };
    const recs = [{ at: "2026-10-20_1000", planId: "wp-TST1", rows: [{ identity: "Structural Columns|TST Col : A|TST-GF(GROUND FLOOR)|concrete||", origin: "Workset1", from: "Workset1", to: "TST-ST-GF COLUMNS" },
      { identity: "Walls|TST Wall : B|TST-GF(GROUND FLOOR)|concrete||", origin: "Workset1", from: "Workset1", to: "TST-ST-GF COLUMNS" }] }];
    const s13 = WP.statusOf(fs13, { ctx: WP.nameCtx(fs13.levels, null, fs13.worksets.map((w) => w.name)), records: recs, pending: [{ id: 77, owner: "jane", to: "TST-ST-BEAMS", model: "TST_S13" }] });
    const stOf = (type) => s13.groups.find((g) => g.type === type).state;
    assert.equal(stOf("TST Col : A"), "applied", "applied by ACE: it sits where ACE put it");
    assert.equal(stOf("TST Wall : B"), "changed after apply", "ACE moved it, it sits elsewhere now");
    assert.equal(stOf("TST Wall : A"), "correct already", "correct, not by ACE");
    assert.equal(stOf("TST Beam : A"), "still to do");
    assert.ok(s13.pct === 100 * 16 / 21 | 0 || s13.pct === Math.round(100 * 16 / 21), "the headline: % correctly assigned");
    const s13t = WP.statusText(s13);
    assert.match(s13t, /^WORKSET STATUS for TST_S13 \(read-only; standard: the workset names \(no standard rules\)\): 76% correctly assigned \(16 of 21 element\(s\)\); wrong 5;/);
    assert.match(s13t, /Groups: applied by ACE 1 \(last 2026-10-20_1000, plan wp-TST1\); changed after apply 1; correct already 1; still to do 1\./);
    assert.match(s13t, /"Workset1": 5 wrong: 5 Structural Framing @ TST-GF\(GROUND FLOOR\) -> "TST-ST-BEAMS"/, "where each wrong group should go");
    assert.match(s13t, /Empty: "TST-ST-BEAMS" \(empty, expected\)\./);
    assert.match(s13t, /Pending actions \(borrowed by others.*\n- 1 element\(s\) held by jane -> "TST-ST-BEAMS": ids 77/);
    // propose does not re-propose applied rows; a changed-after-apply row is flagged for review.
    const pa = WP.propose([{ facts: { ...fs13, worksets: [...fs13.worksets, { name: "Workset1x", elements: 0 }] }, records: recs }]);
    assert.ok(pa.counts.applied.groups === 1 && pa.counts.applied.changed === 1 && pa.detail.some((d) => /^changed after apply: ACE moved it to "TST-ST-GF COLUMNS"/.test(d.Reason) && d["Proposed action"] === "review"));
    // A standard: rules first (clause named), missing worksets, not in the standard.
    const s13s = WP.statusOf(fs13, { std: { label: "TST standard", rules: [{ workset: "TST-ST-BEAMS", category: ["Structural Framing"], clause: "TST BEP 4.2" }, { workset: "TST-ST-SLABS", category: ["Floors"], clause: "TST BEP 4.3" }] }, ctx: WP.nameCtx(fs13.levels, null) });
    assert.ok(s13s.missing.includes("TST-ST-SLABS") && s13s.rows.find((r) => r.workset === "TST-ST-WALLS").inStandard === false && /\(TST BEP 4\.2\)/.test(s13s.rows.find((r) => r.workset === "Workset1").to[0].why), "standard: missing, not in it, the clause");
    // skipDone: a move done by an earlier apply is dropped (and said); partly done keeps the rest.
    const sdn = WP.skipDone({ create: [], rename: [], delete: [], moves: [{ key: "Workset1|Structural Columns|TST Col : A|TST-GF(GROUND FLOOR)|concrete||", category: "Structural Columns", type: "TST Col : A", level: "TST-GF(GROUND FLOOR)", material: "concrete", zone: "", option: "", from: "Workset1", to: "TST-ST-GF COLUMNS", elements: 10 }] }, fs13);
    assert.ok(!sdn.plan.moves.length && sdn.already[0].elements === 10, "already at its target: skipped");
    // filterPlan: only categories / worksets / rows / models; renames a kept move needs stay.
    const pl = { create: ["TST-NEW"], rename: [{ from: "TST-OLD", to: "TST-ST-WALLS2" }], delete: ["TST-GONE"], moves: [{ key: "k1", category: "Walls", from: "Workset1", to: "TST-ST-WALLS2", elements: 2, row: 3 }, { key: "k2", category: "Floors", from: "Workset1", to: "TST-NEW", elements: 1, row: 4 }] };
    const fp = WP.filterPlan(pl, { categories: ["Walls"] }, "TST_S13");
    assert.ok(fp.plan.moves.length === 1 && fp.plan.rename.length === 1 && !fp.plan.create.length && !fp.plan.delete.length, "categories: its moves and the renames they need");
    assert.equal(WP.filterPlan(pl, { rows: [4] }, "TST_S13").plan.create[0], "TST-NEW", "rows: the create its move needs");
    assert.ok(WP.filterPlan(pl, { models: ["TST other"] }, "TST_S13").outOfModel);
    // Pending: merged by id, done ids dropped.
    assert.deepEqual(WP.mergePending([{ id: 1, owner: "a" }, { id: 2 }], [{ id: 2, owner: "b" }, { id: 3 }], { model: "M", done: [1] }).map((p) => [p.id, p.owner ?? null]), [[2, "b"], [3, null]]);

    // The tool: reset end state, only, pins, pending retry, status files and the dashboard tile.
    const wadm13 = () => seen.filter((x) => x.command === "workset_admin" && x.args.action === "apply_plan");
    const rsc = (await call("workset_plan", { action: "apply", plan_file: planFile, reset: "default", check_only: true })).content.at(-1).text;
    const rq = wadm13().at(-1).args;
    assert.ok(rq.keep_worksets.includes("TST-ST-HIDE ALL") && rq.keep_keys.some((k) => k.startsWith("Workset1|Generic Models")) && rq.keep_keys.every((k) => !rq.workset_plan.moves.some((m) => m.key === k)), "groups that stay are named (keep_keys), so the add-in's reset never moves them");
    assert.ok(rq.reset_to === "TST_Walls" && Array.isArray(rq.keep_worksets) && new Set(rq.workset_plan.moves.map((m) => m.key)).size === rq.workset_plan.moves.length, "reset: the default by its final name, kept worksets, each group moves once");
    assert.ok(!rq.workset_plan.moves.some((m) => m.key.startsWith("TST-ST-HIDE ALL|")) && rq.workset_plan.moves.every((m) => m.from !== m.to), "manual-only groups stay; no move to where a group already is");
    assert.ok(rq.workset_plan.moves.some((m) => m.key.startsWith("TST-ST-ROOF STEEL|Structural Framing|TST UB") && m.to === "TST-ST-RF BEAMS"), "ROOF STEEL was skipped: its steel beams go where the confirmed rules put them (straight, one move)");
    assert.ok(!rq.workset_plan.moves.some((m) => m.key.startsWith("Workset1|Generic Models")), "what no rule takes and already sits on the default stays");
    assert.match(rsc, /Reset to "TST_Walls" \(end state, each element moves once\): 0 element\(s\) in 0 group\(s\) no rule takes go to the default \(3 already there\); kept: datum 0, manual-only 5/);
    assert.match(rsc, /Reset to "TST_Walls"[^\n]*\. A moved element that a view on a sheet would show differently is held \(allow_visibility_change, after the user agreed\)\./, "the reset line names the visibility hold");
    const rsu = WP.resetPlan({ plan: { create: [], rename: [], moves: [], delete: [] }, rules: [{ workset: "TST-ST-GF COLUMNS", from: "TST-ST-GF COLUMNS", categories: ["Structural Columns"], levels: [], words: [], codes: [] }] },
      { worksets: [{ name: "Workset1", isDefault: true, elements: 0 }, { name: "TST-ST-GF COLUMNS", elements: 3 }, { name: "TST-ST-ODD", elements: 6 }],
        groups: [{ workset: "TST-ST-ODD", category: "Generic Models", type: "TST X", level: "", elements: 4 }, { workset: "TST-ST-ODD", category: "Structural Columns", type: "TST C", level: "", elements: 2 }] });
    assert.deepEqual(rsu.plan.moves.map((m) => [m.from, m.to, m.elements]), [["TST-ST-ODD", "Workset1", 4], ["TST-ST-ODD", "TST-ST-GF COLUMNS", 2]], "reset: straight to the final workset, the rest to the default");
    assert.deepEqual(rsu.endState.filter((e) => e.workset !== "TST-ST-GF COLUMNS").map((e) => [e.workset, e.before, e.after]), [["Workset1", 0, 4], ["TST-ST-ODD", 6, 0]], "the end state per workset");
    assert.match(rsc, /End state per workset \(before -> after\):\n/, "the end state is shown before the apply");
    const gOnly = path.join(wpDir, "groups-only.json"); const { rules: _r, ...pjNoRules } = JSON.parse(fs.readFileSync(planFile, "utf8")); fs.writeFileSync(gOnly, JSON.stringify(pjNoRules));
    const gor = await call("workset_plan", { action: "apply", plan_file: gOnly, reset: "default", check_only: true });
    assert.ok(gor.isError && /reset: "default" needs a rule-level plan/.test(gor.content[0].text), "reset needs the rules: never everything to the default");
    const oc = (await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs"] }, check_only: true })).content.at(-1).text;
    const oq = wadm13().at(-1).args;
    assert.ok(oq.workset_plan.moves.length && oq.workset_plan.moves.every((m) => m.category === "Stairs") && /Partial apply \(only \{"categories":\["Stairs"\]\}\): 2 move\(s\) of /.test(oc), oc);
    assert.ok(!("pending_retry" in oq), "only Stairs: the pending columns are out of its scope");
    await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs", "Structural Columns"] }, check_only: true });
    assert.ok(wadm13().at(-1).args.pending_retry?.some((p) => p.id === 501), "pending ids in scope are retried first");
    const dro = await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs", "Structural Columns"] }, dry_run: true, explanation: "TST stairs only" });
    assert.match(dro.content.at(-1).text, /Pinned elements: 3 \(moved directly 2; unpinned, moved, re-pinned: 1/, "pins: the add-in's counts");
    assert.match(dro.content.at(-1).text, /Per workset \(before -> after\):\n- "Workset1": 54 -> 3/, "the add-in's per-workset counts in the dry run");
    const apo = await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs", "Structural Columns"] }, explanation: "TST stairs only" });
    const apoT = apo.content.slice(1).map((c) => c.text).join("\n");
    assert.ok(!apo.isError && /applied as ONE undo step/.test(apoT), apoT);
    assert.match(apoT, /Pending retried: 1 moved now, 1 still pending \(still borrowed by jane\)\./, "the add-in's retried [{ id, ok, why }]");
    const pendF = apoT.match(/Pending actions: (\S+pending-actions\.json)/)?.[1];
    const pendJ = pendF && JSON.parse(fs.readFileSync(pendF, "utf8"));
    assert.ok(pendJ && !pendJ.actions.some((p) => p.id === 501) && pendJ.actions.some((p) => p.id === 502 && p.owner === "jane"), "a retried id that moved is dropped; one still held stays");
    assert.match(apoT, /Follow-up: workset_plan \{ action: "apply", plan_file: /, "a ready-to-run follow-up");
    // drop: true (permanent failure) leaves the pending list and is said.
    fs.writeFileSync(pendF, JSON.stringify({ ...pendJ, actions: [...pendJ.actions, { id: 503, owner: "jane", category: "Stairs", from: "Workset1", to: "TST-ST-GF STAIRS", model: "Test" }] }));
    await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs"] }, dry_run: true, explanation: "TST retry" });
    const apd = (await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: ["Stairs"] }, explanation: "TST retry" })).content.slice(1).map((c) => c.text).join("\n");
    assert.ok(/Dropped from the pending actions \(cannot be done: not found\): 1 element\(s\), ids 503\./.test(apd) && !JSON.parse(fs.readFileSync(pendF, "utf8")).actions.some((p) => p.id === 503), apd);
    // A borrowed-only plan (no step to run): the pending actions are still written.
    const bOnly = path.join(wpDir, "borrowed-only.json"); const pjB = JSON.parse(fs.readFileSync(planFile, "utf8"));
    fs.writeFileSync(bOnly, JSON.stringify({ ...pjB, plan: { create: [], rename: [], delete: [], moves: pjB.plan.moves.filter((m) => m.key.startsWith("Workset1|Structural Columns|TST Col : 400x400")) } }));
    fs.writeFileSync(pendF, JSON.stringify({ ...pendJ, actions: [] }));
    const bo = (await call("workset_plan", { action: "apply", plan_file: bOnly, max_ids: 3, dry_run: true, explanation: "TST borrowed" })).content.map((c) => c.text).join("\n");
    assert.ok(/Nothing to do in Test/.test(bo) && JSON.parse(fs.readFileSync(pendF, "utf8")).actions.length === 2 && /Pending actions \(borrowed/.test(bo), bo);
    const recAll = JSON.parse(fs.readFileSync(apoT.match(/Apply record: (\S+)/)[1], "utf8")).records;
    assert.ok(recAll.length >= 2 && recAll.some((r) => r.only?.categories?.[0] === "Stairs" && r.retry?.includes(501)) && !recAll.some((r) => r.rows.some((x) => x.identity === "|||||")), "the partial apply has its own record; the retry in its own section");
    const st = await call("workset_plan", { action: "status", out_dir: path.join(tmp, "wstat") });
    const stT = st.content.at(-1).text;
    assert.ok(!st.isError && /WORKSET STATUS for Test \(read-only; standard: the project's confirmed list/.test(stT) && /Pending actions \(borrowed/.test(stT), stT);
    assert.ok(fs.readdirSync(path.join(tmp, "wstat")).some((f) => f.endsWith(".html")) && fs.readdirSync(path.join(tmp, "wstat")).some((f) => f.endsWith(".csv")), "status: HTML and CSV");
    const md13 = await call("model_dashboard", {});
    assert.match(md13.content.at(-1).text, /^Worksets: \d+% correct \(/, "the dashboard tile");
    // Review fixes: reset + only keeps every group outside the filter where it is; only with empty arrays is refused.
    await call("workset_plan", { action: "apply", plan_file: planFile, reset: "default", only: { categories: ["Structural Columns"] }, check_only: true });
    const ro = wadm13().at(-1).args;
    const gk = ro.workset_plan.moves;
    assert.ok(gk.every((m) => m.category === "Structural Columns") && ro.keep_keys.some((k) => k.startsWith("TST-ST-ROOF STEEL|Structural Framing|TST UB")) && ro.keep_keys.some((k) => k.startsWith("Workset1|Stairs")),
      "reset + only: groups outside the filter (framing, stairs) are kept, never swept to the default");
    const emp = await call("workset_plan", { action: "apply", plan_file: planFile, only: { categories: [] }, check_only: true });
    assert.ok(emp.isError && /only: categories is empty/.test(emp.content[0].text), "only with an empty list is refused, not read as no filter");
    await call("workset_plan", { action: "apply", plan_file: planFile, only: { rows: [9999] }, check_only: true });
    assert.ok(!("pending_retry" in wadm13().at(-1).args), "only by rows: pending entries are out of its scope");
    // appliedState: identity AND workset; new elements of that type elsewhere are classified as usual.
    const idx = WP.appliedIndex([{ at: "T1", planId: "p1", rows: [{ identity: "Walls|TST W|L1|||", origin: "Workset1", from: "Workset1", to: "TST-A" }] }]);
    const gA = { workset: "TST-A", category: "Walls", type: "TST W", level: "L1", material: "", zone: "", option: "", elements: 3 }, gB = { ...gA, workset: "TST-B", elements: 2 };
    assert.equal(WP.appliedState(gA, idx, [gA, gB]).kind, "applied");
    assert.equal(WP.appliedState(gB, idx, [gA, gB]), null, "new elements of that type on another workset: not 'changed after apply'");
    assert.equal(WP.appliedState(gB, idx, [gB]).kind, "changed", "ACE's elements left its target: changed after apply");
    const sDiff = WP.statusOf({ model: "M", worksets: [{ name: "TST-A", elements: 3 }], groups: [gA] }, { std: { label: "TST", rules: [{ workset: "TST-C", category: ["Walls"], clause: "TST 1" }] }, records: [{ at: "T1", planId: "p1", rows: [{ identity: "Walls|TST W|L1|||", origin: "Workset1", from: "Workset1", to: "TST-A" }] }] });
    assert.match(WP.statusText(sDiff), /Applied by ACE, now differs from the standard:\n- 3 Walls @ L1 on "TST-A" \(ACE T1\); the standard says "TST-C"/);
    assert.deepEqual([...WP.levelTokens(["ST-L1-01"])[0].phrases[0]], ["L1", "1"], "a single 1-3 letter prefix is dropped, as the add-in does");
    // Live defects: (a) an explicit vault always wins; (b) found above or beside the model's folders; (c) never a silent fallback.
    const lv = path.join(tmp, "TST_X"), lvV = path.join(lv, "ACE", "ACE Vault");
    fs.mkdirSync(lvV, { recursive: true }); fs.mkdirSync(path.join(lv, "WORK", "TST_sub"), { recursive: true });
    fs.writeFileSync(path.join(lvV, "project-requirements.json"), JSON.stringify({ schema: "ace.project-requirements/1", worksets: { keep: ["TST-ST-OLD"], naming: { categoryTokens: { OLD: ["Generic Models"] } },
      rules: [{ id: "tst-cols", workset: "TST-ST-GF COLUMNS", category: ["Structural Columns"] }] } }));
    const pv = (await call("workset_plan", { action: "propose", vault: lvV, out_dir: path.join(tmp, "wpv") }));
    const pvT = pv.content.at(-1).text;
    assert.ok(pvT.includes(`Vault: ${lvV} (given;`) && /Standard: the project's confirmed list \(project-requirements\.json\) \(.*TST_X.*\), 1 rule\(s\)/.test(pvT), "(a) the explicit vault wins, its path is printed");
    const pvD = parseCsvSmoke(fs.readFileSync(JSON.parse(pv.content[0].text).files[2], "utf8"));
    assert.ok(pvD.some((r) => r[4] === "TST Thing : A" && r[7] === "TST-ST-OLD"), "the vault's categoryTokens are used (OLD -> Generic Models)");
    assert.match(pvT, /TST-ST-OLD: keep;.*kept: confirmed in the vault \(never proposed for deletion\)/, "a workset the vault keeps is never proposed for deletion");
    const pw = (await call("workset_plan", { action: "propose", paths: [path.join(lv, "WORK", "TST_sub", "TST_WP1.rvt")], out_dir: path.join(tmp, "wpw") })).content.at(-1).text;
    fakeOpened = null;
    assert.ok(pw.includes(`Vault: ${lvV} (found beside the model's folders;`), "(b) found from the model's path: X\\WORK\\<sub> -> X\\ACE\\ACE Vault");
    const noWs = path.join(tmp, "TST_nows", "ACE Vault"); fs.mkdirSync(noWs, { recursive: true }); fs.writeFileSync(path.join(noWs, "project-requirements.json"), JSON.stringify({ checks: {} }));
    const pc = await call("workset_plan", { action: "propose", vault: noWs, out_dir: path.join(tmp, "wpc") });
    assert.ok(pc.isError && pc.content[0].text.includes(path.join(noWs, "project-requirements.json")) && /has no workset rules/.test(pc.content[0].text), "(c) a vault without workset rules fails loudly with the path");
    const pm = await call("workset_plan", { action: "propose", vault: path.join(tmp, "TST_missing"), out_dir: path.join(tmp, "wpc") });
    assert.ok(pm.isError && /no project-requirements\.json at .*TST_missing/.test(pm.content[0].text), "(c) a vault that cannot be read fails loudly");
    const prv = JSON.parse((await call("project_rules", { section: "worksets", vault: lvV })).content[0].text);
    assert.ok(prv.vault === lvV && prv.rules.keep[0] === "TST-ST-OLD", "project_rules: the explicit vault wins for the worksets section");
    // PUMP ROOM: a singular category word after another word in its part is a place, not the category.
    for (const n of ["TST-ST-PUMP ROOM", "TST-POOL & Pump Room"]) assert.ok(!WP.parseName(n, {}).categories.includes("Rooms") && WP.parseName(n, {}).words.includes("PUMP"), n);
    assert.ok(WP.parseName("TST-ST-BASE COLUMNS", {}).categories.includes("Structural Columns") && WP.parseName("TST-ST-LOBBY WALLS", {}).categories.includes("Walls") && WP.parseName("TST-ST-ROOMS", {}).categories.includes("Rooms"), "plural element heads and a name-first category still count");
    const W13x = () => Wk13;
    // 1.13.5 live defects: rules stay in models that have their workset; zone / asset worksets keep their elements; type tokens.
    {
      const G5 = (workset, category, type, level, elements, material = "concrete") => ({ workset, category, type, level, material, zone: "", option: "", elements, borrowed: 0 });
      const mk = (model, names, groups) => ({ model, levels: [{ name: "C-GF(GROUND FLOOR)" }, { name: "A-01(FIRST FLOOR)" }, { name: "C-RF(ROOF)" }],
        worksets: [{ name: "Workset1", isDefault: true, elements: 0 }, ...names.map((n) => ({ name: n, elements: groups.filter((g) => g.workset === n).reduce((k, g) => k + g.elements, 0) }))], groups });
      const fA = mk("TST_A", ["TST-ST-GF COL & WALLS", "TST-ST-RF COL & WALLS"], [G5("Workset1", "Structural Columns", "TST C : 1", "C-GF(GROUND FLOOR)", 3), G5("Workset1", "Structural Columns", "TST C : 2", "A-01(FIRST FLOOR)", 1)]);
      const fB = mk("TST_B", ["TST-ST-01 COLUMNS"], [G5("Workset1", "Structural Columns", "TST C : 1", "A-01(FIRST FLOOR)", 2)]);
      const pAB = WP.propose([{ facts: fA }, { facts: fB }]);
      const rows = pAB.rules.map((r, i) => ({ line: i + 2, proposed: r["Proposed workset"], action: r.Action, decision: "OK", rule: r.Rule }));
      const RR = WP.rulesFromRows(rows).rules;
      const plA = WP.planFromRules(RR, fA), plB = WP.planFromRules(RR, fB);
      assert.ok(!plA.plan.create.length && !plB.plan.create.length && !plA.plan.moves.some((m) => m.to === "TST-ST-01 COLUMNS") && !plB.plan.moves.some((m) => /COL & WALLS/.test(m.to)), "a rule never leaks into a model without its workset");
      assert.ok(plA.notes.some((n) => /not used in TST_A .*"TST-ST-01 COLUMNS"/.test(n)), "said per model");
      assert.ok(WP.planFromRules(RR, fA, { createMissing: true }).plan.create.includes("TST-ST-01 COLUMNS"), "create_missing: true allows it");
      const RRc = WP.rulesFromRows(rows.map((r) => r.proposed === "TST-ST-01 COLUMNS" ? { ...r, decision: "create" } : r)).rules;
      assert.ok(RRc.find((r) => r.workset === "TST-ST-01 COLUMNS").create && WP.planFromRules(RRc, fA).plan.create.includes("TST-ST-01 COLUMNS"), "a per-row create decision lets the rule apply everywhere");
      assert.deepEqual(WP.vaultRules(RR).find((r) => r.workset === "TST-ST-01 COLUMNS").models, ["TST_B"], "saved rules record their source models");
      // zoneWins: a boundary wall's columns stay on BOUNDARY WALLS; a rule naming that zone + category takes them; the vault can turn it off.
      const fZ = mk("TST_Z", ["TST-ST-BOUNDARY WALLS", "TST-ST-01 COLUMNS", "TST-ST-STEEL FENCE", "TST-ST-FOUNDATIONS", "TST-ST-RF PARAPETS", "TST-ST-UPSTAND WALL", "TST-ST-GF FRAMING", "TST-ST-SOG", "TST-ST-PCC"],
        [G5("TST-ST-BOUNDARY WALLS", "Structural Columns", "TST C : 1", "A-01(FIRST FLOOR)", 4), G5("TST-ST-BOUNDARY WALLS", "Walls", "TST W : 1", "A-01(FIRST FLOOR)", 6), G5("TST-ST-STEEL FENCE", "Structural Foundations", "TST F : 1", "C-GF(GROUND FLOOR)", 2),
         G5("TST-ST-RF PARAPETS", "Floors", "TST Slab : 1", "C-RF(ROOF)", 1), G5("Workset1", "Floors", "Floor : SOG 120mm", "C-GF(GROUND FLOOR)", 5), G5("Workset1", "Floors", "Floor : BLINDING 50", "C-GF(GROUND FLOOR)", 2)]);
      const nz = { categoryTokens: { FRAMING: ["Structural Framing", "Floors"] }, typeTokens: { BLINDING: "TST-ST-PCC" } };
      const pZ = WP.propose([{ facts: fZ, naming: nz }]);
      const dz = (cur, type) => pZ.detail.find((d) => d["Current workset"] === cur && d["Type / link / contents"] === type);
      assert.ok(dz("TST-ST-BOUNDARY WALLS", "TST C : 1")["Proposed action"] === "keep" && /kept on zone\/asset workset "TST-ST-BOUNDARY WALLS"/.test(dz("TST-ST-BOUNDARY WALLS", "TST C : 1").Reason), "columns stay on BOUNDARY WALLS");
      assert.equal(dz("TST-ST-STEEL FENCE", "TST F : 1")["Proposed action"], "keep", "concrete foundations stay on STEEL FENCE (its material word would have pulled them out)");
      assert.equal(dz("TST-ST-RF PARAPETS", "TST Slab : 1")["Proposed action"], "keep", "what sits on RF PARAPETS stays (an asset workset)");
      assert.equal(dz("Workset1", "Floor : SOG 120mm")["Proposed workset"], "TST-ST-SOG", "a type token (SOG) beats level + category (GF FRAMING)");
      assert.equal(dz("Workset1", "Floor : BLINDING 50")["Proposed workset"], "TST-ST-PCC", "vault typeTokens override");
      const pZ2 = WP.propose([{ facts: { ...fZ, worksets: [...fZ.worksets, { name: "TST-ST-BOUNDARY COLUMNS", elements: 0 }] }, naming: nz }]);
      assert.equal(pZ2.detail.find((d) => d["Current workset"] === "TST-ST-BOUNDARY WALLS" && d.Category === "Structural Columns")["Proposed workset"], "TST-ST-BOUNDARY COLUMNS", "a rule naming that zone and the category takes them");
      const pZ3 = WP.propose([{ facts: fZ, naming: { ...nz, zoneWins: false } }]);
      assert.equal(pZ3.detail.find((d) => d["Current workset"] === "TST-ST-BOUNDARY WALLS" && d.Category === "Structural Columns")["Proposed workset"], "TST-ST-01 COLUMNS", "naming.zoneWins: false: the category rule pulls them out");
      assert.equal(W13x().namingOf({ naming: { typeTokens: { SOG: "TST-ST-SOG" }, zoneWins: false } }).zoneWins, false);
    }
    // refresh: earlier decisions kept, rows marked new / changed / same / gone.
    const rf = await call("workset_plan", { action: "propose", out_dir: path.join(tmp, "wref"), refresh: edited });
    const rfT = rf.content.at(-1).text;
    assert.match(rfT, /Refreshed against the previous file: \d+ new, \d+ changed, \d+ same, \d+ gone; \d+ earlier decision\(s\) kept/, rfT);
    const rfRows = parseCsvSmoke(fs.readFileSync(JSON.parse(rf.content[0].text).files[1], "utf8"));
    const rh = rfRows[0];
    assert.ok(rh.includes("Since last proposal") && rfRows.some((r) => r[rh.indexOf(WP.DECISION)] === "change to TST-ST-RF BEAMS"), "the earlier decision is carried over");
    assert.match(rfT, /Standard: the project's confirmed list/);
    // The vault standard: unconfirmed clauses are listed, never used; confirm_standard after the user agreed.
    const sv13 = path.join(tmp, "wstd-vault");
    fs.mkdirSync(path.join(sv13, "ACE Vault", "rules"), { recursive: true });
    fs.writeFileSync(path.join(sv13, "ACE Vault", "rules", "worksets-standard.json"), JSON.stringify({ schema: "ace.worksets-standard/1", confirmed: false, rules: [{ workset: "TST-ST-GF COLUMNS", category: ["Structural Columns"], clause: "TST BEP 5.1" }] }));
    const su = (await call("workset_plan", { action: "status", vault: sv13, out_dir: path.join(tmp, "wstat2") })).content.at(-1).text;
    assert.ok(/Candidate clauses in .*worksets-standard\.json \(NOT used until the user confirms them/.test(su) && /\[TST BEP 5\.1\]/.test(su) && !/standard: the vault documents/.test(su), su);
    fs.writeFileSync(path.join(sv13, "ACE Vault", "rules", "worksets-standard.json"), JSON.stringify({ schema: "ace.worksets-standard/1", confirmed: true, rules: [{ workset: "TST-ST-GF COLUMNS", category: ["Structural Columns"], clause: "TST BEP 5.1" }] }));
    const sc = (await call("workset_plan", { action: "status", vault: sv13, out_dir: path.join(tmp, "wstat3") })).content.at(-1).text;
    assert.match(sc, /standard: the vault documents \(worksets-standard\.json, confirmed\) \(.*\), 1 rule\(s\)\)/, "a confirmed vault standard comes first and is named");
    assert.match(sc, /Not in the standard: .*"TST-ST-RF FRAMING"/);
  }
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
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /^No check from this server process\. Run the check again first; checks don't survive an MCP restart\./, "a write needs the identical check (none in this process yet)");
  const chk = (await call("register_update", ru)).content.at(-1).text;
  assert.match(chk, /\(check, nothing written\)[\s\S]*Matched \(delivered, date set\): 1: TST-001 <- TST-001_A\.pdf\.\nDelivered but not in the register: 1: TST-999\.pdf\.\nIn the register but not delivered: 1: TST-002\.\nDates later than the submission, cleared: 1: TST-002 Stage 1 2026-12-01\./);
  assert.ok(!fs.existsSync(ru.output), "a check writes nothing");
  fs.writeFileSync(path.join(rDir, "cde", "TST-002.pdf"), "x");
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /has not been checked/, "a changed delivered folder needs a new check");
  await call("register_update", ru);
  const wr = (await call("register_update", { ...ru, confirm: true })).content.at(-1).text;
  assert.ok(fs.existsSync(ru.output) && /written to .*out\.xlsx/.test(wr) && /Report: .*out - report\.csv \(not beside the output\)/.test(wr));
  // 1.14.2: the report never lands beside the output (a submission folder): ACE's reports folder here; report_dir there is refused.
  assert.match(wr, /Self-check ok \(strict XML, tables, style and string indexes\); Excel open test: skipped \((not on Windows|Excel is not installed)\)\./, "the written file is self-checked; the Excel open test is said");
  const repF = wr.match(/Report: (.*out - report\.csv)/)[1];
  assert.ok(fs.existsSync(repF) && path.dirname(repF) !== rDir && !fs.readdirSync(rDir).some((f) => /report\.csv$/.test(f)), "no report CSV beside the output");
  assert.match(chk, /Full report \(every item\): .*out - check report\.csv/, "the check names its full report");
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /has not been checked/, "a check is used once");
  await call("register_update", ru);
  assert.match((await call("register_update", { ...ru, confirm: true })).content[0].text, /exists: give another output path, or overwrite: true/);
  assert.match((await call("register_update", { ...ru, report_dir: rDir })).content[0].text, /is the output's folder: the report never goes beside the output/);
  const rdx = path.join(tmp, "register-reports");
  const chkR = (await call("register_update", { ...ru, report_dir: rdx })).content.at(-1).text;
  assert.ok(new RegExp(`Full report \\(every item\\): ${rdx.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`).test(chkR), "report_dir is used");
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
  // 1.12.9: the project template in the vault: the call needs only vault, discipline, stage, date, label, delivered, output.
  const tv2 = path.join(rDir, "tplproj");
  fs.mkdirSync(path.join(tv2, "ACE Vault", "rules"), { recursive: true });
  fs.copyFileSync(reg, path.join(tv2, "TST register.xlsx"));
  fs.writeFileSync(path.join(tv2, "ACE Vault", "rules", "register.json"), JSON.stringify({ file: "../TST register*.xlsx", headerRow: 1, idColumn: "Doc No",
    disciplines: { ST: { sheet: "TST_REG" } }, stages: { IFC: ["Stage 1"] } }));
  const tr = { vault: tv2, discipline: "ST", stage: "IFC", submission_date: "2026-10-20", label: "SUB 09", delivered_dir: path.join(rDir, "cde"), output: path.join(rDir, "tpl-out.xlsx") };
  const trt = (await call("register_update", tr)).content.at(-1).text;
  assert.match(trt, /Matched \(delivered, date set\): 2: TST-001 <- TST-001_A\.pdf; TST-002 <- TST-002\.pdf\./);
  assert.match(trt, /From the project template \(.*register\.json\): register, sheet, header_row, id_column, stage_columns, stage; from the call: .*submission_date/);
  const tro = (await call("register_update", { ...tr, id_column: "A" })).content.at(-1).text;
  assert.match(tro, /From the project template \(.*\): register, sheet, header_row, stage_columns, stage; from the call: id_column,/, "an explicit argument wins over the file");
  assert.match((await call("register_update", { ...tr, discipline: "AR" })).content[0].text, /Discipline "AR" is not in .*register\.json: known ST\./);
  assert.match((await call("register_update", { ...tr, stage: "CONCEPT" })).content[0].text, /Stage "CONCEPT" is not in .* for ST: known IFC\./);
  assert.match((await call("register_update", { ...ru, discipline: "ST" })).content[0].text, /discipline needs the project's template/);
  // 1.14.2 #29: one discipline in the template: discipline may be left out; an anchored prefix idPattern (no capture
  // group) in the template only filters ids, it never extracts them (it would match only the start of a file name).
  const tv3 = path.join(rDir, "tplproj3");
  fs.mkdirSync(path.join(tv3, "ACE Vault", "rules"), { recursive: true });
  fs.copyFileSync(reg, path.join(tv3, "TST register.xlsx"));
  fs.writeFileSync(path.join(tv3, "ACE Vault", "rules", "register.json"), JSON.stringify({ file: "../TST register*.xlsx", headerRow: 1, idColumn: "Doc No", idPattern: "^TST-0",
    disciplines: { ST: { sheet: "TST_REG" } }, stages: { IFC: ["Stage 1"] } }));
  const t3 = (await call("register_update", { vault: tv3, stage: "IFC", submission_date: "2026-10-20", label: "SUB 10", delivered_dir: path.join(rDir, "cde"), output: path.join(rDir, "tpl3-out.xlsx") })).content.at(-1).text;
  assert.match(t3, /Matched \(delivered, date set\): 2: TST-001 <- TST-001_A\.pdf; TST-002 <- TST-002\.pdf\./, "a prefix pattern filters, the ids are still found");
  assert.match(t3, /Id pattern: no capture group, so it only filters the ids/);
  assert.match(t3, /From the project template \(.*\): register, sheet, header_row, id_column, id_pattern, stage_columns, stage;/, "discipline: the template's only one");
  // Several disciplines and none named: the call's own register / id_column / stage_columns still work.
  fs.writeFileSync(path.join(tv3, "ACE Vault", "rules", "register.json"), JSON.stringify({ file: "../TST register*.xlsx", idColumn: "Doc No", disciplines: { ST: { sheet: "TST_REG" }, AR: { sheet: "TST_REG" } } }));
  assert.ok(!(await call("register_update", { ...ru, vault: tv3, stage: ["Stage 1"] })).isError, "own arguments without discipline");
  assert.match((await call("register_update", { vault: tv3, stage: "IFC", submission_date: "2026-10-20", label: "SUB 10", delivered_dir: path.join(rDir, "cde"), output: path.join(rDir, "tpl3-out.xlsx") })).content[0].text, /Give discipline: one of ST, AR/);
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
// --- reload_addin (G8): the loader swaps the build when idle; the tool waits for /health to show it, and previews made
// before the swap no longer count (a preview proves what the build that ran it would do) ---
{
  const swapEdit = { code: "foreach (var w in ctx.All<Wall>()) w.LookupParameter(\"Comments\").Set(\"swap\");" };
  // A swap the server did not ask for (install.ps1's reload, the loader's own switch, "Load ACE again"): the bridge now
  // answers with another build id, so the preview made on the old build is refused at apply.
  await call("execute_revit_code", { ...swapEdit, dry_run: true, explanation: "preview on build #1" });
  const keep = fakeLoader.build;
  fakeLoader.build = "1.13.3-aaaaaaaaaaaa#7";
  const across = await call("execute_revit_code", { ...swapEdit, explanation: "apply on another build" });
  assert.match(across.content[0].text, /preview was made on ACE build 1\.13\.3-aaaaaaaaaaaa#1.*now runs 1\.13\.3-aaaaaaaaaaaa#7/, "a preview from another ACE build is refused at apply");
  fakeLoader.build = keep;
  const sentApplies = seen.filter((x) => x.command === "execute_code" && x.args?.code === swapEdit.code && !x.args?.dry_run).length;
  assert.equal(sentApplies, 0, "the refused apply never reached Revit");
  await call("execute_revit_code", { ...swapEdit, dry_run: true, explanation: "preview before the reload" });
  const r = json(await call("reload_addin"));
  assert.ok(r.ok === true && r.payloadVersion === "1.13.4" && r.previous === "1.13.3" && r.loaderVersion === "1.0.0" && r.why, "reload_addin reports loaderVersion, payloadVersion, previous, ok, why");
  assert.equal(seen.filter((x) => x.command === "reload_addin").length, 1, "one bridge call; the outcome is read from /health");
  assert.ok(r.previewsCleared >= 1, "previews from before the swap are dropped");
  assert.match((await call("execute_revit_code", { ...swapEdit, explanation: "apply after the reload" })).content[0].text, /not been previewed/, "an apply after a swap needs a new preview");
  const same = json(await call("reload_addin"));
  assert.ok(same.ok === true && same.scheduled === false && /already the installed build/.test(same.why), "nothing new installed: no swap, said plainly");
  assert.equal(seen.filter((x) => x.command === "reload_addin").at(-1).args.force, false, "force is off unless asked");
}

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

// --- 1.13.2: optional local code signing (static checks: the scripts run on Windows only) ---
{
  const rootFile = (f) => fs.readFileSync(new URL(`../../${f}`, import.meta.url), "utf8");
  const trust = rootFile("trust-ace.ps1"), inst = rootFile("install.ps1"), doc = rootFile("doctor.ps1"), pkg = rootFile("build-package.ps1");
  assert.ok(/'trust-ace\.ps1'/.test(pkg), "the package ships trust-ace.ps1");
  assert.ok(/-KeyExportPolicy NonExportable/.test(trust) && /-KeyLength 3072/.test(trust) && /CurrentUser\\My/.test(trust) && /'TrustedPublisher', 'Root'/.test(trust) && /\[switch\]\$Remove/.test(trust), "trust-ace: non-exportable RSA 3072 key, trusted per user, -Remove");
  assert.ok(/Filter 'AceRevitMcp\*\.dll'/.test(inst) && /Sign-AceDlls \$stage/.test(inst) && /Sign-AceDlls \$loaderStage/.test(inst) && /Get-AuthenticodeSignature/.test(inst) && /-HashAlgorithm SHA256/.test(inst), "install signs only ACE's own DLLs, SHA256, verified");
  assert.ok(/Code signing/.test(doc) && /signing\.json/.test(doc), "doctor reports the signing state");
  // G8: the installer installs next to the running build, replaces the loader only on a new version, and asks the
  // running Revit to load the new build (reload_addin through the bridge) instead of asking for a restart.
  assert.ok(/command = 'reload_addin'/.test(inst) && /X-Ace-Token/.test(inst) && /\$oldLoader -ne \$newLoader/.test(inst) && /current\.json/.test(inst), "install: new build folder, current.json, loader by version, reload without restart");
  assert.ok(/AceRevitMcp\.Loader\.dll/.test(doc) && /Reload pending/.test(doc), "doctor reports the loader and a pending reload");
  assert.ok(/'loader'/.test(pkg) && /AceRevitMcp\.Loader\.csproj/.test(pkg), "the package ships the ACE loader per Revit year");
  for (const [n, t] of Object.entries({ trust, inst, doc, pkg }))
    assert.ok(!/\?\?/.test(t.replace(/#.*$/gm, "")), `${n}: no ?? (Windows PowerShell 5.1)`);
}

await client.close();
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`smoke test passed (${seen.length} bridge calls)`);
