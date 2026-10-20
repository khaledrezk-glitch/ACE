# Development backlog

The one list of what is still to build, ranked. The idea agents read it and propose updates; the owner approves what
gets built (see `docs/DEV-TEAM.md`). Each item names its source: the owner's ideas (`docs/PROJECT-STATE.md`), the
architecture concept (`docs/ARCHITECTURE-CONCEPT.md`), the stress review (`docs/STRESS-REVIEW.md`), or the
development radar (the "Development radar" section of a learning report: real usage on the PCs).

Effort: S (hours), M (a day or two), L (more). Status: **open**, **next** (approved for the next cycle), **live check**
(built, waiting for verification in Revit), **in progress**, **done (unreleased)**.

## Product focus (approved by the owner, 15 Oct 2026)

Source: the capability assessment and product brief rev B (14 Oct), owner decisions D1 to D5 (15 Oct). One connected
solution, not one tool: a **spine** (requirements vault + status store + Companion + safety gate + learning loop) with five
modules: set up per requirements, model and produce, coordinate, audit and assure, deliver and prove.

| Decision (15 Oct) | Means |
|---|---|
| D1 | Build may start on the spine and the five modules |
| D2 | The vault and the project requirements file live in a project folder named once, mapped in `config.json` "projects" to the model's cloud id; local centrals: beside the model |
| D3 | Project rules merge with office rules key by key; the project wins |
| D4 | Requirement ids stay stable across document revisions (changed text = a new revision of the same id) |
| D5 | Severity (info / warning / error) and status (met / partly / not met / not checkable / contradicted) are separate scales; "partly" = measured partial compliance |

**Build order:** T10 and T11, then C1 (project requirements file and one resolver), then V1 and V2 (vault and requirement
records), then C1+ (compliance slot and run records), V4, V5 and the Companion compliance view.
**Must before the presentation:** V1, V2, V4, V5, C1, L0, L1, F1, the Companion compliance view and the "since last sync"
greeting (W3). **Should:** P2, P6, P4, F2 (Audit mode), M1 to M3. **After:** C9, P8, C6, C4.
First real dataset: the client's documents for Al Nahda (341 requirement records, 258 testable in the model, 58
covered today). It is client-confidential and stays out of this repository.

## 0. Measurement (small, before more features)

| # | Item | Why | Effort | Status |
|---|---|---|---|---|
| M1 | **Task fingerprint:** a task id and intent label on every call (opened by `plan_task` or the first call, closed on apply or idle), replacing the 15-minute gap | The radar cannot tell that two runs are the same kind of task | S | open |
| M2 | **Claude-side tokens:** read usage from Claude Code result events into the learning report; Claude Desktop keeps the estimate, labelled | The server sees only reply sizes | S | open |
| M3 | **First vs repeat table** in the learning report, per fingerprint, with the reuse rung that served it | Proves "the second run costs far less" | S | open |
| M4 | **Fixed-cost meter:** tool-definition and instruction size per mode reported at start; a bench case fails on >10% growth without a note | Measured 1.8.3: 39,250 + 12,527 chars (about 12.9k tokens) | S | open |
| I1 | **Improvement rule in the dev cycle:** every propose run reads the M3 table; a task whose repeat cost did not fall is a candidate by default | Keeps the loop honest | S | open, after M3 |

Baseline for M3 (NRM classification, 6 Oct): first model 59 calls / 3.31 USD; repeats 19 and 16 calls / 1.86 and 1.40
USD per model; ACE's reuse tools (save_script, run_saved_script, lessons) used 0 times.

## 1. Fewer steps and fewer interactions (owner, 6 Oct)

| # | Item | Why | Effort | Status |
|---|---|---|---|---|
| F1 | **One approval per job:** a multi-step job previewed as one run (each step sees the one before, all inside one transaction group): one Apply card listing every step, one apply, one undo step | Today a 4-step job means 4 previews and 4 approvals | M | open |
| F2 | **Show only the work mode's tools** (MCP tool list changes with the mode) | Fewer tokens in every conversation; needs a live test that Claude Desktop handles a changing tool list | S | open |
| F3 | **Ready-made jobs from the radar:** turn the radar's "code written again and again" and "tasks with many calls" into built-in scripts or native commands | Fewer calls for the jobs people really do | S each | open, fed by the radar |
| F4 | **Ask once, then act:** guidance and a planning helper so Claude asks its one question up front, then previews once, instead of trying, failing and asking | Fewer previews per change (radar: "several previews for one change") | S | open |
| F5 | **Long-running jobs** (concept G4): job id, progress and cancel in the Companion, result kept | No 30-minute waits inside one call | M | open |
| P1 | **plan_task** (read-only): request in, matching job or recipe with inputs, lessons, guide branch and the one question out. Merges F3 (entry point), F4 and the index part of G2 and G5 | One entry point for everything the loop learns; fewer exploration calls | M | open |
| P1b | **Recipe capture on success:** after a first-time-right apply, Claude offers `save_script` with named inputs, tagged to the fingerprint | Moves repeated tasks onto saved scripts | S | open |
| P4 | **NRM classification job** (first ready-made job): mapping table in the requirements file, fill empty only, residue report | The only repeated real task so far (6 models, 6 Oct) | S | open, after C1 |

## 2. Waiting for a live check in Revit

| # | Item | Effort | Status |
|---|---|---|---|
| L1 | 1.8.x as a whole: clash grid speed, snapshots after save, compile cache, presentation standard, worksets per the BEP in the model check, the new Apply cards | S (the owner runs it; benchmark cases exist) | live check |
| L2 | Whether a preview in a workshared model borrows elements (`heldAfterPreview`) | S | live check |
| L3 | Claude Desktop with progress notes and cancel on long calls | S | live check |
| L0 | Revit PC on 1.8.3 (installed 14 Oct; Claude Desktop restart needed to load the new server) | S | live check |
| L4 | Does `execute_revit_code` run with a family document active (FamilyManager reachable)? Decides the route for C9 | S | live check |
| L5 | ACE and Autodesk's Revit MCP Technical Preview loaded together: does Claude pick the right server's tools? | S | live check |

## 3. The concept's next circles (architecture concept, section 10)

| # | Item | Effort | Status |
|---|---|---|---|
| C1 | **Project requirements file** (D2): check set, worksets, clash matrix, naming, LOIN in one project file; office settings stay separate; old files keep working. Done so far: `project-requirements.json` (checks, presentation, worksets, clash ranks) found by cloud id or beside the model, merged by one resolver; `project_rules` tool. Still to do: clash tests and roles from the file (#9 of the spine contract), check options (T12), naming / LOIN / classification sections | M | in progress |
| C1+ | Status store gains a Compliance slot and a run record (run id, rule hash, snapshot), persisted per model and keyed by the model key, not the title; requirement status lives in the store, not in the record | M | done (1.9.0): every model check evaluates the vault's requirements (met / partly / not-met / not-checkable, deviations beside the status), publishes to the status store and, in a trusted vault, writes `04 Audits/<model key>/runs/<run id>.json` with the rules and requirements hashes; `vault compliance` reads it and says when it is out of date. Still to do: link the run to a change-tracker snapshot, reload the last run when a model opens (W2); live check |
| C2 | **Requirements hub:** BEP / LOD / LOIN read into rules, recommendations, and the **contradiction monitor** (documents vs settings vs model) | L | open |
| C3 | **Notifications like Revit warnings** for requirement findings (concept section 8) | M | open |
| C4 | **Project Hub:** one container for rules, findings, models and documents, opened from the ribbon; click a point to edit | L | open |
| C5 | **BIM Tools group** on the ribbon | S | open |
| C6 | **Issue view engine and BCF export** (D4) for Navisworks / ACC | M | open |
| C7 | **Information requirements check from the LOIN** (D10) and the **submission check** as the monitor at the issue stage (D11) | M | open |
| C8 | **Rule packs** for design and code checks (D12) | M | open |
| C9 | **Family creator and checker** (concept section 12) | L | open |
| P2 | Container and file naming check per the project's naming convention (field codes from the EIR) | M | open, after C1 |
| P3a | Family checker for loaded families (naming, category, required shared parameters, in-place, CAD imports, size): the first half of C9 | M | open |
| P3b | Family checker in the family editor and on a library folder | M | open, after L4 and G4 |
| P3c | Family creator for simple parametric families | L | open, after P3a |
| P5 | Excel round trip: parameters and quantities out and in, previewed | M | open |
| P6 | Shared coordinates and link alignment check with a fix preview | S | open |
| P7 | Coordination meeting pack: issue trend since the last meeting, by discipline | S | open |
| P8 | Shop-drawing sheet-set job: views, scale, tags, dimensions, title block, schedules, PDF / DWG set named per the convention; one approval | L | open, after F1 |
| P9 | Model from linked CAD (grids, levels, walls) | L | later |

## 3a. Requirements vault and audit (owner, 14 Oct; design in the project's Audit Vault design note)

The vault is the document store of C2 / C4, not a parallel hub.

| # | Item | Effort | Status |
|---|---|---|---|
| V1 | `vault` tool: init a vault in the project folder (D2), register documents (type, revision, status, hash), read text (DOCX, XLSX, PDF), re-read only changed documents | M | done (1.9.0); live check with the real project folder |
| V2 | Requirement records and decisions (JSON; every record tied to a document, revision and clause; stable ids, D4; severity and status apart, D5) | S | done (1.9.0) |
| V3 | Comparisons: EIR vs BEP, revision vs revision, LOIN vs MIDP / TIDP, documents vs ACE settings (= the C2 analyser) | M | open, with C2 |
| V4 | Master report (DOCX + HTML) saved in the vault; summary and pointer in the model (extensible storage) | M | done (1.9.1): `vault report` writes HTML, Word and an actions CSV to `04 Audits` with the trend since the last report; `vault model_summary` keeps the summary on Project Information (previewed, one undo step, only when asked; owner decision pending). Contradictions section waits for V3; pictures per finding wait for the issue view engine. Live check |
| V5 | Ribbon Audit > Vault / Master Report; consent once per vault for file writes | S | done (1.9.1): Vault opens the vault's index and asks once whether ACE may save audit records there (same trust list as the `vault` tool); Master Report opens the model's latest report, or shows the summary kept in the model and a prompt for Claude. Live check |
| V6 | Export requirement-derived rules as buildingSMART IDS 1.0 (an `ifc_binding` per record; coverage report of what IDS cannot express; round-trip check before any "IDS ready" claim) | S-M | open, after V2 |
| W1 | Show the clash cause and who changed it in the Companion and Clash Browser (the data exists) | S | open |
| W2 | Persist the status store per model (keyed by the model key) so the Companion is not empty after opening a model | S | open |
| W3 | After-sync step that builds the "since last sync" greeting (at most three lines, one action) | M | open |
| W4 | Compliance tab and tree map (document > section > requirement > check) in the dashboard, with drill-down to elements | M | Companion tab done (1.9.1): against which documents, met %, status bar, groups by kind, requirement cards with source, quote, evidence, decision and "Show in model"; Re-run audit, master report, vault. Still to do: the visual tree map in the dashboard and the trend per audit |
| P10 | Revit 2027 path on Autodesk's read / write MCP servers: ACE keeps the workflow and the safety gate | L | open, after the presentation |

## 3a. Al Nahda coverage (assessment proposal A1 to A8, 14 Oct; owner decisions DEC-0002 to DEC-0005 taken 19 Oct)

Requirements the model check can prove, today 25 of 341 (proposal: `backlog-proposal-2026-10-14.md` in the project files).
A1, A3 and A4 have build-ready specs and are no longer blocked by open decisions; building them waits for the owner's go-ahead.

| # | What | Effort | Status |
|---|---|---|---|
| A1 | A check reports which requirements it proves, with evidence per row; LOIN parameter fill per category and stage from `rules/loin.json`; ALD_ names matched exactly, then ignoring case and space / underscore with a spelling warning (DEC-0005) | M (3.5 days; spec analysis/14) | open, unblocked 19 Oct |
| A2 | Units and Revit version | S | open |
| A3 | Classification fill and code grammar (same engine as A1) | S (spec analysis/16) | open, unblocked 19 Oct |
| A4 | Naming from `rules/naming.json` (models, links, families, materials, worksets); ground floor 00 per the CDE template (DEC-0004) | M (spec analysis/16) | open, unblocked 19 Oct |
| A5 | Element rules (no level, unhosted, room volumes, far geometry, Navisworks view) | S | open |
| A6 | Text (font, plotted height, case) | S | open |
| A7 | Coordinates and links (coordinates still TBD in the BEP) | M | open |
| A8 | Clash matrix-driven clash run (tolerance 25 mm, DEC-0002) | L | open |

## 3b. Navisworks (owner, 7 Oct; concept section 15)

| # | Item | Effort | Status |
|---|---|---|---|
| N0 | **Import Navisworks clash reports** (XML / tabular HTML) into the clash engine | M | live check (needs a real report from the office) |
| N1 | **Watch folder:** a report saved to the project's clash folder is imported on its own when the model opens (no click) | S | open |
| N2 | **Navisworks add-in (read):** test settings, viewpoints, comments and all item properties straight from the open NWF; no export step | L | open |
| N3 | **Two-way status:** approvals and notes made in ACE written back to Navisworks (and the reverse), so one status lives in both | M | open, after N2 |
| N4 | **Batch runs:** Navisworks Automation runs the federated tests overnight and drops the XML for N1 | M | open, after N2 |

## 3c. Submission tools (owner, 20 Oct; DP04 next-tools spec, items 2, 3 and 5)

| # | Item | Effort | Status |
|---|---|---|---|
| D2 | **view_membership_fill:** a text parameter set from what a view shows (value_in / value_out), reasons per element out of the view, definition only from the vault's shared parameter files, spelling and impact said first; check > dry run > identical apply | M | built (1.12.6); live check in Revit |
| D3 | **export_deliverables:** NWC / IFC / DWFx in one call, names from the vault naming rule, IFC settings from the vault, pre-flight blocks reported, files table and run record | M | built (1.12.6); live check in Revit (Navisworks exporter installed) |
| D5 | **register_update:** deliverables register (xlsx) updated for a submission, no Revit; check, then confirm | S | built (1.12.6); check with a real register |

## 4. Platform gaps (concept section 13)

| # | Item | Effort | Status |
|---|---|---|---|
| G1 | Verify before commit: a script states what must be true afterwards; the runner checks it and rolls back with the reason. **Extended:** every fix (NativeChange and code runner) declares its expected outcome; after apply, inside the same TransactionGroup, the add-in re-runs that check on exactly those elements and rolls back on mismatch (today the Companion asks Claude to verify, `CompanionView.cs`); the run record says "applied and verified" | M | open; extension proposed 18 Oct by the KhloudTools review (`analysis/17`), build waits for the owner. Review recommends it before the Al Nahda live test |
| G7 | **Capture case:** one button on a failing check or fix writes a JSON file (elements, categories, types, relevant parameters, geometry summary, host / link / workset state, the check or fix definition, add-in version and Revit build), with model, project, people and COBie fields removed by default, so the cloud can reproduce and fix offline; each case becomes a bench regression case | M | in progress (capture_method, owner asked 20 Oct to record his method; 1.12.5: start / stop, case record, plan comparison). Earlier: proposed 18 Oct by the KhloudTools review (`analysis/17`); review recommends it before the Al Nahda live test. Still open: the one-button capture on a failing check and the anonymised bench case |
| G2 | Skills tree: guides, lessons and standards as branches loaded on demand (fewer tokens) | M | open |
| G3 | Sync with review: shared scripts, lessons and rules go through a reviewer before everyone gets them | M | open |
| G5 | Variables between runs (`ctx.Session`) | S | open |
| G6 | Who changed what, including Claude, in the change tracker | M | open |

## 5. Smaller technical items (stress review)

| # | Item | Effort | Status |
|---|---|---|---|
| T1 | Structured output (`outputSchema`) and guides as MCP resources | M | open |
| T2 | Native parameter rules in `find_elements` | M | open |
| T3 | Companion: skip rendering when hidden, cache tile icons | S | open |
| T4 | Clash discipline cached per document; background-save pruning that never removes a snapshot being read | S | open |
| T5 | Guides / lessons: `OST_StructuralConnections` and `OST_PropertyLines` do not exist in Revit 2025 | S | open |
| T6 | A run that reports "Document is a linked file" for every target must return a failure, not `success: true` | S | open |
| T7 | Claude should check `pending_changes` before sending an apply (7 "alreadyApplied" calls in the NRM run) | S | open |
| T8 | Ship `tools/bench` and the clash-test fixtures in the release zip so the benchmark runs on a package install | S | open |
| T9 | `assign_worksets` and `set_parameters` previews report `heldAfterPreview` like the code runner | S | open |
| T10 | Companion "Clash issues" tile showed the list's type name instead of the count | S | done (1.9.0) |
| T11 | One settings resolver for all rule files; the server no longer falls back to the packaged example workset rules (it asks the add-in for the rules in force) | S | done (1.9.0) |
| T12 | Requirement values hard-coded in the model check (900 mm door width, key parameter list, sheet and project information fields) move to check options in the check set, then C1; the two scripts that repeat them become thin callers | S | open |
| T13 | Stamp the add-in version and a hash of the check definitions in every compliance run record and the master report | S | open; proposed 18 Oct by the KhloudTools review (`analysis/17`), build waits for the owner |
| T14 | `CanEdit` also reports "pinned" and "in group X" next to workset ownership | S | open; proposed 18 Oct by the KhloudTools review (`analysis/17`), build waits for the owner |

## 6. Later

| # | Item | Status |
|---|---|---|
| R24 | **Revit 2024 support** beside 2025 (owner, 15 Oct: the Al Nahda project stays on Revit 2024). Revit 2024 runs .NET Framework 4.8, so the add-in needs a second build target; sizing in analysis/11. New code uses `Util/Compat.cs` instead of .NET 5+ only APIs. **Slice 1 done in 1.9.0:** one csproj builds net48 + net8.0-windows, System.Text.Json shipped with a resolver, installer / doctor / uninstall / check_setup per year; scripts answer "not available on Revit 2024 yet". Next: live check on the Al Nahda 2024 project (assembly conflicts with other add-ins are the main unknown). Slice 2: the code runner on net48 (AppDomain-hosted compiler, scripts loaded once per hash) | L | slice 1 done (1.9.0), live check pending; slice 2 open |
| R1 | Rename (Constella or Datum, `docs/NAMING.md`), ideally before the Project Hub | owner decides |
| R2 | Brand guidelines and logo into the repository (it is private now) | owner sends the files |
