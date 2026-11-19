using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidBrush = System.Windows.Media.SolidColorBrush;

namespace SMDWin.Views
{
    /// <summary>
    /// The Settings page with tabs (General, Look, Alerts, Widget &amp; tray, Reports, Advanced): tab switching, search,
    /// and the newer settings — the X button, the accent, the temperature thresholds, the read rate, the workshop details,
    /// the reports folder, export / import / reset.
    /// </summary>
    public partial class MainWindow
    {
        // true while the interface is being filled from the settings (Checked events fired by code are ignored)
        private bool _settingsUiLoading;

        // the "Auto" threshold for the processor, computed at start from TjMax (see InitTempReaderThenStartTimer)
        private float _autoCpuTempLimit = 90f;
        private const float AutoGpuTempLimit = 90f;

        // ── Taburi ───────────────────────────────────────────────────────────────

        private static readonly string[] SettingsTabTags = { "General", "Appearance", "Alerts", "Widget", "Sensors", "Data", "Advanced", "About" };

        internal void SettingsTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag) ShowSettingsTab(tag);
        }

        private void ShowSettingsTab(string tag)
        {
            foreach (var t in SettingsTabTags)
                if (FindName("SetPanel" + t) is FrameworkElement p)
                    p.Visibility = t == tag ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in SettingsTabTags)
                if (FindName("BtnSetTab" + t) is Wpf.Ui.Controls.Button b)
                    SetTabButtonActive(b, t == tag);
            if (FindName("PanelSettings") is ScrollViewer sv) sv.ScrollToTop();
            DecorateSettingsRows();
            if (TxtSettingsSearchHint != null) TxtSettingsSearchHint.Text = PT("Search settings…");
            if (tag == "About") BuildSettingsAbout();
            ScheduleFitSettingsPage();
        }

        // ── Cautare ──────────────────────────────────────────────────────────────

        internal void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = (TxtSettingsSearch?.Text ?? "").Trim();
            if (TxtSettingsSearchHint != null) TxtSettingsSearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            var rowStyle = TryFindResource("SetRow") as Style;
            var cardStyle = TryFindResource("SetCard") as Style;
            FrameworkElement? first = null;
            string? firstTab = null;

            foreach (var t in SettingsTabTags)
            {
                if (FindName("SetPanel" + t) is not FrameworkElement panel) continue;
                foreach (var el in LogicalDescendants(panel).OfType<Border>())
                {
                    bool isRow = rowStyle != null && el.Style == rowStyle;
                    bool isCard = cardStyle != null && el.Style == cardStyle;
                    if (!isRow && !isCard) continue;
                    if (q.Length < 2) { if (isRow) el.Opacity = 1; continue; }

                    // a card matches only by its title; a row by all the texts in it
                    bool match = isCard ? CardTitleMatches(el, q) : TextsOf(el).Any(s => s.Contains(q, StringComparison.CurrentCultureIgnoreCase));
                    if (isRow) el.Opacity = match ? 1 : 0.35;
                    if (match && first == null) { first = el; firstTab = t; }
                }
            }
            if (q.Length < 2 || first == null || firstTab == null) return;
            ShowSettingsTab(firstTab);
            var target = first;
            Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>True when an option on the Settings page mentions the text (for the sidebar search).</summary>
        private bool SettingsHasMatch(string q)
        {
            var rowStyle = TryFindResource("SetRow") as Style;
            if (rowStyle == null) return false;
            foreach (var t in SettingsTabTags)
            {
                if (FindName("SetPanel" + t) is not FrameworkElement panel) continue;
                if (LogicalDescendants(panel).OfType<Border>().Any(b => b.Style == rowStyle
                        && TextsOf(b).Any(x => x.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
                    return true;
            }
            return false;
        }

        /// <summary>The sidebar text drives the (hidden) Settings search while Settings is open, and is cleared with it.</summary>
        private void ForwardSidebarQueryToSettings(string query)
        {
            if (TxtSettingsSearch == null) return;
            bool onSettings = FindName("PanelSettings") is UIElement ps && ps.IsVisible;
            string want = onSettings ? query : "";
            if (TxtSettingsSearch.Text != want) TxtSettingsSearch.Text = want;
        }

        private static bool CardTitleMatches(Border card, string q)
        {
            var titleStyle = Application.Current?.TryFindResource("SetCardTitle") as Style;
            return LogicalDescendants(card).OfType<TextBlock>()
                .Any(tb => tb.Style == titleStyle && (tb.Text ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }

        private static IEnumerable<string> TextsOf(DependencyObject root)
        {
            foreach (var d in LogicalDescendants(root))
            {
                if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
                else if (d is ContentControl cc && cc.Content is string s) yield return s;
                if (d is FrameworkElement fe && fe.ToolTip is string tip) yield return tip;
            }
        }

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var d in LogicalDescendants(child)) yield return d;
            }
        }

        // ── Loading the state into the interface ───────────────────────────────────────

        /// <summary>Called at the end of LoadSettingsIntoUI: the state of the newer controls on the Settings page.</summary>
        private void LoadSettingsTabsIntoUI()
        {
            var s = SettingsService.Current;
            _settingsUiLoading = true;
            try
            {
                if (RbCloseTray != null) RbCloseTray.IsChecked = s.CloseToTray;
                if (RbCloseQuit != null) RbCloseQuit.IsChecked = !s.CloseToTray;

                PaintAccentSwatches(s.AccentName ?? "Blue");

                ApplyTempLimits();
                SelectLimitChip("Cpu", s.CpuTempLimit);
                SelectLimitChip("Gpu", s.GpuTempLimit);
                UpdateTempLimitDescriptions();

                if (FindName("RbSpeed" + s.UpdateSpeed) is RadioButton sp) sp.IsChecked = true;
                if (ChkSlowerOnBattery != null) ChkSlowerOnBattery.IsChecked = s.SlowerOnBattery;

                if (ChkWorkshopMode != null) ChkWorkshopMode.IsChecked = s.WorkshopMode;
                if (PnlWorkshop != null) PnlWorkshop.Visibility = s.WorkshopMode ? Visibility.Visible : Visibility.Collapsed;
                if (TxtWorkshopPhone != null) TxtWorkshopPhone.Text = s.WorkshopPhone ?? "";
                if (TxtWorkshopContact != null) TxtWorkshopContact.Text = s.WorkshopContact ?? "";
                UpdateReportFolderLabel();

                if (TxtDataFolderPath != null) TxtDataFolderPath.ToolTip = AppPaths.DataDirectory;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadSettingsTabsIntoUI"); }
            finally { _settingsUiLoading = false; }
            ApplyUpdateSpeed();
        }

        // ── The X button ────────────────────────────────────────────────────────────

        internal void CloseAction_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || sender is not RadioButton rb) return;
            bool tray = rb.Tag?.ToString() == "Tray";
            SettingsService.Current.CloseToTray = tray;
            // ChkMinimize (hidden) is read by Settings_Changed; keep it in sync
            if (ChkMinimize != null && ChkMinimize.IsChecked != tray) ChkMinimize.IsChecked = tray;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "CloseAction_Checked"); }
        }

        // ── Accent ───────────────────────────────────────────────────────────────

        private static readonly string[] AccentTags = { "MatchWindows", "Blue", "Purple", "Teal", "Green", "Orange", "Red" };

        private static string AccentControlName(string tag) => tag == "MatchWindows" ? "RbAccentWindows" : "RbAccent" + tag;

        /// <summary>Colours the dots (the colours come from ThemeManager) and checks the active one.</summary>
        private void PaintAccentSwatches(string active)
        {
            bool was = _settingsUiLoading;
            _settingsUiLoading = true;
            try
            {
                foreach (var tag in AccentTags)
                {
                    if (FindName(AccentControlName(tag)) is not RadioButton rb) continue;
                    string hex = tag == "MatchWindows" ? ThemeManager.GetWindowsAccentColorHex() : ThemeManager.GetPresetAccentHex(tag);
                    try { rb.Background = new WpfSolidBrush(ThemeManager.ParseHex(hex)); }
                    catch (Exception ex) { AppLogger.Swallowed(ex); }
                    rb.IsChecked = tag == active;
                }
            }
            finally { _settingsUiLoading = was; }
        }

        internal void AccentSwatch_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || sender is not RadioButton rb || rb.Tag is not string tag) return;
            if (SettingsService.Current.AccentName == tag) return;
            SetAccentPreset(tag);
        }

        // ── Temperature thresholds ───────────────────────────────────────────────

        private void SelectLimitChip(string which, int value)
        {
            string suffix = value is 85 or 90 or 95 or 100 ? value.ToString() : "Auto";
            if (FindName($"Rb{which}Lim{suffix}") is RadioButton rb) rb.IsChecked = true;
        }

        internal void CpuTempLimit_Checked(object sender, RoutedEventArgs e) => TempLimitChanged(sender, cpu: true);
        internal void GpuTempLimit_Checked(object sender, RoutedEventArgs e) => TempLimitChanged(sender, cpu: false);

        private void TempLimitChanged(object sender, bool cpu)
        {
            if (_settingsUiLoading || sender is not RadioButton rb) return;
            int v = int.TryParse(rb.Tag?.ToString(), out int n) ? n : 0;
            if (cpu) SettingsService.Current.CpuTempLimit = v; else SettingsService.Current.GpuTempLimit = v;
            ApplyTempLimits();
            UpdateTempLimitDescriptions();
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "TempLimitChanged"); }
        }

        /// <summary>The effective thresholds (the Settings choice or Auto) in all the fields read by alerts and cards.</summary>
        private void ApplyTempLimits()
        {
            var s = SettingsService.Current;
            float cpu = s.CpuTempLimit > 0 ? s.CpuTempLimit : _autoCpuTempLimit;
            float gpu = s.GpuTempLimit > 0 ? s.GpuTempLimit : AutoGpuTempLimit;
            s.TempWarnCpu = cpu;
            s.CpuTempAlertThreshold = cpu;
            s.TempWarnGpu = gpu;
            s.GpuTempAlertThreshold = gpu;
        }

        private void UpdateTempLimitDescriptions()
        {
            var s = SettingsService.Current;
            if (TxtCpuLimitDesc != null)
                TxtCpuLimitDesc.Text = s.CpuTempLimit > 0
                    ? string.Format(LanguageService.S("Settings", "LimitManual", "Notify above {0}"), TempUnit.Fmt(s.CpuTempLimit, space: true))
                    : string.Format(LanguageService.S("Settings", "LimitAutoCpu", "Auto · {0} for this processor"), TempUnit.Fmt(_autoCpuTempLimit, space: true));
            if (TxtGpuLimitDesc != null)
                TxtGpuLimitDesc.Text = s.GpuTempLimit > 0
                    ? string.Format(LanguageService.S("Settings", "LimitManual", "Notify above {0}"), TempUnit.Fmt(s.GpuTempLimit, space: true))
                    : string.Format(LanguageService.S("Settings", "LimitAutoGpu", "Auto · {0}"), TempUnit.Fmt(AutoGpuTempLimit, space: true));
        }

        // ── Test notification ───────────────────────────────────────────────────

        internal void TestNotification_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string title = LanguageService.S("Settings", "TestNotifTitle", "SMD Win notifications work");
                string msg = LanguageService.S("Settings", "TestNotifMsg", "This is how an alert will look.");
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.ShowBalloonTip(5000, title, msg, Forms.ToolTipIcon.Info);
                }
                else ShowToastInfo(title);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TestNotification_Click"); }
        }

        // ── Read rate ─────────────────────────────────────────────────────

        internal void UpdateSpeed_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || sender is not RadioButton rb || rb.Tag is not string tag) return;
            SettingsService.Current.UpdateSpeed = tag;
            ApplyUpdateSpeed();
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "UpdateSpeed_Checked"); }
        }

        internal void SlowerOnBattery_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Current.SlowerOnBattery = ChkSlowerOnBattery?.IsChecked == true;
            ApplyUpdateSpeed();
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "SlowerOnBattery_Click"); }
        }

        private static bool IsOnBattery()
        {
            try { return Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline; }
            catch (Exception ex) { AppLogger.Swallowed(ex); return false; }
        }

        /// <summary>
        /// A single level for all rates: sensors, network and power.
        /// Saver = 3 s / 5 s / 30 s · Normal = 1 s / 2 s / 10 s · Fast = 0.5 s / 1 s / 5 s.
        /// With "Slower on battery", Saver is used on battery.
        /// </summary>
        private void ApplyUpdateSpeed()
        {
            try
            {
                var s = SettingsService.Current;
                string level = s.SlowerOnBattery && IsOnBattery() ? "Saver" : s.UpdateSpeed;
                (double sensors, int net, int power) = level switch
                {
                    "Saver" => (3.0, 5, 30),
                    "Fast"  => (0.5, 1, 5),
                    _       => (1.0, 2, 10),
                };
                bool changed = s.RefreshInterval != sensors || s.NetworkRefreshSec != net || s.BatteryRefreshSec != power;
                s.RefreshInterval = sensors;
                s.NetworkRefreshSec = net;
                s.BatteryRefreshSec = power;
                if (!changed) return;

                _tempTimer.Interval = TimeSpan.FromSeconds(sensors);
                if (_trafficTimer.IsEnabled) _trafficTimer.Interval = TimeSpan.FromSeconds(net);
                if (_netPageTimer != null) _netPageTimer.Interval = TimeSpan.FromSeconds(NetRefreshSeconds());
                ApplyNetRefreshToChart();
                if (_batTimer.IsEnabled) _batTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(power, 5, 15));
                AppLogger.Info("Update speed: {0} (sensors {1}s, network {2}s, power {3}s)", level, sensors, net, power);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ApplyUpdateSpeed"); }
        }

        // ── The page columns ────────────────────────────────────────────────────

        /// <summary>Settings shows one category at a time, full width (MaxWidth in XAML): nothing to rearrange any more.
        /// Kept as the hook the page's size / visibility events call.</summary>
        private void LayoutSettingsColumns() { }

        // ── The tabs (buttons, as in Storage) and the choices in the "Look and charts" card ──

        internal void SettingsTabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag) ShowSettingsTab(tag);
        }

        internal void ThemeSeg_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || sender is not RadioButton rb || rb.Tag is not string t) return;
            var s = SettingsService.Current;
            if (t == "Auto") { if (!s.AutoTheme) BtnAutoTheme_Click(sender, e); return; }
            if (!s.AutoTheme && s.ThemeName == t) return;
            if (FindName(t == "Dark" ? "BtnThemeDark" : "BtnThemeLight") is System.Windows.Controls.Button b) SetTheme_Click(b, e);
        }

        internal void DensitySeg_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading) return;
            DensityPreview_Click(sender, e);
        }

        /// <summary>Checks the chosen button of a choice group without firing the change again.</summary>
        private void CheckSilently(string name)
        {
            if (FindName(name) is not RadioButton rb || rb.IsChecked == true) return;
            bool was = _settingsUiLoading;
            _settingsUiLoading = true;
            try { rb.IsChecked = true; }
            finally { _settingsUiLoading = was; }
        }

        /// <summary>The large previews (theme, text size): the chosen one has an accent outline, not the whole button coloured.</summary>
        internal void SetTileActive(Wpf.Ui.Controls.Button? b, bool active)
        {
            if (b == null) return;
            b.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            b.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, active ? "AccentBrush" : "CardBorderBrush");
            b.BorderThickness = new Thickness(active ? 2 : 1);
            foreach (var label in FindVisualChildren<TextBlock>(b))
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                label.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }

        // ── Atelier (Advanced › Computer service) ───────────────────────────────

        internal void WorkshopMode_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkWorkshopMode?.IsChecked == true;
            SettingsService.Current.WorkshopMode = on;
            if (PnlWorkshop != null) PnlWorkshop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "WorkshopMode_Click"); }
        }

        // ── The workshop data ───────────────────────────────────────────────────

        internal void WorkshopField_LostFocus(object sender, RoutedEventArgs e) => SaveWorkshopContact();

        internal void WorkshopField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { SaveWorkshopContact(); e.Handled = true; }
        }

        private void SaveWorkshopContact()
        {
            try
            {
                var s = SettingsService.Current;
                string phone = TxtWorkshopPhone?.Text.Trim() ?? "";
                string contact = TxtWorkshopContact?.Text.Trim() ?? "";
                if (phone == (s.WorkshopPhone ?? "") && contact == (s.WorkshopContact ?? "")) return;
                s.WorkshopPhone = phone;
                s.WorkshopContact = contact;
                SettingsService.Save();
                if (TxtWorkshopSaved != null)
                    TxtWorkshopSaved.Text = ReportBranding.Workshop.Length > 0
                        ? string.Format(LanguageService.S("Settings", "WorkshopSavedNamed", "Saved — reports will show „{0}”"), ReportBranding.NameAndPhone)
                        : LanguageService.S("Settings", "WorkshopNeedsName", "Saved — add a name to show these on reports");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SaveWorkshopContact"); }
        }

        // ── The reports folder ─────────────────────────────────────────────────

        private void UpdateReportFolderLabel()
        {
            bool custom = !string.IsNullOrWhiteSpace(SettingsService.Current.ReportPath);
            string folder = SettingsService.GetEffectiveReportFolder();
            if (TxtReportFolderPath != null)
            {
                TxtReportFolderPath.Text = custom ? folder : LanguageService.S("Settings", "ReportFolderDefault", "Data › Reports, next to SMD Win");
                TxtReportFolderPath.ToolTip = folder;
            }
            if (BtnResetReportFolder != null) BtnResetReportFolder.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void OpenReportFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsService.GetEffectiveReportFolder());

        internal void ChangeReportFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new Forms.FolderBrowserDialog
                {
                    Description = LanguageService.S("Settings", "SelectReportsFolder", "Select reports folder"),
                    UseDescriptionForTitle = true,
                    SelectedPath = SettingsService.GetEffectiveReportFolder(),
                };
                if (dlg.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;
                SettingsService.Current.ReportPath = dlg.SelectedPath;
                SettingsService.Save();
                UpdateReportFolderLabel();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ChangeReportFolder_Click"); }
        }

        internal void ResetReportFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Current.ReportPath = "";
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "ResetReportFolder_Click"); }
            UpdateReportFolderLabel();
        }

        internal void OpenSensorLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppLogger.LogDirectory);

        private static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "OpenFolder " + dir); }
        }

        // ── Problems: the diagnostic package ─────────────────────────────────────

        internal void ReportProblem_Click(object sender, RoutedEventArgs e) => BtnExportDiag_Click(sender, e);

        // ── Export / import / resetare ───────────────────────────────────────────

        internal void SettingsExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SettingsService.Save();
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"SMDWin-settings-{DateTime.Now:yyyy-MM-dd}.json",
                    Filter = "JSON (*.json)|*.json",
                    InitialDirectory = SettingsService.GetEffectiveReportFolder(),
                };
                if (dlg.ShowDialog(this) != true) return;
                File.Copy(SettingsService.SettingsFilePath, dlg.FileName, overwrite: true);
                ShowToastSuccess(LanguageService.S("Settings", "ExportDone", "Settings saved to a file"));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SettingsExport_Click");
                AppDialog.Show(ex.Message, "SMD Win", AppDialog.Kind.Warning);
            }
        }

        internal void SettingsImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
                if (dlg.ShowDialog(this) != true) return;
                string json = File.ReadAllText(dlg.FileName);
                // the file must really be an SMD Win settings file
                if (System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) == null || !json.Contains("\"SettingsVersion\""))
                {
                    AppDialog.Show(LanguageService.S("Settings", "ImportInvalid", "This file doesn't contain SMD Win settings."), "SMD Win", AppDialog.Kind.Warning);
                    return;
                }
                if (!AppDialog.Confirm(LanguageService.S("Settings", "ImportConfirm", "Replace the current settings and restart SMD Win?"), "SMD Win")) return;
                File.Copy(dlg.FileName, SettingsService.SettingsFilePath, overwrite: true);
                SettingsService.Load();      // the settings in memory = the imported ones (a save on close no longer overwrites them)
                RestartApp();
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SettingsImport_Click");
                AppDialog.Show(ex.Message, "SMD Win", AppDialog.Kind.Warning);
            }
        }

        internal void SettingsReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!AppDialog.Confirm(LanguageService.S("Settings", "ResetConfirm", "Reset all settings to the defaults and restart SMD Win?"), "SMD Win")) return;
                SettingsService.ResetToDefaults();
                RestartApp();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SettingsReset_Click"); }
        }

        private void RestartApp()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
                _isClosingToTray = true;     // iesire completa, nu in tray
                Application.Current.Shutdown();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "RestartApp"); }
        }
    }
}
