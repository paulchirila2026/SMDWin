using System;
using System.Linq;

namespace SMDWin.Services
{
    public static class UnitFormat
    {
        private static readonly int[] StdRamSizesGB = { 2, 4, 6, 8, 10, 12, 16, 24, 32, 48, 64, 128 };

        /// <summary>Nominal capacity (the size on the module label), not the "usable" RAM reported by Windows. Use it for any
        /// "X GB" text that describes the system's total CAPACITY — not for "used"/"free", which stay live, exact values.</summary>
        public static int NominalRamGB(double exactUsableGB)
        {
            if (exactUsableGB <= 0) return 0;
            return StdRamSizesGB.OrderBy(x => Math.Abs(x - (int)Math.Round(exactUsableGB))).First();
        }

        /// <summary>"8 GB" — ready to display, straight from usable MB (what the Windows APIs usually return:
        /// GlobalMemoryStatusEx, ComputerInfo, WMI).</summary>
        public static string RamTotalLabelFromMB(double totalUsableMB)
        {
            int nominal = NominalRamGB(totalUsableMB / 1024.0);
            return nominal > 0 ? $"{nominal} GB" : "—";
        }
    }
}
