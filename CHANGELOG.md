# Changelog

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
