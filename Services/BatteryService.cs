using System;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// Reads battery information from multiple WMI sources with graceful fallbacks.
    /// Source priority: Win32_Battery → SystemPowerStatus API → root\WMI classes → powercfg XML
    /// </summary>
    public class BatteryService
    {
        private bool _staticDataResolved;
        private string _cachedManufacturer = "";
        private string _cachedSerialNumber = "";
        private string _cachedManufactureDate = "";
        private string _cachedUniqueID = "";
        private int _cachedDesignCapacityMWh;
        private int _cachedFullCapacityMWh;
        private int _cachedCycleCount;

        private int _lastChargePercent = -1;
        private DateTime _lastChargeCheckUtc = DateTime.MinValue;

        public async Task<BatteryInfo> GetBatteryInfoAsync()
        {
            return await Task.Run(() =>
            {
                var info = new BatteryInfo();

                // ── 1. Win32_Battery — basic charge, status, chemistry ────────
                try
                {
                    using var srch = SMDWin.Services.WmiHelper.Searcher("SELECT * FROM Win32_Battery", System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp1 = srch.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp1)
                    {
                        info.Present       = true;
                        info.Name          = obj["Name"]?.ToString() ?? "Battery";

                        try { var r = obj["EstimatedChargeRemaining"]; if (r != null) info.ChargePercent = Convert.ToInt32(r); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { var r = obj["EstimatedRunTime"]; if (r != null) { int rt = Convert.ToInt32(r); if (rt > 0 && rt < 99999) info.EstimatedRuntime = rt; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        ushort status = 0;
                        try { status = Convert.ToUInt16(obj["BatteryStatus"] ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        info.Status = status switch
                        {
                            1  => "Discharging",
                            2  => "AC Power (plugged in)",
                            3  => "Fully Charged",
                            4  => "Low",
                            5  => "Critical",
                            6  => "Charging",
                            7  => "Charging",
                            8  => "Charging (low)",
                            9  => "Charging (critical)",
                            11 => "Partially Charged",
                            _  => "Unknown"
                        };
                        info.IsCharging = status is 6 or 7 or 8 or 9;
                        info.IsAC       = status is 2 or 3 or 6 or 7 or 8 or 9;
                        info.IsFull     = status == 3;

                        // nominal voltage (mV); some systems report 0 or absurd values → ignored
                        try
                        {
                            var r = obj["DesignVoltage"];
                            if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v is > 3 and < 30) info.DesignVoltageV = v; }
                        }
                        catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        ushort chem = 0;
                        try { chem = Convert.ToUInt16(obj["Chemistry"] ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        info.Chemistry = chem switch
                        {
                            3 => "Lead Acid", 4 => "Nickel Cadmium", 5 => "Nickel Metal Hydride",
                            6 => "Lithium-ion", 7 => "Zinc Air", 8 => "Lithium Polymer",
                            _ => "Lithium-ion"
                        };
                        break;
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 2. GetSystemPowerStatus (Win32 API) — always works, no WMI ──
                try
                {
                    var sps = new SYSTEM_POWER_STATUS();
                    if (GetSystemPowerStatus(ref sps))
                    {
                        // BatteryFlag: 128 = no battery, 255 = unknown/status error
                        // Some OEM drivers report 0 (high battery) even when AC-only — check ACLineStatus too
                        bool hasBattery = sps.BatteryFlag != 128 &&
                                          (sps.BatteryFlag != 255 || sps.BatteryLifePercent < 255);
                        if (hasBattery)
                        {
                            info.Present = true;
                            if (info.ChargePercent == 0 && sps.BatteryLifePercent is > 0 and < 255)
                                info.ChargePercent = sps.BatteryLifePercent;
                            if (!info.IsCharging && (sps.BatteryFlag & 8) != 0)
                                info.IsCharging = true;
                            if (!info.IsAC && sps.ACLineStatus == 1)
                                info.IsAC = true;
                            if (info.EstimatedRuntime == 0 && sps.BatteryLifeTime is > 0 and < uint.MaxValue)
                                info.EstimatedRuntime = (int)(sps.BatteryLifeTime / 60);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                if (!info.Present) return info;

                if (!_staticDataResolved)
                    ResolveStaticDataOnce();

                if (string.IsNullOrEmpty(info.Manufacturer)) info.Manufacturer = _cachedManufacturer;
                if (string.IsNullOrEmpty(info.SerialNumber)) info.SerialNumber = _cachedSerialNumber;
                if (string.IsNullOrEmpty(info.ManufactureDate)) info.ManufactureDate = _cachedManufactureDate;
                if (string.IsNullOrEmpty(info.UniqueID)) info.UniqueID = _cachedUniqueID;
                if (_cachedDesignCapacityMWh > 0) info.DesignCapacityMWh = _cachedDesignCapacityMWh;
                if (_cachedFullCapacityMWh > 0) info.FullCapacityMWh = _cachedFullCapacityMWh;
                if (_cachedCycleCount > 0) info.CycleCount = _cachedCycleCount;

                // ── 6. BatteryStatus (root\WMI) — live voltage / power ────────
                // Read on every tick — these are genuinely live values (current/voltage).
                try
                {
                    using var srch = SMDWin.Services.WmiHelper.Searcher(
                        new ManagementScope(@"root\WMI"),
                        new ObjectQuery("SELECT DischargeRate,ChargeRate,Voltage FROM BatteryStatus"), System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp2 = srch.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp2)
                    {
                        try { var r = obj["Voltage"];       if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) info.VoltageV = v; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { var r = obj["DischargeRate"]; if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) info.DischargePowerW = v; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        try { var r = obj["ChargeRate"];    if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) { info.ChargePowerW = v; info.IsCharging = true; } } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        break;
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                // ── 7. Compute wear ───────────────────────────────────────────
                if (info.DesignCapacityMWh > 0 && info.FullCapacityMWh > 0)
                    info.WearPct = Math.Max(0, (int)(100.0 - (double)info.FullCapacityMWh / info.DesignCapacityMWh * 100.0));

                // ── 8. Charge-increase fallback (see the comment at the _lastChargePercent fields above) — ONLY if the OEM flag
                // does not already say "charging", we are on AC, and the percentage went up since the last real reading (no comparison
                // when too much time passed between readings — a large gap, e.g. after sleep, would give a false positive on a single
                // large jump in percentage).
                if (!info.IsCharging && info.IsAC && _lastChargePercent >= 0
                    && info.ChargePercent > _lastChargePercent
                    && (DateTime.UtcNow - _lastChargeCheckUtc).TotalMinutes < 5)
                {
                    info.IsCharging = true;
                }
                _lastChargePercent   = info.ChargePercent;
                _lastChargeCheckUtc  = DateTime.UtcNow;

                return info;
            });
        }

        private static string DecodeAcpiPackedDate(object? raw)
        {
            if (raw == null) return "";
            if (!uint.TryParse(raw.ToString(), out uint v) || v == 0) return "";
            int day   = (int)(v & 0x1F);
            int month = (int)((v >> 5) & 0x0F);
            int year  = (int)((v >> 9) & 0x7F) + 1980;
            if (month < 1 || month > 12 || day < 1 || day > 31) return "";
            if (year < 1990 || year > DateTime.Now.Year + 1) return ""; // interval plauzibil
            return $"{month:D2}/{year}";
        }

        private void ResolveStaticDataOnce()
        {
            _staticDataResolved = true; // marked first, so this is never entered again, whatever the result

            try
            {
                using var srch = SMDWin.Services.WmiHelper.Searcher(
                    new ManagementScope(@"root\WMI"),
                    new ObjectQuery("SELECT * FROM BatteryStaticData"), System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp3 = srch.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp3)
                {
                    try { _cachedManufacturer = obj["ManufacturerName"]?.ToString()?.Trim() ?? ""; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    try { _cachedSerialNumber = obj["SerialNumber"]?.ToString()?.Trim() ?? ""; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    try { _cachedManufactureDate = DecodeAcpiPackedDate(obj["ManufactureDate"]); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    try { _cachedUniqueID = obj["UniqueID"]?.ToString() ?? ""; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    try { var r = obj["DesignedCapacity"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) _cachedDesignCapacityMWh = v; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            try
            {
                using var srch = SMDWin.Services.WmiHelper.Searcher(
                    new ManagementScope(@"root\WMI"),
                    new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"), System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp4 = srch.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp4)
                {
                    try { var r = obj["FullChargedCapacity"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) _cachedFullCapacityMWh = v; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            try
            {
                using var srch = SMDWin.Services.WmiHelper.Searcher(
                    new ManagementScope(@"root\WMI"),
                    new ObjectQuery("SELECT CycleCount FROM BatteryCycleCount"), System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp5 = srch.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp5)
                {
                    try { var r = obj["CycleCount"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) _cachedCycleCount = v; } } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // powercfg fallback — likewise once only, and only if root\WMI gave no capacities
            if (_cachedDesignCapacityMWh <= 0 || _cachedFullCapacityMWh <= 0)
                TryPowercfgFallbackOnce();

            if (_cachedDesignCapacityMWh <= 0 && _cachedFullCapacityMWh <= 0)
            {
                // A single INFO entry, not WRN with a stack trace — this is normal on many OEM laptops, not an application error.
                AppLogger.Info("BatteryService: root\\WMI + powercfg battery static data unavailable on this system — wear/capacity will show as \"—\".");
            }
        }

        private void TryPowercfgFallbackOnce()
        {
            try
            {
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smdwin_bat.xml");
                try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

                var psi = new System.Diagnostics.ProcessStartInfo("powercfg",
                    $"/batteryreport /xml /output \"{tmp}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var o = proc.StandardOutput.ReadToEndAsync();
                    var e = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit(15000);
                    o.GetAwaiter().GetResult(); e.GetAwaiter().GetResult();
                }

                if (!System.IO.File.Exists(tmp)) return;
                var xml = System.IO.File.ReadAllText(tmp);

                // Try multiple tag name variants across Windows versions
                int design = XInt(xml, "DesignCapacity") ?? XInt(xml, "DESIGN_CAPACITY") ?? XInt(xml, "design-capacity") ?? 0;
                int full   = XInt(xml, "FullChargeCapacity") ?? XInt(xml, "FULL_CHARGE_CAPACITY") ?? XInt(xml, "last-full-charge-capacity") ?? 0;
                int cycles = XInt(xml, "CycleCount") ?? XInt(xml, "CYCLE_COUNT") ?? XInt(xml, "charge-cycles") ?? 0;
                string? mfr = XStr(xml, "ManufactureName") ?? XStr(xml, "Manufacturer");

                if (design > 0) _cachedDesignCapacityMWh = design;
                if (full   > 0) _cachedFullCapacityMWh   = full;
                if (cycles > 0 && _cachedCycleCount <= 0) _cachedCycleCount = cycles;
                if (!string.IsNullOrWhiteSpace(mfr) && string.IsNullOrWhiteSpace(_cachedManufacturer))
                    _cachedManufacturer = mfr!;

                try { System.IO.File.Delete(tmp); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        private static int? XInt(string xml, string tag)
        {
            try
            {
                int s = xml.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
                if (s < 0) return null;
                s += tag.Length + 2;
                int e = xml.IndexOf($"</{tag}>", s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) return null;
                var raw = xml.Substring(s, e - s).Trim();
                return int.TryParse(raw, out int v) && v > 0 ? v : null;
            }
            catch { return null; }
        }

        private static string? XStr(string xml, string tag)
        {
            try
            {
                int s = xml.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
                if (s < 0) return null;
                s += tag.Length + 2;
                int e = xml.IndexOf($"</{tag}>", s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) return null;
                var val = xml.Substring(s, e - s).Trim();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte  ACLineStatus;        // 0=offline, 1=online, 255=unknown
            public byte  BatteryFlag;         // 1=high, 2=low, 4=critical, 8=charging, 128=no battery, 255=unknown
            public byte  BatteryLifePercent;  // 0-100, 255=unknown
            public byte  SystemStatusFlag;
            public uint  BatteryLifeTime;     // seconds remaining (0xFFFFFFFF = unknown)
            public uint  BatteryFullLifeTime;
        }
    }
}
