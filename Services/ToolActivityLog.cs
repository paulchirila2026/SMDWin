using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>An action started from the Tools page: what, when, and what is known about the result.</summary>
    public class ToolActivityEntry
    {
        public string Tool { get; set; } = "";
        public DateTime When { get; set; }
        /// <summary>The result or a short note ("2.8 GB freed", "opened in a command window").</summary>
        public string Note { get; set; } = "";
        /// <summary>"ok", "warn" or "info" — the note colour in the list.</summary>
        public string Kind { get; set; } = "info";
    }

    /// <summary>The Tools page log ("Recent activity"), in tool-activity.json in the data folder.
    /// Keeps the last 30 entries.</summary>
    public static class ToolActivityLog
    {
        private const int MaxEntries = 30;
        private static readonly object _lock = new();
        private static string FilePath => AppPaths.Data("tool-activity.json");

        public static event Action? Changed;

        public static void Add(string tool, string note, string kind = "info")
        {
            try
            {
                lock (_lock)
                {
                    var list = LoadInternal();
                    list.Insert(0, new ToolActivityEntry { Tool = tool, When = DateTime.Now, Note = note, Kind = kind });
                    if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                }
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ToolActivityLog.Add"); }
        }

        public static List<ToolActivityEntry> List()
        {
            lock (_lock) return LoadInternal();
        }

        private static List<ToolActivityEntry> LoadInternal()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<ToolActivityEntry>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "ToolActivityLog.Load");
                return new();
            }
        }
    }
}
