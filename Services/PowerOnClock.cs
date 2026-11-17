using System;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// How long the PC has been on since it was last switched on (right after power-on SMD Win said "60 days").
    /// Windows' own uptime (GetTickCount64 / LastBootUpTime) only resets on a full restart: with Fast Startup on, "Shut down"
    /// hibernates the kernel, so the counter keeps running across every shut-down — Task Manager shows the same large number.
    /// Every start (cold boot, Fast Startup or resume from hibernation) writes Kernel-Boot event 27 to the System log; the
    /// newest one is the real power-on moment. Sleep does not count as a start. Read off the UI thread, at most every 10 min.
    /// </summary>
    public static class PowerOnClock
    {
        private static DateTime? _startedAt;
        private static DateTime _checkedUtc = DateTime.MinValue;
        private static int _busy;

        /// <summary>Time since Windows' last full restart (what Windows itself calls uptime).</summary>
        public static TimeSpan KernelUptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

        /// <summary>Time since the PC was last switched on; Windows' uptime until the event log has been read.</summary>
        public static TimeSpan SinceOn
        {
            get
            {
                Prime();
                var kernel = KernelUptime;
                if (_startedAt is DateTime s)
                {
                    var d = DateTime.Now - s;
                    if (d >= TimeSpan.Zero && d <= kernel) return d;
                }
                return kernel;
            }
        }

        /// <summary>True when the last start was a Fast Startup / hibernation resume, so Windows' uptime is much longer.</summary>
        public static bool KernelKeptRunning => KernelUptime - SinceOn > TimeSpan.FromMinutes(10);

        /// <summary>Starts the event-log read in the background (called at start-up and by SinceOn).</summary>
        public static void Prime()
        {
            if ((DateTime.UtcNow - _checkedUtc).TotalMinutes < 10) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _checkedUtc = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                        System.Diagnostics.Eventing.Reader.PathType.LogName,
                        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]")
                    { ReverseDirection = true };
                    using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                    using var ev = reader.ReadEvent();
                    if (ev?.TimeCreated is DateTime t)
                    {
                        _startedAt = t;
                        AppLogger.Debug($"PowerOnClock: last start {t:dd.MM HH:mm}, Windows uptime {DisplayFormat.Uptime(KernelUptime)}");
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"PowerOnClock: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }
    }
}
