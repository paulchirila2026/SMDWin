using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public static class PowerAdvancedService
    {
        private static readonly Regex RxGuid = new(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private const string GuidSubProcessor    = "54533251-82be-4824-96c1-47b60b740d00";
        private const string GuidPerfBoostMode   = "be337238-0d82-4146-a960-4f3749d470c7";
        private const string GuidProcThrottleMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        private const string GuidProcThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";

        // Reads go first through the official Windows API (powrprof.dll — PowerReadACValueIndex / PowerReadDCValueIndex on
        // the active scheme), which returns the EFFECTIVE value (user override or the plan default). The registry is only a
        // fallback (the real keys have NO braces).
        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static Guid? GetActiveSchemeGuidApi()
        {
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStructure<Guid>(p); }
                finally { LocalFree(p); }
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.GetActiveSchemeGuidApi: {ex.Message}"); return null; }
        }

        private static int? ReadPowerSettingFromRegistry(string subgroupGuid, string settingGuid, bool ac = true)
        {
            // 1) API Windows
            try
            {
                var scheme = GetActiveSchemeGuidApi();
                if (scheme.HasValue)
                {
                    Guid sch = scheme.Value, sub = new(subgroupGuid), set = new(settingGuid);
                    uint rc = ac ? PowerReadACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out uint v)
                                 : PowerReadDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out v);
                    if (rc == 0) return unchecked((int)v);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.PowerRead*ValueIndex: {ex.Message}"); }

            // 2) Fallback: registry (keys without braces)
            try
            {
                string schemeGuid = GetActiveSchemeGuidApi()?.ToString() ?? GetActiveSchemeGuid();
                string liveValueName    = ac ? "ACSettingIndex" : "DCSettingIndex";
                string defaultValueName = ac ? "DefaultAcIndex" : "DefaultDcIndex";
                string[] paths =
                {
                    $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{schemeGuid}\{subgroupGuid}\{settingGuid}",
                    $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroupGuid}\{settingGuid}\DefaultPowerSchemeValues\{schemeGuid}",
                };
                foreach (var path in paths)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: false);
                    var val = key?.GetValue(liveValueName);
                    if (val != null) return Convert.ToInt32(val);
                }
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroupGuid}\{settingGuid}", writable: false))
                {
                    var val = key?.GetValue(defaultValueName);
                    if (val != null) return Convert.ToInt32(val);
                }
                return null;
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.ReadPowerSettingFromRegistry: {ex.Message}"); return null; }
        }

        // ── Plan settings read for the Power page ──────────────────
        // Fixed Windows GUIDs (the same on every system). Time values are in seconds, 0 = never.
        private const string GuidSubSleep     = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        private const string GuidStandbyIdle  = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
        private const string GuidSubVideo     = "7516b95f-f776-4464-8c53-06167f40cc99";
        private const string GuidVideoIdle    = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        private const string GuidSubDisk      = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        private const string GuidDiskIdle     = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
        private const string GuidSubPciE      = "501a4d13-42af-4429-9fd1-a8218c268e20";
        private const string GuidPciEAspm     = "ee12f906-d277-404b-b6da-e5fa1a576df5";

        /// <summary>Sleep after (seconds, 0 = never).</summary>
        public static int? GetSleepAfterSeconds(bool ac = true)       => ReadPowerSettingFromRegistry(GuidSubSleep, GuidStandbyIdle, ac);
        /// <summary>Turn off display after (seconds, 0 = never).</summary>
        public static int? GetDisplayOffAfterSeconds(bool ac = true)  => ReadPowerSettingFromRegistry(GuidSubVideo, GuidVideoIdle, ac);
        /// <summary>Turn off disk after (seconds, 0 = never).</summary>
        public static int? GetDiskOffAfterSeconds(bool ac = true)     => ReadPowerSettingFromRegistry(GuidSubDisk, GuidDiskIdle, ac);
        /// <summary>PCI Express Link State Power Management: 0 = Off, 1 = Moderate, 2 = Maximum.</summary>
        public static int? GetPciExpressAspm(bool ac = true)          => ReadPowerSettingFromRegistry(GuidSubPciE, GuidPciEAspm, ac);

        /// <summary>"10 min", "1 h 30 min", "Never" for a duration in seconds.</summary>
        public static string FormatIdle(int? seconds, string never = "Never")
        {
            if (seconds is null) return "";   // not read → nothing (the page hides the row)
            if (seconds <= 0) return never;
            var t = TimeSpan.FromSeconds(seconds.Value);
            if (t.TotalMinutes < 1) return $"{t.Seconds} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
            return t.Minutes > 0 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalHours} h";
        }

        // ── The "When the PC is not used" settings (Power page, 11.11.2026) ──────────────────────
        private const string GuidSubButtons   = "4f971e89-eebd-4455-a8de-9e59040e7347";
        private const string GuidLidAction    = "5ca83367-6e45-459f-a27b-476b1d01c936";
        private const string GuidPButtonAction = "7648efa3-dd9c-4e3e-b566-50f929386280";
        private const string GuidRtcWake      = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";
        /// <summary>The hidden "Ultimate Performance" plan template (Windows 10 1803+); powercfg -duplicatescheme makes a copy.</summary>
        public const string UltimatePlanTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

        /// <summary>Closing the lid / pressing the power button: 0 do nothing, 1 sleep, 2 hibernate, 3 shut down, 4 turn off the display.</summary>
        public static int? GetLidAction(bool ac = true)         => ReadPowerSettingFromRegistry(GuidSubButtons, GuidLidAction, ac);
        public static int? GetPowerButtonAction(bool ac = true) => ReadPowerSettingFromRegistry(GuidSubButtons, GuidPButtonAction, ac);
        /// <summary>"Allow wake timers": 0 off, 1 on, 2 only important ones.</summary>
        public static int? GetWakeTimersAllowed(bool ac = true) => ReadPowerSettingFromRegistry(GuidSubSleep, GuidRtcWake, ac);

        private const string GuidHibernateIdle = "9d7815a6-7ee4-497e-8888-515a05f02364";
        /// <summary>Hibernate after sleeping this long (seconds, 0 = never).</summary>
        public static int? GetHibernateAfterSeconds(bool ac = true) => ReadPowerSettingFromRegistry(GuidSubSleep, GuidHibernateIdle, ac);

        // Battery saver ("Energy saver" on Windows 11): turns on below a charge level, can lower the screen brightness
        private const string GuidSubEnergySaver = "de830923-a562-41af-a086-e3a2c6bad2da";
        private const string GuidEsThreshold    = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
        private const string GuidEsBrightness   = "13d09884-f74e-474a-a852-b6bde8ad03a8";

        /// <summary>The charge level (%) at which battery saver turns on by itself (0 = never).</summary>
        public static int? GetBatterySaverThreshold() => ReadPowerSettingFromRegistry(GuidSubEnergySaver, GuidEsThreshold, ac: false);

        public static void SetBatterySaverThreshold(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            RunPowercfg($"/SETDCVALUEINDEX SCHEME_CURRENT {GuidSubEnergySaver} {GuidEsThreshold} {percent}");
            RunPowercfg($"/SETACVALUEINDEX SCHEME_CURRENT {GuidSubEnergySaver} {GuidEsThreshold} {percent}");
            RunPowercfg("/SETACTIVE SCHEME_CURRENT");
        }

        /// <summary>True when battery saver lowers the brightness (the brightness weight is below 100 %).</summary>
        public static bool? GetBatterySaverDims() => ReadPowerSettingFromRegistry(GuidSubEnergySaver, GuidEsBrightness, ac: false) is int v ? v < 100 : null;

        /// <summary>Lower the brightness on battery saver: 70 % of the normal brightness (the Windows default) or unchanged.</summary>
        public static void SetBatterySaverDims(bool dim)
        {
            RunPowercfg($"/SETDCVALUEINDEX SCHEME_CURRENT {GuidSubEnergySaver} {GuidEsBrightness} {(dim ? 70 : 100)}");
            RunPowercfg("/SETACTIVE SCHEME_CURRENT");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        /// <summary>Battery saver is on right now (null = not known).</summary>
        public static bool? IsBatterySaverOn()
        {
            try { return GetSystemPowerStatus(out var s) ? s.SystemStatusFlag == 1 : null; }
            catch (Exception ex) { AppLogger.Debug($"IsBatterySaverOn: {ex.Message}"); return null; }
        }

        /// <summary>The PC runs on the charger right now (null = not known, e.g. a desktop reports "online" — true).</summary>
        public static bool? IsOnAcPower()
        {
            try { return GetSystemPowerStatus(out var s) ? s.AcLineStatus switch { 1 => true, 0 => false, _ => null } : null; }
            catch (Exception ex) { AppLogger.Debug($"IsOnAcPower: {ex.Message}"); return null; }
        }

        /// <summary>Windows' own estimate of the battery time left, in seconds (null = not known, e.g. on the charger).</summary>
        public static int? GetBatteryLifeSeconds()
        {
            try { return GetSystemPowerStatus(out var s) && s.BatteryLifeTime > 0 && s.BatteryLifeTime < 100 * 3600 ? s.BatteryLifeTime : null; }
            catch (Exception ex) { AppLogger.Debug($"GetBatteryLifeSeconds: {ex.Message}"); return null; }
        }

        /// <summary>Makes a plan the active one.</summary>
        public static void SetActivePlan(string guid) => RunPowercfg($"/setactive {guid}");

        /// <summary>Screen / sleep / hibernate / hard-disk idle time, in minutes (0 = never), through powercfg /change.</summary>
        public static void SetIdleMinutes(string what, int minutes, bool ac)
        {
            string key = what switch { "screen" => "monitor-timeout", "sleep" => "standby-timeout", "hib" => "hibernate-timeout", "disk" => "disk-timeout", _ => "" };
            if (key.Length == 0) return;
            RunPowercfg($"/change {key}-{(ac ? "ac" : "dc")} {Math.Max(0, minutes)}");
        }

        /// <summary>The lid ("lid") or power button ("button") action for plugged in / on battery.</summary>
        public static void SetButtonAction(string what, int action, bool ac)
        {
            string setting = what == "lid" ? GuidLidAction : GuidPButtonAction;
            RunPowercfg($"/SET{(ac ? "AC" : "DC")}VALUEINDEX SCHEME_CURRENT {GuidSubButtons} {setting} {action}");
            RunPowercfg("/SETACTIVE SCHEME_CURRENT");
        }

        public static void SetWakeTimersAllowed(bool allowed)
        {
            RunPowercfg($"/SETACVALUEINDEX SCHEME_CURRENT {GuidSubSleep} {GuidRtcWake} {(allowed ? 1 : 0)}");
            RunPowercfg($"/SETDCVALUEINDEX SCHEME_CURRENT {GuidSubSleep} {GuidRtcWake} {(allowed ? 1 : 0)}");
            RunPowercfg("/SETACTIVE SCHEME_CURRENT");
        }

        /// <summary>Hibernation on / off (powercfg /hibernate). Off also turns Fast Startup off — Windows needs the file for it.</summary>
        public static void SetHibernate(bool on) => RunPowercfg($"/hibernate {(on ? "on" : "off")}");

        /// <summary>Size of C:\hiberfil.sys in bytes (null when there is none).</summary>
        public static long? HiberfilBytes()
        {
            try
            {
                var f = new System.IO.FileInfo(System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "hiberfil.sys"));
                return f.Exists ? f.Length : null;
            }
            catch (Exception ex) { AppLogger.Debug($"HiberfilBytes: {ex.Message}"); return null; }
        }

        /// <summary>Brings back the Windows plans with their default settings (powercfg -restoredefaultschemes).</summary>
        public static void RestoreDefaultPlans() => RunPowercfg("-restoredefaultschemes");

        /// <summary>The GUIDs of the plans Windows lists now (powercfg /list).</summary>
        public static System.Collections.Generic.List<string> ListPlanGuids()
        {
            var list = new System.Collections.Generic.List<string>();
            try { foreach (Match m in RxGuid.Matches(RunPowercfgOutput("/list"))) list.Add(m.Value.ToLowerInvariant()); }
            catch (Exception ex) { AppLogger.Debug($"ListPlanGuids: {ex.Message}"); }
            return list;
        }

        /// <summary>Makes a copy of the Ultimate Performance template and returns its GUID (null on failure).</summary>
        public static string? AddUltimatePlan()
        {
            try
            {
                var m = RxGuid.Matches(RunPowercfgOutput($"-duplicatescheme {UltimatePlanTemplate}"))
                    .Select(x => x.Value.ToLowerInvariant()).FirstOrDefault(g => g != UltimatePlanTemplate);
                return m;
            }
            catch (Exception ex) { AppLogger.Debug($"AddUltimatePlan: {ex.Message}"); return null; }
        }

        // ── Processor Performance Boost Mode (PERFBOOSTMODE) ───────────────────
        // Sub-grup SUB_PROCESSOR, setting PERFBOOSTMODE. Valorile documentate Microsoft:
        // 0=Disabled, 1=Enabled, 2=Aggressive, 3=Efficient Enabled, 4=Efficient Aggressive.
        public enum BoostMode { Disabled = 0, Enabled = 1, Aggressive = 2, EfficientEnabled = 3, EfficientAggressive = 4 }

        public static string BoostModeLabel(BoostMode m) => m switch
        {
            BoostMode.Disabled => "Disabled",
            BoostMode.Enabled => "Enabled",
            BoostMode.Aggressive => "Aggressive",
            BoostMode.EfficientEnabled => "Efficient Enabled",
            BoostMode.EfficientAggressive => "Efficient Aggressive",
            _ => "Unknown",
        };

        // ── Min/Max Processor State (SUB_PROCESSOR PROCTHROTTLEMIN/MAX, 0-100%) ─
        // Different from TurboBoost.cs (which sets only MAX to 99/100 as a trick to disable turbo) — here both ends are
        // exposed as real sliders, for fine control (useful on laptops where even 100% max means too much sustained heat/noise).
        public static int? GetMinProcessorState() => ReadPowerSettingFromRegistry(GuidSubProcessor, GuidProcThrottleMin, ac: true);
        public static int? GetMaxProcessorState() => ReadPowerSettingFromRegistry(GuidSubProcessor, GuidProcThrottleMax, ac: true);
        public static int? GetMinProcessorState(bool ac) => ReadPowerSettingFromRegistry(GuidSubProcessor, GuidProcThrottleMin, ac);
        public static int? GetMaxProcessorState(bool ac) => ReadPowerSettingFromRegistry(GuidSubProcessor, GuidProcThrottleMax, ac);

        public static void SetMinProcessorState(int percent) => SetProcessorThrottleValue("PROCTHROTTLEMIN", percent, applyToAc: true, applyToDc: true);
        public static void SetMaxProcessorState(int percent) => SetProcessorThrottleValue("PROCTHROTTLEMAX", percent, applyToAc: true, applyToDc: true);
        public static void SetMinProcessorState(int percent, bool applyToAc, bool applyToDc) => SetProcessorThrottleValue("PROCTHROTTLEMIN", percent, applyToAc, applyToDc);
        public static void SetMaxProcessorState(int percent, bool applyToAc, bool applyToDc) => SetProcessorThrottleValue("PROCTHROTTLEMAX", percent, applyToAc, applyToDc);

        private static void SetProcessorThrottleValue(string settingName, int percent, bool applyToAc, bool applyToDc)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            string guid = GetActiveSchemeGuid();
            if (applyToAc) RunPowercfg($"/SETACVALUEINDEX {guid} SUB_PROCESSOR {settingName} {percent}");
            if (applyToDc) RunPowercfg($"/SETDCVALUEINDEX {guid} SUB_PROCESSOR {settingName} {percent}");
            RunPowercfg($"/SETACTIVE {guid}");
        }

        // ── USB Selective Suspend ───────────────────────────────────────────────
        // USB sub-group (2a737441-1930-4402-8d77-b2bebba308a3), Selective Suspend setting
        // (48e6b7a6-50f5-4782-a5d4-53bb8f07e226) — fixed Windows GUIDs, the same on every system.
        // Useful when troubleshooting random USB disconnects.
        private const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
        private const string UsbSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

        public static bool? GetUsbSelectiveSuspend() => GetUsbSelectiveSuspend(ac: true);
        public static bool? GetUsbSelectiveSuspend(bool ac)
        {
            int? v = ReadPowerSettingFromRegistry(UsbSubgroupGuid, UsbSuspendSettingGuid, ac);
            return v.HasValue ? v.Value != 0 : (bool?)null;
        }

        public static void SetUsbSelectiveSuspend(bool enabled) => SetUsbSelectiveSuspend(enabled, applyToAc: true, applyToDc: true);
        public static void SetUsbSelectiveSuspend(bool enabled, bool applyToAc, bool applyToDc)
        {
            string guid = GetActiveSchemeGuid();
            int val = enabled ? 1 : 0;
            if (applyToAc) RunPowercfg($"/SETACVALUEINDEX {guid} {UsbSubgroupGuid} {UsbSuspendSettingGuid} {val}");
            if (applyToDc) RunPowercfg($"/SETDCVALUEINDEX {guid} {UsbSubgroupGuid} {UsbSuspendSettingGuid} {val}");
            RunPowercfg($"/SETACTIVE {guid}");
        }

        // ── Fast Startup (Hiberboot) ────────────────────────────────────────────
        // Registry, not powercfg — HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled.
        // Requires Administrator (HKLM). Affects Power History: a "fast" shutdown with Fast Startup is really a partial
        // hibernation and may show in the history as "sleep" instead of a clean "shutdown".
        private const string HiberbootKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        private const string HiberbootValueName = "HiberbootEnabled";

        public static bool? GetFastStartup()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(HiberbootKeyPath, writable: false);
                var val = key?.GetValue(HiberbootValueName);
                if (val == null) return null;
                return Convert.ToInt32(val) != 0;
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.GetFastStartup: {ex.Message}"); return null; }
        }

        /// <summary>Requires Administrator — throws UnauthorizedAccessException/SecurityException if the app is not elevated,
        /// caught by the caller like the other settings in this file ("Admin required" message).</summary>
        public static void SetFastStartup(bool enabled)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(HiberbootKeyPath, writable: true)
                ?? throw new Exception($@"Registry key not found: HKLM\{HiberbootKeyPath}");
            key.SetValue(HiberbootValueName, enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }

        /// <summary>GUID of the active power plan, read through the Windows API — no external process, so it can be called
        /// from the UI thread too. null when the API does not answer.</summary>
        public static string? ActiveSchemeGuid() => GetActiveSchemeGuidApi()?.ToString();

        /// <summary>The same GUID read with powercfg. Starts a (slow) process — background only.</summary>
        public static string? ActiveSchemeGuidFromPowercfg()
        {
            try { return GetActiveSchemeGuid(); }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.ActiveSchemeGuidFromPowercfg: {ex.Message}"); return null; }
        }

        private static string GetActiveSchemeGuid()
        {
            string output = RunPowercfgOutput("/GETACTIVESCHEME");
            var m = RxGuid.Match(output);
            if (!m.Success) throw new Exception("Active power scheme not found.");
            return m.Value;
        }

        /// <summary>Reads the current PERFBOOSTMODE for the active scheme (AC — on battery plans may differ, but the UI shows
        /// a single mode, the AC one).</summary>
        public static BoostMode? GetBoostMode() => GetBoostMode(ac: true);
        public static BoostMode? GetBoostMode(bool ac)
        {
            int? v = ReadPowerSettingFromRegistry(GuidSubProcessor, GuidPerfBoostMode, ac);
            if (!v.HasValue) return null;
            return Enum.IsDefined(typeof(BoostMode), v.Value) ? (BoostMode)v.Value : null;
        }

        public static void SetBoostMode(BoostMode mode) => SetBoostMode(mode, applyToAc: true, applyToDc: true);
        public static void SetBoostMode(BoostMode mode, bool applyToAc, bool applyToDc)
        {
            string guid = GetActiveSchemeGuid();
            int val = (int)mode;
            if (applyToAc) RunPowercfg($"/SETACVALUEINDEX {guid} SUB_PROCESSOR PERFBOOSTMODE {val}");
            if (applyToDc) RunPowercfg($"/SETDCVALUEINDEX {guid} SUB_PROCESSOR PERFBOOSTMODE {val}");
            RunPowercfg($"/SETACTIVE {guid}");
        }

        // ── Effective Power Mode (Windows 10 2004+ / 11) ───────────────────────
        // Documented public API: PowerGetEffectiveOverlayScheme (powrprof.dll).
        // Returns an overlay GUID (Best Power Efficiency / Balanced / Best Performance).
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

        private static readonly Guid OverlayBestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        private static readonly Guid OverlayBalanced            = new("00000000-0000-0000-0000-000000000000");
        private static readonly Guid OverlayBestPerformance     = new("ded574b5-45a0-4f42-8737-46345c09c238");

        /// <summary>Returns a friendly name for the current Effective Power Mode, or null if the API is not available (Windows
        /// older than 10 2004, or a desktop without overlay scheme support — normal, not an error).</summary>
        public static string? GetEffectivePowerMode()
        {
            try
            {
                uint hr = PowerGetEffectiveOverlayScheme(out Guid g);
                if (hr != 0) return null; // ERROR_SUCCESS = 0; otherwise the API is not available on this OS
                if (g == OverlayBestPowerEfficiency) return "Better Battery";
                if (g == OverlayBalanced) return "Balanced";
                if (g == OverlayBestPerformance) return "Best Performance";
                return $"Unknown overlay ({g})";
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.GetEffectivePowerMode: {ex.Message}"); return null; }
        }

        // ── The available sleep states ──────────────────────────────────────
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

        public class SleepStatesInfo
        {
            public bool HasS3;
            public bool HasModernStandby;
            public bool HasHibernate;
            public string RawOutput = "";
        }

        /// <summary>
        /// The system sleep states. First through the API (fast, the same answer in any Windows language); if the API does not
        /// answer, from the text of "powercfg /a" (starts a process, understands only the English text).
        /// </summary>
        public static SleepStatesInfo GetSleepStates()
        {
            var info = new SleepStatesInfo();
            try
            {
                var buffer = new byte[PowerCapabilities.BufferBytes];
                if (GetPwrCapabilities(buffer) && PowerCapabilities.Parse(buffer) is { } caps)
                {
                    info.HasS3 = caps.Sleep;
                    info.HasModernStandby = caps.ModernStandby;
                    info.HasHibernate = caps.Hibernate;
                    return info;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.GetPwrCapabilities: {ex.Message}"); }

            try
            {
                string output = RunPowercfgOutput("/A");
                info.RawOutput = output;
                // powercfg /a lists the AVAILABLE states under "The following sleep states are available on this system:" and the
                // unavailable ones below, with a reason. The lines under the first section (available) are used.
                info.HasS3 = Regex.IsMatch(output, @"Standby \(S3\)", RegexOptions.IgnoreCase)
                             && !Regex.IsMatch(output, @"Standby \(S3\)[^\n]*\n[^\n]*not available", RegexOptions.IgnoreCase);
                info.HasModernStandby = output.Contains("Standby (S0 Low Power Idle)", StringComparison.OrdinalIgnoreCase);
                info.HasHibernate = output.Contains("Hibernate", StringComparison.OrdinalIgnoreCase)
                                     && output.IndexOf("Hibernate", StringComparison.OrdinalIgnoreCase) <
                                        (output.IndexOf("following sleep states are not available", StringComparison.OrdinalIgnoreCase) is int i && i >= 0 ? i : int.MaxValue);
            }
            catch (Exception ex) { AppLogger.Debug($"PowerAdvancedService.GetSleepStates: {ex.Message}"); }
            return info;
        }

        // ── Rapoarte Sleep / Energy ─────────────────────────────────────────────
        /// <summary>Generates the powercfg /sleepstudy HTML report — fast, but requires the system to have gone through at least
        /// one Modern Standby sleep cycle; otherwise powercfg returns a clear error ("no sleep study data available"), passed on
        /// to the user unchanged.</summary>
        public static async Task<(bool ok, string pathOrError)> GenerateSleepReportAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"sleepstudy_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            return await Task.Run(() => RunReportCommand($"/SLEEPSTUDY /OUTPUT \"{path}\"", path, timeoutMs: 15000));
        }

        /// <summary>Generates the powercfg /energy HTML report — takes ~60 SECONDS (powercfg actively monitors the system for
        /// that long); the UI caller must show progress/a warning, not just a short spinner.</summary>
        public static async Task<(bool ok, string pathOrError)> GenerateEnergyReportAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"energy_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            return await Task.Run(() => RunReportCommand($"/ENERGY /OUTPUT \"{path}\" /DURATION 60", path, timeoutMs: 75000));
        }

        private static (bool, string) RunReportCommand(string args, string expectedPath, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return (false, "Could not start powercfg.exe");
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    return (false, "powercfg timed out — the report may need Administrator rights.");
                }
                string stdErr = errTask.GetAwaiter().GetResult();
                string stdOut = outTask.GetAwaiter().GetResult();
                if (File.Exists(expectedPath)) return (true, expectedPath);
                string msg = !string.IsNullOrWhiteSpace(stdErr) ? stdErr : stdOut;
                return (false, string.IsNullOrWhiteSpace(msg) ? "powercfg did not produce a report file." : msg.Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static string RunPowercfgOutput(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
            errTask.GetAwaiter().GetResult();
            return outTask.GetAwaiter().GetResult();
        }

        private static void RunPowercfg(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
            outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
        }
    }
}
