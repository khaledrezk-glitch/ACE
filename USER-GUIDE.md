# ACE Revit MCP: User Guide

Ask Claude to do work in Revit 2025 in plain language, and it does the job safely in your open
model. This guide is for everyone on the team. No programming knowledge needed.

---

## 1. Install (once per PC, about 5 minutes)

1. **Close Revit.**
2. Unzip the package `ACE-RevitMCP-<version>.zip` anywhere, e.g. `Downloads`.
3. Double-click **`install.cmd`** and wait for **Done!** (It installs Node.js automatically if it's missing.)
4. Start **Revit 2025**. When it asks about the add-in **ACE Revit MCP**, click **Always Load**.
5. **Fully quit Claude Desktop** (right-click its icon by the clock, then **Quit**) and open it again.

Check it works: in a new Claude chat, type
> Using ace-revit, check the Revit connection and give me an overview of the open model.

Requirements are listed in [REQUIREMENTS.md](REQUIREMENTS.md). You can delete the unzipped folder
afterwards: a copy is kept for repairs.

## 2. How to ask

Just describe the outcome you want, as you would to a colleague:

| Kind of request | Example |
|---|---|
| Understand the model | "What's in this model? Any obvious problems?" |
| Check and QA | "Which doors are narrower than 900 mm? List them by level." |
| | "Which rooms have no door?" / "How complete is the Fire Rating parameter on doors?" |
| Bulk edits | "Put 'Checked' in the Comments of every door on Level 1." |
| | "Renumber the rooms on Level 2 in reading order starting at 201." |
| Create things | "Make a floor plan and a sheet for every level with our A1 title block." |
| | "Create a grid: 5 bays of 7.2 m by 3 bays of 6 m." |
| Complex in concept | "For every apartment, check the bedroom is at least 9 m² and the living room has a window; give me a table of failures." |
| See it | "Show me the plan with those doors highlighted." |
| Across disciplines | "Check every plumbing fixture in the Plumbing model sits inside a room in the Architectural model." |

Tips:
- Say **which level / view / category** when it matters. Claude asks if something is unclear.
- Select elements in Revit and say "**these**" or "**the selected ones**".
- Use the **+ / prompts** menu in Claude: *Do a Revit task*, *Model QA check*, *Report a problem*.
- Ask Claude to **save** a task you'll repeat ("save this as a team script called renumber_rooms_by_level").

## 3. What Claude does before changing anything (your safety)

Every change goes through five steps. Two of the protections are enforced by the software itself,
not just requested of Claude:

1. **Explains the plan** in plain words: what, how, what will and won't change, and how to undo it.
2. **Previews**: it runs the change and rolls it back, then reports "would modify 142 doors, delete 0". *(Enforced: a real change is refused unless that exact change was previewed first.)*
3. **Asks you**: "Shall I apply this?" Nothing happens until you say yes.
4. **Applies** it as **one undo step** named "Claude: …". Press **Ctrl+Z** or say "undo that".
5. **Verifies** the result and reports it, often with a picture.

Also:
- **Read-only checks can't change anything:** they're always rolled back.
- **Risky actions are blocked** unless you agree: files, other programs, internet, saving, closing or syncing models. *(Enforced.)*
- **"Undo that"** only works if Claude's change is still the latest one. It never undoes your own work.
- **Backups**: ask "back up the model first" before big changes.
- **Activity journal**: ask "what did you change today?". The journal is in `%APPDATA%\ACE-RevitMCP\journal`.

Good practice: work on a **copy** or a model you've saved recently, especially while learning.

## 4. When something goes wrong

| Symptom | What to do |
|---|---|
| Claude says Revit is not reachable | Make sure Revit is open with a model. **ACE tab → MCP Status → Restart connection.** |
| Requests time out | Revit only works when idle: close dialogs, press **Esc**, and wait for syncs to finish. |
| No **ACE** tab in Revit | Start menu → **ACE Revit MCP → Check and fix ACE Revit** (with Revit closed). |
| Claude doesn't have the Revit tools | Fully quit and reopen Claude Desktop. Still missing? Run **Check and fix**. |
| Anything else | Start menu → **Check and fix ACE Revit**. It checks everything and repairs what it can. |

### Reporting a problem (so the tool gets better)

Pick one:
- In Claude: *"Report this problem to the ACE tool maintainers."* (or the **Report a problem** prompt), or
- Start menu → **ACE Revit MCP → Report a problem**, then describe it in one sentence.

A Markdown report is created in `%APPDATA%\ACE-RevitMCP\reports` (and copied to the team folder if
one is set up). It contains health checks, versions, recent activity and failures, logs and
recommendations. **Send that `.md` file** to the ACE tool maintainers. It contains element ids and
code, not your drawings.

Ideas for new capabilities are welcome too: *"Create an improvement report: I often need X."*

## 5. Updating and removing

- **Update:** unzip the new package, close Revit, run `install.cmd`. Your settings and scripts are kept.
- **Remove:** run `uninstall.ps1` from the package folder (`-Purge` also deletes settings, scripts and journals).
