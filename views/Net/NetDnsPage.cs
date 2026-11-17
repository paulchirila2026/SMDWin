using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

using Button            = System.Windows.Controls.Button;
using FontFamily        = System.Windows.Media.FontFamily;
using Orientation       = System.Windows.Controls.Orientation;
using TextBox           = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SMDWin.Views.Net
{
    /// <summary>
    /// DNS lookup: the host addresses, how long the resolution took, 4 pings with bars and the computer's DNS servers,
    /// on cards (not console text).
    /// </summary>
    public class NetDnsPage : NetToolPage
    {
        private const int PingCount = 4;

        private TextBox   _txtHost = null!;
        private Button    _btnLookup = null!;
        private TextBlock _tileName = null!, _tileResolve = null!, _tilePing = null!;
        private TextBlock _status = null!;
        private StackPanel _addresses = null!, _pings = null!, _resolvers = null!;
        private bool _busy;

        public NetDnsPage()
        {
            NetWindowHelper.SetupWindow(this, "DNS Lookup — SMDWin", 620, 500);
            Content = BuildUI();
            ShowResolvers();
        }

        private static string T(string key, string fallback) => LanguageService.S("NetTools", key, fallback);

        private static FontFamily Mono => System.Windows.Application.Current.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // antet
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // placute
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // stare

            var header = NetWindowHelper.ToolHeader(T("DnsLookupTitle", "DNS Lookup"),
                T("DnsLookupDesc", "DNS record lookup + ping for any domain or IP"), out var actions);
            actions.Children.Add(NetWindowHelper.HeaderLabel(T("HostIpLabel", "Host / IP:")));
            _txtHost = NetWindowHelper.HeaderBox("google.com", 190);
            _txtHost.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) Lookup_Click(s, new RoutedEventArgs()); };
            actions.Children.Add(_txtHost);
            _btnLookup = NetWindowHelper.PrimaryButton("🔍 " + T("LookupButton", "Lookup"), "Resolve DNS records and ping this host");
            _btnLookup.Click += Lookup_Click;
            actions.Children.Add(_btnLookup);
            Grid.SetRow(header, 0); root.Children.Add(header);
            NetWindowHelper.MakeDraggable(header, this);

            // three tiles: name, resolve time, average ping
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 10) };
            tiles.Children.Add(Tile(T("DnsTileName", "NAME"), out _tileName));
            tiles.Children.Add(Tile(T("DnsTileResolve", "RESOLVED IN"), out _tileResolve));
            tiles.Children.Add(Tile(T("DnsTilePing", "PING (avg)"), out _tilePing));
            Grid.SetRow(tiles, 1); root.Children.Add(tiles);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _addresses = new StackPanel();
            _addresses.Children.Add(NetWindowHelper.Hint(T("DnsAddressesHint", "After Lookup, the addresses this name points to appear here.")));
            var addressCard = NetWindowHelper.CardWithHead(T("DnsAddressesCaption", "Addresses"), T("DnsAddressesName", "Where this name points"),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _addresses }, "M12 2a10 10 0 1 0 0 20a10 10 0 0 0 0-20 M2 12h20 M12 2c3 3 3 17 0 20 M12 2c-3 3-3 17 0 20");
            Grid.SetColumn(addressCard, 0); body.Children.Add(addressCard);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _pings = new StackPanel();
            _pings.Children.Add(NetWindowHelper.Hint(T("DnsPingHint", "After Lookup, each reply appears here with its time.")));
            var pingCard = NetWindowHelper.CardWithHead(T("DnsPingCaption", "Ping test"), string.Format(T("DnsPingNameFmt", "{0} replies, one after another"), PingCount),
                _pings, "M3 12h4l3-8 4 16 3-8h4");
            Grid.SetRow(pingCard, 0); right.Children.Add(pingCard);
            _resolvers = new StackPanel();
            var resolverCard = NetWindowHelper.CardWithHead(T("DnsResolverCaption", "Your DNS servers"), T("DnsResolverName", "Who answers the questions"),
                _resolvers, "M4 6h16 M4 12h16 M4 18h10");
            Grid.SetRow(resolverCard, 2); right.Children.Add(resolverCard);
            Grid.SetColumn(right, 2); body.Children.Add(right);
            Grid.SetRow(body, 2); root.Children.Add(body);

            _status = NetWindowHelper.StatusText(T("DnsEnterHostHint", "Enter a domain or IP and press Lookup…"));
            _status.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_status, 3); root.Children.Add(_status);
            return root;
        }

        private static Border Tile(string caption, out TextBlock value)
        {
            var sp = new StackPanel();
            var cap = new TextBlock { Text = caption, FontSize = 10, FontWeight = FontWeights.SemiBold };
            cap.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            value = new TextBlock { Text = "—", FontSize = Ui.FontXXL, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(cap); sp.Children.Add(value);
            var card = NetWindowHelper.ContentCard(sp);
            card.Margin = new Thickness(0, 0, 10, 0);
            return card;
        }

        private static TextBlock Line(string text, string brushKey, double size, bool semibold = false, bool mono = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            if (mono) tb.FontFamily = Mono;
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        private static Border Pill(string text, string brushKey)
        {
            var label = Line(text, brushKey, 10, semibold: true);
            var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 10, 0),
                Child = label, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            pill.SetResourceReference(Border.BorderBrushProperty, brushKey);
            return pill;
        }

        // The DNS servers of the active adapters (they do not depend on the host searched)
        private void ShowResolvers()
        {
            _resolvers.Children.Clear();
            try
            {
                var servers = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    .SelectMany(n => n.GetIPProperties().DnsAddresses.Select(a => (Adapter: n.Name, Address: a)))
                    .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork || !x.Address.IsIPv6LinkLocal)
                    .Distinct().Take(4).ToList();
                if (servers.Count == 0)
                    _resolvers.Children.Add(Line(T("DnsNoResolvers", "No DNS server found."), "TextSecondaryBrush", Ui.FontS));
                foreach (var (adapter, address) in servers)
                {
                    var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                    row.Children.Add(Line(address.ToString(), "TextPrimaryBrush", Ui.FontM, semibold: true, mono: true));
                    row.Children.Add(Line(adapter, "TextSecondaryBrush", Ui.FontXS));
                    _resolvers.Children.Add(row);
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
            {
                AppLogger.Debug($"DNS page resolvers: {ex.Message}");
                _resolvers.Children.Add(Line(T("DnsNoResolvers", "No DNS server found."), "TextSecondaryBrush", Ui.FontS));
            }
        }

        public override bool SelfTestStart() { Lookup_Click(this, new RoutedEventArgs()); return true; }

        private async void Lookup_Click(object s, RoutedEventArgs e)
        {
            string host = _txtHost.Text.Trim();
            if (string.IsNullOrEmpty(host) || _busy) return;
            _busy = true; _btnLookup.IsEnabled = false;
            _addresses.Children.Clear(); _pings.Children.Clear();
            _tileName.Text = "…"; _tileResolve.Text = "…"; _tilePing.Text = "…";
            _status.Text = string.Format(T("DnsResolvingFmt", "Resolving {0}…"), host);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var entry = await Dns.GetHostEntryAsync(host);
                    sw.Stop();
                    _tileName.Text = entry.HostName;
                    _tileName.ToolTip = entry.Aliases.Length > 0 ? string.Join("\n", entry.Aliases) : entry.HostName;
                    _tileResolve.Text = sw.ElapsedMilliseconds < 1 ? "< 1 ms" : $"{sw.ElapsedMilliseconds} ms";
                    foreach (var ip in entry.AddressList.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                        bool v4 = ip.AddressFamily == AddressFamily.InterNetwork;
                        row.Children.Add(Pill(v4 ? "IPv4" : "IPv6", v4 ? "AccentBrush" : "TextSecondaryBrush"));
                        row.Children.Add(Line(ip.ToString(), "TextPrimaryBrush", Ui.FontM, semibold: true, mono: true));
                        _addresses.Children.Add(row);
                    }
                    if (entry.AddressList.Length == 0)
                        _addresses.Children.Add(Line(T("DnsNoAddresses", "The name has no address."), "TextSecondaryBrush", Ui.FontS));
                }
                catch (SocketException ex)
                {
                    sw.Stop();
                    _tileName.Text = "—"; _tileResolve.Text = "—"; _tilePing.Text = "—";
                    _addresses.Children.Add(Line(string.Format(T("DnsResolutionFailedFmt", "Resolution failed: {0}"), ex.Message), "StatusCritBrush", Ui.FontS));
                    _status.Text = T("DnsFailedStatus", "The name could not be resolved.");
                    return;
                }

                await RunPingsAsync(host);
                _status.Text = string.Format(T("DnsDoneFmt", "Done · {0}"), host);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "DNS lookup " + host);
                _status.Text = string.Format(LanguageService.S("Common", "ErrorFmt", "Error: {0}"), ex.Message);
            }
            finally { _busy = false; _btnLookup.IsEnabled = true; }
        }

        private async Task RunPingsAsync(string host)
        {
            var times = new List<long>();
            var rows = new List<(ColumnDefinition BarCol, ColumnDefinition RestCol, long Ms)>();
            using var ping = new Ping();
            for (int i = 0; i < PingCount; i++)
            {
                long ms = -1; string failure = "";
                try
                {
                    var reply = await ping.SendPingAsync(host, 2000);
                    if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime; else failure = reply.Status.ToString();
                }
                catch (PingException ex) { failure = ex.InnerException?.Message ?? ex.Message; }

                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
                row.Children.Add(Line("#" + (i + 1), "TextSecondaryBrush", Ui.FontS));
                var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
                track.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
                bar.SetResourceReference(Border.BackgroundProperty, ms >= 0 ? "AccentBrush" : "StatusCritBrush");
                // the bar and the rest of the track are two "star" columns: the base width matches the given space by itself
                var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                var barCol = new ColumnDefinition(); var restCol = new ColumnDefinition();
                barHost.ColumnDefinitions.Add(barCol); barHost.ColumnDefinitions.Add(restCol);
                Grid.SetColumnSpan(track, 2);
                barHost.Children.Add(track); barHost.Children.Add(bar);
                Grid.SetColumn(barHost, 1); row.Children.Add(barHost);
                var text = Line(ms >= 0 ? $"{ms} ms" : failure, ms >= 0 ? "TextPrimaryBrush" : "StatusCritBrush", Ui.FontS, semibold: true);
                text.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(text, 2); row.Children.Add(text);
                _pings.Children.Add(row);

                if (ms >= 0) times.Add(ms);
                rows.Add((barCol, restCol, ms));
                // bars proportional to the slowest reply so far
                long max = Math.Max(1, times.Count > 0 ? times.Max() : 1);
                foreach (var (barColumn, restColumn, value) in rows)
                {
                    long shown = value >= 0 ? Math.Max(value, 1) : 0;
                    barColumn.Width = new GridLength(shown, GridUnitType.Star);
                    restColumn.Width = new GridLength(Math.Max(0, max - shown) + 0.0001, GridUnitType.Star);
                }
                _tilePing.Text = times.Count > 0 ? $"{times.Average():0} ms" : "—";
                if (i < PingCount - 1) await Task.Delay(300);
            }
            int lost = PingCount - times.Count;
            if (lost > 0) _tilePing.ToolTip = string.Format(T("DnsPingLostFmt", "{0} of {1} lost"), lost, PingCount);
        }
    }
}
