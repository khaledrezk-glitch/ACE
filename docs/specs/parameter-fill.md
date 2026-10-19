# parameter_fill: parameter completeness from a list, with an approved "NA" fill

Asked for by the owner on 19 Oct 2026 (a client IFC submission: readiness of the information requirements). First input is a client AIS parameter list
(CSV, kept in the project files, never in this repository). Tests use synthetic rows only.

## What it answers, per open model

For every row of the list (parameter name + category group; the key is name + group, because one name such as `Area`
can appear in two groups):

- **missing**: elements of the group that have no parameter with that name (not bound to their category, not a built-in,
  not a family parameter);
- **blank**: they have it but it is empty (text: empty or whitespace; other storage types: `!HasValue`);
- **filled**: they have it with a value (0 counts as filled);
- **storage type** and **isText** (only text can take "NA"; numbers, lengths, areas, Yes/No, dates stored as numbers
  cannot);
- **binding**: for project / shared parameters, whether the binding map binds it as instance or type, and to which of
  the group's categories; built-in parameters report `origin: "built-in"`;
- for **Project Information** rows: the parameter's group (`InternalDefinition.GetGroupTypeId()`), and `inGeneral`
  false when it is not `GroupTypeId.General`. ACE never moves a parameter's group (that changes the definition).

## Category groups (from the list's `categories` column)

| List value | Group key | Elements |
|---|---|---|
| Project Information | `project` | `doc.ProjectInformation` |
| Rooms, Areas, Spaces | `rooms` | `OST_Rooms`, `OST_Areas`, `OST_MEPSpaces` instances |
| Levels | `levels` | `Level` elements |
| Systems, Components, Elements | `elements` | every other model element instance (`CategoryType.Model`, not view-specific, category allows bound parameters), rooms / areas / spaces / levels excluded |

A row marked `Type` is read on the element's type (each type counted once); `Instance` (or blank) on the element.
Unknown category values are reported back as skipped rows, not guessed.

## Bridge command `parameter_fill` (add-in, works on Revit 2024 and 2025: native, no scripts)

Request:
```json
{ "rows": [ { "name": "TST_GEN_City Name", "group": "project", "level": "instance" } ],
  "all_documents": false, "fill": false, "value": "NA", "max_ids": 20,
  "check_only": true, "dry_run": false }
```
- Without `fill` it is read-only (a check): every open project document when `all_documents` is true, else the active
  one. Linked documents are not included.
- With `fill: true` it is a change through `NativeChange.Run` (check_only / dry_run / apply, ONE undo step) on the
  active document only: sets `value` (default "NA") on blank **text** parameters of the listed rows, skips read-only
  parameters and elements that `CanEdit` refuses (borrowed by others, etc.), and reports what it skipped and why.

Response:
```json
{ "documents": [ { "model": "...", "rows": [ { "name": "...", "group": "project", "level": "instance",
    "origin": "shared|project|built-in|family|null", "bound": true, "boundAs": "instance", "storageType": "String",
    "isText": true, "groupId": "General", "inGeneral": true,
    "checked": 1, "missing": 0, "blank": 1, "filled": 0, "blankIds": [76675], "missingIds": [] } ],
    "summary": { "rows": 141, "complete": 40, "withBlanks": 60, "missingEverywhere": 30, "notText": 26, "notInGeneral": 3 } } ],
  "fill": { "set": 120, "skipped": [ { "id": 1, "name": "...", "reason": "read-only" } ] } }
```

## MCP tool `parameter_fill` (server)

- `list`: path to the CSV (columns `parameter_name`, `categories`, `type_instance`; others ignored), or `rows` inline.
  The server parses the CSV (quoted fields, BOM), maps `categories` to the group keys above, drops duplicate name +
  group pairs, and sends `rows` to the bridge.
- `action`: `check` (default, read-only, `all_documents` allowed) or `fill` (active model, follows the preview gate in
  `lib/safety.js` like the other native changes: dry run first, the same request applied, Companion card).
- Returns the bridge answer, plus a short text summary per model.

## Tests

- add-in: the row / group parsing and the blank rules are pure functions, testable in `tools/checkset-test` style.
- server: CSV parsing (quotes, BOM, duplicate key, unknown category), fake-bridge smoke case for check and for the
  preview gate on fill.
