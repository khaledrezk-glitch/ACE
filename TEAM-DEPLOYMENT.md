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
bundled dependencies, so team PCs need no SDK and no npm access. Per Revit year, `bin\addin\<year>` holds the ACE
build (the payload) and `bin\addin\<year>\loader` the ACE loader, the only DLL named in the `.addin` file. Alternatively, run the GitHub
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

- **Interactive:** unzip, then double-click `install.cmd` (Revit may stay open).
- **Silent / IT (per user, no prompts):**
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File "\\server\share\ACE-RevitMCP-1.2.0\install.ps1" -Silent
  ```
  Run it in the user's context (it installs to their profile). Revit may be open: see Updating.
- Then each user: start Revit → **Always Load** → fully quit and reopen Claude Desktop.
- Verify: Start menu → **ACE Revit MCP → Check and fix ACE Revit** (or `doctor.cmd`).

## 4. Updating

Build the new package, then run its `install.cmd` / `install.ps1 -Silent` on each PC. Settings, the token, personal
scripts, journals and reports are preserved. The doctor warns when the add-in and the MCP server versions differ.

**No Revit restart (the ACE loader).** Each PC has, per Revit year, in `%APPDATA%\Autodesk\Revit\Addins\<year>\AceRevitMcp`:
- `AceRevitMcp.Loader.dll`: the ACE loader, named in `AceRevitMcp.addin`. It has its own version (1.0.0) and is
  replaced only when that version changes. Revit's unsigned-add-in prompt (or the signature check, when signing is on)
  concerns this file, so it does not come back with every update;
- `payload\<version>-<hash>\`: one folder per ACE build. An install adds a new folder next to the one Revit runs;
- `current.json`: `{ version, path, sha256, files, previous }`, written by the installer: the build to load (with the
  SHA-256 of every file in it) and the fallback.

While Revit runs, the installer asks it (`reload_addin` through the bridge) to switch to the new build when Revit is
idle; the loader checks every file's SHA-256 (and, with signing on, that the loader and every ACE DLL are signed by the
certificate pinned in `signing.json`; a damaged `signing.json` blocks the load). The hash catches corruption and swapped
files, not a malicious program running as the same user. The loader then
stops the old build, starts the new one, and starts the old one again if that fails. The loader loads only from its
own folder, never from a path sent over the bridge. With Revit closed, the installer also removes the older build
folders (it keeps the current and the previous one).

**Upgrading from an ACE without the loader:** the first install replaces the `.addin` file, so a running Revit needs
**one** restart (the installer says which). The old files are removed by the next install made while Revit is closed.
After that, updates need no restart; only a new loader version does (the installer says so).
`doctor.cmd` reports the loader and build versions and a pending reload.

## 5. Support loop: reports → improvements

1. A user hits a problem, then asks Claude to *"report this problem"*, or uses Start menu → *Report a problem*.
2. The `.md` report lands in `%APPDATA%\ACE-RevitMCP\reports` and in `teamReportsDir`.
3. A maintainer opens Claude Code in this repository and says:
   *"Work on this ACE Revit MCP report and improve the tool: <path to report>"*.
   `CLAUDE.md` tells Claude Code how to triage it, reproduce it, fix it, add tests, and bump the version.
4. Build a new package and roll it out.

Improvement reports (`kind: improvement`) work the same way for feature ideas. Reports include usage
statistics, so the most-used and most-failing workflows are easy to spot.
