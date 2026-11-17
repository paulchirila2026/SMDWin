using System.Collections.Generic;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // PageLoadRegressionChecker does not touch the disk at all — all the tests here use invented lists of times (ms), never
    // the real history file (that is the responsibility of PageLoadHistoryService, not tested here because it would need
    // file system isolation).
    public class PageLoadRegressionCheckerTests
    {
        [Fact]
        public void Median_OddCount_ReturnsMiddleValue()
        {
            var values = new List<double> { 300, 100, 200 };
            Assert.Equal(200, PageLoadRegressionChecker.Median(values));
        }

        [Fact]
        public void Median_EvenCount_ReturnsAverageOfMiddleTwo()
        {
            var values = new List<double> { 100, 200, 300, 400 };
            Assert.Equal(250, PageLoadRegressionChecker.Median(values));
        }

        [Fact]
        public void NotEnoughHistory_NeverFlagsRegression_RegardlessOfSpeed()
        {
            // Only 2 previous runs, default threshold minSamples=3 — insufficient history, even if the current time is absurdly large.
            var past = new List<double> { 400, 450 };
            var result = PageLoadRegressionChecker.Check(past, currentMs: 9000);

            Assert.False(result.HasEnoughHistory);
            Assert.False(result.IsRegression);
        }

        [Fact]
        public void ConsistentlySlowPage_WithMatchingHistory_IsNotFlagged()
        {
            // Startup Manager is normally slow (history ~7000ms) — a new run of ~7200ms is NOT a regression, it is its usual behaviour.
            var past = new List<double> { 6800, 7000, 7100, 6900, 7050 };
            var result = PageLoadRegressionChecker.Check(past, currentMs: 7200);

            Assert.True(result.HasEnoughHistory);
            Assert.False(result.IsRegression);
        }

        [Fact]
        public void SuddenSlowdown_OnNormallyFastPage_IsFlagged()
        {
            // The Disk page is normally fast (~450ms) — a 1200ms run (>1.6x baseline AND above the absolute minimum threshold) is a
            // real regression.
            var past = new List<double> { 420, 460, 440, 450, 435 };
            var result = PageLoadRegressionChecker.Check(past, currentMs: 1200);

            Assert.True(result.HasEnoughHistory);
            Assert.True(result.IsRegression);
            Assert.Equal(440, result.BaselineMedianMs);
        }

        [Fact]
        public void SmallJitter_OnFastPage_IsNotFlagged_EvenIfRatioIsHigh()
        {
            // 5ms -> 9ms is 1.8x baseline (above the 1.6 multiplier), but the absolute minimum threshold (800ms by default) prevents a
            // false positive on an already very fast page, where the variation is just normal jitter.
            var past = new List<double> { 5, 4, 6, 5, 5 };
            var result = PageLoadRegressionChecker.Check(past, currentMs: 9);

            Assert.True(result.HasEnoughHistory);
            Assert.False(result.IsRegression);
        }

        [Fact]
        public void ExactlyAtMultiplierBoundary_IsNotFlagged()
        {
            // currentMs == baseline * multiplier (not strictly greater) -> not a regression.
            var past = new List<double> { 1000, 1000, 1000 };
            var result = PageLoadRegressionChecker.Check(past, currentMs: 1600, regressionMultiplier: 1.6);

            Assert.False(result.IsRegression);
        }
    }
}
