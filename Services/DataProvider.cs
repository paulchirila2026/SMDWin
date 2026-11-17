// SMDWin — Services/DataProvider.cs
// A single background thread collects ALL data every second, instead of several timers on the UI thread.
// Pattern: System Informer → phlib/provider.c → PhInitializeProviderThread()

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Complete hardware data snapshot for one tick.
    /// Immutable — created on the background thread, read on the UI thread without a lock.
    /// </summary>
    public sealed class DataSnapshot
    {
        // ── CPU ───────────────────────────────────────────────────────────────────
        public float CpuPct         { get; init; } = -1f;   // 0-100, -1 = unknown
        public float CpuTempC       { get; init; } = -1f;   // °C, -1 = unknown
        public float CpuFreqMHz     { get; init; } = -1f;

        // ── GPU ───────────────────────────────────────────────────────────────────
        public float GpuPct         { get; init; } = -1f;
        public float GpuTempC       { get; init; } = -1f;
        public float GpuPowerW      { get; init; } = -1f;
        public uint  GpuFanRpm      { get; init; } = 0;

        // ── RAM ───────────────────────────────────────────────────────────────────
        public float RamUsedMB      { get; init; } = -1f;
        public float RamTotalMB     { get; init; } = -1f;
        public float RamPct         { get; init; } = -1f;   // 0-100

        // ── DISK ──────────────────────────────────────────────────────────────────
        public float DiskReadKBs    { get; init; } = 0f;
        public float DiskWriteKBs   { get; init; } = 0f;
        public float DiskActivePct  { get; init; } = 0f;    // 0-100

        // ── NETWORK ───────────────────────────────────────────────────────────────
        public float NetDownKBs     { get; init; } = 0f;
        public float NetUpKBs       { get; init; } = 0f;

        // ── PING ──────────────────────────────────────────────────────────────────
        public float PingMs         { get; init; } = -1f;   // -1 = netestat

        // ── TIMESTAMP ─────────────────────────────────────────────────────────────
        public DateTime Timestamp   { get; init; } = DateTime.UtcNow;

        // ── Helpers ───────────────────────────────────────────────────────────────
        public bool HasCpuTemp  => CpuTempC  >= 0f;
        public bool HasGpuTemp  => GpuTempC  >= 0f;
        public bool HasGpuPower => GpuPowerW >= 0f;
    }

    /// <summary>
    /// Provider thread: collects data in the background and notifies the UI thread through an event.
    /// </summary>
    public sealed class DataProvider : IDisposable
    {
        // ── Readers ───────────────────────────────────────────────────────────────
        private readonly NtDiskReader     _diskReader    = new();
        private readonly NetworkReader    _netReader     = new();

        // ── Internal state ─────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task?                    _task;
        private int                      _intervalMs = 1000;

        // ── External references (set by MainWindow after construction) ─────────────
        /// <summary>Reads the current CPU % (from MainWindow, via PerformanceCounter or NtQuery).</summary>
        public Func<float>?        GetCpuPct    { get; set; }
        /// <summary>Reads GPU % / temperature (from TempReader / LHM).</summary>
        public Func<(float pct, float tempC, float powerW, uint fanRpm)>? GetGpuData { get; set; }
        /// <summary>Reads CPU temperature (from TempReader / LHM).</summary>
        public Func<float>?        GetCpuTemp   { get; set; }
        /// <summary>Reads RAM.</summary>
        public Func<(float usedMB, float totalMB)>? GetRam { get; set; }
        /// <summary>Reads ping (optional, may be null).</summary>
        public Func<float>?        GetPing      { get; set; }

        // ── Event ─────────────────────────────────────────────────────────────────
        /// <summary>Raised on the UI thread (through the Dispatcher) on every new tick.</summary>
        public event Action<DataSnapshot>? SnapshotReady;

        /// <summary>Dispatcher invoke helper — set by MainWindow at creation.</summary>
        public Func<Action, Task>? DispatcherInvoke { get; set; }

        // ── Last published value ───────────────────────────────────────────────
        public DataSnapshot? Latest { get; private set; }

        // ── Interval ──────────────────────────────────────────────────────────────
        public int IntervalMs
        {
            get => _intervalMs;
            set => _intervalMs = Math.Clamp(value, 250, 10_000);
        }

        // ── Start / Stop ──────────────────────────────────────────────────────────
        public void Start()
        {
            Stop();
            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => ProviderLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _task?.Wait(2000); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            _cts?.Dispose();
            _cts  = null;
            _task = null;
        }

        public void Dispose() => Stop();

        // ── Provider loop (background thread) ────────────────────────────────────
        private async Task ProviderLoop(CancellationToken ct)
        {
            // Warm-up: the first disk/net reading returns 0 (no delta yet)
            _diskReader.Update();
            _netReader.Update();
            await Task.Delay(Math.Min(_intervalMs, 1000), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();

                var snap = CollectAll();
                Latest = snap;

                // Notify the UI thread
                if (DispatcherInvoke != null && SnapshotReady != null)
                {
                    try
                    {
                        await DispatcherInvoke(() =>
                        {
                            try { SnapshotReady?.Invoke(snap); }
                            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* a UI handler must not bring down the provider */ }
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _ = Task.Run(() => AppLogger.Warning(ex, "DataProvider.Dispatch"));
                    }
                }

                // Wait for the rest of the interval (minus the time spent reading)
                int elapsed = (int)sw.ElapsedMilliseconds;
                int remaining = _intervalMs - elapsed;
                if (remaining > 10)
                {
                    try { await Task.Delay(remaining, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // ── Colectare date (background thread) ───────────────────────────────────
        private DataSnapshot CollectAll()
        {
            // DISK
            _diskReader.Update();

            // NETWORK
            _netReader.Update();

            // CPU
            float cpuPct = -1f;
            try { cpuPct = GetCpuPct?.Invoke() ?? -1f; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            // GPU
            float gpuPct = -1f, gpuTemp = -1f, gpuPower = -1f;
            uint  gpuFan = 0;
            try
            {
                var g = GetGpuData?.Invoke();
                if (g.HasValue)
                { gpuPct = g.Value.pct; gpuTemp = g.Value.tempC;
                  gpuPower = g.Value.powerW; gpuFan = g.Value.fanRpm; }
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            // CPU TEMP
            float cpuTemp = -1f;
            try { cpuTemp = GetCpuTemp?.Invoke() ?? -1f; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            // RAM
            float ramUsed = -1f, ramTotal = -1f, ramPct = -1f;
            try
            {
                var r = GetRam?.Invoke();
                if (r.HasValue)
                {
                    ramUsed  = r.Value.usedMB;
                    ramTotal = r.Value.totalMB;
                    ramPct   = ramTotal > 0 ? ramUsed / ramTotal * 100f : -1f;
                }
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            // PING (optional)
            float ping = -1f;
            try { ping = GetPing?.Invoke() ?? -1f; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            return new DataSnapshot
            {
                CpuPct        = cpuPct,
                CpuTempC      = cpuTemp,
                GpuPct        = gpuPct,
                GpuTempC      = gpuTemp,
                GpuPowerW     = gpuPower,
                GpuFanRpm     = gpuFan,
                RamUsedMB     = ramUsed,
                RamTotalMB    = ramTotal,
                RamPct        = ramPct,
                DiskReadKBs   = _diskReader.LastReadKBs,
                DiskWriteKBs  = _diskReader.LastWriteKBs,
                DiskActivePct = _diskReader.LastActivePct,
                NetDownKBs    = _netReader.TotalDownKBs,
                NetUpKBs      = _netReader.TotalUpKBs,
                PingMs        = ping,
                Timestamp     = DateTime.UtcNow
            };
        }
    }
}
