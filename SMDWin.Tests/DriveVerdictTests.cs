using SMDWin.Models;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The one verdict of a disk: the same figure and state on every screen.
    public class DriveVerdictTests
    {
        private static NvmeHealthRow Row(string name, string raw, string color = "#22C55E") =>
            new() { Name = name, RawDisplay = raw, TrafficLightColor = color };

        [Fact]
        public void Nvme_LifeLeftIsTheFigure_HeatHistoryDoesNotMakeItWatch()
        {
            // the Lexar NM620 case: SMART score 100, 7 % used, some minutes once spent above the warning temperature
            var d = new DiskHealthEntry { Kind = "NVMe SSD", HealthPercent = 100 };
            d.NvmeHealthRows.Add(Row("Critical Warning", "0x00"));
            d.NvmeHealthRows.Add(Row("Percentage Used", "7%"));
            d.NvmeHealthRows.Add(Row("Warning Composite Temperature Time", "12 min", "#F59E0B"));
            var v = DriveVerdict.Of(d);
            Assert.True(v.HasData);
            Assert.Equal(93, v.Percent);
            Assert.True(v.IsLifeLeft);
            Assert.Equal(DriveVerdict.State.Good, v.Level);
            Assert.Contains("Warning Composite Temperature Time", v.Notes);
        }

        [Fact]
        public void NoSmartData_IsUnknown_NotZeroPercent()
        {
            var v = DriveVerdict.Of(new DiskHealthEntry { Kind = "USB stick", HealthPercent = 0 });
            Assert.False(v.HasData);
            Assert.Equal(DriveVerdict.State.Unknown, v.Level);
            Assert.Equal("—", v.PercentText);
        }

        [Fact]
        public void PendingSectors_AreFailing_EvenWithAHighScore()
        {
            var d = new DiskHealthEntry { Kind = "HDD", RotationRpm = 7200, HealthPercent = 96 };
            d.SmartAttributes.Add(new SmartAttributeEntry { Id = 0xC5, Name = "Current Pending Sector Count", CurrentValue = 100, RawValue = 8 });
            var v = DriveVerdict.Of(d);
            Assert.Equal(DriveVerdict.State.Failing, v.Level);
            Assert.False(v.IsLifeLeft);
            Assert.Equal(96, v.Percent);
        }

        [Fact]
        public void WornSsd_NeedsAttention_OnlyWhenLittleLifeIsLeft()
        {
            var d = new DiskHealthEntry { Kind = "NVMe SSD", HealthPercent = 100 };
            d.NvmeHealthRows.Add(Row("Percentage Used", "25%"));
            Assert.Equal(DriveVerdict.State.Good, DriveVerdict.Of(d).Level);
            d.NvmeHealthRows[0] = Row("Percentage Used", "75%");
            Assert.Equal(DriveVerdict.State.Watch, DriveVerdict.Of(d).Level);
        }
    }
}
