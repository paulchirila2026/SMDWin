using System;
using System.Globalization;

namespace SMDWin.Services
{
    /// <summary>
    /// Data written / read by a disk over its whole life (SMART attributes 0xF1 / 0xF2): the raw value means different
    /// things between manufacturers (sectors, GiB, 32 MiB blocks…), and for an NVMe SSD seen through a RAID controller
    /// (Intel RST) it is in NVMe "data units" of 512,000 bytes, not 512-byte sectors.
    /// </summary>
    public static class LifetimeData
    {
        /// <summary>One NVMe "data unit" = 1000 blocks of 512 bytes.</summary>
        public const double NvmeDataUnitBytes = 512_000;

        /// <summary>Below this, the value read as sectors would mean under ~100 MB written over the disk's whole life —
        /// impossible for a used disk; some models (e.g. Kingston A400) actually report directly in GiB.</summary>
        private const long MaxRawThatCanBeGiB = 200_000;

        /// <summary>Bytes represented by the raw value. ≤ 0 = unknown.</summary>
        /// <param name="smartctlName">The name smartctl gives the attribute (may contain the unit, e.g. "Host_Writes_32MiB").</param>
        /// <param name="nvmeDataUnits">true = an NVMe disk whose values come in NVMe data units.</param>
        public static double Bytes(long raw, string? smartctlName, int sectorBytes, bool nvmeDataUnits)
        {
            if (raw <= 0) return -1;
            double r = raw;
            int sector = sectorBytes > 0 ? sectorBytes : 512;
            string n = (smartctlName ?? "").ToLowerInvariant();
            bool named = n.Length > 0 && !n.StartsWith("unknown", StringComparison.Ordinal);
            if (named)
            {
                if (n.Contains("32mib")) return r * 32.0 * 1_048_576.0;
                if (n.Contains("tib"))   return r * 1_099_511_627_776.0;
                if (n.Contains("gib"))   return r * 1_073_741_824.0;
                if (n.Contains("mib"))   return r * 1_048_576.0;
                if (n.Contains("_tb"))   return r * 1e12;
                if (n.Contains("_gb"))   return r * 1e9;
                if (n.Contains("_mb"))   return r * 1e6;
            }
            if (nvmeDataUnits) return r * NvmeDataUnitBytes;
            if (named && (n.Contains("lba") || n.Contains("sector"))) return r * sector;

            double bytes = r * sector;
            return bytes < 1e9 && raw < MaxRawThatCanBeGiB ? r * 1_073_741_824.0 : bytes;
        }

        /// <summary>"23.6 TB", "850 GB", "12 MB"; "—" when the value is unknown.</summary>
        public static string Format(double bytes, IFormatProvider? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            if (!double.IsFinite(bytes) || bytes <= 0) return "—";
            if (bytes >= 1e12) return (bytes / 1e12).ToString("0.0", culture) + " TB";
            if (bytes >= 1e9)  return (bytes / 1e9).ToString(bytes >= 1e11 ? "0" : "0.#", culture) + " GB";
            return Math.Max(1, bytes / 1e6).ToString("0", culture) + " MB";
        }
    }
}
