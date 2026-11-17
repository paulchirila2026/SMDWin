using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>
    /// Recommendations in Storage › What uses space. "Safe to remove" = temporary caches that Windows and programs rebuild
    /// themselves (deleted directly, as in Auto Clean). "Check first" = user files (old downloads, installers): not ticked
    /// by default and, when chosen, moved to the Recycle Bin — they can be recovered.
    /// </summary>
    public static class SpaceCleanup
    {
        public sealed class Rec
        {
            public string Key = "";
            public bool Safe;
            public string Name = "", Where = "";
            public long Size;
            public int Count;
            /// <summary>Only for "Check first": the exact files moved to the Recycle Bin.</summary>
            public List<string> Files = new();
        }

        private static string S(string key, string en) => LanguageService.S("Space", key, en);
        private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public const int OldDownloadDays = 90;

        // skipped files (locked by an open program or no access): stay in place, counted for the log
        private static int _skipped;

        /// <summary>Browser cache folders (Chrome, Edge, Brave, Firefox), for all profiles.</summary>
        private static IEnumerable<string> BrowserCacheDirs()
        {
            foreach (string baseDir in new[]
            {
                Path.Combine(Local, @"Google\Chrome\User Data"),
                Path.Combine(Local, @"Microsoft\Edge\User Data"),
                Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"),
            })
            {
                if (!Directory.Exists(baseDir)) continue;
                IEnumerable<string> profiles;
                try { profiles = Directory.EnumerateDirectories(baseDir).ToList(); } catch (Exception) { continue; }
                foreach (string prof in profiles)
                    foreach (string sub in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" })
                    {
                        string d = Path.Combine(prof, sub);
                        if (Directory.Exists(d)) yield return d;
                    }
            }
            string ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ff))
            {
                IEnumerable<string> profiles;
                try { profiles = Directory.EnumerateDirectories(ff).ToList(); } catch (Exception) { profiles = Array.Empty<string>(); }
                foreach (string prof in profiles)
                {
                    string d = Path.Combine(prof, "cache2");
                    if (Directory.Exists(d)) yield return d;
                }
            }
        }

        private static string ThumbDir => Path.Combine(Local, @"Microsoft\Windows\Explorer");

        /// <summary>Computes all recommendations (folder reads — call it on a background thread).</summary>
        public static List<Rec> Estimate()
        {
            var list = new List<Rec>();
            void Add(Rec r) { if (r.Size > 0) list.Add(r); }

            Add(new Rec { Key = "Temp", Safe = true, Name = S("RecTemp", "Temporary files"), Where = @"%TEMP%, C:\Windows\Temp",
                Size = FolderSize(Path.GetTempPath()) + FolderSize(Path.Combine(WinDir, "Temp")) });
            Add(new Rec { Key = "WinUpdate", Safe = true, Name = S("RecWinUpdate", "Windows Update cache"), Where = @"SoftwareDistribution\Download",
                Size = FolderSize(Path.Combine(WinDir, @"SoftwareDistribution\Download")) });
            Add(new Rec { Key = "RecycleBin", Safe = true, Name = S("RecRecycle", "Recycle Bin"), Where = S("RecRecycleWhere", "emptied, cannot be undone"),
                Size = RecycleBinSize() });
            Add(new Rec { Key = "Browser", Safe = true, Name = S("RecBrowser", "Browser caches"), Where = "Chrome, Edge, Brave, Firefox",
                Size = BrowserCacheDirs().Sum(d => FolderSize(d)) });
            Add(new Rec { Key = "Thumb", Safe = true, Name = S("RecThumb", "Thumbnail cache"), Where = S("RecThumbWhere", "rebuilt by Windows"),
                Size = FolderSize(ThumbDir, "thumbcache*.db") });

            // downloads: installers separately (any age), the rest only if untouched for 90 days
            var inst = new Rec { Key = "Installers", Name = S("RecInstallers", "Installers in Downloads"), Where = ".exe, .msi, .iso" };
            var old = new Rec { Key = "OldDownloads", Name = S("RecOldDownloads", "Old downloads"), Where = string.Format(S("RecOldWhere", "Downloads, not used in {0}+ days"), OldDownloadDays) };
            try
            {
                if (Directory.Exists(Downloads))
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                    var limit = DateTime.Now.AddDays(-OldDownloadDays);
                    foreach (var f in new DirectoryInfo(Downloads).EnumerateFiles("*", opts))
                    {
                        try
                        {
                            if (SpaceClassifier.IsInstaller(f.Name)) { inst.Size += f.Length; inst.Count++; inst.Files.Add(f.FullName); }
                            else if (f.LastWriteTime < limit && f.LastAccessTime < limit) { old.Size += f.Length; old.Count++; old.Files.Add(f.FullName); }
                        }
                        catch (Exception) { _skipped++; }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug("SpaceCleanup downloads: " + ex.Message); }
            Add(old);
            Add(inst);
            return list;
        }

        /// <summary>Cleans the chosen recommendations. Returns how much was freed (approximate: locked files stay in place).</summary>
        public static long Clean(IEnumerable<Rec> chosen)
        {
            long freed = 0;
            _skipped = 0;
            foreach (var r in chosen)
            {
                try
                {
                    switch (r.Key)
                    {
                        case "Temp":
                            freed += EmptyFolder(Path.GetTempPath()) + EmptyFolder(Path.Combine(WinDir, "Temp")); break;
                        case "WinUpdate":
                            freed += EmptyFolder(Path.Combine(WinDir, @"SoftwareDistribution\Download")); break;
                        case "RecycleBin":
                            long rb = RecycleBinSize();
                            if (SHEmptyRecycleBinW(IntPtr.Zero, null, 0x7) == 0) freed += rb;
                            break;
                        case "Browser":
                            foreach (string d in BrowserCacheDirs()) freed += EmptyFolder(d);
                            break;
                        case "Thumb":
                            freed += DeleteFiles(ThumbDir, "thumbcache*.db"); break;
                        default:
                            foreach (string f in r.Files) freed += ToRecycleBin(f);
                            break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "SpaceCleanup.Clean " + r.Key); }
            }
            ToolActivityLog.Add("Space usage", $"{freed / 1073741824.0:F2} GB freed", "ok");
            AppLogger.Info("SpaceCleanup: {0} → {1:N0} bytes freed, {2} items skipped (in use or protected)", string.Join(", ", chosen.Select(c => c.Key)), freed, _skipped);
            return freed;
        }

        /// <summary>Moves a file to the Recycle Bin. Returns its size if it succeeded.</summary>
        public static long ToRecycleBin(string path)
        {
            try
            {
                long len = new FileInfo(path).Length;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return File.Exists(path) ? 0 : len;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceCleanup.ToRecycleBin " + path); return 0; }
        }

        public static long FolderSize(string path, string pattern = "*")
        {
            try
            {
                if (!Directory.Exists(path)) return 0;
                var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                long sum = 0;
                foreach (var f in new DirectoryInfo(path).EnumerateFiles(pattern, opts))
                    try { sum += f.Length; } catch (Exception) { _skipped++; }
                return sum;
            }
            catch (Exception ex) { AppLogger.Debug("SpaceCleanup.FolderSize: " + ex.Message); return 0; }
        }

        /// <summary>Empties a temporary cache folder: locked (in-use) files stay, links are not followed, and the app's own
        /// folder (%TEMP%\SMDWin — the "bundle" tools) is not touched.</summary>
        private static long EmptyFolder(string path)
        {
            long freed = 0;
            string own = Path.Combine(Path.GetTempPath(), "SMDWin").TrimEnd('\\');
            void Walk(string dir)
            {
                if (dir.TrimEnd('\\').Equals(own, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        try { var fi = new FileInfo(f); long len = fi.Length; fi.Delete(); freed += len; } catch (Exception) { _skipped++; }
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            var di = new DirectoryInfo(d);
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { di.Delete(false); continue; }
                            Walk(d);
                            if (!d.TrimEnd('\\').Equals(own, StringComparison.OrdinalIgnoreCase)) di.Delete(false);
                        }
                        catch (Exception) { _skipped++; }
                    }
                }
                catch (Exception ex) { AppLogger.Debug("SpaceCleanup.EmptyFolder " + dir + ": " + ex.Message); }
            }
            if (Directory.Exists(path)) Walk(path);
            return freed;
        }

        private static long DeleteFiles(string path, string pattern)
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(path)) return 0;
                foreach (var f in Directory.EnumerateFiles(path, pattern))
                    try { var fi = new FileInfo(f); long len = fi.Length; fi.Delete(); freed += len; } catch (Exception) { _skipped++; }
            }
            catch (Exception ex) { AppLogger.Debug("SpaceCleanup.DeleteFiles: " + ex.Message); }
            return freed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO info);

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        public static long RecycleBinSize()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                return SHQueryRecycleBinW(null, ref info) == 0 ? info.i64Size : 0;
            }
            catch (Exception ex) { AppLogger.Debug("SpaceCleanup.RecycleBinSize: " + ex.Message); return 0; }
        }
    }
}
