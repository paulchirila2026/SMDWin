using System;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Chart recording while the charts are not on screen (window minimized, in the tray or on a page without sensors).
    /// The <c>BackgroundRecording</c> setting in Settings → Behavior:
    ///   • Full    — sensors are read at the rate set in Settings; on systems where a read takes long
    ///               (usually a dedicated laptop GPU) the rate slows down by itself to 2–3 s;
    ///   • Reduced — one read every 5 s;
    ///   • Off     — the charts are paused; only the rare read for alerts, widget and tray remains.
    /// Between two reads the chart gets a continuous line (interpolation), never repeated values over long intervals; an
    /// absence longer than <see cref="ChartInterpolateMaxSlots"/> stays a gap.
    /// Drawing is skipped completely while the window is hidden — only the data is recorded.
    /// </summary>
    public partial class MainWindow
    {
        private enum BgRecMode { Full, Reduced, Off }

        private static BgRecMode BgRec => (SettingsService.Current.BackgroundRecording ?? "Full") switch
        {
            "Reduced" => BgRecMode.Reduced,
            "Off"     => BgRecMode.Off,
            _         => BgRecMode.Full,
        };

        /// <summary>The main window is not visible: minimized or hidden in the tray.</summary>
        private bool IsUiHidden => WindowState == System.Windows.WindowState.Minimized || !IsVisible;

        /// <summary>Beyond this many seconds without a read, the gap in the chart stays empty (not joined by a line).</summary>
        private const int ChartInterpolateMaxSlots = 15;

        // the average duration of a sensor read (moving average), in ms
        private double _sensorReadEmaMs;
        // how long the counter thread (CPU %, RAM) sleeps while the window is hidden
        private volatile int _hiddenPollMs = 1000;
        private bool _bgWasHidden;

        private DateTime _bgCostStartUtc = DateTime.MinValue;
        private TimeSpan _bgCostStartCpu;
        private double _bgLastCostPct = double.NaN;

        private DateTime _lastActPushUtc = DateTime.MinValue;

        private void NoteSensorReadMs(long readMs)
        {
            if (readMs <= 0 || readMs > 15000) return;   // over the timeout = sleep/resume, not a real read
            _sensorReadEmaMs = _sensorReadEmaMs <= 0 ? readMs : _sensorReadEmaMs * 0.8 + readMs * 0.2;
        }

        /// <summary>The rate chosen in Settings → Refresh every → Sensors. Charts have one point per second, so it never goes
        /// below 1 s.</summary>
        private static double UserSensorIntervalMs() => Math.Max(1000.0, SettingsService.Current.RefreshInterval * 1000.0);

        /// <summary>How often the sensors are read while the window is hidden.</summary>
        private double HiddenSensorIntervalMs()
        {
            // the compact widget and the tray icons show live values even with the window hidden
            bool liveConsumers = _compactWidget != null || _traySensorIcons.Count > 0;
            switch (BgRec)
            {
                case BgRecMode.Full:
                    double ms = UserSensorIntervalMs();
                    if (_sensorReadEmaMs > 300) ms = Math.Max(ms, 3000);
                    else if (_sensorReadEmaMs > 150) ms = Math.Max(ms, 2000);
                    return ms;
                case BgRecMode.Reduced:
                    return liveConsumers ? 3000 : 5000;
                default:
                    return liveConsumers ? 3000 : 10000;
            }
        }

        /// <summary>On pages without sensors: how long after the last full read another one is made.</summary>
        private double OffPageFullReadGapMs(bool liveConsumers) => BgRec switch
        {
            BgRecMode.Full    => 0,
            BgRecMode.Reduced => liveConsumers ? 2500 : 4500,
            _                 => liveConsumers ? 3000 : 15000,
        };

        /// <summary>Puts the sensor timer on the rate that fits the window state. Called on minimize, restore, hide to tray and
        /// when the setting changes.</summary>
        private void ApplySensorCadence()
        {
            try
            {
                bool hidden = IsUiHidden;
                _hiddenPollMs = BgRec == BgRecMode.Full ? 1000 : 3000;
                if (hidden != _bgWasHidden)
                {
                    _bgWasHidden = hidden;
                    if (hidden) BgCostBegin();
                    else OnUiShownAgain();
                }
                if (_tempTimer == null) return;
                double ms = hidden ? HiddenSensorIntervalMs() : UserSensorIntervalMs();
                if (Math.Abs(_tempTimer.Interval.TotalMilliseconds - ms) > 1)
                    _tempTimer.Interval = TimeSpan.FromMilliseconds(ms);
            }
            catch (Exception ex) { AppLogger.Debug($"ApplySensorCadence: {ex.Message}"); }
        }

        private void OnUiShownAgain()
        {
            BgCostEnd();
            if (BgRec == BgRecMode.Off)
            {
                // after a pause the charts continue from the moment of return, without filling the missing interval
                _lastDbPushUtc = DateTime.MinValue;
                _lastActPushUtc = DateTime.MinValue;
            }
            _renderDirty = true;
        }

        // ── Cat consuma aplicatia in fundal ──────────────────────────────────────────────────────
        private void BgCostBegin()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                _bgCostStartCpu = p.TotalProcessorTime;
                _bgCostStartUtc = DateTime.UtcNow;
            }
            catch (Exception ex) { AppLogger.Debug($"BgCostBegin: {ex.Message}"); _bgCostStartUtc = DateTime.MinValue; }
        }

        /// <summary>Processor percentage (of the whole system, as in Task Manager) used by the app while it was hidden.
        /// Periods under 30 s and those with a stress test running are not representative.</summary>
        private void BgCostEnd()
        {
            try
            {
                if (_bgCostStartUtc == DateTime.MinValue) return;
                double wallSec = (DateTime.UtcNow - _bgCostStartUtc).TotalSeconds;
                _bgCostStartUtc = DateTime.MinValue;
                if (wallSec < 30 || _cpuStress.Running || _gpuStress.IsRunning) return;
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                double cpuSec = (p.TotalProcessorTime - _bgCostStartCpu).TotalSeconds;
                _bgLastCostPct = Math.Max(0, cpuSec / (wallSec * Math.Max(1, Environment.ProcessorCount)) * 100.0);
                AppLogger.Info("Background recording ({0}): {1:F2}% CPU over {2:F0}s hidden, sensor read avg {3:F0} ms, interval {4:F0} ms",
                    BgRec, _bgLastCostPct, wallSec, _sensorReadEmaMs, HiddenSensorIntervalMs());
                UpdateBgRecDescription();
            }
            catch (Exception ex) { AppLogger.Debug($"BgCostEnd: {ex.Message}"); }
        }

        // ── Scrierea in bufferele graficelor ─────────────────────────────────────────────────────

        /// <summary>Value for step <paramref name="s"/> of <paramref name="steps"/> when one push covers several seconds:
        /// a straight line from the last value written to the new one; beyond <see cref="ChartInterpolateMaxSlots"/> steps it
        /// stays empty, with the real value only on the last one.</summary>
        private static float ChartFillValue(float prev, float cur, int s, int steps)
            => SeriesRuns.Fill(prev, cur, s, steps, ChartInterpolateMaxSlots);

        /// <summary>Writes one reading into all the chart series on Temp &amp; Stress, over <paramref name="steps"/> seconds.
        /// <paramref name="values"/> follows the order of <see cref="StressChartSeries"/>. With <paramref name="gap"/> the
        /// interval stays empty.</summary>
        private void PushChartSample(int steps, float[] values, bool gap)
        {
            var series = StressChartSeries();
            int n = Math.Min(series.Length, values.Length);
            var prev = new float[n];
            int prevIdx = (_chartIdx - 1 + ChartMaxPoints) % ChartMaxPoints;
            for (int k = 0; k < n; k++) prev[k] = _chartIdx > 0 ? series[k][prevIdx] : float.NaN;
            for (int s = 0; s < steps; s++)
            {
                int idx = _chartIdx % ChartMaxPoints;
                bool last = s == steps - 1;
                for (int k = 0; k < n; k++)
                    series[k][idx] = gap && !last ? float.NaN : ChartFillValue(prev[k], values[k], s, steps);
                _chartIdx++;
            }
        }

        private float[][]? _stressChartSeries;
        private float[][] StressChartSeries() => _stressChartSeries ??= new[]
        {
            _chartCpu, _chartGpu, _chartCpuFreq, _chartCpuUsage, _chartRamUsage, _chartCpuPower,
            _chartGpuLoad, _chartGpuFreq, _chartGpuVram, _chartGpuPower, _chartCpuVCore,
            _chartGpuMemFreq, _chartSysPower, _chartDiskTemp, _sparkGpuHistory,
        };

        /// <summary>How many real seconds have passed since the last push into the activity charts (disk, network), which also
        /// have one point per second. 0 = the charts are paused (Off, window hidden).</summary>
        private int ComputeActivityCatchUpSteps(bool windowHidden)
        {
            if (windowHidden && BgRec == BgRecMode.Off) return 0;
            var now = DateTime.UtcNow;
            if (_lastActPushUtc == DateTime.MinValue) { _lastActPushUtc = now; return 1; }
            int steps = (int)Math.Round((now - _lastActPushUtc).TotalSeconds);
            _lastActPushUtc = now;
            // after a long absence (sleep, scan) the interval is not filled with the same repeated value
            return steps > ChartInterpolateMaxSlots * 2 ? 1 : Math.Max(1, steps);
        }

        // ── Settings → Behavior → Background recording ───────────────────────────────────────────
        private void LoadBgRecIntoUI()
        {
            var mode = BgRec;
            if (FindName("RbBgRecFull") is System.Windows.Controls.RadioButton f) f.IsChecked = mode == BgRecMode.Full;
            if (FindName("RbBgRecReduced") is System.Windows.Controls.RadioButton r) r.IsChecked = mode == BgRecMode.Reduced;
            if (FindName("RbBgRecOff") is System.Windows.Controls.RadioButton o) o.IsChecked = mode == BgRecMode.Off;
            UpdateBgRecDescription();
        }

        internal void BgRecChip_Checked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not System.Windows.Controls.RadioButton rb) return;
            string mode = rb.Tag?.ToString() switch { "Reduced" => "Reduced", "Off" => "Off", _ => "Full" };
            if (SettingsService.Current.BackgroundRecording == mode) return;
            SettingsService.Current.BackgroundRecording = mode;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "BgRecChip_Checked"); }
            ApplySensorCadence();
            UpdateBgRecDescription();
        }

        /// <summary>The texts of the row in Settings, in the current language; after the first background period the
        /// description also shows the measured usage.</summary>
        private void UpdateBgRecDescription()
        {
            try
            {
                // with the English text as a fallback: an imported language pack may not have the new keys
                static string L(string key, string en) => LanguageService.S("Settings", key, en);
                if (FindName("TxtBgRecLbl") is System.Windows.Controls.TextBlock lbl) lbl.Text = L("BgRecTitle", "Background monitoring");
                if (FindName("RbBgRecFull") is System.Windows.Controls.RadioButton f)
                {
                    f.Content = L("BgRecFull", "Full");
                    f.ToolTip = L("BgRecFullTip", "Sensors are read at the selected update interval. On PCs where a reading takes longer, the interval increases to 2–3 seconds automatically.");
                }
                if (FindName("RbBgRecReduced") is System.Windows.Controls.RadioButton r)
                {
                    r.Content = L("BgRecReduced", "Reduced");
                    r.ToolTip = L("BgRecReducedTip", "One reading every 5 seconds while the charts are not on screen. Less processor use, less detailed charts.");
                }
                if (FindName("RbBgRecOff") is System.Windows.Controls.RadioButton o)
                {
                    o.Content = L("BgRecOff", "Off");
                    o.ToolTip = L("BgRecOffTip", "Charts pause while the window is hidden. Alerts, the widget and the tray icons keep working.");
                }
                if (FindName("TxtBgRecDesc") is System.Windows.Controls.TextBlock desc)
                {
                    string text = L("BgRecDesc", "Continue collecting data while the window is minimized");
                    if (!double.IsNaN(_bgLastCostPct))
                        text += " · " + string.Format(L("BgRecCost", "last time in background: {0}% processor"),
                                                      _bgLastCostPct.ToString(_bgLastCostPct < 1 ? "F1" : "F0"));
                    desc.Text = text;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateBgRecDescription: {ex.Message}"); }
        }
    }
}
