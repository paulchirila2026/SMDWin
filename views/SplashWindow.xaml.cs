using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SMDWin.Services;

namespace SMDWin.Views
{
    public partial class SplashWindow : Window
    {
        private readonly DispatcherTimer _failsafe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool _closed;

        public SplashWindow()
        {
            InitializeComponent();

            // The WPF-UI resources (fonts/styles) — the splash is the first window created, before MainWindow calls Apply() on its
            // pages. (only when the splash is on the app thread — on its separate thread the app resources are not touched)
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                try { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this); }
                catch (Exception ex) { AppLogger.Debug($"SplashWindow theme apply: {ex.Message}"); }
            }

            // The DWM attributes (Mica / rounded corners) are applied on SourceInitialized, NOT in the constructor: on an HWND that
            // is not yet part of the desktop's visual composition, DWM may silently ignore them.
            this.SourceInitialized += SplashWindow_SourceInitialized;

            try { TxtStatus.Text = LanguageService.S("Splash", "Starting"); }
            catch (Exception ex) { AppLogger.Debug($"SplashWindow localization: {ex.Message}"); }

            try
            {
                TxtFooter.Text = SMDWin.Services.AppVersionInfo.Short;
            }
            catch (Exception ex) { AppLogger.Debug($"SplashWindow footer: {ex.Message}"); }

            SetProgress(0);

            // the splash animations:
            //   • entry: the central block rises 8 px and fades in over ~450 ms
            //   • the logo fills with the real progress, smoothly (see SetProgress) — it replaces the progress bar, which is hidden
            StartSplashAnimations();

            StartupProgress.StepChanged += OnStepChanged;
            StartupProgress.Ready += OnReady;
            Closed += (_, _) =>
            {
                _closed = true;
                StartupProgress.StepChanged -= OnStepChanged;
                StartupProgress.Ready -= OnReady;
                _failsafe.Stop();
            };

            // Failsafe (30 s): if a real start-up step hangs (e.g. WMI timeout), the user is not kept in front of a splash that
            // never closes.
            _failsafe.Tick += (_, _) =>
            {
                _failsafe.Stop();
                AppLogger.Warning(new TimeoutException("Splash failsafe triggered"), "SplashWindow: start-up took over 30 s, the splash closes anyway");
                OnReady();
            };
            _failsafe.Start();
        }

        private void StartSplashAnimations()
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                CenterBlock.Opacity = 0;
                CenterBlock.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
                CenterShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            }
            catch (Exception ex) { AppLogger.Debug($"SplashWindow intro animation: {ex.Message}"); }

        }

        // ===================== WINDOW / DWM =====================

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private void TryApplyBackdrop()
        {
            try
            {
                bool applied = Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(this, Wpf.Ui.Controls.WindowBackdropType.Mica);
                AppLogger.Info($"SplashWindow Mica (WPF-UI): ApplyBackdrop returned {applied}");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SplashWindow Mica (WPF-UI): exception in ApplyBackdrop"); }
        }

        private void SplashWindow_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (SMDWin.Services.ThemeManager.IsWindows11())
                {
                    SMDWin.Services.ThemeManager.ApplyRoundedCorners(hwnd);
                    TryApplyBackdrop();
                }
                else
                {
                    AppLogger.Info("SplashWindow Mica: skipped — IsWindows11() returned false.");
                    try
                    {
                        if (GetClientRect(hwnd, out RECT rc))
                        {
                            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                            int radiusPx = (int)Math.Round(12 * dpiScale);
                            IntPtr region = CreateRoundRectRgn(0, 0, rc.Right - rc.Left + 1, rc.Bottom - rc.Top + 1, radiusPx, radiusPx);
                            if (region != IntPtr.Zero) SetWindowRgn(hwnd, region, true);
                        }
                    }
                    catch (Exception exRgn) { AppLogger.Debug($"SplashWindow SetWindowRgn: {exRgn.Message}"); }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SplashWindow_SourceInitialized: {ex.Message}"); }
        }

        // ===================== PROGRES =====================

        private void OnStepChanged(StartupStep step, string message)
        {
            if (_closed) return;
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = message;

                // Real progress: each step is worth 1/N of the bar; while the current step is active, half a segment is added so the bar
                // does not look stuck at a fixed value for the whole step.
                int current = (int)step;
                SetProgress((current + 0.5) / 4.0 * 100.0);
            });
        }

        /// <summary>
        /// Updates the real progress bar. Uses Star GridLength on ColFill/ColEmpty (see SplashWindow.xaml), not pixels — so it
        /// does not depend on ActualWidth and needs no recalculation on resize.
        /// </summary>
        internal void SetProgress(double percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            ColFill.Width = new GridLength(percent, GridUnitType.Star);
            ColEmpty.Width = new GridLength(100 - percent, GridUnitType.Star);
            AnimateLogoTo(percent);
        }

        private double _logoTarget = -1;

        /// <summary>
        /// The logo follows the progress without visible steps: it glides to the new value, then keeps creeping slowly towards
        /// the next step (never reaching it) — a step such as PawnIO can last a while, and the logo must not look stuck.
        /// </summary>
        private void AnimateLogoTo(double percent)
        {
            try
            {
                if (PbSplash.Parent is FrameworkElement track) track.Visibility = Visibility.Collapsed;   // the logo replaces the bar
                double target = percent / 100.0 * SmdLogo.SegmentCount;
                if (Math.Abs(target - _logoTarget) < 0.01) return;
                _logoTarget = target;
                if (!SMDWin.Services.SettingsService.Current.EnableAnimations) { SplashLogo.Level = target; return; }
                // glide to the value (0.7 s), then creep a quarter of a step further over 12 s
                double creepTo = Math.Min(SmdLogo.SegmentCount - 0.3, target + SmdLogo.SegmentCount / 4.0 * 0.45);
                var anim = new DoubleAnimationUsingKeyFrames();
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                if (percent < 100)
                    anim.KeyFrames.Add(new EasingDoubleKeyFrame(creepTo, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(12700)),
                        new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                SplashLogo.BeginAnimation(SmdLogo.LevelProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            catch (Exception ex) { AppLogger.Debug($"SplashWindow logo progress: {ex.Message}"); }
        }

        private void OnReady()
        {
            if (_closed) return;
            Dispatcher.Invoke(() =>
            {
                if (_closed) return;
                SetProgress(100);
                try { TxtStatus.Text = LanguageService.S("Splash", "Ready"); }
                catch (Exception ex) { AppLogger.Debug($"SplashWindow.OnReady localization: {ex.Message}"); }

                // the logo reaches the top, the pulse is drawn (0.4 s) and beats once, then the splash fades
                bool anim = SMDWin.Services.SettingsService.Current.EnableAnimations;
                try
                {
                    if (anim)
                    {
                        SplashLogo.BeginAnimation(SmdLogo.PulseProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                            { BeginTime = TimeSpan.FromMilliseconds(100), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                        var beat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
                        beat.Tick += (_, _) => { beat.Stop(); try { SplashLogo.StartHeartbeatOnce(); } catch (Exception bex) { AppLogger.Debug($"SplashWindow beat: {bex.Message}"); } };
                        beat.Start();
                    }
                    else SplashLogo.PulseProgress = 1;
                }
                catch (Exception ex) { AppLogger.Debug($"SplashWindow pulse: {ex.Message}"); }
                var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220))
                    { BeginTime = TimeSpan.FromMilliseconds(anim ? 1050 : 0) };
                fadeOut.Completed += (_, _) =>
                {
                    try { Close(); }
                    catch (Exception ex) { AppLogger.Debug($"SplashWindow.Close: {ex.Message}"); }
                };
                BeginAnimation(OpacityProperty, fadeOut);
            });
        }
    }
}
