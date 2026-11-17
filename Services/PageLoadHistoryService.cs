using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SMDWin.Services
{
    public static class PageLoadHistoryService
    {
        public class Entry
        {
            public string PageName { get; set; } = "";
            public double ElapsedMs { get; set; }
            public string MachineName { get; set; } = "";
            public DateTime TimestampUtc { get; set; }
        }

        private static string HistoryPath =>
            AppPaths.Data("SelfTest-Results", "page-load-history.json");

        public static void SaveResult(string pageName, double elapsedMs, string? machineName = null)
        {
            try
            {
                var list = LoadAll();
                list.Add(new Entry
                {
                    PageName = pageName,
                    ElapsedMs = elapsedMs,
                    MachineName = machineName ?? Environment.MachineName,
                    TimestampUtc = DateTime.UtcNow,
                });
                // Cap per (page, machine) — as with the benchmark, the last 100 runs per combination are more than enough for a
                // stable median, and the file does not grow forever on a development machine where the self-test runs often.
                var grouped = list.GroupBy(e => (e.PageName, e.MachineName))
                    .SelectMany(g => g.OrderByDescending(e => e.TimestampUtc).Take(100));
                list = grouped.ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(list,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Debug($"PageLoadHistoryService.SaveResult: {ex.Message}"); }
        }

        public static List<Entry> LoadAll()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new List<Entry>();
                return System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryPath))
                       ?? new List<Entry>();
            }
            catch (Exception ex) { AppLogger.Debug($"PageLoadHistoryService.LoadAll: {ex.Message}"); return new List<Entry>(); }
        }

        /// <summary>Earlier times (ms) for a page ON THE SAME MACHINE — different hardware legitimately has different load times,
        /// so mixing machines would spoil the baseline (e.g. an old HDD vs a new NVMe on the Drivers page).</summary>
        public static List<double> GetHistoryFor(string pageName, string? machineName = null)
        {
            string machine = machineName ?? Environment.MachineName;
            return LoadAll()
                .Where(e => e.PageName == pageName && e.MachineName == machine)
                .Select(e => e.ElapsedMs)
                .ToList();
        }
    }
}
