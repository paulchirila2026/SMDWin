using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // HardwareService.HealthTextFromPercent is `internal` (not `public`) — visible here only thanks to InternalsVisibleTo.cs
    // in the main project root. The thresholds (90 / 60) are the same as the score limits of DriveVerdict, so the status text
    // and the verdict shown on every screen agree (see DiskHealthEntryHealthColorTests for the colour part).
    public class HealthTextFromPercentTests
    {
        [Theory]
        [InlineData(100, "Healthy")]
        [InlineData(90, "Healthy")]   // the exact threshold — >= 90 is Healthy
        [InlineData(89, "Warning")]
        [InlineData(60, "Warning")]   // the exact threshold — >= 60 is Warning
        [InlineData(59, "Critical")]
        [InlineData(0, "Critical")]
        public void MapsPercentToExpectedText(int healthPercent, string expected)
        {
            string actual = HardwareService.HealthTextFromPercent(healthPercent, currentStatus: "");
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ExistingSmartWarningStatus_TakesPriorityOverPercent()
        {
            // A disk with 95% health computed heuristically, but already marked ⚠ SMART Warning by SmartFetchFailurePrediction (an
            // explicit signal from the disk, not just a score from the attributes) — the explicit status must stay, not be
            // overwritten by a percentage that looks good.
            string actual = HardwareService.HealthTextFromPercent(95, currentStatus: "⚠ SMART Warning");
            Assert.Equal("⚠ SMART Warning", actual);
        }
    }
}
