using System;
using System.Collections.Generic;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The Event Viewer chart and grouping: calendar-aligned bars, labels, counting.</summary>
    public class EventTimelineTests
    {
        private static EventStamp S(DateTime t, string level, string source = "Src", long id = 1, string log = "System")
            => new(t, level, log, source, id);

        [Fact]
        public void Kind_DependsOnTheRange()
        {
            var now = new DateTime(2026, 10, 2, 9, 30, 0);
            Assert.Equal(EventBucketKind.TenMinutes, EventTimeline.KindFor(now.AddHours(-1), now));
            Assert.Equal(EventBucketKind.Hour, EventTimeline.KindFor(now.AddHours(-6), now));
            Assert.Equal(EventBucketKind.Hour, EventTimeline.KindFor(now.AddDays(-1), now));
            Assert.Equal(EventBucketKind.Day, EventTimeline.KindFor(now.AddDays(-7), now));
            Assert.Equal(EventBucketKind.Day, EventTimeline.KindFor(now.AddDays(-30), now));
        }

        [Fact]
        public void Days_AreWholeCalendarDays_WithADayLabelUnderEachBar()
        {
            // the interval from the capture: 25.09 00:00 → 02.10 09:44 (8 calendar days)
            var from = new DateTime(2026, 9, 25);
            var to = new DateTime(2026, 10, 2, 9, 44, 0);
            var b = EventTimeline.Build(from, to, Array.Empty<EventStamp>(), "ro");
            Assert.Equal(8, b.Count);
            Assert.Equal("Vi 25|Sâ 26|Du 27|Lu 28|Ma 29|Mi 30|Jo 1|Vi 2", string.Join("|", b.Select(x => x.Label)));
            Assert.True(b.All(x => x.Start.TimeOfDay == TimeSpan.Zero && (x.End - x.Start).TotalHours == 24));
            Assert.Equal(new DateTime(2026, 10, 2), b[^1].Start);
        }

        [Fact]
        public void Days_CountErrorsAndWarnings_InTheRightBar()
        {
            var from = new DateTime(2026, 9, 25);
            var to = new DateTime(2026, 10, 2, 9, 44, 0);
            var stamps = new[]
            {
                S(new DateTime(2026, 9, 25, 0, 0, 0), "Error"),          // prima secunda a primei zile
                S(new DateTime(2026, 9, 25, 23, 59, 59), "Critical"),
                S(new DateTime(2026, 9, 26, 0, 0, 0), "Warning"),
                S(new DateTime(2026, 10, 2, 9, 0, 0), "Warning"),
                S(new DateTime(2026, 10, 2, 9, 1, 0), "Information"),    // does not appear in the chart
                S(new DateTime(2026, 9, 24, 23, 0, 0), "Error"),         // before the interval
            };
            var b = EventTimeline.Build(from, to, stamps, "en");
            Assert.Equal("2/0|0/1|0/0|0/0|0/0|0/0|0/0|0/1", string.Join("|", b.Select(x => $"{x.Errors}/{x.Warnings}")));
        }

        [Fact]
        public void Hours_AreClockHours_AndMidnightCarriesTheDayName()
        {
            var from = new DateTime(2026, 10, 1, 21, 20, 0);
            var to = new DateTime(2026, 10, 2, 2, 10, 0);
            var b = EventTimeline.Build(from, to, new[] { S(new DateTime(2026, 10, 2, 0, 30, 0), "Error") }, "ro");
            Assert.Equal("21:00|22:00|23:00|Vi 2|01:00|02:00", string.Join("|", b.Select(x => x.Label)));
            Assert.Equal(1, b[3].Errors);
            Assert.True(b[3].NewDay);
            Assert.True(b[0].NewDay);        // the first bar
            Assert.False(b[1].NewDay);
        }

        [Fact]
        public void TenMinutes_ForTheLastHour()
        {
            var to = new DateTime(2026, 10, 2, 9, 44, 0);
            var b = EventTimeline.Build(to.AddHours(-1), to, new[] { S(new DateTime(2026, 10, 2, 9, 41, 0), "Warning") }, "en");
            Assert.Equal("08:40|08:50|09:00|09:10|09:20|09:30|09:40", string.Join("|", b.Select(x => x.Label)));
            Assert.Equal(1, b[^1].Warnings);
        }

        [Fact]
        public void LongRanges_KeepTheNewestBars()
        {
            var to = new DateTime(2026, 10, 2, 12, 0, 0);
            var b = EventTimeline.Build(to.AddDays(-200), to, Array.Empty<EventStamp>(), "en");
            Assert.Equal(EventTimeline.MaxBuckets, b.Count);
            Assert.Equal(new DateTime(2026, 10, 2), b[^1].Start);
        }

        [Fact]
        public void EmptyOrReversedRange_GivesNoBars()
        {
            var t = new DateTime(2026, 10, 2);
            Assert.Empty(EventTimeline.Build(t, t, Array.Empty<EventStamp>(), "en"));
            Assert.Empty(EventTimeline.Build(t, t.AddHours(-1), Array.Empty<EventStamp>(), "en"));
        }

        [Fact]
        public void DayLabel_FollowsTheLanguage_MondayFirst()
        {
            var monday = new DateTime(2026, 9, 28);
            Assert.Equal("Lu 28", EventTimeline.DayLabel(monday, "ro"));
            Assert.Equal("Mo 28", EventTimeline.DayLabel(monday, "en"));
            Assert.Equal("Di 29", EventTimeline.DayLabel(monday.AddDays(1), "de"));
            Assert.Equal("Di 4", EventTimeline.DayLabel(new DateTime(2026, 10, 4), "fr"));     // duminica
            Assert.Equal("Su 4", EventTimeline.DayLabel(new DateTime(2026, 10, 4), "xx"));     // unknown language → English
            Assert.Equal("Su 4", EventTimeline.DayLabel(new DateTime(2026, 10, 4), null));
        }

        [Fact]
        public void Labels_UnderEveryBar_WhenTheyFit_OtherwiseThinnedFromTheNewest()
        {
            Assert.Equal(1, EventTimeline.LabelEvery(8, 180, 44));     // 8 days on a wide chart
            Assert.Equal(1, EventTimeline.LabelEvery(30, 46, 44));     // 30 days, they just fit
            Assert.Equal(2, EventTimeline.LabelEvery(30, 30, 44));
            Assert.Equal(3, EventTimeline.LabelEvery(49, 18, 44));
            Assert.Equal(1, EventTimeline.LabelEvery(1, 5, 44));
            Assert.Equal(1, EventTimeline.LabelEvery(10, 0, 44));

            // the last bar (now) is always labelled
            Assert.True(EventTimeline.HasLabel(29, 30, 2));
            Assert.False(EventTimeline.HasLabel(28, 30, 2));
            Assert.True(EventTimeline.HasLabel(27, 30, 2));
            Assert.True(Enumerable.Range(0, 30).All(i => EventTimeline.HasLabel(i, 30, 1)));
        }

        [Fact]
        public void Group_CountsEveryOccurrence_NewestGroupFirst()
        {
            var d = new DateTime(2026, 10, 1);
            var stamps = new List<EventStamp>
            {
                S(d.AddHours(1), "Warning", "DCOM", 10016),
                S(d.AddHours(5), "Warning", "DCOM", 10016),
                S(d.AddHours(3), "Error", "Service Control Manager", 7000),
                S(d.AddHours(9), "Warning", "DCOM", 10016),
                S(d.AddHours(2), "Error", "DCOM", 10016),                   // another level → another group
                S(d.AddHours(4), "Warning", "DCOM", 10016, "Application"),  // another log → another group
            };
            var g = EventTimeline.Group(stamps);
            Assert.Equal(4, g.Count);
            Assert.Equal(new EventGroupKey("System", "DCOM", 10016, "Warning"), g[0].Key);
            Assert.Equal(3, g[0].Count);
            Assert.Equal(d.AddHours(1), g[0].First);
            Assert.Equal(d.AddHours(9), g[0].Last);
            Assert.Equal(stamps.Count, g.Sum(x => x.Count));
            Assert.Empty(EventTimeline.Group(Array.Empty<EventStamp>()));
        }

        [Fact]
        public void FirstSentence_IsOneShortLine()
        {
            Assert.Equal("The Software Protection service has stopped.",
                EventText.FirstSentence("The Software Protection service has stopped.\r\n\r\nMore details follow here."));
            Assert.Equal("Name resolution for the name wpad timed out after none of the configured DNS servers responded.",
                EventText.FirstSentence("Name resolution for the name wpad timed out after none of the configured DNS servers responded. Client PID 1234."));
            // without a dot: the whole text, cut to length
            string longText = new string('a', 300);
            Assert.Equal(140, EventText.FirstSentence(longText).Length);
            Assert.True(EventText.FirstSentence(longText).EndsWith("…"));
            // a version number is not the end of a sentence
            Assert.Equal("Windows version 10.0. 22621 started the update", EventText.FirstSentence("Windows  version 10.0. 22621 started the update"));
            Assert.Equal("", EventText.FirstSentence("   "));
            Assert.Equal("", EventText.FirstSentence(null));
        }

        [Fact]
        public void Headline_PrefersTheKnownTitle_ThenTheMessage_ThenTheSource()
        {
            Assert.Equal("DCOM permission notice", EventText.Headline("DCOM permission notice", "raw text", "DCOM", 10016));
            Assert.Equal("The service stopped.", EventText.Headline("", "The service stopped. Details.", "SCM", 7036));
            Assert.Equal("LsaSrv · event 6155", EventText.Headline(null, "", "LsaSrv", 6155));
        }
    }
}
