using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen     = System.Windows.Media.Pen;
using Size    = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// Battery drawn from blocks (Power page + the preview in Developer › Design experiments).
    /// The battery outline with its terminal, and inside the blocks given by <see cref="BatteryBlocks.Blocks"/> for
    /// <see cref="Variant"/>: lit in the level colour (green / orange / red), unlit in light grey.
    /// While the battery charges, the next block to fill pulses; with animations off it stays half lit.
    /// Colours come from the theme, so the drawing looks right on the dark theme too.
    /// </summary>
    public sealed class BatteryPicture : FrameworkElement
    {
        private const double Stroke = 2;      // the outline thickness
        private const double Nub = 5;         // cat iese borna in afara corpului
        private const double Corner = 7;      // the body rounding
        private const double Inset = 5;       // from the edge of the body to the blocks

        private readonly DispatcherTimer _pulse;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _variant = BatteryBlocks.Default;
        private double _percent;
        private bool _charging;
        private bool _hasReading = true;

        /// <summary>One of the variants from <see cref="BatteryBlocks.Styles"/>.</summary>
        public string Variant
        {
            get => _variant;
            set
            {
                string v = BatteryBlocks.IsBlockStyle(value) ? value : BatteryBlocks.Default;
                if (_variant == v) return;
                _variant = v; InvalidateMeasure(); InvalidateVisual();
            }
        }

        /// <summary>The charge, 0–100.</summary>
        public double Percent
        {
            get => _percent;
            set { if (Math.Abs(_percent - value) < 0.01) return; _percent = value; UpdatePulse(); InvalidateVisual(); }
        }

        public bool Charging
        {
            get => _charging;
            set { if (_charging == value) return; _charging = value; UpdatePulse(); InvalidateVisual(); }
        }

        /// <summary>false = the charge could not be read: all blocks unlit.</summary>
        public bool HasReading
        {
            get => _hasReading;
            set { if (_hasReading == value) return; _hasReading = value; UpdatePulse(); InvalidateVisual(); }
        }

        public BatteryPicture()
        {
            _pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
            _pulse.Tick += (_, _) => InvalidateVisual();
            Loaded   += (_, _) => { ThemeManager.ThemeChanged += OnTheme; UpdatePulse(); };
            Unloaded += (_, _) => { ThemeManager.ThemeChanged -= OnTheme; _pulse.Stop(); };
            IsVisibleChanged += (_, _) => UpdatePulse();
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        /// <summary>The pulse runs only while there is something to show: charging, not full, the drawing is on screen, animations are on.</summary>
        private void UpdatePulse()
        {
            bool run = _charging && _hasReading && _percent < 100 && IsLoaded && IsVisible
                       && SettingsService.Current.EnableAnimations;
            if (run == _pulse.IsEnabled) return;
            if (run) _pulse.Start(); else _pulse.Stop();
        }

        private static Size NaturalSize(string variant)
        {
            var shape = BatteryBlocks.ShapeOf(variant);
            if (shape.Portrait) return new Size(56, 106);
            return shape.Rows > 1 ? new Size(112, 58) : new Size(132, 58);
        }

        protected override Size MeasureOverride(Size available)
        {
            var n = NaturalSize(_variant);
            return new Size(Math.Min(n.Width, available.Width), Math.Min(n.Height, available.Height));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < 24 || H < 24) return;

            Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
            var outline = Res("TextPrimaryBrush", Brushes.Black);
            var off     = Res("TextSecondaryBrush", Brushes.Gray);
            Brush ZoneBrush(BatteryZone z) => z switch
            {
                BatteryZone.Crit => Res("StatusCritBrush", Brushes.Red),
                BatteryZone.Warn => Res("StatusWarnBrush", Brushes.Orange),
                _                => Res("StatusOkBrush", Brushes.Green),
            };

            var shape = BatteryBlocks.ShapeOf(_variant);
            Rect body, nub;
            if (shape.Portrait)
            {
                double nubW = Math.Round(W * 0.36);
                body = new Rect(Stroke / 2, Nub + Stroke / 2, W - Stroke, H - Nub - Stroke);
                nub  = new Rect((W - nubW) / 2, 0, nubW, Nub + Stroke);
            }
            else
            {
                double nubH = Math.Round(H * 0.34);
                body = new Rect(Stroke / 2, Stroke / 2, W - Nub - Stroke, H - Stroke);
                nub  = new Rect(W - Nub - Stroke, (H - nubH) / 2, Nub + Stroke, nubH);
            }

            // the outline and the terminal together (the terminal goes slightly under the outline, so the joint does not show)
            dc.PushOpacity(0.85);
            dc.DrawRoundedRectangle(outline, null, nub, 2.5, 2.5);
            dc.DrawRoundedRectangle(null, new Pen(outline, Stroke), body, Corner, Corner);
            dc.Pop();

            // the blocks: split in physical pixels, so they all have exactly the same size at any scaling
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double left = body.X - Stroke / 2 + Inset, top = body.Y - Stroke / 2 + Inset;
            double innerW = body.Width + Stroke - 2 * Inset, innerH = body.Height + Stroke - 2 * Inset;
            double gapPx = Math.Max(1, Math.Round((shape.PartialByOpacity ? 3 : 2) * s));
            var (firstX, sizeX) = BatteryBlocks.Split(Math.Floor(innerW * s), shape.Cols, gapPx);
            var (firstY, sizeY) = BatteryBlocks.Split(Math.Floor(innerH * s), shape.Rows, gapPx);
            if (sizeX < 1 || sizeY < 1) return;

            double leftPx = Math.Round(left * s), topPx = Math.Round(top * s);
            var guides = new GuidelineSet();
            for (int c = 0; c <= shape.Cols; c++)
            {
                double x = leftPx + firstX + c * (sizeX + gapPx);
                guides.GuidelinesX.Add(x / s); guides.GuidelinesX.Add((x - gapPx) / s);
            }
            for (int r = 0; r <= shape.Rows; r++)
            {
                double y = topPx + firstY + r * (sizeY + gapPx);
                guides.GuidelinesY.Add(y / s); guides.GuidelinesY.Add((y - gapPx) / s);
            }

            double phase = _clock.Elapsed.TotalSeconds / BatteryBlocks.PulseSeconds;
            double radius = Math.Min(2, Math.Min(sizeX, sizeY) / s / 3);

            dc.PushGuidelineSet(guides);
            foreach (var b in BatteryBlocks.Blocks(_variant, _hasReading ? _percent : 0, _charging && _hasReading))
            {
                var cell = new Rect((leftPx + firstX + b.Col * (sizeX + gapPx)) / s,
                                    (topPx + firstY + b.Row * (sizeY + gapPx)) / s, sizeX / s, sizeY / s);

                dc.PushOpacity(0.18);
                dc.DrawRoundedRectangle(off, null, cell, radius, radius);
                dc.Pop();

                var lit = ZoneBrush(b.Zone);
                if (b.Charging)
                {
                    // the charging block: the whole block, pulsing (static, half lit, with animations off)
                    dc.PushOpacity(_pulse.IsEnabled ? BatteryBlocks.PulseOpacity(phase) : 0.55);
                    dc.DrawRoundedRectangle(lit, null, cell, radius, radius);
                    dc.Pop();
                }
                else if (b.Lit >= 1)
                {
                    dc.DrawRoundedRectangle(lit, null, cell, radius, radius);
                }
                else if (b.Lit > 0)
                {
                    if (shape.PartialByOpacity)
                    {
                        dc.PushOpacity(0.25 + 0.75 * b.Lit);
                        dc.DrawRoundedRectangle(lit, null, cell, radius, radius);
                        dc.Pop();
                    }
                    else if (shape.Portrait)
                    {
                        // fills from the bottom up
                        double h = Math.Max(Math.Min(2 / s, cell.Height), cell.Height * b.Lit);
                        dc.DrawRoundedRectangle(lit, null, new Rect(cell.X, cell.Bottom - h, cell.Width, h), radius, radius);
                    }
                    else
                    {
                        // fills from left to right
                        double w = Math.Max(Math.Min(2 / s, cell.Width), cell.Width * b.Lit);
                        dc.DrawRoundedRectangle(lit, null, new Rect(cell.X, cell.Y, w, cell.Height), Math.Min(radius, w / 2), radius);
                    }
                }
            }
            dc.Pop();
        }
    }
}
