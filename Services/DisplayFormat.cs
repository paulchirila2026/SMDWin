using System;
using System.Globalization;

namespace SMDWin.Services
{
    /// <summary>Common status thresholds (the same on the Dashboard, System Info and Health).</summary>
    public static class StatusRules
    {
        public const double DiskWarnPct = 85, DiskCritPct = 95;
        public const double MemWarnPct = 80, MemCritPct = 92;

        /// <summary>Colour key for a disk space usage bar.</summary>
        public static string DiskBrushKey(double usedPct, string normalKey = "AccentBrush")
            => usedPct >= DiskCritPct ? "StatusCritBrush" : usedPct >= DiskWarnPct ? "StatusWarnBrush" : normalKey;
    }

    /// <summary>
    /// Formatting of displayed values: the same value looks identical on the Dashboard, System Info, Temp &amp; Stress and Health.
    /// </summary>
    public static class DisplayFormat
    {
        private static readonly System.Collections.Generic.HashSet<string> KeepUpper = new(StringComparer.OrdinalIgnoreCase)
        {
            "CPU", "GPU", "RAM", "VRAM", "SSD", "HDD", "NVMe", "SATA", "USB", "LAN", "WAN", "DNS", "IP", "IPv4", "IPv6", "TPM", "BIOS",
            "UEFI", "SMART", "TRIM", "BSOD", "PSU", "AC", "DC", "PCIe", "OS", "PDF", "ID", "MAC", "VPN", "I/O", "4K", "Q8", "Q32", "QD1",
        };

        /// <summary>"ALL SENSORS" → "All sensors", "CPU TEMP" → "CPU temp": titles use sentence case, abbreviations kept.</summary>
        public static string SentenceCase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var words = text.Trim().Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                string bare = w.Trim('(', ')', ',', '·', ':');
                if (bare.Length == 0) continue;
                string kept = KeepUpper.TryGetValue(bare, out var k) ? k : "";
                if (kept.Length > 0) { words[i] = w.Replace(bare, kept); continue; }
                string low = w.ToLower(CultureInfo.CurrentCulture);
                words[i] = i == 0 && low.Length > 0 ? char.ToUpper(low[0], CultureInfo.CurrentCulture) + low[1..] : low;
            }
            return string.Join(" ", words);
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Size in GB: under 10 GB with one decimal (8.5 GB), above without decimals (18 GB), above 1000 GB in TB (1.2 TB).</summary>
        public static string SizeGb(double gb)
        {
            if (double.IsNaN(gb) || gb < 0) return "—";
            if (gb >= 1000) return (gb / 1024.0).ToString("0.0", Inv) + " TB";
            if (gb < 10) return gb.ToString("0.0", Inv) + " GB";
            return gb.ToString("0", Inv) + " GB";
        }

        /// <summary>Size from bytes, with the same rules as <see cref="SizeGb"/>; under 1 GB in MB.</summary>
        public static string SizeBytes(long bytes)
        {
            if (bytes < 0) return "—";
            double gb = bytes / 1073741824.0;
            if (gb < 1) return (bytes / 1048576.0).ToString("0", Inv) + " MB";
            return SizeGb(gb);
        }

        /// <summary>Uptime without seconds: "3 h 29 min", "2 d 4 h", "12 min".</summary>
        public static string Uptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
            return $"{Math.Max(0, t.Minutes)} min";
        }

        /// <summary>Frequency: under 1000 MHz in MHz, above in GHz with 2 decimals.</summary>
        public static string Freq(double mhz)
        {
            if (double.IsNaN(mhz) || mhz <= 0) return "—";
            return mhz < 1000 ? mhz.ToString("0", Inv) + " MHz" : (mhz / 1000.0).ToString("0.00", Inv) + " GHz";
        }

        /// <summary>Data in formatul unic al aplicatiei („21 Aug 2023”).</summary>
        public static string Date(DateTime d) => DateFormatHelper.FormatDate(d);

        /// <summary>Number with the noun in the right form: "1 update pending" / "14 updates pending".</summary>
        public static string Count(int n, string singular, string plural) => n + " " + (n == 1 ? singular : plural);

        /// <summary>Maker + model, without repeating the maker when the model already starts with it ("HP" + "HP Laptop 15" → "HP Laptop 15").</summary>
        public static string MachineName(string? maker, string? model)
        {
            maker = (maker ?? "").Trim();
            model = (model ?? "").Trim();
            if (maker.Length > 0 && model.StartsWith(maker, System.StringComparison.OrdinalIgnoreCase)) return model;
            return (maker + " " + model).Trim();
        }
    }
}
