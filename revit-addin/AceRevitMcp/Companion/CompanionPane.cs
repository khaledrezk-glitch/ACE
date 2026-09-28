using System;
using System.Collections.Generic;
using System.Linq;
using AceRevitMcp.Util;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Companion
{
    /// <summary>Registers the ACE Companion dockable pane and keeps the "Right now in Revit" context current.</summary>
    internal sealed class CompanionPane : IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("7C2A4B1E-5D3F-4A6B-9C8D-ACE000000002"));
        private CompanionView _view;

        public static void Register(UIControlledApplication app)
        {
            app.RegisterDockablePane(Id, "ACE Companion", new CompanionPane());
            app.SelectionChanged += OnSelectionChanged;
            app.ViewActivated += OnViewActivated;
        }

        public static void Unregister(UIControlledApplication app)
        {
            app.SelectionChanged -= OnSelectionChanged;
            app.ViewActivated -= OnViewActivated;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            _view ??= new CompanionView();
            data.FrameworkElement = _view;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 300 };
            data.VisibleByDefault = true;
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try { Update(e.GetDocument(), e.GetSelectedElements()); }
            catch (Exception ex) { Log.Warn($"Companion selection update: {ex.Message}"); }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                var uidoc = new UIDocument(doc);
                Update(doc, uidoc.Selection.GetElementIds());
            }
            catch (Exception ex) { Log.Warn($"Companion view update: {ex.Message}"); }
        }

        private static void Update(Document doc, ICollection<ElementId> selected)
        {
            var ctx = new RevitContext
            {
                Document = doc?.Title,
                View = doc?.ActiveView?.Name,
                ViewType = doc?.ActiveView?.ViewType.ToString(),
            };
            if (doc != null && selected != null && selected.Count > 0)
            {
                ctx.SelectedIds = selected.Select(i => i.Value).ToList();
                var byCategory = selected.Take(3000)
                    .Select(i => doc.GetElement(i)?.Category?.Name ?? "Other")
                    .GroupBy(n => n).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {g.Key}").Take(5);
                ctx.SelectionSummary = $"{selected.Count} element(s): {string.Join(", ", byCategory)}";
            }
            ActivityHub.UpdateContext(ctx);
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ToggleCompanionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(CompanionPane.Id);
            if (pane.IsShown()) pane.Hide(); else pane.Show();
            return Result.Succeeded;
        }
    }
}
