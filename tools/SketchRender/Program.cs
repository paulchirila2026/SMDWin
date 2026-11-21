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
        var app = new SMDWin.App();
        app.InitializeComponent();
        try { SMDWin.Services.SettingsService.Current.EnableAnimations = false; } catch { }

        var sets = new (DeviceSketch.Kind kind, string file, bool dgpu, string[] hw, string[] val, double[] mark)[]
        {
            (DeviceSketch.Kind.Desktop, "1-desktop", true,
                new[] { "Intel Core i7-12700K · 12 cores", "32 GB DDR4-3200 · 2 modules", "C: · Samsung 980 PRO 1 TB", "NVIDIA GeForce RTX 3070" },
                new[] { "24 310 points · all cores", "41.2 GB/s · speed", "6 420 MB/s · sequential", "142 FPS · 3D render" },
                new[] { 8.6, 7.9, 9.2, 8.4 }),
            (DeviceSketch.Kind.Laptop, "2-laptop", true,
                new[] { "Intel Core i7-1165G7 · 4 cores", "16 GB DDR4-3200 · 2 modules", "C: · WD PC SN530 512 GB", "NVIDIA GeForce MX450" },
                new[] { "9 840 points · all cores", "28.6 GB/s · speed", "2 310 MB/s · sequential", "48 FPS · 3D render" },
                new[] { 6.4, 6.8, 7.6, 4.9 }),
            (DeviceSketch.Kind.AllInOne, "3-all-in-one", false,
                new[] { "Intel Core i5-4570 · 4 cores", "8 GB DDR3-1600 · 2 modules", "C: · KINGSTON SA400S37480G", "NVIDIA GeForce GT 755M" },
                new[] { "5 120 points · all cores", "17.9 GB/s · speed", "512 MB/s · sequential", "22 FPS · 3D render" },
                new[] { 4.6, 4.9, 6.1, 3.2 }),
            (DeviceSketch.Kind.MiniPc, "4-mini-pc", false,
                new[] { "Intel N100 · 4 cores", "16 GB DDR5-4800 · 1 module", "C: · 512 GB NVMe", "Intel UHD Graphics" },
                new[] { "4 680 points · all cores", "22.4 GB/s · speed", "1 960 MB/s · sequential", "18 FPS · 3D render" },
                new[] { 4.2, 5.6, 7.1, 2.6 }),
        };
        string[] titles = { "Processor", "Memory", "Storage", "Graphics card" };

        foreach (var theme in new[] { "Dark", "Light" })
        {
            SMDWin.Services.ThemeManager.Apply(theme, app.Resources);
            foreach (var s in sets)
            {
                var sk = new DeviceSketch { DeviceKind = s.kind, DedicatedGpu = s.dgpu, Width = 760, Height = 420 };
                for (int i = 0; i < 4; i++)
                {
                    sk.Parts[i].Title = titles[i];
                    sk.Parts[i].Hardware = s.hw[i];
                    sk.Parts[i].Value = s.val[i];
                    sk.Parts[i].Mark = s.mark[i];
                    sk.Parts[i].State = DeviceSketch.PartState.Done;
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
                var host = new Window { Content = page, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                                        ShowInTaskbar = false, Left = -10000, Top = -10000, AllowsTransparency = false };
                host.Show();
                page.UpdateLayout();
                sk.Refresh();
                host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                int w = (int)Math.Ceiling(page.ActualWidth), h = (int)Math.Ceiling(page.ActualHeight);
                var bmp = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                bmp.Render(page);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(Path.Combine(outDir, $"{s.file}-{theme.ToLowerInvariant()}.png"))) enc.Save(f);
                host.Close();
                Console.WriteLine($"{s.file} {theme} {w}x{h}");
            }
        }
    }
}
