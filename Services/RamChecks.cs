using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text.RegularExpressions;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// What is worth knowing about the installed memory, beyond what Windows lists:
    ///  • the speed the modules were made for (decoded from the part number: XMP / EXPO kits report only their JEDEC speed to
    ///    Windows) against the speed they run at — "XMP / EXPO off" is the most common cause of slow memory on desktops;
    ///  • different modules mixed together;
    ///  • the result of the last Windows Memory Diagnostic run (scheduled from Tools);
    ///  • memory errors the hardware corrected (WHEA) — a module starting to fail, before the blue screens.
    /// Read-only.
    /// </summary>
    public static class RamChecks
    {
        private static string PT(string en, params object?[] a) => a.Length == 0 ? LiteralTexts.Translate(en) : string.Format(LiteralTexts.Translate(en), a);

        // Samsung / SK hynix / Micron JEDEC modules carry the speed as a code after the dash
        private static readonly Dictionary<string, int> SamsungCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CPB"] = 2133, ["CRC"] = 2400, ["CTD"] = 2666, ["CVF"] = 2933, ["CWE"] = 3200, ["CQK"] = 4800, ["CWM"] = 5600, ["CXK"] = 5600,
        };
        private static readonly Dictionary<string, int> HynixCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TF"] = 2133, ["UH"] = 2400, ["VK"] = 2666, ["WM"] = 2933, ["XN"] = 3200,
        };

        /// <summary>The speed (MT/s) the module is sold for, read from its part number; null = unknown format.</summary>
        public static int? RatedFromPart(string? part)
        {
            string p = (part ?? "").Trim().ToUpperInvariant();
            if (p.Length < 6) return null;
            // the short codes mean the usual JEDEC steps: "26" = 2666, "29" = 2933
            int? Ok(int v) => (v switch { 2600 or 2660 => 2666, 2900 or 2930 => 2933, 3460 => 3466, 1860 => 1866, 1060 => 1066, 1330 => 1333, _ => v }) is var n && n is >= 800 and <= 12000 ? n : null;
            Match m;
            // Kingston FURY / HyperX: KF432C16…, HX436C18… → generation digit + hundreds of MT/s
            if ((m = Regex.Match(p, @"^(?:KF|HX)(\d)(\d{2})C\d")).Success) return Ok(int.Parse(m.Groups[2].Value) * 100);
            // Kingston ValueRAM: KVR32N22S8/8 → 3200
            if ((m = Regex.Match(p, @"^KVR(\d{2})[NSERL]")).Success) return Ok(int.Parse(m.Groups[1].Value) * 100);
            // G.Skill: F4-3200C16D…, F5-6000J3038…
            if ((m = Regex.Match(p, @"^F[345]-(\d{4})[CJ]")).Success) return Ok(int.Parse(m.Groups[1].Value));
            // Crucial: CT8G4DFS832A (DDR4) / CT16G56C46U5 (DDR5) / Ballistix BL2K8G32C16U4B
            if ((m = Regex.Match(p, @"^CT\d+G(\d{2})C\d{2}")).Success) return Ok(int.Parse(m.Groups[1].Value) * 100);
            if ((m = Regex.Match(p, @"^CT\d+G4[A-Z]+\d?(\d{2})[A-Z]")).Success) return Ok(int.Parse(m.Groups[1].Value) * 100);
            if ((m = Regex.Match(p, @"^BL\w*?G(\d{2})C\d{2}")).Success) return Ok(int.Parse(m.Groups[1].Value) * 100);
            // Micron: MTA8ATF1G64AZ-3G2E1 → 3200; DDR5 …-4800 / -5600
            if ((m = Regex.Match(p, @"^MTC?\w+-(\d)G(\d)")).Success) return Ok(int.Parse(m.Groups[1].Value) * 1000 + int.Parse(m.Groups[2].Value) * 100);
            if ((m = Regex.Match(p, @"^MTC?\w+-(4800|5200|5600|6000|6400)")).Success) return Ok(int.Parse(m.Groups[1].Value));
            // Samsung: M378A1K43CB2-CTD
            if ((m = Regex.Match(p, @"^M\d{3}[AR]\w+-([A-Z]{3})")).Success && SamsungCodes.TryGetValue(m.Groups[1].Value, out int sv)) return sv;
            // SK hynix: HMA81GS6CJR8N-XN
            if ((m = Regex.Match(p, @"^HMA\w+-([A-Z]{2})")).Success && HynixCodes.TryGetValue(m.Groups[1].Value, out int hv)) return hv;
            // Patriot: PVS416G320C6 → 3200, PSD48G266681 → 2666
            if ((m = Regex.Match(p, @"^P[A-Z]{2}\d\d+G(\d{3})C\d")).Success) return Ok(int.Parse(m.Groups[1].Value) * 10);
            if ((m = Regex.Match(p, @"^PSD\d\d+G(\d{4})")).Success) return Ok(int.Parse(m.Groups[1].Value));
            // Corsair (CMK16GX4M2B3200C16), TeamGroup (…3200HC16), ADATA (AX4U32008G16A) and most others: "3200C16" / "3200HC16"
            if ((m = Regex.Match(p, @"(?<!\d)([2-9]\d{3})H?C\d{2}")).Success) return Ok(int.Parse(m.Groups[1].Value));
            if ((m = Regex.Match(p, @"^AX[45]U(\d{4})")).Success) return Ok(int.Parse(m.Groups[1].Value));
            return null;
        }

        /// <summary>
        /// The speed the memory runs at, in MT/s. Windows reports MT/s on recent systems and the clock (half) on some older
        /// ones: when the module's own speed is about double, the reported value is the clock.
        /// </summary>
        public static int RunningMts(int configured, int reference)
        {
            if (configured <= 0) return 0;
            if (reference > 0 && Math.Abs(configured * 2 - reference) <= reference * 0.06) return configured * 2;
            return configured;
        }

        /// <summary>The XMP / EXPO cell: text, explanation, colour key. Null when nothing can be said.</summary>
        public static (string Text, string Tip, string Key)? XmpState(List<RamEntry> modules)
        {
            var m = modules.FirstOrDefault(x => !x.IsEmpty && x.ConfiguredMHz > 0);
            if (m == null) return null;
            int? part = RatedFromPart(m.PartNumber);
            int jedec = m.RatedMHz;
            int running = RunningMts(m.ConfiguredMHz, part ?? jedec);
            bool laptop = (m.FormFactor ?? "").Contains("SODIMM", StringComparison.OrdinalIgnoreCase) || (m.FormFactor ?? "").Contains("SO-DIMM", StringComparison.OrdinalIgnoreCase);
            if (part is int p && p > running * 1.03)
                return laptop
                    ? (PT("Below its speed ({0} of {1})", running, p), PT("The module is made for {0} MT/s but runs at {1}. Laptops usually limit the memory speed to what the processor supports — normal, nothing to change.", p, running), "TextSecondaryBrush")
                    : (PT("Off — runs at {0} of {1}", running, p), PT("The modules are made for {0} MT/s (XMP / EXPO profile) but run at {1}. Turning on XMP / EXPO in the BIOS gives the full speed, if the processor and the board support it.", p, running), "StatusWarnBrush");
            if (part is int p2 && jedec > 0 && p2 > jedec * 1.03 && running >= p2 * 0.97)
                return (PT("On ({0} MT/s)", running), PT("The modules run at their XMP / EXPO speed, {0} MT/s.", running), "AccentBrush");
            if (part is int p3 && running >= p3 * 0.97)
                return (PT("Full speed ({0} MT/s)", running), PT("The modules run at the speed they are made for ({0} MT/s).", running), "StatusOkBrush");
            if (jedec > 0 && running < jedec * 0.97)
                return (PT("Below its speed ({0} of {1})", running, jedec), PT("The module reports {0} MT/s but runs at {1} — often a limit of the processor or the board, sometimes a BIOS setting.", jedec, running), "TextSecondaryBrush");
            if (jedec > 0)
                return (PT("Standard speed ({0} MT/s)", running), PT("The modules run at their standard (JEDEC) speed. Whether they also have a faster XMP / EXPO profile cannot be read from Windows for this part number."), "TextSecondaryBrush");
            return null;
        }

        // ── the Windows Memory Diagnostic result and corrected memory errors (System log) ───────────────────────────────
        public sealed record MdResult(DateTime When, bool Errors);

        public static MdResult? LastWindowsMemoryTest()
        {
            try
            {
                string q = "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202 or EventID=1101 or EventID=1102)]]";
                using var r = new EventLogReader(new EventLogQuery("System", PathType.LogName, q) { ReverseDirection = true });
                using var e = r.ReadEvent();
                if (e == null) return null;
                // 1201 / 1101: no errors; 1202 / 1102: errors found
                return new MdResult(e.TimeCreated ?? DateTime.MinValue, e.Id is 1202 or 1102);
            }
            catch (Exception ex) { AppLogger.Debug($"RamChecks mdsched: {ex.Message}"); return null; }
        }

        public static int CorrectedMemoryErrors30Days()
        {
            try
            {
                const long month = 30L * 24 * 3600 * 1000;
                string q = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=47) and TimeCreated[timediff(@SystemTime) <= {month}]]]";
                using var r = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
                int n = 0;
                for (var e = r.ReadEvent(); e != null && n < 10000; e = r.ReadEvent()) using (e) n++;
                return n;
            }
            catch (Exception ex) { AppLogger.Debug($"RamChecks WHEA: {ex.Message}"); return 0; }
        }

        public static List<DiskFinding> Run(List<RamEntry> modules)
        {
            var list = new List<DiskFinding>();
            var inst = modules.Where(m => !m.IsEmpty).ToList();

            // 1. speed vs what the modules are made for
            if (XmpState(modules) is var (text, tip, key) && key == "StatusWarnBrush")
                list.Add(new(DiskFinding.Level.Warn, PT("XMP / EXPO off"), tip));

            // 2. different modules together
            if (inst.Count > 1)
            {
                bool diffSize = inst.Select(m => m.CapacityBytes).Distinct().Count() > 1;
                bool diffPart = inst.Select(m => (m.PartNumber ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                bool diffSpeed = inst.Select(m => m.RatedMHz).Where(v => v > 0).Distinct().Count() > 1;
                if (diffSize || diffSpeed)
                    list.Add(new(DiskFinding.Level.Warn, PT("Different modules mixed"),
                        PT("The modules differ in size or speed. All of them run at the speed of the slowest, and with different sizes only part of the memory works in dual channel. Identical modules are best.")));
                else if (diffPart)
                    list.Add(new(DiskFinding.Level.Info, PT("Modules of different models"),
                        PT("Same size and speed, but different models. Usually fine; if the PC freezes or crashes, test with one module at a time.")));
            }

            // 3. the last Windows Memory Diagnostic
            if (LastWindowsMemoryTest() is MdResult md && md.When > DateTime.MinValue)
                list.Add(md.Errors
                    ? new(DiskFinding.Level.Crit, PT("Windows memory test: errors"),
                          PT("The Windows Memory Diagnostic of {0} found errors. A module is faulty: test them one at a time to find it, then replace it.", md.When.ToString("d")))
                    : new(DiskFinding.Level.Info, PT("Windows memory test: OK"),
                          PT("The Windows Memory Diagnostic of {0} found no errors.", md.When.ToString("d"))));

            // 4. errors the hardware corrected
            int whea = CorrectedMemoryErrors30Days();
            if (whea > 0)
                list.Add(new(whea >= 10 ? DiskFinding.Level.Crit : DiskFinding.Level.Warn, PT("{0} corrected memory errors", whea),
                    PT("In the last 30 days the hardware corrected {0} memory errors (ECC). Nothing was lost yet, but a module is starting to fail — plan to replace it.", whea)));
            return list;
        }

        /// <summary>The theoretical peak in GB/s (MT/s × 8 bytes × channels).</summary>
        public static double TheoreticalGBs(int mts, int channels) => mts * 8.0 * Math.Max(1, channels) / 1000.0;
    }
}
