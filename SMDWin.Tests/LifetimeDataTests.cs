using System.Globalization;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The data written / read over the disk's life (SMART 0xF1 / 0xF2): the unit of the raw value differs from disk to disk.
    public class LifetimeDataTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [Fact]
        public void NvmeBehindRaid_UsesNvmeDataUnits()
        {
            // Toshiba KXG50ZNV512G through Intel RST: 30 589 001 units written, 46 028 690 read
            Assert.Equal("15.7 TB", LifetimeData.Format(LifetimeData.Bytes(30_589_001, "", 512, nvmeDataUnits: true), Inv));
            Assert.Equal("23.6 TB", LifetimeData.Format(LifetimeData.Bytes(46_028_690, "", 512, nvmeDataUnits: true), Inv));
        }

        [Fact]
        public void SameRaw_AsPlainSectors_IsOnlyGigabytes()
            => Assert.Equal("15.7 GB", LifetimeData.Format(LifetimeData.Bytes(30_589_001, "", 512, nvmeDataUnits: false), Inv));

        [Fact]
        public void SataDrive_CountsSectors()
        {
            // 100 billion 512-byte sectors = 51.2 TB
            Assert.Equal("51.2 TB", LifetimeData.Format(LifetimeData.Bytes(100_000_000_000, "Total_LBAs_Written", 512, false), Inv));
            Assert.Equal(4096.0 * 1000, LifetimeData.Bytes(1000, "Total_LBAs_Written", 4096, false));
        }

        [Theory]
        [InlineData("Host_Writes_32MiB", 1000L, 32.0 * 1048576 * 1000)]
        [InlineData("Lifetime_Writes_GiB", 500L, 500.0 * 1073741824)]
        [InlineData("Host_Writes_MiB", 2000L, 2000.0 * 1048576)]
        [InlineData("Total_Writes_GB", 750L, 750e9)]
        public void UnitInTheSmartctlName_Wins(string name, long raw, double expectedBytes)
        {
            Assert.Equal(expectedBytes, LifetimeData.Bytes(raw, name, 512, false));
            // the unit in the name takes priority on an NVMe too
            Assert.Equal(expectedBytes, LifetimeData.Bytes(raw, name, 512, true));
        }

        [Fact]
        public void SmallRaw_WithoutUnit_IsReadAsGiB()
        {
            // Kingston A400: 1 234 "sectors" would be 0.6 MB — in reality they are GiB
            Assert.Equal(1234.0 * 1073741824, LifetimeData.Bytes(1234, "", 512, false));
            // a large value below 1 GB stays in sectors (an almost new disk), it does not become an absurd number
            Assert.Equal(1_500_000 * 512.0, LifetimeData.Bytes(1_500_000, "", 512, false));
        }

        [Fact]
        public void UnknownValue_ShowsADash()
        {
            Assert.True(LifetimeData.Bytes(0, "", 512, true) <= 0);
            Assert.Equal("—", LifetimeData.Format(LifetimeData.Bytes(0, "", 512, false), Inv));
            Assert.Equal("—", LifetimeData.Format(double.NaN, Inv));
        }

        [Theory]
        [InlineData(1.5e12, "1.5 TB")]
        [InlineData(850e9, "850 GB")]
        [InlineData(15.66e9, "15.7 GB")]
        [InlineData(2e9, "2 GB")]
        [InlineData(12e6, "12 MB")]
        public void Format_PicksTheUnit(double bytes, string expected)
            => Assert.Equal(expected, LifetimeData.Format(bytes, Inv));
    }
}
