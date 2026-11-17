using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>
    /// The recent starts, shut-downs, sleeps and wake-ups of the PC, read from the System log (Power page,
    /// "Recent starts and stops"), and from them how many hours a day the PC is on (for the monthly energy estimate).
    /// Sources:
    ///  • Kernel-Boot 27 — Windows started; BootType 0 = full start, 1 = Fast Startup, 2 = resumed from hibernate;
    ///  • Kernel-General 13 — Windows shut down normally;
    ///  • Kernel-Power 41 — the previous session ended without a shut-down (power loss, freeze, forced off);
    ///  • Kernel-Power 42 — going to sleep / hibernate;
    ///  • Power-Troubleshooter 1 — woke from sleep, with what woke it.
    /// </summary>
    public static class PowerEventsService
    {
        public enum Kind { Start, FastStart, Resume, ShutDown, Unexpected, Sleep, Wake }

        public sealed record PowerEvent(DateTime When, Kind Kind, string Detail);

        private const string Query =
            "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=13) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=42)) or " +
            "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)" +
            " and TimeCreated[timediff(@SystemTime) <= {0}]]]";

        /// <summary>The events of the last <paramref name="days"/> days, newest first (at most <paramref name="max"/>).</summary>
        public static List<PowerEvent> Read(int days = 14, int max = 400)
        {
            var list = new List<PowerEvent>();
            try
            {
                long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
                var q = new EventLogQuery("System", PathType.LogName, string.Format(Query, ms)) { ReverseDirection = true };
                using var reader = new EventLogReader(q);
                for (EventRecord? r = reader.ReadEvent(); r != null && list.Count < max; r = reader.ReadEvent())
                {
                    using (r)
                    {
                        if (r.TimeCreated is not DateTime when) continue;
                        string provider = r.ProviderName ?? "";
                        string xml;
                        try { xml = r.ToXml(); } catch (Exception ex) { AppLogger.Debug($"PowerEvents xml: {ex.Message}"); xml = ""; }
                        switch (r.Id)
                        {
                            case 27 when provider.EndsWith("Kernel-Boot", StringComparison.OrdinalIgnoreCase):
                                list.Add(new PowerEvent(when, Data(xml, "BootType") switch { "1" => Kind.FastStart, "2" => Kind.Resume, _ => Kind.Start }, ""));
                                break;
                            case 13 when provider.EndsWith("Kernel-General", StringComparison.OrdinalIgnoreCase):
                                list.Add(new PowerEvent(when, Kind.ShutDown, ""));
                                break;
                            case 41:
                                list.Add(new PowerEvent(when, Kind.Unexpected, ""));
                                break;
                            case 42:
                                // TargetState 5 / 6 = hibernate, the rest = sleep
                                list.Add(new PowerEvent(when, Kind.Sleep, Data(xml, "TargetState") is "5" or "6" ? "hibernate" : ""));
                                break;
                            case 1 when provider.EndsWith("Power-Troubleshooter", StringComparison.OrdinalIgnoreCase):
                                string src = Data(xml, "WakeSourceText") ?? "";
                                if (src.Length == 0) src = Data(xml, "WakeSourceType") switch { "1" => "Power button", "3" => "Timer", _ => "" };
                                list.Add(new PowerEvent(when, Kind.Wake, src));
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"PowerEventsService.Read: {ex.Message}"); }
            return list;
        }

        /// <summary>
        /// The average hours a day the PC was on over the last days, from the events (newest first): on from a start or a
        /// wake-up until the next shut-down, sleep or unexpected stop. Null when the log has too little to say.
        /// </summary>
        public static double? HoursOnPerDay(List<PowerEvent> newestFirst, int days = 7)
        {
            try
            {
                DateTime from = DateTime.Now.AddDays(-days);
                var ev = newestFirst.Where(e => e.When >= from).OrderBy(e => e.When).ToList();
                if (ev.Count < 2) return null;
                double hours = 0;
                DateTime? onSince = null;
                foreach (var e in ev)
                {
                    // "unexpected shut-down" is written at the next start, so it says nothing about when the PC was on
                    if (e.Kind == Kind.Unexpected) continue;
                    bool up = e.Kind is Kind.Start or Kind.FastStart or Kind.Resume or Kind.Wake;
                    // a start without a stop before it (crash, power loss): the end of that session is unknown, so it is left out
                    if (up) { onSince = e.When; continue; }
                    if (onSince is DateTime s) { hours += (e.When - s).TotalHours; onSince = null; }
                }
                if (onSince is DateTime still) hours += (DateTime.Now - still).TotalHours;
                double span = Math.Max(1, (DateTime.Now - ev[0].When).TotalDays);
                double perDay = hours / Math.Min(days, span);
                return perDay is > 0.2 and <= 24 ? perDay : null;
            }
            catch (Exception ex) { AppLogger.Debug($"PowerEventsService.HoursOnPerDay: {ex.Message}"); return null; }
        }

        private static string? Data(string xml, string name)
        {
            if (xml.Length == 0) return null;
            var m = Regex.Match(xml, "<Data Name=['\"]" + Regex.Escape(name) + "['\"]>([^<]*)</Data>");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
