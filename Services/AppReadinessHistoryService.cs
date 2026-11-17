using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SMDWin.Services
{
    public static class AppReadinessHistoryService
    {
        public class Entry
        {
            public int Score { get; set; }
            public double StabilityPercent { get; set; }
            public double? HwCoveragePercent { get; set; }
            public string Rating { get; set; } = "";
            public int WarnCount { get; set; }
            public int FailCount { get; set; }
            public string MachineName { get; set; } = "";
            public DateTime TimestampUtc { get; set; }
        }

        private static string HistoryPath =>
            AppPaths.Data("SelfTest-Results", "app-readiness-history.json");

        public static void SaveResult(AppReadinessScoreCalculator.ScoreResult result, int warnCount, int failCount, string? machineName = null)
        {
            try
            {
                var list = LoadAll();
                list.Add(new Entry
                {
                    Score = result.Score,
                    StabilityPercent = result.StabilityPercent,
                    HwCoveragePercent = result.HwCoveragePercent,
                    Rating = result.Rating,
                    WarnCount = warnCount,
                    FailCount = failCount,
                    MachineName = machineName ?? Environment.MachineName,
                    TimestampUtc = DateTime.UtcNow,
                });
                // Simple cap — unlimited history is pointless; the last 500 runs are enough
                // to see the trend (same cap as the benchmark history).
                if (list.Count > 500) list = list.OrderByDescending(e => e.TimestampUtc).Take(500).ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(list,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Debug($"AppReadinessHistoryService.SaveResult: {ex.Message}"); }
        }

        public static List<Entry> LoadAll()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new List<Entry>();
                return System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryPath))
                       ?? new List<Entry>();
            }
            catch (Exception ex) { AppLogger.Debug($"AppReadinessHistoryService.LoadAll: {ex.Message}"); return new List<Entry>(); }
        }

        /// <summary>The previous run (before the current one) on the same machine —
        /// used to show the trend ("68 → 74").</summary>
        public static Entry? GetPreviousEntry(string? machineName = null)
        {
            string machine = machineName ?? Environment.MachineName;
            return LoadAll()
                .Where(e => e.MachineName == machine)
                .OrderByDescending(e => e.TimestampUtc)
                .FirstOrDefault();
        }
    }
}
