
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Orientation = System.Windows.Controls.Orientation;
using WpfColor    = System.Windows.Media.Color;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfButton   = System.Windows.Controls.Button;

namespace SMDWin.Views
{
    public class BenchmarkWindow : Window
    {
        public enum Kind { Cpu, Gpu }

        private readonly Kind _kind;
        private readonly bool _isLight;
        private readonly WpfColor _bgColor, _fgColor, _fgSub, _accentClr, _borderClr, _hoverClr;
        private readonly WpfColor _amberClr = WpfColor.FromRgb(245, 158, 11);

        /// <summary>Provided by MainWindow: the current CPU load (0-100), for the precheck guard.</summary>
        public Func<float>? GetCurrentCpuLoad { get; set; }
        /// <summary>Provided by MainWindow: the shared GPU stress instance (the same one used by the GPU Stress toggle).</summary>
        public SMDWin.Services.GpuStressService? GpuStress { get; set; }

        private CancellationTokenSource? _cts;
        private System.Windows.Threading.DispatcherTimer? _tickTimer;
        private DateTime _phaseStart;
        private TimeSpan _plannedTotal = TimeSpan.FromSeconds(15);
        private bool _done;
        private System.Diagnostics.ProcessPriorityClass? _origPriority;

        // UI refs
        private TextBlock _txtPhase = null!;
        private TextBlock _txtElapsed = null!;
        private Border _progressFg = null!;
        private Border _progressBg = null!;
        private StackPanel _metricsPanel = null!;
        private StackPanel _precheckPanel = null!;
        private TextBlock _txtPrecheckMsg = null!;
        private StackPanel _runPanel = null!;
        private StackPanel _resultPanel = null!;
        private TextBlock _txtResultScore = null!;
        private TextBlock _txtResultRating = null!;
        private StackPanel _resultMetrics = null!;
        private WpfButton _btnCancel = null!;
        private WpfButton _btnCloseFooter = null!;

        public BenchmarkWindow(Kind kind, bool isLightTheme)
        {
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            _kind    = kind;
            _isLight = isLightTheme;

            WpfColor AppColor(string key, WpfColor fallback) =>
                System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush b
                    ? b.Color : fallback;

            _bgColor   = AppColor("BgDarkBrush",        isLightTheme ? WpfColor.FromRgb(238,242,247) : WpfColor.FromRgb(19,24,32));
            _fgColor   = AppColor("TextPrimaryBrush",   isLightTheme ? WpfColor.FromRgb(15,23,42)    : WpfColor.FromRgb(255,255,255));
            _fgSub     = AppColor("TextSecondaryBrush", isLightTheme ? WpfColor.FromRgb(71,85,105)   : WpfColor.FromRgb(212,228,247));
            _accentClr = AppColor("AccentBrush",        WpfColor.FromRgb(29,111,232));
            _borderClr = AppColor("CardBorderBrush",    isLightTheme ? WpfColor.FromRgb(203,213,225) : WpfColor.FromRgb(42,51,69));
            _hoverClr  = AppColor("BgCardBrush",        isLightTheme ? WpfColor.FromRgb(255,255,255) : WpfColor.FromRgb(28,36,49));

            Title                 = kind == Kind.Cpu ? "CPU Benchmark" : "GPU Benchmark";
            SizeToContent         = SizeToContent.Height;
            Width                 = 460;
            WindowStyle           = WindowStyle.None;
            ResizeMode            = ResizeMode.NoResize;
            AllowsTransparency    = true;
            Background            = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar         = false;

            Content = BuildUI();

            Loaded  += async (_, _) => await StartAsync();
            Closing += (_, _) => { try { _cts?.Cancel(); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); } RestorePriority(); };
        }

        // ── UI construction ──────────────────────────────────────────────────
        private UIElement BuildUI()
        {
            double radiusCardForWin = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;
            var shadow = new Grid();
            shadow.Children.Add(new Border
            {
                Margin       = new Thickness(12),
                CornerRadius = new CornerRadius(radiusCardForWin),
                Background   = new SolidColorBrush(WpfColor.FromArgb(_isLight ? (byte)30 : (byte)140, 0, 0, 0)),
                Effect       = new System.Windows.Media.Effects.BlurEffect { Radius = 18 },
                IsHitTestVisible = false,
            });

            var outer = new Border
            {
                Margin          = new Thickness(12),
                CornerRadius    = new CornerRadius(radiusCardForWin),
                Background      = new SolidColorBrush(_bgColor),
                BorderBrush     = new SolidColorBrush(_borderClr),
                BorderThickness = new Thickness(1.5),
                ClipToBounds    = true,
            };
            shadow.Children.Add(outer);
            outer.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); } };

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            outer.Child = root;

            // ── Title row ──
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = _kind == Kind.Cpu ? "⚙ CPU Benchmark" : "🎮 GPU Benchmark",
                FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(_fgColor),
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = _kind == Kind.Cpu
                    ? "SHA-256 · Mandelbrot · FFT — single-core reference"
                    : "D3D11 compute throughput (FP32)",
                FontSize = Ui.FontS, Foreground = new SolidColorBrush(_fgSub), Opacity = 0.75,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(titleStack, 0);
            titleGrid.Children.Add(titleStack);

            var closeBtn = Net.NetWindowHelper.CloseIconButton(32);
            closeBtn.VerticalAlignment = VerticalAlignment.Top;
            closeBtn.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            closeBtn.Click += (_, _) => Close();
            Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(closeBtn);
            root.Children.Add(titleGrid);

            root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(_borderClr), Opacity = 0.5, Margin = new Thickness(0, 14, 0, 14) });

            // ── Precheck banner (CPU only — shown only if the load is high) ──
            _precheckPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var precheckBox = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(30, _amberClr.R, _amberClr.G, _amberClr.B)),
                BorderBrush = new SolidColorBrush(_amberClr), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var precheckInner = new StackPanel();
            precheckInner.Children.Add(new TextBlock { Text = "⚠ System is busy", FontWeight = FontWeights.Bold, FontSize = Ui.FontM, Foreground = new SolidColorBrush(_amberClr) });
            _txtPrecheckMsg = new TextBlock
            {
                FontSize = Ui.FontS, Foreground = new SolidColorBrush(_fgSub), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            };
            precheckInner.Children.Add(_txtPrecheckMsg);
            var precheckBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var btnCancelPrecheck = MakeBtn("Cancel", filled: false);
            var btnRunAnyway      = MakeBtn("Run Anyway", filled: true);
            btnCancelPrecheck.Click += (_, _) => Close();
            btnRunAnyway.Click += async (_, _) =>
            {
                _precheckPanel.Visibility = Visibility.Collapsed;
                _runPanel.Visibility = Visibility.Visible;
                await RunBenchmarkAsync();
            };
            precheckBtns.Children.Add(btnCancelPrecheck);
            var spacer2 = new System.Windows.Controls.Border { Width = 8 };
            precheckBtns.Children.Add(spacer2);
            precheckBtns.Children.Add(btnRunAnyway);
            precheckInner.Children.Add(precheckBtns);
            precheckBox.Child = precheckInner;
            _precheckPanel.Children.Add(precheckBox);
            root.Children.Add(_precheckPanel);

            // ── Run panel (progres live) ──
            _runPanel = new StackPanel { Visibility = Visibility.Visible };

            var phaseRow = new Grid();
            phaseRow.ColumnDefinitions.Add(new ColumnDefinition());
            phaseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _txtPhase = new TextBlock
            {
                Text = "Initializing…", FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_fgColor),
            };
            Grid.SetColumn(_txtPhase, 0);
            phaseRow.Children.Add(_txtPhase);
            _txtElapsed = new TextBlock
            {
                Text = "0s", FontSize = Ui.FontS, Foreground = new SolidColorBrush(_fgSub),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_txtElapsed, 1);
            phaseRow.Children.Add(_txtElapsed);
            _runPanel.Children.Add(phaseRow);

            _progressBg = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 8, 0, 4),
                Background = new SolidColorBrush(WpfColor.FromArgb(40, _fgSub.R, _fgSub.G, _fgSub.B)),
            };
            _progressFg = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Width = 0,
                Background = new SolidColorBrush(_accentClr),
            };
            var progressGrid = new Grid();
            progressGrid.Children.Add(_progressBg);
            progressGrid.Children.Add(_progressFg);
            _runPanel.Children.Add(progressGrid);

            _metricsPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 4) };
            _runPanel.Children.Add(_metricsPanel);

            var cancelRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _btnCancel = MakeBtn("Cancel", filled: false);
            _btnCancel.Click += (_, _) => { try { _cts?.Cancel(); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); } };
            cancelRow.Children.Add(_btnCancel);
            _runPanel.Children.Add(cancelRow);

            root.Children.Add(_runPanel);

            // ── Result panel (shown at the end) ──
            _resultPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var resultBadgeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            _txtResultScore = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_accentClr) };
            resultBadgeRow.Children.Add(_txtResultScore);
            _resultPanel.Children.Add(resultBadgeRow);
            _txtResultRating = new TextBlock { Text = "", FontSize = Ui.FontM, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = new SolidColorBrush(_fgSub), Margin = new Thickness(0, 0, 0, 12) };
            _resultPanel.Children.Add(_txtResultRating);
            _resultMetrics = new StackPanel();
            _resultPanel.Children.Add(_resultMetrics);

            var footerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _btnCloseFooter = MakeBtn("Close", filled: true);
            _btnCloseFooter.Click += (_, _) => Close();
            footerRow.Children.Add(_btnCloseFooter);
            _resultPanel.Children.Add(footerRow);

            root.Children.Add(_resultPanel);

            return shadow;
        }

        private WpfButton MakeBtn(string label, bool filled)
        {
            var btn = new WpfButton { Content = label, Cursor = System.Windows.Input.Cursors.Hand };
            string styleKey = filled ? "Lift.Primary" : "OutlineButtonStyle";
            if (System.Windows.Application.Current?.TryFindResource(styleKey) is Style s)
                btn.Style = s;
            return btn;
        }

        private void SetProgress(double pct)
        {
            if (_progressBg == null || _progressFg == null) return;
            double w = Math.Max(0, (_progressBg.ActualWidth > 0 ? _progressBg.ActualWidth : 400) * Math.Clamp(pct, 0, 1));
            _progressFg.Width = w;
        }

        private static string FormatAgo(TimeSpan span)
        {
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            int days = (int)span.TotalDays;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }

        private void AddMetricRow(StackPanel panel, string label, string value, WpfColor? valueColor = null)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, FontSize = Ui.FontS, Foreground = new SolidColorBrush(_fgSub) });
            var valTb = new TextBlock { Text = value, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(valueColor ?? _fgColor) };
            Grid.SetColumn(valTb, 1);
            row.Children.Add(valTb);
            panel.Children.Add(row);
        }

        // ── Elapsed-time ticker — keeps visual feedback even between phase updates
        // (avoids the "frozen window" feeling during a long subtest) ──
        private void StartTicker()
        {
            _phaseStart = DateTime.Now;
            _tickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _tickTimer.Tick += (_, _) =>
            {
                if (_done) return;
                var elapsed = DateTime.Now - _phaseStart;
                _txtElapsed.Text = $"{elapsed.TotalSeconds:F0}s";
                double frac = _plannedTotal.TotalSeconds > 0 ? elapsed.TotalSeconds / _plannedTotal.TotalSeconds : 0;
                SetProgress(Math.Min(0.97, frac)); // does not reach 100% until real completion is confirmed
            };
            _tickTimer.Start();
        }
        private void StopTicker() { try { _tickTimer?.Stop(); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); } }

        private void BoostPriority()
        {
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                _origPriority = p.PriorityClass;
                p.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
            }
            catch { _origPriority = null; }
        }
        private void RestorePriority()
        {
            if (_origPriority == null) return;
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = _origPriority.Value; } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            _origPriority = null;
        }

        // ── Orchestrare ──────────────────────────────────────────────────────
        private async Task StartAsync()
        {
            if (_kind == Kind.Cpu)
            {
                float load = GetCurrentCpuLoad?.Invoke() ?? 0f;
                if (load > 20f)
                {
                    _txtPrecheckMsg.Text =
                        $"Current CPU load is {load:F0}%. Close other heavy apps (or wait for the GPU test to finish) " +
                        "for an accurate score — otherwise the result will be lower than your CPU's real performance.";
                    _precheckPanel.Visibility = Visibility.Visible;
                    _runPanel.Visibility = Visibility.Collapsed;
                    return; // wait for the user's decision (Run Anyway / Cancel)
                }
            }
            await RunBenchmarkAsync();
        }

        private async Task RunBenchmarkAsync()
        {
            _cts = new CancellationTokenSource();
            BoostPriority();
            StartTicker();

            try
            {
                if (_kind == Kind.Cpu) await RunCpuAsync(_cts.Token);
                else                   await RunGpuAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                _txtPhase.Text = LanguageService.S("Common", "Cancelled");
            }
            catch (Exception ex)
            {
                _txtPhase.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _done = true;
                StopTicker();
                RestorePriority();
            }
        }

        // ── CPU path ──────────────────────────────────────────────────────────
        private async Task RunCpuAsync(CancellationToken ct)
        {
            _plannedTotal = TimeSpan.FromSeconds(18); // 3 subtests x ~5-6s
            const double perTestSec = 5.0; // longer than 2-3 s — a more stable result

            // Guaranteed timeout: a 90s cushion over the planned duration
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var benchTask = Task.Run(() => SMDWin.Services.CpuStressor.RunFullBenchmark(perTestSec, phase =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (_done) return;
                    _txtPhase.Text = phase;
                    _phaseStart = DateTime.Now; // reset the ticker for the new phase
                });
            }));

            var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
            var completedTask = await Task.WhenAny(benchTask, delayTask);

            if (completedTask != benchTask)
            {
                // Cancel/timeout: the UI leaves immediately; the compute thread (with no cancellation points of its own) finishes by
                // itself in the background within a few seconds — harmless, the window is already closed/hidden.
                throw new OperationCanceledException();
            }

            var r = await benchTask;

            string shaFmt = r.Sha256HashesPerSec >= 1_000_000
                ? $"{r.Sha256HashesPerSec/1_000_000:F2}M hashes/s"
                : $"{r.Sha256HashesPerSec/1_000:F0}K hashes/s";
            string mbrotFmt = r.MandelbrotPixPerSec >= 1_000_000
                ? $"{r.MandelbrotPixPerSec/1_000_000:F1}M px/s"
                : $"{r.MandelbrotPixPerSec/1_000:F0}K px/s";
            string fftFmt = $"{r.FftOpsPerSec:F0} ops/s";

            _done = true;
            SetProgress(1.0);
            _runPanel.Visibility = Visibility.Collapsed;
            _resultPanel.Visibility = Visibility.Visible;
            _txtResultScore.Text = r.CompositeScore.ToString();

            if (r.HashMismatches > 0 || r.FftMismatches > 0)
            {
                AddMetricRow(_resultMetrics, "⚠ Data integrity",
                    $"{r.HashMismatches} SHA-256 + {r.FftMismatches} FFT mismatch(es) — evidence of real hardware instability, not just heat");
            }
            else
            {
                AddMetricRow(_resultMetrics, "Data integrity", "no computation errors detected (math verified under load)");
            }

            try
            {
                string cpuName = "";
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    cpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";

                if (!string.IsNullOrEmpty(cpuName))
                {
                    SMDWin.Services.BenchmarkHistoryService.SaveResult(cpuName, r.Cores, r.CompositeScore, r.Rating,
                        r.Sha256HashesPerSec, r.MandelbrotPixPerSec, r.FftOpsPerSec);
                    var pct = SMDWin.Services.BenchmarkHistoryService.GetPercentile(r.CompositeScore);
                    if (pct.HasValue)
                        AddMetricRow(_resultMetrics, "vs. local history",
                            $"better than ~{pct}% of runs recorded on this PC/stick so far");

                    var machineHistory = SMDWin.Services.BenchmarkHistoryService.GetSameMachineHistory(Environment.MachineName);
                    if (machineHistory.Count >= 2)
                    {
                        var prev = machineHistory[1];
                        int delta = r.CompositeScore - prev.CompositeScore;
                        double deltaPct = prev.CompositeScore > 0 ? 100.0 * delta / prev.CompositeScore : 0;
                        string arrow = delta > 0 ? "▲" : delta < 0 ? "▼" : "≈";
                        var deltaColor = delta > 0 ? WpfColor.FromRgb(34, 197, 94)
                                        : delta < 0 ? WpfColor.FromRgb(239, 68, 68)
                                        : _fgColor;
                        string agoText = FormatAgo(DateTime.UtcNow - prev.TimestampUtc);
                        AddMetricRow(_resultMetrics, "vs. previous run on this PC",
                            $"{arrow} {(delta >= 0 ? "+" : "")}{delta} pts ({(deltaPct >= 0 ? "+" : "")}{deltaPct:F1}%) — was {prev.CompositeScore} {agoText}",
                            deltaColor);
                    }
                }
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"Benchmark history save: {ex.Message}"); }

            SMDWin.Services.AppLogger.LogSection("BENCHMARK: CPU",
                ("Composite Score", r.CompositeScore),
                ("SHA-256",         shaFmt),
                ("Mandelbrot",      mbrotFmt),
                ("FFT",             fftFmt),
                ("Session CPU Max °C", Owner is MainWindow mwCpu ? mwCpu.SessionCpuMaxTemp?.ToString("F0") : null));
            _txtResultRating.Text = r.Rating;
            AddMetricRow(_resultMetrics, "SHA-256 (crypto)", shaFmt);
            AddMetricRow(_resultMetrics, "Mandelbrot (FPU/IPC)", mbrotFmt);
            AddMetricRow(_resultMetrics, "FFT (cache/bandwidth)", fftFmt);
            AddMetricRow(_resultMetrics, "Cores used", r.Cores.ToString());
        }

        // ── GPU path ──────────────────────────────────────────────────────────
        private async Task RunGpuAsync(CancellationToken ct)
        {
            _plannedTotal = TimeSpan.FromSeconds(7); // ~1 s init + ~1 s warm-up/calibration + 2.5 s measurement + margin

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var gct = linked.Token;

            var svc = new SMDWin.Services.GpuBenchmarkService();
            _txtPhase.Text = LanguageService.S("Stress", "InitializingGpu");
            _phaseStart = DateTime.Now;

            // Live readout — GFLOPS per batch, updated by the service during the measurement
            var liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            liveTimer.Tick += (_, _) =>
            {
                if (_done) return;
                _metricsPanel.Children.Clear();
                AddMetricRow(_metricsPanel, "GFLOPS (live)",
                    svc.LiveGFlops > 0 ? $"{svc.LiveGFlops:F0}" : "—", _accentClr);
            };

            SMDWin.Services.GpuBenchmarkResult result;
            var progress = new Progress<string>(msg => { if (!_done) _txtPhase.Text = msg; });
            try
            {
                liveTimer.Start();
                result = await svc.RunAsync(GpuStress!, gct, progress);
            }
            catch (OperationCanceledException)
            {
                liveTimer.Stop();
                throw;
            }
            finally { liveTimer.Stop(); }

            double gflops = result.Succeeded ? result.GFlops : 0;
            // Score /25 on a logarithmic scale: 40 GFLOPS → 0, 20,000 GFLOPS → 25.
            // Logarithmic because GPUs span 3 orders of magnitude (GT 755M ~700 → RTX 4090 ~80,000) — a linear scale would squash
            // everything that is not high-end at 0.
            double gpuScore = gflops > 0
                ? Math.Clamp(25.0 * Math.Log10(Math.Max(1.0, gflops) / 40.0) / Math.Log10(20000.0 / 40.0), 0, 25)
                : 0;
            string gpuDisp = gflops > 0
                ? $"{gflops:F0} GFLOPS (FP32)  ·  {result.Rating}"
                : $"Not available ({result.ErrorMessage})";

            _done = true;
            SetProgress(1.0);
            _runPanel.Visibility = Visibility.Collapsed;
            _resultPanel.Visibility = Visibility.Visible;
            _txtResultScore.Text = gflops > 0 ? $"{gpuScore:F0}/25" : "—";
            _txtResultRating.Text = gpuDisp;

            SMDWin.Services.AppLogger.LogSection("BENCHMARK: GPU",
                ("Score",           gflops > 0 ? $"{gpuScore:F0}/25" : "—"),
                ("GFLOPS",          gflops > 0 ? $"{gflops:F0}" : "—"),
                ("Adapter",         result.AdapterName),
                ("Session GPU Max °C", Owner is MainWindow mwGpu ? mwGpu.SessionGpuMaxTemp?.ToString("F0") : null));
            AddMetricRow(_resultMetrics, "Compute (FP32)", gflops > 0 ? $"{gflops:F0} GFLOPS" : "—");
            AddMetricRow(_resultMetrics, "Dispatch rate", result.DispatchHz > 0 ? $"{result.DispatchHz:F0} dispatch/s" : "—");
            AddMetricRow(_resultMetrics, "Adapter", result.AdapterName.Length > 0 ? result.AdapterName : "—");
            if (!result.Succeeded)
                AddMetricRow(_resultMetrics, "Note", result.ErrorMessage, _amberClr);
        }
    }
}
