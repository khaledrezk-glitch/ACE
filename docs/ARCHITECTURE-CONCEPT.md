# ACE architecture and concept: from points to circles

**Status:** concept for review. Nothing here is built yet unless it is marked *done*. Changes to the add-in wait
for approval.

## 1. The idea

Every capability is a **point**. When points share data, rules or views, they form a **circle**: a stronger
capability than either point alone. Circles join into **bigger circles**. For example:

- The change tracker (a point) and clash detection (a point) formed a circle: every new clash says **what change
  caused it and who made it**. *Done.*
- The BIM requirements hub (section 5) is the next bigger circle. The BEP, LOD and LOIN become the one source of project
  rules, and the model check, worksets, clash tests, naming and submission checks all read from it.

This only works if every new capability is studied with the others first. **The integration rule** (section 7)
makes that a step in every release, and the **Project Hub** (section 7) keeps every point and link in one place.

## 2. Architecture in layers

```
 Surfaces     Ribbon (work modes, BIM Tools group) | Companion | Claude (MCP tools) | HTML / Excel reports
 ------------------------------------------------------------------------------------------------------------
 Workflows    Model check · Clash Browser · Coordination report · Worksets per BEP · Test fit · Structure
              from ARC · Change tracker · Submission check (planned) · Requirements monitor (planned)
 ------------------------------------------------------------------------------------------------------------
 Engines      Rules engine | Model check engine | Clash engine | Issue view engine | Report engine | Status store
 ------------------------------------------------------------------------------------------------------------
 Knowledge    Model brief (host, links, open models, alignment) · Families · Snapshots · Lessons · Requirements
 ------------------------------------------------------------------------------------------------------------
 Platform     Bridge + ExternalEvent · Code runner (dry run, one undo step) · Safety gate · Journal · Approvals
 ------------------------------------------------------------------------------------------------------------
 Loop         Telemetry → learning report → lessons → benchmark → release (runs around every layer)
```

- **Workflows** are thin: they combine engines and never rebuild what an engine does.
- **Engines** do one job each and are shared.
- **Surfaces** only show what engines and workflows produce. The ribbon, the Companion and Claude call the same
  bridge commands, so there is one path for every action (this is true today).

## 3. Inventory: what each capability reads, produces and shares

| Capability | Reads | Produces | Shares with |
|---|---|---|---|
| Model brief | host, links, open models | levels, room types, families, naming, alignment | clash (sources), test fit, structure from ARC, Claude |
| Snapshots / change tracker | model + links | added / moved / retyped / changed, by person | clash cause, dashboard, Companion |
| Model check (dashboard) | model, check set | pass / review / action per check, score | Companion, submission, requirements |
| Clash engine | sources, clash rules | clashes, issues, responsibility, cause, status | Clash Browser, clash view, coordination report, dashboard |
| Clash view (DirectContext3D) | a clash, colours | focused 3D view | Clash Browser, Claude |
| Coordination report | issues | HTML with pictures, CSV | meetings |
| Worksets per BEP (script) | workset rules | moves, wrong-workset report | model check (Workset1 only today) |
| Test fit, structure from ARC | brief, rules in the script | model changes (previewed) | preview pictures, approvals |
| Work modes | mode definitions | ribbon focus / dim / hide, prompts, Claude tools | Companion, Claude |
| Approvals + preview pictures | dry runs | Apply cards | every modifying tool |
| Lessons + learning report + benchmark | telemetry, lessons | improvements | every tool |

## 4. Duplications found, and merge recommendations

Each item is a recommendation. None is applied yet.

| # | Overlap | Recommendation | Gain | Effort |
|---|---|---|---|---|
| D1 | Scripts `audit_model`, `rooms_without_doors`, `door_width_check`, `parameter_completeness` repeat the native model check | One **model check engine**. Retire `audit_model` and `rooms_without_doors`; the other two become check options (door width and parameter lists come from the requirements) | One truth, no drift | S |
| D2 | Seven settings files: `checkset.json`, `worksets.json`, `clash-rules.json`, `clash-colours.json`, `brand.json`, `team.json`, `config.json` | Split into **office settings** (brand, team, config, colours) and one **project requirements** file (check set, worksets, clash matrix, naming, LOIN). Old files keep working | Rules from the BEP in one place | M |
| D3 | *Clash Results* window and *Clash Browser* | One **Clash Browser**. The Clash Results button opens it on the stored results | Less to learn | S |
| D4 | Three ways to make an issue picture (preview pictures, coordination report, clash focus) | One **issue view engine**: section box around elements or points, highlight by role, export a picture. Also the base for **BCF** export (Navisworks, ACC) | Consistent pictures, BCF | M |
| D5 | `get_model_overview`, `get_model_brief` and the dashboard counts | The **brief** is the model knowledge; the overview becomes its quick level | One call for Claude | S |
| D6 | `clash_view`, `reset_clash_view`, `focus_clash` | One `clash_view` with overview / focus / reset | Simpler for Claude | S |
| D7 | Workset rules run in a script (Claude only); the model check only knows "Workset1" | A native **rules engine** in the add-in. The model check reports "elements on the wrong workset per the BEP"; the workset tool fixes them | Monitored all the time, not only when asked | M |
| D8 | The dashboard, Companion Home and Claude each read results their own way | A **status store**: each engine publishes its latest result (health, clashes, changes, requirements), and every surface reads it | Always the same numbers | S |
| D9 | Ribbon: *Model Health* (Audit) and *Dashboard* (Insights) are the same tool | One button | Clearer ribbon | S |
| D10 | *Parameter Check* (planned), "Key parameters filled" (model check), LOIN | One **information requirements** check driven by the LOIN | Checks what the project actually requires | M |
| D11 | *Submission Check* (planned), the Submission mode, "Submission readiness" checks | Submission check = the requirements monitor at the issue stage | No separate tool to build | S |
| D12 | *Design Check* / *Code Packs* (planned) and the rules engine | The same rules engine with different rule packs (design, code, client) | One engine, many packs | M |

## 5. The bigger circle: the BIM requirements hub

### What goes in

- **Project documents:** BEP (pre- and post-appointment), EIR / OIR / AIR / PIR, the LOIN / LOD matrix, MIDP / TIDP, the
  naming convention, CDE rules and client standards.
- **Office practice:** ISO 19650, the ACE standards and lessons from past projects.

### One project requirements file

Claude drafts it from the documents. The BIM manager reviews and approves it. Every rule keeps its **source**
(document, page, clause), so a finding can always quote where it came from.

| Section | Contents | Used by |
|---|---|---|
| Project and stages | stages, milestones, current stage | everything, by stage |
| Roles and responsibility matrix | who models what (e.g. slabs: STR; ceilings: ARC) | clash responsibility (replaces guessed ranks), worksets |
| Naming | files, views, sheets, families, types, worksets, parameters | naming check, workset rules |
| Worksets | the rules (category, function, level, zone...) | workset tool, model check |
| Coordinates | shared coordinates, survey point, north, levels datum | brief alignment check |
| LOIN per stage | per category: geometry (LOD), required parameters with type and allowed values, documentation | information check, parameter tools, Excel import |
| Model checks | enabled checks and thresholds | model check |
| Clash matrix | tests per discipline pair, tolerances, clearances, responsible party | clash engine |
| Deliverables | formats (PDF, DWG, IFC version, COBie, NWC), sheet sets | export, submission, transmittal |

### The analyser

Claude reads the documents (PDF, Word or Excel) and proposes the requirements, with sources. It also lists what
is missing or unclear, for example "no clearance for pipes near beams" or "the LOIN has no stage 4 column".

### The contradiction monitor

It finds three kinds of contradiction. Each finding has a severity, the quoted sources, a recommendation and an
action: the ACE tool that fixes it, with preview.

1. **Between the documents.** For example:
   - The BEP says LOD 300 at stage 3, but the LOIN matrix says 200.
   - The naming in the BEP differs from the CDE rules.
   - Two disciplines are responsible for the same element.
   - A clash tolerance in the BEP differs from the one in the clash matrix.
   - Required stage-4 parameters are missing at stage 5, so information decreases between stages.
2. **Between the requirements and the ACE settings.** For example:
   - A check the BEP requires is switched off in the check set.
   - A workset rule is never reached, because an earlier rule already catches every element it would match.
   - A workset name breaks the naming convention.
   - The colour or responsibility settings disagree with the responsibility matrix.
3. **Between the requirements and the model (compliance).** For example:
   - LOIN parameters are empty or have the wrong values.
   - Views or sheets break the naming convention.
   - Elements are on the wrong workset.
   - Coordinates don't match.
   - The LOD is too low for the stage (from simple geometry cues).
   - Clash tests are overdue.

**Best practice** (ISO 19650, ACE) is reported separately as a *recommendation*, never as a contradiction.

### Monitoring over time

- **Model compliance:** checked cheaply when a model is opened and after syncs, like the snapshots. It's shown in
  the dashboard, on a Companion tile and to Claude.
- **Documents:** analysed again when a new BEP or LOIN revision arrives. The monitor shows what changed between
  revisions and which rules that affects.
- **Trend per project:** compliance per stage and per discipline, for management.

## 6. A BIM Tools group on the ribbon

The ribbon is organised by circle, and each work mode opens its group. **BIM Tools** gathers what the BIM manager
uses, all driven by the requirements hub.

| Group | Tools | Main mode |
|---|---|---|
| Claude | Work mode, Companion, MCP Status, Undo | all |
| **BIM Tools** (new) | **Project Hub**, **Requirements** (analyse, contradictions, compliance), Model Check, Worksets (BEP), Naming Check, Change Tracker / Snapshots, Standards (check set) | Model audit, BIM management |
| Coordination | Clash Browser, Clash View, Coordination Report | Coordination |
| Production | Data (Excel in/out, parameters, smart select), Sheets and Views, Export | Production |
| Design | Test Fit, Structure from ARC, Design / Code Check | Design (new mode) |
| Delivery | Submission Check (requirements at issue), Prepare Submission, Transmittal | Submission |
| Team and Admin | Team Scripts, Users, Usage, Policy | hidden by default |

Two modes follow from this: **BIM management** (BIM Tools first) and **Design** (test fits, derivations, design
checks).

## 7. The Project Hub: one container for everything

Every analysis, rule and conclusion lives in **one container per project**. It opens from the ribbon (BIM Tools →
**Project Hub**) and from the Companion. It is the project's memory, and the place where all the circles can be seen.

### What it holds

Everything in the Hub is a **point** with an id, a type, its content, its source, who made it and when, and links to
the other points it depends on.

| Branch | Points | Where they come from |
|---|---|---|
| **Documents** | BEP, EIR, LOIN / LOD matrix, naming convention, MIDP / TIDP, client standards, each with its revisions | Added by the BIM manager (a file or a CDE link) |
| **Models** | This model, its links, the other discipline models: discipline, alignment, snapshots | Model brief, change tracker |
| **Requirements** | The project requirements (section 5), rule by rule, each quoting its clause | Analyser (Claude), reviewed by the BIM manager |
| **Rules** | Check set, workset rules, clash matrix and responsibility, naming rules, colours | Requirements, office settings |
| **Analyses** | Every run: model check, clash test, requirements compliance, document analysis, each with its date and inputs | The engines |
| **Findings** | Contradictions, compliance issues, clash issues, recommendations, with severity and status | The analyses |
| **Decisions** | Approved clashes, accepted deviations, answers to open questions, with who decided and why | The team |
| **Lessons** | What ACE learnt on this project | Lessons store |

The links make the circles visible, e.g. *LOIN clause 4.2 → rule "Doors need Fire Rating at stage 4" → 37
doors failing on L3 → decision "fire strategy pending, accept until stage 5"*.

### Click a point to edit it (where that is efficient)

Not everything should be edited the same way. The rule is: **edit what people own, annotate what engines compute,
reference what comes from outside.**

| Point | On click | Why |
|---|---|---|
| Requirements and rules | **Edit in place** (a form: value, threshold, workset name, tolerance). The Hub shows what depends on it, marks those analyses *out of date* and offers to re-run them | People own the rules; one edit updates every tool that reads them |
| Findings | **Change status** (open, accepted, fixed, not an issue) and **add a note**; *Show in model* selects and zooms; *Fix* runs the tool that fixes it, with the usual preview | A finding is computed: editing its text would break the link to the model |
| Analyses | **Read-only**, with *Run again* and *Compare with the previous run* | They are records; changing them would lose history |
| Decisions | **Edit**, with the history kept | They record a judgement that may change |
| Documents | **Open**, see revisions, see which requirements quote it; *Analyse this revision* | The source stays untouched |
| Models | *Open*, *Show in the clash view*, its snapshots and alignment | They are references |

Every edit is saved with who and when (an audit trail). Rules and requirements can be locked to the BIM manager's role.

### How it works

- **One store, many readers.** The Hub is the status store and the requirements store together (D2, D8). The model
  check, clash engine, workset tool, Companion, dashboard and Claude all read from it and write their results to it.
  Nothing keeps a private copy.
- **Where it lives.** It sits in the project folder on the shared drive or the CDE, next to the central model, so the
  whole team sees the same Hub. A local copy is kept for offline work. The format is plain files (JSON per branch plus
  the documents), so it can be backed up, compared and versioned. Personal notes can stay local.
- **Out-of-date tracking.** Every analysis records the rules and model snapshot it used. When a rule changes or the
  model changes a lot, the analysis is marked out of date, and the Hub offers to re-run it.
- **Claude.** One tool, *project_hub* (read, search, propose an edit), so Claude always works from the project's
  current rules and past conclusions, and quotes them. Claude proposes edits to rules; people approve them.

### The window

- **Left:** the branches as a tree, with counts and red markers where something is out of date or contradictory.
- **Middle:** the list of the selected branch, with a search and filters (stage, discipline, status, severity).
- **Right:** the point itself. Content, source, links in and out ("depends on", "used by"), history, and the actions
  (edit, status, show in model, fix, run again).
- **Later:** a **map view** that draws the points and links as circles, showing management how requirements, rules,
  models and findings connect.

## 8. The integration rule (for every new capability)

Before building:

1. **Which engine does it belong to?** Extend an engine rather than add a parallel one.
2. **Where do its rules come from?** The requirements hub or the office settings, never a new private file.
3. **What does it publish?** Its latest result goes to the status store, so the dashboard, the Companion and Claude
   see it.
4. **Which existing capabilities does it connect to, and what new circle does that form?** Write it in section 3.
5. **What does it replace or merge?** Add it to section 4.
6. **What does it store in the Project Hub?** Its rules, results and decisions go there as points with links, not
   into a private file.

After releasing: a benchmark case, the learning loop, and a review of this document each quarter (or every minor
version).

## 9. Proposed order (for approval)

1. **Status store and quick merges:** D3, D5, D6, D8, D9. These are small and make the base cleaner.
2. **Rules engine and project requirements file:** D2 and D7, which also feed D1 and D10. Existing files keep working.
3. **Project Hub (store and window) and requirements hub:** the container first, then the analyser and the
   contradiction monitor (documents, settings, model), and the BIM Tools group.
4. **Issue view engine:** D4, then BCF export.
5. **Rule packs:** D11 and D12 (submission, design and code checks), all on the same engine.
