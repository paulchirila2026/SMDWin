using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// Many programs do not declare their size to Windows (no "EstimatedSize" in their uninstall entry), and driver packages
    /// never do. For those, the size is measured from the files: the program folder (InstallLocation) or, for a driver package,
    /// its folder in the Windows driver store (taken from its uninstall command). The result is marked as estimated.
    ///
    /// Measuring is done in the background and remembered in %LocalAppData%\SMDWin\app-sizes.json per folder, together with the
    /// folder's last-change time, so a folder is measured again only when it changed. Folders that would give a wrong figure
    /// (a drive root, the Windows folder, "Program Files" itself, a user profile) are skipped.
    /// </summary>
    public static class AppSizeEstimator
    {
        private sealed record Entry(long Bytes, long Stamp);

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMDWin", "app-sizes.json");

        private static readonly object Gate = new();
        private static Dictionary<string, Entry>? s_cache;

        /// <summary>
        /// The driver-store folder of a driver package, read from its uninstall command
        /// ("…dpinst.exe /u C:\Windows\System32\DriverStore\FileRepository\x.inf_amd64_…\x.inf"); "" when there is none.
        /// </summary>
        public static string DriverFolderFrom(string? uninstall)
        {
            if (string.IsNullOrWhiteSpace(uninstall)) return "";
            int i = uninstall.IndexOf(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            // walk back to the start of the path (a drive letter or the opening quote)
            int start = uninstall.LastIndexOfAny(new[] { ' ', '"' }, i) + 1;
            string path = uninstall.Substring(start).Trim().Trim('"');
            int end = path.IndexOf(".inf", StringComparison.OrdinalIgnoreCase);
            if (end > 0) path = path.Substring(0, end + 4);
            try { return Path.GetDirectoryName(path) ?? ""; } catch { return ""; }
        }

        /// <summary>Fills in SizeBytes / Size for the programs without a size. Returns how many got a size.</summary>
        public static Task<int> EstimateAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default) => Task.Run(() =>
        {
            int filled = 0;
            var cache = LoadCache();
            bool changed = false;
            foreach (var a in apps)
            {
                if (ct.IsCancellationRequested) break;
                if (a.SizeBytes > 0) continue;
                string dir = a.IsDriver ? a.DriverFolder : a.InstallLocation;
                if (!Usable(dir)) continue;
                try
                {
                    var di = new DirectoryInfo(dir);
                    long stamp = di.LastWriteTimeUtc.Ticks;
                    string key = di.FullName.TrimEnd('\\').ToLowerInvariant();
                    long bytes;
                    if (cache.TryGetValue(key, out var e) && e.Stamp == stamp) bytes = e.Bytes;
                    else
                    {
                        bytes = FolderSize(di, ct);
                        if (bytes < 0) continue;   // too big / cancelled: leave it without a size
                        cache[key] = new Entry(bytes, stamp);
                        changed = true;
                    }
                    if (bytes <= 0) continue;
                    a.SizeBytes = bytes;
                    a.Size = FormatSize(bytes);
                    a.SizeEstimated = true;
                    filled++;
                }
                catch (Exception ex) { AppLogger.Debug($"AppSizeEstimator {a.Name}: {ex.Message}"); }
            }
            if (changed) SaveCache(cache);
            return filled;
        }, ct);

        /// <summary>A folder that measures one program: exists, and is not a drive root, the Windows folder, "Program Files"
        /// itself or a whole user profile (the driver store folders of drivers are allowed).</summary>
        private static bool Usable(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) return false;
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                if (full.Length <= 3) return false;   // "C:"
                if (full.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase)) return true;
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
                if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                           Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.UserProfile,
                                           Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                                           Environment.SpecialFolder.CommonApplicationData })
                {
                    string p = Environment.GetFolderPath(sf).TrimEnd('\\');
                    if (p.Length > 0 && full.Equals(p, StringComparison.OrdinalIgnoreCase)) return false;
                }
                // "C:\Program Files (x86)\Common Files" and similar shared folders
                if (full.EndsWith(@"\Common Files", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>The total size of the files in a folder (without following links). -1 when it holds more than 300 000
        /// files or takes over 20 s (then it is not one program's folder, or the disk is too slow — no figure is better).</summary>
        private static long FolderSize(DirectoryInfo root, CancellationToken ct)
        {
            long total = 0; int files = 0;
            var started = Environment.TickCount64;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested || Environment.TickCount64 - started > 20_000) return -1;
                var d = stack.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles("*", opts)) { total += f.Length; if (++files > 300_000) return -1; }
                    foreach (var sub in d.EnumerateDirectories("*", opts)) stack.Push(sub);
                }
                catch (Exception ex) { AppLogger.Debug($"AppSizeEstimator folder: {ex.Message}"); }
            }
            return total;
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
            _           => $"{Math.Max(1, bytes / 1024)} KB",
        };

        private static Dictionary<string, Entry> LoadCache()
        {
            lock (Gate)
            {
                if (s_cache != null) return s_cache;
                try
                {
                    if (File.Exists(CachePath))
                        s_cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath));
                }
                catch (Exception ex) { AppLogger.Debug($"AppSizeEstimator cache: {ex.Message}"); }
                return s_cache ??= new Dictionary<string, Entry>();
            }
        }

        private static void SaveCache(Dictionary<string, Entry> cache)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
                }
                catch (Exception ex) { AppLogger.Debug($"AppSizeEstimator save: {ex.Message}"); }
            }
        }
    }
}
