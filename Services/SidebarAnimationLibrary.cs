using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using WpfColor       = System.Windows.Media.Color;
using WpfPoint       = System.Windows.Point;
using WpfRectangle   = System.Windows.Shapes.Rectangle;
using WpfApplication = System.Windows.Application;

namespace SMDWin.Services
{
    public static class SidebarAnimationLibrary
    {
        // ── Manual engine — fully replaces the Storyboard/Loop below ───────────
        private sealed class ActiveAnim
        {
            public required FrameworkElement Root;
            public required Action<double> Tick; // receives the current phase in [0,1)
            public DateTime Start = DateTime.UtcNow;
            public required double PeriodMs;
        }

        private static readonly List<ActiveAnim> _active = new();
        private static readonly object _lock = new();
        private static bool _renderingHooked = false;
        private static readonly System.Diagnostics.Stopwatch _throttleSw = System.Diagnostics.Stopwatch.StartNew();
        private static double _lastTickMs = 0;
        private static readonly List<ActiveAnim> EmptySnapshot = new();

        private static void EnsureDriver()
        {
            if (_renderingHooked) return;
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            double nowMs = _throttleSw.Elapsed.TotalMilliseconds;
            if (nowMs - _lastTickMs < 33.0) return; // throttle to ~30fps
            _lastTickMs = nowMs;

            List<ActiveAnim> snapshot;
            lock (_lock) snapshot = _active.Count == 0 ? EmptySnapshot : new List<ActiveAnim>(_active);
            if (snapshot.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var a in snapshot)
            {
                double elapsedMs = (now - a.Start).TotalMilliseconds;
                double phase = (elapsedMs % a.PeriodMs) / a.PeriodMs;
                try { a.Tick(phase); }
                catch (Exception ex)
                {
                    AppLogger.Debug($"SidebarAnimationLibrary tick error: {ex.Message}");
                    lock (_lock) _active.Remove(a);
                }
            }
        }

        /// <summary>Registers a new animation driven by the manual engine. onTick(phase) receives the current phase in [0,1) —
        /// the caller computes whatever it needs (angle, scale, opacity) directly from the phase, with no accumulated
        /// intermediate state. The first frame is drawn IMMEDIATELY (phase=0), before the driver's first tick, so the element
        /// never looks empty for a fraction of a second.</summary>
        private static void LoopManual(FrameworkElement root, double periodSeconds, Action<double> onTick)
        {
            var entry = new ActiveAnim { Root = root, Tick = onTick, PeriodMs = periodSeconds * 1000.0 };
            lock (_lock) _active.Add(entry);
            root.Unloaded += (_, _) => { lock (_lock) _active.Remove(entry); };
            try { onTick(0); } catch (Exception ex) { AppLogger.Debug($"SidebarAnimationLibrary first frame: {ex.Message}"); }
            EnsureDriver();
        }

        // ── Math helpers for phase formulas ────────────────────────────
        /// <summary>Triangle wave: 0→1→0 as t goes 0→1. For "back and forth" motions (breathing, sweep, pulse) without
        /// simulating AutoReverse.</summary>
        private static double Triangle(double t) => t < 0.5 ? t * 2.0 : (1.0 - t) * 2.0;
        /// <summary>Fractional part, always positive (unlike % in C# for negative numbers) — needed to compute the shifted phase
        /// of staggered elements (bars/dots that start at different moments of the same cycle).</summary>
        private static double Frac(double x) => x - Math.Floor(x);
        private static WpfColor LerpColor(WpfColor a, WpfColor b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return WpfColor.FromArgb(
                (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Static motif — 3 bars, no animation. For separators/small accents, not for progress indicators.</summary>
        public static UIElement BuildSignatureMarkStatic(double scale = 1.0, double opacity = 0.45)
        {
            const double bw = 3.6, gap = 0.6, startX = 1.0;
            (double left, double height)[] specs =
            {
                (startX,                 9.2),
                (startX + bw + gap,      6.7),
                (startX + 2*(bw + gap), 11.0),
            };
            var canvas = new Canvas { Width = 14 * scale, Height = 12 * scale, Opacity = opacity };
            foreach (var (left, height) in specs)
            {
                var r = new WpfRectangle { Width = bw * scale, Height = height * scale, RadiusX = 1, RadiusY = 1 };
                SetAccent(r, false);
                Canvas.SetLeft(r, left * scale); Canvas.SetBottom(r, 0);
                canvas.Children.Add(r);
            }
            return canvas;
        }

        /// <summary>Animated motif — the same 3 bars, lit in sequence (reuses SequentialBars() below). For
        /// "loading" / "checking" / "in progress" states.</summary>
        public static UIElement BuildSignatureMarkAnimated() => SequentialBars();

        public static readonly (string Key, string DisplayName)[] All =
        {
            ("BreathingDot",     "Breathing dot"),
            ("SequentialBars",   "Sequential bars (logo-style)"),
            ("ArcRotate",        "Spinning arc"),
            ("IconColorPulse",   "Icon color pulse"),
            ("SweepingUnderline","Sweeping underline"),
            ("OrbitDots",        "Orbiting dots"),
            ("ExpandingRing",    "Expanding ring"),
            ("WaveDots",         "Three dots, wave"),
            ("InfinityPath",     "Infinity path"),
            ("SegmentedClock",   "Segmented clock"),
            ("RadarSweep",       "Radar sweep"),
            ("PulsingBars",      "Pulsing bars (equalizer)"),
            ("BouncingDot",      "Bouncing dot"),
            ("CometOrbit",       "Comet orbit"),
            ("HeartbeatPulse",   "Heartbeat pulse"),
        };

        /// <summary>Builds the animated content for a 14x12 Canvas — used directly as a child of the badge Canvas
        /// (TaskBadgeStress/Disk/Ram).</summary>
        public static UIElement Build(string key) => key switch
        {
            "BreathingDot"      => BreathingDot(),
            "SequentialBars"    => SequentialBars(),
            "IconColorPulse"    => IconColorPulse(),
            "SweepingUnderline" => SweepingUnderline(),
            "OrbitDots"         => OrbitDots(),
            "ExpandingRing"     => ExpandingRing(),
            "WaveDots"          => WaveDots(),
            "InfinityPath"      => InfinityPath(),
            "SegmentedClock"    => SegmentedClock(),
            "RadarSweep"        => RadarSweep(),
            "PulsingBars"       => PulsingBars(),
            "BouncingDot"       => BouncingDot(),
            "CometOrbit"        => CometOrbit(),
            "HeartbeatPulse"    => HeartbeatPulse(),
            _                   => ArcRotate(), // "ArcRotate" and any unknown key
        };

        private static void SetAccent(Shape s, bool stroke)
        {
            if (stroke) s.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            else        s.SetResourceReference(Shape.FillProperty,   "AccentBrush");
        }

        private static WpfColor CurrentAccentColor()
        {
            if (WpfApplication.Current.Resources["AccentColor"] is WpfColor c) return c;
            return WpfColor.FromRgb(0x60, 0xA5, 0xFA);
        }

        // ── 1. Breathing dot ─────────────────────────────────────────────────
        private static UIElement BreathingDot()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var dot = new Ellipse { Width = 9, Height = 9, RenderTransformOrigin = new WpfPoint(0.5, 0.5) };
            SetAccent(dot, false);
            Canvas.SetLeft(dot, 2.5); Canvas.SetTop(dot, 1.5);
            var scale = new ScaleTransform(1, 1);
            dot.RenderTransform = scale;
            canvas.Children.Add(dot);

            LoopManual(canvas, 1.0, phase =>
            {
                double t = Triangle(phase);
                scale.ScaleX = scale.ScaleY = 1.0 + t * 1.0; // 1.0 -> 2.0 -> 1.0
                dot.Opacity = 1.0 - t * 0.25;                 // 1.0 -> 0.75 -> 1.0
            });
            return canvas;
        }

        // ── 2. Sequential bars (the app logo) ────────────────────────────
        // EXACT proportions measured from Assets/smdwin.png.
        private static UIElement SequentialBars()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var bars = new WpfRectangle[3];
            const double bw = 3.6, gap = 0.6, startX = 1.0;
            (double left, double height)[] specs =
            {
                (startX,                 9.2),
                (startX + bw + gap,      6.7),
                (startX + 2*(bw + gap), 11.0),
            };
            for (int i = 0; i < 3; i++)
            {
                var r = new WpfRectangle { Width = bw, Height = specs[i].height, RadiusX = 1, RadiusY = 1, Opacity = 0.3 };
                SetAccent(r, false);
                Canvas.SetLeft(r, specs[i].left); Canvas.SetBottom(r, 0);
                canvas.Children.Add(r); bars[i] = r;
            }
            // Each bar lights up in turn, staggered by 1/3 of the cycle, with some rest between (40% duty cycle) — reads clearly
            // as a "sequential wave", not as a simultaneous pulse.
            LoopManual(canvas, 1.2, phase =>
            {
                for (int i = 0; i < 3; i++)
                {
                    double local = Frac(phase - i / 3.0);
                    double t = local < 0.4 ? Triangle(local / 0.4) : 0.0;
                    bars[i].Opacity = 0.3 + t * 0.7;
                }
            });
            return canvas;
        }

        // ── 3. Spinning arc ──────────────────────────────────────────────────
        private static UIElement ArcRotate()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,1 A 5,5 0 1 1 2,6"),
                StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            SetAccent(path, true);
            var rot = new RotateTransform(0);
            path.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            path.RenderTransform = rot;
            canvas.Children.Add(path);

            LoopManual(canvas, 1.0, phase => rot.Angle = phase * 360.0);
            return canvas;
        }

        // ── 4. Icon color pulse ──────────────────────────────────────────────
        private static UIElement IconColorPulse()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,2 L 7,7 M 7,7 A 4,4 0 1 0 7.1,7"),
                StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            WpfColor accentColor = CurrentAccentColor();
            var warm = WpfColor.FromRgb(0xE0, 0x70, 0x4F);
            var brush = new SolidColorBrush(accentColor); // a real initial colour, never Transparent
            path.Stroke = brush;
            canvas.Children.Add(path);

            LoopManual(canvas, 1.6, phase => brush.Color = LerpColor(accentColor, warm, Triangle(phase)));
            return canvas;
        }

        // ── 5. Sweeping underline ────────────────────────────────────────────
        private static UIElement SweepingUnderline()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var track = new WpfRectangle { Width = 12, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0.2 };
            SetAccent(track, false);
            Canvas.SetLeft(track, 1); Canvas.SetTop(track, 8);
            canvas.Children.Add(track);

            var head = new WpfRectangle { Width = 5, Height = 3, RadiusX = 1.5, RadiusY = 1.5 };
            SetAccent(head, false);
            Canvas.SetTop(head, 8);
            canvas.Children.Add(head);

            LoopManual(canvas, 1.4, phase => Canvas.SetLeft(head, 1 + Triangle(phase) * 7)); // 1 -> 8 -> 1
            return canvas;
        }

        // ── 6. Orbiting dots ─────────────────────────────────────────────────
        private static UIElement OrbitDots()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            const double cx = 7, cy = 6; // the centre of the 14x12 canvas
            (double left, double top)[] pos = { (5.5, 0.5), (5.5, 8.5) };
            var g1 = new RotateTransform(0, cx - pos[0].left, cy - pos[0].top);
            var g2 = new RotateTransform(180, cx - pos[1].left, cy - pos[1].top); // pornesc opuse

            var d1 = new Ellipse { Width = 3, Height = 3, RenderTransform = g1 };
            SetAccent(d1, false); Canvas.SetLeft(d1, pos[0].left); Canvas.SetTop(d1, pos[0].top);
            var d2 = new Ellipse { Width = 3, Height = 3, Opacity = 0.5, RenderTransform = g2 };
            SetAccent(d2, false); Canvas.SetLeft(d2, pos[1].left); Canvas.SetTop(d2, pos[1].top);
            canvas.Children.Add(d1); canvas.Children.Add(d2);

            LoopManual(canvas, 1.2, phase =>
            {
                g1.Angle = phase * 360.0;
                g2.Angle = 180 + phase * 360.0;
            });
            return canvas;
        }

        // ── 7. Expanding ring ────────────────────────────────────────────────
        private static UIElement ExpandingRing()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var ring = new Ellipse { Width = 6, Height = 6, StrokeThickness = 2, RenderTransformOrigin = new WpfPoint(0.5,0.5) };
            SetAccent(ring, true);
            Canvas.SetLeft(ring, 4.5); Canvas.SetTop(ring, 3.5);
            var scale = new ScaleTransform(0.5, 0.5);
            ring.RenderTransform = scale;
            canvas.Children.Add(ring);

            var core = new Ellipse { Width = 5, Height = 5 };
            SetAccent(core, false);
            Canvas.SetLeft(core, 4.5); Canvas.SetTop(core, 3.5);
            canvas.Children.Add(core);

            LoopManual(canvas, 1.2, phase =>
            {
                scale.ScaleX = scale.ScaleY = 0.5 + phase * 1.7; // 0.5 -> 2.2
                ring.Opacity = 0.9 * (1.0 - phase);              // 0.9 -> 0
            });
            return canvas;
        }

        // ── 8. Three dots, wave ──────────────────────────────────────────────
        private static UIElement WaveDots()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            double[] lefts = { 1, 5.5, 10 };
            var dots = new Ellipse[3];
            for (int i = 0; i < 3; i++)
            {
                var d = new Ellipse { Width = 3, Height = 3 };
                SetAccent(d, false);
                Canvas.SetLeft(d, lefts[i]); Canvas.SetTop(d, 5);
                canvas.Children.Add(d); dots[i] = d;
            }
            LoopManual(canvas, 1.0, phase =>
            {
                for (int i = 0; i < 3; i++)
                {
                    double local = Frac(phase - i / 3.0);
                    double t = Triangle(local);
                    Canvas.SetTop(dots[i], 5 - t * 4); // 5 -> 1 -> 5
                }
            });
            return canvas;
        }

        // ── 9. Infinity path ─────────────────────────────────────────────────
        // Direct parametric formula (lemniscate) instead of MatrixAnimationUsingPath — the same visual path, computed directly
        // from the phase, without depending on the WPF animation engine for motion along a path.
        private static UIElement InfinityPath()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var dot = new Ellipse { Width = 4, Height = 4 };
            SetAccent(dot, false);
            canvas.Children.Add(dot);

            LoopManual(canvas, 1.6, phase =>
            {
                double t = phase * 2 * Math.PI;
                double x = 7 + 3.0 * Math.Sin(t);
                double y = 6 + 2.0 * Math.Sin(t) * Math.Cos(t);
                Canvas.SetLeft(dot, x - dot.Width / 2);
                Canvas.SetTop(dot, y - dot.Height / 2);
            });
            return canvas;
        }

        // ── 10. Segmented "clock" ────────────────────────────────────────────
        private static UIElement SegmentedClock()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            (double x1, double y1, double x2, double y2)[] segs =
            {
                (7,1,7,3.5), (10.5,3.5,9.4,4.6), (13,7,10.5,7), (10.5,10.5,9.4,9.4),
                (7,13,7,10.5), (3.5,10.5,4.6,9.4), (1,7,3.5,7), (3.5,3.5,4.6,4.6),
            };
            var lines = new Line[8];
            for (int i = 0; i < 8; i++)
            {
                var (x1,y1,x2,y2) = segs[i];
                var ln = new Line { X1=x1, Y1=y1, X2=x2, Y2=y2, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                SetAccent(ln, true);
                canvas.Children.Add(ln); lines[i] = ln;
            }
            LoopManual(canvas, 1.0, phase =>
            {
                for (int i = 0; i < 8; i++)
                {
                    double local = Frac(phase - i / 8.0);
                    double t = local < 0.2 ? Triangle(local / 0.2) : 0.0;
                    lines[i].Opacity = 1.0 - t * 0.85; // 1.0 -> 0.15 -> 1.0
                }
            });
            return canvas;
        }

        // ── 11. Radar sweep ──────────────────────────────────────────────────
        private static UIElement RadarSweep()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            const double cx = 7, cy = 6, len = 5.5;
            var groups = new RotateTransform[3];
            double[] opacities = { 1.0, 0.45, 0.2 };
            double[] angleOffsets = { 0, -22, -44 };
            for (int i = 0; i < 3; i++)
            {
                var ln = new Line { X1 = cx, Y1 = cy, X2 = cx, Y2 = cy - len,
                                     StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, Opacity = opacities[i] };
                SetAccent(ln, true);
                var rot = new RotateTransform(angleOffsets[i]);
                ln.RenderTransformOrigin = new WpfPoint(0.5, 1.0);
                ln.RenderTransform = rot;
                canvas.Children.Add(ln);
                groups[i] = rot;
            }
            LoopManual(canvas, 1.1, phase =>
            {
                for (int i = 0; i < 3; i++) groups[i].Angle = angleOffsets[i] + phase * 360.0;
            });
            return canvas;
        }

        // ── 12. Pulsing bars (equalizer) ─────────────────────────────────────
        private static UIElement PulsingBars()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            (double left, double baseH, double peak)[] specs = { (1, 4, 1.0), (5.5, 4, 1.4), (10, 4, 0.7) };
            var scales = new ScaleTransform[3];
            for (int i = 0; i < 3; i++)
            {
                var r = new WpfRectangle
                {
                    Width = 3, Height = specs[i].baseH, RadiusX = 1, RadiusY = 1,
                    RenderTransformOrigin = new WpfPoint(0.5, 1.0), // anchored at the bottom
                };
                SetAccent(r, false);
                Canvas.SetLeft(r, specs[i].left); Canvas.SetBottom(r, 0);
                var scale = new ScaleTransform(1, 1);
                r.RenderTransform = scale;
                canvas.Children.Add(r);
                scales[i] = scale;
            }
            LoopManual(canvas, 1.0, phase =>
            {
                for (int i = 0; i < 3; i++)
                {
                    double local = Frac(phase - i * 0.15);
                    double t = local < 0.35 ? Triangle(local / 0.35) : 0.0;
                    scales[i].ScaleY = 1.0 + t * (specs[i].peak - 1.0);
                }
            });
            return canvas;
        }

        // ── 13. Bouncing dot ──────────────────────────────────────────────────
        private static UIElement BouncingDot()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            var dot = new Ellipse { Width = 4, Height = 4, RenderTransformOrigin = new WpfPoint(0.5, 0.5) };
            SetAccent(dot, false);
            Canvas.SetTop(dot, 4);
            var translate = new TranslateTransform(0, 0);
            var scale = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            dot.RenderTransform = group;
            Canvas.SetLeft(dot, 1);
            canvas.Children.Add(dot);

            // 3 equal horizontal "jumps" (0->4.5->9->0, loop), with a short vertical squash on every landing.
            double[] pts = { 0.0, 4.5, 9.0, 0.0 };
            LoopManual(canvas, 1.05, phase =>
            {
                double scaled = phase * 3.0;
                int hop = Math.Min(2, (int)scaled);
                double local = scaled - hop;
                translate.X = pts[hop] + (pts[hop + 1] - pts[hop]) * local;
                // short squash in the last 15% of each jump (landing)
                scale.ScaleY = local > 0.85 ? 1.0 - 0.4 * Triangle((local - 0.85) / 0.15) : 1.0;
            });
            return canvas;
        }

        private static UIElement CometOrbit()
        {
            var canvas = new Canvas { Width = 14, Height = 12 };
            const double cx = 7, cy = 6, r = 4.3;
            const int echoCount = 4;
            var main = new Ellipse { Width = 3.4, Height = 3.4 };
            SetAccent(main, false);
            canvas.Children.Add(main);
            var echoes = new Ellipse[echoCount];
            for (int i = 0; i < echoCount; i++)
            {
                var e = new Ellipse { Width = 2.4, Height = 2.4, Opacity = 0.55 - i * 0.12 };
                SetAccent(e, false);
                canvas.Children.Add(e);
                echoes[i] = e;
            }
            void Place(Ellipse dot, double angleDeg)
            {
                double rad = angleDeg * Math.PI / 180.0;
                double x = cx + r * Math.Cos(rad), y = cy + r * Math.Sin(rad);
                Canvas.SetLeft(dot, x - dot.Width / 2);
                Canvas.SetTop(dot, y - dot.Height / 2);
            }
            LoopManual(canvas, 1.2, phase =>
            {
                double mainAngle = phase * 360.0;
                Place(main, mainAngle);
                for (int i = 0; i < echoCount; i++) Place(echoes[i], mainAngle - (i + 1) * 16);
            });
            return canvas;
        }

        private static UIElement HeartbeatPulse()
        {
            var canvas = new Canvas { Width = 14, Height = 12, ClipToBounds = true };
            var baseline = new Line { X1 = 0, Y1 = 6, X2 = 14, Y2 = 6, StrokeThickness = 1, Opacity = 0.25 };
            SetAccent(baseline, true);
            canvas.Children.Add(baseline);

            // Shape of the blip: flat - up - down below the line - back - flat (the classic ECG pulse silhouette), drawn once and
            // only translated on X.
            var blip = new Polyline
            {
                Points = new PointCollection
                {
                    new WpfPoint(0, 6), new WpfPoint(1.2, 6), new WpfPoint(2.0, 1.5),
                    new WpfPoint(2.8, 10), new WpfPoint(3.6, 6), new WpfPoint(5, 6),
                },
                StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            SetAccent(blip, true);
            canvas.Children.Add(blip);

            LoopManual(canvas, 1.3, phase => Canvas.SetLeft(blip, -6 + phase * 20)); // -6 -> 14, leaves the canvas completely and reappears from the left
            return canvas;
        }
    }
}
