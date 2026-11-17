using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// Windows Update blocking in the style of Windows Update Blocker (WUB), without the aggressive registry permission
    /// locking.
    ///
    /// Why "Disabled" on wuauserv is not enough: the Windows Update Medic service (WaaSMedicSvc) repairs it by itself after
    /// a few hours/days, and UsoSvc (Update Orchestrator) starts it when needed. `sc config` is refused for WaaSMedicSvc
    /// (a protected service), but its Start value in the registry can be written by an administrator — WUB does the same.
    ///
    /// Blocking steps (each checked and reported):
    ///   1. save the exact state (Start, DelayedAutostart, FailureActions) — only once;
    ///   2. stop the services; 3. Start = 4 (Disabled) in the registry;
    ///   4. delete FailureActions (automatic restart on error);
    ///   5. disable the scheduled scan tasks (optional — some are protected);
    ///   6. write a restore script to C:\ProgramData\SMDWin, which works even without SMD Win
    ///      (e.g. the app ran from a stick that was removed from the PC).
    /// Unblocking restores EXACTLY the saved values (not assumed values).
    /// </summary>
    public static class WindowsUpdateBlocker
    {
        public sealed record StepResult(string Step, bool Ok, bool Critical, string? Detail = null);

        private sealed class ServiceState
        {
            public string Name { get; set; } = "";
            public int? Start { get; set; }
            public int? DelayedAutostart { get; set; }
            public string? FailureActionsHex { get; set; }
        }

        private sealed class SavedState
        {
            public DateTime Date { get; set; }
            public List<ServiceState> Services { get; set; } = new();
        }

        private static readonly string[] Services = { "wuauserv", "UsoSvc", "WaaSMedicSvc" };

        // Windows 10/11 defaults — used only if the saved state is missing.
        private static readonly Dictionary<string, (int start, int delayed)> Defaults = new()
        {
            ["wuauserv"]     = (3, 0),   // Manual (start on demand)
            ["UsoSvc"]       = (2, 1),   // Automatic (Delayed Start)
            ["WaaSMedicSvc"] = (3, 0),   // Manual
        };

        private static readonly string[] Tasks =
        {
            @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
            @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
            @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static Task",
        };

        /// <summary>C:\ProgramData\SMDWin — stays on the PC even if SMD Win ran from a stick.</summary>
        public static string MachineFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SMDWin");
        private static string StateFile   => Path.Combine(MachineFolder, "windows-update-block.json");
        public  static string RestoreScript => Path.Combine(MachineFolder, "Restore-WindowsUpdate.bat");

        private static string SvcKey(string svc) => $@"SYSTEM\CurrentControlSet\Services\{svc}";

        /// <summary>Blocat = wuauserv are Start = 4 in registry.</summary>
        public static bool IsBlocked()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(SvcKey("wuauserv"));
                return k?.GetValue("Start") is int s && s == 4;
            }
            catch { return false; }
        }

        // ── BLOCARE ─────────────────────────────────────────────────────────────
        public static List<StepResult> Block()
        {
            var steps = new List<StepResult>();

            // 1) The original state — saved only if it does not exist yet (a second "Block" must not overwrite the good state
            //    with an already blocked one).
            try
            {
                Directory.CreateDirectory(MachineFolder);
                if (!File.Exists(StateFile))
                {
                    var st = new SavedState { Date = DateTime.Now };
                    foreach (var svc in Services) st.Services.Add(ReadState(svc));
                    File.WriteAllText(StateFile, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
                }
                steps.Add(new StepResult("Save current settings (for exact restore)", true, true));
            }
            catch (Exception ex)
            {
                steps.Add(new StepResult("Save current settings (for exact restore)", false, true, ex.Message));
                return steps; // without a saved state nothing is blocked — it could not be restored exactly
            }

            // 2) Stop the services (WaaSMedicSvc often refuses to stop — not critical; with Start=4 it does not start after a restart)
            foreach (var svc in Services)
            {
                try
                {
                    using var sc = new ServiceController(svc);
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    steps.Add(new StepResult($"Stop {svc}", true, false));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepResult($"Stop {svc}", false, false, "will stay off after the next restart — " + ShortMsg(ex)));
                }
            }

            // 3) Start = 4 in the registry + 4) no automatic re-enabling
            foreach (var svc in Services)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(SvcKey(svc), writable: true)
                                  ?? throw new InvalidOperationException("service not found");
                    k.SetValue("Start", 4, RegistryValueKind.DWord);
                    k.DeleteValue("FailureActions", throwOnMissingValue: false);
                    bool ok = k.GetValue("Start") is int s && s == 4;
                    // UsoSvc is not critical: with wuauserv and Medic disabled, it has nothing to start
                    steps.Add(new StepResult($"Disable {svc}", ok, svc != "UsoSvc", ok ? null : "value did not stick"));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepResult($"Disable {svc}", false, svc != "UsoSvc", ShortMsg(ex)));
                }
            }

            // 5) Scheduled tasks — some belong to the SYSTEM account and refuse changes; not critical (without the services above
            //    they have nothing to start).
            foreach (var t in Tasks)
            {
                int code = RunHidden("schtasks.exe", $"/Change /TN \"{t}\" /Disable");
                steps.Add(new StepResult($"Disable task {t.Split('\\').Last()}", code == 0, false,
                    code == 0 ? null : "protected by Windows — not needed while the services are disabled"));
            }

            // 6) The restore script, independent of SMD Win
            try
            {
                WriteRestoreScript();
                steps.Add(new StepResult("Create restore script in " + MachineFolder, true, true));
            }
            catch (Exception ex)
            {
                steps.Add(new StepResult("Create restore script", false, true, ShortMsg(ex)));
            }

            AppLogger.Info("WindowsUpdateBlocker.Block: " + string.Join(" | ", steps.Select(s => $"{s.Step}={(s.Ok ? "OK" : "FAIL " + s.Detail)}")));
            return steps;
        }

        // ── DEBLOCARE (restaurare exacta) ──────────────────────────────────────
        public static List<StepResult> Unblock()
        {
            var steps = new List<StepResult>();
            SavedState? saved = null;
            try
            {
                if (File.Exists(StateFile))
                    saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StateFile));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsUpdateBlocker.Unblock read state"); }

            foreach (var svc in Services)
            {
                var st = saved?.Services.FirstOrDefault(s => s.Name == svc);
                var def = Defaults[svc];
                int start = st?.Start is int s0 && s0 != 4 ? s0 : def.start;
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(SvcKey(svc), writable: true)
                                  ?? throw new InvalidOperationException("service not found");
                    k.SetValue("Start", start, RegistryValueKind.DWord);
                    int delayed = st?.DelayedAutostart ?? def.delayed;
                    if (start == 2) k.SetValue("DelayedAutostart", delayed, RegistryValueKind.DWord);
                    if (!string.IsNullOrEmpty(st?.FailureActionsHex))
                        k.SetValue("FailureActions", Convert.FromHexString(st!.FailureActionsHex!), RegistryValueKind.Binary);
                    steps.Add(new StepResult($"Restore {svc} ({StartName(start)})", true, true));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepResult($"Restore {svc}", false, true, ShortMsg(ex)));
                }
            }

            foreach (var t in Tasks)
            {
                int code = RunHidden("schtasks.exe", $"/Change /TN \"{t}\" /Enable");
                steps.Add(new StepResult($"Enable task {t.Split('\\').Last()}", code == 0, false,
                    code == 0 ? null : "protected by Windows — was probably never disabled"));
            }

            // Start wuauserv now (it would start on demand anyway); the others recover by themselves
            try
            {
                using var sc = new ServiceController("wuauserv");
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                steps.Add(new StepResult("Start wuauserv", true, false));
            }
            catch (Exception ex) { steps.Add(new StepResult("Start wuauserv", false, false, "starts on demand — " + ShortMsg(ex))); }

            // Unblocking is done: delete the state and the script (the next state is saved again)
            if (steps.Where(s => s.Critical).All(s => s.Ok))
            {
                try { File.Delete(StateFile); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { File.Delete(RestoreScript); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            AppLogger.Info("WindowsUpdateBlocker.Unblock: " + string.Join(" | ", steps.Select(s => $"{s.Step}={(s.Ok ? "OK" : "FAIL " + s.Detail)}")));
            return steps;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private static ServiceState ReadState(string svc)
        {
            var st = new ServiceState { Name = svc };
            using var k = Registry.LocalMachine.OpenSubKey(SvcKey(svc));
            if (k == null) return st;
            st.Start            = k.GetValue("Start") as int?;
            st.DelayedAutostart = k.GetValue("DelayedAutostart") as int?;
            if (k.GetValue("FailureActions") is byte[] fa) st.FailureActionsHex = Convert.ToHexString(fa);
            return st;
        }

        /// <summary>.bat script that restores the saved values, with self-elevation. Does not depend on SMD Win — works even if
        /// the app is no longer on the PC.</summary>
        private static void WriteRestoreScript()
        {
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StateFile)) ?? new SavedState();
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"rem Restaurare Windows Update - generat de SMD Win la {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("rem Restore Windows Update - generated by SMD Win. Works without SMD Win installed.");
            sb.AppendLine("net session >nul 2>&1 || (powershell -NoProfile -Command \"Start-Process -Verb RunAs -FilePath '%~f0'\" & exit /b)");
            foreach (var svc in Services)
            {
                var st = saved.Services.FirstOrDefault(s => s.Name == svc);
                var def = Defaults[svc];
                int start = st?.Start is int s0 && s0 != 4 ? s0 : def.start;
                string key = $"HKLM\\{SvcKey(svc)}";
                sb.AppendLine($"reg add \"{key}\" /v Start /t REG_DWORD /d {start} /f");
                if (start == 2) sb.AppendLine($"reg add \"{key}\" /v DelayedAutostart /t REG_DWORD /d {st?.DelayedAutostart ?? def.delayed} /f");
                if (!string.IsNullOrEmpty(st?.FailureActionsHex))
                    sb.AppendLine($"reg add \"{key}\" /v FailureActions /t REG_BINARY /d {st!.FailureActionsHex} /f");
            }
            foreach (var t in Tasks)
                sb.AppendLine($"schtasks /Change /TN \"{t}\" /Enable >nul 2>&1");
            sb.AppendLine("net start wuauserv >nul 2>&1");
            sb.AppendLine("del \"%~dp0windows-update-block.json\" >nul 2>&1");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Windows Update a fost restaurat. Se recomanda un restart.");
            sb.AppendLine("echo Windows Update has been restored. A restart is recommended.");
            sb.AppendLine("pause");
            File.WriteAllText(RestoreScript, sb.ToString(), Encoding.ASCII);
        }

        private static string StartName(int start) => start switch
        {
            2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => start.ToString()
        };

        private static string ShortMsg(Exception ex) =>
            ex is UnauthorizedAccessException or System.Security.SecurityException ? "access denied" : ex.Message;

        private static int RunHidden(string exe, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p == null) return -1;
                if (!p.WaitForExit(8000)) return -2;
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}
