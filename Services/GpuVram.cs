using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// The real amount of video memory. Windows' WMI field (Win32_VideoController.AdapterRAM) is 32-bit, so every card with 4 GB
    /// or more reads "4 GB" (a user's RTX 2080 Super with 8 GB showed 4 GB). The full value comes from DirectX (DXGI dedicated
    /// video memory) or, failing that, from the display driver's registry entry (HardwareInformation.qwMemorySize, 64-bit).
    /// Read once and kept.
    /// </summary>
    public static class GpuVram
    {
        private static Dictionary<string, long>? _byName;
        private static readonly object Gate = new();

        /// <summary>Video memory in bytes for the adapter with this name; 0 when unknown.</summary>
        public static long BytesFor(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var map = Load();
            string key = Norm(name);
            if (map.TryGetValue(key, out long v)) return v;
            foreach (var kv in map)   // "NVIDIA GeForce RTX 2080 SUPER" vs a driver name with extra words
                if (kv.Key.Contains(key) || key.Contains(kv.Key)) return kv.Value;
            return 0;
        }

        /// <summary>The better of the WMI value and the full one: the full value when it is known and larger.</summary>
        public static double GbFor(string? name, double wmiGb)
        {
            long full = BytesFor(name);
            double fullGb = full / (1024.0 * 1024 * 1024);
            return fullGb > wmiGb + 0.1 ? fullGb : wmiGb;
        }

        private static string Norm(string s) => s.Trim().ToUpperInvariant();

        private static Dictionary<string, long> Load()
        {
            lock (Gate)
            {
                if (_byName != null) return _byName;
                var map = new Dictionary<string, long>();
                // 1. DirectX
                try
                {
                    using var f = new SharpDX.DXGI.Factory1();
                    for (int i = 0; i < f.GetAdapterCount1(); i++)
                    {
                        using var a = f.GetAdapter1(i);
                        var d = a.Description1;
                        long bytes = (long)d.DedicatedVideoMemory;
                        string n = Norm(d.Description ?? "");
                        if (n.Length > 0 && bytes > 0 && (!map.TryGetValue(n, out long old) || bytes > old)) map[n] = bytes;
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"GpuVram DXGI: {ex.Message}"); }
                // 2. the display driver's registry entries (64-bit value)
                try
                {
                    using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                    if (cls != null)
                        foreach (var sub in cls.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;   // 0000, 0001 …
                            using var k = cls.OpenSubKey(sub);
                            if (k == null) continue;
                            string n = Norm(k.GetValue("DriverDesc") as string ?? "");
                            long bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                            {
                                long l => l,
                                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                                byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                                _ => 0,
                            };
                            if (n.Length > 0 && bytes > 0 && (!map.TryGetValue(n, out long old) || bytes > old)) map[n] = bytes;
                        }
                }
                catch (Exception ex) { AppLogger.Debug($"GpuVram registry: {ex.Message}"); }
                if (map.Count > 0) AppLogger.Info("GpuVram: " + string.Join(", ", System.Linq.Enumerable.Select(map, kv => $"{kv.Key} {kv.Value >> 20} MB")));
                return _byName = map;
            }
        }
    }
}
