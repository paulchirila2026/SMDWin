using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Central validation of the values read from sensors.
    /// Rule: a physically impossible value (NaN, infinity, out of range) becomes "missing" (null), never 0 and never an
    /// arbitrary value. All sources (LHM, MSR, WMI, nvidia-smi, AMD, IGCL) go through the same filter.
    /// </summary>
    public static class SensorValidation
    {
        // Wide physical limits: only impossible values are filtered, not merely high ones.
        public const float MinTempC      = 5f;      // sub 5°C = senzor necitit / 0 raportat gresit
        public const float MaxTempC      = 125f;    // above 125°C = a misread register (e.g. 127, 255)
        public const float MaxFanRpm     = 20000f;
        public const float MaxClockMHz   = 10000f;
        public const float MaxPowerW     = 1500f;
        public const float MaxVoltage    = 3f;      // VCore
        public const float MaxBatteryV   = 60f;
        public const float MaxVramMB     = 262144f; // 256 GB

        public static bool IsPlausibleTemp(float v) => float.IsFinite(v) && v > MinTempC && v < MaxTempC;

        public static float? Temp(float? v) => v.HasValue && IsPlausibleTemp(v.Value) ? v : null;

        public static float? Pct(float? v)
        {
            if (!v.HasValue || !float.IsFinite(v.Value)) return null;
            // LHM/drivers sometimes report slightly above 100% (sampling glitches) — clamp; above 110% the value is clearly corrupt
            // and treated as missing.
            if (v.Value < 0f || v.Value > 110f) return null;
            return Math.Clamp(v.Value, 0f, 100f);
        }

        public static float? Range(float? v, float min, float max)
            => v.HasValue && float.IsFinite(v.Value) && v.Value >= min && v.Value <= max ? v : null;

        /// <summary>Cleans a complete snapshot in place (called on exit from TempReader.ReadCore).</summary>
        public static void Sanitize(TempSnapshot s)
        {
            if (s == null) return;

            s.CpuTemp           = Temp(s.CpuTemp);
            s.GpuTemp           = Temp(s.GpuTemp);
            s.VrmTemp           = Temp(s.VrmTemp);
            s.ChipsetTemp       = Temp(s.ChipsetTemp);
            s.NvmeTemp          = Temp(s.NvmeTemp);
            s.GpuHotSpot        = Temp(s.GpuHotSpot);
            s.GpuVramTemp       = Temp(s.GpuVramTemp);
            s.IntegratedGpuTemp = Temp(s.IntegratedGpuTemp);

            s.CpuLoadPct           = Pct(s.CpuLoadPct);
            s.GpuLoadPct           = Pct(s.GpuLoadPct);
            s.IntegratedGpuLoadPct = Pct(s.IntegratedGpuLoadPct);

            s.CpuFreqMHz      = Range(s.CpuFreqMHz,    1f, MaxClockMHz);
            s.GpuFreqMHz      = Range(s.GpuFreqMHz,    1f, MaxClockMHz);
            s.GpuMemFreqMHz   = Range(s.GpuMemFreqMHz, 1f, MaxClockMHz * 4f);
            s.CpuPowerW       = Range(s.CpuPowerW,       0f, MaxPowerW);
            s.CpuCoresPowerW  = Range(s.CpuCoresPowerW,  0f, MaxPowerW);
            s.CpuUncorePowerW = Range(s.CpuUncorePowerW, 0f, MaxPowerW);
            s.GpuPowerW       = Range(s.GpuPowerW,       0f, MaxPowerW);
            s.GpuMemUsedMB    = Range(s.GpuMemUsedMB,    0f, MaxVramMB);
            // under 0.3 V is not a working core voltage (Ryzen mobile reports 0.1–0.3 V for parked cores): no value instead
            s.CpuVCoreV       = Range(s.CpuVCoreV,     0.3f, MaxVoltage);
            s.BatteryLiveW       = Range(s.BatteryLiveW, -MaxPowerW, MaxPowerW); // semn = incarcare/descarcare
            s.BatteryLiveVoltage = Range(s.BatteryLiveVoltage, 0.1f, MaxBatteryV);

            s.FanSpeeds.RemoveAll(f => !float.IsFinite(f.Rpm) || f.Rpm < 0f || f.Rpm > MaxFanRpm);
            s.CoreFreqs.RemoveAll(f => !float.IsFinite(f) || f <= 0f || f > MaxClockMHz);
            s.CoreTemps.RemoveAll(t => !IsPlausibleTemp(t));
            s.CoreTempsDetailed.RemoveAll(c => c == null || !IsPlausibleTemp(c.Temp));

            foreach (var g in s.Gpus)
            {
                if (g == null) continue;
                g.Temp       = Temp(g.Temp);
                g.HotSpot    = Temp(g.HotSpot);
                g.VramTemp   = Temp(g.VramTemp);
                g.LoadPct    = Pct(g.LoadPct);
                g.PowerW     = Range(g.PowerW, 0f, MaxPowerW);
                g.FreqMHz    = Range(g.FreqMHz, 1f, MaxClockMHz);
                g.MemFreqMHz = Range(g.MemFreqMHz, 1f, MaxClockMHz * 4f);
                g.MemUsedMB  = Range(g.MemUsedMB, 0f, MaxVramMB);
            }
        }

        /// <summary>
        /// Maximum age accepted for a snapshot shown as "live" (at least 10 s, so a slow but working LHM read is not mistaken for
        /// a stuck sensor). Beyond it the data is treated as missing (UI: "—", chart: gap), not as the last known value.
        /// </summary>
        public static TimeSpan MaxLiveAge(TimeSpan refreshInterval)
        {
            var three = TimeSpan.FromTicks(refreshInterval.Ticks * 3);
            return three > TimeSpan.FromSeconds(10) ? three : TimeSpan.FromSeconds(10);
        }
    }
}
