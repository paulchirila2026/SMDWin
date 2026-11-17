using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Common rule for history charts: right-click on the chart → the time window shown
    /// (e.g. 5 min · 10 min · 30 min · 1 h). The current one is ticked.
    /// </summary>
    public static class ChartRangeMenu
    {
        /// <param name="target">The chart (or card) the menu opens on.</param>
        /// <param name="windowsSeconds">The time windows offered, in seconds.</param>
        /// <param name="current">The window used now.</param>
        /// <param name="select">Called when another window is chosen.</param>
        public static void Attach(FrameworkElement target, IReadOnlyList<int> windowsSeconds, Func<int> current, Action<int> select)
        {
            var menu = new ContextMenu();
            foreach (int seconds in windowsSeconds)
            {
                var item = new MenuItem
                {
                    Header = LanguageService.S("Common", "ChartLast", "Last") + " " + ChartScale.FormatWindow(seconds),
                    IsCheckable = true,
                    Tag = seconds,
                };
                item.Click += (_, _) =>
                {
                    try { select(seconds); }
                    catch (Exception ex) { AppLogger.Debug($"ChartRangeMenu select {seconds}: {ex.Message}"); }
                };
                menu.Items.Add(item);
            }
            // the tick is set when opening: the current window may be changed from another chart that shares it
            menu.Opened += (_, _) =>
            {
                int now = current();
                foreach (var o in menu.Items)
                    if (o is MenuItem mi && mi.Tag is int s) mi.IsChecked = s == now;
            };
            target.ContextMenu = menu;
            if (target.ToolTip == null)
                target.ToolTip = LanguageService.S("Common", "ChartRangeTip", "Right-click to change the time range");
        }
    }
}
