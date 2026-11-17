// RamLivePanel.cs — RAM Live panel: indicator segmentat (PercentGauge) + legenda
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;
using WpfSize             = System.Windows.Size;

namespace SMDWin.Views
{
    public class RamLivePanel : Border
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private float  _loadPct  = 0;
        private double _usedGB   = 0;
        private double _freeGB   = 0;
        private double _cachedGB = 0;
        private double _totalGB  = 0;
        private int    _freqMHz  = 0;

        private System.Diagnostics.PerformanceCounter? _pageFaultsCounter;
        private volatile float _pageFaultsPerSec = 0;
        private bool _tickHooked;
        private int _pfReading;   // 1 = a counter read is running (in the background)
        private TextBlock _lblPressure = null!;

        private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private PercentGauge _gauge    = null!;
        private Ellipse?     _dotUsed;
        private TextBlock _lblPct      = null!;
        private TextBlock _lblUsedVal  = null!;
        private TextBlock _lblFreeVal  = null!;
        private TextBlock _lblCacheVal = null!;
        private TextBlock _lblTotalVal = null!;
        private TextBlock _lblFreq     = null!;

        private static readonly WpfColor UsedColor  = WpfColor.FromRgb(251, 191,  36);  // yellow/amber — Used (more intuitive: "occupied")
        // Free = slate grey — on the gauge the free space is the unlit (grey) arc, not a green slice
        private static readonly WpfColor FreeColor  = WpfColor.FromRgb(148, 163, 184);
        private static readonly WpfColor WarnColor  = WpfColor.FromRgb(249, 115,  22);  // orange — warning
        private static readonly WpfColor HotColor   = WpfColor.FromRgb(239,  68,  68);  // red — critical
        // The "Used" colour follows the gauge zone: the theme accent below 75%, orange 75–90%, red above 90%
        private WpfColor LoadColor =>
            _loadPct >= 90 ? HotColor : _loadPct >= 75 ? WarnColor : AccentColor;
        private static WpfColor AccentColor =>
            (Application.Current?.TryFindResource("DataBrush") as SolidColorBrush)?.Color ?? WpfColor.FromRgb(59, 130, 246);

        private static Brush? Res(string key) =>
            Application.Current?.Resources[key] as Brush;

        public RamLivePanel()
        {
            Margin          = new Thickness(0, 0, 0, 8);
            SetResourceReference(BackgroundProperty, "BgCardBrush");
            SetResourceReference(BorderBrushProperty, "BorderBrush2");
            BorderThickness = new Thickness(1);
            SetResourceReference(CornerRadiusProperty, "RadiusCard");
            Child = BuildUI();
            Loaded   += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private UIElement BuildUI()
        {
            const double CardWidth = 190;
            var outer = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(20, 14, 20, 14),
                MinHeight           = 120,
                Width               = CardWidth
            };

            // A segmented 0–100 % gauge (the same style as Storage Benchmark / Speed Test / widget):
            // the normal zone in the accent colour, 75–90 % orange, above 90 % red
            _gauge = new PercentGauge
            {
                Width = 150, Height = 128,
                HorizontalAlignment = HorizontalAlignment.Center,
                WarnAt = 75, CritAt = 90,
                Caption = "used",
                WarnBrush = new SolidColorBrush(WarnColor),
                CritBrush = new SolidColorBrush(HotColor),
                HasValue = false,
            };
            outer.Children.Add(_gauge);

            _lblPct = new TextBlock
            {
                FontSize   = 32, FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(UsedColor),
                FontFamily = new FontFamily("Segoe UI Light, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin     = new Thickness(0, 8, 0, 4)
            };

            // Used / Free — rows centred under the percentage (only them; Cached is in the card next to it)
            var legend = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var usedRow = MakeLegendRow("Used",  UsedColor, ref _lblUsedVal,  "—");
            if (usedRow is Grid ug && ug.Children.Count > 0 && ug.Children[0] is Ellipse ue) _dotUsed = ue;
            legend.Children.Add(usedRow);
            legend.Children.Add(MakeLegendRow("Free",  FreeColor, ref _lblFreeVal,  "—"));
            outer.Children.Add(legend);

            // Cached — reference (no longer in a separate legend, but the variable is kept)
            var dummyCacheVal = new TextBlock();
            _lblCacheVal = dummyCacheVal;
            var dummyTotalVal = new TextBlock();
            _lblTotalVal = dummyTotalVal;

            // Frequency — under the legend, centred (shown only if available)
            _lblFreq = new TextBlock
            {
                FontSize = Ui.FontS, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _lblFreq.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            outer.Children.Add(_lblFreq);

            _lblPressure = new TextBlock
            {
                FontSize = Ui.FontS, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment       = System.Windows.TextAlignment.Center,
                TextWrapping        = TextWrapping.Wrap,
                MaxWidth            = CardWidth,
                Visibility = Visibility.Hidden,
            };
            outer.Children.Add(_lblPressure);

            return outer;
        }

        private UIElement MakeLegendRow(string label, WpfColor color, ref TextBlock valOut, string initial)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);

            var lbl = new TextBlock { Text = label, FontSize = Ui.FontM, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(lbl, 2); row.Children.Add(lbl);

            valOut = new TextBlock { Text = initial, FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(valOut, 3); row.Children.Add(valOut);

            return row;
        }

        private void RedrawPie()
        {
            // The name left over from the donut variant; now it only updates the gauge.
            _gauge.HasValue = _totalGB > 0;
            _gauge.Value    = _loadPct;
            _gauge.InvalidateVisual();
        }

        private void OnLoaded(object s, RoutedEventArgs e)
        {
            // the Windows counter is created and read in the background: on a busy PC, a read can take seconds (seen as 4.6 s and
            // 8.2 s stalls on the RAM page when it was read on the window thread)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var c = new System.Diagnostics.PerformanceCounter("Memory", "Page Faults/sec", true);
                    c.NextValue();   // the first read is always 0; primes the counter
                    Dispatcher.InvokeAsync(() => { if (_sampleTimer.IsEnabled) _pageFaultsCounter = c; else c.Dispose(); });
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"RamLivePanel: Page Faults/sec counter unavailable ({ex.Message})."); }
            });

            System.Threading.Tasks.Task.Run(ReadFreqAndType).ContinueWith(t =>
                Dispatcher.InvokeAsync(() =>
                {
                    var (freqMHz, ddrType) = t.Result;
                    _freqMHz = freqMHz;
                    _lblFreq.Text = _freqMHz > 0 ? $"{ddrType} @ {_freqMHz} MHz  ·  {_freqMHz*2} MT/s" : "";
                    // If the frequency is not available, hide the line completely (do not show "unavailable")
                    _lblFreq.Visibility = _freqMHz > 0 ? Visibility.Visible : Visibility.Collapsed;
                }));
            // Refresh every second — Sample() updates the values AND redraws the gauge.
            Sample();
            RedrawPie();
            // a single subscription, even if the page loads several times
            if (!_tickHooked) { _tickHooked = true; _sampleTimer.Tick += (_, _) => { Sample(); RedrawPie(); }; }
            _sampleTimer.Start();
        }

        private void OnUnloaded(object s, RoutedEventArgs e)
        {
            _sampleTimer.Stop();
            _pageFaultsCounter?.Dispose();
            _pageFaultsCounter = null;
        }

        private static (int freqMHz, string ddrType) ReadFreqAndType()
        {
            try
            {
                using var sr = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                int max = 0;
                string? smbiosCode = null;
                using (var __wmiTmp1 = sr.Get())
                {
                    foreach (System.Management.ManagementObject o in __wmiTmp1)
                {
                    if (o["ConfiguredClockSpeed"] is uint mhz && mhz > 0)
                    {
                        if (mhz > max)
                        {
                            max = (int)mhz;
                            smbiosCode = o["SMBIOSMemoryType"]?.ToString();
                        }
                    }
                }
                }
                string ddrType = SMDWin.Services.HardwareService.GetMemoryType(smbiosCode, max);
                return (max, ddrType);
            }
            catch { return (0, "DDR"); }
        }

        private void Sample()
        {
            var ms = new MEMORYSTATUSEX { dwLength=(uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if(!GlobalMemoryStatusEx(ref ms)) return;

            _loadPct  = (float)ms.dwMemoryLoad;
            _totalGB  = ms.ullTotalPhys / 1_073_741_824.0;
            _freeGB   = ms.ullAvailPhys / 1_073_741_824.0;

            // dwMemoryLoad = (TotalPhys - AvailPhys) / TotalPhys * 100.
            // _usedGB must be consistent with _loadPct to draw the gauge correctly
            _usedGB   = _totalGB * _loadPct / 100.0;
            // cached = the difference between "physically occupied" and "actually used by processes".
            // Estimate: Available includes Standby (cache) — AvailPhys < TotalPhys - UsedByProcesses
            double physUsed = _totalGB - _freeGB;
            _cachedGB = Math.Max(0, physUsed - _usedGB + (_totalGB - physUsed) * 0);

            WpfColor lc = LoadColor;
            _lblPct.Text       = $"{_loadPct:F0}%";
            _lblPct.Foreground = new SolidColorBrush(lc);
            _lblUsedVal.Text   = $"{_usedGB:F2} GB";
            _lblUsedVal.Foreground = new SolidColorBrush(lc);
            if (_dotUsed != null) _dotUsed.Fill = new SolidColorBrush(lc);
            _lblFreeVal.Text   = $"{_freeGB:F2} GB";
            _lblCacheVal.Text  = _cachedGB > 0.05 ? $"{_cachedGB*1024:F0} MB" : "—";
            _lblTotalVal.Text  = $"{_totalGB:F1} GB";

            try
            {
                if (_pageFaultsCounter != null)
                {
                    // read in the background; the last ready value is shown here
                    var counter = _pageFaultsCounter;
                    if (System.Threading.Interlocked.Exchange(ref _pfReading, 1) == 0)
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { _pageFaultsPerSec = counter.NextValue(); }
                            catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"RamLivePanel: Page Faults/sec ({ex.Message})"); }
                            finally { System.Threading.Interlocked.Exchange(ref _pfReading, 0); }
                        });
                    if (_pageFaultsPerSec >= 1000)
                    {
                        _lblPressure.Text = $"⚠ High memory pressure — {_pageFaultsPerSec:F0} page faults/s";
                        _lblPressure.Foreground = new SolidColorBrush(WarnColor);
                        _lblPressure.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _lblPressure.Visibility = Visibility.Hidden;
                    }
                }
            }
            catch (Exception ex)
            {
                SMDWin.Services.AppLogger.Debug($"RamLivePanel: reading Page Faults/sec failed ({ex.Message}).");
                _lblPressure.Visibility = Visibility.Hidden;
            }
        }
    }
}
