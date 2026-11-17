using SMDWin.Models;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // DiskHealthEntry.HealthColor (public, Models/DiagModels.cs) uses the same thresholds (80/50) as
    // HardwareService.HealthTextFromPercent. The text and the colour must always agree — if someone changes a threshold in one
    // place and forgets the other, this test + HealthTextFromPercentTests both fail on exactly the same case, which makes the
    // bug obvious immediately.
    public class DiskHealthEntryHealthColorTests
    {
        [Theory]
        [InlineData(100, "#22C55E")]
        [InlineData(80, "#22C55E")]  // exact threshold — green
        [InlineData(79, "#F59E0B")]
        [InlineData(50, "#F59E0B")]  // exact threshold — orange
        [InlineData(49, "#EF4444")]
        [InlineData(0, "#EF4444")]
        public void HealthColor_MatchesExpectedHex(int healthPercent, string expectedHex)
        {
            var disk = new DiskHealthEntry { HealthPercent = healthPercent };
            Assert.Equal(expectedHex, disk.HealthColor);
        }

        [Theory]
        [InlineData(100)]
        [InlineData(80)]
        [InlineData(79)]
        [InlineData(50)]
        [InlineData(49)]
        [InlineData(0)]
        public void HealthColorThreshold_AgreesWithHealthTextThreshold(int healthPercent)
        {
            var disk = new DiskHealthEntry { HealthPercent = healthPercent };
            string text = HardwareService.HealthTextFromPercent(healthPercent, currentStatus: "");

            bool colorSaysGood = disk.HealthColor == "#22C55E";
            bool colorSaysWarn = disk.HealthColor == "#F59E0B";
            bool textSaysGood = text == "Healthy";
            bool textSaysWarn = text == "Warning";

            Assert.Equal(textSaysGood, colorSaysGood);
            Assert.Equal(textSaysWarn, colorSaysWarn);
        }
    }
}
