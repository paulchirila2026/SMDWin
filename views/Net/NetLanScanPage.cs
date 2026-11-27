using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
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

namespace SMDWin.Views.Net
{
    public class NetLanScanPage : NetToolPage
    {
        private readonly NetworkService _netSvc = new();
        private CancellationTokenSource? _cts;
        // each scan has a generation number. A completion (or an error) of an older scan, arriving after a new scan has already
        // started, is ignored — it does not overwrite the grid or the button state of the current scan.
        private int _scanGeneration;
        private TextBlock  _txtStatus  = null!;
        private DataGrid   _grid       = null!;
        private Button     _btnScan    = null!;
        private Button     _btnStop    = null!;

        // Detail panel elements
        private Border     _detailPanel = null!;
        private TextBlock  _detailTitle = null!;
        private ItemsControl _detailItems = null!;
        private SMDWin.Models.LocalNetworkDevice? _selectedDevice;

        public NetLanScanPage()
        {
            NetWindowHelper.SetupWindow(this, "LAN Scanner — SMDWin", 780, 560);
            Content = BuildUI();
            Closed += (_, _) => _cts?.Cancel();
            // the first scan starts when the page is first shown; later Loaded events (theme change, layout) do not scan again
            bool scannedOnce = false;
            Loaded += (s, e) => { if (scannedOnce) return; scannedOnce = true; LanScanStart_Click(s, e); };
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var hdrLan = new Grid { Margin = new Thickness(0,0,0,12) };
            hdrLan.ColumnDefinitions.Add(new ColumnDefinition());
            hdrLan.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock { Text = LanguageService.S("NetTools", "LanScannerTitle"), FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") });
            titleStack.Children.Add(new TextBlock { Text = LanguageService.S("NetTools", "LanScannerSubtitle"),
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,2,0,0), Opacity = 0.7 });
            Grid.SetColumn(titleStack, 0); hdrLan.Children.Add(titleStack);
            var lanActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Tag = "ActionPanel" };
            _btnScan = NetWindowHelper.PrimaryButton("▶ Scan", "Scan your local network for active devices");
            _btnStop = NetWindowHelper.DangerButton("■ Stop", "Stop the current scan");
            _btnStop.IsEnabled = false;
            _btnScan.Click += LanScanStart_Click;
            _btnStop.Click += LanScanStop_Click;
            lanActions.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnScan, _btnStop));
            NetWindowHelper.AddCloseButton(this, lanActions);
            Grid.SetColumn(lanActions, 1); hdrLan.Children.Add(lanActions);
            Grid.SetRow(hdrLan, 0); root.Children.Add(hdrLan);
            NetWindowHelper.MakeDraggable(hdrLan, this);

            _txtStatus = new TextBlock { Text = MainWindow.PT("Press Scan to discover devices on the local network."),
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,0,0,8) };
            Grid.SetRow(_txtStatus, 1); root.Children.Add(_txtStatus);

            // Grid + overlay in same row
            var gridHost = new Grid();
            _grid = new DataGrid
            { Style = (Style)System.Windows.Application.Current.Resources["VanillaDataGridStyle"],
                AutoGenerateColumns = false, IsReadOnly = true,
                Background = NetWindowHelper.Res("BgCardBrush"),
                Foreground = NetWindowHelper.Res("TextPrimaryBrush"),
                BorderThickness = new Thickness(0), GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowHeight = 34, ColumnHeaderHeight = 38, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow
            };
            var rowStyle = NetWindowHelper.DefaultRowStyle();
            var routerTrigger = new DataTrigger { Binding = new System.Windows.Data.Binding("IsRouter"), Value = true };
            routerTrigger.Setters.Add(new Setter(DataGridRow.FontWeightProperty, FontWeights.Bold));
            routerTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 96, 175, 255))));
            routerTrigger.Setters.Add(new Setter(DataGridRow.ToolTipProperty,
                "This is your router (default gateway)"));
            rowStyle.Triggers.Add(routerTrigger);
            _grid.RowStyle = rowStyle;
            _grid.Columns.Add(new DataGridTextColumn { Header = "IP",        Binding = new System.Windows.Data.Binding("IpAddress"),  Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells), MinWidth = 130 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Hostname",  Binding = new System.Windows.Data.Binding("Hostname"),   Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "MAC",       Binding = new System.Windows.Data.Binding("MacAddress"), Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells), MinWidth = 160 });
            var vendorCol = new DataGridTextColumn { Header = "Vendor", Binding = new System.Windows.Data.Binding("Vendor"),
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) };
            var vendorStyle = new System.Windows.Style(typeof(TextBlock));
            vendorStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, System.Windows.TextTrimming.CharacterEllipsis));
            vendorStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Vendor")));
            vendorCol.ElementStyle = vendorStyle;
            _grid.Columns.Add(vendorCol);
            _grid.Columns.Add(new DataGridTextColumn { Header = MainWindow.PT("Ping (ms)"), Binding = new System.Windows.Data.Binding("PingDisplay"),Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader), MinWidth = 110 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Status",    Binding = new System.Windows.Data.Binding("Status"),     Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader), MinWidth = 80 });
            _grid.MouseDoubleClick += LanDeviceGrid_DoubleClick;

            NetWindowHelper.EnableRightClickSelect(_grid);
            var ctx = new ContextMenu();
            string? SelIp() => (_grid.SelectedItem as SMDWin.Models.LocalNetworkDevice)?.IpAddress;
            string? SelMac() => (_grid.SelectedItem as SMDWin.Models.LocalNetworkDevice)?.MacAddress;
            var miIp = new MenuItem { Header = MainWindow.PT("Copy IP address") };
            miIp.Click += (_, _) => { if (SelIp() is { Length: > 0 } ip) System.Windows.Clipboard.SetText(ip); };
            var miMac = new MenuItem { Header = MainWindow.PT("Copy MAC address") };
            miMac.Click += (_, _) => { if (SelMac() is { Length: > 0 } m && m != "—") System.Windows.Clipboard.SetText(m); };
            var miPing = new MenuItem { Header = MainWindow.PT("Ping this device") };
            miPing.Click += (_, _) => { if (SelIp() is { Length: > 0 } ip) NetToolPage.OpenTool("Ping", ip); };
            var miWeb = new MenuItem { Header = MainWindow.PT("Open in browser") };
            miWeb.Click += (_, _) =>
            {
                if (SelIp() is not { Length: > 0 } ip) return;
                OpenIpInBrowser(ip);
            };
            ctx.Items.Add(miIp); ctx.Items.Add(miMac); ctx.Items.Add(new Separator()); ctx.Items.Add(miPing); ctx.Items.Add(miWeb);
            _grid.ContextMenu = ctx;
            var _phLan = NetWindowHelper.MakePlaceholder("No devices found — run a scan", "🖥");
            _grid.Tag = null; // ensure no conflict
            gridHost.Children.Add(_grid);
            gridHost.Children.Add(_phLan);

            // Detail overlay
            _detailPanel = new Border
            {
                Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                BorderBrush = NetWindowHelper.Res("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20,16,20,16), Margin = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 340, MaxWidth = 500,
                Visibility = Visibility.Collapsed
            };
            var detailRoot = new StackPanel();
            var detailHeader = new Grid { Margin = new Thickness(0,0,0,12) };
            detailHeader.ColumnDefinitions.Add(new ColumnDefinition());
            detailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _detailTitle = new TextBlock { FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush"),
                Cursor = Cursors.Hand,
                ToolTip = MainWindow.PT("Click to open this IP in your browser (useful for printers, routers, and other devices with a web interface)") };
            // the address shown in the title is the one opened (it does not depend on the table selection, which changes on rescan)
            _detailTitle.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenIpInBrowser(_detailTitle.Text);
            };
            Grid.SetColumn(_detailTitle, 0); detailHeader.Children.Add(_detailTitle);
            var closeBtn = new Button { Content = "✕", Width = 28, Height = 28,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                BorderThickness = new Thickness(0), FontSize = Ui.FontXL,
                Cursor = System.Windows.Input.Cursors.Hand };
            closeBtn.Click += (_, _) => _detailPanel.Visibility = Visibility.Collapsed;
            Grid.SetColumn(closeBtn, 1); detailHeader.Children.Add(closeBtn);
            detailRoot.Children.Add(detailHeader);

            _detailItems = new ItemsControl();
            var itemTemplate = new DataTemplate();
            var itemPanel = new FrameworkElementFactory(typeof(StackPanel));
            itemPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            itemPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0,3,0,3));
            var lblFef = new FrameworkElementFactory(typeof(TextBlock));
            lblFef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
            lblFef.SetValue(TextBlock.FontSizeProperty, 11.0);
            lblFef.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            lblFef.SetValue(TextBlock.ForegroundProperty, NetWindowHelper.Res("TextSecondaryBrush"));
            lblFef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Top);
            lblFef.SetValue(FrameworkElement.WidthProperty, 130.0);
            var valFef = new FrameworkElementFactory(typeof(TextBlock));
            valFef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value"));
            valFef.SetValue(TextBlock.FontSizeProperty, 12.0);
            valFef.SetValue(TextBlock.ForegroundProperty, NetWindowHelper.Res("TextPrimaryBrush"));
            valFef.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            valFef.SetValue(FrameworkElement.WidthProperty, 300.0);
            itemPanel.AppendChild(lblFef);
            itemPanel.AppendChild(valFef);
            itemTemplate.VisualTree = itemPanel;
            _detailItems.ItemTemplate = itemTemplate;
            detailRoot.Children.Add(_detailItems);

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,0) };
            var btnPing = NetWindowHelper.SecondaryButton("📡 Ping Again");
            btnPing.Padding = new Thickness(10,5,10,5);
            btnPing.Click += async (_, _) => { if (_selectedDevice != null) await ShowDeviceDetailAsync(_selectedDevice); };
            var btnPS = NetWindowHelper.SecondaryButton("Port Scan", "Run a port scan on the selected device");
            btnPS.Padding = new Thickness(10,5,10,5);
            btnPS.Margin = new Thickness(8,0,0,0);
            btnPS.Click += (_, _) =>
            {
                if (_selectedDevice == null) return;
                _detailPanel.Visibility = Visibility.Collapsed;
                OpenTool("Ports");
            };
            actionRow.Children.Add(btnPing);
            actionRow.Children.Add(btnPS);
            detailRoot.Children.Add(actionRow);
            _detailPanel.Child = detailRoot;
            gridHost.Children.Add(_detailPanel);
            System.Windows.Controls.Panel.SetZIndex(_detailPanel, 10);

            Grid.SetRow(gridHost, 2); root.Children.Add(gridHost);
            return root;
        }

        public override bool SelfTestStart() { LanScanStart_Click(this, new RoutedEventArgs()); return true; }
        public override void SelfTestStop() { if (_cts != null && !_cts.IsCancellationRequested) LanScanStop_Click(this, new RoutedEventArgs()); }

        private async void LanScanStart_Click(object s, RoutedEventArgs e)
        {
            var oldCts = _cts;
            oldCts?.Cancel();
            int myGen = ++_scanGeneration;
            var cts = new CancellationTokenSource();
            _cts = cts;
            if (oldCts != null)
            {
                // The old CancellationTokenSource is released after a short delay (the in-flight tasks of the old scan may still
                // briefly read IsCancellationRequested from it).
                _ = Task.Run(async () => { try { await Task.Delay(500); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } oldCts.Dispose(); });
            }

            _btnScan.IsEnabled = false; _btnStop.IsEnabled = true;
            _grid.ItemsSource = null;
            _txtStatus.Text = LanguageService.S("Network", "ScanningLocalNetwork");
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    if (myGen != _scanGeneration) return; // an older scan — ignored
                    Dispatcher.Invoke(() => _txtStatus.Text = msg);
                });
                var devices = await _netSvc.ScanLocalNetworkAsync(progress, cts.Token);
                if (myGen != _scanGeneration) return; // a new scan started in the meantime
                _grid.ItemsSource = devices;
                _txtStatus.Text = $"Found {devices.Count} device(s).";
            }
            catch (OperationCanceledException)
            {
                if (myGen == _scanGeneration) _txtStatus.Text = LanguageService.S("Network", "ScanStopped");
            }
            catch (Exception ex)
            {
                if (myGen == _scanGeneration) _txtStatus.Text = MainWindow.PT("Scan error: {0}", ex.Message);
            }
            finally
            {
                if (myGen == _scanGeneration) { _btnScan.IsEnabled = true; _btnStop.IsEnabled = false; }
            }
        }

        private void LanScanStop_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _btnScan.IsEnabled = true; _btnStop.IsEnabled = false;
        }

        private static void OpenIpInBrowser(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip.Trim(), out var addr)) return;
            string url = "http://" + addr;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "LAN open in browser (shell)");
                // plan B: the same address through explorer, when the default association for http does not respond
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", url) { UseShellExecute = true }); }
                catch (Exception ex2) { AppLogger.Warning(ex2, "LAN open in browser (explorer)"); }
            }
        }

        private async void LanDeviceGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_grid.SelectedItem is not SMDWin.Models.LocalNetworkDevice dev) return;
            _selectedDevice = dev;
            await ShowDeviceDetailAsync(dev);
        }

        private async System.Threading.Tasks.Task ShowDeviceDetailAsync(SMDWin.Models.LocalNetworkDevice dev)
        {
            _detailPanel.Visibility = Visibility.Collapsed;
            var items = new List<DeviceDetailRow>
            {
                new("IP Address",   dev.IpAddress),
                new("Hostname",     dev.Hostname != "—" ? dev.Hostname : "Resolving…"),
                new("MAC Address",  dev.MacAddress),
                new("Vendor / OUI", dev.Vendor),
                new("Ping",         dev.PingDisplay),
                new("Status",       dev.Status),
                new("Common ports", "Checking…"),
            };
            _detailItems.ItemsSource = items;
            _detailTitle.Text = dev.IpAddress;
            _detailPanel.Visibility = Visibility.Visible;

            await System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var r = await ping.SendPingAsync(dev.IpAddress, 1500);
                    if (r.Status == System.Net.NetworkInformation.IPStatus.Success) dev.PingMs = r.RoundtripTime.ToString();
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                string hostname = dev.Hostname;
                if (hostname == "—")
                {
                    try { hostname = (await System.Net.Dns.GetHostEntryAsync(dev.IpAddress).WaitAsync(TimeSpan.FromSeconds(2))).HostName; dev.Hostname = hostname; }
                    catch { hostname = "—"; }
                }

                var commonPorts = new (int Port, string Label)[]
                {
                    (80, "HTTP"), (443, "HTTPS"), (22, "SSH"), (445, "SMB/File sharing"),
                    (3389, "RDP"), (8080, "HTTP-alt"), (21, "FTP"), (23, "Telnet"),
                };
                var openPorts = new List<string>();
                var portTasks = commonPorts.Select(async cp =>
                {
                    try
                    {
                        using var client = new System.Net.Sockets.TcpClient();
                        var connectTask = client.ConnectAsync(dev.IpAddress, cp.Port);
                        var timeoutTask = System.Threading.Tasks.Task.Delay(400);
                        if (await System.Threading.Tasks.Task.WhenAny(connectTask, timeoutTask) == connectTask && client.Connected)
                            lock (openPorts) openPorts.Add($"{cp.Port} ({cp.Label})");
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }).ToArray();
                await System.Threading.Tasks.Task.WhenAll(portTasks);
                string portsText = openPorts.Count > 0 ? string.Join(", ", openPorts.OrderBy(p => p)) : "None of the common ports respond";

                Dispatcher.Invoke(() => _detailItems.ItemsSource = new List<DeviceDetailRow>
                {
                    new("IP Address",   dev.IpAddress),
                    new("Hostname",     hostname),
                    new("MAC Address",  dev.MacAddress),
                    new("Vendor / OUI", dev.Vendor),
                    new("Ping",         dev.PingDisplay),
                    new("Status",       dev.Status),
                    new("Common ports", portsText),
                });
            });
        }
    }

    internal record DeviceDetailRow(string Label, string Value);
}
