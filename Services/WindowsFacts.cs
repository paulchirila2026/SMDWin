using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>Whether the Windows version still receives security updates.</summary>
    public enum WindowsSupport
    {
        Supported,     // receives updates (or nothing else can be said)
        Windows10End,  // Windows 10: the free updates stopped on 14 October 2025
        LegacyEnd,     // Windows 8.1 and older
    }

    /// <summary>Un antivirus inregistrat in Windows Security.</summary>
    public sealed record AntivirusProduct(string Name, bool Enabled, bool UpToDate);

    /// <summary>Conclusions about Windows and its protection, from the values read by the Windows info view.</summary>
    public static class WindowsFacts
    {
        /// <summary>
        /// Support state by build number. Long-term servicing editions (LTSC / LTSB, "EnterpriseS", "IoTEnterpriseS") and
        /// server editions have a different calendar and are not judged here.
        /// </summary>
        public static WindowsSupport SupportFor(int build, string? editionId)
        {
            string edition = editionId ?? "";
            if (build <= 0) return WindowsSupport.Supported;
            if (edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase)
                || edition.Contains("Server", StringComparison.OrdinalIgnoreCase)) return WindowsSupport.Supported;
            if (build < 10240) return WindowsSupport.LegacyEnd;
            return build < 22000 ? WindowsSupport.Windows10End : WindowsSupport.Supported;
        }

        /// <summary>
        /// State of an antivirus from the Windows code (AntiVirusProduct.productState, three bytes):
        /// the middle byte says whether protection is on, the last whether the signatures are up to date.
        /// </summary>
        public static (bool Enabled, bool UpToDate) AntivirusState(long productState)
            => (((productState >> 12) & 0xF) == 1, ((productState >> 4) & 0xF) == 0);

        /// <summary>
        /// The antivirus actually protecting the computer: the first one that is on (Windows Defender turns itself off when
        /// another is installed, but stays in the list); if none is on, the first in the list. null = no antivirus.
        /// </summary>
        public static AntivirusProduct? ActiveAntivirus(IEnumerable<AntivirusProduct> products)
        {
            var list = products.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
            return list.FirstOrDefault(p => p.Enabled) ?? list.FirstOrDefault();
        }

        /// <summary>How many of the protections read are active: (active, read). A protection not read (null) is not counted.</summary>
        public static (int Active, int Known) ProtectionScore(params bool?[] protections)
            => (protections.Count(p => p == true), protections.Count(p => p.HasValue));

        /// <summary>
        /// The BitLocker state of a volume (Win32_EncryptableVolume.ProtectionStatus): 1 = protected, 0 = not protected,
        /// anything else = unknown (null).
        /// </summary>
        public static bool? BitLockerOn(long protectionStatus) => protectionStatus switch { 1 => true, 0 => false, _ => null };
    }
}
