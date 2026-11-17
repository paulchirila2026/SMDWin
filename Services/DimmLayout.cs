using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Layout of the gold contacts of a drawn memory module. All contacts have the same width and pitch, in whole
    /// pixels — dividing the width by a fixed number of contacts gave fractional widths rounded differently from one
    /// contact to the next (uneven "teeth"). Here the number of contacts adapts to the width.
    /// </summary>
    public static class DimmLayout
    {
        /// <summary>Where the notch between contacts sits, as a fraction of the module width (it differs between
        /// generations, so modules cannot be fitted the wrong way).</summary>
        public static double NotchPosition(string? memoryType, bool soDimm)
        {
            string t = (memoryType ?? "").Trim().ToUpperInvariant();
            if (soDimm) return t switch { "DDR5" => 0.50, "DDR4" => 0.56, "DDR3" => 0.36, "DDR2" => 0.20, _ => 0.50 };
            return t switch { "DDR5" => 0.46, "DDR4" => 0.49, "DDR2" => 0.55, _ => 0.40 };
        }

        public static bool IsSoDimm(string? formFactor)
        {
            string f = (formFactor ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
            return f.Contains("SODIMM");
        }

        /// <summary>LPDDR memory is soldered on the board: it is drawn as chips, not as a module in a socket.</summary>
        public static bool IsSoldered(string? memoryType) => (memoryType ?? "").Trim().StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The laptop form: what the BIOS reports, or — when it reports nothing ("—", common on laptops) — a laptop
        /// (a battery in the system) or soldered memory. A long desktop DIMM is drawn only when nothing says otherwise.
        /// </summary>
        public static bool IsCompact(string? formFactor, string? memoryType)
        {
            if (IsSoDimm(formFactor) || IsSoldered(memoryType)) return true;
            string f = (formFactor ?? "").Trim();
            if (f.Length > 0 && f != "—" && !f.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return false;   // DIMM reported
            try
            {
                return !System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus
                    .HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Width of a laptop module picture for a given height: a real SO-DIMM is about 70 × 30 mm, so roughly 2.3 times its
        /// height (the earlier, longer drawing looked like a desktop module).
        /// </summary>
        public static double CompactWidth(double available, double height) => Math.Min(available, Math.Max(120, height * 2.3));

        /// <summary>The left edge of the notch, in pixels.</summary>
        public static int NotchLeft(int width, int margin, int notchWidth, double position)
            => margin + (int)Math.Round(Math.Max(0, width - 2 * margin - notchWidth) * Math.Clamp(position, 0, 1));

        /// <summary>
        /// Left edges of the contacts, in pixels. Contacts start next to the notch and go towards the ends with a constant
        /// pitch; what is left at the ends (less than one pitch) is board margin.
        /// </summary>
        public static List<int> PinStarts(int width, int margin, int notchLeft, int notchWidth, int pitch, int pinWidth)
        {
            var starts = new List<int>();
            if (pitch <= 0 || pinWidth <= 0 || pinWidth > pitch || width <= 2 * margin) return starts;
            int gap = pitch - pinWidth;

            // left of the notch: the last contact ends one notch gap away
            for (int x = notchLeft - gap - pinWidth; x >= margin; x -= pitch) starts.Add(x);
            starts.Reverse();
            // right of the notch
            for (int x = notchLeft + notchWidth + gap; x + pinWidth <= width - margin; x += pitch) starts.Add(x);
            return starts;
        }
    }
}
