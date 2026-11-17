using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>What a shape in an illustration represents — its colour is chosen when drawing, by role (and theme).</summary>
    public enum ArtFill
    {
        Substrate,      // the green board of a processor / module
        SubstrateEdge,  // marginea mai inchisa a placii
        Lid,            // the metal lid
        LidShade,       // the lower step of the lid
        LidText,        // engraving on the lid
        Die,            // the silicon die (no lid, on laptops)
        DieShine,       // reflection on the die
        Gold,           // contacts, the corner mark, capacitors
        Accent,         // the highlighted area (integrated graphics) — the theme accent colour
        Card,           // "hole": the colour of the card the drawing sits on (notches)
        White,          // text on the accent area
        Ok,             // good state (green, from the theme)
        Warn,           // to watch (yellow)
        Crit,           // serious (red)
        Muted,          // no data (grey)
        Blue,           // fixed blue, a manufacturer colour (Intel Arc)
    }

    public enum ArtKind { Rect, Polygon, Text }

    /// <summary>
    /// One shape of the illustration, in drawing-box coordinates. Rect: rectangle with rounded corners (Radius).
    /// Polygon: the points in <see cref="Points"/>. Text: centred in the box (X, Y, W, H), bold, shrunk to fit.
    /// </summary>
    public sealed record ArtShape(ArtKind Kind, double X, double Y, double W, double H, ArtFill Fill,
                                  double Radius = 0, double Opacity = 1, string Text = "",
                                  IReadOnlyList<(double X, double Y)>? Points = null);

    /// <summary>An illustration: its box and shapes, in drawing order (the last one is on top).</summary>
    public sealed record ArtDrawing(double Width, double Height, IReadOnlyList<ArtShape> Shapes);

    /// <summary>How the drawn processor looks: by its place (laptop / PC / server) and by manufacturer.</summary>
    public enum CpuPackageStyle
    {
        LaptopIntel,          // soldered on the board, no lid: the processor die + the small chip next to it
        LaptopIntelGraphics,  // the same, with the integrated graphics area marked
        LaptopAmd,            // soldered to the board, a single chip
        DesktopIntel,         // metal lid with "wings", notches on the sides
        DesktopAmd,           // lid with cut-outs on the edges
        Server,               // pachet mare, capac in trepte
        Entry,                // a small soldered package (Celeron, Pentium, Atom, N series): a small die under a small lid
        ArmSoc,               // ARM processor (Apple M, Snapdragon): large die with visible cores and memory next to it
    }

    /// <summary>Processor family by name: decides the drawing and the lid inscription.</summary>
    public enum CpuFamily { Other, Core, Ryzen, Server, Entry, Arm }

    /// <summary>
    /// Choice of the processor drawing and reading of its name: which style fits, how many performance / efficiency
    /// cores it has (on hybrid processors), the short name for the lid.
    /// </summary>
    public static class CpuPackage
    {
        public const string Auto = "Auto";

        public static readonly (CpuPackageStyle Style, string Label)[] Styles =
        {
            (CpuPackageStyle.LaptopIntel,         "Laptop · Intel"),
            (CpuPackageStyle.LaptopIntelGraphics, "Laptop · Intel + graphics"),
            (CpuPackageStyle.LaptopAmd,           "Laptop · AMD"),
            (CpuPackageStyle.DesktopIntel,        "Desktop · Intel"),
            (CpuPackageStyle.DesktopAmd,          "Desktop · AMD"),
            (CpuPackageStyle.Server,              "Server / workstation"),
            (CpuPackageStyle.Entry,               "Entry · Celeron / Pentium / Atom"),
            (CpuPackageStyle.ArmSoc,              "ARM · Apple M / Snapdragon"),
        };

        /// <summary>
        /// Processor family. ARM: Apple M1…M9 (with Pro / Max / Ultra), Snapdragon, Qualcomm. Server: Xeon, EPYC,
        /// Threadripper, Opteron. Entry: Celeron, Pentium, Atom, Intel N100 / N200, Athlon. Then Ryzen and Core.
        /// </summary>
        public static CpuFamily Family(string? cpuName)
        {
            string n = cpuName ?? "";
            if (Regex.IsMatch(n, @"\bApple\b|\bM[1-9]\d?( (Pro|Max|Ultra))?\b") || Regex.IsMatch(n, @"\b(Snapdragon|Qualcomm|Kryo|Cortex|ARM)\b", RegexOptions.IgnoreCase)) return CpuFamily.Arm;
            if (IsServerName(n)) return CpuFamily.Server;
            if (Regex.IsMatch(n, @"\b(Celeron|Pentium|Atom|Athlon|Sempron)\b|\b(Intel\W+)?(Processor\s+)?N\d{3}\b", RegexOptions.IgnoreCase)) return CpuFamily.Entry;
            if (Regex.IsMatch(n, @"\bRyzen\b", RegexOptions.IgnoreCase)) return CpuFamily.Ryzen;
            if (Regex.IsMatch(n, @"\bCore\b|\bi[3579]-\d", RegexOptions.IgnoreCase)) return CpuFamily.Core;
            return CpuFamily.Other;
        }

        /// <summary>Large inscription on the lid: the processor brand or family ("intel", "RYZEN", "XEON", "CELERON", "Apple").</summary>
        public static string Brand(string? cpuName)
        {
            string n = cpuName ?? "";
            switch (Family(n))
            {
                case CpuFamily.Arm:
                    return Regex.IsMatch(n, @"Snapdragon", RegexOptions.IgnoreCase) ? "Snapdragon"
                         : Regex.IsMatch(n, @"Qualcomm", RegexOptions.IgnoreCase) ? "Qualcomm" : "Apple";
                case CpuFamily.Server:
                    return Regex.IsMatch(n, @"Xeon", RegexOptions.IgnoreCase) ? "XEON"
                         : Regex.IsMatch(n, @"EPYC", RegexOptions.IgnoreCase) ? "EPYC"
                         : Regex.IsMatch(n, @"Threadripper", RegexOptions.IgnoreCase) ? "RYZEN" : "AMD";
                case CpuFamily.Entry:
                    var m = Regex.Match(n, @"\b(Celeron|Pentium|Atom|Athlon|Sempron)\b", RegexOptions.IgnoreCase);
                    return m.Success ? m.Value.ToUpperInvariant() : (Vendor(n) == "AMD" ? "AMD" : "intel");
                case CpuFamily.Ryzen: return "RYZEN";
                default: return Vendor(n) == "Intel" ? "intel" : Vendor(n);
            }
        }

        /// <summary>"Intel", "AMD" or empty when the name does not say.</summary>
        public static string Vendor(string? cpuName)
        {
            string n = cpuName ?? "";
            if (Regex.IsMatch(n, @"\b(AMD|Ryzen|EPYC|Athlon|Threadripper|Phenom|Sempron|Opteron)\b", RegexOptions.IgnoreCase)) return "AMD";
            if (Regex.IsMatch(n, @"\b(Intel|Core|Xeon|Pentium|Celeron|Atom)\b", RegexOptions.IgnoreCase)) return "Intel";
            return "";
        }

        private static bool IsServerName(string n)
            => Regex.IsMatch(n, @"\b(Xeon|EPYC|Threadripper|Opteron)\b", RegexOptions.IgnoreCase);

        // large sockets (server / workstation) and soldered packages (laptop, mini PC)
        private static bool IsServerSocket(string s)
            => Regex.IsMatch(s, @"LGA\s?(2011|2066|1356|1366|3647|4189|4677)|\bs?TR(X?4|5)\b|\bSP[356]\b|\bG34\b|\bSWRX8\b", RegexOptions.IgnoreCase);
        private static bool IsSolderedSocket(string s)
            => Regex.IsMatch(s, @"BGA|^FP\d|^FT\d|^AM1\b|^FF\d", RegexOptions.IgnoreCase);

        /// <summary>
        /// The style that fits the processor. <paramref name="laptop"/> = the chassis is a laptop (or has a battery);
        /// <paramref name="socket"/> = the socket from the hardware database, if the processor is there;
        /// <paramref name="integratedGraphics"/> = the system uses the processor's graphics.
        /// A server processor in a laptop (mobile Xeon) is drawn as a laptop one.
        /// </summary>
        public static CpuPackageStyle StyleFor(string? cpuName, bool laptop, string? socket, bool integratedGraphics)
        {
            string name = cpuName ?? "", sock = socket ?? "";
            bool amd = Vendor(name) == "AMD";
            var family = Family(name);
            if (family == CpuFamily.Arm) return CpuPackageStyle.ArmSoc;
            bool soldered = laptop || IsSolderedSocket(sock);
            if (soldered && family == CpuFamily.Entry) return CpuPackageStyle.Entry;
            if (soldered)
                return amd ? CpuPackageStyle.LaptopAmd
                     : Vendor(name) == "Intel" ? (integratedGraphics ? CpuPackageStyle.LaptopIntelGraphics : CpuPackageStyle.LaptopIntel)
                     : CpuPackageStyle.LaptopAmd;      // unknown manufacturer: a single chip, no markings
            if (IsServerSocket(sock) || (sock.Length == 0 && IsServerName(name)))
                return CpuPackageStyle.Server;
            return amd ? CpuPackageStyle.DesktopAmd : CpuPackageStyle.DesktopIntel;
        }

        /// <summary>The style chosen in settings (the enum name) or, for "Auto" / unknown, the one that fits the processor.</summary>
        public static CpuPackageStyle Resolve(string? setting, CpuPackageStyle automatic)
            => Enum.TryParse<CpuPackageStyle>(setting, out var chosen) && Enum.IsDefined(chosen) ? chosen : automatic;

        /// <summary>
        /// Performance and efficiency cores of a hybrid Intel processor (12th gen+), inferred from the number of cores and
        /// threads: performance cores have two threads each, efficiency cores one.
        /// null when the processor is not hybrid or it cannot be inferred (all cores alike).
        /// </summary>
        public static (int Performance, int Efficiency)? HybridCores(string? cpuName, int cores, int threads)
        {
            // first what Windows says about each core (exact, also for processors without Hyper-Threading such as Core Ultra 200)
            if (HybridCoresFromWindows() is (int wp, int we)) return (wp, we);
            if (Vendor(cpuName) != "Intel" || cores <= 0 || threads <= cores || threads >= 2 * cores) return null;
            int performance = threads - cores, efficiency = cores - performance;
            return performance > 0 && efficiency > 0 ? (performance, efficiency) : null;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        private static (int, int)? _hybridWin;
        private static bool _hybridWinRead;

        /// <summary>
        /// Performance / efficiency cores as Windows reports them (EfficiencyClass of each core: the highest class = the
        /// performance cores). null on processors whose cores are all alike, or when it cannot be read. Read once.
        /// </summary>
        public static (int Performance, int Efficiency)? HybridCoresFromWindows()
        {
            if (_hybridWinRead) return _hybridWin;
            _hybridWinRead = true;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, IntPtr.Zero, ref len);
                if (len == 0 || len > 1_000_000) return null;
                buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)len);
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return null;
                var classes = new Dictionary<int, int>();
                int off = 0;
                while (off + 10 <= len)
                {
                    int size = System.Runtime.InteropServices.Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    int cls = System.Runtime.InteropServices.Marshal.ReadByte(buf, off + 9);   // PROCESSOR_RELATIONSHIP.EfficiencyClass
                    classes[cls] = classes.TryGetValue(cls, out int n) ? n + 1 : 1;
                    off += size;
                }
                if (classes.Count < 2) return null;
                int top = classes.Keys.Max();
                int perf = classes[top], eff = classes.Where(k => k.Key != top).Sum(k => k.Value);
                _hybridWin = perf > 0 && eff > 0 ? (perf, eff) : null;
                return _hybridWin;
            }
            catch (Exception ex) { AppLogger.Debug($"HybridCoresFromWindows: {ex.Message}"); return null; }
            finally { if (buf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// The short name: "Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz" → "Core i7-8750H";
        /// „AMD Ryzen 5 5600X 6-Core Processor” → „Ryzen 5 5600X”.
        /// </summary>
        public static string ShortName(string? cpuName)
        {
            if (string.IsNullOrWhiteSpace(cpuName)) return "";
            string s = Regex.Replace(cpuName, @"\((R|TM|tm|r|C)\)|®|™", "");
            s = Regex.Replace(s, @"\s*@.*$", "");
            s = Regex.Replace(s, @"\b(Intel|AMD|CPU|Processor|with Radeon (Vega )?Graphics|\d+-Core|\d+th Gen|Genuine)\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s.Length > 0 ? s : cpuName.Trim();
        }

        /// <summary>"2208 MHz" → "2.2 GHz" (rounded to 0.05 GHz, as processor frequencies are given);
        /// unreadable text → unchanged.</summary>
        public static string ClockText(string? mhzText)
        {
            var m = Regex.Match(mhzText ?? "", @"(\d+)\s*MHz", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int mhz) || mhz <= 0) return (mhzText ?? "").Trim();
            return mhz >= 1000
                ? (Math.Round(mhz / 50.0) * 50 / 1000.0).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " GHz"
                : mhz + " MHz";
        }
    }

    /// <summary>Drawing of a processor, as a shape list (painted in views/ArtPicture.cs).</summary>
    public static class CpuArt
    {
        public const double Width = 132, Height = 108;

        private static ArtShape R(double x, double y, double w, double h, ArtFill fill, double radius = 0, double opacity = 1)
            => new(ArtKind.Rect, x, y, w, h, fill, radius, opacity);
        private static ArtShape T(double x, double y, double w, double h, string text, ArtFill fill = ArtFill.LidText)
            => new(ArtKind.Text, x, y, w, h, fill, Text: text);
        private static ArtShape Corner(double x, double y, double size)   // the golden triangle that marks pin 1
            => new(ArtKind.Polygon, x, y - size, size, size, ArtFill.Gold, Points: new[] { (x, y), (x, y - size), (x + size, y) });

        /// <param name="vendorText">Large inscription on the lid ("intel", "AMD"); empty → none.</param>
        /// <param name="modelText">Second line ("Core i7-8700K"); empty → none.</param>
        public static ArtDrawing Build(CpuPackageStyle style, string vendorText = "", string modelText = "")
        {
            var s = new List<ArtShape>();
            switch (style)
            {
                case CpuPackageStyle.DesktopIntel:
                    s.Add(R(18, 6, 96, 96, ArtFill.SubstrateEdge, 4));
                    s.Add(R(19, 7, 94, 94, ArtFill.Substrate, 3.5));
                    s.Add(R(16, 26, 5, 8, ArtFill.Card, 1.5));               // the positioning notches
                    s.Add(R(111, 26, 5, 8, ArtFill.Card, 1.5));
                    s.Add(R(22, 24, 88, 60, ArtFill.LidShade, 5));           // the lid "wings"
                    s.Add(R(31, 14, 70, 80, ArtFill.Lid, 7));                // the raised part
                    Engrave(s, 35, 30, 62, vendorText, modelText);
                    s.Add(Corner(21, 100, 8));
                    break;

                case CpuPackageStyle.DesktopAmd:
                    s.Add(R(18, 6, 96, 96, ArtFill.SubstrateEdge, 4));
                    s.Add(R(19, 7, 94, 94, ArtFill.Substrate, 3.5));
                    s.Add(R(24, 12, 84, 84, ArtFill.LidShade, 7));
                    s.Add(R(25, 13, 82, 82, ArtFill.Lid, 6.5));
                    // cut-outs on the edges (the board shows through, with one capacitor each)
                    foreach (var (x, y, w, h) in new[]
                    {
                        (47.0, 11.0, 13.0, 9.0), (72.0, 11.0, 13.0, 9.0), (47.0, 88.0, 13.0, 9.0), (72.0, 88.0, 13.0, 9.0),
                        (23.0, 35.0, 9.0, 13.0), (23.0, 60.0, 9.0, 13.0), (100.0, 35.0, 9.0, 13.0), (100.0, 60.0, 9.0, 13.0),
                    })
                    {
                        s.Add(R(x, y, w, h, ArtFill.Substrate, 2.5));
                        s.Add(R(x + w / 2 - 2, y + h / 2 - 1.5, 4, 3, ArtFill.Gold, 0.8, 0.85));
                    }
                    Engrave(s, 36, 30, 60, vendorText, modelText);
                    s.Add(Corner(21, 100, 8));
                    break;

                case CpuPackageStyle.Server:
                    s.Add(R(6, 4, 120, 100, ArtFill.SubstrateEdge, 4));
                    s.Add(R(7, 5, 118, 98, ArtFill.Substrate, 3.5));
                    foreach (double y in new[] { 22.0, 78.0 })               // four notches, two on each side
                    {
                        s.Add(R(4, y, 5, 8, ArtFill.Card, 1.5));
                        s.Add(R(123, y, 5, 8, ArtFill.Card, 1.5));
                    }
                    s.Add(R(13, 11, 106, 86, ArtFill.LidShade, 5));          // capac in doua trepte
                    s.Add(R(20, 17, 92, 74, ArtFill.Lid, 6));
                    Engrave(s, 26, 32, 80, vendorText, modelText);
                    s.Add(Corner(9, 102, 8));
                    break;

                case CpuPackageStyle.LaptopIntel:
                case CpuPackageStyle.LaptopIntelGraphics:
                    s.Add(R(6, 17, 120, 74, ArtFill.SubstrateEdge, 3.5));
                    s.Add(R(7, 18, 118, 72, ArtFill.Substrate, 3));
                    if (style == CpuPackageStyle.LaptopIntelGraphics)
                    {
                        // the processor die (long), the integrated graphics area to its right, and the small chip next to it
                        s.Add(R(20, 32, 56, 26, ArtFill.Die, 1.5));
                        s.Add(R(20, 32, 56, 5, ArtFill.DieShine, 1.5, 0.7));
                        s.Add(R(53, 32, 23, 26, ArtFill.Accent, 1.5, 0.92));
                        s.Add(T(53, 41, 23, 8, "GPU", ArtFill.White));
                        s.Add(R(88, 35, 24, 20, ArtFill.Die, 1.5));
                        s.Add(R(88, 35, 24, 4, ArtFill.DieShine, 1.5, 0.7));
                    }
                    else
                    {
                        // the classic laptop processor: a single square package on the board, with the die in the middle
                        s.Add(R(32, 22, 68, 52, ArtFill.SubstrateEdge, 3));
                        s.Add(R(33, 23, 66, 50, ArtFill.Substrate, 2.5));
                        s.Add(R(40, 29, 52, 38, ArtFill.Die, 1.5));
                        s.Add(R(40, 29, 52, 6, ArtFill.DieShine, 1.5, 0.7));
                    }
                    for (int i = 0; i < 9; i++) s.Add(R(20 + i * 10.5, 78, 6, 3.2, ArtFill.Gold, 0.8, 0.85));   // condensatoare
                    s.Add(Corner(9, 88, 7));
                    break;

                case CpuPackageStyle.Entry:
                    // small package soldered on the board: the die under a small lid, with the family inscription
                    s.Add(R(26, 14, 80, 80, ArtFill.SubstrateEdge, 4));
                    s.Add(R(27, 15, 78, 78, ArtFill.Substrate, 3.5));
                    s.Add(R(40, 28, 52, 52, ArtFill.LidShade, 5));
                    s.Add(R(42, 30, 48, 48, ArtFill.Lid, 4));
                    // little fits on the small lid: the family in small letters, and the model without the family word ("Celeron N4020" → "N4020")
                    string shortModel = !string.IsNullOrWhiteSpace(vendorText) && modelText.StartsWith(vendorText, StringComparison.OrdinalIgnoreCase)
                        ? modelText[vendorText.Length..].Trim() : modelText;
                    if (!string.IsNullOrWhiteSpace(vendorText)) s.Add(T(46, 41, 40, 9, vendorText));
                    if (!string.IsNullOrWhiteSpace(shortModel)) s.Add(T(46, 54, 40, 8, shortModel));
                    for (int i = 0; i < 5; i++) s.Add(R(36 + i * 15, 19, 7, 3.2, ArtFill.Gold, 0.8, 0.85));
                    for (int i = 0; i < 5; i++) s.Add(R(36 + i * 15, 85, 7, 3.2, ArtFill.Gold, 0.8, 0.6));
                    s.Add(Corner(29, 91, 7));
                    break;

                case CpuPackageStyle.ArmSoc:
                    // a single package: the large die with the cores (4 performance, 4 efficiency) and the memory next to it
                    s.Add(R(8, 12, 116, 84, ArtFill.SubstrateEdge, 5));
                    s.Add(R(9, 13, 114, 82, ArtFill.Substrate, 4.5));
                    s.Add(R(18, 22, 56, 64, ArtFill.Die, 3));
                    s.Add(R(18, 22, 56, 5, ArtFill.DieShine, 3, 0.7));
                    for (int i = 0; i < 4; i++) s.Add(R(23 + i * 12.5, 32, 10, 10, ArtFill.Accent, 2, 0.95));          // performance cores
                    for (int i = 0; i < 4; i++) s.Add(R(23 + i * 12.5, 46, 10, 6, ArtFill.DieShine, 1.5, 0.75));       // efficiency cores
                    s.Add(R(23, 56, 46, 5, ArtFill.Accent, 1.5, 0.5));                                                 // graphics + the neural engine
                    if (!string.IsNullOrWhiteSpace(vendorText)) s.Add(T(23, 66, 46, 11, vendorText, ArtFill.White));
                    foreach (double y in new[] { 22.0, 58.0 })                                                          // the two memory packages
                    {
                        s.Add(R(84, y, 30, 28, ArtFill.Die, 2.5));
                        s.Add(R(84, y, 30, 4, ArtFill.DieShine, 2.5, 0.6));
                        s.Add(T(88, y + 10, 22, 8, "RAM", ArtFill.White));
                    }
                    s.Add(Corner(11, 94, 7));
                    break;

                default:    // LaptopAmd: a single chip, almost square
                    s.Add(R(16, 10, 100, 88, ArtFill.SubstrateEdge, 3.5));
                    s.Add(R(17, 11, 98, 86, ArtFill.Substrate, 3));
                    s.Add(R(42, 32, 48, 40, ArtFill.Die, 1.5));
                    s.Add(R(42, 32, 48, 6, ArtFill.DieShine, 1.5, 0.7));
                    for (int i = 0; i < 7; i++) s.Add(R(27 + i * 11.5, 20, 6, 3.2, ArtFill.Gold, 0.8, 0.85));
                    for (int i = 0; i < 7; i++) s.Add(R(27 + i * 11.5, 82, 6, 3.2, ArtFill.Gold, 0.8, 0.85));
                    for (int i = 0; i < 4; i++) s.Add(R(100, 34 + i * 10, 3.2, 6, ArtFill.Gold, 0.8, 0.7));
                    s.Add(Corner(19, 95, 7));
                    break;
            }
            return new ArtDrawing(Width, Height, s);
        }

        /// <summary>Engraving on the lid: manufacturer (large), model, two serial lines.</summary>
        private static void Engrave(List<ArtShape> s, double x, double y, double w, string vendor, string model, bool serialLines = true)
        {
            if (!string.IsNullOrWhiteSpace(vendor)) s.Add(T(x, y, w, 13, vendor));
            if (!string.IsNullOrWhiteSpace(model)) s.Add(T(x, y + 17, w, 9, model));
            if (!serialLines) return;
            s.Add(R(x + w * 0.18, y + 34, w * 0.64, 1.6, ArtFill.LidText, 0.8, 0.45));
            s.Add(R(x + w * 0.28, y + 39, w * 0.44, 1.6, ArtFill.LidText, 0.8, 0.45));
        }
    }

    /// <summary>Physical form of a disk when Windows does not say: SATA SSDs can be 2.5" or M.2 boards.</summary>
    public static class DriveForm
    {
        // known SATA models in M.2 form (manufacturer series and suffixes) + the explicit "M.2" mention
        private static readonly Regex RxM2Sata = new(
            @"\bM\.?2\b|\bMSATA\b" +
            @"|\bMZ-?N[A-Z0-9]" +                    // Samsung: MZNLN…, MZNTY…, MZ-N6E…
            @"|SSDSCK[A-Z]{2}" +                     // Intel: SSDSCKKF…, SSDSCKKW…
            @"|WDS\d+[GT]\d[BGR]0B" +                // WD Blue / Green / Red SA500 M.2 2280
            @"|CT\d+[A-Z]+\d+SSD[46]" +              // Crucial …SSD4 (M.2 2280), …SSD6 (M.2 2260)
            @"|S[AU][V4]?\d{3}M8" +                  // Kingston: SA400M8, SUV500M8
            @"|MTFDDAV" +                            // Micron: MTFDDAV = M.2 (MTFDDAK is 2.5 inch)
            @"|TS\d+[GT]MTS\d" +                     // Transcend MTS…
            @"|THNSN.\d+G(8N|VN8)|KSG\d0ZMV" +       // Toshiba / Kioxia M.2 SATA
            @"|SD[89]SN8" +                          // SanDisk X400 / X600 M.2
            @"|HFS\d+G39" +                          // SK hynix SC308 / SC311 M.2
            @"|CV\d-8[A-Z]\d|L8H-\d+V2G",           // Lite-On M.2
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>true if the model (of a SATA SSD) is an M.2 board, not a 2.5" disk.</summary>
        public static bool IsM2Sata(string? model) => !string.IsNullOrWhiteSpace(model) && RxM2Sata.IsMatch(model);

        /// <summary>
        /// The drawing that fits the disk: the type set by the app (<paramref name="kind"/>), with M.2-format SATA SSDs
        /// drawn as a board ("M.2 SATA SSD").
        /// </summary>
        public static string PictureKind(string? kind, string? model, bool laptop = false, string? formFactor = null)
        {
            // the form the disk reports about itself wins (DiskHealthEntry.FormFactor); the model and the PC type
            // are only the fallback
            if (kind is "SATA SSD" or "SSD" && (formFactor == "M.2" || (string.IsNullOrEmpty(formFactor) && IsM2Sata(model)))) return "M.2 SATA SSD";
            if (kind == "HDD" && formFactor == "3.5\"") return "HDD";
            if (kind == "HDD" && (formFactor == "2.5\"" || laptop || Is25Hdd(model))) return "HDD 2.5";
            return kind ?? "Unknown";
        }

        // 2.5" (laptop) HDD models: Seagate ST…LM / LX / LT / ST9…, WD …JP / LP / SP / BP / LU / BE, Toshiba MQ,
        // Hitachi / HGST HT…, Samsung HM…
        private static readonly Regex Rx25Hdd = new(
            @"\bST\d{3,5}(LM|LX|LT|MX)\d|\bST9\d{5}|\bWD\d{2,5}(JP|LP|SP|BP|LU|BE|SU|JU)|\bMQ\d{2}|\bHT[ESP]\d|\bHM\d{3}|Momentus|Scorpio|Travelstar",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>true if the HDD model is a 2.5" (laptop) one, not 3.5".</summary>
        public static bool Is25Hdd(string? model) => !string.IsNullOrWhiteSpace(model) && Rx25Hdd.IsMatch(model);
    }

    /// <summary>Disk state as the drawing shows it (housing colour and LED): from the health percentage.</summary>
    public enum DriveHealthState { Unknown, Good, Watch, Failing }

    public static class DriveHealth
    {
        /// <summary>Without SMART / NVMe data no state is invented: "Unknown". Above 90% good, above 60% watch, below that failing.</summary>
        public static DriveHealthState From(int healthPercent, bool hasData)
            => !hasData || healthPercent <= 0 ? DriveHealthState.Unknown
             : healthPercent >= 90 ? DriveHealthState.Good
             : healthPercent >= 60 ? DriveHealthState.Watch
             : DriveHealthState.Failing;
    }

    /// <summary>Arranges several drawings into one (a grid), so several objects appear in a single illustration.</summary>
    public static class ArtLayout
    {
        /// <summary>Moves and scales a shape: X, Y, W, H, radius and points.</summary>
        public static ArtShape Transform(ArtShape s, double scale, double dx, double dy)
            => s with
            {
                X = s.X * scale + dx, Y = s.Y * scale + dy, W = s.W * scale, H = s.H * scale, Radius = s.Radius * scale,
                Points = s.Points?.Select(p => (p.X * scale + dx, p.Y * scale + dy)).ToList(),
            };

        /// <summary>
        /// Places the drawings (at most <c>columns × rows</c>) in a <paramref name="width"/> × <paramref name="height"/> grid,
        /// each scaled uniformly and centred in its cell. A single drawing stays as is, centred and not enlarged beyond the box.
        /// </summary>
        public static ArtDrawing Tile(IReadOnlyList<ArtDrawing> items, double width, double height, int columns)
        {
            if (items.Count == 0) return new ArtDrawing(width, height, Array.Empty<ArtShape>());
            columns = Math.Clamp(columns, 1, items.Count);
            int rows = (items.Count + columns - 1) / columns;
            double cw = width / columns, ch = height / rows;
            var all = new List<ArtShape>();
            for (int i = 0; i < items.Count; i++)
            {
                var art = items[i];
                double k = Math.Min(cw / art.Width, ch / art.Height);
                double ox = (i % columns) * cw + (cw - art.Width * k) / 2, oy = (i / columns) * ch + (ch - art.Height * k) / 2;
                all.AddRange(art.Shapes.Select(sh => Transform(sh, k, ox, oy)));
            }
            return new ArtDrawing(width, height, all);
        }
    }
}
