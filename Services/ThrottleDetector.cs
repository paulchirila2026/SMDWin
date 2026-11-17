
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    public enum ThrottleReason
    {
        None,           // No throttle
        BdProchot,      // BD PROCHOT — the charger / EC signals overcurrent
        Thermal,        // TJ MAX reached — temperature too high
        PowerLimit,     // PL1/PL2 power limit
        BatterySaver,   // Windows Battery Saver / power plan
        VrThermal,      // VR (voltage regulator) thermal — similar to BD PROCHOT
        Unknown,        // Limited clock but unknown cause
        FixedFrequency,
    }

    public class ThrottleSnapshot
    {
        public float FreqMHz       { get; init; }   // Current frequency
        public float MaxFreqMHz    { get; init; }   // Frecventa maxima (turbo)
        public float FreqRatio     { get; init; }   // 0..1 (1.0 = full speed)
        public bool  IsThrottled   { get; init; }   // true if < 50% of max
        public bool  IsSevere      { get; init; }   // true if < 15% of max (0.4 GHz pattern)
        public ThrottleReason Reason { get; init; }
        public string ReasonLabel  { get; init; } = "";
        public string ReasonDetail { get; init; } = "";
        public bool  IsPluggedIn   { get; init; }
        public float CpuTempC      { get; init; }   // NaN if unknown
        /// <summary>false = the frequency could not be read now; the verdict is based only on temperature.</summary>
        public bool  HasFrequency  { get; init; } = true;
        /// <summary>Turbo is disabled in the Windows power plan (Processor performance boost mode = Disabled) for the current
        /// source (AC / battery). Not throttling: the processor is limited to its base frequency by a setting.</summary>
        public bool  BoostDisabled { get; set; }
    }

    public sealed class ThrottleDetector : IDisposable
    {
        // ── PerformanceCounters ───────────────────────────────────────────────
        private PerformanceCounter? _pcFreqPct;   // % of Maximum Frequency
        private PerformanceCounter? _pcFreqMHz;   // Processor Frequency (MHz) — the NOMINAL clock, it does not move
        // % Processor Performance: the real clock as a share of the nominal one (above 100 with turbo, below when it slows down).
        // Real MHz = Processor Frequency × this / 100 — what Task Manager shows. Without it the counter "froze" at the base clock
        // (3201 MHz on the iMac) while the processor was actually at 3.4–3.6 GHz.
        private PerformanceCounter? _pcPerfPct;
        private bool _initialized = false;
        private float _baseMHz    = 0f;           // base frequency from WMI (fallback)
        private DateTime _lastThrottleDiagLog = DateTime.MinValue;

        private int _readGate = 0; // 0 = free, 1 = a Read() is running
        private float? _pcLastFreqMHz = null;
        private float _pcMinFreqSeen, _pcMaxFreqSeen;   // the range seen this session (turbo-ceiling rule)
        private readonly System.Collections.Generic.HashSet<int> _pcLearnedFixed = new();
        private readonly System.Collections.Generic.Dictionary<int, int> _pcReinitCounts = new();
        private int    _pcFreqStaleCount = 0;
        private float? _pcReinitFreqMHz = null;
        private float? _pcFixedClockMHz = null;
        private const int PcFreqStaleReinitThreshold = 6; // the same order of magnitude as StaleFreqReinitThreshold in TempReader
        private readonly System.Collections.Generic.Queue<float> _pcLoadHistory = new();
        private const int   PcLoadHistoryMax     = 8;
        private const float PcLoadVarianceMin    = 20f; // % — the same threshold as for LHM

        private DateTime? _lowFreqUnderLoadSince = null;
        private const int SustainedThrottleSeconds = 8;

        private float _observedMaxMHz = 0f;
        private const float HighLoadForCalibration = 85f;

        private readonly System.Collections.Generic.List<(DateTime t, float freq, float load)> _sustainedSamples = new();
        private const int   StuckWindowSeconds     = 20;
        private const float StuckFreqToleranceMHz  = 60f;   // variation below this = "fixed"
        private const float StuckRatioCeiling      = 0.75f; // below what fraction of the calibrated max it counts as real throttling

        // ── Win32 API for the battery state ─────────────────────────────────
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte  ACLineStatus;      // 0 = baterie, 1 = AC
            public byte  BatteryFlag;
            public byte  BatteryLifePercent;
            public byte  SystemStatusFlag;  // bit0 = Battery Saver active
            public uint  BatteryLifeTime;
            public uint  BatteryFullLifeTime;
        }

        /// <summary>Maximum junction temperature (°C) for the current processor. The thermal thresholds are computed relative to
        /// it: thermal throttling = temperature near TjMax AND a low frequency (below 90 % of max at TjMax − 3, below 85 % from
        /// TjMax − 10); at TjMax itself, always.</summary>
        public float TjMaxC
        {
            get => _tjMaxC;
            set { if (value >= 70f && value <= 125f) _tjMaxC = value; }
        }
        private float _tjMaxC = 100f;

        private DateTime _boostCheckedUtc = DateTime.MinValue;
        private bool _boostDisabled;
        private bool _boostCheckedForAc;

        /// <summary>The performance counters and the WMI query are created on the first read (<see cref="Read"/>, on a background
        /// thread), not here: the first counter in the process can take a few seconds, and the detector is created with the window.</summary>
        public ThrottleDetector() { }

        /// <summary>No performance counters — only the classification logic (<see cref="Evaluate"/>), for tests.</summary>
        internal ThrottleDetector(float baseMHz)
        {
            _baseMHz = baseMHz;
            _countersDisabled = true;
        }
        private readonly bool _countersDisabled;

        private void TryInit()
        {
            if (_countersDisabled) return;
            try
            {
                _pcFreqPct = new PerformanceCounter(
                    "Processor Information", "% of Maximum Frequency", "_Total", true);
                _pcFreqMHz = new PerformanceCounter(
                    "Processor Information", "Processor Frequency", "_Total", true);
                try
                {
                    _pcPerfPct = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    _pcPerfPct.NextValue();
                }
                catch (Exception ex) { AppLogger.Debug($"ThrottleDetector % Processor Performance: {ex.Message}"); _pcPerfPct = null; }
                // Prime counters
                _pcFreqPct.NextValue();
                _pcFreqMHz.NextValue();
                _initialized = true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"ThrottleDetector TryInit counters: {ex.Message}");
                _initialized = false;
            }

            // Base frequency from WMI as a fallback
            try
            {
                using var searcher = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT MaxClockSpeed, Name FROM Win32_Processor", System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp1 = searcher.Get())
                {
                    foreach (var obj in __wmiTmp1)
                {
                    _baseMHz = Convert.ToSingle(obj["MaxClockSpeed"]);
                    break;
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"ThrottleDetector TryInit MaxClockSpeed: {ex.Message}"); }
        }

        private void ReinitCounters()
        {
            try { _pcFreqPct?.Dispose(); } catch (Exception ex) { AppLogger.Debug($"ThrottleDetector ReinitCounters dispose _pcFreqPct: {ex.Message}"); }
            try { _pcFreqMHz?.Dispose(); } catch (Exception ex) { AppLogger.Debug($"ThrottleDetector ReinitCounters dispose _pcFreqMHz: {ex.Message}"); }
            try { _pcPerfPct?.Dispose(); } catch (Exception ex) { AppLogger.Debug($"ThrottleDetector ReinitCounters dispose _pcPerfPct: {ex.Message}"); }
            _pcFreqPct = null;
            _pcFreqMHz = null;
            _pcPerfPct = null;
            _initialized = false;
            _pcLastFreqMHz = null;
            _pcFreqStaleCount = 0;
            _pcLoadHistory.Clear();
            TryInit();
        }

        /// <param name="sensorClockMHz">The real core frequency read by the sensors (LibreHardwareMonitor), when available.
        /// It takes precedence over the Windows counters: on many processors (Ryzen, some Intel) the counter always shows the
        /// base frequency and does not see the boost.</param>
        public ThrottleSnapshot? Read(float cpuTempC = float.NaN, float cpuLoadPct = float.NaN, float sensorClockMHz = float.NaN)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _readGate, 1, 0) != 0)
                return null;
            try
            {
                return ReadCore(cpuTempC, cpuLoadPct, sensorClockMHz);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _readGate, 0);
            }
        }

        private ThrottleSnapshot ReadCore(float cpuTempC, float cpuLoadPct, float sensorClockMHz)
        {
            if (!_initialized) TryInit();

            // ── Current frequency ─────────────────────────────────────────────
            float freqPct = 0f, freqMHz = 0f, perfPct = 0f;
            try
            {
                if (_pcFreqPct != null) freqPct = _pcFreqPct.NextValue();   // 0..100
                if (_pcFreqMHz != null) freqMHz = _pcFreqMHz.NextValue();
                if (_pcPerfPct != null) perfPct = _pcPerfPct.NextValue();   // 0..~150
            }
            catch (Exception ex)
            {
                // counter invalidated (sleep/resume, performance service restarted): recreated on the next read
                AppLogger.Debug($"ThrottleDetector counters read: {ex.Message}");
                try { _pcFreqPct?.Dispose(); } catch (Exception dex) { AppLogger.Debug(dex.Message); }
                try { _pcFreqMHz?.Dispose(); } catch (Exception dex) { AppLogger.Debug(dex.Message); }
                _pcFreqPct = null; _pcFreqMHz = null;
                _initialized = false;
                freqPct = 0f; freqMHz = 0f;
            }

            float maxMHz = _baseMHz > 0 ? _baseMHz : (freqMHz > 0 ? freqMHz / Math.Max(0.01f, freqPct / 100f) : 0f);

            if (freqMHz <= 0f && freqPct > 0f && maxMHz > 0f)
                freqMHz = maxMHz * (freqPct / 100f);
            // the real clock: nominal × % Processor Performance (see _pcPerfPct)
            if (freqMHz > 0f && perfPct > 1f && perfPct < 300f)
                freqMHz = freqMHz * perfPct / 100f;

            if (freqMHz > 0f)
            {
                _pcLoadHistory.Enqueue(cpuLoadPct);
                while (_pcLoadHistory.Count > PcLoadHistoryMax) _pcLoadHistory.Dequeue();

                if (freqMHz > 0)
                {
                    if (_pcMinFreqSeen <= 0 || freqMHz < _pcMinFreqSeen) _pcMinFreqSeen = freqMHz;
                    if (freqMHz > _pcMaxFreqSeen) _pcMaxFreqSeen = freqMHz;
                }
                if (_pcLastFreqMHz.HasValue && Math.Abs(freqMHz - _pcLastFreqMHz.Value) < 0.5f)
                {
                    _pcFreqStaleCount++;
                    if (_pcFreqStaleCount >= PcFreqStaleReinitThreshold)
                    {
                        bool loadVaries = _pcLoadHistory.Count >= PcLoadHistoryMax
                            && (_pcLoadHistory.Max() - _pcLoadHistory.Min()) >= PcLoadVarianceMin;
                        // at the turbo ceiling (a desktop holding its all-core boost) a steady value is normal — see TempReader
                        bool pcAtCeiling = _pcMaxFreqSeen - _pcMinFreqSeen > 300f && freqMHz >= _pcMaxFreqSeen * 0.95f;
                        bool pcFixedKnown = pcAtCeiling || (_pcFixedClockMHz.HasValue && Math.Abs(freqMHz - _pcFixedClockMHz.Value) < 0.5f);
                        bool pcSurvived = !pcFixedKnown && _pcReinitFreqMHz.HasValue && Math.Abs(freqMHz - _pcReinitFreqMHz.Value) < 0.5f;
                        if (pcSurvived)
                        {
                            _pcFixedClockMHz = freqMHz;
                            AppLogger.Info("ThrottleDetector: frequency stays {0:F0} MHz even after recreating the counters — fixed clock, not a stuck sensor; freeze detection stops while the value does not change", freqMHz);
                        }
                        // the same frequency recreated twice with no effect = a fixed value of this computer (e.g. iMac: always 3201 MHz)
                        int roundedMHz = (int)Math.Round(freqMHz);
                        bool learnedFixed = _pcLearnedFixed.Contains(roundedMHz);
                        if (loadVaries && !pcFixedKnown && !pcSurvived && !learnedFixed)
                        {
                            _pcReinitCounts[roundedMHz] = _pcReinitCounts.GetValueOrDefault(roundedMHz) + 1;
                            if (_pcReinitCounts[roundedMHz] >= 2) _pcLearnedFixed.Add(roundedMHz);
                            _pcReinitFreqMHz = freqMHz;
                            AppLogger.Warning(
                                "ThrottleDetector: freq frozen at {0:F0}MHz (PerformanceCounter) but Load varies ({1:F0}%↔{2:F0}%) — recreating the PerformanceCounters",
                                freqMHz, _pcLoadHistory.Min(), _pcLoadHistory.Max());
                            ReinitCounters();
                            freqPct = 0f; freqMHz = 0f; // the next tick starts clean; this one stays without a reading, better than a broken one
                        }
                        _pcFreqStaleCount = 0;
                    }
                }
                else
                {
                    _pcFreqStaleCount = 0;
                    if (_pcFixedClockMHz.HasValue && Math.Abs(freqMHz - _pcFixedClockMHz.Value) >= 0.5f) _pcFixedClockMHz = null;
                    if (_pcReinitFreqMHz.HasValue && Math.Abs(freqMHz - _pcReinitFreqMHz.Value) >= 0.5f) _pcReinitFreqMHz = null;
                }
                if (freqMHz > 0f) _pcLastFreqMHz = freqMHz;
            }

            // The real frequency from the sensors, when available, replaces the Windows counter. Without it, a counter stuck at a
            // fixed value under variable load (learned fixed clock) says nothing about throttling: it is treated as "no frequency",
            // and the verdict rests on temperature only.
            if (float.IsFinite(sensorClockMHz) && sensorClockMHz > 100f)
            {
                freqMHz = sensorClockMHz;
                freqPct = 0f;
            }
            else if (freqMHz > 0f && _pcFixedClockMHz.HasValue && Math.Abs(freqMHz - _pcFixedClockMHz.Value) < 0.5f)
            {
                freqMHz = 0f;
                freqPct = 0f;
            }

            // ── Starea AC/Baterie ─────────────────────────────────────────────
            bool isPluggedIn    = true;
            bool batterySaverOn = false;
            bool hasBattery     = true;
            try
            {
                if (GetSystemPowerStatus(out var sps))
                {
                    isPluggedIn    = sps.ACLineStatus != 0;   // 1 = mains, 255 = unknown (treated as mains), 0 = battery
                    batterySaverOn = (sps.SystemStatusFlag & 0x01) != 0;
                    hasBattery     = sps.BatteryFlag != 128; // 0x80 = No system battery
                }
            }
            catch (Exception ex) { AppLogger.Debug($"ThrottleDetector GetSystemPowerStatus: {ex.Message}"); }

            var result = Evaluate(freqMHz, freqPct, cpuTempC, cpuLoadPct, isPluggedIn, batterySaverOn, hasBattery, DateTime.UtcNow);
            result.BoostDisabled = IsBoostDisabled(isPluggedIn);
            return result;
        }

        /// <summary>Turbo disabled in the power plan, for the current source. Re-read at most once every 30 s
        /// (or immediately when the source changes).</summary>
        private bool IsBoostDisabled(bool onAc)
        {
            var now = DateTime.UtcNow;
            if (onAc == _boostCheckedForAc && (now - _boostCheckedUtc).TotalSeconds < 30) return _boostDisabled;
            _boostCheckedUtc = now;
            _boostCheckedForAc = onAc;
            try { _boostDisabled = PowerAdvancedService.GetBoostMode(onAc) == PowerAdvancedService.BoostMode.Disabled; }
            catch (Exception ex) { AppLogger.Debug($"ThrottleDetector boost mode: {ex.Message}"); _boostDisabled = false; }
            return _boostDisabled;
        }

        /// <summary>The verdict for one reading: current frequency (MHz and/or % of max), temperature, load and power state.
        /// Separate from reading the counters so it can be tested.</summary>
        internal ThrottleSnapshot Evaluate(float freqMHz, float freqPct, float cpuTempC, float cpuLoadPct,
            bool isPluggedIn, bool batterySaverOn, bool hasBattery, DateTime now)
        {
            float maxMHz = _baseMHz > 0 ? _baseMHz : (freqMHz > 0 && freqPct > 0 ? freqMHz / Math.Max(0.01f, freqPct / 100f) : 0f);
            if (freqMHz <= 0f && freqPct > 0f && maxMHz > 0f)
                freqMHz = maxMHz * (freqPct / 100f);

            // Without a frequency (counter not ready or just recreated) there is no frequency verdict: a 0 MHz reading does not
            // mean a stopped processor. Only the thermal limit remains, which does not depend on frequency.
            if (freqMHz <= 0f)
            {
                _lowFreqUnderLoadSince = null;
                // without a frequency a drop cannot be seen: thermal throttling only when the temperature really reached TjMax
                bool atLimitNoFreq = !float.IsNaN(cpuTempC) && cpuTempC >= _tjMaxC;
                string noFreqDetail = atLimitNoFreq
                    ? string.Format(
                        LanguageService.S("Throttle", "ThermalDetail",
                            "CPU at {0}°C — TJ MAX reached. Check the thermal paste and clean the heatsink.\nFrequency: {1} MHz ({2}% of max)"),
                        cpuTempC.ToString("F0"), "—", "—")
                    : "";
                return new ThrottleSnapshot
                {
                    FreqMHz      = 0f,
                    MaxFreqMHz   = Math.Max(maxMHz, _observedMaxMHz),
                    FreqRatio    = 1f,
                    IsThrottled  = atLimitNoFreq,
                    IsSevere     = false,
                    Reason       = atLimitNoFreq ? ThrottleReason.Thermal : ThrottleReason.None,
                    ReasonLabel  = atLimitNoFreq ? LanguageService.S("Throttle", "ThermalLabel", "Thermal Throttle") : "OK",
                    ReasonDetail = noFreqDetail,
                    IsPluggedIn  = isPluggedIn,
                    CpuTempC     = cpuTempC,
                    HasFrequency = false,
                };
            }

            // Under heavy load the observed frequency is a better reference than MaxClockSpeed (which on many CPUs is really the
            // base frequency, not the real turbo).
            if (!float.IsNaN(cpuLoadPct) && cpuLoadPct >= HighLoadForCalibration && freqMHz > _observedMaxMHz)
                _observedMaxMHz = freqMHz;
            float effectiveMaxMHz = Math.Max(maxMHz, _observedMaxMHz);

            float ratio = effectiveMaxMHz > 0 ? freqMHz / effectiveMaxMHz : (freqPct / 100f);

            _sustainedSamples.Add((now, freqMHz, cpuLoadPct));
            _sustainedSamples.RemoveAll(s => (now - s.t).TotalSeconds > StuckWindowSeconds);
            bool stuckUnderLoad = false;
            if (effectiveMaxMHz > 0 && _sustainedSamples.Count >= 2
                && (now - _sustainedSamples[0].t).TotalSeconds >= StuckWindowSeconds - 1)
            {
                bool allUnderRealLoad = true;
                float minF = float.MaxValue, maxF = float.MinValue;
                foreach (var smp in _sustainedSamples)
                {
                    if (float.IsNaN(smp.load) || smp.load < 55f) { allUnderRealLoad = false; break; }
                    if (smp.freq < minF) minF = smp.freq;
                    if (smp.freq > maxF) maxF = smp.freq;
                }
                if (allUnderRealLoad && (maxF - minF) <= StuckFreqToleranceMHz
                    && (maxF / effectiveMaxMHz) < StuckRatioCeiling)
                    stuckUnderLoad = true;
            }

            bool isSevere = ratio < 0.15f;   // < 15% → 0.4GHz on a 3.2GHz i5-4570

            var (reason, label, detail) = ClassifyThrottle(
                ratio, isSevere, isPluggedIn, batterySaverOn, cpuTempC, freqMHz, effectiveMaxMHz, cpuLoadPct, stuckUnderLoad, hasBattery, now);

            if (!float.IsNaN(cpuTempC) && cpuTempC >= _tjMaxC - 12f
                && (now - _lastThrottleDiagLog).TotalSeconds >= 1)
            {
                _lastThrottleDiagLog = now;
                AppLogger.Debug("ThrottleDetector: temp={0:F0}°C tjMax={1:F0} load={2:F0}% freq={3:F0}MHz effMax={4:F0}MHz ratio={5:F2} severe={6} stuck={7} → reason={8}",
                    cpuTempC, _tjMaxC, cpuLoadPct, freqMHz, effectiveMaxMHz, ratio, isSevere, stuckUnderLoad, reason);
            }

            return new ThrottleSnapshot
            {
                FreqMHz    = freqMHz,
                MaxFreqMHz = effectiveMaxMHz,
                FreqRatio  = ratio,
                IsThrottled = reason != ThrottleReason.None,
                IsSevere    = isSevere,
                Reason      = reason,
                ReasonLabel = label,
                ReasonDetail = detail,
                IsPluggedIn = isPluggedIn,
                CpuTempC    = cpuTempC,
            };
        }

        private (ThrottleReason, string, string) ClassifyThrottle(
            float ratio, bool severe, bool pluggedIn, bool batterySaver,
            float temp, float freqMHz, float maxMHz, float loadPct, bool stuckUnderLoad,
            bool hasBattery, DateTime now)
        {
            float limitC = _tjMaxC - 3f;    // here the processor surely reduces the frequency
            float hotC   = _tjMaxC - 10f;   // zona fierbinte
            float nearC  = _tjMaxC - 20f;

            bool tempInThermalRange = !float.IsNaN(temp) && temp >= hotC;
            if (!severe && ratio >= 0.50f && !stuckUnderLoad && !tempInThermalRange)
            {
                _lowFreqUnderLoadSince = null;
                return (ThrottleReason.None, "OK", "");
            }

            bool lowLoad = !float.IsNaN(loadPct) && loadPct < 55f;

            if (severe && pluggedIn && hasBattery && !lowLoad && (float.IsNaN(temp) || temp < nearC))
            {
                string freqText = freqMHz > 0f ? $"{freqMHz:F0} MHz" : "N/A";
                string detail = string.Format(
                    LanguageService.S("Throttle", "BdProchotDetail",
                        "Charger or EC signals overcurrent to the processor.\nFix: ThrottleStop → Options → disable BD PROCHOT.\nCurrent frequency: {0} ({1}% of max)"),
                    freqText, (ratio * 100).ToString("F0"));
                return (ThrottleReason.BdProchot,
                        LanguageService.S("Throttle", "BdProchotLabel", "BD PROCHOT"),
                        detail);
            }

            // Thermal = hot AND a low frequency. Modern processors (Ryzen, recent Intel) climb by themselves close to TjMax and hold
            // the maximum frequency there — that is normal operation, not throttling. Near the limit (TjMax − 3) a small drop is
            // enough (below 90 % of the maximum seen under load); in the hot zone below it, a clear one (below 85 %).
            // At TjMax itself it counts anyway.
            if (!float.IsNaN(temp) && (temp >= _tjMaxC || (temp >= limitC && ratio < 0.90f) || (temp >= hotC && ratio < 0.85f)))
            {
                string detail = string.Format(
                    LanguageService.S("Throttle", "ThermalDetail",
                        "CPU at {0}°C — TJ MAX reached. Check the thermal paste and clean the heatsink.\nFrequency: {1} MHz ({2}% of max)"),
                    temp.ToString("F0"), freqMHz.ToString("F0"), (ratio * 100).ToString("F0"));
                return (ThrottleReason.Thermal, LanguageService.S("Throttle", "ThermalLabel", "Thermal Throttle"), detail);
            }

            if (!float.IsNaN(temp) && temp >= nearC && severe)
            {
                string detail = string.Format(
                    LanguageService.S("Throttle", "ThermalNearDetail",
                        "CPU at {0}°C — near TJ MAX. Risk of thermal throttling.\nFrequency: {1} MHz ({2}% of max)"),
                    temp.ToString("F0"), freqMHz.ToString("F0"), (ratio * 100).ToString("F0"));
                return (ThrottleReason.Thermal, LanguageService.S("Throttle", "ThermalNearLabel", "Thermal (near TJ)"), detail);
            }

            // hot, but the clock is at its normal level: no limiting
            if (!severe && ratio >= 0.50f && !stuckUnderLoad)
            {
                _lowFreqUnderLoadSince = null;
                return (ThrottleReason.None, "OK", "");
            }

            if (lowLoad) { _lowFreqUnderLoadSince = null; return (ThrottleReason.None, "OK", ""); }

            // Battery Saver / on battery
            if (batterySaver || (!pluggedIn && ratio < 0.40f))
            {
                string detail = pluggedIn
                    ? LanguageService.S("Throttle", "BatterySaverPluggedDetail",
                        "Windows Battery Saver is active — limiting the processor.\nDisable Battery Saver from Settings → System → Power.")
                    : LanguageService.S("Throttle", "BatterySaverUnpluggedDetail",
                        "On battery, and a restrictive power plan is limiting the frequency.\nPlug in the charger or switch to Performance mode.");
                return (ThrottleReason.BatterySaver, LanguageService.S("Throttle", "BatterySaverLabel", "Battery Saver"), detail);
            }

            // Power Limit PL1/PL2 — reached here only with real load (>=55%)
            if (!severe && ratio < 0.50f)
            {
                _lowFreqUnderLoadSince ??= now;
                double sustainedSec = (now - _lowFreqUnderLoadSince.Value).TotalSeconds;
                if (sustainedSec < SustainedThrottleSeconds)
                    return (ThrottleReason.None, "OK", "");

                string detail = string.Format(
                    LanguageService.S("Throttle", "PowerLimitDetail",
                        "CPU limited to {0}% of max frequency, under real load ({1}% load), for {2}s.\nPower Limit (PL1/PL2) set by BIOS or Windows.\nNormal under sustained workload — no action needed."),
                    (ratio * 100).ToString("F0"), loadPct.ToString("F0"), sustainedSec.ToString("F0"));
                return (ThrottleReason.PowerLimit, LanguageService.S("Throttle", "PowerLimitLabel", "Power Limit"), detail);
            }

            if (stuckUnderLoad)
            {
                string detail = string.Format(
                    LanguageService.S("Throttle", "FixedFreqDetail",
                        "CPU locked at {0} MHz ({1}% of the observed maximum), with no variation, under real load ({2}% load) for at least {3}s.\nTurbo never kicks in — Power Limit (PL1/PL2) or thermal throttling that doesn't drop below the usual 50% threshold.\nAfter cooling down, the frequency should become variable again."),
                    freqMHz.ToString("F0"), (ratio * 100).ToString("F0"), loadPct.ToString("F0"), StuckWindowSeconds.ToString());
                return (ThrottleReason.FixedFrequency, LanguageService.S("Throttle", "FixedFreqLabel", "Fixed frequency (turbo locked)"), detail);
            }
            // Not on the Power Limit branch (either the ratio is fine, or we already left above) — reset the timer, so the next
            // episode starts from zero, not from an old leftover.
            _lowFreqUnderLoadSince = null;

            // Severe but without temperature + on battery
            if (severe && !pluggedIn)
            {
                string detail = string.Format(
                    LanguageService.S("Throttle", "BatteryLimitedDetail",
                        "Severe frequency drop ({0} MHz) on battery.\nBattery Saver or an aggressive power plan.\nPlug in the charger."),
                    freqMHz.ToString("F0"));
                return (ThrottleReason.BatterySaver, LanguageService.S("Throttle", "BatteryLimitedLabel", "Battery limited"), detail);
            }

            // Fallback sever
            if (severe)
            {
                string detail = string.Format(
                    LanguageService.S("Throttle", "UnknownDetail",
                        "Frequency: {0} MHz ({1}% of max)\nUnknown cause. Possibly VR Thermal or external PROCHOT."),
                    freqMHz.ToString("F0"), (ratio * 100).ToString("F0"));
                return (ThrottleReason.Unknown, LanguageService.S("Throttle", "UnknownLabel", "Throttle (unknown)"), detail);
            }

            return (ThrottleReason.None, "OK", "");
        }

        public void Dispose()
        {
            _pcFreqPct?.Dispose();
            _pcFreqMHz?.Dispose();
            _pcPerfPct?.Dispose();
        }
    }
}
