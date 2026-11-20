using System;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen   = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// The Windows way of showing a state (as in Settings and in the Windows Security app): a small filled circle in the
    /// state colour with a mark inside — a tick for good (green), "!" for a warning or a problem (yellow / orange / red),
    /// "i" for neutral information (blue, grey). It replaces the coloured "pills" next to the state words. The colour comes
    /// from <see cref="Fill"/>, usually bound to the Foreground of the state text, so the mark follows the text.
    /// </summary>
    public sealed class StatusMark : FrameworkElement
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(StatusMark),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

        public StatusMark() { Width = 14; Height = 14; SnapsToDevicePixels = true; }

        private enum Glyph { Tick, Bang, Info }

        /// <summary>Which mark a colour stands for: green → tick; red, orange, yellow → "!"; anything else → "i".</summary>
        private static Glyph GlyphFor(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min < 0.12) return Glyph.Info;                 // grey
            double h;
            if (max == r) h = 60 * (((g - b) / (max - min)) % 6);
            else if (max == g) h = 60 * ((b - r) / (max - min) + 2);
            else h = 60 * ((r - g) / (max - min) + 4);
            if (h < 0) h += 360;
            if (h >= 75 && h < 170) return Glyph.Tick;               // green
            if (h < 65 || h >= 330) return Glyph.Bang;               // red, orange, yellow
            return Glyph.Info;                                        // blue, violet
        }

        protected override void OnRender(DrawingContext dc)
        {
            var fill = Fill as SolidColorBrush;
            if (fill == null) return;
            double d = Math.Min(ActualWidth, ActualHeight), r = d / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(fill, null, c, r, r);

            // the mark is cut out of the circle: drawn in the card colour behind it (dark on Dark, white on Light)
            var under = TryFindResource("CardSolidBrush") as Brush
                     ?? (IsLight() ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x38)));
            var pen = new Pen(under, Math.Max(1.4, d * 0.115)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            switch (GlyphFor(fill.Color))
            {
                case Glyph.Tick:
                {
                    var g = new StreamGeometry();
                    using (var s = g.Open())
                    {
                        s.BeginFigure(new Point(c.X - r * 0.42, c.Y + r * 0.02), false, false);
                        s.LineTo(new Point(c.X - r * 0.12, c.Y + r * 0.32), true, true);
                        s.LineTo(new Point(c.X + r * 0.44, c.Y - r * 0.30), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                    break;
                }
                case Glyph.Bang:
                    dc.DrawLine(pen, new Point(c.X, c.Y - r * 0.45), new Point(c.X, c.Y + r * 0.08));
                    dc.DrawEllipse(under, null, new Point(c.X, c.Y + r * 0.42), pen.Thickness * 0.62, pen.Thickness * 0.62);
                    break;
                default:
                    dc.DrawLine(pen, new Point(c.X, c.Y - r * 0.05), new Point(c.X, c.Y + r * 0.45));
                    dc.DrawEllipse(under, null, new Point(c.X, c.Y - r * 0.42), pen.Thickness * 0.62, pen.Thickness * 0.62);
                    break;
            }
        }

        private bool IsLight()
        {
            if (TryFindResource("TextPrimaryBrush") is SolidColorBrush t)
                return t.Color.R + t.Color.G + t.Color.B < 3 * 128;
            return false;
        }
    }
}
