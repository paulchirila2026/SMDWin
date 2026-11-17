using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>How the drawn graphics card looks.</summary>
    public enum GpuPictureStyle
    {
        DesktopCard,   // PCI Express card with fans
        LaptopChip,    // chip soldered on the laptop board, with the video memory next to it
        Integrated,    // part of the processor (area marked on the processor die)
    }

    /// <summary>Reading the name of a graphics card: manufacturer, integrated or dedicated, the short name, the driver version.</summary>
    public static class GpuPackage
    {
        /// <summary>"NVIDIA", "AMD", "Intel" or empty when the name does not say.</summary>
        public static string Vendor(string? gpuName)
        {
            string n = gpuName ?? "";
            if (Regex.IsMatch(n, @"\b(NVIDIA|GeForce|Quadro|RTX|GTX|Tesla)\b", RegexOptions.IgnoreCase)) return "NVIDIA";
            if (Regex.IsMatch(n, @"\b(AMD|Radeon|ATI|FirePro)\b", RegexOptions.IgnoreCase)) return "AMD";
            if (Regex.IsMatch(n, @"\b(Intel|Iris|UHD|Arc)\b", RegexOptions.IgnoreCase)) return "Intel";
            return "";
        }

        /// <summary>
        /// true = graphics inside the processor (uses system memory). Intel: HD / UHD / Iris and "Arc Graphics" without a model
        /// (those with a model — A770, B580, A370M — are dedicated cards). AMD: "Radeon Graphics", "Vega N Graphics",
        /// "R5 / R7 Graphics" and the three-digit + M series (680M, 780M).
        /// </summary>
        public static bool IsIntegrated(string? gpuName)
        {
            string n = Clean(gpuName);
            if (Regex.IsMatch(n, @"\bXe MAX\b", RegexOptions.IgnoreCase)) return false;    // Iris Xe MAX is a separate chip
            if (Regex.IsMatch(n, @"\b(U?HD Graphics|Iris|Intel Graphics)\b", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(n, @"\bArc\b", RegexOptions.IgnoreCase))
                return !Regex.IsMatch(n, @"\bArc\s+(Pro\s+)?[AB]\d{3}", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(n, @"\bRadeon\b", RegexOptions.IgnoreCase))
                return Regex.IsMatch(n, @"\bRadeon\s+((Vega|R[3-7])\s*\d*\s+)?Graphics\b|\bRadeon\s+\d{3}M\b", RegexOptions.IgnoreCase);
            return false;
        }

        /// <summary>true = Windows uses its fallback driver: the graphics driver is not installed.</summary>
        public static bool IsBasicDriver(string? gpuName)
            => Regex.IsMatch(gpuName ?? "", @"Microsoft Basic (Display|Render)|Standard VGA", RegexOptions.IgnoreCase);

        /// <summary>
        /// Video memory from the text ("8 GB", "8.0 GB", "4096 MB", "0,5 TB") in GB; 0 when the text does not say. Cards with
        /// more than 4 GB reported by Windows as 4096 MB (32-bit field) are not corrected here.
        /// </summary>
        public static double VramGb(string? text)
        {
            var m = Regex.Match(text ?? "", @"(\d+(?:[.,]\d+)?)\s*(TB|GB|MB)", RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) return 0;
            return m.Groups[2].Value.ToUpperInvariant() switch { "TB" => v * 1024, "MB" => v / 1024, _ => v };
        }

        public static GpuPictureStyle StyleFor(string? gpuName, bool laptop)
            => IsIntegrated(gpuName) ? GpuPictureStyle.Integrated
             : laptop ? GpuPictureStyle.LaptopChip
             : GpuPictureStyle.DesktopCard;

        /// <summary>How many fans the drawing has, by the card's power (unknown → two).</summary>
        public static int Fans(double tdpW) => tdpW <= 0 ? 2 : tdpW < 90 ? 1 : tdpW < 220 ? 2 : 3;

        private static string Clean(string? name)
            => Regex.Replace(Regex.Replace(name ?? "", @"\((R|TM|tm|r|C)\)|®|™", ""), @"\s{2,}", " ").Trim();

        /// <summary>„NVIDIA GeForce GTX 1050 Ti” → „GeForce GTX 1050 Ti”; „Intel(R) UHD Graphics 630” → „UHD Graphics 630”.</summary>
        public static string ShortName(string? gpuName)
        {
            if (string.IsNullOrWhiteSpace(gpuName)) return "";
            string s = Clean(gpuName);
            s = Regex.Replace(s, @"^(NVIDIA|AMD|ATI|Intel)\s+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+(Series|\(Microsoft Corporation.*\))$", "", RegexOptions.IgnoreCase).Trim();
            return s.Length > 0 ? s : gpuName.Trim();
        }

        /// <summary>
        /// Driver version as the manufacturer shows it. NVIDIA: the last five digits of the Windows version
        /// ("31.0.15.3623" → "536.23"). Others: the Windows version, unchanged.
        /// </summary>
        public static string DriverText(string? gpuName, string? windowsVersion)
        {
            string v = (windowsVersion ?? "").Trim();
            if (Vendor(gpuName) != "NVIDIA") return v;
            string digits = v.Replace(".", "");
            if (digits.Length < 5 || !digits.All(char.IsDigit)) return v;
            string last = digits[^5..];
            return $"{last[..3]}.{last[3..]}";
        }
    }

    /// <summary>Drawing of a graphics card (132 × 108), as a shape list.</summary>
    public static class GpuArt
    {
        public const double Width = 132, Height = 108;

        private static ArtShape R(double x, double y, double w, double h, ArtFill fill, double radius = 0, double opacity = 1)
            => new(ArtKind.Rect, x, y, w, h, fill, radius, opacity);
        private static ArtShape Disc(double cx, double cy, double d, ArtFill fill, double opacity = 1)
            => new(ArtKind.Rect, cx - d / 2, cy - d / 2, d, d, fill, d / 2, opacity);
        private static ArtShape T(double x, double y, double w, double h, string text, ArtFill fill)
            => new(ArtKind.Text, x, y, w, h, fill, Text: text);

        /// <summary>A fan: the disc, the blades (triangles around the hub) and the hub.</summary>
        private static void Fan(List<ArtShape> s, double cx, double cy, double d)
        {
            s.Add(Disc(cx, cy, d, ArtFill.DieShine, 0.55));
            s.Add(Disc(cx, cy, d - 4, ArtFill.Die));
            const int blades = 7;
            double hub = d * 0.16, tip = d * 0.44;
            for (int i = 0; i < blades; i++)
            {
                double a = i * 2 * Math.PI / blades;
                (double X, double Y) P(double angle, double r) => (cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r);
                var points = new[] { P(a, hub), P(a + 0.12, tip), P(a + 0.62, tip), P(a + 0.5, hub) };
                s.Add(new ArtShape(ArtKind.Polygon, cx - tip, cy - tip, tip * 2, tip * 2, ArtFill.DieShine, Opacity: 0.9, Points: points));
            }
            s.Add(Disc(cx, cy, d * 0.3, ArtFill.LidShade));
        }

        /// <summary>The manufacturer colour: NVIDIA green, AMD red, Intel Arc blue; unknown → the theme accent colour.</summary>
        public static ArtFill VendorFill(string? vendor) => vendor switch
        {
            "NVIDIA" => ArtFill.Ok,
            "AMD"    => ArtFill.Crit,
            "Intel"  => ArtFill.Blue,
            _        => ArtFill.Accent,
        };

        /// <summary>How many of 12 segments of the VRAM bar are full: one segment per 2 GB, 24 GB and above = full bar.</summary>
        public const int VramSegmentCount = 12;
        public static int VramSegments(double vramGb) => vramGb <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(vramGb / 2.0), 1, VramSegmentCount);

        /// <summary>VRAM bar under the card: 12 segments, the full ones show how much video memory the card has.</summary>
        private static void VramBar(List<ArtShape> s, double x, double y, double w, double h, double vramGb, ArtFill fill)
        {
            int filled = VramSegments(vramGb);
            if (filled == 0) return;
            double gap = 1.2, seg = (w - gap * (VramSegmentCount - 1)) / VramSegmentCount;
            for (int i = 0; i < VramSegmentCount; i++)
                s.Add(i < filled ? R(x + i * (seg + gap), y, seg, h, fill, 1, 0.95) : R(x + i * (seg + gap), y, seg, h, ArtFill.LidShade, 1, 0.45));
        }

        /// <param name="fans">Number of fans, 1–3 (desktop card only).</param>
        /// <param name="intel">Kept for compatibility: equivalent to <c>vendor = "Intel"</c>.</param>
        /// <param name="vendor">"NVIDIA", "AMD", "Intel" (from <see cref="GpuPackage.Vendor"/>): the card accents take the manufacturer colour.</param>
        /// <param name="vramGb">Video memory in GB; above 0 → VRAM bar under the card (not for integrated graphics, which uses system memory).</param>
        public static ArtDrawing Build(GpuPictureStyle style, int fans = 2, bool intel = false, string vendor = "", double vramGb = 0)
        {
            if (string.IsNullOrEmpty(vendor) && intel) vendor = "Intel";
            var tint = VendorFill(vendor);
            var s = new List<ArtShape>();
            switch (style)
            {
                case GpuPictureStyle.DesktopCard:
                    s.Add(R(5, 14, 6, 84, ArtFill.Lid, 1.5));                    // the metal bracket at the back of the case
                    s.Add(R(6.5, 30, 3, 16, ArtFill.LidShade, 1));
                    s.Add(R(6.5, 52, 3, 16, ArtFill.LidShade, 1));
                    s.Add(R(11, 30, 116, 56, ArtFill.SubstrateEdge, 3));         // the board
                    s.Add(R(12, 31, 114, 54, ArtFill.Substrate, 2.5));
                    s.Add(R(28, 85, 66, 8, ArtFill.Gold, 1));                    // the PCI Express contacts
                    s.Add(R(41, 85, 3, 8, ArtFill.Card));
                    s.Add(R(15, 24, 110, 56, ArtFill.Die, 7));                   // the cooler housing
                    s.Add(R(100, 19, 18, 6, ArtFill.LidShade, 1.5));             // the power connector
                    int n = Math.Clamp(fans, 1, 3);
                    double d = n == 3 ? 32 : n == 2 ? 44 : 46;
                    double[] centers = n == 3 ? new[] { 34.0, 70, 106 } : n == 2 ? new[] { 43.0, 97 } : new[] { 70.0 };
                    foreach (double cx in centers) Fan(s, cx, 52, d);
                    if (n == 1)
                    {
                        // on small cards, the rest of the case: the heatsink fins
                        for (int i = 0; i < 5; i++) { s.Add(R(22 + i * 4.5, 36, 2, 32, ArtFill.DieShine, 1, 0.6)); s.Add(R(100 + i * 4.5, 36, 2, 32, ArtFill.DieShine, 1, 0.6)); }
                    }
                    s.Add(R(22, 76.5, 96, 2, tint, 1, 0.9));                     // the light strip, in the manufacturer colour
                    switch (vendor)
                    {
                        case "NVIDIA": s.Add(R(15, 24, 110, 3, tint, 1.5, 0.95)); break;                                                 // the top band
                        case "AMD":    s.Add(new ArtShape(ArtKind.Polygon, 107, 25, 17, 17, tint, Opacity: 0.95, Points: new[] { (124.0, 25.0), (124.0, 42.0), (107.0, 25.0) })); break;   // the corner "slice"
                        case "Intel":  s.Add(R(19, 27, 24, 2.5, tint, 1.2, 0.95)); s.Add(R(19, 32, 14, 2.5, tint, 1.2, 0.7)); break;     // two plates
                    }
                    VramBar(s, 28, 99, 96, 5, vramGb, tint);
                    break;

                case GpuPictureStyle.LaptopChip:
                    s.Add(R(6, 8, 120, 92, ArtFill.SubstrateEdge, 4));           // the piece of the laptop board
                    s.Add(R(7, 9, 118, 90, ArtFill.Substrate, 3.5));
                    s.Add(R(20, 20, 56, 56, ArtFill.SubstrateEdge, 2.5));        // the graphics chip capsule
                    s.Add(R(32, 32, 32, 32, ArtFill.Die, 1.5));
                    s.Add(R(32, 32, 32, 5, ArtFill.DieShine, 1.5, 0.7));
                    s.Add(new ArtShape(ArtKind.Polygon, 22, 67, 7, 7, ArtFill.Gold, Points: new[] { (22.0, 74.0), (22.0, 67.0), (29.0, 74.0) }));
                    for (int i = 0; i < 3; i++)                                  // memoriile video
                    {
                        s.Add(R(86, 20 + i * 19.5, 28, 14, ArtFill.Die, 1.5));
                        s.Add(R(86, 20 + i * 19.5, 28, 3, ArtFill.DieShine, 1.5, 0.6));
                    }
                    for (int i = 0; i < 9; i++) s.Add(R(20 + i * 10.5, 86, 6, 3.2, ArtFill.Gold, 0.8, 0.85));   // condensatoare
                    s.Add(R(62, 22, 12, 3, tint, 1.5, 0.95)); s.Add(R(62, 28, 8, 3, tint, 1.5, 0.7));             // the manufacturer label
                    VramBar(s, 20, 91.5, 94, 4, vramGb, tint);
                    break;

                default:
                    return IntegratedGpuArt.Build(tint);   // a classic chip, no memory drawn
            }
            return new ArtDrawing(Width, Height, s);
        }
    }

    /// <summary>What can be said about a monitor from its data: diagonal, resolution name, how it is connected.</summary>
    public static class DisplayFacts
    {
        // diagonals found on monitors and laptops (inches); the size read from the monitor is in whole cm,
        // so the computed diagonal can be off by up to ~0.3 inch
        private static readonly double[] CommonInches =
        {
            10.1, 11.6, 12.5, 13.3, 14, 15, 15.6, 16, 17, 17.3, 18.5, 19, 19.5, 20, 21.5, 22, 23, 23.8, 24, 24.5, 25,
            27, 28, 29, 31.5, 32, 34, 35, 38, 40, 42, 43, 48, 49, 50, 55, 65, 75,
        };

        /// <summary>Diagonal in inches from the width and height in cm; 0 when the monitor does not report them.</summary>
        public static double DiagonalInches(double widthCm, double heightCm)
            => widthCm <= 0 || heightCm <= 0 ? 0 : Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54;

        /// <summary>Diagonal rounded to the nearest commercial size (if within 0.35 inch), otherwise to one decimal.</summary>
        public static double NiceDiagonal(double inches)
        {
            if (inches <= 0) return 0;
            double nearest = CommonInches.OrderBy(c => Math.Abs(c - inches)).First();
            return Math.Abs(nearest - inches) <= 0.35 ? nearest : Math.Round(inches, 1);
        }

        /// <summary>"23.8″", "27″"; empty when the diagonal is unknown.</summary>
        public static string DiagonalText(double inches)
            => inches <= 0 ? "" : NiceDiagonal(inches).ToString("0.#", CultureInfo.InvariantCulture) + "″";

        /// <summary>Common name of the resolution ("Full HD", "4K"); empty for resolutions without a known name.</summary>
        public static string ResolutionName(int width, int height)
        {
            int w = Math.Max(width, height), h = Math.Min(width, height);    // also for rotated monitors
            return (w, h) switch
            {
                (1280, 720) or (1366, 768) or (1360, 768) => "HD",
                (1600, 900)   => "HD+",
                (1920, 1080)  => "Full HD",
                (1920, 1200)  => "WUXGA",
                (2560, 1080)  => "UltraWide Full HD",
                (2560, 1440)  => "QHD",
                (2560, 1600)  => "WQXGA",
                (2880, 1800)  => "2.8K",
                (3440, 1440)  => "UltraWide QHD",
                (3840, 2160)  => "4K",
                (5120, 2880)  => "5K",
                _ => "",
            };
        }

        /// <summary>Pixels per inch; 0 when the diagonal is unknown.</summary>
        public static int Ppi(int width, int height, double inches)
            => inches <= 0 || width <= 0 || height <= 0 ? 0 : (int)Math.Round(Math.Sqrt((double)width * width + (double)height * height) / inches);

        /// <summary>
        /// Connection type from the Windows code (WmiMonitorConnectionParams.VideoOutputTechnology):
        /// "VGA", "DVI", "HDMI", "DisplayPort", "Miracast"; empty for the laptop's own screen and for unknown codes.
        /// </summary>
        public static string Connection(long technology) => technology switch
        {
            0  => "VGA",
            4  => "DVI",
            5  => "HDMI",
            10 => "DisplayPort",
            15 => "Miracast",
            _  => "",
        };

        /// <summary>true = the device's own screen (laptop, all-in-one), not a monitor connected by cable.</summary>
        public static bool IsBuiltIn(long technology) => technology is 6 or 11 or 13 or 0x80000000;
    }

    /// <summary>One screen in the monitors drawing.</summary>
    /// <param name="PixelWidth">Resolution, for the screen proportions.</param>
    /// <param name="PhysicalHeight">Real height (cm), for the relative size of the screens; 0 = unknown.</param>
    /// <param name="BuiltIn">The laptop screen: drawn on the laptop base, not on a stand.</param>
    /// <param name="Label">Number shown on the screen (empty = none).</param>
    /// <param name="AllInOne">An all-in-one screen: housing with a "chin", on a stand, not on a laptop base.</param>
    public sealed record MonitorSpec(int PixelWidth, int PixelHeight, double PhysicalHeight, bool Primary, bool BuiltIn, string Label = "", bool AllInOne = false);

    /// <summary>Monitors drawing (132 × 108): side by side, in the given order, at their relative proportions and sizes.</summary>
    public static class MonitorArt
    {
        public const double Width = 132, Height = 108;
        public const int MaxScreens = 3;
        private const double Gap = 5, Left = 4, Usable = 124, MaxScreenHeight = 68, StandHeight = 13, BaseOverhang = 6;

        private static ArtShape R(double x, double y, double w, double h, ArtFill fill, double radius = 0, double opacity = 1)
            => new(ArtKind.Rect, x, y, w, h, fill, radius, opacity);

        /// <summary>Width and height of each drawn screen (at most <see cref="MaxScreens"/>), in box units.</summary>
        public static IReadOnlyList<(double W, double H)> Sizes(IReadOnlyList<MonitorSpec> screens)
        {
            var list = screens.Take(MaxScreens).ToList();
            if (list.Count == 0) return Array.Empty<(double, double)>();
            // relative sizes: by real height if all screens have one, otherwise all with the same diagonal
            bool physical = list.All(m => m.PhysicalHeight > 0);
            var rel = list.Select(m =>
            {
                double aspect = m.PixelWidth > 0 && m.PixelHeight > 0 ? (double)m.PixelWidth / m.PixelHeight : 16.0 / 9;
                double h = physical ? m.PhysicalHeight : 1 / Math.Sqrt(1 + aspect * aspect);
                return (W: h * aspect, H: h);
            }).ToList();
            double extra = Gap * (list.Count - 1) + list.Count(m => m.BuiltIn && !m.AllInOne) * 2 * BaseOverhang;
            double scale = Math.Min((Usable - extra) / rel.Sum(r => r.W), MaxScreenHeight / rel.Max(r => r.H));
            return rel.Select(r => (r.W * scale, r.H * scale)).ToList();
        }

        public static ArtDrawing Build(IReadOnlyList<MonitorSpec> screens)
        {
            var s = new List<ArtShape>();
            var list = screens.Take(MaxScreens).ToList();
            var sizes = Sizes(list);
            if (sizes.Count == 0) return new ArtDrawing(Width, Height, s);

            double total = sizes.Sum(z => z.W) + Gap * (list.Count - 1) + list.Count(m => m.BuiltIn && !m.AllInOne) * 2 * BaseOverhang;
            double tallest = sizes.Max(z => z.H);
            double x = Left + (Usable - total) / 2;
            double bottom = (Height - tallest - StandHeight) / 2 + tallest;      // all screens sit on the same line

            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                var (w, h) = sizes[i];
                bool laptopBase = m.BuiltIn && !m.AllInOne;
                if (laptopBase) x += BaseOverhang;
                double y = bottom - h, cx = x + w / 2;
                double bezel = Math.Clamp(Math.Min(w, h) * 0.05, 1.5, 3);

                if (laptopBase)
                {
                    // the laptop base, wider than the screen
                    s.Add(R(x - BaseOverhang, bottom, w + 2 * BaseOverhang, 6, ArtFill.Lid, 3));
                    s.Add(R(cx - 8, bottom, 16, 2, ArtFill.LidShade, 1));
                }
                else
                {
                    if (m.AllInOne) s.Add(R(x - 1, y - 1, w + 2, h + 9, ArtFill.Lid, 4));      // the all-in-one case, with a "chin"
                    double foot = Math.Min(40, w * 0.5);
                    s.Add(R(cx - Math.Min(5, w * 0.08), bottom - 1, Math.Min(10, w * 0.16), StandHeight - 3, ArtFill.LidShade, 1));
                    s.Add(R(cx - foot / 2, bottom + StandHeight - 4, foot, 4, ArtFill.LidShade, 2));
                }
                s.Add(R(x, y, w, h, ArtFill.Die, 3));
                s.Add(R(x + bezel, y + bezel, w - 2 * bezel, h - 2 * bezel, ArtFill.Accent, 1.5, m.Primary ? 0.95 : 0.6));
                if (!string.IsNullOrWhiteSpace(m.Label))
                {
                    double th = Math.Min(18, h * 0.34);
                    s.Add(new ArtShape(ArtKind.Text, x + bezel, y + (h - th) / 2, w - 2 * bezel, th, ArtFill.White, Text: m.Label));
                }
                x += w + Gap + (laptopBase ? BaseOverhang : 0);
            }
            return new ArtDrawing(Width, Height, s);
        }
    }

    /// <summary>System drawing (132 × 108): a laptop or the motherboard of a desktop computer.</summary>
    public static class SystemArt
    {
        public const double Width = 132, Height = 108;

        private static ArtShape R(double x, double y, double w, double h, ArtFill fill, double radius = 0, double opacity = 1)
            => new(ArtKind.Rect, x, y, w, h, fill, radius, opacity);
        private static ArtShape Poly(ArtFill fill, double opacity, params (double X, double Y)[] points)
            => new(ArtKind.Polygon, points.Min(p => p.X), points.Min(p => p.Y),
                   points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y), fill, Opacity: opacity, Points: points);

        public static ArtDrawing Build(bool laptop)
        {
            var s = new List<ArtShape>();
            if (laptop)
            {
                s.Add(R(25, 9, 82, 58, ArtFill.Die, 4));                          // the lid with the screen
                s.Add(R(28.5, 12.5, 75, 51, ArtFill.Accent, 2, 0.9));
                s.Add(R(64.5, 10, 3, 1.6, ArtFill.DieShine, 0.8));                // camera
                s.Add(Poly(ArtFill.Lid, 1, (25, 68), (107, 68), (120, 92), (12, 92)));            // the base, seen from above
                s.Add(Poly(ArtFill.LidShade, 1, (30, 71), (102, 71), (109, 83), (23, 83)));       // tastatura
                for (int i = 1; i < 4; i++)                                       // the key rows
                    s.Add(R(27 - i * 1.7, 71 + i * 3, 78 + i * 3.4, 0.9, ArtFill.Lid, 0, 0.9));
                s.Add(R(54, 85.5, 24, 4.5, ArtFill.LidShade, 1.2));               // touchpad
                s.Add(R(11, 92, 110, 5, ArtFill.LidShade, 2.5));                  // the front edge
            }
            else
            {
                s.Add(R(13, 4, 106, 100, ArtFill.SubstrateEdge, 3));              // the board
                s.Add(R(14, 5, 104, 98, ArtFill.Substrate, 2.5));
                foreach (var (hx, hy) in new[] { (18.0, 9.0), (114.0, 9.0), (18.0, 99.0), (114.0, 99.0), (66.0, 56.0) })
                    s.Add(R(hx - 1.6, hy - 1.6, 3.2, 3.2, ArtFill.Card, 1.6, 0.9));               // the mounting holes
                s.Add(R(10, 10, 10, 42, ArtFill.Lid, 1.5));                       // the rear connectors
                s.Add(R(11.5, 14, 7, 8, ArtFill.LidShade, 1));
                s.Add(R(11.5, 26, 7, 8, ArtFill.LidShade, 1));
                s.Add(R(11.5, 38, 7, 10, ArtFill.LidShade, 1));
                s.Add(R(26, 9, 8, 38, ArtFill.LidShade, 1.5));                    // the processor power stage heatsinks
                s.Add(R(36, 7, 38, 5, ArtFill.LidShade, 1.5));
                s.Add(R(38, 16, 34, 34, ArtFill.LidShade, 2.5));                  // the processor socket
                s.Add(R(42, 20, 26, 26, ArtFill.Lid, 2));
                for (int i = 0; i < 4; i++)                                       // the memory slots
                    s.Add(R(82 + i * 6.5, 10, 3.6, 46, i % 2 == 0 ? ArtFill.Die : ArtFill.DieShine, 1));
                s.Add(R(111, 18, 5, 30, ArtFill.Lid, 1));                         // the power connector
                foreach (var (py, pw) in new[] { (64.0, 62.0), (74.0, 22.0), (84.0, 62.0), (94.0, 22.0) })
                    s.Add(R(24, py, pw, 4.4, ArtFill.Die, 1));                    // the PCI Express slots
                s.Add(R(52, 73, 34, 6.4, ArtFill.SubstrateEdge, 1));              // the M.2 SSD spot
                s.Add(R(52, 74.2, 4, 4, ArtFill.Gold, 0.8, 0.85));
                s.Add(R(92, 66, 22, 22, ArtFill.LidShade, 3));                    // the chipset heatsink
                s.Add(R(95, 69, 16, 16, ArtFill.Lid, 2));
                s.Add(R(52, 91, 10, 10, ArtFill.Lid, 5));                         // the clock battery
                for (int i = 0; i < 4; i++) s.Add(R(92 + i * 5.6, 93, 4, 7, ArtFill.Die, 0.8));   // the SATA connectors
                foreach (var (gx, gy) in new[] { (30.0, 52.0), (36.0, 52.0), (42.0, 54.0), (76.0, 20.0), (76.0, 27.0), (76.0, 34.0) })
                    s.Add(R(gx, gy, 3.4, 3.4, ArtFill.Gold, 1.7, 0.85));          // condensatoare
            }
            return new ArtDrawing(Width, Height, s);
        }
    }
}
