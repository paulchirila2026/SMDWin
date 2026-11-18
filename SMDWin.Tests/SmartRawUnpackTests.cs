using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Raw SMART values that some makers pack with several counters (read through Windows, without smartctl).
    public class SmartRawUnpackTests
    {
        [Theory]
        [InlineData(0xBC, 0x400040004L, 4L)]        // Seagate Command Timeout "4 4 4"
        [InlineData(0xBC, 3L, 3L)]                   // a plain count stays as it is
        [InlineData(0x09, 0x00E9AC00FC12L, 0xAC00FC12L & 0xFFFFFFFFL)]   // hours in the low 4 bytes
        [InlineData(0x09, 12345L, 12345L)]
        [InlineData(0x05, 0x400040004L, 0x400040004L)]   // other attributes are not touched
        public void UnpacksOnlyThePackedAttributes(byte id, long raw, long expected)
            => Assert.Equal(expected, HardwareService.UnpackRaw(id, raw));
    }
}
