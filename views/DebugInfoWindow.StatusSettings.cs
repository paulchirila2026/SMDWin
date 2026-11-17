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
        UIElement BuildStatusPage()
        {
            _checkPanel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
            return new ScrollViewer { Content = _checkPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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

        void AddExperimentsManualButton(System.Windows.Controls.Panel target)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnManual = new Button
            {
                Content = "Manual (incomplete)", Padding = new Thickness(10, 6, 10, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "The manual still has unfinished tabs — opened from here for internal review only; it is not linked from About."
            };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style s3) btnManual.Style = s3;
            btnManual.Click += (_, _) =>
            {
                try
                {
                    var owner = this.Owner ?? this;
                    var win = new SMDWin.Views.ManualWindow { Owner = owner, ShowInTaskbar = true };
                    if (owner is Window ownerWin)
                        Ui.FixOwnedWindowMinimize(win, ownerWin);
                    win.Show();
                }
                catch (Exception ex) { AppLogger.Debug($"AddExperimentsManualButton.BtnManual: {ex.Message}"); }
            };
            row.Children.Add(btnManual);
            target.Children.Add(row);
        }

        private Border? _accentPreview;
        private Slider? _accentR, _accentG, _accentB;
        private TextBox? _accentHexBox;
        private bool _accentSyncing; // avoids an endless slider<->hexbox loop while syncing

        private readonly System.Collections.Generic.List<(Border swatch, string name)> _accentPresetSwatches = new();

        void AddAccentPresetPicker(System.Windows.Controls.Panel target)
        {
            var presets = new (string name, string resourceKey, string tooltip)[]
            {
                ("MatchWindows", "Ink3Brush",       "Match Windows — uses your current Windows accent color"),
                ("Blue",         "WinBlueBrush",     "Blue accent"),
                ("Red",          "ActionRedBrush",   "Red accent"),
                ("Green",        "ActionGreenBrush", "Green accent"),
                ("Orange",       "ChartOrangeBrush", "Orange accent"),
            };

            string currentAccent = SettingsService.Current.AccentName ?? "Blue";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            _accentPresetSwatches.Clear();

            void RefreshHighlight()
            {
                string active = SettingsService.Current.AccentName ?? "Blue";
                foreach (var (sw, name) in _accentPresetSwatches)
                {
                    bool sel = name == active;
                    sw.BorderThickness = new Thickness(sel ? 3 : 0);
                    sw.BorderBrush = sel
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 255, 255, 255))
                        : System.Windows.Media.Brushes.Transparent;
                }
            }

            foreach (var (name, resKey, tip) in presets)
            {
                var fillBrush = (WpfBrush)(TryFindResource(resKey) ?? System.Windows.Media.Brushes.Gray);
                var inner = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = fillBrush };
                if (name == "MatchWindows")
                {
                    inner.Child = new TextBlock
                    {
                        Text = "A", FontSize = 13, FontWeight = FontWeights.Bold,
                        Foreground = System.Windows.Media.Brushes.White,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                bool isChosen = name == currentAccent;
                var outer = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(isChosen ? 3 : 0),
                    BorderBrush = isChosen
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 255, 255, 255))
                        : System.Windows.Media.Brushes.Transparent,
                    Margin = new Thickness(0, 0, 10, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = tip,
                    Child = inner,
                };
                _accentPresetSwatches.Add((outer, name));
                outer.MouseLeftButtonUp += (_, _) =>
                {
                    (this.Owner as SMDWin.Views.MainWindow)?.SetAccentPreset(name);
                    RefreshHighlight();
                    // Sync the R/G/B sliders + hex box below, so they do not show an old colour after picking a preset here — the same
                    // pattern (_accentSyncing) already used by the hex box TextChanged.
                    try
                    {
                        string hex = name == "MatchWindows"
                            ? (ReadWindowsAccentHex() ?? ThemeManager.GetAccentHex("Blue"))
                            : ThemeManager.GetAccentHex(name);
                        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                        _accentSyncing = true;
                        if (_accentR != null) _accentR.Value = c.R;
                        if (_accentG != null) _accentG.Value = c.G;
                        if (_accentB != null) _accentB.Value = c.B;
                        _accentSyncing = false;
                        UpdateAccentPreview();
                    }
                    catch (Exception ex) { AppLogger.Debug($"AccentPreset sync: {ex.Message}"); }
                };
                row.Children.Add(outer);
            }

            target.Children.Add(row);
        }

        void AddAccentColorPicker(System.Windows.Controls.Panel target)
        {
            var hint = new TextBlock
            {
                Text = "Experiment — any color picked here is applied live, on this PC only. It does not change the Blue/Red/Green/Orange presets in Settings.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, Opacity = 0.65,
                Margin = new Thickness(0, 0, 0, 8)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            target.Children.Add(hint);

            string startHex = ThemeManager.GetAccentHex(SettingsService.Current.AccentName);
            var startColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(startHex);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var slidersPanel = new StackPanel();
            _accentR = AddColorSlider(slidersPanel, "R", startColor.R);
            _accentG = AddColorSlider(slidersPanel, "G", startColor.G);
            _accentB = AddColorSlider(slidersPanel, "B", startColor.B);
            Grid.SetColumn(slidersPanel, 0);
            grid.Children.Add(slidersPanel);

            _accentPreview = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(8),
                Margin = new Thickness(12, 0, 0, 0),
                Background = new System.Windows.Media.SolidColorBrush(startColor),
                BorderThickness = new Thickness(1)
            };
            _accentPreview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            Grid.SetColumn(_accentPreview, 1);
            grid.Children.Add(_accentPreview);
            target.Children.Add(grid);

            var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            hexRow.Children.Add(new TextBlock { Text = "Hex:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontSize = 11, Opacity = 0.7 });
            _accentHexBox = new TextBox { Width = 100, Text = startHex, FontFamily = new WpfFontFamily("Consolas"), FontSize = 12 };
            _accentHexBox.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BgInputBrush");
            _accentHexBox.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            _accentHexBox.TextChanged += (_, _) =>
            {
                if (_accentSyncing) return;
                if (System.Text.RegularExpressions.Regex.IsMatch(_accentHexBox.Text, @"^#[0-9A-Fa-f]{6}$"))
                {
                    var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_accentHexBox.Text);
                    _accentSyncing = true;
                    _accentR!.Value = c.R; _accentG!.Value = c.G; _accentB!.Value = c.B;
                    _accentSyncing = false;
                    UpdateAccentPreview();
                }
            };
            hexRow.Children.Add(_accentHexBox);
            target.Children.Add(hexRow);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            var btnApply = new Button { Content = "Apply", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style sApply) btnApply.Style = sApply;
            btnApply.Click += (_, _) =>
            {
                SettingsService.Current.CustomAccentHex = _accentHexBox!.Text;
                _ = System.Threading.Tasks.Task.Run(() => { try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Debug(ex.Message); } });
                (this.Owner as SMDWin.Views.MainWindow)?.ApplyCurrentTheme();
            };
            btnRow.Children.Add(btnApply);

            var btnMatchWin = new Button { Content = "Match Windows", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style sMatch) btnMatchWin.Style = sMatch;
            btnMatchWin.Click += (_, _) =>
            {
                string? winHex = ReadWindowsAccentHex();
                if (winHex == null) { hint.Text = "Could not read the Windows accent color (registry not available)."; return; }
                _accentHexBox!.Text = winHex;
            };
            btnRow.Children.Add(btnMatchWin);

            var btnReset = new Button { Content = "Reset (preset)", Padding = new Thickness(10, 6, 10, 6), Cursor = System.Windows.Input.Cursors.Hand };
            if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style sReset) btnReset.Style = sReset;
            btnReset.Click += (_, _) =>
            {
                SettingsService.Current.CustomAccentHex = null;
                _ = System.Threading.Tasks.Task.Run(() => { try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Debug(ex.Message); } });
                (this.Owner as SMDWin.Views.MainWindow)?.ApplyCurrentTheme();
                string presetHex = ThemeManager.GetAccentHex(SettingsService.Current.AccentName);
                _accentHexBox!.Text = presetHex;
            };
            btnRow.Children.Add(btnReset);

            target.Children.Add(btnRow);
        }

        Slider AddColorSlider(StackPanel parent, string label, byte startValue)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var lbl = new TextBlock { Text = label, Width = 16, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Opacity = 0.7 };
            row.Children.Add(lbl);
            var slider = new Slider { Minimum = 0, Maximum = 255, Value = startValue, Width = 220, VerticalAlignment = VerticalAlignment.Center };
            var valLbl = new TextBlock { Text = startValue.ToString(), Width = 30, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.7 };
            slider.ValueChanged += (_, _) =>
            {
                valLbl.Text = ((int)slider.Value).ToString();
                if (_accentSyncing) return;
                UpdateAccentPreview();
            };
            row.Children.Add(slider);
            row.Children.Add(valLbl);
            parent.Children.Add(row);
            return slider;
        }

        void UpdateAccentPreview()
        {
            if (_accentR == null || _accentG == null || _accentB == null) return;
            var c = System.Windows.Media.Color.FromRgb((byte)_accentR.Value, (byte)_accentG.Value, (byte)_accentB.Value);
            if (_accentPreview != null) _accentPreview.Background = new System.Windows.Media.SolidColorBrush(c);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (_accentHexBox != null && _accentHexBox.Text != hex)
            {
                _accentSyncing = true;
                _accentHexBox.Text = hex;
                _accentSyncing = false;
            }
        }

        static string? ReadWindowsAccentHex()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                var val = key?.GetValue("AccentColor");
                if (val is not int abgr) return null;
                byte r = (byte)(abgr & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            catch (Exception ex) { AppLogger.Debug($"ReadWindowsAccentHex: {ex.Message}"); return null; }
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

        UIElement BuildSettingsPage()
        {
            _settingsTxt = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = Ui.FontS, BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 8),
            };
            _settingsTxt.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _settingsTxt.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");

            var bar = new WrapPanel { Margin = new Thickness(24, 14, 24, 0) };
            bar.Children.Add(ToolBtn("Open data folder", () => OpenInShell(SMDWin.Services.AppPaths.DataDirectory)));
            bar.Children.Add(ToolBtn("Open settings.json", () =>
            {
                string f = System.IO.Path.Combine(SMDWin.Services.AppPaths.DataDirectory, "settings.json");
                if (System.IO.File.Exists(f)) OpenInShell(f);
            }));
            bar.Children.Add(ToolBtn("Run GC", () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); BuildSettingsContent(); BuildChecklist(); }));
            bar.Children.Add(ToolBtn("Copy", () => WpfClipboard.SetText(_settingsTxt?.Text ?? "")));
            bar.Children.Add(ToolBtn("Reload", () => BuildSettingsContent()));

            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bar, 0); host.Children.Add(bar);
            var sv = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(24, 0, 24, 8), Child = _settingsTxt },
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(sv, 1); host.Children.Add(sv);
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
        UIElement BuildLogPage()
        {
            _logBox = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = Ui.FontXS, BorderThickness = new Thickness(0),
                Margin = new Thickness(24, 8, 24, 16),
                AcceptsReturn = true,
                Background = Brushes.Transparent,
            };
            _logBox.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");
            _logBox.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _logScroll = new ScrollViewer
            {
                Content = _logBox,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var onlyProblems = new CheckBox { Content = "warnings / errors only", Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = Ui.FontS };
            onlyProblems.SetResourceReference(CheckBox.ForegroundProperty, "TextSecondaryBrush");
            onlyProblems.Checked   += (_, _) => { _logOnlyProblems = true;  BuildLogContent(); };
            onlyProblems.Unchecked += (_, _) => { _logOnlyProblems = false; BuildLogContent(); };

            var bar = new WrapPanel { Margin = new Thickness(24, 14, 24, 0) };
            bar.Children.Add(ToolBtn("Open log folder", () => OpenInShell(AppLogger.LogDirectory)));
            bar.Children.Add(ToolBtn("Reload", () => BuildLogContent()));
            bar.Children.Add(ToolBtn("Copy", () => WpfClipboard.SetText(_logBox.Text)));
            bar.Children.Add(onlyProblems);

            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bar, 0); host.Children.Add(bar);
            Grid.SetRow(_logScroll, 1); host.Children.Add(_logScroll);
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
            _logScroll.ScrollToBottom();
        }
    }
}
