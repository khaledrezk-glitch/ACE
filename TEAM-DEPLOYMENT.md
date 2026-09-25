# Deploying ACE Revit MCP to the team

## 1. Build the package (maintainer, once per version)

On a PC with the .NET 8+ SDK and Node.js:
```powershell
git clone https://github.com/khaledrezk-glitch/ace.git
cd ace
copy team.example.json team.json      # optional: edit team settings (below)
powershell -ExecutionPolicy Bypass -File build-package.ps1
```
The result is `dist\ACE-RevitMCP-<version>.zip` (about 8 MB). It includes the prebuilt add-in and
bundled dependencies, so team PCs need no SDK and no npm access. Alternatively, run the GitHub
Actions workflow **Build team package** (Actions tab → Run workflow) and download the artifact. Pushing
a tag like `v1.2.0` attaches the zip to a GitHub release.

## 2. Team settings (`team.json`, optional but recommended)

```json
{
  "teamName": "ACE",
  "teamScriptsDir": "%USERPROFILE%\\OneDrive - ACE\\Revit MCP\\scripts",
  "teamReportsDir": "%USERPROFILE%\\OneDrive - ACE\\Revit MCP\\reports"
}
```
- **teamScriptsDir**: a shared folder (OneDrive / SharePoint synced folder, or a network share like
  `\\\\server\\bim\\ace-mcp\\scripts`). Scripts saved with scope "team" land here and appear for everyone.
- **teamReportsDir**: issue and improvement reports are copied here automatically, so maintainers see them
  without anyone emailing files.
- Environment variables (`%USERPROFILE%`, ...) are expanded on each PC.

## 3. Install on each PC

- **Interactive:** unzip, then double-click `install.cmd` (Revit closed).
- **Silent / IT (per user, no prompts):**
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File "\\server\share\ACE-RevitMCP-1.2.0\install.ps1" -Silent
  ```
  Run it in the user's context (it installs to their profile). If Revit is open on an upgrade it
  stops with a message instead of waiting.
- Then each user: start Revit → **Always Load** → fully quit and reopen Claude Desktop.
- Verify: Start menu → **ACE Revit MCP → Check and fix ACE Revit** (or `doctor.cmd`).

## 4. Updating

Build the new package, then run its `install.cmd` / `install.ps1 -Silent` on each PC (Revit closed).
Settings, the token, personal scripts, journals and reports are preserved. The doctor warns when the
add-in and the MCP server versions differ.

## 5. Support loop: reports → improvements

1. A user hits a problem, then asks Claude to *"report this problem"*, or uses Start menu → *Report a problem*.
2. The `.md` report lands in `%APPDATA%\ACE-RevitMCP\reports` and in `teamReportsDir`.
3. A maintainer opens Claude Code in this repository and says:
   *"Work on this ACE Revit MCP report and improve the tool: <path to report>"*.
   `CLAUDE.md` tells Claude Code how to triage it, reproduce it, fix it, add tests, and bump the version.
4. Build a new package and roll it out.

Improvement reports (`kind: improvement`) work the same way for feature ideas. Reports include usage
statistics, so the most-used and most-failing workflows are easy to spot.
