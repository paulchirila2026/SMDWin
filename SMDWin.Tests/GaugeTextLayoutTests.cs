using System;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The value in the centre of a dial: the figures sit on the centre of the arc, the legend under them.
    public class GaugeTextLayoutTests
    {
        [Fact]
        public void Digits_AreCenteredOnTheArcCenter()
        {
            // a 30 px text row: baseline 24 px from the top, 18 px tall digits (occupying 6…24)
            var (valueTop, _) = GaugeTextLayout.Center(centerY: 62, valueBaseline: 24, capsHeight: 18);
            double inkTop = valueTop + 24 - 18, inkBottom = valueTop + 24;
            Assert.Equal(62, (inkTop + inkBottom) / 2, 3);
        }

        [Fact]
        public void OldPlacement_WasAboveTheCenter()
        {
            // the older rule: the line drawn at centre − 0.62 × the line height
            double oldTop = 62 - 30 * 0.62;
            double oldInkCenter = oldTop + 24 - 18 / 2.0;
            Assert.True(oldInkCenter < 62 - 2, "regula veche punea cifrele cu peste 2 px deasupra centrului");
        }

        [Fact]
        public void Caption_StartsBelowTheDigits_WithASmallGap()
        {
            var (valueTop, captionTop) = GaugeTextLayout.Center(62, 24, 18);
            double inkBottom = valueTop + 24;
            Assert.True(captionTop >= inkBottom + 2);
            Assert.True(captionTop <= inkBottom + 18 * 0.3);
        }

        [Theory]
        [InlineData(14, 11, 8)]
        [InlineData(60, 48, 36)]
        public void Works_AtAnySize(double lineHeight, double baseline, double caps)
        {
            var (valueTop, captionTop) = GaugeTextLayout.Center(100, baseline, caps);
            Assert.Equal(100, valueTop + baseline - caps / 2, 3);
            Assert.True(captionTop > 100);
            Assert.True(valueTop < 100 && valueTop + lineHeight > 100);
        }
    }
}
