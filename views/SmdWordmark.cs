using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SMDWin.Views
{
    /// <summary>
    /// The program's name written the same way everywhere it appears as a logo (sidebar, splash, About, Features):
    /// "SMD" in a medium weight and a calmer blue-grey, "Win" in semi-bold in the text colour of the place (no line under it).
    /// The gap between the two words is a thin space.
    /// Size and the colour of "Win" come from the place where it is used; the colour of "SMD" comes from <see cref="MutedBrushKey"/>.
    /// </summary>
    public class SmdWordmark : TextBlock
    {
        private readonly Run _smd = new("SMD") { FontWeight = FontWeights.Medium };
        private readonly Run _win = new("Win") { FontWeight = FontWeights.SemiBold };
        private string _mutedKey = "NavCatFgBrush";
        private bool _mutedKeySet;

        /// <summary>The resource key of the brush used for "SMD". The default is the menu group title colour of the theme.</summary>
        public string MutedBrushKey
        {
            get => _mutedKey;
            set { _mutedKey = value; _mutedKeySet = true; _smd.SetResourceReference(TextElement.ForegroundProperty, value); }
        }

        public SmdWordmark()
        {
            SetResourceReference(FontFamilyProperty, "AppFontDisplay");
            Inlines.Add(_smd);
            Inlines.Add(new Run(" "));   // a thin space: closer than a normal one
            Inlines.Add(_win);
        }

        /// <summary>
        /// The default colour key is applied only after the XAML properties, and only if the place did not set its own.
        /// The splash runs on its own thread: if it looked up the app's "NavCatFgBrush" there, WPF would create that brush on the
        /// splash thread, and the main window (another thread) could no longer use it — it crashed at start-up.
        /// </summary>
        protected override void OnInitialized(System.EventArgs e)
        {
            base.OnInitialized(e);
            if (!_mutedKeySet) _smd.SetResourceReference(TextElement.ForegroundProperty, _mutedKey);
        }
    }
}
