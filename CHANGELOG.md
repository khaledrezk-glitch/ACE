# Changelog

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
