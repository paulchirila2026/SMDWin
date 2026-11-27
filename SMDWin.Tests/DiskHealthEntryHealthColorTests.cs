using SMDWin.Models;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // DiskHealthEntry.HealthColor follows the one verdict of the disk (DriveVerdict): green = healthy, amber = needs attention,
    // red = failing, grey = no SMART data. For a disk judged by its SMART score alone the limits are 90 / 60, the same as
    // HardwareService.HealthTextFromPercent — the text and the colour must always agree.
    public class DiskHealthEntryHealthColorTests
    {
        private static DiskHealthEntry Disk(int healthPercent)
        {
            var d = new DiskHealthEntry { Kind = "HDD", RotationRpm = 7200, HealthPercent = healthPercent };
            d.SmartAttributes.Add(new SmartAttributeEntry { Id = 0x09, Name = "Power-On Hours", CurrentValue = 99, RawValue = 1200 });
            return d;
        }

        [Theory]
        [InlineData(100, "#22C55E")]
        [InlineData(90, "#22C55E")]  // exact threshold — green
        [InlineData(89, "#F59E0B")]
        [InlineData(60, "#F59E0B")]  // exact threshold — amber
        [InlineData(59, "#EF4444")]
        [InlineData(1, "#EF4444")]
        public void HealthColor_MatchesExpectedHex(int healthPercent, string expectedHex)
        {
            Assert.Equal(expectedHex, Disk(healthPercent).HealthColor);
        }

        [Fact]
        public void NoSmartData_IsGrey()
        {
            Assert.Equal("#94A3B8", new DiskHealthEntry { HealthPercent = 100 }.HealthColor);
        }

        [Theory]
        [InlineData(100)]
        [InlineData(90)]
        [InlineData(89)]
        [InlineData(60)]
        [InlineData(59)]
        [InlineData(1)]
        public void HealthColorThreshold_AgreesWithHealthTextThreshold(int healthPercent)
        {
            var disk = Disk(healthPercent);
            string text = HardwareService.HealthTextFromPercent(healthPercent, currentStatus: "");
            Assert.Equal(text == "Healthy", disk.HealthColor == "#22C55E");
            Assert.Equal(text == "Warning", disk.HealthColor == "#F59E0B");
        }
    }
}
