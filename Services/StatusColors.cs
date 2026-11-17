using System;
using System.Collections.Generic;
using WpfBrush   = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor   = System.Windows.Media.Color;

namespace SMDWin.Services
{
    /// <summary>
    /// Status colours written as text in the models ("#22C55E", "#F59E0B", "#EF4444" — also used as keys in comparisons)
    /// are displayed in the current theme's colour (StatusGood / StatusCheck / StatusProblem), readable on white too.
    /// The models stay unchanged; the conversion happens only at display time (<see cref="SMDWin.Views.ThemeStatusBrushConverter"/>).
    /// </summary>
    public static class StatusColors
    {
        private static readonly Dictionary<string, string> LegacyToKey = new(StringComparer.OrdinalIgnoreCase)
        {
            ["#22C55E"] = "StatusGoodBrush",    ["#16A34A"] = "StatusGoodBrush",
            ["#F59E0B"] = "StatusCheckBrush",   ["#D97706"] = "StatusCheckBrush",
            ["#EF4444"] = "StatusProblemBrush", ["#DC2626"] = "StatusProblemBrush",
        };

        /// <summary>Brush to display for a colour given as text; the old status colours become the theme colours.</summary>
        public static WpfBrush ToBrush(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return WpfBrushes.Gray;
            string h = hex.Trim();
            if (LegacyToKey.TryGetValue(h, out var key) && System.Windows.Application.Current?.TryFindResource(key) is WpfBrush themed)
                return themed;
            try { return new System.Windows.Media.SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(h)!); }
            catch (FormatException) { return WpfBrushes.Gray; }
        }
    }
}
