using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public sealed class PsuStressService
    {
        public bool   IsRunning   { get; private set; }
        private DateTime? _startedUtc;
        public DateTime? StartedUtc => IsRunning ? _startedUtc : null;
        public string StatusText  { get; private set; } = "";

        private static int _integrityErrorCount;
        public static int IntegrityErrorCount => _integrityErrorCount;

        private CancellationTokenSource? _cts;
        private Thread? _ramThread;
        private Thread? _diskThread;

        public void Start()
        {
            if (IsRunning) return;
            StressPriorityCoordinator.NotifyStressStarted();
            IsRunning  = true;
            _startedUtc = DateTime.UtcNow;
            StatusText = "Starting…";
            System.Threading.Interlocked.Exchange(ref _integrityErrorCount, 0);
            Volatile.Write(ref _ramFault, null);
            Volatile.Write(ref _diskFault, null);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // RAM — write+read loop over a large buffer (uses bandwidth + DRAM power).
            // The size adapts to free memory: at most 512 MB, but never over 25% of the available RAM.
            _ramThread = new Thread(() =>
            {
                try
                {
                    long availMB = RamFullScanService.GetAvailablePhysicalMB();
                    long targetMB = availMB > 0 ? Math.Min(512, availMB / 4) : 128;
                    if (targetMB < 64)
                    {
                        ReportFault(ref _ramFault, $"RAM: too little free memory ({availMB} MB) — RAM load skipped");
                        return;
                    }
                    int sz = (int)(targetMB * 1024 * 1024 / sizeof(int));
                    var buf = new int[sz];
                    while (!ct.IsCancellationRequested)
                    {
                        for (int i = 0; i < sz && !ct.IsCancellationRequested; i += 64)
                            buf[i] = i;
                        long sum = 0;
                        int mismatches = 0;
                        for (int i = 0; i < sz && !ct.IsCancellationRequested; i += 64)
                        {
                            int v = buf[i];
                            if (v != i) mismatches++;
                            sum += v;
                        }
                        if (mismatches > 0)
                        {
                            System.Threading.Interlocked.Add(ref _integrityErrorCount, mismatches);
                            AppLogger.Warning($"PsuStress.RamThread: {mismatches} RAM mismatches detected in a single pass. Session total: {_integrityErrorCount}.");
                        }
                        GC.KeepAlive(sum);
                    }
                }
                catch (ThreadInterruptedException) { }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "PsuStress.RamThread");
                    ReportFault(ref _ramFault, "RAM load stopped: " + ex.Message);
                }
            })
            // BelowNormal — the same principle as CpuStressor/GpuStressService: fills idle time, but the scheduler preempts it
            // instantly when the UI needs the CPU.
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PsuStress_RAM" };
            _ramThread.Start();

            _diskThread = new Thread(() => DiskLoop(ct))
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PsuStress_Disk" };
            _diskThread.Start();

            StatusText = "Running — RAM + Disk at maximum load";
        }

        // ── Disk ─────────────────────────────────────────────────────────
        // The goal is the storage drive's power draw, NOT its wear:
        //   • one 256 MB file written ONCE;
        //   • random 1 MB reads WITHOUT cache (NO_BUFFERING) — they load the disk, zero wear;
        //   • writes capped at MaxSessionWriteMB for the whole session;
        //   • free space checked first; any error becomes visible (DiskFault), never silent.
        private const int  DiskFileMB          = 256;
        private const int  DiskMinFreeMB       = 2048;
        private const long MaxSessionWriteMB   = 2048;
        private const int  ReadBlock           = 1024 * 1024;   // a multiple of 4096 (sector alignment)
        public  const string TempFilePrefix    = "psu_stress_";

        private static unsafe void DiskLoop(CancellationToken ct)
        {
            string tmpDir  = Path.GetTempPath();
            string tmpFile = Path.Combine(tmpDir, $"{TempFilePrefix}{Guid.NewGuid():N}.tmp");
            void* aligned = null;
            try
            {
                var root = Path.GetPathRoot(tmpDir);
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    long freeMB = di.AvailableFreeSpace / (1024 * 1024);
                    if (freeMB < DiskFileMB + DiskMinFreeMB)
                    {
                        ReportFault(ref _diskFault, $"Disk load skipped: only {freeMB} MB free on {root}");
                        return;
                    }
                }

                // 1) The working file, written only once
                byte[] chunk = new byte[4 * 1024 * 1024];
                new Random().NextBytes(chunk);
                long writtenBytes = 0;
                using (var fs = new FileStream(tmpFile, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, chunk.Length, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < DiskFileMB / 4 && !ct.IsCancellationRequested; i++)
                    {
                        fs.Write(chunk, 0, chunk.Length);
                        writtenBytes += chunk.Length;
                    }
                }
                if (ct.IsCancellationRequested) return;

                // 2) Uncached reads (buffer aligned to 4096, a NO_BUFFERING requirement) + rare writes, capped per session.
                aligned = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(ReadBlock, 4096);
                var span = new Span<byte>(aligned, ReadBlock);
                const FileOptions NoBuffering = (FileOptions)0x20000000;
                long blocks = (long)DiskFileMB * 1024 * 1024 / ReadBlock;
                var rnd = new Random();
                long iter = 0;
                using var readHandle = File.OpenHandle(tmpFile, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite, NoBuffering);
                while (!ct.IsCancellationRequested)
                {
                    long offset = rnd.NextInt64(blocks) * ReadBlock;
                    RandomAccess.Read(readHandle, span, offset);

                    // one 4 MB write every ~64 reads, only while the session cap has not been reached
                    if (++iter % 64 == 0 && writtenBytes < MaxSessionWriteMB * 1024 * 1024)
                    {
                        using var fs = new FileStream(tmpFile, FileMode.Open, FileAccess.Write,
                                           FileShare.ReadWrite, chunk.Length, FileOptions.WriteThrough);
                        fs.Position = rnd.NextInt64(DiskFileMB / 4) * chunk.Length;
                        fs.Write(chunk, 0, chunk.Length);
                        writtenBytes += chunk.Length;
                    }
                }
                AppLogger.Info("PsuStress.Disk: stopped; written this session {0} MB (cap {1} MB)",
                    writtenBytes / (1024 * 1024), MaxSessionWriteMB);
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "PsuStress.DiskThread");
                ReportFault(ref _diskFault, "Disk load stopped: " + ex.Message);
            }
            finally
            {
                if (aligned != null) System.Runtime.InteropServices.NativeMemory.AlignedFree(aligned);
                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
        }

        // Worker thread errors, read by the UI (a single notification per session).
        private static string? _ramFault, _diskFault;
        public string? RamFault  => Volatile.Read(ref _ramFault);
        public string? DiskFault => Volatile.Read(ref _diskFault);

        private static void ReportFault(ref string? slot, string message)
        {
            Volatile.Write(ref slot, message);
            AppLogger.Warning("PsuStress: {0}", message);
        }

        /// <summary>Deletes the work files left after a session that was interrupted abruptly (crash / kill).</summary>
        public static void SweepOrphanedTempFiles()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), TempFilePrefix + "*.tmp"))
                {
                    try { File.Delete(f); AppLogger.Info("PsuStress: orphan file deleted: {0}", f); }
                    catch (Exception ex) { AppLogger.Debug("PsuStress: orphan locked {0}: {1}", f, ex.Message); }
                }
            }
            catch (Exception ex) { AppLogger.Debug("PsuStress.SweepOrphanedTempFiles: {0}", ex.Message); }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            StressPriorityCoordinator.NotifyStressStopped();
            _startedUtc = null;
            IsRunning  = false;   // instant — UI vede oprit imediat
            StatusText = "Stopping…";

            var cts        = _cts;
            var ramThread   = _ramThread;
            var diskThread  = _diskThread;
            _cts        = null;
            _ramThread  = null;
            _diskThread = null;

            try { cts?.Cancel(); } catch (Exception ex) { AppLogger.Debug($"PsuStress.Stop.Cancel: {ex.Message}"); }

            Task.Run(() =>
            {
                try { ramThread?.Interrupt(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { diskThread?.Interrupt(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { ramThread?.Join(3000); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { diskThread?.Join(3000); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try { cts?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                if (!IsRunning) StatusText = "Stopped";   // does not overwrite a run started in the meantime
            });
        }
    }
}
