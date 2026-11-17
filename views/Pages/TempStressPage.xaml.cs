using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "TempStress" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class TempStressPage : System.Windows.Controls.UserControl
    {
        public TempStressPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void BenchClose_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BenchClose_Click(sender, e); }
        private void ChartColorMenuItem_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartColorMenuItem_Loaded(sender, e); }
        private void ChartCtxColorMode_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxColorMode_Click(sender, e); }
        private void ChartCtxGridlines_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxGridlines_Click(sender, e); }
        private void ChartCtxStressMark_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartCtxStressMark_Click(sender, e); }
        private void ChartGridlinesMenuItem_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChartGridlinesMenuItem_Loaded(sender, e); }
        private void ChartTimeRange_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.ChartTimeRange_Click(sender, e); }
        private void CpuFreqChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.CpuFreqChart_SizeChanged(sender, e); }
        private void CpuTempChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.CpuTempChart_SizeChanged(sender, e); }
        private void CpuUsageChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.CpuUsageChart_SizeChanged(sender, e); }
        private void DismissLastAlert_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DismissLastAlert_Click(sender, e); }
        private void ExportStressPdf_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ExportStressPdf_Click(sender, e); }
        private void GpuLoadChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.GpuLoadChart_SizeChanged(sender, e); }
        private void GpuStressChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.GpuStressChart_SizeChanged(sender, e); }
        private void GpuTempChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.GpuTempChart_SizeChanged(sender, e); }
        private void OpenCpuBenchWindow_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenCpuBenchWindow_Click(sender, e); }
        private void OpenFanControl_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenFanControl_Click(sender, e); }
        private void OpenGpuBenchWindow_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenGpuBenchWindow_Click(sender, e); }
        private void ResetChart_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ResetChart_Click(s, e); }
        private void StressRangeMenu_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StressRangeMenu_Click(s, e); }
        private void StressMoreMenu_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StressMoreMenu_Click(s, e); }
        private void StressCardRange_ContextMenuOpening(object s, System.Windows.Controls.ContextMenuEventArgs e) { if (_ready) Main?.StressCardRange_ContextMenuOpening(s, e); else e.Handled = true; }
        private void RowCpuCoreTemps_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.RowCpuCoreTemps_Click(sender, e); }
        private void RunBench_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunBench_Click(sender, e); }
        private void SensorGroup_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SensorGroup_Toggle(sender, e); }
        private void SensorGroupsGrid_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SensorGroupsGrid_SizeChanged(sender, e); }
        private void SensorRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.SensorRow_Click(sender, e); }
        private void StressChartsBack_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StressChartsBack_Click(sender, e); }
        private void SensorScroll_Changed(object sender, System.Windows.Controls.ScrollChangedEventArgs e) { if (_ready) Main?.SensorScroll_Changed(sender, e); }
        private void StressChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.StressChart_MouseLeave(sender, e); }
        private void StressChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (_ready) Main?.StressChart_MouseMove(sender, e); }
        private void StressPlan_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StressPlan_Click(sender, e); }
        private void StressStart_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StressStart_Click(sender, e); }
        private void StressDuration_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.StressDuration_Click(sender, e); }
        private void StressMarkMenuItem_Loaded(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.StressMarkMenuItem_Loaded(sender, e); }
        private void StressScroll_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.StressScroll_SizeChanged(sender, e); }
        private void StressToolbar_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.StressToolbar_SizeChanged(sender, e); }
        private void ToggleCpuStress_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleCpuStress_Click(sender, e); }
        private void ToggleEnduranceMode_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleEnduranceMode_Click(sender, e); }
        private void ToggleGpuStress_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleGpuStress_Click(sender, e); }
        private void ToggleMaxLoad_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleMaxLoad_Click(sender, e); }
        private void TogglePsuStress_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TogglePsuStress_Click(sender, e); }
        private void ToggleTurbo_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleTurbo_Click(sender, e); }
    }
}
