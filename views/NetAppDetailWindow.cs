using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SMDWin.Services;
using SMDWin.Views.Net;

using Binding             = System.Windows.Data.Binding;
using Button              = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// Network › Apps › double click: one program's traffic — download / upload for the last 5 minutes, every address it talked to
    /// (site name from the DNS answers, service by port, country, bytes each way, last activity) and a CSV export.
    /// Fed by <see cref="EtwNetMonitor"/>; without it (session could not start) it shows the connection details only.
    /// </summary>
    public sealed class NetAppDetailWindow : Window
    {
        public sealed class Row
        {
            public string Host { get; init; } = "";
            public string Address { get; init; } = "";
            public string Proto { get; init; } = "";
            public string Service { get; init; } = "";
            public string Country { get; init; } = "";
            public string SentText { get; init; } = "";
            public string RecvText { get; init; } = "";
            public string LastText { get; init; } = "";
            public ulong Sent, Recv;
        }

        private readonly int _pid;
        private readonly string _name, _path;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private MiniLineChart _chart = null!;
        private DataGrid _grid = null!;
        private TextBlock _txtTotals = null!, _txtNow = null!, _txtNote = null!;
        private List<Row> _rows = new();
        private int _tick;
        private readonly Dictionary<string, string> _reverse = new();   // reverse DNS, only for addresses without a name
        private int _reverseBusy;

        public NetAppDetailWindow(int pid, string name, string path)
        {
            _pid = pid; _name = name; _path = path;
            NetWindowHelper.SetupWindow(this, $"{name} — network — SMDWin", 980, 640);
            Content = NetWindowHelper.WindowShell(BuildUI());
            _timer.Tick += (_, _) => Refresh();
            Loaded += (_, _) => { Refresh(); _timer.Start(); };
            Closed += (_, _) => _timer.Stop();
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hdr = NetWindowHelper.ToolHeader(_name, string.IsNullOrEmpty(_path) ? $"PID {_pid}" : $"PID {_pid} · {_path}", out var actions);
            var btnExport = NetWindowHelper.SecondaryButton("Export CSV", "Saves the list of addresses as a CSV file (opens in Excel)");
            btnExport.Margin = new Thickness(0, 0, 8, 0);
            btnExport.Click += (_, _) => ExportCsv();
            actions.Children.Add(btnExport);
            NetWindowHelper.AddCloseButton(this, actions);
            NetWindowHelper.MakeDraggable(hdr, this);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);

            var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
            _txtNow = new TextBlock { FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 24, 0),
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") };
            _txtTotals = new TextBlock { FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
            stats.Children.Add(_txtNow); stats.Children.Add(_txtTotals);
            Grid.SetRow(stats, 1); root.Children.Add(stats);

            _chart = new MiniLineChart
            {
                Capacity = 300, Min = 0, MinSpan = 8,
                ScaleText = v => v >= 1024 ? $"{v / 1024:F1} MB/s" : $"{v:F0} KB/s",
                EmptyText = "Collecting… (one point per second, last 5 minutes)",
            };
            var chartCard = new Border { Background = NetWindowHelper.Res("BgCardBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12), Child = _chart,
                ToolTip = "Blue: download · grey: upload" };
            Grid.SetRow(chartCard, 2); root.Children.Add(chartCard);

            _grid = NetWindowHelper.StandardDataGrid();
            void Col(string header, string path, double width, bool star = false) =>
                _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path),
                    Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
            Col("Site", "Host", 0, star: true);
            Col("Address", "Address", 210);
            Col("Proto", "Proto", 60);
            Col("Service", "Service", 170);
            Col("Country", "Country", 140);
            Col("↑ Sent", "SentText", 85);
            Col("↓ Received", "RecvText", 95);
            Col("Last", "LastText", 80);
            var gridCard = new Border { Background = NetWindowHelper.Res("BgCardBrush"), CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()),
                Child = _grid };
            Grid.SetRow(gridCard, 3); root.Children.Add(gridCard);

            _txtNote = new TextBlock { FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0),
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                Text = "Counted by Windows' own network events (TCP and UDP, IPv4 and IPv6) since the monitor started. Site names come " +
                       "from the DNS answers seen meanwhile; names in brackets are reverse lookups (the provider, not the site). Country: DB-IP (CC BY 4.0)." };
            Grid.SetRow(_txtNote, 4); root.Children.Add(_txtNote);
            return root;
        }

        private void Refresh()
        {
            try
            {
                var mon = EtwNetMonitor.Instance;
                if (!mon.EnsureStarted())
                {
                    _txtNow.Text = "Live traffic per address is not available";
                    _txtTotals.Text = mon.LastError.Length > 0 ? $"Windows did not start the network event session ({mon.LastError})." : "";
                    return;
                }
                var t = mon.Get(_pid);
                if (t == null)
                {
                    _txtNow.Text = "↓ 0 KB/s   ↑ 0 KB/s";
                    _txtTotals.Text = $"No traffic seen yet (monitoring since {mon.StartedUtc.ToLocalTime():HH:mm:ss})";
                    return;
                }
                _txtNow.Text = $"↓ {Rate(t.RecvKBs)}   ↑ {Rate(t.SendKBs)}";
                _txtTotals.Text = $"Since {mon.StartedUtc.ToLocalTime():HH:mm:ss}:  ↓ {Bytes(t.Recv)}  ·  ↑ {Bytes(t.Sent)}  ·  {t.Remotes.Count} addresses";
                _chart.SetData(t.HistRecv, t.HistSend);

                if (_tick++ % 2 != 0) return;   // the table every 2 s: it keeps the selection and scroll steadier
                var now = DateTime.UtcNow;
                _rows = t.Remotes.Select(r =>
                {
                    string host = r.Host;
                    if (host.Length == 0) lock (_reverse) if (_reverse.TryGetValue(r.Address.ToString(), out var rv)) host = rv;
                    return new Row
                    {
                        Host = host.Length > 0 ? host : "—",
                        Address = r.Endpoint,
                        Proto = r.Udp ? "UDP" : "TCP",
                        Service = r.Service.Length > 0 ? r.Service : "—",
                        Country = GeoIpService.CountryDisplay(r.Endpoint) is { Length: > 0 } c ? c : "—",
                        Sent = r.Sent, Recv = r.Recv,
                        SentText = Bytes(r.Sent), RecvText = Bytes(r.Recv),
                        LastText = Ago(now - r.LastSeenUtc),
                    };
                }).ToList();
                var sel = (_grid.SelectedItem as Row)?.Address;
                _grid.ItemsSource = _rows;
                if (sel != null) _grid.SelectedItem = _rows.FirstOrDefault(x => x.Address == sel);
                StartReverseLookups(t.Remotes.Where(r => r.Host.Length == 0).Select(r => r.Address).Take(15));
            }
            catch (Exception ex) { AppLogger.Debug($"NetAppDetail refresh: {ex.Message}"); }
        }

        /// <summary>Addresses resolved before monitoring started have no DNS answer to match; a reverse lookup gives at least the
        /// provider's name (e.g. "…1e100.net" = Google). Shown in brackets, a few at a time, each address once.</summary>
        private void StartReverseLookups(IEnumerable<System.Net.IPAddress> ips)
        {
            if (System.Threading.Interlocked.Exchange(ref _reverseBusy, 1) == 1) return;
            var todo = ips.Where(ip => { lock (_reverse) return !_reverse.ContainsKey(ip.ToString()); }).ToList();
            if (todo.Count == 0) { _reverseBusy = 0; return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var ip in todo)
                    {
                        string name = "";
                        try
                        {
                            var task = System.Net.Dns.GetHostEntryAsync(ip);
                            if (await Task.WhenAny(task, Task.Delay(2000)) == task && task.Result.HostName != ip.ToString())
                                name = $"({task.Result.HostName})";
                        }
                        catch (Exception ex) { AppLogger.Debug($"Reverse DNS {ip}: {ex.Message}"); }
                        lock (_reverse) _reverse[ip.ToString()] = name;
                    }
                }
                finally { _reverseBusy = 0; }
            });
        }

        private void ExportCsv()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Site,Address,Protocol,Service,Country,Sent bytes,Received bytes,Last activity");
                foreach (var r in _rows)
                    sb.AppendLine(string.Join(",", new[] { r.Host, r.Address, r.Proto, r.Service, r.Country, r.Sent.ToString(), r.Recv.ToString(), r.LastText }
                        .Select(Csv)));
                string dir = SettingsService.GetEffectiveReportFolder();
                Directory.CreateDirectory(dir);
                string safe = string.Concat(_name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
                string path = Path.Combine(dir, $"Network-{safe}_{DateTime.Now:yyyy-MM-dd_HHmm}.csv");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch (Exception ex) { AppLogger.Debug($"Open export folder: {ex.Message}"); }
            }
            catch (Exception ex) { AppDialog.Show($"Could not save the file: {ex.Message}", "Export", AppDialog.Kind.Error, this); }
        }

        internal static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        internal static string Rate(double kbs) => kbs >= 1024 ? $"{kbs / 1024:F1} MB/s" : $"{kbs:F0} KB/s";
        internal static string Bytes(ulong b) => b >= 1UL << 30 ? $"{b / (double)(1UL << 30):F2} GB"
                                              : b >= 1UL << 20 ? $"{b / (double)(1UL << 20):F1} MB"
                                              : b >= 1024 ? $"{b / 1024.0:F0} KB" : $"{b} B";
        private static string Ago(TimeSpan t) => t.TotalSeconds < 5 ? "now" : t.TotalMinutes < 1 ? $"{t.TotalSeconds:F0} s ago"
                                              : t.TotalHours < 1 ? $"{t.TotalMinutes:F0} min ago" : $"{t.TotalHours:F0} h ago";
    }
}
