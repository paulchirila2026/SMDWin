using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The drawn memory module contacts: all equal, with a constant pitch, at any width.
    public class DimmLayoutTests
    {
        [Fact]
        public void Pins_AreEquallySpaced_AtEveryWidth()
        {
            const int margin = 9, notchW = 7, pitch = 6, pinW = 4;
            for (int width = 120; width <= 520; width++)
                foreach (double pos in new[] { 0.36, 0.49, 0.56 })
                {
                    int notch = DimmLayout.NotchLeft(width, margin, notchW, pos);
                    var pins = DimmLayout.PinStarts(width, margin, notch, notchW, pitch, pinW);
                    Assert.True(pins.Count > 10, $"latime {width}: prea putine contacte");
                    var left = pins.Where(x => x < notch).ToList();
                    var right = pins.Where(x => x > notch).ToList();
                    // constant pitch in each group
                    Assert.True(left.Zip(left.Skip(1), (a, b) => b - a).All(d => d == pitch), $"latime {width}: pas inegal in stanga");
                    Assert.True(right.Zip(right.Skip(1), (a, b) => b - a).All(d => d == pitch), $"latime {width}: pas inegal in dreapta");
                    // no contact over the notch or outside the board
                    Assert.True(pins.All(x => x + pinW <= notch || x >= notch + notchW), $"latime {width}: contact peste crestatura");
                    Assert.True(pins.All(x => x >= margin && x + pinW <= width - margin), $"latime {width}: contact in afara placii");
                    // the same space on either side of the notch
                    Assert.Equal(notch - (left.Last() + pinW), right.First() - (notch + notchW));
                }
        }

        [Fact]
        public void EndsNeverLeaveRoomForAnotherPin()
        {
            const int margin = 9, notchW = 7, pitch = 6, pinW = 4;
            for (int width = 150; width <= 400; width += 7)
            {
                int notch = DimmLayout.NotchLeft(width, margin, notchW, 0.49);
                var pins = DimmLayout.PinStarts(width, margin, notch, notchW, pitch, pinW);
                Assert.True(pins.First() - margin < pitch);
                Assert.True(width - margin - (pins.Last() + pinW) < pitch);
            }
        }

        [Theory]
        [InlineData("SO-DIMM", true)]
        [InlineData("SODIMM", true)]
        [InlineData("so dimm", true)]
        [InlineData("DIMM", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void SoDimm_IsRecognizedFromTheFormFactor(string? formFactor, bool expected)
            => Assert.Equal(expected, DimmLayout.IsSoDimm(formFactor));

        [Fact]
        public void NotchPosition_DiffersBetweenGenerations()
        {
            Assert.NotEqual(DimmLayout.NotchPosition("DDR4", false), DimmLayout.NotchPosition("DDR5", false));
            Assert.NotEqual(DimmLayout.NotchPosition("DDR4", false), DimmLayout.NotchPosition("DDR4", true));
            Assert.True(DimmLayout.NotchPosition(null, false) is > 0 and < 1);
        }

        [Fact]
        public void BadInput_GivesNoPins()
        {
            Assert.Equal(0, DimmLayout.PinStarts(10, 9, 9, 7, 6, 4).Count);
            Assert.Equal(0, DimmLayout.PinStarts(300, 9, 150, 7, 0, 4).Count);
        }
    }
}
