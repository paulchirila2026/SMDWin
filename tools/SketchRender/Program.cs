// Renders the four benchmark drawings (desktop, laptop, all-in-one, mini PC) to PNG files, on Dark and Light.
// A helper for screenshots only; it lives on its own branch and is not part of the app.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SMDWin.Views;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "sketches";
        Directory.CreateDirectory(outDir);
        Console.WriteLine("start");
        var app = new SMDWin.App();
        app.InitializeComponent();
        Console.WriteLine("resources loaded");
        try { SMDWin.Services.SettingsService.Current.EnableAnimations = false; } catch { }

        DeviceSketch.Hardware H(int mods, int slots, bool nvme, bool sysNvme, bool optical, params bool[] bays)
            => new() { RamModules = mods, RamSlots = slots, Nvme = nvme, SystemOnNvme = sysNvme, Optical = optical, BayDrives = bays.ToList() };
        var sets = new (DeviceSketch.Kind kind, string file, bool dgpu, DeviceSketch.Hardware hw, string[] hwText)[]
        {
            (DeviceSketch.Kind.Desktop, "1a-desktop-full", true, H(2, 4, true, true, true, true, false, true),
                new[] { "Intel Core i7-12700K", "32 GB DDR4 · 2 of 4 slots", "C: · NVMe + 2 HDD + 1 SSD · DVD", "NVIDIA GeForce RTX 3070" }),
            (DeviceSketch.Kind.Desktop, "1b-desktop-simple", false, H(2, 2, false, false, false, false),
                new[] { "Intel Core i5-4570", "8 GB DDR3 · 2 of 2 slots", "C: · SATA SSD", "Intel HD Graphics 4600" }),
            (DeviceSketch.Kind.Laptop, "2a-laptop-old", true, H(1, 2, false, false, true, false),
                new[] { "Intel Core i7-6700HQ", "8 GB DDR4 · 1 of 2 slots", "C: · SATA SSD · DVD", "NVIDIA GeForce GTX 960M" }),
            (DeviceSketch.Kind.Laptop, "2b-laptop-new", true, H(2, 2, true, true, false),
                new[] { "Intel Core i7-1165G7", "16 GB DDR4 · 2 of 2 slots", "C: · NVMe", "NVIDIA GeForce MX450" }),
            (DeviceSketch.Kind.AllInOne, "3a-aio-full", false, H(2, 2, true, true, true, true),
                new[] { "Intel Core i5-4570", "8 GB DDR3 · 2 of 2 slots", "C: · NVMe + HDD · DVD", "Intel HD Graphics" }),
            (DeviceSketch.Kind.AllInOne, "3b-aio-simple", false, H(1, 2, false, false, false, false),
                new[] { "Intel Core i3-7100U", "4 GB DDR4 · 1 of 2 slots", "C: · SATA SSD", "Intel HD Graphics 620" }),
            (DeviceSketch.Kind.MiniPc, "4a-mini-full", false, H(2, 2, true, true, false, false),
                new[] { "Intel Core i5-8500T", "16 GB DDR4 · 2 of 2 slots", "C: · NVMe + SATA SSD", "Intel UHD Graphics 630" }),
            (DeviceSketch.Kind.MiniPc, "4b-mini-simple", false, H(1, 2, true, true, false),
                new[] { "Intel N100", "16 GB DDR5 · 1 of 1 slot", "C: · NVMe", "Intel UHD Graphics" }),
        };
        string[] titles = { "Processor", "Memory", "Storage", "Graphics card" };

        foreach (var theme in new[] { "Dark" })
        {
            SMDWin.Services.ThemeManager.Apply(theme, app.Resources);
            foreach (var s in sets)
            {
                var sk = new DeviceSketch { DeviceKind = s.kind, DedicatedGpu = s.dgpu, Fitted = s.hw, Width = 760, Height = 420 };
                for (int i = 0; i < 4; i++)
                {
                    sk.Parts[i].Title = titles[i];
                    sk.Parts[i].Hardware = s.hwText[i];
                    sk.Parts[i].Value = "";
                    sk.Parts[i].State = DeviceSketch.PartState.Idle;
                }
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(20),
                    Child = sk,
                };
                card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
                var page = new Border { Padding = new Thickness(24), Child = card };
                page.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
                // no window: measure and arrange off screen, then render
                Console.WriteLine($"render {s.file} {theme}…");
                page.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                page.Arrange(new Rect(page.DesiredSize));
                page.UpdateLayout();
                int w = (int)Math.Ceiling(page.ActualWidth), h = (int)Math.Ceiling(page.ActualHeight);
                var bmp = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                bmp.Render(page);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(Path.Combine(outDir, $"{s.file}-{theme.ToLowerInvariant()}.png"))) enc.Save(f);
                Console.WriteLine($"{s.file} {theme} {w}x{h}");
            }
        }
    }
}
