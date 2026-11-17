using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    public class TurboBoost
    {
        private static readonly Regex RxGuid = new(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        /// <summary>Turbo off = maximum processor state below 100 % or boost mode "Disabled" (on AC).</summary>
        public bool IsOff { get; private set; } = false;

        // the previous values (active plan), so "On" restores them exactly, not a fixed 100
        private int? _prevAc, _prevDc;

        /// <summary>Reads the real state from Windows (at start and after any change made outside the button).</summary>
        public void RefreshFromSystem()
        {
            try
            {
                int? maxAc = PowerAdvancedService.GetMaxProcessorState(true);
                var boostAc = PowerAdvancedService.GetBoostMode(true);
                IsOff = (maxAc is int m && m < 100) || boostAc == PowerAdvancedService.BoostMode.Disabled;
            }
            catch (Exception ex) { AppLogger.Debug("TurboBoost.RefreshFromSystem: " + ex.Message); }
        }

        public void SetOff()
        {
            if (!IsOff)
            {
                try { _prevAc = PowerAdvancedService.GetMaxProcessorState(true); _prevDc = PowerAdvancedService.GetMaxProcessorState(false); }
                catch { _prevAc = _prevDc = null; }
            }
            SetMaxPercent(99, 99); IsOff = true;
        }

        public void SetOn()
        {
            int ac = _prevAc is >= 1 and <= 100 && _prevAc != 99 ? _prevAc.Value : 100;
            int dc = _prevDc is >= 1 and <= 100 && _prevDc != 99 ? _prevDc.Value : 100;
            SetMaxPercent(ac, dc);
            // Turbo can also be turned off through the boost mode (Power page) — "on" must really mean on.
            try
            {
                if (PowerAdvancedService.GetBoostMode(true) == PowerAdvancedService.BoostMode.Disabled)
                    PowerAdvancedService.SetBoostMode(PowerAdvancedService.BoostMode.Enabled, applyToAc: true, applyToDc: false);
                if (PowerAdvancedService.GetBoostMode(false) == PowerAdvancedService.BoostMode.Disabled)
                    PowerAdvancedService.SetBoostMode(PowerAdvancedService.BoostMode.Enabled, applyToAc: false, applyToDc: true);
            }
            catch (Exception ex) { AppLogger.Debug("TurboBoost.SetOn boost mode: " + ex.Message); }
            IsOff = false;
        }

        private static void SetMaxPercent(int ac, int dc)
        {
            string guid = GetActiveGuid();
            RunPowercfg($"/SETACVALUEINDEX {guid} SUB_PROCESSOR PROCTHROTTLEMAX {ac}");
            RunPowercfg($"/SETDCVALUEINDEX {guid} SUB_PROCESSOR PROCTHROTTLEMAX {dc}");
            RunPowercfg($"/SETACTIVE {guid}");
        }

        private static string GetActiveGuid()
        {
            string output = RunPowercfgOutput("/GETACTIVESCHEME");
            var m = RxGuid.Match(output);
            if (!m.Success) throw new Exception("Schema Power negasita.");
            return m.Value;
        }

        private static string RunPowercfgOutput(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,   // must drain to prevent deadlock
            };
            using var p = Process.Start(psi)!;
            // Drain stdout and stderr concurrently before WaitForExit
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            { try { p.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
            errTask.GetAwaiter().GetResult();
            return outTask.GetAwaiter().GetResult();
        }

        private static void RunPowercfg(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            // Drain to prevent any pipe blocking, even if we don't use the output
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            { try { p.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
            outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
        }
    }
}
