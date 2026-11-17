using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SMDWin.Services
{
    public static class BenchmarkHistoryService
    {
        public class Entry
        {
            public string CpuModel { get; set; } = "";
            public int Cores { get; set; }
            public int CompositeScore { get; set; }
            public string Rating { get; set; } = "";
            public double Sha256HashesPerSec { get; set; }
            public double MandelbrotPixPerSec { get; set; }
            public double FftOpsPerSec { get; set; }
            public DateTime TimestampUtc { get; set; }
            public string MachineName { get; set; } = "";
        }

        private static string HistoryPath =>
            AppPaths.Data("SelfTest-Results", "benchmark-history.json");

        public static void SaveResult(string cpuModel, int cores, int compositeScore, string rating,
            double sha256, double mandelbrot, double fft)
        {
            try
            {
                var list = LoadAll();
                list.Add(new Entry
                {
                    CpuModel = cpuModel, Cores = cores, CompositeScore = compositeScore, Rating = rating,
                    Sha256HashesPerSec = sha256, MandelbrotPixPerSec = mandelbrot, FftOpsPerSec = fft,
                    TimestampUtc = DateTime.UtcNow, MachineName = Environment.MachineName,
                });
                // Simple cap — the file must not grow forever; the last 500 runs are more than enough for a stable percentile.
                if (list.Count > 500) list = list.OrderByDescending(e => e.TimestampUtc).Take(500).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(list,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Debug($"BenchmarkHistoryService.SaveResult: {ex.Message}"); }
        }

        public static List<Entry> LoadAll()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new List<Entry>();
                return System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryPath))
                       ?? new List<Entry>();
            }
            catch (Exception ex) { AppLogger.Debug($"BenchmarkHistoryService.LoadAll: {ex.Message}"); return new List<Entry>(); }
        }

        /// <summary>Real percentile computed from the local history (not an invented number) — "better than X% of the runs
        /// so far". Returns null when there is not enough data yet (under 5 runs) for a meaningful percentile.</summary>
        public static int? GetPercentile(int compositeScore)
        {
            var all = LoadAll();
            if (all.Count < 5) return null;
            int better = all.Count(e => e.CompositeScore < compositeScore);
            return (int)Math.Round(100.0 * better / all.Count);
        }

        /// <summary>Earlier runs for EXACTLY the same CPU model — useful as a consistency check ("the same CPU scored 320
        /// last time, now 180 — possible throttling or a new problem"), not only as a general ranking.</summary>
        public static List<Entry> GetSameModelHistory(string cpuModel) =>
            LoadAll().Where(e => e.CpuModel == cpuModel)
                      .OrderByDescending(e => e.TimestampUtc)
                      .ToList();

        public static List<Entry> GetSameMachineHistory(string machineName) =>
            LoadAll().Where(e => e.MachineName == machineName)
                      .OrderByDescending(e => e.TimestampUtc)
                      .ToList();
    }
}
