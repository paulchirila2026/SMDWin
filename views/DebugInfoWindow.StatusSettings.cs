using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using WpfColor         = System.Windows.Media.Color;
using WpfBrush         = System.Windows.Media.Brush;
using WpfSolidBrush    = System.Windows.Media.SolidColorBrush;
using WpfFontFamily    = System.Windows.Media.FontFamily;
using Button           = System.Windows.Controls.Button;
using CheckBox         = System.Windows.Controls.CheckBox;
using TextBox          = System.Windows.Controls.TextBox;
using TextBlock        = System.Windows.Controls.TextBlock;
using ScrollViewer     = System.Windows.Controls.ScrollViewer;
using StackPanel       = System.Windows.Controls.StackPanel;
using Border           = System.Windows.Controls.Border;
using Orientation      = System.Windows.Controls.Orientation;
using ComboBox         = System.Windows.Controls.ComboBox;
using ComboBoxItem     = System.Windows.Controls.ComboBoxItem;
using Brushes          = System.Windows.Media.Brushes;
using ColorConverter   = System.Windows.Media.ColorConverter;
using WpfPoint         = System.Windows.Point;
using WpfClipboard     = System.Windows.Clipboard;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // PAGE 0 — STATUS
        // ══════════════════════════════════════════════════════════════════════
        /// <summary>
        /// One page: the state of the app (process, memory, interface, data, environment), then all the settings
        /// (settings.json) and the recent log below, each with its own buttons and its own scroll.
        /// </summary>
        UIElement BuildStatusPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
            _checkPanel = new StackPanel();
            page.Children.Add(_checkPanel);

            page.Children.Add(PageSection("SETTINGS · settings.json"));
            page.Children.Add(BuildSettingsBlock());
            page.Children.Add(PageSection("LOG"));
            page.Children.Add(BuildLogBlock());
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        static FrameworkElement PageSection(string title)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 24, 0, 8) };
            var lbl = new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.Bold, Opacity = 0.45 };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var div = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 5, 0, 0) };
            div.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            sp.Children.Add(lbl); sp.Children.Add(div);
            return sp;
        }

        void BuildChecklist()
        {
            _checkPanel.Children.Clear();
            var pr = System.Diagnostics.Process.GetCurrentProcess();
            long Mb(long bytes) => bytes / 1024 / 1024;

            AddSection("PROCESS");
            AddCheck("PID / architecture", true, $"{pr.Id}  ·  {(Environment.Is64BitProcess ? "x64" : "x86")}  ·  {(IsAdmin() ? "elevated" : "standard user")}");
            TimeSpan up = DateTime.Now - pr.StartTime;
            AddCheck("Running for", true, $"{(int)up.TotalHours}h {up.Minutes:D2}m {up.Seconds:D2}s  (started {pr.StartTime:HH:mm:ss})");
            AddCheck("Threads / handles", pr.Threads.Count < 120 && pr.HandleCount < 5000, $"{pr.Threads.Count} threads  ·  {pr.HandleCount} handles",
                     pr.Threads.Count < 120 && pr.HandleCount < 5000 ? null : "#F97316");
            AddCheck("Memory (working set)", Mb(pr.WorkingSet64) < 400, $"{Mb(pr.WorkingSet64)} MB  ·  private {Mb(pr.PrivateMemorySize64)} MB",
                     Mb(pr.WorkingSet64) < 400 ? "#22C55E" : "#F97316");
            AddCheck("CPU time used", true, $"{pr.TotalProcessorTime.TotalSeconds:F1} s  (user {pr.UserProcessorTime.TotalSeconds:F1} s)");
            AddCheck("Process priority", true, pr.PriorityClass.ToString());

            AddSection("MANAGED MEMORY (.NET)");
            AddCheck("GC heap", true, $"{Mb(GC.GetTotalMemory(false))} MB  ·  allocated in total {Mb(GC.GetTotalAllocatedBytes(false))} MB");
            AddCheck("GC collections", true, $"gen0 {GC.CollectionCount(0)}  ·  gen1 {GC.CollectionCount(1)}  ·  gen2 {GC.CollectionCount(2)}");
            AddCheck("GC mode", true, $"{(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}  ·  {System.Runtime.GCSettings.LatencyMode}");
            var pool = System.Threading.ThreadPool.ThreadCount;
            AddCheck("ThreadPool", true, $"{pool} threads  ·  {System.Threading.ThreadPool.PendingWorkItemCount} queued");

            AddSection("USER INTERFACE");
            AddCheck("WPF rendering tier", RenderCapability.Tier >> 16 >= 2, $"tier {RenderCapability.Tier >> 16}  (2 = full hardware acceleration)");
            var src = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            AddCheck("DPI scaling", true, $"{dpi * 100:F0}%  ·  window {ActualWidth:F0}×{ActualHeight:F0}");
            AddCheck("Screens", true, $"{System.Windows.Forms.Screen.AllScreens.Length}  ·  primary {System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width}×{System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height}");
            AddCheck("Open windows", true, $"{System.Windows.Application.Current.Windows.Count}");
            AddCheck("Culture / UI language", true, $"{System.Globalization.CultureInfo.CurrentCulture.Name}  ·  {SettingsService.Current.Language}");

            AddSection("DATA AND LOG");
            AddCheck("Data folder", SMDWin.Services.AppPaths.IsPortableData, $"{SMDWin.Services.AppPaths.DataDirectory}  (portable: {SMDWin.Services.AppPaths.IsPortableData})");
            try
            {
                var logDir = new System.IO.DirectoryInfo(AppLogger.LogDirectory);
                var files = logDir.Exists ? logDir.GetFiles() : Array.Empty<System.IO.FileInfo>();
                long total = files.Sum(f => f.Length);
                AddCheck("Log folder", logDir.Exists, $"{files.Length} files  ·  {total / 1024} KB  ·  {logDir.FullName}");
                var last = files.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (last != null) AddCheck("Current log", true, $"{last.Name}  ·  {last.Length / 1024} KB  ·  last write {last.LastWriteTime:HH:mm:ss}");
            }
            catch (Exception ex) { AddCheck("Log folder", false, ex.Message); }
            try
            {
                var dd = new System.IO.DirectoryInfo(SMDWin.Services.AppPaths.DataDirectory);
                if (dd.Exists)
                    AddCheck("Data folder contents", true, string.Join("  ·  ", dd.GetFiles().OrderByDescending(f => f.Length).Take(5).Select(f => $"{f.Name} {Math.Max(1, f.Length / 1024)} KB")));
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            AddSection("ENVIRONMENT");
            AddCheck("Operating system", true, $"{Environment.OSVersion}  ·  build {Environment.OSVersion.Version.Build}");
            AddCheck(".NET Runtime", true, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            AddCheck("Executable", true, Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory);
            AddCheck("Command line", true, Environment.CommandLine);
        }

        private TextBox? _lhmDumpBox;
        void AddReportsFolderSettings(System.Windows.Controls.Panel target)
        {
            var s = SettingsService.Current;

            var lbl = new TextBlock
            {
                Text = "Reports Folder", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 6)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            target.Children.Add(lbl);

            var pathRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var txtPath = new TextBox
            {
                Text = s.ReportSavePath, Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center
            };
            txtPath.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BgInputBrush");
            txtPath.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            txtPath.TextChanged += (_, _) => SettingsService.Current.ReportSavePath = txtPath.Text;
            Grid.SetColumn(txtPath, 0);
            pathRow.Children.Add(txtPath);

            var btnBrowse = new Button { Content = "Browse", Padding = new Thickness(10, 6, 10, 6), Cursor = System.Windows.Input.Cursors.Hand };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style sBrowse) btnBrowse.Style = sBrowse;
            btnBrowse.Click += (_, _) =>
            {
                // WPF has no native FolderBrowserDialog; workaround with SaveFileDialog.
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "folder", Title = "Select Reports Folder" };
                if (dlg.ShowDialog() == true)
                {
                    string? dir = System.IO.Path.GetDirectoryName(dlg.FileName);
                    txtPath.Text = dir ?? "";
                    SettingsService.Current.ReportSavePath = txtPath.Text;
                }
            };
            Grid.SetColumn(btnBrowse, 1);
            pathRow.Children.Add(btnBrowse);
            target.Children.Add(pathRow);

            // ── Network mirror ──────────────────────────────────────────────────
            var chkNetwork = new CheckBox
            {
                Content = "Also save reports to a network folder",
                IsChecked = s.UseNetworkReportPath, Margin = new Thickness(0, 2, 0, 4)
            };
            chkNetwork.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            target.Children.Add(chkNetwork);

            var networkRow = new Grid
            {
                Margin = new Thickness(0, 2, 0, 0),
                Visibility = s.UseNetworkReportPath ? Visibility.Visible : Visibility.Collapsed
            };
            networkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            networkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var txtNetworkPath = new TextBox
            {
                Text = s.NetworkReportPath, Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = @"UNC path, e.g. \\192.168.1.1\reports\ or \\SERVER\Share\Folder"
            };
            txtNetworkPath.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BgInputBrush");
            txtNetworkPath.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(txtNetworkPath, 0);
            networkRow.Children.Add(txtNetworkPath);

            var statusTxt = new TextBlock { Text = "", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

            txtNetworkPath.TextChanged += (_, _) =>
            {
                SettingsService.Current.NetworkReportPath = txtNetworkPath.Text;
                statusTxt.Text = "";
            };

            var btnTest = new Button { Content = "Test", Padding = new Thickness(10, 6, 10, 6), Cursor = System.Windows.Input.Cursors.Hand };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style sTest) btnTest.Style = sTest;
            btnTest.Click += (_, _) =>
            {
                string path = txtNetworkPath.Text ?? "";
                if (string.IsNullOrWhiteSpace(path))
                {
                    statusTxt.Text = LanguageService.S("Network", "ReportPathHint");
                    statusTxt.SetResourceReference(TextBlock.ForegroundProperty, "OrangeBrush");
                    return;
                }
                statusTxt.Text = LanguageService.S("Common", "Testing");
                statusTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                System.Threading.Tasks.Task.Run(() => SettingsService.TestNetworkPathWritable(path)).ContinueWith(t =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        bool ok = t.Result;
                        statusTxt.Text = ok
                            ? "✓ Connected — folder is accessible and writable."
                            : "✘ Cannot access this folder. Check the path, share permissions, and network connection.";
                        statusTxt.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusOkBrush" : "StatusErrorBrush");
                    });
                });
            };
            Grid.SetColumn(btnTest, 1);
            networkRow.Children.Add(btnTest);
            target.Children.Add(networkRow);
            target.Children.Add(statusTxt);

            chkNetwork.Click += (_, _) =>
            {
                bool on = chkNetwork.IsChecked == true;
                SettingsService.Current.UseNetworkReportPath = on;
                networkRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                statusTxt.Text = "";
                SettingsService.Save();
            };
        }

        void AddDiagnosticTools(System.Windows.Controls.Panel target)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
            var reinitStatus = new TextBlock
            {
                FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            reinitStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var btnReinit = new Button
            {
                Content = "Force LHM reinit", Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Forces a manual LibreHardwareMonitor reinit without waiting for a real freeze — useful for testing fixes."
            };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style s1) btnReinit.Style = s1;
            btnReinit.Click += (_, _) =>
            {
                var mainWin = this.Owner as SMDWin.Views.MainWindow;
                mainWin?.ForceReinitLhm();
                reinitStatus.Text = $"[{DateTime.Now:HH:mm:ss}] Triggered — see the Log tab for details.";
            };
            row.Children.Add(btnReinit);

            var btnDump = new Button
            {
                Content = "Dump LHM sensors", Padding = new Thickness(10, 6, 10, 6), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Shows every raw value read by LibreHardwareMonitor, before the app picks which ones to use."
            };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style s2) btnDump.Style = s2;
            btnDump.Click += (_, _) =>
            {
                var mainWin = this.Owner as SMDWin.Views.MainWindow;
                var lines = mainWin?.DumpLhmSensorsRaw() ?? new System.Collections.Generic.List<string> { "(fereastra principala indisponibila)" };
                if (_lhmDumpBox != null)
                {
                    _lhmDumpBox.Text = $"[{DateTime.Now:HH:mm:ss}]\n" + string.Join("\n", lines);
                    _lhmDumpBox.Visibility = Visibility.Visible;
                }
            };
            row.Children.Add(btnDump);
            row.Children.Add(reinitStatus);

            target.Children.Add(row);

            _lhmDumpBox = new TextBox
            {
                Text = "",
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Consolas"), FontSize = 11,
                Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            _lhmDumpBox.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BgInputBrush");
            _lhmDumpBox.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            target.Children.Add(_lhmDumpBox);
        }

        void AddSection(string title)
        {
            bool first = _checkPanel.Children.Count == 0;
            var sp = new StackPanel { Margin = new Thickness(0, first ? 0 : 20, 0, 8) };
            var lbl = new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.Bold, Opacity = 0.45 };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var div = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 5, 0, 0) };
            div.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            sp.Children.Add(lbl); sp.Children.Add(div);
            _checkPanel.Children.Add(sp);
        }

        void AddCheck(string label, bool ok, string detail, string? valueColor = null)
        {
            var row = new Border
            {
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
            };
            row.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ico = new TextBlock
            {
                Text = ok ? "✔" : "✘", FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ok ? new WpfSolidBrush(WpfColor.FromRgb(34, 197, 94))
                               : new WpfSolidBrush(WpfColor.FromRgb(239, 68, 68))
            };
            var lbl = new TextBlock { Text = label, FontSize = Ui.FontM, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var val = new TextBlock { Text = detail, FontSize = Ui.FontS, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (valueColor != null)
                val.Foreground = new WpfSolidBrush((WpfColor)ColorConverter.ConvertFromString(valueColor)!);
            else
                val.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            Grid.SetColumn(ico, 0); Grid.SetColumn(lbl, 1); Grid.SetColumn(val, 2);
            g.Children.Add(ico); g.Children.Add(lbl); g.Children.Add(val);
            row.Child = g;
            _checkPanel.Children.Add(row);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAGE 1 — SETTINGS
        // ══════════════════════════════════════════════════════════════════════
        // toolbar buttons: the same secondary button as in the app's other windows
        Button ToolBtn(string text, Action click)
        {
            var b = Net.NetWindowHelper.SecondaryButton(text);
            b.Margin = new Thickness(0, 0, 8, 0);
            b.Click += (_, _) => { try { click(); } catch (Exception ex) { AppLogger.Debug($"DebugInfoWindow.ToolBtn: {ex.Message}"); } };
            return b;
        }

        static void OpenInShell(string path)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        UIElement BuildSettingsBlock()
        {
            _settingsTxt = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = Ui.FontS, BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 0), Height = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            _settingsTxt.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _settingsTxt.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");

            var bar = new WrapPanel();
            bar.Children.Add(ToolBtn("Open data folder", () => OpenInShell(SMDWin.Services.AppPaths.DataDirectory)));
            bar.Children.Add(ToolBtn("Open settings.json", () =>
            {
                string f = System.IO.Path.Combine(SMDWin.Services.AppPaths.DataDirectory, "settings.json");
                if (System.IO.File.Exists(f)) OpenInShell(f);
            }));
            bar.Children.Add(ToolBtn("Run GC", () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); BuildSettingsContent(); BuildChecklist(); }));
            bar.Children.Add(ToolBtn("Copy", () => WpfClipboard.SetText(_settingsTxt?.Text ?? "")));
            bar.Children.Add(ToolBtn("Reload", () => BuildSettingsContent()));

            var host = new StackPanel();
            host.Children.Add(bar);
            host.Children.Add(_settingsTxt);
            return host;
        }

        /// <summary>All properties in settings.json (through reflection), not only those on the Settings screen.</summary>
        void BuildSettingsContent()
        {
            if (_settingsTxt == null) return;
            var sb = new StringBuilder();
            var s  = SettingsService.Current;
            sb.AppendLine($"=== settings.json — all properties  [{DateTime.Now:HH:mm:ss}] ===");
            var props = s.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                         .Where(pi => pi.CanRead && pi.GetIndexParameters().Length == 0)
                         .OrderBy(pi => pi.Name, StringComparer.OrdinalIgnoreCase).ToList();
            int w = props.Count == 0 ? 20 : Math.Min(40, props.Max(pi => pi.Name.Length));
            var defaults = Activator.CreateInstance(s.GetType());
            int changed = 0;
            foreach (var pi in props)
            {
                string val;
                object? cur = null, def = null;
                try { cur = pi.GetValue(s); def = defaults != null ? pi.GetValue(defaults) : null; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (cur is System.Collections.IEnumerable en && cur is not string)
                {
                    var items = en.Cast<object?>().Take(6).Select(x => x?.ToString() ?? "null").ToList();
                    int cnt = en.Cast<object?>().Count();
                    val = $"[{cnt}] " + string.Join(", ", items) + (cnt > items.Count ? ", …" : "");
                }
                else val = cur?.ToString() ?? "null";
                bool diff = !Equals(cur?.ToString(), def?.ToString());
                if (diff) changed++;
                sb.AppendLine($"{(diff ? "*" : " ")} {pi.Name.PadRight(w)} : {val}");
            }
            sb.AppendLine();
            sb.AppendLine($"{props.Count} properties, {changed} different from the default (marked with *)");
            _settingsTxt.Text = sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAGE 2 — LOG
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildLogBlock()
        {
            _logBox = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = Ui.FontXS, BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 0), Height = 340,
                AcceptsReturn = true,
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            _logBox.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");
            _logBox.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");

            var onlyProblems = new CheckBox { Content = "warnings / errors only", Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = Ui.FontS };
            onlyProblems.SetResourceReference(CheckBox.ForegroundProperty, "TextSecondaryBrush");
            onlyProblems.Checked   += (_, _) => { _logOnlyProblems = true;  BuildLogContent(); };
            onlyProblems.Unchecked += (_, _) => { _logOnlyProblems = false; BuildLogContent(); };

            var bar = new WrapPanel();
            bar.Children.Add(ToolBtn("Open log folder", () => OpenInShell(AppLogger.LogDirectory)));
            bar.Children.Add(ToolBtn("Reload", () => BuildLogContent()));
            bar.Children.Add(ToolBtn("Copy", () => WpfClipboard.SetText(_logBox.Text)));
            bar.Children.Add(onlyProblems);

            var host = new StackPanel();
            host.Children.Add(bar);
            host.Children.Add(_logBox);
            return host;
        }

        bool _logOnlyProblems;

        void BuildLogContent()
        {
            string text = AppLogger.GetRecentLines(_logOnlyProblems ? 2000 : 300);
            if (_logOnlyProblems)
                text = string.Join(Environment.NewLine, text.Split('\n').Where(l =>
                    l.Contains("WRN", StringComparison.OrdinalIgnoreCase) || l.Contains("ERR", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("FTL", StringComparison.OrdinalIgnoreCase) || l.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("Error", StringComparison.OrdinalIgnoreCase) || l.Contains("UI-STALL", StringComparison.OrdinalIgnoreCase)).Select(l => l.TrimEnd('\r')));
            _logBox.Text = text;
            _logBox.ScrollToEnd();
        }
    }
}
