using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;
using ImageSource = System.Windows.Media.ImageSource;

namespace AceRevitMcp.Ribbon
{
    internal enum PanelLook { Focus, Dim, Hide }

    /// <summary>A working mode: which ribbon panels lead, which are dimmed and which are hidden, and what Claude and the Companion put first.</summary>
    internal sealed class WorkMode
    {
        public string Key, Label, Summary, Glyph;
        public Dictionary<string, PanelLook> Panels = new Dictionary<string, PanelLook>();
        /// <summary>Tools Claude should lead with in this mode.</summary>
        public string[] ClaudeTools = new string[0];
        /// <summary>Companion prompts for this mode (label, prompt), shown first.</summary>
        public (string Label, string Prompt)[] Prompts = new (string, string)[0];
        public PanelLook LookOf(string panel) => Key == "all" ? PanelLook.Focus : Panels.TryGetValue(panel, out var l) ? l : PanelLook.Dim;
    }

    /// <summary>
    /// Working modes (Model audit, Coordination, Production, Submission, All tools): the ribbon shows the panels for the
    /// task in full, dims the ones that are less relevant (faded icons, still usable) and hides the unrelated ones;
    /// the Companion and Claude put the mode's tools first. The choice is remembered per user.
    /// </summary>
    internal static class WorkModes
    {
        private static readonly string[] AlwaysShown = { "Work mode", "Claude MCP", "Roadmap" };
        private const string ro = " Read-only, don't change anything.";
        private const string gated = " Explain your plan first and show me a preview before applying anything.";

        internal static readonly WorkMode[] All =
        {
            new WorkMode { Key = "all", Label = "All tools", Glyph = Glyphs.Grid, Summary = "Every ACE tool, nothing dimmed." },
            new WorkMode
            {
                Key = "audit", Label = "Model audit", Glyph = Glyphs.Gauge,
                Summary = "Model health, QA and standards: the dashboard, audit and inspection tools lead.",
                Panels = { ["Insights"] = PanelLook.Focus, ["Audit"] = PanelLook.Focus, ["Inspect"] = PanelLook.Focus, ["Compliance"] = PanelLook.Focus,
                           ["Data"] = PanelLook.Dim, ["Export"] = PanelLook.Dim, ["Coordination"] = PanelLook.Dim, ["Deliver"] = PanelLook.Dim,
                           ["Team Tools"] = PanelLook.Hide, ["Admin"] = PanelLook.Hide },
                ClaudeTools = new[] { "model_dashboard", "list_warnings", "find_elements", "get_element_details", "describe_category", "set_parameters" },
                Prompts = new[]
                {
                    ("Run the model check", "Using ace-revit: run the model check (model_dashboard, show: true) and explain the five findings that matter most, with what to do about each." + ro),
                    ("Worst warnings first", "Using ace-revit: which Revit warnings should we fix first and why? Group them by type and tell me which ones distort quantities or rooms." + ro),
                    ("Fix duplicate marks", "Using ace-revit: find duplicate door and window marks and propose a renumbering." + gated),
                    ("Worksets against the BEP", "Using ace-revit: check which elements are on the wrong workset according to our BEP rules (assign_worksets, check_only: true), by category and workset." + ro),
                    ("Unused content to purge", "Using ace-revit: list unused family types, view templates and single-use groups that could be purged before issue." + ro),
                },
            },
            new WorkMode
            {
                Key = "coordination", Label = "Coordination", Glyph = Glyphs.Clash,
                Summary = "Linked models and clashes: the Clash Browser, clash results, change tracker and coordination report lead.",
                Panels = { ["Coordination"] = PanelLook.Focus, ["Insights"] = PanelLook.Focus, ["Inspect"] = PanelLook.Focus,
                           ["Audit"] = PanelLook.Dim, ["Data"] = PanelLook.Dim, ["Export"] = PanelLook.Dim,
                           ["Deliver"] = PanelLook.Hide, ["Compliance"] = PanelLook.Hide, ["Team Tools"] = PanelLook.Hide, ["Admin"] = PanelLook.Hide },
                ClaudeTools = new[] { "run_clash_test", "clash_view", "coordination_report", "model_changes", "get_model_brief" },
                Prompts = new[]
                {
                    ("Check the links line up", "Using ace-revit: do the linked models line up with this one (levels, grids, coordinates)? Tell me anything that would make clash results unreliable." + ro),
                    ("Clash against a link, walk me through", "Using ace-revit: run the clash tests between this model and the MEP link, then show me the top issues one by one in the clash view (clash_view with each key), with who should fix each." + ro),
                    ("What changed in the links", "Using ace-revit: what changed in the linked models since last week, and which new clashes did those changes cause?" + ro),
                    ("Prepare the coordination report", "Using ace-revit: prepare the coordination report for the meeting (coordination_report, open: true) and summarise the actions per discipline." + ro),
                },
            },
            new WorkMode
            {
                Key = "production", Label = "Production", Glyph = Glyphs.Sheets,
                Summary = "Mass production: data, sheets, views and exports lead; review tools are dimmed.",
                Panels = { ["Data"] = PanelLook.Focus, ["Export"] = PanelLook.Focus, ["Inspect"] = PanelLook.Focus,
                           ["Insights"] = PanelLook.Dim, ["Audit"] = PanelLook.Dim, ["Deliver"] = PanelLook.Dim,
                           ["Coordination"] = PanelLook.Hide, ["Compliance"] = PanelLook.Hide, ["Team Tools"] = PanelLook.Hide, ["Admin"] = PanelLook.Hide },
                ClaudeTools = new[] { "set_parameters", "execute_revit_code", "run_saved_script", "assign_worksets", "list_views", "find_elements", "select_elements" },
                Prompts = new[]
                {
                    ("Create sheets for all plans", "Using ace-revit: create a sheet for every floor plan that is not on a sheet yet, using our title block and numbering." + gated),
                    ("Renumber rooms by level", "Using ace-revit: renumber the rooms level by level (e.g. 301, 302...) from left to right." + gated),
                    ("Fill empty parameters", "Using ace-revit: find doors, windows and rooms with empty key parameters and propose values from their type or neighbours." + gated),
                    ("Batch-rename views", "Using ace-revit: rename the views to our naming standard (level - discipline - purpose) and show me the list first." + gated),
                    ("Assign worksets per the BEP", "Using ace-revit: put the elements on the right worksets per our BEP rules (assign_worksets): first check only and show me what would move, then preview." + gated),
                },
            },
            new WorkMode
            {
                Key = "submission", Label = "Submission", Glyph = Glyphs.Package,
                Summary = "Before issue: deliver, audit, compliance and export tools lead.",
                Panels = { ["Deliver"] = PanelLook.Focus, ["Audit"] = PanelLook.Focus, ["Compliance"] = PanelLook.Focus, ["Export"] = PanelLook.Focus, ["Insights"] = PanelLook.Focus,
                           ["Data"] = PanelLook.Dim, ["Coordination"] = PanelLook.Dim, ["Inspect"] = PanelLook.Dim,
                           ["Team Tools"] = PanelLook.Hide, ["Admin"] = PanelLook.Hide },
                ClaudeTools = new[] { "model_dashboard", "list_views", "find_elements", "set_parameters", "coordination_report" },
                Prompts = new[]
                {
                    ("Is this model ready to issue?", "Using ace-revit: is this model ready to issue? Check title block data, project information, empty sheets, views not on sheets, warnings and open clashes, and give me a go / no-go list." + ro),
                    ("Missing title block data", "Using ace-revit: which sheets miss issue date, drawn, checked or approved by? Propose values." + gated),
                    ("Clean up before issue", "Using ace-revit: what should be purged or removed before issue (CAD imports, unused views and types, images)?" + ro),
                },
            },
        };

        private sealed class Button
        {
            public string Panel;
            public RibbonButton Item;
            public Func<int, bool, ImageSource> Icon;
            public string Tip;
            public bool? Dimmed;   // the icon state last drawn, so a mode switch only redraws buttons that change
        }

        private static readonly Dictionary<string, RibbonPanel> Panels = new Dictionary<string, RibbonPanel>();
        private static readonly List<Button> Buttons = new List<Button>();
        private static ComboBox _combo;
        private static bool _updating;

        internal static WorkMode Current { get; private set; } = All[0];
        internal static event Action Changed;

        private static string StateFile => Path.Combine(AceConfig.Directory, "work-mode.txt");

        internal static void RegisterPanel(RibbonPanel p) { if (p != null) Panels[p.Name] = p; }

        /// <summary>Remembers a button so its icon can be faded when its panel is dimmed.</summary>
        internal static void RegisterButton(string panel, RibbonItem item, Func<int, bool, ImageSource> icon)
        {
            if (item is RibbonButton b) Buttons.Add(new Button { Panel = panel, Item = b, Icon = icon, Tip = b.ToolTip });
        }

        /// <summary>The mode selector at the start of the ACE tab.</summary>
        internal static void CreateSelector(RibbonPanel panel)
        {
            RegisterPanel(panel);
            var data = new ComboBoxData("AceWorkMode") { ToolTip = "Working mode: shows the tools for the task you are doing, dims the rest and hides unrelated panels. Claude and the Companion follow the mode." };
            _combo = panel.AddItem(data) as ComboBox;
            if (_combo == null) return;
            foreach (var m in All)
            {
                var member = new ComboBoxMemberData("AceMode_" + m.Key, m.Label) { ToolTip = m.Summary };
                try { member.Image = Icons.Line(16, m.Glyph, planned: false); } catch { }
                _combo.AddItem(member);
            }
            _combo.CurrentChanged += (s, e) =>
            {
                if (_updating) return;
                var key = e.NewValue?.Name?.Replace("AceMode_", "");
                if (key != null) Apply(key);
            };
        }

        /// <summary>Restores the last mode (after the ribbon is built).</summary>
        internal static void Restore()
        {
            var key = "all";
            try { if (File.Exists(StateFile)) key = File.ReadAllText(StateFile).Trim(); } catch { }
            Apply(key, save: false);
        }

        internal static WorkMode Find(string keyOrLabel) =>
            All.FirstOrDefault(m => string.Equals(m.Key, keyOrLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Label, keyOrLabel, StringComparison.OrdinalIgnoreCase)
                                    || (keyOrLabel ?? "").IndexOf(m.Key, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Applies a mode to the ribbon (must run on Revit's UI thread).</summary>
        internal static WorkMode Apply(string key, bool save = true)
        {
            var mode = Find(key) ?? All[0];
            Current = mode;
            foreach (var (name, p) in Panels)
            {
                var look = AlwaysShown.Contains(name) ? PanelLook.Focus : mode.LookOf(name);
                try { p.Visible = look != PanelLook.Hide; } catch { }
            }
            foreach (var b in Buttons)
            {
                var look = AlwaysShown.Contains(b.Panel) ? PanelLook.Focus : mode.LookOf(b.Panel);
                if (look == PanelLook.Hide) continue;
                var dim = look == PanelLook.Dim;
                try
                {
                    if (b.Icon != null && b.Dimmed != dim)
                    {
                        b.Item.LargeImage = b.Icon(32, dim);
                        b.Item.Image = b.Icon(16, dim);
                        b.Dimmed = dim;
                    }
                    b.Item.ToolTip = dim ? $"(Less used in {mode.Label} mode) {b.Tip}" : b.Tip;
                }
                catch (Exception ex) { Log.Warn($"Work mode icon: {ex.Message}"); }
            }
            if (_combo != null)
            {
                _updating = true;
                try
                {
                    var member = _combo.GetItems().FirstOrDefault(i => i.Name == "AceMode_" + mode.Key);
                    if (member != null && _combo.Current?.Name != member.Name) _combo.Current = member;
                }
                catch { }
                finally { _updating = false; }
            }
            if (save) try { File.WriteAllText(StateFile, mode.Key); } catch { }
            try { Changed?.Invoke(); } catch { }
            return mode;
        }

        /// <summary>Bridge "working_mode": { mode } sets it (optional); returns the current mode and what it prioritises.</summary>
        public static JsonNode Command(UIApplication app, JsonObject args)
        {
            var requested = Commands.Args.Str(args, "mode");
            if (requested != null)
            {
                if (Find(requested) == null) throw new Bridge.CommandException($"Unknown mode '{requested}'. Modes: {string.Join(", ", All.Select(m => m.Label))}.");
                Apply(requested);
            }
            return Describe(Current);
        }

        internal static JsonObject Describe(WorkMode m)
        {
            var panels = Roadmap.Panels.Concat(new[] { "Insights" }).ToList();
            return new JsonObject
            {
                ["mode"] = m.Label, ["summary"] = m.Summary,
                ["focus"] = new JsonArray(panels.Where(p => m.LookOf(p) == PanelLook.Focus).Select(p => (JsonNode)p).ToArray()),
                ["dimmed"] = new JsonArray(panels.Where(p => m.LookOf(p) == PanelLook.Dim).Select(p => (JsonNode)p).ToArray()),
                ["hidden"] = new JsonArray(panels.Where(p => m.LookOf(p) == PanelLook.Hide).Select(p => (JsonNode)p).ToArray()),
                ["leadWith"] = new JsonArray(m.ClaudeTools.Select(t => (JsonNode)t).ToArray()),
                ["modes"] = new JsonArray(All.Select(x => (JsonNode)x.Label).ToArray()),
            };
        }
    }
}
