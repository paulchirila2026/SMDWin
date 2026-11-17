using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

using Brushes     = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using TextBox     = System.Windows.Controls.TextBox;
using Button      = System.Windows.Controls.Button;

namespace SMDWin.Views.Net
{
    public class NetTraceroutePage : NetToolPage
    {
        private const int ProbesPerHop = 3;
        private const int MaxHops = 30;

        private TextBox   _txtHost   = null!;
        private TextBlock _txtStatus = null!;
        private DataGrid  _grid      = null!;
        private Button    _btnStart  = null!;
        private Button    _btnStop   = null!;
        private CancellationTokenSource? _traceCts;

        private sealed class TraceHop : INotifyPropertyChanged
        {
            private string _host = "";
            public int    Hop      { get; set; }
            public string Ip       { get; set; } = "";
            public string Host     { get => _host; set { _host = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Host))); } }
            public string Loss     { get; set; } = "";
            public string Rtt      { get; set; } = "";
            public string RttColor { get; set; } = "";
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public NetTraceroutePage()
        {
            NetWindowHelper.SetupWindow(this, "Traceroute — SMDWin", 720, 560);
            Content = BuildUI();
            Closed += (_, _) => { _traceCts?.Cancel(); };
        }

        // the colour of a state from the theme, as hex text for the column converter
        private static string StatusHex(string brushKey)
            => NetWindowHelper.Res(brushKey) is System.Windows.Media.SolidColorBrush b ? b.Color.ToString() : "";

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // antet
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // stare
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // tabel

            var header = NetWindowHelper.ToolHeader(
                LanguageService.S("NetTools", "TracerouteTitle", "Traceroute"),
                LanguageService.S("NetTools", "TracerouteDesc", "Trace the network path to a host, hop by hop"),
                out var actions);
            actions.Children.Add(NetWindowHelper.HeaderLabel(LanguageService.S("NetTools", "HostIpLabel", "Host / IP:")));
            _txtHost = NetWindowHelper.HeaderBox("8.8.8.8", 190);
            _txtHost.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) StartTrace_Click(s, new RoutedEventArgs()); };
            actions.Children.Add(_txtHost);
            _btnStart = NetWindowHelper.PrimaryButton("▶ " + LanguageService.S("NetTools", "TraceButton", "Trace"));
            _btnStop  = NetWindowHelper.DangerButton("■ " + LanguageService.S("Common", "Stop", "Stop"));
            _btnStop.IsEnabled = false;
            _btnStart.Click += StartTrace_Click;
            _btnStop.Click  += StopTrace_Click;
            actions.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnStart, _btnStop));
            Grid.SetRow(header, 0); root.Children.Add(header);
            NetWindowHelper.MakeDraggable(header, this);

            _txtStatus = NetWindowHelper.StatusText(LanguageService.S("NetTools", "EnterHostTraceHint", "Enter a host and press Trace."));
            Grid.SetRow(_txtStatus, 1); root.Children.Add(_txtStatus);

            var gridBorder = NetWindowHelper.ContentCard(null!);
            _grid = NetWindowHelper.StandardDataGrid();
            _grid.Columns.Add(new DataGridTextColumn { Header = "#",        Binding = new System.Windows.Data.Binding("Hop"),  Width = new DataGridLength(40) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "IP",       Binding = new System.Windows.Data.Binding("Ip"),   Width = new DataGridLength(130) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Hostname", Binding = new System.Windows.Data.Binding("Host"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = LanguageService.S("NetTools", "TraceLossColumn", "Loss"), Binding = new System.Windows.Data.Binding("Loss"), Width = new DataGridLength(70) });

            var rttCol = new DataGridTemplateColumn { Header = LanguageService.S("NetTools", "TraceRttColumn", "Time (avg)"), Width = new DataGridLength(110) };
            var rttTpl = new DataTemplate();
            var rttFef = new FrameworkElementFactory(typeof(TextBlock));
            rttFef.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("Rtt"));
            rttFef.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("RttColor") { Converter = new NetStringToBrushConverter() });
            rttFef.SetValue(TextBlock.FontWeightProperty,  FontWeights.SemiBold);
            rttFef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            rttFef.SetValue(TextBlock.MarginProperty, new Thickness(4, 0, 0, 0));
            rttTpl.VisualTree = rttFef; rttCol.CellTemplate = rttTpl;
            _grid.Columns.Add(rttCol);
            var placeholder = NetWindowHelper.MakePlaceholder(LanguageService.S("NetTools", "TraceHint", "Type a host and press Trace — every router on the way appears here"), "🌐");
            NetWindowHelper.ShowWhenEmpty(_grid, placeholder);
            var gridWrap = new Grid();
            gridWrap.Children.Add(_grid);
            gridWrap.Children.Add(placeholder);
            var withHead = new Grid();
            withHead.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            withHead.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            withHead.Children.Add(NetWindowHelper.CardHead("Route", "Every router on the way, in order", "M4 6h16 M4 12h16 M4 18h10"));
            Grid.SetRow(gridWrap, 1); withHead.Children.Add(gridWrap);
            gridBorder.Child = withHead;
            Grid.SetRow(gridBorder, 2); root.Children.Add(gridBorder);
            return root;
        }

        private static string FormatMs(double ms) => ms < 10 ? $"{ms:0.0} ms" : $"{ms:0} ms";

        public override bool SelfTestStart() { StartTrace_Click(this, new RoutedEventArgs()); return true; }
        public override void SelfTestStop() => StopTrace_Click(this, new RoutedEventArgs());

        private async void StartTrace_Click(object s, RoutedEventArgs e)
        {
            _traceCts?.Cancel();
            _traceCts = new CancellationTokenSource();
            var ct = _traceCts.Token;
            string host = _txtHost.Text.Trim();
            if (string.IsNullOrEmpty(host)) return;

            _btnStart.IsEnabled = false; _btnStop.IsEnabled = true;
            _txtStatus.Text = string.Format(LanguageService.S("NetTools", "TracingHostFmt", "Tracing {0}…"), host);
            var hops = new ObservableCollection<TraceHop>();
            _grid.ItemsSource = hops;
            string okHex = StatusHex("StatusOkBrush"), warnHex = StatusHex("StatusWarnBrush"), critHex = StatusHex("StatusCritBrush"), naHex = StatusHex("StatusNaBrush");
            bool reached = false;
            double lastRtt = 0;

            try
            {
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    int silent = 0;
                    for (int ttl = 1; ttl <= MaxHops && !ct.IsCancellationRequested; ttl++)
                    {
                        // each hop is measured several times; RoundtripTime is 0 for "TTL expired" replies, so an own clock is used
                        var times = new List<double>();
                        string ip = "*";
                        bool done = false;
                        for (int probe = 0; probe < ProbesPerHop && !ct.IsCancellationRequested; probe++)
                        {
                            using var ping = new Ping();
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            PingReply? reply = null;
                            try { reply = await ping.SendPingAsync(host, 2500, new byte[32], new PingOptions(ttl, true)); }
                            catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException or InvalidOperationException)
                            {
                                AppLogger.Debug($"Traceroute {host} ttl {ttl}: {ex.Message}");
                            }
                            sw.Stop();
                            if (reply != null && reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                            {
                                if (reply.Address != null) ip = reply.Address.ToString();
                                times.Add(reply.RoundtripTime > 0 ? reply.RoundtripTime : sw.Elapsed.TotalMilliseconds);
                                if (reply.Status == IPStatus.Success) done = true;
                            }
                        }
                        if (ct.IsCancellationRequested) break;

                        double avg = times.Count > 0 ? times.Average() : 0;
                        int lossPct = (int)Math.Round((ProbesPerHop - times.Count) * 100.0 / ProbesPerHop);
                        var hop = new TraceHop
                        {
                            Hop = ttl, Ip = ip, Host = ip == "*" ? "" : ip,
                            Loss = $"{lossPct}%",
                            Rtt = times.Count > 0 ? FormatMs(avg) : "*",
                            RttColor = times.Count == 0 ? naHex : avg > 150 ? critHex : avg > 60 ? warnHex : okHex,
                        };
                        await Dispatcher.InvokeAsync(() => hops.Add(hop));
                        silent = times.Count == 0 ? silent + 1 : 0;
                        if (times.Count > 0) lastRtt = avg;
                        if (ip != "*") _ = ResolveHostAsync(hop, ip, ct);   // the names are filled in as they arrive, without stopping the trace
                        if (done) { reached = true; break; }
                        if (silent >= 8) break;
                    }
                }, ct);
            }
            catch (OperationCanceledException) { AppLogger.Debug("Traceroute: stopped"); }
            catch (Exception ex) { AppLogger.Warning(ex, "Traceroute " + host); }

            _txtStatus.Text = ct.IsCancellationRequested
                ? LanguageService.S("NetTools", "TraceStopped", "Stopped.")
                : reached
                    ? string.Format(LanguageService.S("NetTools", "TraceCompleteFmt", "Complete — {0} hops."), hops.Count) + $"  ·  {FormatMs(lastRtt)}"
                    : string.Format(LanguageService.S("NetTools", "TraceNotReachedFmt", "Stopped after {0} hops — the host did not answer."), hops.Count);
            _btnStart.IsEnabled = true; _btnStop.IsEnabled = false;
        }

        private async System.Threading.Tasks.Task ResolveHostAsync(TraceHop hop, string ip, CancellationToken ct)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(3000);
                var entry = await System.Net.Dns.GetHostEntryAsync(ip, limit.Token);
                if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ip)
                    await Dispatcher.InvokeAsync(() => hop.Host = entry.HostName);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
            {
                AppLogger.Debug($"Traceroute: no name for {ip}");
            }
        }

        private void StopTrace_Click(object s, RoutedEventArgs e) => _traceCts?.Cancel();
    }
}
