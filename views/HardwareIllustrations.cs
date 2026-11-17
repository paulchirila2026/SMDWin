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
            // M.2 board (80 × 22 mm): the outline has the notches at the contacts (left) and the round screw cut-out (right)
            const string NvmeOutline = "M7,25 H102 A2,2 0 0 1 104,27 V34.6 A3.4,3.4 0 0 0 104,41.4 V49 A2,2 0 0 1 102,51 H7 A1,1 0 0 1 6,50 V47.4 H10 V44 H6 V26 A1,1 0 0 1 7,25 Z";
            const string SataM2Outline = "M7,25 H102 A2,2 0 0 1 104,27 V34.6 A3.4,3.4 0 0 0 104,41.4 V49 A2,2 0 0 1 102,51 H7 A1,1 0 0 1 6,50 V47.4 H10 V44 H6 V32.4 H10 V29 H6 V26 A1,1 0 0 1 7,25 Z";
            void M2Board(string outline, string brushKey)
            {
                var board = new Path { Data = Geometry.Parse(outline) };
                board.SetResourceReference(Shape.FillProperty, brushKey); c.Children.Add(board);
                R(8, 25.6, 94, 1.4, 0.7, White85, op: 0.22);   // the reflection of the top edge
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
                case "NVMe SSD":   // M.2 2280: one notch at the contacts (M key), the opposite end straight with a round screw cut-out
                    M2Board(NvmeOutline, tint);
                    M2Fingers(skipNotches: new[] { 44.0 });
                    R(16, 29, 16, 18, 1.5, ChipBr); R(40, 29, 20, 18, 1.5, ChipBr); R(66, 29, 20, 18, 1.5, ChipBr);
                    R(43, 32, 14, 2.6, 1, White85, op: 0.35); R(69, 32, 14, 2.6, 1, White85, op: 0.35);
                    R(34.5, 31, 2.4, 4, 0.6, ChipBr); R(34.5, 41, 2.4, 4, 0.6, ChipBr); R(61, 31, 2.4, 4, 0.6, ChipBr); R(61, 41, 2.4, 4, 0.6, ChipBr);
                    M2Screw();
                    Led(92, 62);
                    break;
                case "M.2 SATA SSD":   // M.2 SATA: two notches at the contacts (B+M key), a "SATA" label, a round cut-out at the other end
                    M2Board(SataM2Outline, tint);
                    M2Fingers(skipNotches: new[] { 29.0, 44.0 });
                    R(16, 29, 14, 18, 1.5, ChipBr); R(76, 29, 14, 18, 1.5, ChipBr);
                    R(34, 30, 38, 16, 2, White85);
                    var sataTxt = new TextBlock { Text = "SATA", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = ChipBr };
                    Canvas.SetLeft(sataTxt, 42); Canvas.SetTop(sataTxt, 32); c.Children.Add(sataTxt);
                    M2Screw();
                    Led(92, 62);
                    break;
                case "HDD":   // 3.5" HDD: the case, the platter, the arm, the label in the state colour
                    R(10, 3, 90, 70, 6, key: "TextSecondaryBrush");
                    E(52, 36, 25, White85); E(52, 36, 5, key: "TextSecondaryBrush");
                    var arm = new Line { X1 = 88, Y1 = 62, X2 = 64, Y2 = 30, StrokeThickness = 4, Stroke = ChipBr, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    c.Children.Add(arm); E(88, 62, 5, ChipBr);
                    R(70, 8, 26, 8, 2, key: tint, op: healthKey == null ? 0.0 : 1);
                    Led(19, 63);
                    break;
                case "HDD 2.5":   // 2.5" HDD (laptop): a smaller metal case, the platter, the SATA connector
                    R(8, 8, 94, 60, 6, CaseBr);
                    R(9, 9, 92, 58, 5.5, CaseEdgeBr, op: 0.5);
                    R(11, 11, 88, 54, 4.5, CaseBr);
                    E(40, 38, 20, White85); E(40, 38, 4, CaseBr);
                    var arm25 = new Line { X1 = 76, Y1 = 56, X2 = 54, Y2 = 32, StrokeThickness = 3.4, Stroke = ChipBr, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    c.Children.Add(arm25); E(76, 56, 4, ChipBr);
                    R(66, 14, 28, 9, 2, key: tint);
                    R(100, 14, 6, 14, 1.5, ChipBr); R(100, 31, 6, 32, 1.5, ChipBr);
                    for (int i = 0; i < 6; i++) R(102, 16 + i * 2.1, 3, 1.1, 0.3, GoldBr);
                    for (int i = 0; i < 14; i++) R(102, 33 + i * 2.1, 3, 1.1, 0.3, GoldBr);
                    Led(18, 58);
                    break;
                case "External HDD":   // portable external drive: a thick housing with the platter visible and the USB cable
                    R(6, 10, 88, 56, 10, CaseBr);
                    R(7, 11, 86, 14, 9, CaseEdgeBr, op: 0.4);
                    E(38, 40, 17, White85); E(38, 40, 4, CaseBr);
                    R(62, 33, 26, 7, 3, key: tint);
                    R(62, 46, 18, 4, 2, White85, op: 0.5);
                    R(94, 34, 8, 8, 1, ChipBr); R(102, 31, 8, 14, 2, key: "TextSecondaryBrush");
                    Led(84, 20);
                    break;
                case "USB stick":
                case "SD card":
                    var plug = new Rectangle { Width = 26, Height = 24, RadiusX = 2, RadiusY = 2, StrokeThickness = 2.5 };
                    plug.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
                    Canvas.SetLeft(plug, 4); Canvas.SetTop(plug, 26); c.Children.Add(plug);
                    R(11, 32, 5, 4, 0, key: "TextSecondaryBrush"); R(11, 41, 5, 4, 0, key: "TextSecondaryBrush");
                    R(30, 20, 72, 36, 8, key: tint); R(44, 30, 36, 16, 3, White85); E(92, 30, 3, White85);
                    Led(92, 46);
                    break;
                case "External SSD":
                case "External drive":   // a small case + the USB cable
                    R(6, 12, 80, 52, 8, key: tint); R(18, 24, 44, 22, 3, White85);
                    Led(72, 24);
                    R(86, 34, 12, 8, 1, ChipBr); R(98, 32, 8, 12, 2, key: "TextSecondaryBrush");
                    break;
                default:           // 2.5" SATA SSD (and "SSD" / unknown): the metal case, the label, the screws, the SATA connector
                    R(6, 6, 92, 64, 6, CaseBr);
                    R(7, 7, 90, 62, 5.5, CaseEdgeBr, op: 0.55);
                    R(9, 9, 86, 58, 4.5, CaseBr);
                    R(9, 62, 86, 5, 3, CaseEdgeBr, op: 0.35);                 // the bottom edge, slightly lit
                    R(14, 10.5, 76, 1.2, 0.6, White85, op: 0.18);             // the top reflection
                    R(15, 14, 62, 48, 3.5, key: kind == "Unknown" ? "TextSecondaryBrush" : tint);
                    R(15, 14, 62, 7, 3.5, White85, op: 0.14);
                    Led(86, 56);
                    R(21, 24, 26, 4, 1, White85); R(21, 31, 42, 2.5, 1, White85, op: 0.55); R(21, 36, 34, 2.5, 1, White85, op: 0.55);
                    // an unknown type gets "?" instead of "SSD": a laptop HDD that Windows could not classify was drawn as an SSD
                    var ssdTxt = new TextBlock { Text = kind == "Unknown" ? "?" : "SSD", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = White85 };
                    Canvas.SetLeft(ssdTxt, 21); Canvas.SetTop(ssdTxt, 43); c.Children.Add(ssdTxt);
                    for (int i = 0; i < 9; i++) R(52 + i * 2.6, 46, i % 3 == 0 ? 1.8 : 1, 10, 0.2, White85, op: 0.6);   // barcode
                    foreach (var (sx, sy) in new[] { (12.5, 12.5), (91.5, 12.5), (12.5, 63.5), (91.5, 63.5) })
                    {
                        E(sx, sy, 2.2, ChipBr);
                        R(sx - 1.6, sy - 0.35, 3.2, 0.7, 0.3, CaseEdgeBr);   // the screw slot
                    }
                    R(97, 13, 7, 15, 1.5, ChipBr); R(97, 31, 7, 32, 1.5, ChipBr);       // the connector: data (7 pins) and power (15), with the key between them
                    for (int i = 0; i < 6; i++) R(99, 15 + i * 2.1, 4, 1.1, 0.3, GoldBr);
                    for (int i = 0; i < 14; i++) R(99, 33 + i * 2.1, 4, 1.1, 0.3, GoldBr);
                    break;
            }
            return c;
        }
    }
}
