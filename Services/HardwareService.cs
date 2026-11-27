using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class HardwareService
    {
        // Pre-compiled Regex — avoids re-compilation on every SMART parse call
        private static readonly System.Text.RegularExpressions.Regex RxPhysDrive =
            new(@"PHYSICALDRIVE(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxDeviceId =
            new(@"DeviceID=""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static volatile List<DiskHealthEntry>? _diskCache;
        private static readonly System.Threading.SemaphoreSlim _diskLock = new(1, 1);

        public void InvalidateDiskCache() => _diskCache = null;

        public async Task<List<DiskHealthEntry>> GetDisksAsync()
        {
            if (_diskCache != null) return _diskCache;

            await _diskLock.WaitAsync();
            try
            {
                if (_diskCache != null) return _diskCache; // double-checked
                bool enriched = true;
                var built = await Task.Run(() =>
                {
                var results = new List<DiskHealthEntry>();

                var partitionMap = BuildPartitionMap();

                // Try modern Storage namespace first
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        @"\\.\root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk");
                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp1)
                    {
                        var size       = obj["Size"]        is ulong s ? FormatSize(s) : "—";
                        var health     = obj["HealthStatus"]?.ToString() ?? "0";
                        var healthInt  = ParseHealth(health);
                        var uniqueId   = obj["UniqueId"]?.ToString() ?? "";
                        // Sets DiskIndex from MSFT_Disk.Number (the real physical index: 0, 1, 2...)
                        // Needed to match SMART data correctly on systems with several disks.
                        int diskNumber = 0;
                        if (obj["Number"] is uint num) diskNumber = (int)num;

                        var entry = new DiskHealthEntry
                        {
                            DeviceId      = uniqueId,
                            Model         = obj["FriendlyName"]?.ToString() ?? "Unknown",
                            SerialNumber  = obj["SerialNumber"]?.ToString() ?? "—",
                            Size          = size,
                            SizeBytes     = obj["Size"] is ulong sBytes ? sBytes : 0,
                            MediaType     = DetectMediaTypeFull(obj),
                            Status        = GetHealthText(health),
                            HealthPercent = healthInt,
                            DiskIndex     = diskNumber,
                        };
                        entry.Partitions = GetPartitionsForDisk(obj["Number"]?.ToString(), partitionMap);
                        results.Add(entry);
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // Fallback
                if (results.Count == 0)
                {
                    try
                    {
                        using var searcher = WmiHelper.Searcher(WmiHelper.DiskQuery);
                        using (var __wmiTmp2 = searcher.Get())
                        {
                            foreach (ManagementObject obj in __wmiTmp2)
                        {
                            var size = obj["Size"] != null ? FormatSize(Convert.ToUInt64(obj["Size"])) : "—";
                            var model = obj["Model"]?.ToString() ?? "Unknown";
                            var index = obj["Index"]?.ToString() ?? "0";

                            var entry = new DiskHealthEntry
                            {
                                DeviceId      = obj["DeviceID"]?.ToString() ?? "—",
                                Model         = model,
                                SerialNumber  = obj["SerialNumber"]?.ToString()?.Trim() ?? "—",
                                Size          = size,
                                SizeBytes     = obj["Size"] != null ? Convert.ToUInt64(obj["Size"]) : 0,
                                MediaType     = DetectMediaTypeFull(obj),
                                Status        = obj["Status"]?.ToString() == "OK" ? "Healthy" : obj["Status"]?.ToString() ?? "Unknown",
                                HealthPercent = obj["Status"]?.ToString() == "OK" ? 100 : 60,
                                DiskIndex     = int.TryParse(index, out int fallbackIdx) ? fallbackIdx : 0,
                            };
                            entry.Partitions = GetPartitionsForDisk(index, partitionMap);
                            results.Add(entry);
                        }
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                }

                try { ClassifyDisks(results); } catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.ClassifyDisks"); }
                enriched = EnrichWithSmartData(results);
                foreach (var d in results)
                {
                    try { FillFormFactorFallback(d); } catch (Exception ex) { AppLogger.Debug($"FormFactor: {ex.Message}"); }
                    if (d.Bus == "USB")
                        try { UsbLinkInfo.Fill(d); } catch (Exception ex) { AppLogger.Debug($"UsbLinkInfo: {ex.Message}"); }
                    AppLogger.Debug($"Disk #{d.DiskIndex} {d.Model}: {d.TypeText}{(d.UsbLinkText.Length > 0 ? " · " + d.UsbLinkText : "")}");
                }
                return results;
                }); // end Task.Run
                if (enriched) _diskCache = built;
                return built;
            }
            finally { _diskLock.Release(); }
        }

        private static Dictionary<string, List<PartitionEntry>> BuildPartitionMap()
        {
            // diskIndex (string "0","1",...) → list of logical drives on that disk
            var map = new Dictionary<string, List<PartitionEntry>>();

            try
            {
                // Step 1: diskIndex → partition DeviceIDs
                // Win32_DiskDriveToDiskPartition can be slow on systems with external/USB HDDs.
                var partByDisk = new Dictionary<string, List<string>>(); // diskIndex → partitionDeviceIDs
                bool step1Ok = false;
                for (int attempt = 0; attempt < 2 && !step1Ok; attempt++)
                {
                    try
                    {
                        // Short timeout (5 s) — after the first timeout, fall back quickly
                        using var s = WmiHelper.Searcher(
                            "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition",
                            TimeSpan.FromSeconds(5));
                        using (var __wmiTmp3 = s.Get())
                        {
                            foreach (ManagementObject rel in __wmiTmp3)
                        {
                            var ant = rel["Antecedent"]?.ToString() ?? "";
                            var dep = rel["Dependent"]?.ToString() ?? "";
                            var idxMatch  = RxPhysDrive.Match(ant);
                            var partMatch = RxDeviceId.Match(dep);
                            if (idxMatch.Success && partMatch.Success)
                            {
                                var diskIdx = idxMatch.Groups[1].Value;
                                var partDev = partMatch.Groups[1].Value.Replace("\\\\", "\\");
                                if (!partByDisk.ContainsKey(diskIdx)) partByDisk[diskIdx] = new();
                                partByDisk[diskIdx].Add(partDev);
                            }
                        }
                        }
                        step1Ok = true;
                    }
                    catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.Timedout)
                    {
                        AppLogger.Warning(ex, $"BuildPartitionMap step1 timeout (attempt {attempt+1}/2) — retrying");
                        // No wait between retries — fall back immediately on the second timeout
                    }
                }
                if (!step1Ok)
                {
                    // Fallback: use DriveInfo directly — no disk→partition mapping, but at least nothing blocks
                    AppLogger.Warning("BuildPartitionMap: fallback to DriveInfo (WMI step1 failed both attempts)");
                    return BuildPartitionMapFallback();
                }

                // Step 2: partitionDeviceID → logical drive letters
                var logByPart = new Dictionary<string, List<PartitionEntry>>();
                using (var s = WmiHelper.Searcher(
                    "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    using (var __wmiTmp4 = s.Get())
                    {
                        foreach (ManagementObject rel in __wmiTmp4)
                    {
                        var ant = rel["Antecedent"]?.ToString() ?? "";
                        var dep = rel["Dependent"]?.ToString() ?? "";
                        var partMatch   = RxDeviceId.Match(ant);
                        var letterMatch = RxDeviceId.Match(dep);
                        if (partMatch.Success && letterMatch.Success)
                        {
                            var partDev = partMatch.Groups[1].Value.Replace("\\\\", "\\");
                            var letter  = letterMatch.Groups[1].Value;
                            if (!logByPart.ContainsKey(partDev)) logByPart[partDev] = new();
                            logByPart[partDev].Add(new PartitionEntry { Letter = letter + "\\" });
                        }
                    }
                    }
                }

                // Step 2.5: fetch partition metadata (Boot, Type/GPT, Index)
                // Win32_DiskPartition: Bootable, Type, Index, DiskIndex
                var partMeta = new Dictionary<string, (bool bootable, string type, int index)>();
                try
                {
                    using var sp = WmiHelper.Searcher(
                        "SELECT DeviceID, Bootable, Type, Index FROM Win32_DiskPartition");
                    using (var __wmiTmp5 = sp.Get())
                    {
                        foreach (ManagementObject dp in __wmiTmp5)
                    {
                        var devId    = dp["DeviceID"]?.ToString() ?? "";
                        bool boot    = Convert.ToBoolean(dp["Bootable"] ?? false);
                        string ptype = dp["Type"]?.ToString() ?? "";
                        int idx      = Convert.ToInt32(dp["Index"] ?? 0);
                        // Normalize Type: "GPT: ..." → "GPT", "Installable File System" → "MBR"
                        string style = ptype.StartsWith("GPT", StringComparison.OrdinalIgnoreCase) ? "GPT"
                                     : ptype.Length > 0 ? "MBR" : "";
                        if (!string.IsNullOrEmpty(devId))
                            partMeta[devId.Replace("\\\\", "\\")] = (boot, style, idx);
                    }
                    }
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* optional metadata */ }

                var ldDetails = new Dictionary<string, (string label, string fs, ulong total, ulong free)>();
                using (var s = WmiHelper.Searcher(
                    "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk WHERE DriveType=3 OR DriveType=2"))
                {
                    using (var __wmiTmp6 = s.Get())
                    {
                        foreach (ManagementObject ld in __wmiTmp6)
                    {
                        var id = ld["DeviceID"]?.ToString() ?? "";
                        uint dtype = 0; try { dtype = Convert.ToUInt32(ld["DriveType"] ?? 0u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        ldDetails[id] = (
                            ld["VolumeName"]?.ToString() is string vn && vn.Length > 0
                                ? vn
                                : dtype == 2 ? "Removable Disk" : "Local Disk",
                            ld["FileSystem"]?.ToString() ?? "—",
                            ld["Size"]      != null ? Convert.ToUInt64(ld["Size"])      : 0UL,
                            ld["FreeSpace"] != null ? Convert.ToUInt64(ld["FreeSpace"]) : 0UL
                        );
                    }
                    }
                }

                // Step 4: assemble map
                foreach (var (diskIdx, partDevIds) in partByDisk)
                {
                    var entries = new List<PartitionEntry>();
                    foreach (var partDev in partDevIds)
                    {
                        if (!logByPart.TryGetValue(partDev, out var logicals)) continue;

                        // Get the metadata for this partition
                        partMeta.TryGetValue(partDev, out var meta);

                        foreach (var pe in logicals)
                        {
                            var letter = pe.Letter.TrimEnd('\\');
                            if (ldDetails.TryGetValue(letter, out var d))
                            {
                                entries.Add(new PartitionEntry
                                {
                                    Letter          = pe.Letter,
                                    Label           = d.label,
                                    FileSystem      = d.fs,
                                    TotalGB         = d.total / 1_073_741_824.0,
                                    FreeGB          = d.free  / 1_073_741_824.0,
                                    IsActive        = meta.bootable,
                                    PartitionStyle  = meta.type,
                                    PartitionNumber = meta.index + 1,
                                });
                            }
                        }
                    }
                    if (entries.Count > 0)
                        map[diskIdx] = entries;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

            return map;
        }

        private static List<PartitionEntry> GetPartitionsForDisk(string? diskIndex,
            Dictionary<string, List<PartitionEntry>>? prefetchedMap = null)
        {
            if (diskIndex == null) return new();

            if (prefetchedMap != null)
                return prefetchedMap.TryGetValue(diskIndex, out var cached) ? cached : new();

            // Fallback: build map on the fly for this single disk (slow path, legacy compat)
            var map = BuildPartitionMap();
            if (map.TryGetValue(diskIndex, out var result))
                return result;

            // Last resort: all logical drives assigned to disk 0
            if (diskIndex == "0")
            {
                var parts = new List<PartitionEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
                    using (var __wmiTmp7 = searcher.Get())
                    {
                        foreach (ManagementObject ld in __wmiTmp7)
                    {
                        var total = ld["Size"]      != null ? Convert.ToUInt64(ld["Size"])      : 0UL;
                        var free  = ld["FreeSpace"] != null ? Convert.ToUInt64(ld["FreeSpace"]) : 0UL;
                        parts.Add(new PartitionEntry
                        {
                            Letter     = (ld["DeviceID"]?.ToString() ?? "") + "\\",
                            Label      = ld["VolumeName"]?.ToString() ?? "Local Disk",
                            FileSystem = ld["FileSystem"]?.ToString() ?? "—",
                            TotalGB    = total / 1_073_741_824.0,
                            FreeGB     = free  / 1_073_741_824.0,
                        });
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                return parts;
            }

            return new();
        }

        // Guard against overlapping SMART queries: if a previous WMI call is still
        // running in the background (Task.WaitAll timed out but tasks are alive),
        // a second call would write to the same disk list concurrently → race condition.
        // 0 = idle, 1 = running.
        private static int _smartRunning;

        private static int _enrichWaitMs = 15_000;

        private static bool EnrichWithSmartData(List<DiskHealthEntry> disks)
        {
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Interlocked.CompareExchange(ref _smartRunning, 1, 0) != 0)
            {
                if (waitSw.ElapsedMilliseconds >= _enrichWaitMs) return false;
                System.Threading.Thread.Sleep(50);
            }

            try
            {
                var pnpMap = BuildDiskPnpMap();
                var t1 = Task.Run(() => SmartFetchFailurePrediction(disks, pnpMap));
                var t2 = Task.Run(() => SmartFetchAtaData(disks, pnpMap));
                var t3 = Task.Run(() => SmartFetchDevicePaths(disks));
                var t4 = Task.Run(() => SmartFetchAtaThresholds(disks, pnpMap));
                bool completed = Task.WaitAll(new[] { t1, t2, t3, t4 }, TimeSpan.FromSeconds(6));

                // Note: if completed == false, WMI tasks may still be running in the background.
                // The _smartRunning flag stays at 1 until they all finish, preventing a new
                // overlapping call from launching in the meantime.
                if (completed)
                {
                    FinalizeSmartData(disks);
                    System.Threading.Interlocked.Exchange(ref _smartRunning, 0);
                }
                else
                    // Reset flag asynchronously once all tasks finish (or are abandoned)
                    Task.WhenAll(t1, t2, t3, t4).ContinueWith(_ =>
                    {
                        FinalizeSmartData(disks);
                        System.Threading.Interlocked.Exchange(ref _smartRunning, 0);
                    });
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "HardwareService.EnrichWithSmartData");
                System.Threading.Interlocked.Exchange(ref _smartRunning, 0);
                return false;
            }
            return true;
        }

        /// <summary>DiskIndex -> normalized PNPDeviceID (Win32_DiskDrive), to match the InstanceNames in root\WMI
        /// with the right physical disk.</summary>
        private static Dictionary<int, string> BuildDiskPnpMap()
        {
            var map = new Dictionary<int, string>();
            try
            {
                using var searcher = WmiHelper.Searcher("SELECT Index, PNPDeviceID FROM Win32_DiskDrive");
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (var __wmiTmp8 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp8)
                {
                    string pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                    if (pnp.Length == 0 || obj["Index"] == null) continue;
                    map[Convert.ToInt32(obj["Index"])] = NormalizePnpId(pnp);
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug("BuildDiskPnpMap: {Msg}", ex.Message); }
            return map;
        }

        /// <summary>Upper case, without the "_0" suffix that root\WMI adds to InstanceName.</summary>
        private static string NormalizePnpId(string id)
        {
            string s = id.Trim().ToUpperInvariant();
            int u = s.LastIndexOf('_');
            if (u > 0 && u < s.Length - 1 && s.Skip(u + 1).All(char.IsDigit)) s = s.Substring(0, u);
            return s;
        }

        /// <summary>
        /// The disk a WMI instance (InstanceName) belongs to. Order: (1) "PhysicalDriveN" in the name,
        /// (2) exact match on PNPDeviceID, (3) if there is only one disk, that one. NEVER "the first disk" on a system
        /// with several disks — one disk's data must not be put on another.
        /// </summary>
        private static DiskHealthEntry? MatchWmiInstanceToDisk(
            string instanceName, List<DiskHealthEntry> disks, Dictionary<int, string> pnpMap)
        {
            var m = RxPhysDrive.Match(instanceName);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                var byNumber = disks.FirstOrDefault(d => d.DiskIndex == n);
                if (byNumber != null) return byNumber;
            }

            string inst = NormalizePnpId(instanceName);
            foreach (var kv in pnpMap)
                if (kv.Value == inst)
                    return disks.FirstOrDefault(d => d.DiskIndex == kv.Key);

            return disks.Count == 1 ? disks[0] : null;
        }

        private static long _wmiSmartSkipUntilTicks;
        private static bool WmiSmartSkipped => DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _wmiSmartSkipUntilTicks);

        private static bool NoteWmiSmartUnsupported(Exception ex, string where)
        {
            if (ex is ManagementException me &&
                (me.ErrorCode == ManagementStatus.NotSupported || me.ErrorCode == ManagementStatus.InvalidClass ||
                 me.ErrorCode == ManagementStatus.InvalidNamespace || me.ErrorCode == ManagementStatus.NotFound))
            {
                bool alreadySkipping = WmiSmartSkipped;
                System.Threading.Interlocked.Exchange(ref _wmiSmartSkipUntilTicks, DateTime.UtcNow.AddSeconds(60).Ticks);
                if (!alreadySkipping)
                    AppLogger.Debug("WMI SMART ({Where}) not available on this system ({Code}) — normal on NVMe / some drivers; skipped for 60 s", where, me.ErrorCode);
                return true;
            }
            return false;
        }

        private static bool IsCriticalSmartAttr(byte attrId) =>
            attrId == 0x05 || attrId == 0xAA || attrId == 0xAB || attrId == 0xAC ||
            attrId == 0xB5 || attrId == 0xB6 || attrId == 0xBB || attrId == 0xBC ||
            attrId == 0xC4 || attrId == 0xC5 || attrId == 0xC6 || attrId == 0xC8;

        private static byte ToByte(int v) => (byte)Math.Clamp(v, 0, 255);

        /// <summary>
        /// Some makers pack several counters into the 6-byte raw value. Seagate's Command Timeout (0xBC) holds three 16-bit
        /// counters ("4 4 4" = 0x400040004 — the total, over 5 s, over 7.5 s) and its Power-On Hours (0x09) keeps the hours in the
        /// low 4 bytes. Read as one number they gave billions of timeouts. smartctl already decodes them (SmartctlService); this
        /// does the same when the data comes from Windows.
        /// </summary>
        internal static long UnpackRaw(byte attrId, long raw) => attrId switch
        {
            0xBC when raw > 0xFFFF     => raw & 0xFFFF,
            0x09 when raw > 0xFFFFFFFF => raw & 0xFFFFFFFF,
            _ => raw,
        };

        /// <summary>Overlays the smartctl data on the WMI data, then computes the health scores.</summary>
        private static void FinalizeSmartData(List<DiskHealthEntry> disks)
        {
            try { OverlaySmartctlData(disks); }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.OverlaySmartctlData"); }
            ComputeSmartHealthScores(disks);
        }

        /// <summary>
        /// When Windows could not say what kind of disk it is (MSFT_PhysicalDisk missing or silent), smartctl and the SMART table
        /// still can: smartctl's rotation rate (0 = solid state, rpm = spinning), else attributes only one kind has — spin-up time,
        /// seek errors, spin retries, load cycles, G-sense (HDD) vs. wear levelling / NAND writes (SSD).
        /// </summary>
        private static void RefineKindFromSmart(DiskHealthEntry d, SmartctlReport r)
        {
            // the form the disk reports about itself — also for a disk in a USB enclosure, read through its adapter
            if (r.FormFactor.Length > 0) d.FormFactor = r.FormFactor;
            if (d.Bus == "USB")
            {
                // smartctl reached the disk inside the enclosure: its rotation says HDD or SSD better than the adapter does
                string? usbKind = r.RotationRate > 0 ? "External HDD"
                                : r.RotationRate == 0 && d.Kind is "External drive" or "External HDD" ? "External SSD" : null;
                if (usbKind != null && usbKind != d.Kind && d.Kind != "USB stick")
                {
                    AppLogger.Info($"Disk #{d.DiskIndex} {d.Model}: the disk inside the USB enclosure is {usbKind} (rotation {r.RotationRate})");
                    d.Kind = usbKind;
                }
                if (r.RotationRate > 0) d.RotationRpm = r.RotationRate;
                return;
            }
            if (d.Kind is not ("Unknown" or "" or "SSD")) return;
            string? kind = null;
            if (r.RotationRate > 0) { kind = "HDD"; d.RotationRpm = r.RotationRate; }
            else if (r.RotationRate == 0 && d.Kind != "SSD") kind = "SSD";
            else if (r.RotationRate < 0 && r.AtaAttributes.Count > 0)
            {
                var ids = r.AtaAttributes.Select(a => a.Id).ToHashSet();
                int hddOnly = new[] { 0x03, 0x07, 0x0A, 0xBF, 0xC1, 0xC8, 0xDC, 0xDD }.Count(ids.Contains);
                int ssdOnly = new[] { 0xA9, 0xAD, 0xB1, 0xB4, 0xCA, 0xE7, 0xE8, 0xE9 }.Count(ids.Contains);
                if (hddOnly >= 2 && ssdOnly == 0) kind = "HDD";
                else if (ssdOnly >= 1 && hddOnly == 0 && d.Kind != "SSD") kind = "SSD";
            }
            if (kind == null || kind == d.Kind) return;
            AppLogger.Info($"Disk #{d.DiskIndex} {d.Model}: Windows said \"{d.Kind}\", smartctl / SMART say {kind} (rotation {r.RotationRate})");
            d.Kind = kind == "SSD" && d.Bus is "SATA" or "ATA" ? "SATA SSD" : kind;
            d.MediaType = kind;
        }

        /// <summary>When the disk did not say its form: NVMe = M.2; SATA SSDs of known M.2 series; 2.5" laptop HDD series.
        /// Nothing is guessed beyond that (a SATA SSD without a known series stays "SATA SSD").</summary>
        private static void FillFormFactorFallback(DiskHealthEntry d)
        {
            if (d.FormFactor.Length > 0) return;
            if (d.Kind == "NVMe SSD") d.FormFactor = "M.2";
            else if (d.Kind is "SATA SSD" or "SSD" && DriveForm.IsM2Sata(d.Model)) d.FormFactor = "M.2";
            else if (d.Kind is "HDD" or "External HDD" && DriveForm.Is25Hdd(d.Model)) d.FormFactor = "2.5\"";
        }

        private static void OverlaySmartctlData(List<DiskHealthEntry> disks)
        {
            if (!SmartctlService.IsAvailable) return;
            var targets = disks.Where(d => !string.IsNullOrWhiteSpace(d.PhysicalDevicePath)).ToList();
            if (targets.Count == 0) return;

            var reads = targets.Select(d => SmartctlService.ReadAsync(d.PhysicalDevicePath)).ToArray();
            // Bounded wait: a disk that does not answer must not keep the disk list blocked.
            if (!Task.WaitAll(reads, TimeSpan.FromSeconds(8)))
                AppLogger.Warning("HardwareService: smartctl did not answer within 8s for all disks — using the WMI data for them");

            for (int i = 0; i < targets.Count; i++)
            {
                if (!reads[i].IsCompletedSuccessfully) continue;
                var report = reads[i].Result;
                if (!report.Success) continue;

                bool replaced = ApplySmartctlAttributes(targets[i], report);
                try { RefineKindFromSmart(targets[i], report); } catch (Exception ex) { AppLogger.Debug($"RefineKindFromSmart: {ex.Message}"); }
                if (!report.OverallHealthPassed)
                {
                    targets[i].Status = "\u26a0 SMART Warning";
                    targets[i].HealthPercent = Math.Min(targets[i].HealthPercent, 30);
                }
                AppLogger.Debug("SMART source for disk {Idx} ({Model}): {Src}, overall {Health}",
                    targets[i].DiskIndex, targets[i].Model, replaced ? "smartctl" : (report.IsNvme ? "smartctl (NVMe, no ATA table)" : "WMI"),
                    report.OverallHealthPassed ? "PASSED" : "FAILED");
            }
        }

        /// <summary>
        /// Replaces the disk's SMART attributes with those read by smartctl (ATA only). Names and descriptions stay the
        /// existing ones (by ID); only vendor-specific attributes, which smartctl recognises from the disk model, take their
        /// name from it ("Unknown_*" stays "Vendor-Specific"). Returns false if nothing changed (NVMe / no attribute table).
        /// Public: also used after a self-test, to refresh the grid.
        /// </summary>
        public static bool ApplySmartctlAttributes(DiskHealthEntry disk, SmartctlReport report)
        {
            if (report == null || !report.Success) return false;
            if (!string.IsNullOrWhiteSpace(report.FirmwareVersion)) disk.Firmware = report.FirmwareVersion.Trim();
            if (report.SataLinkMaxTenths > 0)
            {
                disk.SataLinkMax = report.SataLinkMax; disk.SataLinkCurrent = report.SataLinkCurrent;
                disk.SataLinkMaxTenths = report.SataLinkMaxTenths; disk.SataLinkCurrentTenths = report.SataLinkCurrentTenths;
            }

            if (report.IsNvme)
            {
                var nvmeRows = SMDWin.Models.NvmeHealthRow.FromReport(report);
                if (nvmeRows.Count == 0) return false;
                disk.NvmeHealthRows = nvmeRows;
                return true;
            }
            if (report.AtaAttributes.Count == 0) return false;

            var list = new List<SMDWin.Models.SmartAttributeEntry>(report.AtaAttributes.Count);
            foreach (var a in report.AtaAttributes)
            {
                if (a.Id < 1 || a.Id > 255) continue;
                byte id = (byte)a.Id;
                string name = SmartAttrNameFor(id, disk.Model, a.Name);
                if (name.StartsWith("Vendor-Specific", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(a.Name)
                    && !a.Name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                    name = a.Name.Replace('_', ' ');

                var entry = new SMDWin.Models.SmartAttributeEntry
                {
                    Id             = id,
                    Name           = name,
                    CurrentValue   = ToByte(a.Value),
                    WorstValue     = ToByte(a.Worst),
                    RawValue       = a.RawValue,
                    ThresholdValue = ToByte(a.Threshold),
                    IsCritical     = IsCriticalSmartAttr(id),
                    Description    = GetSmartAttrDesc(id, a.RawValue),
                    SmartctlName   = a.Name ?? "",
                    SectorBytes    = report.LogicalBlockSize > 0 ? report.LogicalBlockSize : 512
                };

                if ((id == 0xF1 || id == 0xF2) && !string.IsNullOrWhiteSpace(a.Name)
                    && !a.Name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Name = a.Name.Replace('_', ' ');
                    entry.NvmeDataUnits = IsNvmeDrive(disk);
                    entry.Description = $"{(id == 0xF1 ? "Total written" : "Total read")}: {LifetimeData.Format(SsdLifeDetector.AttributeToBytes(entry), CultureInfo.InvariantCulture)}";
                }
                list.Add(entry);
            }
            if (list.Count == 0) return false;

            disk.SmartAttributes = list; // new list, not Clear()+Add(): whoever reads the old list never sees a half-built state
            MarkNvmeDataUnits(disk);
            return true;
        }

        private static bool IsNvmeDrive(DiskHealthEntry d)
            => d.Kind == "NVMe SSD"
            || string.Equals(d.MediaType, "NVMe", StringComparison.OrdinalIgnoreCase)
            || (d.Model ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// For an NVMe SSD that exposes its health as ATA-style attributes (e.g. through Intel RST / RAID), data written and
        /// read (0xF1 / 0xF2) are in NVMe data units, not sectors — otherwise 15 TB shows as "0.0 TB".
        /// Can be called at any time (once the disk type is known and after every attribute reload).
        /// </summary>
        public static void MarkNvmeDataUnits(DiskHealthEntry disk)
        {
            bool nvme = IsNvmeDrive(disk);
            foreach (var a in disk.SmartAttributes)
                if (a.Id == 0xF1 || a.Id == 0xF2) a.NvmeDataUnits = nvme;
        }

        private static void SmartFetchFailurePrediction(List<DiskHealthEntry> disks, Dictionary<int, string> pnpMap)
        {
            if (WmiSmartSkipped) return;
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Options.Timeout = TimeSpan.FromSeconds(3);
                var query = new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus");
                using var searcher = WmiHelper.Searcher(scope, query);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (var __wmiTmp9 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp9)
                {
                    bool fail = obj["PredictFailure"] is bool b && b;
                    if (!fail) continue;

                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    var target = MatchWmiInstanceToDisk(instanceName, disks, pnpMap);
                    if (target != null)
                    { target.Status = "\u26a0 SMART Warning"; target.HealthPercent = Math.Min(target.HealthPercent, 30); }
                }
                }
            }
            catch (Exception ex) { if (!NoteWmiSmartUnsupported(ex, "FailurePredictStatus")) AppLogger.Warning(ex, "HardwareService"); }
        }

        private static void SmartFetchAtaData(List<DiskHealthEntry> disks, Dictionary<int, string> pnpMap)
        {
            if (WmiSmartSkipped) return;
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Options.Timeout = TimeSpan.FromSeconds(3);
                var query = new ObjectQuery("SELECT * FROM MSStorageDriver_ATAPISmartData");
                using var searcher = WmiHelper.Searcher(scope, query);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);

                using (var __wmiTmp10 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp10)
                {
                    var rawData = obj["VendorSpecific"] as byte[];
                    if (rawData == null) continue;

                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    var target = MatchWmiInstanceToDisk(instanceName, disks, pnpMap);
                    if (target == null) continue;

                    target.SmartAttributes.Clear();
                    for (int i = 0; i < 30; i++)
                    {
                        int offset = 2 + i * 12;
                        if (offset + 12 > rawData.Length) break;
                        byte attrId = rawData[offset];
                        if (attrId == 0) continue;
                        byte current = rawData[offset + 3];
                        byte worst   = rawData[offset + 4];
                        long rawVal  = 0;
                        for (int bx = 0; bx < 6; bx++)
                            rawVal |= ((long)rawData[offset + 5 + bx]) << (bx * 8);
                        rawVal = UnpackRaw(attrId, rawVal);
                        bool critical = IsCriticalSmartAttr(attrId);
                        target.SmartAttributes.Add(new SMDWin.Models.SmartAttributeEntry
                        {
                            Id           = attrId,
                            Name         = SmartAttrNameFor(attrId, target.Model, null),
                            CurrentValue = current,
                            WorstValue   = worst,
                            RawValue     = rawVal,
                            IsCritical   = critical,
                            Description  = GetSmartAttrDesc(attrId, rawVal)
                        });
                    }
                }
                }
            }
            catch (Exception ex) { if (!NoteWmiSmartUnsupported(ex, "ATAPISmartData")) AppLogger.Warning(ex, "HardwareService"); }
        }

        private static void SmartFetchAtaThresholds(List<DiskHealthEntry> disks, Dictionary<int, string> pnpMap)
        {
            if (WmiSmartSkipped) return;
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Options.Timeout = TimeSpan.FromSeconds(3);
                var query = new ObjectQuery("SELECT * FROM MSStorageDriver_ATAPISmartThresholds");
                using var searcher = WmiHelper.Searcher(scope, query);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);

                using (var __wmiTmp11 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp11)
                {
                    var rawData = obj["VendorSpecific"] as byte[];
                    if (rawData == null) continue;

                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    var target = MatchWmiInstanceToDisk(instanceName, disks, pnpMap);
                    if (target == null) continue;

                    for (int i = 0; i < 30; i++)
                    {
                        int offset = 2 + i * 12;
                        if (offset + 2 > rawData.Length) break;
                        byte attrId = rawData[offset];
                        if (attrId == 0) continue;
                        byte threshold = rawData[offset + 1];
                        var attr = target.SmartAttributes.FirstOrDefault(a => a.Id == attrId);
                        if (attr != null) attr.ThresholdValue = threshold;
                    }
                }
                }
            }
            // Many controllers (especially NVMe/USB) do not expose this WMI class at all — that is normal, not logged as an
            // error; continue with ThresholdValue=0 (a fallback already handled in ComputeSmartHealthScores below).
            catch (Exception ex)
            {
                if (!NoteWmiSmartUnsupported(ex, "ATAPISmartThresholds")) AppLogger.Debug($"SmartFetchAtaThresholds: {ex.Message}");
            }
        }

        private static void ComputeSmartHealthScores(List<DiskHealthEntry> disks)
        {
            foreach (var target in disks)
            {
                if (target.SmartAttributes.Count == 0) continue;

                var wearAttr = target.SmartAttributes.FirstOrDefault(a =>
                    (a.Id == 0xE9 || a.Id == 0xE7 || a.Id == 0xE8)
                    && a.CurrentValue is > 0 and <= 100
                    && a.ThresholdValue > 0);

                int score;
                string scoreSource;
                if (wearAttr != null)
                {
                    score = wearAttr.CurrentValue;
                    scoreSource = $"wear-attr 0x{wearAttr.Id:X2} ({wearAttr.Name})={wearAttr.CurrentValue}% (threshold real={wearAttr.ThresholdValue})";
                }
                else
                {
                    // Pasul 2 — Current vs Threshold real, ca in specificatia ATA SMART.
                    score = 100;
                    var penalized = new List<string>();
                    foreach (var attr in target.SmartAttributes)
                    {
                        if (!attr.IsCritical) continue;
                        if (attr.ThresholdValue == 0) continue;
                        // Safety margin: penalize progressively as CurrentValue approaches Threshold, not only when it reaches it.
                        int margin = attr.CurrentValue - attr.ThresholdValue;
                        if (margin <= 0) { score -= 40; penalized.Add($"0x{attr.Id:X2} FAILED (cur={attr.CurrentValue} <= thr={attr.ThresholdValue})"); }
                        else if (margin <= 5) { score -= 20; penalized.Add($"0x{attr.Id:X2} close to the threshold (cur={attr.CurrentValue}, thr={attr.ThresholdValue})"); }
                        else if (margin <= 15) { score -= 8; penalized.Add($"0x{attr.Id:X2} small margin (cur={attr.CurrentValue}, thr={attr.ThresholdValue})"); }
                    }

                    var pohAttr = target.SmartAttributes.FirstOrDefault(a => a.Id == 0x09);
                    long powerOnHours = pohAttr?.RawValue ?? 0;

                    int[] backblazeTop5 = { 0x05, 0xBB, 0xBC, 0xC5, 0xC6 };
                    foreach (var attr in target.SmartAttributes)
                    {
                        if (!backblazeTop5.Contains(attr.Id) || attr.RawValue <= 0) continue;
                        if (attr.ThresholdValue > 0) continue; // already handled above with the real threshold — no double penalty

                        int penalty;
                        string basis;
                        if (attr.Id == 0xBC && powerOnHours > 0)
                        {
                            double ratePer1000h = attr.RawValue / (double)powerOnHours * 1000.0;
                            penalty = ratePer1000h switch
                            {
                                <= 1   => 2,
                                <= 5   => 4,
                                <= 20  => 7,
                                <= 100 => 12,
                                _      => 18,
                            };
                            basis = $"raw={attr.RawValue} @ {powerOnHours}h → {ratePer1000h:0.00}/1000h";
                        }
                        else
                        {
                            penalty = attr.RawValue switch
                            {
                                <= 5   => 2,
                                <= 20  => 4,
                                <= 100 => 7,
                                <= 500 => 12,
                                _      => 18,
                            };
                            basis = $"raw={attr.RawValue}";
                        }
                        score -= penalty;
                        penalized.Add($"0x{attr.Id:X2} ({attr.Name}) {basis} — an attribute with proven predictive value (Backblaze), no vendor threshold but not ignored");
                    }
                    var untrustedWear = target.SmartAttributes.FirstOrDefault(a =>
                        (a.Id == 0xE9 || a.Id == 0xE7 || a.Id == 0xE8) && a.CurrentValue is > 0 and <= 100 && a.ThresholdValue == 0);
                    if (untrustedWear != null)
                        penalized.Add($"(informative, not scored) 0x{untrustedWear.Id:X2}={untrustedWear.CurrentValue}%, threshold=0 — no real commitment from the maker");
                    scoreSource = penalized.Count > 0 ? string.Join("; ", penalized) : "all critical attributes OK or without a real threshold";
                }
                // Bad blocks and lost data count even when the maker's normalized value is still far from its threshold: a Samsung
                // SSD with 130 replaced blocks and 122 uncorrectable errors scored 88 % ("healthy") because the reallocation counter
                // still read 86 against a threshold of 10. The raw count is scored here (attributes without a threshold were already
                // scored by raw above, so only the ones with a threshold — or every one on the wear-based path — are added).
                {
                    var rawNotes = new List<string>();
                    foreach (var attr in target.SmartAttributes)
                    {
                        bool badBlockKind = attr.Id is 0x05 or 0xC5 or 0xC6 or 0xBB || attr.IsRuntimeBadBlock;
                        if (!badBlockKind || attr.RawValue <= 0) continue;
                        bool alreadyScored = wearAttr == null && attr.ThresholdValue == 0 && attr.Id is 0x05 or 0xBB or 0xC5 or 0xC6;
                        if (alreadyScored) continue;
                        if (attr.IsRuntimeBadBlock && target.SmartAttributes.Any(a => a.Id == 0x05 && a.RawValue == attr.RawValue))
                            continue;   // the same blocks as the reallocation counter (Samsung reports both) — counted once
                        int penalty = attr.RawValue switch { <= 5 => 2, <= 20 => 4, <= 100 => 7, <= 500 => 12, _ => 18 };
                        score -= penalty;
                        rawNotes.Add($"0x{attr.Id:X2} raw={attr.RawValue} -{penalty}");
                    }
                    // data the drive could not recover: never "healthy"
                    bool lostData = target.SmartAttributes.Any(a => a.Id is 0xBB or 0xC6 && a.RawValue > 0);
                    if (lostData && score > 79) { score = 79; rawNotes.Add("uncorrectable errors → at most 79 %"); }
                    if (rawNotes.Count > 0) scoreSource += "; raw counts: " + string.Join(", ", rawNotes);
                }

                score = Math.Max(0, Math.Min(100, score));

                if (score < target.HealthPercent) target.HealthPercent = score;
                target.Status = HealthTextFromPercent(target.HealthPercent, target.Status);

                AppLogger.Info($"S4-DIAG SmartHealth disk={target.DiskIndex} model=\"{target.Model}\" " +
                                $"finalScore={target.HealthPercent}% sursa=[{scoreSource}]");
            }
        }

        private static void SmartFetchDevicePaths(List<DiskHealthEntry> disks)
        {
            try
            {
                using var searcher = WmiHelper.Searcher(WmiHelper.DiskQuery);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (var __wmiTmp12 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp12)
                {
                    string devId = obj["DeviceID"]?.ToString() ?? "";
                    int number = -1;
                    var m = RxPhysDrive.Match(devId);
                    if (m.Success) number = int.Parse(m.Groups[1].Value);
                    else if (obj["Index"] != null) number = Convert.ToInt32(obj["Index"]);
                    if (number < 0) continue;

                    var target = disks.FirstOrDefault(d => d.DiskIndex == number);
                    if (target == null) continue;
                    target.PhysicalDevicePath = devId.Length > 0 ? devId : $@"\\.\PhysicalDrive{number}";
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

            // Disks with no match in Win32_DiskDrive: the path is built from the physical number.
            foreach (var d in disks)
                if (string.IsNullOrWhiteSpace(d.PhysicalDevicePath))
                    d.PhysicalDevicePath = $@"\\.\PhysicalDrive{d.DiskIndex}";
        }

        /// <summary>
        /// The attribute name for this drive. Attribute 183 (0xB7) is "SATA Downshift" on most drives, but on Samsung SSDs it counts
        /// the blocks that went bad while in use ("Runtime Bad Block") — reading it as cable trouble sent users to change a cable on a
        /// failing SSD. smartctl's own name wins when it has one.
        /// </summary>
        internal static string SmartAttrNameFor(byte id, string? model, string? smartctlName)
        {
            if (id == 0xB7)
            {
                if (!string.IsNullOrWhiteSpace(smartctlName)
                    && smartctlName.IndexOf("bad_block", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Runtime Bad Block";
                if ((model ?? "").IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) >= 0
                    && (model ?? "").IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Runtime Bad Block";
            }
            return GetSmartAttrName(id);
        }

        private static string GetSmartAttrName(byte id)
        {
            return id switch
            {
                0x01 => "Read Error Rate",
                0x03 => "Spin-Up Time",
                0x04 => "Start/Stop Count",
                0x05 => "Reallocated Sectors Count",
                0x07 => "Seek Error Rate",
                0x09 => "Power-On Hours",
                0x0A => "Spin Retry Count",
                0x0C => "Power Cycle Count",
                0xAA => "Available Reserved Space",
                0xAB => "SSD Program Fail Count",
                0xAC => "SSD Erase Fail Count",
                0xAD => "Wear Leveling Count",
                0xAE => "Unexpected Power Loss",
                0xB5 => "Program Fail Count",
                0xB6 => "Erase Fail Count",
                0xB7 => "SATA Downshift Errors",
                0xBB => "Uncorrected Errors",
                0xBC => "Command Timeout",
                0xBD => "High Fly Writes",
                0xBE => "Airflow Temperature",
                0xBF => "G-sense Error Rate",
                0xC0 => "Power-Off Retract Count",
                0xC1 => "Load/Unload Cycles",
                0xC2 => "Temperature",
                0xC4 => "Reallocation Events",
                0xC5 => "Current Pending Sectors",
                0xC6 => "Uncorrectable Sectors",
                0xC7 => "UltraDMA CRC Errors",
                0xC8 => "Write Error Rate",
                0xE7 => "SSD Life Left",
                0xE8 => "Available Reserved Space",
                0xE9 => "Media Wearout Indicator",
                0xF1 => "Total LBAs Written",
                0xF2 => "Total LBAs Read",
                0xF9 => "NAND Writes (GiB)",
                _ => $"Vendor-Specific 0x{id:X2}"
            };
        }

        private static string GetSmartAttrDesc(byte id, long raw)
        {
            return id switch
            {
                0x05 => raw == 0 ? "No reallocated sectors \u2714" : $"\u26a0 {raw} reallocated sector(s) — risk of data loss. Run surface scan.",
                0xC5 => raw == 0 ? "No pending sectors \u2714"     : $"\u26a0 {raw} unstable sector(s) — run a surface scan now.",
                0xC6 => raw == 0 ? "No unrecoverable sectors \u2714" : $"\U0001F534 {raw} unrecoverable sector(s) — back up immediately!",
                0xC2 => $"Current temperature: {raw & 0xFF}\u00b0C",
                0x09 => $"{raw:N0} hours powered on ({raw / 24:N0} days)",
                0xE7 => raw > 0 ? $"SSD life remaining: {raw}%" : "N/A",
                0xE8 => "Reserved (spare) NAND blocks remaining",
                0xE9 => "SSD wear indicator — decreases from 100 as the drive wears",
                0xF1 => $"Total written: {(Math.Max(0, SsdLifeDetector.LbasToGB(raw)) / 1000.0).ToString("F1", CultureInfo.InvariantCulture)} TB",
                0xF2 => $"Total read: {(Math.Max(0, SsdLifeDetector.LbasToGB(raw)) / 1000.0).ToString("F1", CultureInfo.InvariantCulture)} TB",
                0xBB => raw == 0 ? "No uncorrectable errors \u2714" : $"\u26a0 {raw} uncorrectable error(s)",
                0xBC => raw == 0 ? "No command timeouts \u2714" : $"{raw} command timeout(s)",
                0x01 => raw == 0 ? "Read error rate: OK \u2714" : $"Read error rate: {raw}",
                0xAD => $"Wear leveling: {raw:N0} cycle(s)",
                0x07 => raw == 0 ? "Seek error rate: OK \u2714" : $"Seek error rate: {raw}",
                _    => ""   // no standard meaning: the column stays empty (the row is hidden from the table while it is 0)
            };
        }

        public async Task<List<RamEntry>> GetRamAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<RamEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(WmiHelper.RamQuery);
                    using (var __wmiTmp13 = searcher.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp13)
                    {
                        var cap = obj["Capacity"] is ulong c ? FormatSize(c) : "—";
                        ulong capBytes = obj["Capacity"] is ulong cb ? cb : 0;

                        int configuredMHz = 0, ratedMHz = 0;
                        try { configuredMHz = Convert.ToInt32(obj["ConfiguredClockSpeed"] ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { ratedMHz      = Convert.ToInt32(obj["Speed"] ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        double voltV = 0, minVoltV = 0, maxVoltV = 0;
                        try { voltV    = Convert.ToDouble(obj["ConfiguredVoltage"] ?? 0) / 1000.0; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { minVoltV = Convert.ToDouble(obj["MinVoltage"]        ?? 0) / 1000.0; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { maxVoltV = Convert.ToDouble(obj["MaxVoltage"]        ?? 0) / 1000.0; } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        int dataW = 64, totalW = 64;
                        try { dataW  = Convert.ToInt32(obj["DataWidth"]  ?? 64); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { totalW = Convert.ToInt32(obj["TotalWidth"] ?? 64); } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        var slotLabel = obj["DeviceLocator"]?.ToString()?.Trim()
                                     ?? obj["BankLabel"]?.ToString()?.Trim()
                                     ?? $"Slot {results.Count + 1}";

                        results.Add(new RamEntry
                        {
                            BankLabel      = obj["BankLabel"]?.ToString() ?? slotLabel,
                            SlotLabel      = slotLabel,
                            SlotIndex      = results.Count,
                            Manufacturer   = obj["Manufacturer"]?.ToString()?.Trim() ?? "—",
                            PartNumber     = obj["PartNumber"]?.ToString()?.Trim() ?? "—",
                            Capacity       = cap,
                            CapacityBytes  = capBytes,
                            Speed          = ratedMHz > 0 ? $"{ratedMHz} MHz" : "—",
                            ConfiguredMHz  = configuredMHz,
                            RatedMHz       = ratedMHz,
                            VoltageV       = voltV,
                            MinVoltageV    = minVoltV,
                            MaxVoltageV    = maxVoltV,
                            DataWidthBits  = dataW,
                            TotalWidthBits = totalW,
                            IsEcc          = totalW > dataW && totalW > 0 && dataW > 0,
                            MemoryType     = RefineLpddr(GetMemoryType(obj["SMBIOSMemoryType"]?.ToString(), ratedMHz), obj["PartNumber"]?.ToString()),
                            FormFactor     = GetFormFactor(obj["FormFactor"]?.ToString()),
                            IsEmpty        = false,
                        });
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // Detect total slot count and add empty slot entries
                try
                {
                    using var arr = WmiHelper.Searcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                    using (var __wmiTmp14 = arr.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp14)
                    {
                        int totalSlots = Convert.ToInt32(obj["MemoryDevices"] ?? 0);
                        for (int i = results.Count; i < totalSlots; i++)
                        {
                            results.Add(new RamEntry
                            {
                                BankLabel    = $"Slot {i + 1}",
                                Manufacturer = "—",
                                PartNumber   = "—",
                                Capacity     = "—",
                                Speed        = "—",
                                MemoryType   = "—",
                                FormFactor   = "—",
                                IsEmpty      = true,
                            });
                        }
                        break; // only first array
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                foreach (var r in results)
                {
                    r.Manufacturer = DecodeHexIfPrintableAscii(r.Manufacturer);
                    r.PartNumber   = DecodeHexIfPrintableAscii(r.PartNumber);
                    r.Manufacturer = DecodeJedecManufacturerId(r.Manufacturer);
                }

                var slotGroups = results.Where(r => !r.IsEmpty).GroupBy(r => r.SlotLabel);
                foreach (var group in slotGroups)
                {
                    if (group.Count() <= 1) continue;
                    int idx = 0;
                    foreach (var r in group)
                    {
                        r.SlotLabel = $"{r.SlotLabel} #{++idx}";
                    }
                }

                return results;
            });
        }

        public async Task<List<TemperatureEntry>> GetTemperaturesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<TemperatureEntry>();

                // Try LibreHardwareMonitor via TempReader
                try
                {
                    var reader = TempReader.Borrow(out bool ownsReader);
                    using var ownedReader = ownsReader ? reader : null;   // closed only if it was opened here
                    var snap = reader.Read();
                    // a reader opened just now often has no values on its first update (LibreHardwareMonitor fills them on the
                    // second one) — that gave a false "Temperatures unavailable" in the self-test
                    for (int retry = 0; retry < 2 && !(snap.CpuTemp > 0) && !(snap.GpuTemp > 0); retry++)
                    {
                        System.Threading.Thread.Sleep(1200);
                        snap = reader.Read();
                    }

                    if (snap.CpuTemp.HasValue && snap.CpuTemp > 0)
                        results.Add(new TemperatureEntry { Name = $"CPU — {snap.CpuSensorName}", Temperature = Math.Round(snap.CpuTemp.Value, 1) });

                    if (snap.GpuTemp.HasValue && snap.GpuTemp > 0)
                        results.Add(new TemperatureEntry { Name = $"GPU — {snap.GpuSensorName}", Temperature = Math.Round(snap.GpuTemp.Value, 1) });
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // WMI fallback
                if (results.Count == 0)
                {
                    try
                    {
                        using var searcher = WmiHelper.Searcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                        int i = 1;
                        using (var __wmiTmp15 = searcher.Get())
                        {
                            foreach (ManagementObject obj in __wmiTmp15)
                        {
                            var tempK = obj["CurrentTemperature"];
                            if (tempK == null) continue;
                            double c = Convert.ToDouble(tempK) / 10.0 - 273.15;
                            if (c > 10 && c < 120)
                                results.Add(new TemperatureEntry { Name = $"Thermal Zone {i++}", Temperature = Math.Round(c, 1) });
                        }
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                }

                if (results.Count == 0)
                {
                    // say the real reason, not always "run as Admin" (the app usually runs as administrator already)
                    string why = !PermissionHelper.IsRunningAsAdmin() ? "run SMD Win as administrator"
                               : !PawnIOService.IsInstalled() ? "the sensor driver (PawnIO) is not installed"
                               : "no sensor answered";
                    results.Add(new TemperatureEntry { Name = $"Temperatures unavailable — {why}", Temperature = -1 });
                }

                return results;
            });
        }

        private volatile SystemSummary? _summaryCache;
        private DateTime _summaryCacheTime = DateTime.MinValue;
        private static readonly TimeSpan _summaryCacheTtl = TimeSpan.FromMinutes(5);
        private readonly System.Threading.SemaphoreSlim _summaryLock = new(1, 1);
        private static readonly string SummaryDiskCachePath = ResolveSummaryDiskCachePath();

        private static string ResolveSummaryDiskCachePath()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(AppLogger.LogDirectory) ?? AppLogger.LogDirectory, "cache");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "systemsummary-cache.json");
            }
            catch { return ""; }
        }

        // The disk cache belongs to one computer: with the app on a USB stick, a file left from another PC is not used
        // (otherwise the Dashboard and Temp & Stress would show the other computer's components).
        private sealed class SummaryDiskCache
        {
            public string MachineId { get; set; } = "";
            public SystemSummary? Summary { get; set; }
        }

        private static SystemSummary? LoadSummaryDiskCache()
        {
            try
            {
                if (string.IsNullOrEmpty(SummaryDiskCachePath) || !File.Exists(SummaryDiskCachePath)) return null;
                var c = System.Text.Json.JsonSerializer.Deserialize<SummaryDiskCache>(File.ReadAllText(SummaryDiskCachePath));
                if (c?.Summary == null || string.IsNullOrEmpty(c.MachineId)) return null;   // old format, no computer identity: the data is read again
                if (!MachineIdentity.IsThisMachine(c.MachineId))
                {
                    AppLogger.Info("HardwareService: the hardware cache is from another computer — ignored, this computer's data is read");
                    return null;
                }
                return c.Summary;
            }
            catch (Exception ex) { AppLogger.Debug($"HardwareService.LoadSummaryDiskCache: {ex.Message}"); return null; }
        }

        private static void SaveSummaryDiskCache(SystemSummary s)
        {
            try
            {
                if (string.IsNullOrEmpty(SummaryDiskCachePath)) return;
                File.WriteAllText(SummaryDiskCachePath, System.Text.Json.JsonSerializer.Serialize(new SummaryDiskCache { MachineId = MachineIdentity.Current, Summary = s }));
            }
            catch (Exception ex) { AppLogger.Debug($"HardwareService.SaveSummaryDiskCache: {ex.Message}"); }
        }

        /// <summary>Cheap clone through a JSON round-trip — SystemSummary is a flat POCO (only string/int/float/bool), so this
        /// is safe and simpler than a hand-maintained copy constructor for 30+ fields.</summary>
        private static SystemSummary CloneSummary(SystemSummary s) =>
            System.Text.Json.JsonSerializer.Deserialize<SystemSummary>(System.Text.Json.JsonSerializer.Serialize(s))!;

        // the hardware read (Hardware.Info) takes 0.6-1 s: the result is reused for a minute (the data is almost static)
        private static HardwareInfo? _sharedHw;
        private static DateTime _sharedHwAt = DateTime.MinValue;
        private static readonly object _sharedHwLock = new();
        private static HardwareInfo GetSharedHardwareInfo()
        {
            lock (_sharedHwLock)
            {
                if (_sharedHw != null && (DateTime.UtcNow - _sharedHwAt).TotalSeconds < 60) return _sharedHw;
                var hw = new HardwareInfo();
                hw.Load();
                _sharedHw = hw; _sharedHwAt = DateTime.UtcNow;
                return hw;
            }
        }

        public async Task<SystemSummary> GetSystemSummaryAsync()
        {
            // Fast path — the memory cache is still valid
            if (_summaryCache != null && (DateTime.Now - _summaryCacheTime) < _summaryCacheTtl)
            {
                var cached = CloneSummary(_summaryCache);
                cached.Uptime = FetchUptime(); // the only field that is always fresh
                return cached;
            }

            await _summaryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_summaryCache != null && (DateTime.Now - _summaryCacheTime) < _summaryCacheTtl)
                {
                    var cached = CloneSummary(_summaryCache);
                    cached.Uptime = FetchUptime();
                    return cached;
                }

                bool isColdStart = _summaryCacheTime == DateTime.MinValue;
                if (isColdStart)
                {
                    var diskCached = LoadSummaryDiskCache();
                    if (diskCached != null && !string.IsNullOrEmpty(diskCached.Cpu))
                    {
                        _summaryCache = diskCached;
                        _summaryCacheTime = DateTime.Now - _summaryCacheTtl + TimeSpan.FromSeconds(10);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fresh = await FetchSystemSummaryFresh().ConfigureAwait(false);
                                _summaryCache = fresh;
                                _summaryCacheTime = DateTime.Now;
                                SaveSummaryDiskCache(fresh);
                            }
                            catch (Exception ex) { AppLogger.Debug($"HardwareService background summary refresh: {ex.Message}"); }
                        });
                        var result = CloneSummary(diskCached);
                        result.Uptime = FetchUptime();
                        return result;
                    }
                }

                var s = await FetchSystemSummaryFresh().ConfigureAwait(false);
                _summaryCache = s;
                _summaryCacheTime = DateTime.Now;
                SaveSummaryDiskCache(s);
                return CloneSummary(s);
            }
            finally { _summaryLock.Release(); }
        }

        private async Task<SystemSummary> FetchSystemSummaryFresh()
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await Task.Run(async () =>
            {
                var s = new SystemSummary();
                var __sw = System.Diagnostics.Stopwatch.StartNew();
                void __Lap(string label)
                {
                    if (__sw.ElapsedMilliseconds > 300)
                        AppLogger.Warning($"S97-DIAG GetSystemSummaryAsync: {label} took {__sw.ElapsedMilliseconds}ms");
                    __sw.Restart();
                }
                try
                {
                    var hw = GetSharedHardwareInfo();
                    __Lap("hw.Load() [CPU/GPU/RAM/Battery/System/Display — Hardware.Info lib]");

                    s.ComputerName = hw.System.Host;
                    s.Manufacturer = hw.System.Manufacturer;
                    s.Model        = hw.System.Model;
                    // Lenovo puts the machine type in "Model" (e.g. 20LW000WGE); the known name (ThinkPad L580) is in the system version
                    // in the BIOS. Read from the registry (instant, no WMI). The machine type stays visible as the motherboard model.
                    try
                    {
                        if ((s.Manufacturer ?? "").Contains("LENOVO", StringComparison.OrdinalIgnoreCase))
                        {
                            using var biosKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                            static bool Usable(string v) => v.Length >= 3
                                && !v.Equals("Lenovo", StringComparison.OrdinalIgnoreCase)
                                && !v.Contains("INVALID", StringComparison.OrdinalIgnoreCase)
                                && !v.Contains("Not Available", StringComparison.OrdinalIgnoreCase)
                                && !v.Contains("O.E.M", StringComparison.OrdinalIgnoreCase)
                                && !v.Contains("Default string", StringComparison.OrdinalIgnoreCase)
                                && !v.Equals("None", StringComparison.OrdinalIgnoreCase)
                                && !v.StartsWith("System ", StringComparison.OrdinalIgnoreCase);
                            string friendly = (biosKey?.GetValue("SystemVersion") as string ?? "").Trim();
                            if (!Usable(friendly)) friendly = (biosKey?.GetValue("SystemFamily") as string ?? "").Trim();
                            if (Usable(friendly) && !friendly.Equals((s.Model ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                if (friendly.StartsWith("Lenovo ", StringComparison.OrdinalIgnoreCase)) friendly = friendly.Substring(7).Trim();
                                s.Model = friendly;
                                s.Manufacturer = "Lenovo";
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"Lenovo friendly model: {ex.Message}"); }
                    s.OsName       = hw.System.WindowsName;
                    s.OsVersion    = hw.System.OS;
                    s.OsBuild      = hw.System.Build;
                    s.OsBuildNumber = hw.System.BuildNumber;
                    s.Architecture = hw.System.Arch;
                    s.InstallDate  = hw.System.InstallDate;

                    if (hw.Cpu != null)
                    {
                        s.Cpu       = hw.Cpu.Name;
                        s.CpuCores  = $"{hw.Cpu.Cores} cores / {hw.Cpu.Threads} threads";
                        s.CpuMaxMHz = hw.Cpu.MaxClockMHz > 0 ? $"{hw.Cpu.MaxClockMHz} MHz" : "";
                        var cacheparts = new List<string>();
                        if (hw.Cpu.L2KB > 0) cacheparts.Add($"L2: {(hw.Cpu.L2KB >= 1024 ? $"{hw.Cpu.L2KB/1024} MB" : $"{hw.Cpu.L2KB} KB")}");
                        if (hw.Cpu.L3KB > 0) cacheparts.Add($"L3: {(hw.Cpu.L3KB >= 1024 ? $"{hw.Cpu.L3KB/1024} MB" : $"{hw.Cpu.L3KB} KB")}");
                        s.CpuCache = cacheparts.Count > 0 ? string.Join("  ", cacheparts) : "";
                    }
                    if (hw.Displays.Count > 0)
                    {
                        // Show all monitor resolutions
                        if (hw.Displays.Count == 1)
                        {
                            var d = hw.Displays[0];
                            s.DisplayResolution = $"{d.Width} × {d.Height}" + (d.RefreshHz > 0 ? $", {d.RefreshHz} Hz" : "");   // "2560 × 1440, 59 Hz"
                        }
                        else
                        {
                            var parts = hw.Displays.Select((d, i) =>
                                $"{i+1}: {d.Width} × {d.Height}" + (d.RefreshHz > 0 ? $", {d.RefreshHz} Hz" : ""));
                            s.DisplayResolution = string.Join("\n", parts);
                        }
                        // Strip \\.\DISPLAY prefix — only keep a friendly label
                        string rawName = hw.Displays[0].Name ?? "";
                        s.DisplayName  = rawName.StartsWith(@"\\.\") ? "" : rawName;
                        s.DisplayCount = hw.Displays.Count;
                    }

                    if (hw.Gpus.Count > 0)
                    {
                        var realGpus = hw.Gpus.Where(g => !string.IsNullOrEmpty(g.Name) && !WmiCache.IsBasicOrVirtualAdapter(g.Name)).ToList();
                        var chosenGpu = realGpus.FirstOrDefault(g => !g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                            ?? realGpus.FirstOrDefault()
                            ?? hw.Gpus[0];
                        s.GpuName = chosenGpu.Name;
                        s.GpuVram = chosenGpu.VramGB > 0 ? $"{chosenGpu.VramGB.ToString("F1", CultureInfo.InvariantCulture)} GB" : "—";
                        s.GpuDriverVersion = chosenGpu.Driver ?? "";
                    }
                    else if (!string.IsNullOrEmpty(WmiCache.Instance.GpuName))
                    {
                        // Fallback: use WMI Win32_VideoController when DXGI finds nothing
                        s.GpuName = WmiCache.Instance.GpuName;
                        s.GpuVram = "—";
                    }

                    if (hw.RamTotalGB > 0) {
                        int rounded = SMDWin.Services.UnitFormat.NominalRamGB(hw.RamTotalGB);
                        s.TotalRam = $"{rounded} GB";
                    } else { s.TotalRam = "—"; }

                    if (hw.Battery != null)
                    {
                        s.HasBattery       = true;
                        s.BatteryStatus    = hw.Battery.Status;
                        s.BatteryCharge    = hw.Battery.ChargePercent + "%";
                        s.BatteryChargeInt = hw.Battery.ChargePercent;
                        s.BatteryWearPct   = (hw.Battery.DesignMWh > 0 && hw.Battery.FullMWh > 0)
                            ? (float)Math.Max(0, 100.0 - (double)hw.Battery.FullMWh / hw.Battery.DesignMWh * 100.0)
                            : -1f;
                        // BatteryMfr / BatteryCycles / BatteryRuntime stay at their defaults — HwBatteryInfo has no such data;
                        // the real source is BatteryService (BatteryInfo).
                    }

                    var uptimeTask  = Task.Run(FetchUptime);
                    var ramTypeTask = Task.Run(FetchRamType);
                    var biosTask    = Task.Run(FetchBiosVersion);
                    await Task.WhenAll(uptimeTask, ramTypeTask, biosTask).ConfigureAwait(false);
                    // Safe to use .Result here — all tasks are guaranteed complete after WhenAll
                    s.Uptime      = uptimeTask.Result;
                    s.RamType     = ramTypeTask.Result;
                    s.BiosVersion = biosTask.Result;
                    __Lap("Uptime+RamType+BIOS (paralel, WMI)");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                return s;
            }, cts.Token);
        }

        // ── Private WMI helpers with individual 3s timeouts ──────────────────

        private static string FetchUptime()
        {
            // since power-on (PowerOnClock), not WMI LastBootUpTime: with Fast Startup that is the last full restart, often weeks ago
            try
            {
                var on = PowerOnClock.SinceOn;
                int days = (int)on.TotalDays;
                return days >= 1 ? $"{days}d {on.Hours}h {on.Minutes}m" : $"{on.Hours}h {on.Minutes}m";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
            return "";
        }

        private static string FetchRamType()
        {
            // Returns the full label, e.g. "DDR4 · 2 × 8 GB · 3200 MHz" or "DDR5 · 1×16GB · 4800MHz" depending on the installed modules
            try
            {
                using var searcher = WmiHelper.Searcher(
                    "SELECT SMBIOSMemoryType, Capacity, Speed FROM Win32_PhysicalMemory");

                string memType = "";
                var capacitiesMB = new List<long>();
                int speed = 0;

                using (var __wmiTmp17 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp17)
                {
                    if (speed == 0 && obj["Speed"] != null)
                        int.TryParse(obj["Speed"]?.ToString(), out speed);

                    if (string.IsNullOrEmpty(memType))
                        memType = GetMemoryType(obj["SMBIOSMemoryType"]?.ToString(), speed);

                    if (obj["Capacity"] is ulong cap && cap > 0)
                        capacitiesMB.Add((long)(cap / 1_048_576UL));
                }
                }

                if (string.IsNullOrEmpty(memType)) return "";

                // Builds the label: DDR4 · 2 × 8 GB · 3200 MHz; different modules: DDR3 · 4 GB + 2 GB · 1600 MHz
                var parts = new List<string> { memType };
                string modules = ModulesText(capacitiesMB);
                if (modules.Length > 0) parts.Add(modules);

                if (speed > 0)
                    parts.Add($"{speed} MHz");

                return string.Join(" · ", parts);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.FetchRamType"); }
            return "";
        }

        /// <summary>"2 × 8 GB" when the modules are identical; otherwise each size: "4 GB + 2 GB", "2 × 8 GB + 4 GB"
        /// (never an average: a 4 GB and a 2 GB module must not show as "2 × 3 GB").</summary>
        internal static string ModulesText(IReadOnlyCollection<long> capacitiesMB)
        {
            static string Size(long mb) => mb >= 1024 ? $"{Math.Round(mb / 1024.0, mb % 1024 == 0 ? 0 : 1)} GB" : $"{mb} MB";
            var groups = capacitiesMB.Where(m => m > 0).GroupBy(m => m).OrderByDescending(g => g.Key).ToList();
            if (groups.Count == 0) return "";
            // one module: "1 × 8 GB" — a bare "8 GB" repeated the total next to it ("8 GB · DDR4 · 8 GB · 2667 MHz")
            // and hid that the PC runs on a single module
            if (groups.Count == 1 && groups[0].Count() == 1) return $"1 × {Size(groups[0].Key)}";
            return string.Join(" + ", groups.Select(g => g.Count() > 1 ? $"{g.Count()} × {Size(g.Key)}" : Size(g.Key)));
        }

        private static string FetchBiosVersion()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var searcher = WmiHelper.Searcher(
                    "SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate FROM Win32_BIOS");
                using (var __wmiTmp18 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp18)
                {
                    var ver  = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    var mfr  = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var date = "";
                    if (obj["ReleaseDate"] is string rd && rd.Length >= 8)
                    {
                        if (DateTime.TryParseExact(rd[..8], "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            date = $" ({SMDWin.Services.DateFormatHelper.FormatDate(dt)})";
                    }
                    // Build a concise label: skip manufacturer if it duplicates the version string
                    string label = string.IsNullOrEmpty(ver) ? mfr : ver;
                    return $"BIOS: {label}{date}";
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
            return "";
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        public static string FormatSize(ulong bytes)
        {
            if (bytes >= 1_099_511_627_776UL) return $"{(bytes / 1_099_511_627_776.0).ToString("F1", CultureInfo.InvariantCulture)} TB";
            if (bytes >= 1_073_741_824UL)     return $"{Math.Round(bytes / 1_073_741_824.0):F0} GB";
            if (bytes >= 1_048_576UL)         return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes} B";
        }


        private static string DetectMediaType(string model)
        {
            var u = model.ToUpperInvariant();
            // NVMe, SSD explicit in name
            if (u.Contains("NVME") || u.Contains("NVM") || u.Contains("SSD") ||
                u.Contains("SOLID") || u.Contains("M.2") || u.Contains("PCIe") ||
                u.Contains("SAMSUNG") && (u.Contains("PM") || u.Contains("EVO") || u.Contains("PRO")) ||
                u.Contains("WD") && u.Contains("SN") ||
                u.Contains("KINGSTON") || u.Contains("CRUCIAL") || u.Contains("SABRENT") ||
                u.Contains("MICRON") || u.Contains("INTEL") && (u.Contains("SSD") || u.Contains("660") || u.Contains("760") || u.Contains("900")) ||
                u.Contains("TOSHIBA") && (u.Contains("TR") || u.Contains("TH") || u.Contains("KSG")) ||
                u.Contains("PLEXTOR") || u.Contains("ADATA") || u.Contains("PATRIOT") ||
                u.Contains("MTFD") || u.Contains("MTFD") || // Micron part numbers (MTFDDAV, MTFHD etc.)
                u.Contains("CT") && u.Contains("BX") || u.Contains("CT") && u.Contains("MX") || // Crucial part numbers
                u.Contains("SANDISK") ||
                u.Contains("HYNIX") ||
                u.Contains("LITEON") || u.Contains("LITE-ON") || u.Contains("LITE ON"))
                return "SSD";
            return "Unknown";
        }

        private static string DetectMediaTypeFull(ManagementObject obj)
        {
            try
            {
                try
                {
                    var bt = obj["BusType"];
                    if (bt != null)
                    {
                        uint busType = Convert.ToUInt32(bt);
                        if (busType == 7)  return "USB";   // USB — the only reliable signal for "external"
                        if (busType == 17) return "NVMe";  // NVMe — enum oficial MSFT_PhysicalDisk.BusType
                    }
                }
                catch (ManagementException) { }

                // Try SpindleSpeed — 0 means no spinning disk (SSD/NVMe; NVMe was already caught above if BusType was available —
                // from here on it is genuinely ambiguous between SATA SSD and NVMe on controllers that do not expose BusType
                // correctly, very rare in practice).
                try
                {
                    var spindle = obj["SpindleSpeed"];
                    if (spindle != null && Convert.ToUInt32(spindle) == 0)
                        return "SSD";
                }
                catch (ManagementException) { /* property not in this class — skip */ }

                // MediaType field: 3=HDD, 4=SSD, 5=SCM
                try
                {
                    var mt = obj["MediaType"]?.ToString();
                    if (mt == "4") return "SSD";
                    if (mt == "3") return "HDD";
                }
                catch (ManagementException) { /* property not in this class — skip */ }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.DetectMediaTypeFull"); }
            // Fallback to name heuristic — try both FriendlyName (MSFT_Disk) and Model (Win32_DiskDrive)
            string model;
            try { model = obj["FriendlyName"]?.ToString() ?? ""; } catch { model = ""; }
            if (string.IsNullOrEmpty(model))
                try { model = obj["Model"]?.ToString() ?? ""; } catch { model = ""; }
            return DetectMediaType(model);
        }
        // ══ The exact type of each disk ═══════════════════════════════════════
        // Order of the signals, most reliable first: BusType (NVMe / USB / SD / virtual) → MediaType (3 = HDD, 4 = SSD)
        // → SpindleSpeed (0 = no platters, >0 = RPM) → model name (last resort). MSFT_Disk (where the disks come from) has
        // NO MediaType / SpindleSpeed — they are taken from MSFT_PhysicalDisk, matched by disk number.
        private static void ClassifyDisks(List<DiskHealthEntry> disks)
        {
            var phys = new Dictionary<int, (int bus, int media, long rpm, string name)>();
            try
            {
                using var ps = WmiHelper.Searcher(@"\\.\root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, BusType, MediaType, SpindleSpeed, FriendlyName FROM MSFT_PhysicalDisk");
                using var col = ps.Get();
                foreach (ManagementObject pd in col)
                {
                    using (pd)
                    {
                        if (!int.TryParse(pd["DeviceId"]?.ToString(), out int id)) continue;
                        int bus = 0, media = 0; long rpm = -1;
                        try { bus = Convert.ToInt32(pd["BusType"] ?? 0); } catch (Exception ex) { AppLogger.Debug($"BusType: {ex.Message}"); }
                        try { media = Convert.ToInt32(pd["MediaType"] ?? 0); } catch (Exception ex) { AppLogger.Debug($"MediaType: {ex.Message}"); }
                        try { if (pd["SpindleSpeed"] != null) rpm = Convert.ToInt64(pd["SpindleSpeed"]); } catch (Exception ex) { AppLogger.Debug($"SpindleSpeed: {ex.Message}"); }
                        phys[id] = (bus, media, rpm, pd["FriendlyName"]?.ToString() ?? "");
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"ClassifyDisks MSFT_PhysicalDisk: {ex.Message}"); }

            foreach (var d in disks)
            {
                if (!phys.TryGetValue(d.DiskIndex, out var p)) p = (0, 0, -1, "");   // no data → nothing "firm"
                string u = ((d.Model ?? "") + " " + (p.name ?? "")).ToUpperInvariant();
                bool rpmKnown = p.rpm > 0 && p.rpm < 100000;              // 0xFFFFFFFF = unknown
                bool noSpin   = p.rpm == 0;
                bool nameNvme = u.Contains("NVME") || u.Contains("NVM EXPRESS");
                bool nameSsd  = DetectMediaType(d.Model ?? "") == "SSD";
                // Seagate: "ST500LM021-1KJ152" — the letters are followed by digits, so no \b after them (it missed every Seagate HDD)
                bool nameHdd  = System.Text.RegularExpressions.Regex.IsMatch(u, @"\bST\d{3,5}(DM|LM|NM|VN|VX|VM|AS|DX|NX)\d|WDC WD\d|\bWD\d{2,3}[A-Z]{2}[A-Z]{1,2}\b|HGST|HITACHI|\bHDW[A-Z]\d|\bDT01ACA|\bMQ0\dABF|\bHD\d{3}[A-Z]{2}\b");
                bool nameStick = System.Text.RegularExpressions.Regex.IsMatch(u, @"FLASH|CRUZER|ULTRA|DATATRAVELER|JUMPDRIVE|USB DISK|STICK|THUMB|PENDRIVE|USB DRIVE");
                bool mediaHdd = p.media == 3, mediaSsd = p.media == 4 || p.media == 5;
                bool hdd = mediaHdd || rpmKnown || (!mediaSsd && !noSpin && nameHdd && !nameSsd);
                bool ssd = !hdd && (mediaSsd || noSpin || nameSsd || nameNvme);
                // MSFT_Disk.BusType is already in MediaType as "USB" / "NVMe" (DetectMediaTypeFull); without MSFT_PhysicalDisk (a damaged
                // Storage Management API, e.g. ThinkPad T540p) a "… USB Device" name or a stick name still means USB
                int bus = p.bus != 0 ? p.bus
                        : d.MediaType == "USB" ? 7 : d.MediaType == "NVMe" ? 17
                        : u.Contains("USB DEVICE") || (nameStick && !nameSsd) ? 7 : 0;

                d.RotationRpm = rpmKnown ? (int)p.rpm : 0;
                d.Bus = bus switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", 12 => "SD", 13 => "MMC",
                    3 => "ATA", 1 => "SCSI", 15 => "Virtual", 16 => "Storage Spaces", _ => ""
                };
                if (bus == 17 || (nameNvme && bus is 8 or 16 or 0)) d.Kind = "NVMe SSD";
                else if (bus == 7)
                {
                    // On USB the adapter often reports "no rotation" (rpm 0) and even "SSD" (media 4) for HDDs or sticks: a 2.5" WD HDD
                    // on USB showed as "External SSD", a Toshiba TransMemory stick too. Here the model name and size matter, not those
                    // two signals.
                    bool stickName = nameStick || System.Text.RegularExpressions.Regex.IsMatch(u,
                        @"TRANSMEMORY|JETFLASH|TRAVELER|STORE ?N ?GO|MASS STORAGE|USB FLASH|\bDT\d|\bDTX\b|\bSE9\b|SURVIVOR|PATRIOT|VERBATIM|SILICON POWER|\bUSB 3\.\d DEVICE");
                    bool small = d.SizeBytes > 0 && d.SizeBytes <= 300UL * 1000 * 1000 * 1000;
                    d.Kind = mediaHdd || rpmKnown || (nameHdd && !nameSsd) ? "External HDD"
                           : stickName || (small && !nameSsd && !nameNvme) ? "USB stick"
                           : nameSsd || nameNvme ? "External SSD" : "External drive";
                }
                else if (bus is 12 or 13) d.Kind = "SD card";
                else if (bus == 15) d.Kind = "Virtual disk";
                else if (hdd) d.Kind = "HDD";
                else if (ssd) d.Kind = bus is 11 or 3 ? "SATA SSD" : "SSD";
                else d.Kind = "Unknown";

                // MediaType (used by the rest of the app) — corrected only when the signals are firm
                if (d.MediaType is "Unknown" or "" || (d.MediaType == "SSD" && (mediaHdd || rpmKnown)) || (d.MediaType == "HDD" && (mediaSsd || noSpin)))
                    d.MediaType = d.Kind switch
                    {
                        "NVMe SSD" => "NVMe", "HDD" => "HDD", "SATA SSD" or "SSD" => "SSD",
                        "USB stick" or "External HDD" or "External SSD" or "External drive" => "USB",
                        _ => d.MediaType
                    };
                MarkNvmeDataUnits(d);
                AppLogger.Debug($"ClassifyDisks: #{d.DiskIndex} {d.Model} → {d.Kind} (bus {bus}, media {p.media}, rpm {p.rpm})");
            }
        }

        private static string GetHealthText(string code) => code switch { "0" => "Healthy", "1" => "Warning", "2" => "Unhealthy", _ => "Unknown" };
        private static int    ParseHealth(string code)   => code switch { "0" => 100, "1" => 60, "2" => 20, _ => 75 };

        internal static string HealthTextFromPercent(int healthPercent, string currentStatus)
        {
            if (!string.IsNullOrEmpty(currentStatus) && currentStatus.StartsWith("\u26a0"))
                return currentStatus;
            // the same limits as DriveVerdict (90 / 60), so the status text and the verdict shown everywhere agree
            return healthPercent >= 90 ? "Healthy" : healthPercent >= 60 ? "Warning" : "Critical";
        }

        public static string GetDiskHealthReason(DiskHealthEntry disk)
        {
            if (disk.HealthPercent >= 80) return "";

            var realloc = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0x05);
            var pending = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC5);
            var uncorrectable = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC6);
            var predictFail = disk.Status.StartsWith("\u26a0");

            if (predictFail)
                return LiteralTexts.Translate("The disk itself reported that it may fail soon (SMART failure prediction).");
            if (uncorrectable != null && uncorrectable.RawValue > 0)
                return string.Format(LiteralTexts.Translate("{0} sector(s) with uncorrectable errors (SMART 0xC6)."), uncorrectable.RawValue);
            if (realloc != null && realloc.RawValue > 0)
                return string.Format(LiteralTexts.Translate("{0} reallocated sector(s) — they were bad and the disk replaced them internally (SMART 0x05)."), realloc.RawValue);
            if (pending != null && pending.RawValue > 0)
                return string.Format(LiteralTexts.Translate("{0} sector(s) waiting to be reallocated (SMART 0xC5) — possibly bad, not confirmed yet."), pending.RawValue);
            return LiteralTexts.Translate("One or more SMART attributes are below the normal threshold.");
        }
        private static readonly Dictionary<string, string> _jedecBank1Ids = new(StringComparer.OrdinalIgnoreCase)
        {
            ["802C"] = "Micron",
            ["80CE"] = "Samsung",
            ["80AD"] = "Hynix",     // JEP106: "Hyundai Electronics" — renamed SK Hynix, the same code
            ["8098"] = "Toshiba",
            ["80C1"] = "Infineon", // later Qimonda
            ["80DA"] = "Winbond",
        };

        private static string DecodeJedecManufacturerId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "—") return raw;
            string hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
            return _jedecBank1Ids.TryGetValue(hex, out var name) ? name : raw;
        }

        private static string DecodeHexIfPrintableAscii(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "—") return raw;
            string hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return raw;

            try
            {
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

                if (bytes.Length == 0) return raw;
                foreach (var b in bytes)
                    if (b < 0x20 || b > 0x7E) return raw; // not clean ASCII text — keep the original

                var decoded = System.Text.Encoding.ASCII.GetString(bytes).Trim();
                return string.IsNullOrWhiteSpace(decoded) ? raw : decoded;
            }
            catch { return raw; }
        }

        public static string GetMemoryType(string? c, int speedMHz = 0)
        {
            string known = c switch
            {
                // SMBIOS memory types: 0x1A DDR4, 0x1B–0x1E LPDDR…LPDDR4, 0x22 DDR5, 0x23 LPDDR5
                "26" => "DDR4", "34" => "DDR5", "35" => "LPDDR5",
                "27" => "LPDDR", "28" => "LPDDR2", "29" => "LPDDR3", "30" => "LPDDR4",
                "24" => "DDR3", "22" => "DDR2", "21" => "DDR2",
                _ => "",
            };
            if (!string.IsNullOrEmpty(known)) return known;

            // Generic/unknown SMBIOS code ("20"/"0"/"1"/"2" or anything else unrecognised) — estimated from the configured speed.
            if (speedMHz >= 3200) return "DDR5";
            if (speedMHz >= 2400) return "DDR4";
            if (speedMHz >= 800)  return "DDR3";   // include zona ambigua 1600-2133
            if (speedMHz >= 400)  return "DDR2";
            return speedMHz > 0 ? "DDR" : "DDR3";  // no useful speed at all: DDR3 is more likely than literal DDR
        }
        /// <summary>
        /// Many laptops report soldered LPDDR memory as plain DDR4 / DDR5. The chip part number tells them apart: Samsung K3…,
        /// Micron MT53 / MT62, SK hynix H9… / H58… are LPDDR chips (modules have other part numbers, e.g. M471…, CT…, HMA…).
        /// </summary>
        public static string RefineLpddr(string type, string? partNumber)
        {
            if (type.StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(partNumber)) return type;
            string pn = partNumber.Trim().ToUpperInvariant();
            bool lp = pn.StartsWith("K3") || pn.StartsWith("MT53") || pn.StartsWith("MT62") || pn.StartsWith("H9") || pn.StartsWith("H58");
            if (!lp) return type;
            return type switch { "DDR5" => "LPDDR5", "DDR4" => "LPDDR4", "DDR3" => "LPDDR3", _ => type };
        }

        private static string GetFormFactor(string? c)   => c switch { "8" => "DIMM", "12" => "SO-DIMM", "13" => "SO-DIMM", _ => "—" };

        // ── Fallback without WMI ──────────────────────────────────────────────
        // When WMI does not answer (some laptops give "Timed out" at start), each volume letter is mapped to its physical disk
        // directly through Windows: IOCTL_STORAGE_GET_DEVICE_NUMBER on \\.\X: gives the disk number (the same as Win32_DiskDrive.Index).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct StorageDeviceNumber { public uint DeviceType; public uint DeviceNumber; public uint PartitionNumber; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            out StorageDeviceNumber outBuf, uint outSize, out uint returned, IntPtr overlapped);

        /// <summary>Number of the physical disk that holds volume "C:" (or null if it cannot be found).</summary>
        private static (int Disk, int Partition)? DiskNumberOfDrive(string driveLetter)
        {
            try
            {
                using var h = CreateFileW($@"\\.\{driveLetter.TrimEnd('\\', ':')}:", 0, 3 /* read|write share */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;
                if (!DeviceIoControl(h, 0x002D1080 /* IOCTL_STORAGE_GET_DEVICE_NUMBER */, IntPtr.Zero, 0, out var num, (uint)System.Runtime.InteropServices.Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero))
                    return null;
                return ((int)num.DeviceNumber, (int)num.PartitionNumber);
            }
            catch (Exception ex) { AppLogger.Debug($"DiskNumberOfDrive({driveLetter}): {ex.Message}"); return null; }
        }

        private static Dictionary<string, List<PartitionEntry>> BuildPartitionMapFallback()
        {
            var map = new Dictionary<string, List<PartitionEntry>>();
            try
            {
                foreach (var di in System.IO.DriveInfo.GetDrives())
                {
                    if (di.DriveType != System.IO.DriveType.Fixed && di.DriveType != System.IO.DriveType.Removable) continue;
                    if (!di.IsReady) continue;
                    try
                    {
                        var loc = DiskNumberOfDrive(di.Name);
                        if (loc == null) continue;
                        string key = loc.Value.Disk.ToString();
                        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<PartitionEntry>();
                        list.Add(new PartitionEntry
                        {
                            Letter          = di.RootDirectory.FullName,
                            Label           = di.VolumeLabel.Length > 0 ? di.VolumeLabel : (di.DriveType == System.IO.DriveType.Removable ? "Removable Disk" : "Local Disk"),
                            FileSystem      = di.DriveFormat,
                            TotalGB         = di.TotalSize / 1_073_741_824.0,
                            FreeGB          = di.AvailableFreeSpace / 1_073_741_824.0,
                            PartitionNumber = loc.Value.Partition,
                        });
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                AppLogger.Info($"BuildPartitionMapFallback: volumes mapped to disks without WMI — {string.Join(", ", map.Select(kv => $"disc {kv.Key}: {string.Join("+", kv.Value.Select(e => e.Letter.TrimEnd('\\')))}"))}");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildPartitionMapFallback"); }
            return map;
        }
    }
}
