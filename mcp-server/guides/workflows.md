# Workflows: coordination, changes, model health, worksets, test fits, production

How to run ACE's built-in workflows well. The tools named here are MCP tools; the scripts are saved scripts (run_saved_script).

## Coordination and clashes

run_clash_test finds clashes across this model, its links and the other open models (standard tests "STR vs MEP", "ARC vs STR", "MEP vs ARC", "MEP vs MEP", or "all"; or custom categories), with depth, level, location and the RESPONSIBLE discipline (the element that is easier to move gives way). Clashes are grouped into ISSUES (one element that has to move and everything it hits, e.g. one duct through 8 beams): report and coordinate by issue (topIssues), not clash by clash. Connected MEP parts and hosted elements are not clashes. A new clash carries its CAUSE when snapshots exist: which side was added, moved or retyped since the last run, and by whom (workshared). Keep the two apart when you report: "responsible" says who should give way, "caused by" says whose change made it. Status persists between runs (new / active / resolved / approved; a resolved clash that comes back is new again and "reopened", worth pointing out): report what is new and what was resolved since last time, grouped by responsible discipline and level. For a walk-through ("show me the clashes", "go through them with me") use clash_view with the clash key, issue by issue (the user sees only the two elements, coloured by model, zoomed to the intersection) and clash_view without a key to show both models in colour; the user can do the same in ACE > Coordination > Clash Browser. To compare with one model use run_clash_test with_model (the secondary; the primary is this model). A BIM manager can compare two links: primary_model = one link, with_model = the other (this model is then ghosted). Colours always follow the role: primary green, secondary red; with several links each link has its own colour, which the user can change in the Clash Browser legend or with clash_view colours. For a coordination meeting ("prepare the clash report", "issues for the meeting") use coordination_report: a picture per issue, responsible, cause, location, and a CSV list for Excel; give the user both file paths. Offer set_clash_status to approve accepted ones (e.g. sleeved penetrations). Check coordination_sources and the brief's alignment first: if levels or grids don't line up, clash results are unreliable - say so. Structure from architecture: in the STR model (ARC linked or open), run the saved script derive_structure_from_arc - first with check_only: true to list what is missing on each side, then a preview with preview_image: true, then apply after confirmation.

Each issue has an issueKey: clash_view with it focuses on the issue's deepest clash, and set_clash_status with it
approves or reopens every clash of the issue. For "what changed and does it create new clashes": call model_changes
with save_snapshot: false (a new snapshot would become the baseline the clash causes are measured against), then
run_clash_test. On a large federated model, run per level (level) or per link (with_model): a run that hits
max_elements is incomplete and does not mark anything resolved.

## Change tracking and linked models

model_changes compares this model and its linked models with an earlier snapshot (since: last, today, yesterday, week, month, a weekday such as monday, "3 days", or a date): added, deleted, moved, retyped and changed elements, by category and by person. Use it for "what changed", "what did STR change since Monday", and after a link is reloaded. Before a milestone (issue, submission), offer snapshot_model with a label. get_model_brief lists the linked and open discipline models and whether their levels and grids line up: when they don't, say so before any coordination work.

## Model health and status

for "how healthy is this model", "audit", "dashboard" or "status report", call model_dashboard (read-only; show: true opens it in Revit). It returns the 0-100 score, each finding with its score impact and sample ids, and the path of an ACE-branded HTML report the user can share. Explain the top findings in plain words and offer fixes (each fix follows the preview protocol). The user can also open it themselves: ACE tab > Insights > Dashboard. The checks follow the sections of Autodesk's Model Checker (file and project, worksets, levels and grids, warnings, model content, views, annotation, naming) plus ACE's rooms, parameters and submission checks; each has a pass rule from the office check set (checkset.json, see checkset.example.json), and results are pass / review / action needed. The dashboard also shows the latest clash results (coordination). Code compliance is not built yet (planned); say so.

## Worksets per the BEP

for "put things on the right worksets", "workset check", "set up worksets" use assign_worksets. It applies the BEP rules file (category, function, family / type, level, zone = scope box, room department, parameter; first match wins). Start with check_only: true and report what is on the wrong workset per category; then preview (dry_run) and apply only after confirmation. only_workset1: true leaves deliberate choices alone; create_missing creates worksets the rules name. If the project's BEP differs from the rules, ask for the BEP table and pass rules.

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

