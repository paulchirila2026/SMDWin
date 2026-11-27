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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ── SMART tab ────────────────────────────────────────────────────────

        private string DiskComboName(DiskHealthEntry d)
        {
            string name = string.IsNullOrWhiteSpace(d.Model) ? $"Disk {d.DiskIndex}" : d.Model.Trim();
            var sameName = _allDisks.Where(x => string.Equals(
                (string.IsNullOrWhiteSpace(x.Model) ? $"Disk {x.DiskIndex}" : x.Model.Trim()), name,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameName.Count <= 1) return name;
            bool sameSizeToo = sameName.Count(x => x.Size == d.Size) > 1;
            return sameSizeToo ? $"{name} ({d.Size}) — disk {d.DiskIndex}" : $"{name} ({d.Size})";
        }

        private void PopulateSmartDiskCombo()
        {
            CbSmartDisk.Items.Clear();
            foreach (var d in _allDisks)
                CbSmartDisk.Items.Add(DiskComboName(d));
            if (CbSmartDisk.Items.Count > 0) CbSmartDisk.SelectedIndex = 0;
        }

        private DiskHealthEntry? _currentSmartDisk;

        internal void SmartDisk_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int idx = CbSmartDisk.SelectedIndex;
            if (idx < 0 || idx >= _allDisks.Count) return;
            var disk = _allDisks[idx];
            _currentSmartDisk = disk;

            SmartGrid.ItemsSource = SelectSmartGridSource(disk); ApplySmartColumns();

            var dvBadge = SMDWin.Services.DriveVerdict.Of(disk);
            TxtSmartHealthBadge.Text = dvBadge.PercentText;
            TxtSmartHealthBadge.SetResourceReference(TextBlock.ForegroundProperty, dvBadge.BrushKey);
            TxtSmartDiskType.Text = $"S/N: {disk.SerialNumber}  •  {disk.PhysicalDevicePath}";

            UpdateSelfTestPanel();
            UpdateSmartVerdict(disk); // the Verdict card

            // ── NVMe Diagnostics ─────────────────────────────────────────────
            bool isNvme = (disk.MediaType ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                       || (disk.Model ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                       || (disk.Model ?? "").Contains("M.2",  StringComparison.OrdinalIgnoreCase);

            if (NvmeDiagPanel != null)
                NvmeDiagPanel.Visibility = Visibility.Collapsed; // the NVMe readings go into the "All attributes" table

            if (isNvme)
                _ = LoadNvmeDiagnosticsAsync(disk); // fire-and-forget: async Task, exceptions are caught internally
        }

        private System.Windows.Threading.DispatcherTimer? _selfTestPollTimer;
        private bool     _selfTestActive;            // true from start until the end/abort
        private string   _selfTestDevicePath = "";   // the disk the test runs on (or last ran on)
        private string   _selfTestDiskName   = "";
        private string   _selfTestStatusText = "";   // the last status, shown again when the user returns to the disk
        private DateTime _selfTestDeadlineUtc;
        private int      _selfTestPollFailures;
        private bool     _selfTestPollInFlight;

        private bool IsSelfTestDiskSelected() =>
            _currentSmartDisk != null &&
            string.Equals(_currentSmartDisk.PhysicalDevicePath, _selfTestDevicePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Syncs the panel with the real state: visibility, buttons, status.</summary>
        private void UpdateSelfTestPanel()
        {
            if (SmartSelfTestPanel == null) return;
            var disk = _currentSmartDisk;
            bool canSelfTest = disk != null
                               && SMDWin.Services.SmartctlService.IsAvailable
                               && !string.IsNullOrWhiteSpace(disk.PhysicalDevicePath);
            SmartSelfTestPanel.Visibility = canSelfTest ? Visibility.Visible : Visibility.Collapsed;

            if (BtnSmartSelfTestShort    != null) BtnSmartSelfTestShort.Content    = PT("Short self-test");
            if (BtnSmartSelfTestExtended != null) BtnSmartSelfTestExtended.Content = PT("Extended self-test");
            if (BtnSmartSelfTestAbort    != null) BtnSmartSelfTestAbort.Content    = LanguageService.S("Smart", "AbortBtn", "Abort");

            bool runningHere = _selfTestActive && IsSelfTestDiskSelected();
            bool unsupported = disk != null && !_selfTestActive && SMDWin.Services.SmartctlService.IsDeviceUnsupported(disk.PhysicalDevicePath);
            // A single test tracked at a time - while one runs, no others are started.
            if (BtnSmartSelfTestShort    != null) BtnSmartSelfTestShort.IsEnabled    = !_selfTestActive && !unsupported;
            if (BtnSmartSelfTestExtended != null) BtnSmartSelfTestExtended.IsEnabled = !_selfTestActive && !unsupported;
            // the disk cannot run a self-test: the buttons disappear (two grey buttons say nothing), only the explanation stays
            if (BtnSmartSelfTestShort    != null) BtnSmartSelfTestShort.Visibility    = unsupported ? Visibility.Collapsed : Visibility.Visible;
            if (BtnSmartSelfTestExtended != null) BtnSmartSelfTestExtended.Visibility = unsupported ? Visibility.Collapsed : Visibility.Visible;
            if (BtnSmartSelfTestAbort    != null) BtnSmartSelfTestAbort.Visibility   = runningHere ? Visibility.Visible : Visibility.Collapsed;
            if (SmartSelfTestProgress    != null && !runningHere) SmartSelfTestProgress.Visibility = Visibility.Collapsed;

            if (TxtSmartSelfTestStatus != null)
            {
                // the row has room for a short text; the full explanation stays in the tooltip
                string status, tip;
                if (IsSelfTestDiskSelected() && _selfTestStatusText.Length > 0)
                    status = tip = _selfTestStatusText;   // test in progress or the last result on this disk
                else if (_selfTestActive)
                    status = tip = string.Format(LanguageService.S("Smart", "TestOtherDrive"), _selfTestDiskName);
                else if (unsupported)
                {
                    status = LanguageService.S("Smart", "SelfTestUnsupportedShort", "Self-test not available for this drive");
                    tip = LanguageService.S("Smart", "SelfTestUnsupported");
                }
                else status = tip = "";
                ShowSelfTestStatus(status, tip);
            }
        }

        private void SetSelfTestProgress(double? percent, bool visible)
        {
            if (SmartSelfTestProgress == null) return;
            SmartSelfTestProgress.Visibility = visible && IsSelfTestDiskSelected() ? Visibility.Visible : Visibility.Collapsed;
            if (percent.HasValue) { SmartSelfTestProgress.IsIndeterminate = false; SmartSelfTestProgress.Value = Math.Clamp(percent.Value, 0, 100); }
            else SmartSelfTestProgress.IsIndeterminate = true;
        }

        private void SetSelfTestStatus(string text)
        {
            _selfTestStatusText = text;
            if (IsSelfTestDiskSelected()) ShowSelfTestStatus(text, text);
        }

        /// <summary>The self-test status text: a single line, cut with "…" when long; full in the tooltip.</summary>
        private void ShowSelfTestStatus(string text, string tooltip)
        {
            if (TxtSmartSelfTestStatus == null) return;
            TxtSmartSelfTestStatus.Text = text;
            TxtSmartSelfTestStatus.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        }

        internal async void SmartSelfTestShort_Click(object sender, RoutedEventArgs e) => await StartSelfTest("short");
        internal async void SmartSelfTestExtended_Click(object sender, RoutedEventArgs e)
        {
            string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
            {
                "en" => en, "de" => de, "fr" => fr, _ => ro
            };
            bool confirmed = SMDWin.Views.AppDialog.Confirm(
                L("Extended test can take several hours and uses the drive intensively while running (though the OS/apps keep working normally). Continue?",
                  "Testul extins poate dura câteva ore și folosește discul intensiv cât rulează (deși OS-ul/aplicațiile funcționează normal în paralel). Continuăm?",
                  "Der erweiterte Test kann mehrere Stunden dauern und beansprucht das Laufwerk währenddessen stark. Fortfahren?",
                  "Le test étendu peut prendre plusieurs heures et sollicite intensivement le disque pendant son exécution. Continuer ?"),
                "SMD Win");
            if (confirmed) await StartSelfTest("long");
        }

        internal async void SmartSelfTestAbort_Click(object sender, RoutedEventArgs e)
        {
            if (!_selfTestActive) return;
            SetSelfTestStatus(LanguageService.S("Smart", "Stopping"));
            bool ok = false;
            try { ok = await SMDWin.Services.SmartctlService.AbortSelfTestAsync(_selfTestDevicePath); }
            catch (Exception ex) { AppLogger.Warning(ex, "SmartSelfTestAbort_Click"); }

            if (ok)
            {
                FinishSelfTest(LanguageService.S("Smart", "TestStopped"));
            }
            else
            {
                // The test may still be running — polling continues, the stop button stays visible.
                SetSelfTestStatus(LanguageService.S("Smart", "TestStopFailed"));
                UpdateSelfTestPanel();
            }
        }

        private async Task StartSelfTest(string type)
        {
            var disk = _currentSmartDisk;
            if (disk == null || _selfTestActive) return;

            _selfTestActive     = true;   // also blocks a second start while waiting for the smartctl answer
            SetNavTaskBadge("TaskBadgeDisk", true);
            _selfTestDevicePath = disk.PhysicalDevicePath;
            _selfTestDiskName   = disk.Model ?? disk.PhysicalDevicePath;
            SetSelfTestStatus(LanguageService.S("Smart", "Starting"));
            UpdateSelfTestPanel();
            SetSelfTestProgress(null, true);

            try
            {
                var (started, message) = await SMDWin.Services.SmartctlService.StartSelfTestAsync(_selfTestDevicePath, type);
                if (!started)
                {
                    FinishSelfTest(LanguageService.S("Smart", "TestStartFailedPrefix") + message);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "StartSelfTest");
                FinishSelfTest(LanguageService.S("Smart", "TestStartFailedPrefix") + ex.Message);
                return;
            }

            SetSelfTestStatus(LanguageService.S("Smart", "TestRunning"));
            _selfTestPollFailures = 0;
            // Tracking limit: a short test takes minutes, an extended one can take hours on a large HDD.
            _selfTestDeadlineUtc = DateTime.UtcNow + (type == "long" ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(30));

            // Poll every 15 s for progress — the self-test runs on the disk controller, the UI is not blocked, the status is just
            // checked periodically via smartctl -a.
            _selfTestPollTimer?.Stop();
            _selfTestPollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _selfTestPollTimer.Tick += async (_, _) => await PollSelfTestProgress();
            _selfTestPollTimer.Start();
            UpdateSelfTestPanel();
        }

        private async Task PollSelfTestProgress()
        {
            if (!_selfTestActive) { _selfTestPollTimer?.Stop(); return; }
            if (_selfTestPollInFlight) return;   // the previous read is still running
            _selfTestPollInFlight = true;
            try
            {
                var report = await SMDWin.Services.SmartctlService.ReadAsync(_selfTestDevicePath, forceFresh: true);
                if (!_selfTestActive) return;    // stopped in the meantime (Abort)

                if (!report.Success)
                {
                    _selfTestPollFailures++;
                    AppLogger.Debug("Self-test poll failed ({N}): {Msg}", _selfTestPollFailures, report.ErrorMessage);
                    if (_selfTestPollFailures >= 4)   // ~1 minute without an answer — give up tracking
                        FinishSelfTest(LanguageService.S("Smart", "TestProgressUnavailable"));
                    return;
                }
                _selfTestPollFailures = 0;

                if (report.SelfTestInProgress)
                {
                    // The percentage is shown only if the disk actually reported it (otherwise 0% remaining would falsely show "100% done").
                    SetSelfTestStatus(report.SelfTestPercentKnown
                        ? string.Format(LanguageService.S("Smart", "TestRunningPct"), 100 - report.SelfTestPercentRemaining)
                        : LanguageService.S("Smart", "TestRunning"));
                    SetSelfTestProgress(report.SelfTestPercentKnown ? 100 - report.SelfTestPercentRemaining : (double?)null, true);
                    if (DateTime.UtcNow > _selfTestDeadlineUtc)
                        FinishSelfTest(LanguageService.S("Smart", "TestProgressUnavailable"));
                    return;
                }

                // The test finished — show the result from the log and keep it visible.
                string resultText = report.SelfTestLog.Count > 0
                    ? $"{report.SelfTestLog[0].Type}: {report.SelfTestLog[0].Status}"
                    : LanguageService.S("Smart", "TestFinished");
                string finishedPath = _selfTestDevicePath;
                FinishSelfTest(resultText);
                await RefreshSmartGridAfterSelfTestAsync(finishedPath, report);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PollSelfTestProgress"); }
            finally { _selfTestPollInFlight = false; }
        }

        private async Task RefreshSmartGridAfterSelfTestAsync(string devicePath, SMDWin.Services.SmartctlReport report)
        {
            try
            {
                var disk = _allDisks.FirstOrDefault(d => string.Equals(d.PhysicalDevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
                if (disk == null || !HardwareService.ApplySmartctlAttributes(disk, report)) return;
                // The grid is updated only if the tested disk is still the one shown.
                if (ReferenceEquals(_currentSmartDisk, disk) && SmartGrid != null)
                    SmartGrid.ItemsSource = SelectSmartGridSource(disk); ApplySmartColumns();
            }
            catch (Exception ex) { AppLogger.Debug("RefreshSmartGridAfterSelfTestAsync: {Msg}", ex.Message); }
            await Task.CompletedTask;
        }

        private void FinishSelfTest(string statusText)
        {
            _selfTestActive = false;
            SetNavTaskBadge("TaskBadgeDisk", false);
            _selfTestPollTimer?.Stop();
            SetSelfTestProgress(null, false);
            SetSelfTestStatus(statusText);
            UpdateSelfTestPanel();
        }

        private async Task LoadNvmeDiagnosticsAsync(DiskHealthEntry disk)
        {
            try
            {
                if (SMDWin.Services.SmartctlService.IsAvailable && !string.IsNullOrWhiteSpace(disk.PhysicalDevicePath))
                {
                    var report = await SMDWin.Services.SmartctlService.ReadAsync(disk.PhysicalDevicePath);
                    if (report.Success && report.IsNvme)
                    {
                        bool anyRealData = report.NvmeAvailableSparePct.HasValue || report.NvmePercentageUsed.HasValue
                                            || report.NvmeDataUnitsWrittenGB.HasValue || report.PowerCycleCount >= 0
                                            || report.NvmeMediaErrors.HasValue;
                        if (!anyRealData)
                        {
                            await Dispatcher.InvokeAsync(() => { if (NvmeDiagPanel != null) NvmeDiagPanel.Visibility = Visibility.Collapsed; });
                            return;
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (TxtNvmeSpare != null)
                                TxtNvmeSpare.Text = report.NvmeAvailableSparePct.HasValue ? $"{report.NvmeAvailableSparePct}%" : "—";
                            if (TxtNvmeWear != null)
                                TxtNvmeWear.Text = report.NvmePercentageUsed.HasValue ? string.Format(_LS("Disk", "NvmeUsedFmt"), report.NvmePercentageUsed) : "—";
                            if (TxtNvmeWritten != null)
                                TxtNvmeWritten.Text = report.NvmeDataUnitsWrittenGB.HasValue
                                    ? (report.NvmeDataUnitsWrittenGB >= 1024 ? $"{report.NvmeDataUnitsWrittenGB / 1024.0:F1} TB" : $"{report.NvmeDataUnitsWrittenGB} GB")
                                    : "—";
                            if (TxtNvmePowerCycles != null)
                                TxtNvmePowerCycles.Text = report.PowerCycleCount >= 0 ? report.PowerCycleCount.ToString("N0") : "—";
                            if (TxtNvmeBusy != null)
                                TxtNvmeBusy.Text = report.NvmeMediaErrors.HasValue ? string.Format(_LS("Disk", "NvmeErrorsFmt"), report.NvmeMediaErrors) : "—";
                            if (TxtNvmeNote != null)
                                TxtNvmeNote.Text = _LS("Disk", "NvmeHealthNote");

                            if (TxtNvmeSpare != null && report.NvmeAvailableSparePct.HasValue)
                            {
                                int spVal = report.NvmeAvailableSparePct.Value;
                                TxtNvmeSpare.Foreground = new SolidColorBrush(
                                    spVal >= 50 ? ThemeColor("StatusSuccessColor", WpfColor.FromRgb(34, 197, 94))  :
                                    spVal >= 20 ? ThemeColor("StatusWarningColor", WpfColor.FromRgb(245, 158, 11)) :
                                                  ThemeColor("StatusErrorColor",   WpfColor.FromRgb(239, 68, 68)));
                            }
                            // media_errors > 0 is a real alarm signal — colour it too
                            if (TxtNvmeBusy != null && report.NvmeMediaErrors.HasValue)
                            {
                                TxtNvmeBusy.Foreground = new SolidColorBrush(
                                    report.NvmeMediaErrors.Value == 0
                                        ? ThemeColor("StatusSuccessColor", WpfColor.FromRgb(34, 197, 94))
                                        : ThemeColor("StatusErrorColor",   WpfColor.FromRgb(239, 68, 68)));
                            }
                            MergeNvmeRowsIntoSmartGrid(disk, report);
                        });
                        return; // we have good data from smartctl — no fallback to WMI
                    }
                }

                bool reliabilityExposed = false;
                string? spare = null, wear = null, written = null, cycles = null, busy = null, note = null;

                await Task.Run(() =>
                {
                    try
                    {
                        using var s = WmiHelper.Searcher(
                            @"\\.\root\Microsoft\Windows\Storage",
                            $"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{disk.DiskIndex}'",
                            TimeSpan.FromSeconds(5));

                        using (var __wmiTmp1 = s.Get())
                        {
                            foreach (System.Management.ManagementObject obj in __wmiTmp1)
                        {
                            reliabilityExposed = true;

                            // Available Spare — % of spare blocks remaining (100 = new)
                            var sp = obj["ReadErrorsCorrected"];
                            var spareVal = obj["FlushLatencyMax"];

                            // Use the documented fields of MSFT_StorageReliabilityCounter
                            try
                            {
                                var wearPct = obj["Wear"];
                                if (wearPct != null) wear = $"{wearPct}%";
                            }
                            catch (Exception ex) { AppLogger.Debug(ex.Message); }

                            try
                            {
                                var spareRaw = obj["PowerOnHours"]; // fallback
                                // Available spare has no direct field — use SMART 0xAB or 0xE8
                                var spareAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xAB || a.Id == 0xE8 || a.Id == 0xE9);
                                if (spareAttr != null) spare = $"{spareAttr.CurrentValue}%";
                            }
                            catch (Exception ex) { AppLogger.Debug(ex.Message); }

                            try
                            {
                                var writtenRaw = obj["WriteLatencyMax"];
                                var tbwAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 241 || a.Id == 242 || a.Id == 0xF1);
                                if (tbwAttr != null)
                                {
                                    long gb = tbwAttr.RawValue / (2 * 1024 * 1024);
                                    written = gb >= 1024 ? $"{gb / 1024.0:F1} TB" : $"{gb} GB";
                                }
                            }
                            catch (Exception ex) { AppLogger.Debug(ex.Message); }

                            break;
                        }
                        }
                    }
                    catch { note = "MSFT_StorageReliabilityCounter indisponibil pe acest sistem."; }

                    if (spare == null && reliabilityExposed)
                    {
                        var spareAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xAB || a.Id == 0xE8 || a.Id == 0xE9 || a.Id == 232);
                        if (spareAttr != null) spare = $"{spareAttr.CurrentValue}%";
                    }
                    if (wear == null)
                    {
                        var wearAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 177 || a.Id == 231 || a.Id == 233);
                        if (wearAttr != null) wear = $"{100 - wearAttr.CurrentValue}% used";
                    }
                    if (written == null && reliabilityExposed)
                    {
                        var tbw = disk.SmartAttributes.FirstOrDefault(a => a.Id == 241 || a.Id == 0xF1);
                        if (tbw != null)
                        {
                            long gb = tbw.RawValue / (2 * 1024 * 1024);
                            written = gb >= 1024 ? $"{gb / 1024.0:F1} TB" : $"{gb} GB";
                        }
                    }

                    if (!reliabilityExposed && note == null)
                        note = "Not exposed by this drive/driver (no Windows storage reliability counters available for it) — install smartctl.exe (Tools\\smartctl.exe) for accurate NVMe data.";

                    var pohAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 12); // Power Cycles
                    if (pohAttr != null) cycles = pohAttr.RawValue.ToString("N0");

                    var busyAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xBE || a.Id == 190);
                    if (busyAttr == null) busy = "—";
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    bool anyRealData = spare != null || wear != null || written != null || cycles != null
                                        || (busy != null && busy != "—");
                    if (!anyRealData)
                    {
                        if (NvmeDiagPanel != null) NvmeDiagPanel.Visibility = Visibility.Collapsed;
                        return;
                    }

                    if (TxtNvmeSpare       != null) TxtNvmeSpare.Text        = spare   ?? "—";
                    if (TxtNvmeWear        != null) TxtNvmeWear.Text         = wear    ?? "—";
                    if (TxtNvmeWritten     != null) TxtNvmeWritten.Text      = written ?? "—";
                    if (TxtNvmePowerCycles != null) TxtNvmePowerCycles.Text  = cycles  ?? "—";
                    if (TxtNvmeBusy       != null) TxtNvmeBusy.Text         = busy    ?? "—";
                    if (TxtNvmeNote       != null) TxtNvmeNote.Text         = note    ?? "Date citite din atributele SMART disponibile (instalează smartctl pentru date NVMe mai precise — vezi Tools\\smartctl.exe).";

                    // Colour Available Spare
                    if (TxtNvmeSpare != null && spare != null && int.TryParse(spare.Replace("%",""), out int spVal))
                    {
                        TxtNvmeSpare.Foreground = new SolidColorBrush(
                            spVal >= 50 ? ThemeColor("StatusSuccessColor", WpfColor.FromRgb(34, 197, 94))  :
                            spVal >= 20 ? ThemeColor("StatusWarningColor", WpfColor.FromRgb(245, 158, 11)) :
                                          ThemeColor("StatusErrorColor",   WpfColor.FromRgb(239, 68, 68)));
                    }
                });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadNvmeDiagnosticsAsync"); }
        }

        /// <summary>"Show all" in the table header: false = attributes without a standard meaning and with value 0 are hidden.</summary>
        private static bool _smartShowAll;
        private DiskHealthEntry? _smartGridDisk;

        private System.Collections.IEnumerable SelectSmartGridSource(DiskHealthEntry disk)
        {
            _smartGridDisk = disk;
            if (disk.NvmeHealthRows.Count > 0) { UpdateSmartShowAllLink(0); return disk.NvmeHealthRows; }
            if (disk.SmartAttributes.Count > 0)
            {
                int hidden = disk.SmartAttributes.Count(a => a.IsRoutine);
                UpdateSmartShowAllLink(hidden);
                return _smartShowAll || hidden == 0 ? disk.SmartAttributes : disk.SmartAttributes.Where(a => !a.IsRoutine).ToList();
            }
            UpdateSmartShowAllLink(0);
            return GetFallbackSmartAttributes(disk);
        }

        private void UpdateSmartShowAllLink(int hidden)
        {
            if (FindName("BtnSmartShowAll") is not TextBlock link) return;
            link.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
            link.Text = _smartShowAll ? PT("Show fewer") : PT("Show all (+{0})", hidden);
            link.ToolTip = MainWindow.PT("Values with no standard meaning that are 0 (vendor counters)");
        }

        /// <summary>NVMe disks report no Value/Worst/Limit figures: those three columns are hidden when no row has them.</summary>
        private void ApplySmartColumns()
        {
            try
            {
                if (SmartGrid?.ItemsSource is not System.Collections.IEnumerable src) return;
                bool any = false;
                foreach (var o in src)
                {
                    if (o is NvmeHealthRow nv) { if (nv.CurrentValue.Length > 0) { any = true; break; } }
                    else { any = true; break; }
                }
                var vis = any ? Visibility.Visible : Visibility.Collapsed;
                for (int i = 2; i <= 4 && i < SmartGrid.Columns.Count; i++) SmartGrid.Columns[i].Visibility = vis;
            }
            catch (Exception ex) { AppLogger.Debug($"ApplySmartColumns: {ex.Message}"); }
        }

        internal void SmartShowAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _smartShowAll = !_smartShowAll;
            if (_smartGridDisk != null) SmartGrid.ItemsSource = SelectSmartGridSource(_smartGridDisk); ApplySmartColumns();
        }

        private static List<SMDWin.Models.SmartInfoRow> GetFallbackSmartAttributes(DiskHealthEntry disk)
        {
            string reason = SMDWin.Services.SmartctlService.GetUnsupportedReason(disk.PhysicalDevicePath);
            string smartText = reason.Length > 0
                ? string.Format(SMDWin.Services.LanguageService.S("Disk", "SmartctlUnsupported"), reason)
                : (SMDWin.Services.PermissionHelper.IsRunningAsAdmin()
                    ? SMDWin.Services.LanguageService.S("Disk", "SmartUnavailableAdmin")
                    : SMDWin.Services.LanguageService.S("Disk", "SmartUnavailableNeedsAdmin"));

            bool healthy = string.Equals(disk.Status, "Healthy", StringComparison.OrdinalIgnoreCase);
            return new List<SMDWin.Models.SmartInfoRow>
            {
                new() { Name = "Model", FriendlyDescription = disk.Model },
                new() { Name = SMDWin.Services.LanguageService.S("Disk", "FallbackSize"),      FriendlyDescription = disk.Size },
                new() { Name = SMDWin.Services.LanguageService.S("Disk", "FallbackMediaType"), FriendlyDescription = disk.MediaType },
                new() { Name = "Status", FriendlyDescription = disk.Status,
                        TrafficLightColor = healthy ? "#22C55E" : "#F59E0B",
                        StatusText = healthy ? $"✔ {SMDWin.Services.LanguageService.S("Smart", "Ok", "OK")}"
                                             : $"⚠ {SMDWin.Services.LanguageService.S("Smart", "Warning", "WARNING")}" },
                new() { Name = "SMART", FriendlyDescription = smartText },
            };
        }
    }
}
