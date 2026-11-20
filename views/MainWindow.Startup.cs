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
        // ── STARTUP MANAGER ───────────────────────────────────────────────────

        private bool _accountMismatchWarned;

        // The list takes a few seconds to read (schtasks is slow), so on a simple return to the page the recently read one is
        // reused; Refresh and any change read it again.
        private DateTime _startupEntriesLoadedUtc = DateTime.MinValue;
        private bool _startupEntriesShowAll;
        private const int StartupEntriesCacheSeconds = 180;

        private async Task LoadStartupAsync(bool useCache = false)
        {
            if (useCache && _allStartupEntries != null && _allStartupEntries.Count > 0
                && _startupEntriesShowAll == SettingsService.Current.StartupShowAllServices
                && (DateTime.UtcNow - _startupEntriesLoadedUtc).TotalSeconds < StartupEntriesCacheSeconds)
            {
                if (ChkStartupShowAllServices != null)
                    ChkStartupShowAllServices.IsChecked = SettingsService.Current.StartupShowAllServices;
                SyncStartupSegmentedVisual(SettingsService.Current.StartupShowAllServices);
                ApplyStartupView();
                _ = LoadBootTimeAsync();
                return;
            }

            ShowLoading(_LS("Startup", "Loading"));
            try
            {
                // Sync the checkbox (hidden, only for compatibility) and the visible segmented control with the saved setting —
                // idempotent (WPF does not fire Checked/Unchecked if the value does not actually change), safe to call on every load
                // without a risk of a double reload.
                if (ChkStartupShowAllServices != null)
                    ChkStartupShowAllServices.IsChecked = SettingsService.Current.StartupShowAllServices;
                SyncStartupSegmentedVisual(SettingsService.Current.StartupShowAllServices);

                bool showAll = ChkStartupShowAllServices?.IsChecked == true;
                // stage 1 (fast): registry, folders, services — appears immediately. The scheduled tasks (schtasks, ~12 s) come after.
                var entries = await Task.Run(() => _startupSvc.GetStartupEntriesAsync(showAll, includeTasks: false).GetAwaiter().GetResult());
                // The target of each entry: a missing file (invalid entry) + impact by the program size
                await Task.Run(() => { foreach (var en in entries) { try { en.AnalyzeTarget(); } catch (Exception ex) { AppLogger.Debug($"AnalyzeTarget {en.Name}: {ex.Message}"); } } });
                // what Windows measured as slowing the start (by file name), and which programs appeared recently
                _startupDelays = await Task.Run(() => StartupDelayService.Recent());
                await Task.Run(() => MarkStartupDelays(entries, _startupDelays));
                _allStartupEntries = entries;
                _startupEntriesLoadedUtc = DateTime.UtcNow;
                _startupEntriesShowAll = showAll;
                ApplyStartupView(); // also sets TxtStartupCount.Text

                // if the app runs as Administrator through an account different from the interactively logged-in one (common in a
                // service shop/school), the HKCU that Startup Manager reads/writes belongs to the admin account, not the real user —
                // the user's startup entries do not appear here and are not changed. Warn once per session, on the first page open.
                if (!_accountMismatchWarned && SMDWin.Services.AccountMismatchDetector.IsRunningAsDifferentUser())
                {
                    _accountMismatchWarned = true;
                    ShowToastWarning(_LS("Startup", "AccountMismatchWarning"), 9000);
                }
            }
            finally { HideLoading(); }
            _ = LoadBootTimeAsync();
            _ = LoadStartupTasksInBackgroundAsync(_startupEntriesLoadedUtc);
        }

        /// <summary>Stage 2: the scheduled tasks are added to the list after the rest is already visible.</summary>
        private async Task LoadStartupTasksInBackgroundAsync(DateTime generation)
        {
            try
            {
                if (TxtStartupCount != null) TxtStartupCount.Text += " · loading scheduled tasks…";
                var tasks = await _startupSvc.GetScheduledTaskEntriesAsync();
                await Task.Run(() => { foreach (var en in tasks) { try { en.AnalyzeTarget(); } catch (Exception ex) { AppLogger.Debug($"AnalyzeTarget {en.Name}: {ex.Message}"); } } });
                // the list was reloaded in the meantime: the old result is discarded
                if (generation != _startupEntriesLoadedUtc || _allStartupEntries == null) return;
                _allStartupEntries.AddRange(tasks);
                _allStartupEntries = _allStartupEntries.OrderBy(e => e.Location).ThenBy(e => e.Name).ToList();
                ApplyStartupView();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadStartupTasksInBackgroundAsync"); }
        }

        // ── Boot Time Breakdown ──────────────────────────────────────────────
        private async void RefreshBootTime_Click(object sender, RoutedEventArgs e) => await LoadBootTimeAsync();

        private async Task LoadBootTimeAsync()
        {
            try
            {
                var boots = await SMDWin.Services.BootTimeService.GetRecentBootsAsync(14);

                if (boots.Count == 0)
                {
                    TxtBootTimeEmpty.Text = LanguageService.S("Startup", "BootTimeUnavailable");
                    TxtBootTimeEmpty.Visibility = Visibility.Visible;
                    PanelBootTimeSummary.Visibility = Visibility.Collapsed;
                    BootDegradationBanner.Visibility = Visibility.Collapsed;
                    ListBootHistory.ItemsSource = null;
                    TxtBootTimeSub.Text = "No start recorded yet";
                    TxtBootLastDate.Text = "—";
                    return;
                }

                TxtBootTimeEmpty.Visibility = Visibility.Collapsed;
                PanelBootTimeSummary.Visibility = Visibility.Visible;

                var latest = boots[0];
                double avgMs = boots.Average(b => (double)b.TotalBootMs);
                var slowest = boots.OrderByDescending(b => b.TotalBootMs).First();

                TxtBootTimeSub.Text = boots.Count == 1 ? "Last start" : $"Last {boots.Count} starts";
                TxtBootTotalTime.Text = FormatBootMs(latest.TotalBootMs);
                TxtBootAvg.Text       = FormatBootMs((int)avgMs);
                TxtBootSlowest.Text   = $"{FormatBootMs(slowest.TotalBootMs)} · {slowest.BootDate:dd.MM}";
                TxtBootSlowest.SetResourceReference(TextBlock.ForegroundProperty,
                    slowest.TotalBootMs > avgMs * 1.25 ? "StatusWarningBrush" : "TextPrimaryBrush");
                TxtBootMainPath.Text  = FormatBootMs(latest.MainPathBootMs);
                TxtBootPostBoot.Text  = FormatBootMs(latest.PostBootDisplay);
                TxtBootLastDate.Text  = SMDWin.Services.DateFormatHelper.FormatDate(latest.BootDate) + " " + latest.BootDate.ToString("HH:mm");

                BootDegradationBanner.Visibility = latest.IsDegradation ? Visibility.Visible : Visibility.Collapsed;
                if (latest.IsDegradation)
                    TxtBootDegradationInfo.Text = "Windows marked the last start as slower than usual — look for anything new in the list below, or check the drivers.";

                ListBootHistory.ItemTemplate = null;
                ListBootHistory.ItemsSource = new[] { BuildBootHistoryChart(boots) };
            }
            catch (Exception ex) { AppLogger.Debug($"LoadBootTimeAsync: {ex.Message}"); }
        }

        /// <summary>The boot chart: one column per boot (old → new), blue below the average, orange above, a dotted line at the
        /// average, the date under each column; tooltip with the date and the times (the Windows part / after sign-in).</summary>
        private UIElement BuildBootHistoryChart(IList<SMDWin.Services.BootTimeEntry> boots)
        {
            const double H = 96;
            var ordered = boots.Reverse().ToList();   // oldest first
            double maxMs = Math.Max(1, ordered.Max(b => (double)b.TotalBootMs)) * 1.1;
            double avgMs = ordered.Average(b => (double)b.TotalBootMs);

            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(H) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cols = new Grid();
            var dates = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(dates, 1);
            for (int i = 0; i < ordered.Count; i++)
            {
                var b = ordered[i];
                cols.ColumnDefinitions.Add(new ColumnDefinition());
                dates.ColumnDefinitions.Add(new ColumnDefinition());
                bool latest = i == ordered.Count - 1;
                bool slow = b.TotalBootMs > avgMs * 1.1;
                var bar = new Border
                {
                    Height = Math.Max(3, b.TotalBootMs / maxMs * H), MaxWidth = 22,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(3, 3, 0, 0), Margin = new Thickness(3, 0, 3, 0),
                    Opacity = latest ? 1.0 : 0.75,
                    ToolTip = $"{SMDWin.Services.DateFormatHelper.FormatDate(b.BootDate)} {b.BootDate:HH:mm} — {FormatBootMs(b.TotalBootMs)}" +
                              $"\nWindows part: {FormatBootMs(b.MainPathBootMs)} · after sign-in: {FormatBootMs(b.PostBootDisplay)}" +
                              (b.LogonWaitMs > 1000 ? "\n" + PT("Waited at the sign-in screen: {0} (not counted)", FormatBootMs(b.LogonWaitMs)) : "") +
                              (b.IsDegradation ? "\nWindows marked this start as slower than usual" : ""),
                };
                bar.SetResourceReference(Border.BackgroundProperty, slow ? "StatusWarningBrush" : "AccentBrush");
                Grid.SetColumn(bar, i);
                cols.Children.Add(bar);

                var d = new TextBlock { Text = b.BootDate.ToString("dd.MM"), FontSize = Ui.FontXS, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                d.Opacity = latest ? 1.0 : 0.7;
                Grid.SetColumn(d, i);
                dates.Children.Add(d);
            }
            host.Children.Add(cols);
            host.Children.Add(dates);

            // The average line + its label
            double avgY = H - avgMs / maxMs * H;
            var avgLine = new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = 1, Stretch = Stretch.Fill, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, avgY, 0, 0), IsHitTestVisible = false, Opacity = 0.7,
            };
            avgLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
            host.Children.Add(avgLine);
            var avgLbl = new Border { Padding = new Thickness(4, 0, 4, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                                      VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Max(0, avgY - 16), 0, 0), IsHitTestVisible = false };
            avgLbl.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            var avgTxt = new TextBlock { Text = $"avg {FormatBootMs((int)avgMs)}", FontSize = Ui.FontXS };
            avgTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            avgLbl.Child = avgTxt;
            host.Children.Add(avgLbl);
            return host;
        }

        private static string FormatBootMs(int ms) =>
            ms >= 1000 ? $"{ms / 1000.0:F1} s" : $"{ms} ms";

        private void ApplyStartupView()
        {
            if (_allStartupEntries == null) return;

            var q = TxtStartupSearch?.Text.Trim() ?? "";
            var all = _allStartupEntries;
            // Active Setup entries run once for each new user, not at every start: listed only with "Show services" (advanced)
            bool advanced = ChkStartupShowAllServices?.IsChecked == true;
            var view = all
                .Where(e => advanced || !e.Location.Contains("Active Setup"))
                .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            e.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            e.Command.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsEnabled)
                .ThenByDescending(e => e.ImpactRating == "High" ? 3 : e.ImpactRating == "Medium" ? 2 : e.ImpactRating == "Low" ? 1 : 0)
                .ThenBy(e => e.Name)
                .ToList();

            string? keep = SelectedStartup?.Command;
            string? keepName = SelectedStartup?.Name;
            StartupGrid.ItemsSource = view;
            if (keep != null)
                StartupGrid.SelectedItem = view.FirstOrDefault(e => e.Command == keep && e.Name == keepName);

            int on = all.Count(e => e.IsEnabled), off = all.Count - on;
            int high = all.Count(e => e.IsEnabled && e.ImpactRating == "High");
            int broken = all.Count(e => e.IsBroken);
            TxtStartupCount.Text = $"{all.Count} entries · {on} on" + (q.Length > 0 ? $" · {view.Count} match" : "");
            TxtStartupFootOn.Text = $"{on} on · {off} off";
            TxtStartupFootHigh.Text = high.ToString();
            TxtStartupFootHigh.SetResourceReference(TextBlock.ForegroundProperty, high > 0 ? "StatusWarningBrush" : "TextPrimaryBrush");
            TxtStartupFootBroken.Text = broken.ToString();
            TxtStartupFootBroken.SetResourceReference(TextBlock.ForegroundProperty, broken > 0 ? "StatusErrorBrush" : "TextPrimaryBrush");

            StartupEmptyState.Visibility = view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (StartupGrid.SelectedItem == null) ShowStartupDetail(null);
            RefreshStartupSummary();
        }

        private void StartupSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyStartupView();

        /// <summary>The "Selected" card: what starts, from where, how heavy, and the buttons.</summary>
        private void ShowStartupDetail(StartupEntry? en)
        {
            if (en == null)
            {
                StartupDetailPanel.Visibility = Visibility.Collapsed;
                StartupDetailEmpty.Visibility = Visibility.Visible;
                return;
            }
            StartupDetailEmpty.Visibility = Visibility.Collapsed;
            StartupDetailPanel.Visibility = Visibility.Visible;

            TxtStDetName.Text = en.Name;
            (string pill, string brush) = en.IsBroken ? ("File not found", "StatusErrorBrush")
                                        : en.ImpactRating == "High" ? ("High impact", "StatusWarningBrush")
                                        : en.ImpactRating == "Medium" ? ("Medium impact", "AccentBrush")
                                        : en.ImpactRating == "Low" ? ("Low impact", "StatusSuccessBrush")
                                        : ("Impact unknown", "TextSecondaryBrush");
            TxtStDetPill.Text = pill;
            TxtStDetPill.SetResourceReference(TextBlock.ForegroundProperty, brush);
            StDetPillBg.SetResourceReference(StatusMark.FillProperty, brush);

            TxtStDetAbout.Text = en.IsBroken
                ? "The program this entry starts no longer exists — it was probably uninstalled or moved. The entry does nothing and can be removed."
                : en.ImpactRating == "High"
                    ? "A large program that loads in the background at every start. It still opens normally when you click it — turning it off here only stops the automatic start."
                    : en.IsSystemEntry
                        ? "Part of Windows or of a driver. Usually best left on."
                        : "Starts automatically when you sign in. Turning it off here does not uninstall it.";

            TxtStDetCommand.Text = en.Command;
            TxtStDetPublisher.Text = string.IsNullOrWhiteSpace(en.Publisher) ? "—" : en.Publisher;
            TxtStDetPublisher.ToolTip = en.Publisher;
            if (en.IsBroken)
            {
                TxtStDetFile.Text = "missing";
                TxtStDetFile.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorBrush");
            }
            else if (en.TargetSizeBytes > 0)
            {
                TxtStDetFile.Text = "found · " + FormatBytesShort(en.TargetSizeBytes);
                TxtStDetFile.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessBrush");
            }
            else
            {
                TxtStDetFile.Text = "—";
                TxtStDetFile.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }
            TxtStDetFile.ToolTip = en.TargetPath;
            TxtStDetWhere.Text = en.WhereLabel;
            TxtStDetWhere.ToolTip = string.IsNullOrWhiteSpace(en.RegistryKey) ? en.Location : en.RegistryKey;
            TxtStDetStatus.Text = en.IsEnabled ? "On at start" : "Off";
            TxtStDetStatus.SetResourceReference(TextBlock.ForegroundProperty, en.IsEnabled ? "StatusSuccessBrush" : "TextSecondaryBrush");

            BtnDisableStartup.Visibility = en.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            BtnEnableStartup.Visibility  = en.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            BtnStartupOpenFolder.IsEnabled = en.TargetPath.Length > 0 && File.Exists(en.TargetPath);
            TxtStDetBackup.Text = "kept in a backup folder";
            TxtStDetApply.Text  = "at the next sign-in";
        }

        private static string FormatBytesShort(long b) =>
            b >= 1L << 30 ? $"{b / 1073741824.0:F1} GB" : b >= 1L << 20 ? $"{b / 1048576.0:F0} MB" : $"{Math.Max(1, b / 1024)} KB";

        private void StartupOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedStartup is not StartupEntry en || en.TargetPath.Length == 0 || !File.Exists(en.TargetPath)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{en.TargetPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "StartupOpenFolder_Click"); }
        }

        private async void LoadStartup_Click(object s, RoutedEventArgs e) => await LoadStartupAsync();

        private async void StartupShowAllServices_Toggle(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool showAll = (s as FrameworkElement)?.Tag as string == "ShowServices";
            if (ChkStartupShowAllServices?.IsChecked == showAll) return; // already in this state
            ChkStartupShowAllServices!.IsChecked = showAll;
            SyncStartupSegmentedVisual(showAll);
            SettingsService.Current.StartupShowAllServices = showAll;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            await LoadStartupAsync();
        }

        private void SyncStartupSegmentedVisual(bool showAll)
        {
            if (SegStartupAppsOnly != null && SegStartupShowServices != null &&
                TxtStartupAppsOnlyLabel != null && TxtStartupShowServicesLabel != null)
                SetSegmentedActive(SegStartupAppsOnly, SegStartupShowServices,
                    TxtStartupAppsOnlyLabel, TxtStartupShowServicesLabel, !showAll);
        }

        private void StartupGrid_SelectionChanged(object s, SelectionChangedEventArgs e) => ShowStartupDetail(SelectedStartup);

        private void StartupGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedStartup is not StartupEntry entry) return;
            bool enable = !entry.IsEnabled;
            // an accidental double click no longer changes the startup without asking
            if (!AppDialog.Confirm(string.Format(LanguageService.S("Msg", enable ? "StartupEnableConfirmFmt" : "StartupDisableConfirmFmt"), entry.Name), "Startup")) return;
            ToolActivityLog.Add("Startup", $"{(enable ? "Enable" : "Disable")} {entry.Name}", "info");
            bool ok = _startupSvc.SetEnabled(entry, enable);
            if (!ok)
                AppDialog.Show(_LS("Services", "ModifyFailed"), "SMD Win");
            else
            {
                entry.IsEnabled = enable;
                _ = LoadStartupAsync();
            }
        }

        private async void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox cb && cb.Tag is StartupEntry entry)
            {
                bool enable = cb.IsChecked == true;
                bool ok = _startupSvc.SetEnabled(entry, enable);
                if (!ok)
                {
                    AppDialog.Show(_LS("Services", "ModifyFailed"), "SMD Win");
                    cb.IsChecked = !enable; // revert
                    return;
                }
                ToolActivityLog.Add("Startup", $"{(enable ? "Enable" : "Disable")} {entry.Name}", "info");
                entry.IsEnabled = enable;
                await LoadStartupAsync();
                // read again: a program that puts its entry back by itself (Edge with "Startup boost") is explained, not just
                // shown switched on again
                var again = _allStartupEntries?.FirstOrDefault(x => x.Name == entry.Name && x.Command == entry.Command);
                if (again != null && again.IsEnabled != enable)
                {
                    string msg = PT("{0} turned its start with Windows back on by itself.", entry.Name);
                    if (entry.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase) || entry.Command.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                        msg += "\n\n" + PT("In Edge: Settings → System and performance → turn off \"Startup boost\" and \"Continue running background extensions\".");
                    else
                        msg += "\n\n" + PT("Look in the program's own settings for an option like \"Start with Windows\".");
                    AppDialog.Show(msg, PT("Startup"));
                }
            }
        }

        private List<StartupEntry>? _allStartupEntries;

        private StartupEntry? SelectedStartup => StartupGrid.SelectedItem as StartupEntry;

        private async void EnableStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_LS("Startup", "SelectEntry")); return; }
            _startupSvc.SetEnabled(SelectedStartup, true);
            await LoadStartupAsync();
        }

        private async void DisableStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_LS("Startup", "SelectEntry")); return; }
            _startupSvc.SetEnabled(SelectedStartup, false);
            await LoadStartupAsync();
        }

        private async void RemoveStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_LS("Startup", "SelectEntry")); return; }
            if (!AppDialog.Confirm(
                string.Format(_LS("Startup", "ConfirmRemoveFmt"), SelectedStartup.Name),
                _LS("Common", "Confirm"))) return;
            ToolActivityLog.Add("Startup", $"Remove {SelectedStartup.Name}", "info");
            _startupSvc.Remove(SelectedStartup);
            await LoadStartupAsync();
        }

        private void OpenTaskManager_Click(object s, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "OpenTaskManager_Click"); }
        }

        private void OpenMsconfig_Click(object s, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msconfig.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "OpenMsconfig_Click"); }
        }

        private List<StartupDelayService.Delay> _startupDelays = new();

        /// <summary>Puts the measured delay on each entry (by its file name) and marks the entries whose file is new.</summary>
        private static void MarkStartupDelays(IEnumerable<StartupEntry> entries, List<StartupDelayService.Delay> delays)
        {
            var byFile = delays.ToDictionary(d => d.File, d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var en in entries)
            {
                try
                {
                    string exe = en.ExeName ?? "";
                    if (exe.Length > 0 && byFile.TryGetValue(System.IO.Path.GetFileName(exe), out var d)) en.MeasuredDelayMs = d.DelayMs;
                    string path = en.TargetPath ?? "";
                    en.AddedRecently = path.Length > 0 && System.IO.File.Exists(path)
                                       && (DateTime.Now - System.IO.File.GetCreationTime(path)).TotalDays <= 14;
                }
                catch (Exception ex) { AppLogger.Debug($"MarkStartupDelays {en.Name}: {ex.Message}"); }
            }
        }
    }
}
