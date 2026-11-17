using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;
using Border    = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// The test bar in Temp &amp; Stress, in order:
    /// <b>what is tested</b> (CPU · GPU · CPU + GPU · Whole system) → <b>intensity</b> (Long 80 % · Standard · Maximum)
    /// → <b>stop after</b> → <b>Start test</b>.
    /// The choices are made before starting and are locked while the test runs, so the test threads are not restarted on
    /// the fly. Start and stop go through StartCpuStress, StartGpuStress, StartMaxLoad and Stop*, so the auto-stop, the test
    /// records, the snapshots and the sensor log work the same.
    /// The individual buttons (CPU Stress, GPU Stress, Max Load, Long test) stay in XAML, hidden (StressToolbarLeft), for the
    /// code that updates them.
    /// </summary>
    public partial class MainWindow
    {
        private enum StressWhat { Cpu, Gpu, Both, System }
        private enum StressIntensity { Long, Standard }

        private StressWhat _planWhat = StressWhat.Cpu;
        private StressIntensity _planIntensity = StressIntensity.Standard;
        private bool _stressPlanCompact;
        private bool _stressPlanInited;
        private bool? _planShownRunning;   // the last drawn state of the button (SetStopLook only on change)

        private static string SP(string key, string en) => LanguageService.S("Stress", key, en);

        /// <summary>A test started from the new bar or from elsewhere (self-test, Developer) is still running.</summary>
        private bool AnyStressActive =>
            _cpuStress.Running || _cpuStressWanted || _gpuStress.IsRunning || _gpuStressUiRunning || _psuStress.IsRunning;

        // ── Alegerile ──────────────────────────────────────────────────────────

        internal void StressPlan_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border b || b.Tag is not string tag) return;
            if (AnyStressActive) return;   // the choices are locked while the test runs
            switch (tag)
            {
                case "what:cpu":    _planWhat = StressWhat.Cpu; break;
                case "what:gpu":    _planWhat = StressWhat.Gpu; break;
                case "what:both":   _planWhat = StressWhat.Both; break;
                case "what:system": _planWhat = StressWhat.System; break;
                case "int:long":    _planIntensity = StressIntensity.Long; break;
                case "int:std":     _planIntensity = StressIntensity.Standard; break;
                default: return;
            }
            UpdateStressPlanUi();
        }

        /// <summary>The chosen intensity → the settings used by the existing start functions.</summary>
        private void ApplyStressIntensity()
        {
            _enduranceModeOn = _planIntensity == StressIntensity.Long;
            _stressIntensityPct = _enduranceModeOn ? 80 : 100;
            _cpuStress.IntensityPct = _stressIntensityPct;
            _gpuStress.EnduranceMode = _enduranceModeOn;
        }

        // ── Start / Stop ─────────────────────────────────────────────────

        internal void StressStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AnyStressActive) { StopStressFromPlan(); return; }

                ApplyStressIntensity();
                AppLogger.Info("Stress plan: start — what={0}, intensity={1}, stop after={2} min",
                    _planWhat, _planIntensity, _stressDurationMinutes);
                switch (_planWhat)
                {
                    case StressWhat.Cpu:  StartCpuStress(); break;
                    case StressWhat.Gpu:  StartGpuStress(); break;
                    case StressWhat.Both: StartCpuStress(); StartGpuStress(); break;
                    case StressWhat.System: StartMaxLoad(); break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "StressStart_Click"); }
            UpdateStressPlanUi();
        }

        /// <summary>Stops everything that runs — the same order as the auto-stop (CheckStressAutoStop).</summary>
        private void StopStressFromPlan()
        {
            try { StopCpuStress(); } catch (Exception ex) { AppLogger.Warning(ex, "Plan stop CPU"); }
            try { StopGpuStress(); } catch (Exception ex) { AppLogger.Warning(ex, "Plan stop GPU"); }
            try { StopAllStress(); } catch (Exception ex) { AppLogger.Warning(ex, "Plan stop all"); }
            _stressAutoStopAtUtc = null;
            UpdateStressPlanUi();
        }

        // ── The bar look ────────────────────────────────────────────────────

        /// <summary>
        /// Brings the bar back to the real state: the choices marked, locked while a test runs, the blue "Start test" button or
        /// the red "Stop · 04:12 / 10:00" one, plus the sentence that says what the test does.
        /// Called on start/stop (UpdateStressBanner), every second (UpdateStressTimers) and on a language change.
        /// </summary>
        private void UpdateStressPlanUi()
        {
            try
            {
                if (FindName("BtnStressStart") is not Wpf.Ui.Controls.Button start) return;
                bool running = AnyStressActive;

                // if a test was started from elsewhere (self-test, Developer), the bar shows what actually runs
                if (running) SyncPlanWithRunningTests();

                SetPlanText("TxtStressWhatLabel", SP("PlanWhatLabel", "Test:"));
                SetPlanText("TxtStressIntLabel", SP("PlanIntensityLabel", "Intensity:"));
                SetPlanText("TxtStressDurLabel", SP("PlanStopAfterLabel", "Stop after:"));
                SetPlanText("TxtStressWhatCpu", "CPU");
                SetPlanText("TxtStressWhatGpu", "GPU");
                SetPlanText("TxtStressWhatBoth", "CPU + GPU");
                SetPlanText("TxtStressWhatSystem", SP("PlanWhatSystem", "System"));
                SetPlanText("TxtStressIntLong", _stressPlanCompact ? SP("PlanIntLongShort", "Endurance") : SP("PlanIntLong", "Endurance"));
                SetPlanText("TxtStressIntStd", SP("PlanIntStandard", "Standard"));
                foreach (var label in new[] { "TxtStressWhatLabel", "TxtStressIntLabel", "TxtStressDurLabel" })
                    if (FindName(label) is TextBlock lt) lt.Visibility = _stressPlanCompact ? Visibility.Collapsed : Visibility.Visible;

                MarkPlanSegment("BtnStressWhatCpu", _planWhat == StressWhat.Cpu);
                MarkPlanSegment("BtnStressWhatGpu", _planWhat == StressWhat.Gpu);
                MarkPlanSegment("BtnStressWhatBoth", _planWhat == StressWhat.Both);
                MarkPlanSegment("BtnStressWhatSystem", _planWhat == StressWhat.System);
                MarkPlanSegment("BtnStressIntLong", _planIntensity == StressIntensity.Long);
                MarkPlanSegment("BtnStressIntStd", _planIntensity == StressIntensity.Standard);

                if (FindName("StressPlanChoices") is FrameworkElement choices)
                {
                    choices.IsHitTestVisible = !running;
                    choices.Opacity = running ? 0.5 : 1;
                    choices.ToolTip = running ? SP("PlanLockedTip", "Stop the test to change what is tested or the intensity.") : null;
                }

                // the button: blue "Start test" / red "Stop · time"
                if (_planShownRunning != running)
                {
                    SetStopLook(start, running, Wpf.Ui.Controls.ControlAppearance.Primary);
                    _planShownRunning = running;
                }
                if (FindName("BtnStressStartIcon") is System.Windows.Shapes.Path icon)
                {
                    string data = running ? "stop_filled" : "play_filled";
                    if (!Equals(icon.Tag, data)) { icon.Data = SMDWin.Services.FluentIcons.Get(data); icon.Tag = data; }
                    icon.Fill = System.Windows.Media.Brushes.White;
                }
                SetPlanText("TxtBtnStressStart", running ? StopLabel() : SP("PlanStart", "Start test"));

                SetPlanText("TxtStressPlanHint", running
                    ? SP("PlanRunningHint", "Test running — the stop time can still be changed.") + " " + PlanSentence()
                    : PlanSentence());
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateStressPlanUi: {ex.Message}"); }
        }

        /// <summary>"Stop · 04:12 / 10:00" (with the chosen duration) or "Stop · 04:12" (∞).</summary>
        private string StopLabel()
        {
            var starts = new List<DateTime>();
            if (_cpuStress.Running && (_cpuStress.StartedUtc ?? _cpuStressStartUtc) is DateTime c0) starts.Add(c0);
            if (_gpuStress.IsRunning && (_gpuStress.StartedUtc ?? _gpuStressStartUtc) is DateTime g0) starts.Add(g0);
            if (_psuStress.IsRunning && _psuStress.StartedUtc is DateTime p0) starts.Add(p0);
            string stop = SP("PlanStop", "Stop");
            if (starts.Count == 0) return stop;
            var elapsed = DateTime.UtcNow - starts.Min();
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            string text = $"{stop} · {Clock(elapsed)}";
            if (_stressDurationMinutes > 0) text += $" / {Clock(TimeSpan.FromMinutes(_stressDurationMinutes))}";
            return text;

            static string Clock(TimeSpan t) => t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }

        /// <summary>The sentence under the bar: what exactly the chosen test does.</summary>
        private string PlanSentence()
        {
            string what = _planWhat switch
            {
                StressWhat.Gpu    => SP("PlanSayGpu", "Graphics card under full load (DirectX 11)"),
                StressWhat.Both   => SP("PlanSayBoth", "Processor and graphics card under full load at the same time"),
                StressWhat.System => SP("PlanSaySystem", "Processor, graphics card, RAM and disk at the same time — the highest power draw"),
                _                 => SP("PlanSayCpu", "All processor cores under full load"),
            };
            bool hasCpu = _planWhat != StressWhat.Gpu;
            string how = _planIntensity switch
            {
                StressIntensity.Long => SP("PlanSayLong", "held at 80 % so the temperature settles — for tests of 30 minutes or more"),
                _ when _planWhat == StressWhat.System => SP("PlanSaySystemStd", "two cores stay free for the RAM and disk work"),
                _ when !hasCpu => "",   // 16.11.2026: "…under full load (DirectX 11), full load." said it twice
                _ => SP("PlanSayStd", "one core stays free so Windows keeps responding"),
            };
            string stopText = _stressDurationMinutes > 0
                ? string.Format(SP("PlanSayStopsAfter", "Stops automatically after {0} min."), _stressDurationMinutes)
                : SP("PlanSayRunsUntil", "Runs until stopped.");
            return how.Length > 0 ? $"{what}, {how}. {stopText}" : $"{what}. {stopText}";
        }

        /// <summary>A test started from elsewhere: the bar shows what actually runs (only "what"; the intensity stays the chosen one).</summary>
        private void SyncPlanWithRunningTests()
        {
            bool cpu = _cpuStress.Running || _cpuStressWanted, gpu = _gpuStress.IsRunning || _gpuStressUiRunning;
            if (_psuStress.IsRunning) _planWhat = StressWhat.System;
            else if (cpu && gpu) _planWhat = StressWhat.Both;
            else if (gpu) _planWhat = StressWhat.Gpu;
            else if (cpu) _planWhat = StressWhat.Cpu;
            _planIntensity = _enduranceModeOn ? StressIntensity.Long : StressIntensity.Standard;
        }

        private void MarkPlanSegment(string name, bool active)
        {
            if (FindName(name) is Border b) ApplySegmentLook(b, active);
        }

        private void SetPlanText(string name, string text)
        {
            if (FindName(name) is TextBlock t && t.Text != text) t.Text = text;
        }

        /// <summary>Called from the sensor tick: only while a test runs (the time on the button) and once after it stops.</summary>
        private void TickStressPlan()
        {
            if (AnyStressActive || _planShownRunning == true) UpdateStressPlanUi();
        }

        /// <summary>Narrow window: the "Test:" / "Intensity:" / "Stop after:" labels disappear;
        /// if it still does not fit, the bar wraps onto two rows by itself (WrapPanel).</summary>
        private void UpdateStressPlanCompact(double toolbarWidth)
        {
            bool compact = toolbarWidth < 1250;
            if (compact == _stressPlanCompact && _stressPlanInited) return;
            _stressPlanCompact = compact;
            _stressPlanInited = true;
            UpdateStressPlanUi();
        }
    }
}
