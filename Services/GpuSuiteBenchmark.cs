using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3DBuffer   = SharpDX.Direct3D11.Buffer;

namespace SMDWin.Services
{
    /// <summary>
    /// The graphics part of the Benchmark page (14.11.2026): three short tests on the same D3D11 device, each ~13 s, so the
    /// card reaches its steady clock and a card that slows down when hot shows it:
    ///   1. 3D render — a 1920×1080 off-screen picture, 16 layers of a 48×48 grid of quads with a lighting pixel shader
    ///      (24 lights per pixel) → frames per second;
    ///   2. compute — 4 independent FP32 multiply-add chains (the shader of GpuBenchmarkService) → GFLOPS;
    ///   3. memory — the driver copies a 64 MB buffer into another one on the card (CopyResource) → GB/s (read + write).
    /// Every time is measured with GPU timestamps (not the CPU clock), in batches of ~200 ms; the result is the median batch.
    /// Nothing is shown on the screen and nothing is written to the disk.
    /// </summary>
    public sealed class GpuSuiteBenchmark
    {
        public sealed class Result
        {
            public string AdapterName { get; set; } = "";
            public double Fps { get; set; }
            public double GFlops { get; set; }
            public double MemoryGBs { get; set; }
            /// <summary>Both memory measurements (copy and read), for the log.</summary>
            public string MemoryNote { get; set; } = "";
            public bool Succeeded { get; set; }
            public string Error { get; set; } = "";
            /// <summary>Per test (render, compute, memory): the batch statistics — for the log and the self-test, so the marks
            /// can be tuned later (a wide min–max = the clock moved during the test, e.g. heat).</summary>
            public TestStat[] Stats { get; } = new TestStat[3];
        }

        public sealed record TestStat(double Median, double Min, double Max, double First, double Last, int Samples, int PerBatch, double OneIssueMs);

        /// <summary>Progress: test (0 render, 1 compute, 2 memory), fraction of that test (0–1), the live value.</summary>
        public readonly record struct Progress(int Test, double Fraction, double Live);

        private const int RtWidth = 1920, RtHeight = 1080, Grid = 48, Layers = 16;
        private const int CsThreads = 256, CsGroups = 16384, CsLoop = 256, CsChains = 4;
        private const double CsFlops = (double)CsGroups * CsThreads * CsLoop * CsChains * 2.0;
        private const int CopyBytes = 64 * 1024 * 1024;
        private const int ReadLoads = 16, ReadThreads = CopyBytes / 16 / ReadLoads;   // 262 144 threads, 16 loads of 16 bytes each
        private const double TargetBatchMs = 200;

        /// <summary>A graphics adapter the test can use (the Microsoft basic renderer is left out).</summary>
        public sealed record AdapterInfo(int Index, string Name, long DedicatedMB);

        /// <summary>The hardware adapters (14.11.2026): on laptops with two (Intel / AMD integrated + NVIDIA / AMD dedicated)
        /// the page lets the user choose; by default the one with the most dedicated memory — Windows' default is often the
        /// integrated one, especially on battery.</summary>
        public static List<AdapterInfo> Adapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                using var f = new Factory1();
                int n = f.GetAdapterCount1();
                for (int i = 0; i < n; i++)
                {
                    using var a = f.GetAdapter1(i);
                    var d = a.Description1;
                    if (d.VendorId == 0x1414 || (d.Flags & AdapterFlags.Software) != 0) continue;   // Microsoft Basic Render Driver
                    list.Add(new AdapterInfo(i, d.Description.Trim(), (long)d.DedicatedVideoMemory / (1024 * 1024)));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"GpuSuiteBenchmark.Adapters: {ex.Message}"); }
            return list;
        }

        /// <summary>The adapter used when the user did not choose: the most dedicated memory.</summary>
        public static int DefaultAdapterIndex(List<AdapterInfo> list) => list.Count == 0 ? -1 : list.OrderByDescending(a => a.DedicatedMB).First().Index;

        private int _adapterIndex = -1;

        public Task<Result> RunAsync(double secondsPerTest, CancellationToken ct, IProgress<Progress>? progress, int adapterIndex = -1)
        {
            _adapterIndex = adapterIndex;
            return Task.Run(() => Run(secondsPerTest, ct, progress), ct);
        }

        private Result Run(double seconds, CancellationToken ct, IProgress<Progress>? progress)
        {
            var result = new Result();
            var trash = new List<IDisposable>();
            D3D11Device? device = null;
            try
            {
                device = CreateDevice(_adapterIndex);
                if (device == null) { result.Error = "No hardware D3D11 graphics device"; return result; }
                var ctx = device.ImmediateContext;
                result.AdapterName = AdapterName(device);
                var qDis = Keep(trash, new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint }));
                var qBeg = Keep(trash, new Query(device, new QueryDescription { Type = QueryType.Timestamp }));
                var qEnd = Keep(trash, new Query(device, new QueryDescription { Type = QueryType.Timestamp }));

                // ── 1. 3D render ────────────────────────────────────────────────────────────────────────────────
                {
                    var rt = Keep(trash, new Texture2D(device, new Texture2DDescription
                    {
                        Width = RtWidth, Height = RtHeight, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
                    }));
                    var rtv = Keep(trash, new RenderTargetView(device, rt));
                    var vsCode = Compile(RenderHlsl, "VSMain", "vs_5_0");
                    var psCode = Compile(RenderHlsl, "PSMain", "ps_5_0");
                    if (vsCode == null || psCode == null) throw new InvalidOperationException("3D shader compilation failed");
                    var vs = Keep(trash, new VertexShader(device, vsCode));
                    var ps = Keep(trash, new PixelShader(device, psCode));
                    var rs = Keep(trash, new RasterizerState(device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid }));
                    ctx.OutputMerger.SetRenderTargets(rtv);
                    ctx.Rasterizer.SetViewport(0, 0, RtWidth, RtHeight);
                    ctx.Rasterizer.State = rs;
                    ctx.InputAssembler.InputLayout = null;
                    ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    ctx.VertexShader.Set(vs);
                    ctx.PixelShader.Set(ps);
                    result.Stats[0] = Measure(ctx, qDis, qBeg, qEnd, () => ctx.DrawInstanced(Grid * Grid * 6, Layers, 0, 0), 1.0,
                                              seconds, ct, (f, v) => progress?.Report(new Progress(0, f, v)));
                    result.Fps = result.Stats[0].Median;
                    ctx.VertexShader.Set(null); ctx.PixelShader.Set(null);
                    ctx.OutputMerger.SetRenderTargets((RenderTargetView?)null);
                }

                // ── 2. compute ──────────────────────────────────────────────────────────────────────────────────
                {
                    var buf = Keep(trash, new D3DBuffer(device, new BufferDescription
                    {
                        SizeInBytes = 16 * 1024 * 1024, Usage = ResourceUsage.Default, BindFlags = BindFlags.UnorderedAccess,
                        OptionFlags = ResourceOptionFlags.BufferAllowRawViews,
                    }));
                    var uav = Keep(trash, RawUav(device, buf, 4 * 1024 * 1024));
                    var code = Compile(ComputeHlsl, "CSMain", "cs_5_0") ?? throw new InvalidOperationException("Compute shader compilation failed");
                    var cs = Keep(trash, new ComputeShader(device, code));
                    ctx.ComputeShader.Set(cs);
                    ctx.ComputeShader.SetUnorderedAccessView(0, uav);
                    result.Stats[1] = Measure(ctx, qDis, qBeg, qEnd, () => ctx.Dispatch(CsGroups, 1, 1), CsFlops / 1e9,
                                              seconds, ct, (f, v) => progress?.Report(new Progress(1, f, v)));
                    result.GFlops = result.Stats[1].Median;
                    ctx.ComputeShader.SetUnorderedAccessView(0, null);
                    ctx.ComputeShader.Set(null);
                }

                // 16.11.2026: the 3D and compute resources are freed first. On an iMac (GT 755M, 1 GB, 2560 × 1440 desktop) only
                // ~340 MB of the card's memory was free: both memory tests gave 10–16 GB/s, the speed of the PCIe bus (the buffers
                // were probably kept in system memory) while the memory clock read 2 500 MHz (~80 GB/s). Only the 3 timers stay.
                ctx.ClearState();
                for (int k = trash.Count - 1; k >= 3; k--) { try { trash[k].Dispose(); } catch (Exception ex) { AppLogger.Debug($"GpuSuite free: {ex.Message}"); } }
                if (trash.Count > 3) trash.RemoveRange(3, trash.Count - 3);
                ctx.Flush();

                // ── 3. memory: the driver's own copy between two buffers on the card (14.11.2026 — the compute-shader copy
                //    gave 1 GB/s on a GT 755M, far from its ~60 GB/s) ─────────────────────────────────────────────────────
                //    14.11.2026 (2): the copy gave 15 GB/s on the same GT 755M (GDDR5, ~86 GB/s on paper) — on some cards the
                //    driver sends CopyResource to the copy engine, which is made for the PCIe bus. Now half of the time is the
                //    copy and half a read test (a compute shader reads the 64 MB buffer in wide, aligned loads); the higher of
                //    the two is the result, both go to the log.
                {
                    var desc = new BufferDescription { SizeInBytes = CopyBytes, Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                                                       OptionFlags = ResourceOptionFlags.BufferAllowRawViews };
                    var src = Keep(trash, new D3DBuffer(device, desc));
                    var dst = Keep(trash, new D3DBuffer(device, desc));
                    var copy = Measure(ctx, qDis, qBeg, qEnd, () => ctx.CopyResource(src, dst), 2.0 * CopyBytes / 1e9,
                                       seconds / 2, ct, (f, v) => progress?.Report(new Progress(2, f * 0.5, v)));

                    var srv = Keep(trash, new ShaderResourceView(device, src, new ShaderResourceViewDescription
                    {
                        Format = Format.R32_Typeless, Dimension = ShaderResourceViewDimension.ExtendedBuffer,
                        BufferEx = new ShaderResourceViewDescription.ExtendedBufferResource
                        { FirstElement = 0, ElementCount = CopyBytes / 4, Flags = ShaderResourceViewExtendedBufferFlags.Raw },
                    }));
                    var sink = Keep(trash, new D3DBuffer(device, new BufferDescription
                    {
                        SizeInBytes = 4096, Usage = ResourceUsage.Default, BindFlags = BindFlags.UnorderedAccess,
                        OptionFlags = ResourceOptionFlags.BufferAllowRawViews,
                    }));
                    var uav = Keep(trash, RawUav(device, sink, 1024));
                    TestStat? read = null;
                    var code = Compile(ReadHlsl, "CSMain", "cs_5_0");
                    if (code != null)
                    {
                        var cs = Keep(trash, new ComputeShader(device, code));
                        ctx.ComputeShader.Set(cs);
                        ctx.ComputeShader.SetShaderResource(0, srv);
                        ctx.ComputeShader.SetUnorderedAccessView(0, uav);
                        // ReadThreads threads × ReadLoads loads × 16 bytes = the whole buffer, once per dispatch
                        read = Measure(ctx, qDis, qBeg, qEnd, () => ctx.Dispatch(ReadThreads / 256, 1, 1), (double)CopyBytes / 1e9,
                                       seconds / 2, ct, (f, v) => progress?.Report(new Progress(2, 0.5 + f * 0.5, v)));
                        ctx.ComputeShader.SetUnorderedAccessView(0, null);
                        ctx.ComputeShader.SetShaderResource(0, null);
                        ctx.ComputeShader.Set(null);
                    }
                    else AppLogger.Warning("GpuSuiteBenchmark: the read shader did not compile");
                    result.Stats[2] = read != null && read.Median > copy.Median ? read : copy;
                    result.MemoryGBs = result.Stats[2].Median;
                    result.MemoryNote = $"copy (driver, read + write) {copy.Median:0.0} GB/s · read (shader) {(read?.Median ?? 0):0.0} GB/s · used: {(ReferenceEquals(result.Stats[2], read) ? "read" : "copy")}";
                }

                result.Succeeded = result.Fps > 0 || result.GFlops > 0 || result.MemoryGBs > 0;
                AppLogger.LogSection("BENCHMARK PAGE: GPU",
                    ("Adapter", result.AdapterName), ("3D fps", Math.Round(result.Fps, 1)),
                    ("GFLOPS", Math.Round(result.GFlops, 1)), ("Memory GB/s", Math.Round(result.MemoryGBs, 1)), ("Memory tests", result.MemoryNote),
                    ("3D stats", result.Stats[0]), ("Compute stats", result.Stats[1]), ("Memory stats", result.Stats[2]));
            }
            catch (OperationCanceledException) { result.Error = "Cancelled"; }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                AppLogger.Warning(ex, "GpuSuiteBenchmark");
            }
            finally
            {
                for (int i = trash.Count - 1; i >= 0; i--) { try { trash[i].Dispose(); } catch { /* already gone with the device */ } }
                try { device?.ImmediateContext?.ClearState(); device?.ImmediateContext?.Dispose(); } catch { }
                device?.Dispose();
            }
            return result;
        }

        private static T Keep<T>(List<IDisposable> list, T item) where T : IDisposable { list.Add(item); return item; }

        private static D3D11Device? CreateDevice(int adapterIndex)
        {
            // the same 3 s guard as the other GPU tests: a broken driver must not hang the page; no WARP fallback (it would
            // measure the processor)
            D3D11Device? dev = null;
            var done = new ManualResetEventSlim(false);
            Task.Run(() =>
            {
                try
                {
                    if (adapterIndex >= 0)
                    {
                        using var f = new Factory1();
                        using var a = f.GetAdapter1(adapterIndex);
                        dev = new D3D11Device(a, DeviceCreationFlags.None, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0);
                    }
                    else dev = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.None, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0);
                }
                catch (Exception ex) { AppLogger.Debug($"GpuSuiteBenchmark device: {ex.Message}"); }
                finally { done.Set(); }
            });
            return done.Wait(3000) ? dev : null;
        }

        private static UnorderedAccessView RawUav(D3D11Device device, D3DBuffer buf, int elements) =>
            new(device, buf, new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless, Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = elements, Flags = UnorderedAccessViewBufferFlags.Raw },
            });

        /// <summary>Runs the work for <paramref name="seconds"/> in batches of ~200 ms timed by the GPU; returns the median of
        /// (units per batch / batch time). <paramref name="unitsPerIssue"/> = how much one call does (1 frame, N GFLOP, N GB).</summary>
        private static TestStat Measure(DeviceContext ctx, Query qDis, Query qBeg, Query qEnd, Action issue, double unitsPerIssue,
                                      double seconds, CancellationToken ct, Action<double, double> report)
        {
            Batch(ctx, qDis, qBeg, qEnd, issue, 2, ct, out _);                         // warm-up (driver compile, clocks)
            if (!Batch(ctx, qDis, qBeg, qEnd, issue, 1, ct, out double oneMs) || oneMs <= 0) oneMs = 20;
            int perBatch = Math.Clamp((int)Math.Round(TargetBatchMs / oneMs), 1, 512);
            var samples = new List<double>(96);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                ct.ThrowIfCancellationRequested();
                if (Batch(ctx, qDis, qBeg, qEnd, issue, perBatch, ct, out double ms) && ms > 0)
                {
                    double v = unitsPerIssue * perBatch / (ms / 1000.0);
                    samples.Add(v);
                    report(Math.Min(1, sw.Elapsed.TotalSeconds / seconds), v);
                }
            }
            if (samples.Count == 0) return new TestStat(0, 0, 0, 0, 0, 0, perBatch, oneMs);
            double first = samples[0], last = samples[^1];
            samples.Sort();
            return new TestStat(samples[samples.Count / 2], samples[0], samples[^1], first, last, samples.Count, perBatch, oneMs);
        }

        private static bool Batch(DeviceContext ctx, Query qDis, Query qBeg, Query qEnd, Action issue, int n, CancellationToken ct, out double gpuMs)
        {
            gpuMs = 0;
            ctx.Begin(qDis);
            ctx.End(qBeg);
            for (int i = 0; i < n; i++) issue();
            ctx.End(qEnd);
            ctx.End(qDis);
            ctx.Flush();
            var sw = Stopwatch.StartNew();
            QueryDataTimestampDisjoint dis;
            while (!ctx.GetData(qDis, out dis)) { if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false; Thread.Sleep(1); }
            long t0, t1;
            while (!ctx.GetData(qBeg, out t0)) { if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false; Thread.Sleep(1); }
            while (!ctx.GetData(qEnd, out t1)) { if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 10_000) return false; Thread.Sleep(1); }
            if (dis.Disjoint || dis.Frequency <= 0 || t1 <= t0) return false;
            gpuMs = (t1 - t0) * 1000.0 / dis.Frequency;
            return true;
        }

        private static byte[]? Compile(string hlsl, string entry, string profile)
        {
            try
            {
                var bc = SharpDX.D3DCompiler.ShaderBytecode.Compile(hlsl, entry, profile,
                    SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3, SharpDX.D3DCompiler.EffectFlags.None);
                return bc.Bytecode?.Data;
            }
            catch (Exception ex) { AppLogger.Debug($"GpuSuiteBenchmark compile {entry}: {ex.Message}"); return null; }
        }

        private static string AdapterName(D3D11Device device)
        {
            try
            {
                using var dxgi = device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgi.GetParent<Adapter>();
                return adapter.Description.Description.Trim();
            }
            catch { return ""; }
        }

        // the quads are made from the vertex number (no vertex buffer): quad = vid / 6, corner = vid % 6
        private const string RenderHlsl = @"
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float layer : TEXCOORD1; };
VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    const uint G = 48;
    uint q = vid / 6, c = vid % 6;
    float2 corner = float2((c == 1 || c == 2 || c == 4) ? 1 : 0, (c == 2 || c == 4 || c == 5) ? 1 : 0);
    float2 p = (float2(q % G, q / G) + corner) / G;
    VSOut o;
    o.pos = float4(p.x * 2 - 1, 1 - p.y * 2, 0.5, 1);
    o.uv = p;
    o.layer = iid;
    return o;
}
float4 PSMain(VSOut i) : SV_Target
{
    float3 n = normalize(float3(sin(i.uv * 12.0 + i.layer), 1.0));
    float3 c = 0;
    [unroll] for (int k = 0; k < 24; k++)
    {
        float3 l = normalize(float3(cos(k * 0.7 + i.layer), sin(k * 1.3), 0.8));
        float d = saturate(dot(n, l));
        c += d * float3(0.02 * k, 0.5, 0.9) + pow(d, 16.0) * 0.05;
    }
    return float4(c * 0.04, 1);
}";

        // Read bandwidth: thread i loads 16-byte words i, i + T, i + 2T … (neighbouring threads read neighbouring words, the
        // way the card reads fastest). The XOR of what was read is written only if it equals a value it never has, so the
        // compiler cannot drop the loads and almost nothing is written.
        private const string ReadHlsl = @"
ByteAddressBuffer Src : register(t0);
RWByteAddressBuffer Sink : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    const uint T = 262144;
    uint4 acc = uint4(0, 0, 0, 0);
    [unroll] for (uint i = 0; i < 16; i++)
        acc ^= Src.Load4((id.x + i * T) * 16);
    uint s = acc.x ^ acc.y ^ acc.z ^ acc.w;
    if (s == 0x9E3779B9u) Sink.Store((id.x & 1023) * 4, s);
}";

        // 16.11.2026: the old loop (v = v × const + const, 256 times) gave 7 455 GFLOPS on an Intel UHD (real peak ~420): the
        // driver's compiler can merge a chain of the same affine step with constants. Now each step multiplies two values that
        // change every step (x = y·x + y; y = x·y + x — two pairs), from values read from memory: nothing can be merged or
        // worked out in advance. Same work per thread as before: 4 multiply-adds × 256 = 2 048 operations.
        private const string ComputeHlsl = @"
RWByteAddressBuffer Output : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint addr = (id.x & 0xFFFFF) * 4;
    float s = asfloat(Output.Load(addr));
    float x0 = s + 0.11f, y0 = s * 0.37f + (float)(id.x & 7) * 0.01f;
    float x1 = s + 0.23f, y1 = s * 0.71f + (float)(id.x & 15) * 0.01f;
    [loop] for (int i = 0; i < 256; i++)
    {
        x0 = mad(y0, x0, y0);
        y0 = mad(x0, y0, x0);
        x1 = mad(y1, x1, y1);
        y1 = mad(x1, y1, x1);
    }
    Output.Store(addr, asuint(x0 + y0 + x1 + y1));
}";

    }
}
