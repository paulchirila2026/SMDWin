using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// The start-type changes made to services from SMD Win (the list, the quick switches, the Startup Manager), kept in
    /// Data\service-changes.json: shown on the Services page with an Undo for each. An undone change leaves the list.
    /// </summary>
    public static class ServiceChangeLog
    {
        public sealed class Entry
        {
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public DateTime When { get; set; }
        }

        private static readonly object _lock = new();
        private static string FilePath => AppPaths.Data("service-changes.json");
        // the services being put back right now (the change itself runs on another thread, so not a thread flag)
        private static readonly HashSet<string> _undoing = new(StringComparer.OrdinalIgnoreCase);

        public static event Action? Changed;

        public static void Record(string name, string displayName, string from, string to)
        {
            lock (_lock) { if (_undoing.Contains(name)) return; }
            try
            {
                lock (_lock)
                {
                    var list = LoadUnlocked();
                    // a second change of the same service keeps the first "from": Undo goes back to how it was before SMD Win
                    var old = list.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (old != null)
                    {
                        list.Remove(old);
                        if (string.Equals(old.From, to, StringComparison.OrdinalIgnoreCase)) { SaveUnlocked(list); Changed?.Invoke(); return; }
                        from = old.From;
                    }
                    list.Insert(0, new Entry { Name = name, DisplayName = displayName, From = from, To = to, When = DateTime.Now });
                    if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
                    SaveUnlocked(list);
                }
                AppLogger.Info("Service change: {0} {1} → {2}", name, from, to);
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ServiceChangeLog.Record"); }
        }

        public static List<Entry> List()
        {
            lock (_lock) return LoadUnlocked();
        }

        /// <summary>Puts the service back to its start type from before the change; true when it worked.</summary>
        public static async System.Threading.Tasks.Task<bool> UndoAsync(Entry e, WinServicesService svc)
        {
            bool ok;
            lock (_lock) _undoing.Add(e.Name);
            try { ok = await svc.SetServiceStartTypeAsync(e.Name, e.From).ConfigureAwait(true); }
            finally { lock (_lock) _undoing.Remove(e.Name); }
            if (!ok) return false;
            lock (_lock)
            {
                var list = LoadUnlocked();
                list.RemoveAll(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
                SaveUnlocked(list);
            }
            AppLogger.Info("Service change undone: {0} back to {1}", e.Name, e.From);
            Changed?.Invoke();
            return true;
        }

        private static List<Entry> LoadUnlocked()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { AppLogger.Debug($"ServiceChangeLog load: {ex.Message}"); return new(); }
        }

        private static void SaveUnlocked(List<Entry> list)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { AppLogger.Debug($"ServiceChangeLog save: {ex.Message}"); }
        }
    }
}
