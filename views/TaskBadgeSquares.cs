using System;
using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen     = System.Windows.Media.Pen;

namespace SMDWin.Views
{
    /// <summary>
    /// The "test in progress" indicator made of squares (sidebar + the preview in Developer › Design experiments).
    /// Draws the frame given by <see cref="SquareBadgeAnim.Cells"/> for <see cref="Variant"/> and <see cref="Phase"/>, in the
    /// accent colour, at the element size. The phase is advanced by whoever hosts it.
    /// </summary>
    public sealed class TaskBadgeSquares : FrameworkElement
    {
        private string _variant = SquareBadgeAnim.Default;
        private double _phase;

        public string Variant
        {
            get => _variant;
            set { if (_variant == value) return; _variant = value; InvalidateVisual(); }
        }

        /// <summary>Pozitia in ciclu, 0–1.</summary>
        public double Phase
        {
            get => _phase;
            set { if (_phase == value) return; _phase = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double k = Math.Min(w, h) / SquareBadgeAnim.Box;
            double ox = (w - k * SquareBadgeAnim.Box) / 2, oy = (h - k * SquareBadgeAnim.Box) / 2;
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.Gray;

            foreach (var cell in SquareBadgeAnim.Cells(_variant, _phase))
            {
                if (cell.W <= 0.05 || cell.H <= 0.05 || cell.Opacity <= 0.01) continue;
                var rect = new Rect(ox + cell.X * k, oy + cell.Y * k, cell.W * k, cell.H * k);
                dc.PushOpacity(Math.Clamp(cell.Opacity, 0, 1));
                if (cell.Outline)
                {
                    var pen = new Pen(accent, 1.3 * k);
                    dc.DrawRoundedRectangle(null, pen, rect, 2.2 * k, 2.2 * k);
                }
                else
                {
                    double r = Math.Min(cell.W, cell.H) * 0.22 * k;   // corners only slightly rounded: they must stay squares
                    dc.DrawRoundedRectangle(accent, null, rect, r, r);
                }
                dc.Pop();
            }
        }
    }
}
