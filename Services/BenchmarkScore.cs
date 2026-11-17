using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// The 0–10 marks of the Benchmark page (14.11.2026), like the Windows 7 experience index. Every measured value becomes a
    /// mark on a logarithmic scale: <c>mark = 6 + log(value / reference) / log(step)</c> — the reference is a decent desktop
    /// PC of a few years ago (≈ i5-8400, DDR4 dual channel, SATA / NVMe SSD, GTX 1060-class graphics) = 6, and "step" is how
    /// much faster one more point means for that test (×1.26 for one core … ×2.2 for graphics, whose range is the widest).
    /// The component mark is the mean of its tests; the general mark is the mean of the components.
    /// The references are first estimates; they are tuned after runs on real PCs (the raw values go to the app log).
    /// </summary>
    public static class BenchmarkScore
    {
        public const double Min = 1.0, Max = 10.0;

        /// <summary>One test: the value that gives 6 and how much faster one more point is. LowerIsBetter for latency.</summary>
        public sealed record Scale(double Reference, double Step, bool LowerIsBetter = false);

        // ── the scales (value units in brackets) ──────────────────────────────────────────────────────────────────────
        // processor: Mandelbrot + FFT (geometric mean, each divided by 80 Mpix/s and 14 000 FFT/s) — on one thread and on all
        // threads. 14.11.2026: the first version used SHA-256 for one core (wrong reference ×300, and CPUs with SHA
        // instructions would win unfairly); the references come from the first real run (i5-4570: all threads 3.1 → ≈ 4.7)
        // 14.11.2026 (2): processor value = one i5-4570 core ≈ 1.0 (see CpuGeo on the Benchmark page). One core: a current
        // mid-range core (~2.2×) ≈ 7; the i5-4570 ≈ 4.3. All cores: i5-4570 (4 × 1) ≈ 4.1, i5-12400 (≈ 13) ≈ 7.2, 8 cores / 16 threads
        // of today (≈ 22) ≈ 8.5. First estimates — to be tuned from the saved results (Data\Benchmark).
        public static readonly Scale CpuSingle    = new(1.6, 1.32);          // one thread [× one i5-4570 core]
        public static readonly Scale CpuMulti     = new(7.9, 1.5);           // all threads [× one i5-4570 core]
        // everyday processor test: Deflate (level "optimal") of text-like data, all threads [MB/s] — i5-4570 ≈ 220 MB/s ≈ 4.2 (estimate)
        // 16.11.2026: .NET's Deflate is ~3× slower than estimated (i5-10210U at 1.6 GHz, 8 threads: 33 MB/s, while its other
        // processor tests put it at ~2.3) — the reference follows the all-cores test: i5-4570 ≈ 68 MB/s ≈ 4.1
        public static readonly Scale CpuCompress  = new(150, 1.5);
        public static readonly Scale RamBandwidth = new(20, 1.36);           // read [GB/s]
        public static readonly Scale RamLatency   = new(95, 1.3, true);      // random access [ns] — 16.11.2026: was 85 / 1.19, too steep (a laptop at 177 ns got 1.8); now i5-4570 106 ns ≈ 5.6, laptop DDR4 177 ns ≈ 3.6
        public static readonly Scale DiskSeq      = new(650, 1.67);          // sequential read [MB/s]
        // sequential write [MB/s]: HDD ≈ 120 → 3.2, SATA SSD ≈ 450 → 5.8, NVMe ≈ 2000 → 8.7
        public static readonly Scale DiskWrite    = new(500, 1.67);
        public static readonly Scale Disk4K       = new(80, 2.0);            // 4K random read, one request at a time [MB/s]
        public static readonly Scale GpuRender    = new(100, 2.2);           // 3D test [frames/s] — 14.11.2026: 300 put a GT 755M at 2.2 for 3D but 3.8 for compute; now both ≈ 3.7
        public static readonly Scale GpuCompute   = new(4000, 2.2);          // FP32 [GFLOPS]
        public static readonly Scale GpuMemory    = new(160, 1.57);          // copy, read + write [GB/s]

        /// <summary>The mark of one measured value (1–10); null when the value is missing or not valid.</summary>
        public static double? Mark(double value, Scale s)
        {
            if (!double.IsFinite(value) || value <= 0) return null;
            double ratio = s.LowerIsBetter ? s.Reference / value : value / s.Reference;
            double mark = 6 + Math.Log(ratio) / Math.Log(s.Step);
            return Math.Round(Math.Clamp(mark, Min, Max), 1);
        }

        /// <summary>The mean of the marks that exist, rounded to one decimal; null if none.</summary>
        public static double? Mean(IEnumerable<double?> marks)
        {
            var ok = marks.Where(m => m.HasValue).Select(m => m!.Value).ToList();
            return ok.Count == 0 ? null : Math.Round(ok.Average(), 1);
        }

        /// <summary>The level of a mark: what the PC is good for.</summary>
        public sealed record Tier(int Index, string Name, string GoodFor, double From, double To, string Color);

        public static readonly Tier[] Tiers =
        {
            new(0, "Insufficient",     "below today's needs",               0,   2,   "#DC2626"),
            new(1, "Entry level",      "web, e-mail, documents",            2,   4,   "#F97316"),
            new(2, "Standard",         "office work, video calls",          4,   5.5, "#EAB308"),
            new(3, "Advanced",         "multitasking, photo editing",       5.5, 7,   "#84CC16"),
            new(4, "High performance", "video editing, CAD, games",         7,   8.5, "#22C55E"),
            new(5, "Workstation",      "3D rendering, AI, top games",       8.5, 10,  "#16A34A"),
        };

        public static Tier TierOf(double mark) => Tiers.FirstOrDefault(t => mark < t.To) ?? Tiers[^1];

        /// <summary>The colour of a mark on the continuous red → green scale of the page (bar, dials, test blocks).</summary>
        public static (byte R, byte G, byte B) ScaleColor(double mark)
        {
            (double at, byte r, byte g, byte b)[] stops = { (0, 220, 38, 38), (2.5, 249, 115, 22), (4.7, 234, 179, 8), (6.8, 132, 204, 22), (10, 22, 163, 74) };
            mark = Math.Clamp(mark, 0, 10);
            for (int i = 1; i < stops.Length; i++)
                if (mark <= stops[i].at)
                {
                    var a = stops[i - 1]; var z = stops[i];
                    double f = (mark - a.at) / (z.at - a.at);
                    return ((byte)(a.r + (z.r - a.r) * f), (byte)(a.g + (z.g - a.g) * f), (byte)(a.b + (z.b - a.b) * f));
                }
            return (22, 163, 74);
        }

        /// <summary>Where the mark sits on the tier bar (0–1): each tier takes the same width, whatever its range.</summary>
        public static double TierPosition(double mark)
        {
            var t = TierOf(mark);
            double inside = Math.Clamp((mark - t.From) / (t.To - t.From), 0, 1);
            return (t.Index + inside) / Tiers.Length;
        }
    }
}
