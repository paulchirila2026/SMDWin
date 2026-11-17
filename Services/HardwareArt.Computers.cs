using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    // ══════════════════════════════════════════════════════════════════════════
    // Extended illustration library: computers, motherboards, integrated graphics, peripherals, network.
    // All are shape lists (as in HardwareArt.cs), without WPF: they can be checked visually outside the app.
    // No brand marks or logos: Apple machines get only the matching silhouette.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The kind of computer drawn.</summary>
    public enum ComputerKind
    {
        Laptop,       // a classic laptop, seen slightly from above
        Ultrabook,    // laptop subtire
        AllInOne,     // display with the computer behind it
        Tower,        // turn
        SmallDesktop, // desktop mic (SFF), culcat
        MiniPc,       // mini PC (USFF, Mac mini, NUC)
        Tablet,       // tableta / 2-in-1 detasabil
        Server,       // server in rack
    }

    /// <summary>Choice of the computer drawing: the chassis type from BIOS (SMBIOS), then the model name.</summary>
    public static class ComputerPackage
    {
        /// <summary>
        /// The matching drawing. <paramref name="chassisTypes"/> = Win32_SystemEnclosure.ChassisTypes (SMBIOS: 3 desktop, 4 low profile,
        /// 6 mini tower, 7 tower, 8 portable, 9 laptop, 10 notebook, 13 all-in-one, 14 sub-notebook, 15 space-saving,
        /// 17 server, 23 rack, 30 tablet, 31 convertible, 32 detachable, 35 mini PC, 36 stick PC);
        /// <paramref name="laptop"/> = the system has a battery or a laptop chassis (when the type is missing).
        /// The model name wins when the BIOS says "Other" / "Unknown" (iMacs often say "Other").
        /// </summary>
        public static ComputerKind KindFor(IReadOnlyCollection<ushort>? chassisTypes, string? manufacturer, string? model, bool laptop)
        {
            string m = ((manufacturer ?? "") + " " + (model ?? "")).Trim();
            var byName = FromName(m);
            ushort first = chassisTypes is { Count: > 0 } ? chassisTypes.First() : (ushort)0;

            // the model name is more precise than "Other" / "Unknown" and than a generic "Desktop" set by manufacturers
            bool vague = first is 0 or 1 or 2 or 3;
            if (byName.HasValue && (vague || byName is ComputerKind.AllInOne or ComputerKind.MiniPc or ComputerKind.Ultrabook))
                return byName.Value;

            switch (first)
            {
                case 13: return ComputerKind.AllInOne;
                case 14: return ComputerKind.Ultrabook;
                case 8 or 9 or 10 or 31: return laptop && IsSlimName(m) ? ComputerKind.Ultrabook : ComputerKind.Laptop;
                case 30 or 32: return ComputerKind.Tablet;
                case 17 or 23 or 25 or 28: return ComputerKind.Server;
                case 35 or 36 or 24: return ComputerKind.MiniPc;
                case 4 or 15 or 16 or 5: return ComputerKind.SmallDesktop;
                case 6 or 7: return ComputerKind.Tower;
                case 3: return ComputerKind.Tower;
            }
            if (byName.HasValue) return byName.Value;
            return laptop ? ComputerKind.Laptop : ComputerKind.Tower;
        }

        private static bool IsSlimName(string m)
            => Regex.IsMatch(m, @"\b(MacBook|XPS\s?1[35]|Zenbook|Spectre|Gram|Surface Laptop|Swift|ThinkPad X1|Yoga Slim|Air|Aero|EliteBook (8\d0|1030|1040)|Latitude 7\d{3})\b", RegexOptions.IgnoreCase);

        /// <summary>Type inferred from the model name; null when the name says nothing.</summary>
        public static ComputerKind? FromName(string? name)
        {
            string n = name ?? "";
            if (n.Length == 0) return null;
            if (Regex.IsMatch(n, @"\biMac\d|\bAll[- ]?in[- ]?One\b|\bAIO\b|\bIdeaCentre A\d|\bOptiPlex \d+\s?AIO|\bEliteOne|\bProOne|\bSurface Studio", RegexOptions.IgnoreCase)) return ComputerKind.AllInOne;
            if (Regex.IsMatch(n, @"\bMac\s?mini|\bMacmini\d|\bNUC\b|\bNUC\d|\bBrix\b|\bMini\s?PC\b|\bPN\d{2}\b|\bEliteDesk .*Mini|\bProDesk .*Mini|\bThinkCentre M\d+\w?\s*Tiny|\bOptiPlex .*Micro|\bBeelink|\bMinisforum", RegexOptions.IgnoreCase)) return ComputerKind.MiniPc;
            if (Regex.IsMatch(n, @"\bMacBook|\bXPS\s?1[35]\b|\bZenbook|\bSpectre\b|\bLG gram\b|\bSurface Laptop\b|\bThinkPad X1\b|\bUltrabook\b", RegexOptions.IgnoreCase)) return ComputerKind.Ultrabook;
            if (Regex.IsMatch(n, @"\bSurface (Pro|Go)\b|\biPad\b|\bTablet\b", RegexOptions.IgnoreCase)) return ComputerKind.Tablet;
            if (Regex.IsMatch(n, @"\bPowerEdge\b|\bProLiant\b|\bThinkSystem\b|\bPRIMERGY\b|\bSuperServer\b|\bSystem x\d", RegexOptions.IgnoreCase)) return ComputerKind.Server;
            if (Regex.IsMatch(n, @"\bSFF\b|\bSmall Form Factor\b|\bUSFF\b", RegexOptions.IgnoreCase)) return ComputerKind.SmallDesktop;
            return null;
        }

        /// <summary>Name of the type, for the chassis label (instead of "Other").</summary>
        public static string Label(ComputerKind kind, string language) => (kind, language) switch
        {
            (ComputerKind.Laptop, "ro") => "Laptop",
            (ComputerKind.Laptop, _) => "Laptop",
            (ComputerKind.Ultrabook, "ro") => "Ultrabook",
            (ComputerKind.Ultrabook, _) => "Ultrabook",
            (ComputerKind.AllInOne, _) => "All-in-one",
            (ComputerKind.Tower, "ro") => "Turn",
            (ComputerKind.Tower, "de") => "Tower",
            (ComputerKind.Tower, "fr") => "Tour",
            (ComputerKind.Tower, _) => "Tower",
            (ComputerKind.SmallDesktop, "ro") => "Desktop mic",
            (ComputerKind.SmallDesktop, "de") => "Kleiner Desktop",
            (ComputerKind.SmallDesktop, "fr") => "Petit bureau",
            (ComputerKind.SmallDesktop, _) => "Small desktop",
            (ComputerKind.MiniPc, _) => "Mini PC",
            (ComputerKind.Tablet, "ro") => "Tabletă",
            (ComputerKind.Tablet, _) => "Tablet",
            (ComputerKind.Server, _) => "Server",
            _ => "",
        };

        public static bool IsPortable(ComputerKind kind) => kind is ComputerKind.Laptop or ComputerKind.Ultrabook or ComputerKind.Tablet;
    }

    /// <summary>The computer drawings (132 × 108).</summary>
    public static class ComputerArt
    {
        public const double Width = 132, Height = 108;

        internal static ArtShape R(double x, double y, double w, double h, ArtFill fill, double radius = 0, double opacity = 1)
            => new(ArtKind.Rect, x, y, w, h, fill, radius, opacity);
        internal static ArtShape Disc(double cx, double cy, double d, ArtFill fill, double opacity = 1)
            => new(ArtKind.Rect, cx - d / 2, cy - d / 2, d, d, fill, d / 2, opacity);
        internal static ArtShape Poly(ArtFill fill, double opacity, params (double X, double Y)[] p)
            => new(ArtKind.Polygon, p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X) - p.Min(q => q.X), p.Max(q => q.Y) - p.Min(q => q.Y), fill, Opacity: opacity, Points: p);
        internal static ArtShape T(double x, double y, double w, double h, string text, ArtFill fill)
            => new(ArtKind.Text, x, y, w, h, fill, Text: text);

        public static ArtDrawing Build(ComputerKind kind)
        {
            var s = new List<ArtShape>();
            switch (kind)
            {
                case ComputerKind.Laptop:
                    return SystemArt.Build(laptop: true);

                case ComputerKind.Ultrabook:
                    s.Add(R(22, 10, 88, 56, ArtFill.Die, 4));                                  // the lid with the screen, thin bezels
                    s.Add(R(24.5, 12.5, 83, 51, ArtFill.Accent, 2, 0.9));
                    s.Add(R(64.5, 11, 3, 1.4, ArtFill.DieShine, 0.8));                         // camera
                    s.Add(Poly(ArtFill.Lid, 1, (22, 67), (110, 67), (122, 86), (10, 86)));      // the thin base, seen from above
                    s.Add(Poly(ArtFill.LidShade, 1, (28, 70), (104, 70), (110, 78), (22, 78)));   // tastatura joasa
                    for (int i = 1; i < 3; i++) s.Add(R(25 - i * 1.6, 70 + i * 2.6, 82 + i * 3.2, 0.8, ArtFill.Lid, 0, 0.9));
                    s.Add(R(55, 79.5, 22, 4, ArtFill.LidShade, 1.2));                          // touchpad
                    s.Add(R(9, 86, 114, 3.5, ArtFill.LidShade, 1.7));                          // a very thin edge
                    break;

                case ComputerKind.AllInOne:
                    s.Add(R(12, 8, 108, 72, ArtFill.Lid, 6));                                  // case
                    s.Add(R(16, 12, 100, 56, ArtFill.Die, 3));                                 // the screen bezel
                    s.Add(R(18, 14, 96, 52, ArtFill.Accent, 2, 0.92));                         // the screen
                    s.Add(R(12, 69, 108, 11, ArtFill.LidShade, 0));                            // „barbia”
                    s.Add(R(12, 74, 108, 6, ArtFill.LidShade, 6));
                    s.Add(Disc(66, 74.5, 2.2, ArtFill.DieShine, 0.8));                         // mica pastila / senzor
                    s.Add(Poly(ArtFill.LidShade, 1, (58, 80), (74, 80), (78, 94), (54, 94)));  // the stand
                    s.Add(R(36, 93, 60, 5, ArtFill.Lid, 2.5));                                 // talpa
                    break;

                case ComputerKind.Tower:
                    s.Add(R(32, 5, 68, 98, ArtFill.Lid, 4));                                   // the case front
                    s.Add(R(34, 7, 64, 94, ArtFill.LidShade, 3, 0.5));
                    s.Add(R(40, 13, 52, 5.5, ArtFill.Die, 1));                                 // drive bays
                    s.Add(R(40, 21, 52, 5.5, ArtFill.Die, 1));
                    s.Add(R(44, 15, 18, 1.5, ArtFill.DieShine, 0.6));
                    s.Add(Disc(66, 40, 7, ArtFill.Die));                                       // the power button
                    s.Add(Disc(66, 40, 3.2, ArtFill.Accent, 0.95));
                    s.Add(R(52, 50, 7, 3, ArtFill.Die, 1)); s.Add(R(73, 50, 7, 3, ArtFill.Die, 1));   // USB
                    for (int i = 0; i < 9; i++) s.Add(R(41, 61 + i * 3.6, 50, 1.6, ArtFill.Die, 0.8, 0.75));   // the vent grille
                    s.Add(R(40, 96, 52, 2, ArtFill.Accent, 1, 0.9));                           // banda luminoasa
                    s.Add(R(36, 103, 8, 3, ArtFill.LidShade, 1)); s.Add(R(88, 103, 8, 3, ArtFill.LidShade, 1));   // picioare
                    break;

                case ComputerKind.SmallDesktop:
                    s.Add(Poly(ArtFill.LidShade, 1, (14, 44), (108, 44), (120, 34), (26, 34)));    // the top face
                    s.Add(R(14, 44, 94, 30, ArtFill.Lid, 3));                                  // fata
                    s.Add(Poly(ArtFill.LidShade, 1, (108, 44), (120, 34), (120, 62), (108, 74)));  // latura
                    s.Add(R(20, 51, 44, 6, ArtFill.Die, 1));                                   // unitate optica
                    s.Add(R(22, 53, 14, 1.4, ArtFill.DieShine, 0.6));
                    s.Add(R(20, 62, 22, 5, ArtFill.Die, 1));                                   // locas
                    for (int i = 0; i < 6; i++) s.Add(R(70 + i * 3.4, 51, 1.8, 16, ArtFill.Die, 0.8, 0.7));   // aerisire
                    s.Add(Disc(96, 59, 6, ArtFill.Die)); s.Add(Disc(96, 59, 2.8, ArtFill.Accent, 0.95));   // pornire
                    s.Add(R(22, 74, 10, 3, ArtFill.LidShade, 1)); s.Add(R(88, 74, 10, 3, ArtFill.LidShade, 1));
                    break;

                case ComputerKind.MiniPc:
                    s.Add(R(22, 80, 90, 5, ArtFill.LidShade, 2.5, 0.35));                      // shadow
                    s.Add(Poly(ArtFill.LidShade, 1, (26, 52), (106, 52), (116, 42), (36, 42)));    // the top lid
                    s.Add(R(26, 52, 80, 28, ArtFill.Lid, 5));                                  // fata
                    s.Add(Poly(ArtFill.LidShade, 1, (106, 52), (116, 42), (116, 70), (106, 80)));  // latura
                    s.Add(R(26, 72, 80, 8, ArtFill.LidShade, 4, 0.9));                         // talpa
                    s.Add(R(34, 60, 7, 3, ArtFill.Die, 1)); s.Add(R(44, 60, 7, 3, ArtFill.Die, 1));    // USB
                    s.Add(R(94, 59.5, 6, 4, ArtFill.Die, 1));
                    s.Add(Disc(85, 61.5, 3.4, ArtFill.Accent, 0.95));                          // the power light
                    break;

                case ComputerKind.Tablet:
                    s.Add(R(30, 7, 72, 94, ArtFill.Lid, 9));                                   // case
                    s.Add(R(33, 10, 66, 88, ArtFill.Die, 7));                                  // rama
                    s.Add(R(36, 15, 60, 78, ArtFill.Accent, 3, 0.92));                         // the screen
                    s.Add(Disc(66, 12.5, 2.2, ArtFill.DieShine, 0.8));                         // camera
                    s.Add(R(58, 94.5, 16, 1.4, ArtFill.DieShine, 0.5, 0.7));                   // gesture bar
                    break;

                case ComputerKind.Server:
                    s.Add(R(6, 6, 4, 96, ArtFill.LidShade, 1)); s.Add(R(122, 6, 4, 96, ArtFill.LidShade, 1));   // the rack posts
                    double[] ys = { 10, 40, 70 };
                    for (int u = 0; u < 3; u++)
                    {
                        double y = ys[u];
                        s.Add(R(10, y, 112, 27, ArtFill.Lid, 2.5));
                        s.Add(R(13, y + 3, 78, 21, ArtFill.Die, 1.5));                         // the drive bays
                        for (int c = 0; c < 6; c++) s.Add(R(15 + c * 12.7, y + 5, 10.5, 17, ArtFill.LidShade, 1, 0.9));
                        for (int c = 0; c < 6; c++) s.Add(R(17 + c * 12.7, y + 19, 4, 1.4, ArtFill.Accent, 0.9));    // LED-urile
                        s.Add(Disc(104, y + 13.5, 7, ArtFill.Die)); s.Add(Disc(104, y + 13.5, 3.2, ArtFill.Accent, u == 0 ? 0.95 : 0.5));
                        s.Add(R(96, y + 4, 22, 3, ArtFill.LidShade, 1)); s.Add(R(96, y + 20, 22, 3, ArtFill.LidShade, 1));
                    }
                    break;
            }
            return new ArtDrawing(Width, Height, s);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Motherboards
    // ══════════════════════════════════════════════════════════════════════════

    public enum BoardKind { Atx, MicroAtx, MiniItx, Laptop }

    public static class BoardPackage
    {
        /// <summary>Board form factor: from its name (ATX, mATX, mini-ITX), then from the computer's chassis type.</summary>
        public static BoardKind KindFor(string? boardModel, ComputerKind computer)
        {
            string n = boardModel ?? "";
            if (ComputerPackage.IsPortable(computer)) return BoardKind.Laptop;
            if (Regex.IsMatch(n, @"mini[- ]?ITX|\bITX\b|\bDeskMini\b|\bNUC\b|\bMac\s?mini", RegexOptions.IgnoreCase)) return BoardKind.MiniItx;
            if (Regex.IsMatch(n, @"micro[- ]?ATX|\bmATX\b|\bM-ATX\b|\b(A|B|H|Z|X)\d{2,3}M\b|\bPRO-?M\b|-M\d?\b", RegexOptions.IgnoreCase)) return BoardKind.MicroAtx;
            if (Regex.IsMatch(n, @"\bATX\b", RegexOptions.IgnoreCase)) return BoardKind.Atx;
            return computer switch
            {
                ComputerKind.MiniPc => BoardKind.MiniItx,
                ComputerKind.SmallDesktop => BoardKind.MicroAtx,
                ComputerKind.AllInOne => BoardKind.MiniItx,
                ComputerKind.Server => BoardKind.Atx,
                _ => BoardKind.Atx,
            };
        }
    }

    public static class BoardArt
    {
        public const double Width = 132, Height = 108;
        private static ArtShape R(double x, double y, double w, double h, ArtFill f, double r = 0, double o = 1) => ComputerArt.R(x, y, w, h, f, r, o);

        public static ArtDrawing Build(BoardKind kind)
        {
            var s = new List<ArtShape>();
            if (kind == BoardKind.Laptop) return BuildLaptopBoard(s);

            // the drawn board dimensions (real proportions: ATX 305×244, mATX 244×244, mini-ITX 170×170)
            double bw, bh; int pcie, ram;
            switch (kind)
            {
                case BoardKind.MiniItx:  bw = 70;  bh = 70;  pcie = 1; ram = 2; break;
                case BoardKind.MicroAtx: bw = 92;  bh = 92;  pcie = 2; ram = 4; break;
                default:                 bw = 114; bh = 91;  pcie = 4; ram = 4; break;
            }
            double bx = (Width - bw) / 2, by = (Height - bh) / 2;
            double X(double f) => bx + f * bw;
            double Y(double f) => by + f * bh;
            double W(double f) => f * bw;
            double H(double f) => f * bh;

            s.Add(R(bx - 1, by - 1, bw + 2, bh + 2, ArtFill.SubstrateEdge, 3));
            s.Add(R(bx, by, bw, bh, ArtFill.Substrate, 2.5));
            foreach (var (fx, fy) in new[] { (0.05, 0.06), (0.95, 0.06), (0.05, 0.94), (0.95, 0.94), (0.5, 0.5) })
                s.Add(R(X(fx) - 1.6, Y(fy) - 1.6, 3.2, 3.2, ArtFill.Card, 1.6, 0.9));          // the mounting holes

            bool tiny = kind == BoardKind.MiniItx;
            s.Add(R(bx - 2, Y(0.1), W(tiny ? 0.14 : 0.1), H(0.42), ArtFill.Lid, 1.5));         // the rear connectors
            for (int i = 0; i < 3; i++) s.Add(R(bx - 0.5, Y(0.14 + i * 0.13), W(tiny ? 0.1 : 0.065), H(0.09), ArtFill.LidShade, 1));
            s.Add(R(X(0.22), Y(0.09), W(0.075), H(0.4), ArtFill.LidShade, 1.5));                // the power stage heatsinks
            s.Add(R(X(0.30), Y(0.06), W(0.30), H(0.055), ArtFill.LidShade, 1.5));
            double sk = tiny ? 0.30 : 0.30;                                                      // the processor socket
            double sw = tiny ? 0.34 : 0.30, sh = tiny ? 0.40 : 0.36;
            s.Add(R(X(sk), Y(0.16), W(sw), H(sh), ArtFill.LidShade, 2.5));
            s.Add(R(X(sk + 0.035), Y(0.16 + 0.04), W(sw - 0.07), H(sh - 0.08), ArtFill.Lid, 2));
            double ramX = X(0.72);
            for (int i = 0; i < ram; i++)                                                         // the memory slots
                s.Add(R(X(tiny ? 0.76 + i * 0.07 : 0.72 + i * 0.058), Y(0.09), W(0.034), H(tiny ? 0.40 : 0.46), i % 2 == 0 ? ArtFill.Die : ArtFill.DieShine, 1));
            _ = ramX;
            s.Add(R(X(0.93), Y(0.17), W(0.04), H(0.28), ArtFill.Lid, 1));                         // the power connector
            // PCI Express slots at the bottom; the main one is the longest
            for (int i = 0; i < pcie; i++)
            {
                double fy = tiny ? 0.66 : 0.64 + i * (kind == BoardKind.MicroAtx ? 0.13 : 0.095);
                double len = (i == 0 || (kind == BoardKind.Atx && i == 2)) ? 0.54 : 0.2;
                s.Add(R(X(0.17), Y(fy), W(len), H(0.05), ArtFill.Die, 1));
            }
            if (!tiny)
            {
                s.Add(R(X(0.44), Y(kind == BoardKind.Atx ? 0.60 : 0.52), W(0.28), H(0.05), ArtFill.SubstrateEdge, 1));   // the M.2 SSD spot
                s.Add(R(X(0.44), Y(kind == BoardKind.Atx ? 0.605 : 0.525), W(0.034), H(0.04), ArtFill.Gold, 0.8, 0.85));
            }
            else
            {
                s.Add(R(X(0.14), Y(0.78), W(0.28), H(0.06), ArtFill.SubstrateEdge, 1));
            }
            double cs = tiny ? 0.2 : 0.18;                                                        // the chipset heatsink
            s.Add(R(X(0.74), Y(tiny ? 0.60 : 0.64), W(cs), H(cs * (bw / bh)), ArtFill.LidShade, 3));
            s.Add(R(X(0.74 + cs * 0.15), Y((tiny ? 0.60 : 0.64) + cs * (bw / bh) * 0.15), W(cs * 0.7), H(cs * (bw / bh) * 0.7), ArtFill.Lid, 2));
            for (int i = 0; i < 4; i++) s.Add(R(X(0.74 + i * 0.045), Y(0.90), W(0.03), H(0.07), ArtFill.Die, 0.8));   // the SATA connectors
            double batX = tiny ? 0.52 : 0.45, batY = tiny ? 0.80 : 0.755;
            s.Add(R(X(batX), Y(batY), W(0.07), H(0.07 * (bw / bh)), ArtFill.Lid, 5));                                    // the clock battery
            return new ArtDrawing(Width, Height, s);
        }

        private static ArtDrawing BuildLaptopBoard(List<ArtShape> s)
        {
            // irregular outline: a cut corner at the top right (room for a fan), a cut-out at the bottom left
            var outline = new (double X, double Y)[] { (8, 12), (92, 12), (124, 26), (124, 96), (60, 96), (60, 84), (8, 84) };
            s.Add(ComputerArt.Poly(ArtFill.SubstrateEdge, 1, outline));
            var inner = new (double X, double Y)[] { (9.5, 13.5), (91.4, 13.5), (122.5, 27), (122.5, 94.5), (61.5, 94.5), (61.5, 82.5), (9.5, 82.5) };
            s.Add(ComputerArt.Poly(ArtFill.Substrate, 1, inner));
            foreach (var (hx, hy) in new[] { (14.0, 18.0), (86.0, 18.0), (118.0, 90.0), (14.0, 78.0) })
                s.Add(R(hx - 1.6, hy - 1.6, 3.2, 3.2, ArtFill.Card, 1.6, 0.9));
            // the processor and the chip next to it
            s.Add(R(26, 28, 32, 32, ArtFill.SubstrateEdge, 2));
            s.Add(R(32, 34, 20, 20, ArtFill.Die, 1.5));
            s.Add(R(32, 34, 20, 3.5, ArtFill.DieShine, 1.5, 0.7));
            s.Add(R(62, 34, 14, 14, ArtFill.Die, 1.5));
            // heat pipe: from the processor to the fan
            s.Add(ComputerArt.Poly(ArtFill.LidShade, 0.95, (52, 40), (95, 40), (95, 47), (52, 50)));
            s.Add(R(28, 30, 28, 28, ArtFill.LidShade, 2, 0.35));
            // the fan
            double cx = 100, cy = 45;
            s.Add(ComputerArt.Disc(cx, cy, 28, ArtFill.Lid, 1));
            s.Add(ComputerArt.Disc(cx, cy, 24, ArtFill.Die, 1));
            const int blades = 6;
            for (int i = 0; i < blades; i++)
            {
                double a = i * 2 * Math.PI / blades;
                (double, double) P(double ang, double r) => (cx + Math.Cos(ang) * r, cy + Math.Sin(ang) * r);
                s.Add(new ArtShape(ArtKind.Polygon, cx - 11, cy - 11, 22, 22, ArtFill.DieShine, Opacity: 0.9,
                    Points: new[] { P(a, 3.5), P(a + 0.15, 11), P(a + 0.75, 10.5), P(a + 0.55, 3.5) }));
            }
            s.Add(ComputerArt.Disc(cx, cy, 6, ArtFill.LidShade));
            // the memory and the SSD
            s.Add(R(14, 66, 44, 11, ArtFill.Die, 1.5));                                          // SO-DIMM
            for (int i = 0; i < 4; i++) s.Add(R(18 + i * 10, 68, 7, 6, ArtFill.LidShade, 1));
            s.Add(R(66, 70, 44, 8, ArtFill.SubstrateEdge, 1));                                   // loc M.2
            s.Add(R(66, 71, 4, 6, ArtFill.Gold, 0.8, 0.85));
            // battery connector and ports on the edge
            s.Add(R(112, 62, 8, 18, ArtFill.Lid, 1.5));
            for (int i = 0; i < 3; i++) s.Add(R(14 + i * 10, 15.5, 7, 3, ArtFill.Gold, 0.8, 0.85));
            foreach (var (gx, gy) in new[] { (22.0, 24.0), (62.0, 26.0), (80.0, 64.0), (76.0, 24.0) })
                s.Add(R(gx, gy, 3.2, 3.2, ArtFill.Gold, 1.6, 0.85));                            // condensatoare
            return new ArtDrawing(Width, Height, s);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Integrated graphics: a new, readable drawing
    // ══════════════════════════════════════════════════════════════════════════

    public static class IntegratedGpuArt
    {
        public const double Width = 132, Height = 108;
        private static ArtShape R(double x, double y, double w, double h, ArtFill f, double r = 0, double o = 1) => ComputerArt.R(x, y, w, h, f, r, o);

        /// <summary>
        /// Integrated graphics as a classic chip: one square package with the silicon die in the middle, contacts on the
        /// edges and the manufacturer label. No memory drawn: the graphics uses system memory (stated on the card).
        /// </summary>
        public static ArtDrawing Build(ArtFill tint = ArtFill.Accent)
        {
            var s = new List<ArtShape>();
            s.Add(R(14, 10, 104, 88, ArtFill.SubstrateEdge, 4));                 // the board piece
            s.Add(R(15, 11, 102, 86, ArtFill.Substrate, 3.5));
            s.Add(R(34, 20, 64, 68, ArtFill.SubstrateEdge, 3));                  // the chip package
            s.Add(R(35, 21, 62, 66, ArtFill.Substrate, 2.5));
            s.Add(R(43, 29, 46, 50, ArtFill.Die, 2));                            // the silicon die
            s.Add(R(43, 29, 46, 6, ArtFill.DieShine, 2, 0.7));
            s.Add(R(49, 52, 22, 3, tint, 1.5, 0.95));                            // the manufacturer label
            s.Add(R(49, 58, 14, 3, tint, 1.5, 0.7));
            for (int i = 0; i < 6; i++)                                          // condensatoare in jurul pachetului
            {
                s.Add(R(38 + i * 10.5, 90, 6, 3.2, ArtFill.Gold, 0.8, 0.85));
                s.Add(R(38 + i * 10.5, 14, 6, 3.2, ArtFill.Gold, 0.8, 0.6));
            }
            s.Add(new ArtShape(ArtKind.Polygon, 36, 30, 7, 7, ArtFill.Gold, Points: new[] { (36.0, 30.0), (36.0, 23.0), (43.0, 30.0) }));
            return new ArtDrawing(Width, Height, s);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Peripherals and network
    // ══════════════════════════════════════════════════════════════════════════

    public enum PeripheralKind { Keyboard, Mouse, Camera, Speakers, Printer, UsbStick, Headphones, UsbHub, Phone, ExternalHdd, Router }

    public static class PeripheralArt
    {
        public const double Width = 132, Height = 108;
        private static ArtShape R(double x, double y, double w, double h, ArtFill f, double r = 0, double o = 1) => ComputerArt.R(x, y, w, h, f, r, o);

        /// <summary>Kind of device from its name; null when the name does not say.</summary>
        public static PeripheralKind? KindFor(string? name)
        {
            string n = name ?? "";
            bool Is(string pattern) => Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase);
            if (Is(@"keyboard|tastatur|\bHID Keyboard")) return PeripheralKind.Keyboard;
            if (Is(@"\bmouse\b|trackball|touchpad|\bpointing\b")) return PeripheralKind.Mouse;
            if (Is(@"camera|webcam|\bUVC\b|\bC9\d\d\b|\bBRIO\b|\bFacetime\b")) return PeripheralKind.Camera;
            if (Is(@"headphone|headset|earbud|earphone|airpods|\bbuds\b|\bcasti\b|\bcăști\b")) return PeripheralKind.Headphones;
            if (Is(@"speaker|\baudio\b|boxe|sound|realtek|\bhigh definition\b")) return PeripheralKind.Speakers;
            if (Is(@"printer|imprimant|\bLaserJet\b|\bDeskJet\b|\bPixma\b|\bEcoTank\b|\bBrother\b")) return PeripheralKind.Printer;
            if (Is(@"iphone|ipad|android|galaxy|\bpixel\b|smartphone|mobile phone|apple mobile|\bMTP\b|portable device|\bphone\b")) return PeripheralKind.Phone;
            if (Is(@"\brouter\b|\bmodem\b|access point")) return PeripheralKind.Router;
            if (Is(@"my passport|my book|backup plus|expansion (portable|desk)|easystore|elements (se|portable)|portable (ssd|hdd|drive|disk)|external (hdd|hard|drive|disk|ssd)|\bUSB (3\.\d )?(hdd|hard)")) return PeripheralKind.ExternalHdd;
            if (Is(@"\bhub\b")) return PeripheralKind.UsbHub;
            if (Is(@"mass storage|flash|\bUSB (stick|drive|disk)|\bdisk drive\b|Cruzer|DataTraveler")) return PeripheralKind.UsbStick;
            return null;
        }

        /// <summary>Order in which a type is chosen as the main drawing when there are several devices (most "visible" first).</summary>
        public static readonly PeripheralKind[] Priority =
        {
            PeripheralKind.Printer, PeripheralKind.Phone, PeripheralKind.Router, PeripheralKind.ExternalHdd, PeripheralKind.Headphones,
            PeripheralKind.Camera, PeripheralKind.Keyboard, PeripheralKind.Mouse, PeripheralKind.Speakers, PeripheralKind.UsbHub, PeripheralKind.UsbStick,
        };

        /// <summary>The distinct types recognised in the list of names, in the order of <see cref="Priority"/>.</summary>
        public static IReadOnlyList<PeripheralKind> DistinctKinds(IEnumerable<string?> names)
        {
            var found = new HashSet<PeripheralKind>();
            foreach (var n in names) if (KindFor(n) is PeripheralKind k) found.Add(k);
            return Priority.Where(found.Contains).ToList();
        }

        /// <summary>
        /// Drawing for a peripherals card: one type → its full drawing; 2–4 types → a 2 × 2 grid of smaller drawings;
        /// above four, the first four by <see cref="Priority"/> remain.
        /// </summary>
        public static ArtDrawing BuildSet(IReadOnlyList<PeripheralKind> kinds)
        {
            if (kinds.Count == 0) return Build(PeripheralKind.UsbStick);
            if (kinds.Count == 1) return Build(kinds[0]);
            return ArtLayout.Tile(kinds.Take(4).Select(Build).ToList(), Width, Height, 2);
        }

        private static IReadOnlyList<(double X, double Y)> Arc(double cx, double cy, double rOut, double rIn, double fromDeg, double toDeg, int steps)
        {
            var pts = new List<(double X, double Y)>();
            for (int i = 0; i <= steps; i++)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / steps) * Math.PI / 180;
                pts.Add((cx + Math.Cos(a) * rOut, cy + Math.Sin(a) * rOut));
            }
            for (int i = steps; i >= 0; i--)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / steps) * Math.PI / 180;
                pts.Add((cx + Math.Cos(a) * rIn, cy + Math.Sin(a) * rIn));
            }
            return pts;
        }

        private static ArtShape Poly(IReadOnlyList<(double X, double Y)> pts, ArtFill fill, double opacity = 1)
        {
            double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
            return new ArtShape(ArtKind.Polygon, x0, y0, pts.Max(p => p.X) - x0, pts.Max(p => p.Y) - y0, fill, Opacity: opacity, Points: pts);
        }

        public static ArtDrawing Build(PeripheralKind kind)
        {
            var s = new List<ArtShape>();
            switch (kind)
            {
                case PeripheralKind.Keyboard:
                    s.Add(R(6, 32, 120, 52, ArtFill.Lid, 5));
                    s.Add(R(9, 35, 114, 46, ArtFill.LidShade, 3, 0.85));
                    for (int row = 0; row < 4; row++)
                    {
                        int keys = row == 3 ? 0 : 11 - (row == 1 ? 1 : 0);
                        for (int k = 0; k < keys; k++) s.Add(R(12 + k * 9.6 + (row == 1 ? 3 : row == 2 ? 5 : 0), 38 + row * 9.4, 7.8, 7, ArtFill.Die, 1.2, 0.9));
                    }
                    s.Add(R(26, 38 + 3 * 9.4, 70, 7, ArtFill.Die, 1.2, 0.9));        // space bar
                    s.Add(R(100, 38 + 3 * 9.4, 10, 7, ArtFill.Die, 1.2, 0.9));
                    s.Add(R(12, 38 + 3 * 9.4, 11, 7, ArtFill.Die, 1.2, 0.9));
                    break;

                case PeripheralKind.Mouse:
                    s.Add(R(38, 14, 56, 82, ArtFill.Lid, 26));
                    s.Add(R(41, 17, 50, 76, ArtFill.LidShade, 23, 0.85));
                    s.Add(R(65.2, 17, 1.6, 34, ArtFill.Die, 0.6, 0.8));              // the button divider
                    s.Add(R(41, 50, 50, 1.5, ArtFill.Die, 0.6, 0.7));
                    s.Add(R(61, 26, 10, 18, ArtFill.Die, 5));                        // rotita
                    s.Add(R(63.5, 29, 5, 8, ArtFill.Accent, 2.5, 0.95));
                    break;

                case PeripheralKind.Camera:
                    s.Add(R(36, 20, 60, 36, ArtFill.Lid, 8));                        // the webcam body
                    s.Add(ComputerArt.Disc(66, 38, 26, ArtFill.Die));
                    s.Add(ComputerArt.Disc(66, 38, 18, ArtFill.LidShade, 0.9));
                    s.Add(ComputerArt.Disc(66, 38, 10, ArtFill.Accent, 0.9));
                    s.Add(ComputerArt.Disc(62.5, 34.5, 3, ArtFill.White, 0.55));
                    s.Add(ComputerArt.Disc(88, 26, 2.4, ArtFill.Accent, 0.95));      // LED-ul
                    s.Add(R(60, 56, 12, 14, ArtFill.LidShade, 1.5));                 // the neck
                    s.Add(R(44, 69, 44, 7, ArtFill.Lid, 3.5));                       // clema / talpa
                    break;

                case PeripheralKind.Speakers:
                    foreach (double x in new[] { 14.0, 78.0 })
                    {
                        s.Add(R(x, 18, 40, 72, ArtFill.Lid, 6));
                        s.Add(R(x + 3, 21, 34, 66, ArtFill.LidShade, 4, 0.8));
                        s.Add(ComputerArt.Disc(x + 20, 38, 12, ArtFill.Die));        // tweeter
                        s.Add(ComputerArt.Disc(x + 20, 38, 5, ArtFill.DieShine, 0.8));
                        s.Add(ComputerArt.Disc(x + 20, 67, 26, ArtFill.Die));        // woofer
                        s.Add(ComputerArt.Disc(x + 20, 67, 17, ArtFill.LidShade, 0.9));
                        s.Add(ComputerArt.Disc(x + 20, 67, 7, ArtFill.Die));
                    }
                    break;

                case PeripheralKind.Printer:
                    s.Add(R(34, 12, 64, 12, ArtFill.LidShade, 2));                   // top paper tray
                    s.Add(R(14, 24, 104, 48, ArtFill.Lid, 6));                       // the body
                    s.Add(R(20, 30, 36, 10, ArtFill.Accent, 2, 0.9));                // the small screen
                    s.Add(ComputerArt.Disc(100, 35, 4, ArtFill.Die)); s.Add(ComputerArt.Disc(100, 35, 1.8, ArtFill.Accent, 0.95));
                    s.Add(R(24, 52, 84, 6, ArtFill.Die, 2));                         // the outlet slot
                    s.Add(R(32, 62, 68, 30, ArtFill.White, 1.5, 0.95));              // foaia
                    s.Add(R(38, 70, 56, 2, ArtFill.LidShade, 1, 0.7));
                    s.Add(R(38, 76, 46, 2, ArtFill.LidShade, 1, 0.7));
                    s.Add(R(38, 82, 52, 2, ArtFill.LidShade, 1, 0.7));
                    break;

                case PeripheralKind.Headphones:
                    s.Add(Poly(Arc(66, 58, 46, 39, 180, 360, 18), ArtFill.Lid));                        // the headband
                    foreach (double x in new[] { 12.0, 98.0 })
                    {
                        s.Add(R(x, 52, 22, 40, ArtFill.LidShade, 9));                                   // castile
                        s.Add(R(x + 3, 58, 16, 28, ArtFill.Die, 6));
                        s.Add(ComputerArt.Disc(x + 11, 72, 9, ArtFill.Accent, 0.95));
                    }
                    break;

                case PeripheralKind.UsbHub:
                    s.Add(R(2, 54, 14, 16, ArtFill.Lid, 2));                                            // mufa USB
                    s.Add(R(5, 58, 7, 3, ArtFill.Die, 0.5)); s.Add(R(5, 64, 7, 3, ArtFill.Die, 0.5));
                    s.Add(R(16, 60, 16, 5, ArtFill.LidShade, 2.5));                                     // the cable
                    s.Add(R(30, 40, 90, 42, ArtFill.Lid, 9));                                           // the body
                    s.Add(R(30, 72, 90, 10, ArtFill.LidShade, 9, 0.85));
                    for (int i = 0; i < 4; i++)
                    {
                        s.Add(R(41 + i * 19, 50, 14, 9, ArtFill.Die, 1.5));
                        s.Add(R(43 + i * 19, 54, 10, 2, ArtFill.Gold, 0.5, 0.9));
                    }
                    s.Add(ComputerArt.Disc(112, 48, 4, ArtFill.Accent, 0.95));
                    break;

                case PeripheralKind.Phone:
                    s.Add(R(42, 6, 48, 96, ArtFill.Die, 9));                                            // the body
                    s.Add(R(46, 12, 40, 84, ArtFill.Accent, 5, 0.95));                                  // the screen
                    s.Add(R(58, 14, 16, 4, ArtFill.Die, 2));                                            // the camera cutout
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++) s.Add(R(51 + c * 12.5, 30 + r * 14, 9, 9, ArtFill.White, 2.5, 0.85));
                    s.Add(R(56, 89, 20, 2, ArtFill.White, 1, 0.8));
                    break;

                case PeripheralKind.ExternalHdd:
                    s.Add(R(16, 22, 98, 64, ArtFill.Lid, 10));                                          // portable enclosure
                    s.Add(R(16, 22, 98, 16, ArtFill.LidShade, 10, 0.6));
                    s.Add(ComputerArt.Disc(48, 58, 36, ArtFill.Die));                                   // the platter shows through
                    s.Add(ComputerArt.Disc(48, 58, 26, ArtFill.LidShade, 0.9));
                    s.Add(ComputerArt.Disc(48, 58, 8, ArtFill.Die));
                    s.Add(R(76, 46, 30, 6, ArtFill.Die, 3, 0.8)); s.Add(R(76, 58, 22, 4, ArtFill.Die, 2, 0.5));
                    s.Add(ComputerArt.Disc(104, 74, 4, ArtFill.Accent, 0.95));
                    s.Add(R(114, 52, 10, 8, ArtFill.Die, 2));                                           // the USB cable
                    s.Add(R(122, 49, 10, 14, ArtFill.LidShade, 1.5));
                    break;

                case PeripheralKind.Router:
                    return NetworkArt.Build(NetworkKind.Router);

                case PeripheralKind.UsbStick:
                    s.Add(R(14, 40, 28, 28, ArtFill.LidShade, 2));                   // mufa USB
                    s.Add(R(14, 40, 28, 28, ArtFill.Lid, 2, 0.6));
                    s.Add(R(20, 47, 6, 5, ArtFill.Die, 0.8)); s.Add(R(20, 56, 6, 5, ArtFill.Die, 0.8));
                    s.Add(R(42, 34, 76, 40, ArtFill.Accent, 9, 0.95));
                    s.Add(R(52, 44, 36, 20, ArtFill.White, 3, 0.9));
                    s.Add(ComputerArt.Disc(106, 54, 6, ArtFill.White, 0.9)); s.Add(ComputerArt.Disc(106, 54, 3, ArtFill.Accent, 0.9));
                    break;
            }
            return new ArtDrawing(Width, Height, s);
        }
    }

    public enum NetworkKind { Router, WifiCard, EthernetPort }

    public static class NetworkArt
    {
        public const double Width = 132, Height = 108;
        private static ArtShape R(double x, double y, double w, double h, ArtFill f, double r = 0, double o = 1) => ComputerArt.R(x, y, w, h, f, r, o);

        /// <summary>How many of 4 bars are full for a Wi-Fi signal in percent: 75+ → 4, 50+ → 3, 25+ → 2, any signal → 1, no signal → 0.</summary>
        public static int SignalBars(int signalPct) => signalPct >= 75 ? 4 : signalPct >= 50 ? 3 : signalPct >= 25 ? 2 : signalPct > 0 ? 1 : 0;

        /// <summary>The signal colour: good (60%+) green, medium (35%+) yellow, weak red.</summary>
        public static ArtFill SignalFill(int signalPct) => signalPct >= 60 ? ArtFill.Ok : signalPct >= 35 ? ArtFill.Warn : ArtFill.Crit;

        /// <summary>
        /// Router with the connection signal: four rising bars on the right (the full ones in the quality colour) and the router
        /// LEDs in the same colour. Not connected: grey bars, LEDs off and a red cross over the bars. Not read yet
        /// (<paramref name="known"/> = false): everything grey, no cross.
        /// </summary>
        public static ArtDrawing BuildWifi(int signalPct, bool connected, bool known = true)
        {
            var s = new List<ArtShape>();
            bool live = connected && known;
            int bars = live ? SignalBars(signalPct) : 0;
            var tint = live ? SignalFill(signalPct) : ArtFill.Muted;
            s.Add(R(25, 30, 4, 38, ArtFill.LidShade, 2)); s.Add(R(103, 30, 4, 38, ArtFill.LidShade, 2));          // the antennas, at the ends of the body
            s.Add(R(12, 66, 108, 32, ArtFill.Lid, 6));                                                            // the body, across the full width
            s.Add(R(12, 90, 108, 8, ArtFill.LidShade, 6, 0.85));
            for (int i = 0; i < 4; i++) s.Add(ComputerArt.Disc(28 + i * 9, 80, 3.6, tint, live ? 0.95 : 0.4));    // the LEDs
            for (int i = 0; i < 3; i++) s.Add(R(76 + i * 12, 76, 9, 8, ArtFill.Die, 1));                           // the ports
            for (int i = 0; i < 4; i++)                                                                          // the signal bars: centred, above the body, between the antennas
            {
                double h = 8 + i * 6, x = 47 + i * 10;
                s.Add(R(x, 60 - h, 6, h, i < bars ? tint : ArtFill.Muted, 1.5, i < bars ? 0.95 : 0.35));
            }
            if (!connected && known)
            {
                // the red cross, over the bars
                s.Add(new ArtShape(ArtKind.Polygon, 50, 28, 32, 32, ArtFill.Crit, Opacity: 0.95, Points: new[] { (52.0, 34.0), (56.0, 30.0), (80.0, 54.0), (76.0, 58.0) }));
                s.Add(new ArtShape(ArtKind.Polygon, 50, 28, 32, 32, ArtFill.Crit, Opacity: 0.95, Points: new[] { (76.0, 30.0), (80.0, 34.0), (56.0, 58.0), (52.0, 54.0) }));
            }
            return new ArtDrawing(Width, Height, s);
        }

        public static ArtDrawing Build(NetworkKind kind)
        {
            var s = new List<ArtShape>();
            switch (kind)
            {
                case NetworkKind.Router:
                    s.Add(R(30, 14, 4, 40, ArtFill.LidShade, 2)); s.Add(R(98, 14, 4, 40, ArtFill.LidShade, 2));   // the antennas
                    s.Add(R(64, 8, 4, 44, ArtFill.LidShade, 2));
                    s.Add(R(12, 52, 108, 34, ArtFill.Lid, 6));                                                    // the body
                    s.Add(R(12, 78, 108, 8, ArtFill.LidShade, 6, 0.85));
                    for (int i = 0; i < 4; i++) s.Add(ComputerArt.Disc(26 + i * 9, 66, 3.6, ArtFill.Accent, i < 3 ? 0.95 : 0.4));   // LED-urile
                    for (int i = 0; i < 4; i++) s.Add(R(74 + i * 10, 62, 8, 7, ArtFill.Die, 1));                   // the ports
                    s.Add(R(22, 92, 8, 3, ArtFill.LidShade, 1)); s.Add(R(102, 92, 8, 3, ArtFill.LidShade, 1));
                    break;

                case NetworkKind.WifiCard:
                    s.Add(R(6, 36, 120, 44, ArtFill.SubstrateEdge, 3));                                           // placuta
                    s.Add(R(7, 37, 118, 42, ArtFill.Substrate, 2.5));
                    for (int i = 0; i < 12; i++) s.Add(R(12 + i * 4, 70, 2.2, 9, ArtFill.Gold, 0.4, 0.9));         // contacte
                    s.Add(R(34, 44, 30, 22, ArtFill.Die, 1.5)); s.Add(R(34, 44, 30, 4, ArtFill.DieShine, 1.5, 0.6));
                    s.Add(R(72, 46, 14, 14, ArtFill.LidShade, 1.5)); s.Add(R(92, 46, 14, 14, ArtFill.LidShade, 1.5));
                    s.Add(R(116, 46, 6, 6, ArtFill.Lid, 3)); s.Add(R(116, 58, 6, 6, ArtFill.Lid, 3));              // the antenna connectors
                    for (int i = 0; i < 4; i++)                                                                    // the signal: four bars
                        s.Add(R(64 + i * 8, 30 - (6 + i * 4), 5, 6 + i * 4, ArtFill.Accent, 1.2, i < 3 ? 0.95 : 0.45));
                    break;

                case NetworkKind.EthernetPort:
                    s.Add(R(22, 22, 88, 64, ArtFill.Lid, 6));                                                     // mufa
                    s.Add(R(32, 36, 68, 38, ArtFill.Die, 3));
                    s.Add(R(46, 28, 40, 10, ArtFill.Die, 2));                                                     // clema
                    for (int i = 0; i < 8; i++) s.Add(R(37 + i * 7.6, 60, 3.2, 12, ArtFill.Gold, 0.5, 0.9));       // the contacts
                    s.Add(R(26, 26, 8, 6, ArtFill.Accent, 1.5, 0.95)); s.Add(R(98, 26, 8, 6, ArtFill.LidShade, 1.5, 0.9));  // LED-urile
                    break;
            }
            return new ArtDrawing(Width, Height, s);
        }
    }
}
