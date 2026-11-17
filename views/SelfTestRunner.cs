using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The full self-test (the 7 steps + summary + saving the report), used both from the Developer window and from the
    /// quick start on the Dashboard (5 clicks on LIVE). The report is the text sent through <c>progress</c>.
    /// </summary>
    internal static class SelfTestRunner
    {
        internal sealed class Result
        {
            public string Report = "";
            public string SavedPath = "";
        }

        internal static async Task<Result> RunAllAsync(MainWindow? mainWin, Action<string> progress, CancellationToken ct)
        {
            var lines = new StringBuilder();
            object gate = new();
            void P(string line)
            {
                lock (gate) lines.AppendLine(line);
                progress(line);
            }
            string Text() { lock (gate) return lines.ToString(); }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            P("═══════════════════════════════════════════════════════════");
            P($"  SMDWin — AUTOMATED SELF-TEST   ·   {SMDWin.Services.AppVersionInfo.Long}");
            P($"  Started: {DateTime.Now:dd-MM-yyyy HH:mm:ss}   Machine: {Environment.MachineName}");
            P("═══════════════════════════════════════════════════════════");

            if (mainWin != null)
            {
                P("");
                P("── 1/7  PAGE SWEEP ───────────────────────────────────────");
                await mainWin.RunSelfTestPageSweepAsync(P, ct);

                P("");
                P("── 2/7  CPU STRESS (load + UI responsiveness) ────────────");
                await mainWin.RunSelfTestCpuStressAsync(P, 10, ct);

                P("");
                P("── 3/7  GPU STRESS ───────────────────────────────────────");
                await mainWin.RunSelfTestGpuStressAsync(P, 10, ct);

                P("");
                P("── 4/7  INTERNAL TIMERS (heartbeat) ──────────────────────");
                await mainWin.RunSelfTestTimerHeartbeatAsync(P, ct);

                P("");
                P("── 5/7  LHM vs. APP SENSORS ───────────────────────────────");
                await mainWin.RunSelfTestLhmVsAppAsync(P, ct);

                P("");
                P("── 6/7  DASHBOARD CHART CONTINUITY ────────────────────────");
                await mainWin.RunSelfTestDashboardContinuityAsync(P, ct);

                P("");
                P("── 7/7  BACKEND CHECKS (drivers/sensors/app/settings/i18n/icons) ───");
            }
            else
            {
                P("⚠ Main window not found — skipping page sweep, CPU/GPU stress and timer heartbeat.");
            }

            var backend = await SelfTestService.RunAsync(P, ct: ct);

            sw.Stop();
            var counts = SelfTestSignalCounter.Count(Text());
            int totalWarn = counts.WarnCount, totalFail = counts.FailCount;

            var previousReadiness = AppReadinessHistoryService.GetPreviousEntry();
            var readiness = AppReadinessScoreCalculator.Calculate(
                totalWarn, totalFail, backend.HwDbKnownCount, backend.HwDbTotalCount);
            AppReadinessHistoryService.SaveResult(readiness, totalWarn, totalFail);

            P("");
            P("── SUMMARY ────────────────────────────────────────────────");
            P($"  Warnings: {totalWarn}   Errors: {totalFail}");
            string trendTxt = previousReadiness != null
                ? $" ({previousReadiness.Score} → {readiness.Score})"
                : " (first recorded run on this machine)";
            string hwCoverageTxt = readiness.HwCoveragePercent.HasValue
                ? $" · hardware database knows {readiness.HwCoveragePercent.Value:F0}% of this PC's parts (not part of the score)"
                : "";
            P($"  App readiness score: {readiness.Score}/100 — {readiness.Rating}{trendTxt}{hwCoverageTxt}");
            P($"  Total duration: {sw.Elapsed:mm\\:ss}");
            P("═══════════════════════════════════════════════════════════");

            string full = Text();
            string saved = await SelfTestService.SaveReportAsync(full, P);
            return new Result { Report = full, SavedPath = saved };
        }
    }
}
