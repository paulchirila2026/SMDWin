using System;
using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfSize  = System.Windows.Size;

namespace SMDWin.Views
{
    public static class CardClipBehavior
    {
        // Radius = the simple variant, uniform on all 4 corners (double)
        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.RegisterAttached(
                "Radius", typeof(double), typeof(CardClipBehavior),
                new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

        // Radii = the full variant, with asymmetric radii (native XAML CornerRadius, accepts "12" or "0,0,10,10")
        public static readonly DependencyProperty RadiiProperty =
            DependencyProperty.RegisterAttached(
                "Radii", typeof(CornerRadius), typeof(CardClipBehavior),
                new PropertyMetadata(new CornerRadius(0), OnRadiiChanged));

        public static CornerRadius GetRadii(DependencyObject d) => (CornerRadius)d.GetValue(RadiiProperty);
        public static void SetRadii(DependencyObject d, CornerRadius value) => d.SetValue(RadiiProperty, value);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            double r = (double)e.NewValue;
            SetRadii(d, new CornerRadius(r));
        }

        private static void OnRadiiChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement el) return;

            var radii = (CornerRadius)e.NewValue;
            bool active = radii.TopLeft > 0 || radii.TopRight > 0 || radii.BottomLeft > 0 || radii.BottomRight > 0;

            if (active)
            {
                el.SizeChanged += Element_SizeChanged;
                UpdateClip(el);
            }
            else
            {
                el.SizeChanged -= Element_SizeChanged;
                el.Clip = null;
            }
        }

        private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement el) UpdateClip(el);
        }

        private static void UpdateClip(FrameworkElement el)
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            var r = GetRadii(el);
            if (w <= 0 || h <= 0) { el.Clip = null; return; }

            // Simple case (equal radii on all corners) — RectangleGeometry is enough.
            if (r.TopLeft == r.TopRight && r.TopRight == r.BottomRight && r.BottomRight == r.BottomLeft)
            {
                el.Clip = r.TopLeft > 0
                    ? new RectangleGeometry(new Rect(0, 0, w, h), r.TopLeft, r.TopLeft)
                    : null;
                return;
            }

            // Asymmetric case — the outline is built by hand with StreamGeometry (arcs on the rounded corners, straight lines on
            // those with radius 0).
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                double tl = Math.Min(r.TopLeft,     Math.Min(w, h) / 2);
                double tr = Math.Min(r.TopRight,    Math.Min(w, h) / 2);
                double br = Math.Min(r.BottomRight, Math.Min(w, h) / 2);
                double bl = Math.Min(r.BottomLeft,  Math.Min(w, h) / 2);

                ctx.BeginFigure(new WpfPoint(0, tl), true, true);
                ctx.ArcTo(new WpfPoint(tl, 0), new WpfSize(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new WpfPoint(w - tr, 0), true, false);
                ctx.ArcTo(new WpfPoint(w, tr), new WpfSize(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new WpfPoint(w, h - br), true, false);
                ctx.ArcTo(new WpfPoint(w - br, h), new WpfSize(br, br), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new WpfPoint(bl, h), true, false);
                ctx.ArcTo(new WpfPoint(0, h - bl), new WpfSize(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new WpfPoint(0, tl), true, false);
            }
            geo.Freeze();
            el.Clip = geo;
        }
    }
}
