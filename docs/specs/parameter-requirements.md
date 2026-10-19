# Parameter requirements: several sources, model-aware check (extends `parameter_fill`)

Asked for by the owner on 19 Oct 2026 ("work on the tool to ensure it can perform the needed tasks and checks", REH IFC
submission). It closes duplication **D10** in `docs/ARCHITECTURE-CONCEPT.md` (one information requirements check driven
by the LOIN). Client files (AIS, LOIN matrix, Model Checker set, shared parameter files) stay in the project folder,
never in this repository; tests use synthetic fixtures shaped like them.

Release 1.10.0 ships the read-only part (P1 sources, P2 pre-flight, P3 check, P8 checker review, a CSV report). The
writes (P4 impact scan, P5 binding plan, P6 derived fill, P7 missing register) follow in 1.11.0.

## Integration rule answers

1. **Engine:** the `parameter_fill` engine (add-in `Rules/ParameterFill.cs`, server `lib/paramlist.js` +
   `lib/paramsources.js`). No parallel checker: the model check's "Key parameters filled" stays generic; this is the
   requirement-driven check.
2. **Rules:** the project's own requirement documents, read where they are (AIS CSV, LOIN xlsx, Model Checker XML,
   shared parameter files), plus one editable mapping file the project keeps beside them
   (`ace-parameter-mapping.json`: Modon category → Revit categories, LOIN sheet → disciplines, approved name aliases,
   model → discipline). ACE pre-fills it by best guess and never applies a guess silently: unmapped categories and
   spelling candidates are listed until a person writes them in.
3. **Publishes:** the per-model result (and a CSV in `report_dir`); the compliance engine can read it later as the
   evidence of the LOIN requirements.
4. **Connects to:** approvals (fill), the vault (sources are the same client documents), compliance (evidence), the
   Companion (later: the federated summary).
5. **Replaces or merges:** D10 (Parameter Check plan, LOIN check).
8. **Proactive and model-aware:** every check run starts with a pre-flight of the model as a whole (worksharing,
   borrowed elements, groups, design options, links, discipline scope), so the result says what a fill could and could
   not touch before anyone asks for one.

## P1. Requirement sources (server, `lib/paramsources.js`, pure functions)

`parameter_fill` takes any of:

| Input | File | Gives |
|---|---|---|
| `list` (existing) | AIS-style CSV: `parameter_name`, `categories`, `type_instance` | name, group, level |
| `loin` | xlsx; every sheet that has a header row with `GROUP`, `CATEGORY` and a column starting `Shared` (whitespace-insensitive, any row in the first 10) | name, discipline sheet, client category |
| `checker` | Model Checker `MCSettings` XML | for each **active** `Check` with a `Parameter` filter `HasValue` (or `Defined`): name, categories (`Category` filters, `Included`, operator And/Or; Exclude ignored for scope), level (`TypeOrInstance` "Is Element Type" Equal True → type, False → instance) |
| `shared_parameters` | one or more Revit shared parameter files (Modon corporate set) | name → GUID, data type, file group |
| `project_parameters` | the project's shared parameter file | name → GUID, data type (the one a binding must use) |
| `mapping` | `ace-parameter-mapping.json` (path; created with best guesses when missing) | see below |

**Readers.**
- Shared parameter file: tab-separated; UTF-16 LE/BE by BOM or by NUL bytes, else UTF-8 (BOM stripped). `*GROUP`/`GROUP`
  and `*PARAM`/`PARAM` lines by their header row's column names (never by position).
- Checker XML: a small tag tokenizer (attributes, entities, self-closing tags; no DTD). `Heading` and `Section` nest at
  any depth: walk recursively. A check is **active** when its heading, every enclosing section and the check itself have
  `IsChecked="True"` and no enclosing heading or section is named `<--Unused-->` or `Unplaced`.
- LOIN xlsx: `readXlsx` from `lib/docs.js` (no new dependency). Sheet names starting `00_` are document management.
  A blank GROUP cell inherits the one above. Category cells are trimmed.

**Name rules (never guess).** A row's name is used exactly as listed, or through an alias a person wrote into the
mapping file. Bracketed pseudo-names (containing `[` or `]`) are skipped as "not a parameter name". Against the
shared parameter files, each name is classified: `exact` (project file), `exact-client` (only in the client files: the
project file lacks it), `spelling` (matches after case / space / `_` normalisation: candidate given), `prefix-drift`
(the part after the `MOD_<code>_` prefix matches a parameter whose prefix differs, e.g. ME vs MECH: candidate given),
`unknown`. Only `exact`, `exact-client` and aliased names are checked as such; the others are still checked under the
listed name (it may exist in a model) and their candidates are reported as defects to resolve.

**Merging.** Rows from all sources merge on name + group (+ categories for `elements`): each merged row keeps every
source reference (`ais` line, `loin` sheet!row, `checker` check name and id). A row with no categories means every
element of the group.

**Groups and categories.** `project` = Project Information, `rooms` = Rooms/Areas/Spaces, `levels` = Levels,
`elements` = model elements; an `elements` row may carry `categories` (Revit `OST_` names). Checker rows whose only
category is `OST_ProjectInformation` / `OST_Rooms|OST_Areas|OST_MEPSpaces` / `OST_Levels` map to those groups. LOIN
categories map through the mapping file's `categories`; an empty list means unmapped (row reported, not checked).
LOIN rows have no type/instance: level `any` (instance first, then the element's type).

**Mapping file** (`ace-parameter-mapping.json`, JSON, edited by people):
```json
{
  "about": "ACE parameter requirement mapping. Edit freely; ACE never overwrites a value you set.",
  "categories": { "Walls": ["OST_Walls"], "Pumps": ["OST_MechanicalEquipment"], "Kitchen (& White Goods)": [] },
  "disciplines": { "01_ARC": ["AR"], "02_STR": ["ST"], "08_ELV": [] },
  "aliases": { "MOD_ME_Component ID": "MOD_MECH_Component ID" },
  "models": { "720001-AR": "AR" }
}
```
When the file is missing ACE writes it with best guesses (keyword rules, generic, in `paramsources.js`) and marks it
`"draft": true`. When it exists, ACE adds keys for new categories / sheets it has not seen (empty lists, i.e. unmapped)
and never changes a value already there.

## P2. Pre-flight (add-in, read-only, every check)

Per model: Revit and ACE version; workshared, central or local (and the central path); worksets; links (count, not
loaded, names); phases; design option sets; groups (instances); and for the elements the rows would write to, how many
of the blank values sit on elements in a group, in a design option, or **borrowed by someone else**
(`WorksharingUtils.GetCheckoutStatus` == OwnedByOtherUser; checked only for elements with a blank value).

**Discipline scope.** The server sends `scope: { codes: [...], models: { "<title part>": "AR" }, ownership:
{ "OST_Walls": ["AR", "ST"] } }`. The model's discipline is the `models` entry whose key is part of the title, else the
first title token (split on `-`, `_`, space, `.`) equal to a code. Rows carrying `disciplines` that do not include the
model's discipline are **out of scope** (not scanned). For general `elements` rows (no categories), elements whose
category has owners in `ownership` that do not include the model's discipline are **foreign** (e.g. MEP placeholders in
the architectural model): not judged, counted per category in the pre-flight. Ownership comes from the LOIN sheets
(category → the sheet's disciplines) unless the mapping file says otherwise. INFERENCE noted in the output: the REH
model responsibility matrix has no model element authors filled in, so ownership falls back to the LOIN.

## P3. Check (add-in; server adds cross-model checks)

Per model × row, besides 1.9.3's counts: `na` (text equal to NA / N/A, any case: declared missing, counted apart from
filled); `onType` (level `any`: found on the type, not the instance); `categories` (resolved) and `unknownCategories`;
binding: `unboundCategories` (row categories the binding does not cover), `wrongLevel` (required instance but bound as
type, or the reverse); GUID: `modelGuid`, `guidMatch` (against the row's expected GUID from the project file, else
the client file), `definitions` (how many definitions with that name the model has); Project Information rows:
`value` (the display value, so the server can compare twins). `status`, one of: `out-of-scope`, `unmapped`,
`no-elements`, `not-bound`, `wrong-guid`, `partly-bound`, `blanks`, `complete-with-na`, `complete`.

Server: the same name with different GUIDs across models (`guidDiffers`); Project Information twins (`MOD_GN_x` and
`MOD_GEN_x`, same attribute after the prefix) whose values differ; per-model text summary; with `report_dir`, a CSV of
every model × row (status, counts, sources, defects) and a short markdown summary.

## P8. Client checker review (server only, no Revit: `action: "review_checker"`)

From the checker XML (and, when given, the AIS list and the shared parameter files): parameter checks active vs parked
in Unused / Unplaced; per category the active parameter checks; scope defects (a discipline parameter, e.g. `MOD_ST_*`
or an AIS `elements` row, checked only on `OST_ProjectInformation`); names absent from the shared parameter files and
from the AIS; duplicated checks (same parameter + categories); checks with no category. Output: JSON + text, and with
`report_dir` a markdown report.

## Bridge contract (add-in `parameter_fill`)

Request rows gain optional fields; old requests keep working:
```json
{ "rows": [ { "name": "MOD_ST_Concrete Grade", "group": "elements", "level": "any",
              "categories": ["OST_StructuralColumns"], "guid": "…", "disciplines": ["ST"], "key": "r12" } ],
  "scope": { "codes": ["AR","ST"], "models": {}, "ownership": { "OST_StructuralColumns": ["ST"] } },
  "all_documents": true, "max_ids": 20 }
```
Response per document gains `discipline`, `disciplineSource` (`mapping` | `title` | null) and `preflight`; per row the
fields of P3 plus `key` echoed. The `summary` gains `withNA`, `outOfScope`, `wrongGuid`, `notBound`, `wrongLevel`,
`foreign`. Fill (1.9.3 behaviour) accepts the same rows; elements in groups or design options and borrowed elements are
skipped with their reason.

## Tests

- add-in pure logic in `tools/checkset-test`: status rules, discipline from title, NA counting, row parsing of the new
  fields.
- server `test/paramsources.js`: shared parameter file (UTF-16 and UTF-8, header order), checker XML (nested sections,
  Unused, entities, levels), LOIN rows from a synthetic xlsx built in the test, name classification (spelling, prefix
  drift, bracket pseudo-names, alias), merge, mapping file create + extend without overwriting, the P8 review.
- smoke: fake bridge answers the new fields; `review_checker` runs without a bridge.

## 1.11.0: the writes and the summary (P4 to P7, P9)

### P4. Impact scan (add-in, read-only; runs with every fill or bind dry run, and as `action: "impact"`)
For each parameter a write would touch:
- **schedules** (`ViewSchedule`, not templates, not revision / title block schedules) with a field of that parameter
  (`ScheduleField.ParameterId` = the shared parameter element id, or the built-in parameter id), and each schedule
  filter on that field with its type (HasValue, HasNoValue, Equal, …): a "has value" filter will start showing rows
  that were hidden once "NA" is written;
- **view filters** (`ParameterFilterElement`): rules on that parameter, walking And/Or filters;
- **labels in tag and title block families**: Revit cannot read a label's contents, so ACE uses the proxy that a
  shared parameter can only be in a label when it is in the family: for every loaded tag and title block family with
  instances, `EditFamily` (outside any transaction, closed without saving, cached per family) and look for the
  parameter by GUID (shared) or name. Then count the sheets that show it: title blocks on sheets, and tags in views
  placed on sheets. Reported as "label in <family>: prints on N sheets" and labelled as inferred;
- **IFC**: server side, from `ifc_property_sets` (the user-defined property set file of the IFC export setup, when the
  person gives its path): the property sets and IFC entities each parameter is exported to.
- **client checker**: server side, the active checks that change from fail to pass, with the caveat that "NA" passes a
  HasValue check: it makes the checker green without the data, which is why P7 keeps the register.

### P5. Binding plan (add-in, approval-gated write, `action: "bind"`, active model only)
For rows that are not bound, partly bound (categories missing) or bound at the wrong level: a plan per parameter,
then the same preview gate as every native change (dry run, the same request applied, ONE undo step).
- The definition comes only from a file: the project shared parameter file first; a parameter only in the client file
  is bound from a temporary shared parameter file holding exactly that line (client GUID, name, data type) and the plan
  says "add this line to the project shared parameter file". ACE never invents a GUID and never edits the project file.
  `Application.SharedParametersFilename` is restored afterwards, also on failure.
- Existing binding of the same GUID: extend its categories (`ReInsert`). The same name bound with another GUID: no
  change, reported (a person decides). Project Information rows bind to `GroupTypeId.General`.
- Workshared models: the plan says to sync with central afterwards; failures (e.g. not editable) are reported per
  parameter, not thrown.

### P6. Fill, derived values first (extends 1.9.3)
One transaction, one undo step, in this order:
1. **derived values** from the mapping file's `derive` rules: `{ "target": "A", "copyFrom": "B" }` (also proposed
   automatically for Project Information twins `MOD_GN_x` / `MOD_GEN_x`: blank side takes the filled side, never
   overwriting a value) and `{ "target": "A", "join": ["B", "C"], "separator": "" }` (all parts must have values);
2. **the fill value** ("NA") for blank text parameters that remain.
Type parameters are written once per type, with the number of instances each type change reaches. Skips (group, design
option, borrowed, read-only, not text) are listed with reasons. The response separates `derived` (per rule) and `na`.

### P7. Missing register and evidence (server)
Before any fill (dry run or apply) the server writes the register of genuinely blank values per model to
`report_dir` (or the vault's audit folder): `missing-register-<model>-<date>.csv` (model, parameter, category, level,
element id, type name) + a summary, from the add-in's `register` (all blank ids per row and category, compact). After an
applied fill it re-runs the check and writes `evidence-<model>-<date>.md`: missing before, derived, NA written,
skipped, still blank, not bound. That file is what a QA/QC checklist item points to, instead of a pre-marked
"Compliant".

### P9. Federated summary (server)
From a check of all open models: discipline × model × status counts (complete, complete with NA, blanks, not bound,
wrong GUID, not text, out of scope, unmapped) and one row per model × requirement with its sources (AIS line, LOIN
sheet and row, checker check). Written as xlsx (the zip writer in `lib/docx.js`) and markdown in `report_dir`.
