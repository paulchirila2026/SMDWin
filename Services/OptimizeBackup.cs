using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// Keeps the ORIGINAL values of the settings changed by the Optimize window, so "Revert" restores them exactly
    /// (not fixed values). On the first apply of a setting, what was there before is saved; on revert it is read from here
    /// and the entry is removed. File: %LocalAppData%\SMDWin\optimize-backup.json
    /// </summary>
    public static class OptimizeBackup
    {
        public enum Mode { None, Apply, Restore }

        [ThreadStatic] public static Mode Current;

        public class Entry
        {
            public bool Exists { get; set; }
            public int Value { get; set; }
            public int Delayed { get; set; }
            public string Text { get; set; } = "";
        }

        private static readonly object _lock = new();
        private static readonly string _path = AppPaths.Data("optimize-backup.json");

        private static Dictionary<string, Entry> Load()
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "OptimizeBackup.Load"); }
            return new();
        }

        private static void Save(Dictionary<string, Entry> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "OptimizeBackup.Save"); }
        }

        /// <summary>Saves the previous value, only if there is no copy yet (the first value stays the original).</summary>
        public static void Remember(string key, Func<Entry> read)
        {
            lock (_lock)
            {
                var d = Load();
                if (d.ContainsKey(key)) return;
                try { d[key] = read(); Save(d); } catch (Exception ex) { AppLogger.Warning(ex, "OptimizeBackup.Remember " + key); }
            }
        }

        /// <summary>Returns and removes the copy; null if there is none.</summary>
        public static Entry? Take(string key)
        {
            lock (_lock)
            {
                var d = Load();
                if (!d.TryGetValue(key, out var e)) return null;
                d.Remove(key); Save(d);
                return e;
            }
        }

        public static string RegKey(string hive, string sub, string name) => $"reg|{hive}|{sub}|{name}";
        public static string SvcKey(string name) => "svc|" + name;
        public const string PowerKey = "power|scheme";

        public static Entry ReadReg(RegistryKey hive, string sub, string name)
        {
            using var k = hive.OpenSubKey(sub);
            var v = k?.GetValue(name);
            return v is int i ? new Entry { Exists = true, Value = i } : new Entry { Exists = false };
        }

        public static Entry ReadService(string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (k?.GetValue("Start") is not int start) return new Entry { Exists = false };
            return new Entry { Exists = true, Value = start, Delayed = k.GetValue("DelayedAutostart") is int d ? d : 0 };
        }

        /// <summary>sc command for the start type (2 auto, 3 manual, 4 disabled).</summary>
        public static string ScStartType(Entry e) => e.Value switch
        {
            2 => e.Delayed == 1 ? "delayed-auto" : "auto",
            3 => "demand",
            4 => "disabled",
            0 => "boot",
            1 => "system",
            _ => "demand",
        };
    }
}
