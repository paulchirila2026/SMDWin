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
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX_Snap
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX_Snap lpBuffer);

        /// <summary>Builds a SystemSnapshot from the current state (the last cached TempSnapshot + live RAM + free space on the
        /// system drive).</summary>
        private SMDWin.Models.SystemSnapshot BuildCurrentSnapshot(string name)
        {
            var snap = new SMDWin.Models.SystemSnapshot { Name = name, TakenAt = DateTime.Now };

            if (_lastTempSnap != null)
            {
                snap.CpuTempC   = _lastTempSnap.CpuTemp;
                snap.GpuTempC   = _lastTempSnap.GpuTemp;
                snap.CpuLoadPct = _lastTempSnap.CpuLoadPct;
                snap.CpuFreqMHz = _lastTempSnap.CpuFreqMHz;
                if (_lastTempSnap.FanSpeeds.Count > 0)
                    snap.FanRpmAvg = _lastTempSnap.FanSpeeds.Average(f => f.Rpm);

                if (_cpuFreqMin < float.MaxValue) snap.Extra["CpuFreqMinMHz"] = _cpuFreqMin;
                if (_cpuFreqMax > 0)              snap.Extra["CpuFreqMaxMHz"] = _cpuFreqMax;
                if (_lastTempSnap.CpuPowerW.HasValue) snap.Extra["CpuPowerW"] = _lastTempSnap.CpuPowerW.Value;
                if (_lastTempSnap.GpuLoadPct.HasValue) snap.Extra["GpuLoadPct"] = _lastTempSnap.GpuLoadPct.Value;

                if (_dashCpuTempMin > 0) snap.Extra["CpuTempMinC"] = _dashCpuTempMin;
                if (_dashCpuTempMax > 0) snap.Extra["CpuTempMaxC"] = _dashCpuTempMax;
                if (_dashGpuTempMin > 0) snap.Extra["GpuTempMinC"] = _dashGpuTempMin;
                if (_dashGpuTempMax > 0) snap.Extra["GpuTempMaxC"] = _dashGpuTempMax;
            }

            try
            {
                var mem = new MEMORYSTATUSEX_Snap();
                mem.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(mem);
                if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
                {
                    snap.RamUsedPct = mem.dwMemoryLoad;
                    snap.RamUsedGB  = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024f / 1024f / 1024f;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildCurrentSnapshot.RAM"); }

            try
            {
                string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var di = new DriveInfo(sysDrive);
                if (di.IsReady)
                {
                    snap.DiskFreeGB = di.AvailableFreeSpace / 1024f / 1024f / 1024f;
                    snap.DiskLabel  = sysDrive.TrimEnd('\\');
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildCurrentSnapshot.Disk"); }

            return snap;
        }

        private void AutoSaveStressSnapshot(string kind)
        {
            try
            {
                string name = $"Auto — {kind} stress";
                var snap = BuildCurrentSnapshot(name);
                // writing the file and re-reading the list (all the snapshots on disk) happen in the background:
                // the Stop button responds immediately and the charts do not stop
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        SMDWin.Services.SnapshotService.Save(snap);
                        var all = SMDWin.Services.SnapshotService.ListAll();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RenderSnapshotList(all);
                            ShowToast(string.Format(LanguageService.S("Msg", "SnapshotSavedFmt"), name), "📸", 2500);
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, $"AutoSaveStressSnapshot({kind}).save"); }
                });
            }
            catch (Exception ex)
            {
                // Non-blocking — a missed snapshot must not break the stress stop.
                AppLogger.Warning(ex, $"AutoSaveStressSnapshot({kind})");
            }
        }

        private void SaveSnapshot_Click(object sender, RoutedEventArgs e)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox(
                "Snapshot name (e.g. \"Before cleanup\"):",
                "SMDWin — Save Snapshot", "");
            if (string.IsNullOrWhiteSpace(input)) return;

            var snap = BuildCurrentSnapshot(input.Trim());
            try
            {
                SMDWin.Services.SnapshotService.Save(snap);
                SMDWin.Services.ToolActivityLog.Add("Snapshot", $"“{input.Trim()}” saved", "ok");
                ShowToastSuccess(LanguageService.S("Msg", "SnapshotSaved"), 2500);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SaveSnapshot_Click");
                ShowToastWarning(LanguageService.S("Msg", "SnapshotSaveFail"));
            }
            RefreshSnapshotList();
        }

        // the snapshot files are read in the background — the Tools page took 2.7 s to open the first time
        private async void RefreshSnapshotList()
        {
            try
            {
                var all = await System.Threading.Tasks.Task.Run(() => SMDWin.Services.SnapshotService.ListAll());
                RenderSnapshotList(all);
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshSnapshotList: {ex.Message}"); }
        }

        private void RenderSnapshotList(List<(string Path, SMDWin.Models.SystemSnapshot Snapshot)> all)
        {
            if (FindName("SnapshotListPanel") is not StackPanel panel) return;
            panel.Children.Clear();

            if (FindName("TxtNoSnapshots") is TextBlock noneTxt)
                noneTxt.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            const int visibleCount = 3;
            StackPanel? extraPanel = null;

            for (int i = 0; i < all.Count; i++)
            {
                var (path, snap) = all[i];
                var row = BuildSnapshotRow(path, snap);
                if (i < visibleCount)
                {
                    panel.Children.Add(row);
                }
                else
                {
                    extraPanel ??= new StackPanel { Visibility = Visibility.Collapsed };
                    extraPanel.Children.Add(row);
                }
            }

            if (extraPanel != null)
            {
                int extraCount = all.Count - visibleCount;
                panel.Children.Add(extraPanel);

                var toggleBtn = new Button
                {
                    Content = $"Show {extraCount} more",
                    Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FontSize = Ui.FontS,
                };
                toggleBtn.SetResourceReference(Button.ForegroundProperty, "AccentBrush");
                bool expanded = false;
                toggleBtn.Click += (s, e) =>
                {
                    expanded = !expanded;
                    extraPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    toggleBtn.Content = expanded ? "Show less" : $"Show {extraCount} more";
                };
                panel.Children.Add(toggleBtn);
            }
        }

        /// <summary>A row in the Snapshots card: name + date (and "Auto" if saved automatically), Compare, delete.</summary>
        private Border BuildSnapshotRow(string path, SMDWin.Models.SystemSnapshot snap)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            const string autoPrefix = "Auto — ";
            bool isAuto = snap.Name.StartsWith(autoPrefix, StringComparison.Ordinal);
            string displayName = isAuto ? snap.Name[autoPrefix.Length..] : snap.Name;

            var text = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var name = new TextBlock { Text = displayName, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var sub = new TextBlock
            {
                Text = SMDWin.Services.DateFormatHelper.FormatDateTime(snap.TakenAt) + (isAuto ? " · saved automatically" : ""),
                FontSize = Ui.FontXS,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(name);
            text.Children.Add(sub);
            row.Children.Add(text);

            var btnCompare = new Wpf.Ui.Controls.Button
            {
                Content = "Compare", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            btnCompare.Click += (s, e) => ShowSnapshotCompare(snap);
            Grid.SetColumn(btnCompare, 1);
            row.Children.Add(btnCompare);

            var trash = SMDWin.Services.FluentIcons.Make("delete");
            var btnDelete = new Button
            {
                Content = new Viewbox { Width = 14, Height = 14, Child = trash },
                Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Style = TryFindResource("GhostButtonStyle") as Style, ToolTip = MainWindow.PT("Delete snapshot"),
            };
            btnDelete.Click += (s, e) =>
            {
                SMDWin.Services.SnapshotService.Delete(path);
                RefreshSnapshotList();
            };
            Grid.SetColumn(btnDelete, 2);
            row.Children.Add(btnDelete);

            var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = row };
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return wrap;
        }

        private void ShowSnapshotCompare(SMDWin.Models.SystemSnapshot before)
        {
            var now = BuildCurrentSnapshot("Current");

            var accentBrush = TryFindResource("AccentBrush") as Brush ?? System.Windows.Media.Brushes.CornflowerBlue;
            var cardBg       = TryFindResource("BgCardBrush") as Brush ?? (Brush)TryFindResource("BgDarkBrush") ?? System.Windows.Media.Brushes.Black;
            var titleBarBg   = TryFindResource("SidebarAcrylicBrush") as Brush ?? cardBg;
            var borderBrush  = TryFindResource("CardBorderBrush") as Brush ?? System.Windows.Media.Brushes.Gray;
            var textPrimary  = TryFindResource("TextPrimaryBrush") as Brush ?? System.Windows.Media.Brushes.White;
            var textSecondary= TryFindResource("TextSecondaryBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

            var win = new Window
            {
                Title = MainWindow.PT("Snapshot Comparison"),
                Width = 560, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = cardBg,
                AllowsTransparency = false
            };
            win.SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(hwnd, SettingsService.Current.ThemeName);
                    SMDWin.Services.ThemeManager.ApplyRoundedCorners(hwnd, small: true);
                }
                catch (Exception ex) { AppLogger.Debug($"Snapshot title bar: {ex.Message}"); }
            };

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // content

            // ── Custom title bar — replaces the native bar, which did not match the theme ──
            var titleBar = new Grid { Height = 40, Background = titleBarBg };
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleBar.MouseLeftButtonDown += (s, e) => { try { win.DragMove(); } catch (Exception ex) { AppLogger.Debug(ex.Message); } };

            var titleStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            titleStack.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Fill = accentBrush
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = MainWindow.PT("Snapshot Comparison"), FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                Foreground = textPrimary, VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            Grid.SetColumn(titleStack, 0);
            titleBar.Children.Add(titleStack);

            var btnCloseX = Net.NetWindowHelper.CloseIconButton(40, Ui.FontL);
            btnCloseX.Click += (s, e) => win.Close();
            Grid.SetColumn(btnCloseX, 1);
            titleBar.Children.Add(btnCloseX);

            Grid.SetRow(titleBar, 0);
            outer.Children.Add(titleBar);

            // ── Content ──
            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            Grid.SetRow(root, 1);
            outer.Children.Add(root);

            root.Children.Add(new TextBlock
            {
                Text = $"{before.Name}  ·  {SMDWin.Services.DateFormatHelper.FormatDateTime(before.TakenAt)}  →  now",
                FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Foreground = textSecondary,
                Margin = new Thickness(0, 0, 0, 16)
            });

            void Row(string iconColorHex, string label, float? a, float? b, string unit, bool lowerIsBetter, string format = "F1")
            {
                if (unit == "°C") { a = TempUnit.ConvertN(a); b = TempUnit.ConvertN(b); unit = TempUnit.Symbol; }
                if (a == null && b == null) return;
                var card = new Border
                {
                    Background = TryFindResource("BgHoverBrush") as Brush ?? System.Windows.Media.Brushes.Transparent,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var r = new Grid();
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 9, Height = 9, Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Fill = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(iconColorHex)!)
                };
                Grid.SetColumn(iconDot, 0);

                var lbl = new TextBlock { Text = label, FontSize = Ui.FontL, Foreground = textPrimary, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                Grid.SetColumn(lbl, 1);

                string aTxt = a.HasValue && !float.IsNaN(a.Value) ? $"{a.Value.ToString(format)}{unit}" : "—";
                string bTxt = b.HasValue && !float.IsNaN(b.Value) ? $"{b.Value.ToString(format)}{unit}" : "—";
                var vals = new TextBlock
                {
                    Text = $"{aTxt}  →  {bTxt}", FontSize = Ui.FontL, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = textSecondary, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(vals, 2);

                string arrow = "—"; Brush arrowBrush = textSecondary;
                if (a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) > 0.05f)
                {
                    bool improved = lowerIsBetter ? b.Value < a.Value : b.Value > a.Value;
                    arrow = b.Value > a.Value ? "▲" : "▼";
                    arrowBrush = improved
                        ? (TryFindResource("StatusOkBrush") as Brush ?? System.Windows.Media.Brushes.MediumSeaGreen)
                        : (TryFindResource("StatusErrorBrush") as Brush ?? System.Windows.Media.Brushes.IndianRed);
                }
                var arrowTb = new TextBlock
                {
                    Text = arrow, FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                    Foreground = arrowBrush, VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                Grid.SetColumn(arrowTb, 3);

                r.Children.Add(iconDot); r.Children.Add(lbl); r.Children.Add(vals); r.Children.Add(arrowTb);
                card.Child = r;
                root.Children.Add(card);
            }

            Row("#EF4444", "CPU Temperature", before.CpuTempC, now.CpuTempC, "°C", lowerIsBetter: true, format: "F0");
            Row("#EF4444", "CPU Temp (Min)",
                before.Extra.TryGetValue("CpuTempMinC", out var ctMinA) ? ctMinA : (float?)null,
                now.Extra.TryGetValue("CpuTempMinC", out var ctMinB) ? ctMinB : (float?)null,
                "°C", lowerIsBetter: true, format: "F0");
            Row("#EF4444", "CPU Temp (Max)",
                before.Extra.TryGetValue("CpuTempMaxC", out var ctMaxA) ? ctMaxA : (float?)null,
                now.Extra.TryGetValue("CpuTempMaxC", out var ctMaxB) ? ctMaxB : (float?)null,
                "°C", lowerIsBetter: true, format: "F0");
            Row("#F97316", "GPU Temperature", before.GpuTempC, now.GpuTempC, "°C", lowerIsBetter: true, format: "F0");
            Row("#F97316", "GPU Temp (Min)",
                before.Extra.TryGetValue("GpuTempMinC", out var gtMinA) ? gtMinA : (float?)null,
                now.Extra.TryGetValue("GpuTempMinC", out var gtMinB) ? gtMinB : (float?)null,
                "°C", lowerIsBetter: true, format: "F0");
            Row("#F97316", "GPU Temp (Max)",
                before.Extra.TryGetValue("GpuTempMaxC", out var gtMaxA) ? gtMaxA : (float?)null,
                now.Extra.TryGetValue("GpuTempMaxC", out var gtMaxB) ? gtMaxB : (float?)null,
                "°C", lowerIsBetter: true, format: "F0");
            Row("#F59E0B", "CPU Power",
                before.Extra.TryGetValue("CpuPowerW", out var pA) ? pA : (float?)null,
                now.Extra.TryGetValue("CpuPowerW", out var pB) ? pB : (float?)null,
                " W", lowerIsBetter: true);
            Row("#3B82F6", "CPU Freq (Min)",
                before.Extra.TryGetValue("CpuFreqMinMHz", out var fMinA) ? fMinA / 1000f : (float?)null,
                now.Extra.TryGetValue("CpuFreqMinMHz", out var fMinB) ? fMinB / 1000f : (float?)null,
                " GHz", lowerIsBetter: false, format: "F2");
            Row("#2563EB", "CPU Freq (Max)",
                before.Extra.TryGetValue("CpuFreqMaxMHz", out var fMaxA) ? fMaxA / 1000f : (float?)null,
                now.Extra.TryGetValue("CpuFreqMaxMHz", out var fMaxB) ? fMaxB / 1000f : (float?)null,
                " GHz", lowerIsBetter: false, format: "F2");
            Row("#A855F7", "GPU Load",
                before.Extra.TryGetValue("GpuLoadPct", out var gA) ? gA : (float?)null,
                now.Extra.TryGetValue("GpuLoadPct", out var gB) ? gB : (float?)null,
                "%", lowerIsBetter: true, format: "F0");
            Row("#22C55E", $"Disk {before.DiskLabel} Free", before.DiskFreeGB, now.DiskFreeGB, " GB", lowerIsBetter: false, format: "F1");

            var btnClose = new Button
            {
                Content = "Close", Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                Style = TryFindResource("OutlineButtonStyle") as Style
            };
            btnClose.Click += (s, e) => win.Close();
            root.Children.Add(btnClose);

            win.Content = outer;
            win.ShowDialog();
        }
    }
}
