using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class CpuStressor
    {
        private CancellationTokenSource? _cts;
        private readonly List<Thread> _threads = new();
        private readonly object _stateLock = new();

        private static int _integrityErrorCount;
        public static int IntegrityErrorCount => _integrityErrorCount;

        public bool Running
        {
            get
            {
                lock (_stateLock)
                    return _cts != null && !_cts.IsCancellationRequested && _threads.Count > 0;
            }
        }

        private DateTime? _startedUtc;
        public DateTime? StartedUtc { get { lock (_stateLock) return _startedUtc; } }

        private static volatile int _intensityPct = 100;
        public int IntensityPct
        {
            get => _intensityPct;
            set => _intensityPct = Math.Max(10, Math.Min(100, value));
        }

        /// <summary>
        /// Starts the test threads at minimum priority: they use all the processor's free time, but do not take it from Windows
        /// services and the interface. Callers leave at least one thread free (<paramref name="reserveCores"/>): with all threads
        /// busy, sensor reads (and therefore the interface) fell ~10 s behind whatever the test priority, without raising the
        /// temperature or frequency.
        /// </summary>
        /// <param name="reserveCores">Threads left free: 1 for the processor test, 2 for "System" (plus the RAM and disk work).</param>
        public void Start(int workers, int reserveCores = 0)
        {
            lock (_stateLock)
            {
                if (Running) return;
                _startedUtc = DateTime.UtcNow;
                StressPriorityCoordinator.NotifyStressStarted();
                workers = Math.Max(1, Math.Min(workers, Environment.ProcessorCount) - Math.Max(0, reserveCores));
                System.Threading.Interlocked.Exchange(ref _integrityErrorCount, 0);
                _cts = new CancellationTokenSource();
                _threads.Clear();
                for (int i = 0; i < workers; i++)
                {
                    var token = _cts.Token;
                    int rampMs = i * 150;   // cores join one by one: the load rises in ~1 s, the interface is not starved at the start
                    try
                    {
                        var t = new Thread(() => { if (rampMs > 0) token.WaitHandle.WaitOne(rampMs); if (!token.IsCancellationRequested) BurnLoop(token); })
                        {
                            IsBackground = true,
                            Priority     = ThreadPriority.Lowest,   // below the rest of the system: the stress uses all free time, but does not take the processor from Windows services and the interface
                            Name         = $"SMDWin-CpuStress-{i}"
                        };
                        t.Start();
                        _threads.Add(t);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[StressCpu] Failed to start worker {i}: {ex.Message}");
                    }
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource? oldCts;
            List<Thread> workersToJoin;
            lock (_stateLock)
            {
                oldCts = _cts;
                _cts   = null;
                _startedUtc = null;
                workersToJoin = new List<Thread>(_threads);
                _threads.Clear();
            }
            // Cancel outside the lock — thread-safe, signals BurnLoop to exit
            try { oldCts?.Cancel(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            if (oldCts != null) StressPriorityCoordinator.NotifyStressStopped();

            if (workersToJoin.Count > 0)
            {
                Task.Run(() =>
                {
                    foreach (var t in workersToJoin)
                    {
                        try
                        {
                            if (!t.Join(2000))
                            {
                                try { t.Interrupt(); } catch (Exception logEx) { AppLogger.Warning(logEx, "CpuStressor.Stop.Interrupt"); }
                                if (!t.Join(500))
                                    AppLogger.Warning($"CpuStressor: worker '{t.Name}' did not exit after Interrupt — process will clean up on exit");
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "CpuStressor.Stop.Join"); }
                    }
                });
            }
            try { oldCts?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        private static volatile float _fmaSink;

        private static bool IsFinite(Vector256<float> v)
        {
            for (int i = 0; i < 8; i++)
                if (float.IsNaN(v.GetElement(i)) || float.IsInfinity(v.GetElement(i))) return false;
            return true;
        }

        private static bool HasRealCorruption(Vector256<float> v)
        {
            for (int i = 0; i < 8; i++)
                if (float.IsNaN(v.GetElement(i))) return true;
            return false;
        }

        private const int IntKernelBufLen = 16 * 1024; // 16K x long = 128 KB per copie
        private const long IntKernelMul = 6364136223846793005L; // odd — an LCG with a full period
        private const long IntKernelAdd = 1442695040888963407L;

        private static void RunIntKernelSlice(long[] bufA, long[] bufB, int elapsedMsLimit, CancellationToken token, System.Diagnostics.Stopwatch slice)
        {
            while (slice.ElapsedMilliseconds < elapsedMsLimit)
            {
                for (int i = 0; i < IntKernelBufLen; i++)
                {
                    bufA[i] = unchecked(bufA[i] * IntKernelMul + IntKernelAdd);
                    bufB[i] = unchecked(bufB[i] * IntKernelMul + IntKernelAdd);
                }
                if (token.IsCancellationRequested) break;
            }
        }

        private static void BurnLoop(CancellationToken token)
        {
            try
            {
                bool useFma = Fma.IsSupported;

                // Resources for the SHA fallback — allocated only if needed
                byte[]? data = null;
                SHA256? sha  = null;
                if (!useFma)
                {
                    data = new byte[1024 * 1024];
                    new Random().NextBytes(data);
                    sha = SHA256.Create();
                }
                Span<byte> hashBuf = stackalloc byte[32]; // SHA-256 = 32 bytes

                var a = Vector256.Create(1.0000001f);
                var b = Vector256.Create(0.0000001f);
                var v0 = Vector256.Create(1.10f);
                var v1 = Vector256.Create(1.21f);
                var v2 = Vector256.Create(1.32f);
                var v3 = Vector256.Create(1.43f);
                var v4 = Vector256.Create(1.54f);
                var v5 = Vector256.Create(1.65f);
                var v6 = Vector256.Create(1.76f);
                var v7 = Vector256.Create(1.87f);
                // 12 independent chains: a multiply-add takes ~4-5 cycles and two can start per cycle, so ~10 must be in flight for
                // the units to never wait (8 left ~20 % of them idle on Haswell — lower power than OCCT). 12 + 2 constants fit the
                // 16 vector registers.
                var v8  = Vector256.Create(1.98f);
                var v9  = Vector256.Create(2.09f);
                var v10 = Vector256.Create(2.20f);
                var v11 = Vector256.Create(2.31f);

                var intSeedRnd = new Random(Environment.CurrentManagedThreadId * 104729 + 7);
                var intBufA = new long[IntKernelBufLen];
                var intBufB = new long[IntKernelBufLen];
                for (int i = 0; i < IntKernelBufLen; i++)
                {
                    long v = intSeedRnd.NextInt64();
                    intBufA[i] = v; intBufB[i] = v;
                }

                var slice = new System.Diagnostics.Stopwatch();
                while (!token.IsCancellationRequested)
                {
                    int pct = _intensityPct;

                    // The integer check runs 2 s of every 30 s: it verifies another part of the processor, but it is much lighter
                    // than the vector work — at half of the time (as before) the power and the temperature kept dropping every 20 s.
                    const long KernelPeriodMs = 30_000, IntKernelMs = 2_000;
                    bool intKernelTurn = (Environment.TickCount64 % KernelPeriodMs) < IntKernelMs;

                    // ── Work slice: exactly pct ms, measured ──────────────────
                    slice.Restart();
                    if (intKernelTurn)
                    {
                        RunIntKernelSlice(intBufA, intBufB, pct, token, slice);

                        // Lockstep check — see the comment at RunIntKernelSlice.
                        // A full buffer comparison costs little compared with the millions of multiplications in the slice above.
                        bool mismatch = false;
                        for (int i = 0; i < IntKernelBufLen; i++)
                            if (intBufA[i] != intBufB[i]) { mismatch = true; break; }
                        if (mismatch)
                        {
                            System.Threading.Interlocked.Increment(ref _integrityErrorCount);
                            // Resynchronize both copies on A (resynchronizing on A is enough: the error has already been recorded and the test
                            // continues, exactly as with the FMA kernel).
                            Array.Copy(intBufA, intBufB, IntKernelBufLen);
                        }
                    }
                    else if (useFma)
                    {
                        while (slice.ElapsedMilliseconds < pct)
                        {
                            // ~4096 iterations × 8 FMAs ≈ a few tens of µs per block — fine granularity for checking the clock/token without
                            // measurable overhead on the hot loop.
                            for (int i = 0; i < 4096; i++)
                            {
                                v0 = Fma.MultiplyAdd(v0, a, b);
                                v1 = Fma.MultiplyAdd(v1, a, b);
                                v2 = Fma.MultiplyAdd(v2, a, b);
                                v3 = Fma.MultiplyAdd(v3, a, b);
                                v4 = Fma.MultiplyAdd(v4, a, b);
                                v5 = Fma.MultiplyAdd(v5, a, b);
                                v6 = Fma.MultiplyAdd(v6, a, b);
                                v7 = Fma.MultiplyAdd(v7, a, b);
                                v8  = Fma.MultiplyAdd(v8,  a, b);
                                v9  = Fma.MultiplyAdd(v9,  a, b);
                                v10 = Fma.MultiplyAdd(v10, a, b);
                                v11 = Fma.MultiplyAdd(v11, a, b);
                            }
                            if (token.IsCancellationRequested) break;
                        }
                        // Sink — the result "escapes" the loop, so the JIT cannot eliminate the computation
                        _fmaSink = v0.GetElement(0) + v3.GetElement(1) + v7.GetElement(2) + v11.GetElement(3);

                        if (!IsFinite(v0) || !IsFinite(v1) || !IsFinite(v2) || !IsFinite(v3) ||
                            !IsFinite(v4) || !IsFinite(v5) || !IsFinite(v6) || !IsFinite(v7) ||
                            !IsFinite(v8) || !IsFinite(v9) || !IsFinite(v10) || !IsFinite(v11))
                        {
                            if (HasRealCorruption(v0) || HasRealCorruption(v1) || HasRealCorruption(v2) || HasRealCorruption(v3) ||
                                HasRealCorruption(v4) || HasRealCorruption(v5) || HasRealCorruption(v6) || HasRealCorruption(v7) ||
                                HasRealCorruption(v8) || HasRealCorruption(v9) || HasRealCorruption(v10) || HasRealCorruption(v11))
                            {
                                System.Threading.Interlocked.Increment(ref _integrityErrorCount);
                            }
                            v0 = Vector256.Create(1.10f); v1 = Vector256.Create(1.21f);
                            v2 = Vector256.Create(1.32f); v3 = Vector256.Create(1.43f);
                            v4 = Vector256.Create(1.54f); v5 = Vector256.Create(1.65f);
                            v6 = Vector256.Create(1.76f); v7 = Vector256.Create(1.87f);
                            v8 = Vector256.Create(1.98f); v9 = Vector256.Create(2.09f);
                            v10 = Vector256.Create(2.20f); v11 = Vector256.Create(2.31f);
                        }
                    }
                    else
                    {
                        while (slice.ElapsedMilliseconds < pct)
                        {
                            sha!.TryComputeHash(data, hashBuf, out _);
                            if (token.IsCancellationRequested) break;
                        }
                    }

                    // ── Sleep slice: the rest of the 100 ms period ────────────────
                    if (pct < 100 && !token.IsCancellationRequested)
                        Thread.Sleep(100 - pct);
                }
                sha?.Dispose();
            }
            catch (OperationCanceledException) { /* normal stop */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StressCpu] BurnLoop exception: {ex.Message}");
                // Do NOT rethrow — unhandled on a background thread would crash the app
            }
        }

        public static double Benchmark(double durationSec, out int hashMismatches)
        {
            int cores = Environment.ProcessorCount;
            var counts = new long[cores];
            var mismatchCounts = new long[cores];
            var barrier = new System.Threading.Barrier(cores + 1); // sincronizare start
            var deadline = DateTime.UtcNow.AddSeconds(durationSec + 0.05); // a little headroom

            var threads = new System.Threading.Thread[cores];
            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    var data = new byte[65536];
                    new Random(idx * 7919 + 1).NextBytes(data); // fixed seed, deterministic per thread
                    using var sha = SHA256.Create();
                    byte[]? referenceHash = null;
                    long c = 0, localMismatches = 0;
                    barrier.SignalAndWait(); // all start at the same time
                    while (DateTime.UtcNow < deadline)
                    {
                        byte[] h = sha.ComputeHash(data);
                        if (referenceHash == null) referenceHash = h;
                        else if (!h.AsSpan().SequenceEqual(referenceHash)) localMismatches++;
                        c++;
                    }
                    counts[idx] = c;
                    mismatchCounts[idx] = localMismatches;
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };   // below the interface: the window stays responsive, the idle processor time (the score) is the same
                threads[idx].Start();
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            barrier.SignalAndWait(); // start signal
            foreach (var t in threads) t.Join();
            sw.Stop();

            hashMismatches = (int)Math.Min(int.MaxValue, mismatchCounts.Sum());
            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        public static double Benchmark(double durationSec = 3.0) => Benchmark(durationSec, out _);


        public static double BenchmarkSingleCore(double durationSec, out int hashMismatches)
        {
            var data = new byte[65536];
            new Random(42).NextBytes(data);
            using var sha = SHA256.Create();
            byte[]? referenceHash = null;
            int mismatches = 0;
            long count = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < durationSec)
            {
                byte[] h = sha.ComputeHash(data);
                if (referenceHash == null) referenceHash = h;
                else if (!h.AsSpan().SequenceEqual(referenceHash)) mismatches++;
                count++;
            }
            sw.Stop();
            hashMismatches = mismatches;
            return count / sw.Elapsed.TotalSeconds;
        }

        // Overload without out — kept for any existing call that does not need the correctness check (e.g. code that only
        // measures speed).
        public static double BenchmarkSingleCore(double durationSec = 2.0)
            => BenchmarkSingleCore(durationSec, out _);

        // ── Mandelbrot benchmark — floating-point intensive, stresses FPU & IPC ──────────
        // Returns iterations/second (higher = better). Each thread renders a slice of the set.
        /// <param name="threadCount">0 = one thread per logical processor; 1 = the single-thread test of the Benchmark page.</param>
        public static double BenchmarkMandelbrot(double durationSec = 3.0, int threadCount = 0)
        {
            int cores = threadCount > 0 ? threadCount : Environment.ProcessorCount;
            const int Width = 1024, Height = 1024, MaxIter = 256;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var counts = new long[cores];
            var threads = new System.Threading.Thread[cores];

            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                int rowStart = idx * (Height / cores);
                int rowEnd   = (idx == cores - 1) ? Height : rowStart + (Height / cores);
                threads[idx] = new System.Threading.Thread(() =>
                {
                    long c = 0;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        for (int py = rowStart; py < rowEnd; py++)
                        {
                            double cy = -2.0 + py * 4.0 / Height;
                            for (int px = 0; px < Width; px++)
                            {
                                double cx = -2.5 + px * 4.0 / Width;
                                double x = 0, y = 0;
                                int iter = 0;
                                while (x * x + y * y <= 4.0 && iter < MaxIter)
                                {
                                    double xtemp = x * x - y * y + cx;
                                    y = 2.0 * x * y + cy;
                                    x = xtemp;
                                    iter++;
                                }
                                c++;
                            }
                        }
                    }
                    counts[idx] = c;
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };   // below the interface: the window stays responsive, the idle processor time (the score) is the same
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// The Benchmark page's Mandelbrot: returns WORK per second — the iterations done (+1 per pixel for the
        /// pixel's own cost) — instead of pixels per second. With pixels, the threads that drew the top and bottom quarters
        /// (almost no point of the set: ~2 iterations a pixel) counted 25× more pixels than the middle ones (~53), so "all
        /// cores" came out ~12× "one core" on a 4-core i5. Every thread also draws interleaved rows (row = i, i + n, …), so all
        /// threads get the same mix, and the time is checked after every row, not after a whole picture.
        /// </summary>
        /// <param name="perSecond">optional: the work done in each second (index = second), for "keeps X% of its speed".</param>
        public static double BenchmarkMandelbrotWork(double durationSec, int threadCount = 0, long[]? perSecond = null)
        {
            int cores = threadCount > 0 ? threadCount : Environment.ProcessorCount;
            const int Width = 1024, Height = 1024, MaxIter = 256;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var work = new long[cores];
            var threads = new System.Threading.Thread[cores];
            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    long w = 0, lastW = 0;
                    int py = idx;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        double cy = -2.0 + py * 4.0 / Height;
                        for (int px = 0; px < Width; px++)
                        {
                            double cx = -2.5 + px * 4.0 / Width;
                            double x = 0, y = 0;
                            int iter = 0;
                            while (x * x + y * y <= 4.0 && iter < MaxIter)
                            {
                                double xtemp = x * x - y * y + cx;
                                y = 2.0 * x * y + cy;
                                x = xtemp;
                                iter++;
                            }
                            w += iter + 1;
                        }
                        if (perSecond != null)
                        {
                            int sec = (int)sw.Elapsed.TotalSeconds;
                            long rowWork = w - lastW; lastW = w;
                            if (sec < perSecond.Length) System.Threading.Interlocked.Add(ref perSecond[sec], rowWork);
                        }
                        py += cores;
                        if (py >= Height) py = idx;
                    }
                    work[idx] = w;
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };   // below the interface: the window stays responsive, the idle processor time (the score) is the same
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            return work.Sum() / sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// The Benchmark page's everyday test: every thread compresses the same 4 MB of text-like data (words,
        /// numbers, punctuation — compresses about 3:1, like documents and logs) with Deflate, again and again. Returns MB of
        /// input compressed per second, all threads together.
        /// </summary>
        public static double BenchmarkCompression(double durationSec, int threadCount = 0)
        {
            int cores = threadCount > 0 ? threadCount : Environment.ProcessorCount;
            byte[] data = CompressionSample(4 * 1024 * 1024);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bytes = new long[cores];
            var threads = new System.Threading.Thread[cores];
            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    using var ms = new System.IO.MemoryStream(data.Length);
                    long b = 0;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        ms.SetLength(0);
                        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        {
                            // in 256 KB pieces, so the time is checked often enough on slow processors
                            for (int off = 0; off < data.Length && sw.Elapsed.TotalSeconds < durationSec; off += 256 * 1024)
                            {
                                int n = Math.Min(256 * 1024, data.Length - off);
                                z.Write(data, off, n);
                                b += n;
                            }
                        }
                    }
                    bytes[idx] = b;
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };   // below the interface: the window stays responsive, the idle processor time (the score) is the same
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            return bytes.Sum() / 1e6 / sw.Elapsed.TotalSeconds;
        }

        private static byte[] CompressionSample(int size)
        {
            string[] words = { "the", "system", "driver", "update", "memory", "disk", "error", "service", "network", "windows", "file",
                               "temperature", "processor", "battery", "report", "version", "device", "user", "time", "status", "and", "of",
                               "to", "in", "is", "for", "with", "on", "ok", "warning", "data", "value", "read", "write", "start", "stop" };
            var rnd = new Random(1987);
            var sb = new System.Text.StringBuilder(size + 64);
            while (sb.Length < size)
            {
                int k = rnd.Next(100);
                if (k < 70) sb.Append(words[rnd.Next(words.Length)]);
                else if (k < 85) sb.Append(rnd.Next(100000));
                else sb.Append((char)('a' + rnd.Next(26))).Append((char)('a' + rnd.Next(26))).Append((char)('a' + rnd.Next(26)));
                sb.Append(rnd.Next(12) == 0 ? ".\n" : rnd.Next(8) == 0 ? ", " : " ");
            }
            return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, size));
        }

        // ── FFT benchmark — memory-bandwidth + compute, stresses cache hierarchy ──────────
        // Returns FFT-ops/second (each op = FFT of 4096 complex doubles).
        public static double BenchmarkFFT(double durationSec, out int fftMismatches) => BenchmarkFFT(durationSec, 0, out fftMismatches);

        /// <param name="threadCount">0 = one thread per logical processor; 1 = single thread.</param>
        public static double BenchmarkFFT(double durationSec, int threadCount, out int fftMismatches)
        {
            int cores = threadCount > 0 ? threadCount : Environment.ProcessorCount;
            const int N = 4096;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var counts = new long[cores];
            var mismatchCounts = new long[cores];
            var threads = new System.Threading.Thread[cores];

            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    // Allocate per-thread FFT buffer
                    var re = new double[N];
                    var im = new double[N];
                    var rnd = new Random(idx * 31337);
                    for (int k = 0; k < N; k++) { re[k] = rnd.NextDouble(); im[k] = 0; }

                    long c = 0;
                    long localMismatches = 0;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        double energyBefore = 0;
                        for (int k = 0; k < N; k++) energyBefore += re[k] * re[k] + im[k] * im[k];

                        // Cooley-Tukey iterative FFT (in-place, radix-2)
                        int n = N;
                        // Bit-reversal permutation
                        for (int j = 1, h = n >> 1; j < n - 1; j++)
                        {
                            if (j < h) { (re[j], re[h]) = (re[h], re[j]); (im[j], im[h]) = (im[h], im[j]); }
                            int mask = n >> 1;
                            while ((h & mask) != 0) { h ^= mask; mask >>= 1; }
                            h ^= mask;
                        }
                        // FFT butterfly
                        for (int len = 2; len <= n; len <<= 1)
                        {
                            double ang = -2.0 * Math.PI / len;
                            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                            for (int j = 0; j < n; j += len)
                            {
                                double uRe = 1.0, uIm = 0.0;
                                for (int k2 = 0; k2 < len / 2; k2++)
                                {
                                    int a = j + k2, b = j + k2 + len / 2;
                                    double tRe = uRe * re[b] - uIm * im[b];
                                    double tIm = uRe * im[b] + uIm * re[b];
                                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                                    re[a] += tRe;        im[a] += tIm;
                                    double newURe = uRe * wRe - uIm * wIm;
                                    uIm = uRe * wIm + uIm * wRe;
                                    uRe = newURe;
                                }
                            }
                        }

                        double energyAfter = 0;
                        for (int k = 0; k < N; k++) energyAfter += re[k] * re[k] + im[k] * im[k];
                        double expected = N * energyBefore;
                        // Generous relative tolerance (1e-6) — covers the normal accumulation of double-precision rounding error over N=4096
                        // points, but is far below what a real hardware bit flip would produce.
                        if (expected > 1e-9 && Math.Abs(energyAfter - expected) / expected > 1e-6)
                            localMismatches++;

                        c++;
                        // Reset buffer to avoid denormals killing perf
                        if (c % 100 == 0) for (int k = 0; k < N; k++) { re[k] = rnd.NextDouble(); im[k] = 0; }
                    }
                    counts[idx] = c;
                    mismatchCounts[idx] = localMismatches;
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };   // below the interface: the window stays responsive, the idle processor time (the score) is the same
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            fftMismatches = (int)Math.Min(int.MaxValue, mismatchCounts.Sum());
            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        public static double BenchmarkFFT(double durationSec = 3.0) => BenchmarkFFT(durationSec, out _);

        // ── Combined benchmark result ────────────────────────────────────────
        public class BenchmarkResult
        {
            public double Sha256HashesPerSec  { get; set; }
            public double MandelbrotPixPerSec { get; set; }
            public double FftOpsPerSec        { get; set; }
            public int    Cores               { get; set; }
            public int    HashMismatches       { get; set; }
            public int    FftMismatches        { get; set; }
            // Composite score: weighted geometric mean, normalized to i7-12700K baseline
            public int    CompositeScore      { get; set; }
            public string Rating              { get; set; } = "";
            public double ScorePct            { get; set; }   // 0..1 for progress bar
        }

        public static BenchmarkResult RunFullBenchmark(double perTestSec = 2.0,
                                                       Action<string>? progress = null)
        {
            int cores = Environment.ProcessorCount;

            progress?.Invoke("SHA-256 (crypto)…");
            // BenchmarkSingleCore is more reliable than multi-threaded Benchmark()
            // which can return 0 if threads start after the deadline on loaded systems.
            // Single-core also better reflects real-world crypto performance.
            double sha = BenchmarkSingleCore(perTestSec, out int hashMismatches);

            progress?.Invoke("Mandelbrot (FPU / IPC)…");
            double mbrot = BenchmarkMandelbrot(perTestSec);

            progress?.Invoke("FFT (cache / bandwidth)…");
            double fft = BenchmarkFFT(perTestSec, out int fftMismatches);

            if (hashMismatches > 0 || fftMismatches > 0)
                progress?.Invoke($"⚠ Data integrity check FAILED — {hashMismatches} hash mismatch(es), {fftMismatches} FFT mismatch(es). This points to real hardware instability (RAM, overclock, or CPU), independent of temperature.");

            // Normalize each result against i5-8400 baseline (score 500 = solid mid-range)
            // i7-12700K → ~900-950, Celeron J4005 → ~80-150, i5-4570 → ~300-380
            const double shaRef   = 4_000.0;       // i5-8400 SHA-256 of 64 KB blocks per second (was 1 200 000, a wrong unit — an i5-4570 measures ~3 600)
            const double mbrotRef =  80_000_000.0;  // i5-8400 Mandelbrot pix/s
            const double fftRef   =  14_000.0;       // i5-8400 FFT ops/s

            double nSha   = sha   / shaRef;
            double nMbrot = mbrot / mbrotRef;
            double nFft   = fft   / fftRef;

            // Geometric mean, scaled so i5-8400 = 500, cap at 1000
            double geo       = Math.Pow(nSha * nMbrot * nFft, 1.0 / 3.0);
            int    composite = (int)Math.Min(1000, Math.Round(geo * 500));
            double pct       = Math.Clamp(geo / 2.0, 0.01, 1.0);  // 2.0 = ~i9-13900K

            string rating = composite >= 850 ? "🚀 Exceptional"
                          : composite >= 650 ? "✅ Excellent"
                          : composite >= 450 ? "✔ Good"
                          : composite >= 250 ? "⚠ Average"
                          : composite >= 100 ? "🐢 Slow"
                          :                    "🐢 Very Slow";

            return new BenchmarkResult
            {
                Sha256HashesPerSec  = sha,
                MandelbrotPixPerSec = mbrot,
                FftOpsPerSec        = fft,
                HashMismatches      = hashMismatches,
                FftMismatches       = fftMismatches,
                Cores               = cores,
                CompositeScore      = composite,
                Rating              = rating,
                ScorePct            = pct,
            };
        }
    }
}
