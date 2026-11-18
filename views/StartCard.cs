using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Color               = System.Windows.Media.Color;
using Colors              = System.Windows.Media.Colors;
using Brushes             = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using Cursors             = System.Windows.Input.Cursors;
using Path                = System.Windows.Shapes.Path;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// The big "start" card shown in a test's empty area while there are no results yet:
    /// an icon tile, a title, one plain sentence, a few facts (duration / needs / safe) and a large Start button.
    /// Once the test starts, the page replaces the card with its live view (progress and Stop stay in the same area).
    /// </summary>
    internal static class StartCard
    {
        /// <param name="iconData">Path geometry on a 24×24 canvas.</param>
        /// <param name="facts">Short facts, each shown with a small dot in front (e.g. "about 1 minute").</param>
        /// <param name="extra">Optional line under the facts (e.g. the selected drive).</param>
        /// <param name="compact">Smaller variant for narrow areas (overlay on a chart).</param>
        /// <param name="framed">False when the host is already a card (no second border).</param>
        public static Border Build(string iconData, string title, string description, string[] facts,
                                   string buttonText, Action onStart, UIElement? extra = null, bool compact = false, bool framed = true)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = compact ? new Thickness(18, 14, 18, 14) : new Thickness(26, 22, 26, 22),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 980,
                Cursor = Cursors.Arrow,
            };
            if (framed)
            {
                // solid background: the card may sit on top of an empty chart or block map
                card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            }
            else { card.BorderThickness = new Thickness(0); card.Background = Brushes.Transparent; }

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ① icon tile (accent at low opacity)
            double tile = compact ? 52 : 72;
            var tileB = new Border
            {
                Width = tile, Height = tile, CornerRadius = new CornerRadius(compact ? 12 : 16),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, compact ? 16 : 22, 0),
                Background = Tint("AccentBrush", 30),
            };
            var ico = new Path { Data = Geometry.Parse(iconData), Stretch = Stretch.Uniform, Width = tile * 0.42, Height = tile * 0.42 };
            ico.SetResourceReference(Shape.FillProperty, "AccentBrush");
            tileB.Child = ico;
            g.Children.Add(tileB);

            // ② text
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = title, FontSize = compact ? 16 : 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            text.Children.Add(t);
            var d = new TextBlock { Text = description, FontSize = compact ? 12.5 : 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(d);
            if (facts.Length > 0)
            {
                var fw = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var f in facts)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 2) };
                    var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                    dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
                    sp.Children.Add(dot);
                    var ft = new TextBlock { Text = f, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                    ft.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    sp.Children.Add(ft);
                    fw.Children.Add(sp);
                }
                text.Children.Add(fw);
            }
            if (extra != null)
            {
                if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
                text.Children.Add(extra);
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            // ③ the big Start button
            var btn = new Wpf.Ui.Controls.Button
            {
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Padding = compact ? new Thickness(18, 9, 20, 9) : new Thickness(24, 12, 26, 12),
                FontSize = compact ? 14 : 15.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(compact ? 14 : 22, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            var bc = new StackPanel { Orientation = Orientation.Horizontal };
            var play = new Path { Data = Geometry.Parse("M0,0 L10,6 L0,12 Z"), Width = compact ? 9 : 11, Height = compact ? 11 : 13, Stretch = Stretch.Fill, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            play.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = btn });
            bc.Children.Add(play);
            bc.Children.Add(new TextBlock { Text = buttonText, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = bc;
            btn.Click += (_, _) => onStart();
            Grid.SetColumn(btn, 2);
            g.Children.Add(btn);

            // narrow areas: the button moves under the text
            g.SizeChanged += (_, e) =>
            {
                bool narrow = e.NewSize.Width < (compact ? 430 : 560);
                Grid.SetColumn(btn, narrow ? 1 : 2);
                Grid.SetRow(btn, narrow ? 1 : 0);
                btn.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                btn.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(compact ? 14 : 22, 0, 0, 0);
                if (narrow && g.RowDefinitions.Count == 0)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
            };

            card.Child = g;
            return card;
        }

        /// <summary>A small "Label  value" line for the card (e.g. "Drive  C: · Samsung 970").</summary>
        public static TextBlock InfoLine(string label, string value)
        {
            var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var l = new System.Windows.Documents.Run(label + "  ");
            l.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
            var v = new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold };
            v.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
            tb.Inlines.Add(l); tb.Inlines.Add(v);
            return tb;
        }

        private static Brush Tint(string key, byte alpha)
        {
            var c = (Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.SteelBlue;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze(); return b;
        }
    }
}
