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
        private readonly TextBlock _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 8, 10, 8), Visibility = Visibility.Collapsed };
        private readonly DockPanel _root = new DockPanel { LastChildFill = true };

        public PaneHost()
        {
            var banner = new Border { Child = _note, Background = System.Windows.Media.Brushes.LightYellow };
            DockPanel.SetDock(banner, Dock.Top);
            _root.Children.Add(banner);
            _root.Children.Add(_host);
        }

        /// <summary>A warning above the content (e.g. the previous build runs because the current one failed its checks); null hides it.</summary>
        public void SetNote(string note)
        {
            _note.Text = note ?? "";
            _note.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _root;
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
