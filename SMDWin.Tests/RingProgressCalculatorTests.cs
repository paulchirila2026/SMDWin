using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The same cases as in SelfTestService ("CALCULATION SELF-CHECKS"), but run here with `dotnet test`, without starting the
    // app or pressing the self-test button. The two places check the same thing for different reasons: SelfTestService
    // catches the regression "in the field" (self-test run on a client computer shows ✘ immediately); the tests here catch the
    // regression BEFORE commit/build, as early as possible.
    public class RingProgressCalculatorTests
    {
        private const double Diameter = 84;
        private const double Thickness = 9;

        [Fact]
        public void At100Percent_OffsetIsZero_FullCircleVisible()
        {
            var result = RingProgressCalculator.Calculate(Diameter, Thickness, 100);
            Assert.Equal(0.0, result.OffsetUnits, precision: 3);
        }

        [Fact]
        public void At0Percent_OffsetEqualsFullDash_NothingVisible()
        {
            var result = RingProgressCalculator.Calculate(Diameter, Thickness, 0);
            Assert.Equal(result.DashUnits, result.OffsetUnits, precision: 3);
        }

        [Theory]
        [InlineData(77)]  // a real reported case — health shown as 77% but the ring appeared full
        [InlineData(50)]
        [InlineData(25)]
        [InlineData(90)]
        public void VisibleArcLength_MatchesPercentOfRealCircumference(double percent)
        {
            var result = RingProgressCalculator.Calculate(Diameter, Thickness, percent);
            double visiblePx = RingProgressCalculator.VisibleArcLengthPx(result, Thickness);
            double expectedPx = result.CircumferencePx * (percent / 100.0);

            Assert.Equal(expectedPx, visiblePx, precision: 1);
        }

        [Fact]
        public void PercentAbove100_IsClampedTo100()
        {
            var over = RingProgressCalculator.Calculate(Diameter, Thickness, 150);
            var full = RingProgressCalculator.Calculate(Diameter, Thickness, 100);
            Assert.Equal(full.OffsetUnits, over.OffsetUnits, precision: 3);
        }

        [Fact]
        public void PercentBelow0_IsClampedTo0()
        {
            var under = RingProgressCalculator.Calculate(Diameter, Thickness, -20);
            var empty = RingProgressCalculator.Calculate(Diameter, Thickness, 0);
            Assert.Equal(empty.OffsetUnits, under.OffsetUnits, precision: 3);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void InvalidDiameter_Throws(double diameter)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => RingProgressCalculator.Calculate(diameter, Thickness, 50));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void InvalidThickness_Throws(double thickness)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => RingProgressCalculator.Calculate(Diameter, thickness, 50));
        }
    }
}
