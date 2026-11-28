using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SMDWin.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Pen = System.Windows.Media.Pen;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SMDWin.Views.Net
{
    /// <summary>
    /// Network › Wi-Fi › Channel analysis: every access point in range (WifiScanner), drawn as hills on the channels of a band,
    /// with the connected network's details, the interference on its channel, the recommendations (WifiAnalysis) and the full
    /// table. Scans every 10 s while the tab is visible; nothing runs when it is hidden.
    /// </summary>
    public sealed class WifiChannelsView : Grid
    {
        private static string PT(string en, params object?[] a) => MainWindow.PT(en, a);
        private static Brush R(string key) => NetWindowHelper.Res(key);

        private string _band = "";
        private WifiScanResult? _last;
        private bool _busy, _active;
        private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

        private readonly Dictionary<string, Button> _bandButtons = new();
        private readonly TextBlock _scanInfo = new();
        private readonly Border _locationNote = new();
        private readonly WifiChannelChart _chart = new() { MinHeight = 300 };
        private readonly TextBlock _chartSub = new();
        private readonly StackPanel _connBody = new();
        private readonly StackPanel _interBody = new();
        private readonly StackPanel _recoBody = new();
        private readonly DataGrid _grid = NetWindowHelper.StandardDataGrid();
        private readonly CheckBox _allBands = new();
        private readonly TextBlock _tableCount = new();

        public WifiChannelsView()
        {
            Margin = new Thickness(20, 4, 20, 16);
            _timer.Tick += (_, _) => ScanNow();
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var stack = new StackPanel();
            sv.Content = stack;
            Children.Add(sv);

            stack.Children.Add(BuildTopBar());
            BuildLocationNote();
            stack.Children.Add(_locationNote);

            // row 1: the chart + your connection
            var row1 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            var chartStack = new StackPanel();
            _chartSub.FontSize = Ui.FontXS; _chartSub.Foreground = R("TextSecondaryBrush"); _chartSub.TextWrapping = TextWrapping.Wrap; _chartSub.Margin = new Thickness(0, -6, 0, 8);
            chartStack.Children.Add(_chartSub);
            _chart.Height = 320;
            chartStack.Children.Add(_chart);
            chartStack.Children.Add(Legend());
            var chartCard = NetWindowHelper.CardWithHead(PT("Channels and signal"), null, chartStack);
            chartCard.Margin = new Thickness(0, 0, 12, 0);
            row1.Children.Add(chartCard);
            var conn = NetWindowHelper.CardWithHead(PT("Your connection"), null, _connBody);
            Grid.SetColumn(conn, 1); row1.Children.Add(conn);
            stack.Children.Add(row1);

            // row 2: interference + recommendations
            var row2 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row2.ColumnDefinitions.Add(new ColumnDefinition());
            row2.ColumnDefinitions.Add(new ColumnDefinition());
            var inter = NetWindowHelper.CardWithHead(PT("Interference"), null, _interBody);
            inter.Margin = new Thickness(0, 0, 6, 0);
            row2.Children.Add(inter);
            var reco = NetWindowHelper.CardWithHead(PT("Recommendations"), null, _recoBody);
            reco.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(reco, 1); row2.Children.Add(reco);
            stack.Children.Add(row2);

            // row 3: every access point
            stack.Children.Add(BuildTable());

            stack.Children.Add(new TextBlock
            {
                Text = PT("What Windows reports is what the adapter hears from here: a network may look different next to the router. Clients and channel busy appear only when the router announces them. The channel width and the standard come from what each router announces, not from a measurement."),
                FontSize = Ui.FontXS, Foreground = R("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 0),
            });

            Render();
        }

        // ── lifecycle ───────────────────────────────────────────────────────────────────────────────────────────────────────
        public void Start()
        {
            if (_active) return;
            _active = true;
            _timer.Start();
            ScanNow(first: _last == null);
        }

        public void Stop() { _active = false; _timer.Stop(); }

        private async void ScanNow(bool first = false)
        {
            if (_busy) return;
            _busy = true;
            _scanInfo.Text = PT("Scanning…");
            try
            {
                // the list Windows already has, at once on the first opening; then a fresh scan, read a few seconds later
                if (first)
                {
                    var quick = await Task.Run(WifiScanner.Read);
                    if (_active) { _last = quick; Render(); }
                }
                await Task.Run(WifiScanner.TriggerScan);
                await Task.Delay(4000);
                if (!_active) return;
                var r = await Task.Run(WifiScanner.Read);
                if (!_active) return;
                _last = r; Render();
            }
            catch (Exception ex) { AppLogger.Debug($"WifiChannelsView.ScanNow: {ex.Message}"); }
            finally { _busy = false; UpdateScanInfo(); }
        }

        private void UpdateScanInfo()
        {
            if (_last == null) { _scanInfo.Text = ""; return; }
            _scanInfo.Text = PT("{0} access points · scanned at {1} · every 10 s", _last.Aps.Count, _last.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture))
                             + (_last.Adapter.Length > 0 ? " · " + _last.Adapter : "");
        }

        // ── layout pieces ───────────────────────────────────────────────────────────────────────────────────────────────────
        private FrameworkElement BuildTopBar()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new[] { "2.4", "5", "6" })
            {
                var btn = new Wpf.Ui.Controls.Button
                {
                    Content = b + " GHz", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 2, 0), Height = 28,
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                };
                string band = b;
                btn.Click += (_, _) => { _band = band; Render(); };
                _bandButtons[b] = btn;
                seg.Children.Add(btn);
            }
            var track = new Border { Child = seg, Tag = "SegTrack", VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(FrameworkElement.StyleProperty, "SegmentTrackStyle");
            g.Children.Add(track);
            foreach (var b in _bandButtons.Values) MainWindow.SetTabButtonActive(b as Wpf.Ui.Controls.Button, false);

            _scanInfo.FontSize = Ui.FontXS; _scanInfo.Foreground = R("TextSecondaryBrush");
            _scanInfo.VerticalAlignment = VerticalAlignment.Center; _scanInfo.Margin = new Thickness(14, 0, 10, 0);
            _scanInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_scanInfo, 1); g.Children.Add(_scanInfo);

            var scan = NetWindowHelper.SecondaryButton(PT("Scan now"), PT("Ask the adapter for a fresh scan"));
            scan.Height = 28; scan.Padding = new Thickness(10, 5, 10, 5);
            scan.Click += (_, _) => ScanNow();
            Grid.SetColumn(scan, 2); g.Children.Add(scan);
            return g;
        }

        private void BuildLocationNote()
        {
            var tb = new TextBlock
            {
                Text = PT("Windows treats the list of nearby networks as location information, and location access is off for SMD Win — the analysis needs it. Nothing is sent anywhere."),
                FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Foreground = R("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var link = new Wpf.Ui.Controls.Button { Content = PT("Open location settings"), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 5), VerticalAlignment = VerticalAlignment.Center };
            link.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); }
                catch (Exception ex) { AppLogger.Debug($"Open location settings: {ex.Message}"); }
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(tb); Grid.SetColumn(link, 1); g.Children.Add(link);
            _locationNote.Background = R("BgHoverBrush");
            _locationNote.CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard());
            _locationNote.Padding = new Thickness(12, 8, 12, 8);
            _locationNote.Margin = new Thickness(0, 0, 0, 12);
            _locationNote.Child = g;
            _locationNote.Visibility = Visibility.Collapsed;
        }

        private FrameworkElement Legend()
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            void Item(Brush b, string text, string tip, bool dashed = false)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 2), ToolTip = tip };
                var r = new System.Windows.Shapes.Rectangle { Width = 14, Height = 8, Stroke = b, StrokeThickness = 1.5, Fill = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                if (dashed) r.StrokeDashArray = new DoubleCollection { 3, 2 };
                sp.Children.Add(r);
                sp.Children.Add(new TextBlock { Text = text, FontSize = Ui.FontXS, Foreground = R("TextSecondaryBrush") });
                wp.Children.Add(sp);
            }
            Item(R("AccentBrush"), PT("your network"), PT("The access point this PC is connected to (filled)."));
            Item(R("AccentBrush"), PT("other units of your network"), PT("Other access points with the same name or the same MAC prefix: the router's other band, mesh units, extenders."), true);
            Item(R("StatusOkBrush"), PT("recommended channel"), PT("The least crowded channel worth choosing on this band."));
            Item(R("StatusCritBrush"), PT("DFS channels"), PT("DFS (Dynamic Frequency Selection): 5 GHz channels 52–144, shared with weather and airport radars. The router must listen for radar up to a minute before using them and must leave at once when it hears one. Usually empty — but some phones and older devices ignore them."));
            return wp;
        }

        private FrameworkElement BuildTable()
        {
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // the standard table of the app (the header height and padding are the same as in the other pages)
            _grid.RowHeight = 30; _grid.CanUserSortColumns = true; _grid.CanUserResizeColumns = true;
            _grid.MaxHeight = 460; _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            void Col(string header, string path, double width, string? tip = null, string? sortPath = null, string? brushPath = null, bool bold = false)
            {
                var c = new DataGridTextColumn
                {
                    Header = tip == null ? header : new TextBlock { Text = header, ToolTip = tip },
                    Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width),
                    SortMemberPath = sortPath ?? path,
                };
                var st = new Style(typeof(TextBlock));
                st.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                if (brushPath != null) st.Setters.Add(new Setter(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(brushPath)));
                if (bold) st.Setters.Add(new Setter(TextBlock.FontWeightProperty, new System.Windows.Data.Binding("Weight")));
                c.ElementStyle = st;
                _grid.Columns.Add(c);
            }
            Col(PT("Network"), "Name", 170, bold: true);
            Col("BSSID", "Bssid", 128, PT("The MAC address of the access point — one router has one per band."));
            Col(PT("Manufacturer"), "Vendor", 120, PT("From the first half of the MAC address. Empty when the router uses a random MAC."));
            Col(PT("Band"), "BandText", 68, null, "BandSort");
            Col(PT("Channel"), "Channel", 80);
            Col(PT("Width"), "WidthText", 72, PT("How many MHz the network takes: 20 MHz = one channel; 40 / 80 / 160 MHz join neighbouring channels — faster, but more exposed to neighbours."), "Width");
            Col(PT("Signal"), "SignalText", 80, PT("Received power, in dBm: −50 excellent, −67 good, −75 weak, under −80 unusable."), "Rssi", "SignalBrush");
            Col(PT("Standard"), "Standard", 88, PT("Wi-Fi 4 = 802.11n, Wi-Fi 5 = 802.11ac, Wi-Fi 6 / 6E = 802.11ax, Wi-Fi 7 = 802.11be."));
            Col(PT("Streams"), "StreamsText", 82, PT("Spatial streams (MIMO): how many antennas transmit at the same time. 2×2 doubles the speed of 1×1."), "Streams");
            Col(PT("Max speed"), "RateText", 94, PT("The highest link speed the access point announces, with the best signal. Real transfers are about half of it."), "Rate");
            Col(PT("Security"), "Security", 130, null, null, "SecurityBrush");
            Col(PT("Clients"), "ClientsText", 74, PT("Devices connected to it, when the router announces it (BSS Load)."), "ClientsSort");
            Col(PT("Busy"), "BusyText", 64, PT("How much of the time the router hears its channel busy, when it announces it (BSS Load)."), "BusySort");
            Col(PT("Seen for"), "SeenText", 88, PT("Since when this window has seen it."), "SeenSort");

            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            _allBands.Content = PT("All bands"); _allBands.FontSize = Ui.FontS; _allBands.VerticalAlignment = VerticalAlignment.Center;
            _allBands.Checked += (_, _) => FillTable(); _allBands.Unchecked += (_, _) => FillTable();
            DockPanel.SetDock(_allBands, Dock.Right); head.Children.Add(_allBands);
            _tableCount.FontSize = Ui.FontXS; _tableCount.Foreground = R("TextSecondaryBrush"); _tableCount.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(_tableCount);
            body.Children.Add(head);
            Grid.SetRow(_grid, 1); body.Children.Add(_grid);
            return NetWindowHelper.CardWithHead(PT("Access points in range"), null, body);
        }

        // ── render ──────────────────────────────────────────────────────────────────────────────────────────────────────────
        private void Render()
        {
            var r = _last;
            _locationNote.Visibility = r?.AccessDenied == true && r.Aps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var aps = r?.Aps ?? new List<WifiAp>();
            var me = aps.FirstOrDefault(a => a.Connected);
            if (_band.Length == 0 && r != null)
                _band = me?.Band is { Length: > 0 } mb ? mb : aps.GroupBy(a => a.Band).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "2.4";
            string band = _band.Length == 0 ? "2.4" : _band;
            foreach (var (b, btn) in _bandButtons)
            {
                int n = aps.Count(a => a.Band == b);
                btn.Content = n > 0 ? $"{b} GHz · {n}" : $"{b} GHz";
                MainWindow.SetTabButtonActive(btn as Wpf.Ui.Controls.Button, b == band);
            }
            UpdateScanInfo();

            var best = WifiAnalysis.Best(aps, band, me);
            int bestCh = aps.Any(a => a.Band == band) ? best.FirstOrDefault().ch : 0;
            _chart.Show(aps, band, me, bestCh);
            _chartSub.Text = band switch
            {
                "2.4" => PT("2.4 GHz: only channels 1, 6 and 11 do not overlap each other (dotted lines). A network between them disturbs both sides."),
                "5" => PT("5 GHz: 20 MHz channels side by side; wider networks join neighbours into 40 / 80 / 160 MHz blocks. The red area is DFS."),
                _ => PT("6 GHz (Wi-Fi 6E / 7): only recent devices see it; wide, clean channels. Phones look first at the PSC channels."),
            };

            RenderConnection(aps, me);
            RenderInterference(aps, band, me);
            RenderRecommendations(aps, band, me, best);
            FillTable();
        }

        private static TextBlock Small(string text, string brush = "TextSecondaryBrush") => new()
        {
            Text = text, FontSize = Ui.FontXS, Foreground = R(brush), TextWrapping = TextWrapping.Wrap,
        };

        private void KeyValue(Panel host, string key, string value, string? tip = null, string valueBrush = "TextPrimaryBrush")
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var k = new TextBlock { Text = key, FontSize = Ui.FontS, Foreground = R("TextSecondaryBrush"), ToolTip = tip, VerticalAlignment = VerticalAlignment.Top };
            if (tip != null) k.TextDecorations = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline, Pen = new Pen(R("BorderBrush2"), 1) { DashStyle = DashStyles.Dot } } };
            var v = new TextBlock { Text = value, FontSize = Ui.FontS, Foreground = R(valueBrush), TextWrapping = TextWrapping.Wrap };
            g.Children.Add(k); Grid.SetColumn(v, 1); g.Children.Add(v);
            host.Children.Add(g);
        }

        private static string SecText(string s) => s == "Open" ? PT("Open (no password)") : s;

        private static string SignalBrushKey(int rssi) => rssi >= -60 ? "StatusOkBrush" : rssi >= -70 ? "TextPrimaryBrush" : rssi >= -80 ? "StatusWarnBrush" : "StatusCritBrush";

        private static string SignalWord(int rssi) => rssi >= -50 ? PT("excellent") : rssi >= -60 ? PT("very good") : rssi >= -67 ? PT("good") : rssi >= -75 ? PT("weak") : PT("very weak");

        private void RenderConnection(List<WifiAp> aps, WifiAp? me)
        {
            _connBody.Children.Clear();
            if (me == null)
            {
                _connBody.Children.Add(Small(_last == null ? PT("Reading…") : PT("This PC is not connected to a Wi-Fi network (or Windows did not report which one).")));
                return;
            }
            var title = new TextBlock { Text = WifiAnalysis.Name(me), FontSize = Ui.FontXL, FontWeight = FontWeights.SemiBold, Foreground = R("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            _connBody.Children.Add(title);
            var sig = new TextBlock { FontSize = Ui.FontS, Margin = new Thickness(0, 0, 0, 8) };
            sig.Inlines.Add(new System.Windows.Documents.Run($"{me.Rssi} dBm") { Foreground = R(SignalBrushKey(me.Rssi)), FontWeight = FontWeights.SemiBold });
            sig.Inlines.Add(new System.Windows.Documents.Run(" · " + SignalWord(me.Rssi)) { Foreground = R("TextSecondaryBrush") });
            _connBody.Children.Add(sig);

            KeyValue(_connBody, PT("Access point"), me.Bssid + (me.Vendor.Length > 0 ? "\n" + me.Vendor : ""), PT("The MAC address of the access point — one router has one per band."));
            KeyValue(_connBody, PT("Band · channel"), PT("{0} GHz · channel {1} · {2} MHz", me.Band, me.Channel, me.WidthMHz) + (me.IsDfs ? " · DFS" : ""));
            KeyValue(_connBody, PT("Frequency"), PT("{0} MHz (block {1}–{2} MHz)", me.FrequencyMHz, me.LowMHz, me.HighMHz), PT("The centre of the primary channel; the block is the whole width the network uses."));
            KeyValue(_connBody, PT("Standard"), $"{me.Standard} · {me.Streams}×{me.Streams} MIMO", PT("Spatial streams (MIMO): how many antennas transmit at the same time. 2×2 doubles the speed of 1×1."));
            KeyValue(_connBody, PT("Max speed"), PT("{0} Mb/s announced", me.MaxRateMbps), PT("The highest link speed the access point announces, with the best signal. Real transfers are about half of it."));
            KeyValue(_connBody, PT("Security"), SecText(me.Security) + (me.Pmf.Length > 0 ? " · PMF " + PT(me.Pmf) : ""),
                PT("PMF (Protected Management Frames, 802.11w): stops the fake “disconnect” packets used to kick devices off a network. Required by WPA3."),
                me.WeakSecurity || me.OpenNetwork ? "StatusCritBrush" : "TextPrimaryBrush");
            if (me.Clients.HasValue || me.UtilizationPct.HasValue)
                KeyValue(_connBody, PT("Load"), PT("{0} clients · channel busy {1}%", me.Clients?.ToString(CultureInfo.InvariantCulture) ?? "—", me.UtilizationPct ?? 0),
                    PT("Announced by the router (BSS Load): devices connected and how much of the time it hears the channel busy."),
                    me.UtilizationPct >= 50 ? "StatusWarnBrush" : "TextPrimaryBrush");
            if (me.Country.Length > 0) KeyValue(_connBody, PT("Country"), me.Country, PT("The country the router is set for — it decides the allowed channels and power."));

            var units = aps.Where(a => a != me && WifiAnalysis.IsOwn(a, me)).OrderBy(a => a.Band).ThenByDescending(a => a.Rssi).ToList();
            if (units.Count > 0)
            {
                _connBody.Children.Add(new TextBlock { Text = PT("Other units of your network ({0})", units.Count), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = R("TextPrimaryBrush"), Margin = new Thickness(0, 10, 0, 2),
                    ToolTip = PT("Other access points with the same name or the same MAC prefix: the router's other band, mesh units, extenders.") });
                foreach (var u in units.Take(8))
                {
                    var t = new TextBlock { FontSize = Ui.FontXS, Margin = new Thickness(0, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis };
                    t.Inlines.Add(new System.Windows.Documents.Run(PT("{0} GHz · ch {1}", u.Band, u.Channel) + "  ") { Foreground = R("TextPrimaryBrush") });
                    t.Inlines.Add(new System.Windows.Documents.Run($"{u.Rssi} dBm") { Foreground = R(SignalBrushKey(u.Rssi)) });
                    t.Inlines.Add(new System.Windows.Documents.Run("  " + u.Bssid) { Foreground = R("TextSecondaryBrush") });
                    _connBody.Children.Add(t);
                }
            }
        }

        private void RenderInterference(List<WifiAp> aps, string band, WifiAp? me)
        {
            _interBody.Children.Clear();
            var onBand = aps.Where(a => a.Band == band).ToList();
            if (onBand.Count == 0) { _interBody.Children.Add(Small(_last == null ? PT("Reading…") : PT("No networks on this band."))); return; }

            // crowding per channel, as bars
            int[] chs = band == "2.4" ? WifiAnalysis.Ch24 : band == "5" ? WifiAnalysis.Ch5 : WifiAnalysis.Psc6;
            _interBody.Children.Add(Small(PT("How crowded each channel is (the other networks that reach it, the loud ones counting more):")));
            var bars = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 2), Height = 86 };
            foreach (int c in chs)
            {
                double v = WifiAnalysis.Crowding(aps, band, c, me);
                double lvl = Math.Clamp(Math.Log10(1 + v) / 3.6, 0, 1);
                string key = lvl < 0.45 ? "StatusOkBrush" : lvl < 0.7 ? "StatusWarnBrush" : "StatusCritBrush";
                var cell = new Grid { Margin = new Thickness(1, 0, 1, 0), ToolTip = PT("Channel {0}: {1} network(s) overlap it", c, onBand.Count(a => a.LowMHz < WifiAnalysis.FrequencyOf(band, c) + 10 && a.HighMHz > WifiAnalysis.FrequencyOf(band, c) - 10 && !WifiAnalysis.IsOwn(a, me))) };
                cell.RowDefinitions.Add(new RowDefinition());
                cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var bar = new Border { Background = R(key), Height = Math.Max(3, 64 * lvl), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2, 2, 0, 0), Opacity = me?.Channel == c ? 1 : 0.75 };
                if (me != null && me.Band == band && me.Channel == c) { bar.BorderBrush = R("AccentBrush"); bar.BorderThickness = new Thickness(2); }
                cell.Children.Add(bar);
                var lbl = new TextBlock { Text = c.ToString(CultureInfo.InvariantCulture), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, Foreground = R(me?.Band == band && me.Channel == c ? "AccentBrush" : "TextSecondaryBrush") };
                if (chs.Length > 16 && chs.ToList().IndexOf(c) % 2 == 1 && me?.Channel != c) lbl.Text = "";
                Grid.SetRow(lbl, 1); cell.Children.Add(lbl);
                bars.Children.Add(cell);
            }
            _interBody.Children.Add(bars);

            if (me == null || me.Band != band)
            {
                _interBody.Children.Add(Small(me == null ? PT("Connect to a network to see the interference on its channel.") : PT("Your network is on {0} GHz — switch the band above to see the interference on its channel.", me.Band)));
                return;
            }
            var (co, adj) = WifiAnalysis.Interference(aps, me);
            int strong = co.Concat(adj).Count(a => a.Rssi >= -67);
            var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 6) };
            void Stat(string value, string label, string tip, string brush)
            {
                var sp = new StackPanel { ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
                sp.Children.Add(new TextBlock { Text = value, FontSize = Ui.FontXXL, FontWeight = FontWeights.SemiBold, Foreground = R(brush) });
                sp.Children.Add(new TextBlock { Text = label, FontSize = Ui.FontXS, Foreground = R("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
                stats.Children.Add(sp);
            }
            Stat(co.Count.ToString(CultureInfo.InvariantCulture), PT("same channel"), PT("Co-channel: networks on exactly your channel. They hear each other and take turns — slower, but orderly."), co.Count >= 3 ? "StatusWarnBrush" : "TextPrimaryBrush");
            Stat(adj.Count.ToString(CultureInfo.InvariantCulture), PT("partial overlap"), PT("Adjacent-channel interference: networks whose block covers part of yours on another channel. They cannot coordinate and are heard as noise — the worse kind."), adj.Count > 0 ? "StatusCritBrush" : "StatusOkBrush");
            Stat(strong.ToString(CultureInfo.InvariantCulture), PT("strong (≥ −67 dBm)"), PT("Of those, the ones heard loud enough to really compete for air time."), strong > 0 ? "StatusWarnBrush" : "StatusOkBrush");
            Stat(me.UtilizationPct is int u ? u + "%" : "—", PT("channel busy"), PT("Announced by the router (BSS Load): devices connected and how much of the time it hears the channel busy."), me.UtilizationPct >= 50 ? "StatusWarnBrush" : "TextPrimaryBrush");
            _interBody.Children.Add(stats);

            foreach (var a in co.Select(a => (a, PT("same channel"))).Concat(adj.Select(a => (a, PT("partial overlap · {0} MHz", WifiAnalysis.OverlapMHz(a, me))))).OrderByDescending(x => x.a.Rssi).Take(6))
            {
                var t = new TextBlock { FontSize = Ui.FontXS, Margin = new Thickness(0, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = WifiChannelChart.TipText(a.a) };
                t.Inlines.Add(new System.Windows.Documents.Run(WifiAnalysis.Name(a.a)) { Foreground = R("TextPrimaryBrush"), FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run(PT("  · ch {0} · ", a.a.Channel)) { Foreground = R("TextSecondaryBrush") });
                t.Inlines.Add(new System.Windows.Documents.Run($"{a.a.Rssi} dBm") { Foreground = R(SignalBrushKey(a.a.Rssi)) });
                t.Inlines.Add(new System.Windows.Documents.Run(" · " + a.Item2) { Foreground = R("TextSecondaryBrush") });
                _interBody.Children.Add(t);
            }
            if (co.Count + adj.Count == 0) _interBody.Children.Add(Small(PT("No other network shares your channel — clean air."), "StatusOkBrush"));
        }

        private void RenderRecommendations(List<WifiAp> aps, string band, WifiAp? me, List<(int ch, double crowding)> best)
        {
            _recoBody.Children.Clear();
            if (!aps.Any(a => a.Band == band)) { _recoBody.Children.Add(Small(_last == null ? PT("Reading…") : PT("No networks on this band."))); return; }
            int bestCh = best.First().ch;
            int width = WifiAnalysis.RecommendedWidth(aps, band, bestCh, me);
            var head = new Border { Background = R("BgHoverBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8) };
            var hs = new StackPanel();
            hs.Children.Add(new TextBlock { Text = PT("Best channel on {0} GHz: {1} · {2} MHz", band, bestCh, width), FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Foreground = R("StatusOkBrush") });
            string why = band == "2.4" ? PT("Only 1, 6 and 11 are considered — the others overlap two of them. 20 MHz is the right width on 2.4 GHz.")
                       : band == "5" ? PT("Channels without DFS are preferred (no radar pause). 80 MHz only when the whole block is quiet.")
                       : PT("PSC channels are preferred — phones find them first.");
            hs.Children.Add(Small(why));
            if (me != null && me.Band == band)
            {
                bool already = me.Channel == bestCh || best.Take(1).Any(b => Math.Abs(b.crowding - WifiAnalysis.Crowding(aps, band, me.Channel, me)) < 1);
                hs.Children.Add(Small(already ? PT("Your network is already on a good channel.") : PT("Your network is on channel {0}. Change it in the router's Wi-Fi settings (wireless › channel) — or leave “Auto” if the router picks well.", me.Channel),
                    already ? "StatusOkBrush" : "StatusWarnBrush"));
            }
            head.Child = hs;
            _recoBody.Children.Add(head);

            // the ranking of the candidates
            var rank = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (ch, cr) in best.Take(band == "2.4" ? 3 : 6))
            {
                double lvl = Math.Clamp(Math.Log10(1 + cr) / 3.6, 0, 1);
                var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1),
                    BorderBrush = R(lvl < 0.45 ? "StatusOkBrush" : lvl < 0.7 ? "StatusWarnBrush" : "StatusCritBrush"), ToolTip = PT("Crowding score {0:0} (lower is better)", cr) };
                chip.Child = new TextBlock { Text = PT("ch {0}", ch) + (me?.Band == band && me.Channel == ch ? " · " + PT("yours") : ""), FontSize = Ui.FontXS, Foreground = R("TextPrimaryBrush") };
                rank.Children.Add(chip);
            }
            _recoBody.Children.Add(rank);

            foreach (var f in WifiAnalysis.Findings(aps, band, me))
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                string key = f.Severity switch { 0 => "StatusOkBrush", 1 => "TextSecondaryBrush", 2 => "StatusWarnBrush", _ => "StatusCritBrush" };
                g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = R(key), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                var t = new TextBlock { Text = PT(f.Text, f.Args), FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Foreground = R("TextPrimaryBrush") };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                _recoBody.Children.Add(g);
            }
        }

        // ── the table ───────────────────────────────────────────────────────────────────────────────────────────────────────
        public sealed class Row
        {
            public string Name { get; init; } = "";
            public FontWeight Weight { get; init; }
            public string Bssid { get; init; } = "";
            public string Vendor { get; init; } = "";
            public string BandText { get; init; } = "";
            public int BandSort { get; init; }
            public int Channel { get; init; }
            public int Width { get; init; }
            public string WidthText { get; init; } = "";
            public int Rssi { get; init; }
            public string SignalText { get; init; } = "";
            public Brush SignalBrush { get; init; } = Brushes.Gray;
            public string Standard { get; init; } = "";
            public int Streams { get; init; }
            public string StreamsText { get; init; } = "";
            public int Rate { get; init; }
            public string RateText { get; init; } = "";
            public string Security { get; init; } = "";
            public Brush SecurityBrush { get; init; } = Brushes.Gray;
            public int ClientsSort { get; init; }
            public string ClientsText { get; init; } = "";
            public int BusySort { get; init; }
            public string BusyText { get; init; } = "";
            public double SeenSort { get; init; }
            public string SeenText { get; init; } = "";
        }

        private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? PT("{0} s", (int)t.TotalSeconds) : t.TotalHours < 1 ? PT("{0} min", (int)t.TotalMinutes) : PT("{0} h {1} min", (int)t.TotalHours, t.Minutes);

        private void FillTable()
        {
            var aps = _last?.Aps ?? new List<WifiAp>();
            string band = _band.Length == 0 ? "2.4" : _band;
            var shown = aps.Where(a => _allBands.IsChecked == true || a.Band == band).ToList();
            var now = DateTime.Now;
            var sortCol = _grid.Columns.FirstOrDefault(c => c.SortDirection != null);
            var sortDir = sortCol?.SortDirection;
            var rows = shown.OrderByDescending(a => a.Connected).ThenByDescending(a => a.Rssi).Select(a => new Row
            {
                Name = (a.Connected ? "● " : "") + WifiAnalysis.Name(a),
                Weight = a.Connected ? FontWeights.SemiBold : FontWeights.Normal,
                Bssid = a.Bssid, Vendor = a.Vendor,
                BandText = a.Band + " GHz", BandSort = a.Band == "2.4" ? 0 : a.Band == "5" ? 1 : 2,
                Channel = a.Channel,
                Width = a.WidthMHz, WidthText = a.WidthMHz + " MHz",
                Rssi = a.Rssi, SignalText = a.Rssi + " dBm", SignalBrush = R(SignalBrushKey(a.Rssi)),
                Standard = a.Standard, Streams = a.Streams, StreamsText = a.Streams + "×" + a.Streams,
                Rate = a.MaxRateMbps, RateText = a.MaxRateMbps + " Mb/s",
                Security = SecText(a.Security) + (a.Pmf == "required" ? " · PMF" : "") + (a.Wps ? " · WPS" : ""),
                SecurityBrush = R(a.WeakSecurity || a.OpenNetwork ? "StatusCritBrush" : "TextPrimaryBrush"),
                ClientsSort = a.Clients ?? -1, ClientsText = a.Clients?.ToString(CultureInfo.InvariantCulture) ?? "—",
                BusySort = a.UtilizationPct ?? -1, BusyText = a.UtilizationPct is int u ? u + "%" : "—",
                SeenSort = (now - a.FirstSeen).TotalSeconds, SeenText = Ago(now - a.FirstSeen),
            }).ToList();
            _grid.ItemsSource = rows;
            // keep the column the user sorted by across refreshes
            if (sortCol != null && sortDir != null)
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_grid.ItemsSource);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(sortCol.SortMemberPath, sortDir.Value));
                sortCol.SortDirection = sortDir;
            }
            _tableCount.Text = _allBands.IsChecked == true ? PT("{0} access points on all bands", rows.Count) : PT("{0} access points on {1} GHz", rows.Count, band);
        }
    }
}
