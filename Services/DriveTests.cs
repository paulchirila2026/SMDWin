using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SMDWin.Services
{
    /// <summary>
    /// The deterministic test data: every 8-byte word depends only on its position on the drive and a seed, so a block that
    /// comes back from another place (a fake stick that wraps around) or damaged is recognised. SplitMix64 per word.
    /// </summary>
    public static class DrivePattern
    {
        public static ulong Word(ulong seed, long position)
        {
            ulong z = seed + (ulong)position * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public static void Fill(Span<byte> buf, long offset, ulong seed)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(buf);
            for (int i = 0; i < words.Length; i++) words[i] = Word(seed, offset + i * 8L);
        }

        /// <summary>Index (in bytes, inside the buffer) of the first word that is not what was written; -1 = all correct.</summary>
        public static long FirstMismatch(ReadOnlySpan<byte> buf, long offset, ulong seed)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(buf);
            for (int i = 0; i < words.Length; i++)
                if (words[i] != Word(seed, offset + i * 8L)) return i * 8L;
            return -1;
        }
    }

    public sealed class DriveTestProgress
    {
        public string Phase = "";
        public double Percent;
        public double MBps;
        public double ElapsedSec;
        public double EtaSec;
        public double DoneGB;
    }

    public sealed record CapacityResult(bool Completed, bool Fake, double TestedGB, double GoodGB, double WriteMBps, double ReadMBps, string Error);
    public sealed record SustainedResult(bool Completed, List<(double GB, double MBps)> Samples, double FastMBps, double SlowMBps, double CacheGB, bool CacheFound, string Error);
    public sealed record AccessResult(bool Completed, double AvgMs, double MedianMs, double P95Ms, int Over100Ms, int Count, string Error);

    /// <summary>
    /// The drive tests that need more than SMART:
    ///  • real capacity (USB sticks, memory cards): fills the free space with known data, reads it back past the cache and finds
    ///    where the data stops being correct — a fake stick reports more space than it has;
    ///  • sustained write: writes one big file and watches the speed per second — shows how big the fast cache of an SSD is and
    ///    the speed after it ("fast at first, then slow");
    ///  • access time (hard disks): random reads all over the disk — how long the head needs to get anywhere; worn mechanics
    ///    show up here even when SMART says all is well. Read-only.
    /// The test files are always deleted at the end, also when the test is stopped or fails.
    /// </summary>
    public static class DriveTests
    {
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, CREATE_ALWAYS = 2;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        public const string FilePrefix = "SMDWin_DriveTest_";
        private const int Block = 4 * 1024 * 1024;                 // 4 MB, a multiple of every sector size
        private const long FileBytes = 1L << 30;                   // 1 GB per file (FAT32-safe)

        private static unsafe Span<byte> Aligned(out IntPtr mem, int size)
        {
            void* p = NativeMemory.AlignedAlloc((nuint)size, 4096);
            mem = (IntPtr)p;
            return new Span<byte>(p, size);
        }

        private static unsafe void FreeAligned(IntPtr mem) { if (mem != IntPtr.Zero) NativeMemory.AlignedFree((void*)mem); }

        private static void DeleteTestFiles(string root)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, FilePrefix + "*.bin"))
                    try { File.Delete(f); } catch (Exception ex) { AppLogger.Warning(ex, "DriveTests delete " + f); }
            }
            catch { }
        }

        private static SafeFileHandle OpenWrite(string path) =>
            CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);

        private static SafeFileHandle OpenRead(string path) =>
            CreateFile(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);

        private static void Check(SafeFileHandle h, string what)
        {
            if (h.IsInvalid) throw new IOException($"{what}: Win32 error {Marshal.GetLastWin32Error()}");
        }

        // ── real capacity ────────────────────────────────────────────────────────────────────────────────────────────────
        public static Task<CapacityResult> CapacityCheckAsync(string root, IProgress<DriveTestProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            IntPtr mem = IntPtr.Zero;
            ulong seed = (ulong)DateTime.UtcNow.Ticks;
            long total = 0;
            double writeSec = 0, readSec = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                DeleteTestFiles(root);
                var di = new DriveInfo(root);
                total = (di.AvailableFreeSpace - 16L * 1024 * 1024) / Block * Block;   // leave 16 MB, whole blocks
                if (total < Block) return new CapacityResult(false, false, 0, 0, 0, 0, "Not enough free space for the test.");
                var buf = Aligned(out mem, Block);

                // 1) write
                long written = 0; int fileNo = 0;
                var wsw = Stopwatch.StartNew();
                while (written < total && !ct.IsCancellationRequested)
                {
                    string path = System.IO.Path.Combine(root, $"{FilePrefix}{fileNo:D4}.bin");
                    using var h = OpenWrite(path); Check(h, "create");
                    long inFile = 0;
                    while (inFile < FileBytes && written < total && !ct.IsCancellationRequested)
                    {
                        DrivePattern.Fill(buf, written, seed);
                        RandomAccess.Write(h, buf, inFile);
                        inFile += Block; written += Block;
                        double s = wsw.Elapsed.TotalSeconds;
                        progress.Report(new DriveTestProgress { Phase = "write", Percent = 50.0 * written / total, DoneGB = written / 1e9,
                                                                MBps = written / 1048576.0 / Math.Max(s, 0.001), ElapsedSec = sw.Elapsed.TotalSeconds,
                                                                EtaSec = s / Math.Max(written, 1) * (total - written) + s / Math.Max(written, 1) * total * 0.6 });
                    }
                    fileNo++;
                }
                writeSec = wsw.Elapsed.TotalSeconds;
                if (ct.IsCancellationRequested) return new CapacityResult(false, false, written / 1e9, 0, 0, 0, "");

                // 2) read back, past the cache (no buffering), and compare
                long verified = 0, firstBad = -1;
                var rsw = Stopwatch.StartNew();
                for (int i = 0; i < fileNo && firstBad < 0 && !ct.IsCancellationRequested; i++)
                {
                    string path = System.IO.Path.Combine(root, $"{FilePrefix}{i:D4}.bin");
                    using var h = OpenRead(path); Check(h, "open");
                    long len = RandomAccess.GetLength(h), inFile = 0;
                    while (inFile < len && !ct.IsCancellationRequested)
                    {
                        int n = RandomAccess.Read(h, buf, inFile);
                        if (n < Block) { firstBad = verified + Math.Max(0, n); break; }
                        long bad = DrivePattern.FirstMismatch(buf, verified, seed);
                        if (bad >= 0) { firstBad = verified + bad; break; }
                        inFile += Block; verified += Block;
                        double s = rsw.Elapsed.TotalSeconds;
                        progress.Report(new DriveTestProgress { Phase = "verify", Percent = 50 + 50.0 * verified / written, DoneGB = verified / 1e9,
                                                                MBps = verified / 1048576.0 / Math.Max(s, 0.001), ElapsedSec = sw.Elapsed.TotalSeconds,
                                                                EtaSec = s / Math.Max(verified, 1) * (written - verified) });
                    }
                }
                readSec = rsw.Elapsed.TotalSeconds;
                if (ct.IsCancellationRequested) return new CapacityResult(false, false, written / 1e9, verified / 1e9, written / 1048576.0 / Math.Max(writeSec, 0.001), 0, "");
                bool fake = firstBad >= 0;
                double good = (fake ? firstBad : verified) / 1e9;
                AppLogger.Info($"Capacity check {root}: written {written / 1e9:F1} GB, good {good:F2} GB, fake {fake}");
                return new CapacityResult(true, fake, written / 1e9, good, written / 1048576.0 / Math.Max(writeSec, 0.001),
                                          verified / 1048576.0 / Math.Max(readSec, 0.001), "");
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "CapacityCheck");
                return new CapacityResult(false, false, total / 1e9, 0, 0, 0, ex.Message);
            }
            finally
            {
                FreeAligned(mem);
                DeleteTestFiles(root);
            }
        }, ct);

        // ── sustained write ──────────────────────────────────────────────────────────────────────────────────────────────
        public static Task<SustainedResult> SustainedWriteAsync(string root, long maxBytes, TimeSpan maxTime, IProgress<DriveTestProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            IntPtr mem = IntPtr.Zero;
            var samples = new List<(double GB, double MBps)>();
            try
            {
                DeleteTestFiles(root);
                var di = new DriveInfo(root);
                // never fill the drive: at most what is asked, and always leave 10% of the drive + 5 GB free
                long room = di.AvailableFreeSpace - di.TotalSize / 10 - 5L * 1024 * 1024 * 1024;
                long limit = Math.Min(maxBytes, room) / Block * Block;
                if (limit < 2L * 1024 * 1024 * 1024) return new SustainedResult(false, samples, 0, 0, 0, false, "Not enough free space for the test (it needs at least 2 GB plus 10% of the drive free).");
                var buf = Aligned(out mem, Block);
                DrivePattern.Fill(buf, 0, 12345);   // incompressible data, like real files
                string path = System.IO.Path.Combine(root, $"{FilePrefix}sustained.bin");
                using var h = OpenWrite(path); Check(h, "create");
                var sw = Stopwatch.StartNew();
                long written = 0, lastBytes = 0; double lastT = 0;
                while (written < limit && sw.Elapsed < maxTime && !ct.IsCancellationRequested)
                {
                    RandomAccess.Write(h, buf, written);
                    written += Block;
                    double t = sw.Elapsed.TotalSeconds;
                    if (t - lastT >= 0.5)
                    {
                        double mbps = (written - lastBytes) / 1048576.0 / (t - lastT);
                        samples.Add((written / 1e9, mbps));
                        lastBytes = written; lastT = t;
                        progress.Report(new DriveTestProgress { Phase = "write", Percent = Math.Max(100.0 * written / limit, 100.0 * t / maxTime.TotalSeconds),
                                                                MBps = mbps, DoneGB = written / 1e9, ElapsedSec = t });
                    }
                }
                bool done = !ct.IsCancellationRequested;
                var (fast, slow, cache, found) = AnalyzeSustained(samples);
                AppLogger.Info($"Sustained write {root}: {written / 1e9:F1} GB, fast {fast:F0} MB/s, after {slow:F0} MB/s, cache {(found ? cache.ToString("F1") + " GB" : "not reached")}");
                return new SustainedResult(done, samples, fast, slow, cache, found, "");
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SustainedWrite");
                return new SustainedResult(false, samples, 0, 0, 0, false, ex.Message);
            }
            finally
            {
                FreeAligned(mem);
                DeleteTestFiles(root);
            }
        }, ct);

        /// <summary>
        /// The fast part = median of the first samples; the cache ends where the speed falls under half of that and stays
        /// there (most of the next 10 samples). Returns (fast, after-cache median, GB written before the drop, drop found).
        /// </summary>
        public static (double Fast, double Slow, double CacheGB, bool Found) AnalyzeSustained(IReadOnlyList<(double GB, double MBps)> s)
        {
            if (s.Count < 6) return (s.Count > 0 ? s.Average(x => x.MBps) : 0, 0, 0, false);
            double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
            double fast = Median(s.Skip(1).Take(Math.Min(10, s.Count - 1)).Select(x => x.MBps));
            for (int i = 4; i < s.Count - 4; i++)
            {
                if (s[i].MBps >= fast * 0.5) continue;
                var next = s.Skip(i).Take(10).ToList();
                if (next.Count(x => x.MBps < fast * 0.6) >= next.Count * 0.8)
                    return (fast, Median(s.Skip(i).Select(x => x.MBps)), s[i].GB, true);
            }
            return (fast, 0, 0, false);
        }

        // ── access time (random reads over the whole disk) ───────────────────────────────────────────────────────────────
        public static Task<AccessResult> AccessTimeAsync(string physicalPath, ulong sizeBytes, int count, IProgress<DriveTestProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            IntPtr mem = IntPtr.Zero;
            try
            {
                using var h = CreateFile(physicalPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
                Check(h, "open");
                var buf = Aligned(out mem, 4096);
                var rnd = new Random();
                long span = (long)(sizeBytes / 4096) - 2;
                if (span < 1000) return new AccessResult(false, 0, 0, 0, 0, 0, "The disk size is not known.");
                var times = new List<double>(count);
                var sw = new Stopwatch();
                for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                {
                    long off = (long)(rnd.NextDouble() * span) * 4096;
                    sw.Restart();
                    RandomAccess.Read(h, buf, off);
                    sw.Stop();
                    if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);   // the first reads wake the disk up
                    if (i % 10 == 0)
                        progress.Report(new DriveTestProgress { Phase = "read", Percent = 100.0 * i / count, MBps = times.Count > 0 ? times.Average() : 0 });
                }
                if (times.Count < 20) return new AccessResult(false, 0, 0, 0, 0, times.Count, "");
                var sorted = times.OrderBy(x => x).ToArray();
                // the average without the worst 2% (a single pause of the system should not decide the verdict)
                var core = sorted.Take((int)(sorted.Length * 0.98)).ToArray();
                var r = new AccessResult(!ct.IsCancellationRequested, core.Average(), sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.95)],
                                         sorted.Count(x => x > 100), sorted.Length, "");
                AppLogger.Info($"Access time {physicalPath}: avg {r.AvgMs:F1} ms, median {r.MedianMs:F1}, p95 {r.P95Ms:F1}, >100 ms {r.Over100Ms}");
                return r;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "AccessTime");
                return new AccessResult(false, 0, 0, 0, 0, 0, ex.Message);
            }
            finally { FreeAligned(mem); }
        }, ct);
    }
}
