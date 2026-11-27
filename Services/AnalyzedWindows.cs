using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>A Windows installation found on a drive (not the one SMD Win runs on).</summary>
    public sealed record WindowsInstall(string Root, string Name, string Version, string VolumeLabel, long SizeBytes, DateTime? LastUsed)
    {
        /// <summary>"Windows 11 · 10.0.22631 · D: (Data) · last used 26 Nov 2026"</summary>
        public string Letter => Root.TrimEnd('\\');
    }

    /// <summary>What the System log of a Windows says about its last starts, stops and crashes.</summary>
    public sealed class CrashDiagnosis
    {
        public DateTime? LastBoot, LastCleanShutdown, LastUnexpected, LastBlueScreen;
        public int Unexpected, BlueScreens, DiskErrors, HardwareErrors;
        public string LastStopCode = "";
        public bool LogMissing;
    }

    /// <summary>
    /// Which Windows the Event Viewer and BSOD pages look at: this PC (the default), or a Windows installation on another drive —
    /// the internal disk of a PC when SMD Win runs from a rescue stick (Hiren's BootCD PE, WinPE), or the disk of another PC
    /// connected by USB. The logs are read straight from &lt;drive&gt;\Windows\System32\winevt\Logs\*.evtx and the crash dumps
    /// from &lt;drive&gt;\Windows\Minidump, so nothing has to start on that Windows.
    /// </summary>
    public static class AnalyzedWindows
    {
        /// <summary>The root of the Windows being looked at ("D:\"), or null for this PC.</summary>
        public static string? OfflineRoot { get; private set; }
        public static WindowsInstall? Offline { get; private set; }
        public static bool IsOffline => OfflineRoot != null;
        /// <summary>Raised on the thread that changed the choice.</summary>
        public static event Action? Changed;

        public static void Set(WindowsInstall? install)
        {
            Offline = install;
            OfflineRoot = install?.Root;
            AppLogger.Info($"AnalyzedWindows: {(install == null ? "this PC" : install.Root + " " + install.Name + " " + install.Version)}");
            Changed?.Invoke();
        }

        /// <summary>true when SMD Win itself runs from WinPE (Hiren's BootCD PE, a Windows setup / rescue stick).</summary>
        public static bool RunningInWinPE()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
                return k != null;
            }
            catch { return false; }
        }

        /// <summary>The query source of a log: the live log by name, or the .evtx file of the other Windows.</summary>
        public static (string path, PathType type) LogSource(string logName) => LogSourceFor(OfflineRoot, logName);

        /// <summary>The query source of a log for a given Windows root (null = this PC).</summary>
        public static (string path, PathType type) LogSourceFor(string? root, string logName)
            => root == null ? (logName, PathType.LogName)
                            : (Path.Combine(root, "Windows", "System32", "winevt", "Logs", logName + ".evtx"), PathType.FilePath);

        /// <summary>Where the crash dumps of the Windows being looked at are (null = use this PC's CrashControl setting).</summary>
        public static string? OfflineMinidumpFolder => OfflineRoot == null ? null : Path.Combine(OfflineRoot, "Windows", "Minidump");
        public static string? OfflineMemoryDump => OfflineRoot == null ? null : Path.Combine(OfflineRoot, "Windows", "MEMORY.DMP");

        /// <summary>The Windows installations on the other drives (fixed, removable, USB), newest use first.</summary>
        public static List<WindowsInstall> Find()
        {
            var list = new List<WindowsInstall>();
            string self = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").ToUpperInvariant();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    string root = d.RootDirectory.FullName;
                    if (root.ToUpperInvariant() == self) continue;
                    if (!File.Exists(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"))) continue;
                    string version = "", name = "Windows";
                    string krnl = Path.Combine(root, "Windows", "System32", "ntoskrnl.exe");
                    if (File.Exists(krnl))
                    {
                        var fv = FileVersionInfo.GetVersionInfo(krnl);
                        version = $"{fv.ProductMajorPart}.{fv.ProductMinorPart}.{fv.ProductBuildPart}";
                        name = fv.ProductMajorPart == 10 ? (fv.ProductBuildPart >= 22000 ? "Windows 11" : "Windows 10")
                             : fv.ProductMajorPart == 6 ? fv.ProductMinorPart switch { 1 => "Windows 7", 2 => "Windows 8", 3 => "Windows 8.1", _ => "Windows Vista" }
                             : "Windows";
                    }
                    string sysLog = Path.Combine(root, "Windows", "System32", "winevt", "Logs", "System.evtx");
                    DateTime? last = File.Exists(sysLog) ? File.GetLastWriteTime(sysLog) : null;
                    list.Add(new WindowsInstall(root, name, version, d.VolumeLabel ?? "", d.TotalSize, last));
                }
                catch (Exception ex) { AppLogger.Debug($"AnalyzedWindows.Find {d.Name}: {ex.Message}"); }
            }
            return list.OrderByDescending(w => w.LastUsed ?? DateTime.MinValue).ToList();
        }

        /// <summary>
        /// The last 90 days of the System log of the Windows being looked at: starts (6005), clean stops (6006), unexpected stops
        /// (6008 / Kernel-Power 41), blue screens (BugCheck 1001), disk errors (disk 7 / 51 / 153, Ntfs 55 / 98) and WHEA errors.
        /// </summary>
        public static Task<CrashDiagnosis> DiagnoseAsync(string? root, CancellationToken ct) => Task.Run(() =>
        {
            var r = new CrashDiagnosis();
            var (path, type) = LogSourceFor(root, "System");
            if (type == PathType.FilePath && !File.Exists(path)) { r.LogMissing = true; return r; }
            try
            {
                string from = DateTime.Now.AddDays(-90).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var q = new EventLogQuery(path, type,
                    $"*[System[TimeCreated[@SystemTime>='{from}'] and (EventID=6005 or EventID=6006 or EventID=6008 or EventID=41 or EventID=1001 " +
                    "or EventID=7 or EventID=51 or EventID=153 or EventID=55 or EventID=98 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=46 or EventID=47)]]");
                using var reader = new EventLogReader(q);
                EventRecord? rec;
                int n = 0, n41 = 0, n6008 = 0;
                while (!ct.IsCancellationRequested && (rec = reader.ReadEvent()) != null && n++ < 50000)
                {
                    using (rec)
                    {
                        string src = rec.ProviderName ?? "";
                        DateTime t = rec.TimeCreated ?? DateTime.MinValue;
                        static DateTime? Max(DateTime? a, DateTime b) => a == null || b > a ? b : a;
                        switch (rec.Id)
                        {
                            case 6005 when src == "EventLog": r.LastBoot = Max(r.LastBoot, t); break;
                            case 6006 when src == "EventLog": r.LastCleanShutdown = Max(r.LastCleanShutdown, t); break;
                            case 6008 when src == "EventLog": n6008++; r.LastUnexpected = Max(r.LastUnexpected, t); break;
                            case 41 when src.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase): n41++; r.LastUnexpected = Max(r.LastUnexpected, t); break;
                            case 1001 when src.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) || src == "BugCheck":
                                r.BlueScreens++;
                                if (r.LastBlueScreen == null || t > r.LastBlueScreen)
                                {
                                    r.LastBlueScreen = t;
                                    try { r.LastStopCode = rec.Properties.Count > 0 ? rec.Properties[0].Value?.ToString() ?? "" : ""; }
                                    catch (Exception ex) { AppLogger.Swallowed(ex); }
                                }
                                break;
                            case 7 or 51 or 153 when src.Equals("disk", StringComparison.OrdinalIgnoreCase):
                            case 55 or 98 when src.Equals("Ntfs", StringComparison.OrdinalIgnoreCase) || src.Contains("Ntfs", StringComparison.OrdinalIgnoreCase):
                                r.DiskErrors++; break;
                            case 17 or 18 or 19 or 20 or 46 or 47 when src.Contains("WHEA", StringComparison.OrdinalIgnoreCase):
                                r.HardwareErrors++; break;
                        }
                    }
                }
                // one unexpected stop is usually logged twice (Kernel-Power 41 and EventLog 6008 at the next start): count one kind
                r.Unexpected = Math.Max(n41, n6008);
            }
            catch (Exception ex) { AppLogger.Debug($"DiagnoseAsync: {ex.Message}"); r.LogMissing = true; }
            return r;
        }, ct);
    }
}
