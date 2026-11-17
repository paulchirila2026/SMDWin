using System;
using System.Globalization;

namespace SMDWin.Services
{
    /// <summary>
    /// Fallback icon for a program without its own icon: a coloured square with the first letter of the name.
    /// The colour is chosen from the name, so the same program gets the same colour on every start and on any PC
    /// (string.GetHashCode is not used because it differs between runs).
    /// </summary>
    public static class AppTile
    {
        /// <summary>Mid-tone colours on which white text reads well in both light and dark themes.</summary>
        public static readonly string[] Palette =
        {
            "#2563EB", "#7C3AED", "#DB2777", "#DC2626", "#EA580C",
            "#B45309", "#15803D", "#0F766E", "#0E7490", "#4B5563",
        };

        /// <summary>First letter or digit of the name, upper case; "?" when the name has none.</summary>
        public static string Initial(string? name)
        {
            if (!string.IsNullOrEmpty(name))
                foreach (var rune in name.EnumerateRunes())
                    if (System.Text.Rune.IsLetterOrDigit(rune))
                        return rune.ToString().ToUpper(CultureInfo.InvariantCulture);
            return "?";
        }

        /// <summary>Index of the colour in <see cref="Palette"/> — the same for the same name, regardless of case and surrounding spaces.</summary>
        public static int ColorIndex(string? name)
        {
            string s = (name ?? "").Trim().ToLowerInvariant();
            uint hash = 2166136261;                 // 32-bit FNV-1a
            foreach (char c in s) { hash ^= c; hash *= 16777619; }
            return (int)(hash % (uint)Palette.Length);
        }

        public static string ColorHex(string? name) => Palette[ColorIndex(name)];
    }
}
