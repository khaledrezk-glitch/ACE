# Changelog

## 1.14.11 (21 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **save_model save_as:** `save_as: { path, overwrite?, as_central? }` saves the named model (handle / path / model, as
  before) to a new .rvt with `Document.SaveAs` (`SaveAsOptions.OverwriteExistingFile`; as_central sets
  `WorksharingSaveAsOptions { SaveAsCentral = true }`). Checked before Revit is called: the path ends in .rvt, its folder
  exists, an existing file only with overwrite, not the model's own file, as_central only for a workshared model. Still
  needs allow_risky (server and add-in) and refuses cloud, linked, family and read-only documents and an open transaction;
  a never-saved model (a detached copy) can now be saved this way. Reply: path, previousPath, kind (save_as / save_as
  central), overwritten, ms, handle; afterwards the open model is the new file and an open_model handle follows it. A
  central saved at a new path warns that other users' locals still point at the old central. Journaled.
- **execute_revit_code mode "no_transaction":** for APIs that fail with "any open transaction phase started by API
  client" (RevitLinkType.LoadFrom, Document.SaveAs, closing another model, TransmissionData on other files): the code
  runs with no TransactionGroup and no transaction. Needs allow_risky (refused by the server and the add-in otherwise);
  its dry run only compiles and screens (nothing runs), and the apply needs that dry run of the identical code (the
  existing preview gate). Not one undo step and nothing is rolled back on an error: the reply, the journal and
  undo_last_claude_change say so; the change tracker still reports what changed. Guides (transactions, workflows),
  instructions, smoke tests, check-set tests (save_as rules), risk case (RevitLinkType.LoadFrom) and two bench cases.

## 1.14.10 (21 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **Purge unused (G10):** new `purge_unused` (add-in command and MCP tool): a full Purge Unused in passes until nothing
  is left (the owner's "purge three times" method; max_passes default 3, at most 5), not an element delete. Each pass
  deletes the list Revit's Purge Unused shows (`Document.GetUnusedElements`, all categories) with ONE `Document.Delete`
  in its own transaction; a refused set is split in halves (a fresh transaction per attempt, at most 12, no
  SubTransactions, no per-id deletes) and the ids that still fail are listed; Revit link types are never purged. All
  passes are one TransactionGroup: check_only (pass 1), dry run (every pass, rolled back), identical apply = ONE undo
  step. The planHash covers every pass's set: an apply whose pass finds something else than the dry run's same pass
  stops before that pass deletes anything and rolls everything back (plan-changed, naming the pass). Reply: per pass
  found / deleted / by category / names, total, "unused left after the last pass". Specific types: Revit's Purge dialog
  by hand. NativeChange gains `ownTransactions` (an inner transaction left open is rolled back before the group).
- **Right model only:** save_model requires handle, path or model (it no longer saves whatever model is active: a
  switched Revit window saved the wrong model live); execute_revit_code takes `expect_model` and refuses, running
  nothing, when the active model is another one.
- **DWFx warm-up export:** Revit takes the in-session print settings only after a first DWFx export in the session
  (live finding); a one-sheet warm-up export into a scratch folder now runs before the real one, inside the same
  rolled-back transaction.
- **Per-model workset rules (G9):** a rule's `models` key (exact or `*`, `!` excludes, case-insensitive; matched on the
  title, central / file path and cloud ids) limits it to those models in assign_worksets, workset audit / rename /
  delete_empty, workset_plan propose / status and the model check, when the rule also has `"modelsScope": "only"`;
  skipped rules are named in the reply. Rules saved by workset_plan now list every model they were proposed for and carry
  `modelsScope: "only"`. Backward-safe: a `models` list without the marker (saved by 1.13.x / 1.14.x, often only the
  first model) does not limit the rule: it applies as before, and the reply adds one line ("N rule(s) list other models
  in `models` ... set modelsScope: "only" to limit them"). Server and add-in agree; nothing to change before upgrading.
- **DWFx sheet extents (#37):** a merged DWFx scales every page by the largest sheet's extents, so one title block with
  invisible lines above its frame shrank all pages to ~88 %. The pre-flight now compares each sheet's outline with its
  paper and every element on the sheet (BoundingBox in sheet coordinates) with the title block frame (1 mm tolerance) and
  blocks with `sheet-extents`, naming the sheet, outline vs paper and the elements that stick out (mm beyond which edge);
  a title block larger than its Sheet Width / Height is named as a family to edit. `dwfx.allow_oversize: true` exports
  anyway (a warning; the drawn-extent check then only warns for that file). `preflight.sheetExtents` lists the sheets.

## 1.14.9 (21 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **Deliverables folder stays clean:** a DWFx that fails its self-check is kept in `%APPDATA%\ACE-RevitMCP\failed-exports`,
  never in `out_dir`. The export notes any Revit `<model>_backup` folder found in `out_dir` (not a deliverable).

## 1.14.8 (21 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **DWFx with no margin:** per paper group the in-session print setting is set inside the rolled-back export transaction
  (the printer's matching paper size, orientation, zoom 100 %, no margin); nothing is saved in the model. `dwfx.printer`
  picks the printer; a printer without the paper size blocks (`no-paper-size`) and lists the installed printers.
- **DWFx drawn-extent check:** each sheet page's drawing must cover at least 98 % of the page each way, or the file is
  kept as `-FAILED`; `pageCheck[].extentRatio` gives the measure.
- **Background jobs:** `background: true` on export_deliverables answers at once with a job id; `job_status` reads the
  reply from the job file (any ACE server process can).
- **Version mismatch warning:** every Revit call says so when the ACE server and the add-in are not one version (a
  long-lived server keeps its old code until its client restarts).

## 1.14.7 (21 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **DWFx with 3D views:** `dwfx.views` adds named 3D views to the combined DWFx with the sheets (exact names; missing,
  ambiguous or unprintable views block in pre-flight). The page self-check skips the view pages; the reply says
  "N of M sheets and K of L views in the files".
- **same_as_folder reads subfolders** (printed sets in PDF\ and CAD\), up to 4 levels, one stem per sheet; Revit's
  `_backup` folders are skipped.
- **open_model warns** that opening a workshared file attached creates `<name>_backup` beside it (stronger when the folder
  looks like a deliverables folder) and recommends `detach: "preserve_worksets"` for read-only and export work.

## 1.14.6 (20 Oct 2026)
- Add-in and MCP server (loads through the ACE loader without a Revit restart when 1.14.x is installed).
- **DWFx on true paper size:** each sheet's paper format (ISO A/B, ANSI, ARCH) and orientation come from its title block
  size; same-size sheets are exported together, mixed sizes give one combined file per size. A sheet that fits no format
  blocks the export unless `dwfx.allow_paper_fallback` (Revit's default paper, scaled, said so).
- **DWFx self-check:** every page (FixedPage and ePlot paper) is compared with its title block, size and orientation;
  a failed file is kept as `<name>-FAILED.dwfx` (never replacing an older one) and the target is not written.
  `sheetCheck` compares the sheets asked for with the sheets in the files; the reply states both checks.
- register_update: a PC without Excel is reported as "Excel is not installed" without the COM error text.

## 1.14.5 (20 Oct 2026)
- MCP server only (`install.cmd -SkipAddin`).
- **register_update output opens in Excel (third cause):** carried table styles used the `xr9:` prefix without its
  declaration, so styles.xml was not well-formed. Carried blocks now bring their namespace declarations (and
  mc:Ignorable entries); an undeclared prefix is stripped. Every XML part of the written file is parsed strictly before
  anything else (part, line and column named on failure); firstSheet / activeTab are reset when sheets are dropped. The
  Excel open test now fails on a timeout (a repair prompt), on a silently repaired copy and on "Unable to get the Open
  property"; it kills only the Excel it started.

## 1.14.4 (20 Oct 2026)
- MCP server only (`install.cmd -SkipAddin`).
- **register_update output opens in Excel (second cause):** the restored tables pointed at formatting overrides (dxfs)
  that exceljs had not written. The register's dxfs and custom table styles are now carried into the output with the
  references shifted (exceljs's own stay first). The self-check also verifies every dxf, cell style and shared-string
  index, and on Windows the output is opened read-only in Excel (COM, 60 s; `excel_open_test: false` skips it); any
  failure deletes the output.

## 1.14.3 (20 Oct 2026)
- MCP server only (`install.cmd -SkipAddin`).
- **register_update output opens in Excel again:** exceljs 4.4 reads Excel's tables as headerless (Excel omits
  headerRowCount when it is 1) and wrote `headerRowCount="0"` with an autoFilter, which Excel refuses. The written file
  now gets each table's XML back from the source register, and a self-check re-reads the file and validates every
  table (no autoFilter on a headerless table, ref within the used range, column count, unique ids); on failure the
  output is deleted and nothing is reported as written.
- An id pattern without a capture group (e.g. an anchored prefix from the vault) only filters ids, it no longer
  extracts them; `discipline` defaults to the template's only discipline; a confirm after an MCP restart says the check
  must be run again.

## 1.14.2 (20 Oct 2026)
- MCP server only (`install.cmd -SkipAddin`; the 1.14.1 add-in works with it).
- **register_update from a live run:** the report CSV never lands beside the output (it would be uploaded with the
  submission): `report_dir`, else the trusted vault's `04 Audits/registers`, else ACE's reports folder; never
  overwritten; a check writes its report too. Merged cells block only where they reach data rows in the id or stage
  columns (title-block merges are counted and ignored). The reply gives counts and the first 10 of each list, the full
  detail is in the report. `stage_columns` and `stage` take up to 60 columns.

## 1.14.1 (20 Oct 2026)
- First update meant to load through the loader with Revit open (no restart once 1.14.0 is installed).
- **Same-name models:** run_clash_test resolves models by path first (`with_model_path`); the active model is always
  "this" model; a name or path that fits more than one open document is blocked with the candidates listed (it used to
  test one copy against the other). coordination_report no longer looks up a name two documents share. Still open:
  clash_view colours by name, and the clash status store is keyed by title.
- **save_model** takes `handle` or `path` and saves a model that is not active; a shared title is refused. Lookups by
  path (close, activate, save) need exactly one match and never guess between a central and its copy.
- **capture_method stop with an untrusted vault** now stops before Revit is asked (the capture keeps running) and says
  how to give consent once (`vault consent: true`); the note says "not trusted" instead of "no trusted vault".

## 1.14.0 (20 Oct 2026)
- **ACE loader (G8): updates without a Revit restart.** A small, stable `AceRevitMcp.Loader.dll` (own version 1.0.0) is
  now the only DLL in the Revit `.addin` file; it owns the ribbon (proxy commands), the bridge's ExternalEvent and the
  Companion pane, and loads the ACE build from `payload\<version>-<hash>` named in `current.json` (own folder only).
  `current.json` lists the SHA-256 of **every file** of the build (Compiler and dependencies included); the loader
  refuses unlisted files, junctions and links, reads each file once and loads exactly the bytes it checked (the compiler
  and dependencies too). With signing on, the loader and every ACE DLL must be signed by the certificate pinned in
  `signing.json`; an unreadable `signing.json` counts as on and blocks the load. A build that fails its checks falls back
  to the previous one, with a warning in the log, `/health` and the Companion pane. **What the hash does not do:** it
  protects against corruption, incomplete copies and swapped files, not against a malicious program running as the same
  Windows user (it could rewrite `current.json` too); signing narrows that, since the key can't be exported. Revit 2025:
  one collectible AssemblyLoadContext per build; Revit 2024: loaded from bytes (old copies stay in memory, inactive,
  until Revit closes).
- **Upgrade:** the first install of 1.14.0 replaces the old `.addin` file, so a running Revit needs **one restart**.
  After that, `install.cmd` runs with Revit open: it adds the new build next to the running one and asks Revit to load
  it (`reload_addin`) when Revit is idle; if the new build fails to start, the previous one runs again. Only a new loader
  version needs a restart, and the installer says so. The unsigned-add-in prompt concerns only the loader, so it no
  longer comes back with every update.
- **`reload_addin`** (MCP tool and bridge command): reports loaderVersion, payloadVersion, previous, ok, why. Every bridge
  answer names the ACE build that ran it, and an apply whose preview ran on another build is refused, however the swap
  happened (installer, the loader's own switch when no model is open, "Load ACE again"). A reload keeps the models ACE
  opened (for close_model discard) and the last undoable Claude run, as plain data held by the loader. A reload whose
  bridge can't listen counts as failed (the previous build starts again).
- **doctor / check_setup:** loader and build versions, a pending reload. **uninstall:** removes the loader and every
  build folder. Benchmark case for `reload_addin`.

## 1.13.7 (20 Oct 2026)
- **clash_resolve no longer loses whole chunks on commit:** a dry run whose commit Revit refuses now fails (every native
  command: a refused commit was counted as success), so errors such as "Can't cut joined element" show in the preview.
  Steps behind a commit error, or behind new warnings only (e.g. a shape-edited floor), are found by the failure's
  element ids (else by halving, at most 24 trial runs, always rolled back), dropped, and the chunk runs again (3 rounds);
  dropped steps are report-only (`dropped-failing`, Revit's message and ids). The apply drops the same steps as its dry
  run; anything new is marked "differs from preview". `drop_failing_steps: false` keeps the old behaviour.
  `exclude_ids` takes steps out before the plan (report-only `excluded`).
- **export_deliverables:** DWFx runs inside a rolled-back transaction ("requires that document is modifiable").

## 1.13.6 (20 Oct 2026)
- **export_deliverables:** IFC setups saved in the model (the exporter's extensible storage) are read and used by name,
  model first then vault; `list_ifc_setups`. DWFx: one combined file named as the model, only the listed sheets or
  `same_as_folder` (the printed DWG/PDF set, unmatched files block), each sheet on its own paper size at 1:1; sheet
  sizes and scales reported.
- **workset_plan visibility check:** the dry run and the apply compare every sheet view and the export 3D views before
  and after; elements that would disappear from or newly appear on a sheet view are held back (their workset is not
  deleted) unless `allow_visibility_change` (true or view / workset names); export views are reported only.
- **Scripts:** file writes count as not undoable (readonly refuses them, a dry run does not run them); new
  `ctx.WriteOutput(name, text)` / `ctx.OutputFolder` writes to ACE's output folder in any mode.

## 1.13.5 (20 Oct 2026)
- **Workset rules from the second live run:** a rule applies only in models that already have its workset (no cross-model
  creates unless a row says create or `create_missing`); zone/asset worksets keep their elements (`naming.zoneWins`,
  default on); type tokens (SOG, PCC, `naming.typeTokens`) beat level + category; the self-check counts only model and
  annotation categories (Revit's own sketch planes no longer roll back an apply); worksets with leading/trailing spaces
  are found and can be renamed.
- **Documents:** `close_model discard:true` (only models ACE opened or detached copies); propose/confirm batches close
  what they opened and return to the model that was active; `open_model activate:true`; new `save_model` (risky, needs
  consent; saves in place, never synchronizes).
- **Safety:** scripts that call something not undoable (Save, SaveAs, RenameWorkset, Sync, Close, checkouts, exports, …)
  are not executed in a dry run: it compiles and screens them and says they cannot be previewed; readonly refuses them;
  the identical real run needs allow_risky. view_membership_fill: a shared-parameter-file group that is not a Revit group
  falls back to General; `param_group` overrides.

## 1.13.4 (20 Oct 2026)
- MCP server only (install with `install.ps1 -SkipAddin`, Revit stays open; the 1.13.3 add-in works with it).
- **Live fixes:** an explicit vault always wins (workset_plan, project_rules worksets) and its path is printed; without
  one the vault is found above or beside the model's folders; a vault without workset rules fails loudly (no silent
  fallback). Kept worksets (vault worksets.keep, written by confirm; confirmed names; saved rules' worksets) are never
  proposed for deletion or emptied by a reset. A singular category word after another word ("PUMP ROOM") is a place.

## 1.13.3 (20 Oct 2026)
- **workset_plan from the first live run (8 models):** levels match whole codes only (the code before "(" after an
  "X-" prefix, the bracketed words as a phrase, the add-in's level code / phrase, vault naming.levelAliases; never a
  substring or a single letter: BASE, GRID-B, GROUND BEAMS are no levels); "01" is a level when a code 01 exists; a
  category word counts as the head of the name, never inside a zone phrase ("POOL & Pump Room"); vault categoryTokens
  always count (phrases, one token -> several categories); FOUNDATIONS is Structural Foundations even with a FOUNDATION
  level; empty-but-expected and catch-all worksets are never proposed for deletion; Workset1 keeps its name unless the
  vault sets naming.defaultName; sketches, sub-elements and legend components get no row.
- **The standard:** vault worksets-standard.json (clauses, confirmed once by the user), else the project list, else the
  office worksets.json; every reply names it. **status** (read-only): % correct, wrong with where to, empty, not in the
  standard, missing, visibility, borrowed; each group applied by ACE / changed after apply / correct already / still to
  do (from the apply records); HTML + CSV and the model_dashboard tile. **propose refresh** keeps decisions and marks
  new / changed / gone; applied rows are not re-proposed. **apply only** (rows, worksets, categories, models) and
  **reset: "default"** (end state, each element moves once); moves already done are skipped; pins unpinned / moved /
  re-pinned (counts said); borrowed elements become pending actions in the vault, retried first next time; every apply
  writes an apply record and shows the status again.

## 1.13.2 (20 Oct 2026)
- **Optional local code signing:** `trust-ace.ps1` (run once per user, no administrator rights) creates a code-signing
  certificate in CurrentUser\My (RSA 3072, SHA256, 5 years, private key not exportable), trusts its public part in
  CurrentUser\TrustedPublisher and Root and saves the thumbprint to signing.json; `-Remove` undoes it. install.ps1 then
  signs ACE's own DLLs (AceRevitMcp*.dll, never third-party ones; timestamped when the server is reachable) and verifies
  each; any problem warns and the install carries on unsigned. doctor reports the signing state; the package ships the
  script; USER-GUIDE "Stop the Unsigned Add-In prompt".

## 1.13.1 (20 Oct 2026)
- **workset_plan review fixes:** the plan goes to apply_plan as workset_plan (no longer sharing the panel's plan field);
  saved link-code, zone and type regexes match between underscores ((?<![A-Za-z0-9])...(?![A-Za-z0-9]) instead of \b);
  saved rules keep first-match order (each new rule before the first less specific one, category-free rules last, never
  ahead of a category rule) and overlaps with existing rules are listed to confirm (overlaps_ok: true saves them); confirm
  reads both xlsx sheets (on both, the Groups sheet wins per group, with a note; unused decisions are said); apply prints
  what would move but was not previewed (worksets not yours yet) and the types that keep a workset.

## 1.13.0 (20 Oct 2026)
- **workset_plan (new):** propose a workset list for one or several models from the workset NAMES: one row per target
  workset (spelling twins merged) with what it holds (category decides, then level / zone, a material mismatch never
  matches, majority only breaks ties; links by discipline code, CAD imports to CAD / LINKS, stairs and runs to STAIRS),
  counts, open / default / export-view visibility (a move that would hide elements is flagged), visibility worksets
  manual only, the datum workset by the add-in's flag, identical models once; the element groups as detail; CSV + xlsx
  with a decision column. confirm reads the rule-level, group-level or hand-made file against the live facts (a changed
  count is flagged), writes a plan per model and shows the rules; save_rules + confirm: true merges them into the vault
  (conflicts listed, never overwritten). apply runs workset_admin apply_plan: check, dry run, identical apply, ONE undo
  step, borrowed elements skipped. Audit lists quote workset names (a name may hold commas).
- **clash_resolve chunks and flips:** chunk_size / chunk (also from the rules, validated), the chunk list and the next
  chunk; "Flip, confirm" items (existing joins in the wrong order that do not clash) switch only by id in flips, ids off
  the list are refused; concrete volume and area per category before and after; new warnings named by plan item; near
  and dropped not-touching pairs counted. run_clash_test tests other open models only when named (models).

## 1.12.9 (20 Oct 2026)
- **General method in the tool, template per project in the vault:** register_update reads the project's register
  template from the vault's rules/register.json (file or glob, headerRow, idColumn, idPattern, disciplines with their
  tab and overrides, stages with their columns, dateColumns; example bep/register.example.json), so a call needs only
  vault, discipline, stage, submission_date, label, the delivered files and output; explicit arguments win, the reply
  says what came from the file and what from the call, and an unknown discipline or stage is refused with the known
  ones. Office methods midp-register-update (register_update) and workset-cleanup (assign_worksets) added; each method
  says its scope. Workset rules in force documented: the project's first, the office worksets.json after them.
- **1.12.8 review fixes:** stored clash results counted once (the top-level total; the interference report uses either
  the not-rechecked rows or that total); clash_resolve narrows all-touching to the clashing pairs when the call gives
  keys and to a level or test when given, passes max_pairs and same_category (default true, false opts out; from the
  call or the rules files, validated) and prints the add-in's "above the cap" result; trim_to_face.preview_required
  false is refused (every apply already needs the identical dry run); the trim skip reasons are printed.
- **Workset audit, model-aware (from a live audit):** an empty workset whose name matches elements on another workset
  (LINKS = RVT links + CAD imports, COLUMN, BEAM, WALL, FLOOR, FOUNDATION, GRID, MEP/arch words; vault
  naming.categoryTokens extends it) is "empty, but N elements look like they belong here" and delete_empty keeps it
  (only the truly empty ones are sent, the kept ones listed with the reason); Workset1, or Shared Levels and Grids
  holding non-datum elements, is flagged with its element count, structural share and top categories; without rules
  the wrong-workset count reads "not checked: no rules" in the text, HTML and CSV. clash_resolve's cap line prints
  the joins above the cap (join + switch-join) and the add-in's advice; capture says when annotation counts are not
  available.

## 1.12.8 (20 Oct 2026)
- **Trim limit:** trim_to_face.max_mm defaults to 1000 mm; a rules file may raise it up to 3000 mm (longer trims are report-only, "trim too long").
- **From a recorded manual solve:** clash_resolve reads the vault's order as one category priority list (e.g.
  Structural Foundations > Structural Columns > Walls > Floors > Structural Framing, also the built-in default; same
  category: the larger element cuts) and says where the order came from (request, vault or default); plan steps name
  their cutter, and capture_method compares the person's join direction with it, with agreement per category pair.
- **clash_resolve R2 and R3 (approved by the owner):** scope "all-touching" (every touching pair of the categories,
  not only the clashing ones; selection: true for the current selection) and a new fix kind "trim" (a beam or wall end
  trimmed to the face it runs into), both from the vault's rules/clash-resolve.json (scope, trim_to_face { allowed,
  preview_required, max_mm }); a call can switch trim off, never on. The reply groups the plan per category pair, lists
  every trim (end, length before and after, trim, face), counts the not-touching pairs dropped and says where the order
  and scope came from; evidence and journal include the trims. Same gate: check, dry run, identical apply.
- **Office clash-resolve rules:** `%APPDATA%\ACE-RevitMCP\clash-resolve.json` (or config.json "clashResolveRules"),
  same keys as the vault file; the project vault's file wins key by key (trim_to_face per sub-key); the reply names the
  file each setting came from; project_rules section "clash-resolve" shows the merged rules; example in
  bep/clash-resolve.example.json.
- **Office methods linked to tools:** guides/methods/index.json is the registry (name, skill, version, updated, tools,
  sourceCase, file) and guides/methods/<name>.md the text; revit_guide lists them ("methods") and reads one
  ("methods/<name>"); each linked tool's description ends with "Office method: revit_guide methods/<name>" from the
  registry; a capture case with proposed join rules names the method it would update. First: structural-interference-solving
  (clash_resolve, run_clash_test, coordination_report, capture_method), version 1.
- **Clash results not re-checked:** run_clash_test and coordination_report show results from other models and stored
  results this run did not re-evaluate on their own line ("stored, not re-checked in this run: N"), out of the open
  totals; interference reports leave such rows out.
- **Clash store key:** run records, evidence and interference file names use the add-in's store key (the central
  model's name), so runs on local copies connect; capture_method finds the plan by it.
- **capture_method:** area, volume and annotation changes are grouped in the case ("Floors: area changed on 3 (sketch
  edits likely)", "Annotation: dimensions 120 -> 119").
- **register_update:** a heading with fewer naming fields than most ids and a word ("000-GENERAL") is skipped; an id
  listed twice in the register appears once in every list, marked "(x2 in register)".

## 1.12.7 (20 Oct 2026)
- **register_update, from a test on a real register:** section heading rows are no longer ids (id_pattern from the call
  or the vault's rules/naming.json idPattern; without one, an id needs a digit and a separator and no space, or the
  shape most rows share), listed as "headings/skipped"; stage takes a list (one date into the planned and actual
  columns); a delivered file and an id with the same final number block and one naming field different are reported
  once as "probably the same, confirm" and set only with accept_near; a wrong id column (median id under 6 characters,
  over 20% duplicated, or a file matching several ids) is blocked in the check too, with sample values
  (force_id_column overrides).

## 1.12.6 (20 Oct 2026)
- **view_membership_fill:** one text parameter set from what a view shows: value_in on the elements the view shows,
  value_out on the rest (e.g. an export status from the Navisworks view). The reply says per reason why elements are out
  (section box, hidden, category, view filter, phase, workset). A parameter missing in the model is bound only from the
  vault's shared parameter files (never an invented GUID; otherwise blocked); a likely misspelling against the vault, an
  approved spelling rule, near-duplicate names in the model and every rule / IFC settings / requirement file that names
  the parameter are said before anything changes. Binding needs categories (a shared parameter file holds none);
  only model views are accepted, and a view that shows none of the elements blocks. Linked models are not filled. Check > dry run > identical apply (planHash), ONE undo step,
  self-checked; a run record is written.
- **export_deliverables:** NWC, IFC and DWFx in one call, each optional, into a required out_dir (an existing file is
  replaced only with overwrite). File names from the vault's rules/export.json nameTemplate ({model}, {date}, {format})
  or the model name; IFC settings from a JSON file or a named setup kept in the vault (rules/export.json ifc.setups or
  rules/ifc/<setup>.json; exporter keys are translated). check_only gives the pre-flight; every block (no Navisworks
  exporter, view / setup / sheets missing, file exists) is reported and nothing is exported. The reply is a table of
  file, size, time, count and warnings, plus a run record. The model is not changed.
- **register_update** (no Revit, xlsx): a deliverables register updated for one submission. The register sheet is
  copied into a new workbook named by the submission's label (formulas naming the sheet follow, formulas into other tabs
  become values), delivered rows get the submission date in the stage column, later dates are cleared, and the report
  lists matched, delivered but not in the register, not delivered and cleared. A check writes nothing; the write needs
  confirm after the identical check; the register itself is never written. Columns are found by header text first
  (a header "ID" or "IFC" is never read as a column letter); dates are compared by whole day; a later date computed by
  a formula is reported and left; merged cells in the id or stage columns stop the write unless allow_merged; defined
  names and data validations into dropped tabs are warned about; an existing report CSV is refused like the workbook.
  New dependency: exceljs.

## 1.12.5 (20 Oct 2026)
- **Worksets audit, rename, delete empty** (assign_worksets action audit / rename / delete_empty): a reviewable table of
  every user workset (elements by category, owner, open) flagged empty, off the naming rule, near-duplicate or holding
  elements on the wrong workset, as HTML and CSV with out_dir; the naming rule is a new optional `naming` { pattern,
  allowed, rename } in the worksets section of project-requirements.json. Rename (default: the rule's suggestions) and
  delete_empty (a workset with elements only with a named move_to) go check > dry run > identical apply (expect_model,
  planHash / expect_plan), ONE undo step, self-checked (element count per category unchanged). Shared Levels and Grids
  is never renamed or deleted; every block says what, why, who and how.
- **capture_method** (start / status / stop): records how a person solves something. The case (vault cases/<date>-<label>
  .json and .md, else out_dir) groups the element changes into actions by pair type with counts and exceptions, lists
  the Revit commands from the journal, compares with the latest clash_resolve plan (agree / differs / planned, not done
  / not planned) and ends with proposed rules that need the owner's confirmation; nothing becomes a rule by itself.

## 1.12.4 (20 Oct 2026)
- **open_model, nothing silent:** reads the transmitted flag and the external references before opening; lists
  missing links (`unresolvedLinks`); answers the "Unresolved References" dialog with "ignore and continue" and records
  every dialog with its answer and why (`dialogs`; one that needed a person is a warning). A transmitted workshared
  model opens detached with worksets kept unless told otherwise; `clear_transmitted` (writes the file, needs confirm)
  clears the flag first. The transmitted dialog itself is never answered by ACE.

## 1.12.3 (20 Oct 2026)
- **open_model / close_model:** open a model in the running Revit and make it active (detach keeping or, with
  confirmation, discarding worksets; audit; all / last viewed / no worksets), optionally closing the previous model
  without saving when it has no unsaved changes; close a non-active model without saving. Revit stays open across a
  batch of models. Refuses missing, already open and newer-version files with the reason.
- **Interference report after a clean run:** run_clash_test stores every run (also with 0 clashes, with its filters), so
  the report is written with "0 interferences found", the filters and what they left out.

## 1.12.2 (20 Oct 2026)
- **Structural material filters on the clash tools:** `materials`, `exclude_materials`, `a_materials`, `b_materials`
  (steel, concrete, precast, timber, masonry, other, unknown) on run_clash_test, clash_resolve and coordination_report,
  so steel can be left out where it shares categories with concrete. What a filter left out is always reported; a
  filtered run never marks stored clashes of filtered-out elements resolved; the scope is part of clash_resolve's gate.
- **Interference report:** `coordination_report` with `interference: true` and `out_dir` writes, per model, a
  client-readable HTML laid out like Revit's Interference Report (A and B as category : family : type : id, level,
  depth, location, status, responsible; grouped by issue, then approved and resolved) with the same rows as CSV and xlsx.

## 1.12.1 (20 Oct 2026)
- **clash_resolve, from the first live test:** joined steel ends are cut back with the join cutback (the extension only
  for free ends; `plan[].method`); each step is checked on its own and a step that does not clear its clash is dropped
  and reported ("did not clear the clash (depth X -> Y mm)", `dropped`), so one bad cope no longer rolls back the good
  joins; report-only items carry a reason code and are grouped (`reportGroups`) with a recommendation per reason.
- **Change summaries:** warning records Revit removes when a change resolves warnings read "N resolved warning
  record(s)", never as deleted elements.
- **run_clash_test:** a clash that comes back after being resolved shows as "reopened", not "new".

## 1.12.0 (20 Oct 2026)
- **Structural self-clash solver (`clash_resolve`):** runs an ST vs ST clash test on the host model and fixes only what is
  safe without moving or replacing anything: Join Geometry / Switch Join Order for concrete pairs (join order from the
  project vault `rules/clash-resolve.json`, default foundation > column > wall > beam > slab > stair), and steel end
  cutbacks (start / end extension, location line unchanged) or copes against the supporting column or beam. Everything
  else (mixed material, duplicates, deep overlaps, nodes, links) is report-only with a recommendation.
- **Same safety as parameter_fill:** `action: "plan"` is read-only; `fix` needs an identical dry run in the same session
  and the apply is bound to the previewed plan (`planHash` / `expect_plan`, refused as "the model changed since the
  preview"). Fail fast on groups, design options, borrowed elements and stale central models. Inside the one undo step
  the clash test runs again and a guard compares every touched element (exists, type, location, tags, dimensions and spot
  elevations with their references) and the Revit warnings per key; any difference, any fresh warning or a fix that
  made a clash worse rolls everything back. Changed dimension values on shortened members are reported as information.
  Evidence file with clashes before and after.

## 1.11.3 (19 Oct 2026)
- **Impact shows what is actually written:** for a target a derive rule fills (a literal Project Information value, a copy
  or a join), the title block and label lines show that value or source instead of the NA text.
- **Guide:** a preview is remembered per MCP server process; after a restart the dry run must be repeated before the apply.

## 1.11.2 (19 Oct 2026)
- **Discipline from the title:** the mapping's `titleField` (the title token that holds the discipline) reaches the
  add-in. A title with several discipline codes (or none) puts a warning first in the check reply and the summary
  workbook; that model's discipline-scoped rows are plan-only until `models` or `titleField` says which.
- **Complete with NA is its own state:** never counted as complete in the summary text, workbook, markdown or evidence.

## 1.11.1 (19 Oct 2026)
- **Alias rows accept every GUID:** a name matched through an alias (explicit, prefix map or the spelling rule) accepts
  every GUID the files define for the target name, like a direct match (no false "GUID differs").
- **Several Revit sessions:** the default is the session with a model open; with several models open and no `instance`
  or expect_model to decide, ACE lists them and asks (a write never goes to a guessed Revit).
- **Rolled back means nothing set:** after a rollback (failed self-check, `rolledBack`) the reply and the evidence say
  "would have been set; rolled back" for fill, derive, bind and rebind counts; no `changed` block, no after state.
- **Impact:** title blocks in one line ("shown on N sheet(s) via title blocks"), inferred for built-in Project
  Information parameters (their labels leave no GUID or name in the family, so every title block family with instances
  is listed; `titleBlocks: { families, sheets, instances }`, labels carry `proxy`).
- **Add-in answers after a rollback:** every native change answers `rolledBack`; a failed apply puts its summary under
  `wouldHaveChanged`, and parameter_fill moves counts to `wouldSet` / `wouldDo` and the end state to `afterIfKept`.
- **Title rules:** a derive glob (matches the FULL model title) or `models` key that matches no open model is a warning
  in the plan.
- **LOIN cells with several names** ("A, B"): one row per name (the first part's prefix carried); parts that do not
  resolve make the cell a pseudo-name "to check"; a client file that also defines the whole cell is a client action.
- **Evidence items:** the mapping's `evidence.items` (id, text, rows / check) are listed in the evidence file with their
  status after the apply.

## 1.11.0 (19 Oct 2026)
- **Add-in: one plan per model, verified before it is kept:** `parameter_fill` takes `bind`, `rebind`, `derive`
  (copyFrom, join, or a literal `value` written only into truly blank targets), the fill, `impact` and `register` as one
  write plan in one transaction group (bindings > rebinds > regenerate > derive > NA last; type values written once).
  Before commit every planned write is read back; any mismatch rolls the whole group back (`verify`, transaction
  "VerifyFailed"). Bindings come from a shared parameter file only (hash checked, the file setting restored and read
  back; untouched in check_only), unknown group types are refused, and borrowed or locked elements block the plan with
  who holds them and how to clear it. Impact scan: schedules, view filters, and tag / title block labels (families open
  in the editor are skipped and named). Register of every blank id; `whitespaceOnly` counted.
- **Several Revit sessions:** each add-in listens on the first free port of 48884-48893 and writes
  `%APPDATA%\ACE-RevitMCP\instances\<pid>.json` (machine, models; family documents left out).
- **Execution stage (`parameter_fill action fill`):** runs the ready auto-fixable actions of the active model (or
  `actions: [ids]` from the action list) as ONE plan, ONE approval, ONE undo step: bindings (from the project shared
  parameter file, or a temporary file holding exactly the client's line, with "add this line to the project file"; never
  a new GUID; never for built-ins), derived values from the mapping's approved derive rules (ACE shape, or the project
  shape's twins and string formulas that parse to a join; others are reported "rule not machine-readable"), then the
  approved NA value. The gate key covers rows, bindings, derive rules, value and the source file bytes; the apply sends
  `expect_model` and every write plan `expect_version`. With `all_documents` the other models' plans are listed read-only.
- **Fail fast, never silent:** an unapproved NA policy or derive section, a missing project shared parameter file, a
  changed source file (named), another add-in version and borrowed elements stop the plan before Revit writes anything,
  each with what, why and how to clear it. Held parts of the default plan and every skip are listed in the reply.
- **Impact (P4) in the dry run:** schedules (has-value filters), view filters, labels on sheets (inferred), IFC property
  sets (`ifc_property_sets`) and the client checks that flip, with the caveat that "NA" passes a HasValue check.
- **Missing register and evidence (P7):** `missing-register-<model>-<stamp>.csv/.md` before every fill (report_dir, else
  the trusted vault's `04 Audits/<model>/parameters`, else the reports folder); `evidence-<model>-<stamp>.md` after an
  apply (missing before, bound, derived, NA written, skipped, still blank, not bound, self-check, blocks); a
  `fill-run-*.json` run record with the blocks and skips. A failed self-check is reported as rolled back.
- **Actions:** every action has a stable id; auto-fixable ones carry what the execution stage runs and whether it is ready.
  New: built-in parameters not available on some categories (information action, never a binding; the AIS `origin`
  column is read), LOIN sheets without a discipline scope, names a client file defines several times with different
  GUIDs (all GUIDs accepted in the check: `guids` on the bridge row).
- **Discipline scope:** generic role codes (AR, ST, ME, EL, PL, FP, ID, LA, CV, BM, PS) read the discipline from the model
  title when the mapping names none; `groupDisciplines` scopes AIS and checker rows per group ("*" = every model).
- **Checker review:** scope defects of parked checks are reported too (flagged). A check of all open models says how many
  linked models were not checked.
- **Write-path hardening:** one Revit session is pinned per call; every preview records its session (machine, pid,
  port) and an apply that would reach another session is refused. The dry run also names its model. Rows whose scope or
  categories rest on an unapproved mapping part (an entry with confidence other than high, an unapproved section) are
  plan-only: checked, held with the reason, never written. An unknown add-in version blocks. Audit files never overwrite
  each other and model names are safe in paths; an untrusted vault is said. Every binding carries `fileHash`.
- **GUID differs, not wrong (DEC-0004):** status `guid-differs` (1.10 `wrong-guid` still read): a decision with two
  options, and an explicit-only `rebind` action that replaces the definition keeping the values (shows what is dropped).
- **Mapping:** an approved `nameAliases.spellingRule` turns case / space-only differences into aliases;
  `derive.projectInformationValues` sets literal Project Information values per model title glob (most specific wins).
  `test_force_verify_mismatch` (acceptance tests only) forces the apply's self-check to fail (the dry run passes, so the
  gate allows the apply), which rolls the whole plan back.
- **Several Revit sessions:** the server reads the add-in's instance files (live pid + /health), picks the session by
  model (`expect_model`, or `revit_status instance`), else the only one, else the most recent with a note naming the
  others; `revit_status` and `check_setup` list them. Older add-ins: /health on ports 48884-48893, then config.port.

## 1.10.0 (19 Oct 2026)
- **Parameter requirements from several sources:** new read-only tool `parameter_check` merges the AIS CSV (`list`),
  the LOIN workbook (`loin`), the client's Model Checker XML (`checker`, active HasValue / Defined checks only) and the
  shared parameter files (`shared_parameters`, `project_parameters`) into one requirement table, through the project
  mapping (below). Names are
  classified (exact, client-only, spelling or prefix-drift candidate, unknown); unmapped categories, candidates, typo
  twins and GUID conflicts are reported, never guessed. Actions: `check` (per model: status per row, NA counted apart,
  discipline scope; GUID differences across models and Project Information twins with different values),
  `requirements` and `review_checker` (no Revit). Large results go to `report_dir` as CSV + markdown; the reply stays compact (summary, per-row status, defect counts).
- **Project specifics live in the vault:** the mapping (client category -> Revit categories, LOIN sheet -> disciplines,
  aliases, LOIN column names, NA policy) is read from the vault's `rules/parameter-mapping.json` (or `vault` /
  `mapping`); without one only the generic checks run and the reply says what is missing. `create_mapping: true` writes
  a draft with best guesses; a draft never drives a fill. ACE extends only a mapping in its own shape, writes it
  atomically and never rewrites a file it cannot read. No built-in fill value: the NA policy or `value`.
- **Model-aware pre-flight on every check:** Revit / ACE version, workshared and central, worksets, links (not loaded),
  phases, design options, groups, and for the blank values: how many sit in groups, design options or elements borrowed
  by someone (named). Each model's discipline comes from the mapping or its title; rows of other disciplines are out of
  scope, and elements of categories owned by other disciplines are counted as foreign, not judged. Faster scan on large
  models (one collector per category, parameters read by definition / GUID, type lookups cached).
- **Fill:** `parameter_check fill_plan: true` counts what a fill would set without a transaction; `parameter_fill`
  needs its own dry run, in the same model (`expect_model`), and the bytes of every source file are part of the
  preview: a changed file needs a new dry run. After an apply the reply and reports show the state after the fill.
- **Federated summary and actions:** whenever a check writes reports (`report_dir`, or a large result), ACE also writes
  `parameter-summary_<date>.xlsx` (sheets Summary: discipline x model x status, Rows: model x requirement with sources,
  Actions, Pre-flight) and `actions_<date>.md`: a ranked, read-only action list (submission blockers first, then blanks
  by element count, decisions, client file defects), each with why, impact, owner, type and how to clear its blockers.
  The reply shows the top 10.

## 1.9.3 (19 Oct 2026)
- **Parameter list check and NA fill:** new tool `parameter_fill` reads a client parameter list (CSV: parameter_name, categories, type_instance; or rows) and reports per open model which parameters are complete, blank, missing everywhere, not text or (Project Information) not in General. `action: "fill"` sets "NA" (or `value`) on blank text parameters of the active model, previewed first, one undo step.
- Compliance shows a decided project decision (kind "note", status "decided") beside the requirement's status, met included, so the result names the reading it rests on (e.g. "DEC-0002 (decided: 25 mm in force)"). Open notes stay hidden; deviations work as before.
- `check_setup` on Revit 2024 lists C# scripts as "not available yet" (info) instead of a failed Revit session with a wrong fix.

## 1.9.2 (15 Oct 2026)
- **Model size in compliance:** a requirement bound to the file-size check is now met or not met by the check's pass
  rule (e.g. `failAt: 300` MB), not by its count, which is MB rather than failing items. Evidence reads "File size:
  280 MB (warn; review at 250, action at 300)". Reports show the size in MB.
- **Fix:** a note in the project file's `checks` section (such as `"about": "..."`) made compliance fail; notes are
  now skipped. Found with the Al Nahda project file.

## 1.9.1 (15 Oct 2026)
- **Fixes from the release review:** the add-in and Claude's vault tool now find the same vault (the "ACE Vault" folder
  beside a project file, or the file's `"vault"` setting); one bad entry in the trust list no longer untrusts every
  vault; `vault report` defaults to the open model when a vault holds several; report names are matched in full, so
  "Tower" never picks up "Tower_audit_X"'s reports; on Revit 2024, files are replaced in one step (no window where the
  old snapshot or log is deleted but the new one not yet in place).
- **Master audit report (V4):** `vault report` builds one report per audit from the vault and the latest compliance run:
  cover, summary (counts, open findings by severity, score, the three actions that matter most, what closed and what is
  new since the last report), document register (changed since the last audit), compliance matrix (requirement, source
  clause, test, evidence, status, severity, decision), model findings, decisions and deviations, and actions. Saved in
  `04 Audits` as HTML, Word (A4 landscape, real tables, brand font and colour) and an actions CSV, plus a small record
  the next report compares with; the vault index links the latest. Compliance run records now carry the model check
  (score and every check). New `vault model_summary`: keeps the latest report's summary on Project Information
  (extensible storage, readable by anyone, writable only by ACE), previewed and applied as one undo step, only when asked.
- **Companion: Compliance tab (W4, first part):** what the model was audited against, % met, a status bar, requirements
  grouped by kind (worst first), and per requirement its source, the document's words, the evidence, any decision and
  "Show in model" for the failing elements of the bound check. Re-run audit, open the master report and the vault from
  there.
- **Ribbon Audit > Vault and Master Report (V5):** Vault opens the project's vault and asks once per vault whether ACE
  may save audit records there (the same permission Claude's vault tool uses). Master Report opens the model's latest
  report; without one it shows the summary kept in the model and a ready prompt for Claude.

## 1.9.0 (15 Oct 2026)
- **Revit 2024 support, first part (R24):** the add-in is now built twice from one source, for Revit 2024 (.NET Framework
  4.8) and Revit 2025 (.NET 8). The installer puts the right build into each installed Revit (or the years given with
  `-RevitYear`); the package holds `bin\addin\2024` and `bin\addin\2025`. On Revit 2024 every native tool works (model
  check, model brief, element search, warnings, clash tests, worksets, parameters, presentation standard, change
  tracking, compliance), but C# scripts (`execute_revit_code`, `run_saved_script`) are not available yet and say so.
  System.Text.Json 8 and its dependencies ship beside the 2024 add-in, served by a resolver that only ever loads ACE's
  own copies. doctor, uninstall and `check_setup` check each year (no script compiler expected on 2024). Use one Revit
  with ACE at a time: both share one port.
- **Compliance per requirement (C1+):** every model check now also evaluates the project's requirements: met, partly
  (a measured part of a set, e.g. 3 of 100 elements on the wrong workset), not-met or not-checkable, with the evidence and
  any accepted deviation shown beside the status (a deviation made against an earlier text of the clause asks to be
  confirmed again). Requirements reach a check through `"requirement"` on a check or workset rule in
  project-requirements.json, or the record's `test.binding`. The result goes to the status store, and in a trusted vault a
  run record (rules and requirements hashes) is saved in `04 Audits/<model>/runs`. New `vault` action `compliance` reads
  the last result without re-running the check and says when it is out of date. Checks that measure a set now report its
  size (views, sheets, key parameters, project information, BEP worksets).
- **Project rules (C1, first part):** one `project-requirements.json` per project holds the project's own rules in the
  same sections as the office files (`checks`, `presentation`, `worksets`, `clash`). It is found beside the central model
  or in its `ACE Vault` folder, or, for cloud models, through `config.json` "projects" (cloud model id, central path or
  title to the project folder). One resolver merges it over the office files key by key and the project wins (a check's
  settings merge one by one, project workset rules come first, `"inheritOffice": false` replaces a section). The model
  check, `assign_worksets`, the clash responsibility ranks and the presentation standard all read the merged rules; the
  presentation window saves to the project file when the project defines the standard. Clash ranks are re-read when the
  rules change (before: only after a Revit restart). New read-only tool `project_rules`: which file applies, how it was
  found, where each section comes from, a hash per section and the merged rules.
- **One source for workset rules (T11):** the MCP server no longer falls back to the packaged example rules; it asks the
  add-in for the rules in force, so Claude and the model check always use the same rules.
- **Requirements vault (V1, V2):** new tool `vault`. An `ACE Vault` folder per project (created beside the central model,
  or in a folder you name and, for cloud models, remember in `config.json`) with the five folders of the audit design, a
  document register (`vault.json`: key, type, revision, status, SHA-256), the requirement records
  (`requirements.json`), the decisions (`decisions.json`), the project requirements file and a browsable `index.html`.
  ACE reads the documents itself: PDF (pages, and client comments made as PDF annotations), DOCX (paragraphs with their
  heading path, table rows; unaccepted deletions are left out and flagged), XLSX / XLSM (rows by sheet; hidden sheets
  flagged) and text files (UTF-8 or UTF-16, e.g. shared parameter files). Records are validated: a record without its
  document, quote and location is rejected. Ids stay the same across revisions; a new quote for an id keeps the old one
  and asks for acceptance again. Severity is info / warning / error (the extraction's own word is kept); status is not
  stored on a requirement. `revalidate` re-reads one changed document and checks every record's quote at the page, row
  or paragraph it cites. Decisions (accept, deviation, reject, note) record who and when. The first write to a vault
  asks for the user's consent; then the folder is trusted. Tested on the first real project set: 341 records from 17
  documents imported, and all 341 quotes found at their cited place by ACE's own reader. PDF reading needs Node.js 22+
  (the doctor says so on older versions).
- **Fix (T10):** the Companion's "Clash issues" tile showed a type name instead of the number of issues.

## 1.8.3 (7 Oct 2026)
- **Development radar** (in the learning report): groups the local call log into tasks and flags development
  candidates: tasks with many calls, repeated previews, failures, expensive tasks, large replies, code written again
  and again. No tokens, no background process. The call log now records reply sizes.
- **Development team:** ten agents (`.claude/agents/`) and the saved workflow `ace-dev-cycle` (propose, one approval,
  build); `docs/DEV-TEAM.md` and the ranked `docs/BACKLOG.md`. A build cycle first checks how many items wait for a
  live check in Revit and, above 3, returns the owner's live tests instead of building; both builders get the planner's
  integration contract; the offline checks run again after the fix round; the usage and market scouts run only with
  shared reports or when asked.
- Claude is told to work with the fewest interactions (one question up front, one script and one approval per job).
- **Navisworks clash reports:** a Clash Detective report (XML, or the tabular HTML) is imported into the clash engine
  (Clash Browser **Import Navisworks report**, or `run_clash_test` with `import_report`). Each Navisworks test becomes
  "NW: name"; every item is matched to the real element (model by file name, then element id), so issues,
  responsibility, cause, the Clash View, approvals, the coordination report and the dashboard work on it. Re-imports
  track new / active / resolved; Navisworks Approved and Resolved are kept, ACE approvals survive. Items in models that
  are not loaded keep their Navisworks name and id. A native clash run no longer hides imported tests from the browser.
  The later stage (a Navisworks add-in, two-way status, batch runs) is in the backlog (N1 to N4).

## 1.8.2 (6 Oct 2026)
- **Apply cards you can understand** (the owner: "I don't understand what to approve, especially for a long multi-step
  process"): every card shows Claude's plain explanation of what the change does and why (Claude must now give one
  with every preview), the steps of a multi-step job with this one marked ("Step 2 of 4"), a red "Look at this before
  applying" box for deletions and for changes to parameters, types or families (with names, e.g. "Deletes 2 Shared
  parameters: Fire Rating, Acoustic Class"), "What exactly changes" per category with element names, and "Show in
  model" to select the changed elements. A card without a title takes it from the explanation, never "script".
  Claude is told to stop and explain any deletion or shared-data change that the request did not ask for.

## 1.8.1 (6 Oct 2026)
- **Worksets per the BEP, native** (concept D7): the rules engine moved from a script into the add-in. The model check
  now reports elements on the wrong workset whenever there is a rules file; `assign_worksets` uses the same engine,
  previewed and applied like every change (Apply card, one undo step).
- **Review fixes** (`/code-review` of the 1.8.0 changes):
  - Cancel: each request has its own id and token; a cancel in Claude stops only that request, never a clash run you
    start from the ribbon or your own actions queued behind it.
  - Long calls no longer fail after 5 minutes (the bridge call no longer uses Node's fetch).
  - Long replies always stay valid JSON; lists inside lists are shortened first, with a note.
  - Presentation standard: spot dimensions no longer counted twice; scales without a size row are left alone instead of
    failing; a check now counts the annotations that need a new type; an apply uses exactly the standard that was
    previewed; the window no longer saves on Check or Preview, keeps choices for kinds the model doesn't have, accepts
    "1:50", and its previews leave no Apply card; named views are used even when not on sheets; dependent views count.
  - Scripts: `tag_untagged` tags rooms; `delete_duplicate_instances` never deletes a copy that hosts something or is in a
    group; `renumber_doors` never repeats a mark of a door it does not renumber and letters go A..Z, AA...
  - Safety: task dialogs are never answered with OK; the quick text screen checks the code as written too and no longer
    flags "m²" or the word "dynamic"; the compiler screen also catches XML files, temp files, copying models, writing
    transmission data, printing and the shared parameter file.
  - Snapshots record a model version only when it is saved; find_elements' `truncated` is right when paging; the
    team check set is written to the share even before it exists; area warnings get the area hint.

## 1.8.0 (2 Oct 2026)
- **Merged duplicates** (concept D1, D3, D5, D6, D8, D9):
  - The *Clash Results* window is merged into the **Clash Browser**; `run_clash_test` with `show` opens the browser.
  - *Model Health* (Audit panel) is merged into **Insights → Dashboard**.
  - `clash_view` is one tool for Claude: overview, focus on a clash (`key`), or `reset`. Previously there were three.
  - `get_model_overview` is now `get_model_brief` with `quick: true`.
  - The built-in scripts `audit_model` and `rooms_without_doors` are retired: the native model check covers them.
  - A **status store**: the model check, clashes and changes publish their latest result, and the Companion and
    dashboard read the same numbers.
- **Presentation standard** (ACE > Deliver > Presentation Standard, and Claude's `presentation_standard`): printed text
  height by view scale (default 3 mm for 1:50 and larger, 2.5 mm for 1:100 and smaller) and one type per kind (text,
  each dimension style, optionally tags per category) and size across the views on sheets. A window to choose the type
  and size for each, check, preview and apply (one undo step, Apply cards in the Companion). The rules live in the office
  check set (`presentation`), and the model check reports text off the standard and mixed annotation types.
- **Stress review, round 2:**
  - Speed: clash search through a 3D box grid (one pass per model, booleans only where boxes touch); automatic
    snapshots after the save at idle time, written in the background; compiled scripts and reference assemblies
    cached, compiler warmed up at start; unchanged links reuse their snapshot; quicker model check, type lists,
    overview, brief, element search; all coordination pictures in one export.
  - Safety: the compiler checks what a script really calls (files, programs, network, reflection, saving / syncing),
    enforced in the add-in (`tools/risk-cases`); task dialogs are closed with Cancel, never confirmed;
    `ctx.CanEdit` / `ctx.Editable` skip elements in use by colleagues; Claude's cancel reaches Revit (`/cancel`,
    `ctx.ThrowIfCancelled()`); team lessons are capped and labelled as notes.
  - Workflows: `list_warnings` and the `delete_duplicate_instances` script; `renumber_doors`; `tag_untagged` in named
    views or every floor plan; `sheets_for_levels` reuses existing plans and skips levels with sheets; test fit for a
    list of rooms or a whole level; `option_label` on Apply cards; paging (`offset` / `next`) on find_elements and
    list_types.
  - Tools merged from 42 to 36: `lessons` (remember / recall / forget), `snapshots` (take / list), `saved_scripts`
    (list or read), `run_clash_test` `stored` / `sources`, `report_issue` kind `learning`.
- **Stress review** (`docs/STRESS-REVIEW.md`: complex tasks, speed, best practice, tokens). Fixed in this round:
  - A preview applies only in the model (and, for code, the view) it was previewed in; a failed rollback is reported,
    never hidden; script assemblies are unloaded after each run.
  - Revit busy: a request must start within 60 s and the error names what Revit is doing; a long call that is still
    running says "do not send it again" instead of "Revit is not reachable". Claude's cancel stops the wait, and long
    calls send progress.
  - `model_changes` understands weekdays and "3 days"; unknown words are an error instead of "no changes".
  - `set_clash_status` works from clash or issue keys alone; `clash_view` focuses an issue; issue lists are shorter.
  - Safety screen catches aliases, comments inside calls, reflection, `SaveAndClose`, unloading links and reading
    files; tool labels corrected; the bridge accepts this PC only, with a constant-time token check and a size limit;
    `assign_worksets` always runs the built-in script (15-minute default for large models).
  - Tokens: compact JSON replies (about 25% smaller), long replies shortened list by list and kept valid (60k
    characters at most), script result first and repeated warnings grouped; workflow detail moved from the
    connection instructions to `revit_guide workflows` (about 1,100 fewer tokens in every conversation).
- **Tidy pass** (`/simplify`, whole codebase; behaviour unchanged):
  - The status store now holds each model's results and clash-view session; the leftover "last result" statics in
    clashes, dashboard, change tracker, coordination report and clash view are gone (they could mix up two models).
  - Clash runs build the model list and a geometry cache once for all tests; issues and counts are computed once.
  - Shared helpers: `ViewTools` (view types, highlight, solid fill, boxes, PNG export) replaces three copies of the
    picture export; `Lengths` replaces the scattered 304.8 conversions.
  - One check for "already applied or cancelled in the panel" (`ActivityHub.Decided`) and one preview gate in the
    MCP server (`gated`) for code runs and parameter changes.
  - Clash Browser search is debounced and virtualised; switching work mode only redraws icons that change; snapshots
    compress faster; finished bridge calls release their timers.
  - Journal, activity log and learning report files use the local date (not UTC). A damaged line in the benchmark
    history no longer hides the whole trend. Dead code and unused usings removed.
- **Code review fixes** (whole codebase):
  - **Other open models stay safe:** a dry run or read-only run now rolls back changes to every open model, not only
    the active one. An applied run keeps them together.
  - **No double apply:** an apply uses up its preview before it is sent, so a timeout followed by a retry cannot apply
    the same change twice. The add-in also refuses a second apply of a change Claude already applied, until it is
    previewed again.
  - **`set_parameters`** commits inside a transaction group, so Revit's checks run during the preview too. If Revit
    rolls the change back, it reports that nothing was changed, instead of "applied".
  - **Clash status:** a clash run only resolves clashes between models that were loaded. An approved clash stays
    approved if it disappears and comes back.
  - **Risky-code screen** also catches `File.Open*`, `FileInfo`/`DirectoryInfo`, writers, zip files and model exports.
  - **Undo:** ACE's own rolled-back work (coordination pictures) no longer stops `undo_last_claude_change`.
  - **Clash speed:** each element's geometry is read once per clash run.
  - **Lessons:** a project lesson can be saved for two projects with the same wording.
  - **Workset rules:** a file saved with a byte-order mark is read correctly, and an invalid file gives a clear message.
  - **Activity log:** the date must be YYYY-MM-DD, so the path cannot leave the journal folder.
- **Companion redesigned**: a header with a connection light, the model name and the work mode (switchable); a tab bar
  with counts; a new **Home** page (what needs you, the model at a glance: health score, clash issues, changes; quick
  actions that run straight away: model check, Clash Browser, clash view, what changed, coordination report, snapshot;
  and *Ask Claude*: your text plus the view and selection, copied for Claude Desktop). Approvals: option cards with
  added / modified / deleted chips, expiry, pictures that enlarge on click, *Ask for changes*. Activity: markers by
  kind, grouped by day, a changes-and-failures filter. Results: search. Prompts: the mode's first, foldable groups, search.
- **Workset assignment per the BEP** (`assign_worksets`, built-in script `assign_worksets`): rules by category, function
  (wall function, structural, MEP system, framing type), family / type name, level, zone (scope box), room department or
  parameter value; first match wins; names can use `{level}`, `{zone}`, `{category}`. Rules file `worksets.json`
  (`%APPDATA%\ACE-RevitMCP` or `"worksetRules"` in config.json for the team share); example `bep/worksets.example.json`.
  Check only (read-only report), Workset1 only, create missing worksets; borrowed elements are skipped; the usual
  preview, Apply card and one undo step. Prompts in the Production and Model audit modes.
## 1.7.0 (core v2, installed on the work PC 30 Sep 2026)
- **Working modes** (ACE tab → *Work mode*, first on the tab): Model audit, Coordination, Production (mass
  production: data, sheets, exports), Submission, All tools. A mode shows the panels for the task in full, dims the less
  relevant ones (faded icons, still usable) and hides unrelated panels; the Companion shows the mode's prompts first;
  Claude leads with the mode's tools (`working_mode`, and `workingMode` in `revit_status`). Remembered per user.
- **Model check after Autodesk's Model Checker** (BIM Interoperability Tools): 19 new checks in its sections: file and
  project (file size, starting view), worksets (elements on Workset1), levels, grids and links (not pinned), warnings
  (duplicate and overlapping elements), model content (linked CAD in 3D, raster images, groups used once, unused family
  types, design options, generic models), views (unused view templates), annotation (detail lines, overridden
  dimensions, line styles, text types) and naming ('Copy' views and types). Results are Pass / Review / Action needed.
  **Office check set** (`checkset.example.json`): switch checks off, set review and action thresholds and score limits,
  per office or project (`%APPDATA%\ACE-RevitMCP\checkset.json` or `"checkSet"` in config.json for a team share).
- Dashboard: Model check summary (pass / review / action, by section), results grouped by section with their rule, a
  Coordination card with the latest clash results, and clash detection shown as live in the tools status.
- **Clash Browser and ACE Clash View** (ACE tab → Coordination → *Clash Browser*; after Navisworks Clash Detective and
  ACC Model Coordination): choose the model to compare with, run the test against that model only, browse the clashes
  grouped by issue (filter by status, search), Previous / Next, Approve, Mark active, Select in Revit. Colours follow
  the role: the primary model (this model by default) in green, the secondary (the chosen link) in red; compared
  with all links, each link has its own colour from the brand palette, and any model's colour can be chosen in the
  legend (remembered). Other links hidden. For BIM managers the primary can be a link (link vs link; this model is then ghosted). Click a
  clash: everything else is dimmed, only the two elements are drawn in their colours, cut to a box around the clash (a
  large slab shows just the area around the pipe), the intersection in gold, with a section box and zoom on where they
  meet. Linked and open-model elements are drawn with Revit's temporary 3D graphics (DirectContext3D); only the ACE Clash
  View's own settings change. Claude: `clash_view`, `focus_clash`, `reset_clash_view`, `clash_results`, and
  `run_clash_test` with `with_model`.
- **Coordination report** (ACE tab → Coordination → *Coordination Report*, or `coordination_report`): the meeting-ready
  report of the clash issues. A 3D picture per issue (section box around it, this model's elements in ACE Red), the
  responsible discipline and reason, the cause (what changed and who), level and location, blank Action / Owner / Due
  lines, a summary of actions by discipline, and an Excel-ready CSV of all issues. Pictures come from temporary views
  that are rolled back; the model is not changed.
- **Bevelled ribbon icons**: ACE Rich Black tiles with seamless bevelled edges lit from the top left and a soft shadow below (like PowerPoint's
  bevel), an embossed glyph and a shaded red dot. Live tools are black, planned tools white (`"iconMix": "none"` for all
  black). `brand.json`: `"iconStyle": "bevel" | "3d" | "flat"`, `"iconColor"`, `"iconMix"`.
- **Official ACE brand values** (ACE Brand Guideline and Brand Card): UAE Flag Red `#EF3340` (was `#C8102E`), Charcoal
  Black `#212121`, Grey `#414042`, Platinum `#E6E6E6`, Quick Silver `#A0A0A0`; Poppins for text and BW Gradual for report
  headlines (Arial where not installed); company name "Al Ain Consulting Engineers". All other greys replaced by brand greys.
- **What caused each new clash**: the clash test now uses the change tracker's snapshots. A new clash says which side
  was added, moved or retyped since the snapshot before the last run, in which model, and by whom (workshared). This is
  kept separate from the responsible discipline (who should give way). Shown in the report, in Revit ("New from changes
  by ...", selectable) and to Claude (`cause`, `causedBy`).
- **Clash issues**: clashes are grouped into issues (one element that has to move and everything it hits, e.g. one duct
  through 8 beams), shown first in the report, in Revit (select an issue's elements) and to Claude (`topIssues`).
- Clash fixes: a pair of elements is one clash whichever side found it (MEP vs MEP counted pairs twice); connected
  MEP parts and hosted elements are no longer clashes; a run on one level (or one cut short by `max_elements`) no
  longer marks the other clashes resolved; a resolved clash that comes back is new again and marked reopened; named
  disciplines such as "MEP (plumbing / fire)" are no longer replaced by the model's discipline.
- `tools/clash-test`: the clash logic (responsibility, status between runs, issues) is tested without Revit.
- **`pending_changes`**: Claude reads the Apply cards waiting in the Companion panel instead of guessing (in the demo it
  wrongly said only the last option was left). Every previewed option keeps its own card. Two lessons from the demo
  added to the built-in lessons, and two benchmark cases (panel cards, `open_view` on room 301).
- **Model brief** (`get_model_brief`): what Claude reads before any task. The building, levels with room types, room
  types with counts, areas, levels, numbers and whether furnished, the families available per purpose with their real
  footprint, insertion point and facing, naming conventions, and the **linked and other open models** (ARC / STR / MEP
  guess) with a level, grid and coordinate **alignment check** against this model.
- **`describe_family`**: footprint, insertion point, placement type, host, type dimensions, nested families, sample facing.
- **Lessons** (`remember_lesson`, `recall_lessons`, `forget_lesson`): Claude saves facts, techniques, mistakes and
  preferences (model, project or ACE-wide; personal or shared with the team) and gets them back in every brief.
  Reviewed lessons ship with the package (`mcp-server/lessons/built-in.jsonl`), seeded with what the Snowdon tests taught.
- **Change tracker** (ACE tab → Insights → *Change Tracker*, or `model_changes`): compares the model **and its linked
  models** with an earlier snapshot: added, deleted, moved (distance, rotation, level), retyped and changed elements
  (Mark, Comments, workset, phase), by category and, in workshared models, by person. Snapshots are taken automatically
  when a model is opened (once a day) and after saves or syncs (at most every 2 hours), or by name with `snapshot_model`
  (e.g. "Stage 3 issue"). Report in Revit (select added / moved / changed elements) and as HTML.
- **Clash detection with responsibility** (ACE tab → Coordination → *Run Clash Test* / *Clash Results*, or
  `run_clash_test`): across this model, its links and the other open models (ARC / STR / MEP by model). Standard tests
  STR vs MEP, ARC vs STR, MEP vs ARC, MEP vs MEP, or custom categories; Revit's solid intersection after a box pre-filter,
  with a tolerance (touching or joined elements are not clashes) and optional clearance. Each clash gets the
  **responsible discipline** from ACE priority rules (the element that is easier to move gives way; override in
  `clash-rules.json`). Status kept between runs (new, active, resolved, approved; `set_clash_status`). HTML report and a
  Revit window grouped by responsible discipline and level.
- **Structure from ARC** (`derive_structure_from_arc` script): in the STR model, creates structural columns and walls from
  the ARC model's columns and load-bearing walls (sizes matched or new types made, levels mapped by name or height,
  existing ones skipped); `check_only` lists what is missing on each side.
- `coordination_sources`: which models take part, with their disciplines.
- `open_view`: opens a view and zooms to a room or elements directly (reliable even when Revit is not the focused window; found in the management demo), so users watch changes appear.
- `tools/script-check`: compile-checks every script against the Revit 2025 API without Revit.
- **Learning report** (`learning_report`): first-time-right rate of code tasks and its trend, repeated API mistakes with
  hints, code written again and again (recipe candidates), failing scripts, slow calls, lessons learned, and
  recommendations; shareable with the team. `node lib/learning.js` for maintainers.
- **Benchmark** (`tools/bench`): fixed tasks with expected results on the Snowdon sample (brief, family, test fits,
  preview pictures, QA scripts, dashboard, change tracker), run through the bridge before each release.
- CLAUDE.md: the learning loop (collect, review, improve, prove, release).
- The dashboard window is now a shared ACE report window (also used by the change tracker).
- Instructions: brief first; keep learning as part of every task; change tracking and linked models.

## 1.6.0
- **Preview pictures:** a preview (`dry_run`) with `preview_image: true` also returns a plan and a 3D picture of the
  area that would change, with the changed elements in ACE Red. They are taken inside the dry run on temporary
  views that disappear with the rollback, so the model is untouched. Claude shows them before asking; the
  **Apply card in the ACE panel shows them too**.
- New built-in script **`test_fit_out`**: an office test fit. 4-desk pods (desk + chair per person) on a regular
  grid, clear of walls, doors, columns and fixtures, for a target m2 per person; reports seats, m2 per person and
  the room's maximum; re-running replaces the previous test fit in the room. About 4 s for a 225 m2 office.
- Claude's instructions cover both (space planning, visual previews). Saved-script cards in the ACE panel now show the
  key inputs in their title (e.g. "test_fit_out (room number 301, m2 per person 8)"), so several options can be told apart.

## 1.5.0
- **Insights dashboard** (ACE tab → Insights → Dashboard, or ask Claude: new `model_dashboard` tool). Read-only:
  - a **health score (0–100)** with a trend per model, from 15 audit checks: warnings, imported CAD, in-place families,
    unloaded links, unplaced / not enclosed rooms, rooms without doors, narrow doors, duplicate marks, views not on
    sheets, views without templates, empty sheets, key parameters, sheet title block data, project information;
  - key figures, most frequent warnings, parameter completeness, statistics by level, submission readiness,
    Claude's activity over 7 days, and the **status of every ACE tool** (live, or planned with its phase: clash
    detection and code compliance show "no results yet");
  - shown **inside Revit** (with Refresh, *Select in Revit* for each finding, Open in browser) and saved as a
    self-contained ACE-branded **HTML report** in `Documents\ACE Insights\<model>\`, ready to share.

## 1.4.0
- **The ACE ribbon shows the roadmap:** panels Audit, Inspect, Data, Export, Coordination, Deliver, Compliance,
  Team Tools and Admin, plus *Undo Claude Change* next to Companion: 25 planned tools as real buttons. Each opens an
  ACE-branded card: what it will do, how it will work, its phase, and (where possible) what Claude can already do today,
  with a prompt to copy. Planned buttons carry a hollow red ring; live ones a solid dot.
- New **Roadmap** button: one window with what is live today and every planned tool by phase (for presentations).
- Companion **Context & prompts:** 21 ready-made prompts in 5 groups (selection, this view, model checks, common
  tasks, session and support). Prompts that need a selection no longer turn unreadable in the dark theme.

## 1.3.3
- Companion header logo: sharper and slightly larger (high-quality scaling, 44 px high, about 84 px wide), still on its
  white field with 25% clear space. `branding/README.md` now says to crop the lettermark from the largest official
  logo file available (the small drawing-embedded PNG renders soft).

## 1.3.2
- **ACE brand applied** (per ACE brand guidelines): black / white / greys with ACE Red `#C8102E` as a thin accent and for
  the main action only; Arial; no emoji-style symbols; the header follows the ACE signature layout (the ACE lettermark on
  a white logo field, a thin red divider, "Companion" and "ACE | AlAin Consulting Engineers").
- The logo is never recoloured and is shown at 72 px wide or more; ribbon icons stay simple black/white glyphs with a small
  red accent (the logo is too small to use at 16/32 px). A reversed (white) logo is TBC with ACE Marketing.
- New brand.json fields: `fullName`, `greyDark`, `greyLight`, `font`. Logo files and the guidelines stay out of the public repo.

## 1.3.1
- **Branding:** a `branding/` folder (`brand.json` + optional logo) sets the company name, primary and accent colours,
  and logo for the Revit ribbon and the Companion panel. The installer copies it to every PC. Neutral defaults until
  the official ACE logo and colours are added.
- Ribbon buttons now have icons (drawn as vectors in the brand colour, or the company logo if `useLogoOnRibbon`).

## 1.3.0
- **ACE Companion panel** inside Revit (ACE tab → *Companion*), docked on the right, working alongside Claude Desktop
  with no AI of its own and no extra cost:
  - **Approvals:** every change Claude previews appears as a card with **Apply / Cancel**. Apply makes the change as one
    undo step and writes it to the journal. If Claude later tries to apply the same change, the add-in answers
    "already applied" (or "cancelled"), so nothing is ever applied twice.
  - **Activity:** a live, plain-language feed of what Claude reads, previews and changes.
  - **Results:** elements from Claude's latest answer; click to select and zoom, or *Select all*.
  - **Context & prompts:** the current model, view and selection, *Copy this context for Claude*, and ready-made prompts
    (explain my selection, check its parameters, model QA, what changed today, undo, report a problem).
  - Follows Revit's light or dark theme.
- Claude's instructions explain the panel; the MCP server handles the panel's "already applied" and "cancelled" answers.

## 1.2.2
- ACE is now registered for **Claude Code sessions** too (`~/.claude.json`, used by the desktop app's Code tab and the
  CLI), not only Claude Desktop chats. Found on a work PC where a Code session couldn't see `ace-revit`.
- New `lib/register.js` does all registration (add / remove / status) with exact-key JSON edits, backups and atomic
  writes; the installer asks you to quit Claude Desktop first so the running app can't overwrite the entry.
- Doctor and `check_setup` check every Claude config (Desktop, Store app, Claude Code).

## 1.2.1
- Installer: finds a `dotnet` that actually has an SDK 8+ (fixes "SDK still not available" right after winget installed it),
  and builds without the .NET welcome banner or the ASP.NET dev-certificate creation. New `get-ace.ps1` one-line installer.
- `door_width_check` now reads door families that store width in a plain "Width" family parameter
  (found in real-model testing: 132 of 142 Snowdon Towers doors readable, up from 8), and treats zero widths
  (curtain wall panel doors) as unreadable instead of reporting them as 0 mm doors.
- The add-in returns a clear message when `code` or `command` arrives with the wrong JSON type.

## 1.2.0
- **Smarter Claude:** `revit_api_lookup` reads the real Revit API on the PC (signatures, overloads, enums,
  obsolete flags); `describe_category` shows which parameters a category really has (instance/type,
  % filled, samples); `list_types` browses loaded families and types; `revit_guide` gives 9 expert guides
  (verified against the Revit 2025 API); rewritten working instructions (think → plan → preview → confirm
  → apply → verify); three prompts (*Do a Revit task*, *Model QA check*, *Report a problem*).
- New built-in QA scripts: `parameter_completeness`, `rooms_without_doors`, `door_width_check`.
- Script timing (`scriptMs`) in every run.
- **Team use:** a prebuilt release package (`build-package.ps1`, GitHub Actions workflow) that needs no .NET SDK
  or npm on team PCs; `team.json` for a shared script library and a shared report folder; scripts can be saved
  with `scope: "team"`; `install.ps1 -Silent` for IT deployment; a repair copy of the package; Start-menu shortcuts.
- **Support:** `doctor.cmd` checks every component and repairs problems with `-Fix`; `check_setup` does the same from
  inside Claude; `report_issue` / `report.cmd` write Markdown issue, improvement and health reports (environment,
  checks, usage stats, failures with code, add-in log, recommendations); a local call log feeds the analysis.
- Docs: USER-GUIDE, AGENT-GUIDE, REQUIREMENTS, TEAM-DEPLOYMENT, CLAUDE.md (maintainers).

## 1.1.0
- Enforced safety protocol: preview-before-apply gate, risky-code screening, activity journal; change summaries
  (added/modified/deleted); read-only runs always rolled back; `backup_model`; safe `undo_last_claude_change`.

## 1.0.0
- First version: Revit 2025 add-in with a localhost bridge, C# code runner, core model tools, script library, installer.
