using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen   = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;

namespace SMDWin.Views
{
    /// <summary>
    /// The one grid used by every chart in the app (Dashboard, Temp &amp; Stress, Storage, Network, Benchmark, Power…):
    /// no frame around the chart, only thin horizontal and vertical lines, in a neutral colour, each line fading out
    /// towards both of its ends. The lines are 1 physical pixel wide and sit on the pixel grid, so they stay sharp at any
    /// screen scaling. Only the inner lines are drawn (no outer edges), so the chart does not look boxed in.
    /// </summary>
    public static class ChartGrid
    {
        /// <summary>The number of columns for a plot of this width: about one every 70 px, between 3 and 12.</summary>
        public static int AutoColumns(double width) => Math.Clamp((int)Math.Round(width / 70.0), 3, 12);

        /// <summary>The grid colour for the current theme: the text colour at a low opacity (about 11 % on Dark, 13 % on Light).</summary>
        public static Color LineColor(FrameworkElement? fe = null)
        {
            object? r = fe?.TryFindResource("TextPrimaryBrush") ?? System.Windows.Application.Current?.TryFindResource("TextPrimaryBrush");
            var c = (r as SolidColorBrush)?.Color ?? Color.FromRgb(0xE6, 0xEA, 0xF2);
            bool light = c.R + c.G + c.B < 3 * 128;
            return Color.FromArgb(light ? (byte)34 : (byte)28, c.R, c.G, c.B);
        }

        /// <summary>The device pixels per DIP of an element (1 when it is not on screen yet).</summary>
        public static double Scale(Visual? v)
        {
            try { return v == null ? 1 : VisualTreeHelper.GetDpi(v).DpiScaleX; } catch { return 1; }
        }

        // the fading brushes, reused while the colour and the plot stay the same (the charts redraw every second)
        private static readonly Dictionary<(Color, double, double, bool), Brush> s_fades = new();

        private static Brush Fade(Color c, double from, double to, bool horizontal)
        {
            var key = (c, Math.Round(from, 1), Math.Round(to, 1), horizontal);
            if (s_fades.TryGetValue(key, out var b)) return b;
            if (s_fades.Count > 256) s_fades.Clear();
            var clear = Color.FromArgb(0, c.R, c.G, c.B);
            var g = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = horizontal ? new Point(from, 0) : new Point(0, from),
                EndPoint   = horizontal ? new Point(to, 0)   : new Point(0, to),
            };
            g.GradientStops.Add(new GradientStop(clear, 0));
            g.GradientStops.Add(new GradientStop(c, 0.2));
            g.GradientStops.Add(new GradientStop(c, 0.8));
            g.GradientStops.Add(new GradientStop(clear, 1));
            g.Freeze();
            s_fades[key] = g;
            return g;
        }

        private static double Snap(double v, double scale) => (Math.Round(v * scale) + 0.5) / scale;

        /// <summary>One horizontal line in the grid style (fading at both ends), for charts that need a line at a given value.</summary>
        public static void HLine(DrawingContext dc, double x0, double x1, double y, Color c, double scale = 1)
        {
            if (x1 - x0 < 2) return;
            scale = scale > 0 ? scale : 1;
            var pen = new Pen(Fade(c, x0, x1, true), 1 / scale);
            double yy = Snap(y, scale);
            dc.DrawLine(pen, new Point(x0, yy), new Point(x1, yy));
        }

        /// <summary>One vertical line in the grid style (fading at both ends).</summary>
        public static void VLine(DrawingContext dc, double x, double y0, double y1, Color c, double scale = 1)
        {
            if (y1 - y0 < 2) return;
            scale = scale > 0 ? scale : 1;
            var pen = new Pen(Fade(c, y0, y1, false), 1 / scale);
            double xx = Snap(x, scale);
            dc.DrawLine(pen, new Point(xx, y0), new Point(xx, y1));
        }

        /// <summary>
        /// The grid over the plot area <paramref name="r"/>: <paramref name="rows"/> rows and <paramref name="cols"/> columns
        /// (0 = <see cref="AutoColumns"/>), inner lines only.
        /// </summary>
        public static void Draw(DrawingContext dc, Rect r, Color c, int rows = 4, int cols = 0, double scale = 1)
        {
            if (r.Width < 8 || r.Height < 8 || c.A == 0) return;
            if (cols <= 0) cols = AutoColumns(r.Width);
            rows = Math.Max(1, rows);
            for (int i = 1; i < rows; i++) HLine(dc, r.Left, r.Right, r.Top + r.Height * i / rows, c, scale);
            // the vertical lines follow the "Gridlines" switch in the chart menu (on by default)
            if (!SMDWin.Services.SettingsService.Current.ShowChartGridlines) return;
            for (int i = 1; i < cols; i++) VLine(dc, r.Left + r.Width * i / cols, r.Top, r.Bottom, c, scale);
        }

        /// <summary>
        /// For charts built from shapes on a Canvas: adds the grid over the plot area as one rectangle at the bottom of the
        /// canvas (behind the lines), not hit-testable.
        /// </summary>
        public static void AddTo(System.Windows.Controls.Canvas cv, Rect plot, int rows = 4, int cols = 0)
        {
            if (Background(plot.Width, plot.Height, LineColor(cv), rows, cols, Scale(cv)) is not Brush b) return;
            var rect = new System.Windows.Shapes.Rectangle { Width = plot.Width, Height = plot.Height, Fill = b, IsHitTestVisible = false };
            System.Windows.Controls.Canvas.SetLeft(rect, plot.Left);
            System.Windows.Controls.Canvas.SetTop(rect, plot.Top);
            cv.Children.Insert(0, rect);
        }

        /// <summary>For Canvas charts: one horizontal grid-style line (fading at its ends) at a labelled value.</summary>
        public static void AddHLine(System.Windows.Controls.Canvas cv, double x0, double x1, double y)
        {
            if (x1 - x0 < 2) return;
            double sc = Scale(cv) > 0 ? Scale(cv) : 1, yy = Snap(y, sc);
            cv.Children.Insert(0, new System.Windows.Shapes.Line
            {
                X1 = x0, X2 = x1, Y1 = yy, Y2 = yy, StrokeThickness = 1 / sc, IsHitTestVisible = false,
                Stroke = Fade(LineColor(cv), x0, x1, true),
            });
        }

        /// <summary>The same grid as a background brush for a canvas of the given size (the Dashboard charts draw on canvases).</summary>
        public static Brush? Background(double w, double h, Color c, int rows = 4, int cols = 0, double scale = 1)
        {
            if (w < 8 || h < 8) return null;
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h));   // keeps the drawing at the full size
                Draw(dc, new Rect(0, 0, w, h), c, rows, cols, scale);
            }
            dg.Freeze();
            var b = new DrawingBrush(dg)
            {
                Stretch = Stretch.None, TileMode = TileMode.None,
                ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h), Viewport = new Rect(0, 0, w, h),
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            };
            b.Freeze();
            return b;
        }
    }
}
