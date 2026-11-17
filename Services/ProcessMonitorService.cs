using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class ProcessMonitorService : IDisposable
    {
        private volatile PerformanceCounter? _cpuTotal;   // created in the background (see the constructor)
        // "% Processor Utility" is what Task Manager shows: CPU time weighted by the real clock (turbo counts more than 100 %
        // of the base speed). Missing on very old Windows — then the plain processor time is used.
        private volatile PerformanceCounter? _cpuUtility;
        // real disk throughput (all physical disks) for the Disk card; the per-process column is I/O, see ProcessMemoryInfo
        private volatile PerformanceCounter? _diskReadTotal, _diskWriteTotal;

        private readonly Dictionary<int, (TimeSpan cpu, DateTime ts)> _prevCpuTime = new();
        private readonly object _procStateLock = new();

        private readonly Dictionary<int, bool> _pidIsNewCache = new();
        // description and path of each process, read only once: MainModule can take up to 1 s (e.g. Excel), and the list
        // refreshes every few seconds. The key includes the name, so a reused PID is read again.
        private readonly Dictionary<int, (string Name, string? Desc, string? Path)> _moduleInfoCache = new();

        // P/Invoke for GetProcessIoCounters (no admin needed on same-session processes)
        [DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        // Track previous IO bytes for rate calculation
        private readonly Dictionary<int, (ulong readBytes, ulong writeBytes, DateTime ts)> _prevIo = new();

        private readonly HashSet<int> _moduleAccessDenied = new();

        private readonly HashSet<int> _moduleTimedOut = new();
        private static readonly TimeSpan ModuleAccessTimeout = TimeSpan.FromMilliseconds(800);

        // ── GPU per-process usage via PDH "GPU Engine" category ──────────────
        // Available on Windows 10 1709+ with WDDM 2.x drivers.
        // Instance names look like: "pid_1234_phys_0_eng_0_engtype_3D"
        // We aggregate all engtype_3D instances for each PID.
        private readonly Dictionary<int, float> _gpuPerPid = new();
        private readonly Dictionary<string, (PerformanceCounter ctr, int pid)> _gpuCounters = new();
        private DateTime _gpuLastRefresh = DateTime.MinValue;
        private DateTime _gpuLastEnum    = DateTime.MinValue;
        private static readonly TimeSpan GpuCacheTtl = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan GpuEnumTtl  = TimeSpan.FromSeconds(10);
        private static bool _gpuCategoryAvailable = true; // set false on first failure

        private readonly Dictionary<string, CounterSample> _gpuPrev = new();

        /// <summary>
        /// GPU % per process from the "GPU Engine" counters. One read of the whole category per refresh (ReadCategory), the rate
        /// computed from the previous sample of each instance — not one PerformanceCounter per instance: each of those re-reads the
        /// whole category on NextValue, so with a few hundred engine instances it cost a full CPU core (i5-4570:
        /// SMD Win at 50 % CPU).
        /// </summary>
        private void RefreshGpuPerProcess()
        {
            if (!_gpuCategoryAvailable) return;
            var now = DateTime.Now;
            if ((now - _gpuLastRefresh) < GpuCacheTtl) return;
            _gpuLastRefresh = now;

            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var data = cat.ReadCategory();
                var util = data["Utilization Percentage"];
                var pidMap = new Dictionary<int, float>();
                var seen = new HashSet<string>();
                if (util != null)
                {
                    foreach (InstanceData inst in util.Values)
                    {
                        string name = inst.InstanceName;
                        if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        int pid = 0;
                        var parts = name.Split('_');
                        for (int i = 0; i < parts.Length - 1; i++)
                            if (parts[i] == "pid" && int.TryParse(parts[i + 1], out int p)) { pid = p; break; }
                        if (pid <= 0) continue;
                        seen.Add(name);
                        var cur = inst.Sample;
                        if (_gpuPrev.TryGetValue(name, out var prev))
                        {
                            float val = CounterSample.Calculate(prev, cur);
                            if (float.IsFinite(val) && val >= 0 && (!pidMap.TryGetValue(pid, out float existing) || val > existing))
                                pidMap[pid] = Math.Min(val, 100f);
                        }
                        _gpuPrev[name] = cur;
                    }
                }
                foreach (var gone in _gpuPrev.Keys.Where(k => !seen.Contains(k)).ToList()) _gpuPrev.Remove(gone);

                lock (_gpuPerPid)
                {
                    _gpuPerPid.Clear();
                    foreach (var kv in pidMap)
                        _gpuPerPid[kv.Key] = kv.Value;
                }
            }
            catch (InvalidOperationException)
            {
                // "GPU Engine" category doesn't exist (no WDDM 2.x)
                _gpuCategoryAvailable = false;
                _gpuPrev.Clear();
            }
            catch (Exception ex) { AppLogger.Debug($"GPU PDH: {ex.Message}"); }
        }

        private bool _disposed;

        public ProcessMonitorService()
        {
            // the first performance counter in the process can take a few seconds (Windows loads the counter libraries): it is
            // created in the background, not on the thread that builds the window. Until then the CPU total is missing (0).
            _ = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    pc.NextValue(); // prime (the first read is always 0)
                    if (_disposed) pc.Dispose(); else _cpuTotal = pc;
                    if (sw.ElapsedMilliseconds > 500) AppLogger.Info($"ProcessMonitorService: processor counter created in {sw.ElapsedMilliseconds} ms (in the background)");
                    try
                    {
                        var util = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                        util.NextValue();
                        if (_disposed) util.Dispose(); else _cpuUtility = util;
                    }
                    catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: % Processor Utility unavailable: {ex.Message}"); }
                    try
                    {
                        var dr = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  "_Total", true);
                        var dw = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                        dr.NextValue(); dw.NextValue();
                        if (_disposed) { dr.Dispose(); dw.Dispose(); } else { _diskReadTotal = dr; _diskWriteTotal = dw; }
                    }
                    catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: PhysicalDisk counters unavailable: {ex.Message}"); }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "ProcessMonitorService: the 'Processor \\ % Processor Time \\ _Total' counter is unavailable" +
                        "(corrupt performance counter category?) — the CPU total will show 0%." +
                        "Possible fix on the machine: 'lodctr /R' in an Administrator console, then restart.");
                }
            });
        }

        // Process.MainWindowHandle enumerates every top-level window FOR EACH process (≈ processes × windows per snapshot); one
        // EnumWindows pass gives the set of processes that own a visible top-level window, the same answer for all of them
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static HashSet<int> PidsWithMainWindow()
        {
            var set = new HashSet<int>();
            try
            {
                EnumWindows((h, _) =>
                {
                    // like MainWindowHandle: visible and not owned by another window (GW_OWNER = 4)
                    if (IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero && GetWindowThreadProcessId(h, out uint pid) != 0)
                        set.Add((int)pid);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { AppLogger.Debug($"PidsWithMainWindow: {ex.Message}"); }
            return set;
        }

        /// <param name="light">For the Dashboard and Stress cards (every 2 s, top CPU / RAM only): skips GPU % and network per
        /// process — the GPU counters and the ETW traffic monitor are needed only by the Processes page and the Process Monitor,
        /// and the light calls kept the ETW session running on the Dashboard all the time.</param>
        public async Task<ProcessSnapshot> GetSnapshotAsync(int topN = 15, bool light = false)
        {
            if (_disposed) return new ProcessSnapshot();
            return await Task.Run(() =>
            {
                ThreadTags.Set(light ? "process list (light)" : "process list");
                if (_disposed) return new ProcessSnapshot();
                var snap = new ProcessSnapshot();
                try
                {
                // Total CPU — the same measure as Task Manager (utility); per-process values are scaled by the same factor
                float totalCpuTime = -1, cpuUtil = -1;
                try { if (_cpuTotal != null) totalCpuTime = _cpuTotal.NextValue(); }
                catch (Exception logEx) { AppLogger.Warning(logEx, "ProcessMonitorService.GetSnapshotAsync: TotalCpuPct"); }
                try { if (_cpuUtility != null) cpuUtil = _cpuUtility.NextValue(); }
                catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: % Processor Utility: {ex.Message}"); }
                double cpuScale = cpuUtil >= 0 && totalCpuTime >= 1 ? Math.Clamp(cpuUtil / totalCpuTime, 1.0, 2.5) : 1.0;
                float cpuShown = cpuUtil >= 0 ? cpuUtil : totalCpuTime;
                if (cpuShown >= 0) snap.TotalCpuPct = (float)Math.Round(Math.Min(100f, cpuShown), 1);

                // Real disk throughput, all physical disks (KB/s)
                try
                {
                    if (_diskReadTotal != null && _diskWriteTotal != null)
                        snap.TotalDiskKBs = (_diskReadTotal.NextValue() + _diskWriteTotal.NextValue()) / 1024f;
                }
                catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: PhysicalDisk read: {ex.Message}"); }

                // Real network traffic, all physical adapters (KB/s); virtual adapters are left out like in the Network page
                try
                {
                    double net = 0;
                    foreach (var a in NetworkTrafficSampler.GetCurrentTraffic())
                        if (!a.IsVirtual) net += a.SendKBs + a.RecvKBs;
                    snap.TotalNetKBs = (float)net;
                }
                catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: adapter traffic: {ex.Message}"); }

                // Total RAM — read from WmiCache (PerformanceCounter, zero WMI cost)
                try
                {
                    var (used, total) = WmiCache.Instance.GetRamUsage();
                    snap.UsedRamMB  = used;
                    snap.TotalRamMB = total;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "ProcessMonitorService.GetSnapshotAsync"); }

                // Per-process info
                var procs = Process.GetProcesses();
                var list  = new List<ProcessEntry>();

                var parentMap = ProcessTreeHelper.GetParentMap();
                var privateWs = ProcessMemoryInfo.GetPrivateWorkingSets();   // the "Memory" Task Manager shows
                var withWindow = PidsWithMainWindow();

                int sigBudget = 30;
                int enrichCount = Math.Min(topN, 20);
                var procById = new Dictionary<int, Process>(procs.Length);

                const int SlowStepMs = 200;
                var loopSw = System.Diagnostics.Stopwatch.StartNew();

                lock (_procStateLock)
                {
                // ── PASS 1: cheap fields, for ALL processes ────────────
                foreach (var p in procs)
                {
                    if (_disposed) break; // abort iteration if service disposed during enumeration
                    try
                    {
                        if (p.Id == 0) continue; // Idle

                        var entry = new ProcessEntry
                        {
                            Pid  = p.Id,
                            Name = p.ProcessName,
                        };

                        if (parentMap.TryGetValue(p.Id, out int parentPid))
                            entry.ParentPid = parentPid;

                        // private working set (like Task Manager); the full working set only if it could not be read
                        if (privateWs.TryGetValue(p.Id, out long pws) && pws > 0)
                            entry.RamMB = (float)Math.Round(pws / 1_048_576.0, 1);
                        else
                            try { entry.RamMB = (float)Math.Round(p.WorkingSet64 / 1_048_576.0, 1); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                        try { entry.Threads = p.Threads.Count; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                        try { entry.Handles = p.HandleCount; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                        entry.HasWindow = withWindow.Contains(p.Id);

                        // Mark system processes
                        var sysNames = new[] { "System", "Registry", "smss", "csrss", "wininit",
                            "services", "lsass", "svchost", "dwm", "fontdrvhost", "audiodg",
                            "spoolsv", "SearchIndexer", "WmiPrvSE", "MsMpEng", "NisSrv" };
                        entry.IsSystem = sysNames.Any(n =>
                            string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase))
                            || p.SessionId == 0;

                        try
                        {
                            var nowCpu = DateTime.UtcNow;
                            TimeSpan cpuTime = p.TotalProcessorTime;
                            if (_prevCpuTime.TryGetValue(p.Id, out var prevCpu))
                            {
                                double wallMs = (nowCpu - prevCpu.ts).TotalMilliseconds;
                                if (wallMs > 50)
                                {
                                    double cpuMs = (cpuTime - prevCpu.cpu).TotalMilliseconds;
                                    double pct = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0 * cpuScale;
                                    entry.CpuPct = (float)Math.Round(Math.Clamp(pct, 0, 100), 2);
                                }
                            }
                            _prevCpuTime[p.Id] = (cpuTime, nowCpu);
                        }
                        catch
                        {
                            // access denied / process ended — CPU stays 0 for this PID
                            entry.CpuPct = 0f;
                        }

                        list.Add(entry);
                        procById[p.Id] = p; // keep the Process alive — it may be needed in pass 2
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    // NO dispose here — see the single disposal at the end of both passes.
                }

                var enrichPids = new HashSet<int>(list.Where(x => x.HasWindow).Select(x => x.Pid));
                foreach (var e in list.OrderByDescending(x => x.CpuPct).Take(enrichCount)) enrichPids.Add(e.Pid);
                foreach (var e in list.OrderByDescending(x => x.RamMB).Take(enrichCount)) enrichPids.Add(e.Pid);

                // ── PASS 2: the expensive block — ONLY for enrichPids ──────────────
                foreach (var entry in list)
                {
                    if (_disposed) break;
                    if (!enrichPids.Contains(entry.Pid)) continue;
                    if (!procById.TryGetValue(entry.Pid, out var p)) continue;
                    try
                    {
                        var ioSw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            if (!_moduleAccessDenied.Contains(p.Id) &&
                                GetProcessIoCounters(p.Handle, out var io))  // p.Handle may throw Win32Exception(5) for protected PIDs
                            {
                                var now = DateTime.UtcNow;
                                if (_prevIo.TryGetValue(p.Id, out var prev))
                                {
                                    double secs = (now - prev.ts).TotalSeconds;
                                    if (secs > 0.01)
                                    {
                                        entry.DiskReadKBs  = (float)((io.ReadTransferCount  - prev.readBytes)  / 1024.0 / secs);
                                        entry.DiskWriteKBs = (float)((io.WriteTransferCount - prev.writeBytes) / 1024.0 / secs);
                                        if (entry.DiskReadKBs  < 0) entry.DiskReadKBs  = 0;
                                        if (entry.DiskWriteKBs < 0) entry.DiskWriteKBs = 0;
                                    }
                                }
                                _prevIo[p.Id] = (io.ReadTransferCount, io.WriteTransferCount, now);
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            _moduleAccessDenied.Add(p.Id); // p.Handle denied — skip IO for this PID
                        }
                        catch (InvalidOperationException) { } // process exited between GetProcesses() and Handle access — harmless
                        catch (Exception ex) { AppLogger.Warning(ex, "ProcessMonitorService.unknown"); }
                        finally
                        {
                            if (ioSw.ElapsedMilliseconds > SlowStepMs)
                                AppLogger.Warning($"ProcessMonitor SLOW STEP: GetProcessIoCounters/Handle for PID {p.Id} ({entry.Name}) took {ioSw.ElapsedMilliseconds}ms");
                        }

                        if (!_moduleAccessDenied.Contains(p.Id) && !_moduleTimedOut.Contains(p.Id))
                        {
                            var moduleSw = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                string? descOut = null, exePathOut = null;
                                bool completed;
                                if (_moduleInfoCache.TryGetValue(p.Id, out var known) && known.Name == entry.Name)
                                {
                                    descOut = known.Desc; exePathOut = known.Path; completed = true;
                                }
                                else
                                {
                                    int pidForPath = p.Id;
                                    completed = System.Threading.Tasks.Task.Run(() =>
                                    {
                                        // the path straight from Windows (instant), the description from the file's version info —
                                        // not Process.MainModule, which walks all the process's modules (up to 800 ms per process)
                                        string? path = ProcessMemoryInfo.GetImagePath(pidForPath);
                                        if (path == null)
                                        {
                                            var mainModule = p.MainModule;   // fallback; may throw Win32Exception like before
                                            path = mainModule?.FileName;
                                        }
                                        if (!string.IsNullOrEmpty(path))
                                        {
                                            try
                                            {
                                                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                                                if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
                                                    descOut = fvi.FileDescription;
                                            }
                                            catch (Exception ex) { AppLogger.Debug($"ProcessMonitor version info {path}: {ex.Message}"); }
                                        }
                                        exePathOut = path;
                                    }).Wait(ModuleAccessTimeout);
                                    if (completed) _moduleInfoCache[p.Id] = (entry.Name, descOut, exePathOut);
                                }

                                if (!completed)
                                {
                                    _moduleTimedOut.Add(p.Id);
                                    AppLogger.Warning($"ProcessMonitor: MainModule access for PID {p.Id} ({entry.Name}) exceeded {ModuleAccessTimeout.TotalMilliseconds}ms — unresponsive process (protected by AV/EDR, suspended, etc.). Skipped for good for this PID, so the app is not blocked again.");
                                }
                                else
                                {
                                    entry.Description = descOut ?? "";
                                    if (!string.IsNullOrWhiteSpace(exePathOut))
                                    {
                                        var sign = FileSignatureService.GetSignInfo(exePathOut, ref sigBudget);
                                        entry.SignStatus = sign.Status;
                                        entry.Publisher  = sign.Publisher;
                                        // our own (unsigned) program is not marked as suspicious
                                        if (string.Equals(exePathOut, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                                        { entry.SignStatus = ""; entry.Publisher = "SMD Win (this program)"; }

                                        if (!_pidIsNewCache.TryGetValue(p.Id, out bool isNew))
                                        {
                                            isNew = KnownProcessesService.IsFirstTimeSeen(exePathOut);
                                            _pidIsNewCache[p.Id] = isNew;
                                        }
                                        entry.IsNew = isNew;
                                    }
                                }
                            }
                            catch (AggregateException ex) when (ex.InnerException is System.ComponentModel.Win32Exception)
                            {
                                _moduleAccessDenied.Add(p.Id); // never try again for this PID
                            }
                            catch (AggregateException ex) when (ex.InnerException is InvalidOperationException)
                            {
                                // process exited between snapshot and MainModule access — harmless
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                _moduleAccessDenied.Add(p.Id); // never try again for this PID
                            }
                            catch (InvalidOperationException) { } // process exited between snapshot and MainModule access — harmless
                            catch (Exception ex)
                            {
                                _moduleAccessDenied.Add(p.Id);
                                AppLogger.Warning(ex, "ProcessMonitorService.unknown");
                            }
                            finally
                            {
                                if (moduleSw.ElapsedMilliseconds > SlowStepMs)
                                    AppLogger.Warning($"ProcessMonitor SLOW STEP: MainModule/FileSignature for PID {p.Id} ({entry.Name}) took {moduleSw.ElapsedMilliseconds}ms");
                            }
                        }
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }

                // ── Single dispose after BOTH passes — each PID's Process may have been needed until pass 2. ─────────────────
                foreach (var p in procById.Values) { try { p.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }

                loopSw.Stop();
                if (loopSw.ElapsedMilliseconds > 3000)
                    AppLogger.Warning($"ProcessMonitor SLOW LOOP: full enumeration of {procs.Length} processes ({enrichPids.Count} enriched) took {loopSw.ElapsedMilliseconds}ms total — check preceding SLOW STEP lines above for the exact PID/call responsible");

                // Cleanup stale entries
                var alive = list.Select(x => x.Pid).ToHashSet();
                foreach (var k in _prevCpuTime.Keys.Where(k => !alive.Contains(k)).ToList())
                    _prevCpuTime.Remove(k);
                // Prune dead PIDs from the deny-list to avoid unbounded growth
                _moduleAccessDenied.IntersectWith(alive);
                _moduleTimedOut.IntersectWith(alive);
                // Prune stale IO history
                foreach (var k in _prevIo.Keys.Where(k => !alive.Contains(k)).ToList())
                    _prevIo.Remove(k);
                foreach (var k in _pidIsNewCache.Keys.Where(k => !alive.Contains(k)).ToList())
                    _pidIsNewCache.Remove(k);
                foreach (var k in _moduleInfoCache.Keys.Where(k => !alive.Contains(k)).ToList())
                    _moduleInfoCache.Remove(k);
                }

                KnownProcessesService.FlushIfDirty();

                var pidToName = list.ToDictionary(x => x.Pid, x => x.Name);
                foreach (var e in list)
                {
                    if (e.ParentPid > 0 && pidToName.TryGetValue(e.ParentPid, out var pName))
                        e.ParentName = pName;
                    // else: the parent has already exited — ParentDisplay shows "PID X (ended)" automatically, see ProcessEntry.ParentDisplay
                }

                // Populate GPU usage per process
                if (!light) RefreshGpuPerProcess();
                lock (_gpuPerPid)
                {
                    foreach (var entry in list)
                    {
                        if (_gpuPerPid.TryGetValue(entry.Pid, out float gpuPct))
                            entry.GpuPct = gpuPct;
                    }
                }

                // Network per process — from the ETW monitor (TCP + UDP, IPv4 + IPv6); the TCP connection statistics only when it cannot start
                if (!light) try
                {
                    TcpEStatsService.Instance.Refresh();
                    foreach (var entry in list)
                        if (TcpEStatsService.Instance.TryGetPidTraffic(entry.Pid, out double sKBs, out double rKBs, out _, out _))
                        {
                            entry.NetSentBytes = (long)(sKBs * 1024);
                            entry.NetRecvBytes = (long)(rKBs * 1024);
                        }
                }
                catch (Exception ex) { AppLogger.Debug($"ProcessMonitorService: per-process network: {ex.Message}"); }

                snap.TopByCpu = list.OrderByDescending(x => x.CpuPct)
                                    .ThenByDescending(x => x.RamMB)
                                    .Take(topN).ToList();

                snap.TopByRam = list.OrderByDescending(x => x.RamMB)
                                    .Take(topN).ToList();

                snap.AllProcesses = list.OrderByDescending(x => x.CpuPct)
                                        .ThenByDescending(x => x.RamMB).ToList();

                // Apps: like Task Manager, an app row includes its helper processes (Edge's 20+ msedge.exe, a program's ffmpeg
                // children) — the values are the sums, "· N processes" shows how many. Those helpers then leave Background.
                var (apps, adopted) = GroupApps(list);
                snap.ForegroundApps = apps.OrderByDescending(x => x.CpuPct)
                                          .ThenByDescending(x => x.RamMB).ToList();

                snap.BackgroundProcesses = list.Where(x => !x.HasWindow && !adopted.Contains(x.Pid))
                                               .OrderByDescending(x => x.CpuPct)
                                               .ThenByDescending(x => x.RamMB).ToList();

                snap.ProcessCount = list.Count;
                return snap;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "ProcessMonitorService.GetSnapshotAsync: unexpected error while listing processes");
                    snap.Error = ex.Message;
                    return snap;
                }
            });
        }

        // Shells and system hosts own windows and start everything else: they never take other programs' processes into their group
        private static readonly HashSet<string> NoAdoptRoots = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "svchost", "ApplicationFrameHost", "sihost", "dwm", "csrss", "winlogon", "wininit", "services",
            "System", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        };

        /// <summary>
        /// Groups each windowed app with its helper processes: a process without a window joins the nearest windowed ancestor
        /// (parent, grandparent…), unless that ancestor is a shell or system host, where it only joins if it is the same program.
        /// Returns the summed app rows (copies — the entries in AllProcesses keep their own values) and the PIDs taken in.
        /// </summary>
        private static (List<ProcessEntry> apps, HashSet<int> adopted) GroupApps(List<ProcessEntry> list)
        {
            var byPid = new Dictionary<int, ProcessEntry>(list.Count);
            foreach (var e in list) byPid[e.Pid] = e;
            var groups = new Dictionary<int, List<ProcessEntry>>();
            foreach (var e in list) if (e.HasWindow) groups[e.Pid] = new List<ProcessEntry> { e };
            var adopted = new HashSet<int>();

            foreach (var e in list)
            {
                if (e.HasWindow) continue;
                int cur = e.ParentPid, depth = 0;
                while (cur > 0 && depth++ < 16)
                {
                    if (groups.TryGetValue(cur, out var g))
                    {
                        var root = g[0];
                        if (!NoAdoptRoots.Contains(root.Name) || string.Equals(root.Name, e.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            g.Add(e);
                            adopted.Add(e.Pid);
                        }
                        break;
                    }
                    if (!byPid.TryGetValue(cur, out var parent) || parent.ParentPid == cur) break;
                    cur = parent.ParentPid;
                }
            }

            var apps = new List<ProcessEntry>(groups.Count);
            foreach (var g in groups.Values)
            {
                if (g.Count == 1) { apps.Add(g[0]); continue; }
                var row = g[0].CloneShallow();
                row.GroupCount   = g.Count;
                row.CpuPct       = (float)Math.Round(Math.Min(100.0, g.Sum(x => (double)x.CpuPct)), 2);
                row.RamMB        = g.Sum(x => x.RamMB);
                row.DiskReadKBs  = g.Sum(x => x.DiskReadKBs);
                row.DiskWriteKBs = g.Sum(x => x.DiskWriteKBs);
                row.NetSentBytes = g.Sum(x => x.NetSentBytes);
                row.NetRecvBytes = g.Sum(x => x.NetRecvBytes);
                row.GpuPct       = Math.Min(100f, g.Sum(x => x.GpuPct));
                row.Threads      = g.Sum(x => x.Threads);
                row.Handles      = g.Sum(x => x.Handles);
                apps.Add(row);
            }
            return (apps, adopted);
        }

        public string KillProcess(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.Dispose();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }


        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cpuTotal?.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            try { _cpuUtility?.Dispose(); _diskReadTotal?.Dispose(); _diskWriteTotal?.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            lock (_procStateLock) { _prevCpuTime.Clear(); }
            foreach (var (ctr, _) in _gpuCounters.Values) try { ctr.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            _gpuCounters.Clear();
        }
    }
}
