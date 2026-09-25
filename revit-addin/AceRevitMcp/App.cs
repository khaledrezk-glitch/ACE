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

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Config = AceConfig.LoadOrCreate();
                var dispatcher = new RequestDispatcher(new CommandRegistry());
                dispatcher.Initialize();
                application.ControlledApplication.DocumentChanged += ChangeTracker.OnDocumentChanged;
                Server = new BridgeServer(Config, dispatcher, application.ControlledApplication.VersionNumber);
                Server.Start();
                CreateRibbon(application);
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
            Server?.Dispose();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            const string tab = "ACE";
            try { application.CreateRibbonTab(tab); } catch { /* already exists */ }
            var panel = application.CreateRibbonPanel(tab, "Claude MCP");
            var path = typeof(App).Assembly.Location;
            panel.AddItem(new PushButtonData("AceMcpStatus", "MCP\nStatus", path, typeof(StatusCommand).FullName)
            {
                ToolTip = "Show whether Claude can reach this Revit session, and restart the connection if needed.",
            });
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
