using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SMDWin.Views
{
    /// <summary>
    /// The soft pulse around the dot at the end of a chart line: a ring of the line colour that grows and fades, every 1.8 s.
    /// It is part of the drawing (animated by WPF itself), so the chart is not redrawn for it. All charts share the same two
    /// timers, so their pulses stay in step. Nothing is drawn with animations turned off in Settings.
    /// </summary>
    internal static class ChartPulse
    {
        private static AnimationClock? _radius, _fade;
        private static readonly Dictionary<Color, SolidColorBrush> _brushes = new();

        private static void EnsureClocks()
        {
            if (_radius != null) return;
            var dur = new Duration(TimeSpan.FromMilliseconds(1800));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            var grow = new DoubleAnimation(3.5, 10, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var fade = new DoubleAnimation(0.45, 0, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            // 15 frames a second is plenty for a slow soft ring; at the default 60 the charts kept WPF drawing the window 60 times a
            // second on every page (SMD Win at 15-30 % CPU on a laptop i5 with integrated graphics)
            Timeline.SetDesiredFrameRate(grow, 15);
            Timeline.SetDesiredFrameRate(fade, 15);
            grow.Freeze(); fade.Freeze();
            _radius = grow.CreateClock();
            _fade = fade.CreateClock();
        }

        private static SolidColorBrush Brush(Color c)
        {
            if (_brushes.TryGetValue(c, out var b)) return b;
            if (_brushes.Count > 32) _brushes.Clear();
            b = new SolidColorBrush(c);
            b.ApplyAnimationClock(System.Windows.Media.Brush.OpacityProperty, _fade);
            _brushes[c] = b;
            return b;
        }

        /// <summary>Stops the timers while the window is minimized or hidden in the tray (nothing to show, no frames to draw).</summary>
        public static void Pause(bool paused)
        {
            try
            {
                if (_radius?.Controller == null || _fade?.Controller == null) return;
                if (paused) { _radius.Controller.Pause(); _fade.Controller.Pause(); }
                else { _radius.Controller.Resume(); _fade.Controller.Resume(); }
            }
            catch (Exception ex) { AppLogger.Debug($"ChartPulse.Pause: {ex.Message}"); }
        }

        /// <summary>Draws the pulse ring centred on <paramref name="center"/> (before the dot itself, so the dot stays on top).</summary>
        public static void Draw(DrawingContext dc, Point center, Color color)
        {
            if (SettingsService.Current?.EnableAnimations == false) return;
            try
            {
                EnsureClocks();
                dc.DrawEllipse(Brush(color), null, center, null, 3.5, _radius, 3.5, _radius);
            }
            catch (Exception ex) { AppLogger.Debug($"ChartPulse.Draw: {ex.Message}"); }
        }
    }
}
