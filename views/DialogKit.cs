using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Button      = System.Windows.Controls.Button;
using Image       = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using WpfColor    = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// Common parts for the modal dialogs (Auto-Clean, Optimize): a shell with a shadow, a header with icon + title +
    /// subtitle + X, section labels, card rows with hover, status chips. All colours come from the theme resources
    /// (SetResourceReference), so the dialog follows Dark / Light without per-theme code.
    /// </summary>
    internal static class DialogKit
    {
        internal static readonly WpfColor Green  = WpfColor.FromRgb(34, 197, 94);
        internal static readonly WpfColor Amber  = WpfColor.FromRgb(245, 158, 11);
        internal static readonly WpfColor Red    = WpfColor.FromRgb(239, 68, 68);
        internal static readonly WpfColor Slate  = WpfColor.FromRgb(100, 116, 139);

        /// <summary>Sets the window up as a chromeless modal dialog (transparent, centred on the owner).</summary>
        internal static void SetupDialogWindow(Window w, string title, double width)
        {
            w.Title                 = title;
            w.Width                 = width;
            w.SizeToContent         = SizeToContent.Height;
            w.WindowStyle           = WindowStyle.None;
            w.ResizeMode            = ResizeMode.NoResize;
            w.AllowsTransparency    = true;
            w.Background            = Brushes.Transparent;
            w.ShowInTaskbar         = false;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.UseLayoutRounding     = true;
            w.SnapsToDevicePixels   = true;
            if (System.Windows.Application.Current?.Resources.Contains("AppFont") == true)
                w.FontFamily = (System.Windows.Media.FontFamily)System.Windows.Application.Current.Resources["AppFont"];
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            w.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
            try { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(w); Ui.UseAppScrollBars(w); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* stil WPF-UI optional */ }
        }

        /// <summary>Shell: a rounded card with a shadow, the page background (BgDark) and a subtle outline.</summary>
        internal static FrameworkElement Shell(UIElement content)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(22, 18, 22, 18),
                Child           = content,
            };
            card.SetResourceReference(Border.BackgroundProperty,   "BgDarkBrush");
            card.SetResourceReference(Border.BorderBrushProperty,  "BorderBrush2");
            card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
            // the shadow on its own layer behind the card (an effect on the card would blur the dialog text)
            var layers = Ui.ShadowLayer(card, new CornerRadius(8), new Thickness(14),
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.30,
                    Color = Colors.Black, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                }, "BgDarkBrush");
            ((Border)layers.Children[0]).SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
            return layers;
        }

        /// <summary>An icon from the app's icon system (Icon.* in IconsDark/IconsLight).</summary>
        internal static Image Icon(string key, double size = 18, double opacity = 0.85)
        {
            var img = new Image { Width = size, Height = size, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };
            img.SetResourceReference(Image.SourceProperty, key);
            return img;
        }

        /// <summary>Header: icon tile + title + subtitle, X on the right. Dragging the header moves the window.</summary>
        internal static Grid Header(Window w, string iconKey, string title, string subtitle, out TextBlock subtitleBlock)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top, Child = Icon(iconKey, 20, 1.0),
            };
            tile.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            tile.BorderThickness = new Thickness(1);
            g.Children.Add(tile);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = title, FontSize = Ui.FontXL, FontWeight = FontWeights.Bold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            texts.Children.Add(t);
            subtitleBlock = new TextBlock { Text = subtitle, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            subtitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            texts.Children.Add(subtitleBlock);
            Grid.SetColumn(texts, 1); g.Children.Add(texts);

            var close = Net.NetWindowHelper.CloseIconButton(32);
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(12, 0, 0, 0);
            close.Click += (_, _) => w.Close();
            Grid.SetColumn(close, 2); g.Children.Add(close);

            g.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
                try { w.DragMove(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            };
            return g;
        }

        private static bool IsInsideButton(DependencyObject d)
        {
            for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
                if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
            return false;
        }

        /// <summary>Section label (small capitals, secondary text) with optional text on the right.</summary>
        internal static Grid SectionLabel(string text, string? right = null, double topMargin = 4)
        {
            var g = new Grid { Margin = new Thickness(2, topMargin, 2, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock { Text = SMDWin.Services.DisplayFormat.SentenceCase(text), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Opacity = 0.8 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            g.Children.Add(l);
            if (!string.IsNullOrEmpty(right))
            {
                var r = new TextBlock { Text = right, FontSize = Ui.FontXS, Opacity = 0.75 };
                r.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetColumn(r, 1); g.Children.Add(r);
            }
            return g;
        }

        /// <summary>Card row: card background, subtle outline, hover on BgHover. A click anywhere = onClick.</summary>
        internal static Border RowCard(UIElement child, Action? onClick = null)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 6), Child = child,
                Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : null,
            };
            b.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            if (onClick != null)
                b.MouseLeftButtonUp += (_, e) =>
                {
                    // A click directly on the checkbox already toggles it — it is not toggled twice
                    if (e.OriginalSource is DependencyObject d && IsInsideToggle(d)) return;
                    onClick();
                };
            return b;
        }

        private static bool IsInsideToggle(DependencyObject d)
        {
            for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
                if (cur is System.Windows.Controls.Primitives.ToggleButton) return true;
            return false;
        }

        /// <summary>Cip mic colorat (stare / risc).</summary>
        internal static Border Chip(string text, WpfColor color, string? tooltip = null)
        {
            var tb = new TextBlock { Text = text, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ReadableColor.ForText(color)) };
            return new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
                Background = new SolidColorBrush(WpfColor.FromArgb(34, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(70, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center, Child = tb, ToolTip = tooltip,
            };
        }

        internal static void SetChip(Border chip, string text, WpfColor color)
        {
            if (chip.Child is TextBlock tb) { tb.Text = text; tb.Foreground = new SolidColorBrush(color); }
            chip.Background  = new SolidColorBrush(WpfColor.FromArgb(34, color.R, color.G, color.B));
            chip.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(70, color.R, color.G, color.B));
        }

        /// <summary>Primary / secondary text bound to the theme resources.</summary>
        internal static TextBlock Text(string text, double size, bool secondary = false, bool semibold = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (semibold) tb.FontWeight = FontWeights.SemiBold;
            tb.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
            return tb;
        }

        /// <summary>Footer bar: a card with content on the left and buttons on the right.</summary>
        internal static Border Footer(UIElement left, params UIElement[] buttons)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(left);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var b in buttons) sp.Children.Add(b);
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            var f = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 12, 10), Margin = new Thickness(0, 8, 0, 0), Child = g,
            };
            f.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            f.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return f;
        }
    }
}
