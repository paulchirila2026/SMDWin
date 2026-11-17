using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using WpfBrush = System.Windows.Media.Brush;

namespace SMDWin.Views.Net
{
    /// <summary>
    /// The text of the coloured buttons (Primary, Danger, Success, Caution) in the network tools. The Network page has its own
    /// WPF-UI dictionaries, and they can bring back black text on an accent background; a value set directly on the button
    /// takes priority over any style, so the text stays white while the button is coloured and returns to the style when it
    /// is not.
    /// </summary>
    internal static class NetButtonLook
    {
        private static readonly SolidColorBrush OnColor = Freeze(System.Windows.Media.Colors.White);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wpf.Ui.Controls.Button, object> Wired = new();

        private static SolidColorBrush Freeze(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static bool Colored(Wpf.Ui.Controls.ControlAppearance a)
            => a is Wpf.Ui.Controls.ControlAppearance.Primary or Wpf.Ui.Controls.ControlAppearance.Danger
                or Wpf.Ui.Controls.ControlAppearance.Success or Wpf.Ui.Controls.ControlAppearance.Caution;

        /// <summary>Watches the button's look: white text while it is coloured. Can be called several times on the same button.</summary>
        public static void Apply(Wpf.Ui.Controls.Button b)
        {
            if (Wired.TryGetValue(b, out _)) return;
            Wired.Add(b, new object());
            void Sync()
            {
                // white text only on an enabled coloured button: a disabled one gets the theme's pale fill, and white on it
                // disappears (Light theme)
                if (Colored(b.Appearance) && b.IsEnabled) b.Foreground = OnColor;
                else b.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            }
            Sync();
            b.IsEnabledChanged += (_, _) => Sync();
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                Wpf.Ui.Controls.Button.AppearanceProperty, typeof(Wpf.Ui.Controls.Button));
            descriptor?.AddValueChanged(b, (_, _) => Sync());
        }

        /// <summary>Applies <see cref="Apply"/> to every WPF-UI button in the page's visual tree.</summary>
        public static void Sweep(DependencyObject root)
        {
            try
            {
                foreach (var b in NetWindowHelper.Descendants<Wpf.Ui.Controls.Button>(root)) Apply(b);
            }
            catch (System.Exception ex) { AppLogger.Debug($"NetButtonLook.Sweep: {ex.Message}"); }
        }
    }
}
