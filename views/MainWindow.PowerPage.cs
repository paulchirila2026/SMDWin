using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page: at the top the battery card (laptop) or the diagram (PC) | the active plan, at the bottom
    /// Sleep &amp; wake (+ UPS) | the consumers. The bottom row takes the rest of the window, so the page does not stay with
    /// empty space; when the content is larger, the page scrolls.
    /// </summary>
    public partial class MainWindow
    {
        private bool _powerFitHooked;

        private void StartPowerCardsFit()
        {
            try
            {
                if (!_powerFitHooked && FindName("PanelBattery") is ScrollViewer sv && sv.Content is System.Windows.Controls.Panel host)
                {
                    _powerFitHooked = true;
                    // on maximize / restore the visible area changes height after this event → once more after layout
                    sv.SizeChanged += (_, _) =>
                    {
                        FitPowerCards();
                        Dispatcher.BeginInvoke(new Action(FitPowerCards), System.Windows.Threading.DispatcherPriority.Loaded);
                    };
                    // the diagram / battery card / app consumption appear and disappear after the data is read.
                    // The Visibility property is watched, not IsVisibleChanged: a card in a hidden column does not become "visible" when it
                    // appears, so its column would never be shown.
                    var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                        UIElement.VisibilityProperty, typeof(UIElement));
                    foreach (var name in new[] { "BatteryNoBatteryPanel", "BatteryHeroCard", "BatDrainCard", "PowerSleepCard", "UpsCard" })
                        if (FindName(name) is FrameworkElement fe)
                        {
                            fe.SizeChanged += (_, _) => FitPowerCards();
                            fe.IsVisibleChanged += (_, _) => FitPowerCards();
                            visibility?.AddValueChanged(fe, (_, _) => FitPowerCards());
                        }
                }
                Dispatcher.BeginInvoke(new Action(FitPowerCards), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex) { AppLogger.Debug($"StartPowerCardsFit: {ex.Message}"); }
        }

        private bool _fittingPowerCards;

        private void FitPowerCards()
        {
            if (_fittingPowerCards) return;
            _fittingPowerCards = true;
            try
            {
                if (FindName("PanelBattery") is not ScrollViewer sv || FindName("PowerPageGrid") is not Grid page) return;

                // the source of the advanced settings follows the switch in the plan card (MainWindow.PowerRedesign.cs)
                if (FindName("PowerProfileRow") is FrameworkElement pr) pr.Visibility = Visibility.Collapsed;

                // The bottom row: Sleep & wake (+ UPS) | consumers. A column with no visible card disappears and the other takes the
                // full width (e.g. on a PC the battery consumers are not shown).
                static bool AnyVisible(object? o) => o is System.Windows.Controls.Panel p
                    && p.Children.OfType<FrameworkElement>().Any(c => c.Visibility == Visibility.Visible);
                bool leftOn = AnyVisible(FindName("PowerColLeftStack"));
                bool rightOn = AnyVisible(FindName("PowerColRightStack"));
                if (FindName("PowerColLeftStack") is FrameworkElement left && FindName("PowerColRightStack") is FrameworkElement right)
                {
                    left.Visibility = leftOn ? Visibility.Visible : Visibility.Collapsed;
                    right.Visibility = rightOn ? Visibility.Visible : Visibility.Collapsed;
                    Grid.SetColumn(left, 0); Grid.SetColumnSpan(left, rightOn ? 1 : 3);
                    Grid.SetColumn(right, leftOn ? 2 : 0); Grid.SetColumnSpan(right, leftOn ? 1 : 3);
                }

                // The page fills the visible area: the bottom row takes the rest of the height (scroll when the content is larger)
                double avail = sv.ActualHeight - page.Margin.Top - page.Margin.Bottom - 2;
                if (avail > 0 && Math.Abs(page.MinHeight - avail) > 1) page.MinHeight = avail;
                // (11.11.2026: the cards row is sized by its content now — the page fits one screen, no stretched empty cards)

                // Diagram (PC): height by the column width (660 × 300), at most 430 px, centred; its text stays at the size used in
                // the rest of the app
                if (FindName("PowerFlowBox") is FrameworkElement flow && flow.IsVisible)
                {
                    double availW = (flow.Parent as FrameworkElement)?.ActualWidth ?? 0;
                    if (availW > 0)
                    {
                        double h = Math.Clamp(availW * 300.0 / 660.0, 200, 430);
                        if (double.IsNaN(flow.Height) || Math.Abs(h - flow.Height) > 2) flow.Height = h;
                        if (FindName("PowerFlowWidget") is PowerFlowAnimation pfa) pfa.SetTextScale(Math.Min(h / 300.0, availW / 660.0));
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FitPowerCards: {ex.Message}"); }
            finally { _fittingPowerCards = false; }
        }
    }
}
