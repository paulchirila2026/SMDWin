using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The short model name used to find a part in the hardware database when the full name does not match exactly.
    public class HardwareModelTokenTests
    {
        [Theory]
        [InlineData("Intel(R) Core(TM) i5-4570 CPU @ 3.20GHz", "I5-4570")]
        [InlineData("Intel(R) Core(TM) i5-1035G1 CPU @ 1.00GHz", "I5-1035G1")]   // laptop suffix ending in a digit
        [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "I7-1165G7")]
        [InlineData("Intel(R) Core(TM) i5-10210U CPU @ 1.60GHz", "I5-10210U")]
        [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", "I5-12400F")]
        [InlineData("NVIDIA GeForce MX250", "MX250")]
        [InlineData("NVIDIA GeForce GT 755M", "GT 755M")]
        public void ExtractsTheModel(string name, string expected)
            => Assert.Equal(expected, HardwareDatabaseService.ExtractModelToken(name));
    }
}
