using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SMDWin.Services;

using Color               = System.Windows.Media.Color;
using Colors              = System.Windows.Media.Colors;
using Brushes             = System.Windows.Media.Brushes;
using FontFamily          = System.Windows.Media.FontFamily;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using SolidColorBrush     = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using Cursors             = System.Windows.Input.Cursors;
using Application         = System.Windows.Application;

namespace SMDWin.Views
{
    public class CompactWidgetWindow : Window
    {
        // cell height and width depend on the style (Pill / Gauges / Minimal)
        private double PillHeight  => _style switch { "Gauges" => 76, "Minimal" => 36, _ => 62 };
        private double MetricWidth => _style switch { "Gauges" => 58, "Minimal" => 60, _ => 70 };
        private string _style = "Gauges";

        /// <summary>Called when the style changes from the widget menu (MainWindow updates Settings).</summary>
        public Action? StyleChanged { get; set; }

        private static string CurrentStyleSetting()
        {
            string s = SettingsService.Current.WidgetStyle ?? "Gauges";
            return s is "Pill" or "Gauges" or "Minimal" ? s : "Gauges";
        }
        private const double SepWidth    = 1;
        private const double PadH        = 14;
        private const double CornerR     = 22;
        private const double MarginRight = 16;   // gap from screen right edge
        private const double MarginBottom= 16;   // gap from taskbar/screen bottom

        private const double ShadowMargin = 18;

        // ── Always-dark colors ────────────────────────────────────────────────
        private static readonly Color BgColor    = Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E);
        private static readonly Color LabelColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        private static readonly Color ValueColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color SepColor   = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private static readonly Color HotColor   = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);  // red ≥85%
        private static readonly Color WarnColor  = Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24);  // yellow ≥70%
        private static readonly Color TimerColor = Color.FromArgb(0xFF, 0xF9, 0x73, 0x16);  // orange for timer

        // ── Available metrics ─────────────────────────────────────────────────
        private static readonly string[] AllMetrics =
            { "CPU", "CPUFreq", "CPUTemp", "GPU", "GPUTemp", "RAM",
              "Battery", "BatteryTime", "NetUp", "NetDown", "DiskWrite", "DiskRead" };

        // Navigation targets per metric
        private static readonly Dictionary<string, string> MetricNav = new()
        {
            ["CPU"]         = "CPU",
            ["CPUFreq"]     = "CPU",
            ["CPUTemp"]     = "CPU",
            ["GPU"]         = "GPU",
            ["GPUTemp"]     = "GPU",
            ["RAM"]         = "RAM",
            ["Battery"]     = "Battery",
            ["BatteryTime"] = "Battery",
            ["NetUp"]       = "Network",
            ["NetDown"]     = "Network",
            ["DiskRead"]    = "Storage",
            ["DiskWrite"]   = "Storage",
        };

        private static readonly Dictionary<string, (float min, float max)> BarRanges = new()
        {
            ["CPU"]     = (0f, 100f),
            ["GPU"]     = (0f, 100f),
            ["RAM"]     = (0f, 100f),
            ["Battery"] = (0f, 100f),
            ["CPUTemp"] = (30f, 100f),
            ["GPUTemp"] = (30f, 100f),
            ["NetUp"]     = (0f, ScaleService.DefaultNetMax),
            ["NetDown"]   = (0f, ScaleService.DefaultNetMax),
            ["DiskRead"]  = (0f, 550f * 1024f),
            ["DiskWrite"] = (0f, 550f * 1024f),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private string[] _activeMetrics = Array.Empty<string>();
        private int      _scrollOffset  = 0;     // for scroll between metric pages
        private const int MaxVisible    = 10;     // max metrics per page

        // Shutdown timer state
        private bool     _timerActive    = false;
        private DateTime _timerEnd       = DateTime.MinValue;
        private double   _timerTotalSecs = 1;

        // ── UI references ─────────────────────────────────────────────────────
        private readonly Dictionary<string, TextBlock> _lblMap = new();
        private readonly Dictionary<string, TextBlock> _valMap = new();
        private readonly Dictionary<string, Border> _borderMap = new();
        private readonly Dictionary<string, Rectangle> _barFillMap = new();
        private readonly HashSet<string> _criticalMetrics = new();
        private static readonly SolidColorBrush _bBorderTransparent = Freeze(new SolidColorBrush(Colors.Transparent));
        private StackPanel _metricsPanel = new();
        private Border     _pill         = new();

        // Timer metric UI
        private TextBlock  _timerLbl  = null!;
        private TextBlock  _timerVal  = null!;
        private Rectangle  _timerBar  = null!;
        private StackPanel _timerCell = null!;

        // ── NavigateTo delegate (set by MainWindow) ───────────────────────────
        public Action<string>? NavigateTo { get; set; }

        public Action? CancelShutdown { get; set; }

        public CompactWidgetWindow()
        {
            WindowStyle        = WindowStyle.None;
            AllowsTransparency = true;
            Background         = Brushes.Transparent;
            ResizeMode         = ResizeMode.NoResize;
            Topmost            = true;
            ShowInTaskbar      = false;  // stays in tray onlyget - not in taskbar
            ShowActivated      = false;
                                         // the main app focus on opening
            SizeToContent      = SizeToContent.Width;
            _style             = CurrentStyleSetting();
            Height             = PillHeight + ShadowMargin * 2;
            Title              = "SMDWin — Widget";
            UseLayoutRounding   = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            BuildUI();

            Loaded += (_, _) => { ApplyOpacity(); ApplyMetrics(); StartRefresh(); };
            // ContentRendered fires after SizeToContent has calculated actual width
            ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(MoveToDefaultPosition));
            Closing += OnClosing;

            // Left-click drag (single click); double-click → navigate
            MouseLeftButtonDown += OnMouseLeftDown;

            // Scroll → next metric page
            MouseWheel += OnMouseWheel;

            // Right-click context menu
            MouseRightButtonUp += OnRightClick;
        }

        // ── Build UI ──────────────────────────────────────────────────────────
        private void BuildUI()
        {
            _metricsPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(PadH, 0, PadH, 0)
            };

            _pill = new Border
            {
                Background   = new SolidColorBrush(BgColor),
                BorderBrush  = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = PillCorner(),
                Height       = PillHeight,
                Child        = _metricsPanel,
            };
            // the shadow sits on its own empty layer: an Effect on the pill itself would blur the metric texts
            _pillShadow = new Border
            {
                Background   = new SolidColorBrush(BgColor),
                CornerRadius = PillCorner(),
                Height       = PillHeight,
                IsHitTestVisible = false,
                Effect       = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, Opacity = 0.45, BlurRadius = 14, ShadowDepth = 2
                }
            };
            var layers = new Grid { Margin = new Thickness(ShadowMargin) };
            layers.Children.Add(_pillShadow);
            layers.Children.Add(_pill);
            Content = layers;
        }

        private Border _pillShadow = new();

        private CornerRadius PillCorner() => _style == "Minimal"
            ? new CornerRadius(PillHeight / 2)                // a full capsule
            : new CornerRadius(14);

        // ── Rebuild metric cells ──────────────────────────────────────────────
        public void ApplyMetrics()
        {
            // the style is read on every rebuild (changed in Settings or in the widget menu)
            _style = CurrentStyleSetting();
            Height = PillHeight + ShadowMargin * 2;
            _pill.Height = PillHeight;
            _pill.CornerRadius = PillCorner();
            _pillShadow.Height = PillHeight;
            _pillShadow.CornerRadius = PillCorner();
            _metricsPanel.Margin = new Thickness(_style == "Minimal" ? 16 : PadH, 0, _style == "Minimal" ? 16 : PadH, 0);
            var csv = SettingsService.Current.WidgetCompactMetrics;
            _activeMetrics = (csv ?? "CPU,GPU,RAM,NetUp,NetDown,Battery")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(m => AllMetrics.Contains(m))
                .ToArray();

            if (_activeMetrics.Length == 0)
                _activeMetrics = new[] { "CPU", "GPU", "RAM", "NetUp", "NetDown", "Battery" };

            _scrollOffset = 0;
            RebuildCells();
        }

        private void RebuildCells()
        {
            _metricsPanel.Children.Clear();
            _lblMap.Clear();
            _valMap.Clear();
            _borderMap.Clear();
            _barFillMap.Clear();
            _sparkMap.Clear();
            _gaugeMap.Clear();
            _criticalMetrics.Clear();
            // The "unchanged text" cache is cleared when the cells are rebuilt: new cells start with "--", and a stable value would
            // otherwise never be written until it changes.
            _lastText.Clear();

            // Page slice
            var page = _activeMetrics.Skip(_scrollOffset).Take(MaxVisible).ToArray();

            for (int i = 0; i < page.Length; i++)
            {
                string metric = page[i];
                if (i > 0) AddSeparator();

                var lbl = MakeLbl(MetricLabel(metric));
                var val = MakeVal("--");
                var cell = BuildCell(metric, lbl, val);
                cell.MouseLeftButtonDown += OnCellMouseDown;

                var cellBorder = new Border
                {
                    Child           = cell,
                    BorderBrush     = _bBorderTransparent,
                    BorderThickness = new Thickness(1.5),
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(2)
                };

                _metricsPanel.Children.Add(cellBorder);
                _lblMap[metric] = lbl;
                _valMap[metric] = val;
                _borderMap[metric] = cellBorder;
            }

            // Timer metric (always at end if active)
            if (_timerActive)
            {
                AddSeparator();
                BuildTimerCell();
                _metricsPanel.Children.Add(_timerCell);
            }

            InvalidateMeasure(); UpdateLayout();
            MoveToDefaultPosition();
        }

        private void BuildTimerCell()
        {
            _timerLbl = MakeLbl("TIMER");
            _timerLbl.Foreground = new SolidColorBrush(TimerColor);

            _timerVal = MakeVal("--:--");
            _timerVal.Foreground = new SolidColorBrush(TimerColor);

            _timerBar = new Rectangle
            {
                Height = 3, Fill = new SolidColorBrush(TimerColor),
                RadiusX = 2, RadiusY = 2, HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };

            var barGrid = new Grid { Margin = new Thickness(0, 3, 0, 0), Height = 3 };
            barGrid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(40,255,120,0)), CornerRadius = new CornerRadius(2) });
            barGrid.Children.Add(_timerBar);

            _timerCell = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                Width               = MetricWidth + 10,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor              = Cursors.Hand
            };

            // Content: label + value + bar
            _timerCell.Children.Add(_timerLbl);
            _timerCell.Children.Add(_timerVal);
            _timerCell.Children.Add(barGrid);

            // Hover: dim cell + show ✕ via label text swap
            _timerCell.MouseEnter += (_, _) =>
            {
                _timerLbl.Text = "✕ STOP";
                _timerLbl.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 80, 80));
                _timerVal.Opacity = 0.4;
            };
            _timerCell.MouseLeave += (_, _) =>
            {
                _timerLbl.Text = LanguageService.S("Tools", "LabelTimer");
                _timerLbl.Foreground = new SolidColorBrush(TimerColor);
                _timerVal.Opacity = 1.0;
            };

            // Click anywhere on cell → cancel timer
            _timerCell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CancelShutdown?.Invoke();
                SetTimerActive(false, DateTime.MinValue, 1);
            };
        }

        // ── Timer public API ──────────────────────────────────────────────────
        public void SetTimerActive(bool active, DateTime endTime, double totalSeconds)
        {
            bool wasActive = _timerActive;
            _timerActive    = active;
            _timerEnd       = endTime;
            _timerTotalSecs = Math.Max(1, totalSeconds);

            // Rebuild the UI only when the state changes (active↔inactive), not every second.
            if (active != wasActive)
                RebuildCells();
        }

        private void UpdateTimerCell()
        {
            if (!_timerActive || _timerVal == null) return;
            var remaining = _timerEnd - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                _timerVal.Text = "00:00";
                if (_timerBar != null) _timerBar.Width = 0;
                return;
            }
            _timerVal.Text = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";

            // Progress bar width
            if (_timerBar != null)
            {
                double pct  = Math.Clamp(remaining.TotalSeconds / _timerTotalSecs, 0, 1);
                double maxW = _timerCell?.ActualWidth > 10 ? _timerCell.ActualWidth : MetricWidth + 10;
                _timerBar.Width = pct * maxW;
            }
        }

        // ── Refresh ───────────────────────────────────────────────────────────
        private void StartRefresh()
        {
            _refreshTimer.Tick += (_, _) => { try { UpdateMetrics(); UpdateTimerCell(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } };
            _refreshTimer.Start();
        }

        private void UpdateMetrics()
        {
            // Read battery only every 10s (P/Invoke is expensive)
            if ((DateTime.Now - _batLastRead).TotalSeconds >= 10)
            {
                try
                {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    bool hasBat = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery
                               && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
                    _batPct  = hasBat ? ps.BatteryLifePercent * 100f : -1f;
                    _batSecs = hasBat ? ps.BatteryLifeRemaining : -2f;
                }
                catch { _batPct = -1f; _batSecs = -2f; }
                _batLastRead = DateTime.Now;
            }

            var page = _activeMetrics.Skip(_scrollOffset).Take(MaxVisible).ToArray();
            foreach (string metric in page)
            {
                if (!_valMap.TryGetValue(metric, out var valTb)) continue;
                (string text, Color color) = GetMetricValue(metric);

                UpdateBar(metric);
                UpdateVisuals(metric); // sparkline (Pill) / arc (Gauges)

                // Change detection: skip UI update if nothing changed
                if (_lastText.TryGetValue(metric, out string? last) && last == text) continue;
                _lastText[metric] = text;

                valTb.Text = text;
                // Use frozen cached brushes
                valTb.Foreground = color == HotColor  ? _bHot
                                 : color == WarnColor ? _bWarn
                                 : _bWhite;

                UpdateCriticalBorder(metric, IsMetricCritical(metric));
            }

            if (SettingsService.Current.WidgetAlertOnlyMode)
            {
                bool shouldShow = _criticalMetrics.Count > 0;
                if (shouldShow && !IsVisible) Show();
                else if (!shouldShow && IsVisible) Hide();
            }
            else if (!IsVisible)
            {
                Show();
            }
        }

        private bool IsMetricCritical(string metric) => metric switch
        {
            "CPU"      => _cpuPct  >= 90,
            "GPU"      => _gpuPct  >= 90,
            "RAM"      => _ramPct  >= 90,
            "CPUTemp"  => _cpuTemp.HasValue && _cpuTemp.Value >= 90,
            "GPUTemp"  => _gpuTemp.HasValue && _gpuTemp.Value >= 90,
            "Battery"  => _batPct  >= 0 && _batPct < 10,
            _          => false,
        };

        /// <summary>Starts/stops the pulse animation on the cell outline once, on the state transition (not on every tick) —
        /// avoids restarting the animation and the related flicker.</summary>
        private void UpdateCriticalBorder(string metric, bool critical)
        {
            if (!_borderMap.TryGetValue(metric, out var border)) return;

            if (critical)
            {
                if (_criticalMetrics.Contains(metric)) return; // already pulsing
                _criticalMetrics.Add(metric);
                var pulseBrush = new SolidColorBrush(HotColor);
                border.BorderBrush = pulseBrush;
                var anim = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(0.6))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                pulseBrush.BeginAnimation(SolidColorBrush.OpacityProperty, anim);
            }
            else
            {
                if (!_criticalMetrics.Contains(metric)) return; // already stopped
                _criticalMetrics.Remove(metric);
                border.BorderBrush = _bBorderTransparent;
            }
        }

        private void UpdateBar(string metric)
        {
            if (!BarRanges.TryGetValue(metric, out var range)) return;
            if (!_barFillMap.TryGetValue(metric, out var fill)) return;

            float? raw = metric switch
            {
                "CPU"       => _cpuPct  >= 0 ? _cpuPct  : (float?)null,
                "GPU"       => _gpuPct  >= 0 ? _gpuPct  : (float?)null,
                "RAM"       => _ramPct  >= 0 ? _ramPct  : (float?)null,
                "Battery"   => _batPct  >= 0 ? _batPct  : (float?)null,
                "CPUTemp"   => _cpuTemp,
                "GPUTemp"   => _gpuTemp,
                "NetUp"     => _netUp,
                "NetDown"   => _netDown,
                "DiskRead"  => _diskRead,
                "DiskWrite" => _diskWrite,
                _           => null,
            };
            if (!raw.HasValue) return;

            float rangeMax = metric switch
            {
                "NetUp"     => ScaleService.Observe(ScaleService.KeyNetUp,   raw.Value, 8f * 1024f),
                "NetDown"   => ScaleService.Observe(ScaleService.KeyNetDown, raw.Value, 8f * 1024f),
                "DiskRead"  => ScaleService.Observe(ScaleService.KeyDiskRead("C"),  raw.Value, 8f * 1024f),
                "DiskWrite" => ScaleService.Observe(ScaleService.KeyDiskWrite("C"), raw.Value, 8f * 1024f),
                _           => range.max,
            };

            double frac = Math.Clamp((raw.Value - range.min) / (rangeMax - range.min), 0.0, 1.0);
            double barW = MetricWidth - 4;

            if (metric == "Battery")
            {
                fill.Fill = raw.Value > 20 ? _bBatGreen : raw.Value >= 10 ? _bBatOrange : _bBatRed;
            }

            fill.Clip = new RectangleGeometry(new Rect(0, 0, frac * barW, 4));
        }
        private (string text, Color color) GetMetricValue(string metric)
        {
            switch (metric)
            {
                case "CPU":
                {
                    float p = _cpuPct;
                    if (p < 0) return (_hasReceivedData ? "--" : "·", ValueColor);
                    return ($"{p:F0}%", p >= 85 ? HotColor : p >= 70 ? WarnColor : ValueColor);
                }
                case "CPUFreq":
                {
                    float? mhz = _cpuFreq;
                    string t = mhz.HasValue
                        ? (mhz.Value >= 1000 ? $"{mhz.Value/1000f:F1}G" : $"{mhz.Value:F0}M") : "--";
                    return (t, ValueColor);
                }
                case "CPUTemp":
                {
                    float? t = _cpuTemp;
                    return (t.HasValue ? $"{TempUnit.Deg(t.Value)}" : "--",
                            t.HasValue && t.Value >= 85 ? HotColor
                          : t.HasValue && t.Value >= 70 ? WarnColor : ValueColor);
                }
                case "GPU":
                {
                    float p = _gpuPct;
                    return (p >= 0 ? $"{p:F0}%" : "--",
                            p >= 85 ? HotColor : p >= 70 ? WarnColor : ValueColor);
                }
                case "GPUTemp":
                {
                    float? t = _gpuTemp;
                    return (t.HasValue ? $"{TempUnit.Deg(t.Value)}" : "--",
                            t.HasValue && t.Value >= 85 ? HotColor
                          : t.HasValue && t.Value >= 70 ? WarnColor : ValueColor);
                }
                case "RAM":
                {
                    float p = _ramPct;
                    return (p >= 0 ? $"{p:F0}%" : "--",
                            p >= 85 ? HotColor : p >= 70 ? WarnColor : ValueColor);
                }
                case "Battery":
                {
                    if (_batPct < 0) return ("--", ValueColor);
                    return ($"{_batPct:F0}%",
                            _batPct < 20 ? HotColor : _batPct < 40 ? WarnColor : ValueColor);
                }
                case "BatteryTime":
                {
                    if (_batSecs < -1) return ("--", ValueColor);
                    if (_batSecs < 0) return ("AC", ValueColor);
                    int s = (int)_batSecs;
                    string t = s >= 3600 ? $"{s/3600}h{(s%3600)/60:D2}" : $"{s/60}min";
                    return (t, ValueColor);
                }
                case "NetUp":   return (FormatNet(_netUp),   ValueColor);
                case "NetDown": return (FormatNet(_netDown), ValueColor);
                case "DiskRead":  return (FormatNet(_diskRead),  ValueColor);
                case "DiskWrite": return (FormatNet(_diskWrite), ValueColor);
                default: return ("--", ValueColor);
            }
        }

        // ── PushData from MainWindow polling loop ─────────────────────────────
        private float  _cpuPct = -1f, _gpuPct = -1f, _ramPct = -1f;
        private bool   _hasReceivedData = false;
        private float  _netUp = 0f, _netDown = 0f, _diskRead = 0f, _diskWrite = 0f;
        private float? _cpuTemp, _gpuTemp, _cpuFreq;

        // Cached frozen brushes — avoids GC pressure from new SolidColorBrush every tick
        private static readonly SolidColorBrush _bWhite  = Freeze(new SolidColorBrush(ValueColor));
        private static readonly SolidColorBrush _bWarn   = Freeze(new SolidColorBrush(WarnColor));
        private static readonly SolidColorBrush _bHot    = Freeze(new SolidColorBrush(HotColor));
        private static readonly SolidColorBrush _bBatGreen  = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
        private static readonly SolidColorBrush _bBatOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        private static readonly SolidColorBrush _bBatRed    = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
        private static T Freeze<T>(T obj) where T : System.Windows.Freezable { obj.Freeze(); return obj; }

        // Battery cache — read only every 10s (P/Invoke is expensive)
        private float  _batPct  = -1f;
        private float  _batSecs = -1f;
        private DateTime _batLastRead = DateTime.MinValue;

        // Change detection — skip UI update if value unchanged
        private readonly Dictionary<string, string> _lastText = new();
        private bool   _firstCpuTempLogged = false;
        private readonly System.Diagnostics.Stopwatch _openedStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private bool   _mainWindowPushing = false; // true = MainWindow face polling, timer intern oprit

        public void PushData(float cpuPct, float gpuPct, float ramPct,
            float netUp, float netDown, float diskRead, float diskWrite,
            float? cpuTemp, float? gpuTemp, float? cpuFreqMHz)
        {
            _cpuPct = cpuPct; _gpuPct = gpuPct; _ramPct = ramPct;
            _netUp  = netUp;  _netDown = netDown;
            _diskRead = diskRead; _diskWrite = diskWrite;
            _cpuTemp = cpuTemp; _gpuTemp = gpuTemp; _cpuFreq = cpuFreqMHz;
            _hasReceivedData = true;

            // Diagnostic: how long from opening the widget to the first CPU temperature
            if (!_firstCpuTempLogged && cpuTemp.HasValue)
            {
                _firstCpuTempLogged = true;
                SMDWin.Services.AppLogger.Info($"S-DIAG Widget: first CPU temperature ({cpuTemp.Value:F0}°C) at +{_openedStopwatch.ElapsedMilliseconds}ms after opening");
            }

            // Stop the redundant internal timer — MainWindow does the polling
            if (!_mainWindowPushing)
            {
                _mainWindowPushing = true;
                _refreshTimer.Stop();
            }

            // Direct update (PushData is called on the Dispatcher by OnTempTick)
            try { UpdateMetrics(); UpdateTimerCell(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        // ── Mouse handlers ────────────────────────────────────────────────────
        private DateTime _lastClickTime = DateTime.MinValue;

        private void OnCellMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var now = DateTime.Now;
            if ((now - _lastClickTime).TotalMilliseconds < 400)
            {
                // Double-click → navigate
                if (sender is StackPanel cell && cell.Tag is string metric
                    && MetricNav.TryGetValue(metric, out var page))
                {
                    NavigateTo?.Invoke(page);
                    // Bring main window to front
                    Application.Current.MainWindow?.Show();
                    Application.Current.MainWindow?.Activate();
                    if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                        Application.Current.MainWindow.WindowState = WindowState.Normal;
                }
                _lastClickTime = DateTime.MinValue;
                e.Handled = true;
            }
            else
            {
                _lastClickTime = now;
            }
        }

        private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    DragMove();
                    // After DragMove() returns (drag finished), save the custom position — the same mechanism used by
                    // PinnedProcess/ShutdownTimer.
                    SMDWin.Services.WidgetManager.SavePosition(this);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            int totalPages = (int)Math.Ceiling(_activeMetrics.Length / (double)MaxVisible);
            if (totalPages <= 1) return;
            _scrollOffset = e.Delta < 0
                ? Math.Min(_scrollOffset + MaxVisible, (_activeMetrics.Length - 1) / MaxVisible * MaxVisible)
                : Math.Max(_scrollOffset - MaxVisible, 0);
            RebuildCells();
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu
            {
                Background          = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x27)),
                BorderBrush         = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness     = new Thickness(1),
                UseLayoutRounding   = true,
                SnapsToDevicePixels = true
            };
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);

            var resetItem = new MenuItem { Header = MainWindow.PT("Reset position") };
            resetItem.Click += (_, _) =>
            {
                SMDWin.Services.WidgetManager.ClearPosition(this);
                MoveToDefaultPosition();
            };
            menu.Items.Add(resetItem);

            var timerItem = new MenuItem
            {
                Header = _timerActive ? "Hide timer" : "Show timer",
                IsEnabled = _timerActive
            };
            timerItem.Click += (_, _) =>
            {
                if (_timerActive) SetTimerActive(false, DateTime.MinValue, 1);
            };
            menu.Items.Add(timerItem);

            // quick switch between styles, directly in the menu (not in a submenu: the widget does not take focus, and the submenu
            // did not open / did not receive the click)
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Style", IsEnabled = false });
            foreach (var (key, name) in new[] { ("Gauges", "Gauges"), ("Pill", "Graphs"), ("Minimal", "Compact") })
            {
                var it = new MenuItem { Header = "   " + name, IsCheckable = true, IsChecked = _style == key, StaysOpenOnClick = false };
                string k = key;
                it.Click += (_, _) => SetStyle(k);
                menu.Items.Add(it);
            }

            menu.Items.Add(new Separator());

            var clickItem = new MenuItem { Header = _clickThrough ? "Disable click-through" : "Enable click-through",
                ToolTip = MainWindow.PT("When enabled, mouse clicks pass through the widget") };
            clickItem.Click += (_, _) => EnableClickThrough(!_clickThrough);
            menu.Items.Add(clickItem);

            var closeItem = new MenuItem { Header = MainWindow.PT("Close widget") };
            closeItem.Click += (_, _) => Close();
            menu.Items.Add(closeItem);

            menu.IsOpen = true;
        }

        // ── Position ──────────────────────────────────────────────────────────
        /// <summary>Positions the pill at a saved custom position (if the user has dragged it before and that position is still
        /// valid — see WidgetManager, multi-monitor), otherwise bottom-right on the primary monitor.</summary>
        public void MoveToDefaultPosition()
        {
            var saved = SMDWin.Services.WidgetManager.GetSavedPosition(this);
            if (saved.HasValue && IsPositionOnAnyScreen(saved.Value.X, saved.Value.Y))
            {
                Left = saved.Value.X;
                Top  = saved.Value.Y;
                return;
            }

            var wa = SystemParameters.WorkArea;
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = ActualWidth > 10 ? ActualWidth : (DesiredSize.Width > 10 ? DesiredSize.Width : 600);
            Left = wa.Right  - w  - MarginRight  + ShadowMargin;
            Top  = wa.Bottom - PillHeight - MarginBottom - ShadowMargin;
        }

        private bool IsPositionOnAnyScreen(double x, double y)
        {
            const int margin = 50;
            try
            {
                double scale = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                    scale = src.CompositionTarget.TransformToDevice.M11;
                double px = x * scale, py = y * scale;

                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = screen.WorkingArea;
                    if (px >= wa.Left - margin && px <= wa.Right - margin
                        && py >= wa.Top - margin && py <= wa.Bottom - margin)
                        return true;
                }
            }
            catch { return true; } // if enumerating the screens fails, the widget is not blocked
            return false;
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _refreshTimer.Stop();
        }

        // ── Click-through (Win32) ─────────────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_EXSTYLE       = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private bool _clickThrough = false;

        private void EnableClickThrough(bool enable)
        {
            _clickThrough = enable;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE,
                    enable ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        // ── Opacity ───────────────────────────────────────────────────────────
        public void ApplyOpacity()
        {
            double op = SettingsService.Current.WidgetOpacity;
            if (op < 0.2) op = 1.0;
            Opacity = op;
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private static TextBlock MakeLbl(string text) => new()
        {
            Text                = text,
            FontSize            = 9.5,
            FontWeight          = FontWeights.Normal,
            Foreground          = new SolidColorBrush(LabelColor),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        private static TextBlock MakeVal(string text) => new()
        {
            Text                = text,
            FontSize            = Ui.FontL,
            FontWeight          = FontWeights.SemiBold,
            Foreground          = new SolidColorBrush(ValueColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily          = new FontFamily("Consolas, Courier New, monospace")
        };

        private void AddSeparator() => _metricsPanel.Children.Add(new Rectangle
        {
            Width = SepWidth, Height = _style == "Minimal" ? 14 : _style == "Gauges" ? 34 : 26,
            Fill  = new SolidColorBrush(SepColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(_style == "Minimal" ? 8 : 3, 0, _style == "Minimal" ? 8 : 3, 0),
        });

        // ══════════════════════════════════════════════════════════════════════
        // Three cell styles, chosen in Settings or in the widget menu:
        //   Pill    — small label + value + sparkline (the last ~40 readings) in the metric colour
        //   Gauges  — segmented mini-arc (the same visual language as the dials in Storage / Speed Test)
        //             with the value in the centre, the label below
        //   Minimal — text only on one line ("CPU° 52°"), a narrow capsule; value colour = the threshold
        // Data, thresholds, alerts and the critical pulse are common to all styles.
        // ══════════════════════════════════════════════════════════════════════
        private const int HistoryLen = 40;
        private readonly Dictionary<string, Queue<float>> _hist = new();
        private readonly Dictionary<string, (System.Windows.Shapes.Polyline Line, System.Windows.Shapes.Polygon Area, double W, double H)> _sparkMap = new();
        private readonly Dictionary<string, MiniArcGauge> _gaugeMap = new();
        private readonly Dictionary<string, float> _freqMaxSeen = new();

        private StackPanel BuildCell(string metric, TextBlock lbl, TextBlock val)
        {
            var cell = new StackPanel
            {
                Orientation         = _style == "Minimal" ? Orientation.Horizontal : Orientation.Vertical,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag                 = metric,   // used on double-click (navigation)
                Background          = Brushes.Transparent,
            };

            switch (_style)
            {
                case "Minimal":
                    cell.MinWidth = MetricWidth;
                    lbl.VerticalAlignment = VerticalAlignment.Center;
                    lbl.Margin = new Thickness(0, 1, 6, 0);
                    val.FontSize = 13.5;
                    val.VerticalAlignment = VerticalAlignment.Center;
                    cell.Children.Add(lbl);
                    cell.Children.Add(val);
                    break;

                case "Gauges":
                {
                    cell.Width = MetricWidth;
                    var g = new Grid { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
                    var gauge = new MiniArcGauge { Width = 46, Height = 46 };
                    g.Children.Add(gauge);
                    val.FontSize = 11.5;
                    val.VerticalAlignment = VerticalAlignment.Center;
                    val.Margin = new Thickness(0, 2, 0, 0);
                    g.Children.Add(val);
                    cell.Children.Add(g);
                    lbl.Margin = new Thickness(0, 1, 0, 0);
                    cell.Children.Add(lbl);
                    _gaugeMap[metric] = gauge;
                    break;
                }

                default: // Pill
                {
                    cell.Width = MetricWidth;
                    cell.Children.Add(lbl);
                    val.Margin = new Thickness(0, 0, 0, 1);
                    cell.Children.Add(val);
                    double w = MetricWidth - 8, h = 14;
                    var canvas = new Canvas { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
                    var area = new System.Windows.Shapes.Polygon { StrokeThickness = 0 };
                    var line = new System.Windows.Shapes.Polyline { StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
                    canvas.Children.Add(area);
                    canvas.Children.Add(line);
                    cell.Children.Add(canvas);
                    _sparkMap[metric] = (line, area, w, h);
                    break;
                }
            }

            // History kept between rebuilds: the sparkline does not restart from zero when the style changes
            if (_hist.ContainsKey(metric)) UpdateVisuals(metric, redrawOnly: true);
            return cell;
        }

        /// <summary>The raw numeric value of a metric (null = unavailable).</summary>
        private float? RawValue(string metric) => metric switch
        {
            "CPU"         => _cpuPct  >= 0 ? _cpuPct  : (float?)null,
            "GPU"         => _gpuPct  >= 0 ? _gpuPct  : (float?)null,
            "RAM"         => _ramPct  >= 0 ? _ramPct  : (float?)null,
            "Battery"     => _batPct  >= 0 ? _batPct  : (float?)null,
            "BatteryTime" => _batSecs >= 0 ? _batSecs / 60f : (float?)null,
            "CPUTemp"     => _cpuTemp,
            "GPUTemp"     => _gpuTemp,
            "CPUFreq"     => _cpuFreq,
            "NetUp"       => _netUp,
            "NetDown"     => _netDown,
            "DiskRead"    => _diskRead,
            "DiskWrite"   => _diskWrite,
            _             => null,
        };

        /// <summary>Position 0..1 of the value on the metric scale (for the arc and the sparkline).</summary>
        private double Fraction(string metric, float v)
        {
            if (metric == "CPUFreq")
            {
                float max = Math.Max(_freqMaxSeen.GetValueOrDefault(metric), v);
                _freqMaxSeen[metric] = max;
                return max > 0 ? v / max : 0;
            }
            if (metric == "BatteryTime") return Math.Clamp(v / 300.0, 0, 1);   // 5 h = plin
            if (!BarRanges.TryGetValue(metric, out var r)) return 0;
            float rangeMax = metric switch
            {
                "NetUp"     => ScaleService.Observe(ScaleService.KeyNetUp,   v, 8f * 1024f),
                "NetDown"   => ScaleService.Observe(ScaleService.KeyNetDown, v, 8f * 1024f),
                "DiskRead"  => ScaleService.Observe(ScaleService.KeyDiskRead("C"),  v, 8f * 1024f),
                "DiskWrite" => ScaleService.Observe(ScaleService.KeyDiskWrite("C"), v, 8f * 1024f),
                _           => r.max,
            };
            return Math.Clamp((v - r.min) / (rangeMax - r.min), 0.0, 1.0);
        }

        /// <summary>The metric colour by value (sparkline / arc).</summary>
        private static Color MetricColor(string metric, float v) => metric switch
        {
            "CPUTemp" or "GPUTemp" => v >= 85 ? HotColor : v >= 70 ? WarnColor : Color.FromRgb(0x4A, 0xDE, 0x80),
            "CPU" or "GPU" or "RAM" => v >= 85 ? HotColor : v >= 70 ? WarnColor : Color.FromRgb(0x60, 0xA5, 0xFA),
            "Battery"     => v < 20 ? HotColor : v < 40 ? WarnColor : Color.FromRgb(0x4A, 0xDE, 0x80),
            "BatteryTime" => v < 20 ? HotColor : v < 40 ? WarnColor : Color.FromRgb(0x4A, 0xDE, 0x80),
            "NetDown"     => Color.FromRgb(0x34, 0xD3, 0x99),
            "NetUp"       => Color.FromRgb(0x60, 0xA5, 0xFA),
            "DiskRead"    => Color.FromRgb(0xA7, 0x8B, 0xFA),
            "DiskWrite"   => Color.FromRgb(0xF4, 0x72, 0xB6),
            _             => Color.FromRgb(0x60, 0xA5, 0xFA),
        };

        /// <summary>Adds the current read to the history and redraws the metric's sparkline / arc.</summary>
        private void UpdateVisuals(string metric, bool redrawOnly = false)
        {
            float? raw = RawValue(metric);
            if (!_hist.TryGetValue(metric, out var q)) _hist[metric] = q = new Queue<float>(HistoryLen + 1);
            if (!redrawOnly && raw.HasValue && float.IsFinite(raw.Value))
            {
                q.Enqueue(raw.Value);
                while (q.Count > HistoryLen) q.Dequeue();
            }
            if (q.Count == 0) return;
            float last = q.Last();
            var color = MetricColor(metric, last);

            if (_gaugeMap.TryGetValue(metric, out var gauge))
            {
                gauge.Fraction = raw.HasValue ? Fraction(metric, last) : 0;
                gauge.Accent = color;
                gauge.InvalidateVisual();
            }

            if (_sparkMap.TryGetValue(metric, out var sp))
            {
                var pts = new PointCollection(q.Count + 2);
                // Scale: fixed for percentages / temperatures (the shape does not "jump"), automatic for the rest
                bool fixedScale = BarRanges.ContainsKey(metric) && metric is not ("NetUp" or "NetDown" or "DiskRead" or "DiskWrite");
                float lo = fixedScale ? BarRanges[metric].min : 0f;
                float hi = fixedScale ? BarRanges[metric].max : Math.Max(q.Max() * 1.15f, 1f);
                if (!fixedScale && metric is "CPUFreq" or "BatteryTime") lo = Math.Max(0f, q.Min() * 0.9f);
                double stepX = sp.W / (HistoryLen - 1);
                double x0 = sp.W - (q.Count - 1) * stepX;   // right-aligned, like the app's charts
                int i = 0;
                foreach (var v in q)
                {
                    double f = Math.Clamp((v - lo) / Math.Max(hi - lo, 0.0001f), 0, 1);
                    pts.Add(new System.Windows.Point(x0 + i * stepX, sp.H - 1 - f * (sp.H - 2)));
                    i++;
                }
                sp.Line.Points = pts;
                sp.Line.Stroke = new SolidColorBrush(color);
                var areaPts = new PointCollection(pts) { new System.Windows.Point(pts[^1].X, sp.H), new System.Windows.Point(pts[0].X, sp.H) };
                sp.Area.Points = areaPts;
                sp.Area.Fill = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            }
        }

        /// <summary>A 270° segmented arc, drawn directly (OnRender) — the small variant of SegmentedGauge.</summary>
        internal sealed class MiniArcGauge : FrameworkElement
        {
            private double _target, _shown;   // _shown glides towards _target (animations on), so the arc does not jump
            private bool _gliding;
            private TimeSpan _lastFrame;

            public double Fraction
            {
                get => _target;
                set
                {
                    _target = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
                    bool animate = (SettingsService.Current?.EnableAnimations ?? false) && IsVisible;
                    if (animate && Math.Abs(_target - _shown) >= 0.004)
                    {
                        if (!_gliding) { _gliding = true; _lastFrame = TimeSpan.Zero; CompositionTarget.Rendering += OnGlideFrame; }
                    }
                    else { StopGlide(); _shown = _target; }
                }
            }

            public MiniArcGauge() { Unloaded += (_, _) => { StopGlide(); _shown = _target; }; }

            private void StopGlide()
            {
                if (!_gliding) return;
                _gliding = false;
                CompositionTarget.Rendering -= OnGlideFrame;
            }

            private void OnGlideFrame(object? sender, EventArgs e)
            {
                var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
                if (now == _lastFrame) return;
                double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
                _lastFrame = now;
                _shown += (_target - _shown) * (1 - Math.Exp(-dt / 0.14));
                if (Math.Abs(_target - _shown) < 0.002 || !IsVisible) { _shown = _target; StopGlide(); }
                InvalidateVisual();
            }

            public Color  Accent   { get; set; } = Color.FromRgb(0x60, 0xA5, 0xFA);
            private const int Segments = 18;
            private static readonly SolidColorBrush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)));

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 8 || h < 8) return;
                double cx = w / 2, cy = h / 2 + 1, r = Math.Min(w, h) / 2 - 3.5;
                const double start = 135, sweep = 270, gap = 3.2;
                double seg = sweep / Segments;
                double litF = Math.Clamp(_shown, 0, 1) * Segments;   // segmentul in curs de aprindere: partial transparent
                int lit = (int)Math.Floor(litF);
                double partial = litF - lit;
                System.Windows.Media.Pen? partPen = null;
                if (partial > 0.05)
                {
                    var pb = new SolidColorBrush(Accent) { Opacity = 0.3 + 0.7 * partial };
                    partPen = new System.Windows.Media.Pen(pb, 4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                    partPen.Freeze();
                }
                var litPen   = new System.Windows.Media.Pen(new SolidColorBrush(Accent), 4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                var trackPen = new System.Windows.Media.Pen(TrackBrush, 4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                litPen.Freeze(); trackPen.Freeze();
                for (int i = 0; i < Segments; i++)
                {
                    double a0 = (start + i * seg + gap / 2) * Math.PI / 180;
                    double a1 = (start + (i + 1) * seg - gap / 2) * Math.PI / 180;
                    var p0 = new System.Windows.Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
                    var p1 = new System.Windows.Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(p0, false, false);
                        ctx.ArcTo(p1, new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, i < lit ? litPen : i == lit && partPen != null ? partPen : trackPen, geo);
                }
            }
        }

        private void SetStyle(string style)
        {
            if (style == _style) return;
            SettingsService.Current.WidgetStyle = style;
            SettingsService.Save();
            ApplyMetrics();
            try { UpdateMetrics(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            StyleChanged?.Invoke();
        }

        private static string MetricLabel(string m) => m switch
        {
            "CPU"         => "CPU",
            "CPUFreq"     => "FREQ",
            "CPUTemp"     => "CPU°",
            "GPU"         => "GPU",
            "GPUTemp"     => "GPU°",
            "RAM"         => "RAM",
            "Battery"     => "BAT",
            "BatteryTime" => "TIME",
            "NetUp"       => "↑NET",
            "NetDown"     => "↓NET",
            "DiskRead"    => "R",
            "DiskWrite"   => "W",
            _             => m
        };

        private static string FormatNet(float kbps)
        {
            if (kbps >= 1048576f) return $"{kbps/1024/1024:F1}G";
            if (kbps >= 1024f)        return $"{kbps/1024:F1}M";
            if (kbps >= 1)           return $"{kbps:F0}K";
            return "0K";
        }
    }
}
