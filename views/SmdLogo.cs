using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

using Brush  = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color  = System.Windows.Media.Color;
using Pen    = System.Windows.Media.Pen;
using Point  = System.Windows.Point;
using Size   = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// The SMD Win logo — a segmented gauge with a pulse line — drawn as vectors, so it is sharp at any size and every part
    /// can be animated. The same drawing as Assets/smdwin.ico (variant A).
    ///  • <see cref="Level"/>: how many segments are lit (0…14, fractions fade the next segment in — no visible steps).
    ///  • <see cref="PulseProgress"/>: how much of the pulse line is drawn (0…1).
    ///  • <see cref="PulseDot"/>: a light dot running along the pulse (0…1; negative = hidden).
    ///  • <see cref="PulseScale"/>: a short "beat" of the pulse (1 = normal).
    ///  • <see cref="PulseGlow"/>: a soft halo around the pulse line (0…1), used with the scale for the heartbeat.
    ///  • <see cref="PulseStart"/>: where the drawn part of the pulse begins (0…1) — with PulseProgress it draws and erases
    ///    the line like a monitor trace.
    ///  • <see cref="Glint"/>: a light passing over the segments (position in segments 0…14; negative = none).
    ///  • <see cref="ThickRing"/>: the 30 % thicker ring of the taskbar icon (above 30 px).
    ///  • <see cref="Spin"/>: loading mode — two opposite segments run around the arc (negative = off).
    /// Below 30 px the simplified drawing is used (9 thicker segments, thicker pulse), like the 16/20/24 px icons.
    /// Geometry in a 256 × 256 box: centre (128, 150), arc from 221.25° clockwise over 262.5°.
    /// </summary>
    public class SmdLogo : FrameworkElement
    {
        private sealed record Spec(int N, string[] Colors, double RIn, double ROut, double Gap, double PulseW);

        private static readonly Spec Full = new(14,
            new[] { "#22DDFF", "#23D2FF", "#25C7FF", "#26BBFF", "#27B1FF", "#29A7FF", "#2B9CFF", "#2C91FF", "#2E86FF", "#2F7BFF",
                    "#FFB81A", "#FF8A25", "#FF5A2E", "#FF2D3A" }, 98, 124, 3.4, 14);
        // the ring and pulse 30 % thicker, wider gaps — only for the taskbar / tray / .ico, where the normal
        // drawing looked thin next to other apps' icons; inside the program it would look heavy, so the program keeps Full
        private static readonly Spec Thick = Full with { RIn = 124 - 26 * 1.3, Gap = 7, PulseW = 18.2 };
        private static readonly Spec Simple = new(9,
            new[] { "#22DDFF", "#25C7FF", "#27B1FF", "#2B9CFF", "#2E86FF", "#2F7BFF", "#FFB81A", "#FF7A28", "#FF2D3A" }, 86, 126, 6.5, 24);

        private const double Cx = 128, Cy = 150, Start = 221.25, Span = 262.5;
        private static readonly Point[] PulsePts =
        {
            new(55, 150), new(95, 150), new(115, 105), new(140, 200), new(160, 142), new(172, 150), new(200, 150),
        };

        private static readonly Dictionary<Spec, (Geometry[] Segs, SolidColorBrush[] Brushes)> Cache = new();

        // ── properties ───────────────────────────────────────────────────
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PulseProgressProperty = DependencyProperty.Register(nameof(PulseProgress), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PulseDotProperty = DependencyProperty.Register(nameof(PulseDot), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PulseScaleProperty = DependencyProperty.Register(nameof(PulseScale), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PulseGlowProperty = DependencyProperty.Register(nameof(PulseGlow), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PulseStartProperty = DependencyProperty.Register(nameof(PulseStart), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty GlintProperty = DependencyProperty.Register(nameof(Glint), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(-10.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(nameof(Spin), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty DimOpacityProperty = DependencyProperty.Register(nameof(DimOpacity), typeof(double), typeof(SmdLogo),
            new FrameworkPropertyMetadata(0.18, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Level         { get => (double)GetValue(LevelProperty);         set => SetValue(LevelProperty, value); }
        public double PulseProgress { get => (double)GetValue(PulseProgressProperty); set => SetValue(PulseProgressProperty, value); }
        public double PulseDot      { get => (double)GetValue(PulseDotProperty);      set => SetValue(PulseDotProperty, value); }
        public double PulseScale    { get => (double)GetValue(PulseScaleProperty);    set => SetValue(PulseScaleProperty, value); }
        public double PulseGlow     { get => (double)GetValue(PulseGlowProperty);     set => SetValue(PulseGlowProperty, value); }
        public double PulseStart    { get => (double)GetValue(PulseStartProperty);    set => SetValue(PulseStartProperty, value); }
        public double Glint         { get => (double)GetValue(GlintProperty);         set => SetValue(GlintProperty, value); }
        /// <summary>The thicker ring (taskbar / tray look). Plain property: set before the first render.</summary>
        public bool ThickRing { get; set; }
        public double Spin          { get => (double)GetValue(SpinProperty);          set => SetValue(SpinProperty, value); }
        /// <summary>
        /// The colour of the unlit segments, when it must not follow the app's theme — the tray icon sits on the Windows taskbar,
        /// whose light/dark setting is separate from SMD Win's (null = the theme's secondary text colour).
        /// </summary>
        public Color? DimColor { get; set; }
        /// <summary>Opacity of an unlit segment (it is drawn in the theme's secondary text colour).</summary>
        public double DimOpacity    { get => (double)GetValue(DimOpacityProperty);    set => SetValue(DimOpacityProperty, value); }

        /// <summary>Number of segments in the full drawing (Level runs 0…SegmentCount).</summary>
        public const int SegmentCount = 14;

        public SmdLogo()
        {
            SnapsToDevicePixels = true;
            // the unlit colour follows the theme
            SMDWin.Services.ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged -= OnTheme;
            Loaded += (_, _) => { SMDWin.Services.ThemeManager.ThemeChanged -= OnTheme; SMDWin.Services.ThemeManager.ThemeChanged += OnTheme; };
        }

        private void OnTheme() => Dispatcher.InvokeAsync(InvalidateVisual);

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsNaN(Width) ? 24 : Width, h = double.IsNaN(Height) ? w : Height;
            return new Size(w, h);
        }

        // ── drawing ──────────────────────────────────────────────────────
        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            var spec = size < 30 ? Simple : ThickRing ? Thick : Full;
            var (segs, brushes) = GetShapes(spec);

            dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));

            // the app's resources only on the app's own thread: looked up from the splash thread (a separate one) WPF would create
            // the brush there and the main window could no longer use it
            var app = System.Windows.Application.Current;
            var dimBase = DimColor
                          ?? (app != null && app.Dispatcher == Dispatcher
                              ? (TryFindResource("TextSecondaryBrush") as SolidColorBrush)?.Color : null)
                          ?? Color.FromRgb(0x8E, 0xA3, 0xC7);
            var dim = new SolidColorBrush(dimBase) { Opacity = DimOpacity };
            dim.Freeze();

            // Level is in full-drawing units (0…14); the simplified drawing maps it to its 9 segments
            double lit = Level * spec.N / (double)SegmentCount;
            double spin = Spin;
            for (int i = 0; i < spec.N; i++)
            {
                double on;
                if (spin >= 0)
                {
                    // two opposite heads, each with a short fading tail behind it
                    double head = spin * spec.N / SegmentCount;
                    on = Math.Max(Tail(head, i, spec.N), Tail(head + spec.N / 2.0, i, spec.N));
                }
                else on = Math.Clamp(lit - i, 0, 1);

                dc.DrawGeometry(dim, null, segs[i]);
                if (on > 0.001)
                {
                    dc.PushOpacity(on);
                    dc.DrawGeometry(brushes[i], null, segs[i]);
                    dc.Pop();
                    // the glint: a white sheen on the lit segments near its position, fading over ~1.5 segments
                    double gpos = Glint * spec.N / SegmentCount;
                    double gi = 1 - Math.Abs(gpos - (i + 0.5)) / 1.5;
                    if (gi > 0.01)
                    {
                        dc.PushOpacity(0.6 * gi * on);
                        dc.DrawGeometry(Brushes.White, null, segs[i]);
                        dc.Pop();
                    }
                }
            }

            DrawPulse(dc, spec, spin >= 0 ? 0.35 : 1.0);
            dc.Pop(); dc.Pop();
        }

        private static double Tail(double head, int i, int n)
        {
            double d = ((head - i) % n + n) % n;        // how far segment i is behind the head
            return d < 1 ? 1 : d < 3 ? 1 - (d - 1) / 2.4 : 0;
        }

        private void DrawPulse(DrawingContext dc, Spec spec, double opacity)
        {
            double progress = Math.Clamp(PulseProgress, 0, 1);
            double from = Math.Clamp(PulseStart, 0, 1);
            if (progress <= from + 0.001) return;
            var grad = new LinearGradientBrush(Color.FromRgb(0x22, 0xDD, 0xFF), Color.FromRgb(0x2F, 0x6B, 0xFF), new Point(55, 0), new Point(200, 0))
                { MappingMode = BrushMappingMode.Absolute };
            grad.Freeze();
            var pen = new Pen(grad, spec.PulseW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();

            double scale = PulseScale;
            if (Math.Abs(scale - 1) > 0.001) dc.PushTransform(new ScaleTransform(scale, scale, Cx, Cy));
            if (opacity < 1) dc.PushOpacity(opacity);

            // the drawn part of the polyline, by length
            double total = 0;
            for (int i = 1; i < PulsePts.Length; i++) total += (PulsePts[i] - PulsePts[i - 1]).Length;
            double want = total * progress, skip = total * from, acc = 0;
            Point At(double l)
            {
                double a = 0;
                for (int i = 1; i < PulsePts.Length; i++)
                {
                    double len = (PulsePts[i] - PulsePts[i - 1]).Length;
                    if (a + len >= l) return PulsePts[i - 1] + (PulsePts[i] - PulsePts[i - 1]) * ((l - a) / len);
                    a += len;
                }
                return PulsePts[^1];
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                // from PulseStart to PulseProgress along the polyline (the corners in between kept)
                ctx.BeginFigure(At(skip), false, false);
                for (int i = 1; i < PulsePts.Length; i++)
                {
                    double len = (PulsePts[i] - PulsePts[i - 1]).Length;
                    if (acc + len > skip && acc + len < want) ctx.LineTo(PulsePts[i], true, true);
                    acc += len;
                }
                ctx.LineTo(At(want), true, true);
            }
            g.Freeze();
            double glow = Math.Clamp(PulseGlow, 0, 1);
            if (glow > 0.01)
            {
                // the heartbeat's halo: the same line, wider and faint, under the real one
                var halo = new Pen(grad, spec.PulseW + 26 * glow) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                halo.Freeze();
                dc.PushOpacity(0.28 * glow);
                dc.DrawGeometry(null, halo, g);
                dc.Pop();
            }
            dc.DrawGeometry(null, pen, g);

            // the travelling dot
            double dot = PulseDot;
            if (dot >= 0 && dot <= 1)
            {
                double at = total * dot; acc = 0;
                for (int i = 1; i < PulsePts.Length; i++)
                {
                    double len = (PulsePts[i] - PulsePts[i - 1]).Length;
                    if (acc + len >= at)
                    {
                        var p = PulsePts[i - 1] + (PulsePts[i] - PulsePts[i - 1]) * ((at - acc) / len);
                        // fades in at the start and out at the end of its run
                        double a = Math.Min(1, Math.Min(dot / 0.12, (1 - dot) / 0.12));
                        var dotGlow = new RadialGradientBrush(Color.FromArgb((byte)(150 * a), 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                        dotGlow.Freeze();
                        dc.DrawEllipse(dotGlow, null, p, 22, 22);
                        var core = new SolidColorBrush(Color.FromArgb((byte)(240 * a), 255, 255, 255)); core.Freeze();
                        dc.DrawEllipse(core, null, p, 7.5, 7.5);
                        break;
                    }
                    acc += len;
                }
            }

            if (opacity < 1) dc.Pop();
            if (Math.Abs(scale - 1) > 0.001) dc.Pop();
        }

        private static (Geometry[] Segs, SolidColorBrush[] Brushes) GetShapes(Spec s)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(s, out var hit)) return hit;
                var segs = new Geometry[s.N];
                var brushes = new SolidColorBrush[s.N];
                double step = Span / s.N;
                for (int i = 0; i < s.N; i++)
                {
                    double a0 = Start - i * step, a1 = a0 - (step - s.Gap);
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(Pt(s.ROut, a0), true, true);
                        ctx.ArcTo(Pt(s.ROut, a1), new Size(s.ROut, s.ROut), 0, false, SweepDirection.Clockwise, true, true);
                        ctx.LineTo(Pt(s.RIn, a1), true, true);
                        ctx.ArcTo(Pt(s.RIn, a0), new Size(s.RIn, s.RIn), 0, false, SweepDirection.Counterclockwise, true, true);
                    }
                    // soften the corners a little, like the icon (a thin outline in the same colour, round joins)
                    var pen = new Pen(Brushes.Black, 2) { LineJoin = PenLineJoin.Round };
                    var widened = System.Windows.Media.Geometry.Combine(g, g.GetWidenedPathGeometry(pen), GeometryCombineMode.Union, null);
                    widened.Freeze();
                    segs[i] = widened;
                    var b = (SolidColorBrush)new BrushConverter().ConvertFromString(s.Colors[i])!;
                    b.Freeze();
                    brushes[i] = b;
                }
                var r = (segs, brushes);
                Cache[s] = r;
                return r;
            }
        }

        private static Point Pt(double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(Cx + r * Math.Cos(a), Cy - r * Math.Sin(a));
        }

        // ── ready-made animations ────────────────────────────────────────
        private static bool AnimationsOn => SMDWin.Services.SettingsService.Current.EnableAnimations;

        /// <summary>Moves the lit level smoothly to <paramref name="to"/> (segments) over <paramref name="ms"/>.</summary>
        public void AnimateLevel(double to, double ms, IEasingFunction? ease = null)
        {
            if (!AnimationsOn || ms <= 0) { BeginAnimation(LevelProperty, null); Level = to; return; }
            BeginAnimation(LevelProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
                { EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>One short "beat" of the pulse line.</summary>
        public void Beat()
        {
            if (!AnimationsOn) return;
            BeginAnimation(PulseScaleProperty, new DoubleAnimation(1.0, 1.14, TimeSpan.FromMilliseconds(180))
                { AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        /// <summary>
        /// A heartbeat for the pulse line: a strong beat and a smaller one right after it ("lub-dub"), each with a soft halo,
        /// then a rest — about 70 beats a minute, so it reads as calm. Runs until <see cref="StopHeartbeat"/>.
        /// </summary>
        public void StartHeartbeat(TimeSpan? begin = null) => Heartbeat(begin, forever: true);

        /// <summary>One "lub-dub" (used when a live reading peaks).</summary>
        public void StartHeartbeatOnce() => Heartbeat(null, forever: false);

        private void Heartbeat(TimeSpan? begin, bool forever)
        {
            if (!AnimationsOn) return;
            var cycle = TimeSpan.FromMilliseconds(forever ? 1700 : 720);
            var repeat = forever ? RepeatBehavior.Forever : new RepeatBehavior(1);
            var fill = forever ? FillBehavior.HoldEnd : FillBehavior.Stop;
            var scale = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = repeat, BeginTime = begin, FillBehavior = fill };
            var glow  = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = repeat, BeginTime = begin, FillBehavior = fill };
            var up   = new CubicEase { EasingMode = EasingMode.EaseOut };
            var down = new CubicEase { EasingMode = EasingMode.EaseInOut };
            void K(DoubleAnimationUsingKeyFrames a, double ms, double v, IEasingFunction e) =>
                a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), e));
            K(scale, 0, 1.00, up);   K(scale, 110, 1.11, up);  K(scale, 250, 1.00, down);
            K(scale, 330, 1.05, up); K(scale, 480, 1.00, down); K(scale, cycle.TotalMilliseconds, 1.00, down);
            K(glow, 0, 0.0, up);     K(glow, 110, 1.0, up);    K(glow, 280, 0.25, down);
            K(glow, 350, 0.6, up);   K(glow, 700, 0.0, down);  K(glow, cycle.TotalMilliseconds, 0.0, down);
            BeginAnimation(PulseScaleProperty, scale);
            BeginAnimation(PulseGlowProperty, glow);
        }

        public void StopHeartbeat()
        {
            BeginAnimation(PulseScaleProperty, null);
            BeginAnimation(PulseGlowProperty, null);
        }

        /// <summary>Loading mode on/off: two opposite segments run around the arc (about one turn per 1.6 s).</summary>
        public void SetSpinning(bool on)
        {
            if (!on || !AnimationsOn) { BeginAnimation(SpinProperty, null); Spin = on ? 0 : -1; return; }
            BeginAnimation(SpinProperty, new DoubleAnimation(0, SegmentCount, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }
}
