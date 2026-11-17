using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Processes paused from SMD Win. A paused program stays frozen until something resumes it, so every pause is
    /// remembered (in memory and in a file in Data) and undone when SMD Win closes. If SMD Win stopped without closing
    /// normally (crash, forced close), the processes still paused are resumed at the next start.
    /// A process is identified by PID + start time, so a new program that reused the PID is never touched.
    /// </summary>
    public static class PausedProcesses
    {
        public sealed class Entry
        {
            public int Pid { get; set; }
            public string Name { get; set; } = "";
            public long StartTicksUtc { get; set; }
            /// <summary>How many times it was paused (each pause needs its own resume).</summary>
            public int Count { get; set; }
        }

        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

        private static readonly object _lock = new();
        private static readonly Dictionary<int, Entry> _paused = new();
        private static string FilePath => AppPaths.Data("paused-processes.json");

        public static int Count { get { lock (_lock) return _paused.Count; } }


        /// <summary>Pauses the process. Throws if Windows refuses (access denied, the process has ended).</summary>
        public static void Pause(int pid)
        {
            using var proc = Process.GetProcessById(pid);
            long start = StartTicks(proc);
            int status = NtSuspendProcess(proc.Handle);
            if (status != 0) throw new InvalidOperationException($"NtSuspendProcess 0x{status:X8}");
            lock (_lock)
            {
                if (_paused.TryGetValue(pid, out var e) && e.StartTicksUtc == start) e.Count++;
                else _paused[pid] = new Entry { Pid = pid, Name = SafeName(proc), StartTicksUtc = start, Count = 1 };
                Save();
            }
            AppLogger.Info("Process paused: {0} ({1})", SafeName(proc), pid);
        }

        /// <summary>Resumes the process completely (all the pauses made from SMD Win).</summary>
        public static void Resume(int pid)
        {
            using var proc = Process.GetProcessById(pid);
            int times;
            lock (_lock) times = _paused.TryGetValue(pid, out var e) ? Math.Max(1, e.Count) : 1;
            for (int i = 0; i < times; i++)
            {
                int status = NtResumeProcess(proc.Handle);
                if (status != 0) throw new InvalidOperationException($"NtResumeProcess 0x{status:X8}");
            }
            lock (_lock) { _paused.Remove(pid); Save(); }
            AppLogger.Info("Process resumed: {0} ({1})", SafeName(proc), pid);
        }

        /// <summary>On close: resumes every process paused in this session. Returns how many were resumed.</summary>
        public static int ResumeAll()
        {
            List<Entry> list;
            lock (_lock) list = _paused.Values.ToList();
            int n = ResumeEntries(list);
            lock (_lock) { _paused.Clear(); Save(); }
            return n;
        }

        /// <summary>At start: resumes the processes left paused by a session that did not close normally.</summary>
        public static int ResumeLeftovers()
        {
            List<Entry>? list = null;
            try
            {
                if (File.Exists(FilePath))
                    list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PausedProcesses: unreadable file"); }
            int n = list is { Count: > 0 } ? ResumeEntries(list) : 0;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { AppLogger.Warning(ex, "PausedProcesses: delete file"); }
            return n;
        }

        private static int ResumeEntries(IEnumerable<Entry> entries)
        {
            int n = 0;
            foreach (var e in entries)
            {
                try
                {
                    using var proc = Process.GetProcessById(e.Pid);
                    // same PID, but a different program started later: not ours
                    if (e.StartTicksUtc != 0 && StartTicks(proc) != e.StartTicksUtc) continue;
                    for (int i = 0; i < Math.Max(1, e.Count); i++) NtResumeProcess(proc.Handle);
                    n++;
                    AppLogger.Info("Process resumed automatically: {0} ({1})", e.Name, e.Pid);
                }
                catch (ArgumentException) { /* the process has already ended */ }
                catch (Exception ex) { AppLogger.Warning(ex, $"PausedProcesses: resume {e.Name} ({e.Pid})"); }
            }
            return n;
        }

        private static void Save()
        {
            try
            {
                if (_paused.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_paused.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PausedProcesses.Save"); }
        }

        private static long StartTicks(Process p)
        {
            try { return p.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) { AppLogger.Debug($"PausedProcesses start time {p.Id}: {ex.Message}"); return 0; }
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; }
            catch (Exception ex) { AppLogger.Debug($"PausedProcesses name: {ex.Message}"); return "?"; }
        }
    }
}
