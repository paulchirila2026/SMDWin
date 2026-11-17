using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Power" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class PowerPage : System.Windows.Controls.UserControl
    {
        public PowerPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void AppDrainAnalyze_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.AppDrainAnalyze_Click(s, e); }
        private void AppDrainDetails_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.AppDrainDetails_Click(s, e); }
        private void AppDrainGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) { if (_ready) Main?.AppDrainGrid_DoubleClick(s, e); }
        private void AppDrainKill_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.AppDrainKill_Click(s, e); }
        private void BatDrawChart_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.BatDrawChart_SizeChanged(sender, e); }
        private void BatteryTest_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BatteryTest_Click(s, e); }
        private void BoostModeOption_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BoostModeOption_Click(sender, e); }
        private void ChkPowerFlowAnimation_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChkPowerFlowAnimation_Checked(sender, e); }
        private void ChkPowerFlowAnimation_Unchecked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChkPowerFlowAnimation_Unchecked(sender, e); }
        private void ExpAdvancedPower_UserToggled(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ExpAdvancedPower_UserToggled(sender, e); }
        private void FastStartup_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.FastStartup_Click(sender, e); }
        private void GenerateEnergyReport_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.GenerateEnergyReport_Click(sender, e); }
        private void GenerateSleepReport_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.GenerateSleepReport_Click(sender, e); }
        private void OpenOptimizer_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenOptimizer_Click(sender, e); }
        private void OpenPowerHistory_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenPowerHistory_Click(s, e); }
        private void PanelBattery_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) { if (_ready) Main?.PanelBattery_ScrollChanged(sender, e); }
        private void PowerDiagRefresh_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.PowerDiagRefresh_Click(sender, e); }
        private void PowerProfileChip_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.PowerProfileChip_Checked(sender, e); }
        private void ProcStateRangeTrack_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.ProcStateRangeTrack_SizeChanged(sender, e); }
        private void SetPowerPlan_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SetPowerPlan_Click(sender, e); }
        private void ThumbProcStateMax_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) { if (_ready) Main?.ThumbProcStateMax_DragDelta(sender, e); }
        private void ThumbProcStateMin_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) { if (_ready) Main?.ThumbProcStateMin_DragDelta(sender, e); }
        private void ThumbProcState_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) { if (_ready) Main?.ThumbProcState_DragCompleted(sender, e); }
        private void ToggleAppDrainExpand_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ToggleAppDrainExpand_Click(sender, e); }
        private void UsbSelectiveSuspend_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.UsbSelectiveSuspend_Click(sender, e); }
    }
}
