using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The one-point-per-second history behind the charts with a selectable time window.
    public class SecondHistoryTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private static float[] V(float x) => new[] { x };

        [Fact]
        public void OneReadingPerSecond_GivesOnePointPerSecond()
        {
            var h = new SecondHistory(1);
            for (int t = 0; t < 10; t++) Assert.Equal(1, h.Push(T0.AddSeconds(t), V(t)));
            Assert.Equal(10L, h.Count);
            var w = h.Window(0, 5);
            Assert.Equal(5, w.Count);
            Assert.Equal(5.0, w[0], 6);
            Assert.Equal(9.0, w[4], 6);
        }

        [Fact]
        public void ReadingEveryThreeSeconds_KeepsRealTime_WithAStraightLineBetween()
        {
            var h = new SecondHistory(1);
            h.Push(T0, V(10));
            Assert.Equal(3, h.Push(T0.AddSeconds(3), V(40)));
            Assert.Equal(4L, h.Count);
            var w = h.Window(0, 10);
            Assert.Equal(4, w.Count);
            Assert.Equal(10.0, w[0], 4);
            Assert.Equal(20.0, w[1], 4);
            Assert.Equal(30.0, w[2], 4);
            Assert.Equal(40.0, w[3], 4);
        }

        [Fact]
        public void SlightlyIrregularTicks_DoNotDriftInTime()
        {
            var h = new SecondHistory(1);
            var t = T0;
            h.Push(t, V(0));
            for (int i = 0; i < 600; i++) { t = t.AddSeconds(1.04); h.Push(t, V(i)); }   // 10 minutes with 1.04 s ticks
            long expected = 1 + (long)Math.Round(600 * 1.04);
            Assert.True(Math.Abs(h.Count - expected) <= 1, $"count={h.Count} expected≈{expected}");
        }

        [Fact]
        public void TwoReadingsInTheSameSecond_ReplaceTheLastPoint()
        {
            var h = new SecondHistory(1);
            h.Push(T0, V(1));
            Assert.Equal(0, h.Push(T0.AddSeconds(0.2), V(7)));
            Assert.Equal(1L, h.Count);
            Assert.Equal(7f, h.Last(0));
        }

        [Fact]
        public void LongAbsence_LeavesAGap_NotInventedValues()
        {
            var h = new SecondHistory(1);
            h.Push(T0, V(10));
            h.Push(T0.AddSeconds(60), V(50));
            var w = h.Window(0, 100);
            Assert.Equal(61, w.Count);
            Assert.Equal(10.0, w[0], 6);
            Assert.True(w.Skip(1).Take(59).All(double.IsNaN));
            Assert.Equal(50.0, w[60], 6);
        }

        [Fact]
        public void GapFlag_LeavesTheIntervalEmpty_EvenWhenShort()
        {
            var h = new SecondHistory(1);
            h.Push(T0, V(10));
            h.Push(T0.AddSeconds(4), V(50), gap: true);
            var w = h.Window(0, 10);
            Assert.True(double.IsNaN(w[1]) && double.IsNaN(w[2]) && double.IsNaN(w[3]));
            Assert.Equal(50.0, w[4], 6);
        }

        [Fact]
        public void MissingSensor_StaysAGap()
        {
            var h = new SecondHistory(2);
            h.Push(T0, new[] { 1f, float.NaN });
            h.Push(T0.AddSeconds(1), new[] { 2f, float.NaN });
            Assert.True(h.Window(1, 5).All(double.IsNaN));
            Assert.Equal(2.0, h.Window(0, 5)[1], 6);
        }

        [Fact]
        public void OlderThanCapacity_IsDropped()
        {
            var h = new SecondHistory(1, capacity: 10);
            for (int t = 0; t < 25; t++) h.Push(T0.AddSeconds(t), V(t));
            Assert.Equal(25L, h.Count);
            Assert.Equal(10, h.Stored);
            var w = h.Window(0, 100);
            Assert.Equal(10, w.Count);
            Assert.Equal(15.0, w[0], 6);
            Assert.Equal(24.0, w[9], 6);
        }

        [Fact]
        public void EmptyHistory_HasNoWindow()
        {
            var h = new SecondHistory(1);
            Assert.Empty(h.Window(0, 60));
            Assert.True(float.IsNaN(h.Last(0)));
        }
    }
}
