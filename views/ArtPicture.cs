using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point   = System.Windows.Point;
using Size    = System.Windows.Size;

namespace SMDWin.Views
{
    /// <summary>
    /// Draws an illustration given as a shape list (<see cref="ArtDrawing"/>, from Services/HardwareArt.cs):
    /// scales it uniformly to fit the element and picks each shape's colour by its role, from the theme resources.
    /// The shapes know nothing about WPF, so the same drawing can be checked outside the app.
    /// </summary>
    public sealed class ArtPicture : FrameworkElement
    {
        public static readonly DependencyProperty DrawingProperty = DependencyProperty.Register(
            nameof(Drawing), typeof(ArtDrawing), typeof(ArtPicture),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        // fixed manufacturer colour (Intel Arc): the same in every theme
        private static readonly Brush BlueBrush = CreateBlue();
        private static Brush CreateBlue()
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
            b.Freeze();
            return b;
        }

        public ArtDrawing? Drawing
        {
            get => (ArtDrawing?)GetValue(DrawingProperty);
            set => SetValue(DrawingProperty, value);
        }

        public ArtPicture()
        {
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        protected override Size MeasureOverride(Size available)
        {
            var drawing = Drawing;
            if (drawing == null) return new Size(0, 0);
            // the drawing's natural size, scaled down uniformly if the space is smaller
            double scale = Math.Min(1, Math.Min(available.Width / drawing.Width, available.Height / drawing.Height));
            if (double.IsNaN(scale) || scale <= 0) scale = 1;
            return new Size(drawing.Width * scale, drawing.Height * scale);
        }

        private Brush BrushFor(ArtFill fill)
        {
            Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
            return fill switch
            {
                ArtFill.Substrate     => Res("DimmPcbBrush", Brushes.SeaGreen),
                ArtFill.SubstrateEdge => Res("DimmPcbEdgeBrush", Brushes.DarkGreen),
                ArtFill.Lid           => Res("CpuLidBrush", Brushes.LightGray),
                ArtFill.LidShade      => Res("CpuLidShadeBrush", Brushes.Silver),
                ArtFill.LidText       => Res("CpuLidTextBrush", Brushes.SlateGray),
                ArtFill.Die           => Res("CpuDieBrush", Brushes.DarkSlateGray),
                ArtFill.DieShine      => Res("CpuDieShineBrush", Brushes.SlateGray),
                ArtFill.Gold          => Res("DimmPinBrush", Brushes.Goldenrod),
                ArtFill.Accent        => Res("AccentBrush", Brushes.SteelBlue),
                ArtFill.White         => Brushes.White,
                ArtFill.Ok            => Res("StatusOkBrush", Brushes.SeaGreen),
                ArtFill.Warn          => Res("StatusWarnBrush", Brushes.Goldenrod),
                ArtFill.Crit          => Res("StatusCritBrush", Brushes.IndianRed),
                ArtFill.Muted         => Res("StatusNaBrush", Brushes.Gray),
                ArtFill.Blue          => BlueBrush,
                _                     => Res("BgCardBrush", Brushes.White),
            };
        }

        protected override void OnRender(DrawingContext dc)
        {
            var art = Drawing;
            double W = ActualWidth, H = ActualHeight;
            if (art == null || W < 4 || H < 4) return;

            double scale = Math.Min(W / art.Width, H / art.Height);
            dc.PushTransform(new TranslateTransform((W - art.Width * scale) / 2, (H - art.Height * scale) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));

            var typeface = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            foreach (var shape in art.Shapes)
            {
                var brush = BrushFor(shape.Fill);
                bool faded = shape.Opacity < 0.999;
                if (faded) dc.PushOpacity(shape.Opacity);
                switch (shape.Kind)
                {
                    case ArtKind.Rect:
                        dc.DrawRoundedRectangle(brush, null, new Rect(shape.X, shape.Y, shape.W, shape.H), shape.Radius, shape.Radius);
                        break;

                    case ArtKind.Polygon when shape.Points is { Count: >= 3 } points:
                        var geometry = new StreamGeometry();
                        using (var ctx = geometry.Open())
                        {
                            ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true, isClosed: true);
                            foreach (var p in points.Skip(1)) ctx.LineTo(new Point(p.X, p.Y), isStroked: false, isSmoothJoin: false);
                        }
                        geometry.Freeze();
                        dc.DrawGeometry(brush, null, geometry);
                        break;

                    case ArtKind.Text when shape.Text.Length > 0:
                        // the text fills the box height; if it is too wide, it shrinks to fit
                        double size = Math.Max(1, shape.H * 0.95);
                        var text = new FormattedText(shape.Text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, size, brush, dpi);
                        if (text.Width > shape.W && text.Width > 0)
                        {
                            size = Math.Max(1, size * shape.W / text.Width);
                            text = new FormattedText(shape.Text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, size, brush, dpi);
                        }
                        dc.DrawText(text, new Point(shape.X + (shape.W - text.Width) / 2, shape.Y + (shape.H - text.Height) / 2));
                        break;
                }
                if (faded) dc.Pop();
            }

            dc.Pop();
            dc.Pop();
        }
    }
}
