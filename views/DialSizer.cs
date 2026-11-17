using System;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Panel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// The dials on the Dashboard, Temp &amp; Stress and RAM pick a size step (S / M / L) from the space in the card body,
    /// instead of shrinking freely together with the text. On large screens the dial grows (L), on low windows it simplifies
    /// (S: no sub-value and no min / max row).
    /// Enabled with <c>local:DialSizer.Auto="True"</c> on the <see cref="Viewbox"/> (the <c>CompDialBox</c> style already does it).
    /// </summary>
    public static class DialSizer
    {
        // Scale relative to the base dial (124 × 112): S ≈ 96 px wide, M = 124, L ≈ 160.
        private const double ScaleS = 0.77, ScaleM = 1.0, ScaleL = 1.29;
        // The step changes only when the space really requires it (hysteresis), so it does not oscillate in auto-height rows.
        private const double UpgradeMargin = 12, DowngradeTolerance = 0.97;
        // The arc height without the min / max line.
        private const double ArcH = 112;

        public static readonly DependencyProperty AutoProperty =
            DependencyProperty.RegisterAttached("Auto", typeof(bool), typeof(DialSizer),
                new PropertyMetadata(false, OnAutoChanged));

        public static bool GetAuto(DependencyObject d) => (bool)d.GetValue(AutoProperty);
        public static void SetAuto(DependencyObject d, bool value) => d.SetValue(AutoProperty, value);

        private static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(DialSizer), new PropertyMetadata(false));

        private static readonly DependencyProperty ScaleProperty =
            DependencyProperty.RegisterAttached("Scale", typeof(double), typeof(DialSizer), new PropertyMetadata(ScaleM));

        private static readonly DependencyProperty BaseHeightProperty =
            DependencyProperty.RegisterAttached("BaseHeight", typeof(double), typeof(DialSizer), new PropertyMetadata(double.NaN));

        private static void OnAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Viewbox vb) return;
            vb.Loaded -= OnLoaded;
            if ((bool)e.NewValue) vb.Loaded += OnLoaded;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Viewbox vb || (bool)vb.GetValue(HookedProperty)) return;
            if (vb.Parent is not FrameworkElement host) return;
            vb.SetValue(HookedProperty, true);
            host.SizeChanged += (_, _) => Apply(vb, host);
            Apply(vb, host);
        }

        private static void Apply(Viewbox vb, FrameworkElement host)
        {
            if (vb.Child is not FrameworkElement child) return;
            double avail = host.ActualHeight;
            if (avail <= 0) return;

            double baseH = (double)vb.GetValue(BaseHeightProperty);
            if (double.IsNaN(baseH))
            {
                baseH = double.IsNaN(child.Height) ? ArcH : child.Height;
                vb.SetValue(BaseHeightProperty, baseH);
            }
            double baseW = double.IsNaN(child.Width) ? 124 : child.Width;

            double cur = (double)vb.GetValue(ScaleProperty);
            double next = cur;
            // Until it settles (at most 3 steps): smaller only when the space runs out, larger only with spare room.
            for (int i = 0; i < 3; i++)
            {
                double need = (next <= ScaleS ? ArcH : baseH) * next;
                double step = next;
                if (avail < need * DowngradeTolerance)
                    step = next >= ScaleL ? ScaleM : ScaleS;
                else if (next <= ScaleS && avail >= baseH * ScaleM + UpgradeMargin)
                    step = ScaleM;
                else if (next == ScaleM && avail >= baseH * ScaleL + UpgradeMargin)
                    step = ScaleL;
                if (step == next) break;
                next = step;
            }

            bool first = double.IsInfinity(vb.MaxHeight);
            if (next == cur && !first) return;

            vb.SetValue(ScaleProperty, next);
            bool small = next <= ScaleS;
            bool wasSmall = cur <= ScaleS && !first;
            // Without the min / max row in S, the dial has only the arc height.
            child.Height = small ? ArcH : baseH;
            vb.StretchDirection = StretchDirection.Both;
            vb.MaxHeight = (small ? ArcH : baseH) * next;
            vb.MaxWidth = baseW * next;
            if (small != wasSmall) ApplyDetail(child, small);
        }

        // The S step hides the sub-value and the min / max row (they would end up below 11 px).
        private static void ApplyDetail(FrameworkElement child, bool small)
        {
            if (child is not Panel panel) return;
            var sub = Application.Current?.TryFindResource("CompDialSub") as Style;
            var range = Application.Current?.TryFindResource("CompDialRange") as Style;
            foreach (var el in panel.Children)
            {
                if (el is not TextBlock tb) continue;
                if (ReferenceEquals(tb.Style, sub) || ReferenceEquals(tb.Style, range))
                    tb.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }
}
