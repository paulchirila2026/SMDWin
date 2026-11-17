using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>One USB device present now (or seen earlier in this session).</summary>
    public sealed class UsbDeviceState
    {
        public string DeviceId     { get; init; } = "";   // USB\VID_046D&PID_C52B\5&2A3B...
        public string Name         { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string VidPid       { get; init; } = "";   // 046D:C52B
        public bool   IsHub        { get; init; }
        public bool   Connected    { get; set; }
        public int    Drops        { get; set; }          // disconnections seen while the monitor was open
        public int    StartsSinceBoot { get; set; } = -1; // from the Windows event log; -1 = unknown
        public DateTime? LastChange { get; set; }

        public string StatusText     => Connected ? "Connected" : "Disconnected";
        public string DropsText      => Drops == 0 ? "—" : Drops.ToString();
        public string ReconnectsText => StartsSinceBoot <= 1 ? (StartsSinceBoot < 0 ? "?" : "—") : (StartsSinceBoot - 1).ToString();
        public string LastChangeText => LastChange.HasValue ? LastChange.Value.ToString("HH:mm:ss") : "";
        /// <summary>Red when the device dropped out (now or several times), so it stands out in the list.</summary>
        public bool   IsProblem      => !Connected || Drops >= 2 || StartsSinceBoot >= 4;
    }

    /// <summary>One line of the live log.</summary>
    public sealed record UsbEvent(DateTime Time, bool Connected, string Name, string VidPid, string Detail)
    {
        public string TimeText  => Time.ToString("HH:mm:ss");
        public string Arrow     => Connected ? "▲" : "▼";
        public string KindText  => Connected ? "connected" : "disconnected";
    }

    /// <summary>
    /// The USB monitor's data: which USB devices are present (WMI Win32_PnPEntity), how many times each one has been started
    /// since Windows started (event log Kernel-PnP/Configuration, event 410 — every plug-in starts the device again, so more
    /// than one start means it was reconnected), and the two Windows power settings behind most "the mouse drops out now and
    /// then" cases: USB selective suspend and "Allow the computer to turn off this device".
    /// </summary>
    public static class UsbMonitorService
    {
        private static readonly Regex VidPidRx = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Generic names Windows gives to the USB node; the real name is usually on the HID child (mouse, keyboard…)
        private static readonly string[] GenericNames =
        {
            "USB Input Device", "USB Composite Device", "USB Mass Storage Device", "USB Device", "Unknown USB Device",
            "USB-Eingabegerät", "Dispozitiv de intrare USB", "Périphérique d'entrée USB",
        };

        /// <summary>The USB devices present right now, keyed by device instance ID (upper case). Root hubs are left out.</summary>
        public static Dictionary<string, (string Name, string Manufacturer, string VidPid, bool IsHub)> ReadPresent()
        {
            var usb = new Dictionary<string, (string Name, string Manufacturer, string VidPid, bool IsHub)>(StringComparer.OrdinalIgnoreCase);
            var childNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // VID:PID → better name from a child
            try
            {
                using var s = WmiHelper.Searcher(
                    @"SELECT DeviceID, Name, Manufacturer, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\%' OR DeviceID LIKE 'HID\\%'",
                    TimeSpan.FromSeconds(10));
                using var res = s.Get();
                foreach (ManagementObject o in res)
                {
                    string id    = (o["DeviceID"] as string ?? "").Trim();
                    string name  = (o["Name"] as string ?? "").Trim();
                    string manuf = (o["Manufacturer"] as string ?? "").Trim();
                    string cls   = (o["PNPClass"] as string ?? "").Trim();
                    if (id.Length == 0) continue;
                    var m = VidPidRx.Match(id);
                    string vidPid = m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant() : "";

                    bool isUsbNode = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
                    bool isInterface = id.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
                    if (isUsbNode && !isInterface)
                    {
                        if (id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;   // root hubs never unplug
                        bool hub = cls.Equals("USB", StringComparison.OrdinalIgnoreCase) && name.Contains("Hub", StringComparison.OrdinalIgnoreCase);
                        usb[id.ToUpperInvariant()] = (name, manuf, vidPid, hub);
                    }
                    else if (vidPid.Length > 0 && !IsGeneric(name) && !childNames.ContainsKey(vidPid))
                    {
                        childNames[vidPid] = name;   // e.g. "HID-compliant mouse", "HID Keyboard Device"
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UsbMonitorService.ReadPresent"); }

            // better names for the generic USB nodes
            foreach (var key in usb.Keys.ToList())
            {
                var d = usb[key];
                if (IsGeneric(d.Name) && d.VidPid.Length > 0 && childNames.TryGetValue(d.VidPid, out var better))
                    usb[key] = (better, d.Manufacturer, d.VidPid, d.IsHub);
            }
            return usb;
        }

        private static bool IsGeneric(string name)
            => string.IsNullOrWhiteSpace(name) || GenericNames.Any(g => name.Equals(g, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// How many times each USB device was started since Windows started (Kernel-PnP/Configuration, event 410). The first start
        /// is at boot or at the first plug-in; each later one is a reconnection. Empty if the log cannot be read.
        /// </summary>
        public static Dictionary<string, int> ReadStartsSinceBoot()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                long uptimeMs = Environment.TickCount64;
                string xpath = $"*[System[(EventID=410) and TimeCreated[timediff(@SystemTime) <= {uptimeMs}]]]";
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "Microsoft-Windows-Kernel-PnP/Configuration", System.Diagnostics.Eventing.Reader.PathType.LogName, xpath);
                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                var idRx = new Regex(@"<Data Name=['""]DeviceInstanceId['""]>([^<]+)</Data>", RegexOptions.IgnoreCase);
                for (var ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
                {
                    using (ev)
                    {
                        var m = idRx.Match(ev.ToXml());
                        if (!m.Success) continue;
                        string id = m.Groups[1].Value.Trim();
                        if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                        result[id.ToUpperInvariant()] = result.GetValueOrDefault(id.ToUpperInvariant()) + 1;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UsbMonitorService.ReadStartsSinceBoot: {ex.Message}"); }
            return result;
        }

        // ── USB selective suspend (power plan) ───────────────────────────────
        private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
        private const string SetSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

        /// <summary>(on mains, on battery): true = Windows may suspend idle USB ports. Null if it cannot be read.</summary>
        public static async Task<(bool? Ac, bool? Dc)> ReadSelectiveSuspendAsync()
        {
            try
            {
                var r = await ProcessRunner.RunAsync("powercfg", $"/q SCHEME_CURRENT {SubUsb} {SetSelectiveSuspend}", 8000).ConfigureAwait(false);
                // the last two hex values of the output are the AC and DC indexes (the labels around them are translated)
                var hex = Regex.Matches(r.Stdout ?? "", @"0x([0-9a-fA-F]{8})").Select(x => Convert.ToInt32(x.Groups[1].Value, 16)).ToList();
                if (hex.Count < 2) return (null, null);
                return (hex[^2] != 0, hex[^1] != 0);
            }
            catch (Exception ex) { AppLogger.Debug($"UsbMonitorService.ReadSelectiveSuspend: {ex.Message}"); return (null, null); }
        }

        /// <summary>Turns USB selective suspend off for the current power plan (mains and battery).</summary>
        public static async Task<bool> DisableSelectiveSuspendAsync()
        {
            try
            {
                var a = await ProcessRunner.RunAsync("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubUsb} {SetSelectiveSuspend} 0", 8000).ConfigureAwait(false);
                var d = await ProcessRunner.RunAsync("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubUsb} {SetSelectiveSuspend} 0", 8000).ConfigureAwait(false);
                var s = await ProcessRunner.RunAsync("powercfg", "/setactive SCHEME_CURRENT", 8000).ConfigureAwait(false);
                AppLogger.Info($"USB selective suspend turned off (exit codes {a.ExitCode}/{d.ExitCode}/{s.ExitCode})");
                return a.ExitCode == 0;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UsbMonitorService.DisableSelectiveSuspend"); return false; }
        }

        // ── "Allow the computer to turn off this device to save power" ──────
        /// <summary>USB devices and hubs on which Windows may cut the power (MSPower_DeviceEnable = true): (allowed, total).</summary>
        public static (int Allowed, int Total) ReadDevicePowerOff()
        {
            int allowed = 0, total = 0;
            try
            {
                using var s = WmiHelper.Searcher(@"root\wmi", "SELECT InstanceName, Enable FROM MSPower_DeviceEnable", TimeSpan.FromSeconds(10));
                using var res = s.Get();
                foreach (ManagementObject o in res)
                {
                    string inst = o["InstanceName"] as string ?? "";
                    if (!inst.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    total++;
                    if (o["Enable"] is bool b && b) allowed++;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UsbMonitorService.ReadDevicePowerOff: {ex.Message}"); }
            return (allowed, total);
        }

        /// <summary>Unticks "Allow the computer to turn off this device" on every USB device and hub. Returns how many changed.</summary>
        public static int DisableDevicePowerOff()
        {
            int changed = 0;
            try
            {
                using var s = WmiHelper.Searcher(@"root\wmi", "SELECT * FROM MSPower_DeviceEnable", TimeSpan.FromSeconds(10));
                using var res = s.Get();
                foreach (ManagementObject o in res)
                {
                    try
                    {
                        string inst = o["InstanceName"] as string ?? "";
                        if (!inst.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (o["Enable"] is bool b && b)
                        {
                            o["Enable"] = false;
                            o.Put();
                            changed++;
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"UsbMonitorService.DisableDevicePowerOff item: {ex.Message}"); }
                }
                AppLogger.Info($"USB power-off allowance removed on {changed} device(s)");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UsbMonitorService.DisableDevicePowerOff"); }
            return changed;
        }
    }
}
