using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfColor   = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint   = System.Windows.Point;
using WpfSize    = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// Canvas-based sparkline renderer for PerfmonService data.
    /// Usage: create the instance, attach the Canvas to the UI, call Draw() on every tick.
    /// </summary>
    public class PerfmonSparkline
    {
        private readonly Canvas   _canvas;
        private readonly WpfColor    _lineColor;
        private readonly WpfColor    _fillColor;
        private readonly float    _maxValue;   // 0 = auto-scale
        private readonly string   _label;
        private readonly string   _unit;
        private readonly bool     _showLabels;
        private readonly bool     _useDynamicColor;

        private Polyline?  _line;
        private Polygon?   _fill;
        private TextBlock? _lblCurrent;
        private TextBlock? _lblMax;
        private TextBlock? _lblTitle;
        private Path?      _thresholdLine;

        public float ThresholdValue { get; set; } = 0f; // 0 = no threshold line
        public WpfColor ThresholdColor  { get; set; } = WpfColor.FromArgb(180, 239, 68, 68); // red

        public PerfmonSparkline(
            Canvas canvas,
            string label,
            string unit,
            WpfColor lineColor,
            float maxValue = 0f,
            bool showLabels = true,
            bool useDynamicColor = true)
        {
            _canvas    = canvas;
            _label     = label;
            _unit      = unit;
            _lineColor = lineColor;
            _fillColor = WpfColor.FromArgb(40, lineColor.R, lineColor.G, lineColor.B);
            _maxValue  = maxValue;
            _showLabels = showLabels;
            _useDynamicColor = useDynamicColor;

            BuildElements();
        }

        private void BuildElements()
        {
            // Fill polygon
            _fill = new Polygon
            {
                Fill   = new SolidColorBrush(_fillColor),
                Stroke = WpfBrushes.Transparent
            };
            _canvas.Children.Add(_fill);

            // Line
            _line = new Polyline
            {
                Stroke          = new SolidColorBrush(_lineColor),
                StrokeThickness = Ui.ChartStroke,
                StrokeLineJoin  = PenLineJoin.Round
            };
            _canvas.Children.Add(_line);

            // Threshold line (optional)
            _thresholdLine = new Path
            {
                Stroke          = new SolidColorBrush(ThresholdColor),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                Visibility      = Visibility.Collapsed
            };
            _canvas.Children.Add(_thresholdLine);

            // Title label (top-left)
            _lblTitle = new TextBlock
            {
                Text       = _label,
                FontSize   = 9,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(160, 200, 200, 210)),
                Margin     = new Thickness(6, 3, 0, 0)
            };
            Canvas.SetLeft(_lblTitle, 0);
            Canvas.SetTop(_lblTitle, 0);
            _canvas.Children.Add(_lblTitle);

            // Current value (top-right)
            _lblCurrent = new TextBlock
            {
                FontSize   = Ui.FontXS,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_lineColor),
                Margin     = new Thickness(0, 3, 6, 0)
            };
            _canvas.Children.Add(_lblCurrent);
            Canvas.SetTop(_lblCurrent, 0);

            // Max label (bottom-right, small)
            _lblMax = new TextBlock
            {
                FontSize   = 8,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(100, 200, 200, 210)),
                Margin     = new Thickness(0, 0, 6, 3)
            };
            _canvas.Children.Add(_lblMax);

            if (!_showLabels)
            {
                _lblTitle.Visibility   = Visibility.Collapsed;
                _lblCurrent.Visibility = Visibility.Collapsed;
                _lblMax.Visibility     = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Redraws the sparkline with the new data.
        /// Called from the UI thread on every PerfmonService tick.
        /// </summary>
        public void Draw(float[] values, float? overrideMax = null)
        {
            if (_canvas.ActualWidth < 10 || _canvas.ActualHeight < 10) return;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            int    n = values.Length;

            if (n == 0) return;

            float dataMax = overrideMax ?? (_maxValue > 0 ? _maxValue : Math.Max(values.Max(), 1f));
            float current = values.LastOrDefault();

            double stepX = w / Math.Max(n - 1, 1);

            // Points for the line
            var linePoints = new PointCollection(n);
            var fillPoints = new PointCollection(n + 2);

            fillPoints.Add(new WpfPoint(0, h)); // bottom-left

            for (int i = 0; i < n; i++)
            {
                double x = i * stepX;
                double y = h - (values[i] / dataMax * (h - 4)) - 2;
                y = Math.Clamp(y, 2, h - 2);

                linePoints.Add(new WpfPoint(x, y));
                fillPoints.Add(new WpfPoint(x, y));
            }

            fillPoints.Add(new WpfPoint((n - 1) * stepX, h)); // bottom-right

            if (_line  != null) _line.Points  = linePoints;
            if (_fill  != null) _fill.Points  = fillPoints;

            // Threshold line
            if (ThresholdValue > 0 && _thresholdLine != null)
            {
                double ty = h - (ThresholdValue / dataMax * (h - 4)) - 2;
                ty = Math.Clamp(ty, 2, h - 2);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new WpfPoint(0, ty), false, false);
                    ctx.LineTo(new WpfPoint(w, ty), true, false);
                }
                geo.Freeze();
                _thresholdLine.Data       = geo;
                _thresholdLine.Stroke     = new SolidColorBrush(ThresholdColor);
                _thresholdLine.Visibility = Visibility.Visible;
            }

            // Labels
            string fmt = FormatValue(current);
            if (_lblCurrent != null)
            {
                _lblCurrent.Text = $"{fmt} {_unit}";
                _lblCurrent.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_lblCurrent, w - _lblCurrent.DesiredSize.Width - 6);
            }

            if (_lblMax != null)
            {
                _lblMax.Text = $"max {FormatValue(dataMax)} {_unit}";
                _lblMax.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_lblMax,  w - _lblMax.DesiredSize.Width - 6);
                Canvas.SetTop(_lblMax,   h - _lblMax.DesiredSize.Height - 3);
            }

            // Dynamic current colour — only when _useDynamicColor is active (see the comment on the field). Otherwise the fixed colour
            // given to the constructor is kept, so paired series (Send/Recv, Read/Write) stay visually distinct.
            if (_lblCurrent != null)
            {
                WpfColor c = _useDynamicColor ? GetDynamicColor(current, dataMax) : _lineColor;
                _lblCurrent.Foreground = new SolidColorBrush(c);
                if (_line != null) _line.Stroke = new SolidColorBrush(c);
                _fill!.Fill = new SolidColorBrush(WpfColor.FromArgb(35, c.R, c.G, c.B));
            }
        }

        private static string FormatValue(float v)
        {
            if (v >= 1_000_000_000f) return $"{v / 1_000_000_000f:F1}G";
            if (v >= 1_000_000f)     return $"{v / 1_000_000f:F1}M";
            if (v >= 1_000f)         return $"{v / 1_000f:F1}K";
            if (v >= 100f)           return $"{v:F0}";
            if (v >= 10f)            return $"{v:F1}";
            return $"{v:F2}";
        }

        private static WpfColor GetDynamicColor(float current, float max)
        {
            float pct = max > 0 ? current / max : 0;
            if (pct > 0.85f) return WpfColor.FromRgb(239, 68, 68);   // red
            if (pct > 0.65f) return WpfColor.FromRgb(245, 158, 11);  // orange
            return WpfColor.FromRgb(34, 197, 94);                     // green
        }
    }
}
