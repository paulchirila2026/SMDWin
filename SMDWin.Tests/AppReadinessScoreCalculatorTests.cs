using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class AppReadinessScoreCalculatorTests
    {
        [Fact]
        public void PerfectRun_FullHwCoverage_ScoresMax()
        {
            var result = AppReadinessScoreCalculator.Calculate(warnCount: 0, failCount: 0, hwDbKnownCount: 5, hwDbTotalCount: 5);

            Assert.Equal(100, result.Score);
            Assert.Equal(100, result.StabilityPercent);
            Assert.Equal(100, result.HwCoveragePercent);
            Assert.Equal("Excellent", result.Rating);
        }

        [Fact]
        public void NoHardwareChecked_ScoreIsStabilityOnly()
        {
            var result = AppReadinessScoreCalculator.Calculate(warnCount: 0, failCount: 0, hwDbKnownCount: 0, hwDbTotalCount: 0);

            Assert.Null(result.HwCoveragePercent);
            Assert.Equal(100, result.Score);
        }

        [Fact]
        public void FailuresCostMoreThanWarnings()
        {
            var withWarnings = AppReadinessScoreCalculator.Calculate(warnCount: 3, failCount: 0, hwDbKnownCount: 5, hwDbTotalCount: 5);
            var withFailures = AppReadinessScoreCalculator.Calculate(warnCount: 0, failCount: 3, hwDbKnownCount: 5, hwDbTotalCount: 5);

            Assert.True(withFailures.Score < withWarnings.Score);
        }

        [Fact]
        public void MatchesRealSessionCase_SixWarningsOneError_FourOfFiveHwKnown()
        {
            // A real report case: 6 warnings, 1 error, 4 of 5 hardware checks (CPU/GPU/2xdisk/RAM missing from the DB — assume 1 of
            // 5 found).
            var result = AppReadinessScoreCalculator.Calculate(warnCount: 6, failCount: 1, hwDbKnownCount: 1, hwDbTotalCount: 5);

            // stability = 100 - 15 - 30 = 55; hwCoverage = 20%
            Assert.Equal(55, result.StabilityPercent);
            Assert.Equal(20, result.HwCoveragePercent);
            // the score is stability only (16.11.2026); the coverage is reported next to it
            Assert.Equal(55, result.Score);
            Assert.Equal("Acceptable", result.Rating);
        }

        [Fact]
        public void LowHwCoverage_DoesNotLowerTheScore()
        {
            var result = AppReadinessScoreCalculator.Calculate(warnCount: 0, failCount: 0, hwDbKnownCount: 1, hwDbTotalCount: 4);
            Assert.Equal(100, result.Score);
            Assert.Equal(25, result.HwCoveragePercent);
        }

        [Fact]
        public void ManyWarnings_StabilityClampedAtZero_NeverNegative()
        {
            var result = AppReadinessScoreCalculator.Calculate(warnCount: 100, failCount: 0, hwDbKnownCount: 5, hwDbTotalCount: 5);
            Assert.Equal(0, result.StabilityPercent);
            Assert.True(result.Score >= 0);
        }

        [Theory]
        [InlineData(-1, 0, 0, 0)]
        [InlineData(0, -1, 0, 0)]
        [InlineData(0, 0, -1, 0)]
        [InlineData(0, 0, 0, -1)]
        [InlineData(0, 0, 6, 5)] // known > total, invalid
        public void InvalidInputs_Throw(int warn, int fail, int known, int total)
        {
            Assert.ThrowsAny<System.ArgumentException>(
                () => AppReadinessScoreCalculator.Calculate(warn, fail, known, total));
        }

        [Theory]
        [InlineData(0, "Excellent")]   // stability 100
        [InlineData(2, "Excellent")]   // stability 90 — exact threshold, still Excellent
        [InlineData(3, "Good")]        // stability 85
        [InlineData(5, "Good")]        // stability 75 — exact threshold, still Good
        [InlineData(6, "Acceptable")] // stability 70
        [InlineData(10, "Acceptable")]// stability 50 — exact threshold, still Acceptable
        [InlineData(11, "Needs attention")] // stability 45
        public void RatingLabel_MatchesScoreThresholds(int warnCount, string expectedRating)
        {
            // hwDbTotal=0 so overall is exactly stability (no mixing with coverage), for predictable results on exact multiples of 5.
            var result = AppReadinessScoreCalculator.Calculate(warnCount, failCount: 0, hwDbKnownCount: 0, hwDbTotalCount: 0);
            Assert.Equal(expectedRating, result.Rating);
        }
    }
}
