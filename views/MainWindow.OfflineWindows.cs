using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Event Viewer and BSOD can read a Windows on another drive (AnalyzedWindows): the button in both page headers shows which
    /// Windows is read ("This PC" / "Windows 11 — D:") and opens OfflineWindowsDialog. Started from a rescue stick (WinPE) with
    /// exactly one Windows on the internal disks, that Windows is chosen by itself — it is the one that needs looking at.
    /// </summary>
    public partial class MainWindow
    {
        private bool _offlineHooked, _winPeChecked;

        private void HookAnalyzedWindows()
        {
            if (_offlineHooked) return;
            _offlineHooked = true;
            AnalyzedWindows.Changed += () => Dispatcher.InvokeAsync(async () => await OnAnalyzedWindowsChanged());
        }

        /// <summary>Called before the first scan of Event Viewer / BSOD.</summary>
        private void EnsureWinPEChoice()
        {
            HookAnalyzedWindows();
            UpdateOtherWindowsLabels();
            if (_winPeChecked) return;
            _winPeChecked = true;
            try
            {
                if (!AnalyzedWindows.RunningInWinPE() || AnalyzedWindows.IsOffline) return;
                var found = AnalyzedWindows.Find();
                if (found.Count != 1) return;
                AnalyzedWindows.Set(found[0]);   // the scan that follows already reads it
                ShowToastSuccess(PT("Started from a rescue stick: reading {0} on {1}", found[0].Name, found[0].Letter));
            }
            catch (Exception ex) { AppLogger.Debug($"EnsureWinPEChoice: {ex.Message}"); }
        }

        internal async void OpenOtherWindows_Click(object sender, RoutedEventArgs e)
        {
            HookAnalyzedWindows();
            var found = await Task.Run(() => AnalyzedWindows.Find());
            var dlg = new OfflineWindowsDialog(found) { Owner = this };
            dlg.ShowDialog();
        }

        private void UpdateOtherWindowsLabels()
        {
            string text = AnalyzedWindows.Offline is WindowsInstall w ? $"{w.Name} — {w.Letter}" : PT("This PC");
            foreach (var name in new[] { "TxtEvOtherWindows", "TxtCrashOtherWindows" })
                if (FindName(name) is System.Windows.Controls.TextBlock t) t.Text = text;
            foreach (var name in new[] { "BtnEvOtherWindows", "BtnCrashOtherWindows" })
                if (FindName(name) is Wpf.Ui.Controls.Button b)
                    b.Appearance = AnalyzedWindows.IsOffline ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            if (FindName("StabilityCard") is FrameworkElement sc)
                sc.Visibility = AnalyzedWindows.IsOffline ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task OnAnalyzedWindowsChanged()
        {
            try
            {
                UpdateOtherWindowsLabels();
                _evCalDays = null;   // the calendar belongs to the other Windows now
                _evCalRaw = null;
                _evCalSelected = null;
                if (_eventScan != null) await RunEventScan();
                await LoadCrashesInternalAsync();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "OnAnalyzedWindowsChanged"); }
        }
    }
}
