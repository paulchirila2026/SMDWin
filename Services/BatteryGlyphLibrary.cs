using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfColor       = System.Windows.Media.Color;
using WpfPoint       = System.Windows.Point;
using WpfRectangle   = System.Windows.Shapes.Rectangle;
using WpfBrush        = System.Windows.Media.Brush;
using WpfBrushes       = System.Windows.Media.Brushes;

namespace SMDWin.Services
{
    public static class BatteryGlyphLibrary
    {
        public static readonly (string Key, string DisplayName)[] All =
        {
            ("Classic",   "Classic outline"),
            ("Pill",      "Rounded pill"),
            ("Minimal",   "Minimal bar"),
            ("Segmented", "Segmented cells"),
            ("Ring",      "Circular ring"),
        };

        public static UIElement Build(string key, int chargePercent, WpfColor chargeColor, bool isCharging) => key switch
        {
            "Pill"      => Pill(chargePercent, chargeColor, isCharging),
            "Minimal"   => Minimal(chargePercent, chargeColor, isCharging),
            "Segmented" => Segmented(chargePercent, chargeColor, isCharging),
            "Ring"      => Ring(chargePercent, chargeColor, isCharging),
            _           => Classic(chargePercent, chargeColor, isCharging), // "Classic" and any unknown key
        };

        private static WpfBrush FillBrush(WpfColor c) => new LinearGradientBrush(
            WpfColor.FromArgb(255, c.R, c.G, c.B), Darken(c, 0.28), new WpfPoint(0, 0), new WpfPoint(0, 1));

        private static WpfColor Darken(WpfColor c, double amt) => WpfColor.FromRgb(
            (byte)(c.R * (1 - amt)), (byte)(c.G * (1 - amt)), (byte)(c.B * (1 - amt)));

        private static void AddBolt(Canvas canvas, double cx, double cy, double size)
        {
            var vb = new Viewbox { Width = size, Height = size };
            Canvas.SetLeft(vb, cx - size / 2); Canvas.SetTop(vb, cy - size / 2);
            var inner = new Canvas { Width = 24, Height = 24 };
            inner.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M13,2 L3,14 H12 L11,22 L21,10 H12 L13,2Z"),
                Fill = new SolidColorBrush(WpfColor.FromRgb(0x10, 0x18, 0x28)),
                Stroke = WpfBrushes.White, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            });
            vb.Child = inner;
            canvas.Children.Add(vb);
        }

        // ── 1. Classic — outline + nub + umplere dreptunghiulara (design actual) ──
        private static UIElement Classic(int pct, WpfColor color, bool charging)
        {
            var canvas = new Canvas { Width = 130, Height = 60 };
            canvas.Children.Add(new Border { Width = 112, Height = 55, BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(7), Opacity = 0.85 });
            canvas.Children.Add(Positioned(new Border { Width = 5, Height = 19,
                Background = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(0,3,3,0), Opacity = 0.85 }, 112, 18));
            double fillW = Math.Max(2, 104 * Math.Min(100, pct) / 100.0);
            var fill = new Border { Width = fillW, Height = 47, CornerRadius = new CornerRadius(4), Background = FillBrush(color) };
            canvas.Children.Add(Positioned(fill, 4, 4));
            if (charging) AddBolt(canvas, 56, 27, 20);
            return canvas;
        }

        // ── 2. Pill — a fully rounded capsule, no square corners ────────────
        private static UIElement Pill(int pct, WpfColor color, bool charging)
        {
            var canvas = new Canvas { Width = 130, Height = 60 };
            canvas.Children.Add(new Border { Width = 112, Height = 48, BorderThickness = new Thickness(2.5),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(24), Opacity = 0.85 });
            canvas.Children.Add(Positioned(new Border { Width = 4, Height = 16,
                Background = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(2), Opacity = 0.85 }, 116, 22));
            double fillW = Math.Max(6, 100 * Math.Min(100, pct) / 100.0);
            var fill = new Border { Width = fillW, Height = 36, CornerRadius = new CornerRadius(18), Background = FillBrush(color) };
            canvas.Children.Add(Positioned(fill, 6, 6));
            if (charging) AddBolt(canvas, 56, 24, 18);
            return canvas;
        }

        // ── 3. Minimal — no outline, only a progress bar on a light background ─────
        private static UIElement Minimal(int pct, WpfColor color, bool charging)
        {
            var canvas = new Canvas { Width = 130, Height = 60 };
            canvas.Children.Add(Positioned(new Border { Width = 120, Height = 14,
                Background = new SolidColorBrush(WpfColor.FromArgb(40, 148, 163, 184)), CornerRadius = new CornerRadius(7) }, 0, 23));
            double fillW = Math.Max(4, 120 * Math.Min(100, pct) / 100.0);
            canvas.Children.Add(Positioned(new Border { Width = fillW, Height = 14, CornerRadius = new CornerRadius(7), Background = FillBrush(color) }, 0, 23));
            if (charging) AddBolt(canvas, 60, 30, 18);
            return canvas;
        }

        // ── 4. Segmented — 5 "celule" separate, umplute proportional (gen fuel gauge) ─
        private static UIElement Segmented(int pct, WpfColor color, bool charging)
        {
            var canvas = new Canvas { Width = 130, Height = 60 };
            canvas.Children.Add(new Border { Width = 112, Height = 50, BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(6), Opacity = 0.85 });
            canvas.Children.Add(Positioned(new Border { Width = 5, Height = 18,
                Background = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), CornerRadius = new CornerRadius(0,3,3,0), Opacity = 0.85 }, 112, 16));
            const int cells = 5;
            double cellW = (112 - 6 * 2 - (cells - 1) * 3) / (double)cells; // 2px inset + 3px gap between cells
            int filledCells = (int)Math.Round(cells * Math.Min(100, pct) / 100.0);
            for (int i = 0; i < cells; i++)
            {
                double left = 6 + i * (cellW + 3);
                var cell = new WpfRectangle
                {
                    Width = cellW, Height = 38, RadiusX = 2, RadiusY = 2,
                    Fill = i < filledCells ? FillBrush(color) : new SolidColorBrush(WpfColor.FromArgb(35, 148, 163, 184)),
                };
                canvas.Children.Add(Positioned(cell, left, 6));
            }
            if (charging) AddBolt(canvas, 56, 25, 18);
            return canvas;
        }

        // ── 5. Ring — a circular ring instead of a horizontal bar ───────────────────
        private static UIElement Ring(int pct, WpfColor color, bool charging)
        {
            var canvas = new Canvas { Width = 130, Height = 60 };
            const double r = 24, cx = 30, cy = 30, thick = 7;
            var track = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = thick,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(40, 148, 163, 184)) };
            canvas.Children.Add(Positioned(track, cx - r, cy - r));

            var arc = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = FillBrush(color) };
            double circumference = 2 * Math.PI * r;
            double dash = circumference / thick;
            arc.StrokeDashArray = new DoubleCollection { dash, 1000 };
            arc.StrokeDashOffset = (1 - Math.Min(100, pct) / 100.0) * dash;
            arc.RenderTransform = new RotateTransform(-90, r, r);
            canvas.Children.Add(Positioned(arc, cx - r, cy - r));

            var pctText = new TextBlock { Text = $"{pct}%", FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x1f,0x29,0x37)), TextAlignment = TextAlignment.Center, Width = r * 2 };
            canvas.Children.Add(Positioned(pctText, cx - r, cy - 9));

            if (charging) AddBolt(canvas, 80, 30, 22);
            return canvas;
        }

        private static UIElement Positioned(UIElement el, double left, double top)
        {
            Canvas.SetLeft(el, left); Canvas.SetTop(el, top);
            return el;
        }
    }
}
