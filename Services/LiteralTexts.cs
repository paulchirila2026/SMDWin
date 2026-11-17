using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>
    /// Texts written directly in XAML (without their own line in ApplyLanguage) are translated by their English text:
    /// a key is made from it ("How SMD Win looks" → how_smd_win_looks) and looked up in the language pack.
    /// The keys live in the <see cref="Section"/> section of Languages/*.json and are shared by all pages.
    /// One key serves every spelling of the same text ("MEMORY", "Memory", "memory"): the translation takes the form of
    /// the text on the page (<see cref="MatchCase"/>), and leading/trailing spaces are kept.
    /// </summary>
    public static class LiteralTexts
    {
        public const string Section = "PageText";
        private const int MaxKeyLength = 60;

        /// <summary>Key of a text: lower-case letters and digits, everything else becomes "_"; at most 60 characters.</summary>
        public static string Key(string english)
        {
            if (string.IsNullOrWhiteSpace(english)) return "";
            string key = Regex.Replace(english.Replace("&", "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            return key.Length > MaxKeyLength ? key[..MaxKeyLength].TrimEnd('_') : key;
        }

        /// <summary>
        /// Texts with nothing to translate: values, units, proper names and technical terms that are the same in every language
        /// ("1s", "°C", "Google", "CPU", "Secure Boot", "chkdsk /scan").
        /// </summary>
        public static bool IsTranslatable(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("%", StringComparison.Ordinal) || (t.Contains('\\') && !t.Contains(' '))) return false;   // file paths
            if (Regex.IsMatch(t, @"^[\d.,]+\s*(s|ms|min|h|d|%|°C|°F)?$")) return false;              // „0.5s”, „85°C”
            if (Regex.IsMatch(t, @"^°[CF]$")) return false;
            int letters = 0;
            foreach (char c in t) if (char.IsLetter(c)) letters++;
            if (letters < 3) return false;
            return !SameEverywhere.Contains(t);
        }

        // proper names, abbreviations and technical terms: unchanged in every language
        private static readonly HashSet<string> SameEverywhere = new(StringComparer.OrdinalIgnoreCase)
        {
            "drp.su", "Google", "SMD Win",
            // names of Windows tools and settings, shown as Windows itself names them
            "Event Viewer", "Windows Memory Diagnostic", "WORKGROUP",
            "CPU", "GPU", "RAM", "VRAM", "iGPU", "SSD", "BIOS", "TPM", "Secure Boot", "ECC", "UPS", "PID", "MAC", "MHz", "PDF",
            "Wi-Fi", "Ethernet", "Bluetooth", "DNS", "Gateway", "Gateway · DNS", "PING", "Router", "Internet", "VPN/Proxy", "Traceroute",
            "SMART", "TRIM", "Diskpart", "chkdsk /scan", "msconfig", "services.msc", "SysMain", "PowerShell", "EventID",
            "XMP / EXPO", "XMP/EXPO", "VCore", "BD PROCHOT", "⚠ BD PROCHOT", "Uncore / DRAM", "VRM / MOSFET", "Chipset / PCH", "NVMe / SSD",
            "Driver", "Driver OK", "Manual", "KB/s ↓", "— GB / — GB", "Hot Spot", "Throttling", "THROTTLE", "Cache", "Timings", "Min", "Max", "Build", "Slot", "LIVE", "PORTABLE", "STRESS", "BENCHMARK",
            "DISK I/O", "TOP CPU", "TOP RAM", "CPU TEMP", "GPU TEMP", "VRAM Temp", "Device ID",
            // names of the pages and of the Windows tools opened from the app
            "Dashboard", "Temp & Stress", "BSOD / Crash", "Windows Memory Diagnostic", "Disk Management", "Device Manager",
            "Task Manager", "Programs & Features", "Disk Cleanup", "Auto Clean",
            // short labels that are the same in Romanian, German and French
            "USB", "CPU + GPU", "Test:",
        };

        /// <summary>Translation of the text into the current language; the English text stays when there is no key.</summary>
        public static string Translate(string english)
            => Translate(english, LanguageService.CurrentCode, key => LanguageService.Get(Section, key, Missing) is var v && v != Missing ? v : null);

        /// <summary>
        /// Translation of a text with the language pack given as a function (key → translation or null).
        /// In English the text stays exactly as on the page.
        /// </summary>
        public static string Translate(string english, string languageCode, Func<string, string?> lookup)
        {
            if (string.IsNullOrEmpty(english) || languageCode == "en") return english;
            string key = Key(english);
            if (key.Length == 0) return english;
            string? found = lookup(key);
            if (string.IsNullOrWhiteSpace(found)) return english;

            // leading/trailing spaces belong to the page layout (" free / "), not to the text
            string core = english.Trim();
            int start = english.IndexOf(core, StringComparison.Ordinal);
            string lead = english[..start], trail = english[(start + core.Length)..];
            return lead + MatchCase(core, found.Trim(), languageCode) + trail;
        }

        /// <summary>
        /// Gives the translation the case of the text on the page: all capitals if the text is written that way ("MEMORY"),
        /// otherwise the first letter upper or lower case as in the text ("Read" / "read"). In German the first letter is not
        /// lowered (nouns are capitalized); a leading abbreviation ("SMART …") stays as is.
        /// </summary>
        public static string MatchCase(string source, string translation, string languageCode)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation)) return translation;
            int letters = 0; bool anyLower = false;
            foreach (char c in source)
                if (char.IsLetter(c)) { letters++; if (char.IsLower(c)) anyLower = true; }
            if (letters >= 2 && !anyLower) return translation.ToUpperInvariant();

            int s = FirstLetter(source), t = FirstLetter(translation);
            if (s < 0 || t < 0) return translation;
            bool acronym = t + 1 < translation.Length && char.IsUpper(translation[t + 1]);
            if (char.IsUpper(source[s]) && char.IsLower(translation[t]))
                return translation[..t] + char.ToUpperInvariant(translation[t]) + translation[(t + 1)..];
            if (char.IsLower(source[s]) && char.IsUpper(translation[t]) && !acronym && languageCode != "de")
                return translation[..t] + char.ToLowerInvariant(translation[t]) + translation[(t + 1)..];
            return translation;
        }

        private static int FirstLetter(string text)
        {
            for (int i = 0; i < text.Length; i++) if (char.IsLetter(text[i])) return i;
            return -1;
        }

        private const string Missing = "\u0001";

        /// <summary>true if the current language pack has a translation for this text.</summary>
        public static bool HasTranslation(string english)
        {
            string key = Key(english);
            if (key.Length == 0) return true;
            return LanguageService.Get(Section, key, Missing) != Missing;
        }
    }
}
