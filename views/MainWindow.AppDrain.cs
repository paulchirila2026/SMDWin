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
    public partial class MainWindow
    {
        // ── BATTERY TEST ──────────────────────────────────────────────────────
        internal void BatteryTest_Click(object s, RoutedEventArgs e)
        {
            var win = new BatteryTestWindow { Owner = this };
            win.Show();
        }

        // ── APP POWER DRAIN ───────────────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _appDrainTimer;
        private System.Windows.Threading.DispatcherTimer? _appDrainRetryTimer;
        // Previous snapshot for the CPU time delta (current usage, not cumulative since boot)
        private Dictionary<int, (long CpuTicks, DateTime Time)> _prevCpuSnapshot = new();

        // Friendly display name lookup (process name → human-readable)
        private static readonly Dictionary<string, string> _procFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge"]          = "Microsoft Edge",
            ["chrome"]          = "Google Chrome",
            ["firefox"]         = "Mozilla Firefox",
            ["opera"]           = "Opera",
            ["brave"]           = "Brave Browser",
            ["iexplore"]        = "Internet Explorer",
            ["code"]            = "Visual Studio Code",
            ["devenv"]          = "Visual Studio",
            ["idea64"]          = "IntelliJ IDEA",
            ["pycharm64"]       = "PyCharm",
            ["rider64"]         = "Rider",
            ["slack"]           = "Slack",
            ["teams"]           = "Microsoft Teams",
            ["outlook"]         = "Microsoft Outlook",
            ["winword"]         = "Microsoft Word",
            ["excel"]           = "Microsoft Excel",
            ["powerpnt"]        = "Microsoft PowerPoint",
            ["onenote"]         = "Microsoft OneNote",
            ["acrobat"]         = "Adobe Acrobat",
            ["acrord32"]        = "Adobe Reader",
            ["photoshop"]       = "Adobe Photoshop",
            ["illustrator"]     = "Adobe Illustrator",
            ["premiere"]        = "Adobe Premiere",
            ["afterfx"]         = "Adobe After Effects",
            ["spotify"]         = "Spotify",
            ["discord"]         = "Discord",
            ["zoom"]            = "Zoom",
            ["telegram"]        = "Telegram",
            ["whatsapp"]        = "WhatsApp",
            ["obs64"]           = "OBS Studio",
            ["obs32"]           = "OBS Studio",
            ["vlc"]             = "VLC Media Player",
            ["wmplayer"]        = "Windows Media Player",
            ["steam"]           = "Steam",
            ["epicgameslauncher"] = "Epic Games Launcher",
            ["notepad"]         = "Notepad",
            ["notepad++"]       = "Notepad++",
            ["explorer"]        = "Windows Explorer",
            ["powershell"]      = "PowerShell",
            ["cmd"]             = "Command Prompt",
            ["windowsterminal"] = "Windows Terminal",
            ["taskmgr"]         = "Task Manager",
            ["mspaint"]         = "Paint",
            ["calculator"]      = "Calculator",
            ["snagit32"]        = "Snagit",
            ["7zfm"]            = "7-Zip",
            ["winrar"]          = "WinRAR",
            ["onedrive"]        = "OneDrive",
            ["dropbox"]         = "Dropbox",
            ["googledrivefs"]   = "Google Drive",
            ["skype"]           = "Skype",
            ["lync"]            = "Skype for Business",
            ["svchost"]         = "Windows Service Host",
            ["wuauclt"]         = "Windows Update",
            ["msiexec"]         = "Windows Installer",
            ["antimalware"]     = "Windows Defender",
            ["msmpeng"]         = "Windows Defender",
            ["searchindexer"]   = "Windows Search Indexer",
            ["searchhost"]      = "Windows Search",
            ["fontdrvhost"]     = "Font Driver Host",
            ["dwm"]             = "Desktop Window Manager",
            ["audiodg"]         = "Windows Audio",
            ["runtimebroker"]   = "Runtime Broker",
            ["startmenuexperiencehost"] = "Start Menu",
            ["shellexperiencehost"]     = "Windows Shell",
            ["ctfmon"]          = "Text Input Processor",
        };

        private static string GetFriendlyName(string processName)
        {
            if (_procFriendlyNames.TryGetValue(processName, out var friendly))
                return friendly;
            // Capitalize first letter and clean up common suffixes
            if (processName.EndsWith("64") || processName.EndsWith("32"))
                processName = processName[..^2];
            if (string.IsNullOrEmpty(processName)) return "Unknown";
            return char.ToUpper(processName[0]) + processName[1..];
        }


        private DateTime _lastAppDrainCallStartUtc = DateTime.MinValue;
        private volatile bool _appDrainRefreshInProgress = false;
        private int _appDrainCallCount = 0;
        private async Task DoRefreshAppDrain()
        {
            if (_appDrainRefreshInProgress)
            {
                AppLogger.Debug("AppDrain: refresh already running — overlapping call ignored.");
                return;
            }
            _appDrainRefreshInProgress = true;
            try
            {
            var callGap = DateTime.UtcNow - _lastAppDrainCallStartUtc;
            if (_lastAppDrainCallStartUtc != DateTime.MinValue && callGap.TotalSeconds < 1.0)
                AppLogger.Warning("AppDrain: called only {0:F2}s after the previous call — risk of overlap.", callGap.TotalSeconds);
            _lastAppDrainCallStartUtc = DateTime.UtcNow;
            AppLogger.Info("AppDrain: refresh #{0} started (prevSnapshotCount={1}).",
                ++_appDrainCallCount, _prevCpuSnapshot.Count);

            if (TxtAppDrainStatus != null) TxtAppDrainStatus.Text = _LS("Common", "Scanning");

            var rawItems = await Task.Run(() =>
            {
                var result = new System.Collections.Generic.List<(string ProcName, string FriendlyName, string Usage, double BarWidth, string Color, double CpuPct)>();
                try
                {
                    var now   = DateTime.UtcNow;
                    var procs = System.Diagnostics.Process.GetProcesses()
                        .Where(p => { try { return !p.HasExited; } catch { return false; } })
                        .ToList();

                    // Computes the CPU time delta against the previous snapshot
                    var currentSnapshot = new Dictionary<int, (long CpuTicks, DateTime Time)>();
                    var deltas = new List<(int Pid, string ProcName, double CpuPct)>();

                    int matchedInPrev = 0, rejectedTooSoon = 0, perProcessErrors = 0;

                    foreach (var p in procs)
                    {
                        try
                        {
                            long ticks = p.TotalProcessorTime.Ticks;
                            currentSnapshot[p.Id] = (ticks, now);

                            if (_prevCpuSnapshot.TryGetValue(p.Id, out var prev))
                            {
                                matchedInPrev++;
                                double elapsedSec = (now - prev.Time).TotalSeconds;
                                if (elapsedSec > 0.5)
                                {
                                    long deltaTicks  = ticks - prev.CpuTicks;
                                    // Normalize to the number of cores → % of a single core
                                    double cpuPct = deltaTicks / (double)TimeSpan.TicksPerSecond
                                                    / elapsedSec
                                                    / Environment.ProcessorCount * 100.0;
                                    string pName = "";
                                    try { if (!p.HasExited) pName = p.ProcessName; } catch { pName = ""; }
                                    if (!string.IsNullOrEmpty(pName))
                                        deltas.Add((p.Id, pName, Math.Max(0, cpuPct)));
                                }
                                else
                                {
                                    rejectedTooSoon++;
                                }
                            }
                        }
                        catch (Exception ex) { perProcessErrors++; AppLogger.Debug(ex.Message); }
                    }

                    bool isFirstEverCall = _prevCpuSnapshot.Count == 0;
                    _prevCpuSnapshot = currentSnapshot;
                    // Release the OS handles after the snapshot
                    foreach (var p in procs) try { p.Dispose(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                    AppLogger.Debug(
                        "AppDrain: scan complet — {0} procese, {1} in snapshot-ul anterior ({2} respinse <0.5s), {3} erori per-proces, {4} delta-uri calculate, isFirstEverCall={5}.",
                        procs.Count, matchedInPrev, rejectedTooSoon, perProcessErrors, deltas.Count, isFirstEverCall);

                    // Without a previous snapshot (first call), show a message
                    if (deltas.Count == 0 && _prevCpuSnapshot.Count > 0)
                    {
                        if (!isFirstEverCall)
                        {
                            AppLogger.Info(
                                "AppDrain: 0 deltas — {0} processes scanned, {1} matched previous snapshot ({2} rejected as too-soon <0.5s), {3} per-process errors.",
                                procs.Count, matchedInPrev, rejectedTooSoon, perProcessErrors);
                        }
                        return result; // will return an empty list → status "Refreshing..."
                    }

                    double maxCpu = deltas.Any() ? deltas.Max(d => d.CpuPct) : 1;
                    if (maxCpu < 0.1) maxCpu = 0.1;

                    var grouped = deltas
                        .GroupBy(d => GetFriendlyName(d.ProcName))
                        .Select(g => (
                            FriendlyName: g.Key,
                            ProcName:     g.OrderByDescending(x => x.CpuPct).First().ProcName,
                            CpuPct:       g.Sum(x => x.CpuPct),
                            Count:        g.Count()
                        ))
                        .ToList();
                    double maxGroupCpu = grouped.Any() ? grouped.Max(g => g.CpuPct) : 1;
                    if (maxGroupCpu < 0.1) maxGroupCpu = 0.1;

                    foreach (var g in grouped.OrderByDescending(x => x.CpuPct).Take(12))
                    {
                        try
                        {
                            double ratio  = g.CpuPct / maxGroupCpu;
                            string level  = g.CpuPct > 5 ? "High" : g.CpuPct > 1 ? "Medium" : "Low";
                            string color  = g.CpuPct > 5 ? "#F87171" : g.CpuPct > 1 ? "#FBBF24" : "#34D399";
                            string displayName = g.Count > 1 ? $"{g.FriendlyName} ({g.Count})" : g.FriendlyName;
                            result.Add((g.ProcName, displayName, level,
                                        Math.Max(4, ratio * 74), color, g.CpuPct));
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Monitor.DoRefreshAppDrain"); }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Monitor.DoRefreshAppDrain"); }
                return result;
            });

            var items = rawItems.Take(13).Select(r =>
            {
                string lvl = r.Usage == "High" ? "High" : r.Usage == "Medium" ? "Med" : "Low";
                string lvlBgHex = r.Usage == "High"   ? "#20EF4444" :
                                  r.Usage == "Medium" ? "#20FBBF24" : "#2034D399";
                System.Windows.Media.Brush? lvlBg = null;
                try { lvlBg = new System.Windows.Media.SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(lvlBgHex)!); } catch (Exception logEx) { AppLogger.Warning(logEx, "lvlBg = new System.Windows.Media.SolidColorBrush((WpfColor)S"); }
                return new AppDrainItem
                {
                    Name        = r.FriendlyName,
                    ProcessName = r.ProcName,
                    Usage       = $"{r.CpuPct:F0}%",
                    UsageLevel  = lvl,
                    BarWidth    = r.BarWidth,
                    BarColor    = new System.Windows.Media.SolidColorBrush(
                        (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(r.Color)!),
                    LevelBg     = lvlBg,
                };
            }).ToList();

            if (LstAppDrain != null) LstAppDrain.ItemsSource = items;
            UpdatePowerTopList(rawItems.Select(r => (r.FriendlyName, r.ProcName, r.CpuPct)).ToList());
            string chosenStatus = items.Count > 0
                ? $"Top {items.Count} processes by current CPU usage — auto-refreshes every 10s  •  double-click for details"
                : _prevCpuSnapshot.Count > 0
                    ? "Refreshing — first reading in progress…"
                    : "No data available";
            if (TxtAppDrainStatus != null) TxtAppDrainStatus.Text = chosenStatus;
            AppLogger.Info("AppDrain: refresh #{0} complet — {1} items afisate, status=\"{2}\".",
                _appDrainCallCount, items.Count, chosenStatus);
            }
            finally { _appDrainRefreshInProgress = false; }
        }

        internal void StartAppDrainAutoRefresh()
        {
            AppLogger.Info("AppDrain: StartAppDrainAutoRefresh() called (timer existed={0}, current items={1}).",
                _appDrainTimer != null, LstAppDrain?.Items.Count ?? -1);
            if (_appDrainTimer != null)
            {
                if ((LstAppDrain?.Items.Count ?? 0) == 0) _ = DoRefreshAppDrain();
                return;
            }
            _appDrainTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _appDrainTimer.Tick += async (_, _) =>
            {
                if (PanelBattery?.Visibility == Visibility.Visible)
                    await DoRefreshAppDrain();
            };
            _appDrainTimer.Start();
            _ = DoRefreshAppDrain(); // immediate first load

            _appDrainRetryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _appDrainRetryTimer.Tick += async (_, _) =>
            {
                _appDrainRetryTimer?.Stop();
                if (_appDrainTimer != null && PanelBattery?.Visibility == Visibility.Visible)
                    await DoRefreshAppDrain();
            };
            _appDrainRetryTimer.Start();
        }

        internal void StopAppDrainAutoRefresh()
        {
            AppLogger.Info("AppDrain: StopAppDrainAutoRefresh() called (timer active={0}).", _appDrainTimer != null);
            _appDrainTimer?.Stop();
            _appDrainTimer = null;
            _appDrainRetryTimer?.Stop();
            _appDrainRetryTimer = null;
        }

        // ── POWERCFG BATTERY REPORT ───────────────────────────────────────────
        /// <summary>
        /// The TOP CONSUMERS card on Power: applications by processor use, as a list with block bars
        /// (like TOP PROCESSES on RAM); as many rows as fit in the card.
        /// </summary>
        private void UpdatePowerTopList(List<(string Name, string Proc, double CpuPct)> apps)
        {
            try
            {
                if (FindName("PowerTopList") is not System.Windows.Controls.StackPanel host) return;
                host.Children.Clear();
                int rows = 8;
                if (host.Parent is System.Windows.Controls.Grid pg && pg.RowDefinitions.Count > 1 && pg.RowDefinitions[1].ActualHeight > 0)
                    rows = Math.Clamp((int)((pg.RowDefinitions[1].ActualHeight - 6) / 31), 4, 12);
                var top = apps.OrderByDescending(a => a.CpuPct).Take(rows).ToList();
                if (top.Count == 0) return;
                double max = Math.Max(0.1, top[0].CpuPct);
                foreach (var (name, proc, cpu) in top)
                {
                    var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 9), ToolTip = string.IsNullOrWhiteSpace(proc) ? null : proc };
                    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(120) });
                    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(56) });
                    var n = new System.Windows.Controls.TextBlock { Text = name, FontSize = Ui.FontM, TextTrimming = TextTrimming.CharacterEllipsis,
                                            VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                    n.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    row.Children.Add(n);

                    var bar = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 12, Height = 7, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    int lit = cpu < 0.5 ? 0 : Math.Clamp((int)Math.Round(cpu / max * 12), 1, 12);
                    string key = cpu > 5 ? "StatusWarnBrush" : "AccentBrush";   // above 5 % it visibly drains the battery
                    for (int i = 0; i < 12; i++)
                    {
                        var b = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, i < 11 ? 2 : 0, 0) };
                        b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, i < lit ? key : "BorderBrush2");
                        bar.Children.Add(b);
                    }
                    System.Windows.Controls.Grid.SetColumn(bar, 1); row.Children.Add(bar);

                    var v = new System.Windows.Controls.TextBlock
                    {
                        Text = $"{cpu:F0}%", FontFamily = TryFindResource("NumFont") as System.Windows.Media.FontFamily, FontSize = 15, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    };
                    v.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    System.Windows.Controls.Grid.SetColumn(v, 2); row.Children.Add(v);
                    host.Children.Add(row);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdatePowerTopList: {ex.Message}"); }
        }

        private class AppDrainItem
        {
            public string Name        { get; set; } = "";
            public string ProcessName { get; set; } = "";
            public string Usage       { get; set; } = "";
            public string UsageLevel  { get; set; } = "";
            public double BarWidth    { get; set; }
            public double BarRatio    => BarWidth / 74.0;  // normalize to 0..1 for ScaleTransform
            public System.Windows.Media.Brush? BarColor  { get; set; }
            public System.Windows.Media.Brush? LevelBg   { get; set; }
        }

        private string? GetSelectedAppDrainProcess()
        {
            if (LstAppDrain?.SelectedItem is AppDrainItem item) return item.ProcessName;
            return null;
        }

        internal void AppDrainGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess();
            if (string.IsNullOrEmpty(procName)) return;
            OpenProcessAnalyzer(procName);
        }

        private bool _appDrainExpanded = false;
        internal void ToggleAppDrainExpand_Click(object sender, RoutedEventArgs e)
        {
            _appDrainExpanded = !_appDrainExpanded;
            if (LstAppDrain != null) LstAppDrain.MaxHeight = _appDrainExpanded ? 2000 : 205;
            if (TxtToggleAppDrainExpand != null) TxtToggleAppDrainExpand.Text = _appDrainExpanded ? "Show less" : "Show all";
        }

        private void OpenProcessAnalyzer(string procName)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(procName);
                if (procs.Length == 0) { AppDialog.Show(string.Format(LanguageService.S("Msg", "ProcessNotFoundFmt"), procName)); return; }
                var win = new SMDWin.Views.ProcessAnalyzerWindow(procs[0].Id, GetFriendlyName(procName)) { Owner = this };
                SMDWin.Views.Ui.FixOwnedWindowMinimize(win, this);
                win.Show();
            }
            catch (Exception ex) { AppDialog.Show(ex.Message); }
        }

        internal void AppDrainDetails_Click(object s, RoutedEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess()
                ?? (s is MenuItem mi ? mi.Tag?.ToString() : null);
            if (string.IsNullOrEmpty(procName)) return;
            OpenProcessAnalyzer(procName);
        }

        internal void AppDrainAnalyze_Click(object s, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Monitor.AppDrainAnalyze_Click"); }
        }

        internal void AppDrainKill_Click(object s, RoutedEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess()
                ?? (s is MenuItem mi2 ? mi2.Tag?.ToString() : null);
            if (string.IsNullOrEmpty(procName)) return;
            string friendly = GetFriendlyName(procName);
            if (!AppDialog.Confirm(string.Format(LanguageService.S("Msg", "KillAllConfirmFmt"), friendly),
                LanguageService.S("Msg", "KillAllTitle"))) return;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
                {
                    try { p.Kill(entireProcessTree: true); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                _ = DoRefreshAppDrain();
            }
            catch (Exception ex) { AppDialog.Show(ex.Message); }
        }
    }
}
