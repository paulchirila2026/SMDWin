using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen   = System.Windows.Media.Pen;

namespace SMDWin.Views
{
    /// <summary>
    /// The Dashboard charts in the Task Manager style: a thin frame and a fine grid (10 columns × 4 rows) in the colour of the
    /// component (processor, graphics card, memory, network). The frame is the Border around each chart canvas
    /// (…Frame in DashboardPage.xaml); the grid is the canvas background, redrawn when the chart changes size or the theme changes.
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
                var col = TryFindResource(comp + "Color") is Color cc ? cc : Color.FromRgb(0x8F, 0xBB, 0xFF);
                if (chart.Parent is Border frame)
                {
                    var fb = new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)); fb.Freeze();
                    frame.BorderBrush = fb;
                }
                double w = chart.ActualWidth, h = chart.ActualHeight;
                if (w < 20 || h < 12) return;
                // one tile = one grid cell, drawn with a 1 px line on its left and top edge
                double cw = w / 10.0, ch = h / 4.0;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(38, col.R, col.G, col.B)), 1); pen.Freeze();
                var g = new GeometryGroup();
                g.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, ch)));
                g.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(cw, 0.5)));
                g.Freeze();
                var tile = new DrawingBrush(new GeometryDrawing(null, pen, g))
                {
                    TileMode = TileMode.Tile, Stretch = Stretch.None, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, cw, ch), Viewbox = new Rect(0, 0, cw, ch),
                    AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                };
                tile.Freeze();
                chart.Background = tile;
            }
            catch (Exception ex) { AppLogger.Debug($"PaintChartFrame: {ex.Message}"); }
        }
    }
}
