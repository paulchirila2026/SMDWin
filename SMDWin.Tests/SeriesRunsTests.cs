using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The gaps in the charts (seconds without a read = NaN) and filling the interval between two reads.
    public class SeriesRunsTests
    {
        private const double N = double.NaN;

        [Fact]
        public void NoGaps_IsOneRun()
        {
            var runs = SeriesRuns.Finite(new[] { 1.0, 2.0, 3.0 });
            Assert.Single(runs);
            Assert.Equal((0, 2), runs[0]);
        }

        [Fact]
        public void GapInTheMiddle_SplitsTheLine()
        {
            var runs = SeriesRuns.Finite(new[] { 1.0, 2.0, N, N, 5.0, 6.0 });
            Assert.Equal(2, runs.Count);
            Assert.Equal((0, 1), runs[0]);
            Assert.Equal((4, 5), runs[1]);
        }

        [Fact]
        public void GapsAtBothEnds_AndSinglePoint()
        {
            var runs = SeriesRuns.Finite(new[] { N, 2.0, N, 4.0, 5.0, N });
            Assert.Equal(2, runs.Count);
            Assert.Equal((1, 1), runs[0]);   // punct izolat
            Assert.Equal((3, 4), runs[1]);
        }

        [Fact]
        public void OnlyGaps_OrEmpty_HasNoRuns()
        {
            Assert.Empty(SeriesRuns.Finite(new[] { N, N }));
            Assert.Empty(SeriesRuns.Finite(new double[0]));
        }

        [Fact]
        public void Infinity_CountsAsGap()
            => Assert.Empty(SeriesRuns.Finite(new[] { double.PositiveInfinity }));

        [Fact]
        public void SingleStep_WritesTheNewValue()
            => Assert.Equal(50f, SeriesRuns.Fill(40f, 50f, 0, 1, 15));

        [Fact]
        public void ThreeSteps_DrawAStraightLineToTheNewValue()
        {
            Assert.Equal(43f, SeriesRuns.Fill(40f, 49f, 0, 3, 15));
            Assert.Equal(46f, SeriesRuns.Fill(40f, 49f, 1, 3, 15));
            Assert.Equal(49f, SeriesRuns.Fill(40f, 49f, 2, 3, 15));
        }

        [Fact]
        public void LongAbsence_StaysEmpty_ExceptTheLastStep()
        {
            Assert.True(float.IsNaN(SeriesRuns.Fill(40f, 49f, 0, 60, 15)));
            Assert.True(float.IsNaN(SeriesRuns.Fill(40f, 49f, 58, 60, 15)));
            Assert.Equal(49f, SeriesRuns.Fill(40f, 49f, 59, 60, 15));
        }

        [Fact]
        public void Above_ReturnsTheStretchesWhereTheFlagIsOn()
        {
            // 0/1 = marcajele de sub grafice (test in curs, throttling); golurile (NaN) intrerup intervalul
            var runs = SeriesRuns.Above(new double[] { 0, 1, 1, 0, double.NaN, 1, double.NaN, 1, 1 }, 0.5);
            Assert.Equal(3, runs.Count);
            Assert.Equal((1, 2), runs[0]);
            Assert.Equal((5, 5), runs[1]);
            Assert.Equal((7, 8), runs[2]);   // an interval still open at the end is closed on the last value
        }

        [Fact]
        public void Above_NothingOn_GivesNoRuns()
        {
            Assert.Equal(0, SeriesRuns.Above(new double[] { 0, 0, double.NaN }, 0.5).Count);
            Assert.Equal(0, SeriesRuns.Above(new double[0], 0.5).Count);
        }

        [Fact]
        public void UnknownPreviousValue_IsNotInvented()
        {
            // without a previous value there is nothing to interpolate from: the steps get the new value directly
            Assert.Equal(49f, SeriesRuns.Fill(float.NaN, 49f, 0, 3, 15));
            Assert.True(float.IsNaN(SeriesRuns.Fill(40f, float.NaN, 1, 3, 15)));
        }
    }
}
