using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow : Window
    {
        // ── PROCESS MONITOR ───────────────────────────────────────────────────

        private async Task RefreshProcessesAsync()
        {
            bool firstLoad = _lastProcSnap == null;
            if (firstLoad)
            {
                try { ShowLoading(_LS("Processes", "Loading")); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { SetTabButtonStyles(BtnTabApps, BtnTabBackground, BtnTabAll); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
            try
            {
            // First load: ask for only 20 processes (faster), then 50
            int count = firstLoad ? 20 : 50;
            var _swProc = System.Diagnostics.Stopwatch.StartNew();
            var snapTask = _procMonitor.GetSnapshotAsync(count);
            var winner = await Task.WhenAny(snapTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (winner != snapTask)
            {
                AppLogger.Warning($"ProcessMonitor: GetSnapshotAsync({count}) did not return within 15s — skipping this refresh (check SLOW STEP/SLOW LOOP lines above for the exact PID/call still stuck)");
                return;
            }
            var snap = await snapTask;
            _swProc.Stop();
            if (_swProc.ElapsedMilliseconds > 500)
                AppLogger.Info($"ProcessMonitor GetSnapshotAsync({count}) slow: {_swProc.ElapsedMilliseconds}ms");

            // the first reading of the totals can come back empty (the counters need two samples) while the list already has
            // values: then the processor total is the sum of the list, and the memory waits for the next reading ("…")
            double cpuTotal = snap.TotalCpuPct;
            if (cpuTotal <= 0.05)
            {
                double sum = snap.AllProcesses.Sum(p => (double)p.CpuPct);
                if (sum > 0) cpuTotal = Math.Min(100, sum);
            }
            TxtProcCpuTotal.Text = $"{cpuTotal:F1}%";
            TxtProcRamUsed.Text  = snap.UsedRamMB > 0 ? $"{snap.UsedRamMB / 1024.0:F1} GB" : "…";
            // Round total RAM to nearest standard size
            TxtProcCount.Text    = snap.ProcessCount.ToString();
            _procTotalRamMB = snap.TotalRamMB;

            // Store full snapshot for tab switching / search
            _lastProcSnap = snap;

            // Store history for mini-charts
            _procCpuHistory[_procChartIdx % ProcChartPoints] = snap.TotalCpuPct;
            _procRamHistory[_procChartIdx % ProcChartPoints] = snap.TotalRamMB > 0
                ? snap.UsedRamMB * 100f / snap.TotalRamMB : 0;

            // Disk and network totals: the real disk throughput and adapter traffic. The sum of the per-process values is only a
            // fallback — per-process I/O also counts network and pipe transfers, and per-process network only sees TCP.
            float totalDiskKBs = 0f, totalNetKBs = 0f;
            foreach (var p in snap.AllProcesses)
            {
                totalDiskKBs += p.DiskReadKBs + p.DiskWriteKBs;
                totalNetKBs  += p.NetSentKBs  + p.NetRecvKBs;
            }
            if (snap.TotalDiskKBs >= 0) totalDiskKBs = snap.TotalDiskKBs;
            if (snap.TotalNetKBs  >= 0) totalNetKBs  = snap.TotalNetKBs;
            _procDiskHistory[_procChartIdx % ProcChartPoints] = totalDiskKBs;
            _procNetHistory [_procChartIdx % ProcChartPoints] = totalNetKBs;
            _procChartIdx++;

            if (TxtProcDiskTotal != null)
                TxtProcDiskTotal.Text = totalDiskKBs > 1000 ? $"{totalDiskKBs/1024f:F1} MB/s" : $"{totalDiskKBs:F0} KB/s";
            if (TxtProcNetTotal != null)
                TxtProcNetTotal.Text  = totalNetKBs > 1000 ? $"{totalNetKBs/1024f:F1} MB/s" : $"{totalNetKBs:F0} KB/s";


            float diskMax = Math.Max(10f, _procDiskHistory.Max());
            float netMax  = Math.Max(10f, _procNetHistory.Max());

            DrawProcMiniChart(ProcCpuMiniChart,  _procCpuHistory,  _procChartIdx,
                WpfColor.FromRgb(96, 175, 255), 0, 100);
            DrawProcMiniChart(ProcRamMiniChart,  _procRamHistory,  _procChartIdx,
                WpfColor.FromRgb(46, 229, 90),  0, 100);
            DrawProcMiniChart(ProcDiskMiniChart, _procDiskHistory, _procChartIdx,
                WpfColor.FromRgb(245, 158, 11), 0, diskMax);
            DrawProcMiniChart(ProcNetMiniChart,  _procNetHistory,  _procChartIdx,
                WpfColor.FromRgb(34, 211, 238),  0, netMax);

            UpdateProcSummaryCards(snap, totalDiskKBs, totalNetKBs);

            ApplyProcFilter();
            }
            finally { if (firstLoad) try { HideLoading(); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
        }

        // The history of the top cards (CPU %, memory %, disk KB/s, network KB/s): one point per second while the page is open.
        // The time window is chosen with a right click on any chart and is the same as in a process's details.
        private readonly SMDWin.Services.SecondHistory _procSumHist = new(4);
        private static readonly int[] ProcSummaryWindows = { 60, 300, 900, 3600 };
        private bool _procSumChartsReady;

        private static int ProcSummaryWindowSec()
        {
            int w = SettingsService.Current.ProcessChartWindowSec;
            return Array.IndexOf(ProcSummaryWindows, w) >= 0 ? w : 60;
        }

        private void EnsureProcSummaryCharts()
        {
            if (_procSumChartsReady) return;
            _procSumChartsReady = true;
            ProcCpuChart.Max = 100; ProcRamChart.Max = 100;
            ProcCpuChart.ScaleText = ProcRamChart.ScaleText = v => $"{v:0}%";
            ProcDiskChart.NiceMax = ProcNetChart.NiceMax = peak => SMDWin.Services.ChartScale.NiceRateMaxKBs(peak);
            ProcDiskChart.ScaleText = ProcNetChart.ScaleText = SMDWin.Services.ChartScale.FormatRateKBs;
            foreach (var chart in new[] { ProcCpuChart, ProcRamChart, ProcDiskChart, ProcNetChart })
                ChartRangeMenu.Attach(chart, ProcSummaryWindows, ProcSummaryWindowSec, sec =>
                {
                    SettingsService.Current.ProcessChartWindowSec = sec;
                    try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "ProcSummaryWindow save"); }
                    int w = ProcSummaryWindowSec();
                    ProcCpuChart.ShowHistory(_procSumHist, w, 0);
                    ProcRamChart.ShowHistory(_procSumHist, w, 1);
                    ProcDiskChart.ShowHistory(_procSumHist, w, 2);
                    ProcNetChart.ShowHistory(_procSumHist, w, 3);
                });
        }

        // The top cards: the chart (MiniLineChart) and the biggest consumer for each resource
        private void UpdateProcSummaryCards(ProcessSnapshot snap, float diskKBs, float netKBs)
        {
            try
            {
                EnsureProcSummaryCharts();
                _procSumHist.Push(DateTime.UtcNow, new[]
                {
                    (float)snap.TotalCpuPct,
                    snap.TotalRamMB > 0 ? (float)(snap.UsedRamMB * 100.0 / snap.TotalRamMB) : 0f,
                    diskKBs, netKBs,
                });
                int sumWindow = ProcSummaryWindowSec();
                ProcCpuChart.ShowHistory(_procSumHist, sumWindow, 0);
                ProcRamChart.ShowHistory(_procSumHist, sumWindow, 1);
                ProcDiskChart.ShowHistory(_procSumHist, sumWindow, 2);
                ProcNetChart.ShowHistory(_procSumHist, sumWindow, 3);

                static string Rate(float kbs) => kbs >= 1024 ? $"{kbs / 1024f:F1} MB/s" : $"{kbs:F0} KB/s";
                void Top(TextBlock name, TextBlock val, ProcessEntry? p, string v, string prefix = "Most: ")
                {
                    name.Text = p == null ? "" : prefix + p.Name;
                    val.Text  = p == null ? "" : v;
                }
                var all = snap.AllProcesses;
                var cpu  = all.OrderByDescending(p => p.CpuPct).FirstOrDefault();
                var ram  = all.OrderByDescending(p => p.RamMB).FirstOrDefault();
                var disk = all.OrderByDescending(p => p.DiskTotalKBs).FirstOrDefault(p => p.DiskTotalKBs >= 1);
                var net  = all.OrderByDescending(p => p.NetTotalKBs).FirstOrDefault(p => p.NetTotalKBs >= 1);
                Top(TxtProcCpuTop,  TxtProcCpuTopVal,  cpu,  cpu == null ? "" : cpu.CpuDisplay);
                Top(TxtProcRamTop,  TxtProcRamTopVal,  ram,  ram == null ? "" : ram.RamDisplay);
                // the biggest I/O (files + network + pipes) — not necessarily disk, so it is named that way
                Top(TxtProcDiskTop, TxtProcDiskTopVal, disk, disk == null ? "" : Rate(disk.DiskTotalKBs), "Most I/O: ");
                Top(TxtProcNetTop,  TxtProcNetTopVal,  net,  net == null ? "" : Rate(net.NetTotalKBs));
                if (disk == null) TxtProcDiskTop.Text = T4("Idle", "Inactiv", "Leerlauf", "Inactif");
                if (net == null)  TxtProcNetTop.Text  = T4("Idle", "Inactiv", "Leerlauf", "Inactif");
                if (TxtProcTableSub != null)
                    TxtProcTableSub.Text = PT(_procSortColumn switch
                    {
                        "RamMB" => "Sorted by memory", "DiskTotalKBs" => "Sorted by I/O", "NetTotalKBs" => "Sorted by network",
                        "Name" => "Sorted by name", "Pid" => "Sorted by PID", _ => "Sorted by CPU"
                    }) + " · " + PT("right-click for actions");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UpdateProcSummaryCards"); }
        }

        private static void DrawProcMiniChart(System.Windows.Controls.Canvas canvas,
            float[] history, int writeIdx, WpfColor color, float minVal, float maxVal)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            float range = maxVal - minVal;
            if (range <= 0) range = 1;

            int count = Math.Min(writeIdx, ProcChartPoints);
            if (count < 2) return;

            int startIdx = writeIdx >= ProcChartPoints ? writeIdx % ProcChartPoints : 0;
            var pts = new List<System.Windows.Point>();
            for (int i = 0; i < count; i++)
            {
                float v = history[(startIdx + i) % ProcChartPoints];
                double x = ProcChartPoints > 1 ? w * i / (ProcChartPoints - 1.0) : w;
                double y = h - h * Math.Max(0, Math.Min(1, (v - minVal) / range));
                y = Math.Max(1, Math.Min(h - 1, y));
                pts.Add(new System.Windows.Point(x, y));
            }

            if (pts.Count < 2) return;

            // ── 4.3: 80% threshold dashed line (only for percentage charts 0-100) ──
            if (Math.Abs(minVal) < 0.1f && Math.Abs(maxVal - 100f) < 0.1f)
            {
                double threshY = h - h * 0.80;
                threshY = Math.Max(1, Math.Min(h - 1, threshY));
                var dashLine = new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = threshY, X2 = w, Y2 = threshY,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(120, 251, 191, 36)),
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 }
                };
                canvas.Children.Add(dashLine);
            }

            // Filled area
            var poly = new System.Windows.Shapes.Polygon();
            poly.Points.Add(new System.Windows.Point(pts[0].X, h));
            foreach (var p in pts) poly.Points.Add(p);
            poly.Points.Add(new System.Windows.Point(pts[^1].X, h));
            poly.Fill = new System.Windows.Media.LinearGradientBrush(
                WpfColor.FromArgb(70, color.R, color.G, color.B),
                WpfColor.FromArgb(5,  color.R, color.G, color.B),
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            poly.Stroke = null;
            canvas.Children.Add(poly);

            // Line
            for (int i = 0; i < pts.Count - 1; i++)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = pts[i].X, Y1 = pts[i].Y,
                    X2 = pts[i+1].X, Y2 = pts[i+1].Y,
                    Stroke = new SolidColorBrush(color), StrokeThickness = Ui.ChartStroke
                });
            }

            // ── 4.3: Transparent overlay for hover tooltip with per-point values ──
            var tooltipRect = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = true,
            };
            // Build tooltip with last few values
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(LanguageService.S("Monitor", "RecentValues", "Recent values:"));
            int showCount = Math.Min(count, 8);
            for (int i = Math.Max(0, count - showCount); i < count; i++)
            {
                float v = history[(startIdx + i) % ProcChartPoints];
                string vStr = maxVal <= 100f ? $"{v:F1}%" : (v >= 1024 ? $"{v/1024:F1} MB/s" : $"{v:F0} KB/s");
                sb.AppendLine($"  [{i + 1}] {vStr}");
            }
            tooltipRect.ToolTip = sb.ToString().TrimEnd();
            canvas.Children.Add(tooltipRect);
        }

        // Process monitor tab state
        private ProcessSnapshot? _lastProcSnap;
        private string _currentProcTab = "apps"; // "apps" | "background" | "all" — default: apps
        private const int BackgroundTopCount = 10;
        private int _procBackgroundTotalCount;

        private void TabApps_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "apps";
            SetTabButtonStyles(BtnTabApps, BtnTabBackground, BtnTabAll);
            ApplyProcFilter();
        }
        private void TabBackground_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "background";
            SetTabButtonStyles(BtnTabBackground, BtnTabApps, BtnTabAll);
            ApplyProcFilter();
        }
        private void TabAll_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "all";
            SetTabButtonStyles(BtnTabAll, BtnTabApps, BtnTabBackground);
            ApplyProcFilter();
        }
        private void SetTabButtonStyles(Wpf.Ui.Controls.Button active, params Wpf.Ui.Controls.Button[] inactive)
        {
            SetTabButtonActive(active, true);
            foreach (var b in inactive)
                SetTabButtonActive(b, false);
        }
        private void ProcSearch_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyProcFilter();

        private void ApplyProcFilter()
        {
            if (_lastProcSnap == null || ProcessesGrid == null) return;

            var _swFilter = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed
                && ProcessesGrid.IsMouseOver)
                return;

            var search = TxtProcSearch?.Text?.Trim() ?? "";
            List<ProcessEntry> source = _currentProcTab switch
            {
"apps"=> _lastProcSnap.ForegroundApps,
"background" => _lastProcSnap.BackgroundProcesses,
                _            => _lastProcSnap.AllProcesses
            };
            if (!string.IsNullOrEmpty(search))
                source = source.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            else if (_currentProcTab == "background")
            {
                _procBackgroundTotalCount = source.Count;
                source = source.Take(BackgroundTopCount).ToList();
            }

            // Apply numeric sort
            bool asc = _procSortDir == System.ComponentModel.ListSortDirection.Ascending;
            source = _procSortColumn switch
            {
"CpuPct"=> asc ? source.OrderBy(p => p.CpuPct).ToList()  : source.OrderByDescending(p => p.CpuPct).ToList(),
"RamMB"=> asc ? source.OrderBy(p => p.RamMB).ToList()   : source.OrderByDescending(p => p.RamMB).ToList(),
"Threads" => asc ? source.OrderBy(p => p.Threads).ToList() : source.OrderByDescending(p => p.Threads).ToList(),
"Handles" => asc ? source.OrderBy(p => p.Handles).ToList() : source.OrderByDescending(p => p.Handles).ToList(),
"Pid"=> asc ? source.OrderBy(p => p.Pid).ToList()     : source.OrderByDescending(p => p.Pid).ToList(),
"DiskTotalKBs" => asc ? source.OrderBy(p => p.DiskTotalKBs).ToList() : source.OrderByDescending(p => p.DiskTotalKBs).ToList(),
"NetTotalKBs"  => asc ? source.OrderBy(p => p.NetTotalKBs).ToList()  : source.OrderByDescending(p => p.NetTotalKBs).ToList(),
"Name"=> asc ? source.OrderBy(p => p.Name).ToList()    : source.OrderByDescending(p => p.Name).ToList(),
                _         => source.OrderByDescending(p => p.CpuPct).ToList()
            };

            int selectedPid = (ProcessesGrid.SelectedItem as ProcessEntry)?.Pid ?? -1;

            if (_currentProcTab == "all" && string.IsNullOrEmpty(search))
            {
                var view = new System.Windows.Data.ListCollectionView(source);
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("ProcessGroup"));
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription("ProcessGroup",
                    System.ComponentModel.ListSortDirection.Ascending));
                ProcessesGrid.ItemsSource = view;
            }
            else
            {
                ProcessesGrid.ItemsSource = source;
            }

            if (TxtProcCountLabel != null)
            {
                TxtProcCountLabel.Text = (_currentProcTab == "background" && string.IsNullOrEmpty(search)
                    && _procBackgroundTotalCount > BackgroundTopCount)
                    ? PT("processes — top {0} by usage of {1} background (see \"All\" for everything)", BackgroundTopCount, _procBackgroundTotalCount)
                    : PT("processes");
            }

            if (selectedPid >= 0)
            {
                var again = source.FirstOrDefault(p => p.Pid == selectedPid);
                if (again != null) ProcessesGrid.SelectedItem = again;
            }
            }
            finally
            {
                _swFilter.Stop();
                if (_swFilter.ElapsedMilliseconds > 500)
                    AppLogger.Info($"ApplyProcFilter slow: {_swFilter.ElapsedMilliseconds}ms (tab={_currentProcTab}, rows={_lastProcSnap?.AllProcesses?.Count ?? 0})");
            }
        }

        private int _procTickFailCount = 0;

        private async void OnProcTick(object? s, EventArgs e)
        {
            if (_procTickBusy) return;
            if (PanelProcesses?.Visibility != Visibility.Visible
                || WindowState == WindowState.Minimized) return;
            _procTickBusy = true;
            try
            {
                await RefreshProcessesAsync();
                _procTickFailCount = 0; // reset on success
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "MainWindow.Monitor.OnProcTick");
                if (++_procTickFailCount >= 5)
                {
                    _procTimer.Stop();
                    _ = Dispatcher.InvokeAsync(() => {
                        if (TxtProcessesTitle != null)
                            TxtProcessesTitle.Text = LanguageService.S("Processes", "PausedTitle");
                    });
                }
            }
            finally { _procTickBusy = false; }
        }

        private void KillProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) { AppDialog.Show(_LS("Processes", "SelectProcess")); return; }
            if (!AppDialog.Confirm(string.Format(_LS("Processes", "ConfirmKillFmt"), p.Name, p.Pid),
                _LS("Common", "Confirm"))) return;
            var err = _procMonitor.KillProcess(p.Pid);
            if (err.Length > 0) AppDialog.Show(string.Format(_LS("Common", "ErrorFmt"), err), "SMD Win");
            else _ = RefreshProcessesAsync();
        }

        private void SuspendProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) { AppDialog.Show(_LS("Processes", "SelectProcess")); return; }
            if (!AppDialog.Confirm(string.Format(_LS("Processes", "PauseConfirmFmt"), p.Name, p.Pid),
                _LS("Processes", "PauseTitle"))) return;
            ToolActivityLog.Add("Processes", $"Suspend {p.Name} ({p.Pid})", "info");
            try
            {
                // remembered by PausedProcesses: resumed automatically when SMD Win closes
                SMDWin.Services.PausedProcesses.Pause(p.Pid);
                ShowToastSuccess(string.Format(_LS("Processes", "SuspendedFmt"), p.Name));
                _ = RefreshProcessesAsync();
            }
            catch (Exception ex) { AppDialog.Show(string.Format(_LS("Processes", "CannotSuspendFmt"), ex.Message)); }
        }

        private void ResumeProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) { AppDialog.Show(_LS("Processes", "SelectProcess")); return; }
            try
            {
                SMDWin.Services.PausedProcesses.Resume(p.Pid);
                ShowToastSuccess(string.Format(_LS("Processes", "ResumedFmt"), p.Name));
                _ = RefreshProcessesAsync();
            }
            catch (Exception ex) { AppDialog.Show(string.Format(_LS("Processes", "CannotResumeFmt"), ex.Message)); }
        }

        private void OpenProcessLocation_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) return;
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(p.Pid);
                string? path = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) { AppDialog.Show(LanguageService.S("Msg", "ProcPathUnreadable")); return; }
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "OpenLocationFailFmt"), ex.Message)); }
        }

        private void SelectParentProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) { AppDialog.Show(_LS("Processes", "SelectProcess")); return; }
            if (p.ParentPid <= 0) { AppDialog.Show(LanguageService.S("Msg", "NoParent")); return; }

            var parent = ProcessesGrid.Items.OfType<ProcessEntry>().FirstOrDefault(x => x.Pid == p.ParentPid);
            if (parent == null)
            {
                AppDialog.Show(string.Format(LanguageService.S("Msg", "ParentExitedFmt"), p.ParentPid));
                return;
            }
            ProcessesGrid.SelectedItem = parent;
            ProcessesGrid.ScrollIntoView(parent);
        }

        private void AnalyzeProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p)
            {
                AppDialog.Show(_LS("Processes", "SelectForAnalyze"));
                return;
            }
            var win = new SMDWin.Views.ProcessAnalyzerWindow(p.Pid, p.Name) { Owner = this };
            SMDWin.Views.Ui.FixOwnedWindowMinimize(win, this);
            SMDWin.Services.ThemeManager.Apply(
                SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName,
                win.Resources);
            win.Show();
        }



        private string _procSortColumn = "CpuPct";
        private System.ComponentModel.ListSortDirection _procSortDir = System.ComponentModel.ListSortDirection.Descending;

        private void ProcessesGrid_Sorting(object s, System.Windows.Controls.DataGridSortingEventArgs e)
        {
            e.Handled = true; // prevent default string sort

            string col = e.Column.SortMemberPath ?? e.Column.Header?.ToString() ?? "";

            // Toggle direction if same column
            if (col == _procSortColumn)
                _procSortDir = _procSortDir == System.ComponentModel.ListSortDirection.Ascending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending;
            else
            {
                _procSortColumn = col;
                _procSortDir = System.ComponentModel.ListSortDirection.Descending;
            }

            // Update column header sort direction indicators
            foreach (var c in ProcessesGrid.Columns)
                c.SortDirection = null;
            e.Column.SortDirection = _procSortDir;

            ApplyProcFilter(); // re-apply with new sort
        }

        private void ProcessesGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is ProcessEntry p)
            {
                var win = new SMDWin.Views.ProcessAnalyzerWindow(p.Pid, p.Name) { Owner = this };
                SMDWin.Views.Ui.FixOwnedWindowMinimize(win, this);
                SMDWin.Services.ThemeManager.Apply(
                    SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                        ? SMDWin.Services.ThemeManager.ResolveAuto()
                        : SMDWin.Services.SettingsService.Current.ThemeName,
                    win.Resources);
                win.Show();
            }
        }

        private void UpdateChipSelection(System.Windows.Controls.Panel parent, string selectedTag, bool alwaysBordered = false)
        {
            foreach (var child in parent.Children)
            {
                if (child is Wpf.Ui.Controls.Button btn)
                {
                    bool sel = btn.Tag?.ToString() == selectedTag;
                    SetTabButtonActive(btn, sel);
                }
            }
        }

        internal void ProcRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            double secs = double.TryParse(btn.Tag?.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 1.0;
            if (TxtProcRefreshVal != null) TxtProcRefreshVal.Text = $"{secs}s";
            SettingsService.Current.ProcessRefreshSec = (int)Math.Ceiling(secs);
            SettingsService.Save();
            if (_procTimer.IsEnabled)
                _procTimer.Interval = TimeSpan.FromSeconds(secs);
            // Highlight selected chip
            if (btn.Parent is System.Windows.Controls.Panel panel)
                UpdateChipSelection(panel, btn.Tag?.ToString() ?? "");
        }

        internal void TempRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            // Supports RadioButton (new) and Button (legacy)
            string? tagStr = sender is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                           : sender is Button btn2 ? btn2.Tag?.ToString() : null;
            double secs = double.TryParse(tagStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 2.0;
            if (TxtTempRefreshVal != null) TxtTempRefreshVal.Text = $"{secs}s";
            SettingsService.Current.RefreshInterval = secs;
            SettingsService.Save();
            _tempTimer.Interval = TimeSpan.FromSeconds(secs);
        }

        internal void NetRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb) return;
            if (!int.TryParse(rb.Tag?.ToString(), out int secs)) secs = 2;
            SettingsService.Current.NetworkRefreshSec = secs;
            SettingsService.Save();
            if (_trafficTimer.IsEnabled)
                _trafficTimer.Interval = TimeSpan.FromSeconds(secs);
            if (_netPageTimer != null) _netPageTimer.Interval = TimeSpan.FromSeconds(NetRefreshSeconds());   // the Network page
            ApplyNetRefreshToChart();
        }

        internal void BatRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb) return;
            if (!int.TryParse(rb.Tag?.ToString(), out int secs)) secs = 10;
            SettingsService.Current.BatteryRefreshSec = secs;
            SettingsService.Save();
            if (_batTimer.IsEnabled)
                _batTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(secs, 5, 15));
        }
    }
}
