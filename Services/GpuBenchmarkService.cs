using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace SMDWin.Services
{
    public class GpuBenchmarkResult
    {
        public double GFlops       { get; set; }   // FP32 GFlop/s — measured with GPU timestamps
        public long   DispatchHz   { get; set; }   // real dispatches/s during the measurement
        public string Rating       { get; set; } = "";
        public string RatingColor  { get; set; } = "#22C55E";
        public string AdapterName  { get; set; } = "";
        public bool   Succeeded    { get; set; }
        public string ErrorMessage { get; set; } = "";
    }

    public class GpuBenchmarkService
    {
        // ── Shader parameters — single source for the FLOP calculation ─────────
        private const int ThreadsPerGroup = 256;
        private const int Groups          = 16384;               // 16384 × 256 = 4.19M threads/dispatch
        private const int LoopIters       = 256;
        private const int Chains          = 4;                   // 4 independent accumulators (ILP)
        // FLOP per dispatch = threads × iterations × chains × 2 (mad = mul + add)
        private const double FlopsPerDispatch =
            (double)Groups * ThreadsPerGroup * LoopIters * Chains * 2.0;  // ≈ 8.59 GFLOP

        private const double TargetBatchMs   = 200.0;
        private const double MeasureSeconds  = 2.5;

        /// <summary>Updated live after every batch — for the readout in BenchmarkWindow.</summary>
        public double LiveGFlops { get; private set; }

        public async Task<GpuBenchmarkResult> RunAsync(
            GpuStressService stressSvc,
            CancellationToken ct = default,
            IProgress<string>? progress = null)
        {
            return await Task.Run(() =>
            {
                var result = new GpuBenchmarkResult();

                if (stressSvc != null && stressSvc.IsRunning)
                {
                    result.Succeeded    = false;
                    result.ErrorMessage = "GPU stress test is already running. Stop it first.";
                    result.Rating       = "⚠ Stop GPU stress first";
                    result.RatingColor  = "#F59E0B";
                    return result;
                }

                D3D11Device?   device = null;
                DeviceContext? ctx    = null;
                SharpDX.Direct3D11.Buffer? uavBuf = null;
                UnorderedAccessView? uav = null;
                ComputeShader? cs = null;
                Query? qDisjoint = null, qBegin = null, qEnd = null;

                try
                {
                    // ── Hardware init with timeout (same model as GpuStressService) ──
                    progress?.Report("Initializing GPU…");
                    var initDone = new ManualResetEventSlim(false);
                    Exception? initEx = null;
                    Task.Run(() =>
                    {
                        try
                        {
                            device = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.None,
                                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
                        }
                        catch (Exception ex) { initEx = ex; }
                        finally { initDone.Set(); }
                    });
                    if (!initDone.Wait(3000) || initEx != null || device == null)
                    {
                        // Do NOT fall back to WARP: a benchmark on a software rasterizer measures the CPU, not the GPU —
                        // it is more honest to say there is no usable GPU.
                        result.Succeeded    = false;
                        result.ErrorMessage = initEx?.Message ?? "D3D11 hardware init timeout";
                        result.Rating       = "⚠ GPU not available";
                        result.RatingColor  = "#EF4444";
                        return result;
                    }

                    ctx = device.ImmediateContext;
                    result.AdapterName = GetAdapterName(device);

                    // ── Resurse ──────────────────────────────────────────────
                    uavBuf = new SharpDX.Direct3D11.Buffer(device, new BufferDescription
                    {
                        SizeInBytes = 4 * 1024 * 1024 * 4,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.BufferAllowRawViews,
                        StructureByteStride = 0
                    });
                    uav = new UnorderedAccessView(device, uavBuf, new UnorderedAccessViewDescription
                    {
                        Format = Format.R32_Typeless,
                        Dimension = UnorderedAccessViewDimension.Buffer,
                        Buffer = new UnorderedAccessViewDescription.BufferResource
                        { FirstElement = 0, ElementCount = 4 * 1024 * 1024, Flags = UnorderedAccessViewBufferFlags.Raw }
                    });

                    cs = CompileShader(device);
                    if (cs == null)
                    {
                        result.Succeeded    = false;
                        result.ErrorMessage = "Compute shader compilation failed";
                        result.Rating       = "⚠ Shader compile failed";
                        result.RatingColor  = "#EF4444";
                        return result;
                    }

                    qDisjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
                    qBegin    = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
                    qEnd      = new Query(device, new QueryDescription { Type = QueryType.Timestamp });

                    ctx.ComputeShader.Set(cs);
                    ctx.ComputeShader.SetUnorderedAccessView(0, uav);

                    // ── Calibration: how long does ONE dispatch take on this GPU? ─────
                    progress?.Report("Warming up…");
                    // 2 warm-up dispatches (driver compile, clock ramp-up)
                    RunBatch(ctx, qDisjoint, qBegin, qEnd, 2, ct, out _);
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();

                    if (!RunBatch(ctx, qDisjoint, qBegin, qEnd, 1, ct, out double oneMs) || oneMs <= 0)
                        oneMs = 20; // conservative fallback if the query was disjoint
                    int perBatch = Math.Clamp((int)Math.Round(TargetBatchMs / oneMs), 1, 256);

                    // ── Measurement: ~2.5 s of batches, median ────────────────
                    progress?.Report("Measuring GPU compute…");
                    var samples = new System.Collections.Generic.List<double>(16);
                    long totalDispatches = 0;
                    var swWall = Stopwatch.StartNew();
                    while (swWall.Elapsed.TotalSeconds < MeasureSeconds && !ct.IsCancellationRequested)
                    {
                        if (RunBatch(ctx, qDisjoint, qBegin, qEnd, perBatch, ct, out double gpuMs) && gpuMs > 0)
                        {
                            double gflops = FlopsPerDispatch * perBatch / (gpuMs / 1000.0) / 1e9;
                            samples.Add(gflops);
                            totalDispatches += perBatch;
                            LiveGFlops = gflops;
                            progress?.Report($"Measuring GPU compute… {gflops:F0} GFLOPS");
                        }
                    }
                    swWall.Stop();
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();

                    if (samples.Count == 0)
                    {
                        result.Succeeded    = false;
                        result.ErrorMessage = "All timestamp batches were disjoint (unstable GPU clock reads)";
                        result.Rating       = "⚠ No data";
                        result.RatingColor  = "#6B7280";
                        return result;
                    }

                    samples.Sort();
                    double median = samples[samples.Count / 2];
                    result.GFlops     = Math.Round(median, 1);
                    result.DispatchHz = swWall.Elapsed.TotalSeconds > 0
                        ? (long)(totalDispatches / swWall.Elapsed.TotalSeconds) : 0;
                    result.Succeeded  = true;

                    (result.Rating, result.RatingColor) = median switch
                    {
                        >= 8000 => ("🚀 High-end GPU",   "#22C55E"),
                        >= 2000 => ("✅ Mid-range GPU",   "#3B82F6"),
                        >= 500  => ("⚠ Entry-level GPU", "#F59E0B"),
                        >= 100  => ("💻 iGPU / Old GPU", "#F59E0B"),
                        _       => ("🐢 Very slow GPU",  "#EF4444"),
                    };

                    AppLogger.LogSection("BENCHMARK: GPU (S35)",
                        ("Adapter",      result.AdapterName),
                        ("GFLOPS (med)", result.GFlops),
                        ("GFLOPS (max)", Math.Round(samples[^1], 1)),
                        ("Dispatch/s",   result.DispatchHz),
                        ("Batch size",   perBatch));
                }
                catch (OperationCanceledException)
                {
                    result.Succeeded    = false;
                    result.ErrorMessage = "Cancelled";
                }
                catch (Exception ex)
                {
                    result.Succeeded    = false;
                    result.ErrorMessage = ex.Message;
                    result.Rating       = "⚠ GPU benchmark failed";
                    result.RatingColor  = "#6B7280";
                    AppLogger.Warning(ex, "GpuBenchmarkService");
                }
                finally
                {
                    try { if (ctx != null) { ctx.ComputeShader.SetUnorderedAccessView(0, null); ctx.ComputeShader.Set(null); } } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    qEnd?.Dispose(); qBegin?.Dispose(); qDisjoint?.Dispose();
                    cs?.Dispose(); uav?.Dispose(); uavBuf?.Dispose();
                    ctx?.Dispose(); device?.Dispose();
                }

                return result;
            }, ct);
        }

        /// <summary>
        /// Runs a batch of N dispatches between two GPU timestamps and returns the duration measured by the GPU CLOCK (ms).
        /// false = disjoint batch (unstable clock — e.g. a power state change) or timeout — it is discarded.
        /// </summary>
        private static bool RunBatch(DeviceContext ctx, Query qDisjoint, Query qBegin, Query qEnd,
                                     int dispatches, CancellationToken ct, out double gpuMs)
        {
            gpuMs = 0;
            ctx.Begin(qDisjoint);
            ctx.End(qBegin);
            for (int i = 0; i < dispatches; i++)
                ctx.Dispatch(Groups, 1, 1);
            ctx.End(qEnd);
            ctx.End(qDisjoint);
            ctx.Flush();

            // Poll with a timeout — on problematic drivers (Boot Camp) we never stay stuck
            var sw = Stopwatch.StartNew();
            QueryDataTimestampDisjoint dis = default;
            while (!ctx.GetData(qDisjoint, out dis))
            {
                if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false;
                Thread.Sleep(1);
            }
            long t0 = 0, t1 = 0;
            while (!ctx.GetData(qBegin, out t0))
            {
                if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false;
                Thread.Sleep(1);
            }
            while (!ctx.GetData(qEnd, out t1))
            {
                if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false;
                Thread.Sleep(1);
            }

            if (dis.Disjoint || dis.Frequency <= 0 || t1 <= t0) return false;
            gpuMs = (t1 - t0) * 1000.0 / dis.Frequency;
            return true;
        }

        private static ComputeShader? CompileShader(D3D11Device device)
        {
            // 4 independent MAD chains — mad latency is ~4 cycles; a single serial chain (the stress variant) leaves the FP32
            // units half idle. The constants keep values finite (no overflow/denormals).
            const string hlsl = @"
RWByteAddressBuffer Output : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint addr = (id.x & 0xFFFFF) * 4;
    float v0 = asfloat(Output.Load(addr));
    float v1 = v0 + 0.5f;
    float v2 = v0 + 0.25f;
    float v3 = v0 + 0.125f;
    [loop]
    for (int i = 0; i < 256; i++)
    {
        v0 = mad(v0, 1.0000003f, 0.000001f);
        v1 = mad(v1, 1.0000003f, 0.000001f);
        v2 = mad(v2, 1.0000003f, 0.000001f);
        v3 = mad(v3, 1.0000003f, 0.000001f);
    }
    Output.Store(addr, asuint(v0 + v1 + v2 + v3));
}";
            try
            {
                var bc = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    hlsl, "CSMain", "cs_5_0",
                    SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3,
                    SharpDX.D3DCompiler.EffectFlags.None);
                return bc.Bytecode == null ? null : new ComputeShader(device, bc.Bytecode);
            }
            catch { return null; }
        }

        private static string GetAdapterName(D3D11Device device)
        {
            try
            {
                using var dxgiDev = device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgiDev.GetParent<SharpDX.DXGI.Adapter>();
                return adapter.Description.Description.Trim();
            }
            catch { return "Unknown GPU"; }
        }
    }
}
