using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public static class SelfTestService
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
        private const int GDI_OBJECTS  = 0;
        private const int USER_OBJECTS = 1;

        public class SelfTestOptions
        {
            public double BenchmarkPerTestSeconds = 3.3; // x3 teste ≈ 10s
            public int CrashLookbackDays = 30;
        }

        public class SelfTestResult
        {
            public string Report = "";
            public int WarnCount;
            public int FailCount;
            public int HwDbKnownCount;
            public int HwDbTotalCount;
        }

        /// <summary>
        /// Runs the backend checks. progress is called for every new log line (used by the UI for live display).
        /// </summary>
        public static async Task<SelfTestResult> RunAsync(
            Action<string>? progress = null,
            SelfTestOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SelfTestOptions();
            var sb = new StringBuilder();
            int warnCount = 0, failCount = 0;

            void Log(string line)
            {
                sb.AppendLine(line);
                progress?.Invoke(line);
            }
            void Section(string title)
            {
                Log("");
                Log($"── {title} " + new string('─', Math.Max(1, 58 - title.Length)));
            }
            void Ok(string label, string detail = "") => Log($"  ✔ {label}" + (detail.Length > 0 ? $"  —  {detail}" : ""));
            void Warn(string label, string detail = "") { warnCount++; Log($"  ⚠ {label}" + (detail.Length > 0 ? $"  —  {detail}" : "")); }
            void Fail(string label, string detail = "") { failCount++; Log($"  ✘ {label}" + (detail.Length > 0 ? $"  —  {detail}" : "")); }

            void Expect(string label, double actual, double expected, double tolerance, string unit = "")
            {
                double diff = Math.Abs(actual - expected);
                if (diff <= tolerance)
                    Ok(label, $"{actual:F2}{unit} (asteptat {expected:F2}{unit})");
                else
                    Fail(label, $"got {actual:F2}{unit}, expected {expected:F2}{unit} (diff {diff:F2}, toleranta {tolerance:F2})");
            }

            int hwDbKnown = 0, hwDbTotal = 0;
            void TrackHwDb(bool known) { hwDbTotal++; if (known) hwDbKnown++; }

            var hw = new HardwareService();
            var driverSvc = new DriverService();
            SMDWin.Models.SystemSummary? sum = null;

            // ── 1. SYSTEM INFO ──────────────────────────────────────────────
            Section("SYSTEM INFO");
            try
            {
                sum = await hw.GetSystemSummaryAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                Ok("CPU", sum.Cpu);
                Ok("GPU", sum.GpuName);
                Ok("RAM", $"{sum.TotalRam} ({sum.RamType})");
                Ok("OS", $"{sum.OsName} {sum.OsBuild} ({sum.OsBuildNumber}) · " +
                   (sum.OsArch.Length > 0 ? sum.OsArch : OsRegistryInfo.Current.Architecture));   // OsArch was never filled in
                Ok("Model", $"{sum.Manufacturer} {sum.Model}");
                if (string.IsNullOrWhiteSpace(sum.Cpu)) Warn("CPU empty in SystemSummary — possibly slow/stuck WMI");
                if (string.IsNullOrWhiteSpace(sum.GpuName)) Warn("GPU empty in SystemSummary");
            }
            catch (Exception ex) { Fail("GetSystemSummaryAsync threw an exception", ex.Message); }

            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool isAdmin = new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if (isAdmin) Ok("Admin", "Elevated"); else Warn("Admin", "Running without elevation — some features (SMART, driver install) may be missing data");
            }
            catch (Exception ex) { Fail("Admin check failed", ex.Message); }

            Section("CALCULATION SELF-CHECKS");
            try
            {
                const double diameter = 84;
                const double thickness = 9;

                // 100% -> the whole circle visible, offset 0
                var full = RingProgressCalculator.Calculate(diameter, thickness, 100);
                Expect("Ring @ 100% — offset", full.OffsetUnits, 0.0, 0.01);

                // 0% -> nothing visible, offset = the whole dash (empty circle)
                var empty = RingProgressCalculator.Calculate(diameter, thickness, 0);
                Expect("Ring @ 0% — offset", empty.OffsetUnits, empty.DashUnits, 0.01);

                // 77% -> the visible arc must be ~77% of the Ellipse's real circumference, not the whole circle.
                var seventySeven = RingProgressCalculator.Calculate(diameter, thickness, 77);
                double visiblePx = RingProgressCalculator.VisibleArcLengthPx(seventySeven, thickness);
                double expectedVisiblePx = seventySeven.CircumferencePx * 0.77;
                Expect("Ring @ 77% — arc vizibil (px)", visiblePx, expectedVisiblePx, 0.5, "px");

                // 50% -> half of the circle visible
                var half = RingProgressCalculator.Calculate(diameter, thickness, 50);
                double halfVisiblePx = RingProgressCalculator.VisibleArcLengthPx(half, thickness);
                Expect("Ring @ 50% — arc vizibil (px)", halfVisiblePx, half.CircumferencePx * 0.5, 0.5, "px");
            }
            catch (Exception ex) { Fail("RingProgressCalculator self-check threw an exception", ex.Message); }

            // ── 2. DRIVERS & DEVICES ──────────────────────────────────────────
            Section("DRIVERS & DEVICES");
            try
            {
                bool pawnIo = PawnIOService.IsInstalled();
                if (pawnIo) Ok("PawnIO driver", "installed"); else Warn("PawnIO driver", "NOT installed — CPU/GPU sensors may be missing data");
            }
            catch (Exception ex) { Fail("PawnIOService.IsInstalled() threw an exception", ex.Message); }

            try
            {
                var missing = await driverSvc.GetMissingDevicesAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (missing.Count == 0) Ok("Missing devices (Device Manager)", "none");
                else
                {
                    Warn($"Missing/errored devices: {missing.Count}");
                    foreach (var d in missing.Take(15))
                    {
                        bool looksUsb = d.DeviceClass?.Contains("USB", StringComparison.OrdinalIgnoreCase) == true;
                        string hint = looksUsb && (d.ErrorCode == 10 || d.ErrorCode == 12 || d.ErrorCode == 43)
                            ? "  (often insufficient USB power — try a different port or a powered hub)" : "";
                        Log($"      - {d.Name}  [{d.DeviceClass}]  error code {d.ErrorCode}{hint}");
                    }
                    if (missing.Count > 15) Log($"      … and {missing.Count - 15} more");
                }
            }
            catch (Exception ex) { Fail("GetMissingDevicesAsync threw an exception", ex.Message); }

            try
            {
                var unsigned = await driverSvc.GetUnsignedDriversAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (unsigned.Count == 0) Ok("Unsigned drivers", "none");
                else Warn($"Unsigned drivers found: {unsigned.Count}", string.Join(", ", unsigned.Take(5).Select(d => d.DisplayName)));
            }
            catch (Exception ex) { Fail("GetUnsignedDriversAsync threw an exception", ex.Message); }

            try
            {
                var allDrivers = await driverSvc.GetDriversAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (allDrivers.Count == 0) Fail("GetDriversAsync", "0 drivers returned — the Drivers page would appear empty");
                else Ok($"Drivers enumerated (Win32_PnPSignedDriver)", $"{allDrivers.Count}");
            }
            catch (Exception ex) { Fail("GetDriversAsync threw an exception", ex.Message); }

            // ── 3. SENSORS (idle reading, no stress) ──────────────────────
            Section("SENSORS (idle)");
            try
            {
                var idleTemps = await hw.GetTemperaturesAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (idleTemps.Count == 0) Fail("GetTemperaturesAsync", "0 sensors returned — no temperature sensor detected");
                else if (idleTemps.Count == 1 && idleTemps[0].Temperature < 0)
                    Warn($"No temperature read: {idleTemps[0].Name.Replace("Temperatures unavailable — ", "")}");   // the real reason, from GetTemperaturesAsync
                else
                {
                    Ok($"Temperature sensors detected: {idleTemps.Count}");
                    int nullCount = idleTemps.Count(t => t.Temperature <= 0);
                    if (nullCount > 0) Warn($"Sensors with invalid/N-A value: {nullCount}/{idleTemps.Count}");
                    foreach (var t in idleTemps.Take(20))
                        Log($"      - {t.Name,-28} {t.Display}");
                }
            }
            catch (Exception ex) { Fail("GetTemperaturesAsync threw an exception", ex.Message); }

            try
            {
                var ram = await hw.GetRamAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (ram.Count == 0) Warn("GetRamAsync", "0 RAM modules returned");
                else
                {
                    // the list includes empty slots (IsEmpty) — count the modules actually installed
                    int installed = ram.Count(m => !m.IsEmpty);
                    Ok($"RAM modules detected: {installed}" + (installed != ram.Count ? $" (in {ram.Count} slots)" : ""));
                }
            }
            catch (Exception ex) { Fail("GetRamAsync threw an exception", ex.Message); }

            try
            {
                var disks = await hw.GetDisksAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (disks.Count == 0) Warn("GetDisksAsync", "0 disks returned");
                else
                {
                    Ok($"Disks detected: {disks.Count}");
                    // the exact type and form, and the USB link — what the Storage page and the PDF show (11.11.2026)
                    foreach (var d in disks)
                    {
                        Log($"      - #{d.DiskIndex} {d.Model,-30} {d.TypeText}{(d.UsbLinkText.Length > 0 ? " · " + d.UsbLinkText : "")}" +
                            $"   [kind {d.Kind}, bus {d.Bus}, form {(d.FormFactor.Length > 0 ? d.FormFactor : "?")}, rpm {d.RotationRpm}]");
                        if (d.UsbSlowLink) Warn($"{d.Model}: USB 3 device running at {d.UsbConnected} — USB 2 port, cable or hub");
                    }
                }
            }
            catch (Exception ex) { Fail("GetDisksAsync threw an exception", ex.Message); }

            Section("FREE DISK SPACE (system drive)");
            try
            {
                string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var driveInfo = new DriveInfo(sysDrive);
                double freeGB = driveInfo.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                double totalGB = driveInfo.TotalSize / 1024.0 / 1024 / 1024;
                double freePct = 100.0 * driveInfo.AvailableFreeSpace / driveInfo.TotalSize;
                string detail = $"{freeGB:F1} GB free / {totalGB:F0} GB ({freePct:F0}%)";
                if (freePct < 10) Warn($"System drive ({sysDrive}) low on space", $"{detail} — under 10% free can cause Windows Update failures, pagefile issues, and general slowdowns");
                else if (freePct < 15) Warn($"System drive ({sysDrive}) getting full", $"{detail} — under the commonly recommended 15% free threshold");
                else Ok($"System drive ({sysDrive})", detail);
            }
            catch (Exception ex) { Log($"  · Free disk space check unavailable: {ex.Message}"); }

            Section("WINDOWS UPDATE HISTORY");
            try
            {
                using var qfeSearcher = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", System.TimeSpan.FromSeconds(10));
                using var __wmiQfe = qfeSearcher.Get();
                var updates = __wmiQfe.Cast<System.Management.ManagementObject>()
                    .Select(o => new { Id = o["HotFixID"]?.ToString(), Date = o["InstalledOn"]?.ToString() })
                    .Where(u => !string.IsNullOrEmpty(u.Date))
                    .ToList();
                if (updates.Count == 0) Log("  · No update history available via Win32_QuickFixEngineering on this system");
                else
                {
                    var parsed = updates
                        .Select(u => (u.Id, Date: DateTime.TryParse(u.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null))
                        .Where(u => u.Date.HasValue)
                        .OrderByDescending(u => u.Date)
                        .ToList();
                    if (parsed.Count > 0)
                    {
                        var latest = parsed[0];
                        int daysAgo = Math.Max(0, (int)(DateTime.Now - latest.Date!.Value).TotalDays);
                        Ok("Most recent update", $"{latest.Id} — {latest.Date:dd-MM-yyyy} ({daysAgo} day(s) ago)");
                        if (daysAgo <= 3)
                            Log("      · Installed very recently — if problems started around this time, this update is a reasonable first suspect");
                    }
                }
            }
            catch (Exception ex) { Log($"  · Windows Update history unavailable: {ex.Message}"); }

            Section("PAGEFILE (VIRTUAL MEMORY)");
            try
            {
                using var pfSearcher = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage", System.TimeSpan.FromSeconds(10));
                using var __wmiPf = pfSearcher.Get();
                var pageFiles = __wmiPf.Cast<System.Management.ManagementObject>().ToList();
                if (pageFiles.Count == 0)
                    Warn("No pagefile configured", "system-managed virtual memory is off — can cause instability under memory pressure, especially with 8GB RAM or less");
                else
                {
                    long totalMB = pageFiles.Sum(p => Convert.ToInt64(p["AllocatedBaseSize"] ?? 0L));
                    Ok("Pagefile", $"{totalMB} MB allocated across {pageFiles.Count} file(s)");
                }
            }
            catch (Exception ex) { Log($"  · Pagefile check unavailable: {ex.Message}"); }

            Section("STORAGE RELIABILITY (NVMe + SATA, unified)");
            try
            {
                var scope = new System.Management.ManagementScope(@"root\Microsoft\Windows\Storage");
                scope.Connect();
                using var physSearcher = SMDWin.Services.WmiHelper.Searcher(scope,
                    new System.Management.ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk"), System.TimeSpan.FromSeconds(10));
                int diskCount = 0, warnedCount = 0;
                using (var __wmiTmp1 = physSearcher.Get())
                {
                    foreach (System.Management.ManagementObject disk in __wmiTmp1)
                {
                    diskCount++;
                    string devId = disk["DeviceId"]?.ToString() ?? "?";
                    string friendly = disk["FriendlyName"]?.ToString() ?? "Disk " + devId;
                    // BusType: 17 = NVMe, 11 = SATA, 7 = USB, 8 = RAID — see the MSFT_PhysicalDisk docs
                    int busType = disk["BusType"] is ushort bt ? bt : 0;
                    string busLabel = busType switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", _ => $"bus {busType}" };

                    try
                    {
                        using var relSearcher = SMDWin.Services.WmiHelper.Searcher(scope,
                            new System.Management.ObjectQuery($"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId='{devId}'"), System.TimeSpan.FromSeconds(10));
                        using var __wmiRel = relSearcher.Get();
                        var rel = __wmiRel.Cast<System.Management.ManagementObject>().FirstOrDefault();
                        if (rel == null) { Log($"  · {friendly} ({busLabel}) — no reliability counters exposed by this device/driver"); continue; }

                        object? wearObj = rel["Wear"];
                        object? rdUncObj = rel["ReadErrorsUncorrected"];
                        object? wrUncObj = rel["WriteErrorsUncorrected"];
                        object? tempObj = rel["Temperature"];
                        object? pohObj = rel["PowerOnHours"];

                        var parts = new List<string>();
                        bool hasWarning = false;

                        if (wearObj != null && Convert.ToInt32(wearObj) > 0)
                        {
                            int wear = Convert.ToInt32(wearObj);
                            parts.Add($"wear={wear}%");
                            if (wear >= 90) hasWarning = true;
                        }
                        if (rdUncObj != null && Convert.ToUInt64(rdUncObj) > 0)
                        { parts.Add($"read errors uncorrected={rdUncObj}"); hasWarning = true; }
                        if (wrUncObj != null && Convert.ToUInt64(wrUncObj) > 0)
                        { parts.Add($"write errors uncorrected={wrUncObj}"); hasWarning = true; }
                        if (tempObj != null && Convert.ToInt32(tempObj) > 0)
                            parts.Add($"temp={tempObj}°C");
                        if (pohObj != null)
                            parts.Add($"power-on hours={pohObj}");

                        string detail = parts.Count > 0 ? string.Join(", ", parts) : "no wear/error data reported";
                        if (hasWarning)
                        {
                            warnedCount++;
                            Warn($"{friendly} ({busLabel}) reliability", detail);
                        }
                        else
                        {
                            Ok($"{friendly} ({busLabel})", detail);
                        }
                    }
                    catch (Exception ex) { Log($"  · {friendly} ({busLabel}) — could not read reliability counters: {ex.Message}"); }
                }
                }
                if (diskCount == 0) Log("  · No physical disks found via MSFT_PhysicalDisk (unusual — Storage Management API may be unavailable on this system)");
            }
            catch (Exception ex) { Log($"  · Storage reliability check unavailable: {ex.Message} (needs Windows 8+ Storage Management API)"); }

            // ── STORAGE — IDENTIFICATION & MEDIA TYPE CROSS-CHECK ───────────────
            Section("STORAGE IDENTIFICATION (model + media type cross-check)");
            try
            {
                var disks = await hw.GetDisksAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (disks.Count == 0)
                    Fail("GetDisksAsync", "0 disks returned — the Storage page would appear empty");
                foreach (var d in disks)
                {
                    Ok($"Disk {d.DiskIndex}", $"{d.Model}  ·  {d.MediaType}  ·  {d.Size}  ·  Health {d.HealthPercent}%  ·  Serial {d.SerialNumber}");

                    // Cross-check #1: an INTERNAL disk (no Removable/Network partition) labelled "USB"/"external" is almost certainly a
                    // BusType misdetection (e.g. RAID(8) mistaken for USB on laptops with Intel RST) — reported explicitly.
                    bool anyRemovablePartition = d.Partitions.Any(p =>
                    {
                        try
                        {
                            var l = p.Letter?.TrimEnd('\\');
                            if (string.IsNullOrEmpty(l)) return false;
                            var dt = new DriveInfo(l).DriveType;
                            return dt == DriveType.Removable || dt == DriveType.Network;
                        }
                        catch { return false; }
                    });
                    bool hasBootOrSystemPartition = d.Partitions.Any(p =>
                        !string.IsNullOrEmpty(p.Letter) &&
                        p.Letter!.TrimEnd(':', '\\').Equals(
                            Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd(':', '\\'),
                            StringComparison.OrdinalIgnoreCase));
                    if (hasBootOrSystemPartition && !anyRemovablePartition &&
                        d.MediaType.Contains("extern", StringComparison.OrdinalIgnoreCase))
                    {
                        Fail($"Disk {d.DiskIndex} media type inconsistency",
                            $"contains the Windows boot partition but is labeled '{d.MediaType}' (external) — likely a BusType mis-detection");
                    }

                    // Cross-check #2: the model explicitly contains "SSD"/"NVMe" but MediaType came out "HDD" — the name heuristic was
                    // ignored or overridden by a wrong WMI signal.
                    bool nameSaysFlash = d.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                        || d.Model.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
                    if (nameSaysFlash && d.MediaType.StartsWith("HDD", StringComparison.OrdinalIgnoreCase))
                        Fail($"Disk {d.DiskIndex} media type inconsistency",
                            $"model name says SSD/NVMe but MediaType='{d.MediaType}'");

                    // generic USB sticks and SD cards ("USB DISK 2.0") have no catalogue model
                    if (d.Kind is "USB stick" or "SD card")
                        Log($"  · Disk {d.DiskIndex}: removable flash media — not checked against the hardware database.");
                    else if (SMDWin.Services.HardwareDatabaseService.LookupStorage(d.Model) == null)
                    {
                        TrackHwDb(known: false);
                        Warn($"Disk {d.DiskIndex} not in hardware database", $"model=\"{d.Model}\" — add to Data/HardwareDB/storage.json in a future session");
                    }
                    else TrackHwDb(known: true);
                }
            }
            catch (Exception ex) { Fail("GetDisksAsync threw an exception", ex.Message); }

            Section("HARDWARE DATABASE COVERAGE (GPU + RAM)");
            try
            {
                string gpuName = sum?.GpuName ?? "";
                if (string.IsNullOrWhiteSpace(gpuName))
                    Log("  · No GPU name resolved by SystemSummary — skipping database check.");
                else if (SMDWin.Services.HardwareDatabaseService.LookupGpu(gpuName) == null)
                {
                    TrackHwDb(known: false);
                    Warn("GPU not in hardware database", $"model=\"{gpuName}\" — add to Data/HardwareDB/gpu.json in a future session");
                }
                else
                {
                    TrackHwDb(known: true);
                    Ok("GPU in hardware database", gpuName);
                }
            }
            catch (Exception ex) { Warn("GPU database check threw an exception", ex.Message); }
            try
            {
                var ramModules = await hw.GetRamAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
                foreach (var m in ramModules.Where(m => !m.IsEmpty))
                {
                    string ramName = $"{m.Manufacturer} {m.PartNumber}".Trim();
                    if (string.IsNullOrWhiteSpace(m.PartNumber))
                        Log($"  · {m.BankLabel}: no part number reported — skipping database check.");
                    else if (SMDWin.Services.HardwareDatabaseService.Ram.FirstOrDefault(r =>
                                 r.Key.Contains(SMDWin.Services.HardwareDatabaseService.NormalizeKey(m.PartNumber))) == null)
                    {
                        TrackHwDb(known: false);
                        Warn($"RAM module {m.BankLabel} not in hardware database", $"part=\"{m.PartNumber}\" — add to Data/HardwareDB/ram.json in a future session");
                    }
                    else
                    {
                        TrackHwDb(known: true);
                        Ok($"RAM module {m.BankLabel} in hardware database", ramName);
                    }
                }
            }
            catch (Exception ex) { Warn("RAM database check threw an exception", ex.Message); }

            // ── CPU — MODEL, FREQUENCY SANITY & THROTTLE CLASSIFICATION ─────────
            Section("CPU IDENTIFICATION & THROTTLE SANITY (brief real load)");
            try
            {
                string cpuName = sum?.Cpu ?? "";
                if (string.IsNullOrWhiteSpace(cpuName)) Warn("CPU model", "empty — SystemSummary didn't resolve a name");
                else Ok("CPU model", cpuName);

                if (!string.IsNullOrWhiteSpace(cpuName) && SMDWin.Services.HardwareDatabaseService.LookupCpu(cpuName) == null)
                {
                    TrackHwDb(known: false);
                    Warn("CPU not in hardware database", $"model=\"{cpuName}\" — add to Data/HardwareDB/cpu.json in a future session");
                }
                else if (!string.IsNullOrWhiteSpace(cpuName))
                {
                    TrackHwDb(known: true);
                }

                using var throttle = new ThrottleDetector();
                var stressor = new CpuStressor();
                stressor.Start(Environment.ProcessorCount);
                try
                {
                    var tempReader = SMDWin.Services.TempReader.Borrow(out bool ownsReader);
                    using var ownedReader = ownsReader ? tempReader : null;   // closed only if it was opened here
                    float maxFreqSeen = 0, minRatioSeen = 1f;
                    bool sawImpossibleZero = false, boostOff = false;
                    _ = throttle.Read(); // priming read (counters need 2 reads to be valid)
                    for (int i = 0; i < 4 && !ct.IsCancellationRequested; i++)
                    {
                        await Task.Delay(500, ct);
                        var tempSnap = await Task.Run(() => tempReader.Read(), ct).WaitAsync(TimeSpan.FromSeconds(8), ct);
                        float cpuTempC = (float)(tempSnap?.CpuTemp ?? double.NaN);
                        var throttleSnap = throttle.Read(cpuTempC, 100f); // load=100 — the stress really runs
                        if (throttleSnap == null) continue;
                        maxFreqSeen = Math.Max(maxFreqSeen, throttleSnap.FreqMHz);
                        minRatioSeen = Math.Min(minRatioSeen, throttleSnap.FreqRatio);
                        // Under real load (100% on all cores) the frequency CANNOT be 0 — if a sensor reports 0 when PROCHOT kicks in,
                        // the self-test catches it here.
                        if (throttleSnap.FreqMHz <= 0f) sawImpossibleZero = true;
                        boostOff |= throttleSnap.BoostDisabled;
                    }
                    if (sawImpossibleZero)
                        Fail("CPU frequency reading", "reported 0 MHz while under 100% real load — impossible reading, sensor/counter glitch");
                    else if (maxFreqSeen <= 0f)
                        Warn("CPU frequency reading", "could not get a single valid reading under load (Processor Information counters unavailable?)");
                    else
                        Ok("CPU frequency under load", $"max observed {maxFreqSeen:F0} MHz, min ratio {minRatioSeen * 100:F0}% of calibrated max");
                    if (boostOff)
                        Log("  · Turbo is turned off in the Windows power plan (Processor Boost Mode = Disabled) — the frequency stays at the base clock and the benchmark score below is lower than this CPU can do");
                }
                finally { stressor.Stop(); }
            }
            catch (Exception ex) { Fail("CPU throttle sanity check threw an exception", ex.Message); }

            // ── BATTERY — HEALTH & CAPACITY SANITY ──────────────────────────────
            Section("BATTERY HEALTH VERIFICATION");
            try
            {
                var batSvc = new BatteryService();
                var bat = await batSvc.GetBatteryInfoAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (!bat.Present)
                    Log("  · No battery present (desktop or battery removed) — skipping.");
                else
                {
                    Ok("Battery", $"{bat.Manufacturer} {bat.Name}  ·  {bat.ChargePercent}%  ·  {bat.Status}");
                    if (bat.WearPct.HasValue)
                        Ok("Health (wear-based)", $"{100 - bat.WearPct.Value}%  (design={bat.DesignCapacityMWh / 1000.0:F1} Wh, full={bat.FullCapacityMWh / 1000.0:F1} Wh)");
                    else
                        Warn("Health", "no WearPct available — DesignCapacity/FullCapacity missing from WMI on this system");

                    // Capacity Now = 0 Wh while on AC with a normal health percentage is a contradictory combination (either fully
                    // discharged on AC — legitimate only if ChargePercent is 0 too — or a bad CAPACITY_INFORMATION read).
                    double capNowWh = (bat.FullCapacityMWh / 1000.0) * bat.ChargePercent / 100.0;
                    if (bat.FullCapacityMWh > 0 && bat.ChargePercent > 5 && capNowWh < 0.5)
                        Fail("Battery capacity now", $"computed {capNowWh:F1} Wh but ChargePercent={bat.ChargePercent}% — contradictory reading");

                    if (bat.DesignCapacityMWh > 0 && bat.FullCapacityMWh > bat.DesignCapacityMWh * 1.10)
                        Warn("Battery capacity", $"FullCapacity ({bat.FullCapacityMWh} mWh) is >10% ABOVE DesignCapacity ({bat.DesignCapacityMWh} mWh) — implausible, check WMI source");
                }
            }
            catch (Exception ex) { Fail("BatteryService.GetBatteryInfoAsync threw an exception", ex.Message); }

            // ── WI-FI / NETWORK ADAPTER — PRESENCE EVEN WHEN DISCONNECTED ───────
            Section("WI-FI / NETWORK ADAPTER PRESENCE");
            try
            {
                var allNics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var wifiAdapters = allNics.Where(n =>
                {
                    var name = n.Name.ToLowerInvariant();
                    var desc = n.Description.ToLowerInvariant();
                    if (name.Contains("bluetooth") || desc.Contains("bluetooth")) return false;
                    // virtual adapters created by Windows (Wi-Fi Direct, hosted network) are not Wi-Fi adapters
                    if (desc.Contains("wi-fi direct") || desc.Contains("virtual")) return false;
                    return n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                        || name.Contains("wi-fi") || name.Contains("wifi") || name.Contains("wireless")
                        || desc.Contains("wi-fi") || desc.Contains("wifi") || desc.Contains("wireless");
                }).ToList();

                if (wifiAdapters.Count == 0)
                    Log("  · No Wi-Fi-type adapter found on this system (desktop without Wi-Fi card, or genuinely absent).");
                else
                {
                    foreach (var w in wifiAdapters)
                    {
                        Ok($"Wi-Fi adapter found", $"{w.Description}  ·  status={w.OperationalStatus}");
                    }
                }
            }
            catch (Exception ex) { Fail("Wi-Fi adapter presence check threw an exception", ex.Message); }

            Section("NETWORK ADAPTER SPEED");
            try
            {
                using var netSearcher = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name, Speed, NetConnectionStatus, AdapterType FROM Win32_NetworkAdapter WHERE NetConnectionStatus=2", System.TimeSpan.FromSeconds(10));
                int wiredCount = 0;
                using (var __wmiTmp2 = netSearcher.Get())
                {
                    foreach (System.Management.ManagementObject na in __wmiTmp2)
                {
                    string name = na["Name"]?.ToString() ?? "?";
                    string adapterType = na["AdapterType"]?.ToString() ?? "";
                    bool looksWireless = name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                                       || name.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                                       || name.Contains("802.11", StringComparison.OrdinalIgnoreCase);
                    if (looksWireless) continue; // Wi-Fi speed varies normally, not a sign of a problem

                    object? speedObj = na["Speed"];
                    if (speedObj == null) continue;
                    ulong speedBps = Convert.ToUInt64(speedObj);
                    if (speedBps == 0) continue;
                    wiredCount++;
                    double speedMbps = speedBps / 1_000_000.0;
                    if (speedMbps < 100)
                        Warn($"{name} negotiated at only {speedMbps:F0} Mbps",
                            "well below typical Ethernet (100/1000 Mbps) — often a bad cable, damaged port, or an old switch forcing a low speed (a common indirect sign of duplex mismatch)");
                    else
                        Ok(name, $"{speedMbps:F0} Mbps");
                }
                }
                if (wiredCount == 0) Log("  · No connected wired adapters found to check");
            }
            catch (Exception ex) { Log($"  · Network adapter speed check unavailable: {ex.Message}"); }

            Section("DNS RESPONSE TIME");
            try
            {
                var dnsSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await System.Net.Dns.GetHostAddressesAsync("cloudflare.com").WaitAsync(TimeSpan.FromSeconds(5), ct);
                    dnsSw.Stop();
                    long ms = dnsSw.ElapsedMilliseconds;
                    if (ms > 500) Warn("DNS resolution slow", $"{ms}ms for cloudflare.com — over 200-300ms is noticeably slow for DNS; could explain \"internet feels slow\" complaints even when download speed tests fine");
                    else Ok("DNS resolution time", $"{ms}ms");
                }
                catch (Exception ex) { Warn("DNS resolution failed", ex.Message); }
            }
            catch (Exception ex) { Log($"  · DNS check unavailable: {ex.Message}"); }

            // ── 4. PROCESSOR CALCULATION CHECK ─────────────────────────────────
            // 16.11.2026: the old processor benchmark (SHA-256 + Mandelbrot in pixels + FFT, its own "516 · Good" score) is no longer
            // run here: the Benchmark page gives the marks, and two scores for the same processor in one report confused. What
            // stays is the part that finds hardware faults: FFT on all threads with every result checked.
            Section("PROCESSOR CALCULATION CHECK");
            try
            {
                int mismatches = 0;
                double ops = await Task.Run(() => CpuStressor.BenchmarkFFT(Math.Max(3.0, options.BenchmarkPerTestSeconds), out mismatches), ct);
                if (mismatches == 0)
                    Ok("Calculation check", $"no wrong results in {ops:N0} FFT per second on {Environment.ProcessorCount} threads — the processor and memory calculate correctly (the marks are on the Benchmark page)");
                else
                    Fail("Calculation check FAILED",
                        $"{mismatches} wrong FFT result(s) — direct evidence of hardware instability (unstable memory, an unsafe overclock or a failing processor), whatever the temperatures say");
            }
            catch (Exception ex) { Fail("Calculation check threw an exception", ex.Message); }

            // ── RECENT CRASHES (informational, non-blocking) ─────────────────
            Section("BONUS  RECENT CRASHES (BSOD)");
            try
            {
                var crashSvc = new CrashService();
                var crashes = await crashSvc.GetCrashesAsync().WaitAsync(TimeSpan.FromSeconds(15), ct);
                var recent = crashes
                    .Where(c => c.StopCode != "—")
                    .Where(c => c.CrashTime >= DateTime.Now.AddDays(-options.CrashLookbackDays)).ToList();
                if (recent.Count == 0) Ok($"No BSOD in the last {options.CrashLookbackDays} days");
                else
                {
                    Warn($"BSODs in the last {options.CrashLookbackDays} days: {recent.Count}");
                    foreach (var c in recent.Take(10))
                        Log($"      - {c.CrashTime:dd-MM-yyyy HH:mm}  {c.StopCode}  module: {c.FaultingModule}");
                }
            }
            catch (Exception ex) { Warn("GetCrashesAsync threw an exception (non-blocking)", ex.Message); }

            Section("APP HEALTH (SMDWin)");
            try
            {
                using var self = Process.GetCurrentProcess();
                self.Refresh();
                double rssMB = self.WorkingSet64 / 1024.0 / 1024.0;
                int gdi  = GetGuiResources(self.Handle, GDI_OBJECTS);
                int user = GetGuiResources(self.Handle, USER_OBJECTS);
                var uptime = DateTime.Now - self.StartTime;

                Ok("Memory (Working Set)", $"{rssMB:F0} MB");

                try
                {
                    string historyPath = Path.Combine(AppLogger.LogDirectory, "selftest-memory-history.csv");
                    string machine = Environment.MachineName;
                    var history = new List<(DateTime time, double mb)>();
                    if (File.Exists(historyPath))
                    {
                        foreach (var line in File.ReadAllLines(historyPath))
                        {
                            var parts = line.Split(',');
                            if (parts.Length == 3 && parts[0] == machine
                                && DateTime.TryParse(parts[1], out var t)
                                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var mb))
                                history.Add((t, mb));
                        }
                    }
                    File.AppendAllText(historyPath, $"{machine},{DateTime.Now:O},{rssMB:F1}\n");

                    if (history.Count >= 3)
                    {
                        var last3 = history.OrderByDescending(h => h.time).Take(3).OrderBy(h => h.time).ToList();
                        bool consistentGrowth = ConsistentGrowthDetector.IsConsistentGrowth(
                            last3[0].mb, last3[1].mb, rssMB, minAbsoluteGrowth: 150);
                        if (consistentGrowth)
                            Warn("SMDWin memory trend — likely leak",
                                $"{last3[0].mb:F0} MB → {last3[1].mb:F0} MB → {rssMB:F0} MB across the last 3 fresh launches on this machine — consistent growth, not just a one-time high reading");
                        else if (rssMB > 900)
                            Ok("Memory trend", $"{rssMB:F0} MB is high, but no consistent growth vs. the last {history.Count} runs on this machine — likely just a heavier session, not a leak");
                    }
                    else if (rssMB > 900)
                        Warn("SMDWin memory high", $"{rssMB:F0} MB — not enough history on this machine yet to confirm a trend (need 3+ self-test runs); will track from here");
                }
                catch (Exception ex) { AppLogger.Debug($"Memory history tracking: {ex.Message}"); if (rssMB > 900) Warn("SMDWin memory high", $"{rssMB:F0} MB"); }

                Ok("Process handles (total)", $"{self.HandleCount}");
                if (self.HandleCount > 3000) Warn("Process handles high", $"{self.HandleCount} — possible leak (files/registry/etc. not disposed)");

                try
                {
                    string handlesHistoryPath = Path.Combine(AppLogger.LogDirectory, "selftest-handles-history.csv");
                    string machine = Environment.MachineName;
                    var handlesHistory = new List<(DateTime time, double count)>();
                    if (File.Exists(handlesHistoryPath))
                    {
                        foreach (var line in File.ReadAllLines(handlesHistoryPath))
                        {
                            var parts = line.Split(',');
                            if (parts.Length == 3 && parts[0] == machine
                                && DateTime.TryParse(parts[1], out var t)
                                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var count))
                                handlesHistory.Add((t, count));
                        }
                    }
                    File.AppendAllText(handlesHistoryPath, $"{machine},{DateTime.Now:O},{self.HandleCount}\n");

                    if (handlesHistory.Count >= 3)
                    {
                        var last3Handles = handlesHistory.OrderByDescending(h => h.time).Take(3).OrderBy(h => h.time).ToList();
                        bool handlesGrowing = ConsistentGrowthDetector.IsConsistentGrowth(
                            last3Handles[0].count, last3Handles[1].count, self.HandleCount, minAbsoluteGrowth: 500);
                        if (handlesGrowing)
                            Warn("SMDWin handle count trend — possible leak",
                                $"{last3Handles[0].count:F0} → {last3Handles[1].count:F0} → {self.HandleCount} across the last 3 fresh launches on this machine — consistent growth, well under the absolute limit but worth watching");
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"Handle count history tracking: {ex.Message}"); }

                if (gdi >= 0)
                {
                    Ok("GDI objects", $"{gdi} / 10000 (default Windows per-process limit)");
                    if (gdi > 6000) Warn("GDI objects high", $"{gdi}/10000 — close to the limit; real risk of Win32Exception 1816 when rendering canvases");
                }
                else Warn("GetGuiResources(GDI)", "call failed — could not read the counter");

                if (user >= 0)
                {
                    Ok("USER objects", $"{user} / 10000 (default Windows per-process limit)");
                    if (user > 6000) Warn("USER objects high", $"{user}/10000 — close to the limit");
                }
                else Warn("GetGuiResources(USER)", "call failed — could not read the counter");

                Ok("Active threads", $"{self.Threads.Count}");
                Ok("Uptime since launch", $"{(int)uptime.TotalMinutes} min");
            }
            catch (Exception ex) { Fail("Own process resource check failed", ex.Message); }

            // ── 6. FILES & SETTINGS ─────────────────────────────────────────
            Section("FILES & SETTINGS");
            try
            {
                string settingsPath = SettingsService.SettingsFilePath;
                if (!File.Exists(settingsPath))
                    Warn("settings.json", "doesn't exist — defaults will be used on next launch");
                else
                {
                    try
                    {
                        var raw = await File.ReadAllTextAsync(settingsPath, ct);
                        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(raw);
                        Ok("settings.json", $"valid, {raw.Length / 1024.0:F0} KB, version {SettingsService.Current.SettingsVersion}");
                    }
                    catch (Exception exJson) { Fail("settings.json is corrupt/invalid", exJson.Message); }
                }
            }
            catch (Exception ex) { Fail("settings.json check failed", ex.Message); }

            try
            {
                string logDir = AppLogger.LogDirectory;
                if (!Directory.Exists(logDir)) Fail("Logs folder", $"doesn't exist: {logDir}");
                else
                {
                    // Test write — confirms the folder is really writable (not just that it exists).
                    string probe = Path.Combine(logDir, $".selftest_probe_{Guid.NewGuid():N}.tmp");
                    try
                    {
                        await File.WriteAllTextAsync(probe, "probe", ct);
                        File.Delete(probe);
                        Ok("Logs folder writable", logDir);
                    }
                    catch (Exception exW) { Fail("Logs folder is NOT writable", exW.Message); }

                    // Rotation: configured for 7 days (Serilog) — files older than 10 days (safety margin) suggest rotation is not
                    // cleaning up correctly.
                    var oldLogs = Directory.GetFiles(logDir, "smdwin-*.log")
                        .Select(f => new FileInfo(f))
                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalDays > 10)
                        .ToList();
                    if (oldLogs.Count == 0) Ok("Log rotation (7 days)", "no old files left over");
                    else Warn($"Logs older than 10 days found: {oldLogs.Count}", "Serilog rotation should have already cleaned these up");
                }
            }
            catch (Exception ex) { Fail("Logs folder check failed", ex.Message); }

            Section("CRITICAL EVENT PROVIDERS (last 30 days)");
            try
            {
                string cutoff30 = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ");
                int CountProviderEvents(string logName, string[] providers, int[]? eventIds = null)
                {
                    try
                    {
                        string providerXPath = string.Join(" or ", providers.Select(p => $"Provider[@Name='{p}']"));
                        string idXPath = eventIds != null
                            ? " and (" + string.Join(" or ", eventIds.Select(id => $"EventID={id}")) + ")"
                            : "";
                        string xpath = $"*[System[({providerXPath}){idXPath} and TimeCreated[@SystemTime>='{cutoff30}']]]";
                        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                            new System.Diagnostics.Eventing.Reader.EventLogQuery(logName,
                                System.Diagnostics.Eventing.Reader.PathType.LogName, xpath));
                        int count = 0;
                        while (reader.ReadEvent() is { } cntRec && count < 1000) { using (cntRec) count++; }
                        return count;
                    }
                    catch { return -1; } // provider/log not available on this system — not an error
                }

                void CheckGroup(string label, string[] providers, int[]? eventIds = null)
                {
                    int n = CountProviderEvents("System", providers, eventIds);
                    if (n < 0) return; // not available, nothing reported (not a problem)
                    if (n == 0) Ok(label, "none in the last 30 days");
                    else Warn($"{label}: {n} events in the last 30 days",
                        "see \"Generate AI Diagnostic Package\" → Critical Event Providers for full details");
                }

                CheckGroup("WHEA (hardware errors)", new[] { "Microsoft-Windows-WHEA-Logger" });
                CheckGroup("Kernel-Power (unexpected shutdown, ID 41)", new[] { "Microsoft-Windows-Kernel-Power" }, new[] { 41 });
                CheckGroup("Display driver reset/TDR", new[] { "nvlddmkm", "amdkmdag", "igfx", "Display" });
                CheckGroup("Disk / Storage controller errors", new[] { "disk", "Ntfs", "storahci", "stornvme", "iaStorAC", "nvme" });
                CheckGroup("USB errors", new[] { "usbhub3", "USB", "Microsoft-Windows-USB-USBHUB3" });
            }
            catch (Exception ex) { Fail("Critical event provider check failed", ex.Message); }

            // Readings from the System Information and Windows info views: what Windows reports on this computer.
            // They are states of the system, not of the app, so they are not counted as warnings.
            Section("DISPLAY & PROTECTION READINGS");
            try
            {
                var monitors = await Task.Run(SystemProbes.ReadMonitors, ct);
                if (monitors.Count == 0)
                    Log("  · Monitors  —  Windows reports neither size nor connection; the Display card shows the resolution only");
                foreach (var (id, m) in monitors)
                {
                    double inches = DisplayFacts.DiagonalInches(m.WidthCm, m.HeightCm);
                    string size = inches > 0 ? $"{m.WidthCm:0} × {m.HeightCm:0} cm → {DisplayFacts.DiagonalText(inches)}" : "size not reported";
                    string link = m.Technology < 0 ? "connection not reported"
                                : DisplayFacts.IsBuiltIn(m.Technology) ? "built-in screen"
                                : DisplayFacts.Connection(m.Technology) is { Length: > 0 } name ? name
                                : $"connection code {m.Technology} (not shown)";
                    Ok($"Monitor {id}", $"{size} · {link}");
                }

                var (avRead, av, avCount) = await Task.Run(SystemProbes.ReadAntivirus, ct);
                if (!avRead) Log("  · Antivirus  —  the Windows Security list cannot be read here; Windows info leaves it out");
                else if (av == null) Ok("Antivirus", "none registered in Windows Security");
                else Ok("Antivirus", $"{av.Name} — {(av.Enabled ? "enabled" : "disabled")}, {(av.UpToDate ? "up to date" : "signatures out of date")} ({avCount} registered)");

                bool? encrypted = await Task.Run(SystemProbes.ReadSystemDriveEncryption, ct);
                if (encrypted.HasValue) Ok("System drive encryption", encrypted.Value ? "encrypted (BitLocker)" : "not encrypted");
                else Log("  · System drive encryption  —  not readable on this Windows edition; Windows info leaves it out");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Fail("Display & protection readings failed", ex.Message); }

            Section("ICON THEME AUDIT");
            try
            {
                var darkDict = new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/views/IconsDark.xaml", UriKind.Absolute) };
                var lightDict = new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/views/IconsLight.xaml", UriKind.Absolute) };

                var darkKeys = darkDict.Keys.Cast<object>().Select(k => k.ToString() ?? "").ToHashSet();
                var lightKeys = lightDict.Keys.Cast<object>().Select(k => k.ToString() ?? "").ToHashSet();

                var onlyDark = darkKeys.Except(lightKeys).ToList();
                var onlyLight = lightKeys.Except(darkKeys).ToList();

                if (onlyDark.Count == 0 && onlyLight.Count == 0)
                    Ok("IconsDark.xaml / IconsLight.xaml", $"{darkKeys.Count} keys, identical sets — no icon disappears when switching theme");
                else
                {
                    if (onlyDark.Count > 0)
                    {
                        Warn($"Keys only in IconsDark.xaml (missing from Light): {onlyDark.Count}", "these icons disappear in Light theme");
                        foreach (var k in onlyDark.Take(15)) Log($"      - {k}");
                        if (onlyDark.Count > 15) Log($"      … and {onlyDark.Count - 15} more");
                    }
                    if (onlyLight.Count > 0)
                    {
                        Warn($"Keys only in IconsLight.xaml (missing from Dark): {onlyLight.Count}", "these icons disappear in Dark theme");
                        foreach (var k in onlyLight.Take(15)) Log($"      - {k}");
                        if (onlyLight.Count > 15) Log($"      … and {onlyLight.Count - 15} more");
                    }
                }
            }
            catch (Exception ex) { Fail("Icon theme audit failed", ex.Message); }

            // ── 7. LOCALIZATION (EN/RO/DE/FR key completeness) ──────────────
            Section("LOCALIZATION");
            try
            {
                string langDir = Path.Combine(AppPaths.BundleDirectory, "Languages");
                var codes = new[] { "en", "ro", "de", "fr" };
                var flatByCode = new Dictionary<string, HashSet<string>>();
                var textsByCode = new Dictionary<string, Dictionary<string, string>>();

                foreach (var code in codes)
                {
                    string path = Path.Combine(langDir, $"{code}.json");
                    if (!File.Exists(path)) { Fail($"Languages/{code}.json", "file missing"); continue; }
                    try
                    {
                        var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                            await File.ReadAllTextAsync(path, ct));
                        var flat = new HashSet<string>();
                        var texts = new Dictionary<string, string>();
                        if (raw != null)
                        {
                            foreach (var section in raw)
                            {
                                if (section.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                                foreach (var kv in section.Value.EnumerateObject())
                                {
                                    flat.Add($"{section.Key}.{kv.Name}");
                                    if (kv.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                        texts[$"{section.Key}.{kv.Name}"] = kv.Value.GetString() ?? "";
                                }
                            }
                        }
                        flatByCode[code] = flat;
                        textsByCode[code] = texts;
                    }
                    catch (Exception exJ) { Fail($"Languages/{code}.json", $"invalid JSON: {exJ.Message}"); }
                }

                if (flatByCode.TryGetValue("en", out var enKeys))
                {
                    Ok("Languages/en.json (reference)", $"{enKeys.Count} keys");
                    foreach (var code in codes.Where(c => c != "en"))
                    {
                        if (!flatByCode.TryGetValue(code, out var otherKeys)) continue;
                        var missing = enKeys.Except(otherKeys).ToList();
                        var extra   = otherKeys.Except(enKeys).ToList();
                        var extraReal = extra.Where(k => !k.StartsWith("UI.")).ToList();
                        if (missing.Count == 0 && extraReal.Count == 0) Ok($"Languages/{code}.json", "keys identical to en.json (excl. UI.* — dynamic DE/FR-only lookup section)");
                        else
                        {
                            if (missing.Count > 0)
                            {
                                Warn($"Languages/{code}.json — keys missing vs. en.json: {missing.Count}");
                                foreach (var k in missing.Take(15)) Log($"      - {k}");
                                if (missing.Count > 15) Log($"      … and {missing.Count - 15} more");
                            }
                            if (extraReal.Count > 0) Warn($"Languages/{code}.json — extra keys vs. en.json: {extraReal.Count}", "possibly unused leftovers, non-blocking");
                        }

                        // the values ({0}, {1}) are filled in English order: a translation with different placeholders shows them wrong
                        if (textsByCode.TryGetValue("en", out var enTexts) && textsByCode.TryGetValue(code, out var otherTexts))
                        {
                            var mismatched = LanguageAudit.PlaceholderMismatches(enTexts, otherTexts);
                            if (mismatched.Count == 0) Ok($"Languages/{code}.json placeholders", "same {0}, {1}… as en.json in every text");
                            else
                            {
                                Warn($"Languages/{code}.json — texts whose placeholders differ from en.json: {mismatched.Count}", "values would be shown in the wrong place");
                                foreach (var k in mismatched.Take(15)) Log($"      - {k}");
                                if (mismatched.Count > 15) Log($"      … and {mismatched.Count - 15} more");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Fail("Localization check failed", ex.Message); }

            return new SelfTestResult
            {
                Report = sb.ToString(),
                WarnCount = warnCount,
                FailCount = failCount,
                HwDbKnownCount = hwDbKnown,
                HwDbTotalCount = hwDbTotal,
            };
        }

        public static async Task<string> SaveReportAsync(string fullReport, Action<string>? progress = null)
        {
            void Log(string line) { progress?.Invoke(line); AppLogger.Info($"SelfTest.SaveReportAsync: {line.Trim()}"); }
            string savedPath = "";
            string reportFolder = "";
            string fileName = $"SMDWin_SelfTest_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            try
            {
                string folder = AppPaths.Data("SelfTest-Results");
                Directory.CreateDirectory(folder);
                savedPath = Path.Combine(folder, fileName);
                await File.WriteAllTextAsync(savedPath, fullReport, Encoding.UTF8);
                reportFolder = folder;
                Log($"  Report saved: {savedPath}");
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Could not save the report to the Data folder: {ex.Message}");
                try
                {
                    reportFolder = AppLogger.LogDirectory;
                    savedPath = Path.Combine(reportFolder, fileName);
                    await File.WriteAllTextAsync(savedPath, fullReport, Encoding.UTF8);
                    Log($"  Report saved (fallback): {savedPath}");
                }
                catch (Exception ex2) { Log($"  ✘ Fallback also failed: {ex2.Message}"); savedPath = ""; }
            }

            // ── Copy the program log (current day) next to the report ──────────
            if (!string.IsNullOrEmpty(reportFolder))
            {
                try
                {
                    string logSrc = Path.Combine(AppLogger.LogDirectory, $"smdwin-{DateTime.Now:yyyyMMdd}.log");
                    if (File.Exists(logSrc))
                    {
                        string logDest = Path.Combine(reportFolder, Path.GetFileNameWithoutExtension(fileName) + "_log.txt");
                        // Copy instead of move — Serilog is still writing to the original file. File.Copy can fail if the sink holds an
                        // exclusive lock at that exact moment — the main report is not blocked for that.
                        await Task.Run(() => File.Copy(logSrc, logDest, overwrite: true));
                        Log($"  Log copied: {logDest}");
                    }
                    else
                    {
                        Log($"  ⚠ Could not find today's log ({Path.GetFileName(logSrc)}) — couldn't copy it.");
                    }
                }
                catch (Exception ex) { Log($"  ⚠ Could not copy the app log: {ex.Message}"); }
            }
            return savedPath;
        }
    }
}
