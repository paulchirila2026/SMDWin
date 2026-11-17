using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class ConsistentGrowthDetectorTests
    {
        [Fact]
        public void MonotonicGrowth_AboveThreshold_IsDetected()
        {
            // The real case (memory): 500 -> 600 -> 700 MB, threshold 150 -> total 200 > 150
            Assert.True(ConsistentGrowthDetector.IsConsistentGrowth(500, 600, 700, minAbsoluteGrowth: 150));
        }

        [Fact]
        public void MonotonicGrowth_BelowThreshold_IsNotDetected()
        {
            // Real growth, but too small to matter (normal measurement noise)
            Assert.False(ConsistentGrowthDetector.IsConsistentGrowth(500, 520, 540, minAbsoluteGrowth: 150));
        }

        [Fact]
        public void NonMonotonic_DipInMiddle_IsNotFlagged()
        {
            // 500 -> 300 -> 700: not CONSISTENT growth, only noise/normal variation
            Assert.False(ConsistentGrowthDetector.IsConsistentGrowth(500, 300, 700, minAbsoluteGrowth: 150));
        }

        [Fact]
        public void StableValues_AreNotFlagged()
        {
            Assert.False(ConsistentGrowthDetector.IsConsistentGrowth(500, 500, 500, minAbsoluteGrowth: 150));
        }

        [Fact]
        public void Shrinking_IsNotFlagged()
        {
            Assert.False(ConsistentGrowthDetector.IsConsistentGrowth(700, 600, 500, minAbsoluteGrowth: 150));
        }

        [Fact]
        public void HandleCountScale_WorksWithLargerNumbers()
        {
            // Handle counts are in thousands, not hundreds — the threshold is different (500), but the logic is identical (the same
            // function, reused).
            Assert.True(ConsistentGrowthDetector.IsConsistentGrowth(1200, 1800, 2500, minAbsoluteGrowth: 500));
            Assert.False(ConsistentGrowthDetector.IsConsistentGrowth(1200, 1400, 1500, minAbsoluteGrowth: 500));
        }
    }
}
