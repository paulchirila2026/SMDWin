using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SMDWin.Models;
using SMDWin.Services;

// Explicit aliases — avoid conflicts with System.Windows.Forms.* and System.IO.*
using WpfColor       = System.Windows.Media.Color;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfClipboard   = System.Windows.Clipboard;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfTabControl  = System.Windows.Controls.TabControl;
using WpfTabItem     = System.Windows.Controls.TabItem;
using WpfBrushes     = System.Windows.Media.Brushes;
using PenLineCap     = System.Windows.Media.PenLineCap;
using PenLineJoin    = System.Windows.Media.PenLineJoin;
using WpfPath        = System.Windows.Shapes.Path;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfViewbox     = System.Windows.Controls.Viewbox;
using WpfCanvas      = System.Windows.Controls.Canvas;
using WpfPanel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// The System Information page: cards for processor, memory, storage, graphics, display, motherboard, network, battery
    /// and peripherals (the Hardware tab) and the information about Windows (the Windows tab).
    /// Follows the active theme and language; reused the same way in all themes.
    /// </summary>
    public partial class SystemInfoView : System.Windows.Controls.UserControl
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public int dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)] public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            [MarshalAs(UnmanagedType.U4)] public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        private const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        // ── Theme colours ─────────────────────────────────────────────────────
        private readonly bool      _isDark;
        private readonly WpfColor  _bgMain;
        private readonly WpfColor  _bgCard;
        private readonly WpfColor  _bgHover;
        private readonly WpfColor  _textPrimary;
        private readonly WpfColor  _textSecondary;
        private readonly WpfColor  _textMuted;
        private readonly WpfColor  _border;
        private readonly WpfColor  _accent;
        private readonly WpfColor  _green;
        private readonly WpfColor  _orange;
        private readonly WpfColor  _red;

        // ── Services ─────────────────────────────────────────────────────────
        private readonly HardwareService _hw  = new();
        private readonly BatteryService  _bat = new();
        private readonly NetworkService  _net = new();

        // ── UI ────────────────────────────────────────────────────────────────
        private TextBlock?  _statusBar;
        private readonly Dictionary<string, List<Border>> _tabPanels = new();
        private StackPanel? _allCardsHost;
        private int _currentColumns = 3;   // latimea implicita 1265 → 3 coloane
        private TextBlock? _txtHeaderSubtitle;
        private (string Key, string[] SubKeys)[] _groupKeyMap = Array.Empty<(string, string[])>();
        private WpfTabControl? _tabControl;
        private FrameworkElement? _mainContentScroll;
        private FrameworkElement? _loadingOverlay;

        private readonly List<(string Title, string Subtitle, List<(string Label, string Value)> Rows)> _cardData = new();
    private readonly List<string> _cardIconKeys = new();   // the same order as _cardData (for the PDF icons)

        public SystemInfoView()
        {
            string theme = SettingsService.Current.ThemeName;
            _isDark = !ThemeManager.IsLight(theme) &&
                      !(theme == "Auto" && !ThemeManager.WindowsIsDark());

            if (_isDark)
            {
                _bgMain        = H("#0F1621");
                _bgCard        = H("#1A2233");
                _bgHover       = H("#222D40");
                _textPrimary   = H("#F8FAFF");   // aproape alb pur
                _textSecondary = H("#B0C8E8");   // lighter than #8BA0BC
                _textMuted     = H("#7C93B8");
                _border        = H("#263145");
            }
            else
            {
                _bgMain        = H("#EEF2F8");
                _bgCard        = H("#FFFFFF");
                _bgHover       = H("#E4EDFB");
                _textPrimary   = H("#0A0F1A");   // aproape negru pur
                _textSecondary = H("#2D3F52");   // darker than #5A6E84
                _textMuted     = H("#8698AC");
                _border        = H("#D0DCE8");
            }

            // the same colours as the rest of the app — from the active theme's resources.
            static WpfColor Res(string key, WpfColor fb)
                => (System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color ?? fb;
            _bgMain        = Res("BgDarkBrush",        _bgMain);
            _bgCard        = Res("BgCardBrush",        _bgCard);
            _bgHover       = Res("BgHoverBrush",       _bgHover);
            _textPrimary   = Res("TextPrimaryBrush",   _textPrimary);
            _textSecondary = Res("TextSecondaryBrush", _textSecondary);
            _border        = Res("CardBorderBrush",    _border);

            _accent = H(ThemeManager.GetAccentHex(SettingsService.Current.AccentName));
            _green  = H("#22C55E");
            _orange = H("#F59E0B");
            _red    = H("#EF4444");

            FontFamily = new WpfFontFamily("Segoe UI");
            FontSize   = Ui.FontL;
            UseLayoutRounding   = true;
            SnapsToDevicePixels = true;
            System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(this, System.Windows.Media.TextRenderingMode.Grayscale);

            try { BuildUI(); }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SystemInfoView.BuildUI");
                Content = new TextBlock
                {
                    Text = $"System Information init error:\n{ex.Message}",
                    Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(_textSecondary)
                };
            }

            // the number of columns, the size and the row heights are chosen together, in FitHeroRows (SystemInfoView.Cards.cs)
            SizeChanged += (_, _) => ScheduleFitHeroRows();

            Loaded += async (_, _) => await RefreshAsync(force: false);
        }

        // The data is read on first display and refreshed when the page is reopened after more than a minute.
        private bool _loadBusy;
        private DateTime _loadedAt = DateTime.MinValue;

        public async Task RefreshAsync(bool force)
        {
            if (_loadBusy) return;
            if (!force && _loadedAt != DateTime.MinValue && (DateTime.Now - _loadedAt).TotalSeconds < 300) return;
            _loadBusy = true;
            try
            {
                bool reloadWindows = _windowsLoaded;
                if (force) { _bundle = null; _winInfoCache = null; _winInfoTask = null; }
                await LoadDataAsync();
                _loadedAt = DateTime.Now;
                // on first display the Windows data already starts from LoadDataAsync; on refresh it is read again, in the background
                if (reloadWindows && _winInfoCache == null) { _windowsLoaded = false; _ = LoadWindowsInfoAsync(); }
            }
            finally { _loadBusy = false; }
        }

        // ── Skeleton UI ───────────────────────────────────────────────────────
        private int _tab;                       // there is a single page; 1 = the Windows area was requested (it scrolls to it)
        private bool _windowsLoaded;
        private bool _hardwareReady;

        private void ApplyTabs()
        {
            if (_mainContentScroll != null) _mainContentScroll.Visibility = _hardwareReady ? Visibility.Visible : Visibility.Hidden;
            if (_loadingOverlay != null) _loadingOverlay.Visibility = !_hardwareReady ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>1 if the Windows area of the page was requested, otherwise 0.</summary>
        public int CurrentTab => _tab;

        // The card requested from the Dashboard: scroll to it and highlight it briefly, once the cards exist
        private string? _pendingFocus;

        public void FocusCard(string iconKey)
        {
            _pendingFocus = iconKey;
            Dispatcher.BeginInvoke(new Action(ApplyPendingFocus), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void ApplyPendingFocus()
        {
            if (_pendingFocus == null) return;
            var host = _allCardsHost;
            if (host == null || !_hardwareReady) return;
            var card = FindCard(host, _pendingFocus);
            if (card == null) return;   // the Windows card appears after the Windows data is read; the focus stays pending
            _pendingFocus = null;
            card.BringIntoView();
            // a single highlighted card: a new click (or a rebuilt page) closes the old highlight, so no row stays with the accent border
            EndFocusHighlight();
            _focusCard = card;
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            _focusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            _focusTimer.Tick += (_, _) => EndFocusHighlight();
            _focusTimer.Start();
        }

        private Border? _focusCard;
        private System.Windows.Threading.DispatcherTimer? _focusTimer;

        private void EndFocusHighlight()
        {
            _focusTimer?.Stop();
            _focusTimer = null;
            _focusCard?.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            _focusCard = null;
        }

        private static Border? FindCard(DependencyObject root, string iconKey)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border b && b.Tag is string tag && tag == iconKey) return b;
                var found = FindCard(child, iconKey);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The page has a single scrollable area; 1 = scroll to the Windows cards (from the end).</summary>
        public void ShowTab(int tab)
        {
            _tab = 0;
            if (tab == 1) FocusCard("Windows");
        }

        private void BuildUI()
        {
            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Header: the computer illustration, its name, the tabs and the buttons ───
            // no line under it: no other page has one
            var hdr = new Border
            {
                Padding         = new Thickness(0, 0, 0, 8),
                Margin          = new Thickness(0, 0, 0, 8),
            };
            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel { VerticalAlignment = WpfVAlign.Center };
            var title = new TextBlock
            {
                Text = L("System Information", "Informații Sistem", "Systeminformationen", "Informations système"),
                FontSize = 22, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_textPrimary),
            };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
            _txtHeaderSubtitle = new TextBlock
            {
                Text = L("loading…", "se încarcă…", "wird geladen…", "chargement…"),
                FontSize = Ui.FontM, Foreground = new SolidColorBrush(_textSecondary),
                Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            titles.Children.Add(title);
            titles.Children.Add(_txtHeaderSubtitle);
            hdrGrid.Children.Add(titles);

            var right = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVAlign.Center };
            var btnCopy = MakeHeaderButton(L("Copy all", "Copiază tot", "Alles kopieren", "Tout copier"),
                "M9,9 H20 V20 H9 Z M5,15 H4 A1,1 0 0 1 3,14 V4 A1,1 0 0 1 4,3 H14 A1,1 0 0 1 15,4 V5");
            btnCopy.Margin = new Thickness(18, 0, 0, 0);
            btnCopy.Click += (_, _) => CopyAll();
            var btnExportPdf = MakeHeaderButton("Export PDF",
                "M6,9 V3 H18 V9 M6,18 H4 A1,1 0 0 1 3,17 V10 A1,1 0 0 1 4,9 H20 A1,1 0 0 1 21,10 V17 A1,1 0 0 1 20,18 H18 M6,14 H18 V21 H6 Z");
            btnExportPdf.Margin = new Thickness(8, 0, 0, 0);
            btnExportPdf.Click += (_, _) => ExportToPdf();
            right.Children.Add(btnCopy);
            right.Children.Add(btnExportPdf);
            Grid.SetColumn(right, 1);
            hdrGrid.Children.Add(right);
            hdr.Child = hdrGrid;
            Grid.SetRow(hdr, 0);
            outerGrid.Children.Add(hdr);

            // ── Fila Hardware ──────────────────────────────────────────────────
            var mainStack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

            (string Key, string TitleEn, string TitleRo, string TitleDe, string TitleFr)[] groups =
            {
                ("System",      "System",              "Sistem",                 "System",              "Système"),
                ("ProcMem",     "Processor & Memory",  "Procesor & Memorie",     "Prozessor & Speicher","Processeur & Mémoire"),
                ("Graphics",    "Graphics",            "Grafică",                "Grafik",              "Graphique"),
                ("Storage",     "Storage",             "Stocare",                "Speicherplatz",       "Stockage"),
                ("Power",       "Power",               "Alimentare",             "Stromversorgung",     "Alimentation"),
                ("Motherboard", "Motherboard",         "Placă de bază",          "Hauptplatine",        "Carte mère"),
                ("Displays",    "Display",             "Afișaj",                 "Bildschirm",          "Écran"),
                ("NetPeriph",   "Network & Peripherals","Rețea & Periferice",    "Netzwerk & Peripherie","Réseau & Périphériques"),
            };
            // Each group can contain several keys (e.g. ProcMem = CPU + Memory); the keys must exist in _tabPanels so
            // BuildAllCards() can populate them.
            Dictionary<string, string[]> groupKeys = new()
            {
                ["System"]      = new[] { "System" },
                ["ProcMem"]     = new[] { "CPU", "Memory" },
                ["Graphics"]    = new[] { "Graphics" },
                ["Storage"]     = new[] { "Storage" },
                ["Power"]       = new[] { "Battery", "Chassis" },
                ["Motherboard"] = new[] { "Motherboard" },
                ["Displays"]    = new[] { "Displays" },
                ["NetPeriph"]   = new[] { "Network", "Peripherals" },
            };
            _groupKeyMap = groups.Select(g => (g.Key, groupKeys[g.Key])).ToArray();

            _allCardsHost = new StackPanel();
            foreach (var g in groups)
                foreach (string key in groupKeys[g.Key])
                    _tabPanels[key] = new List<Border>();
            mainStack.Children.Add(_allCardsHost);

            var mainScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = WpfHAlign.Stretch,
                Content = mainStack,
            };
            Grid.SetRow(mainScroll, 1);
            mainScroll.SizeChanged += (_, _) => ScheduleFitHeroRows();
            // also when the content changes height after the data arrived (cards added, texts that appear late), the rows are
            // fitted again: otherwise empty space remains below them
            double lastStackHeight = 0;
            mainStack.SizeChanged += (_, e) =>
            {
                if (_fittingRows || Math.Abs(e.NewSize.Height - lastStackHeight) < 1) return;
                lastStackHeight = e.NewSize.Height;
                ScheduleFitHeroRows();
            };
            mainScroll.Visibility = Visibility.Hidden; // hidden until the real data arrives (Hidden: it already has its real size, for fitting)
            outerGrid.Children.Add(mainScroll);
            _mainContentScroll = mainScroll;

            // A clean, empty screen until the data arrives
            var loadingPanel = new StackPanel
            {
                HorizontalAlignment = WpfHAlign.Center, VerticalAlignment = WpfVAlign.Center,
            };
            // [0] the error text (hidden until a load fails), [1] the scan: the drawing with the lens and the list of readings
            loadingPanel.Children.Add(new TextBlock
            {
                Text = L("Loading hardware information…", "Se încarcă informațiile hardware…", "Hardwareinformationen werden geladen…", "Chargement des informations matérielles…"),
                FontSize = Ui.FontL, Foreground = new SolidColorBrush(_textSecondary),
                HorizontalAlignment = WpfHAlign.Center, Visibility = Visibility.Collapsed,
            });
            _scan = new SysInfoScan
            {
                Heading = L("Reading the hardware…", "Se citește hardware-ul…", "Hardware wird gelesen…", "Lecture du matériel…"),
                StillReading = L("still reading…", "încă se citește…", "wird noch gelesen…", "lecture en cours…"),
            };
            _scan.Start(ScanItems());
            loadingPanel.Children.Add(_scan);
            _ = SetScanKindAsync();
            Grid.SetRow(loadingPanel, 1);
            outerGrid.Children.Add(loadingPanel);
            _loadingOverlay = loadingPanel;

            _tabControl = new WpfTabControl { Visibility = Visibility.Collapsed };

            // ── Status bar ──────────────────────────────────────────────────
            _statusBar = new TextBlock
            {
                Text       = L("Loading hardware data…", "Se încarcă datele hardware…", "Hardwaredaten werden geladen…", "Chargement des données matérielles…"),
                FontSize   = Ui.FontS,
                Foreground = new SolidColorBrush(_textSecondary),
                Margin     = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,   // appears only on errors
            };
            Grid.SetRow(_statusBar, 2);
            outerGrid.Children.Add(_statusBar);

            Content = outerGrid;
            ApplyTabs();
        }

        // ── Load data ─────────────────────────────────────────────────────────
        private sealed record HwBundle(
            SystemSummary Summary, List<NetworkAdapterEntry> Nets, List<RamEntry> Rams, List<DiskHealthEntry> Disks,
            List<ManagementObject> Gpu, EnclosureInfo Enclosure,
            (Dictionary<string, string> friendlyByHwId, Dictionary<string, string> serialByHwId) MonitorNames,
            Dictionary<string, MonitorPhysical> MonitorPhysical, List<(string, string)> Audio,
            List<string> Usbs, List<string> Bts);
        private static HwBundle? _bundle;
        private static DateTime _bundleAt = DateTime.MinValue;
        private const int BundleTtlSeconds = 300;

        private SysInfoScan? _scan;

        private string[] ScanItems() => new[]
        {
            L("Processor and board", "Procesor și placă de bază", "Prozessor und Mainboard", "Processeur et carte mère"),
            L("Memory", "Memorie", "Arbeitsspeicher", "Mémoire"),
            L("Storage", "Stocare", "Speicher", "Stockage"),
            L("Graphics", "Placă video", "Grafik", "Graphisme"),
            L("Displays", "Monitoare", "Bildschirme", "Écrans"),
            L("Network", "Rețea", "Netzwerk", "Réseau"),
            L("Audio", "Audio", "Audio", "Audio"),
            L("USB and Bluetooth", "USB și Bluetooth", "USB und Bluetooth", "USB et Bluetooth"),
            L("Windows", "Windows", "Windows", "Windows"),
        };

        private async Task SetScanKindAsync()
        {
            try { var k = await SysInfoScan.DetectKindAsync(); if (_scan != null) _scan.DeviceKind = k; }
            catch (Exception ex) { AppLogger.Debug($"SysInfoScan kind: {ex.Message}"); }
        }

        /// <summary>Ticks one row of the scan list when its readings finish (whatever the outcome).</summary>
        private void ScanTick(int i, params Task?[] tasks)
        {
            var real = tasks.Where(x => x != null).Cast<Task>().ToArray();
            if (real.Length == 0) { _scan?.MarkDone(i); return; }
            _ = Task.WhenAll(real).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => _scan?.MarkDone(i))), TaskScheduler.Default);
        }

        private async Task LoadDataAsync()
        {
            try
            {
                if (_scan != null && !_hardwareReady) _scan.Start(ScanItems());
                // The hardware data is read only once and kept for 5 minutes, shared by all page rebuilds (theme change, reopening).
                // Only the battery is always read again (the percentage changes).
                var batTask = _bat.GetBatteryInfoAsync();
                // the Windows data (license, protection, timeline) is read in parallel with the hardware data, so the page appears only
                // once, with all the cards — not first the hardware and then, a moment later, the Windows cards
                var winTask = _winInfoCache == null ? WinInfoTask() : null;
                var b = _bundle;
                if (b == null || (DateTime.UtcNow - _bundleAt).TotalSeconds > BundleTtlSeconds)
                {
                    var summaryTask = _hw.GetSystemSummaryAsync();
                    var netTask     = _net.GetAdaptersAsync();
                    var ramTask     = _hw.GetRamAsync();
                    var diskTask    = _hw.GetDisksAsync();
                    // WMI queries on a background thread, in parallel with the rest.
                    var gpuObjsTask = Task.Run(FetchGpuObjects);
                    var enclosureTask = Task.Run(FetchEnclosureInfo);
                    var monitorNamesTask = Task.Run(FetchMonitorFriendlyNames);
                    var monitorPhysicalTask = Task.Run(SystemProbes.ReadMonitors);
                    var audioRowsTask = Task.Run(FetchAudioRows);
                    var usbBtTask = Task.Run(FetchUsbAndBluetooth);
                    ScanTick(0, summaryTask, enclosureTask); ScanTick(1, ramTask); ScanTick(2, diskTask); ScanTick(3, gpuObjsTask);
                    ScanTick(4, monitorNamesTask, monitorPhysicalTask); ScanTick(5, netTask); ScanTick(6, audioRowsTask); ScanTick(7, usbBtTask);

                    await Task.WhenAll(summaryTask, netTask, ramTask, diskTask, gpuObjsTask, enclosureTask, monitorNamesTask, monitorPhysicalTask, audioRowsTask, usbBtTask);
                    var (usbs0, bts0) = usbBtTask.Result;
                    b = new HwBundle(summaryTask.Result, netTask.Result, ramTask.Result, diskTask.Result,
                        gpuObjsTask.Result, enclosureTask.Result, monitorNamesTask.Result, monitorPhysicalTask.Result,
                        audioRowsTask.Result, usbs0, bts0);
                    _bundle = b; _bundleAt = DateTime.UtcNow;
                }
                else _scan?.MarkAllDone(8);   // the hardware data was already read (kept for 5 minutes)
                ScanTick(8, winTask);
                var bat = await batTask;
                if (winTask != null)
                {
                    // at most 6 seconds of waiting; if Windows answers more slowly, its cards are added when they arrive
                    try
                    {
                        if (await Task.WhenAny(winTask, Task.Delay(6000)) == winTask && winTask.Status == TaskStatus.RanToCompletion)
                            _winInfoCache = winTask.Result;
                    }
                    catch (Exception ex) { AppLogger.Debug($"SystemInfo WinInfo wait: {ex.Message}"); }
                }
                if (_winInfoCache != null) { _winInfo = _winInfoCache; _windowsLoaded = true; }
                var tempsEmpty = new List<TemperatureEntry>();   // temperatures are not shown here

                await Dispatcher.InvokeAsync(() => ShowLoadedDataAsync(b, bat, tempsEmpty)).Task.Unwrap();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_loadingOverlay is StackPanel lp && lp.Children.Count > 0 && lp.Children[0] is TextBlock tb)
                    {
                        tb.Visibility = Visibility.Visible;
                        if (_scan != null) _scan.Visibility = Visibility.Collapsed;
                        tb.Text = L($"Failed to load hardware data: {ex.Message}",
                            $"Nu s-au putut încărca datele hardware: {ex.Message}",
                            $"Hardwaredaten konnten nicht geladen werden: {ex.Message}",
                            $"Échec du chargement des données matérielles : {ex.Message}");
                        tb.Foreground = new SolidColorBrush(H("#EF4444"));
                    }
                    if (_statusBar != null)
                    {
                        _statusBar.Text = L("Load error", "Eroare la încărcare", "Ladefehler", "Erreur de chargement");
                        _statusBar.Visibility = Visibility.Visible;
                    }
                });
            }
        }

        /// <summary>
        /// Builds the page from the data read, in three separate steps (cards · layout · row fitting). Between them the interface
        /// thread is released, so the loading animation and the rest of the window do not freeze for ~1 s in one piece.
        /// The duration of each step is written to the log.
        /// </summary>
        private async Task ShowLoadedDataAsync(HwBundle b, BatteryInfo bat, List<TemperatureEntry> tempsEmpty)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var list in _tabPanels.Values) list.Clear();
            _cardData.Clear();
            _cardIconKeys.Clear();
            BuildAllCards(
                b.Summary, bat,
                b.Nets,    b.Rams,
                b.Disks,   tempsEmpty,
                b.Usbs, b.Bts,
                b.Gpu, b.Enclosure,
                b.MonitorNames, b.MonitorPhysical, b.Audio,
                layout: false);
            long tData = sw.ElapsedMilliseconds;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            sw.Restart();
            RebuildAllCardsLayout();
            long tCards = sw.ElapsedMilliseconds;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            sw.Restart();
            _hardwareReady = true;
            // the layout (columns, size, heights) is computed before the page is shown: no "jump" after display
            try { UpdateLayout(); FitHeroRows(); } catch (Exception fitEx) { AppLogger.Debug($"SystemInfo first fit: {fitEx.Message}"); }
            ApplyTabs();
            long tFit = sw.ElapsedMilliseconds;
            AppLogger.Info($"PAGE-TIMING SystemInfo: fisa tehnica {tData} ms · carduri {tCards} ms · asezare {tFit} ms");
            _ = LoadWindowsInfoAsync();
            if (_pendingFocus != null) _ = Dispatcher.BeginInvoke(new Action(ApplyPendingFocus), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }


        private Border MakeErrorCard(string msg) => new Border
        {
            Background      = new SolidColorBrush(_isDark ? H("#2C1515") : H("#FFF0F0")),
            BorderBrush     = new SolidColorBrush(_red),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(20),
            Margin          = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text         = L("Error: ", "Eroare: ", "Fehler: ", "Erreur : ") + msg,
                Foreground   = new SolidColorBrush(_red),
                FontSize     = Ui.FontM,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        private WpfButton MakeHeaderButton(string label, string iconPath)
        {
            var sp = new StackPanel
            {
                Orientation       = WpfOrientation.Horizontal,
                VerticalAlignment = WpfVAlign.Center,
            };
            sp.Children.Add(MakeSvgIcon(iconPath, "#94A3B8", 13));
            sp.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = Ui.FontS,
                Foreground        = new SolidColorBrush(_textSecondary),
                Margin            = new Thickness(5, 0, 0, 0),
                VerticalAlignment = WpfVAlign.Center,
            });
            var btn = new WpfButton
            {
                Content = sp,
                Style   = TryFindResource("OutlineButtonStyle") as Style,
                Padding = new Thickness(10, 5, 10, 5),
            };
            return btn;
        }

        private WpfButton MakeIconButton(string svgPath, string tooltip)
        {
            var btn = new WpfButton
            {
                Content         = MakeSvgIcon(svgPath, "#64748B", 14),
                Background      = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(5, 3, 5, 3),
                Margin          = new Thickness(2, 0, 0, 0),
                ToolTip         = tooltip,
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            btn.MouseEnter += (_, _) =>
            {
                if (btn.Content is WpfViewbox vb && vb.Child is WpfCanvas cv)
                    foreach (var ch in cv.Children.OfType<System.Windows.Shapes.Path>())
                        ch.Stroke = new SolidColorBrush(_accent);
            };
            btn.MouseLeave += (_, _) =>
            {
                if (btn.Content is WpfViewbox vb && vb.Child is WpfCanvas cv)
                    foreach (var ch in cv.Children.OfType<System.Windows.Shapes.Path>())
                        ch.Stroke = new SolidColorBrush(H("#64748B"));
            };
            return btn;
        }

        private static WpfViewbox MakeSvgIcon(string data, string colorHex, double size)
        {
            var p = new WpfPath
            {
                Stroke             = new SolidColorBrush(H(colorHex)),
                StrokeThickness    = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                StrokeLineJoin     = PenLineJoin.Round,
                Fill               = WpfBrushes.Transparent,
                Data               = Geometry.Parse(data),
            };
            var canvas = new WpfCanvas { Width = 24, Height = 24 };
            canvas.Children.Add(p);
            return new WpfViewbox { Width = size, Height = size, Child = canvas };
        }


        private string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
        {
            "ro" => ro,
            "de" => de,
            "fr" => fr,
            _    => en,
        };

        /// <summary>Builds a meaningful Google search term for the RAM modules installed.</summary>
        private static string BuildRamSearchTerm(
            List<RamEntry> rams,
            SystemSummary summary)
        {
            var first = rams.FirstOrDefault(r => !r.IsEmpty);
            if (first == null) return "";

            // Decode known JEDEC manufacturer IDs (WMI returns them as hex strings)
            string mfr = first.Manufacturer?.Trim() ?? "";
            if (mfr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(mfr, @"^[0-9A-Fa-f]{4}$"))
            {
                if (int.TryParse(mfr.Replace("0x", "").Replace("0X", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out int jedec))
                {
                    mfr = jedec switch
                    {
                        0x802C => "Micron",
                        0x80AD => "SK Hynix",
                        0x80CE => "Samsung",
                        0x0198 or 0x0100 => "Kingston",
                        0x802E => "Micron",
                        0x014F => "Transcend",
                        0x9E02 => "Corsair",
                        0x0143 => "Ramaxel",
                        0x80B0 => "Elpida",
                        0x04CD => "G.Skill",
                        _ => ""
                    };
                }
            }
            if (mfr is "—" or "Unknown" or "Not Specified" or "") mfr = "";

            string memType = first.MemoryType?.Trim() ?? "";
            string capacity = first.Capacity?.Trim() ?? "";
            string speed    = first.RatedMHz > 0 ? $"{first.RatedMHz}MHz"
                            : first.ConfiguredMHz > 0 ? $"{first.ConfiguredMHz}MHz"
                            : (first.Speed?.Trim() ?? "");
            if (speed is "—") speed = "";

            var parts = new[] { mfr, memType, capacity, speed, "RAM" }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" ", parts);
        }

        private static WpfColor H(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
                return WpfColor.FromRgb(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
            if (hex.Length == 8)
                return WpfColor.FromArgb(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
            return WpfColor.FromRgb(128, 128, 128);
        }
    }
}
