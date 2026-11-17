using System.Windows;

namespace SMDWin.Views
{
    /// <summary>
    /// WPF-UI buttons without the "raised" outline (the darker bottom line, ControlElevationBorderBrush): neutral background,
    /// no outline. The outline comes straight from the WPF-UI style (not from a button-only resource), so it is removed on
    /// each button at load. A button that chooses its own outline (e.g. the selected tab) is not touched.
    /// </summary>
    internal static class ButtonLook
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Wpf.Ui.Controls.Button), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => { if (s is Wpf.Ui.Controls.Button b) Apply(b); }));
        }

        public static void Apply(Wpf.Ui.Controls.Button b)
        {
            // coloured buttons (primary, green, red…) stay without an outline; ordinary ones get the fine "raised white" outline
            bool neutral = b.Appearance == Wpf.Ui.Controls.ControlAppearance.Secondary;
            void Edge(DependencyProperty dp)
            {
                if (b.ReadLocalValue(dp) != DependencyProperty.UnsetValue) return;
                if (neutral) b.SetResourceReference(dp, "BtnNeutralBorderBrush");
                else b.SetValue(dp, System.Windows.Media.Brushes.Transparent);
            }
            Edge(System.Windows.Controls.Control.BorderBrushProperty);
            Edge(Wpf.Ui.Controls.Button.MouseOverBorderBrushProperty);
            Edge(Wpf.Ui.Controls.Button.PressedBorderBrushProperty);

            // the selected filter declared in XAML (SelBtnBg background) outside a grey track: the same look as after a click
            if (System.Windows.Application.Current?.TryFindResource("SelBtnBg") is System.Windows.Media.Brush sel
                && ReferenceEquals(b.Background, sel) && !MainWindow.IsOnSegmentTrack(b))
                MainWindow.SetTabButtonActive(b, true);
        }
    }
}
