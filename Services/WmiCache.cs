using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Caches expensive WMI queries. All static facts run in PARALLEL at startup.
    /// RAM live usage is read via PerformanceCounter — no WMI per tick.
    /// Thread-safe singleton.
    /// </summary>
    public class WmiCache : IDisposable
    {
        public static readonly WmiCache Instance = new();
        private WmiCache() { }

        private readonly SemaphoreSlim _lock = new(1, 1);
        private DateTime _lastRefresh = DateTime.MinValue;
        // Static hardware data (CPU, GPU, OS, RAM capacity, motherboard) never changes
        // at runtime — 24h TTL prevents accidental re-queries while still allowing
        // a forced refresh if ever needed (e.g. after hibernate/resume).
        private readonly TimeSpan _ttl = TimeSpan.FromHours(24);

        // ── Cached static values — volatile backing fields prevent stale reads
        // across threads (properties written from parallel Task.Run, read from UI thread)
        private volatile string _cpuName     = "";
        private volatile string _gpuName     = "";
        private volatile string _osName      = "";
        private volatile string _osBuild     = "";
        private volatile string _osBuildNumber = "";
        private volatile string _totalRam    = "";
        // float is not atomic on 32-bit CLR — use Interlocked via int reinterpret trick
        private int _totalRamGBBits;
        private volatile string _machineName = "";
        private volatile string _motherboard = "";
        private int _hasBatteryInt;   // 0 = false, 1 = true — Interlocked-safe
        private int _isReadyInt;

        public string CpuName     => _cpuName;
        public string GpuName     => _gpuName;
        public string OsName      => _osName;
        public string OsBuild     => _osBuild;
        public string OsBuildNumber => _osBuildNumber;
        public string TotalRam    => _totalRam;
        public float  TotalRamGB
        {
            get => BitConverter.Int32BitsToSingle(Interlocked.CompareExchange(ref _totalRamGBBits, 0, 0));
            private set => Interlocked.Exchange(ref _totalRamGBBits, BitConverter.SingleToInt32Bits(value));
        }
        public string MachineName => _machineName;
        public string Motherboard => _motherboard;
        public bool   HasBattery  => Interlocked.CompareExchange(ref _hasBatteryInt, 0, 0) == 1;
        public bool   IsStale     => DateTime.Now - _lastRefresh > _ttl;
        public bool   IsReady     => Interlocked.CompareExchange(ref _isReadyInt, 0, 0) == 1;

        private PerformanceCounter? _ramAvailMB;
        private long _totalRamMB;

        /// <summary>Returns (usedMB, totalMB) instantly from PerformanceCounter cache.</summary>
        public (long usedMB, long totalMB) GetRamUsage()
        {
            try
            {
                if (_ramAvailMB == null || _totalRamMB == 0)
                    return (0, 0);
                long freeMB = (long)_ramAvailMB.NextValue();
                long used   = Math.Max(0, _totalRamMB - freeMB);
                return (used, _totalRamMB);
            }
            catch { return (0, 0); }
        }

        public async Task RefreshAsync()
        {
            if (!await _lock.WaitAsync(0)) return; // skip if already refreshing
            try
            {
                var tasks = new List<Task>
                {
                    Task.Run(FetchCpu),
                    Task.Run(FetchGpu),
                    Task.Run(FetchOs),
                    Task.Run(FetchRam),
                    Task.Run(FetchMachine),
                    Task.Run(FetchMotherboard),
                    Task.Run(FetchBattery),
                    Task.Run(InitRamCounter),
                };
                await Task.WhenAll(tasks);

                _lastRefresh = DateTime.Now;
                Interlocked.Exchange(ref _isReadyInt, 1);
            }
            finally { _lock.Release(); }
        }

        // ── Individual fetch methods (each catches its own errors) ────────────

        private void FetchCpu()
        {
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name FROM Win32_Processor");
                using (var __wmiTmp1 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp1)
                { _cpuName = o["Name"]?.ToString()?.Trim() ?? ""; break; }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchCpu"); }
        }

        /// <summary>Windows' own fallback and virtual display adapters: never chosen over a real graphics card.</summary>
        internal static bool IsBasicOrVirtualAdapter(string name) =>
            name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase);

        private void FetchGpu()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Name, AdapterRAM, AdapterCompatibility FROM Win32_VideoController");
                string bestName = "";
                long   bestRam  = -1;
                bool   bestIsDedicated = false;
                bool   bestIsBasic = false;

                using (var __wmiTmp2 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp2)
                {
                    string n   = o["Name"]?.ToString()?.Trim() ?? "";
                    long   ram = 0;
                    try { ram = Convert.ToInt64(o["AdapterRAM"]); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    string compat = o["AdapterCompatibility"]?.ToString()?.ToUpperInvariant() ?? "";

                    // Classification: Intel iGPU = integrated, the rest = dedicated
                    bool isDedicated = !compat.Contains("INTEL") &&
                                       !n.ToUpperInvariant().Contains("INTEL");

                    // Preference: a real card over Windows' basic/virtual adapters, then dedicated > integrated,
                    // then more RAM. A laptop can list "Microsoft Basic Display Adapter" next to a working AMD/Intel
                    // card; it used to win the tie as "not Intel = dedicated".
                    if (n.Length == 0) continue;
                    bool isBasic = IsBasicOrVirtualAdapter(n);
                    if (isBasic) isDedicated = false;
                    if (bestName.Length == 0 ||
                        (bestIsBasic && !isBasic) ||
                        (bestIsBasic == isBasic &&
                            ((!bestIsDedicated && isDedicated) ||
                             (bestIsDedicated == isDedicated && ram > bestRam))))
                    {
                        bestName = n;
                        bestRam  = ram;
                        bestIsDedicated = isDedicated;
                        bestIsBasic = isBasic;
                    }
                }
                }
                if (bestName.Length > 0) _gpuName = bestName;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchGpu"); }
        }

        public static string GuessVersionFromBuild(string buildStr)
        {
            if (!int.TryParse(buildStr, out int b)) return "";
            return b switch
            {
                >= 27000 => "25H2",
                >= 26100 => "24H2",
                >= 22631 => "23H2",
                >= 22621 => "22H2",
                >= 22000 => "21H2",   // Windows 11 RTM
                >= 19045 => "22H2",   // Windows 10
                >= 19044 => "21H2",
                >= 19043 => "21H1",
                >= 19042 => "20H2",
                >= 19041 => "2004",
                _ => b > 0 ? $"Build {b}" : "",
            };
        }

        public static string ResolveDisplayVersion(string buildNumber)
        {
            string winVersion = "";
            try
            {
                winVersion = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "DisplayVersion", null)?.ToString() ?? "";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.ResolveDisplayVersion"); }
            return winVersion.Length > 0 ? winVersion : GuessVersionFromBuild(buildNumber);
        }

        private void FetchOs()
        {
            // the registry first: instant and in English on every Windows language; WMI only if it could not be read
            var reg = OsRegistryInfo.Current;
            if (reg.Ok && reg.ProductName.Length > 0)
            {
                _osName = reg.ProductName;
                _osBuildNumber = reg.BuildNumber;
                _osBuild = ResolveDisplayVersion(reg.BuildNumber);
                return;
            }
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
                using (var __wmiTmp3 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp3)
                {
                    _osName  = o["Caption"]?.ToString()?.Trim() ?? "";
                    string arch = o["OSArchitecture"]?.ToString()?.Trim() ?? "";
                    string build = o["BuildNumber"]?.ToString() ?? "";
                    _ = arch; // the architecture is not shown here (it comes from another source in Architecture)
                    _osBuildNumber = build;

                    _osBuild = ResolveDisplayVersion(build);
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        private void FetchRam()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                using (var __wmiTmp4 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp4)
                {
                    ulong kb = Convert.ToUInt64(o["TotalVisibleMemorySize"] ?? 0UL);
                    TotalRamGB = kb / 1_048_576f;
                    int[] stdSizes = { 2,4,6,8,10,12,16,24,32,48,64,128 };
                    int rounded = stdSizes.OrderBy(x => Math.Abs(x - (int)Math.Round(TotalRamGB))).First();
                    _totalRam   = TotalRamGB >= 1 ? $"{rounded} GB" : $"{kb / 1024} MB";
                    _totalRamMB = (long)(kb / 1024);
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        private void FetchMachine()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                using (var __wmiTmp5 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp5)
                {
                    _machineName = $"{o["Manufacturer"]?.ToString()?.Trim()} {o["Model"]?.ToString()?.Trim()}".Trim();
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchMachine"); }
        }

        private void FetchMotherboard()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Manufacturer, Product FROM Win32_BaseBoard");
                using (var __wmiTmp6 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp6)
                {
                    _motherboard = $"{o["Manufacturer"]?.ToString()?.Trim()} {o["Product"]?.ToString()?.Trim()}".Trim();
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchMotherboard"); }
        }

        private void FetchBattery()
        {
            try
            {
                // Use a COUNT-only query — avoids enumerating all battery properties
                using var s = WmiHelper.Searcher(
                    "SELECT DeviceID FROM Win32_Battery");
                bool found = false;
                using (var __wmiTmp7 = s.Get())
                {
                    foreach (ManagementObject _ in __wmiTmp7) { found = true; break; }
                }
                Interlocked.Exchange(ref _hasBatteryInt, found ? 1 : 0);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchBattery"); }
        }

        private void InitRamCounter()
        {
            try
            {
                // Dispose any existing counter to prevent handle leak on repeated refresh
                var old = _ramAvailMB;
                _ramAvailMB = null;
                old?.Dispose();

                // "Available MBytes" is updated by kernel every ~1s — zero WMI overhead
                var pc = new PerformanceCounter("Memory", "Available MBytes", readOnly: true);
                pc.NextValue(); // prime — first read always returns 0
                _ramAvailMB  = pc;
                _totalRamMB  = _totalRamMB > 0 ? _totalRamMB : (long)(TotalRamGB * 1024);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        public void Dispose()
        {
            try { _ramAvailMB?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            _ramAvailMB = null;
        }
    }
}
