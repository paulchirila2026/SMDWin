using System;
using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Size    = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// Block bar (as in the rest of the app): <see cref="Blocks"/> equal blocks on whole pixels, lit up to
    /// <see cref="Percent"/>; a partly lit block fills proportionally. The colour of the lit blocks comes from the
    /// <see cref="FillKey"/> resource, unlit ones are light grey.
    /// </summary>
    public sealed class BlockBar : FrameworkElement
    {
        private int _blocks = 12;
        private double _percent;
        private string _fillKey = "DataBrush";

        public int Blocks { get => _blocks; set { _blocks = Math.Clamp(value, 1, 60); InvalidateVisual(); } }
        public double Percent { get => _percent; set { _percent = value; InvalidateVisual(); } }
        public string FillKey { get => _fillKey; set { _fillKey = value ?? "DataBrush"; InvalidateVisual(); } }

        public BlockBar()
        {
            Height = 8;
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        protected override Size MeasureOverride(Size available)
            => new(double.IsInfinity(available.Width) ? 120 : available.Width, double.IsNaN(Height) ? 8 : Height);

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < _blocks * 2 || H < 2) return;

            Brush lit = TryFindResource(_fillKey) as Brush ?? TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
            Brush off = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

            // split in physical pixels: all blocks have exactly the same width at any scaling
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double gapPx = Math.Max(1, Math.Round(2 * s));
            var (first, size) = BatteryBlocks.Split(Math.Floor(W * s), _blocks, gapPx);
            if (size < 1) return;

            var levels = BatteryBlocks.LitLevels(_blocks, _percent);
            var guides = new GuidelineSet();
            for (int i = 0; i <= _blocks; i++)
            {
                double x = first + i * (size + gapPx);
                guides.GuidelinesX.Add(x / s); guides.GuidelinesX.Add((x - gapPx) / s);
            }
            dc.PushGuidelineSet(guides);
            for (int i = 0; i < _blocks; i++)
            {
                var cell = new Rect((first + i * (size + gapPx)) / s, 0, size / s, H);
                dc.PushOpacity(0.18);
                dc.DrawRoundedRectangle(off, null, cell, 1.5, 1.5);
                dc.Pop();
                if (levels[i] <= 0) continue;
                double w = levels[i] >= 1 ? cell.Width : Math.Max(Math.Min(2 / s, cell.Width), cell.Width * levels[i]);
                dc.DrawRoundedRectangle(lit, null, new Rect(cell.X, 0, w, H), Math.Min(1.5, w / 2), 1.5);
            }
            dc.Pop();
        }
    }
}
