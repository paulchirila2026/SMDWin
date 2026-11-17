using System.Collections.Generic;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The chart scale (the peak label) and reducing long series to a reasonable number of points.
    public class ChartScaleTests
    {
        [Theory]
        [InlineData(0.7, 1)]
        [InlineData(1, 1)]
        [InlineData(1.2, 2)]
        [InlineData(3, 5)]
        [InlineData(7, 10)]
        [InlineData(64, 100)]
        [InlineData(100, 100)]
        [InlineData(101, 200)]
        [InlineData(4999, 5000)]
        public void NiceCeil_RoundsUpTo_1_2_5(double value, double expected)
            => Assert.Equal(expected, ChartScale.NiceCeil(value), 6);

        [Fact]
        public void NiceCeil_OfZeroOrGarbage_IsOne()
        {
            Assert.Equal(1.0, ChartScale.NiceCeil(0), 6);
            Assert.Equal(1.0, ChartScale.NiceCeil(double.NaN), 6);
        }

        [Theory]
        [InlineData(0, "64")]            // sub prag: scala minima → 100 KB/s
        [InlineData(300, "500 KB/s")]
        [InlineData(900, "1 MB/s")]      // 1000 KB/s does not exist as a label: it switches to MB
        [InlineData(1500, "2 MB/s")]
        [InlineData(11600, "20 MB/s")]
        [InlineData(95000, "200 MB/s")]       // 92.8 MB/s + a 10% margin goes over 100
        public void RateScale_IsRoundInTheDisplayedUnit(double peakKBs, string label)
        {
            double max = ChartScale.NiceRateMaxKBs(peakKBs);
            Assert.True(max >= peakKBs * ChartScale.Headroom || peakKBs == 0);
            if (peakKBs == 0) { Assert.Equal("100 KB/s", ChartScale.FormatRateKBs(max)); return; }
            Assert.Equal(label, ChartScale.FormatRateKBs(max));
        }

        [Theory]
        [InlineData(0.5, "0.5 KB/s")]
        [InlineData(512, "512 KB/s")]
        [InlineData(1536, "1.5 MB/s")]
        [InlineData(10240, "10 MB/s")]
        [InlineData(1800000, "1.7 GB/s")]
        public void FormatRate(double kbs, string text) => Assert.Equal(text, ChartScale.FormatRateKBs(kbs));

        [Theory]
        [InlineData(698, 1024, "1 GB")]
        [InlineData(120, 200, "200 MB")]
        [InlineData(3000, 5120, "5 GB")]
        public void SizeScale(double peakMB, double expectedMax, string label)
        {
            double max = ChartScale.NiceSizeMaxMB(peakMB);
            Assert.Equal(expectedMax, max, 3);
            Assert.Equal(label, ChartScale.FormatSizeMB(max));
        }

        [Theory]
        [InlineData(60, "60 s")]
        [InlineData(300, "5 min")]
        [InlineData(900, "15 min")]
        [InlineData(3600, "1 h")]
        public void FormatWindow(int seconds, string text) => Assert.Equal(text, ChartScale.FormatWindow(seconds));

        [Fact]
        public void Buckets_OfOneSecond_ReturnTheSeriesUnchanged()
        {
            var r = SeriesRuns.Buckets(new[] { 1.0, 2.0, 3.0 }, 100, 1);
            Assert.Equal(3, r.Count);
            Assert.Equal(3.0, r[2], 6);
        }

        [Fact]
        public void Buckets_KeepThePeakOfEachInterval()
        {
            // seconds 10..15, 3 s intervals: [9-11] [12-14] [15-17]
            var r = SeriesRuns.Buckets(new[] { 1.0, 9.0, 2.0, 3.0, 4.0, 7.0 }, 15, 3);
            Assert.Equal(3, r.Count);
            Assert.Equal(9.0, r[0], 6);   // 10, 11
            Assert.Equal(4.0, r[1], 6);   // 12, 13, 14
            Assert.Equal(7.0, r[2], 6);   // 15 (interval still open)
        }

        [Fact]
        public void Buckets_AreStable_WhenOneMoreSecondArrives()
        {
            var a = SeriesRuns.Buckets(new[] { 1.0, 9.0, 2.0, 3.0, 4.0, 7.0 }, 15, 3);
            var b = SeriesRuns.Buckets(new[] { 1.0, 9.0, 2.0, 3.0, 4.0, 7.0, 5.0 }, 16, 3);
            Assert.Equal(a.Count, b.Count);            // second 16 falls into the same interval as 15
            Assert.Equal(a[0], b[0], 6);
            Assert.Equal(a[1], b[1], 6);
            Assert.Equal(7.0, b[2], 6);
        }

        [Fact]
        public void Buckets_WithOnlyGaps_StayEmpty()
        {
            var r = SeriesRuns.Buckets(new[] { double.NaN, double.NaN, double.NaN, 5.0 }, 5, 3);
            Assert.Equal(2, r.Count);
            Assert.True(double.IsNaN(r[0]));     // second 2 (NaN) — interval [0-2]
            Assert.Equal(5.0, r[1], 6);
        }

        [Theory]
        [InlineData(60, 1)]
        [InlineData(300, 1)]
        [InlineData(600, 2)]
        [InlineData(1800, 5)]
        [InlineData(3600, 10)]
        public void BucketSize_KeepsChartsUnder360Points(int window, int bucket)
            => Assert.Equal(bucket, SeriesRuns.BucketSecondsFor(window));
    }
}
