using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Storage" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class StoragePage : System.Windows.Controls.UserControl
    {
        public StoragePage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void BenchDiskActivityCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.BenchDiskActivityCanvas_SizeChanged(sender, e); }
        private void BenchDiskWin10m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.BenchDiskWin10m_Click(s, e); }
        private void BenchDiskWin1h_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.BenchDiskWin1h_Click(s, e); }
        private void BenchDiskWin5m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.BenchDiskWin5m_Click(s, e); }
        private void BenchDiskWin60_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.BenchDiskWin60_Click(s, e); }
        private void BtnStorageDevMgr_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BtnStorageDevMgr_Click(sender, e); }
        private void ExportBadSectors_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ExportBadSectors_Click(sender, e); }
        private void OpenDiskpart_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenDiskpart_Click(sender, e); }
        /// <summary>"Windows tools": opens its menu under the button.</summary>
        private void StorageToolsMenu_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is System.Windows.FrameworkElement b && b.ContextMenu is { } m)
            {
                m.PlacementTarget = b;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                m.IsOpen = true;
            }
        }
        private void RefreshDiskCombos_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RefreshDiskCombos_Click(sender, e); }
        private void RunFullBench_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunFullBench_Click(sender, e); }
        private void RunManualTrim_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunManualTrim_Click(sender, e); }
        private void RunPartitionHealth_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunPartitionHealth_Click(sender, e); }
        private void ShortcutAutoClean_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ShortcutAutoClean_Click(sender, e); }
        private void SmartDiskActivityCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SmartDiskActivityCanvas_SizeChanged(sender, e); }
        private void SmartDiskWin10m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SmartDiskWin10m_Click(s, e); }
        private void SmartDiskWin1h_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SmartDiskWin1h_Click(s, e); }
        private void SmartDiskWin5m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SmartDiskWin5m_Click(s, e); }
        private void SmartDiskWin60_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SmartDiskWin60_Click(s, e); }
        private void SmartDisk_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (_ready) Main?.SmartDisk_Changed(sender, e); }
        private void SmartSelfTestAbort_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SmartSelfTestAbort_Click(sender, e); }
        private void SmartSelfTestExtended_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SmartSelfTestExtended_Click(sender, e); }
        private void SmartSelfTestShort_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SmartSelfTestShort_Click(sender, e); }
        private void SmartShowAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SmartShowAll_Click(sender, e); }
        private void SmartVerdictScroll_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SmartVerdictScroll_SizeChanged(sender, e); }
        private void StopFullBench_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StopFullBench_Click(sender, e); }
        private void StorageActWin_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StorageActWin_Click(sender, e); }
        private void StorageActivityCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.StorageActivityCanvas_MouseLeave(sender, e); }
        private void StorageActivityCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.StorageActivityCanvas_MouseMove(sender, e); }
        private void StorageActivityCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.StorageActivityCanvas_SizeChanged(sender, e); }
        private void StorageCleanmgr_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StorageCleanmgr_Click(sender, e); }
        private void StorageFindLarge_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StorageFindLarge_Click(sender, e); }
        private void StorageSettings_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StorageSettings_Click(sender, e); }
        private void StorageTab_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StorageTab_Click(sender, e); }
        private void StorageWin10m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StorageWin10m_Click(s, e); }
        private void StorageWin1h_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StorageWin1h_Click(s, e); }
        private void StorageWin5m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StorageWin5m_Click(s, e); }
        private void StorageWin60_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StorageWin60_Click(s, e); }
        private void SurfaceBlockMap_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.SurfaceBlockMap_MouseMove(sender, e); }
        private void SurfaceChartMenu_Opened(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SurfaceChartMenu_Opened(sender, e); }
        private void SurfaceChartWindow_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SurfaceChartWindow_Click(sender, e); }
        private void SurfaceDiskActivityCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SurfaceDiskActivityCanvas_SizeChanged(sender, e); }
        private void SurfaceDiskWin10m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SurfaceDiskWin10m_Click(s, e); }
        private void SurfaceDiskWin1h_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SurfaceDiskWin1h_Click(s, e); }
        private void SurfaceDiskWin5m_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SurfaceDiskWin5m_Click(s, e); }
        private void SurfaceDiskWin60_Click(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SurfaceDiskWin60_Click(s, e); }
        private void SurfaceRepair_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SurfaceRepair_Click(sender, e); }
        private void SurfaceProblemsToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SurfaceProblemsToggle_Click(sender, e); }
        private void SurfaceScan_Start_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SurfaceScan_Start_Click(sender, e); }
        private void SurfaceSpeedChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.SurfaceSpeedChart_MouseMove(sender, e); }
        private void ToggleDiskLatency_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleDiskLatency_Click(sender, e); }
    }
}
