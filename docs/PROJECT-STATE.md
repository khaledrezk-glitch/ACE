# Project state: where everything stands

**Written:** 30 September 2026, after the 1.7.0 work and the concept sessions. This is the single summary to start from
in a new session. The detail is in `docs/ARCHITECTURE-CONCEPT.md` (the concept), `CHANGELOG.md` (what was built) and
`CLAUDE.md` (the rules for working on it).

**Current rule from the owner:** work on architecture and concept only. No changes to the add-in or the MCP server
until the owner says so.

## 1. What it is

A Revit 2025 add-in plus an MCP server that lets Claude (Claude Desktop) work inside Revit safely: read the model,
preview changes with pictures, apply them only after approval (one undo step), and run coordination, auditing and
production tools. It currently carries the ACE brand. The owner intends to **rename it and continue it as a personal
tool** (see `docs/NAMING.md`).

## 2. What is built, and how far it is verified

"Live" means it ran in Revit on a real model. "Offline" means it builds and its tests pass here, but it has not run in
Revit.

| Capability | Status |
|---|---|
| Bridge, safety gate (preview before apply), journal, one undo step per change | Live |
| Companion panel (approvals with Apply / Cancel, activity, results, prompts) | Live (1.6.0); the redesign (Home, quick actions, Ask Claude) is offline and not in the last package |
| Preview pictures of a change (plan + 3D) | Live |
| Office test fits (desks placed per m² per person, options applied one by one) | Live, shown to management on 29 Sep |
| Insights dashboard (score, checks, HTML report, in Revit) | Live (1.6.0); the Model Checker-style checks and check set are offline |
| Branded ribbon with live and planned tools, bevelled black / white icons | Live (owner confirmed the icons work) |
| Installer, doctor, issue reports, team package | Live (work PC installed 1.7.0) |
| Model brief (levels, rooms, families, links and open models, alignment) | Offline |
| Lessons, learning report, benchmark | Offline (benchmark not yet run with Revit) |
| Change tracker (snapshots, what changed by whom, links too) | Offline |
| Clash detection: responsibility, issues, cause (who changed what), status between runs | Offline |
| Clash Browser and ACE Clash View (primary green, compared red, per-link colours, focus on one clash, intersection in gold) | Offline |
| Coordination report (picture per issue, CSV for Excel) | Offline |
| Structure from ARC (columns, load-bearing walls) | Offline |
| Worksets per the BEP (rules by category, function, level, zone, department) | Offline |
| Working modes (Model audit, Coordination, Production, Submission, All tools) | Offline |
| Official brand values (UAE Flag Red, Charcoal, greys, Poppins / BW Gradual) | Offline in the add-in; the brand guidelines file is kept out of git while the repository is public |

**Numbers:** 45 MCP tools, 12 built-in scripts, about 60 commits on `claude/brave-carson-vcjduc`. Version 1.7.0 (not yet
released as verified).

## 3. The owner's ideas, collected (in the order they came)

1. Clash detection to work with linked models, decide the responsible discipline, and let STR take its layout from ARC. *Built.*
2. The learning loop must run with everything and keep improving. *Built and written into `CLAUDE.md`.*
3. A change tracker. *Built.*
4. Dashboards for the status of all tools (health, clashes), in HTML or in Revit. *Built.*
5. A clash view like Navisworks / ACC: two models in colour, a clash browser, dim everything else, zoom to the
   intersection even on large elements. *Built.* Then: the owner's model is always primary; BIM managers can compare
   link against link. *Built.* Primary **green**, other **red**, and a colour choice per link. *Built.*
6. Model health checks after the Revit interoperability tools (Model Checker). *Built.*
7. Working modes (mass production, clash detection, auditing...) that focus, dim or hide tools. *Built.*
8. Workset assignment by category, function and location, as per the BEP. *Built.*
9. The Companion needs better content, abilities and design. *Built (offline).*
10. BIM requirements analysis (BEP, LOD, LOIN, best practice) with recommendations, actions and a **contradiction
    monitor**. *Concept (section 5).*
11. Every tool studied with the others: compatibility, efficiency, no duplication, merges into stronger tools
    ("points form circles, circles form bigger ones"). *Concept (sections 1, 3, 4, 9).*
12. A **BIM Tools** group on the ribbon. *Concept (section 6).*
13. All analyses, rules, conclusions, models and documents in **one container** opened from the ribbon, editable by
    clicking a point where that is efficient. *Concept (section 7, Project Hub).*
14. Requirements findings raised as **notifications, like Revit warnings**. *Concept (section 8).*
15. A reminder to make a **presentation** of the hidden capabilities when something is really strong. *Recorded
    (`CLAUDE.md` and concept section 11).*
16. **Creative names**, and a future rename away from ACE, as a personal tool. *`docs/NAMING.md`.*
17. A **family creator and checker**. *Concept (section 12): the checker checks the family standard and LOIN in the
    project, in the family editor or on a library folder; the creator drafts, checks, flexes and previews before saving.*

## 4. Decisions and constraints to keep

- No features that add running costs.
- Never launch Revit remotely (licensing). Never close Revit with unsaved work without asking.
- Every change is previewed, confirmed and one undo step. Read-only means rolled back.
- Brand: ACE lettermark only, never recoloured; red as an accent; British spelling; no emojis. The logo files and the
  brand guidelines stay out of git while the GitHub repository is **public** (owner chose to make it private; not done
  yet).
- Colours in the clash view follow the role: primary green, compared red, each link its own colour.
- Develop on `claude/brave-carson-vcjduc`; no pull request unless asked.

## 5. Open items

| Item | Needs |
|---|---|
| Verify 1.7.0 live: brief, change tracker, clashes and Clash Browser, coordination report, model check, worksets, modes, new Companion | Revit with a real project that has linked ARC / STR / MEP models; send screenshots and errors |
| Rebuild the package with the latest Companion and colours | Owner's go-ahead (add-in changes are paused) |
| Make the repository private, then commit the brand guidelines and the lettermark | Owner changes the visibility on GitHub |
| Home PC: Desktop Commander offline since 28 Sep | Open the Desktop Commander app and sign in |
| A real BEP / LOIN matrix to test the requirements structure against | Owner sends one |
| Choose the product name | `docs/NAMING.md` |

## 6. Next steps (when the owner says go)

The order is from concept section 10:

1. **Verify 1.7.0 live and fix** what that shows. Then remind the owner about the first presentation (the coordination
   circle).
2. **Quick merges and the status store** (D3, D5, D6, D8, D9).
3. **Rules engine and the project requirements file** (D2, D7).
4. **Project Hub, requirements analyser, contradiction monitor, notifications, BIM Tools group.**
5. **Issue view engine and BCF export** (D4).
6. **Rule packs** for submission, design and code checks (D11, D12).
7. **Rename** (see `docs/NAMING.md`), ideally before step 4, so the new parts carry the new name from the start.
