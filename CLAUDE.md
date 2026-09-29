# ACE Revit MCP: notes for Claude Code (maintainers)

## Layout
- `revit-addin/AceRevitMcp`: Revit 2025 add-in (.NET 8). `Bridge/` is the localhost HTTP + ExternalEvent
  queue, `Commands/` holds native commands (registered in `CommandRegistry.cs`), `Scripting/` is the
  C# code runner (TransactionGroup wrapping, dry runs, change tracking), and `Util/` has JSON, ModelGuard
  (warnings/dialogs) and ChangeTracker (DocumentChanged, safe undo).
- `revit-addin/AceRevitMcp.Compiler`: Roslyn, loaded in an isolated AssemblyLoadContext. Only primitives cross the boundary.
- `mcp-server/index.js`: MCP tools and prompts. `lib/`: `core` (config/bridge), `safety` (preview gate,
  risky-code screen, journal), `scripts` (built-in < team < user library), `telemetry` (local call log),
  `diagnostics` (checks, recommendations, reports; also a CLI).
- `mcp-server/instructions.md`: what Claude is told at connection time. `guides/*.md`: expert knowledge (`revit_guide`).
- `install.ps1`, `doctor.ps1`, `build-package.ps1`: Windows PowerShell 5.1 compatible (no `??`, no ternaries).

## Checks before pushing
```bash
dotnet build revit-addin/AceRevitMcp/AceRevitMcp.csproj -c Release       # must be warning-free
dotnet build revit-addin/AceRevitMcp.Compiler/AceRevitMcp.Compiler.csproj -c Release
dotnet run --project tools/tracker-test                                   # change-tracker comparison (after the add-in build)
cd mcp-server && npm ci && npm run check                                   # syntax + smoke test (fake Revit bridge)
pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('install.ps1',[ref]$null,[ref]$e); $e"
```
Scripts in `mcp-server/scripts/*.cs` and any API usage in `guides/*.md` must compile against the Revit 2025 reference
assemblies (NuGet `Nice3point.Revit.Api.RevitAPI` 2025.0.2), exactly as the add-in wraps them:
```bash
dotnet publish revit-addin/AceRevitMcp.Compiler -c Release -o tools/script-check/compiler   # once
dotnet run --project tools/script-check -- mcp-server/scripts                                # every script must PASS
```
With Revit open (on a Windows PC), also run the benchmark: `node tools/bench/run.mjs`.

## Invariants (don't break)
- A real (non-dry-run) modification requires an identical, successful dry run in the same session (`lib/safety.js`).
- `readonly` runs are always rolled back. Every applied run is exactly ONE undo step.
- Risky code (files, processes, network, save/close/sync) needs `allow_risky` after user consent.
- The MCP server registers as `ace-revit` and never touches other servers in the Claude config
  (PowerShell property names are case-insensitive: never add a key that could equal an existing one).
- Keep Add-in and MCP server versions in step (`AceRevitMcp.csproj` `<Version>` = `mcp-server/package.json` version);
  add a line to `CHANGELOG.md` for each release.

## The learning loop (keep it running with every change)
ACE is meant to get better continuously. Every release should pass through this loop:
1. **Collect:** team members run `learning_report` (ask Claude, `share_with_team: true`) and share `issue-*.md` reports.
   Claude saves lessons as it works (`remember_lesson`, team-shared ones land in the team `lessons` folder).
2. **Review:** read the learning reports (first-time-right trend, repeated compile errors, recipe candidates, failing
   scripts, slow calls) and the team lessons.
3. **Improve:** fix guides/instructions for repeated API mistakes, add ScriptContext helpers, turn recipe candidates into
   tested built-in scripts, and **promote good team lessons** into `mcp-server/lessons/built-in.jsonl` (reviewed wording,
   stable `id`). Wrong or outdated built-in lessons are removed there.
4. **Prove:** add or extend a case in `tools/bench/cases.json` for every fix or new capability, then run the benchmark with
   the Snowdon sample open: `node tools/bench/run.mjs` (all cases must pass; history in `logs/bench-history.jsonl`).
5. **Release:** bump the version, CHANGELOG, `build-package.ps1`.

## Working on a user report (`issue-*.md` / `improvement-*.md` from report_issue or doctor)
1. Read "What happened", **Health checks** and **Recommendations** first. Setup problems (❌ rows) usually
   mean an installer or doctor improvement, not a code bug.
2. Use the **Appendix** (failing calls with error, stage, compile messages and the exact C# code) to
   reproduce. Compile failures from API misuse → fix `guides/*.md` / `instructions.md` so Claude stops
   making that mistake (and consider a `ScriptContext` helper). Runtime failures in saved scripts → fix
   the script and compile-check it.
3. Usage table: frequent `execute_revit_code` patterns → candidates for built-in scripts or native commands;
   slow calls → performance.
4. Fix, add or extend a smoke-test assertion, run the checks above, bump the version, update CHANGELOG, commit.
5. Reply with a short summary the user can understand: what was wrong, what changed, and whether they need
   to update (build a new package with `build-package.ps1`).
