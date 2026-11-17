using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class RamTestProgress
    {
        public int    PatternIndex  { get; set; }   // 0-5
        public int    TotalPatterns { get; set; } = 6;
        public string PatternName  { get; set; } = "";
        public double PercentDone  { get; set; }
        public long   ErrorCount   { get; set; }
        public string StatusText   { get; set; } = "";
        public bool   IsFinished   { get; set; }
        public bool   Passed       => IsFinished && ErrorCount == 0;
    }

    public class RamTestService
    {
        // Test 256 MB by default — enough to catch errors, fast enough for in-app test
        // User can choose 64 / 256 / 512 MB
        public async Task RunAsync(
            int testSizeMB,
            IProgress<RamTestProgress> progress,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                long errors = 0;
                int totalSizeMB = Math.Max(64, Math.Min(1024, testSizeMB));

                const int ChunkSizeMB = 128;
                int chunkCount = Math.Max(1, (int)Math.Ceiling(totalSizeMB / (double)ChunkSizeMB));

                // Test patterns — classic memory test patterns
                var patterns = new (uint write, string name)[]
                {
                    (0x00000000, "All Zeros"),
                    (0xFFFFFFFF, "All Ones"),
                    (0xAAAAAAAA, "Alternating 10"),
                    (0x55555555, "Alternating 01"),
                    (0xDEADBEEF, "Random Pattern"),
                    (0x12345678, "Sequential"),
                };

                // Total progress steps: each pattern is tested on each chunk.
                int totalSteps = patterns.Length * chunkCount;
                int stepsDone  = 0;
                int mbTested   = 0; // how many MB were tested before a possible OOM

                for (int chunk = 0; chunk < chunkCount && !ct.IsCancellationRequested; chunk++)
                {
                    int chunkMB = Math.Min(ChunkSizeMB, totalSizeMB - chunk * ChunkSizeMB);
                    int count   = (chunkMB * 1024 * 1024) / sizeof(uint);

                    uint[]? buf;
                    try { buf = new uint[count]; }
                    catch (OutOfMemoryException)
                    {
                        progress.Report(new RamTestProgress
                        {
                            IsFinished = true,
                            ErrorCount = errors > 0 ? errors : -1,
                            StatusText = chunk == 0
                                ? $"⚠ Not enough free RAM for a {chunkMB} MB test chunk. Try a smaller size."
                                : $"⚠ Stopped after {mbTested} MB — insufficient RAM for the next chunk. {errors} error(s) found so far."
                        });
                        return;
                    }

                    for (int pi = 0; pi < patterns.Length && !ct.IsCancellationRequested; pi++)
                    {
                        var (pat, name) = patterns[pi];
                        progress.Report(new RamTestProgress
                        {
                            PatternIndex = pi, PatternName = name,
                            PercentDone = stepsDone * 100.0 / totalSteps,
                            ErrorCount = errors,
                            StatusText = chunkCount > 1
                                ? $"Chunk {chunk + 1}/{chunkCount} — writing pattern: {name}..."
                                : $"Writing pattern: {name}..."
                        });

                        // Write pass
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                            buf[i] = pat;

                        // Add a memory barrier to prevent compiler/CPU from optimizing away
                        Thread.MemoryBarrier();

                        // Read + verify pass
                        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                        {
                            if (buf[i] != pat)
                            {
                                errors++;
                                // Don't flood progress with millions of errors
                                if (errors > 1000) break;
                            }
                        }

                        Thread.MemoryBarrier();
                        stepsDone++;

                        progress.Report(new RamTestProgress
                        {
                            PatternIndex = pi + 1, PatternName = name,
                            PercentDone  = stepsDone * 100.0 / totalSteps,
                            ErrorCount   = errors,
                            StatusText   = errors == 0
                                ? string.Format(LanguageService.S("Ram", "PatternOkFmt", "✅ {0} — OK"), name)
                                : string.Format(LanguageService.S("Ram", "PatternErrFmt", "❌ {0} — {1} error(s) found!"), name, errors)
                        });
                    }

                    // Walking bit test — catches single-bit failures missed by fixed patterns. Runs on EVERY chunk, to cover all the
                    // tested memory.
                    if (!ct.IsCancellationRequested)
                    {
                        progress.Report(new RamTestProgress
                        {
                            PatternIndex = patterns.Length, PatternName = "Walking Bit",
                            PercentDone  = stepsDone * 100.0 / totalSteps,
                            ErrorCount   = errors,
                            StatusText   = chunkCount > 1
                                ? string.Format(LanguageService.S("Ram", "WalkingBitChunkFmt", "Chunk {0}/{1} — walking bit test..."), chunk + 1, chunkCount)
                                : LanguageService.S("Ram", "WalkingBitSingle", "Walking bit test...")
                        });
                        for (int bit = 0; bit < 32 && !ct.IsCancellationRequested; bit++)
                        {
                            uint pat = 1u << bit;
                            for (int i = 0; i < count; i++) buf[i] = pat;
                            Thread.MemoryBarrier();
                            for (int i = 0; i < count; i++)
                                if (buf[i] != pat) errors++;
                        }
                    }

                    mbTested += chunkMB;
                    buf = null; // release the current chunk before the next one (the GC reclaims it naturally)
                }

                string finalStatus = ct.IsCancellationRequested
                    ? LanguageService.S("Ram", "TestCancelled", "⚪ Test cancelled")
                    : errors == 0
                        ? string.Format(LanguageService.S("Ram", "FinalPassedFmt", "✅ PASSED — No errors found in {0} MB test"), totalSizeMB)
                        : string.Format(LanguageService.S("Ram", "FinalFailedFmt", "❌ FAILED — {0} memory error(s) detected!"), errors);

                progress.Report(new RamTestProgress
                {
                    IsFinished   = true,
                    ErrorCount   = ct.IsCancellationRequested ? 0 : errors,
                    PercentDone  = 100,
                    StatusText   = finalStatus
                });
            }, ct);
        }
    }
}
