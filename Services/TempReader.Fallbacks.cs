using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{
    public partial class TempReader
    {
        // ── Method 2: OpenHardwareMonitor WMI (if installed) ───────────────
        private static bool _ohmWmiFailed = false;
        private static bool _wmiThermalFailed = false;

        private static void TryOhmWmi(TempSnapshot snap)
        {
            if (_ohmWmiFailed) return;
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\OpenHardwareMonitor",
                    "SELECT * FROM Sensor WHERE SensorType='Temperature'");
                using (var __wmiTmp1 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp1)
                {
                    try
                    {
                        string name  = o["Name"]?.ToString() ?? "";
                        string hwType= o["Parent"]?.ToString() ?? "";
                        float  val   = Convert.ToSingle(o["Value"]);
                        if (val <= 0 || val > 150) continue;

                        string nu = name.ToUpper();
                        string hu = hwType.ToUpper();

                        if (!snap.CpuTemp.HasValue &&
                            (nu.Contains("CPU") || nu.Contains("PACKAGE") ||
                             nu.Contains("CORE") || hu.Contains("CPU")))
                        {
                            snap.CpuTemp       = val;
                            snap.CpuSensorName = name + " (OHM)";
                        }
                        if (!snap.GpuTemp.HasValue &&
                            (nu.Contains("GPU") || hu.Contains("GPU")))
                        {
                            snap.GpuTemp       = val;
                            snap.GpuSensorName = name + " (OHM)";
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                }
                }
            }
            catch (Exception ex)
            {
                _ohmWmiFailed = true;
                AppLogger.Warning(ex, "TempReader.TryOhmWmi (disabled)");
                _gpuWmiPermFailed = true; // also disable GPU WMI fallback
            }
        }

        // ── Metoda 3: WMI ThermalZone (aproximativ, dar universal) ───────────
        private static void TryWmiThermal(TempSnapshot snap)
        {
            if (_wmiThermalFailed) return;
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                var temps = new List<float>();
                using (var __wmiTmp2 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp2)
                {
                    try
                    {
                        float t = (float)(Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15);
                        if (t > 10 && t < 110) temps.Add(t);
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader.TryWmiThermal"); }
                }
                }
                if (temps.Count > 0)
                {
                    // ThermalZone gives the minimum temperature (closer to idle); the maximum is more relevant for CPU load
                    snap.CpuTemp       = temps.Max();
                    snap.CpuSensorName = "ThermalZone (" + temps.Count + " zone, ~aprox)";
                    if (snap.Backend.StartsWith("none"))
                        snap.Backend = "WMI-ThermalZone";
                }
            }
            catch (Exception ex)
            {
                _wmiThermalFailed = true;
                AppLogger.Warning(ex, "TempReader.TryWmiThermal (disabled)");
            }
        }

        // ── ThermalZone that never moves ─────────────────────────────────────────
        // A zone is shown only after its value has changed at least once in this session: a number that never moved cannot
        // be told apart from a placeholder, and "—" is better than a wrong temperature.
        // On some laptops (seen on an ASUS Vivobook X1504MA) the ACPI zone returns one fixed number (24 °C) whatever the load:
        // it is a placeholder of the firmware, not a sensor. When the value stays exactly the same for 90 s while the
        // processor load moves by 40 points or more, it is not shown (the temperature reads "—" instead of a frozen number).
        // A later change of the value makes it trusted again.
        private float? _tzLastValue;
        private DateTime _tzSameSinceUtc;
        private float _tzLoadMin = float.MaxValue, _tzLoadMax = float.MinValue;
        private bool _tzFrozen;
        private bool _tzEverMoved;
        private static readonly TimeSpan TzFrozenAfter = TimeSpan.FromSeconds(90);
        private const float TzLoadSpreadMin = 40f;

        private void CheckThermalZoneFrozen(TempSnapshot snap)
        {
            if (!snap.CpuTemp.HasValue || !snap.CpuSensorName.StartsWith("ThermalZone", StringComparison.Ordinal)) return;
            float t = snap.CpuTemp.Value;
            var now = DateTime.UtcNow;
            if (!_tzLastValue.HasValue || Math.Abs(t - _tzLastValue.Value) > 0.05f)
            {
                if (_tzLastValue.HasValue && !_tzEverMoved)
                {
                    _tzEverMoved = true;
                    AppLogger.Info("ThermalZone: the value changed ({0:F1} → {1:F1} °C) — it is a live reading, shown from now on", _tzLastValue.Value, t);
                }
                if (_tzFrozen) AppLogger.Info("ThermalZone: the value moves again ({0:F1} °C) — shown again", t);
                _tzFrozen = false;
                _tzLastValue = t;
                _tzSameSinceUtc = now;
                _tzLoadMin = float.MaxValue; _tzLoadMax = float.MinValue;
            }
            if (snap.CpuLoadPct is float load)
            {
                _tzLoadMin = Math.Min(_tzLoadMin, load);
                _tzLoadMax = Math.Max(_tzLoadMax, load);
            }
            if (!_tzFrozen && now - _tzSameSinceUtc >= TzFrozenAfter && _tzLoadMax - _tzLoadMin >= TzLoadSpreadMin)
            {
                _tzFrozen = true;
                AppLogger.Warning("ThermalZone: fixed at {0:F1} °C for {1:F0} s while the load moved {2:F0}–{3:F0} % — a firmware placeholder, not shown",
                    t, (now - _tzSameSinceUtc).TotalSeconds, _tzLoadMin, _tzLoadMax);
            }
            if (_tzFrozen || !_tzEverMoved)
            {
                snap.CpuTemp = null;
                snap.CpuSensorName = "--";
            }
        }

        private static int CpuScore(string name)
        {
            string n = name.ToLower();
            if (n.Contains("gpu") || n.Contains("hot spot") || n.Contains("ambient")) return -1;
            if (n.Contains("core max"))  return 105;
            if (n.Contains("package"))   return 115;
            if (n.Contains("tdie") || n.Contains("tctl")) return 110;
            if (n.Contains("ccd"))       return 100;
            if (n.Contains("core") && (n.Contains("avg") || n.Contains("average"))) return 95;
            if (n.Contains("core"))      return 60;
            if (n.Contains("cpu"))       return 70;
            return 10;
        }

        private static int GpuScore(string name)
        {
            string n = name.ToLower();
            if (n.Contains("package") || n.Contains("tdie") || n.Contains("ambient")) return -1;
            // AMD Radeon reports "Edge" (die surface, ~60°C) and "Junction" (hotspot, ~80°C).
            // We want Edge for display — it's the real operating temperature, not the peak hotspot.
            // Junction/Hotspot is captured separately into snap.GpuHotSpot (line ~519).
            if (n.Contains("edge"))                                                       return 130;
            if (n.Contains("gpu core") || (n.Contains("core") && n.Contains("gpu")))     return 110;
            if (n == "gpu temperature" || n == "gpu")                                     return 100;
            if (n.Contains("memory") && n.Contains("temp"))                               return  90;
            if (n.Contains("gpu"))                                                        return  80;
            // Hotspot / Junction stays low-priority — goes to GpuHotSpot, not display temp
            if (n.Contains("hot spot") || n.Contains("hotspot") || n.Contains("junction")) return  40;
            return 10;
        }

        private static bool _gpuWmiPermFailed = false;
        private static int _gpuWmiMissCount = 0;

        private void TryGpuWmiFallback(TempSnapshot snap)
        {
            if (_gpuWmiPermFailed) return;

            if (_gpuWmiMissCount >= 5) return;

            // Try re-scanning LHM hardware tree more aggressively (SubHardware of SubHardware)
            if (_computer != null)
            {
                try
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        try
                        {
                            if (hw == null) continue;
                            hw.Update();
                            bool isGpu = hw.HardwareType == HardwareType.GpuNvidia
                                      || hw.HardwareType == HardwareType.GpuAmd
                                      || hw.HardwareType == HardwareType.GpuIntel;
                            if (!isGpu) continue;

                            // Check all sensors including deeply nested SubHardware
                            var allSensors = hw.Sensors.ToList();
                            foreach (var sub in hw.SubHardware)
                            {
                                try { sub.Update(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                                allSensors.AddRange(sub.Sensors);
                                foreach (var sub2 in sub.SubHardware)
                                {
                                    try { sub2.Update(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                                    allSensors.AddRange(sub2.Sensors);
                                }
                            }

                            foreach (var s in allSensors)
                            {
                                if (s?.SensorType != SensorType.Temperature) continue;
                                if (!s.Value.HasValue || !SensorValidation.IsPlausibleTemp(s.Value.Value)) continue;
                                int sg = GpuScore(s.Name ?? "");
                                if (sg >= 0)
                                {
                                    snap.GpuTemp = s.Value.Value;
                                    snap.GpuSensorName = (s.Name ?? "GPU") + " (deep)";
                                    return;
                                }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }

            // Last resort: OpenHardwareMonitor WMI namespace for GPU specifically
            bool found = false;
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\OpenHardwareMonitor",
                    "SELECT * FROM Sensor WHERE SensorType='Temperature'");
                using (var __wmiTmp3 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp3)
                {
                    try
                    {
                        string name   = o["Name"]?.ToString() ?? "";
                        string parent = o["Parent"]?.ToString() ?? "";
                        float  val    = Convert.ToSingle(o["Value"]);
                        if (val <= 0 || val > 150) continue;
                        string nu = name.ToUpper(), pu = parent.ToUpper();
                        if (nu.Contains("GPU") || pu.Contains("GPU") || pu.Contains("NVIDIA") || pu.Contains("AMD"))
                        {
                            snap.GpuTemp = val;
                            snap.GpuSensorName = name + " (OHM-GPU)";
                            found = true;
                            _gpuWmiMissCount = 0; // success — reset the counter
                            return;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                }
                }
                if (!found)
                {
                    _gpuWmiMissCount++;
                    if (_gpuWmiMissCount >= 5)
                        AppLogger.Info("TempReader.WmiFallback: no matching GPU sensor found in root\\OpenHardwareMonitor after 5 attempts — disabling this fallback for the rest of the session.");
                }
            }
            catch (Exception ex)
            {
                if (ex is System.Management.ManagementException me &&
                    (me.Message.Contains("Invalid namespace") || me.Message.Contains("Not supported")))
                {
                    _gpuWmiPermFailed = true;
                    AppLogger.Info("TempReader.WmiFallback: root\\OpenHardwareMonitor namespace not available (OHM not installed) — GPU WMI fallback disabled for the rest of the session.");
                }
                else
                {
                    AppLogger.Warning(ex, "TempReader");
                }
            }
        }


        private static volatile bool _smiProvidesTemp = true;
        private static volatile bool _smiProvidesFan  = true;
        private static volatile bool _smiProvidesLoad = true;
        private static volatile bool _smiProvidesFreq = true;

        internal static void TryNvidiaSmiExtended(TempSnapshot snap)
        {
            try
            {
                string[] paths = {
                    @"C:\Windows\System32\nvidia-smi.exe",
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
                };
                string? smiPath = paths.FirstOrDefault(System.IO.File.Exists);
                if (smiPath == null)
                {
                    _smiProvidesTemp = _smiProvidesFan = _smiProvidesLoad = _smiProvidesFreq = false;
                    return;
                }

                // Query: temp, fan%, utilization%, clock MHz, power W in one call
                var psi = new System.Diagnostics.ProcessStartInfo(smiPath,
                    "--query-gpu=temperature.gpu,fan.speed,utilization.gpu,clocks.current.graphics,power.draw --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,   // prevent stderr blocking stdout pipe
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return;

                // Hard 3-second timeout — kill if hung (driver crash, virtualized GPU, etc.)
                string line = "";
                if (proc.WaitForExit(3000))
                {
                    line = proc.StandardOutput.ReadLine()?.Trim() ?? "";
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    return;
                }

                var parts = line.Split(',');
                if (parts.Length < 5) return;

                _smiProvidesTemp = float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float capT) && capT > 0;
                // Fan: the only criterion is a successful parse — 0% is a legitimate reading (fan stopped at idle on semi-passive GPUs),
                // "[N/A]" is the one that does not parse.
                _smiProvidesFan  = float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _);
                _smiProvidesLoad = float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _);
                _smiProvidesFreq = float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float capC) && capC > 0;

                if (!snap.GpuTemp.HasValue && float.TryParse(parts[0].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float temp) && temp > 0)
                { snap.GpuTemp = temp; snap.GpuSensorName = "nvidia-smi"; }

                // nvidia-smi gives the fan speed in PERCENT, not RPM — it used to be added to FanSpeeds and shown as "35 RPM";
                // it is no longer shown as a fan speed

                if (!snap.GpuLoadPct.HasValue && float.TryParse(parts[2].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float load))
                    snap.GpuLoadPct = load;

                if (!snap.GpuFreqMHz.HasValue && float.TryParse(parts[3].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float clock) && clock > 0)
                    snap.GpuFreqMHz = clock;

                if (!snap.GpuPowerW.HasValue && float.TryParse(parts[4].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float power) && power > 0)
                    snap.GpuPowerW = power;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 6: AMD radeonsmi / amdgpu-ls ──────────────────────────────
        // Radeon Software (ReLive / Adrenalin) installs a CLI at a known path
        private static void TryAmdSmi(TempSnapshot snap)
        {
            try
            {
                string[] candidates = {
                    @"C:\Program Files\AMD\CNext\CNext\amd-smi.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMDRyzenMasterSDK.dll", // existence check only
                };
                // Try amd-smi first (AMD Software Adrenalin 2023+)
                string? smiPath = candidates[0];
                if (!System.IO.File.Exists(smiPath)) smiPath = null;

                if (smiPath != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(smiPath,
                        "metric --gpu 0 --json")
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null && proc.WaitForExit(3000))
                    {
                        string json = proc.StandardOutput.ReadToEnd();
                        // Parse simple fields from JSON without adding a dependency
                        float? ParseJsonFloat(string text, string key)
                        {
                            int idx = text.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) return null;
                            int colon = text.IndexOf(':', idx);
                            if (colon < 0) return null;
                            int start = colon + 1;
                            while (start < text.Length && (text[start] == ' ' || text[start] == '"')) start++;
                            int end = start;
                            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
                            if (end > start && float.TryParse(text.Substring(start, end - start),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
                                return v;
                            return null;
                        }
                        if (!snap.GpuTemp.HasValue)
                        {
                            var t = ParseJsonFloat(json, "temperature_edge") ?? ParseJsonFloat(json, "temp");
                            if (t.HasValue && t > 0 && t < 120)
                            { snap.GpuTemp = t; snap.GpuSensorName = "amd-smi"; }
                        }
                        if (snap.FanSpeeds.Count == 0)
                        {
                            var rpm = ParseJsonFloat(json, "fan_speed");
                            if (rpm.HasValue && rpm > 0)
                                snap.FanSpeeds.Add(("GPU Fan (amd-smi)", rpm.Value));
                        }
                        if (!snap.GpuLoadPct.HasValue)
                        {
                            var load = ParseJsonFloat(json, "gfx_activity");
                            if (load.HasValue) snap.GpuLoadPct = load;
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            // Fallback: try rocm-smi (AMD ROCm, rare on consumer but worth trying)
            if (!snap.GpuTemp.HasValue)
            {
                try
                {
                    string rocm = @"C:\Program Files\ROCm\bin\rocm-smi.exe";
                    if (System.IO.File.Exists(rocm))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(rocm, "--showtemp --csv")
                        {
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            UseShellExecute = false, CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null && proc.WaitForExit(3000))
                        {
                            foreach (var ln in proc.StandardOutput.ReadToEnd().Split('\n'))
                            {
                                var parts = ln.Trim().Split(',');
                                if (parts.Length >= 2 &&
                                    float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float t)
                                    && t > 0 && t < 120)
                                { snap.GpuTemp = t; snap.GpuSensorName = "rocm-smi"; break; }
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }
        }

        private static bool _igclFirstCallDone;

        private static void TryIntelIgcl(TempSnapshot snap)
        {
            if (SensorStartGuard.SafeMode) return;   // native library: not loaded after a crashed start
            IReadOnlyList<IgclGpuSample> samples;
            if (!_igclFirstCallDone)
            {
                // the first call loads Intel's native library: guarded like the LibreHardwareMonitor open
                SensorStartGuard.Begin("the Intel graphics library (IGCL)");
                try { samples = IgclService.ReadAll(); }
                finally { _igclFirstCallDone = true; SensorStartGuard.End("the Intel graphics library (IGCL)"); }
            }
            else samples = IgclService.ReadAll();   // empty list if IGCL is unavailable (negative cache, zero cost)
            if (samples.Count == 0) return;

            // "Primary" Intel sample: the discrete card (Arc) is preferred, then the integrated one.
            var primary    = samples.FirstOrDefault(s => !s.IsIntegrated) ?? samples[0];
            var integrated = samples.FirstOrDefault(s => s.IsIntegrated);

            // Is the main GPU slot free or held only by a weak "hint" (WMI)?
            bool slotFreeOrWeak = (!snap.GpuTemp.HasValue && !snap.GpuLoadPct.HasValue)
                               || snap.GpuSensorName == "--"
                               || snap.GpuSensorName.Contains("WMI",   StringComparison.OrdinalIgnoreCase)
                               || snap.GpuSensorName.Contains("Intel", StringComparison.OrdinalIgnoreCase);

            if (slotFreeOrWeak)
            {
                if (!snap.GpuTemp.HasValue && primary.TempC.HasValue)
                {
                    snap.GpuTemp       = primary.TempC;
                    snap.GpuSensorName = primary.Name + " (IGCL)";
                }
                if (!snap.GpuLoadPct.HasValue    && primary.LoadPct.HasValue)     snap.GpuLoadPct     = primary.LoadPct;
                if (!snap.GpuFreqMHz.HasValue    && primary.FreqMHz.HasValue)     snap.GpuFreqMHz     = primary.FreqMHz;
                if (!snap.GpuPowerW.HasValue     && primary.PowerW.HasValue)      snap.GpuPowerW      = primary.PowerW;
                if (!snap.GpuMemFreqMHz.HasValue && primary.VramFreqMHz.HasValue) snap.GpuMemFreqMHz  = primary.VramFreqMHz;

                // Thermal throttling reported directly by the hardware (Arc).
                if (primary.ThermalThrottle && !snap.GpuThrottled)
                {
                    snap.GpuThrottled = true;
                    if (string.IsNullOrEmpty(snap.ThrottleReason))
                        snap.ThrottleReason = "GPU temperature limit (IGCL)";
                }

                // Fans (discrete Arc) — no duplicates.
                foreach (var f in primary.Fans)
                    if (!snap.FanSpeeds.Any(x => x.Name == f.Name))
                        snap.FanSpeeds.Add(f);

                // iGPU fallback: temperature ≈ CPU package (same die).
                if (!snap.GpuTemp.HasValue && primary.IsIntegrated && snap.CpuTemp.HasValue)
                {
                    snap.GpuTemp       = snap.CpuTemp;
                    snap.GpuSensorName = primary.Name + " (≈ CPU pkg)";
                }
            }
            else if (integrated != null)
            {
                // Hybrid system: the main slot is held by NVIDIA/AMD — the integrated Intel fills the fields LHM has not populated yet
                // (nothing is overwritten, only gaps are filled).
                if (!snap.HasIntegratedGpu)
                {
                    snap.HasIntegratedGpu  = true;
                    snap.IntegratedGpuName = integrated.Name;
                }
                if (!snap.IntegratedGpuTemp.HasValue)
                    snap.IntegratedGpuTemp = integrated.TempC ?? snap.CpuTemp; // ≈ CPU package if IGCL gives no temperature
                if (!snap.IntegratedGpuLoadPct.HasValue && integrated.LoadPct.HasValue)
                    snap.IntegratedGpuLoadPct = integrated.LoadPct;
            }
        }

        private static bool? _intelIgpuOnlyCached;
        private static bool IsIntelIgpuOnlySystem()
        {
            if (_intelIgpuOnlyCached.HasValue) return _intelIgpuOnlyCached.Value;
            bool hasIntelIgpu = false, hasOtherGpu = false;
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name FROM Win32_VideoController");
                using (var __wmiTmp4 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp4)
                {
                    string n = o["Name"]?.ToString() ?? "";
                    if (n.Length == 0) continue;
                    // Microsoft Basic Display / virtual ones (RDP, Meet etc.) do not count
                    if (n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Virtual",   StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Remote",    StringComparison.OrdinalIgnoreCase)) continue;
                    bool isIntel = n.Contains("Intel", StringComparison.OrdinalIgnoreCase);
                    bool isIgpu  = isIntel && (n.Contains("HD Graphics",  StringComparison.OrdinalIgnoreCase) ||
                                               n.Contains("UHD",          StringComparison.OrdinalIgnoreCase) ||
                                               n.Contains("Iris",         StringComparison.OrdinalIgnoreCase) ||
                                               n.Contains("Graphics",     StringComparison.OrdinalIgnoreCase));
                    // Intel Arc = a DISCRETE card — does not qualify for "≈ CPU package"
                    if (isIntel && n.Contains("Arc", StringComparison.OrdinalIgnoreCase)) { hasOtherGpu = true; continue; }
                    if (isIgpu) hasIntelIgpu = true;
                    else        hasOtherGpu  = true;   // NVIDIA / AMD / Arc
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"IsIntelIgpuOnlySystem: {ex.Message}"); }
            _intelIgpuOnlyCached = hasIntelIgpu && !hasOtherGpu;
            if (_intelIgpuOnlyCached.Value)
                AppLogger.Info("TempReader: Intel iGPU-only system detected — the GPU temperature uses the ≈ CPU package fallback when no other source is available.");
            return _intelIgpuOnlyCached.Value;
        }

        private static bool   _wmiGpuNameQueried = false;
        private static string _wmiGpuNameCached  = "";

        private static void TryWmiGpuLoad(TempSnapshot snap)
        {
            if (!_wmiGpuNameQueried)
            {
                _wmiGpuNameQueried = true;
                try
                {
                    using var s = WmiHelper.Searcher(
                        "SELECT Name FROM Win32_VideoController");
                    using (var __wmiTmp5 = s.Get())
                    {
                        foreach (ManagementObject o in __wmiTmp5)
                    {
                        try
                        {
                            string name = o["Name"]?.ToString() ?? "";
                            if (name.Contains("Intel") || name.Contains("AMD") || name.Contains("NVIDIA"))
                            { _wmiGpuNameCached = name; break; }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }

            // Apply from the cache — zero cost on later ticks
            if (!snap.GpuTemp.HasValue && snap.GpuSensorName == "--" && _wmiGpuNameCached.Length > 0)
                snap.GpuSensorName = _wmiGpuNameCached + " (WMI)";
        }

        private static int _wmiFanMissCount = 0;

        private static void TryWmiFan(TempSnapshot snap)
        {
            if (_wmiFanMissCount >= 3) return;
            bool foundAny = false;
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name, ActiveCooling FROM Win32_Fan");
                using (var __wmiTmp6 = s.Get())
                {
                    foreach (ManagementObject o in __wmiTmp6)
                {
                    try
                    {
                        _ = o["Name"]?.ToString(); // the enumeration itself matters (foundAny); the name is no longer used in the UI
                        foundAny = true;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader.TryWmiFan"); }
                }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            if (foundAny)
                _wmiFanMissCount = 0;
            else
            {
                _wmiFanMissCount++;
                if (_wmiFanMissCount >= 3)
                    AppLogger.Info("TempReader.TryWmiFan: Win32_Fan returned 0 fans 3 times — disabled for the rest of the session.");
            }
        }

        // ── Metoda 9: CPU load via PerformanceCounter ─────────────────────────
        // Instant, no WMI overhead. Used only when LHM doesn't provide load.
        private static System.Diagnostics.PerformanceCounter? _cpuPerfCounter;
        private static readonly object _perfLock = new();
        private static void TryPerfCounterCpuLoad(TempSnapshot snap)
        {
            try
            {
                lock (_perfLock)
                {
                    if (_cpuPerfCounter == null)
                        _cpuPerfCounter = new System.Diagnostics.PerformanceCounter(
                            "Processor", "% Processor Time", "_Total", readOnly: true);
                }
                float v = _cpuPerfCounter!.NextValue();
                if (v >= 0 && v <= 100)
                    snap.CpuLoadPct = v;
            }
            catch
            {
                lock (_perfLock) { _cpuPerfCounter = null; }
            }
        }
    }
}
