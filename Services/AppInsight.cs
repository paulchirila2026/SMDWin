using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// What can be learned about an installed program beyond its registry entry (Applications page, "Selected" card):
    /// real disk usage, when it last ran, whether it runs now, whether the main program is signed.
    /// All are (slow) disk / system reads — call them from the background. Anything that cannot be found stays null / empty.
    /// </summary>
    public sealed class AppInsightResult
    {
        /// <summary>The main program (from the declared icon or picked from the folder); empty if not found.</summary>
        public string MainExe { get; init; } = "";
        /// <summary>Real size of the program folder; null when the folder is missing or shared.</summary>
        public long? DiskBytes { get; init; }
        /// <summary>Counting stopped at the time limit: the real size is at least this much.</summary>
        public bool DiskPartial { get; init; }
        /// <summary>Last start recorded by Windows (prefetch); null if there is no trace.</summary>
        public DateTime? LastRun { get; init; }
        /// <summary>The program's processes running now (file names, no duplicates) and how many there are in total.</summary>
        public List<string> RunningNames { get; init; } = new();
        public int RunningCount { get; init; }
        /// <summary>„signed” / „untrusted” / „unsigned” / „” (neverificat), ca in <see cref="FileSignatureService"/>.</summary>
        public string SignStatus { get; init; } = "";
        public string SignPublisher { get; init; } = "";
    }

    public static class AppInsight
    {
        private static readonly TimeSpan DiskBudget = TimeSpan.FromSeconds(5);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>Folders that do not belong to a single program (a program installed "in" them has no folder of its own).</summary>
        private static string[] SharedFolders()
        {
            var kinds = new[]
            {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            };
            return kinds.Select(k => { try { return Environment.GetFolderPath(k); } catch (Exception) { return ""; } })
                        .Where(p => p.Length > 0).ToArray();
        }

        public static AppInsightResult Read(InstalledApp app, CancellationToken ct)
        {
            string folder = (app.InstallLocation ?? "").Trim().Trim('"');
            bool ownFolder = false;
            try { ownFolder = AppCommands.IsOwnFolder(folder, SharedFolders()) && Directory.Exists(folder); }
            catch (Exception ex) { AppLogger.Debug($"AppInsight folder: {ex.Message}"); }

            // main program: the one from the declared icon, otherwise the one in the folder whose name resembles the program
            var topExes = new List<string>();
            if (ownFolder)
                try { topExes = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Take(40).ToList(); }
                catch (Exception ex) { AppLogger.Debug($"AppInsight exes: {ex.Message}"); }

            string mainExe = AppCommands.IconFilePath(app.IconSource);
            if (!mainExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(mainExe) || AppCommands.IsGenericExeName(mainExe))
            {
                string picked = AppCommands.PickMainExe(app.Name, topExes.Select(Path.GetFileName).Where(n => n != null)!);
                mainExe = picked.Length > 0 ? Path.Combine(folder, picked) : "";
            }

            long? diskBytes = null; bool partial = false;
            if (ownFolder && !ct.IsCancellationRequested) (diskBytes, partial) = FolderSize(folder, ct);

            DateTime? lastRun = ct.IsCancellationRequested ? null : LastRun(mainExe, topExes);

            var runningNames = new List<string>(); int runningCount = 0;
            if (!ct.IsCancellationRequested && (ownFolder || mainExe.Length > 0))
                (runningNames, runningCount) = Running(ownFolder ? folder : "", mainExe);

            string signStatus = "", signPublisher = "";
            if (!ct.IsCancellationRequested && mainExe.Length > 0)
            {
                try
                {
                    int budget = 1;
                    var info = FileSignatureService.GetSignInfo(mainExe, ref budget);
                    signStatus = info.Status ?? ""; signPublisher = info.Publisher ?? "";
                }
                catch (Exception ex) { AppLogger.Debug($"AppInsight signature: {ex.Message}"); }
            }

            return new AppInsightResult
            {
                MainExe = mainExe, DiskBytes = diskBytes, DiskPartial = partial, LastRun = lastRun,
                RunningNames = runningNames, RunningCount = runningCount,
                SignStatus = signStatus, SignPublisher = signPublisher,
            };
        }

        /// <summary>Sum of the file sizes in the folder (without links to other folders), with a time limit.</summary>
        private static (long? Bytes, bool Partial) FolderSize(string folder, CancellationToken ct)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                var watch = Stopwatch.StartNew();
                long total = 0; int n = 0;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    total += file.Length;
                    if ((++n & 0xFF) == 0 && (ct.IsCancellationRequested || watch.Elapsed > DiskBudget)) return (total, true);
                }
                return (total, false);
            }
            catch (Exception ex) { AppLogger.Debug($"AppInsight.FolderSize {folder}: {ex.Message}"); return (null, false); }
        }

        /// <summary>
        /// Last start from Windows prefetch files (C:\Windows\Prefetch\NAME.EXE-code.pf — the last write time is the last start).
        /// Matching is by program name, so helper programs with common names are skipped.
        /// </summary>
        private static DateTime? LastRun(string mainExe, List<string> topExes)
        {
            try
            {
                string prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                if (!Directory.Exists(prefetch)) return null;
                var names = new List<string>();
                if (mainExe.Length > 0) names.Add(Path.GetFileName(mainExe));
                names.AddRange(topExes.Select(e => Path.GetFileName(e)!).Where(n => !AppCommands.IsGenericExeName(n)).Take(12));

                DateTime? newest = null;
                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var pf in Directory.EnumerateFiles(prefetch, AppCommands.PrefetchName(name) + "-*.pf"))
                    {
                        if (!AppCommands.IsPrefetchOf(pf, name)) continue;
                        var written = File.GetLastWriteTime(pf);
                        if (newest == null || written > newest) newest = written;
                    }
                return newest;
            }
            catch (Exception ex) { AppLogger.Debug($"AppInsight.LastRun: {ex.Message}"); return null; }
        }

        /// <summary>Processes started from the program folder (or the main program itself, when the folder is not its own).</summary>
        private static (List<string> Names, int Count) Running(string folder, string mainExe)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            try
            {
                var buffer = new StringBuilder(1024);
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                        if (handle == IntPtr.Zero) continue;
                        try
                        {
                            uint size = (uint)buffer.Capacity;
                            buffer.Clear();
                            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) continue;
                            string path = buffer.ToString();
                            bool match = (folder.Length > 0 && AppCommands.IsInside(path, folder))
                                      || (mainExe.Length > 0 && string.Equals(path, mainExe, StringComparison.OrdinalIgnoreCase));
                            if (!match) continue;
                            count++;
                            names.Add(Path.GetFileName(path));
                        }
                        finally { CloseHandle(handle); }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"AppInsight.Running: {ex.Message}"); }
            return (names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), count);
        }
    }
}
