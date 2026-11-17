using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SMDWin.Services;
using SMDWin.Views.Net;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// The long self-test (13.11.2026): after the page tour, every tool is started for about 10 seconds and captured while it
    /// runs and after it stops — the speed test, ping, traceroute, DNS, ports, LAN, Wi-Fi Live, the surface scan, the basic
    /// RAM test and the CPU + GPU stress with full dials. A tool that runs shows texts and columns that an idle page does not
    /// (that is how the cut columns in Network were found), and the same text checks run on these pictures.
    /// Nothing is written to the disk and no setting changes: the disk benchmark is left out (it writes a test file).
    /// The pictures go to screenshots/live/ in the zip, on the user's theme.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>How many live steps there are (for the progress bar).</summary>
        private const int LiveStepCount = 11;   // 10 tools + the benchmark

        private async Task CaptureSelfTestLiveToolsAsync(string folder, List<string> files, Action<string, double>? progress,
                                                         CancellationToken ct, SelfTestUiData data)
        {
            string sub = Path.Combine(folder, "live");
            Directory.CreateDirectory(sub);
            int n = 0, done = 0;
            string label = PT("Tools running");

            void Shoot(string what)
            {
                var toastWas = ToastBorder.Visibility;
                ToastBorder.Visibility = Visibility.Collapsed;
                UpdateLayout();
                try
                {
                    string f = Path.Combine(sub, $"{++n:00}-{what}.png");
                    if (SaveElementPng(this, f)) files.Add(f);
                    CheckPicture(this, $"Live · {Path.GetFileNameWithoutExtension(f)}",
                                 TryFindResource("BgDarkBrush") as Brush ?? Brushes.White, data.Issues);
                }
                catch (Exception ex) { AppLogger.Debug($"Self-test live shot {what}: {ex.Message}"); }
                finally { ToastBorder.Visibility = toastWas; }
            }

            // start → run → picture while running → stop → picture after; a step that fails is logged and the next one runs
            async Task Step(string what, Func<Task<bool>> start, Action stop, int runMs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"{label} · {what}", done / (double)LiveStepCount);
                bool started = false;
                try
                {
                    started = await start();
                    if (!started) { AppLogger.Info("Self-test live: {0} skipped (not available)", what); return; }
                    await Task.Delay(runMs, ct);
                    Shoot(what + "-running");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLogger.Warning(ex, $"Self-test live {what}"); }
                finally
                {
                    if (started)
                    {
                        try { stop(); } catch (Exception ex) { AppLogger.Warning(ex, $"Self-test live stop {what}"); }
                    }
                    done++;
                }
                if (started)
                {
                    await Task.Delay(1500, ct);
                    Shoot(what + "-after");
                }
            }

            // the network tools: each page knows how to start and stop itself (NetToolPage.SelfTestStart / SelfTestStop)
            async Task<bool> StartNet(string key, int openMs = 900)
            {
                ShowNetTool(key, null);
                await Task.Delay(openMs, ct);
                return _netToolPages.TryGetValue(key, out var page) && page.SelfTestStart();
            }
            void StopNet(string key) { if (_netToolPages.TryGetValue(key, out var page)) page.SelfTestStop(); }

            try
            {
                await NavigateTo("Network");
                foreach (var (key, ms) in new[] { ("SpeedTest", 12000), ("Ping", 10000), ("Traceroute", 10000), ("Dns", 5000),
                                                  ("Ports", 10000), ("Lan", 10000), ("Wifi", 10000) })
                    await Step("Network-" + key, () => StartNet(key), () => StopNet(key), ms);
                ShowNetTool("Overview", null);

                // the surface scan only reads the disk
                await Step("Disk-Surface", async () =>
                {
                    await NavigateTo("Disk");
                    ClickNamed("BtnStorageTabSurface");
                    await Task.Delay(900, ct);
                    if (_surfaceScanCts != null && !_surfaceScanCts.IsCancellationRequested) return false;   // the user's own scan runs
                    if (CbSurfaceDisk != null && CbSurfaceDisk.SelectedIndex < 0 && _allDisks.Count > 0) CbSurfaceDisk.SelectedIndex = 0;
                    SurfaceScan_Start_Click(this, new RoutedEventArgs());
                    return true;
                }, () => { if (_surfaceScanCts != null && !_surfaceScanCts.IsCancellationRequested) _surfaceScanCts.Cancel(); }, 10000);
                ClickNamed("BtnStorageTabOverview");

                // the basic RAM test (memory only), stopped after 10 s
                await Step("Ram-Test", async () =>
                {
                    await NavigateTo("Ram");
                    await Task.Delay(700, ct);
                    if (_ramSequenceCts != null && !_ramSequenceCts.IsCancellationRequested) return false;
                    RunBasicRamTests_Click(this, new RoutedEventArgs());
                    return true;
                }, () => _ramSequenceCts?.Cancel(), 10000);

                // CPU + GPU stress, long enough for the dials to fill
                await Step("Stress-CpuGpu", async () =>
                {
                    await NavigateTo("Stress");
                    await Task.Delay(700, ct);
                    if (AnyStressActive) return false;
                    StartCpuStress();
                    StartGpuStress();
                    UpdateStressPlanUi();
                    return true;
                }, StopStressFromPlan, 15000);

                // the Benchmark page, a full run (~2 min): its record (raw values, sensor timeline) goes into the zip
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"{label} · Benchmark", done / (double)LiveStepCount);
                if (!_benchRunning && !AnyStressActive)
                {
                    await NavigateTo("Benchmark");
                    await Task.Delay(800, ct);
                    // 16.11.2026: the benchmark started 3 s after the stress test, with the processor still busy and hot
                    // ("busy 93 %"). Wait until the load stays under 15 % for 5 s (at most 45 s), so the marks are comparable.
                    var calm = System.Diagnostics.Stopwatch.StartNew();
                    int quiet = 0;
                    while (calm.Elapsed.TotalSeconds < 45 && quiet < 5)
                    {
                        await Task.Delay(1000, ct);
                        quiet = BenchRecentCpuLoad() is float benchLoad && benchLoad < 15 ? quiet + 1 : 0;
                    }
                    AppLogger.Info($"Self-test: benchmark starts after {calm.Elapsed.TotalSeconds:0} s of waiting for a quiet processor (quiet: {quiet >= 5})");
                    var run = RunBenchmarkAsync();
                    await Task.Delay(20000, ct);
                    Shoot("Benchmark-running");
                    var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(6), ct));
                    if (finished != run) { _benchCts?.Cancel(); await run; }
                    await Task.Delay(1200, ct);
                    Shoot("Benchmark-done");
                }
                done++;
            }
            finally
            {
                // whatever happened, nothing keeps running after the self-test
                try { if (AnyStressActive) StopStressFromPlan(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { _benchCts?.Cancel(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { foreach (var page in _netToolPages.Values) page.SelfTestStop(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { await NavigateTo("Dashboard"); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
            progress?.Invoke(label, 1);
        }
    }
}
