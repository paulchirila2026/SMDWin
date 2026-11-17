using System;

namespace SMDWin.Services
{
    internal static class StressPriorityCoordinator
    {
        private static int _activeCount = 0;
        private static readonly object _lock = new();

        // the interface thread gets a higher priority while the stress runs: the charts are not starved of processor time
        private static void SetUiThreadPriority(System.Threading.ThreadPriority p)
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { System.Threading.Thread.CurrentThread.Priority = p; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }));
            }
            catch (Exception ex) { AppLogger.Debug($"StressPriorityCoordinator.SetUiThreadPriority: {ex.Message}"); }
        }

        public static void NotifyStressStarted()
        {
            lock (_lock)
            {
                _activeCount++;
                if (_activeCount == 1)
                {
                    // Only the interface thread goes up; the PROCESS stays "Normal". With the whole process at AboveNormal, the 7 stress
                    // threads (BelowNormal inside an AboveNormal process = above normal applications) took all the processor and starved
                    // the Windows services the sensor reads depend on (WMI, performance counters): a read took 6-7 s and the interface froze.
                    SetUiThreadPriority(System.Threading.ThreadPriority.AboveNormal);
                }
            }
        }

        public static void NotifyStressStopped()
        {
            lock (_lock)
            {
                _activeCount = Math.Max(0, _activeCount - 1);
                if (_activeCount == 0)
                {
                    SetUiThreadPriority(System.Threading.ThreadPriority.Normal);
                }
            }
        }
    }
}
