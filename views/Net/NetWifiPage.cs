using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SMDWin.Services;

// WPF type aliases — resolve ambiguity with System.Drawing / System.Windows.Forms
using Application          = System.Windows.Application;
using Brush                = System.Windows.Media.Brush;
using Brushes              = System.Windows.Media.Brushes;
using Button               = System.Windows.Controls.Button;
using Canvas               = System.Windows.Controls.Canvas;
using CheckBox             = System.Windows.Controls.CheckBox;
using ColorConverter       = System.Windows.Media.ColorConverter;
using ComboBox             = System.Windows.Controls.ComboBox;
using Cursors              = System.Windows.Input.Cursors;
using FontFamily           = System.Windows.Media.FontFamily;
using HorizontalAlignment  = System.Windows.HorizontalAlignment;
using MenuItem             = System.Windows.Controls.MenuItem;
using MessageBox           = System.Windows.MessageBox;
using Orientation          = System.Windows.Controls.Orientation;
using Panel                = System.Windows.Controls.Panel;
using Point                = System.Windows.Point;
using TextBox              = System.Windows.Controls.TextBox;
using VerticalAlignment    = System.Windows.VerticalAlignment;
using WpfColor             = System.Windows.Media.Color;

namespace SMDWin.Views.Net
{
    public class NetWifiPage : NetToolPage
    {
        private record WifiEntry(string Ssid, string Password, string Auth, string Encryption);
        private List<WifiEntry> _entries = new();

        private StackPanel _list       = null!;
        private TextBlock  _txtEmpty   = null!;
        private Button     _btnScan    = null!;
        private Button     _btnExport  = null!;

        private Button _tabSaved = null!, _tabLive = null!;
        private Grid   _pageSaved = null!, _pageLive = null!;
        private System.Windows.Threading.DispatcherTimer? _liveTimer;
        private bool _liveMonitoringActive = false;
        private Wpf.Ui.Controls.Button _btnLiveMonitor = null!;
        private TextBlock _chartTitle = null!;
        private readonly List<(DateTime t, int rssi, int pct)>   _signalHistory = new();
        private readonly List<(DateTime t, long pingMs)>          _pingHistory   = new();
        private const int LiveHistoryMaxPoints = 300; // 300 x 2s = 10 minutes of history
        private TextBlock _liveSsid = null!, _liveBand = null!, _liveSecurity = null!, _liveSpeed = null!, _liveSignalPct = null!, _livePing = null!, _liveAdapter = null!;
        private Canvas _signalCanvas = null!;
        private ArtPicture _routerPicture = null!;
        private TextBlock _routerCaption = null!;
        private TextBlock _routerName = null!;
        private bool _routerIdentityBusy;
        private StackPanel _headerSlot = null!;
        private TextBlock _liveBestPoint = null!;
        private StackPanel _legend = null!;
        // history per network (key = SSID): signal in % on every read of the surroundings
        private readonly Dictionary<string, List<(DateTime t, int pct)>> _nearbyHist = new();
        private readonly Dictionary<string, (int pct, int channel)> _nearbyNow = new();
        private DateTime _lastNearbyRead = DateTime.MinValue;
        private bool _nearbyBusy;
        private bool _resumeLive;
        private static readonly string[] SeriesHex = { "#E15759", "#59A14F", "#F28E2B", "#B07AA1", "#76B7B2", "#EDC948", "#FF9DA7", "#9C755F" };
        private int _bestRssiSeen = int.MinValue;
        private DateTime _bestRssiTimeUtc = DateTime.MinValue;

        public NetWifiPage()
        {
            NetWindowHelper.SetupWindow(this, "Wi-Fi Networks — SMDWin", 780, 640);
            Content = BuildRootUI();
            Closed += (_, _) => _liveTimer?.Stop();
            Loaded += (_, _) => { WifiScan_Click(this, new RoutedEventArgs()); _ = LoadRouterIdentityAsync(); };
            // monitoring stops when the page is not visible and resumes when it returns
            IsVisibleChanged += (_, e) =>
            {
                bool visible = (bool)e.NewValue;
                if (!visible && _liveMonitoringActive) { _resumeLive = true; ToggleLiveMonitor_Click(this, new RoutedEventArgs()); }
                else if (visible && _resumeLive) { _resumeLive = false; if (!_liveMonitoringActive) ToggleLiveMonitor_Click(this, new RoutedEventArgs()); }
            };
        }

        private UIElement BuildRootUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // tab strip
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // pages

            var tabStripRow = new Grid { Margin = new Thickness(20, 12, 20, 0) };
            tabStripRow.ColumnDefinitions.Add(new ColumnDefinition());
            tabStripRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabStrip = new StackPanel { Orientation = Orientation.Horizontal };
            _tabSaved = MakeTabButton(SMDWin.Views.MainWindow.PT("Saved networks"), false);
            _tabLive  = MakeTabButton(SMDWin.Views.MainWindow.PT("Live"), true);
            _tabSaved.Click += (_, _) => SwitchTab(true);
            _tabLive.Click  += (_, _) => SwitchTab(false);
            tabStrip.Children.Add(_tabSaved);
            tabStrip.Children.Add(_tabLive);
            // the tabs on the grey track, as in the rest of the app
            var tabTrack = new Border { Child = tabStrip, Tag = "SegTrack", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 10) };
            tabTrack.SetResourceReference(FrameworkElement.StyleProperty, "SegmentTrackStyle");
            Grid.SetColumn(tabTrack, 0); tabStripRow.Children.Add(tabTrack);

            // the actions of the active tab sit in the top right corner, as in the other tools
            _headerSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            NetWindowHelper.AddCloseButton(this, _headerSlot);
            Grid.SetColumn(_headerSlot, 1); tabStripRow.Children.Add(_headerSlot);

            NetWindowHelper.MakeDraggable(tabStripRow, this);
            Grid.SetRow(tabStripRow, 0);
            root.Children.Add(tabStripRow);

            var pageHost = new Grid();
            _pageSaved = (Grid)BuildUI();
            _pageLive  = BuildLivePage();
            pageHost.Children.Add(_pageSaved);
            pageHost.Children.Add(_pageLive);
            foreach (var action in new[] { _btnLiveMonitor, _btnExport })
            {
                (action.Parent as System.Windows.Controls.Panel)?.Children.Remove(action);
                action.Margin = new Thickness(6, 0, 0, 10);
                _headerSlot.Children.Insert(0, action);
            }
            Grid.SetRow(pageHost, 1);
            root.Children.Add(pageHost);

            SwitchTab(false);

            return root;
        }

        private Button MakeTabButton(string text, bool active)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 2, 0),
                Height = 28, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            };
            SetTabActive(b, active);
            return b;
        }

        private static void SetTabActive(Button b, bool active)
        {
            SMDWin.Views.MainWindow.SetTabButtonActive(b as Wpf.Ui.Controls.Button, active);
        }

        // Wi-Fi Live (reads every 2 s); stopping goes back to the saved networks, which stops the live reading
        public override bool SelfTestStart() { SwitchTab(false); return true; }
        public override void SelfTestStop() { if (_liveMonitoringActive) SwitchTab(true); }

        private void SwitchTab(bool showSaved)
        {
            _pageSaved.Visibility = showSaved ? Visibility.Visible : Visibility.Collapsed;
            _pageLive.Visibility  = showSaved ? Visibility.Collapsed : Visibility.Visible;
            SetTabActive(_tabSaved, showSaved);
            SetTabActive(_tabLive, !showSaved);
            if (_btnExport != null) _btnExport.Visibility = showSaved ? Visibility.Visible : Visibility.Collapsed;
            if (_btnLiveMonitor != null) _btnLiveMonitor.Visibility = showSaved ? Visibility.Collapsed : Visibility.Visible;

            if (!showSaved)
            {
                if (!_liveMonitoringActive) ToggleLiveMonitor_Click(this, new RoutedEventArgs());
                PollLiveWifi();
            }
            else if (_liveMonitoringActive)
            {
                ToggleLiveMonitor_Click(this, new RoutedEventArgs()); // stop monitoring when leaving the tab
            }
        }

        private void ToggleLiveMonitor_Click(object sender, RoutedEventArgs e)
        {
            _liveMonitoringActive = !_liveMonitoringActive;
            if (_liveMonitoringActive)
            {
                if (_liveTimer == null)
                {
                    _liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _liveTimer.Tick += (_, _) => PollLiveWifi();
                }
                _liveTimer.Start();
                _btnLiveMonitor.Content = SMDWin.Views.MainWindow.PT("Stop live monitoring");
                _btnLiveMonitor.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
                if (_chartTitle != null) _chartTitle.Text = SMDWin.Views.MainWindow.PT("Wi-Fi networks nearby — signal over the last 10 minutes");
            }
            else
            {
                _liveTimer?.Stop();
                _btnLiveMonitor.Content = SMDWin.Views.MainWindow.PT("Start live monitoring");
                _btnLiveMonitor.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                if (_chartTitle != null) _chartTitle.Text = SMDWin.Views.MainWindow.PT("Wi-Fi networks nearby — paused");
            }
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // column labels
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list

            // Header bar
            var header = new Border
            {
                Background   = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Padding      = new Thickness(16,10,16,10)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock { Text = SMDWin.Views.MainWindow.PT("Wi-Fi networks"),
                FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") });
            titleStack.Children.Add(new TextBlock { Text = SMDWin.Views.MainWindow.PT("Saved Wi-Fi networks on this PC, with passwords"),
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,2,0,0) });
            Grid.SetColumn(titleStack, 0); headerGrid.Children.Add(titleStack);

            _btnExport = NetWindowHelper.SecondaryButton(SMDWin.Views.MainWindow.PT("Export CSV"), SMDWin.Views.MainWindow.PT("Export network list to CSV file"));
            _btnExport.Height = 28; _btnExport.Padding = new Thickness(10,5,10,5);
            _btnExport.Click += WifiExportCsv_Click;
            Grid.SetColumn(_btnExport, 1); headerGrid.Children.Add(_btnExport);

            _btnScan = new Button { Visibility = Visibility.Collapsed };

            header.Child = headerGrid;
            NetWindowHelper.MakeDraggable(header, this);
            Grid.SetRow(header, 0); root.Children.Add(header);

            // Column labels
            var colHeader = new Border { Background = NetWindowHelper.Res("BgHoverBrush"),
                Padding = new Thickness(12,6,12,6) };
            var colGrid = new Grid();
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            void ColLbl(string t, int col) {
                var tb = new TextBlock { Text = t, FontSize = Ui.FontXS, FontWeight = FontWeights.Bold,
                    Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
                Grid.SetColumn(tb, col); colGrid.Children.Add(tb);
            }
            ColLbl(SMDWin.Views.MainWindow.PT("Network (SSID)"), 0); ColLbl(SMDWin.Views.MainWindow.PT("Password"), 1); ColLbl(SMDWin.Views.MainWindow.PT("Security"), 2);
            var actLbl = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Actions"), FontSize = Ui.FontXS, FontWeight = FontWeights.Bold,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(actLbl, 3); colGrid.Children.Add(actLbl);
            colHeader.Child = colGrid;
            Grid.SetRow(colHeader, 1); root.Children.Add(colHeader);

            // List + empty placeholder
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listHost = new StackPanel();
            _list = new StackPanel();
            _txtEmpty = new TextBlock
            {
                Text = SMDWin.Views.MainWindow.PT("Loading saved Wi-Fi networks…"),
                FontSize = Ui.FontM, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                Margin = new Thickness(16,20,16,20), HorizontalAlignment = HorizontalAlignment.Center
            };
            listHost.Children.Add(_list);
            listHost.Children.Add(_txtEmpty);
            sv.Content = listHost;
            Grid.SetRow(sv, 2); root.Children.Add(sv);
            return root;
        }

        private Grid BuildLivePage()
        {
            var root = new Grid { Margin = new Thickness(20, 12, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status cards
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // best point
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // chart

            // ── Status cards ──────────────────────────────────────────────────
            var statusGrid = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(0, 0, 0, 12) };
            (TextBlock label, TextBlock value) MakeStatCard(string label)
            {
                var card = new Border { Background = NetWindowHelper.Res("BgCardBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8) };
                var stack = new StackPanel();
                var lbl = new TextBlock { Text = label, FontSize = Ui.FontXS, Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
                var val = new TextBlock { Text = "—", FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                    Foreground = NetWindowHelper.Res("TextPrimaryBrush"), Margin = new Thickness(0, 2, 0, 0) };
                stack.Children.Add(lbl); stack.Children.Add(val);
                card.Child = stack;
                statusGrid.Children.Add(card);
                return (lbl, val);
            }
            (_, _liveSsid)     = MakeStatCard(SMDWin.Views.MainWindow.PT("Network (SSID)"));
            (_, _liveSignalPct) = MakeStatCard(SMDWin.Views.MainWindow.PT("Signal"));
            (_, _liveBand)     = MakeStatCard(SMDWin.Views.MainWindow.PT("Band"));
            (_, _liveSpeed)    = MakeStatCard(SMDWin.Views.MainWindow.PT("Link Speed"));
            (_, _livePing)     = MakeStatCard(SMDWin.Views.MainWindow.PT("Latency (8.8.8.8)"));
            (_, _liveSecurity) = MakeStatCard(SMDWin.Views.MainWindow.PT("Security"));
            _liveAdapter = new TextBlock { Text = "", FontSize = Ui.FontXS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Opacity = 0.7 };

            // the drawn router, with the connection's signal bars, next to the status cards
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var routerCard = new Border { Background = NetWindowHelper.Res("BgCardBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Padding = new Thickness(14, 10, 14, 8), Margin = new Thickness(0, 0, 12, 20), Width = 176 };
            var routerStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _routerPicture = HardwareIllustrations.WifiRouter(0, true, false);
            _routerPicture.Width = 132; _routerPicture.Height = 108; _routerPicture.HorizontalAlignment = HorizontalAlignment.Center;
            routerStack.Children.Add(_routerPicture);
            _routerCaption = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Waiting for live data"), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            routerStack.Children.Add(_routerCaption);
            _routerName = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Looking for the router…"), FontSize = Ui.FontXS, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 0), MaxHeight = 30,
                TextTrimming = TextTrimming.CharacterEllipsis };   // two lines at most, the rest as "…" (full text in the tooltip)
            routerStack.Children.Add(_routerName);
            routerCard.Child = routerStack;
            header.Children.Add(routerCard);
            Grid.SetColumn(statusGrid, 1); header.Children.Add(statusGrid);
            Grid.SetRow(header, 0); root.Children.Add(header);

            var bestBorder = new Border { Background = NetWindowHelper.Res("BgHoverBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12) };
            var bestGrid = new Grid();
            bestGrid.ColumnDefinitions.Add(new ColumnDefinition());
            bestGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _liveBestPoint = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Best signal seen this session: — (press “Start live monitoring” at the top right and walk around — we'll track the strongest point automatically). Windows may ask once for location access: it treats the Wi-Fi details as location; they are only shown here."),
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_liveBestPoint, 0); bestGrid.Children.Add(_liveBestPoint);
            _btnLiveMonitor = new Wpf.Ui.Controls.Button
            {
                Content = SMDWin.Views.MainWindow.PT("Start live monitoring"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Padding = new Thickness(12, 5, 12, 6), MinHeight = 30, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,   // no fixed height: the text descenders ("g") were cut off
            };
            _btnLiveMonitor.Click += ToggleLiveMonitor_Click;
            Grid.SetColumn(_btnLiveMonitor, 1); bestGrid.Children.Add(_btnLiveMonitor);
            bestBorder.Child = bestGrid;
            Grid.SetRow(bestBorder, 1); root.Children.Add(bestBorder);

            // ── Chart ────────────────────────────────────────────────────────
            var chartBorder = new Border { Background = NetWindowHelper.Res("BgCardBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(12) };
            var chartStack = new Grid();
            chartStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _chartTitle = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Wi-Fi networks nearby — signal over the last 10 minutes"), FontSize = Ui.FontS,
                FontWeight = FontWeights.SemiBold, Foreground = NetWindowHelper.Res("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(_chartTitle, 0); chartStack.Children.Add(_chartTitle);
            var plotRow = new Grid();
            plotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            plotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            _signalCanvas = new Canvas { ClipToBounds = false, Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(78, 6, 8, 22) };
            _signalCanvas.SizeChanged += (_, _) => RedrawSignalChart();
            plotRow.Children.Add(_signalCanvas);
            _legend = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            var legendScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _legend };
            Grid.SetColumn(legendScroll, 1); plotRow.Children.Add(legendScroll);
            Grid.SetRow(plotRow, 1); chartStack.Children.Add(plotRow);
            chartBorder.Child = chartStack;
            Grid.SetRow(chartBorder, 2); root.Children.Add(chartBorder);

            return root;
        }

        private bool _pollBusy;

        /// <summary>
        /// The Wi-Fi read runs in the background: the first full read can wait for Windows' location question (Windows 11
        /// 24H2+) for as long as the question stays open — on the UI thread that froze the whole window (49 s in a self-test).
        /// </summary>
        private void PollLiveWifi()
        {
            if (_pollBusy) return;
            _pollBusy = true;
            _ = Task.Run(() =>
            {
                SMDWin.Services.WifiInfo? info = null;
                try { info = SMDWin.Services.WifiReader.Read(forceRefresh: true, allowLocationPrompt: true); }
                catch (Exception ex) { AppLogger.Debug($"PollLiveWifi read: {ex.Message}"); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (info != null) ShowLiveWifi(info); }
                    finally { _pollBusy = false; }
                }));
            });
        }

        private void ShowLiveWifi(SMDWin.Services.WifiInfo info)
        {
            try
            {
                // the only place that may trigger Windows' location question (Windows 11 24H2+): opened by the user, with the
                // explanation below the cards
                ShowLocationNote(SMDWin.Services.WifiReader.LocationDenied);
                if (!info.IsConnected)
                {
                    _liveSsid.Text = LanguageService.S("Network", "NotConnected");
                    _routerPicture.Drawing = NetworkArt.BuildWifi(0, false);
                    _routerCaption.Text = _liveSsid.Text;
                    return;
                }
                _liveSsid.Text = info.Ssid;
                _routerPicture.Drawing = NetworkArt.BuildWifi(info.SignalPct, true);
                _routerCaption.Text = $"{info.Ssid} · {NetworkArt.SignalBars(info.SignalPct)}/4";
                _liveSignalPct.Text = $"{info.SignalPct}%  ({info.Rssi} dBm, {info.RssiLabel})";
                _liveBand.Text = string.IsNullOrEmpty(info.Band) ? "—" : info.Band;
                _liveSpeed.Text = info.LinkSpeedMbps > 0 ? $"{info.LinkSpeedMbps} Mbps" : "—";
                _liveSecurity.Text = string.IsNullOrEmpty(info.Security) ? "—" : info.Security;

                _signalHistory.Add((DateTime.Now, info.Rssi, info.SignalPct));
                if (_signalHistory.Count > LiveHistoryMaxPoints) _signalHistory.RemoveAt(0);

                if (info.Rssi > _bestRssiSeen)
                {
                    _bestRssiSeen = info.Rssi;
                    _bestRssiTimeUtc = DateTime.Now;
                    _liveBestPoint.Text = SMDWin.Views.MainWindow.PT("Best signal seen this session: {0} dBm at {1:HH:mm:ss} — you were closest to the router around then", _bestRssiSeen, _bestRssiTimeUtc);
                }

                if (!SMDWin.Services.WifiReader.LocationDenied) ReadNearbyIfDue(info.Ssid);   // netsh's list needs location too
                RedrawSignalChart();

                // Latency — a simple ping to 8.8.8.8, in the background (the UI timer is not blocked)
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        using var ping = new System.Net.NetworkInformation.Ping();
                        var r = await ping.SendPingAsync("8.8.8.8", 1200);
                        long ms = r.Status == System.Net.NetworkInformation.IPStatus.Success ? r.RoundtripTime : -1;
                        Dispatcher.Invoke(() =>
                        {
                            _livePing.Text = ms >= 0 ? $"{ms} ms" : "timeout";
                            _pingHistory.Add((DateTime.Now, ms));
                            if (_pingHistory.Count > LiveHistoryMaxPoints) _pingHistory.RemoveAt(0);
                        });
                    }
                    catch { Dispatcher.Invoke(() => _livePing.Text = "—"); }
                });
            }
            catch (Exception ex) { AppLogger.Debug($"PollLiveWifi: {ex.Message}"); }
        }

        private Border? _locationNote;

        /// <summary>
        /// Windows 11 counts the Wi-Fi details as location data. When access is off, a short note says why band, security and the
        /// nearby networks are missing, with a link to the Windows setting. Before the first answer, a one-line explanation.
        /// </summary>
        private void ShowLocationNote(bool denied)
        {
            try
            {
                if (_locationNote == null)
                {
                    if (!denied) return;
                    var tb = new TextBlock
                    {
                        Text = SMDWin.Views.MainWindow.PT("Windows treats the Wi-Fi details (band, security, nearby networks) as location information, and location access is off for SMD Win. The network name and signal are still shown. Nothing is sent anywhere — to see the rest, allow SMD Win in Windows Settings › Privacy & security › Location."),
                        FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                    };
                    var link = new Wpf.Ui.Controls.Button
                    {
                        Content = SMDWin.Views.MainWindow.PT("Open location settings"), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 5),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    link.Click += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); }
                        catch (Exception ex) { AppLogger.Debug($"Open location settings: {ex.Message}"); }
                    };
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.Children.Add(tb); Grid.SetColumn(link, 1); g.Children.Add(link);
                    _locationNote = new Border
                    {
                        Background = NetWindowHelper.Res("BgHoverBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                        Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12), Child = g,
                    };
                    if (_liveBestPoint.Parent is Grid bg && bg.Parent is Border bb && bb.Parent is Grid root)
                    {
                        // under the "best signal" row, above the chart
                        int row = Grid.GetRow(bb);
                        root.RowDefinitions.Insert(row + 1, new RowDefinition { Height = GridLength.Auto });
                        foreach (UIElement c in root.Children) if (Grid.GetRow(c) > row) Grid.SetRow(c, Grid.GetRow(c) + 1);
                        Grid.SetRow(_locationNote, row + 1);
                        root.Children.Add(_locationNote);
                    }
                }
                _locationNote.Visibility = denied ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { AppLogger.Debug($"ShowLocationNote: {ex.Message}"); }
        }

        // The router brand and model: the gateway MAC (the manufacturer) and, when the router answers UPnP, the model
        private async Task LoadRouterIdentityAsync()
        {
            if (_routerIdentityBusy) return;
            _routerIdentityBusy = true;
            try
            {
                var id = await Task.Run(() => SMDWin.Services.RouterIdentityReader.ReadAsync());
                if (id == null) { _routerName.Text = SMDWin.Views.MainWindow.PT("No router found"); return; }
                string name = id.DisplayName;
                _routerName.Text = name.Length > 0 ? name : id.Gateway;
                _routerName.SetResourceReference(TextBlock.ForegroundProperty, id.HasModel ? "TextPrimaryBrush" : "TextSecondaryBrush");
                _routerName.ToolTip = string.Join("\n", new[]
                {
                    $"IP {id.Gateway}",
                    id.Mac.Length > 0 ? $"MAC {id.Mac}" : "",
                    id.Vendor.Length > 0 ? $"Vendor (from MAC): {id.Vendor}" : "",
                    id.Manufacturer.Length > 0 ? $"Manufacturer (UPnP): {id.Manufacturer}" : "",
                    id.Model.Length > 0 ? $"Model (UPnP): {id.Model}" : id.HasModel ? "" : SMDWin.Views.MainWindow.PT("Model: the router does not announce it"),
                }.Where(x => x.Length > 0));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "NetWifiPage router identity");
                _routerName.Text = SMDWin.Views.MainWindow.PT("Router unknown");
            }
            finally { _routerIdentityBusy = false; }
        }

        // The networks around: a background read at most every 6 s (netsh is slow), history per network
        private void ReadNearbyIfDue(string connectedSsid)
        {
            if (_nearbyBusy || (DateTime.Now - _lastNearbyRead).TotalSeconds < 6) return;
            _nearbyBusy = true; _lastNearbyRead = DateTime.Now;
            _ = Task.Run(() =>
            {
                var found = SMDWin.Services.WifiNearbyReader.Read();
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var now = DateTime.Now;
                        var best = new Dictionary<string, (int pct, int ch)>();
                        foreach (var n in found)
                            if (!best.TryGetValue(n.Ssid, out var cur) || n.SignalPct > cur.pct) best[n.Ssid] = (n.SignalPct, n.Channel);
                        _nearbyNow.Clear();
                        foreach (var (ssid, v) in best)
                        {
                            _nearbyNow[ssid] = v;
                            if (!_nearbyHist.TryGetValue(ssid, out var h)) _nearbyHist[ssid] = h = new();
                            h.Add((now, v.pct));
                            h.RemoveAll(x => (now - x.t).TotalMinutes > 10);
                        }
                        // networks gone from the list are deleted when they have no more points in the 10-minute window
                        foreach (var key in _nearbyHist.Keys.ToList())
                            if (_nearbyHist[key].Count == 0) _nearbyHist.Remove(key);
                        RedrawSignalChart();
                    }
                    finally { _nearbyBusy = false; }
                });
            });
        }

        private static System.Windows.Media.Brush SeriesBrush(int i)
            => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SeriesHex[i % SeriesHex.Length]));

        // A chart with all the networks around (the connected one thick and in the accent colour), the last 10 minutes
        private void RedrawSignalChart()
        {
            try
            {
                _signalCanvas.Children.Clear();
                _legend.Children.Clear();
                double w = _signalCanvas.ActualWidth, h = _signalCanvas.ActualHeight;
                if (w < 40 || h < 40) return;
                var now = DateTime.Now;
                const double windowSec = 600;
                string connected = _liveSsid.Text;

                // the connected network: the 2 s history; the others: the strongest 7
                var own = _signalHistory.Where(p => (now - p.t).TotalSeconds <= windowSec).ToList();
                var others = _nearbyHist.Where(kv => kv.Key != connected)
                    .OrderByDescending(kv => kv.Value.Count > 0 ? kv.Value[^1].pct : 0).Take(7).ToList();

                // the vertical axis: from the weakest point seen (rounded) to 100 %
                int minPct = 100;
                foreach (var p in own) minPct = Math.Min(minPct, p.pct);
                foreach (var kv in others) foreach (var p in kv.Value) minPct = Math.Min(minPct, p.pct);
                int lo = Math.Max(0, Math.Min(60, (minPct - 10) / 10 * 10));
                double Y(int pct) => h - h * (pct - lo) / (100.0 - lo);
                double X(DateTime t) => w - w * (now - t).TotalSeconds / windowSec;

                ChartGrid.AddTo(_signalCanvas, new Rect(0, 0, w, h), 1);   // the shared grid: vertical lines; horizontal ones at the labels
                for (int pct = lo; pct <= 100; pct += (100 - lo) > 50 ? 20 : 10)
                {
                    double y = Y(pct);
                    if (y > 1 && y < h - 1) ChartGrid.AddHLine(_signalCanvas, 0, w, y);
                    var lbl = new TextBlock { Text = $"{pct}% · {pct / 2 - 100} dBm", FontSize = Ui.FontXS, Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
                    Canvas.SetLeft(lbl, -74); Canvas.SetTop(lbl, y - 8);
                    _signalCanvas.Children.Add(lbl);
                }
                for (int min = 0; min <= 10; min += 2)
                {
                    double x = w - w * (min * 60.0) / windowSec;
                    var lbl = new TextBlock { Text = min == 0 ? SMDWin.Views.MainWindow.PT("now") : $"-{min} min", FontSize = Ui.FontXS, Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
                    Canvas.SetLeft(lbl, Math.Max(0, Math.Min(w - 36, x - 16))); Canvas.SetTop(lbl, h + 2);
                    _signalCanvas.Children.Add(lbl);
                }

                void Line(IEnumerable<(DateTime t, int pct)> pts, System.Windows.Media.Brush stroke, double thick)
                {
                    var pc = new System.Windows.Media.PointCollection();
                    foreach (var p in pts) pc.Add(new Point(X(p.t), Y(p.pct)));
                    if (pc.Count < 2) return;
                    _signalCanvas.Children.Add(new System.Windows.Shapes.Polyline
                    { Points = pc, Stroke = stroke, StrokeThickness = thick, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round });
                }

                int idx = 0;
                foreach (var kv in others)
                {
                    var br = SeriesBrush(idx++);
                    Line(kv.Value.Where(p => (now - p.t).TotalSeconds <= windowSec), br, 1.5);
                    AddLegend(br, kv.Key, kv.Value.Count > 0 ? kv.Value[^1].pct : 0, _nearbyNow.TryGetValue(kv.Key, out var v) ? v.channel : 0, false);
                }
                if (own.Count > 0)
                {
                    var accent = NetWindowHelper.Res("AccentBrush");
                    Line(own.Select(p => (p.t, p.pct)), accent, 3);
                    AddLegend(accent, string.IsNullOrEmpty(connected) ? "—" : connected, own[^1].pct, 0, true);
                }
                // reteaua conectata prima in legenda
                if (_legend.Children.Count > 1) { var last = _legend.Children[^1]; _legend.Children.RemoveAt(_legend.Children.Count - 1); _legend.Children.Insert(0, last); }
            }
            catch (Exception ex) { AppLogger.Debug($"RedrawSignalChart: {ex.Message}"); }
        }

        private void AddLegend(System.Windows.Media.Brush brush, string name, int pct, int channel, bool connected)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sw = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = brush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var tx = new StackPanel();
            tx.Children.Add(new TextBlock { Text = connected ? name + " ✓" : name, FontSize = Ui.FontS, FontWeight = connected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
            string detail = $"{pct}%  ·  {pct / 2 - 100} dBm" + (channel > 0 ? $"  ·  ch {channel}" : "");
            tx.Children.Add(new TextBlock { Text = detail, FontSize = Ui.FontXS, Foreground = NetWindowHelper.Res("TextSecondaryBrush") });
            Grid.SetColumn(tx, 1); row.Children.Add(sw); row.Children.Add(tx);
            _legend.Children.Add(row);
        }

        private async void WifiScan_Click(object s, RoutedEventArgs e)
        {
            _btnScan.IsEnabled = false; _btnScan.Content = LanguageService.S("Network", "Reading");
            _txtEmpty.Visibility = Visibility.Collapsed;
            _list.Children.Clear();
            try
            {
                var entries = await Task.Run(ReadWifiProfiles);
                _entries = entries;
                BuildWifiList(entries);
                if (entries.Count == 0)
                {
                    _txtEmpty.Text = SMDWin.Views.MainWindow.PT("No saved Wi-Fi networks found, or Admin rights required.");
                    _txtEmpty.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                _txtEmpty.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message;
                _txtEmpty.Visibility = Visibility.Visible;
            }
            finally { _btnScan.IsEnabled = true; _btnScan.Content = LanguageService.S("Network", "Scan"); }
        }

        private static List<WifiEntry> ReadWifiProfiles()
        {
            var result = new List<WifiEntry>();
            // the WLAN API first: it works on any Windows language (the netsh text below is translated, "All User Profile"
            // exists only on an English Windows)
            var api = SMDWin.Services.WlanProfiles.Read();
            if (api != null)
            {
                foreach (var p in api) result.Add(new WifiEntry(p.Ssid, p.Password, p.Auth, p.Cipher));
                return result;
            }
            try
            {
                var listProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "netsh", "wlan show profiles")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                string listOut = (listProc != null ? SMDWin.Services.ProcessRunner.ReadStdoutBounded(listProc, 10000) : null) ?? "";
                listProc?.WaitForExit();

                foreach (var line in listOut.Split('\n'))
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0 || !line.Contains("All User Profile")) continue;
                    string ssid = line[(colonIdx+1)..].Trim();
                    if (string.IsNullOrEmpty(ssid)) continue;

                    var detailProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "netsh", $"wlan show profile name=\"{ssid}\" key=clear")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    string detail = (detailProc != null ? SMDWin.Services.ProcessRunner.ReadStdoutBounded(detailProc, 10000) : null) ?? "";
                    detailProc?.WaitForExit();

                    string password = "—", auth = "—", encryption = "—";
                    foreach (var dl in detail.Split('\n'))
                    {
                        if (dl.Contains("Key Content")    && dl.Contains(':')) password   = dl[(dl.IndexOf(':')+1)..].Trim();
                        if (dl.Contains("Authentication") && dl.Contains(':') && !dl.Contains("Network")) auth = dl[(dl.IndexOf(':')+1)..].Trim();
                        if (dl.Contains("Cipher")         && dl.Contains(':')) encryption = dl[(dl.IndexOf(':')+1)..].Trim();
                    }
                    result.Add(new WifiEntry(ssid, password, auth, encryption));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ReadWifiProfiles"); }
            return result;
        }

        private void BuildWifiList(List<WifiEntry> entries)
        {
            _list.Children.Clear();
            if (entries.Count == 0) return;

            bool isDark = ThemeManager.IsDark(SettingsService.Current.ThemeName);
            var clrRow    = isDark ? WpfColor.FromRgb(22,30,42)   : WpfColor.FromRgb(255,255,255);
            var clrRowAlt = isDark ? WpfColor.FromRgb(25,33,46)   : WpfColor.FromRgb(245,248,252);
            var clrSep    = isDark ? WpfColor.FromArgb(25,255,255,255) : WpfColor.FromArgb(25,0,0,0);
            var clrPrimary = isDark ? WpfColor.FromRgb(203,213,225) : WpfColor.FromRgb(30,41,59);
            var clrSec     = WpfColor.FromRgb(100,116,139);
            var clrReveal  = isDark ? WpfColor.FromRgb(96,165,250) : WpfColor.FromRgb(37,99,235);

            int idx = 0;
            foreach (var wifi in entries)
            {
                bool isOdd = idx % 2 == 1;
                bool hasPassword = wifi.Password != "—" && !string.IsNullOrEmpty(wifi.Password);
                bool revealed = false;

                var row = new Border
                {
                    Background      = new SolidColorBrush(isOdd ? clrRowAlt : clrRow),
                    BorderThickness = new Thickness(0,0,0,1),
                    BorderBrush     = new SolidColorBrush(clrSep),
                    Padding         = new Thickness(12,8,12,8),
                    Cursor          = hasPassword ? System.Windows.Input.Cursors.Hand : null,
                    ToolTip         = hasPassword ? SMDWin.Views.MainWindow.PT("Double-click to show / hide password") : null
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

                var ssidBlock = new TextBlock { Text = wifi.Ssid, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(clrPrimary), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(ssidBlock, 0); grid.Children.Add(ssidBlock);

                var pwdBlock = new TextBlock
                {
                    Text = hasPassword ? new string('•', Math.Min(wifi.Password.Length, 10)) : "—",
                    FontSize = Ui.FontS, FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(hasPassword ? clrPrimary : clrSec),
                    VerticalAlignment = VerticalAlignment.Center, Tag = wifi.Password
                };
                Grid.SetColumn(pwdBlock, 1); grid.Children.Add(pwdBlock);

                var authBlock = new TextBlock { Text = wifi.Auth, FontSize = Ui.FontXS,
                    Foreground = new SolidColorBrush(clrSec), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(authBlock, 2); grid.Children.Add(authBlock);

                var actPanel = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

                if (hasPassword)
                {
                    string capturedPwd = wifi.Password;
                    string capturedSsid = wifi.Ssid;

                    var copyBtn = MakeActionChip("Copy", isDark,
                        WpfColor.FromArgb(50,80,130,220), WpfColor.FromArgb(80,96,165,250),
                        WpfColor.FromArgb(40,37,99,235),  WpfColor.FromArgb(100,37,99,235),
                        isDark ? WpfColor.FromRgb(147,197,253) : WpfColor.FromRgb(37,99,235));
                    copyBtn.MouseLeftButtonUp += (_, _) =>
                    {
                        System.Windows.Clipboard.SetText(capturedPwd);
                        NetWindowHelper.ShowInfo(SMDWin.Views.MainWindow.PT("Password copied!"), "SMD Win", this);
                    };
                    actPanel.Children.Add(copyBtn);

                    var qrBtn = MakeActionChip("QR", isDark,
                        WpfColor.FromArgb(50,60,160,100), WpfColor.FromArgb(80,74,222,128),
                        WpfColor.FromArgb(40,22,163,74),  WpfColor.FromArgb(100,22,163,74),
                        isDark ? WpfColor.FromRgb(74,222,128) : WpfColor.FromRgb(22,163,74));
                    qrBtn.Margin = new Thickness(6,0,0,0);
                    qrBtn.MouseLeftButtonUp += (_, _) => ShowQr(capturedSsid, capturedPwd, isDark);
                    actPanel.Children.Add(qrBtn);

                    string capturedPwdRow = wifi.Password;
                    row.MouseLeftButtonDown += (_, mouseArgs) =>
                    {
                        if (mouseArgs.ClickCount == 2)
                        {
                            revealed = !revealed;
                            pwdBlock.Text = revealed ? capturedPwdRow : new string('•', Math.Min(capturedPwdRow.Length, 10));
                            pwdBlock.Foreground = new SolidColorBrush(revealed ? clrReveal : clrPrimary);
                        }
                    };
                }
                Grid.SetColumn(actPanel, 3); grid.Children.Add(actPanel);
                row.Child = grid;
                _list.Children.Add(row);
                idx++;
            }
            _txtEmpty.Visibility = Visibility.Collapsed;
        }

        private static Border MakeActionChip(string text, bool isDark,
            WpfColor bgDark, WpfColor borderDark, WpfColor bgLight, WpfColor borderLight, WpfColor fg)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Background      = new SolidColorBrush(isDark ? bgDark : bgLight),
                BorderBrush     = new SolidColorBrush(isDark ? borderDark : borderLight),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(9,4,9,4),
                Cursor          = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = text, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(fg) }
            };
        }

        private void WifiExportCsv_Click(object s, RoutedEventArgs e)
        {
            if (_entries.Count == 0)
            { NetWindowHelper.ShowInfo(SMDWin.Views.MainWindow.PT("Scan Wi-Fi networks first."), "SMD Win", this); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "wifi_passwords", DefaultExt = ".csv", Filter = "CSV files|*.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("SSID,Password,Authentication,Encryption");
                foreach (var w in _entries)
                    sb.AppendLine($"\"{w.Ssid}\",\"{w.Password}\",\"{w.Auth}\",\"{w.Encryption}\"");
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                NetWindowHelper.ShowInfo("Exported: " + System.IO.Path.GetFileName(dlg.FileName), "SMD Win", this);
            }
            catch (Exception ex) { NetWindowHelper.ShowInfo("Export error: " + ex.Message, "SMD Win", this); }
        }

        private void ShowQr(string ssid, string password, bool isDark)
        {
            string qrData = $"WIFI:T:WPA;S:{EscapeWifiQr(ssid)};P:{EscapeWifiQr(password)};;";
            var fgColor = isDark ? WpfColor.FromRgb(220,230,255) : WpfColor.FromRgb(10,15,40);
            var bgColor = isDark ? WpfColor.FromRgb(12,18,32)    : WpfColor.FromRgb(248,250,255);

            var dlg = new Window
            {
                Title = SMDWin.Views.MainWindow.PT("Wi-Fi QR Code"), SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = new SolidColorBrush(WpfColor.FromArgb(0,0,0,0)),
                Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            bool closing = false;
            void SafeClose() { if (closing) return; closing = true; dlg.Dispatcher.BeginInvoke(new Action(() => { try { if (dlg.IsLoaded) dlg.Close(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } })); }
            dlg.KeyDown     += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) { ke.Handled = true; SafeClose(); } };
            dlg.Deactivated += (_, _) => SafeClose();

            var outer = new Border { Margin = new Thickness(12), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60,100,140,255)),
                BorderThickness = new Thickness(1.5), Padding = new Thickness(24,20,24,20) };

            var root2 = new StackPanel { Width = 260 };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new TextBlock { Text = SMDWin.Views.MainWindow.PT("Wi-Fi QR Code"), FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(fgColor), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleTb, 0); titleGrid.Children.Add(titleTb);
            var closeBtn = Net.NetWindowHelper.CloseIconButton(32);
            closeBtn.Click += (_, _) => SafeClose();
            Grid.SetColumn(closeBtn, 1); titleGrid.Children.Add(closeBtn);
            root2.Children.Add(titleGrid);

            root2.Children.Add(new TextBlock { Text = ssid, FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fgColor),
                Margin = new Thickness(0,8,0,12), HorizontalAlignment = HorizontalAlignment.Center });

            var qrImage = RenderQrCode(qrData, 220, isDark);
            root2.Children.Add(new System.Windows.Controls.Image { Source = qrImage, Width = 220, Height = 220,
                Margin = new Thickness(0,0,0,12), HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true });

            root2.Children.Add(new TextBlock { Text = SMDWin.Views.MainWindow.PT("Scan with your phone to connect"),
                FontSize = Ui.FontS, Foreground = new SolidColorBrush(WpfColor.FromRgb(100,116,139)),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });

            outer.Child = root2; dlg.Content = outer; dlg.ShowDialog();
        }

        private static string EscapeWifiQr(string s) =>
            s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\"", "\\\"");

        private static BitmapSource RenderQrCode(string data, int size, bool darkTheme)
        {
            bool[,] matrix = QrCodeMatrix.Generate(data);
            int modules = matrix.GetLength(0);
            int cell = Math.Max(1, size / modules);
            int imgSize = modules * cell;
            var dark  = darkTheme ? WpfColor.FromRgb(220,230,255) : WpfColor.FromRgb(0,0,0);
            var light = darkTheme ? WpfColor.FromRgb(12,18,32)    : WpfColor.FromRgb(255,255,255);
            var bmp = new WriteableBitmap(imgSize, imgSize, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
            bmp.Lock();
            unsafe
            {
                var ptr = (byte*)bmp.BackBuffer;
                int stride = bmp.BackBufferStride;
                for (int my = 0; my < modules; my++)
                for (int mx = 0; mx < modules; mx++)
                {
                    var c = matrix[my, mx] ? dark : light;
                    for (int py = 0; py < cell; py++)
                    for (int px = 0; px < cell; px++)
                    {
                        int x = mx*cell+px, y = my*cell+py;
                        byte* p = ptr + y*stride + x*4;
                        p[0]=c.B; p[1]=c.G; p[2]=c.R; p[3]=255;
                    }
                }
            }
            bmp.AddDirtyRect(new Int32Rect(0, 0, imgSize, imgSize));
            bmp.Unlock();
            return bmp;
        }
    }
}
