using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public static class RamFullScanService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public const int ChunkSizeMB = 256;
        public const int MinFreeHeadroomMB = 1536; // always keep at least 1.5 GB free
        private const double TargetFraction = 0.75;  // target 75% of the currently free memory

        public class ChunkResult
        {
            public int Index;
            public int SizeMB;
            public bool Passed;
            public long Errors;
        }

        public class FullScanProgress
        {
            public int ChunkIndex;      // the current sector (0-based), or the number of finished sectors when IsFinished
            public int TotalChunks;
            public int ChunkSizeMB   = RamFullScanService.ChunkSizeMB;
            public int TargetTotalMB;
            public string PatternName = "";
            public double PercentDone;
            public long   ErrorCount;
            public string StatusText  = "";
            public bool   IsFinished;
            public bool   Passed => IsFinished && ErrorCount == 0;
            /// <summary>Set ONLY when a sector has just finished — the UI adds it to the sector map (WrapPanel of coloured squares)
            /// without keeping a separate history.</summary>
            public ChunkResult? FinishedChunk;
        }

        /// <summary>How many MB are free NOW according to Windows (not the .NET GC — GlobalMemoryStatusEx reflects real physical
        /// memory, which is what matters for a test that must not cause swapping).</summary>
        public static long GetAvailablePhysicalMB()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return 0;
            return (long)(status.ullAvailPhys / (1024 * 1024));
        }

        /// <summary>Computes the test target (MB) from the current free memory, applying the fraction + safety headroom described
        /// above. Exposed separately so the UI can show "will test ~X GB" BEFORE starting the test.</summary>
        public static long ComputeTargetMB()
        {
            long freeMB = GetAvailablePhysicalMB();
            if (freeMB <= 0) return ChunkSizeMB; // minimal fallback if WinAPI fails
            long budget = freeMB - MinFreeHeadroomMB;
            if (budget <= 0) return 0; // too little free memory to be safe
            long target = (long)(budget * TargetFraction);
            return Math.Max(0, target);
        }

        public static async Task RunAsync(IProgress<FullScanProgress> progress, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                long targetMB = ComputeTargetMB();
                if (targetMB < ChunkSizeMB)
                {
                    progress.Report(new FullScanProgress
                    {
                        IsFinished = true,
                        ErrorCount = -1,
                        StatusText = string.Format(
                            LanguageService.Get("Ram", "FullScanInsufficientMem", "⚠ Not enough free RAM for a safe scan (minimum ~{0} MB free needed)."),
                            MinFreeHeadroomMB + ChunkSizeMB)
                    });
                    return;
                }

                int totalChunks = (int)Math.Max(1, targetMB / ChunkSizeMB);
                long totalErrors = 0;
                int count = (ChunkSizeMB * 1024 * 1024) / sizeof(uint);

                // Patterns per sector — a subset chosen for a reasonable speed over many sectors (the full walking-bit stays in the
                // Quick test, on a small fixed buffer; here the classic "stuck bit"/coupling patterns are covered: all-0, all-1,
                // checkerboard + complement — enough for a large-volume scan).
                var patterns = new (uint write, string name)[]
                {
                    (0x00000000, "All Zeros"),
                    (0xFFFFFFFF, "All Ones"),
                    (0xAAAAAAAA, "Checkerboard"),
                    (0x55555555, "Checkerboard (inv)"),
                };

                // each sector's buffer stays REFERENCED until the end of the whole test: otherwise .NET often reuses the same memory
                // range (LOH) for repeated large allocations, and the test would re-test the same physical pages in different sectors.
                // The usage is already budgeted: ComputeTargetMB() sizes targetMB so ALL sectors fit at once (75% of free, minus a
                // 1.5 GB reserve).
                var liveBuffers = new List<uint[]>(totalChunks);

                for (int chunk = 0; chunk < totalChunks && !ct.IsCancellationRequested; chunk++)
                {
                    uint[]? buf;
                    try { buf = new uint[count]; }
                    catch (OutOfMemoryException)
                    {
                        // Another process used memory in the meantime — stop here instead of failing the whole test; the sectors already
                        // scanned stay valid.
                        progress.Report(new FullScanProgress
                        {
                            ChunkIndex = chunk, TotalChunks = totalChunks,
                            TargetTotalMB = (int)targetMB, ErrorCount = totalErrors,
                            IsFinished = true,
                            StatusText = string.Format(
                                LanguageService.Get("Ram", "FullScanOom", "⚠ Insufficient memory at sector {0}/{1} — scan stopped, {2} sector(s) verified OK."),
                                chunk + 1, totalChunks, chunk)
                        });
                        return;
                    }

                    long chunkErrors = 0;
                    foreach (var (pat, name) in patterns)
                    {
                        if (ct.IsCancellationRequested) break;

                        progress.Report(new FullScanProgress
                        {
                            ChunkIndex = chunk, TotalChunks = totalChunks,
                            TargetTotalMB = (int)targetMB, PatternName = name,
                            PercentDone = (chunk + 0.0) * 60.0 / totalChunks,
                            ErrorCount = totalErrors + chunkErrors,
                            StatusText = string.Format(
                                LanguageService.Get("Ram", "FullScanSectorProgress", "Sector {0}/{1} — {2}…"),
                                chunk + 1, totalChunks, name)
                        });

                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                            buf[i] = pat;
                        Thread.MemoryBarrier();
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                            if (buf[i] != pat) chunkErrors++;
                        Thread.MemoryBarrier();
                    }

                    totalErrors += chunkErrors;
                    liveBuffers.Add(buf); // stays alive — not eligible for GC

                    progress.Report(new FullScanProgress
                    {
                        ChunkIndex = chunk + 1, TotalChunks = totalChunks,
                        TargetTotalMB = (int)targetMB,
                        PercentDone = (chunk + 1) * 60.0 / totalChunks,
                        ErrorCount = totalErrors,
                        StatusText = chunkErrors == 0
                            ? string.Format(LanguageService.Get("Ram", "FullScanSectorOk", "✔ Sector {0}/{1} OK"), chunk + 1, totalChunks)
                            : string.Format(LanguageService.Get("Ram", "FullScanSectorErr", "✘ Sector {0}/{1} — {2} error(s)"), chunk + 1, totalChunks, chunkErrors),
                        FinishedChunk = new ChunkResult
                        {
                            Index = chunk, SizeMB = ChunkSizeMB,
                            Passed = chunkErrors == 0, Errors = chunkErrors
                        }
                    });
                }

                // ── Phase 2: the same memory again, on all processor threads at once ─────────────────────────────────────────
                // Errors often show only under load and heat (memory controller, marginal timings / XMP), and the fixed patterns
                // above miss addressing faults. Here: own address (each word holds a value derived from its place — catches
                // two addresses landing on the same cell), random data (data-dependent faults) and moving inversions
                // (write / verify / invert up and down — coupling between neighbouring cells).
                if (!ct.IsCancellationRequested && liveBuffers.Count > 0)
                {
                    long p2Errors = 0;
                    var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct };
                    ulong seed = (ulong)DateTime.UtcNow.Ticks;
                    uint Own(int b, int i) => (uint)(i * 2654435761u) ^ (uint)(b * 0x9E3779B9u) ^ 0xA5A5A5A5u;
                    uint Rnd(int b, int i) => (uint)DrivePattern.Word(seed, ((long)b << 32) | (uint)i);
                    void Report(string name, double pct) => progress.Report(new FullScanProgress
                    {
                        ChunkIndex = totalChunks, TotalChunks = totalChunks, TargetTotalMB = (int)targetMB, PatternName = name,
                        PercentDone = pct, ErrorCount = totalErrors + p2Errors,
                        StatusText = string.Format(LanguageService.Get("Ram", "FullScanLoadPhase", "All threads at once — {0}…"), name),
                    });
                    try
                    {
                        // own address
                        Report("own address", 62);
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; for (int i = 0; i < x.Length; i++) x[i] = Own(b, i); });
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; long e = 0; for (int i = 0; i < x.Length; i++) if (x[i] != Own(b, i)) e++; if (e > 0) Interlocked.Add(ref p2Errors, e); });
                        // random data
                        Report("random data", 75);
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; for (int i = 0; i < x.Length; i++) x[i] = Rnd(b, i); });
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; long e = 0; for (int i = 0; i < x.Length; i++) if (x[i] != Rnd(b, i)) e++; if (e > 0) Interlocked.Add(ref p2Errors, e); });
                        // moving inversions
                        Report("moving inversions", 88);
                        const uint P = 0x5A5A5A5Au;
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; for (int i = 0; i < x.Length; i++) x[i] = P; });
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; long e = 0; for (int i = 0; i < x.Length; i++) { if (x[i] != P) e++; x[i] = ~P; } if (e > 0) Interlocked.Add(ref p2Errors, e); });
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; long e = 0; for (int i = x.Length - 1; i >= 0; i--) { if (x[i] != ~P) e++; x[i] = P; } if (e > 0) Interlocked.Add(ref p2Errors, e); });
                        Parallel.For(0, liveBuffers.Count, po, b => { var x = liveBuffers[b]; long e = 0; for (int i = 0; i < x.Length; i++) if (x[i] != P) e++; if (e > 0) Interlocked.Add(ref p2Errors, e); });
                    }
                    catch (OperationCanceledException) { }
                    totalErrors += p2Errors;
                    if (p2Errors > 0) AppLogger.Warning($"RAM full scan: {p2Errors} error(s) in the all-threads phase");
                }

                bool cancelled = ct.IsCancellationRequested;
                double gbTested = totalChunks * ChunkSizeMB / 1024.0;
                progress.Report(new FullScanProgress
                {
                    ChunkIndex = totalChunks, TotalChunks = totalChunks,
                    TargetTotalMB = (int)targetMB,
                    PercentDone = 100,
                    ErrorCount = cancelled ? 0 : totalErrors,
                    IsFinished = true,
                    StatusText = cancelled
                        ? LanguageService.Get("Ram", "FullScanCancelled", "⚪ Scan cancelled")
                        : totalErrors == 0
                            ? string.Format(LanguageService.Get("Ram", "FullScanPassedFmt", "✅ PASSED — {0} GB verified, 0 errors"), $"{gbTested:F1}")
                            : string.Format(LanguageService.Get("Ram", "FullScanFailedFmt", "❌ FAILED — {0} error(s) in {1} GB tested"), totalErrors, $"{gbTested:F1}")
                });
            }, ct);
        }
    }
}
