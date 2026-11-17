
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public static class RamBandwidthService
    {
        public class Result
        {
            public double ReadGBs   { get; set; }
            public double WriteGBs  { get; set; }
            public int    WritePasses { get; set; }
            public int    ReadPasses   { get; set; }
        }

        private const int BufSizeBytes = 128 * 1024 * 1024;
        private const double TargetSecondsPerPhase = 4.0; // ~4s write + ~4s read = ~8s total
        private const int MinPasses = 3;
        private const int MaxPasses = 30; // safety net — does not run forever on very fast RAM

        public static async Task<Result> RunAsync(Action<string>? progress = null)
        {
            return await Task.Run(() => Run(progress));
        }

        private static Result Run(Action<string>? progress)
        {
            int threads = Math.Clamp(Environment.ProcessorCount, 1, 8);

            progress?.Invoke("Allocating memory…");
            var buffers = new byte[threads][];
            for (int t = 0; t < threads; t++)
            {
                buffers[t] = new byte[BufSizeBytes];
                new Random(t + 1).NextBytes(buffers[t]); // date reale — evita optimizari agresive ale JIT-ului
            }

            // ── Calibration: one write pass measures the real speed, then compute how many passes are needed to reach
            // ~TargetSecondsPerPhase ──
            double calibGBps = MeasureWritePass(buffers, threads);
            int writePasses = calibGBps > 0
                ? Math.Clamp((int)Math.Ceiling(TargetSecondsPerPhase * calibGBps /
                    (BufSizeBytes * threads / (1024.0 * 1024 * 1024))), MinPasses, MaxPasses)
                : MinPasses;

            double writeTotalGBps = calibGBps; // the first pass counts as part of the measurement
            int writeDone = 1;
            for (int p = 2; p <= writePasses; p++)
            {
                progress?.Invoke($"RAM Write — pass {p}/{writePasses}…");
                writeTotalGBps += MeasureWritePass(buffers, threads);
                writeDone++;
            }
            double writeAvgGBs = writeTotalGBps / writeDone;

            // ── Read: the same pattern — calibration + passes up to the target duration ──
            double calibReadGBps = MeasureReadPass(buffers, threads);
            int readPasses = calibReadGBps > 0
                ? Math.Clamp((int)Math.Ceiling(TargetSecondsPerPhase * calibReadGBps /
                    (BufSizeBytes * threads / (1024.0 * 1024 * 1024))), MinPasses, MaxPasses)
                : MinPasses;

            double readTotalGBps = calibReadGBps;
            int readDone = 1;
            for (int p = 2; p <= readPasses; p++)
            {
                progress?.Invoke($"RAM Read — pass {p}/{readPasses}…");
                readTotalGBps += MeasureReadPass(buffers, threads);
                readDone++;
            }
            double readAvgGBs = readTotalGBps / readDone;

            GC.KeepAlive(buffers);

            return new Result
            {
                WriteGBs    = writeAvgGBs,
                ReadGBs     = readAvgGBs,
                WritePasses = writeDone,
                ReadPasses  = readDone,
            };
        }

        private static double MeasureWritePass(byte[][] buffers, int threads)
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, threads, t =>
            {
                buffers[t].AsSpan().Fill((byte)(sw.ElapsedTicks & 0xFF)); // vectorised write
            });
            sw.Stop();
            double totalGB = (double)BufSizeBytes * threads / (1024.0 * 1024 * 1024);
            return sw.Elapsed.TotalSeconds > 0 ? totalGB / sw.Elapsed.TotalSeconds : 0;
        }

        private static double MeasureReadPass(byte[][] buffers, int threads)
        {
            var sinks = new long[threads];
            var sw = Stopwatch.StartNew();
            Parallel.For(0, threads, t =>
            {
                var longs = MemoryMarshal.Cast<byte, long>(buffers[t].AsSpan());
                long sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                int n = longs.Length;
                int i = 0;
                for (; i <= n - 4; i += 4)
                {
                    sum0 += longs[i];
                    sum1 += longs[i + 1];
                    sum2 += longs[i + 2];
                    sum3 += longs[i + 3];
                }
                for (; i < n; i++) sum0 += longs[i];
                sinks[t] = sum0 + sum1 + sum2 + sum3; // prevents the JIT from eliminating the code
            });
            sw.Stop();
            GC.KeepAlive(sinks);
            double totalGB = (double)BufSizeBytes * threads / (1024.0 * 1024 * 1024);
            return sw.Elapsed.TotalSeconds > 0 ? totalGB / sw.Elapsed.TotalSeconds : 0;
        }
    }
}
