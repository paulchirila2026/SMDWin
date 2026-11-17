using System;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// Windows' own description of itself, read from the registry (HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion) instead of
    /// WMI's Win32_OperatingSystem: instant (no WMI service round trip, no 10 s timeout when WMI is busy at start-up) and in
    /// English on every Windows language (WMI's "OSArchitecture" is translated, e.g. "64 biți"). Read once and kept — it does not
    /// change while the program runs.
    /// </summary>
    public sealed class OsRegistryInfo
    {
        public string ProductName { get; private init; } = "";   // "Windows 11 Pro"
        public string Edition { get; private init; } = "";       // "Professional", "EnterpriseS"…
        public string BuildNumber { get; private init; } = "";   // "26100"
        public string Ubr { get; private init; } = "";           // "2314"
        public string DisplayVersion { get; private init; } = ""; // "24H2"
        public string RegisteredOwner { get; private init; } = "";
        public string Architecture { get; private init; } = "";  // "64-bit" / "32-bit" / "ARM 64-bit"
        /// <summary>The installation date — like WMI's InstallDate, it moves to the date of the last feature update.</summary>
        public DateTime? InstalledOn { get; private init; }
        public bool Ok => BuildNumber.Length > 0;

        /// <summary>The install date in WMI's compact form ("yyyyMMddHHmmss…"), for code that parses that form.</summary>
        public string InstallDateCompact => InstalledOn is DateTime d ? d.ToString("yyyyMMddHHmmss") + ".000000+000" : "";

        private static OsRegistryInfo? _cached;
        private static readonly object _lock = new();

        public static OsRegistryInfo Current
        {
            get
            {
                if (_cached != null) return _cached;
                lock (_lock) return _cached ??= Read();
            }
        }

        private static OsRegistryInfo Read()
        {
            try
            {
                // the 64-bit view, also from a 32-bit process
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return new OsRegistryInfo();

                string S(string name) => key.GetValue(name)?.ToString()?.Trim() ?? "";
                string build = S("CurrentBuildNumber");
                if (build.Length == 0) build = S("CurrentBuild");
                string product = S("ProductName");
                // ProductName stays "Windows 10 …" in the registry on Windows 11
                if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");

                DateTime? installed = null;
                // InstallTime: FILETIME (QWORD), more precise; InstallDate: Unix seconds (DWORD)
                if (key.GetValue("InstallTime") is long ft && ft > 0)
                {
                    try { installed = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch (ArgumentOutOfRangeException) { }
                }
                if (installed == null && key.GetValue("InstallDate") is int unix && unix > 0)
                    installed = DateTimeOffset.FromUnixTimeSeconds((uint)unix).LocalDateTime;

                string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64   => "64-bit",
                    System.Runtime.InteropServices.Architecture.Arm64 => "ARM 64-bit",
                    System.Runtime.InteropServices.Architecture.X86   => "32-bit",
                    var other => other.ToString(),
                };

                return new OsRegistryInfo
                {
                    ProductName = product,
                    Edition = S("EditionID"),
                    BuildNumber = build,
                    Ubr = S("UBR"),
                    DisplayVersion = S("DisplayVersion") is { Length: > 0 } dv ? dv : S("ReleaseId"),
                    RegisteredOwner = S("RegisteredOwner"),
                    Architecture = arch,
                    InstalledOn = installed,
                };
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "OsRegistryInfo");
                return new OsRegistryInfo();
            }
        }
    }
}
