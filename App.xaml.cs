using System;
using System.Runtime.InteropServices;
using System.Threading;
using SMDWin.Services;

namespace SMDWin
{
    public partial class App : System.Windows.Application
    {
        /// <summary>When the last UI errors happened (to tell an isolated error from repeated ones).</summary>
        private readonly System.Collections.Generic.List<DateTime> _uiErrorTimes = new();

        private Mutex? _singleInstanceMutex;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            // Every animation at most 30 frames a second (WPF's default is 60). Any running animation keeps WPF redrawing the window;
            // at 60 that alone kept a core busy on laptops with integrated graphics. The slow decorative loops ask for 15.
            try
            {
                System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                    typeof(System.Windows.Media.Animation.Timeline), new System.Windows.FrameworkPropertyMetadata { DefaultValue = 30 });
            }
            catch (Exception exFps) { AppLogger.Debug($"OnStartup frame rate: {exFps.Message}"); }

            try { SMDWin.Views.ButtonLook.Register(); }   // borderless WPF-UI buttons (variant B)
            catch (Exception exBtn) { AppLogger.Debug($"OnStartup ButtonLook: {exBtn.Message}"); }
            try
            {
                int cores = Environment.ProcessorCount;
                System.Threading.ThreadPool.GetMinThreads(out int curWorker, out int curIo);
                int targetWorker = Math.Max(curWorker, cores * 2);
                int targetIo     = Math.Max(curIo, cores * 2);
                System.Threading.ThreadPool.SetMinThreads(targetWorker, targetIo);
            }
            catch (Exception exPool) { AppLogger.Debug($"OnStartup ThreadPool.SetMinThreads: {exPool.Message}"); }

            try
            {
                var fixedNumberCulture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
                fixedNumberCulture.NumberFormat.NumberDecimalSeparator = ".";
                fixedNumberCulture.NumberFormat.NumberGroupSeparator   = ",";
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = fixedNumberCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture = fixedNumberCulture;
            }
            catch (Exception ex) { AppLogger.Debug("OnStartup: fixed-number-culture setup failed: " + ex.Message); }

            // ── Single-instance guard ──────────────────────────────────────────────
            _singleInstanceMutex = new Mutex(true, "SMDWin_SingleInstance_v3", out bool isFirst);
            // Restart requested by the app (settings imported / reset, Administrator): the old instance is closing right now,
            // so wait for the mutex to be released instead of giving up.
            if (!isFirst && Array.Exists(e.Args, a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)))
            {
                try { isFirst = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { isFirst = true; }
            }
            if (!isFirst)
            {
                // Another instance is running — bring it to front and exit
                try
                {
                    var current = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                    {
                        if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Single-instance focus restore failed"); }
                Shutdown();
                return;
            }

            // 1. Register crash handlers FIRST — before anything else
            DispatcherUnhandledException += (s, ex) =>
            {
                if (ex.Exception is ArgumentNullException anex
                    && anex.StackTrace?.Contains("DataGridItemAutomationPeer") == true)
                {
                    try { AppLogger.Warning(ex.Exception, "WPF DataGrid automation peer bug — recovered, app continues"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    ex.Handled = true;
                    return;
                }

                if (ex.Exception is System.Runtime.InteropServices.COMException comEx
                    && (uint)comEx.HResult == 0x80263001)
                {
                    try { AppLogger.Warning(ex.Exception, "WindowChrome DWM composition disabled — cosmetic only, app continues"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    ex.Handled = true;
                    return;
                }

                try { AppLogger.Fatal(ex.Exception, "UI unhandled exception"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                ex.Handled = true;  // mark handled first so WPF doesn't terminate

                // One error in a page is usually local (one card, one button): the user may keep working.
                // Errors that repeat quickly mean the app is in a bad state, so it closes cleanly.
                var now = DateTime.UtcNow;
                _uiErrorTimes.RemoveAll(t => (now - t).TotalSeconds > 60);
                _uiErrorTimes.Add(now);
                bool tooMany = _uiErrorTimes.Count >= 3;
                bool keepGoing = false;
                try
                {
                    string logs = SMDWin.Services.AppPaths.LogsDirectory;
                    string text = tooMany
                        ? string.Format(SMDWin.Services.LanguageService.S("App", "ErrorRepeatedFmt"), ex.Exception.Message, logs)
                        : string.Format(SMDWin.Services.LanguageService.S("App", "ErrorContinueFmt"), ex.Exception.Message, logs);
                    var answer = System.Windows.MessageBox.Show(text, "SMD Win",
                        tooMany ? System.Windows.MessageBoxButton.OK : System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Error);
                    keepGoing = !tooMany && answer == System.Windows.MessageBoxResult.Yes;
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (keepGoing) { AppLogger.Warning("UI error: the user chose to continue"); return; }
                // Shut down cleanly (Turbo / paused processes are restored on close) — prevents a zombie process
                try { Current?.Shutdown(1); } catch { Environment.Exit(1); }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                try { AppLogger.Fatal(ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString()),
                    "Background thread unhandled exception"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (ex.IsTerminating)
                {
                    try
                    {
                        System.Windows.MessageBox.Show(
                            string.Format(SMDWin.Services.LanguageService.S("App", "ErrorFatalFmt"),
                                (ex.ExceptionObject as Exception)?.Message ?? ex.ExceptionObject?.ToString(),
                                SMDWin.Services.AppPaths.LogsDirectory),
                            "SMD Win",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                try
                {
                    // Suppress expected socket errors from LAN/port scans — these are normal
                    // when connecting to IPs that aren't running our server
                    var inner = ex.Exception?.InnerException;
                    if (inner is System.Net.Sockets.SocketException se &&
                        (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.HostUnreachable ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable))
                    {
                        // silent — expected during network scans
                    }
                    else if (inner is OperationCanceledException)
                    {
                        // silent — expected when cancellation token fires
                    }
                    else
                    {
                        AppLogger.Warning(ex.Exception ?? new Exception("Unobserved task exception"), "Unobserved task exception");
                    }
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                ex.SetObserved();
            };

            // 2. Init Serilog AFTER the handlers — wrapped in try/catch so a Serilog failure never prevents the app from starting.
            // All app files go to a single folder (Data) — set up before logging and settings.
            System.Collections.Generic.List<string> dataNotes = new();
            try { dataNotes = SMDWin.Services.AppPaths.ConsolidateDataFolder(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            try { AppLogger.Initialize(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            foreach (var n in dataNotes) AppLogger.Info("Data folder: {0}", n);
            SMDWin.Services.BootTiming.Mark("App: log started (Data folder checked)");

            // the splash appears now, within the first seconds — before the main window starts building
            // (settings and language first, so its texts are in the chosen language)
            try { SMDWin.Views.MainWindow.PrepareSettingsAndLanguage(); } catch (Exception ex) { AppLogger.Warning(ex, "OnStartup: setari / limba"); }
            try { SMDWin.Views.SplashHost.ShowOnOwnThread(); } catch (Exception ex) { AppLogger.Warning(ex, "OnStartup: splash"); }
            SMDWin.Services.BootTiming.Mark("App: splash shown");

            System.Threading.Tasks.Task.Run(() =>
            {
                try { SMDWin.Services.DiskBenchmarkService.SweepOrphanedBenchFiles(); }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { SMDWin.Services.PsuStressService.SweepOrphanedTempFiles(); }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            });

            // The disk list (models, partitions, SMART) takes ~1-1.5 s through WMI. Started now, while the splash is up, it is ready
            // (or nearly) when the Dashboard asks for it; the Dashboard's call waits on the same lock and gets the same result, so
            // nothing is read twice. Always read fresh at start — a saved copy could show an old health value.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    SMDWin.Services.PowerOnClock.Prime();   // the power-on time, from the event log (uptime with Fast Startup)
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var disks = await new SMDWin.Services.HardwareService().GetDisksAsync();
                    AppLogger.Debug($"Disk list pre-read: {disks.Count} disks in {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) { AppLogger.Debug($"Disk list pre-read: {ex.Message}"); }
            });

            // Secondary windows: on close, Windows sometimes moves focus to ANOTHER application (typical for modal dialogs without
            // a title bar / with a semi-transparent layer), and the main window ends up behind — it looks minimized. A single global
            // handler, valid for all windows (Tools, Network, Health Check, dialogs).
            try
            {
                System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Window),
                    System.Windows.FrameworkElement.LoadedEvent,
                    new System.Windows.RoutedEventHandler(OnAnyWindowLoaded));
            }
            catch (Exception ex) { AppLogger.Debug("RegisterClassHandler(Window.Loaded): " + ex.Message); }

            try { SMDWin.Services.UiStallLogger.Install(Dispatcher); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            base.OnStartup(e);
            SMDWin.Services.BootTiming.Mark("App: OnStartup done (main window next)");
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Window, object>
            _closeFocusHooked = new();

        private static void OnAnyWindowLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is not System.Windows.Window w) return;
            if (w is SMDWin.Views.MainWindow) return;
            if (_closeFocusHooked.TryGetValue(w, out _)) return;   // Loaded may come more than once
            _closeFocusHooked.Add(w, new object());
            w.Closing += (_, args) =>
            {
                try
                {
                    if (args.Cancel || !w.IsActive) return;   // close cancelled / the window did not have focus
                    System.Windows.Window? target = w.Owner;
                    if (target == null)
                    {
                        // Widgets (Topmost, no Owner) must not bring the main window to the front
                        if (w.Topmost) return;
                        foreach (System.Windows.Window x in Current.Windows)
                            if (x is SMDWin.Views.MainWindow) { target = x; break; }
                    }
                    if (target == null || ReferenceEquals(target, w)) return;
                    if (!target.IsVisible || target.WindowState == System.Windows.WindowState.Minimized) return;
                    target.Activate();
                }
                catch (Exception ex) { AppLogger.Debug("Window close focus restore: " + ex.Message); }
            };
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            try { AppLogger.Info("SMDWin exiting (code {Code})", e.ApplicationExitCode); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            // an ETW session outlives the process that started it: stop it, or it keeps collecting until the next start of SMD Win
            try { SMDWin.Services.EtwNetMonitor.Instance.Stop(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            try { AppLogger.Shutdown(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            try { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            base.OnExit(e);
            // Nuclear option: force process exit so no background thread can keep it alive.
            // Called after base.OnExit so all WPF cleanup runs first.
            Environment.Exit(e.ApplicationExitCode);
        }
    }
}
