using System;
using System.Diagnostics;

namespace SMDWin.Services
{
    /// <summary>
    /// Start-up timing: each <see cref="Mark"/> writes to the log how many milliseconds have passed since the process
    /// started and how long the step took since the previous mark ("BOOT-TIMING: step — +5230 ms (step 410 ms)").
    /// The log shows where the time goes until the window appears.
    /// </summary>
    public static class BootTiming
    {
        private static readonly object _lock = new();
        private static DateTime _processStart = DateTime.MinValue;
        private static double _lastMs = -1;

        private static double SinceProcessStartMs()
        {
            if (_processStart == DateTime.MinValue)
            {
                try { _processStart = Process.GetCurrentProcess().StartTime; }
                catch (Exception ex) { _processStart = DateTime.Now; AppLogger.Debug($"BootTiming: StartTime: {ex.Message}"); }
            }
            return (DateTime.Now - _processStart).TotalMilliseconds;
        }

        public static void Mark(string step)
        {
            try
            {
                lock (_lock)
                {
                    double now = SinceProcessStartMs();
                    string delta = _lastMs < 0 ? "" : $" (pas {now - _lastMs:F0} ms)";
                    _lastMs = now;
                    AppLogger.Info($"BOOT-TIMING: {step} — +{now:F0} ms since process start{delta}");
                }
            }
            catch (Exception ex) { AppLogger.Debug($"BootTiming.Mark: {ex.Message}"); }
        }
    }
}
