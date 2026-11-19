
using System;
using System.Collections.Generic;
using SMDWin.Services;

namespace SMDWin.Models
{
    public sealed class NvmeHealthRow
    {
        private const string Green = "#22C55E", Amber = "#F59E0B", Red = "#EF4444";

        public string IdHex { get; init; } = "";
        public string Name { get; init; } = "";
        public string CurrentValue { get; init; } = "";
        public string WorstValue { get; init; } = "";       // NVMe has no "worst" — stays empty
        public string ThresholdValue { get; init; } = "";
        public string RawDisplay { get; init; } = "";
        public string FriendlyDescription { get; init; } = "";
        public string TrafficLightColor { get; init; } = Green;

        public string StatusColor => TrafficLightColor;
        public string StatusText => TrafficLightColor != Green
            ? LanguageService.S("Smart", "Warning", "WARNING")
            : LanguageService.S("Smart", "Ok", "OK");
        public string AttributeTooltip => FriendlyDescription;
        public string RowBackground => TrafficLightColor == Red ? "#10EF4444" : TrafficLightColor == Amber ? "#10F59E0B" : "Transparent";

        private static string FmtBytesGB(long gb) => gb >= 1000 ? $"{gb / 1000.0:F1} TB" : $"{gb:N0} GB";

        public static List<NvmeHealthRow> FromReport(SmartctlReport r)
        {
            var rows = new List<NvmeHealthRow>();
            if (r == null || !r.Success || !r.IsNvme) return rows;

            void Add(int id, string name, string raw, string desc, string color = Green, string value = "", string threshold = "") =>
                rows.Add(new NvmeHealthRow
                {
                    IdHex = $"0x{id:X2}", Name = name, RawDisplay = raw, FriendlyDescription = desc,
                    TrafficLightColor = color, CurrentValue = value, ThresholdValue = threshold
                });

            // 01 — Critical Warning (bitmask; 0 = all good)
            if (r.NvmeCriticalWarning.HasValue)
            {
                int cw = r.NvmeCriticalWarning.Value;
                var bits = new List<string>();
                if ((cw & 0x01) != 0) bits.Add("spare capacity below threshold");
                if ((cw & 0x02) != 0) bits.Add("temperature over/under threshold");
                if ((cw & 0x04) != 0) bits.Add("reliability degraded");
                if ((cw & 0x08) != 0) bits.Add("media in read-only mode");
                if ((cw & 0x10) != 0) bits.Add("volatile memory backup failed");
                if ((cw & 0x20) != 0) bits.Add("persistent memory region unreliable");
                Add(0x01, "Critical Warning", $"0x{cw:X2}",
                    cw == 0 ? "No critical warning — OK" : "⚠ The drive reports: " + (bits.Count > 0 ? string.Join("; ", bits) : $"flags 0x{cw:X2}"),
                    cw == 0 ? Green : Red);
            }

            // 02 — Composite Temperature
            if (r.TemperatureCelsius.HasValue)
            {
                double t = r.TemperatureCelsius.Value;
                Add(0x02, "Composite Temperature", $"{TempUnit.Fmt(t)}",
                    $"{TempUnit.Fmt(t)} — {(t < 70 ? "normal" : t < 80 ? "warm" : "⚠ hot")}",
                    t >= 80 ? Red : t >= 70 ? Amber : Green);
            }

            // 03 / 04 — Available Spare (+ prag)
            if (r.NvmeAvailableSpareThreshold.HasValue || r.NvmeAvailableSparePct.HasValue)
            {
                int? spare = r.NvmeAvailableSparePct, thr = r.NvmeAvailableSpareThreshold;
                if (spare.HasValue)
                {
                    bool below = thr.HasValue && spare.Value <= thr.Value;
                    Add(0x03, "Available Spare", $"{spare.Value}%",
                        below ? $"⚠ Spare blocks at {spare.Value}% — at or below the drive's own threshold ({thr}%)"
                              : $"{spare.Value}% of spare blocks remaining (100% = new)",
                        below ? Red : spare.Value < 50 ? Amber : Green,
                        spare.Value.ToString(), thr.HasValue ? thr.Value.ToString() : "");
                }
                if (thr.HasValue)
                    Add(0x04, "Available Spare Threshold", $"{thr.Value}%",
                        "Spare level below which the drive raises a critical warning");
            }

            // 05 — Percentage Used
            if (r.NvmePercentageUsed.HasValue)
            {
                int p = r.NvmePercentageUsed.Value;
                Add(0x05, "Percentage Used", $"{p}%",
                    p >= 100 ? $"{p}% — the manufacturer's endurance rating has been reached (the drive can keep working, but plan a backup and replacement)"
                             : $"{p}% of the rated endurance used (estimate from the drive; 0% = new)",
                    p >= 100 ? Red : p >= 80 ? Amber : Green);
            }

            // 06 / 07 — Data Units Read / Written
            if (r.NvmeDataUnitsReadGB.HasValue)
                Add(0x06, "Data Units Read", FmtBytesGB(r.NvmeDataUnitsReadGB.Value), $"{FmtBytesGB(r.NvmeDataUnitsReadGB.Value)} read over the drive's lifetime");
            if (r.NvmeDataUnitsWrittenGB.HasValue)
                Add(0x07, "Data Units Written", FmtBytesGB(r.NvmeDataUnitsWrittenGB.Value), $"{FmtBytesGB(r.NvmeDataUnitsWrittenGB.Value)} written over the drive's lifetime");

            // 08 / 09 — comenzi host
            if (r.NvmeHostReads.HasValue)  Add(0x08, "Host Read Commands",  r.NvmeHostReads.Value.ToString("N0"),  "Read commands completed over the drive's lifetime");
            if (r.NvmeHostWrites.HasValue) Add(0x09, "Host Write Commands", r.NvmeHostWrites.Value.ToString("N0"), "Write commands completed over the drive's lifetime");

            // 0A — Controller Busy Time (minute)
            if (r.NvmeControllerBusyMinutes.HasValue)
            {
                long m = r.NvmeControllerBusyMinutes.Value;
                Add(0x0A, "Controller Busy Time", $"{m:N0} min", $"{m:N0} minutes ({m / 60.0:N0} h) the controller spent processing I/O");
            }

            // 0B / 0C — power cycles / power-on hours
            if (r.PowerCycleCount >= 0)
                Add(0x0B, "Power Cycles", r.PowerCycleCount.ToString("N0"), $"{r.PowerCycleCount:N0} power cycles (on/off)");
            if (r.PowerOnHours >= 0)
                Add(0x0C, "Power-On Hours", $"{r.PowerOnHours:N0} h", $"{r.PowerOnHours:N0} hours powered on ({r.PowerOnHours / 24:N0} days)");

            // 0D — Unsafe Shutdowns
            if (r.NvmeUnsafeShutdowns.HasValue)
                Add(0x0D, "Unsafe Shutdowns", r.NvmeUnsafeShutdowns.Value.ToString("N0"),
                    "Power lost without a clean shutdown (common on laptops; a very high count on a desktop deserves a look at the PSU)");

            // 0E — Media and Data Integrity Errors
            if (r.NvmeMediaErrors.HasValue)
            {
                long e = r.NvmeMediaErrors.Value;
                Add(0x0E, "Media and Data Integrity Errors", e.ToString("N0"),
                    e == 0 ? "No media or data integrity errors — OK" : $"⚠ {e:N0} unrecovered media/data integrity error(s) — back up and check the drive",
                    e == 0 ? Green : Red);
            }

            // 0F — Error Information Log Entries
            if (r.NvmeErrorLogEntries.HasValue)
                Add(0x0F, "Error Information Log Entries", r.NvmeErrorLogEntries.Value.ToString("N0"),
                    "Entries in the drive's error log — often harmless (e.g. invalid commands from the OS); the media-error counter is the reliable signal");

            // 10 / 11 — time spent above the temperature thresholds
            if (r.NvmeWarningTempMinutes.HasValue)
            {
                long w = r.NvmeWarningTempMinutes.Value;
                Add(0x10, "Warning Composite Temperature Time", $"{w:N0} min",
                    w == 0 ? "The drive never exceeded its warning temperature — OK" : $"⚠ {w:N0} minutes spent above the warning temperature — check cooling",
                    w == 0 ? Green : Amber);
            }
            if (r.NvmeCriticalTempMinutes.HasValue)
            {
                long c = r.NvmeCriticalTempMinutes.Value;
                Add(0x11, "Critical Composite Temperature Time", $"{c:N0} min",
                    c == 0 ? "The drive never reached its critical temperature — OK" : $"⚠ {c:N0} minutes spent above the critical temperature",
                    c == 0 ? Green : Red);
            }

            // 12 / 13 — the drive slowing itself down because of heat (thermal management)
            if (r.NvmeThrottleLightCount.HasValue || r.NvmeThrottleHeavyCount.HasValue)
            {
                long l = r.NvmeThrottleLightCount ?? 0, h = r.NvmeThrottleHeavyCount ?? 0;
                long secs = (r.NvmeThrottleLightSeconds ?? 0) + (r.NvmeThrottleHeavySeconds ?? 0);
                string time = secs >= 3600 ? $"{secs / 3600.0:0.#} h" : secs >= 60 ? $"{secs / 60} min" : $"{secs} s";
                Add(0x12, "Thermal Throttling", $"{l + h:N0}×",
                    l + h == 0 ? "The drive never slowed down because of heat — OK"
                    : h > 0 ? $"⚠ Slowed down by heat {l + h:N0} times ({h:N0} heavily), {time} in all — needs better cooling (heatsink / airflow)"
                    : $"Slowed down lightly by heat {l:N0} times, {time} in all — normal under long heavy use; often means it runs warm",
                    l + h == 0 ? Green : h > 0 ? Red : Amber);
            }

            return rows;
        }
    }
}
