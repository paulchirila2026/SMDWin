using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;
using TextBlock   = System.Windows.Controls.TextBlock;
using HAlign      = System.Windows.HorizontalAlignment;
using Rectangle   = System.Windows.Shapes.Rectangle;
using Ellipse     = System.Windows.Shapes.Ellipse;
using Path        = System.Windows.Shapes.Path;
using WColor      = System.Windows.Media.Color;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Point       = System.Windows.Point;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    /// <summary>
    /// Component illustrations in one place: the same drawings on the Storage and RAM pages and in the System Information
    /// window (one drawing, no copies). The processor, graphics card, monitors, system and memory modules are drawn elements
    /// (<see cref="ArtPicture"/>, <see cref="DimmPicture"/>); the disks are a small sheet of shapes.
    /// </summary>
    internal static class HardwareIllustrations
    {
        /// <summary>
        /// The processor: the style is chosen by processor (laptop / PC / server, Intel / AMD), unless another one is fixed in
        /// Developer › Design experiments. The manufacturer and the short model appear on the lid.
        /// </summary>
        public static ArtPicture Cpu(string cpuName, bool laptop, string? socket, bool integratedGraphics)
        {
            var auto = CpuPackage.StyleFor(cpuName, laptop, socket, integratedGraphics);
            return Cpu(CpuPackage.Resolve(SettingsService.Current.CpuPictureStyle, auto), cpuName);
        }

        public static ArtPicture Cpu(CpuPackageStyle style, string cpuName)
        {
            return new ArtPicture { Drawing = CpuArt.Build(style, CpuPackage.Brand(cpuName), CpuPackage.ShortName(cpuName)) };
        }

        /// <summary>
        /// The graphics card: a card with fans on desktops (how many fans depends on its power), the soldered chip on laptops,
        /// the marked area of the processor for integrated graphics.
        /// </summary>
        public static ArtPicture Gpu(string gpuName, bool laptop, double tdpW, double vramGb = 0)
            => new()
            {
                Drawing = GpuArt.Build(GpuPackage.StyleFor(gpuName, laptop), GpuPackage.Fans(tdpW), false, GpuPackage.Vendor(gpuName), vramGb),
            };

        /// <summary>The monitors, side by side in the given order, at their relative proportions and sizes.</summary>
        public static ArtPicture Monitors(IReadOnlyList<MonitorSpec> screens)
            => new() { Drawing = MonitorArt.Build(screens) };

        /// <summary>The system: a laptop or the motherboard of a desktop computer.</summary>
        public static ArtPicture Computer(bool laptop) => new() { Drawing = SystemArt.Build(laptop) };

        /// <summary>The computer, by its type (laptop, ultrabook, all-in-one, tower, small desktop, mini PC, tablet, server).</summary>
        public static ArtPicture Computer(ComputerKind kind) => new() { Drawing = ComputerArt.Build(kind) };

        /// <summary>The motherboard, by form factor (ATX, micro-ATX, mini-ITX, laptop).</summary>
        public static ArtPicture Board(BoardKind kind) => new() { Drawing = BoardArt.Build(kind) };

        /// <summary>Dispozitiv periferic (tastatura, mouse, camera, boxe, imprimanta, stick).</summary>
        public static ArtPicture Peripheral(PeripheralKind kind) => new() { Drawing = PeripheralArt.Build(kind) };

        /// <summary>The peripherals of a card: one type → its drawing, several → a grid with the four most visible.</summary>
        public static ArtPicture Peripheral(IReadOnlyList<PeripheralKind> kinds) => new() { Drawing = PeripheralArt.BuildSet(kinds) };

        /// <summary>Network equipment (router, Wi-Fi card, network jack).</summary>
        public static ArtPicture Network(NetworkKind kind) => new() { Drawing = NetworkArt.Build(kind) };

        /// <summary>The router with the Wi-Fi signal bars: the colour and the number of full bars show the connection quality.</summary>
        public static ArtPicture WifiRouter(int signalPct, bool connected, bool known = true) => new() { Drawing = NetworkArt.BuildWifi(signalPct, connected, known) };

        /// <summary>The memory module: a long DIMM on desktops, a short SO-DIMM on laptops.</summary>
        public static DimmPicture Dimm(string? memType, string? formFactor)
        {
            bool soDimm = DimmLayout.IsCompact(formFactor, memType);
            return new DimmPicture
            {
                MemoryType = memType ?? "", SoDimm = soDimm,
                HorizontalAlignment = soDimm ? HAlign.Left : HAlign.Stretch,
            };
        }

        /// <summary>
        /// The empty memory slot: the same space as a module (<see cref="Dimm"/>), a dotted outline and an empty groove,
        /// so it shows how many free slots the board has.
        /// </summary>
        public static Canvas EmptySlot(string? formFactor)
        {
            bool soDimm = DimmLayout.IsCompact(formFactor, null);
            double w = soDimm ? 145 : 220, h = 62;
            var c = new Canvas { Width = w, Height = h };
            var frame = new Rectangle
            {
                Width = w - 4, Height = h - 4, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.8, Opacity = 0.6,
                StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = System.Windows.Media.Brushes.Transparent,
            };
            frame.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
            Canvas.SetLeft(frame, 2); Canvas.SetTop(frame, 2); c.Children.Add(frame);
            void Bar(double x, double y, double bw, double bh, double op)
            {
                var r = new Rectangle { Width = bw, Height = bh, RadiusX = bh / 2, RadiusY = bh / 2, Opacity = op };
                r.SetResourceReference(Shape.FillProperty, "TextSecondaryBrush");
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
            }
            Bar(14, h / 2 - 3, w - 28, 6, 0.25);          // the slot groove
            Bar(w / 2 - 8, h - 14, 16, 4, 0.35);          // the middle latch
            return c;
        }

        // ══ The drive pictograms (110 × 76) ════════════════════════════════════
        private static readonly SolidColorBrush ChipBr = Frz(WColor.FromRgb(0x1B, 0x25, 0x36));
        private static readonly SolidColorBrush GoldBr = Frz(WColor.FromRgb(0xD6, 0xA9, 0x4A));
        private static readonly SolidColorBrush White85 = Frz(WColor.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush CaseBr = Frz(WColor.FromRgb(0x2E, 0x36, 0x44));      // the metal case of the SATA SSD
        private static readonly SolidColorBrush CaseEdgeBr = Frz(WColor.FromRgb(0x5A, 0x66, 0x7A));  // a slightly lit edge
        private static SolidColorBrush Frz(WColor c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // ── the materials of the drive drawings (the same in both themes, like a photo): brushed metal, the platter, the board ──
        private static LinearGradientBrush Lin(string a, string b, bool diagonal = false)
        {
            var g = new LinearGradientBrush((WColor)ColorConverter.ConvertFromString(a), (WColor)ColorConverter.ConvertFromString(b),
                                            new Point(0, 0), diagonal ? new Point(1, 1) : new Point(0, 1));
            g.Freeze(); return g;
        }
        private static readonly LinearGradientBrush MetalBr = Lin("#8D97A8", "#5F6879");
        private static readonly LinearGradientBrush InnerBr = Lin("#4A5262", "#3A414F", true);
        private static readonly LinearGradientBrush ShellBr = Lin("#3F4756", "#262C37");
        private static readonly LinearGradientBrush ArmBr   = Lin("#D8DEE7", "#8F99A8", true);
        private static readonly LinearGradientBrush PcbBr   = Lin("#21403A", "#16292A");
        private static readonly LinearGradientBrush ChipGr  = Lin("#2D333D", "#14181E", true);
        private static readonly LinearGradientBrush AluBr   = Lin("#B9C1CC", "#7D8796");
        private static readonly LinearGradientBrush Alu2Br  = Lin("#C7CED8", "#97A1AF", true);
        private static readonly RadialGradientBrush PlatterBr = MakePlatter();
        private static RadialGradientBrush MakePlatter()
        {
            var g = new RadialGradientBrush { Center = new Point(0.45, 0.40), GradientOrigin = new Point(0.45, 0.40), RadiusX = 0.62, RadiusY = 0.62 };
            g.GradientStops.Add(new GradientStop(WColor.FromRgb(0xF3, 0xF6, 0xFA), 0));
            g.GradientStops.Add(new GradientStop(WColor.FromRgb(0xCF, 0xD6, 0xE0), 0.55));
            g.GradientStops.Add(new GradientStop(WColor.FromRgb(0x9E, 0xA8, 0xB6), 1));
            g.Freeze(); return g;
        }
        private static SolidColorBrush Hex(string h) => Frz((WColor)ColorConverter.ConvertFromString(h));
        private static readonly SolidColorBrush HubBr = Hex("#C3CAD5"), HubEdgeBr = Hex("#8A94A3"), ScrewBr = Hex("#6C7686"),
            MagnetBr = Hex("#232831"), MagnetTopBr = Hex("#2F3540"), PlatEdgeBr = Hex("#2F3541"), PivotBr = Hex("#CFD5DE"),
            PortBr = Hex("#1D222B"), CableBr = Hex("#55606F"), LabelBr = Hex("#E8ECF2"), LabelInkBr = Hex("#1B2230"),
            DarkPcbBr = Hex("#1B2230"), TraceBr = Frz(WColor.FromArgb(0x1F, 0x3F, 0xD0, 0xA0)), SmdBr = Hex("#8A6D3A"),
            GreyInkBr = Hex("#8D97A8"), GreyInk2Br = Hex("#5D6878"), CaseScrewBr = Hex("#9EA7B4"), StickBodyBr = Hex("#232A36");

        /// <summary>
        /// Disk icon (110 × 76), by type: "NVMe SSD", "M.2 SATA SSD", "SATA SSD" / "SSD", "HDD", "HDD 2.5", "External HDD",
        /// "External SSD", "External drive", "USB stick", "SD card"; anything else = a generic SSD.
        /// <paramref name="health"/> lights the LED (green, yellow, red); the board / label is coloured only for "watch" and
        /// "failing"; "Unknown" = the neutral drawing. The type comes from <see cref="DriveForm.PictureKind"/>.
        /// </summary>
        public static Canvas Drive(string kind, DriveHealthState health = DriveHealthState.Unknown)
        {
            var c = new Canvas { Width = 110, Height = 76 };
            // the disk state shows in the drawing: the label / board takes the state colour and an LED appears; no data → the neutral drawing
            string? healthKey = health switch
            {
                DriveHealthState.Good => "StatusOkBrush", DriveHealthState.Watch => "StatusWarnBrush", DriveHealthState.Failing => "StatusCritBrush", _ => null,
            };
            // the board / label stays in the accent colour while the disk is healthy (only the LED is green); it is coloured only on a warning
            string tint = health is DriveHealthState.Watch or DriveHealthState.Failing ? healthKey! : "AccentBrush";
            void R(double x, double y, double w, double h, double r, Brush? fill = null, string? key = null, double op = 1)
            {
                var e = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Opacity = op };
                if (key != null) e.SetResourceReference(Shape.FillProperty, key); else e.Fill = fill;
                Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e);
            }
            void E(double cx, double cy, double rr, Brush? fill = null, string? key = null, double op = 1)
            {
                var e = new Ellipse { Width = rr * 2, Height = rr * 2, Opacity = op };
                if (key != null) e.SetResourceReference(Shape.FillProperty, key); else e.Fill = fill;
                Canvas.SetLeft(e, cx - rr); Canvas.SetTop(e, cy - rr); c.Children.Add(e);
            }
            void Led(double x, double y)
            {
                // only a warning lights the LED; a healthy disk is shown by the green text next to the picture
                if (healthKey == null || health == DriveHealthState.Good) return;
                E(x, y, 5.6, key: "BgCardBrush");
                E(x, y, 3.8, key: healthKey);
            }
            // shapes from path data (the drawings were designed as SVG, 110 × 76)
            void P(string data, Brush? fill = null, string? key = null, double op = 1)
            {
                var e = new Path { Data = Geometry.Parse(data), Opacity = op };
                if (key != null) e.SetResourceReference(Shape.FillProperty, key); else e.Fill = fill;
                c.Children.Add(e);
            }
            void S(string data, Brush stroke, double th, double op = 1)
            {
                c.Children.Add(new Path { Data = Geometry.Parse(data), Stroke = stroke, StrokeThickness = th, Opacity = op,
                                          StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            void Ring(double cx, double cy, double rr, Brush stroke, double th, double op)
            {
                var e = new Ellipse { Width = rr * 2, Height = rr * 2, Stroke = stroke, StrokeThickness = th, Opacity = op };
                Canvas.SetLeft(e, cx - rr); Canvas.SetTop(e, cy - rr); c.Children.Add(e);
            }
            // the platter: dark rim, silver disc with a few tracks and two reflections, the hub with its screws
            void Platter(double cx, double cy, double r, double hub, bool screws)
            {
                E(cx, cy, r + 1.4, PlatEdgeBr);
                E(cx, cy, r, PlatterBr);
                foreach (double k in new[] { 0.82, 0.66, 0.5 }) if (r * k > hub + 3) Ring(cx, cy, r * k, ScrewBr, 0.6, 0.22);
                // the light wedge (upper left) and a fainter one opposite
                double a1 = -128 * Math.PI / 180, a2 = -90 * Math.PI / 180;
                string Wedge(double from, double to) => FormattableString.Invariant(
                    $"M{cx},{cy} L{cx + r * Math.Cos(from):0.##},{cy + r * Math.Sin(from):0.##} A{r},{r} 0 0 1 {cx + r * Math.Cos(to):0.##},{cy + r * Math.Sin(to):0.##} Z");
                P(Wedge(a1, a2), Brushes.White, op: 0.45);
                P(Wedge(a1 + Math.PI, a2 + Math.PI), Brushes.White, op: 0.16);
                var h = new Ellipse { Width = hub * 2, Height = hub * 2, Fill = HubBr, Stroke = HubEdgeBr, StrokeThickness = 0.8 };
                Canvas.SetLeft(h, cx - hub); Canvas.SetTop(h, cy - hub); c.Children.Add(h);
                if (screws)
                    for (int i = 0; i < 6; i++)
                    {
                        double a = (i * 60 - 90) * Math.PI / 180;
                        E(cx + hub * 0.6 * Math.Cos(a), cy + hub * 0.6 * Math.Sin(a), hub * 0.125, ScrewBr);
                    }
                E(cx, cy, hub * 0.28, HubEdgeBr);
            }
            // the head arm: from the pivot to the head over the platter, the pivot cap
            void Arm(string body, double px, double py, double pr)
            {
                P(body, ArmBr);
                var piv = new Ellipse { Width = pr * 2, Height = pr * 2, Fill = PivotBr, Stroke = HubEdgeBr, StrokeThickness = 0.8 };
                Canvas.SetLeft(piv, px - pr); Canvas.SetTop(piv, py - pr); c.Children.Add(piv);
                E(px, py, pr * 0.33, ScrewBr);
            }
            void SataPins(double x, double y1, double y2)
            {
                for (int i = 0; i < 6; i++) R(x, y1 + i * 2.1, 3, 1.1, 0.3, GoldBr);
                for (int i = 0; i < 15; i++) R(x, y2 + i * 2.1, 3, 1.1, 0.3, GoldBr);
            }
            // a black chip with a faint sheen and the orientation dot
            void Chip(double x, double y, double w, double h)
            {
                R(x, y, w, h, 1.4, ChipGr);
                R(x + 2, y + 2, w - 4, 1.3, 0.5, Brushes.White, op: 0.09);
                E(x + 2.8, y + h - 2.8, 0.8, Brushes.White, op: 0.22);
            }

            // M.2 board (80 × 22 mm): the outline            // M.2 board (80 × 22 mm): the outline has the notches at the contacts (left) and the round screw cut-out (right)
            const string NvmeOutline = "M7,25 H102 A2,2 0 0 1 104,27 V34.6 A3.4,3.4 0 0 0 104,41.4 V49 A2,2 0 0 1 102,51 H7 A1,1 0 0 1 6,50 V47.4 H10 V44 H6 V26 A1,1 0 0 1 7,25 Z";
            const string SataM2Outline = "M7,25 H102 A2,2 0 0 1 104,27 V34.6 A3.4,3.4 0 0 0 104,41.4 V49 A2,2 0 0 1 102,51 H7 A1,1 0 0 1 6,50 V47.4 H10 V44 H6 V32.4 H10 V29 H6 V26 A1,1 0 0 1 7,25 Z";
            void M2Board(string outline, string brushKey)
            {
                var board = new Path { Data = Geometry.Parse(outline), Fill = PcbBr };
                c.Children.Add(board);
                R(8, 24.6, 93, 0.8, 0.4, Brushes.White, op: 0.16);   // the reflection of the top edge
                S("M16,49 H40 L43,46 H96 M16,27 H30", TraceBr, 0.5);   // fine traces
                R(56, 48, 30, 2.5, 1, key: brushKey, op: 0.95);        // the label stripe: accent, or the state colour
            }
            // the gold contact fingers along the edge; missing where the notches are (start y of each one)
            void M2Fingers(double[] skipNotches)
            {
                for (int i = 0; i < 9; i++)
                {
                    double fy = 26.6 + i * 2.55;
                    if (skipNotches.Any(n => fy + 1.5 > n - 0.6 && fy < n + 3.4 + 0.6)) continue;
                    R(6.4, fy, 8.4, 1.5, 0.4, GoldBr);
                }
            }
            // the mounting screw in the round cut-out: the metal guide and the screw head
            void M2Screw()
            {
                E(104.6, 38, 2.2, CaseEdgeBr);
                E(104.6, 38, 1.1, CaseBr);
            }
            switch (kind)
            {
                case "NVMe SSD":   // M.2 2280: the controller, a DRAM chip, two flash packages, small parts; one notch (M key), the screw
                    M2Board(NvmeOutline, tint);
                    M2Fingers(skipNotches: new[] { 44.0 });
                    Chip(17, 28, 16, 16); R(19, 30, 12, 12, 1, Brushes.Transparent);
                    Chip(37, 30, 8, 12);
                    Chip(49, 27.5, 20, 19); Chip(73, 27.5, 20, 19);
                    foreach (var (sx, sy) in new[] { (35.0, 46.0), (38.0, 46.0), (41.0, 46.0), (47.0, 28.0), (47.0, 31.0), (47.0, 44.0), (95.0, 29.0), (95.0, 32.0), (95.0, 45.0) })
                        R(sx, sy, 1.6, 0.9, 0.2, SmdBr);
                    M2Screw();
                    Led(92, 62);
                    break;
                case "M.2 SATA SSD":   // M.2 SATA: two notches (B+M key), the controller, one flash package, a "SATA" label
                    M2Board(SataM2Outline, tint);
                    M2Fingers(skipNotches: new[] { 29.0, 44.0 });
                    Chip(17, 29, 14, 14); Chip(38, 28, 22, 19);
                    R(66, 30, 28, 13, 2, LabelBr, op: 0.92);
                    var sataTxt = new TextBlock { Text = "SATA", FontSize = 6.5, FontWeight = FontWeights.Bold, Foreground = LabelInkBr };
                    Canvas.SetLeft(sataTxt, 71.5); Canvas.SetTop(sataTxt, 32.2); c.Children.Add(sataTxt);
                    M2Screw();
                    Led(92, 62);
                    break;
                case "HDD":   // 3.5" HDD, cover off: the case, the platter with the hub, the head arm, the voice-coil magnet, the flex cable
                    R(7, 2, 96, 72, 6, MetalBr);
                    var rim35 = new Rectangle { Width = 94, Height = 70, RadiusX = 5.5, RadiusY = 5.5, Stroke = CaseScrewBr, StrokeThickness = 1, Opacity = 0.35 };
                    Canvas.SetLeft(rim35, 8); Canvas.SetTop(rim35, 3); c.Children.Add(rim35);
                    P("M14,8 H96 A3,3 0 0 1 99,11 V48 L90,52 V70 H14 A3,3 0 0 1 11,67 V11 A3,3 0 0 1 14,8 Z", InnerBr);
                    Platter(44, 38, 27.5, 8, screws: true);
                    P("M84,50 L99,46 A3,3 0 0 1 102.6,48.6 L104,66 A3,3 0 0 1 101,69.2 L86,70 A3,3 0 0 1 83,67 Z", MagnetBr);
                    P("M86,52 L98,49 L99.5,64 L87,66 Z", MagnetTopBr);
                    Arm("M88,57 L60,25.5 L57.5,27.5 L84,61 Z", 87, 59, 5.4);
                    P("M55.2,25.6 L58.1,22.4 L60.6,24.6 L57.7,27.8 Z", ChipBr);   // the head
                    S("M83,64 C78,68 72,69 66,70", GoldBr, 2.2);
                    foreach (var (sx, sy) in new[] { (12.0, 6.5), (98.0, 6.5), (12.0, 69.5), (98.0, 69.5) }) E(sx, sy, 1.6, CaseScrewBr);
                    Led(18, 63);
                    break;
                case "HDD 2.5":   // 2.5" HDD (laptop), cover off: a smaller case, platter, arm, magnet, the SATA connector
                    R(5, 9, 94, 58, 5, MetalBr);
                    var rim25 = new Rectangle { Width = 92, Height = 56, RadiusX = 4.5, RadiusY = 4.5, Stroke = CaseScrewBr, StrokeThickness = 1, Opacity = 0.35 };
                    Canvas.SetLeft(rim25, 6); Canvas.SetTop(rim25, 10); c.Children.Add(rim25);
                    R(9, 13, 86, 50, 3.5, InnerBr);
                    Platter(37, 38, 21.7, 6.2, screws: true);
                    P("M73,46 L86,43 A2.5,2.5 0 0 1 89,45.2 L90,58 A2.5,2.5 0 0 1 87.4,60.6 L75,61 A2.5,2.5 0 0 1 72.5,58.5 Z", MagnetBr);
                    Arm("M77,52 L52,26 L50,27.8 L73.5,55 Z", 76, 54, 4.6);
                    P("M47.9,26.1 L50.5,23.2 L52.8,25.2 L50.2,28.1 Z", ChipBr);
                    R(99, 15, 6, 13, 1.4, PortBr); R(99, 31, 6, 30, 1.4, PortBr);
                    SataPins(101, 17, 33);
                    Led(16, 59);
                    break;
                case "External HDD":   // portable drive: the 2.5" mechanism inside a rounded shell, the USB cable
                    R(3, 6, 88, 64, 11, ShellBr);
                    var rimExt = new Rectangle { Width = 86, Height = 62, RadiusX = 10, RadiusY = 10, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.10 };
                    Canvas.SetLeft(rimExt, 4); Canvas.SetTop(rimExt, 7); c.Children.Add(rimExt);
                    R(9, 12, 76, 52, 6, InnerBr);
                    Platter(35, 38, 19.8, 5.6, screws: false);
                    P("M65,46 L76,43.5 A2.4,2.4 0 0 1 78.8,45.5 L80,57 A2.4,2.4 0 0 1 77.6,59.5 L67,60 A2.4,2.4 0 0 1 64.6,57.6 Z", MagnetBr);
                    Arm("M69,52 L48,27.5 L46.2,29 L66,55 Z", 68, 54, 4.2);
                    R(90, 33, 7, 10, 1.5, PortBr);
                    S("M97,38 C102,38 103,34 107,34", CableBr, 3);
                    Led(80, 16);
                    break;
                case "USB stick":
                case "SD card":   // the metal USB plug with its two windows, the body with a label in the accent / state colour, the lanyard hole
                    R(4, 27, 26, 22, 1.5, AluBr);
                    var plugRim = new Rectangle { Width = 24, Height = 20, RadiusX = 1, RadiusY = 1, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.3 };
                    Canvas.SetLeft(plugRim, 5); Canvas.SetTop(plugRim, 28); c.Children.Add(plugRim);
                    R(11, 33, 5, 4, 0, InnerBr); R(11, 40, 5, 4, 0, InnerBr);
                    R(30, 22, 72, 32, 7, StickBodyBr);
                    R(31, 23, 70, 8, 6, Brushes.White, op: 0.07);
                    R(44, 33, 34, 10, 3, key: tint);
                    Ring(92, 38, 3.5, GreyInk2Br, 1.6, 1);
                    Led(92, 26);
                    break;
                case "External SSD":
                case "External drive":   // a small aluminium case, a label stripe, the USB cable
                    R(8, 10, 80, 56, 12, AluBr);
                    var extRim = new Rectangle { Width = 78, Height = 54, RadiusX = 11, RadiusY = 11, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.3 };
                    Canvas.SetLeft(extRim, 9); Canvas.SetTop(extRim, 11); c.Children.Add(extRim);
                    R(14, 16, 68, 44, 8, Alu2Br);
                    var extTxt = new TextBlock { Text = kind == "External SSD" ? "SSD" : "USB", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = LabelInkBr, Opacity = 0.75 };
                    Canvas.SetLeft(extTxt, 22); Canvas.SetTop(extTxt, 27); c.Children.Add(extTxt);
                    R(22, 46, 30, 3, 1.5, key: tint);
                    R(88, 33, 5, 10, 2, PortBr);
                    S("M93,38 C99,38 100,33 106,33", CableBr, 3);
                    Led(74, 22);
                    break;
                default:           // 2.5" SATA SSD (and "SSD" / unknown): brushed aluminium, the label with a coloured band, screws, the connector
                    R(4, 5, 94, 66, 6, AluBr);
                    var ssdRim = new Rectangle { Width = 92, Height = 64, RadiusX = 5.5, RadiusY = 5.5, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.25 };
                    Canvas.SetLeft(ssdRim, 5); Canvas.SetTop(ssdRim, 6); c.Children.Add(ssdRim);
                    R(10, 11, 82, 54, 4, Alu2Br);
                    for (int y = 13; y < 65; y += 2) R(10, y, 82, 0.5, 0, Brushes.White, op: 0.05);   // the brushing
                    R(16, 17, 64, 42, 3, DarkPcbBr);
                    R(16, 17, 64, 9, 3, key: kind == "Unknown" ? "TextSecondaryBrush" : tint);
                    R(16, 23, 64, 3, 0, key: kind == "Unknown" ? "TextSecondaryBrush" : tint);
                    // an unknown type gets "?" instead of "SSD": a laptop HDD that Windows could not classify was drawn as an SSD
                    var ssdTxt = new TextBlock { Text = kind == "Unknown" ? "?" : "SSD", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = LabelBr };
                    Canvas.SetLeft(ssdTxt, 20.5); Canvas.SetTop(ssdTxt, 30); c.Children.Add(ssdTxt);
                    R(21, 49, 30, 2.2, 1, GreyInkBr); R(21, 53, 20, 2.2, 1, GreyInk2Br);
                    for (int i = 0; i < 9; i++) R(56 + i * 2.4, 44, i % 3 == 0 ? 1.6 : 0.9, 11, 0, LabelBr, op: 0.7);   // barcode
                    foreach (var (sx, sy) in new[] { (9.5, 10.5), (92.5, 10.5), (9.5, 65.5), (92.5, 65.5) }) E(sx, sy, 2, ScrewBr);
                    R(98, 12, 7, 15, 1.4, PortBr); R(98, 30, 7, 33, 1.4, PortBr);   // the connector: data (7 pins) and power (15)
                    SataPins(100, 14, 32);
                    Led(86, 59);
                    break;
            }
            return c;
        }
    }
}
