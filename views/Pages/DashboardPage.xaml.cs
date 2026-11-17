using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Dashboard" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class DashboardPage : System.Windows.Controls.UserControl
    {
        public DashboardPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void ChartColorMenuItem_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartColorMenuItem_Loaded(sender, e); }
        private void ChartCtxColorMode_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxColorMode_Click(sender, e); }
        private void ChartCtxFill_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxFill_Click(sender, e); }
        private void ChartCtxRange_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxRange_Click(sender, e); }
        private void ChartRangeMenuItem_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartRangeMenuItem_Loaded(sender, e); }
        private void ChartWindowBtn_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartWindowBtn_Click(sender, e); }
        private void LiveBadge_MouseUp(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.LiveBadge_MouseUp(s, e); }
        private void DashBattery_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashBattery_Click(s, e); }
        private void DashChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.DashChart_MouseLeave(sender, e); }
        private void DashChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.DashChart_MouseMove(sender, e); }
        private void DashCpuTempChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.DashCpuTempChart_SizeChanged(sender, e); }
        private void DashCpu_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashCpu_Click(s, e); }
        private void DashDisk_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashDisk_Click(s, e); }
        private void DashGpuTempChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.DashGpuTempChart_SizeChanged(sender, e); }
        private void DashHardware_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashHardware_Click(s, e); }
        private void DashName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashName_Click(sender, e); }
        private void DashNet_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashNet_Click(s, e); }
        private void DashNetwork_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashNetwork_Click(s, e); }
        private void DashPingChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.DashPingChart_SizeChanged(sender, e); }
        private void DashPing_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashPing_Click(s, e); }
        private void DashRam_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashRam_Click(s, e); }
        private void DashRowsGrid_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.DashRowsGrid_SizeChanged(sender, e); }
        private void DashRowsScroll_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.DashRowsScroll_SizeChanged(sender, e); }
        private void DashRows_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DashRows_Loaded(sender, e); }
        private void DashStorageDonut_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DashStorageDonut_Loaded(sender, e); }
        private void DashStorageGrid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashStorageGrid_MouseLeftButtonUp(sender, e); }
        private void DashStorage_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashStorage_Click(s, e); }
        private void DashWindowsOs_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.DashWindowsOs_Click(s, e); }
        private void ExportSnapshot_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ExportSnapshot_Click(sender, e); }
        private void SparkCpu_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SparkCpu_SizeChanged(sender, e); }
        private void SparkDiskActivity_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SparkDiskActivity_SizeChanged(sender, e); }
        private void SparkGpu_SizeChanged(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SparkGpu_SizeChanged(sender, e); }
        private void SparkNetActivity_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SparkNetActivity_SizeChanged(sender, e); }
        private void SparkRam_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SparkRam_SizeChanged(sender, e); }
    }
}
