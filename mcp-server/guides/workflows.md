# Workflows: coordination, changes, model health, worksets, test fits, production

How to run ACE's built-in workflows well. The tools named here are MCP tools; the scripts are saved scripts (run_saved_script).

## Coordination and clashes

run_clash_test finds clashes across this model, its links and the other open models (standard tests "STR vs MEP", "ARC vs STR", "MEP vs ARC", "MEP vs MEP", or "all"; or custom categories), with depth, level, location and the RESPONSIBLE discipline (the element that is easier to move gives way). Clashes are grouped into ISSUES (one element that has to move and everything it hits, e.g. one duct through 8 beams): report and coordinate by issue (topIssues), not clash by clash. Connected MEP parts and hosted elements are not clashes. A new clash carries its CAUSE when snapshots exist: which side was added, moved or retyped since the last run, and by whom (workshared). Keep the two apart when you report: "responsible" says who should give way, "caused by" says whose change made it. Status persists between runs (new / active / resolved / approved; a resolved clash that comes back is new again and "reopened", worth pointing out): report what is new and what was resolved since last time, grouped by responsible discipline and level. For a walk-through ("show me the clashes", "go through them with me") use clash_view with the clash key, issue by issue (the user sees only the two elements, coloured by model, zoomed to the intersection) and clash_view without a key to show both models in colour; the user can do the same in ACE > Coordination > Clash Browser. To compare with one model use run_clash_test with_model (the secondary; the primary is this model). A BIM manager can compare two links: primary_model = one link, with_model = the other (this model is then ghosted). Colours always follow the role: primary green, secondary red; with several links each link has its own colour, which the user can change in the Clash Browser legend or with clash_view colours. For a coordination meeting ("prepare the clash report", "issues for the meeting") use coordination_report: a picture per issue, responsible, cause, location, and a CSV list for Excel; give the user both file paths. Offer set_clash_status to approve accepted ones (e.g. sleeved penetrations). Check coordination_sources and the brief's alignment first: if levels or grids don't line up, clash results are unreliable - say so. Structure from architecture: in the STR model (ARC linked or open), run the saved script derive_structure_from_arc - first with check_only: true to list what is missing on each side, then a preview with preview_image: true, then apply after confirmation.

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
another way. The join order (strongest first; default foundation > column > wall > beam > slab > stair) and the steel
switches come from the project vault (`rules/clash-resolve.json`: { "order": [...], "steel": { "cutback": true,
"coping": true } }), a call's order or steel wins. Stages: action plan (read-only: before by pair, the plan by kind, the
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

## Change tracking and linked models

model_changes compares this model and its linked models with an earlier snapshot (since: last, today, yesterday, week, month, a weekday such as monday, "3 days", or a date): added, deleted, moved, retyped and changed elements, by category and by person. Use it for "what changed", "what did STR change since Monday", and after a link is reloaded. Before a milestone (issue, submission), offer snapshot_model with a label. get_model_brief lists the linked and open discipline models and whether their levels and grids line up: when they don't, say so before any coordination work.

## Model health and status

for "how healthy is this model", "audit", "dashboard" or "status report", call model_dashboard (read-only; show: true opens it in Revit). It returns the 0-100 score, each finding with its score impact and sample ids, and the path of an ACE-branded HTML report the user can share. Explain the top findings in plain words and offer fixes (each fix follows the preview protocol). The user can also open it themselves: ACE tab > Insights > Dashboard. The checks follow the sections of Autodesk's Model Checker (file and project, worksets, levels and grids, warnings, model content, views, annotation, naming) plus ACE's rooms, parameters and submission checks; each has a pass rule from the office check set (checkset.json, see checkset.example.json), merged with the project's project-requirements.json when the model has one (project wins; project_rules shows what is in force), and results are pass / review / action needed. The dashboard also shows the latest clash results (coordination). Code compliance is not built yet (planned); say so.

## Worksets per the BEP

for "put things on the right worksets", "workset check", "set up worksets" use assign_worksets. It applies the BEP rules file (category, function, family / type, level, zone = scope box, room department, parameter; first match wins). Start with check_only: true and report what is on the wrong workset per category; then preview (dry_run) and apply only after confirmation. only_workset1: true leaves deliberate choices alone; create_missing creates worksets the rules name. If the project's BEP differs from the rules, ask for the BEP table and pass rules.

With a rules file in place, model_dashboard also reports "Elements on the wrong workset (BEP)" (the same rules), so
the check runs every time the model is checked, not only when asked.

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

