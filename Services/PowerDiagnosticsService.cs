using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>
    /// Power diagnostics for the Power page: what keeps the PC awake, why it woke up, what can wake it, wake timers,
    /// unexpected shutdowns.
    ///
    /// Page rule: what cannot be read is NOT shown. So every method returns <c>null</c> when the read failed (powercfg
    /// missing / no rights / unknown format) and an empty list only when the read succeeded and there really is nothing
    /// ("nothing blocks sleep" is information, "could not find out" is not).
    ///
    /// powercfg output is translated into the Windows language, so only the markers that are NOT translated are parsed:
    /// [PROCESS] / [SERVICE] / [DRIVER] / [CALLER]… for /requests and /waketimers. The last wake comes from the System log
    /// (Power-Troubleshooter, ID 1), from the event XML — the field names (WakeSourceText, WakeTime) do not depend on the
    /// language.
    /// </summary>
    public static class PowerDiagnosticsService
    {
        public sealed record SleepBlocker(string Category, string Kind, string Name, string Reason);
        public sealed record WakeInfo(DateTime When, string Source);
        public sealed record WakeTimer(string Owner, string Reason);
        /// <summary>Dates = the day of each unexpected shutdown (for the 30 blocks of the Stability card).</summary>
        public sealed record ShutdownStats(int Count30d, DateTime? Last, System.Collections.Generic.List<DateTime>? Dates = null);

        private static readonly Regex RxTag = new(@"^\[(PROCESS|SERVICE|DRIVER|CALLER|KERNEL|[A-Z]+)\]\s*(.+)$", RegexOptions.Compiled);

        private static string? RunPowercfg(string args, int timeoutMs = 6000)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string? output = ProcessRunner.ReadStdoutBounded(p, timeoutMs);
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception ex) { AppLogger.Debug($"powercfg kill: {ex.Message}"); } return null; }
                return p.ExitCode == 0 ? output : null;
            }
            catch (Exception ex) { AppLogger.Debug($"PowerDiagnostics powercfg {args}: {ex.Message}"); return null; }
        }

        private static string ShortName(string raw)
        {
            string s = raw.Trim();
            // „\Device\HarddiskVolume3\Program Files\Google\Chrome\chrome.exe” → „chrome.exe”
            // „…\svchost.exe (SystemEventsBroker)” → „svchost.exe (SystemEventsBroker)”
            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0)
            {
                int slash = s.LastIndexOf('\\', exe);
                if (slash >= 0) s = s[(slash + 1)..];
            }
            return s;
        }

        /// <summary>powercfg /requests — what currently prevents the display or the system from sleeping.</summary>
        public static List<SleepBlocker>? GetSleepBlockers()
        {
            string? output = RunPowercfg("/requests");
            if (output == null) return null;
            var list = new List<SleepBlocker>();
            string category = "";
            var lines = output.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                // category headers: "DISPLAY:", "SYSTEM:", "AWAYMODE:", "EXECUTION:"… (not translated)
                if (Regex.IsMatch(line, @"^[A-Z]{3,}:$")) { category = line.TrimEnd(':'); continue; }
                var m = RxTag.Match(line);
                if (!m.Success) continue;
                string reason = "";
                if (i + 1 < lines.Length)
                {
                    string next = lines[i + 1].Trim();
                    if (next.Length > 0 && !next.StartsWith("[") && !Regex.IsMatch(next, @"^[A-Z]{3,}:$"))
                        reason = next;
                }
                list.Add(new SleepBlocker(category, m.Groups[1].Value, ShortName(m.Groups[2].Value), reason));
            }
            return list;
        }

        /// <summary>powercfg /waketimers — the timers that can wake the PC (scheduled tasks, drivers).</summary>
        public static List<WakeTimer>? GetWakeTimers()
        {
            string? output = RunPowercfg("/waketimers");
            if (output == null) return null;
            var list = new List<WakeTimer>();
            var lines = output.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int b = line.IndexOf('[');
                if (b < 0) continue;
                var m = RxTag.Match(line[b..]);
                if (!m.Success) continue;
                string owner = m.Groups[2].Value;
                // "[SERVICE] \Device\...\svchost.exe (SystemEventsBroker) expires at 03:00 on 27.09.2026."
                int exp = owner.IndexOf(" expires", StringComparison.OrdinalIgnoreCase);
                if (exp > 0) owner = owner[..exp];
                string reason = "";
                for (int j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0 || t.Contains('[')) break;
                    if (t.StartsWith("Reason", StringComparison.OrdinalIgnoreCase) || t.Contains(':'))
                    { reason = t.Contains(':') ? t[(t.IndexOf(':') + 1)..].Trim() : t; break; }
                }
                list.Add(new WakeTimer(ShortName(owner), reason));
            }
            return list;
        }

        /// <summary>powercfg /devicequery wake_armed — the devices allowed to wake the PC.</summary>
        public static List<string>? GetWakeArmedDevices()
        {
            string? output = RunPowercfg("/devicequery wake_armed");
            if (output == null) return null;
            var devs = output.Replace("\r", "").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            // "NONE" (or the translated equivalent, a single capitalized word) = no device
            if (devs.Count == 1 && Regex.IsMatch(devs[0], @"^[\p{Lu}\s\.]{2,20}$") && !devs[0].Contains(' '))
                return new List<string>();
            return devs;
        }

        /// <summary>powercfg /devicequery wake_programmable — every device that COULD be allowed to wake the PC.</summary>
        public static List<string>? GetWakeProgrammableDevices()
        {
            string? output = RunPowercfg("/devicequery wake_programmable");
            if (output == null) return null;
            var devs = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (devs.Count == 1 && Regex.IsMatch(devs[0], @"^[\p{Lu}\s\.]{2,20}$") && !devs[0].Contains(' ')) return new List<string>();
            return devs;
        }

        /// <summary>Allows a device to wake the PC again (powercfg /deviceenablewake).</summary>
        public static bool EnableWake(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Contains('"')) return false;
            return RunPowercfg($"/deviceenablewake \"{deviceName}\"") != null;
        }

        // ── Ignoring a program or driver that keeps the PC awake (11.11.2026) ─────────────────────────
        /// <summary>An entry of powercfg /requestsoverride: Kind = PROCESS / SERVICE / DRIVER, Name as Windows stores it.</summary>
        public sealed record RequestOverride(string Kind, string Name, string Requests);

        /// <summary>The name powercfg /requestsoverride expects: the exe for a process, the service name for a service, the device
        /// name (without the hardware path) for a driver.</summary>
        public static string OverrideName(SleepBlocker b)
        {
            if (b.Kind == "SERVICE")
            {
                var m = Regex.Match(b.Name, @"\(([^()\\]+)\)\s*$");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            if (b.Kind == "DRIVER") return Regex.Replace(b.Name, @"\s*\([^()]*\\[^()]*\)", "").Trim();
            return b.Name;
        }

        /// <summary>Windows ignores this program / service / driver when it asks to keep the PC or the screen awake.</summary>
        public static bool AddRequestOverride(SleepBlocker b)
        {
            string kind = b.Kind is "PROCESS" or "SERVICE" or "DRIVER" ? b.Kind : "PROCESS";
            string name = OverrideName(b);
            if (name.Length == 0 || name.Contains('"')) return false;
            bool ok = RunPowercfg($"/requestsoverride {kind} \"{name}\" DISPLAY SYSTEM AWAYMODE") != null;
            // a driver is sometimes stored with its full name (with the hardware path): set that form too
            if (kind == "DRIVER" && b.Name != name && !b.Name.Contains('"'))
                ok |= RunPowercfg($"/requestsoverride DRIVER \"{b.Name}\" DISPLAY SYSTEM AWAYMODE") != null;
            return ok;
        }

        public static bool RemoveRequestOverride(RequestOverride o)
            => !o.Name.Contains('"') && RunPowercfg($"/requestsoverride {o.Kind} \"{o.Name}\"") != null;

        /// <summary>The current overrides (powercfg /requestsoverride without arguments): headers [PROCESS] / [SERVICE] /
        /// [DRIVER], then "name REQUEST REQUEST…".</summary>
        public static List<RequestOverride>? GetRequestOverrides()
        {
            string? output = RunPowercfg("/requestsoverride");
            if (output == null) return null;
            var list = new List<RequestOverride>();
            string kind = "";
            foreach (var raw in output.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                var h = Regex.Match(line, @"^\[(PROCESS|SERVICE|DRIVER)\]$");
                if (h.Success) { kind = h.Groups[1].Value; continue; }
                if (kind.Length == 0) continue;
                var m = Regex.Match(line, @"^(.+?)((?:\s+(?:DISPLAY|SYSTEM|AWAYMODE|EXECUTION))+)\s*$");
                if (m.Success) list.Add(new RequestOverride(kind, m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            }
            return list;
        }

        /// <summary>The scheduled task named in a wake-timer reason ("… execute 'NT TASK\HP\…\Printer Health Monitor' scheduled
        /// task …") → "\HP\…\Printer Health Monitor"; null when the timer is not a scheduled task.</summary>
        public static string? TaskPathOf(WakeTimer t)
        {
            var m = Regex.Match(t.Reason ?? "", @"NT TASK(\\[^'""]+)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>Stops a scheduled task from waking the PC ("Wake the computer to run this task" off), without disabling the
        /// task itself — Task Scheduler COM (Schedule.Service). true on success.</summary>
        public static bool StopTaskWaking(string taskPath)
        {
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return false;
                dynamic svc = Activator.CreateInstance(type)!;
                svc.Connect();
                int cut = taskPath.LastIndexOf('\\');
                string folderPath = cut > 0 ? taskPath[..cut] : "\\";
                string name = taskPath[(cut + 1)..];
                dynamic folder = svc.GetFolder(folderPath);
                dynamic task = folder.GetTask(name);
                dynamic def = task.Definition;
                def.Settings.WakeToRun = false;
                // TASK_UPDATE (4); the same account and logon type as before, no password needed for these
                folder.RegisterTaskDefinition(name, def, 4, null, null, (int)def.Principal.LogonType, null);
                AppLogger.Info($"Scheduled task {taskPath}: no longer wakes the PC");
                return true;
            }
            catch (Exception ex) { AppLogger.Warning($"StopTaskWaking {taskPath}: {ex.Message}"); return false; }
        }

        /// <summary>Prevents a device from waking the PC (powercfg /devicedisablewake).</summary>
        public static bool DisableWake(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Contains('"')) return false;
            return RunPowercfg($"/devicedisablewake \"{deviceName}\"") != null;
        }

        private static string? XmlData(string xml, string name)
        {
            var m = Regex.Match(xml, $"<Data Name=['\"]{name}['\"]>(.*?)</Data>", RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        }

        /// <summary>Last wake from sleep/hibernation and its source (System log, Power-Troubleshooter ID 1).</summary>
        public static WakeInfo? GetLastWake()
        {
            try
            {
                var q = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]")
                { ReverseDirection = true };
                using var reader = new EventLogReader(q);
                using var rec = reader.ReadEvent();
                if (rec == null) return null;
                string xml = rec.ToXml();
                string? src = XmlData(xml, "WakeSourceText");
                DateTime when = rec.TimeCreated ?? DateTime.MinValue;
                if (DateTime.TryParse(XmlData(xml, "WakeTime"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var wt))
                    when = wt.ToLocalTime();
                if (string.IsNullOrWhiteSpace(src)) src = XmlData(xml, "WakeSourceType") switch
                {
                    "0" => "Unknown source",
                    "1" => "Power button",
                    "3" => "Timer",
                    "5" => "Device",
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(src) || when == DateTime.MinValue) return null;
                return new WakeInfo(when, src!);
            }
            catch (Exception ex) { AppLogger.Debug($"PowerDiagnostics.GetLastWake: {ex.Message}"); return null; }
        }

        /// <summary>Opriri neasteptate (Kernel-Power 41) in ultimele 30 de zile.</summary>
        public static ShutdownStats? GetUnexpectedShutdowns()
        {
            try
            {
                long ms = (long)TimeSpan.FromDays(30).TotalMilliseconds;
                var q = new EventLogQuery("System", PathType.LogName,
                    $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= {ms}]]]")
                { ReverseDirection = true };
                using var reader = new EventLogReader(q);
                int count = 0; DateTime? last = null;
                var dates = new System.Collections.Generic.List<DateTime>();
                for (EventRecord? r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
                {
                    using (r)
                    {
                        count++;
                        last ??= r.TimeCreated;
                        if (r.TimeCreated.HasValue) dates.Add(r.TimeCreated.Value);
                        if (count > 500) break;
                    }
                }
                return new ShutdownStats(count, last, dates);
            }
            catch (Exception ex) { AppLogger.Debug($"PowerDiagnostics.GetUnexpectedShutdowns: {ex.Message}"); return null; }
        }
    }
}
