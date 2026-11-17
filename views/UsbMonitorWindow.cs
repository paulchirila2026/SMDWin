using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin.Services;
using SMDWin.Views.Net;

using Binding             = System.Windows.Data.Binding;
using Control             = System.Windows.Controls.Control;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using CheckBox            = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// Tools › USB monitor: watches USB devices connect and drop out, live, for the "my mouse disconnects now and then" cases.
    /// - Windows tells every top-level window when devices change (WM_DEVICECHANGE); the list is then read again and compared
    ///   (plus a check every 5 s, in case a message is missed).
    /// - From the Windows event log: how many times each device was started since Windows started — reconnections that
    ///   happened before the monitor was opened.
    /// - The two power settings behind most such cases (USB selective suspend, "Allow the computer to turn off this device"),
    ///   each with a one-click fix.
    /// The window can stay open (also minimized) for as long as needed; the log can be saved as a text report.
    /// </summary>
    public sealed class UsbMonitorWindow : Window
    {
        private static UsbMonitorWindow? _open;

        /// <summary>Opens the monitor, or brings the one already open to the front.</summary>
        public static void ShowSingle(Window? owner)
        {
            if (_open != null)
            {
                if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
                _open.Activate();
                return;
            }
            _open = new UsbMonitorWindow();
            if (owner != null && owner.IsVisible) _open.Owner = owner;
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }

        private readonly ObservableCollection<UsbDeviceState> _devices = new();
        private readonly ObservableCollection<UsbEvent> _events = new();
        private readonly Dictionary<string, UsbDeviceState> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DateTime _startedAt = DateTime.Now;
        private bool _scanning, _scanAgain, _firstScanDone;

        private TextBlock _txtStatus = null!, _txtAlert = null!;
        private Border _alertBand = null!;
        private TextBlock _txtSuspend = null!, _txtPowerOff = null!, _txtReconnects = null!;
        private Button _btnFixSuspend = null!, _btnFixPowerOff = null!;
        private CheckBox _chkSound = null!;

        public UsbMonitorWindow()
        {
            NetWindowHelper.SetupWindow(this, "USB Monitor — SMDWin", 1100, 680);
            Content = NetWindowHelper.WindowShell(BuildUI());
            _debounce.Tick += async (_, _) => { _debounce.Stop(); await ScanAsync(); };
            _poll.Tick += async (_, _) => await ScanAsync();
            Loaded += async (_, _) =>
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                src?.AddHook(WndProc);
                await ScanAsync();
                _poll.Start();
                _ = RefreshChecksAsync();
                _ = Task.Run(UsbMonitorService.ReadStartsSinceBoot).ContinueWith(t => Dispatcher.Invoke(() => ApplyStarts(t.Result)),
                        TaskContinuationOptions.OnlyOnRanToCompletion);
            };
            Closed += (_, _) => { _poll.Stop(); _debounce.Stop(); };
        }

        // WM_DEVICECHANGE: DBT_DEVICEARRIVAL (0x8000), DBT_DEVICEREMOVECOMPLETE (0x8004), DBT_DEVNODES_CHANGED (0x0007)
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0219)
            {
                int ev = wParam.ToInt32();
                if (ev == 0x0007 || ev == 0x8000 || ev == 0x8004) { _debounce.Stop(); _debounce.Start(); }
            }
            return IntPtr.Zero;
        }

        // ══ UI ═════════════════════════════════════════════════════════════════
        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // alert band
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // checks
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status

            var hdr = NetWindowHelper.ToolHeader("USB Monitor",
                "Watches USB devices connect and drop out — leave it open while the problem happens", out var actions);
            _chkSound = new CheckBox { Content = "Sound on disconnect", IsChecked = true, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0), Foreground = NetWindowHelper.Res("TextSecondaryBrush") };
            actions.Children.Add(_chkSound);
            var btnSave = NetWindowHelper.SecondaryButton("Save log", "Saves the devices and the log as a text report");
            btnSave.Margin = new Thickness(0, 0, 8, 0);
            btnSave.Click += (_, _) => SaveLog();
            actions.Children.Add(btnSave);
            var btnClear = NetWindowHelper.SecondaryButton("Clear", "Clears the log and the drop counters");
            btnClear.Margin = new Thickness(0, 0, 8, 0);
            btnClear.Click += (_, _) => { _events.Clear(); foreach (var d in _devices) d.Drops = 0; RefreshGrid(); HideAlert(); };
            actions.Children.Add(btnClear);
            NetWindowHelper.AddCloseButton(this, actions);
            NetWindowHelper.MakeDraggable(hdr, this);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);

            // the last disconnection, in red, until cleared
            _txtAlert = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = NetWindowHelper.Res("StatusErrorBrush"),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _alertBand = new Border { Child = _txtAlert, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
                BorderBrush = NetWindowHelper.Res("StatusErrorBrush"),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xEF, 0x44, 0x44)) };
            Grid.SetRow(_alertBand, 1); root.Children.Add(_alertBand);

            // three checks
            var checks = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
            checks.Children.Add(CheckCard("USB power saving",
                "Windows may switch off idle USB ports (selective suspend) — the most common cause of a mouse that drops out now and then.",
                out _txtSuspend, out _btnFixSuspend, "Turn off"));
            _btnFixSuspend.Click += async (_, _) =>
            {
                _btnFixSuspend.IsEnabled = false;
                await UsbMonitorService.DisableSelectiveSuspendAsync();
                await RefreshChecksAsync();
            };
            checks.Children.Add(CheckCard("Devices Windows may turn off",
                "\"Allow the computer to turn off this device to save power\" in Device Manager, on USB devices and hubs.",
                out _txtPowerOff, out _btnFixPowerOff, "Don't allow"));
            _btnFixPowerOff.Click += async (_, _) =>
            {
                _btnFixPowerOff.IsEnabled = false;
                await Task.Run(UsbMonitorService.DisableDevicePowerOff);
                await RefreshChecksAsync();
            };
            checks.Children.Add(CheckCard("Reconnections since Windows started",
                "From the Windows event log: devices started more than once since boot were unplugged or dropped out.",
                out _txtReconnects, out var btnNone, ""));
            btnNone.Visibility = Visibility.Collapsed;
            Grid.SetRow(checks, 2); root.Children.Add(checks);

            // devices | log
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var grid = NetWindowHelper.StandardDataGrid();
            grid.ItemsSource = _devices;
            var rowStyle = NetWindowHelper.DefaultRowStyle();
            var problem = new DataTrigger { Binding = new Binding(nameof(UsbDeviceState.IsProblem)), Value = true };
            problem.Setters.Add(new Setter(Control.ForegroundProperty, NetWindowHelper.Res("StatusErrorBrush")));
            rowStyle.Triggers.Add(problem);
            grid.RowStyle = rowStyle;
            grid.Columns.Add(TextCol("Device", nameof(UsbDeviceState.Name), new DataGridLength(1, DataGridLengthUnitType.Star), tooltip: nameof(UsbDeviceState.DeviceId)));
            grid.Columns.Add(TextCol("ID", nameof(UsbDeviceState.VidPid), new DataGridLength(90)));
            grid.Columns.Add(TextCol("State", nameof(UsbDeviceState.StatusText), new DataGridLength(100)));
            grid.Columns.Add(TextCol("Drops now", nameof(UsbDeviceState.DropsText), new DataGridLength(90)));
            grid.Columns.Add(TextCol("Since boot", nameof(UsbDeviceState.ReconnectsText), new DataGridLength(90)));
            grid.Columns.Add(TextCol("Last change", nameof(UsbDeviceState.LastChangeText), new DataGridLength(100)));
            var devCard = NetWindowHelper.CardWithHead("Devices", "USB devices", grid);
            ToolTipService.SetToolTip(devCard, "Drops now: disconnections seen while this window is open · Since boot: reconnections recorded by Windows since it started");
            Grid.SetColumn(devCard, 0); body.Children.Add(devCard);

            var log = NetWindowHelper.StandardDataGrid();
            log.ItemsSource = _events;
            log.Columns.Add(TextCol("Time", nameof(UsbEvent.TimeText), new DataGridLength(80)));
            log.Columns.Add(TextCol("", nameof(UsbEvent.Arrow), new DataGridLength(28)));
            log.Columns.Add(TextCol("Device", nameof(UsbEvent.Name), new DataGridLength(1, DataGridLengthUnitType.Star), tooltip: nameof(UsbEvent.Detail)));
            log.Columns.Add(TextCol("Event", nameof(UsbEvent.KindText), new DataGridLength(100)));
            var logCard = NetWindowHelper.CardWithHead("Log", "Connected / disconnected", log);
            Grid.SetColumn(logCard, 2); body.Children.Add(logCard);
            Grid.SetRow(body, 3); root.Children.Add(body);

            _txtStatus = NetWindowHelper.StatusText("Reading the USB devices…");
            _txtStatus.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(_txtStatus, 4); root.Children.Add(_txtStatus);
            return root;
        }

        private static DataGridTextColumn TextCol(string header, string path, DataGridLength width, string? tooltip = null)
        {
            var col = new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
            col.ElementStyle = new Style(typeof(TextBlock));
            col.ElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            col.ElementStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            if (tooltip != null) col.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(tooltip)));
            return col;
        }

        private static Border CheckCard(string title, string desc, out TextBlock value, out Button fix, string fixText)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") });
            sp.Children.Add(new TextBlock { Text = desc, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 8) });
            var row = new DockPanel { LastChildFill = true };
            fix = NetWindowHelper.SecondaryButton(fixText.Length > 0 ? fixText : "—");
            fix.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(fix, Dock.Right);
            row.Children.Add(fix);
            value = new TextBlock { Text = "…", FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") };
            row.Children.Add(value);
            sp.Children.Add(row);
            var card = NetWindowHelper.ContentCard(sp);
            card.Margin = new Thickness(0, 0, 10, 0);
            card.Padding = new Thickness(14, 12, 14, 12);
            return card;
        }

        // ══ Data ═══════════════════════════════════════════════════════════════
        private async Task ScanAsync()
        {
            if (_scanning) { _scanAgain = true; return; }
            _scanning = true;
            try
            {
                do
                {
                    _scanAgain = false;
                    var present = await Task.Run(UsbMonitorService.ReadPresent);
                    ApplyScan(present);
                } while (_scanAgain);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UsbMonitorWindow.ScanAsync"); }
            finally { _scanning = false; }
        }

        private void ApplyScan(Dictionary<string, (string Name, string Manufacturer, string VidPid, bool IsHub)> present)
        {
            var now = DateTime.Now;
            bool first = !_firstScanDone;
            _firstScanDone = true;

            // new or back
            foreach (var (id, d) in present)
            {
                if (_byId.TryGetValue(id, out var known))
                {
                    if (!known.Connected)
                    {
                        known.Connected = true;
                        string gap = known.LastChange.HasValue ? $" after {FormatGap(now - known.LastChange.Value)}" : "";
                        known.LastChange = now;
                        _events.Insert(0, new UsbEvent(now, true, known.Name, known.VidPid, $"back{gap} · {id}"));
                    }
                    if (!string.IsNullOrEmpty(d.Name)) known.Name = d.Name;
                }
                else
                {
                    var st = new UsbDeviceState { DeviceId = id, Name = string.IsNullOrEmpty(d.Name) ? id : d.Name,
                        Manufacturer = d.Manufacturer, VidPid = d.VidPid, IsHub = d.IsHub, Connected = true };
                    _byId[id] = st;
                    _devices.Add(st);
                    if (!first)
                    {
                        st.LastChange = now;
                        _events.Insert(0, new UsbEvent(now, true, st.Name, st.VidPid, id));
                    }
                }
            }

            // gone
            var dropped = new List<UsbDeviceState>();
            foreach (var st in _byId.Values)
            {
                if (st.Connected && !present.ContainsKey(st.DeviceId))
                {
                    st.Connected = false;
                    st.Drops++;
                    st.LastChange = now;
                    _events.Insert(0, new UsbEvent(now, false, st.Name, st.VidPid, st.DeviceId));
                    dropped.Add(st);
                }
            }
            if (dropped.Count > 0)
            {
                string names = string.Join(", ", dropped.Select(x => x.Name));
                // several devices at the same moment = a hub or the port's power, not the devices themselves
                string hint = dropped.Count >= 2 ? " — several at once: check the hub, the cable or the port's power" : "";
                ShowAlert($"{now:HH:mm:ss}  Disconnected: {names}{hint}");
                if (_chkSound.IsChecked == true) try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }

            while (_events.Count > 500) _events.RemoveAt(_events.Count - 1);
            RefreshGrid();
            int conn = _devices.Count(x => x.Connected);
            _txtStatus.Text = $"Watching since {_startedAt:HH:mm:ss} · {conn} device(s) connected · {_events.Count(e => !e.Connected)} disconnection(s) seen";
        }

        private void ApplyStarts(Dictionary<string, int> starts)
        {
            foreach (var st in _byId.Values)
                st.StartsSinceBoot = starts.TryGetValue(st.DeviceId, out int n) ? n : 0;
            RefreshGrid();
            var flaky = _byId.Values.Where(x => x.StartsSinceBoot > 1).OrderByDescending(x => x.StartsSinceBoot).ToList();
            if (starts.Count == 0)
                _txtReconnects.Text = "Log not available";
            else if (flaky.Count == 0)
                _txtReconnects.Text = "None";
            else
            {
                var top = flaky[0];
                _txtReconnects.Text = $"{top.Name}: {top.StartsSinceBoot - 1}×" + (flaky.Count > 1 ? $"  (+{flaky.Count - 1} more)" : "");
                _txtReconnects.Foreground = NetWindowHelper.Res(top.StartsSinceBoot >= 4 ? "StatusErrorBrush" : "StatusWarningBrush");
            }
        }

        private async Task RefreshChecksAsync()
        {
            var (ac, dc) = await UsbMonitorService.ReadSelectiveSuspendAsync();
            bool anyOn = ac == true || dc == true;
            _txtSuspend.Text = ac == null ? "Unknown"
                : anyOn ? $"On ({(ac == true ? "mains" : "")}{(ac == true && dc == true ? " + " : "")}{(dc == true ? "battery" : "")})" : "Off";
            _txtSuspend.Foreground = NetWindowHelper.Res(anyOn ? "StatusWarningBrush" : "TextPrimaryBrush");
            _btnFixSuspend.Visibility = anyOn ? Visibility.Visible : Visibility.Collapsed;
            _btnFixSuspend.IsEnabled = true;

            var (allowed, total) = await Task.Run(UsbMonitorService.ReadDevicePowerOff);
            _txtPowerOff.Text = total == 0 ? "Not available" : $"{allowed} of {total}";
            _txtPowerOff.Foreground = NetWindowHelper.Res(allowed > 0 ? "StatusWarningBrush" : "TextPrimaryBrush");
            _btnFixPowerOff.Visibility = allowed > 0 ? Visibility.Visible : Visibility.Collapsed;
            _btnFixPowerOff.IsEnabled = true;
        }

        private void RefreshGrid()
        {
            // the rows are plain objects: re-sort (problems first, then by name) and let the grid redraw them
            var ordered = _devices.OrderByDescending(x => x.IsProblem).ThenBy(x => x.IsHub).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _devices.Clear();
            foreach (var d in ordered) _devices.Add(d);
        }

        private void ShowAlert(string text) { _txtAlert.Text = text; _alertBand.Visibility = Visibility.Visible; }
        private void HideAlert() => _alertBand.Visibility = Visibility.Collapsed;

        private static string FormatGap(TimeSpan t)
            => t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0} s" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:F0} min" : $"{t.TotalHours:F1} h";

        private void SaveLog()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"SMD Win — USB Monitor · {Environment.MachineName} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Watching since {_startedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"USB power saving (selective suspend): {_txtSuspend.Text}");
                sb.AppendLine($"Devices Windows may turn off: {_txtPowerOff.Text}");
                sb.AppendLine($"Reconnections since Windows started: {_txtReconnects.Text}");
                sb.AppendLine();
                sb.AppendLine("DEVICES");
                foreach (var d in _devices)
                    sb.AppendLine($"  {d.Name,-40} {d.VidPid,-10} {d.StatusText,-13} drops now: {d.Drops,-3} since boot: {d.ReconnectsText,-4} {d.DeviceId}");
                sb.AppendLine();
                sb.AppendLine("LOG");
                foreach (var e in _events.Reverse())
                    sb.AppendLine($"  {e.Time:HH:mm:ss}  {e.KindText,-13} {e.Name} ({e.VidPid})  {e.Detail}");

                string dir = SettingsService.GetEffectiveReportFolder();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"USB-Monitor_{DateTime.Now:yyyy-MM-dd_HHmm}.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "UsbMonitorWindow.SaveLog");
                NetWindowHelper.ShowInfo("The log could not be saved: " + ex.Message, "USB Monitor", this);
            }
        }
    }
}
