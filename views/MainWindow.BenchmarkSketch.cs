using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
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
