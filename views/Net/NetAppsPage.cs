using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using SMDWin.Views;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button               = System.Windows.Controls.Button;
using Control             = System.Windows.Controls.Control;
using MenuItem            = System.Windows.Controls.MenuItem;
using ContextMenu         = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using TextBox             = System.Windows.Controls.TextBox;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;

namespace SMDWin.Views.Net
{
    public class NetAppsPage : NetToolPage
    {
        private readonly MainWindow _owner;
        private readonly FirewallService _fw = new();

        private DataGrid  _grid          = null!;
        private TextBlock _txtStatus     = null!;
        private Button    _btnRefresh    = null!;
        private TextBox   _txtSearch     = null!;
        private Button    _btnListenOnly = null!;

        private List<SMDWin.Models.NetAppEntry> _allEntries = new();
        private bool _listenOnly = false;
        private System.Windows.Threading.DispatcherTimer? _liveTimer;

        public NetAppsPage(MainWindow owner)
        {
            _owner = owner;
            NetWindowHelper.SetupWindow(this, "Network Apps — SMDWin", 980, 600);
            Content = BuildUI();
            // One timer, running only while the tab is visible. Before, every Loaded (each time the tab was shown again) created
            // a new 2-second timer and none was ever stopped (Closed does not fire for a page) — after a few visits several
            // refreshes ran in the background all the time, with signature checks and firewall reads.
            _liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _liveTimer.Tick += async (_, _) => await RefreshAsync(silent: true);
            bool firstLoad = true;
            Loaded += async (_, _) =>
            {
                if (firstLoad) { firstLoad = false; await RefreshAsync(); }
                if (IsVisible) _liveTimer.Start();
            };
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) _liveTimer.Start(); else _liveTimer.Stop();
            };
            Unloaded += (_, _) => _liveTimer.Stop();
        }

        private UIElement BuildUI()
        {
            var root = new Grid();
            var header = NetWindowHelper.ToolHeader(
                "Network Apps",
                "TCP and UDP traffic per program · double-click for the addresses and a chart",
                out var actions);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            actions.Children.Add(NetWindowHelper.HeaderLabel("Filter"));
            _txtSearch = NetWindowHelper.HeaderBox("", 150, SMDWin.Services.LanguageService.S("NetApps", "SearchTooltip"));
            _txtSearch.TextChanged += (_, _) => ApplyFilter();
            actions.Children.Add(_txtSearch);

            _btnListenOnly = NetWindowHelper.SecondaryButton("Listening Only",
                "Show only processes listening for incoming connections (LISTEN)");
            _btnListenOnly.Click += (_, _) =>
            {
                _listenOnly = !_listenOnly;
                _btnListenOnly.Content = _listenOnly ? "Listening only · on" : "Listening only";
                _btnListenOnly.Foreground = _listenOnly
                    ? NetWindowHelper.Res("AccentBrush") : NetWindowHelper.Res("TextPrimaryBrush");
                ApplyFilter();
            };
            actions.Children.Add(_btnListenOnly);

            _txtStatus = new TextBlock { FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,12,0) };
            actions.Children.Add(_txtStatus);
            _btnRefresh = NetWindowHelper.SecondaryButton("↻ Refresh", "Reload network connections per application");
            _btnRefresh.Click += async (_, _) => await RefreshAsync();
            actions.Children.Add(_btnRefresh);

            var btnExport = NetWindowHelper.SecondaryButton("Export", "Saves the list of programs and their traffic as a CSV file");
            btnExport.Margin = new Thickness(8, 0, 0, 0);
            btnExport.Click += (_, _) => ExportCsv();
            actions.Children.Add(btnExport);

            _btnAlerts = NetWindowHelper.SecondaryButton("Alerts", "Network alerts in the notification centre (all off by default)");
            _btnAlerts.Margin = new Thickness(8, 0, 0, 0);
            _btnAlerts.Click += (_, _) => ShowAlertsMenu();
            actions.Children.Add(_btnAlerts);
            UpdateAlertsButton();

            Grid.SetRow(header, 0); root.Children.Add(header);
            NetWindowHelper.MakeDraggable(header, this);

            var gridBorder = new Border
            {
                Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(0)
            };
            _grid = NetWindowHelper.StandardDataGrid();
            _grid.RowHeight = 40;
            _grid.MouseDoubleClick += Grid_DoubleClick;

            var kindCol = new DataGridTemplateColumn { Header = "", Width = new DataGridLength(40) };
            var kindTpl = new DataTemplate();
            var kindFef = new FrameworkElementFactory(typeof(ArtPicture));
            kindFef.SetBinding(ArtPicture.DrawingProperty, new System.Windows.Data.Binding("CategoryArt"));
            kindFef.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("AppCategory"));
            kindFef.SetValue(FrameworkElement.WidthProperty, 22.0);
            kindFef.SetValue(FrameworkElement.HeightProperty, 22.0);
            kindFef.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            kindTpl.VisualTree = kindFef;
            kindCol.CellTemplate = kindTpl;
            _grid.Columns.Add(kindCol);

            // Risk flag — not digitally signed and/or an unusual location (Temp/Downloads). Not malware detection, only a visual hint.
            var riskCol = new DataGridTemplateColumn { Header = "Risk", Width = new DataGridLength(52) };
            var riskTpl = new DataTemplate();
            var riskFef = new FrameworkElementFactory(typeof(TextBlock));
            riskFef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RiskIcon"));
            riskFef.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("RiskColor") { Converter = new NetStringToBrushConverter() });
            riskFef.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            riskFef.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("RiskLabel"));
            riskTpl.VisualTree = riskFef;
            riskCol.CellTemplate = riskTpl;
            _grid.Columns.Add(riskCol);

            _grid.Columns.Add(new DataGridTextColumn { Header = "Process", Binding = new System.Windows.Data.Binding("ProcessName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150, FontWeight = FontWeights.SemiBold });
            // the fixed columns added for ETW left no room for the two star columns: "Process" shrank to nothing at
            // 1680 px. Process keeps 150 px at least; the "Blocked" column went (the Firewall button shows it); the fixed ones are narrower; if the window is still too narrow, the grid scrolls.
            _grid.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new System.Windows.Data.Binding("Pid"), Width = new DataGridLength(60) });

            // Blocked icon
            var blockedCol = new DataGridTemplateColumn { Header = "Blocked", Width = new DataGridLength(76) };
            var blockedTpl = new DataTemplate();
            var blockedFef = new FrameworkElementFactory(typeof(TextBlock));
            blockedFef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("BlockedIcon"));
            blockedFef.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            blockedFef.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("BlockedDisplay"));
            blockedTpl.VisualTree = blockedFef;
            blockedCol.CellTemplate = blockedTpl;
            // not added any more: the Firewall column's button already says Block / Unblock, and the room is needed
            // for the process name at 1680 px

            // 13 columns did not fit: "Now" holds both directions, "Local" is in the Remote tooltip and the detail window,
            // State is a short word, the country has no code; Site / service takes the free room; every text column shows its
            // whole value as a tooltip when it is cut
            DataGridTextColumn TextCol(string header, string path, DataGridLength width, double min = 0, string? tipPath = null)
            {
                var c = new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width };
                if (min > 0) c.MinWidth = min;
                var st = new Style(typeof(TextBlock));
                st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                st.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(tipPath ?? path)));
                st.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                c.ElementStyle = st;
                return c;
            }
            _grid.Columns.Add(TextCol("Now ↓ ↑", "NowDisplay", new DataGridLength(134)));   // "↓184 KB/s ↑0.0 KB/s" was cut
            _grid.Columns.Add(TextCol("Total", "TotalDisplay", new DataGridLength(124)));
            _grid.Columns.Add(TextCol("Proto", "Protocol", new DataGridLength(72)));
            _grid.Columns.Add(TextCol("Conns", "ConnectionCount", new DataGridLength(56)));
            _grid.Columns.Add(TextCol("Remote", "RemoteEndpoint", new DataGridLength(150), tipPath: "EndpointsTip"));
            _grid.Columns.Add(TextCol("Site / service", "SiteDisplay", new DataGridLength(1, DataGridLengthUnitType.Star), 170));
            _grid.Columns.Add(TextCol("State", "StateDisplay", new DataGridLength(96), tipPath: "State"));
            _grid.Columns.Add(TextCol("Location", "GeoShort", new DataGridLength(130), tipPath: "GeoDisplay"));

            var actionCol = new DataGridTemplateColumn { Header = "Firewall", Width = new DataGridLength(90) };
            var actionTpl = new DataTemplate();
            var btnFef = new FrameworkElementFactory(typeof(Button));
            if (Application.Current.Resources["OutlineButtonStyle"] is Style outlineStyle)
                btnFef.SetValue(Control.StyleProperty, outlineStyle);
            btnFef.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");   // follows the theme
            btnFef.SetValue(Control.PaddingProperty, new Thickness(10, 3, 10, 3));
            btnFef.SetValue(Control.FontSizeProperty, 12.0);
            btnFef.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            btnFef.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnFef.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);
            btnFef.AddHandler(Button.ClickEvent, new RoutedEventHandler(QuickBlock_Click));
            btnFef.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("IsBlocked") { Converter = new BlockLabelConverter() });
            actionTpl.VisualTree = btnFef;
            actionCol.CellTemplate = actionTpl;
            _grid.Columns.Add(actionCol);

            var _placeholderApps = NetWindowHelper.MakePlaceholder("No active network connections found", "⊘");
            var gridStack = new Grid();
            gridStack.Children.Add(_grid);
            gridStack.Children.Add(_placeholderApps);
            gridBorder.Child = gridStack;
            Grid.SetRow(gridBorder, 1); root.Children.Add(gridBorder);

            return root;
        }

        private class BlockLabelConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => (value is bool b && b) ? "Unblock" : "Block";
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => throw new NotSupportedException();
        }

        private async void QuickBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not SMDWin.Models.NetAppEntry entry) return;
            if (string.IsNullOrEmpty(entry.ProcessPath)) return;
            btn.IsEnabled = false;
            try
            {
                bool ok = entry.IsBlocked
                    ? await _fw.UnblockAppAsync(entry.ProcessPath)
                    : await _fw.BlockAppAsync(entry.ProcessPath);
                if (ok) await RefreshAsync(silent: true);
            }
            finally { btn.IsEnabled = true; }
        }

        private async Task RefreshAsync(bool silent = false)
        {
            if (!silent) { _btnRefresh.IsEnabled = false; _txtStatus.Text = LanguageService.S("Common", "Loading"); }
            try
            {
                var entries = await Task.Run(() => _owner.GetNetConnections());
                _allEntries = entries;
                ApplyFilter();
            }
            catch (Exception ex)
            {
                if (!silent) _txtStatus.Text = $"Error: {ex.Message}";
            }
            finally { if (!silent) _btnRefresh.IsEnabled = true; }
        }

        private void ApplyFilter()
        {
            IEnumerable<SMDWin.Models.NetAppEntry> filtered = _allEntries;
            string search = _txtSearch?.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(en => en.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (_listenOnly)
                filtered = filtered.Where(en => en.State == "LISTEN");

            var list = filtered.ToList();
            int? selPid = (_grid.SelectedItem as SMDWin.Models.NetAppEntry)?.Pid;
            _grid.ItemsSource = list;
            if (selPid != null) _grid.SelectedItem = list.FirstOrDefault(x => x.Pid == selPid);
            var mon = EtwNetMonitor.Instance;
            _txtStatus.Text = $"{list.Count} / {_allEntries.Count} programs";
            _txtStatus.ToolTip = mon.Running
                ? $"Traffic counted from Windows' network events (TCP + UDP, IPv4 + IPv6) since {mon.StartedUtc.ToLocalTime():HH:mm:ss}"
                : "Traffic from the TCP connection counters only (UDP and IPv6 not counted)" +
                  (mon.LastError.Length > 0 ? $" — the network event session did not start: {mon.LastError}" : "");
        }

        private void Grid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_grid.SelectedItem is not SMDWin.Models.NetAppEntry entry) return;
            if (EtwNetMonitor.Instance.Running)
            {
                // the per-address view with the chart (one window per program)
                var w = new NetAppDetailWindow(entry.Pid, entry.ProcessName, entry.ProcessPath);
                if (Window.GetWindow(this) is Window owner && owner.IsVisible) w.Owner = owner;
                w.Show();
                return;
            }
            // only what can be measured: the traffic comes from the connection's TCP counters and is missing when they were not read
            bool hasTraffic = entry.TotalSentMB + entry.TotalRecvMB > 0 || entry.SendKBs + entry.RecvKBs > 0;
            string more = entry.ConnectionCount > 1 ? $" (+{entry.ConnectionCount - 1} more)" : "";
            string msg = $"Process: {entry.ProcessName}\nPID: {entry.Pid}\n" +
                         $"Path: {(string.IsNullOrEmpty(entry.ProcessPath) ? "not readable (protected process)" : entry.ProcessPath)}\n" +
                         $"Connections: {entry.ConnectionCount}\nState: {entry.State}\n" +
                         $"Local: {entry.LocalEndpoint}\nRemote: {entry.RemoteEndpoint}{more}\n" +
                         $"Location: {(entry.GeoDisplay != "—" ? entry.GeoDisplay : IsPrivateEndpoint(entry.RemoteEndpoint) ? "local network or this computer" : "unknown")}\n" +
                         $"Traffic: {(hasTraffic ? $"↑{entry.TotalSentMB:F1} MB / ↓{entry.TotalRecvMB:F1} MB" : "not measured (no data counted for this connection yet)")}" +
                         (entry.HasRiskFlag ? $"\n\nRisk: {entry.RiskLabel}" : "") +
                         (entry.GeoDisplay != "—" ? $"\n\n{SMDWin.Services.GeoIpService.Attribution}" : "");
            SMDWin.Views.AppDialog.Show(msg, "Connection details", SMDWin.Views.AppDialog.Kind.Info, Window.GetWindow(this));
        }

        private Button _btnAlerts = null!;

        private void UpdateAlertsButton()
        {
            int on = (SettingsService.Current.NetAlertNewApp ? 1 : 0) + (SettingsService.Current.NetAlertMbPerMin > 0 ? 1 : 0)
                   + (SettingsService.Current.NetAlertBlocklist ? 1 : 0);
            _btnAlerts.Content = on > 0 ? $"Alerts · {on} on" : "Alerts";
            _btnAlerts.Foreground = on > 0 ? NetWindowHelper.Res("AccentBrush") : NetWindowHelper.Res("TextPrimaryBrush");
        }

        /// <summary>The three alerts as a menu under the button; each change is saved and applied at once.</summary>
        private void ShowAlertsMenu()
        {
            var s = SettingsService.Current;
            var menu = new ContextMenu { PlacementTarget = _btnAlerts, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Changed() { SettingsService.Save(); NetAlerts.Apply(); UpdateAlertsButton(); }

            var newApp = new MenuItem { Header = "A program uses the internet for the first time", IsCheckable = true, IsChecked = s.NetAlertNewApp,
                ToolTip = "Compared with the programs already seen on this PC. The first 2 minutes after switching it on only learn what already runs." };
            newApp.Click += (_, _) => { s.NetAlertNewApp = newApp.IsChecked; Changed(); };
            menu.Items.Add(newApp);

            var traffic = new MenuItem { Header = s.NetAlertMbPerMin > 0 ? $"A program moves more than {s.NetAlertMbPerMin} MB in a minute" : "A program moves a lot of data in a minute" };
            foreach (int mb in new[] { 0, 50, 100, 250, 500, 1000 })
            {
                var opt = new MenuItem { Header = mb == 0 ? "Off" : $"More than {mb} MB / minute", IsCheckable = true, IsChecked = s.NetAlertMbPerMin == mb };
                opt.Click += (_, _) => { s.NetAlertMbPerMin = mb; Changed(); };
                traffic.Items.Add(opt);
            }
            menu.Items.Add(traffic);

            var bl = new MenuItem { Header = "Contact with known malware control servers", IsCheckable = true, IsChecked = s.NetAlertBlocklist,
                ToolTip = "Uses the abuse.ch Feodo Tracker list (CC0), downloaded when switched on and then once a day — the only thing sent for " +
                          "this alert is that download; the traffic itself never leaves the PC." };
            bl.Click += (_, _) => { s.NetAlertBlocklist = bl.IsChecked; Changed(); };
            menu.Items.Add(bl);

            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "Forget the known programs", ToolTip = "The next 2 minutes learn again, without alerts" };
            reset.Click += (_, _) => NetAlerts.ResetKnown();
            menu.Items.Add(reset);
            menu.Items.Add(new MenuItem
            {
                Header = NetAlerts.AnyOn ? "While an alert is on, the traffic monitor keeps running in the background"
                                         : "The alerts appear in the notification centre (the bell)",
                IsEnabled = false,
            });
            menu.IsOpen = true;
        }

        private void ExportCsv()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Program,PID,Path,Protocol,Upload KB/s,Download KB/s,Sent MB,Received MB,Connections,State,Remote,Site / service,Country");
                foreach (var e in _allEntries)
                    sb.AppendLine(string.Join(",", new[]
                    {
                        e.ProcessName, e.Pid.ToString(), e.ProcessPath, e.Protocol,
                        e.SendKBs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                        e.RecvKBs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                        e.TotalSentMB.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        e.TotalRecvMB.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        e.ConnectionCount.ToString(), e.State, e.RemoteEndpoint, e.SiteDisplay, e.GeoDisplay,
                    }.Select(NetAppDetailWindow.Csv)));
                string dir = SettingsService.GetEffectiveReportFolder();
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"Network-Apps_{DateTime.Now:yyyy-MM-dd_HHmm}.csv");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch (Exception ex) { AppLogger.Debug($"Open export folder: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                SMDWin.Views.AppDialog.Show($"Could not save the file: {ex.Message}", "Export", SMDWin.Views.AppDialog.Kind.Error, Window.GetWindow(this));
            }
        }

        /// <summary>True for "*:*", loopback, private (10/8, 172.16/12, 192.168/16), link-local and unparseable endpoints.</summary>
        private static bool IsPrivateEndpoint(string endpoint)
        {
            string host = endpoint;
            int colon = endpoint.LastIndexOf(':');
            if (colon > 0) host = endpoint[..colon];
            if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return true;
            if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any)) return true;
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
    }
}
