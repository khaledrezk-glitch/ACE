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

## Install (once per PC)

Requirements: Windows 10/11, Revit 2025, and Claude Desktop (or Claude Code). The installer adds the
.NET 8 SDK and Node.js with `winget` if they're missing. No admin rights are needed for the add-in.

1. Close Revit.
2. Download this repository (green **Code** button > *Download ZIP*, then extract; or `git clone`).
3. Double-click **`install.cmd`**.
4. Start Revit 2025. When it asks about the add-in **ACE Revit MCP**, click **Always Load**.
5. Fully quit Claude Desktop (tray icon > Quit) and reopen it. The Revit tools appear under the 🔨 / tools icon.

Check the connection with the **ACE** ribbon tab > **MCP Status**, or run
`powershell -ExecutionPolicy Bypass -File test-connection.ps1`.

To update, pull or download the new version and run `install.cmd` again. To remove everything, run `uninstall.ps1`
(add `-Purge` to also delete your config and saved scripts).

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

| Tool | Purpose |
|---|---|
| `revit_status` | Check the connection |
| `get_model_overview` | Levels, units, project info, active view, element counts per category |
| `get_selection` | What you've selected in Revit |
| `find_elements` | Search by category, level, view, name, or parameter conditions |
| `get_element_details` | All parameters, location, host, room for specific elements |
| `set_parameters` | Bulk parameter edits in one transaction |
| `select_elements` | Select and zoom so you can see what Claude means |
| `list_views`, `view_image` | List views, and export a view as an image Claude can look at |
| **`execute_revit_code`** | Run C# against the full Revit API (preview, compile check, auto/manual/readonly transactions) |
| `backup_model`, `undo_last_claude_change`, `get_activity_log` | Backup, safe undo, and "what did you do?" |
| `list_saved_scripts`, `read_saved_script`, `run_saved_script`, `save_script` | Reusable script library |

Built-in scripts (`mcp-server/scripts`): `audit_model`, `renumber_rooms`, `tag_untagged`,
`grid_system`, `sheets_for_levels`, `copy_parameter`. Scripts you save go to
`%APPDATA%\ACE-RevitMCP\scripts`.

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

| Symptom | Fix |
|---|---|
| Claude says Revit is not reachable | Revit must be open with the add-in loaded. Check **ACE > MCP Status**, and restart the connection there. |
| Requests time out | Revit only runs commands when idle. Close open dialogs, press **Esc** to end any active command, and wait for the model to finish loading. |
| No **ACE** tab in Revit | Re-run `install.cmd` with Revit closed, and click **Always Load** when Revit asks. |
| Port 48884 is already in use | Edit `"port"` in `%APPDATA%\ACE-RevitMCP\config.json`, then restart Revit. |
| Tools don't appear in Claude Desktop | Fully quit and reopen Claude Desktop. Check `%APPDATA%\Claude\claude_desktop_config.json` has a `"revit"` entry. |
| Anything else | Read the log at `%APPDATA%\ACE-RevitMCP\logs\addin.log`. |

Manual registration, if needed. Claude Desktop config:

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Program Files\\nodejs\\node.exe",
      "args": ["C:\\Users\\<you>\\AppData\\Local\\ACE-RevitMCP\\mcp-server\\index.js"]
    }
  }
}
```

Claude Code: `claude mcp add revit --scope user -- node "%LOCALAPPDATA%\ACE-RevitMCP\mcp-server\index.js"`

## Repository layout

```
install.cmd / install.ps1     one-click installer (prereqs, build, register)
uninstall.ps1                 removes everything
test-connection.ps1           checks Revit is reachable
revit-addin/
  AceRevitMcp/                Revit 2025 add-in (.NET 8): bridge, commands, code runner
  AceRevitMcp.Compiler/       Roslyn C# compiler, loaded in isolation (no clashes with Dynamo etc.)
mcp-server/
  index.js                    MCP server + Claude's working instructions
  scripts/                    built-in script library
  test/smoke.js               end-to-end test against a fake Revit bridge
```

Development checks (no Revit needed): `dotnet build revit-addin/AceRevitMcp/AceRevitMcp.csproj` and
`cd mcp-server && npm install && npm run check`.
