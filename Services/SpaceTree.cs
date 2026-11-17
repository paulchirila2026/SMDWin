using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>Categories in Storage › What uses space (colour of the map blocks and the legend).</summary>
    public enum SpaceKind { Media, Downloads, Programs, Games, Windows, Safe, Archives, Images, Other }

    /// <summary>A large file (big enough to show as a separate block in the map).</summary>
    public sealed class SpaceFile
    {
        public string Name = "";
        public SpaceDir? Parent;
        public long Size;
        public DateTime Modified;
        /// <summary>Windows files (pagefile, hiberfil, swapfile): shown, but cannot be moved.</summary>
        public bool IsSystem;
        public string Directory => Parent?.Path ?? "";
        public string FullPath => Path.Combine(Directory, Name);
        public SpaceKind Kind => SpaceClassifier.FileKind(this);
    }

    /// <summary>A folder: total size (with subfolders), its subfolders and its large files.</summary>
    public sealed class SpaceDir
    {
        public string Name = "";
        public SpaceDir? Parent;
        public long Size;               // total, with everything it contains
        public long OwnSize;            // only the files directly in it
        public int Files;               // files, with everything it contains
        public int OwnFiles;
        public readonly List<SpaceDir> Dirs = new();
        public readonly List<SpaceFile> BigFiles = new();
        private string? _path;
        internal string? RootPath;      // the path of the scanned root ("C:\")

        public string Path
        {
            get
            {
                if (_path != null) return _path;
                if (Parent == null) return _path = RootPath ?? Name;
                string p = Parent.Path;
                return _path = p.EndsWith('\\') ? p + Name : p + "\\" + Name;
            }
        }

        public SpaceKind Kind => SpaceClassifier.FolderKind(Path);

        /// <summary>Size of the files in this folder that do not show as separate blocks ("other files").</summary>
        public long SmallFilesSize => Math.Max(0, OwnSize - BigFiles.Sum(f => f.Size));

        /// <summary>The totals, bottom up (after all files and folders are in place).</summary>
        internal static void ComputeTotals(SpaceDir root, CancellationToken ct = default)
        {
            var order = new List<SpaceDir>();
            var stack = new Stack<SpaceDir>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                order.Add(d);
                foreach (var c in d.Dirs) stack.Push(c);
            }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                var d = order[i];
                d.Size = d.OwnSize; d.Files = d.OwnFiles;
                foreach (var c in d.Dirs) { d.Size += c.Size; d.Files += c.Files; }
                d.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
                d.BigFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
        }

        /// <summary>Subtracts a deleted / moved file from the sizes of the folders above it.</summary>
        public void RemoveFile(SpaceFile f)
        {
            if (!BigFiles.Remove(f)) return;
            OwnSize -= f.Size; OwnFiles--;
            for (var d = this; d != null; d = d.Parent) { d.Size -= f.Size; d.Files--; }
        }

        public IEnumerable<SpaceFile> AllBigFiles()
        {
            var stack = new Stack<SpaceDir>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                foreach (var f in d.BigFiles) yield return f;
                foreach (var c in d.Dirs) stack.Push(c);
            }
        }
    }

    /// <summary>Result of a scan: the root, how long it took and whether the NTFS index was used.</summary>
    public sealed class SpaceScan
    {
        public SpaceDir Root = new();
        public double Seconds;
        public bool Fast;
        public long DriveTotal, DriveFree;
        public DateTime When = DateTime.Now;
    }

    public static class SpaceClassifier
    {
        private static readonly string[] MediaExt = { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".mts", ".m2ts", ".webm", ".flv",
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".jpg", ".jpeg", ".png", ".heic", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd", ".tif", ".tiff" };
        private static readonly string[] ImageExt = { ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".wim", ".esd", ".exe", ".msi", ".msix", ".appx", ".dmg" };
        private static readonly string[] ArchiveExt = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".bak" };
        private static readonly string[] GameMarks = { "\\steamapps\\", "\\steam\\", "\\epic games\\", "\\xboxgames\\", "\\riot games\\", "\\games\\", "\\gog galaxy\\", "\\ubisoft\\", "\\ea games\\", "\\battle.net\\" };

        public static bool IsSafeFolder(string pathLower)
            => pathLower.Contains("\\appdata\\local\\temp") || pathLower.EndsWith("\\windows\\temp") || pathLower.Contains("\\windows\\temp\\")
            || pathLower.Contains("\\softwaredistribution\\download")
            || pathLower.EndsWith("\\cache") || pathLower.Contains("\\cache\\cache_data") || pathLower.EndsWith("\\code cache") || pathLower.Contains("\\cache2")
            || pathLower.Contains("\\$recycle.bin");

        public static SpaceKind FolderKind(string path)
        {
            string p = (path ?? "").ToLowerInvariant() + "\\";
            string t = p.TrimEnd('\\');
            if (IsSafeFolder(t)) return SpaceKind.Safe;
            if (GameMarks.Any(p.Contains)) return SpaceKind.Games;
            if (p.Contains("\\downloads\\")) return SpaceKind.Downloads;
            if (p.Contains("\\videos\\") || p.Contains("\\pictures\\") || p.Contains("\\music\\") || p.Contains("\\documents\\")
                || p.Contains("\\desktop\\") || p.Contains("\\onedrive") || p.Contains("\\dcim\\")) return SpaceKind.Media;
            if (p.Contains("\\program files") || p.Contains("\\programdata\\") || p.Contains("\\appdata\\")) return SpaceKind.Programs;
            if (p.Length >= 3 && p.Substring(1).StartsWith(":\\windows\\")) return SpaceKind.Windows;
            return SpaceKind.Other;
        }

        public static SpaceKind FileKind(SpaceFile f)
        {
            if (f.IsSystem) return SpaceKind.Windows;
            string ext = Path.GetExtension(f.Name).ToLowerInvariant();
            string dir = f.Directory.ToLowerInvariant() + "\\";
            if (GameMarks.Any(dir.Contains)) return SpaceKind.Games;
            if (MediaExt.Contains(ext)) return SpaceKind.Media;
            if (ImageExt.Contains(ext)) return SpaceKind.Images;
            if (ArchiveExt.Contains(ext)) return SpaceKind.Archives;
            var fk = FolderKind(f.Directory);
            return fk is SpaceKind.Safe or SpaceKind.Windows or SpaceKind.Programs ? fk : SpaceKind.Other;
        }

        public static bool IsInstaller(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext is ".exe" or ".msi" or ".msix" or ".iso" or ".img";
        }
    }

    /// <summary>Disk scan for the map: the NTFS index when possible (seconds), otherwise folder by folder.</summary>
    public static class SpaceScanner
    {
        /// <summary>Files from this size up show as separate blocks (and in the list of the largest files).</summary>
        public const long BigFileBytes = 16L * 1024 * 1024;

        /// <summary>Below this much free memory (MB) the NTFS index is not used: it keeps in memory a few lists as long as the
        /// number of files on the disk (tens to hundreds of MB), and on a PC without free memory Windows starts paging and
        /// everything freezes (the window can stay unresponsive for tens of seconds). The folder-by-folder scan needs little.</summary>
        public const long LowMemoryMB = 1024;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
        private const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;

        /// <summary>
        /// Scan on a separate thread, in the Windows "background mode": low processor, disk and memory priority.
        /// On an idle PC it is just as fast; on a busy one it lets Windows and the app window work first.
        /// If stopped, the memory it used is released immediately (not at the next .NET collection).
        /// </summary>
        public static Task<SpaceScan> ScanInBackgroundAsync(string rootPath, bool lowMemory, CancellationToken ct, IProgress<string>? progress)
        {
            var tcs = new TaskCompletionSource<SpaceScan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                bool background = false;
                try { background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin); }
                catch (Exception ex) { AppLogger.Debug("SpaceScanner background mode: " + ex.Message); }
                try { tcs.TrySetResult(Scan(rootPath, ct, progress, lowMemory)); }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(ct);
                    try { GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); }
                    catch (Exception ex) { AppLogger.Debug("SpaceScanner GC: " + ex.Message); }
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
                finally
                {
                    if (background)
                        try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); } catch (Exception) { }
                }
            })
            { IsBackground = true, Name = "SpaceScan", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            return tcs.Task;
        }

        public static SpaceScan Scan(string rootPath, CancellationToken ct, IProgress<string>? progress, bool lowMemory = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string root = Path.GetFullPath(rootPath);
            if (!root.EndsWith('\\')) root += "\\";
            var scan = new SpaceScan();
            try
            {
                var di = new DriveInfo(root);
                if (di.IsReady) { scan.DriveTotal = di.TotalSize; scan.DriveFree = di.AvailableFreeSpace; }
            }
            catch (Exception ex) { AppLogger.Debug("SpaceScanner drive info: " + ex.Message); }

            SpaceDir? tree = null;
            if (lowMemory)
                AppLogger.Info("SpaceScanner: fast scan not used for {0} — little free memory ({1:N0} MB)", root, RamFullScanService.GetAvailablePhysicalMB());
            else try
            {
                var p = new Progress<double>(v => progress?.Report(string.Format(LanguageService.S("Space", "ReadingIndex", "Reading the drive's file index… {0:P0}"), v)));
                if (MftScanner.TryScan(root, BigFileBytes, ct, p, out var index, out string reason) && index != null)
                {
                    int r = index.FindDirectory(root);
                    if (r >= 0) tree = index.BuildSpaceTree(r, root, BigFileBytes, ct);
                    else AppLogger.Info("SpaceScanner: {0} not found in the drive index — standard scan", root);
                }
                else AppLogger.Info("SpaceScanner: fast scan not used for {0} — {1}", root, reason);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceScanner fast"); }

            ct.ThrowIfCancellationRequested();
            if (tree != null && !LooksRight(tree)) { AppLogger.Warning("SpaceScanner: fast scan result did not match the disk — standard scan"); tree = null; }
            scan.Fast = tree != null;
            tree ??= WalkScan(root, ct, progress);
            ct.ThrowIfCancellationRequested();
            AddSystemFiles(tree, root);
            SpaceDir.ComputeTotals(tree, ct);
            scan.Root = tree;
            scan.Seconds = sw.Elapsed.TotalSeconds;
            AppLogger.Info("SpaceScanner: {0} — {1:F1} GB in {2:N0} files, {3:F1} s ({4})", root, tree.Size / 1073741824.0, tree.Files, scan.Seconds, scan.Fast ? "NTFS index" : "folder by folder");
            return scan;
        }

        /// <summary>Check: the few largest files in the index must exist on disk, with the same size.</summary>
        private static bool LooksRight(SpaceDir tree)
        {
            int ok = 0, wrong = 0;
            foreach (var f in tree.AllBigFiles().OrderByDescending(x => x.Size).Take(6))
            {
                try
                {
                    var info = new FileInfo(f.FullPath);
                    if (!info.Exists) { wrong++; continue; }
                    if (Math.Abs(info.Length - f.Size) <= Math.Max(1L << 20, f.Size / 10)) ok++; else wrong++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            }
            return wrong <= 1;
        }

        /// <summary>The normal scan (other file systems, external disks): folder by folder, without links and without "System" files.</summary>
        private static SpaceDir WalkScan(string root, CancellationToken ct, IProgress<string>? progress)
        {
            var top = new SpaceDir { Name = root, RootPath = root };
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            var stack = new Stack<(DirectoryInfo Info, SpaceDir Node)>();
            stack.Push((new DirectoryInfo(root), top));
            long files = 0; var last = DateTime.UtcNow;
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (info, node) = stack.Pop();
                try
                {
                    foreach (var e in info.EnumerateFileSystemInfos("*", opts))
                    {
                        if (e is DirectoryInfo d)
                        {
                            var child = new SpaceDir { Name = d.Name, Parent = node };
                            node.Dirs.Add(child);
                            stack.Push((d, child));
                        }
                        else if (e is FileInfo f)
                        {
                            long len;
                            try { len = f.Length; } catch (Exception) { continue; }
                            node.OwnSize += len; node.OwnFiles++; files++;
                            if (len >= BigFileBytes) node.BigFiles.Add(new SpaceFile { Name = f.Name, Parent = node, Size = len, Modified = f.LastWriteTime });
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
                if ((DateTime.UtcNow - last).TotalMilliseconds > 400)
                {
                    last = DateTime.UtcNow;
                    progress?.Report(string.Format(LanguageService.S("Space", "ScannedFiles", "Scanned {0:N0} files…"), files));
                }
            }
            return top;
        }

        /// <summary>pagefile / hiberfil / swapfile in the disk root: large, but "System", so skipped by the scan.
        /// Added as files of the root, marked as not movable.</summary>
        private static void AddSystemFiles(SpaceDir tree, string root)
        {
            if (tree.Parent != null || root.TrimEnd('\\').Length > 2) return;   // only when the whole disk was scanned
            try
            {
                var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
                foreach (string n in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles(n, opts))
                    {
                        if (tree.BigFiles.Any(x => x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        tree.BigFiles.Add(new SpaceFile { Name = f.Name, Parent = tree, Size = f.Length, Modified = f.LastWriteTime, IsSystem = true });
                        tree.OwnSize += f.Length; tree.OwnFiles++;
                    }
            }
            catch (Exception ex) { AppLogger.Debug("SpaceScanner system files: " + ex.Message); }
        }
    }
}
