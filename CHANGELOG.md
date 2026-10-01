# Changelog

## 1.7.0 (core v2)
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
