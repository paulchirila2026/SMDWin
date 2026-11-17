using System;
using System.IO;

namespace SMDWin.Services
{
    /// <summary>
    /// Protects the start-up from a sensor library that takes the whole process down. Opening LibreHardwareMonitor loads
    /// native code (processor, graphics and board drivers); when that code crashes, or security software kills the
    /// process for it, nothing reaches the log and the app closes right after the splash screen on every start.
    ///
    /// A marker file is written just before the sensors are opened and deleted right after. If the marker is still there
    /// at the next start, the previous start died while opening them: this start runs in "safe sensor mode" (no
    /// LibreHardwareMonitor, no Intel graphics library — only Windows' own readings), so the app opens. The mode is kept
    /// in the settings until the user chooses to try the sensors again.
    /// </summary>
    public static class SensorStartGuard
    {
        private static readonly object _lock = new();
        private static bool _evaluated;
        private static bool _safeMode;
        private static bool _enteredNow;

        private static string MarkerPath => AppPaths.Data("sensor-start.pending");

        /// <summary>True when the native sensor libraries must not be loaded in this session.</summary>
        public static bool SafeMode { get { Evaluate(); return _safeMode; } }

        /// <summary>True when this start found the marker of a crashed start (the notice explains it once).</summary>
        public static bool EnteredSafeModeThisStart { get { Evaluate(); return _enteredNow; } }

        private static void Evaluate()
        {
            lock (_lock)
            {
                if (_evaluated) return;
                _evaluated = true;
                try
                {
                    if (File.Exists(MarkerPath))
                    {
                        string when = "";
                        try { when = File.ReadAllText(MarkerPath).Trim(); } catch { }
                        AppLogger.Warning("SENSOR-GUARD: the previous start closed while the sensor libraries were opening ({0}) — this start uses safe sensor mode (Windows readings only).", when);
                        try { File.Delete(MarkerPath); } catch (Exception ex) { AppLogger.Debug($"SensorStartGuard delete: {ex.Message}"); }
                        SettingsService.Current.SensorSafeMode = true;
                        SettingsService.Save();
                        _enteredNow = true;
                    }
                    _safeMode = SettingsService.Current.SensorSafeMode;
                    if (_safeMode && !_enteredNow)
                        AppLogger.Info("SENSOR-GUARD: safe sensor mode is on (set after a crashed start) — LibreHardwareMonitor and the Intel graphics library are not loaded.");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "SensorStartGuard.Evaluate"); }
            }
        }

        /// <summary>Called just before the native sensor libraries are opened.</summary>
        public static void Begin(string what)
        {
            AppLogger.Info("SENSOR-GUARD: opening {0}…", what);
            try { File.WriteAllText(MarkerPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {what}"); }
            catch (Exception ex) { AppLogger.Debug($"SensorStartGuard begin: {ex.Message}"); }
        }

        /// <summary>Called right after the open returned (successfully or with a normal error the app handled).</summary>
        public static void End(string what)
        {
            try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); }
            catch (Exception ex) { AppLogger.Debug($"SensorStartGuard end: {ex.Message}"); }
            AppLogger.Info("SENSOR-GUARD: {0} opened", what);
        }

        /// <summary>The user chose to try the sensors again: from the next start the libraries are loaded normally.</summary>
        public static void TryAgainNextStart()
        {
            SettingsService.Current.SensorSafeMode = false;
            SettingsService.Save();
            AppLogger.Info("SENSOR-GUARD: the user turned safe sensor mode off — the sensors are tried again at the next start.");
        }
    }
}
