
using System;
using System.Collections.Generic;
using System.Management;
using SMDWin.Services;

namespace SMDWin.Services
{
    /// <summary>
    /// Reads temperatures from the OpenHardwareMonitor WMI namespace and ACPI thermal zones.
    /// Used as a secondary fallback if LHM does not detect the sensors.
    /// </summary>
    public static class OhmWmiFallback
    {
        // ── Cache — refreshed on every call (no WMI connection kept open) ──
        public static float? CpuTempOhm  { get; private set; }
        public static float? GpuTempOhm  { get; private set; }
        public static float? AcpiMinTemp { get; private set; }  // the lowest ACPI zone (usually the CPU package)

    }
}
