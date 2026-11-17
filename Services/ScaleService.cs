using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    public static class ScaleService
    {
        // Standard keys — used consistently by Dashboard/Storage/Network/widget.
        public const string KeyNetDown = "NetDown";
        public const string KeyNetUp   = "NetUp";
        public static string KeyDiskRead(string drive)  => $"DiskRead:{drive}";
        public static string KeyDiskWrite(string drive) => $"DiskWrite:{drive}";

        // Decay applied on every Observe() that does NOT set a new maximum — slow enough not to be visible from one tick to the
        // next (ticks are 1-2 s), but halving a peak that is not reached again within a few hours of normal use.
        private const float DecayFactor = 0.9995f;

        private const float NetDecayFactor = 0.9992f;

        private const float DiskDecayFactor = 0.9985f;

        private const float MinFloorNet  = 8f * 1024f;  // 8 MB/s
        private const float MinFloorDisk = 8f * 1024f;  // 8 MB/s

        public const float DefaultNetMax = 8f * 1024f; // 8 MB/s — seed before the first measurement

        private static DateTime _lastSave = DateTime.MinValue;
        private static bool     _dirty    = false;

        /// <summary>Initial seed for a disk, by the drive type already detected by HardwareService (MediaType: "SSD"/"HDD"/"USB").
        /// Used only while there is no persisted measurement for that drive yet.</summary>
        public static float DefaultDiskMax(string? mediaType) => mediaType switch
        {
            "SSD" => 550f  * 1024f,  // seed SATA-SSD-class; NVMe drives raise the ceiling by themselves
            "HDD" => 180f  * 1024f,
            "USB" => 40f   * 1024f,
            _     => 550f  * 1024f,
        };

        /// <summary>Reports a new value (KB/s) for a metric and returns the current recommended ceiling for scaling the chart/bar.
        /// Safe to call on every tick — reads are in memory, saving to disk is throttled (see MaybeSave).</summary>
        public static float Observe(string key, float valueKBs, float minFloor = MinFloorNet)
        {
            if (valueKBs < 0 || float.IsNaN(valueKBs) || float.IsInfinity(valueKBs))
                return GetScale(key, DefaultNetMax);

            var dict = SettingsService.Current.ScaleMaxObserved;
            float current = dict.TryGetValue(key, out var v) ? v : 0f;
            float updated;

            // Only >2% above the current maximum counts as "growth" — avoids rewrites for insignificant measurement noise.
            if (valueKBs > current * 1.02f)
            {
                updated = valueKBs;
                _dirty = true;
            }
            else
            {
                float decay = key.StartsWith("DiskRead:") || key.StartsWith("DiskWrite:")
                    ? DiskDecayFactor
                    : (key == KeyNetDown || key == KeyNetUp) ? NetDecayFactor
                    : DecayFactor;
                updated = current > minFloor ? current * decay : minFloor;
            }

            dict[key] = updated;
            MaybeSave();
            return updated;
        }

        /// <summary>Reads the current ceiling for a metric without reporting a new value — useful when building the UI
        /// (e.g. the widget at Loaded) before the first tick.</summary>
        public static float GetScale(string key, float defaultMax)
        {
            var dict = SettingsService.Current.ScaleMaxObserved;
            return dict.TryGetValue(key, out var v) && v > 1f ? v : defaultMax;
        }

        private static void MaybeSave()
        {
            if (!_dirty) return;
            // Throttle — at most one disk write every 20 s, even if Observe() is called many times per second from several sources
            // (Dashboard + Storage + widget).
            if ((DateTime.Now - _lastSave).TotalSeconds < 20) return;
            SettingsService.Save();
            _lastSave = DateTime.Now;
            _dirty = false;
        }
    }
}
