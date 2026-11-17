using System;
using System.Collections.Generic;
using System.Linq;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Keeps the last result of the CPU / GPU stress tests (Temp &amp; Stress) in TestResultStore: duration, maximum and
    /// average temperature, how much the processor slowed down and why, calculation errors.
    /// The Health window reads these results instead of running the tests again.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class StressRun
        {
            public DateTime Start = DateTime.Now;
            public float MaxT;
            public double SumT;
            public int N, ThrottledN;
            public int ErrorsAtStart;
            public readonly Dictionary<string, int> Reasons = new();
        }

        private StressRun? _cpuRun, _gpuRun;

        /// <summary>Below this the test says nothing about cooling or stability, so it is not saved.</summary>
        private const int MinRecordedStressSec = 20;

        private void BeginStressRun(bool gpu)
        {
            var r = new StressRun { ErrorsAtStart = gpu ? GpuStressService.IntegrityErrorCount : CpuStressor.IntegrityErrorCount };
            if (gpu) _gpuRun = r; else _cpuRun = r;
        }

        /// <summary>Called once per second, with the current temperatures and the last read of the slowdown detector.</summary>
        private void SampleStressRuns(float cpuTemp, float gpuTemp, ThrottleSnapshot? t)
        {
            try
            {
                if (_cpuRun != null && float.IsFinite(cpuTemp) && cpuTemp > 0)
                {
                    _cpuRun.MaxT = Math.Max(_cpuRun.MaxT, cpuTemp); _cpuRun.SumT += cpuTemp; _cpuRun.N++;
                    if (t?.IsThrottled == true)
                    {
                        _cpuRun.ThrottledN++;
                        string reason = t.Reason.ToString();
                        _cpuRun.Reasons[reason] = _cpuRun.Reasons.TryGetValue(reason, out int c) ? c + 1 : 1;
                    }
                }
                if (_gpuRun != null && float.IsFinite(gpuTemp) && gpuTemp > 0)
                {
                    _gpuRun.MaxT = Math.Max(_gpuRun.MaxT, gpuTemp); _gpuRun.SumT += gpuTemp; _gpuRun.N++;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SampleStressRuns: {ex.Message}"); }
        }

        /// <summary>How far above the temperature limit from Settings a stress test may go before it is stopped.</summary>
        private const float StressThermalMarginC = 10f;
        /// <summary>Consecutive reads above the stop threshold needed (a single spike does not stop the test).</summary>
        private const int StressThermalConfirmReads = 3;
        private int _stressHotReads;

        /// <summary>
        /// Safety stop: while a stress test runs, a processor or graphics card that stays more than
        /// <see cref="StressThermalMarginC"/> °C above its limit in Settings stops all the tests. Laptops protect themselves
        /// too (they slow down or shut down), but a test that is no longer telling anything useful should not keep heating.
        /// </summary>
        private void CheckStressThermalStop(float cpuTemp, float gpuTemp)
        {
            try
            {
                bool anyRunning = _cpuStress.Running || _gpuStress.IsRunning || _psuStress.IsRunning;
                if (!anyRunning) { _stressHotReads = 0; return; }
                var s = SettingsService.Current;
                float cpuStop = s.TempWarnCpu + StressThermalMarginC;
                float gpuStop = s.TempWarnGpu + StressThermalMarginC;
                bool cpuHot = float.IsFinite(cpuTemp) && cpuTemp >= cpuStop && cpuTemp < 125;   // above 125 °C = a misread sensor
                bool gpuHot = float.IsFinite(gpuTemp) && gpuTemp >= gpuStop && gpuTemp < 125;
                if (!cpuHot && !gpuHot) { _stressHotReads = 0; return; }
                if (++_stressHotReads < StressThermalConfirmReads) return;
                _stressHotReads = 0;

                string part = cpuHot ? "CPU" : "GPU";
                float temp = cpuHot ? cpuTemp : gpuTemp;
                float limit = cpuHot ? s.TempWarnCpu : s.TempWarnGpu;
                AppLogger.Warning("Stress: safety stop, {0} at {1:F0} °C (limit {2:F0} °C + {3:F0})", part, temp, limit, StressThermalMarginC);
                try { StopCpuStress(); } catch (Exception ex) { AppLogger.Warning(ex, "ThermalStop CPU"); }
                try { StopGpuStress(); } catch (Exception ex) { AppLogger.Warning(ex, "ThermalStop GPU"); }
                try { StopAllStress(); } catch (Exception ex) { AppLogger.Warning(ex, "ThermalStop all"); }
                _stressAutoStopAtUtc = null;

                string msg = string.Format(LanguageService.S("Stress", "ThermalStopFmt"), part,
                    TempUnit.Fmt(temp, space: true), TempUnit.Fmt(limit, space: true));
                ToolActivityLog.Add("Stress test", msg, "warn");
                ShowToast(msg, "⚠", 8000, ToastKind.Warning);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "CheckStressThermalStop"); }
        }

        private void EndStressRun(bool gpu)
        {
            var r = gpu ? _gpuRun : _cpuRun;
            if (gpu) _gpuRun = null; else _cpuRun = null;
            if (r == null) return;
            try
            {
                int sec = (int)(DateTime.Now - r.Start).TotalSeconds;
                if (sec < MinRecordedStressSec || r.N == 0) return;
                var rec = new StressTestRecord
                {
                    When = DateTime.Now,
                    DurationSec = sec,
                    MaxTempC = r.MaxT,
                    AvgTempC = (float)(r.SumT / r.N),
                    ThrottlePct = gpu ? 0 : (int)Math.Round(100.0 * r.ThrottledN / r.N),
                    ThrottleReason = r.Reasons.Count == 0 ? "" : r.Reasons.OrderByDescending(kv => kv.Value).First().Key,
                    IntegrityErrors = Math.Max(0, (gpu ? GpuStressService.IntegrityErrorCount : CpuStressor.IntegrityErrorCount) - r.ErrorsAtStart),
                };
                if (gpu) TestResultStore.SaveGpu(rec); else TestResultStore.SaveCpu(rec);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "EndStressRun"); }
        }
    }
}
