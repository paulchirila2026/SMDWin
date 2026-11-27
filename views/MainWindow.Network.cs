using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {

        // ── Adapter card view-model ───────────────────────────────────────────
        private class AdapterCardVm : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnProp(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

            public string Name       { get; set; } = "";
            public string NicName    { get; set; } = ""; // Windows NIC name (nic.Name) for the traffic match
            public string Type       { get; set; } = "";
            public string Status     { get; set; } = "";
            public string IpAddress  { get; set; } = "—";
            public string Ipv6       { get; set; } = "";       // IPv6 address
            public string MacAddress { get; set; } = "—";
            public string Gateway    { get; set; } = "—";
            public string Dns        { get; set; } = "—";
            public string Speed      { get; set; } = "—";
            public string? SpeedCompareNote { get; set; } = null;
            public string Ssid       { get; set; } = "";       // WiFi network name
            public string Band       { get; set; } = "";       // 2.4 GHz / 5 GHz / 6 GHz
            public string BandChannel { get; set; } = "";
            public string Signal     { get; set; } = "";       // signal strength %
            public string TotalRecv  { get; set; } = "";       // total received since boot
            public string TotalSent  { get; set; } = "";       // total sent since boot

            private string _recvDisplay = "—";
            public string RecvDisplay
            {
                get => _recvDisplay;
                set { if (_recvDisplay != value) { _recvDisplay = value; OnProp(nameof(RecvDisplay)); } }
            }

            private string _sentDisplay = "—";
            public string SentDisplay
            {
                get => _sentDisplay;
                set { if (_sentDisplay != value) { _sentDisplay = value; OnProp(nameof(SentDisplay)); } }
            }
            public string ExtraLabel { get; set; } = "";
            public string ExtraValue { get; set; } = "";

            // The row in the ADAPTERS table (Network B)
            public bool   HasGateway => Status == "Up" && Gateway != "—" && !string.IsNullOrWhiteSpace(Gateway);
            public string StateLabel => SMDWin.Services.LiteralTexts.Translate(Status == "Up" ? (HasGateway ? "In use" : "Up") : Type == "Ethernet" ? "Cable unplugged" : "Disconnected");
            public System.Windows.Media.Brush? StateBrush => Application.Current?.TryFindResource(
                Status == "Up" ? "StatusOkBrush" : Type == "Ethernet" ? "StatusWarningBrush" : "TextSecondaryBrush") as System.Windows.Media.Brush;
            public string SubLine => !string.IsNullOrEmpty(Ssid) ? $"{Type} · {Ssid}{(string.IsNullOrEmpty(Signal) ? "" : " · " + Signal)}" : Type;
            public string AdapterTooltip => $"MAC {MacAddress}\nGateway {Gateway}\nDNS {Dns}"
                + (string.IsNullOrEmpty(Ipv6) ? "" : $"\nIPv6 {Ipv6}")
                + (string.IsNullOrEmpty(TotalRecv) ? "" : $"\nSince boot: ↓ {TotalRecv} · ↑ {TotalSent}");

            // Aliases for compact card template
            public string Ip4        => IpAddress;
            public string SpeedLabel => Speed;
            public string StatusLabel => Status == "Up" ? "Active" : Status == "Down" ? "Inactive" : Status;

            public System.Windows.Media.Brush StatusBg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 34, 197, 94))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 100, 116, 139));
            public System.Windows.Media.Brush StatusFg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));

            // Icon per type — SVG path data for Viewbox rendering
            // Icon per type (Fluent icon geometry, bound to Path.Data)
            public System.Windows.Media.Geometry TypeIcon => SMDWin.Services.FluentIcons.Get(Type switch
            {
                "Wi-Fi"     => "wifi_1",
                "Bluetooth" => "bluetooth",
                "VPN"       => "shield_globe",
                "Ethernet"  => "plug_connected",
                _           => "globe",
            });

            public System.Windows.Media.Brush IconBg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 34, 197, 94))   // green — active
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 148, 163, 184)); // grey — inactive

            // Status pill colours — use the static ThemeColor (StatusSuccessColor defined in Styles.xaml)
            private static readonly System.Windows.Media.Color _clrSuccess =
                (Application.Current?.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94);
            private static readonly System.Windows.Media.Color _clrError =
                (Application.Current?.TryFindResource("StatusErrorColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(239, 68, 68);
            private static readonly System.Windows.Media.Color _clrNeutral = System.Windows.Media.Color.FromRgb(100, 116, 139);

            public System.Windows.Media.Brush PillBg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, _clrSuccess.R, _clrSuccess.G, _clrSuccess.B))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, _clrNeutral.R, _clrNeutral.G, _clrNeutral.B));
            public System.Windows.Media.Brush PillFg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(_clrSuccess)
                : new System.Windows.Media.SolidColorBrush(_clrNeutral);

            // LED indicator
            public System.Windows.Media.Brush LedColor => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(_clrSuccess)
                : new System.Windows.Media.SolidColorBrush(_clrError);
            public System.Windows.Media.Color LedGlowColor => Status == "Up" ? _clrSuccess : _clrError;
            public double LedGlowOpacity => Status == "Up" ? 0.85 : 0.35;
        }

        internal void NetOverviewDownCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            if (_netOverviewDownSpark == null && NetOverviewDownCanvas != null
                && NetOverviewDownCanvas.ActualWidth >= 10)
                _netOverviewDownSpark = new PerfmonSparkline(NetOverviewDownCanvas, "", "KB/s",
                    System.Windows.Media.Color.FromRgb(34, 197, 94), showLabels: false);
        }

        internal void NetOverviewUpCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            if (_netOverviewUpSpark == null && NetOverviewUpCanvas != null
                && NetOverviewUpCanvas.ActualWidth >= 10)
                _netOverviewUpSpark = new PerfmonSparkline(NetOverviewUpCanvas, "", "KB/s",
                    System.Windows.Media.Color.FromRgb(96, 165, 250), showLabels: false);
        }

        private bool _netChartAdaptGuard = false;

        /// <summary>The traffic chart takes the space left free on the page (110–420 px): on large screens nothing stays empty
        /// under the cards, on a laptop the chart returns to its minimum height.</summary>
        internal void NetTabOverview_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            if (_netChartAdaptGuard) return;
            if (NetTabOverview == null || FindName("NetTrafficChart") is not FrameworkElement chart) return;
            try
            {
                _netChartAdaptGuard = true;
                double viewport = NetTabOverview.ViewportHeight, extent = NetTabOverview.ExtentHeight;
                if (viewport <= 0 || extent <= 0) return;
                double slack = viewport - extent;               // > 0 = free space, < 0 = the page scrolls
                if (slack > -8 && slack < 8) return;            // tolerance band, no jitter
                double current = double.IsNaN(chart.Height) ? chart.ActualHeight : chart.Height;
                double target = Math.Clamp(current + slack, 110, 420);
                if (Math.Abs(target - current) >= 4) chart.Height = target;
            }
            catch (Exception ex) { AppLogger.Debug($"NetTabOverview_ScrollChanged: {ex.Message}"); }
            finally { _netChartAdaptGuard = false; }
        }

        private bool _ramTableAdaptGuard = false;

        /// <summary>The Network page rate, from Settings → Refresh every → Network (1–10 s).</summary>
        private static int NetRefreshSeconds() => Math.Clamp(SettingsService.Current.NetworkRefreshSec, 1, 10);

        /// <summary>The traffic chart shows the chosen time window (right click on the chart);
        /// the rate in Settings only changes how often it is redrawn.</summary>
        private void ApplyNetRefreshToChart() => RenderNetTraffic();

        private void StartNetPageTimer()
        {
            if (_netPageTimer != null) return; // already started
            _netPageTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(NetRefreshSeconds())
            };
            ApplyNetRefreshToChart();
            _netPageTimer.Tick += (_, _) =>
            {
                if (_currentPanel != "Network") { StopNetPageTimer(); return; }
                if (++_netChainTick % Math.Max(1, 10 / NetRefreshSeconds()) == 0) _ = CheckNetChainAsync();   // the chain: about every 10 s
                // Refresh the traffic in the cards
                var traffic = _netTrafficSvc.GetCurrentTraffic();
                if (AdapterCardsPanel?.ItemsSource is System.Collections.Generic.IEnumerable<AdapterCardVm> cards)
                {
                    string FormatKBs(double kbs) => kbs >= 1024
                        ? $"{kbs / 1024.0:F1} MB/s" : $"{kbs:F0} KB/s";

                    foreach (var card in cards)
                    {
                        var t = traffic?.FirstOrDefault(tr =>
                            (!string.IsNullOrEmpty(card.NicName) &&
                             card.NicName.Equals(tr.Name, StringComparison.OrdinalIgnoreCase)) ||
                            card.Name.Contains(tr.Name, StringComparison.OrdinalIgnoreCase) ||
                            tr.Name.Contains(card.Name, StringComparison.OrdinalIgnoreCase));
                        if (t != null)
                        {
                            card.RecvDisplay = FormatKBs(t.RecvKBs);
                            card.SentDisplay = FormatKBs(t.SendKBs);
                        }
                    }
                    // Force a binding refresh
                    var src = AdapterCardsPanel.ItemsSource;
                    AdapterCardsPanel.ItemsSource = null;
                    AdapterCardsPanel.ItemsSource = src;
                }
                RenderNetTraffic();
                RefreshTopApps();
            };
            _netPageTimer.Start();
        }

        private void StopNetPageTimer()
        {
            _netPageTimer?.Stop();
            _netPageTimer = null;
            _netLastBucket = long.MinValue;   // on return the chart does not slide by all the missed time
        }

        private async Task LoadNetworkAsync()
        {
            ShowLoading(_LS("Network", "LoadingAdapters"));
            try
            {
                var adapters = await _netService.GetAdaptersAsync();
                // the Wi-Fi details (native WLAN calls) are read on a background thread: read here, on the UI thread, they blocked
                // the window for ~1.3 s when the page opened on a slow laptop (self-test)
                SMDWin.Services.WifiInfo? wifiNow = null;
                if (adapters.Any(a => a.Type == "Wi-Fi"))
                {
                    try { wifiNow = await Task.Run(() => SMDWin.Services.WifiReader.Read()); }
                    catch (Exception ex) { AppLogger.Debug($"LoadNetworkAsync Wi-Fi: {ex.Message}"); }
                }

                // the IPv6 address and the totals since start of every adapter are read here, once, in the background.
                // They were read inside the card loop on the UI thread — two full adapter lists per adapter (7 adapters on the
                // ThinkPad E14 → the window blocked 1.2–1.5 s when the page opened).
                var niInfo = await Task.Run(() =>
                {
                    var list = new List<(string Name, string Desc, string Ipv6, long Recv, long Sent)>();
                    try
                    {
                        foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            string v6 = ""; long rx = 0, tx = 0;
                            try
                            {
                                var u = n.GetIPProperties().UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                                                                                            && !System.Net.IPAddress.IsLoopback(x.Address) && !x.Address.IsIPv6LinkLocal);
                                if (u != null) v6 = u.Address.ToString();
                            }
                            catch (Exception ex) { AppLogger.Debug($"Network IPv6 {n.Name}: {ex.Message}"); }
                            try { var st = n.GetIPv4Statistics(); rx = st.BytesReceived; tx = st.BytesSent; }
                            catch (Exception ex) { AppLogger.Debug($"Network stats {n.Name}: {ex.Message}"); }
                            list.Add((n.Name, n.Description, v6, rx, tx));
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"Network interfaces: {ex.Message}"); }
                    return list;
                });

                // Keep legacy NetGrid working
                NetGrid.ItemsSource = adapters;

                // Get current traffic for display in cards
                var traffic = _netTrafficSvc.GetCurrentTraffic();

                // Build adapter card view-models
                if (AdapterCardsPanel != null)
                {
                    var cards = adapters.Select(a =>
                    {
                        // Match traffic by adapter name (partial)
                        var t = traffic?.FirstOrDefault(tr =>
                            a.Name.Contains(tr.Name, StringComparison.OrdinalIgnoreCase) ||
                            tr.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase));

                        string FormatKBs(double kbs) => kbs >= 1024
                            ? $"{kbs / 1024.0:F1} MB/s"
                            : $"{kbs:F0} KB/s";

                        string FormatBytes(long bytes) =>
                            bytes > 1_073_741_824L ? $"{bytes / 1_073_741_824.0:F1} GB"
                          : bytes > 1_048_576L     ? $"{bytes / 1_048_576.0:F0} MB"
                          : bytes > 1024           ? $"{bytes / 1024:F0} KB"
                          : "—";

                        string ssid = "", band = "", signal = "", ipv6 = "", bandChannel = "";
                        string? speedCompareNote = null;
                        if (a.Type == "Wi-Fi")
                        {
                            try
                            {
                                var wifi = wifiNow;
                                if (wifi != null && wifi.IsConnected)
                                {
                                    ssid   = wifi.Ssid;
                                    band   = wifi.Band;
                                    signal = wifi.SignalPct > 0 ? $"{wifi.SignalPct}%" : "";
                                    if (!string.IsNullOrEmpty(wifi.Band) && wifi.Channel > 0)
                                        bandChannel = $"{wifi.Band} ({wifi.Channel})";

                                    if (wifi.LinkSpeedMbps > 0)
                                        speedCompareNote = $"WifiReader (wlanapi): {wifi.LinkSpeedMbps} Mbps — verify in Windows Settings > Network > Wi-Fi > Hardware properties which value is correct";
                                }
                            }
                            catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        }

                        // IPv6 address and the totals since boot (read above, in the background)
                        long totalRecvBytes = 0, totalSentBytes = 0;
                        var niHit = niInfo.FirstOrDefault(n => n.Desc.Contains(a.Name, StringComparison.OrdinalIgnoreCase)
                                                             || n.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
                        if (niHit.Name != null)
                        {
                            if (niHit.Ipv6.Length > 0) ipv6 = niHit.Ipv6;
                            totalRecvBytes = niHit.Recv;
                            totalSentBytes = niHit.Sent;
                        }

                        string nicWindowsName = "";
                        try
                        {
                            var matchNic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                                .FirstOrDefault(n => n.Description.Contains(a.Name, StringComparison.OrdinalIgnoreCase)
                                                  || a.Name.Contains(n.Description, StringComparison.OrdinalIgnoreCase)
                                                  || n.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
                            nicWindowsName = matchNic?.Name ?? "";
                        }
                        catch (Exception ex) { AppLogger.Debug(ex.Message); }

                        return new AdapterCardVm
                        {
                            Name        = a.Name,
                            NicName     = nicWindowsName,
                            Type        = a.Type,
                            Status      = a.Status,
                            IpAddress   = string.IsNullOrEmpty(a.IpAddress)  ? "—" : a.IpAddress,
                            Ipv6        = ipv6,
                            MacAddress  = string.IsNullOrEmpty(a.MacAddress) ? "—" : a.MacAddress,
                            Gateway     = string.IsNullOrEmpty(a.Gateway)    ? "—" : a.Gateway,
                            Dns         = string.IsNullOrEmpty(a.Dns)        ? "—" : a.Dns,
                            Speed       = string.IsNullOrEmpty(a.Speed)      ? "—" : a.Speed,
                            SpeedCompareNote = speedCompareNote,
                            Ssid        = ssid,
                            Band        = band,
                            BandChannel = bandChannel,
                            Signal      = signal,
                            TotalRecv   = totalRecvBytes > 0 ? FormatBytes(totalRecvBytes) : "",
                            TotalSent   = totalSentBytes > 0 ? FormatBytes(totalSentBytes) : "",
                            RecvDisplay = t != null ? FormatKBs(t.RecvKBs) : "0 KB/s",
                            SentDisplay = t != null ? FormatKBs(t.SendKBs) : "0 KB/s",
                        };
                    }).ToList();
                    cards = cards.OrderByDescending(c => c.HasGateway).ThenByDescending(c => c.Status == "Up").ToList();
                    AdapterCardsPanel.ItemsSource = cards;
                    if (TxtNetAdaptersSub != null)
                        TxtNetAdaptersSub.Text = string.Format(LanguageService.S("Network", "AdaptersFoundInUseFmt", "{0} found · {1} in use"), cards.Count, cards.Count(c => c.HasGateway));
                    _ = CheckNetChainAsync();
                }

                // Init traffic sparklines — try immediately, and also on SizeChanged as fallback
                void TryInitDownSpark()
                {
                    if (_netOverviewDownSpark == null && NetOverviewDownCanvas != null
                        && NetOverviewDownCanvas.ActualWidth >= 10)
                        _netOverviewDownSpark = new PerfmonSparkline(NetOverviewDownCanvas, "", "KB/s",
                            WpfColor.FromRgb(34, 197, 94), showLabels: false);
                }
                void TryInitUpSpark()
                {
                    if (_netOverviewUpSpark == null && NetOverviewUpCanvas != null
                        && NetOverviewUpCanvas.ActualWidth >= 10)
                        _netOverviewUpSpark = new PerfmonSparkline(NetOverviewUpCanvas, "", "KB/s",
                            WpfColor.FromRgb(96, 165, 250), showLabels: false);
                }

                // Immediate attempt (works if canvas already rendered)
                TryInitDownSpark();
                TryInitUpSpark();

                // Delayed fallback — canvas may not have size yet on first navigation.
                // Use DispatcherTimer: fires once on the UI thread after layout completes.
                if (_netOverviewDownSpark == null || _netOverviewUpSpark == null)
                {
                    var retryTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(350)
                    };
                    retryTimer.Tick += (_, _) =>
                    {
                        retryTimer.Stop();
                        TryInitDownSpark();
                        TryInitUpSpark();
                    };
                    retryTimer.Start();
                }
            }
            finally { HideLoading(); }
        }


        // ── Open router admin page in default browser ─────────────────────────
        internal void OpenRouterPage_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // Find first active adapter with a gateway
                string? gw = null;
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var gwa in ni.GetIPProperties().GatewayAddresses)
                    {
                        string g = gwa.Address.ToString();
                        if (!g.StartsWith("169.254") && g.Contains('.'))
                        { gw = g; break; }
                    }
                    if (gw != null) break;
                }
                if (string.IsNullOrEmpty(gw))
                { AppDialog.Show(_LS("Network", "NoGateway")); return; }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"http://{gw}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.Show(string.Format(LanguageService.S("Msg", "RouterPageFailFmt"), ex.Message));
            }
        }

        private readonly Dictionary<string, bool> _unsignedCache = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<int, (string name, string path)> _procInfoCache = new();
        private DateTime _procInfoCacheBuilt = DateTime.MinValue;
        private static readonly TimeSpan ProcInfoCacheTtl = TimeSpan.FromSeconds(15);

        // the caches are touched from several threads (background tick + UI) — protected with a lock
        private readonly object _procCacheLock = new();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(System.IntPtr h, uint flags, System.Text.StringBuilder name, ref int size);

        // QueryFullProcessImageName with limited rights — works on protected processes too, without exceptions/warnings
        private static string TryGetProcessPath(int pid)
        {
            var h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == System.IntPtr.Zero) return "";
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
            }
            finally { CloseHandle(h); }
        }

        private Dictionary<int, (string name, string path)> GetCachedProcInfo()
        {
            lock (_procCacheLock)
            {
                if ((DateTime.Now - _procInfoCacheBuilt) < ProcInfoCacheTtl && _procInfoCache.Count > 0)
                    return _procInfoCache;
            }

            var fresh = new Dictionary<int, (string name, string path)>();
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    fresh[p.Id] = (p.ProcessName, TryGetProcessPath(p.Id));
                }
                catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.GetCachedProcInfo"); }
                finally { p.Dispose(); }
            }
            lock (_procCacheLock)
            {
                _procInfoCache = fresh;
                _procInfoCacheBuilt = DateTime.Now;
            }
            return fresh;
        }
        /// <summary>A process seen by the traffic monitor that has no path: its name if it still runs, otherwise "ended" (short-lived
        /// programs — an updater, a helper — appear in the traffic but are gone by the time the list is built).</summary>
        private static string NameOrEnded(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
            catch (ArgumentException) { return $"(ended) PID {pid}"; }
            catch (Exception ex) { AppLogger.Debug($"NameOrEnded {pid}: {ex.Message}"); return $"PID {pid}"; }
        }


        internal List<SMDWin.Models.NetAppEntry> GetNetConnections()
        {
            var result = new List<SMDWin.Models.NetAppEntry>();
            try
            {
                // the per-app rates come from the difference between two refreshes (nothing else refreshes them on this page)
                SMDWin.Services.TcpEStatsService.Instance.Refresh();
                var tcpRows = SMDWin.Services.TcpEStatsService.GetTcpConnectionsWithPid();
                var procInfo = GetCachedProcInfo();

                // Group connections per PID
                var connsByPid = new Dictionary<int, List<SMDWin.Services.TcpConnectionInfo>>();
                foreach (var row in tcpRows)
                {
                    if (row.Pid == 0) continue;
                    // Skip pure loopback
                    if (row.Local.StartsWith("127.0.0.1") &&
                        (row.Remote == "*:*" || row.Remote.StartsWith("127.0.0.1"))) continue;

                    if (!connsByPid.TryGetValue(row.Pid, out var list))
                        connsByPid[row.Pid] = list = new();
                    list.Add(row);
                }

                // the ETW monitor (TCP + UDP, IPv4 + IPv6): apps with traffic but no IPv4 TCP connection (QUIC, calls, games,
                // IPv6-only) get a row too, and every row gets its busiest remote with the site name and the service
                var etw = SMDWin.Services.EtwNetMonitor.Instance.Running
                    ? SMDWin.Services.EtwNetMonitor.Instance.Snapshot(withRemotes: true).ToDictionary(a => a.Pid)
                    : new Dictionary<int, SMDWin.Services.NetAppTraffic>();
                foreach (var pid in etw.Keys)
                    if (pid != 0 && !connsByPid.ContainsKey(pid)) connsByPid[pid] = new List<SMDWin.Services.TcpConnectionInfo>();

                foreach (var kv in connsByPid)
                {
                    int pid   = kv.Key;
                    var conns = kv.Value;
                    etw.TryGetValue(pid, out var traffic);
                    var top = traffic?.Remotes.FirstOrDefault();
                    if (conns.Count == 0)
                    {
                        if (top == null) continue;
                        conns.Add(new SMDWin.Services.TcpConnectionInfo(pid, top.LocalPort > 0 ? $"*:{top.LocalPort}" : "*",
                            top.Endpoint, top.Udp ? "UDP" : "ACTIVE"));
                    }

                    // Best connection: ESTABLISHED > LISTEN > first
                    var best = conns.Find(c => c.State == "ESTABLISHED")
                            ?? conns.Find(c => c.State == "LISTEN")
                            ?? conns[0];

                    procInfo.TryGetValue(pid, out var info);
                    string procPath = info.path ?? SMDWin.Services.ProcessMemoryInfo.GetImagePath(pid) ?? "";
                    string procName = info.name ?? (pid == 4 ? "System" : procPath.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(procPath) : NameOrEnded(pid));

                    bool isBlocked = !string.IsNullOrEmpty(procPath) && _firewallSvc.IsAppBlocked(procPath);

                    bool isUnsigned = false;
                    bool isSuspiciousLocation = false;
                    if (!string.IsNullOrEmpty(procPath) && System.IO.File.Exists(procPath))
                    {
                        bool haveSig;
                        lock (_procCacheLock) haveSig = _unsignedCache.TryGetValue(procPath, out isUnsigned);
                        if (!haveSig)
                        {
                            try { System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(procPath); isUnsigned = false; }
                            catch { isUnsigned = true; }
                            lock (_procCacheLock) _unsignedCache[procPath] = isUnsigned;
                        }

                        string pathLower = procPath.ToLowerInvariant();
                        isSuspiciousLocation = pathLower.Contains(@"\appdata\local\temp\")
                            || pathLower.Contains(@"\windows\temp\")
                            || pathLower.Contains(@"\downloads\")
                            || (pathLower.Contains(@"\temp\") && !pathLower.Contains(@"\program files"));
                    }

                    // Traffic via EStats (already collected by background Refresh tick)
                    SMDWin.Services.TcpEStatsService.Instance.TryGetPidTraffic(
                        pid, out double sendKBs, out double recvKBs,
                        out ulong totalSent, out ulong totalRecv);

                    bool hasEstablished = conns.Any(c => c.State == "ESTABLISHED");
                    string displayState = hasEstablished ? "ESTABLISHED"
                        : conns.Any(c => c.State == "LISTEN") ? "LISTEN"
                        : best.State;
                    // the remote shown: the one with the most traffic (ETW), else the TCP table's best connection
                    string remote = top != null ? top.Endpoint : best.Remote;
                    string proto = traffic == null ? "TCP" : traffic.HasTcp && traffic.HasUdp ? "TCP+UDP" : traffic.HasUdp ? "UDP" : "TCP";

                    result.Add(new SMDWin.Models.NetAppEntry
                    {
                        ProcessName     = procName,
                        ProcessPath     = procPath,
                        Pid             = pid,
                        Protocol        = proto,
                        LocalEndpoint   = best.Local,
                        RemoteEndpoint  = remote,
                        Site            = top?.Host ?? "",
                        Service         = top?.Service ?? "",
                        State           = displayState,
                        SendKBs         = sendKBs,
                        RecvKBs         = recvKBs,
                        TotalSentMB     = totalSent / 1_048_576.0,
                        TotalRecvMB     = totalRecv / 1_048_576.0,
                        IsBlocked       = isBlocked,
                        GeoCountry      = SMDWin.Services.GeoIpService.CountryDisplay(remote),   // offline, public IPv4 only
                        ConnectionCount = conns.Count,
                        EstablishedCount = conns.Count(c => c.State == "ESTABLISHED"),
                        IsUnsigned      = isUnsigned,
                        IsSuspiciousLocation = isSuspiciousLocation,
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.unknown"); }

            return result
                .OrderByDescending(x => x.TotalTrafficKBs)
                .ThenByDescending(x => x.State == "ESTABLISHED")
                .ThenBy(x => x.AppCategory)
                .ThenBy(x => x.ProcessName)
                .ToList();
        }

        internal void OpenNetworkSettings_Click(object s, RoutedEventArgs e) => _netService.OpenNetworkSettings();
        internal void OpenNetAdapters_Click(object s, RoutedEventArgs e) => _netService.OpenNetworkAdapters();

        // ── Network sub-tab pill navigation ──────────────────────────────────


        internal void TogglePingSection_Click(object s, RoutedEventArgs e)
        {
            bool show = PingSectionContent.Visibility != Visibility.Visible;
            PingSectionContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtPingSectionArrow.Text = show ? "▼ Hide" : "Show";
        }

        internal void TogglePortScanSection_Click(object s, RoutedEventArgs e)
        {
            bool show = PortScanSectionContent.Visibility != Visibility.Visible;
            PortScanSectionContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtPortScanSectionArrow.Text = show ? "▼ Hide" : "Show";
        }
        // ── Quick Network Utilities ──────────────────────────────────────────
        private async void FlushDns_Click(object s, RoutedEventArgs e)
        {
            if (!AppDialog.Confirm(LanguageService.S("Msg", "FlushDnsConfirm"), LanguageService.S("Msg", "FlushDnsTitle"))) return;
            ToolActivityLog.Add("Network", "Flush DNS", "info");
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Network", "FlushingDns");
            try
            {
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true
                    };
                    var p = System.Diagnostics.Process.Start(psi)!;
                    p.WaitForExit(5000);
                });
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Network", "DnsFlushed");
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message; }
        }

        private async void ReleaseRenewIp_Click(object s, RoutedEventArgs e)
        {
            if (!AppDialog.Confirm(LanguageService.S("Msg", "RenewIpConfirm"), LanguageService.S("Msg", "RenewIpTitle"))) return;
            ToolActivityLog.Add("Network", "Release / renew IP", "info");
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Network", "ReleasingIp");
            try
            {
                await Task.Run(() =>
                {
                    void Run(string args)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", args)
                        { UseShellExecute = false, CreateNoWindow = true };
                        var p = System.Diagnostics.Process.Start(psi)!;
                        p.WaitForExit(15000);
                    }
                    Run("/release");
                    Run("/renew");
                });
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "IP released and renewed successfully.";
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message; }
        }

        private void ResetWinsock_Click(object s, RoutedEventArgs e)
        {
            if (!AppDialog.Confirm(
PT("This will reset the Winsock catalog.\nA system restart is required afterward.\n\nContinue?"), PT("Reset Winsock"))) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "winsock reset")
                { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi);
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Network", "WinsockReset");
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message; }
        }

        private async void TestMtu_Click(object s, RoutedEventArgs e)
        {
            if (TxtMtuResult != null) TxtMtuResult.Text = LanguageService.S("Network", "TestingMtu");
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "";
            try
            {
                int optimalMtu = await Task.Run(() =>
                {
                    using var pinger = new System.Net.NetworkInformation.Ping();
                    for (int size = 1472; size >= 576; size -= 8)
                    {
                        var opts = new System.Net.NetworkInformation.PingOptions { DontFragment = true, Ttl = 128 };
                        try
                        {
                            var reply = pinger.Send("8.8.8.8", 2000, new byte[size], opts);
                            if (reply?.Status == System.Net.NetworkInformation.IPStatus.Success)
                                return size + 28; // add IP+ICMP headers
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.TestMtu_Click"); }
                    }
                    return 576;
                });
                string quality = optimalMtu >= 1500 ? "Excellent (standard)" :
                                 optimalMtu >= 1400 ? "Slightly reduced (VPN/tunnel?)" :
"Low — may affect performance";
                if (TxtMtuResult  != null) TxtMtuResult.Text  = PT("Optimal MTU: {0} bytes — {1}", optimalMtu, quality);
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "";
            }
            catch (Exception ex)
            {
                if (TxtMtuResult != null) TxtMtuResult.Text = LanguageService.S("Common", "TestFailed") + ".";
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message;
            }
        }

        private void OpenNetWin<T>(Func<T> factory) where T : Window
        {
            var win = factory();
            win.Owner = this;
            win.Show();
            win.Activate();
        }

        internal void OpenPowerHistory_Click(object s, RoutedEventArgs e)
            => OpenNetWin(() => new SMDWin.Views.PowerHistoryWindow());

        private void OpenNativeEventViewer_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("eventvwr.msc")
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "OpenNativeEventViewer_Click");
                AppDialog.Show(LanguageService.S("Msg", "EventViewerOpenFail"));
            }
        }
    }
}
