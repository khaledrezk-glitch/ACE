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

**No package at hand?** Open PowerShell (Revit closed) and paste this one line. It downloads ACE from GitHub
and installs it, building the add-in on the PC, so it may ask to install the .NET SDK:
```powershell
irm https://raw.githubusercontent.com/khaledrezk-glitch/ACE/claude/brave-carson-vcjduc/get-ace.ps1 | iex
```

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

## 3. The ACE Companion panel (inside Revit)

Click **ACE tab → Companion** to show or hide it (it docks on the right). It works alongside Claude Desktop:

| Tab | What it's for |
|---|---|
| **Approvals** | When Claude previews a change, a card appears with: the title and **what Claude says it does and why**; for a long job, **the steps and which one this is** ("Step 2 of 4"); the counts; a red **"Look at this before applying"** box for anything deleted and for changes to parameters, types or families; **What exactly changes** (each category with element names); pictures when Claude made them; and **Show in model** to select the changed elements. Click **Apply** to make the change (one undo step), or **Cancel**. You can also just answer Claude in the chat. After Apply, tell Claude "done" and it verifies the result. |
| **Activity** | A live list of what Claude is doing in your model. |
| **Results** | Elements from Claude's latest answer. **Click one to select and zoom to it**, or *Select all*. |
| **Context & prompts** | Your current model, view and selection. *Copy this context for Claude*, plus one-click prompts such as "Explain my selection" and "Model QA check": click, then paste into Claude (Ctrl+V). |

### The Insights dashboard

Click **ACE tab → Insights → Dashboard** (read-only, takes a few seconds on large models). You get:
- a **health score out of 100** and its trend, with every finding ranked by its impact on the score;
- the most frequent warnings, key parameters filled, statistics by level, submission readiness
  (project information and sheet title block data), and Claude's activity this week;
- a **model check** in the sections of Autodesk's Model Checker (file and project, worksets, levels and grids,
  warnings, model content, views, annotation, naming): Pass / Review / Action needed per check and per section;
  your office can set the rules in a check set (`checkset.example.json` in the package);
- the latest **clash results** and the **status of every ACE tool**, including the planned ones.

In the window: **Refresh** after you fix things, choose a finding and click **Select in Revit** to select and
zoom to its elements, **Open in browser** to view or print it. Every run is saved as an HTML file in
`Documents\ACE Insights\<model name>\`: send it to your team lead or attach it to a submission. You can also
ask Claude: *"Show me the model dashboard and explain the top issues."*

### Working modes

The **Work mode** list at the start of the ACE tab sets up the ribbon for what you are doing: **Model audit**,
**Coordination**, **Production** (data, sheets, exports), **Submission** or **All tools**. The tools for the task stay
in full, less relevant ones are dimmed (still usable) and unrelated panels are hidden. The Companion shows the mode's
prompts first and Claude leads with the mode's tools. You can also tell Claude: *"switch to coordination mode"*.

### Worksets per the BEP

Ask Claude: *"Check the worksets against our BEP"* or *"Put the elements on the right worksets"*. It uses the BEP rules
file (by category, wall function, structural, MEP system, family or type name, level, zone or room department),
shows what would move, and applies only after you confirm (one undo step). Your BIM manager keeps the rules in
`worksets.json` (example in the package: `mcp-server\bep\worksets.example.json`). With that file in place, the
dashboard also reports elements on the wrong workset every time you check the model.

### Presentation standard (drawings)

**ACE > Deliver > Presentation Standard** keeps every drawing in one style:

- **Text height by scale:** for example 3 mm for 1:50 and larger scales, 2.5 mm for 1:100 and smaller. The height is
  the printed height on the sheet.
- **One type per kind and size:** pick the type for text and for each dimension style (linear, angular, radial, spot)
  at each size, or leave it on *Automatic* (ACE uses the type already used most at that size, or makes one).
- **Tags (optional):** one tag type per category, for example every door tag the same.
- **Check model** lists what is off; **Preview** shows what would change; **Apply** makes the change as one undo step.
  Save stores the standard in the office check set, so the dashboard checks it and Claude applies the same rules
  ("apply our presentation standard to the sheets").

### Clash Browser (coordination)

**ACE tab → Coordination → Clash Browser**: choose the **primary** model (this model; a BIM manager can pick a link)
and the model to **compare with**, click **Run clash test**, then click a clash in the list. The **ACE Clash View**
shows it alone: everything else dimmed, the primary model's element in green, the other in red (with several links, each link in its own colour: click
a colour square in the legend to change it), the intersection in
gold, zoomed to where they meet. **Show both models** colours the two models whole. Approve, mark active, select, or
open the **Coordination Report** for the meeting (a picture per issue, who should fix it, and an Excel list).

The ribbon also shows the **planned tools** (white buttons with a hollow red ring). Click one to see what it will do,
or **ACE tab → Roadmap** for the whole plan.

## 4. What Claude does before changing anything (your safety)

Every change goes through five steps. Two of the protections are enforced by the software itself,
not just requested of Claude:

1. **Explains the plan** in plain words: what, how, what will and won't change, and how to undo it.
2. **Previews**: it runs the change and rolls it back, then reports "would modify 142 doors, delete 0". *(Enforced: a real change is refused unless that exact change was previewed first.)*
3. **Asks you**: "Shall I apply this?" Nothing happens until you say yes, or click **Apply** in the ACE Companion panel.
4. **Applies** it as **one undo step** named "Claude: …". Press **Ctrl+Z** or say "undo that".
5. **Verifies** the result and reports it, often with a picture.

Also:
- **Read-only checks can't change anything:** they're always rolled back.
- **Risky actions are blocked** unless you agree: files, other programs, internet, saving, closing or syncing models. *(Enforced.)*
- **"Undo that"** only works if Claude's change is still the latest one. It never undoes your own work.
- **Backups**: ask "back up the model first" before big changes.
- **Activity journal**: ask "what did you change today?". The journal is in `%APPDATA%\ACE-RevitMCP\journal`.

Good practice: work on a **copy** or a model you've saved recently, especially while learning.

## 5. When something goes wrong

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

## 6. Updating and removing

- **Update:** unzip the new package, close Revit, run `install.cmd`. Your settings and scripts are kept.
- **Remove:** run `uninstall.ps1` from the package folder (`-Purge` also deletes settings, scripts and journals).
