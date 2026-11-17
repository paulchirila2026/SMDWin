using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;

using Orientation = System.Windows.Controls.Orientation;
using TextBox     = System.Windows.Controls.TextBox;
using Button               = System.Windows.Controls.Button;

namespace SMDWin.Views.Net
{
    public class NetPortScanPage : NetToolPage
    {
        private TextBox   _txtHost      = null!;
        private TextBox   _txtPortStart = null!;
        private TextBox   _txtPortStop  = null!;
        private TextBlock _txtStatus    = null!;
        private DataGrid  _grid         = null!;
        private Button    _btnScan      = null!;
        private Button    _btnStop      = null!;
        private CancellationTokenSource? _cts;
        // a generation number, as in the LAN scan — a Stop quickly followed by a new Scan does not let the old scan's
        // state/text overwrite the new scan.
        private int _scanGeneration;

        public NetPortScanPage()
        {
            NetWindowHelper.SetupWindow(this, "Port Scanner — SMDWin", 740, 560);
            Content = BuildUI();
                        Closed += (_, _) => _cts?.Cancel();
        }

        private UIElement BuildUI()
        {
            var root = NetWindowHelper.BuildWindowRoot(
                "Port Scanner",
                "TCP port scan on any host (max 5 000 ports per scan)",
                out var header);

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var actions = NetWindowHelper.GetActionPanel(header);
            // the fields in the header (the same position as in the other tools), then Scan / Stop
            void Lbl(string t) => actions.Children.Add(NetWindowHelper.HeaderLabel(t));
            TextBox Box(string v, double w)
            {
                var tb = NetWindowHelper.HeaderBox(v, w);
                actions.Children.Add(tb); return tb;
            }
            Lbl("Host:"); _txtHost = Box("127.0.0.1", 130);
            Lbl("Ports:"); _txtPortStart = Box("1", 56);
            Lbl("–"); _txtPortStop  = Box("1024", 56);
            _btnScan = NetWindowHelper.PrimaryButton("▶ Scan", "Start TCP port scan on the specified host");
            _btnStop = NetWindowHelper.DangerButton("■ Stop", "Stop the current port scan");
            _btnScan.Click += ScanRun_Click;
            _btnStop.Click += ScanStop_Click;
            _btnStop.IsEnabled = false;   // the toggle shows Scan until a scan runs
            actions.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnScan, _btnStop));
            NetWindowHelper.AddCloseButton(this, actions);
            Grid.SetRow(header, 0); root.Children.Add(header);
            NetWindowHelper.MakeDraggable(header, this);

            _txtStatus = NetWindowHelper.StatusText("Enter host and press Scan…");
            Grid.SetRow(_txtStatus, 2); root.Children.Add(_txtStatus);

            var gridBorder = NetWindowHelper.ContentCard(null!);
            _grid = NetWindowHelper.StandardDataGrid();
            _grid.Columns.Add(new DataGridTextColumn { Header = "Port",     Binding = new System.Windows.Data.Binding("PortDisplay"), Width = new DataGridLength(65) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Protocol", Binding = new System.Windows.Data.Binding("Protocol"),    Width = new DataGridLength(70) });

            var statusCol = new DataGridTemplateColumn { Header = "Status", Width = new DataGridLength(100) };
            var statusTpl = new DataTemplate();
            var fef = new FrameworkElementFactory(typeof(TextBlock));
            fef.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("StatusText"));
            fef.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("StatusColor") { Converter = new NetStringToBrushConverter() });
            fef.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            fef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            statusTpl.VisualTree = fef; statusCol.CellTemplate = statusTpl;
            _grid.Columns.Add(statusCol);
            _grid.Columns.Add(new DataGridTextColumn { Header = "Service", Binding = new System.Windows.Data.Binding("Service"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var _ph = NetWindowHelper.MakePlaceholder(LanguageService.S("NetTools", "PortsHint", "Type a host and a port range, then press Scan — the ports that answer appear here"), "🔍");
            NetWindowHelper.ShowWhenEmpty(_grid, _ph);
            var _gridWrap = new Grid();
            _gridWrap.Children.Add(_grid);
            _gridWrap.Children.Add(_ph);
            {
                var withHead = new Grid();
                withHead.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                withHead.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                withHead.Children.Add(NetWindowHelper.CardHead("Open ports", "TCP ports that answered", "M6 11h12v10H6z M9 11V7a3 3 0 0 1 6 0v4"));
                Grid.SetRow(_gridWrap, 1); withHead.Children.Add(_gridWrap);
                gridBorder.Child = withHead;
            }
            Grid.SetRow(gridBorder, 3); root.Children.Add(gridBorder);
            return root;
        }

        public override bool SelfTestStart() { ScanRun_Click(this, new RoutedEventArgs()); return true; }
        public override void SelfTestStop() { if (_cts != null && !_cts.IsCancellationRequested) ScanStop_Click(this, new RoutedEventArgs()); }

        private async void ScanRun_Click(object s, RoutedEventArgs e)
        {
            var oldCts = _cts;
            oldCts?.Cancel();
            int myGen = ++_scanGeneration;
            var cts = new CancellationTokenSource();
            _cts = cts;
            if (oldCts != null)
                _ = Task.Run(async () => { try { await Task.Delay(500); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } oldCts.Dispose(); });
            string host = _txtHost.Text.Trim();
            if (!int.TryParse(_txtPortStart.Text, out int pStart)) pStart = 1;
            if (!int.TryParse(_txtPortStop.Text,  out int pStop))  pStop  = 1024;
            pStart = Math.Max(1, Math.Min(65535, pStart));
            pStop  = Math.Max(pStart, Math.Min(65535, pStop));
            if (pStop - pStart > 5000) { NetWindowHelper.ShowInfo("Max 5 000 ports per scan.", "SMD Win", this); return; }

            _btnScan.IsEnabled = false; _btnStop.IsEnabled = true;
            _txtStatus.Text = $"Scanning {host} ports {pStart}–{pStop}…";
            var results = new ObservableCollection<PortScanResult>();
            _grid.ItemsSource = results;
            int scanned = 0, total = pStop - pStart + 1;
            var progress = new Progress<PortScanResult>(r => {
                if (myGen != _scanGeneration) return;   // an older scan — ignored
                if (r.IsOpen) results.Add(r);
                scanned++;
                if (scanned % 50 == 0 || scanned == total)
                    _txtStatus.Text = $"Scanned {scanned}/{total} — {results.Count} open";
            });
            try
            {
                await new NetworkTrafficService().ScanPortsAsync(host, pStart, pStop, progress, cts.Token);
                if (myGen != _scanGeneration) return;    // a new scan started in the meantime
                _txtStatus.Text = $"Complete — {results.Count} open ports out of {total}";
            }
            catch (OperationCanceledException)
            {
                if (myGen == _scanGeneration) _txtStatus.Text = LanguageService.S("Network", "ScanStopped");
            }
            finally
            {
                if (myGen == _scanGeneration) { _btnScan.IsEnabled = true; _btnStop.IsEnabled = false; }
            }
        }

        private void ScanStop_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel(); _txtStatus.Text = LanguageService.S("Smart", "Stopping");
        }
    }
}
