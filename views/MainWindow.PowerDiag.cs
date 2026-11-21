using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page — real information, not just decoration:
    ///   • the power diagram (desktop) gets live values: CPU/GPU watts, board voltages, CMOS battery, fans
    ///     (PowerFlowAnimation.SetLive, every 2 s, only while visible);
    ///   • the "Sleep &amp; wake" card: available sleep states, what keeps the PC awake now, the last wake,
    ///     wake timers, devices that can wake the PC (with a "Block" button);
    ///   • the stability card: unexpected shutdowns in the last 30 days.
    /// Rule: what cannot be read does NOT appear (no empty rows, no "—").
    /// The "Sleep &amp; wake" card is always on the page: first with "reading…", then with the data or with the reason
    /// they could not be read — it does not disappear without an explanation.
    /// </summary>
    public partial class MainWindow
    {
        private System.Windows.Threading.DispatcherTimer? _powerLiveTimer;
        private DateTime _powerDiagLastRun = DateTime.MinValue;
        private bool _powerDiagBusy;

        // ── Live values on the power diagram ─────────────────────────────────────
        private void EnsurePowerLiveTimer()
        {
            if (_powerLiveTimer != null) return;
            _powerLiveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _powerLiveTimer.Tick += (_, _) => PushPowerLive();
            _powerLiveTimer.Start();
            PushPowerLive();
        }

        private void PushPowerLive()
        {
            try
            {
                if (PowerFlowWidget == null || !PowerFlowWidget.IsVisible) return;
                var snap = _lastTempSnap;
                PowerFlowWidget.SetLive(snap?.CpuPowerW, snap?.GpuPowerW, snap?.Rail12V, snap?.Rail5V, snap?.Rail3V3,
                                        snap?.CmosBatteryV, snap?.FanSpeeds);
            }
            catch (Exception ex) { AppLogger.Debug($"PushPowerLive: {ex.Message}"); }
        }

        // ── Sleep & wake + stabilitate ─────────────────────────────────────────────
        internal void PowerDiagRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshPowerDiagAsync(force: true);

        private async Task RefreshPowerDiagAsync(bool force = false)
        {
            if (_powerDiagBusy) return;
            if (!force && (DateTime.Now - _powerDiagLastRun).TotalSeconds < 20) return;
            _powerDiagBusy = true;
            bool empty = FindName("PowerSleepHost") is StackPanel h && h.Children.Count == 0;
            if (empty) ShowPowerSleepNote(LanguageService.S("Power", "SleepReading", "Reading what keeps the PC awake…"), "TextSecondaryBrush");
            if (FindName("BtnPowerDiagRefresh") is System.Windows.Controls.Button refresh) refresh.IsEnabled = false;
            try
            {
                var r = await Task.Run(() =>
                {
                    PowerAdvancedService.SleepStatesInfo? states = null;
                    bool? fastStartup = null;
                    List<PowerDiagnosticsService.SleepBlocker>? blockers = null;
                    PowerDiagnosticsService.WakeInfo? lastWake = null;
                    List<PowerDiagnosticsService.WakeTimer>? timers = null;
                    List<string>? devices = null, programmable = null;
                    List<PowerDiagnosticsService.RequestOverride>? overrides = null;
                    int? wakeTimers = null;
                    // The reads are independent (three start a powercfg process each, one reads the System log): run in parallel, so the
                    // wait is as long as the slowest of them, not their sum.
                    Parallel.Invoke(
                        () => { try { states = PowerAdvancedService.GetSleepStates(); } catch (Exception ex) { AppLogger.Debug($"PowerDiag states: {ex.Message}"); } },
                        () => { try { fastStartup = PowerAdvancedService.GetFastStartup(); } catch (Exception ex) { AppLogger.Debug($"PowerDiag fast startup: {ex.Message}"); } },
                        () => blockers = PowerDiagnosticsService.GetSleepBlockers(),
                        () => lastWake = PowerDiagnosticsService.GetLastWake(),
                        () => timers = PowerDiagnosticsService.GetWakeTimers(),
                        () => devices = PowerDiagnosticsService.GetWakeArmedDevices(),
                        () => programmable = PowerDiagnosticsService.GetWakeProgrammableDevices(),
                        () => overrides = PowerDiagnosticsService.GetRequestOverrides(),
                        () => { try { wakeTimers = PowerAdvancedService.GetWakeTimersAllowed(true); } catch (Exception ex) { AppLogger.Debug($"PowerDiag wake timers: {ex.Message}"); } });
                    return (states, fastStartup, blockers, lastWake, timers, devices, programmable, overrides, wakeTimers);
                });
                _powerDiagLastRun = DateTime.Now;
                (_pdProgrammable, _pdOverrides, _pdWakeTimers) = (r.programmable, r.overrides, r.wakeTimers);
                BuildPowerSleepCard(r.states, r.fastStartup, r.blockers, r.lastWake, r.timers, r.devices);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "RefreshPowerDiagAsync");
                if (FindName("PowerSleepHost") is StackPanel host && host.Children.Count == 0)
                    ShowPowerSleepNote(LanguageService.S("Power", "SleepReadFailed",
                        "Could not read the sleep and wake information. Try Refresh."), "StatusWarnBrush");
            }
            finally
            {
                _powerDiagBusy = false;
                if (FindName("BtnPowerDiagRefresh") is System.Windows.Controls.Button again) again.IsEnabled = true;
            }
        }

        /// <summary>The line under the Sleep &amp; wake card title: "reading…", the summary or the reason for the failure.</summary>
        private void ShowPowerSleepNote(string text, string brushKey)
        {
            if (FindName("TxtPowerSleepSummary") is not TextBlock summary) return;
            summary.Text = text;
            summary.Visibility = Visibility.Visible;
            summary.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        private TextBlock DiagText(string text, string brushKey = "TextPrimaryBrush", bool semibold = false, double? size = null)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = size ?? Ui.FontS, TextWrapping = TextWrapping.Wrap,
                FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        private Border DiagChip(string text, string brushKey)
        {
            var fg = (TryFindResource(brushKey) as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Gray;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 4),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, fg.R, fg.G, fg.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fg) },
            };
            return chip;
        }


        private static string Ago(DateTime when)
        {
            var d = DateTime.Now - when;
            if (d.TotalMinutes < 1) return PS("AgoNow", "just now");
            if (d.TotalHours < 1) return string.Format(PS("AgoMin", "{0} min ago"), (int)d.TotalMinutes);
            if (d.TotalDays < 1) return string.Format(PS("AgoHours", "{0} h ago"), (int)d.TotalHours);
            if (d.TotalDays < 2) return PS("AgoYesterday", "yesterday");   // not "1 days ago"
            return string.Format(PS("AgoDays", "{0} days ago"), (int)d.TotalDays);
        }

        // the texts of the Sleep & wake card (Languages/*.json › PowerSleep)
        private static string PS(string key, string en) => LanguageService.S("PowerSleep", key, en);
        private static string PSn(int n, string oneKey, string oneEn, string manyKey, string manyEn)
            => n == 1 ? string.Format(PS(oneKey, oneEn), n) : string.Format(PS(manyKey, manyEn), n);

        /// <summary>
        /// The header of a Sleep &amp; wake category: a clear title, a sentence saying what the category covers and, on the
        /// right, the count with its word ("3 active", "1 timer"); orange when the category shows a problem.
        /// </summary>
        private void SleepSection(StackPanel host, string title, string meaning, string count, bool warn)
        {
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            var badgeText = new TextBlock { Text = count, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold };
            var tone = (TryFindResource(warn ? "StatusWarnBrush" : "TextSecondaryBrush") as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Gray;
            badge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(32, tone.R, tone.G, tone.B));
            badgeText.Foreground = new SolidColorBrush(tone);
            badge.Child = badgeText;
            DockPanel.SetDock(badge, Dock.Right); top.Children.Add(badge);
            top.Children.Add(DiagText(title, "TextPrimaryBrush", true, Ui.FontL));
            host.Children.Add(top);

            var sub = DiagText(meaning, "TextSecondaryBrush", false, Ui.FontS);
            sub.Margin = new Thickness(0, 0, 0, 6);
            host.Children.Add(sub);
            var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 2) };
            rule.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            host.Children.Add(rule);
        }

        /// <summary>
        /// A row: dot · name (semibold) · detail (up to two lines, full in the tooltip) · kind label (e.g. "Blocks sleep")
        /// or action.
        /// </summary>
        private void SleepItem(StackPanel host, string dotKey, string name, string detail, UIElement? action, string? full = null,
                               string? tag = null, string tagBrushKey = "TextSecondaryBrush")
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5), Background = System.Windows.Media.Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
            g.Children.Add(dot);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var n = DiagText(name, "TextPrimaryBrush", true); n.TextWrapping = TextWrapping.NoWrap; n.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(n);
            if (detail.Length > 0)
            {
                var dt = DiagText(detail, "TextSecondaryBrush");
                dt.TextTrimming = TextTrimming.CharacterEllipsis; dt.MaxHeight = Ui.FontS * 2.9;
                dt.FontSize = Ui.FontS;
                sp.Children.Add(dt);
            }
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            if (action is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(fe, 2); g.Children.Add(fe); }
            else if (!string.IsNullOrEmpty(tag))
            {
                var chip = DiagChip(tag, tagBrushKey);
                chip.Margin = new Thickness(0); chip.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(chip, 2); g.Children.Add(chip);
            }
            g.ToolTip = string.Join("\n", new[] { full ?? name, detail }.Where(x => !string.IsNullOrWhiteSpace(x)));
            host.Children.Add(g);
        }

        /// <summary>„Cirrus Logic CS4206B (AB 07) (HDAUDIO\FUNC_01…)” → „Cirrus Logic CS4206B (AB 07)”: calea tehnica merge in tooltip.</summary>
        private static string CleanDeviceName(string name)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\s*\([^()]*\\[^()]*\)", "");
            return string.IsNullOrWhiteSpace(s) ? name ?? "" : s.Trim();
        }

        private void BuildPowerSleepCard(PowerAdvancedService.SleepStatesInfo? states, bool? fastStartup,
            List<PowerDiagnosticsService.SleepBlocker>? blockers, PowerDiagnosticsService.WakeInfo? lastWake,
            List<PowerDiagnosticsService.WakeTimer>? timers, List<string>? devices)
        {
            if (FindName("PowerSleepHost") is not StackPanel host || FindName("PowerSleepCard") is not Border card) return;
            // one strip of chips, each opening its list with the actions (MainWindow.PowerSimple.cs → BuildSleepStrip).
            // The older three-column layout below is kept, but no longer reached.
            (_pdStates, _pdFastStartup, _pdBlockers, _pdLastWake, _pdTimers, _pdArmed) =
                (states ?? _pdStates, fastStartup ?? _pdFastStartup, blockers, lastWake, timers, devices);
            if (!SettingsService.Current.DesignExperimentOldSleepCard)
            {
                BuildSleepStrip();
                if (host.Children.Count == 0)
                    ShowPowerSleepNote(LanguageService.S("Power", "SleepReadFailed", "Could not read the sleep and wake information. Try Refresh."), "StatusWarnBrush");
                card.Visibility = Visibility.Visible;
                FitPowerCards();
                return;
            }
            host.Children.Clear();

            // By sections: small title + rows (dot · name · one-line detail, full in the tooltip · action).
            // 1. What the PC can do (chips, no label)
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            if (states != null)
            {
                if (states.HasS3) chips.Children.Add(DiagChip(PS("ChipS3", "Sleep (S3)"), "StatusOkBrush"));
                if (states.HasModernStandby) chips.Children.Add(DiagChip(PS("ChipModernStandby", "Modern Standby"), "StatusOkBrush"));
                if (states.HasHibernate) chips.Children.Add(DiagChip(PS("ChipHibernate", "Hibernate"), "StatusOkBrush"));
            }
            if (fastStartup.HasValue)
                chips.Children.Add(DiagChip(fastStartup.Value ? PS("ChipFastOn", "Fast Startup on") : PS("ChipFastOff", "Fast Startup off"),
                                            fastStartup.Value ? "AccentBrush" : "TextSecondaryBrush"));
            if (chips.Children.Count > 0) host.Children.Add(chips);

            // Three categories side by side (stacked on a narrow card): each with title, explanation and count
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
            StackPanel NewSection(string title, string meaning, string count, bool warn)
            {
                var section = new StackPanel { Margin = new Thickness(0, 0, 22, 10) };
                SleepSection(section, title, meaning, count, warn);
                grid.Children.Add(section);
                return section;
            }

            // 2. What keeps the PC awake now
            if (blockers != null)
            {
                bool blocking = blockers.Any(b => b.Category is "SYSTEM" or "DISPLAY");
                var sec = NewSection(PS("AwakeTitle", "Keeping the PC awake"), PS("AwakeMeaning", "Programs and devices that stop it from going to sleep right now"),
                                     blockers.Count == 0 ? PS("CountNone", "none") : string.Format(PS("CountActive", "{0} active"), blockers.Count), blocking);
                if (blockers.Count == 0)
                    SleepItem(sec, "StatusOkBrush", PS("Nothing", "Nothing"), PS("NothingDetail", "The PC can sleep and turn off the screen normally"), null);
                foreach (var b in blockers.Take(5))
                {
                    string what = b.Category switch
                    {
                        "DISPLAY"   => PS("CatDisplay", "Keeps the screen on"),
                        "SYSTEM"    => PS("CatSystem", "Blocks sleep"),
                        "AWAYMODE"  => PS("CatAway", "Away mode"),
                        "EXECUTION" => PS("CatExec", "Keeps running"),
                        "PERFBOOST" => PS("CatPerf", "Performance boost"),
                        _           => b.Category,
                    };
                    bool bad = b.Category is "SYSTEM" or "DISPLAY";
                    SleepItem(sec, bad ? "StatusWarnBrush" : "TextSecondaryBrush", CleanDeviceName(b.Name),
                              b.Reason ?? "", null, b.Name, what, bad ? "StatusWarnBrush" : "TextSecondaryBrush");
                }
                if (blockers.Count > 5) sec.Children.Add(DiagText(string.Format(PS("More", "+{0} more"), blockers.Count - 5), "TextSecondaryBrush"));
            }

            // 3. Ultima trezire + cronometre
            if (lastWake != null || timers is { Count: > 0 })
            {
                var sec = NewSection(PS("WakeTitle", "Wake-ups"), PS("WakeMeaning", "When the PC last woke up and what is scheduled to wake it"),
                                     timers is { Count: > 0 } ? PSn(timers.Count, "TimersOne", "{0} timer", "TimersMany", "{0} timers") : PS("NoTimers", "no timers"),
                                     timers is { Count: > 0 });
                if (lastWake != null)
                    SleepItem(sec, "TextSecondaryBrush", string.Format(PS("LastWake", "Last wake-up {0}"), Ago(lastWake.When)), $"{lastWake.When:dd.MM.yyyy HH:mm} · {lastWake.Source}", null,
                              null, PS("LastWakeTag", "Last wake-up"));
                foreach (var t in (timers ?? new()).Take(4))
                    SleepItem(sec, "StatusWarnBrush", t.Owner, string.IsNullOrWhiteSpace(t.Reason) ? PS("TimerDefault", "Scheduled to wake the PC") : t.Reason, null,
                              null, PS("TimerTag", "Timer"), "StatusWarnBrush");
            }

            // 4. Devices that can wake the PC (+ block)
            if (devices != null)
            {
                var sec = NewSection(PS("CanWakeTitle", "Can wake the PC"), PS("CanWakeMeaning", "Devices allowed to wake it from sleep (keyboard, mouse…)"),
                                     devices.Count == 0 ? PS("CountNone", "none") : PSn(devices.Count, "DevOne", "{0} device", "DevMany", "{0} devices"), false);
                if (devices.Count == 0) SleepItem(sec, "StatusOkBrush", PS("NoDevice", "No device"), PS("NoDeviceDetail", "Only the power button wakes the PC"), null);
                foreach (var d in devices.Take(8))
                {
                    var btn = new Wpf.Ui.Controls.Button
                    {
                        Content = PS("Block", "Block"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                        Padding = new Thickness(10, 2, 10, 2), FontSize = Ui.FontS,
                        ToolTip = PS("BlockTip", "This device will no longer be able to wake the PC from sleep"),
                    };
                    string devName = d;
                    btn.Click += async (_, _) =>
                    {
                        if (!AppDialog.Confirm(string.Format(PS("BlockConfirm", "Stop \"{0}\" from waking the PC?\n\nIt can be allowed again from Device Manager → Power Management."), devName), "SMD Win"))
                            return;
                        bool ok = await Task.Run(() => PowerDiagnosticsService.DisableWake(devName));
                        if (!ok) AppDialog.Show(string.Format(PS("BlockFailed", "Could not change the wake setting for \"{0}\"."), devName), "SMD Win", AppDialog.Kind.Error);
                        await RefreshPowerDiagAsync(force: true);
                    };
                    SleepItem(sec, "AccentBrush", CleanDeviceName(d), "", btn, d);
                }
            }

            if (grid.Children.Count > 0)
            {
                int sections = grid.Children.Count;
                grid.SizeChanged += (_, e) =>
                {
                    int cols = e.NewSize.Width >= 980 ? sections : e.NewSize.Width >= 640 ? Math.Min(2, sections) : 1;
                    if (grid.Columns != cols) grid.Columns = Math.Max(1, cols);
                };
                host.Children.Add(grid);
            }

            // Summary in the header of the collapsed card — only what could be read
            if (FindName("TxtPowerSleepSummary") is TextBlock summary)
            {
                var parts = new List<string>();
                if (blockers != null)
                    parts.Add(blockers.Count == 0 ? PS("SumNothing", "Nothing keeps it awake")
                              : PSn(blockers.Count, "SumThingsOne", "{0} thing keeping it awake", "SumThingsMany", "{0} things keeping it awake"));
                if (lastWake != null) parts.Add(string.Format(PS("SumLastWake", "last wake-up {0}"), Ago(lastWake.When)));
                if (timers is { Count: > 0 }) parts.Add(PSn(timers.Count, "SumTimersOne", "{0} wake timer", "SumTimersMany", "{0} wake timers"));
                if (devices != null)
                    parts.Add(devices.Count == 0 ? PS("SumNoDevice", "no device can wake it")
                              : PSn(devices.Count, "SumDevOne", "{0} device can wake it", "SumDevMany", "{0} devices can wake it"));
                summary.Text = string.Join("  ·  ", parts);
                summary.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                bool warn = blockers != null && blockers.Any(b => b.Category is "SYSTEM" or "DISPLAY");
                summary.SetResourceReference(TextBlock.ForegroundProperty, warn ? "StatusWarnBrush" : "TextSecondaryBrush");
            }

            // Nothing read (powercfg did not answer, log inaccessible): the card stays, with the explanation
            if (host.Children.Count == 0)
                ShowPowerSleepNote(LanguageService.S("Power", "SleepReadFailed",
                    "Could not read the sleep and wake information. Try Refresh."), "StatusWarnBrush");
            card.Visibility = Visibility.Visible;
            FitPowerCards();
        }

    }
}
