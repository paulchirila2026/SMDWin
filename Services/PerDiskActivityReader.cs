using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace SMDWin.Services
{
    /// <summary>
    /// The activity of each physical disk (Storage page, the chart in every disk card): read / write speed, the response time
    /// and how busy the disk is, from the Windows "PhysicalDisk" counters of each instance ("0 C:", "1 D:"), not "_Total".
    /// One <see cref="PerformanceCounterCategory.ReadCategory"/> per read gives all disks at once, so the cost does not grow
    /// with the number of disks. Call from a background thread.
    /// </summary>
    public sealed class PerDiskActivityReader
    {
        public readonly record struct Sample(float ReadKBs, float WriteKBs, float LatencyMs, float BusyPct);

        private static readonly string[] CounterNames =
        {
            "Disk Read Bytes/sec", "Disk Write Bytes/sec", "Avg. Disk sec/Transfer", "% Idle Time",
        };

        private PerformanceCounterCategory? _category;
        private Dictionary<string, CounterSample[]> _previous = new();
        private int _failures;

        /// <summary>false after repeated failures (counters disabled or broken): no more reads.</summary>
        public bool IsAvailable { get; private set; } = true;

        /// <summary>How many reads returned data (the first one only primes the counters).</summary>
        public int GoodReads { get; private set; }
        private string? _loggedInstances;

        /// <summary>The disk numbers seen in the last read (a disk without a counter — some USB sticks — is missing).</summary>
        public HashSet<int> KnownDisks { get; private set; } = new();

        /// <summary>The values since the previous read, by disk number (Win32 disk index). Empty on the first read.</summary>
        public Dictionary<int, Sample> Read()
        {
            var result = new Dictionary<int, Sample>();
            if (!IsAvailable) return result;
            try
            {
                _category ??= new PerformanceCounterCategory("PhysicalDisk");
                var data = _category.ReadCategory();
                var columns = new InstanceDataCollection?[CounterNames.Length];
                for (int i = 0; i < CounterNames.Length; i++)
                    columns[i] = data.Contains(CounterNames[i]) ? data[CounterNames[i]] : null;
                if (columns[0] == null || columns[1] == null) { Fail(); return result; }

                var next = new Dictionary<string, CounterSample[]>();
                var known = new HashSet<int>();
                foreach (InstanceData inst in columns[0]!.Values)
                {
                    string name = inst.InstanceName;
                    if (name == "_Total") continue;
                    int space = name.IndexOf(' ');
                    if (!int.TryParse(space > 0 ? name[..space] : name, out int index)) continue;
                    known.Add(index);

                    var current = new CounterSample[CounterNames.Length];
                    for (int i = 0; i < CounterNames.Length; i++)
                        current[i] = columns[i] != null && columns[i]!.Contains(name) ? columns[i]![name].Sample : CounterSample.Empty;
                    next[name] = current;

                    if (!_previous.TryGetValue(name, out var before)) continue;
                    float read = Rate(before[0], current[0]) / 1024f;
                    float write = Rate(before[1], current[1]) / 1024f;
                    float latency = columns[2] != null ? Rate(before[2], current[2]) * 1000f : float.NaN;
                    float busy = columns[3] != null ? Math.Clamp(100f - Rate(before[3], current[3]), 0f, 100f) : float.NaN;
                    // a counter reset (disk removed and re-added) gives absurd values for one read
                    if (read > 10_000_000f) read = 0f;
                    if (write > 10_000_000f) write = 0f;
                    if (latency > 60_000f) latency = float.NaN;
                    result[index] = new Sample(read, write, latency, busy);
                }
                // once, and again when the set of disks changes (a USB disk plugged in): which counter instance is which disk.
                // For the USB chart that only filled at its right end — the index must match DiskHealthEntry.Index.
                string set = string.Join(", ", next.Keys.OrderBy(k => k));
                if (set != _loggedInstances)
                {
                    _loggedInstances = set;
                    AppLogger.Info($"PhysicalDisk instances: {(set.Length > 0 ? set : "(none)")}");
                }
                _previous = next;
                KnownDisks = known;
                _failures = 0;
                if (result.Count > 0) GoodReads++;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"PerDiskActivityReader: {ex.Message}");
                Fail();
            }
            return result;
        }

        private static float Rate(CounterSample before, CounterSample now)
        {
            try
            {
                float v = CounterSample.Calculate(before, now);
                return float.IsFinite(v) && v >= 0 ? v : 0f;
            }
            catch { return 0f; }
        }

        private void Fail()
        {
            if (++_failures >= 5) IsAvailable = false;
        }
    }
}
