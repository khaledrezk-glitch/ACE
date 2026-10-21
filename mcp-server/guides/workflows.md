# Workflows: coordination, changes, model health, worksets, test fits, production

How to run ACE's built-in workflows well. The tools named here are MCP tools; the scripts are saved scripts (run_saved_script).

## Coordination and clashes

Office methods (the office's agreed way of doing a task, from a recorded solve) are in revit_guide "methods"; a tool
linked to one says "Office method: revit_guide methods/<name>" in its description: read it before using the tool for
that task. Adding a method: guides/methods/<name>.md and one entry in guides/methods/index.json.

run_clash_test finds clashes across this model, its links and the other open models (standard tests "STR vs MEP", "ARC vs STR", "MEP vs ARC", "MEP vs MEP", or "all"; or custom categories), with depth, level, location and the RESPONSIBLE discipline (the element that is easier to move gives way). Clashes are grouped into ISSUES (one element that has to move and everything it hits, e.g. one duct through 8 beams): report and coordinate by issue (topIssues), not clash by clash. Connected MEP parts and hosted elements are not clashes. A new clash carries its CAUSE when snapshots exist: which side was added, moved or retyped since the last run, and by whom (workshared). Keep the two apart when you report: "responsible" says who should give way, "caused by" says whose change made it. Status persists between runs (new / active / resolved / approved; a resolved clash that comes back is new again and "reopened", worth pointing out): report what is new and what was resolved since last time, grouped by responsible discipline and level. For a walk-through ("show me the clashes", "go through them with me") use clash_view with the clash key, issue by issue (the user sees only the two elements, coloured by model, zoomed to the intersection) and clash_view without a key to show both models in colour; the user can do the same in ACE > Coordination > Clash Browser. To compare with one model use run_clash_test with_model (the secondary; the primary is this model). A BIM manager can compare two links: primary_model = one link, with_model = the other (this model is then ghosted). Colours always follow the role: primary green, secondary red; with several links each link has its own colour, which the user can change in the Clash Browser legend or with clash_view colours. For a coordination meeting ("prepare the clash report", "issues for the meeting") use coordination_report: a picture per issue, responsible, cause, location, and a CSV list for Excel; give the user both file paths. Results from other models and stored results the run did not re-check come on their own line ("stored, not re-checked in this run: N") and are not in the open totals: report the open count of this run, and say the stored ones are older. Offer set_clash_status to approve accepted ones (e.g. sleeved penetrations). Check coordination_sources and the brief's alignment first: if levels or grids don't line up, clash results are unreliable - say so. Structure from architecture: in the STR model (ARC linked or open), run the saved script derive_structure_from_arc - first with check_only: true to list what is missing on each side, then a preview with preview_image: true, then apply after confirmation.

Each issue has an issueKey: clash_view with it focuses on the issue's deepest clash, and set_clash_status with it
approves or reopens every clash of the issue. For "what changed and does it create new clashes": call model_changes
with save_snapshot: false (a new snapshot would become the baseline the clash causes are measured against), then
run_clash_test. On a large federated model, run per level (level) or per link (with_model): a run that hits
max_elements is incomplete and does not mark anything resolved.

Navisworks reports: when the user has a Clash Detective report ("here is the Navisworks report", a .xml or .html
file), call run_clash_test with import_report: the full path (ask for the XML export if they have both; it is exact,
the HTML tabular report also works when it includes Item ID and the source file). Each Navisworks test becomes the ACE
test "NW: <name>": the items are matched to the elements of this model and its links or open models by file name and
element id, so responsibility, issues, cause, clash_view, set_clash_status and coordination_report work on it as on
ACE's own clashes. Report "matched" per test: items in models that are not loaded keep their Navisworks name and id but
cannot be shown or selected; name those models (notes) and suggest linking them. Re-importing a newer report tracks
new / active / resolved; Navisworks Approved and Resolved are kept and approvals made in ACE survive. ACE's own run and
the Navisworks import can disagree (tolerances, Navisworks rules): present them as two tests, do not merge them.

Material filters: run_clash_test, coordination_report and clash_resolve take materials (only these, both sides),
exclude_materials (leave these out) and a_materials / b_materials (per side), from steel, concrete, timber, masonry,
precast, other and unknown (no structural material set). The answer counts what the filter left out (filtered, per
material): always say it, so a short clash list is never mistaken for a clean model.

Interference report (a client-readable list of every clash, like Revit's Interference Report): coordination_report
with interference: true and out_dir (the folder the user names; ask when they did not), plus the same test, level,
category, discipline and material filters as run_clash_test. ACE writes interference-<model>-<test>-<stamp>.html
(header with model, test, date, filters and counts by status and by pair; a table grouped by issue: A and B as category
: family : type : id, level, depth mm, location x / y / z, status, responsible), .csv and .xlsx, one set per model.
Give the user the paths and the counts.

A typical structural job: (1) run_clash_test test "ST vs ST" with exclude_materials ["steel"] and the stairs left out of
a_categories / b_categories (e.g. columns, framing, walls, floors, foundations) to see the concrete interferences;
(2) coordination_report interference: true with the same filters and out_dir, for the client; (3) clash_resolve with
materials ["concrete"] (action plan, then fix with dry_run, then the identical call) to join what can be joined; the
material scope is part of the preview, so the apply must carry the same materials; (4) run_clash_test again.

Structural self-clashes (ST vs ST): for "fix the structural clashes", "interference check ST vs ST" use clash_resolve.
The default test is "ST vs ST" (structural columns, framing, walls, floors, foundations, stairs, ramps against the same
set); a_categories / b_categories, level, tolerance_mm and keys (clash or issue keys from run_clash_test) narrow it.
Only four fix kinds exist, all of them keep every element where it is: join (Join Geometry, concrete with concrete),
switch-join (the join exists, the wrong element wins), cutback (a steel member end stops at the column face; the
location line and the analytical connection stay) and cope (steel into steel). It never deletes, recreates or moves an
element, so ids, tags, dimensions, schedules and sheets stay. Everything else (duplicates, overlaps that need a design
decision, mixed materials) is report-only with a reason and a recommendation: give those to the user, never "fix" them
another way. The join order is one category priority list, strongest cuts (default Structural Foundations > Structural
Columns > Walls > Floors > Structural Framing > Stairs; same category: the larger element cuts); the reply says whether
it came from the request, the vault or the default. Scope: "clashing" (default: only clashing pairs) or "all-touching"
(every touching pair of the categories, as a person cleans an area; selection: true limits it to the current selection);
not-touching pairs are dropped and counted. Trim to a face (a beam or wall end trimmed to the face it runs into) is
used only when the vault allows it (trim_to_face { "allowed": true, "preview_required": true, "max_mm": n }); a call can
pass trim_to_face: false, never switch it on. Read every trim to the user (element, end, length before and after, the
face) before the apply. With all-touching the plan can hold hundreds of joins: report it per category pair.
All-touching runs in chunks (chunk_size, default max_pairs 500, also from the rules file; by level then id, stable after
an earlier chunk is applied): the reply lists every chunk and which one is planned; each chunk is its own dry run, apply
and undo step; then "run chunk 2 with the same args" (chunk: 2). A missing join is planned only when the two really
touch (near pairs: no step; pairs that no longer touch at run time are dropped and counted). An existing join in the
wrong order that does not clash is "Flip, confirm" (pair, current cutter, the rule's cutter): it switches only when the
user picks its id and the call passes it in flips (ids not on that list are refused). The reply gives the concrete
volume and area per category before and after, and names the plan item behind each new warning. same_category is
true by default (wall with wall, slab with slab joins are part of the office method), false opts out. A call with keys
works on those clashing pairs only, even when the rules say all-touching; a level or test narrows the touching pairs.
preview_required is always effectively true: every apply needs the identical dry run (false is refused). A trim is
skipped when the member hosts openings, and only one trim per end is kept (the smallest). The order and the steel
switches come from the project vault (`rules/clash-resolve.json`: { "order": [...], "scope": "...", "trim_to_face": {...},
"steel": { "cutback": true, "coping": true } }) over the office file (`%APPDATA%\ACE-RevitMCP\clash-resolve.json`, or
config.json "clashResolveRules"; example in bep/clash-resolve.example.json): the project wins key by key, trim_to_face
merges per sub-key, and the reply names which file each setting came from (project_rules section "clash-resolve" shows
the merged rules), a call's order or steel wins. Stages: action plan (read-only: before by pair, the plan by kind, the
report-only list, blocks), then fix with dry_run: true (in a rolled-back transaction: the clashes that would remain,
the annotation guard, the warnings before and after), then the identical call after the user's OK (ONE undo step,
evidence file `evidence-clash-<model>-<stamp>.md` in the vault's 04 Audits/<model>/clashes or report_dir). Borrowed
elements, groups, design options or a read-only model stop the fix before the dry run, each with what, why, who and how
to clear it. A guard difference (an element deleted, retyped or moved, or a tag, dimension or spot elevation lost or changed), a failed
self-check or a new warning means nothing is applied: report it as "would have been ...; rolled back, nothing changed".
Dimension values that changed on a cut-back member while their references stayed are information, not a failure:
mention them. If the model changes between the dry run and the apply, the apply is refused ("the model changed since
the preview"): run the dry run again. Another test name (e.g. "Arch vs ST") runs with the categories and disciplines
the add-in defines for it, unless a_categories / b_categories are given.
After an apply, re-run run_clash_test with the same test so the fixed clashes show as resolved.

## Opening models and running a batch

open_model opens an .rvt in the Revit session ACE talks to and makes it active: detach (none, preserve_worksets,
discard_worksets), audit, worksets (all, last_viewed, none) and close_previous. For a central model or a model on the
client's share, open it detached with preserve_worksets so the central is never touched; discard_worksets removes the
workset information from the opened copy and needs the user's explicit OK (confirm: true). Opening can take minutes.
close_model closes a model that is not active, without saving: ask before closing a model with work in it.

A batch over several models (e.g. the same clash check and interference report for every structural model of a
package): for each path, open_model with close_previous: true (and detach preserve_worksets for centrals), run the
read-only steps (run_clash_test, coordination_report interference: true with out_dir, clash_resolve action plan), and
collect one line per model. The reply says which model was closed, or that the previous one was kept open and why
(e.g. unsaved changes): report that, never close it another way. A fix (clash_resolve fix, parameter_fill) still needs
its own dry run and the user's OK per model. A detached copy is saved only with save_model save_as (see below).

save_model saves a named model (handle, path or model) in place; save_as { path, overwrite, as_central } writes it to a
new .rvt (existing folder; an existing file only with overwrite: true; as_central only for a workshared model, e.g. a
detached copy that becomes a new central). Both need allow_risky after the user agreed, and cannot be undone. After
save_as the open model IS the new file. A central saved at a new path leaves other users' locals on the old central and
links to the old file are not repointed: say so. Never do Save As in execute_revit_code when save_model can.

The open_model reply lists every dialog Revit raised while opening with the answer ACE gave and why; a dialog that
"needed a person" (no answer) is a warning: tell the user and check the model before relying on it. It also lists
unresolved links (type and path) and whether the file was flagged transmitted. Unresolved links belong in the health
report too: mention them with the model's findings (model_dashboard) and in the batch summary, never only in passing.
clear_transmitted writes into the file on disk: only on a working copy, with confirm: true after the user's OK.

## Change tracking and linked models

model_changes compares this model and its linked models with an earlier snapshot (since: last, today, yesterday, week, month, a weekday such as monday, "3 days", or a date): added, deleted, moved, retyped and changed elements, by category and by person. Use it for "what changed", "what did STR change since Monday", and after a link is reloaded. Before a milestone (issue, submission), offer snapshot_model with a label. get_model_brief lists the linked and open discipline models and whether their levels and grids line up: when they don't, say so before any coordination work.

## Recording a person's method (capture_method)

When the user wants ACE to learn how they solve something by hand ("watch how I fix these", "record my method"):
capture_method start with a label (and categories, default structural) before they begin; they work in Revit as
usual; capture_method stop when they are done (status says whether a capture runs). The model is never changed. The
case is written to the vault (cases/<date>-<label>.json and .md; trusted vault, else out_dir): the element changes
grouped into actions by pair type ("Floors x Structural Columns: join added; Floors cut Structural Columns in 5 of 6;
exceptions ..."), single-element actions per category (moved, end extension changed, type changed), the Revit commands
from the journal in order (readable names; unknown ids kept raw) and, when a clash_resolve run record exists for the
model (found by the clash store key, the central's name), a comparison per element pair against the plan's own cutter:
agree, differs, planned, not done, not planned, with join-direction agreement per category pair. Area, volume and
annotation changes are grouped too ("Floors: area changed on 3 (sketch edits likely)", "Annotation: dimensions 120 ->
119"): sketch edits show only there. Summarise the actions and the
differences for the user. The case ends with proposed rules (e.g. a join order): they need the owner's confirmation and
are never written into the vault rules by ACE on its own.

## Learning from a recorded case (the user's "Record my work")

The user can record a task themselves: ACE tab > Record my work (a label and an optional note at start, "what was the
rule you followed?" at stop). It is capture_method with the user in control: it records every model they work on
(element, parameter, geometry, join and sketch changes, the views used, Revit's transaction names, the journal) and
writes the case into the project's vault cases/ (else the ACE folder's cases/), marked recordedBy "owner (ribbon)". While
it runs, capture_method status shows it and capture_method start / stop are refused: only the user stops it. Afterwards:
capture_method list shows recent cases ("not analysed yet" for a new ribbon case); capture_method list with case: <file>
completes it (actions per model, journal commands, the clash_resolve plan comparison) and writes the analysis back. Then
follow the capture-best-practice flow: turn the case into proposed rules (what was done, when, the exceptions, with the
user's closing note as the stated rule and the counts as evidence), show them to the owner and ask for confirmation of
each; only confirmed rules go into the vault rules or an office method (revit_guide methods). Never treat a case as a
rule by itself, and say where the record is incomplete (its caps: e.g. elements outside the start snapshot show as
"modified" with no state before).

## Model health and status

for "how healthy is this model", "audit", "dashboard" or "status report", call model_dashboard (read-only; show: true opens it in Revit). It returns the 0-100 score, each finding with its score impact and sample ids, and the path of an ACE-branded HTML report the user can share. Explain the top findings in plain words and offer fixes (each fix follows the preview protocol). The user can also open it themselves: ACE tab > Insights > Dashboard. The checks follow the sections of Autodesk's Model Checker (file and project, worksets, levels and grids, warnings, model content, views, annotation, naming) plus ACE's rooms, parameters and submission checks; each has a pass rule from the office check set (checkset.json, see checkset.example.json), merged with the project's project-requirements.json when the model has one (project wins; project_rules shows what is in force), and results are pass / review / action needed. The dashboard also shows the latest clash results (coordination). Code compliance is not built yet (planned); say so.

Purge unused ("purge the model", "remove everything unused"): purge_unused, never execute_revit_code deletes by id (a
mass delete is blocked, and per-id SubTransaction deletes crashed Revit 2025). It is a FULL Purge Unused only, the
owner's method: purge three times in a row to reach zero. Each pass deletes the list Revit's Purge Unused shows
(Document.GetUnusedElements, all categories) with one delete (a refused set is split in halves; what still fails is
listed); passes repeat until a pass finds nothing (max_passes, default 3, at most 5). Revit link types are never purged.
check_only shows pass 1; dry_run runs every pass and rolls back; the identical call is ONE undo step and must find
exactly what each dry-run pass found (else nothing changes: run the dry run again). To purge specific types, the user
uses Revit's own Purge dialog by hand. Report per pass found / deleted, the total and "unused left after the last pass".

## Worksets per the BEP

for "put things on the right worksets", "workset check", "set up worksets" use assign_worksets. It applies the BEP rules file (category, function, family / type, level, zone = scope box, room department, parameter; first match wins). Start with check_only: true and report what is on the wrong workset per category; then preview (dry_run) and apply only after confirmation. only_workset1: true leaves deliberate choices alone; create_missing creates worksets the rules name. If the project's BEP differs from the rules, ask for the BEP table and pass rules.

With a rules file in place, model_dashboard also reports "Elements on the wrong workset (BEP)" (the same rules), so
the check runs every time the model is checked, not only when asked.

Rules in force (D3): the project's project-requirements.json section "worksets" first (its rules are tried first, its
default_workset and naming win key by key), the office worksets.json after it; "inheritOffice": false in the project's
section uses the project's rules alone. project_rules section "worksets" shows the result and its files; the audit's
naming rule is the same merged section. Office method: revit_guide methods/workset-cleanup.
Per-model rules: a rule's "models" key ("TST_STR", ["TST_ARC*", "!TST_ARC_OLD"]; matched on the model's title and
central / file name, case-insensitive, without .rvt; * wildcard, ! excludes) with `"modelsScope": "only"` limits it to
those models (`{ "workset": "S-BEAMS", "category": "Structural Framing", "models": ["TST_STR"], "modelsScope": "only" }`),
so one project file holds the rules of every model; a rule without models applies everywhere. A models list WITHOUT
modelsScope (saved by workset_plan before 1.14.10: where the rule was proposed, often only the first model) does not
limit the rule: it applies as before and the reply says "N rule(s) list other models in `models`"; add
`"modelsScope": "only"` (after checking the list names every model it should) only when the user wants the limit. assign_worksets, workset_plan (propose,
status) and the model check leave the other models' rules out and the reply says which were skipped and why: say so to
the user when a rule they expected did not apply. Rules saved by workset_plan carry the models their workset is in
and modelsScope "only" (no models when the rule may create its workset).

Workset housekeeping ("clean up the worksets", "which worksets are empty", "rename the worksets to the BEP names"):
assign_worksets action audit first (read-only): every user workset with its element count by category, owner and
whether it is open, flagged empty, naming (breaks the project's rule), near-duplicate ("ST_Columns" vs "ST-COLUMNS")
and wrong-workset (elements the BEP rules put elsewhere). With out_dir it also writes an HTML table for review and a
CSV. Show the table and let the user decide before anything changes. The naming rule is optional and lives in the
worksets section of project-requirements.json: "naming": { "pattern": "<regex>", "allowed": [names], "rename":
[{ "from", "to" }] } (Shared Levels and Grids is exempt). action rename takes renames [{ from, to }] (default: the
rule's suggestions: its rename list, and near-duplicates of an allowed name; a target that already exists is not
suggested, that is a merge: move the elements, then delete the empty one). action delete_empty deletes only worksets
with no element at all (view-specific, hidden, in groups and design options counted); a workset that still holds
elements is kept and listed unless the user names it in worksets AND gives move_to (its elements move there first).
Both run like clash_resolve: dry_run: true (the plan, the self-check: element count per category the same before and
after), then the identical call (ONE undo step). Shared Levels and Grids and the system worksets are never renamed or
deleted; borrowed, closed or changed-in-central worksets block with what, why, who and how.
The audit is model-aware: an empty workset whose name says what it should hold (LINKS = RVT links and CAD imports,
CAD, COLUMN, BEAM/FRAMING, WALL, FLOOR/SLAB, FOUNDATION, GRID/LEVEL and MEP/arch words) while those elements sit on
another workset is flagged "empty, but N elements look like they belong here (on <workset>)": move them there with the
rules, do not delete it. naming.categoryTokens ({ "PCC": ["Structural Foundations"] }) extends or overrides the
word-to-category table. Workset1, or Shared Levels and Grids holding more than datums, is flagged "default workset holds
N elements (M structural); assign them by rules" with its top categories. Without BEP rules the wrong-workset count is
"not checked: no rules", never 0. delete_empty without names sends only the truly empty worksets and lists the
expected-to-hold ones it kept, with why.

Workset plan ("propose the worksets", "a workset list for these models", "assign by the workset names"): workset_plan.
action propose (read-only; the active model, or paths opened in turn, detached with worksets preserved; identical models
proposed once, "same as"): one row per target workset (spelling twins merged: case, spaces, "-" / "_", COLUMN / COLUMNS),
what it holds read from its name (the category word decides; then level or zone narrows; a workset with a category and no
level is the catch-all for levels without their own; a material word excludes the other material, so concrete never
goes to a STEEL workset; where like elements sit only breaks a tie; links by the discipline code in their name first,
CAD imports to the CAD / LINKS workset), the elements it holds and would receive, open / default / export-view
visibility (views, default 3D views named NAVISWORKS / NWC; a move that would hide elements there is flagged), and a
decision column; the moving element groups are the detail (second sheet, -groups.csv); no confident home = review.
Visibility worksets (HIDE, HIDDEN, TEMP, or the vault's worksets.naming.manualOnly) are manual only; the datum workset
(the add-in's protected flag) is never renamed or deleted; Workset1 keeps its name ("keep Workset1 (default)"; a rename
only with the vault's naming.defaultName). Levels match whole codes only (the code before "(" after an "X-" prefix:
"A-01(...)" -> 01, the bracketed words as one phrase, vault naming.levelAliases { TOKEN: [level names or regex] }); a
category word counts as the head of the name, never inside a zone phrase ("POOL & Pump Room" is a zone); vault
naming.categoryTokens always count (phrases too, one token may map to several categories). Empty-but-expected and
catch-all worksets (a category, no level) are never proposed for deletion; sketches and legend components get no row. The user edits the file; action confirm (file: rule-level, group-level or the hand-made CSV, matched
by Model + Current workset + Category + Type + Level and the element count: a changed count is flagged, never applied)
writes a plan per model beside the file and shows the rules; save_rules + confirm: true saves them into the vault's
project-requirements.json (each new rule before the first less specific one, category-free rules last; a conflicting
rule is listed and kept; a rule that would shadow, or be shadowed by, an existing one is listed and saved only with
overlaps_ok: true after the user agreed). An xlsx is read on both sheets: rule rows and group rows; where both decide a
group, the Groups sheet wins. action apply with plan_file:
check_only, dry_run, then the identical call (ONE undo step: create, rename, move, delete; borrowed elements skipped
and listed; a changed group count refuses the whole plan). Office method: revit_guide methods/workset-cleanup.
A confirmed rule applies only in models that already have its workset (never created elsewhere: a "create" decision on
the row, or confirm with create_missing: true, allows it); saved rules record their source models. What sits on a zone
or asset workset (BOUNDARY WALLS, STEEL FENCE, RF PARAPETS) stays there ("kept on zone/asset workset"), unless one rule
names that zone and its category (vault naming.zoneWins: false turns this off). A type word that names a workset (SOG,
PCC, a zone word) beats level + category; vault naming.typeTokens { "BLINDING": "<workset>" } overrides.
The vault (every workset_plan reply prints its path): an explicit vault always wins; else the model's folders and their
parents are searched for an ACE Vault (also one level down: X\WORK\<sub> finds X\ACE\ACE Vault). A given vault without
workset rules, or one that cannot be read, stops with the path: never a silent fallback to the built-in words. Worksets
in the vault's worksets.keep (written by confirm with save_rules) or its confirmed names are never proposed for deletion
and never emptied by a reset. The standard (every reply names it): the vault's rules/worksets-standard.json (each rule with its clause, used only once
the user confirmed it: confirm_standard: true), else the project's list (project-requirements.json worksets), else the
office worksets.json; else the workset names. propose refresh: <previous file> keeps the decisions and marks rows new /
changed / same / gone; rows ACE applied are not proposed again, a row changed after apply is flagged for review. action
status (read-only): % correctly assigned, wrong (with where to and why), empty (expected / unused), not in the standard,
missing, visibility, borrowed, and each group applied by ACE (date, plan) / changed after apply / correct already / still
to do; HTML + CSV and the model_dashboard "Worksets: N% correct" tile. apply only: { rows, worksets, categories, models }
applies part of the plan (its own planHash); reset: "default" (rule-level plans only) computes the end state so each
element moves once: no rule -> the default workset; datum, manual-only and skipped groups stay. Pinned elements are
unpinned, moved and re-pinned in the same undo step (counts said). Borrowed elements: the rest moves; they become pending
actions (vault 04 Audits/<model>/worksets/pending-actions.json, with holder and target), retried first on the next apply.
Every apply writes worksets-applied.json and shows the status again; moves already done are skipped and said. Every
view on a sheet (and the export views) is compared before and after the moves: a moved element that a sheet view would
show differently is HELD (it stays where it is); the dry run lists the changes by sheet and view (export views are
reported only). Show them to the user; only after they agreed pass allow_visibility_change: true (or the view / workset
names) and run the dry run again (it is part of the change, so the apply needs its own dry run).

## Parameter requirements (AIS, LOIN, Model Checker, shared parameter files)

For "are all the client's parameters filled", "check the parameter list / LOIN", "fill the blanks" use parameter_check
(read-only) and give every source the user has at once: list (AIS CSV: parameter_name, categories, type_instance) or
rows, loin (xlsx), checker (Model Checker XML), shared_parameters (client files), project_parameters (the project's
file, whose GUIDs a binding must use). They merge into one table (name + group, + categories for elements). action
requirements shows the merged table and its defects without Revit; review_checker reviews the client's checker itself
(active vs parked checks, discipline parameters checked only on Project Information, unknown names, duplicates). The
default action checks every open model (all_documents: true): per model and row the status (complete,
complete-with-na, blanks, not-bound, partly-bound, wrong-guid, out-of-scope, no-elements), GUID differences across
models and Project Information twins with different values, and a ranked action list (top 10 in the reply). With
report_dir (or a large result) the reports go to files (summary xlsx, CSV, markdown, actions); give the user the paths.

Everything project-specific lives in the project mapping in the vault (`rules/parameter-mapping.json`, found from the
open model, or `vault` / `mapping`): client category -> Revit categories, LOIN sheet -> disciplines (`disciplines`), AIS / checker group ->
disciplines (`groupDisciplines`, "*" = every model), approved name
aliases, model -> discipline, LOIN column names (loinTable) and the NA policy (the fill value and the texts that mean
"declared missing"). Without one, only the generic checks run and the reply says what is missing. create_mapping: true
writes a draft with best guesses for a person to confirm; a draft never drives a fill. Report unmapped categories and
spelling / prefix-drift candidates as decisions for the user; never treat a guess as confirmed. Model-title globs (the
keys of `derive.projectInformationValues`) match the FULL model title ("*-ST-*-0001", not "ST-*"); `models` keys match a
part of the title. `titleField` (top level or under `models`) is the 0-based title token (split on - _ space .) that holds
the discipline code. When the title holds several codes (or none while rows name disciplines) the reply starts with a
warning: the result then judges every discipline's rows and those rows are plan-only until `models` or `titleField` is
set. "Complete with NA" is its own state, never counted as complete. A rule that matches no open model is a warning in the plan: tell the user. A LOIN cell listing several
names ("A, B") is checked one row per name; if the parts do not resolve, it is a pseudo-name to check with the client.
`evidence.items` ([{ id, text, rows: [names] | check: "<checker check>" }]) names the project's QA / requirement items;
the evidence file states each one's status after the apply (met, met with NA, partly met, not met).

Four stages: 1 check (parameter_check, read-only), 2 reports (report_dir: summary xlsx, CSV, markdown), 3 actions (the
ranked list: each has a stable id, a type and its blockers; auto-fixable ones say whether they are ready), 4 execution
(parameter_fill action fill), only when the user asks. Execution runs the ready auto-fixable actions of the active model
(or `actions: [ids]`) as ONE plan, ONE approval, ONE undo step: bindings (definitions only from the project shared
parameter file, or a temporary file with exactly the client's line: tell the user to add that line to the project file),
derived values (the mapping's approved derive rules and listed twins; never overwriting a value), then the approved NA
value on the blanks that remain. dry_run: true first: it writes the missing register (blank values before the fill),
shows the impact (schedules, view filters, labels on sheets, IFC property sets with ifc_property_sets, client checks that
flip: "NA" passes a HasValue check without the data), then the identical call applies; the add-in reads every write back
and rolls the whole plan back on any mismatch (then nothing was set: report the counts as "would have been set; rolled
back", never as done). The preview is remembered by this MCP server process only: after a restart (or from another
Claude session) the apply is refused as "not previewed", so run the dry run again first. The apply writes the evidence file. With all_documents: true the other
models' plans are listed read-only: the user switches to each model and repeats the call (one approval per model).
Blocks are never silent: an unapproved NA policy or derive section, a missing project shared parameter file, a changed
source file, another ACE version, borrowed elements: say what, why and how to clear it, as the reply gives it.
parameter_check fill_plan: true still counts what a plain NA fill would set (read-only).

## A parameter from what a view shows (view_membership_fill)

for "set the export status from the Navisworks view", "mark what this view shows": view_membership_fill with view,
parameter, value_in (the elements the view shows) and value_out (the rest of categories; default: model categories).
1. check_only: true first. Read the reply to the user: the counts in / out, why elements are out (first reason found:
   outside the section box, hidden in the view, category hidden, a view filter, phase, workset), the plan (bind, set in,
   set out) and the notes.
2. Spelling: the parameter name is checked against the vault's shared parameter files (project file, client files,
   mapping). An approved spelling rule (mapping nameAliases spellingRule) or alias writes the vault's spelling; otherwise
   a "Likely spelling issue" is said and nothing is renamed: ask the user. Near-duplicate names in the model are listed.
3. A parameter missing in the model is bound only with the definition (GUID, TEXT) from the vault's shared parameter
   file; with no definition the run is blocked ("no-definition"). Never invent a GUID; ask for the file or for the
   parameter to be bound by a person. Only text parameters are created. Binding needs categories (the shared
   parameter file holds none): ask the user which. Only model views are accepted; linked models are not filled.
4. Impact: the reply names the project rules and the vault files (IFC export settings, requirement lists) that mention
   the parameter: their selection follows the new values. Say so before the dry run.
5. dry_run: true, show the plan and the self-check, then the identical call after the user's OK (ONE undo step;
   expect_plan). A failed self-check rolls back: report "would have been", never done.

## Deliverables: export and register

export_deliverables writes NWC (a 3D view), IFC (the model or a view) and DWFx (sheets or a sheet set) in one call, each
optional; the model is not changed. out_dir is required; an existing file is replaced only with overwrite: true after
the user agreed. File names: file per format, else the vault's rules/export.json "nameTemplate" ({model}, {date} as
YYYYMMDD, {format}), else the model name. IFC settings: ifc.config (a JSON file: ACE's keys FileVersion,
ExportBaseQuantities, WallAndColumnSplitting, SpaceBoundaryLevel, other keys passed as options; an exporter's saved setup
works, its IFCVersion / SplitWallsAndColumns / SpaceBoundaries keys are read), or ifc.setup (a name: the setup saved in
the model by Revit's IFC exporter is used first, then the vault's: rules/export.json "ifc": { "setups": { name: "file.json"
or { ...settings } } }, else rules/ifc/<name>.json). list_ifc_setups: true lists both (read-only). DWFx sheets "as
issued": dwfx.same_as_folder: <folder with the issued .dwg / .pdf> matches each file to a sheet (the sheet number in the
file name: "stemPattern" (regex with (?<number>...)) in the vault's rules/export.json "dwfx" when set, else the number
as the whole file name or its start followed by _, space, . or - and a non-digit, also after "stemPrefix" (a regex for
the project prefix) when set; then the sheet name as whole words). Nothing is guessed: files that match no sheet, or
more than one, block the export until they are renamed or allow_unmatched: true (after the user agreed); sheets not in
the folder are listed; same_as_folder wins over dwfx.sheets / sheet_set (said). The DWFx is always ONE combined file
(combined: false is refused), named by dwfx.file, else dwfx.name, else the naming rule; dwfx.paper: "sheet" keeps each
sheet's own paper size. Run check_only first for the pre-flight (exporter installed,
views, setup, sheets, existing files); every block is reported with how to clear it and nothing is exported. Show the
files table (file, size, time, count, warnings) and the run record.

register_update (no Revit) updates a deliverables register (xlsx) for one submission: the register sheet is copied into
a new workbook named by label, the stage column of delivered rows gets submission_date, later dates in stage_columns
are cleared. Delivered = file names or a folder (e.g. the export out_dir); an id matches when the file name contains it,
or id_pattern (the vault naming regex). Without confirm it only checks: show the report (matched, delivered but not in
the register, not delivered, cleared, ambiguous: counts and the first 10 of each; every item is in the report CSV it
names); then the identical call with confirm: true writes output and its report CSV. The report never goes beside the
output (a submission folder): report_dir, else the trusted vault's 04 Audits/registers, else ACE's reports folder; give
the user its path. The register itself is never written. Columns are found by header text first (a column letter only
when no header matches). A later date computed by a formula is reported and left. Merged cells in the data rows of the id
or stage columns stop the write (allow_merged: true only after the user checked); title-block merges above header_row
are ignored and counted; defined names and validations into dropped tabs are warnings.
The project's layout lives in the vault's rules/register.json (see bep/register.example.json and revit_guide
methods/midp-register-update): with it the call is vault, discipline, stage (a stage name, e.g. IFC: its columns get the
date), submission_date, label, delivered and output; explicit arguments win, and the reply says which came from where.
Rows whose id cell is not an id (section headings) are skipped and listed (id_pattern, or the vault's rules/naming.json
"idPattern"). An id pattern with a capture group extracts the id from file names (group 1); without one (e.g. an
anchored prefix "^XX01-") it only filters the ids found in file names as they are. discipline may be left out when the
template has one discipline (or when register, id_column and stage_columns are given). Tables in the sheet are kept as
in the register, and the written file is self-checked (strict XML of every part, tables Excel would refuse, dxf / cell style / shared-string
indexes; on Windows Excel
opens it read-only unless excel_open_test: false): a failing file is deleted, never reported as written. A confirm after an MCP restart finds no check: run the check again. stage can be a list (e.g. the stage's planned and actual columns). "Probably the same, confirm" pairs
(same final number block, one naming field different) are never set by themselves: ask the user, then pass
accept_near: [ids]. "The id column looks wrong" stops the check: show the sample values and fix id_column;
force_id_column: true only when the user confirms the column.

## Requirements vault and audits

The project's client documents (EIR, BEP, LOIN, MIDP, client comments) live in its vault (`vault`). Start with
`vault status`. To add a document: `register` with a short key (EIR, BEP, LOIN) and the revision as printed, then read it
(`read`, by pages, sheet rows or `find`) and write requirement records: one per obligation, the document's own words in
`source.quote` (verbatim, short), the page / clause / sheet and row, kind, scope, a test (A automatic in Revit, P partial,
X on files, M manual) and severity (info / warning / error). Never invent a clause or a page; a record without its source is
rejected. Save with `import` (records or an extraction file), then ask the user to accept them (`decide`, decision
accept). When a document gets a new revision, `register` it under the same key and run `revalidate`: records whose quote
is still there move to the new revision with the same id; the others are flagged for the auditor. The rules ACE checks
live in the vault's project-requirements.json (`project_rules` shows what is in force). Ask before the first write to a
vault (consent).

Compliance follows every model check: a requirement is tied to a check by `"requirement": "REQ-..."` on a check or a
workset rule in project-requirements.json, or by the record's `test.binding` (`C1:checks/<check key>`). `vault compliance`
returns met / partly (a measured part of a set, e.g. 3 of 100 elements) / not-met / not-checkable per requirement, with
the evidence and any accepted deviation; it reads the last result (and says if the rules or requirements changed since),
`run: true` runs the model check first. Report not-met by severity, and never call a deviation "met".

The master report (`vault report`) is built from the latest compliance run: cover, summary with the trend since the last
report, document register, compliance matrix, model findings, decisions, actions (CSV beside it, owner and due left blank
for the team). Run the model check first if the requirements changed (the report says so). `vault model_summary` keeps
the latest report's summary inside the model: only when the user asks, preview first (dry_run: true), then apply.

## Space planning (office test fit)

for an office test fit ("how many people fit", "put desks in room X", "fit out the office") use the saved script test_fit_out (inputs: room_number, m2_per_person, desk_type, min_aisle_mm, door_clearance_mm, wall_clearance_mm). It places 4-desk pods on a regular grid clear of walls, doors, columns and fixtures, reports the seats, m2 per person and the room's maximum, and a re-run replaces the previous test fit in that room. Preview it with preview_image: true. If the target does not fit, say so plainly with the maximum, rather than squeezing aisles below the minimum.

Several rooms at once: room_numbers, or level (optionally department) fits every matching room in one run, with one
picture and one Apply card; give each option an option_label ("Option A - 10 m2") in run_saved_script so the cards
differ.

## Warnings

list_warnings groups the model's warnings by type with counts, element ids and the known fix. Show the elements
(select_elements), then fix one type at a time with a preview: identical instances with the saved script
delete_duplicate_instances; others with a short script that uses the ids. Report what was left and why.

## Production (marks, tags, sheets)

Order matters: renumber, then tag, then sheets. renumber_doors (scheme room: 301A, 301B; or level: D-L3-001) skips doors
in use by colleagues. tag_untagged with all_floor_plans: true (or views: [...]) tags every plan in one run.
sheets_for_levels then reuses each level's existing plan that is not on a sheet (so the new tags appear on the sheets),
skips levels that already have one and, by default, levels that are not building storeys. For a long job offer
backup_model first, and remember that each run is its own undo step.

## Presentation standard

presentation_standard applies the office drawing standard: the printed text height for each view scale (default 3 mm
for 1:50 and larger, 2.5 mm for 1:100 and smaller) and one type per kind (text, each dimension style, optionally tags
per category) and size, in the views on sheets. Start with check_only: true and report the wrong heights and the
types it would use or make; then preview and apply after confirmation. A project's own sizes (its BEP) go in
text_sizes. The rules are in the office check set; the user picks types and sizes in ACE > Deliver > Presentation
Standard, so point them there when they want to change the standard itself. Tag text height is set in the tag
family, so for tags it unifies the type, not the size.

