using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    public class NetLanSpeedPage : NetToolPage
    {
        private const int LanTestPort = 15099;
        private CancellationTokenSource? _cts;
        private TcpListener? _lanServer;

        private TextBox   _txtTarget   = null!;
        private TextBox   _txtDuration = null!;
        private TextBlock _txtDown     = null!;
        private TextBlock _txtUp       = null!;
        private TextBlock _txtPing     = null!;
        private TextBlock _txtStatus   = null!;
        private TextBox   _txtLog      = null!;
        private Button    _btnRun      = null!;
        private Button    _btnStop     = null!;
        private Button    _btnSelf     = null!;

        public NetLanSpeedPage()
        {
            NetWindowHelper.SetupWindow(this, "LAN Speed Test — SMDWin", 760, 580);
            Content = BuildUI();
            Loaded += (_, _) => AutoDetectGateway();
            Closed += (_, _) => { _cts?.Cancel(); _lanServer?.Stop(); };
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var hdrSpd = NetWindowHelper.ToolHeader("LAN Speed Test", "Gateway latency · LAN TCP throughput", out var spdActions);
            spdActions.Children.Add(NetWindowHelper.HeaderLabel("Target IP:"));
            _txtTarget = NetWindowHelper.HeaderBox("", 130, "Local IP of another PC running SMDWin (e.g. 192.168.1.5). Leave empty for Self-test.");
            spdActions.Children.Add(_txtTarget);
            spdActions.Children.Add(NetWindowHelper.HeaderLabel("Seconds:"));
            _txtDuration = NetWindowHelper.HeaderBox("5", 44, "Duration of each TCP test in seconds.");
            spdActions.Children.Add(_txtDuration);
            _btnSelf = NetWindowHelper.SecondaryButton("🖥 Self-test", "Test local disk speed as baseline");
            _btnSelf.Click += LanSpeedSelf_Click;
            spdActions.Children.Add(_btnSelf);
            _btnRun  = NetWindowHelper.PrimaryButton("▶ Run Test", "Start LAN speed test to the selected server");
            _btnStop = NetWindowHelper.DangerButton("■ Stop", "Stop the speed test");
            _btnStop.IsEnabled = false;
            _btnRun.Click  += LanSpeedRun_Click;
            _btnStop.Click += LanSpeedStop_Click;
            spdActions.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnRun, _btnStop));
            Grid.SetRow(hdrSpd, 0); root.Children.Add(hdrSpd);
            NetWindowHelper.MakeDraggable(hdrSpd, this);

            // Info card
            var info = new Border { Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,0,12) };
            info.Child = new TextBlock
            {
                Text = "• Gateway ping test: measures latency to your router (any PC).\n" +
                       "• LAN TCP throughput: measures transfer speed between TWO PCs.\n" +
                       "  → Run SMDWin on BOTH PCs, click Self-test on PC-B, enter PC-B IP on PC-A.\n" +
                       "  → Temporary TCP server opens on port 15099 — allow in firewall if needed.",
                FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush")
            };
            Grid.SetRow(info, 1); root.Children.Add(info);

            // Metric tiles
            var tiles = new Border { Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(20,16,20,16), Margin = new Thickness(0,0,0,12) };
            var tilesGrid = new Grid();
            tilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel Tile(string label, string unit, out TextBlock valRef, int col, string colorKey)
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = label, FontSize = Ui.FontS, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = NetWindowHelper.Res("TextSecondaryBrush") });
                valRef = new TextBlock { Text = "—", FontSize = 32, FontWeight = FontWeights.Bold,
                    Foreground = NetWindowHelper.Res(colorKey), HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(valRef);
                sp.Children.Add(new TextBlock { Text = unit, FontSize = Ui.FontS, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = NetWindowHelper.Res("TextSecondaryBrush") });
                Grid.SetColumn(sp, col); tilesGrid.Children.Add(sp);
                return sp;
            }
            Tile("⬇ Download (LAN)", "Mbps", out _txtDown, 0, "StatusSuccessBrush");
            Tile("⬆ Upload (LAN)",   "Mbps", out _txtUp,   1, "AccentBrush");
            Tile("Ping to gateway",    "ms",   out _txtPing,  2, "TextSecondaryBrush");
            tiles.Child = tilesGrid;
            Grid.SetRow(tiles, 3); root.Children.Add(tiles);

            // Status + log
            var bottom = new StackPanel();
            _txtStatus = new TextBlock { Text = MainWindow.PT("Enter target IP and press Run Test."),
                FontSize = Ui.FontM, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                Margin = new Thickness(0,0,0,8), TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(_txtStatus);
            var logBorder = new Border { Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(12) };
            _txtLog = new TextBox { Style = (Style)System.Windows.Application.Current.Resources["VanillaTextBoxStyle"], IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"), FontSize = Ui.FontS,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                MinHeight = 120, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            logBorder.Child = _txtLog;
            bottom.Children.Add(logBorder);
            Grid.SetRow(bottom, 4); root.Children.Add(bottom);
            return root;
        }

        private void AutoDetectGateway()
        {
            if (!string.IsNullOrWhiteSpace(_txtTarget.Text)) return;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        var addr = gw.Address;
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        { _txtTarget.Text = addr.ToString(); return; }
                    }
                }
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
        }

        private void Log(string msg) => Dispatcher.Invoke(() =>
        {
            _txtLog.Text += msg + "\n";
            _txtLog.ScrollToEnd();
        });

        private async void LanSpeedRun_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
            SetBusy(true);
            _txtDown.Text = "…"; _txtUp.Text = "…"; _txtPing.Text = "…"; _txtLog.Text = "";
            _txtStatus.Text = LanguageService.S("Network", "RunningLanSpeedTest");
            string target = _txtTarget.Text.Trim();
            if (!int.TryParse(_txtDuration.Text.Trim(), out int dur) || dur < 1) dur = 5;

            if (string.IsNullOrEmpty(target))
            {
                Log($"No target IP. Enter a Target IP or use Self-test for loopback.\n  On PC-B click Self-test, then on PC-A enter PC-B's IP and Run Test.");
                _txtStatus.Text = LanguageService.S("Network", "EnterTargetIpHint");
                SetBusy(false); return;
            }

            await Task.Run(async () =>
            {
                double dlMbps = 0, ulMbps = 0; long pingMs = -1;
                try
                {
                    Log($"[1/3] Pinging {target}…");
                    try
                    {
                        using var pinger = new System.Net.NetworkInformation.Ping();
                        var replies = new List<long>();
                        for (int i = 0; i < 4 && !ct.IsCancellationRequested; i++)
                        {
                            var r = await pinger.SendPingAsync(target, 1000);
                            if (r.Status == IPStatus.Success) replies.Add(r.RoundtripTime);
                            await Task.Delay(150, ct);
                        }
                        if (replies.Count > 0) pingMs = (long)new List<long>(replies).Average();
                        Log($"  Ping: {(pingMs >= 0 ? $"{pingMs} ms" : "unreachable")}");
                        Dispatcher.Invoke(() => _txtPing.Text = pingMs >= 0 ? pingMs.ToString() : "—");
                    }
                    catch { Log("  Ping failed."); }

                    if (ct.IsCancellationRequested) return;

                    Log($"[2/3] TCP download from {target}:{LanTestPort} ({dur}s)…");
                    try
                    {
                        using var tcp = new TcpClient(); tcp.ReceiveBufferSize = 1 << 20;
                        await tcp.ConnectAsync(target, LanTestPort, ct);
                        var ns = tcp.GetStream();
                        await ns.WriteAsync(new byte[] { 0x01 }, ct);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        long totalBytes = 0; var buf = new byte[65536];
                        while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested)
                        { int read = await ns.ReadAsync(buf, ct); if (read == 0) break; totalBytes += read; }
                        dlMbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0 : 0;
                        Log($"  ↓ {totalBytes/1024.0/1024.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s = {dlMbps:F1} Mbps");
                    }
                    catch (Exception ex) { Log($"  ↓ Download failed: {ex.Message}"); }

                    if (ct.IsCancellationRequested) return;

                    Log($"[3/3] TCP upload to {target}:{LanTestPort} ({dur}s)…");
                    try
                    {
                        using var tcp = new TcpClient(); tcp.SendBufferSize = 1 << 20;
                        await tcp.ConnectAsync(target, LanTestPort, ct);
                        var ns = tcp.GetStream();
                        await ns.WriteAsync(new byte[] { 0x02 }, ct);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        long totalBytes = 0; var buf = new byte[65536]; new Random().NextBytes(buf);
                        while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested)
                        { await ns.WriteAsync(buf, ct); totalBytes += buf.Length; }
                        ulMbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0 : 0;
                        Log($"  ↑ {totalBytes/1024.0/1024.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s = {ulMbps:F1} Mbps");
                    }
                    catch (Exception ex) { Log($"  ↑ Upload failed: {ex.Message}"); }

                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _txtDown.Text = dlMbps > 0 ? $"{dlMbps:F0}" : "—";
                            _txtUp.Text   = ulMbps > 0 ? $"{ulMbps:F0}" : "—";
                            _txtStatus.Text = $"Done. ↓{dlMbps:F0} Mbps  ↑{ulMbps:F0} Mbps  Ping: {(pingMs>=0?$"{pingMs} ms":"—")}";
                        });
                        Log("Complete.");
                    }
                }
                catch (OperationCanceledException) { Log("Stopped."); }
                catch (Exception ex) { Log($"Error: {ex.Message}"); }
            }, ct);

            SetBusy(false);
            if (_cts.IsCancellationRequested) _txtStatus.Text = LanguageService.S("Smart", "TestStopped");
        }

        private async void LanSpeedSelf_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
            SetBusy(true);
            _txtDown.Text = "…"; _txtUp.Text = "…"; _txtPing.Text = "—"; _txtLog.Text = "";
            _txtStatus.Text = LanguageService.S("Network", "RunningLoopbackSelfTest");
            if (!int.TryParse(_txtDuration.Text.Trim(), out int dur) || dur < 1) dur = 5;

            await Task.Run(async () =>
            {
                try
                {
                    Log("Loopback self-test (TCP via 127.0.0.1)…");
                    Log($"   Duration: {dur}s per direction");
                    double dlMbps = await RunLoopbackTest(dur, ct, Log, upload: false);
                    double ulMbps = await RunLoopbackTest(dur, ct, Log, upload: true);
                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _txtDown.Text = $"{dlMbps:F0}"; _txtUp.Text = $"{ulMbps:F0}";
                            _txtStatus.Text = MainWindow.PT("Loopback done. ↓{0} Mbps  ↑{1} Mbps  (local stack speed)", dlMbps.ToString("F0"), ulMbps.ToString("F0"));
                        });
                        Log("Self-test complete. This shows your PC's TCP stack speed, NOT actual LAN speed.");
                        // Start remote server
                        try
                        {
                            string? myIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                                .AddressList
                                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
                                ?.ToString();
                            if (myIp != null)
                            {
                                Log($"\nStarting TCP server on {myIp}:{LanTestPort}…");
                                Log($"   On the OTHER PC: enter {myIp} in Target IP and click Run Test.");
                                _ = StartLanServerAsync(ct, Log);
                            }
                        }
                        catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
                    }
                }
                catch (OperationCanceledException) { Log("Stopped."); }
                catch (Exception ex) { Log($"Error: {ex.Message}"); }
            }, ct);

            SetBusy(false);
        }

        private void LanSpeedStop_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel(); _lanServer?.Stop();
            _txtStatus.Text = LanguageService.S("Smart", "Stopping");
        }

        private void SetBusy(bool busy)
        {
            _btnRun.IsEnabled  = !busy;
            _btnStop.IsEnabled = busy;
            _btnSelf.IsEnabled = !busy;
        }

        private static async Task<double> RunLoopbackTest(int dur, CancellationToken ct, Action<string> log, bool upload)
        {
            int port = new Random().Next(40000, 59000);
            long totalBytes = 0;
            var buf = new byte[65536];
            if (upload) new Random().NextBytes(buf);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var serverReady = new SemaphoreSlim(0, 1);

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start(); serverReady.Release();
                    using var client = await listener.AcceptTcpClientAsync();
                    listener.Stop();
                    client.ReceiveBufferSize = client.SendBufferSize = 1 << 20;
                    var ns = client.GetStream();
                    if (upload)
                    { var rb = new byte[65536]; while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested)
                      { int r = await ns.ReadAsync(rb, ct); if (r == 0) break; Interlocked.Add(ref totalBytes, r); } }
                    else
                    { var sb = new byte[65536]; new Random().NextBytes(sb);
                      while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested)
                      { await ns.WriteAsync(sb, ct); Interlocked.Add(ref totalBytes, sb.Length); } }
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            });

            await serverReady.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient(); tcp.SendBufferSize = tcp.ReceiveBufferSize = 1 << 20;
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
                var ns = tcp.GetStream();
                if (upload)
                { while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested) await ns.WriteAsync(buf, ct); }
                else
                { var rb = new byte[65536]; while (sw.Elapsed.TotalSeconds < dur && !ct.IsCancellationRequested)
                  { int r = await ns.ReadAsync(rb, ct); if (r == 0) break; } }
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            await serverTask; sw.Stop();
            double mbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0 : 0;
            log($"  {(upload ? "↑ Upload" : "↓ Download")} loopback: {mbps:F0} Mbps");
            return mbps;
        }

        private async Task StartLanServerAsync(CancellationToken ct, Action<string> log)
        {
            try
            {
                _lanServer?.Stop();
                _lanServer = new TcpListener(System.Net.IPAddress.Any, LanTestPort);
                _lanServer.Start();
                log($"   Server listening on port {LanTestPort}. Waiting for connections…");
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _lanServer.AcceptTcpClientAsync(ct);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                client.SendBufferSize = client.ReceiveBufferSize = 1 << 20;
                                var ns = client.GetStream();
                                var cmd = new byte[1]; await ns.ReadAsync(cmd, ct);
                                var bigBuf = new byte[65536]; new Random().NextBytes(bigBuf);
                                if (cmd[0] == 0x01)
                                    while (!ct.IsCancellationRequested) await ns.WriteAsync(bigBuf, ct);
                                else
                                    while (!ct.IsCancellationRequested) { int r = await ns.ReadAsync(bigBuf, ct); if (r == 0) break; }
                            }
                            catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
                            finally { client.Dispose(); }
                        }, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
                }
            }
            catch (Exception ex) { log($"   Server error: {ex.Message}"); }
            finally { _lanServer?.Stop(); log("   Server stopped."); }
        }
    }
}
