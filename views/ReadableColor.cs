using SMDWin.Services;
using Color = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// Fixed status colours (bright green, amber, yellow) used as TEXT read well on Dark but fade on the white Light cards
    /// (green on pale green came out at 1.6:1). On Light, a colour that is too bright for text is darkened until it reads;
    /// on Dark it is used as it is.
    /// </summary>
    internal static class ReadableColor
    {
        public static bool LightTheme()
        {
            string t = SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName;
            return ThemeManager.IsLight(ThemeManager.Normalize(t));
        }

        private static double Luminance(Color c)
        {
            static double Lin(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>The colour to use for text on the current theme's cards.</summary>
        public static Color ForText(Color c)
        {
            if (!LightTheme()) return c;
            // darken in steps until the contrast with a white card is at least about 4:1 (luminance ≤ 0.21)
            for (int i = 0; i < 8 && Luminance(c) > 0.21; i++)
                c = Color.FromArgb(c.A, (byte)(c.R * 0.85), (byte)(c.G * 0.85), (byte)(c.B * 0.85));
            return c;
        }
    }
}
