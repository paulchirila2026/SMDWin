using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using SMDWin.Views.Net;

namespace SMDWin.Views
{
    /// <summary>
    /// Network tools on the Network page: a menu at the top (Overview + the 12 tools) and, below it, the chosen tool. The
    /// pages are created on first open and stay alive: a running test continues when the tab changes.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<string, NetToolPage> _netToolPages = new();
        private readonly Dictionary<string, System.Windows.Controls.Button> _netToolTabs = new();
        private Grid? _netToolHost;
        private string _netToolKey = "Overview";

        private static readonly (string Key, string Label)[] NetToolMenu =
        {
            ("Overview", "Overview"), ("SpeedTest", "Speed test"), ("Ping", "Ping"),
            ("Traceroute", "Traceroute"), ("Dns", "DNS"), ("Ports", "Ports"), ("Lan", "LAN"),
            ("Wifi", "Wi-Fi"), ("Firewall", "Firewall"), ("Apps", "Apps"), ("Fixes", "Quick fixes"),
        };

        /// <summary>Adds the menu and the tool area to the Network page (rows 1 and 2; Overview moves to row 2).</summary>
        private void InitNetToolMenu()
        {
            if (_netToolHost != null) return;
            PanelNetwork.RowDefinitions.Clear();
            PanelNetwork.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelNetwork.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelNetwork.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(NetTabOverview, 2);

            var menu = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            // the shared grey track (variant B): the tabs sit on it, the chosen one is a lighter pill
            var menuTrack = new Border { Child = menu, Tag = "SegTrack", Margin = new Thickness(0, 0, 0, 10) };
            menuTrack.SetResourceReference(StyleProperty, "SegmentTrackStyle");
            foreach (var (key, label) in NetToolMenu)
            {
                // the same tabs as in Storage and Settings (SetTabButtonActive)
                var tab = new Wpf.Ui.Controls.Button
                {
                    Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 2, 0),
                    Cursor = System.Windows.Input.Cursors.Hand, Tag = key, FocusVisualStyle = null,
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                };
                string k = key;
                tab.Click += (_, _) => ShowNetTool(k, null);
                _netToolTabs[key] = tab;
                menu.Children.Add(tab);
            }
            Grid.SetRow(menuTrack, 1);
            PanelNetwork.Children.Add(menuTrack);
            TranslateLateContent(menu);   // the tab names ("Speed test", "Quick fixes"…)

            _netToolHost = new Grid { Visibility = Visibility.Collapsed };
            Grid.SetRow(_netToolHost, 2);
            PanelNetwork.Children.Add(_netToolHost);
            MarkNetToolTab();
            LinkChainNodes();
        }

        // The steps of the chain PC → router → DNS → internet: clicking a step = a ping to it (Wi-Fi opens the Wi-Fi tab)
        private void LinkChainNodes()
        {
            void Link(Border? ring, TextBlock? sub, string tip, Action<string> open, string fallback = "")
            {
                if (ring?.Parent is not StackPanel node) return;
                node.Background = System.Windows.Media.Brushes.Transparent;
                node.Cursor = System.Windows.Input.Cursors.Hand;
                node.ToolTip = tip;
                node.MouseLeftButtonUp += (_, _) =>
                {
                    string target = (sub?.Text ?? "").Trim();
                    if (!System.Net.IPAddress.TryParse(target, out _) && !target.Contains('.')) target = fallback;
                    open(target);
                };
            }
            Link(NetHop1Ring, NetHop1Sub, "Open Wi-Fi", _ => ShowNetTool("Wifi", null));
            Link(NetHop2Ring, NetHop2Sub, "Ping the router", t => ShowNetTool("Ping", t.Length > 0 ? t : null));
            Link(NetHop3Ring, NetHop3Sub, "Ping the DNS server", t => ShowNetTool("Ping", t.Length > 0 ? t : null));
            Link(NetHop4Ring, NetHop4Sub, "Ping the internet", t => ShowNetTool("Ping", t.Length > 0 ? t : "8.8.8.8"), "8.8.8.8");
        }

        private void MarkNetToolTab()
        {
            foreach (var (key, tab) in _netToolTabs)
            {
                bool on = key == _netToolKey;
                if (tab is Wpf.Ui.Controls.Button ub) SetTabButtonActive(ub, on);
                else tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private NetToolPage CreateNetToolPage(string key) => key switch
        {
            "Diagnose"   => new NetDiagnosticsPage(),
            "SpeedTest"  => new NetSpeedTestPage(),
            "Ping"       => new NetPingMonitorPage(this),
            "Traceroute" => new NetTraceroutePage(),
            "Dns"        => new NetDnsPage(),
            "Ports"      => new NetPortScanPage(),
            "Lan"        => new NetLanScanPage(),
            "LanSpeed"   => new NetLanSpeedPage(),
            "Wifi"       => new NetWifiPage(),
            "Firewall"   => new NetFirewallPage(),
            "Apps"       => new NetAppsPage(this),
            "Fixes"      => new NetQuickFixPage(
                                TxtNetUtilStatus, TxtMtuResult,
                                () => FlushDns_Click(this, new RoutedEventArgs()),
                                () => ReleaseRenewIp_Click(this, new RoutedEventArgs()),
                                () => ResetWinsock_Click(this, new RoutedEventArgs()),
                                () => TestMtu_Click(this, new RoutedEventArgs())),
            _            => throw new ArgumentException("Unknown network tool: " + key),
        };

        /// <summary>Opens a tool on the Network page (or "Overview"). <paramref name="target"/> is used for Ping.</summary>
        internal void ShowNetTool(string key, string? target)
        {
            try
            {
                if (_netToolHost == null) InitNetToolMenu();
                _netToolKey = key;
                bool overview = key == "Overview";
                NetTabOverview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
                _netToolHost!.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;

                if (!overview)
                {
                    if (!_netToolPages.TryGetValue(key, out var page))
                    {
                        page = CreateNetToolPage(key);
                        _netToolPages[key] = page;
                        _netToolHost.Children.Add(page);
                        // the tool is built after the page texts were translated: translate it once it is in the tree
                        var created = page;
                        RoutedEventHandler? once = null;
                        once = (_, _) => { created.Loaded -= once; TranslateLateContent(created); };
                        created.Loaded += once;
                    }
                    foreach (var p in _netToolPages.Values)
                        p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
                    if (key == "Ping" && !string.IsNullOrWhiteSpace(target) && page is NetPingMonitorPage ping)
                        ping.SetTargetAndStart(target);
                }
                MarkNetToolTab();
                if (_currentPanel != "Network") _ = NavigateTo("Network");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ShowNetTool " + key); }
        }

        // ── The "Active connections" card on Overview ───────────────────────────
        private DateTime _topAppsAt = DateTime.MinValue;
        private bool _topAppsBusy;

        /// <summary>Re-reads the connections (at most once every 6 s) and rebuilds the card with the most active apps.</summary>
        private async void RefreshTopApps(bool force = false)
        {
            if (_topAppsBusy || NetTopAppsCard == null) return;
            if (!force && (DateTime.Now - _topAppsAt).TotalSeconds < 6) return;
            _topAppsBusy = true; _topAppsAt = DateTime.Now;
            try
            {
                var list = await System.Threading.Tasks.Task.Run(() => GetNetConnections());
                BuildTopAppsCard(list);
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshTopApps: {ex.Message}"); }
            finally { _topAppsBusy = false; }
        }

        private void BuildTopAppsCard(IEnumerable<SMDWin.Models.NetAppEntry> entries)
        {
            var all = entries.ToList();
            var live = all.Where(e => e.State == "ESTABLISHED").ToList();
            var top = live.GroupBy(e => e.ProcessName)
                .Select(g => (Name: g.Key, Count: g.Sum(x => Math.Max(1, x.EstablishedCount)), Remote: g.First().RemoteEndpoint,
                              Risk: g.Any(x => x.HasRiskFlag), Pid: g.First().Pid))
                .OrderByDescending(x => x.Count).ToList();
            int appCount = top.Count, connCount = top.Sum(x => x.Count);
            top = top.Take(7).ToList();

            var root = new StackPanel();
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // a single-line header, like the other cards: title + summary
            var names = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var cap = new TextBlock { Text = SMDWin.Services.LiteralTexts.Translate("Active connections") };
            cap.SetResourceReference(StyleProperty, "DashRowName");
            var title = new TextBlock { Text = string.Format(LanguageService.S("Network", "AppsConnectionsFmt", "{0} apps · {1} connections"), appCount, connCount) };
            title.SetResourceReference(StyleProperty, "DashRowInlineSub");
            names.Children.Add(cap); names.Children.Add(title);
            var all_ = new TextBlock { Text = SMDWin.Services.LiteralTexts.Translate("All apps") + " →", FontSize = Ui.FontS, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            all_.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            all_.MouseLeftButtonUp += (_, _) => ShowNetTool("Apps", null);
            Grid.SetColumn(all_, 1);
            head.Children.Add(names); head.Children.Add(all_);
            root.Children.Add(head);

            if (top.Count == 0)
            {
                var none = new TextBlock { Text = SMDWin.Services.LiteralTexts.Translate("No application is connected to the network right now."), FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
                none.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                root.Children.Add(none);
            }
            if (top.Count > 0)
            {
                // column captions, aligned with the rows below (same idea as the Adapters card)
                var cols = new Grid { Margin = new Thickness(11, 0, 11, 6) };
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                var c1 = new TextBlock { Text = PT("Application · busiest destination"), FontSize = Ui.FontXS };
                var c2 = new TextBlock { Text = PT("Connections"), FontSize = Ui.FontXS, TextAlignment = TextAlignment.Right };
                c1.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                c2.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetColumn(c1, 1); Grid.SetColumn(c2, 2);
                cols.Children.Add(c1); cols.Children.Add(c2);
                root.Children.Add(cols);
            }
            foreach (var t in top)
            {
                var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
                box.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                box.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

                // letter tile standing in for the app icon
                var tile = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                tile.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                tile.Child = new TextBlock { Text = t.Name.Length > 0 ? t.Name.Substring(0, 1).ToUpperInvariant() : "?", FontSize = Ui.FontXS, FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameLine = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                var nm = new TextBlock { Text = t.Name, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Name };
                nm.SetResourceReference(TextBlock.ForegroundProperty, t.Risk ? "StatusWarnBrush" : "TextPrimaryBrush");
                nameLine.Children.Add(nm);
                if (t.Risk)
                {
                    var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = SMDWin.Services.LiteralTexts.Translate("Unsigned program or unusual location") };
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusWarnBrush");
                    nameLine.Children.Add(dot);
                }
                var rm = new TextBlock { Text = t.Remote, FontSize = Ui.FontXS, TextTrimming = TextTrimming.CharacterEllipsis };
                rm.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                texts.Children.Add(nameLine); texts.Children.Add(rm);

                var ct = new TextBlock { Text = t.Count.ToString(), FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                ct.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

                Grid.SetColumn(texts, 1); Grid.SetColumn(ct, 2);
                row.Children.Add(tile); row.Children.Add(texts); row.Children.Add(ct);
                box.Child = row;
                box.MouseLeftButtonUp += (_, _) => ShowNetTool("Apps", null);
                root.Children.Add(box);
            }
            NetTopAppsCard.Child = root;
        }

        internal void OpenLanSpeed_Click(object s, RoutedEventArgs e)       => ShowNetTool("LanSpeed", null);
        private void OpenPingMonitor_Click(object s, RoutedEventArgs e)    => ShowNetTool("Ping", null);
        private void OpenFirewall_Click(object s, RoutedEventArgs e)       => ShowNetTool("Firewall", null);
    }
}

namespace SMDWin.Views.Net
{
    /// <summary>Quick fixes: the same actions as the Overview menu, with the result shown next to them.</summary>
    public sealed class NetQuickFixPage : NetToolPage
    {
        public NetQuickFixPage(TextBlock statusSource, TextBlock mtuSource, Action flushDns, Action renewIp, Action winsock, Action mtu)
        {
            NetWindowHelper.SetupWindow(this, "Quick fixes — SMDWin", 640, 420);

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            var title = new TextBlock { Text = MainWindow.PT("Quick fixes"), FontSize = Ui.FontXL, FontWeight = FontWeights.Bold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var sub = new TextBlock { Text = MainWindow.PT("Common repairs for a connection that misbehaves"), FontSize = Ui.FontS, Margin = new Thickness(0, 2, 0, 12) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            root.Children.Add(title); root.Children.Add(sub);

            void Row(string name, string what, string button, Action act)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var n = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold };
                n.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var d = new TextBlock { Text = what, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
                d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                texts.Children.Add(n); texts.Children.Add(d);
                var b = NetWindowHelper.SecondaryButton(button);
                b.Click += (_, _) => act();
                Grid.SetColumn(b, 1);
                g.Children.Add(texts); g.Children.Add(b);
                var card = NetWindowHelper.ContentCard(g);
                card.Padding = new Thickness(14, 10, 14, 10);
                root.Children.Add(card);
            }
            Row("Flush DNS", "Clears the saved names. Helps when a site opens on one device but not on this one.", "Flush", flushDns);
            Row("Release / renew IP", "Asks the router for a new address. The connection drops for a few seconds.", "Renew", renewIp);
            Row("Winsock reset", "Repairs the network stack. Needs a restart afterwards.", "Reset", winsock);
            Row("MTU test", "Finds the largest packet size the connection carries without splitting it.", "Test", mtu);

            var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = statusSource });
            var mtuText = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            mtuText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            mtuText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = mtuSource });
            root.Children.Add(status); root.Children.Add(mtuText);

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }
    }
}
