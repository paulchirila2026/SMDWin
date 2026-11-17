using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{
    public partial class TempReader
    {
        // ── Metoda 1: LibreHardwareMonitor ────────────────────────────────────
        private double _cpuUpdateCostMs;
        private bool _cpuSkippedThisRead;   // true when the CPU read was skipped on purpose in this tick → no "frozen sensor" checks
        private readonly System.Diagnostics.Stopwatch _cpuLastUpdateSw = new();

        private void ReadLhm(TempSnapshot snap)
        {
            float? bestCpu = null; string cpuName = "--"; int cpuScore = -1;
            int cpuPowerScore = -1, cpuVcoreScore = -1;
            // AMD APUs (Ryzen with Radeon Vega / 600M / 700M / 800M): LHM puts the graphics temperature in the processor node
            // ("GFX"), not in the GPU node — kept here for a GPU that has no temperature of its own
            float? apuGfxTemp = null;
            _hwIntegratedGpuTemp = null;
            _hwIntegratedGpuLoad = null;
            _hwIntegratedGpuName = "--";
            // one GpuNodeSnapshot per physical graphics card found in this tick — see the comment at TempSnapshot.Gpus.
            var gpuNodesThisTick = new List<GpuNodeSnapshot>();
            var coreList = new List<float>();
            var coreFreqs = new List<float>();
            snap.FanSpeeds.Clear();
            snap.CoreFreqs.Clear();

            if (_computer == null) return;

            bool needsReinitAfterLoop = false;
            _cpuSkippedThisRead = false;
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    if (hw == null) continue;
                    var swHw = System.Diagnostics.Stopwatch.StartNew();
                    bool isCpuHw = hw.HardwareType == HardwareType.Cpu;

                    bool isThrottledGpu = hw.HardwareType == HardwareType.GpuNvidia
                                       || hw.HardwareType == HardwareType.GpuAmd;
                    string gpuThrottleKey = isThrottledGpu ? (hw.Identifier?.ToString() ?? hw.Name ?? "gpu") : "";
                    bool skipUpdateThisTick = false;
                    if (isThrottledGpu)
                    {
                        if (_gpuLastUpdateSw.TryGetValue(gpuThrottleKey, out var swPrev))
                        {
                            if (swPrev.ElapsedMilliseconds < GpuThrottleIntervalMs)
                                skipUpdateThisTick = true;
                        }
                    }

                    // CPU: the per-core MSR read can take 200–650 ms on laptops (measured on an i5-10210U). If a read is slow, the next one
                    // waits twice as long as it took (at most 1.5 s) — the values stay those from LHM, so nothing disappears, but the
                    // processor is no longer kept busy by sensor reads under stress.
                    if (isCpuHw && !skipUpdateThisTick && _cpuUpdateCostMs > 100
                        && _cpuLastUpdateSw.IsRunning
                        && _cpuLastUpdateSw.ElapsedMilliseconds < Math.Min(1500, (long)(_cpuUpdateCostMs * 2)))
                    {
                        skipUpdateThisTick = true;
                        _cpuSkippedThisRead = true;
                    }

                    try
                    {
                        if (!skipUpdateThisTick)
                        {
                            var swCpuUpd = isCpuHw ? System.Diagnostics.Stopwatch.StartNew() : null;
                            hw.Update();
                            if (swCpuUpd != null)
                            {
                                _cpuUpdateCostMs = _cpuUpdateCostMs <= 0 ? swCpuUpd.ElapsedMilliseconds
                                                 : _cpuUpdateCostMs * 0.6 + swCpuUpd.ElapsedMilliseconds * 0.4;
                                _cpuLastUpdateSw.Restart();
                            }
                            if (isThrottledGpu)
                            {
                                if (_gpuLastUpdateSw.TryGetValue(gpuThrottleKey, out var swExisting))
                                    swExisting.Restart();
                                else
                                    _gpuLastUpdateSw[gpuThrottleKey] = System.Diagnostics.Stopwatch.StartNew();
                            }
                        }
                        if (isCpuHw && _cpuHwUpdateFailStreak > 0)
                        {
                            AppLogger.Info("TempReader: hw.Update() for the CPU succeeded again after {0} consecutive failure(s).", _cpuHwUpdateFailStreak);
                            _cpuHwUpdateFailStreak = 0;
                        }
                    }
                    catch (Exception hwEx)
                    {
                        if (isCpuHw)
                        {
                            _cpuHwUpdateFailStreak++;
                            AppLogger.Warning(hwEx, $"TempReader: hw.Update() failed for the CPU ({hw.Name}) — consecutive failure #{_cpuHwUpdateFailStreak}");
                            if (_cpuHwUpdateFailStreak >= CpuHwUpdateFailThreshold)
                            {
                                // Do NOT call ReinitLhm() here — we are still inside the foreach over _computer.Hardware; Close()+reinit would replace
                                // _computer while the rest of the hardware (GPU, RAM, etc.) still has to be processed in the SAME read cycle.
                                // Only signal — the real reinit happens after the foreach.
                                needsReinitAfterLoop = true;
                            }
                        }
                        // /* LHM bug on some CPUs (e.g. i5-4570) — continue */
                    }
                    if (swHw.ElapsedMilliseconds > 150)
                        AppLogger.Warning($"LHM Read slow: {hw.Name} ({hw.HardwareType}) — {swHw.ElapsedMilliseconds}ms");
                    foreach (var sub in hw.SubHardware)
                    {
                        var swSub = System.Diagnostics.Stopwatch.StartNew();
                        try { sub.Update(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                        if (swSub.ElapsedMilliseconds > 150)
                            AppLogger.Warning($"LHM Read slow (sub): {hw.Name}/{sub.Name} — {swSub.ElapsedMilliseconds}ms");
                    }

                    bool isCpu  = hw.HardwareType == HardwareType.Cpu;
                    if (isCpu && _cpuClockCeilingMHz == 0) _cpuClockCeilingMHz = CpuClockCeilingMHz(hw.Name ?? "");
                    bool isGpu  = hw.HardwareType == HardwareType.GpuNvidia
                              || hw.HardwareType == HardwareType.GpuAmd
                              || hw.HardwareType == HardwareType.GpuIntel;
                    bool isMb   = hw.HardwareType == HardwareType.Motherboard;
                    bool isCtrl = hw.HardwareType == HardwareType.SuperIO
                               || hw.HardwareType == HardwareType.EmbeddedController;
                    bool isBattery = hw.HardwareType == HardwareType.Battery;
                    if (isGpu) _gpuDetected = true;

                    // LOCAL accumulators for THIS GPU hardware node: ALL fields (temp/load/power/freq/vram/hotspot) are isolated per node,
                    // for any card type (Nvidia/AMD/Intel), so sensors from different cards never mix.
                    float? hwGpuTemp = null; int hwGpuTempScore = -1;
                    float? hwGpuLoad = null;
                    float? hwGpuPowerW = null; int hwGpuPowerScore = -1;
                    float? hwGpuFreqMHz = null, hwGpuMemFreqMHz = null, hwGpuMemUsedMB = null;
                    float? hwGpuHotSpot = null, hwGpuVramTemp = null;
                    string hwGpuName = hw.Name ?? "--";

                    var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));
                    foreach (var s in sensors)
                    {
                        try
                        {
                            if (s == null || !s.Value.HasValue) continue;
                            float v = s.Value.Value;
                            if (!float.IsFinite(v)) continue;
                            string n = (s.Name ?? "").ToLower();

                            if (s.SensorType == SensorType.Fan && v >= 0)
                            {
                                snap.FanSpeeds.Add(($"{hw.Name} — {s.Name}", v));
                                continue;
                            }
                            if ((isCtrl || isMb) && s.SensorType == SensorType.Control && v >= 0)
                            {
                                // Not added to FanSpeeds (no RPM), but don't skip — let temp scanning continue
                            }

                            // ── CPU Clock (MHz) ──────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Clock && v > 0)
                            {
                                if (n.Contains("core") && !n.Contains("bus") && !n.Contains("ring"))
                                    coreFreqs.Add(v);
                                // "CPU Core #1" or "Bus Speed" — take highest core
                                if (!snap.CpuFreqMHz.HasValue || v > snap.CpuFreqMHz.Value)
                                    if (n.Contains("core") && !n.Contains("bus"))
                                        snap.CpuFreqMHz = v;
                            }

                            if (isCpu && s.SensorType == SensorType.Power && v > 0)
                            {
                                int pw = n.Contains("package")                                   ? 100
                                       : (n.Contains("cpu") && !n.Contains("core")
                                          && !n.Contains("memory") && !n.Contains("dram")
                                          && !n.Contains("soc"))                                 ?  70
                                       :                                                            10;
                                if (pw > cpuPowerScore)
                                { cpuPowerScore = pw; snap.CpuPowerW = v; }
                                if (n.Contains("core") && !n.Contains("uncore") && !n.Contains("package"))
                                    snap.CpuCoresPowerW = v;
                                if (n.Contains("uncore") || n.Contains("dram"))
                                    snap.CpuUncorePowerW = v;
                            }

                            // ── CPU Load % ───────────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Load && v >= 0)
                                if (n.Contains("total") || n.Contains("cpu total") || !snap.CpuLoadPct.HasValue)
                                    snap.CpuLoadPct = v;

                            if (isGpu && s.SensorType == SensorType.Clock && v > 0)
                            {
                                if (n.Contains("core") || n.Contains("gpu core"))
                                    hwGpuFreqMHz = v;
                                else if (n.Contains("memory") || n.Contains("mem"))
                                    hwGpuMemFreqMHz = v;
                            }

                            if (isGpu && s.SensorType == SensorType.Power && v > 0)
                            {
                                int gp = (n == "gpu power" || n.Contains("package") || n.Contains("total")) ? 100
                                       : n.Contains("board")                                                 ?  90
                                       : n.Contains("gpu")                                                   ?  50
                                       :                                                                        10;
                                if (gp > hwGpuPowerScore)
                                { hwGpuPowerScore = gp; hwGpuPowerW = v; }
                            }

                            if (isGpu && s.SensorType == SensorType.Load && v >= 0)
                            {
                                if (n.Contains("core") || n.Contains("gpu core") || !hwGpuLoad.HasValue)
                                    hwGpuLoad = v;
                            }

                            if (isGpu && s.SensorType == SensorType.SmallData && v > 0)
                                if (n.Contains("used") && n.Contains("mem"))
                                    hwGpuMemUsedMB = v;

                            if (isCpu && s.SensorType == SensorType.Voltage && v > 0)
                            {
                                int vc = n.Contains("vcore")                    ? 100
                                       : (n.Contains("core") && !n.Contains("#")) ?  80
                                       : n.Contains("#")                          ?  20
                                       :                                             10;
                                if (vc > cpuVcoreScore)
                                { cpuVcoreScore = vc; snap.CpuVCoreV = v; }
                            }

                            // ── Motherboard voltages (+12 / +5 / +3.3 V, VBAT) ─────────
                            if ((isMb || isCtrl) && s.SensorType == SensorType.Voltage && v > 0)
                            {
                                string vn = n.Replace(" ", "");
                                if (vn.Contains("vbat") || vn.Contains("cmos") || vn == "battery" || vn.Contains("vbatt"))
                                { if (v >= 1.8f && v <= 3.6f) snap.CmosBatteryV = v; }
                                else if (vn.Contains("sb") || vn.Contains("standby") || vn.Contains("dual")) { /* standby lines — not shown */ }
                                else if (vn.Contains("12v"))
                                { if (v >= 10.5f && v <= 13.5f) snap.Rail12V = v; }
                                else if (vn.Contains("+5v") || vn == "5v" || vn.Contains("5vcc") || vn == "vcc5")
                                { if (v >= 4.4f && v <= 5.6f) snap.Rail5V = v; }
                                else if (vn.Contains("3.3v") || vn.Contains("3vcc") || vn.Contains("+3.3") || vn == "3.3v" || vn == "avcc3")
                                { if (v >= 2.9f && v <= 3.7f) snap.Rail3V3 = v; }
                                else if (!snap.CpuVCoreV.HasValue && vn.Contains("vcore") && v >= 0.5f && v <= 1.8f)
                                    snap.CpuVCoreV = v;
                            }

                            // ── Temperatures ─────────────────────────────────
                            if (s.SensorType != SensorType.Temperature) continue;
                            if (!SensorValidation.IsPlausibleTemp(v)) continue;
                            if (isCpu && n == "gfx") { apuGfxTemp = v; continue; }

                            if (isCpu || n.Contains("cpu") || n.Contains("package")
                                || n.Contains("tdie") || n.Contains("core"))
                            {
                                int sc = CpuScore(s.Name ?? "");
                                if (sc >= 0 && sc > cpuScore)
                                { cpuScore = sc; bestCpu = v; cpuName = s.Name ?? "--"; }
                                if (isCpu && n.Contains("core")
                                    && !n.Contains("max") && !n.Contains("avg") && !n.Contains("average")
                                    && !n.Contains("min") && !n.Contains("package"))
                                {
                                    coreList.Add(v);
                                    snap.CoreTemps.Add(v);

                                    CpuTopologyService.CoreKind kind;
                                    if (n.Contains("p-core"))
                                        kind = CpuTopologyService.CoreKind.Performance;
                                    else if (n.Contains("e-core"))
                                        kind = CpuTopologyService.CoreKind.Efficiency;
                                    else
                                    {
                                        var kinds = CpuTopologyService.GetPerCoreKinds();
                                        int posIdx = coreList.Count - 1;
                                        kind = (posIdx >= 0 && posIdx < kinds.Count)
                                            ? kinds[posIdx] : CpuTopologyService.CoreKind.Unknown;
                                    }
                                    int coreIdx = coreList.Count - 1; // display: only the order of appearance, not the "#N" in the name (may be ambiguous between P/E)
                                    snap.CoreTempsDetailed.Add(new CoreTempEntry { Index = coreIdx, Temp = v, Kind = kind });
                                }
                            }

                            if ((isMb || isCtrl) && (n.Contains("cpu") || n.Contains("system")))
                            {
                                int sc = CpuScore(s.Name ?? "");
                                if (sc >= 0 && sc > cpuScore)
                                { cpuScore = sc; bestCpu = v; cpuName = (s.Name ?? "--") + " (MB)"; }
                            }

                            if ((isMb || isCtrl) && (n.Contains("vrm") || n.Contains("mosfet") || n.Contains("mos")))
                                if (!snap.VrmTemp.HasValue || v > snap.VrmTemp.Value)
                                    snap.VrmTemp = v;

                            if ((isMb || isCtrl) && (n.Contains("chipset") || n.Contains("pch") || n.Contains("sb")))
                                if (!snap.ChipsetTemp.HasValue || v > snap.ChipsetTemp.Value)
                                    snap.ChipsetTemp = v;

                            if (isGpu)
                            {
                                int sg = GpuScore(s.Name ?? "");
                                if (sg >= 0 && sg > hwGpuTempScore)
                                { hwGpuTempScore = sg; hwGpuTemp = v; }
                                if (n.Contains("hotspot") || n.Contains("junction") || n.Contains("hot spot"))
                                    hwGpuHotSpot = v;
                                if (n.Contains("memory") && n.Contains("temp"))
                                    hwGpuVramTemp = v;
                            }

                            if (isBattery && s.SensorType == SensorType.Power
                                && (n.Contains("charge") || n.Contains("discharge")))
                            {
                                snap.BatteryLiveW = Math.Abs(v);
                            }
                            if (isBattery && s.SensorType == SensorType.Voltage)
                            {
                                snap.BatteryLiveVoltage = v;
                            }

                            if (!isCpu && !isGpu && !isMb && !isCtrl
                                && (n.Contains("temperature") || n.Contains("temp"))
                                && (hw.Name?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true
                                 || hw.Name?.Contains("SSD",  StringComparison.OrdinalIgnoreCase) == true
                                 || hw.Name?.Contains("M.2",  StringComparison.OrdinalIgnoreCase) == true))
                                if (!snap.NvmeTemp.HasValue || v > snap.NvmeTemp.Value)
                                    snap.NvmeTemp = v;
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                    }

                    // is this hardware node a graphics card? Its GpuNodeSnapshot is built STRICTLY from the local accumulators above
                    // (no field taken from another node) and added to this tick's list — the decision "which is the main card / which is
                    // integrated" is made after the foreach, once, based on the whole list.
                    if (isGpu)
                    {
                        gpuNodesThisTick.Add(new GpuNodeSnapshot
                        {
                            Name       = hwGpuName,
                            Kind       = hw.HardwareType,
                            Temp       = hwGpuTemp,
                            HotSpot    = hwGpuHotSpot,
                            VramTemp   = hwGpuVramTemp,
                            LoadPct    = hwGpuLoad,
                            PowerW     = hwGpuPowerW,
                            FreqMHz    = hwGpuFreqMHz,
                            MemFreqMHz = hwGpuMemFreqMHz,
                            MemUsedMB  = hwGpuMemUsedMB,
                        });
                        // Compatibility: _hwIntegratedGpu* stay populated from the Intel node, for code that still reads them.
                        if (hw.HardwareType == HardwareType.GpuIntel)
                        {
                            _hwIntegratedGpuTemp = hwGpuTemp;
                            _hwIntegratedGpuLoad = hwGpuLoad;
                            _hwIntegratedGpuName = hwGpuName;
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }

            // FANS-LIVE: the fan values once, ~20 s after start (the FANS-DIAG dump at start-up is taken right after the first
            // read, when some chips still report 0) — together with Data\lhm-report.txt it shows why fans read 0 or are missing
            if (!_fansLiveLogged && _lhmFirstInitUtc != DateTime.MinValue && (DateTime.UtcNow - _lhmFirstInitUtc).TotalSeconds > 20)
            {
                _fansLiveLogged = true;
                AppLogger.Info(snap.FanSpeeds.Count == 0
                    ? "FANS-LIVE: no fan sensor found by LHM after 20 s"
                    : "FANS-LIVE: " + string.Join(" | ", snap.FanSpeeds.Select(f => $"{f.Name} = {f.Rpm:F0} RPM")));
            }

            if (needsReinitAfterLoop)
            {
                AppLogger.Warning("TempReader: hw.Update() failed repeatedly for the CPU ({0} failures in a row) — a much stronger signal than \"the value did not change\", forcing an LHM reinit.", _cpuHwUpdateFailStreak);
                DumpLhmValueHistory();
                _ = System.Threading.Tasks.Task.Run(() => ReinitLhm(resetFreqTracking: false));
                _cpuHwUpdateFailStreak = 0;
            }

            // final composition of the GPUs, based on the per-node list built in the foreach above.
            snap.Gpus = gpuNodesThisTick;
            var discreteNodes  = gpuNodesThisTick.Where(g => g.IsDiscrete).ToList();
            var integratedNode = gpuNodesThisTick.FirstOrDefault(g => !g.IsDiscrete); // Intel
            bool hasDiscreteGpuHw = discreteNodes.Count > 0; // card physically present — even if asleep now

            if (hasDiscreteGpuHw)
            {
                var primary = discreteNodes.FirstOrDefault(g => g.HasReading) ?? discreteNodes[0];
                if (primary.HasReading)
                {
                    snap.GpuTemp       = primary.Temp;
                    snap.GpuLoadPct    = primary.LoadPct;
                    snap.GpuPowerW     = primary.PowerW;
                    snap.GpuFreqMHz    = primary.FreqMHz;
                    snap.GpuMemFreqMHz = primary.MemFreqMHz;
                    snap.GpuMemUsedMB  = primary.MemUsedMB;
                    snap.GpuHotSpot    = primary.HotSpot;
                    snap.GpuVramTemp   = primary.VramTemp;
                    snap.GpuSensorName = primary.Name;
                }
                else
                {
                    snap.GpuTemp = null; snap.GpuLoadPct = null; snap.GpuPowerW = null;
                    snap.GpuFreqMHz = null; snap.GpuMemFreqMHz = null; snap.GpuMemUsedMB = null;
                    snap.GpuHotSpot = null; snap.GpuVramTemp = null;
                    snap.GpuSensorName = primary.Name + " (idle)";
                }
            }
            else if (integratedNode != null && integratedNode.HasReading)
            {
                // iGPU-only system (no physical dedicated card) — Intel is legitimately the "card" shown on the main row.
                snap.GpuTemp       = integratedNode.Temp;
                snap.GpuLoadPct    = integratedNode.LoadPct;
                snap.GpuPowerW     = integratedNode.PowerW;
                snap.GpuFreqMHz    = integratedNode.FreqMHz;
                snap.GpuMemFreqMHz = integratedNode.MemFreqMHz;
                snap.GpuMemUsedMB  = integratedNode.MemUsedMB;
                snap.GpuHotSpot    = integratedNode.HotSpot;
                snap.GpuVramTemp   = integratedNode.VramTemp;
                snap.GpuSensorName = integratedNode.Name;
            }
            // an AMD APU: the GPU node has load and clocks but no temperature — the one in the processor node is the same chip
            if (!snap.GpuTemp.HasValue && apuGfxTemp.HasValue
                && gpuNodesThisTick.Any(g => g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !g.Temp.HasValue))
            {
                snap.GpuTemp = apuGfxTemp;
                snap.GpuSensorName = (gpuNodesThisTick.FirstOrDefault(g => g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))?.Name ?? "Radeon") + " (GFX, processor sensor)";
            }
            // otherwise (no GPU node found by LHM at all) — snap.GpuTemp is not touched; what D3DKMT/WMI already wrote earlier in
            // the pipeline (ReadCore) is kept.

            // The separate "iGPU" row — shown ONLY when there is ALSO a physical dedicated card AND Intel has a real reading NOW;
            // the data comes STRICTLY from the Intel node itself (never from another node) — this prevents "seeing Intel twice".
            if (hasDiscreteGpuHw && integratedNode != null && integratedNode.HasReading)
            {
                snap.HasIntegratedGpu     = true;
                snap.IntegratedGpuTemp    = integratedNode.Temp;
                snap.IntegratedGpuLoadPct = integratedNode.LoadPct;
                snap.IntegratedGpuName    = integratedNode.Name;
            }
            else
            {
                snap.HasIntegratedGpu = false;
            }

            if (coreList.Count > 0)
            {
                float avg = coreList.Sum() / coreList.Count;
                float max = coreList.Max();
                // Use Package if it was caught directly (score 115), otherwise the average;
                // if bestCpu comes from Package (cpuScore >= 110), prefer it over the average
                if (cpuScore < 110)
                {
                    bestCpu = avg;
                    cpuName = $"Core Avg ({coreList.Count} cores)";
                }
                // otherwise bestCpu stays Package/TDie — more precise
            }

            // tick with the CPU read skipped (see above): the values are the same as in the previous tick, so they do not count as "frozen"
            if (_cpuSkippedThisRead) goto AfterCpuTempStaleChecks;

            if (snap.CpuLoadPct.HasValue)
            {
                _lhmLoadHistory.Enqueue(snap.CpuLoadPct.Value);
                while (_lhmLoadHistory.Count > StaleLoadHistoryMax) _lhmLoadHistory.Dequeue();
            }

            // only real changes count: a repeated value says nothing about the sensor's resolution
            if (bestCpu.HasValue && (!_lhmLastCpuTemp.HasValue || Math.Abs(bestCpu.Value - _lhmLastCpuTemp.Value) >= 0.25f))
                NoteCpuTempResolution(bestCpu.Value);

            bool tempUnchanged = bestCpu.HasValue && _lhmLastCpuTemp.HasValue
                && Math.Abs(bestCpu.Value - _lhmLastCpuTemp.Value) < 0.25f;
            if (!tempUnchanged || _lhmLastValueChangeUtc == System.DateTime.MinValue)
                _lhmLastValueChangeUtc = System.DateTime.UtcNow;

            if (tempUnchanged)
            {
                AppLogger.Debug(
                    "STALE-CHECK: temp={0:F2}°C (prev={1:F2}, Δ={2:F3}) StaleCount={3} ForceUpdateCount={4} " +
                    "Load={5:F0}% LoadHistory[{6}]={7:F0}%↔{8:F0}% StressActive={9} StaleAccepted={10} " +
                    "(current thresholds: LoadImplausibleMin={11:F0}%, TempImplausibleC={12:F0}°C, LoadVarianceMin={13:F0}%, AcceptedMaxAge={14:F0}s)",
                    bestCpu.GetValueOrDefault(), _lhmLastCpuTemp.GetValueOrDefault(), bestCpu.GetValueOrDefault() - _lhmLastCpuTemp.GetValueOrDefault(),
                    _lhmStaleCount, _lhmForceUpdateCount,
                    snap.CpuLoadPct ?? -1f, _lhmLoadHistory.Count,
                    _lhmLoadHistory.Count > 0 ? _lhmLoadHistory.Min() : -1f,
                    _lhmLoadHistory.Count > 0 ? _lhmLoadHistory.Max() : -1f,
                    StressActive, _staleAccepted,
                    StaleLoadImplausibleMin, StaleTempImplausibleC, StaleLoadVarianceMin, StaleAcceptedMaxAge.TotalSeconds);

                if (!_staleAccepted)
                {
                    _lhmStaleCount++;
                    if (_lhmStaleCount >= 2)
                    {
                        bool loadVaries = _lhmLoadHistory.Count >= StaleLoadHistoryMax
                            && (_lhmLoadHistory.Max() - _lhmLoadHistory.Min()) >= StaleLoadVarianceMin;

                        if (loadVaries && (System.DateTime.UtcNow - _lhmLastValueChangeUtc).TotalSeconds >= LoadVariesFreezeSeconds)
                        {
                            // A sensor that is certainly stuck — the live Load contradicts a frozen temp.
                            AppLogger.Warning(
                                "CPU temp frozen at {0}°C but Load varies ({1:F0}%↔{2:F0}%) — LHM reinit",
                                bestCpu.GetValueOrDefault(), _lhmLoadHistory.Min(), _lhmLoadHistory.Max());
                            DumpLhmValueHistory();
                            _ = System.Threading.Tasks.Task.Run(() => ReinitLhm(resetFreqTracking: false));
                            snap.CpuTempStale = true; // mark the current read on the chart (dotted line)
                        }
                        else
                        {
                            _lhmForceUpdateCount++;
                            bool frozenLongEnough = (System.DateTime.UtcNow - _lhmLastValueChangeUtc).TotalSeconds >= StressFreezeSeconds;
                            if (StressActive && _lhmForceUpdateCount > 1 && frozenLongEnough)
                            {
                                // Stress active + still frozen after 2 attempts → definitely a stuck sensor, force a full reinit (not just Update()).
                                AppLogger.Warning(
                                    "CPU temp frozen at {0}°C during the stress test after {1} force-updates — LHM reinit",
                                    bestCpu.GetValueOrDefault(), _lhmForceUpdateCount);
                                DumpLhmValueHistory();
                                _ = System.Threading.Tasks.Task.Run(() => ReinitLhm(resetFreqTracking: false));
                                snap.CpuTempStale = true;
                                _lhmForceUpdateCount = 0;
                            }
                            else if (!StressActive && _lhmForceUpdateCount > 3)
                            {
                                float loadNowCheck = snap.CpuLoadPct ?? -1f;
                                float loadMaxRecent = _lhmLoadHistory.Count > 0 ? _lhmLoadHistory.Max() : loadNowCheck;
                                bool implausible = bestCpu.GetValueOrDefault() < ImplausibleTempC
                                                 && Math.Max(loadNowCheck, loadMaxRecent) > ImplausibleLoadMin;
                                if (implausible)
                                {
                                    AppLogger.Warning(
                                        "CPU temp IMPLAUSIBLE: {0}°C accepted as 'stable' but Load is {1:F0}% (recent max {2:F0}%) —" +
                                        "a CPU under real load cannot stay at {0}°C, it is certainly a stuck sensor, not a real value." +
                                        "Forced LHM reinit, NOT accepted.",
                                        bestCpu.GetValueOrDefault(), loadNowCheck, loadMaxRecent);
                                    DumpLhmValueHistory();
                                    _ = System.Threading.Tasks.Task.Run(() => ReinitLhm(resetFreqTracking: false));
                                    snap.CpuTempStale = true;
                                    _lhmForceUpdateCount = 0;
                                }
                                else
                                {
                                    _staleAccepted = true;
                                    _staleAcceptedAtUtc = System.DateTime.UtcNow;
                                    if (!_staleLoggedOnce)
                                    {
                                        _staleLoggedOnce = true;
                                        float loadNow = snap.CpuLoadPct ?? -1f;
                                        float loadMin = _lhmLoadHistory.Count > 0 ? _lhmLoadHistory.Min() : loadNow;
                                        float loadMax = _lhmLoadHistory.Count > 0 ? _lhmLoadHistory.Max() : loadNow;
                                        AppLogger.Info(
                                            "CPU temp constant at {0}°C — accepted as real reading (Load {1:F0}%, recent history {2:F0}%↔{3:F0}%, plausibility check: OK)",
                                            bestCpu.GetValueOrDefault(), loadNow, loadMin, loadMax);
                                    }
                                }
                            }
                            else
                            {
                                AppLogger.Debug("LHM CPU temp stale ({0}°C) — force-Update #{1}", bestCpu.GetValueOrDefault(), _lhmForceUpdateCount);
                                try
                                {
                                    foreach (var hw in _computer!.Hardware)
                                    {
                                        try { hw.Update(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                                        foreach (var sub in hw.SubHardware)
                                            try { sub.Update(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                                    }
                                }
                                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                                // _lhmStaleCount is NOT reset here (see the comment above).
                            }
                        }
                    }
                }
                else
                {
                    bool loadVariesNow = _lhmLoadHistory.Count >= StaleLoadHistoryMax
                        && (_lhmLoadHistory.Max() - _lhmLoadHistory.Min()) >= StaleLoadVarianceMin;
                    float loadNowRecheck = snap.CpuLoadPct ?? -1f;
                    bool implausibleNow = bestCpu.GetValueOrDefault() < ImplausibleTempC && loadNowRecheck > ImplausibleLoadMin;
                    if ((loadVariesNow && (System.DateTime.UtcNow - _lhmLastValueChangeUtc).TotalSeconds >= LoadVariesFreezeSeconds) || implausibleNow)
                    {
                        if (implausibleNow && !loadVariesNow)
                        {
                            AppLogger.Warning(
                                "CPU temp IMPLAUSIBLE after acceptance: {0}°C accepted earlier, but current Load is {1:F0}% — revoking acceptance immediately, LHM reinit.",
                                bestCpu.GetValueOrDefault(), loadNowRecheck);
                        }
                        else
                        {
                            AppLogger.Warning(
                                "CPU temp still frozen at {0}°C after acceptance, but Load now varies ({1:F0}%↔{2:F0}%) — revoking acceptance, LHM reinit",
                                bestCpu.GetValueOrDefault(), _lhmLoadHistory.Min(), _lhmLoadHistory.Max());
                        }
                        DumpLhmValueHistory();
                        _ = System.Threading.Tasks.Task.Run(() => ReinitLhm(resetFreqTracking: false));
                        snap.CpuTempStale = true;
                    }
                    else if (_lhmLoadHistory.Count > 0)
                    {
                        AppLogger.Debug(
                            "CPU temp watchdog re-check: temp still accepted at {0}°C, Load history {1:F0}%↔{2:F0}% ({3} samples) — below the {4:F0}% threshold, no reinit.",
                            bestCpu.GetValueOrDefault(), _lhmLoadHistory.Min(), _lhmLoadHistory.Max(), _lhmLoadHistory.Count, StaleLoadVarianceMin);
                    }
                    // 2) Acceptance expiry — restart the detection cycle periodically, so a really stuck sensor gets new chances to
                    //    reinit instead of being shown as "real" indefinitely.
                    else if (System.DateTime.UtcNow - _staleAcceptedAtUtc > StaleAcceptedMaxAge)
                    {
                        AppLogger.Debug("CPU temp accepted as real expired after {0:F0}s at {1}°C — checking again",
                            StaleAcceptedMaxAge.TotalSeconds, bestCpu.GetValueOrDefault());
                        _staleAccepted = false;
                        // _staleLoggedOnce stays true — "accepted" is not logged again on every cycle
                        _lhmStaleCount = 0;
                        _lhmForceUpdateCount = 0;
                    }
                }
            }
            else if (!bestCpu.HasValue)
            {
                if (snap.CpuLoadPct.HasValue && snap.CpuLoadPct.Value >= StaleLoadImplausibleMin)
                {
                    _lhmNullCount++;
                    AppLogger.Debug(
                        "NULL-CHECK: cpuTemp=null NullCount={0}/{1} Load={2:F0}% StressActive={3}",
                        _lhmNullCount, StaleNullReinitThreshold, snap.CpuLoadPct.Value, StressActive);
                    // no sensor driver yet (PawnIO not installed): a restart cannot bring the temperature back, and three restarts
                    // in a row at start-up kept the processor near 100 % (Acer Ryzen 5 3500U) — none in that case
                    if (_lhmNullCount >= StaleNullReinitThreshold && !_lhmEverCpuTemp
                        && (_lhmNullReinits >= MaxNullReinitsWithoutTemp || !SafePawnIoInstalled()))
                    {
                        // the sensor library never gave a processor temperature in this session (usually: no sensor driver):
                        // restarting it every few seconds changes nothing and costs processor time
                        if (!_lhmNullReinitsStopped)
                        {
                            _lhmNullReinitsStopped = true;
                            AppLogger.Info("CPU temp: no reading from the sensor library after {0} restarts in this session — no more restarts for a missing value (sensor driver installed: {1})",
                                _lhmNullReinits, SafePawnIoInstalled());
                        }
                        _lhmNullCount = 0;
                    }
                    else if (_lhmNullCount >= StaleNullReinitThreshold)
                    {
                        _lhmNullReinits++;
                        AppLogger.Warning(
                            "CPU temp NULL for {0} consecutive ticks with Load={1:F0}% (CPU clearly active, the sensor does not answer at all) — LHM reinit",
                            _lhmNullCount, snap.CpuLoadPct.Value);
                        DumpLhmValueHistory();
                        _lhmNullCount = 0;
                        _ = System.Threading.Tasks.Task.Run(() => ReinitLhm());
                    }
                }
                else
                {
                    // Low/unknown load — null is normal (real idle, or LHM still warming up at start/after a reinit); no conclusion is drawn.
                    _lhmNullCount = 0;
                }
                snap.CpuTempStale = false;
            }
            else
            {
                // The value REALLY changed — full reset, the sensor is alive
                _lhmEverCpuTemp = true;
                _lhmStaleCount = 0;
                _lhmForceUpdateCount = 0;
                _lhmNullCount = 0;
                snap.CpuTempStale = false;
                if (_staleAccepted)
                {
                    _staleAccepted = false;
                    _staleLoggedOnce = false;
                }
            }
            if (bestCpu.HasValue) _lhmEverCpuTemp = true;
            _lhmLastCpuTemp = bestCpu;

        AfterCpuTempStaleChecks:
            if (coreFreqs.Count > 0)
            {
                snap.CpuFreqMHz = coreFreqs.Max();
                snap.CoreFreqs.AddRange(coreFreqs.OrderByDescending(x => x));
            }
            ApplyCpuClockPlausibility(snap);

            if (!_cpuSkippedThisRead && snap.CpuFreqMHz.HasValue && snap.CpuFreqMHz.Value > 0)
            {
                if (!_lhmMinFreqSeen.HasValue || snap.CpuFreqMHz.Value < _lhmMinFreqSeen.Value) _lhmMinFreqSeen = snap.CpuFreqMHz.Value;
                if (!_lhmMaxFreqSeen.HasValue || snap.CpuFreqMHz.Value > _lhmMaxFreqSeen.Value) _lhmMaxFreqSeen = snap.CpuFreqMHz.Value;
                if (_lhmLastCpuFreq.HasValue && Math.Abs(snap.CpuFreqMHz.Value - _lhmLastCpuFreq.Value) < 0.5f)
                {
                    _lhmFreqStaleCount++;
                    if (_lhmFreqStaleCount >= StaleFreqReinitThreshold)
                    {
                        bool loadVaries = _lhmLoadHistory.Count >= StaleLoadHistoryMax
                            && (_lhmLoadHistory.Max() - _lhmLoadHistory.Min()) >= StaleFreqLoadVarianceMin;
                        float freqNow = snap.CpuFreqMHz.Value;
                        bool atIdleFloor = _lhmMinFreqSeen.HasValue && freqNow <= _lhmMinFreqSeen.Value + 1f;   // repaus: frecventa minima normala
                        // at the turbo ceiling: a desktop on a performance plan holds its all-core boost (e.g. i5-10400F 4006 / 4106 MHz)
                        // whatever the load — a steady clock there is normal, not a stuck sensor. Only once the sensor has been seen to move
                        // in this session (min well below max), so a value stuck from the start is still caught.
                        bool atTurboCeiling = _lhmMaxFreqSeen.HasValue && _lhmMinFreqSeen.HasValue
                            && _lhmMaxFreqSeen.Value - _lhmMinFreqSeen.Value > 300f
                            && freqNow >= _lhmMaxFreqSeen.Value * 0.95f;
                        bool fixedKnown = IsKnownFixedClock(freqNow) || atIdleFloor || atTurboCeiling || _lhmFreqReinitCount >= MaxFreqReinitsPerSession;
                        bool survivedReinit = !fixedKnown && _lhmFreqReinitMHz.HasValue && Math.Abs(freqNow - _lhmFreqReinitMHz.Value) < 0.5f;
                        if (survivedReinit)
                        {
                            LearnFixedClock(freqNow);
                            AppLogger.Info("CPU freq stays {0:F0} MHz even after the LHM reinit — a fixed clock / locked turbo, not a stuck sensor; frequency freeze detection stops while the value does not change", freqNow);
                        }
                        if (fixedKnown || survivedReinit)
                        {
                            _lhmFreqStaleCount = 0;
                        }
                        else if (loadVaries && _lhmStaleCount == 0)
                        {
                            // the temperature is still changing: LHM is alive, and a constant clock under varying load is the
                            // processor sitting on its turbo step (common on desktops) — no reinit (on the iMac it rebuilt every
                            // sensor twice in 2 minutes for this)
                            _lhmFreqStaleCount = 0;
                        }
                        else if (loadVaries)
                        {
                            _lhmFreqReinitMHz = freqNow;
                            _lhmFreqReinitCount++;
                            AppLogger.Warning(
                                "CPU freq frozen at {0:F0} MHz but Load varies ({1:F0}%↔{2:F0}%) — LHM reinit",
                                snap.CpuFreqMHz.Value, _lhmLoadHistory.Min(), _lhmLoadHistory.Max());
                            DumpLhmValueHistory();
                            _ = System.Threading.Tasks.Task.Run(() => ReinitLhm());
                        }
                        else
                        {
                            // Stable load + constant frequency = a normal state (long idle or a legitimately fixed turbo) — only the counter is
                            // reset, no reinit.
                            _lhmFreqStaleCount = 0;
                        }
                    }
                }
                else
                {
                    _lhmFreqStaleCount = 0;
                    // a value DIFFERENT from the one learned as a fixed clock / since the last reinit => detection is re-enabled
                    float fNow = snap.CpuFreqMHz.Value;
                    // the learned fixed steps are NOT forgotten when the value changes (the CPU parks on steps: 3193/3392 MHz)
                    if (_lhmFreqReinitMHz.HasValue && Math.Abs(fNow - _lhmFreqReinitMHz.Value) >= 0.5f) _lhmFreqReinitMHz = null;
                }
                _lhmLastCpuFreq = snap.CpuFreqMHz;
            }
            if (!_cpuSkippedThisRead) RecordLhmValueSample(bestCpu, snap.CpuFreqMHz, snap.CpuLoadPct);

            if (StressActive)
            {
                _stressLoadLogTickCount++;
                if (_stressLoadLogTickCount >= 10)
                {
                    _stressLoadLogTickCount = 0;
                    AppLogger.Info(
                        "S3-STRESS-LOAD cpuLoad={0}% cpuTemp={1}°C cpuFreq={2}MHz mode={3}",
                        snap.CpuLoadPct.HasValue ? snap.CpuLoadPct.Value.ToString("F0") : "null",
                        bestCpu.HasValue ? bestCpu.Value.ToString("F1") : "null",
                        snap.CpuFreqMHz.HasValue ? snap.CpuFreqMHz.Value.ToString("F0") : "null",
                        _currentMode);
                }
            }
            else
            {
                _stressLoadLogTickCount = 0;
            }

            if (bestCpu.HasValue)
            {
                snap.CpuTemp       = bestCpu;
                snap.CpuSensorName = cpuName;
            }
            // the GPU assignment (GpuTemp/GpuSensorName/etc.) is done above, right after the foreach — see the "final composition
            // of the GPUs" block (depends on which node is dedicated/integrated).

            // ── Throttle detection ───────────────────────────────────────────
            // CPU throttle: temp >= 95°C, or freq drops > 30% below max
            DetectThrottling(snap);
        }

        private void DetectThrottling(TempSnapshot snap)
        {
            var reasons = new List<string>();

            // Thermal throttle: CPU or GPU near max temperature. The processor limit differs (Intel ~100°C, AMD 90–95°C), so the
            // threshold is relative to its TjMax.
            float cpuThermalC = Math.Clamp(EstimatedTjMaxC, 85u, 115u) - 5f;
            if (snap.CpuTemp.HasValue && snap.CpuTemp.Value >= cpuThermalC)
                reasons.Add($"CPU Thermal ({snap.CpuTemp.Value:F0}°C)");
            if (snap.GpuTemp.HasValue && snap.GpuTemp.Value >= 87f)
                reasons.Add($"GPU Thermal ({snap.GpuTemp.Value:F0}°C)");

            // Power throttle: if we have power data and freq is low while load is high
            if (snap.CpuFreqMHz.HasValue && snap.CpuLoadPct.HasValue
                && snap.CpuLoadPct.Value > 80f && snap.CpuFreqMHz.Value < 1500f)
                reasons.Add("CPU Power Limit");

            // Check LHM sensors for BDPROCHOT/power limit flags
            try
            {
                if (_computer != null)
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType != HardwareType.Cpu) continue;
                        foreach (var s in hw.Sensors)
                        {
                            if (s == null || !s.Value.HasValue) continue;
                            string sn = (s.Name ?? "").ToLower();
                            // BDPROCHOT: external thermal alert pin (prochot = Processor Hot)
                            if (sn.Contains("prochot") || sn.Contains("bdprochot"))
                            {
                                if (s.Value.Value > 0)
                                    reasons.Add("BDPROCHOT (External Thermal Alert)");
                            }
                            if (sn.Contains("power limit") || sn.Contains("pl1") || sn.Contains("pl2")
                                || sn.Contains("thermal throttle") || sn.Contains("throttling"))
                            {
                                if (s.Value.Value > 0 && !reasons.Any(r => r.Contains("Power")))
                                    reasons.Add("CPU Power Limit (PL1/PL2)");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            snap.CpuThrottled = reasons.Any(r => r.Contains("CPU"));
            snap.GpuThrottled = reasons.Any(r => r.Contains("GPU"));
            snap.ThrottleReason = reasons.Count > 0 ? string.Join(", ", reasons) : "";
        }
    }
}
