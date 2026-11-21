using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The drawing of the Benchmark page (DeviceSketch): which computer to draw — from the chassis type Windows reports,
    /// the maker / model name and the battery — and what each callout says in each moment of the test.
    /// </summary>
    public partial class MainWindow
    {
        private static DeviceSketch.Kind? _benchKindCache;
        private static bool _benchOptical;

        /// <summary>
        /// What the drawing shows as fitted: the memory modules and slots (RAM page list), the drives inside the computer (not USB /
        /// SD / virtual): an M.2 drive on the board, the others in bays (3.5" hard disks, 2.5" drives), and the optical drive.
        /// The storage line points at the drive Windows runs from. Until the lists are read, the default drawing stays.
        /// </summary>
        private DeviceSketch.Hardware BenchFitted()
        {
            var hw = new DeviceSketch.Hardware { Optical = _benchOptical };
            var mods = _ramModules ?? new();
            int fitted = mods.Count(r => !r.IsEmpty);
            if (fitted > 0) { hw.RamModules = fitted; hw.RamSlots = Math.Max(fitted, mods.Count); }
            var disks = (_allDisks ?? new()).Where(d =>
                    !(d.Bus is "USB" or "SD" or "Virtual") && !d.Kind.StartsWith("External", StringComparison.OrdinalIgnoreCase)
                    && !d.Kind.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && d.Kind is not ("SD card" or "Virtual disk"))
                .ToList();
            if (disks.Count == 0) return hw;   // not read yet: the default (one M.2 drive)
            bool OnBoard(DiskHealthEntry d) => d.Bus == "NVMe" || d.FormFactor == "M.2" || d.Kind == "NVMe SSD";
            bool IsSys(DiskHealthEntry d) => d.Partitions.Any(p => p.IsSystem || string.Equals(p.Letter?.TrimEnd(':', '\\'), "C", StringComparison.OrdinalIgnoreCase));
            var sys = disks.FirstOrDefault(IsSys) ?? disks[0];
            var board = disks.Where(OnBoard).ToList();
            hw.Nvme = board.Count > 0;
            hw.SystemOnNvme = OnBoard(sys);
            hw.BayDrives = disks.Where(d => !OnBoard(d))
                                .OrderBy(d => d == sys ? 0 : 1)
                                .Select(d => d.Kind == "HDD" && d.FormFactor != "2.5\"")
                                .ToList();
            return hw;
        }

        /// <summary>Finds the kind of computer once (WMI on a background thread) and sets the drawing.</summary>
        private async Task DetectBenchDeviceKindAsync()
        {
            try
            {
                if (_benchKindCache is not DeviceSketch.Kind kind)
                {
                    string maker = _summary?.Manufacturer ?? "", model = _summary?.Model ?? "";
                    bool battery = BenchHasBattery();
                    kind = await Task.Run(() =>
                    {
                        ushort[]? chassis = null;
                        try
                        {
                            using var q = WmiHelper.Searcher("SELECT ChassisTypes FROM Win32_SystemEnclosure", TimeSpan.FromSeconds(8));
                            using var col = q.Get();
                            chassis = col.Cast<ManagementObject>().FirstOrDefault()?["ChassisTypes"] as ushort[];
                        }
                        catch (Exception ex) { AppLogger.Debug($"Benchmark chassis: {ex.Message}"); }
                        // an optical drive (DVD / Blu-ray) is drawn only when Windows sees one
                        try
                        {
                            using var cd = WmiHelper.Searcher("SELECT DeviceID FROM Win32_CDROMDrive", TimeSpan.FromSeconds(6));
                            using var cdc = cd.Get();
                            _benchOptical = cdc.Count > 0;
                        }
                        catch (Exception ex) { AppLogger.Debug($"Benchmark optical drive: {ex.Message}"); _benchOptical = false; }
                        var ck = ComputerPackage.KindFor(chassis, maker, model, battery);
                        AppLogger.Info("Benchmark: drawing for {0} (chassis {1}, battery {2})", ck, chassis is { Length: > 0 } ? string.Join(",", chassis) : "?", battery);
                        return DeviceSketch.KindFor(ck);
                    });
                    _benchKindCache = kind;
                }
                if (_benchSketch != null) _benchSketch.DeviceKind = kind;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DetectBenchDeviceKindAsync"); }
        }

        private static readonly System.Text.RegularExpressions.Regex LiveRx = new(
            @"(\d+(?:[.,]\d+)?)\s*(GHz|FPS|fps|MB/s|GB/s|MT/s|pts|Mpx/s|GFLOPS)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The number the small chart of the tested part follows: the first value with a unit (GHz, FPS, MB/s…) in its live text.</summary>
        private static double? LiveNumber(string text)
        {
            var m = LiveRx.Match(text ?? "");
            if (!m.Success) return null;
            return double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        private static bool BenchHasBattery()
        {
            try
            {
                var st = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
                return (st & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && st != System.Windows.Forms.BatteryChargeStatus.Unknown;
            }
            catch { return false; }
        }

        /// <summary>
        /// The callouts of the drawing from the state of the page: before the test the hardware only; during the test the
        /// part being tested in orange with its live value, the next ones "next", the finished ones with their mark;
        /// after the test every part with its mark and main value.
        /// </summary>
        private void UpdateBenchSketch()
        {
            var sk = _benchSketch;
            if (sk == null) return;
            string gpu = BenchGpuName() is { Length: > 0 } chosen ? chosen : _summary?.GpuName ?? "";
            sk.DedicatedGpu = gpu.Length == 0 || !GpuPackage.IsIntegrated(gpu);
            try { sk.Fitted = BenchFitted(); } catch (Exception ex) { AppLogger.Debug($"Benchmark fitted parts: {ex.Message}"); }
            for (int i = 0; i < 4; i++)
            {
                var c = _benchComps[i];
                if (c == null) continue;
                var co = sk.Parts[i];
                bool now = _benchRunning && _benchStep == i;
                bool next = _benchRunning && _benchStep >= 0 && BenchPos(_benchStep) < BenchPos(i);
                co.Mark = c.Mark;
                co.State = now ? DeviceSketch.PartState.Active
                         : next ? DeviceSketch.PartState.Next
                         : c.Mark != null ? DeviceSketch.PartState.Done
                         : DeviceSketch.PartState.Idle;
                co.Title = now ? PT("{0} · in test", c.Name) : c.Name;
                co.Hardware = c.Device;
                string? main = c.Values.Length > c.MainTest ? c.Values[c.MainTest] : null;
                if (now)
                    co.Value = string.Join(" · ", new[] { _benchHudLeft.Length > 0 ? _benchHudLeft : _benchSub, _benchHudRight }.Where(x => !string.IsNullOrWhiteSpace(x)));
                else if (next)
                    co.Value = PT("next");
                else if (c.Mark != null && main != null)
                    co.Value = $"{main} · {c.Tests[c.MainTest]}";
                else
                    co.Value = "";
                co.Live = now ? LiveNumber(co.Value) : null;
            }
            sk.Refresh();
        }
    }
}
