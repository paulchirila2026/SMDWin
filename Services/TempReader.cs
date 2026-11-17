using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{

    public partial class TempReader : IDisposable
    {
        private Computer? _computer;

        private float? _hwIntegratedGpuTemp;
        private float? _hwIntegratedGpuLoad;
        private string _hwIntegratedGpuName = "--";

        // ── FAN CONTROL ──────────────────────────────────────────────────────
        // A controllable fan discovered through LHM. It has a Control sensor (duty cycle %) with IControl.SetSoftware()/
        // SetDefault(). The RPM (if any) comes from a paired Fan sensor on the same hardware.
        public class FanController
        {
            public string Name = "";
            public float? Rpm;          // null if the board reports no RPM (duty only)
            public float DutyPercent;   // pozitia curenta a controlului (0-100)
            public bool CanControl;     // true if IControl is available (writable)
            internal ISensor? ControlSensor;
        }

        /// <summary>
        /// Lists the detected fans. CanControl=true where the speed can be set.
        /// Empty list => the hardware exposes no fans (e.g. iMac/Boot Camp) => the UI shows N/A.
        /// </summary>
        public List<FanController> GetFans()
        {
            var result = new List<FanController>();
            if (_computer == null) return result;
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Motherboard &&
                        hw.HardwareType != HardwareType.SuperIO &&
                        hw.HardwareType != HardwareType.EmbeddedController &&
                        hw.HardwareType != HardwareType.Cooler) continue;
                    try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                    var all = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).ToList();
                    var controls = all.Where(s => s.SensorType == SensorType.Control).ToList();
                    var fans     = all.Where(s => s.SensorType == SensorType.Fan).ToList();

                    foreach (var ctrl in controls)
                    {
                        var rpm = fans.FirstOrDefault(f => f.Index == ctrl.Index)
                                 ?? fans.FirstOrDefault(f => (f.Name ?? "") == (ctrl.Name ?? ""));
                        result.Add(new FanController
                        {
                            Name         = $"{ctrl.Name}",
                            Rpm          = rpm?.Value,
                            DutyPercent  = ctrl.Value ?? 0,
                            CanControl   = ctrl.Control != null,
                            ControlSensor = ctrl
                        });
                    }
                    foreach (var fan in fans.Where(f => !controls.Any(c => c.Index == f.Index)))
                    {
                        result.Add(new FanController
                        {
                            Name = $"{fan.Name}", Rpm = fan.Value, DutyPercent = 0, CanControl = false
                        });
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug("GetFans: " + ex.Message); }
            return result;
        }

        /// <summary>Sets the speed (0-100%) for a controllable fan.</summary>
        public bool SetFanDuty(FanController fan, float percent)
        {
            try
            {
                if (fan?.ControlSensor?.Control == null) return false;
                percent = Math.Clamp(percent, 0, 100);
                fan.ControlSensor.Control.SetSoftware(percent);
                return true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SetFanDuty"); return false; }
        }

        /// <summary>Returns the fan to the board's automatic control (BIOS).</summary>
        public bool SetFanAuto(FanController fan)
        {
            try
            {
                if (fan?.ControlSensor?.Control == null) return false;
                fan.ControlSensor.Control.SetDefault();
                return true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SetFanAuto"); return false; }
        }

        public string Backend { get; private set; } = "none";
        private bool _gpuDetected = false;   // becomes true once a GPU hardware node is found
        private bool _d3dGpuPermFailed = false;
        private bool _msrPermFailed = false;
        private uint _msrTjMax = 100;
        private bool _msrTjMaxDetected = false;
        private bool _lhmWarmedUp = false;
        private LhmSensorMode _currentMode  = LhmSensorMode.Full; // starts full, caller can narrow
        private readonly object _modeLock   = new();
        private System.DateTime _lastModeChange = System.DateTime.MinValue;
        private float? _lhmLastCpuTemp = null;
        private int    _lhmStaleCount  = 0;
        private System.DateTime _lhmLastValueChangeUtc = System.DateTime.MinValue;
        private const double StressFreezeMinSeconds = 30;
        private const double LoadVariesFreezeMinSeconds = 45;

        // Older processors (and some laptops' embedded controllers) report the temperature in whole degrees and refresh it only
        // every few seconds, so the same value for a minute while the load moves is normal there, not a stuck sensor. Once the
        // readings are seen to be whole numbers only, the "frozen" checks wait longer and the "too cold for this load" check
        // is stricter (an old CPU with a wrong TjMax can read 10-15 °C low and would otherwise be restarted in a loop).
        private int  _cpuTempIntSamples, _cpuTempFracSamples;
        private bool CpuTempCoarse => _cpuTempIntSamples >= 12 && _cpuTempFracSamples == 0;
        private double FreezeScale => CpuTempCoarse ? 2.5 : 1.0;
        private double StressFreezeSeconds   => StressFreezeMinSeconds * FreezeScale;
        private double LoadVariesFreezeSeconds => LoadVariesFreezeMinSeconds * FreezeScale;
        private float  ImplausibleLoadMin => CpuTempCoarse ? 60f : StaleLoadImplausibleMin;
        private float  ImplausibleTempC   => CpuTempCoarse ? 28f : StaleTempImplausibleC;
        private void NoteCpuTempResolution(float t)
        {
            if (Math.Abs(t - MathF.Round(t)) < 0.01f) { if (_cpuTempIntSamples < 1000) _cpuTempIntSamples++; }
            else _cpuTempFracSamples++;
            if (_cpuTempIntSamples == 12 && _cpuTempFracSamples == 0)
                AppLogger.Info("CPU temperature is reported in whole degrees — the frozen-sensor checks are more tolerant");
        }
        private int    _lhmForceUpdateCount = 0;
        private int    _lhmNullCount = 0;
        // restarts of the sensor library because the processor temperature was missing; stopped after a few when it never
        // gave a temperature in this session (no sensor driver: the value cannot appear by restarting)
        private int    _lhmNullReinits = 0;
        private bool   _lhmEverCpuTemp = false;
        private bool   _lhmNullReinitsStopped = false;
        private const int MaxNullReinitsWithoutTemp = 3;

        private static bool SafePawnIoInstalled()
        {
            try { return PawnIOService.IsInstalled(); }
            catch (Exception ex) { AppLogger.Debug($"SafePawnIoInstalled: {ex.Message}"); return false; }
        }
        private readonly Dictionary<string, System.Diagnostics.Stopwatch> _gpuLastUpdateSw = new();
        private const int GpuThrottleIntervalMs = 2000;
        private bool   _staleAccepted  = false;
        private bool   _staleLoggedOnce = false;
        private System.DateTime _staleAcceptedAtUtc = System.DateTime.MinValue;
        private static readonly System.TimeSpan StaleAcceptedMaxAge = System.TimeSpan.FromSeconds(30);
        private readonly Queue<float> _lhmLoadHistory = new();
        private readonly Queue<string> _lhmValueHistory = new();
        private const int StaleValueHistoryMax = 20; // ~20 recent ticks before freezing
        private void RecordLhmValueSample(float? cpuTemp, float? freqMHz, float? loadPct = null)
        {
            string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
            string freqTxt = freqMHz.HasValue ? freqMHz.Value.ToString("F0") : "null";
            string loadTxt = loadPct.HasValue ? loadPct.Value.ToString("F0") : "null";
            _lhmValueHistory.Enqueue($"{ts} temp={(cpuTemp.HasValue ? cpuTemp.Value.ToString("F1") : "null")}°C freq={freqTxt}MHz load={loadTxt}% mode={_currentMode}");
            while (_lhmValueHistory.Count > StaleValueHistoryMax) _lhmValueHistory.Dequeue();
        }
        private void DumpLhmValueHistory()
        {
            try { AppLogger.Warning("TempReader: value history before the freeze —" + string.Join(" | ", _lhmValueHistory)); }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }
        private const int   StaleLoadHistoryMax  = 15;   // ~15 recent reads (15-45s, not 6-18s)
        private const float StaleLoadVarianceMin = 20f;  // % — below this the load counts as "stable" (used by the TEMPERATURE watchdog)
        private const float StaleLoadImplausibleMin = 25f;  // % Load — above this, the CPU clearly does real work
        private const float StaleTempImplausibleC   = 35f;  // °C — physically impossible below this under real sustained load
        private const int   StaleNullReinitThreshold = 3;
        private int _cpuHwUpdateFailStreak = 0;
        private const int CpuHwUpdateFailThreshold = 2;
        private const float StaleFreqLoadVarianceMin = 20f;
        private float? _lhmLastCpuFreq  = null;
        private float? _lhmFreqReinitMHz = null;
        private readonly System.Collections.Generic.List<float> _lhmFixedClocks = new();   // learned fixed steps (not cleared when the value changes)
        private int _lhmFreqReinitCount = 0;
        private const int MaxFreqReinitsPerSession = 2;
        private bool IsKnownFixedClock(float mhz) { foreach (var f in _lhmFixedClocks) if (Math.Abs(f - mhz) < 0.5f) return true; return false; }
        private void LearnFixedClock(float mhz) { if (!IsKnownFixedClock(mhz)) { if (_lhmFixedClocks.Count >= 16) _lhmFixedClocks.RemoveAt(0); _lhmFixedClocks.Add(mhz); } }
        private int    _lhmFreqStaleCount = 0;
        private const int StaleFreqReinitThreshold = 6;
        // the lowest frequency seen in the session = the processor's idle "floor"; staying there is not a stuck sensor
        private float? _lhmMinFreqSeen;
        private float? _lhmMaxFreqSeen;
        private readonly object _readLock = new();
        private static readonly object _globalNativeAccessLock = new();

        public bool StressActive { get; set; } = false;
        private int _stressLoadLogTickCount = 0;

        // open instances: two LibreHardwareMonitor instances reading the same processor at the same time interfere
        // (NullReferenceException in IntelCpu.Update, then reinit) — so occasional reads borrow an existing one
        private static readonly System.Collections.Generic.List<TempReader> _live = new();
        private static readonly object _liveLock = new();

        /// <summary>
        /// A reader for an occasional read (self-test, temperature list): the one already opened by the app, if any, otherwise a
        /// new one. <paramref name="owned"/> = true only for the new one — only that one is closed after use.
        /// </summary>
        public static TempReader Borrow(out bool owned)
        {
            lock (_liveLock)
            {
                if (_live.Count > 0) { owned = false; return _live[0]; }
            }
            owned = true;
            return new TempReader();
        }

        public TempReader()
        {
            lock (_liveLock) _live.Add(this);
            // LibreHardwareMonitor is the default source — fallback to WMI + PerformanceCounter if LHM is not available
            TryInitLhm();
        }

        /// <summary>
        /// Switch which LHM hardware nodes are polled on the next Read() call.
        /// Safe to call from any thread — takes effect on the next background tick.
        /// </summary>
        public void SetSensorMode(LhmSensorMode mode)
        {
            lock (_modeLock)
            {
                if (_currentMode == mode) return;
                var now = System.DateTime.UtcNow;
                if ((now - _lastModeChange).TotalSeconds < 1.5 && mode != LhmSensorMode.Full)
                    return; // Full priority, the rest wait
                _lastModeChange = now;
                _currentMode = mode;
                ApplySensorMode(mode);
            }
        }

        private void ApplySensorMode(LhmSensorMode mode)
        {
            if (_computer == null) return;
            bool gotLock = false;
            try { System.Threading.Monitor.TryEnter(_readLock, 1000, ref gotLock); }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            if (!gotLock)
                AppLogger.Debug($"ApplySensorMode({mode}): could not get _readLock within 1s — applying unserialized (small risk of a race with a read in progress).");
            try
            {
                bool full    = mode == LhmSensorMode.Full;
                bool cpuOnly = mode == LhmSensorMode.CpuOnly;
                _computer.IsMotherboardEnabled = true;
                _computer.IsControllerEnabled  = true;
                _computer.IsMemoryEnabled      = full;         // RAM temperature — only when needed
                // CpuOnly: disables the GPU — avoids concurrent access with GPU Stress (D3D11), which on Boot Camp / GT 755M blocked
                // LHM Read for 8-19 seconds
                if (cpuOnly) _computer.IsGpuEnabled = false;
                else         _computer.IsGpuEnabled = true;
                AppLogger.Info("LHM sensor mode → {Mode}", mode);
                _lhmValueHistory.Enqueue($"{System.DateTime.Now:HH:mm:ss.fff} ── MODE CHANGE → {mode} (gotLock={gotLock}) ──");
                while (_lhmValueHistory.Count > StaleValueHistoryMax) _lhmValueHistory.Dequeue();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader.SetSensorMode"); }
            finally { if (gotLock) System.Threading.Monitor.Exit(_readLock); }
        }

        private void TryEnableGpuLazy()
        {
            try
            {
                if (_computer == null) { _gpuDetected = true; return; }
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuNvidia
                     || hw.HardwareType == HardwareType.GpuAmd
                     || hw.HardwareType == HardwareType.GpuIntel)
                    {
                        _gpuDetected = true;
                        return;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            // Mark as checked regardless so we don't retry on every Read()
            _gpuDetected = true;
        }

        private bool _fansDiagDumped = false;
        private bool _fansLiveLogged = false;
        private DateTime _lhmFirstInitUtc = DateTime.MinValue;

        private void TryInitLhm()
        {
            bool gotGlobalLock = false;
            try
            {
                System.Threading.Monitor.TryEnter(_globalNativeAccessLock, 4000, ref gotGlobalLock);
                if (!gotGlobalLock)
                {
                    AppLogger.Warning("TryInitLhm: could not take the global LHM lock within 4s (another TempReader instance is initializing at the same time) — giving up the init, Backend stays unavailable until the next attempt.");
                    Backend = "none (LHM: another initialization in progress)";
                    return;
                }
                TryInitLhmCore();
            }
            finally { if (gotGlobalLock) System.Threading.Monitor.Exit(_globalNativeAccessLock); }
        }

        /// <summary>Writes LibreHardwareMonitor's own report (hardware, sensors, Super I/O detection) to Data\lhm-report.txt.</summary>
        private void WriteLhmReport()
        {
            bool got = false;
            try
            {
                System.Threading.Thread.Sleep(5000);   // after start-up, so it does not slow the first reads
                System.Threading.Monitor.TryEnter(_readLock, 3000, ref got);
                if (!got) { AppLogger.Debug("WriteLhmReport: _readLock busy — skipped"); return; }
                var comp = _computer;
                if (comp == null) return;
                string report = comp.GetReport();
                System.IO.File.WriteAllText(AppPaths.Data("lhm-report.txt"), report);
                AppLogger.Info($"LHM report written: Data\\lhm-report.txt ({report.Length / 1024} KB)");
            }
            catch (Exception ex) { AppLogger.Debug($"WriteLhmReport: {ex.Message}"); }
            finally { if (got) System.Threading.Monitor.Exit(_readLock); }
        }

        private void TryInitLhmCore()
        {
            if (SensorStartGuard.SafeMode)
            {
                // a previous start closed while LibreHardwareMonitor was opening (SensorStartGuard): Windows readings only
                Backend = "none (safe sensor mode)";
                return;
            }
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled         = true,
                    IsGpuEnabled         = true,
                    IsMotherboardEnabled = true,   // needed for the SuperIO CPU temp (Nuvoton/ITE)
                    IsControllerEnabled  = true,   // likewise — some boards expose the CPU temperature only here
                    IsMemoryEnabled      = false,  // enabled lazily only in Full mode
                    IsStorageEnabled     = false,
                };
                SensorStartGuard.Begin("LibreHardwareMonitor (processor, graphics, board)");
                try { c.Open(); }
                finally { SensorStartGuard.End("LibreHardwareMonitor"); }
                _computer    = c;
                _currentMode = LhmSensorMode.CpuGpuOnly; // real sync with the state above
                Backend      = "LibreHardwareMonitor";

                try
                {
                    if (!_fansDiagDumped)
                    {
                        _fansDiagDumped = true;
                        _lhmFirstInitUtc = DateTime.UtcNow;
                        // the full LHM report (which Super I/O / embedded controller chips were probed and found, with their IDs)
                        // goes to Data\lhm-report.txt once per session, in the background; the self-test zip includes it
                        _ = System.Threading.Tasks.Task.Run(WriteLhmReport);
                        foreach (var hw in c.Hardware)
                        {
                            if (hw.HardwareType != HardwareType.Motherboard
                                && hw.HardwareType != HardwareType.SuperIO
                                && hw.HardwareType != HardwareType.EmbeddedController
                                && hw.HardwareType != HardwareType.Cooler) continue;
                            hw.Update();
                            AppLogger.Info($"FANS-DIAG: hardware root '{hw.Name}' ({hw.HardwareType}) — {hw.Sensors.Length} direct sensors, {hw.SubHardware.Length} sub-hardware");
                            foreach (var s in hw.Sensors)
                                AppLogger.Info($"FANS-DIAG:   [{hw.Name}] {s.SensorType} '{s.Name}' = {(s.Value.HasValue ? s.Value.Value.ToString("F1") : "null")}");
                            foreach (var sub in hw.SubHardware)
                            {
                                sub.Update();
                                AppLogger.Info($"FANS-DIAG:   sub-hardware '{sub.Name}' ({sub.HardwareType}) — {sub.Sensors.Length} sensors");
                                foreach (var s in sub.Sensors)
                                    AppLogger.Info($"FANS-DIAG:     [{sub.Name}] {s.SensorType} '{s.Name}' = {(s.Value.HasValue ? s.Value.Value.ToString("F1") : "null")}");
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"FANS-DIAG dump failed: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                Backend = "none (LHM: " + ex.Message + ")";
                AppLogger.Warning(ex, $"TryInitLhmCore: Computer initialization failed — _computer stays null until the next attempt. Backend='{Backend}'");
            }
        }

        public void ReinitLhm(bool resetFreqTracking = true)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _reinitInProgress, 1, 0) != 0)
            {
                AppLogger.Info("ReinitLhm: a reinit is already running on this instance — skipping this call (avoids concurrent native access).");
                return;
            }
            try
            {
                ReinitLhmCore(resetFreqTracking);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reinitInProgress, 0);
            }
        }

        private void ReinitLhmCore(bool resetFreqTracking)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int attemptId = System.Threading.Interlocked.Increment(ref _reinitAttemptCounter);
            AppLogger.Info($"ReinitLhm #{attemptId}: START (thread={System.Threading.Thread.CurrentThread.ManagedThreadId}, resetFreqTracking={resetFreqTracking})");
            try
            {
                var oldComputer = _computer;
                _computer = null;
                _lhmWarmedUp = false;
                _gpuDetected = false; // new Computer object — rescan the hardware from scratch
                _lhmStaleCount = 0;
                _lhmForceUpdateCount = 0;
                _lhmNullCount = 0;
                _staleAccepted = false;
                _staleLoggedOnce = false;
                _lhmLastCpuTemp = null;
                if (resetFreqTracking)
                {
                    _lhmLoadHistory.Clear();
                    _lhmLastCpuFreq = null;
                    _lhmFreqStaleCount = 0;
                }

                bool closeDone = RunWithHardTimeout($"ReinitLhm #{attemptId} Close()", 3000, () =>
                {
                    bool gotLock = false;
                    try
                    {
                        System.Threading.Monitor.TryEnter(_globalNativeAccessLock, 2500, ref gotLock);
                        if (!gotLock)
                        {
                            AppLogger.Warning($"ReinitLhm #{attemptId} Close(): could not take the global LHM lock within 2.5s — skipping the explicit Close() (oldComputer will eventually be collected by the GC).");
                            return;
                        }
                        try { oldComputer?.Close(); } catch (Exception ex) { AppLogger.Debug($"ReinitLhm #{attemptId} Close() exception: {ex.Message}"); }
                    }
                    finally { if (gotLock) System.Threading.Monitor.Exit(_globalNativeAccessLock); }
                });
                if (!closeDone)
                    AppLogger.Warning($"ReinitLhm #{attemptId}: Close() did not return within 3s — ABANDONED (orphan thread), continuing with the re-init anyway.");

                var mode = _currentMode;
                bool initDone = RunWithHardTimeout($"ReinitLhm #{attemptId} TryInitLhm()", 5000, () =>
                {
                    try { TryInitLhm(); }
                    catch (Exception ex) { AppLogger.Warning(ex, $"ReinitLhm #{attemptId} TryInitLhm() exception"); }
                });
                if (!initDone)
                {
                    AppLogger.Warning($"ReinitLhm #{attemptId}: TryInitLhm() did not return within 5s — ABANDONED (orphan thread). _computer stays null until the next attempt.");
                    sw.Stop();
                    return;
                }

                if (_computer != null)
                {
                    ApplySensorMode(mode);
                    _currentMode = mode; // TryInitLhm() resets to CpuGpuOnly — restore the real mode
                }
                else
                {
                    for (int retry = 1; retry <= 2 && _computer == null; retry++)
                    {
                        AppLogger.Warning($"ReinitLhm #{attemptId}: _computer still null after TryInitLhm() — retrying immediately ({retry}/2), not waiting for the natural cycle.");
                        System.Threading.Thread.Sleep(500);
                        bool retryDone = RunWithHardTimeout($"ReinitLhm #{attemptId} retry{retry}", 5000, () =>
                        {
                            try { TryInitLhm(); }
                            catch (Exception ex) { AppLogger.Warning(ex, $"ReinitLhm #{attemptId} retry{retry} exception"); }
                        });
                        if (!retryDone)
                            AppLogger.Warning($"ReinitLhm #{attemptId} retry{retry}: did not return within 5s — ABANDONED.");
                        else if (_computer != null)
                        {
                            ApplySensorMode(mode);
                            _currentMode = mode;
                        }
                    }
                    if (_computer == null)
                        AppLogger.Warning($"ReinitLhm #{attemptId}: _computer STILL null after all attempts — Backend='{Backend}' (see the TryInitLhmCore message above for the exact cause).");
                }
                sw.Stop();
                AppLogger.Info($"ReinitLhm #{attemptId}: DONE after a frozen CPU temp sensor ({sw.ElapsedMilliseconds}ms, _computer={( _computer != null ?"OK" : "null")})");
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.Warning(ex, $"ReinitLhm #{attemptId}: unexpected exception after {sw.ElapsedMilliseconds}ms");
            }
        }

        private static int _reinitAttemptCounter = 0;
        private int _reinitInProgress = 0; // 0 = free, 1 = a ReinitLhm() is running

        /// <summary>Runs <paramref name="action"/> on a dedicated thread and waits at most <paramref name="timeoutMs"/>. If the
        /// thread does not finish in time it is NOT interrupted (there is no safe API to stop a blocked native call) — we simply
        /// stop waiting; it finishes on its own whenever that is, without blocking the calling thread. Returns true if it
        /// finished in time.</summary>
        private bool RunWithHardTimeout(string label, int timeoutMs, Action action)
        {
            var swStep = System.Diagnostics.Stopwatch.StartNew();
            var thread = new System.Threading.Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { AppLogger.Debug($"{label}: exception on the dedicated thread: {ex.Message}"); }
            })
            { IsBackground = true, Name = label.Length > 60 ? label.Substring(0, 60) : label };
            thread.Start();
            bool finished = thread.Join(timeoutMs);
            swStep.Stop();
            if (finished)
                AppLogger.Debug($"{label}: OK ({swStep.ElapsedMilliseconds}ms)");
            return finished;
        }

        private TempSnapshot? _lastGoodSnap;
        private int _lockTimeoutStreak;
        /// <summary>True when several consecutive reads could not take _readLock — almost certainly a native LHM call stuck for
        /// good inside the lock.</summary>
        public bool IsProbablyHung => System.Threading.Volatile.Read(ref _lockTimeoutStreak) >= 4;

        public List<string> DumpAllSensorsRaw()
        {
            var lines = new List<string>();
            if (_computer == null) { lines.Add("(_computer e null — LHM nu e initializat)"); return lines; }
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    try
                    {
                        lines.Add($"[{hw.HardwareType}] {hw.Name}");
                        foreach (var s in hw.Sensors)
                        {
                            try
                            {
                                string val = s.Value.HasValue ? s.Value.Value.ToString("F2") : "null";
                                lines.Add($"    {s.SensorType,-12} {s.Name,-30} = {val}");
                            }
                            catch (Exception ex) { lines.Add($"    (sensor error: {ex.Message})"); }
                        }
                        foreach (var sub in hw.SubHardware)
                        {
                            lines.Add($"  [{sub.HardwareType}] {sub.Name} (sub)");
                            foreach (var s in sub.Sensors)
                            {
                                try
                                {
                                    string val = s.Value.HasValue ? s.Value.Value.ToString("F2") : "null";
                                    lines.Add($"      {s.SensorType,-12} {s.Name,-30} = {val}");
                                }
                                catch (Exception ex) { lines.Add($"      (sensor error: {ex.Message})"); }
                            }
                        }
                    }
                    catch (Exception ex) { lines.Add($"  (hardware error {hw?.Name}: {ex.Message})"); }
                }
            }
            catch (Exception ex) { lines.Add($"(enumeration error: {ex.Message})"); }
            return lines;
        }

        public TempSnapshot Read()
        {
            ThreadTags.Set("sensor read (LHM)");
            var th = System.Threading.Thread.CurrentThread;
            var oldPrio = th.Priority;
            try { th.Priority = System.Threading.ThreadPriority.AboveNormal; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            try
            {
                bool got = false;
                try
                {
                    System.Threading.Monitor.TryEnter(_readLock, 1200, ref got);
                    if (!got)
                    {
                        int streak = System.Threading.Interlocked.Increment(ref _lockTimeoutStreak);
                        if (streak == 1 || streak == 4)
                            AppLogger.Warning($"TempReader.Read: _readLock busy >1.2s (streak={streak}) — returning the last good snapshot" + (streak >= 4 ? "; the native read seems BLOCKED for good (hard recovery needed)" : ""));
                        return _lastGoodSnap ?? new TempSnapshot { Backend = Backend };
                    }
                    System.Threading.Volatile.Write(ref _lockTimeoutStreak, 0);

                    bool gotNative = false;
                    try
                    {
                        System.Threading.Monitor.TryEnter(_globalNativeAccessLock, 2000, ref gotNative);
                        if (!gotNative)
                        {
                            AppLogger.Warning("TempReader.Read: _globalNativeAccessLock busy >2s (LHM reinit running on another thread) — returning the last good snapshot, the native driver is not touched while the reinit works.");
                            return _lastGoodSnap ?? new TempSnapshot { Backend = Backend };
                        }
                        var snap = ReadCore();
                        _lastGoodSnap = snap;
                        return snap;
                    }
                    finally
                    {
                        if (gotNative) System.Threading.Monitor.Exit(_globalNativeAccessLock);
                    }
                }
                finally
                {
                    if (got) System.Threading.Monitor.Exit(_readLock);
                }
            }
            finally
            {
                try { th.Priority = oldPrio; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
        }

        private TempSnapshot ReadCore()
        {
            var snap = new TempSnapshot { Backend = Backend };

            if (_computer != null && !_gpuDetected && true)
                TryEnableGpuLazy();

            // Method -1: MSR CPU temperature — straight from the hardware, zero overhead, no LHM
            if (!snap.CpuTemp.HasValue && !_msrPermFailed)
                TryMsrCpuTemp(snap);

            // Method 0: D3DKMT — straight from the Windows kernel, no external driver.
            // The same API Task Manager uses, returns temperature + power + fan. LHM = fallback.
            if (!snap.GpuTemp.HasValue && !_d3dGpuPermFailed)
                TryGpuD3DKMT(snap);

            // Method 1: LHM (the most precise)
            if (_computer != null && true)
            {
                if (!_lhmWarmedUp)
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        try { hw.Update(); foreach (var s in hw.SubHardware) try { s.Update(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                        catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    }
                    _lhmWarmedUp = true;
                }
                ReadLhm(snap);
            }

            // Method 2: if the CPU is still null, try OpenHardwareMonitor through WMI
            if (!snap.CpuTemp.HasValue)
                TryOhmWmi(snap);

            // Method 3: WMI ThermalZone fallback (approximate, but works everywhere)
            if (!snap.CpuTemp.HasValue)
                TryWmiThermal(snap);

            // Metoda 4: GPU fallback — D3DKMTQueryStatistics / NVAPI hint via WMI
            if (!snap.GpuTemp.HasValue)
                TryGpuWmiFallback(snap);

            bool needNvidiaSmi = (!snap.GpuTemp.HasValue    && _smiProvidesTemp)
                               || (!snap.GpuLoadPct.HasValue && _smiProvidesLoad)
                               || (!snap.GpuFreqMHz.HasValue && _smiProvidesFreq);
            if (needNvidiaSmi)
            {
                TryNvidiaSmiExtended(snap);
                if (snap.GpuTemp.HasValue && snap.GpuSensorName == "--")
                    snap.GpuSensorName = "nvidia-smi";
            }

            // Metoda 6: AMD — radeonsmi (AMD Radeon Software CLI, no admin)
            if (!snap.GpuTemp.HasValue || snap.FanSpeeds.Count == 0)
                TryAmdSmi(snap);

            TryIntelIgcl(snap);

            // Metoda 7: WMI Win32_VideoController — GPU load % (universal, no admin)
            if (!snap.GpuLoadPct.HasValue)
                TryWmiGpuLoad(snap);

            // Metoda 8: Fan RPM via WMI Win32_Fan (some OEM boards expose this)
            if (snap.FanSpeeds.Count == 0)
                TryWmiFan(snap);

            // Metoda 9: CPU load fallback via PerformanceCounter
            if (!snap.CpuLoadPct.HasValue)
                TryPerfCounterCpuLoad(snap);

            CheckThermalZoneFrozen(snap);

            if (!snap.GpuTemp.HasValue && snap.CpuTemp.HasValue && IsIntelIgpuOnlySystem())
            {
                snap.GpuTemp = snap.CpuTemp;
                if (snap.GpuSensorName == "--" || snap.GpuSensorName.Contains("WMI", StringComparison.OrdinalIgnoreCase))
                    snap.GpuSensorName = "Intel iGPU (≈ CPU pkg)";
                else if (!snap.GpuSensorName.Contains("CPU pkg", StringComparison.OrdinalIgnoreCase))
                    snap.GpuSensorName += " (≈ CPU pkg)";
            }

            SensorValidation.Sanitize(snap);
            snap.CapturedUtc = DateTime.UtcNow;
            return snap;
        }

        /// <summary>
        /// Used for FREEZE RECOVERY on an instance whose native read stayed blocked.
        /// Closes the driver ONLY if it gets both locks. If it does not, a thread is still inside a native call on this
        /// _computer: a parallel Close() could cause a memory access error in native code (a process crash that try/catch
        /// cannot catch). In that case the object is deliberately abandoned — the system releases its resources when the
        /// process exits.
        /// </summary>
        public void AbandonClose()
        {
            bool got = false, gotGlobal = false;
            try
            {
                try { System.Threading.Monitor.TryEnter(_readLock, 0, ref got); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                if (got)
                {
                    try { System.Threading.Monitor.TryEnter(_globalNativeAccessLock, 2500, ref gotGlobal); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                if (!got || !gotGlobal)
                {
                    AppLogger.Warning("TempReader.AbandonClose: a native read is still running — the driver is NOT closed (avoids a native crash); the instance stays abandoned.");
                    return;
                }
                try { _computer?.Close(); } catch (Exception ex) { AppLogger.Warning(ex, "TempReader.AbandonClose: Close"); }
                _computer = null;
            }
            finally
            {
                if (gotGlobal) { try { System.Threading.Monitor.Exit(_globalNativeAccessLock); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
                if (got) { try { System.Threading.Monitor.Exit(_readLock); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
            }
        }

        private bool _disposed;

        /// <summary>
        /// Full close. All native resources (LHM, MSR, D3DKMT, IGCL) are released ONLY if no native read is in progress (both
        /// locks obtained). Otherwise they are abandoned — see AbandonClose. Returns true if the close was complete.
        /// </summary>
        public bool CloseSafely(int timeoutMs = 3000)
        {
            if (_disposed) return true;
            _disposed = true;
            bool got = false, gotGlobal = false;
            try
            {
                try { System.Threading.Monitor.TryEnter(_readLock, timeoutMs, ref got); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                if (got)
                {
                    try { System.Threading.Monitor.TryEnter(_globalNativeAccessLock, timeoutMs, ref gotGlobal); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                if (!got || !gotGlobal)
                {
                    AppLogger.Warning("TempReader.CloseSafely: native read still running after {0}ms — native resources are NOT closed (avoids a native crash).", timeoutMs);
                    return false;
                }
                try { _computer?.Close(); } catch (Exception ex) { AppLogger.Warning(ex, "TempReader.CloseSafely: LHM Close"); }
                _computer = null;
                lock (_perfLock) { _cpuPerfCounter?.Dispose(); _cpuPerfCounter = null; }
                try { CloseD3DAdapter(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { CloseMsrHandle(); }  catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try { IgclService.Shutdown(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                return true;
            }
            finally
            {
                if (gotGlobal) { try { System.Threading.Monitor.Exit(_globalNativeAccessLock); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
                if (got) { try { System.Threading.Monitor.Exit(_readLock); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
            }
        }

        public void Dispose()
        {
            lock (_liveLock) _live.Remove(this);
            CloseSafely();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private IntPtr _msrHandle = new IntPtr(-1); // INVALID_HANDLE_VALUE
        private bool   _msrHandleOpened = false;
    }
}
