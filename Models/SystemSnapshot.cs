using System;
using System.Collections.Generic;

namespace SMDWin.Models
{
    public class SystemSnapshot
    {
        public string Name      { get; set; } = "";
        public DateTime TakenAt { get; set; } = DateTime.Now;

        public float? CpuTempC   { get; set; }
        public float? GpuTempC   { get; set; }
        public float? CpuLoadPct { get; set; }
        public float? CpuFreqMHz { get; set; }
        public float? FanRpmAvg  { get; set; }

        public float? RamUsedPct { get; set; }
        public float? RamUsedGB  { get; set; }

        public float? DiskFreeGB { get; set; }
        public string DiskLabel  { get; set; } = "C:";

        /// <summary>Extra, optional metrics, so the format can be extended without breaking JSON files already saved
        /// (deserialization ignores unknown keys, and new ones are simply missing from old snapshots when comparing).</summary>
        public Dictionary<string, float> Extra { get; set; } = new();
    }
}
