using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen     = System.Windows.Media.Pen;
using Point   = System.Windows.Point;

namespace SMDWin.Views
{
    /// <summary>
    /// A simple line chart for the RAM / Power page cards: a 1.5 px accent line, the area under it accent 8 %, 3 discreet
    /// horizontal lines, a dot on the last value.
    /// A fixed window of <see cref="Capacity"/> values; new data enters from the right. Drawn directly (OnRender).
    /// </summary>
    public sealed class MiniLineChart : FrameworkElement
    {
        private readonly List<double> _vals = new();
        private readonly List<double> _vals2 = new();   // the second series (optional): a grey line, no fill

        public int Capacity { get; set; } = 60;
        /// <summary>Smooth lines (curves) instead of straight segments, for all charts. The curve does not overshoot the real
        /// values (monotone interpolation), so the peaks and minimums stay the measured ones.</summary>
        public static bool SmoothLines { get; set; } = true;
        /// <summary>The lower bound of the scale.</summary>
        public double Min { get; set; } = 0;
        /// <summary>The upper bound; NaN = automatic (data peak + 10 %).</summary>
        public double Max { get; set; } = double.NaN;
        /// <summary>Dotted warning line (NaN = none).</summary>
        public double WarnLine { get; set; } = double.NaN;
        /// <summary>The warning line label ("85 °C limit"), drawn above it at the right end; null = none.</summary>
        public string? WarnText { get; set; }
        /// <summary>The text of the scale's upper limit, drawn in the top-left corner (e.g. "10 MB/s"), to show how much a peak
        /// means. null = no label.</summary>
        public Func<double, string>? ScaleText { get; set; }
        /// <summary>For the automatic scale (<see cref="Max"/> = NaN): the upper limit computed from the data peak, usually
        /// rounded to a "nice" value (<see cref="SMDWin.Services.ChartScale"/>). null = the peak + 10 %.</summary>
        public Func<double, double>? NiceMax { get; set; }

        /// <summary>The markers under the chart: two thin lines on the bottom edge, on the intervals in which the marker series
        /// given to <see cref="ShowHistory"/> are "on" — the first in accent (e.g. a test was running), the second in the
        /// warning colour (e.g. throttling). Each can be hidden separately.</summary>
        public bool ShowMarksA { get; set; } = true;
        public bool ShowMarksB { get; set; } = true;
        /// <summary>
        /// How the first marker (the test) is shown: true = the part of the curve during the test is drawn in red, with a red
        /// shade under it (it shows at a glance when the test ran); false = the thin line under the chart.
        /// </summary>
        public bool TintMarksA { get; set; } = true;
        private List<double>? _marksA, _marksB;
        /// <summary>true while the chart receives marker series: the bottom band stays reserved, so the chart line does not jump
        /// when a marker appears / disappears.</summary>
        private bool _hasMarkSeries;

        /// <summary>The automatic scale does not go below Min + MinSpan (e.g. 64 KB/s for traffic) — otherwise 1 KB/s noise
        /// fills the chart.</summary>
        public double MinSpan { get; set; } = 0;

        /// <summary>
        /// With animations on (Settings › Appearance › Animations) the chart flows: the new point appears immediately at the edge,
        /// and the rest of the drawing slides left until the next point.
        /// The value is in steps (0 = in place). Redrawn at 30 frames per second, only while the chart is visible.
        /// </summary>
        public static readonly DependencyProperty ScrollPhaseProperty = DependencyProperty.Register(
            nameof(ScrollPhase), typeof(double), typeof(MiniLineChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double ScrollPhase { get => (double)GetValue(ScrollPhaseProperty); set => SetValue(ScrollPhaseProperty, value); }

        /// <summary>
        /// The head of the line (the point at the edge) rises / falls smoothly towards the new value instead of jumping:
        /// 0 = still at the old value, 1 = arrived. Animated in the first part of the interval between reads.
        /// </summary>
        public static readonly DependencyProperty HeadBlendProperty = DependencyProperty.Register(
            nameof(HeadBlend), typeof(double), typeof(MiniLineChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double HeadBlend { get => (double)GetValue(HeadBlendProperty); set => SetValue(HeadBlendProperty, value); }

        private double _headFrom = double.NaN, _headFrom2 = double.NaN;   // the value the head starts from

        private DateTime _lastGlideUtc = DateTime.MinValue;

        /// <summary>
        /// Starts the slide by <paramref name="steps"/> steps. It lasts as long as the interval between the last two new points,
        /// so it ends exactly when the next one comes: the chart flows continuously, without stops, at any read rate (1 s, 3 s on
        /// battery, one point every 10 s on the one-hour window). What was left of the previous slide is added, so a point that
        /// came slightly early does not make the line jump either.
        /// With <paramref name="keepRunning"/> (a read that brought no new point) the slide in progress continues untouched.
        /// </summary>
        private void Glide(double steps, bool keepRunning = false)
        {
            bool on = SMDWin.Services.SettingsService.Current?.EnableAnimations ?? false;
            if (keepRunning && steps <= 0 && on && IsVisible) return;
            var now = DateTime.UtcNow;
            double gapMs = _lastGlideUtc == DateTime.MinValue ? 1000 : (now - _lastGlideUtc).TotalMilliseconds;
            _lastGlideUtc = now;
            if (!on || !IsVisible || steps <= 0 || _vals.Count < 2)
            {
                BeginAnimation(ScrollPhaseProperty, null);
                BeginAnimation(HeadBlendProperty, null);
                ScrollPhase = 0;
                HeadBlend = 1;
                return;
            }
            // the head starts from the value of `steps` reads ago (the one drawn at the edge until now)
            int from = _vals.Count - 1 - (int)Math.Round(steps);
            _headFrom = from >= 0 ? _vals[from] : double.NaN;
            int from2 = _vals2.Count - 1 - (int)Math.Round(steps);
            _headFrom2 = from2 >= 0 && _vals2.Count > 0 ? _vals2[from2] : double.NaN;

            double rest = Math.Clamp(ScrollPhase, 0, 1);   // how much of the previous step had not slid yet
            var anim = new System.Windows.Media.Animation.DoubleAnimation(steps + rest, 0,
                TimeSpan.FromMilliseconds(Math.Clamp(gapMs, 120, 12000)));
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, 30);
            BeginAnimation(ScrollPhaseProperty, anim);
            var head = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(Math.Clamp(gapMs * 0.45, 100, 600)))
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(head, 30);
            BeginAnimation(HeadBlendProperty, head);
        }

        public MiniLineChart()
        {
            MinHeight = 30;
            SnapsToDevicePixels = true;
            Loaded   += (_, _) => { _res = null; SMDWin.Services.ThemeManager.ThemeChanged += OnTheme; };
            Unloaded += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(() => { _res = null; _texts.Clear(); InvalidateVisual(); }));

        // ── The drawing resources, prepared once (and on a theme change), not on every frame ──
        // The chart redraws up to 30 times per second while it slides; its pen, fill and texts are reused, so memory does not
        // fill with new objects (its clean-ups cause small stutters).
        private sealed class Res
        {
            public SolidColorBrush Accent = null!;
            public Brush Fill = null!, TextSecondary = null!, Warn = null!, Crit = null!, Bg = null!, RedFill = null!;
            public Pen Grid = null!, Line = null!, Line2 = null!, WarnDash = null!, ScaleDash = null!, RedLine = null!, MarkWarn = null!, MarkCrit = null!;
        }
        private Res? _res;
        private readonly Dictionary<(string, double, double, int), FormattedText> _texts = new();

        private static Pen Frozen(Pen p) { p.Freeze(); return p; }
        private static Brush FrozenWithOpacity(Brush b, double opacity) { var c = b.CloneCurrentValue(); c.Opacity = opacity; c.Freeze(); return c; }

        /// <summary>The area under the line: the data colour at the top, fading out towards the bottom. On Dark it is lighter and
        /// fades out sooner — a strong fill on a dark card reads as a heavy block.</summary>
        private static Brush FadeFill(System.Windows.Media.Color c, bool dark)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            b.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(dark ? (byte)0x30 : (byte)0x48, c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x00, c.R, c.G, c.B), dark ? 0.85 : 1));
            b.Freeze();
            return b;
        }

        private Res R()
        {
            if (_res != null) return _res;
            var accent = TryFindResource("DataBrush") as SolidColorBrush ?? TryFindResource("AccentBrush") as SolidColorBrush ?? new SolidColorBrush(Colors.DodgerBlue);
            var grid = TryFindResource("BorderBrush2") as Brush ?? Brushes.LightGray;
            var sec = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var warn = TryFindResource("StatusWarnBrush") as Brush ?? Brushes.Orange;
            var crit = TryFindResource("StatusCritBrush") as SolidColorBrush ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            var r = new Res
            {
                Accent = accent,
                Fill = FadeFill(accent.Color, TryFindResource("BgDarkColor") is System.Windows.Media.Color bgc && bgc.R + bgc.G + bgc.B < 384),
                TextSecondary = sec, Warn = warn, Crit = crit,
                // the card background behind the scale text; none on translucent (glass) cards, where an opaque patch would show
                Bg = TryFindResource("CardSurfaceBrush") is SolidColorBrush cs && cs.Color.A < 255
                    ? null! : TryFindResource("BgCardBrush") as Brush ?? Brushes.Transparent,
                RedFill = FrozenWithOpacity(crit, 0.16),
                Grid = Frozen(new Pen(FrozenWithOpacity(grid, 0.6), 1)),
                Line = Frozen(new Pen(FrozenWithOpacity(accent, 1), 2) { LineJoin = PenLineJoin.Round }),
                Line2 = Frozen(new Pen(FrozenWithOpacity(sec, 1), 1.3) { LineJoin = PenLineJoin.Round }),
                WarnDash = Frozen(new Pen(FrozenWithOpacity(warn, 1), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }),
                ScaleDash = Frozen(new Pen(FrozenWithOpacity(sec, 1), 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) }),
                RedLine = Frozen(new Pen(FrozenWithOpacity(crit, 1), 1.8) { LineJoin = PenLineJoin.Round }),
                MarkWarn = Frozen(new Pen(FrozenWithOpacity(warn, 1), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }),
                MarkCrit = Frozen(new Pen(FrozenWithOpacity(crit, 1), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }),
            };
            // the pens have frozen copies of the colours (the theme resources stay untouched);
            // outside the visual tree the resources are missing: the fallbacks are not kept, it retries on the next draw
            if (IsLoaded) _res = r;
            return r;
        }

        /// <summary>The drawn text (scale, limit, "Collecting data…"), reused while the text, size and colour stay the same.</summary>
        private FormattedText Text(string text, double size, Brush brush)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var key = (text, size, dpi, brush.GetHashCode());
            if (_texts.TryGetValue(key, out var ft)) return ft;
            if (_texts.Count > 64) _texts.Clear();
            ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, brush, dpi);
            _texts[key] = ft;
            return ft;
        }

        public void Push(double v)
        {
            if (!double.IsFinite(v)) return;
            _vals.Add(v);
            while (_vals.Count > Math.Max(2, Capacity) + HiddenPoints) _vals.RemoveAt(0);
            Glide(1);
            InvalidateVisual();
        }

        /// <summary>Two series at once (e.g. read + write): the first in accent with fill, the second grey.</summary>
        public void Push(double v, double v2)
        {
            if (!double.IsFinite(v) || !double.IsFinite(v2)) return;
            _vals.Add(v); _vals2.Add(v2);
            int cap = Math.Max(2, Capacity) + HiddenPoints;
            while (_vals.Count > cap) _vals.RemoveAt(0);
            while (_vals2.Count > cap) _vals2.RemoveAt(0);
            Glide(1);
            InvalidateVisual();
        }

        public void Clear() { _vals.Clear(); _vals2.Clear(); InvalidateVisual(); }

        /// <summary>Replaces all points (e.g. the history kept in the background); the last <see cref="Capacity"/> stay.
        /// With <paramref name="keepGaps"/> NaN values stay gaps in the line (seconds without a read); otherwise they become 0.</summary>
        public void SetData(IList<double> v, IList<double>? v2 = null, int newPoints = 0, bool keepGaps = false)
            => SetData(v, v2, newPoints, keepGaps, keepRunning: false);

        private void SetData(IList<double> v, IList<double>? v2, int newPoints, bool keepGaps, bool keepRunning)
        {
            _vals.Clear(); _vals2.Clear();
            int cap = Math.Max(2, Capacity) + HiddenPoints;
            for (int i = Math.Max(0, v.Count - cap); i < v.Count; i++) _vals.Add(double.IsFinite(v[i]) ? v[i] : keepGaps ? double.NaN : 0);
            if (v2 != null)
                for (int i = Math.Max(0, v2.Count - cap); i < v2.Count; i++) _vals2.Add(double.IsFinite(v2[i]) ? v2[i] : keepGaps ? double.NaN : 0);
            Glide(newPoints, keepRunning);
            InvalidateVisual();
        }

        private long _shownBucket = long.MinValue;
        private int _shownBucketSeconds;

        /// <summary>
        /// Shows the last <paramref name="windowSeconds"/> seconds from a history with one point per second. On long windows a
        /// chart point covers several seconds (their peak), so the drawing stays light; seconds without a read stay gaps.
        /// <paramref name="secondSeries"/> = the second line (grey), −1 = none.
        /// </summary>
        public void ShowHistory(SMDWin.Services.SecondHistory history, int windowSeconds, int series, int secondSeries = -1,
                                int markSeriesA = -1, int markSeriesB = -1)
        {
            int bucket = SMDWin.Services.SeriesRuns.BucketSecondsFor(windowSeconds);
            Capacity = Math.Max(2, (int)Math.Ceiling(windowSeconds / (double)bucket));
            long end = history.Count - 1;
            int take = windowSeconds + HiddenPoints * bucket;
            var first = SMDWin.Services.SeriesRuns.Buckets(history.Window(series, take), end, bucket);
            var second = secondSeries >= 0 ? SMDWin.Services.SeriesRuns.Buckets(history.Window(secondSeries, take), end, bucket) : null;

            // how many new points appeared since the previous drawing → how far the chart slides (no slide after a pause or when
            // the time window changed)
            long lastBucket = SMDWin.Services.SeriesRuns.BucketOf(end, bucket);
            bool reset = _shownBucket == long.MinValue || _shownBucketSeconds != bucket;
            long added = reset ? 0 : lastBucket - _shownBucket;
            _shownBucket = lastBucket; _shownBucketSeconds = bucket;
            _hasMarkSeries = markSeriesA >= 0 || markSeriesB >= 0;
            _marksA = markSeriesA >= 0 ? SMDWin.Services.SeriesRuns.Buckets(history.Window(markSeriesA, take), end, bucket) : null;
            _marksB = markSeriesB >= 0 ? SMDWin.Services.SeriesRuns.Buckets(history.Window(markSeriesB, take), end, bucket) : null;
            // no new point (a read in the same second / the same group of seconds): the slide in progress continues
            SetData(first, second, added is >= 1 and <= 3 ? (int)added : 0, keepGaps: true, keepRunning: !reset && added == 0);
        }

        /// <summary>
        /// A few points older than <see cref="Capacity"/> are kept outside the chart, on the left: during the slide they leave
        /// the chart smoothly, instead of the start of the line jumping by one step on every read.
        /// </summary>
        private const int HiddenPoints = 10;

        /// <summary>The visible points (the last <see cref="Capacity"/>).</summary>
        private static IEnumerable<double> Visible(List<double> l, int cap)
        {
            for (int i = Math.Max(0, l.Count - cap); i < l.Count; i++) yield return l[i];
        }

        /// <summary>Points a+1..b of a continuous run (the figure is already started at a). Smooth: Bezier curves with monotone
        /// tangents (Fritsch–Carlson) — no "bellies" above or below the real values.</summary>
        private static void AddRun(StreamGeometryContext c, Func<int, Point> pt, int a, int b, bool stroked)
        {
            if (!SmoothLines || b - a < 2)
            {
                for (int i = a + 1; i <= b; i++) c.LineTo(pt(i), stroked, stroked);
                return;
            }
            int n = b - a + 1;
            var p = new Point[n];
            for (int i = 0; i < n; i++) p[i] = pt(a + i);
            var m = MonotoneSlopes(p);
            for (int i = 0; i < n - 1; i++)
            {
                double dx = (p[i + 1].X - p[i].X) / 3;
                c.BezierTo(new Point(p[i].X + dx, p[i].Y + m[i] * dx),
                           new Point(p[i + 1].X - dx, p[i + 1].Y - m[i + 1] * dx),
                           p[i + 1], stroked, stroked);
            }
        }

        /// <summary>The tangents (Fritsch–Carlson) for a smooth curve through points: the curve does not go above / below the
        /// real values. The control points of segment i: (x_i + dx, y_i + m_i·dx) and (x_i+1 − dx, y_i+1 − m_i+1·dx), with
        /// dx = the step / 3.</summary>
        internal static double[] MonotoneSlopes(IReadOnlyList<Point> p)
        {
            int n = p.Count;
            var m = new double[n];       // the tangent at each point
            if (n < 2) return m;
            var d = new double[n - 1];   // panta fiecarui segment
            for (int i = 0; i < n - 1; i++)
            {
                double dx = p[i + 1].X - p[i].X;
                d[i] = dx > 1e-9 ? (p[i + 1].Y - p[i].Y) / dx : 0;
            }
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++)
                m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (Math.Abs(d[i]) < 1e-12) { m[i] = 0; m[i + 1] = 0; continue; }
                double al = m[i] / d[i], be = m[i + 1] / d[i], sq = al * al + be * be;
                if (sq > 9) { double t = 3 / Math.Sqrt(sq); m[i] = t * al * d[i]; m[i + 1] = t * be * d[i]; }
            }
            return m;
        }

        public double Peak { get { double m = double.NaN; foreach (var v in Visible(_vals, Math.Max(2, Capacity))) if (double.IsFinite(v) && (double.IsNaN(m) || v > m)) m = v; return m; } }

        /// <summary>Text for a chart without data yet (e.g. right after start-up), instead of an empty chart.</summary>
        public string? EmptyText { get; set; }

        /// <summary>true when the chart has something to draw (at least two real points).</summary>
        public bool HasData => _vals.Count >= 2 && SMDWin.Services.SeriesRuns.Finite(_vals).Count > 0;

        private void DrawEmptyText(DrawingContext dc, double w, double h)
        {
            string text = EmptyText ?? SMDWin.Services.LanguageService.S("Common", "CollectingData", "Collecting data…");
            if (string.IsNullOrEmpty(text) || h < 24) return;
            var ft = Text(text, 12, R().TextSecondary);
            dc.PushOpacity(0.7);
            dc.DrawText(ft, new Point(Math.Max(0, (w - ft.Width) / 2), Math.Max(0, (h - ft.Height) / 2)));
            dc.Pop();
        }

        /// <summary>The upper limit label: a discreet dotted line at the top of the scale + its text at the top left.</summary>
        private void DrawScaleLabel(DrawingContext dc, double hi, double top, double width)
        {
            if (ScaleText == null) return;
            string text = ScaleText(hi);
            if (string.IsNullOrEmpty(text)) return;
            var res = R();
            var ft = Text(text, 10.5, res.TextSecondary);
            double y = Math.Round(top) + 0.5;
            dc.PushOpacity(0.55);
            dc.DrawLine(res.ScaleDash, new Point(ft.Width + 8, y), new Point(width, y));
            dc.Pop();
            // the card background under the text, so the chart line does not pass through it
            if (res.Bg is Brush bg)
            {
                dc.PushOpacity(0.95);
                dc.DrawRectangle(bg, null, new Rect(0, top - 3, ft.Width + 6, ft.Height + 1));
                dc.Pop();
            }
            dc.DrawText(ft, new Point(2, top - 3));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 10 || H < 10) return;

            var res = R();
            var accent = res.Accent;
            var gridPen = res.Grid;
            for (int k = 1; k <= 3; k++)
            {
                double y = Math.Round(H * k / 4.0) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(W, y));
            }
            if (_vals.Count < 2 || SMDWin.Services.SeriesRuns.Finite(_vals).Count == 0) { DrawEmptyText(dc, W, H); return; }
            // the continuous runs: a NaN (a second without a read) breaks the line instead of being joined or drawn as 0
            var runs = SMDWin.Services.SeriesRuns.Finite(_vals);
            if (runs.Count == 0) return;

            double lo = Min, hi = Max;
            if (!double.IsFinite(hi))
            {
                hi = lo;
                foreach (var v in Visible(_vals, Capacity)) if (double.IsFinite(v)) hi = Math.Max(hi, v);
                foreach (var v in Visible(_vals2, Capacity)) if (double.IsFinite(v)) hi = Math.Max(hi, v);
                if (NiceMax != null) hi = NiceMax(Math.Max(hi, lo + MinSpan));
                else { hi = hi <= lo ? lo + 1 : hi * 1.1; hi = Math.Max(hi, lo + MinSpan); }
            }
            if (hi <= lo) hi = lo + 1;
            bool markStrip = _hasMarkSeries && ((ShowMarksA && !TintMarksA) || ShowMarksB);
            double top = 4, bot = H - (markStrip ? 9 : 2);
            // The animated slide (0 without animations): the newest point sits at the right edge (it shows the current value,
            // together with the text), and the rest of the line starts further right and slides into place — like a pen writing at
            // the edge. Points that would pass the edge stay on it.
            double shift = ScrollPhase * (W - 4) / (Capacity - 1);
            double edgeX = W - 3;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));
            double X(int i, int count) => i == count - 1
                ? edgeX
                : Math.Min(edgeX, i * (W - 4) / (Capacity - 1) + 1 + shift + (Capacity - count) * (W - 4) / (Capacity - 1));
            double Y(double v) => bot - Math.Clamp((v - lo) / (hi - lo), 0, 1) * (bot - top);
            double blend = HeadBlend;
            // the head (the last point) moves smoothly from the old value to the new one
            double HeadY(List<double> vals, int i, double from) =>
                i == vals.Count - 1 && blend < 1 && double.IsFinite(from) ? Y(from) + (Y(vals[i]) - Y(from)) * blend : Y(vals[i]);
            Point P(int i) => new(X(i, _vals.Count), HeadY(_vals, i, _headFrom));

            if (double.IsFinite(WarnLine) && WarnLine > lo && WarnLine < hi)
            {
                double y = bot - (WarnLine - lo) / (hi - lo) * (bot - top);
                dc.DrawLine(res.WarnDash, new Point(0, y), new Point(W, y));
                if (!string.IsNullOrEmpty(WarnText))
                {
                    var wt = Text(WarnText, 10, res.Warn);
                    double ty = y - wt.Height - 1 < top ? y + 2 : y - wt.Height - 1;
                    dc.DrawText(wt, new Point(W - wt.Width - 2, ty));
                }
            }

            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                foreach (var (a, b) in runs)
                {
                    if (b == a) continue;   // an isolated point has no line
                    c.BeginFigure(P(a), false, false);
                    AddRun(c, P, a, b, true);
                }
            }
            line.Freeze();
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                foreach (var (a, b) in runs)
                {
                    if (b == a) continue;
                    c.BeginFigure(new Point(P(a).X, H), true, true);
                    c.LineTo(P(a), true, false);
                    AddRun(c, P, a, b, false);
                    c.LineTo(new Point(P(b).X, H), true, false);
                }
            }
            area.Freeze();
            dc.DrawGeometry(res.Fill, null, area);
            if (_vals2.Count >= 2)
            {
                // the second series, right-aligned like the first
                Point Q(int i) => new(X(i, _vals2.Count), HeadY(_vals2, i, _headFrom2));
                var l2 = new StreamGeometry();
                using (var c2 = l2.Open())
                {
                    foreach (var (a, b) in SMDWin.Services.SeriesRuns.Finite(_vals2))
                    {
                        if (b == a) continue;
                        c2.BeginFigure(Q(a), false, false);
                        AddRun(c2, Q, a, b, true);
                    }
                }
                l2.Freeze();
                dc.DrawGeometry(null, res.Line2, l2);
            }
            dc.DrawGeometry(null, res.Line, line);
            if (double.IsFinite(_vals[_vals.Count - 1]))
            {
                ChartPulse.Draw(dc, P(_vals.Count - 1), accent.Color);
                dc.DrawEllipse(accent, null, P(_vals.Count - 1), 3, 3);
            }
            // The markers are aligned to the right end of the chart. On long windows their number of points may differ by one from
            // the curve's (a different remainder when dividing into intervals), so they are matched from the right end.
            List<(double X0, double X1, bool ToEnd)> MarkSpans(List<double>? marks)
            {
                var spans = new List<(double X0, double X1, bool ToEnd)>();
                if (marks == null || marks.Count == 0) return spans;
                int n = _vals.Count, off = marks.Count - n;
                foreach (var (from, to) in SMDWin.Services.SeriesRuns.Above(marks, 0.5))
                {
                    int first = from - off, last = to - off;
                    if (last < 0 || first > n - 1) continue;
                    first = Math.Max(0, first); last = Math.Min(n - 1, last);
                    spans.Add((X(first, n), X(last, n), last == n - 1));
                }
                return spans;
            }

            if (_hasMarkSeries && ShowMarksA && TintMarksA)
            {
                // the test: the curve and its shade in red for the duration of the test (the same hue as the STRESS badge)
                var red = res.Crit; var redFill = res.RedFill; var redPen = res.RedLine;
                foreach (var span in MarkSpans(_marksA))
                {
                    double left = span.X0, right = span.ToEnd ? W : span.X1;
                    if (right - left < 2) left = right - 2;
                    dc.PushClip(new RectangleGeometry(new Rect(left, 0, right - left, H)));
                    dc.DrawGeometry(redFill, null, area);
                    dc.DrawGeometry(null, redPen, line);
                    dc.Pop();
                    if (span.ToEnd && double.IsFinite(_vals[_vals.Count - 1])) dc.DrawEllipse(red, null, P(_vals.Count - 1), 3, 3);
                }
            }
            if (markStrip)
            {
                // the line markers: 2 px on the bottom edge, aligned with the chart points
                void Marks(List<double>? marks, Pen markPen, double y)
                {
                    foreach (var span in MarkSpans(marks))
                    {
                        double left = span.X1 - span.X0 < 2 ? span.X1 - 2 : span.X0;
                        dc.DrawLine(markPen, new Point(left, y), new Point(span.X1, y));
                    }
                }
                if (ShowMarksB) Marks(_marksB, res.MarkWarn, H - 5.5);
                if (ShowMarksA && !TintMarksA) Marks(_marksA, res.MarkCrit, H - 1.5);
            }
            dc.Pop();
            DrawScaleLabel(dc, hi, top, W);
        }
    }
}
