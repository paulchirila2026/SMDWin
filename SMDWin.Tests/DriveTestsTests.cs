using System.Collections.Generic;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class DriveTestsTests
    {
        [Fact]
        public void PatternDependsOnPosition()
        {
            var a = new byte[4096]; var b = new byte[4096];
            DrivePattern.Fill(a, 0, 7);
            DrivePattern.Fill(b, 1L << 30, 7);   // the same block written 1 GB further: a wrapping fake stick returns this
            Assert.Equal(-1, DrivePattern.FirstMismatch(a, 0, 7));
            Assert.Equal(0, DrivePattern.FirstMismatch(b, 0, 7));
        }

        [Fact]
        public void FindsTheFirstDamagedWord()
        {
            var a = new byte[4096];
            DrivePattern.Fill(a, 8192, 99);
            a[1000] ^= 0x10;
            Assert.Equal(1000 / 8 * 8, DrivePattern.FirstMismatch(a, 8192, 99));
        }

        [Fact]
        public void SustainedFindsTheEndOfTheCache()
        {
            var s = new List<(double, double)>();
            for (int i = 0; i < 40; i++) s.Add((i * 1.0, 2000));     // 40 GB fast
            for (int i = 40; i < 80; i++) s.Add((i * 1.0, 400));     // then slow
            var r = DriveTests.AnalyzeSustained(s);
            Assert.True(r.Found);
            Assert.InRange(r.CacheGB, 39, 41);
            Assert.InRange(r.Slow, 390, 410);
        }

        [Fact]
        public void SustainedSteadyHasNoDrop()
        {
            var s = new List<(double, double)>();
            for (int i = 0; i < 60; i++) s.Add((i * 0.5, 500 + (i % 3) * 20));
            Assert.False(DriveTests.AnalyzeSustained(s).Found);
        }
    }
}
