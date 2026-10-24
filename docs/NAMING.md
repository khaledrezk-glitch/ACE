# Naming: a new name for the tool, and names for its parts

The tool carries the ACE brand today. The owner plans to rename it and continue it as a personal tool. These are
ideas to choose from. Before deciding, search each name for existing products and trademarks in the AEC and software
space; that search has not been done here.

## 1. Product name ideas

The strongest names echo the idea at the heart of the tool: **points that join into circles, and circles into bigger
ones**. The ones that also come from architecture or BIM tell users what it is for.

| Name | Meaning | Why it fits | Watch out |
|---|---|---|---|
| **Constella** | From *constellation*: separate stars that form a figure | Exactly the concept: capabilities are points, and together they form shapes you can name | Invented word; easy to own |
| **Orbit** | Things circling a centre | Tools and models circling the project; the "circles" idea | Common word; many products use it |
| **Datum** | The reference everything is measured from (levels, grids, survey point) | Every model, rule and finding is aligned to one reference; a Revit and BIM word users know | Used by some data products |
| **Concord** | Agreement, harmony | Models that fit together, requirements without contradictions | Also a place and an aircraft |
| **Tessera** | One tile of a mosaic | Many small tiles (tools) forming a picture (the project) | Less obvious meaning |
| **Keystone** | The stone that locks an arch | Architectural; the piece that holds the rest together | Common in business names |
| **Axon** | The link between nerve cells | Connections that carry signals: the notifications and links between points | Medical association |
| **Loom** | Weaves threads into cloth | Weaving models, documents and rules into one fabric | Loom is also a video product |

**Recommendation:** **Constella**. It is distinctive, easy to protect, and says the concept in one word: many points
forming meaningful shapes. **Datum** is the best alternative if you want a BIM word the whole team understands at once.

Tagline ideas:

- "Every point connected."
- "Your models, in one constellation."
- "Where BIM comes together."

## 2. Names for the parts

Names for the parts make a demo memorable and help people remember which tool does what. Here are two sets. Each
keeps the plain description beside it, so the ribbon stays clear.

| Part today | Set A: *Constella* (sky) | Set B: *Datum* (architecture) | Plain label (always shown) |
|---|---|---|---|
| Companion panel | **Guide** | **Assistant** | Companion |
| Project Hub (the container) | **Sky** | **Vault** | Project Hub |
| Map view of points and links | **Star map** | **Blueprint** | Project map |
| Model check / dashboard | **Pulse** | **Survey** | Model check |
| Clash Browser | **Lens** | **Section** | Clash Browser |
| Clash view (focus one clash) | **Focus** | **Detail** | Clash view |
| Coordination report | **Minutes** | **Minutes** | Coordination report |
| Change tracker | **Trail** | **Trace** | Change tracker |
| Model brief (reads the models) | **Scout** | **Scout** | Model brief |
| Requirements hub (BEP, LOD, LOIN) | **Charter** | **Codex** | Requirements |
| Contradiction monitor | **Sentinel** | **Sentinel** | Contradiction monitor |
| Notifications | **Signals** | **Flags** | Notifications |
| Working modes | **Phases** | **Stances** | Work mode |
| Learning loop | **Evolve** | **Evolve** | Learning |
| Test fit | **Fit** | **Fit** | Test fit |
| Structure from ARC | **Derive** | **Derive** | Structure from ARC |
| Worksets per BEP | **Sort** | **Sort** | Worksets |
| Family creator | **Forge** | **Mould** | Family Creator |
| Family checker | **Assay** | **Gauge** | Family Checker |

Use the part names in presentations and the Companion. On the ribbon, lead with the plain label and add the part name
in the tooltip, so nobody has to learn a vocabulary to use the tools.

## 3. What a rename touches

The ACE brand is already switchable in one place (`branding/brand.json`: name, colours, fonts, logo). A full rename also
touches these technical names. It should be done once, in one release, with an installer that migrates the old settings:

| Where | Today | Notes |
|---|---|---|
| Ribbon tab, window titles, Companion title | "ACE" (from `brand.json`) | Already configurable |
| MCP server name in Claude's config | `ace-revit` | Installer must remove the old entry and add the new one, without touching other servers |
| Settings folder | `%APPDATA%\ACE-RevitMCP` | Move it on first start; keep a pointer from the old path for one version |
| Add-in and namespaces | `AceRevitMcp` (.NET assembly, `.addin` file, ExternalCommand class names) | Ribbon commands are addressed by class name: rename in one go and rebuild |
| Package, installer, doctor, Start menu | `ACE-RevitMCP-<version>.zip`, "ACE Revit MCP" | Straightforward |
| Documents | README, USER-GUIDE, AGENT-GUIDE, CHANGELOG, CLAUDE.md | Keep the history in CHANGELOG ("formerly ACE Revit MCP") |
| Repository | `khaledrezk-glitch/ACE` | GitHub redirects the old URL after a rename |
| Brand assets | ACE lettermark, ACE colours and fonts | A personal version needs its own logo and palette; the ACE assets belong to ACE |

## 4. Before making it personal

The tool has been developed for use at ACE and with ACE material (brand, sample projects, office standards). Before
continuing it as a personal product, check the employment terms on ownership of tools made at or for work. Keep ACE's
brand assets and internal documents out of the personal version, and agree with ACE how its team can keep using the
tool.
