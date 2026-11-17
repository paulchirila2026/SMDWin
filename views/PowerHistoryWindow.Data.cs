using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Canvas              = System.Windows.Controls.Canvas;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox          = System.Windows.MessageBox;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using ToolTip             = System.Windows.Controls.ToolTip;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Cursors             = System.Windows.Input.Cursors;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;

namespace SMDWin.Views
{
    public partial class PowerHistoryWindow
    {
        // ── Event Log Reading ─────────────────────────────────────────────────
        private static (List<PowerEvent> events, bool hasSecLog) ReadEvents(int days)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
                var val = key?.GetValue("HiberbootEnabled");
                AppLogger.Info("PowerHistory: Fast Startup (HiberbootEnabled) = {0} — if it is 1, a normal 'Shut down' was logged as EventID 42 (Sleep), NOT as a clean shutdown.",
                    val?.ToString() ?? "unknown (key/value missing)");
            }
            catch (Exception ex) { AppLogger.Debug("PowerHistory: reading HiberbootEnabled failed:" + ex.Message); }

            var events = new List<PowerEvent>();
            var since = DateTime.UtcNow.AddDays(-days);
            bool hasSecLog = false;

            // System log
            var sysQ = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID=6005 or EventID=6006 or EventID=6008 or EventID=41 or EventID=42 or EventID=107) and TimeCreated[@SystemTime>='{since:yyyy-MM-ddTHH:mm:ss}Z']]]");
            try
            {
                using var r = new EventLogReader(sysQ);
                EventRecord? rec;
                while ((rec = r.ReadEvent()) != null)
                    using (rec)
                    {
                        var t    = rec.TimeCreated ?? DateTime.MinValue;
                        var kind = rec.Id switch {
                            6005 => EventKind.Boot,
                            6006 => EventKind.Shutdown,
                            6008 => EventKind.UnexpectedShutdown,
                            41   => EventKind.Crash,
                            42   => EventKind.Sleep,
                            107  => EventKind.Resume,
                            _    => (EventKind?)null
                        };
                        if (kind == null) continue;
                        events.Add(new PowerEvent
                        {
                            Time  = t, Kind = kind.Value,
                            Notes = rec.Id == 41   ? "Kernel-Power 41 (crash/power loss)"
                                  : rec.Id == 6008 ? "System did not shut down cleanly"
                                  : rec.Id == 42   ? "System entered sleep/standby"
                                  : rec.Id == 107  ? "System resumed from sleep" : ""
                        });
                    }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            AppLogger.Info("PowerHistory: System log query ('since' UTC={0:yyyy-MM-ddTHH:mm:ss}Z) — {1} Boot/Shutdown/Sleep/Resume/Crash events found in total.", since, events.Count);

            // Security log (Admin)
            try
            {
                var secQ = new EventLogQuery("Security", PathType.LogName,
                    $"*[System[(EventID=4624 or EventID=4634) and TimeCreated[@SystemTime>='{since:yyyy-MM-ddTHH:mm:ss}Z']]]");
                using var sr = new EventLogReader(secQ);
                EventRecord? rec; int cnt = 0;
                while ((rec = sr.ReadEvent()) != null && cnt < 600)
                    using (rec)
                    {
                        var t    = rec.TimeCreated ?? DateTime.MinValue;
                        var kind = rec.Id == 4624 ? EventKind.Logon : EventKind.Logoff;
                        if (kind == EventKind.Logon)
                        {
                            string? lt = null;
                            try { lt = rec.Properties?.Count > 8 ? rec.Properties[8].Value?.ToString() : null; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                            if (lt != "2") continue;
                        }
                        events.Add(new PowerEvent { Time = t, Kind = kind }); cnt++;
                    }
                hasSecLog = true;
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // Calculate durations: Boot→next Shutdown/Crash
            var sorted = events.OrderBy(e => e.Time).ToList();

            var deduped = new List<PowerEvent>();
            foreach (var ev in sorted)
            {
                if (ev.Kind == EventKind.Logon
                    && deduped.Count > 0
                    && deduped[^1].Kind == EventKind.Logon
                    && (ev.Time - deduped[^1].Time).TotalSeconds < 5)
                    continue; // duplicate — skip
                deduped.Add(ev);
            }
            sorted = deduped;

            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Kind != EventKind.Boot) continue;
                var next = sorted.Skip(i + 1)
                    .FirstOrDefault(e => e.Kind is EventKind.Shutdown or EventKind.UnexpectedShutdown or EventKind.Crash);
                if (next == null) continue;
                var dur = next.Time - sorted[i].Time;
                sorted[i].Duration = $"{(int)dur.TotalHours}h {dur.Minutes:D2}m";
                // Also stamp the shutdown with session duration
                next.Duration = sorted[i].Duration;
            }

            return (sorted, hasSecLog);
        }

        // ── Day Summary Builder ───────────────────────────────────────────────
        private static List<DaySummary> BuildDaySummaries(List<PowerEvent> events, int days)
        {
            var result = new List<DaySummary>();
            var today  = DateTime.Today;

            bool carriedInSleep = false;

            for (int d = days - 1; d >= 0; d--)
            {
                var day     = today.AddDays(-d);
                var summary = new DaySummary { Date = day };

                // Was machine running at start of this day?
                var lastBootBefore = events
                    .Where(e => e.Kind == EventKind.Boot && e.Time.Date < day)
                    .OrderByDescending(e => e.Time)
                    .FirstOrDefault();

                double? runStart = null;
                if (lastBootBefore != null)
                {
                    bool hadShutdown = events.Any(e =>
                        e.Kind is EventKind.Shutdown or EventKind.UnexpectedShutdown or EventKind.Crash
                        && e.Time > lastBootBefore.Time && e.Time.Date < day);
                    if (!hadShutdown) runStart = 0.0;
                }

                var dayEvts = events
                    .Where(e => e.Kind is EventKind.Boot or EventKind.Shutdown
                             or EventKind.UnexpectedShutdown or EventKind.Crash
                             or EventKind.Sleep or EventKind.Resume
                             && e.Time.Date == day)
                    .OrderBy(e => e.Time)
                    .ToList();

                if (dayEvts.Count > 0)
                    AppLogger.Debug("PowerHistory: day {0:yyyy-MM-dd} — {1} events: {2}",
                        day, dayEvts.Count,
                        string.Join(" | ", dayEvts.Select(e => $"{e.Time:HH:mm:ss} {e.Kind}")));
                else
                    AppLogger.Debug("PowerHistory: day {0:yyyy-MM-dd} — 0 events in the queried window.", day);

                bool inSleep = runStart.HasValue && carriedInSleep;

                int dayEvtCount = 0;
                foreach (var ev in dayEvts)
                {
                    dayEvtCount++;
                    double frac = ev.Time.TimeOfDay.TotalSeconds / 86400.0;
                    if (ev.Kind == EventKind.Boot)
                    {
                        if (runStart.HasValue)
                            summary.Segments.Add((runStart.Value, frac, false, inSleep, false));
                        runStart = frac;
                        inSleep  = false;
                    }
                    else if (ev.Kind == EventKind.Sleep)
                    {
                        // Rare case: two consecutive Sleeps without a Resume between them (possible with Modern Standby).
                        if (runStart.HasValue)
                            summary.Segments.Add((runStart.Value, frac, false, inSleep, false));
                        runStart = frac;   // sleep starts here
                        inSleep  = true;
                    }
                    else if (ev.Kind == EventKind.Resume)
                    {
                        // Close sleep segment, start new running segment
                        if (runStart.HasValue)
                            summary.Segments.Add((runStart.Value, frac, false, true, false)); // isSleep=true
                        runStart = frac;
                        inSleep  = false;
                    }
                    else if (ev.Kind == EventKind.Shutdown)
                    {
                        if (runStart.HasValue)
                            summary.Segments.Add((runStart.Value, frac, false, inSleep, false));
                        runStart = null; inSleep = false;
                    }
                    else // crash / unexpected
                    {
                        if (runStart.HasValue)
                            summary.Segments.Add((runStart.Value, frac, true, false, false));
                        runStart = null; inSleep = false;
                        summary.Crashes++;
                    }
                }

                // Still running / sleeping at end of day
                if (runStart.HasValue)
                {
                    double endFrac = day == today
                        ? DateTime.Now.TimeOfDay.TotalSeconds / 86400.0
                        : 1.0;
                    bool noEvidence = dayEvtCount == 0 && !carriedInSleep;
                    summary.Segments.Add((runStart.Value, endFrac, false, inSleep, noEvidence));
                }

                if (dayEvtCount == 0 && runStart.HasValue && day != today)
                    AppLogger.Debug($"PowerHistory: day without events ({day:yyyy-MM-dd}) —" +
                                     $"state carried over from the previous day: carriedInSleep={carriedInSleep}" +
                                     $"→ drawn as {(carriedInSleep ?"Sleep" : "Unknown (no evidence)")}.");

                carriedInSleep = runStart.HasValue && inSleep;

                summary.UptimeHours = summary.Segments.Where(s => !s.IsSleep && !s.IsBad && !s.IsUnknown).Sum(s => (s.EndFrac - s.StartFrac) * 24.0);
                result.Add(summary);
            }
            return result;
        }
    }
}
