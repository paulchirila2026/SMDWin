using System;
using System.Collections.Generic;
using System.Management;

namespace SMDWin.Services
{
    /// <summary>What the monitor says about itself: the screen size (cm) and the connection type (the Windows code; -1 = not read).</summary>
    public readonly record struct MonitorPhysical(double WidthCm, double HeightCm, long Technology);

    /// <summary>
    /// Short Windows reads used by the System Information and Windows info views and checked by the self-test:
    /// monitor size and connection, the active antivirus, system disk encryption. All are slow (WMI) — call them from a
    /// background thread. A read that fails returns "not read", it does not throw.
    /// </summary>
    public static class SystemProbes
    {
        /// <summary>
        /// Size and connection of each monitor (WmiMonitorBasicDisplayParams, WmiMonitorConnectionParams), by the monitor's
        /// hardware identifier (the second part of InstanceName, e.g. "DELA0A2").
        /// </summary>
        public static Dictionary<string, MonitorPhysical> ReadMonitors()
        {
            var result = new Dictionary<string, MonitorPhysical>(StringComparer.OrdinalIgnoreCase);
            static string HardwareId(ManagementBaseObject mo)
            {
                string instanceName = mo["InstanceName"]?.ToString() ?? "";
                var segs = instanceName.Split('\\');
                return segs.Length > 1 ? segs[1] : "";
            }
            try
            {
                using var sizeS = SMDWin.Services.WmiHelper.Searcher(@"root\wmi",
                    "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", System.TimeSpan.FromSeconds(10));
                using var col = sizeS.Get();
                foreach (ManagementObject mo in col)
                {
                    string id = HardwareId(mo);
                    if (id.Length == 0) continue;
                    double w = mo["MaxHorizontalImageSize"] is byte bw ? bw : 0, h = mo["MaxVerticalImageSize"] is byte bh ? bh : 0;
                    result[id] = new MonitorPhysical(w, h, -1);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SystemProbes.ReadMonitors size: {ex.Message}"); }
            try
            {
                using var conS = SMDWin.Services.WmiHelper.Searcher(@"root\wmi",
                    "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", System.TimeSpan.FromSeconds(10));
                using var col = conS.Get();
                foreach (ManagementObject mo in col)
                {
                    string id = HardwareId(mo);
                    if (id.Length == 0 || mo["VideoOutputTechnology"] == null) continue;
                    long technology = Convert.ToInt64(mo["VideoOutputTechnology"]) & 0xFFFFFFFF;
                    result[id] = result.TryGetValue(id, out var known)
                        ? known with { Technology = technology }
                        : new MonitorPhysical(0, 0, technology);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SystemProbes.ReadMonitors connection: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// Antivirus products registered in Windows Security. Read = false when the list does not exist (Windows Server) or
        /// cannot be read; Active = the one actually protecting (null = none); Count = how many are registered.
        /// </summary>
        public static (bool Read, AntivirusProduct? Active, int Count) ReadAntivirus()
        {
            try
            {
                var products = new List<AntivirusProduct>();
                using var searcher = SMDWin.Services.WmiHelper.Searcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", System.TimeSpan.FromSeconds(10));
                using var list = searcher.Get();
                foreach (ManagementObject av in list)
                {
                    long state = 0;
                    try { state = Convert.ToInt64(av["productState"] ?? 0); }
                    catch (Exception ex) { AppLogger.Debug($"SystemProbes productState: {ex.Message}"); }
                    var (enabled, upToDate) = WindowsFacts.AntivirusState(state);
                    products.Add(new AntivirusProduct(av["displayName"]?.ToString()?.Trim() ?? "", enabled, upToDate));
                }
                return (true, WindowsFacts.ActiveAntivirus(products), products.Count);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SystemProbes.ReadAntivirus: {ex.Message}");
                return (false, null, 0);
            }
        }

        /// <summary>
        /// BitLocker / "Device encryption" on the Windows disk: true = encrypted, false = not encrypted,
        /// null = not read (edition without encryption, no rights or unknown state).
        /// </summary>
        public static bool? ReadSystemDriveEncryption()
        {
            try
            {
                string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
                using var searcher = SMDWin.Services.WmiHelper.Searcher(@"root\cimv2\Security\MicrosoftVolumeEncryption",
                    "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", System.TimeSpan.FromSeconds(10));
                using var list = searcher.Get();
                foreach (ManagementObject volume in list)
                    if (string.Equals(volume["DriveLetter"]?.ToString(), systemDrive, StringComparison.OrdinalIgnoreCase))
                        return WindowsFacts.BitLockerOn(Convert.ToInt64(volume["ProtectionStatus"] ?? 2));
            }
            catch (Exception ex) { AppLogger.Debug($"SystemProbes.ReadSystemDriveEncryption: {ex.Message}"); }
            return null;
        }
    }
}
