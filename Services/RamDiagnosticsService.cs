using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    // ── Models ────────────────────────────────────────────────────────────────

    public enum DualChannelStatus { Unknown, SingleChannel, DualChannel, QuadChannel, FlexMode }

    public class RamChannelInfo
    {
        public DualChannelStatus Status      { get; set; } = DualChannelStatus.Unknown;
        public string            Description { get; set; } = "";
        public string            Advice      { get; set; } = "";
        public double            BandwidthMultiplier { get; set; } = 1.0;
    }

    public class RamTimings
    {
        /// <summary>CAS latency in clocks (e.g. 16)</summary>
        public int    CL      { get; set; }
        /// <summary>RAS-to-CAS Delay</summary>
        public int    TRCD    { get; set; }
        /// <summary>Row Precharge Time</summary>
        public int    TRP     { get; set; }
        /// <summary>Row Active Time</summary>
        public int    TRAS    { get; set; }
        /// <summary>Frequency in MHz</summary>
        public int    MHz     { get; set; }
        /// <summary>Tensiunea</summary>
        public double Voltage { get; set; }
        /// <summary>Absolute latency in nanoseconds: CL * 1000 / MHz (MHz = ConfiguredClockSpeed from WMI = half the DDR rate)</summary>
        public double AbsoluteLatencyNs => (MHz > 0 && CL > 0) ? Math.Round(CL * 1000.0 / MHz, 1) : 0;
        /// <summary>String formatat: CL16-18-18-38 @ 3200 MHz 1.35V</summary>
        public string Summary => CL > 0
            ? $"CL{CL}-{TRCD}-{TRP}-{TRAS} @ {MHz} MHz  {(Voltage > 0 ? $"{Voltage:F3}V" : "")}"
            : "—";
    }

    public class WalkingBitProgress
    {
        public int    BitIndex     { get; set; }
        public int    TotalBits    { get; set; } = 32;
        public double PercentDone  { get; set; }
        public long   ErrorCount   { get; set; }
        public string StatusText   { get; set; } = "";
        public bool   IsFinished   { get; set; }
        public bool   Passed       => IsFinished && ErrorCount == 0;
    }

    // ── Service ───────────────────────────────────────────────────────────────

    public class RamDiagnosticsService
    {
        // ── Dual Channel Detection ────────────────────────────────────────────

        /// <summary>
        /// Memory channel of each module, read from the firmware labels (DeviceLocator / BankLabel): "ChannelA-DIMM0",
        /// "P0 CHANNEL B", "DIMM_A1", "A2_DIMM". "BANK n" labels do not tell the channel (on many boards n is only the slot
        /// index) and are ignored.
        /// </summary>
        internal static string? ChannelOf(SMDWin.Models.RamEntry m)
        {
            foreach (var raw in new[] { m.SlotLabel, m.BankLabel })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string t = raw.ToUpperInvariant();
                var ch = System.Text.RegularExpressions.Regex.Match(t, @"CHANNEL\s*-?\s*([A-H0-7])(?![A-Z])");
                if (ch.Success) return ch.Groups[1].Value;
                var dimm = System.Text.RegularExpressions.Regex.Match(t, @"(?:^|[^A-Z])(?:DIMM|SLOT)?[_ -]?([A-H])[_ -]?[0-3](?:$|[^0-9])");
                if (dimm.Success) return dimm.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// Channel mode. Decided only from the channels written by the firmware; when the labels do not say, the result is
        /// Unknown (no advice) — slot position is not a reliable hint.
        /// </summary>
        public static RamChannelInfo DetectChannelMode(
            List<SMDWin.Models.RamEntry> modules)
        {
            var populated = modules.Where(m => !m.IsEmpty).ToList();
            int count = populated.Count;
            int totalSlots = modules.Count;

            if (count == 0)
                return new RamChannelInfo { Status = DualChannelStatus.Unknown, Description = "No RAM modules detected." };

            if (count == 1)
                return new RamChannelInfo
                {
                    Status = DualChannelStatus.SingleChannel,
                    Description = "Single channel — only 1 module installed.",
                    Advice = totalSlots >= 2 ? "A second matching module would double the memory speed." : "",
                    BandwidthMultiplier = 1.0,
                };

            var channels = populated.Select(ChannelOf).ToList();
            if (channels.All(c => c != null))
            {
                int distinct = channels.Distinct().Count();
                string where = FormatSlots(populated);
                if (distinct == 1)
                    return new RamChannelInfo
                    {
                        Status = DualChannelStatus.SingleChannel,
                        Description = $"Single channel — all modules are on channel {channels[0]} ({where}).",
                        Advice = "Moving one module to the other channel would double the memory speed. Check the motherboard manual for the slot order.",
                        BandwidthMultiplier = 1.0,
                    };
                if (distinct >= 4)
                    return new RamChannelInfo
                    {
                        Status = DualChannelStatus.QuadChannel,
                        Description = $"Quad channel — modules on {distinct} channels ({where}).",
                        BandwidthMultiplier = 4.0,
                    };
                bool balanced = populated.GroupBy(ChannelOf).Select(g => g.Sum(x => (double)x.CapacityBytes)).Distinct().Count() == 1;
                if (!balanced && distinct == 2)
                    return new RamChannelInfo
                    {
                        Status = DualChannelStatus.FlexMode,
                        Description = $"Flex mode — the two channels hold different amounts of memory ({where}).",
                        Advice = "Matching amounts on both channels give full dual-channel speed.",
                        BandwidthMultiplier = 1.5,
                    };
                return new RamChannelInfo
                {
                    Status = DualChannelStatus.DualChannel,
                    Description = $"Dual channel — modules on {distinct} channels ({where}).",
                    BandwidthMultiplier = 2.0,
                };
            }

            // The labels do not tell the channel. Two slots = two channels on any consumer platform.
            if (count == 2 && totalSlots == 2)
                return new RamChannelInfo { Status = DualChannelStatus.DualChannel, Description = "Dual channel — both slots populated.", BandwidthMultiplier = 2.0 };

            return new RamChannelInfo
            {
                Status = DualChannelStatus.Unknown,
                Description = $"{count} modules installed — the firmware does not say which channel each one uses.",
            };
        }

        private static string FormatSlots(List<SMDWin.Models.RamEntry> modules) =>
            string.Join(" + ", modules.Select(m =>
                !string.IsNullOrEmpty(m.SlotLabel) ? m.SlotLabel : $"Slot {m.SlotIndex + 1}"));

        // ── RAM Timings (via WMI) ─────────────────────────────────────────────

        /// <summary>
        /// Reads the RAM timings from WMI.
        /// Win32_PhysicalMemory does not expose CL/tRCD/tRP/tRAS directly, but they can be estimated from Speed and
        /// ConfiguredClockSpeed + SPD estimates.
        /// </summary>
        public static async Task<RamTimings> GetTimingsAsync(
            List<SMDWin.Models.RamEntry> modules)
        {
            return await Task.Run(() =>
            {
                var timings = new RamTimings();

                try
                {
                    // WMI Win32_PhysicalMemory — ConfiguredClockSpeed + Voltage
                    using var s = WmiHelper.Searcher(
                        "SELECT Speed, ConfiguredClockSpeed, ConfiguredVoltage, " +
                        "MinVoltage, MaxVoltage FROM Win32_PhysicalMemory");

                    int maxConfiguredMHz = 0;
                    double voltV = 0;

                    using (var __wmiTmp1 = s.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp1)
                    {
                        try
                        {
                            int cfg = Convert.ToInt32(obj["ConfiguredClockSpeed"] ?? 0);
                            if (cfg > maxConfiguredMHz) maxConfiguredMHz = cfg;
                            if (voltV == 0)
                            {
                                double v = Convert.ToDouble(obj["ConfiguredVoltage"] ?? 0) / 1000.0;
                                if (v > 0) voltV = v;
                            }
                        }
                        catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    }
                    }

                    timings.MHz = maxConfiguredMHz;
                    timings.Voltage = voltV;

                    // Real timings are not exposed in standard WMI.
                    // CL is estimated from the frequency using typical JEDEC values:
                    // DDR4-2133: CL15, DDR4-2400: CL16, DDR4-2666: CL16,
                    // DDR4-3000: CL15 (XMP) or CL16, DDR4-3200: CL16/CL18,
                    // DDR4-3600: CL16/CL18, DDR5-4800: CL40, DDR5-6000: CL36
                    (timings.CL, timings.TRCD, timings.TRP, timings.TRAS) =
                        EstimateTimings(maxConfiguredMHz, modules.FirstOrDefault()?.MemoryType ?? "");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "RamDiagnosticsService.GetTimings"); }

                return timings;
            });
        }

        private static (int cl, int tRCD, int tRP, int tRAS) EstimateTimings(int mhz, string memType)
        {
            bool isDdr5 = memType.Contains("DDR5", StringComparison.OrdinalIgnoreCase);

            if (isDdr5)
            {
                return mhz switch
                {
                    >= 7200 => (34, 34, 34, 68),
                    >= 6400 => (36, 36, 36, 76),
                    >= 6000 => (36, 36, 36, 76),
                    >= 5600 => (40, 40, 40, 80),
                    >= 5200 => (42, 42, 42, 84),
                    >= 4800 => (40, 39, 39, 77),
                    _       => (40, 40, 40, 80),
                };
            }
            else // DDR4 / DDR3
            {
                return mhz switch
                {
                    >= 4000 => (18, 22, 22, 42),
                    >= 3600 => (16, 18, 18, 38),
                    >= 3200 => (16, 18, 18, 38),
                    >= 3000 => (15, 17, 17, 35),
                    >= 2666 => (16, 18, 18, 35),
                    >= 2400 => (16, 16, 16, 39),
                    >= 2133 => (15, 15, 15, 36),
                    >= 1866 => (13, 13, 13, 32),
                    >= 1600 => (11, 11, 11, 28),
                    _       => (9,  9,  9,  24),
                };
            }
        }

        // ── Walking Bit Test ──────────────────────────────────────────────────

        /// <summary>
        /// Walking Bit Pattern test — detects coupling errors between adjacent bits.
        /// Writes 0x00000001, 0x00000002, 0x00000004... up to 0x80000000, inverting each bit and checking that the others did
        /// not change. More effective than fixed-pattern tests at finding real errors.
        /// </summary>
        public async Task RunWalkingBitTestAsync(
            int testSizeMB,
            IProgress<WalkingBitProgress> progress,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                long errors = 0;
                int totalSizeMB = Math.Max(64, Math.Min(2048, testSizeMB));
                const int TOTAL_BITS = 32;

                const int ChunkSizeMB = 128;
                int chunkCount = Math.Max(1, (int)Math.Ceiling(totalSizeMB / (double)ChunkSizeMB));
                int totalSteps = chunkCount * TOTAL_BITS;
                int stepsDone  = 0;
                int mbTested   = 0;

                for (int chunk = 0; chunk < chunkCount && !ct.IsCancellationRequested; chunk++)
                {
                    int chunkMB = Math.Min(ChunkSizeMB, totalSizeMB - chunk * ChunkSizeMB);
                    int count   = (chunkMB * 1024 * 1024) / sizeof(uint);

                    uint[]? buf;
                    try { buf = new uint[count]; }
                    catch (OutOfMemoryException)
                    {
                        progress.Report(new WalkingBitProgress
                        {
                            IsFinished = true,
                            ErrorCount = errors > 0 ? errors : -1,
                            StatusText = chunk == 0
                                ? $"⚠ Insufficient RAM for a {chunkMB} MB test chunk. Try a smaller size."
                                : $"⚠ Stopped after {mbTested} MB — insufficient RAM for the next chunk. {errors} error(s) found so far."
                        });
                        return;
                    }

                    for (int bit = 0; bit < TOTAL_BITS && !ct.IsCancellationRequested; bit++)
                    {
                        uint pattern = 1u << bit;
                        stepsDone++;

                        progress.Report(new WalkingBitProgress
                        {
                            BitIndex    = bit,
                            TotalBits   = TOTAL_BITS,
                            PercentDone = stepsDone * 100.0 / totalSteps,
                            ErrorCount  = errors,
                            StatusText  = chunkCount > 1
                                ? $"Chunk {chunk + 1}/{chunkCount} · bit {bit + 1}/{TOTAL_BITS}: pattern 0x{pattern:X8}…"
                                : $"Walking bit {bit + 1}/{TOTAL_BITS}: pattern 0x{pattern:X8}…"
                        });

                        // Pass 1: scrie pattern
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                            buf[i] = pattern;

                        Thread.MemoryBarrier();

                        // Pass 2: check the pattern, write the complement
                        uint complement = ~pattern;
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                        {
                            if (buf[i] != pattern)
                                Interlocked.Increment(ref errors);
                            buf[i] = complement;
                        }

                        Thread.MemoryBarrier();

                        // Pass 3: check the complement
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                        {
                            if (buf[i] != complement)
                                Interlocked.Increment(ref errors);
                        }
                    }

                    mbTested += chunkMB;
                    buf = null;
                }

                bool passed = errors == 0 && !ct.IsCancellationRequested;
                progress.Report(new WalkingBitProgress
                {
                    BitIndex    = TOTAL_BITS,
                    TotalBits   = TOTAL_BITS,
                    PercentDone = 100,
                    ErrorCount  = errors,
                    IsFinished  = true,
                    StatusText  = ct.IsCancellationRequested
                        ? $"⚪ Test cancelled. Errors found so far: {errors}"
                        : passed
                            ? $"✅ Walking Bit Test PASSED — no errors in {totalSizeMB} MB"
                            : $"❌ Walking Bit Test FAILED — {errors} error(s) detected in {totalSizeMB} MB"
                });
            }, ct);
        }

        // ── Rowhammer Vulnerability Info ──────────────────────────────────────

        /// <summary>
        /// Returns information about Rowhammer vulnerability for the current configuration. Performs no real attack —
        /// informational only.
        /// </summary>
        public static (bool likelyVulnerable, string info, string mitigation) GetRowhammerInfo(
            List<SMDWin.Models.RamEntry> modules, int refreshRateMs = 0)
        {
            var nonEmpty = modules.Where(m => !m.IsEmpty).ToList();
            if (!nonEmpty.Any())
                return (false, "No RAM detected.", "");

            string memType = nonEmpty.FirstOrDefault()?.MemoryType ?? "";
            int mhz = nonEmpty.FirstOrDefault()?.ConfiguredMHz ?? 0;
            bool isDdr4 = memType.Contains("DDR4", StringComparison.OrdinalIgnoreCase);
            bool isDdr3 = memType.Contains("DDR3", StringComparison.OrdinalIgnoreCase);
            bool isDdr5 = memType.Contains("DDR5", StringComparison.OrdinalIgnoreCase);
            bool isEcc  = nonEmpty.Any(m => m.IsEcc);

            // DDR5 has improved TRR (Target Row Refresh) — harder to exploit
            // DDR4 / LPDDR4 with TRR/pTRR are partly mitigated
            // DDR3 and older DDR4 without TRR are the most vulnerable
            bool likelyVulnerable = isDdr3 || (isDdr4 && !isEcc);

            string info;
            if (isDdr5)
                info = "DDR5 includes improved TRR (Target Row Refresh) — significantly reduced Rowhammer risk.";
            else if (isEcc)
                info = "ECC RAM detected — ECC can correct single-bit Rowhammer flips, reducing impact.";
            else if (isDdr4)
                info = "DDR4 without ECC — potential Rowhammer vulnerability. Modern DDR4 has partial TRR mitigation but is not fully immune.";
            else if (isDdr3)
                info = "DDR3 without ECC — most vulnerable to Rowhammer attacks. No hardware mitigation.";
            else
                info = "RAM type unknown — Rowhammer vulnerability status cannot be determined.";

            string mitigation = isEcc
                ? "ECC provides partial protection."
                : isDdr5
                    ? "DDR5 TRR provides strong hardware mitigation."
                    : "Keep OS and BIOS updated. Use ECC RAM for high-security environments.";

            return (likelyVulnerable, info, mitigation);
        }

        // ── Memory Map (RAM usage breakdown) ─────────────────────────────────


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
