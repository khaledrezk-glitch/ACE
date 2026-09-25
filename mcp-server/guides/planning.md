# Planning a Revit task (read this first for anything non-trivial)

## 1. Restate the goal as a checkable outcome
"Renumber rooms" → "Every placed room on Level 2 has a unique number 201..2NN in reading order;
unplaced rooms untouched." If you can't state how you'd verify it, ask one question.

## 2. Discover before you design
- `get_model_overview`: units, levels, phases, what categories exist.
- `describe_category` for every category you'll read or write: exact parameter names, storage type,
  instance vs TYPE, how filled they are. Never assume "Width" is an instance parameter; for many doors it's a type parameter.
- `list_types` before placing or changing types: exact family and type names, and whether they're loaded.
- `revit_api_lookup` for any API member you're not 100% sure about in Revit 2025 (overloads change between versions).

## 3. Decide the edge cases explicitly (and tell the user)
Unplaced or unenclosed rooms, elements in groups or design options, linked vs host elements, pinned
elements, phases (existing vs new), worksharing ownership, elements hidden in the view, values that
already exist (overwrite or skip?), mixed units.

## 4. Design the script
- Read phase: collect everything once, build dictionaries (id → data). No edits yet.
- Compute phase: decide every change in memory and build a change list.
- Write phase: apply the change list in ONE transaction (mode auto).
- Return a compact report: counts, a sample of changes (≤20), skipped items with reasons.
- Guard: if the change count is wildly different from expectations, `throw` with a clear message
  (the run is rolled back automatically).

## 5. Preview, then explain it like an engineer to an architect
"Would renumber 37 rooms on Level 2 (201-237). 3 unplaced rooms skipped. No other elements change."
Mention anything surprising in `wouldChange` (e.g. modified views or tags, which are normal side effects).

## 6. Apply only after a yes, then verify independently
Verify with a SEPARATE read-only query (not the script's own claims), and a `view_image` when layout matters.

## 7. Leave the team better off
If it's likely to be repeated, `save_script` with inputs (`scope: "team"` if it's useful to everyone).
