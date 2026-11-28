using System;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Event Viewer year map: the level buttons narrow it to one kind of event.
    public class EventCalendarLevelTests
    {
        private static EventCalendarRaw Raw()
        {
            var raw = new EventCalendarRaw();
            var d1 = new DateTime(2026, 5, 1);
            var d2 = new DateTime(2026, 5, 2);
            raw.Counts[(d1, "UnknownSourceX", 99999, (byte)3)] = 4;   // a warning only on day 1
            raw.Counts[(d2, "UnknownSourceX", 99998, (byte)2)] = 2;   // an error only on day 2
            return raw;
        }

        [Fact]
        public void WarningFilterShowsOnlyTheWarningDay()
        {
            var days = EventCalendar.Build(Raw(), Array.Empty<DateTime>(), "Warning");
            Assert.True(days.ContainsKey(new DateTime(2026, 5, 1)));
            Assert.False(days.ContainsKey(new DateTime(2026, 5, 2)));
            Assert.Equal(CalendarDayLevel.Minor, days[new DateTime(2026, 5, 1)].Level);
        }

        [Fact]
        public void ErrorFilterShowsOnlyTheErrorDay()
        {
            var days = EventCalendar.Build(Raw(), Array.Empty<DateTime>(), "Error");
            Assert.False(days.ContainsKey(new DateTime(2026, 5, 1)));
            Assert.True(days.ContainsKey(new DateTime(2026, 5, 2)));
        }

        [Fact]
        public void WarningFilterIgnoresCrashDumps()
        {
            var days = EventCalendar.Build(new EventCalendarRaw(), new[] { new DateTime(2026, 5, 3) }, "Warning");
            Assert.Empty(days);
        }
    }
}
