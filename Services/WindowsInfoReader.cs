using System;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// What Windows says about itself: version, installation, license, protection and the computer's identity.
    /// A value that cannot be read stays empty (or null), it does not throw. Reads are slow (WMI): call them from a
    /// background thread. Used by the Windows tab in System Information.
    /// </summary>
    public sealed class WindowsInfo
    {
        public string ProductName = "", Edition = "", DisplayVersion = "", Architecture = "", RegisteredUser = "";
        public int BuildNumber;
        public string Ubr = "";
        public DateTime? InstalledOn, LastUpdateOn;

        // licenta
        public uint LicenseStatus;                 // 1 = licentiat
        public bool LicenseRead;
        public string LicenseChannel = "";
        public string ProductKey = "";             // empty = digital license (no readable key)

        // protectie (null = necitit)
        public string BootMode = "";
        public bool? SecureBoot;
        public string TpmVersion = "";             // empty = not present
        public bool TpmRead;
        public bool AntivirusRead;
        public AntivirusProduct? Antivirus;
        public bool? DiskEncrypted;
        public bool? Firewall;                     // Windows Firewall / an active firewall product

        // sistem
        public string Workgroup = "";

        public bool Licensed => LicenseStatus == 1;
        public bool IsWindows11 => BuildNumber >= 22000;
        public bool IsLongTermChannel => Edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase);
        public string FullBuild => BuildNumber > 0 ? BuildNumber + (Ubr.Length > 0 ? "." + Ubr : "") : "";
    }

    public static class WindowsInfoReader
    {
        private static string? _productKeyCache;

        public static WindowsInfo Read()
        {
            var w = new WindowsInfo();
            AppLogger.Debug("WindowsInfoReader: start");

            var reg = OsRegistryInfo.Current;
            if (reg.Ok)
            {
                w.ProductName = reg.ProductName;
                w.Architecture = reg.Architecture;
                w.RegisteredUser = reg.RegisteredOwner;
                w.InstalledOn = reg.InstalledOn;
            }
            else
            try
            {
                using var s = SMDWin.Services.WmiHelper.Searcher("SELECT Caption, OSArchitecture, InstallDate, RegisteredUser FROM Win32_OperatingSystem", System.TimeSpan.FromSeconds(10));
                using var col = s.Get();
                foreach (ManagementObject obj in col)
                {
                    w.ProductName = obj["Caption"]?.ToString()?.Replace("Microsoft ", "").Trim() ?? "";
                    w.Architecture = obj["OSArchitecture"]?.ToString() ?? "";
                    w.RegisteredUser = obj["RegisteredUser"]?.ToString()?.Trim() ?? "";
                    if (obj["InstallDate"]?.ToString() is { Length: > 0 } raw)
                    {
                        try { w.InstalledOn = ManagementDateTimeConverter.ToDateTime(raw); }
                        catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader install date: {ex.Message}"); }
                    }
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: Win32_OperatingSystem"); }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    w.Ubr = key.GetValue("UBR")?.ToString() ?? "";
                    w.DisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? key.GetValue("ReleaseId")?.ToString() ?? "";
                    w.Edition = key.GetValue("EditionID")?.ToString() ?? "";
                    string buildStr = key.GetValue("CurrentBuildNumber")?.ToString() ?? key.GetValue("CurrentBuild")?.ToString() ?? "";
                    int.TryParse(buildStr, out w.BuildNumber);
                    string productName = key.GetValue("ProductName")?.ToString() ?? "";
                    // ProductName stays "Windows 10" in the registry even on Windows 11
                    if (w.BuildNumber >= 22000) productName = productName.Replace("Windows 10", "Windows 11");
                    if (productName.Length > 0) w.ProductName = productName;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: registry version"); }

            try
            {
                using var lic = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name, LicenseStatus, Description FROM SoftwareLicensingProduct " +
                    "WHERE PartialProductKey IS NOT NULL AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'", System.TimeSpan.FromSeconds(10));
                using var col = lic.Get();
                foreach (ManagementObject obj in col)
                {
                    string licName = obj["Name"]?.ToString() ?? "", desc = obj["Description"]?.ToString() ?? "";
                    w.LicenseStatus = obj["LicenseStatus"] is uint u ? u : 0;
                    w.LicenseRead = true;
                    w.LicenseChannel =
                        licName.Contains("OEM", StringComparison.OrdinalIgnoreCase) ? "OEM" :
                        licName.Contains("Retail", StringComparison.OrdinalIgnoreCase) ? "Retail" :
                        licName.Contains("Volume", StringComparison.OrdinalIgnoreCase) ? "Volume" :
                        desc.Contains("OEM", StringComparison.OrdinalIgnoreCase) ? "OEM" :
                        desc.Contains("Retail", StringComparison.OrdinalIgnoreCase) ? "Retail" :
                        desc.Contains("DIGITAL", StringComparison.OrdinalIgnoreCase) || desc.Contains("Electronic", StringComparison.OrdinalIgnoreCase) ? "Digital" : "";
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: licence"); }

            w.ProductKey = ReadProductKey();

            w.Workgroup = ReadWorkgroup();
            w.BootMode = ReadBootMode();

            w.SecureBoot = ReadSecureBoot();

            try
            {
                using var tpm = SMDWin.Services.WmiHelper.Searcher(@"root\cimv2\security\microsofttpm", "SELECT SpecVersion FROM Win32_Tpm", System.TimeSpan.FromSeconds(10));
                using var col = tpm.Get();
                var obj = col.Cast<ManagementObject>().FirstOrDefault();
                w.TpmRead = true;
                w.TpmVersion = obj?["SpecVersion"] is string spec ? spec.Split(',')[0].Trim() : "";
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader tpm: {ex.Message}"); }

            (w.AntivirusRead, w.Antivirus, _) = SystemProbes.ReadAntivirus();
            w.DiskEncrypted = SystemProbes.ReadSystemDriveEncryption();
            w.Firewall = ReadFirewall();
            w.LastUpdateOn = ReadLastUpdate();

            AppLogger.Debug("WindowsInfoReader: done");
            return w;
        }

        /// <summary>The computer's domain or workgroup (empty if it cannot be read).</summary>
        /// <summary>Secure Boot: the same source for the Dashboard, System Info and Health. A missing value (old BIOS / Legacy mode) = off.</summary>
        public static bool ReadSecureBoot()
        {
            try
            {
                var sb = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
                return sb is int i && i == 1;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: secure boot"); return false; }
        }

        /// <summary>Firewall on (WMI SecurityCenter2, with the registry state as a fallback); null = could not be read.</summary>
        public static bool? ReadFirewall()
        {
            try
            {
                using var searcher = SMDWin.Services.WmiHelper.Searcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct", System.TimeSpan.FromSeconds(10));
                using var col = searcher.Get();
                bool found = false, anyEnabled = false;
                foreach (ManagementObject obj in col)
                {
                    found = true;
                    uint state = 0;
                    try { state = (uint)(obj["productState"] ?? 0u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    if (((state >> 12) & 0xF) == 1 || ((state >> 4) & 0xF) == 1) anyEnabled = true;
                }
                if (found) return anyEnabled;
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader firewall (WMI): {ex.Message}"); }
            try
            {
                var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", "EnableFirewall", null);
                if (v is int vi) return vi == 1;
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader firewall (registry): {ex.Message}"); }
            return null;
        }

        public static string ReadWorkgroup()
        {
            try
            {
                using var wg = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
                string domain = wg?.GetValue("Domain") as string ?? "", workgroup = wg?.GetValue("Workgroup") as string ?? "";
                return !string.IsNullOrWhiteSpace(domain) ? domain
                    : !string.IsNullOrWhiteSpace(workgroup) ? workgroup
                    : System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName is { Length: > 0 } dn ? dn
                    : "WORKGROUP";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: workgroup"); return ""; }
        }

        /// <summary>"UEFI" or "BIOS / MBR" (empty if it cannot be read).</summary>
        public static string ReadBootMode()
        {
            try
            {
                var fw = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "PEFirmwareType", null);
                return fw is int f ? (f == 2 ? "UEFI" : "BIOS / MBR") : "";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsInfoReader: boot mode"); return ""; }
        }

        private static DateTime? ReadLastUpdate()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
                if (key?.GetValue("LastSuccessTime") is string last
                    && DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    return dt.ToLocalTime();
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader update key: {ex.Message}"); }
            try
            {
                DateTime? newest = null;
                using var s = SMDWin.Services.WmiHelper.Searcher("SELECT InstalledOn FROM Win32_QuickFixEngineering", System.TimeSpan.FromSeconds(10));
                using var col = s.Get();
                foreach (ManagementObject qfe in col)
                    if (qfe["InstalledOn"] is string raw
                        && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                        && (newest == null || d > newest)) newest = d;
                return newest;
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader hotfixes: {ex.Message}"); }
            return null;
        }

        /// <summary>The OEM key from the BIOS (OA3x) or the fallback key from the registry; empty when the license is digital.</summary>
        public static string ReadProductKey()
        {
            if (_productKeyCache != null) return _productKeyCache;
            string pk = "";
            try
            {
                using var s = SMDWin.Services.WmiHelper.Searcher("SELECT OA3xOriginalProductKey FROM SoftwareLicensingService", System.TimeSpan.FromSeconds(10));
                using var col = s.Get();
                foreach (ManagementObject mo in col) { pk = mo["OA3xOriginalProductKey"]?.ToString()?.Trim() ?? ""; break; }
            }
            catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader OA3x key: {ex.Message}"); }
            if (string.IsNullOrWhiteSpace(pk))
            {
                try
                {
                    using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform", false);
                    pk = rk?.GetValue("BackupProductKeyDefault")?.ToString()?.Trim() ?? "";
                }
                catch (Exception ex) { AppLogger.Debug($"WindowsInfoReader backup key: {ex.Message}"); }
            }
            _productKeyCache = pk;
            return pk;
        }
    }
}
