# Requirements

## Every PC that uses the tool

| Software | Version | How it gets installed |
|---|---|---|
| Windows | 10 or 11, 64-bit | - |
| Autodesk Revit | **2025** (any update) | Your usual Autodesk install. Signing in to an Autodesk account is fine and not needed by ACE. |
| Claude Desktop | current | [claude.ai/download](https://claude.ai/download). Sign in and open it once before installing ACE. |
| Node.js | 18 or newer (LTS recommended) | Installed automatically by `install.cmd` via `winget` if missing, or install from [nodejs.org](https://nodejs.org). |
| winget (App Installer) | any | Built into Windows 10/11. Only needed if Node.js must be installed. |
| Claude Code CLI | optional | Only for people who use Claude Code in a terminal. Registered automatically if present. |

No admin rights are needed: everything installs per user (`%APPDATA%`, `%LOCALAPPDATA%`).
No internet access is needed during install when using the release package (dependencies are bundled),
except to download Node.js if it's missing.

Network: the add-in listens on **localhost only** (port 48884 by default, token-protected). There
are no firewall prompts and nothing is reachable from other machines.

## Only the machine that BUILDS the package (maintainers)

| Software | Why |
|---|---|
| .NET SDK 8 or newer | Compiles the Revit add-in (`build-package.ps1`) |
| Node.js 18+ with npm | Bundles the MCP server's dependencies |
| Git | To get the source |

Installing directly from a source checkout (instead of the package) also builds on that PC, so it
needs the .NET SDK there too (the installer adds it with winget).

## What gets installed where

| What | Where |
|---|---|
| Revit add-in | `%APPDATA%\Autodesk\Revit\Addins\2025\AceRevitMcp\` + `AceRevitMcp.addin` |
| MCP server | `%LOCALAPPDATA%\ACE-RevitMCP\mcp-server\` |
| Repair copy of the package | `%LOCALAPPDATA%\ACE-RevitMCP\package\` |
| Settings, logs, journal, reports, personal scripts, backups | `%APPDATA%\ACE-RevitMCP\` |
| Claude Desktop registration | `"ace-revit"` in `%APPDATA%\Claude\claude_desktop_config.json` (and the Store-app copy) |
| Start menu | *ACE Revit MCP* → Check and fix · Report a problem · User guide |
