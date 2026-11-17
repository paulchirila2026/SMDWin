using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SMDWin.Services;

namespace SMDWin.Views
{
    public partial class MainWindow : Window
    {

        // The order follows the sidebar. "Settings" and "Events" have no explicit load in NavigateTo — they are tested anyway,
        // only as "opens without an exception".
        private static readonly string[] _selfTestPages =
        {
            "Dashboard", "Stress", "Drivers", "Disk", "Ram", "Network",
            "Apps", "Services", "Processes", "Startup", "Battery",
            "Crash", "Events", "Tools", "PowerShell", "Settings",
        };

        /// <summary>
        /// Walks all the sidebar pages (real navigation, real code) and checks: (a) no exception on navigation, (b) load time,
        /// (c) how many rows/elements were populated in the page (generic, through reflection on the visual tree — no fixed
        /// grid names assumed).
        /// </summary>
        public async Task RunSelfTestPageSweepAsync(Action<string> log, CancellationToken ct)
        {
            log("Page sweep (real navigation, real code behind the buttons):");
            foreach (var page in _selfTestPages)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try
                {
                    await NavigateTo(page);
                    // Let the UI settle (rendering, possible async bindings) before counting elements — otherwise we read 0 from grids
                    // that are still populating at a lower DispatcherPriority.
                    await Task.Delay(400, ct);
                    sw.Stop();

                    int items = CountItemsInPanel(page);
                    string itemsTxt = items >= 0 ? $"{items} rows/items" : "(no grid/list to count)";
                    double elapsedMs = sw.ElapsedMilliseconds;
                    var pastTimes = PageLoadHistoryService.GetHistoryFor(page);
                    var regression = PageLoadRegressionChecker.Check(pastTimes, elapsedMs);
                    PageLoadHistoryService.SaveResult(page, elapsedMs);

                    bool isSlow = regression.HasEnoughHistory
                        ? regression.IsRegression
                        : elapsedMs > 2000;
                    string slowDetail = regression.HasEnoughHistory
                        ? $"{elapsedMs:F0}ms is {(elapsedMs / Math.Max(1, regression.BaselineMedianMs)):F1}x the usual time of this page ({regression.BaselineMedianMs:F0}ms, from {pastTimes.Count} earlier runs on this machine)"
                        : $"{elapsedMs:F0}ms is well above the ~400-800ms typical for other pages (not enough history yet for a per-page baseline — fixed limit used)";
                    string zeroHint = (items == 0 && page == "Stress")
                        ? " (if this was right after app launch, sensors may not have finished initializing yet — check section 5/7 LHM vs APP SENSORS below; if that also shows 0, it's likely real)"
                        : "";
                    string flag = (items == 0) ? $"  ⚠ ZERO data displayed — possible issue{zeroHint}"
                                : isSlow ? $"  ⚠ SLOW — {slowDetail}"
                                : "";
                    log($"  {(items == 0 || isSlow ? "⚠" : "✔")} {page,-12} — {sw.ElapsedMilliseconds,5} ms — {itemsTxt}{flag}");

                    var zeroSize = FindZeroSizeVisibleElements(page);
                    if (zeroSize.Count > 0)
                    {
                        log($"      ⚠ Zero-size visible elements on {page}: {zeroSize.Count}");
                        foreach (var name in zeroSize.Take(10)) log($"          - {name}");
                        if (zeroSize.Count > 10) log($"          … and {zeroSize.Count - 10} more");
                    }

                    if (_navPanels != null && _navPanels.TryGetValue(page, out var btnRoot) && btnRoot != null)
                    {
                        var lowContrast = ButtonContrastGuard.FindLowContrast(btnRoot);
                        if (lowContrast.Count > 0)
                        {
                            log($"      ⚠ Buttons with unreadable text (low contrast) on {page}: {lowContrast.Count}");
                            foreach (var t in lowContrast.Take(10)) log($"          - {t}");
                        }
                    }

                    var cut = FindTruncatedTextWithoutTooltip(page);
                    if (cut.Count > 0)
                    {
                        log($"      ⚠ Text cut with \"…\" and no tooltip on {page}: {cut.Count}");
                        foreach (var t in cut.Take(10)) log($"          - {t}");
                        if (cut.Count > 10) log($"          … and {cut.Count - 10} more");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log($"  ✘ {page,-12} — EXCEPTION during navigation: {ex.Message}");
                    AppLogger.Warning(ex, $"SelfTest.NavigateTo.{page}");
                }
            }

            // Fixed page texts that would stay in English in the current language (missing from the language pack)
            try
            {
                var untranslated = ListUntranslatedPageTexts();
                if (LanguageService.CurrentCode == "en")
                    log("  ✔ Page texts — language is English; the check for texts left in English runs in the other languages");
                else if (untranslated.Count == 0)
                    log($"  ✔ Page texts — all fixed texts have a translation in '{LanguageService.CurrentCode}'");
                else
                {
                    log($"  ⚠ Page texts without a translation in '{LanguageService.CurrentCode}': {untranslated.Count}");
                    foreach (var text in untranslated.Take(25)) log($"          - {text}");
                    if (untranslated.Count > 25) log($"          … and {untranslated.Count - 25} more");
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SelfTest.PageTexts"); }

            // Return to the Dashboard at the end, a clean state for the user.
            try { await NavigateTo("Dashboard"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        /// <summary>
        /// Texts cut with "…" (TextTrimming) whose full value is not visible anywhere: no tooltip on the text or on a close parent.
        /// Compares the real text width with its space; a text that fits does not count.
        /// </summary>
        private List<string> FindTruncatedTextWithoutTooltip(string panel)
        {
            var result = new List<string>();
            if (_navPanels == null || !_navPanels.TryGetValue(panel, out var root) || root == null)
                return result;
            foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
            {
                if (!tb.IsVisible || tb.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(tb.Text)) continue;
                if (tb.ActualWidth <= 0 || tb.TextWrapping != TextWrapping.NoWrap) continue;
                var ft = new System.Windows.Media.FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture,
                    tb.FlowDirection, new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                    tb.FontSize, System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(tb).PixelsPerDip);
                if (ft.WidthIncludingTrailingWhitespace <= tb.ActualWidth + 0.5) continue;   // incape
                bool hasTip = false;
                DependencyObject? node = tb;
                for (int depth = 0; node != null && depth < 5 && !hasTip; depth++)
                {
                    if (node is FrameworkElement fe && fe.ToolTip != null) hasTip = true;
                    node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                }
                if (hasTip) continue;
                string text = tb.Text.Length > 40 ? tb.Text[..40] + "…" : tb.Text;
                result.Add($"\"{text}\"" + (string.IsNullOrEmpty(tb.Name) ? "" : $" ({tb.Name})"));
            }
            return result;
        }

        private List<string> FindZeroSizeVisibleElements(string panel)
        {
            var result = new List<string>();
            if (_navPanels == null || !_navPanels.TryGetValue(panel, out var root) || root == null)
                return result;
            foreach (var fe in FindVisualChildren<System.Windows.FrameworkElement>(root))
            {
                if (!fe.IsVisible) continue;
                if (string.IsNullOrEmpty(fe.Name)) continue;
                if (fe.Name == "contentPresenter" || fe.Name == "ContentSite" || fe.Name == "PART_ContentPresenter")
                    continue;
                // A TextBlock with empty Text LEGITIMATELY has ActualWidth=0 — not a layout bug, just a label not yet populated at the
                // time of the check (e.g. a "Min-Max" field filled after the first sensor tick, not guaranteed to have happened within
                // the 400 ms wait of the page sweep).
                if (fe is System.Windows.Controls.TextBlock tb && string.IsNullOrEmpty(tb.Text))
                    continue;
                // Only elements that should already be rendered (past the layout pass) — an element with an explicit Width/Height of 0
                // set on purpose in XAML (compatibility stubs, see FanRowsContainer etc.) is not a bug; only ActualWidth/Height = 0
                // WHEN the element has no explicit size forced to 0 in XAML matters.
                bool explicitZero = (fe.Width == 0) || (fe.Height == 0);
                if (explicitZero) continue;
                // A container without any element (the fan list on a PC without speed sensors, an empty "needs attention" list)
                // legitimately has height 0; so does a level bar at 0% (e.g. battery 0%).
                // the icon of an app not loaded yet (or without an icon): the square with the initial is shown instead
                if (fe is System.Windows.Controls.Image noPicture && noPicture.Source == null) continue;
                if (fe is System.Windows.Controls.Panel emptyPanel && emptyPanel.Children.Count == 0) continue;
                if (fe is System.Windows.Controls.ItemsControl emptyList && emptyList.Items.Count == 0) continue;
                if (fe.ActualHeight > 0 && fe.Name.EndsWith("Fill", StringComparison.Ordinal)) continue;
                if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                    result.Add($"{fe.GetType().Name} \"{fe.Name}\" ({fe.ActualWidth:F0}x{fe.ActualHeight:F0})");
            }
            return result;
        }

        /// <summary>
        /// Generically counts the populated elements in a navigation panel (a DataGrid or another ItemsControl inside the panel),
        /// without assuming fixed control names — works whatever the grid is called in each page's XAML. Returns -1 if the panel
        /// does not exist or contains no ItemsControl (pages like Tools/Settings, which are forms rather than lists).
        /// </summary>
        private int CountItemsInPanel(string panel)
        {
            if (_navPanels == null || !_navPanels.TryGetValue(panel, out var root) || root == null)
                return -1;

            if (panel == "Drivers")
            {
                int basicCount = DriversBasicList?.Children.Count ?? 0;
                int advCount   = DriversGrid?.Items.Count ?? 0;
                return _driverViewMode == "Basic" ? basicCount : advCount;
            }

            int total = 0;
            bool found = false;
            foreach (var dg in FindVisualChildren<System.Windows.Controls.DataGrid>(root))
            {
                if (dg.Visibility != Visibility.Visible) continue;
                total += dg.Items.Count;
                found = true;
            }
            foreach (var ic in FindVisualChildren<System.Windows.Controls.ItemsControl>(root))
            {
                if (ic is System.Windows.Controls.DataGrid) continue; // already counted above
                if (ic.Visibility != Visibility.Visible) continue;
                total += ic.Items.Count;
                found = true;
            }
            return found ? total : -1;
        }

        /// <summary>
        /// CPU stress using the app's real _cpuStress instance — checks not only that it "started", but that the load
        /// (CpuLoadPct, read from the same TempReader/LHM the Dashboard uses) actually gets close to 100%.
        /// </summary>
        public async Task RunSelfTestCpuStressAsync(Action<string> log, int seconds, CancellationToken ct)
        {
            log($"CPU Stress — {seconds}s, all cores ({Environment.ProcessorCount}):");
            double maxLoad = 0, sumLoad = 0; int samples = 0;
            double maxUiLagMs = 0, sumUiLagMs = 0; int uiSamples = 0;
            try
            {
                _cpuStress.Start(Environment.ProcessorCount);
                if (_tempReader != null) _tempReader.StressActive = true;
                try { SetNavTaskBadge("TaskBadgeStress", true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                for (int s = 0; s < seconds; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(1000, ct);

                    var uiSw = Stopwatch.StartNew();
                    try
                    {
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                        uiSw.Stop();
                        double lagMs = uiSw.Elapsed.TotalMilliseconds;
                        maxUiLagMs = Math.Max(maxUiLagMs, lagMs);
                        sumUiLagMs += lagMs; uiSamples++;
                    }
                    catch (Exception exUi) { log($"      t+{s + 1,2}s   UI ping failed: {exUi.Message}"); }

                    try
                    {
                        var snap = await Task.Run(() => _tempReader?.Read(), ct);
                        float? load = snap?.CpuLoadPct;
                        if (load.HasValue && load.Value > 0)
                        {
                            maxLoad = Math.Max(maxLoad, load.Value);
                            sumLoad += load.Value;
                            samples++;
                            log($"      t+{s + 1,2}s   CPU load: {load.Value,5:F0}%   running={_cpuStress.Running}   UI ping: {uiSw.Elapsed.TotalMilliseconds,5:F0}ms");
                        }
                        else
                        {
                            log($"      t+{s + 1,2}s   CPU load: N/A (load sensor unavailable)   running={_cpuStress.Running}   UI ping: {uiSw.Elapsed.TotalMilliseconds,5:F0}ms");
                        }
                    }
                    catch (Exception ex) { log($"      t+{s + 1,2}s   load read failed: {ex.Message}"); }
                }
            }
            finally
            {
                try { _cpuStress.Stop(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (_tempReader != null) _tempReader.StressActive = false;
                try { SetNavTaskBadge("TaskBadgeStress", _cpuStress.Running || _gpuStress.IsRunning); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            if (samples == 0)
                log("  ⚠ Could not read CPU load percentage (sensor unavailable) — stress ran, but load % could not be confirmed.");
            else
            {
                double avg = sumLoad / samples;
                string verdict = maxLoad >= 90 ? "✔" : "⚠";
                log($"  {verdict} CPU load — max {maxLoad:F0}%, avg {avg:F0}%" +
                    (maxLoad < 90 ? "  (below 90% — check whether CpuStressor uses all cores on this laptop)" : ""));
            }

            if (uiSamples > 0)
            {
                double avgUi = sumUiLagMs / uiSamples;
                // Threshold: below 200 ms a user barely notices the delay; above 1000 ms (a whole second just for the UI to respond to
                // a button press) is exactly the "freezes" feeling.
                string uiVerdict = maxUiLagMs < 200 ? "✔" : maxUiLagMs < 1000 ? "⚠" : "✘";
                log($"  {uiVerdict} UI responsiveness during stress — max lag {maxUiLagMs:F0}ms, avg {avgUi:F0}ms" +
                    (maxUiLagMs >= 1000 ? "  (UI was genuinely unresponsive for over 1s — matches \"freeze\" reports)" : ""));
            }
        }

        /// <summary>
        /// GPU stress using the real _gpuStress instance. As with the CPU, checks the load percentage (GpuLoadPct) where the
        /// graphics card exposes it — many integrated/laptop GPUs do not expose load% through LHM, in which case DispatchHz
        /// (proof the shader is actually running) is reported as a secondary signal.
        /// </summary>
        /// <summary>
        /// Waits ~1 second and measures during that time, every 100 ms, how long the UI thread takes to respond
        /// (an empty operation, at Background priority). Returns the largest delay, in ms.
        /// </summary>
        private async Task<double> MeasureUiLagForOneSecondAsync(CancellationToken ct)
        {
            double worst = 0;
            var total = Stopwatch.StartNew();
            while (total.ElapsedMilliseconds < 1000)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try { await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                await Task.Delay(90, ct);
            }
            return worst;
        }

        public async Task RunSelfTestGpuStressAsync(Action<string> log, int seconds, CancellationToken ct)
        {
            log($"GPU Stress — {seconds}s:");
            double maxLoad = 0, sumLoad = 0; int loadSamples = 0;
            double maxUiLagMs = 0;
            long maxDispatchHz = 0;
            try
            {
                _gpuStress.Start();
                if (!string.IsNullOrEmpty(_gpuStress.LastError))
                    log($"  ⚠ GpuStressService reported an error on start: {_gpuStress.LastError}");
                if (_tempReader != null) _tempReader.StressActive = true;
                try { SetNavTaskBadge("TaskBadgeStress", true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                for (int s = 0; s < seconds; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    double uiLag = await MeasureUiLagForOneSecondAsync(ct);
                    maxUiLagMs = Math.Max(maxUiLagMs, uiLag);
                    maxDispatchHz = Math.Max(maxDispatchHz, _gpuStress.DispatchHz);
                    try
                    {
                        var snap = await Task.Run(() => _tempReader?.Read(), ct);
                        float? load = snap?.GpuLoadPct;
                        if (load.HasValue && load.Value > 0)
                        {
                            maxLoad = Math.Max(maxLoad, load.Value);
                            sumLoad += load.Value;
                            loadSamples++;
                            log($"      t+{s + 1,2}s   GPU load: {load.Value,5:F0}%   dispatchHz={_gpuStress.DispatchHz}   running={_gpuStress.IsRunning}   UI lag (worst of 10): {uiLag,5:F0}ms");
                        }
                        else
                        {
                            log($"      t+{s + 1,2}s   GPU load: N/A   dispatchHz={_gpuStress.DispatchHz}   running={_gpuStress.IsRunning}   UI lag (worst of 10): {uiLag,5:F0}ms");
                        }
                    }
                    catch (Exception ex) { log($"      t+{s + 1,2}s   load read failed: {ex.Message}"); }
                }
            }
            finally
            {
                try { _gpuStress.Stop(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (_tempReader != null) _tempReader.StressActive = false;
                try { SetNavTaskBadge("TaskBadgeStress", _cpuStress.Running || _gpuStress.IsRunning); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            if (maxDispatchHz <= 0)
                log("  ✘ GPU stress did NOT actually run — DispatchHz stayed at 0 for the whole test");
            else if (loadSamples == 0)
                log($"  ⚠ GPU stress ran (DispatchHz max {maxDispatchHz}), but the GPU doesn't expose load% through sensors — normal on some iGPUs.");
            else
            {
                double avg = sumLoad / loadSamples;
                string verdict = maxLoad >= 80 ? "✔" : "⚠";
                log($"  {verdict} GPU load — max {maxLoad:F0}%, avg {avg:F0}%   (DispatchHz max {maxDispatchHz})");
            }
            string uiVerdict = maxUiLagMs < 200 ? "✔" : maxUiLagMs < 1000 ? "⚠" : "✘";
            log($"  {uiVerdict} UI responsiveness during GPU stress — max lag {maxUiLagMs:F0}ms" +
                (maxUiLagMs >= 200 ? "  (the window stutters while the GPU is under stress)" : ""));
        }

        public async Task RunSelfTestTimerHeartbeatAsync(Action<string> log, CancellationToken ct)
        {
            log("Internal timers — state + real heartbeat:");

            var timers = new (string Name, System.Windows.Threading.DispatcherTimer Timer)[]
            {
                ("_tempTimer (sensors)",          _tempTimer),
                ("_procTimer (processes)",        _procTimer),
                ("_trafficTimer (network)",       _trafficTimer),
                ("_bgMonitorTimer (background)",  _bgMonitorTimer),
                ("_smoothRenderTimer (rendering)",_smoothRenderTimer),
                ("_clockTimer (clock)",           _clockTimer),
                ("_batTimer (battery)",           _batTimer),
            };

            foreach (var (name, timer) in timers)
            {
                ct.ThrowIfCancellationRequested();
                if (timer == null) { log($"  ✘ {name,-30} — instance is null"); continue; }
                if (!timer.IsEnabled)
                {
                    log($"  ⚠ {name,-30} — STOPPED (IsEnabled=false) — normal only if the related feature isn't in use right now");
                    continue;
                }

                // Real pulse — only for short-interval timers, so the test does not take whole minutes waiting for a long-interval timer.
                if (timer.Interval.TotalSeconds > 0 && timer.Interval.TotalSeconds <= 2)
                {
                    int ticks = 0;
                    void OnTick(object? s, EventArgs e) => ticks++;
                    timer.Tick += OnTick;
                    try { await Task.Delay(timer.Interval + TimeSpan.FromMilliseconds(600), ct); }
                    finally { timer.Tick -= OnTick; }

                    if (ticks > 0) log($"  ✔ {name,-30} — active, {ticks} tick(s) in {timer.Interval.TotalSeconds + 0.6:F1}s expected");
                    else log($"  ✘ {name,-30} — IsEnabled=true but NO Tick fired — possibly stuck/frozen");
                }
                else
                {
                    log($"  ✔ {name,-30} — active (interval {timer.Interval.TotalSeconds:F0}s, too long for a heartbeat check in this test)");
                }
            }
        }

        public async Task RunSelfTestDashboardContinuityAsync(Action<string> log, CancellationToken ct)
        {
            log("Dashboard chart continuity (navigate away, wait, come back):");
            try
            {
                await NavigateTo("Dashboard");
                await Task.Delay(500, ct);
                int idxBefore = _chartIdx;
                int dbHeadBefore = _dbHead;

                await NavigateTo("Settings"); // page excluded from needFullRead
                var waitSec = 20;
                log($"      Away from Dashboard on Settings for {waitSec}s...");
                await Task.Delay(waitSec * 1000, ct);

                await NavigateTo("Dashboard");
                await Task.Delay(500, ct);
                int idxAfter = _chartIdx;
                int dbHeadAfter = _dbHead;

                int idxDelta = idxAfter - idxBefore;
                double ratio = idxDelta / (double)waitSec;
                string verdict = ratio >= 0.85 ? "✔" : ratio >= 0.5 ? "⚠" : "✘";
                log($"  {verdict} Stress buffer (_chartIdx) advanced {idxDelta} slots in {waitSec}s real time — ratio {ratio:F2} " +
                    "(should be ~1.00 if each slot = 1 real second — see S9-FIX)");

                int dbDelta = dbHeadAfter - dbHeadBefore;
                double dbRatio = dbDelta / (double)waitSec;
                string dbVerdict = dbRatio >= 0.85 ? "✔" : dbRatio >= 0.5 ? "⚠" : "✘";
                log($"  {dbVerdict} Dashboard's own buffer (_dbHead) advanced {dbDelta} slots in {waitSec}s real time — ratio {dbRatio:F2} " +
                    "(separate buffer from the one above — see S9b-FIX)");
            }
            catch (Exception ex) { log($"  ✘ Dashboard continuity check failed: {ex.Message}"); }
            finally { try { await NavigateTo("Dashboard"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }

            log("Chart range-switch data preservation (regression test for the S23-FIX bug class):");
            try
            {
                // Populate some real history before the test — without it, an empty buffer would "pass" the test by mistake (nothing to lose).
                await NavigateTo("Dashboard");
                await Task.Delay(3000, ct); // cateva secunde de date reale in _dbCpu

                int dbHeadPre = _dbHead;
                SettingsService.Current.ChartWindowSeconds = 300;
                await Task.Delay(100, ct);
                SettingsService.Current.ChartWindowSeconds = 60;
                await Task.Delay(100, ct);
                SettingsService.Current.ChartWindowSeconds = 300;
                await Task.Delay(100, ct);
                int dbHeadPost = _dbHead;

                // The real test: _dbHead must NOT have decreased or "jumped back" — if a future bug resizes the buffer destructively,
                // _dbHead would reset or become inconsistent with the real size of the kept data.
                if (dbHeadPost < dbHeadPre)
                    log($"  ✘ REGRESSION: _dbHead dropped from {dbHeadPre} to {dbHeadPost} after range changes — the buffer was probably resized destructively (the S23-FIX bug, back again)");
                else
                    log($"  ✔ _dbHead stays consistent ({dbHeadPre} → {dbHeadPost}) after 3 range changes — no data lost");

                // Also check that the _dbCpu array really has the same fixed capacity — if someone changes DashBufSize back to a small
                // value, it shows here.
                if (_dbCpu.Length < 3000)
                    log($"  ⚠ _dbCpu.Length={_dbCpu.Length} — under 3000 (expected 3600 = 60 min); the buffer is smaller than set in S9b-FIX");
                else
                    log($"  ✔ _dbCpu.Length={_dbCpu.Length} — full capacity (60 min), as set in S9b-FIX");
            }
            catch (Exception ex) { log($"  ✘ Range-switch regression test failed: {ex.Message}"); }

            log("Known icon-override risk buttons still resolvable (regression check for the _btnIcons bug class):");
            try
            {
                string[] riskyButtons = {
                    "BtnNetSettings", "BtnNetAdapters", "BtnAddRemove", "BtnServicesMsc",
                    "BtnSetManual", "BtnDisableService", "BtnDisableStartup", "BtnOpenTaskMgr",
                    "BtnDeviceManager",
                };
                int missing = 0;
                foreach (var name in riskyButtons)
                {
                    if (FindName(name) == null) { log($"  ⚠ {name} not found via FindName — was it renamed/removed? Check the icon-language dictionary (_btnIcons) stays in sync."); missing++; }
                }
                if (missing == 0) log($"  ✔ All {riskyButtons.Length} checked buttons resolve correctly");
            }
            catch (Exception ex) { log($"  ✘ Icon-override risk check failed: {ex.Message}"); }
        }

        /// <summary>
        /// Compares what LibreHardwareMonitor sees at the raw level (DumpAllSensorsRaw, already used by the Developer Panel) with
        /// what the app manages to show in its own snapshot (TempReader.Read()). It does not match sensor names exactly (they vary
        /// too much between motherboards/manufacturers to be reliable) — instead it counts, per type (Temperature/Load), how many
        /// LHM sensors report a real value vs. what the app exposes through its known fields (CpuTemp, GpuTemp, CpuLoadPct,
        /// GpuLoadPct). A large difference between "LHM sees X sensors with a value" and "the app shows 0" is exactly the sign of
        /// a "mute" sensor — LHM reads it, but the app code does not map it to any field it uses.
        /// </summary>
        public async Task RunSelfTestLhmVsAppAsync(Action<string> log, CancellationToken ct)
        {
            log("LHM raw sensors vs. what the app actually surfaces:");
            try
            {
                var rawLines = DumpLhmSensorsRaw();
                int lhmTempCount = rawLines.Count(l => l.TrimStart().StartsWith("Temperature") && !l.TrimEnd().EndsWith("= null"));
                int lhmLoadCount = rawLines.Count(l => l.TrimStart().StartsWith("Load") && !l.TrimEnd().EndsWith("= null"));

                var snap = await Task.Run(() => _tempReader?.Read(), ct);
                int appTempCount = (snap?.CpuTemp.HasValue == true ? 1 : 0) + (snap?.GpuTemp.HasValue == true ? 1 : 0);
                int appLoadCount = (snap?.CpuLoadPct.HasValue == true ? 1 : 0) + (snap?.GpuLoadPct.HasValue == true ? 1 : 0);

                log($"      LHM temperature sensors with a value: {lhmTempCount}   —   app surfaces: {appTempCount} (CpuTemp/GpuTemp)");
                log($"      LHM load sensors with a value:        {lhmLoadCount}   —   app surfaces: {appLoadCount} (CpuLoadPct/GpuLoadPct)");

                if (lhmTempCount > 0 && appTempCount == 0)
                    log("  ⚠ LHM sees temperature sensors, but the app shows none — CpuTemp/GpuTemp mapping may be broken on this hardware");
                else
                    log("  ✔ Temperature: app mapping looks consistent with what LHM reports");

                if (lhmLoadCount > 0 && appLoadCount == 0)
                    log("  ⚠ LHM sees load sensors, but the app shows none — CpuLoadPct/GpuLoadPct mapping may be broken on this hardware");
                else
                    log("  ✔ Load: app mapping looks consistent with what LHM reports");

                if (lhmTempCount == 0 && lhmLoadCount > 0)
                    log("  ✘ LHM reports ZERO temperature sensors but load sensors work fine — likely a partial LHM init failure (temperature module didn't start). If the app still shows a temperature, it's from a fallback source (e.g. WMI ThermalZone), not LHM directly.");
            }
            catch (Exception ex) { log($"  ✘ LHM vs app comparison failed: {ex.Message}"); }
        }

        internal void ForceReinitLhm() => _tempReader?.ReinitLhm();
        internal List<string> DumpLhmSensorsRaw() => _tempReader?.DumpAllSensorsRaw() ?? new List<string> { "(TempReader indisponibil)" };

        // Experimental tab — bridge methods so DebugInfoWindow can open these windows
        internal void OpenPowerHistoryFromDev() => OpenPowerHistory_Click(this, new System.Windows.RoutedEventArgs());
        internal void OpenSoundTestFromDev()    { var w = new SoundTestWindow(this, SettingsService.Current.ThemeName); w.Show(); }
        internal void OpenBatteryTestFromDev()  { var w = new BatteryTestWindow { Owner = this }; w.Show(); }

        internal void TriggerMaxLoadFromDev() => ToggleMaxLoad_Click(this, new System.Windows.RoutedEventArgs());

        internal void TriggerCpuBenchFromDev() => OpenCpuBenchWindow_Click(this, new System.Windows.RoutedEventArgs());
        internal void TriggerGpuBenchFromDev() => OpenGpuBenchWindow_Click(this, new System.Windows.RoutedEventArgs());

        internal void TriggerBatteryReportFromDev() => GenerateBatteryReport_Click(this, new System.Windows.RoutedEventArgs());
    }
}
