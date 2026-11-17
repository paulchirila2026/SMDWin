using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>A stress test (CPU or GPU) started from Temp &amp; Stress, as it finished.</summary>
    public class StressTestRecord
    {
        public DateTime When { get; set; }
        public int DurationSec { get; set; }
        public float MaxTempC { get; set; }
        public float AvgTempC { get; set; }
        /// <summary>Percentage of the samples in which the processor throttled (CPU only).</summary>
        public int ThrottlePct { get; set; }
        /// <summary>The most frequent throttle reason ("Thermal", "PowerLimit"…), empty if it did not throttle.</summary>
        public string ThrottleReason { get; set; } = "";
        public int IntegrityErrors { get; set; }
    }

    /// <summary>A memory test started from the RAM page.</summary>
    public class RamTestRecord
    {
        public DateTime When { get; set; }
        public bool Advanced { get; set; }
        /// <summary>Tests that found memory errors (Integrity, Walking bit, Full scan).</summary>
        public List<string> Failed { get; set; } = new();
        /// <summary>Tests that could not run to the end.</summary>
        public List<string> Incomplete { get; set; } = new();
    }

    public class TestResults
    {
        /// <summary>The computer the tests ran on (see MachineIdentity); another PC's results are not shown.</summary>
        public string MachineId { get; set; } = "";
        public StressTestRecord? Cpu { get; set; }
        public StressTestRecord? Gpu { get; set; }
        public RamTestRecord? Ram { get; set; }
    }

    /// <summary>
    /// The latest result of each test (CPU / GPU / RAM), in test-results.json in the data folder.
    /// The Health window reads it instead of running the tests itself.
    /// </summary>
    public static class TestResultStore
    {
        private static readonly object _lock = new();
        private static string FilePath => AppPaths.Data("test-results.json");

        public static TestResults Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new();
                    var t = JsonSerializer.Deserialize<TestResults>(File.ReadAllText(FilePath)) ?? new();
                    return MachineIdentity.IsThisMachine(t.MachineId) ? t : new();
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TestResultStore.Load"); return new(); }
            }
        }

        public static void SaveCpu(StressTestRecord r) => Update(t => t.Cpu = r);
        public static void SaveGpu(StressTestRecord r) => Update(t => t.Gpu = r);
        public static void SaveRam(RamTestRecord r) => Update(t => t.Ram = r);

        private static void Update(Action<TestResults> change)
        {
            lock (_lock)
            {
                try
                {
                    TestResults t;
                    try { t = File.Exists(FilePath) ? JsonSerializer.Deserialize<TestResults>(File.ReadAllText(FilePath)) ?? new() : new(); }
                    catch (Exception ex) { AppLogger.Warning(ex, "TestResultStore.Update(read)"); t = new(); }
                    if (!MachineIdentity.IsThisMachine(t.MachineId)) t = new();   // another computer: start from scratch
                    t.MachineId = MachineIdentity.Current;
                    change(t);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TestResultStore.Update"); }
            }
        }
    }
}
