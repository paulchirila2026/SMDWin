using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The square animations of the "test in progress" indicator in the sidebar: the frames are computed only from the phase.</summary>
    public class SquareBadgeAnimTests
    {
        [Fact]
        public void EveryStyle_StaysInsideTheBox_AtAnyPhase()
        {
            foreach (var (key, _) in SquareBadgeAnim.Styles)
                for (int step = 0; step <= 200; step++)
                {
                    foreach (var c in SquareBadgeAnim.Cells(key, step / 200.0))
                    {
                        Assert.True(c.X >= -0.001 && c.Y >= -0.001, $"{key} @ {step}: iese din caseta (stanga/sus)");
                        Assert.True(c.X + c.W <= SquareBadgeAnim.Box + 0.001, $"{key} @ {step}: iese din caseta (dreapta)");
                        Assert.True(c.Y + c.H <= SquareBadgeAnim.Box + 0.001, $"{key} @ {step}: iese din caseta (jos)");
                        Assert.True(c.Opacity >= -0.001 && c.Opacity <= 1.001, $"{key} @ {step}: opacitate {c.Opacity}");
                        Assert.True(c.W >= 0 && c.H >= 0, $"{key} @ {step}: marime negativa");
                    }
                }
        }

        [Fact]
        public void EveryStyle_LoopsWithoutAJump()
        {
            // the end of the cycle must look like its start, otherwise the animation "jumps" on every loop
            foreach (var (key, _) in SquareBadgeAnim.Styles)
            {
                var a = SquareBadgeAnim.Cells(key, 0.0);
                var b = SquareBadgeAnim.Cells(key, 0.9999);
                Assert.Equal(a.Count, b.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    // the visible "ink" = area × opacity (an empty or transparent square is not visible at all)
                    double inkA = a[i].W * a[i].H * a[i].Opacity, inkB = b[i].W * b[i].H * b[i].Opacity;
                    Assert.True(Math.Abs(inkA - inkB) < 0.5, $"{key}: patratul {i} sare la reluare ({inkA:F2} → {inkB:F2})");
                    if (inkA > 0.5 && inkB > 0.5)
                        Assert.True(Math.Abs(a[i].Y - b[i].Y) < 0.05, $"{key}: patratul {i} isi schimba locul la reluare");
                }
            }
        }

        [Fact]
        public void Orbit_LightMovesClockwise_OneSquareAtATime()
        {
            for (int i = 0; i < 4; i++)
            {
                var cells = SquareBadgeAnim.Cells(SquareBadgeAnim.Orbit, i / 4.0);
                int brightest = Enumerable.Range(0, 4).OrderByDescending(k => cells[k].Opacity).First();
                Assert.Equal(i, brightest);
                Assert.True(Math.Abs(cells[i].Opacity - 1.0) < 0.001);
            }
            // the order of the squares: top-left, top-right, bottom-right, bottom-left
            var c0 = SquareBadgeAnim.Cells(SquareBadgeAnim.Orbit, 0);
            Assert.True(c0[1].X > c0[0].X && c0[1].Y == c0[0].Y);
            Assert.True(c0[2].Y > c0[1].Y && c0[2].X == c0[1].X);
            Assert.True(c0[3].X == c0[0].X && c0[3].Y == c0[2].Y);
        }

        [Fact]
        public void Fill_GrowsFromTheBottom_ThenFadesOut()
        {
            var low  = SquareBadgeAnim.Cells(SquareBadgeAnim.Fill, 0.15)[1];
            var mid  = SquareBadgeAnim.Cells(SquareBadgeAnim.Fill, 0.40)[1];
            var full = SquareBadgeAnim.Cells(SquareBadgeAnim.Fill, 0.75)[1];
            Assert.True(low.H < mid.H && mid.H < full.H);
            // the bottom edge stays in place: the fill grows upwards
            Assert.True(Math.Abs((low.Y + low.H) - (full.Y + full.H)) < 0.001);
            Assert.True(Math.Abs(full.Opacity - 1.0) < 0.001);
            Assert.True(SquareBadgeAnim.Cells(SquareBadgeAnim.Fill, 0.95)[1].Opacity < 0.5);
            Assert.True(SquareBadgeAnim.Cells(SquareBadgeAnim.Fill, 0.5)[0].Outline);
        }

        [Fact]
        public void UnknownStyle_FallsBackToTheDefault()
        {
            Assert.Equal(SquareBadgeAnim.Cells(SquareBadgeAnim.Default, 0.3).Count, SquareBadgeAnim.Cells("SpinRing", 0.3).Count);
            Assert.False(SquareBadgeAnim.IsSquareStyle("SpinRing"));
            Assert.False(SquareBadgeAnim.IsSquareStyle(null));
            Assert.True(SquareBadgeAnim.Styles.All(s => SquareBadgeAnim.IsSquareStyle(s.Key)));
            Assert.True(SquareBadgeAnim.IsSquareStyle(SquareBadgeAnim.Default));
        }

        [Theory]
        [InlineData(0L, 0.0)]
        [InlineData(600L, 0.5)]
        [InlineData(1200L, 0.0)]
        [InlineData(1500L, 0.25)]
        public void PhaseAt_ComesFromTheClock(long elapsedMs, double expected)
            => Assert.True(Math.Abs(SquareBadgeAnim.PhaseAt(SquareBadgeAnim.Orbit, elapsedMs) - expected) < 0.0001);
    }
}
