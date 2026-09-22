# 00_PROJECT_INTAKE.md
ACE | Al Ain Consulting Engineers – Contractor Technical Proposal Evaluation Support
Phase: INITIAL SETUP / DISCOVERY
Prepared by: Claude Code (working file – not a client deliverable)
Date: 2026-09-22
Status: **INCOMPLETE – master folder not yet accessible from the environment in which this file was produced**

---

## A. Project purpose

ACE acts as consultant on behalf of a client. For certain projects the client invites construction contractors to tender, and ACE must review and technically evaluate the contractors' submitted proposals on the client's behalf, recording the evaluation in the **client's mandatory Excel evaluation workbook**. ACE may later provide construction supervision on the same project.

This evaluation support system is intended to help ACE complete the client's required evaluation **more quickly, more consistently and more defensibly**, for repeated projects belonging to the same client. Its guiding principles are:

- The client's Excel workbook and the client's criteria are the governing evaluation instrument. No parallel scoring system is to be created and the client's criteria are not to be redesigned.
- Every material evaluation finding must be traceable to contractor evidence (document, section, page, what was actually provided, ACE's interpretation).
- Historical ACE evaluations are precedent, not rules. Hierarchy: current client criteria → current project conditions → current contractor evidence → ACE precedent → consistency improvements.
- Contractor fit is assessed against the current project (relevant experience, personnel, methodology, programme, resources), justified only through the client's criteria and the submitted evidence. No arbitrary preference.
- Submission completeness is checked; treatment of missing documents is derived from the client documents, not invented.
- AI assists the ACE evaluator. Claude Code handles ingestion, classification, extraction, indexing and gap-finding. ChatGPT handles evaluation architecture, criteria comparison and precedent analysis. The ACE human evaluator retains professional judgment, validation and final responsibility.
- Source files are read-only. All generated analysis lives in a clearly separate working area, in lightweight Markdown/CSV/JSON, with original filenames preserved for auditability.

## B. Current folder map

**Stated master folder (local computer):** `D:\Technical Evaluation`

**Environment in which this intake was produced:** a remote cloud container holding a fresh clone of the GitHub repository `khaledrezk-glitch/ACE`, branch `claude/trusting-turing-ijcnib`.

The local `D:` drive is **not mounted or reachable** from this container. No attachment or user-data mounts contained any files. The repository itself contained only:

```
ACE/
└── README.md        (one line: "# ACE")
```

Therefore **no client, contractor or precedent documents have been inspected**. The folder map of `D:\Technical Evaluation` is unknown and has not been assumed.

Working area created by this run (the only generated content):

```
ACE/
└── 90_WORKING/
    ├── 00_PROJECT_INTAKE.md   (this file)
    └── 00_WORKING_LOG.md
```

No other folders (01_CLIENT, 02_CONTRACTORS, 03_ACE_PRECEDENT, 99_OUTPUT) were created, in line with the instruction not to build structures before seeing the actual documents and not to impose a structure on an existing one.

## C. Documents currently identified

| Group | Documents identified | Notes |
|---|---|---|
| Client baseline (requirements, scope/TOR, tender documents, addenda, required submission list, scoring definitions) | None | Master folder not accessible |
| Evaluation workbook (client's mandatory Excel) | None | Presence cannot be confirmed |
| Contractor submissions | None | Master folder not accessible |
| Previous ACE evaluations (precedent: criteria + contractor proposal + completed ACE evaluation) | None | Master folder not accessible |
| Unknown / needs classification | None | Nothing to classify |

## D. Initial observations

Factual only:

1. The instruction set describes a local master folder at `D:\Technical Evaluation`; the run was executed in a cloud environment with no access to that path.
2. The GitHub repository `khaledrezk-glitch/ACE` is effectively empty (single-line README, one commit). It has not been used to hold any source documents so far.
3. No source files were read, modified, renamed or moved during this run, because none were available.
4. The project brief itself (section 1–15 of the instructions) is the only information available about the client process. It states that the client's Excel workbook governs, that BIM/ISO 19650 are out of scope unless a tender includes them, and that scoring rules, compliance classifications and missing-document treatment are to be derived from the client documents. None of these can yet be verified against actual documents.

## E. Missing information

The following major document groups have **not been provided** to this environment. They are listed as *not yet available*, not as confirmed absent from `D:\Technical Evaluation`:

- Client project/tender requirements and scope / Terms of Reference.
- Client list of required contractor submission documents.
- Client mandatory Excel evaluation workbook (with its criteria, weightings, scoring definitions and any mandatory fields).
- Tender addenda / clarifications.
- Contractor technical proposals (one folder per contractor).
- Historical ACE precedent sets (client criteria + contractor proposal + completed ACE evaluation).
- Any existing ACE working notes or partial evaluations.

Open ambiguity for review: **how the master folder will be made available to Claude Code.** Options are (a) run Claude Code locally on the machine holding `D:\Technical Evaluation`, or (b) place the source documents (or a representative subset) in this repository / a shared location reachable from the cloud environment. Option (a) is the closest match to the "Master Folder Principle" in the brief. This decision belongs to ACE.

## F. Recommended next analysis step

Based only on what is actually present (nothing), the next step is **not analytical**; it is access:

1. Make `D:\Technical Evaluation` available to Claude Code, preferably by running Claude Code locally with that folder as the working directory, so that source files remain in place and read-only.
2. Re-run this discovery step against the real folder: inspect structure, list and high-level-classify files (client baseline / Excel workbook / contractor submissions / ACE precedent / unknown), record scale (file counts, page counts, scanned vs text PDFs, Excel files), and update this intake file and the working log accordingly.
3. Only after that: examine the client's Excel evaluation workbook first (structure, sheets, criteria, scoring definitions, mandatory fields, compliance classifications), since it is the governing instrument and will determine everything downstream.

No contractor analysis, scoring, workbook completion or methodology design has been started.
