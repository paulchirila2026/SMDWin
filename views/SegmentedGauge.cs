using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color   = System.Windows.Media.Color;
using Point   = System.Windows.Point;
using Pen     = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;

namespace SMDWin.Views
{
    /// <summary>
    /// A speed gauge with a segmented arc — used in Storage → Benchmark (live) and in the Internet Speed Test.
    ///
    /// • A 270° arc, split into segments that light up as the value grows.
    /// • LOGARITHMIC scale: log10(1+v) / log10(1+max) — plenty of room for small values (10, 100), while large values still
    ///   fit. Initially 0–InitialMax (1000 by default).
    /// • Or a STEPPED scale (Stops): each step takes an equal part of the arc, linear inside it.
    ///   E.g. {100, 600, 1000}: 100 at 1/3, 600 at 2/3, 1000 at the end — suitable for disks (HDD / SATA / the SATA ceiling).
    ///   On auto-extension the steps become {first, the former end, the new end}.
    /// • Auto-extension: when the value passes ~92% of the scale, the end rises to the next "round" step
    ///   (1000 → 2500 → 5000 → 10,000 → 25,000 …). It does not shrink during a test (it would jump), only on ResetScale().
    /// • The maximum reached (Peak) is marked with a half-lit segment.
    /// • Optional: the "typical" zone (BandLow–BandHigh) — a thin green line on the outside of the arc.
    /// • The current value is written in the centre, with the unit and a label below.
    /// Drawn directly (OnRender) — a single element, without hundreds of Shapes in the visual tree.
    /// </summary>
    public sealed class SegmentedGauge : FrameworkElement
    {
        private double _value, _peak, _scaleMax;

        public SegmentedGauge()
        {
            _scaleMax = InitialMax;
            MinWidth = 120; MinHeight = 110;
        }

        /// <summary>The scale end at start / after ResetScale.</summary>
        public double InitialMax { get; set; } = 1000;

        public int Segments { get; set; } = 28;

        /// <summary>A stepped scale (increasing values, the last = the end). Null = logarithmic scale.</summary>
        public double[]? Stops
        {
            get => _initialStops;
            set
            {
                _initialStops = value is { Length: > 0 } ? (double[])value.Clone() : null;
                _stops = _initialStops is null ? null : (double[])_initialStops.Clone();
                if (_stops != null) _scaleMax = _stops[^1];
                InvalidateVisual();
            }
        }
        private double[]? _initialStops, _stops;

        /// <summary>The colour of the lit segments. Null = AccentBrush from the theme (the default everywhere — the dials look
        /// the same across the app; see GaugeArc).</summary>
        public Brush? Accent { get; set; }

        public string Unit { get; set; } = "MB/s";
        public string Caption { get; set; } = "";
        public string ValueFormat { get; set; } = "F0";

        /// <summary>The typical zone (0 = no zone).</summary>
        public double BandLow  { get; set; }
        public double BandHigh { get; set; }

        public double ScaleMax => _scaleMax;
        /// <summary>The displayed maximum ("max ..."). When the page provides it (e.g. Speed Test: the sustained peak), it
        /// replaces the maximum computed from the animated value, so the figure in the dial and the one in the table match.</summary>
        public double? PeakOverride { get; set; }
        private double ShownPeak => PeakOverride ?? _peak;
        public double Peak => ShownPeak;

        /// <summary>
        /// The minimum and maximum given by the page. When present, "▼ min   ▲ max" appears under the value (blue / orange arrow,
        /// as in the Dashboard and in Temp &amp; Stress) instead of the "max …" text.
        /// </summary>
        public double? RangeMin { get; set; }
        public double? RangeMax { get; set; }
        private static readonly Brush RangeDownBrush = FrozenBrush(0x4F, 0xA3, 0xFF);
        private static readonly Brush RangeUpBrush   = FrozenBrush(0xFF, 0x8A, 0x3D);
        private static Brush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public double Value
        {
            get => _value;
            set
            {
                _value = Math.Max(0, double.IsFinite(value) ? value : 0);
                if (_value > _peak) _peak = _value;
                AutoExtend(_value);
                InvalidateVisual();
            }
        }

        /// <summary>Prepares a new test: value 0, no maximum, the initial scale.</summary>
        public void ResetScale()
        {
            _value = 0; _peak = 0; PeakOverride = null; RangeMin = null; RangeMax = null; _scaleMax = InitialMax;
            if (_initialStops != null) { _stops = (double[])_initialStops.Clone(); _scaleMax = _stops[^1]; }
            InvalidateVisual();
        }

        private void AutoExtend(double v)
        {
            if (v <= _scaleMax * 0.92) return;
            double target = v / 0.92;
            // the next round step: 1·10^n, 2.5·10^n, 5·10^n
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(target, 1))));
            foreach (var m in new[] { 1.0, 2.5, 5.0, 10.0 })
            {
                double cand = m * mag;
                if (cand >= target) { SetScaleMax(Math.Max(_scaleMax, cand)); return; }
            }
        }

        private void SetScaleMax(double newMax)
        {
            if (newMax <= _scaleMax) return;
            if (_stops != null)
                _stops = _stops.Length >= 3 ? new[] { _stops[0], _scaleMax, newMax } : new[] { _scaleMax, newMax };
            _scaleMax = newMax;
        }

        private double Frac(double v)
        {
            if (v <= 0) return 0;
            if (_stops != null)
            {
                int n = _stops.Length;
                double lo = 0;
                for (int i = 0; i < n; i++)
                {
                    double hi = _stops[i];
                    if (v <= hi) return Math.Clamp((i + (v - lo) / Math.Max(1e-9, hi - lo)) / n, 0, 1);
                    lo = hi;
                }
                return 1;
            }
            return Math.Clamp(Math.Log10(1 + v) / Math.Log10(1 + _scaleMax), 0, 1);
        }

        private const double StartDeg = 135, SweepDeg = 270;   // 0° = right, clockwise (Y down)

        private static Point Pt(double cx, double cy, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        private static string Short(double v) =>
            v >= 1000 ? (v % 1000 == 0 ? $"{v / 1000:0}K" : $"{v / 1000:0.#}K") : $"{v:0}";

        /// <summary>The data colour at a position along the arc (0 = start, 1 = end); null when the theme has no gradient end.</summary>
        private Brush? DataAt(double t)
        {
            if (TryFindResource("DataBrush") is not SolidColorBrush ab || TryFindResource("DataEndColor") is not Color b) return null;
            var a = ab.Color;
            t = Math.Clamp(t, 0, 1);
            byte L(byte x, byte y) => (byte)(x + (y - x) * t);
            var br = new SolidColorBrush(Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B)));
            br.Freeze();
            return br;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 60 || H < 60) return;

            var accentBase = Accent ?? (TryFindResource("DataBrush") as Brush) ?? (TryFindResource("AccentBrush") as Brush) ?? Brushes.DodgerBlue;
            var off    = GaugeArc.Track(this);
            var text1  = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            var text2  = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var band   = TryFindResource("StatusOkBrush") as Brush ?? Brushes.ForestGreen;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var mono = new Typeface(GaugeArc.NumFont(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Geometry: the 270° arc has its "mouth" at the bottom; the full circle would have a diameter of 2R, but vertically it
            // takes only R + R·sin(45°) ≈ 1.71R — it is centred on the real space.
            double labelPad = 16;
            double R = Math.Min((W - 2 * labelPad) / 2, (H - labelPad - 4) / 1.71);
            if (R < 30) return;
            double cx = W / 2, cy = labelPad + R + 2;
            double rOut = R, rIn = R - GaugeArc.Thickness(R);

            int n = Math.Max(8, Segments);
            double segDeg = SweepDeg / n;
            double gapDeg = GaugeArc.GapDeg(segDeg);
            double fVal = Frac(_value), fPeak = Frac(ShownPeak);
            int peakSeg = ShownPeak > 0 ? Math.Min(n - 1, (int)(fPeak * n - 1e-9)) : -1;

            for (int i = 0; i < n; i++)
            {
                double a0 = StartDeg + i * segDeg + gapDeg / 2, a1 = StartDeg + (i + 1) * segDeg - gapDeg / 2;
                double segEndFrac = (i + 1) / (double)n, segStartFrac = i / (double)n;
                // with the default data colour the arc is a gradient (DataColor → DataEndColor); a set Accent stays plain
                var accent = Accent ?? DataAt((segStartFrac + segEndFrac) / 2) ?? accentBase;
                // Fully lit if the value passes the end of the segment; partial (more transparent) for the segment the value is in —
                // a finer transition than "all or nothing".
                Brush fill;
                if (fVal >= segEndFrac) fill = accent;
                else if (fVal > segStartFrac)
                {
                    var b = accent.CloneCurrentValue();
                    b.Opacity = 0.35 + 0.65 * (fVal - segStartFrac) * n;
                    fill = b;
                }
                else if (i == peakSeg && fPeak > fVal + 0.5 / n)
                {
                    // the maximum reached: a half-lit segment (an outline looked like an empty box on Light)
                    var b = accent.CloneCurrentValue();
                    b.Opacity = 0.45;
                    fill = b;
                }
                else fill = off;

                var g = GaugeArc.Segment(cx, cy, rOut, rIn, a0, a1);
                dc.DrawGeometry(fill, null, g);
            }

            // The typical zone — a thin green line on the outside of the arc
            if (BandHigh > BandLow && BandHigh > 0)
            {
                double b0 = StartDeg + Frac(BandLow) * SweepDeg, b1 = StartDeg + Frac(BandHigh) * SweepDeg;
                if (b1 - b0 > 0.5)
                {
                    double rb = rOut + 4;
                    var bg = new StreamGeometry();
                    using (var c = bg.Open())
                    {
                        c.BeginFigure(Pt(cx, cy, rb, b0), false, false);
                        c.ArcTo(Pt(cx, cy, rb, b1), new System.Windows.Size(rb, rb), 0, b1 - b0 > 180, SweepDirection.Clockwise, true, true);
                    }
                    bg.Freeze();
                    dc.DrawGeometry(null, new Pen(band, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, bg);
                }
            }

            // Scale labels: 0, the powers of 10 in the interval and the end of the scale
            void Label(double v)
            {
                double deg = StartDeg + Frac(v) * SweepDeg;
                var ft = new FormattedText(Short(v), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                           ui, Math.Max(9, R * 0.1), text2, dpi);
                var p = Pt(cx, cy, rIn - ft.Height * 0.9, deg);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
            }
            Label(0);
            if (_stops != null) foreach (var st in _stops) Label(st);
            else
            {
                for (double p10 = 10; p10 < _scaleMax * 0.8; p10 *= 10) Label(p10);
                Label(_scaleMax);
            }

            // The value in the centre
            string valTxt = _value > 0 ? _value.ToString(ValueFormat, CultureInfo.InvariantCulture) : "—";
            double valueSize = Math.Max(14, R * 0.36);
            var fv = new FormattedText(valTxt, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                       mono, valueSize, text1, dpi);
            // the figures sit exactly on the centre of the arc, the unit under them (the same rule as PercentGauge)
            var (valueTop, unitTop) = SMDWin.Services.GaugeTextLayout.Center(cy, fv.Baseline, GaugeArc.CapsHeight(mono, valueSize));
            dc.DrawText(fv, new Point(cx - fv.Width / 2, valueTop));
            var fu = new FormattedText(string.IsNullOrEmpty(Caption) ? Unit : $"{Unit} · {Caption}",
                                       CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                       ui, Math.Max(9, R * 0.12), text2, dpi);
            dc.DrawText(fu, new Point(cx - fu.Width / 2, unitTop));
            if (RangeMin is double rangeMin && RangeMax is double rangeMax && rangeMax > 0)
            {
                string minTxt = rangeMin.ToString(ValueFormat, CultureInfo.InvariantCulture);
                string maxTxt = rangeMax.ToString(ValueFormat, CultureInfo.InvariantCulture);
                string range = $"▼ {minTxt}   ▲ {maxTxt}";
                var fr = new FormattedText(range, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                           ui, Math.Max(12, R * 0.14), text2, dpi);
                fr.SetForegroundBrush(RangeDownBrush, 0, 1);
                fr.SetForegroundBrush(RangeUpBrush, range.IndexOf('▲'), 1);
                dc.DrawText(fr, new Point(cx - fr.Width / 2, cy + R * 0.62));
            }
            else if (ShownPeak > 0 && ShownPeak > _value * 1.02)
            {
                var fp = new FormattedText("max " + ShownPeak.ToString(ValueFormat, CultureInfo.InvariantCulture),
                                           CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                           ui, Math.Max(12, R * 0.14), text2, dpi);
                dc.DrawText(fp, new Point(cx - fp.Width / 2, cy + R * 0.62));
            }
        }
    }
}
