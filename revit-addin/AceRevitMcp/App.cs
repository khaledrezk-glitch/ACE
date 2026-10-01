using System;
using System.Collections.Generic;
using System.Linq;
using AceRevitMcp.Bridge;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp
{
    /// <summary>Revit add-in entry point: starts the local bridge so Claude can drive Revit.</summary>
    public sealed class App : IExternalApplication
    {
        internal static BridgeServer Server { get; private set; }
        internal static AceConfig Config { get; private set; }
        internal static RequestDispatcher Dispatcher { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Config = AceConfig.LoadOrCreate();
                Branding.Load();
                var dispatcher = new RequestDispatcher(new CommandRegistry());
                dispatcher.Initialize();
                Dispatcher = dispatcher;
                application.ControlledApplication.DocumentChanged += ChangeTracker.OnDocumentChanged;
                Tracking.ChangeTracking.Attach(application);
                Server = new BridgeServer(Config, dispatcher, application.ControlledApplication.VersionNumber);
                Server.Start();
                CreateRibbon(application);
                Scripting.CodeRunner.WarmUp();
                try { Companion.CompanionPane.Register(application); }
                catch (Exception ex) { Log.Error($"Could not register the ACE Companion panel: {ex}"); }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"Startup failed: {ex}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged -= ChangeTracker.OnDocumentChanged;
            try { Tracking.ChangeTracking.Detach(); } catch { }
            try { Companion.CompanionPane.Unregister(application); } catch { }
            Server?.Dispose();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            const string tab = "ACE";
            try { application.CreateRibbonTab(tab); } catch { /* already exists */ }
            // Working mode first: it decides which panels lead, which are dimmed and which are hidden.
            try { Ribbon.WorkModes.CreateSelector(application.CreateRibbonPanel(tab, "Work mode")); }
            catch (Exception ex) { Log.Warn($"Work mode selector: {ex.Message}"); }
            var panel = application.CreateRibbonPanel(tab, "Claude MCP");
            Ribbon.WorkModes.RegisterPanel(panel);
            var path = typeof(App).Assembly.Location;
            var companion = new PushButtonData("AceCompanion", "Companion", path, typeof(Companion.ToggleCompanionCommand).FullName)
            {
                ToolTip = "Show or hide the ACE Companion panel: approve Claude's previewed changes, follow its activity, click results to select them, and copy ready-made prompts.",
            };
            var status = new PushButtonData("AceMcpStatus", "MCP\nStatus", path, typeof(StatusCommand).FullName)
            {
                ToolTip = "Show whether Claude can reach this Revit session, and restart the connection if needed.",
            };
            try
            {
                companion.LargeImage = Icons.Companion(32); companion.Image = Icons.Companion(16);
                status.LargeImage = Icons.Status(32); status.Image = Icons.Status(16);
            }
            catch (Exception ex) { Log.Warn($"Ribbon icons: {ex.Message}"); }
            Ribbon.WorkModes.RegisterButton(panel.Name, panel.AddItem(companion), (sz, dim) => Icons.Companion(sz, dim));
            Ribbon.WorkModes.RegisterButton(panel.Name, panel.AddItem(status), (sz, dim) => Icons.Status(sz, dim));

            // Planned tools (roadmap): real buttons that open a card explaining what each will do.
            try { AddPlannedTools(application, tab, panel, path); }
            catch (Exception ex) { Log.Warn($"Roadmap buttons: {ex.Message}"); }
            try { Ribbon.WorkModes.Restore(); }
            catch (Exception ex) { Log.Warn($"Work mode: {ex.Message}"); }
        }

        private static void AddPlannedTools(UIControlledApplication application, string tab, RibbonPanel claudePanel, string path)
        {
            PushButtonData Data(Ribbon.Feature f)
            {
                var live = f.LiveCommand != null;
                var d = new PushButtonData("AcePlanned_" + f.Key, f.Label.Replace("&&", "&"), path, f.LiveCommand ?? "AceRevitMcp.Ribbon.Planned_" + f.Key)
                {
                    ToolTip = live ? f.LiveSummary ?? f.Summary : f.Summary,
                    LongDescription = live ? "Live." : $"Coming soon (Phase {f.Phase}: {Ribbon.Roadmap.Phases[f.Phase]}). Click to see what it will do" +
                                      (f.TodayWithClaude != null ? " and what Claude can already do today." : "."),
                };
                try { d.LargeImage = Icons.Line(32, f.Glyph, planned: !live); d.Image = Icons.Line(16, f.Glyph, planned: !live); }
                catch (Exception ex) { Log.Warn($"Icon {f.Key}: {ex.Message}"); }
                return d;
            }

            Func<int, bool, System.Windows.Media.ImageSource> IconOf(Ribbon.Feature f) => (sz, dim) => Icons.Line(sz, f.Glyph, planned: f.LiveCommand == null, dim: dim);
            void Reg(RibbonPanel p, RibbonItem item, Func<int, bool, System.Windows.Media.ImageSource> icon) => Ribbon.WorkModes.RegisterButton(p.Name, item, icon);
            Func<int, bool, System.Windows.Media.ImageSource> Fixed(string glyph) => (sz, dim) => Icons.Line(sz, glyph, planned: false, dim: dim);

            foreach (var f in Ribbon.Roadmap.Features.Where(x => x.Panel == claudePanel.Name)) Reg(claudePanel, claudePanel.AddItem(Data(f)), IconOf(f));

            // Live: the Insights dashboard (model health, QA, submission readiness, Claude activity, tool status).
            var insights = application.CreateRibbonPanel(tab, "Insights");
            Ribbon.WorkModes.RegisterPanel(insights);
            var dashboard = new PushButtonData("AceDashboard", "Dashboard", path, typeof(Dashboard.DashboardCommand).FullName)
            {
                ToolTip = "Model insights: health score, audit findings, warnings, rooms and doors QA, parameters, submission readiness, Claude activity and the status of every ACE tool.",
                LongDescription = "Read-only. Shown inside Revit and saved as an HTML report in Documents\\ACE Insights, ready to share.",
            };
            try { dashboard.LargeImage = Icons.Line(32, Ribbon.Glyphs.Dashboard, planned: false); dashboard.Image = Icons.Line(16, Ribbon.Glyphs.Dashboard, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon dashboard: {ex.Message}"); }
            Reg(insights, insights.AddItem(dashboard), Fixed(Ribbon.Glyphs.Dashboard));
            var changes = new PushButtonData("AceChangeTracker", "Change\nTracker", path, typeof(Tracking.ChangeTrackerCommand).FullName)
            {
                ToolTip = "What changed in this model and its linked models since the last snapshot: added, deleted, moved, retyped and changed elements, by category and by person.",
                LongDescription = "Read-only. Snapshots are taken automatically when a model is opened and after saves or syncs. Report saved in Documents\\ACE Insights.",
            };
            try { changes.LargeImage = Icons.Line(32, Ribbon.Glyphs.History, planned: false); changes.Image = Icons.Line(16, Ribbon.Glyphs.History, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon change tracker: {ex.Message}"); }
            Reg(insights, insights.AddItem(changes), Fixed(Ribbon.Glyphs.History));

            foreach (var name in Ribbon.Roadmap.Panels)
            {
                var panel = application.CreateRibbonPanel(tab, name);
                Ribbon.WorkModes.RegisterPanel(panel);
                var features = Ribbon.Roadmap.Features.Where(x => x.Panel == name).ToList();
                foreach (var f in features.Where(x => x.Large)) Reg(panel, panel.AddItem(Data(f)), IconOf(f));
                var smallF = features.Where(x => !x.Large).ToList();
                var small = smallF.Select(Data).ToList();
                IList<RibbonItem> stacked = null;
                if (small.Count == 1) stacked = new List<RibbonItem> { panel.AddItem(small[0]) };
                else if (small.Count == 2) stacked = panel.AddStackedItems(small[0], small[1]);
                else if (small.Count >= 3) stacked = panel.AddStackedItems(small[0], small[1], small[2]);
                if (stacked != null) for (var i = 0; i < stacked.Count && i < smallF.Count; i++) Reg(panel, stacked[i], IconOf(smallF[i]));
            }

            var about = application.CreateRibbonPanel(tab, "Roadmap");
            Ribbon.WorkModes.RegisterPanel(about);
            var roadmap = new PushButtonData("AceRoadmap", "Roadmap", path, typeof(Ribbon.RoadmapCommand).FullName)
            {
                ToolTip = "What ACE can do today, and the tools planned for the ACE ribbon.",
            };
            try { roadmap.LargeImage = Icons.Line(32, Ribbon.Glyphs.Map, planned: false); roadmap.Image = Icons.Line(16, Ribbon.Glyphs.Map, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon roadmap: {ex.Message}"); }
            Reg(about, about.AddItem(roadmap), Fixed(Ribbon.Glyphs.Map));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var server = App.Server;
            var running = server?.IsRunning == true;
            var dialog = new TaskDialog("ACE Revit MCP")
            {
                MainInstruction = running ? "Claude connection is running" : "Claude connection is NOT running",
                MainContent =
                    $"Endpoint: {server?.Url}\n" +
                    $"Config: {AceConfig.FilePath}\n" +
                    $"Log: {Log.FilePath}\n" +
                    (server?.LastError != null ? $"\nLast error: {server.LastError}\n" : "") +
                    "\nRecent activity:\n" + string.Join("\n", Log.Tail().Reverse().Take(8)),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, running ? "Restart connection" : "Start connection");
            if (running) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Stop connection");

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    server?.Stop();
                    server?.Start();
                    TaskDialog.Show("ACE Revit MCP", server?.IsRunning == true ? "Connection started." : $"Could not start: {server?.LastError}");
                    break;
                case TaskDialogResult.CommandLink2:
                    server?.Stop();
                    break;
            }
            return Result.Succeeded;
        }
    }
}
