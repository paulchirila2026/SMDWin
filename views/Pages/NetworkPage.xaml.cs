using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Network" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class NetworkPage : System.Windows.Controls.UserControl
    {
        public NetworkPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void CopyPublicIp_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.CopyPublicIp_Click(s, e); }
        private void NetOverviewDownCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.NetOverviewDownCanvas_SizeChanged(sender, e); }
        private void NetOverviewUpCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.NetOverviewUpCanvas_SizeChanged(sender, e); }
        private void NetTabOverview_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) { if (_ready) Main?.NetTabOverview_ScrollChanged(sender, e); }
        private void OpenLanSpeed_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenLanSpeed_Click(s, e); }
        private void OpenNetAdapters_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenNetAdapters_Click(s, e); }
        private void OpenNetworkSettings_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenNetworkSettings_Click(s, e); }
        private void OpenRouterPage_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenRouterPage_Click(s, e); }
        private void RefreshPublicIp_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RefreshPublicIp_Click(s, e); }
        private void TogglePingSection_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TogglePingSection_Click(s, e); }
        private void TogglePortScanSection_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TogglePortScanSection_Click(s, e); }
    }
}
