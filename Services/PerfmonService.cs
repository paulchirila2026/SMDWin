using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using WpfTimer = System.Threading.Timer;

namespace SMDWin.Services
{
    /// <summary>
    /// Singleton — collects Performance Monitor counters every 1 s.
    /// Each counter is stored in a circular RingBuffer of 60 values (60 s).
    /// Thread-safe. Never throws — errors are logged and ignored.
    /// </summary>
    public sealed class PerfmonService : IDisposable
    {
        public static readonly PerfmonService Instance = new();

        // ── Ring buffer ──────────────────────────────────────────────────────
        public const int BufSize = 60; // 60 seconds of history

        private readonly ConcurrentDictionary<string, float[]>  _buffers = new();
        private readonly ConcurrentDictionary<string, int>      _heads   = new();
        private readonly ConcurrentDictionary<string, bool>     _valid   = new();

        // ── Registered counters ────────────────────────────────────────────
        private readonly List<(string key, PerformanceCounter ctr)> _counters = new();
        private readonly object _lock = new();

        // ── Timer ────────────────────────────────────────────────────────────
        private WpfTimer? _timer;
        private bool   _started;
        private bool   _disposed;

        // ── Contoare disk (dinamice per disk) ────────────────────────────────
        private readonly List<(string diskName, PerformanceCounter read, PerformanceCounter write)> _diskCounters = new();

        // ── Public keys for standard counters ────────────────────────────
        public const string CpuTotal           = "cpu.total";
        public const string CpuQueueLength     = "cpu.queue";
        public const string MemAvailMB         = "mem.avail_mb";
        public const string MemCacheBytes      = "mem.cache_bytes";
        public const string MemPagesSec        = "mem.pages_sec";
        public const string MemPageFaultsSec   = "mem.pagefaults_sec";
        public const string MemCommitMB        = "mem.commit_mb";
        public const string NetBytesTotalSec   = "net.bytes_total_sec";
        public const string SysCalls           = "sys.calls_sec";

        // Disk keys: "disk.read.{name}", "disk.write.{name}"
        public static string DiskReadKey(string diskName)  => $"disk.read.{diskName}";
        public static string DiskWriteKey(string diskName) => $"disk.write.{diskName}";

        // ── Live properties (latest value) ────────────────────────────────
        public float CpuPercent        => LastValue(CpuTotal);
        public float QueueLength       => LastValue(CpuQueueLength);
        public float AvailableMB       => LastValue(MemAvailMB);
        public float CacheMB           => LastValue(MemCacheBytes) / 1024f / 1024f;
        public float PagesSec          => LastValue(MemPagesSec);
        public float PageFaultsSec     => LastValue(MemPageFaultsSec);
        public float CommitMB          => LastValue(MemCommitMB);
        public IReadOnlyList<string>   DiskNames => _diskNames;
        private readonly List<string>  _diskNames = new();

        // ── Start/Stop ───────────────────────────────────────────────────────
        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            Task.Run(InitCounters);
        }

        private void InitCounters()
        {
            try
            {
                Register(CpuTotal,         "Processor",        "% Processor Time", "_Total");
                Register(CpuQueueLength,   "System",           "Processor Queue Length");
                Register(MemAvailMB,       "Memory",           "Available MBytes");
                Register(MemCacheBytes,    "Memory",           "Cache Bytes");
                Register(MemPagesSec,      "Memory",           "Pages/sec");
                Register(MemPageFaultsSec, "Memory",           "Page Faults/sec");
                Register(MemCommitMB,      "Memory",           "Committed Bytes");
                Register(NetBytesTotalSec, "Network Interface","Bytes Total/sec", null, aggregate: true);
                Register(SysCalls,         "System",           "System Calls/sec");

                // Disk counters — per physical disk
                try
                {
                    var cat = new PerformanceCounterCategory("PhysicalDisk");
                    var instances = cat.GetInstanceNames()
                        .Where(n => n != "_Total")
                        .ToArray();

                    foreach (var disk in instances)
                    {
                        var readKey  = DiskReadKey(disk);
                        var writeKey = DiskWriteKey(disk);

                        var readCtr  = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  disk, readOnly: true);
                        var writeCtr = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", disk, readOnly: true);

                        // Prime
                        try { readCtr.NextValue();  } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                        try { writeCtr.NextValue(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                        EnsureBuffer(readKey);
                        EnsureBuffer(writeKey);

                        lock (_lock)
                        {
                            _diskCounters.Add((disk, readCtr, writeCtr));
                            if (!_diskNames.Contains(disk)) _diskNames.Add(disk);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "PerfmonService.DiskInit"); }

                // Prime all of them (the first read is 0 or garbage)
                foreach (var (_, ctr) in _counters)
                    try { ctr.NextValue(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                _timer = new WpfTimer(Tick, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "PerfmonService.InitCounters");
            }
        }

        private void Register(string key, string category, string counter, string? instance = null,
                               bool aggregate = false)
        {
            try
            {
                EnsureBuffer(key);

                if (aggregate)
                {
                    // Sum all instances (e.g. Network Interface)
                    try
                    {
                        var cat = new PerformanceCounterCategory(category);
                        var instances = cat.GetInstanceNames();
                        foreach (var inst in instances)
                        {
                            var aggKey = $"{key}.{inst}";
                            EnsureBuffer(aggKey);
                            var ctr = new PerformanceCounter(category, counter, inst, readOnly: true);
                            lock (_lock) _counters.Add((aggKey, ctr));
                        }
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    return;
                }

                var pc = instance != null
                    ? new PerformanceCounter(category, counter, instance, readOnly: true)
                    : new PerformanceCounter(category, counter, readOnly: true);

                lock (_lock) _counters.Add((key, pc));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"PerfmonService.Register({key})");
            }
        }

        private void EnsureBuffer(string key)
        {
            _buffers.TryAdd(key, new float[BufSize]);
            _heads.TryAdd(key, 0);
            _valid.TryAdd(key, false);
        }

        public volatile bool PollPaused;

        private void Tick(object? _)
        {
            try
            {
                if (PollPaused) return;
                TickCore();
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            finally
            {
                try { if (!_disposed) _timer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan); }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
        }

        private void TickCore()
        {
            List<(string key, PerformanceCounter ctr)> snapshot;
            List<(string diskName, PerformanceCounter read, PerformanceCounter write)> diskSnapshot;

            lock (_lock)
            {
                snapshot     = new List<(string, PerformanceCounter)>(_counters);
                diskSnapshot = new List<(string, PerformanceCounter, PerformanceCounter)>(_diskCounters);
            }

            // Contoare standard
            foreach (var (key, ctr) in snapshot)
            {
                try
                {
                    float val = ctr.NextValue();

                    // Committed Bytes → MB
                    if (key == MemCommitMB) val /= 1024f * 1024f;

                    Push(key, val);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            // Disk counters
            foreach (var (diskName, readCtr, writeCtr) in diskSnapshot)
            {
                try { Push(DiskReadKey(diskName),  readCtr.NextValue());  } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { Push(DiskWriteKey(diskName), writeCtr.NextValue()); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            // Aggregate net.bytes_total_sec = the sum of all adapters
            float netTotal = 0f;
            foreach (var key in _buffers.Keys.Where(k => k.StartsWith(NetBytesTotalSec + ".")))
                netTotal += LastValue(key);
            Push(NetBytesTotalSec, netTotal);

            // Notify subscribers
            DataUpdated?.Invoke();
        }

        private void Push(string key, float value)
        {
            if (!_buffers.TryGetValue(key, out var buf)) return;
            int head = _heads.GetOrAdd(key, 0);
            buf[head % BufSize] = value;
            _heads[key] = (head + 1) % BufSize;
            _valid[key] = true;
        }

        // ── Reading data ──────────────────────────────────────────────────────

        /// <summary>The last value in the buffer.</summary>
        public float LastValue(string key)
        {
            if (!_buffers.TryGetValue(key, out var buf)) return 0f;
            if (!_valid.TryGetValue(key, out bool v) || !v) return 0f;
            int head = _heads.GetOrAdd(key, 0);
            int last = (head - 1 + BufSize) % BufSize;
            return buf[last];
        }

        /// <summary>
        /// Returns the last N values in chronological order (oldest → newest).
        /// N is clamped to BufSize.
        /// </summary>
        public float[] GetHistory(string key, int count = BufSize)
        {
            count = Math.Clamp(count, 1, BufSize);
            if (!_buffers.TryGetValue(key, out var buf)) return new float[count];
            if (!_valid.TryGetValue(key, out bool v) || !v) return new float[count];

            int head = _heads.GetOrAdd(key, 0);
            var result = new float[count];
            for (int i = 0; i < count; i++)
            {
                int idx = (head - count + i + BufSize * 2) % BufSize;
                result[i] = buf[idx];
            }
            return result;
        }

        /// <summary>The average of the last N values.</summary>
        public float Average(string key, int count = 10)
            => GetHistory(key, count).Average();

        /// <summary>The max of the last N values.</summary>
        public float Max(string key, int count = BufSize)
            => GetHistory(key, count).Max();

        /// <summary>Event raised on every tick (1 s). Subscribe on the UI thread through the Dispatcher.</summary>
        public event Action? DataUpdated;

        // ── Dispose ───────────────────────────────────────────────────────────
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _timer?.Dispose();

            lock (_lock)
            {
                foreach (var (_, ctr) in _counters) try { ctr.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                foreach (var (_, r, w) in _diskCounters)
                {
                    try { r.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    try { w.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                _counters.Clear();
                _diskCounters.Clear();
            }
        }
    }
}
