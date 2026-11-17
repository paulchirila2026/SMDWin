using System;

namespace SMDWin.Services
{
    public static class RingProgressCalculator
    {
        public readonly struct RingDashResult
        {
            public readonly double DashUnits;   // value for StrokeDashArray (both elements)
            public readonly double OffsetUnits; // value for StrokeDashOffset
            public readonly double CircumferencePx;

            public RingDashResult(double dashUnits, double offsetUnits, double circumferencePx)
            {
                DashUnits = dashUnits;
                OffsetUnits = offsetUnits;
                CircumferencePx = circumferencePx;
            }
        }

        /// <summary>
        /// Computes dash/offset for a circular ring drawn with an Ellipse, so that the visible part of the circle is exactly
        /// `percent`% of the shape's real circumference.
        /// </summary>
        /// <param name="diameterPx">Width/Height of the Ellipse in XAML (bounding box).</param>
        /// <param name="strokeThicknessPx">Real StrokeThickness from XAML.</param>
        /// <param name="percent">0-100. 100 = full circle, 0 = empty circle.</param>
        public static RingDashResult Calculate(double diameterPx, double strokeThicknessPx, double percent)
        {
            if (diameterPx <= 0) throw new ArgumentOutOfRangeException(nameof(diameterPx));
            if (strokeThicknessPx <= 0) throw new ArgumentOutOfRangeException(nameof(strokeThicknessPx));

            double clampedPercent = Math.Max(0, Math.Min(100, percent));
            double radius = diameterPx / 2.0;
            double circumference = 2 * Math.PI * radius;

            double dashUnits = circumference / strokeThicknessPx;
            double offsetUnits = (1 - clampedPercent / 100.0) * dashUnits;

            return new RingDashResult(dashUnits, offsetUnits, circumference);
        }

        /// <summary>
        /// Length in pixels of the visible ("on") part of the ring, from the Calculate result. Useful for the self-test:
        /// the visible part must be ~= percent% of CircumferencePx.
        /// </summary>
        public static double VisibleArcLengthPx(RingDashResult result, double strokeThicknessPx)
            => result.CircumferencePx - result.OffsetUnits * strokeThicknessPx;
    }
}
