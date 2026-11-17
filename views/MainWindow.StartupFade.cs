using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The first appearance of the main window: the window is kept off the screen by DWM ("cloaked") from the moment it is
    /// created, so the empty black frame Windows draws before the first WPF frame is never seen. When start-up is done, the
    /// window is shown, drawn for a few frames while still hidden, then revealed with a short fade of its content (when
    /// animations are on).
    /// </summary>
    public partial class MainWindow
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_CLOAK = 13;
        private bool _cloaked;

        private void SetCloak(bool on)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int v = on ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0) _cloaked = on;
            }
            catch (Exception ex) { AppLogger.Debug($"SetCloak({on}): {ex.Message}"); }
        }

        private void CloakUntilReady()
        {
            SetCloak(true);
            if (!_cloaked) return;
            // safety: never stay hidden if start-up does not reach the end
            var safety = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            safety.Tick += (_, _) => { safety.Stop(); if (_cloaked) { AppLogger.Warning("Start-up did not reveal the window in 60 s — shown now"); SetCloak(false); } };
            safety.Start();
        }

        /// <summary>Called just before the window becomes visible: waits until it has drawn a few frames, then shows it.</summary>
        private void RevealWithFade()
        {
            bool anim = SettingsService.Current.EnableAnimations;
            var root = FindName("RootGrid") as UIElement;
            if (anim && root != null) root.Opacity = 0;
            if (!_cloaked && !anim) return;

            int frames = 0;
            bool done = false;
            EventHandler? onRender = null;
            var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            void Finish()
            {
                if (done) return;
                done = true;
                if (onRender != null) CompositionTarget.Rendering -= onRender;
                fallback.Stop();
                SetCloak(false);
                if (root == null) return;
                if (!anim) { root.Opacity = 1; return; }
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (_, _) => { root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = 1; };
                root.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            onRender = (_, _) => { if (++frames >= 3) Finish(); };
            CompositionTarget.Rendering += onRender;
            fallback.Tick += (_, _) => Finish();
            fallback.Start();
        }
    }
}
