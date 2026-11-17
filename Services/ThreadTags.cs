using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>
    /// Names for native thread ids, so the CPU-WATCH line can say which part of SMD Win is busy ("UI", "ETW network",
    /// "LHM read"…) instead of bare numbers. A thread tags itself with <see cref="Set"/>; the last tag wins (pool threads are
    /// reused, so a tag means "last seen doing").
    /// </summary>
    public static class ThreadTags
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static readonly ConcurrentDictionary<int, string> _tags = new();

        public static void Set(string tag)
        {
            try { _tags[(int)GetCurrentThreadId()] = tag; } catch (Exception ex) { AppLogger.Debug($"ThreadTags: {ex.Message}"); }
        }

        public static string Of(int nativeId) => _tags.TryGetValue(nativeId, out var t) ? t : "";

        /// <summary>Set while the self-test runs: a high CPU then is expected (stress, benchmark, screenshots).</summary>
        public static volatile bool SelfTestRunning;
    }
}
