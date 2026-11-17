using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Remembers, per service, the start type before the LAST disable done from SMDWin, so re-enabling returns exactly to it
    /// (it may have been "Manual" or "Automatic (Delayed Start)"), not to a generic "Automatic".
    /// </summary>
    public static class StartupOriginalStateStore
    {
        private class Entry
        {
            public string StartType { get; set; } = "Automatic";
            public bool   Delayed   { get; set; }
        }

        private static readonly string _path = AppPaths.Data("startup-original-state.json");
        private static readonly object _lock = new();

        private static Dictionary<string, Entry> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new();
            }
            catch (Exception ex) { AppLogger.Debug("StartupOriginalStateStore.Load: " + ex.Message); return new(); }
        }

        private static void SaveInternal(Dictionary<string, Entry> data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(data));
            }
            catch (Exception ex) { AppLogger.Debug("StartupOriginalStateStore.Save: " + ex.Message); }
        }

        /// <summary>Called right before disabling a service — remembers its current state (before this disable), so it can be
        /// restored exactly when re-enabled.</summary>
        public static void RecordBeforeDisable(string serviceName, string startType, bool delayed)
        {
            lock (_lock)
            {
                var data = Load();
                data[serviceName] = new Entry { StartType = startType, Delayed = delayed };
                SaveInternal(data);
            }
        }

        /// <summary>The state remembered for this service, if any (i.e. if it was disabled at least once from SMDWin). Null if
        /// nothing is remembered — the caller must use a safe fallback ("Automatic").</summary>
        public static (string startType, bool delayed)? GetOriginal(string serviceName)
        {
            lock (_lock)
            {
                var data = Load();
                return data.TryGetValue(serviceName, out var e) ? (e.StartType, e.Delayed) : null;
            }
        }
    }
}
