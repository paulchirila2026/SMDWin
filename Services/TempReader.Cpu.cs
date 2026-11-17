using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{
    public partial class TempReader
    {
        // ── CPU clock plausibility ───────────────────────────────────────────────────────────────────────────────
        // On some processors (seen on a Ryzen 3 7320U) the clock read by LibreHardwareMonitor is wrong: 9–15 GHz on a chip
        // that boosts to 4.1 GHz, and in between values that are too high but look possible. Once a core clock passes the
        // ceiling of the processor, the LHM clocks are not trusted for the rest of the session: the clock is taken from
        // Windows instead (% Processor Performance × base clock), the same value Task Manager shows.

        private float _cpuClockCeilingMHz;
        private bool _lhmClocksUntrusted;
        private System.Diagnostics.PerformanceCounter? _pcPerfPct;
        private float _baseClockMHz;

        /// <summary>The highest clock the processor can reach (its boost from the database + 10 %), or 6.5 GHz when unknown.</summary>
        private static float CpuClockCeilingMHz(string cpuName)
        {
            try
            {
                if (HardwareDatabaseService.LookupCpu(cpuName)?.BoostClockGhz is double boost && boost > 0.5)
                    return (float)(boost * 1000 * 1.10 + 100);
            }
            catch (Exception ex) { AppLogger.Debug($"CpuClockCeilingMHz: {ex.Message}"); }
            return 6500f;
        }

        private void ApplyCpuClockPlausibility(TempSnapshot snap)
        {
            float ceiling = _cpuClockCeilingMHz > 0 ? _cpuClockCeilingMHz : 6500f;
            if (!_lhmClocksUntrusted && snap.CpuFreqMHz is float f && f > ceiling)
            {
                _lhmClocksUntrusted = true;
                AppLogger.Info("CPU clock from LHM is implausible ({0:F0} MHz > {1:F0} MHz ceiling) — the clock comes from Windows (% Processor Performance) for the rest of the session", f, ceiling);
            }
            if (!_lhmClocksUntrusted) return;

            snap.CoreFreqs.Clear();
            float win = ReadWindowsClockMHz();
            snap.CpuFreqMHz = win > 0 ? win : null;
        }

        /// <summary>The current clock as Windows reports it: % Processor Performance × base clock (MaxClockSpeed).</summary>
        private float ReadWindowsClockMHz()
        {
            try
            {
                if (_baseClockMHz <= 0)
                {
                    using var q = SMDWin.Services.WmiHelper.Searcher("SELECT MaxClockSpeed FROM Win32_Processor", System.TimeSpan.FromSeconds(10));
                    using var res = q.Get();
                    foreach (ManagementObject o in res) { _baseClockMHz = Convert.ToSingle(o["MaxClockSpeed"]); break; }
                }
                if (_pcPerfPct == null)
                {
                    _pcPerfPct = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    _pcPerfPct.NextValue();   // the first reading is always 0
                    return 0;
                }
                float pct = _pcPerfPct.NextValue();
                return _baseClockMHz > 0 && pct > 0 ? _baseClockMHz * pct / 100f : 0;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"ReadWindowsClockMHz: {ex.Message}");
                return 0;
            }
        }


        // ── Driver-free alternative: MSR via a CPUID fallback ────────────────────
        // On systems without WinRing0, CPUID is used for the microarchitecture and TjMax is estimated from a table, while the
        // temperature comes from LHM (normal fallback)

        public uint EstimatedTjMaxC
        {
            get
            {
                if (!_msrTjMaxDetected)
                {
                    _msrTjMax = GetTjMaxFromBrandString();
                    _msrTjMaxDetected = true;
                }
                return _msrTjMax;
            }
        }

        private uint GetTjMaxFromBrandString()
        {
            try
            {
                string cpuNameForDb = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
                var dbCpu = SMDWin.Services.HardwareDatabaseService.LookupCpu(cpuNameForDb);
                if (dbCpu?.TjMaxC is double tjmaxFromDb && tjmaxFromDb > 0)
                    return (uint)tjmaxFromDb;
            }
            catch (Exception ex) { AppLogger.Debug($"GetTjMaxFromBrandString DB lookup: {ex.Message}"); }

            try
            {
                string name = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";

                // ── AMD (Family in hexazecimal, dar PROCESSOR_IDENTIFIER il arata ZECIMAL) ──
                if (name.Contains("AuthenticAMD") || name.Contains("AMD"))
                {
                    var mFam = System.Text.RegularExpressions.Regex.Match(name, @"Family (\d+)");
                    if (mFam.Success && int.TryParse(mFam.Groups[1].Value, out int fam))
                    {
                        // Family 26 (0x1A) = Zen5 (Ryzen 9000/AI 300) — Tctl limit ~95°C
                        // Family 25 (0x19) = Zen3/Zen4 (Ryzen 5000/7000/8000) — ~90-95°C
                        //   (X3D with stacked 3D cache: lower limit, ~89-90°C, but X3D cannot be told apart from Family/Model alone without a
                        //   name table — 90°C is used, the safe/conservative value for the whole family)
                        // Family 23 (0x17) = Zen/Zen+/Zen2 (Ryzen 1000-4000) — ~95°C
                        // Family < 23 = pre-Zen (very old, Bulldozer/Excavator) — ~90-100°C
                        if (fam >= 26) return 95;
                        if (fam == 25)
                        {
                            // Family 25 mixes limits: Vermeer (Ryzen 5000 desktop, Model 33) has 90 °C on 5800X/5900X/5950X/X3D; the Cezanne APUs
                            // (5600G/5700G, Model 80), Rembrandt (Model 68), Raphael (Ryzen 7000, Model 97) and Phoenix (Model 116/117) have 95 °C.
                            // An unknown model stays at 90.
                            var amdMod = System.Text.RegularExpressions.Regex.Match(name, @"Model (\d+)");
                            if (amdMod.Success && int.TryParse(amdMod.Groups[1].Value, out int amdModel)
                                && amdModel is 80 or 68 or 97 or 116 or 117)
                                return 95;
                            return 90;
                        }
                        if (fam == 23) return 95;
                        if (fam > 0 && fam < 23) return 90;
                    }
                    return 95; // unknown AMD — a conservative value
                }

                // ── Intel — Family 6, Model → microarhitectura (tabel LHM IntelCpu.cs) ──
                var mMod = System.Text.RegularExpressions.Regex.Match(name, @"Family 6 Model (\d+)");
                if (mMod.Success && int.TryParse(mMod.Groups[1].Value, out int model))
                {
                    return model switch
                    {
                        // old Core 2 / Atom — variable TjMax, low values (~85-100)
                        15 or 23 => 100,                     // Core 2 (65/45nm)
                        28 => 90,                             // Atom (45nm)
                        // Nehalem/SandyBridge/IvyBridge — covered below
                        26 or 30 or 31 or 37 or 44 or 46 or 47 => 100, // Nehalem
                        42 or 45 => 98,                       // Sandy Bridge — 95-98°C tipic
                        58 or 62 => 105,                      // Ivy Bridge
                        60 or 69 or 70 or 63 => 100,           // Haswell
                        61 or 71 or 79 or 86 => 100,           // Broadwell
                        78 or 94 => 100,                       // Skylake
                        142 or 158 => 100,                     // Kaby Lake
                        165 or 166 => 100,                     // Comet Lake
                        140 or 141 => 100,                     // Tiger Lake
                        125 or 126 or 108 => 100,              // Ice Lake
                        151 or 154 => 100,                     // Alder Lake
                        183 or 186 or 191 => 100,              // Raptor Lake
                        172 or 170 => 105,                     // Meteor Lake
                        197 => 105,                            // Arrow Lake / Panther Lake — estimare
                        // modern Atoms (N-series, low-power) — lower TjMax, ~90-95°C
                        122 or 92 or 95 or 150 => 90,
                        _ => 100,
                    };
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            return 100; // safe default — unknown, use the most common Intel value
        }

        private void TryMsrCpuTemp(TempSnapshot snap)
        {
            // The CPU temperature comes from LHM + PawnIO (normal sensors). Stub called from Read().
            _msrPermFailed = true;
        }

        private void CloseMsrHandle()
        {
            if (_msrHandleOpened && _msrHandle != new IntPtr(-1))
            {
                try { CloseHandle(_msrHandle); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                _msrHandle = new IntPtr(-1);
                _msrHandleOpened = false;
            }
        }
    }
}
