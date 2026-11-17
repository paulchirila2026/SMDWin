using System;

namespace SMDWin.Services
{
    /// <summary>
    /// The real start-up steps reported by the SplashScreen (not simulated).
    /// The order matches the sequence in MainWindow.InitTempReaderThenStartTimer and the Loaded handler:
    /// PawnIO -> LibreHardwareMonitor -> Sensors -> Dashboard/UI.
    /// </summary>
    public enum StartupStep
    {
        PawnIO = 0,
        Lhm = 1,
        Sensors = 2,
        Interfata = 3
    }

    /// <summary>
    /// Minimal bus through which MainWindow reports the REAL initialization progress, and SplashWindow subscribes to show
    /// the current step live. No logic of its own — it only carries events between the two windows, so neither needs a
    /// direct reference to the other.
    /// </summary>
    public static class StartupProgress
    {
        /// <summary>Raised every time a real start-up step begins.</summary>
        public static event Action<StartupStep, string>? StepChanged;

        /// <summary>Raised when the dashboard is fully loaded and the app can be shown.</summary>
        public static event Action? Ready;

        private static bool _readyReported;

        public static void Report(StartupStep step, string message)
        {
            try { StepChanged?.Invoke(step, message); }
            catch (Exception ex) { AppLogger.Debug($"StartupProgress.Report: {ex.Message}"); }
        }

        public static void ReportReady()
        {
            // Idempotent: if the splash failsafe already closed the window, the event is not sent a second time.
            if (_readyReported) return;
            _readyReported = true;
            try { Ready?.Invoke(); }
            catch (Exception ex) { AppLogger.Debug($"StartupProgress.ReportReady: {ex.Message}"); }
        }

        /// <summary>Reset used only if a second splash is ever started in the same process (not the case today).</summary>
        internal static void ResetForTests() => _readyReported = false;
    }
}
