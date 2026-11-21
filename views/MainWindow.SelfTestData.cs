using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Self-test data checks: the same value read from two sources (memory, free space, battery, graphics card name, processor
    /// load and clock) — a big difference points to a wrong reading; and the sensors watched for 20 seconds — values stuck on
    /// one number, outside what is possible, or missing most of the time. Lines start with ✓, ⚠ or ✗ (as in the main report).
    /// </summary>
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

        private static string Wmi(string query, string prop)
        {
            try
            {
                using var s = SMDWin.Services.WmiHelper.Searcher(query, System.TimeSpan.FromSeconds(10));
                using var r = s.Get();
                foreach (ManagementObject o in r) return o[prop]?.ToString() ?? "";
            }
            catch (Exception ex) { AppLogger.Debug($"SelfTestData WMI {prop}: {ex.Message}"); }
            return "";
        }

        internal async Task RunDataChecksAsync(List<string> lines, Action<double>? progress, CancellationToken ct)
        {
            void Ok(string t) => lines.Add("✓ " + t);
            void Warn(string t) => lines.Add("⚠ " + t);
            void Fail(string t) => lines.Add("✗ " + t);

            lines.Add("── TWO SOURCES, SAME VALUE ─────────────────────────────");
            await Task.Run(() =>
            {
                // memory: what Windows can use vs the modules installed
                try
                {
                    var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                    double usable = GlobalMemoryStatusEx(ref m) ? m.TotalPhys / 1073741824.0 : 0;
                    double installed = 0;
                    using (var s = SMDWin.Services.WmiHelper.Searcher("SELECT Capacity FROM Win32_PhysicalMemory", System.TimeSpan.FromSeconds(10)))
                    using (var r = s.Get())
                        foreach (ManagementObject o in r) installed += Convert.ToDouble(o["Capacity"] ?? 0) / 1073741824.0;
                    if (usable <= 0 || installed <= 0) Warn($"RAM: usable {usable:0.0} GB, installed {installed:0.0} GB — one source did not answer");
                    else if (usable > installed + 0.2) Fail($"RAM: usable {usable:0.0} GB is MORE than installed {installed:0.0} GB — module list incomplete");
                    else if (usable < installed * 0.75) Warn($"RAM: usable {usable:0.0} GB of {installed:0.0} GB installed — a lot is reserved (graphics / BIOS)");
                    else Ok($"RAM: usable {usable:0.0} GB of {installed:0.0} GB installed");
                }
                catch (Exception ex) { Warn("RAM check: " + ex.Message); }

                // free space on the system drive: .NET vs WMI
                try
                {
                    string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    double net = new DriveInfo(sys).AvailableFreeSpace / 1073741824.0;
                    string wmiFree = Wmi($"SELECT FreeSpace FROM Win32_LogicalDisk WHERE DeviceID='{sys.TrimEnd('\\')}'", "FreeSpace");
                    double wmi = double.TryParse(wmiFree, out var w) ? w / 1073741824.0 : -1;
                    if (wmi < 0) Warn($"Free space {sys}: {net:0.0} GB (WMI did not answer)");
                    else if (Math.Abs(net - wmi) > Math.Max(1, net * 0.02)) Warn($"Free space {sys}: {net:0.0} GB vs WMI {wmi:0.0} GB");
                    else Ok($"Free space {sys}: {net:0.0} GB (both sources agree)");
                }
                catch (Exception ex) { Warn("Free space check: " + ex.Message); }

                // battery percentage: Windows power status vs WMI
                try
                {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) Ok("Battery: none (desktop)");
                    else
                    {
                        int a = (int)Math.Round(ps.BatteryLifePercent * 100);
                        string b = Wmi("SELECT EstimatedChargeRemaining FROM Win32_Battery", "EstimatedChargeRemaining");
                        if (!int.TryParse(b, out int bw)) Warn($"Battery: {a} % (WMI did not answer)");
                        else if (Math.Abs(a - bw) > 3) Warn($"Battery: {a} % (Windows) vs {bw} % (WMI)");
                        else Ok($"Battery: {a} % (both sources agree)");
                    }
                }
                catch (Exception ex) { Warn("Battery check: " + ex.Message); }
            }, ct);

            // graphics card name: WMI vs the sensor library
            try
            {
                var names = new List<string>();
                await Task.Run(() =>
                {
                    using var s = SMDWin.Services.WmiHelper.Searcher("SELECT Name FROM Win32_VideoController", System.TimeSpan.FromSeconds(10));
                    using var r = s.Get();
                    foreach (ManagementObject o in r) if (o["Name"]?.ToString() is { Length: > 0 } n) names.Add(n);
                }, ct);
                string sensorGpu = _lastTempSnap?.GpuSensorName ?? "";
                if (names.Count == 0) Warn("Graphics card: WMI lists none");
                else if (sensorGpu.Length < 3 || sensorGpu == "--") Warn($"Graphics card: {string.Join(", ", names)} — the sensors name none");
                else if (!names.Any(n => Similar(n, sensorGpu))) Warn($"Graphics card: WMI \"{string.Join(", ", names)}\" vs sensors \"{sensorGpu}\"");
                else Ok($"Graphics card: {string.Join(", ", names)}");
            }
            catch (Exception ex) { Warn("Graphics card check: " + ex.Message); }
            progress?.Invoke(0.2);

            // ── 20 seconds of sensors ───────────────────────────────────────────────
            lines.Add("");
            lines.Add("── SENSORS WATCHED FOR 20 SECONDS ──────────────────────");
            var series = new Dictionary<string, List<float?>>();
            var winLoad = new List<float>();
            var winClock = new List<float>();
            System.Diagnostics.PerformanceCounter? pcUtil = null, pcPerf = null;
            float baseMhz = 0;
            try
            {
                pcUtil = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                pcPerf = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                pcUtil.NextValue(); pcPerf.NextValue();
                float.TryParse(Wmi("SELECT MaxClockSpeed FROM Win32_Processor", "MaxClockSpeed"), out baseMhz);
            }
            catch (Exception ex) { AppLogger.Debug($"SelfTestData counters: {ex.Message}"); }

            void Rec(string name, float? v)
            {
                if (!series.TryGetValue(name, out var l)) series[name] = l = new List<float?>();
                l.Add(v);
            }
            for (int i = 0; i < 20; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                var sn = _lastTempSnap;
                Rec("CPU temperature °C", sn?.CpuTemp);
                Rec("CPU clock MHz", sn?.CpuFreqMHz);
                Rec("CPU load %", sn?.CpuLoadPct);
                Rec("CPU power W", sn?.CpuPowerW);
                Rec("CPU voltage V", sn?.CpuVCoreV);
                Rec("GPU temperature °C", sn?.GpuTemp);
                Rec("GPU load %", sn?.GpuLoadPct);
                Rec("GPU power W", sn?.GpuPowerW);
                if (sn?.FanSpeeds is { Count: > 0 } fans) Rec("Fan RPM (fastest)", fans.Max(f => f.Rpm));
                try
                {
                    if (pcUtil != null) winLoad.Add(Math.Min(100, pcUtil.NextValue()));
                    if (pcPerf != null && baseMhz > 0) winClock.Add(baseMhz * pcPerf.NextValue() / 100f);
                }
                catch (Exception ex) { AppLogger.Debug($"SelfTestData counter read: {ex.Message}"); }
                progress?.Invoke(0.2 + 0.8 * (i + 1) / 20.0);
            }
            pcUtil?.Dispose(); pcPerf?.Dispose();

            var limits = new Dictionary<string, (float Lo, float Hi)>
            {
                ["CPU temperature °C"] = (5, 115), ["GPU temperature °C"] = (5, 115), ["CPU clock MHz"] = (200, 6800),
                ["CPU load %"] = (0, 100), ["GPU load %"] = (0, 100), ["CPU power W"] = (0.1f, 400), ["GPU power W"] = (0.1f, 600),
                ["CPU voltage V"] = (0.3f, 1.8f), ["Fan RPM (fastest)"] = (0, 9000),
            };
            var loadVals = series.TryGetValue("CPU load %", out var lv) ? lv.Where(v => v.HasValue).Select(v => v!.Value).ToList() : new List<float>();
            bool loadMoved = loadVals.Count > 3 && loadVals.Max() - loadVals.Min() > 10;
            foreach (var (name, vals) in series)
            {
                var real = vals.Where(v => v.HasValue && float.IsFinite(v.Value)).Select(v => v!.Value).ToList();
                int missing = vals.Count - real.Count;
                if (real.Count == 0) { lines.Add($"· {name}: not read on this computer"); continue; }
                float mn = real.Min(), mx = real.Max();
                string range = $"{mn:0.##} – {mx:0.##}";
                var (lo, hi) = limits.TryGetValue(name, out var l) ? l : (float.MinValue, float.MaxValue);
                if (mn < lo || mx > hi) Fail($"{name}: {range} — outside what is possible ({lo:0.##}–{hi:0.##})");
                else if (missing > vals.Count / 2) Warn($"{name}: {range} — missing in {missing} of {vals.Count} readings");
                // only the processor sensors are compared with the processor load: an idle graphics card (on a laptop, often
                // switched off by Windows) rightly keeps the same values while the processor works
                else if (mx - mn < 0.001f && loadMoved && name.StartsWith("CPU") && !name.StartsWith("CPU load") && !name.Contains("voltage"))
                    Warn($"{name}: stuck on {mn:0.##} for 20 s while the load moved ({loadVals.Min():0}–{loadVals.Max():0} %)");
                else if (mx - mn < 0.001f && name.StartsWith("GPU") && !name.StartsWith("GPU load"))
                    Ok($"{name}: {range} — unchanged for 20 s (the graphics card was idle; on a laptop it may be switched off)");
                else Ok($"{name}: {range}{(missing > 0 ? $" ({missing} missing)" : "")}");
            }

            // the processor load and clock against Windows' own counters (the ones Task Manager shows)
            if (loadVals.Count > 5 && winLoad.Count > 5)
            {
                double diff = Math.Abs(loadVals.Average() - winLoad.Average());
                string t = $"CPU load: sensors {loadVals.Average():0} % vs Windows {winLoad.Average():0} % (20 s average)";
                if (diff > 20) Warn(t + " — large difference"); else Ok(t);
            }
            var clockVals = series.TryGetValue("CPU clock MHz", out var cv) ? cv.Where(v => v.HasValue).Select(v => v!.Value).ToList() : new List<float>();
            if (clockVals.Count > 5 && winClock.Count > 5 && winClock.Average() > 0)
            {
                double ratio = clockVals.Average() / winClock.Average();
                string t = $"CPU clock: shown {clockVals.Average():0} MHz vs Windows {winClock.Average():0} MHz";
                if (ratio > 1.35 || ratio < 0.6) Warn(t + $" — {ratio:0.00}× apart"); else Ok(t);
            }
        }

        private static bool Similar(string a, string b)
        {
            static string Norm(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray())
                .Replace("NVIDIA", "").Replace("GEFORCE", "").Replace("AMD", "").Replace("RADEON", "").Replace("INTEL", "").Replace("TM", "").Replace("R", "");
            string x = Norm(a), y = Norm(b);
            return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
        }
    }
}
