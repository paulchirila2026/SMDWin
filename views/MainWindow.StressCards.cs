using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// Temp &amp; Stress — the CPU / GPU cards: a dial with the temperature, three charts (temperature · load · frequency)
    /// and the footer with 4 facts. The frequency is on a chart so it is immediately visible whether the processor slows down
    /// (throttling): the load stays high, the frequency drops.
    /// Called from OnTempTick on every sensor read, with the same snapshot that fills the "All sensors" table. The charts have
    /// one point per second and 60 minutes of history (<see cref="_stHist"/>), written on every read whatever page is shown;
    /// they are drawn from the history only while the page is visible.
    /// </summary>
    public partial class MainWindow
    {
        private readonly List<(float load, float clk)> _stCpuHist = new();
        private int _stThrottledSec;
        private float _stCpuClkEma;
        private bool _stHooked;

        // The chart history: temperature, load, frequency (GHz) for the CPU, then the same for the GPU;
        // then the markers (1 = yes, 0 = no): CPU stress, GPU stress, CPU throttling, GPU throttling.
        private readonly SecondHistory _stHist = new(10);
        private const int StSerCpuTest = 6, StSerGpuTest = 7, StSerCpuThrottle = 8, StSerGpuThrottle = 9;
        private static readonly string[] StChartNames =
            { "StCpuChartTemp", "StCpuChartLoad", "StCpuChartClock", "StGpuChartTemp", "StGpuChartLoad", "StGpuChartClock" };

        /// <summary>Draws a chart from the history: the last <paramref name="cap"/> seconds of the series, with the lines under it
        /// for the test and throttling periods of the same component.</summary>
        private void StShowChart(string name, int series, int cap, double min, double max, double warn)
        {
            if (FindName(name) is not MiniLineChart c) return;
            c.Min = min; c.Max = max; c.WarnLine = warn;
            c.WarnText = name.EndsWith("Temp", StringComparison.Ordinal) && double.IsFinite(warn) ? $"{TempUnit.Fmt(warn, 0, true)} limit" : null;
            bool gpu = series >= 3;
            c.ShowMarksA = SettingsService.Current.StressChartShowTests;
            c.ShowMarksB = SettingsService.Current.StressChartShowThrottle;
            c.TintMarksA = SettingsService.Current.StressChartMarkStyle != "Line";
            c.ShowHistory(_stHist, cap, series, -1, gpu ? StSerGpuTest : StSerCpuTest, gpu ? StSerGpuThrottle : StSerCpuThrottle);
        }

        private void StShowCpuCharts(int cap, double warn)
        {
            StShowChart("StCpuChartTemp", 0, cap, 20, 105, warn);
            StShowChart("StCpuChartLoad", 1, cap, 0, 100, double.NaN);
            StShowChart("StCpuChartClock", 2, cap, 0, double.NaN, double.NaN);
        }

        private void StShowGpuCharts(int cap, double warn)
        {
            StShowChart("StGpuChartTemp", 3, cap, 20, 105, warn);
            StShowChart("StGpuChartLoad", 4, cap, 0, 100, double.NaN);
            StShowChart("StGpuChartClock", 5, cap, 0, double.NaN, double.NaN);
        }

        /// <summary>Immediately redraws the card charts from the history (e.g. when the time window changes), without waiting for
        /// the next sensor read — on battery it may come only after 3 seconds.</summary>
        private void RedrawStressChartsNow()
        {
            try
            {
                if (_stHist.Count == 0) return;
                int cap = Math.Clamp(StressChartPoints, 30, 3600);
                StShowCpuCharts(cap, SettingsService.Current.TempWarnCpu > 0 ? SettingsService.Current.TempWarnCpu : 90f);
                StShowGpuCharts(cap, SettingsService.Current.TempWarnGpu > 0 ? SettingsService.Current.TempWarnGpu : 90f);
            }
            catch (Exception ex) { AppLogger.Debug($"RedrawStressChartsNow: {ex.Message}"); }
        }

        /// <summary>Once: the scale label of the frequency charts and the right-click menu (the lines under the charts: test
        /// period / throttling).</summary>
        private void SetupStressCharts()
        {
            foreach (string clock in new[] { "StCpuChartClock", "StGpuChartClock" })
                if (FindName(clock) is MiniLineChart c)
                {
                    c.NiceMax = peak => Math.Max(1, Math.Ceiling(peak * 2) / 2);   // in half-GHz steps
                    c.ScaleText = v => $"{v:0.#} GHz";
                }

            // right click on a chart: how the test period is shown (red curve / line underneath / not at all) and the throttling line
            var menu = new System.Windows.Controls.ContextMenu();
            var head = new System.Windows.Controls.MenuItem { IsEnabled = false, FontWeight = FontWeights.SemiBold, Header = LanguageService.S("Stress", "ChartMarkHeader", "Stress test on the chart:") };
            var tint = new System.Windows.Controls.MenuItem { IsCheckable = true, Header = LanguageService.S("Stress", "ChartMarkTint", "Red curve while the test runs") };
            var lineItem = new System.Windows.Controls.MenuItem { IsCheckable = true, Header = LanguageService.S("Stress", "ChartMarkLine", "Line under the chart") };
            var none = new System.Windows.Controls.MenuItem { IsCheckable = true, Header = LanguageService.S("Stress", "ChartMarkNone", "Not marked") };
            var throttle = new System.Windows.Controls.MenuItem { IsCheckable = true, Header = LanguageService.S("Stress", "ChartShowThrottle", "Line under the chart: throttling") };
            void SyncMenu()
            {
                bool show = SettingsService.Current.StressChartShowTests;
                bool asLine = SettingsService.Current.StressChartMarkStyle == "Line";
                tint.IsChecked = show && !asLine;
                lineItem.IsChecked = show && asLine;
                none.IsChecked = !show;
                throttle.IsChecked = SettingsService.Current.StressChartShowThrottle;
            }
            void Choose(bool show, string style)
            {
                SettingsService.Current.StressChartShowTests = show;
                if (show) SettingsService.Current.StressChartMarkStyle = style;
                SaveStressChartMarks();
                SyncMenu();
            }
            tint.Click += (_, _) => Choose(true, "Tint");
            lineItem.Click += (_, _) => Choose(true, "Line");
            none.Click += (_, _) => Choose(false, "");
            throttle.Click += (_, _) => { SettingsService.Current.StressChartShowThrottle = throttle.IsChecked; SaveStressChartMarks(); };
            menu.Items.Add(head); menu.Items.Add(tint); menu.Items.Add(lineItem); menu.Items.Add(none);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(throttle);
            menu.Opened += (_, _) => SyncMenu();
            string tip = LanguageService.S("Stress", "ChartMarksTip",
                "Red = a stress test was running, orange line = throttling. Right-click to change how they are shown.");
            foreach (string name in StChartNames)
                if (FindName(name) is MiniLineChart c) { c.ContextMenu = menu; c.ToolTip = tip; }
        }

        private void SaveStressChartMarks()
        {
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "SaveStressChartMarks"); }
            foreach (string name in StChartNames)
                if (FindName(name) is MiniLineChart c)
                {
                    c.ShowMarksA = SettingsService.Current.StressChartShowTests;
                    c.ShowMarksB = SettingsService.Current.StressChartShowThrottle;
                    c.TintMarksA = SettingsService.Current.StressChartMarkStyle != "Line";
                    c.InvalidateVisual();
                }
        }

        private void UpdateStressCards(TempSnapshot snap)
        {
            try
            {
                if (!_stHooked) { _stHooked = true; HideStressChartChecks(); SetupStressCharts(); }
                int cap = Math.Clamp(StressChartPoints, 30, 3600);
                float warnC = SettingsService.Current.TempWarnCpu > 0 ? SettingsService.Current.TempWarnCpu : 90f;
                float warnG = SettingsService.Current.TempWarnGpu > 0 ? SettingsService.Current.TempWarnGpu : 90f;
                var thr = _lastThrottle;
                bool throttled = thr?.IsThrottled == true;

                // ── CPU ──
                float? ct = snap.CpuTemp is float c0 && c0 > 0 ? c0 : null;
                float? cl = CpuLoadNow;
                // frequency = the average of the cores, smoothed (EMA) — as in Task Manager. The "max core clock" sensor jumps between
                // 0.8 and 3.6 GHz on every read (cores enter/leave idle).
                float? cfRaw = snap.CoreFreqs.Count > 0 && snap.CoreFreqs.Any(f => f > 100)
                    ? snap.CoreFreqs.Where(f => f > 100).Average()
                    : snap.CpuFreqMHz is float f0 && f0 > 0 ? f0 : null;
                if (cfRaw.HasValue) _stCpuClkEma = _stCpuClkEma <= 0 ? cfRaw.Value : _stCpuClkEma + 0.3f * (cfRaw.Value - _stCpuClkEma);
                float? cf = cfRaw.HasValue ? _stCpuClkEma : null;
                float? gt = snap.GpuTemp is float g0 && g0 > 0 ? g0 : null;
                float? gl = GpuLoadNow;
                float? gf = snap.GpuFreqMHz is float g1 && g1 > 0 ? g1 : null;

                // with background recording off, the time the window was hidden stays empty
                // "real" throttling: the usual power limit under load (Power Limit) is not marked
                bool cpuThrottleMark = snap.CpuThrottled
                    || (throttled && thr!.Reason != SMDWin.Services.ThrottleReason.PowerLimit);
                int stSeconds = _stHist.Push(DateTime.UtcNow,
                    new[]
                    {
                        ct ?? float.NaN, cl ?? float.NaN, cf / 1000f ?? float.NaN, gt ?? float.NaN, gl ?? float.NaN, gf / 1000f ?? float.NaN,
                        _cpuStress.Running ? 1f : 0f, _gpuStress.IsRunning ? 1f : 0f,
                        cpuThrottleMark ? 1f : 0f, snap.GpuThrottled ? 1f : 0f,
                    },
                    gap: IsUiHidden && BgRec == BgRecMode.Off, maxInterpolated: ChartInterpolateMaxSlots);
                // the throttling seconds: a read may cover several seconds (slow background rate)
                if (throttled) _stThrottledSec += Math.Clamp(stSeconds, 1, ChartInterpolateMaxSlots);
                bool stVisible = _uiVisibleCached && PanelStress?.Visibility == Visibility.Visible;

                StSetDial("StCpu", ct, warnC);
                if (stVisible) StShowCpuCharts(cap, warnC);
                StSetText("TxtStCpuTempHead", ct.HasValue ? $"{TempUnit.Fmt(ct, 0, true)}" : "—");
                StSetText("TxtStCpuLoadHead", cl.HasValue ? $"{cl:F0}%" : "—");
                StSetText("TxtStCpuClockHead", cf.HasValue ? $"{cf / 1000f:F2} GHz" : "—");
                if (FindName("StCpuChartTemp") is MiniLineChart tc && double.IsFinite(tc.Peak)) StSetText("TxtStCpuTempNote", $"{HighestWord()} {TempUnit.Fmt(tc.Peak, 0, true)}{ChartWindowLabel()}");
                if (FindName("StCpuChartLoad") is MiniLineChart lc && double.IsFinite(lc.Peak)) StSetText("TxtStCpuLoadNote", $"{HighestWord()} {lc.Peak:F0}%{ChartWindowLabel()}");

                // the minimum frequency while the load is high — the best sign of throttling
                if (cl.HasValue && cf.HasValue)
                {
                    _stCpuHist.Add((cl.Value, cf.Value));
                    while (_stCpuHist.Count > cap) _stCpuHist.RemoveAt(0);
                }
                var busy = _stCpuHist.Where(h => h.load >= 80).ToList();
                float maxClk = _stCpuHist.Count > 0 ? _stCpuHist.Max(h => h.clk) : 0;
                // short ("under load 1.90–2.40 GHz"): the long sentence was cut on every laptop; the full one is in the tooltip
                string loMin = busy.Count >= 3 ? (busy.Min(h => h.clk) / 1000f).ToString("F2") : "", loMax = (maxClk / 1000f).ToString("F2");
                StSetText("TxtStCpuClockNote", busy.Count >= 3 ? PT("under load {0}–{1} GHz", loMin, loMax)
                                              : maxClk > 0 ? $"{HighestWord()} {loMax} GHz" : "");
                if (FindName("TxtStCpuClockNote") is TextBlock clkNote)
                    clkNote.ToolTip = busy.Count >= 3 ? PT("min under load {0} · max {1} GHz", loMin, loMax) : null;

                // the displayed frequency = the average of the cores (smoothed, like Task Manager); in the tooltip: what the sensors and
                // the Windows counter give
                if (FindName("TxtStCpuClockHead") is TextBlock clkHead)
                {
                    var tip = new System.Text.StringBuilder();
                    var cores = snap.CoreFreqs.Where(f => f > 100).ToList();
                    if (cores.Count > 0)
                        tip.AppendFormat(_LS("Stress", "ClockTipCores"), (cores.Average() / 1000f).ToString("F2"), (cores.Max() / 1000f).ToString("F2"));
                    if (thr != null && thr.HasFrequency && thr.FreqMHz > 0)
                    {
                        if (tip.Length > 0) tip.Append('\n');
                        tip.AppendFormat(_LS("Stress", "ClockTipWindows"), (thr.FreqMHz / 1000f).ToString("F2"));
                    }
                    clkHead.ToolTip = tip.Length > 0 ? tip.ToString() : null;
                }

                StSetText("TxtStCpuPower", snap.CpuPowerW is float pw && pw > 0 ? $"{pw:F1} W" : "—");
                StSetText("TxtStCpuVCore", snap.CpuVCoreV is float vc && vc > 0 ? $"{vc:F3} V" : "—");
                if (snap.CoreTemps.Count > 0)
                {
                    int hi = 0; for (int i = 1; i < snap.CoreTemps.Count; i++) if (snap.CoreTemps[i] > snap.CoreTemps[hi]) hi = i;
                    StSetText("TxtStCpuHotKey", "Hottest core");
                    StSetText("TxtStCpuHot", $"{TempUnit.Fmt(snap.CoreTemps[hi], 0, true)} · #{hi + 1}");
                }
                else { StSetText("TxtStCpuHotKey", "Cores"); StSetText("TxtStCpuHot", PT("{0} threads", Environment.ProcessorCount)); }
                if (FindName("TxtStCpuThrottle") is TextBlock tt)
                {
                    // under load, the frequency dropped below 85% of the maximum seen: a power limit (normal on laptops) or a temperature limit
                    string? limitKind = null;
                    if (!throttled && cl >= 80 && cf.HasValue && maxClk > 0 && busy.Count >= 5 && cf.Value < maxClk * 0.85f)
                    {
                        float tj = _throttleDetector.TjMaxC;
                        bool hot = ct.HasValue && (ct.Value >= tj - 5f || ct.Value >= warnC);
                        limitKind = hot ? "Temp" : "Power";
                    }
                    if (throttled)
                    {
                        tt.Text = string.IsNullOrWhiteSpace(thr!.ReasonLabel) ? PT("now") : thr.ReasonLabel;
                        tt.ToolTip = string.IsNullOrWhiteSpace(thr.ReasonDetail) ? null : thr.ReasonDetail;
                        tt.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarnBrush");
                    }
                    else if (limitKind != null)
                    {
                        string from = (maxClk / 1000f).ToString("F2"), to = (cf!.Value / 1000f).ToString("F2");
                        tt.Text = _LS("Stress", limitKind == "Temp" ? "LimitTemp" : "LimitPower");
                        tt.ToolTip = limitKind == "Temp"
                            ? string.Format(_LS("Stress", "LimitTempTip"), from, to, TempUnit.Fmt(ct!.Value, 0, true))
                            : string.Format(_LS("Stress", "LimitPowerTip"), from, to);
                        tt.SetResourceReference(TextBlock.ForegroundProperty, limitKind == "Temp" ? "StatusWarnBrush" : "StatusInfoBrush");
                    }
                    else
                    {
                        // with Turbo off in the power plan the clock stays at the base speed and "none" made the processor
                        // look slow by itself (i5-10210U held at 1.61 GHz)
                        bool turboOff = _stThrottledSec == 0 && TurboOffCached();
                        tt.Text = _stThrottledSec > 0 ? PT("{0} s this session", _stThrottledSec) : turboOff ? PT("Turbo off (power plan)") : PT("none");
                        tt.ToolTip = turboOff ? PT("Turbo is off in the power plan (Power › Processor): the processor stays at its base speed.") : null;
                        tt.SetResourceReference(TextBlock.ForegroundProperty, _stThrottledSec > 0 || turboOff ? "StatusWarnBrush" : "TextPrimaryBrush");   // colour only when it needs attention
                    }
                }
                StSetStatus("TxtStCpuStatus", ct, warnC, throttled, cl, _cpuStress.Running);

                // ── GPU ──
                StSetDial("StGpu", gt, warnG);
                if (stVisible) StShowGpuCharts(cap, warnG);
                StSetText("TxtStGpuTempHead", gt.HasValue ? $"{TempUnit.Fmt(gt, 0, true)}" : "—");
                StSetText("TxtStGpuLoadHead", gl.HasValue ? $"{gl:F0}%" : "—");
                StSetText("TxtStGpuClockHead", gf.HasValue ? $"{gf / 1000f:F2} GHz" : "—");
                if (FindName("StGpuChartTemp") is MiniLineChart gtc && double.IsFinite(gtc.Peak)) StSetText("TxtStGpuTempNote", $"{HighestWord()} {TempUnit.Fmt(gtc.Peak, 0, true)}{ChartWindowLabel()}");
                if (FindName("StGpuChartLoad") is MiniLineChart glc && double.IsFinite(glc.Peak)) StSetText("TxtStGpuLoadNote", $"{HighestWord()} {glc.Peak:F0}%{ChartWindowLabel()}");
                if (FindName("StGpuChartClock") is MiniLineChart gcc && double.IsFinite(gcc.Peak)) StSetText("TxtStGpuClockNote", $"{HighestWord()} {gcc.Peak:F2} GHz{ChartWindowLabel()}");
                else if (!gf.HasValue) StSetText("TxtStGpuClockNote", "Not reported by this GPU");   // e.g. Intel integrated graphics
                // a chart with no data says so, instead of "Collecting data…" forever
                if (FindName("StGpuChartClock") is MiniLineChart gccE)
                {
                    string? empty = gf.HasValue ? null : PT("Not reported by this GPU");
                    if (gccE.EmptyText != empty) { gccE.EmptyText = empty; gccE.InvalidateVisual(); }
                }
                if (!gl.HasValue) StSetText("TxtStGpuLoadNote", "Not reported by this GPU");
                StSetText("TxtStGpuVram", snap.GpuMemUsedMB is float vr && vr > 0 ? $"{vr:F0} MB" : "—");
                // integrated graphics have no memory of their own: "shared" instead of an empty "—" (Intel UHD)
                StSetText("TxtStGpuMemClk", snap.GpuMemFreqMHz is float mc && mc > 0 ? $"{mc:F0} MHz"
                                          : GpuPackage.IsIntegrated(_summary?.GpuName) ? PT("shared with the system") : "—");
                if (snap.GpuPowerW is float gp && gp > 0) { StSetText("TxtStGpuPowerKey", "Power"); StSetText("TxtStGpuPower", $"{gp:F1} W"); }
                else if (!string.IsNullOrWhiteSpace(_summary?.GpuDriverVersion)) { StSetText("TxtStGpuPowerKey", "Driver"); StSetText("TxtStGpuPower", SMDWin.Services.GpuPackage.DriverText(_summary!.GpuName ?? "", _summary.GpuDriverVersion)); }
                else { StSetText("TxtStGpuPowerKey", "Power"); StSetText("TxtStGpuPower", "—"); }
                if (snap.GpuHotSpot is float hs && hs > 0) { StSetText("TxtStGpuHotKey", "Hot spot"); StSetText("TxtStGpuHot", $"{TempUnit.Fmt(hs, 0, true)}"); }
                else if (FindName("StGpuChartLoad") is MiniLineChart gl2 && double.IsFinite(gl2.Peak)) { StSetText("TxtStGpuHotKey", "Max load"); StSetText("TxtStGpuHot", $"{gl2.Peak:F0}%"); }
                else { StSetText("TxtStGpuHotKey", "Hot spot"); StSetText("TxtStGpuHot", "—"); }
                StSetStatus("TxtStGpuStatus", gt, warnG, false, gl, _gpuStress.IsRunning);
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateStressCards: {ex.Message}"); }
        }

        /// <summary>The "Top processes" card (always visible): 8 processes by CPU, with block bars.</summary>
        private void UpdateStressTopList(List<SMDWin.Models.GroupedProcessEntry> grouped)
        {
            try
            {
                if (FindName("StressTopList") is not StackPanel host) return;
                host.Children.Clear();
                var top = grouped.OrderByDescending(g => g.TotalCpuPct).Take(8).ToList();
                foreach (var p in top)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                    string exe = string.IsNullOrWhiteSpace(p.ExeName) ? p.DisplayName : p.ExeName;
                    if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
                    var n = RamTxt(exe + (p.InstanceCount > 1 ? $" · {p.InstanceCount}" : ""), Ui.FontM, "TextPrimaryBrush");
                    n.VerticalAlignment = VerticalAlignment.Center; n.Margin = new Thickness(0, 0, 10, 0); n.ToolTip = p.DisplayName;
                    row.Children.Add(n);
                    float pct = Math.Clamp(p.TotalCpuPct, 0, 100);
                    var bar = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 12, Height = 7, VerticalAlignment = VerticalAlignment.Center };
                    int lit = pct <= 0.5f ? 0 : Math.Clamp((int)Math.Round(pct / 100 * 12), 1, 12);
                    for (int i = 0; i < 12; i++)
                    {
                        var b = new Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, i < 11 ? 2 : 0, 0) };
                        b.SetResourceReference(Border.BackgroundProperty, i < lit ? "DataBrush" : "BorderBrush2");
                        bar.Children.Add(b);
                    }
                    Grid.SetColumn(bar, 1); row.Children.Add(bar);
                    var v = new TextBlock
                    {
                        Text = $"{pct:F0}%", FontFamily = TryFindResource("NumFont") as System.Windows.Media.FontFamily,
                        FontSize = 15, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    };
                    v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    Grid.SetColumn(v, 2); row.Children.Add(v);
                    host.Children.Add(row);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateStressTopList: {ex.Message}"); }
        }

        private void StSetDial(string prefix, float? temp, float warn)
        {
            if (FindName(prefix + "Gauge") is PercentGauge g)
            {
                g.WarnAt = Math.Max(40, warn - 10); g.CritAt = warn;   // the dial scale: 0–100 °C
                g.HasValue = temp.HasValue; g.Value = temp ?? 0;
            }
            if (FindName("Txt" + prefix + "Temp") is TextBlock t)
            {
                t.Text = temp.HasValue ? $"{TempUnit.Convert(temp.Value):F0}" : "—";
                // the figure blends smoothly into orange / red when the temperature passes the limits
                ColorFx.Foreground(t, temp >= warn ? "StatusCritBrush" : temp >= warn - 10 ? "StatusWarnBrush" : "TextPrimaryBrush");
            }
        }

        private void StSetText(string name, string text)
        {
            if (FindName(name) is not TextBlock t) return;
            text = PT(text);   // fixed labels ("Hottest core", "Max load") — values have no key and stay as they are
            t.Text = text;
            // the notes sit in the chart header, on a single line: the full text stays in the tooltip
            if (name.EndsWith("Note", StringComparison.Ordinal)) t.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        }

        private void StSetStatus(string name, float? temp, float warn, bool throttled, float? load, bool underTest = false)
        {
            if (FindName(name) is not TextBlock t) return;
            (string txt, string key) = throttled ? (PT("Throttling"), "StatusWarnBrush")
                : temp >= warn ? (T4("Hot", "Fierbinte", "Heiß", "Chaud"), "StatusCritBrush")
                : temp >= warn - 10 ? (T4("Warm", "Cald", "Warm", "Tiède"), "StatusWarnBrush")
                : !temp.HasValue ? (T4("No sensor", "Fără senzor", "Kein Sensor", "Pas de capteur"), "TextSecondaryBrush")
                // never "Idle" while its stress test runs (the load sensor lags a few seconds behind, and a laptop graphics chip
                // that was asleep reads 0 % at first)
                : !underTest && load is float l && l < 10 ? (T4("Idle", "Inactiv", "Leerlauf", "Inactif"), "AccentBrush")
                : ("OK", "StatusOkBrush");
            t.Text = txt;
            t.SetResourceReference(TextBlock.ForegroundProperty, key);
        }

        /// <summary>The page has the window height (minimum 640): CPU and GPU share the height, the sensor table scrolls in its
        /// card. On very small screens the whole page scrolls.</summary>
        internal void StressScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ScrollViewer sv && FindName("StressLayoutGrid") is Grid g)
                g.Height = Math.Max(640, sv.ActualHeight - 8);
        }

        /// <summary>The chart window, stated next to the maximum ("max 55 °C · 1 min"): the chart maximum is not the session
        /// maximum from the table.</summary>
        /// <summary>"highest" (the chart maximum), the same word as on the Dashboard.</summary>
        private static string HighestWord() => T4("highest", "maxim", "Spitze", "maximum");

        private string ChartWindowLabel() => _stressChartRangeSeconds switch
        {
            >= 3600 => " · 1 h",
            >= 900  => " · 15 min",
            >= 300  => " · 5 min",
            _       => " · 1 min",
        };

        /// <summary>The toolbar always stays on one row: below 1300 px the test buttons and Reset / PDF keep only their icon
        /// (the tooltips say what they do).</summary>
        internal void StressToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateStressPlanCompact(e.NewSize.Width);   // the test bar (short labels on narrow windows)
            var vis = e.NewSize.Width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var name in new[] { "TxtBtnStressCpu", "TxtBtnStressGpu", "TxtBtnMaxLoad", "TxtBtnEnduranceMode", "TxtBtnResetChart", "TxtBtnExportPdf" })
                if (FindName(name) is TextBlock t && t.Visibility != vis) t.Visibility = vis;
        }

        /// <summary>The "All sensors" table on two columns (CPU | GPU + system) when the card is wide enough, otherwise a single
        /// column — so large screens need no scrolling.</summary>
        internal void SensorGroupsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid g || FindName("SensorColRight") is not FrameworkElement right) return;
            bool two = e.NewSize.Width >= 900;
            if (two == (Grid.GetColumn(right) == 2)) return;
            g.ColumnDefinitions[1].Width = new GridLength(two ? 16 : 0);
            g.ColumnDefinitions[2].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(right, two ? 2 : 0);
            Grid.SetRow(right, two ? 0 : 1);
            if (FindName("SensorHeaderRight") is FrameworkElement h) h.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The bottom gradient appears only while the "All sensors" table still has rows hidden below the visible edge.</summary>
        internal void SensorScroll_Changed(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv || FindName("SensorScrollFade") is not FrameworkElement fade) return;
            bool more = sv.ScrollableHeight > 1 && sv.VerticalOffset < sv.ScrollableHeight - 1;
            var vis = more ? Visibility.Visible : Visibility.Collapsed;
            if (fade.Visibility != vis) fade.Visibility = vis;
        }

        /// <summary>Clicking a group title in "All sensors" collapses / expands it.</summary>
        internal void SensorGroup_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string name || FindName(name) is not FrameworkElement grp) return;
            grp.Visibility = grp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            fe.Opacity = grp.Visibility == Visibility.Visible ? 1 : 0.7;
        }

        /// <summary>The checkboxes in the table chose what appeared in the old charts (now hidden) — hide them so they do not
        /// seem to do something. The column stays as alignment space.</summary>
        private void HideStressChartChecks()
        {
            try
            {
                if (FindName("SensorTablePanel") is not DependencyObject root) return;
                var stack = new Stack<DependencyObject>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    if (d is Border b && b.Name.StartsWith("Chk", StringComparison.Ordinal)) b.Visibility = Visibility.Hidden;
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"HideStressChartChecks: {ex.Message}"); }
        }
    }
}
