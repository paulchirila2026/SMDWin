using System;

namespace SMDWin.Services
{
    /// <summary>
    /// Temperatures shown in the unit chosen in Settings (°C by default, or °F).
    /// Sensors, thresholds and history stay in °C; only the displayed text is converted.
    /// </summary>
    public static class TempUnit
    {
        public static bool Fahrenheit =>
            string.Equals(SettingsService.Current?.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase);

        public static string Symbol => Fahrenheit ? "°F" : "°C";

        public static double Convert(double celsius) => Fahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;

        public static float? ConvertN(float? celsius) => celsius.HasValue ? (float)Convert((double)celsius.Value) : null;

        /// <summary>"72°C" / "162°F"; with <paramref name="space"/> "72 °C". A missing value → "—".</summary>
        public static string Fmt(double celsius, int decimals = 0, bool space = false)
        {
            if (!double.IsFinite(celsius)) return "—";
            return Convert(celsius).ToString(decimals > 0 ? "F1" : "F0") + (space ? " " : "") + Symbol;
        }

        public static string Fmt(double? celsius, int decimals = 0, bool space = false) =>
            celsius.HasValue ? Fmt(celsius.Value, decimals, space) : "—";

        /// <summary>Only the number and "°", for narrow places (charts, tray, widget).</summary>
        public static string Deg(double celsius) =>
            double.IsFinite(celsius) ? Convert(celsius).ToString("F0") + "°" : "—";

        public static string Deg(double? celsius) => celsius.HasValue ? Deg(celsius.Value) : "—";
    }
}
