using System.Windows;
using System.Windows.Controls;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        /// <summary>
        /// The single look of the segmented controls (like SegmentChipStyle in Settings) for the segments made of Border:
        /// the container provides the grey track, the chosen one is a plate with an outline, the rest are transparent.
        /// </summary>
        private static void ApplySegmentLook(Border b, bool active)
        {
            b.CornerRadius = new CornerRadius(5);
            b.Padding = new Thickness(11, 3, 11, 3);
            b.Margin = new Thickness(0);
            b.BorderThickness = new Thickness(1);
            b.ClearValue(UIElement.EffectProperty);
            if (active)
            {
                // the same pill as the radio buttons on the grey track (SegmentChipStyle): visible on the dark theme too
                b.SetResourceReference(Border.BackgroundProperty, "SegOnBrush");
                b.SetResourceReference(Border.BorderBrushProperty, "SelBorderBrush");
            }
            else
            {
                b.Background = System.Windows.Media.Brushes.Transparent;
                b.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
            if (b.Child is TextBlock tb)
            {
                tb.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimaryBrush" : "TextSecondaryBrush");
                tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }
}
