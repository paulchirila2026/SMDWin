using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class WinServicesService
    {
        // Services that must never be stopped or disabled — doing so can leave the
        // system unprotected or unstable. Blocked at the service layer regardless of UI input.
        private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "MpsSvc",              // Windows Firewall
            "WinDefend",           // Windows Defender Antivirus
            "wscsvc",              // Windows Security Center
            "SecurityHealthService", // Windows Security Health
            "SamSs",               // Security Accounts Manager — stopping this crashes the session
            "RpcSs",               // Remote Procedure Call — system-critical
            "DcomLaunch",          // DCOM Server Process Launcher — system-critical
        };

        /// <summary>Returns true if the service is protected and cannot be stopped or disabled.</summary>
        public static bool IsCritical(string serviceName)
            => CriticalServices.Contains(serviceName);

        // Known services — values are LanguageService keys, not hardcoded strings
        private static readonly Dictionary<string, string> KnownServices = new()
        {
            ["wuauserv"] = "svc_wuauserv",
            ["WSearch"] = "svc_WSearch",
            ["SysMain"] = "svc_SysMain",
            ["DiagTrack"] = "svc_DiagTrack",
            ["WerSvc"] = "svc_WerSvc",
            ["Spooler"] = "svc_Spooler",
            ["BITS"] = "svc_BITS",
            ["wscsvc"] = "svc_wscsvc",
            ["MpsSvc"] = "svc_MpsSvc",
            ["WinDefend"] = "svc_WinDefend",
            ["TabletInputService"] = "svc_TabletInputService",
            ["XblGameSave"] = "svc_XblGameSave",
            ["XboxNetApiSvc"] = "svc_XboxNetApiSvc",
            ["MapsBroker"] = "svc_MapsBroker",
            ["lfsvc"] = "svc_lfsvc",
            ["RetailDemo"] = "svc_RetailDemo",
            ["RemoteRegistry"] = "svc_RemoteRegistry",
            ["TrkWks"] = "svc_TrkWks",
            ["Fax"] = "svc_Fax",
            ["PhoneSvc"] = "svc_PhoneSvc",
            ["WMPNetworkSvc"] = "svc_WMPNetworkSvc",
        };

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _publisherCache = new(StringComparer.OrdinalIgnoreCase);

        private static string ResolvePublisher(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return "";
            try
            {
                string exePath = StartupManagerService.ExtractExePath(imagePath);
                if (string.IsNullOrEmpty(exePath)) return "";
                exePath = Environment.ExpandEnvironmentVariables(exePath);
                if (!System.IO.File.Exists(exePath)) return "";
                if (_publisherCache.TryGetValue(exePath, out var cached)) return cached;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                string pub = info.CompanyName?.Trim() ?? "";
                _publisherCache[exePath] = pub;
                return pub;
            }
            catch { return ""; }
        }

        public async Task<List<WinServiceEntry>> GetServicesAsync(bool onlyKnown = false)
        {
            return await Task.Run(() =>
            {
                var results = new System.Collections.Concurrent.ConcurrentBag<WinServiceEntry>();

                try
                {
                    var services = ServiceController.GetServices();
                    System.Threading.Tasks.Parallel.ForEach(services, svc =>
                    {
                        try
                        {
                            bool isKnown = KnownServices.ContainsKey(svc.ServiceName);
                            if (onlyKnown && !isKnown) return;

                            var startType = GetStartType(svc.ServiceName);
                            string imagePath = GetImagePath(svc.ServiceName);
                            results.Add(new WinServiceEntry
                            {
                                Name        = svc.ServiceName,
                                DisplayName = svc.DisplayName,
                                Status      = svc.Status.ToString(),
                                StartType   = startType,
                                IsKnown     = isKnown,
                                IsCritical  = IsCritical(svc.ServiceName),
                                Description = KnownServices.TryGetValue(svc.ServiceName, out var langKey)
                                    ? LanguageService.S("Services", langKey, langKey)
                                    : "",
                                ImagePath   = imagePath,
                                Publisher   = ResolvePublisher(imagePath),
                            });
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "WinServicesService.GetServicesAsync"); }
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new WinServiceEntry
                    {
                        Name        = "Error",
                        DisplayName = ex.Message,
                        Status      = "Unknown"
                    });
                }

                var sorted = results.ToList();
                sorted.Sort((a, b) =>
                {
                    // Known first
                    if (a.IsKnown != b.IsKnown) return a.IsKnown ? -1 : 1;
                    return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                });

                return sorted;
            });
        }

        private static string GetImagePath(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("ImagePath")?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string GetStartType(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                var val = key?.GetValue("Start");
                if (val == null) return "Unknown";
                return (int)val switch
                {
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Unknown"
                };
            }
            catch { return "Unknown"; }
        }

        /// <summary>Also reads the "Automatic (Delayed Start)" flag — a value separate from Start=2 (DelayedAutostart) that
        /// GetStartType does not distinguish. Needed to restore a service's original state exactly, not just "plain Automatic".</summary>
        public static (string startType, bool delayed) GetStartTypeAndDelayed(string serviceName)
        {
            string startType = GetStartType(serviceName);
            bool delayed = false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                delayed = startType == "Automatic" && (key?.GetValue("DelayedAutostart") as int?) == 1;
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* delayed stays false */ }
            return (startType, delayed);
        }

        public async Task<bool> SetServiceStartTypeAsync(string serviceName, string startType)
            => await SetServiceStartTypeAsync(serviceName, startType, delayed: false);

        /// <summary>Overload that also restores "Automatic (Delayed Start)" when the service's original state had it — otherwise
        /// a service re-enabled by the Startup Manager would silently lose this flag, even though it went back to "Automatic".</summary>
        public async Task<bool> SetServiceStartTypeAsync(string serviceName, string startType, bool delayed)
        {
            // Never allow disabling or setting to Manual a critical security service
            if (IsCritical(serviceName) && startType is "Disabled" or "Manual")
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    string before = GetStartType(serviceName);
                    var sc = new ServiceController(serviceName);
                    var type = startType switch
                    {
                        "Automatic" => ServiceStartMode.Automatic,
                        "Manual"    => ServiceStartMode.Manual,
                        "Disabled"  => ServiceStartMode.Disabled,
                        _ => ServiceStartMode.Manual
                    };
                    ServiceHelper.ChangeStartMode(sc, type);

                    // DelayedAutostart is a separate registry value that ChangeServiceConfig (above) may reset on write — it is restored
                    // explicitly only when startType is Automatic (the only case where it makes sense).
                    try
                    {
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                        if (key != null && type == ServiceStartMode.Automatic)
                            key.SetValue("DelayedAutostart", delayed ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    catch (Exception ex) { AppLogger.Debug("SetServiceStartTypeAsync DelayedAutostart: " + ex.Message); }

                    // every change made from SMD Win is remembered, so it can be shown and undone later (Services → nothing selected)
                    if (!string.Equals(before, startType, StringComparison.OrdinalIgnoreCase))
                        ServiceChangeLog.Record(serviceName, SafeDisplayName(sc), before, startType);
                    return true;
                }
                catch { return false; }
            });
        }

        private static string SafeDisplayName(ServiceController sc)
        {
            try { return sc.DisplayName; } catch { return sc.ServiceName; }
        }

        public async Task<bool> StartServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> StopServiceAsync(string serviceName)
        {
            if (IsCritical(serviceName))
                return false; // silently blocked — UI should never offer Stop on critical services

            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    return true;
                }
                catch { return false; }
            });
        }
    }

    // Helper to change start mode via Win32 API (ServiceController doesn't expose it directly)
    internal static class ServiceHelper
    {
        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool ChangeServiceConfig(
            IntPtr hService, uint nServiceType, uint nStartType, uint nErrorControl,
            string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
            string? lpDependencies, string? lpServiceStartName, string? lpPassword,
            string? lpDisplayName);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_CHANGE_CONFIG = 0x0002;

        public static void ChangeStartMode(ServiceController svc, ServiceStartMode mode)
        {
            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Exception("Acces refuzat la SCM.");
            try
            {
                var svcHandle = OpenService(scm, svc.ServiceName, SERVICE_CHANGE_CONFIG);
                if (svcHandle == IntPtr.Zero) throw new Exception("Acces refuzat la serviciu.");
                try
                {
                    uint startType = mode switch
                    {
                        ServiceStartMode.Automatic => 2,
                        ServiceStartMode.Manual    => 3,
                        ServiceStartMode.Disabled  => 4,
                        _ => 3
                    };
                    if (!ChangeServiceConfig(svcHandle, SERVICE_NO_CHANGE, startType,
                        SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                        throw new Exception("ChangeServiceConfig eșuat.");
                }
                finally { CloseServiceHandle(svcHandle); }
            }
            finally { CloseServiceHandle(scm); }
        }
    }
}
