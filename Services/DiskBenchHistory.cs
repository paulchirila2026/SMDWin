using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// History of Storage → Benchmark results per disk, for the "vs last run" comparison.
    /// File: disk-benchmark-history.json in the data folder (AppPaths.Data), portable like the rest of the data.
    /// Disk key = the name shown in the disk list (model + capacity), stable between runs.
    /// The last 20 runs per disk are kept.
    /// </summary>
    public static class DiskBenchHistory
    {
        public sealed class Entry
        {
            public string   DiskKey     { get; set; } = "";
            public DateTime Date        { get; set; }
            public double   SeqReadMBps  { get; set; }
            public double   SeqWriteMBps { get; set; }
            public long     RandReadIops  { get; set; }
            public long     RandWriteIops { get; set; }
            public double   LatencyP50Us  { get; set; }
            public bool     QuickMode     { get; set; }
        }

        private const int MaxPerDisk = 20;
        private static readonly object _lock = new();
        private static string FilePath => AppPaths.Data("disk-benchmark-history.json");

        private static List<Entry> LoadAll()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Entry>();
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>();
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "DiskBenchHistory.LoadAll");
                return new List<Entry>();
            }
        }

        /// <summary>Last saved run for the disk (before the current one), or null.</summary>
        public static Entry? GetLast(string diskKey, bool quickMode)
        {
            if (string.IsNullOrWhiteSpace(diskKey)) return null;
            lock (_lock)
            {
                // Only runs of the same kind are compared (Quick and Full use different test files).
                return LoadAll()
                    .Where(e => e.DiskKey == diskKey && e.QuickMode == quickMode)
                    .OrderByDescending(e => e.Date)
                    .FirstOrDefault();
            }
        }

        public static void Add(string diskKey, BenchmarkResult r)
        {
            if (string.IsNullOrWhiteSpace(diskKey) || r == null) return;
            lock (_lock)
            {
                try
                {
                    var all = LoadAll();
                    all.Add(new Entry
                    {
                        DiskKey = diskKey, Date = DateTime.Now,
                        SeqReadMBps = r.ReadAvgMBps, SeqWriteMBps = r.WriteAvgMBps,
                        RandReadIops = r.ReadIOPS, RandWriteIops = r.WriteIOPS,
                        LatencyP50Us = r.LatencyP50Us, QuickMode = r.IsQuickMode,
                    });
                    // Keep only the last MaxPerDisk runs for each disk
                    all = all.GroupBy(e => e.DiskKey)
                             .SelectMany(g => g.OrderByDescending(e => e.Date).Take(MaxPerDisk))
                             .OrderBy(e => e.Date)
                             .ToList();
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DiskBenchHistory.Add"); }
            }
        }
    }
}
