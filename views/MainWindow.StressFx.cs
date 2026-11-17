using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;
using Border    = System.Windows.Controls.Border;
using Color     = System.Windows.Media.Color;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// Temp &amp; Stress, while a test runs, on the CPU / GPU card of the component under test: under the dial, a thin line that
    /// fills up to the chosen stop time, and the time left ("7:42 left"); with no stop time (∞) only the running time.
    /// Updated from OnTempTick (UpdateStressFx).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Shows / hides the progress line on both cards, from the tests that actually run.</summary>
        private void UpdateStressFx()
        {
            try
            {
                bool psu = _psuStress.IsRunning;
                bool cpuOn = _cpuStress.Running || psu;
                bool gpuOn = _gpuStress.IsRunning || _gpuStressUiRunning || psu;

                DateTime? cpuStart = _cpuStress.Running ? (_cpuStress.StartedUtc ?? _cpuStressStartUtc) : psu ? _psuStress.StartedUtc : null;
                DateTime? gpuStart = _gpuStress.IsRunning || _gpuStressUiRunning ? (_gpuStress.StartedUtc ?? _gpuStressStartUtc) : psu ? _psuStress.StartedUtc : null;

                StSetRunBox("StCpu", cpuOn, cpuStart);
                StSetRunBox("StGpu", gpuOn, gpuStart);
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateStressFx: {ex.Message}"); }
        }

        // ── The progress line + time left ─────────────────────────────────────

        private void StSetRunBox(string prefix, bool on, DateTime? startUtc)
        {
            if (FindName(prefix + "RunBox") is not StackPanel box) return;
            if (!on)
            {
                if (box.Visibility != Visibility.Collapsed) box.Visibility = Visibility.Collapsed;
                if (FindName(prefix + "RunFill") is Border f0) SetFill(f0, 0, false);
                return;
            }
            box.Visibility = Visibility.Visible;
            var now = DateTime.UtcNow;
            var text = FindName("Txt" + prefix + "RunLeft") as TextBlock;
            var fill = FindName(prefix + "RunFill") as Border;
            var track = fill?.Parent as Border;

            if (_stressAutoStopAtUtc is DateTime end && _stressDurationMinutes > 0)
            {
                var total = TimeSpan.FromMinutes(_stressDurationMinutes);
                var left = end - now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                double frac = Math.Clamp(1 - left.TotalSeconds / total.TotalSeconds, 0, 1);
                if (track != null) track.Visibility = Visibility.Visible;
                if (fill != null) SetFill(fill, frac, true);
                if (text != null) text.Text = string.Format(SP("RunLeft", "{0} left"), Clock(left));
            }
            else
            {
                // no stop time (∞): only how long it has been running
                if (track != null) track.Visibility = Visibility.Collapsed;
                var elapsed = startUtc is DateTime s ? now - s : TimeSpan.Zero;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (text != null) text.Text = string.Format(SP("RunElapsed", "running · {0}"), Clock(elapsed));
            }

            static string Clock(TimeSpan t) => t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        /// <summary>The filled part of the line, as a horizontal scale (0–1), sliding smoothly to the new value.</summary>
        private static void SetFill(Border fill, double frac, bool animate)
        {
            if (fill.RenderTransform is not ScaleTransform st || st.IsFrozen)
            {
                st = new ScaleTransform(0, 1);
                fill.RenderTransform = st;
            }
            if (!animate || SettingsService.Current?.EnableAnimations == false || Math.Abs(st.ScaleX - frac) > 0.25)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.ScaleX = frac;
                return;
            }
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(frac, TimeSpan.FromMilliseconds(950)));
        }
    }
}
