using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The line drawing at the top of the Benchmark page: this computer (desktop, laptop seen from below with the cover off,
    /// all-in-one seen through the panel, or mini PC with the lid off) with the four tested parts and a callout for each one —
    /// a small mark ring, the part's name, the hardware and the value.
    ///   before the test: the parts in plain lines, the fans turning slowly;
    ///   during the test: the part being tested in orange, warm glow and heat waves, its fan speeding up with the load,
    ///     memory modules blinking, the storage activity LED flashing, a spinning ring in the callout;
    ///   after the test: each part in the colour of its mark, the mark inside its ring.
    /// Without a dedicated graphics card, the graphics callout points at the processor (the test runs on its graphics).
    /// The drawing is a fixed 760 × 420 scene scaled to the control; the animation runs only while visible and when the
    /// animations are on (Settings), with the screen refresh (a third of it while the graphics card is tested).
    /// </summary>
    public sealed class DeviceSketch : FrameworkElement
    {
        public enum Kind { Desktop, Laptop, AllInOne, MiniPc }
        public enum PartState { Idle, Next, Active, Done }

        /// <summary>One callout; the index is the benchmark component: 0 processor, 1 memory, 2 storage, 3 graphics.</summary>
        public sealed class Callout
        {
            public string Title = "", Hardware = "", Value = "";
            public PartState State;
            public double? Mark;
        }

        public readonly Callout[] Parts = { new(), new(), new(), new() };

        /// <summary>The four drawings: laptops (and tablets, ultrabooks), all-in-ones, mini PCs, and everything else as a tower.</summary>
        public static Kind KindFor(ComputerKind kind) => kind switch
        {
            ComputerKind.Laptop or ComputerKind.Ultrabook or ComputerKind.Tablet => Kind.Laptop,
            ComputerKind.AllInOne => Kind.AllInOne,
            ComputerKind.MiniPc => Kind.MiniPc,
            _ => Kind.Desktop,
        };

        private Kind _kind = Kind.Desktop;
        public Kind DeviceKind { get => _kind; set { if (_kind != value) { _kind = value; _model = null; InvalidateVisual(); } } }

        private bool _dgpu = true;
        /// <summary>The computer has a dedicated graphics card (drawn on desktops and laptops; never on all-in-ones and mini PCs).</summary>
        public bool DedicatedGpu { get => _dgpu; set { if (_dgpu != value) { _dgpu = value; InvalidateVisual(); } } }

        /// <summary>Formats a mark in the app language (decimal comma in ro / de / fr).</summary>
        public Func<double, string> Format { get; set; } = v => v.ToString("0.0", CultureInfo.InvariantCulture);

        private const double SceneW = 760, SceneH = 420;
        private const double RingX = 474, TextX = 504, LeadEndX = 450;
        // the callout rows from the top, in the order of the parts in the drawing: processor, memory, graphics, storage
        private static readonly int[] RowOrder = { 0, 1, 3, 2 };
        // the four rows centred on the middle of the scene, where every drawing is centred too
        private static readonly double[] RowY = { 84, 164, 244, 324 };

        private bool _hooked;      // drawing on every screen refresh (CompositionTarget.Rendering)
        private int _frame;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last;
        // smoothed fan loads (index = fan source) and their accumulated phases
        private readonly double[] _load = new double[4], _rot = new double[4], _edge = new double[4];
        private double _air, _trace, _ringSpin;
        private readonly double[] _glow = new double[4];

        public DeviceSketch()
        {
            IsVisibleChanged += (_, _) => UpdateTimer();
            Loaded += (_, _) => { ThemeManager.ThemeChanged += OnTheme; UpdateTimer(); };
            Unloaded += (_, _) => { ThemeManager.ThemeChanged -= OnTheme; Unhook(); };
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        /// <summary>Call after changing <see cref="Parts"/>: redraws and sets the frame rate for the new state.</summary>
        public void Refresh()
        {
            UpdateTimer();
            InvalidateVisual();
        }

        private static bool Animations => SettingsService.Current?.EnableAnimations != false;

        /// <summary>
        /// The animation follows the screen refresh (a timer at 15–30 frames per second made the fans jerk). While the
        /// graphics card is tested only every third refresh is drawn, so the drawing takes almost nothing from the test.
        /// </summary>
        private void UpdateTimer()
        {
            bool animate = IsVisible && IsLoaded && Animations;
            if (animate && !_hooked) { _last = _clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; _hooked = true; }
            else if (!animate) Unhook();
        }

        private void Unhook()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (Parts[3].State == PartState.Active && (++_frame % 3) != 0) return;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? SceneW : available.Width;
            double h = w * SceneH / SceneW;
            if (!double.IsInfinity(available.Height) && h > available.Height) { h = available.Height; w = h * SceneW / SceneH; }
            return new Size(w, h);
        }

        // ══ the drawing as data ════════════════════════════════════════════════════════════════════════════════════════

        private enum OpT { Shape, Fan, Edge, Air, Heat, Glow, Pipe, Trace, Led, Dot }
        private enum Sty { Ln, Ln2, LnFill, Faint, Shadow }

        private sealed class Op
        {
            public OpT T;
            public Geometry? G;
            public Sty S;
            public double X, Y, R, X2, Y2, W;
            public int Part = -1, Src = -1, Seed;
            public bool DGpu, Warm, Front;
        }

        private sealed class Model
        {
            public double S = 1, Dx, Dy;
            public bool CanDGpu;
            public readonly List<Op> Ops = new();
            public readonly Point[] Anchor = new Point[4];
            public Point Map(Point p) => new(p.X * S + Dx, p.Y * S + Dy);
        }

        /// <summary>Builds the ops in drawing order; <see cref="DGpu"/> / <see cref="Front"/> mark the ops added while set.</summary>
        private sealed class Builder
        {
            public readonly Model M = new();
            public bool DGpu, Front;
            private int _seed;
            private static Geometry Fz(Geometry g) { g.Freeze(); return g; }
            private Op Add(Op o) { o.DGpu = DGpu; o.Front = Front; M.Ops.Add(o); return o; }

            public void Rect(double x, double y, double w, double h, double rx, Sty s = Sty.Ln, int part = -1, double width = 0)
                => Add(new Op { T = OpT.Shape, G = Fz(new RectangleGeometry(new Rect(x, y, w, h), rx, rx)), S = s, Part = part, W = width, Seed = _seed++ });
            public void Circ(double cx, double cy, double r, Sty s = Sty.Ln2)
                => Add(new Op { T = OpT.Shape, G = Fz(new EllipseGeometry(new Point(cx, cy), r, r)), S = s });
            public void Shadow(double cx, double cy, double rx, double ry)
                => Add(new Op { T = OpT.Shape, G = Fz(new EllipseGeometry(new Point(cx, cy), rx, ry)), S = Sty.Shadow });
            public void Line(double x1, double y1, double x2, double y2, Sty s = Sty.Ln2)
                => Add(new Op { T = OpT.Shape, G = Fz(new LineGeometry(new Point(x1, y1), new Point(x2, y2))), S = s });
            public void Path(string d, Sty s = Sty.Ln2, int part = -1, double width = 0)
                => Add(new Op { T = OpT.Shape, G = Fz(Geometry.Parse(d)), S = s, Part = part, W = width });
            public void Fan(double cx, double cy, double r, int src) => Add(new Op { T = OpT.Fan, X = cx, Y = cy, R = r, Src = src });
            public void Edge(double x1, double y1, double x2, double y2, int src) => Add(new Op { T = OpT.Edge, X = x1, Y = y1, X2 = x2, Y2 = y2, Src = src });
            public void Air(string d, bool warm) => Add(new Op { T = OpT.Air, G = Fz(Geometry.Parse(d)), Warm = warm });
            public void Heat(double x, double y, int part) => Add(new Op { T = OpT.Heat, X = x, Y = y, Part = part });
            public void Glow(double cx, double cy, double r, int part) => Add(new Op { T = OpT.Glow, X = cx, Y = cy, R = r, Part = part });
            public void Pipe(string d, int part) => Add(new Op { T = OpT.Pipe, G = Fz(Geometry.Parse(d)), Part = part });
            public void Trace(string d) => Add(new Op { T = OpT.Trace, G = Fz(Geometry.Parse(d)) });
            public void Led(double x, double y) => Add(new Op { T = OpT.Led, X = x, Y = y, Part = 2 });
            public void Dot(double x, double y, double r) => Add(new Op { T = OpT.Dot, X = x, Y = y, R = r });
            public void Fins(double x0, double step, int n, double y1, double y2) { for (int i = 0; i < n; i++) Line(x0 + i * step, y1, x0 + i * step, y2, Sty.Faint); }
        }

        private Model? _model;
        private Model Current => _model ??= _kind switch
        {
            Kind.Laptop   => BuildLaptop(),
            Kind.AllInOne => BuildAllInOne(),
            Kind.MiniPc   => BuildMini(),
            _             => BuildDesktop(),
        };

        // fan sources
        private const int FanCpu = 0, FanGpu = 3, FanCase = 1, FanPsu = 2;

        /// <summary>A tower seen from the side with the glass panel off: rear exhaust, CPU cooler, 4 DIMMs, the card, PSU at the bottom.</summary>
        private static Model BuildDesktop()
        {
            var b = new Builder();
            var m = b.M; m.Dx = -30; m.CanDGpu = true;
            b.Shadow(270, 414, 175, 7);
            b.Rect(120, 22, 300, 378, 12, Sty.Ln, -1, 2.2); b.Rect(132, 34, 268, 354, 7, Sty.Ln2);
            b.Rect(120, 400, 34, 8, 2); b.Rect(386, 400, 34, 8, 2);
            b.Line(408, 34, 408, 388); b.Rect(372, 17, 22, 5, 2); b.Dot(383, 19.5, 1.6);
            b.Rect(124, 64, 6, 56, 1, Sty.Ln2);
            b.Rect(134, 44, 12, 80, 2); b.Edge(140, 47, 140, 121, FanCase);
            b.Air("M132 60 C110 58 96 54 80 48", true); b.Air("M132 84 C108 84 94 86 76 88", true); b.Air("M132 108 C110 112 96 118 80 126", true);
            b.Rect(152, 44, 204, 252, 4, Sty.Ln2); b.Path("M162 54 h80 v12 h-66 v52 h-14 z");
            foreach (var x in new[] { 270, 282, 294, 306 }) b.Rect(x, 68, 7, 104, 1.5, Sty.Ln, 1);
            b.Rect(338, 110, 9, 40, 1.5, Sty.Ln2);
            b.Glow(214, 128, 72, 0);
            b.Rect(178, 92, 72, 72, 6, Sty.Ln, 0); b.Fan(214, 128, 28, FanCpu);
            b.Heat(214, 86, 0);
            b.Trace("M250 112 L268 112"); b.Trace("M250 140 L268 140");
            b.Line(162, 200, 336, 200); b.Line(162, 246, 300, 246);
            b.DGpu = true;
            b.Glow(260, 207, 90, 3);
            b.Rect(146, 190, 228, 34, 5, Sty.Ln, 3); b.Rect(146, 186, 228, 4, 1.5, Sty.Ln2);
            b.Line(158, 207, 250, 207);
            b.Edge(170, 228, 236, 228, FanGpu); b.Edge(262, 228, 328, 228, FanGpu);
            b.DGpu = false;
            b.Rect(290, 254, 40, 30, 4, Sty.Ln2); b.Rect(176, 258, 88, 12, 2, Sty.Ln, 2); b.Led(258, 264);
            b.Path("M132 304 H400"); b.Rect(136, 318, 128, 58, 4);
            b.Edge(154, 380, 246, 380, FanPsu); b.Rect(150, 376, 100, 8, 2, Sty.Ln2);
            b.Air("M132 334 C112 334 100 336 86 340", true); b.Air("M132 356 C112 358 100 362 86 368", true);
            b.Rect(300, 318, 92, 24, 3); b.Rect(300, 348, 92, 24, 3);
            // the front intake fans are drawn over the callout lines
            b.Front = true;
            b.Rect(390, 56, 12, 80, 2, Sty.LnFill); b.Rect(390, 150, 12, 80, 2, Sty.LnFill);
            b.Edge(396, 59, 396, 133, FanCase); b.Edge(396, 153, 396, 227, FanCase);
            b.Air("M430 84 C424 86 418 90 410 96", false); b.Air("M432 140 C424 140 418 140 410 140", false); b.Air("M430 196 C424 194 418 190 410 186", false);
            m.Anchor[0] = new Point(250, 100); m.Anchor[1] = new Point(313, 120); m.Anchor[2] = new Point(264, 264); m.Anchor[3] = new Point(374, 207);
            return m;
        }

        /// <summary>A 15.6" laptop seen from below with the cover off: blowers and fin stacks at the rear, heat pipes, SO-DIMMs, M.2, battery.</summary>
        private static Model BuildLaptop()
        {
            var b = new Builder();
            var m = b.M; m.S = 0.85; m.Dx = -40; m.Dy = 39; m.CanDGpu = true;
            b.Shadow(300, 372, 225, 8);
            b.Rect(90, 46, 420, 16, 6, Sty.Ln2);
            b.Rect(90, 62, 420, 292, 16, Sty.Ln, -1, 2.2); b.Rect(98, 70, 404, 276, 11, Sty.Ln2);
            b.Rect(122, 60, 38, 16, 3); b.Rect(440, 60, 38, 16, 3);
            b.Rect(114, 72, 72, 12, 2, Sty.Ln2); b.Fins(120, 8, 8, 73, 83);
            b.Air("M128 64 C124 50 120 40 114 30", true); b.Air("M150 64 C150 50 150 40 150 30", true); b.Air("M172 64 C176 50 180 40 186 30", true);
            b.DGpu = true;
            b.Rect(414, 72, 72, 12, 2, Sty.Ln2); b.Fins(420, 8, 8, 73, 83);
            b.Air("M428 64 C424 50 420 40 414 30", true); b.Air("M450 64 C450 50 450 40 450 30", true); b.Air("M472 64 C476 50 480 40 486 30", true);
            b.DGpu = false;
            b.Path("M104 92 H496 V214 H404 V232 H196 V214 H104 Z");
            b.Fan(150, 128, 36, FanCpu);
            b.DGpu = true; b.Fan(450, 128, 36, FanGpu); b.DGpu = false;
            b.Glow(252, 134, 58, 0);
            b.Rect(238, 120, 30, 30, 2, Sty.Ln, 0); b.Rect(246, 128, 14, 14, 1, Sty.Ln2);
            b.Heat(253, 112, 0);
            b.DGpu = true;
            b.Glow(346, 134, 60, 3);
            b.Rect(328, 116, 36, 36, 2, Sty.Ln, 3); b.Rect(337, 125, 18, 18, 1, Sty.Ln2);
            foreach (var (x, y) in new[] { (312, 112), (312, 140), (370, 112), (370, 140) }) b.Rect(x, y, 12, 14, 1, Sty.Ln2);
            b.Heat(346, 108, 3);
            b.DGpu = false;
            b.Pipe("M244 128 C214 128 200 86 170 84", 0); b.Pipe("M244 142 C206 142 196 88 150 84", 0);
            b.DGpu = true;
            b.Pipe("M362 128 C392 128 402 86 430 84", 3); b.Pipe("M362 142 C400 142 406 88 450 84", 3);
            b.DGpu = false;
            b.Rect(206, 176, 82, 35, 2, Sty.Ln, 1); b.Rect(206, 186, 82, 35, 2, Sty.Ln, 1);
            b.Trace("M253 150 L253 176");
            b.Rect(300, 196, 94, 26, 2, Sty.Ln, 2); b.Rect(306, 203, 22, 12, 1, Sty.Ln2); b.Rect(334, 203, 22, 12, 1, Sty.Ln2); b.Led(388, 209);
            b.Rect(168, 176, 26, 30, 2, Sty.Ln2);
            b.Rect(138, 246, 324, 92, 7);
            foreach (var x in new[] { 219, 300, 381 }) b.Line(x, 250, x, 334);
            b.Rect(282, 232, 36, 14, 2, Sty.Ln2);
            b.Rect(104, 250, 26, 80, 6, Sty.Ln2); b.Rect(470, 250, 26, 80, 6, Sty.Ln2);
            m.Anchor[0] = new Point(268, 128); m.Anchor[1] = new Point(288, 200); m.Anchor[2] = new Point(394, 209); m.Anchor[3] = new Point(364, 140);
            return m;
        }

        /// <summary>A 24" all-in-one from the front, the parts behind the panel drawn through it: exhaust at the top, board, power board.</summary>
        private static Model BuildAllInOne()
        {
            var b = new Builder();
            var m = b.M; m.S = 0.82; m.Dx = -26; m.Dy = 41;
            b.Shadow(300, 414, 140, 8);
            b.Path("M282 300 L268 400 H332 L318 300 Z", Sty.Ln); b.Rect(216, 398, 168, 10, 5);
            b.Rect(80, 24, 440, 300, 12, Sty.LnFill, -1, 2.2);
            b.Rect(90, 34, 420, 246, 3, Sty.Ln2);
            b.Circ(300, 29, 2); b.Dot(490, 302, 2);
            for (int i = 0; i < 22; i++) { b.Circ(112 + i * 4, 302, 0.9); b.Circ(400 + i * 4, 302, 0.9); }
            b.Rect(300, 38, 96, 10, 2, Sty.Ln2);
            b.Air("M314 22 C312 12 308 6 302 -2", true); b.Air("M348 22 C348 12 348 6 348 -2", true); b.Air("M382 22 C384 12 388 6 394 -2", true);
            b.Fan(348, 92, 36, FanCpu);
            b.Path("M190 132 H420 V272 H190 Z");
            b.Pipe("M270 182 C272 150 300 128 322 112", 0); b.Pipe("M284 182 C288 156 316 134 334 120", 0);
            b.Glow(276, 196, 62, 0);
            b.Rect(260, 180, 32, 32, 2, Sty.Ln, 0); b.Rect(268, 188, 16, 16, 1, Sty.Ln2);
            b.Heat(276, 170, 0); b.Trace("M292 196 L314 196");
            b.Rect(318, 160, 82, 35, 2, Sty.Ln, 1); b.Rect(318, 200, 82, 35, 2, Sty.Ln, 1);
            b.Rect(204, 236, 94, 26, 2, Sty.Ln, 2); b.Rect(210, 243, 22, 12, 1, Sty.Ln2); b.Led(292, 249);
            b.Rect(318, 244, 26, 22, 2, Sty.Ln2);
            b.Rect(104, 140, 72, 128, 4);
            b.Circ(124, 166, 9); b.Circ(150, 166, 9); b.Rect(116, 190, 42, 30, 3, Sty.Ln2); b.Circ(126, 240, 6); b.Circ(148, 240, 6);
            b.Air("M226 336 C226 326 226 318 226 308", false); b.Air("M374 336 C374 326 374 318 374 308", false);
            m.Anchor[0] = new Point(292, 186); m.Anchor[1] = new Point(400, 178); m.Anchor[2] = new Point(298, 249); m.Anchor[3] = m.Anchor[0];
            return m;
        }

        /// <summary>A mini PC from the front and above, lid off: blower and fin stack at the rear, two SO-DIMMs, M.2, ports in front.</summary>
        private static Model BuildMini()
        {
            var b = new Builder();
            var m = b.M; m.Dx = -70; m.Dy = 3;
            b.Shadow(300, 372, 160, 9);
            b.Path("M180 92 Q180 80 192 80 H408 Q420 80 420 92 V300 H180 Z", Sty.Ln, -1, 2.2);
            b.Rect(190, 90, 220, 204, 8, Sty.Ln2);
            b.Path("M180 300 H420 V348 Q420 358 410 358 H190 Q180 358 180 348 Z", Sty.LnFill, -1, 2.2);
            b.Rect(204, 322, 22, 10, 2); b.Rect(234, 322, 22, 10, 2);
            b.Rect(266, 323, 14, 7, 3.5); b.Circ(294, 327, 4, Sty.Ln);
            b.Circ(394, 327, 7, Sty.Ln); b.Dot(394, 327, 2.2);
            b.Rect(230, 84, 110, 8, 2, Sty.Ln2);
            b.Air("M246 80 C242 64 238 54 232 42", true); b.Air("M284 80 C284 64 284 54 284 42", true); b.Air("M322 80 C326 64 330 54 336 42", true);
            b.Air("M140 170 C156 170 168 172 180 176", false); b.Air("M140 210 C156 210 168 208 180 204", false);
            b.Air("M460 170 C444 170 432 172 420 176", false); b.Air("M460 210 C444 210 432 208 420 204", false);
            b.Fan(240, 142, 40, FanCpu);
            b.Rect(282, 94, 60, 16, 2, Sty.Ln2); b.Fins(288, 8, 7, 96, 108);
            b.Pipe("M300 170 C300 146 306 126 312 110", 0); b.Pipe("M312 170 C314 148 322 128 328 110", 0);
            b.Glow(306, 186, 58, 0);
            b.Rect(288, 168, 36, 36, 2, Sty.Ln, 0); b.Rect(297, 177, 18, 18, 1, Sty.Ln2);
            b.Heat(306, 160, 0); b.Trace("M300 204 L284 224");
            b.Rect(202, 224, 110, 46, 2, Sty.Ln, 1); b.Rect(202, 234, 110, 46, 2, Sty.Ln, 1);
            b.Rect(352, 130, 34, 150, 2, Sty.Ln, 2); b.Rect(359, 140, 20, 28, 1, Sty.Ln2); b.Rect(359, 176, 20, 28, 1, Sty.Ln2); b.Led(369, 270);
            m.Anchor[0] = new Point(324, 186); m.Anchor[1] = new Point(312, 252); m.Anchor[2] = new Point(386, 210); m.Anchor[3] = m.Anchor[0];
            return m;
        }

        // ══ render ═════════════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly Color Hot = Color.FromRgb(0xFF, 0x8A, 0x3D), HotText = Color.FromRgb(0xFF, 0xB4, 0x6B), HotTextLight = Color.FromRgb(0xC2, 0x41, 0x0C),
                                      Cold = Color.FromRgb(0x6F, 0xA3, 0xFF), ColdLight = Color.FromRgb(0x3B, 0x6F, 0xD8), Copper = Color.FromRgb(0x8A, 0x6A, 0x52),
                                      Led = Color.FromRgb(0x3C, 0xCF, 0x7A), Power = Color.FromRgb(0x4F, 0x86, 0xFF);

        private static SolidColorBrush B(Color c, double o = 1) { var x = new SolidColorBrush(c) { Opacity = Math.Clamp(o, 0, 1) }; x.Freeze(); return x; }
        private static Pen P(Brush b, double w, double[]? dash = null, double offset = 0, bool round = false)
        {
            var p = new Pen(b, w) { LineJoin = PenLineJoin.Round };
            if (round) { p.StartLineCap = p.EndLineCap = PenLineCap.Round; p.DashCap = PenLineCap.Round; }
            if (dash != null) p.DashStyle = new DashStyle(dash, offset);
            p.Freeze();
            return p;
        }
        private static Color Mark(double mark) { var (r, g, bl) = BenchmarkScore.ScaleColor(mark); return Color.FromRgb(r, g, bl); }

        private bool LightTheme => TryFindResource("BgCardBrush") is SolidColorBrush b && (b.Color.R * 0.3 + b.Color.G * 0.59 + b.Color.B * 0.11) > 140;

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 60 || H < 40) return;

            double now = _clock.Elapsed.TotalSeconds;
            bool anim = Animations && _hooked;
            double dt = anim ? Math.Clamp(now - _last, 0, 0.2) : 0;
            _last = now;
            double t = anim ? now : 0;

            var model = Current;
            bool gpuShown = _dgpu && model.CanDGpu;
            var hot = new bool[4];
            for (int i = 0; i < 4; i++) hot[i] = Parts[i].State == PartState.Active;
            if (hot[3] && !gpuShown) hot[0] = true;   // integrated graphics: the processor works

            // fan loads follow the test smoothly (the fan spins up, it does not jump)
            double cpuL = hot[0] ? 1 : hot[1] ? 0.7 : hot[2] ? 0.12 : 0.05;
            double gpuL = hot[3] && gpuShown ? 1 : 0.03;
            double[] target = { cpuL, Math.Max(0.1, Math.Max(cpuL, gpuL) * 0.8), Math.Max(0.1, Math.Max(cpuL, gpuL) * 0.6), gpuL };
            for (int i = 0; i < 4; i++)
            {
                _load[i] = anim ? _load[i] + (target[i] - _load[i]) * Math.Min(1, dt * 1.6) : target[i];
                _rot[i] = (_rot[i] + dt * 360 / (3.2 - 2.9 * _load[i])) % 360;
                _edge[i] = (_edge[i] + dt * 18 / (3.0 - 2.75 * _load[i])) % 18;
                double gTarget = hot[i] ? 1 : 0;
                _glow[i] = anim ? _glow[i] + (gTarget - _glow[i]) * Math.Min(1, dt * 2.5) : gTarget;
            }
            _air = (_air + dt * 32 / (2.2 - 1.5 * _load[FanCase])) % 32;
            _trace = (_trace + dt * 9 / 0.7) % 9;
            _ringSpin = (_ringSpin + dt * 300) % 360;

            // colours from the theme
            bool light = LightTheme;
            var ink = TryFindResource("TextPrimaryBrush") as SolidColorBrush;
            var sub = TryFindResource("TextSecondaryBrush") as SolidColorBrush;
            Color inkC = ink?.Color ?? Colors.White, subC = sub?.Color ?? Colors.Gray;
            var lnPen = P(B(subC, light ? 0.75 : 0.62), 1.5);
            var ln2Pen = P(B(subC, light ? 0.45 : 0.38), 1.1);
            var faintPen = P(B(subC, light ? 0.32 : 0.27), 1.1);
            var fill = TryFindResource("CardSurfaceBrush") as Brush ?? TryFindResource("BgCardBrush") as Brush ?? Brushes.Transparent;
            var edgeBrush = B(subC, light ? 0.55 : 0.5);
            var bladeBrush = B(subC, light ? 0.95 : 0.85);
            var trackBrush = TryFindResource("BorderBrush2") as Brush ?? B(subC, 0.3);
            var shadow = B(Colors.Black, light ? 0.08 : 0.35);
            var hotPen = P(B(Hot), 1.9);
            var hotFill = B(Hot, 0.13);

            double k = Math.Min(W / SceneW, H / SceneH);
            // left-aligned: a wider control gives the callout texts more room instead of empty margins
            dc.PushTransform(new TranslateTransform(0, (H - SceneH * k) / 2));
            _textRight = W / k - 6;
            dc.PushTransform(new ScaleTransform(k, k));

            // ── the device ──
            dc.PushTransform(new MatrixTransform(model.S, 0, 0, model.S, model.Dx, model.Dy));
            DrawOps(dc, model, false);
            dc.Pop();

            // ── the callouts ──
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int row = 0; row < 4; row++)
            {
                int p = RowOrder[row];
                double y = RowY[row];
                var c = Parts[p];
                bool isHot = c.State == PartState.Active;
                var a = model.Map(p == 3 && !gpuShown ? model.Anchor[0] : model.Anchor[p]);
                double dy = Math.Abs(y - a.Y);
                double bx = Math.Min(LeadEndX - 10, Math.Max(a.X + dy * 0.7, a.X + 12));
                var lead = new StreamGeometry();
                using (var g = lead.Open())
                {
                    g.BeginFigure(a, false, false);
                    if (dy > 3) g.LineTo(new Point(bx, y), true, true);
                    g.LineTo(new Point(LeadEndX, y), true, true);
                }
                lead.Freeze();
                dc.DrawGeometry(null, isHot ? P(B(Hot), 1.2) : P(B(subC, light ? 0.5 : 0.42), 1), lead);
                dc.DrawEllipse(B(Hot), null, a, 2.2, 2.2);

                DrawRing(dc, RingX, y, 20, c, trackBrush, inkC, subC, dpi);

                var titleBrush = isHot ? B(light ? HotTextLight : HotText) : B(inkC);
                Text(dc, c.Title, TextX, y - 6, 13, titleBrush, FontWeights.SemiBold, dpi);
                Text(dc, c.Hardware, TextX, y + 10, 11.5, B(subC), FontWeights.Normal, dpi);
                if (c.Value.Length > 0)
                    Text(dc, c.Value, TextX, y + 26, 12, isHot ? B(light ? HotTextLight : HotText) : B(inkC, 0.85), FontWeights.Normal, dpi);
            }

            dc.PushTransform(new MatrixTransform(model.S, 0, 0, model.S, model.Dx, model.Dy));
            DrawOps(dc, model, true);
            dc.Pop();

            dc.Pop(); dc.Pop();

            void DrawOps(DrawingContext d, Model mdl, bool front)
            {
                foreach (var op in mdl.Ops)
                {
                    if (op.Front != front) continue;
                    if (op.DGpu && !gpuShown) continue;
                    switch (op.T)
                    {
                        case OpT.Shape:
                        {
                            if (op.S == Sty.Shadow) { d.DrawGeometry(shadow, null, op.G); break; }
                            Pen pen = op.S switch { Sty.Ln2 => ln2Pen, Sty.Faint => faintPen, _ => lnPen };
                            if (op.W > 0) pen = P(pen.Brush, op.W);
                            Brush? f = op.S == Sty.LnFill ? fill : null;
                            int part = op.Part;
                            bool blink = false;
                            if (part >= 0)
                            {
                                if (hot[part]) { pen = hotPen; f = hotFill; blink = part == 1 && anim; }
                                else if (Parts[part].State == PartState.Done && Parts[part].Mark is double mk && !(part == 0 && Parts[0].Mark == null))
                                {
                                    var mc = Mark(mk);
                                    pen = P(B(mc), 1.8); f = B(mc, 0.10);
                                }
                            }
                            if (blink)
                            {
                                double period = 0.45 + (op.Seed * 0.37 % 1) * 0.4;
                                bool dim = ((int)(t / (period / 2)) % 2) == 1;
                                if (dim) d.PushOpacity(0.35);
                                d.DrawGeometry(f, pen, op.G);
                                if (dim) d.Pop();
                            }
                            else d.DrawGeometry(f, pen, op.G);
                            break;
                        }
                        case OpT.Fan:
                        {
                            int src = op.Src, part = src == FanGpu ? 3 : 0;
                            d.DrawEllipse(null, lnPen, new Point(op.X, op.Y), op.R, op.R);
                            Brush blade = hot[part] ? B(HotText) : Parts[part].State == PartState.Done && Parts[part].Mark is double mk ? B(Mark(mk)) : bladeBrush;
                            Brush hub = hot[part] ? B(Hot) : blade;
                            d.PushTransform(new TranslateTransform(op.X, op.Y));
                            d.PushTransform(new RotateTransform(_rot[src]));
                            d.DrawGeometry(null, P(blade, 2, null, 0, true), Blades(op.R));
                            d.Pop();
                            double hr = Math.Max(3, op.R * 0.14);
                            d.DrawEllipse(hub, null, new Point(0, 0), hr, hr);
                            d.Pop();
                            break;
                        }
                        case OpT.Edge:
                            // an edge-on fan: dashes moving along the frame
                            d.DrawLine(P(edgeBrush, 5, new[] { 0.8, 1.0 }, -_edge[op.Src] / 5), new Point(op.X, op.Y), new Point(op.X2, op.Y2));
                            break;
                        case OpT.Air:
                        {
                            var c = op.Warm ? HotText : light ? ColdLight : Cold;
                            d.DrawGeometry(null, P(B(c, 0.6), 1.5, new[] { 4.0, 6.67 }, -_air / 1.5, true), op.G);
                            break;
                        }
                        case OpT.Heat:
                        {
                            if (!hot[op.Part] || !anim) break;
                            var hp = HeatWave;
                            for (int i = 0; i < 3; i++)
                            {
                                double ph = ((t + i * 0.6) / 1.8) % 1.0;
                                double o = ph < 0.3 ? ph / 0.3 * 0.9 : 0.9 * (1 - (ph - 0.3) / 0.7);
                                d.PushTransform(new TranslateTransform(op.X - 18 + i * 18, op.Y + 6 - 22 * ph));
                                d.DrawGeometry(null, P(B(HotText, o), 1.5, null, 0, true), hp);
                                d.Pop();
                            }
                            break;
                        }
                        case OpT.Glow:
                        {
                            double lvl = _glow[op.Part];
                            if (lvl < 0.01) break;
                            var g = new RadialGradientBrush(Color.FromArgb((byte)(255 * 0.7 * lvl * (light ? 0.6 : 1)), Hot.R, Hot.G, Hot.B), Color.FromArgb(0, Hot.R, Hot.G, Hot.B));
                            g.Freeze();
                            d.DrawEllipse(g, null, new Point(op.X, op.Y), op.R, op.R);
                            break;
                        }
                        case OpT.Pipe:
                            d.DrawGeometry(null, hot[op.Part] ? P(B(Hot, 0.95), 3, null, 0, true) : P(B(Copper, 0.6), 3, null, 0, true), op.G);
                            break;
                        case OpT.Trace:
                            if (hot[0] || hot[1]) d.DrawGeometry(null, P(B(Hot), 1.4, new[] { 3 / 1.4, 6 / 1.4 }, -_trace / 1.4), op.G);
                            break;
                        case OpT.Led:
                        {
                            bool off = hot[2] && anim && ((int)(t / 0.125) % 2) == 1;
                            if (!off) d.DrawEllipse(B(Led), null, new Point(op.X, op.Y), 2, 2);
                            break;
                        }
                        case OpT.Dot:
                            d.DrawEllipse(B(Power), null, new Point(op.X, op.Y), op.R, op.R);
                            break;
                    }
                }
            }
        }

        private static Geometry? _heatWave;
        private static Geometry HeatWave => _heatWave ??= Frozen(Geometry.Parse("M0 0 q4 -6 0 -12 q-4 -6 0 -12"));
        private static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

        private static readonly Dictionary<double, Geometry> _blades = new();
        /// <summary>Four curved blades around (0, 0), as in the mockup.</summary>
        private static Geometry Blades(double r)
        {
            if (_blades.TryGetValue(r, out var g)) return g;
            var sg = new StreamGeometry();
            using (var c = sg.Open())
            {
                (double cx, double cy, double ex, double ey)[] q = { (-.15, -.68, .32, -.9), (.68, -.15, .9, .32), (.15, .68, -.32, .9), (-.68, .15, -.9, -.32) };
                foreach (var (cx, cy, ex, ey) in q)
                {
                    c.BeginFigure(new Point(0, 0), false, false);
                    c.QuadraticBezierTo(new Point(cx * r, cy * r), new Point(ex * r, ey * r), true, true);
                }
            }
            sg.Freeze();
            _blades[r] = sg;
            return sg;
        }

        /// <summary>The callout ring: empty with a dash before / after the turn, a spinning arc while tested, the mark when done.</summary>
        private void DrawRing(DrawingContext dc, double cx, double cy, double r, Callout c, Brush track, Color ink, Color sub, double dpi)
        {
            dc.DrawEllipse(null, P(track, 5), new Point(cx, cy), r, r);
            if (c.State == PartState.Active)
            {
                dc.PushTransform(new RotateTransform(_ringSpin, cx, cy));
                dc.DrawGeometry(null, P(B(Hot), 5, null, 0, true), Arc(cx, cy, r, -90, -90 + 100));
                dc.Pop();
                return;
            }
            if (c.State == PartState.Done && c.Mark is double m)
            {
                double sweep = Math.Clamp(m, 0, 10) / 10 * 360;
                if (sweep > 0.5) dc.DrawGeometry(null, P(B(Mark(m)), 5, null, 0, true), Arc(cx, cy, r, -90, -90 + Math.Min(359.5, sweep)));
                var tf = new Typeface(TryFindResource("NumFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(Format(m), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, tf, 13.5, B(ink), dpi);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
                return;
            }
            dc.DrawLine(P(B(sub, 0.6), 1.5), new Point(cx - 5, cy), new Point(cx + 5, cy));
        }

        private static Geometry Arc(double cx, double cy, double r, double fromDeg, double toDeg)
        {
            double a0 = fromDeg * Math.PI / 180, a1 = toDeg * Math.PI / 180;
            var sg = new StreamGeometry();
            using (var c = sg.Open())
            {
                c.BeginFigure(new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), false, false);
                c.ArcTo(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, true);
            }
            sg.Freeze();
            return sg;
        }

        private double _textRight = SceneW - 4;

        private void Text(DrawingContext dc, string s, double x, double baseline, double size, Brush brush, FontWeight weight, double dpi)
        {
            if (string.IsNullOrEmpty(s)) return;
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, tf, size, brush, dpi)
            { MaxTextWidth = Math.Max(40, _textRight - x), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
        }
    }
}
