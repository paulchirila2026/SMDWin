using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin.Services;
using Brush      = System.Windows.Media.Brush;
using Brushes    = System.Windows.Media.Brushes;
using Color      = System.Windows.Media.Color;
using Point      = System.Windows.Point;
using Pen        = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using Size       = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// A mark dial of the Benchmark page: the same 270° block ring as the Dashboard dials (GaugeArc), the mark written inside
    /// ("5,4") with a small caption under it ("of 10", "running…", "next"). The colour is the colour of the mark's level.
    /// </summary>
    public sealed class ScoreDial : FrameworkElement
    {
        private double? _score;
        public double? Score { get => _score; set { _score = value; InvalidateVisual(); } }
        private string _caption = "";
        public string Caption { get => _caption; set { _caption = value; InvalidateVisual(); } }
        public int Segments { get; set; } = 14;
        /// <summary>Formats the mark in the app language (decimal comma in ro / de / fr).</summary>
        public Func<double, string> Format { get; set; } = v => v.ToString("0.0", CultureInfo.InvariantCulture);

        public ScoreDial()
        {
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }
        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double R = Math.Min(W, H * 1.06) / 2 - 2;
            if (R < 20) return;
            double cx = W / 2, cy = H / 2 + R * 0.06;
            double rIn = R - GaugeArc.Thickness(R);
            int n = Math.Max(6, Segments);
            double seg = GaugeArc.SweepDeg / n, gap = GaugeArc.GapDeg(seg);
            double lit = _score is double s ? Math.Clamp(s, 0, 10) / 10.0 * n : 0;
            Brush on = _score is double s2 ? TierBrush(s2) : (TryFindResource("DataBrush") as Brush ?? Brushes.DodgerBlue);
            var track = GaugeArc.Track(this);
            for (int i = 0; i < n; i++)
            {
                double a0 = GaugeArc.StartDeg + i * seg + gap / 2, a1 = GaugeArc.StartDeg + (i + 1) * seg - gap / 2;
                Brush fill = track;
                if (lit >= i + 1) fill = on;
                else if (lit > i) { var b = on.CloneCurrentValue(); b.Opacity = 0.3 + 0.7 * (lit - i); fill = b; }
                dc.DrawGeometry(fill, null, GaugeArc.Segment(cx, cy, R, rIn, a0, a1));
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ink = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            var ink2 = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var num = new Typeface(GaugeArc.NumFont(this), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            string txt = _score is double v ? Format(v) : "—";
            double size = Math.Max(14, rIn * 0.62);
            var ft = new FormattedText(txt, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, num, size, ink, dpi);
            double caps = GaugeArc.CapsHeight(num, size);
            double baseY = cy + caps * 0.35;
            dc.DrawText(ft, new Point(cx - ft.Width / 2, baseY - ft.Baseline));
            if (_caption.Length > 0)
            {
                var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var fc = new FormattedText(_caption, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, Math.Max(10, rIn * 0.2), ink2, dpi)
                { MaxTextWidth = rIn * 1.7, Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                dc.DrawText(fc, new Point(cx - fc.Width / 2, baseY + rIn * 0.12));
            }
        }

        /// <summary>The colour of a mark on the page's continuous red → green scale.</summary>
        internal static Brush TierBrush(double mark)
        {
            var (r, g, bl) = BenchmarkScore.ScaleColor(mark);
            var b = new SolidColorBrush(Color.FromRgb(r, g, bl)); b.Freeze(); return b;
        }
    }
}
