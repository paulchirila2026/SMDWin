using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// ALIAS: avoids CS0104 - Brush is ambiguous between System.Drawing and System.Windows.Media (the csproj has
// UseWindowsForms=true) — the same pattern used in FanControlWindow.xaml.cs and most other windows
// (CompactWidgetWindow, DebugInfoWindow etc.).
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace SMDWin.Views
{
    public class MiniSparkline : Canvas
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<float>), typeof(MiniSparkline),
                new FrameworkPropertyMetadata(null, OnRedrawNeeded));

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(MiniSparkline),
                new FrameworkPropertyMetadata(Brushes.SteelBlue, OnRedrawNeeded));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(MiniSparkline),
                new FrameworkPropertyMetadata(0.0, OnRedrawNeeded));

        public double MaxValue
        {
            get => (double)GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }

        public IReadOnlyList<float>? Values
        {
            get => (IReadOnlyList<float>?)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        /// <summary>The line colour — usually bound to CpuColor/RamColor on ProcessEntry, so the sparkline has the same colour
        /// code as the bar/text in the cell next to it.</summary>
        public Brush LineBrush
        {
            get => (Brush)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        private readonly Polyline _line = new()
        {
            StrokeThickness = 1.2,
            StrokeLineJoin  = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
        };

        public MiniSparkline()
        {
            Children.Add(_line);
            // ActualWidth/Height are not valid yet at construction (layout not done) — redraw when the container finally gets a
            // real size.
            SizeChanged += (_, __) => Redraw();
        }

        private static void OnRedrawNeeded(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((MiniSparkline)d).Redraw();

        private void Redraw()
        {
            _line.Stroke = LineBrush;

            var vals = Values;
            double w = ActualWidth, h = ActualHeight;
            if (vals == null || vals.Count < 2 || w <= 0 || h <= 0)
            {
                _line.Points = null;
                return;
            }

            float max = 0f;
            if (MaxValue > 0.001)
            {
                max = (float)MaxValue;
            }
            else
            {
                foreach (var v in vals)
                    if (!float.IsNaN(v) && v > max) max = v;
                if (max <= 0.001f) max = 1f; // all values 0 (idle process) — avoids division by 0, the line comes out flat at the bottom
            }

            var pts = new PointCollection(vals.Count);
            for (int i = 0; i < vals.Count; i++)
            {
                double x = w * i / (vals.Count - 1);
                float v = float.IsNaN(vals[i]) ? 0f : vals[i];
                double y = h - Math.Clamp(v / max, 0.0, 1.0) * h;
                pts.Add(new Point(x, y));
            }
            _line.Points = pts;
        }
    }
}
