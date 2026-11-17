using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    // ── Models ────────────────────────────────────────────────────────────────

    public class DiskSizeEntry
    {
        /// <summary>Display name (file name or folder name)</summary>
        public string Name        { get; set; } = "";
        /// <summary>Full path</summary>
        public string FullPath    { get; set; } = "";
        /// <summary>Size in bytes</summary>
        public long   Bytes       { get; set; }
        /// <summary>Human-readable size string ("1.23 GB")</summary>
        public string SizeDisplay { get; set; } = "";
        /// <summary>0.0 – 1.0 relative to the largest entry in the set (for bar rendering)</summary>
        public double BarFraction { get; set; }
        /// <summary>
        /// Gradient color string based on BarFraction.
        /// 0.0 = green (#22C55E), 1.0 = red (#EF4444), interpolated through yellow (#EAB308).
        /// </summary>
        public string BarColor    { get; set; } = "#22C55E";
        /// <summary>True if this entry is a directory.</summary>
        public bool   IsDirectory { get; set; }
        /// <summary>Number of files inside (folders only).</summary>
        public int    FileCount   { get; set; }
    }

    public class HibernateStatus
    {
        public bool   IsEnabled     { get; set; }
        public long   FileBytes     { get; set; }
        public string FileSizeText  { get; set; } = "";
        public string HibFilePath   { get; set; } = @"C:\hiberfil.sys";
    }

    // ── Service ───────────────────────────────────────────────────────────────

    public class DiskAnalysisService
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        private static long GetActualDiskSize(string path, long logicalSize)
        {
            try
            {
                uint high;
                uint low = GetCompressedFileSizeW(path, out high);
                if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
                    return logicalSize; // API failed — use the logical size, better than nothing
                long actual = ((long)high << 32) | low;
                // For normal files (not sparse, not compressed), actual == logicalSize — nothing changes here.
                // Only when actual < logicalSize (sparse/compressed) is the real, smaller value reported.
                return actual > 0 ? actual : logicalSize;
            }
            catch { return logicalSize; }
        }

        // ── Top Folders ───────────────────────────────────────────────────────


        private static void CalcDirSize(string dir, ref long total, ref int fileCount,
            CancellationToken ct, int maxDepth)
        {
            if (maxDepth <= 0 || ct.IsCancellationRequested) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (ct.IsCancellationRequested) return;
                    try { var fi = new FileInfo(f); total += GetActualDiskSize(f, fi.Length); fileCount++; }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService.CalcDirSize"); }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (ct.IsCancellationRequested) return;
                    try { CalcDirSize(sub, ref total, ref fileCount, ct, maxDepth - 1); }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
        }

        // ── Shared helpers ────────────────────────────────────────────────────


        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "—";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
