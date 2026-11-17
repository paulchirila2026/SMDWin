using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SMDWin.Services;
using Application = System.Windows.Application;
using Border      = System.Windows.Controls.Border;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using ButtonBase  = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox    = System.Windows.Controls.CheckBox;
using Control     = System.Windows.Controls.Control;
using RadioButton = System.Windows.Controls.RadioButton;
using Color       = System.Windows.Media.Color;
using TextBlock   = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// Safety net for buttons: dark text on a dark background (or light on light) appears when a colour was left from the
    /// old theme or set once from code. For every loaded button and after every theme change, the text contrast is compared
    /// with the button's real background; if it is too low, the local value is cleared (the style colour returns) and, if it
    /// still does not read, white or black is chosen.
    /// </summary>
    public static class ButtonContrastGuard
    {
        private const double MinRatio = 2.2;
        private static bool _installed;
        private static System.Windows.Threading.DispatcherTimer? _timer;
        private static readonly DependencyProperty ForcedProperty =
            DependencyProperty.RegisterAttached("GuardForced", typeof(bool), typeof(ButtonContrastGuard), new PropertyMetadata(false));

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
                ThemeManager.ThemeChanged += () => Application.Current?.Dispatcher.BeginInvoke(new Action(() => ScanAllWindows()),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ButtonContrastGuard.Install"); }
            StartTimer();
        }

        /// <summary>The states that change colours after loading (selected / active / disabled) raise no event of their own:
        /// while the app is in the foreground, the visible part of the windows is rechecked every few seconds.</summary>
        private static void StartTimer()
        {
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp == null) return;
                _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ContextIdle, disp)
                    { Interval = TimeSpan.FromSeconds(2.5) };
                _timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (Application.Current is not { } app) return;
                        foreach (Window w in app.Windows)
                            if (w.IsVisible && w.IsActive) Scan(w, 0);
                    }
                    catch (Exception ex) { AppLogger.Debug("ButtonContrastGuard.Timer: " + ex.Message); }
                };
                _timer.Start();
            }
            catch (Exception ex) { AppLogger.Debug("ButtonContrastGuard.StartTimer: " + ex.Message); }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ButtonBase b) Fix(b);
        }

        public static int ScanAllWindows()
        {
            int fixedCount = 0;
            try
            {
                foreach (Window w in Application.Current.Windows) fixedCount += Scan(w, 0);
                if (fixedCount > 0) AppLogger.Debug($"ButtonContrastGuard: {fixedCount} buttons corrected after the theme change");
            }
            catch (Exception ex) { AppLogger.Debug("ButtonContrastGuard.ScanAllWindows: " + ex.Message); }
            return fixedCount;
        }

        /// <summary>Checks and corrects all buttons under <paramref name="root"/>; returns the number corrected.</summary>
        public static int Scan(DependencyObject root, int depth)
        {
            if (depth > 200) return 0;
            if (root is UIElement hidden && !hidden.IsVisible) return 0;   // hidden parts are not visible, not checked
            int n = 0;
            if (root is ButtonBase b && Fix(b)) n++;
            else if (root is Border pill && FixPill(pill)) n++;
            int count = root is Visual || root is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
            for (int i = 0; i < count; i++) n += Scan(VisualTreeHelper.GetChild(root, i), depth + 1);
            return n;
        }

        /// <summary>Buttons with too little contrast (for the self-test / log), without changing them.</summary>
        public static List<string> FindLowContrast(DependencyObject root)
        {
            var list = new List<string>();
            void Walk(DependencyObject node, int depth)
            {
                if (depth > 200) return;
                if (node is UIElement hidden && !hidden.IsVisible) return;   // hidden controls are not visible: not reported
                if (node is ButtonBase b && IsLow(b)) list.Add(DescribeButton(b));
                int c = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
                for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
            }
            Walk(root, 0);
            return list;
        }

        private static string DescribeButton(ButtonBase b)
            => string.IsNullOrEmpty(b.Name) ? (b.Content as string ?? b.GetType().Name) : b.Name;

        private static bool Fix(ButtonBase b)
        {
            try
            {
                if (b is CheckBox || !b.IsEnabled) return false;   // a disabled button is pale on purpose
                // on hover / pressed the colours come from the style triggers (e.g. the window X: red with white) and go away by
                // themselves; a colour forced now would stay after the mouse leaves (white X on a light background)
                if (b.IsMouseOver || b.IsPressed) return false;
                if (!TryEffectiveBackground(b, out Color bg)) return false;
                if (b is RadioButton && !IsSaturatedDark(bg)) return false;   // the usual chips have a light background and their own colours
                bool changed = false;

                // coloured buttons (darker blue / red / green): dark text has no place on them, even if the numeric ratio looks acceptable
                if (b.Foreground is SolidColorBrush darkFg && IsSaturatedDark(bg) && Luminance(Color.FromRgb(darkFg.Color.R, darkFg.Color.G, darkFg.Color.B)) < 0.2)
                {
                    b.SetCurrentValue(Control.ForegroundProperty, Brushes.White);
                    changed = true;
                }
                else if (b.Foreground is SolidColorBrush fg && Ratio(fg.Color, bg) < MinRatio)
                {
                    b.ClearValue(Control.ForegroundProperty);
                    if (b.Foreground is SolidColorBrush fg2 && Ratio(fg2.Color, bg) < MinRatio)
                        b.SetCurrentValue(Control.ForegroundProperty, Readable(bg));
                    changed = true;
                }

                // text and icons with their own colour, left unreadable on this background
                var good = b.Foreground as SolidColorBrush;
                foreach (var d in Descendants(b))
                {
                    if (d is TextBlock tb && tb.ReadLocalValue(TextBlock.ForegroundProperty) is SolidColorBrush tf && (Ratio(tf.Color, bg) < MinRatio || (IsSaturatedDark(bg) && Luminance(Color.FromRgb(tf.Color.R, tf.Color.G, tf.Color.B)) < 0.2)))
                    { tb.ClearValue(TextBlock.ForegroundProperty); changed = true; }
                    else if (d is System.Windows.Shapes.Shape sh)
                    {
                        if (sh.ReadLocalValue(System.Windows.Shapes.Shape.StrokeProperty) is SolidColorBrush ss && Ratio(ss.Color, bg) < MinRatio)
                        { sh.Stroke = good ?? Readable(bg); changed = true; }
                        if (sh.ReadLocalValue(System.Windows.Shapes.Shape.FillProperty) is SolidColorBrush sf && sf.Color.A > 40 && Ratio(sf.Color, bg) < MinRatio)
                        { sh.Fill = good ?? Readable(bg); changed = true; }
                    }
                }
                return changed;
            }
            catch (Exception ex) { AppLogger.Debug("ButtonContrastGuard.Fix: " + ex.Message); return false; }
        }

        /// <summary>"Buttons" made of a Border with a TextBlock (chips, segments): on a dark coloured background dark text becomes
        /// white; when the background is no longer coloured, the forced value is cleared.</summary>
        private static bool FixPill(Border pill)
        {
            try
            {
                bool dark = pill.IsEnabled && pill.Opacity >= 0.9 && pill.Background is SolidColorBrush s && s.Color.A >= 160 && s.Opacity >= 0.9 && IsSaturatedDark(Color.FromRgb(s.Color.R, s.Color.G, s.Color.B));
                bool changed = false;
                foreach (var d in PillTexts(pill))
                {
                    if (d is not TextBlock tb) continue;
                    if (dark)
                    {
                        if (tb.Foreground is SolidColorBrush f && Luminance(Color.FromRgb(f.Color.R, f.Color.G, f.Color.B)) < 0.2)
                        { tb.SetCurrentValue(TextBlock.ForegroundProperty, Brushes.White); tb.SetValue(ForcedProperty, true); changed = true; }
                    }
                    else if ((bool)tb.GetValue(ForcedProperty))
                    { tb.ClearValue(TextBlock.ForegroundProperty); tb.SetValue(ForcedProperty, false); changed = true; }
                }
                return changed;
            }
            catch (Exception ex) { AppLogger.Debug("ButtonContrastGuard.FixPill: " + ex.Message); return false; }
        }

        private static bool IsLow(ButtonBase b)
            => b is not CheckBox && b.IsEnabled && TryEffectiveBackground(b, out Color bg) && (b is not RadioButton || IsSaturatedDark(bg))
               && b.Foreground is SolidColorBrush fg
               && (Ratio(fg.Color, bg) < MinRatio || (IsSaturatedDark(bg) && Luminance(Color.FromRgb(fg.Color.R, fg.Color.G, fg.Color.B)) < 0.2));

        /// <summary>The texts sitting directly on this Border's background: an inner Border with its own background has its own check.</summary>
        private static IEnumerable<DependencyObject> PillTexts(Border pill)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(pill);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                int n = cur is Visual ? VisualTreeHelper.GetChildrenCount(cur) : 0;
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(cur, i);
                    if (c is ButtonBase) continue;
                    if (c is Border inner && inner.Background is SolidColorBrush ib && ib.Color.A >= 160) continue;
                    yield return c;
                    stack.Push(c);
                }
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                int n = cur is Visual ? VisualTreeHelper.GetChildrenCount(cur) : 0;
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(cur, i);
                    if (c is ButtonBase) continue;      // a button inside a button has its own check
                    yield return c;
                    stack.Push(c);
                }
            }
        }

        /// <summary>The button's visible background: the template's border (if any), otherwise Background.</summary>
        private static bool TryEffectiveBackground(ButtonBase b, out Color color)
        {
            color = default;
            Brush? br = null;
            double opacity = b.Opacity;
            if (b.Template?.FindName("Bd", b) is Border bd) { br = bd.Background; opacity *= bd.Opacity; }
            br ??= b.Background;
            // semi-transparent background ("soft" style, e.g. a pale red pill): the real colour is mixed with the background, it is
            // not judged by the solid colour
            if (br != null) opacity *= br.Opacity;
            if (opacity < 0.9) return false;
            if (br is SolidColorBrush s && s.Color.A >= 160) { color = Color.FromRgb(s.Color.R, s.Color.G, s.Color.B); return true; }
            return false;
        }

        private static bool IsSaturatedDark(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min >= 90 && Luminance(c) < 0.5;
        }

        private static Brush Readable(Color bg) => Luminance(bg) > 0.45 ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)) : Brushes.White;

        private static double Luminance(Color c)
        {
            static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        private static double Ratio(Color a, Color b)
        {
            double l1 = Luminance(Color.FromRgb(a.R, a.G, a.B)), l2 = Luminance(b);
            if (l1 < l2) (l1, l2) = (l2, l1);
            return (l1 + 0.05) / (l2 + 0.05);
        }
    }
}
