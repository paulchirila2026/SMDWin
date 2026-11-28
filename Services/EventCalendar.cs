using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>How bad a day was in the Event Viewer calendar (the worst event of the day decides).</summary>
    public enum CalendarDayLevel { None = 0, Minor = 1, Serious = 2, BlueScreen = 3 }

    /// <summary>One day of the calendar: the counts per kind and the resulting level.</summary>
    public sealed class CalendarDay
    {
        public DateTime Date;
        public int BlueScreens, Serious, Minor;
        public CalendarDayLevel Level => BlueScreens > 0 ? CalendarDayLevel.BlueScreen : Serious > 0 ? CalendarDayLevel.Serious
                                       : Minor > 0 ? CalendarDayLevel.Minor : CalendarDayLevel.None;
    }

    /// <summary>The raw counts read from the logs: per day, per (source, ID, level). Classified later on the UI thread.</summary>
    public sealed class EventCalendarRaw
    {
        public readonly Dictionary<(DateTime day, string source, long id, byte level), int> Counts = new();
        /// <summary>The oldest record still in the System log: before it there is no data (the log was overwritten).</summary>
        public DateTime? OldestSystem;
        public bool Partial;
    }

    /// <summary>
    /// The "last year" calendar of Event Viewer (GitHub style). Reads only the time, level, source and ID of the error / critical /
    /// warning events of the System and Application logs — no descriptions — so a whole year takes a few seconds at most.
    /// Red = blue screen (BugCheck 1001), orange = serious errors, light orange = small problems, neutral = nothing worth noting.
    /// </summary>
    public static class EventCalendar
    {
        private static readonly string[] Logs = { "System", "Application" };
        private const int CapPerLog = 400_000;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

        public static Task<EventCalendarRaw> ReadAsync(DateTime from, CancellationToken ct) => Task.Run(() =>
        {
            var raw = new EventCalendarRaw();
            var sw = Stopwatch.StartNew();
            string fromStr = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            foreach (var log in Logs)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var (srcPath, srcType) = AnalyzedWindows.LogSource(log);   // this PC or the Windows on another drive
                    var q = new EventLogQuery(srcPath, srcType,
                        $"*[System[TimeCreated[@SystemTime>='{fromStr}'] and (Level=1 or Level=2 or Level=3)]]");
                    using var reader = new EventLogReader(q) { BatchSize = 256 };
                    int n = 0;
                    EventRecord? rec;
                    while (!ct.IsCancellationRequested && (rec = reader.ReadEvent()) != null)
                    {
                        using (rec)
                        {
                            if (++n > CapPerLog || sw.Elapsed > Budget) { raw.Partial = true; break; }
                            var t = rec.TimeCreated;
                            if (t == null) continue;
                            var key = (t.Value.Date, rec.ProviderName ?? "", (long)rec.Id, rec.Level ?? 0);
                            raw.Counts[key] = raw.Counts.TryGetValue(key, out int c) ? c + 1 : 1;
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"EventCalendar {log}: {ex.Message}"); }
            }
            // the first record still kept in the System log
            try
            {
                var (sp, st) = AnalyzedWindows.LogSource("System");
                using var first = new EventLogReader(new EventLogQuery(sp, st, "*"));
                using var r0 = first.ReadEvent();
                raw.OldestSystem = r0?.TimeCreated;
            }
            catch (Exception ex) { AppLogger.Debug($"EventCalendar oldest: {ex.Message}"); }
            return raw;
        }, ct);

        /// <summary>
        /// The kind of one event: a blue screen (BugCheck 1001), serious (the event database says "serious", or an unknown
        /// critical event), minor (database "watch", or an unknown error), or nothing (routine / harmless / plain warnings).
        /// Called on the UI thread (the event database is not thread safe).
        /// </summary>
        public static CalendarDayLevel Classify(string source, long id, byte level)
        {
            if (id == 1001 && (source.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)
                               || source.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)))
                return CalendarDayLevel.BlueScreen;
            var kb = EventKnowledge.Find(source, id);
            if (kb != null)
                return kb.Severity switch
                {
                    "serious" => level <= 3 ? CalendarDayLevel.Serious : CalendarDayLevel.None,
                    "watch"   => CalendarDayLevel.Minor,
                    _         => CalendarDayLevel.None,
                };
            var ex = EventExplainer.Explain(source, id);
            if (ex?.weight == EventExplainer.Weight.Harmless) return CalendarDayLevel.None;
            if (ex?.weight == EventExplainer.Weight.Serious) return CalendarDayLevel.Serious;
            return level switch
            {
                1 => CalendarDayLevel.Serious,
                2 => CalendarDayLevel.Minor,
                _ => CalendarDayLevel.None,   // unknown warnings: too many and too vague to colour a day
            };
        }

        /// <summary>The days from the raw counts (+ the dates of the crash dumps found on disk, which also mean a blue screen).</summary>
        /// <param name="onlyLevel">
        /// null = the normal calendar (every error / critical event, weighed by what it means). "Critical", "Error" or "Warning" =
        /// only events of that Windows level are counted (a blue screen counts as critical and as an error): a day with any of
        /// them is orange (full orange when the event database calls it serious or it is critical, light orange otherwise).
        /// </param>
        public static Dictionary<DateTime, CalendarDay> Build(EventCalendarRaw raw, IEnumerable<DateTime> dumpTimes, string? onlyLevel = null)
        {
            byte? want = onlyLevel switch { "Critical" => (byte)1, "Error" => (byte)2, "Warning" => (byte)3, _ => null };
            var days = new Dictionary<DateTime, CalendarDay>();
            var kinds = new Dictionary<(string, long, byte), CalendarDayLevel>();
            CalendarDay Day(DateTime d) => days.TryGetValue(d, out var x) ? x : days[d] = new CalendarDay { Date = d };
            foreach (var (k, n) in raw.Counts)
            {
                if (want.HasValue && k.level != want.Value) continue;
                var kk = (k.source, k.id, k.level);
                if (!kinds.TryGetValue(kk, out var kind)) kinds[kk] = kind = Classify(k.source, k.id, k.level);
                if (want.HasValue && kind != CalendarDayLevel.BlueScreen)
                    kind = want == 1 || kind == CalendarDayLevel.Serious ? CalendarDayLevel.Serious : CalendarDayLevel.Minor;
                switch (kind)
                {
                    case CalendarDayLevel.BlueScreen: Day(k.day).BlueScreens += n; break;
                    case CalendarDayLevel.Serious:    Day(k.day).Serious += n; break;
                    case CalendarDayLevel.Minor:      Day(k.day).Minor += n; break;
                }
            }
            // a dump written without the 1001 event (the log was cleared, or the PC restarted before Windows could write it)
            foreach (var t in dumpTimes)
            {
                if (want == 3) break;   // a blue screen is not a warning
                var d = Day(t.Date);
                if (d.BlueScreens == 0) d.BlueScreens = 1;
            }
            return days;
        }
    }
}
