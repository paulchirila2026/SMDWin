using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The speed a module is sold for, read from its part number (XMP / EXPO kits report only JEDEC to Windows).</summary>
    public class RamChecksTests
    {
        [Theory]
        [InlineData("CMK16GX4M2B3200C16", 3200)]     // Corsair Vengeance LPX
        [InlineData("F4-3600C18D-32GTZN", 3600)]     // G.Skill DDR4
        [InlineData("F5-6000J3038F16GX2", 6000)]     // G.Skill DDR5
        [InlineData("KF432C16BBK2/16", 3200)]        // Kingston FURY
        [InlineData("KF556C40BBK2-32", 5600)]        // Kingston FURY DDR5
        [InlineData("HX426C16FB3/8", 2666)]          // Kingston HyperX
        [InlineData("KVR32N22S8/8", 3200)]           // Kingston ValueRAM
        [InlineData("BL2K8G32C16U4B", 3200)]         // Crucial Ballistix
        [InlineData("CT8G4DFS832A.C8FE", 3200)]      // Crucial DDR4
        [InlineData("CT16G56C46U5.M8D1", 5600)]      // Crucial DDR5
        [InlineData("MTA8ATF1G64AZ-3G2E1", 3200)]    // Micron
        [InlineData("M378A1K43CB2-CTD", 2666)]       // Samsung
        [InlineData("HMA81GS6CJR8N-XN", 3200)]       // SK hynix
        [InlineData("TF3D416G3200HC16F01", 3200)]    // TeamGroup
        [InlineData("PVS416G320C6K", 3200)]          // Patriot Viper
        public void ReadsTheSpeed(string part, int mts) => Assert.Equal(mts, RamChecks.RatedFromPart(part));

        [Theory]
        [InlineData("")]
        [InlineData("Module")]
        [InlineData("0000000000000000")]
        public void UnknownGivesNull(string part) => Assert.Null(RamChecks.RatedFromPart(part));

        [Fact]
        public void ClockIsDoubledWhenWindowsReportsHalf()
        {
            Assert.Equal(3200, RamChecks.RunningMts(1600, 3200));
            Assert.Equal(3200, RamChecks.RunningMts(3200, 3200));
            Assert.Equal(2133, RamChecks.RunningMts(2133, 3200));
        }
    }
}
