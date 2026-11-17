using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Color = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// After a theme change, texts that were given a status colour as a fixed brush (UEFI, Licensed, Connected, "no recent
    /// crashes"…) still had the colour of the old theme: the dark theme's light green (#46C07A) on white, 2.3:1 (16.11.2026
    /// self-test, Dark → Light). The window is walked once and every status colour of either theme, or the old fixed ones, is
    /// tied to the theme resource instead — it follows every later theme change by itself.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Dictionary<uint, string> StatusColorKeys = new()
        {
            [0x46C07A] = "StatusGoodBrush", [0x15803D] = "StatusGoodBrush", [0x22C55E] = "StatusGoodBrush", [0x16A34A] = "StatusGoodBrush",
            [0xF2A33A] = "StatusCheckBrush", [0xB45309] = "StatusCheckBrush", [0xF59E0B] = "StatusCheckBrush", [0xD97706] = "StatusCheckBrush",
            [0xF26464] = "StatusProblemBrush", [0xDC2626] = "StatusProblemBrush", [0xEF4444] = "StatusProblemBrush",
        };

        private void RemapStatusColorsAfterTheme()
        {
            try
            {
                int n = 0;
                Walk(this, ref n);
                if (n > 0) AppLogger.Debug($"Theme: {n} status colours tied to the theme");
            }
            catch (Exception ex) { AppLogger.Debug($"RemapStatusColorsAfterTheme: {ex.Message}"); }

            static uint Rgb(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

            static void Walk(DependencyObject d, ref int n)
            {
                if (d is TextBlock tb && tb.Foreground is SolidColorBrush fb && fb.Color.A == 255
                    && DependencyPropertyHelper.GetValueSource(tb, TextBlock.ForegroundProperty).BaseValueSource == BaseValueSource.Local
                    && StatusColorKeys.TryGetValue(Rgb(fb.Color), out var key))
                { tb.SetResourceReference(TextBlock.ForegroundProperty, key); n++; }
                else if (d is System.Windows.Shapes.Shape sh && sh.Fill is SolidColorBrush sb && sb.Color.A == 255
                    && DependencyPropertyHelper.GetValueSource(sh, System.Windows.Shapes.Shape.FillProperty).BaseValueSource == BaseValueSource.Local
                    && StatusColorKeys.TryGetValue(Rgb(sb.Color), out var key2))
                { sh.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key2); n++; }

                int count = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(d, i), ref n);
            }
        }
    }
}
