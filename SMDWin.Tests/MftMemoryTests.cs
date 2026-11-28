using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The NTFS index is used when its estimated size fits in the free memory (Space usage).
    public class MftMemoryTests
    {
        [Fact]
        public void SystemDiskFitsEvenWithLittleFreeMemory()
        {
            // a 100 GB system disk: about 400 thousand records, 0.7 GB free (a PC with 8 GB and a lot of programs open)
            Assert.True(MftScanner.IndexFitsInMemory(400_000, 700, out long mb));
            Assert.InRange(mb, 40, 120);
        }

        [Fact]
        public void HugeIndexDoesNotFitInLittleMemory()
        {
            // 8 million records need several hundred MB: with 600 MB free the folder scan is used
            Assert.False(MftScanner.IndexFitsInMemory(8_000_000, 600, out long mb));
            Assert.True(mb > 500);
        }

        [Fact]
        public void HugeIndexFitsWhenThereIsRoom()
        {
            Assert.True(MftScanner.IndexFitsInMemory(8_000_000, 8_000, out _));
        }

        [Fact]
        public void UnknownFreeMemoryDoesNotLimit()
        {
            Assert.True(MftScanner.IndexFitsInMemory(50_000_000, 0, out _));
        }

        [Fact]
        public void NearlyNoMemoryNeverUsesTheIndex()
        {
            Assert.False(MftScanner.IndexFitsInMemory(100_000, 200, out _));
        }
    }
}
