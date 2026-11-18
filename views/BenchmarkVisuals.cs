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
        private readonly CountUp _count;
        /// <summary>A new mark counts up from the previous one (or 0) when the dial is on screen and animations are on.</summary>
        public double? Score
        {
            get => _score;
            set
            {
                var old = _score;
                _score = value;
                if (value is double v && (old == null || Math.Abs(old.Value - v) > 0.001)) _count.Start(old ?? 0, CountUpDelay);
                else if (value == null) _count.Stop();
                InvalidateVisual();
            }
        }
        /// <summary>Seconds to wait before counting up (the general mark waits for the finale of the drawing).</summary>
        public double CountUpDelay { get; set; }
        private string _caption = "";
        public string Caption { get => _caption; set { _caption = value; InvalidateVisual(); } }
        public int Segments { get; set; } = 14;
        /// <summary>Formats the mark in the app language (decimal comma in ro / de / fr).</summary>
        public Func<double, string> Format { get; set; } = v => v.ToString("0.0", CultureInfo.InvariantCulture);

        public ScoreDial()
        {
            _count = new CountUp(this);
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }
        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double? score = _score is double target ? _count.Value(target) : null;
            double R = Math.Min(W, H * 1.06) / 2 - 2;
            if (R < 20) return;
            double cx = W / 2, cy = H / 2 + R * 0.06;
            double rIn = R - GaugeArc.Thickness(R);
            int n = Math.Max(6, Segments);
            double seg = GaugeArc.SweepDeg / n, gap = GaugeArc.GapDeg(seg);
            double lit = score is double s ? Math.Clamp(s, 0, 10) / 10.0 * n : 0;
            Brush on = score is double s2 ? TierBrush(s2) : (TryFindResource("DataBrush") as Brush ?? Brushes.DodgerBlue);
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
            string txt = score is double v ? Format(v) : "—";
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

    /// <summary>
    /// The small mark ring in the header of a component card (the same ring as the callouts of DeviceSketch):
    /// a dash before the test, an orange arc turning while the component is tested, the mark inside a coloured ring after.
    /// </summary>
    public sealed class MarkRing : FrameworkElement
    {
        private double? _mark;
        private readonly CountUp _count;
        /// <summary>A new mark fills the ring and counts up (when on screen and animations are on).</summary>
        public double? Mark
        {
            get => _mark;
            set
            {
                var old = _mark;
                _mark = value;
                if (value is double v && (old == null || Math.Abs(old.Value - v) > 0.001)) _count.Start(old ?? 0, 0.15);
                else if (value == null) _count.Stop();
                InvalidateVisual();
            }
        }
        private bool _active;
        public bool Active { get => _active; set { if (_active != value) { _active = value; UpdateTimer(); InvalidateVisual(); } } }
        public Func<double, string> Format { get; set; } = v => v.ToString("0.0", CultureInfo.InvariantCulture);

        private bool _hooked;   // drawing on every screen refresh while the component is tested
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public MarkRing()
        {
            _count = new CountUp(this);
            IsVisibleChanged += (_, _) => UpdateTimer();
            Loaded += (_, _) => { ThemeManager.ThemeChanged += OnTheme; UpdateTimer(); };
            Unloaded += (_, _) => { ThemeManager.ThemeChanged -= OnTheme; Unhook(); };
        }
        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        private void UpdateTimer()
        {
            bool animate = _active && IsVisible && IsLoaded && SettingsService.Current?.EnableAnimations != false;
            if (animate && !_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
            else if (!animate) Unhook();
        }

        private void Unhook()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }

        private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double th = Math.Max(3, Math.Min(W, H) * 0.11);
            double r = Math.Min(W, H) / 2 - th / 2 - 1;
            if (r < 6) return;
            var c = new Point(W / 2, H / 2);
            var track = TryFindResource("BorderBrush2") as Brush ?? Brushes.Gray;
            dc.DrawEllipse(null, new Pen(track, th), c, r, r);
            if (_active)
            {
                var hot = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)); hot.Freeze();
                double a0 = (_hooked ? _clock.Elapsed.TotalSeconds * 300 : 0) % 360 - 90;
                dc.DrawGeometry(null, new Pen(hot, th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, a0, a0 + 100));
                return;
            }
            var ink = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            var ink2 = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_mark is double target)
            {
                double m = _count.Value(target);
                double sweep = Math.Clamp(m, 0, 10) / 10 * 360;
                if (sweep > 0.5)
                    dc.DrawGeometry(null, new Pen(ScoreDial.TierBrush(m), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, -90, -90 + Math.Min(359.5, sweep)));
                var num = new Typeface(GaugeArc.NumFont(this), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(Format(m), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, num, Math.Max(9, r * 0.72), ink, dpi);
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                return;
            }
            var dash = new Pen(ink2, 1.5) { };
            dc.PushOpacity(0.6);
            dc.DrawLine(dash, new Point(c.X - r * 0.3, c.Y), new Point(c.X + r * 0.3, c.Y));
            dc.Pop();
        }

        private static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
        {
            double a0 = fromDeg * Math.PI / 180, a1 = toDeg * Math.PI / 180;
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, true);
            }
            sg.Freeze();
            return sg;
        }
    }

    /// <summary>
    /// A number that counts up to its new value in 0.9 s (ease-out), drawing its owner on every screen refresh meanwhile.
    /// Only when the owner is on screen and the animations are on; otherwise the value is shown at once.
    /// </summary>
    internal sealed class CountUp
    {
        private readonly FrameworkElement _owner;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _from, _start, _delay;
        private bool _hooked;
        private const double Duration = 0.9;

        public CountUp(FrameworkElement owner)
        {
            _owner = owner;
            owner.Unloaded += (_, _) => Stop();
        }

        public void Start(double from, double delay)
        {
            if (!_owner.IsLoaded || !_owner.IsVisible || SettingsService.Current?.EnableAnimations == false) { Stop(); return; }
            _from = from; _delay = delay; _start = _clock.Elapsed.TotalSeconds;
            if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
        }

        /// <summary>The value to draw now for the given target.</summary>
        public double Value(double target)
        {
            if (!_hooked) return target;
            double x = (_clock.Elapsed.TotalSeconds - _start - _delay) / Duration;
            if (x >= 1) { Stop(); return target; }
            if (x <= 0) return _from;
            double e = 1 - Math.Pow(1 - x, 3);
            return _from + (target - _from) * e;
        }

        public void Stop()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }

        private void OnFrame(object? sender, EventArgs e) => _owner.InvalidateVisual();
    }
}
