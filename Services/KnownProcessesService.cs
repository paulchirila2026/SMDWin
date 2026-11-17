using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    public static class KnownProcessesService
    {
        private static readonly string FilePath = ResolveFilePath();

        private static string ResolveFilePath()
        {
            try
            {
                return AppPaths.Data("known_processes.json");
            }
            catch
            {
                return Path.Combine(AppPaths.AppDataFallbackDirectory, "known_processes.json");
            }
        }

        private static HashSet<string>? _known;
        private static bool _seedingFirstRun;
        private static bool _dirty;
        private static DateTime _lastFlushUtc = DateTime.MinValue;
        private static readonly object _lock = new();

        private static void EnsureLoaded()
        {
            if (_known != null) return;
            lock (_lock)
            {
                if (_known != null) return;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath);
                        _known = JsonSerializer.Deserialize<HashSet<string>>(json)
                                 ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // The very first run on this computer — see the class comment.
                        _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _seedingFirstRun = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"KnownProcessesService: load failed ({ex.Message}) — starting with an empty list, no 'new' badges this session.");
                    _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _seedingFirstRun = true; // a corrupt file equals "we know nothing" — better to skip the badges than to show them wrong
                }
            }
        }

        public static bool IsFirstTimeSeen(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            EnsureLoaded();
            lock (_lock)
            {
                bool isNew = !_known!.Contains(exePath);
                if (isNew)
                {
                    _known.Add(exePath);
                    _dirty = true;
                }
                return isNew && !_seedingFirstRun;
            }
        }

        /// <summary>
        /// Writes to disk if there are unsaved changes AND at least a minute has passed since the last flush — called from the
        /// normal tick of the Processes page, NOT for every new process found (that would mean I/O every 2 s).
        /// </summary>
        public static void FlushIfDirty()
        {
            lock (_lock)
            {
                if (_known == null || !_dirty) return;
                if ((DateTime.UtcNow - _lastFlushUtc).TotalSeconds < 60) return;
                try
                {
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(_known));
                    _dirty = false;
                    _lastFlushUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"KnownProcessesService: save failed ({ex.Message}).");
                }
            }
        }
    }
}
