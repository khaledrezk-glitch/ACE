# Office method: workset cleanup (version 2)

Scope: general method in the tool, template per project in the vault.

Audit first, then change. The workset list, the naming rule and the assignment rules come from the project vault
(project-requirements.json, section "worksets": rules, default_workset, naming { pattern, allowed, rename }); the office
worksets.json applies below it (project rules first, office rules after them; "inheritOffice": false in the project
section uses the project's alone). project_rules section "worksets" shows what is in force and where it came from.
A rule's "models" key (a name or a list; * wildcards, "!name" excludes; matched on the model's title and central / file
name) limits it to those models, so one project file holds per-model rules; without it a rule applies to every model.
Replies name the rules skipped for the open model and why.

## With ACE's tools

- assign_worksets action audit (out_dir for the HTML / CSV table): empty, off the naming rule, near-duplicate, elements
  on the wrong workset. Let the user review the table.
- action rename (the rule's suggestions, or renames) and action delete_empty (move_to only for a workset the user
  named): check_only, then dry_run, then the identical call: ONE undo step, self-checked.
- assign_worksets (action assign): check_only, dry_run, then apply, to put elements on the right worksets.
- Shared Levels and Grids is never renamed or deleted; borrowed worksets block with who holds them.
- workset_plan (a workset list for one or several models): propose -> the user edits the decision column -> confirm
  (plan per model, rules shown; save_rules + confirm: true writes them to the vault) -> apply (check_only, dry_run, then
  the identical call). Assignment follows the workset NAMES: the category word decides, level / zone narrows, material
  never mismatches; visibility worksets are manual only; the next model needs no CSV (assign_worksets with the saved rules).
- workset_plan action status (read-only) before and after: % correct against the standard, what ACE applied, what changed
  after, pending (borrowed) actions. Apply part of the plan with only; reset: "default" when the model is a mess.
