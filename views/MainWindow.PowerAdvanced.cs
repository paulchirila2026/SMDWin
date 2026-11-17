using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        private void OpenSystemInfo_Click(object s, RoutedEventArgs e) => _ = OpenSystemInfoPage(tab: 0);

        private bool _powerProfileIsAc = true;

        /// <summary>Plugged in / On battery: which Windows profile the settings below show and change.</summary>
        internal void PowerProfileChip_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb) return;
            bool ac = rb.Tag?.ToString() == "AC";
            if (ac == _powerProfileIsAc) return;
            _powerProfileIsAc = ac;
            // Boost mode / processor state / USB are reloaded for the chosen profile;
            // Fast Startup has no AC/DC variants in Windows.
            LoadAdvancedPowerSection();
        }

        private void LoadAdvancedPowerSection()
        {
            try
            {
                var mode = SMDWin.Services.PowerAdvancedService.GetBoostMode(_powerProfileIsAc);
                if (BtnBoostModeDropdown != null)
                {
                    var lastSet = _powerProfileIsAc ? _lastSetBoostModeAc : _lastSetBoostModeDc;
                    int idx = mode.HasValue ? (int)mode.Value : (lastSet.HasValue ? (int)lastSet.Value : 1 /* Enabled */);
                    BtnBoostModeDropdown.Content = idx switch
                    {
                        0 => CmbBoostDisabled?.Content,
                        1 => CmbBoostEnabled?.Content,
                        2 => CmbBoostAggressive?.Content,
                        3 => CmbBoostEfficientEnabled?.Content,
                        4 => CmbBoostEfficientAggressive?.Content,
                        _ => CmbBoostEnabled?.Content,
                    };
                    BtnBoostModeDropdown.ToolTip = mode.HasValue ? null
                        : lastSet.HasValue
                            ? null
                            : "Could not read the current value from this system — showing \"Enabled\" (Windows default) as a starting point, not a confirmed reading. Administrator rights may be required.";
                }
            }
            catch (Exception ex) { AppLogger.Debug($"LoadAdvancedPowerSection.BoostMode: {ex.Message}"); }

            try
            {
                var sleep = SMDWin.Services.PowerAdvancedService.GetSleepStates();
                if (TxtSleepStates != null)
                {
                    string txt = sleep.HasModernStandby ? "Modern Standby (S0)"
                               : sleep.HasS3 ? "Standby (S3)"
                               : "Unknown";
                    if (sleep.HasHibernate) txt += " + Hibernate";
                    TxtSleepStates.Text = LanguageService.S("Power", "SleepStatesPrefix") + txt;
                    TxtSleepStates.ToolTip = null;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"LoadAdvancedPowerSection.SleepStates: {ex.Message}"); }

            try
            {
                _loadingProcStateSliders = true;
                _procStateTemporary = SMDWin.Services.TurboRestore.HasPendingFor(SMDWin.Services.TurboRestore.Part.ProcState);
                var min = SMDWin.Services.PowerAdvancedService.GetMinProcessorState(_powerProfileIsAc);
                var max = SMDWin.Services.PowerAdvancedService.GetMaxProcessorState(_powerProfileIsAc);
                _procStateMinPct = min ?? 5;
                _procStateMaxPct = max ?? 100;
                PositionProcStateThumbs();
                _loadingProcStateSliders = false;
            }
            catch (Exception ex) { AppLogger.Debug($"LoadAdvancedPowerSection.ProcState: {ex.Message}"); }

            try
            {
                var usb = SMDWin.Services.PowerAdvancedService.GetUsbSelectiveSuspend(_powerProfileIsAc);
                if (ChkUsbSelectiveSuspend != null) ChkUsbSelectiveSuspend.IsChecked = usb;
            }
            catch (Exception ex) { AppLogger.Debug($"LoadAdvancedPowerSection.UsbSuspend: {ex.Message}"); }

            try
            {
                // Fast Startup — has no AC/DC variants, a single registry value.
                var fastStartup = SMDWin.Services.PowerAdvancedService.GetFastStartup();
                if (ChkFastStartup != null) ChkFastStartup.IsChecked = fastStartup;
                if (ChkFastStartup != null && !fastStartup.HasValue)
                    ChkFastStartup.ToolTip = LanguageService.S("Battery", "HiberbootReadError");
            }
            catch (Exception ex) { AppLogger.Debug($"LoadAdvancedPowerSection.FastStartup: {ex.Message}"); }
        }

        private bool _loadingProcStateSliders;
        /// <summary>The processor range was changed "only while SMD Win is open" (restored on close).</summary>
        private bool _procStateTemporary;

        private double _procStateMinPct = 5;
        private double _procStateMaxPct = 100;
        private const double ProcStateThumbSize = 18;

        internal void ProcStateRangeTrack_SizeChanged(object sender, SizeChangedEventArgs e) => PositionProcStateThumbs();

        private void PositionProcStateThumbs()
        {
            if (ProcStateRangeTrack == null || ThumbProcStateMin == null || ThumbProcStateMax == null || ProcStateRangeFill == null) return;
            double w = ProcStateRangeTrack.ActualWidth;
            if (w <= ProcStateThumbSize) return; // the layout has not settled yet, SizeChanged will call again
            double usable = w - ProcStateThumbSize;
            double minX = (_procStateMinPct / 100.0) * usable;
            double maxX = (_procStateMaxPct / 100.0) * usable;
            Canvas.SetLeft(ThumbProcStateMin, minX);
            Canvas.SetLeft(ThumbProcStateMax, maxX);
            Canvas.SetLeft(ProcStateRangeFill, minX + ProcStateThumbSize / 2);
            ProcStateRangeFill.Width = Math.Max(0, (maxX + ProcStateThumbSize / 2) - (minX + ProcStateThumbSize / 2));
            if (TxtProcStateRange != null)
            {
                string range = $"{(int)_procStateMinPct}% – {(int)_procStateMaxPct}%";
                TxtProcStateRange.Text = _procStateTemporary
                    ? range + " · " + LanguageService.S("Stress", "TurboTagTemporary") : range;
                TxtProcStateRange.ToolTip = _procStateTemporary ? LanguageService.S("Power", "ProcStateTemporary") : null;
            }
        }

        internal void ThumbProcStateMin_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (ProcStateRangeTrack == null) return;
            double w = ProcStateRangeTrack.ActualWidth;
            if (w <= ProcStateThumbSize) return;
            double usable = w - ProcStateThumbSize;
            double currentX = (_procStateMinPct / 100.0) * usable + e.HorizontalChange;
            double pct = Math.Clamp(currentX / usable * 100.0, 0, 100);
            // The minimum handle cannot pass the maximum handle (keep at least 1% apart, so the handles do not overlap completely
            // and become impossible to grab separately).
            pct = Math.Min(pct, _procStateMaxPct - 1);
            if (pct < 0) pct = 0;
            _procStateMinPct = pct;
            PositionProcStateThumbs();
        }

        internal void ThumbProcStateMax_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (ProcStateRangeTrack == null) return;
            double w = ProcStateRangeTrack.ActualWidth;
            if (w <= ProcStateThumbSize) return;
            double usable = w - ProcStateThumbSize;
            double currentX = (_procStateMaxPct / 100.0) * usable + e.HorizontalChange;
            double pct = Math.Clamp(currentX / usable * 100.0, 0, 100);
            pct = Math.Max(pct, _procStateMinPct + 1);
            if (pct > 100) pct = 100;
            _procStateMaxPct = pct;
            PositionProcStateThumbs();
        }

        internal void ThumbProcState_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (_loadingProcStateSliders) return;
            try
            {
                int? oldMin = SMDWin.Services.PowerAdvancedService.GetMinProcessorState(_powerProfileIsAc);
                int? oldMax = SMDWin.Services.PowerAdvancedService.GetMaxProcessorState(_powerProfileIsAc);
                if (oldMin == (int)_procStateMinPct && oldMax == (int)_procStateMaxPct) return;   // nothing changed

                // like Turbo: the change lasts only while SMD Win is open, or stays permanently
                int keep = AskPowerPersistence(LanguageService.S("Power", "ProcStateKeepMsg"),
                                               LanguageService.S("Power", "ProcStateRangeTitle"));
                if (keep < 0) { LoadAdvancedPowerSection(); return; }
                if (keep == 0) SMDWin.Services.TurboRestore.RememberOriginalIfNeeded(SMDWin.Services.TurboRestore.Part.ProcState);

                // the previous values are written to the log, so they can be restored manually
                ToolActivityLog.Add("Power", $"CPU state {(_powerProfileIsAc ? "plugged in" : "battery")}: min {oldMin}% → {(int)_procStateMinPct}%, max {oldMax}% → {(int)_procStateMaxPct}% ({(keep == 1 ? "permanent" : "until SMD Win closes")})", "info");
                SMDWin.Services.PowerAdvancedService.SetMinProcessorState((int)_procStateMinPct, applyToAc: _powerProfileIsAc, applyToDc: !_powerProfileIsAc);
                SMDWin.Services.PowerAdvancedService.SetMaxProcessorState((int)_procStateMaxPct, applyToAc: _powerProfileIsAc, applyToDc: !_powerProfileIsAc);

                if (keep == 1) SMDWin.Services.TurboRestore.Clear(SMDWin.Services.TurboRestore.Part.ProcState);
                else SMDWin.Services.TurboRestore.ClearIfBackToOriginal(SMDWin.Services.TurboRestore.Part.ProcState);
                _procStateTemporary = SMDWin.Services.TurboRestore.HasPendingFor(SMDWin.Services.TurboRestore.Part.ProcState);
                PositionProcStateThumbs();
                InitTurboButtonState();   // the maximum state is part of the Turbo state too
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), LanguageService.S("Power", "ProcStateRangeTitle")); LoadAdvancedPowerSection(); }
        }

        internal void UsbSelectiveSuspend_Click(object sender, RoutedEventArgs e)
        {
            try { ToolActivityLog.Add("Power", $"USB selective suspend: was {(SMDWin.Services.PowerAdvancedService.GetUsbSelectiveSuspend(_powerProfileIsAc) == true ? "on" : "off")}, now {(ChkUsbSelectiveSuspend?.IsChecked == true ? "on" : "off")}", "info");
                  SMDWin.Services.PowerAdvancedService.SetUsbSelectiveSuspend(ChkUsbSelectiveSuspend?.IsChecked == true, applyToAc: _powerProfileIsAc, applyToDc: !_powerProfileIsAc); }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "USB Selective Suspend"); LoadAdvancedPowerSection(); }
        }

        internal void FastStartup_Click(object sender, RoutedEventArgs e)
        {
            try { ToolActivityLog.Add("Power", $"Fast startup: was {(SMDWin.Services.PowerAdvancedService.GetFastStartup() == true ? "on" : "off")}, now {(ChkFastStartup?.IsChecked == true ? "on" : "off")}", "info");
                  SMDWin.Services.PowerAdvancedService.SetFastStartup(ChkFastStartup?.IsChecked == true); }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "Fast Startup"); LoadAdvancedPowerSection(); }
        }

        private SMDWin.Services.PowerAdvancedService.BoostMode? _lastSetBoostModeAc;
        private SMDWin.Services.PowerAdvancedService.BoostMode? _lastSetBoostModeDc;

        internal void BoostModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;
            if (!int.TryParse(btn.Tag.ToString(), out int idx)) return;
            try
            {
                var mode = (SMDWin.Services.PowerAdvancedService.BoostMode)idx;
                if (BtnBoostModeDropdown != null) BtnBoostModeDropdown.IsChecked = false; // the popup closes before the question
                int keep = AskTurboPersistence();
                if (keep < 0) { LoadAdvancedPowerSection(); return; }
                if (keep == 0) SMDWin.Services.TurboRestore.RememberOriginalIfNeeded();
                SMDWin.Services.PowerAdvancedService.SetBoostMode(mode, applyToAc: _powerProfileIsAc, applyToDc: !_powerProfileIsAc);
                FinishTurboChange(keep, "boost mode " + SMDWin.Services.PowerAdvancedService.BoostModeLabel(mode));
                InitTurboButtonState();   // the Turbo button in Temp & Stress shows the same state
                if (_powerProfileIsAc) _lastSetBoostModeAc = mode; else _lastSetBoostModeDc = mode;
                AppLogger.Info($"Processor Boost Mode set to {SMDWin.Services.PowerAdvancedService.BoostModeLabel(mode)}");
                if (BtnBoostModeDropdown != null)
                {
                    BtnBoostModeDropdown.Content = btn.Content;
                    BtnBoostModeDropdown.ToolTip = null; // a selection now confirmed by the user, no longer an assumption
                    BtnBoostModeDropdown.IsChecked = false; // closes the popup
                }
            }
            catch (Exception ex)
            {
                if (BtnBoostModeDropdown != null) BtnBoostModeDropdown.IsChecked = false;
                AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "Processor Boost Mode");
                LoadAdvancedPowerSection(); // back to the real value, so the button does not stay on a selection that was not applied
            }
        }

        internal async void GenerateSleepReport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) btn.IsEnabled = false;
            try
            {
                ShowLoading(LanguageService.S("Msg", "SleepReportLoading"));
                var (ok, pathOrError) = await SMDWin.Services.PowerAdvancedService.GenerateSleepReportAsync();
                HideLoading();
                if (!ok) { AppDialog.Show(pathOrError, "Sleep Report", AppDialog.Kind.Warning); return; }
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrError) { UseShellExecute = true }); }
                catch (Exception exOpen) { AppDialog.Show(string.Format(LanguageService.S("Msg", "ReportSavedOpenFailFmt"), pathOrError, exOpen.Message)); }
            }
            catch (Exception ex) { HideLoading(); AppDialog.Show(ex.Message); }
            finally { if (sender is Button b) b.IsEnabled = true; }
        }

        internal async void GenerateEnergyReport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) btn.IsEnabled = false;
            var txt = TxtBtnEnergyReport;
            string originalText = txt?.Text ?? "";
            try
            {
                var cts = new System.Threading.CancellationTokenSource();
                var countdownTask = Task.Run(async () =>
                {
                    for (int s = 60; s > 0 && !cts.IsCancellationRequested; s--)
                    {
                        int remaining = s;
                        await Dispatcher.InvokeAsync(() => { if (txt != null) txt.Text = $"Monitoring… {remaining}s"; });
                        try { await Task.Delay(1000, cts.Token); } catch { break; }
                    }
                });

                var (ok, pathOrError) = await SMDWin.Services.PowerAdvancedService.GenerateEnergyReportAsync();
                cts.Cancel();
                try { await countdownTask; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (txt != null) txt.Text = originalText;

                if (!ok) { AppDialog.Show(pathOrError, "Energy Report", AppDialog.Kind.Warning); return; }
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrError) { UseShellExecute = true }); }
                catch (Exception exOpen) { AppDialog.Show(string.Format(LanguageService.S("Msg", "ReportSavedOpenFailFmt"), pathOrError, exOpen.Message)); }
            }
            catch (Exception ex) { if (txt != null) txt.Text = originalText; AppDialog.Show(ex.Message); }
            finally { if (sender is Button b) b.IsEnabled = true; }
        }

        private async void GenerateBatteryReport_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn) btn.IsEnabled = false;
            try
            {
                string reportPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"battery_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");

                ShowLoading(_LS("Battery", "GeneratingReport"));
                bool ok = await Task.Run(() =>
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("powercfg", $"/batteryreport /output \"{reportPath}\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        p?.WaitForExit(10000);
                        return System.IO.File.Exists(reportPath);
                    }
                    catch { return false; }
                });
                HideLoading();

                if (!ok)
                {
                    AppDialog.Show(_LS("Battery", "ReportFailed"),
"Battery Report", AppDialog.Kind.Warning);
                    return;
                }

                // Show in built-in viewer window
                var viewer = new BatteryReportViewer(reportPath) { Owner = this };
                viewer.Show();
            }
            catch (Exception ex)
            {
                HideLoading();
                AppDialog.Show(ex.Message);
            }
            finally
            {
                if (s is Button b) b.IsEnabled = true;
            }
        }
    }
}
