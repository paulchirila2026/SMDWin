using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SMDWin.Services;

using Size = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// The logo as a live element:
    ///  • sidebar: lit up to the orange segments at rest; while a test, benchmark or scan runs (the same state as the sidebar task
    ///    badges) it rises to red, and falls back when it ends — it only moves when something starts or stops;
    ///  • taskbar button: a moving progress band while a test runs (Windows' own progress on the icon);
    ///  • tray: the small gauge shows how busy the PC is — the most loaded of CPU, RAM and CPU temperature (relative to the limit),
    ///    redrawn only when the number of lit segments changes.
    /// </summary>
    public partial class MainWindow
    {
        private const double LogoRestLevel = 12;   // 10 blue + yellow + orange; the two red ones only during a test
        private bool _logoBusy;

        private SmdLogo? SidebarLogo => BtnAbout?.Template?.FindName("ImgSidebarLogo", BtnAbout) as SmdLogo;

        /// <summary>Called whenever a task badge changes (SetNavTaskBadge) and once at start-up.</summary>
        private void UpdateLogoActivity(bool force = false)
        {
            try
            {
                bool busy = _taskBadgeStressOn || _taskBadgeDiskOn || _ramTaskActiveCount > 0;
                if (busy == _logoBusy && !force) return;
                _logoBusy = busy;

                if (SidebarLogo is SmdLogo logo)
                {
                    logo.AnimateLevel(busy ? SmdLogo.SegmentCount : LogoRestLevel, busy ? 1200 : 1600);
                    if (busy && !force) logo.Beat();
                }

                TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
                TaskbarItemInfo.ProgressState = busy
                    ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                    : System.Windows.Shell.TaskbarItemProgressState.None;
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateLogoActivity: {ex.Message}"); }
        }

        /// <summary>
        /// Shows a test's real progress on the taskbar button (0…1), or its state: paused = yellow, stopped by force / failed = red.
        /// Tests that know their duration call this; the others keep the moving band.
        /// </summary>
        internal void SetTaskbarProgress(double? fraction, bool paused = false, bool error = false)
        {
            try
            {
                TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
                if (error)       TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
                else if (paused) TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Paused;
                else if (fraction.HasValue) TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
                else TaskbarItemInfo.ProgressState = _logoBusy ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                                                               : System.Windows.Shell.TaskbarItemProgressState.None;
                if (fraction.HasValue) TaskbarItemInfo.ProgressValue = Math.Clamp(fraction.Value, 0, 1);
            }
            catch (Exception ex) { AppLogger.Debug($"SetTaskbarProgress: {ex.Message}"); }
        }

        // ── tray: live gauge ─────────────────────────────────────────────
        /// <summary>Windows' own setting for the taskbar / Start colour (Settings › Personalisation › Colours › Windows mode).</summary>
        private static bool TaskbarIsLight()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
            }
            catch (Exception ex) { AppLogger.Debug($"TaskbarIsLight: {ex.Message}"); return false; }
        }

        private int _trayLitShown = -1;
        private DateTime _trayGaugeAt = DateTime.MinValue;

        /// <summary>From the temperature tick (UpdateTrayIcon). At most every 2 s, and only when the lit count changes.</summary>
        private void UpdateTrayGauge(float? cpuTemp, float? cpuLoad)
        {
            if (_notifyIcon == null) return;
            if ((DateTime.UtcNow - _trayGaugeAt).TotalSeconds < 2) return;
            _trayGaugeAt = DateTime.UtcNow;
            try
            {
                double cpu = cpuLoad is float c && float.IsFinite(c) ? c : 0;
                double ram = 0;
                try { var (used, total) = WmiCache.Instance.GetRamUsage(); if (total > 0) ram = used * 100.0 / total; }
                catch (Exception ex) { AppLogger.Debug($"Tray gauge RAM: {ex.Message}"); }
                double limit = SettingsService.Current.CpuTempLimit > 0 ? SettingsService.Current.CpuTempLimit : _autoCpuTempLimit;
                double temp = cpuTemp is float t && float.IsFinite(t) && limit > 35 ? Math.Clamp((t - 30) / (limit - 30) * 100, 0, 100) : 0;
                double load = Math.Max(cpu, Math.Max(ram, temp));

                const int traySegments = 9;   // the simplified drawing used at tray size
                int lit = (int)Math.Round(load / 100.0 * traySegments);
                if (lit == _trayLitShown) return;
                _trayLitShown = lit;

                int px = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
                // the unlit segments follow the TASKBAR's colour, not SMD Win's theme: with the app on Light they were drawn in its dark
                // grey and looked black on a dark taskbar
                bool lightTaskbar = TaskbarIsLight();
                var logo = new SmdLogo
                {
                    Width = px, Height = px, Level = lit * (double)SmdLogo.SegmentCount / traySegments,
                    DimColor = lightTaskbar ? System.Windows.Media.Color.FromRgb(0x1F, 0x29, 0x37) : System.Windows.Media.Colors.White,
                    DimOpacity = lightTaskbar ? 0.28 : 0.32,
                    ThickRing = true,   // the taskbar / tray look (30 % thicker ring) — used above 30 px, i.e. at 200 % scaling
                };
                logo.Measure(new Size(px, px));
                logo.Arrange(new Rect(0, 0, px, px));
                var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(logo);
                var pixels = new byte[px * px * 4];
                rtb.CopyPixels(pixels, px * 4, 0);

                using var bmp = new System.Drawing.Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, px, px),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                bmp.UnlockBits(data);

                // GetHicon makes a raw handle Windows does not free: copy it into a managed Icon, then destroy the raw one
                IntPtr h = bmp.GetHicon();
                System.Drawing.Icon icon;
                try { using var tmp = System.Drawing.Icon.FromHandle(h); icon = (System.Drawing.Icon)tmp.Clone(); }
                finally { DestroyIcon(h); }
                var old = _notifyIcon.Icon;
                _notifyIcon.Icon = icon;
                if (old != null && !ReferenceEquals(old, System.Drawing.SystemIcons.Application)) old.Dispose();
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateTrayGauge: {ex.Message}"); }
        }

        // ── CPU self-watch (SMD Win at 50 % CPU on an i5-4570, cause not visible in the log) ──────────────────
        private System.Windows.Threading.DispatcherTimer? _cpuWatch;
        private TimeSpan _cpuWatchPrev;
        private DateTime _cpuWatchAt;
        private int _cpuWatchHigh;
        private readonly System.Collections.Generic.Dictionary<int, double> _cpuWatchThreads = new();

        /// <summary>
        /// Every 30 s: SMD Win's own CPU use. When it stays above 15 % of the whole PC for a minute, one log line says what was going
        /// on — the page shown, minimized or not, open windows, the ETW traffic monitor, the busiest threads — so the next log
        /// explains a high CPU without a profiler.
        /// </summary>
        private void RememberThreadTimes(System.Diagnostics.Process me)
        {
            _cpuWatchThreads.Clear();
            foreach (System.Diagnostics.ProcessThread t in me.Threads)
                try { _cpuWatchThreads[t.Id] = t.TotalProcessorTime.TotalSeconds; } catch (Exception tex) { AppLogger.Swallowed(tex); }
        }

        private void StartCpuSelfWatch()
        {
            if (_cpuWatch != null) return;
            ThreadTags.Set("UI");
            using (var me = System.Diagnostics.Process.GetCurrentProcess()) _cpuWatchPrev = me.TotalProcessorTime;
            _cpuWatchAt = DateTime.UtcNow;
            _cpuWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _cpuWatch.Tick += (_, _) =>
            {
                try
                {
                    using var me = System.Diagnostics.Process.GetCurrentProcess();
                    var now = DateTime.UtcNow;
                    double pct = (me.TotalProcessorTime - _cpuWatchPrev).TotalMilliseconds
                                 / ((now - _cpuWatchAt).TotalMilliseconds * Environment.ProcessorCount) * 100;
                    _cpuWatchPrev = me.TotalProcessorTime; _cpuWatchAt = now;
                    if (pct < 15 || ThreadTags.SelfTestRunning) { _cpuWatchHigh = 0; RememberThreadTimes(me); return; }
                    if (++_cpuWatchHigh < 2) { RememberThreadTimes(me); return; }          // a minute in a row
                    if (_cpuWatchHigh > 2 && _cpuWatchHigh % 10 != 0) { RememberThreadTimes(me); return; }   // then every 5 minutes
                    string panel = string.IsNullOrEmpty(_currentPanel) ? "Dashboard" : _currentPanel;
                    var windows = string.Join(", ", System.Windows.Application.Current.Windows.OfType<Window>()
                        .Where(w => w.IsVisible && w != this).Select(w => w.GetType().Name));
                    // per thread: CPU seconds in the LAST interval (not since the thread started), with its tag when known
                    var nowTimes = new System.Collections.Generic.Dictionary<int, double>();
                    foreach (System.Diagnostics.ProcessThread t in me.Threads)
                        try { nowTimes[t.Id] = t.TotalProcessorTime.TotalSeconds; } catch (Exception tex) { AppLogger.Swallowed(tex); }
                    double secs = _cpuWatch!.Interval.TotalSeconds;
                    var threads = nowTimes
                        .Select(kv => (kv.Key, d: kv.Value - (_cpuWatchThreads.TryGetValue(kv.Key, out var pv) ? pv : 0)))
                        .OrderByDescending(x => x.d).Take(5)
                        .Select(x => { string tag = ThreadTags.Of(x.Key); return $"#{x.Key}{(tag.Length > 0 ? " " + tag : "")} {x.d / secs * 100:F0}%"; });
                    _cpuWatchThreads.Clear();
                    foreach (var kv in nowTimes) _cpuWatchThreads[kv.Key] = kv.Value;
                    var etw = EtwNetMonitor.Instance;
                    AppLogger.Warning($"CPU-WATCH: SMD Win used {pct:F0}% of the PC for the last minute — page {panel}, " +
                        $"{(WindowState == WindowState.Minimized || !IsVisible ? "minimized/hidden" : "visible")}, other windows [{windows}], " +
                        $"ETW {(etw.Running ? $"on ({etw.Totals.events} recorded / {etw.RawEvents} callbacks)" : "off")}, busiest threads (share of one core) {string.Join(", ", threads)}");
                }
                catch (Exception ex) { AppLogger.Debug($"CPU-WATCH: {ex.Message}"); }
            };
            _cpuWatch.Start();
        }
    }
}
