using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
            /// <summary>While tested: the live number of the test (GHz, FPS, MB/s…), drawn as a small chart after the value.</summary>
            public double? Live;
        }

        public readonly Callout[] Parts = { new(), new(), new(), new() };

        /// <summary>What the sensors read now; the drawing follows it (fans by load, glow by temperature, disk LED by activity).</summary>
        public sealed class LiveData
        {
            public float? CpuTemp, CpuLoad, GpuTemp, GpuLoad, DiskActive;
        }

        /// <summary>Asked twice a second while the drawing animates.</summary>
        public Func<LiveData?>? LiveSource { get; set; }

        private int _highlight = -1, _hover = -1;
        /// <summary>The part to emphasise (the mouse is over its card); -1 for none.</summary>
        public int Highlight { get => _highlight; set { if (_highlight != value) { _highlight = value; InvalidateVisual(); } } }

        /// <summary>The mouse moved onto a part of the drawing or its callout (the index), or off it (-1).</summary>
        public event Action<int>? PartHover;

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

        /// <summary>
        /// What is fitted in this computer, so the drawing shows the real parts: memory modules (and empty slots), the M.2 drive on
        /// the board, the 2.5" / 3.5" drives in their bays and the optical drive. The drawing has room for: desktop — 2 or 4 slots,
        /// 1 M.2, 3 bays; laptop, all-in-one and mini PC — 2 slots, 1 M.2, 1 x 2.5" bay, the optical drive (not on mini PCs).
        /// </summary>
        public sealed class Hardware
        {
            public int RamModules = 2, RamSlots = 2;
            public bool Nvme = true;
            /// <summary>The drives in bays, in order: true = 3.5" hard disk, false = 2.5" (SSD or laptop disk).</summary>
            public List<bool> BayDrives = new();
            public bool Optical;
            /// <summary>Windows runs from the M.2 drive (otherwise from the first bay drive): the storage line points at it.</summary>
            public bool SystemOnNvme = true;
            public string Key => $"{RamModules}/{RamSlots}/{Nvme}/{string.Join(",", BayDrives)}/{Optical}/{SystemOnNvme}";
        }

        private Hardware _hw = new();
        public Hardware Fitted
        {
            get => _hw;
            set { var v = value ?? new Hardware(); if (v.Key != _hw.Key) { _hw = v; _model = null; InvalidateVisual(); } else _hw = v; }
        }

        /// <summary>Formats a mark in the app language (decimal comma in ro / de / fr).</summary>
        public Func<double, string> Format { get; set; } = v => v.ToString("0.0", CultureInfo.InvariantCulture);

        private const double SceneW = 760, SceneH = 420;
        private const double RingX = 474, TextX = 504, LeadEndX = 450;
        // the callout rows from the top: in the order in which the lines leave the drawing (worked out per drawing), so the
        // lines never cross each other
        private int[] _rowOrder = { 0, 1, 3, 2 };
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
        private LiveData _live = new();
        private double _liveAt = -10;
        // transitions: when each part finished (orange → its mark colour, the ring counting up, a dot along the line)
        private readonly PartState[] _prev = new PartState[4];
        private bool _prevInit;
        private readonly double[] _doneAt = { -99, -99, -99, -99 };
        private double _finaleAt = -99;
        // the live numbers of the tested part, a few per second, for the small chart in its callout
        private readonly List<double>[] _hist = { new(), new(), new(), new() };
        private double _histAt;
        // the last layout, for the mouse
        private double _k = 1, _offY;

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
        /// <summary>False = a still picture (the galleries of the Developer window).</summary>
        public bool Animate { get; set; } = true;

        private void UpdateTimer()
        {
            bool animate = Animate && IsVisible && IsLoaded && Animations;
            if (animate && !_hooked) { _last = _clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; _hooked = true; }
            else if (!animate) Unhook();
        }

        private void Unhook()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int part = PartAt(e.GetPosition(this));
            if (part != _hover) { _hover = part; PartHover?.Invoke(part); if (!_hooked) InvalidateVisual(); }
        }

        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; PartHover?.Invoke(-1); if (!_hooked) InvalidateVisual(); }
        }

        /// <summary>The part under a point: a callout row, or a part of the drawing (its outline, a little larger).</summary>
        private int PartAt(Point pt)
        {
            var p = new Point(pt.X / _k, (pt.Y - _offY) / _k);
            if (p.X >= RingX - 26)
            {
                for (int r = 0; r < 4; r++) if (Math.Abs(p.Y - RowY[r]) <= 36) return _rowOrder[r];
                return -1;
            }
            var m = Current;
            bool gpuShown = _dgpu && m.CanDGpu;
            var q = new Point((p.X - m.Dx) / m.S, (p.Y - m.Dy) / m.S);
            for (int i = m.Ops.Count - 1; i >= 0; i--)
            {
                var op = m.Ops[i];
                if (op.T != OpT.Shape || op.Part < 0 || op.G == null || (op.DGpu && !gpuShown)) continue;
                var bounds = op.G.Bounds; bounds.Inflate(6, 6);
                if (bounds.Contains(q)) return op.Part == 0 && !gpuShown && Parts[3].State == PartState.Active ? 3 : op.Part;
            }
            return -1;
        }

        private static double Ease(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);
        private static Color Lerp(Color a, Color b, double f)
        {
            f = Math.Clamp(f, 0, 1);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
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

        private enum OpT { Shape, Fan, Edge, Air, Heat, Glow, Pipe, Trace, Led, Dot, Sheen }
        private enum Sty { Ln, Ln2, LnFill, Faint, Shadow, Case }

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
            /// <summary>
            /// Optional lanes (drawing units): the line of a part first goes up / down to this height and then right, through a
            /// free gap, instead of right across other parts. NaN = straight right from the anchor.
            /// </summary>
            public readonly double[] Lane = { double.NaN, double.NaN, double.NaN, double.NaN };
            private double? _right;
            /// <summary>The right edge of the drawing in scene units: where the lines turn towards the callouts.</summary>
            public double Right
            {
                get
                {
                    if (_right is double r) return r;
                    double mx = 0;
                    foreach (var op in Ops) if (op.T == OpT.Shape && op.S != Sty.Shadow && op.G != null) mx = Math.Max(mx, op.G.Bounds.Right);
                    _right = mx * S + Dx;
                    return _right.Value;
                }
            }
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
            /// <summary>
            /// The faint outline of a blower's housing (as on a laptop fan): around the fan, opening towards the fin stack it blows
            /// through. up = the fins are above the fan, between x1 and x2, with their lower edge at y.
            /// </summary>
            public void Blower(double cx, double cy, double r, double x1, double x2, double finY)
            {
                double rl = cx - x1, rr = x2 - cx;   // the housing widens towards the outlet side (a volute)
                Path(string.Format(CultureInfo.InvariantCulture,
                    "M{0},{1} V{2} A{3},{3} 0 0 0 {4},{5} A{6},{6} 0 0 0 {7},{8} V{1}",
                    x1, finY, cy, rl, cx, cy + rl, (rl + rr) / 2, x2, cy - (rr - rl) / 2), Sty.Faint);
            }
            /// <summary>A memory slot with no module: the same size as a module, faint and without chips.</summary>
            public void EmptySlot(double x, double y, double w, double h) => Rect(x, y, w, h, 1.5, Sty.Faint);
            /// <summary>A screw / standoff: a tiny faint ring.</summary>
            public void Screw(double x, double y) => Circ(x, y, 1.7, Sty.Faint);
            /// <summary>A speaker: its box and the membranes (a ring with the centre cap).</summary>
            public void Speaker(double x, double y, double w, double h)
            {
                Rect(x, y, w, h, 6, Sty.Ln2);
                double r = Math.Min(w, h) * 0.32;
                int n = h > w * 2 ? 2 : 1;
                for (int i = 0; i < n; i++)
                {
                    double cy = n == 1 ? y + h / 2 : y + h * (i == 0 ? 0.28 : 0.72);
                    Circ(x + w / 2, cy, r, Sty.Faint); Circ(x + w / 2, cy, r * 0.32, Sty.Faint);
                }
            }
            /// <summary>A 3.5" hard disk seen from the side in its bay (platters hinted by a circle) or a 2.5" drive in a sled.</summary>
            public void BayDrive(double x, double y, double w, double h, bool hdd, int part)
            {
                if (hdd) { Rect(x, y, w, h, 2, Sty.Ln, part); Circ(x + w - h * 0.9, y + h / 2, h * 0.32, Sty.Faint); Rect(x + 6, y + h / 2 - 2, w * 0.35, 4, 1, Sty.Faint); }
                else { Rect(x, y, w, h, 2, Sty.Faint); Rect(x + 4, y + 3, w * 0.66, h - 6, 1.5, Sty.Ln, part); Rect(x + 9, y + h / 2 - 2, w * 0.3, 4, 1, Sty.Faint); }
                Rect(x - 4, y + h / 2 - 3, 4, 6, 1, Sty.Ln2);   // the SATA plug at the back
            }
            /// <summary>A 2.5" drive seen from above (laptop, all-in-one, mini PC): its label and the connector.</summary>
            public void Drive25(double x, double y, double w, double h, int part)
            {
                Rect(x, y, w, h, 3, Sty.Ln, part);
                bool tall = h > w;
                if (tall) { Rect(x + 6, y + 8, w - 12, h * 0.4, 1.5, Sty.Faint); Rect(x + w * 0.22, y + h - 6, w * 0.56, 4, 1, Sty.Ln2); }
                else { Rect(x + 8, y + 6, w * 0.4, h - 12, 1.5, Sty.Faint); Rect(x + w - 6, y + h * 0.22, 4, h * 0.56, 1, Sty.Ln2); }
                Screw(x + 4, y + 4); Screw(x + w - 4, y + 4); Screw(x + 4, y + h - 4); Screw(x + w - 4, y + h - 4);
            }
            /// <summary>An optical drive seen from above: the disc and its hub, faint.</summary>
            public void Optical(double x, double y, double w, double h)
            {
                Rect(x, y, w, h, 3);
                double r = Math.Min(w, h) * 0.38;
                Circ(x + w / 2, y + h / 2, r, Sty.Faint); Circ(x + w / 2, y + h / 2, r * 0.22, Sty.Faint);
            }
            /// <summary>A still, very faint diagonal reflection (the glass panel, the screen, the cover): gives the drawing depth.</summary>
            public void Sheen(string d) => Add(new Op { T = OpT.Sheen, G = Fz(Geometry.Parse(d)) });
            /// <summary>The memory chips on a module (vertical module: chips down its length; horizontal: along it).</summary>
            public void Chips(double x, double y, double w, double h, int n, bool vertical)
            {
                for (int i = 0; i < n; i++)
                {
                    // square chips, as the real memory chips are
                    if (vertical) { double c = w * 0.56; Rect(x + (w - c) / 2, y + 6 + i * (h - 12) / n + ((h - 12) / n - c) / 2, c, c, 0.6, Sty.Faint); }
                    else { double c = Math.Min((w - 10) / n - 4, h * 0.55); Rect(x + 5 + i * (w - 10) / n + ((w - 10) / n - 4 - c) / 2, y + (h - c) / 2, c, c, 0.8, Sty.Faint); }
                }
            }
        }

        private Model? _model;
        private Model Current => _model ??= _kind switch
        {
            Kind.Laptop   => BuildLaptop(_hw),
            Kind.AllInOne => BuildAllInOne(_hw),
            Kind.MiniPc   => BuildMini(_hw),
            _             => BuildDesktop(_hw),
        };

        // fan sources
        private const int FanCpu = 0, FanGpu = 3, FanCase = 1, FanPsu = 2;

        /// <summary>A tower seen from the side with the glass panel off: rear exhaust, CPU cooler, 2 or 4 DIMM slots, the card,
        /// the M.2 drive, PSU and the drive cage at the bottom, the optical drive at the top front.</summary>
        private static Model BuildDesktop(Hardware hw)
        {
            var b = new Builder();
            var m = b.M; m.Dx = -30; m.CanDGpu = true;
            b.Shadow(270, 414, 175, 7);
            b.Rect(120, 22, 300, 378, 12, Sty.Case, -1, 2.2); b.Rect(132, 34, 268, 354, 7, Sty.Ln2);
            b.Sheen("M176 34 H246 L166 388 H132 V246 Z");
            b.Rect(134, 400, 30, 6, 2); b.Rect(376, 400, 30, 6, 2);   // feet under the case
            b.Line(408, 34, 408, 388); b.Rect(372, 17, 22, 5, 2); b.Dot(383, 19.5, 1.6);
            b.Rect(134, 44, 12, 80, 2); b.Edge(140, 47, 140, 121, FanCase);
            b.Air("M132 60 C110 58 96 54 80 48", true); b.Air("M132 84 C108 84 94 86 76 88", true); b.Air("M132 108 C110 112 96 118 80 126", true);
            b.Rect(152, 44, 204, 252, 4, Sty.Ln2); b.Path("M162 54 h80 v12 h-66 v52 h-14 z");
            foreach (var (sx, sy) in new[] { (157, 49), (157, 291), (351, 291), (254, 291), (351, 104) }) b.Screw(sx, sy);   // (none on the top edge: the processor line runs there)
            b.Rect(196, 46, 16, 6, 1.5, Sty.Ln2);   // processor power (8-pin)
            // memory: the fitted modules, the other slots empty (two modules in a 4-slot board go in the 2nd and 4th slot)
            int slots = hw.RamSlots >= 4 ? 4 : 2;
            double[] slotX = slots == 4 ? new double[] { 270, 282, 294, 306 } : new double[] { 282, 306 };
            int[] fillOrder = slots == 4 ? new[] { 1, 3, 0, 2 } : new[] { 0, 1 };
            int mods = Math.Clamp(hw.RamModules, 1, slots);
            var filled = new bool[slots];
            for (int i = 0; i < mods; i++) filled[fillOrder[i]] = true;
            double ramRight = 0;
            for (int i = 0; i < slots; i++)
            {
                if (filled[i]) { b.Rect(slotX[i], 68, 7, 104, 1.5, Sty.Ln, 1); b.Chips(slotX[i], 68, 7, 104, 6, true); ramRight = Math.Max(ramRight, slotX[i] + 7); }
                else b.EmptySlot(slotX[i] + 1.5, 70, 4, 100);
            }
            b.Rect(338, 110, 9, 40, 1.5, Sty.Ln2);
            b.Glow(214, 128, 72, 0);
            b.Rect(178, 92, 72, 72, 6, Sty.Ln, 0); b.Fan(214, 128, 28, FanCpu);
            foreach (var (sx, sy) in new[] { (184, 98), (244, 98), (184, 158), (244, 158) }) b.Circ(sx, sy, 1.6, Sty.Faint);
            b.Heat(214, 86, 0);
            b.Trace("M250 112 L268 112"); b.Trace("M250 140 L268 140");
            b.Line(162, 200, 336, 200); b.Line(162, 246, 300, 246);
            b.DGpu = true;
            b.Glow(260, 207, 90, 3);
            b.Rect(146, 190, 228, 34, 5, Sty.Ln, 3); b.Rect(146, 186, 228, 4, 1.5, Sty.Ln2);
            b.Rect(334, 180, 18, 6, 1.5, Sty.Ln2);   // the card's power plug
            b.Line(158, 207, 250, 207);
            b.Edge(170, 228, 236, 228, FanGpu); b.Edge(262, 228, 328, 228, FanGpu);
            b.DGpu = false;
            b.Rect(290, 254, 40, 30, 4, Sty.Ln2);
            // the M.2 drive on the board (or its empty slot)
            bool sysNvme = hw.Nvme && (hw.SystemOnNvme || hw.BayDrives.Count == 0);
            // an M.2 2280 drive: about 3.6 times as long as wide, two square memory chips and the controller
            if (hw.Nvme)
            {
                b.Rect(180, 254, 68, 19, 2.5, Sty.Ln, sysNvme ? 2 : -1);
                b.Rect(186, 258.5, 10, 10, 0.8, Sty.Faint); b.Rect(200, 258.5, 10, 10, 0.8, Sty.Faint); b.Rect(215, 259.5, 8, 8, 0.8, Sty.Faint);
                b.Screw(244, 263.5); if (sysNvme) b.Led(236, 263.5);
            }
            else { b.Rect(180, 254, 68, 19, 2.5, Sty.Faint); b.Screw(244, 263.5); }
            b.Path("M132 304 H400"); b.Rect(136, 318, 128, 58, 4); b.Fins(150, 8, 13, 326, 368);
            b.Edge(154, 380, 246, 380, FanPsu); b.Rect(150, 376, 100, 8, 2, Sty.Ln2);
            b.Air("M132 334 C112 334 100 336 86 340", true); b.Air("M132 356 C112 358 100 362 86 368", true);
            // the drive cage: up to 3 drives, 3.5" disks fill a bay, 2.5" drives sit in a sled
            b.Rect(294, 310, 102, 74, 4, Sty.Faint);
            int bays = Math.Min(3, hw.BayDrives.Count);
            Point bayAnchor = new(392, 324);
            for (int i = 0; i < bays; i++)
            {
                double y = 314 + i * 23;
                bool sys = !sysNvme && i == 0;
                b.BayDrive(300, y, 90, 19, hw.BayDrives[i], sys ? 2 : -1);
                if (sys) { b.Led(hw.BayDrives[i] ? 384 : 358, y + 9.5); bayAnchor = new Point(hw.BayDrives[i] ? 390 : 363.4, y + 9.5); }
            }
            // the optical drive at the top front (5.25" bay), drawn over the board's corner
            if (hw.Optical)
            {
                b.Rect(296, 35, 106, 13, 2, Sty.LnFill);
                b.Line(304, 41.5, 380, 41.5, Sty.Faint); b.Rect(388, 39, 8, 5, 1.5, Sty.Faint);
            }
            // the front intake fans are drawn over the callout lines
            b.Front = true;
            b.Rect(390, 56, 12, 80, 2, Sty.LnFill); b.Rect(390, 150, 12, 80, 2, Sty.LnFill);
            b.Edge(396, 59, 396, 133, FanCase); b.Edge(396, 153, 396, 227, FanCase);
            b.Air("M430 84 C424 86 418 90 410 96", false); b.Air("M432 140 C424 140 418 140 410 140", false); b.Air("M430 196 C424 194 418 190 410 186", false);
            m.Anchor[0] = new Point(214, 92); m.Lane[0] = hw.Optical ? 51 : 49;   // under the optical drive, above the board's heatsink
            m.Anchor[1] = new Point(ramRight > 0 ? ramRight : 313, 120);
            m.Anchor[2] = sysNvme || bays == 0 ? new Point(248, 263.5) : bayAnchor;
            m.Anchor[3] = new Point(374, 207);
            return m;
        }

        /// <summary>A 15.6" laptop seen from below with the cover off: blowers (with their housing) and fin stacks at the rear,
        /// heat pipes along the fins, SO-DIMMs, M.2, battery, and — when fitted — the 2.5" bay and the optical drive.</summary>
        private static Model BuildLaptop(Hardware hw)
        {
            var b = new Builder();
            var m = b.M; m.S = 0.85; m.Dx = -40; m.Dy = 39; m.CanDGpu = true;
            b.Shadow(300, 372, 225, 8);
            b.Rect(90, 46, 420, 16, 6, Sty.Ln2);
            b.Rect(90, 62, 420, 292, 16, Sty.Case, -1, 2.2); b.Rect(98, 70, 404, 276, 11, Sty.Ln2);
            b.Sheen("M150 70 H226 L156 346 H98 V250 Z");
            b.Rect(204, 62, 26, 8, 2, Sty.Ln2); b.Rect(370, 62, 26, 8, 2, Sty.Ln2);   // small hinges, clear of the fin stacks
            b.Rect(114, 72, 72, 12, 2, Sty.Ln2); b.Fins(120, 8, 8, 73, 83);
            b.Air("M128 64 C124 50 120 40 114 30", true); b.Air("M150 64 C150 50 150 40 150 30", true); b.Air("M172 64 C176 50 180 40 186 30", true);
            b.DGpu = true;
            b.Rect(414, 72, 72, 12, 2, Sty.Ln2); b.Fins(420, 8, 8, 73, 83);
            b.Air("M428 64 C424 50 420 40 414 30", true); b.Air("M450 64 C450 50 450 40 450 30", true); b.Air("M472 64 C476 50 480 40 486 30", true);
            b.DGpu = false;
            b.Path("M104 92 H496 V214 H404 V232 H196 V214 H104 Z");
            // the blowers sit right under their fin stacks, inside a faint housing that opens towards the fins
            b.Blower(150, 124, 33, 114, 186, 84); b.Fan(150, 124, 30, FanCpu);
            b.DGpu = true; b.Blower(450, 124, 33, 414, 486, 84); b.Fan(450, 124, 30, FanGpu); b.DGpu = false;
            b.Glow(252, 134, 58, 0);
            b.Rect(238, 120, 30, 30, 2, Sty.Ln, 0); b.Rect(246, 128, 14, 14, 1, Sty.Ln2);
            b.Heat(253, 112, 0);
            b.DGpu = true;
            b.Glow(346, 134, 60, 3);
            b.Rect(328, 116, 36, 36, 2, Sty.Ln, 3); b.Rect(337, 125, 18, 18, 1, Sty.Ln2);
            foreach (var (x, y) in new[] { (312, 112), (312, 140), (370, 112), (370, 140) }) b.Rect(x, y, 12, 14, 1, Sty.Ln2);
            b.Heat(346, 108, 3);
            b.DGpu = false;
            // the heat pipes go round the housing and run along the fin stack (the fan blows through the fins, not onto the pipes)
            b.Pipe("M240 128 C222 124 210 77 190 77 H122", 0); b.Pipe("M240 142 C216 138 204 81 190 81 H122", 0);
            b.DGpu = true;
            b.Pipe("M364 128 C382 124 394 77 410 77 H478", 3); b.Pipe("M364 142 C388 138 400 81 410 81 H478", 3);
            b.DGpu = false;
            // two SO-DIMM slots stacked; an empty one shows only its socket
            int mods = Math.Clamp(hw.RamModules, 1, 2);
            b.Rect(206, 158, 82, 30, 2, Sty.Ln, 1); b.Chips(206, 158, 82, 30, 4, false);
            if (mods >= 2) { b.Rect(206, 192, 82, 30, 2, Sty.Ln, 1); b.Chips(206, 192, 82, 30, 4, false); }
            else b.EmptySlot(206, 192, 82, 30);
            // the coin battery that keeps the clock and the BIOS settings
            b.Circ(302, 178, 8); b.Circ(302, 178, 5, Sty.Faint);
            b.Trace("M253 150 L253 158");
            bool bay = hw.BayDrives.Count > 0;
            bool sysNvme = hw.Nvme && (hw.SystemOnNvme || !bay);
            if (hw.Nvme)
            {
                b.Rect(314, 196, 80, 24, 2.5, Sty.Ln, sysNvme ? 2 : -1);
                b.Rect(321, 202, 12, 12, 0.8, Sty.Faint); b.Rect(337, 202, 12, 12, 0.8, Sty.Faint); b.Rect(354, 203, 10, 10, 0.8, Sty.Faint);
                b.Screw(389, 208); if (sysNvme) b.Led(380, 208);
            }
            else { b.Rect(314, 196, 80, 24, 2.5, Sty.Faint); b.Screw(389, 208); }
            // the Wi-Fi card (M.2, replaceable) with its two antenna wires
            b.Rect(160, 172, 32, 36, 2); b.Rect(166, 186, 12, 12, 0.8, Sty.Faint);
            b.Circ(167, 178, 2.4, Sty.Ln2); b.Circ(184, 178, 2.4, Sty.Ln2); b.Screw(176, 204);
            b.Path("M167 178 C152 172 130 170 108 168", Sty.Faint); b.Path("M184 178 C170 186 136 186 108 184", Sty.Faint);
            // bottom row: [2.5" bay] [battery] [optical drive]
            double left = 138, right = 462;
            if (bay)
            {
                b.Drive25(108, 248, 80, 88, sysNvme ? -1 : 2);
                if (!sysNvme) b.Led(180, 256);
                left = 198;
            }
            else b.Speaker(104, 250, 26, 80);
            if (hw.Optical) { b.Optical(384, 246, 110, 92); right = 374; }
            else b.Speaker(470, 250, 26, 80);
            b.Rect(left, 246, right - left, 92, 7);
            for (int i = 1; i < 4; i++) { double x = left + (right - left) * i / 4; b.Line(x, 250, x, 334); }
            b.Rect((left + right) / 2 - 18, 232, 36, 9, 2, Sty.Ln2);
            m.Anchor[0] = new Point(253, 116); m.Lane[0] = 101;
            m.Anchor[1] = new Point(288, 173);
            if (sysNvme || !bay) m.Anchor[2] = new Point(394, 208);
            else { m.Anchor[2] = new Point(188, 262); m.Lane[2] = 243.5; }   // between the board and the battery
            m.Anchor[3] = new Point(346, 152); m.Lane[3] = 161;
            return m;
        }

        /// <summary>A 24" all-in-one from the front, the parts behind the panel drawn through it: fin stack under the top vent with
        /// the blower right under it, board, power board, and — when fitted — the 2.5" drive and the slim optical drive.</summary>
        private static Model BuildAllInOne(Hardware hw)
        {
            var b = new Builder();
            var m = b.M; m.S = 0.82; m.Dx = -26; m.Dy = 41;
            b.Shadow(300, 414, 140, 8);
            b.Path("M282 300 L268 400 H332 L318 300 Z", Sty.Ln); b.Rect(216, 398, 168, 10, 5);
            b.Rect(80, 24, 440, 300, 12, Sty.Case, -1, 2.2);
            b.Rect(90, 34, 420, 246, 3, Sty.Ln2);
            b.Sheen("M160 34 H262 L176 280 H90 V196 Z");
            b.Circ(300, 29, 2); b.Dot(490, 302, 2);
            for (int i = 0; i < 22; i++) { b.Circ(112 + i * 4, 302, 0.9); b.Circ(400 + i * 4, 302, 0.9); }
            b.Air("M314 22 C312 12 308 6 302 -2", true); b.Air("M348 22 C348 12 348 6 348 -2", true); b.Air("M382 22 C384 12 388 6 394 -2", true);
            // fin stack under the vent, the blower right under it: its air goes through the fins and out of the vent
            b.Rect(312, 37, 72, 14, 2, Sty.Ln2); b.Fins(318, 8, 9, 38, 50);
            b.Blower(348, 86, 33, 312, 384, 51); b.Fan(348, 86, 30, FanCpu);
            b.Path("M190 140 H420 V272 H190 Z");
            // the heat pipes go up past the left of the housing and run along the fin stack
            b.Pipe("M268 182 C264 112 282 42 308 42 H380", 0); b.Pipe("M282 182 C280 120 294 46 308 46 H380", 0);
            b.Glow(276, 196, 62, 0);
            b.Rect(260, 180, 32, 32, 2, Sty.Ln, 0); b.Rect(268, 188, 16, 16, 1, Sty.Ln2);
            b.Heat(276, 170, 0); b.Trace("M292 196 L314 196");
            int mods = Math.Clamp(hw.RamModules, 1, 2);
            b.Rect(318, 160, 82, 35, 2, Sty.Ln, 1); b.Chips(318, 160, 82, 35, 4, false);
            if (mods >= 2) { b.Rect(318, 200, 82, 35, 2, Sty.Ln, 1); b.Chips(318, 200, 82, 35, 4, false); }
            else b.EmptySlot(318, 200, 82, 35);
            bool bay = hw.BayDrives.Count > 0;
            bool sysNvme = hw.Nvme && (hw.SystemOnNvme || !bay);
            if (hw.Nvme)
            {
                b.Rect(212, 237, 80, 24, 2.5, Sty.Ln, sysNvme ? 2 : -1);
                b.Rect(219, 243, 12, 12, 0.8, Sty.Faint); b.Rect(235, 243, 12, 12, 0.8, Sty.Faint); b.Rect(252, 244, 10, 10, 0.8, Sty.Faint);
                b.Screw(287, 249); if (sysNvme) b.Led(278, 249);
            }
            else { b.Rect(212, 237, 80, 24, 2.5, Sty.Faint); b.Screw(287, 249); }
            b.Rect(318, 244, 26, 22, 2, Sty.Ln2);
            // the power board: transformer (with its windings), two large capacitors, a heatsink, the mains and output plugs
            b.Rect(104, 140, 72, 128, 4);
            b.Rect(114, 150, 30, 28, 2, Sty.Ln2); foreach (var y in new[] { 157.0, 164, 171 }) b.Line(117, y, 141, y, Sty.Faint);
            b.Circ(159, 156, 7); b.Circ(159, 156, 3, Sty.Faint); b.Circ(159, 176, 7); b.Circ(159, 176, 3, Sty.Faint);
            b.Rect(114, 192, 52, 22, 2, Sty.Ln2); b.Fins(119, 6, 8, 194, 212);
            b.Rect(114, 240, 18, 16, 2, Sty.Ln2); b.Rect(148, 242, 18, 12, 2, Sty.Ln2);
            b.Screw(109, 145); b.Screw(171, 145); b.Screw(109, 263); b.Screw(171, 263);
            // right column behind the panel: slim optical drive at the top, 2.5" drive under it
            if (hw.Optical) b.Optical(432, 56, 66, 66);
            if (bay) { b.Drive25(438, 150, 54, 78, sysNvme ? -1 : 2); if (!sysNvme) b.Led(465, 220); }
            b.Air("M226 336 C226 326 226 318 226 308", false); b.Air("M374 336 C374 326 374 318 374 308", false);
            m.Anchor[0] = new Point(289, 180); m.Lane[0] = 144;   // right of the heat pipes
            m.Anchor[1] = new Point(400, 178);
            m.Anchor[2] = sysNvme || !bay ? new Point(292, 249) : new Point(492, 189);
            m.Anchor[3] = m.Anchor[0];
            return m;
        }

        /// <summary>A mini PC from the front and above, lid off: fin stack at the rear with the blower right in front of it, two
        /// SO-DIMM slots, M.2, ports in front, and — when fitted — the 2.5" drive.</summary>
        private static Model BuildMini(Hardware hw)
        {
            var b = new Builder();
            var m = b.M; m.Dx = -70; m.Dy = 3;
            b.Shadow(300, 372, 160, 9);
            b.Path("M180 92 Q180 80 192 80 H408 Q420 80 420 92 V300 H180 Z", Sty.Ln, -1, 2.2);
            b.Rect(190, 90, 220, 204, 8, Sty.Ln2);
            b.Sheen("M214 90 H262 L222 294 H190 V210 Z");
            b.Path("M180 300 H420 V348 Q420 358 410 358 H190 Q180 358 180 348 Z", Sty.LnFill, -1, 2.2);
            b.Rect(204, 322, 22, 10, 2); b.Rect(234, 322, 22, 10, 2);
            b.Rect(266, 323, 14, 7, 3.5); b.Circ(294, 327, 4, Sty.Ln);
            b.Circ(394, 327, 7, Sty.Ln); b.Dot(394, 327, 2.2);
            b.Rect(200, 84, 80, 8, 2, Sty.Ln2);
            b.Air("M214 80 C210 64 206 54 200 42", true); b.Air("M240 80 C240 64 240 54 240 42", true); b.Air("M266 80 C270 64 274 54 280 42", true);
            b.Air("M140 170 C156 170 168 172 180 176", false); b.Air("M140 210 C156 210 168 208 180 204", false);
            b.Air("M460 170 C444 170 432 172 420 176", false); b.Air("M460 210 C444 210 432 208 420 204", false);
            b.Rect(204, 94, 72, 14, 2, Sty.Ln2); b.Fins(210, 8, 8, 95, 107);
            b.Blower(240, 146, 33, 204, 276, 108); b.Fan(240, 146, 30, FanCpu);
            b.Pipe("M300 168 C302 128 296 98 280 98 H212", 0); b.Pipe("M312 168 C314 124 306 102 280 102 H212", 0);
            b.Glow(306, 186, 58, 0);
            b.Rect(288, 168, 36, 36, 2, Sty.Ln, 0); b.Rect(297, 177, 18, 18, 1, Sty.Ln2);
            b.Heat(306, 160, 0); b.Trace("M300 204 L284 212");
            int mods = Math.Clamp(hw.RamModules, 1, 2);
            b.Rect(202, 212, 110, 30, 2, Sty.Ln, 1); b.Chips(202, 212, 110, 30, 4, false);
            if (mods >= 2) { b.Rect(202, 248, 110, 30, 2, Sty.Ln, 1); b.Chips(202, 248, 110, 30, 4, false); }
            else b.EmptySlot(202, 248, 110, 30);
            bool bay = hw.BayDrives.Count > 0;
            bool sysNvme = hw.Nvme && (hw.SystemOnNvme || !bay);
            double m2h = bay ? 76 : 150;
            if (hw.Nvme)
            {
                b.Rect(352, 130, 34, m2h, 2.5, Sty.Ln, sysNvme ? 2 : -1);
                b.Rect(363, 138, 12, 12, 0.8, Sty.Faint); b.Rect(363, 154, 12, 12, 0.8, Sty.Faint); b.Rect(364, 170, 10, 10, 0.8, Sty.Faint);
                b.Screw(369, 130 + m2h - 4);
                if (sysNvme) b.Led(369, 130 + m2h - 14);
            }
            else b.Rect(352, 130, 34, m2h, 2, Sty.Faint);
            if (bay) { b.Drive25(336, 214, 66, 68, sysNvme ? -1 : 2); if (!sysNvme) b.Led(394, 222); }
            m.Anchor[0] = new Point(320, 168); m.Lane[0] = 121;   // right of the heat pipes
            m.Anchor[1] = new Point(312, 263); m.Lane[1] = 287;
            m.Anchor[2] = sysNvme || !bay ? new Point(386, bay ? 180 : 210) : new Point(402, 248);
            m.Anchor[3] = m.Anchor[0];
            return m;
        }

        // ══ render ═════════════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly Color Hot = Color.FromRgb(0xFF, 0x8A, 0x3D), HotText = Color.FromRgb(0xFF, 0xB4, 0x6B), HotTextLight = Color.FromRgb(0xC2, 0x41, 0x0C),
                                      Cold = Color.FromRgb(0x6F, 0xA3, 0xFF), ColdLight = Color.FromRgb(0x3B, 0x6F, 0xD8), Copper = Color.FromRgb(0x8A, 0x6A, 0x52),
                                      Led = Color.FromRgb(0x3C, 0xCF, 0x7A), Power = Color.FromRgb(0x4F, 0x86, 0xFF),
                                      SoftHeat = Color.FromRgb(0xF6, 0xB9, 0x8A);

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

            // a part that has just finished (tested → done) starts its transition; the last one also starts the finale
            bool justFinished = false;
            for (int i = 0; i < 4; i++)
            {
                var st = Parts[i].State;
                if (_prevInit && st == PartState.Done && _prev[i] == PartState.Active) { _doneAt[i] = now; justFinished = true; }
                _prev[i] = st;
            }
            if (justFinished && _prevInit && Array.TrueForAll(Parts, x => x.State == PartState.Done)) _finaleAt = now + 0.5;
            _prevInit = true;
            double Blend(int i) => anim ? Math.Clamp((now - _doneAt[i]) / 0.6, 0, 1) : 1;
            double RingFill(int i) => anim ? Ease((now - _doneAt[i] - 0.3) / 0.8) : 1;
            double Finale(int i)
            {
                if (!anim) return 0;
                double tt = now - _finaleAt - Array.IndexOf(_rowOrder, i) * 0.16;
                return tt < 0 || tt > 0.5 ? 0 : Math.Sin(tt / 0.5 * Math.PI);
            }
            int emph = _highlight >= 0 ? _highlight : _hover;
            if (emph == 3 && !gpuShown) emph = 0;

            // the live readings (twice a second)
            if (anim && now - _liveAt > 0.5) { _liveAt = now; try { _live = LiveSource?.Invoke() ?? _live; } catch { } }
            double cpuTempF = _live.CpuTemp is float ctc ? Math.Clamp((ctc - 50) / 40.0, 0, 1) : 0;
            double gpuTempF = _live.GpuTemp is float gtc ? Math.Clamp((gtc - 50) / 40.0, 0, 1) : 0;
            Color GlowColor(int part) => Lerp(SoftHeat, Color.FromRgb(0xFF, 0x7A, 0x5C), part == 3 && gpuShown
                ? (_live.GpuTemp is float g2 ? (g2 - 78) / 15.0 : 0) : (_live.CpuTemp is float c2 ? (c2 - 78) / 15.0 : 0));

            // fan loads follow the test and the real load, smoothly (the fan spins up, it does not jump)
            double liveCpu = (_live.CpuLoad ?? 0) / 100.0, liveGpu = gpuShown ? (_live.GpuLoad ?? 0) / 100.0 : 0;
            double cpuL = Math.Max(hot[0] ? 1 : hot[1] ? 0.7 : hot[2] ? 0.12 : 0.02, Math.Clamp(liveCpu, 0, 1) * 0.9);
            double gpuL = Math.Max(hot[3] && gpuShown ? 1 : 0.02, Math.Clamp(liveGpu, 0, 1) * 0.9);
            double[] target = { cpuL, Math.Max(0.06, Math.Max(cpuL, gpuL) * 0.8), Math.Max(0.06, Math.Max(cpuL, gpuL) * 0.6), gpuL };
            for (int i = 0; i < 4; i++)
            {
                _load[i] = anim ? _load[i] + (target[i] - _load[i]) * Math.Min(1, dt * 1.6) : target[i];
                _rot[i] = (_rot[i] + dt * 360 * (0.06 + 2.3 * _load[i])) % 360;
                _edge[i] = (_edge[i] + dt * 18 * (0.25 + 3.75 * _load[i])) % 18;
                // the glow: full while tested, otherwise a little with the temperature (60 °C faint, 90 °C clear)
                double tf = i == 3 ? gpuTempF : i == 0 ? cpuTempF : 0;
                double gTarget = hot[i] ? 0.75 + 0.25 * tf : 0.4 * tf;
                _glow[i] = anim ? _glow[i] + (gTarget - _glow[i]) * Math.Min(1, dt * 2.5) : gTarget;
            }
            _air = (_air + dt * 32 / (2.2 - 1.5 * _load[FanCase])) % 32;
            _trace = (_trace + dt * 9 / 0.7) % 9;
            _ringSpin = (_ringSpin + dt * 300) % 360;
            for (int i = 0; i < 4; i++)
                if (Parts[i].State != PartState.Active) _hist[i].Clear();
            if (anim && now - _histAt > 0.2)
            {
                _histAt = now;
                for (int i = 0; i < 4; i++)
                    if (Parts[i].State == PartState.Active && Parts[i].Live is double lv)
                    {
                        _hist[i].Add(lv);
                        if (_hist[i].Count > HistMax) _hist[i].RemoveAt(0);
                    }
            }

            // colours from the theme
            bool light = LightTheme;
            var ink = TryFindResource("TextPrimaryBrush") as SolidColorBrush;
            var sub = TryFindResource("TextSecondaryBrush") as SolidColorBrush;
            Color inkC = ink?.Color ?? Colors.White, subC = sub?.Color ?? Colors.Gray;
            var lnPen = P(B(subC, light ? 0.75 : 0.62), 1.5);
            var ln2Pen = P(B(subC, light ? 0.45 : 0.38), 0.95);
            var faintPen = P(B(subC, light ? 0.32 : 0.27), 0.8);
            var fill = TryFindResource("CardSurfaceBrush") as Brush ?? TryFindResource("BgCardBrush") as Brush ?? Brushes.Transparent;
            // the case: a soft vertical gradient (a touch lighter at the top) so it has volume, not a flat outline
            Color fillC = (fill as SolidColorBrush)?.Color ?? (light ? Colors.White : Color.FromRgb(0x15, 0x1E, 0x31));
            // the card colour is often translucent (a thin white veil on Dark): keep its transparency — mixing it as an opaque
            // colour turned the case into a white slab
            LinearGradientBrush caseFill;
            if (fillC.A >= 250)
                caseFill = new LinearGradientBrush(Lerp(fillC, inkC, light ? 0.025 : 0.06), Lerp(fillC, Colors.Black, light ? 0.02 : 0.10), 90);
            else
                caseFill = new LinearGradientBrush(Color.FromArgb((byte)Math.Min(255, fillC.A * 1.8 + 4), fillC.R, fillC.G, fillC.B),
                                                   Color.FromArgb((byte)(fillC.A * 0.5), fillC.R, fillC.G, fillC.B), 90);
            caseFill.Freeze();
            var sheen = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb((byte)(light ? 0 : 14), 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            }, 0);
            if (light) sheen = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 0x30, 0x50, 0x80), 0), new GradientStop(Color.FromArgb(10, 0x30, 0x50, 0x80), 0.5),
                new GradientStop(Color.FromArgb(0, 0x30, 0x50, 0x80), 1),
            }, 0);
            sheen.Freeze();
            var edgeBrush = B(subC, light ? 0.55 : 0.5);
            var bladeBrush = B(subC, light ? 0.95 : 0.85);
            var trackBrush = TryFindResource("BorderBrush2") as Brush ?? B(subC, 0.3);
            var shadow = B(Colors.Black, light ? 0.08 : 0.35);
            var hotPen = P(B(Hot), 1.9);
            var hotFill = B(Hot, 0.13);
            var accentC = (TryFindResource("DataBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x4F, 0x86, 0xFF);

            double k = Math.Min(W / SceneW, H / SceneH);
            // left-aligned: a wider control gives the callout texts more room instead of empty margins
            dc.PushTransform(new TranslateTransform(0, (H - SceneH * k) / 2));
            _k = k; _offY = (H - SceneH * k) / 2;
            _textRight = W / k - 6;
            // callout texts grow in scene units on a small drawing (laptop screen), so the title stays about 12–13 px on screen
            double tk = Math.Clamp(0.95 / Math.Max(0.1, k), 1, 1.6);
            dc.PushTransform(new ScaleTransform(k, k));

            // ── the device ──
            dc.PushTransform(new MatrixTransform(model.S, 0, 0, model.S, model.Dx, model.Dy));
            DrawOps(dc, model, false);
            dc.Pop();

            // ── the callouts ──
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            // each line: from the part (up / down to its lane), right to just past the drawing, then to its row. The rows follow
            // the heights of the lanes, so the lines keep their order and never cross.
            int AnchorOf(int part) => part == 3 && !gpuShown ? 0 : part;
            double LaneY(int part)
            {
                int ap = AnchorOf(part);
                double ly = double.IsNaN(model.Lane[ap]) ? model.Anchor[ap].Y : model.Lane[ap];
                return ly * model.S + model.Dy;
            }
            _rowOrder = new[] { 0, 1, 2, 3 }.OrderBy(LaneY).ThenBy(i => i == 3 ? 0.5 : i).ToArray();
            double x0 = Math.Min(model.Right + 10, LeadEndX - 34);
            for (int row = 0; row < 4; row++)
            {
                int p = _rowOrder[row];
                double y = RowY[row];
                var c = Parts[p];
                bool isHot = c.State == PartState.Active;
                int ap = AnchorOf(p);
                var a = model.Map(model.Anchor[ap]);
                if (_highlight == p || _hover == p)
                    dc.DrawRoundedRectangle(B(accentC, light ? 0.07 : 0.10), null, new Rect(RingX - 28, y - 28, _textRight - RingX + 30, 62 + 26 * (tk - 1)), 10, 10);
                double ly = LaneY(p);
                var route = new List<Point> { a };
                if (Math.Abs(ly - a.Y) > 0.5) route.Add(new Point(a.X, ly));
                if (Math.Abs(ly - y) > 3) { route.Add(new Point(x0, ly)); route.Add(new Point(LeadEndX - 6, y)); }
                route.Add(new Point(LeadEndX, y));
                var lead = new StreamGeometry();
                using (var g = lead.Open())
                {
                    g.BeginFigure(a, false, false);
                    for (int ri = 1; ri < route.Count; ri++) g.LineTo(route[ri], true, true);
                }
                lead.Freeze();
                bool emphRow = _highlight == p || _hover == p;
                dc.DrawGeometry(null, isHot ? P(B(Hot), 1.2) : emphRow ? P(B(accentC, 0.9), 1.2) : P(B(subC, light ? 0.5 : 0.42), 1), lead);
                dc.DrawEllipse(B(Hot), null, a, 2.2, 2.2);
                double total = 0;
                for (int ri = 1; ri < route.Count; ri++) total += (route[ri] - route[ri - 1]).Length;
                Point Along(double f)
                {
                    double at = Math.Clamp(f, 0, 1) * total;
                    for (int ri = 1; ri < route.Count; ri++)
                    {
                        double l = (route[ri] - route[ri - 1]).Length;
                        if (at <= l || ri == route.Count - 1) return route[ri - 1] + (route[ri] - route[ri - 1]) * (l > 0 ? Math.Min(1, at / l) : 0);
                        at -= l;
                    }
                    return route[^1];
                }
                // while tested: small dots flow from the part to its callout (the results going out)
                if (isHot && anim)
                    for (int kd = 0; kd < 3; kd++)
                    {
                        double f = (t * 0.45 + kd / 3.0) % 1.0;
                        dc.DrawEllipse(B(Hot, 0.85 * Math.Sin(f * Math.PI) + 0.1), null, Along(f), 2, 2);
                    }
                // just finished: a ring spreads from the part, in the colour of its mark
                if (c.State == PartState.Done && c.Mark is double mkR && anim)
                {
                    double rf = (now - _doneAt[p]) / 0.7;
                    if (rf >= 0 && rf < 1) dc.DrawEllipse(null, P(B(Mark(mkR), 0.6 * (1 - rf)), 1.5), a, 4 + 26 * Ease(rf), 4 + 26 * Ease(rf));
                }
                // just finished: a dot runs from the part to the ring, in the colour of the mark
                double bl = Blend(p);
                if (c.State == PartState.Done && c.Mark is double mk0 && bl > 0 && bl < 1)
                {
                    var pos = Along(Ease(bl));
                    var mc0 = Mark(mk0);
                    dc.DrawEllipse(B(mc0, 0.25), null, pos, 7, 7);
                    dc.DrawEllipse(B(mc0), null, pos, 3, 3);
                }

                DrawRing(dc, RingX, y, 20, c, trackBrush, inkC, subC, dpi, RingFill(p));

                var titleBrush = isHot ? B(light ? HotTextLight : HotText) : B(inkC);
                double tb = y - 4 - 2 * tk;
                Text(dc, c.Title, TextX, tb, 13 * tk, titleBrush, FontWeights.SemiBold, dpi);
                Text(dc, c.Hardware, TextX, tb + 16 * tk, 11.5 * tk, B(subC), FontWeights.Normal, dpi);
                if (c.Value.Length > 0)
                {
                    double vw = Text(dc, c.Value, TextX, tb + 32 * tk, 12 * tk, isHot ? B(light ? HotTextLight : HotText) : B(inkC, 0.85), FontWeights.Normal, dpi);
                    // the small live chart after the value, while tested
                    var hs = _hist[p];
                    if (isHot && hs.Count >= 3 && TextX + vw + 12 + 64 <= _textRight)
                    {
                        double mx = double.MinValue, mn = double.MaxValue;
                        foreach (var v in hs) { mx = Math.Max(mx, v); mn = Math.Min(mn, v); }
                        if (mx - mn < Math.Abs(mx) * 0.04) { double mid = (mx + mn) / 2; mx = mid + Math.Abs(mid) * 0.02 + 0.01; mn = mid - Math.Abs(mid) * 0.02 - 0.01; }
                        double sx0 = TextX + vw + 12, ch = 12 * tk, yb = tb + 32 * tk + 2;
                        var sg = new StreamGeometry();
                        using (var g = sg.Open())
                        {
                            for (int j = 0; j < hs.Count; j++)
                            {
                                var pt = new Point(sx0 + j * 64.0 / (HistMax - 1), yb - (hs[j] - mn) / (mx - mn) * ch);
                                if (j == 0) g.BeginFigure(pt, false, false); else g.LineTo(pt, true, true);
                            }
                        }
                        sg.Freeze();
                        dc.DrawGeometry(null, P(B(light ? HotTextLight : HotText, 0.9), 1.3, null, 0, true), sg);
                    }
                }
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
                            Brush? f = op.S == Sty.LnFill ? fill : op.S == Sty.Case ? caseFill : null;
                            int part = op.Part;
                            bool blink = false;
                            if (part >= 0)
                            {
                                if (hot[part]) { pen = hotPen; f = hotFill; blink = part == 1 && anim; }
                                else if (Parts[part].State == PartState.Done && Parts[part].Mark is double mk)
                                {
                                    // from orange to the mark colour; at the finale a short pulse, part after part
                                    var mc = Lerp(Hot, Mark(mk), Ease(Blend(part)));
                                    double fp = Finale(part);
                                    double settle = anim ? Math.Clamp((now - _doneAt[part] - 1.0) / 0.8, 0, 1) : 1;
                                    double tone = Math.Max(fp, 1 - settle);   // 1 = bright, 0 = the quieter tone it keeps
                                    pen = P(B(mc, 0.62 + 0.38 * tone), 1.6 + 0.2 * tone + 1.0 * fp); f = B(mc, 0.05 + 0.05 * tone + 0.22 * fp);
                                }
                                if (part == emph && !hot[part]) { pen = P(B(accentC), 2.2); f = B(accentC, light ? 0.10 : 0.14); }
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
                            Brush blade = hot[part] ? B(HotText) : Parts[part].State == PartState.Done && Parts[part].Mark is double mk ? B(Lerp(HotText, Mark(mk), Ease(Blend(part)))) : bladeBrush;
                            Brush hub = hot[part] ? B(Hot) : blade;
                            d.PushTransform(new TranslateTransform(op.X, op.Y));
                            d.PushTransform(new RotateTransform(_rot[src]));
                            d.DrawGeometry(null, P(blade, 1.3, null, 0, true), Blades(op.R));
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
                            bool tooHot = (op.Part == 0 ? _live.CpuTemp : _live.GpuTemp) is float ht && ht >= 85;
                            if (!(hot[op.Part] || tooHot) || !anim) break;
                            var hp = HeatWave;
                            for (int i = 0; i < 3; i++)
                            {
                                double ph = ((t + i * 0.6) / 1.8) % 1.0;
                                double o = ph < 0.3 ? ph / 0.3 * 0.9 : 0.9 * (1 - (ph - 0.3) / 0.7);
                                d.PushTransform(new TranslateTransform(op.X - 18 + i * 18, op.Y + 6 - 22 * ph));
                                d.DrawGeometry(null, P(B(SoftHeat, o * 0.45), 1.1, null, 0, true), hp);
                                d.Pop();
                            }
                            break;
                        }
                        case OpT.Glow:
                        {
                            double lvl = _glow[op.Part];
                            if (lvl < 0.01) break;
                            var gc = GlowColor(op.Part);
                            var g = new RadialGradientBrush(Color.FromArgb((byte)(255 * 0.32 * lvl * (light ? 0.7 : 1)), gc.R, gc.G, gc.B), Color.FromArgb(0, gc.R, gc.G, gc.B));
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
                            // tested: fast; otherwise in the rhythm of the real disk activity; idle: steady
                            double da = _live.DiskActive ?? 0;
                            double period = hot[2] ? 0.25 : da > 3 ? 0.6 - 0.45 * Math.Min(1, da / 60) : 0;
                            bool off = anim && period > 0 && ((int)(t / (period / 2)) % 2) == 1;
                            if (!off) d.DrawEllipse(B(Led), null, new Point(op.X, op.Y), 2, 2);
                            break;
                        }
                        case OpT.Dot:
                            d.DrawEllipse(B(Power), null, new Point(op.X, op.Y), op.R, op.R);
                            break;
                        case OpT.Sheen:
                            d.DrawGeometry(sheen, null, op.G);
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
                // 7 thin curved blades from the hub to the rim, as on real fans
                for (int k = 0; k < 7; k++)
                {
                    double a = k * 2 * Math.PI / 7;
                    Point At(double rr, double da) => new(Math.Cos(a + da) * rr * r, Math.Sin(a + da) * rr * r);
                    c.BeginFigure(At(0.2, 0), false, false);
                    c.QuadraticBezierTo(At(0.62, 0.18), At(0.9, 0.62), true, true);
                }
            }
            sg.Freeze();
            _blades[r] = sg;
            return sg;
        }

        /// <summary>The callout ring: empty with a dash before / after the turn, a spinning arc while tested, the mark when done.</summary>
        private void DrawRing(DrawingContext dc, double cx, double cy, double r, Callout c, Brush track, Color ink, Color sub, double dpi, double fill = 1)
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
                // just finished: the ring fills and the number counts up to the mark
                double shown = m * Math.Clamp(fill, 0, 1);
                double sweep = Math.Clamp(shown, 0, 10) / 10 * 360;
                if (sweep > 0.5) dc.DrawGeometry(null, P(B(Mark(m)), 5, null, 0, true), Arc(cx, cy, r, -90, -90 + Math.Min(359.5, sweep)));
                var tf = new Typeface(TryFindResource("NumFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(Format(shown), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, tf, 13.5, B(ink), dpi);
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
        private const int HistMax = 26;

        /// <summary>Draws one line of text (trimmed to the room left); returns its width.</summary>
        private double Text(DrawingContext dc, string s, double x, double baseline, double size, Brush brush, FontWeight weight, double dpi)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, tf, size, brush, dpi)
            { MaxTextWidth = Math.Max(40, _textRight - x), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
            return ft.WidthIncludingTrailingWhitespace;
        }
    }
}
