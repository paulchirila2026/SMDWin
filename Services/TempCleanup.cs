using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// The one place that decides what a temp clean-up may delete (Tools → Clear temporary files and Auto-Clean → Temp).
    /// Every rule here is there so that a clean-up can never hurt a PC:
    ///  • only two fixed folders: %LOCALAPPDATA%\Temp and %WINDIR%\Temp — NOT the TEMP/TMP variables, which some PCs point at
    ///    C:\ or a data folder (a clean-up there would delete real files);
    ///  • each folder is checked: it exists, it is not a link, it is really named "Temp" and sits where Windows puts it;
    ///  • links (junctions / symlinks) inside are never followed — only real files under the folder are touched;
    ///  • files waiting for the next restart (PendingFileRenameOperations: an update or installer finishing at reboot) are kept;
    ///  • files marked System, and SMD Win's own folder (its bundled tools), are kept;
    ///  • by default only files older than 7 days (written AND created before), like Windows' own Disk Cleanup;
    ///  • files in use cannot be deleted and are simply skipped; folders are removed only when left empty and old.
    /// </summary>
    public static class TempCleanup
    {
        public const int DefaultAgeDays = 7;

        /// <summary>The temp folders that may be cleaned, each already checked. Never more than these two.</summary>
        public static List<string> SafeRoots()
        {
            var list = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (local.Length > 0 && IsSafeRoot(Path.Combine(local, "Temp"), local)) list.Add(Path.Combine(local, "Temp"));
            if (win.Length > 0 && IsSafeRoot(Path.Combine(win, "Temp"), win)) list.Add(Path.Combine(win, "Temp"));
            return list;
        }

        /// <summary>The folder exists, is a real folder (not a link), is named "Temp" and sits directly in its expected parent.</summary>
        public static bool IsSafeRoot(string path, string expectedParent)
        {
            try
            {
                var d = new DirectoryInfo(path);
                if (!d.Exists) return false;
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (!string.Equals(d.Name, "Temp", StringComparison.OrdinalIgnoreCase)) return false;
                if (d.Parent == null || !string.Equals(d.Parent.FullName.TrimEnd('\\'), Path.GetFullPath(expectedParent).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                if (d.Parent.Parent == null) return false;   // never a "Temp" right under a drive root reached this way
                return true;
            }
            catch { return false; }
        }

        /// <summary>SMD Win's own folders in Temp (the bundled smartctl and other tools) — never touched.</summary>
        public static List<string> OwnFolders()
        {
            var l = new List<string>();
            try { l.Add(Path.Combine(Path.GetTempPath(), "SMDWin").TrimEnd('\\')); } catch { }
            try { l.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SMDWin").TrimEnd('\\')); } catch { }
            return l;
        }

        /// <summary>The files Windows will move or delete at the next restart (an update or installer that is not finished yet).</summary>
        public static HashSet<string> PendingRenameFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
                if (k?.GetValue("PendingFileRenameOperations") is string[] ops)
                    foreach (var raw in ops)
                    {
                        string p = raw.Trim().TrimStart('!');
                        if (p.StartsWith(@"\??\")) p = p[4..];
                        if (p.Length > 3) set.Add(p);
                    }
            }
            catch { }
            return set;
        }

        /// <summary>Windows is waiting for a restart (an update or a driver is half installed).</summary>
        public static bool RebootPending()
        {
            try
            {
                using (var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) if (a != null) return true;
                using (var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) if (b != null) return true;
            }
            catch { }
            return PendingRenameFiles().Count > 0;
        }

        public sealed class Rules
        {
            public DateTime? Cutoff;                       // null = any age
            public List<string> Own = OwnFolders();
            public HashSet<string> Pending = PendingRenameFiles();
        }

        public static Rules MakeRules(bool olderOnly) => new() { Cutoff = olderOnly ? DateTime.Now.AddDays(-DefaultAgeDays) : null };

        private static bool UnderAny(string path, List<string> folders) =>
            folders.Any(o => path.Equals(o, StringComparison.OrdinalIgnoreCase) || path.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase));

        public static bool MayDelete(FileInfo f, Rules r)
        {
            try
            {
                var a = f.Attributes;
                if ((a & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return false;
                if (UnderAny(f.FullName, r.Own)) return false;
                if (r.Pending.Contains(f.FullName)) return false;
                if (r.Cutoff is DateTime c && (f.LastWriteTime >= c || f.CreationTime >= c)) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>All files under the folder, without following links into other places.</summary>
        public static IEnumerable<FileInfo> Files(string root)
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try { return new DirectoryInfo(root).EnumerateFiles("*", opt); } catch { return Array.Empty<FileInfo>(); }
        }

        /// <summary>Deletes what the rules allow under one checked root. Returns (bytes freed, files deleted, files skipped).</summary>
        public static (long Freed, int Deleted, int Skipped) Clean(string root, Rules r)
        {
            long freed = 0; int del = 0, skip = 0;
            if (!SafeRoots().Any(s => s.Equals(root, StringComparison.OrdinalIgnoreCase))) return (0, 0, 0);   // only a checked root
            foreach (var f in Files(root).ToList())
            {
                if (!MayDelete(f, r)) continue;
                bool wasReadOnly = false;
                try
                {
                    long len = f.Length;
                    if (f.IsReadOnly) { wasReadOnly = true; f.IsReadOnly = false; }
                    f.Delete(); freed += len; del++;
                }
                catch
                {
                    skip++;   // in use or protected: left alone
                    if (wasReadOnly) try { f.IsReadOnly = true; } catch { }
                }
            }
            // the folders left empty (deepest first), only if old too; never the root, links, SMD Win's folder or pending ones
            try
            {
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                foreach (var d in new DirectoryInfo(root).EnumerateDirectories("*", opt).OrderByDescending(x => x.FullName.Length).ToList())
                {
                    try
                    {
                        if (UnderAny(d.FullName, r.Own) || r.Pending.Any(p => p.StartsWith(d.FullName + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                        if (r.Cutoff is DateTime c && (d.CreationTime >= c || d.LastWriteTime >= c)) continue;
                        if (!d.EnumerateFileSystemInfos().Any()) d.Delete(false);
                    }
                    catch { }
                }
            }
            catch { }
            return (freed, del, skip);
        }
    }
}
