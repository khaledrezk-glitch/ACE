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
    /// <summary>
    /// Revit add-in entry point when ACE is loaded directly by a plain manifest (fallback / development). Installed ACE
    /// is started by the ACE loader through PayloadEntry instead; both run the same StartServices / StopServices.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        internal static BridgeServer Server { get; private set; }
        internal static AceConfig Config { get; private set; }
        internal static RequestDispatcher Dispatcher { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                PayloadEntry.Start(application, null);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                try { Log.Error($"Startup failed: {ex}"); } catch { }
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            PayloadEntry.Stop();
            return Result.Succeeded;
        }

        /// <summary>Starts the bridge, the trackers, the ribbon and the Companion (throws when ACE can't run at all).</summary>
        internal static void StartServices(UIControlledApplication application)
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
                InstanceRegistry.Attach(application);
                Server.Start();
                // A reload that can't listen has failed: the loader then starts the previous build again. (At Revit startup a
                // missing port is reported by MCP Status, as before: the ribbon still works.)
                if (!Server.IsRunning && PayloadEntry.Reloaded)
                    throw new InvalidOperationException($"The bridge is not listening after the reload: {Server.LastError}");
                // After a reload the documents are already open: the instance file names them now, not at the next switch.
                var ui = PayloadEntry.Get<UIApplication>("uiApplication");
                if (ui != null) InstanceRegistry.Update(ui);
                // ... and the Companion shows their last compliance run at once (DM1).
                if (ui != null) foreach (Document d in ui.Application.Documents) Rules.ComplianceStore.LoadInto(d);
                // What the build before this one left in the loader's store (plain data): models ACE opened, the last
                // undoable Claude run.
                Commands.ModelFileCommands.Restore(PayloadEntry.Store);
                ChangeTracker.Restore(PayloadEntry.Store);
            }
            catch (Exception ex)
            {
                Log.Error($"Startup failed: {ex}");
                throw;
            }
            // From here on a failure costs one surface, not ACE.
            try { CreateRibbon(application); }
            catch (Exception ex) { Log.Error($"Ribbon: {ex}"); }
            Scripting.CodeRunner.WarmUp();
            try { Companion.CompanionPane.Register(application); }
            catch (Exception ex) { Log.Error($"Could not register the ACE Companion panel: {ex}"); }
            Log.Info(PayloadEntry.Hosted
                ? $"ACE {typeof(App).Assembly.GetName().Version?.ToString(3)} started by the ACE loader {PayloadEntry.LoaderVersion} from {PayloadEntry.Directory}"
                : $"ACE {typeof(App).Assembly.GetName().Version?.ToString(3)} started (loaded directly from {PayloadEntry.Directory})");
        }

        /// <summary>
        /// Releases everything StartServices took, in this order: the bridge (port and instance file), the request queue
        /// and the ExternalEvent target, Revit events, the Companion content, the ribbon hooks, open ACE windows and the
        /// script compiler. Safe after a partial start and when called twice.
        /// </summary>
        internal static void StopServices(UIControlledApplication application)
        {
            void Step(string what, Action a) { try { a(); } catch (Exception ex) { Log.Warn($"Stop ({what}): {ex.Message}"); } }
            Step("bridge", () => Server?.Dispose());
            Server = null;
            Step("dispatcher", () => Dispatcher?.Shutdown());
            Dispatcher = null;
            if (application != null) Step("document events", () => application.ControlledApplication.DocumentChanged -= ChangeTracker.OnDocumentChanged);
            Step("change tracking", Tracking.ChangeTracking.Detach);
            Step("models opened by ACE", () => Commands.ModelFileCommands.SaveAndForget(PayloadEntry.Store));
            Step("undo state", () => ChangeTracker.Save(PayloadEntry.Store));
            if (application != null) Step("companion", () => Companion.CompanionPane.Unregister(application));
            Step("instance registry", InstanceRegistry.Detach);
            Step("work modes", Ribbon.WorkModes.Detach);
            Step("clash browser", Coordination.ClashBrowser.CloseOpen);
            Step("report windows", Dashboard.ReportWindow.CloseAll);
            Step("script compiler", Scripting.CodeRunner.Shutdown);
            Log.Info("ACE stopped");
        }

        /// <summary>
        /// Builds the ACE tab. After a reload (a new build in the same Revit session) the panels and buttons already exist:
        /// RibbonSlots finds them by name and points them at the new build's commands instead of adding them again.
        /// </summary>
        private static void CreateRibbon(UIControlledApplication application)
        {
            const string tab = "ACE";
            try { application.CreateRibbonTab(tab); } catch { /* already exists */ }
            // Working mode first: it decides which panels lead, which are dimmed and which are hidden.
            try { Ribbon.WorkModes.CreateSelector(Ribbon.RibbonSlots.Panel(application, tab, "Work mode")); }
            catch (Exception ex) { Log.Warn($"Work mode selector: {ex.Message}"); }
            var panel = Ribbon.RibbonSlots.Panel(application, tab, "Claude MCP");
            Ribbon.WorkModes.RegisterPanel(panel);
            var companion = Ribbon.RibbonSlots.Button("AceCompanion", "Companion", typeof(Companion.ToggleCompanionCommand).FullName);
            companion.ToolTip = "Show or hide the ACE Companion panel: approve Claude's previewed changes, follow its activity, click results to select them, and copy ready-made prompts.";
            var status = Ribbon.RibbonSlots.Button("AceMcpStatus", "MCP\nStatus", typeof(StatusCommand).FullName);
            status.ToolTip = "Show whether Claude can reach this Revit session, and restart the connection if needed.";
            try
            {
                companion.LargeImage = Icons.Companion(32); companion.Image = Icons.Companion(16);
                status.LargeImage = Icons.Status(32); status.Image = Icons.Status(16);
            }
            catch (Exception ex) { Log.Warn($"Ribbon icons: {ex.Message}"); }
            Ribbon.WorkModes.RegisterButton(panel.Name, Ribbon.RibbonSlots.Add(panel, companion), (sz, dim) => Icons.Companion(sz, dim));
            Ribbon.WorkModes.RegisterButton(panel.Name, Ribbon.RibbonSlots.Add(panel, status), (sz, dim) => Icons.Status(sz, dim));

            // Planned tools (roadmap): real buttons that open a card explaining what each will do.
            try { AddPlannedTools(application, tab, panel); }
            catch (Exception ex) { Log.Warn($"Roadmap buttons: {ex.Message}"); }
            try { Ribbon.WorkModes.Restore(); }
            catch (Exception ex) { Log.Warn($"Work mode: {ex.Message}"); }
        }

        private static void AddPlannedTools(UIControlledApplication application, string tab, RibbonPanel claudePanel)
        {
            PushButtonData Data(Ribbon.Feature f)
            {
                var live = f.LiveCommand != null;
                var d = Ribbon.RibbonSlots.Button("AcePlanned_" + f.Key, f.Label.Replace("&&", "&"), f.LiveCommand ?? "AceRevitMcp.Ribbon.Planned_" + f.Key);
                d.ToolTip = live ? f.LiveSummary ?? f.Summary : f.Summary;
                d.LongDescription = live ? "Live." : $"Coming soon (Phase {f.Phase}: {Ribbon.Roadmap.Phases[f.Phase]}). Click to see what it will do" +
                                    (f.TodayWithClaude != null ? " and what Claude can already do today." : ".");
                try { d.LargeImage = Icons.Line(32, f.Glyph, planned: !live); d.Image = Icons.Line(16, f.Glyph, planned: !live); }
                catch (Exception ex) { Log.Warn($"Icon {f.Key}: {ex.Message}"); }
                return d;
            }

            Func<int, bool, System.Windows.Media.ImageSource> IconOf(Ribbon.Feature f) => (sz, dim) => Icons.Line(sz, f.Glyph, planned: f.LiveCommand == null, dim: dim);
            void Reg(RibbonPanel p, RibbonItem item, Func<int, bool, System.Windows.Media.ImageSource> icon) => Ribbon.WorkModes.RegisterButton(p.Name, item, icon);
            Func<int, bool, System.Windows.Media.ImageSource> Fixed(string glyph) => (sz, dim) => Icons.Line(sz, glyph, planned: false, dim: dim);

            foreach (var f in Ribbon.Roadmap.Features.Where(x => x.Panel == claudePanel.Name)) Reg(claudePanel, Ribbon.RibbonSlots.Add(claudePanel, Data(f)), IconOf(f));

            // Live: the Insights dashboard (model health, QA, submission readiness, Claude activity, tool status).
            var insights = Ribbon.RibbonSlots.Panel(application, tab, "Insights");
            Ribbon.WorkModes.RegisterPanel(insights);
            var dashboard = Ribbon.RibbonSlots.Button("AceDashboard", "Dashboard", typeof(Dashboard.DashboardCommand).FullName);
            dashboard.ToolTip = "Model insights: health score, audit findings, warnings, rooms and doors QA, parameters, submission readiness, Claude activity and the status of every ACE tool.";
            dashboard.LongDescription = "Read-only. Shown inside Revit and saved as an HTML report in Documents\\ACE Insights, ready to share.";
            try { dashboard.LargeImage = Icons.Line(32, Ribbon.Glyphs.Dashboard, planned: false); dashboard.Image = Icons.Line(16, Ribbon.Glyphs.Dashboard, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon dashboard: {ex.Message}"); }
            Reg(insights, Ribbon.RibbonSlots.Add(insights, dashboard), Fixed(Ribbon.Glyphs.Dashboard));
            var changes = Ribbon.RibbonSlots.Button("AceChangeTracker", "Change\nTracker", typeof(Tracking.ChangeTrackerCommand).FullName);
            changes.ToolTip = "What changed in this model and its linked models since the last snapshot: added, deleted, moved, retyped and changed elements, by category and by person.";
            changes.LongDescription = "Read-only. Snapshots are taken automatically when a model is opened and after saves or syncs. Report saved in Documents\\ACE Insights.";
            try { changes.LargeImage = Icons.Line(32, Ribbon.Glyphs.History, planned: false); changes.Image = Icons.Line(16, Ribbon.Glyphs.History, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon change tracker: {ex.Message}"); }
            Reg(insights, Ribbon.RibbonSlots.Add(insights, changes), Fixed(Ribbon.Glyphs.History));

            foreach (var name in Ribbon.Roadmap.Panels)
            {
                var panel = Ribbon.RibbonSlots.Panel(application, tab, name);
                Ribbon.WorkModes.RegisterPanel(panel);
                var features = Ribbon.Roadmap.Features.Where(x => x.Panel == name).ToList();
                foreach (var f in features.Where(x => x.Large)) Reg(panel, Ribbon.RibbonSlots.Add(panel, Data(f)), IconOf(f));
                var smallF = features.Where(x => !x.Large).ToList();
                var small = smallF.Select(Data).ToList();
                var stacked = Ribbon.RibbonSlots.Stacked(panel, small);
                if (stacked != null) for (var i = 0; i < stacked.Count && i < smallF.Count; i++) Reg(panel, stacked[i], IconOf(smallF[i]));
            }

            var about = Ribbon.RibbonSlots.Panel(application, tab, "Roadmap");
            Ribbon.WorkModes.RegisterPanel(about);
            var roadmap = Ribbon.RibbonSlots.Button("AceRoadmap", "Roadmap", typeof(Ribbon.RoadmapCommand).FullName);
            roadmap.ToolTip = "What ACE can do today, and the tools planned for the ACE ribbon.";
            try { roadmap.LargeImage = Icons.Line(32, Ribbon.Glyphs.Map, planned: false); roadmap.Image = Icons.Line(16, Ribbon.Glyphs.Map, planned: false); }
            catch (Exception ex) { Log.Warn($"Icon roadmap: {ex.Message}"); }
            Reg(about, Ribbon.RibbonSlots.Add(about, roadmap), Fixed(Ribbon.Glyphs.Map));
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
