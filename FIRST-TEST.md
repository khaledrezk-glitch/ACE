# First test (about 10 minutes)

Do this once after installing, to see the whole setup working before you trust it with real
projects. **Use a copy of a model**, e.g. one of Revit's sample projects (File > Open > Samples), or
"Save As" a scratch copy of one of your own.

## 0. Install

1. Close Revit.
2. Double-click `install.cmd` and wait for **Done!**
3. Start Revit 2025. When asked about **ACE Revit MCP**, click **Always Load**.
4. Open your scratch model. Click the **ACE** tab > **MCP Status**. It should say *Claude connection is running*.
5. Fully quit Claude Desktop (tray icon > Quit), then start it again.

## 1. Connection (read-only)

Paste into Claude Desktop:

> Check the Revit connection and give me an overview of the open model.

✅ Expected: Revit version, model title, levels, units, and the categories with element counts.

## 2. Understanding and seeing (read-only)

> Run the model audit and explain the most important problems in simple words. Then show me a picture of the current view.

✅ Expected: grouped warnings, rooms, CAD imports and so on, then an image of your active view.

## 3. A real change, with full safety (on the scratch copy)

> Put the text "Checked by Claude" in the Comments of every door on the first level. Explain your plan first.

✅ Expected, in this order:
1. **Plan:** what Claude understood, the steps, what will and won't change, and how to undo it.
2. **Preview:** "would modify N doors…" with the model still unchanged.
3. **Question:** "Shall I apply this?" **Answer "yes".**
4. **Result:** counts and examples of what changed.

Then in Revit, select a door and look at its Comments. Also check that the **Undo** dropdown
(arrow next to the undo button) shows one entry: **Claude: …**

## 4. Undo

> Undo that change.

✅ Expected: the Comments are empty again. Now try it another way: make any small edit yourself
(move a door), then ask Claude to undo. ✅ Expected: **it refuses**, because the latest change is
yours, not Claude's.

## 5. Something complex in concept

> Create a structural grid of 4 bays by 3 bays, 6 m each, near the origin, then make a new floor plan and a sheet for every level. Walk me through it before doing anything.

✅ Expected: plan, preview ("would add 9 grids…"), your confirmation, then the result and a picture.

## 6. What happened?

> What did you change in Revit today?

✅ Expected: a list of the previews and changes, with results. The full journal, including the
code, is in `%APPDATA%\ACE-RevitMCP\journal`.

---

If a step fails, copy the message Claude shows, plus the last lines of
`%APPDATA%\ACE-RevitMCP\logs\addin.log`, into the Claude Code session that built this, and it will fix it.
