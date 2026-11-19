
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public sealed class SmartctlAttribute
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public int Worst { get; set; }
        public int Threshold { get; set; }
        public long RawValue { get; set; }
        public string RawString { get; set; } = "";
        public bool WhenFailed { get; set; } // true if the attribute fell below its threshold
    }

    public sealed class SmartctlSelfTestEntry
    {
        public string Type { get; set; } = "";       // "Short offline", "Extended offline" etc.
        public string Status { get; set; } = "";      // "Completed without error", "In progress" etc.
        public int PercentRemaining { get; set; }
        public long LifetimeHours { get; set; }
    }

    public sealed class SmartctlReport
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";

        public string ModelName { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string FirmwareVersion { get; set; } = "";
        /// <summary>SATA link: what the drive supports and what it negotiated ("6.0 Gb/s" / "3.0 Gb/s"), with the speed in
        /// tenths of Gb/s (60 = 6.0); "" / 0 = not reported (NVMe, USB bridges).</summary>
        public string SataLinkMax { get; set; } = "";
        public string SataLinkCurrent { get; set; } = "";
        public int SataLinkMaxTenths { get; set; }
        public int SataLinkCurrentTenths { get; set; }
        /// <summary>NVMe thermal management: how many times the drive slowed itself down (light / heavy) and for how long (s).</summary>
        public long? NvmeThrottleLightCount { get; set; }
        public long? NvmeThrottleHeavyCount { get; set; }
        public long? NvmeThrottleLightSeconds { get; set; }
        public long? NvmeThrottleHeavySeconds { get; set; }
        public bool IsNvme { get; set; }
        /// <summary>The logical sector size (512 / 4096), from "logical_block_size"; 0 = unknown.</summary>
        public int LogicalBlockSize { get; set; }

        public long PowerOnHours { get; set; } = -1;
        /// <summary>From "rotation_rate": 0 = solid state, > 0 = a spinning disk's rpm, -1 = not reported.</summary>
        public int RotationRate { get; set; } = -1;
        /// <summary>The physical form the disk reports about itself (ATA IDENTIFY word 168, "form_factor"): "M.2", "2.5\"",
        /// "3.5\"", "1.8\"", "mSATA"… — "" when the disk does not say (NVMe never does).</summary>
        public string FormFactor { get; set; } = "";
        public int PowerCycleCount { get; set; } = -1;
        public double? TemperatureCelsius { get; set; }

        // NVMe-specific (much more reliable than the ATA translation through WMI)
        public int? NvmePercentageUsed { get; set; }      // 0-100, straight from the controller
        public int? NvmeAvailableSparePct { get; set; }
        public long? NvmeDataUnitsWrittenGB { get; set; }
        public long? NvmeMediaErrors { get; set; }
        public int? NvmeCriticalWarning { get; set; }     // bitmask — 0 = all ok
        public int?  NvmeAvailableSpareThreshold { get; set; }
        public long? NvmeDataUnitsReadGB { get; set; }
        public long? NvmeHostReads { get; set; }
        public long? NvmeHostWrites { get; set; }
        public long? NvmeControllerBusyMinutes { get; set; }
        public long? NvmeUnsafeShutdowns { get; set; }
        public long? NvmeErrorLogEntries { get; set; }
        public long? NvmeWarningTempMinutes { get; set; }
        public long? NvmeCriticalTempMinutes { get; set; }

        // ATA-specific
        public List<SmartctlAttribute> AtaAttributes { get; set; } = new();

        public bool OverallHealthPassed { get; set; } = true;
        public List<SmartctlSelfTestEntry> SelfTestLog { get; set; } = new();
        public bool SelfTestInProgress { get; set; }
        public int SelfTestPercentRemaining { get; set; }
        /// <summary>true only if the disk actually reported a percentage (otherwise SelfTestPercentRemaining=0 would falsely mean
        /// "100% done").</summary>
        public bool SelfTestPercentKnown { get; set; }

        public int ExitStatus { get; set; }
        /// <summary>The disk was in standby/sleep and was not woken (only for reads with -n standby).</summary>
        public bool InStandby { get; set; }
        /// <summary>smartctl messages with warning/error severity (without the informational ones).</summary>
        public List<string> Messages { get; set; } = new();

        public string RawJson { get; set; } = ""; // kept for debugging/export
    }

    /// <summary>Result of the light temperature read (ReadTemperatureAsync).</summary>
    public sealed class SmartctlTempReading
    {
        public double? Celsius { get; set; }
        /// <summary>The disk is asleep — it was not woken, so there is no value (not an error).</summary>
        public bool InStandby { get; set; }
        public string ErrorMessage { get; set; } = "";
    }

    public static class SmartctlService
    {
        private const int ReadTimeoutMs = 20_000;
        private const int TempTimeoutMs = 10_000;
        private const int ReportCacheSeconds = 10;
        private const int CooldownSeconds = 60;
        private const int SearchRetrySeconds = 15;

        // ── Localizare smartctl.exe ───────────────────────────────────────────
        private static string? _cachedExePath;
        private static DateTime _lastSearchUtc = DateTime.MinValue;
        private static bool _loggedFound, _loggedMissing;
        private static readonly object _searchLock = new();

        public static string? FindExecutable()
        {
            lock (_searchLock)
            {
                if (_cachedExePath != null)
                {
                    if (File.Exists(_cachedExePath)) return _cachedExePath;
                    _cachedExePath = null; // gone in the meantime (e.g. USB unplugged)
                }

                if ((DateTime.UtcNow - _lastSearchUtc).TotalSeconds < SearchRetrySeconds) return null;
                _lastSearchUtc = DateTime.UtcNow;

                _cachedExePath = SearchExecutable();

                if (_cachedExePath != null && !_loggedFound)
                {
                    _loggedFound = true;
                    AppLogger.Info("smartctl: found at {Path}", _cachedExePath);
                }
                else if (_cachedExePath == null && !_loggedMissing)
                {
                    _loggedMissing = true;
                    AppLogger.Info("smartctl: NEGASIT (cautat in {Dir}\\Tools, in PATH si in Program Files\\smartmontools) — " +
                                   "SMART stays limited to WMI (not supported on NVMe), no self-test and no live temperature", AppPaths.BundleDirectory);
                }
                return _cachedExePath; // null → the caller must fall back to WMI (the existing path)
            }
        }

        private static string? SearchExecutable()
        {
            try
            {
                // 1) smartctl inclus in exe (extras in AppPaths.BundleDirectory)
                string bundled = Path.Combine(AppPaths.BundleDirectory, "Tools", "smartctl.exe");
                if (File.Exists(bundled)) return bundled;
                // 2) Tools\smartctl.exe placed manually next to the exe
                string besideExe = Path.Combine(AppPaths.ExeDirectory, "Tools", "smartctl.exe");
                if (File.Exists(besideExe)) return besideExe;

                // fallback: PATH
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(Path.PathSeparator))
                    {
                        try
                        {
                            string candidate = Path.Combine(dir, "smartctl.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch (Exception ex) { AppLogger.Debug("SmartctlService.SearchExecutable: invalid PATH entry {Dir}: {Msg}", dir, ex.Message); }
                    }
                }

                // fallback: the default smartmontools install location
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string defaultInstall = Path.Combine(pf, "smartmontools", "bin", "smartctl.exe");
                if (File.Exists(defaultInstall)) return defaultInstall;
            }
            catch (Exception ex) { AppLogger.Debug("SmartctlService.SearchExecutable: {Msg}", ex.Message); }

            return null;
        }

        public static bool IsAvailable => FindExecutable() != null;

        // ── Coordonare per disc ───────────────────────────────────────────────
        private sealed class DeviceGate
        {
            public readonly SemaphoreSlim Lock = new(1, 1);
            public SmartctlReport? LastReport;
            public DateTime LastReportUtc = DateTime.MinValue;
            public DateTime CooldownUntilUtc = DateTime.MinValue;
            public bool KnownNvme;
            public string? ResolvedPath;
            public string? DeviceType;
            public DateTime UnsupportedUntilUtc = DateTime.MinValue;
            public string UnsupportedReason = "";
        }

        private static readonly ConcurrentDictionary<string, DeviceGate> _gates =
            new(StringComparer.OrdinalIgnoreCase);

        private static DeviceGate GateFor(string devicePath) =>
            _gates.GetOrAdd(devicePath ?? "", _ => new DeviceGate());

        private static bool TryGetCached(DeviceGate gate, out SmartctlReport? report)
        {
            lock (gate)
            {
                if (gate.LastReport != null &&
                    (DateTime.UtcNow - gate.LastReportUtc).TotalSeconds < ReportCacheSeconds)
                {
                    report = gate.LastReport;
                    return true;
                }
            }
            report = null;
            return false;
        }

        private static void InvalidateCache(DeviceGate gate)
        {
            lock (gate) { gate.LastReport = null; }
        }

        private static SmartctlReport Fail(string message) =>
            new SmartctlReport { Success = false, ErrorMessage = message };

        private static readonly System.Text.RegularExpressions.Regex _rxPhysDrive = new(
            @"^\\\\\.\\PHYSICALDRIVE(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private const int UnsupportedRetryMinutes = 10;

        /// <summary>"\\.\PHYSICALDRIVE0" → "\\.\PhysicalDrive0" (the form smartctl accepts).</summary>
        internal static string NormalizeDevicePath(string? path)
        {
            string p = (path ?? "").Trim();
            var m = _rxPhysDrive.Match(p);
            return m.Success ? $@"\\.\PhysicalDrive{m.Groups[1].Value}" : p;
        }

        private static IEnumerable<(string Path, string? Type)> TargetCandidates(string normalized)
        {
            yield return (normalized, null);
            yield return (normalized, "sat");
            // USB enclosures that hide SMART behind their bridge chip: the NVMe-to-USB bridges (JMicron JMS583, ASMedia ASM2362,
            // Realtek RTL9210) and old SATA bridges that only accept the short command form. Tried only after the usual forms
            // failed, so normal disks never get these commands.
            foreach (var bridge in new[] { "sntjmicron", "sntasmedia", "sntrealtek", "sat,12" })
                yield return (normalized, bridge);
            var m = _rxPhysDrive.Match(normalized);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            {
                yield return ($"/dev/pd{n}", null);
                yield return ($"/dev/pd{n}", "sat");
                if (n < 26)
                {
                    string sd = $"/dev/sd{(char)('a' + n)}";
                    yield return (sd, null);
                    yield return (sd, "sat");
                }
            }
        }

        private static string TargetArgs(DeviceGate gate) =>
            (gate.DeviceType != null ? $"-d {gate.DeviceType} " : "") + $"\"{gate.ResolvedPath}\"";

        /// <summary>Called with the disk lock held. true = there is a working addressing form.</summary>
        private static async Task<bool> EnsureTargetAsync(string exe, string devicePath, DeviceGate gate)
        {
            if (gate.ResolvedPath != null) return true;
            if (DateTime.UtcNow < gate.UnsupportedUntilUtc) return false;

            string lastMsg = "";
            bool permissionProblem = false, timedOut = false;
            foreach (var (path, type) in TargetCandidates(NormalizeDevicePath(devicePath)))
            {
                string args = $"-i -j {(type != null ? "-d " + type + " " : "")}\"{path}\"";
                try
                {
                    var (stdout, stderr, exit) = await RunProcessAsync(exe, args, TempTimeoutMs).ConfigureAwait(false);
                    // Keep the FIRST reason (auto-detection), not the one from the last form tried — the fallback forms (/dev/sdX, etc.)
                    // often give less useful messages ("No such device").
                    if (string.IsNullOrWhiteSpace(stdout)) { if (lastMsg.Length == 0) lastMsg = FirstLine(stderr); continue; }

                    var probe = new SmartctlReport { ExitStatus = exit };
                    try { ParseJson(stdout, probe); }
                    catch (JsonException) { continue; }

                    // A sleeping (standby) disk gives no identity, but the addressing form is valid: smartctl recognised it.
                    if (probe.IsNvme || probe.InStandby || !string.IsNullOrEmpty(probe.ModelName) || !string.IsNullOrEmpty(probe.SerialNumber))
                    {
                        gate.ResolvedPath = path; gate.DeviceType = type; gate.KnownNvme = probe.IsNvme;
                        gate.UnsupportedReason = "";
                        AppLogger.Info("smartctl: {Dev} is read as {Path}{Type}", devicePath, path, type != null ? " (-d " + type + ")" : "");
                        return true;
                    }
                    if (probe.Messages.Count > 0 && lastMsg.Length == 0) lastMsg = string.Join(" ", probe.Messages.Take(2));
                    if (lastMsg.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) { permissionProblem = true; break; }
                }
                catch (TimeoutException ex)
                {
                    lastMsg = ex.Message; timedOut = true;
                    gate.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(CooldownSeconds);
                    break;
                }
                catch (Exception ex) { lastMsg = ex.Message; break; }
            }

            gate.UnsupportedReason = string.IsNullOrWhiteSpace(lastMsg) ? "smartctl could not identify this device" : lastMsg;
            // permissions / timeout: retry soon; otherwise (no form works) — 10 minutes
            gate.UnsupportedUntilUtc = DateTime.UtcNow.AddMinutes(permissionProblem || timedOut ? 1 : UnsupportedRetryMinutes);
            AppLogger.Info("smartctl: {Dev} cannot be read ({Reason}) — not retried for {Min} min (Refresh drives retries)",
                devicePath, gate.UnsupportedReason, permissionProblem || timedOut ? 1 : UnsupportedRetryMinutes);
            return false;
        }

        private static string UnsupportedMessage(DeviceGate gate) =>
            "SMART is not available for this device — smartctl: " +
            (string.IsNullOrWhiteSpace(gate.UnsupportedReason) ? "device not recognized" : gate.UnsupportedReason);

        /// <summary>true if smartctl tried the disk and cannot read it (USB flash memory, controller without SMART, etc.).</summary>
        public static bool IsDeviceUnsupported(string? devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath)) return false;
            return _gates.TryGetValue(devicePath, out var g) && g.ResolvedPath == null && DateTime.UtcNow < g.UnsupportedUntilUtc;
        }

        /// <summary>Reason (in English, from smartctl) why the disk cannot be read; "" if not applicable.</summary>
        public static string GetUnsupportedReason(string? devicePath) =>
            IsDeviceUnsupported(devicePath) && _gates.TryGetValue(devicePath!, out var g) ? g.UnsupportedReason : "";

        /// <summary>"Refresh drives": forgets the discovered addressing and the "cannot be read" states, so the disks are
        /// detected again (e.g. an external disk connected meanwhile, or a changed controller mode).</summary>
        public static void ForgetDeviceStates()
        {
            foreach (var g in _gates.Values)
            {
                g.ResolvedPath = null; g.DeviceType = null; g.KnownNvme = false;
                g.UnsupportedUntilUtc = DateTime.MinValue; g.UnsupportedReason = "";
                g.CooldownUntilUtc = DateTime.MinValue;
                lock (g) { g.LastReport = null; }
            }
        }

        // ── Full read ───────────────────────────────────────────────────

        /// <summary>
        /// Runs "smartctl -a -j" on a disk and parses the JSON output.
        /// devicePath: e.g. "\\.\PhysicalDrive0" (Windows) — comes from DiskHealthEntry.PhysicalDevicePath.
        /// The returned report may be shared between callers (10 s memory) — it is read, not modified. forceFresh=true bypasses
        /// the memory and the pause after a timeout (for actions explicitly requested by the user).
        /// </summary>
        public static async Task<SmartctlReport> ReadAsync(string devicePath, bool forceFresh = false)
        {
            string? exe = FindExecutable();
            if (exe == null)
                return Fail("smartctl.exe not found (expected in Tools\\smartctl.exe next to the app, or in PATH).");
            if (string.IsNullOrWhiteSpace(devicePath))
                return Fail("No device path for this disk.");

            var gate = GateFor(devicePath);
            if (gate.ResolvedPath == null && DateTime.UtcNow < gate.UnsupportedUntilUtc) return Fail(UnsupportedMessage(gate));

            if (!forceFresh)
            {
                if (TryGetCached(gate, out var cachedFast)) return cachedFast!;
                if (DateTime.UtcNow < gate.CooldownUntilUtc)
                    return Fail("Disk did not respond recently — will retry shortly.");
            }

            await gate.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                // someone else may have read it meanwhile, while we waited for the lock
                if (!forceFresh && TryGetCached(gate, out var cachedLate)) return cachedLate!;

                var report = await ReadCoreAsync(exe, devicePath, gate).ConfigureAwait(false);
                if (report.Success)
                {
                    lock (gate)
                    {
                        gate.LastReport = report;
                        gate.LastReportUtc = DateTime.UtcNow;
                    }
                }
                return report;
            }
            finally { gate.Lock.Release(); }
        }

        private static async Task<SmartctlReport> ReadCoreAsync(string exe, string devicePath, DeviceGate gate)
        {
            if (!await EnsureTargetAsync(exe, devicePath, gate).ConfigureAwait(false))
                return Fail(UnsupportedMessage(gate));

            string stdout = "", stderr = "";
            int exit = 0;
            try
            {
                (stdout, stderr, exit) = await RunProcessAsync(exe, $"-a -j {TargetArgs(gate)}", ReadTimeoutMs)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                gate.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(CooldownSeconds);
                return Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail($"Failed to run smartctl: {ex.Message}");
            }

            var report = new SmartctlReport { RawJson = stdout, ExitStatus = exit };

            if (string.IsNullOrWhiteSpace(stdout))
            {
                report.ErrorMessage = !string.IsNullOrWhiteSpace(stderr)
                    ? FirstLine(stderr)
                    : $"smartctl returned no output (exit status {exit}).";
                return report;
            }

            try { ParseJson(stdout, report); }
            catch (JsonException ex)
            {
                report.ErrorMessage = $"Failed to parse smartctl output: {ex.Message}";
                return report;
            }

            // smartctl returns valid JSON even when it could not open the disk (permissions, undetected device type, standby) —
            // then there is no real data.
            bool hasData = !string.IsNullOrEmpty(report.ModelName)
                        || !string.IsNullOrEmpty(report.SerialNumber)
                        || report.AtaAttributes.Count > 0
                        || report.TemperatureCelsius.HasValue
                        || report.NvmePercentageUsed.HasValue
                        || report.NvmeAvailableSparePct.HasValue
                        || report.NvmeDataUnitsWrittenGB.HasValue
                        || report.PowerOnHours >= 0;

            report.Success = hasData;
            if (!hasData) report.ErrorMessage = BuildErrorMessage(report, stderr);
            else if (report.IsNvme) gate.KnownNvme = true;
            return report;
        }

        private static string BuildErrorMessage(SmartctlReport report, string stderr)
        {
            if (report.InStandby) return "Disk is in standby — not woken up.";
            if (report.Messages.Count > 0) return string.Join(" ", report.Messages.Take(2));
            if (!string.IsNullOrWhiteSpace(stderr)) return FirstLine(stderr);
            return $"smartctl returned no data (exit status {report.ExitStatus}).";
        }

        // ── Light temperature read ──────────────────────────────────────

        /// <summary>
        /// Reads ONLY the temperature (smartctl -A). On HDDs it uses -n standby: if the disk is asleep, smartctl exits without
        /// waking it (InStandby=true, no value) — unlike "-a", which would keep mechanical disks always spinning when called
        /// periodically. If there is a full read younger than 10 s, it is reused.
        /// </summary>
        public static async Task<SmartctlTempReading> ReadTemperatureAsync(string devicePath, bool wakeIfStandby = false)
        {
            var result = new SmartctlTempReading();
            string? exe = FindExecutable();
            if (exe == null) { result.ErrorMessage = "smartctl.exe not found."; return result; }
            if (string.IsNullOrWhiteSpace(devicePath)) { result.ErrorMessage = "No device path for this disk."; return result; }

            var gate = GateFor(devicePath);

            if (TryGetCached(gate, out var cached) && cached!.TemperatureCelsius.HasValue)
            {
                result.Celsius = cached.TemperatureCelsius;
                return result;
            }
            if (DateTime.UtcNow < gate.CooldownUntilUtc)
            {
                result.ErrorMessage = "Disk did not respond recently — will retry shortly.";
                return result;
            }
            if (gate.ResolvedPath == null && DateTime.UtcNow < gate.UnsupportedUntilUtc)
            {
                result.ErrorMessage = UnsupportedMessage(gate);
                return result;
            }

            await gate.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryGetCached(gate, out var cachedLate) && cachedLate!.TemperatureCelsius.HasValue)
                {
                    result.Celsius = cachedLate.TemperatureCelsius;
                    return result;
                }
                if (!await EnsureTargetAsync(exe, devicePath, gate).ConfigureAwait(false))
                {
                    result.ErrorMessage = UnsupportedMessage(gate);
                    return result;
                }

                bool useStandbyFlag = !gate.KnownNvme && !wakeIfStandby;
                string args = useStandbyFlag
                    ? $"-A -j -n standby {TargetArgs(gate)}"
                    : $"-A -j {TargetArgs(gate)}";

                var reading = await ReadTempCoreAsync(exe, args, gate).ConfigureAwait(false);

                // -n may be rejected on some device types (e.g. NVMe) — a single retry without it, only if the disk is NOT reported asleep.
                if (useStandbyFlag && !reading.Celsius.HasValue && !reading.InStandby
                    && DateTime.UtcNow >= gate.CooldownUntilUtc)
                {
                    reading = await ReadTempCoreAsync(exe, $"-A -j {TargetArgs(gate)}", gate).ConfigureAwait(false);
                }
                return reading;
            }
            finally { gate.Lock.Release(); }
        }

        private static async Task<SmartctlTempReading> ReadTempCoreAsync(string exe, string args, DeviceGate gate)
        {
            var result = new SmartctlTempReading();
            try
            {
                var (stdout, stderr, exit) = await RunProcessAsync(exe, args, TempTimeoutMs).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    if (stderr.Contains("STANDBY", StringComparison.OrdinalIgnoreCase)) result.InStandby = true;
                    else result.ErrorMessage = !string.IsNullOrWhiteSpace(stderr)
                        ? FirstLine(stderr)
                        : $"smartctl returned no output (exit status {exit}).";
                    return result;
                }

                var scratch = new SmartctlReport { ExitStatus = exit };
                ParseJson(stdout, scratch);
                if (scratch.IsNvme) gate.KnownNvme = true;

                result.Celsius = scratch.TemperatureCelsius;
                result.InStandby = scratch.InStandby;
                if (!result.Celsius.HasValue && !result.InStandby)
                    result.ErrorMessage = scratch.Messages.Count > 0
                        ? string.Join(" ", scratch.Messages.Take(2))
                        : "No temperature reported by this disk.";
            }
            catch (TimeoutException ex)
            {
                gate.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(CooldownSeconds);
                result.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        // ── APM (Advanced Power Management) ───────────────────────────────────

        /// <summary>The APM level cannot be read / is not supported by the disk.</summary>
        public const int ApmUnknown = -1;
        /// <summary>The disk is asleep — it was not woken, so there is no value.</summary>
        public const int ApmStandby = -2;

        /// <summary>
        /// Reads the real APM level of an ATA disk (smartctl -g apm), 1–254. Returns ApmUnknown if the disk does not support APM /
        /// is NVMe / smartctl is missing, or ApmStandby if it is asleep (-n standby: the disk is not woken).
        /// </summary>
        public static async Task<int> ReadApmLevelAsync(string devicePath)
        {
            string? exe = FindExecutable();
            if (exe == null || string.IsNullOrWhiteSpace(devicePath)) return ApmUnknown;

            var gate = GateFor(devicePath);
            if (DateTime.UtcNow < gate.CooldownUntilUtc) return ApmUnknown;
            if (gate.ResolvedPath == null && DateTime.UtcNow < gate.UnsupportedUntilUtc) return ApmUnknown;

            await gate.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await EnsureTargetAsync(exe, devicePath, gate).ConfigureAwait(false)) return ApmUnknown;
                var (stdout, stderr, exit) = await RunProcessAsync(exe, $"-n standby -g apm {TargetArgs(gate)}", TempTimeoutMs)
                    .ConfigureAwait(false);
                string all = stdout + "\n" + stderr;

                if (all.Contains("STANDBY", StringComparison.OrdinalIgnoreCase) ||
                    all.Contains("SLEEP", StringComparison.OrdinalIgnoreCase))
                    return ApmStandby;

                // smartctl output is always in English: "APM level is:     254 (maximum performance)"
                var m = System.Text.RegularExpressions.Regex.Match(all, @"APM level is:\s*(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int level) && level >= 1 && level <= 254)
                    return level;
                return ApmUnknown;
            }
            catch (TimeoutException ex)
            {
                gate.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(CooldownSeconds);
                AppLogger.Debug("SmartctlService.ReadApmLevelAsync: {Msg}", ex.Message);
                return ApmUnknown;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SmartctlService.ReadApmLevelAsync: {Msg}", ex.Message);
                return ApmUnknown;
            }
            finally { gate.Lock.Release(); }
        }

        // ── Self-test ─────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a SMART self-test. type: "short" (~2 min) or "long"/"extended" (may take hours).
        /// The test runs ASYNCHRONOUSLY on the disk controller — this function only STARTS it and returns immediately; progress is
        /// read later from SelfTestPercentRemaining (by calling ReadAsync again, periodically). On NVMe, self-test needs smartctl 7.4+.
        /// </summary>
        public static async Task<(bool started, string message)> StartSelfTestAsync(string devicePath, string type)
        {
            string? exe = FindExecutable();
            if (exe == null) return (false, "smartctl.exe not found.");
            if (string.IsNullOrWhiteSpace(devicePath)) return (false, "No device path for this disk.");

            string testArg = type.ToLowerInvariant() switch
            {
                "short" => "short",
                "long" or "extended" => "long",
                _ => "short"
            };

            var gate = GateFor(devicePath);
            await gate.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await EnsureTargetAsync(exe, devicePath, gate).ConfigureAwait(false))
                    return (false, UnsupportedMessage(gate));
                var (stdout, stderr, exit) = await RunProcessAsync(exe, $"-t {testArg} {TargetArgs(gate)}", ReadTimeoutMs)
                    .ConfigureAwait(false);
                InvalidateCache(gate); // starea discului tocmai s-a schimbat
                string all = stdout + "\n" + stderr;

                // smartctl is not localized — the message is always in English
                bool started = all.Contains("has begun", StringComparison.OrdinalIgnoreCase);
                return (started, started ? "Self-test started." : SummarizeOutput(all, exit));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally { gate.Lock.Release(); }
        }

        /// <summary>Stops a running self-test.</summary>
        public static async Task<bool> AbortSelfTestAsync(string devicePath)
        {
            string? exe = FindExecutable();
            if (exe == null || string.IsNullOrWhiteSpace(devicePath)) return false;

            var gate = GateFor(devicePath);
            await gate.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await EnsureTargetAsync(exe, devicePath, gate).ConfigureAwait(false)) return false;
                var (stdout, stderr, exit) = await RunProcessAsync(exe, $"-X {TargetArgs(gate)}", ReadTimeoutMs)
                    .ConfigureAwait(false);
                InvalidateCache(gate);
                string all = stdout + "\n" + stderr;
                return all.Contains("abort", StringComparison.OrdinalIgnoreCase) || exit == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SmartctlService.AbortSelfTestAsync: {Msg}", ex.Message);
                return false;
            }
            finally { gate.Lock.Release(); }
        }

        // ── Rulare proces ─────────────────────────────────────────────────────

        /// <summary>
        /// Runs smartctl and returns (stdout, stderr, exit code). Throws TimeoutException if the process does not finish within
        /// timeoutMs — in which case it is killed with its whole tree.
        /// Elevation is NOT requested here: app.manifest has requireAdministrator, and Verb="runas" is ignored anyway when output
        /// is redirected (UseShellExecute=false). smartctl uses the exit code as a warning bitmask, not just 0=success — so the
        /// exit code is not treated as an error here: the JSON stays useful.
        /// </summary>
        private static async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
            string exePath, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start smartctl process.");

            // Read both streams in parallel, so the process never blocks on a full buffer
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLogger.Debug("SmartctlService: kill after timeout failed: {Msg}", ex.Message); }
                // pending reads finish on their own when the pipes close — no unobserved exceptions
                _ = outTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _ = errTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException($"smartctl did not respond within {timeoutMs / 1000}s.");
            }

            var both = Task.WhenAll(outTask, errTask);
            if (await Task.WhenAny(both, Task.Delay(3000)).ConfigureAwait(false) != both)
                throw new TimeoutException("smartctl output did not close after the process exited.");

            return (await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false), proc.ExitCode);
        }

        private static string FirstLine(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t;
            }
            return text.Trim();
        }

        /// <summary>Trims the smartctl text output to the relevant lines (not the whole banner).</summary>
        private static string SummarizeOutput(string text, int exit)
        {
            string[] markers = { "can't", "cannot", "not supported", "unsupported", "failed", "error", "unable", "denied", "requires" };
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            var relevant = lines.Where(l => markers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
            if (relevant.Count == 0) relevant = lines.TakeLast(2).ToList();
            return relevant.Count > 0 ? string.Join(" ", relevant) : $"smartctl exit status {exit}.";
        }

        // ── Parsare JSON ──────────────────────────────────────────────────────

        private static bool TryProp(JsonElement parent, string name, out JsonElement value)
        {
            value = default;
            return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
        }

        private static long? AsLong(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Number) return null;
            if (e.TryGetInt64(out long l)) return l;
            if (e.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
                return d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
            return null;
        }

        private static int? AsInt(JsonElement e)
        {
            long? l = AsLong(e);
            return l.HasValue ? (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue) : null;
        }

        private static string AsString(JsonElement e) =>
            e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";

        /// <summary>smartctl's "2.5 inches" → 2.5", "M.2" stays; "less than 1.8 inches" and unknown values → "".</summary>
        internal static string NormalizeFormFactor(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.StartsWith("less", StringComparison.OrdinalIgnoreCase)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d(?:\.\d+)?)\s*inch", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + "\"";
            return name.Length <= 12 ? name : "";   // M.2, mSATA, CFast, MicroSSD
        }

        /// <summary>Absurd temperatures (0, or above 120°C — packed fields read wrongly) are ignored instead of reaching the UI.</summary>
        private static double? SaneTemp(double? t) => t.HasValue && t.Value > 0 && t.Value <= 120 ? t : null;

        private static void Section(string name, Action parse)
        {
            try { parse(); }
            catch (Exception ex) { AppLogger.Debug("smartctl parse [{Section}]: {Msg}", name, ex.Message); }
        }

        private static void ParseJson(string json, SmartctlReport report)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Unexpected smartctl JSON (root is not an object).");

            // Each section is isolated: an unexpected value in one no longer cancels the rest.
            Section("messages", () => ParseMessages(root, report));
            Section("identity", () => ParseIdentity(root, report));
            Section("nvme", () => ParseNvme(root, report));
            Section("ata-attributes", () => ParseAtaAttributes(root, report));
            Section("ata-selftest", () => ParseAtaSelfTest(root, report));
            Section("nvme-selftest", () => ParseNvmeSelfTest(root, report));
        }

        private static void ParseMessages(JsonElement root, SmartctlReport report)
        {
            if (!TryProp(root, "smartctl", out var sc)) return;
            if (!TryProp(sc, "messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) return;

            foreach (var m in msgs.EnumerateArray())
            {
                string text = TryProp(m, "string", out var s) ? AsString(s) : "";
                if (text.Length == 0) continue;
                string severity = TryProp(m, "severity", out var sv) ? AsString(sv) : "";

                if (text.Contains("Device is in", StringComparison.OrdinalIgnoreCase) &&
                    (text.Contains("STANDBY", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("SLEEP", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("IDLE", StringComparison.OrdinalIgnoreCase)))
                    report.InStandby = true;

                if (!severity.Equals("information", StringComparison.OrdinalIgnoreCase))
                    report.Messages.Add(text);
            }
        }

        private static void ParseIdentity(JsonElement root, SmartctlReport report)
        {
            if (TryProp(root, "model_name", out var mn)) report.ModelName = AsString(mn);
            else if (TryProp(root, "scsi_model_name", out var smn)) report.ModelName = AsString(smn);
            if (TryProp(root, "logical_block_size", out var lbs) && AsInt(lbs) is int lbsv) report.LogicalBlockSize = lbsv;
            if (TryProp(root, "serial_number", out var sn)) report.SerialNumber = AsString(sn);
            if (TryProp(root, "firmware_version", out var fw)) report.FirmwareVersion = AsString(fw);
            if (TryProp(root, "interface_speed", out var ispd))
            {
                if (TryProp(ispd, "max", out var mx))
                {
                    if (TryProp(mx, "string", out var ms)) report.SataLinkMax = AsString(ms);
                    if (TryProp(mx, "units_per_second", out var mu) && AsInt(mu) is int mui) report.SataLinkMaxTenths = mui;
                }
                if (TryProp(ispd, "current", out var cu))
                {
                    if (TryProp(cu, "string", out var cs)) report.SataLinkCurrent = AsString(cs);
                    if (TryProp(cu, "units_per_second", out var cuu) && AsInt(cuu) is int cui) report.SataLinkCurrentTenths = cui;
                }
            }

            if (TryProp(root, "device", out var dev) && TryProp(dev, "type", out var dt))
                report.IsNvme = AsString(dt).Contains("nvme", StringComparison.OrdinalIgnoreCase);
            if (!report.IsNvme && TryProp(root, "nvme_smart_health_information_log", out _))
                report.IsNvme = true;

            if (TryProp(root, "smart_status", out var ss) && TryProp(ss, "passed", out var passed)
                && (passed.ValueKind == JsonValueKind.True || passed.ValueKind == JsonValueKind.False))
                report.OverallHealthPassed = passed.GetBoolean();

            if (TryProp(root, "power_on_time", out var pot) && TryProp(pot, "hours", out var hrs)
                && AsLong(hrs) is long hours)
                report.PowerOnHours = hours;

            if (TryProp(root, "rotation_rate", out var rr) && AsInt(rr) is int rpm) report.RotationRate = rpm;
            if (TryProp(root, "form_factor", out var ff) && TryProp(ff, "name", out var ffName))
                report.FormFactor = NormalizeFormFactor(AsString(ffName));

            if (TryProp(root, "power_cycle_count", out var pcc) && AsInt(pcc) is int cycles)
                report.PowerCycleCount = cycles;

            if (TryProp(root, "temperature", out var temp) && TryProp(temp, "current", out var tc)
                && tc.ValueKind == JsonValueKind.Number && tc.TryGetDouble(out double tempC))
                report.TemperatureCelsius = SaneTemp(tempC);
        }

        private static void ParseNvme(JsonElement root, SmartctlReport report)
        {
            if (!report.IsNvme || !TryProp(root, "nvme_smart_health_information_log", out var log)) return;

            if (TryProp(log, "percentage_used", out var pu) && AsInt(pu) is int used) report.NvmePercentageUsed = used;
            if (TryProp(log, "available_spare", out var avs) && AsInt(avs) is int spare) report.NvmeAvailableSparePct = spare;
            if (TryProp(log, "data_units_written", out var duw) && duw.ValueKind == JsonValueKind.Number
                && duw.TryGetDouble(out double units))
                // NVMe spec: 1 data unit = 512,000 bytes
                report.NvmeDataUnitsWrittenGB = (long)(units * 512_000.0 / 1_000_000_000.0);
            if (TryProp(log, "media_errors", out var me) && AsLong(me) is long mediaErrors) report.NvmeMediaErrors = mediaErrors;
            if (TryProp(log, "critical_warning", out var cw) && AsInt(cw) is int warn) report.NvmeCriticalWarning = warn;

            if (TryProp(log, "available_spare_threshold", out var ast) && AsInt(ast) is int spareThr) report.NvmeAvailableSpareThreshold = spareThr;
            if (TryProp(log, "data_units_read", out var dur) && dur.ValueKind == JsonValueKind.Number && dur.TryGetDouble(out double unitsRead))
                report.NvmeDataUnitsReadGB = (long)(unitsRead * 512_000.0 / 1_000_000_000.0);
            if (TryProp(log, "host_reads", out var hr) && AsLong(hr) is long hostReads) report.NvmeHostReads = hostReads;
            if (TryProp(log, "host_writes", out var hw) && AsLong(hw) is long hostWrites) report.NvmeHostWrites = hostWrites;
            if (TryProp(log, "controller_busy_time", out var cbt) && AsLong(cbt) is long busy) report.NvmeControllerBusyMinutes = busy;
            if (TryProp(log, "unsafe_shutdowns", out var us) && AsLong(us) is long unsafeSd) report.NvmeUnsafeShutdowns = unsafeSd;
            if (TryProp(log, "num_err_log_entries", out var nerr) && AsLong(nerr) is long errEntries) report.NvmeErrorLogEntries = errEntries;
            if (TryProp(log, "warning_temp_time", out var wtt) && AsLong(wtt) is long warnTime) report.NvmeWarningTempMinutes = warnTime;
            if (TryProp(log, "critical_comp_time", out var cct) && AsLong(cct) is long critTime) report.NvmeCriticalTempMinutes = critTime;
            if (TryProp(log, "thermal_temp1_transition_count", out var t1c) && AsLong(t1c) is long t1) report.NvmeThrottleLightCount = t1;
            if (TryProp(log, "thermal_temp2_transition_count", out var t2c) && AsLong(t2c) is long t2) report.NvmeThrottleHeavyCount = t2;
            if (TryProp(log, "thermal_temp1_total_time", out var t1t) && AsLong(t1t) is long t1s) report.NvmeThrottleLightSeconds = t1s;
            if (TryProp(log, "thermal_temp2_total_time", out var t2t) && AsLong(t2t) is long t2s) report.NvmeThrottleHeavySeconds = t2s;

            // Fallbacks if the root fields are missing (older smartctl versions)
            if (!report.TemperatureCelsius.HasValue && TryProp(log, "temperature", out var lt)
                && lt.ValueKind == JsonValueKind.Number && lt.TryGetDouble(out double logTemp))
                report.TemperatureCelsius = SaneTemp(logTemp);
            if (report.PowerOnHours < 0 && TryProp(log, "power_on_hours", out var poh) && AsLong(poh) is long pohv)
                report.PowerOnHours = pohv;
            if (report.PowerCycleCount < 0 && TryProp(log, "power_cycles", out var pc) && AsInt(pc) is int pcv)
                report.PowerCycleCount = pcv;
        }

        private static void ParseAtaAttributes(JsonElement root, SmartctlReport report)
        {
            if (report.IsNvme) return;
            if (!TryProp(root, "ata_smart_attributes", out var ataAttrs)
                || !TryProp(ataAttrs, "table", out var table)
                || table.ValueKind != JsonValueKind.Array) return;

            foreach (var attr in table.EnumerateArray())
            {
                var a = new SmartctlAttribute();
                if (TryProp(attr, "id", out var id)) a.Id = AsInt(id) ?? 0;
                if (TryProp(attr, "name", out var name)) a.Name = AsString(name);
                if (TryProp(attr, "value", out var val)) a.Value = AsInt(val) ?? 0;
                if (TryProp(attr, "worst", out var worst)) a.Worst = AsInt(worst) ?? 0;
                if (TryProp(attr, "thresh", out var thresh)) a.Threshold = AsInt(thresh) ?? 0;
                if (TryProp(attr, "raw", out var raw))
                {
                    if (TryProp(raw, "value", out var rv)) a.RawValue = AsLong(rv) ?? 0;
                    if (TryProp(raw, "string", out var rs)) a.RawString = AsString(rs);
                    // Some makers pack several counters in the 48-bit raw value (Seagate: Power-On Hours "64530 (233 172 0)",
                    // Command Timeout "4 4 4" = 0x400040004): smartctl's text starts with the real number, the raw value read as one
                    // number gave 213,708,982,778,898 hours. The leading number of the text wins when it is smaller.
                    var lead = System.Text.RegularExpressions.Regex.Match(a.RawString ?? "", @"^\s*(\d+)");
                    if (lead.Success && long.TryParse(lead.Groups[1].Value, out long decoded) && decoded < a.RawValue)
                        a.RawValue = decoded;
                }
                if (TryProp(attr, "when_failed", out var wf))
                {
                    string w = AsString(wf);
                    a.WhenFailed = !string.IsNullOrEmpty(w) && w != "-";
                }
                if (a.Id > 0) report.AtaAttributes.Add(a);
            }

            // Fallback: some versions do not put the temperature at the root — take it from 194/190
            // (the least significant byte = °C; the rest of the field may be min/max).
            if (!report.TemperatureCelsius.HasValue)
            {
                var tempAttr = report.AtaAttributes.FirstOrDefault(x => x.Id == 194)
                            ?? report.AtaAttributes.FirstOrDefault(x => x.Id == 190);
                if (tempAttr != null) report.TemperatureCelsius = SaneTemp(tempAttr.RawValue & 0xFF);
            }
        }

        private static void ParseAtaSelfTest(JsonElement root, SmartctlReport report)
        {
            if (report.IsNvme) return;

            if (TryProp(root, "ata_smart_self_test_log", out var selfTestLog))
            {
                // "standard" = the classic log; "extended" = the GP log, on newer disks
                JsonElement table = default;
                bool found = (TryProp(selfTestLog, "standard", out var std) && TryProp(std, "table", out table))
                          || (TryProp(selfTestLog, "extended", out var ext) && TryProp(ext, "table", out table));
                if (found && table.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in table.EnumerateArray())
                    {
                        var e = new SmartctlSelfTestEntry();
                        if (TryProp(t, "type", out var ty) && TryProp(ty, "string", out var tys)) e.Type = AsString(tys);
                        if (TryProp(t, "status", out var stt) && TryProp(stt, "string", out var sts)) e.Status = AsString(sts);
                        if (TryProp(t, "lifetime_hours", out var lh)) e.LifetimeHours = AsLong(lh) ?? 0;
                        report.SelfTestLog.Add(e);
                    }
                }
            }

            // Self-test in progress. status.value: the high nibble 0xF = "in progress" (ATA spec); if the value is missing, rely on the text.
            if (TryProp(root, "ata_smart_data", out var smartData)
                && TryProp(smartData, "self_test", out var selfTest)
                && TryProp(selfTest, "status", out var stStatus))
            {
                long? code = TryProp(stStatus, "value", out var sv) ? AsLong(sv) : null;
                string text = TryProp(stStatus, "string", out var ss) ? AsString(ss) : "";

                report.SelfTestInProgress = code.HasValue
                    ? (code.Value >> 4) == 0xF
                    : text.Contains("progress", StringComparison.OrdinalIgnoreCase);

                if (TryProp(stStatus, "remaining_percent", out var rp) && AsInt(rp) is int remaining)
                {
                    report.SelfTestPercentRemaining = Math.Clamp(remaining, 0, 100);
                    report.SelfTestPercentKnown = true;
                }
            }
        }

        private static void ParseNvmeSelfTest(JsonElement root, SmartctlReport report)
        {
            if (!TryProp(root, "nvme_self_test_log", out var log)) return;

            // current_self_test_operation.value: 0 = no test; 1 = short; 2 = extended; 0xE = vendor
            if (TryProp(log, "current_self_test_operation", out var op))
            {
                long? v = TryProp(op, "value", out var ov) ? AsLong(ov) : null;
                report.SelfTestInProgress = v.HasValue && v.Value != 0;

                // the percentage may be next to the operation or inside it, depending on the version
                JsonElement pctEl = default;
                bool hasPct = TryProp(log, "current_self_test_completion_percent", out pctEl)
                           || TryProp(op, "completion_percent", out pctEl);
                if (report.SelfTestInProgress && hasPct && AsInt(pctEl) is int done)
                {
                    // NVMe reports the percentage DONE; our model keeps "remaining"
                    report.SelfTestPercentRemaining = 100 - Math.Clamp(done, 0, 100);
                    report.SelfTestPercentKnown = true;
                }
            }

            if (TryProp(log, "table", out var table) && table.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in table.EnumerateArray())
                {
                    var e = new SmartctlSelfTestEntry();
                    if (TryProp(t, "self_test_code", out var code) && TryProp(code, "string", out var cs)) e.Type = AsString(cs);
                    if (TryProp(t, "self_test_result", out var res) && TryProp(res, "string", out var rs)) e.Status = AsString(rs);
                    if (TryProp(t, "power_on_hours", out var poh)) e.LifetimeHours = AsLong(poh) ?? 0;
                    report.SelfTestLog.Add(e);
                }
            }
        }
    }
}
