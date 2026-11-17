using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SMDWin.Services;
using Point = System.Windows.Point;

namespace SMDWin.Views
{
    /// <summary>
    /// When a page opens, its cards appear one after another: each fades in and rises a few pixels, 35 ms after the previous
    /// one (top to bottom, left to right). Only the cards drawn with the shared card styles move; with animations turned off
    /// in Settings nothing moves. The animations are removed when they end, so the code can still set the cards' opacity.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string[] CardStyleKeys =
            { "CompCardStyle", "CardStyle", "BaseCardStyle", "SetCard", "DashCardStyle", "DashClickCardStyle" };

        private HashSet<Style>? _cardStyles;

        private void AnimateCardsIn(FrameworkElement page)
        {
            if (SettingsService.Current.EnableAnimations == false) return;
            // after layout, so the positions (for the order) are known
            _ = Dispatcher.InvokeAsync(() =>
            {
                try { RunCardsIn(page); }
                catch (Exception ex) { AppLogger.Debug($"AnimateCardsIn: {ex.Message}"); }
            }, DispatcherPriority.Loaded);
        }

        private void RunCardsIn(FrameworkElement page)
        {
            if (!page.IsVisible) return;
            _cardStyles ??= new HashSet<Style>(CardStyleKeys.Select(k => TryFindResource(k)).OfType<Style>());
            if (_cardStyles.Count == 0) return;

            var cards = new List<Border>();
            void Walk(DependencyObject d)
            {
                if (cards.Count >= 16) return;
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is Border b && b.Style != null && _cardStyles.Contains(b.Style))
                    {
                        if (b.IsVisible && b.ActualHeight > 0) cards.Add(b);
                        continue;   // a card inside a card does not move separately
                    }
                    if (c is UIElement u && !u.IsVisible) continue;
                    Walk(c);
                }
            }
            Walk(page);

            Point Pos(Border b)
            {
                try { return b.TransformToAncestor(page).Transform(new Point(0, 0)); }
                catch (InvalidOperationException) { return new Point(0, 0); }
            }
            var ordered = cards.Select(b => (b, p: Pos(b)))
                               .OrderBy(t => Math.Round(t.p.Y / 40)).ThenBy(t => t.p.X)
                               .Select(t => t.b).ToList();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            for (int i = 0; i < ordered.Count; i++)
            {
                var card = ordered[i];
                var begin = TimeSpan.FromMilliseconds(i * 35);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease };
                fade.Completed += (_, _) => card.BeginAnimation(UIElement.OpacityProperty, null);
                card.BeginAnimation(UIElement.OpacityProperty, fade);

                // the rise only on cards without a transform of their own
                if (card.RenderTransform == null || card.RenderTransform == Transform.Identity)
                {
                    var tt = new TranslateTransform(0, 10);
                    card.RenderTransform = tt;
                    var rise = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = begin, EasingFunction = ease };
                    rise.Completed += (_, _) => card.RenderTransform = Transform.Identity;
                    tt.BeginAnimation(TranslateTransform.YProperty, rise);
                }
            }
        }
    }
}
