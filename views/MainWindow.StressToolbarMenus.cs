using System;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Temp &amp; Stress, right of the bar: the chart interval in a button with a menu ("1 min ▾") — the same menu appears on
    /// right click on the CPU / GPU cards — and the "⋯" button with Reset Min/Max and Export PDF.
    /// The choice goes through the old hidden buttons (BtnChartRange*, BtnResetChart, BtnExportStressPdf), so the rest of the
    /// code (the active interval, PDF disabled during export) stays unchanged.
    /// </summary>
    public partial class MainWindow
    {
        private static string SR(string key, string en) => LanguageService.S("Stress", key, en);

        private static readonly (int Seconds, string Key, string En)[] StressRanges =
        {
            (60,   "Range1m",  "1 min"),
            (300,  "Range5m",  "5 min"),
            (900,  "Range15m", "15 min"),
            (3600, "Range1h",  "1 h"),
        };

        private string StressRangeLabel(int seconds)
        {
            foreach (var r in StressRanges) if (r.Seconds == seconds) return SR(r.Key, r.En);
            return SR("Range1m", "1 min");
        }

        /// <summary>The text on the interval button and the tooltips of the two buttons.</summary>
        internal void UpdateStressRangeButton()
        {
            try
            {
                if (FindName("TxtStressRangeMenu") is TextBlock t) t.Text = StressRangeLabel(_stressChartRangeSeconds);
                if (FindName("BtnStressRangeMenu") is FrameworkElement b)
                    b.ToolTip = SR("RangeMenuTip", "How much time the charts show. Also available with a right-click on the CPU / GPU cards.");
                if (FindName("BtnStressMoreMenu") is FrameworkElement m)
                    m.ToolTip = SR("MoreMenuTip", "Reset Min/Max · Export PDF");
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        /// <summary>The interval menu items: the title (inactive) and the four durations, the active one checked.</summary>
        private void FillStressRangeMenu(System.Windows.Controls.ContextMenu menu)
        {
            menu.Items.Clear();
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = SR("RangeMenuTitle", "Chart range"), IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var r in StressRanges)
            {
                int secs = r.Seconds;
                var mi = new System.Windows.Controls.MenuItem
                {
                    Header = SR(r.Key, r.En),
                    IsCheckable = true,
                    IsChecked = secs == _stressChartRangeSeconds,
                };
                mi.Click += (_, _) => SetStressChartRange(secs);
                menu.Items.Add(mi);
            }
        }

        /// <summary>Changes the interval through the old hidden button with the same duration (ChartTimeRange_Click).</summary>
        private void SetStressChartRange(int seconds)
        {
            try
            {
                Border? b = seconds switch
                {
                    300  => BtnChartRange5m,
                    900  => BtnChartRange15m,
                    3600 => BtnChartRange1h,
                    _    => BtnChartRange1m,
                };
                if (b != null) ChartTimeRange_Click(b, null!);
                UpdateStressRangeButton();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SetStressChartRange"); }
        }

        internal void StressRangeMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor) return;
            try
            {
                var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                FillStressRangeMenu(menu);
                menu.IsOpen = true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "StressRangeMenu_Click"); }
        }

        /// <summary>Right click on the CPU or GPU card: the same interval menu, filled right before it opens.</summary>
        internal void StressCardRange_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.ContextMenu is System.Windows.Controls.ContextMenu menu)
                    FillStressRangeMenu(menu);
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); e.Handled = true; }
        }

        internal void StressMoreMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor) return;
            try
            {
                var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

                var reset = new System.Windows.Controls.MenuItem { Header = SR("MoreReset", "Reset Min/Max"),
                                                                   ToolTip = SR("MoreResetTip", "Clears the minimum and maximum values and restarts the charts.") };
                reset.Click += (s, a) => ResetChart_Click(s, a);
                menu.Items.Add(reset);

                var pdf = new System.Windows.Controls.MenuItem { Header = SR("MorePdf", "Export PDF report"),
                                                                 IsEnabled = BtnExportStressPdf?.IsEnabled ?? true };
                pdf.Click += (s, a) => ExportStressPdf_Click(s, a);
                menu.Items.Add(pdf);

                menu.IsOpen = true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "StressMoreMenu_Click"); }
        }
    }
}
