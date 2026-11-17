using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class DriverService
    {
        private volatile List<DriverEntry>? _cachedDrivers;
        private DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
        private readonly System.Threading.SemaphoreSlim _cacheLock = new(1, 1);

        private static readonly string DiskCachePath = ResolveDiskCachePath();

        private static string ResolveDiskCachePath()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(AppLogger.LogDirectory) ?? AppLogger.LogDirectory, "cache");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "drivers-cache.json");
            }
            catch { return ""; }
        }

        // the saved driver list belongs to a single computer (see MachineIdentity)
        private sealed class DriverDiskCache
        {
            public string MachineId { get; set; } = "";
            public List<DriverEntry>? Drivers { get; set; }
        }

        private static List<DriverEntry>? LoadDiskCache()
        {
            try
            {
                if (string.IsNullOrEmpty(DiskCachePath) || !File.Exists(DiskCachePath)) return null;
                var json = File.ReadAllText(DiskCachePath);
                if (json.TrimStart().StartsWith("[")) return null;   // old format, without a computer
                var c = JsonSerializer.Deserialize<DriverDiskCache>(json);
                return c != null && MachineIdentity.IsThisMachine(c.MachineId) ? c.Drivers : null;
            }
            catch (Exception ex) { AppLogger.Debug($"DriverService.LoadDiskCache: {ex.Message}"); return null; }
        }

        private static void SaveDiskCache(List<DriverEntry> drivers)
        {
            try
            {
                if (string.IsNullOrEmpty(DiskCachePath)) return;
                File.WriteAllText(DiskCachePath, JsonSerializer.Serialize(new DriverDiskCache { MachineId = MachineIdentity.Current, Drivers = drivers }));
            }
            catch (Exception ex) { AppLogger.Debug($"DriverService.SaveDiskCache: {ex.Message}"); }
        }

        public async Task<List<DriverEntry>> GetDriversAsync()
        {
            // Fast path — no lock needed when cache is valid (volatile read)
            if (_cachedDrivers != null && (DateTime.Now - _cacheTime) < _cacheTtl)
                return _cachedDrivers;

            await _cacheLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check inside lock
                if (_cachedDrivers != null && (DateTime.Now - _cacheTime) < _cacheTtl)
                    return _cachedDrivers;

                bool isColdStart = _cacheTime == DateTime.MinValue;
                if (isColdStart)
                {
                    var diskCached = LoadDiskCache();
                    if (diskCached != null && diskCached.Count > 0)
                    {
                        _cachedDrivers = diskCached;
                        // Almost expired — the next call (not this one) triggers a normal refresh, giving the background refresh time to finish.
                        _cacheTime = DateTime.Now - _cacheTtl + TimeSpan.FromSeconds(10);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fresh = await Task.Run(FetchDriversFromWmi).ConfigureAwait(false);
                                _cachedDrivers = fresh;
                                _cacheTime = DateTime.Now;
                                SaveDiskCache(fresh);
                            }
                            catch (Exception ex) { AppLogger.Debug($"DriverService background refresh: {ex.Message}"); }
                        });
                        return _cachedDrivers;
                    }
                }

                var result = await Task.Run(FetchDriversFromWmi).ConfigureAwait(false);

                _cachedDrivers = result;
                _cacheTime = DateTime.Now;
                SaveDiskCache(result);
                return result;
            }
            finally { _cacheLock.Release(); }
        }

        private static List<DriverEntry> FetchDriversFromWmi()
        {
                var results = new List<DriverEntry>();

                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.SysDriverQuery);

                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp1)
                    {
                        results.Add(new DriverEntry
                        {
                            Name = obj["Name"]?.ToString() ?? "",
                            DisplayName = obj["DisplayName"]?.ToString() ?? "",
                            Status = obj["State"]?.ToString() ?? "",
                            Provider = obj["PathName"]?.ToString() ?? "",
                            IsSigned = true // default; signature check below
                        });
                    }
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new DriverEntry
                    {
                        Name = "Error",
                        DisplayName = ex.Message,
                        Status = "Error"
                    });
                }

                // Get device drivers with version info
                try
                {
                    var byName = new Dictionary<string, DriverEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in results)
                        if (!byName.ContainsKey(r.Name)) byName[r.Name] = r;

                    var signed = GetSignedDriverInfo();
                    foreach (var d in signed)
                    {
                        if (byName.TryGetValue(d.Name, out var existing))
                        {
                            existing.Version = d.Version;
                            existing.Provider = d.Provider;
                            existing.Date = d.Date;
                            existing.DateRaw = d.DateRaw;
                            existing.IsSigned = d.IsSigned;
                        }
                        else
                        {
                            results.Add(d);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
                return results;
        }

        // The Win32_PnPSignedDriver query is the slow part (~4 s): the result is kept for 3 min and used by both the advanced
        // and the simple list; the background start prepares it in advance.
        private static readonly object _signedLock = new();
        private static List<DriverEntry>? _signedCache;
        private static DateTime _signedAt = DateTime.MinValue;


        private static List<DriverEntry> GetSignedDriverInfo()
        {
            lock (_signedLock)
            {
                if (_signedCache != null && (DateTime.UtcNow - _signedAt).TotalSeconds < 180)
                    return _signedCache.Select(d => d.Clone()).ToList();
                var fresh = ReadSignedDriverInfo();
                if (fresh.Count > 0) { _signedCache = fresh; _signedAt = DateTime.UtcNow; }
                return fresh.Select(d => d.Clone()).ToList();
            }
        }

        private static List<DriverEntry> ReadSignedDriverInfo()
        {
            var results = new List<DriverEntry>();

            try
            {
                using var searcher = WmiHelper.Searcher(
                    @"root\CIMv2",
                    WmiHelper.PnpSignedDriverQuery);

                using (var __wmiTmp2 = searcher.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp2)
                {
                    var name = obj["DeviceName"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    results.Add(new DriverEntry
                    {
                        Name = obj["InfName"]?.ToString() ?? name,
                        DisplayName = name,
                        Version = obj["DriverVersion"]?.ToString() ?? "n/a",
                        Provider = obj["Manufacturer"]?.ToString() ?? "n/a",
                        Date = FormatDate(obj["DriverDate"]?.ToString()),
                        DateRaw = ParseWmiDateRaw(obj["DriverDate"]?.ToString()),
                        IsSigned = obj["IsSigned"] is bool b ? b : true,
                        Status = obj["DeviceClass"]?.ToString() ?? "n/a"
                    });
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

            return results;
        }

        private static string FormatDate(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "n/a";
            try
            {
                return SMDWin.Services.DateFormatHelper.FormatDate(ManagementDateTimeConverter.ToDateTime(wmiDate));
            }
            catch { return wmiDate[..8]; }
        }

        private static DateTime? ParseWmiDateRaw(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return null;
            try { return ManagementDateTimeConverter.ToDateTime(wmiDate); }
            catch { return null; }
        }

        public async Task<List<DriverEntry>> GetUnsignedDriversAsync()
        {
            var all = await GetDriversAsync();
            // 16.11.2026: a WMI timeout is kept in the list as an "Error" entry — it was counted as one unsigned driver
            // ("Unsigned drivers found: 1 — Timed out")
            return all.FindAll(d => !d.IsSigned && d.Status != "Error");
        }

        public void OpenDeviceManager()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("devmgmt.msc")
            {
                UseShellExecute = true
            });
        }

        // ── BASIC VIEW: Device Manager-style list with HardwareID ───────────
        public async Task<List<DeviceManagerEntry>> GetDeviceManagerDevicesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<DeviceManagerEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.PnpEntityQuery);
                    using (var __wmiTmp3 = searcher.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp3)
                    {
                        var name       = obj["Name"]?.ToString() ?? "Unknown Device";
                        var status     = obj["Status"]?.ToString() ?? "";
                        var configErr  = obj["ConfigManagerErrorCode"];
                        int errCode    = configErr != null ? Convert.ToInt32(configErr) : 0;
                        var hwIds      = obj["HardwareID"] as string[];
                        var hwId       = hwIds != null && hwIds.Length > 0 ? hwIds[0] : "";
                        var cls        = obj["PNPClass"]?.ToString() ?? obj["ClassGuid"]?.ToString() ?? "Other";
                        var mfr        = obj["Manufacturer"]?.ToString() ?? "n/a";
                        var deviceId   = obj["DeviceID"]?.ToString() ?? "";

                        bool isGenericDisplayDriver = cls.Equals("Display", StringComparison.OrdinalIgnoreCase)
                            && (name.Contains("Basic Display Adapter", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Standard VGA", StringComparison.OrdinalIgnoreCase));

                        bool isMissing = (errCode != 0 && errCode != 22) || isGenericDisplayDriver;
                        bool hasDriver = errCode == 0 && !string.IsNullOrEmpty(hwId);

                        results.Add(new DeviceManagerEntry
                        {
                            Name         = name,
                            DeviceClass  = cls,
                            Manufacturer = mfr,
                            HardwareId   = hwId,
                            DeviceId     = deviceId,
                            ErrorCode    = errCode,
                            IsMissing    = isMissing,
                            Status       = errCode == 0 ? (isGenericDisplayDriver ? "Generic driver" : "OK") :
                                           errCode == 28 ? "No driver" :
                                           errCode == 43 ? "Error (43)" :
                                           $"Error ({errCode})"
                        });
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                // ── Enrich with Date, Version, IsSigned from Win32_PnPSignedDriver ──
                try
                {
                    var signed = GetSignedDriverInfo();
                    foreach (var d in signed)
                    {
                        var existing = results.Find(r =>
                            r.Name.Equals(d.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                            r.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Version  = d.Version;
                            existing.Date     = d.Date;
                            existing.DateRaw  = d.DateRaw;
                            existing.IsSigned = d.IsSigned;
                            if (string.IsNullOrEmpty(existing.Manufacturer) || existing.Manufacturer == "n/a")
                                existing.Manufacturer = d.Provider;
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                results.Sort((a, b) =>
                {
                    // Missing/error devices first
                    int am = a.IsMissing ? 0 : 1;
                    int bm = b.IsMissing ? 0 : 1;
                    if (am != bm) return am.CompareTo(bm);
                    return string.Compare(a.DeviceClass + a.Name, b.DeviceClass + b.Name,
                        StringComparison.OrdinalIgnoreCase);
                });
                return results;
            });
        }

        public async Task<List<DeviceManagerEntry>> GetMissingDevicesAsync()
        {
            var all = await GetDeviceManagerDevicesAsync();
            return all.FindAll(d => d.IsMissing);
        }
    }
}
