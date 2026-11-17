using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Short, plain-word explanations for the technical terms on the pages (Package power, TRIM, Boost…).
    /// The on-screen label is matched by its text (<see cref="LiteralTexts.Key"/>), and the explanation appears on
    /// mouse hover. The text in the current language lives in the "Glossary" section of Languages/*.json;
    /// the English text here is the fallback.
    /// </summary>
    public static class Glossary
    {
        public const string Section = "Glossary";

        private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
        {
            ["package_power"] = "Total power drawn by the whole processor chip (cores, built-in graphics, memory controller). It rises when the processor works hard.",
            ["clock_now"] = "Current speed of the processor. It rises under load and drops when idle to save power.",
            ["vram"] = "Video memory: the memory on the graphics card used for images and textures.",
            ["trim"] = "Tells an SSD which blocks are no longer used so it can clean them in advance and stay fast. Safe to run. Not needed for hard disks.",
            ["file_system_check"] = "Scans the disk's file structure for errors. A scan only reads; repairs may need a restart.",
            ["fast_startup"] = "Saves part of Windows to disk at shutdown so the next start is faster. A normal shutdown then behaves like a partial hibernate.",
            ["usb_selective_suspend"] = "Lets Windows switch off idle USB ports to save power. Turn it off if a mouse, keyboard or USB device disconnects at random.",
            ["boost"] = "Lets the processor run faster than its base speed for short periods when power and temperature allow. Disabled means quieter and cooler, with lower peak speed.",
            ["secure_boot"] = "A firmware feature that lets only trusted software start at boot. Windows 11 asks for it; many older computers do not have it, and that is normal.",
            ["tpm"] = "A security chip that stores encryption keys. Windows 11 asks for it; many older computers do not have one, and that is normal.",
            ["boot_mode"] = "How the computer starts Windows: UEFI is the modern way, BIOS / MBR is the older one.",
            ["jitter"] = "How much the delay (ping) changes from one test packet to the next. Lower is better for calls and games.",
            ["gateway"] = "The router that connects this computer to the local network and the internet.",
            ["dns"] = "Turns website names into addresses. A slow DNS makes pages take longer to start loading.",
            ["workgroup"] = "Name of the simple home or office network group the computer belongs to. Usually WORKGROUP.",
            ["processor_state_range"] = "Lowest and highest processor speed, as a percentage of its maximum. Lowering the top value gives less heat and fan noise.",
            ["throttling"] = "The processor slowing itself down to avoid overheating or drawing too much power.",
            ["sleep_s3"] = "Sleep mode that keeps memory powered and switches off almost everything else. The computer wakes up fast.",
            ["wake_timer"] = "A scheduled task that is allowed to wake the computer from sleep, for example for updates or maintenance.",
            ["smart"] = "Health data that the disk reports about itself: errors, wear, temperature, hours of use.",
            ["vcore"] = "Voltage supplied to the processor cores. It changes with load.",
            ["uptime"] = "Time since the computer was last started.",
        };

        // the normalized label (LiteralTexts.Key) → the explanation key
        private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
        {
            ["package_power"] = "package_power",
            ["clock_now"] = "clock_now",
            ["vram"] = "vram",
            ["vram_in_use"] = "vram",
            ["trim"] = "trim",
            ["run_trim"] = "trim",
            ["file_system_check"] = "file_system_check",
            ["fast_startup"] = "fast_startup",
            ["usb_selective_suspend"] = "usb_selective_suspend",
            ["processor_boost_mode_turbo"] = "boost",
            ["boost"] = "boost",
            ["turbo_boost"] = "boost",
            ["secure_boot"] = "secure_boot",
            ["tpm"] = "tpm",
            ["boot_mode"] = "boot_mode",
            ["jitter"] = "jitter",
            ["gateway"] = "gateway",
            ["gateway_dns"] = "gateway",
            ["dns"] = "dns",
            ["workgroup"] = "workgroup",
            ["processor_state_range_min_max"] = "processor_state_range",
            ["throttling"] = "throttling",
            ["wake_timer"] = "wake_timer",
            ["smart"] = "smart",
            ["vcore"] = "vcore",
        };

        /// <summary>Explanation key for an on-screen label, or null if the label has no explanation.</summary>
        public static string? KeyFor(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            return Labels.TryGetValue(LiteralTexts.Key(label), out var key) ? key : null;
        }

        /// <summary>English explanation (fallback when the current language does not have it).</summary>
        public static string EnglishText(string key) => English.TryGetValue(key, out var t) ? t : "";

        /// <summary>All explanation keys, for the language packs and tests.</summary>
        public static IReadOnlyCollection<string> Keys => English.Keys.ToList();

        /// <summary>The explanation in the current language (fallback: English).</summary>
        public static string Text(string key) => LanguageService.S(Section, key, EnglishText(key));
    }
}
