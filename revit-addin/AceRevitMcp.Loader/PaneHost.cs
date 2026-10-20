using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// The ACE Companion dockable pane. Revit can't unregister a pane, so the loader registers it once and keeps it; each
    /// payload build puts its own content in (and takes it out again in Stop).
    /// </summary>
    internal sealed class PaneHost : IDockablePaneProvider
    {
        /// <summary>The same id the payload uses when it is loaded directly (AceRevitMcp.Companion.CompanionPane.Id).</summary>
        public static readonly DockablePaneId Id = new DockablePaneId(new System.Guid("7C2A4B1E-5D3F-4A6B-9C8D-ACE000000002"));

        private readonly Border _host = new Border();

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _host;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 300 };
            data.VisibleByDefault = true;
        }

        /// <summary>The payload's view, or null (then a short note says why ACE is not there).</summary>
        public void SetContent(object content, string whyEmpty = null)
        {
            _host.Child = content as UIElement ?? new TextBlock
            {
                Text = "ACE is not loaded" + (string.IsNullOrEmpty(whyEmpty) ? "." : ": " + whyEmpty) + "\n\nSee " + LoaderLog.FilePath,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
            };
        }
    }
}
