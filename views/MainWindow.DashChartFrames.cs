using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Pen   = System.Windows.Media.Pen;

namespace SMDWin.Views
{
    /// <summary>
    /// The Dashboard chart grids: no frame, only the shared chart grid (<see cref="ChartGrid"/>: thin neutral lines that fade
    /// towards their ends). The Border around each chart canvas (…Frame in DashboardPage.xaml) is kept as a plain container;
    /// the grid is the canvas background, redrawn when the chart changes size or the theme changes.
    /// </summary>
    public partial class MainWindow
    {
        private bool _chartFramesHooked;

        private IEnumerable<(Canvas? Chart, string Comp)> DashFramedCharts() => new (Canvas?, string)[]
        {
            (SparkCpu, "CompCpu"), (DashCpuTempChart, "CompCpu"),
            (SparkGpu, "CompGpu"), (DashGpuTempChart, "CompGpu"),
            (SparkRam, "CompRam"),
            (SparkNetActivity, "CompNet"), (DashPingChart, "CompNet"),
        };

        /// <summary>Called with every chart redraw; does the work once (and again on resize / theme change).</summary>
        private void EnsureDashChartFrames()
        {
            if (_chartFramesHooked) return;
            _chartFramesHooked = true;
            try
            {
                foreach (var (c, comp) in DashFramedCharts())
                {
                    if (c == null) continue;
                    string key = comp;
                    c.SizeChanged += (_, _) => PaintChartFrame(c, key);
                    PaintChartFrame(c, key);
                }
                ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var (c, comp) in DashFramedCharts()) if (c != null) PaintChartFrame(c, comp);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex) { AppLogger.Debug($"EnsureDashChartFrames: {ex.Message}"); }
        }

        private void PaintChartFrame(Canvas chart, string comp)
        {
            try
            {
                // no frame: only the shared grid (thin lines that fade towards their ends), the same as on every other chart
                if (chart.Parent is Border frame) { frame.BorderThickness = new Thickness(0); frame.BorderBrush = null; }
                chart.Background = ChartGrid.Background(chart.ActualWidth, chart.ActualHeight, ChartGrid.LineColor(chart), 4, 0, ChartGrid.Scale(chart))
                                   ?? (Brush)Brushes.Transparent;
            }
            catch (Exception ex) { AppLogger.Debug($"PaintChartFrame: {ex.Message}"); }
        }

        /// <summary>Redraws the grids (after the "Gridlines" switch in the chart menu changes).</summary>
        internal void RepaintDashChartGrids()
        {
            foreach (var (c, comp) in DashFramedCharts()) if (c != null) PaintChartFrame(c, comp);
        }
    }
}
