using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin.Services;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace SMDWin.Views
{
    /// <summary>
    /// A floating widget that follows ONE process (by PID): CPU %, RAM, Disk I/O, with a 60 s chart.
    /// Several instances can coexist, each on a different PID.
    ///
    /// Everything is read by PID, from a single handle opened with minimal rights (PROCESS_QUERY_LIMITED_INFORMATION):
    /// GetProcessTimes (CPU), K32GetProcessMemoryInfo (RAM), GetProcessIoCounters (disk), GetExitCodeProcess (process ended).
    /// Performance counters by process name would give the FIRST instance when several processes share a name (chrome,
    /// msedge, svchost), and the "Process" category enumerates ALL processes on every read; the handle calls cost
    /// microseconds. Keeping the handle open also prevents the PID from being reused by another process.
    /// The charts: one geometry (StreamGeometry) per series, on 2 reused Paths.
    /// UI: the PID under the name, the session maximum for CPU / RAM, separate read + write for disk, a "Process ended" state
    /// (the charts stay, the widget fades).
    /// </summary>
    public partial class PinnedProcessWindow : Window
    {
        // ── Constants ──────────────────────────────────────────
        private const int MaxSamples = 60;

        // ── Identification ─────────────────────────────────────
        private int    _pid;
        private string _procName = "";
        public int Pid => _pid;

        // ── Timer ──────────────────────────────────────────────
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        // ── Circular history buffers ───────────────────────────
        private readonly float[] _cpuHist       = new float[MaxSamples];
        private readonly float[] _ramHist       = new float[MaxSamples];
        private readonly float[] _diskReadHist  = new float[MaxSamples];
        private readonly float[] _diskWriteHist = new float[MaxSamples];
        private int _idx;
        private int _count;

        // ── Win32 (a handle with minimal rights) ──────────────────
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS io);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX pmc, int cb);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                           QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        private IntPtr _hProc = IntPtr.Zero;
        private long   _lastCpu100ns = -1;
        private ulong  _lastRead, _lastWrite;
        private long   _lastTick;           // Stopwatch timestamp
        private bool   _ended;

        // ── Chart colors (the widget is always dark, like the compact widget) ──
        private static readonly WpfColor CpuColor   = WpfColor.FromRgb(96, 175, 255);   // #60AFFF
        private static readonly WpfColor RamColor   = WpfColor.FromRgb(52, 211, 153);   // #34D399 (the same as the label)
        private static readonly WpfColor DiskRColor = WpfColor.FromRgb(251, 191, 36);   // #FBBF24 read
        private static readonly WpfColor DiskWColor = WpfColor.FromRgb(251, 146, 60);   // #FB923C scriere

        // ── Peaks ──────────────────────────────────────────────
        private float _maxRam, _maxCpu;

        // ── Chart visuals (created once, reused) ──
        private readonly Dictionary<Canvas, (System.Windows.Shapes.Path fillA, System.Windows.Shapes.Path lineA,
                                             System.Windows.Shapes.Path fillB, System.Windows.Shapes.Path lineB,
                                             Line grid)> _chartParts = new();

        // ═══════════════════════════════════════════════════════
        //  Constructors
        // ═══════════════════════════════════════════════════════

        /// <summary>Creates a widget pinned to a specific process.</summary>
        public PinnedProcessWindow(int pid, string procName) : this()
        {
            AttachToProcess(pid, procName);
        }

        /// <summary>Default constructor (no process yet).</summary>
        public PinnedProcessWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (FindName("CardBorder") is System.Windows.Controls.Border card)
                {
                    card.MouseLeftButtonDown += (s, e) =>
                    {
                        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                        {
                            DragMove();
                            SMDWin.Services.WidgetManager.SavePosition(this);
                        }
                    };
                }
            };
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            ClearHistory();
            SourceInitialized += (_, _) => ApplyTitleBarTheme();
            SizeChanged += (_, _) => RedrawAllCharts();

            _timer.Tick += (_, _) => Sample();
            _timer.Start();
        }

        private void ClearHistory()
        {
            _idx = 0; _count = 0; _maxRam = 0; _maxCpu = 0;
            for (int i = 0; i < MaxSamples; i++)
            {
                _cpuHist[i] = _ramHist[i] = float.NaN;
                _diskReadHist[i] = _diskWriteHist[i] = float.NaN;
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Process management
        // ═══════════════════════════════════════════════════════

        /// <summary>Attach (or re-attach) to a process by PID.</summary>
        public void AttachToProcess(int pid, string procName)
        {
            _pid      = pid;
            _procName = procName;
            _ended    = false;
            Title     = $"Widget — {procName} ({pid})";
            TxtProcessName.Text = procName;
            TxtProcessSub.Text  = $"PID {pid}";
            TxtProcessSub.Foreground = new SolidColorBrush(WpfColor.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            if (FindName("CardBorder") is UIElement cardEl) cardEl.Opacity = 1.0;

            ClearHistory();
            CloseProcHandle();
            _hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (_hProc == IntPtr.Zero)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                AppLogger.Warning($"PinnedProcessWindow: OpenProcess({pid}) failed, Win32 {err}");
                TxtProcessSub.Text = $"PID {pid} · access denied";
                return;
            }
            // The first read = the reference for differences (CPU / disk)
            _lastCpu100ns = ReadCpuTime();
            if (GetProcessIoCounters(_hProc, out var io)) { _lastRead = io.ReadTransferCount; _lastWrite = io.WriteTransferCount; }
            _lastTick = Stopwatch.GetTimestamp();
            if (!_timer.IsEnabled) _timer.Start();
        }


        private long ReadCpuTime()
        {
            if (_hProc == IntPtr.Zero) return -1;
            return GetProcessTimes(_hProc, out _, out _, out long k, out long u) ? k + u : -1;
        }

        private bool ProcessAlive()
        {
            if (_hProc == IntPtr.Zero) return false;
            return GetExitCodeProcess(_hProc, out uint code) && code == STILL_ACTIVE;
        }

        // ═══════════════════════════════════════════════════════
        //  Sampling (every 1s)
        // ═══════════════════════════════════════════════════════

        private void Sample()
        {
            if (_pid <= 0 || _hProc == IntPtr.Zero || _ended) return;

            if (!ProcessAlive())
            {
                MarkEnded();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double dtSec = _lastTick > 0 ? (now - _lastTick) / (double)Stopwatch.Frequency : 1.0;
            if (dtSec <= 0.05) dtSec = 1.0;
            _lastTick = now;

            // ── CPU: processor time difference / (real time × logical cores) — like Task Manager ──
            float cpu = 0;
            long cpuNow = ReadCpuTime();
            if (cpuNow >= 0 && _lastCpu100ns >= 0)
            {
                double used = (cpuNow - _lastCpu100ns) / 1e7;   // secunde
                cpu = (float)Math.Clamp(used / dtSec / Environment.ProcessorCount * 100.0, 0, 100);
            }
            _lastCpu100ns = cpuNow;

            // ── RAM: private working set (the same value as the Memory column in Process Monitor and in Task Manager);
            //    the full working set only if it cannot be read ──
            float ramMb = float.NaN;
            if (SMDWin.Services.ProcessMemoryInfo.GetPrivateWorkingSets().TryGetValue(_pid, out long pws) && pws > 0)
                ramMb = (float)(pws / 1_048_576.0);
            else if (K32GetProcessMemoryInfo(_hProc, out var pmc, System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>()))
                ramMb = (float)(pmc.WorkingSetSize.ToUInt64() / 1_048_576.0);

            // ── Disk I/O: the difference of the transferred bytes (read / write) ──
            float rdKBs = 0, wrKBs = 0;
            if (GetProcessIoCounters(_hProc, out var io))
            {
                rdKBs = (float)((io.ReadTransferCount  - Math.Min(io.ReadTransferCount,  _lastRead))  / 1024.0 / dtSec);
                wrKBs = (float)((io.WriteTransferCount - Math.Min(io.WriteTransferCount, _lastWrite)) / 1024.0 / dtSec);
                _lastRead = io.ReadTransferCount; _lastWrite = io.WriteTransferCount;
            }

            // ── Store in circular buffer ──
            _cpuHist[_idx]       = cpu;
            _ramHist[_idx]       = ramMb;
            _diskReadHist[_idx]  = rdKBs;
            _diskWriteHist[_idx] = wrKBs;
            _idx   = (_idx + 1) % MaxSamples;
            _count = Math.Min(_count + 1, MaxSamples);

            if (!float.IsNaN(ramMb) && ramMb > _maxRam) _maxRam = ramMb;
            if (cpu > _maxCpu) _maxCpu = cpu;

            // ── Labels ──
            TxtCpuVal.Text  = $"{cpu:F1}%";
            TxtCpuPeak.Text = _maxCpu > 0.05f ? $"  max {_maxCpu:F0}%" : "";
            TxtRamVal.Text  = float.IsNaN(ramMb) ? "—" : FormatMb(ramMb);
            TxtRamPeak.Text = _maxRam > 0 ? $"  peak {FormatMb(_maxRam)}" : "";
            float diskTotal = rdKBs + wrKBs;
            TxtDiskVal.Text   = diskTotal >= 1 ? FormatRate(diskTotal) : "0";
            TxtDiskSplit.Text = diskTotal >= 1 ? $"  R {FormatShort(rdKBs)} · W {FormatShort(wrKBs)}" : "";

            RedrawAllCharts();
        }

        private void MarkEnded()
        {
            _ended = true;
            _timer.Stop();
            TxtProcessSub.Text = $"PID {_pid} · process ended";
            TxtProcessSub.Foreground = new SolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44));
            TxtCpuVal.Text = "—"; TxtDiskVal.Text = "—"; TxtDiskSplit.Text = "";
            if (FindName("CardBorder") is UIElement cardEl) cardEl.Opacity = 0.7;
            CloseProcHandle();
        }

        private static string FormatMb(float mb) => mb >= 1024 ? $"{mb / 1024:F1}G" : $"{mb:F0}M";

        private static string FormatShort(float kbs)
            => kbs >= 1024 ? $"{kbs / 1024:F1}M" : $"{kbs:F0}K";

        private static string FormatRate(float kbs)
            => kbs >= 1024 ? $"{kbs / 1024:F1}M/s" : $"{kbs:F0}K/s";

        // ═══════════════════════════════════════════════════════
        //  Chart rendering
        // ═══════════════════════════════════════════════════════

        private void RedrawAllCharts()
        {
            float ramMax  = Math.Max(100, _maxRam * 1.25f);
            float diskMax = 100;

            int start = (_idx - _count + MaxSamples) % MaxSamples;
            for (int i = 0; i < _count; i++)
            {
                int j = (start + i) % MaxSamples;
                float dr = _diskReadHist[j], dw = _diskWriteHist[j];
                if (!float.IsNaN(dr) && dr > diskMax) diskMax = dr;
                if (!float.IsNaN(dw) && dw > diskMax) diskMax = dw;
            }
            diskMax *= 1.2f;

            DrawMiniChart(CpuChart,  _cpuHist,      null,           CpuColor,   CpuColor,   0, 100);
            DrawMiniChart(RamChart,  _ramHist,      null,           RamColor,   RamColor,   0, ramMax);
            DrawMiniChart(DiskChart, _diskReadHist, _diskWriteHist, DiskRColor, DiskWColor, 0, diskMax);
        }

        private (System.Windows.Shapes.Path fillA, System.Windows.Shapes.Path lineA,
                 System.Windows.Shapes.Path fillB, System.Windows.Shapes.Path lineB, Line grid) Parts(Canvas canvas, WpfColor a, WpfColor b)
        {
            if (_chartParts.TryGetValue(canvas, out var p)) return p;
            System.Windows.Shapes.Path Fill(WpfColor c)
            {
                var br = new LinearGradientBrush(WpfColor.FromArgb(55, c.R, c.G, c.B), WpfColor.FromArgb(5, c.R, c.G, c.B),
                                                 new WpfPoint(0, 0), new WpfPoint(0, 1));
                br.Freeze();
                return new System.Windows.Shapes.Path { Fill = br };
            }
            System.Windows.Shapes.Path Stroke(WpfColor c)
            {
                var br = new SolidColorBrush(c); br.Freeze();
                return new System.Windows.Shapes.Path { Stroke = br, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            }
            var gridBrush = new SolidColorBrush(WpfColor.FromArgb(25, 128, 128, 160)); gridBrush.Freeze();
            var grid = new Line { Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
            p = (Fill(a), Stroke(a), Fill(b), Stroke(b), grid);
            canvas.Children.Add(grid);
            canvas.Children.Add(p.fillA); canvas.Children.Add(p.fillB);
            canvas.Children.Add(p.lineA); canvas.Children.Add(p.lineB);
            _chartParts[canvas] = p;
            return p;
        }

        /// <summary>One or two series (area + line) on a Canvas, with reused Paths.</summary>
        private void DrawMiniChart(Canvas canvas, float[] histA, float[]? histB,
                                   WpfColor colorA, WpfColor colorB, float minVal, float maxVal)
        {
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;
            var p = Parts(canvas, colorA, colorB);

            double midY = Math.Round(h / 2) + 0.5;
            p.grid.X1 = 0; p.grid.X2 = w; p.grid.Y1 = midY; p.grid.Y2 = midY;

            float range = maxVal - minVal;
            if (range <= 0) range = 1;
            SetSeries(p.fillA, p.lineA, histA, w, h, minVal, range);
            if (histB != null) SetSeries(p.fillB, p.lineB, histB, w, h, minVal, range);
            else { p.fillB.Data = null; p.lineB.Data = null; }
        }

        private void SetSeries(System.Windows.Shapes.Path fill, System.Windows.Shapes.Path line, float[] hist,
                               double w, double h, float minVal, float range)
        {
            var pts = new List<WpfPoint>(_count);
            int start = (_idx - _count + MaxSamples) % MaxSamples;
            // Right-aligned: the last value is always at the right edge
            double x0 = w - (_count - 1) * (w / (MaxSamples - 1.0));
            for (int i = 0; i < _count; i++)
            {
                int j = (start + i) % MaxSamples;
                float val = hist[j];
                if (float.IsNaN(val)) continue;
                double x = x0 + w * i / (MaxSamples - 1.0);
                double y = Math.Clamp(h - h * (val - minVal) / range, 1, h - 1);
                pts.Add(new WpfPoint(x, y));
            }
            if (pts.Count < 2) { fill.Data = null; line.Data = null; return; }

            var lg = new StreamGeometry();
            using (var c = lg.Open())
            {
                c.BeginFigure(pts[0], false, false);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            lg.Freeze();
            var fg = new StreamGeometry();
            using (var c = fg.Open())
            {
                c.BeginFigure(new WpfPoint(pts[0].X, h), true, true);
                c.PolyLineTo(pts, true, false);
                c.LineTo(new WpfPoint(pts[^1].X, h), true, false);
            }
            fg.Freeze();
            line.Data = lg;
            fill.Data = fg;
        }

        // ═══════════════════════════════════════════════════════
        //  UI events
        // ═══════════════════════════════════════════════════════

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
                SMDWin.Services.WidgetManager.SavePosition(this);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ═══════════════════════════════════════════════════════
        //  Theming
        // ═══════════════════════════════════════════════════════

        private void ApplyTitleBarTheme()
        {
            try
            {
                var hwnd     = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                string theme = SettingsService.Current.ThemeName;
                string resolved = ThemeManager.Normalize(theme);
                ThemeManager.ApplyTitleBarColor(hwnd, resolved);
                if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                    ThemeManager.SetCaptionColor(hwnd, t["BgDark"]);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PinnedProcessWindow.xaml.ApplyTitleBarTheme"); }
        }

        // ═══════════════════════════════════════════════════════
        //  Cleanup
        // ═══════════════════════════════════════════════════════

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            CloseProcHandle();
            base.OnClosed(e);
        }

        private void CloseProcHandle()
        {
            if (_hProc == IntPtr.Zero) return;
            try { CloseHandle(_hProc); } catch (Exception ex) { AppLogger.Debug($"PinnedProcessWindow.CloseHandle: {ex.Message}"); }
            _hProc = IntPtr.Zero;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Process picker dialog (code-only, no XAML) — theme-aware
    // ═══════════════════════════════════════════════════════════════
    public class ProcessPickerWindow : Window
    {
        public int? SelectedPid { get; private set; }
        private readonly System.Windows.Controls.ListBox _list;

        public ProcessPickerWindow()
        {
            Title  = MainWindow.PT("Pick a Process");
            Width  = 340; Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle     = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode      = ResizeMode.NoResize;
            Background      = System.Windows.Media.Brushes.Transparent;
            Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    var resolved = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    ThemeManager.ApplyTitleBarColor(hwnd, resolved);
                    if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                        ThemeManager.SetCaptionColor(hwnd, t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "PinnedProcessWindow.xaml.DisposeCounters"); }
            };

            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };

            var search = new System.Windows.Controls.TextBox
            {
                Margin          = new Thickness(0, 0, 0, 8),
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgCardBrush"),
                Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderBrush     = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
            };

            _list = new System.Windows.Controls.ListBox
            {
                Height      = 300,
                Background  = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgDarkBrush"),
                Foreground  = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBorderBrush"),
                FontSize    = Ui.FontM,
            };

            var entries = Process.GetProcesses()
                .Select(p => { try { return (p.Id, p.ProcessName); } catch { return (-1, ""); } finally { try { p.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } } })
                .Where(x => x.Item1 > 0)
                .OrderBy(x => x.Item2)
                .ToList();

            void Populate(string filter)
            {
                _list.Items.Clear();
                foreach (var (pid, name) in entries)
                {
                    if (!string.IsNullOrEmpty(filter) &&
                        !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    _list.Items.Add($"{name}  (PID {pid})");
                }
            }
            Populate("");
            search.TextChanged += (_, _) => Populate(search.Text);

            var btnRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var btnOk = new System.Windows.Controls.Button
            {
                Content = "Pin", Width = 72, Height = 28,
                Margin  = new Thickness(0, 0, 8, 0),
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush"),
                Foreground      = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
            };
            btnOk.Click += (_, _) =>
            {
                var item  = _list.SelectedItem?.ToString() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(item, @"\(PID (\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                {
                    SelectedPid = pid;
                    DialogResult = true;
                }
            };

            var btnCancel = new System.Windows.Controls.Button
            {
                Content = "Cancel", Width = 72, Height = 28,
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgCardBrush"),
                Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(0),
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };

            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);

            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = MainWindow.PT("Search process:"),
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 4), FontSize = Ui.FontS,
            });
            sp.Children.Add(search);
            sp.Children.Add(_list);
            sp.Children.Add(btnRow);

            var titleBar = new System.Windows.Controls.Grid { Margin = new Thickness(14, 10, 8, 4) };
            titleBar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            titleBar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var titleTb = new System.Windows.Controls.TextBlock
            {
                Text = MainWindow.PT("Pick a Process"), FontSize = Ui.FontL, FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            var btnClose = Net.NetWindowHelper.CloseIconButton(28, Ui.FontM);
            btnClose.Click += (_, _) => { DialogResult = false; Close(); };
            System.Windows.Controls.Grid.SetColumn(btnClose, 1);
            titleBar.Children.Add(titleTb);
            titleBar.Children.Add(btnClose);
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
            sp.Children.Insert(0, titleBar);

            var shell = new System.Windows.Controls.Border
            {
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgDarkBrush"),
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius((System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8),
                ClipToBounds = true,
                Child = sp,
            };
            Content = shell;
        }
    }
}
