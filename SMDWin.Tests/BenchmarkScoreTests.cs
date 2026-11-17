using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class BenchmarkScoreTests
    {
        [Fact]
        public void Reference_IsSix() => Assert.Equal(6.0, BenchmarkScore.Mark(BenchmarkScore.GpuCompute.Reference, BenchmarkScore.GpuCompute));

        [Fact]
        public void OneStepFaster_IsOneMore() => Assert.Equal(7.0, BenchmarkScore.Mark(4000 * 2.2, BenchmarkScore.GpuCompute));

        [Fact]
        public void LowerLatency_IsBetter()
        {
            Assert.True(BenchmarkScore.Mark(60, BenchmarkScore.RamLatency) > BenchmarkScore.Mark(120, BenchmarkScore.RamLatency));
        }

        [Fact]
        public void Marks_AreClampedAndInvalidIsNull()
        {
            Assert.Equal(10.0, BenchmarkScore.Mark(1e9, BenchmarkScore.DiskSeq));
            Assert.Equal(1.0, BenchmarkScore.Mark(0.001, BenchmarkScore.DiskSeq));
            Assert.Null(BenchmarkScore.Mark(0, BenchmarkScore.DiskSeq));
            Assert.Null(BenchmarkScore.Mark(double.NaN, BenchmarkScore.DiskSeq));
        }

        [Fact]
        public void Mean_SkipsMissing() => Assert.Equal(5.4, BenchmarkScore.Mean(new double?[] { 6.8, 6.1, null, 3.3 }));

        [Theory]
        [InlineData(1.5, "Insufficient")]
        [InlineData(5.4, "Standard")]
        [InlineData(5.5, "Advanced")]
        [InlineData(10, "Workstation")]
        public void Tiers(double mark, string name) => Assert.Equal(name, BenchmarkScore.TierOf(mark).Name);
    }
}
