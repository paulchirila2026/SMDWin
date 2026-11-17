using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// The animation of the Benchmark banner (mockup v4, 14.11.2026) — the part being tested, drawn as the real one in a
    /// 320 × 200 scene scaled to the control:
    ///   processor: the package of its maker (Intel with side tabs, AMD with the "octopus" lid, ARM / other as a black SoC),
    ///     the engraving taken from the real model name ("intel. / CORE™ i5 / i5-4570", "AMD / RYZEN 5 / 3600"), a thermal
    ///     camera layer that warms up slowly (~20 s) and soft rising heat haze;
    ///   memory: a DIMM whose chips blink (blue = read, violet = write);
    ///   storage by type: 3.5" HDD (fast platter, the head seeks over the platter only), 2.5" SATA SSD (label, SATA data +
    ///     power connectors with pins lighting in sequence), NVMe / M.2 SATA stick (M or B+M key);
    ///   graphics: a dedicated card in its PCIe slot (fans, outputs on the bracket facing back) or, for integrated graphics,
    ///     the processor package with two tiles — CPU and GPU — the GPU tile lighting in waves.
    /// ~30 frames per second, only while <see cref="Running"/> and visible; colours follow the theme (light / dark stage).
    /// </summary>
    public sealed class BenchStage : FrameworkElement
    {
        public enum SceneKind { Cpu, Ram, Disk, Gpu }

        private SceneKind _scene = SceneKind.Cpu;
        public SceneKind Scene { get => _scene; set { if (_scene != value) { _scene = value; _sceneStart = _clock.Elapsed.TotalSeconds; } InvalidateVisual(); } }

        /// <summary>The processor name as Windows reports it (for the package and the engraving).</summary>
        public string CpuName { get; set; } = "";
        /// <summary>"hdd", "sata25", "nvme" or "m2sata".</summary>
        public string DiskKind { get; set; } = "sata25";
        public bool GpuIntegrated { get; set; }

        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _sceneStart;
        private bool _running;
        public bool Running { get => _running; set { _running = value; UpdateTimer(); InvalidateVisual(); } }

        public BenchStage()
        {
            _timer.Tick += (_, _) => InvalidateVisual();
            IsVisibleChanged += (_, _) => UpdateTimer();
            Unloaded += (_, _) => _timer.Stop();
            Loaded += (_, _) => UpdateTimer();
        }

        private void UpdateTimer()
        {
            bool animate = _running && IsVisible && SettingsService.Current?.EnableAnimations != false;
            if (animate && !_timer.IsEnabled) _timer.Start();
            else if (!animate && _timer.IsEnabled) _timer.Stop();
        }

        /// <summary>True on the Light theme (the stage is then light, the parts keep their real colours).</summary>
        internal bool LightTheme => TryFindResource("BgCardBrush") is SolidColorBrush b && (b.Color.R * 0.3 + b.Color.G * 0.59 + b.Color.B * 0.11) > 140;

        // ── small helpers ────────────────────────────────────────────────────────────────────────────────────────────
        private static SolidColorBrush B(byte r, byte g, byte b, double o = 1) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)) { Opacity = o }; x.Freeze(); return x; }
        private static SolidColorBrush B(Color c, double o) { var x = new SolidColorBrush(c) { Opacity = Math.Clamp(o, 0, 1) }; x.Freeze(); return x; }
        private static Pen P(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
        private static LinearGradientBrush Lin(Color a, Color z, double angle) { var g = new LinearGradientBrush(a, z, angle); g.Freeze(); return g; }
        private static Brush Soft(Color c, double opacity)
        {
            var g = new RadialGradientBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            g.Freeze(); return g;
        }
        private static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static readonly Brush Gold = B(0xD4, 0xAF, 0x37), Pcb = B(0x1B, 0x5A, 0x38), PcbDark = B(0x14, 0x30, 0x1F), PcbBlue = B(0x0E, 0x17, 0x26),
                                      Chip = B(0x10, 0x17, 0x22), ChipEdge = B(0x33, 0x41, 0x55), Black = B(0x11, 0x18, 0x27);
        private static readonly Brush Ihs = Lin(C(0xE3, 0xE7, 0xED), C(0x95, 0x9E, 0xAB), 45), Alu = Lin(C(0xB4, 0xBB, 0xC5), C(0x7B, 0x84, 0x92), 90),
                                      AluDark = Lin(C(0x5D, 0x65, 0x73), C(0x2F, 0x35, 0x3F), 45), Shroud = Lin(C(0x2E, 0x36, 0x44), C(0x16, 0x1B, 0x24), 90),
                                      Bracket = Lin(C(0x8E, 0x97, 0xA4), C(0xD2, 0xD7, 0xDE), 0);
        private static readonly Pen IhsPen = P(B(0x7C, 0x85, 0x91), 1), ChipPen = P(ChipEdge, 1), PcbPen = P(B(0x2B, 0x7A, 0x50), 1), EdgePen = P(B(0x3B, 0x45, 0x58), 1);

        private static Color Thermal(double h)
        {
            (double at, Color c)[] st = { (0, C(37, 99, 235)), (.35, C(6, 182, 212)), (.6, C(250, 204, 21)), (.82, C(249, 115, 22)), (1, C(220, 38, 38)) };
            h = Math.Clamp(h, 0, 1);
            for (int i = 1; i < st.Length; i++)
                if (h <= st[i].at)
                {
                    double f = (h - st[i - 1].at) / (st[i].at - st[i - 1].at);
                    var a = st[i - 1].c; var z = st[i].c;
                    return Color.FromRgb((byte)(a.R + (z.R - a.R) * f), (byte)(a.G + (z.G - a.G) * f), (byte)(a.B + (z.B - a.B) * f));
                }
            return st[^1].c;
        }

        private void Text(DrawingContext dc, string s, double x, double y, double size, Brush ink, bool bold = false, bool mono = false, bool italic = false, bool center = true)
        {
            if (string.IsNullOrEmpty(s)) return;
            var tf = new Typeface(mono ? (TryFindResource("NumFont") as FontFamily ?? new FontFamily("Consolas")) : new FontFamily("Segoe UI"),
                                  italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, tf, size, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y - ft.Baseline));
        }

        /// <summary>Soft heat haze: blurred warm blobs that rise slowly and fade.</summary>
        private static void Haze(DrawingContext dc, double t, double x0, double x1, double yBase, double level, bool blue)
        {
            if (level <= 0.01) return;
            var c = blue ? C(0x9C, 0xC3, 0xFF) : C(0xFF, 0xB2, 0x7A);
            for (int i = 0; i < 7; i++)
            {
                double ph = (t * 0.12 + i / 7.0) % 1.0;
                double x = x0 + (x1 - x0) * ((i * 0.37) % 1.0) + 6 * Math.Sin(t * 0.6 + i);
                double op = Math.Sin(ph * Math.PI) * 0.45 * level;
                dc.DrawEllipse(Soft(c, op), null, new Point(x, yBase - ph * 46), 18 + ph * 18, 12 + ph * 8);
            }
        }

        // ── render ───────────────────────────────────────────────────────────────────────────────────────────────────
        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 40 || H < 30) return;
            double k = Math.Min(W / 320, H / 200);
            dc.PushTransform(new TranslateTransform((W - 320 * k) / 2, (H - 200 * k) / 2));
            dc.PushTransform(new ScaleTransform(k, k));
            double t = _running ? _clock.Elapsed.TotalSeconds - _sceneStart : 0;
            switch (_scene)
            {
                case SceneKind.Cpu: DrawCpu(dc, t, false); break;
                case SceneKind.Ram: DrawRam(dc, t); break;
                case SceneKind.Disk: DrawDisk(dc, t); break;
                default: if (GpuIntegrated) DrawCpu(dc, t, true); else DrawGpu(dc, t); break;
            }
            dc.Pop(); dc.Pop();
        }

        private static void Scaled(DrawingContext dc, double s) { dc.PushTransform(new ScaleTransform(s, s, 160, 100)); }

        /// <summary>The engraving of the lid from the real model name.</summary>
        internal static (string logo, string line1, string line2, string vendor) Engraving(string name)
        {
            string n = Regex.Replace(name ?? "", @"\((R|TM|C)\)|®|™", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\b(CPU|Processor|\d+-Core|with Radeon.*|w/ Radeon.*|@.*)$", "", RegexOptions.IgnoreCase).Trim();
            n = Regex.Replace(n, @"\s+", " ");
            Match m;
            if ((m = Regex.Match(n, @"Core\s+Ultra\s+(\d)\s+(\w+)", RegexOptions.IgnoreCase)).Success) return ("intel.", $"CORE™ ULTRA {m.Groups[1].Value}", m.Groups[2].Value, "intel");
            if ((m = Regex.Match(n, @"Core\s+(i\d)\s*-?\s*(\w+)", RegexOptions.IgnoreCase)).Success) return ("intel.", $"CORE™ {m.Groups[1].Value.ToLowerInvariant()}", $"{m.Groups[1].Value.ToLowerInvariant()}-{m.Groups[2].Value}", "intel");
            if ((m = Regex.Match(n, @"\b(Pentium|Celeron|Xeon|Atom)\b\s*(.*)", RegexOptions.IgnoreCase)).Success) return ("intel.", m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Trim(), "intel");
            if ((m = Regex.Match(n, @"Ryzen\s+(AI\s+)?(\d)\s+(PRO\s+)?(\w+)", RegexOptions.IgnoreCase)).Success)
                return ("AMD", $"RYZEN{(m.Groups[1].Success ? " AI" : "")} {m.Groups[2].Value}", m.Groups[4].Value, "amd");
            if ((m = Regex.Match(n, @"\b(Athlon|Threadripper|EPYC|Phenom|A\d{1,2})\b\s*(.*)", RegexOptions.IgnoreCase)).Success) return ("AMD", m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Trim(), "amd");
            if ((m = Regex.Match(n, @"Snapdragon\s*(?:\w+\s+)?(X\s*(?:Elite|Plus)|\d\w*)?\s*-?\s*(\S*)", RegexOptions.IgnoreCase)).Success)
                return ("Snapdragon", m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), "arm");
            string vendor = CpuPackage.Vendor(name).ToLowerInvariant();
            var parts = n.Split(' ', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "", "", vendor == "" ? "arm" : vendor);
        }

        private void DrawCpu(DrawingContext dc, double t, bool igpu)
        {
            var (logo, l1, l2, vendor) = Engraving(CpuName);
            double heat = _running ? Smooth(t / 20.0) : 0.15;
            Haze(dc, t, 122, 198, 50, heat * (igpu ? 0.6 : 1), false);
            Scaled(dc, 0.78);
            Rect lid;
            if (vendor == "arm")
            {
                dc.DrawRoundedRectangle(B(0x15, 0x19, 0x22), P(B(0x2E, 0x35, 0x45), 1), new Rect(95, 35, 130, 130), 10, 10);
                lid = new Rect(110, 50, 100, 100);
                dc.DrawRoundedRectangle(B(0x2A, 0x31, 0x40), P(B(0x4A, 0x55, 0x68), 1), lid, 4, 4);
            }
            else
            {
                dc.DrawRoundedRectangle(vendor == "amd" ? B(0x1D, 0x4D, 0x33) : B(0x2B, 0x47, 0x2C), P(B(0x3F, 0x5F, 0x40), 1), new Rect(85, 25, 150, 150), 6, 6);
                for (int i = 0; i < 18; i++)
                {
                    dc.DrawRectangle(Gold, null, new Rect(92 + i * 7.8, 27, 3, 5)); dc.DrawRectangle(Gold, null, new Rect(92 + i * 7.8, 168, 3, 5));
                    dc.DrawRectangle(Gold, null, new Rect(87, 32 + i * 7.8, 5, 3)); dc.DrawRectangle(Gold, null, new Rect(228, 32 + i * 7.8, 5, 3));
                }
                if (vendor == "intel")
                {
                    dc.DrawRoundedRectangle(Ihs, IhsPen, new Rect(98, 70, 8, 60), 2, 2);
                    dc.DrawRoundedRectangle(Ihs, IhsPen, new Rect(214, 70, 8, 60), 2, 2);
                    lid = new Rect(103, 43, 114, 114);
                }
                else
                {
                    foreach (var r in new[] { new Rect(118, 32, 18, 14), new Rect(184, 32, 18, 14), new Rect(118, 154, 18, 14), new Rect(184, 154, 18, 14),
                                              new Rect(96, 66, 14, 18), new Rect(96, 116, 14, 18), new Rect(210, 66, 14, 18), new Rect(210, 116, 14, 18) })
                        dc.DrawRoundedRectangle(Ihs, IhsPen, r, 3, 3);
                    lid = new Rect(106, 42, 108, 116);
                }
                dc.DrawRoundedRectangle(Ihs, IhsPen, lid, 6, 6);
            }

            if (igpu)
            {
                // two tiles on the package: CPU and GPU; the GPU's units light in waves
                var cpuTile = new Rect(lid.X + 6, lid.Y + 16, lid.Width * 0.44, lid.Height - 32);
                var gpuTile = new Rect(lid.X + lid.Width * 0.54, lid.Y + 16, lid.Width * 0.40, lid.Height - 32);
                dc.DrawRoundedRectangle(B(0x2A, 0x32, 0x42), P(B(0x4A, 0x55, 0x68), 1), cpuTile, 3, 3);
                dc.DrawRoundedRectangle(B(0x1E, 0x26, 0x36), P(B(0x4A, 0x55, 0x68), 1), gpuTile, 3, 3);
                var label = B(0x9A, 0xA6, 0xBA);
                Text(dc, "CPU", cpuTile.X + cpuTile.Width / 2, cpuTile.Y + cpuTile.Height / 2 + 4, 9, label, bold: true);
                Text(dc, "GPU", gpuTile.X + gpuTile.Width / 2, gpuTile.Bottom + 11, 8, label, bold: true);
                double cw = (gpuTile.Width - 8) / 4, ch = (gpuTile.Height - 8) / 6;
                for (int r = 0; r < 6; r++)
                    for (int c = 0; c < 4; c++)
                    {
                        double w = (Math.Sin(t * 2.2 - r * 0.7 + c * 0.4) + 1) / 2 * Math.Min(1, t / 2);
                        dc.DrawRoundedRectangle(B(0x3B, 0x82, 0xF6, 0.15 + 0.75 * w), null, new Rect(gpuTile.X + 4 + c * cw, gpuTile.Y + 4 + r * ch, cw - 2, ch - 2), 1, 1);
                    }
            }
            else
            {
                var ink = vendor == "arm" ? B(0xA5, 0xAE, 0xC0) : B(0x5B, 0x64, 0x72);
                double cx = lid.X + lid.Width / 2;
                Text(dc, logo, cx, lid.Y + lid.Height * 0.42, vendor == "arm" ? 12 : 14, ink, bold: true, italic: logo == "intel.");
                Text(dc, l1, cx, lid.Y + lid.Height * 0.58, 11, ink, bold: true);
                Text(dc, l2, cx, lid.Y + lid.Height * 0.72, 8.5, ink, mono: true);
            }

            // thermal camera layer, clipped to the lid
            dc.PushClip(new RectangleGeometry(lid, 6, 6));
            double layer = igpu ? 0.15 + 0.35 * heat : 0.18 + 0.5 * heat;
            dc.PushOpacity(layer);
            dc.DrawRectangle(B(Thermal(0.1 + 0.3 * heat), 1), null, lid);
            var spots = igpu ? new[] { (lid.X + lid.Width * 0.74, lid.Y + lid.Height * 0.5, lid.Width * 0.32) }
                             : new[] { (.32, .34), (.68, .34), (.32, .66), (.68, .66) }.Select(s => (lid.X + lid.Width * s.Item1, lid.Y + lid.Height * s.Item2, lid.Width * 0.26)).ToArray();
            for (int i = 0; i < spots.Length; i++)
            {
                double v = Math.Min(1, heat * (0.92 + 0.08 * Math.Sin(t * 0.5 + i * 1.9)));
                var (x, y, r) = spots[i];
                dc.DrawEllipse(Soft(Thermal(0.3 + 0.65 * v), 1), null, new Point(x, y), r, r);
            }
            dc.Pop(); dc.Pop();
            dc.Pop();   // Scaled
        }

        private void DrawRam(DrawingContext dc, double t)
        {
            Scaled(dc, 0.92);
            dc.DrawRoundedRectangle(Pcb, PcbPen, new Rect(30, 62, 260, 72), 4, 4);
            dc.DrawRectangle(B(0x22, 0x70, 0x4A, 0.6), null, new Rect(30, 62, 260, 6));
            for (int i = 0; i < 8; i++)
            {
                var r = new Rect(40 + i * 31, 74, 25, 36);
                double a = _running ? Math.Max(0, Math.Sin(t * 6.5 + i * 2.1) * Math.Sin(t * 1.7 + i)) : 0;
                bool write = (i + (int)(t * 3)) % 3 == 0;
                var col = write ? C(0xA7, 0x8B, 0xFA) : C(0x60, 0xA5, 0xFA);
                dc.DrawRoundedRectangle(Chip, a > 0.5 ? P(B(col, 1), 1) : ChipPen, r, 2, 2);
                if (a > 0.02) dc.DrawRoundedRectangle(Soft(col, a * 0.8), null, new Rect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), 3, 3);
            }
            for (int i = 0; i < 64; i++) dc.DrawRectangle(Gold, null, new Rect(34 + i * 4, 124, 2.6, 10));
            dc.Pop();
        }

        private void DrawDisk(DrawingContext dc, double t)
        {
            switch (DiskKind)
            {
                case "hdd": DrawHdd(dc, t); break;
                case "nvme": DrawM2(dc, t, false); break;
                case "m2sata": DrawM2(dc, t, true); break;
                default: DrawSata25(dc, t); break;
            }
        }

        private void DrawHdd(DrawingContext dc, double t)
        {
            Scaled(dc, 0.86);
            dc.DrawRoundedRectangle(Alu, P(B(0x5B, 0x64, 0x72), 1), new Rect(62, 22, 196, 142), 8, 8);
            dc.DrawRoundedRectangle(B(0x9A, 0xA2, 0xAE), null, new Rect(70, 30, 180, 126), 6, 6);
            foreach (var p in new[] { new Point(73, 33), new Point(247, 33), new Point(73, 153), new Point(247, 153) }) dc.DrawEllipse(B(0x5B, 0x64, 0x72), null, p, 2.4, 2.4);
            var center = new Point(140, 93);
            var platter = new RadialGradientBrush(C(0xC9, 0xCE, 0xD6), C(0x7E, 0x87, 0x94)); platter.Freeze();
            dc.DrawEllipse(platter, P(B(0x6B, 0x72, 0x80), 1), center, 58, 58);
            // spinning sheen (7200 rpm reads as a blur) + a fixed reflection
            dc.PushTransform(new RotateTransform(_running ? (t * 2160) % 360 : 0, 140, 93));
            for (int i = 0; i < 3; i++)
            {
                double a0 = i * 2.09, a1 = a0 + 0.7;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(center, true, true);
                    c.LineTo(new Point(140 + 58 * Math.Cos(a0), 93 + 58 * Math.Sin(a0)), false, false);
                    c.ArcTo(new Point(140 + 58 * Math.Cos(a1), 93 + 58 * Math.Sin(a1)), new Size(58, 58), 0, false, SweepDirection.Clockwise, false, false);
                }
                dc.DrawGeometry(B(0xFF, 0xFF, 0xFF, 0.10), null, g);
            }
            dc.Pop();
            dc.PushTransform(new RotateTransform(-30, 122, 72));
            dc.DrawEllipse(Soft(Colors.White, 0.35), null, new Point(122, 72), 30, 12);
            dc.Pop();
            dc.DrawEllipse(B(0xB7, 0xBE, 0xC9), P(B(0x6B, 0x72, 0x80), 1), center, 13, 13);
            for (int i = 0; i < 6; i++) dc.DrawEllipse(B(0x4B, 0x55, 0x63), null, new Point(140 + 8.5 * Math.Cos(i * 1.047), 93 + 8.5 * Math.Sin(i * 1.047)), 1.5, 1.5);
            // actuator: pivot at (226,136); 0–22° keeps the head on the recording surface (34–52 from the centre, the hub is 13)
            var pivot = new Point(226, 136);
            double seek = _running ? 0.5 + 0.35 * Math.Sin(t * 5.3) + 0.15 * Math.Sin(t * 13) : 0.5;
            double angle = 22 * Math.Clamp(seek, 0, 1);   // degrees around the pivot
            dc.DrawEllipse(B(0x4B, 0x55, 0x63), P(B(0x1F, 0x29, 0x37), 1), pivot, 12, 12);
            dc.PushTransform(new RotateTransform(angle, pivot.X, pivot.Y));
            var arm = new StreamGeometry();
            using (var c = arm.Open())
            {
                c.BeginFigure(new Point(226, 136), true, true);
                c.LineTo(new Point(220, 128), true, false); c.LineTo(new Point(176, 98), true, false);
                c.LineTo(new Point(172, 102), true, false); c.LineTo(new Point(216, 140), true, false);
            }
            dc.DrawGeometry(B(0xD1, 0xD5, 0xDB), P(B(0x6B, 0x72, 0x80), 1), arm);
            dc.DrawRectangle(B(0x1F, 0x29, 0x37), null, new Rect(169, 97, 8, 5));
            dc.Pop();
            dc.Pop();
        }

        private void DrawSata25(DrawingContext dc, double t)
        {
            Scaled(dc, 0.9);
            dc.DrawRoundedRectangle(AluDark, P(B(0x2A, 0x30, 0x3A), 1), new Rect(48, 34, 200, 132), 7, 7);
            dc.DrawRoundedRectangle(null, P(B(0x6B, 0x72, 0x80, 0.35), 1), new Rect(54, 40, 188, 120), 5, 5);
            dc.DrawRoundedRectangle(B(0xF3, 0xF4, 0xF6), null, new Rect(66, 56, 150, 88), 4, 4);
            dc.DrawRoundedRectangle(B(0x1D, 0x4E, 0xD8), null, new Rect(66, 56, 150, 26), 4, 4);
            dc.DrawRectangle(B(0x1D, 0x4E, 0xD8), null, new Rect(66, 72, 150, 10));
            Text(dc, "SSD", 76, 74, 13, Brushes.White, bold: true, center: false);
            Text(dc, "2.5\" SATA", 76, 100, 12, B(0x11, 0x18, 0x27), bold: true, center: false);
            Text(dc, "6 Gb/s", 76, 115, 9, B(0x37, 0x41, 0x51), center: false);
            for (int i = 0; i < 12; i++) dc.DrawRectangle(B(0x11, 0x18, 0x27), null, new Rect(76 + i * 3, 124, i % 3 == 0 ? 2 : 1, 12));
            // connectors on the short edge: data (7 pins) and power (15 pins); the data pins light in a running sequence
            dc.DrawRoundedRectangle(Black, null, new Rect(248, 58, 11, 30), 1.5, 1.5);
            dc.DrawRoundedRectangle(Black, null, new Rect(248, 96, 11, 52), 1.5, 1.5);
            for (int i = 0; i < 15; i++) dc.DrawRectangle(Gold, null, new Rect(251, 99 + i * 3.1, 5, 1.6));
            for (int i = 0; i < 7; i++)
            {
                double ph = _running ? (t * 3.2 - i * 0.18) % 1.0 : 0;
                double lit = ph < 0 ? 0 : Math.Max(0, 1 - ph * 3);
                dc.DrawRectangle(Gold, null, new Rect(251, 63 + i * 3.4, 5, 1.8));
                if (lit > 0.02) dc.DrawEllipse(Soft(C(0x60, 0xA5, 0xFA), lit * 0.9), null, new Point(253.5, 64 + i * 3.4), 7, 3.5);
            }
            dc.Pop();
        }

        private void DrawM2(DrawingContext dc, double t, bool sata)
        {
            Scaled(dc, 0.9);
            var bg = LightTheme ? B(0xE6, 0xEC, 0xF4) : B(0x0F, 0x17, 0x26);
            dc.DrawRoundedRectangle(sata ? PcbDark : PcbBlue, P(sata ? B(0x2B, 0x7A, 0x50) : B(0x24, 0x32, 0x4A), 1), new Rect(20, 72, 280, 56), 3, 3);
            for (int i = 0; i < 20; i++) dc.DrawRectangle(Gold, null, new Rect(20, 76 + i * 2.5, 12, 1.6));
            dc.DrawRectangle(bg, null, new Rect(20, sata ? 84 : 110, 12, 5));
            if (sata) dc.DrawRectangle(bg, null, new Rect(20, 110, 12, 5));
            dc.DrawEllipse(bg, null, new Point(300, 100), 8, 8);
            dc.DrawRoundedRectangle(B(0x1F, 0x29, 0x37), P(B(0x4B, 0x55, 0x63), 1), new Rect(48, 82, 46, 36), 3, 3);
            Text(dc, sata ? "SATA" : "NVMe", 71, 104, 8, B(0x9C, 0xA3, 0xAF));
            if (!sata) dc.DrawRoundedRectangle(Black, ChipPen, new Rect(102, 86, 26, 28), 2, 2);
            double[] xs = sata ? new[] { 110.0, 190.0 } : new[] { 140.0, 212.0 };
            for (int i = 0; i < xs.Length; i++)
            {
                dc.DrawRoundedRectangle(Black, ChipPen, new Rect(xs[i], 80, 66, 40), 2, 2);
                double a = _running ? Math.Max(0, Math.Sin(t * (sata ? 4 : 7) + i * 1.8)) : 0;
                if (a > 0.02) dc.DrawRoundedRectangle(Soft(C(0x60, 0xA5, 0xFA), a * 0.55), null, new Rect(xs[i] + 3, 83, 60, 34), 2, 2);
            }
            double warm = Smooth(t / 15.0) * (sata ? 0.25 : 0.55);
            if (warm > 0.02) dc.DrawEllipse(Soft(C(0xF9, 0x73, 0x16), warm), null, new Point(71, 100), 30, 30);
            Text(dc, sata ? "M.2 2280 · SATA (B+M)" : "M.2 2280 · PCIe (M)", 270, 142, 9, LightTheme ? B(0x47, 0x55, 0x69) : B(0x8F, 0xB3, 0xE6));
            dc.Pop();
        }

        private void DrawGpu(DrawingContext dc, double t)
        {
            double warm = Smooth(t / 15.0);
            Haze(dc, t, 80, 240, 48, warm, true);
            Scaled(dc, 0.84);
            dc.DrawRoundedRectangle(B(0x0B, 0x0F, 0x14), P(B(0x2D, 0x37, 0x48), 1), new Rect(74, 153, 200, 13), 2, 2);   // PCIe x16 slot
            dc.DrawRoundedRectangle(B(0x0B, 0x0F, 0x14), P(B(0x2D, 0x37, 0x48), 1), new Rect(276, 155, 10, 9), 1, 1);      // latch
            // bracket seen edge-on; the outputs stick out to the back (left)
            dc.DrawRoundedRectangle(Bracket, P(B(0x7B, 0x84, 0x92), 1), new Rect(20, 36, 9, 132), 1.5, 1.5);
            foreach (var (y, h) in new[] { (52.0, 14.0), (72.0, 14.0), (92.0, 10.0), (108.0, 10.0) })
            {
                dc.DrawRoundedRectangle(B(0x1B, 0x21, 0x2B), P(B(0x4B, 0x55, 0x63), 1), new Rect(13, y, 8, h), 1.5, 1.5);
                dc.DrawRoundedRectangle(B(0x0A, 0x0D, 0x12), null, new Rect(14.5, y + 3, 5, h - 6), 1, 1);
            }
            dc.DrawRoundedRectangle(B(0x0E, 0x2A, 0x1A), null, new Rect(29, 52, 268, 102), 3, 3);
            for (int i = 0; i < 34; i++) dc.DrawRectangle(Gold, null, new Rect(84 + i * 5.6, 153, 3.5, 10));
            dc.DrawRoundedRectangle(Shroud, EdgePen, new Rect(29, 44, 272, 104), 10, 10);
            dc.DrawRoundedRectangle(Black, P(B(0x37, 0x41, 0x51), 1), new Rect(244, 34, 34, 11), 2, 2);
            for (int i = 0; i < 4; i++) dc.DrawRoundedRectangle(B(0x2B, 0x33, 0x42), null, new Rect(248 + i * 7.5, 36, 4, 6), 1, 1);
            dc.DrawRoundedRectangle(B(0x3B, 0x82, 0xF6, _running ? 0.6 + 0.4 * Math.Sin(t * 1.5) : 0.6), null, new Rect(40, 140, 250, 2.5), 1.2, 1.2);
            double spin = Smooth(t / 4.0);
            foreach (double cx in new[] { 108.0, 222.0 })
            {
                var c0 = new Point(cx, 96);
                dc.DrawEllipse(B(0x0A, 0x0E, 0x15), EdgePen, c0, 41, 41);
                dc.PushOpacity(1 - spin * 0.45);
                dc.PushTransform(new RotateTransform(_running ? (t * (90 + 900 * spin)) % 360 : 0, cx, 96));
                for (int i = 0; i < 11; i++)
                {
                    dc.PushTransform(new RotateTransform(i * 32.7, cx, 96));
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(c0, true, true);
                        c.QuadraticBezierTo(new Point(cx + 14, 90), new Point(cx + 9, 59), true, false);
                        c.QuadraticBezierTo(new Point(cx - 3, 63), c0, true, false);
                    }
                    dc.DrawGeometry(B(0x4A, 0x56, 0x70), null, g);
                    dc.Pop();
                }
                dc.Pop(); dc.Pop();
                if (spin > 0.02) dc.DrawEllipse(B(0x6B, 0x7A, 0x94, spin * 0.35), null, c0, 37, 37);
                dc.DrawEllipse(B(0x1F, 0x29, 0x37), P(B(0x60, 0xA5, 0xFA), 1.2), c0, 10, 10);
            }
            dc.Pop();
        }
    }
}
