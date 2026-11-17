using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Settings" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        public SettingsPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void AdminModeBadge_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.AdminModeBadge_Click(sender, e); }
        private void BatRefreshChip_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BatRefreshChip_Click(sender, e); }
        private void BgRecChip_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BgRecChip_Checked(sender, e); }
        private void BtnAutoTheme_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BtnAutoTheme_Click(sender, e); }
        private void BtnInstallPawnIo_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.BtnInstallPawnIo_Click(sender, e); }
        private void ChkAutoTheme_Changed(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChkAutoTheme_Changed(sender, e); }
        private void ChkTraySensors_Changed(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChkTraySensors_Changed(s, e); }
        private void ChkWidgetEnabled_Changed(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChkWidgetEnabled_Changed(s, e); }
        private void DensityPreview_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DensityPreview_Click(sender, e); }
        private void DriverSearchSite_Changed(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DriverSearchSite_Changed(s, e); }
        private void NetRefreshChip_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.NetRefreshChip_Click(sender, e); }
        private void OpenAppLog_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenAppLog_Click(s, e); }
        private void OpenDataFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenDataFolder_Click(sender, e); }
        private void ProcRefreshChip_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ProcRefreshChip_Click(sender, e); }
        private void SetTheme_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SetTheme_Click(sender, e); }
        private void SettingsPage_IsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) { if (_ready) Main?.SettingsPage_IsVisibleChanged(sender, e); }
        private void SettingsPage_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) { if (_ready) Main?.SettingsPage_SizeChanged(sender, e); }
        private void Settings_Changed(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.Settings_Changed(s, e); }
        private void SliderCpuTempAlert_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e) { if (_ready) Main?.SliderCpuTempAlert_ValueChanged(s, e); }
        private void SliderCpuUsageAlert_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e) { if (_ready) Main?.SliderCpuUsageAlert_ValueChanged(s, e); }
        private void SliderEventDays_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e) { if (_ready) Main?.SliderEventDays_ValueChanged(s, e); }
        private void SliderGpuTempAlert_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e) { if (_ready) Main?.SliderGpuTempAlert_ValueChanged(s, e); }
        private void SliderRefresh_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e) { if (_ready) Main?.SliderRefresh_ValueChanged(s, e); }
        private void TempGpuBtn_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TempGpuBtn_Click(s, e); }
        private void TempRefreshChip_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TempRefreshChip_Click(sender, e); }
        private void TempUnitChip_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TempUnitChip_Checked(sender, e); }
        private void TglSensorLogger_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TglSensorLogger_Checked(sender, e); }
        private void TglSensorLogger_Unchecked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TglSensorLogger_Unchecked(sender, e); }
        private void TxtWorkshopName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (_ready) Main?.TxtWorkshopName_KeyDown(sender, e); }
        private void TxtWorkshopName_LostFocus(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TxtWorkshopName_LostFocus(sender, e); }
        private void TxtWorkshopName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_ready) Main?.TxtWorkshopName_TextChanged(sender, e); }
        private void WidgetAlertOnly_Changed(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.WidgetAlertOnly_Changed(s, e); }
        private void SettingsTabBtn_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SettingsTabBtn_Click(sender, e); }
        private void ThemeSeg_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ThemeSeg_Checked(sender, e); }
        private void DensitySeg_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.DensitySeg_Checked(sender, e); }
        private void WorkshopMode_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.WorkshopMode_Click(sender, e); }
        private void WidgetStyle_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.WidgetStyle_Click(sender, e); }
        private void AccentSwatch_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.AccentSwatch_Checked(sender, e); }
        private void ChangeReportFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ChangeReportFolder_Click(sender, e); }
        private void CloseAction_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.CloseAction_Checked(sender, e); }
        private void CpuTempLimit_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.CpuTempLimit_Checked(sender, e); }
        private void GpuTempLimit_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.GpuTempLimit_Checked(sender, e); }
        private void OpenReportFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenReportFolder_Click(sender, e); }
        private void OpenSensorLogFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenSensorLogFolder_Click(sender, e); }
        private void ReportProblem_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ReportProblem_Click(sender, e); }
        private void ResetReportFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.ResetReportFolder_Click(sender, e); }
        private void SettingsExport_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SettingsExport_Click(sender, e); }
        private void SettingsImport_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SettingsImport_Click(sender, e); }
        private void SettingsReset_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SettingsReset_Click(sender, e); }
        private void SettingsTab_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SettingsTab_Checked(sender, e); }
        private void SlowerOnBattery_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.SlowerOnBattery_Click(sender, e); }
        private void TestNotification_Click(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.TestNotification_Click(sender, e); }
        private void UpdateSpeed_Checked(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.UpdateSpeed_Checked(sender, e); }
        private void WorkshopField_LostFocus(object sender, System.Windows.RoutedEventArgs e) { if (_ready) Main?.WorkshopField_LostFocus(sender, e); }
        private void WorkshopField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (_ready) Main?.WorkshopField_KeyDown(sender, e); }
        private void SettingsSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_ready) Main?.SettingsSearch_TextChanged(sender, e); }
    }
}
