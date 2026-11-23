using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush      = System.Windows.Media.Brush;
using Brushes    = System.Windows.Media.Brushes;
using Color      = System.Windows.Media.Color;
using Point      = System.Windows.Point;
using Pen        = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;

namespace SMDWin.Views
{
    /// <summary>
    /// A percentage gauge with a 270° segmented arc — the same visual language as SegmentedGauge (Storage → Benchmark,
    /// Speed Test) and the widget's "Mini gauges", but for values 0–100 %:
    ///   • a LINEAR scale 0–100 (not logarithmic — with percentages each segment is worth the same);
    ///   • each lit segment has the colour of the ZONE it is in (normal / high / critical), so it shows at a glance how much
    ///     of the arc entered the yellow or red zone;
    ///   • configurable thresholds (WarnAt / CritAt); the value in the centre takes the colour of the current zone;
    ///   • the base colours come from the theme (AccentBrush, BorderBrush2, TextPrimary/Secondary) → works in Light and Dark.
    /// Drawn directly (OnRender) — a single visual element.
    /// </summary>
    public sealed class PercentGauge : FrameworkElement
    {
        private double _value;     // the received value
        private double _shown;     // the drawn value: slides towards _value (animations on), so the arc does not jump

        // The opening sweep: in the first seconds after the app starts, each dial fills from 0 to its value the first time it is
        // shown — once, a slower glide than the usual one. Later (other pages opened afterwards) the value appears normally.
        private const double IntroSec = 1.1;
        private bool _introDone;

        // each dial sweeps from 0 the first time it is shown, whenever that is (pages are built when first opened)
        private bool IntroAllowed =>
            !_introDone && SMDWin.Views.Fx.Motion;

        /// <summary>Starts the opening sweep if it is still due: from 0 to the current value.</summary>
        private bool TryIntro()
        {
            if (!IntroAllowed || !HasValue || !IsVisible || _value < 0.5) return false;
            _introDone = true;
            _shown = 0;
            StartGlide(IntroSec);
            return true;
        }

        public PercentGauge()
        {
            MinWidth = 36; MinHeight = 32;
            IsVisibleChanged += (_, _) => { if (IsVisible && TryIntro()) InvalidateVisual(); };
            // the colours come from the theme → redraw on a theme change (only while loaded)
            Loaded   += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged += OnThemeChanged;
            Unloaded += (_, _) => { SMDWin.Services.ThemeManager.ThemeChanged -= OnThemeChanged; _busyTimer?.Stop(); StopGlide(); _shown = _value; };
        }

        private void OnThemeChanged() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        public int Segments { get; set; } = 30;

        /// <summary>The threshold from which the segments switch to the warning colour (%).</summary>
        public double WarnAt { get; set; } = 75;
        /// <summary>The threshold from which the segments switch to the critical colour (%).</summary>
        public double CritAt { get; set; } = 90;

        /// <summary>The colour of the normal zone. Null = AccentBrush from the theme.</summary>
        public Brush? NormalBrush { get; set; }

        /// <summary>true = the normal zone in the text colour (TextPrimaryBrush); the accent stays only for navigation and links.
        /// Taken from the theme on every draw.</summary>
        public bool UseInk { get; set; }
        public Brush WarnBrush { get; set; } = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)));
        public Brush CritBrush { get; set; } = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

        /// <summary>Text mic sub valoare (ex. „used”).</summary>
        public string Caption { get; set; } = "";

        /// <summary>A small line under the arc (e.g. "6.2 / 8 GB"). Empty = not drawn.</summary>
        public string Footer { get; set; } = "";

        /// <summary>false = only the arc, without the value in the centre (the small variant, e.g. the Dashboard rows).</summary>
        public bool ShowValue { get; set; } = true;

        /// <summary>No data (empty arc, "—" in the centre).</summary>
        public bool HasValue { get; set; } = true;

        public double Value
        {
            get => _value;
            set
            {
                double v = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
                if (Math.Abs(v - _value) < 0.05 && HasValue) return;
                _value = v;
                bool animate = (SMDWin.Services.SettingsService.Current?.EnableAnimations ?? false) && IsVisible && HasValue;
                if (TryIntro()) { InvalidateVisual(); return; }
                if (animate && Math.Abs(_value - _shown) >= 0.3) StartGlide(GlideSec);
                else { StopGlide(); _shown = _value; }
                InvalidateVisual();
            }
        }

        // ── Smooth motion: the drawn value moves from where it is to the new value in a fixed time, starting and ending gently
        //    (ease in-out), so a jump from 50 to 70 % is seen as a steady sweep, not a leap followed by a crawl. A new value that
        //    arrives mid-way restarts the sweep from the current position. ──
        private const double GlideSec = 0.7;
        private bool _gliding;
        private double _glideFrom, _glideDur = GlideSec;
        private TimeSpan _glideStart = TimeSpan.MinValue;

        private void StartGlide(double durationSec)
        {
            _glideFrom = _shown;
            _glideDur = durationSec;
            _glideStart = TimeSpan.MinValue;   // taken from the next frame
            if (_gliding) return;
            _gliding = true;
            CompositionTarget.Rendering += OnGlideFrame;
        }

        private void StopGlide()
        {
            if (!_gliding) return;
            _gliding = false;
            CompositionTarget.Rendering -= OnGlideFrame;
        }

        private void OnGlideFrame(object? sender, EventArgs e)
        {
            var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
            if (_glideStart == TimeSpan.MinValue) _glideStart = now;
            double p = _glideDur <= 0 ? 1 : Math.Clamp((now - _glideStart).TotalSeconds / _glideDur, 0, 1);
            double k = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;   // ease in-out (cubic)
            _shown = _glideFrom + (_value - _glideFrom) * k;
            if (p >= 1 || !IsVisible) { _shown = _value; StopGlide(); }
            InvalidateVisual();
        }

        // ── "Working" animation: a bright arc (comet) goes around the dial while Busy = true ──
        private bool _busy;
        private double _phase;
        private System.Windows.Threading.DispatcherTimer? _busyTimer;

        public bool Busy
        {
            get => _busy;
            set
            {
                if (_busy == value) return;
                _busy = value;
                if (value)
                {
                    _busyTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
                    _busyTimer.Tick -= BusyTick; _busyTimer.Tick += BusyTick;
                    _busyTimer.Start();
                }
                else _busyTimer?.Stop();
                InvalidateVisual();
            }
        }

        private void BusyTick(object? s, EventArgs e) { _phase += 0.45; InvalidateVisual(); }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>The zone colour for a given percentage value.</summary>
        public Brush ZoneBrush(double pct)
        {
            if (pct >= CritAt) return CritBrush;
            if (pct >= WarnAt) return WarnBrush;
            if (NormalBrush != null) return NormalBrush;
            if (UseInk && TryFindResource("TextPrimaryBrush") is Brush ink) return ink;
            return (TryFindResource("DataBrush") as Brush) ?? (TryFindResource("AccentBrush") as Brush) ?? Brushes.DodgerBlue;
        }

        private const double StartDeg = 135, SweepDeg = 270;   // 0° = right, clockwise (Y down)

        /// <summary>The data colour at a position along the arc (0 = start, 1 = end): DataColor blended into DataEndColor.</summary>
        internal Brush DataAt(double t)
        {
            var a = (TryFindResource("DataBrush") as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.DodgerBlue;
            if (TryFindResource("DataEndColor") is not Color b) return Freeze(new SolidColorBrush(a));
            t = Math.Clamp(t, 0, 1);
            byte L(byte x, byte y) => (byte)(x + (y - x) * t);
            return Freeze(new SolidColorBrush(Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B))));
        }

        private static Point Pt(double cx, double cy, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 30 || H < 26) return;

            var off   = GaugeArc.Track(this);
            var text1 = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            var text2 = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var mono = new Typeface(GaugeArc.NumFont(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // The 270° arc takes ~1.71R vertically; leave room at the bottom for the Footer if there is one
            double footerPad = string.IsNullOrEmpty(Footer) ? 0 : 14;
            double R = Math.Min((W - 8) / 2, (H - 6 - footerPad) / 1.71);
            if (R < 12) return;
            double cx = W / 2, cy = R + 4;
            double rOut = R, rIn = R - GaugeArc.Thickness(R);

            int n = Math.Max(10, Segments);
            double segDeg = SweepDeg / n;
            double gapDeg = GaugeArc.GapDeg(segDeg);
            double fVal = HasValue ? _shown / 100.0 : 0;

            for (int i = 0; i < n; i++)
            {
                double a0 = StartDeg + i * segDeg + gapDeg / 2, a1 = StartDeg + (i + 1) * segDeg - gapDeg / 2;
                double segStart = i / (double)n, segEnd = (i + 1) / (double)n;
                double mid = (segStart + segEnd) / 2 * 100;
                // the zone colour by the middle of the segment
                Brush zone = ZoneBrush(mid);
                bool hotZone = mid >= WarnAt;
                // the normal zone with the default data colour: a gradient along the arc (DataColor → DataEndColor)
                if (!hotZone && NormalBrush == null && !UseInk) zone = DataAt(mid / 100.0);
                Brush fill;
                if (_busy)
                {
                    // the comet head moves along the arc; behind it the segments fade gradually
                    // a full circle: after the last segment the head passes the gap at the bottom and comes back at the first one
                    // after the same time, instead of jumping from the end straight to the start
                    const int Tail = 8;
                    double N = n * 360.0 / SweepDeg;
                    double head = _phase % N;
                    double dist = (head - i + N) % N;
                    if (dist < Tail)
                    {
                        var b = ((TryFindResource("DataBrush") as Brush) ?? (TryFindResource("AccentBrush") as Brush) ?? Brushes.DodgerBlue).CloneCurrentValue();
                        b.Opacity = 1.0 - dist / Tail * 0.9;
                        fill = b;
                    }
                    else fill = off;
                }
                else if (fVal >= segEnd) fill = zone;
                else if (fVal > segStart)
                {
                    var b = zone.CloneCurrentValue();
                    b.Opacity = 0.35 + 0.65 * (fVal - segStart) * n;
                    fill = b;
                }
                else if (hotZone)
                {
                    // the end of the arc (the attention / critical zone) is slightly coloured even when off — it shows in advance where the
                    // orange / red starts (the colour "towards the outside")
                    var b = zone.CloneCurrentValue();
                    b.Opacity = 0.22;
                    fill = b;
                }
                else fill = off;

                dc.DrawGeometry(fill, null, GaugeArc.Segment(cx, cy, rOut, rIn, a0, a1));
            }

            if (!ShowValue) return;

            // The value in the centre (the colour of the current zone) + a small legend
            string valTxt = HasValue ? _shown.ToString("F0", CultureInfo.InvariantCulture) + "%" : "—";
            double valueSize = Math.Max(14, R * 0.42);
            var fv = new FormattedText(valTxt, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                       mono, valueSize, HasValue ? ZoneBrush(_shown) : text1, dpi);
            FormattedText? fc = string.IsNullOrEmpty(Caption) ? null
                : new FormattedText(Caption, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                    ui, Math.Max(9, R * 0.15), text2, dpi);
            // the figures sit exactly on the centre of the arc (as in the card dials), the legend under them
            var (valueTop, captionTop) = SMDWin.Services.GaugeTextLayout.Center(cy, fv.Baseline, GaugeArc.CapsHeight(mono, valueSize));
            dc.DrawText(fv, new Point(cx - fv.Width / 2, valueTop));
            if (fc != null) dc.DrawText(fc, new Point(cx - fc.Width / 2, captionTop));
            if (!string.IsNullOrEmpty(Footer))
            {
                var ff = new FormattedText(Footer, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                                           ui, Math.Max(9, R * 0.14), text2, dpi);
                dc.DrawText(ff, new Point(cx - ff.Width / 2, cy + R * 0.72));
            }
        }
    }

    /// <summary>The shared geometry of all dials: PercentGauge (percentages) and SegmentedGauge (speeds) use the same
    /// thickness, the same gap between segments, the same colour for the unlit segments and the same font for the figures —
    /// they look identical anywhere in the app.</summary>
    internal static class GaugeArc
    {
        public const double StartDeg = 135, SweepDeg = 270;

        public static double Thickness(double r) => r >= 40 ? Math.Max(7, r * 0.15) : Math.Max(4, r * 0.24);

        public static double GapDeg(double segDeg) => Math.Min(2.6, segDeg * 0.3);

        public static Brush Track(FrameworkElement fe) =>
            fe.TryFindResource("GaugeTrackBrush") as Brush ?? fe.TryFindResource("BorderBrush2") as Brush ?? Brushes.LightGray;

        public static FontFamily NumFont(FrameworkElement fe) =>
            fe.TryFindResource("NumFont") as FontFamily ?? new FontFamily("Bahnschrift, Segoe UI");

        private static Point Pt(double cx, double cy, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        /// <summary>The height of a font's figures (capitals) at the given size — what is actually visible of the text line.</summary>
        public static double CapsHeight(Typeface font, double fontSize)
            => (font.CapsHeight > 0.3 && font.CapsHeight < 1 ? font.CapsHeight : 0.7) * fontSize;

        public static Geometry Segment(double cx, double cy, double rOut, double rIn, double a0, double a1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(Pt(cx, cy, rOut, a0), true, true);
                c.ArcTo(Pt(cx, cy, rOut, a1), new System.Windows.Size(rOut, rOut), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(Pt(cx, cy, rIn, a1), true, true);
                c.ArcTo(Pt(cx, cy, rIn, a0), new System.Windows.Size(rIn, rIn), 0, false, SweepDirection.Counterclockwise, true, true);
            }
            g.Freeze();
            return g;
        }
    }
}
