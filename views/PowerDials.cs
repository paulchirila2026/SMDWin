using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush      = System.Windows.Media.Brush;
using Brushes    = System.Windows.Media.Brushes;
using Color      = System.Windows.Media.Color;
using Point      = System.Windows.Point;
using Pen        = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using Size       = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// The power plan dial (Power page): a half circle of blocks, like the SMD Win logo, with a needle. The first ten blocks
    /// are the data colour (light → strong), the last four go to orange and red — the plans that save no energy at all.
    /// Power Saver lights 4 blocks, Balanced 7, High Performance 10, Ultimate all 14.
    /// </summary>
    public sealed class PlanDial : FrameworkElement
    {
        public const int Blocks = 14;

        public static readonly DependencyProperty LitProperty = DependencyProperty.Register(
            nameof(Lit), typeof(double), typeof(PlanDial), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>How many blocks are lit (0–14); fractions light the last block partly while the needle moves.</summary>
        public double Lit { get => (double)GetValue(LitProperty); set => SetValue(LitProperty, value); }

        public string LeftLabel { get; set; } = "Saver";
        public string RightLabel { get; set; } = "Max";

        public PlanDial()
        {
            MinWidth = 120; MinHeight = 70;
            Loaded   += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        /// <summary>Moves the needle to <paramref name="lit"/> blocks (a short glide when animations are on).</summary>
        public void MoveTo(double lit)
        {
            lit = Math.Clamp(lit, 0, Blocks);
            if (SMDWin.Services.SettingsService.Current?.EnableAnimations == true && IsLoaded)
                BeginAnimation(LitProperty, new DoubleAnimation(lit, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { BeginAnimation(LitProperty, null); Lit = lit; }
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? 280 : available.Width;
            double h = double.IsInfinity(available.Height) ? w * 0.6 : Math.Min(available.Height, w * 0.6);
            return new Size(Math.Min(w, 420), Math.Max(MinHeight, Math.Min(h, 250)));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            const double labelPad = 16;
            double R = Math.Min((W - 8) / 2, H - labelPad - 6);
            if (R < 30) return;
            double cx = W / 2, cy = R + 4;
            // the same ring thickness and gaps as the dials of the Dashboard (GaugeArc), so every dial of the app looks alike
            double rOut = R, rIn = R - GaugeArc.Thickness(R) * 1.25;
            double seg = 180.0 / Blocks, gap = GaugeArc.GapDeg(seg);
            double lit = Math.Clamp(Lit, 0, Blocks);
            var track = GaugeArc.Track(this);

            for (int i = 0; i < Blocks; i++)
            {
                double a0 = 180 + i * seg + gap / 2, a1 = 180 + (i + 1) * seg - gap / 2;
                Brush on = BlockBrush(i);
                Brush fill;
                if (lit >= i + 1) fill = on;
                else if (lit > i) { var b = on.CloneCurrentValue(); b.Opacity = 0.3 + 0.7 * (lit - i); fill = b; }
                else fill = track;
                dc.DrawGeometry(fill, null, GaugeArc.Segment(cx, cy, rOut, rIn, a0, a1));
            }

            // the needle: from the hub to just inside the blocks
            var ink = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            double ang = (180 + 180 * lit / Blocks) * Math.PI / 180;
            double len = rIn * 0.92;
            var tip = new Point(cx + len * Math.Cos(ang), cy + len * Math.Sin(ang));
            var pen = new Pen(ink, Math.Max(2.5, R * 0.03)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(cx, cy), tip);
            var hubFill = TryFindResource("BgCardBrush") as Brush ?? Brushes.White;
            var hubPen = new Pen(ink, Math.Max(2.5, R * 0.028)); hubPen.Freeze();
            dc.DrawEllipse(hubFill, hubPen, new Point(cx, cy), Math.Max(5, R * 0.06), Math.Max(5, R * 0.06));

            // the two ends of the scale
            var text2 = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double fs = Math.Max(9, Math.Min(12, R * 0.085));
            var fl = new FormattedText(LeftLabel, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, fs, text2, dpi);
            var fr = new FormattedText(RightLabel, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, ui, fs, text2, dpi);
            double midL = cx - (rOut + rIn) / 2, midR = cx + (rOut + rIn) / 2;
            dc.DrawText(fl, new Point(midL - fl.Width / 2, cy + 4));
            dc.DrawText(fr, new Point(midR - fr.Width / 2, cy + 4));
        }

        /// <summary>The colour of block <paramref name="i"/> when lit: the data colour along the first ten, then orange, then red.</summary>
        private Brush BlockBrush(int i)
        {
            if (i >= 12) return Opaque("StatusProblemBrush", i == 12 ? 0.8 : 1.0);
            if (i >= 10) return Opaque("StatusCheckBrush", i == 10 ? 0.8 : 1.0);
            var a = (TryFindResource("DataBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
            if (TryFindResource("DataEndColor") is not Color e) return Freeze(new SolidColorBrush(a));
            double t = i / 9.0;
            byte L(byte x, byte y) => (byte)(x + (y - x) * t);
            // light at the saving end, the full data colour towards performance
            return Freeze(new SolidColorBrush(Color.FromRgb(L(e.R, a.R), L(e.G, a.G), L(e.B, a.B))));
        }

        private Brush Opaque(string key, double opacity)
        {
            var c = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Orange;
            return Freeze(new SolidColorBrush(c) { Opacity = opacity });
        }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }

    /// <summary>
    /// A full ring of blocks with the percentage in the middle (Power page: the battery charge and the battery health) — the
    /// same blocks as the other dials of the app. The colour is given by the page (charge / health zone).
    /// </summary>
    public sealed class SegmentRing : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(SegmentRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        public int Segments { get; set; } = 26;   // ≈ the block size of the Dashboard dials (20 blocks over 270°)
        /// <summary>The theme key of the lit blocks (e.g. StatusGoodBrush).</summary>
        public string FillKey { get; set; } = "DataBrush";
        public bool HasValue { get; set; } = true;

        public SegmentRing()
        {
            MinWidth = 60; MinHeight = 60;
            Loaded   += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => SMDWin.Services.ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        public void Show(double value, string fillKey)
        {
            FillKey = fillKey;
            HasValue = true;
            value = Math.Clamp(value, 0, 100);
            if (SMDWin.Services.SettingsService.Current?.EnableAnimations == true && IsLoaded && Math.Abs(value - Value) > 0.5)
                BeginAnimation(ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { BeginAnimation(ValueProperty, null); Value = value; }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double R = Math.Min(W, H) / 2 - 2;
            if (R < 20) return;
            double cx = W / 2, cy = H / 2;
            // the same ring thickness and gaps as the dials of the Dashboard (GaugeArc)
            double rOut = R, rIn = R - GaugeArc.Thickness(R);
            int n = Math.Max(8, Segments);
            double seg = 360.0 / n, gap = GaugeArc.GapDeg(seg);
            double f = HasValue ? Math.Clamp(Value, 0, 100) / 100.0 * n : 0;
            var on = TryFindResource(FillKey) as Brush ?? TryFindResource("DataBrush") as Brush ?? Brushes.DodgerBlue;
            var track = GaugeArc.Track(this);
            for (int i = 0; i < n; i++)
            {
                double a0 = -90 + i * seg + gap / 2, a1 = -90 + (i + 1) * seg - gap / 2;
                Brush fill;
                if (f >= i + 1) fill = on;
                else if (f > i) { var b = on.CloneCurrentValue(); b.Opacity = 0.3 + 0.7 * (f - i); fill = b; }
                else fill = track;
                dc.DrawGeometry(fill, null, GaugeArc.Segment(cx, cy, rOut, rIn, a0, a1));
            }

            // the percentage in the middle
            var ink = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var mono = new Typeface(GaugeArc.NumFont(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            string txt = HasValue ? Math.Round(Value).ToString("F0", CultureInfo.InvariantCulture) + "%" : "—";
            double size = Math.Max(12, rIn * 0.62);
            var ft = new FormattedText(txt, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, mono, size, ink, dpi);
            double caps = GaugeArc.CapsHeight(mono, size);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Baseline + caps / 2));
        }
    }
}
