using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace SMDWin.Services
{
    public sealed class GpuStressService : IDisposable
    {
        [DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        private static void PinToLastLogicalCore()
        {
            try
            {
                int lastCore = Math.Max(0, Environment.ProcessorCount - 1);
                UIntPtr mask = (UIntPtr)(1UL << lastCore);
                SetThreadAffinityMask(GetCurrentThread(), mask);
            }
            catch (Exception ex) { AppLogger.Debug($"GpuStressService.PinToLastLogicalCore: {ex.Message}"); }
        }

        /// <summary>State of a single run. Each Start() gets a NEW instance, so an old thread (still stopping) cannot be
        /// "revived" by a quick Start() and cannot change the state of the new run.</summary>
        private sealed class RunState
        {
            public volatile bool StopRequested;
            public int PriorityReleased;          // 0/1 — StressPriorityCoordinator notified only once
            public Thread? Thread;
        }

        private readonly object _runLock = new();
        private RunState? _currentRun;
        private volatile bool _running = false;

        /// <summary>True if the GPU driver was reset (TDR / device removed) during the last run.</summary>
        public bool DriverResetDetected { get; private set; }

        private static void ReleasePriorityOnce(RunState run)
        {
            if (System.Threading.Interlocked.Exchange(ref run.PriorityReleased, 1) == 0)
                StressPriorityCoordinator.NotifyStressStopped();
        }

        public bool   IsRunning  => _running;
        private DateTime? _startedUtc;
        public DateTime? StartedUtc => _running ? _startedUtc : null;
        public long   DispatchHz { get; private set; }
        public string StatusText { get; private set; } = "Idle";
        public string LastError  { get; private set; } = "";
        public volatile bool EnduranceMode = false;
        private static int _gpuIntegrityErrorCount;
        public static int IntegrityErrorCount => _gpuIntegrityErrorCount;

        public void Start()
        {
            RunState run;
            lock (_runLock)
            {
                if (_currentRun != null) return;
                run = new RunState();
                _currentRun = run;
                StressPriorityCoordinator.NotifyStressStarted();
                _running    = true;
                _startedUtc = DateTime.UtcNow;
                LastError   = "";
                DriverResetDetected = false;
                StatusText  = "Starting…";
                DispatchHz  = 0;
                System.Threading.Interlocked.Exchange(ref _gpuIntegrityErrorCount, 0);
                run.Thread = new Thread(() => StressLoop(run))
                {
                    IsBackground = true,
                    Priority     = ThreadPriority.BelowNormal,
                    Name         = "GpuStress_DX11"
                };
                run.Thread.SetApartmentState(ApartmentState.MTA);
            }
            run.Thread.Start();
        }

        public void Stop()
        {
            RunState? run;
            lock (_runLock)
            {
                run = _currentRun;
                if (run == null) return;
                _currentRun = null;
                run.StopRequested = true;
                _startedUtc = null;
                _running    = false;   // imediat — UI vede oprit
                StatusText  = "Stopping…";
                DispatchHz  = 0;
            }
            ReleasePriorityOnce(run);
            var t = run.Thread;
            if (t != null)
                System.Threading.Tasks.Task.Run(() =>
                {
                    // Join with a longer timeout for slow drivers (e.g. Boot Camp / GT 755M)
                    if (!t.Join(2000))
                    {
                        try { t.Interrupt(); } catch (Exception logEx) { AppLogger.Warning(logEx, "GpuStressService.Stop.Interrupt"); }
                        if (!t.Join(500))
                            AppLogger.Warning("GpuStressService: thread did not exit after Interrupt — process will clean up on exit");
                    }
                    lock (_runLock)
                    {
                        // Only if no new run has started meanwhile
                        if (_currentRun == null && string.IsNullOrEmpty(LastError)) StatusText = "Stopped";
                    }
                });
        }

        public void Dispose() => Stop();

        private void StressLoop(RunState run)
        {
            PinToLastLogicalCore();

            D3D11Device?          device  = null;
            DeviceContext?        ctx     = null;
            Texture2D?            bigRT   = null;
            RenderTargetView?     rtv     = null;
            SharpDX.Direct3D11.Buffer? uavBuf = null;
            UnorderedAccessView?  uav     = null;
            SharpDX.Direct3D11.Buffer? verifyBuf     = null;
            UnorderedAccessView?       verifyUav     = null;
            SharpDX.Direct3D11.Buffer? verifyStaging = null;
            ComputeShader?             verifyShader  = null;
            ComputeShader?        cs      = null;
            var                   pacingQueries = new List<Query>();   // frames in flight on the GPU (the queue does not drain between frames)
            bool                  usedWarp    = false;
            // integrated graphics (no dedicated video memory): uses the computer's memory and bus, so a full-load test
            // slows down the whole system, including the app window
            bool                  sharedMemoryGpu = false;

            try
            {
                Adapter1? preferredAdapter = null;
                try
                {
                    using var factory1 = new SharpDX.DXGI.Factory1();
                    long bestVram = -1;
                    for (int i = 0; i < factory1.GetAdapterCount1(); i++)
                    {
                        var a = factory1.GetAdapter1(i);
                        if ((a.Description1.Flags & AdapterFlags.Software) != 0) { a.Dispose(); continue; } // sarim WARP/Basic
                        long vram = (long)a.Description1.DedicatedVideoMemory;
                        AppLogger.Debug($"GpuStressService: adaptor #{i} '{a.Description1.Description}' — VRAM dedicat {vram / 1024 / 1024} MB");
                        if (vram > bestVram)
                        {
                            bestVram = vram;
                            preferredAdapter?.Dispose();
                            preferredAdapter = a;
                        }
                        else
                        {
                            a.Dispose();
                        }
                    }
                    if (preferredAdapter != null)
                    {
                        sharedMemoryGpu = bestVram >= 0 && bestVram < 1024L * 1024 * 1024;
                        AppLogger.Info($"GpuStressService: picked the adapter with the most dedicated VRAM ({bestVram / 1024 / 1024} MB) for the stress" +
                            (sharedMemoryGpu ? " — integrated graphics: smaller memory fills and a short pause between frames, so the system stays usable." : "."));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"GpuStressService: DXGI adapter enumeration failed ({ex.Message}) — falling back to the default adapter.");
                    preferredAdapter = null;
                }

                // D3D11 init with a timeout — on Boot Camp / GT 755M it can block the UI if the driver does not answer promptly.
                // Try hardware, fall back to WARP after 3 s. The hardware init writes ONLY to its own variable. If it takes longer
                // than 3 s it is marked abandoned: when (and if) it finishes, it releases its device and adapter itself — it cannot
                // overwrite the WARP device used in the meantime.
                var initGate = new object();
                bool initFinished = false, initAbandoned = false;
                D3D11Device? hwDevice = null;
                Exception? initEx = null;
                var adapterForInit = preferredAdapter;
                using (var initDone = new System.Threading.ManualResetEventSlim(false))
                {
                    var doneEvent = initDone;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        D3D11Device? created = null;
                        Exception? err = null;
                        try
                        {
                            created = adapterForInit != null
                                ? new D3D11Device(adapterForInit, DeviceCreationFlags.None,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 })
                                : new D3D11Device(DriverType.Hardware, DeviceCreationFlags.None,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
                        }
                        catch (Exception ex) { err = ex; }
                        lock (initGate)
                        {
                            initFinished = true;
                            if (initAbandoned)
                            {
                                try { created?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                                try { adapterForInit?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                                AppLogger.Info("GpuStress: the D3D11 hardware init finished after the timeout — device released (WARP was already in use).");
                                return;
                            }
                            hwDevice = created; initEx = err;
                            try { doneEvent.Set(); } catch (ObjectDisposedException) { }
                        }
                    });

                    bool inTime = initDone.Wait(3000);
                    lock (initGate)
                    {
                        if (!initFinished) initAbandoned = true;
                        else inTime = true;
                    }
                    if (inTime && initEx == null && hwDevice != null)
                    {
                        device = hwDevice;
                        preferredAdapter?.Dispose();
                    }
                    else
                    {
                        // Fallback to the software rasterizer (WARP) — does not stress the physical GPU, but at least does not block the app
                        AppLogger.Warning($"D3D11 Hardware init failed/timeout ({initEx?.Message ?? "timeout 3s"}), falling back to WARP");
                        if (inTime) { try { hwDevice?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } preferredAdapter?.Dispose(); }
                        // if it timed out, the adapter is released by the init thread when it finishes
                        device = new D3D11Device(DriverType.Warp, DeviceCreationFlags.None);
                        StatusText = "Running (WARP — driver issue)";
                        usedWarp   = true;
                    }
                    preferredAdapter = null;
                }

                if (run.StopRequested) return;
                ctx = device.ImmediateContext;

                StatusText = $"GPU: {GetAdapterName(device)}";

                bigRT = new Texture2D(device, new Texture2DDescription
                {
                    Width = 4096, Height = 4096, MipLevels = 1, ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                });
                rtv = new RenderTargetView(device, bigRT);

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
                    {
                        FirstElement = 0, ElementCount = 4 * 1024 * 1024,
                        Flags = UnorderedAccessViewBufferFlags.Raw
                    }
                });

                cs = CompileComputeShader(device);
                ctx.Rasterizer.SetViewport(0, 0, 4096, 4096);
                ctx.OutputMerger.SetRenderTargets(rtv);

                verifyShader = CompileVerifyShader(device);
                if (verifyShader != null)
                {
                    try
                    {
                        verifyBuf = new SharpDX.Direct3D11.Buffer(device, new BufferDescription
                        {
                            SizeInBytes       = VerifyElementCount * 4,
                            Usage             = ResourceUsage.Default,
                            BindFlags         = BindFlags.UnorderedAccess,
                            CpuAccessFlags    = CpuAccessFlags.None,
                            OptionFlags       = ResourceOptionFlags.BufferAllowRawViews,
                            StructureByteStride = 0
                        });
                        verifyUav = new UnorderedAccessView(device, verifyBuf, new UnorderedAccessViewDescription
                        {
                            Format    = Format.R32_Typeless,
                            Dimension = UnorderedAccessViewDimension.Buffer,
                            Buffer    = new UnorderedAccessViewDescription.BufferResource
                            {
                                FirstElement = 0, ElementCount = VerifyElementCount,
                                Flags = UnorderedAccessViewBufferFlags.Raw
                            }
                        });
                        // Staging — the only buffer type the CPU can read directly (Map/Unmap); the buffer above (Default+UAV) allows no CPU access.
                        verifyStaging = new SharpDX.Direct3D11.Buffer(device, new BufferDescription
                        {
                            SizeInBytes    = VerifyElementCount * 4,
                            Usage          = ResourceUsage.Staging,
                            BindFlags      = BindFlags.None,
                            CpuAccessFlags = CpuAccessFlags.Read,
                            OptionFlags    = ResourceOptionFlags.None
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"GpuStressService: verification buffer init failed ({ex.Message}) — the stress continues without verification.");
                        verifyBuf?.Dispose(); verifyBuf = null;
                        verifyUav?.Dispose(); verifyUav = null;
                        verifyStaging?.Dispose(); verifyStaging = null;
                    }
                }

                for (int qi = 0; qi < 3; qi++)
                {
                    try { pacingQueries.Add(new Query(device, new QueryDescription { Type = QueryType.Event })); }
                    catch (Exception qex) { AppLogger.Warning(qex, "GpuStress.PacingQuery"); break; }
                }

                StatusText = cs != null ? "Running" : "Running (render only)";

                bool onWarp = usedWarp;
                int dispatchGroups   = onWarp ? 512 : 65536;
                int dispatchesPerFrm = onWarp ? 1   : 4;
                // 16 fills × 64 MB = 1 GB of memory writes per frame: fine on a card with its own memory, but on integrated graphics
                // it occupies the whole computer's memory bus (the processor and the interface are left without it)
                int clearsPerFrame = sharedMemoryGpu ? 2 : 16;

                long count = 0;
                var  timer = DateTime.UtcNow;
                int  frame = 0, framesThisSecond = 0;
                var  verifyTimer = System.Diagnostics.Stopwatch.StartNew();

                while (!run.StopRequested)
                {
                    frame++;
                    float ft = frame * 0.01f;

                    if (run.StopRequested) break; // double check — run.StopRequested may be set in the middle of the iteration

                    if (cs != null)
                    {
                        bool skipThisFrame = EnduranceMode && (frame % 5 == 0);
                        if (!skipThisFrame)
                        {
                            ctx.ComputeShader.Set(cs);
                            ctx.ComputeShader.SetUnorderedAccessView(0, uav);
                            for (int d = 0; d < dispatchesPerFrm; d++)
                            {
                                if (run.StopRequested) goto stressLoopExit;
                                ctx.Dispatch(dispatchGroups, 1, 1);
                            }
                            ctx.ComputeShader.Set(null);
                            ctx.ComputeShader.SetUnorderedAccessView(0, null);
                        }
                    }

                    if (verifyUav != null && verifyStaging != null && verifyShader != null
                        && verifyTimer.ElapsedMilliseconds >= 2000)
                    {
                        RunIntegrityCheck(device, ctx, verifyShader, verifyUav, verifyBuf!, verifyStaging);
                        verifyTimer.Restart();
                    }

                    if (run.StopRequested) break;
                    for (int i = 0; i < clearsPerFrame; i++)
                    {
                        if ((i & 7) == 0 && run.StopRequested) goto stressLoopExit;
                        ctx.ClearRenderTargetView(rtv,
                            new SharpDX.Mathematics.Interop.RawColor4(
                                0.5f + 0.5f * MathF.Sin(ft + i * 0.07f),
                                0.5f + 0.5f * MathF.Cos(ft + i * 0.05f),
                                0.5f + 0.5f * MathF.Sin(ft * 1.3f + i * 0.09f),
                                1f));
                    }
                    if (run.StopRequested) break;

                    ctx.Flush();
                    count += dispatchesPerFrm;

                    framesThisSecond++;
                    if (pacingQueries.Count > 0)
                    {
                        // Two frames always stay queued while waiting for the previous one: the card never runs out of work between frames
                        // (otherwise its utilization drops towards 70 %, especially on fast cards).
                        int n = pacingQueries.Count;
                        ctx.End(pacingQueries[frame % n]);
                        if (frame > n - 1)
                        {
                            var oldest = pacingQueries[(frame - (n - 1)) % n];
                            var paceSw = System.Diagnostics.Stopwatch.StartNew();
                            while (!ctx.GetData(oldest, out SharpDX.Mathematics.Interop.RawBool done) || !done)
                            {
                                if (run.StopRequested) goto stressLoopExit;
                                if (paceSw.ElapsedMilliseconds > 5000) break;
                                if (paceSw.ElapsedMilliseconds < 4) Thread.Yield(); else Thread.Sleep(1);
                            }
                        }
                    }

                    var now = DateTime.UtcNow;
                    if ((now - timer).TotalSeconds >= 1.0)
                    {
                        DispatchHz = count;
                        count = 0;
                        timer = now;
                        // frames are too short (fast card): more work is put into one frame until a frame takes ~25-50 ms.
                        // Still well under the 2 s limit after which Windows resets the driver.
                        if (!onWarp && !sharedMemoryGpu && framesThisSecond > 40 && dispatchesPerFrm < 64)
                        {
                            dispatchesPerFrm = Math.Min(64, dispatchesPerFrm * 2);
                            AppLogger.Debug($"GpuStress: {framesThisSecond} frames/s — dispatches per frame: {dispatchesPerFrm}");
                        }
                        framesThisSecond = 0;
                    }
                    Thread.Sleep(sharedMemoryGpu ? 2 : 0); // yield — also makes the thread interruptible; on integrated graphics, 2 ms of breathing room for the rest of the system
                }
                stressLoopExit: ;
            }
            catch (ThreadInterruptedException)
            {
                // Stop() called Interrupt() — clean exit, no error
            }
            catch (Exception ex)
            {
                bool isCurrent;
                lock (_runLock) isCurrent = ReferenceEquals(_currentRun, run);
                if (IsDeviceLost(ex, device))
                {
                    AppLogger.Warning(ex, "GpuStress: driverul GPU s-a resetat (TDR / device removed) in timpul testului");
                    if (isCurrent)
                    {
                        DriverResetDetected = true;
                        LastError  = "GPU driver reset (TDR) during the test — possible GPU or driver instability.";
                        StatusText = "Error: GPU driver reset (TDR)";
                    }
                }
                else
                {
                    AppLogger.Warning(ex, "GpuStress: the run stopped with an error");
                    if (isCurrent)
                    {
                        LastError  = ex.Message;
                        StatusText = $"Error: {ex.Message}";
                    }
                }
            }
            finally
            {
                foreach (var pq in pacingQueries) { try { pq.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                cs?.Dispose();
                uav?.Dispose();
                uavBuf?.Dispose();
                rtv?.Dispose();
                bigRT?.Dispose();
                verifyShader?.Dispose();
                verifyUav?.Dispose();
                verifyBuf?.Dispose();
                verifyStaging?.Dispose();
                ctx?.Dispose();
                device?.Dispose();
                // the thread releases its priority only once (also when exiting on an error),
                // but changes the public state ONLY if it is still the current run.
                ReleasePriorityOnce(run);
                lock (_runLock)
                {
                    if (ReferenceEquals(_currentRun, run))
                    {
                        _currentRun = null;
                        _running    = false;
                        _startedUtc = null;
                        DispatchHz  = 0;
                        if (string.IsNullOrEmpty(LastError)) StatusText = "Stopped";
                    }
                }
            }
        }

        private static bool IsDeviceLost(Exception ex, D3D11Device? device)
        {
            const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
            const int DXGI_ERROR_DEVICE_HUNG    = unchecked((int)0x887A0006);
            const int DXGI_ERROR_DEVICE_RESET   = unchecked((int)0x887A0007);
            int hr = ex is SharpDX.SharpDXException sx ? sx.ResultCode.Code : ex.HResult;
            if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_HUNG || hr == DXGI_ERROR_DEVICE_RESET)
                return true;
            try { return device != null && device.DeviceRemovedReason.Failure; }
            catch { return false; }
        }

        private const int VerifyElementCount = 256; // 1 group of 256 threads, 1KB total

        private static ComputeShader? CompileVerifyShader(D3D11Device device)
        {
            const string hlsl = @"
RWByteAddressBuffer VerifyBuf : register(u0);
[numthreads(256,1,1)]
void CSVerify(uint3 id : SV_DispatchThreadID)
{
    uint addr = id.x * 4;
    float v = asfloat(VerifyBuf.Load(addr));
    [unroll(512)]
    for (int i = 0; i < 512; i++)
        v = mad(v, 1.0000003f, 0.000001f);
    VerifyBuf.Store(addr, asuint(v));
}";
            try
            {
                var bc = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    hlsl, "CSVerify", "cs_5_0",
                    SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3,
                    SharpDX.D3DCompiler.EffectFlags.None);
                return bc.Bytecode == null ? null : new ComputeShader(device, bc.Bytecode);
            }
            catch (Exception ex) { AppLogger.Debug($"GpuStressService: verify shader compilation failed ({ex.Message})."); return null; }
        }

        private static void RunIntegrityCheck(D3D11Device device, DeviceContext ctx, ComputeShader verifyShader,
            UnorderedAccessView verifyUav, SharpDX.Direct3D11.Buffer verifyBuf, SharpDX.Direct3D11.Buffer verifyStaging)
        {
            try
            {
                // New seed for every check — distribution quality does not matter
                // (only a finite, reasonable value that differs from run to run).
                float seed = 1.0f + (float)(new Random().NextDouble() * 0.5);
                var seedData = new float[VerifyElementCount];
                for (int i = 0; i < VerifyElementCount; i++) seedData[i] = seed;

                using (var seedStream = new SharpDX.DataStream(VerifyElementCount * 4, true, true))
                {
                    seedStream.WriteRange(seedData);
                    seedStream.Position = 0;
                    ctx.UpdateSubresource(new SharpDX.DataBox(seedStream.DataPointer, 0, 0), verifyBuf);
                }

                ctx.ComputeShader.Set(verifyShader);
                ctx.ComputeShader.SetUnorderedAccessView(0, verifyUav);
                ctx.Dispatch(1, 1, 1); // 1 grup x 256 threads = VerifyElementCount
                ctx.ComputeShader.Set(null);
                ctx.ComputeShader.SetUnorderedAccessView(0, null);

                ctx.CopyResource(verifyBuf, verifyStaging);

                var box = ctx.MapSubresource(verifyStaging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    float expected = seed;
                    for (int i = 0; i < 512; i++) expected = expected * 1.0000003f + 0.000001f;

                    bool mismatch = false;
                    unsafe
                    {
                        float* p = (float*)box.DataPointer;
                        for (int i = 0; i < VerifyElementCount; i++)
                        {
                            float actual = p[i];
                            if (float.IsNaN(actual) || float.IsInfinity(actual)
                                || Math.Abs(actual - expected) / Math.Max(1e-9f, Math.Abs(expected)) > 1e-4f)
                            {
                                mismatch = true;
                                break;
                            }
                        }
                    }
                    if (mismatch)
                    {
                        System.Threading.Interlocked.Increment(ref _gpuIntegrityErrorCount);
                        AppLogger.Warning($"GpuStressService: correctness check FAILED — mismatch between the GPU result and the expected value (seed={seed:F4}). Total count: {_gpuIntegrityErrorCount}.");
                    }
                }
                finally { ctx.UnmapSubresource(verifyStaging, 0); }
            }
            catch (Exception ex)
            {
                // A check that failed technically (not a value mismatch) is no proof of corruption — it is only logged,
                // not counted as an integrity error.
                AppLogger.Debug($"GpuStressService.RunIntegrityCheck: {ex.Message}");
            }
        }

        private static ComputeShader? CompileComputeShader(D3D11Device device)
        {
            const string hlsl = @"
RWByteAddressBuffer Output : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint addr = (id.x & 0xFFFFF) * 4;
    float v = asfloat(Output.Load(addr));
    [unroll(512)]
    for (int i = 0; i < 512; i++)
        v = mad(v, 1.0000003f, 0.000001f);
    Output.Store(addr, asuint(v));
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
