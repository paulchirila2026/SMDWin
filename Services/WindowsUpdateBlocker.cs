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
    /// Turning Windows Update off without breaking Windows Settings.
    ///
    /// The first version disabled the services (wuauserv, UsoSvc, WaaSMedicSvc), like Windows Update Blocker. That worked, but
    /// Settings › Windows Update then showed an error (0x80070422 "There were some problems…") for as long as they were off.
    /// Now the services stay as Windows wants them and the updates are stopped the way Windows itself offers:
    ///   1. the "Pause updates" of Settings, stretched to 10 years (FlightSettingsMaxPauseDays + the pause dates) — Settings
    ///      shows "Updates paused until …", no error;
    ///   2. the group policy "no automatic updates" (NoAutoUpdate = 1) and no forced restart while someone is signed in —
    ///      Settings shows "Some settings are managed by your organization", which is expected;
    ///   3. the previous values are saved, so turning it back on restores exactly what was there; a restore script in
    ///      C:\ProgramData\SMDWin works even without SMD Win.
    /// A PC blocked with the old method is switched over: the services are restored first.
    /// Updates downloaded BEFORE the block can still ask for a restart (RebootPending) — that is not a new update.
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

        /// <summary>Off = paused by SMD Win (the pause runs past a year from now), or blocked with the old method (wuauserv Start = 4).</summary>
        public static bool IsBlocked() => IsPausedLong() || ServicesBlockedOldWay();

        private static bool ServicesBlockedOldWay()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(SvcKey("wuauserv"));
                return k?.GetValue("Start") is int s && s == 4;
            }
            catch { return false; }
        }

        private const string UxKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        private const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        private static string PauseFile => Path.Combine(MachineFolder, "windows-update-pause.json");
        private static readonly string[] PauseNames =
        {
            "FlightSettingsMaxPauseDays", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
            "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime", "PauseUpdatesStartTime", "PauseUpdatesExpiryTime",
        };
        private static readonly string[] PolicyNames = { "NoAutoUpdate", "NoAutoRebootWithLoggedOnUsers" };

        private sealed class SavedValue
        {
            public string Key { get; set; } = "";
            public string Name { get; set; } = "";
            public string? Kind { get; set; }      // null = the value did not exist
            public string? Text { get; set; }
            public int? Number { get; set; }
        }

        private static bool IsPausedLong()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(UxKey);
                return k?.GetValue("PauseUpdatesExpiryTime") is string s
                       && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var until)
                       && until > DateTime.UtcNow.AddDays(365);
            }
            catch { return false; }
        }

        /// <summary>The date the pause ends, or null.</summary>
        public static DateTime? PausedUntil()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(UxKey);
                return k?.GetValue("PauseUpdatesExpiryTime") is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var until) ? until.ToLocalTime() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Windows waits for a restart to finish updates it has already installed (Windows Update or the servicing stack).
        /// After turning updates off this still shows as "Restart required" — those updates came before the block.
        /// </summary>
        public static bool RestartPending()
        {
            try
            {
                using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                if (a != null) return true;
                using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                return b != null;
            }
            catch { return false; }
        }

        // ── TURN OFF (pause + policy) ─────────────────────────────────────────────
        public static List<StepResult> Block()
        {
            var steps = new List<StepResult>();
            // a PC blocked the old way: give the services back to Windows first (that was the cause of the error in Settings)
            if (ServicesBlockedOldWay())
            {
                var back = RestoreServices();
                steps.Add(new StepResult("Give the update services back to Windows (old method)", back.Where(s => s.Critical).All(s => s.Ok), true));
            }
            try
            {
                Directory.CreateDirectory(MachineFolder);
                // the values before the first pause, saved once (a second "off" must not overwrite them)
                if (!File.Exists(PauseFile))
                {
                    var saved = new List<SavedValue>();
                    foreach (var n in PauseNames) saved.Add(ReadValue(UxKey, n));
                    foreach (var n in PolicyNames) saved.Add(ReadValue(AuPolicyKey, n));
                    File.WriteAllText(PauseFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
                }
                steps.Add(new StepResult("Save current settings (for exact restore)", true, true));
            }
            catch (Exception ex)
            {
                steps.Add(new StepResult("Save current settings (for exact restore)", false, true, ShortMsg(ex)));
                return steps;
            }
            try
            {
                var now = DateTime.UtcNow;
                string start = now.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                string end = now.AddDays(3650).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                using (var k = Registry.LocalMachine.CreateSubKey(UxKey, writable: true))
                {
                    k.SetValue("FlightSettingsMaxPauseDays", 7300, RegistryValueKind.DWord);
                    k.SetValue("PauseFeatureUpdatesStartTime", start); k.SetValue("PauseFeatureUpdatesEndTime", end);
                    k.SetValue("PauseQualityUpdatesStartTime", start); k.SetValue("PauseQualityUpdatesEndTime", end);
                    k.SetValue("PauseUpdatesStartTime", start); k.SetValue("PauseUpdatesExpiryTime", end);
                }
                steps.Add(new StepResult("Pause updates for 10 years (Settings shows \"Updates paused\")", IsPausedLong(), true));
            }
            catch (Exception ex) { steps.Add(new StepResult("Pause updates", false, true, ShortMsg(ex))); }
            try
            {
                using var p = Registry.LocalMachine.CreateSubKey(AuPolicyKey, writable: true);
                p.SetValue("NoAutoUpdate", 1, RegistryValueKind.DWord);
                p.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
                steps.Add(new StepResult("Policy: no automatic updates, no forced restart", true, false));
            }
            catch (Exception ex) { steps.Add(new StepResult("Policy: no automatic updates", false, false, ShortMsg(ex))); }
            try { WriteRestoreScript(); steps.Add(new StepResult("Create restore script in " + MachineFolder, true, false)); }
            catch (Exception ex) { steps.Add(new StepResult("Create restore script", false, false, ShortMsg(ex))); }
            AppLogger.Info("WindowsUpdateBlocker.Block (pause): " + string.Join(" | ", steps.Select(s => $"{s.Step}={(s.Ok ? "OK" : "FAIL " + s.Detail)}")));
            return steps;
        }

        // ── TURN BACK ON ──────────────────────────────────────────────────────────
        public static List<StepResult> Unblock()
        {
            var steps = new List<StepResult>();
            if (ServicesBlockedOldWay() || File.Exists(StateFile)) steps.AddRange(RestoreServices());
            List<SavedValue>? saved = null;
            try { if (File.Exists(PauseFile)) saved = JsonSerializer.Deserialize<List<SavedValue>>(File.ReadAllText(PauseFile)); }
            catch (Exception ex) { AppLogger.Warning(ex, "WindowsUpdateBlocker.Unblock read pause state"); }
            try
            {
                foreach (var n in PauseNames) RestoreValue(UxKey, n, saved);
                foreach (var n in PolicyNames) RestoreValue(AuPolicyKey, n, saved);
                steps.Add(new StepResult("End the pause and remove the policy (previous values restored)", !IsPausedLong(), true));
            }
            catch (Exception ex) { steps.Add(new StepResult("End the pause", false, true, ShortMsg(ex))); }
            if (steps.Where(s => s.Critical).All(s => s.Ok))
            {
                try { File.Delete(PauseFile); } catch (Exception ex) { AppLogger.Swallowed(ex); }
                try { File.Delete(RestoreScript); } catch (Exception ex) { AppLogger.Swallowed(ex); }
            }
            AppLogger.Info("WindowsUpdateBlocker.Unblock: " + string.Join(" | ", steps.Select(s => $"{s.Step}={(s.Ok ? "OK" : "FAIL " + s.Detail)}")));
            return steps;
        }

        private static SavedValue ReadValue(string key, string name)
        {
            var v = new SavedValue { Key = key, Name = name };
            using var k = Registry.LocalMachine.OpenSubKey(key);
            object? o = k?.GetValue(name);
            if (o is int i) { v.Kind = "dword"; v.Number = i; }
            else if (o is string s) { v.Kind = "string"; v.Text = s; }
            return v;
        }

        private static void RestoreValue(string key, string name, List<SavedValue>? saved)
        {
            var sv = saved?.FirstOrDefault(x => x.Key == key && x.Name == name);
            using var k = Registry.LocalMachine.CreateSubKey(key, writable: true);
            if (sv?.Kind == "dword" && sv.Number is int n) k.SetValue(name, n, RegistryValueKind.DWord);
            else if (sv?.Kind == "string" && sv.Text != null) k.SetValue(name, sv.Text);
            else k.DeleteValue(name, throwOnMissingValue: false);
        }

        // ── restoring the services of the first method (exact saved values) ─────
        private static List<StepResult> RestoreServices()
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

            AppLogger.Info("WindowsUpdateBlocker.RestoreServices: " + string.Join(" | ", steps.Select(s => $"{s.Step}={(s.Ok ? "OK" : "FAIL " + s.Detail)}")));
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
            var saved = File.Exists(PauseFile) ? JsonSerializer.Deserialize<List<SavedValue>>(File.ReadAllText(PauseFile)) ?? new() : new List<SavedValue>();
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"rem Restaurare Windows Update - generat de SMD Win la {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("rem Restore Windows Update - generated by SMD Win. Works without SMD Win installed.");
            sb.AppendLine("net session >nul 2>&1 || (powershell -NoProfile -Command \"Start-Process -Verb RunAs -FilePath '%~f0'\" & exit /b)");
            void Line(string key, string name)
            {
                var sv = saved.FirstOrDefault(x => x.Key == key && x.Name == name);
                string full = "HKLM\\" + key;
                if (sv?.Kind == "dword" && sv.Number is int n) sb.AppendLine($"reg add \"{full}\" /v {name} /t REG_DWORD /d {n} /f >nul");
                else if (sv?.Kind == "string" && sv.Text != null) sb.AppendLine($"reg add \"{full}\" /v {name} /t REG_SZ /d \"{sv.Text}\" /f >nul");
                else sb.AppendLine($"reg delete \"{full}\" /v {name} /f >nul 2>&1");
            }
            foreach (var n in PauseNames) Line(UxKey, n);
            foreach (var n in PolicyNames) Line(AuPolicyKey, n);
            sb.AppendLine("del \"%~dp0windows-update-pause.json\" >nul 2>&1");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Windows Update a fost repornit.");
            sb.AppendLine("echo Windows Update has been turned back on.");
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
