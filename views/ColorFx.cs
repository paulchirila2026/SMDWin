using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;
using Color = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// Text that changes colour smoothly (about half a second) instead of jumping, e.g. a dial figure turning orange or red
    /// when the value passes a limit. <c>ColorFx.Foreground(textBlock, "StatusWarnBrush")</c> blends from the colour shown now to
    /// the theme colour of that key. With animations turned off in Settings the colour is set at once.
    /// The text block gets its own brush; after a theme change it follows on the next call (every sensor read).
    /// </summary>
    internal static class ColorFx
    {
        private sealed class State
        {
            public SolidColorBrush Brush = null!;
            public Color Target;
        }

        private static readonly ConditionalWeakTable<TextBlock, State> _states = new();
        private static readonly IEasingFunction Ease = CreateEase();

        private static IEasingFunction CreateEase()
        {
            var e = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            e.Freeze();
            return e;
        }

        public static void Foreground(TextBlock? tb, string brushKey)
        {
            if (tb == null) return;
            try
            {
                if (tb.TryFindResource(brushKey) is not SolidColorBrush target)
                {
                    tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                    return;
                }
                var to = target.Color;
                bool animate = SettingsService.Current?.EnableAnimations != false && tb.IsVisible;

                if (!_states.TryGetValue(tb, out var st) || !ReferenceEquals(tb.Foreground, st.Brush))
                {
                    // the first call (or the colour was set elsewhere meanwhile): start from the colour shown now
                    var from = (tb.Foreground as SolidColorBrush)?.Color ?? to;
                    st = new State { Brush = new SolidColorBrush(from), Target = from };
                    _states.AddOrUpdate(tb, st);
                    tb.Foreground = st.Brush;
                }
                if (st.Target == to) return;
                st.Target = to;

                if (!animate)
                {
                    st.Brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    st.Brush.Color = to;
                    return;
                }
                // From = the colour shown right now (also in the middle of a previous blend)
                var anim = new ColorAnimation(to, TimeSpan.FromMilliseconds(550)) { EasingFunction = Ease };
                // at the end the colour is written as the brush's own value and the animation is removed: a brush that keeps a
                // finished animation still holds its old base colour, and pictures of the window (self-test screenshots) or a
                // later theme change can show that old colour (the dial figures came out almost black on Dark)
                var brush = st.Brush;
                anim.Completed += (_, _) =>
                {
                    if (st.Target != to) return;   // a newer blend has started meanwhile
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    brush.Color = to;
                };
                st.Brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
            catch (Exception ex) { AppLogger.Debug($"ColorFx.Foreground: {ex.Message}"); }
        }
    }
}
