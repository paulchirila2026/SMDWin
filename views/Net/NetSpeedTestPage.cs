using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using ProgressBar         = System.Windows.Controls.ProgressBar;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;
using WpfSize             = System.Windows.Size;

namespace SMDWin.Views.Net
{
    public class NetSpeedTestPage : NetToolPage
    {
        private readonly SpeedTestService _svc = new();
        private CancellationTokenSource? _cts;

        // Animation state
        private double _dlCurrent = 0, _dlTarget = 0;
        private int _phaseNow = 0;
        private double _ulCurrent = 0, _ulTarget = 0;
        private System.Windows.Threading.DispatcherTimer? _animTimer;

        // UI references
        private Button      _btnRun    = null!;
        private Button      _btnStop   = null!;
        private TextBlock   _txtPhase  = null!;
        private Border?     _startCard;
        private TextBlock   _txtPing   = null!;
        private TextBlock   _txtJitter = null!;
        private TextBlock   _txtServer = null!;
        private TextBlock   _txtDlVal  = null!;
        private TextBlock   _txtUlVal  = null!;
        // Segmented gauges. The scale is logarithmic 0–1000 Mbps and extends by itself above 1000 (2.5G, 5G, 10G).
        private SMDWin.Views.SegmentedGauge _dlCanvas = null!;
        private SMDWin.Views.SegmentedGauge _ulCanvas = null!;
        private ProgressBar _progress  = null!;

        // The live chart and the figures under it
        private readonly List<(double T, double Mbps, int Phase)> _samples = new();
        private readonly System.Diagnostics.Stopwatch _testClock = new();
        private DateTime _lastChartDraw = DateTime.MinValue;
        private Canvas _chartCanvas = null!;
        private TextBlock _peakDl = null!, _avgDl = null!, _peakUl = null!, _avgUl = null!, _dataUsed = null!;
        private StackPanel _historyList = null!;
        private readonly List<(DateTime At, double Dl, double Ul, double Ping)> _history = new();


        // Culori
        private static readonly WpfColor DlColor1 = WpfColor.FromRgb(0,   200, 220);
        private static readonly WpfColor DlColor2 = WpfColor.FromRgb(30,  100, 255);
        private static readonly WpfColor UlColor1 = WpfColor.FromRgb(22,  163,  74);

        // The steps for the internet speed test — 0-50-100-300-500-750-1000 Mbps.
        // Pseudo-logarithmic: equal distances on the dial but useful values (typical Wi-Fi 50-300, fibre 500-1000). Above
        // 1000 Mbps is rare — the needle reaches the end.

        public NetSpeedTestPage()
        {
            NetWindowHelper.SetupWindow(this, "Internet Speed Test — SMDWin", 680, 580);
            Content = BuildUI();
            Loaded += (_, _) =>
            {
                DrawDial(_dlCanvas, 0, DlColor2);
                DrawDial(_ulCanvas, 0, UlColor1);
                _txtServer.Text = "speed.cloudflare.com";
                // Redraw on resize (the canvas may be smaller than CX/CY)
                _dlCanvas.SizeChanged += (_, _) => DrawDial(_dlCanvas, _dlCurrent, DlColor2);
                _ulCanvas.SizeChanged += (_, _) => DrawDial(_ulCanvas, _ulCurrent, UlColor1);
            };
            Closed += (_, _) => { _cts?.Cancel(); _animTimer?.Stop(); _svc.Dispose(); };
        }

        // ── Core dial draw ─────────────────────────────────────────────────────

        // Returns the point on the circle for a given angle (0° is at 12 o'clock)
        private static WpfPoint P(double r, double angleDeg, double cx, double cy)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new WpfPoint(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
        }

        /// <summary>Updates the segmented gauge (the colour is fixed at construction).</summary>
        private static void DrawDial(SMDWin.Views.SegmentedGauge g, double mbps, WpfColor _) => g.Value = mbps;


        // ── UI ────────────────────────────────────────────────────────────────
        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0: antet
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1: ping / jitter / server
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2: the dials
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 3: the live chart
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4: progres
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 5: starea

            // The standard header: title on the left, Start / Stop on the right
            var hdr = NetWindowHelper.ToolHeader("Internet Speed Test", "Download · Upload · Ping · Jitter", out var hdrBtns);
            _btnRun = NetWindowHelper.PrimaryButton("▶ Start Test", "Run internet speed test — measures download, upload, ping and jitter");
            _btnRun.Click += RunTest_Click;
            _btnStop = NetWindowHelper.DangerButton("■ Stop");
            _btnStop.IsEnabled = false;
            _btnStop.ToolTip = LanguageService.S("Network", "SpeedTestStopTooltip");
            _btnStop.Click += (_, _) =>
            {
                _cts?.Cancel();
                _animTimer?.Stop();
                _btnStop.IsEnabled = false;
                _btnRun.IsEnabled  = true;
                if (_txtPhase != null) _txtPhase.Text = LanguageService.S("Network", "CancelledDot");
            };
            hdrBtns.Children.Add(NetWindowHelper.MakeStartStopToggle(_btnRun, _btnStop));
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);
            NetWindowHelper.MakeDraggable(hdr, this);

            // Info bar
            var infoCard = new Border { Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(0,10,0,10),
                Margin = new Thickness(0,0,0,10) };
            var infoRow = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center };
            _txtPing   = MakeInfoTile(infoRow, "PING",   "—", "ms");
            MakeInfoSep(infoRow);
            _txtJitter = MakeInfoTile(infoRow, "JITTER", "—", "ms");
            MakeInfoSep(infoRow);
            _txtServer = MakeInfoTile(infoRow, "SERVER", "—", "");
            infoCard.Child = infoRow;
            Grid.SetRow(infoCard, 1); root.Children.Add(infoCard);

            // Dials card
            var dialsCard = new Border { Background = NetWindowHelper.Res("BgCardBrush"),
                CornerRadius = new CornerRadius(NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(24,16,24,18),
                Margin = new Thickness(0,0,0,10) };
            var dialsGrid = new Grid();
            dialsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            dialsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            dialsGrid.ColumnDefinitions.Add(new ColumnDefinition());

            var (dlPanel, dlVal) = BuildDialPanel("↓  DOWNLOAD", DlColor2, out _dlCanvas);
            var (ulPanel, ulVal) = BuildDialPanel("↑  UPLOAD",   UlColor1, out _ulCanvas);
            _txtDlVal = dlVal; _txtUlVal = ulVal;

            var div = new Border { Width = 1,
                Background = new SolidColorBrush(WpfColor.FromArgb(40,180,180,200)),
                Margin = new Thickness(0,16,0,16), VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetColumn(div, 1); dialsGrid.Children.Add(div);
            Grid.SetColumn(ulPanel, 2);
            dialsGrid.Children.Add(dlPanel); dialsGrid.Children.Add(ulPanel);
            dialsCard.Child = dialsGrid;
            Grid.SetRow(dialsCard, 2); root.Children.Add(dialsCard);

            // The live chart (under the dials) and, next to it, the previous tests in this session
            var lower = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            _chartCanvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            var chartHost = new Grid { MinHeight = 110 };  // fills all the free space under the dials (110: the axis stays inside the card on 768 px screens)
            chartHost.Children.Add(_chartCanvas);
            chartHost.SizeChanged += (_, e) => { _chartW = e.NewSize.Width; _chartH = e.NewSize.Height; DrawSpeedChart(); };

            var figures = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
            TextBlock Figure(string caption, string brushKey)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var cap = new TextBlock { Text = caption, FontSize = 10, FontWeight = FontWeights.SemiBold };
                cap.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                var val = new TextBlock { Text = "—", FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold };
                val.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                sp.Children.Add(cap); sp.Children.Add(val);
                figures.Children.Add(sp);
                return val;
            }
            _peakDl = Figure("↓ PEAK", "TextPrimaryBrush");
            _avgDl = Figure("↓ AVERAGE", "TextPrimaryBrush");
            _peakUl = Figure("↑ PEAK", "TextPrimaryBrush");
            _avgUl = Figure("↑ AVERAGE", "TextPrimaryBrush");
            _dataUsed = Figure("DATA USED", "TextSecondaryBrush");

            // legend: blue = discharge, green = charge
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            void LegendItem(string text, WpfColor c)
            {
                legend.Children.Add(new Border { Width = 18, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                var t = new TextBlock { Text = text, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 18, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                legend.Children.Add(t);
            }
            LegendItem("Download", DlColor2);
            LegendItem("Upload", UlColor1);
            var legendNote = new TextBlock { Text = "each test lasts 20 s", FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center };
            legendNote.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            legend.Children.Add(legendNote);

            var chartBody = new Grid();
            chartBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            chartBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartBody.Children.Add(legend);
            Grid.SetRow(chartHost, 1); chartBody.Children.Add(chartHost);
            // before the first test: the start card sits on the empty chart (big Start, what the test does)
            _startCard = StartCard.Build(SMDWin.Views.MainWindow.StartIconGauge,
                SMDWin.Views.MainWindow.PT("Internet speed test"),
                SMDWin.Views.MainWindow.PT("Measures download and upload speed, ping and jitter against a nearby server."),
                new[] { SMDWin.Views.MainWindow.PT("about 1 minute"), SMDWin.Views.MainWindow.PT("data use grows with the speed") },
                null, null,
                compact: true);
            _startCard.Margin = new Thickness(4);
            Grid.SetRow(_startCard, 0); Grid.SetRowSpan(_startCard, 3); chartBody.Children.Add(_startCard);
            Grid.SetRow(figures, 2); chartBody.Children.Add(figures);
            var chartCard = NetWindowHelper.CardWithHead(SMDWin.Views.MainWindow.PT("Speed over time"), SMDWin.Views.MainWindow.PT("Live while the test runs"), chartBody, "M3 12h4l3-8 4 16 3-8h4");
            Grid.SetColumn(chartCard, 0); lower.Children.Add(chartCard);

            _historyList = new StackPanel();
            ShowHistory();
            var historyCard = NetWindowHelper.CardWithHead(SMDWin.Views.MainWindow.PT("Earlier tests"), SMDWin.Views.MainWindow.PT("This session"),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _historyList }, "M12 8v5l3 2 M12 3a9 9 0 1 0 0 18a9 9 0 0 0 0-18");
            Grid.SetColumn(historyCard, 2); lower.Children.Add(historyCard);
            Grid.SetRow(lower, 3); root.Children.Add(lower);

            // Progress
            _progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Value = 0,
                Margin = new Thickness(0,0,0,8), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(WpfColor.FromArgb(40,200,200,200)) };
            _progress.Foreground = new LinearGradientBrush(DlColor1, UlColor1,
                new WpfPoint(0,0), new WpfPoint(1,0));
            Grid.SetRow(_progress, 4); root.Children.Add(_progress);

            // Phase label
            _txtPhase = new TextBlock { Text = "Press Start Test to begin", FontSize = Ui.FontS,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Opacity = 0.7 };
            Grid.SetRow(_txtPhase, 5); root.Children.Add(_txtPhase);

            // short screens (1366 × 768): smaller dials, so the chart under them keeps its axis and "Mbps" inside the card
            root.SizeChanged += (_, e) =>
            {
                if (!e.HeightChanged) return;
                bool shortScreen = e.NewSize.Height < 600;
                foreach (var g in new[] { _dlCanvas, _ulCanvas })
                    if (g != null) { g.Height = shortScreen ? 150 : 196; g.Width = shortScreen ? 184 : 240; }
            };

            return root;
        }

        private (StackPanel panel, TextBlock valTb) BuildDialPanel(
            string label, WpfColor labelColor, out SMDWin.Views.SegmentedGauge canvas)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            sp.Children.Add(new TextBlock { Text = label, FontSize = Ui.FontS,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0,0,0,6) });
            // a neutral label + a dial in the accent colour, as everywhere in the app
            ((TextBlock)sp.Children[sp.Children.Count - 1]).SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            canvas = new SMDWin.Views.SegmentedGauge
            {
                Width = 240, Height = 196, HorizontalAlignment = HorizontalAlignment.Center,
                InitialMax = 1000, Unit = "Mbps", ValueFormat = "F1",
            };
            sp.Children.Add(canvas);

            // The value is written in the centre of the gauge; the TextBlock stays (invisible) because the rest of the code still
            // updates it.
            var valTb = new TextBlock { Text = "—", Visibility = Visibility.Collapsed };

            return (sp, valTb);
        }

        // ── Test runner ───────────────────────────────────────────────────────
        public override bool SelfTestStart() { if (!_btnRun.IsEnabled) return false; RunTest_Click(this, new RoutedEventArgs()); return true; }
        public override void SelfTestStop()
        {
            if (_btnStop.IsEnabled) _btnStop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, _btnStop));
        }

        private async void RunTest_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel(); _cts = new CancellationTokenSource();
            _animTimer?.Stop();
            _btnRun.IsEnabled  = false;
            _btnStop.IsEnabled = true;
            if (_startCard != null) _startCard.Visibility = Visibility.Collapsed;
            _samples.Clear(); _testClock.Restart(); _phaseNow = 1;
            _peakDl.Text = _avgDl.Text = _peakUl.Text = _avgUl.Text = _dataUsed.Text = "—";
            DrawSpeedChart();
            _dlCurrent = 0; _dlTarget = 0;
            _ulCurrent = 0; _ulTarget = 0;
            _txtDlVal.Text = "—"; _txtUlVal.Text = "—";
            _txtPing.Text = "—"; _txtJitter.Text = "—";
            // SERVER-FIRST: the server is known from the start (speed.cloudflare.com)
            _txtServer.Text = "speed.cloudflare.com";
            _progress.Value = 0;
            _txtPhase.Text = LanguageService.S("Network", "MeasuringLatency");
            
            _dlCanvas.ResetScale();
            _ulCanvas.ResetScale();

            // 30fps animation
            _animTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(33) };
            _animTimer.Tick += (_, _) =>
            {
                _dlCurrent += (_dlTarget - _dlCurrent) * 0.15; // Slightly increased smoothness
                _ulCurrent += (_ulTarget - _ulCurrent) * 0.15;
                _dlCanvas.PeakOverride = _samples.Count > 0 ? SustainedPeak(1) : null;
                _ulCanvas.PeakOverride = _samples.Count > 0 ? SustainedPeak(2) : null;
                DrawDial(_dlCanvas, _dlCurrent, DlColor2);
                DrawDial(_ulCanvas, _ulCurrent, UlColor1);
                
                if (_dlCurrent > 0.1) _txtDlVal.Text = $"{_dlCurrent:F1}";
                if (_ulCurrent > 0.1) _txtUlVal.Text = $"{_ulCurrent:F1}";
            };
            _animTimer.Start();

            #pragma warning disable CS0219
            int phase = 0;
#pragma warning restore CS0219
            var progress = new Progress<string>(msg => Dispatcher.InvokeAsync(() =>
            {
                _txtPhase.Text = msg;
                if (msg.Contains("download", StringComparison.OrdinalIgnoreCase))
                    { phase = 1; _progress.Value = 5; _dlCurrent = 0; _dlTarget = 0; }
                else if (msg.Contains("upload", StringComparison.OrdinalIgnoreCase))
                    { phase = 2; _progress.Value = 55; _ulCurrent = 0; _ulTarget = 0; }
            }));

            try
            {
                var liveSpeed = new Progress<double>(mbps => Dispatcher.InvokeAsync(() =>
                {
                    if (_phaseNow == 2) _ulTarget = mbps;
                    else                _dlTarget = mbps;
                    _samples.Add((_testClock.Elapsed.TotalSeconds, mbps, _phaseNow == 2 ? 2 : 1));
                    if ((DateTime.Now - _lastChartDraw).TotalMilliseconds > 120) { _lastChartDraw = DateTime.Now; DrawSpeedChart(); }
                }));
                var phaseSig = new Progress<string>(p => Dispatcher.InvokeAsync(() =>
                {
                    if (p == "upload") _phaseNow = 2;
                    else if (p == "download") _phaseNow = 1;
                }));
                var result = await _svc.RunAsync(progress, _cts.Token, liveSpeed, phaseSig);

                if (result.Success)
                {
                    _dlTarget = result.DownloadMbps;
                    _ulTarget = result.UploadMbps;
                    _txtPing.Text   = result.PingMs   > 0 ? $"{result.PingMs:F0}"   : "—";
                    _txtJitter.Text = result.JitterMs > 0 ? $"{result.JitterMs:F0}" : "—";
                    
                    string srv = result.Server;
                    if (srv.StartsWith("www.")) srv = srv[4..];
                    _txtServer.Text = srv.Length > 22 ? srv[..22] + "…" : srv;
                    
                    _progress.Value = 100;
                    _txtPhase.Text  = LanguageService.S("Common", "Complete");

                    await System.Threading.Tasks.Task.Delay(1500);
                    _animTimer?.Stop();
                    _dlCurrent = result.DownloadMbps;
                    _ulCurrent = result.UploadMbps;
                    DrawDial(_dlCanvas, _dlCurrent, DlColor2);
                    DrawDial(_ulCanvas, _ulCurrent, UlColor1);
                    _txtDlVal.Text = $"{result.DownloadMbps:F1}";
                    _txtUlVal.Text = $"{result.UploadMbps:F1}";
                    DrawSpeedChart();
                    UpdateFigures();
                    _history.Insert(0, (DateTime.Now, result.DownloadMbps, result.UploadMbps, result.PingMs));
                    if (_history.Count > 8) _history.RemoveAt(_history.Count - 1);
                    ShowHistory();
                }
                else { _txtPhase.Text = LanguageService.S("Network", "TestFailedCheckConnection"); _animTimer?.Stop(); }
            }
            catch (OperationCanceledException) { _txtPhase.Text = LanguageService.S("Network", "CancelledDot"); _animTimer?.Stop(); }
            catch (Exception ex) { _txtPhase.Text = $"Error: {ex.Message}"; _animTimer?.Stop(); }
            finally
            {
                _testClock.Stop();
                _btnStop.IsEnabled = false;
                _btnRun.IsEnabled  = true;
            }
        }

        // ── The live chart ─────────────────────────────────────────────────────
        // The closest "round" step above the maximum value (10, 25, 50, 100, 250, 500, 1000...)
        private static double NiceCeiling(double v)
        {
            double[] steps = { 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };
            foreach (double st in steps) if (v <= st) return st;
            return Math.Ceiling(v / 1000) * 1000;
        }

        // Curba neteda prin puncte (Catmull-Rom transformat in segmente Bezier)
        private static PathGeometry SmoothCurve(IReadOnlyList<WpfPoint> p)
        {
            var fig = new PathFigure { StartPoint = p[0], IsClosed = false };
            for (int i = 0; i < p.Count - 1; i++)
            {
                var p0 = p[Math.Max(0, i - 1)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(p.Count - 1, i + 2)];
                // the control points stay between the Y of the two points: the curve does not overshoot the real values (no "waves")
                double lo = Math.Min(p1.Y, p2.Y), hi = Math.Max(p1.Y, p2.Y);
                var c1 = new WpfPoint(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, lo, hi));
                var c2 = new WpfPoint(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, lo, hi));
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
            return new PathGeometry(new[] { fig });
        }

        // The area under the curve: the curve, then down to the axis and back to the start
        private static PathGeometry CloseUnder(PathGeometry curve, WpfPoint first, WpfPoint last, double baseY)
        {
            var fig = new PathFigure { StartPoint = new WpfPoint(first.X, baseY), IsClosed = true };
            fig.Segments.Add(new LineSegment(first, false));
            foreach (var seg in curve.Figures[0].Segments) fig.Segments.Add(seg);
            fig.Segments.Add(new LineSegment(new WpfPoint(last.X, baseY), false));
            return new PathGeometry(new[] { fig });
        }

        // The measured values come irregularly and jump a lot. For drawing: points at a fixed interval (0.25 s), with the measured
        // value interpolated in time, then a moving average (time constant ~2 s). The final figures (peak, average) stay from the
        // real values.
        private static List<(double T, double Mbps)> SmoothSeries(IReadOnlyList<(double T, double Mbps, int Phase)> raw, double origin)
        {
            const double step = 0.25, tau = 2.0;
            double alpha = 1 - Math.Exp(-step / tau);
            var result = new List<(double T, double Mbps)> { (0, 0) };
            double tEnd = raw[^1].T - origin;
            double smooth = 0; int j = 0;
            for (double t = step; ; t += step)
            {
                bool last = t >= tEnd;
                double tt = last ? tEnd : t;
                double abs = tt + origin;
                while (j < raw.Count - 2 && raw[j + 1].T < abs) j++;
                var a = raw[j]; var b = raw[Math.Min(j + 1, raw.Count - 1)];
                double v = abs <= a.T ? a.Mbps : abs >= b.T || b.T <= a.T ? b.Mbps : a.Mbps + (b.Mbps - a.Mbps) * (abs - a.T) / (b.T - a.T);
                smooth += (v - smooth) * alpha;
                result.Add((tt, smooth));
                if (last) break;
            }
            return result;
        }

        // The "sustained" peak: the average of 3 consecutive measurements (~1 s). An isolated 0.4 s measurement can jump far above
        // the real speed; the same number appears in the dial ("max") and in the table below.
        private double SustainedPeak(int phase)
        {
            var v = _samples.Where(x => x.Phase == phase).Select(x => x.Mbps).ToList();
            if (v.Count == 0) return 0;
            if (v.Count < 3) return v.Average();
            double best = 0;
            for (int i = 2; i < v.Count; i++) best = Math.Max(best, (v[i] + v[i - 1] + v[i - 2]) / 3.0);
            return best;
        }

        /// <summary>The chart starts at 100 Mbps (most connections are faster than 40-50); faster ones scale it up.</summary>
        private const double MinChartMbps = 100;
        private double _yMaxShown = MinChartMbps;   // the vertical axis grows, but no longer shrinks during the test (it does not "shake")

        private double _chartW, _chartH;   // the real size of the area (ActualWidth lags behind on resize)

        private void DrawSpeedChart()
        {
            if (_chartCanvas == null) return;
            double w = _chartW > 0 ? _chartW : _chartCanvas.ActualWidth, h = _chartH > 0 ? _chartH : _chartCanvas.ActualHeight;
            if (w < 60 || h < 40) return;
            _chartCanvas.Children.Clear();

            const double pL = 44, pR = 8, pT = 6, pB = 20;
            double cw = w - pL - pR, ch = h - pT - pB;
            var smoothed = new Dictionary<int, List<(double T, double Mbps)>>();
            foreach (int ph in new[] { 1, 2 })
            {
                var rawPh = _samples.Where(x => x.Phase == ph).ToList();
                if (rawPh.Count >= 2) smoothed[ph] = SmoothSeries(rawPh, rawPh[0].T - 0.4 < 0 ? 0 : rawPh[0].T - 0.4);
            }
            double peak = smoothed.Count > 0 ? smoothed.Values.Max(v => v.Max(x => x.Mbps)) : 0;
            if (_samples.Count == 0) _yMaxShown = MinChartMbps;
            _yMaxShown = Math.Max(_yMaxShown, NiceCeiling(Math.Max(MinChartMbps, peak * 1.1)));
            double yMax = _yMaxShown;
            // the time axis is fixed (0-20 s) and each phase starts from 0: the two lines overlap
            const double xMax = SMDWin.Services.SpeedTestService.PhaseSeconds;

            // the shared chart grid (thin lines fading towards their ends, no frame); its rows match the labels
            ChartGrid.AddTo(_chartCanvas, new Rect(pL, pT, cw, ch), 4);
            for (int i = 0; i <= 4; i++)
            {
                double value = yMax * i / 4, y = pT + ch - ch * i / 4;
                var label = new TextBlock { Text = value >= 1000 ? $"{value / 1000:0.#}G" : $"{value:0}", FontSize = Ui.FontXS, Opacity = 0.8 };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Canvas.SetLeft(label, 4); Canvas.SetTop(label, y - 8);
                _chartCanvas.Children.Add(label);
            }
            var unit = new TextBlock { Text = "Mbps", FontSize = 9, Opacity = 0.6 };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Canvas.SetLeft(unit, 4); Canvas.SetTop(unit, h - pB + 4);
            _chartCanvas.Children.Add(unit);
            for (double t = 0; t <= xMax; t += 5)
            {
                var tick = new TextBlock { Text = $"{t:0}s", FontSize = Ui.FontXS, Opacity = 0.8 };
                tick.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Canvas.SetLeft(tick, pL + cw * t / xMax - 8); Canvas.SetTop(tick, h - pB + 4);
                _chartCanvas.Children.Add(tick);
            }

            foreach (int phase in new[] { 1, 2 })
            {
                var color = phase == 1 ? DlColor2 : UlColor1;
                if (!smoothed.TryGetValue(phase, out var series)) continue;
                var pts = new List<WpfPoint>(series.Count);
                foreach (var smp in series)
                    pts.Add(new WpfPoint(pL + cw * Math.Min(1, smp.T / xMax), pT + ch - ch * Math.Min(1, smp.Mbps / yMax)));
                var curve = SmoothCurve(pts);
                var area = new System.Windows.Shapes.Path
                {
                    Data = CloseUnder(curve, pts[0], pts[^1], pT + ch),
                    Fill = new LinearGradientBrush(WpfColor.FromArgb(70, color.R, color.G, color.B), WpfColor.FromArgb(4, color.R, color.G, color.B),
                        new WpfPoint(0, 0), new WpfPoint(0, 1)),
                };
                _chartCanvas.Children.Add(area);
                var stroke = new System.Windows.Shapes.Path { Data = curve, Stroke = new SolidColorBrush(color), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
                _chartCanvas.Children.Add(stroke);
                var marker = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color) };
                Canvas.SetLeft(marker, pts[^1].X - 4); Canvas.SetTop(marker, pts[^1].Y - 4);
                _chartCanvas.Children.Add(marker);
            }
        }

        // Peak, average and the amount of data used (the area under the curve), for each phase
        private void UpdateFigures()
        {
            string Mbps(double v) => v >= 100 ? $"{v:0} Mbps" : $"{v:0.#} Mbps";
            double Peak(int phase) => SustainedPeak(phase);
            double Avg(int phase) { var v = _samples.Where(x => x.Phase == phase && x.Mbps > 0).Select(x => x.Mbps).ToList(); return v.Count > 0 ? v.Average() : 0; }
            double megabytes = 0;
            for (int i = 1; i < _samples.Count; i++)
                if (_samples[i].Phase == _samples[i - 1].Phase)
                    megabytes += _samples[i].Mbps * (_samples[i].T - _samples[i - 1].T) / 8;
            _peakDl.Text = Mbps(Peak(1)); _avgDl.Text = Mbps(Avg(1));
            _peakUl.Text = Mbps(Peak(2)); _avgUl.Text = Mbps(Avg(2));
            _dataUsed.Text = megabytes >= 1000 ? $"{megabytes / 1000:0.0} GB" : $"{megabytes:0} MB";
        }

        private void ShowHistory()
        {
            _historyList.Children.Clear();
            if (_history.Count == 0)
            {
                var none = new TextBlock { Text = "No test yet — press Start Test.", FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
                none.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                _historyList.Children.Add(none);
                return;
            }
            foreach (var (at, dl, ul, ping) in _history)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var time = new TextBlock { Text = at.ToString("HH:mm:ss"), FontSize = Ui.FontXS };
                time.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                var values = new TextBlock { Text = $"↓ {dl:0.#}   ↑ {ul:0.#}  Mbps" + (ping > 0 ? $"   ·  {ping:0} ms" : ""), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                values.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                row.Children.Add(time); row.Children.Add(values);
                _historyList.Children.Add(row);
            }
        }

        private TextBlock MakeInfoTile(StackPanel parent, string lbl, string val, string unit)
        {
            var sp = new StackPanel { Margin = new Thickness(36,0,36,0),
                HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = lbl, FontSize = 9,
                FontWeight = FontWeights.SemiBold, Opacity = 0.85,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0,0,0,4) });
            var valTb = new TextBlock { Text = val, FontSize = Ui.FontSectionTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(valTb);
            if (!string.IsNullOrEmpty(unit))
                sp.Children.Add(new TextBlock { Text = unit, FontSize = 9, Opacity = 0.75,
                    Foreground = NetWindowHelper.Res("TextSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center });
            parent.Children.Add(sp);
            return valTb;
        }

        private static void MakeInfoSep(StackPanel parent) =>
            parent.Children.Add(new Border { Width = 1,
                Margin = new Thickness(0,4,0,4),
                Background = new SolidColorBrush(WpfColor.FromArgb(60,180,180,200)),
                VerticalAlignment = VerticalAlignment.Stretch });
    }
}