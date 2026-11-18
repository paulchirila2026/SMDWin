using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// Screenshots for the self-test: every page of the app with its tabs (Storage, Settings) and the Network tools, then the
    /// separate windows (Health, power history, battery test, benchmark, clean-up, notifications, manual, features, licenses,
    /// About, sensor driver) — on the Light and on the Dark theme. Only the SMD Win window is captured (rendered from the app itself, not a screen capture), so
    /// nothing else on the desktop ends up in the file. The pictures go into the self-test zip (screenshots/light, /dark),
    /// so a visual problem can be seen exactly as it looks on that computer.
    /// </summary>
    public partial class MainWindow
    {
        // the pages in sidebar order (the names used by NavigateTo)
        private static readonly string[] _shotPages =
        {
            "Dashboard", "SysInfo", "Stress", "Benchmark", "Disk", "Ram", "Battery", "Network",
            "Apps", "Services", "Processes", "Startup", "Tools", "Events", "Crash", "Drivers", "Settings",
        };

        /// <summary>
        /// Takes the screenshots into <paramref name="folder"/>; returns the files written. The theme is switched only for the
        /// duration of the capture and is restored at the end, whatever happens.
        /// </summary>
        internal async Task<List<string>> CaptureSelfTestScreenshotsAsync(string folder, Action<string, double>? progress, CancellationToken ct,
                                                                          SelfTestUiData? data = null, bool longMode = false)
        {
            var files = new List<string>();
            data ??= new SelfTestUiData();
            Directory.CreateDirectory(folder);
            string environment = DescribeEnvironment() + $"Self-test: {(longMode ? "long (the tools run ~10 s each, screenshots/live)" : "short")}\r\n";
            data.StallsAtStart = UiStallLogger.StallCount;
            data.Resources.Add(SampleResources("before", collect: true));
            // the steps per theme (pages + Storage tabs + Network tools + Settings › Advanced + windows), for the progress bar
            const int windowCount = 10;
            int total = 2 * (_shotPages.Length + 5 + (NetToolMenu.Length - 1) + 1 + windowCount), done = 0;
            // the long self-test: the page tour takes the first 70 % of the bar, the running tools the rest
            double tourShare = longMode ? 0.7 : 1.0;
            void Progress(string label) => progress?.Invoke(label, tourShare * Math.Min(1.0, done / (double)total));
            try { File.WriteAllText(Path.Combine(folder, "environment.txt"), environment); files.Add(Path.Combine(folder, "environment.txt")); }
            catch (Exception ex) { AppLogger.Debug($"Self-test environment.txt: {ex.Message}"); }
            var s = SettingsService.Current;
            string origTheme = s.ThemeName;
            bool origAuto = s.AutoTheme;
            string startTheme = ThemeManager.IsDark(ThemeManager.Normalize(origTheme == "Auto" ? ThemeManager.ResolveAuto() : origTheme)) ? "Dark" : "Light";
            string otherTheme = startTheme == "Dark" ? "Light" : "Dark";

            try
            {
                foreach (string theme in new[] { startTheme, otherTheme })
                {
                    ct.ThrowIfCancellationRequested();
                    if (theme != startTheme)
                    {
                        s.AutoTheme = false;
                        s.ThemeName = theme;
                        var swTheme = System.Diagnostics.Stopwatch.StartNew();
                        ApplyCurrentTheme();
                        HighlightActiveThemeButton(theme, false);   // Settings shows the theme in the picture
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        data.ThemeSwitch.Add((theme, swTheme.ElapsedMilliseconds));
                        await Task.Delay(1500, ct);   // the theme brushes and the background lights are redrawn
                    }
                    string sub = Path.Combine(folder, theme.ToLowerInvariant());
                    Directory.CreateDirectory(sub);

                    int n = 0;
                    string Name(string what) => Path.Combine(sub, $"{++n:00}-{what}.png");

                    // the main window as it is now, without the progress toast
                    void ShootMain(string what)
                    {
                        var toastWas = ToastBorder.Visibility;
                        ToastBorder.Visibility = Visibility.Collapsed;
                        UpdateLayout();
                        try
                        {
                            string f = Name(what);
                            if (SaveElementPng(this, f)) files.Add(f);
                            CheckPicture(this, $"{theme} · {Path.GetFileNameWithoutExtension(f)}",
                                         TryFindResource("BgDarkBrush") as Brush ?? Brushes.White, data.Issues);
                        }
                        finally { ToastBorder.Visibility = toastWas; }
                    }

                    async Task Step(string what, int settleMs, Func<Task> show)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            // the time to open (until the page is drawn) and the window blocks while it settles
                            var swOpen = System.Diagnostics.Stopwatch.StartNew();
                            int stalls0 = UiStallLogger.StallCount; long stallMs0 = UiStallLogger.StallTotalMs;
                            await show();
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                            long openMs = swOpen.ElapsedMilliseconds;
                            done++;
                            Progress($"{theme} · {what}");
                            await Task.Delay(settleMs, ct);           // the cards fade in and the first data arrive
                            data.Times.Add(new PageTime(theme, what, openMs, UiStallLogger.StallCount - stalls0, UiStallLogger.StallTotalMs - stallMs0));
                            ShootMain(what);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { AppLogger.Warning(ex, $"Self-test screenshot {theme}/{what}"); }
                    }

                    // 1) every page, in the order of the sidebar, with its tabs and tools right after it
                    foreach (string page in _shotPages)
                    {
                        // the Power page opens on its first tab (it may still show the second one from the previous pass)
                        if (page == "Battery" && _prInit) SetPowerTab("bat");
                        await Step(page, 1400, () => NavigateTo(page));
                        if (page == "Disk")
                        {
                            foreach (var (btn, tab) in new[] { ("BtnStorageTabSmart", "Smart"), ("BtnStorageTabSurface", "Surface"),
                                                               ("BtnStorageTabBench", "Bench"), ("BtnStorageTabFiles", "Files") })
                                await Step("Disk-" + tab, 1400, () => { ClickNamed(btn); return Task.CompletedTask; });
                            ClickNamed("BtnStorageTabOverview");
                        }
                        else if (page == "Network")
                        {
                            // the tools of the Network page (Wi-Fi, ping, DNS…); some start reading by themselves when opened
                            foreach (var (key, _) in NetToolMenu)
                                if (key != "Overview")
                                    await Step("Network-" + key, 2200, () => { ShowNetTool(key, null); return Task.CompletedTask; });
                            ShowNetTool("Overview", null);
                        }
                        else if (page == "Battery" && _psLaptop)
                        {
                            // a laptop: the second tab of the Power page
                            await Step("Battery-Sleep", 1400, () => { SetPowerTab("sleep"); return Task.CompletedTask; });
                            SetPowerTab("bat");
                        }
                        else if (page == "Settings")
                        {
                            // every Settings tab gets its own picture (used for the README gallery)
                            foreach (var tab in new[] { "Appearance", "Alerts", "Widget", "Sensors", "Data", "Advanced", "About" })
                            {
                                string t = tab;
                                await Step("Settings-" + t, 900, () => { ClickNamed("BtnSetTab" + t); return Task.CompletedTask; });
                            }
                            ClickNamed("BtnSetTabGeneral");
                        }
                    }

                    // 2) the separate windows, on the same theme (the ones that read something get more time)
                    bool light = theme == "Light";
                    var windows = new (string Name, Func<Window> Make, int SettleMs)[]
                    {
                        ("Health",        () => new HealthCheckWindow("C:", this) { Owner = this }, 9000),
                        ("PowerHistory",  () => new PowerHistoryWindow { Owner = this }, 3000),
                        ("BatteryTest",   () => new BatteryTestWindow { Owner = this }, 1500),
                        ("AutoClean",     () => new AutoCleanDialog(light) { Owner = this }, 5000),
                        ("Notifications", () => new NotificationCenterWindow { Owner = this }, 1200),
                        ("Manual",        () => new ManualWindow { Owner = this }, 1500),
                        ("Features",      () => new FeaturesWindow { Owner = this }, 1200),
                        ("Licenses",      () => new LicensesWindow { Owner = this }, 1200),
                        ("About",         () => new AboutWindow { Owner = this }, 900),
                        ("SensorSetup",   () => new SensorSetupWindow(_ => Task.FromResult(false)) { Owner = this }, 900),
                    };
                    foreach (var (wName, make, settle) in windows)
                    {
                        ct.ThrowIfCancellationRequested();
                        done++;
                        Progress($"{theme} · {wName}");
                        files.AddRange(await CaptureDialogAsync(make, Name("Window-" + wName), settle, ct, data.Issues, theme,
                                                                TryFindResource("BgDarkBrush") as Brush ?? Brushes.White));
                    }
                    data.Resources.Add(SampleResources($"after tour {(theme == startTheme ? 1 : 2)} ({theme})", collect: true));
                }
            }
            finally
            {
                // back to the user's theme, exactly as it was
                if (s.ThemeName != origTheme || s.AutoTheme != origAuto)
                {
                    s.ThemeName = origTheme;
                    s.AutoTheme = origAuto;
                    try { ApplyCurrentTheme(); HighlightActiveThemeButton(origTheme, origAuto); }
                    catch (Exception ex) { AppLogger.Warning(ex, "Self-test screenshots: restore theme"); }
                }
                try { await NavigateTo("Dashboard"); }
                catch (Exception ex) { AppLogger.Debug($"Self-test screenshots: back to Dashboard: {ex.Message}"); }
            }
            // the long self-test: every tool started for ~10 s, captured while it runs and after (MainWindow.SelfTestLive.cs)
            if (longMode)
            {
                try
                {
                    await CaptureSelfTestLiveToolsAsync(folder, files, (label, f) => progress?.Invoke(label, tourShare + (1 - tourShare) * f), ct, data);
                    data.Resources.Add(SampleResources("after the running tools", collect: true));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLogger.Warning(ex, "Self-test live tools"); }
            }
            // the text files and the HTML report (report.html goes to the zip root, see BuildQuickSelfTestZip)
            try
            {
                string ui = Path.Combine(folder, "ui-checks.txt");
                File.WriteAllText(ui, IssuesText(data)); files.Add(ui);
                string perf = Path.Combine(folder, "performance.txt");
                File.WriteAllText(perf, PerformanceText(data)); files.Add(perf);
                if (data.DataChecks.Count > 0)
                {
                    string dc = Path.Combine(folder, "data-checks.txt");
                    File.WriteAllText(dc, string.Join("\r\n", data.DataChecks)); files.Add(dc);
                }
                string html = Path.Combine(folder, "report.html");
                File.WriteAllText(html, ReportHtml(data, environment, files, folder)); files.Add(html);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Self-test report files"); }
            AppLogger.Info("Self-test screenshots: {0} files in {1}, {2} interface findings", files.Count, folder, data.Issues.Count);
            return files;
        }

        /// <summary>Opens a small window, waits for its opening animation, captures it and closes it.</summary>
        private static async Task<List<string>> CaptureDialogAsync(Func<Window> make, string file, int settleMs, CancellationToken ct,
                                                                   List<UiIssue>? issues = null, string theme = "", Brush? pageBg = null)
        {
            var done = new List<string>();
            Window? w = null;
            try
            {
                w = make();
                w.ShowActivated = false;
                w.Show();
                await Task.Delay(settleMs, ct);
                if (SaveElementPng(w, file)) done.Add(file);
                if (issues != null) CheckPicture(w, $"{theme} · {Path.GetFileNameWithoutExtension(file)}", pageBg ?? Brushes.White, issues);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Warning(ex, $"Self-test screenshot {Path.GetFileName(file)}"); }
            finally
            {
                try { w?.Close(); }
                catch (Exception ex) { AppLogger.Debug($"Self-test screenshot close: {ex.Message}"); }
            }
            return done;
        }

        /// <summary>The screen, scaling, window and app settings the pictures were taken with (environment.txt in the zip).</summary>
        private string DescribeEnvironment()
        {
            var sb = new System.Text.StringBuilder();
            var dpi = VisualTreeHelper.GetDpi(this);
            var wa = SystemParameters.WorkArea;
            sb.AppendLine($"SMD Win {AppVersionInfo.Long} · tested {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Windows: {Environment.OSVersion.VersionString} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            sb.AppendLine($"Screen: {SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:0} × {SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:0} px · scaling {dpi.DpiScaleX * 100:0} %");
            sb.AppendLine($"Work area: {wa.Width:0} × {wa.Height:0} (WPF units)");
            sb.AppendLine($"Window: {ActualWidth:0} × {ActualHeight:0} · {WindowState}");
            bool compact = RootGrid?.LayoutTransform is ScaleTransform st && st.ScaleX < 0.999;
            sb.AppendLine($"Density: {SettingsService.Current.UiDensity} → {(compact ? "Compact (90 %)" : "Normal")}");
            sb.AppendLine($"Theme: {SettingsService.Current.ThemeName}{(SettingsService.Current.AutoTheme ? " (auto)" : "")} · language: {LanguageService.CurrentCode} · culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
            sb.AppendLine($"Animations: {(SettingsService.Current.EnableAnimations ? "on" : "off")} · update speed: {SettingsService.Current.UpdateSpeed}");
            sb.AppendLine($"Dashboard chart window: {_chartTimeRangeSeconds} s · Temp & Stress: {_stressChartRangeSeconds} s");
            sb.AppendLine($"Sensors: {_tempReader?.Backend ?? "none"} · sensor driver: {(PawnIOService.IsInstalled() ? "installed" : "not installed")}");
            sb.AppendLine($"Fonts: Segoe UI Variable {(Fonts.SystemFontFamilies.Any(f => f.Source.StartsWith("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)) ? "present" : "MISSING (fallback font in use)")}");
            return sb.ToString();
        }

        /// <summary>Presses a named tab button (Storage, Settings) exactly as a click would.</summary>
        private void ClickNamed(string name)
        {
            if (FindName(name) is System.Windows.Controls.Primitives.ButtonBase b)
                b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
        }

        /// <summary>
        /// Renders a whole window to a PNG at the screen's scaling (sharp text, the same pixels the user sees), on the page
        /// background colour so translucent areas look as on screen. The window itself is rendered (not its content): the
        /// content may be scaled (Compact density = 90 %) or offset by a margin, and rendering it alone cut the picture.
        /// </summary>
        private static bool SaveElementPng(Window? win, string file)
        {
            if (win == null || win.ActualWidth < 10 || win.ActualHeight < 10) return false;
            var dpi = VisualTreeHelper.GetDpi(win);
            int w = (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY);
            if (w > 8000 || h > 8000) return false;

            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var bgVisual = new DrawingVisual();
            using (var dc = bgVisual.RenderOpen())
                dc.DrawRectangle(win.TryFindResource("BgDarkBrush") as Brush ?? Brushes.White, null, new Rect(0, 0, win.ActualWidth, win.ActualHeight));
            rtb.Render(bgVisual);
            rtb.Render(win);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
            return true;
        }
    }
}
