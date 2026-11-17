using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace SMDWin.Services
{
    /// <summary>
    /// Thin static wrapper around Serilog.
    /// Usage:  AppLogger.Info("message");
    ///         AppLogger.Warning(ex, "context");
    ///         AppLogger.Error(ex, "fatal context");
    ///
    /// Logs are written to:
    ///   Data\Logs\smdwin-.log next to SMDWin.exe (rolling, one file per day, 7 days kept)
    /// Debug sink is also active in Debug builds.
    /// </summary>
    public static class AppLogger
    {
        private static ILogger _log = Serilog.Core.Logger.None;
        private static bool _initialized;
        private static string? _logDir; // effective directory (portable or AppData)

        private static string ResolveLogDirectory()
        {
            return AppPaths.LogsDirectory;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            string logDir = ResolveLogDirectory();
            _logDir = logDir;

            Directory.CreateDirectory(logDir);

            string logPath = Path.Combine(logDir, "smdwin-.log");

            // Beta and debug builds keep the detailed log (it is what testers send back); a stable release writes
            // only Information and above, so the log stays small.
            bool detailed = AppVersionInfo.Prerelease;
#if DEBUG
            detailed = true;
#endif
            var cfg = new LoggerConfiguration()
                .MinimumLevel.Is(detailed ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
                .Enrich.WithThreadId()
                .WriteTo.File(
                    path:              logPath,
                    rollingInterval:   RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate:    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] (tid:{ThreadId}) {Message:lj}{NewLine}{Exception}",
                    shared:            false)
#if DEBUG
                .WriteTo.Debug(
                    outputTemplate:    "[SMDWin {Level:u3}] {Message:lj}{NewLine}{Exception}")
#endif
                ;

            Log.Logger = cfg.CreateLogger();
            _log = Log.Logger;

            // the build first: a log sent back must say which build it came from
            _log.Information("─── SMDWin started ─── {Build} (v{Version}) — logs: {LogDir}",
                AppVersionInfo.Long,
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString() ?? "?",
                logDir);
        }

        public static void Shutdown()
        {
            try { Log.CloseAndFlush(); } catch { }
        }

        /// <summary>Effective log directory (Data\Logs, see AppPaths).</summary>
        public static string LogDirectory => _logDir ?? ResolveLogDirectory();

        // ── Convenience methods ───────────────────────────────────────────────

        public static void Info(string message, params object?[] args)
            => _log.Information(message, args);

        public static void Debug(string message, params object?[] args)
            => _log.Debug(message, args);

        public static void Warning(string message, params object?[] args)
            => _log.Warning(message, args);

        public static void Warning(Exception ex, string context)
            => _log.Warning(ex, context);

        public static void Error(string message, params object?[] args)
            => _log.Error(message, args);

        public static void Error(Exception ex, string context)
            => _log.Error(ex, context);

        public static void Fatal(Exception ex, string context)
            => _log.Fatal(ex, context);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _swallowed = new();

        /// <summary>
        /// An exception caught on purpose (optional read, element that may be missing): logged at Debug level at most
        /// 3 times per location, so a sensor or read that always fails shows in the log without flooding it.
        /// </summary>
        /// <summary>How many times each place (file:line) has ignored an error since start (for the self-test report).</summary>
        public static System.Collections.Generic.Dictionary<string, int> SwallowedCounts() => new(_swallowed);

        public static void Swallowed(Exception ex,
            [System.Runtime.CompilerServices.CallerMemberName] string member = "",
            [System.Runtime.CompilerServices.CallerFilePath] string file = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            try
            {
                string where = $"{System.IO.Path.GetFileName(file)}:{line}";
                int n = _swallowed.AddOrUpdate(where, 1, (_, c) => c + 1);
                if (n <= 3) _log.Debug("Ignored at {Where} ({Member}): {Type}: {Message}", where, member, ex.GetType().Name, ex.Message);
            }
            catch { /* logging must never throw */ }
        }

        public static void LogSection(string title, params (string Key, object? Value)[] fields)
        {
            try
            {
                _log.Information("=== {Title} ===", title);
                foreach (var (key, value) in fields)
                    _log.Information("  {Key}: {Value}", key, value ?? "—");
            }
            catch (Exception ex) { Warning(ex, "AppLogger.LogSection"); }
        }

        /// <summary>
        /// Returns last N lines from the most recent log file.
        /// Used by the Debug Panel.
        /// </summary>
        public static string GetRecentLines(int count = 50)
        {
            try
            {
                string logDir = _logDir ?? ResolveLogDirectory();

                if (!Directory.Exists(logDir)) return "(no log files yet)";

                var files = Directory.GetFiles(logDir, "smdwin-*.log");
                if (files.Length == 0) return "(no log files yet)";

                // Most recent file
                Array.Sort(files);
                string latest = files[^1];

                // Read with FileShare.ReadWrite — Serilog keeps the file open
                using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string content = reader.ReadToEnd();

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                int start = Math.Max(0, lines.Length - count);
                return string.Join('\n', lines[start..]);
            }
            catch (Exception ex)
            {
                return $"(error reading log: {ex.Message})";
            }
        }
    }
}
