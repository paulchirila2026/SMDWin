
using System.IO;

namespace SMDWin.Services
{
    /// <summary>A driver from the triage list: name + memory range (if it could be calibrated).</summary>
    public sealed class KernelDumpDriver
    {
        public string Name     { get; set; } = "";   // ex: "nvlddmkm.sys"
        public string FullPath { get; set; } = "";   // ex: "\SystemRoot\System32\drivers\nvlddmkm.sys"
        public ulong  Base     { get; set; }          // 0 if not calibrated
        public uint   Size     { get; set; }          // 0 if not calibrated
        public string Category => MiniDumpParser.CategorizeDriver(Name);
        public MiniDumpParser.DriverKind Kind => MiniDumpParser.GetDriverKind(Name);
        public bool Contains(ulong addr) => Base != 0 && Size != 0 && addr >= Base && addr < Base + Size;
    }

    /// <summary>Everything that could be extracted natively from a kernel dump.</summary>
    public sealed class KernelDumpInfo
    {
        public bool     Is64            { get; set; }
        public string   Architecture    { get; set; } = "";
        public uint     BugCheckCode    { get; set; }
        public ulong[]  Params          { get; set; } = new ulong[4];
        public string   BugCheckName    { get; set; } = "";
        public string   ExplanationRo   { get; set; } = "";
        public string   DumpTypeName    { get; set; } = "";
        public DateTime? SystemTimeUtc  { get; set; }   // the real moment of the crash
        public ulong    Rip             { get; set; }   // the current instruction at the crash (x64)
        public ulong    ExceptionAddress{ get; set; }
        public List<KernelDumpDriver> Drivers { get; } = new();
        public bool     DriverRangesValid { get; set; } // base/size calibration succeeded
        public string?  CulpritName     { get; set; }   // the probable culprit driver
        public string?  CulpritSource   { get; set; }   // where it comes from (Rip / exception / parameter N)
        public bool     CulpritIsGeneric{ get; set; }   // only ntoskrnl/hal matched
        public string   CodeHex => $"0x{BugCheckCode:X8}";
        public string   ParamsHex => Is64
            ? string.Join(", ", Params.Select(p => $"0x{p:X16}"))
            : string.Join(", ", Params.Select(p => $"0x{(uint)p:X8}"));
    }

    public static class KernelDumpParser
    {
        private const uint SigPage = 0x45474150; // "PAGE"
        private const uint SigDu64 = 0x34365544; // "DU64"
        private const uint SigDump = 0x504D5544; // "DUMP" (32-bit)
        private const int  MaxScanBytes = 8 * 1024 * 1024; // triage ≤ ~2 MB; the read is capped


        /// <summary>Parses a kernel dump; null if it is not a PAGE* format.
        /// Throws IOException/UnauthorizedAccessException for access problems (the caller handles those differently from
        /// "unknown format").</summary>
        public static KernelDumpInfo? TryParse(string path)
        {
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int toRead = (int)Math.Min(fs.Length, MaxScanBytes);
                buf = new byte[toRead];
                int off = 0;
                while (off < toRead)
                {
                    int n = fs.Read(buf, off, toRead - off);
                    if (n <= 0) break;
                    off += n;
                }
                if (off < 0x1000) return null;
            }

            uint sig  = U32(buf, 0x0);
            uint valid= U32(buf, 0x4);
            if (sig != SigPage) return null;

            var info = new KernelDumpInfo();
            if (valid == SigDu64 && buf.Length >= 0x2000)
            {
                // ── Header 64-bit (offseturi: kdmp-parser + rekall, identice) ──
                info.Is64 = true;
                uint machine = U32(buf, 0x30);
                info.Architecture = machine switch
                {
                    0x8664 => "x64", 0xAA64 => "ARM64", 0x014C => "x86", _ => $"0x{machine:X}"
                };
                info.BugCheckCode = U32(buf, 0x38);
                for (int i = 0; i < 4; i++) info.Params[i] = U64(buf, 0x40 + i * 8);

                // AMD64 CONTEXT at 0x348 — Rip at +0xF8 (WinNT.h layout, stable).
                if (machine == 0x8664)
                    info.Rip = U64(buf, 0x348 + 0xF8);

                // EXCEPTION_RECORD64 at 0xF00 — ExceptionAddress at +0x10.
                info.ExceptionAddress = U64(buf, 0xF00 + 0x10);

                uint dumpType = U32(buf, 0xF98);
                info.DumpTypeName = DumpTypeName(dumpType);

                long ft = (long)U64(buf, 0xFA8);
                if (ft > 0)
                    try { info.SystemTimeUtc = DateTime.FromFileTimeUtc(ft); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                // The driver list exists only in triage dumps (Minidump, type 4).
                if (dumpType == 4)
                    ScanTriageDrivers64(buf, info);
            }
            else if (valid == SigDump)
            {
                // ── 32-bit header (rekall _DMP_HEADER) — old x86 systems ──
                info.Is64 = false;
                uint machine = U32(buf, 0x20);
                info.Architecture = machine == 0x014C ? "x86" : $"0x{machine:X}";
                info.BugCheckCode = U32(buf, 0x28);
                for (int i = 0; i < 4; i++) info.Params[i] = U32(buf, 0x2C + i * 4);
                uint dumpType = U32(buf, 0xF88);
                info.DumpTypeName = DumpTypeName(dumpType);
                long ft = (long)U64(buf, 0xFC0);
                if (ft > 0)
                    try { info.SystemTimeUtc = DateTime.FromFileTimeUtc(ft); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                // On 32-bit only the driver names are reported (no base calibration — x86 systems are rare today; the header stays
                // the main information).
                if (dumpType == 4)
                    ScanDriverNamesOnly(buf, info, 0x1000);
            }
            else return null;

            (info.BugCheckName, info.ExplanationRo) = DescribeBugCheck(info.BugCheckCode);
            PickCulprit(info);
            return info;
        }

        // ── 64-bit triage: names + base/size calibration by voting ──────────────
        private static void ScanTriageDrivers64(byte[] buf, KernelDumpInfo info)
        {
            const int start = 0x2000; // TRIAGE_DUMP starts right after the header
            var records = FindDumpStrings(buf, start); // (record offset, path)
            if (records.Count == 0) return;

            foreach (var (recOff, path) in records)
                info.Drivers.Add(new KernelDumpDriver
                {
                    FullPath = path,
                    Name     = ShortName(path),
                });

            // References: the driver list entries store the offset (u32, from the start of the FILE) of the name record. Two keys
            // are tried: the offset of the length field (recOff) and the offset of the text (recOff+4).
            // ONE pass over the area before the pool (where the entries live), with key→driver dictionaries — O(region), not
            // O(drivers × region).
            int poolStart = records.Min(r => r.RecOff);
            var key0 = new Dictionary<uint, int>(); // recOff   → index driver
            var key4 = new Dictionary<uint, int>(); // recOff+4 → index driver
            for (int d = 0; d < records.Count; d++)
            {
                key0[(uint)records[d].RecOff]     = d;
                key4[(uint)(records[d].RecOff + 4)] = d;
            }
            var hitsPerKey = new Dictionary<int, List<(int Hit, int DriverIdx)>>
            { [0] = new(), [4] = new() };
            for (int p = start; p + 4 <= poolStart; p += 4)
            {
                uint v = U32(buf, p);
                if (key0.TryGetValue(v, out int d0)) hitsPerKey[0].Add((p, d0));
                if (key4.TryGetValue(v, out int d4)) hitsPerKey[4].Add((p, d4));
            }

            // Pick the key variant that found exactly one reference for the most drivers.
            var best = hitsPerKey.OrderByDescending(kv =>
                kv.Value.GroupBy(h => h.DriverIdx).Count(g => g.Count() == 1)).First().Value;
            var uniqueHits = best.GroupBy(h => h.DriverIdx)
                                 .Where(g => g.Count() == 1)
                                 .ToDictionary(g => g.Key, g => g.First().Hit);
            if (uniqueHits.Count < Math.Max(3, records.Count * 6 / 10))
            {
                AppLogger.Debug($"KernelDump: reference calibration failed ({uniqueHits.Count}/{records.Count}) — names-only list");
                return; // keep the names, without bases
            }

            // Vote for the relative offset of the DllBase (u64) and SizeOfImage (u32) fields from the position of the name
            // reference, within a ±0x80 window.
            //
            // SUBTLE: when entries have a uniform stride, the offset of "MY field" and "the same field of the NEIGHBOURING entry"
            // collect almost identical votes (they differ only on the edge entries). So the maximum is not simply taken: ALL
            // candidates with votes close to the maximum are collected, then the (baseOff, sizeOff) pair with the best GLOBAL
            // CONSISTENCY is chosen — the [Base, Base+Size) ranges of real drivers do not overlap; a pair shifted by one entry
            // produces overlaps. Tiebreak: the offsets with minimal |rel| (the fields sit next to the name reference in the same entry).
            var baseVotes = new Dictionary<int, int>();
            for (int rel = -0x80; rel <= 0x80; rel += 8)
            {
                int votes = 0;
                foreach (var hit in uniqueHits.Values)
                {
                    int p = hit + rel;
                    if (p < start || p + 8 > buf.Length) continue;
                    if (IsKernelBase(U64(buf, p))) votes++;
                }
                if (votes > 0) baseVotes[rel] = votes;
            }
            var sizeVotes = new Dictionary<int, int>();
            for (int rel = -0x80; rel <= 0x80; rel += 4)
            {
                int votes = 0;
                foreach (var hit in uniqueHits.Values)
                {
                    int p = hit + rel;
                    if (p < start || p + 4 > buf.Length) continue;
                    uint v = U32(buf, p);
                    if (v >= 0x1000 && v <= 0x8000000 && (v & 0xFFF) == 0) votes++;
                }
                if (votes > 0) sizeVotes[rel] = votes;
            }

            int need = Math.Max(3, uniqueHits.Count * 7 / 10);
            int maxB = baseVotes.Count > 0 ? baseVotes.Values.Max() : 0;
            int maxS = sizeVotes.Count > 0 ? sizeVotes.Values.Max() : 0;
            if (maxB < need || maxS < need)
            {
                AppLogger.Debug($"KernelDump: base/size calibration vote insufficient (base {maxB}, size {maxS}, needed {need}) — names-only list");
                return;
            }
            var candB = baseVotes.Where(kv => kv.Value >= maxB - 1).Select(kv => kv.Key).ToList();
            var candS = sizeVotes.Where(kv => kv.Value >= maxS - 1).Select(kv => kv.Key).ToList();

            int bestBaseOff = 0, bestSizeOff = 0; long bestScore = long.MinValue;
            foreach (int bOff in candB)
            foreach (int sOff in candS)
            {
                // Build the ranges for this pair and count the overlaps.
                var ranges = new List<(ulong B, ulong E)>();
                foreach (var hit in uniqueHits.Values)
                {
                    int pb = hit + bOff, ps = hit + sOff;
                    if (pb < start || pb + 8 > buf.Length || ps < start || ps + 4 > buf.Length) continue;
                    ulong b = U64(buf, pb); uint s = U32(buf, ps);
                    if (IsKernelBase(b) && s >= 0x1000 && s <= 0x8000000)
                        ranges.Add((b, b + s));
                }
                ranges.Sort();
                int overlaps = 0;
                for (int r = 1; r < ranges.Count; r++)
                    if (ranges[r].B < ranges[r - 1].E) overlaps++;
                long score = ranges.Count * 10L - overlaps * 25L - (Math.Abs(bOff) + Math.Abs(sOff)) / 8;
                if (score > bestScore) { bestScore = score; bestBaseOff = bOff; bestSizeOff = sOff; }
            }

            foreach (var (dIdx, hit) in uniqueHits)
            {
                ulong b = U64(buf, hit + bestBaseOff);
                uint  s = U32(buf, hit + bestSizeOff);
                if (IsKernelBase(b) && s >= 0x1000 && s <= 0x8000000)
                {
                    info.Drivers[dIdx].Base = b;
                    info.Drivers[dIdx].Size = s;
                }
            }
            info.DriverRangesValid = info.Drivers.Count(d => d.Base != 0) >= need;
            AppLogger.Debug($"KernelDump: {info.Drivers.Count} drivere, calibrare {(info.DriverRangesValid ? "OK" : "parțială")} (baseOff {bestBaseOff}, sizeOff {bestSizeOff})");
        }

        private static void ScanDriverNamesOnly(byte[] buf, KernelDumpInfo info, int start)
        {
            foreach (var (_, path) in FindDumpStrings(buf, start))
                info.Drivers.Add(new KernelDumpDriver { FullPath = path, Name = ShortName(path) });
        }

        /// <summary>Finds DUMP_STRING records: u32 (character count) + UTF-16LE, ending in .sys/.exe/.dll — strictly validated,
        /// with no layout assumptions.</summary>
        private static List<(int RecOff, string Path)> FindDumpStrings(byte[] buf, int start)
        {
            var result = new List<(int, string)>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i + 4 <= buf.Length; i += 4)
            {
                uint len = U32(buf, i);
                if (len < 5 || len > 200) continue;
                long end = (long)i + 4 + len * 2;
                if (end > buf.Length) continue;

                // Character-by-character validation — a plausible Windows path.
                bool ok = true;
                var chars = new char[len];
                for (int c = 0; c < len; c++)
                {
                    char ch = (char)(buf[i + 4 + c * 2] | (buf[i + 5 + c * 2] << 8));
                    if (ch < 0x20 || ch > 0x7E) { ok = false; break; }
                    if (!(char.IsLetterOrDigit(ch) || ch is '\\' or '/' or '.' or '_' or '-' or ' ' or ':' or '(' or ')' or '~' or '$' or '@' or '!' or '+'))
                    { ok = false; break; }
                    chars[c] = ch;
                }
                if (!ok) continue;

                string s = new string(chars);
                if (!(s.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                   || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   || s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) continue;
                if (!s.Contains('\\') && !s.Contains('.')) continue;
                if (seen.Add(ShortName(s)))
                    result.Add((i, s));
            }
            return result;
        }

        // ── The probable culprit ─────────────────────────────────────────────────
        private static readonly HashSet<string> GenericImages = new(StringComparer.OrdinalIgnoreCase)
        { "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll" };

        private static void PickCulprit(KernelDumpInfo info)
        {
            if (!info.DriverRangesValid) return;

            // CODE address candidates, in order of confidence.
            var candidates = new List<(ulong Addr, string Source)>();
            if (info.ExceptionAddress != 0) candidates.Add((info.ExceptionAddress, LanguageService.S("Crash", "CulpritExceptionAddress")));
            if (info.Rip != 0)              candidates.Add((info.Rip, LanguageService.S("Crash", "CulpritRipRegister")));

            // Parameters that are instruction addresses, per code (Microsoft Bug Check Code Reference): 0xD1/0xA → p4;
            // 0x1E/0x3B/0x7E/0x8E → p2; 0x50 → p3; 0xFC → p1.
            int? idx = info.BugCheckCode switch
            {
                0x0A or 0xD1        => 3,
                0x1E or 0x3B or 0x7E or 0x8E => 1,
                0x50                => 2,
                0xFC                => 0,
                _                   => null,
            };
            if (idx.HasValue && info.Params[idx.Value] != 0)
                candidates.Add((info.Params[idx.Value], string.Format(LanguageService.S("Crash", "CulpritBugcheckParamFmt"), idx.Value + 1)));

            // Generic: any parameter shaped like a kernel address.
            for (int i = 0; i < 4; i++)
                if (IsKernelAddr(info.Params[i]))
                    candidates.Add((info.Params[i], string.Format(LanguageService.S("Crash", "CulpritBugcheckParamFmt"), i + 1)));

            // Pass 1: skip the generic images (ntoskrnl/hal).
            foreach (var (addr, src) in candidates)
            {
                var hit = info.Drivers.FirstOrDefault(d => d.Contains(addr) && !GenericImages.Contains(d.Name));
                if (hit != null)
                {
                    info.CulpritName = hit.Name; info.CulpritSource = src; return;
                }
            }
            // Pass 2: accept the kernel too, but marked generic.
            foreach (var (addr, src) in candidates)
            {
                var hit = info.Drivers.FirstOrDefault(d => d.Contains(addr));
                if (hit != null)
                {
                    info.CulpritName = hit.Name; info.CulpritSource = src; info.CulpritIsGeneric = true; return;
                }
            }
        }

        // ── Bugcheck dictionary (Microsoft Bug Check Code Reference — stable codes, unchanged for years; short explanations) ──────────
        public static (string Name, string Ro) DescribeBugCheck(uint code) => code switch
        {
            0x0A => ("IRQL_NOT_LESS_OR_EQUAL", "Un driver a accesat memorie paginată la un IRQL prea înalt — tipic un driver defect."),
            0x1A => ("MEMORY_MANAGEMENT", "Eroare gravă de gestionare a memoriei — des cauzată de RAM defect sau driver."),
            0x1E => ("KMODE_EXCEPTION_NOT_HANDLED", "O excepție în kernel nu a fost tratată — driver sau incompatibilitate hardware."),
            0x24 => ("NTFS_FILE_SYSTEM", "Eroare în driverul NTFS — verifică discul (chkdsk) și cablurile SATA."),
            0x27 => ("RDR_FILE_SYSTEM", "Eroare în redirector-ul de fișiere din rețea (SMB)."),
            0x2E => ("DATA_BUS_ERROR", "Eroare de paritate în memorie — aproape sigur RAM/hardware defect."),
            0x34 => ("CACHE_MANAGER", "Eroare în cache manager — de regulă memorie insuficientă în pool."),
            0x35 => ("NO_MORE_IRP_STACK_LOCATIONS", "Un driver a trimis un IRP cu stivă insuficientă — driver defect în lanț."),
            0x3B => ("SYSTEM_SERVICE_EXCEPTION", "Excepție într-un serviciu de sistem — frecvent driver grafic sau antivirus."),
            0x44 => ("MULTIPLE_IRP_COMPLETE_REQUESTS", "Un driver a completat același IRP de două ori — bug clar de driver."),
            0x4E => ("PFN_LIST_CORRUPT", "Lista de pagini fizice coruptă — RAM defect sau driver care scrie aiurea."),
            0x50 => ("PAGE_FAULT_IN_NONPAGED_AREA", "Acces la memorie invalidă din kernel — driver defect, RAM sau antivirus."),
            0x51 => ("REGISTRY_ERROR", "Eroare de registry — hive corupt sau problemă de disc."),
            0x7A => ("KERNEL_DATA_INPAGE_ERROR", "Nu s-a putut citi o pagină din fișierul de paginare — disc/cablu/controller defect."),
            0x7B => ("INACCESSIBLE_BOOT_DEVICE", "Windows nu-și mai vede discul de boot — mod SATA schimbat în BIOS sau driver de storage lipsă."),
            0x7E => ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Excepție netratată într-un thread de sistem — driverul din stack trace e suspectul."),
            0x7F => ("UNEXPECTED_KERNEL_MODE_TRAP", "Trap neașteptat în kernel (des double-fault) — overclock, RAM sau driver."),
            0x80 => ("NMI_HARDWARE_FAILURE", "Întrerupere hardware nemascabilă — defect hardware raportat de placă."),
            0x8E => ("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "Excepție kernel netratată — tipic driver defect."),
            0x9F => ("DRIVER_POWER_STATE_FAILURE", "Un driver a blocat tranziția de power (sleep/resume) — des driver de rețea/GPU/USB."),
            0xA5 => ("ACPI_BIOS_ERROR", "BIOS-ul ACPI raportează date invalide — actualizează BIOS-ul."),
            0xB8 => ("ATTEMPTED_SWITCH_FROM_DPC", "Comutare ilegală de context dintr-un DPC — bug de driver."),
            0xBE => ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Un driver a scris în memorie read-only — driver defect."),
            0xC2 => ("BAD_POOL_CALLER", "Alocare/eliberare greșită de pool — driver defect."),
            0xC4 => ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier a prins un driver cu mâța-n sac — parametrii indică driverul."),
            0xC5 => ("DRIVER_CORRUPTED_EXPOOL", "Pool corupt de un driver care scrie după eliberare."),
            0xC9 => ("DRIVER_VERIFIER_IOMANAGER_VIOLATION", "Driver Verifier: încălcare la nivel de I/O Manager."),
            0xD1 => ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Un driver a accesat memorie paginată la IRQL înalt — parametrul 4 arată driverul."),
            0xDE => ("POOL_CORRUPTION_IN_FILE_AREA", "Corupție de pool în zona de fișiere."),
            0xE1 => ("WORKER_THREAD_RETURNED_AT_BAD_IRQL", "Un worker thread s-a întors la IRQL greșit — driver defect."),
            0xE2 => ("MANUALLY_INITIATED_CRASH", "Crash declanșat manual (CrashOnCtrlScroll) — nu e o defecțiune."),
            0xEF => ("CRITICAL_PROCESS_DIED", "Un proces critic Windows (csrss/wininit) a murit — fișiere de sistem corupte sau disc."),
            0xF4 => ("CRITICAL_OBJECT_TERMINATION", "Obiect critic terminat neașteptat — des disc/SSD pe moarte."),
            0xF5 => ("FLTMGR_FILE_SYSTEM", "Eroare în Filter Manager — de regulă un filtru antivirus."),
            0xFC => ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Execuție de cod din memorie ne-executabilă — driver defect sau RAM."),
            0xFE => ("BUGCODE_USB_DRIVER", "Eroare în driverul USB — dispozitiv/hub/driver USB defect."),
            0x109 => ("CRITICAL_STRUCTURE_CORRUPTION", "PatchGuard: kernelul a fost modificat — driver ilegal sau RAM defect."),
            0x116 => ("VIDEO_TDR_FAILURE", "Placa video n-a mai răspuns și resetarea a eșuat — driver GPU sau placă defectă."),
            0x117 => ("VIDEO_TDR_TIMEOUT_DETECTED", "Timeout la resetarea plăcii video (TDR) — driver GPU/supraîncălzire."),
            0x119 => ("VIDEO_SCHEDULER_INTERNAL_ERROR", "Eroare internă în scheduler-ul video — driver GPU."),
            0x124 => ("WHEA_UNCORRECTABLE_ERROR", "Eroare hardware fatală raportată de CPU (WHEA) — CPU/placă de bază/alimentare, des overclock."),
            0x12B => ("FAULTY_HARDWARE_CORRUPTED_PAGE", "Pagină de memorie coruptă de hardware — RAM defect (rulează testul RAM din SMDWin)."),
            0x133 => ("DPC_WATCHDOG_VIOLATION", "Un driver a stat prea mult la IRQL înalt — des driver de storage (firmware SSD) sau rețea."),
            0x139 => ("KERNEL_SECURITY_CHECK_FAILURE", "Corupție de structuri detectată de kernel — driver defect sau RAM."),
            0x13A => ("KERNEL_MODE_HEAP_CORRUPTION", "Heap de kernel corupt — driver defect."),
            0x144 => ("BUGCODE_USB3_DRIVER", "Eroare în driverul USB 3.x (xHCI)."),
            0x154 => ("UNEXPECTED_STORE_EXCEPTION", "Excepție în store-ul de memorie comprimată — des SSD/driver de storage."),
            0x15E => ("BUGCODE_NDIS_DRIVER_LIVE_DUMP", "Problemă raportată de un driver de rețea (NDIS)."),
            0x1C4 => ("DRIVER_VERIFIER_DETECTED_VIOLATION_LIVEDUMP", "Driver Verifier (live dump)."),
            0x1CA => ("SYNTHETIC_WATCHDOG_TIMEOUT", "Watchdog sintetic expirat — sistemul a înghețat (des în VM)."),
            0x1E2 => ("INVALID_KERNEL_STACK_ADDRESS", "Adresă de stivă kernel invalidă."),
            _    => ($"BUGCHECK_0x{code:X}", string.Format(LanguageService.S("Crash", "UnknownBugcheckCode", "Unfamiliar code — search online for \"bugcheck 0x{0}\" along with the parameters."), code.ToString("X"))),
        };

        private static string DumpTypeName(uint t) => t switch
        {
            1 => "Full dump", 2 => "Kernel dump", 4 => "Minidump (triage)",
            5 => "Bitmap dump", 6 => "Live kernel bitmap", 8 => "Kernel memory dump",
            9 => "Kernel+user memory dump", 0xA => "Complete memory dump", _ => $"tip {t}"
        };

        private static string ShortName(string path)
        {
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return (i >= 0 ? path[(i + 1)..] : path).Trim();
        }

        private static bool IsKernelBase(ulong v) => v >= 0xFFFF800000000000UL && v < 0xFFFFFFFFFFF00000UL && (v & 0xFFF) == 0;
        private static bool IsKernelAddr(ulong v) => v >= 0xFFFF800000000000UL && v < 0xFFFFFFFFFFF00000UL;

        private static uint  U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static ulong U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
    }
}
