using System;
using System.IO;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Single source for the app's data paths.
    ///
    /// SMDWin is portable: data (settings, logs, self-test results, reports, history) lives in one "Data" folder
    /// next to SMDWin.exe, so files do not pile up next to the exe.
    /// Content shipped inside the exe (Languages, Tools) is embedded as resources and extracted on first use
    /// into BundleDirectory — see below.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>The real folder of SMDWin.exe (USB stick, Desktop etc.).</summary>
        public static string ExeDirectory { get; } = ResolveExeDirectory();

        /// <summary>The data folder: "Data" next to the exe if it is writable, otherwise %AppData%\SMDWin
        /// (for example a write-protected USB stick or a folder without write permission).</summary>
        public static string DataDirectory { get; } = ResolveDataDirectory();

        /// <summary>Name of the data folder next to the exe.</summary>
        public const string DataFolderName = "Data";

        /// <summary>True if data is saved next to the exe (normal portable mode).</summary>
        public static bool IsPortableData => string.Equals(
            Path.GetFullPath(DataDirectory).TrimEnd('\\'),
            Path.GetFullPath(Path.Combine(ExeDirectory, DataFolderName)).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        /// <summary>The program log and the sensor records (Data\Logs).</summary>
        public static string LogsDirectory => Data("Logs");

        /// <summary>Default folder for reports and exports (Data\Reports), created on demand.</summary>
        public static string ReportsDirectory
        {
            get
            {
                string d = Data("Reports");
                try { Directory.CreateDirectory(d); } catch (Exception ex) { AppLogger.Swallowed(ex); }
                return d;
            }
        }

        private static readonly object _bundleLock = new();
        private static string? _bundleDir;

        /// <summary>
        /// Folder with the content shipped inside the exe: Languages\*.json and Tools\smartctl.exe (+ drivedb.h).
        /// On first access it extracts the "SMDWinBundle/…" resources to %TEMP%\SMDWin\bundle\&lt;build&gt;\.
        /// Only a few files (~2 MB), so it takes milliseconds; on later starts the files already exist and nothing is written.
        /// &lt;build&gt; = the assembly MVID, so every new build has its own folder (no mixing between versions).
        /// If extraction fails (TEMP not accessible), the exe folder is used — a Languages / Tools folder placed
        /// next to the exe by hand still works there.
        /// </summary>
        public static string BundleDirectory
        {
            get
            {
                if (_bundleDir != null) return _bundleDir;
                lock (_bundleLock)
                {
                    return _bundleDir ??= ExtractBundle();
                }
            }
        }

        private const string BundlePrefix = "SMDWinBundle/";

        private static string ExtractBundle()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var asm = typeof(AppPaths).Assembly;
                string buildId = asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
                string root = Path.Combine(Path.GetTempPath(), "SMDWin", "bundle", buildId);
                int written = 0, total = 0;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith(BundlePrefix, StringComparison.Ordinal)) continue;
                    total++;
                    string rel = name.Substring(BundlePrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    string dest = Path.Combine(root, rel);
                    using var src = asm.GetManifestResourceStream(name);
                    if (src == null) continue;
                    bool program = IsProgramFile(dest);
                    // Already extracted (earlier start) and complete → nothing to write. A program (smartctl) runs with
                    // administrator rights from a folder other programs can write to, so for it the content must be
                    // identical to the copy inside SMDWin.exe, not just the size.
                    if (File.Exists(dest) && new FileInfo(dest).Length == src.Length
                        && (!program || LockIfIdentical(dest, src)))
                        continue;
                    src.Position = 0;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    // Atomic write: temporary file + move, so two instances started at the same time
                    // never leave a half-written smartctl.exe.
                    string tmp = dest + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                    using (var fs = File.Create(tmp)) src.CopyTo(fs);
                    try { File.Move(tmp, dest, overwrite: true); }
                    catch { try { File.Delete(tmp); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } if (!File.Exists(dest)) throw; }
                    written++;
                    if (program)
                    {
                        src.Position = 0;
                        if (!LockIfIdentical(dest, src))
                            throw new IOException("the extracted program does not match the embedded copy: " + rel);
                    }
                }
                if (total == 0) return ExeDirectory; // development build without resources → next to the exe
                AppLogger.Info("AppPaths: bundle {0} ({1}/{2} files written, {3} ms)", root, written, total, sw.ElapsedMilliseconds);
                return root;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "AppPaths.ExtractBundle — using the exe folder");
                return ExeDirectory;
            }
        }

        /// <summary>Open, write-protected handles on the extracted programs: kept for the whole run.</summary>
        private static readonly System.Collections.Generic.List<FileStream> _toolLocks = new();

        private static bool IsProgramFile(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Opens the extracted program so nobody can change or delete it while SMD Win runs (sharing = read only), then
        /// checks that its content is identical to the embedded copy. On a match the handle stays open; otherwise it is
        /// closed and false is returned (the file is written again).
        /// </summary>
        private static bool LockIfIdentical(string path, Stream embedded)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] onDisk = System.Security.Cryptography.SHA256.HashData(fs);
                embedded.Position = 0;
                byte[] expected = System.Security.Cryptography.SHA256.HashData(embedded);
                if (!onDisk.AsSpan().SequenceEqual(expected))
                {
                    AppLogger.Warning("AppPaths: {0} differs from the embedded copy — written again", path);
                    fs.Dispose();
                    return false;
                }
                lock (_toolLocks) _toolLocks.Add(fs);
                return true;
            }
            catch (Exception ex)
            {
                fs?.Dispose();
                AppLogger.Warning(ex, "AppPaths.LockIfIdentical " + path);
                return false;
            }
        }

        public static string AppDataFallbackDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMDWin");

        public static string Data(params string[] relative)
        {
            var parts = new string[relative.Length + 1];
            parts[0] = DataDirectory;
            Array.Copy(relative, 0, parts, 1, relative.Length);
            return Path.Combine(parts);
        }

        private static string ResolveExeDirectory()
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                {
                    var d = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* fall back to BaseDirectory below */ }
            return AppContext.BaseDirectory;
        }

        private static string ResolveDataDirectory()
        {
            if (IsWritable(ExeDirectory))
            {
                string data = Path.Combine(ExeDirectory, DataFolderName);
                try { Directory.CreateDirectory(data); return data; }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
            var fallback = AppDataFallbackDirectory;
            try { Directory.CreateDirectory(fallback); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* reported on the first Save */ }
            return fallback;
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, $".smdwin_write_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// One-time migration: if a data file exists in the old location (the temporary extraction folder or %AppData%)
        /// and does NOT exist yet in DataDirectory, it is copied. Existing data is never overwritten.
        /// Returns true if anything was copied.
        /// </summary>
        public static bool MigrateLegacyFile(string relativePath)
        {
            try
            {
                string target = Path.Combine(DataDirectory, relativePath);
                if (File.Exists(target)) return false;
                foreach (var oldRoot in new[] { BundleDirectory, AppDataFallbackDirectory })
                {
                    if (string.IsNullOrEmpty(oldRoot)) continue;
                    string src = Path.Combine(oldRoot, relativePath);
                    if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(src)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(src, target, overwrite: false);
                    AppLogger.Info("AppPaths: date migrate din {0} in {1}", src, target);
                    return true;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppPaths.MigrateLegacyFile " + relativePath); }
            return false;
        }

        // Files and folders that older versions wrote directly next to the exe.
        private static readonly string[] LegacyExeFiles =
        {
            "settings.json", "known_processes.json", "ps_commands.json", "app-notes.json",
            "disk-benchmark-history.json", "tool-activity.json", "test-results.json", "startup-original-state.json",
        };
        private static readonly string[] LegacyExeFolders = { "SelfTest-Results", "unknown-hardware", "Startup-Removed" };

        /// <summary>
        /// Called once at start, before logging and settings: moves into the Data folder what older versions wrote
        /// next to the exe and copies what was in %AppData% / %LocalAppData% (sensor logs, snapshots, SMART history).
        /// Never overwrites anything in Data. Returns the messages for the log (logging is not started yet).
        /// </summary>
        public static System.Collections.Generic.List<string> ConsolidateDataFolder()
        {
            var notes = new System.Collections.Generic.List<string>();
            string data = DataDirectory;
            bool same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (same(data, ExeDirectory)) return notes;

            void MoveFile(string src, string dst)
            {
                try
                {
                    if (!File.Exists(src) || File.Exists(dst)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    try { File.Move(src, dst); notes.Add($"moved {src} -> {dst}"); }
                    catch { File.Copy(src, dst); notes.Add($"copied {src} -> {dst}"); }
                }
                catch (Exception ex) { notes.Add($"could not move {src}: {ex.Message}"); }
            }
            void MergeFolder(string src, string dst, bool move, string pattern = "*")
            {
                try
                {
                    if (!Directory.Exists(src)) return;
                    foreach (var f in Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories))
                    {
                        string target = Path.Combine(dst, Path.GetRelativePath(src, f));
                        if (File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (move) { try { File.Move(f, target); continue; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                        File.Copy(f, target);
                    }
                    if (move)
                    {
                        try { if (!Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories).Any(e => File.Exists(e))) Directory.Delete(src, true); }
                        catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    }
                    notes.Add($"merged {src} -> {dst}");
                }
                catch (Exception ex) { notes.Add($"could not merge {src}: {ex.Message}"); }
            }

            // 1. Next to the exe → Data (move; if the exe is on read-only media, copy only)
            if (data.StartsWith(ExeDirectory, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in LegacyExeFiles) MoveFile(Path.Combine(ExeDirectory, f), Path.Combine(data, f));
                foreach (var d in LegacyExeFolders) MergeFolder(Path.Combine(ExeDirectory, d), Path.Combine(data, d), move: true);

                // 2. %AppData% / %LocalAppData% → Data (copy; the old folders stay as a backup)
                string roaming = AppDataFallbackDirectory;
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMDWin");
                string marker = Path.Combine(data, ".imported-appdata");
                if (!File.Exists(marker))
                {
                    MergeFolder(Path.Combine(roaming, "logs"), Path.Combine(data, "Logs"), move: false, pattern: "sensors-*.csv");
                    MoveFile(Path.Combine(roaming, "monitor_log.csv"), Path.Combine(data, "Logs", "monitor_log.csv"));
                    MergeFolder(Path.Combine(roaming, "snapshots"), Path.Combine(data, "Snapshots"), move: false);
                    MergeFolder(Path.Combine(local, "SmartHistory"), Path.Combine(data, "SmartHistory"), move: false);
                    MoveFile(Path.Combine(local, "optimize-backup.json"), Path.Combine(data, "optimize-backup.json"));
                    try { File.WriteAllText(marker, DateTime.Now.ToString("o")); File.SetAttributes(marker, FileAttributes.Hidden); }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }
            }
            return notes;
        }

        /// <summary>Called once at start, before SettingsService.Load().</summary>
        public static void MigrateLegacyData()
        {
            MigrateLegacyFile("settings.json");
            MigrateLegacyFile("known_processes.json");
            MigrateLegacyFile("ps_commands.json");
            MigrateLegacyFile(Path.Combine("SelfTest-Results", "benchmark-history.json"));
            MigrateLegacyFile(Path.Combine("SelfTest-Results", "app-readiness-history.json"));
            MigrateLegacyFile(Path.Combine("SelfTest-Results", "page-load-history.json"));
        }
    }
}
