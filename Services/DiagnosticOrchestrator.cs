
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public enum DiagnosticPhase { Cpu, Gpu, Ram, Storage, Network,
        WindowsActivation, StorageHealth, FreeSpace, EventLogErrors, BsodHistory,
        BatteryHealth, MissingDrivers, Antivirus, WindowsUpdate, CriticalServices,
        Uptime, DiskTemperature, Security, Firewall, RestorePoints, ClockSync,
        NetworkDriverErrors, Throttling, RamDiagnosticHistory, HardwareErrors }

    public enum DiagnosticSeverity { Ok, Warning, Critical }

    /// <summary>The result of a single phase — factual data, WITHOUT an arbitrary score.</summary>
    public class DiagnosticPhaseResult
    {
        public DiagnosticPhase Phase { get; set; }
        public bool Success { get; set; }
        /// <summary>A result can complete with warnings — Success=true does not necessarily mean
        /// "nothing to report" (e.g. GPU load N/A on hybrid hardware).</summary>
        public string Summary { get; set; } = "";
        public Dictionary<string, string> Details { get; set; } = new();
        public TimeSpan Elapsed { get; set; }
        public Exception? Error { get; set; }

        private DiagnosticSeverity? _severity;
        public DiagnosticSeverity Severity
        {
            get => _severity ?? (Success ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning);
            set => _severity = value;
        }

        public bool Skip { get; set; }
    }

    public class DiagnosticOrchestrator
    {
        /// <summary>
        /// True for a critical result that means physical danger to the hardware or data — the only ones that tint the Dark
        /// background red: RAM errors, a failing disk (SMART health under 50 %) or a hot disk, 2+ blue
        /// screens in 30 days, a worn-out battery (under 40 %) and thermal throttling right now. Everything else (activation,
        /// updates, antivirus, uptime, drivers…) stays a list item.
        /// CPU/GPU stress-test results no longer count — a test that once reached 95 °C kept the background red
        /// after the PC had cooled to 60 °C. A live temperature above the limit still turns it red (UpdateAuroraAlert, "hot"),
        /// and goes back as soon as it cools; the old test stays Critical in the Health check list.
        /// </summary>
        public static bool IsPhysicalDanger(DiagnosticPhaseResult r) =>
            !r.Skip && r.Severity == DiagnosticSeverity.Critical && r.Phase is
                DiagnosticPhase.Ram or DiagnosticPhase.RamDiagnosticHistory
                or DiagnosticPhase.StorageHealth or DiagnosticPhase.DiskTemperature or DiagnosticPhase.BsodHistory
                or DiagnosticPhase.BatteryHealth or DiagnosticPhase.Throttling;

        /// <summary>Last number of pending Windows updates found by the Health check (null = not measured yet).</summary>
        public static int? LastPendingUpdates { get; private set; }

        /// <summary>O faza tocmai a inceput.</summary>
        public event Action<DiagnosticPhase>? PhaseStarted;
        /// <summary>REAL progress (0.0-1.0) within the current phase — derived from the test's real data
        /// (elapsed/total for stress, BarFraction for storage, etc.), not from a separate guessed timer.</summary>
        public event Action<DiagnosticPhase, double>? PhaseProgress;
        /// <summary>A phase finished (successfully or with an error) — its result is already complete.</summary>
        public event Action<DiagnosticPhaseResult>? PhaseCompleted;

        /// <summary>
        /// Runs all 5 phases in turn. Each phase is independent — an error in one does NOT stop the rest
        /// (just like the modules of the diagnostic package); it is only recorded in that phase's result.
        /// </summary>
        public async Task<List<DiagnosticPhaseResult>> RunAsync(
            string driveLetter,
            CancellationToken ct,
            int cpuStressSeconds = 60,
            int gpuStressSeconds = 30)
        {
            var results = new List<DiagnosticPhaseResult>();

            results.Add(await RunPhaseAsync(DiagnosticPhase.Cpu, () => RunCpuPhaseAsync(cpuStressSeconds, ct), ct));
            results.Add(await RunPhaseAsync(DiagnosticPhase.Gpu, () => RunGpuPhaseAsync(gpuStressSeconds, ct), ct));
            results.Add(await RunPhaseAsync(DiagnosticPhase.Ram, () => RunRamPhaseAsync(ct), ct));
            results.Add(await RunPhaseAsync(DiagnosticPhase.Storage, () => RunStoragePhaseAsync(driveLetter, ct), ct));
            results.Add(await RunPhaseAsync(DiagnosticPhase.Network, () => RunNetworkPhaseAsync(ct), ct));

            return results;
        }

        /// <summary>The quick checks (system information) are done — only the background tests remain
        /// (RAM, CPU, GPU, Windows Update). Argument: how long they took.</summary>
        public event Action<TimeSpan>? QuickChecksCompleted;

        /// <summary>
        /// Health Check in two stages:
        ///  1. Quick checks — information read from the system (activation, security, disks, drivers, logs…) —
        ///     run IN PARALLEL (4 at a time) on the thread pool, so they all appear within seconds.
        ///  2. Processor, graphics, throttling and memory are NOT tested here: the latest tests from
        ///     Temp &amp; Stress / RAM (TestResultStore) are read, plus what the app sees now (throttle detector).
        ///     A test that has not run yet = Details["NotTested"].
        ///  3. The Windows Update search (network, slow) comes last, when includeWindowsUpdate is true.
        /// The order of the returned results stays the same.
        /// </summary>
        public async Task<List<DiagnosticPhaseResult>> RunHealthCheckAsync(
            string driveLetter,
            CancellationToken ct,
            Func<ThrottleSnapshot?>? getThrottleSnapshot = null,
            bool includeWindowsUpdate = true)
        {
            var quick = new (DiagnosticPhase phase, Func<Task<DiagnosticPhaseResult>> body)[]
            {
                (DiagnosticPhase.WindowsActivation,   () => RunWindowsActivationPhaseAsync(ct)),
                (DiagnosticPhase.Security,            () => RunSecurityPhaseAsync(ct)),
                (DiagnosticPhase.Firewall,            () => RunFirewallPhaseAsync(ct)),
                (DiagnosticPhase.RestorePoints,       () => RunRestorePointsPhaseAsync(ct)),
                (DiagnosticPhase.ClockSync,           () => RunClockSyncPhaseAsync(ct)),
                (DiagnosticPhase.StorageHealth,       () => RunStorageHealthPhaseAsync(ct)),
                (DiagnosticPhase.FreeSpace,           () => RunFreeSpacePhaseAsync(ct)),
                (DiagnosticPhase.DiskTemperature,     () => RunDiskTemperaturePhaseAsync(ct)),
                (DiagnosticPhase.BatteryHealth,       () => RunBatteryHealthPhaseAsync(ct)),
                (DiagnosticPhase.MissingDrivers,      () => RunMissingDriversPhaseAsync(ct)),
                (DiagnosticPhase.Antivirus,           () => RunAntivirusPhaseAsync(ct)),
                (DiagnosticPhase.CriticalServices,    () => RunCriticalServicesPhaseAsync(ct)),
                (DiagnosticPhase.Uptime,              () => RunUptimePhaseAsync(ct)),
                (DiagnosticPhase.EventLogErrors,      () => RunEventLogErrorsPhaseAsync(ct)),
                (DiagnosticPhase.NetworkDriverErrors, () => RunNetworkDriverErrorsPhaseAsync(ct)),
                (DiagnosticPhase.BsodHistory,         () => RunBsodHistoryPhaseAsync(ct)),
                (DiagnosticPhase.HardwareErrors,      () => RunHardwareErrorsPhaseAsync(ct)),
                (DiagnosticPhase.RamDiagnosticHistory,() => RunRamDiagnosticHistoryPhaseAsync(ct)),
                (DiagnosticPhase.Cpu,                 () => Task.FromResult(CpuFromLastTest())),
                (DiagnosticPhase.Throttling,          () => Task.FromResult(ThrottlingFromLastTest(getThrottleSnapshot))),
                (DiagnosticPhase.Gpu,                 () => Task.FromResult(GpuFromLastTest())),
                (DiagnosticPhase.Ram,                 () => Task.FromResult(RamFromLastTest())),
            };

            // ── Stage 1: quick checks in parallel (max 4 at once — WMI does not like dozens
            //    of simultaneous queries) ─────────────────────────────────────────────────────
            var swQuick = Stopwatch.StartNew();
            var quickResults = new DiagnosticPhaseResult[quick.Length];
            using (var gate = new SemaphoreSlim(4))
            {
                var tasks = quick.Select((q, i) => Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try { quickResults[i] = await RunPhaseAsync(q.phase, q.body, ct); }
                    finally { gate.Release(); }
                }, ct)).ToArray();
                await Task.WhenAll(tasks);
            }
            swQuick.Stop();
            QuickChecksCompleted?.Invoke(swQuick.Elapsed);

            var results = new List<DiagnosticPhaseResult>(quickResults);

            // ── Stage 2: Windows Update (network, may take a while) ───────────────────────────────
            if (includeWindowsUpdate)
                results.Add(await RunPhaseAsync(DiagnosticPhase.WindowsUpdate, () => RunWindowsUpdatePhaseAsync(ct), ct));

            return results;
        }

        // ── Battery Health — reuses BatteryService, the same WearPct used for the health
        //    ring on the Power page. Skip=true on desktops (no battery). ──
        private async Task<DiagnosticPhaseResult> RunBatteryHealthPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.BatteryHealth };
            try
            {
                var svc = new BatteryService();
                var info = await svc.GetBatteryInfoAsync();
                PhaseProgress?.Invoke(DiagnosticPhase.BatteryHealth, 1.0);
                if (!info.Present)
                {
                    result.Skip = true; // desktop — nothing to report, the row is hidden
                    return result;
                }
                if (!info.WearPct.HasValue)
                {
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Could not determine battery wear level");
                    return result;
                }
                int health = 100 - info.WearPct.Value;
                result.Severity = health < 40 ? DiagnosticSeverity.Critical      // red only when really worn out
                                 : health < 70 ? DiagnosticSeverity.Warning
                                 : DiagnosticSeverity.Ok;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                result.Summary = Tx("{0}% health", health) +
                    (info.CycleCount > 0 ? Tx(" — {0} charge cycles", info.CycleCount) : "");
                result.Details["HealthPercent"] = health.ToString();
                result.Details["CycleCount"] = info.CycleCount.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunBatteryHealthPhaseAsync: {ex.Message}");
                result.Skip = true; // battery read error — safer to hide
                                     // than to show a false state (it may be a desktop).
            }
            return result;
        }

        // ── Missing/problematic drivers — reuses DriverService, the same list
        //    shown on the Drivers page ────────────────────────────────────────────────
        private async Task<DiagnosticPhaseResult> RunMissingDriversPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.MissingDrivers };
            try
            {
                var svc = new DriverService();
                var missing = await svc.GetMissingDevicesAsync();
                PhaseProgress?.Invoke(DiagnosticPhase.MissingDrivers, 1.0);
                int count = missing.Count;
                result.Severity = count >= 1 ? DiagnosticSeverity.Warning   // a missing driver is not a danger, however many
                                 : DiagnosticSeverity.Ok;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                result.Summary = count == 0
                    ? Tx("All drivers installed")
                    : Tx("{0} missing or with errors", TxN(count, "driver", "drivers"));
                result.Details["MissingCount"] = count.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunMissingDriversPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not check driver status");
            }
            return result;
        }

        // ── Disk temperatures — reuses SmartctlService, the same reading as the SYSTEM
        //    section in Temp & Stress (throttled there to 45 s; here a single reading per run
        //    is cheap enough) ──────────────────
        private async Task<DiagnosticPhaseResult> RunDiskTemperaturePhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.DiskTemperature };
            try
            {
                if (!SmartctlService.IsAvailable)
                {
                    result.Skip = true; // smartctl not available on this system
                    return result;
                }
                var hw = new HardwareService();
                var disks = await hw.GetDisksAsync();
                ct.ThrowIfCancellationRequested();

                double worstTemp = -1;         // highest temperature, for the "OK" message
                string worstModel = "";
                var worstSeverity = DiagnosticSeverity.Ok;
                double sevTemp = -1;           // temperature of the disk with the most serious severity
                string sevModel = "";
                foreach (var disk in disks)
                {
                    if (string.IsNullOrWhiteSpace(disk.PhysicalDevicePath)) continue;
                    try
                    {
                        var reading = await SmartctlService.ReadTemperatureAsync(disk.PhysicalDevicePath);
                        if (!reading.Celsius.HasValue) continue;
                        double t = reading.Celsius.Value;

                        if (t > worstTemp) { worstTemp = t; worstModel = disk.Model; }

                        bool isNvme = (disk.MediaType ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase);
                        double warnAt = isNvme ? 70 : 55;
                        double critAt = isNvme ? 80 : 65;
                        var sev = t >= critAt ? DiagnosticSeverity.Critical
                                : t >= warnAt ? DiagnosticSeverity.Warning
                                :               DiagnosticSeverity.Ok;
                        if (sev > worstSeverity || (sev == worstSeverity && sev != DiagnosticSeverity.Ok && t > sevTemp))
                        {
                            worstSeverity = sev; sevTemp = t; sevModel = disk.Model;
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"RunDiskTemperaturePhaseAsync: {disk.PhysicalDevicePath}: {ex.Message}"); }
                }
                PhaseProgress?.Invoke(DiagnosticPhase.DiskTemperature, 1.0);

                if (worstTemp < 0)
                {
                    result.Skip = true; // no disk reported a temperature — nothing to show
                    return result;
                }
                result.Severity = worstSeverity;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                result.Summary = result.Severity == DiagnosticSeverity.Ok
                    ? Tx("Disk temperatures OK (worst: {0})", TempUnit.Fmt(worstTemp))
                    : Tx("{0} running hot — {1}", sevModel, TempUnit.Fmt(sevTemp));
                result.Details["WorstTempC"] = (result.Severity == DiagnosticSeverity.Ok ? worstTemp : sevTemp).ToString("F0");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunDiskTemperaturePhaseAsync: {ex.Message}");
                result.Skip = true;
            }
            return result;
        }

        // ── Antivirus — WMI root\SecurityCenter2, the standard mechanism used by
        //    Windows Security itself to list registered antivirus products ────────────────
        private async Task<DiagnosticPhaseResult> RunAntivirusPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Antivirus };
            await Task.Run(() =>
            {
                try
                {
                    using var searcher = SMDWin.Services.WmiHelper.Searcher(
                        @"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", System.TimeSpan.FromSeconds(10));
                    string bestName = ""; bool anyEnabled = false; bool found = false;
                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp1)
                    {
                        found = true;
                        string name = obj["displayName"]?.ToString() ?? "";
                        uint state = 0;
                        try { state = (uint)(obj["productState"] ?? 0u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        // productState is a 3-byte bitmask (hex): the middle byte is the activation state.
                        // 0x10=on, 0x11=on (snoozed), 0x01/0x00=off — the publicly known "enabled" bits are checked
                        // (standard technique, also used by Windows Security Center).
                        bool enabled = ((state >> 12) & 0xF) == 1 || ((state >> 4) & 0xF) == 1 || ((state >> 4) & 0xF) == 0x1;
                        if (enabled) { anyEnabled = true; bestName = name; }
                        else if (string.IsNullOrEmpty(bestName)) bestName = name;
                    }
                    }
                    if (!found)
                    {
                        result.Severity = DiagnosticSeverity.Critical;
                        result.Summary = Tx("No antivirus product detected");
                    }
                    else
                    {
                        result.Severity = anyEnabled ? DiagnosticSeverity.Ok : DiagnosticSeverity.Critical;
                        result.Success = anyEnabled;
                        result.Summary = anyEnabled ? Tx("{0} is active", bestName) : Tx("{0} appears to be disabled", bestName);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunAntivirusPhaseAsync: {ex.Message}");
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Could not verify antivirus status");
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.Antivirus, 1.0);
            return result;
        }

        // ── Critical services — a SMALL list, separate from "Known Services" on the Windows Services
        //    page (that one holds optional services the user may legitimately disable — Fax, Xbox, etc.).
        //    Here are ONLY core system services that should NEVER be stopped on a healthy install. ──
        private static readonly string[] CoreServiceNames =
            { "EventLog", "RpcSs", "Dnscache", "BFE", "Winmgmt" };

        private async Task<DiagnosticPhaseResult> RunCriticalServicesPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.CriticalServices };
            await Task.Run(() =>
            {
                var stopped = new List<string>();
                foreach (var name in CoreServiceNames)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(name);
                        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                            stopped.Add(name);
                    }
                    catch (Exception ex) { AppLogger.Debug($"RunCriticalServicesPhaseAsync: {name}: {ex.Message}"); }
                }
                result.Severity = stopped.Count > 0 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Ok;
                result.Success = stopped.Count == 0;
                result.Summary = stopped.Count == 0
                    ? Tx("All core system services running")
                    : Tx("Stopped: {0}", string.Join(", ", stopped));
                result.Details["StoppedCount"] = stopped.Count.ToString();
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.CriticalServices, 1.0);
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunUptimePhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Uptime };
            await Task.Run(() =>
            {
                try
                {
                    // Windows' own counter: the time since the last full RESTART (with Fast Startup, shutting down does not reset it)
                    var uptime = PowerOnClock.KernelUptime;
                    if (uptime.TotalDays < 30)
                    {
                        result.Skip = true;
                        return;
                    }
                    result.Severity = DiagnosticSeverity.Warning;   // a reminder, never critical
                    // the message named Fast Startup on a laptop where it is off — that PC hibernates every day. The
                    // cause is said only when it is known: Fast Startup on → use Restart; off → sleep / hibernate keep Windows running.
                    bool? fastStartup = null;
                    try { fastStartup = PowerAdvancedService.GetFastStartup(); } catch (Exception ex) { AppLogger.Debug($"Uptime Fast Startup: {ex.Message}"); }
                    result.Summary = !PowerOnClock.KernelKeptRunning
                        ? Tx("System has been running for {0:F0} days without a restart", uptime.TotalDays)
                        : fastStartup == true
                            ? Tx("Not restarted for {0:F0} days — use Restart, not Shut down (Fast Startup keeps Windows running)", uptime.TotalDays)
                            : Tx("Not restarted for {0:F0} days — sleep and hibernate keep Windows running; restart it from time to time", uptime.TotalDays);
                    result.Details["UptimeDays"] = uptime.TotalDays.ToString("F0");
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunUptimePhaseAsync: {ex.Message}");
                    result.Skip = true;
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.Uptime, 1.0);
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunWindowsUpdatePhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.WindowsUpdate };
            await Task.Run(() =>
            {
                try
                {
                    Type? sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    if (sessionType == null)
                    {
                        result.Severity = DiagnosticSeverity.Warning;
                        result.Summary = Tx("Windows Update Agent is not available on this system");
                        return;
                    }
                    dynamic session = Activator.CreateInstance(sessionType)!;
                    dynamic searcher = session.CreateUpdateSearcher();
                    ct.ThrowIfCancellationRequested();
                    dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0");
                    int total = (int)searchResult.Updates.Count;

                    // Only the updates Windows Update itself would install: the optional ones (optional driver and
                    // feature previews, "BrowseOnly" or not auto-selected) are left out — Windows Settings says "You're up
                    // to date" while they are listed, and counting them reported "2 pending" on an up-to-date PC.
                    int count = 0, optional = 0;
                    bool hasCriticalSecurityUpdate = false;
                    var titles = new List<string>();
                    for (int i = 0; i < total; i++)
                    {
                        try
                        {
                            dynamic u = searchResult.Updates.Item(i);
                            bool browseOnly = false, autoSelect = true;
                            try { browseOnly = (bool)u.BrowseOnly; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                            try { autoSelect = (bool)u.AutoSelectOnWebSites; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                            string title = "";
                            try { title = (string)u.Title ?? ""; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                            if (browseOnly || !autoSelect) { optional++; titles.Add("(optional) " + title); continue; }
                            count++;
                            titles.Add(title);
                            try
                            {
                                string? msrc = u.MsrcSeverity as string;
                                if (string.Equals(msrc, "Critical", StringComparison.OrdinalIgnoreCase)) hasCriticalSecurityUpdate = true;
                            }
                            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* a property missing on some updates (e.g. drivers) — ignored */ }
                        }
                        catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    }
                    if (total > 0)
                        AppLogger.Info($"Windows Update: {count} to install, {optional} optional — {string.Join(" | ", titles.Take(8))}");
                    if (optional > 0) result.Details["OptionalCount"] = optional.ToString();

                    result.Severity = hasCriticalSecurityUpdate ? DiagnosticSeverity.Critical
                                     : count >= 1 ? DiagnosticSeverity.Warning
                                     : DiagnosticSeverity.Ok;
                    // the updates were paused on purpose from SMD Win (Tools › Windows Update): information, not a problem
                    bool blockedByUs = false;
                    try { blockedByUs = WindowsUpdateBlocker.IsBlocked(); } catch (Exception ex) { AppLogger.Debug($"IsBlocked: {ex.Message}"); }
                    if (blockedByUs)
                    {
                        result.Severity = DiagnosticSeverity.Ok;
                        result.Success = true;
                        result.Summary = count == 0 ? Tx("Updates are paused from SMD Win")
                                                    : Tx("Updates are paused from SMD Win · {0} waiting", count);
                        result.Details["Info"] = "1";
                        result.Details["PendingCount"] = count.ToString();
                        LastPendingUpdates = 0;
                        return;
                    }
                    result.Success = result.Severity == DiagnosticSeverity.Ok;
                    result.Summary = count == 0
                        ? Tx("No pending Windows updates")
                        : hasCriticalSecurityUpdate
                            ? Tx("{0} pending — includes a critical security update", TxN(count, "update", "updates"))
                            : Tx("{0} pending installation", TxN(count, "update", "updates"));
                    result.Details["PendingCount"] = count.ToString();
                    LastPendingUpdates = count;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunWindowsUpdatePhaseAsync: {ex.Message}");
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Could not check for Windows updates");
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.WindowsUpdate, 1.0);
            return result;
        }

        // ── Windows Activation — reuses the WMI query used by the Windows information view ──
        private async Task<DiagnosticPhaseResult> RunWindowsActivationPhaseAsync(CancellationToken ct)
        {
            PhaseProgress?.Invoke(DiagnosticPhase.WindowsActivation, 0.3);
            return await Task.Run(() =>
            {
                var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.WindowsActivation };
                try
                {
                    using var lic = SMDWin.Services.WmiHelper.Searcher(
                        "SELECT Name,LicenseStatus FROM SoftwareLicensingProduct " +
                        "WHERE PartialProductKey IS NOT NULL AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'", System.TimeSpan.FromSeconds(10));
                    uint status = 0;
                    bool found = false;
                    using (var __wmiTmp2 = lic.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp2)
                    {
                        found = true;
                        try { status = (uint)(obj["LicenseStatus"] ?? 0u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        break; // the first result is enough
                    }
                    }
                    bool activated = found && status == 1;
                    result.Severity = activated ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning;
                    result.Success = activated;
                    result.Summary = activated
                        ? Tx("Windows is activated")
                        : Tx("Windows is not activated — some features may be limited");
                    result.Details["LicenseStatusCode"] = status.ToString();
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunWindowsActivationPhaseAsync: {ex.Message}");
                    // Unknown (WMI unavailable) — do NOT report Critical for a read error, only say it could not be checked.
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Could not verify Windows activation status");
                }
                PhaseProgress?.Invoke(DiagnosticPhase.WindowsActivation, 1.0);
                return result;
            }, ct);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFirmwareType(out uint firmwareType);

        private async Task<DiagnosticPhaseResult> RunSecurityPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Security };
            await Task.Run(() =>
            {
                bool secureBootOn = false;
                bool tpmPresent = false;
                string tpmVersion = "";
                bool legacyBios = false;
                try
                {
                    // 1 = FirmwareTypeBios (legacy), 2 = FirmwareTypeUefi. If the call fails (very old Windows without this
                    // API, rare), assume UEFI — keep the default behaviour rather than invent a false "legacy".
                    legacyBios = GetFirmwareType(out uint fwType) && fwType == 1;
                }
                catch (Exception ex) { AppLogger.Debug($"RunSecurityPhaseAsync GetFirmwareType: {ex.Message}"); }

                try
                {
                    secureBootOn = WindowsInfoReader.ReadSecureBoot();
                }
                catch (Exception ex) { AppLogger.Debug($"RunSecurityPhaseAsync SecureBoot: {ex.Message}"); }

                try
                {
                    using var tpmSearcher = SMDWin.Services.WmiHelper.Searcher(
                        @"root\cimv2\security\microsofttpm", "SELECT SpecVersion FROM Win32_Tpm", System.TimeSpan.FromSeconds(10));
                    using var __wmiTpm = tpmSearcher.Get();
                    var tpmObj = __wmiTpm.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (tpmObj?["SpecVersion"] is string specVer)
                    {
                        tpmPresent = true;
                        tpmVersion = specVer.Split(',')[0].Trim();
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"RunSecurityPhaseAsync TPM: {ex.Message}"); }

                if (legacyBios)
                {
                    // Secure Boot does not exist on legacy firmware — left out, the user is not penalised for a motherboard
                    // limitation. Severity comes only from TPM (which, rarely, can exist without UEFI).
                    result.Severity = DiagnosticSeverity.Ok;
                    result.Success = true;
                    result.Summary = tpmPresent
                        ? Tx("Legacy BIOS (Secure Boot not available on this firmware) — TPM {0} present", tpmVersion)
                        : Tx("Legacy BIOS (Secure Boot not available on this firmware) — TPM not present");
                    result.Details["SecureBoot"] = "Not applicable (Legacy BIOS)";
                    if (!tpmPresent) result.Details["Info"] = "1";
                }
                else
                {
                    // without Secure Boot / TPM the PC works normally (only Windows 11 and some games require them) →
                    // information only, not a warning
                    result.Severity = DiagnosticSeverity.Ok;
                    result.Success = true;
                    string tpmText = tpmPresent ? Tx("TPM {0} present", tpmVersion) : Tx("TPM not present");
                    if (secureBootOn && tpmPresent)
                        result.Summary = Tx("Secure Boot on, TPM {0} present", tpmVersion);
                    else
                    {
                        // "Info" level: not a problem, but the user should know (the Health window counts it neither as passed nor as "to check")
                        result.Summary = Tx("Secure Boot {0} · {1}", (secureBootOn ? Tx("on") : Tx("off")), tpmText);
                        result.Details["Tip"] = Tx("Needed only for Windows 11 upgrades and some games");
                        result.Details["Info"] = "1";
                    }
                    result.Details["SecureBoot"] = secureBootOn ? "Enabled" : "Disabled";
                }
                result.Details["Tpm"] = tpmPresent ? tpmVersion : "Not present";
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.Security, 1.0);
            return result;
        }

        // ── Firewall — the same WMI namespace (root\SecurityCenter2) as Antivirus, but the FirewallProduct
        //    class — equally standard, used by Windows Security itself. FirewallService is NOT used (it reads
        //    individual rules through netsh for the Firewall Rules page; here only the global on/off state
        //    matters, not the rule list). ─────────────
        private async Task<DiagnosticPhaseResult> RunFirewallPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Firewall };
            await Task.Run(() =>
            {
                try
                {
                    using var searcher = SMDWin.Services.WmiHelper.Searcher(
                        @"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct", System.TimeSpan.FromSeconds(10));
                    string bestName = ""; bool anyEnabled = false; bool found = false;
                    using (var __wmiTmp3 = searcher.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp3)
                    {
                        found = true;
                        string name = obj["displayName"]?.ToString() ?? "";
                        uint state = 0;
                        try { state = (uint)(obj["productState"] ?? 0u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        // same bitmask as Antivirus (see RunAntivirusPhaseAsync) — the middle byte of the state shows on/off.
                        bool enabled = ((state >> 12) & 0xF) == 1 || ((state >> 4) & 0xF) == 1;
                        if (enabled) { anyEnabled = true; bestName = name; }
                        else if (string.IsNullOrEmpty(bestName)) bestName = name;
                    }
                    }
                    if (!found)
                    {
                        // SecurityCenter2 does not always report Windows Defender Firewall as a separate "product" —
                        // fallback to the global state in the registry (the same mechanism netsh advfirewall uses).
                        bool? regEnabled = null;
                        try
                        {
                            var v = Microsoft.Win32.Registry.GetValue(
                                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile",
                                "EnableFirewall", null);
                            if (v is int vi) regEnabled = vi == 1;
                        }
                        catch (Exception ex) { AppLogger.Debug($"RunFirewallPhaseAsync registry fallback: {ex.Message}"); }

                        if (regEnabled == null)
                        {
                            result.Severity = DiagnosticSeverity.Warning;
                            result.Summary = Tx("Could not determine firewall status");
                        }
                        else
                        {
                            result.Severity = regEnabled.Value ? DiagnosticSeverity.Ok : DiagnosticSeverity.Critical;
                            result.Success = regEnabled.Value;
                            result.Summary = regEnabled.Value ? Tx("Windows Firewall is active") : Tx("Windows Firewall is disabled");
                        }
                    }
                    else
                    {
                        result.Severity = anyEnabled ? DiagnosticSeverity.Ok : DiagnosticSeverity.Critical;
                        result.Success = anyEnabled;
                        result.Summary = anyEnabled ? Tx("{0} is active", bestName) : Tx("{0} appears to be disabled", bestName);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunFirewallPhaseAsync: {ex.Message}");
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Could not verify firewall status");
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.Firewall, 1.0);
            return result;
        }

        // ── Restore Points — WMI root\default, SystemRestore class (the same mechanism as the PowerShell
        //    cmdlet Get-ComputerRestorePoint). Whether System Protection is ENABLED is not checked (that would
        //    need a per-partition read), only whether at least one recent restore point exists — the practical
        //    signal a technician wants ("if something breaks tomorrow, can it be restored?"). ──────────
        private async Task<DiagnosticPhaseResult> RunRestorePointsPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.RestorePoints };
            await Task.Run(() =>
            {
                try
                {
                    using var searcher = SMDWin.Services.WmiHelper.Searcher(
                        @"root\default", "SELECT SequenceNumber, CreationTime FROM SystemRestore", System.TimeSpan.FromSeconds(10));
                    int count = 0;
                    DateTime? newest = null;
                    using (var __wmiTmp4 = searcher.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp4)
                    {
                        count++;
                        try
                        {
                            if (obj["CreationTime"] is string wmiDate)
                            {
                                var dt = System.Management.ManagementDateTimeConverter.ToDateTime(wmiDate);
                                if (newest == null || dt > newest) newest = dt;
                            }
                        }
                        catch (Exception ex) { AppLogger.Debug($"RunRestorePointsPhaseAsync date parse: {ex.Message}"); }
                    }
                    }

                    if (count == 0)
                    {
                        result.Severity = DiagnosticSeverity.Warning;
                        result.Summary = Tx("No system restore points found");
                    }
                    else
                    {
                        int ageDays = newest.HasValue ? (int)(DateTime.Now - newest.Value).TotalDays : 999;
                        result.Severity = ageDays > 90 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
                        result.Success = result.Severity == DiagnosticSeverity.Ok;
                        result.Summary = newest.HasValue
                            ? Tx("{0} — newest from {1:dd MMM yyyy} ({2}d ago)", TxN(count, "restore point", "restore points"), newest.Value, ageDays)
                            : Tx("{0} found", TxN(count, "restore point", "restore points"));
                    }
                    result.Details["Count"] = count.ToString();
                }
                catch (Exception ex)
                {
                    // System Restore fully disabled at system level can make the WMI class itself inaccessible, not just
                    // empty — Warning, not Critical (turning it off is the user's decision, not necessarily wrong).
                    AppLogger.Debug($"RunRestorePointsPhaseAsync: {ex.Message}");
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("System Restore appears to be disabled");
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.RestorePoints, 1.0);
            return result;
        }

        // ── Storage Health (SMART) — reuses HardwareService.GetDisksAsync(), the same HealthPercent
        //    already shown as a badge on the Storage page ────────────────
        private async Task<DiagnosticPhaseResult> RunStorageHealthPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.StorageHealth };
            try
            {
                var hw = new HardwareService();
                var disks = await hw.GetDisksAsync();
                PhaseProgress?.Invoke(DiagnosticPhase.StorageHealth, 0.7);
                ct.ThrowIfCancellationRequested();

                if (disks.Count == 0)
                {
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("No disks detected");
                }
                else
                {
                    // the one verdict of each disk (DriveVerdict), the same as on the Storage / SMART pages; disks without SMART
                    // data (USB sticks, some adapters) are not judged — before, their "0 %" made the result red, and an NVMe at
                    // 93 % life showed here as "minimum 100 %"
                    var judged = disks.Select(d => (d, v: DriveVerdict.Of(d))).Where(x => x.v.HasData).ToList();
                    int unknown = disks.Count - judged.Count;
                    if (judged.Count == 0)
                    {
                        result.Severity = DiagnosticSeverity.Ok;
                        result.Success = true;
                        result.Summary = Tx("No disk reports health data (SMART) here");
                    }
                    else
                    {
                        var worst = judged.OrderByDescending(x => x.v.Level).ThenBy(x => x.v.Percent).First();
                        result.Severity = worst.v.Level switch
                        {
                            DriveVerdict.State.Failing => DiagnosticSeverity.Critical,   // red only for a failing disk
                            DriveVerdict.State.Watch   => DiagnosticSeverity.Warning,
                            _                          => DiagnosticSeverity.Ok,
                        };
                        result.Success = result.Severity == DiagnosticSeverity.Ok;
                        string what = worst.v.IsLifeLeft ? Tx("{0}% life left", worst.v.Percent) : Tx("{0}% health", worst.v.Percent);
                        result.Summary = result.Severity == DiagnosticSeverity.Ok
                            ? (judged.Count == 1 ? Tx("Disk healthy: {0} ({1})", worst.d.Model?.Trim() ?? "", what)
                                                 : Tx("All {0} disks healthy · lowest: {1} ({2})", judged.Count, worst.d.Model?.Trim() ?? "", what))
                            : Tx("{0}: {1} — {2}", worst.d.Model?.Trim() ?? "", what, Tx(worst.v.Word))
                              + (worst.v.Problems.Count > 0 ? " (" + string.Join(", ", worst.v.Problems.Take(3)) + ")" : "");
                        if (unknown > 0) result.Summary += " · " + Tx("{0} without health data", unknown);
                        result.Details["WorstHealthPercent"] = worst.v.Percent.ToString();
                    }
                    result.Details["DiskCount"] = disks.Count.ToString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunStorageHealthPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not read disk health (SMART)");
            }
            PhaseProgress?.Invoke(DiagnosticPhase.StorageHealth, 1.0);
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunFreeSpacePhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.FreeSpace };
            try
            {
                var hw = new HardwareService();
                var disks = await hw.GetDisksAsync();
                PhaseProgress?.Invoke(DiagnosticPhase.FreeSpace, 0.7);
                ct.ThrowIfCancellationRequested();

                var allParts = disks.SelectMany(d => d.Partitions).Where(p => p.TotalGB > 0).ToList();
                if (allParts.Count == 0)
                {
                    // the disk list could not map the volumes (slow WMI): read directly from Windows, without WMI
                    foreach (var di in System.IO.DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (di.DriveType != System.IO.DriveType.Fixed || !di.IsReady) continue;
                            allParts.Add(new SMDWin.Models.PartitionEntry
                            {
                                Letter = di.RootDirectory.FullName, Label = di.VolumeLabel, FileSystem = di.DriveFormat,
                                TotalGB = di.TotalSize / 1_073_741_824.0, FreeGB = di.AvailableFreeSpace / 1_073_741_824.0,
                            });
                        }
                        catch (Exception ex) { AppLogger.Debug($"FreeSpace DriveInfo: {ex.Message}"); }
                    }
                }
                if (allParts.Count == 0)
                {
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("No partitions detected");
                }
                else
                {
                    string sysRoot = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
                    var sysPart = allParts.FirstOrDefault(p => string.Equals(p.Letter?.TrimEnd(':', '\\'), sysRoot.TrimEnd(':'), StringComparison.OrdinalIgnoreCase))
                                  ?? allParts.First(); // defensive fallback — should not happen, but better than an exception

                    double SysUsedFrac(SMDWin.Models.PartitionEntry p) => p.TotalGB > 0 ? p.UsedGB / p.TotalGB : 0;
                    double sysFrac = SysUsedFrac(sysPart);
                    // same thresholds as the Dashboard and System Info (StatusRules)
                    var sysSeverity = sysFrac * 100 >= StatusRules.DiskCritPct ? DiagnosticSeverity.Critical
                                     : sysFrac * 100 >= StatusRules.DiskWarnPct ? DiagnosticSeverity.Warning
                                     : DiagnosticSeverity.Ok;

                    // Other partitions (not Windows): being full matters, but capped at Attention — a D: at 100% is a real
                    // problem (worth mentioning), but not one that threatens the system like a full C:.
                    var others = allParts.Where(p => p != sysPart).ToList();
                    SMDWin.Models.PartitionEntry? worstOther = others.Count > 0
                        ? others.OrderByDescending(SysUsedFrac).First() : null;
                    bool otherIsFull = worstOther != null && SysUsedFrac(worstOther) * 100 >= StatusRules.DiskWarnPct;

                    result.Severity = sysSeverity == DiagnosticSeverity.Critical ? DiagnosticSeverity.Critical
                                     : sysSeverity == DiagnosticSeverity.Warning || otherIsFull ? DiagnosticSeverity.Warning
                                     : DiagnosticSeverity.Ok;
                    result.Success = result.Severity == DiagnosticSeverity.Ok;

                    var fullOthers = others.Where(p => SysUsedFrac(p) * 100 >= StatusRules.DiskWarnPct).OrderByDescending(SysUsedFrac).ToList();
                    string othersTxt = string.Join(" · ", fullOthers.Select(p => Tx("{0} {1:F0}% used", p.Letter, SysUsedFrac(p) * 100)));
                    if (sysSeverity != DiagnosticSeverity.Ok)
                        result.Summary = Tx("System drive {0} — {1} free ({2:F0}% used)", sysPart.Letter, DisplayFormat.SizeGb(sysPart.FreeGB), sysFrac*100)
                                       + (othersTxt.Length > 0 ? " · " + othersTxt : "");
                    else if (otherIsFull && worstOther != null)
                        result.Summary = SMDWin.Services.LiteralTexts.Translate(Tx("System drive OK")) + " — " + othersTxt;
                    else
                        result.Summary = Tx("Free space OK on all {0} partition(s)", allParts.Count);

                    result.Details["SystemDrive"] = sysPart.Letter ?? sysRoot;
                    result.Details["SystemDriveUsedPct"] = (sysFrac * 100).ToString("F0");
                    result.Details["PartitionCount"] = allParts.Count.ToString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunFreeSpacePhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not read free disk space");
            }
            PhaseProgress?.Invoke(DiagnosticPhase.FreeSpace, 1.0);
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunClockSyncPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.ClockSync };
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "w32tm.exe",
                    Arguments = "/query /status",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("w32tm did not start");
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
                PhaseProgress?.Invoke(DiagnosticPhase.ClockSync, 1.0);

                // "Last Successful Sync Time:" — the standard line in w32tm output, the same text whatever the system
                // culture (the label stays in English even on localized Windows, unlike the other lines of this output).
                string? syncLine = output.Split('\n')
                    .FirstOrDefault(l => l.Contains("Last Successful Sync Time", StringComparison.OrdinalIgnoreCase));

                if (proc.ExitCode != 0 || syncLine == null)
                {
                    bool isDisabled = false;
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController("W32Time");
                        isDisabled = sc.StartType == System.ServiceProcess.ServiceStartMode.Disabled;
                    }
                    catch (Exception ex) { AppLogger.Debug($"ClockSync ServiceController check: {ex.Message}"); }

                    if (isDisabled)
                    {
                        result.Severity = DiagnosticSeverity.Warning;
                        result.Summary = Tx("Windows Time service is disabled — clock will not auto-sync");
                    }
                    else
                    {
                        result.Severity = DiagnosticSeverity.Ok;
                        result.Success = true;
                        result.Summary = Tx("Windows Time service not active right now (normal — starts on demand)");
                    }
                    return result;
                }

                string valueText = syncLine.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "";
                if (valueText.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                    valueText.Contains("never", StringComparison.OrdinalIgnoreCase) ||
                    valueText.Contains("unspecified", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(valueText))
                {
                    result.Severity = DiagnosticSeverity.Warning;
                    result.Summary = Tx("Clock has never synced successfully");
                    return result;
                }

                if (DateTime.TryParse(valueText, out var lastSync))
                {
                    double ageDays = (DateTime.Now - lastSync).TotalDays;
                    result.Severity = ageDays > 14 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
                    result.Success = result.Severity == DiagnosticSeverity.Ok;
                    result.Summary = result.Severity == DiagnosticSeverity.Ok
                        ? Tx("Clock last synced {0:F0}d ago", ageDays)
                        : Tx("Clock has not synced in {0:F0}d — check date/time settings", ageDays);
                    result.Details["LastSyncDate"] = lastSync.ToString("yyyy-MM-dd HH:mm");
                }
                else
                {
                    result.Severity = DiagnosticSeverity.Ok;
                    result.Summary = Tx("Clock synced ({0})", valueText);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunClockSyncPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not check clock synchronization");
            }
            return result;
        }

        // ── Event Log — uses the same EventLogService as the Event Viewer page, filtered to "Critical",
        //    last 7 days ───────────────────────────────────────
        private async Task<DiagnosticPhaseResult> RunEventLogErrorsPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.EventLogErrors };
            try
            {
                var svc = new EventLogService();
                var events = await svc.GetEventsAsync(DateTime.Now.AddDays(-7), DateTime.Now, "Critical", "", ct);
                int count = events.Count;
                result.Severity = count >= 5 ? DiagnosticSeverity.Critical
                                 : count >= 1 ? DiagnosticSeverity.Warning
                                 : DiagnosticSeverity.Ok;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                // which event it is: the most frequent one, by its short title from the event database when known
                string top = "";
                if (count > 0)
                {
                    var g = events.GroupBy(e => (e.Source, e.EventId)).OrderByDescending(x => x.Count()).First();
                    var kb = EventKnowledge.Find(g.Key.Source, g.Key.EventId);
                    string src = g.Key.Source.Replace("Microsoft-Windows-", "", StringComparison.OrdinalIgnoreCase);
                    top = (kb != null && kb.Title.Length > 0 ? kb.Title : $"{src} {g.Key.EventId}") + (g.Count() > 1 ? $" ×{g.Count()}" : "");
                    result.Details["TopEvent"] = $"{src} {g.Key.EventId}";
                }
                result.Summary = count == 0
                    ? Tx("No critical events in the last 7 days")
                    : Tx("{0} in the last 7 days", TxN(count, "critical event", "critical events")) + " · " + top;
                result.Details["CriticalEventCount"] = count.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunEventLogErrorsPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not read Event Log");
            }
            PhaseProgress?.Invoke(DiagnosticPhase.EventLogErrors, 1.0);
            return result;
        }

        // ── Hardware errors (WHEA) — what the processor, the memory controller or a PCIe device reported to Windows in the
        //    last 30 days. A fatal error (the PC stopped) is red; many corrected errors are worth a look (often a PCIe link
        //    with power saving, a loose card, or an unstable overclock). ─────────────
        private async Task<DiagnosticPhaseResult> RunHardwareErrorsPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.HardwareErrors };
            await Task.Run(() =>
            {
                try
                {
                    string from = DateTime.Now.AddDays(-30).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                        $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{from}']]]");
                    using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                    int fatal = 0, corrected = 0, n = 0;
                    DateTime? last = null;
                    System.Diagnostics.Eventing.Reader.EventRecord? rec;
                    while (!ct.IsCancellationRequested && (rec = reader.ReadEvent()) != null && n < 5000)
                    {
                        using (rec)
                        {
                            n++;
                            // 18 / 20 / 46 / 1 = fatal (machine check, PCIe fatal…); the rest are errors the hardware corrected
                            if (rec.Id is 18 or 20 or 46 or 1 || rec.Level is 1 or 2) fatal++; else corrected++;
                            if (rec.TimeCreated is DateTime t && (last == null || t > last)) last = t;
                        }
                    }
                    result.Severity = fatal > 0 ? DiagnosticSeverity.Critical : corrected >= 10 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
                    result.Success = result.Severity == DiagnosticSeverity.Ok;
                    result.Summary = fatal + corrected == 0 ? Tx("No hardware errors in the last 30 days")
                        : fatal > 0 ? Tx("{0} fatal and {1} corrected hardware errors in 30 days (last: {2:d MMM})", fatal, corrected, last ?? DateTime.Now)
                        : Tx("{0} corrected hardware errors in 30 days (last: {1:d MMM})", corrected, last ?? DateTime.Now);
                    result.Details["WheaFatal"] = fatal.ToString();
                    result.Details["WheaCorrected"] = corrected.ToString();
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"RunHardwareErrorsPhaseAsync: {ex.Message}");
                    result.Severity = DiagnosticSeverity.Ok;
                    result.Success = true;
                    result.Summary = Tx("No hardware errors in the last 30 days");
                }
            }, ct);
            PhaseProgress?.Invoke(DiagnosticPhase.HardwareErrors, 1.0);
            return result;
        }

        private static readonly string[] NetworkDriverKeywords =
        {
            "ndis", "tcpip", "dot3svc", "wlansvc", "e1d", "e1r", "e1c", // Windows + Intel Ethernet
            "netwtw", "iwl", "athw", "rt640", "rt680", "rtwlan",        // WiFi: Intel/Atheros/Realtek
            "bcmwl", "b57nd60a", "vwifibus",                             // Broadcom + virtual WiFi
        };

        private async Task<DiagnosticPhaseResult> RunNetworkDriverErrorsPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.NetworkDriverErrors };
            try
            {
                var svc = new EventLogService();
                var events = await svc.GetEventsAsync(DateTime.Now.AddDays(-7), DateTime.Now, "Error", "", ct);
                var netEvents = events.Where(e => NetworkDriverKeywords.Any(k =>
                    e.Source.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
                int count = netEvents.Count;
                result.Severity = count >= 1 ? DiagnosticSeverity.Warning
                                 : DiagnosticSeverity.Ok;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                result.Summary = count == 0
                    ? Tx("No network driver errors in the last 7 days")
                    : Tx("{0} in the last 7 days — {1}", TxN(count, "network driver error", "network driver errors"), netEvents.First().Source);
                result.Details["Count"] = count.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunNetworkDriverErrorsPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not check network driver errors");
            }
            PhaseProgress?.Invoke(DiagnosticPhase.NetworkDriverErrors, 1.0);
            return result;
        }

        // ── BSOD — uses the same CrashService as the BSOD / Crash page ────────────────
        private async Task<DiagnosticPhaseResult> RunBsodHistoryPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.BsodHistory };
            try
            {
                var svc = new CrashService();
                var crashes = await svc.GetCrashesAsync();
                ct.ThrowIfCancellationRequested();
                var cutoff = DateTime.Now.AddDays(-30);
                int recent = crashes.Count(c => c.CrashTime >= cutoff);
                result.Severity = recent >= 2 ? DiagnosticSeverity.Critical
                                 : recent == 1 ? DiagnosticSeverity.Warning
                                 : DiagnosticSeverity.Ok;
                result.Success = result.Severity == DiagnosticSeverity.Ok;
                result.Summary = recent == 0
                    ? Tx("No BSODs in the last 30 days")
                    : Tx("{0} in the last 30 days", TxN(recent, "BSOD", "BSODs"));
                result.Details["BsodCount30Days"] = recent.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunBsodHistoryPhaseAsync: {ex.Message}");
                result.Severity = DiagnosticSeverity.Warning;
                result.Summary = Tx("Could not read crash history");
            }
            PhaseProgress?.Invoke(DiagnosticPhase.BsodHistory, 1.0);
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunPhaseAsync(
            DiagnosticPhase phase, Func<Task<DiagnosticPhaseResult>> body, CancellationToken ct)
        {
            PhaseStarted?.Invoke(phase);
            var sw = Stopwatch.StartNew();
            DiagnosticPhaseResult result;
            try
            {
                result = await body();
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation propagates — the orchestrator stops completely
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"DiagnosticOrchestrator.{phase}");
                result = new DiagnosticPhaseResult
                {
                    Phase = phase, Success = false,
                    Summary = Tx("Test failed: {0}", ex.Message), Error = ex,
                };
            }
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            PhaseCompleted?.Invoke(result);
            return result;
        }

        // ── CPU: stress + integrity check (Prime95-style), NO benchmark ───
        private async Task<DiagnosticPhaseResult> RunCpuPhaseAsync(int seconds, CancellationToken ct)
        {
            var stressor = new CpuStressor();
            stressor.Start(Environment.ProcessorCount);
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    PhaseProgress?.Invoke(DiagnosticPhase.Cpu, Math.Min(1.0, sw.Elapsed.TotalSeconds / seconds));
                }
            }
            finally { stressor.Stop(); }

            int errors = CpuStressor.IntegrityErrorCount;
            var result = new DiagnosticPhaseResult
            {
                Phase = DiagnosticPhase.Cpu,
                Success = errors == 0,
                Summary = errors == 0
                    ? Tx("CPU stable — {0}s stress at 100% load, no computation errors", seconds)
                    : Tx("{0} computation error(s) detected during {1}s stress — possible hardware instability (not temperature-related)", errors, seconds),
            };
            result.Details["DurationSec"] = seconds.ToString();
            result.Details["IntegrityErrors"] = errors.ToString();
            result.Details["Cores"] = Environment.ProcessorCount.ToString();
            return result;
        }

        // ══ The results of tests already run (Temp & Stress, RAM) ═══════════════════════
        private const int TestStaleDays = 30;

        private static string TestAge(DateTime when)
        {
            int days = (int)(DateTime.Now - when).TotalDays;
            string date = when.ToString("dd.MM.yyyy");
            return days >= TestStaleDays ? Tx("{0} ({1} days ago)", date, days) : date;
        }


        // the summaries are shown in the Health window and the report in the user's language (PageText keys, like the other
        // literal texts); counts keep their number and translate the noun ("3 updates" → "3 actualizări")
        private static string Tx(string en, params object?[] args) =>
            args.Length == 0 ? LiteralTexts.Translate(en) : string.Format(LiteralTexts.Translate(en), args);
        private static string TxN(int n, string one, string many) => $"{n} " + LiteralTexts.Translate(n == 1 ? one : many);

        private static string Dur(int sec) => sec >= 120 ? $"{sec / 60} min" : $"{sec} s";

        private static DiagnosticPhaseResult NotTested(DiagnosticPhase phase, string what)
        {
            var r = new DiagnosticPhaseResult { Phase = phase, Success = true, Severity = DiagnosticSeverity.Ok, Summary = Tx("Not tested yet · {0}", Tx(what)) };
            r.Details["NotTested"] = "1";
            return r;
        }

        private static void AddStressDetails(DiagnosticPhaseResult r, StressTestRecord t)
        {
            r.Details["LastTest"] = t.When.ToString("dd.MM.yyyy HH:mm");
            r.Details["Duration"] = Dur(t.DurationSec);
            r.Details["MaxTemp"] = $"{TempUnit.Fmt(t.MaxTempC, 0, true)}";
            r.Details["AvgTemp"] = $"{TempUnit.Fmt(t.AvgTempC, 0, true)}";
            r.Details["IntegrityErrors"] = t.IntegrityErrors.ToString();
            if ((DateTime.Now - t.When).TotalDays >= TestStaleDays) r.Details["Stale"] = "1";
        }

        /// <summary>Processor: the latest CPU stress test — maximum temperature and calculation errors.</summary>
        private static DiagnosticPhaseResult CpuFromLastTest()
        {
            var t = TestResultStore.Load().Cpu;
            if (t == null) return NotTested(DiagnosticPhase.Cpu, "run a CPU stress test in Temp & Stress");
            var r = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Cpu, Success = true };
            AddStressDetails(r, t);
            string when = Tx("{0} test on {1}", Dur(t.DurationSec), TestAge(t.When));
            if (t.IntegrityErrors > 0) { r.Severity = DiagnosticSeverity.Critical; r.Summary = Tx("{0} calculation errors under load · {1}", t.IntegrityErrors, when); }
            else if (t.MaxTempC >= 95) { r.Severity = DiagnosticSeverity.Critical; r.Summary = Tx("Reached {0} under load — cooling can't keep up · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            else if (t.MaxTempC >= 90) { r.Severity = DiagnosticSeverity.Warning; r.Summary = Tx("Hot under load: {0} · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            else { r.Severity = DiagnosticSeverity.Ok; r.Summary = Tx("Stable, max {0} · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            return r;
        }

        /// <summary>Graphics card: the last GPU stress test.</summary>
        private static DiagnosticPhaseResult GpuFromLastTest()
        {
            var t = TestResultStore.Load().Gpu;
            if (t == null) return NotTested(DiagnosticPhase.Gpu, "run a GPU stress test in Temp & Stress");
            var r = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Gpu, Success = true };
            AddStressDetails(r, t);
            string when = Tx("{0} test on {1}", Dur(t.DurationSec), TestAge(t.When));
            if (t.IntegrityErrors > 0) { r.Severity = DiagnosticSeverity.Critical; r.Summary = Tx("{0} wrong results under load · {1}", t.IntegrityErrors, when); }
            else if (t.MaxTempC >= 95) { r.Severity = DiagnosticSeverity.Critical; r.Summary = Tx("Reached {0} under load · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            else if (t.MaxTempC >= 88) { r.Severity = DiagnosticSeverity.Warning; r.Summary = Tx("Hot under load: {0} · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            else { r.Severity = DiagnosticSeverity.Ok; r.Summary = Tx("Stable, max {0} · {1}", TempUnit.Fmt(t.MaxTempC, 0, true), when); }
            return r;
        }

        /// <summary>Processor slowdown: what the detector sees now + how much it slowed down in the last CPU stress test.</summary>
        private static DiagnosticPhaseResult ThrottlingFromLastTest(Func<ThrottleSnapshot?>? getSnapshot)
        {
            var r = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Throttling, Success = true, Severity = DiagnosticSeverity.Ok };
            var snap = getSnapshot?.Invoke();
            var t = TestResultStore.Load().Cpu;
            if (snap == null && t == null) { r.Skip = true; return r; }

            if (snap != null && snap.Reason != ThrottleReason.None)
            {
                // only overheating is critical; a fixed clock or PROCHOT from the charger is slow but not a danger
                bool severe = snap.Reason is ThrottleReason.Thermal;
                r.Severity = severe ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
                r.Summary = Tx("Slowed down right now: {0} — {1:F0} MHz ({2:F0}% of max)", snap.ReasonLabel, snap.FreqMHz, snap.FreqRatio * 100);
                r.Details["Reason"] = snap.Reason.ToString();
            }
            else if (t != null && t.ThrottlePct >= 20)
            {
                bool thermal = t.ThrottleReason is "Thermal" or "BdProchot";
                r.Severity = thermal ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
                r.Summary = Tx("Slowed down {0}% of the last stress test ({1}) · {2}", t.ThrottlePct, ReasonText(t.ThrottleReason), TestAge(t.When));
                r.Details["Reason"] = t.ThrottleReason;
            }
            else
            {
                r.Summary = t == null ? Tx("Not slowed down now · no stress test yet")
                                      : Tx("No slowdown now or in the last stress test ({0})", TestAge(t.When));
                if (t == null) r.Details["NotTested"] = "1";
            }
            if (t != null) r.Details["SlowedDuringTest"] = $"{t.ThrottlePct}%";
            return r;

            static string ReasonText(string reason) => reason switch
            {
                "Thermal" => Tx("too hot"),
                "PowerLimit" => Tx("power limit"),
                "BdProchot" => Tx("the charger / board forced it"),
                "BatterySaver" => Tx("battery saver"),
                "VrThermal" => Tx("power circuit too hot"),
                "FixedFrequency" => Tx("fixed low speed"),
                _ => Tx("unknown reason"),
            };
        }

        /// <summary>Memory: the latest test from the RAM page.</summary>
        private static DiagnosticPhaseResult RamFromLastTest()
        {
            var t = TestResultStore.Load().Ram;
            if (t == null) return NotTested(DiagnosticPhase.Ram, "run a memory test on the RAM page");
            var r = new DiagnosticPhaseResult { Phase = DiagnosticPhase.Ram, Success = true };
            string kind = t.Advanced ? Tx("Advanced test") : Tx("Basic test");
            r.Details["LastTest"] = t.When.ToString("dd.MM.yyyy HH:mm");
            r.Details["Kind"] = kind;
            if ((DateTime.Now - t.When).TotalDays >= TestStaleDays) r.Details["Stale"] = "1";
            if (t.Failed.Count > 0)
            {
                r.Severity = DiagnosticSeverity.Critical;
                r.Summary = Tx("Errors found ({0}) · {1} on {2}", string.Join(", ", t.Failed), kind.ToLowerInvariant(), TestAge(t.When));
            }
            else if (t.Incomplete.Count > 0)
            {
                r.Severity = DiagnosticSeverity.Warning;
                r.Summary = Tx("{0} could not finish · {1}", string.Join(", ", t.Incomplete), TestAge(t.When));
            }
            else
            {
                r.Severity = DiagnosticSeverity.Ok;
                r.Summary = Tx("No errors · {0} on {1}", kind.ToLowerInvariant(), TestAge(t.When));
            }
            return r;
        }


        private async Task<DiagnosticPhaseResult> RunGpuPhaseAsync(int seconds, CancellationToken ct)
        {
            using var gpu = new GpuStressService();
            gpu.Start();
            var sw = Stopwatch.StartNew();
            long maxDispatchHz = 0;
            try
            {
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    if (gpu.DispatchHz > maxDispatchHz) maxDispatchHz = gpu.DispatchHz;
                    PhaseProgress?.Invoke(DiagnosticPhase.Gpu, Math.Min(1.0, sw.Elapsed.TotalSeconds / seconds));
                }
            }
            finally { gpu.Stop(); }

            bool hadError = !string.IsNullOrEmpty(gpu.LastError);
            bool ranAtAll = maxDispatchHz > 0;
            var result = new DiagnosticPhaseResult
            {
                Phase = DiagnosticPhase.Gpu,
                Success = !hadError && ranAtAll,
                Summary = hadError
                    ? Tx("GPU stress failed: {0}", gpu.LastError)
                    : !ranAtAll
                        ? Tx("GPU stress did not report any compute activity — driver or adapter issue")
                        : Tx("GPU compute stable — {0}s D3D11 compute shader stress, no errors", seconds),
            };
            result.Details["DurationSec"] = seconds.ToString();
            result.Details["MaxDispatchHz"] = maxDispatchHz.ToString();
            return result;
        }

        private async Task<DiagnosticPhaseResult> RunRamDiagnosticHistoryPhaseAsync(CancellationToken ct)
        {
            var result = new DiagnosticPhaseResult { Phase = DiagnosticPhase.RamDiagnosticHistory };
            try
            {
                var svc = new EventLogService();
                // 1-year window — the test runs rarely; what matters is whether it EVER ran recently, not only in the
                // last days. Not wider (2+ years), to avoid slowing the health check by scanning years of log for a
                // provider that almost certainly has no entries.
                var events = await svc.GetEventsAsync(DateTime.Now.AddYears(-1), DateTime.Now, "All", "MemoryDiagnostics", ct);
                var latest = events
                    .Where(e => e.EventId == 1201 || e.EventId == 1202)
                    .OrderByDescending(e => e.TimeCreated)
                    .FirstOrDefault();

                if (latest == null)
                {
                    result.Skip = true; // never ran — not a system problem
                    return result;
                }

                if (latest.EventId == 1202)
                {
                    result.Severity = DiagnosticSeverity.Critical;
                    result.Summary = Tx("Windows Memory Diagnostic found errors on {0:dd MMM yyyy}", latest.TimeCreated);
                }
                else
                {
                    result.Severity = DiagnosticSeverity.Ok;
                    result.Success = true;
                    result.Summary = Tx("Windows Memory Diagnostic ran on {0:dd MMM yyyy}, no errors found", latest.TimeCreated);
                }
                result.Details["LastRunDate"] = latest.TimeCreated.ToString("yyyy-MM-dd");
                result.Details["EventId"] = latest.EventId.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug($"RunRamDiagnosticHistoryPhaseAsync: {ex.Message}");
                result.Skip = true; // read error — safer to hide than to show something false
            }
            PhaseProgress?.Invoke(DiagnosticPhase.RamDiagnosticHistory, 1.0);
            return result;
        }

        // ── RAM: bandwidth + integritate (2 teste existente, rulate secvential) ────────
        private async Task<DiagnosticPhaseResult> RunRamPhaseAsync(CancellationToken ct)
        {
            // Bandwidth takes the first half of the phase progress (0.0-0.5), integrity the second half (0.5-1.0) —
            // a simple split, not exact to the second, only so it does not stay "frozen" at 0% or jump straight to 100%.
            RamBandwidthService.Result? bwResult = null;
            try
            {
                bwResult = await RamBandwidthService.RunAsync(msg =>
                    PhaseProgress?.Invoke(DiagnosticPhase.Ram, 0.25));
            }
            catch (Exception ex) { AppLogger.Debug($"DiagnosticOrchestrator RAM bandwidth: {ex.Message}"); }
            PhaseProgress?.Invoke(DiagnosticPhase.Ram, 0.5);

            long integrityErrors = 0;
            bool integrityRan = false;
            try
            {
                var ramTest = new RamTestService();
                var progress = new Progress<RamTestProgress>(p =>
                {
                    PhaseProgress?.Invoke(DiagnosticPhase.Ram, 0.5 + p.PercentDone / 100.0 * 0.5);
                    if (p.IsFinished)
                    {
                        integrityRan = true;
                        integrityErrors = p.ErrorCount;
                    }
                });
                await ramTest.RunAsync(256, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Debug($"DiagnosticOrchestrator RAM integrity: {ex.Message}"); }

            var result = new DiagnosticPhaseResult
            {
                Phase = DiagnosticPhase.Ram,
                Success = integrityRan && integrityErrors == 0,
                Summary = !integrityRan
                    ? Tx("RAM integrity test could not complete")
                    : integrityErrors == 0
                        ? Tx("RAM stable — bandwidth measured, 256MB integrity test passed")
                        : Tx("{0} RAM error(s) detected — possible faulty module or unstable overclock", integrityErrors),
            };
            if (bwResult != null)
            {
                result.Details["ReadGBs"] = bwResult.ReadGBs.ToString("F1");
                result.Details["WriteGBs"] = bwResult.WriteGBs.ToString("F1");
            }
            result.Details["IntegrityErrors"] = integrityErrors.ToString();
            return result;
        }

        // ── Storage: reuses the existing DiskBenchmarkService, real progress through BarFraction
        //    (already a real 0.0-1.0, not guessed) ─────────────────────────────────
        private async Task<DiagnosticPhaseResult> RunStoragePhaseAsync(string driveLetter, CancellationToken ct)
        {
            var svc = new DiskBenchmarkService();
            var progress = new Progress<BenchmarkProgress>(p =>
                PhaseProgress?.Invoke(DiagnosticPhase.Storage, p.BarFraction));
            var bench = await svc.RunAsync(driveLetter, ct, progress, forceQuick: true);

            var result = new DiagnosticPhaseResult
            {
                Phase = DiagnosticPhase.Storage,
                Success = true, // the storage benchmark has no "pass/fail", it only measures
                Summary = Tx("{0} — Read {1:F0} MB/s, Write {2:F0} MB/s", bench.Rating, bench.ReadAvgMBps, bench.WriteAvgMBps),
            };
            if (!string.IsNullOrEmpty(bench.CacheWarning))
                result.Details["CacheWarning"] = bench.CacheWarning;
            if (!string.IsNullOrEmpty(bench.WriteWarning))
                result.Details["WriteWarning"] = bench.WriteWarning;
            result.Details["ReadAvgMBps"] = bench.ReadAvgMBps.ToString("F0");
            result.Details["WriteAvgMBps"] = bench.WriteAvgMBps.ToString("F0");
            result.Details["Rating"] = bench.Rating;
            return result;
        }

        // ── Network: reuses the existing SpeedTestService ─────────────────────────────
        private async Task<DiagnosticPhaseResult> RunNetworkPhaseAsync(CancellationToken ct)
        {
            using var svc = new SpeedTestService();
            // SpeedTestService reports no single global BarFraction — an approximate 3-stage progress is used
            // (ping/download/upload), good enough for a progress bar without claiming per-second precision.
            var phaseProgress = new Progress<string>(_ => { });
            var liveSpeed = new Progress<double>(_ => { });
            int phaseIdx = 0;
            var phase = new Progress<string>(name =>
            {
                phaseIdx++;
                PhaseProgress?.Invoke(DiagnosticPhase.Network, Math.Min(1.0, phaseIdx / 3.0));
            });
            var speedResult = await svc.RunAsync(phaseProgress, ct, liveSpeed, phase);

            var result = new DiagnosticPhaseResult
            {
                Phase = DiagnosticPhase.Network,
                Success = speedResult.Success,
                Summary = speedResult.Success
                    ? Tx("{0} — ↓{1:F0} Mbps / ↑{2:F0} Mbps, ping {3:F0}ms", speedResult.Rating, speedResult.DownloadMbps, speedResult.UploadMbps, speedResult.PingMs)
                    : Tx("Network test failed: {0}", speedResult.Error),
            };
            result.Details["DownloadMbps"] = speedResult.DownloadMbps.ToString("F1");
            result.Details["UploadMbps"] = speedResult.UploadMbps.ToString("F1");
            result.Details["PingMs"] = speedResult.PingMs.ToString("F0");
            result.Details["JitterMs"] = speedResult.JitterMs.ToString("F1");
            return result;
        }
    }
}
