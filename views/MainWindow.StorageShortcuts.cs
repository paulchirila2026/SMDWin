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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ── Quick Shortcut handlers ────────────────────────────────────────────
        internal void ShortcutAutoClean_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isLight = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.SettingsService.Current.ThemeName) ||
                               (SMDWin.Services.SettingsService.Current.ThemeName == "Auto" &&
                                !SMDWin.Services.ThemeManager.IsDark(SMDWin.Services.ThemeManager.ResolveAuto()));
                var dlg = new AutoCleanDialog(isLight)
                {
                    Owner       = this,
                    FormatBytes = FormatBytes,
                    ScanAction  = () => Task.Run(() =>
                    {
                        var sizes = new Dictionary<string, long>();
                        sizes["Temp"]      = GetFolderSize(System.IO.Path.GetTempPath()) + GetFolderSize(@"C:\Windows\Temp");
                        sizes["Prefetch"]  = GetFolderSize(@"C:\Windows\Prefetch");
                        sizes["Thumb"]     = GetFolderSize(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                        sizes["WinUpdate"] = GetFolderSize(@"C:\Windows\SoftwareDistribution\Download");
                        sizes["EventLog"]  = GetFolderSize(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                        sizes["RecycleBin"] = GetRecycleBinSize();
                        return sizes;
                    }),
                    CleanAction = async (selected) =>
                    {
                        long freed = 0;
                        await Task.Run(() =>
                        {
                            if (selected.GetValueOrDefault("Temp"))
                            { freed += DeleteFolder(System.IO.Path.GetTempPath()); freed += DeleteFolder(@"C:\Windows\Temp"); }
                            if (selected.GetValueOrDefault("Prefetch"))
                                freed += DeleteFolder(@"C:\Windows\Prefetch");
                            if (selected.GetValueOrDefault("Thumb"))
                                freed += DeleteFiles(System.IO.Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                            if (selected.GetValueOrDefault("WinUpdate"))
                                freed += DeleteFolder(@"C:\Windows\SoftwareDistribution\Download");
                            if (selected.GetValueOrDefault("EventLog"))
                                freed += DeleteFiles(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                            if (selected.GetValueOrDefault("RecycleBin"))
                                EmptyRecycleBin();
                        });
                        return freed;
                    },
                };
                dlg.ShowDialog();
            }
            catch (Exception ex) { SetShortcutResult($"Could not open Auto Clean: {ex.Message}"); }
        }


        private void SetShortcutResult(string msg)
        {
            if (FindName("TxtShortcutResult") is System.Windows.Controls.TextBlock tb) tb.Text = msg;
        }

        internal async void RunManualTrim_Click(object sender, RoutedEventArgs e)
        {
            if (TxtTrimResult != null) TxtTrimResult.Text = LanguageService.S("Disk", "TrimRunning");
            try
            {
                var lines   = new List<string>();
                int success = 0, failed = 0;

                foreach (var di in await SMDWin.Services.DriveSnapshot.ReadyDrivesAsync())
                {
                    if (di.Type != System.IO.DriveType.Fixed) continue;
                    if (!di.Format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                    string letter = di.Root.TrimEnd('\\', '/');
                    if (letter.Length >= 2 && letter[1] == ':') letter = letter.Substring(0, 2).ToUpper();
                    var trimDisk = _allDisks.FirstOrDefault(d => d.Partitions.Any(p =>
                        (p.Letter ?? "").TrimEnd('\\', ':').Equals(letter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase)));
                    if (trimDisk != null && trimDisk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase)) continue;
                    var (ok, msg) = await DiskAdvancedService.RunManualTrimAsync(letter);

                    // Filter out everything VERBOSE or technical — keep only the useful result
                    var cleanLines = msg.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l) &&
                                    !l.StartsWith("VERBOSE:", StringComparison.OrdinalIgnoreCase) &&
                                    !l.StartsWith("Invoking", StringComparison.OrdinalIgnoreCase) &&
                                    !l.Contains("Invoking retrim", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Prefer the lines with concrete data (Total space / Allocations trimmed)
                    string cleanMsg = cleanLines.FirstOrDefault(l =>
                        l.StartsWith("Total space", StringComparison.OrdinalIgnoreCase) ||
                        l.StartsWith("Allocations", StringComparison.OrdinalIgnoreCase))
                        ?? (ok ? "TRIM completed" : cleanLines.FirstOrDefault() ?? "Failed");

                    if (cleanMsg.Length > 70) cleanMsg = cleanMsg[..70] + "…";
                    lines.Add($"{(ok ? "✓" : "✗")}  {letter}\\  —  {cleanMsg}");
                    if (ok) success++; else failed++;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"TRIM  {DateTime.Now:HH:mm:ss}  —  {success} ok" + (failed > 0 ? $", {failed} failed" : ""));
                sb.AppendLine(new string('─', 38));
                foreach (var l in lines) sb.AppendLine(l);

                if (TxtTrimResult != null)
                    TxtTrimResult.Text = lines.Count > 0
                        ? sb.ToString().TrimEnd()
                        : "No NTFS fixed drives found.";

                // Refresh the TRIM status list — it may have changed after retrim
                try
                {
                    var refreshed = await DiskAdvancedService.GetTrimStatusAsync(_allDisks);
                    var entries = refreshed.Select(t => new TrimDisplayEntry
                    {
                        DriveLetter = t.DriveLetter,
                        Description = t.Description
                    }).ToList();
                    if (TrimList != null) TrimList.ItemsSource = entries;
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
            catch (Exception ex)
            {
                if (TxtTrimResult != null) TxtTrimResult.Text = $"TRIM error: {ex.Message}";
            }
        }

        private CancellationTokenSource? _healthScanCts;

        internal async void RunPartitionHealth_Click(object sender, RoutedEventArgs e)
        {
            // Toggle stop
            if (_healthScanCts != null && !_healthScanCts.IsCancellationRequested)
            {
                _healthScanCts.Cancel();
                return;
            }

            string? letter = CbHealthDrive?.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(letter)) return;

            _healthScanCts?.Dispose();
            _healthScanCts = new CancellationTokenSource();

            if (PbHealthScan  != null) PbHealthScan.Visibility = Visibility.Visible;
            if (TxtHealthScanBtn != null) TxtHealthScanBtn.Text = LanguageService.S("Common", "Stop");
            SetStopLook(FindName("BtnHealthScan") as System.Windows.Controls.Button, running: true);
            if (HealthResultBanner != null) HealthResultBanner.Visibility = Visibility.Collapsed;
            if (TxtHealthStatus != null) TxtHealthStatus.Text = $"Scanning {letter}\\ ...";
            SetNavTaskBadge("TaskBadgeDisk", true);

            var progress = new Progress<string>(msg =>
            {
                if (TxtHealthStatus != null) TxtHealthStatus.Text = msg;
            });

            try
            {
                var result = await DiskAdvancedService.ScanPartitionHealthAsync(
                    letter, progress, _healthScanCts.Token);

                bool ok = result.Status == "Healthy";
                bool hasIssues = result.Status == "Issues Found";

                if (HealthResultBanner != null)
                {
                    HealthResultBanner.Visibility = Visibility.Visible;
                    HealthResultBanner.Background = new SolidColorBrush(
                        ok       ? ThemeColor("StatusOkBgBrush",   WpfColor.FromRgb(0x16, 0x53, 0x2A)) :
                        hasIssues? ThemeColor("StatusCritBgColor", WpfColor.FromRgb(0x7F, 0x1D, 0x1D)) :
                                   ThemeColor("StatusInfoBgColor", WpfColor.FromRgb(0x1E, 0x3A, 0x5F)));
                    HealthResultBanner.BorderBrush = new SolidColorBrush(
                        ok        ? ThemeColor("StatusSuccessColor", WpfColor.FromRgb(34, 197, 94))  :
                        hasIssues ? ThemeColor("StatusErrorColor",   WpfColor.FromRgb(239, 68, 68)) :
                                    ThemeColor("StatusInfoColor",    WpfColor.FromRgb(59, 130, 246)));
                    HealthResultBanner.BorderThickness = new Thickness(1);
                }
                if (TxtHealthIcon  != null) TxtHealthIcon.Text  = ok ? "✅" : result.Status == "Issues Found" ? "❌" : "ℹ";
                if (TxtHealthTitle != null) TxtHealthTitle.Text  = $"{letter}\\ — {result.Status}";
                if (TxtHealthDetails != null) TxtHealthDetails.Text = result.Details;
                if (TxtHealthStatus  != null) TxtHealthStatus.Text = result.Status;
            }
            catch (OperationCanceledException)
            {
                if (TxtHealthStatus != null) TxtHealthStatus.Text = LanguageService.S("Disk", "ScanCancelled");
            }
            catch (Exception ex)
            {
                if (TxtHealthStatus != null) TxtHealthStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message;
            }
            finally
            {
                if (PbHealthScan != null) PbHealthScan.Visibility = Visibility.Collapsed;
                if (TxtHealthScanBtn != null) TxtHealthScanBtn.Text = LanguageService.S("Common", "Scan");
                SetStopLook(FindName("BtnHealthScan") as System.Windows.Controls.Button, running: false, Wpf.Ui.Controls.ControlAppearance.Secondary);
                SetNavTaskBadge("TaskBadgeDisk", false);
            }
        }
    }
}
