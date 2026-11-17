using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfHAlign = System.Windows.HorizontalAlignment;

namespace SMDWin.Services
{
    public static class FillBarBuilder
    {
        public static Border Build(double fraction, WpfColor fillColor, double height = 8, Thickness? margin = null)
        {
            double clamped = Math.Clamp(fraction, 0, 1);
            var track = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(height / 2),
                Background = new WpfBrush(WpfColor.FromArgb(40, 128, 128, 128)),
                Margin = margin ?? new Thickness(0),
                ClipToBounds = true,
            };
            var fill = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(height / 2),
                HorizontalAlignment = WpfHAlign.Left,
                Background = new WpfBrush(fillColor),
            };
            // The real width is known only after layout — Loaded covers the first draw,
            // SizeChanged covers later window/column resizes.
            fill.Loaded += (_, _) => fill.Width = Math.Max(2, track.ActualWidth * clamped);
            track.SizeChanged += (_, _) => fill.Width = Math.Max(2, track.ActualWidth * clamped);
            track.Child = fill;
            return track;
        }

    }
}
