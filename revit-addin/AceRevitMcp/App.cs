using System;
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
                Tracking.ChangeTracking.Attach(application.ControlledApplication);
                Server = new BridgeServer(Config, dispatcher, application.ControlledApplication.VersionNumber);
                Server.Start();
                CreateRibbon(application);
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
            try { Companion.CompanionPane.Unregister(application); } catch { }
            Server?.Dispose();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            const string tab = "ACE";
            try { application.CreateRibbonTab(tab); } catch { /* already exists */ }
            var panel = application.CreateRibbonPanel(tab, "Claude MCP");
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
            panel.AddItem(companion);
            panel.AddItem(status);

            // Planned tools (roadmap): real buttons that open a card explaining what each will do.
            try { AddPlannedTools(application, tab, panel, path); }
            catch (Exception ex) { Log.Warn($"Roadmap buttons: {ex.Message}"); }
        }

        private static void AddPlannedTools(UIControlledApplication application, string tab, RibbonPanel claudePanel, string path)
        {
            PushButtonData Data(Ribbon.Feature f)
            {
                var d = new PushButtonData("AcePlanned_" + f.Key, f.Label.Replace("&&", "&"), path, "AceRevitMcp.Ribbon.Planned_" + f.Key)
                {
                    ToolTip = f.Summary,
                    LongDescription = $"Coming soon (Phase {f.Phase}: {Ribbon.Roadmap.Phases[f.Phase]}). Click to see what it will do" +
                                      (f.TodayWithClaude != null ? " and what Claude can already do today." : "."),
                };
                try { d.LargeImage = Icons.Line(32, f.Glyph, planned: true); d.Image = Icons.Line(16, f.Glyph, planned: true); }
                catch (Exception ex) { Log.Warn($"Icon {f.Key}: {ex.Message}"); }
                return d;
            }

            foreach (var f in Ribbon.Roadmap.Features.Where(x => x.Panel == claudePanel.Name)) claudePanel.AddItem(Data(f));

            // Live: the Insights dashboard (model health, QA, submission readiness, Claude activity, tool status).
            var insights = application.CreateRibbonPanel(tab, "Insights");
            var dashboard = new PushButtonData("AceDashboard", "Dashboard", path, typeof(Dashboard.DashboardCommand).FullName)
            {
                ToolTip = "Model insights: health score, audit findings, warnings, rooms and doors QA, parameters, submission readiness, Claude activity and the status of every ACE tool.",
                LongDescription = "Read-only. Shown inside Revit and saved as an HTML report in Documents\\ACE Insights, ready to share.",
            };
            try { dashboard.LargeImage = Icons.Line(32, Ribbon.Glyphs.Dashboard, planned: false); dashboard.Image = Icons.Line(16, Ribbon.Glyphs.Dashboard, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon dashboard: {ex.Message}"); }
            insights.AddItem(dashboard);
            var changes = new PushButtonData("AceChangeTracker", "Change\nTracker", path, typeof(Tracking.ChangeTrackerCommand).FullName)
            {
                ToolTip = "What changed in this model and its linked models since the last snapshot: added, deleted, moved, retyped and changed elements, by category and by person.",
                LongDescription = "Read-only. Snapshots are taken automatically when a model is opened and after saves or syncs. Report saved in Documents\\ACE Insights.",
            };
            try { changes.LargeImage = Icons.Line(32, Ribbon.Glyphs.History, planned: false); changes.Image = Icons.Line(16, Ribbon.Glyphs.History, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon change tracker: {ex.Message}"); }
            insights.AddItem(changes);

            foreach (var name in Ribbon.Roadmap.Panels)
            {
                var panel = application.CreateRibbonPanel(tab, name);
                var features = Ribbon.Roadmap.Features.Where(x => x.Panel == name).ToList();
                foreach (var f in features.Where(x => x.Large)) panel.AddItem(Data(f));
                var small = features.Where(x => !x.Large).Select(Data).ToList();
                if (small.Count == 1) panel.AddItem(small[0]);
                else if (small.Count == 2) panel.AddStackedItems(small[0], small[1]);
                else if (small.Count >= 3) panel.AddStackedItems(small[0], small[1], small[2]);
            }

            var about = application.CreateRibbonPanel(tab, "Roadmap");
            var roadmap = new PushButtonData("AceRoadmap", "Roadmap", path, typeof(Ribbon.RoadmapCommand).FullName)
            {
                ToolTip = "What ACE can do today, and the tools planned for the ACE ribbon.",
            };
            try { roadmap.LargeImage = Icons.Line(32, Ribbon.Glyphs.Map, planned: false); roadmap.Image = Icons.Line(16, Ribbon.Glyphs.Map, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon roadmap: {ex.Message}"); }
            about.AddItem(roadmap);
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
