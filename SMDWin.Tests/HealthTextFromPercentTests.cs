using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // HardwareService.HealthTextFromPercent is `internal` (not `public`) — visible here only thanks to InternalsVisibleTo.cs
    // in the main project root. The tested thresholds (80/50) must stay IDENTICAL to DiskHealthEntry.HealthColor
    // (Models/DiagModels.cs) — the two must always agree (the same percentage = the same text and the same colour); see
    // DiskHealthEntryHealthColorTests for the colour part.
    public class HealthTextFromPercentTests
    {
        [Theory]
        [InlineData(100, "Healthy")]
        [InlineData(80, "Healthy")]   // the exact threshold — >= 80 is Healthy
        [InlineData(79, "Warning")]
        [InlineData(50, "Warning")]   // the exact threshold — >= 50 is Warning
        [InlineData(49, "Critical")]
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
            string actual = HardwareService.HealthTextFromPercent(95, currentStatus: "\u26a0 SMART Warning");
            Assert.Equal("\u26a0 SMART Warning", actual);
        }
    }
}
