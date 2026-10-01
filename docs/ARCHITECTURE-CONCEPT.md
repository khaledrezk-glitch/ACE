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

### Principle: smart and proactive, not rigid

Every capability should behave like an experienced colleague, not a form.

**Proactive: it notices and offers.**
- When a model opens: "The MEP link changed since yesterday: 40 elements moved near your beams. Run the STR vs MEP test?"
- When it sees a pattern: "You renamed 12 views to the new standard by hand. Shall I do the other 86? (preview first)"
- When it sees the stage: "Stage 4 starts next week per the BEP. 37 doors still miss Fire Rating."
- When the work changes: "You're resolving clashes. Switch to Coordination mode?"
- When a task is done: it offers the natural next step, e.g. after a clash run, "prepare the coordination report?"

**Smart: it understands, rather than matching exactly.**
- **Engines give facts; Claude gives judgement.** The engines are exact and repeatable (clashes, counts, rules), and
  Claude interprets them: what matters most, why, and what to do first. That's the difference from a rigid checker.
- **Intent over literal rules.** It matches names and categories loosely ("Door - Single" and "SGL_Door"), reads a BEP
  written in prose, and asks when it's unsure instead of failing.
- **Context decides.** The same finding matters more at issue than at concept stage, in the owner's discipline than
  in a link, and on a new element than on an accepted one.
- **Rules are strong defaults, not walls.** Any rule can be accepted as a deviation with a reason. The reason becomes
  a decision in the Project Hub, and ACE stops repeating it.
- **It adapts.** Thresholds and priorities learn from the project's history (what the team fixes, what it accepts).
  Lessons carry across projects.
- **It explains itself.** Every suggestion says why, from which source (clause, rule, model change), and what the
  fix would change.

**Guardrails, so being proactive never becomes risky or noisy:**
- Suggestions never change the model by themselves. Acting on one runs the tool with the usual preview and approval.
- **Proactivity dial**, per user: *off*, *quiet* (a count only), *normal* (cards), *eager* (also as you work). The work
  mode tunes it; for example, Production batches suggestions until you pause.
- The learning loop measures which suggestions people accept or ignore, and drops the ones that don't help.

In the architecture this is an **observer layer**: it watches events (open, sync, element changes, link reloads,
stage dates, repeated actions, idle time), asks the engines, and posts **suggestions** through the same channel as the
notifications (section 8). Each suggestion has a one-click action.

## 2. Architecture in layers

```
 Surfaces     Ribbon (work modes, BIM Tools group) | Companion | Claude (MCP tools) | HTML / Excel reports
 ------------------------------------------------------------------------------------------------------------
 Observer     Watches events and patterns → suggestions and notifications (proactive, with a dial per user)
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
| D1 | Scripts `audit_model`, `rooms_without_doors`, `door_width_check`, `parameter_completeness` repeat the native model check | One **model check engine**. Retire `audit_model` and `rooms_without_doors`; the other two become check options (door width and parameter lists come from the requirements) | One truth, no drift | S · **done (1.7.0)** |
| D2 | Seven settings files: `checkset.json`, `worksets.json`, `clash-rules.json`, `clash-colours.json`, `brand.json`, `team.json`, `config.json` | Split into **office settings** (brand, team, config, colours) and one **project requirements** file (check set, worksets, clash matrix, naming, LOIN). Old files keep working | Rules from the BEP in one place | M |
| D3 | *Clash Results* window and *Clash Browser* | One **Clash Browser**. The Clash Results button opens it on the stored results | Less to learn | S · **done (1.7.0)** |
| D4 | Three ways to make an issue picture (preview pictures, coordination report, clash focus) | One **issue view engine**: section box around elements or points, highlight by role, export a picture. Also the base for **BCF** export (Navisworks, ACC) | Consistent pictures, BCF | M · primitives shared (`Util/ViewTools`, 1.7.0) |
| D5 | `get_model_overview`, `get_model_brief` and the dashboard counts | The **brief** is the model knowledge; the overview becomes its quick level | One call for Claude | S · **done (1.7.0)** |
| D6 | `clash_view`, `reset_clash_view`, `focus_clash` | One `clash_view` with overview / focus / reset | Simpler for Claude | S · **done (1.7.0)** |
| D7 | Workset rules run in a script (Claude only); the model check only knows "Workset1" | A native **rules engine** in the add-in. The model check reports "elements on the wrong workset per the BEP"; the workset tool fixes them | Monitored all the time, not only when asked | M |
| D8 | The dashboard, Companion Home and Claude each read results their own way | A **status store**: each engine publishes its latest result (health, clashes, changes, requirements), and every surface reads it | Always the same numbers | S · **done (1.7.0)** |
| D9 | Ribbon: *Model Health* (Audit) and *Dashboard* (Insights) are the same tool | One button | Clearer ribbon | S · **done (1.7.0)** |
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

- **Model compliance:** checked cheaply as you model (only the changed elements), when a model is opened and before
  syncs. It's raised as notifications (section 8) and shown in the dashboard, on a Companion tile and to Claude.
- **Documents:** analysed again when a new BEP or LOIN revision arrives. The monitor shows what changed between
  revisions and which rules that affects.
- **Trend per project:** compliance per stage and per discipline, for management.

## 6. A BIM Tools group on the ribbon

The ribbon is organised by circle, and each work mode opens its group. **BIM Tools** gathers what the BIM manager
uses, all driven by the requirements hub.

| Group | Tools | Main mode |
|---|---|---|
| Claude | Work mode, Companion, MCP Status, Undo | all |
| **BIM Tools** (new) | **Project Hub**, **Requirements** (analyse, contradictions, compliance), Model Check, Worksets (BEP), Naming Check, **Family Checker**, Change Tracker / Snapshots, Standards (check set) | Model audit, BIM management |
| Coordination | Clash Browser, Clash View, Coordination Report | Coordination |
| Production | Data (Excel in/out, parameters, smart select), Sheets and Views, Export | Production |
| Design | Test Fit, Structure from ARC, **Family Creator**, Design / Code Check | Design (new mode) |
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

## 8. Notifications, like Revit warnings

The requirements monitor and the other engines don't wait to be asked. When something breaks a requirement, ACE
tells the person who caused it, at the moment they cause it, the same way Revit warns about overlapping walls.

### When a notification appears

| Moment | Example | How it is shown |
|---|---|---|
| **While modelling** (an element is added or changed) | A door placed at stage 4 without Fire Rating; a wall on the wrong workset; a view named against the convention; a duct entering a beam | A **Revit warning** with ACE's text, listing the elements (the same dialog and Show button as Revit's own warnings), or a quiet ACE toast, depending on the rule's severity |
| **Before Sync with Central** | "5 new BEP issues since your last sync" | A summary with *Show*, *Fix* and *Sync anyway*. Only rules marked *error* in the requirements can ask you to fix before syncing, and only if the BIM manager turns that on |
| **When a model is opened** | "12 open requirement issues, 3 new since yesterday; the MEP link changed" | A Companion card and the Home tile |
| **When a document or a link changes** | A new BEP revision contradicts the LOIN; the STR link now clashes with your ducts | A Companion card with the contradiction and both sources quoted |

### How it works in Revit

- **Real Revit warnings.** ACE registers its own warning types when Revit starts (Revit's `FailureDefinition`), and
  a model updater (Revit's `IUpdater`) checks only the elements that changed against the rules that concern them. So
  the check is fast, and the warnings look and behave like Revit's.
- **One list, like Manage > Warnings.** Revit forgets posted warnings between sessions, so ACE keeps its own
  persistent list in the **Project Hub** (Findings branch). It's opened from the ribbon and the Companion, grouped by
  rule and severity. Each row has *Show in model*, *Fix* (the tool that fixes it, with preview) and *Accept with a
  reason* (which becomes a decision).
- **Counts everywhere.** The number of open ACE warnings appears on the Companion tab, the Home page, the dashboard and
  Claude's status. The count per model gives a trend, like Revit's warning count.

### Keeping it useful, not noisy

- **Severity from the requirements:**
  - *info*: listed only;
  - *warning*: a toast or a Revit warning;
  - *error*: a Revit warning, and optionally a check before sync.
- **Once per element and rule.** It doesn't repeat until something changes. Accepted items stay silent and carry
  their reason.
- **Only what applies now:** the current stage, the user's discipline and model, and the work mode. For example, the
  Production mode can batch warnings until you pause.
- **Personal settings:** *show as toast*, *show as Revit warning* or *list only*, per severity. The BIM manager sets
  the minimum.
- **Batched.** One message for a group of elements ("14 doors without Fire Rating"), not fourteen.

## 9. The integration rule (for every new capability)

Before building:

1. **Which engine does it belong to?** Extend an engine rather than add a parallel one.
2. **Where do its rules come from?** The requirements hub or the office settings, never a new private file.
3. **What does it publish?** Its latest result goes to the status store, so the dashboard, the Companion and Claude
   see it.
4. **Which existing capabilities does it connect to, and what new circle does that form?** Write it in section 3.
5. **What does it replace or merge?** Add it to section 4.
6. **What does it store in the Project Hub?** Its rules, results and decisions go there as points with links, not
   into a private file.
7. **Does it raise notifications?** Findings that people must act on use the one notification system (section 8),
   with severity from the requirements.
8. **Is it smart and proactive?** Does it adapt to context, explain why, allow a reasoned deviation, and offer the next
   step through the observer, instead of waiting to be asked?

After releasing: a benchmark case, the learning loop, and a review of this document each quarter (or every minor
version).

## 10. Proposed order (for approval)

1. **Status store and quick merges:** D3, D5, D6, D8, D9. These are small and make the base cleaner.
2. **Rules engine and project requirements file:** D2 and D7, which also feed D1 and D10. Existing files keep working.
3. **Project Hub (store and window) and requirements hub:** the container first, then the analyser and the
   contradiction monitor (documents, settings, model), its **notifications** (Revit warnings, the ACE warnings list,
   before-sync summary) and the BIM Tools group.
4. **Issue view engine:** D4, then BCF export.
5. **Rule packs:** D11 and D12 (submission, design and code checks), all on the same engine.

## 11. Showcase: what to present when it is strong

A presentation is due when a circle is **complete and verified live in Revit**. Each entry has the story (the
hidden capability and the thinking behind it) and the demo that proves it.

| Circle | Story | Demo | Ready? |
|---|---|---|---|
| **Who caused this clash** | Clash detection alone says *what* clashes. Joined with the change tracker, ACE says *whose change made it and when*, and separately *who should give way* by the priority rules | Move a duct in the MEP link, re-run: the new clash names the change and the person | Built, not yet verified live |
| **Navisworks-style review inside Revit** | Two models in colour, one clash at a time: everything else dimmed, zoomed to the intersection even on a large slab, and linked elements coloured, which Revit itself can't do | Clash Browser → click through issues → Coordination report for the meeting | Built, not yet verified live |
| **Issues, not clashes** | 40 clashes can be 3 real problems: one duct through 8 beams is one issue, with one responsible discipline | The same test as a raw list, then grouped | Built, not yet verified live |
| **Office test fits** | "How many people fit in this office?" answered by placing real desks, with options applied one by one | Management demo (done 29 Sep) | Shown |
| **The model knows its own health** | Model Checker-style checks with an office check set, a score and a trend, plus coordination status on one page | Dashboard before and after a clean-up | Built, not yet verified live |
| **Learning loop** | ACE gets better from its own mistakes: lessons, first-time-right trend, benchmark before every release | Learning report over a month | Running |
| **The requirements hub** (planned) | The BEP, LOD and LOIN become rules that every tool follows; contradictions are found between documents, settings and the model, and raised like Revit warnings | A BEP revision that contradicts the LOIN, then a door without Fire Rating raising a warning | Concept |
| **Project Hub** (planned) | Everything the project knows, analysed and decided, in one place, with every point linked to its sources, so the circles can be seen | The map view: clause → rule → failing elements → decision | Concept |

## 12. Family creator and checker (idea, studied with the integration rule)

**Idea:** Claude creates Revit families from a description, a catalogue sheet or a sketch, and checks families (one, a
project's families, or a whole library folder) against the office and project standards.

### Integration rule answers

| Question | Answer |
|---|---|
| Which engine? | **Family intelligence** (today `describe_family`: footprint, insertion point, placement, nested families), extended to open family documents. The **rules engine** holds the family standard; the **model check engine** runs it; the **issue view engine** makes the pictures |
| Where do the rules come from? | The requirements hub: the office family standard (naming, category, required and shared parameters, subcategories, materials, detail levels) and the project LOIN (geometry level and parameters per stage) |
| What does it publish? | Family check results to the Project Hub (a Families branch: each family a point, its findings, its compliance); notifications when a non-compliant family is loaded into a project |
| What does it connect to? | Test fit and space planning (they read the families' real footprint and facing), LOIN compliance, the model check (in-place families, unused types), the Excel parameter tools, and the lessons (for example "this office desk's origin is at its centre") |
| What does it merge? | The planned *Parameter Manager* (shared parameters in families) and part of *Design Check*. Family naming joins the naming check |

### The checker

It checks each family for:

- **Naming** (family, types, parameters).
- **Category and subcategories.**
- **Parameters:** the required and shared ones, with their groups and units.
- **Origin and insertion point:** sensible reference planes, the insertion point at the right place.
- **Flexing:** every type is loaded and the key dimensions are stepped, reporting what fails to regenerate.
- **Graphics:** visibility per detail level, materials assigned.
- **Size:** file size and nested families.
- **Content:** no CAD imports, no in-place families.
- **Level of detail:** the geometry level against the LOIN for the stage.

It works in three places:

- in the **project** (the loaded families);
- in the **family editor** (the open family);
- on a **library folder** (batch, read-only, with a report).

### The creator

1. Claude drafts the family: template, reference planes and labelled dimensions, geometry (extrusions, sweeps,
   voids), parameters and types, subcategories and materials.
2. It checks the draft with the checker.
3. It flexes the draft.
4. It shows a preview picture.
5. It saves the family only after approval.

It starts from office templates and keeps to the family standard, so a new family passes the checker by construction.
It suits simple, parametric families (furniture, equipment, casework, generic components); complex families stay
manual, with Claude's help.

### Circle it forms

**Families that are right from the start.** The standard defines them, the creator makes them, the checker proves
them, the notifications catch non-compliant ones at load time, and the test fit and LOIN checks rely on their data.

## 13. Benchmark: "15 features for your company MCP"

The reference is a LinkedIn post by Omar Raafat (AEC software engineer), Sep 2026. Its architecture is *AI agent →
skills → tools → software*, held together by *hooks + learning + sync*. ACE is compared with each of the 15 features:

| # | Feature | ACE today | Status |
|---|---|---|---|
| 1 | **Warnings handler:** Revit warnings during a run go back to the AI; on error, roll back | ModelGuard catches warnings and dialogs and returns `revitWarnings`; failures roll back everything | **Have** |
| 2 | **Verify before commit:** the AI checks the result before committing; if the check fails, everything reverts | Dry run + preview pictures + approval. There's no automatic check of the result inside the transaction | **Partly**: see G1 |
| 3 | **Dry run:** know what will be added, changed or deleted, without changing the project | `dry_run`, `wouldChange`, preview gate (an apply needs an identical preview) | **Have** |
| 4 | **API lookup:** search the API of the installed version rather than old knowledge | `revit_api_lookup` reflects the installed Revit 2025 API | **Have** |
| 5 | **Hooks:** document opened → check; before sync → audit; element changed → update | Automatic snapshots on open, save and sync. The observer and notifications (sections 1, 8) are concept | **Partly**: concept covers it |
| 6 | **Skills tree:** knowledge in branches (Revit → Structure → Rebar; Modelling; QA/QC); load only what's needed, so less context and fewer tokens | Guides on demand (`revit_guide`), but flat; the connection instructions are long | **Partly**: see G2 |
| 7 | **Skills system:** tools say what the AI *can* do; skills teach *how the company does it* (standards, workflows, rules, examples) | Guides, lessons, built-in scripts; requirements hub and Project Hub (concept) | **Partly**: see G2 |
| 8 | **AI learning skill:** learn from corrections and successful workflows; knowledge becomes reusable | Lessons, learning report (first-time-right, recipe candidates), benchmark, promotion into built-in lessons | **Have** (G2 makes skills the target) |
| 9 | **Save commands as tools:** a command that works and repeats becomes a fixed tool, one call | `save_script` / `run_saved_script`, team library, recipe candidates | **Have** |
| 10 | **Tools and skills sync:** a central repository, engineer → repository → review → all devices | Team scripts and lessons folders, shared package. **No review step** before sharing | **Partly**: see G3 |
| 11 | **Long-running jobs:** heavy operations in the background, a job id to follow progress | Calls wait up to 15 minutes; no background jobs or progress | **Gap**: see G4 |
| 12 | **Visual check:** modify → capture → check → fix | `view_image`, preview pictures in plan and 3D, clash and coordination pictures | **Have** |
| 13 | **Variables between runs:** keep values between calls instead of sending the context again | The brief is cached for 10 minutes; nothing else persists between runs | **Gap**: see G5 |
| 14 | **QA rules:** company standards as rules the AI audits automatically | Model check with an office check set; workset rules; requirements hub (concept) | **Have**, growing |
| 15 | **Changes tracking:** who changed what (person or AI), which tool or skill, and when | Journal (every Claude change), change tracker (by person), telemetry. They aren't joined yet | **Partly**: see G6 |

Where ACE already goes further than the list: preview pictures before applying, Apply cards in the Companion, the
clash engine with responsibility and cause, Navisworks-style review with linked elements coloured, working modes,
the model brief across links, and the learning benchmark.

### Gaps to close (each answered with the integration rule)

| Gap | What | Engine / circle |
|---|---|---|
| **G1 Verify before commit** | A script or tool states what must be true afterwards (e.g. "every desk inside the room", "no new warnings", "count = 24"). The runner checks it inside the transaction and rolls back with the reason if it fails. Claude adds these checks to its own code | Platform (code runner). Joins the safety gate: preview → verify → approve |
| **G2 Skills tree** | Turn guides, lessons and office standards into **skills**: a tree (Revit → Modelling / Coordination / QA / Structure / MEP; Office → standards / workflows / examples) with a short index at connection and branches loaded on demand. Lessons and saved scripts attach to their branch | Knowledge layer. Joins lessons, guides, requirements hub. Lighter instructions, fewer tokens |
| **G3 Sync with review** | Shared scripts, skills, lessons and rules go **engineer → team queue → reviewer (BIM manager) → everyone**, with versions. The Project Hub shows the queue | Learning loop + Project Hub |
| **G4 Long-running jobs** | Clash tests on big models, library-wide family checks, batch exports run as background jobs: a job id, progress in the Companion, a cancel button, the result in the Project Hub when done | Platform (bridge) + Companion. Needed for the family checker and requirements monitor |
| **G5 Variables between runs** | A session store: named results ("the L3 rooms", "last clash issues") and values that later calls can use without resending them | Platform (code runner `ctx.Session`) + MCP server |
| **G6 Who changed what, including Claude** | The change tracker marks elements changed by Claude with the tool, skill or script, the approval (panel or chat) and the person who approved, joined with the journal. "What changed this week" separates people from Claude | Change tracker + journal: a new circle, *full traceability* |

These go into the build order (section 10) when development resumes:

- G1 and G5 with the quick merges;
- G2 and G3 with the rules engine and requirements;
- G4 before the family checker and the requirements monitor;
- G6 with the Project Hub.

