# ACE Revit MCP: Roadmap

Every feature below works two ways:
- **Through Claude:** "audit this model", "prepare the submission for client X".
- **Without Claude:** a button on the ACE ribbon, so the whole team benefits.

All features follow the existing rules: read-only by default; any change is explained, previewed,
confirmed, applied as one undo step, and recorded in the activity journal.

---

## The ACE ribbon: ready-made tools, with or without Claude

Each capability is built **once**, as a shared engine inside the add-in, and reached two ways:
- **Ribbon button:** one click, a simple dialog for options, and results in an **ACE Results** panel docked in
  Revit. Clicking an item in the list selects and zooms to its elements. There's also an HTML/Excel report.
- **Claude:** the same engine as an MCP tool, for requests like *"audit this model and fix the safe warnings on Level 2"*.

Ribbon tools that change the model follow the same safety rule without Claude: a **preview dialog**
("This will modify 142 doors: [details]"), then **Apply** or **Cancel**, one undo step, and an entry in the journal.
Buttons follow the user's **role** from Phase 1: hidden or greyed out when not allowed.

| Ribbon panel | Buttons | Phase |
|---|---|---|
| **Claude** | MCP Status · Activity Log · Undo Claude Change | exists (Status) / 1 |
| **Audit** | Model Health (score + report) · Warning Solver · Parameter Check · Rooms & Doors QA | 2 |
| **Inspect** | Snoop Selection · Snoop Document · Event Monitor | 3 |
| **Data** | Export to Excel · Import from Excel · Parameter Manager · Smart Select | 3 |
| **Export** | Batch Export (PDF / DWG / IFC / NWC) · Export Profiles | 3 |
| **Coordination** | Run Clash Test · Clash Results | 4 |
| **Deliver** | Submission Check · Prepare Submission · Transmittal | 5 |
| **Compliance** | Design Check · Code Packs | 6 |
| **Team Tools** | One button per shared team script, with icon and input form, updated from the team folder | 3 |
| **Admin** *(admins only)* | Users & Roles · Usage Report · Publish Policy | 1 |

Some capabilities are **Claude-only**, because they need reasoning rather than a fixed recipe: free-form
requests, proposing clash fixes, explaining audit findings, and writing new scripts. When a Claude
workflow proves useful, it can be saved and promoted to a **Team Tools** button.

Foundation work, done in Phase 1 before the first new button:
- WPF dialogs and the dockable **ACE Results** pane (element list → select/zoom, export to Excel/HTML).
- A shared "run tool" pipeline: options → preview → confirm → apply → report → journal.
- Role checks, so the same code serves the ribbon and the MCP tools.

---

## Phase 1: Control who can use it (foundation for rollout)

**Goal:** administrators decide who may use ACE, which features and ribbon buttons each person gets, and can switch it off.

**How it works:**
- **A signed policy file** (`policy.json` plus an Ed25519 signature) is published by an administrator to a
  shared location: a network share, SharePoint/OneDrive, or an HTTPS URL. It contains:
  - users or machines allowed or blocked, as Windows/Autodesk user names or groups;
  - a role per user: `viewer` (read-only questions), `editor` (model changes), `power` (risky actions,
    team-script publishing), `admin`;
  - feature switches: code execution, model changes, exports, team scripts, and which tools are allowed;
  - an expiry date and a kill switch (`enabled: false` turns ACE off for everyone).
- **The Revit add-in enforces it**, because the add-in is the part that actually touches models. It
  checks the signature with a public key built into the add-in, so the file can't be edited to grant
  access. It caches the last valid policy with an offline grace period (e.g. 7 days) and refuses commands
  the user's role doesn't allow, with a clear message.
- **The ACE ribbon** shows the user's status: *Active (editor)*, *Read-only*, *Blocked by administrator*.
- **Usage audit:** every command (user, machine, model, tool, result) is appended to the team log folder.
  The doctor and the reports include policy status.
- **`ace-admin.ps1`:** creates the signing key once, adds and removes users, changes roles, signs and
  publishes the policy, and shows usage.

**Honest limit:** this is governance, not DRM. A user with local admin rights could delete the add-in, but
can't *grant themselves* access or bypass the role limits while using it. Stronger control (code signing
and central servers) can come later if needed.

## Phase 2: Model audit, BIM audit and warning solver

**Model / BIM audit:** a **health score (0–100)** from configurable **rule packs** (JSON), with results
per category, element ids, and a fix hint for each finding:
- **Warnings:** total and by type, plus the worst offenders.
- **Model content:** file size, CAD imports and links, in-place families, groups, unused and purgeable
  items, design options, worksets and ownership, links status, view and sheet counts, views not on
  sheets, and unplaced or unenclosed rooms.
- **Naming conventions:** views, sheets, levels, families and types, checked against the office standard.
- **Parameter completeness:** checked against the office's required-parameter list.
- **Outputs:** an interactive HTML report, an Excel file and Markdown. Scores are stored per model over
  time, so trends are visible ("health went from 62 to 81 this month").

**Warning solver:** it groups warnings, then offers **safe automatic fixes** where the fix is certain,
always previewed:
- **Duplicate mark values:** renumber following a rule.
- **Identical instances in the same place:** delete the duplicate.
- **Overlapping room separation or area lines:** delete the duplicate line.
- **Slightly off-axis walls and lines:** straighten them within a tolerance.
- **Warnings it can't fix safely** (room not enclosed, joins, stair rules): an explained list with the
  elements selected and a view zoomed to them.

## Phase 3: Built-in equivalents of well-known add-ins

We **reimplement the capabilities** instead of copying code. RevitLookup is MIT-licensed, so ideas can be
reused with attribution; pyRevit is GPL-3 and DiRoots/ProSheets are commercial, so they are built independently.

| Inspired by | ACE capability |
|---|---|
| **RevitLookup** | `snoop` tool + ribbon button: inspect any element, selection, document or application in depth (properties, parameters, geometry, sub-elements, dependents, schemas, API types), with a live event monitor. Claude uses it to understand unusual families and data. |
| **DiRoots SheetLink / ParaManager** | **Excel round-trip:** export chosen categories and parameters to `.xlsx`, edit in Excel, re-import with a preview of every change. **Parameter manager:** create or bind shared and project parameters in bulk from a list. |
| **DiRoots ProSheets** | **Batch export:** sheet and view sets to PDF, DWG, IFC or NWC, with naming rules like `{Project}-{Sheet Number}-{Rev}`, combined or separate PDFs, saved export profiles, and a transmittal log. |
| **DiRoots OneFilter / FamilyReviser** | Advanced selection by any parameter or rule, and batch family and type renaming, cleanup, and purge. |
| **pyRevit** | **Team toolbar:** any saved script can become a ribbon button with an icon and a simple input form, runnable **without Claude**. Buttons update from the team script folder, and there are startup and document-open hooks. |

File-writing features (exports, Excel) get a **dedicated safe tool** that writes only inside a
configured exports folder, so they don't need the risky-code permission.

## Phase 4: Clash detection

- **In-Revit clash tests** between categories or links, e.g. ducts vs beams, or pipes vs walls in a
  structural link. It uses a fast bounding-box pass, then exact solid intersection, with
  **tolerance and clearance** ("pipes within 50 mm of beams").
- **Results:** clash groups by level or zone, a status per clash (new, active, resolved, approved)
  kept between runs, a 3D section-box view per clash, and HTML/Excel/BCF export so results can be
  shared with Navisworks or ACC users.
- **Claude assist:** explains clashes in plain words and proposes fixes, such as lowering a duct run by
  150 mm, applied only after preview and confirmation.

## Phase 5: BIM submission preparation

A **submission checklist** per client or employer's information requirements (a configurable template),
run as one guided workflow:
1. Pre-flight audit (Phase 2) with a minimum score.
2. Required parameters and title-block data complete: project info, revisions, sheet issue dates.
3. Naming checks (e.g. ISO 19650 file, view and sheet naming), and sheet numbering and sequence.
4. Cleanup **on a detached copy, never the working model**: purge, remove CAD imports and unused
   views, set the starting view.
5. Exports (Phase 3): PDF sets, DWG, IFC with the right mapping, NWC, COBie-lite spreadsheets.
6. Transmittal and a submission report: what was checked, what was exported, and the file hashes.

## Phase 6: Design checks and code compliance

- **A rule engine:** rules are data, not code, so the BIM team can add and adjust them. Examples:
  - minimum room areas and dimensions by room type;
  - door clear widths and corridor widths;
  - stair riser, tread and headroom;
  - travel distance to exits (approximate, using room connectivity through doors);
  - window-to-floor ratio for daylight;
  - accessibility clearances;
  - parking counts.
- **Code packs:** a pack per code or client standard, for example a company standard pack and a
  local building code pack, such as the Egyptian code or the Saudi Building Code (SBC). A pack must be
  **authored and approved by the firm's engineers**: the tool provides the engine and examples, and
  every report states which pack and version it used. Results are **advisory**, not a certification.
- **Output:** pass/fail per rule with element ids, measured vs required values, views highlighting the
  failures, and Excel/HTML reports.

---

## Suggested order and rough size

| Phase | Why this order | Size |
|---|---|---|
| 1. Usage control + ribbon foundation | Needed before rolling out to many PCs; results pane, dialogs and preview pipeline used by every button | M–L |
| 2. Audit + warning solver | Highest everyday value; builds on existing scripts | M |
| 3a. Snoop + team toolbar | Big usability win, and makes the other features one-click | M |
| 3b. Excel round-trip + batch export | Replaces paid tools for common work | M–L |
| 4. Clash detection | Needs solid-geometry work and result tracking | L |
| 5. Submission preparation | Combines 2 + 3b into a workflow | M |
| 6. Code compliance | Needs the rule engine plus engineer-approved packs | L (engine) + ongoing (packs) |

## Decisions needed from ACE

1. **Usage control:** where should the policy live (network share, SharePoint/OneDrive, or a web URL)?
   Should users be identified by Windows user, Autodesk user, or both? Who are the administrators?
2. **Codes:** which building codes and client standards come first, and who at ACE owns and approves the rule packs?
3. **Submission templates:** one or two real client requirement documents to model the first checklist on.
4. **Office standards:** naming conventions and required parameters, which feed the audit rules.
