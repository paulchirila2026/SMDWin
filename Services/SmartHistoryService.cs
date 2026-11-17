using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SMDWin.Models;

namespace SMDWin.Services
{
    // ── Models ────────────────────────────────────────────────────────────────

    public class SmartSnapshot
    {
        public DateTime Timestamp        { get; set; }
        public int      HealthPercent    { get; set; }
        public int      Temperature      { get; set; }  // °C, -1 = N/A
        public long     PowerOnHours     { get; set; }
        public long     ReallocatedSectors { get; set; }
        public long     PendingSectors   { get; set; }
        public long     TbwGB            { get; set; }  // Total Bytes Written in GB
        public int      WearLevel        { get; set; }  // 0-100, -1 = N/A
        public long     SurfaceScanBadSectors { get; set; } = -1;
    }

    public class SmartDiskHistory
    {
        public string SerialNumber { get; set; } = "";
        public string Model        { get; set; } = "";

        [JsonPropertyName("snapshots")]
        public List<SmartSnapshot> Snapshots { get; set; } = new();
    }

    public class SmartDegradationAlert
    {
        public bool   HasAlert   { get; set; }
        public string Message    { get; set; } = "";
        public string Severity   { get; set; } = ""; // "warning" or "critical"
    }

    // ── Service ───────────────────────────────────────────────────────────────

    public static class SmartHistoryService
    {
        private static readonly string HistoryDir = AppPaths.Data("SmartHistory");

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static readonly object _fileLock = new();

        private static readonly ConcurrentDictionary<string, DateTime> _lastSaveUtc = new();
        private const int MinSnapshotIntervalMinutes = 5;
        private const int MaxSnapshots = 365;

        // ── Save ──────────────────────────────────────────────────────────────

        public static void SaveSnapshot(DiskHealthEntry disk)
        {
            if (string.IsNullOrWhiteSpace(disk.SerialNumber) || disk.SerialNumber == "—") return;
            string serial = disk.SerialNumber;

            if (_lastSaveUtc.TryGetValue(serial, out var lastSave) &&
                (DateTime.UtcNow - lastSave).TotalMinutes < MinSnapshotIntervalMinutes)
                return;

            // The smartctl read (may start a process) is done outside the file lock.
            // Always called from Task.Run, and ReadAsync does not capture the synchronization context.
            SmartctlReport? report = null;
            try
            {
                if (SmartctlService.IsAvailable && !string.IsNullOrWhiteSpace(disk.PhysicalDevicePath))
                {
                    var r = SmartctlService.ReadAsync(disk.PhysicalDevicePath).GetAwaiter().GetResult();
                    if (r.Success) report = r;
                }
            }
            catch (Exception ex) { AppLogger.Debug("SmartHistoryService: smartctl for the snapshot: {Msg}", ex.Message); }

            var smart = disk.SmartAttributes;
            SmartAttributeEntry? S(byte id) => smart.FirstOrDefault(a => a.Id == id);

            long pohHours = report != null && report.PowerOnHours >= 0 ? report.PowerOnHours : (S(9)?.RawValue ?? 0);
            long realloc  = S(5)?.RawValue   ?? 0;
            long pending  = S(197)?.RawValue ?? 0;

            int tempC = -1;
            if (report?.TemperatureCelsius is double liveTemp && liveTemp > 0)
                tempC = (int)Math.Round(liveTemp);
            else
            {
                var tAttr = S(0xC2) ?? S(0xBE);
                if (tAttr != null)
                {
                    long lsb = tAttr.RawValue & 0xFF; // the least significant byte = °C
                    if (lsb > 0 && lsb < 100) tempC = (int)lsb;
                }
            }

            long tbwGB = 0;
            int  wearPct = -1;
            if (report != null && report.IsNvme)
            {
                if (report.NvmeDataUnitsWrittenGB.HasValue) tbwGB = report.NvmeDataUnitsWrittenGB.Value;
                if (report.NvmePercentageUsed.HasValue)     wearPct = Math.Clamp(100 - report.NvmePercentageUsed.Value, 0, 100);
            }
            else
            {
                var tbwAttr = S(241);
                if (tbwAttr != null) tbwGB = (long)Math.Max(0, SsdLifeDetector.AttributeToGB(tbwAttr));

                var wearAttr = S(177) ?? S(231) ?? S(232) ?? S(233);
                if (wearAttr != null) wearPct = (int)wearAttr.CurrentValue;
            }

            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(HistoryDir);
                    string path = HistoryPath(serial);

                    var history = LoadHistory(serial) ?? new SmartDiskHistory
                    {
                        SerialNumber = serial,
                        Model        = disk.Model ?? ""
                    };

                    // The app may be restarted: the limit is also checked against the last snapshot in the file.
                    if (history.Snapshots.Count > 0 &&
                        (DateTime.Now - history.Snapshots[^1].Timestamp).TotalMinutes < MinSnapshotIntervalMinutes)
                    {
                        _lastSaveUtc[serial] = history.Snapshots[^1].Timestamp.ToUniversalTime();
                        return;
                    }

                    history.Snapshots.Add(new SmartSnapshot
                    {
                        Timestamp          = DateTime.Now,
                        HealthPercent      = disk.HealthPercent,
                        Temperature        = tempC,
                        PowerOnHours       = pohHours,
                        ReallocatedSectors = realloc,
                        PendingSectors     = pending,
                        TbwGB              = tbwGB,
                        WearLevel          = wearPct
                    });

                    ThinHistory(history);
                    if (history.Snapshots.Count > MaxSnapshots)
                        history.Snapshots.RemoveRange(0, history.Snapshots.Count - MaxSnapshots);

                    WriteHistoryFile(path, history);
                    _lastSaveUtc[serial] = DateTime.UtcNow;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "SmartHistoryService.SaveSnapshot"); }
            } // lock (_fileLock)
        }

        /// <summary>Entries older than 7 days are reduced to the last one of each day; Surface Scan entries
        /// (SurfaceScanBadSectors >= 0) are always kept — they are the comparison points for the alert.</summary>
        private static void ThinHistory(SmartDiskHistory history)
        {
            var cutoff = DateTime.Now.AddDays(-7);
            var older  = history.Snapshots.Where(sn => sn.Timestamp <  cutoff).OrderBy(sn => sn.Timestamp).ToList();
            var recent = history.Snapshots.Where(sn => sn.Timestamp >= cutoff).ToList();
            if (older.Count < 2) return;

            var thinned = older
                .GroupBy(sn => sn.Timestamp.Date)
                .SelectMany(g =>
                {
                    var lastOfDay = g.Last();
                    return g.Where(sn => sn.SurfaceScanBadSectors >= 0 || ReferenceEquals(sn, lastOfDay));
                });
            history.Snapshots = thinned.Concat(recent).ToList();
        }

        /// <summary>Atomic write: a temporary file first, then the swap — an interruption mid-write never leaves a truncated
        /// JSON in place of the history.</summary>
        private static void WriteHistoryFile(string path, SmartDiskHistory history)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(history, _json));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // ── Save: Surface Scan result ────────────────────────────────────────

        public static void SaveSurfaceScanResult(string serialNumber, string model, int healthPercentNow, long badSectorCount)
        {
            if (string.IsNullOrWhiteSpace(serialNumber) || serialNumber == "—") return;

            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(HistoryDir);
                    string path = HistoryPath(serialNumber);

                    var history = LoadHistory(serialNumber) ?? new SmartDiskHistory
                    {
                        SerialNumber = serialNumber,
                        Model        = model ?? ""
                    };

                    var last = history.Snapshots.Count > 0 ? history.Snapshots[^1] : null;

                    history.Snapshots.Add(new SmartSnapshot
                    {
                        Timestamp             = DateTime.Now,
                        HealthPercent         = healthPercentNow,
                        Temperature           = last?.Temperature ?? -1,
                        PowerOnHours          = last?.PowerOnHours ?? 0,
                        ReallocatedSectors    = last?.ReallocatedSectors ?? 0,
                        PendingSectors        = last?.PendingSectors ?? 0,
                        TbwGB                 = last?.TbwGB ?? 0,
                        WearLevel             = last?.WearLevel ?? -1,
                        SurfaceScanBadSectors = badSectorCount
                    });

                    if (history.Snapshots.Count > MaxSnapshots)
                        history.Snapshots.RemoveRange(0, history.Snapshots.Count - MaxSnapshots);

                    WriteHistoryFile(path, history);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "SmartHistoryService.SaveSurfaceScanResult"); }
            }
        }


        // ── Load ──────────────────────────────────────────────────────────────

        public static SmartDiskHistory? LoadHistory(string serialNumber)
        {
            lock (_fileLock)
            {
                try
                {
                    string path = HistoryPath(serialNumber);
                    if (!File.Exists(path)) return null;
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<SmartDiskHistory>(json, _json);
                }
                catch (JsonException ex)
                {
                    string path = HistoryPath(serialNumber);
                    try { File.Move(path, path + ".corrupt", true); }
                    catch (Exception moveEx) { AppLogger.Debug("SmartHistoryService: could not move the corrupt file: {Msg}", moveEx.Message); }
                    AppLogger.Warning(ex, "SmartHistoryService.LoadHistory: corrupt JSON, set aside as .corrupt");
                    return null;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("SmartHistoryService.LoadHistory: {Msg}", ex.Message);
                    return null;
                }
            }
        }

        // ── Degradation detection ─────────────────────────────────────────────

        /// <summary>
        /// Compares the latest snapshot with the previous ones to detect degradation.
        /// </summary>
        public static SmartDegradationAlert CheckDegradation(SmartDiskHistory history)
        {
            if (history.Snapshots.Count < 2)
                return new SmartDegradationAlert { HasAlert = false };

            var latest = history.Snapshots[^1];
            var windowStart = latest.Timestamp.AddDays(-30);
            var reference = history.Snapshots
                .Where(s => s.Timestamp >= windowStart && s.Timestamp < latest.Timestamp.AddHours(-1))
                .OrderBy(s => s.Timestamp)
                .FirstOrDefault() ?? history.Snapshots[0];

            var scanSnapshots = history.Snapshots.Where(s => s.SurfaceScanBadSectors >= 0)
                .OrderByDescending(s => s.Timestamp).ToList();
            if (scanSnapshots.Count >= 2)
            {
                var latestScan = scanSnapshots[0];
                var priorScan  = scanSnapshots[1];
                long badDelta  = latestScan.SurfaceScanBadSectors - priorScan.SurfaceScanBadSectors;
                if (badDelta > 0)
                    return new SmartDegradationAlert
                    {
                        HasAlert = true,
                        Severity = badDelta >= 5 || latestScan.SurfaceScanBadSectors >= 20 ? "critical" : "warning",
                        Message  = $"⚠ Surface Scan bad sectors grew by {badDelta} (from {priorScan.SurfaceScanBadSectors} to {latestScan.SurfaceScanBadSectors}) since the scan on {SMDWin.Services.DateFormatHelper.FormatDate(priorScan.Timestamp)} — this is a repeat, not a one-off."
                    };
            }

            // Reallocated sectors increased → bad sign
            long reallocDelta = latest.ReallocatedSectors - reference.ReallocatedSectors;
            if (reallocDelta > 0)
                return new SmartDegradationAlert
                {
                    HasAlert = true,
                    Severity = reallocDelta >= 5 ? "critical" : "warning",
                    Message  = $"⚠ Reallocated sectors grew by {reallocDelta} since {SMDWin.Services.DateFormatHelper.FormatDate(reference.Timestamp)} — drive may be failing."
                };

            // Pending sectors increased
            long pendingDelta = latest.PendingSectors - reference.PendingSectors;
            if (pendingDelta > 0)
                return new SmartDegradationAlert
                {
                    HasAlert = true,
                    Severity = "warning",
                    Message  = $"⚠ {pendingDelta} new pending sectors since {SMDWin.Services.DateFormatHelper.FormatDate(reference.Timestamp)}."
                };

            // Wear level dropped significantly (SSD)
            if (latest.WearLevel >= 0 && reference.WearLevel >= 0)
            {
                int wearDelta = reference.WearLevel - latest.WearLevel;
                if (wearDelta >= 10)
                    return new SmartDegradationAlert
                    {
                        HasAlert = true,
                        Severity = latest.WearLevel < 20 ? "critical" : "warning",
                        Message  = $"⚠ SSD wear level dropped by {wearDelta}% (now {latest.WearLevel}%)."
                    };
            }

            return new SmartDegradationAlert { HasAlert = false };
        }

        private static string HistoryPath(string serial) =>
            Path.Combine(HistoryDir,
                string.Concat(serial.Where(c => char.IsLetterOrDigit(c) || c == '-')) + ".json");
    }

    public static class SsdLifeDetector
    {
        /// <summary>The data written / read (attributes 0xF1 / 0xF2) in GB; −1 = unknown. The unit of the raw value is chosen in
        /// <see cref="LifetimeData.Bytes"/>.</summary>
        public static double AttributeToGB(SmartAttributeEntry e)
        {
            double bytes = AttributeToBytes(e);
            return bytes > 0 ? bytes / 1e9 : -1;
        }

        public static double AttributeToBytes(SmartAttributeEntry e)
            => LifetimeData.Bytes(e.RawValue, e.SmartctlName, e.SectorBytes, e.NvmeDataUnits);

        public static double LbasToGB(long rawLbas)
        {
            if (rawLbas <= 0) return -1;
            return rawLbas * 512.0 / 1_000_000_000.0; // LBAs → GB
        }
    }
}
