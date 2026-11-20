using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    /// What System Info shows while it reads the hardware (5–10 s on the first opening): the drawing of this computer
    /// (laptop, all-in-one, mini PC or tower — the same detection as the Benchmark drawing) with a magnifying glass gliding
    /// over it and showing the parts inside, then a progress bar and the list of the readings, each one ticked when it
    /// really finishes (the readings run in parallel). Nothing is drawn in the first 0.35 s, so a fast load does not flash.
    /// A reading still running after 8 s says so. The animation stops when hidden or when the animations are off (Settings).
    /// </summary>
    public sealed class SysInfoScan : FrameworkElement
    {
        private string[] _items = Array.Empty<string>();
        private bool[] _done = Array.Empty<bool>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _startAt;
        private bool _hooked;
        private DeviceSketch.Kind _kind = DeviceSketch.Kind.Laptop;

        public string Heading { get; set; } = "Reading the hardware…";
        public string StillReading { get; set; } = "still reading…";

        public DeviceSketch.Kind DeviceKind { get => _kind; set { _kind = value; InvalidateVisual(); } }

        public SysInfoScan()
        {
            IsVisibleChanged += (_, _) => UpdateHook();
            Loaded += (_, _) => { ThemeManager.ThemeChanged += OnTheme; UpdateHook(); };
            Unloaded += (_, _) => { ThemeManager.ThemeChanged -= OnTheme; Unhook(); };
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        /// <summary>A new reading starts: the list, all running.</summary>
        public void Start(string[] items)
        {
            _items = items;
            _done = new bool[items.Length];
            _startAt = _clock.Elapsed.TotalSeconds;
            UpdateHook();
            InvalidateVisual();
        }

        /// <summary>One reading finished (any thread may ask through the dispatcher).</summary>
        public void MarkDone(int i)
        {
            if (i < 0 || i >= _done.Length || _done[i]) return;
            _done[i] = true;
            InvalidateVisual();
        }

        public void MarkAllDone(int upTo)
        {
            for (int i = 0; i < Math.Min(upTo, _done.Length); i++) _done[i] = true;
            InvalidateVisual();
        }

        private static bool Animations => SettingsService.Current?.EnableAnimations != false;

        private void UpdateHook()
        {
            bool on = IsVisible && IsLoaded && Animations;
            if (on && !_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
            else if (!on) Unhook();
        }

        private void Unhook()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }

        private int _frame;
        // 30 frames per second are plenty for a lens and a spinner; the page is loading in the meantime
        private void OnFrame(object? s, EventArgs e) { if ((++_frame & 1) == 0) InvalidateVisual(); }

        protected override Size MeasureOverride(Size available) => new(420, 300);

        private static SolidColorBrush B(Color c, double o = 1) { var b = new SolidColorBrush(c) { Opacity = Math.Clamp(o, 0, 1) }; b.Freeze(); return b; }
        private static Pen P(Brush b, double w, bool round = false)
        {
            var p = new Pen(b, w) { LineJoin = PenLineJoin.Round };
            if (round) p.StartLineCap = p.EndLineCap = PenLineCap.Round;
            p.Freeze();
            return p;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double el = _clock.Elapsed.TotalSeconds - _startAt;
            if (el < 0.35 && _hooked) return;   // a fast load shows nothing at all
            double t = _hooked ? _clock.Elapsed.TotalSeconds : 0;
            // fades in over a quarter of a second
            dc.PushOpacity(_hooked ? Math.Clamp((el - 0.35) / 0.25, 0, 1) : 1);

            var ink = (TryFindResource("TextPrimaryBrush") as SolidColorBrush)?.Color ?? Colors.White;
            var sub = (TryFindResource("TextSecondaryBrush") as SolidColorBrush)?.Color ?? Colors.Gray;
            var accent = (TryFindResource("DataBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3B, 0x82, 0xF6);
            var ok = (TryFindResource("StatusOkBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x22, 0xC5, 0x5E);
            var card = TryFindResource("CardSurfaceBrush") as Brush ?? TryFindResource("BgCardBrush") as Brush ?? Brushes.Transparent;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // ── the drawing (a 200 × 120 scene, centred) ──
            double cx = W / 2;
            dc.PushTransform(new TranslateTransform(cx - 100, 0));
            var line = P(B(sub, 0.75), 1.6);
            var line2 = P(B(sub, 0.4), 0.9);
            Rect screen = DrawDevice(dc, line, line2, card);
            // the lens glides over the computer (a slow figure eight inside the "screen" area)
            double lx = screen.X + screen.Width / 2 + Math.Sin(t * 0.9) * screen.Width * 0.32;
            double ly = screen.Y + screen.Height / 2 + Math.Sin(t * 1.7) * screen.Height * 0.22;
            const double R = 19;
            var lensGeo = new EllipseGeometry(new Point(lx, ly), R, R);
            dc.PushClip(lensGeo);
            dc.DrawRectangle(card, null, screen);
            DrawInside(dc, screen, P(B(accent, 0.9), 1.1), B(accent, 0.18));
            dc.Pop();
            dc.DrawEllipse(B(accent, 0.06), P(B(ink, 0.9), 2), new Point(lx, ly), R, R);
            var handle = P(B(ink, 0.9), 4, true);
            dc.DrawLine(handle, new Point(lx + R * 0.72, ly + R * 0.72), new Point(lx + R * 1.35, ly + R * 1.35));
            var shine = new StreamGeometry();
            using (var g = shine.Open())
            {
                g.BeginFigure(new Point(lx - R * 0.62, ly - R * 0.42), false, false);
                g.ArcTo(new Point(lx - R * 0.15, ly - R * 0.74), new Size(R * 0.8, R * 0.8), 0, false, SweepDirection.Clockwise, true, true);
            }
            shine.Freeze();
            dc.DrawGeometry(null, P(B(Colors.White, 0.35), 1.5, true), shine);
            dc.Pop();

            // ── the heading, the bar and the list ──
            int n = _items.Length, done = _done.Count(x => x);
            var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var uiSemi = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var head = new FormattedText(Heading, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, uiSemi, 13, B(ink), dpi);
            var count = new FormattedText($"  {done} / {n}", CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, 13, B(sub), dpi);
            double hy = 136;
            double hx = cx - (head.Width + count.Width) / 2;
            dc.DrawText(head, new Point(hx, hy));
            dc.DrawText(count, new Point(hx + head.Width, hy));

            double bw = Math.Min(W - 32, 360), bx = cx - bw / 2, by = hy + 26;
            dc.DrawRoundedRectangle(B(sub, 0.2), null, new Rect(bx, by, bw, 4), 2, 2);
            _shown += ((n > 0 ? (double)done / n : 0) - _shown) * (_hooked ? 0.18 : 1);
            if (_shown > 0.002) dc.DrawRoundedRectangle(B(accent), null, new Rect(bx, by, bw * _shown, 4), 2, 2);

            double colW = bw / 2, ry0 = by + 16;
            for (int i = 0; i < n; i++)
            {
                double x = bx + (i % 2) * colW, y = ry0 + (i / 2) * 20;
                var c = new Point(x + 7, y + 8);
                if (_done[i])
                {
                    dc.DrawEllipse(B(ok), null, c, 6, 6);
                    var tick = new StreamGeometry();
                    using (var g = tick.Open())
                    {
                        g.BeginFigure(new Point(c.X - 2.8, c.Y + 0.2), false, false);
                        g.LineTo(new Point(c.X - 0.8, c.Y + 2.3), true, true);
                        g.LineTo(new Point(c.X + 3, c.Y - 2), true, true);
                    }
                    tick.Freeze();
                    dc.DrawGeometry(null, P(card, 1.6, true), tick);
                }
                else
                {
                    dc.DrawEllipse(null, P(B(sub, 0.3), 1.5), c, 5.5, 5.5);
                    double a0 = (t * 420 + i * 40) % 360;
                    dc.DrawGeometry(null, P(B(accent), 1.6, true), Arc(c, 5.5, a0, a0 + 250));
                }
                string label = _items[i];
                bool slow = !_done[i] && el > 8;
                var ft = new FormattedText(label, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, 12,
                    B(_done[i] ? sub : ink, _done[i] ? 0.9 : 1), dpi)
                { MaxTextWidth = colW - 22, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, new Point(x + 18, y));
                if (slow)
                {
                    var st = new FormattedText(" · " + StillReading, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, 11, B(sub, 0.8), dpi)
                    { MaxTextWidth = Math.Max(10, colW - 22 - ft.Width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(st, new Point(x + 18 + ft.Width, y + 1));
                }
            }
            dc.Pop();
        }

        private double _shown;

        private static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
        {
            double a0 = fromDeg * Math.PI / 180, a1 = toDeg * Math.PI / 180;
            var sg = new StreamGeometry();
            using (var g = sg.Open())
            {
                g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
                g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, true);
            }
            sg.Freeze();
            return sg;
        }

        /// <summary>The outline of the computer; returns the area the lens moves over (the screen, or the case).</summary>
        private Rect DrawDevice(DrawingContext dc, Pen line, Pen line2, Brush fill)
        {
            switch (_kind)
            {
                case DeviceSketch.Kind.AllInOne:
                {
                    var scr = new Rect(30, 6, 140, 86);
                    dc.DrawRoundedRectangle(fill, line, scr, 6, 6);
                    dc.DrawRectangle(null, line2, new Rect(37, 13, 126, 66));
                    var stand = Geometry.Parse("M92 92 L88 112 H112 L108 92 Z"); stand.Freeze();
                    dc.DrawGeometry(null, line, stand);
                    dc.DrawRoundedRectangle(null, line, new Rect(76, 112, 48, 4), 2, 2);
                    return new Rect(37, 13, 126, 66);
                }
                case DeviceSketch.Kind.MiniPc:
                {
                    var top = Geometry.Parse("M50 40 L70 22 H160 L140 40 Z"); top.Freeze();
                    dc.DrawGeometry(fill, line, top);
                    dc.DrawRoundedRectangle(fill, line, new Rect(50, 40, 90, 50), 4, 4);
                    var side = Geometry.Parse("M140 40 L160 22 V72 L140 90 Z"); side.Freeze();
                    dc.DrawGeometry(fill, line, side);
                    dc.DrawRoundedRectangle(null, line2, new Rect(58, 76, 10, 5), 1.5, 1.5);
                    dc.DrawRoundedRectangle(null, line2, new Rect(72, 76, 10, 5), 1.5, 1.5);
                    dc.DrawEllipse(null, line2, new Point(128, 78), 3.5, 3.5);
                    return new Rect(54, 44, 82, 42);
                }
                case DeviceSketch.Kind.Desktop:
                {
                    dc.DrawRoundedRectangle(fill, line, new Rect(64, 4, 72, 108), 6, 6);
                    dc.DrawRoundedRectangle(null, line2, new Rect(70, 10, 60, 96), 3, 3);
                    dc.DrawRoundedRectangle(null, line, new Rect(66, 112, 12, 4), 1, 1);
                    dc.DrawRoundedRectangle(null, line, new Rect(122, 112, 12, 4), 1, 1);
                    return new Rect(70, 10, 60, 96);
                }
                default:
                {
                    dc.DrawRoundedRectangle(fill, line, new Rect(35, 8, 130, 80), 6, 6);
                    dc.DrawRectangle(null, line2, new Rect(42, 15, 116, 66));
                    var bse = Geometry.Parse("M20 92 H180 L172 102 H28 Z"); bse.Freeze();
                    dc.DrawGeometry(fill, line, bse);
                    dc.DrawLine(line2, new Point(88, 92), new Point(112, 92));
                    return new Rect(42, 15, 116, 66);
                }
            }
        }

        /// <summary>The parts inside, seen through the lens: processor, memory modules, storage, a fan and the traces.</summary>
        private static void DrawInside(DrawingContext dc, Rect a, Pen p, Brush chipFill)
        {
            double X(double f) => a.X + a.Width * f;
            double Y(double f) => a.Y + a.Height * f;
            double s = Math.Min(a.Width, a.Height);
            dc.DrawRoundedRectangle(null, p, new Rect(X(0.10), Y(0.14), s * 0.28, s * 0.28), 2, 2);
            dc.DrawRectangle(chipFill, p, new Rect(X(0.10) + s * 0.06, Y(0.14) + s * 0.06, s * 0.16, s * 0.16));
            dc.DrawRectangle(null, p, new Rect(X(0.40), Y(0.10), s * 0.07, s * 0.42));
            dc.DrawRectangle(null, p, new Rect(X(0.40) + s * 0.11, Y(0.10), s * 0.07, s * 0.42));
            dc.DrawRoundedRectangle(null, p, new Rect(X(0.62), Y(0.16), a.Width * 0.30, s * 0.13), 1, 1);
            dc.DrawEllipse(null, p, new Point(X(0.22), Y(0.74)), s * 0.14, s * 0.14);
            dc.DrawRoundedRectangle(null, p, new Rect(X(0.60), Y(0.62), a.Width * 0.32, s * 0.24), 2, 2);
            var tr = P(B(((SolidColorBrush)p.Brush).Color, 0.55), 0.7);
            dc.DrawLine(tr, new Point(X(0.10) + s * 0.28, Y(0.30)), new Point(X(0.40), Y(0.30)));
            dc.DrawLine(tr, new Point(X(0.40) + s * 0.18, Y(0.24)), new Point(X(0.62), Y(0.24)));
            dc.DrawLine(tr, new Point(X(0.76), Y(0.16) + s * 0.13), new Point(X(0.76), Y(0.62)));
        }

        // ── which computer to draw: once per run of the app ──
        private static DeviceSketch.Kind? _kindCache;

        /// <summary>The kind of this computer, read once (WMI on a background thread, a few hundred ms) and kept.</summary>
        public static async Task<DeviceSketch.Kind> DetectKindAsync()
        {
            if (_kindCache is DeviceSketch.Kind k) return k;
            var kind = await Task.Run(() =>
            {
                ushort[]? chassis = null; string maker = "", model = "";
                try
                {
                    using var q = WmiHelper.Searcher("SELECT ChassisTypes FROM Win32_SystemEnclosure", TimeSpan.FromSeconds(5));
                    using var col = q.Get();
                    chassis = col.Cast<ManagementObject>().FirstOrDefault()?["ChassisTypes"] as ushort[];
                }
                catch (Exception ex) { AppLogger.Debug($"SysInfoScan chassis: {ex.Message}"); }
                try
                {
                    using var q = WmiHelper.Searcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem", TimeSpan.FromSeconds(5));
                    using var col = q.Get();
                    var o = col.Cast<ManagementObject>().FirstOrDefault();
                    maker = o?["Manufacturer"]?.ToString() ?? ""; model = o?["Model"]?.ToString() ?? "";
                }
                catch (Exception ex) { AppLogger.Debug($"SysInfoScan model: {ex.Message}"); }
                bool battery = false;
                try
                {
                    var st = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
                    battery = (st & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && st != System.Windows.Forms.BatteryChargeStatus.Unknown;
                }
                catch { }
                return DeviceSketch.KindFor(ComputerPackage.KindFor(chassis, maker, model, battery));
            });
            _kindCache = kind;
            return kind;
        }
    }
}
