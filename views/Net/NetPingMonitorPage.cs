
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

// WPF type aliases — resolve ambiguity with System.Drawing / System.Windows.Forms
using Application          = System.Windows.Application;
using Brush                = System.Windows.Media.Brush;
using Brushes              = System.Windows.Media.Brushes;
using Button                = System.Windows.Controls.Button;
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
using WpfColor             = System.Windows.Media.Color;
using WpfPoint             = System.Windows.Point;

namespace SMDWin.Views.Net
{
    public class NetPingMonitorPage : NetToolPage
    {
        private readonly SMDWin.Views.MainWindow _owner;

        private TextBox   _txtHost   = null!;
        private TextBlock _txtLive   = null!;
        private TextBlock _txtUptime = null!;
        private Button    _btnStart  = null!;
        private Button    _btnStop   = null!;
        private Canvas    _chart     = null!;
        private Canvas    _hoverLayer = null!;   // the line and label under the mouse, over the chart (redrawn separately)
        private CheckBox  _chkBackground = null!;
        private CheckBox  _chkSustainedOnly = null!;
        private StackPanel _logPanel = null!;
        private readonly List<Border> _intervalBtns = new();
        private int _windowMinutes = 5;

        private TextBlock _txtMin    = null!;
        private TextBlock _txtAvg    = null!;
        private TextBlock _txtMax    = null!;
        private TextBlock _txtJitter = null!;
        private TextBlock _txtStdDev = null!;

        private int _baselineHistIdx;
        private DateTime _windowOpenedAt = DateTime.MinValue;

        public NetPingMonitorPage()
            : this((SMDWin.Views.MainWindow)System.Windows.Application.Current.MainWindow) { }

        public NetPingMonitorPage(SMDWin.Views.MainWindow owner)
        {
            _owner = owner;
            NetWindowHelper.SetupWindow(this, "Ping Monitor — SMDWin", 760, 680);
            Content = BuildUI();

            _windowMinutes = _owner.PingWindowMinutesShared > 0 ? _owner.PingWindowMinutesShared : 5;
            _txtHost.Text = _owner.PingHostCurrent;
            _chkBackground.IsChecked = _owner.PingBackgroundTrackingOn;
            SetIntervalButtonStyles(_windowMinutes);
            _baselineHistIdx = _owner.PingBackgroundTrackingOn ? 0 : _owner.PingHistIdxSnapshot;
            _windowOpenedAt = _owner.PingBackgroundTrackingOn ? DateTime.MinValue : DateTime.Now;
            RefreshRunningState();
            RedrawAll();

            _owner.PingDataUpdated += OnOwnerPingDataUpdated;
            Closed += (_, _) => _owner.PingDataUpdated -= OnOwnerPingDataUpdated;
        }

        private void OnOwnerPingDataUpdated() => Dispatcher.InvokeAsync(RedrawAll);

        private void RefreshRunningState()
        {
            bool running = _owner.PingIsRunning;
            // Start always stays active (see the comment in PingStart_Click) — pressing it while the ping already runs RESTARTS it
            // with the current host from the text box, it is not a no-op. Only Stop reflects the real state (active only when it
            // actually runs).
            _btnStart.IsEnabled = true;
            _btnStop.IsEnabled  = running;
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0: antet
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1: time window + options
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2: figures
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 3: grafic
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4: jurnal

            // ── The standard header: title, host, Start / Stop ──
            var hdrPing = NetWindowHelper.ToolHeader(
                SMDWin.Services.LanguageService.S("NetTools", "PingMonitorTitle", "Ping Monitor"),
                SMDWin.Services.LanguageService.S("NetTools", "PingMonitorDesc", "Real-time latency chart with packet loss tracking"),
                out var pingActions);
            pingActions.Children.Add(NetWindowHelper.HeaderLabel(SMDWin.Services.LanguageService.S("NetTools", "PingHostLabel", "Host:")));
            _txtHost = NetWindowHelper.HeaderBox("8.8.8.8", 140,
                SMDWin.Services.LanguageService.S("NetTools", "PingHostTooltip", "Ping target — 8.8.8.8 by default, or e.g. your router's local IP."));
            pingActions.Children.Add(_txtHost);
            // back to the default target (after a PC on the network was pinged)
            var btnDefault = NetWindowHelper.SecondaryButton("↺ 8.8.8.8",
                SMDWin.Services.LanguageService.S("NetTools", "PingHostDefaultTip", "Back to the default target (8.8.8.8)"));
            btnDefault.Margin = new Thickness(6, 0, 0, 0);
            btnDefault.Click += (_, _) =>
            {
                _txtHost.Text = "8.8.8.8";
                if (_owner.PingIsRunning && !string.Equals(_owner.PingHostCurrent, "8.8.8.8", StringComparison.OrdinalIgnoreCase))
                    PingStart_Click(this, new RoutedEventArgs());
            };
            pingActions.Children.Add(btnDefault);
            _btnStart = NetWindowHelper.PrimaryButton("▶ Start", "Begin continuous ping to the specified host");
            _btnStop  = NetWindowHelper.DangerButton("■ Stop", "Stop the current ping session");
            _btnStop.IsEnabled = false;
            _btnStart.Click += PingStart_Click;
            _btnStop.Click  += PingStop_Click;
            pingActions.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnStart, _btnStop));
            Grid.SetRow(hdrPing, 0); root.Children.Add(hdrPing);
            NetWindowHelper.MakeDraggable(hdrPing, this);

            // ── The time window (segmented control) and the two options, on the same row ──
            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // borderless grey track (variant B); the chosen interval is a lighter pill
            var segmented = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
            segmented.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
            var pillsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            void AddIntervalBtn(string label, int minutes)
            {
                var tb = new TextBlock { Text = label, FontSize = Ui.FontS, HorizontalAlignment = HorizontalAlignment.Center };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                var b = new Border
                {
                    Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(6), Tag = minutes,
                    Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent, MinWidth = 42, Child = tb,
                };
                b.MouseLeftButtonUp += (_, _) =>
                {
                    _windowMinutes = minutes;
                    _owner.PingWindowMinutesShared = minutes;
                    SetIntervalButtonStyles(minutes);
                    RedrawAll();
                };
                _intervalBtns.Add(b);
                pillsPanel.Children.Add(b);
            }
            AddIntervalBtn("1m", 1);
            AddIntervalBtn("5m", 5);
            AddIntervalBtn("10m", 10);
            AddIntervalBtn("1h", 60);
            AddIntervalBtn("3h", 180);
            segmented.Child = pillsPanel;
            Grid.SetColumn(segmented, 0); toolbar.Children.Add(segmented);

            var options = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _chkBackground = new CheckBox
            { Style = (Style)System.Windows.Application.Current.Resources["VanillaCheckBoxStyle"],
                Content = new TextBlock { Text = SMDWin.Services.LanguageService.S("NetTools", "PingTrackBackgroundLabel", "Track in background (even when this window is closed)"), TextWrapping = TextWrapping.Wrap },
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 2, 0, 2),
                ToolTip = SMDWin.Services.LanguageService.S("NetTools", "PingTrackBackgroundTooltip", "Keeps recording outages even if you close this window or minimize the app. Off by default."),
            };
            _chkBackground.Checked   += (_, _) => _owner.SetPingBackgroundTrackingExternal(true);
            _chkBackground.Unchecked += (_, _) => _owner.SetPingBackgroundTrackingExternal(false);
            options.Children.Add(_chkBackground);

            _chkSustainedOnly = new CheckBox
            { Style = (Style)System.Windows.Application.Current.Resources["VanillaCheckBoxStyle"],
                Content = new TextBlock { Text = SMDWin.Services.LanguageService.S("NetTools", "PingSustainedOnlyLabel", "Uptime % counts only sustained outages"), TextWrapping = TextWrapping.Wrap },
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 2, 0, 2),
                IsChecked = SettingsService.Current.PingUptimeSustainedOnly,
                ToolTip = SMDWin.Services.LanguageService.S("NetTools", "PingSustainedOnlyTooltip", "Off: every lost ping counts. On: only 3+ losses in a row count — matches the log below exactly."),
            };
            _chkSustainedOnly.Checked   += (_, _) => { SettingsService.Current.PingUptimeSustainedOnly = true;  SettingsService.Save(); RedrawAll(); };
            _chkSustainedOnly.Unchecked += (_, _) => { SettingsService.Current.PingUptimeSustainedOnly = false; SettingsService.Save(); RedrawAll(); };
            options.Children.Add(_chkSustainedOnly);
            Grid.SetColumn(options, 1); toolbar.Children.Add(options);
            Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

            // ── The figures: one tile for each, the current ping coloured by state ──
            var stats = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
            Border StatTile(string label, out TextBlock field, bool live = false)
            {
                var sp = new StackPanel();
                var cap = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
                cap.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                var val = new TextBlock { Text = "—", FontSize = live ? Ui.FontTitleLg : Ui.FontTitle, FontWeight = FontWeights.SemiBold };
                val.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                if (Application.Current.Resources["NumFont"] is FontFamily nf) val.FontFamily = nf;
                sp.Children.Add(cap); sp.Children.Add(val);
                field = val;
                var card = NetWindowHelper.ContentCard(sp);
                card.Padding = new Thickness(14, 10, 14, 10);
                card.Margin = new Thickness(0, 0, 8, 0);
                if (live) card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                return card;
            }
            stats.Children.Add(StatTile(SMDWin.Services.LanguageService.S("NetTools", "PingLiveLabel", "PING (ms)"), out _txtLive, live: true));
            stats.Children.Add(StatTile("MIN (ms)",    out _txtMin));
            stats.Children.Add(StatTile("AVG (ms)",    out _txtAvg));
            stats.Children.Add(StatTile("MAX (ms)",    out _txtMax));
            _txtMax.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningBrush");   // only MAX keeps the attention colour
            stats.Children.Add(StatTile("JITTER (ms)", out _txtJitter));
            stats.Children.Add(StatTile("STD DEV",     out _txtStdDev));
            stats.Children.Add(StatTile(SMDWin.Services.LanguageService.S("NetTools", "PingUptimeLabel", "UPTIME"), out _txtUptime));
            Grid.SetRow(stats, 2); root.Children.Add(stats);

            // ── The chart ──
            var chartBorder = NetWindowHelper.ContentCard(null!);
            var chartGrid = new Grid();
            _chart = new Canvas { Background = System.Windows.Media.Brushes.Transparent };
            chartGrid.SizeChanged += (_, e) =>
            {
                _chart.Width  = e.NewSize.Width;
                _chart.Height = e.NewSize.Height;
                DrawPingChart();
            };
            chartGrid.Children.Add(_chart);
            _hoverLayer = new Canvas { IsHitTestVisible = false };
            chartGrid.Children.Add(_hoverLayer);
            _chart.MouseMove  += (_, e) => { _hoverX = e.GetPosition(_chart).X; DrawHover(); };
            _chart.MouseLeave += (_, _) => { _hoverX = double.NaN; DrawHover(); };
            var withHead = new Grid();
            withHead.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            withHead.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            withHead.Children.Add(NetWindowHelper.CardHead(SMDWin.Views.MainWindow.PT("Latency"), SMDWin.Views.MainWindow.PT("Round-trip time of every ping"), "M3 12h4l3-8 4 16 3-8h4"));
            Grid.SetRow(chartGrid, 1); withHead.Children.Add(chartGrid);
            chartBorder.Child = withHead;
            Grid.SetRow(chartBorder, 3); root.Children.Add(chartBorder);

            // ── The episode log ──
            var logBorder = NetWindowHelper.ContentCard(null!);
            logBorder.Padding = new Thickness(12, 8, 12, 8);
            logBorder.Margin = new Thickness(0, 10, 0, 0);
            logBorder.MaxHeight = 140;
            var logScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _logPanel = new StackPanel();
            logScroll.Content = _logPanel;
            logBorder.Child = logScroll;
            Grid.SetRow(logBorder, 4); root.Children.Add(logBorder);

            return root;
        }

        private void SetIntervalButtonStyles(int minutes)
        {
            var res = System.Windows.Application.Current.Resources;
            foreach (var b in _intervalBtns)
            {
                bool active = b.Tag is int m && m == minutes;
                if (active)
                {
                    b.Background = res["SelBtnBg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
                    b.BorderBrush = res["SelBtnBorderBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
                    b.BorderThickness = res["SelBtnBorderThickness"] as Thickness? ?? new Thickness(1);
                    b.Effect = null;   // no shadow on a button with text: it blurs the text
                }
                else
                {
                    b.Background = System.Windows.Media.Brushes.Transparent;
                    b.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    b.BorderThickness = new Thickness(1);
                    b.ClearValue(Border.EffectProperty);
                }
                if (b.Child is TextBlock tb)
                {
                    if (active) tb.Foreground = res["SelBtnFg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
                    else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
        }

        public override bool SelfTestStart() { PingStart_Click(this, new RoutedEventArgs()); return true; }
        public override void SelfTestStop() => PingStop_Click(this, new RoutedEventArgs());

        private void PingStart_Click(object s, RoutedEventArgs e)
        {
            string host = _txtHost.Text.Trim();
            if (string.IsNullOrEmpty(host)) host = "8.8.8.8";
            // same host and already running: the history stays (Start does not restart it from zero)
            if (_owner.PingIsRunning && string.Equals(host, _owner.PingHostCurrent, StringComparison.OrdinalIgnoreCase))
            {
                RefreshRunningState();
                return;
            }
            _owner.StartAutoPingExternal(host);
            _baselineHistIdx = _owner.PingHistIdxSnapshot;
            _windowOpenedAt = DateTime.Now;
            RefreshRunningState();
        }

        public void SetTargetAndStart(string target)
        {
            _txtHost.Text = target;
            PingStart_Click(this, new RoutedEventArgs());
        }

        private void PingStop_Click(object s, RoutedEventArgs e)
        {
            _owner.StopAutoPingExternal();
            RefreshRunningState();
        }

        private void RedrawAll()
        {
            RefreshRunningState();

            var hist = _owner.PingHistoryRaw;
            int histIdx = _owner.PingHistIdxSnapshot;
            if (histIdx > _baselineHistIdx)
            {
                float last = hist[(histIdx - 1 + hist.Length) % hist.Length];
                _txtLive.Text = float.IsNaN(last) ? "Timeout" : $"{last:F0}";
                _txtLive.SetResourceReference(TextBlock.ForegroundProperty,
                      float.IsNaN(last) ? "StatusCritBrush" : last < 30 ? "StatusOkBrush" : last < 100 ? "StatusWarnBrush" : "StatusCritBrush");
            }
            else
            {
                _txtLive.Text = "—";
                _txtLive.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }

            // Uptime for the currently selected window — relative to the baseline (see the _baseline* fields above), not to the
            // whole shared buffer.
            int windowSamples = Math.Min(_windowMinutes * 60, hist.Length);
            int count = Math.Min(Math.Max(0, histIdx - _baselineHistIdx), hist.Length);
            int visibleCount = Math.Min(count, windowSamples);
            int lostInWindow = 0; // total individual packets lost — also used for the log message
            if (visibleCount > 0)
            {
                int start = histIdx >= hist.Length
                    ? (histIdx - visibleCount + hist.Length) % hist.Length
                    : Math.Max(0, histIdx - visibleCount);

                bool sustainedOnly = SettingsService.Current.PingUptimeSustainedOnly;
                int downInWindow = 0;
                int runLen = 0;
                for (int i = 0; i <= visibleCount; i++)
                {
                    bool isLost = i < visibleCount && float.IsNaN(hist[(start + i) % hist.Length]);
                    if (isLost)
                    {
                        runLen++;
                        lostInWindow++;
                        if (!sustainedOnly) downInWindow++;
                    }
                    else
                    {
                        // Reached the end of a run (or the end of the window) — if we were in "sustained only" mode and the run reached the
                        // threshold, count ALL the packets in that run now (not only those after the threshold).
                        if (sustainedOnly && runLen >= SMDWin.Views.MainWindow.PingLossThreshold)
                            downInWindow += runLen;
                        runLen = 0;
                    }
                }

                double uptimePct = 100.0 * (visibleCount - downInWindow) / visibleCount;
                _txtUptime.Text = $"{uptimePct:F1}%";
                _txtUptime.SetResourceReference(TextBlock.ForegroundProperty,
                    uptimePct >= 99.5 ? "StatusOkBrush" : uptimePct >= 95 ? "StatusWarnBrush" : "StatusCritBrush");
            }
            else { _txtUptime.Text = "—"; _txtUptime.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); }

            // The episode log — only those AFTER the window was opened (temporal baseline), newest first, at most 20.
            _logPanel.Children.Clear();
            var events = _owner.PingTimeoutEventsSnapshot.Where(ev => ev.Start >= _windowOpenedAt).ToList();
            if (events.Count == 0)
            {
                string noTimeoutMsg = SettingsService.Current.PingUptimeSustainedOnly
                    ? "No timeouts recorded this session."
                    : (lostInWindow > 0
                        ? string.Format(
                            "No sustained outages ({0}+ in a row). {1} isolated packet(s) lost this session.",
                            SMDWin.Views.MainWindow.PingLossThreshold, lostInWindow)
                        : "No timeouts recorded this session.");
                _logPanel.Children.Add(new TextBlock
                {
                    Text = noTimeoutMsg, FontSize = Ui.FontS, Opacity = 0.6,
                    Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                foreach (var ev in events.AsEnumerable().Reverse().Take(20))
                {
                    // one row per episode: a coloured dot (red = in progress), the interval, the duration and the diagnosis
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ev.End.HasValue ? "StatusWarnBrush" : "StatusCritBrush");
                    row.Children.Add(dot);
                    string range = ev.End.HasValue ? $"{ev.Start:HH:mm:ss} → {ev.End.Value:HH:mm:ss}" : $"{ev.Start:HH:mm:ss} → now";
                    var rangeText = new TextBlock { Text = range, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    rangeText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    if (Application.Current.Resources["NumFont"] is FontFamily numFont) rangeText.FontFamily = numFont;
                    Grid.SetColumn(rangeText, 1); row.Children.Add(rangeText);
                    string duration = ev.End.HasValue ? $"{(ev.End.Value - ev.Start).TotalSeconds:F0}s down" : "ongoing";
                    var durationText = new TextBlock { Text = duration, FontSize = Ui.FontS, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    durationText.SetResourceReference(TextBlock.ForegroundProperty, ev.End.HasValue ? "TextSecondaryBrush" : "StatusCritBrush");
                    Grid.SetColumn(durationText, 2); row.Children.Add(durationText);
                    if (!string.IsNullOrEmpty(ev.Diagnosis))
                    {
                        var why = new TextBlock { Text = ev.Diagnosis, FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = ev.Diagnosis };
                        why.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        Grid.SetColumn(why, 3); row.Children.Add(why);
                    }
                    _logPanel.Children.Add(row);
                }
            }

            UpdateStabilityStats();
            DrawPingChart();
        }

        private void UpdateStabilityStats()
        {
            var hist = _owner.PingHistoryRaw;
            int histIdx = _owner.PingHistIdxSnapshot;
            int windowSamples = Math.Min(_windowMinutes * 60, hist.Length);
            int count = Math.Min(Math.Max(0, histIdx - _baselineHistIdx), hist.Length);
            int visibleCount = Math.Min(count, windowSamples);

            if (visibleCount < 1)
            {
                _txtMin.Text = _txtAvg.Text = _txtMax.Text = _txtJitter.Text = _txtStdDev.Text = "—";
                return;
            }

            int start = histIdx >= hist.Length
                ? (histIdx - visibleCount + hist.Length) % hist.Length
                : Math.Max(0, histIdx - visibleCount);

            var samples = new List<double>();
            for (int i = 0; i < visibleCount; i++)
            {
                float v = hist[(start + i) % hist.Length];
                if (!float.IsNaN(v)) samples.Add(v);
            }

            if (samples.Count == 0)
            {
                _txtMin.Text = _txtAvg.Text = _txtMax.Text = _txtJitter.Text = _txtStdDev.Text = "—";
                return;
            }

            double min = samples.Min();
            double max = samples.Max();
            double avg = samples.Average();

            // Jitter = the average of the absolute differences between consecutive samples
            double jitter = 0;
            if (samples.Count > 1)
            {
                double sumDiff = 0;
                for (int i = 1; i < samples.Count; i++)
                    sumDiff += Math.Abs(samples[i] - samples[i - 1]);
                jitter = sumDiff / (samples.Count - 1);
            }

            double variance = samples.Average(v => Math.Pow(v - avg, 2));
            double stdDev = Math.Sqrt(variance);

            _txtMin.Text    = $"{min:F0}";
            _txtAvg.Text    = $"{avg:F1}";
            _txtMax.Text    = $"{max:F0}";
            _txtJitter.Text = $"{jitter:F1}";
            _txtStdDev.Text = $"{stdDev:F1}";
        }

        /// <summary>A chart point: the average of several consecutive measurements (at 1h / 3h there are thousands of
        /// measurements, and the chart has a few hundred pixels — drawing each measurement separately blocked the window).</summary>
        private readonly struct PingBucket
        {
            public readonly double X; public readonly float Avg, Max; public readonly int Count, Lost; public readonly long T0, T1;
            public PingBucket(double x, float avg, float max, int count, int lost, long t0, long t1)
            { X = x; Avg = avg; Max = max; Count = count; Lost = lost; T0 = t0; T1 = t1; }
        }
        private readonly List<PingBucket> _buckets = new();
        private double _hoverX = double.NaN;
        private double _plotTop, _plotBottom, _plotMax;

        private void DrawPingChart()
        {
            double w = _chart.ActualWidth, h = _chart.ActualHeight;
            _buckets.Clear();
            if (w < 30 || h < 20) { DrawHover(); return; }

            _chart.Children.Clear();

            var hist  = _owner.PingHistoryRaw;
            var ticks = _owner.PingTimeTicksRaw;
            int histIdx = _owner.PingHistIdxSnapshot;

            int windowSamples = Math.Min(_windowMinutes * 60, hist.Length);
            int count = Math.Min(Math.Max(0, histIdx - _baselineHistIdx), hist.Length);
            int visibleCount = Math.Min(count, windowSamples);
            int start = histIdx >= hist.Length
                ? (histIdx - visibleCount + hist.Length) % hist.Length
                : Math.Max(0, histIdx - visibleCount);

            const float PingCeiling = 300f; // fixed cap — any peak above this is clamped, it does not rescale the chart
            float maxPing = 100f;
            for (int i = 0; i < visibleCount; i++)
            {
                float v = hist[(start + i) % hist.Length];
                if (!float.IsNaN(v) && v > maxPing) maxPing = v;
            }
            maxPing = Math.Min(maxPing * 1.15f, PingCeiling);

            const double pL = 40, pR = 10, pT = 10, pB = 24;
            double cW = w - pL - pR, cH = h - pT - pB;
            _plotTop = pT; _plotBottom = pT + cH; _plotMax = maxPing;

            foreach (int ms in new[] { 0, 50, 100, 200, 300 })
            {
                if (ms > maxPing) continue;
                double y = pT + cH - ms / maxPing * cH;
                var grid = new Line { X1 = pL, X2 = pL + cW, Y1 = y, Y2 = y, StrokeThickness = 1, Opacity = 0.6,
                    StrokeDashArray = ms == 0 ? null : new System.Windows.Media.DoubleCollection { 3, 4 } };
                grid.SetResourceReference(Shape.StrokeProperty, "CardBorderBrush");
                _chart.Children.Add(grid);
                var lbl = new TextBlock { Text = $"{ms} ms", FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Opacity = 0.8 };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, y - 11); _chart.Children.Add(lbl);
            }

            if (visibleCount < 2) { DrawHover(); return; }

            // ── grouped measurements: at most one point per 2 px ──
            int nb = Math.Max(1, Math.Min(visibleCount, (int)(cW / 2)));
            double SlotX(double slot) => pL + cW * (windowSamples - visibleCount + slot) / (windowSamples - 1.0);
            for (int b = 0; b < nb; b++)
            {
                int i0 = (int)((long)b * visibleCount / nb), i1 = (int)((long)(b + 1) * visibleCount / nb);
                if (i1 <= i0) continue;
                double sum = 0; float mx = 0; int n = 0, lost = 0; long t0 = 0, t1 = 0;
                for (int i = i0; i < i1; i++)
                {
                    int k = (start + i) % hist.Length;
                    long t = ticks[k];
                    if (t != 0) { if (t0 == 0) t0 = t; t1 = t; }
                    float v = hist[k];
                    if (float.IsNaN(v)) { lost++; continue; }
                    sum += v; n++; if (v > mx) mx = v;
                }
                _buckets.Add(new PingBucket(SlotX((i0 + i1 - 1) / 2.0), n > 0 ? (float)(sum / n) : float.NaN, mx, n, lost, t0, t1));
            }

            bool isLight = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.SettingsService.Current.ThemeName);
            var lineColor = (Application.Current.Resources["DataBrush"] as SolidColorBrush)?.Color
                            ?? (isLight ? WpfColor.FromRgb(37, 99, 235) : WpfColor.FromRgb(96, 175, 255));
            var areaFill = new LinearGradientBrush(
                WpfColor.FromArgb(isLight ? (byte)40 : (byte)50, lineColor.R, lineColor.G, lineColor.B),
                WpfColor.FromArgb((byte)5, lineColor.R, lineColor.G, lineColor.B),
                new WpfPoint(0, 0), new WpfPoint(0, 1));
            areaFill.Freeze();
            double Y(float v) => pT + cH - Math.Min(v, maxPing) / maxPing * cH;

            // the line breaks where nothing answered; under it, a red mark for the lost packets
            var lossBrush = Application.Current.Resources["StatusCritBrush"] as Brush ?? Brushes.IndianRed;
            var run = new List<int>();
            void FlushRun()
            {
                if (run.Count >= 2)
                {
                    // the smooth curve (the same as in the other charts, with "Smooth charts"): it does not go above / below the real values
                    var p = run.Select(r => new WpfPoint(_buckets[r].X, Y(_buckets[r].Avg))).ToList();
                    bool smooth = MiniLineChart.SmoothLines;
                    var m = smooth ? MiniLineChart.MonotoneSlopes(p) : null;
                    void Seg(StreamGeometryContext g, int q)
                    {
                        if (m == null) { g.LineTo(p[q + 1], true, true); return; }
                        double dx = (p[q + 1].X - p[q].X) / 3;
                        g.BezierTo(new WpfPoint(p[q].X + dx, p[q].Y + m[q] * dx),
                                   new WpfPoint(p[q + 1].X - dx, p[q + 1].Y - m[q + 1] * dx), p[q + 1], true, true);
                    }
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new WpfPoint(p[0].X, pT + cH), true, true);
                        g.LineTo(p[0], true, true);
                        for (int q = 0; q < p.Count - 1; q++) Seg(g, q);
                        g.LineTo(new WpfPoint(p[^1].X, pT + cH), true, true);
                    }
                    area.Freeze();
                    _chart.Children.Add(new System.Windows.Shapes.Path { Data = area, Fill = areaFill });
                    // the line: short pieces of the same colour (the colour by ping) — a single element per colour
                    int s0 = 0;
                    while (s0 < p.Count - 1)
                    {
                        var col = PingColorGradient((_buckets[run[s0]].Avg + _buckets[run[s0 + 1]].Avg) / 2f);
                        int s1 = s0 + 1;
                        while (s1 < p.Count - 1 && PingColorGradient((_buckets[run[s1]].Avg + _buckets[run[s1 + 1]].Avg) / 2f) == col) s1++;
                        var geo = new StreamGeometry();
                        using (var g = geo.Open())
                        {
                            g.BeginFigure(p[s0], false, false);
                            for (int q = s0; q < s1; q++) Seg(g, q);
                        }
                        geo.Freeze();
                        var br = new SolidColorBrush(col); br.Freeze();
                        _chart.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = br, StrokeThickness = 2.5,
                            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
                        s0 = s1;
                    }
                }
                else if (run.Count == 1)
                {
                    var a = _buckets[run[0]];
                    var dotBr = new SolidColorBrush(PingColorGradient(a.Avg)); dotBr.Freeze();
                    var dot = new Ellipse { Width = 4, Height = 4, Fill = dotBr };
                    Canvas.SetLeft(dot, a.X - 2); Canvas.SetTop(dot, Y(a.Avg) - 2); _chart.Children.Add(dot);
                }
                run.Clear();
            }
            for (int b = 0; b < _buckets.Count; b++)
            {
                var bk = _buckets[b];
                if (bk.Lost > 0)
                {
                    var tick = new System.Windows.Shapes.Rectangle { Width = 2, Height = 6, Fill = lossBrush };
                    Canvas.SetLeft(tick, bk.X - 1); Canvas.SetTop(tick, pT + cH - 6); _chart.Children.Add(tick);
                }
                if (bk.Count > 0) run.Add(b); else FlushRun();
            }
            FlushRun();

            // the last point: a filled circle in the state colour; the value is already in the box at the top
            var lastB = _buckets[^1];
            if (lastB.Count > 0)
            {
                var mk = new SolidColorBrush(PingColorGradient(lastB.Avg)); mk.Freeze();
                var marker = new Ellipse { Width = 9, Height = 9, Fill = mk, StrokeThickness = 2 };
                marker.SetResourceReference(Shape.StrokeProperty, "BgCardBrush");
                Canvas.SetLeft(marker, lastB.X - 4.5); Canvas.SetTop(marker, Y(lastB.Avg) - 4.5);
                _chart.Children.Add(marker);
            }

            DrawTimeAxis(pT + cH);
            DrawHover();
        }

        private string TimeFormat => _windowMinutes <= 10 ? "HH:mm:ss" : "HH:mm";

        /// <summary>The time under the chart, at equal distances, only on the part with data.</summary>
        private void DrawTimeAxis(double baseY)
        {
            var withTime = _buckets.Where(b => b.T1 != 0).ToList();
            if (withTime.Count == 0) return;
            double span = withTime[^1].X - withTime[0].X;
            int labels = Math.Clamp((int)(span / 95), 0, 6);
            var picks = new List<PingBucket>();
            if (labels == 0) picks.Add(withTime[^1]);
            else for (int k = 0; k <= labels; k++) picks.Add(withTime[(int)Math.Round((double)k * (withTime.Count - 1) / labels)]);

            foreach (var b in picks)
            {
                var lbl = new TextBlock { Text = new DateTime(b.T1).ToString(TimeFormat), FontSize = Ui.FontXS, Opacity = 0.8 };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                if (Application.Current.Resources["NumFont"] is FontFamily nf) lbl.FontFamily = nf;
                lbl.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = lbl.DesiredSize.Width;
                double x = Math.Clamp(b.X - lw / 2, 0, Math.Max(0, _chart.ActualWidth - lw));
                Canvas.SetLeft(lbl, x); Canvas.SetTop(lbl, baseY + 5);
                _chart.Children.Add(lbl);
            }
        }

        /// <summary>Under the mouse: a vertical line, a dot and a label with the time and the ping (the average / maximum if
        /// there are several measurements).</summary>
        private void DrawHover()
        {
            if (_hoverLayer == null) return;
            _hoverLayer.Children.Clear();
            if (double.IsNaN(_hoverX) || _buckets.Count == 0) return;

            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < _buckets.Count; i++)
            {
                double d = Math.Abs(_buckets[i].X - _hoverX);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (bestD > 20) return;
            var b = _buckets[best];

            var vline = new Line { X1 = b.X, X2 = b.X, Y1 = _plotTop, Y2 = _plotBottom, StrokeThickness = 1, Opacity = 0.7,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 3 } };
            vline.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
            _hoverLayer.Children.Add(vline);

            string fmt = TimeFormat;
            string when = b.T0 == 0 ? "" : (b.T1 - b.T0 >= TimeSpan.TicksPerSecond * 2 && fmt == "HH:mm:ss") || new DateTime(b.T0).ToString(fmt) != new DateTime(b.T1).ToString(fmt)
                ? $"{new DateTime(b.T0).ToString(fmt)} – {new DateTime(b.T1).ToString(fmt)}"
                : new DateTime(b.T1).ToString(fmt);
            string value;
            if (b.Count == 0) value = LanguageService.S("NetTools", "PingHoverNoReply", "No reply");
            else if (b.Count == 1) value = $"{b.Avg:F0} ms";
            else value = string.Format(LanguageService.S("NetTools", "PingHoverAvgMax", "avg {0} ms · max {1} ms"), b.Avg.ToString("F0"), b.Max.ToString("F0"));
            if (b.Count > 0 && b.Lost > 0)
                value += " · " + string.Format(LanguageService.S("NetTools", "PingHoverLost", "{0} lost"), b.Lost);

            if (b.Count > 0)
            {
                double y = _plotBottom - Math.Min(b.Avg, _plotMax) / _plotMax * (_plotBottom - _plotTop);
                var dotBr = new SolidColorBrush(PingColorGradient(b.Avg)); dotBr.Freeze();
                var dot = new Ellipse { Width = 8, Height = 8, Fill = dotBr, StrokeThickness = 2 };
                dot.SetResourceReference(Shape.StrokeProperty, "BgCardBrush");
                Canvas.SetLeft(dot, b.X - 4); Canvas.SetTop(dot, y - 4);
                _hoverLayer.Children.Add(dot);
            }

            var sp = new StackPanel();
            if (when.Length > 0)
            {
                var t1 = new TextBlock { Text = when, FontSize = Ui.FontXS, Opacity = 0.85 };
                t1.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(t1);
            }
            var t2 = new TextBlock { Text = value, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold };
            t2.SetResourceReference(TextBlock.ForegroundProperty, b.Count == 0 ? "StatusCritBrush" : "TextPrimaryBrush");
            sp.Children.Add(t2);
            var tip = new Border { Child = sp, Padding = new Thickness(8, 4, 8, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            tip.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            tip.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            tip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = tip.DesiredSize.Width;
            double tx = b.X + 10 + tw > _chart.ActualWidth ? b.X - 10 - tw : b.X + 10;
            Canvas.SetLeft(tip, Math.Max(0, tx)); Canvas.SetTop(tip, _plotTop + 2);
            _hoverLayer.Children.Add(tip);
        }

        private static readonly (float Ms, byte R, byte G, byte B)[] _pingColorStops =
        {
            (0f,    0x22, 0xC5, 0x5E), // green — excellent
            (30f,   0x22, 0xC5, 0x5E), // green — still good
            (60f,   0xF5, 0x9E, 0x0B), // amber — acceptabil dar vizibil
            (150f,  0xF9, 0x73, 0x16), // orange — slow
            (250f,  0xEF, 0x44, 0x44), // red — problematic
        };

        private static WpfColor PingColorGradient(float ms)
        {
            if (ms <= _pingColorStops[0].Ms) return WpfColor.FromRgb(_pingColorStops[0].R, _pingColorStops[0].G, _pingColorStops[0].B);
            for (int i = 1; i < _pingColorStops.Length; i++)
            {
                var (ms1, r1, g1, b1) = _pingColorStops[i - 1];
                var (ms2, r2, g2, b2) = _pingColorStops[i];
                if (ms <= ms2)
                {
                    float f = ms2 > ms1 ? (ms - ms1) / (ms2 - ms1) : 1f;
                    f = Math.Clamp(f, 0f, 1f);
                    byte r = (byte)(r1 + (r2 - r1) * f);
                    byte g = (byte)(g1 + (g2 - g1) * f);
                    byte b = (byte)(b1 + (b2 - b1) * f);
                    return WpfColor.FromRgb(r, g, b);
                }
            }
            var last = _pingColorStops[^1];
            return WpfColor.FromRgb(last.R, last.G, last.B);
        }
    }
}
