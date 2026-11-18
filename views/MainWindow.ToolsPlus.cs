using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color   = System.Windows.Media.Color;
using HAlign  = System.Windows.HorizontalAlignment;
using VAlign  = System.Windows.VerticalAlignment;
using UiButton = Wpf.Ui.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;

namespace SMDWin.Views
{
    /// <summary>
    /// Tools, stage 3 (mockup "tools3"): the cards grouped by what has to be done —
    ///   Repair Windows: SFC and DISM run inside the page (progress, time left, the result in plain words), plus
    ///     "Reset Windows Update" and "Reset print queue";
    ///   Clean up: plus "Old Windows components" (DISM StartComponentCleanup, also in the page);
    ///   Before a reinstall or repair: restore point (with the date of the last one), driver back-up, BitLocker state
    ///     and recovery key, then the diagnostic package;
    ///   Tests &amp; reports: Windows memory test, battery report, USB monitor;
    ///   History (full width, instead of "Recent activity"): the service changes with Undo and everything run from Tools.
    /// The page itself stays in MainWindow.xaml; the new rows and the new card are added here, once.
    /// </summary>
    public partial class MainWindow
    {
        private bool _toolsPlusDone;
        private StackPanel? _toolsRepairStack, _toolsCleanStack;
        private TextBlock? _toolsRestoreSub, _toolsBitLockerSub;

        /// <summary>Called when Tools is shown: adds the new rows and the Tests card (only the first time).</summary>
        private void EnsureToolsPlus()
        {
            if (_toolsPlusDone) return;
            if (FindName("ToolsCardsGrid") is not Grid grid || grid.Children.Count < 6) return;
            _toolsPlusDone = true;
            try
            {
                var cells = grid.Children.Cast<UIElement>().ToList();
                _toolsRepairStack = CardStack(cells[0]);
                _toolsCleanStack = CardStack(cells[1]);
                var packageStack = CardStack(cells[2]);

                // ── Repair: SFC / DISM in the page, two new resets ──
                if (FindName("BtnCmdSfc") is UiButton sfc) { sfc.Click -= WinCmd_Click; sfc.Click += async (_, _) => await RunRepairAsync(RepairKind.Sfc, sfc); }
                if (FindName("BtnCmdDism") is UiButton dism) { dism.Click -= WinCmd_Click; dism.Click += async (_, _) => await RunRepairAsync(RepairKind.Dism, dism); }
                if (_toolsRepairStack != null)
                {
                    int at = Math.Min(_toolsRepairStack.Children.Count, 3);   // after SFC and DISM
                    _toolsRepairStack.Children.Insert(at, ToolsRow("\uE895", PT("Reset Windows Update"), PT("when updates are stuck or fail again and again"), PT("Reset"), async (b) => await ResetWindowsUpdateAsync(b)));
                    _toolsRepairStack.Children.Insert(at + 1, ToolsRow("\uE749", PT("Reset print queue"), PT("stuck print jobs · restarts the Print Spooler"), PT("Reset"), async (b) => await ResetPrintQueueAsync(b)));
                }

                // ── Clean up: old Windows components ──
                if (_toolsCleanStack != null)
                    _toolsCleanStack.Children.Insert(Math.Min(_toolsCleanStack.Children.Count, 3),
                        ToolsRow("\uE74D", PT("Old Windows components"), PT("left behind by updates · usually frees 1–5 GB · 5–20 min"), PT("Clean"), async (b) => await RunRepairAsync(RepairKind.Components, b)));

                // ── Before a reinstall or repair: restore point, drivers, BitLocker, then the diagnostic package ──
                Border? usbRow = null;
                if (packageStack != null)
                {
                    if (packageStack.Children.OfType<DockPanel>().FirstOrDefault() is DockPanel head &&
                        FindTextBlock(head, "Diagnostic package") is TextBlock title)
                        title.Text = PT("Before a reinstall or repair");
                    var restore = ToolsRow("\uE81C", PT("Restore point"), PT("checking…"), PT("Create"), async (b) => await CreateRestorePointAsync(b), primary: true);
                    _toolsRestoreSub = RowSub(restore);
                    var drivers = ToolsRow("\uE896", PT("Back up drivers"), PT("to a folder, to install them again after a reinstall"), PT("Back up…"), async (b) => await BackupDriversAsync(b));
                    var bitlocker = ToolsRow("\uE72E", "BitLocker", PT("checking…"), PT("Recovery key"), async (b) => await ShowBitLockerKeyAsync(b));
                    _toolsBitLockerSub = RowSub(bitlocker);
                    int at = 1;   // after the header
                    packageStack.Children.Insert(at++, restore);
                    packageStack.Children.Insert(at++, drivers);
                    packageStack.Children.Insert(at++, bitlocker);
                    var cap = new TextBlock { Text = PT("Diagnostic package").ToUpperInvariant(), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
                    cap.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    packageStack.Children.Insert(at, cap);
                    // the USB monitor moves to "Tests & reports"
                    usbRow = packageStack.Children.OfType<Border>().FirstOrDefault(b => FindName("BtnUsbMonitor") is DependencyObject u && IsAncestor(b, u));
                    if (usbRow != null) packageStack.Children.Remove(usbRow);
                }

                // ── Tests & reports: a new card, before the timer ──
                var tests = new StackPanel();
                tests.Children.Add(CardHeader("\uE9D9", PT("Tests & reports")));
                tests.Children.Add(ToolsRow("\uE950", PT("Memory test"), PT("Windows Memory Diagnostic · runs at the next restart"), PT("Schedule"), async (b) => await ScheduleMemoryTestAsync()));
                if (BenchHasBattery())
                    tests.Children.Add(ToolsRow("\uE83F", PT("Battery report"), PT("wear, capacity and use of the last days"), PT("Open"), async (b) => await OpenBatteryReportAsync(b)));
                if (usbRow != null) { usbRow.Margin = new Thickness(0); tests.Children.Add(usbRow); }
                var testsCard = new Border { Padding = new Thickness(18, 14, 18, 4), Child = tests, Cursor = System.Windows.Input.Cursors.Arrow };
                testsCard.SetResourceReference(StyleProperty, "CompCardStyle");
                var testsCell = new Grid();
                testsCell.Children.Add(testsCard);
                grid.Children.Insert(3, testsCell);

                // ── History: the "Recent activity" card becomes the full-width history ──
                if (FindName("ToolsActivityCell") is Grid act && CardStack(act) is StackPanel actStack &&
                    actStack.Children.OfType<DockPanel>().FirstOrDefault() is DockPanel ah && FindTextBlock(ah, "Recent activity") is TextBlock at2)
                    at2.Text = PT("History");
                if (FindName("TxtToolsSubtitle") is TextBlock subT) subT.Text = PT("Repair, clean up and prepare a PC — what is run here is kept in the history");

                bool narrowNow = FindName("PanelShutdown") is FrameworkElement page && page.ActualWidth > 0 && page.ActualWidth < 1180;
                _toolsNarrow = narrowNow;
                LayoutToolsCards(grid, narrowNow);
                ServiceChangeLog.Changed += () => Dispatcher.InvokeAsync(RefreshToolActivity);
                _ = RefreshToolsStatusAsync();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "EnsureToolsPlus"); }
        }

        private static StackPanel? CardStack(UIElement cell)
        {
            DependencyObject? d = cell;
            for (int i = 0; i < 4 && d != null; i++)
            {
                if (d is StackPanel sp) return sp;
                d = d switch
                {
                    Grid g when g.Children.Count > 0 => g.Children[0],
                    Border b => b.Child,
                    _ => null,
                };
            }
            return null;
        }

        private static TextBlock? FindTextBlock(DependencyObject root, string text)
        {
            if (root is TextBlock t && (t.Text == text || t.Text == LiteralTexts.Translate(text))) return t;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                if (FindTextBlock(child, text) is TextBlock f) return f;
            return null;
        }

        private static bool IsAncestor(DependencyObject root, DependencyObject node)
        {
            for (DependencyObject? d = node; d != null; d = LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, root)) return true;
            return false;
        }

        private static TextBlock? RowSub(Border row) =>
            (row.Child as Grid)?.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();

        private static DockPanel CardHeader(string glyph, string title)
        {
            var dp = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"), FontSize = 17, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VAlign.Center };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var t = new TextBlock { Text = title, VerticalAlignment = VAlign.Center };
            t.SetResourceReference(StyleProperty, "DashRowName");
            dp.Children.Add(ic); dp.Children.Add(t);
            return dp;
        }

        /// <summary>One row like the XAML ones: icon · title + description · button. The click gets the button (to disable it).</summary>
        private Border ToolsRow(string glyph, string title, string sub, string button, Func<UiButton, Task> click, bool primary = false)
        {
            var row = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) };
            row.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"), FontSize = 15, VerticalAlignment = VAlign.Center };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            g.Children.Add(ic);
            var sp = new StackPanel { VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 10, 0) };
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, TextWrapping = TextWrapping.Wrap };
            var s = new TextBlock { Text = sub, TextWrapping = TextWrapping.Wrap };
            s.SetResourceReference(StyleProperty, "CompSub");
            sp.Children.Add(t); sp.Children.Add(s);
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            var b = new UiButton { Content = button, Appearance = primary ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary, Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VAlign.Center };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                try { await click(b); }
                catch (Exception ex) { AppLogger.Warning(ex, $"Tools: {title}"); AppDialog.Show(ex.Message, title, AppDialog.Kind.Warning); }
                finally { b.IsEnabled = true; }
            };
            Grid.SetColumn(b, 2);
            g.Children.Add(b);
            row.Child = g;
            return row;
        }

        // ══ SFC / DISM / component clean-up in the page ═════════════════════════════════════════════════════════════════

        private enum RepairKind { Sfc, Dism, Components }
        private readonly Dictionary<RepairKind, Border> _repairPanels = new();
        private readonly HashSet<RepairKind> _repairRunning = new();

        private Brush Tint(string key, byte a)
        {
            var c = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
            var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b;
        }

        /// <summary>The panel under the row of the tool (progress while running, the result after); created on first use.</summary>
        private Border RepairPanel(RepairKind kind, FrameworkElement button)
        {
            if (_repairPanels.TryGetValue(kind, out var p)) return p;
            p = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
            _repairPanels[kind] = p;
            // insert right after the row that holds the button
            DependencyObject? d = button;
            while (d != null && !(d is Border row && LogicalTreeHelper.GetParent(row) is StackPanel)) d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
            if (d is Border rowB && LogicalTreeHelper.GetParent(rowB) is StackPanel host)
                host.Children.Insert(host.Children.IndexOf(rowB) + 1, p);
            return p;
        }

        private void ShowRepairProgress(Border panel, string title, double pct, string sub)
        {
            panel.Background = Tint("AccentBrush", 18);
            panel.BorderBrush = Tint("AccentBrush", 60);
            var sp = new StackPanel();
            var t = new TextBlock { Text = pct >= 0 ? $"{title} {pct:0} %" : title, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(t);
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 7, 0, 5) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, Math.Clamp(pct, 0, 100)), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 100 - Math.Clamp(pct, 0, 100)), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(3), Background = Tint("AccentBrush", 40) };
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(3) };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            bar.Children.Add(track); bar.Children.Add(fill);
            sp.Children.Add(bar);
            var s = new TextBlock { Text = sub, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap };
            s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(s);
            panel.Child = sp;
        }

        private void ShowRepairResult(Border panel, string kindKey, string title, string sub)
        {
            panel.Background = Tint(kindKey, 22);
            panel.BorderBrush = Tint(kindKey, 70);
            var sp = new StackPanel();
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, kindKey);
            var s = new TextBlock { Text = sub, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(t); sp.Children.Add(s);
            panel.Child = sp;
        }

        private static Encoding OemEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch { return Encoding.Default; }
        }

        /// <summary>
        /// Runs SFC, DISM RestoreHealth or DISM StartComponentCleanup hidden, reads the percentage from its output and shows the
        /// result in plain words (with Windows' own last message under it). The app stays usable meanwhile.
        /// </summary>
        private async Task RunRepairAsync(RepairKind kind, FrameworkElement button)
        {
            if (_repairRunning.Contains(kind)) return;
            string confirm = kind switch
            {
                RepairKind.Sfc => _LS("Tools", "SfcConfirm"),
                RepairKind.Dism => _LS("Tools", "DismConfirm"),
                _ => PT("Removes the old versions of Windows components left behind by updates. Takes 5–20 minutes; the updates installed so far can no longer be uninstalled. Continue?"),
            };
            if (!AppDialog.Confirm(confirm, kind == RepairKind.Sfc ? "SFC" : "DISM")) return;
            _repairRunning.Add(kind);
            var panel = RepairPanel(kind, button);
            string name = kind switch { RepairKind.Sfc => PT("Checking system files…"), RepairKind.Dism => PT("Repairing the Windows image…"), _ => PT("Cleaning old components…") };
            string tool = kind switch { RepairKind.Sfc => "Check system files", RepairKind.Dism => "Repair Windows image", _ => "Old Windows components" };
            var sw = Stopwatch.StartNew();
            ShowRepairProgress(panel, name, 0, PT("starting · the app stays usable"));
            string args = kind switch
            {
                RepairKind.Sfc => "/scannow",
                RepairKind.Dism => "/Online /Cleanup-Image /RestoreHealth",
                _ => "/Online /Cleanup-Image /StartComponentCleanup",
            };
            var psi = new ProcessStartInfo(kind == RepairKind.Sfc ? "sfc.exe" : "dism.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                // sfc writes UTF-16 when its output is redirected; dism uses the console code page
                StandardOutputEncoding = kind == RepairKind.Sfc ? Encoding.Unicode : OemEncoding(),
            };
            // a 32-bit view of System32 would start the wrong sfc: the app is 64-bit, so the path is fine as it is
            var all = new StringBuilder();
            int exit = -1;
            try
            {
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start");
                var errTask = proc.StandardError.ReadToEndAsync();
                var buf = new char[512];
                double lastPct = 0;
                var pctRx = new Regex(@"(\d{1,3}(?:[.,]\d)?)\s*%");
                while (true)
                {
                    int n = await proc.StandardOutput.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    all.Append(buf, 0, n);
                    string tail = all.Length > 400 ? all.ToString(all.Length - 400, 400) : all.ToString();
                    var ms = pctRx.Matches(tail);
                    if (ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) && pct <= 100)
                    {
                        lastPct = pct;
                        double el = sw.Elapsed.TotalSeconds;
                        string left = pct > 3 ? PT("~{0} min left", Math.Max(1, (int)Math.Round(el / pct * (100 - pct) / 60))) : PT("estimating the time");
                        ShowRepairProgress(panel, name, pct, left + " · " + PT("the app stays usable"));
                    }
                }
                await proc.WaitForExitAsync();
                exit = proc.ExitCode;
                all.Append(await errTask);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"RunRepair {kind}");
                ShowRepairResult(panel, "StatusErrorBrush", PT("Could not start: {0}", ex.Message), "");
                _repairRunning.Remove(kind);
                return;
            }
            finally { sw.Stop(); }

            string text = all.ToString().Replace("\0", "");
            string lastLine = text.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 8 && !Regex.IsMatch(l, @"^\[?[=\s\d.,%\]]*$")).LastOrDefault() ?? "";
            string minutes = PT("{0} min", Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMinutes)));
            string when = DateTime.Now.ToString("HH:mm");
            (string key, string title) result;
            if (kind == RepairKind.Sfc)
            {
                string t = text.ToLowerInvariant();
                result = t.Contains("did not find any integrity violations") ? ("StatusSuccessBrush", PT("✓ System files are in order"))
                       : t.Contains("successfully repaired") ? ("StatusSuccessBrush", PT("✓ Damaged system files were found and repaired"))
                       : t.Contains("unable to fix") ? ("StatusWarningBrush", PT("Damaged files were found that SFC could not fix — run DISM, then SFC again"))
                       : t.Contains("could not perform") ? ("StatusErrorBrush", PT("SFC could not run — try again after a restart"))
                       : exit == 0 ? ("StatusSuccessBrush", PT("✓ SFC finished")) : ("StatusWarningBrush", PT("SFC finished — see Windows' message below"));
            }
            else
                result = exit == 0
                    ? ("StatusSuccessBrush", kind == RepairKind.Dism ? PT("✓ The Windows image is in order (repaired if needed)") : PT("✓ Old components cleaned"))
                    : ("StatusErrorBrush", PT("DISM ended with error {0}", $"0x{exit:X8}"));
            ShowRepairResult(panel, result.key, result.title, $"{when} · {minutes}" + (lastLine.Length > 0 ? " · " + lastLine : ""));
            ToolActivityLog.Add(tool, result.title.TrimStart('✓', ' '), result.key == "StatusSuccessBrush" ? "ok" : "warn");
            AppLogger.Info("Tools: {0} finished, exit {1}, {2:0} s — {3}", kind, exit, sw.Elapsed.TotalSeconds, lastLine);
            _repairRunning.Remove(kind);
        }

        // ══ the other new tools ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Runs a command line hidden and returns its exit code and output.</summary>
        private static async Task<(int exit, string output)> RunHiddenAsync(string file, string args, int timeoutSec = 300)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = OemEncoding(),
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            var done = p.WaitForExitAsync();
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(timeoutSec))) != done) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, (await o) + (await e));
        }

        private async Task ResetWindowsUpdateAsync(UiButton b)
        {
            if (!AppDialog.Confirm(PT("Stops the update services, moves the downloaded updates aside (SoftwareDistribution, catroot2) and starts the services again. Windows downloads the updates again afterwards. Continue?"), PT("Reset Windows Update"))) return;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string script =
                "net stop wuauserv /y & net stop bits /y & net stop cryptsvc /y & net stop msiserver /y & " +
                $"rd /s /q \"{win}\\SoftwareDistribution.old\" 2>nul & ren \"{win}\\SoftwareDistribution\" SoftwareDistribution.old & " +
                $"rd /s /q \"{win}\\System32\\catroot2.old\" 2>nul & ren \"{win}\\System32\\catroot2\" catroot2.old & " +
                "net start cryptsvc & net start bits & net start wuauserv & net start msiserver";
            ShowToast(PT("Resetting Windows Update…"));
            var (exit, output) = await Task.Run(() => RunHiddenAsync("cmd.exe", "/c " + script, 180));
            bool moved = !Directory.Exists(Path.Combine(win, "SoftwareDistribution")) || Directory.Exists(Path.Combine(win, "SoftwareDistribution.old"));
            AppLogger.Info("Tools: Windows Update reset, exit {0}, moved {1}", exit, moved);
            ToolActivityLog.Add("Reset Windows Update", moved ? "done · updates download again" : "partly done — a service did not stop", moved ? "ok" : "warn");
            if (moved) ShowToast(PT("Windows Update was reset — check for updates again"));
            else ShowToastWarning(PT("Windows Update could not be reset completely — a restart and a second try usually help"));
        }

        private async Task ResetPrintQueueAsync(UiButton b)
        {
            if (!AppDialog.Confirm(PT("Stops the Print Spooler, deletes the jobs stuck in the queue and starts it again. Documents waiting to print are lost. Continue?"), PT("Reset print queue"))) return;
            string spool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");
            var (exit, _) = await Task.Run(() => RunHiddenAsync("cmd.exe", $"/c net stop spooler /y & del /f /q \"{spool}\\*\" & net start spooler", 90));
            int left = 0;
            try { left = Directory.Exists(spool) ? Directory.GetFiles(spool).Length : 0; } catch { }
            bool ok = left == 0;
            AppLogger.Info("Tools: print queue reset, exit {0}, files left {1}", exit, left);
            ToolActivityLog.Add("Reset print queue", ok ? "queue emptied · spooler restarted" : $"{left} files could not be deleted", ok ? "ok" : "warn");
            if (ok) ShowToast(PT("The print queue was emptied")); else ShowToastWarning(PT("Some print jobs could not be deleted — try again after a restart"));
        }

        private async Task CreateRestorePointAsync(UiButton b)
        {
            ShowToast(PT("Creating a restore point…"));
            // Windows makes at most one per 24 h by default; the frequency limit is lifted for this one
            string ps = "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore'; " +
                        "$old=(Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency; " +
                        "Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord; " +
                        "try { Checkpoint-Computer -Description 'SMD Win' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK' } catch { 'ERR ' + $_.Exception.Message } " +
                        "finally { if ($old -ne $null) { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord } else { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } }";
            var (exit, output) = await Task.Run(() => RunHiddenAsync("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", 240));
            bool ok = output.Contains("OK") && !output.Contains("ERR ");
            AppLogger.Info("Tools: restore point, exit {0}: {1}", exit, output.Trim());
            ToolActivityLog.Add("Restore point", ok ? "created" : "not created", ok ? "ok" : "warn");
            if (ok) ShowToast(PT("Restore point created"));
            else AppDialog.Show(PT("The restore point could not be created. System Protection is probably off for drive C: — it can be turned on in System Properties → System Protection.") +
                                "\n\n" + output.Replace("ERR ", "").Trim(), PT("Restore point"), AppDialog.Kind.Warning);
            await RefreshToolsStatusAsync();
        }

        private async Task BackupDriversAsync(UiButton b)
        {
            string target;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = PT("Folder for the driver back-up (a USB stick is a good place)"), UseDescriptionForTitle = true })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                target = Path.Combine(dlg.SelectedPath, $"Drivers-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}");
            }
            Directory.CreateDirectory(target);
            ShowToast(PT("Backing up the drivers… (1–3 min)"));
            var (exit, output) = await Task.Run(() => RunHiddenAsync("dism.exe", $"/online /export-driver /destination:\"{target}\"", 900));
            int count = 0;
            try { count = Directory.GetDirectories(target).Length; } catch { }
            AppLogger.Info("Tools: driver back-up to {0}, exit {1}, {2} drivers", target, exit, count);
            ToolActivityLog.Add("Back up drivers", exit == 0 ? $"{count} drivers → {target}" : $"error {exit}", exit == 0 ? "ok" : "warn");
            if (exit == 0)
            {
                ShowToast(PT("{0} drivers saved", count));
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); } catch { }
            }
            else AppDialog.Show(PT("The drivers could not be saved.") + "\n\n" + output.Trim(), PT("Back up drivers"), AppDialog.Kind.Warning);
        }

        /// <summary>The BitLocker recovery key of C: (ID + 48-digit password), to note down before a repair or a reinstall.</summary>
        private async Task ShowBitLockerKeyAsync(UiButton b)
        {
            var (exit, output) = await Task.Run(() => RunHiddenAsync("manage-bde.exe", "-protectors -get C: -Type RecoveryPassword", 60));
            var keys = Regex.Matches(output, @"\b(\d{6}(?:-\d{6}){7})\b").Select(m => m.Groups[1].Value).Distinct().ToList();
            var ids = Regex.Matches(output, @"\{[0-9A-Fa-f\-]{36}\}").Select(m => m.Value).Distinct().ToList();
            if (keys.Count == 0)
            {
                AppDialog.Show(PT("Drive C: has no BitLocker recovery key (it is not encrypted, or it uses only a TPM)."), "BitLocker");
                return;
            }
            string text = PT("Recovery key of drive C: — note it down or save it before a repair or a reinstall:") + "\n\n" +
                          string.Join("\n\n", keys.Select((k, i) => (i < ids.Count ? "ID " + ids[i] + "\n" : "") + k)) +
                          "\n\n" + PT("The key is now also in the clipboard.");
            try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, keys)); } catch { }
            ToolActivityLog.Add("BitLocker", "recovery key shown", "info");
            AppDialog.Show(text, "BitLocker");
        }

        private async Task ScheduleMemoryTestAsync()
        {
            ToolActivityLog.Add("Memory test", "Windows Memory Diagnostic opened", "info");
            try { Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppDialog.Show(ex.Message, PT("Memory test"), AppDialog.Kind.Warning); }
            await Task.CompletedTask;
        }

        private async Task OpenBatteryReportAsync(UiButton b)
        {
            string file = Path.Combine(AppPaths.ReportsDirectory, $"battery-report-{DateTime.Now:yyyy-MM-dd_HHmm}.html");
            Directory.CreateDirectory(AppPaths.ReportsDirectory);
            var (exit, output) = await Task.Run(() => RunHiddenAsync("powercfg.exe", $"/batteryreport /output \"{file}\"", 60));
            if (File.Exists(file))
            {
                ToolActivityLog.Add("Battery report", "created", "ok");
                try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
            }
            else AppDialog.Show(PT("The battery report could not be created.") + "\n\n" + output.Trim(), PT("Battery report"), AppDialog.Kind.Warning);
        }

        /// <summary>The last restore point and the BitLocker state of C:, read in the background.</summary>
        private async Task RefreshToolsStatusAsync()
        {
            var (restore, bitlocker) = await Task.Run(() =>
            {
                string r = PT("none yet — or System Protection is off");
                try
                {
                    using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT CreationTime, Description FROM SystemRestore"));
                    DateTime? last = null;
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            var dt = ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString() ?? "");
                            if (last == null || dt > last) last = dt;
                        }
                    if (last is DateTime l) r = PT("last one: {0}", SummaryAgo(l));
                }
                catch (Exception ex) { AppLogger.Debug($"Restore points: {ex.Message}"); }

                string bl = PT("not encrypted");
                try
                {
                    using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                        new ObjectQuery("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"));
                    foreach (ManagementObject o in s.Get())
                        using (o)
                            if (string.Equals(o["DriveLetter"]?.ToString(), "C:", StringComparison.OrdinalIgnoreCase))
                                bl = Convert.ToInt32(o["ProtectionStatus"] ?? 0) == 1 ? PT("C: is encrypted — note the recovery key before a repair") : PT("C: is not protected");
                }
                catch (Exception ex) { AppLogger.Debug($"BitLocker state: {ex.Message}"); bl = PT("not available on this Windows edition"); }
                return (r, bl);
            });
            if (_toolsRestoreSub != null) _toolsRestoreSub.Text = restore;
            if (_toolsBitLockerSub != null) _toolsBitLockerSub.Text = bitlocker;
        }
    }
}
