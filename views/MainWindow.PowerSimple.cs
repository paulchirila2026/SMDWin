using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SMDWin.Services;
using SMDWin.Views.Net;
using Button      = System.Windows.Controls.Button;
using CheckBox    = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem    = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Brush       = System.Windows.Media.Brush;
using Ellipse     = System.Windows.Shapes.Ellipse;
using RadioButton = System.Windows.Controls.RadioButton;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page, made simple (mockup approved — one screen, everything changeable in place):
    ///  • header: "Keep awake" for a while (the plan stays as it is) and "Power check" (findings in a small window);
    ///  • "When the PC is not used": screen off / sleep / hard disks / (laptop) lid / power button, each value a click away,
    ///    plugged in and on battery side by side on a laptop;
    ///  • the plan card: the plans, the processor in three steps (Cool &amp; quiet · Normal · Full speed), the detailed
    ///    controls under "Advanced settings";
    ///  • "Sleep": one row of chips (what stops sleep · what wakes the PC · which devices can wake it · last wake-up); a chip
    ///    opens its list with the actions (Ignore, Close app, Turn off, allow / block);
    ///  • "Start-up and shut-down": Fast Startup (+ days since the last full restart), Hibernate, sleep type, reset plans.
    /// Rule kept from the old page: what cannot be read is not shown.
    /// </summary>
    public partial class MainWindow
    {
        private static string PSim(string key, string en) => LanguageService.S("PowerSimple", key, en);

        private bool _psInit;
        private bool _psLaptop;
        private int? _idleScreenAc, _idleScreenDc, _idleSleepAc, _idleSleepDc, _idleDiskAc, _idleDiskDc, _lidAc, _lidDc, _btnAc, _btnDc;
        private int? _psCpuMin, _psCpuMax;
        private PowerAdvancedService.BoostMode? _psCpuBoost;
        private bool _cpuModeSync;
        private bool _psBusy;

        // the last sleep / wake read (RefreshPowerDiagAsync) — the Sleep strip and Power check use it
        private List<PowerDiagnosticsService.SleepBlocker>? _pdBlockers;
        private List<PowerDiagnosticsService.WakeTimer>? _pdTimers;
        private List<string>? _pdArmed, _pdProgrammable;
        private List<PowerDiagnosticsService.RequestOverride>? _pdOverrides;
        private PowerDiagnosticsService.WakeInfo? _pdLastWake;
        private PowerAdvancedService.SleepStatesInfo? _pdStates;
        private bool? _pdFastStartup;
        private int? _pdWakeTimers;

        /// <summary>Once, when the Power page is first shown: the header controls, the processor steps, the titles.</summary>
        private void InitPowerSimple()
        {
            if (_psInit) return;
            _psInit = true;
            try
            {
                if (FindName("TxtPowerSubtitle") is TextBlock sub) sub.Text = PSim("Subtitle", "How the PC saves energy, and what stops it from sleeping");
                if (FindName("TxtPowerNowTitle") is TextBlock t1) t1.Text = PSim("IdleTitle", "When the PC is not used");
                if (FindName("TxtPowerIdleTitle") is TextBlock t2) t2.Text = PSim("IdleTitle", "When the PC is not used");
                if (FindName("TxtPowerSleepTitle") is TextBlock t3) t3.Text = PSim("SleepTitle", "Sleep");
                if (FindName("TxtPowerStartupTitle") is TextBlock t4) t4.Text = PSim("StartupTitle", "Start-up and shut-down");
                if (FindName("TxtCpuModeTitle") is TextBlock t5) t5.Text = PSim("CpuTitle", "Processor");
                if (FindName("TxtKeepAwakeTitle") is TextBlock t6) t6.Text = PSim("KeepAwake", "Keep awake");
                if (FindName("TxtBtnPowerCheck") is TextBlock t7) t7.Text = PSim("PowerCheck", "Power check");
                if (FindName("BtnPowerAdvanced") is System.Windows.Controls.Primitives.ToggleButton adv) adv.Content = PSim("Advanced", "Advanced settings");
                if (FindName("SleepTimeline") is FrameworkElement tl) tl.Visibility = Visibility.Collapsed;

                if (FindName("BtnPowerCheck") is Button check) check.Click += async (_, _) => await RunPowerCheckAsync();
                if (FindName("BtnKeepAwakeFor") is Button kf) { kf.Content = KeepLabel(_keepFor); kf.Click += (_, _) => ShowKeepAwakeMenu(kf); }
                if (FindName("ChkKeepAwake") is CheckBox kc) kc.Click += (_, _) => SetKeepAwake(kc.IsChecked == true);
                BuildCpuModeButtons();
                InitPowerRedesign();   // the new cards (MainWindow.PowerRedesign.cs)
            }
            catch (Exception ex) { AppLogger.Debug($"InitPowerSimple: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Reading the settings
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Reads the idle settings, the processor state and the start-up settings (background thread), then draws the
        /// cards. <paramref name="laptop"/> = the PC has a battery (null = keep the last answer).</summary>
        private async Task RefreshPowerSimpleAsync(bool? laptop = null)
        {
            InitPowerSimple();
            if (laptop.HasValue) _psLaptop = laptop.Value;
            if (_psBusy) return;
            _psBusy = true;
            try
            {
                var r = await Task.Run(() =>
                {
                    int? Try(Func<int?> f) { try { return f(); } catch (Exception ex) { AppLogger.Debug($"PowerSimple read: {ex.Message}"); return null; } }
                    var v = new
                    {
                        sa = Try(() => PowerAdvancedService.GetDisplayOffAfterSeconds(true)), sd = Try(() => PowerAdvancedService.GetDisplayOffAfterSeconds(false)),
                        la = Try(() => PowerAdvancedService.GetSleepAfterSeconds(true)),      ld = Try(() => PowerAdvancedService.GetSleepAfterSeconds(false)),
                        da = Try(() => PowerAdvancedService.GetDiskOffAfterSeconds(true)),    dd = Try(() => PowerAdvancedService.GetDiskOffAfterSeconds(false)),
                        ca = Try(() => PowerAdvancedService.GetLidAction(true)),              cd = Try(() => PowerAdvancedService.GetLidAction(false)),
                        ba = Try(() => PowerAdvancedService.GetPowerButtonAction(true)),      bd = Try(() => PowerAdvancedService.GetPowerButtonAction(false)),
                        min = Try(() => PowerAdvancedService.GetMinProcessorState(true)),     max = Try(() => PowerAdvancedService.GetMaxProcessorState(true)),
                        boost = Try(() => (int?)PowerAdvancedService.GetBoostMode(true)),
                        minD = Try(() => PowerAdvancedService.GetMinProcessorState(false)), maxD = Try(() => PowerAdvancedService.GetMaxProcessorState(false)),
                        boostD = Try(() => (int?)PowerAdvancedService.GetBoostMode(false)),
                        ha = Try(() => PowerAdvancedService.GetHibernateAfterSeconds(true)),  hd = Try(() => PowerAdvancedService.GetHibernateAfterSeconds(false)),
                        bs = Try(() => PowerAdvancedService.GetBatterySaverThreshold()),
                        dim = Try(() => PowerAdvancedService.GetBatterySaverDims() is bool d ? (d ? 1 : 0) : (int?)null),
                    };
                    long? hib = PowerAdvancedService.HiberfilBytes();
                    PowerAdvancedService.SleepStatesInfo? states = null;
                    bool? fast = null;
                    List<string> plans = new();
                    try { states = PowerAdvancedService.GetSleepStates(); } catch (Exception ex) { AppLogger.Debug($"PowerSimple states: {ex.Message}"); }
                    try { fast = PowerAdvancedService.GetFastStartup(); } catch (Exception ex) { AppLogger.Debug($"PowerSimple fast: {ex.Message}"); }
                    try { plans = PowerAdvancedService.ListPlanGuids(); } catch (Exception ex) { AppLogger.Debug($"PowerSimple plans: {ex.Message}"); }
                    return (v, hib, states, fast, plans);
                });
                var v = r.v;
                (_idleScreenAc, _idleScreenDc, _idleSleepAc, _idleSleepDc, _idleDiskAc, _idleDiskDc) = (v.sa, v.sd, v.la, v.ld, v.da, v.dd);
                (_lidAc, _lidDc, _btnAc, _btnDc) = (v.ca, v.cd, v.ba, v.bd);
                (_psCpuMin, _psCpuMax, _psCpuBoost) = (v.min, v.max, v.boost is int b ? (PowerAdvancedService.BoostMode?)b : null);
                (_psCpuMinDc, _psCpuMaxDc, _psCpuBoostDc) = (v.minD, v.maxD, v.boostD is int bd ? (PowerAdvancedService.BoostMode?)bd : null);
                (_idleHibAc, _idleHibDc, _bsThreshold) = (v.ha, v.hd, v.bs);
                _bsDims = v.dim is int dm ? dm == 1 : null;
                _pdStates = r.states ?? _pdStates;
                _pdFastStartup = r.fast;

                SyncCpuModeButtons();
                BuildStartupTiles(r.hib);
                BuildUltimateRow(r.plans);
                LayoutPowerCards();   // the layout changes when it turns out the PC is a laptop (or not)
                RenderPowerCards();
                FitPowerCards();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "RefreshPowerSimpleAsync"); }
            finally { _psBusy = false; }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  "When the PC is not used"
        // ════════════════════════════════════════════════════════════════════════════════════════


        private static string ActionText(int a) => a switch
        {
            0 => PSim("ActNothing", "Do nothing"), 1 => PSim("ActSleep", "Sleep"), 2 => PSim("ActHibernate", "Hibernate"),
            3 => PSim("ActShutDown", "Shut down"), 4 => PSim("ActScreenOff", "Turn off the screen"), _ => "?",
        };

        private string IdleText(int? sec) => sec is null ? "—" : PowerAdvancedService.FormatIdle(sec, PW("Never", "Never"));

        /// <summary>"When the PC is not used" — drawn by the new card (MainWindow.PowerRedesign.cs).</summary>
        private void BuildIdleSettings() => RenderIdleCard();

        private FrameworkElement IdleLabel(string icon, string title, string sub)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 5, 0, 5), VerticalAlignment = VAlign.Center };
            var vb = new Viewbox { Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VAlign.Center,
                                   Child = new Canvas { Width = 24, Height = 24, Children = { FluentIcons.Make(icon) } } };
            DockPanel.SetDock(vb, Dock.Left);
            dp.Children.Add(vb);
            var sp = new StackPanel { VerticalAlignment = VAlign.Center };
            sp.Children.Add(Txt2(title, "TextPrimaryBrush", Ui.FontM));
            if (sub.Length > 0)
            {
                // one line; the full text in the tooltip when the column is narrow
                var s = Txt2(sub, "TextSecondaryBrush", Ui.FontXS);
                s.TextWrapping = TextWrapping.NoWrap;
                s.TextTrimming = TextTrimming.CharacterEllipsis;
                s.ToolTip = sub;
                sp.Children.Add(s);
            }
            dp.Children.Add(sp);
            return dp;
        }

        private async Task SetIdleAsync(string what, int minutes, bool ac, string title)
        {
            try
            {
                await Task.Run(() => PowerAdvancedService.SetIdleMinutes(what, minutes, ac));
                ToolActivityLog.Add("Power", $"{title} ({(ac ? "plugged in" : "battery")}): {IdleText(minutes * 60)}", "info");
                ShowToastSuccess(string.Format(PSim("Saved", "Saved — {0}: {1}"), title, IdleText(minutes * 60)));
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
            await RefreshPowerSimpleAsync();
        }

        private async Task SetButtonAsync(string what, int action, bool ac, string title)
        {
            try
            {
                await Task.Run(() => PowerAdvancedService.SetButtonAction(what, action, ac));
                ToolActivityLog.Add("Power", $"{title} ({(ac ? "plugged in" : "battery")}): {ActionText(action)}", "info");
                ShowToastSuccess(string.Format(PSim("Saved", "Saved — {0}: {1}"), title, ActionText(action)));
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
            await RefreshPowerSimpleAsync();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  The processor in three steps
        // ════════════════════════════════════════════════════════════════════════════════════════

        private static readonly (string Key, string Title, string Sub)[] CpuModes =
        {
            ("cool", "Cool & quiet", "no Turbo"),
            ("normal", "Normal", "Turbo when needed"),
            ("full", "Full speed", "always fast"),
        };

        private void BuildCpuModeButtons()
        {
            if (FindName("CpuModeHost") is not System.Windows.Controls.Primitives.UniformGrid host) return;
            host.Children.Clear();
            foreach (var (key, title, sub) in CpuModes)
            {
                var sp = new StackPanel { HorizontalAlignment = HAlign.Center };
                var t = new TextBlock { Text = PSim("Cpu_" + key, title), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HAlign.Center };
                var s = new TextBlock { Text = PSim("CpuSub_" + key, sub), FontSize = Ui.FontXS, HorizontalAlignment = HAlign.Center, Opacity = 0.8 };
                sp.Children.Add(t); sp.Children.Add(s);
                var rb = new RadioButton { GroupName = "CpuModeGroup", Tag = key, Content = sp, HorizontalAlignment = HAlign.Stretch };
                if (TryFindResource("SegmentChipStyle") is Style st) rb.Style = st;
                rb.Checked += async (_, _) => { if (!_cpuModeSync) await ApplyCpuModeAsync(key); };
                host.Children.Add(rb);
            }
        }

        /// <summary>The processor step of the source shown (a laptop shows plugged in or on battery; a PC has one).</summary>
        private string CurrentCpuMode()
        {
            bool dc = _psLaptop && _prSrc == "dc";
            var (boost, min, max) = dc ? (_psCpuBoostDc, _psCpuMinDc, _psCpuMaxDc) : (_psCpuBoost, _psCpuMin, _psCpuMax);
            if (boost == PowerAdvancedService.BoostMode.Disabled) return "cool";
            if (min is >= 100) return "full";
            if (max is 100 && min is < 100) return "normal";
            return "";
        }

        private void SyncCpuModeButtons()
        {
            string mode = CurrentCpuMode();
            _cpuModeSync = true;
            try
            {
                if (FindName("CpuModeHost") is System.Windows.Controls.Panel host)
                    foreach (var rb in host.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == mode;
                if (FindName("TxtCpuModeExplain") is TextBlock ex)
                    ex.Text = mode switch
                    {
                        "cool"   => PSim("CpuExplain_cool", "Never goes above its base speed. Cooler and quieter, a bit slower in games and heavy work."),
                        "full"   => PSim("CpuExplain_full", "Always at full speed, even when idle. More heat and fan noise."),
                        "normal" => PSim("CpuExplain_normal", "Speeds up for heavy work and slows down when idle. The usual choice."),
                        _        => (_psLaptop && _prSrc == "dc" ? _psCpuMinDc : _psCpuMin).HasValue
                            ? string.Format(PSim("CpuExplain_custom", "Custom settings ({0}–{1} %) — see Advanced settings."),
                                            _psLaptop && _prSrc == "dc" ? _psCpuMinDc : _psCpuMin, _psLaptop && _prSrc == "dc" ? _psCpuMaxDc : _psCpuMax)
                            : "",
                    };
            }
            finally { _cpuModeSync = false; }
        }

        private async Task ApplyCpuModeAsync(string mode)
        {
            int keep = AskTurboPersistence();
            if (keep < 0) { SyncCpuModeButtons(); return; }
            if (keep == 0)
            {
                TurboRestore.RememberOriginalIfNeeded(TurboRestore.Part.Turbo);
                TurboRestore.RememberOriginalIfNeeded(TurboRestore.Part.ProcState);
            }
            try
            {
                // a laptop changes only the source shown in the plan card (Windows keeps both values and switches by itself)
                bool toAc = !_psLaptop || _prSrc == "ac", toDc = !_psLaptop || _prSrc == "dc";
                await Task.Run(() =>
                {
                    var (boost, min, max) = mode switch
                    {
                        "cool" => (PowerAdvancedService.BoostMode.Disabled, 5, 100),
                        "full" => (PowerAdvancedService.BoostMode.Enabled, 100, 100),
                        _      => (PowerAdvancedService.BoostMode.Enabled, 5, 100),
                    };
                    PowerAdvancedService.SetBoostMode(boost, applyToAc: toAc, applyToDc: toDc);
                    PowerAdvancedService.SetMinProcessorState(min, applyToAc: toAc, applyToDc: toDc);
                    PowerAdvancedService.SetMaxProcessorState(max, applyToAc: toAc, applyToDc: toDc);
                });
                FinishTurboChange(keep, "processor: " + mode);
                LoadAdvancedPowerSection();
                InitTurboButtonState();
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
            await RefreshPowerSimpleAsync();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Ultimate Performance (desktops)
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Keeps the list of plans on this PC; the plan card offers Ultimate as one of its plans (MainWindow.PowerRedesign.cs).</summary>
        private void BuildUltimateRow(List<string> planGuids)
        {
            _prPlanGuids = planGuids.Select(g => g.ToLowerInvariant()).ToList();
            if (FindName("TxtUltimateRow") is TextBlock row) row.Visibility = Visibility.Collapsed;
        }

        private async Task ActivatePlanAsync(string guid)
        {
            try
            {
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("powercfg", $"/setactive {guid}") { UseShellExecute = false, CreateNoWindow = true };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(3000);
                });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ActivatePlanAsync"); }
            LoadActivePowerPlan();
            await RefreshPowerSimpleAsync();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Start-up and shut-down
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Start-up and shut-down, one row each: icon · title + one line · the control on the right (the full text in the
        /// tooltip). Fast Startup (+ days since the last full restart), Hibernate, sleep type, reset the plans.</summary>
        private void BuildStartupTiles(long? hiberfil)
        {
            if (FindName("PowerStartupHost") is not System.Windows.Controls.Primitives.UniformGrid host) return;
            host.Children.Clear();

            // 1. Fast Startup
            if (_pdFastStartup.HasValue)
            {
                bool on = _pdFastStartup.Value;
                int days = (int)PowerOnClock.KernelUptime.TotalDays;
                var toggle = Toggle(on, async v =>
                {
                    try
                    {
                        await Task.Run(() => PowerAdvancedService.SetFastStartup(v));
                        if (ChkFastStartup != null) ChkFastStartup.IsChecked = v;
                        ToolActivityLog.Add("Power", $"Fast startup: {(v ? "on" : "off")}", "info");
                    }
                    catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "Fast Startup"); }
                    await RefreshPowerSimpleAsync();
                });
                var right = new List<UIElement>();
                string sub, brush = "TextSecondaryBrush";
                if (on && days >= 2)
                {
                    sub = string.Format(PSim("NotRestartedShort", "Not fully restarted for {0} days"), days);
                    if (days >= 14) brush = "StatusCheckBrush";
                    right.Add(SmallButton(PSim("RestartShort", "Restart"), PSim("RestartNow", "Restart now"), () => { AskRestartNow(); return Task.CompletedTask; }));
                }
                else sub = on ? PSim("FastSubShort", "Starts faster; “Shut down” does not fully restart Windows")
                              : PSim("FastOffNote", "“Shut down” fully turns Windows off.");
                right.Add(toggle);
                host.Children.Add(StartupRow("flash", PSim("FastStartup", "Fast Startup"), sub, brush,
                    PSim("FastSub", "Starts faster, but “Shut down” does not fully restart Windows."), right.ToArray()));
            }

            if (_pdStates != null)
            {
                // 2. Hibernate
                bool hib = _pdStates.HasHibernate || hiberfil is > 0;
                var hibToggle = Toggle(hib, async v =>
                {
                    try { await Task.Run(() => PowerAdvancedService.SetHibernate(v)); ToolActivityLog.Add("Power", $"Hibernate: {(v ? "on" : "off")}", "info"); }
                    catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "Hibernate"); }
                    _pdStates = null;   // read again
                    await RefreshPowerSimpleAsync();
                });
                string hibSub = hib
                    ? (hiberfil is > 0 ? string.Format(PSim("HibSize", "Uses {0} on C:."), DisplayFormat.SizeBytes(hiberfil.Value)) : PSim("HibOn", "Saves everything to the disk and turns off."))
                    : PSim("HibOffShort", "Off — Fast Startup needs it");
                host.Children.Add(StartupRow("document", PSim("Hibernate", "Hibernate"), hibSub, "TextSecondaryBrush",
                    hib ? PSim("HibOn", "Saves everything to the disk and turns off.") : PSim("HibOff", "Off — the PC cannot hibernate, and Fast Startup needs it."), hibToggle));

                // 3. Sleep type
                var (kind, text, chipBrush) = _pdStates.HasModernStandby
                    ? (PSim("SleepModern", "Modern Standby"), PSim("SleepModernSub", "The PC stays connected while asleep and wakes instantly; it uses a little battery."), "StatusOkBrush")
                    : _pdStates.HasS3
                        ? (PSim("SleepClassic", "Classic sleep (S3)"), PSim("SleepClassicSub", "The PC is fully paused while asleep."), "StatusOkBrush")
                        : (PSim("SleepNone", "No sleep"), PSim("SleepNoneSub", "Windows offers no sleep state — often a missing chipset or graphics driver."), "StatusWarnBrush");
                var chip = DiagChip(kind, chipBrush);
                chip.Margin = new Thickness(0);
                host.Children.Add(StartupRow("weather_moon", PSim("SleepType", "Sleep type"), text, "TextSecondaryBrush", text, chip));
            }

            // 4. Reset the plans
            var reset = SmallButton(PSim("Reset", "Reset"), null, async () =>
            {
                if (!AppDialog.ConfirmDanger(PSim("ResetConfirm", "Bring back the Windows power plans with their default settings?\n\nPlans you created are removed."),
                        PSim("PowerPlans", "Power plans"), PSim("Reset", "Reset"), LanguageService.S("Common", "Cancel")))
                    return;
                try { await Task.Run(PowerAdvancedService.RestoreDefaultPlans); ToolActivityLog.Add("Power", "Power plans reset to the Windows defaults", "info"); }
                catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
                SettingsService.Current.UltimatePlanGuid = null;
                SettingsService.Current.PowerPlanAc = null;
                SettingsService.Current.PowerPlanDc = null;
                SettingsService.Save();
                LoadActivePowerPlan();
                ShowToastSuccess(PSim("ResetDone", "Power plans reset to the Windows defaults"));
                await RefreshPowerSimpleAsync();
            });
            host.Children.Add(StartupRow("arrow_counterclockwise", PSim("PowerPlans", "Power plans"), PSim("ResetSubShort", "Back to the Windows plans"), "TextSecondaryBrush",
                PSim("ResetSub", "Brings back the Windows plans and their default settings."), reset));

            host.Columns = 1;
            host.Rows = Math.Max(1, host.Children.Count);
        }

        private Border StartupRow(string icon, string title, string sub, string subBrush, string tip, params UIElement[] right)
        {
            var d = new DockPanel { LastChildFill = true };
            var ic = FluentIcons.Box(icon, 16, subBrush == "StatusCheckBrush" ? "StatusCheckBrush" : "DataBrush");
            ic.Margin = new Thickness(0, 0, 10, 0);
            ic.VerticalAlignment = VAlign.Center;
            DockPanel.SetDock(ic, Dock.Left);
            d.Children.Add(ic);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center, Margin = new Thickness(10, 0, 0, 0) };
            foreach (var r in right)
            {
                if (r is FrameworkElement fe) { fe.VerticalAlignment = VAlign.Center; fe.Margin = new Thickness(acts.Children.Count > 0 ? 8 : 0, 0, 0, 0); }
                acts.Children.Add(r);
            }
            DockPanel.SetDock(acts, Dock.Right);
            d.Children.Add(acts);
            // only the title and the control on the row; the explanation (and the state line, e.g. the size of the
            // hibernation file) is shown on hover. A state that needs attention (not restarted for 14+ days) tints the icon.
            string hover = sub.Length > 0 && sub != tip ? sub + "\n" + tip : tip;
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VAlign.Center, FontSize = Ui.FontM };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            d.Children.Add(t);
            d.ToolTip = hover;
            d.Background = System.Windows.Media.Brushes.Transparent;   // the tooltip also on the empty part of the row
            var row = new Border { Child = d, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8), MinHeight = 40 };
            row.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            row.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return row;
        }

        private static TextBlock PsSmall(string text, string brush = "TextSecondaryBrush")
        {
            var t = Txt2(text, brush, Ui.FontXS);
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        private CheckBox Toggle(bool on, Func<bool, Task> changed)
        {
            var c = new CheckBox { IsChecked = on, VerticalAlignment = VAlign.Center };
            if (TryFindResource("ToggleSwitchStyle") is Style st) c.Style = st;
            c.Click += async (_, _) => await changed(c.IsChecked == true);
            return c;
        }

        private void AskRestartNow()
        {
            if (!AppDialog.Confirm(PSim("RestartConfirm", "Restart Windows now?\n\nSave your work first — open programs will be closed."), "SMD Win")) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "Restart"); }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Keep awake
        // ════════════════════════════════════════════════════════════════════════════════════════

        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

        private string _keepFor = "60";
        private DateTime _keepUntil, _keepStarted;
        private bool _keepOn, _keepSawTest;
        private System.Windows.Threading.DispatcherTimer? _keepTimer;

        private static string KeepLabel(string key) => key switch
        {
            "30" => "30 min", "60" => PSim("Keep1h", "1 hour"), "120" => PSim("Keep2h", "2 hours"),
            "test" => PSim("KeepTest", "While a test runs"), _ => PSim("KeepOff", "Until turned off"),
        };

        private void ShowKeepAwakeMenu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var key in new[] { "30", "60", "120", "test", "off" })
            {
                var mi = new MenuItem { Header = KeepLabel(key), IsCheckable = true, IsChecked = key == _keepFor };
                mi.Click += (_, _) =>
                {
                    _keepFor = key;
                    anchor.Content = KeepLabel(key);
                    if (_keepOn) SetKeepAwake(true);   // restart with the new duration
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        /// <summary>Keeps the PC and the screen awake (SetThreadExecutionState on the UI thread, which lives as long as the app);
        /// the power plan is not touched. Ends by itself after the chosen time / when the test ends; Windows also ends it when
        /// SMD Win closes.</summary>
        private void SetKeepAwake(bool on)
        {
            try
            {
                _keepOn = on;
                SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
                _keepStarted = DateTime.Now;
                _keepSawTest = false;
                _keepUntil = int.TryParse(_keepFor, out int min) ? DateTime.Now.AddMinutes(min) : DateTime.MaxValue;
                if (on)
                {
                    _keepTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                    _keepTimer.Tick -= KeepAwakeTick; _keepTimer.Tick += KeepAwakeTick;
                    _keepTimer.Start();
                }
                else _keepTimer?.Stop();
                UpdateKeepAwakeUi();
                ToolActivityLog.Add("Power", on ? $"Keep awake: on ({KeepLabel(_keepFor)})" : "Keep awake: off", "info");
            }
            catch (Exception ex) { AppLogger.Debug($"SetKeepAwake: {ex.Message}"); }
        }

        private void KeepAwakeTick(object? s, EventArgs e)
        {
            if (!_keepOn) return;
            if (_keepFor == "test")
            {
                if (_logoBusy) _keepSawTest = true;
                // ends when the test ends (or after 2 minutes if no test started)
                if ((_keepSawTest && !_logoBusy) || (!_keepSawTest && (DateTime.Now - _keepStarted).TotalMinutes > 2)) SetKeepAwake(false);
            }
            else if (DateTime.Now >= _keepUntil) SetKeepAwake(false);
            UpdateKeepAwakeUi();
        }

        private void UpdateKeepAwakeUi()
        {
            if (FindName("ChkKeepAwake") is CheckBox c && c.IsChecked != _keepOn) c.IsChecked = _keepOn;
            if (FindName("TxtKeepAwakeState") is TextBlock t)
                t.Text = !_keepOn ? PSim("KeepStateOff", "off")
                       : _keepFor == "test" ? PSim("KeepStateTest", "while a test runs")
                       : _keepUntil == DateTime.MaxValue ? PSim("KeepStateOn", "on")
                       : string.Format(PSim("KeepStateUntil", "until {0:HH:mm}"), _keepUntil);
            if (FindName("PowerKeepAwakeBox") is Border box)
            {
                box.SetResourceReference(Border.BorderBrushProperty, _keepOn ? "AccentBrush" : "CardBorderBrush");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Sleep: the strip of chips and the open list
        // ════════════════════════════════════════════════════════════════════════════════════════

        private static readonly string[] NeverCloseProcesses = { "svchost.exe", "system", "audiodg.exe", "csrss.exe", "lsass.exe", "wininit.exe", "services.exe", "explorer.exe", "smdwin.exe" };

        private bool IsBadBlocker(PowerDiagnosticsService.SleepBlocker b) => b.Category is "SYSTEM" or "DISPLAY";

        /// <summary>What keeps the PC awake — drawn by the new card as three lists side by side (MainWindow.PowerRedesign.cs).</summary>
        private void BuildSleepStrip()
        {
            RenderAwakeCard();
            UpdatePowerTabs();
        }

        private static string ShortWakeSource(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return "?";
            string s = System.Text.RegularExpressions.Regex.Replace(src, @"^(Device|Dispozitiv)\s*-\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (src.Contains("Power Button", StringComparison.OrdinalIgnoreCase)) return PSim("WakePowerButton", "power button");
            return s.Length > 48 ? s[..47] + "…" : s;
        }

        /// <summary>A row of the open list: dot · name (bold) + one-line detail · actions.</summary>
        private Grid SleepRow(StackPanel list, string dotKey, string name, string detail, params UIElement[] actions)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VAlign.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
            g.Children.Add(dot);
            var t = new TextBlock { FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VAlign.Center, ToolTip = $"{name}\n{detail}".Trim() };
            var n = new Run(name) { FontWeight = FontWeights.SemiBold };
            n.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
            t.Inlines.Add(n);
            if (detail.Length > 0)
            {
                var d = new Run("   " + detail);
                d.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
                t.Inlines.Add(d);
            }
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center, Margin = new Thickness(10, 0, 0, 0) };
            foreach (var a in actions) { if (a is FrameworkElement fe) fe.Margin = new Thickness(6, 0, 0, 0); acts.Children.Add(a); }
            Grid.SetColumn(acts, 2); g.Children.Add(acts);
            var wrap = new Border { Child = g, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 0, 0, 5), BorderThickness = new Thickness(1) };
            wrap.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            g.Margin = new Thickness(0);
            list.Children.Add(wrap);
            return g;
        }

        private Wpf.Ui.Controls.Button SmallButton(string text, string? tip, Func<Task> click)
        {
            var b = new Wpf.Ui.Controls.Button { Content = text, ToolTip = tip, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Padding = new Thickness(10, 2, 10, 2), FontSize = Ui.FontS };
            b.Click += async (_, _) => { b.IsEnabled = false; try { await click(); } finally { b.IsEnabled = true; } };
            return b;
        }

        private void FillBlockers(StackPanel list)
        {
            var items = (_pdBlockers ?? new()).OrderByDescending(IsBadBlocker).ToList();
            if (items.Count == 0) SleepRow(list, "StatusOkBrush", PS("Nothing", "Nothing"), PS("NothingDetail", "The PC can sleep and turn off the screen normally"));
            foreach (var b in items.Take(12))
            {
                string what = b.Category switch
                {
                    "DISPLAY" => PS("CatDisplay", "Keeps the screen on"), "SYSTEM" => PS("CatSystem", "Blocks sleep"),
                    "AWAYMODE" => PS("CatAway", "Away mode"), "EXECUTION" => PS("CatExec", "Keeps running"), _ => b.Category,
                };
                string detail = string.IsNullOrWhiteSpace(b.Reason) ? what : $"{what} · {b.Reason}";
                if (!IsBadBlocker(b)) { SleepRow(list, "TextSecondaryBrush", CleanDeviceName(b.Name), detail, DiagChip(PSim("Normal", "Normal"), "StatusOkBrush")); continue; }
                var acts = new List<UIElement>();
                if (b.Kind is "PROCESS" or "SERVICE" or "DRIVER")
                    acts.Add(SmallButton(PSim("Ignore", "Ignore"), PSim("IgnoreTip", "Windows will no longer let it stop sleep or keep the screen on"), async () =>
                    {
                        bool ok = await Task.Run(() => PowerDiagnosticsService.AddRequestOverride(b));
                        if (!ok) AppDialog.Show(string.Format(PSim("IgnoreFailed", "Windows did not accept the change for \"{0}\"."), b.Name), "SMD Win", AppDialog.Kind.Warning);
                        else { ToolActivityLog.Add("Power", $"Sleep: ignoring {b.Kind} {b.Name}", "info"); ShowToastSuccess(PSim("IgnoreDone", "Ignored — it can no longer stop sleep")); }
                        await RefreshPowerDiagAsync(force: true);
                    }));
                if (b.Kind == "PROCESS" && !NeverCloseProcesses.Contains(b.Name.ToLowerInvariant()))
                    acts.Add(SmallButton(PSim("CloseApp", "Close app"), null, async () =>
                    {
                        string proc = System.IO.Path.GetFileNameWithoutExtension(b.Name);
                        if (!AppDialog.Confirm(string.Format(PSim("CloseConfirm", "Close {0}?\n\nUnsaved work in it is lost."), proc), "SMD Win")) return;
                        await Task.Run(() =>
                        {
                            foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
                                using (p) try { p.Kill(entireProcessTree: true); } catch (Exception ex) { AppLogger.Debug($"Close {proc}: {ex.Message}"); }
                        });
                        ToolActivityLog.Add("Power", $"Sleep: closed {proc}", "info");
                        await Task.Delay(800);
                        await RefreshPowerDiagAsync(force: true);
                    }));
                SleepRow(list, "StatusWarnBrush", CleanDeviceName(b.Name), detail, acts.ToArray());
            }
            // what was ignored before, with Undo
            foreach (var o in (_pdOverrides ?? new()).Take(8))
                SleepRow(list, "TextSecondaryBrush", CleanDeviceName(o.Name), PSim("Ignored", "ignored — cannot stop sleep"),
                    SmallButton(PSim("Undo", "Undo"), null, async () =>
                    {
                        await Task.Run(() => PowerDiagnosticsService.RemoveRequestOverride(o));
                        ToolActivityLog.Add("Power", $"Sleep: no longer ignoring {o.Name}", "info");
                        await RefreshPowerDiagAsync(force: true);
                    }));
        }

        private void FillTimers(StackPanel list)
        {
            bool on = _pdWakeTimers is null or not 0;
            if (on)
            {
                if (_pdTimers is { Count: 0 }) SleepRow(list, "StatusOkBrush", PSim("NoTimers", "No scheduled wake-up"), PSim("NoTimersSub", "Nothing is set to turn the PC on"));
                foreach (var t in (_pdTimers ?? new()).Take(10))
                {
                    string? task = PowerDiagnosticsService.TaskPathOf(t);
                    string name = task != null ? task.TrimStart('\\').Split('\\').Last() : t.Owner;
                    string detail = task ?? (string.IsNullOrWhiteSpace(t.Reason) ? PS("TimerDefault", "Scheduled to wake the PC") : t.Reason);
                    if (task != null)
                        SleepRow(list, "StatusWarnBrush", name, detail, SmallButton(PSim("TurnOff", "Turn off"), PSim("TurnOffTip", "The task keeps running, but no longer turns the PC on"), async () =>
                        {
                            bool ok = await Task.Run(() => PowerDiagnosticsService.StopTaskWaking(task));
                            if (!ok) AppDialog.Show(string.Format(PSim("TurnOffFailed", "Could not change the task \"{0}\". It can be changed in Task Scheduler (Conditions › Wake the computer)."), task), "SMD Win", AppDialog.Kind.Warning);
                            else { ToolActivityLog.Add("Power", $"Wake-up: task {task} no longer wakes the PC", "info"); ShowToastSuccess(PSim("TurnOffDone", "Done — the task can no longer wake the PC")); }
                            await RefreshPowerDiagAsync(force: true);
                        }));
                    else SleepRow(list, "StatusWarnBrush", name, detail);
                }
            }
            // the general switch: scheduled wake-ups allowed or not
            var tg = Toggle(on, async allow =>
            {
                try { await Task.Run(() => PowerAdvancedService.SetWakeTimersAllowed(allow)); ToolActivityLog.Add("Power", $"Wake timers: {(allow ? "allowed" : "off")}", "info"); }
                catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
                await RefreshPowerDiagAsync(force: true);
            });
            SleepRow(list, "AccentBrush", PSim("AllowTimers", "Allow scheduled wake-ups"), PSim("AllowTimersSub", "tasks such as updates and backups can turn the PC on"), tg);
        }

        private void FillDevices(StackPanel list)
        {
            var armed = new HashSet<string>(_pdArmed ?? new(), StringComparer.OrdinalIgnoreCase);
            var all = (_pdProgrammable ?? new()).Concat(armed).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(d => armed.Contains(d)).ThenBy(d => d).Take(14).ToList();
            if (all.Count == 0) SleepRow(list, "StatusOkBrush", PS("NoDevice", "No device"), PS("NoDeviceDetail", "Only the power button wakes the PC"));
            foreach (var d in all)
            {
                bool on = armed.Contains(d);
                string u = d.ToUpperInvariant();
                string kind = u.Contains("KEYBOARD") || u.Contains("TASTAT") ? PSim("DevKeyboard", "keyboard")
                            : u.Contains("MOUSE") || u.Contains("POINTING") ? PSim("DevMouse", "mouse")
                            : u.Contains("ETHERNET") || u.Contains("NETWORK") || u.Contains("WI-FI") || u.Contains("WIRELESS") || u.Contains("GBE") || u.Contains("LAN") ? PSim("DevNetwork", "network card")
                            : "";
                var tg = Toggle(on, async allow =>
                {
                    bool ok = await Task.Run(() => allow ? PowerDiagnosticsService.EnableWake(d) : PowerDiagnosticsService.DisableWake(d));
                    if (!ok) AppDialog.Show(string.Format(PS("BlockFailed", "Could not change the wake setting for \"{0}\"."), d), "SMD Win", AppDialog.Kind.Error);
                    else ToolActivityLog.Add("Power", $"Wake: {d} {(allow ? "can" : "can no longer")} wake the PC", "info");
                    await RefreshPowerDiagAsync(force: true);
                });
                tg.ToolTip = on ? PSim("CanWake", "can wake the PC") : PSim("CannotWake", "cannot wake the PC");
                SleepRow(list, on ? "AccentBrush" : "TextSecondaryBrush", CleanDeviceName(d),
                         (kind.Length > 0 ? kind + " · " : "") + (on ? PSim("CanWake", "can wake the PC") : PSim("CannotWake", "cannot wake the PC")), tg);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Power check
        // ════════════════════════════════════════════════════════════════════════════════════════

        internal sealed record PowerFinding(string Level, string Title, string Detail, string? Action, Func<Task>? Fix);

        private async Task RunPowerCheckAsync()
        {
            var win = new PowerCheckWindow(PSim("PowerCheck", "Power check")) { Owner = this };
            win.OpenFullReport = () => GenerateEnergyReport_Click(FindName("BtnGenerateEnergyReport") ?? this, new RoutedEventArgs());
            win.Show();
            await Task.WhenAll(RefreshPowerDiagAsync(force: true), RefreshPowerSimpleAsync());
            win.SetFindings(BuildPowerFindings());
        }

        private List<PowerFinding> BuildPowerFindings()
        {
            var f = new List<PowerFinding>();
            foreach (var b in (_pdBlockers ?? new()).Where(IsBadBlocker).Take(3))
                f.Add(new PowerFinding("warn", string.Format(PSim("FKeepsAwake", "{0} keeps the PC awake"), CleanDeviceName(b.Name)),
                    string.IsNullOrWhiteSpace(b.Reason) ? PSim("FKeepsAwakeSub", "While it does, the PC does not go to sleep on its own.") : b.Reason,
                    b.Kind is "PROCESS" or "SERVICE" or "DRIVER" ? PSim("Ignore", "Ignore") : null,
                    async () => { await Task.Run(() => PowerDiagnosticsService.AddRequestOverride(b)); await RefreshPowerDiagAsync(force: true); }));
            if (_pdWakeTimers is null or not 0)
                foreach (var t in (_pdTimers ?? new()).Take(3))
                {
                    string? task = PowerDiagnosticsService.TaskPathOf(t);
                    f.Add(new PowerFinding("warn", string.Format(PSim("FWakes", "{0} can turn the PC on"), task?.TrimStart('\\').Split('\\').Last() ?? t.Owner),
                        PSim("FWakesSub", "A scheduled task can wake the PC from sleep, for example at night."),
                        task != null ? PSim("TurnOff", "Turn off") : null,
                        task == null ? null : async () => { await Task.Run(() => PowerDiagnosticsService.StopTaskWaking(task)); await RefreshPowerDiagAsync(force: true); }));
                }
            if (_pdStates != null && !_pdStates.HasS3 && !_pdStates.HasModernStandby)
                f.Add(new PowerFinding("warn", PSim("FNoSleep", "This PC cannot sleep"), PSim("SleepNoneSub", "Windows offers no sleep state — often a missing chipset or graphics driver."), null, null));
            else if (_idleSleepAc is 0)
                f.Add(new PowerFinding("info", PSim("FNeverSleeps", "The PC never sleeps when plugged in"),
                    PSim("FNeverSleepsSub", "Fine if it should always stay on. Otherwise 1 hour of sleep saves energy."), PSim("Set1h", "Set 1 hour"),
                    () => SetIdleAsync("sleep", 60, true, PSim("GoSleep", "Go to sleep"))));
            if (_psLaptop && _idleSleepDc is 0)
                f.Add(new PowerFinding("warn", PSim("FNeverSleepsBat", "The laptop never sleeps on battery"),
                    PSim("FNeverSleepsBatSub", "Left on, the battery runs down completely."), PSim("Set15", "Set 15 min"),
                    () => SetIdleAsync("sleep", 15, false, PSim("GoSleep", "Go to sleep"))));
            if (_psCpuMin is >= 100)
                f.Add(new PowerFinding("info", PSim("FCpuFull", "The processor never slows down"),
                    PSim("FCpuFullSub", "It stays at full speed even when idle: more heat, noise and power."), PSim("SetNormal", "Set Normal"),
                    () => ApplyCpuModeAsync("normal")));
            int days = (int)PowerOnClock.KernelUptime.TotalDays;
            if (_pdFastStartup == true && days >= 14)
                f.Add(new PowerFinding("info", string.Format(PSim("FNoRestart", "Windows has not fully restarted for {0} days"), days),
                    PSim("FNoRestartSub", "“Shut down” keeps it running (Fast Startup). A restart now and then applies updates and frees memory."),
                    PSim("RestartNow", "Restart now"), () => { AskRestartNow(); return Task.CompletedTask; }));
            foreach (var d in (_pdArmed ?? new()).Where(d => { var u = d.ToUpperInvariant(); return u.Contains("ETHERNET") || u.Contains("NETWORK") || u.Contains("GBE") || u.Contains("WI-FI") || u.Contains("WIRELESS"); }).Take(1))
                f.Add(new PowerFinding("info", PSim("FNetWake", "The network card can wake the PC"),
                    PSim("FNetWakeSub", "Network traffic can then turn the PC on at random times."), PSim("StopIt", "Stop it"),
                    async () => { await Task.Run(() => PowerDiagnosticsService.DisableWake(d)); await RefreshPowerDiagAsync(force: true); }));
            if (!f.Any(x => x.Level == "warn"))
                f.Add(new PowerFinding("ok", PSim("FSleepOk", "Nothing stops the PC from sleeping"), PSim("FSleepOkSub", "No app or task keeps it awake right now."), null, null));
            if (_psCpuMin is < 100)
                f.Add(new PowerFinding("ok", PSim("FCpuOk", "Processor settings are normal"), PSim("FCpuOkSub", "It slows down when idle and speeds up when needed."), null, null));
            return f;
        }
    }

    /// <summary>The small "Power check" window: a list of findings, each with its fix button, and a link to the full Windows report.</summary>
    internal sealed class PowerCheckWindow : Window
    {
        private readonly StackPanel _list = new();
        private readonly TextBlock _intro;
        public Action? OpenFullReport { get; set; }

        public PowerCheckWindow(string title)
        {
            NetWindowHelper.SetupWindow(this, title + " — SMD Win", 600, 560);
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hdr = NetWindowHelper.ToolHeader(title, LanguageService.S("PowerSimple", "CheckSub", "Settings and devices that waste power or stop sleep"), out var actions);
            NetWindowHelper.AddCloseButton(this, actions);
            NetWindowHelper.MakeDraggable(hdr, this);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);
            _intro = new TextBlock { Text = LanguageService.S("PowerSimple", "Checking", "Checking settings and devices…"), Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap };
            _intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(_intro, 1); root.Children.Add(_intro);
            var sv = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(sv, 2); root.Children.Add(sv);
            var foot = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = Ui.FontS };
            foot.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            foot.Inlines.Add(new Run(LanguageService.S("PowerSimple", "FullReport", "The full Windows report (about 60 s): ")));
            var link = new Hyperlink(new Run(LanguageService.S("PowerSimple", "OpenDetails", "Open details"))) { FontWeight = FontWeights.SemiBold };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) => OpenFullReport?.Invoke();
            foot.Inlines.Add(link);
            Grid.SetRow(foot, 3); root.Children.Add(foot);
            Content = NetWindowHelper.WindowShell(root);
        }

        internal void SetFindings(List<MainWindow.PowerFinding> findings)
        {
            _list.Children.Clear();
            int warn = findings.Count(f => f.Level == "warn"), info = findings.Count(f => f.Level == "info");
            _intro.Text = warn == 0 && info == 0
                ? LanguageService.S("PowerSimple", "AllFine", "Everything looks fine.")
                : string.Format(LanguageService.S("PowerSimple", "Summary", "{0} to look at, {1} suggestion(s), the rest is fine."), warn, info);
            foreach (var f in findings)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, f.Level == "warn" ? "StatusWarnBrush" : f.Level == "info" ? "AccentBrush" : "StatusOkBrush");
                g.Children.Add(dot);
                var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var t = new TextBlock { Text = f.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var d = new TextBlock { Text = f.Detail, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
                d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(t); sp.Children.Add(d);
                Grid.SetColumn(sp, 1); g.Children.Add(sp);
                if (f.Action != null && f.Fix != null)
                {
                    var b = new Wpf.Ui.Controls.Button { Content = f.Action, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VAlign.Center };
                    b.Click += async (_, _) =>
                    {
                        b.IsEnabled = false;
                        try { await f.Fix(); b.Content = "✓ " + LanguageService.S("PowerSimple", "Done", "Done"); dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusOkBrush"); }
                        catch (Exception ex) { AppLogger.Warning(ex, "PowerCheck fix"); b.IsEnabled = true; }
                    };
                    Grid.SetColumn(b, 2); g.Children.Add(b);
                }
                var wrap = new Border { Child = g, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8) };
                wrap.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                _list.Children.Add(wrap);
            }
        }
    }
}
