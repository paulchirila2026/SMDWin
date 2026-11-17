using System;
using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen     = System.Windows.Media.Pen;
using Point   = System.Windows.Point;
using Size    = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// Battery health: the bar has the length of the design capacity; the filled part is the capacity now, and what was
    /// lost to wear appears hatched, in grey. It shows at a glance how much the battery has lost. Colours come from the theme.
    /// </summary>
    public sealed class BatteryHealthBar : FrameworkElement
    {
        private double _health = 100;
        private string _fillKey = "StatusOkBrush";

        /// <summary>Health in percent (0–100): how much of the design capacity is left.</summary>
        public double Health { get => _health; set { _health = Math.Clamp(value, 0, 100); InvalidateVisual(); } }
        public string FillKey { get => _fillKey; set { _fillKey = value ?? "StatusOkBrush"; InvalidateVisual(); } }

        /// <summary>Colour by wear: up to 15 % fine, up to 35 % attention, above that critical.</summary>
        public static string KeyForWear(int wearPct) => wearPct <= 15 ? "StatusOkBrush" : wearPct <= 35 ? "StatusWarnBrush" : "StatusCritBrush";

        public BatteryHealthBar()
        {
            Height = 10;
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        protected override Size MeasureOverride(Size available)
            => new(double.IsInfinity(available.Width) ? 160 : available.Width, double.IsNaN(Height) ? 10 : Height);

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < 8 || H < 2) return;
            Brush lit = TryFindResource(_fillKey) as Brush ?? Brushes.SteelBlue;
            Brush gray = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

            double fillW = Math.Round(W * _health / 100.0);
            double radius = Math.Min(3, H / 2);

            // the lost part: faint background + diagonal hatching
            var lost = new Rect(0, 0, W, H);
            dc.PushClip(new RectangleGeometry(lost, radius, radius));
            dc.PushOpacity(0.14);
            dc.DrawRectangle(gray, null, lost);
            dc.Pop();
            var pen = new Pen(gray, 1);
            dc.PushOpacity(0.35);
            for (double x = fillW - H; x < W; x += 4)
                dc.DrawLine(pen, new Point(x + H, 0), new Point(x, H));
            dc.Pop();
            dc.Pop();

            if (fillW > 0)
                dc.DrawRoundedRectangle(lit, null, new Rect(0, 0, fillW, H), radius, radius);
        }
    }
}
