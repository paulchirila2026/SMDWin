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

namespace SMDWin.Views
{
    public partial class ProcessAnalyzerWindow : Window
    {
        // The time windows offered on right click on the charts (the same for all four)
        private static readonly int[] ChartWindows = { 60, 300, 900, 3600 };

        private readonly int    _pid;
        private readonly string _procName;
        private readonly DispatcherTimer _timer;
        private readonly NetworkTrafficService _netSvc = new();

        // History: one point per second, at most one hour (how long the window stayed open).
        // Series: 0 CPU %, 1 memory MB, 2 network sent KB/s, 3 network received KB/s, 4 disk read KB/s, 5 disk write KB/s
        private readonly SecondHistory _hist = new(6);
        private const int SerCpu = 0, SerRam = 1, SerNetSend = 2, SerNetRecv = 3, SerDiskRead = 4, SerDiskWrite = 5;

        // Everything is measured by PID. The old "Process" performance counters were opened by NAME: with several processes of
        // the same name (msedge, svchost) they read the first one, not this one, and the instance names shift when one exits.
        private TimeSpan _prevCpu;
        private DateTime _prevCpuAt;
        private ulong _prevRead, _prevWrite;
        private DateTime _prevIoAt;
        private bool _ioPrimed, _cpuPrimed;
        private System.Threading.Tasks.Task? _netRefresh;

        private float _maxCpu  = 0;
        private float _maxRam  = 0;
        private float _sumCpu  = 0;
        private float _totalSentMB  = 0;
        private float _totalRecvMB  = 0;
        private float _totalReadMB  = 0;
        private float _totalWriteMB = 0;

        public ProcessAnalyzerWindow(int pid, string procName)
        {
            _pid      = pid;
            _procName = procName;

            InitializeComponent();
            try { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this); Ui.UseAppScrollBars(this); }
            catch (Exception ex) { AppLogger.Debug($"ProcessAnalyzerWindow ApplicationThemeManager.Apply: {ex.Message}"); }
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            TxtProcName.Text     = procName;
            TxtProcSubtitle.Text = $"PID: {pid}  |  Monitoring…";

            // Extract the real icon of the process (like Task Manager)
            LoadProcessIcon(pid);

            SetupCharts();

            // Prime the per-connection traffic baseline (the first rates appear after the second refresh)
            _netRefresh = System.Threading.Tasks.Task.Run(() => TcpEStatsService.Instance.Refresh());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();
            // Load modules immediately on open
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(500);
                Dispatcher.Invoke(LoadModules);
            });

            Closed += (_, _) =>
            {
                _timer.Stop();
                _netSvc.Dispose();
            };

            Loaded += (_, _) =>
            {
                // Apply title bar color to match current theme
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, SMDWin.Services.SettingsService.Current.ThemeName);
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                void OnThemeChanged()
                {
                    try
                    {
                        var helper2 = new System.Windows.Interop.WindowInteropHelper(this);
                        SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper2.Handle, SMDWin.Services.SettingsService.Current.ThemeName);
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                SMDWin.Services.ThemeManager.ThemeChanged += OnThemeChanged;
                Closed += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged -= OnThemeChanged;
            };
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            float cpu = 0, ramMb = 0;
            float diskReadKBs = 0, diskWriteKBs = 0;

            try
            {
                using var proc = Process.GetProcessById(_pid);
                var now = DateTime.UtcNow;

                // CPU: processor time used by THIS process since the last tick, over all logical processors
                var cpuNow = proc.TotalProcessorTime;
                if (_cpuPrimed)
                {
                    double wallMs = (now - _prevCpuAt).TotalMilliseconds;
                    if (wallMs > 50)
                        cpu = (float)Math.Round(Math.Clamp((cpuNow - _prevCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0, 0, 100), 2);
                }
                _prevCpu = cpuNow; _prevCpuAt = now; _cpuPrimed = true;

                // Memory: private working set, the value Task Manager shows
                ramMb = (float)Math.Round(ProcessMemoryInfo.GetPrivateWorkingSet(proc) / 1_048_576.0, 1);

                // I/O: everything the process reads and writes (files, network, pipes, devices)
                if (ProcessMemoryInfo.TryGetIo(proc, out ulong rd, out ulong wr))
                {
                    if (_ioPrimed)
                    {
                        double secs = (now - _prevIoAt).TotalSeconds;
                        if (secs > 0.05 && rd >= _prevRead && wr >= _prevWrite)
                        {
                            diskReadKBs  = (float)((rd - _prevRead)  / 1024.0 / secs);
                            diskWriteKBs = (float)((wr - _prevWrite) / 1024.0 / secs);
                        }
                    }
                    _prevRead = rd; _prevWrite = wr; _prevIoAt = now; _ioPrimed = true;
                }
            }
            catch (ArgumentException)
            {
                TxtProcSubtitle.Text = $"PID: {_pid}  |  ⚠ Process has ended.";
                _timer.Stop();
                return;
            }
            catch (Exception ex) { AppLogger.Debug($"ProcessAnalyzer tick: {ex.Message}"); }

            // Network: this process only (ETW: TCP + UDP; fallback: its TCP connections) — it used to add up the whole PC's adapters
            float sendKBs = 0, recvKBs = 0;
            try
            {
                if (TcpEStatsService.Instance.TryGetPidTraffic(_pid, out double netS, out double netR, out _, out _))
                {
                    sendKBs = (float)netS;
                    recvKBs = (float)netR;
                }
                if (_netRefresh == null || _netRefresh.IsCompleted)
                    _netRefresh = System.Threading.Tasks.Task.Run(() => TcpEStatsService.Instance.Refresh());
                _totalSentMB += sendKBs / 1024f;
                _totalRecvMB += recvKBs / 1024f;
                TxtNetTotal.Text = $"↑ {_totalSentMB:F1} MB  ↓ {_totalRecvMB:F1} MB total";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ProcessAnalyzer timer tick"); }

            _totalReadMB  += diskReadKBs  / 1024f;
            _totalWriteMB += diskWriteKBs / 1024f;

            // Store samples
            bool firstSample = _hist.Count == 0;
            _hist.Push(DateTime.UtcNow, new[] { cpu, ramMb, sendKBs, recvKBs, diskReadKBs, diskWriteKBs });

            if (cpu   > _maxCpu)  _maxCpu  = cpu;
            if (ramMb > _maxRam)  _maxRam  = ramMb;
            // The average over the displayed time window
            _sumCpu = 0;
            int validCount = 0;
            foreach (double v in _hist.Window(SerCpu, ChartWindowSec()))
                if (double.IsFinite(v)) { _sumCpu += (float)v; validCount++; }

            // Update stat labels
            TxtLiveCpu.Text = $"{cpu:F1}%";
            TxtLiveRam.Text = ramMb >= 1024 ? $"{ramMb/1024:F2} GB" : $"{ramMb:F0} MB";
            TxtMaxCpu.Text  = $"{_maxCpu:F1}%";
            if (TxtAvgCpu != null) TxtAvgCpu.Text = validCount > 0 ? $"avg {_sumCpu/validCount:F1}%" : "";
            TxtMaxRam.Text  = _maxRam >= 1024 ? $"{_maxRam/1024:F2} GB" : $"{_maxRam:F0} MB";

            string FmtRate(float kbs) => kbs >= 1024 ? $"{kbs/1024:F1} MB/s" : $"{kbs:F0} KB/s";
            string FmtTotal(float mb) => mb >= 1024 ? $"{mb/1024:F2} GB total" : $"{mb:F1} MB total";
            TxtLiveNetSend.Text = FmtRate(sendKBs);
            TxtLiveNetRecv.Text = FmtRate(recvKBs);
            if (TxtTotalNetSend != null) TxtTotalNetSend.Text = FmtTotal(_totalSentMB);
            if (TxtTotalNetRecv != null) TxtTotalNetRecv.Text = FmtTotal(_totalRecvMB);

            if (TxtLiveDiskRead != null)
                TxtLiveDiskRead.Text = diskReadKBs  > 0.5f ? FmtRate(diskReadKBs)  : "—";
            TxtLiveDisk.Text = diskWriteKBs > 0.5f ? FmtRate(diskWriteKBs) : "—";
            TxtDiskTotal.Text = $"R: {FmtTotal(_totalReadMB).Replace(" total","")}  W: {FmtTotal(_totalWriteMB).Replace(" total","")}";
            if (TxtTotalDiskRead  != null) TxtTotalDiskRead.Text  = FmtTotal(_totalReadMB);
            if (TxtTotalDiskWrite != null) TxtTotalDiskWrite.Text = FmtTotal(_totalWriteMB);

            TxtSampleCount.Text = $"Samples: {Math.Min(_hist.Stored, ChartWindowSec())} / {ChartWindowSec()}  •  Interval: 1s";
            // Hide loading overlay after first sample arrives
            if (firstSample && LoadingOverlay != null)
                LoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;

            RedrawCharts();
        }

        private static int ChartWindowSec()
        {
            int w = SettingsService.Current.ProcessChartWindowSec;
            return Array.IndexOf(ChartWindows, w) >= 0 ? w : 60;
        }

        /// <summary>The scale of each chart (the rounded upper limit + its label) and the menu with the time window.</summary>
        private void SetupCharts()
        {
            CpuChart.NiceMax = peak => Math.Min(100, Math.Max(10, ChartScale.NiceCeilWithHeadroom(peak)));
            CpuChart.ScaleText = v => $"{v:0}%";
            RamChart.NiceMax = peak => ChartScale.NiceSizeMaxMB(peak);
            RamChart.ScaleText = ChartScale.FormatSizeMB;
            DiskChart.NiceMax = peak => ChartScale.NiceRateMaxKBs(peak);
            DiskChart.ScaleText = ChartScale.FormatRateKBs;
            NetChart.NiceMax = peak => ChartScale.NiceRateMaxKBs(peak);
            NetChart.ScaleText = ChartScale.FormatRateKBs;

            foreach (var chart in new[] { CpuChart, RamChart, DiskChart, NetChart })
                ChartRangeMenu.Attach(chart, ChartWindows, ChartWindowSec, sec =>
                {
                    SettingsService.Current.ProcessChartWindowSec = sec;
                    try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "ProcessChartWindow save"); }
                    RedrawCharts();
                });
            ApplyChartWindowText();
        }

        private void ApplyChartWindowText()
        {
            string text = LanguageService.S("Common", "ChartLast", "Last").ToLowerInvariant() + " " + ChartScale.FormatWindow(ChartWindowSec());
            if (TxtCpuChartRange != null) TxtCpuChartRange.Text = text;
            if (TxtRamChartRange != null) TxtRamChartRange.Text = text;
        }

        private void RedrawCharts()
        {
            if (_hist.Count == 0) return;
            int window = ChartWindowSec();
            CpuChart.ShowHistory(_hist, window, SerCpu);
            RamChart.ShowHistory(_hist, window, SerRam);
            DiskChart.ShowHistory(_hist, window, SerDiskRead, SerDiskWrite);   // read in accent, write grey
            NetChart.ShowHistory(_hist, window, SerNetRecv, SerNetSend);       // primit in accent, trimis gri
            ApplyChartWindowText();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void PinAsWidget_Click(object sender, RoutedEventArgs e)
        {
            // Check if this PID is already pinned — if so, bring that widget to front
            var existing = System.Windows.Application.Current.Windows
                               .OfType<PinnedProcessWindow>()
                               .FirstOrDefault(w => w.Pid == _pid);   // by PID — several processes may have the same name
            if (existing != null)
            {
                existing.Activate();
                return;
            }

            // Offset new widget so they don't stack exactly on top of each other
            int widgetCount = System.Windows.Application.Current.Windows
                                  .OfType<PinnedProcessWindow>().Count();
            var pinned = new PinnedProcessWindow(_pid, _procName);
            SMDWin.Services.WidgetManager.Register(pinned);
            pinned.Show();
        }

        private void LoadProcessIcon(int pid)
        {
            // Run on a separate thread — GetProcessById + GetMainModuleFileName can block
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Windows.Media.ImageSource? icon = null;
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                    if (!string.IsNullOrEmpty(path))
                    {
                        int unboundedBudget = 1;
                        var sign = SMDWin.Services.FileSignatureService.GetSignInfo(path, ref unboundedBudget);
                        Dispatcher.InvokeAsync(() =>
                        {
                            TxtProcSubtitle.Text = sign.Status switch
                            {
                                "signed"    => $"PID: {pid}  |  {(string.IsNullOrWhiteSpace(sign.Publisher) ? "Signed" : sign.Publisher)}",
                                "untrusted" => $"PID: {pid}  |  Signed (untrusted)",
                                "unsigned"  => $"PID: {pid}  |  Unsigned",
                                _           => $"PID: {pid}  |  Monitoring…"
                            };
                        });
                    }

                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        // ExtractAssociatedIcon — the same API Task Manager uses
                        using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (sysIcon != null)
                        {
                            // Convertim System.Drawing.Icon → WPF ImageSource
                            using var bmp = sysIcon.ToBitmap();
                            var hBmp = bmp.GetHbitmap();
                            try
                            {
                                icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hBmp,
                                    IntPtr.Zero,
                                    System.Windows.Int32Rect.Empty,
                                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                                icon.Freeze();
                            }
                            finally
                            {
                                // Release the HBitmap — otherwise a memory leak
                                DeleteObject(hBmp);
                            }
                        }
                    }
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* system processes, access denied — use the fallback */ }

                Dispatcher.InvokeAsync(() =>
                {
                    if (icon != null)
                    {
                        ImgProcIcon.Source = icon;
                    }
                    else
                    {
                        // Fallback: a neutral generic icon from the WPF resources
                        try
                        {
                            ImgProcIcon.Source = null; // hide — a title bar without an icon is fine
                        }
                        catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    }
                });
            });
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string theme = SMDWin.Services.SettingsService.Current.ThemeName;
                string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, resolved);
                if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                    SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        private void TabModules_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ModulesGrid != null) ModulesGrid.Visibility = System.Windows.Visibility.Visible;
            if (ThreadsGrid != null) ThreadsGrid.Visibility = System.Windows.Visibility.Collapsed;
            MainWindow.SetTabButtonActive(BtnTabModules, true);
            MainWindow.SetTabButtonActive(BtnTabThreads, false);
        }

        private void TabThreads_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ModulesGrid != null) ModulesGrid.Visibility = System.Windows.Visibility.Collapsed;
            if (ThreadsGrid != null) ThreadsGrid.Visibility = System.Windows.Visibility.Visible;
            MainWindow.SetTabButtonActive(BtnTabModules, false);
            MainWindow.SetTabButtonActive(BtnTabThreads, true);
            LoadThreads();
        }

        private void RefreshModules_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ModulesGrid?.Visibility == System.Windows.Visibility.Visible) LoadModules();
            else LoadThreads();
        }

        private void LoadModules()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(_pid);
                var modules = new System.Collections.Generic.List<ModuleEntry>();
                foreach (System.Diagnostics.ProcessModule m in proc.Modules)
                {
                    try
                    {
                        var fi = new System.IO.FileInfo(m.FileName);
                        modules.Add(new ModuleEntry
                        {
                            Name     = m.ModuleName,
                            Version  = m.FileVersionInfo?.FileVersion ?? "",
                            Path     = m.FileName,
                            SizeText = fi.Exists ? FormatBytes(fi.Length) : "—"
                        });
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                if (ModulesGrid != null)
                    ModulesGrid.ItemsSource = modules.OrderBy(m => m.Name).ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"LoadModules: {ex.Message}");
            }
        }

        private void LoadThreads()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(_pid);
                var threads = new System.Collections.Generic.List<ThreadEntry>();
                foreach (System.Diagnostics.ProcessThread t in proc.Threads)
                {
                    try
                    {
                        threads.Add(new ThreadEntry
                        {
                            Id            = t.Id,
                            State         = t.ThreadState.ToString(),
                            Priority      = t.PriorityLevel.ToString(),
                            CpuTimeText   = $"{t.TotalProcessorTime.TotalSeconds:F2}s",
                            StartTimeText = FormatStartTime(t.StartTime)
                        });
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                if (ThreadsGrid != null)
                    ThreadsGrid.ItemsSource = threads.OrderBy(t => t.Id).ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"LoadThreads: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1024)      return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        private static string FormatStartTime(DateTime dt)
        {
            try { return dt.ToString("dd.MM HH:mm:ss"); }
            catch { return "—"; }
        }
    }

    // ── Data models for Modules/Threads grids ────────────────────────────────
    public class ModuleEntry
    {
        public string Name     { get; set; } = "";
        public string Version  { get; set; } = "";
        public string Path     { get; set; } = "";
        public string SizeText { get; set; } = "";
    }

    public class ThreadEntry
    {
        public int    Id            { get; set; }
        public string State         { get; set; } = "";
        public string Priority      { get; set; } = "";
        public string CpuTimeText   { get; set; } = "";
        public string StartTimeText { get; set; } = "";
    }
}
