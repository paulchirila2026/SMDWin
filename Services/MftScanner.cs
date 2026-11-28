using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SMDWin.Services
{
    /// <summary>
    /// Fast scan of an NTFS disk: instead of asking the system for the listing of every folder (slow — millions of
    /// requests), it reads the MFT directly, the index in which NTFS keeps the name, folder and size of every file.
    /// A disk with a million files is read in a few seconds instead of minutes.
    /// Needs administrator rights and works only on local NTFS volumes; in any other case it returns false and the caller
    /// uses the normal scan. Read-only — it never writes to the disk.
    /// </summary>
    public static class MftScanner
    {
        private const uint GenericRead = 0x80000000, ShareAll = 0x7, OpenExisting = 3;
        private const uint FsctlGetNtfsVolumeData = 0x00090064;
        private const int ChunkBytes = 4 * 1024 * 1024;
        private const long MaxRecords = 60_000_000;      // above this the index would need too much memory
        private const long BytesPerRecordInMemory = 80;  // the index lists (about 30 B) + folder names + the folder tree built from it
        private const long BaseMemoryMB = 48;            // read buffer, result tree and working space

        /// <summary>
        /// Whether the index of a volume with <paramref name="recordCount"/> records can be built without pushing the PC into paging:
        /// the estimated size, plus half of it again and a fixed reserve, must be free. The decision rests on the real size of the
        /// index (a 100 GB system disk needs about 60 MB), not on a fixed amount of free memory.
        /// </summary>
        /// <param name="availableMB">Free physical memory; 0 or less = unknown, no limit applied.</param>
        internal static bool IndexFitsInMemory(long recordCount, long availableMB, out long estimatedMB)
        {
            estimatedMB = recordCount * BytesPerRecordInMemory / (1024 * 1024) + BaseMemoryMB;
            if (availableMB <= 0) return true;
            return availableMB >= estimatedMB + estimatedMB / 2 + 256;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                         uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr inBuffer, uint inSize,
                                                   byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long newPosition, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

        /// <summary>
        /// Reads the index of the volume that holds <paramref name="path"/>.
        /// </summary>
        /// <param name="keepFileNamesFrom">Size from which file names are kept (see <see cref="MftIndex"/>).</param>
        /// <param name="progress">How much has been read, 0–1.</param>
        /// <param name="reason">Why it failed (for the log), when the result is false.</param>
        /// <param name="availableMB">Free physical memory (0 = unknown): the index is not built when it would not fit.</param>
        public static bool TryScan(string path, long keepFileNamesFrom, CancellationToken ct, IProgress<double>? progress,
                                   out MftIndex? index, out string reason, long availableMB = 0)
            => TryScan(path, keepFileNamesFrom, ct, progress, out index, out reason, out _, availableMB);

        /// <param name="tooLittleMemory">True when the only reason for returning false is the free memory.</param>
        internal static bool TryScan(string path, long keepFileNamesFrom, CancellationToken ct, IProgress<double>? progress,
                                     out MftIndex? index, out string reason, out bool tooLittleMemory, long availableMB)
        {
            index = null;
            tooLittleMemory = false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') { reason = "not a local drive letter"; return false; }
                var drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) { reason = $"drive type {drive.DriveType}"; return false; }
                if (!drive.IsReady) { reason = "drive not ready"; return false; }
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) { reason = $"file system {drive.DriveFormat}"; return false; }

                using var volume = CreateFileW(@"\\.\" + root.Substring(0, 2), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (volume.IsInvalid) { reason = $"cannot open the volume (error {Marshal.GetLastWin32Error()}; needs administrator rights)"; return false; }

                var info = new byte[96];
                if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, info, (uint)info.Length, out uint got, IntPtr.Zero) || got < 72)
                { reason = $"volume data not available (error {Marshal.GetLastWin32Error()})"; return false; }

                int bytesPerSector = BitConverter.ToInt32(info, 40);
                int bytesPerCluster = BitConverter.ToInt32(info, 44);
                int recordSize = BitConverter.ToInt32(info, 48);
                long mftBytes = BitConverter.ToInt64(info, 56);
                long mftStartLcn = BitConverter.ToInt64(info, 64);
                if (bytesPerSector < 512 || bytesPerCluster < bytesPerSector || recordSize < 512 || recordSize > 65536
                    || recordSize % 512 != 0 || mftBytes < recordSize * 16L || mftStartLcn <= 0)
                { reason = "unexpected volume geometry"; return false; }

                long recordCount = mftBytes / recordSize;
                if (recordCount > MaxRecords) { reason = $"index too large ({recordCount:N0} records)"; return false; }
                if (!IndexFitsInMemory(recordCount, availableMB, out long estimatedMB))
                { reason = $"the index needs about {estimatedMB:N0} MB and only {availableMB:N0} MB are free ({recordCount:N0} records)"; tooLittleMemory = true; return false; }
                long totalBytes = recordCount * recordSize;

                // record 0 describes the MFT itself: where its pieces are on disk
                var first = new byte[Align(recordSize, bytesPerSector)];
                if (!ReadAt(volume, mftStartLcn * bytesPerCluster, first, first.Length)) { reason = $"cannot read the index (error {Marshal.GetLastWin32Error()})"; return false; }
                if (!NtfsRecordParser.TryParse(first.AsSpan(0, recordSize), out var mft) || mft.RunsLength == 0 || mft.DataStartVcn != 0)
                { reason = "index header not readable"; return false; }

                var runs = new List<(long Vcn, long Lcn, long Clusters)>();
                long covered = 0;        // how many clusters of the table we know the location of
                void AddRuns(List<(long Lcn, long Clusters)> more)
                {
                    foreach (var (lcn, clusters) in more) { runs.Add((covered, lcn, clusters)); covered += clusters; }
                }
                AddRuns(NtfsRecordParser.ParseRuns(first.AsSpan(mft.RunsOffset, mft.RunsLength)));
                if (covered == 0) { reason = "index location not readable"; return false; }
                // a heavily fragmented table continues its list of pieces in other records; those that arrive before their turn wait here
                var pending = new Dictionary<long, List<(long Lcn, long Clusters)>>();

                var result = new MftIndex(root, recordCount, keepFileNamesFrom);
                var buffer = new byte[Math.Max(ChunkBytes, Align(recordSize, bytesPerCluster))];
                int chunk = buffer.Length / recordSize * recordSize;
                var watch = Stopwatch.StartNew();
                int run = 0;

                for (long pos = 0; pos < totalBytes; )
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(chunk, totalBytes - pos);

                    // the requested piece may cross the boundary between two fragments of the table
                    int filled = 0;
                    while (filled < want)
                    {
                        long at = pos + filled;
                        while (run < runs.Count && at >= (runs[run].Vcn + runs[run].Clusters) * bytesPerCluster) run++;
                        if (run >= runs.Count) break;
                        long inRun = at - runs[run].Vcn * bytesPerCluster;
                        int piece = (int)Math.Min(want - filled, runs[run].Clusters * bytesPerCluster - inRun);
                        long diskOffset = runs[run].Lcn * bytesPerCluster + inRun;
                        if (!ReadAt(volume, diskOffset, buffer, piece, filled))
                        { reason = $"read error in the index (error {Marshal.GetLastWin32Error()})"; return false; }
                        filled += piece;
                    }
                    if (filled < recordSize) { reason = "index is fragmented beyond what could be followed"; return false; }

                    int records = filled / recordSize;
                    long firstRecord = pos / recordSize;
                    for (int i = 0; i < records; i++)
                    {
                        var span = buffer.AsSpan(i * recordSize, recordSize);
                        if (!NtfsRecordParser.TryParse(span, out var rec)) continue;
                        result.Add(firstRecord + i, rec, span);

                        if (rec.IsContinuation && rec.BaseRecord == 0 && rec.RunsLength > 0 && rec.InUse)
                        {
                            pending[rec.DataStartVcn] = NtfsRecordParser.ParseRuns(span.Slice(rec.RunsOffset, rec.RunsLength));
                            while (pending.Remove(covered, out var next)) AddRuns(next);
                        }
                    }
                    pos += (long)records * recordSize;
                    progress?.Report(Math.Min(1.0, pos / (double)totalBytes));
                }

                AppLogger.Info($"MftScanner: {root} — {recordCount:N0} records, {totalBytes / 1048576.0:F0} MB index, {runs.Count} fragment(s), {watch.Elapsed.TotalSeconds:F1} s");
                index = result;
                reason = "";
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "MftScanner.TryScan");
                index = null;
                reason = ex.Message;
                return false;
            }
        }

        private static int Align(int value, int unit) => (value + unit - 1) / unit * unit;

        /// <summary>Reads <paramref name="count"/> bytes from the given volume position into <paramref name="buffer"/> starting at
        /// <paramref name="bufferOffset"/>. Position and length are multiples of the sector size.</summary>
        private static bool ReadAt(SafeFileHandle volume, long diskOffset, byte[] buffer, int count, int bufferOffset = 0)
        {
            if (!SetFilePointerEx(volume, diskOffset, out _, 0)) return false;
            if (bufferOffset == 0)
                return ReadFile(volume, buffer, (uint)count, out uint read, IntPtr.Zero) && read == count;

            var piece = new byte[count];
            if (!ReadFile(volume, piece, (uint)count, out uint got, IntPtr.Zero) || got != count) return false;
            Buffer.BlockCopy(piece, 0, buffer, bufferOffset, count);
            return true;
        }
    }
}
