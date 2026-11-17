using SMDWin.Models;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The disk type line shown on the Storage page, System Info and the PDF sheet (11.11.2026), and the smartctl form-factor
    // names it is built from.
    public class DiskTypeTextTests
    {
        [Theory]
        [InlineData("2.5 inches", "2.5\"")]
        [InlineData("3.5 inches", "3.5\"")]
        [InlineData("1.8 inches", "1.8\"")]
        [InlineData("M.2", "M.2")]
        [InlineData("mSATA", "mSATA")]
        [InlineData("less than 1.8 inches", "")]
        [InlineData("", "")]
        public void NormalizeFormFactor(string smartctl, string expected)
            => Assert.Equal(expected, SmartctlService.NormalizeFormFactor(smartctl));

        [Theory]
        [InlineData("SATA SSD", "SATA", "M.2", 0, "M.2 SATA SSD")]
        [InlineData("SATA SSD", "SATA", "2.5\"", 0, "2.5\" SATA SSD")]
        [InlineData("SATA SSD", "SATA", "", 0, "SATA SSD")]
        [InlineData("NVMe SSD", "NVMe", "", 0, "M.2 NVMe SSD")]
        [InlineData("HDD", "SATA", "3.5\"", 7200, "3.5\" HDD · 7200 rpm")]
        [InlineData("HDD", "SATA", "", 5400, "HDD · 5400 rpm")]
        [InlineData("External HDD", "USB", "2.5\"", 5400, "External HDD · 2.5\" · 5400 rpm")]
        [InlineData("USB stick", "USB", "", 0, "USB stick")]
        public void TypeText(string kind, string bus, string form, int rpm, string expected)
        {
            var d = new DiskHealthEntry { Kind = kind, Bus = bus, FormFactor = form, RotationRpm = rpm };
            Assert.Equal(expected, d.TypeText);
        }

        [Fact]
        public void UsbLinkText_SlowLink()
        {
            var d = new DiskHealthEntry { UsbSupported = "USB 3.0 · 5 Gb/s", UsbConnected = "USB 2.0 · 480 Mb/s", UsbSlowLink = true };
            Assert.Equal("USB 3.0 device · connected as USB 2.0", d.UsbLinkText);
        }
    }
}
