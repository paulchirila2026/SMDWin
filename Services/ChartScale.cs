using System;
using System.Globalization;

namespace SMDWin.Services
{
    /// <summary>
    /// Chart scale: the upper limit rounded to a "nice" value (1 / 2 / 5 × 10ⁿ in the displayed unit) and its text,
    /// so the chart label says clearly what the top means (e.g. "10 MB/s", not "11.3 MB/s").
    /// </summary>
    public static class ChartScale
    {
        /// <summary>Headroom above the peak: the curve never touches the top of the chart (peak + 10%).</summary>
        public const double Headroom = 1.1;

        /// <summary>The smallest 1 / 2 / 5 × 10ⁿ value that is ≥ <paramref name="value"/>.</summary>
        public static double NiceCeil(double value)
        {
            if (!double.IsFinite(value) || value <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
                if (m * mag >= value * 0.999999) return m * mag;
            return 10 * mag;
        }

        /// <summary>Upper limit for a value with 10% headroom above the peak, rounded up to the next 1 / 2 / 5 × 10ⁿ step.</summary>
        public static double NiceCeilWithHeadroom(double peak) => NiceCeil(double.IsFinite(peak) ? peak * Headroom : 0);

        /// <summary>Upper limit for a speed in KB/s: rounded in the unit it is shown in (KB/s, MB/s, GB/s),
        /// not lower than <paramref name="floorKBs"/>.</summary>
        public static double NiceRateMaxKBs(double peakKBs, double floorKBs = 64)
        {
            double v = Math.Max(double.IsFinite(peakKBs) ? peakKBs * Headroom : 0, floorKBs);
            if (v < 1000) { double n = NiceCeil(v); return n >= 1000 ? 1024.0 : n; }   // 1000 KB/s is shown as 1 MB/s
            if (v < 1000 * 1024.0) { double n = NiceCeil(v / 1024.0); return n >= 1000 ? 1024.0 * 1024.0 : n * 1024.0; }
            return NiceCeil(v / (1024.0 * 1024.0)) * 1024.0 * 1024.0;
        }

        /// <summary>„500 KB/s”, „2 MB/s”, „1.5 MB/s”, „1.2 GB/s”.</summary>
        public static string FormatRateKBs(double kbs)
        {
            if (!double.IsFinite(kbs) || kbs < 0) return "—";
            if (kbs < 1000) return Num(kbs) + " KB/s";
            if (kbs < 1000 * 1024.0) return Num(kbs / 1024.0) + " MB/s";
            return Num(kbs / (1024.0 * 1024.0)) + " GB/s";
        }

        /// <summary>Upper limit for an amount of memory in MB (rounded in MB or GB).</summary>
        public static double NiceSizeMaxMB(double peakMB, double floorMB = 10)
        {
            double v = Math.Max(double.IsFinite(peakMB) ? peakMB * Headroom : 0, floorMB);
            if (v < 1000) { double n = NiceCeil(v); return n >= 1000 ? 1024.0 : n; }   // 1000 MB is shown as 1 GB
            return NiceCeil(v / 1024.0) * 1024.0;
        }

        /// <summary>„800 MB”, „2 GB”, „1.5 GB”.</summary>
        public static string FormatSizeMB(double mb)
        {
            if (!double.IsFinite(mb) || mb < 0) return "—";
            return mb < 1000 ? Num(mb) + " MB" : Num(mb / 1024.0) + " GB";
        }

        /// <summary>Short number: no decimals when whole (or ≥ 100), otherwise one decimal.</summary>
        private static string Num(double v)
        {
            double r = Math.Round(v, 1);
            return r >= 100 || Math.Abs(r - Math.Round(r)) < 0.05
                ? Math.Round(r).ToString("0", CultureInfo.InvariantCulture)
                : r.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>"60 s", "5 min", "1 h" for a time window in seconds.</summary>
        public static string FormatWindow(int seconds)
        {
            if (seconds < 120) return seconds + " s";
            if (seconds < 3600) return (seconds / 60) + " min";
            return seconds % 3600 == 0 ? (seconds / 3600) + " h" : (seconds / 60) + " min";
        }
    }
}
