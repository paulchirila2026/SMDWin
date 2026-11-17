using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    // ── Result models ────────────────────────────────────────────────────────

    /// <summary>Per-pass result for one sequential direction.</summary>
    public record PassResult(double MBps, double DurationSec);

    /// <summary>Full benchmark result for one drive.</summary>
    public class BenchmarkResult
    {
        public string  DriveLetter  { get; set; } = "C:";
        public bool    IsRemovable  { get; set; }
        public bool    IsSsd        { get; set; }
        public bool    IsNvme       { get; set; }
        /// <summary>True if the disk is connected through USB (USB-SATA bridge or stick), whether Windows reports it as
        /// "Removable" or "Fixed" — many external HDD enclosures show up as "Fixed". Tracked separately because USB-SATA
        /// bridges often have their own read/write cache, invisible to Windows, which can make a mechanical HDD "look" like
        /// an SSD (low latency, high speed) on small reads/writes.</summary>
        public bool    IsUsbAttached { get; set; }
        public string  DriveLabel   { get; set; } = "";
        public string? CacheWarning { get; set; }

        public string? WriteWarning { get; set; }
        /// <summary>True when calibration found writes below the threshold on an internal disk — the test ran in the
        /// reduced variant (small files), so it does not take tens of minutes.</summary>
        public bool IsSlowWriteMode { get; set; }

        // Sequential — list of per-pass values (typically 3 passes)
        public List<double> WritePassMBps { get; set; } = new();
        public List<double> ReadPassMBps  { get; set; } = new();

        // Aggregates (computed from passes 2+ to skip SLC warmup)
        public double WriteAvgMBps  => WritePassMBps.Count > 1 ? WritePassMBps.Skip(1).Average() : WritePassMBps.Count > 0 ? WritePassMBps[0] : 0;
        public double ReadAvgMBps   => ReadPassMBps.Count  > 1 ? ReadPassMBps.Skip(1).Average()  : ReadPassMBps.Count  > 0 ? ReadPassMBps[0]  : 0;
        public double WriteMinMBps  => WritePassMBps.Count > 0 ? WritePassMBps.Min() : 0;
        public double WriteMaxMBps  => WritePassMBps.Count > 0 ? WritePassMBps.Max() : 0;
        public double ReadMinMBps   => ReadPassMBps.Count  > 0 ? ReadPassMBps.Min()  : 0;
        public double ReadMaxMBps   => ReadPassMBps.Count  > 0 ? ReadPassMBps.Max()  : 0;

        // 4K Random
        public long   ReadIOPS      { get; set; }
        public long   WriteIOPS     { get; set; }

        public double SeqReadQ8MBps      { get; set; }
        public double SeqWriteQ8MBps     { get; set; }
        public long   Rand4kReadQ32Iops  { get; set; }
        public long   Rand4kWriteQ32Iops { get; set; }
        public bool   HasQdResults => SeqReadQ8MBps > 0 || Rand4kReadQ32Iops > 0;

        // Latency (microseconds)
        public double LatencyP50Us  { get; set; }
        public double LatencyP95Us  { get; set; }
        public double LatencyP99Us  { get; set; }
        public double LatencyMinUs  { get; set; }
        public double LatencyAvgUs  { get; set; }
        public double LatencyMaxUs  { get; set; }

        // Rating
        public string Rating        { get; set; } = "";
        public string RatingColor   { get; set; } = "#22C55E";
        public bool   IsQuickMode   { get; set; }  // true = 1 pass / 128MB (USB auto or chosen by the user)

        // Legacy compat (used by DiskAdvancedService comparison)
        public double SeqReadMBs  => ReadAvgMBps;
        public double SeqWriteMBs => WriteAvgMBps;
    }

    /// <summary>Live progress during benchmark.</summary>
    public class BenchmarkProgress
    {
        public string Phase      { get; set; } = "";   // "write" | "read" | "iops_read" | "iops_write" | "latency" | "done"
        public string Message    { get; set; } = "";
        public double CurrentMBps { get; set; }
        public int    PassNumber  { get; set; }        // 1-based
        public int    TotalPasses { get; set; }
        public double BarFraction { get; set; }        // 0.0-1.0 for progress bar
        /// <summary>Sequential test: how much of the phase data (write or read) has been transferred, 0–1; -1 in other phases.</summary>
        public double SeqFraction { get; set; } = -1;
        /// <summary>The sequential test: the total volume of a phase, in MB (file × passes).</summary>
        public double SeqTotalMB  { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────

    public class DiskBenchmarkService
    {
        public static void SweepOrphanedBenchFiles()
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    try
                    {
                        foreach (var pattern in new[] { "_smdwin_bench_*.tmp" })
                        {
                            foreach (var file in Directory.EnumerateFiles(drive.RootDirectory.FullName, pattern, SearchOption.TopDirectoryOnly))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    // safety margin: only files older than 1 h — a real benchmark in progress must never be touched
                                    if ((DateTime.Now - fi.LastWriteTime).TotalHours < 1) continue;
                                    fi.Delete();
                                    AppLogger.Info("Sweep: deleted orphan benchmark file — {0} ({1:F0} MB)",
                                        file, fi.Length / 1024.0 / 1024.0);
                                }
                                catch (Exception exFile) { AppLogger.Debug("Sweep tmp: {0}", exFile.Message); }
                            }
                        }
                    }
                    catch (Exception exDrive) { AppLogger.Debug("Sweep drive {0}: {1}", drive.Name, exDrive.Message); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SweepOrphanedBenchFiles"); }
        }

        // FILE_FLAG_NO_BUFFERING — bypasses OS page cache (real disk speed)
        private const FileOptions NoBuffering  = (FileOptions)0x20000000;
        private const FileOptions WriteThrough = FileOptions.WriteThrough;

        // ── P/Invoke for reads with a sector-ALIGNED buffer ──────────────────
        // FILE_FLAG_NO_BUFFERING requires the buffer passed to ReadFile to be aligned to the physical sector size
        // (usually 4096 bytes on Advanced Format SSDs). A GC-allocated byte[] is NOT guaranteed to be aligned,
        // so FileStream.Read with NoBuffering may throw ERROR_INVALID_PARAMETER or — worse — work "by accident"
        // on some systems and not on others. Solution: read directly through ReadFile with an aligned pointer.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool ReadFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            byte* lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool WriteFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            byte* lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);
        private const uint FILE_BEGIN = 0;

        /// <summary>
        /// Reads exactly <paramref name="count"/> aligned bytes from a handle opened with FILE_FLAG_NO_BUFFERING,
        /// using a native sector-aligned buffer. Returns the bytes actually read (may be fewer at EOF).
        /// </summary>
        private static unsafe int ReadAligned(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr alignedPtr, int count)
        {
            bool ok = ReadFile(handle, (byte*)alignedPtr, (uint)count, out uint bytesRead, IntPtr.Zero);
            return ok ? (int)bytesRead : 0;
        }

        private static string _lastReadError = "";

        /// <summary>Reads at an explicit offset (RandomAccess), independent of the handle's internal position;
        /// the buffer stays the native aligned one. The error (if any) stays in <see cref="_lastReadError"/>.</summary>
        private static unsafe int ReadAtOffset(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr alignedPtr, int count, long offset)
        {
            try { return RandomAccess.Read(handle, new Span<byte>((void*)alignedPtr, count), offset); }
            catch (Exception ex) { _lastReadError = ex.GetType().Name + ": " + ex.Message; return 0; }
        }

        /// <summary>Writes at an explicit offset (the file grows safely, whatever the handle position); if RandomAccess
        /// fails, falls back to the native write after moving the pointer.</summary>
        private static unsafe int WriteAtOffset(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr alignedPtr, int count, long offset)
        {
            try { RandomAccess.Write(handle, new ReadOnlySpan<byte>((void*)alignedPtr, count), offset); return count; }
            catch (Exception ex)
            {
                _lastWriteError = ex.GetType().Name + ": " + ex.Message;
                SetFilePointerEx(handle, offset, out _, FILE_BEGIN);
                return WriteAligned(handle, alignedPtr, count);
            }
        }

        private static string _lastWriteError = "";

        /// <summary>Writes with an aligned buffer — required for FILE_FLAG_NO_BUFFERING.</summary>
        private static unsafe int WriteAligned(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr alignedPtr, int count)
        {
            bool ok = WriteFile(handle, (byte*)alignedPtr, (uint)count, out uint bytesWritten, IntPtr.Zero);
            return ok ? (int)bytesWritten : 0;
        }

        /// <summary>Allocates sector-aligned native memory — used for all NoBuffering reads/writes.</summary>
        private static IntPtr AllocAligned(int size, uint sectorSize, out IntPtr rawPtr)
        {
            rawPtr = Marshal.AllocHGlobal(size + (int)sectorSize);
            long addr = ((rawPtr.ToInt64() + sectorSize - 1) / sectorSize) * sectorSize;
            return new IntPtr(addr);
        }

        private static (double mbps, long iops) RunParallelIo(
            string path, int threads, int chunk, long fileBytes, bool write, bool random,
            bool removable, double seconds, CancellationToken ct, Action<double>? onProgress = null)
        {
            long totalOps    = 0;
            long totalChunks = Math.Max(1, fileBytes / chunk);
            var  stopAt      = DateTime.UtcNow.AddSeconds(seconds);
            var  swTotal     = Stopwatch.StartNew();
            var  workers     = new Thread[threads];

            for (int t = 0; t < threads; t++)
            {
                int tid = t;
                workers[t] = new Thread(() =>
                {
                    IntPtr buf = AllocAligned(chunk, 4096, out IntPtr raw);
                    try
                    {
                        // Same flags as the equivalent QD1 tests: NoBuffering on internal disks (the OS cache would distort reads),
                        // WriteThrough on writes. FileShare.ReadWrite is REQUIRED — all N handles stay open on the same file at once.
                        FileOptions opts = random ? FileOptions.RandomAccess : FileOptions.SequentialScan;
                        if (!removable) opts |= NoBuffering;
                        if (write)      opts |= WriteThrough;
                        using var fs = new FileStream(path, FileMode.Open,
                            write ? FileAccess.ReadWrite : FileAccess.Read,
                            FileShare.ReadWrite, chunk, opts);

                        var  rng          = new Random(1000 + tid * 7919);
                        long stripeChunks = Math.Max(1, totalChunks / threads);
                        long stripeStart  = tid * stripeChunks;
                        long pos          = 0;
                        long localOps     = 0;

                        while (DateTime.UtcNow < stopAt && !ct.IsCancellationRequested)
                        {
                            long chunkIdx = random
                                ? Math.Abs(rng.NextInt64()) % totalChunks
                                : stripeStart + (pos++ % stripeChunks);
                            SetFilePointerEx(fs.SafeFileHandle, chunkIdx * chunk, out _, FILE_BEGIN);
                            if (write) WriteAligned(fs.SafeFileHandle, buf, chunk);
                            else       ReadAligned (fs.SafeFileHandle, buf, chunk);
                            localOps++;
                        }
                        Interlocked.Add(ref totalOps, localOps);
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskBench.RunParallelIo"); }
                    finally { Marshal.FreeHGlobal(raw); }
                }) { IsBackground = true, Name = $"SMDWin-QDBench-{tid}" };
                workers[t].Start();
            }

            // Progress pump on the calling thread (the benchmark already runs inside Task.Run)
            while (workers.Any(w => w.IsAlive))
            {
                Thread.Sleep(150);
                onProgress?.Invoke(Math.Clamp(swTotal.Elapsed.TotalSeconds / seconds, 0, 1));
            }
            foreach (var w in workers) w.Join();
            swTotal.Stop();

            double secsActual = Math.Max(0.001, swTotal.Elapsed.TotalSeconds);
            double mbps = totalOps * (double)chunk / (1024.0 * 1024.0) / secsActual;
            long   iops = (long)(totalOps / secsActual);
            return (mbps, iops);
        }

        // Full mode (intern NVMe/SATA)
        private const int seqFileMB_Full      = 512;   // 512 MB: more accurate than 256 MB for SSD warm-up
        private const int seqPasses_Full      = 3;
        private const int Rand4KOps_Full      = 2000;
        private const int Rand4KWriteOps_Full = 500;
        private const int LatencyOps_Full     = 1000;

        // Quick mode (USB/Removable automatically, or chosen by the user)
        private const int seqFileMB_Quick      = 128;
        private const int seqPasses_Quick      = 1;
        private const int Rand4KOps_Quick      = 500;
        private const int Rand4KWriteOps_Quick = 100;
        private const int LatencyOps_Quick     = 300;

        private const int SeqChunkKB   = 512;
        private const int Rand4KFileMB = 256;  // 256MB: avoids the OS cache on IOPS (32MB was too small)

        /// <param name="forceQuick">null = auto (quick pt Removable, full pt intern), true/false = fortat</param>
        private static readonly Dictionary<string, (bool nvme, bool ssd, bool usb, string model)> _detectCache = new();

        public async Task<BenchmarkResult> RunAsync(
            string driveLetter,
            CancellationToken ct = default,
            IProgress<BenchmarkProgress>? progress = null,
            bool? forceQuick = null)
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
            var effectiveCt = linked.Token;

            return await Task.Run(() =>
            {
                // Local shadow: 'ct' inside this lambda is the combined token (external cancel + 5-minute watchdog).
                var ct = effectiveCt;

                var root   = driveLetter.TrimEnd('\\') + "\\";
                var result = new BenchmarkResult { DriveLetter = driveLetter };

                // ── Detect drive type ─────────────────────────────────────────
                try
                {
                    var di = new DriveInfo(root);
                    result.IsRemovable = di.DriveType == DriveType.Removable
                                      || di.DriveType == DriveType.Network;
                    result.DriveLabel  = di.VolumeLabel.Length > 0 ? di.VolumeLabel : di.DriveType.ToString();
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                // Declared outside the try block: it is also used below, for the USB rating (confirmedNotSsd).
                string model = "";
                // disk type detection (WMI) is slow and does not change between two tests on the same drive:
                // it is remembered, so the second test starts immediately
                string cacheKey = driveLetter.TrimEnd('\\', ':').ToUpperInvariant();
                try { var dik = new DriveInfo(root); cacheKey += "|" + dik.TotalSize + "|" + dik.VolumeLabel; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } // another stick on the same letter = another entry
                bool detectCached = false;
                lock (_detectCache)
                {
                    if (_detectCache.TryGetValue(cacheKey, out var dc))
                    {
                        result.IsNvme = dc.nvme; result.IsSsd = dc.ssd; result.IsUsbAttached = dc.usb; model = dc.model;
                        detectCached = true;
                    }
                }
                if (!detectCached)
                try
                {
                    string letter = driveLetter.TrimEnd('\\', ':'); // "C"
                    int diskIndex = -1;
                    string iface = "", mtText = "";
                    // Pas 1: LogicalDisk → DiskPartition
                    string qPart = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}:'}} " +
                                   "WHERE AssocClass=Win32_LogicalDiskToPartition";
                    using var partSearcher = SMDWin.Services.WmiHelper.Searcher(qPart, System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp1 = partSearcher.Get())
                    {
                        foreach (ManagementObject part in __wmiTmp1)
                    {
                        // Pas 2: DiskPartition → DiskDrive
                        string qDisk = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} " +
                                       "WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                        using var diskSearcher = SMDWin.Services.WmiHelper.Searcher(qDisk, System.TimeSpan.FromSeconds(10));
                        using (var __wmiTmp2 = diskSearcher.Get())
                        {
                        foreach (ManagementObject disk in __wmiTmp2)
                        {
                            mtText = disk["MediaType"]?.ToString()      ?? "";
                            model  = disk["Model"]?.ToString()          ?? "";
                            iface  = disk["InterfaceType"]?.ToString()  ?? "";
                            try { diskIndex = Convert.ToInt32(disk["Index"] ?? -1); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                            break;
                        }
                        }
                        break;
                    }
                    }

                    bool numericDetected = false;
                    if (diskIndex >= 0)
                    {
                        try
                        {
                            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                            var query = new ObjectQuery(
                                $"SELECT MediaType, SpindleSpeed, BusType FROM MSFT_PhysicalDisk WHERE DeviceId='{diskIndex}'");
                            using var pdSearcher = SMDWin.Services.WmiHelper.Searcher(scope, query, System.TimeSpan.FromSeconds(10));
                            using (var __wmiTmp2 = pdSearcher.Get())
                            {
                                foreach (ManagementObject pd in __wmiTmp2)
                            {
                                ushort mediaType = 0; try { mediaType = Convert.ToUInt16(pd["MediaType"] ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                                ushort busType   = 0; try { busType   = Convert.ToUInt16(pd["BusType"]   ?? 0); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                                uint   spindle   = 1; try { spindle   = Convert.ToUInt32(pd["SpindleSpeed"] ?? 1u); } catch (Exception ex) { AppLogger.Debug(ex.Message); }

                                result.IsNvme = busType == 17;                        // 17 = NVMe
                                result.IsUsbAttached = busType == 7;                  // 7 = USB (MSFT_PhysicalDisk.BusType)
                                result.IsSsd  = result.IsNvme
                                             || mediaType == 4 || mediaType == 5      // 4=SSD, 5=SCM
                                             || (mediaType == 0 && spindle == 0);     // Unspecified but without platters
                                // MediaType 3 (HDD) or 4/5 (SSD/SCM) = a firm answer;
                                // 0 (Unspecified) without a useful SpindleSpeed → leave it to the fallback
                                numericDetected = mediaType is 3 or 4 or 5 || spindle == 0;
                                break;
                            }
                            }
                        }
                        catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* Storage namespace unavailable — fallback below */ }
                    }

                    // Fallback: string heuristics (only if the numeric check gave no firm answer)
                    if (!numericDetected)
                    {
                        result.IsNvme = mtText.Contains("NVMe",        StringComparison.OrdinalIgnoreCase)
                                     || mtText.Contains("Non-Volatile", StringComparison.OrdinalIgnoreCase)
                                     || model.Contains("NVMe",          StringComparison.OrdinalIgnoreCase)
                                     || iface.Contains("NVMe",          StringComparison.OrdinalIgnoreCase);
                        result.IsSsd  = result.IsNvme
                                     || mtText.Contains("SSD",   StringComparison.OrdinalIgnoreCase)
                                     || mtText.Contains("Solid",  StringComparison.OrdinalIgnoreCase)
                                     || model.Contains("SSD",     StringComparison.OrdinalIgnoreCase);
                    }
                    // Win32_DiskDrive.InterfaceType reliably reports "USB" for many external enclosures even when
                    // MSFT_PhysicalDisk does not see the disk at all (common for external HDDs) — an independent signal,
                    // combined with the one above.
                    if (iface.Contains("USB", StringComparison.OrdinalIgnoreCase)) result.IsUsbAttached = true;
                    lock (_detectCache) _detectCache[cacheKey] = (result.IsNvme, result.IsSsd, result.IsUsbAttached, model);
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                // ── Choose the mode: Quick or Full ──────────────────────────────
                // Auto: Removable → Quick (avoids waiting 10+ minutes on a slow USB stick)
                // Forced by the user: forceQuick override
                bool quickMode = forceQuick ?? result.IsRemovable;
                result.IsQuickMode   = quickMode;
                int seqFileMB        = quickMode ? seqFileMB_Quick      : seqFileMB_Full;
                int seqPasses        = quickMode ? seqPasses_Quick       : seqPasses_Full;
                int rand4kOps        = quickMode ? Rand4KOps_Quick       : Rand4KOps_Full;
                int rand4kWriteOps   = quickMode ? Rand4KWriteOps_Quick  : Rand4KWriteOps_Full;
                int latencyOps       = quickMode ? LatencyOps_Quick      : LatencyOps_Full;
                var seqOptions = result.IsRemovable
                    ? WriteThrough
                    : WriteThrough | NoBuffering;

                int chunkSize = SeqChunkKB * 1024;
                var data      = new byte[chunkSize];
                new Random(42).NextBytes(data);

                IntPtr seqWritePtr = AllocAligned(chunkSize, 4096, out IntPtr seqWriteRaw);
                Marshal.Copy(data, 0, seqWritePtr, chunkSize);

                // ── Check free space ──────────────────────────────────────────
                try
                {
                    var di = new DriveInfo(root);
                    // the test file + a reserve (2 % of the disk, at least 1 GB), so the disk is not left full if the test is interrupted
                    long reserve  = Math.Max(1L << 30, di.TotalSize / 50);
                    long required = (long)(seqFileMB + 64) * 1024 * 1024 + reserve;
                    if (di.AvailableFreeSpace < required)
                    {
                        result.Rating      = $"⚠ Not enough free space (needs {required / 1073741824.0:0.#} GB free)";
                        result.RatingColor = "#EF4444";
                        Marshal.FreeHGlobal(seqWriteRaw);
                        return result;
                    }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                var seqTmpFile  = Path.Combine(root, $"_smdwin_bench_seq_{Guid.NewGuid():N}.tmp");
                var randTmpFile = Path.Combine(root, $"_smdwin_bench_rnd_{Guid.NewGuid():N}.tmp");

                const double targetSecondsPerPhaseFull  = 12.0;
                const double targetSecondsPerPhaseQuick = 3.0;
                const double SlowRemovableThresholdMBps = 15.0;
                Report(progress, "calibrate", "Calibrating…", 0, 1, 1, 0.0);
                const double VerySlowWriteMBps = 20.0;
                bool slowDrive = false;
                try
                {
                    const int calibChunks = 24; // 24 × 512KB = 12MB — fast, but enough for a stable estimate
                    var calibSw = Stopwatch.StartNew();
                    using (var calibFs = new FileStream(seqTmpFile, FileMode.Create,
                        FileAccess.Write, FileShare.None, chunkSize, seqOptions))
                    {
                        for (int i = 0; i < calibChunks; i++)
                            WriteAligned(calibFs.SafeFileHandle, seqWritePtr, chunkSize);
                        calibFs.Flush();
                    }
                    calibSw.Stop();
                    double calibMBps = calibSw.Elapsed.TotalSeconds > 0.001
                        ? (calibChunks * chunkSize / 1_048_576.0) / calibSw.Elapsed.TotalSeconds
                        : 0;

                    if (calibMBps > 0)
                    {
                        double targetSeconds = quickMode ? targetSecondsPerPhaseQuick : targetSecondsPerPhaseFull;
                        var (minPasses, maxPasses) = quickMode ? (1, 3) : (3, 12);
                        seqPasses = Math.Clamp((int)Math.Ceiling(targetSeconds * calibMBps / seqFileMB), minPasses, maxPasses);

                        if (quickMode && calibMBps < SlowRemovableThresholdMBps)
                        {
                            // Genuinely slow stick (USB 2.0, SD card in a reader, etc.) — reduce the amount of data too, not only the number
                            // of passes. Halve the sequential file (floor 16 MB, still meaningful as a test). rand4kOps/rand4kWriteOps are NOT
                            // touched — the random IOPS tests are already time-boxed (fixed 5 s / 3 s, see below). latencyOps, however, IS a
                            // number of operations — it is reduced as well.
                            seqFileMB      = Math.Max(16, seqFileMB / 2);
                            latencyOps     = Math.Max(80, latencyOps * 2 / 5);
                        }

                        AppLogger.Info("Benchmark {0}: sequential write calibration {1:F1} MB/s (removable={2})",
                            driveLetter, calibMBps, result.IsRemovable);
                        if (!result.IsRemovable && calibMBps < VerySlowWriteMBps)
                        {
                            slowDrive  = true;
                            seqFileMB  = 32;
                            seqPasses  = 1;
                            latencyOps = Math.Min(latencyOps, 300);
                            result.IsSlowWriteMode = true;
                            AppLogger.Warning("Benchmark {0}: very slow writes ({1:F1} MB/s) on an internal disk — running the reduced variant",
                                driveLetter, calibMBps);
                        }
                    }
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* calibration failed — the default Quick/Full values stay */ }

                int chunks = (seqFileMB * 1024 * 1024) / chunkSize;

                try
                {
                    int totalUnits = seqPasses * 2 + 3 + (quickMode ? 0 : 4); // N writes + N reads + iops_read + iops_write + latency [+ 4×QD]
                    double UnitFrac(int unitsCompleted, double withinUnitFrac) =>
                        Math.Clamp((unitsCompleted + Math.Clamp(withinUnitFrac, 0, 1)) / totalUnits, 0.0, 0.99);

                    // ── Sequential Write passes ───────────────────────────────
                    for (int pass = 1; pass <= seqPasses && !ct.IsCancellationRequested; pass++)
                    {
                        string passMsg  = $"Sequential Write — pass {pass}/{seqPasses}";
                        Report(progress, "write", passMsg, 0, pass, seqPasses, UnitFrac(pass - 1, 0));

                        var sw      = Stopwatch.StartNew();
                        var chunkSw = Stopwatch.StartNew();

                        using var fs = new FileStream(seqTmpFile,
                            pass == 1 ? FileMode.Create : FileMode.Open,
                            FileAccess.Write, FileShare.None, chunkSize, seqOptions);

                        if (pass > 1) SetFilePointerEx(fs.SafeFileHandle, 0, out _, FILE_BEGIN);

                        int writtenChunks = 0, writeSince = 0;
                        var writeDeadline = DateTime.UtcNow.AddSeconds(40);
                        for (int i = 0; i < chunks && !ct.IsCancellationRequested && DateTime.UtcNow < writeDeadline; i++)
                        {
                            if (WriteAtOffset(fs.SafeFileHandle, seqWritePtr, chunkSize, (long)i * chunkSize) <= 0)
                            {
                                AppLogger.Warning("Benchmark {0}: the sequential write stopped at block {1} ({2})", driveLetter, i, _lastWriteError);
                                break;
                            }
                            writtenChunks++; writeSince++;
                            if (writeSince >= 4 && chunkSw.Elapsed.TotalSeconds > 0.01)
                            {
                                // speed over EVERYTHING written since the last report (counting only a fixed number of blocks
                                // under-reported high speeds several times over)
                                double live = (writeSince * (double)chunkSize / 1_048_576.0) / chunkSw.Elapsed.TotalSeconds;
                                writeSince = 0;
                                Report(progress, "write", passMsg, live, pass, seqPasses, UnitFrac(pass - 1, (double)i / chunks),
                                    (pass - 1 + (double)i / chunks) / seqPasses, (double)seqFileMB * seqPasses);
                                chunkSw.Restart();
                            }
                        }
                        fs.Flush();
                        sw.Stop();
                        if (pass == 1)
                            try { AppLogger.Info("Benchmark {0}: sequential file written: {1} MB of {2} MB expected", driveLetter, new FileInfo(seqTmpFile).Length / 1_048_576, (long)chunks * chunkSize / 1_048_576); }
                            catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        if (!ct.IsCancellationRequested && writtenChunks > 0)
                            result.WritePassMBps.Add(writtenChunks * (double)chunkSize / 1_048_576.0 / sw.Elapsed.TotalSeconds);
                    }

                    if (ct.IsCancellationRequested) return result;

                    // If any pass stopped at its deadline, the file may be shorter than expected — reads and QD tests use the real length.
                    try
                    {
                        int haveChunks = (int)(new FileInfo(seqTmpFile).Length / chunkSize);
                        if (haveChunks > 0 && haveChunks < chunks) chunks = haveChunks;
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                    // Flush OS write buffers before reading
                    Thread.Sleep(result.IsRemovable ? 1000 : 300);

                    // ── Sequential Read passes ────────────────────────────────
                    // read directly with a natively aligned buffer (see ReadAligned)
                    for (int pass = 1; pass <= seqPasses && !ct.IsCancellationRequested; pass++)
                    {
                        string passMsg = $"Sequential Read — pass {pass}/{seqPasses}";
                        Report(progress, "read", passMsg, 0, pass, seqPasses, UnitFrac(seqPasses + (pass - 1), 0));

                        var sw      = Stopwatch.StartNew();
                        var chunkSw = Stopwatch.StartNew();
                        int count   = 0, readSince = 0, readReports = 0, readError = 0;

                        uint sectorSize = 4096; // safe alignment for all Advanced Format SSDs/HDDs
                        IntPtr rawBuf = Marshal.AllocHGlobal(chunkSize + (int)sectorSize);
                        long alignedAddr = ((rawBuf.ToInt64() + sectorSize - 1) / sectorSize) * sectorSize;
                        IntPtr alignedPtr = new IntPtr(alignedAddr);
                        try
                        {
                            using var fs = new FileStream(seqTmpFile,
                                FileMode.Open, FileAccess.Read, FileShare.None, chunkSize,
                                FileOptions.SequentialScan | NoBuffering);

                            int bytesRead;
                            var passDeadline = DateTime.UtcNow.AddSeconds(35);
                            while (count < chunks
                                   && (bytesRead = ReadAtOffset(fs.SafeFileHandle, alignedPtr, chunkSize, (long)count * chunkSize)) > 0
                                   && !ct.IsCancellationRequested
                                   && DateTime.UtcNow < passDeadline)
                            {
                                count++; readSince++;
                                if (readSince >= 4 && chunkSw.Elapsed.TotalSeconds > 0.01)
                                {
                                    double live = (readSince * (double)chunkSize / 1_048_576.0) / chunkSw.Elapsed.TotalSeconds;
                                    readSince = 0; readReports++;
                                    Report(progress, "read", passMsg, live, pass, seqPasses, UnitFrac(seqPasses + (pass - 1), (double)count / chunks),
                                        (pass - 1 + (double)count / chunks) / seqPasses, (double)seqFileMB * seqPasses);
                                    chunkSw.Restart();
                                }
                            }
                            if (count == 0) readError = Marshal.GetLastWin32Error();
                            if (count == 0 && !ct.IsCancellationRequested)
                            {
                                AppLogger.Warning("Benchmark {0}: the direct read failed ({1}) — retrying through the Windows cache", driveLetter, _lastReadError);
                                // fallback: normal read (through the cache); the result is flagged in a warning
                                using var fb = new FileStream(seqTmpFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, FileOptions.SequentialScan);
                                var fbBuf = new byte[chunkSize];
                                sw.Restart(); chunkSw.Restart();
                                int n;
                                var fbDeadline = DateTime.UtcNow.AddSeconds(35);
                                while (count < chunks && (n = fb.Read(fbBuf, 0, chunkSize)) > 0 && !ct.IsCancellationRequested && DateTime.UtcNow < fbDeadline)
                                {
                                    count++; readSince++;
                                    if (readSince >= 4 && chunkSw.Elapsed.TotalSeconds > 0.01)
                                    {
                                        double live = (readSince * (double)chunkSize / 1_048_576.0) / chunkSw.Elapsed.TotalSeconds;
                                        readSince = 0; readReports++;
                                        Report(progress, "read", passMsg, live, pass, seqPasses, UnitFrac(seqPasses + (pass - 1), (double)count / chunks),
                                            (pass - 1 + (double)count / chunks) / seqPasses, (double)seqFileMB * seqPasses);
                                        chunkSw.Restart();
                                    }
                                }
                                if (count > 0 && string.IsNullOrWhiteSpace(result.CacheWarning))
                                    result.CacheWarning = "Sequential read could not bypass the Windows cache, so the read speed may look higher than the drive's real speed.";
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(rawBuf);
                        }
                        sw.Stop();
                        {
                            double passMB = (double)count * chunkSize / 1_048_576.0;
                            double passMBps = sw.Elapsed.TotalSeconds > 0.001 ? passMB / sw.Elapsed.TotalSeconds : 0;
                            if (count == 0)
                                AppLogger.Warning("Benchmark {0}: the sequential read (pass {1}/{2}) returned no data — Win32 error {3}",
                                    driveLetter, pass, seqPasses, readError);
                            else
                                AppLogger.Info("Benchmark {0}: sequential read {1}/{2}: {3:F0} MB in {4:F2} s = {5:F0} MB/s, {6} live values",
                                    driveLetter, pass, seqPasses, passMB, sw.Elapsed.TotalSeconds, passMBps, readReports);
                            // chart safety net: the pass average, at its end (if there were no live values, the line still appears)
                            if (passMBps > 0 && !ct.IsCancellationRequested)
                                Report(progress, "read", passMsg, passMBps, pass, seqPasses, UnitFrac(seqPasses + (pass - 1), 1.0),
                                    Math.Min(0.999, (double)pass / seqPasses), (double)seqFileMB * seqPasses);
                        }
                        if (!ct.IsCancellationRequested)
                        {
                            double actualMB = (double)count * chunkSize / 1_048_576.0;
                            if (sw.Elapsed.TotalSeconds > 0.001 && actualMB > 0)
                                result.ReadPassMBps.Add(actualMB / sw.Elapsed.TotalSeconds);
                        }
                    }

                    if (ct.IsCancellationRequested) return result;

                    // ── 4K Random IOPS ────────────────────────────────────────
                    Report(progress, "iops_write", "4K Random Write — preparing…", 0, 1, 1, UnitFrac(seqPasses * 2, 0));

                    int r4kChunk    = 4096;
                    long r4kBytes   = (long)(slowDrive ? 32 : Rand4KFileMB) * 1024 * 1024;
                    long r4kBlocks  = r4kBytes / r4kChunk;

                    {
                        using var fs = new FileStream(randTmpFile, FileMode.Create,
                            FileAccess.Write, FileShare.None, chunkSize,
                            result.IsRemovable ? WriteThrough : WriteThrough | NoBuffering);
                        long fillChunks = Math.Max(1, r4kBytes / chunkSize);
                        for (long i = 0; i < fillChunks && !ct.IsCancellationRequested; i++)
                        {
                            WriteAligned(fs.SafeFileHandle, seqWritePtr, chunkSize);
                            if (i % 8 == 7)
                                Report(progress, "iops_write", $"4K Random Write — preparing… {(int)(100.0 * i / fillChunks)}%",
                                    0, 1, 1, UnitFrac(seqPasses * 2, (double)i / fillChunks * 0.9));
                        }
                    }

                    if (ct.IsCancellationRequested) return result;

                    // 4K Random Read IOPS — time-based 5s (NVMe face milioane, HDD face ~500)
                    int iopsReadUnit = seqPasses * 2; // the drive right after the last read step
                    Report(progress, "iops_read", "4K Random Read…", 0, 1, 1, UnitFrac(iopsReadUnit, 0));
                    {
                        var rng = new Random(7);
                        long readOps = 0;
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        var sw = Stopwatch.StartNew();
                        IntPtr iopsAlignedPtr = AllocAligned(r4kChunk, 4096, out IntPtr iopsRawPtr);
                        try
                        {
                            using var fs = new FileStream(randTmpFile, FileMode.Open,
                                FileAccess.Read, FileShare.Read, r4kChunk,
                                result.IsRemovable ? FileOptions.RandomAccess : FileOptions.RandomAccess | NoBuffering);
                            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                            {
                                long offset = (Math.Abs(rng.NextInt64()) % r4kBlocks) * r4kChunk;
                                SetFilePointerEx(fs.SafeFileHandle, offset, out _, FILE_BEGIN);
                                ReadAligned(fs.SafeFileHandle, iopsAlignedPtr, r4kChunk);
                                readOps++;
                                if (readOps % 500 == 0)
                                    Report(progress, "iops_read", $"4K Random Read… {readOps:N0} ops",
                                        0, 1, 1, UnitFrac(iopsReadUnit, sw.Elapsed.TotalSeconds / 5.0));
                            }
                        }
                        finally { Marshal.FreeHGlobal(iopsRawPtr); }
                        sw.Stop();
                        result.ReadIOPS = sw.Elapsed.TotalSeconds > 0
                            ? (long)(readOps / sw.Elapsed.TotalSeconds) : 0;
                    }

                    if (ct.IsCancellationRequested) return result;

                    // 4K Random Write IOPS — time-based 3s
                    int iopsWriteUnit = seqPasses * 2 + 1;
                    Report(progress, "iops_write", "4K Random Write…", 0, 1, 1, UnitFrac(iopsWriteUnit, 0));
                    {
                        var wrng = new Random(13);
                        long writeOps = 0;
                        var deadline2 = DateTime.UtcNow.AddSeconds(3);
                        var sw = Stopwatch.StartNew();
                        IntPtr wAlignedPtr = AllocAligned(r4kChunk, 4096, out IntPtr wRawPtr);
                        try
                        {
                            using var fs = new FileStream(randTmpFile, FileMode.Open,
                                FileAccess.ReadWrite, FileShare.None, r4kChunk,
                                result.IsRemovable ? WriteThrough | FileOptions.RandomAccess
                                                   : WriteThrough | NoBuffering | FileOptions.RandomAccess);
                            while (DateTime.UtcNow < deadline2 && !ct.IsCancellationRequested)
                            {
                                long offset = (Math.Abs(wrng.NextInt64()) % r4kBlocks) * r4kChunk;
                                SetFilePointerEx(fs.SafeFileHandle, offset, out _, FILE_BEGIN);
                                WriteAligned(fs.SafeFileHandle, wAlignedPtr, r4kChunk);
                                writeOps++;
                                if (writeOps % 500 == 0)
                                    Report(progress, "iops_write", $"4K Random Write… {writeOps:N0} ops",
                                        0, 1, 1, UnitFrac(iopsWriteUnit, sw.Elapsed.TotalSeconds / 3.0));
                            }
                        }
                        finally { Marshal.FreeHGlobal(wRawPtr); }
                        sw.Stop();
                        result.WriteIOPS = sw.Elapsed.TotalSeconds > 0
                            ? (long)(writeOps / sw.Elapsed.TotalSeconds) : 0;
                    }

                    if (ct.IsCancellationRequested) return result;

                    // ── Latency (QD1 random read, microseconds) ───────────────
                    int latencyUnit = seqPasses * 2 + 2;
                    var latencySw = Stopwatch.StartNew();
                    Report(progress, "latency", "Latency measurement…", 0, 1, 1, UnitFrac(latencyUnit, 0));
                    {
                        var lrng = new Random(31);
                        var latencies = new List<double>(latencyOps);
                        IntPtr latAlignedPtr = AllocAligned(r4kChunk, 4096, out IntPtr latRawPtr);
                        try
                        {
                            using var fs = new FileStream(randTmpFile, FileMode.Open,
                                FileAccess.Read, FileShare.Read, r4kChunk,
                                result.IsRemovable ? FileOptions.RandomAccess : FileOptions.RandomAccess | NoBuffering);

                            for (int i = 0; i < latencyOps && !ct.IsCancellationRequested; i++)
                            {
                                long offset = (Math.Abs(lrng.NextInt64()) % r4kBlocks) * r4kChunk;
                                SetFilePointerEx(fs.SafeFileHandle, offset, out _, FILE_BEGIN);
                                long t0 = Stopwatch.GetTimestamp();
                                ReadAligned(fs.SafeFileHandle, latAlignedPtr, r4kChunk);
                                long t1 = Stopwatch.GetTimestamp();
                                latencies.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency); // μs
                                if (i % 100 == 0 && i > 0)
                                    Report(progress, "latency", $"Latency… {i}/{latencyOps}",
                                        0, 1, 1, UnitFrac(latencyUnit, (double)i / latencyOps));
                            }
                        }
                        finally { Marshal.FreeHGlobal(latRawPtr); }

                        latencies.Sort();
                        int n = latencies.Count;
                        if (n > 0)
                        {
                            result.LatencyP50Us = latencies[(int)(n * 0.50)];
                            result.LatencyP95Us = latencies[(int)(n * 0.95)];
                            result.LatencyP99Us = latencies[(int)(n * 0.99)];
                            result.LatencyMinUs = latencies[0];
                            result.LatencyMaxUs = latencies[n - 1];
                            double sum = 0; foreach (var v in latencies) sum += v;
                            result.LatencyAvgUs = sum / n;
                        }
                    }

                    if (ct.IsCancellationRequested) return result;

                    if (!quickMode && !ct.IsCancellationRequested)
                    {
                        int qdUnit = latencyUnit + 1;
                        long seqFileBytes = (long)chunks * chunkSize;

                        // SEQ Read QD8 — 8 simultaneous sequential streams on the large file
                        Report(progress, "read", "Sequential Read (QD8)…", 0, 1, 1, UnitFrac(qdUnit, 0));
                        result.SeqReadQ8MBps = RunParallelIo(seqTmpFile, threads: 8, chunk: chunkSize,
                            fileBytes: seqFileBytes, write: false, random: false,
                            removable: result.IsRemovable, seconds: 4.0, ct,
                            frac => Report(progress, "read", "Sequential Read (QD8)…", 0, 1, 1, UnitFrac(qdUnit, frac))).mbps;

                        if (ct.IsCancellationRequested) return result;

                        // SEQ Write QD8
                        Report(progress, "write", "Sequential Write (QD8)…", 0, 1, 1, UnitFrac(qdUnit + 1, 0));
                        result.SeqWriteQ8MBps = RunParallelIo(seqTmpFile, threads: 8, chunk: chunkSize,
                            fileBytes: seqFileBytes, write: true, random: false,
                            removable: result.IsRemovable, seconds: 4.0, ct,
                            frac => Report(progress, "write", "Sequential Write (QD8)…", 0, 1, 1, UnitFrac(qdUnit + 1, frac))).mbps;

                        if (ct.IsCancellationRequested) return result;

                        // RND4K Read QD32 — 32 simultaneous 4K reads on the random file
                        Report(progress, "iops_read", "4K Random Read (QD32)…", 0, 1, 1, UnitFrac(qdUnit + 2, 0));
                        result.Rand4kReadQ32Iops = RunParallelIo(randTmpFile, threads: 32, chunk: r4kChunk,
                            fileBytes: (long)r4kBlocks * r4kChunk, write: false, random: true,
                            removable: result.IsRemovable, seconds: 4.0, ct,
                            frac => Report(progress, "iops_read", "4K Random Read (QD32)…", 0, 1, 1, UnitFrac(qdUnit + 2, frac))).iops;

                        if (ct.IsCancellationRequested) return result;

                        // RND4K Write QD32
                        Report(progress, "iops_write", "4K Random Write (QD32)…", 0, 1, 1, UnitFrac(qdUnit + 3, 0));
                        result.Rand4kWriteQ32Iops = RunParallelIo(randTmpFile, threads: 32, chunk: r4kChunk,
                            fileBytes: (long)r4kBlocks * r4kChunk, write: true, random: true,
                            removable: result.IsRemovable, seconds: 3.0, ct,
                            frac => Report(progress, "iops_write", "4K Random Write (QD32)…", 0, 1, 1, UnitFrac(qdUnit + 3, frac))).iops;
                    }

                    // Reclassifying "too fast / too little latency to be an HDD" is valid ONLY for internal disks. A USB-SATA bridge
                    // (common for external HDDs in an enclosure) often has its own read/write cache, invisible to Windows — a Toshiba
                    // MQ01ABF050 (5400 rpm mechanical HDD) can "look" like an SSD on small tests simply because the USB bridge answered
                    // from its cache, not from the platter. Both heuristics below are disabled when the disk is on USB, even if Windows
                    // reports it as "Fixed" (many external enclosures do NOT show as "Removable").
                    if (!result.IsSsd && !result.IsRemovable && !result.IsUsbAttached
                        && result.LatencyP50Us > 0 && result.LatencyP50Us < 500)
                    {
                        result.IsSsd = true;
                        AppLogger.Info("Benchmark: disk reclassified as SSD based on the measured latency" +
                                       "(P50 {0:F0}µs — impossible for a mechanical HDD)", result.LatencyP50Us);
                    }

                    double score = result.ReadAvgMBps;
                    // a disk on USB (even if Windows shows it as "Fixed", common for external HDDs in an enclosure) gets the same rating
                    // category as removable disks — a "SATA SSD"/"NVMe" label would be false for a disk behind a USB bridge whose cache
                    // can inflate the measured speeds.
                    if (result.IsRemovable || result.IsUsbAttached)
                    {
                        // if it is EXPLICITLY known not to be an SSD (the model name does not contain "SSD"/"Solid"/"NVMe" — classic HDDs),
                        // it does not get the "SSD" label just because the USB bridge answered quickly from its cache.
                        bool confirmedNotSsd = !result.IsSsd && !result.IsNvme && !string.IsNullOrWhiteSpace(model);
                        (result.Rating, result.RatingColor) = (confirmedNotSsd, score) switch
                        {
                            (true, _)         => (score >= 60 ? "💿 USB HDD" : "🐢 USB HDD — Slow", "#F59E0B"),
                            (false, >= 400)   => ("⚡ USB 3.x SSD Fast", "#22C55E"),
                            (false, >= 100)   => ("✅ USB 3.x SSD",       "#3B82F6"),
                            (false, >= 30)    => ("💾 USB Flash / HDD",   "#F59E0B"),
                            _                 => ("🐢 USB Slow",          "#EF4444"),
                        };
                        if (quickMode) result.Rating += "  ·  Quick";
                        if (confirmedNotSsd && score >= 150)
                        {
                            result.CacheWarning = SMDWin.Services.LanguageService.S("Disk", "UsbBridgeCacheWarning",
                                "This is a mechanical hard drive (HDD), but the measured speed looks like an SSD. " +
                                "External USB-SATA enclosures often have their own cache, invisible to Windows — " +
                                "small test files can be served from that cache instead of the actual platters. " +
                                "Real sustained speed on large files is likely much lower (typically 80-150 MB/s for a HDD).");
                        }
                    }
                    else if (result.IsNvme)
                    {
                        (result.Rating, result.RatingColor) = score switch
                        {
                            >= 5000 => ("⚡ NVMe Gen5",   "#22C55E"),
                            >= 3000 => ("⚡ NVMe Gen4",   "#22C55E"),
                            >= 1500 => ("🚀 NVMe Gen3",   "#3B82F6"),
                            _       => ("✅ NVMe",        "#3B82F6"),
                        };
                    }
                    else if (result.IsSsd)
                    {
                        // SATA SSD: typical read 450-560 MB/s. Boot Camp can be slower (350-480).
                        (result.Rating, result.RatingColor) = score switch
                        {
                            >= 450 => ("✅ SATA SSD",      "#22C55E"),
                            >= 300 => ("✅ SATA SSD",      "#3B82F6"),   // BootCamp/driver overhead
                            >= 200 => ("⚠ SATA SSD Slow", "#F59E0B"),
                            _      => ("⚠ SATA SSD — Low speed", "#EF4444"),
                        };
                    }
                    else
                    {
                        // IsNvme=false, IsSsd=false, IsRemovable=false.
                        // Fallback: speed > 150 MB/s on an internal drive = undetected SSD (WMI MediaType empty/unknown)
                        if (score >= 200)
                        {
                            (result.Rating, result.RatingColor) = score switch
                            {
                                >= 450 => ("✅ SSD (auto-detected)", "#22C55E"),
                                >= 300 => ("✅ SSD",                  "#3B82F6"),
                                _      => ("⚠ SSD — Low speed",      "#F59E0B"),
                            };
                            if (result.LatencyP50Us >= 500)
                            {
                                result.CacheWarning = SMDWin.Services.LanguageService.S("Disk", "CacheWarning",
                                    "This speed looks like an SSD, but the drive wasn't confirmed as a real SSD (measured access latency is typical of a mechanical HDD). You most likely have an SSHD or an HDD with its own onboard cache (in firmware, invisible to Windows) — the test file was read/written from cache, not from the platter. Real sustained speed on large files is likely much lower.");
                            }
                        }
                        else
                        {
                            (result.Rating, result.RatingColor) = score switch
                            {
                                >= 150 => ("💿 HDD Fast", "#F59E0B"),
                                >= 80  => ("💿 HDD",      "#F59E0B"),
                                _      => ("🐢 HDD Slow", "#EF4444"),
                            };
                        }
                    }

                    if (!result.IsRemovable && result.WritePassMBps.Count > 0)
                    {
                        double wAvg = result.WriteAvgMBps;
                        bool readMuchFaster = result.ReadAvgMBps > wAvg * 3;
                        bool slowSsd4k = (result.IsSsd || result.IsNvme) && result.WriteIOPS > 0 && result.WriteIOPS < 1000;
                        if (wAvg < 30 || slowDrive || slowSsd4k)
                        {
                            AppLogger.Warning("Benchmark {0}: slow writes — seq {1:F1} MB/s, 4K {2} IOPS, read {3:F0} MB/s",
                                driveLetter, wAvg, result.WriteIOPS, result.ReadAvgMBps);
                            // the warning, from translated pieces (fallback: the English text)
                            static string Part(string key, string english) => SMDWin.Services.LanguageService.S("Disk", key, english);
                            const string slowEnglish = "Writing to this drive is very slow (about {0} MB/s; a healthy SSD writes hundreds of MB/s).";
                            string speed = wAvg.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                            string slow;
                            // a language pack from outside may have broken braces: the English text is used then
                            try { slow = string.Format(Part("WriteSlow", slowEnglish), speed); }
                            catch (FormatException) { slow = string.Format(slowEnglish, speed); }
                            result.WriteWarning = slow
                                + " " + (readMuchFaster
                                    ? Part("WriteSlowReadFast", "Reading is much faster than writing, which usually points to a worn-out or failing SSD, a drive that is almost full, or a bad SATA cable/port.")
                                    : Part("WriteSlowCauses", "Possible causes: a worn-out or failing SSD, a drive that is almost full, or a bad SATA cable/port."))
                                + " " + Part("WriteSlowAdvice", "Check the drive's SMART page and the Windows Event Viewer for disk warnings, and back up important data.")
                                + (slowDrive ? " " + Part("WriteSlowShortened", "(The test was shortened to avoid running for many minutes.)") : "");
                        }
                    }

                    Report(progress, "done", "✓ Benchmark complete", 0, 1, 1, 1.0);
                }
                finally
                {
                    Marshal.FreeHGlobal(seqWriteRaw);
                    TryDelete(seqTmpFile);
                    TryDelete(randTmpFile);
                }

                return result;
            }, ct);
        }

        private static void Report(IProgress<BenchmarkProgress>? p, string phase,
            string msg, double mbps, int pass, int total, double bar, double seqFrac = -1, double seqTotalMB = 0)
            => p?.Report(new BenchmarkProgress
            {
                Phase = phase, Message = msg, CurrentMBps = mbps,
                PassNumber = pass, TotalPasses = total, BarFraction = bar,
                SeqFraction = seqFrac, SeqTotalMB = seqTotalMB
            });

        private static void TryDelete(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 2) AppLogger.Debug("TryDelete failed after 3 attempts: {0}", ex.Message);
                    else Thread.Sleep(150);
                }
            }
        }
    }

    // ── Legacy compatibility — kept for other references in the code ────────────────
    public class DiskBenchmarkResult
    {
        public double SeqReadMBs  { get; set; }
        public double SeqWriteMBs { get; set; }
        public string DriveLetter { get; set; } = "C:";
        public string Rating      { get; set; } = "";
        public string RatingColor { get; set; } = "#22C55E";
    }

    public class DiskBenchmarkProgress
    {
        public string Message     { get; set; } = "";
        public string Phase       { get; set; } = "";
        public double CurrentMBps { get; set; }
    }

    public class DiskIopsResult
    {
        public long   ReadIOPS    { get; set; }
        public long   WriteIOPS   { get; set; }
        public string DriveLetter { get; set; } = "C:";
    }

    // ── EoL Predictor (neschimbat) ────────────────────────────────────────────
    public class DiskEoLPredictor
    {
        /// <summary>
        /// SINGLE source for "% life remaining" on an SSD/NVMe — used both for the "Wear Level" pill on the disk card and for
        /// the end-of-life prediction, so two different percentages never appear for the same information.
        /// Standard SMART convention: CurrentValue is normalized 1-100, higher = better, and drops from 100 (new) towards the
        /// threshold as the disk wears. Dedicated "life left" attributes are preferred over RAW ones (RAW interpretation differs
        /// between manufacturers and can give wrong values, e.g. Wear Range Delta).
        /// </summary>
        public static int GetWearPercentRemaining(DiskHealthEntry disk)
        {
            if (disk?.SmartAttributes == null || disk.SmartAttributes.Count == 0) return -1;
            var smart = disk.SmartAttributes;
            SmartAttributeEntry? S(byte id) => smart.FirstOrDefault(a => a.Id == id);

            // 231 (0xE7) SSD Life Left, 233 (0xE9) Media Wearout Indicator,
            // 169 (0xA9) NAND Endurance — all normalized, higher = more life left.
            var lifeAttr = S(231) ?? S(233) ?? S(169);
            if (lifeAttr != null && lifeAttr.CurrentValue > 0 && lifeAttr.CurrentValue <= 100)
                return lifeAttr.CurrentValue;

            // Fallback: 177 (0xB1) Wear Range Delta / 230 (0xE6) — on some drives RawValue is % worn;
            // used only when there is no dedicated "life left" attribute.
            var usedAttr = S(177) ?? S(230);
            if (usedAttr != null && usedAttr.RawValue >= 0 && usedAttr.RawValue <= 100)
                return (int)Math.Max(0, 100 - usedAttr.RawValue);

            return -1;
        }

        public static (double yearsRemaining, string basis) Predict(DiskHealthEntry disk)
        {
            if (disk?.SmartAttributes == null || disk.SmartAttributes.Count == 0)
                return (-1, "");
            var smart = disk.SmartAttributes;
            SmartAttributeEntry? S(byte id) => smart.FirstOrDefault(a => a.Id == id);
            var pohAttr = S(9);
            long powerOnHours = pohAttr?.RawValue ?? 0;
            var tbwAttr = S(241) ?? S(242);
            long tbwGB = 0;
            if (tbwAttr != null)
            {
                long raw = tbwAttr.RawValue;
                tbwGB = raw / (2 * 1024 * 1024);
            }
            long reallocated   = S(5)?.RawValue   ?? 0;
            long pending       = S(197)?.RawValue  ?? 0;
            long uncorrectable = S(198)?.RawValue  ?? 0;
            int wearIndicator = GetWearPercentRemaining(disk);
            bool isSsd = disk.MediaType?.Contains("SSD",   StringComparison.OrdinalIgnoreCase) == true
                      || disk.MediaType?.Contains("NVMe",  StringComparison.OrdinalIgnoreCase) == true
                      || disk.MediaType?.Contains("Solid", StringComparison.OrdinalIgnoreCase) == true;
            double yearsRemaining = -1;
            string basis = "";
            if (isSsd)
            {
                if (wearIndicator > 0 && wearIndicator <= 100 && powerOnHours > 100)
                {
                    bool wearPlausible = !(wearIndicator < 5 && powerOnHours < 20000);
                    if (!wearPlausible) wearIndicator = -1; // forces the fallback to the TBW branch below
                }
                if (wearIndicator > 0 && wearIndicator <= 100 && powerOnHours > 100)
                {
                    double wearUsed = 100.0 - wearIndicator;
                    if (wearUsed > 0)
                    {
                        double hoursPerWearPct = powerOnHours / wearUsed;
                        yearsRemaining = hoursPerWearPct * wearIndicator / 8760.0;
                        basis = "based on SSD wear level";
                    }
                    else { yearsRemaining = 10; basis = "drive is new"; }
                }
                else if (tbwGB > 0 && powerOnHours > 100)
                {
                    bool plausible = !(powerOnHours > 1000 && tbwGB < 50);
                    if (plausible)
                    {
                        double dailyWriteGB = tbwGB / Math.Max(1, powerOnHours / 24.0);
                        double capacityGB   = ParseSizeToGB(disk.Size);
                        double ratedTbwGB   = capacityGB > 0 ? capacityGB * 4 : 1000;
                        double remainingGB  = ratedTbwGB - tbwGB;
                        if (remainingGB > 0 && dailyWriteGB > 0)
                        {
                            yearsRemaining = remainingGB / dailyWriteGB / 365.0;
                            basis = $"TBW written: {tbwGB:N0} GB, est. rated: {ratedTbwGB:N0} GB";
                        }
                    }
                }
                if (reallocated + pending + uncorrectable > 0)
                {
                    basis += $" — ⚠ {reallocated + pending + uncorrectable} bad sectors";
                    yearsRemaining = yearsRemaining > 0 ? yearsRemaining * 0.5 : 0.5;
                }

                if (yearsRemaining >= 0 && yearsRemaining <= 1.0
                    && disk.HealthPercent >= 80
                    && reallocated + pending + uncorrectable == 0)
                {
                    yearsRemaining = -1;
                    basis = "wear data inconsistent with reported health — prediction skipped";
                }
            }
            else
            {
                const double RatedHddHours = 35_000;
                if (powerOnHours > 0)
                {
                    yearsRemaining = Math.Max(0, RatedHddHours - powerOnHours) / 8760.0;
                    basis = $"Power-on hours: {powerOnHours:N0} h";
                    if (reallocated > 5 || uncorrectable > 0)
                    { yearsRemaining *= 0.4; basis += $" — ⚠ {reallocated} reallocated"; }
                    else if (reallocated > 0 || pending > 0)
                    { yearsRemaining *= 0.75; basis += $" — {reallocated} reallocated sectors"; }
                }
            }
            return (Math.Max(0, Math.Round(yearsRemaining, 1)), basis);
        }
        private static double ParseSizeToGB(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return 0;
            var parts = size.Trim().Split(' ');
            if (parts.Length < 2) return 0;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val)) return 0;
            return parts[1].ToUpperInvariant() switch
            { "TB" => val * 1024, "GB" => val, "MB" => val / 1024, _ => 0 };
        }
    }
}
