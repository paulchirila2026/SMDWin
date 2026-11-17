using System;

namespace SMDWin.Services
{
    public static class DateFormatHelper
    {
        public const string DateFormat     = "dd MMM yyyy";
        public const string DateTimeFormat = "dd MMM yyyy HH:mm";

        private static readonly System.Globalization.CultureInfo _inv =
            System.Globalization.CultureInfo.InvariantCulture;

        // Shown dates use the interface language for the month ("04 nov 2026" in Romanian, "04 Nov 2026" in English); the dot
        // some languages put after a short month ("nov.") is dropped. Files and logs that must stay comparable keep their own formats.
        private static string Ui(DateTime dt, string format) => dt.ToString(format, LanguageService.UiCulture).Replace(".", "");

        /// <summary>Reads back a date written by FormatDate / FormatDateTime (in any interface language).</summary>
        public static bool TryParseShown(string? text, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            var names = LanguageService.UiCulture.DateTimeFormat.AbbreviatedMonthNames;
            for (int m = 0; m < 12; m++)
            {
                string local = names[m].Replace(".", "");
                if (local.Length == 0) continue;
                int i = t.IndexOf(" " + local + " ", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) { t = t[..(i + 1)] + _inv.DateTimeFormat.AbbreviatedMonthNames[m] + t[(i + 1 + local.Length)..]; break; }
            }
            return DateTime.TryParseExact(t, new[] { "dd MMM yyyy", "d MMM yyyy", "dd MMM yyyy HH:mm", "dd MMM yyyy HH:mm:ss" }, _inv,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out value);
        }

        public static string FormatDate(DateTime dt)     => Ui(dt, DateFormat);
        public static string FormatDateTime(DateTime dt) => Ui(dt, DateTimeFormat);
        /// <summary>With seconds — where precision matters (Event Viewer, etc.).</summary>
        public static string FormatDateTimeSec(DateTime dt) => Ui(dt, "dd MMM yyyy HH:mm:ss");

        /// <summary>
        /// Parses the compact WMI format ("20240717000000.000000+180" or just "20240717...") used by
        /// Win32_OperatingSystem.InstallDate, the registry InstallDate (Programs and Features), etc.
        /// Returns "—" if it cannot be parsed.
        /// </summary>
        public static string FormatCompactDate(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 8) return "—";
            try
            {
                int yr = int.Parse(raw.Substring(0, 4));
                int mo = int.Parse(raw.Substring(4, 2));
                int dy = int.Parse(raw.Substring(6, 2));
                return FormatDate(new DateTime(yr, mo, dy));
            }
            catch { return "—"; }
        }
    }
}
