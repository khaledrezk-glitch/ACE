using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Ribbon
{
    /// <summary>A ribbon tool: live today, or planned (the button opens a card that explains what it will do).</summary>
    internal sealed class Feature
    {
        public string Key, Panel, Label, Summary, Glyph;
        public int Phase;
        public bool Large = true;
        public string[] WillDo = new string[0];
        /// <summary>What a user can already do today by asking Claude, and a prompt to paste (optional).</summary>
        public string TodayWithClaude, Prompt;
        public string Note;
    }

    internal static class Roadmap
    {
        public static readonly Dictionary<int, string> Phases = new Dictionary<int, string>
        {
            [1] = "Usage control and ribbon foundation",
            [2] = "Model audit and warning solver",
            [3] = "Built-in equivalents of well-known add-ins",
            [4] = "Clash detection",
            [5] = "BIM submission preparation",
            [6] = "Design checks and code compliance",
        };

        /// <summary>Ribbon panels in display order.</summary>
        public static readonly string[] Panels = { "Audit", "Inspect", "Data", "Export", "Coordination", "Deliver", "Compliance", "Team Tools", "Admin" };

        public static readonly Feature[] Features =
        {
            // ---- Claude panel (next to Companion and MCP Status) ----
            new Feature { Key = "undo", Panel = "Claude MCP", Label = "Undo Claude\nChange", Phase = 1, Glyph = Glyphs.Undo,
                Summary = "Undo Claude's latest change with one click, only when it really is the latest change in the model.",
                WillDo = new[] { "Checks that nobody changed the model after Claude's change.", "Undoes it as one step and records it in the activity journal." },
                TodayWithClaude = "Ask Claude to undo its last change. It refuses if you changed anything afterwards.",
                Prompt = "Undo your last change in Revit, and tell me exactly what was reverted." },

            // ---- Audit ----
            new Feature { Key = "health", Panel = "Audit", Label = "Model\nHealth", Phase = 2, Glyph = Glyphs.Gauge,
                Summary = "A health score from 0 to 100 for the open model, with a report of every finding.",
                WillDo = new[] {
                    "Scores warnings, CAD imports, in-place families, unused items, views not on sheets, unplaced rooms and more.",
                    "Checks naming (views, sheets, levels, families) and required parameters against the ACE standard.",
                    "Keeps the score per model over time, so progress is visible.",
                    "HTML, Excel and Markdown reports; click a finding to select and zoom to the elements." },
                TodayWithClaude = "A first version is live: ACE tab > Insights > Dashboard shows a health score with every finding. Claude can also explain it and help fix the issues.",
                Prompt = "Audit this Revit model: give me an overview, the main problems ranked by importance, and what to fix first." },
            new Feature { Key = "warnings", Panel = "Audit", Label = "Warning Solver", Large = false, Phase = 2, Glyph = Glyphs.Warning,
                Summary = "Groups the model's warnings and fixes the ones that can be fixed safely.",
                WillDo = new[] {
                    "Safe automatic fixes, always previewed: duplicate marks, identical instances in the same place, overlapping room separation lines, slightly off-axis walls.",
                    "Warnings that need a person: an explained list, with the elements selected and a view zoomed to them." },
                TodayWithClaude = "Claude can list and group warnings and explain them.",
                Prompt = "List the warnings in this model grouped by type, with counts, and tell me which ones are safe to fix automatically." },
            new Feature { Key = "params", Panel = "Audit", Label = "Parameter Check", Large = false, Phase = 2, Glyph = Glyphs.Checklist,
                Summary = "Checks that required parameters are filled in, per category.",
                WillDo = new[] { "Uses the ACE required-parameter list.", "Shows % filled per category and the elements that are missing values." },
                TodayWithClaude = "Claude runs the built-in parameter_completeness script.",
                Prompt = "Check parameter completeness for doors, windows and rooms, and list the elements missing values." },
            new Feature { Key = "roomsdoors", Panel = "Audit", Label = "Rooms && Doors QA", Large = false, Phase = 2, Glyph = Glyphs.Plan,
                Summary = "Quick QA of rooms and doors: rooms without doors, door widths, unplaced or unenclosed rooms.",
                WillDo = new[] { "Rooms with no door, doors narrower than a set width, unplaced and unenclosed rooms.", "Results listed by level; click to select." },
                TodayWithClaude = "Claude runs the built-in rooms_without_doors and door_width_check scripts.",
                Prompt = "Find rooms without doors and doors narrower than 900 mm, listed by level." },

            // ---- Inspect (RevitLookup-like) ----
            new Feature { Key = "snoopsel", Panel = "Inspect", Label = "Snoop\nSelection", Phase = 3, Glyph = Glyphs.Search,
                Summary = "Inspect the selected elements in depth, like RevitLookup.",
                WillDo = new[] { "Properties, parameters, geometry, sub-elements, dependents, schemas and API types.", "Search and copy any value." },
                TodayWithClaude = "Claude can read and explain any element's parameters and properties.",
                Prompt = "Explain the selected elements: category, family and type, key parameters, and anything unusual." },
            new Feature { Key = "snoopdoc", Panel = "Inspect", Label = "Snoop Document", Large = false, Phase = 3, Glyph = Glyphs.DocSearch,
                Summary = "Inspect the whole document: project information, settings, links, worksets and more.",
                WillDo = new[] { "Browse the document and the Revit application objects.", "Useful for troubleshooting families and data." } },
            new Feature { Key = "events", Panel = "Inspect", Label = "Event Monitor", Large = false, Phase = 3, Glyph = Glyphs.Pulse,
                Summary = "Watch what changes in the model live: added, modified and deleted elements.",
                WillDo = new[] { "A live log of model changes and Revit events.", "Helps find what a tool or a colleague's action changed." } },

            // ---- Data (DiRoots SheetLink / ParaManager / OneFilter-like) ----
            new Feature { Key = "xlsout", Panel = "Data", Label = "Export\nto Excel", Phase = 3, Glyph = Glyphs.ExcelOut,
                Summary = "Export chosen categories and parameters to Excel for editing.",
                WillDo = new[] { "Pick categories, parameters and filters; export to .xlsx.", "Saved export profiles for common schedules." } },
            new Feature { Key = "xlsin", Panel = "Data", Label = "Import\nfrom Excel", Phase = 3, Glyph = Glyphs.ExcelIn,
                Summary = "Bring edited Excel data back into the model, with a preview of every change.",
                WillDo = new[] { "Shows each value that would change before anything is applied.", "Applied as one undo step and recorded in the journal." } },
            new Feature { Key = "parammgr", Panel = "Data", Label = "Parameter Manager", Large = false, Phase = 3, Glyph = Glyphs.Sliders,
                Summary = "Create and bind shared and project parameters in bulk from a list.",
                WillDo = new[] { "Load a list of parameters, groups and categories, and bind them in one step.", "Checks for duplicates and conflicts first." } },
            new Feature { Key = "smartsel", Panel = "Data", Label = "Smart Select", Large = false, Phase = 3, Glyph = Glyphs.Select,
                Summary = "Select elements by any parameter or rule.",
                WillDo = new[] { "Rules like 'doors on Level 2 with Fire Rating empty'.", "Save and reuse selection rules." },
                TodayWithClaude = "Ask Claude to find and select elements by any rule, in plain words.",
                Prompt = "Select all doors on Level 1 whose Fire Rating is empty." },

            // ---- Export (ProSheets-like) ----
            new Feature { Key = "batchexport", Panel = "Export", Label = "Batch\nExport", Phase = 3, Glyph = Glyphs.Sheets,
                Summary = "Export sheet and view sets to PDF, DWG, IFC or NWC in one go.",
                WillDo = new[] { "Naming rules such as {Project}-{Sheet Number}-{Rev}.", "Combined or separate PDFs; files written only to the configured exports folder.", "A transmittal log of what was exported." } },
            new Feature { Key = "exportprofiles", Panel = "Export", Label = "Export\nProfiles", Phase = 3, Glyph = Glyphs.Bookmark,
                Summary = "Saved export settings, shared with the team.",
                WillDo = new[] { "One profile per client or deliverable type.", "Shared through the team folder so everyone exports the same way." } },

            // ---- Coordination ----
            new Feature { Key = "clash", Panel = "Coordination", Label = "Run Clash\nTest", Phase = 4, Glyph = Glyphs.Clash,
                Summary = "Clash tests inside Revit between categories or linked models.",
                WillDo = new[] { "For example ducts against beams, or pipes against walls in the structural link.", "Tolerance and clearance rules (for example pipes within 50 mm of beams).", "A fast bounding-box pass, then exact solid intersection." } },
            new Feature { Key = "clashresults", Panel = "Coordination", Label = "Clash\nResults", Phase = 4, Glyph = Glyphs.ClashList,
                Summary = "Review clashes by level or zone and track their status between runs.",
                WillDo = new[] { "Status per clash: new, active, resolved, approved.", "A 3D section-box view per clash.", "HTML, Excel and BCF export for Navisworks and ACC users.", "Claude explains clashes and proposes fixes, applied only after preview and confirmation." } },

            // ---- Deliver ----
            new Feature { Key = "subcheck", Panel = "Deliver", Label = "Submission\nCheck", Phase = 5, Glyph = Glyphs.Clipboard,
                Summary = "Checks a model against the client's submission requirements before issue.",
                WillDo = new[] { "Minimum health score, required parameters and title-block data.", "Naming (for example ISO 19650) and sheet numbering.", "A configurable checklist per client." } },
            new Feature { Key = "subprep", Panel = "Deliver", Label = "Prepare Submission", Large = false, Phase = 5, Glyph = Glyphs.Package,
                Summary = "Prepares the submission on a detached copy, never the working model.",
                WillDo = new[] { "Purge, remove CAD imports and unused views, set the starting view.", "Exports the PDF, DWG, IFC, NWC and COBie sets." } },
            new Feature { Key = "transmittal", Panel = "Deliver", Label = "Transmittal", Large = false, Phase = 5, Glyph = Glyphs.Send,
                Summary = "A transmittal and submission report for each issue.",
                WillDo = new[] { "What was checked, what was exported, and file hashes.", "ACE-branded, ready to send." } },

            // ---- Compliance ----
            new Feature { Key = "designcheck", Panel = "Compliance", Label = "Design\nCheck", Phase = 6, Glyph = Glyphs.SetSquare,
                Summary = "Checks the design against rules: room sizes, door and corridor widths, stairs, travel distance and more.",
                WillDo = new[] { "Pass or fail per rule, with measured and required values.", "Views highlighting each failure; Excel and HTML reports.", "Results are advisory, not a certification." } },
            new Feature { Key = "codepacks", Panel = "Compliance", Label = "Code\nPacks", Phase = 6, Glyph = Glyphs.Book,
                Summary = "Rule packs per building code or client standard, authored and approved by ACE engineers.",
                WillDo = new[] { "For example an ACE standard pack and local authority packs.", "Every report states which pack and version it used." } },

            // ---- Team Tools (pyRevit-like) ----
            new Feature { Key = "teamtools", Panel = "Team Tools", Label = "Team\nScripts", Phase = 3, Glyph = Glyphs.Grid,
                Summary = "Any saved team script becomes a ribbon button, runnable without Claude.",
                WillDo = new[] { "An icon and a simple input form per script.", "Buttons update automatically from the team scripts folder.", "The best Claude workflows get promoted here for everyone." },
                TodayWithClaude = "Claude can list, explain and run the team's saved scripts.",
                Prompt = "List the saved scripts you can run in Revit and what each one does." },

            // ---- Admin ----
            new Feature { Key = "users", Panel = "Admin", Label = "Users &&\nRoles", Phase = 1, Glyph = Glyphs.User,
                Summary = "Decide who may use ACE and what each person may do.",
                WillDo = new[] { "Roles: viewer (questions only), editor (model changes), power, admin.", "Allow or block users and machines; switch features on or off.", "A kill switch turns ACE off for everyone." },
                Note = "Shown to administrators only when released." },
            new Feature { Key = "usage", Panel = "Admin", Label = "Usage Report", Large = false, Phase = 1, Glyph = Glyphs.Chart,
                Summary = "Who used ACE, on which models, for what, and with what result.",
                WillDo = new[] { "Usage per user, project and tool.", "Failures and the most common requests, to guide improvements." },
                Note = "Shown to administrators only when released." },
            new Feature { Key = "policy", Panel = "Admin", Label = "Publish Policy", Large = false, Phase = 1, Glyph = Glyphs.Shield,
                Summary = "Publish the signed access policy to every ACE PC.",
                WillDo = new[] { "Signed so it can't be edited to grant access.", "Works offline with a grace period; published to a network share, SharePoint or a web address." },
                Note = "Shown to administrators only when released." },
        };

        public static Feature Get(string key) => Features.First(f => f.Key == key);
    }

    /// <summary>24 × 24 line icons (drawn as strokes).</summary>
    internal static class Glyphs
    {
        public const string Undo = "M9,5 L4,10 L9,15 M4,10 H15 A5,5 0 0 1 15,20 H11";
        public const string Gauge = "M4,17 A8,8 0 1 1 20,17 M12,16 L16,9 M4,20 H20";
        public const string Warning = "M12,3 L22,20 H2 Z M12,9 V14 M12,17 V17.2";
        public const string Checklist = "M3,6 L5,8 L8,4.5 M11,6 H21 M3,12 L5,14 L8,10.5 M11,12 H21 M3,18 L5,20 L8,16.5 M11,18 H21";
        public const string Plan = "M3,3 H21 V21 H3 Z M3,12 H9 M13,12 H21 M12,3 V8 M13,12 A4,4 0 0 0 9,8";
        public const string Search = "M4,10 A6,6 0 1 0 16,10 A6,6 0 1 0 4,10 M14.5,14.5 L21,21";
        public const string DocSearch = "M12,21 H4 V3 H13 L18,8 V11 M13,3 V8 H18 M12,16 A3,3 0 1 0 18,16 A3,3 0 1 0 12,16 M17.2,18.2 L20.5,21.5";
        public const string Pulse = "M2,12 H6 L9,5 L13,19 L16,12 H22";
        public const string ExcelOut = "M3,4 H15 V20 H3 Z M3,9 H15 M3,14 H15 M9,4 V20 M17,12 H23 M20,9 L23,12 L20,15";
        public const string ExcelIn = "M9,4 H21 V20 H9 Z M9,9 H21 M9,14 H21 M15,4 V20 M1,12 H7 M4,9 L7,12 L4,15";
        public const string Sliders = "M3,6 H21 M3,12 H21 M3,18 H21 M8,4 V8 M16,10 V14 M11,16 V20";
        public const string Select = "M3,7 V3 H7 M10,3 H14 M17,3 H21 V7 M3,10 V14 M3,17 V21 H7 M11,11 L20,14 L16,16 L14,20 Z";
        public const string Sheets = "M4,8 H13 V21 H4 Z M8,4 H17 V8 M19,5 V13 M16,10 L19,13 L22,10";
        public const string Bookmark = "M6,3 H18 V21 L12,16.5 L6,21 Z";
        public const string Clash = "M3,3 H14 V14 H3 Z M10,10 H21 V21 H10 Z";
        public const string ClashList = "M3,3 H11 V11 H3 Z M7,7 H15 V15 H7 Z M13,19 H21 M18,11 H21 M18,15 H21";
        public const string Clipboard = "M6,4 H18 V22 H6 Z M9,2 H15 V6 H9 Z M9,14 L11,16.5 L15.5,11";
        public const string Package = "M3,7 L12,3 L21,7 V17 L12,21 L3,17 Z M3,7 L12,11 L21,7 M12,11 V21";
        public const string Send = "M2,11 L22,3 L15,21 L11,13 Z M11,13 L22,3";
        public const string SetSquare = "M3,21 V3 L21,21 Z M7,17 V12 L12,17 Z";
        public const string Book = "M12,6 A2,2 0 0 0 10,4 H3 V19 H10 A2,2 0 0 1 12,21 A2,2 0 0 1 14,19 H21 V4 H14 A2,2 0 0 0 12,6 Z M12,6 V21";
        public const string Grid = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z";
        public const string User = "M8,8 A4,4 0 1 0 16,8 A4,4 0 1 0 8,8 M4,21 A8,7 0 0 1 20,21";
        public const string Chart = "M3,21 H21 M7,21 V12 M12,21 V5 M17,21 V15";
        public const string Shield = "M12,2 L20,5 V11 C20,16 16.5,20 12,22 C7.5,20 4,16 4,11 V5 Z M9,12 L11,14 L15,10";
        public const string Dashboard = "M3,3 H10 V12 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,16 H10 V21 H3 Z";
        public const string Map = "M3,6 L9,3 L15,6 L21,3 V18 L15,21 L9,18 L3,21 Z M9,3 V18 M15,6 V21";
    }
}
