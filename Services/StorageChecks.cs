using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>One thing worth knowing about a drive, shown as a small chip on its card (short text + explanation).</summary>
    public sealed record DiskFinding(DiskFinding.Level Severity, string Short, string Detail)
    {
        public enum Level { Info, Warn, Crit }
    }

    /// <summary>
    /// The checks that explain why a drive is slow or failing even when SMART looks fine:
    ///  • the link it runs on (SATA II instead of III, an NVMe on fewer PCIe lanes or an older generation than it supports);
    ///  • partitions not aligned to 4 KB (old cloning tools), TRIM turned off for SSDs;
    ///  • disk errors Windows wrote in its log in the last 30 days, put on the drive they belong to;
    ///  • SMR hard disks (slow on long writes, not for RAID) and firmware versions with a known serious bug.
    /// Everything is read-only.
    /// </summary>
    public static class StorageChecks
    {
        // ── Windows disk errors, read once for all drives (60 s memory) ──────────────────────────────────────────────────
        private sealed class DiskEvents { public int BadBlock, Controller, Paging, Retried, HardFail; }
        private static Dictionary<int, DiskEvents>? _events;
        private static Dictionary<string, int>? _ntfsByVolume;
        private static DateTime _eventsAt;
        private static readonly object _evLock = new();
        private static readonly Regex RxHarddisk = new(@"Harddisk(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void LoadEvents()
        {
            lock (_evLock)
            {
                if (_events != null && (DateTime.Now - _eventsAt).TotalSeconds < 60) return;
                var map = new Dictionary<int, DiskEvents>();
                var ntfs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    const long month = 30L * 24 * 3600 * 1000;
                    string q = "*[System[Provider[@Name='disk' or @Name='Disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153 or EventID=154)"
                             + $" and TimeCreated[timediff(@SystemTime) <= {month}]]]";
                    using (var r = new EventLogReader(new EventLogQuery("System", PathType.LogName, q)))
                        for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                            using (e)
                            {
                                string where = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() ?? "" : "";
                                var m = RxHarddisk.Match(where);
                                if (!m.Success) { try { m = RxHarddisk.Match(e.FormatDescription() ?? ""); } catch { } }
                                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int idx)) continue;
                                if (!map.TryGetValue(idx, out var de)) map[idx] = de = new DiskEvents();
                                switch (e.Id)
                                {
                                    case 7: de.BadBlock++; break;
                                    case 11: de.Controller++; break;
                                    case 51: de.Paging++; break;
                                    case 153: de.Retried++; break;
                                    case 154: de.HardFail++; break;
                                }
                            }
                    // file system damage found by NTFS (55), per volume ("C:")
                    string qn = $"*[System[Provider[@Name='Ntfs' or @Name='Microsoft-Windows-Ntfs'] and (EventID=55) and TimeCreated[timediff(@SystemTime) <= {month}]]]";
                    using (var r = new EventLogReader(new EventLogQuery("System", PathType.LogName, qn)))
                        for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                            using (e)
                            {
                                string text = "";
                                try { text = string.Join(" ", e.Properties.Select(p => p.Value?.ToString() ?? "")); } catch { }
                                var vm = Regex.Match(text, @"\b([A-Z]):", RegexOptions.IgnoreCase);
                                if (vm.Success) { string v = vm.Groups[1].Value.ToUpperInvariant(); ntfs[v] = ntfs.TryGetValue(v, out int c) ? c + 1 : 1; }
                            }
                }
                catch (Exception ex) { AppLogger.Debug($"StorageChecks events: {ex.Message}"); }
                _events = map; _ntfsByVolume = ntfs; _eventsAt = DateTime.Now;
            }
        }

        // ── TRIM (one setting for all NTFS volumes), read once ───────────────────────────────────────────────────────────
        private static bool? _trimOff;
        private static bool TrimOff()
        {
            if (_trimOff.HasValue) return _trimOff.Value;
            try
            {
                var psi = new ProcessStartInfo("fsutil.exe", "behavior query DisableDeleteNotify") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                var m = Regex.Match(o, @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
                _trimOff = m.Success && m.Groups[1].Value == "1";
            }
            catch { _trimOff = false; }
            return _trimOff.Value;
        }

        // ── the PCIe link of an NVMe drive (through its controller) ──────────────────────────────────────────────────────
        internal static (int CurGen, int CurWidth, int MaxGen, int MaxWidth)? PcieLink(int diskIndex)
        {
            try
            {
                string? pnp = null;
                using (var s = new ManagementObjectSearcher($"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={diskIndex}"))
                    foreach (ManagementObject o in s.Get()) using (o) pnp = o["PNPDeviceID"]?.ToString();
                if (string.IsNullOrEmpty(pnp)) return null;
                string? parent = DevProps(pnp, "DEVPKEY_Device_Parent").GetValueOrDefault("DEVPKEY_Device_Parent")?.ToString();
                if (string.IsNullOrEmpty(parent) || !parent.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return null;
                var p = DevProps(parent, "DEVPKEY_PciDevice_CurrentLinkSpeed", "DEVPKEY_PciDevice_CurrentLinkWidth",
                                         "DEVPKEY_PciDevice_MaxLinkSpeed", "DEVPKEY_PciDevice_MaxLinkWidth");
                int I(string k) => p.TryGetValue(k, out var v) && v != null && int.TryParse(v.ToString(), out int n) ? n : 0;
                int cg = I("DEVPKEY_PciDevice_CurrentLinkSpeed"), cw = I("DEVPKEY_PciDevice_CurrentLinkWidth");
                int mg = I("DEVPKEY_PciDevice_MaxLinkSpeed"), mw = I("DEVPKEY_PciDevice_MaxLinkWidth");
                if (cg <= 0 || cw <= 0) return null;
                return (cg, cw, mg, mw);
            }
            catch (Exception ex) { AppLogger.Debug($"StorageChecks PCIe link: {ex.Message}"); return null; }
        }

        private static Dictionary<string, object?> DevProps(string deviceId, params string[] keys)
        {
            var res = new Dictionary<string, object?>();
            using var mo = new ManagementObject($"Win32_PnPEntity.DeviceID=\"{deviceId.Replace("\\", "\\\\")}\"");
            using var inp = mo.GetMethodParameters("GetDeviceProperties");
            inp["devicePropertyKeys"] = keys;
            using var outp = mo.InvokeMethod("GetDeviceProperties", inp, null);
            if (outp?["deviceProperties"] is ManagementBaseObject[] arr)
                foreach (var prop in arr)
                    using (prop)
                    {
                        string? k = prop["KeyName"]?.ToString();
                        if (k != null && prop.Properties.Cast<PropertyData>().Any(pd => pd.Name == "Data")) res[k] = prop["Data"];
                    }
            return res;
        }

        internal static string GenName(int g) => g switch { 1 => "PCIe 1.0", 2 => "PCIe 2.0", 3 => "PCIe 3.0", 4 => "PCIe 4.0", 5 => "PCIe 5.0", 6 => "PCIe 6.0", _ => "PCIe" };

        // ── partition alignment ──────────────────────────────────────────────────────────────────────────────────────────
        private static List<string> MisalignedPartitions(int diskIndex)
        {
            var bad = new List<string>();
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT Index, StartingOffset, Size FROM Win32_DiskPartition WHERE DiskIndex={diskIndex}");
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        ulong off = Convert.ToUInt64(o["StartingOffset"] ?? 0UL), size = Convert.ToUInt64(o["Size"] ?? 0UL);
                        if (size < 1UL << 30) continue;                      // small system partitions do not matter
                        if (off % 4096 != 0) bad.Add($"#{o["Index"]}");
                    }
            }
            catch (Exception ex) { AppLogger.Debug($"StorageChecks alignment: {ex.Message}"); }
            return bad;
        }

        // ── known drive facts (only well-documented ones) ────────────────────────────────────────────────────────────────
        private static readonly Regex RxSmr = new(
            @"WD(20|30|40|60)EFAX|WD(20|30|40|60)EZAZ|WD(10|20)SPZX|ST(2000|3000|4000|6000|8000)DM00[3478]|ST(1000|2000)LM0(15|48|49)|ST(4000|5000)LM0(00|24)|DT02ABA|MQ04ABF100|MQ04ABD200",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Firmware versions with a published serious bug and the fix (model fragment, bad firmware, fixed in).</summary>
        private static readonly (string Model, string Firmware, string Fix)[] BadFirmware =
        {
            ("980 PRO", "3B2QGXA7", "5B2QGXA7"),   // health dropped quickly / drive turned read-only (Samsung, early 2023)
            ("990 PRO", "0B2QJXD7", "1B2QJXD7"),   // rapid health loss (Samsung, early 2023)
        };

        public static bool IsSmr(string model) => RxSmr.IsMatch(model ?? "");

        // ── all checks for one drive ─────────────────────────────────────────────────────────────────────────────────────
        public static List<DiskFinding> Run(DiskHealthEntry d)
        {
            var list = new List<DiskFinding>();
            static string PT(string en, params object?[] a) => a.Length == 0 ? LiteralTexts.Translate(en) : string.Format(LiteralTexts.Translate(en), a);
            bool ssd = d.Kind is "NVMe SSD" or "SATA SSD" or "SSD" or "M.2 SATA SSD" or "External SSD";
            bool nvme = d.Kind == "NVMe SSD" || (d.Bus ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase);

            // 1. the link
            if (d.SataLinkMaxTenths > 0 && d.SataLinkCurrentTenths > 0 && d.SataLinkCurrentTenths < d.SataLinkMaxTenths)
                list.Add(new(ssd ? DiskFinding.Level.Warn : DiskFinding.Level.Info,
                    PT("SATA link {0}", d.SataLinkCurrent),
                    ssd ? PT("The drive supports {0} but runs at {1} — an older SATA port or a poor cable. An SSD loses about half of its speed.", d.SataLinkMax, d.SataLinkCurrent)
                        : PT("Hardly matters for a hard disk: it supports {0} but runs at {1} (an older SATA port or a poor cable).", d.SataLinkMax, d.SataLinkCurrent)));
            if (nvme && PcieLink(d.DiskIndex) is var (cg, cw, mg, mw))
            {
                if (mw > 0 && cw < mw)
                    list.Add(new(DiskFinding.Level.Warn, PT("Only x{0} of x{1} PCIe lanes", cw, mw),
                        PT("The SSD can use {0} lanes but runs on {1} — the slot, an adapter or a shared lane in the BIOS. Its top speed drops to about {2}%.", mw, cw, 100 * cw / mw)));
                if (mg > 0 && cg < mg)
                    list.Add(new(DiskFinding.Level.Info, PT("{0} (supports {1})", GenName(cg), GenName(mg)),
                        PT("The SSD supports {0}; right now its link runs at {1}. Normal in an older slot or while the drive saves power — if the Benchmark is slow too, check the slot and the BIOS.", GenName(mg), GenName(cg))));
                else
                    list.Add(new(DiskFinding.Level.Info, $"{GenName(cg)} x{cw}", PT("The PCIe link of the SSD: generation and lanes, as the drive negotiated them.")));
            }

            // 2. Windows disk errors (last 30 days)
            LoadEvents();
            if (_events != null && _events.TryGetValue(d.DiskIndex, out var ev))
            {
                // USB / SD drives: Windows numbers the disks in the order they are connected, so "Harddisk1" in last month's log may
                // have been another stick or card reader; and a stick pulled out without "Eject" also leaves "bad block" entries.
                // So for them it is a warning about read / write errors in the log, not a count of the drive's bad blocks.
                bool removable = (d.Bus ?? "") is "USB" or "SD" || d.Kind is "USB stick" or "SD card" or "External HDD" or "External SSD" or "External drive";
                if (ev.BadBlock + ev.HardFail > 0 && removable)
                    list.Add(new(DiskFinding.Level.Warn, PT("{0} read / write errors in the Windows log", ev.BadBlock + ev.HardFail),
                        PT("In the last 30 days Windows logged {0} failed reads or writes on the USB drive with this number. On USB drives they usually come from removing the drive without \"Eject\", a loose connection, or another USB drive that had the same number earlier. Run a Surface scan to check this drive itself.", ev.BadBlock + ev.HardFail)));
                else if (ev.BadBlock + ev.HardFail > 0)
                    list.Add(new(DiskFinding.Level.Crit, PT("{0} bad-block errors", ev.BadBlock + ev.HardFail),
                        PT("Windows could not read or write some blocks of this drive in the last 30 days ({0} times). Back up the important data and run a Surface scan.", ev.BadBlock + ev.HardFail)));
                if (ev.Controller > 0)
                    list.Add(new(DiskFinding.Level.Warn, PT("{0} controller errors", ev.Controller),
                        PT("The connection to this drive failed {0} times in the last 30 days — most often the cable, the port or the power (for a USB drive: another port or cable, no hub).", ev.Controller)));
                if (ev.Retried + ev.Paging > 0)
                    list.Add(new(DiskFinding.Level.Warn, PT("{0} retried operations", ev.Retried + ev.Paging),
                        PT("Windows had to repeat {0} reads or writes on this drive in the last 30 days. A few are harmless; many, or more each week, point to a failing drive or connection.", ev.Retried + ev.Paging)));
            }
            if (_ntfsByVolume != null)
            {
                int fs = d.Partitions.Sum(p => _ntfsByVolume.TryGetValue((p.Letter ?? "").TrimEnd('\\', ':'), out int c) ? c : 0);
                if (fs > 0)
                    list.Add(new(DiskFinding.Level.Warn, PT("File system errors"),
                        PT("Windows found damage in the file system of this drive {0} times in the last 30 days. Tools → Check disk repairs it at the next restart.", fs)));
            }

            // 3. alignment and TRIM
            var mis = MisalignedPartitions(d.DiskIndex);
            if (mis.Count > 0)
                list.Add(new(ssd ? DiskFinding.Level.Warn : DiskFinding.Level.Info, PT("Partition not aligned"),
                    PT("A partition does not start on a 4 KB boundary (usually after cloning with an old tool). Writes are slower and an SSD wears faster; a partition tool can align it.")));
            if (ssd && TrimOff())
                list.Add(new(DiskFinding.Level.Warn, PT("TRIM is off"),
                    PT("Windows does not tell the SSD which blocks are free (TRIM turned off). The SSD slows down over time. Turn it on: fsutil behavior set DisableDeleteNotify 0")));

            // 4. known facts about the model
            if (!ssd && IsSmr(d.Model))
                list.Add(new(DiskFinding.Level.Info, PT("SMR hard disk"),
                    PT("This model records with SMR (shingled tracks): fine for normal use, but very slow during long writes and not suitable for RAID or a NAS.")));
            foreach (var (model, fw, fix) in BadFirmware)
                if ((d.Model ?? "").Contains(model, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Firmware, fw, StringComparison.OrdinalIgnoreCase))
                    list.Add(new(DiskFinding.Level.Crit, PT("Firmware {0}: update", fw),
                        PT("This firmware has a known bug that wears the drive out quickly. Install firmware {0} or newer with Samsung Magician — back up first.", fix)));
            return list;
        }

        /// <summary>For the self-test / report: forget the cached results.</summary>
        public static void Forget() { lock (_evLock) { _events = null; } _trimOff = null; }
    }
}
