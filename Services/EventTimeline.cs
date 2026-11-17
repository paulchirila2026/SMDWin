using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>A Windows log event reduced to what counting needs: when, how severe, from where.</summary>
    public readonly record struct EventStamp(DateTime Time, string Level, string LogName, string Source, long EventId);

    /// <summary>Identical events (same log, source, ID and level) form a group — one row with "×N" in the table.</summary>
    public readonly record struct EventGroupKey(string LogName, string Source, long EventId, string Level);

    /// <summary>How many times a group appeared in the range, first and last time.</summary>
    public sealed record EventGroupStat(EventGroupKey Key, int Count, DateTime First, DateTime Last);

    public enum EventBucketKind { TenMinutes, Hour, Day }

    /// <summary>
    /// One bar of the Event Viewer chart: its range, how many errors (critical + error) and warnings fell in it, and the
    /// label under the bar. <paramref name="NewDay"/> = the first bar of a day (on the hourly chart its label is the day).
    /// </summary>
    public sealed record EventBucket(DateTime Start, DateTime End, int Errors, int Warnings, string Label, bool NewDay);

    /// <summary>
    /// The "problems per day / per hour" chart in Event Viewer. Bars are aligned to the calendar (whole days, fixed hours),
    /// not to the scan time, so each bar's label ("Mo 28", "14:00") says exactly what it covers.
    /// </summary>
    public static class EventTimeline
    {
        /// <summary>At most this many bars; a longer range keeps only the newest.</summary>
        public const int MaxBuckets = 62;

        /// <summary>Up to 2 hours: 10-minute bars; up to 2 days: hourly; otherwise daily.</summary>
        public static EventBucketKind KindFor(DateTime from, DateTime to)
        {
            double hours = (to - from).TotalHours;
            return hours <= 2 ? EventBucketKind.TenMinutes : hours <= 48 ? EventBucketKind.Hour : EventBucketKind.Day;
        }

        private static DateTime Floor(DateTime t, EventBucketKind kind) => kind switch
        {
            EventBucketKind.Day  => t.Date,
            EventBucketKind.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind),
            _                    => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute / 10 * 10, 0, t.Kind),
        };

        private static DateTime Next(DateTime start, EventBucketKind kind) => kind switch
        {
            EventBucketKind.Day  => start.AddDays(1),
            EventBucketKind.Hour => start.AddHours(1),
            _                    => start.AddMinutes(10),
        };

        // the day abbreviations, Monday to Sunday
        private static readonly Dictionary<string, string[]> DayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" },
            ["ro"] = new[] { "Lu", "Ma", "Mi", "Jo", "Vi", "Sâ", "Du" },
            ["de"] = new[] { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" },
            ["fr"] = new[] { "Lu", "Ma", "Me", "Je", "Ve", "Sa", "Di" },
        };

        /// <summary>"Mo 28" — the abbreviated weekday in the interface language + the day of the month.</summary>
        public static string DayLabel(DateTime day, string? language)
        {
            var names = language != null && DayNames.TryGetValue(language, out var n) ? n : DayNames["en"];
            int mondayFirst = ((int)day.DayOfWeek + 6) % 7;
            return $"{names[mondayFirst]} {day.Day}";
        }

        /// <summary>
        /// Bars for the range [from, to], with the event counts from <paramref name="stamps"/>.
        /// Only Critical / Error (as errors) and Warning levels are counted; the rest do not appear in the chart.
        /// </summary>
        public static List<EventBucket> Build(DateTime from, DateTime to, IEnumerable<EventStamp> stamps, string? language)
        {
            var list = new List<EventBucket>();
            if (to <= from) return list;
            var kind = KindFor(from, to);

            var starts = new List<DateTime>();
            for (var s = Floor(from, kind); s < to; s = Next(s, kind)) starts.Add(s);
            if (starts.Count > MaxBuckets) starts.RemoveRange(0, starts.Count - MaxBuckets);
            if (starts.Count == 0) return list;

            var errors = new int[starts.Count];
            var warnings = new int[starts.Count];
            DateTime first = starts[0], end = Next(starts[^1], kind);
            foreach (var st in stamps)
            {
                if (st.Time < first || st.Time >= end) continue;
                bool isError = st.Level is "Critical" or "Error";
                if (!isError && st.Level != "Warning") continue;
                int i = starts.BinarySearch(Floor(st.Time, kind));
                if (i < 0) continue;
                if (isError) errors[i]++; else warnings[i]++;
            }

            for (int i = 0; i < starts.Count; i++)
            {
                var s = starts[i];
                bool newDay = i == 0 || s.Date != starts[i - 1].Date;
                string label = kind switch
                {
                    EventBucketKind.Day  => DayLabel(s, language),
                    // on the hourly chart, the midnight bar carries the name of the day that begins
                    EventBucketKind.Hour => s.Hour == 0 ? DayLabel(s, language) : s.ToString("HH") + ":00",
                    _                    => s.ToString("HH:mm"),
                };
                list.Add(new EventBucket(s, Next(s, kind), errors[i], warnings[i], label, newDay));
            }
            return list;
        }

        /// <summary>
        /// Every how many bars a label is placed, so labels do not overlap: 1 = under every bar.
        /// <paramref name="slotWidth"/> = width of one bar including its gap; <paramref name="labelWidth"/> =
        /// width of one label with a little room around it.
        /// </summary>
        public static int LabelEvery(int count, double slotWidth, double labelWidth)
        {
            if (count <= 1 || slotWidth <= 0 || labelWidth <= 0) return 1;
            return Math.Clamp((int)Math.Ceiling(labelWidth / slotWidth), 1, count);
        }

        /// <summary>Does bar <paramref name="index"/> get a label? Counted back from the last bar (now),
        /// so the newest one is always labelled.</summary>
        public static bool HasLabel(int index, int count, int every)
            => every <= 1 || (count - 1 - index) % every == 0;

        /// <summary>Groups in the range, most recent first.</summary>
        public static List<EventGroupStat> Group(IEnumerable<EventStamp> stamps)
            => stamps.GroupBy(s => new EventGroupKey(s.LogName, s.Source, s.EventId, s.Level))
                     .Select(g => new EventGroupStat(g.Key, g.Count(), g.Min(s => s.Time), g.Max(s => s.Time)))
                     .OrderByDescending(g => g.Last)
                     .ToList();
    }

    /// <summary>Short text of an event: the table row first shows what it means, not the technical source.</summary>
    public static class EventText
    {
        /// <summary>First sentence of the message, on one line, at most <paramref name="max"/> characters.</summary>
        public static string FirstSentence(string? message, int max = 140)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            string s = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ").Trim();
            // end of sentence: a full stop followed by a space, after at least a few words (not "v1. 2" or "e.g. ")
            for (int i = 12; i < s.Length - 1 && i < max; i++)
                if (s[i] == '.' && s[i + 1] == ' ' && !char.IsDigit(s[i - 1]) && (i < 2 || s[i - 2] != '.'))
                { s = s[..(i + 1)]; break; }
            return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
        }

        /// <summary>
        /// Row title: the explanation from the SMD Win database if the event is known, otherwise the first sentence of the
        /// Windows message; when there is no message either, the source and ID.
        /// </summary>
        public static string Headline(string? kbTitle, string? message, string source, long eventId)
        {
            if (!string.IsNullOrWhiteSpace(kbTitle)) return kbTitle!;
            string first = FirstSentence(message);
            return first.Length > 0 ? first : $"{source} · event {eventId}";
        }
    }
}
