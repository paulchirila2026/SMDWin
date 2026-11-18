using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Button      = System.Windows.Controls.Button;
using CheckBox    = System.Windows.Controls.CheckBox;
using Color       = System.Windows.Media.Color;
using ComboBox    = System.Windows.Controls.ComboBox;
using HAlign      = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock   = System.Windows.Controls.TextBlock;
using UGrid       = System.Windows.Controls.Primitives.UniformGrid;
using VAlign      = System.Windows.VerticalAlignment;
using Point       = System.Windows.Point;
using Size        = System.Windows.Size;
using Pen         = System.Windows.Media.Pen;

namespace SMDWin.Views
{
    /// <summary>
    /// The Benchmark page (under Temp &amp; Stress): marks from 0 to 10 for the processor, the memory, the storage and the
    /// graphics, like the Windows 7 experience index; the general mark is their mean (BenchmarkScore). Made for a PC with
    /// problems, not for comparisons over time: the page keeps nothing between runs (the log and Data\Benchmark do).
    /// Layout (mockup "bench-pc") — the page fills the window height:
    ///   banner: before the run what is tested, the warnings and Start; while running the step, the total progress and the
    ///     4 steps; after the run the time, the highest temperatures, whether the processor slowed down from heat,
    ///     "Copy results" and "Run again";
    ///   the hero card: this computer drawn in lines (DeviceSketch — desktop, laptop, all-in-one or mini PC) with a callout per
    ///     component (mark ring, hardware, value; the part being tested in orange, the parts coloured by their mark after),
    ///     and on the right the general dial + "Performance class" (the class name, what it suits, a continuous red → green
    ///     bar 0–10 with ticks and a pin at the mark);
    ///   one card per component: a small mark ring in the header, the main value, the weakest component outlined, each test
    ///     with its value, its own mark and an ⓘ tooltip; the storage card chooses the drive.
    /// Tests: processor Mandelbrot + FFT on one thread and on all threads · memory read speed + latency · storage: the quick
    /// disk benchmark of the chosen drive (256 MB temporary file, deleted at the end) · graphics: 3 tests × 13 s.
    /// </summary>
    public partial class MainWindow
    {
        private Grid? _panelBenchmark;
        private Border? _benchBanner;
        private ScoreDial? _benchAllDial;
        private TextBlock? _benchClassName, _benchClassDesc;
        private Canvas? _benchPin, _benchTicks, _benchNames;
        private UGrid? _benchCards;
        // graphics adapter chosen for the test (laptops with two); -1 = Windows' default
        private List<GpuSuiteBenchmark.AdapterInfo>? _benchGpuAdapters;
        private int _benchGpuAdapter = -1;
        /// <summary>How much of its speed the processor kept at the end of the processor step (last seconds / first 2 s).</summary>
        private double? _benchSustained;
        private bool _benchKeysHooked;
        private DeviceSketch? _benchSketch;   // the computer drawn in lines, with a callout per component
        private ScrollViewer? _benchScroll;
        private bool _benchFitQueued;

        /// <summary>
        /// The drawing takes the height left by the banner and the cards, so the whole page fits on a laptop screen
        /// (a ScrollViewer gives its content unlimited height, so a star row alone would not shrink it); 240–500 px.
        /// </summary>
        private void FitBenchSketchSoon()
        {
            if (_benchFitQueued) return;
            _benchFitQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _benchFitQueued = false;
                if (_benchScroll == null || _benchSketch == null || _benchBanner == null || _benchCards == null) return;
                double free = _benchScroll.ViewportHeight - 6 - (_benchBanner.ActualHeight + 14) - _benchCards.ActualHeight - 14 - 24;
                double h = Math.Clamp(free, 240, 500);
                if (Math.Abs(_benchSketch.MaxHeight - h) > 2) _benchSketch.MaxHeight = h;
            }), DispatcherPriority.Loaded);
        }
        private readonly BenchComp[] _benchComps = new BenchComp[4];
        private CancellationTokenSource? _benchCts;
        private bool _benchRunning;
        private string _benchDoneText = "";
        private readonly List<(string text, string? brush)> _benchDoneChips = new();
        private int _benchStep = -1;
        private string _benchSub = "", _benchHudLeft = "", _benchHudRight = "";
        private DispatcherTimer? _benchTicker;
        private Stopwatch? _benchClock;
        private double _benchStepStart;
        private float _benchMaxCpuTemp, _benchMaxGpuTemp, _benchCpuMhzMax, _benchCpuMhzMinLate;
        private string _benchDrive = "";
        private bool _benchThemeHooked;
        // everything measured, for the log and the self-test (Services/BenchmarkRecord.cs)
        private BenchmarkRecord? _benchRec;
        private int _benchTickCount;
        /// <summary>The file of the last run (Data\Benchmark), for the self-test.</summary>
        internal string? LastBenchmarkFile { get; private set; }
        // the running banner, rebuilt each run
        private TextBlock? _benchTxtNow, _benchTxtSub, _benchTxtPct, _benchTxtLeft;
        private ColumnDefinition? _benchBarOn, _benchBarOff;
        private readonly (TextBlock state, ColumnDefinition on, ColumnDefinition off, Border card, Border fill)[] _benchStepUi = new (TextBlock, ColumnDefinition, ColumnDefinition, Border, Border)[4];

        // expected seconds per component (the progress bar and the time left); the disk depends on the drive
        private static readonly double[] BenchExpected = { 30, 16, 35, 42 };
        private const double GpuSecondsPerTest = 13;
        // the processor value: geometric mean of Mandelbrot work and FFT, each divided by what one core of an i5-4570 does
        // (estimated from its first runs: 277 old pts on one core); one i5-4570 core ≈ 1.0
        private const double CpuMbNorm = 400_000_000, CpuFftNorm = 5_500;
        private static double CpuGeo(double mandelbrotWork, double fft) => Math.Sqrt(Math.Max(0, mandelbrotWork / CpuMbNorm) * Math.Max(0, fft / CpuFftNorm));

        /// <summary>One component card: its tests (name, tooltip, shown value, mark) and its mark.</summary>
        private sealed class BenchComp
        {
            public string Name = "", Device = "";
            public string[] Tests = Array.Empty<string>();
            public string[] Tips = Array.Empty<string>();
            public string?[] Values = Array.Empty<string?>();
            public double?[] Marks = Array.Empty<double?>();
            public double? Mark;
            public MarkRing Ring = null!;
            public TextBlock DeviceText = null!, WeakText = null!, MainValue = null!, MainCaption = null!;
            public int MainTest;   // the test whose value is shown under the dial
            public TextBlock AdviceText = null!, ExtraText = null!;
            public bool[] Suspect = Array.Empty<bool>();
            public double[] Raw = Array.Empty<double>();    // the measured value of each test (for the plausibility check)   // the value looked wrong: not used in the mark (shown with "?")
            public TextBlock[] ValueTexts = Array.Empty<TextBlock>(), MarkTexts = Array.Empty<TextBlock>();
            public UGrid[] Bars = Array.Empty<UGrid>();
            public Border Card = null!;
        }

        private Grid CreateBenchmarkPanel()
        {
            if (_panelBenchmark != null) return _panelBenchmark;
            _panelBenchmark = new Grid { Visibility = Visibility.Collapsed };
            _panelBenchmark.SetResourceReference(FrameworkElement.MarginProperty, "PageMargin");
            MainContentBorder.Children.Add(_panelBenchmark);
            return _panelBenchmark;
        }

        // ── numbers in the app language (decimal comma in ro / de / fr) ─────────────────────────────────────────────────
        private static CultureInfo BenchCulture => LanguageService.CurrentCode switch
        {
            "ro" => CultureInfo.GetCultureInfo("ro-RO"),
            "de" => CultureInfo.GetCultureInfo("de-DE"),
            "fr" => CultureInfo.GetCultureInfo("fr-FR"),
            _    => CultureInfo.InvariantCulture,
        };
        private static string BNum(double v, string fmt = "0.0") => v.ToString(fmt, BenchCulture);

        /// <summary>Builds the page the first time it is opened (later visits keep it, with the last results).</summary>
        private void EnsureBenchmarkPage()
        {
            var host = CreateBenchmarkPanel();
            if (host.Children.Count > 0) { RefreshBenchmarkView(); return; }
            if (!_benchThemeHooked)
            {
                _benchThemeHooked = true;
                ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (_benchBanner != null) RefreshBenchmarkView(); }));
            }

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // the page fills the window: the drawing row takes the free height (MinHeight = the visible height), the cards their own
            var root = new Grid { Margin = new Thickness(0, 0, 4, 4) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 300 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            scroll.Content = root;
            scroll.SizeChanged += (_, _) => { root.MinHeight = Math.Max(0, scroll.ViewportHeight - 6); FitBenchSketchSoon(); };
            host.Children.Add(scroll);
            _benchScroll = scroll;

            _benchBanner = new Border { Margin = new Thickness(0, 0, 0, 14) };
            _benchBanner.SizeChanged += (_, _) => FitBenchSketchSoon();
            root.Children.Add(_benchBanner);
            // the hero card: the computer with its callouts on the left, the general mark and the performance class on the right
            var top = BenchCard();
            top.Margin = new Thickness(0, 0, 0, 14);
            top.Padding = new Thickness(12, 10, 20, 10);
            Grid.SetRow(top, 1);
            var hero = new Grid();
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 330 });
            _benchSketch = new DeviceSketch { MaxHeight = 500, Format = v => BNum(v), VerticalAlignment = VAlign.Center };
            hero.Children.Add(_benchSketch);
            var right = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(20, 6, 0, 6), Margin = new Thickness(8, 0, 0, 0) };
            right.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetColumn(right, 1);
            var allSp = new StackPanel { VerticalAlignment = VAlign.Center };
            allSp.Children.Add(BenchCaption(PT("General mark"), HAlign.Center));
            _benchAllDial = new ScoreDial { Width = 160, Height = 140, Segments = 20, Format = v => BNum(v), Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HAlign.Center };
            allSp.Children.Add(_benchAllDial);
            allSp.Children.Add(BuildBenchClassPanel());
            right.Child = allSp;
            hero.Children.Add(right);
            top.Child = hero;
            root.Children.Add(top);
            _ = DetectBenchDeviceKindAsync();

            // the component cards: 4 per row, 2 on a narrow window; they stretch to the bottom of the page
            _benchCards = new UGrid { Columns = 4 };
            _benchCards.SizeChanged += (_, _) => FitBenchSketchSoon();
            Grid.SetRow(_benchCards, 2);
            root.Children.Add(_benchCards);
            host.SizeChanged += (_, e) => { if (_benchCards != null) _benchCards.Columns = e.NewSize.Width < 980 ? 2 : 4; };

            var s = _summary;
            _benchDrive = SystemDriveLetter();
            InitBenchComp(0, PT("Processor"), BenchCpuDevice(),
                          new[] { PT("One core"), PT("All cores"), PT("Compression") },
                          new[] { PT("Calculations on a single thread (floating point and integers)"), PT("The same calculations on all threads at once"),
                                  PT("Compressing documents and logs on all threads — close to everyday work") });
            InitBenchComp(1, PT("Memory"), BenchRamDevice(),
                          new[] { PT("Speed"), PT("Latency") },
                          new[] { PT("How fast the memory is read, in a long row"), PT("How long one random access takes — lower is better") });
            InitBenchComp(2, PT("Storage"), BenchDiskDevice(_benchDrive),
                          new[] { PT("Sequential"), PT("4K random"), PT("Write") },
                          new[] { PT("Reading a large file in one go"), PT("Small reads one at a time — how responsive the system feels"),
                                  PT("Writing a large file — cheap SSDs slow down a lot here") });
            _benchGpuAdapters = GpuSuiteBenchmark.Adapters();
            if (_benchGpuAdapter < 0) _benchGpuAdapter = GpuSuiteBenchmark.DefaultAdapterIndex(_benchGpuAdapters);
            InitBenchComp(3, PT("Graphics card"), BenchGpuName() is { Length: > 0 } gn ? gn : s?.GpuName ?? "",
                          new[] { PT("3D render"), PT("Compute"), PT("Texture / memory") },
                          new[] { PT("A 3D picture with lighting, 1920 × 1080"), PT("Floating-point operations (FP32)"), PT("Copying data in the card's memory") });
            _benchComps[0].MainTest = 1;   // processor: all cores; the others: their first test
            for (int i = 0; i < 4; i++) _benchCards.Children.Add(BuildBenchCompCard(_benchComps[i], i));

            HookBenchKeys();
            RefreshBenchmarkView();
        }

        private bool _benchDiskSlowWrite;
        private string? _benchDiskSkipReason;   // why the storage test did not measure (shown on the card)
        private List<string>? _benchLastPre;   // the warnings when the last test started
        private bool _benchTurboWasOff;        // Turbo was off during the last test (the advice says so instead of blaming the processor)
        private (double Fps, double GFlops, double MemGBs) _benchGpuRaw;

        /// <summary>
        /// Values that cannot be right (a measuring problem, not a slow PC) are marked "?" and left out of the mark (
        /// "1 GB/s" on a GT 755M once put the whole card at 1.0). Conservative limits, only clear cases.
        /// </summary>
        private void BenchCheckSuspect()
        {
            void Flag(BenchComp c, int t, bool bad)
            {
                if (!bad || c.Values.Length <= t || c.Values[t] == null) return;
                c.Suspect[t] = true;
                c.Marks[t] = null;
                AppLogger.Warning("Benchmark: {0} / {1} = {2} looks wrong — not counted", c.Name, c.Tests[t], c.Values[t]);
                _benchRec?.Details.Add($"Suspect value (not counted): {c.Name} / {c.Tests[t]} = {c.Values[t]}");
            }
            var cpu = _benchComps[0];
            // all cores slower than one core: something else was using the processor
            if (Environment.ProcessorCount > 1)
                Flag(cpu, 1, cpu.Raw[0] > 0 && cpu.Raw[1] > 0 && cpu.Raw[1] < cpu.Raw[0] * 0.9);
            var ram = _benchComps[1];
            Flag(ram, 0, ram.Raw[0] > 0 && ram.Raw[0] < 1.5);
            Flag(ram, 1, ram.Raw[1] > 400);
            var disk = _benchComps[2];
            Flag(disk, 0, disk.Raw[0] > 0 && disk.Raw[0] < 3);
            var (fps, gf, mem) = _benchGpuRaw;
            var gpu = _benchComps[3];
            // memory is no longer compared with compute — a wrong compute value (7 455 GFLOPS on an Intel UHD) marked
            // a right memory value. Instead: compute far above what the 3D test allows (real cards: 40–70 GFLOPS per frame/s)
            Flag(gpu, 2, mem > 0 && mem < 2);
            Flag(gpu, 1, gf > 0 && fps > 0 && gf > fps * 400);
            Flag(gpu, 0, fps > 0 && fps < 1 && gf > 200);
        }

        /// <summary>
        /// One short piece of advice for a component after the test — what a technician would tell the customer.
        /// Only when there is something useful to say.
        /// </summary>
        private string? BenchAdvice(int i)
        {
            var c = _benchComps[i];
            if (i == 2 && _benchDiskSkipReason != null && !_benchRunning) return _benchDiskSkipReason;
            if (c?.Mark is not double mark) return null;
            switch (i)
            {
                case 0:
                    if (_benchTurboWasOff) return PT("Measured with Turbo off — the processor can do much more. Turn Turbo on and run the test again.");
                    if (_benchLastPre?.Count > 0 && BenchOnBattery() && mark < 4) return PT("Measured on battery — plug in the charger and run the test again for the real mark.");
                    if (_benchSustained is double keep && keep < 0.8) return PT("It slows down under load: clean the cooler and replace the thermal paste.");
                    if (mark < 3) return PT("The processor is the limit: fine for web and documents, slow for heavy programs.");
                    return null;
                case 1:
                    if (_summary is { RamTotalGB: > 0 and < 7.5f }) return PT("Under 8 GB of memory: Windows 11 and the browser run short — add memory.");
                    if (BenchSingleModule()) return PT("One memory module (single channel): a second, identical module would make the memory and the integrated graphics much faster.");
                    if (c.Marks[0] is double sp && sp < 4) return PT("Slow memory: check that two modules are fitted (dual channel).");
                    return null;
                case 2:
                    if (BenchDiskKind(_benchDrive) == "hdd") return PT("A hard disk: an SSD would make Windows start and respond much faster.");
                    if (mark < 4) return PT("A slow SSD: a faster one would help.");
                    if (_benchDiskSlowWrite || c.Marks[2] is double w && w < 3.5) return PT("Writing slows down a lot: a budget SSD — fine for everyday use, slow for large copies.");
                    return null;
                case 3:
                    if (mark < 3.5) return PT("Fine for office work and video, weak for games and 3D.");
                    return null;
            }
            return null;
        }

        private string BenchGpuName() => _benchGpuAdapters?.FirstOrDefault(a => a.Index == _benchGpuAdapter)?.Name ?? "";

        /// <summary>Esc stops a running test.</summary>
        private void HookBenchKeys()
        {
            if (_benchKeysHooked) return;
            _benchKeysHooked = true;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && _benchRunning && _currentPanel == "Benchmark") { _benchCts?.Cancel(); e.Handled = true; }
            };
            // plugging / unplugging the charger: the warnings of the start card follow
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_benchRunning && _benchBanner != null && _currentPanel == "Benchmark") RefreshBenchmarkView();
            }));
        }

        /// <summary>
        /// What can make the marks lower than the PC really is: on battery (laptops slow the processor down and
        /// often use the integrated graphics), the "Power saver" plan, other programs already using the processor.
        /// </summary>
        private List<string> BenchPreChecks()
        {
            var list = new List<string>();
            if (BenchOnBattery())
                list.Add(PT("On battery — plug in the charger. On battery the processor is slowed down and laptops often use the integrated graphics."));
            if (BenchTurboOff())
                list.Add(PT("Turbo is off in the power plan — the processor stays at its base speed and the marks come out much lower."));
            try
            {
                if (string.Equals(PowerAdvancedService.ActiveSchemeGuid(), "a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
                    list.Add(PT("The power plan is \"Power saver\" — the marks come out lower. Choose Balanced or High performance."));
            }
            catch (Exception ex) { AppLogger.Debug($"Benchmark power plan: {ex.Message}"); }
            if (BenchDiskSpaceShort(_benchDrive) is string spaceMsg) list.Add(spaceMsg);
            if (BenchRecentCpuLoad() is float load && load >= 15)
                list.Add(PT("The processor is already busy ({0} %) — close the programs that run in the background.", load.ToString("0")));
            return list;
        }

        /// <summary>One line of the check before the test: a warning (amber) or a condition that is fine (green).</summary>
        private sealed record BenchCheck(string Glyph, bool Ok, string Title, string Hint, string Full, bool IsTurbo = false);

        /// <summary>
        /// The check before the test, as tiles (mockup "warn" A): power, Turbo, power plan, free space, processor load —
        /// each one green when fine or amber with what to do. The same conditions as BenchPreChecks (which the record keeps).
        /// </summary>
        private List<BenchCheck> BenchReadiness()
        {
            var list = new List<BenchCheck>();
            if (BenchHasBattery())
                list.Add(BenchOnBattery()
                    ? new("\uE850", false, PT("On battery"), PT("Plug in the charger"), PT("On battery — plug in the charger. On battery the processor is slowed down and laptops often use the integrated graphics."))
                    : new("\uE945", true, PT("Plugged in"), PT("Charger connected"), ""));
            bool turboOff = BenchTurboOff();
            list.Add(turboOff
                ? new("\uEC4A", false, PT("Turbo is off"), PT("Marks come out lower"), PT("Turbo is off in the power plan — the processor stays at its base speed and the marks come out much lower."), IsTurbo: true)
                : new("\uEC4A", true, PT("Turbo on"), PT("Full processor speed"), ""));
            try
            {
                if (string.Equals(PowerAdvancedService.ActiveSchemeGuid(), "a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
                    list.Add(new("\uE7E8", false, PT("Power saver plan"), PT("Choose Balanced"), PT("The power plan is \"Power saver\" — the marks come out lower. Choose Balanced or High performance.")));
            }
            catch (Exception ex) { AppLogger.Debug($"Benchmark power plan: {ex.Message}"); }
            list.Add(BenchDiskSpaceShort(_benchDrive) is string spaceMsg
                ? new("\uEDA2", false, PT("Not enough free space"), PT("The storage test is skipped"), spaceMsg)
                : new("\uEDA2", true, PT("Enough free space"), PT("For the storage test"), ""));
            if (BenchRecentCpuLoad() is float load && load >= 15)
                list.Add(new("\uE9D9", false, PT("Processor busy · {0} %", load.ToString("0")), PT("Close background programs"),
                             PT("The processor is already busy ({0} %) — close the programs that run in the background.", load.ToString("0"))));
            else
                list.Add(new("\uE9D9", true, PT("Processor idle"), PT("Nothing heavy running"), ""));
            return list;
        }

        private Brush BenchTint(string key, byte alpha)
        {
            var c = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Orange;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze(); return b;
        }

        /// <summary>
        /// One tile: a small tinted square with the icon, the title in the status colour and the advice under it; tinted
        /// background, no outline. The Turbo tile is also the switch: a click turns "Turbo for the test" on or off and the
        /// tile turns green ("Turbo on for the test · put back afterwards") or amber again.
        /// </summary>
        private Border BenchCheckTile(BenchCheck c)
        {
            var tile = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconBox = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VAlign.Center };
            var icon = new TextBlock { Text = c.Glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"), FontSize = 13,
                                       HorizontalAlignment = HAlign.Center, VerticalAlignment = VAlign.Center };
            iconBox.Child = icon;
            g.Children.Add(iconBox);
            var text = new StackPanel { VerticalAlignment = VAlign.Center };
            var title = new TextBlock { FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold };
            var hint = new TextBlock { FontSize = Ui.FontXS, Margin = new Thickness(0, 1, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(title); text.Children.Add(hint);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            tile.Child = g;

            Border? track = null; Border? knob = null;
            if (c.IsTurbo)
            {
                track = new Border { Width = 30, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VAlign.Center, BorderThickness = new Thickness(1) };
                knob = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VAlign.Center, Margin = new Thickness(2, 0, 2, 0) };
                track.Child = knob;
                Grid.SetColumn(track, 2);
                g.Children.Add(track);
                tile.Cursor = System.Windows.Input.Cursors.Hand;
                tile.MouseLeftButtonUp += (_, _) => { _benchTurboForTest = !_benchTurboForTest; Paint(); };
            }

            void Paint()
            {
                bool ok = c.Ok || (c.IsTurbo && _benchTurboForTest);
                string key = ok ? "StatusGoodBrush" : "StatusCheckBrush";
                tile.Background = BenchTint(key, 26);
                iconBox.Background = BenchTint(key, 34);
                icon.SetResourceReference(TextBlock.ForegroundProperty, key);
                title.SetResourceReference(TextBlock.ForegroundProperty, key);
                if (c.IsTurbo && _benchTurboForTest)
                {
                    title.Text = PT("Turbo on for the test");
                    hint.Text = PT("Put back afterwards");
                }
                else { title.Text = c.Title; hint.Text = c.Hint; }
                tile.ToolTip = c.IsTurbo
                    ? c.Full + "\n\n" + (_benchTurboForTest ? PT("Click to leave Turbo off for the test.") : PT("Click to turn Turbo on for the test (the setting is put back afterwards)."))
                    : c.Full.Length > 0 ? c.Full : null;
                if (track != null && knob != null)
                {
                    knob.HorizontalAlignment = _benchTurboForTest ? HAlign.Right : HAlign.Left;
                    if (_benchTurboForTest)
                    {
                        track.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                        track.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                        knob.Background = Brushes.White;
                    }
                    else
                    {
                        track.Background = Brushes.Transparent;
                        track.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
                        knob.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
                    }
                }
            }
            Paint();
            return tile;
        }

        /// <summary>
        /// After a test: the conditions it ran in as small soft pills (tinted, no outline) — the part before " — " of each
        /// warning, the whole sentence in the tooltip.
        /// </summary>
        private void AddBenchWarningChips(System.Windows.Controls.Panel host, List<string> warnings)
        {
            foreach (var w in warnings)
            {
                int cut = w.IndexOf(" — ", StringComparison.Ordinal);
                string label = cut > 0 ? w[..cut] : w;
                var b = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 11, 4), Margin = new Thickness(0, 2, 6, 2),
                                     ToolTip = w, Background = BenchTint("StatusCheckBrush", 26), VerticalAlignment = VAlign.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VAlign.Center };
                dot.SetResourceReference(Border.BackgroundProperty, "StatusCheckBrush");
                var tb = new TextBlock { Text = label, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "StatusCheckBrush");
                sp.Children.Add(dot); sp.Children.Add(tb);
                b.Child = sp;
                host.Children.Add(b);
            }
        }

        /// <summary>"8 GB DDR4-2667 · 1 module" (the card had no description — the summary was not read yet when
        /// the page was built; UpdateBenchCards asks again while it is empty).</summary>
        private string BenchRamDevice()
        {
            var s = _summary;
            var mods = BenchRamModules();
            // "—" on the start card — the summary was not read yet; the modules give the total and the type too
            double totalGb = s?.RamTotalGB > 0 ? s.RamTotalGB : mods.Sum(m => (double)m.CapacityBytes) / (1024.0 * 1024 * 1024);
            if (totalGb <= 0) return "";
            int mhz = mods.Select(m => m.ConfiguredMHz > 0 ? m.ConfiguredMHz : m.RatedMHz).DefaultIfEmpty(0).Max();
            // RamType is the whole label ("DDR4 · 1 × 8 GB · 2667 MHz"): only its first part is the type
            string type = (s?.RamType ?? "").Split('·')[0].Trim();
            if (type.Length == 0) type = mods.Select(m => (m.MemoryType ?? "").Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            string head = $"{BNum(totalGb, "0")} GB {type}{(mhz > 0 && type.Length > 0 ? "-" + mhz : mhz > 0 ? " " + mhz + " MHz" : "")}".Trim();
            if (mods.Count > 0 && !IsSolderedRam(mods)) head += " · " + (mods.Count == 1 ? PT("1 module") : PT("{0} modules", mods.Count));
            return head;
        }

        /// <summary>The installed memory modules (the RAM page's list). If the RAM page was not opened yet, it is read once in the
        /// background and the cards are updated when it arrives.</summary>
        private List<RamEntry> BenchRamModules()
        {
            if (_ramModules.Count == 0 && !_benchRamLoading)
            {
                _benchRamLoading = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var list = await _hwService.GetRamAsync();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_ramModules.Count == 0) _ramModules = list;
                            if (_benchComps[1] != null) _benchComps[1].Device = BenchRamDevice();
                            UpdateBenchCards();
                        });
                    }
                    catch (Exception ex) { AppLogger.Debug($"Benchmark RAM modules: {ex.Message}"); }
                });
            }
            return _ramModules.Where(m => !m.IsEmpty).ToList();
        }
        private bool _benchRamLoading;

        /// <summary>One memory module in a slot (not soldered): it runs in single channel — the most useful advice on such PCs.</summary>
        private bool BenchSingleModule()
        {
            var mods = BenchRamModules();
            if (mods.Count != 1 || IsSolderedRam(mods)) return false;
            try { return SMDWin.Services.RamDiagnosticsService.DetectChannelMode(mods).Status == SMDWin.Services.DualChannelStatus.SingleChannel; }
            catch { return true; }
        }

        private static bool BenchOnBattery()
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                bool hasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0
                                  && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
                return hasBattery && ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            }
            catch { return false; }
        }

        /// <summary>
        /// Turbo is off for the current power source (ThinkPad E14: "Cool &amp; quiet" on the Power page kept the
        /// i5-10210U at 1.6 GHz instead of 4.2 — the marks were half of what the processor can do and the advice blamed it).
        /// Off = boost mode Disabled, or the maximum processor state under 100 % (which also stops Turbo).
        /// </summary>
        private static bool BenchTurboOff()
        {
            try
            {
                bool ac = !BenchOnBattery();
                var boost = PowerAdvancedService.GetBoostMode(ac);
                int? max = PowerAdvancedService.GetMaxProcessorState(ac);
                return boost == PowerAdvancedService.BoostMode.Disabled || (max is int m && m < 100);
            }
            catch { return false; }
        }

        private DateTime _turboOffAt = DateTime.MinValue;
        private bool _turboOffCached;
        /// <summary>BenchTurboOff, read at most every 10 s (used by the live cards, every second).</summary>
        private bool TurboOffCached()
        {
            if ((DateTime.UtcNow - _turboOffAt).TotalSeconds > 10) { _turboOffCached = BenchTurboOff(); _turboOffAt = DateTime.UtcNow; }
            return _turboOffCached;
        }

        /// <summary>
        /// Too little free space for the storage test (C: had 1.4 GB free, the test needs its file + a reserve of 2 %
        /// of the disk, at least 1 GB — it stopped silently and the card showed "0 MB/s"). Same rule as DiskBenchmarkService.
        /// </summary>
        private string? BenchDiskSpaceShort(string? drive)
        {
            try
            {
                string d = string.IsNullOrEmpty(drive) ? SystemDriveLetter() : drive;
                var di = new DriveInfo(d.TrimEnd('\\') + "\\");
                long reserve = Math.Max(1L << 30, di.TotalSize / 50);
                long required = (128L + 64) * 1024 * 1024 + reserve;
                if (di.AvailableFreeSpace >= required) return null;
                return PT("Not enough free space on {0} for the storage test: {1} free, {2} needed — the storage test will be skipped.",
                          d.TrimEnd('\\'), DisplayFormat.SizeGb(di.AvailableFreeSpace / 1073741824.0), DisplayFormat.SizeGb(required / 1073741824.0));
            }
            catch { return null; }
        }

        /// <summary>The average processor load over the last 5 seconds (the Dashboard history), not one instant reading.</summary>
        private float? BenchRecentCpuLoad()
        {
            int n = 0; float sum = 0;
            for (int k = 1; k <= 5 && k <= _dbHead; k++)
            {
                float v = _dbCpu[((_dbHead - k) % DashBufSize + DashBufSize) % DashBufSize];
                if (float.IsFinite(v)) { sum += v; n++; }
            }
            return n > 0 ? sum / n : _lastTempSnap?.CpuLoadPct;
        }

        /// <summary>"Turn on Turbo for the test" (checked by default when Turbo is off); the setting is put back at the end.</summary>
        private bool _benchTurboForTest = true;
        private (int? BoostAc, int? BoostDc, int? MaxAc, int? MaxDc)? _benchTurboSaved;

        private void BenchTurboOnForTest(BenchmarkRecord rec)
        {
            try
            {
                TurboRestore.RememberOriginalIfNeeded(TurboRestore.Part.Turbo);   // put back at the next start if the app stops now
                _benchTurboSaved = ((int?)PowerAdvancedService.GetBoostMode(true), (int?)PowerAdvancedService.GetBoostMode(false),
                                    PowerAdvancedService.GetMaxProcessorState(true), PowerAdvancedService.GetMaxProcessorState(false));
                PowerAdvancedService.SetBoostMode(PowerAdvancedService.BoostMode.Enabled, applyToAc: true, applyToDc: true);
                PowerAdvancedService.SetMaxProcessorState(100, applyToAc: true, applyToDc: true);
                rec.Details.Add($"Turbo turned on for the test (was: boost AC {_benchTurboSaved.Value.BoostAc} DC {_benchTurboSaved.Value.BoostDc}, max AC {_benchTurboSaved.Value.MaxAc} DC {_benchTurboSaved.Value.MaxDc})");
                AppLogger.Info("Benchmark: Turbo turned on for the test");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Benchmark: turning Turbo on"); _benchTurboSaved = null; }
        }

        private void BenchTurboRestore()
        {
            if (_benchTurboSaved is not { } sv) return;
            _benchTurboSaved = null;
            try
            {
                if (sv.BoostAc is int bac && Enum.IsDefined(typeof(PowerAdvancedService.BoostMode), bac))
                    PowerAdvancedService.SetBoostMode((PowerAdvancedService.BoostMode)bac, applyToAc: true, applyToDc: false);
                if (sv.BoostDc is int bdc && Enum.IsDefined(typeof(PowerAdvancedService.BoostMode), bdc))
                    PowerAdvancedService.SetBoostMode((PowerAdvancedService.BoostMode)bdc, applyToAc: false, applyToDc: true);
                if (sv.MaxAc is int mac) PowerAdvancedService.SetMaxProcessorState(mac, applyToAc: true, applyToDc: false);
                if (sv.MaxDc is int mdc) PowerAdvancedService.SetMaxProcessorState(mdc, applyToAc: false, applyToDc: true);
                TurboRestore.ClearIfBackToOriginal(TurboRestore.Part.Turbo);
                AppLogger.Info("Benchmark: Turbo setting put back");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Benchmark: putting Turbo back"); }
        }

        private string BenchCpuDevice()
        {
            var s = _summary;
            if (s == null || string.IsNullOrWhiteSpace(s.Cpu)) return "";
            int threads = s.CpuThreads > 0 ? s.CpuThreads : Environment.ProcessorCount;
            return $"{s.Cpu.Trim()} · {threads} {PT("threads")}";
        }

        private static string SystemDriveLetter()
        {
            try { return (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\'); }
            catch { return "C:"; }
        }

        /// <summary>The physical disk that holds the drive letter (for its name and the picture of the animation).</summary>
        private DiskHealthEntry? BenchDiskOf(string letter)
        {
            string l = letter.TrimEnd('\\', ':').ToUpperInvariant();
            try { return _allDisks.FirstOrDefault(d => d.Partitions.Any(p => p.Letter.TrimEnd('\\', ':').ToUpperInvariant() == l)); }
            catch { return null; }
        }

        private string BenchDiskDevice(string letter)
        {
            var d = BenchDiskOf(letter);
            return d == null ? letter : $"{letter} · {d.Model.Trim()}";
        }

        /// <summary>The drive type for the animation: "hdd", "nvme", "m2sata" or "sata25".</summary>
        private string BenchDiskKind(string letter)
        {
            var d = BenchDiskOf(letter);
            if (d == null) return "sata25";
            string kind = d.Kind ?? "";
            if (kind.Contains("HDD", StringComparison.OrdinalIgnoreCase) || d.RotationRpm > 0) return "hdd";
            if (kind.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || d.Bus == "NVMe") return "nvme";
            if ((d.FormFactor ?? "").Contains("M.2", StringComparison.OrdinalIgnoreCase)) return "m2sata";
            return "sata25";
        }

        private void InitBenchComp(int i, string name, string device, string[] tests, string[] tips)
        {
            _benchComps[i] = new BenchComp
            {
                Name = name, Device = device, Tests = tests, Tips = tips,
                Values = new string?[tests.Length], Marks = new double?[tests.Length], Suspect = new bool[tests.Length], Raw = new double[tests.Length],
            };
        }

        private Border BenchCard()
        {
            var b = new Border { Padding = new Thickness(16, 14, 16, 14) };
            b.SetResourceReference(StyleProperty, "CardStyle");
            // the large cards of this page do not light up under the mouse (a local background wins over the style's hover)
            b.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            return b;
        }

        private TextBlock BenchCaption(string text, HAlign align)
        {
            var t = new TextBlock { Text = text.ToUpper(BenchCulture), FontSize = Ui.FontXS, FontWeight = FontWeights.Bold, HorizontalAlignment = align };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return t;
        }

        /// <summary>The mark's colour for TEXT: on Light the yellow / light-green part of the scale had 2:1 contrast on white
        /// — the same hue, 40 % darker.</summary>
        private static Brush ScaleTextBrush(double mark, bool light)
        {
            var (r, g, b) = BenchmarkScore.ScaleColor(mark);
            if (light) { r = (byte)(r * 0.6); g = (byte)(g * 0.6); b = (byte)(b * 0.6); }
            var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br;
        }

        private static Brush ScaleBrush(double mark)
        {
            var (r, g, b) = BenchmarkScore.ScaleColor(mark);
            var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br;
        }

        // ── performance class: name, what it suits, the continuous bar with ticks, names and the pin ───────────────────
        private FrameworkElement BuildBenchClassPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(BenchCaption(PT("Performance class"), HAlign.Center));
            _benchClassName = new TextBlock { FontSize = Ui.FontXXL, FontWeight = FontWeights.Bold, HorizontalAlignment = HAlign.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            _benchClassName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            _benchClassDesc = new TextBlock { FontSize = Ui.FontS, HorizontalAlignment = HAlign.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            _benchClassDesc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(_benchClassName); sp.Children.Add(_benchClassDesc);

            var scale = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            scale.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });   // pin
            scale.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // bar
            scale.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });    // separators between the classes
            scale.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });   // names (two lines when a class is narrow)
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            foreach (var (at, r, g, b) in new[] { (0.0, 220, 38, 38), (0.25, 249, 115, 22), (0.47, 234, 179, 8), (0.68, 132, 204, 22), (1.0, 22, 163, 74) })
                grad.GradientStops.Add(new GradientStop(Color.FromRgb((byte)r, (byte)g, (byte)b), at));
            grad.Freeze();
            var bar = new Border { Height = 14, CornerRadius = new CornerRadius(7), Background = grad };
            Grid.SetRow(bar, 1);
            scale.Children.Add(bar);
            _benchPin = new Canvas { ClipToBounds = false, IsHitTestVisible = false };
            Grid.SetRow(_benchPin, 0); Grid.SetRowSpan(_benchPin, 2);
            _benchTicks = new Canvas { IsHitTestVisible = false };
            Grid.SetRow(_benchTicks, 2);
            _benchNames = new Canvas { Background = Brushes.Transparent };   // hit-testable: the class names have tooltips
            Grid.SetRow(_benchNames, 3);
            scale.Children.Add(_benchTicks); scale.Children.Add(_benchNames); scale.Children.Add(_benchPin);
            scale.SizeChanged += (_, _) => UpdateBenchClass();
            sp.Children.Add(scale);
            return sp;
        }

        private void UpdateBenchClass()
        {
            if (_benchPin == null || _benchTicks == null || _benchNames == null || _benchClassName == null || _benchClassDesc == null) return;
            double? all = _benchRunning ? null : BenchmarkScore.Mean(_benchComps.Select(c => c?.Mark));
            var cur = all is double a ? BenchmarkScore.TierOf(a) : null;
            _benchClassName.Text = cur != null ? PT(cur.Name) : "—";
            _benchClassDesc.Text = cur != null ? PT("Suitable for: {0}", PT(cur.GoodFor)) : _benchRunning ? PT("running…") : PT("worked out after the test");
            double w = _benchTicks.ActualWidth;
            if (w <= 0) return;

            // no numbers under the bar (the mark is in the dial and in the pin) — only a thin line where a class ends
            _benchTicks.Children.Clear();
            foreach (var tier in BenchmarkScore.Tiers.Take(BenchmarkScore.Tiers.Length - 1))
            {
                double x = tier.To / 10 * w;
                var tick = new Border { Width = 1, Height = 6, Opacity = 0.6 };
                tick.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
                Canvas.SetLeft(tick, Math.Clamp(x, 0, w - 1)); Canvas.SetTop(tick, 2);
                _benchTicks.Children.Add(tick);
            }

            _benchNames.Children.Clear();
            foreach (var t in BenchmarkScore.Tiers)
            {
                bool on = cur == t;
                // a name wider than its class wraps on two lines ("High / Performance") instead of being cut
                var n = new TextBlock { Text = PT(t.Name), FontSize = Ui.FontS, FontWeight = on ? FontWeights.Bold : FontWeights.Normal,
                                        TextWrapping = TextWrapping.WrapWithOverflow, TextAlignment = TextAlignment.Center, LineHeight = Ui.FontS + 3,
                                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                n.SetResourceReference(TextBlock.ForegroundProperty, on ? "TextPrimaryBrush" : "TextSecondaryBrush");
                double center = (t.From + t.To) / 2 / 10 * w, span = (t.To - t.From) / 10 * w;
                n.ToolTip = PT(t.Name) + "\n" + PT("Suitable for: {0}", PT(t.GoodFor));
                n.Background = Brushes.Transparent;
                n.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double nw = n.DesiredSize.Width;
                if (nw > span - 4)
                {
                    // never narrower than the longest word (words are not split)
                    double word = 0;
                    foreach (var part in n.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var probe = new TextBlock { Text = part, FontSize = n.FontSize, FontWeight = n.FontWeight };
                        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        word = Math.Max(word, probe.DesiredSize.Width);
                    }
                    nw = Math.Max(span - 4, Math.Ceiling(word) + 1);
                }
                n.Width = nw;
                Canvas.SetLeft(n, Math.Clamp(center - nw / 2, 0, w - nw)); Canvas.SetTop(n, 2);
                _benchNames.Children.Add(n);
            }

            // the pin: value bubble, a stem down to the bar, a ring on the bar — exactly at the mark
            _benchPin.Children.Clear();
            if (all is double mark)
            {
                double x = Math.Clamp(mark / 10 * w, 0, w);
                var bubble = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 1, 8, 2) };
                bubble.SetResourceReference(Border.BackgroundProperty, "TextPrimaryBrush");
                var bt = new TextBlock { Text = BNum(mark), FontSize = Ui.FontS, FontWeight = FontWeights.Bold };
                bt.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
                bt.SetResourceReference(TextBlock.ForegroundProperty, "BgCardBrush");
                bubble.Child = bt;
                bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double bw = bubble.DesiredSize.Width, bh = bubble.DesiredSize.Height;
                Canvas.SetLeft(bubble, Math.Clamp(x - bw / 2, 0, Math.Max(0, w - bw))); Canvas.SetTop(bubble, 0);
                var stem = new Border { Width = 2, Height = Math.Max(2, 34 - bh + 2) };
                stem.SetResourceReference(Border.BackgroundProperty, "TextPrimaryBrush");
                Canvas.SetLeft(stem, x - 1); Canvas.SetTop(stem, bh - 1);
                var ring = new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, StrokeThickness = 3 };
                ring.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BgCardBrush");
                ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimaryBrush");
                Canvas.SetLeft(ring, x - 7); Canvas.SetTop(ring, 34);   // centred on the 14 px bar (row 1 starts at 34)
                _benchPin.Children.Add(stem); _benchPin.Children.Add(bubble); _benchPin.Children.Add(ring);
            }
        }

        // ── component cards ─────────────────────────────────────────────────────────────────────────────────────────────
        private Border BuildBenchCompCard(BenchComp c, int index)
        {
            var card = BenchCard();
            card.Margin = new Thickness(index % 4 == 0 ? 0 : 7, 0, index % 4 == 3 ? 0 : 7, 0);
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            card.Child = g;

            var head = new DockPanel();
            // the mark ring (the same as the callout of the drawing) at the right of the header
            c.Ring = new MarkRing { Width = 46, Height = 46, Format = v => BNum(v), VerticalAlignment = VAlign.Top, Margin = new Thickness(8, -2, 0, 0) };
            DockPanel.SetDock(c.Ring, Dock.Right);
            head.Children.Add(c.Ring);
            if (index == 2)
            {
                // the storage card chooses the drive (fixed, ready drives; the system drive first)
                var cb = new ComboBox { MinWidth = 64, VerticalAlignment = VAlign.Top, Margin = new Thickness(8, 0, 0, 0), ToolTip = PT("Drive to test") };
                try
                {
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).OrderBy(d => d.Name))
                        cb.Items.Add(d.Name.TrimEnd('\\'));
                }
                catch (Exception ex) { AppLogger.Debug($"Benchmark drives: {ex.Message}"); }
                if (!cb.Items.Contains(_benchDrive)) cb.Items.Insert(0, _benchDrive);
                cb.SelectedItem = _benchDrive;
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is string l && !_benchRunning) { _benchDrive = l; c.Device = BenchDiskDevice(l); UpdateBenchCards(); }
                };
                DockPanel.SetDock(cb, Dock.Right);
                head.Children.Add(cb);
            }
            else if (index == 3 && _benchGpuAdapters is { Count: > 1 } adapters)
            {
                // two graphics adapters (laptop): choose which one is tested; the dedicated one by default
                var cb = new ComboBox { MaxWidth = 150, VerticalAlignment = VAlign.Top, Margin = new Thickness(8, 0, 0, 0), ToolTip = PT("Graphics card to test") };
                foreach (var a in adapters) cb.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Index, ToolTip = a.DedicatedMB > 0 ? $"{a.Name} · {a.DedicatedMB} MB" : a.Name });
                cb.SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Index == _benchGpuAdapter));
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is ComboBoxItem it && it.Tag is int ai && !_benchRunning)
                    {
                        _benchGpuAdapter = ai; c.Device = BenchGpuName();
                        Array.Clear(c.Values); Array.Clear(c.Marks); Array.Clear(c.Suspect); c.Mark = null;   // the results were of the other card
                        UpdateBenchCards();
                    }
                };
                DockPanel.SetDock(cb, Dock.Right);
                head.Children.Add(cb);
            }
            var titles = new StackPanel();
            var name = new TextBlock { Text = c.Name, FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            titles.Children.Add(name);
            c.DeviceText = new TextBlock { Text = c.Device, FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.Device };
            c.DeviceText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            titles.Children.Add(c.DeviceText);
            head.Children.Add(titles);
            g.Children.Add(head);

            // the main value of the component ("11.912 pts  all cores"); "—  not tested" before the test
            var mainSp = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
            c.MainValue = new TextBlock { FontSize = Ui.FontTitle, FontWeight = FontWeights.SemiBold, VerticalAlignment = VAlign.Bottom, Margin = new Thickness(0, 0, 8, 0) };
            c.MainValue.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
            c.MainValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            c.MainCaption = new TextBlock { FontSize = Ui.FontS, VerticalAlignment = VAlign.Bottom, Margin = new Thickness(0, 0, 0, 3), Text = c.Tests.Length > c.MainTest ? c.Tests[c.MainTest] : "" };
            c.MainCaption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            mainSp.Children.Add(c.MainValue); mainSp.Children.Add(c.MainCaption);
            Grid.SetRow(mainSp, 1);
            g.Children.Add(mainSp);

            // under the value: "weakest component", the advice for this component (if any) and, for the processor, how much of
            // its speed it kept under load
            var notes = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            c.WeakText = new TextBlock { Text = "▲ " + PT("weakest component"), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
            c.WeakText.SetResourceReference(TextBlock.ForegroundProperty, "StatusCheckBrush");
            notes.Children.Add(c.WeakText);
            c.AdviceText = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
            c.AdviceText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            notes.Children.Add(c.AdviceText);
            c.ExtraText = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            notes.Children.Add(c.ExtraText);
            Grid.SetRow(notes, 2);
            g.Children.Add(notes);

            var tests = new StackPanel();
            var sepBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = tests };
            sepBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetRow(sepBorder, 3);
            g.Children.Add(sepBorder);
            c.ValueTexts = new TextBlock[c.Tests.Length];
            c.MarkTexts = new TextBlock[c.Tests.Length];
            c.Bars = new UGrid[c.Tests.Length];
            for (int i = 0; i < c.Tests.Length; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keyLine = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
                var k = new TextBlock { Text = c.Tests[i], FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VAlign.Center };
                k.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                keyLine.Children.Add(k);
                row.Children.Add(keyLine);
                // what the test measures, written under its bar (it was a tooltip; the cards have the room for it)
                if (c.Tips.Length > i && c.Tips[i].Length > 0)
                {
                    var tip = new TextBlock { Text = c.Tips[i], FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Opacity = 0.8 };
                    tip.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    Grid.SetRow(tip, 2); Grid.SetColumnSpan(tip, 3);
                    row.Children.Add(tip);
                }
                var v = new TextBlock { Text = "—", FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VAlign.Center };
                v.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
                v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetColumn(v, 1);
                row.Children.Add(v);
                c.ValueTexts[i] = v;
                var m = new TextBlock { FontSize = Ui.FontS, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right, VerticalAlignment = VAlign.Center };
                m.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
                Grid.SetColumn(m, 2);
                row.Children.Add(m);
                c.MarkTexts[i] = m;
                var bar = new UGrid { Rows = 1, Columns = 10, Height = 5, Margin = new Thickness(0, 5, 0, 0) };
                for (int j = 0; j < 10; j++)
                {
                    var blk = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(j == 0 ? 0 : 1, 0, j == 9 ? 0 : 1, 0) };
                    blk.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                    bar.Children.Add(blk);
                }
                Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 3);
                row.Children.Add(bar);
                c.Bars[i] = bar;
                tests.Children.Add(row);
            }
            c.Card = card;
            return card;
        }

        // ══ the view ═══════════════════════════════════════════════════════════════════════════════════════════════════

        private void RefreshBenchmarkView()
        {
            if (_benchBanner == null) return;
            // before the first run the same page, with the drawing and the cards still empty, and Start in the banner
            if (_benchBanner.Child != null) PinStylesBeforeDetach(_benchBanner.Child);
            _benchBanner.Child = _benchRunning ? BuildBenchRunningBanner() : BuildBenchIdleBanner();
            UpdateBenchCards();
        }

        private void UpdateBenchCards()
        {
            if (_benchAllDial == null) return;
            double? all = _benchRunning ? null : BenchmarkScore.Mean(_benchComps.Select(c => c?.Mark));
            _benchAllDial.Score = all;
            _benchAllDial.Caption = all != null ? PT("of 10") : _benchRunning ? PT("running…") : PT("not tested yet");
            int weakest = -1;
            if (!_benchRunning && _benchComps.Count(c => c?.Mark != null) >= 2)
            {
                double low = double.MaxValue;
                for (int i = 0; i < 4; i++) if (_benchComps[i]?.Mark is double m && m < low) { low = m; weakest = i; }
            }
            for (int i = 0; i < 4; i++)
            {
                var c = _benchComps[i];
                if (c == null) continue;
                bool now = _benchRunning && _benchStep == i;
                bool next = _benchRunning && _benchStep < i;
                c.Ring.Mark = c.Mark;
                c.Ring.Active = now;
                c.Card.Opacity = next ? 0.6 : 1;
                if (i == 1 && (string.IsNullOrEmpty(c.Device) || !c.Device.Contains('·'))) { var rd = BenchRamDevice(); if (rd.Length > 0) c.Device = rd; }
                c.DeviceText.Text = c.Device;
                c.DeviceText.ToolTip = string.IsNullOrEmpty(c.Device) ? null : c.Device;
                string? main = c.Values.Length > c.MainTest ? c.Values[c.MainTest] : null;
                c.MainValue.Text = main ?? "—";
                c.MainCaption.Text = main != null ? c.Tests[c.MainTest] : now ? PT("running…") : next ? PT("next") : PT("not tested yet");
                bool weak = i == weakest;
                c.WeakText.Visibility = weak ? Visibility.Visible : Visibility.Collapsed;
                if (weak) { c.Card.SetResourceReference(Border.BorderBrushProperty, "StatusCheckBrush"); c.Card.BorderThickness = new Thickness(1.5); }
                else { c.Card.ClearValue(Border.BorderBrushProperty); c.Card.ClearValue(Border.BorderThicknessProperty); }
                string? advice = _benchRunning ? null : BenchAdvice(i);
                c.AdviceText.Text = advice ?? "";
                c.AdviceText.Visibility = advice != null ? Visibility.Visible : Visibility.Collapsed;
                if (i == 0 && !_benchRunning && _benchSustained is double keep)
                {
                    int pct = (int)Math.Round(Math.Min(1, keep) * 100);
                    c.ExtraText.Text = PT("Under load it keeps {0} % of its speed", pct);
                    c.ExtraText.ToolTip = PT("The speed of all cores at the end of the processor test, compared with the first seconds. Much lower = it slows down from heat or power limits.");
                    c.ExtraText.SetResourceReference(TextBlock.ForegroundProperty, pct >= 90 ? "StatusGoodBrush" : pct >= 80 ? "StatusCheckBrush" : "StatusProblemBrush");
                    c.ExtraText.Visibility = Visibility.Visible;
                }
                else c.ExtraText.Visibility = Visibility.Collapsed;
                for (int t = 0; t < c.Tests.Length; t++)
                {
                    bool sus = c.Suspect.Length > t && c.Suspect[t];
                    c.ValueTexts[t].Text = (sus ? "⚠ " : "") + (c.Values[t] ?? "—");
                    c.ValueTexts[t].ToolTip = sus ? PT("This value looks wrong, so it is not counted in the mark. Run the test again.") : null;
                    if (sus) c.ValueTexts[t].SetResourceReference(TextBlock.ForegroundProperty, "StatusCheckBrush");
                    else c.ValueTexts[t].SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    double? m = c.Marks[t];
                    c.MarkTexts[t].Text = m is double mm0 ? BNum(mm0) : sus ? "?" : "";
                    if (m is double mm1) c.MarkTexts[t].Foreground = ScaleTextBrush(mm1, IsLightThemeActive());
                    else if (sus) c.MarkTexts[t].SetResourceReference(TextBlock.ForegroundProperty, "StatusCheckBrush");
                    for (int j = 0; j < 10; j++)
                    {
                        var blk = (Border)c.Bars[t].Children[j];
                        if (m is double mm && j < Math.Round(mm)) blk.Background = ScaleBrush(mm);
                        else blk.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                    }
                }
            }
            UpdateBenchClass();
            UpdateBenchSketch();
        }

        /// <summary>
        /// Gives every element of a subtree that is about to be removed its current style as a local value. Removed from the
        /// window, an element looks its style up again and, not finding it, loses its template; a template animation that is
        /// held (the Turbo check box after it was ticked) then throws ArgumentNullException('defaultDestinationValue') and
        /// the click on Start ended in the error window. With the style pinned nothing is looked up again.
        /// </summary>
        private static void PinStylesBeforeDetach(DependencyObject root)
        {
            try
            {
                if (root is FrameworkElement fe && fe.Style is { } st) fe.Style = st;
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++) PinStylesBeforeDetach(VisualTreeHelper.GetChild(root, i));
            }
            catch (Exception ex) { AppLogger.Debug($"PinStylesBeforeDetach: {ex.Message}"); }
        }

        // ── banners ─────────────────────────────────────────────────────────────────────────────────────────────────────

        private UIElement BuildBenchIdleBanner()
        {
            var card = BenchCard();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool done = _benchDoneText.Length > 0;
            if (done)
            {
                var wrap = new WrapPanel { VerticalAlignment = VAlign.Center };
                var title = new TextBlock { Text = "✓  " + _benchDoneText, FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 12, 2), VerticalAlignment = VAlign.Center };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                wrap.Children.Add(title);
                foreach (var (text, brush) in _benchDoneChips)
                {
                    var chip = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(0, 2, 6, 2) };
                    chip.SetResourceReference(Border.BorderBrushProperty, brush ?? "CardBorderBrush");
                    var ct = new TextBlock { Text = text, FontSize = Ui.FontS };
                    ct.SetResourceReference(TextBlock.ForegroundProperty, brush ?? "TextSecondaryBrush");
                    chip.Child = ct;
                    wrap.Children.Add(chip);
                }
                // after a test: the conditions it ran in (not a new reading — the load is still high right after it)
                AddBenchWarningChips(wrap, _benchLastPre ?? new List<string>());
                if (BenchTurboOff())
                {
                    var turbo = BenchCheckTile(new BenchCheck("\uEC4A", false, PT("Turbo is off"), PT("Marks come out lower"),
                        PT("Turbo is off in the power plan — the processor stays at its base speed and the marks come out much lower."), IsTurbo: true));
                    turbo.Margin = new Thickness(4, 2, 0, 2);
                    turbo.VerticalAlignment = VAlign.Center;
                    wrap.Children.Add(turbo);
                }
                g.Children.Add(wrap);
            }
            else
            {
                var sp = new StackPanel { VerticalAlignment = VAlign.Center };
                var title = new TextBlock { Text = PT("Marks from 0 to 10 for the processor, memory, storage and graphics card · about 2.5 minutes"), FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                sp.Children.Add(title);
                var sub = new TextBlock { Text = PT("Close other programs during the test. The storage test writes a 256 MB temporary file and deletes it at the end."), FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(sub);
                var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, -6) };
                foreach (var check in BenchReadiness()) chips.Children.Add(BenchCheckTile(check));
                sp.Children.Add(chips);
                g.Children.Add(sp);
            }
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center, Margin = new Thickness(16, 0, 0, 0) };
            if (done)
            {
                var copy = new Button { Content = "⧉  " + PT("Copy results"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), ToolTip = PT("Copies the marks and values as text — for the customer or the service sheet") };
                copy.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
                copy.Click += (_, _) =>
                {
                    try { System.Windows.Clipboard.SetText(BenchSummaryText()); ShowToast(PT("Results copied")); }
                    catch (Exception ex) { AppLogger.Debug($"Benchmark copy: {ex.Message}"); }
                };
                btns.Children.Add(copy);
            }
            var btn = new Button { Content = "▶  " + (done ? PT("Run again") : PT("Start")), Padding = new Thickness(18, 8, 18, 8) };
            btn.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            btn.Click += async (_, _) => await RunBenchmarkAsync();
            btns.Children.Add(btn);
            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);
            card.Child = g;
            return card;
        }

        /// <summary>The results as plain text (Copy results): the PC, the general mark and class, every component and test.</summary>
        private string BenchSummaryText()
        {
            var sb = new StringBuilder();
            var s = _summary;
            sb.AppendLine($"SMD Win · {PT("Benchmark")} · {DateTime.Now:dd.MM.yyyy HH:mm}");
            if (s != null) sb.AppendLine(string.Join(" · ", new[] { $"{s.Manufacturer} {s.Model}".Trim(), s.Cpu, s.GpuName }.Where(x => !string.IsNullOrWhiteSpace(x))));
            double? all = BenchmarkScore.Mean(_benchComps.Select(c => c.Mark));
            if (all is double a) sb.AppendLine($"{PT("General mark")}: {BNum(a)} / 10 · {PT("Performance class")}: {PT(BenchmarkScore.TierOf(a).Name)}");
            sb.AppendLine();
            foreach (var c in _benchComps)
            {
                sb.AppendLine($"{c.Name}: {(c.Mark is double m ? BNum(m) : "—")} / 10{(c.Device.Length > 0 ? "  (" + c.Device + ")" : "")}");
                for (int i = 0; i < c.Tests.Length; i++)
                    sb.AppendLine($"   {c.Tests[i]}: {c.Values[i] ?? "—"}{(c.Marks[i] is double tm ? "  → " + BNum(tm) : "")}");
            }
            if (_benchDoneChips.Count > 0) sb.AppendLine().AppendLine(string.Join(" · ", _benchDoneChips.Select(x => x.text)));
            return sb.ToString();
        }

        private UIElement BuildBenchRunningBanner()
        {
            // the part being tested is shown by the drawing below (DeviceSketch); the banner has the step, the progress and the steps
            var outer = BenchCard();
            var info = new StackPanel();
            outer.Child = info;

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var stop = new Button { Content = "■  " + PT("Stop"), Padding = new Thickness(14, 6, 14, 6) };
            stop.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            stop.Click += (_, _) => _benchCts?.Cancel();
            DockPanel.SetDock(stop, Dock.Right);
            head.Children.Add(stop);
            var titles = new WrapPanel { VerticalAlignment = VAlign.Center };
            _benchTxtNow = new TextBlock { FontSize = Ui.FontXXL, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            _benchTxtNow.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            _benchTxtSub = new TextBlock { FontSize = Ui.FontM, VerticalAlignment = VAlign.Bottom, Margin = new Thickness(0, 0, 0, 2) };
            _benchTxtSub.SetResourceReference(TextBlock.ForegroundProperty, "DataBrush");
            titles.Children.Add(_benchTxtNow); titles.Children.Add(_benchTxtSub);
            head.Children.Add(titles);
            info.Children.Add(head);

            // the total progress
            var total = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            total.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            total.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            total.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = new Grid { Height = 10, VerticalAlignment = VAlign.Center };
            _benchBarOn = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
            _benchBarOff = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_benchBarOn); bar.ColumnDefinitions.Add(_benchBarOff);
            var trackB = new Border { CornerRadius = new CornerRadius(5) };
            trackB.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
            Grid.SetColumnSpan(trackB, 2);
            bar.Children.Add(trackB);
            var fillB = new Border { CornerRadius = new CornerRadius(5) };
            fillB.SetResourceReference(Border.BackgroundProperty, "DataBrush");
            bar.Children.Add(fillB);
            total.Children.Add(bar);
            _benchTxtPct = new TextBlock { FontSize = Ui.FontL, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 0, 0), MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VAlign.Center };
            _benchTxtPct.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
            _benchTxtPct.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(_benchTxtPct, 1);
            total.Children.Add(_benchTxtPct);
            _benchTxtLeft = new TextBlock { FontSize = Ui.FontS, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VAlign.Center };
            _benchTxtLeft.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(_benchTxtLeft, 2);
            total.Children.Add(_benchTxtLeft);
            info.Children.Add(total);

            // the 4 steps
            var steps = new UGrid { Rows = 1, Columns = 4 };
            for (int i = 0; i < 4; i++)
            {
                var st = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 3 ? 0 : 4, 0) };
                st.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                var sp = new StackPanel();
                var nm = new TextBlock { Text = _benchComps[i].Name, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                nm.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                sp.Children.Add(nm);
                var state = new TextBlock { FontSize = Ui.FontXS, Margin = new Thickness(0, 2, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis };
                sp.Children.Add(state);
                var pb = new Grid { Height = 4 };
                var on = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
                var off = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                pb.ColumnDefinitions.Add(on); pb.ColumnDefinitions.Add(off);
                var tr = new Border { CornerRadius = new CornerRadius(2) };
                tr.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                Grid.SetColumnSpan(tr, 2);
                pb.Children.Add(tr);
                var fill = new Border { CornerRadius = new CornerRadius(2) };
                pb.Children.Add(fill);
                sp.Children.Add(pb);
                st.Child = sp;
                steps.Children.Add(st);
                _benchStepUi[i] = (state, on, off, st, fill);
            }
            info.Children.Add(steps);
            var note = new TextBlock { Text = PT("The rest of the app stays usable, but other programs should be closed during the test."), FontSize = Ui.FontXS, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            info.Children.Add(note);
            UpdateBenchRunning();
            return outer;
        }


        /// <summary>The live part of the running banner (every 250 ms): titles, total progress, steps, live values.</summary>
        private void UpdateBenchRunning()
        {
            if (!_benchRunning || _benchTxtNow == null || _benchClock == null) return;
            int step = Math.Clamp(_benchStep, 0, 3);
            double totalExp = BenchExpected.Sum();
            double inStep = _benchClock.Elapsed.TotalSeconds - _benchStepStart;
            double stepFrac = Math.Min(0.97, inStep / BenchExpected[step]);
            double done = BenchExpected.Take(step).Sum() + BenchExpected[step] * stepFrac;
            double pct = Math.Clamp(done / totalExp, 0, 0.99);
            double left = Math.Max(5, BenchExpected.Skip(step).Sum() - Math.Min(inStep, BenchExpected[step] * 0.97));

            _benchTxtNow.Text = PT("Now: {0}", _benchComps[step].Name);
            _benchTxtSub!.Text = _benchSub;
            _benchTxtPct!.Text = $"{pct * 100:0}%";
            _benchTxtLeft!.Text = PT("~{0} s left", Math.Round(left).ToString("0"));
            _benchBarOn!.Width = new GridLength(Math.Max(0.001, pct), GridUnitType.Star);
            _benchBarOff!.Width = new GridLength(Math.Max(0.001, 1 - pct), GridUnitType.Star);
            UpdateBenchSketch();
            for (int i = 0; i < 4; i++)
            {
                var (state, on, off, card, fill) = _benchStepUi[i];
                if (state == null) continue;
                double f = i < step ? 1 : i == step ? Math.Max(0.02, stepFrac) : 0;
                on.Width = new GridLength(Math.Max(0.001, f), GridUnitType.Star);
                off.Width = new GridLength(Math.Max(0.001, 1 - f), GridUnitType.Star);
                fill.SetResourceReference(Border.BackgroundProperty, i < step ? "StatusGoodBrush" : "DataBrush");
                card.SetResourceReference(Border.BorderBrushProperty, i == step ? "DataBrush" : "CardBorderBrush");
                state.Text = i < step ? (_benchComps[i].Mark is double m ? PT("done · {0}", BNum(m)) : PT("done"))
                           : i == step ? _benchSub : PT("next");
                state.SetResourceReference(TextBlock.ForegroundProperty, i < step ? "StatusGoodBrush" : "TextSecondaryBrush");
            }
        }

        /// <summary>Every 250 ms while running: the live values from the sensors, the highest temperatures, the clock range.</summary>
        private void BenchTick()
        {
            var snap = _lastTempSnap;
            // one sensor row per second for the record
            if (_benchRec != null && ++_benchTickCount % 4 == 0 && _benchClock != null)
                _benchRec.Timeline.Add(string.Join(",",
                    _benchClock.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture), _benchStep,
                    BenchmarkRecord.Num(snap?.CpuTemp), BenchmarkRecord.Num(snap?.CpuFreqMHz), BenchmarkRecord.Num(snap?.CpuLoadPct), BenchmarkRecord.Num(snap?.CpuPowerW, "0.0"),
                    BenchmarkRecord.Num(snap?.GpuTemp), BenchmarkRecord.Num(snap?.GpuFreqMHz), BenchmarkRecord.Num(snap?.GpuLoadPct), BenchmarkRecord.Num(snap?.GpuPowerW, "0.0")));
            if (snap?.CpuTemp is float ct && ct > _benchMaxCpuTemp) _benchMaxCpuTemp = ct;
            if (snap?.GpuTemp is float gt && gt > _benchMaxGpuTemp) _benchMaxGpuTemp = gt;
            // the processor clock during the processor step (after 3 s): a big drop under load = slowed down from heat / power
            if (_benchStep == 0 && _benchClock != null && _benchClock.Elapsed.TotalSeconds - _benchStepStart > 3 && snap?.CpuFreqMHz is float mhz && mhz > 100)
            {
                if (mhz > _benchCpuMhzMax) _benchCpuMhzMax = mhz;
                if (_benchCpuMhzMinLate <= 0 || mhz < _benchCpuMhzMinLate) _benchCpuMhzMinLate = mhz;
            }
            string temp(float? v) => v is float f && f > 0 ? $"{f:0} °C" : "";
            switch (_benchStep)
            {
                case 0:
                    _benchHudRight = string.Join(" · ", new[]
                    {
                        snap?.CpuFreqMHz is float m2 && m2 > 100 ? $"{BNum(m2 / 1000.0, "0.0")} GHz" : "",
                        temp(snap?.CpuTemp),
                    }.Where(x => x.Length > 0));
                    break;
                case 3:
                    _benchHudRight = string.Join(" · ", new[]
                    {
                        snap?.GpuFreqMHz is float g && g > 50 ? $"{g:0} MHz" : "",
                        temp(snap?.GpuTemp),
                        snap?.GpuLoadPct is float l && l > 0 ? $"{l:0}%" : "",
                    }.Where(x => x.Length > 0));
                    break;
            }
            if (_currentPanel == "Benchmark") UpdateBenchRunning();
        }

        // ══ the run ════════════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly string[] BenchStepNames = { "Processor", "Memory", "Storage", "Graphics" };

        private void BenchSetStep(int step, string sub)
        {
            BenchCloseStep();
            _benchStep = step;
            _benchStepStart = _benchClock?.Elapsed.TotalSeconds ?? 0;
            _benchSub = sub;
            _benchHudLeft = sub;
            _benchHudRight = "";
            UpdateBenchCards();
            UpdateBenchRunning();
        }

        /// <summary>The time of the step that just ended, into the record.</summary>
        private void BenchCloseStep()
        {
            if (_benchRec != null && _benchClock != null && _benchStep >= 0 && _benchStep < 4
                && _benchRec.Durations.All(d => d.Step != BenchStepNames[_benchStep]))
                _benchRec.Durations.Add((BenchStepNames[_benchStep], _benchClock.Elapsed.TotalSeconds - _benchStepStart));
        }

        private async Task RunBenchmarkAsync()
        {
            if (_benchRunning) return;
            if (_cpuStress.Running || _gpuStress.IsRunning)
            {
                AppDialog.Show(PT("Stop the stress test first — the benchmark needs the processor and the graphics card free."), PT("Benchmark"));
                return;
            }

            foreach (var c in _benchComps)
            {
                Array.Clear(c.Values); Array.Clear(c.Marks); Array.Clear(c.Suspect); Array.Clear(c.Raw); c.Mark = null;
            }
            _benchSustained = null; _benchDiskSlowWrite = false; _benchGpuRaw = default; _benchDiskSkipReason = null;
            bool turboOn = _benchTurboForTest && BenchTurboOff();
            _benchCts = new CancellationTokenSource();
            var ct = _benchCts.Token;
            _benchRunning = true;
            _benchDoneText = "";
            _benchDoneChips.Clear();
            _benchMaxCpuTemp = _benchMaxGpuTemp = _benchCpuMhzMax = _benchCpuMhzMinLate = 0;
            _benchClock = Stopwatch.StartNew();
            _benchStep = -1;
            _benchTickCount = 0;
            var s0 = _summary;
            _benchRec = new BenchmarkRecord
            {
                Pc = s0 == null ? "" : string.Join(" · ", new[]
                {
                    $"{s0.Manufacturer} {s0.Model}".Trim(), s0.Cpu, s0.CpuThreads > 0 ? $"{s0.CpuThreads} threads" : "",
                    s0.RamTotalGB > 0 ? $"{s0.RamTotalGB:0} GB {s0.RamType}".Trim() : "", s0.GpuName, $"{s0.OsName} {s0.OsBuild}".Trim(),
                }.Where(x => !string.IsNullOrWhiteSpace(x))),
            };
            var rec = _benchRec;
            if (turboOn) { BenchTurboOnForTest(rec); await Task.Delay(1500); }   // the new setting needs a moment to apply
            var pre = BenchPreChecks();
            _benchLastPre = pre;
            _benchTurboWasOff = BenchTurboOff();
            foreach (var w in pre) rec.Details.Add("Before the test: " + w);
            rec.Details.Add($"Graphics adapter chosen: {BenchGpuName()} (index {_benchGpuAdapter}) · adapters: {string.Join(" | ", (_benchGpuAdapters ?? new()).Select(a => $"{a.Index} {a.Name} {a.DedicatedMB} MB"))}");
            RefreshBenchmarkView();
            _benchTicker ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => BenchTick(), Dispatcher);
            _benchTicker.Start();
            AppLogger.Info("Benchmark page: started");
            int errors = 0;
            bool cancelled = false;
            try
            {
                // ── processor ─────────────────────────────────────────────────────────────────────────────────────
                var cpu = _benchComps[0];
                BenchSetStep(0, PT("One core (1 of 3)"));
                _benchHudLeft = PT("1 thread · 100%");
                // one thread: the same Mandelbrot + FFT as the all-threads test (no SHA-256: CPUs with SHA instructions won unfairly).
                // Mandelbrot counts the work (iterations), not pixels — see BenchmarkMandelbrotWork. The value is the
                // geometric mean of the two, scaled so that one core of an i5-4570 (Haswell, 2013) ≈ 1 (= 1000 pts).
                var single = await Task.Run(() =>
                {
                    double mb1 = CpuStressor.BenchmarkMandelbrotWork(5.0, threadCount: 1);
                    double fft1 = CpuStressor.BenchmarkFFT(5.0, 1, out int m1);
                    return (geo: CpuGeo(mb1, fft1), m: m1, mb: mb1, fft: fft1);
                });
                errors += single.m;
                cpu.Values[0] = $"{BNum(single.geo * 1000, "#,0")} {PT("pts")}";
                cpu.Marks[0] = BenchmarkScore.Mark(single.geo, BenchmarkScore.CpuSingle);
                cpu.Raw[0] = single.geo;
                rec.Add("Processor", "One thread", single.geo, "× consts", cpu.Marks[0], BenchmarkScore.CpuSingle,
                        $"Mandelbrot {single.mb:0} iterations/s (÷ {CpuMbNorm:0}) · FFT {single.fft:0} ops/s (÷ {CpuFftNorm:0}) · 1 thread · FFT mismatches {single.m} · pts = value × 1000");
                UpdateBenchCards();
                ct.ThrowIfCancellationRequested();

                _benchSub = PT("All cores (2 of 3)");
                _benchHudLeft = PT("{0} threads · 100%", Environment.ProcessorCount);
                var buckets = new long[8];
                var multi = await Task.Run(() =>
                {
                    double mb = CpuStressor.BenchmarkMandelbrotWork(5.0, 0, buckets);
                    double fft = CpuStressor.BenchmarkFFT(5.0, out int m2);
                    return (geo: CpuGeo(mb, fft), m2, mb, fft);
                });
                errors += multi.m2;
                cpu.Values[1] = $"{BNum(multi.geo * 1000, "#,0")} {PT("pts")}";
                cpu.Marks[1] = BenchmarkScore.Mark(multi.geo, BenchmarkScore.CpuMulti);
                cpu.Raw[1] = multi.geo;
                rec.Add("Processor", "All threads", multi.geo, "× consts", cpu.Marks[1], BenchmarkScore.CpuMulti,
                        $"Mandelbrot {multi.mb:0} iterations/s · FFT {multi.fft:0} ops/s · {Environment.ProcessorCount} threads · FFT mismatches {multi.m2} · all / one = {(single.geo > 0 ? multi.geo / single.geo : 0):0.00}");
                UpdateBenchCards();
                ct.ThrowIfCancellationRequested();

                // everyday work: compression on all threads, then 4 s of the all-cores Mandelbrot again — compared with its
                // first 2 s, it says how much speed the processor keeps after ~20 s of full load
                _benchSub = PT("Compression (3 of 3)");
                double zip = await Task.Run(() => CpuStressor.BenchmarkCompression(5.0));
                cpu.Values[2] = zip > 0 ? $"{BNum(zip, "0")} MB/s" : null;
                cpu.Marks[2] = BenchmarkScore.Mark(zip, BenchmarkScore.CpuCompress);
                rec.Add("Processor", "Compression", zip, "MB/s", cpu.Marks[2], BenchmarkScore.CpuCompress, $"Deflate optimal, 4 MB text-like data, {Environment.ProcessorCount} threads");
                ct.ThrowIfCancellationRequested();
                double late = await Task.Run(() => CpuStressor.BenchmarkMandelbrotWork(4.0));
                double early = (buckets[0] + buckets[1]) / 2.0;
                _benchSustained = early > 0 && late > 0 ? late / early : null;
                rec.Details.Add($"Processor under load: Mandelbrot first 2 s {early:0} / s · per second {string.Join(" ", buckets.Select(b => b.ToString(CultureInfo.InvariantCulture)))} · after ~20 s {late:0} / s · kept {(_benchSustained ?? 0) * 100:0} %");
                BenchCheckSuspect();
                cpu.Mark = BenchmarkScore.Mean(cpu.Marks);
                rec.ComponentMarks["Processor"] = cpu.Mark;
                cpu.Device = BenchCpuDevice();
                ct.ThrowIfCancellationRequested();

                // ── memory ────────────────────────────────────────────────────────────────────────────────────────
                var ram = _benchComps[1];
                BenchSetStep(1, PT("Speed (1 of 2)"));
                var bw = await RamBandwidthService.RunAsync(msg => Dispatcher.BeginInvoke(new Action(() => _benchHudLeft = msg)));
                ram.Values[0] = bw.ReadGBs > 0 ? $"{BNum(bw.ReadGBs)} GB/s" : null;
                ram.Marks[0] = BenchmarkScore.Mark(bw.ReadGBs, BenchmarkScore.RamBandwidth);
                ram.Raw[0] = bw.ReadGBs;
                rec.Add("Memory", "Speed (read)", bw.ReadGBs, "GB/s", ram.Marks[0], BenchmarkScore.RamBandwidth,
                        $"write {bw.WriteGBs:0.0} GB/s · passes read {bw.ReadPasses} / write {bw.WritePasses}");
                _benchHudRight = ram.Values[0] ?? "";
                UpdateBenchCards();
                ct.ThrowIfCancellationRequested();
                _benchSub = PT("Latency (2 of 2)");
                double lat = await RamLatencyBenchmark.RunAsync(ct, msg => Dispatcher.BeginInvoke(new Action(() => _benchHudLeft = msg)));
                ram.Values[1] = lat > 0 ? $"{BNum(lat, "0")} ns" : null;
                ram.Marks[1] = BenchmarkScore.Mark(lat, BenchmarkScore.RamLatency);
                ram.Raw[1] = lat;
                BenchCheckSuspect();
                ram.Mark = BenchmarkScore.Mean(ram.Marks);
                rec.Add("Memory", "Latency", lat, "ns", ram.Marks[1], BenchmarkScore.RamLatency, "random pointer chase, 64 MB");
                rec.ComponentMarks["Memory"] = ram.Mark;
                ct.ThrowIfCancellationRequested();

                // ── storage (quick disk benchmark of the system drive) ────────────────────────────────────────────
                var disk = _benchComps[2];
                BenchSetStep(2, PT("Write (1 of 3)"));
                string drive = string.IsNullOrEmpty(_benchDrive) ? SystemDriveLetter() : _benchDrive;
                var prog = new Progress<BenchmarkProgress>(p =>
                {
                    bool rnd = p.Phase.StartsWith("iops", StringComparison.Ordinal) || p.Phase == "latency";
                    _benchSub = rnd ? PT("4K random (3 of 3)") : p.Phase == "write" ? PT("Write (1 of 3)") : PT("Sequential (2 of 3)");
                    _benchHudLeft = p.Message;
                    _benchHudRight = p.CurrentMBps > 0 ? $"{BNum(p.CurrentMBps, "0")} MB/s" : "";
                });
                var dr = await new DiskBenchmarkService().RunAsync(drive, ct, prog, forceQuick: true);
                ct.ThrowIfCancellationRequested();
                // the test stopped before measuring (not enough free space…): say why instead of "0 MB/s"
                _benchDiskSkipReason = dr.ReadAvgMBps <= 0 && dr.WriteAvgMBps <= 0
                    ? (BenchDiskSpaceShort(drive) ?? (string.IsNullOrWhiteSpace(dr.Rating) ? PT("The storage test could not run.") : dr.Rating.TrimStart('⚠', ' ')))
                    : null;
                if (_benchDiskSkipReason != null) rec.Details.Add("Storage test skipped: " + _benchDiskSkipReason);
                double seq = dr.ReadAvgMBps, k4 = dr.ReadIOPS * 4096.0 / 1_000_000.0;
                disk.Values[0] = seq > 0 ? $"{BNum(seq, "0")} MB/s" : null;
                disk.Values[1] = k4 > 0 ? $"{BNum(k4, k4 < 10 ? "0.0" : "0")} MB/s" : null;
                disk.Marks[0] = BenchmarkScore.Mark(seq, BenchmarkScore.DiskSeq);
                disk.Raw[0] = seq;
                disk.Marks[1] = BenchmarkScore.Mark(k4, BenchmarkScore.Disk4K);
                double wr = dr.WriteAvgMBps;
                disk.Values[2] = wr > 0 ? $"{BNum(wr, "0")} MB/s" : null;
                disk.Marks[2] = BenchmarkScore.Mark(wr, BenchmarkScore.DiskWrite);
                _benchDiskSlowWrite = dr.IsSlowWriteMode;
                BenchCheckSuspect();
                disk.Mark = BenchmarkScore.Mean(disk.Marks);
                string dtype = dr.IsNvme ? "NVMe SSD" : dr.IsSsd ? "SSD" : dr.IsRemovable ? "USB" : "HDD";
                var physical = BenchDiskOf(drive);
                disk.Device = physical != null ? $"{drive} · {physical.Model.Trim()}" : $"{drive} · {dtype}";
                rec.Add("Storage", "Sequential (read)", seq, "MB/s", disk.Marks[0], BenchmarkScore.DiskSeq,
                        $"min {dr.ReadMinMBps:0} · max {dr.ReadMaxMBps:0} · passes {string.Join("/", dr.ReadPassMBps.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))}");
                rec.Add("Storage", "Sequential (write)", wr, "MB/s", disk.Marks[2], BenchmarkScore.DiskWrite, $"passes {string.Join("/", dr.WritePassMBps.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))} · slow write mode {dr.IsSlowWriteMode}");
                rec.Add("Storage", "4K random (read)", k4, "MB/s", disk.Marks[1], BenchmarkScore.Disk4K, $"{dr.ReadIOPS} IOPS, one request at a time");
                rec.ComponentMarks["Storage"] = disk.Mark;
                rec.Details.Add($"Storage: drive {drive} · {disk.Device} · quick {dr.IsQuickMode} · USB {dr.IsUsbAttached}");
                rec.Details.Add($"Storage: write avg {dr.WriteAvgMBps:0} MB/s (min {dr.WriteMinMBps:0}, max {dr.WriteMaxMBps:0}) · 4K write {dr.WriteIOPS} IOPS · slow write mode {dr.IsSlowWriteMode}");
                if (dr.HasQdResults) rec.Details.Add($"Storage: seq Q8 read {dr.SeqReadQ8MBps:0} / write {dr.SeqWriteQ8MBps:0} MB/s · 4K Q32 read {dr.Rand4kReadQ32Iops} / write {dr.Rand4kWriteQ32Iops} IOPS");
                if (dr.LatencyP50Us > 0) rec.Details.Add($"Storage: latency p50 {dr.LatencyP50Us:0} µs · p95 {dr.LatencyP95Us:0} · p99 {dr.LatencyP99Us:0} · max {dr.LatencyMaxUs:0}");
                if (!string.IsNullOrEmpty(dr.CacheWarning)) rec.Details.Add("Storage warning: " + dr.CacheWarning);
                if (!string.IsNullOrEmpty(dr.WriteWarning)) rec.Details.Add("Storage warning: " + dr.WriteWarning);

                // ── graphics: 3 tests × 13 s ──────────────────────────────────────────────────────────────────────
                var gpu = _benchComps[3];
                BenchSetStep(3, PT("3D render (1 of 3)"));
                string[] gpuSteps = { PT("3D render (1 of 3)"), PT("Compute (2 of 3)"), PT("Texture / memory (3 of 3)") };
                var gp = new Progress<GpuSuiteBenchmark.Progress>(p =>
                {
                    _benchSub = gpuSteps[Math.Clamp(p.Test, 0, 2)];
                    _benchHudLeft = p.Test switch
                    {
                        0 => $"{BNum(p.Live, "0")} {PT("frames/s")}",
                        1 => $"{BNum(p.Live, "#,0")} GFLOPS",
                        _ => $"{BNum(p.Live, "0")} GB/s",
                    };
                });
                var gr = await new GpuSuiteBenchmark().RunAsync(GpuSecondsPerTest, ct, gp, _benchGpuAdapter);
                ct.ThrowIfCancellationRequested();
                if (gr.Succeeded)
                {
                    gpu.Values[0] = gr.Fps > 0 ? $"{BNum(gr.Fps, "0")} {PT("frames/s")}" : null;
                    gpu.Values[1] = gr.GFlops > 0 ? (gr.GFlops >= 1000 ? $"{BNum(gr.GFlops / 1000, "0.0")} TFLOPS" : $"{BNum(gr.GFlops, "0")} GFLOPS") : null;
                    gpu.Values[2] = gr.MemoryGBs > 0 ? $"{BNum(gr.MemoryGBs, "0")} GB/s" : null;
                    gpu.Marks[0] = BenchmarkScore.Mark(gr.Fps, BenchmarkScore.GpuRender);
                    gpu.Marks[1] = BenchmarkScore.Mark(gr.GFlops, BenchmarkScore.GpuCompute);
                    gpu.Marks[2] = BenchmarkScore.Mark(gr.MemoryGBs, BenchmarkScore.GpuMemory);
                    _benchGpuRaw = (gr.Fps, gr.GFlops, gr.MemoryGBs);
                    BenchCheckSuspect();
                    gpu.Mark = BenchmarkScore.Mean(gpu.Marks);
                }
                if (!string.IsNullOrWhiteSpace(gr.AdapterName)) gpu.Device = gr.AdapterName;
                string[] gNames = { "3D render", "Compute", "Texture / memory" }, gUnits = { "frames/s", "GFLOPS", "GB/s" };
                var gScales = new[] { BenchmarkScore.GpuRender, BenchmarkScore.GpuCompute, BenchmarkScore.GpuMemory };
                for (int gi = 0; gi < 3; gi++)
                {
                    var st = gr.Stats[gi];
                    rec.Add("Graphics", gNames[gi], st?.Median ?? 0, gUnits[gi], gpu.Marks[gi], gScales[gi], st == null ? "not run" :
                        $"min {st.Min:0.#} · max {st.Max:0.#} · first {st.First:0.#} · last {st.Last:0.#} · {st.Samples} batches × {st.PerBatch} · one call {st.OneIssueMs:0.00} ms");
                }
                rec.ComponentMarks["Graphics"] = gpu.Mark;
                if (gr.MemoryNote.Length > 0) rec.Details.Add("Graphics memory: " + gr.MemoryNote);
                rec.Details.Add($"Graphics: adapter {gr.AdapterName} · {GpuSecondsPerTest} s per test · ok {gr.Succeeded}{(gr.Error.Length > 0 ? " · " + gr.Error : "")}");
                if (!gr.Succeeded) AppLogger.Warning("Benchmark page: graphics test failed — {0}", gr.Error);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { AppLogger.Warning(ex, "Benchmark page run"); }
            finally
            {
                BenchTurboRestore();
                MemoryRelease.AfterLargeTest("the benchmark");
                _benchTicker?.Stop();
                BenchCloseStep();
                _benchRunning = false;
                _benchStep = -1;
                var took = _benchClock?.Elapsed ?? TimeSpan.Zero;
                _benchDoneText = cancelled ? PT("Test stopped") : PT("Test finished in {0}", $"{(int)took.TotalMinutes}:{took.Seconds:00}");
                // the chips of the final banner: highest temperatures, slowed down from heat or not, problems
                _benchDoneChips.Clear();
                if (_benchMaxCpuTemp > 0) _benchDoneChips.Add((PT("Processor max {0} °C", _benchMaxCpuTemp.ToString("0")), null));
                if (_benchMaxGpuTemp > 0) _benchDoneChips.Add((PT("Graphics card max {0} °C", _benchMaxGpuTemp.ToString("0")), null));
                bool slowed = (_benchCpuMhzMax > 0 && _benchCpuMhzMinLate > 0 && _benchCpuMhzMinLate < _benchCpuMhzMax * 0.8 && _benchMaxCpuTemp >= 85)
                              || (_benchSustained is double kept && kept < 0.85 && _benchMaxCpuTemp >= 80);
                if (!cancelled && (_benchCpuMhzMax > 0 || _benchSustained != null))
                    _benchDoneChips.Add(slowed ? (PT("The processor slowed down from heat"), "StatusCheckBrush") : (PT("No thermal slowdown"), "StatusGoodBrush"));
                if (pre.Count > 0) _benchDoneChips.Add(("⚠ " + PT("Tested in conditions that lower the marks"), "StatusCheckBrush"));
                if (errors > 0) _benchDoneChips.Add(("⚠ " + PT("{0} calculation errors — possible hardware instability", errors), "StatusProblemBrush"));
                if (_benchDiskSkipReason != null && !cancelled) _benchDoneChips.Add(("⚠ " + PT("Storage test skipped"), "StatusCheckBrush"));
                if (_benchComps[3].Mark == null && !cancelled) _benchDoneChips.Add((PT("the graphics test could not run"), "StatusCheckBrush"));
                if (_benchRec != null)
                {
                    _benchRec.General = BenchmarkScore.Mean(_benchComps.Select(c => c.Mark));
                    _benchRec.Cancelled = cancelled;
                    _benchRec.CalcErrors = errors;
                    _benchRec.Took = took;
                    _benchRec.Notes = $"Highest temperatures: CPU {_benchMaxCpuTemp:0} °C · GPU {_benchMaxGpuTemp:0} °C · CPU clock in the processor step {_benchCpuMhzMinLate:0}–{_benchCpuMhzMax:0} MHz · slowed down: {(slowed ? "yes" : "no")} · drive {_benchDrive}";
                    LastBenchmarkFile = _benchRec.Save();
                    AppLogger.Info("Benchmark record ({0}):\n{1}", LastBenchmarkFile ?? "not saved", _benchRec.ToText());
                }
                AppLogger.LogSection("BENCHMARK PAGE: RESULT",
                    ("Processor", $"{_benchComps[0].Mark} ({string.Join(" / ", _benchComps[0].Values)})"),
                    ("Memory",    $"{_benchComps[1].Mark} ({string.Join(" / ", _benchComps[1].Values)})"),
                    ("Storage",   $"{_benchComps[2].Mark} ({string.Join(" / ", _benchComps[2].Values)})"),
                    ("Graphics",  $"{_benchComps[3].Mark} ({string.Join(" / ", _benchComps[3].Values)})"),
                    ("General",   BenchmarkScore.Mean(_benchComps.Select(c => c.Mark))?.ToString() ?? "—"),
                    ("Took",      took.ToString(@"m\:ss")), ("Cancelled", cancelled), ("Calc errors", errors));
                _benchCts?.Dispose(); _benchCts = null;
                RefreshBenchmarkView();
            }
        }
    }
}
