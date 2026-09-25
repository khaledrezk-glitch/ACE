# ACE: Claude ↔ Revit 2025 (MCP)

Tell Claude what you want in plain language ("please do this for me in Revit") and it does the whole
job in your open Revit 2025 model: it looks at the model, plans the steps, writes and runs Revit API
code, checks the result, and reports back.

```
Claude Desktop / Claude Code
        │  MCP (stdio)
        ▼
ACE MCP server (Node.js)                    %LOCALAPPDATA%\ACE-RevitMCP\mcp-server
        │  HTTP on localhost only + secret token
        ▼
ACE add-in inside Revit 2025                %APPDATA%\Autodesk\Revit\Addins\2025\AceRevitMcp
        │  runs on Revit's main thread via ExternalEvent
        ▼
Your model
```

## Why it handles complex requests

Most Revit MCP tools only offer a fixed menu ("create wall", "list doors"). ACE has those basics, and
its main tool, **`execute_revit_code`**, lets Claude write C# against the **full Revit API** and run
it directly in Revit. One request can cover a whole workflow: analyse geometry, cross-check rules,
update thousands of elements, build views and sheets.

It is built so you can trust it with a live model. Claude **explains, previews, asks, then applies**,
and the most important rules are enforced by the software itself, not just requested of Claude:

| Safeguard | What it means for you |
|---|---|
| **Plan in plain words first** | Before changing anything, Claude explains what it understood, the steps it will take, and what will and won't change. |
| **Enforced preview** | A change runs completely and is then rolled back, and Claude reports "would add 12 grids, modify 3 views, delete 0". **The server refuses any real change that wasn't previewed first with exactly the same code and inputs.** |
| **Your confirmation** | Claude shows you the preview and waits for your "yes" before applying it. |
| **One Undo step** | Each applied change is a single named step ("Claude: renumber rooms"). Undo it with **Ctrl+Z**, or ask Claude to undo it. |
| **Safe undo** | `undo_last_claude_change` refuses if you changed anything afterwards, so it never undoes your own work. |
| **All-or-nothing** | If anything fails partway, every change from that run is rolled back. |
| **Backups** | Claude can copy your .rvt file to a timestamped backup before big changes (`backup_model`). |
| **Code screening** | Code that would touch files on disk, run other programs, use the network, or save, close or sync models is blocked unless you explicitly allow it. |
| **Activity journal** | Every preview and change is logged with a plain-language description, the result, and the exact code, in `%APPDATA%\ACE-RevitMCP\journal`. Ask Claude "what did you change today?" |
| **No blocked Revit** | Warnings are dismissed and reported back to Claude, and errors roll back instead of leaving a dialog open. |
| **Local only** | The add-in listens on `localhost` only and needs a secret token, so other machines can't reach it. |

Claude also gets a built-in guide (the MCP server instructions) that tells it how to work: understand
the model, plan, dry-run, run, verify (it can even **look at views as images**), then report.

## Install

- **Team members:** unzip the release package `ACE-RevitMCP-<version>.zip`, then double-click **`install.cmd`**
  (Revit closed). Then follow **[USER-GUIDE.md](USER-GUIDE.md)**.
- **Maintainers and IT:** build the package and roll it out with **[TEAM-DEPLOYMENT.md](TEAM-DEPLOYMENT.md)**.
  Requirements are in **[REQUIREMENTS.md](REQUIREMENTS.md)**.
- **Tune your Claude:** **[AGENT-GUIDE.md](AGENT-GUIDE.md)**.
- **What's next:** [ROADMAP.md](ROADMAP.md) (usage control, auditing, add-in equivalents, clash detection, submissions, code checks).
- **Problems:** Start menu → *ACE Revit MCP* → **Check and fix** (`doctor.cmd -Fix`) or **Report a problem** (`report.cmd`).

## Using it

**First time? Follow [FIRST-TEST.md](FIRST-TEST.md). It's a 10-minute guided test on a scratch copy of a model.**

Open a model in Revit, then just ask Claude. Examples:

- "Give me an overview of this model and tell me what looks wrong." (runs the `audit_model` script)
- "Renumber all rooms on Level 2 in reading order, starting at 201."
- "Create a structural grid: 4 bays of 7.2 m in X, 3 bays of 6 m in Y, starting at the origin."
- "Make a floor plan and a sheet for every level using our A1 title block, numbered A-101 onwards."
- "Find every door narrower than 900 mm, list them by level, and put 'CHECK WIDTH' in their Comments."
- "Copy each room's Name into its Comments parameter, but only where Comments is empty."
- "Tag every untagged window in the active view."
- "For every wall on Level 1 longer than 10 m that isn't a curtain wall, report the type and length, then show me a picture of the plan with them selected."
- "Every room without a floor finish: set Floor Finish to 'Screed' and give me a count per level."

For big edits, tell Claude to show you the dry-run result first ("show me what you'd change before
doing it"). It does that by default for anything large.

### Tools Claude gets

| Area | Tools |
|---|---|
| Understand | `revit_status`, `get_model_overview`, `get_selection`, `find_elements`, `get_element_details`, `describe_category`, `list_types`, `list_views`, `view_image` |
| Knowledge | `revit_api_lookup` (the real Revit API on the PC), `revit_guide` (9 expert guides) |
| Change (gated by preview + confirmation) | **`execute_revit_code`**, `set_parameters`, `run_saved_script`, `select_elements` |
| Safety | `backup_model`, `undo_last_claude_change`, `get_activity_log` |
| Library | `list_saved_scripts`, `read_saved_script`, `save_script` (personal or team) |
| Support | `check_setup`, `report_issue` |

Built-in scripts: `audit_model`, `parameter_completeness`, `rooms_without_doors`, `door_width_check`,
`renumber_rooms`, `tag_untagged`, `grid_system`, `sheets_for_levels`, `copy_parameter`.

### Compared with other Revit MCP servers

Before settling on this design I reviewed the published Revit MCP servers on npm: `mcp-server-for-revit`,
`revit-mcp`, `revit-mcp-server` (179 tools), `@shuotao/revit-mcp-server`, `@kimminsub/revit-mcp`
and `@visionxt/revit-mcp`. Most offer long lists of fixed tools. ACE covers the same ground through
code execution and keeps a small set of reliable tools. It adopts their best ideas in a safer form:

- **Undo and checkpoints:** undo only when the latest change is really Claude's; backups of the .rvt file.
- **Compile-only code checks:** `compile_only: true`.
- **"Secure" code execution:** risky-code screening plus the enforced preview.
- **Audits and warnings:** the `audit_model` script.

## Troubleshooting

Run **`doctor.cmd`**: it checks every component and prints a fix for each problem. `doctor.cmd -Fix` repairs
automatically, and `report.cmd` writes an issue report for the maintainers. The full table is in
[USER-GUIDE.md](USER-GUIDE.md#4-when-something-goes-wrong).

## Repository layout

```
install.cmd / install.ps1     installer (prebuilt package or source; -Silent for IT; -SkipAddin)
doctor.cmd / doctor.ps1       check everything, -Fix repairs, -Report writes a Markdown report
report.cmd                    create an issue report for the maintainers
build-package.ps1             build the team release zip (prebuilt add-in + bundled dependencies)
uninstall.ps1                 removes everything (-Purge also removes settings, scripts and journals)
team.example.json             team settings template (shared scripts and reports folders)
revit-addin/                  Revit 2025 add-in (.NET 8) + isolated Roslyn compiler
mcp-server/                   MCP server: index.js, lib/, instructions.md, guides/, scripts/, test/
USER-GUIDE / AGENT-GUIDE / REQUIREMENTS / TEAM-DEPLOYMENT / FIRST-TEST / CHANGELOG / CLAUDE.md
```

Maintainer checks (no Revit needed) are in [CLAUDE.md](CLAUDE.md).
