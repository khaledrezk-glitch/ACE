# Development backlog

The one list of what is still to build, ranked. The idea agents read it and propose updates; the owner approves what
gets built (see `docs/DEV-TEAM.md`). Each item names its source: the owner's ideas (`docs/PROJECT-STATE.md`), the
architecture concept (`docs/ARCHITECTURE-CONCEPT.md`), the stress review (`docs/STRESS-REVIEW.md`), or the
development radar (the "Development radar" section of a learning report: real usage on the PCs).

Effort: S (hours), M (a day or two), L (more). Status: **open**, **next** (approved for the next cycle), **live check**
(built, waiting for verification in Revit).

## 1. Fewer steps and fewer interactions (owner, 6 Oct)

| # | Item | Why | Effort | Status |
|---|---|---|---|---|
| F1 | **One approval per job:** a multi-step job previewed as one run (each step sees the one before, all inside one transaction group): one Apply card listing every step, one apply, one undo step | Today a 4-step job means 4 previews and 4 approvals | M | open |
| F2 | **Show only the work mode's tools** (MCP tool list changes with the mode) | Fewer tokens in every conversation; needs a live test that Claude Desktop handles a changing tool list | S | open |
| F3 | **Ready-made jobs from the radar:** turn the radar's "code written again and again" and "tasks with many calls" into built-in scripts or native commands | Fewer calls for the jobs people really do | S each | open, fed by the radar |
| F4 | **Ask once, then act:** guidance and a planning helper so Claude asks its one question up front, then previews once, instead of trying, failing and asking | Fewer previews per change (radar: "several previews for one change") | S | open |
| F5 | **Long-running jobs** (concept G4): job id, progress and cancel in the Companion, result kept | No 30-minute waits inside one call | M | open |

## 2. Waiting for a live check in Revit

| # | Item | Effort | Status |
|---|---|---|---|
| L1 | 1.8.x as a whole: clash grid speed, snapshots after save, compile cache, presentation standard, worksets per the BEP in the model check, the new Apply cards | S (the owner runs it; benchmark cases exist) | live check |
| L2 | Whether a preview in a workshared model borrows elements (`heldAfterPreview`) | S | live check |
| L3 | Claude Desktop with progress notes and cancel on long calls | S | live check |

## 3. The concept's next circles (architecture concept, section 10)

| # | Item | Effort | Status |
|---|---|---|---|
| C1 | **Project requirements file** (D2): check set, worksets, clash matrix, naming, LOIN in one project file; office settings stay separate; old files keep working | M | open |
| C2 | **Requirements hub:** BEP / LOD / LOIN read into rules, recommendations, and the **contradiction monitor** (documents vs settings vs model) | L | open |
| C3 | **Notifications like Revit warnings** for requirement findings (concept section 8) | M | open |
| C4 | **Project Hub:** one container for rules, findings, models and documents, opened from the ribbon; click a point to edit | L | open |
| C5 | **BIM Tools group** on the ribbon | S | open |
| C6 | **Issue view engine and BCF export** (D4) for Navisworks / ACC | M | open |
| C7 | **Information requirements check from the LOIN** (D10) and the **submission check** as the monitor at the issue stage (D11) | M | open |
| C8 | **Rule packs** for design and code checks (D12) | M | open |
| C9 | **Family creator and checker** (concept section 12) | L | open |

## 4. Platform gaps (concept section 13)

| # | Item | Effort | Status |
|---|---|---|---|
| G1 | Verify before commit: a script states what must be true afterwards; the runner checks it and rolls back with the reason | M | open |
| G2 | Skills tree: guides, lessons and standards as branches loaded on demand (fewer tokens) | M | open |
| G3 | Sync with review: shared scripts, lessons and rules go through a reviewer before everyone gets them | M | open |
| G5 | Variables between runs (`ctx.Session`) | S | open |
| G6 | Who changed what, including Claude, in the change tracker | M | open |

## 5. Smaller technical items (stress review)

| # | Item | Effort | Status |
|---|---|---|---|
| T1 | Structured output (`outputSchema`) and guides as MCP resources | M | open |
| T2 | Native parameter rules in `find_elements` | M | open |
| T3 | Companion: skip rendering when hidden, cache tile icons | S | open |
| T4 | Clash discipline cached per document; background-save pruning that never removes a snapshot being read | S | open |

## 6. Later

| # | Item | Status |
|---|---|---|
| R1 | Rename (Constella or Datum, `docs/NAMING.md`), ideally before the Project Hub | owner decides |
| R2 | Brand guidelines and logo into the repository (it is private now) | owner sends the files |
