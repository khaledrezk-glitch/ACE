# Stress review: complex tasks, speed, best practice, tokens

**Date:** 1 October 2026, on the 1.7.0 code after the tidy pass. Four reviews: complex multi-step tasks (traced call by
call), speed on a large model (200k elements, 8 links), best practice (MCP and the Revit API), and token use.
They were done by reading the code. Revit was not available, so everything below still needs a live check.

Status: **Done** = fixed in this round (built, offline tests pass). **Next** = recommended, waiting for the go-ahead.
Effort: S (hours), M (a day or two), L (more).

## 1. Safety and correctness

| # | Finding | Status |
|---|---|---|
| 1 | A preview could be applied in another model (switch model between preview and apply), or for code, with another active view. | **Done:** each preview records its model and active view; an apply elsewhere is refused (Claude and the Companion's Apply button). |
| 2 | If a rollback failed, Claude was still told "the model is exactly as before". | **Done:** every rollback is checked; a failure is reported as `rollbackFailed` with a warning to tell the user and check. |
| 3 | The risky-code screen was easy to get around (aliases, comments inside a call, reflection, `SaveAndClose`, unloading links, reading files such as the bridge token). | **Done (stopgap):** those patterns are caught and comments are ignored. **Next (M):** screen with Roslyn's semantic model in the compiler, so nothing can be hidden. |
| 4 | Tool labels: read-only clash runs that store status, undo and backup not marked as changing things, team script overwrite not marked. | **Done.** |
| 5 | `assign_worksets` would run a user or team script with the same name instead of the built-in one. | **Done:** always the built-in script. |
| 6 | The bridge accepted any Host header, compared the token in variable time and had no size limit. | **Done:** this PC only, constant-time token check, 8 MB limit. |
| 7 | Workshared models: elements owned by others make the whole job roll back late; a preview may leave elements borrowed. | **Next (M):** `ctx.CanEdit` / `ctx.SkipUneditable` helpers, report would-be-borrowed elements in previews, relinquish after a preview; verify the borrowing live. |
| 8 | `ModelGuard` presses OK on every dialog and handles failures for every document. | **Next (M):** per-transaction failure handling, an allowlist of dialogs. |
| 9 | Script assemblies were never unloaded (memory grows over a long session). | **Done.** |

## 2. Complex tasks

| # | Finding | Status |
|---|---|---|
| 10 | A dialog or a long clash run made every other call wait its full timeout (up to 30 minutes), then said "Revit is not reachable". | **Done:** a request must start within 60 s; the error names the command Revit is busy with; a call still running says "do not send it again"; `/health` shows what Revit is doing. |
| 11 | `model_changes` ignored words it did not know ("since Monday" gave "no changes"). | **Done:** weekdays, "3 days", "2 weeks ago" understood; anything else is an error. Tested. |
| 12 | `set_clash_status` needed the test name, which the results never gave; unknown keys did nothing silently. Walking through issues needed clash keys. | **Done:** keys alone are enough, issue keys work (approve a whole issue, focus on its deepest clash), unknown keys are an error. Issue lists carry `issueKey` and three keys instead of all of them. |
| 13 | Running `model_changes` before a clash run moved the baseline, so every new clash looked "unchanged". | **Done (guidance):** the workflows guide says to pass `save_snapshot: false`. **Next (S):** `run_clash_test changed_since`. |
| 14 | Renumber + tag + sheets needs about 3 calls per level: `tag_untagged` works on the active view only; `sheets_for_levels` makes new plans (so the new tags are not on the sheets), duplicates on re-run. | **Next (S):** `views` / `all_floor_plans` for tagging, `use_existing_views` and skip existing sheets, a `renumber_doors` script. |
| 15 | Test fit takes one room at a time; option cards can get the same title. | **Next (S):** `level` / `room_numbers` input, an `option_label` in the card title. |
| 16 | "Fix the top warnings" has no tool: Claude writes `GetWarnings()` code each time. | **Next (S):** `list_warnings` plus fix scripts for the common ones. |
| 17 | `assign_worksets` on a large model hit the default 5-minute timeout, so the preview never registered. | **Done:** 15 minutes by default, adjustable. |
| 18 | Clash views add real undo steps, so after a walk-through Ctrl+Z undoes the view, not Claude's change. | **Next (S):** treat clash-view edits like the coordination report's temporary work. |

## 3. Speed (large models)

| # | Finding | Gain | Status |
|---|---|---|---|
| 19 | Clash search builds a new collector for every element of set A and every other model (about 70k collectors). | 10-50x | **Next (M):** one pass per model into a 3D grid of boxes, then query it. |
| 20 | Clash pair work: solids transformed again for every model; boolean test on pairs whose boxes don't even touch. | 2-5x | **Next (S).** |
| 21 | The automatic snapshot after save, sync and open freezes Revit (all elements, all parameters, gzip on the main thread). | 3-5x, then off the main thread | **Next (M):** quick filters, cached names, defer to idle time, save in the background. |
| 22 | Every `execute_revit_code` compiles from scratch (the preview and the apply compile twice), re-reading hundreds of reference assemblies. | 1-3 s to under 0.3 s | **Next (M):** cache references and compiled scripts, warm up at start. |
| 23 | `model_changes` captures all 8 links every time, even unchanged ones. | 5-9x | **Next (M):** skip links whose file has not changed. |
| 24 | Model check: warnings read twice, element counts by materialising every element, family lookups per instance. | 2-4x | **Next (S).** |
| 25 | `find_elements` filters levels and parameters in a loop and counts everything after the limit. | 5-20x | **Next (M):** native level and parameter filters. |
| 26 | `list_types`, `describe_category`, the quick brief and the full brief walk the whole model for counts or samples. | 2-10x each | **Next (S).** |
| 27 | Coordination report: one image export per issue. | 1.5-3x | **Next (S):** one export for all. |

## 4. Tokens

| # | Finding | Status |
|---|---|---|
| 28 | Every reply was pretty-printed JSON, about 25% larger than needed. | **Done:** compact JSON. |
| 29 | A reply could be 120,000 characters (about 30k tokens) and was cut in the middle, leaving broken JSON. | **Done:** 60,000 characters at most; the longest lists are shortened with a note ("N more not shown"), so the reply stays valid and keeps every field. Tested. |
| 30 | The script result came last, after warnings, so a long reply lost the result first; 1,000 identical warnings were listed 1,000 times. | **Done:** result first; warnings grouped with a count (top 30). |
| 31 | Fixed cost at the start of every conversation: about 14k tokens (instructions about 4k, 42 tool descriptions about 10k). | **Done (part):** workflow detail moved to `revit_guide workflows` (read when needed); long clash descriptions trimmed. Instructions about 2.8k tokens. **Next (M):** merge tools (lessons x3, snapshots into model_changes, script list/read, clash results into run_clash_test) and show only the working mode's tools. |
| 32 | `assign_worksets` returns every moved element id (200k on a big model). | **Next (S):** cap the ids, return counts per workset. |

## 5. MCP features not used yet

| # | Finding | Status |
|---|---|---|
| 33 | A cancel from Claude did not stop the call; long calls sent no progress. | **Done (server side):** cancel stops the wait (a request Revit has not started is then skipped); a progress note every 15 s. **Next (M):** cancel a running script (a token in `ScriptContext`). |
| 34 | No structured output (`outputSchema`), no paging on list tools, guides not offered as resources. | **Next (M).** |
| 35 | Team lessons from the shared folder go into the brief verbatim (a channel for injected instructions). | **Next (S):** mark them as data and cap their length. |

## Suggested order for the next round

1. Clash speed (19, 20) with clash-test cases for the grid.
2. Snapshot freeze (21) and compile cache (22): every user feels them.
3. Workshared safety (7) and the Roslyn screen (3).
4. Workflow gaps (14, 15, 16, 18) as built-in scripts and small tool options.
5. Tool merges and modes (31), then structured output and paging (34).

Each fix gets a benchmark case in `tools/bench/cases.json`. The live benchmark with the Snowdon sample, plus a real
project with links, decides whether a fix is done.
