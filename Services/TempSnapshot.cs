using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{
    public class TempSnapshot
    {
        public float? CpuTemp      { get; set; }
        public float? GpuTemp      { get; set; }
        public string CpuSensorName{ get; set; } = "--";
        public string GpuSensorName{ get; set; } = "--";
        public string Backend      { get; set; } = "none";
        public bool   CpuTempStale { get; set; } = false;

        /// <summary>The moment (UTC) the values were actually read from the hardware.
        /// DateTime.MinValue = empty snapshot, no real read.</summary>
        public DateTime CapturedUtc { get; set; } = DateTime.MinValue;

        public TimeSpan Age => CapturedUtc == DateTime.MinValue
            ? TimeSpan.MaxValue
            : DateTime.UtcNow - CapturedUtc;

        public bool IsFresh(TimeSpan maxAge) => Age <= maxAge;

        /// <summary>Copy without measured values (keeps only the backend and the sensor names).
        /// Used when the data is too old to be shown as "live".</summary>
        public TempSnapshot WithoutMeasurements() => new TempSnapshot
        {
            Backend           = Backend,
            CpuSensorName     = CpuSensorName,
            GpuSensorName     = GpuSensorName,
            IntegratedGpuName = IntegratedGpuName,
            HasIntegratedGpu  = HasIntegratedGpu,
            CapturedUtc       = CapturedUtc,
        };

        // Fan speeds (RPM)
        public List<(string Name, float Rpm)> FanSpeeds { get; set; } = new();

        // CPU frequencies & power
        public float? CpuFreqMHz   { get; set; }   // effective/max core clock
        public float? CpuPowerW    { get; set; }   // CPU package power
        public float? CpuLoadPct   { get; set; }   // total CPU load %
        public List<float> CoreFreqs { get; set; } = new(); // per-core MHz

        public List<float> CoreTemps { get; set; } = new(); // per-core °C

        public List<CoreTempEntry> CoreTempsDetailed { get; set; } = new();

        public float? VrmTemp      { get; set; }   // VRM / MOSFET temperature
        public float? ChipsetTemp  { get; set; }   // Chipset / PCH temperature
        public float? NvmeTemp     { get; set; }   // NVMe SSD temperature (from LHM Storage node)
        public float? GpuHotSpot   { get; set; }   // GPU Hot Spot / Junction temperature
        public float? GpuVramTemp  { get; set; }
        public float? BatteryLiveW       { get; set; }
        public float? BatteryLiveVoltage { get; set; }

        // GPU frequencies & power
        public float? GpuFreqMHz   { get; set; }   // GPU core clock
        public float? GpuPowerW    { get; set; }   // GPU power draw
        public float? GpuLoadPct   { get; set; }   // GPU load %
        public float? GpuMemFreqMHz{ get; set; }   // GPU memory clock
        public float? GpuMemUsedMB { get; set; }   // GPU VRAM used

        // Voltages
        public float? CpuVCoreV    { get; set; }   // CPU VCore voltage
        // motherboard voltages (Super I/O chip), only if plausible — otherwise null
        // (many boards report an uncalibrated "+12V", e.g. 1.5 V; an absurd value is not shown).
        public float? Rail12V      { get; set; }
        public float? Rail5V       { get; set; }
        public float? Rail3V3      { get; set; }
        public float? CmosBatteryV { get; set; }   // the CMOS battery (VBAT), ~3.0 V when new

        public float? CpuCoresPowerW  { get; set; }  // CPU Cores power sub-domain
        public float? CpuUncorePowerW { get; set; }  // CPU Uncore (cache/IMC) power sub-domain

        // Throttling
        public bool   CpuThrottled { get; set; }
        public bool   GpuThrottled { get; set; }
        public string ThrottleReason { get; set; } = "";

        public bool   HasIntegratedGpu     { get; set; } = false;
        public float? IntegratedGpuTemp    { get; set; }
        public float? IntegratedGpuLoadPct { get; set; }
        public string IntegratedGpuName    { get; set; } = "--";

        public List<GpuNodeSnapshot> Gpus { get; set; } = new();
    }

    /// <summary>Readings of a SINGLE GPU hardware node (one physical graphics card — Nvidia, AMD or Intel), isolated from
    /// the other cards in the system. HasReading==false means the card is PHYSICALLY present but has no valid reading NOW
    /// (typically a dedicated Optimus/hybrid card asleep at idle) — the UI must show the row as "--"/idle, NOT quietly show
    /// another card's data under this label.</summary>
    public class GpuNodeSnapshot
    {
        public string Name         { get; set; } = "--";
        public HardwareType Kind   { get; set; }
        public bool   IsDiscrete   => Kind == HardwareType.GpuNvidia || Kind == HardwareType.GpuAmd;
        public float? Temp         { get; set; }
        public float? HotSpot      { get; set; }
        public float? VramTemp     { get; set; }
        public float? LoadPct      { get; set; }
        public float? PowerW       { get; set; }
        public float? FreqMHz      { get; set; }
        public float? MemFreqMHz   { get; set; }
        public float? MemUsedMB    { get; set; }
        public bool   HasReading   => Temp.HasValue || LoadPct.HasValue || PowerW.HasValue;
    }

    public class CoreTempEntry
    {
        public int   Index { get; set; } // 0-based — 0 = "CPU Core #1" from LHM
        public float Temp  { get; set; }
        public CpuTopologyService.CoreKind Kind { get; set; } = CpuTopologyService.CoreKind.Unknown;
    }

    /// <summary>Which LHM hardware nodes are currently enabled.</summary>
    public enum LhmSensorMode
    {
        /// <summary>CPU + GPU only — fastest, for dashboard idle monitoring.</summary>
        CpuGpuOnly,
        /// <summary>All sensors — CPU, GPU, Motherboard (SuperIO), Memory — for stress/temp tab.</summary>
        Full,
        /// <summary>CPU only — used while GPU Stress runs: avoids concurrent access to the GPU sensors
        /// (LHM + D3D11 stress at once → blocks the Boot Camp driver → LHM Read slow, 8-19 s).</summary>
        CpuOnly
    }
}
