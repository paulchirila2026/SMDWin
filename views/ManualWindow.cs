using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WpfFontFamily = System.Windows.Media.FontFamily;
using Button        = System.Windows.Controls.Button;
using TextBlock     = System.Windows.Controls.TextBlock;
using ScrollViewer   = System.Windows.Controls.ScrollViewer;
using StackPanel     = System.Windows.Controls.StackPanel;
using Border         = System.Windows.Controls.Border;
using Orientation    = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    public class ManualWindow : Window
    {
        public class Tab
        {
            public string Title { get; set; } = "";
            public List<(string Heading, string Body)> Sections { get; set; } = new();
        }

        private readonly StackPanel _bodyPanel = new();
        private readonly List<Button> _tabButtons = new();
        private List<Tab> _tabs = new();
        private int _activeTab = 0;

        public ManualWindow()
        {
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            Title = "SMDWin — Manual";
            Width = 900; Height = 680;
            MinWidth = 640; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI");
            ShowInTaskbar = true;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            double radiusCardForWin = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 40, CornerRadius = new CornerRadius(radiusCardForWin),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                ResizeBorderThickness = new Thickness(4),
            });

            try
            {
                foreach (ResourceDictionary rd in System.Windows.Application.Current.Resources.MergedDictionaries)
                    Resources.MergedDictionaries.Add(rd);
                foreach (var key in System.Windows.Application.Current.Resources.Keys)
                    try { Resources[key] = System.Windows.Application.Current.Resources[key]; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (System.Windows.Application.Current.Resources["BgDarkBrush"] is System.Windows.Media.Brush bg)
                    Background = bg;
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }

            SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, SMDWin.Services.SettingsService.Current.ThemeName);
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            };

            _tabs = BuildTabs();
            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body (nav + content)

            var titleBar = new Grid { Height = 40 };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
            var titleTxt = new TextBlock { Text = "📖  SMDWin Manual", FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            titleTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var closeBtn = Net.NetWindowHelper.CloseIconButton(40);
            closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            closeBtn.Click += (_, _) => Close();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            titleBar.Children.Add(titleTxt);
            titleBar.Children.Add(closeBtn);
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            var bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); // side nav
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // content

            var navScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var nav = new StackPanel { Margin = new Thickness(10, 6, 6, 10) };
            for (int i = 0; i < _tabs.Count; i++)
            {
                int idx = i;
                // the tab names are translated (the help text itself is written in English for now); the emoji stays in front
                var btn = new Button { Content = TranslateTabTitle(_tabs[i].Title), HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2),
                    Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand };
                btn.Click += (_, _) => SelectTab(idx);
                _tabButtons.Add(btn);
                nav.Children.Add(btn);
            }
            navScroll.Content = nav;
            Grid.SetColumn(navScroll, 0);
            bodyGrid.Children.Add(navScroll);

            var contentScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 16, 24, 24) };
            contentScroll.Content = _bodyPanel;
            Grid.SetColumn(contentScroll, 1);
            bodyGrid.Children.Add(contentScroll);

            Grid.SetRow(bodyGrid, 1);
            root.Children.Add(bodyGrid);

            SelectTab(0);
            return root;
        }

        private void SelectTab(int idx)
        {
            _activeTab = idx;
            var res = System.Windows.Application.Current.Resources;
            for (int i = 0; i < _tabButtons.Count; i++)
            {
                bool active = i == idx;
                _tabButtons[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                if (active)
                {
                    _tabButtons[i].Foreground = res["SelBtnFg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
                    _tabButtons[i].Background = res["SelBtnBg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
                    _tabButtons[i].BorderBrush = res["SelBtnBorderBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
                    _tabButtons[i].BorderThickness = res["SelBtnBorderThickness"] as Thickness? ?? new Thickness(1);
                    _tabButtons[i].Effect = null;   // no shadow on a button with text: it blurs the text
                }
                else
                {
                    _tabButtons[i].ClearValue(Button.EffectProperty);
                    _tabButtons[i].BorderThickness = new Thickness(1);
                    _tabButtons[i].SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
                    _tabButtons[i].SetResourceReference(Button.BackgroundProperty, "BgDarkBrush");
                    _tabButtons[i].SetResourceReference(Button.BorderBrushProperty, "CardBorderBrush");
                }
            }

            _bodyPanel.Children.Clear();
            var tab = _tabs[idx];
            foreach (var (heading, body) in tab.Sections)
            {
                if (!string.IsNullOrEmpty(heading))
                {
                    var h = new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
                    h.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    _bodyPanel.Children.Add(h);
                }
                var b = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
                b.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                _bodyPanel.Children.Add(b);
            }
        }

        // ── Content ──────────────────────────────────────────────────────────
        // Short, direct, without an "AI assistant" tone — explained like a colleague who knows the program, not like a
        // technical manual or a chatbot.
        private static List<Tab> BuildTabs() => new()
        {
            new Tab { Title = "👋 Overview", Sections = new()
            {
                ("What is SMDWin?", "A hardware monitoring and diagnostics tool for Windows PCs and laptops. It reads temperatures, load, and other sensors directly from your hardware, runs stress tests, checks disk and RAM health, and helps troubleshoot problems — freezes, crashes, slow performance, driver issues."),
                ("How readings work", "Most numbers come from the LibreHardwareMonitor library, plus a small kernel driver (PawnIO) for direct access where Windows itself doesn't expose the data. If a sensor shows \"N/A\", it usually means your specific hardware doesn't expose that value — not a bug."),
                ("Is it safe?", "Reading sensors never changes anything on your PC. A handful of features do change settings (Auto-Clean, Optimize Performance, Power toggles) — each one is explained in its own tab below, with the exact command it runs. Nothing here modifies files without asking first."),
            }},

            new Tab { Title = "📊 Dashboard", Sections = new()
            {
                ("What you're looking at", "A live overview — CPU, GPU, RAM, disk, network, and ping, all updating every second. Click any card to jump to that page for more detail."),
                ("Chart range", "Right-click a chart for range (1 minute to 1 hour) and color options. The same setting applies to every chart on this page."),
            }},

            new Tab { Title = "🌡 Temp & Stress", Sections = new()
            {
                ("Live sensors", "The table on the left shows every temperature/load/power sensor your hardware exposes. Check a box to plot it on the right. Min/Max/Avg reset when you click \"Reset Min/Max\", not automatically."),
                ("Stress testing", "\"Start CPU Stress\" loads all cores at 100% using vectorized math (FMA) — genuinely demanding on modern CPUs, not a light synthetic load. It also checks the math itself: if the hardware computes something wrong under load (unstable overclock, bad RAM, a failing CPU), you'll see an \"integrity error\" count — a real, direct signal, independent of temperature."),
                ("Full Diagnostic", "Runs a complete pass: CPU/GPU stress with the integrity check above, RAM bandwidth/latency/a real memory test, disk health and benchmark, temperatures under load, battery, and internet speed. Takes a few minutes — it's meant to be thorough, not quick."),
            }},

            new Tab { Title = "💾 Storage", Sections = new() { ("Coming soon", "This tab isn't written yet — SMART health, benchmark, and Surface Scan explanations are next.") }},
            new Tab { Title = "🧠 RAM Memory", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},

            new Tab { Title = "🔋 Power", Sections = new()
            {
                ("Advanced Power toggles", "Processor Boost Mode controls turbo — Disabled runs cooler/quieter at reduced peak speed. USB Selective Suspend lets Windows power down idle USB ports; turning it off is a common fix for random mouse/keyboard disconnects. Fast Startup is a partial hibernate disguised as a shutdown — off gives you a genuinely clean boot each time, at the cost of a few extra seconds."),
                ("Processor State Range", "The two-handle slider limits how far the CPU can throttle down (left) or boost up (right). Lowering the right handle trades performance for less heat and a quieter fan."),
            }},

            new Tab { Title = "🧹 Tools", Sections = new()
            {
                ("Auto-Clean", "Deletes regenerable cache — temp files, prefetch, thumbnails. Windows rebuilds all of it automatically; nothing here is permanent except the Recycle Bin and Event Logs categories, both off by default for that reason."),
                ("Optimize Performance", "A checklist of common tweaks — disabling background services, telemetry, and visual animations. Every item is reversible. A few have real trade-offs (Windows Search gets slower if disabled, High Performance mode drains a laptop battery faster) — each checkbox explains its own trade-off when you hover it."),
            }},

            new Tab { Title = "🌐 Network", Sections = new() { ("Coming soon", "This tab isn't written yet — Wi-Fi Live, LAN Scanner, and the rest of the Network tools are next.") }},
            new Tab { Title = "📱 Applications", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "⚙ Windows Services", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "📈 Process Monitor", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "🚀 Startup Manager", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "📋 Event Viewer", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "💥 BSOD / Crash", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},
            new Tab { Title = "🔧 Drivers", Sections = new() { ("Coming soon", "This tab isn't written yet.") }},

            new Tab { Title = "⚠️ Capabilities & Limits", Sections = new()
            {
                ("Sensors — CPU & GPU", "CPU Package temp, core temps per-thread, frequency (GHz), load % per core, power consumption (W), VCore voltage, BDPROCHOT throttle detection. GPU temperature, load %, clock (MHz), VRAM used/total, hotspot temp (GDDR6X cards only)."),
                ("Sensors — Storage, RAM & Fans", "S.M.A.R.T. temperature and health %, wear level, real-time disk read/write speed. RAM used/available, usage %, frequency (MHz), XMP/EXPO profile detection. Fan RPM per channel, fan control detection via the embedded controller."),
                ("Libraries used", "LibreHardwareMonitor (sensor core), PawnIO (signed driver for sensor access), System.Management (WMI queries), PdfSharp (PDF export), SharpDX (GPU tests), Serilog (logs), .NET 8 WPF (Windows only)."),
                ("PawnIO — kernel driver", "Reads CPU MSR registers directly — enables VCore, power (W), and BDPROCHOT readings. Also accesses the embedded controller (EC) for fan RPM. Requires Administrator rights to load; without Admin, temperature and load still work, but MSR-based data (VCore, power) shows N/A."),
                ("Tools & Optimization", "Shutdown/Sleep/Hibernate timer with presets. Auto-Clean (Temp, Prefetch, WU cache, Recycle Bin). Startup Manager. Windows Services control. Driver Manager with update history. Integrated PowerShell runner."),
                ("Known limitations", "S.M.A.R.T. is unavailable on some NVMe controllers behind RAID mode. Wi-Fi saved passwords require Administrator privileges. GPU hotspot temperature only exists on GDDR6X cards (RTX 3000 series and up). Fan RPM via the embedded controller may not work on every motherboard."),
            }},
        };

        private static string TranslateTabTitle(string title)
        {
            int sp = title.IndexOf(' ');
            if (sp > 0 && sp <= 3 && !char.IsLetterOrDigit(title[0]))
                return title[..sp] + " " + SMDWin.Views.MainWindow.PT(title[(sp + 1)..]);
            return SMDWin.Views.MainWindow.PT(title);
        }
    }
}
