using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    // ── Models ────────────────────────────────────────────────────────────────

    public class TrimStatus
    {
        public string DriveLetter  { get; set; } = "";
        public bool   TrimEnabled  { get; set; }
        public string Description  { get; set; } = "";
        public bool   IsNtfs       { get; set; }
        public bool   IsSsd        { get; set; }
    }

    public class PartitionHealthResult
    {
        public string DriveLetter  { get; set; } = "";
        public string Status       { get; set; } = ""; // "Healthy", "Issues Found", "Scan Failed"
        public string Details      { get; set; } = "";
        public bool   HasIssues    { get; set; }
        public bool   IsRunning    { get; set; }
    }

    public class ApmInfo
    {
        public string DriveLetter   { get; set; } = "";
        public string PhysPath      { get; set; } = "";
        public int    CurrentLevel  { get; set; } = -1;  // 1-254, -1 = not supported
        public bool   IsSupported   { get; set; }
        public string LevelLabel    { get; set; } = "";
    }

    // ── Service ───────────────────────────────────────────────────────────────

    public static class DiskAdvancedService
    {
        // ── TRIM Status ───────────────────────────────────────────────────────

        /// <summary>
        /// Checks TRIM status for all NTFS drives using fsutil.
        /// Requires no elevation for read; manual TRIM (retrim) needs admin.
        /// </summary>
        public static async Task<List<TrimStatus>> GetTrimStatusAsync(List<DiskHealthEntry>? disks = null)
        {
            var results = new List<TrimStatus>();

            var fsSettings = await QueryDeleteNotifyAsync().ConfigureAwait(false);

            await Task.Run(() =>
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Fixed) continue;

                    // letter = "C:" (no backslash — avoids the duplicate "C: C:\")
                    string letter = di.Name.TrimEnd('\\', '/').TrimEnd();
                    if (letter.Length >= 2 && letter[1] == ':')
                        letter = letter.Substring(0, 2).ToUpper(); // "C:"
                    string fs = di.DriveFormat ?? "";
                    bool isNtfs = fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
                    bool isRefs = fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);

                    var ts = new TrimStatus { DriveLetter = letter, IsNtfs = isNtfs };

                    if (!isNtfs && !isRefs)
                    {
                        ts.Description = $"{(fs.Length > 0 ? fs.ToUpper() : "?")} — TRIM not applicable";
                        ts.TrimEnabled = false;
                        results.Add(ts);
                        continue;
                    }

                    var disk = FindDiskForLetter(disks, letter);
                    ts.IsSsd = disk == null || !disk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase);
                    if (disk != null && !ts.IsSsd)
                    {
                        ts.Description = "TRIM not applicable — mechanical drive (HDD)";
                        ts.TrimEnabled = false;
                        results.Add(ts);
                        continue;
                    }

                    string key = isRefs ? "REFS" : "NTFS";
                    if (!fsSettings.TryGetValue(key, out bool disabled) && !fsSettings.TryGetValue("", out disabled))
                    {
                        ts.Description = $"{letter} — Cannot query TRIM";
                        results.Add(ts);
                        continue;
                    }

                    // DisableDeleteNotify = 0  →  TRIM enabled;  = 1  →  TRIM disabled
                    ts.TrimEnabled = !disabled;
                    ts.Description = ts.TrimEnabled
                        ? "TRIM enabled ✔"
                        : $"TRIM disabled ✘ — run: fsutil behavior set DisableDeleteNotify {(isRefs ? "ReFS " : "NTFS ")}0";
                    results.Add(ts);
                }
            }).ConfigureAwait(false);

            return results;
        }

        /// <summary>Reads DisableDeleteNotify per file system. Key "NTFS"/"REFS", or "" for the old format
        /// (a single line with no file system name). Value = true if disabled.</summary>
        private static async Task<Dictionary<string, bool>> QueryDeleteNotifyAsync()
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var r = await ProcessRunner.RunAsync("fsutil", "behavior query DisableDeleteNotify", 5000).ConfigureAwait(false);
                if (r.TimedOut) { AppLogger.Warning("DiskAdvancedService: fsutil DisableDeleteNotify timed out"); return map; }

                // "NTFS DisableDeleteNotify = 0  (…localized text…)" or the old "DisableDeleteNotify = 0".
                // Only the digit after "=" matters — the rest of the line is localized.
                var rx = new System.Text.RegularExpressions.Regex(
                    @"^\s*(?:(NTFS|ReFS)\s+)?DisableDeleteNotify\s*=\s*(\d)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(r.Stdout))
                    map[m.Groups[1].Success ? m.Groups[1].Value : ""] = m.Groups[2].Value != "0";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DiskAdvancedService.QueryDeleteNotify"); }
            return map;
        }

        private static DiskHealthEntry? FindDiskForLetter(List<DiskHealthEntry>? disks, string letter)
        {
            if (disks == null) return null;
            string want = letter.TrimEnd('\\', ':').ToUpperInvariant();
            foreach (var d in disks)
                foreach (var p in d.Partitions)
                    if ((p.Letter ?? "").TrimEnd('\\', ':').Equals(want, StringComparison.OrdinalIgnoreCase))
                        return d;
            return null;
        }

        /// <summary>
        /// Runs Optimize-Volume -ReTrim on the given drive letter (manual TRIM).
        /// Requires Administrator.
        /// </summary>
        public static async Task<(bool success, string message)> RunManualTrimAsync(string driveLetter)
        {
            try
            {
                string letter = driveLetter.TrimEnd('\\', ':');
                // The letter goes into a PowerShell command — strictly a single letter is accepted.
                if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
                    return (false, $"Invalid drive letter: '{driveLetter}'");

                var r = await ProcessRunner.RunAsync("powershell",
                    $"-NoProfile -NonInteractive -Command \"Optimize-Volume -DriveLetter {letter} -ReTrim -Verbose\"",
                    180_000).ConfigureAwait(false);

                if (r.TimedOut) return (false, $"TRIM on {letter}: did not finish within 3 minutes and was stopped.");
                if (r.ExitCode != 0 || r.Stderr.Contains("error", StringComparison.OrdinalIgnoreCase))
                    return (false, $"TRIM failed: {(string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout.Trim() : r.Stderr.Trim())}");

                return (true, $"TRIM completed on {letter}:\\. {r.Stdout.Trim()}");
            }
            catch (Exception ex)
            {
                return (false, $"Cannot run TRIM: {ex.Message}");
            }
        }

        // ── Partition Health (chkdsk /scan) ───────────────────────────────────

        /// <summary>
        /// Runs chkdsk /scan on the given drive — non-destructive online scan.
        /// Does NOT require reboot. Reports issues found without fixing them.
        /// </summary>
        public static async Task<PartitionHealthResult> ScanPartitionHealthAsync(
            string driveLetter,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            string letter = driveLetter.TrimEnd('\\', ':');
            var result = new PartitionHealthResult
            {
                DriveLetter = letter + ":",
                IsRunning   = true
            };

            progress?.Report($"Scanning {letter}:\\ — this may take a few minutes...");

            try
            {
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo("chkdsk",
                        $"{letter}: /scan")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };

                    var output  = new System.Text.StringBuilder();
                    var errOut  = new System.Text.StringBuilder();

                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        result.Status  = "Scan Failed";
                        result.Details = "Could not start chkdsk.";
                        return;
                    }

                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        output.AppendLine(e.Data);
                        progress?.Report(e.Data);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) errOut.AppendLine(e.Data);
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    using var cancelReg = ct.Register(() =>
                    {
                        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                        catch (Exception ex) { AppLogger.Debug("chkdsk cancel kill: {Msg}", ex.Message); }
                    });

                    // Wait up to 10 minutes
                    bool finished = proc.WaitForExit(600_000);
                    if (!finished)
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch (Exception ex) { AppLogger.Debug("chkdsk timeout kill: {Msg}", ex.Message); }
                        proc.WaitForExit(5000);
                    }
                    else
                    {
                        // WaitForExit(timeout) does not guarantee that the async output events have been drained
                        proc.WaitForExit();
                    }

                    if (ct.IsCancellationRequested)
                    {
                        result.Status  = "Cancelled";
                        result.Details = "Scan cancelled by user.";
                        return;
                    }

                    string full = output.ToString();

                    if (!finished)
                    {
                        result.HasIssues = false;
                        result.Status    = "Timed Out";
                        result.Details   = string.Join("\n", full.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(8));
                        return;
                    }

                    // Parse chkdsk output
                    bool hasIssues = full.Contains("errors found", StringComparison.OrdinalIgnoreCase)
                                  || full.Contains("Windows has made corrections", StringComparison.OrdinalIgnoreCase)
                                  || full.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
                                  || proc.ExitCode == 1 || proc.ExitCode == 2;

                    bool clean = full.Contains("Windows has scanned the file system and found no problems",
                                    StringComparison.OrdinalIgnoreCase)
                              || proc.ExitCode == 0;

                    result.HasIssues = hasIssues;
                    result.Status    = hasIssues       ? "Issues Found"
                                     : clean           ? "Healthy"
                                     :                   "Scan Complete";

                    // Extract last meaningful lines for summary
                    var lines = full.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .TakeLast(8)
                        .ToArray();
                    result.Details = string.Join("\n", lines);

                }, ct);
            }
            catch (OperationCanceledException)
            {
                result.Status  = "Cancelled";
                result.Details = "Scan cancelled by user.";
            }
            catch (Exception ex)
            {
                result.Status  = "Scan Failed";
                result.Details = ex.Message;
                AppLogger.Warning(ex, "DiskAdvancedService.ScanPartitionHealth");
            }
            finally
            {
                result.IsRunning = false;
            }

            return result;
        }

        // ── APM (Advanced Power Management) ──────────────────────────────────

        public static async Task<List<ApmInfo>> GetApmInfoAsync(List<DiskHealthEntry> disks)
        {
            var results = new List<ApmInfo>();

            foreach (var disk in disks)
            {
                bool isHdd = !disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                          && !disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
                if (!isHdd) continue;

                var info = new ApmInfo { PhysPath = disk.PhysicalDevicePath };

                // Get drive letter from first partition
                var firstPart = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.Letter));
                info.DriveLetter = firstPart?.Letter?.TrimEnd('\\') ?? disk.PhysicalDevicePath;

                try
                {
                    int apmLevel = SmartctlService.IsAvailable
                        ? await SmartctlService.ReadApmLevelAsync(disk.PhysicalDevicePath).ConfigureAwait(false)
                        : SmartctlService.ApmUnknown;

                    if (apmLevel >= 1)
                    {
                        info.IsSupported  = true;
                        info.CurrentLevel = apmLevel;
                        info.LevelLabel   = ApmLevelLabel(apmLevel);
                    }
                    else if (apmLevel == SmartctlService.ApmStandby)
                    {
                        info.IsSupported = false;
                        info.LevelLabel  = "APM: drive is in standby — not woken up to read it";
                    }
                    else
                    {
                        info.IsSupported = false;
                        info.LevelLabel  = "APM: N/A — not supported by this drive, or smartctl unavailable";
                    }
                }
                catch (Exception ex)
                {
                    info.IsSupported = false;
                    info.LevelLabel  = $"APM read error: {ex.Message}";
                    AppLogger.Warning(ex, "DiskAdvancedService.GetApmInfo");
                }

                results.Add(info);
            }

            return results;
        }

        private static string ApmLevelLabel(int level) => level switch
        {
            <= 0   => "Not supported",
            1      => "1 — Max power saving (frequent spin-down, wears drive)",
            <= 127 => $"{level} — Aggressive power saving",
            <= 192 => $"{level} — Balanced",
            <= 253 => $"{level} — Performance (rare spin-down)",
            254    => "254 — Max performance (no spin-down)",
            _      => $"{level}"
        };

        // ── Benchmark reference values ─────────────────────────────────────────

        public record BenchReference(
            string Category,
            double SeqReadMin, double SeqReadMid, double SeqReadMax,
            double SeqWriteMin, double SeqWriteMid, double SeqWriteMax,
            string Note);

        public static BenchReference? GetReference(double seqReadMBps, bool isSsd, bool isNvme)
        {
            if (isNvme)
                return seqReadMBps >= 4000
                    ? new BenchReference("NVMe PCIe 4.0 SSD", 5000, 6500, 7000, 3000, 5000, 7000,
                        "Gen4 NVMe (Samsung 990 Pro, WD SN850X, etc.)")
                    : new BenchReference("NVMe PCIe 3.0 SSD", 2000, 3000, 3500, 1500, 2000, 3500,
                        "Gen3 NVMe (Samsung 970 EVO, WD SN750, etc.)");
            if (isSsd)
                return new BenchReference("SATA SSD", 480, 530, 560, 400, 500, 540,
                    "SATA III SSD (Samsung 870 EVO, Kingston A400, etc.)");
            // HDD
            return new BenchReference("HDD 7200 RPM", 100, 160, 200, 80, 150, 200,
                "Desktop HDD (Seagate Barracuda, WD Blue 7200 RPM)");
        }

        public static BenchReference? GetReference(double seqReadMBps)
        {
            // Classify by sequential read speed (fallback when the type is unknown)
            if (seqReadMBps >= 4000)
                return new BenchReference("NVMe PCIe 4.0 SSD",
                    5000, 6500, 7000,  3000, 5000, 7000,
                    "Gen4 NVMe (Samsung 990 Pro, WD SN850X, etc.)");

            if (seqReadMBps >= 2000)
                return new BenchReference("NVMe PCIe 3.0 SSD",
                    2000, 3000, 3500,  1500, 2000, 3500,
                    "Gen3 NVMe (Samsung 970 EVO, WD SN750, etc.)");

            if (seqReadMBps >= 400)
                return new BenchReference("SATA SSD",
                    480, 530, 560,  400, 500, 540,
                    "SATA III SSD (Samsung 870 EVO, Kingston A400, etc.)");

            if (seqReadMBps >= 100)
                return new BenchReference("HDD 7200 RPM",
                    100, 160, 200,  80, 150, 200,
                    "Desktop HDD (Seagate Barracuda, WD Blue 7200 RPM)");

            return new BenchReference("HDD 5400 RPM",
                50, 100, 130,  50, 90, 120,
                "Laptop HDD (WD Blue 5400 RPM, Seagate Laptop HDD)");
        }

        /// <summary>
        /// Returns what percentile the user's result falls into vs typical drives of that class.
        /// </summary>
        public static (int percentile, string label, string color) GetPercentile(
            double userMBps, double refMin, double refMid, double refMax)
        {
            if (refMax <= refMin) return (50, "Average", "#F59E0B");

            double range = refMax - refMin;
            double pos   = (userMBps - refMin) / range;

            int pct = (int)Math.Clamp(pos * 100, 0, 100);

            return pct switch
            {
                >= 80 => (pct, "Top performer", "#22C55E"),
                >= 50 => (pct, "Above average", "#22C55E"),
                >= 25 => (pct, "Average", "#F59E0B"),
                _     => (pct, "Below average — possible degradation", "#EF4444"),
            };
        }
    }
}
