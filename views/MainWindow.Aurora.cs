using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SMDWin.Services;
using Color = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// The soft lights behind the pages, drawn once as a picture at the screen's exact pixel size: on Dark three coloured lights,
    /// on Light one white glow behind the page title.
    /// WPF gradients are rounded to whole colour levels, and on large dark areas those levels show as steps (banding),
    /// more on some monitors than others. Here every pixel is computed in full precision and then rounded up or down at random
    /// by at most one level (dithering): the eye sees a smooth transition and no visible grain.
    /// The lights sit at fixed places (top left, right, bottom), so the page looks the same every time.
    /// The picture is redrawn only when the window size, the screen scaling or the theme changes.
    ///
    /// State tint (Dark only), three levels: blue when all is well (the base picture); a faint amber when there is a warning
    /// (the last Health check found warnings or non-dangerous critical items, or a Dashboard value is over its warning
    /// threshold); a faint red while something is dangerous (a high temperature held for a while, or a Health check item that
    /// means physical danger — DiagnosticOrchestrator.IsPhysicalDanger). The tint is a second picture that fades in over the
    /// first one — slowly, so it reads as part of the background and not as a flash. Drawn only when needed.
    /// </summary>
    public partial class MainWindow
    {
        private readonly record struct AuroraLight(double X, double Y, double Rx, double Ry, Color Color, double Strength);
        private readonly record struct AuroraSpec(int W, int H, Color Bg, AuroraLight[] Lights, double DpiX, double DpiY, bool Dark);

        private DispatcherTimer? _auroraTimer;
        private bool _auroraHooked;
        private int _auroraGen;
        private int _auroraAlertGen;
        private int _auroraAlertLevel;   // 0 = all well (no tint), 1 = warning (amber), 2 = danger (red)
        private (int w, int h, bool dark, Color bg) _auroraLast;
        private (int w, int h, bool dark, Color bg, int level) _auroraAlertLast;

        internal void AuroraLayer_Loaded(object sender, RoutedEventArgs e)
        {
            RenderAurora();
            UpdateAuroraAlert();
            if (_auroraHooked) return;
            _auroraHooked = true;
            ThemeManager.ThemeChanged += () => Dispatcher.InvokeAsync(RenderAurora, DispatcherPriority.Background);
            DpiChanged += (_, _) => ScheduleAurora();
            // the chart pulses stop while nothing is shown
            void SyncPulse() => ChartPulse.Pause(WindowState == WindowState.Minimized || !IsVisible);
            StateChanged += (_, _) => SyncPulse();
            IsVisibleChanged += (_, _) => SyncPulse();
        }

        internal void AuroraLayer_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleAurora();

        /// <summary>Waits until resizing pauses, then redraws once.</summary>
        private void ScheduleAurora()
        {
            if (_auroraTimer == null)
            {
                _auroraTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
                _auroraTimer.Tick += (_, _) => { _auroraTimer.Stop(); RenderAurora(); };
            }
            _auroraTimer.Stop();
            _auroraTimer.Start();
        }

        /// <summary>The lights and the picture size for the current window, theme and screen scaling; null while the window has no size yet.</summary>
        private AuroraSpec? BuildAuroraSpec(int level)
        {
            if (AuroraLayer == null) return null;
            bool dark = ThemeManager.IsDark(ThemeManager.Normalize(
                SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName));
            double wDip = AuroraLayer.ActualWidth, hDip = AuroraLayer.ActualHeight;
            if (wDip < 50 || hDip < 50) return null;
            var dpi = VisualTreeHelper.GetDpi(AuroraLayer);
            int w = Math.Min(6000, (int)Math.Ceiling(wDip * dpi.DpiScaleX));
            int h = Math.Min(4000, (int)Math.Ceiling(hDip * dpi.DpiScaleY));
            var bg = TryFindResource("BgDarkColor") is Color c ? c : Color.FromRgb(0x0A, 0x0E, 0x16);

            // the lights, in device pixels: positions from the edges, sizes fixed, so they do not move around between sizes
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            AuroraLight[] lights;
            if (!dark)
            {
                // Light: one soft white glow behind the page title (the "spotlight"), so the white cards seem to float, and two
                // very faint blue-grey lights in the opposite corners that give the page a little character
                lights = new[]
                {
                    new AuroraLight(w * 0.58,     -120 * sy,   1000 * sx, 440 * sy, Color.FromRgb(0xFF, 0xFF, 0xFF), 0.92),
                    new AuroraLight(180 * sx,     -60 * sy,    700 * sx,  520 * sy, Color.FromRgb(0x8F, 0xA6, 0xD8), 0.08),
                    new AuroraLight(w - 60 * sx,  h + 40 * sy, 760 * sx,  420 * sy, Color.FromRgb(0x9A, 0xA9, 0xC4), 0.06),
                };
            }
            else
            {
                // Dark: the lights grow with the window, so each one covers about a third of it (before, fixed sizes
                // left a small patch in a corner on large windows); never smaller than the old fixed sizes
                // about 30 % larger again and at 80 % of the strength: a wide soft wash over the page instead of a
                // patch in the corner; with the gentler fall-off in DrawAurora the light fades over a longer distance
                double rx1 = Math.Max(900 * sx, w * 0.82), ry1 = Math.Max(720 * sy, h * 1.05);   // top left
                double rx2 = Math.Max(800 * sx, w * 0.66), ry2 = Math.Max(780 * sy, h * 1.00);   // right
                double rx3 = Math.Max(980 * sx, w * 0.72), ry3 = Math.Max(540 * sy, h * 0.60);   // bottom
                double y2 = h * 0.38;
                // all well: blue, between the first strength and the halved one (a hint of colour, not a glow the eye goes to)
                lights = level switch
                {
                    0 => new[]
                    {
                        new AuroraLight(180 * sx,     -60 * sy,    rx1, ry1, Color.FromRgb(0x4C, 0x6E, 0xFF), 0.125),
                        new AuroraLight(w - 80 * sx,  y2,          rx2, ry2, Color.FromRgb(0xA7, 0x8B, 0xFA), 0.078),
                        new AuroraLight(w * 0.55,     h + 80 * sy, rx3, ry3, Color.FromRgb(0x2D, 0xD4, 0xBF), 0.038),
                    },
                    // a warning: the same places in a faint amber (weaker than the blue, so it is felt more than seen)
                    1 => new[]
                    {
                        new AuroraLight(180 * sx,     -60 * sy,    rx1, ry1, Color.FromRgb(0xF0, 0x9A, 0x2C), 0.11),
                        new AuroraLight(w - 80 * sx,  y2,          rx2, ry2, Color.FromRgb(0xD9, 0x77, 0x3A), 0.065),
                        new AuroraLight(w * 0.55,     h + 80 * sy, rx3, ry3, Color.FromRgb(0xB0, 0x6C, 0x2E), 0.035),
                    },
                    // danger: deep red, wine, dark rose — subtle, below the earlier alert strength
                    _ => new[]
                    {
                        new AuroraLight(180 * sx,     -60 * sy,    rx1, ry1, Color.FromRgb(0xE0, 0x30, 0x3C), 0.19),
                        new AuroraLight(w - 80 * sx,  y2,          rx2, ry2, Color.FromRgb(0xC2, 0x38, 0x6A), 0.11),
                        new AuroraLight(w * 0.55,     h + 80 * sy, rx3, ry3, Color.FromRgb(0x9A, 0x2E, 0x4C), 0.07),
                    },
                };
            }
            return new AuroraSpec(w, h, bg, lights, 96 * sx, 96 * sy, dark);
        }

        private void RenderAurora()
        {
            try
            {
                if (AuroraImage == null || BuildAuroraSpec(0) is not AuroraSpec spec) return;
                if (!spec.Dark)
                {
                    // no alert picture on Light
                    if (AuroraAlertImage != null)
                    {
                        AuroraAlertImage.BeginAnimation(UIElement.OpacityProperty, null);
                        AuroraAlertImage.Opacity = 0;
                        AuroraAlertImage.Source = null;
                    }
                    _auroraAlertLast = default;
                }

                if (!(_auroraLast == (spec.W, spec.H, spec.Dark, spec.Bg) && AuroraImage.Source != null))
                {
                    _auroraLast = (spec.W, spec.H, spec.Dark, spec.Bg);
                    int gen = ++_auroraGen;
                    _ = Task.Run(() =>
                    {
                        // computed off the UI thread; a frozen picture can be handed back
                        var bmp = DrawAurora(spec.W, spec.H, spec.Bg, spec.Lights, spec.DpiX, spec.DpiY, spec.Dark);
                        Dispatcher.InvokeAsync(() => { if (gen == _auroraGen) AuroraImage.Source = bmp; });
                    });
                }

                if (spec.Dark && _auroraAlertLevel > 0) RenderAlertImage();
            }
            catch (Exception ex) { AppLogger.Debug($"RenderAurora: {ex.Message}"); }
        }

        // ══ Alert tint ═══════════════════════════════════════════════════════
        /// <summary>
        /// Called whenever the state may have changed (a temperature alert starts or ends, a health check finishes, the setting is
        /// switched). Something is critical when a CPU or GPU temperature has been over its limit for the alert delay, or the last
        /// health check found a critical problem (the same state as the red dot on the Health button).
        /// </summary>
        internal void UpdateAuroraAlert()
        {
            try
            {
                var s = SettingsService.Current;
                bool hot = (_dashAlertState.TryGetValue("cputemp", out bool ct) && ct)
                        || (_dashAlertState.TryGetValue("gputemp", out bool gt) && gt);
                bool danger = hot || s.HealthCheckLastDanger;   // only physical danger, not every critical item
                // a warning: the last Health check found something (the orange / red dot on "PC health"), or another
                // Dashboard value is held over its threshold
                bool warn = s.HealthCheckLastSeverity is "Warning" or "Critical"
                         || _dashAlertState.Any(kv => kv.Value && kv.Key is not ("cputemp" or "gputemp"));
                int level = !s.AuroraAlertTint ? 0 : danger ? 2 : warn ? 1 : 0;
                if (level == _auroraAlertLevel) return;
                int was = _auroraAlertLevel;
                _auroraAlertLevel = level;
                if (level == 0) { FadeAuroraAlert(0); return; }
                // from amber to red (or back): fade the old tint out first, then draw and fade in the new one
                if (was > 0) FadeAuroraAlert(0, then: RenderAlertImage);
                else RenderAlertImage();   // fades in as soon as the picture is ready
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateAuroraAlert: {ex.Message}"); }
        }

        /// <summary>Draws the red picture if it is missing or outdated, then fades it in.</summary>
        private void RenderAlertImage()
        {
            try
            {
                int level = _auroraAlertLevel;
                if (level == 0 || AuroraAlertImage == null || BuildAuroraSpec(level) is not AuroraSpec spec || !spec.Dark) return;
                if (_auroraAlertLast == (spec.W, spec.H, spec.Dark, spec.Bg, level) && AuroraAlertImage.Source != null)
                {
                    FadeAuroraAlert(1);
                    return;
                }
                _auroraAlertLast = (spec.W, spec.H, spec.Dark, spec.Bg, level);
                int gen = ++_auroraAlertGen;
                _ = Task.Run(() =>
                {
                    var bmp = DrawAurora(spec.W, spec.H, spec.Bg, spec.Lights, spec.DpiX, spec.DpiY, spec.Dark);
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (gen != _auroraAlertGen || AuroraAlertImage == null) return;
                        AuroraAlertImage.Source = bmp;
                        if (_auroraAlertLevel > 0) FadeAuroraAlert(1);
                    });
                });
            }
            catch (Exception ex) { AppLogger.Debug($"RenderAlertImage: {ex.Message}"); }
        }

        /// <summary>A very slow fade (4 s in, 6 s out). With animations switched off the change is immediate.</summary>
        private void FadeAuroraAlert(double to, Action? then = null)
        {
            if (AuroraAlertImage == null) return;
            if (!SettingsService.Current.EnableAnimations || (to == 0 && AuroraAlertImage.Opacity <= 0.01))
            {
                AuroraAlertImage.BeginAnimation(UIElement.OpacityProperty, null);
                AuroraAlertImage.Opacity = to;
                then?.Invoke();
                return;
            }
            var fade = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromSeconds(to > 0 ? 4 : (then != null ? 2 : 6)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            if (then != null) fade.Completed += (_, _) => then();
            AuroraAlertImage.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static BitmapSource DrawAurora(int w, int h, Color bg, AuroraLight[] lights, double dpiX, double dpiY, bool dark = true)
        {
            var px = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                // a fixed seed per row: the dithering pattern is the same on every redraw
                uint rng = ((uint)y * 2654435761u) | 1u;
                double Rand()
                {
                    rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                    return (rng & 0xFFFFFF) / 16777216.0;
                }

                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double r = bg.R, g = bg.G, b = bg.B;
                    foreach (var l in lights)
                    {
                        double dx = (x - l.X) / l.Rx, dy = (y - l.Y) / l.Ry;
                        double d2 = dx * dx + dy * dy;
                        if (d2 >= 1) continue;
                        // smooth to zero at the edge, no visible rim; on Dark a gentler curve (t^1.5 instead of t²) so the light
                        // carries further out from its centre (Light keeps the tighter spotlight)
                        double t = 1 - d2;
                        double k = (dark ? t * Math.Sqrt(t) : t * t) * l.Strength;
                        r += (l.Color.R - r) * k;
                        g += (l.Color.G - g) * k;
                        b += (l.Color.B - b) * k;
                    }
                    // triangular dither of at most one level, then round
                    int R = Clamp(r + Rand() - Rand());
                    int G = Clamp(g + Rand() - Rand());
                    int B = Clamp(b + Rand() - Rand());
                    px[row + x] = (255 << 24) | (R << 16) | (G << 8) | B;
                }
            }
            var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Pbgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;

            static int Clamp(double v)
            {
                int i = (int)Math.Round(v);
                return i < 0 ? 0 : i > 255 ? 255 : i;
            }
        }
    }
}
