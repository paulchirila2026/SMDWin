using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using Orientation = System.Windows.Controls.Orientation;
using Button      = System.Windows.Controls.Button;

namespace SMDWin.Views.Net
{
    public class NetDiagnosticsPage : NetToolPage
    {
        private sealed class DiagRow
        {
            public string Label = "";
            public string Target = "";
            public TextBlock StatusIcon = null!;
            public Border StatusHost = null!;
            public TextBlock StatusDetail = null!;
            public Button PingBtn = null!;
            public CancellationTokenSource? Cts;
        }
        private readonly System.Collections.Generic.List<DiagRow> _rows = new();
        private Button _btnRecheck = null!;

        public NetDiagnosticsPage()
        {
            NetWindowHelper.SetupWindow(this, "Network Diagnostics — SMDWin", 720, 460);
            Content = BuildUI();
            Closed += (_, _) => { foreach (var r in _rows) r.Cts?.Cancel(); };
            Loaded += async (_, _) => await RecheckAll();
        }

        private UIElement BuildUI()
        {
            var root = NetWindowHelper.BuildWindowRoot(
                "Network Diagnostics",
                "Find out where a connection problem is — router, ISP, DNS, or the site itself",
                out var header);

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // action row
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // table card

            var actions = NetWindowHelper.GetActionPanel(header);
            NetWindowHelper.AddCloseButton(this, actions);
            Grid.SetRow(header, 0); root.Children.Add(header);
            NetWindowHelper.MakeDraggable(header, this);

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
            _btnRecheck = NetWindowHelper.SecondaryButton("↻ Recheck All");
            _btnRecheck.Click += async (_, _) => await RecheckAll();
            actionRow.Children.Add(_btnRecheck);
            var btnRouterPage = NetWindowHelper.SecondaryButton("🌐 Open Router Page");
            btnRouterPage.Margin = new Thickness(8,0,0,0);
            btnRouterPage.Click += (_, _) => OpenRouterPage();
            actionRow.Children.Add(btnRouterPage);
            Grid.SetRow(actionRow, 1); root.Children.Add(actionRow);

            var card = NetWindowHelper.ContentCard(null!);
            var tableStack = new StackPanel { Margin = new Thickness(2) };

            var headerRow = new Grid { Margin = new Thickness(4,0,4,8) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            void HeaderCell(int col, string text)
            {
                var tb = new TextBlock { Text = text, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                    Opacity = 0.6, Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
                Grid.SetColumn(tb, col); headerRow.Children.Add(tb);
            }
            HeaderCell(1, "STEP"); HeaderCell(2, "STATUS"); HeaderCell(3, "PING"); HeaderCell(4, "MONITOR");
            tableStack.Children.Add(headerRow);
            tableStack.Children.Add(new Border { Height = 1, Opacity = 0.15, Margin = new Thickness(4,0,4,8),
                Background = NetWindowHelper.Res("CardBorderBrush") });

            string gateway = DetectGateway() ?? "192.168.1.1";
            AddRow(tableStack, "Loopback", "127.0.0.1", "Tests this PC's own network adapter — catches a disabled adapter or a broken driver.");
            AddRow(tableStack, "Router", gateway, "Your router — if this fails, the problem is your local network (cable/Wi-Fi), not the internet.");
            AddRow(tableStack, "DNS", "google.com", "Resolves a domain name — if the router works but this fails, it's likely DNS or your ISP.");
            AddRow(tableStack, "Web", "https://google.com", "Actually connects to a website — the final, most complete check.");

            {
                var withHead = new Grid();
                withHead.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                withHead.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                withHead.Children.Add(NetWindowHelper.CardHead("Checks", "From this PC to the internet, step by step", "M2 9a15 15 0 0 1 20 0 M5 12.5a10 10 0 0 1 14 0 M8.5 16a5 5 0 0 1 7 0 M12 19.5h.01"));
                Grid.SetRow(tableStack, 1); withHead.Children.Add(tableStack);
                card.Child = withHead;
            }
            Grid.SetRow(card, 2); root.Children.Add(card);

            return root;
        }

        private void AddRow(StackPanel parent, string label, string target, string explanation)
        {
            var row = new DiagRow { Label = label, Target = target };

            var outer = new Grid { Margin = new Thickness(4,6,4,6) };
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            row.StatusIcon = new TextBlock { Text = "…", FontSize = Ui.FontM, VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
            row.StatusHost = new Border { Child = row.StatusIcon, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(row.StatusHost, 0); outer.Children.Add(row.StatusHost);

            var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = explanation };
            labelStack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") });
            Grid.SetColumn(labelStack, 1); outer.Children.Add(labelStack);

            var detailStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            detailStack.Children.Add(new TextBlock { Text = target, FontSize = Ui.FontS,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Opacity = 0.7 });
            row.StatusDetail = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
            detailStack.Children.Add(row.StatusDetail);
            Grid.SetColumn(detailStack, 2); outer.Children.Add(detailStack);

            row.PingBtn = NetWindowHelper.SecondaryButton("Ping");
            row.PingBtn.Padding = new Thickness(10,3,10,3);
            row.PingBtn.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            row.PingBtn.Click += async (_, _) => await RunPing(row);
            Grid.SetColumn(row.PingBtn, 3); outer.Children.Add(row.PingBtn);

            var monitorLink = new TextBlock
            {
                Text = "Monitor →", FontSize = Ui.FontS, Cursor = System.Windows.Input.Cursors.Hand,
                TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetWindowHelper.Res("AccentBrush"),
                ToolTip = "Open continuous ping monitoring for this target.",
            };
            string pingTarget = target.StartsWith("http") ? new Uri(target).Host : target;
            monitorLink.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    OpenTool("Ping", pingTarget);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetDiagnosticsPage.OpenPingMonitor"); }
            };
            Grid.SetColumn(monitorLink, 4); outer.Children.Add(monitorLink);

            parent.Children.Add(outer);
            parent.Children.Add(new Border { Height = 1, Opacity = 0.08, Margin = new Thickness(4,0,4,0),
                Background = NetWindowHelper.Res("CardBorderBrush") });

            _rows.Add(row);
        }

        private void SetRowStatus(DiagRow row, bool? ok, string detail)
        {
            if (ok == null && SettingsService.Current.DesignExperimentSignatureMotif)
            {
                // the logo's spinner (two opposite segments running around the gauge) while the check runs
                var spin = new SMDWin.Views.SmdLogo { Width = 16, Height = 16, Level = 0 };
                spin.SetSpinning(true);
                row.StatusHost.Child = spin;
            }
            else
            {
                row.StatusIcon.Text = ok == true ? "OK" : ok == false ? "Failed" : "…";
                row.StatusIcon.FontWeight = FontWeights.SemiBold;
                row.StatusIcon.SetResourceReference(TextBlock.ForegroundProperty,
                    ok == true ? "StatusOkBrush" : ok == false ? "StatusCritBrush" : "TextSecondaryBrush");
                row.StatusHost.Child = row.StatusIcon;
            }
            row.StatusDetail.Text = detail;
        }

        private async Task RecheckAll()
        {
            _btnRecheck.IsEnabled = false;
            foreach (var r in _rows) SetRowStatus(r, null, "Checking…");

            var (loopbackOk, loopbackDetail) = CheckLoopback();
            SetRowStatus(_rows[0], loopbackOk, loopbackDetail);

            if (loopbackOk)
            {
                var (routerOk, routerDetail) = await CheckRouterAsync(_rows[1].Target);
                SetRowStatus(_rows[1], routerOk, routerDetail);

                if (routerOk)
                {
                    var (dnsOk, dnsDetail) = await CheckDnsAsync(_rows[2].Target);
                    SetRowStatus(_rows[2], dnsOk, dnsDetail);

                    if (dnsOk)
                    {
                        var (webOk, webDetail) = await CheckWebAsync(_rows[3].Target);
                        SetRowStatus(_rows[3], webOk, webDetail);
                    }
                    else SetRowStatus(_rows[3], null, "Skipped — DNS failed first.");
                }
                else
                {
                    SetRowStatus(_rows[2], null, "Skipped — router unreachable.");
                    SetRowStatus(_rows[3], null, "Skipped — router unreachable.");
                }
            }
            else
            {
                SetRowStatus(_rows[1], null, "Skipped — no active network adapter.");
                SetRowStatus(_rows[2], null, "Skipped — no active network adapter.");
                SetRowStatus(_rows[3], null, "Skipped — no active network adapter.");
            }

            _btnRecheck.IsEnabled = true;
        }

        private static (bool ok, string detail) CheckLoopback()
        {
            try
            {
                bool any = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                    ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    && ni.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                return any ? (true, "Network adapter is up.") : (false, "No active adapter with an IP address found — check if it's disabled or the driver is missing.");
            }
            catch (Exception ex) { return (false, $"Could not check adapters: {ex.Message}"); }
        }

        private static async Task<(bool ok, string detail)> CheckRouterAsync(string gateway)
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(gateway, 1500);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? (true, $"{reply.RoundtripTime} ms")
                    : (false, $"No reply ({reply.Status}).");
            }
            catch (Exception ex) { return (false, $"Failed: {ex.Message}"); }
        }

        private static async Task<(bool ok, string detail)> CheckDnsAsync(string host)
        {
            try
            {
                var entry = await System.Net.Dns.GetHostEntryAsync(host);
                var ip = entry.AddressList.FirstOrDefault();
                return ip != null ? (true, $"→ {ip}") : (false, "No address returned.");
            }
            catch (Exception ex) { return (false, $"Failed: {ex.Message}"); }
        }

        private static async Task<(bool ok, string detail)> CheckWebAsync(string url)
        {
            try
            {
                using var cts = SharedHttp.TimeoutCts(TimeSpan.FromSeconds(5));
                var resp = await SharedHttp.Instance.GetAsync(url, cts.Token);
                bool ok = resp.IsSuccessStatusCode || (int)resp.StatusCode == 204;
                return (ok, $"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { return (false, $"Failed: {ex.Message}"); }
        }

        private async Task RunPing(DiagRow row)
        {
            row.Cts?.Cancel();
            row.Cts = new CancellationTokenSource();
            var ct = row.Cts.Token;

            row.PingBtn.IsEnabled = false;
            string host = row.Target.StartsWith("http") ? new Uri(row.Target).Host : row.Target;
            SetRowStatus(row, null, "Pinging…");

            const int count = 4;
            int sent = 0, received = 0;
            long min = long.MaxValue, max = 0, total = 0;

            try
            {
                for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                {
                    sent++;
                    try
                    {
                        using var ping = new System.Net.NetworkInformation.Ping();
                        var reply = await ping.SendPingAsync(host, 1500);
                        if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        {
                            received++;
                            long rtt = reply.RoundtripTime;
                            min = Math.Min(min, rtt); max = Math.Max(max, rtt); total += rtt;
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"NetDiagnosticsPage ping {host}: {ex.Message}"); }
                    if (i < count - 1) await Task.Delay(150, ct);
                }
            }
            catch (TaskCanceledException) { }

            if (ct.IsCancellationRequested) { row.PingBtn.IsEnabled = true; return; }

            if (received == 0)
            {
                SetRowStatus(row, false, "No reply — 100% loss");
            }
            else
            {
                double avg = total / (double)received;
                double lossPct = 100.0 * (sent - received) / sent;
                SetRowStatus(row, true, lossPct > 0
                    ? $"min {min} · avg {avg:F0} · max {max} ms · {lossPct:F0}% loss"
                    : $"min {min} · avg {avg:F0} · max {max} ms");
            }
            row.PingBtn.IsEnabled = true;
        }

        private static string? DetectGateway()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var gwa in ni.GetIPProperties().GatewayAddresses)
                    {
                        string g = gwa.Address.ToString();
                        if (!g.StartsWith("169.254") && g.Contains('.')) return g;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"NetDiagnosticsPage.DetectGateway: {ex.Message}"); }
            return null;
        }

        private void OpenRouterPage()
        {
            try
            {
                string? gw = DetectGateway();
                if (string.IsNullOrEmpty(gw))
                { SMDWin.Views.AppDialog.Show(LanguageService.S("Network", "NoGateway")); return; }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"http://{gw}") { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetDiagnosticsPage.OpenRouterPage"); }
        }
    }
}
