
using System.IO;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>One telemetry sample for an Intel GPU, per read cycle.</summary>
    public sealed class IgclGpuSample
    {
        public string Name        { get; set; } = "Intel GPU";
        public bool   IsIntegrated{ get; set; }
        public float? TempC       { get; set; }   // GPU core temp (telemetry or sysman)
        public float? VramTempC   { get; set; }   // VRAM temp (Arc)
        public float? LoadPct     { get; set; }   // global GPU utilization (Δ counter)
        public float? FreqMHz     { get; set; }   // current GPU core frequency
        public float? VramFreqMHz { get; set; }   // VRAM frequency
        public float? PowerW      { get; set; }   // putere medie GPU (Δ energie)
        public bool   ThermalThrottle { get; set; } // gpuTemperatureLimited
        public List<(string Name, float Rpm)> Fans { get; } = new();
    }

    public static class IgclService
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlInitFn(ref CtlInitArgs args, out nint apiHandle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlCloseFn(nint apiHandle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlEnumerateDevicesFn(nint apiHandle, ref uint count, [Out] nint[]? devices);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlGetDevicePropertiesFn(nint device, ref CtlDeviceAdapterProperties props);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlPowerTelemetryGetFn(nint device, ref CtlPowerTelemetry telemetry);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlEnumTemperatureSensorsFn(nint device, ref uint count, [Out] nint[]? sensors);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlTemperatureGetPropertiesFn(nint sensor, ref CtlTempProperties props);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CtlTemperatureGetStateFn(nint sensor, out double temperature);

        private static CtlInitFn?                     _ctlInit;
        private static CtlCloseFn?                    _ctlClose;
        private static CtlEnumerateDevicesFn?         _ctlEnumerateDevices;
        private static CtlGetDevicePropertiesFn?      _ctlGetDeviceProperties;
        private static CtlPowerTelemetryGetFn?        _ctlPowerTelemetryGet;
        private static CtlEnumTemperatureSensorsFn?   _ctlEnumTempSensors;
        private static CtlTemperatureGetPropertiesFn? _ctlTempGetProps;
        private static CtlTemperatureGetStateFn?      _ctlTempGetState;

        // ── Constants from igcl_api.h ───────────────────────────────────────────
        private const int  CTL_RESULT_SUCCESS = 0;
        // CTL_MAKE_VERSION(1,0) = (1 << 16) | 0. 1.0 is used (not the current CTL_IMPL_VERSION 1.1) for compatibility with
        // older Intel drivers — the driver answers with SupportedVersion in args anyway.
        private const uint CTL_APP_VERSION_1_0 = (1u << 16) | 0u;
        private const uint CTL_DEVICE_TYPE_GRAPHICS = 1;
        private const uint CTL_ADAPTER_FLAG_INTEGRATED = 1u; // CTL_BIT(0)
        private const uint INTEL_PCI_VENDOR_ID = 0x8086;
        // ctl_temp_sensors_t
        private const uint CTL_TEMP_SENSORS_GLOBAL = 0;
        private const uint CTL_TEMP_SENSORS_GPU    = 1;
        // ctl_data_type_t (the numeric subset used by telemetry)
        private const uint DT_INT8 = 0, DT_UINT8 = 1, DT_INT16 = 2, DT_UINT16 = 3,
                           DT_INT32 = 4, DT_UINT32 = 5, DT_INT64 = 6, DT_UINT64 = 7,
                           DT_FLOAT = 8, DT_DOUBLE = 9;

        // ── Blittable structures — the same layout as the C header (x64) ─────────

        /// <summary>ctl_init_args_t — 36 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct CtlInitArgs
        {
            public uint Size;             // @0
            public byte Version;          // @4 (+3 pad)
            public uint AppVersion;       // @8   ctl_version_info_t
            public uint Flags;            // @12  ctl_init_flags_t
            public uint SupportedVersion; // @16  [out]
            // ctl_application_id_t (GUID-like, 16 bytes) @20 — all 0 = default ID
            public uint  UidData1;
            public ushort UidData2;
            public ushort UidData3;
            public fixed byte UidData4[8];
        }

        /// <summary>ctl_oc_telemetry_item_t — 24 bytes (bool C = 1 byte → byte).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlTelemetryItem
        {
            public byte  BSupported;  // @0 (+3 pad)
            public uint  Units;       // @4
            public uint  Type;        // @8 (+4 pad — the 8-byte union is 8-aligned)
            public ulong Value;       // @16  ctl_data_value_t (union), decoded by Type
        }

        /// <summary>ctl_psu_info_t — 56 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlPsuInfo
        {
            public byte  BSupported;  // @0 (+3 pad)
            public uint  PsuType;     // @4
            public CtlTelemetryItem EnergyCounter; // @8
            public CtlTelemetryItem Voltage;       // @32
        }

        /// <summary>
        /// ctl_power_telemetry_t version 0 — 808 bytes. The V>0 fields (gpuVrTemp, gpuEffectiveClock, etc.) are NOT included:
        /// with Version=0 the driver writes only the V0 area and checks Size≥808, so the structure is complete and safe on
        /// every driver that exports telemetry.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlPowerTelemetry
        {
            public uint Size;    // @0
            public byte Version; // @4 (+3 pad — the first item is 8-aligned)
            public CtlTelemetryItem TimeStamp;                    // @8   seconds (double), 1 ms resolution
            public CtlTelemetryItem GpuEnergyCounter;             // @32  J — power = ΔE/Δt
            public CtlTelemetryItem GpuVoltage;                   // @56
            public CtlTelemetryItem GpuCurrentClockFrequency;     // @80  MHz
            public CtlTelemetryItem GpuCurrentTemperature;        // @104 °C (max dintre senzori)
            public CtlTelemetryItem GlobalActivityCounter;        // @128 secunde busy — load = Δ/Δt
            public CtlTelemetryItem RenderComputeActivityCounter; // @152
            public CtlTelemetryItem MediaActivityCounter;         // @176
            public byte GpuPowerLimited;                          // @200
            public byte GpuTemperatureLimited;                    // @201
            public byte GpuCurrentLimited;                        // @202
            public byte GpuVoltageLimited;                        // @203
            public byte GpuUtilizationLimited;                    // @204 (+3 pad)
            public CtlTelemetryItem VramEnergyCounter;            // @208
            public CtlTelemetryItem VramVoltage;                  // @232
            public CtlTelemetryItem VramCurrentClockFrequency;    // @256
            public CtlTelemetryItem VramCurrentEffectiveFrequency;// @280
            public CtlTelemetryItem VramReadBandwidthCounter;     // @304
            public CtlTelemetryItem VramWriteBandwidthCounter;    // @328
            public CtlTelemetryItem VramCurrentTemperature;       // @352
            public byte VramPowerLimited;                         // @376 (deprecated, always false)
            public byte VramTemperatureLimited;
            public byte VramCurrentLimited;
            public byte VramVoltageLimited;
            public byte VramUtilizationLimited;                   // @380 (+3 pad)
            public CtlTelemetryItem TotalCardEnergyCounter;       // @384
            public CtlPsuInfo Psu0;                               // @408 (CTL_PSU_COUNT = 5)
            public CtlPsuInfo Psu1;
            public CtlPsuInfo Psu2;
            public CtlPsuInfo Psu3;
            public CtlPsuInfo Psu4;
            public CtlTelemetryItem Fan0;                         // @688 (CTL_FAN_COUNT = 5)
            public CtlTelemetryItem Fan1;
            public CtlTelemetryItem Fan2;
            public CtlTelemetryItem Fan3;
            public CtlTelemetryItem Fan4;                         // → 808 total
        }

        /// <summary>ctl_device_adapter_properties_t — 320 bytes on x64 (Version=0).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct CtlDeviceAdapterProperties
        {
            public uint Size;               // @0
            public byte Version;            // @4 (+3 pad)
            public nint PDeviceID;          // @8   [in] buffer alocat de apelant (LUID, 8 bytes)
            public uint DeviceIdSize;       // @16
            public uint DeviceType;         // @20  ctl_device_type_t
            public uint SupportedSubfunctionFlags; // @24 (+4 pad)
            public ulong DriverVersion;     // @32
            public ulong FwMajor;           // @40  ctl_firmware_version_t (3 × u64)
            public ulong FwMinor;
            public ulong FwBuild;
            public uint PciVendorId;        // @64
            public uint PciDeviceId;        // @68
            public uint RevId;              // @72
            public uint NumEusPerSubSlice;  // @76
            public uint NumSubSlicesPerSlice;// @80
            public uint NumSlices;          // @84
            public fixed byte Name[100];    // @88  CTL_MAX_DEVICE_NAME_LEN
            public uint GraphicsAdapterProperties; // @188  bit0 = INTEGRATED
            public uint Frequency;          // @192 (V>0)
            public ushort PciSubsysId;      // @196 (V>1)
            public ushort PciSubsysVendorId;// @198
            public byte BdfBus;             // @200 ctl_adapter_bdf_t (V>1)
            public byte BdfDevice;
            public byte BdfFunction;        // @202 (+1 pad)
            public uint NumXeCores;         // @204 (V>2)
            public fixed byte Reserved[108];// @208  CTL_MAX_RESERVED_SIZE → 316 (+4 pad) = 320
        }

        /// <summary>ctl_temp_properties_t — 24 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlTempProperties
        {
            public uint   Size;           // @0
            public byte   Version;        // @4 (+3 pad)
            public uint   Type;           // @8  ctl_temp_sensors_t (+4 pad)
            public double MaxTemperature; // @16
        }

        // ── Stare per device ───────────────────────────────────────────────────
        private sealed class Device
        {
            public nint   Handle;
            public string Name = "Intel GPU";
            public bool   IsIntegrated;
            // sysman temperature sensors (enumerated once at init): (handle, type) — GLOBAL preferred, then GPU.
            public List<(nint Handle, uint Type)> TempSensors = new();
            // Previous values for the monotonic counters (Δ-based):
            public double PrevTimeStamp   = double.NaN; // secunde
            public double PrevGlobalAct   = double.NaN; // secunde busy
            public double PrevRenderAct   = double.NaN;
            public double PrevGpuEnergy   = double.NaN; // J
            public bool   TelemetryUnsupported;         // driver with a working ctlPowerTelemetryGet
        }

        private static readonly object _lock = new();
        private static bool _probed;              // the availability probe already ran
        private static bool _available;           // ControlLib.dll + ctlInit + ≥1 GPU Intel
        private static nint _lib;
        private static nint _apiHandle;
        private static readonly List<Device> _devices = new();

        /// <summary>True if IGCL is initialized and there is at least one Intel GPU.</summary>
        public static bool IsAvailable { get { lock (_lock) { EnsureInit(); return _available; } } }

        private static void EnsureInit()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                // 1) Load ControlLib.dll — first the standard search (System32, the app folder), then DriverStore, where DCH drivers
                //    always install it (they do not always copy it into System32).
                if (!NativeLibrary.TryLoad("ControlLib.dll", out _lib))
                {
                    string? found = FindControlLibInDriverStore();
                    if (found == null || !NativeLibrary.TryLoad(found, out _lib))
                    {
                        AppLogger.Info("IGCL: ControlLib.dll not available (no Intel Graphics driver on the system) — Intel sensors disabled");
                        return;
                    }
                    AppLogger.Info($"IGCL: ControlLib.dll loaded from DriverStore: {found}");
                }

                // 2) Resolve the real entry points (ctl*, __cdecl).
                if (!TryGetExport("ctlInit",                     out _ctlInit) ||
                    !TryGetExport("ctlClose",                    out _ctlClose) ||
                    !TryGetExport("ctlEnumerateDevices",         out _ctlEnumerateDevices) ||
                    !TryGetExport("ctlGetDeviceProperties",      out _ctlGetDeviceProperties) ||
                    !TryGetExport("ctlPowerTelemetryGet",        out _ctlPowerTelemetryGet))
                {
                    AppLogger.Warning(new Exception("IGCL: ctl* export missing from ControlLib.dll (very old driver?)"), "IgclService");
                    return;
                }
                // sysman sensors are optional — they do not exist on some drivers.
                TryGetExport("ctlEnumTemperatureSensors",  out _ctlEnumTempSensors);
                TryGetExport("ctlTemperatureGetProperties", out _ctlTempGetProps);
                TryGetExport("ctlTemperatureGetState",      out _ctlTempGetState);

                // Layout sanity check — if a future refactor breaks the marshaling, it shows in the log at once, not as corrupted readings.
                int szTel = Marshal.SizeOf<CtlPowerTelemetry>();
                int szIni = Marshal.SizeOf<CtlInitArgs>();
                if (szTel != 808 || szIni != 36)
                {
                    string telStatus = szTel != 808 ? $"telemetry={szTel} (expected 808)" : "telemetry=OK";
                    string iniStatus = szIni != 36  ? $"init={szIni} (expected 36)"       : "init=OK";
                    AppLogger.Warning(new Exception($"IGCL: layout structuri gresit — {telStatus}, {iniStatus} — abandon"), "IgclService");
                    return;
                }

                // 3) ctlInit — AppVersion 1.0, UID 0 (default), flags 0.
                var args = new CtlInitArgs
                {
                    Size       = (uint)szIni,
                    Version    = 0,
                    AppVersion = CTL_APP_VERSION_1_0,
                    Flags      = 0,
                };
                int hr = _ctlInit!(ref args, out _apiHandle);
                if (hr != CTL_RESULT_SUCCESS || _apiHandle == 0)
                {
                    AppLogger.Info($"IGCL: ctlInit failed (0x{hr:X8}) — Intel sensors disabled");
                    return;
                }

                // 4) Enumerate the adapters (the two-call idiom: count, then array).
                uint count = 0;
                hr = _ctlEnumerateDevices!(_apiHandle, ref count, null);
                if (hr != CTL_RESULT_SUCCESS || count == 0)
                {
                    AppLogger.Info($"IGCL: no adapters (hr=0x{hr:X8}, count={count})");
                    ShutdownLocked();
                    return;
                }
                var handles = new nint[count];
                hr = _ctlEnumerateDevices!(_apiHandle, ref count, handles);
                if (hr != CTL_RESULT_SUCCESS)
                {
                    AppLogger.Info($"IGCL: ctlEnumerateDevices failed (0x{hr:X8})");
                    ShutdownLocked();
                    return;
                }

                // 5) Per-adapter properties — keep only Intel GPUs.
                for (int i = 0; i < count; i++)
                    ProbeDevice(handles[i]);

                if (_devices.Count == 0)
                {
                    AppLogger.Info("IGCL: no Intel GPU reported by the driver");
                    ShutdownLocked();
                    return;
                }

                _available = true;
                foreach (var d in _devices)
                    AppLogger.Info($"IGCL: active — {d.Name} ({(d.IsIntegrated ? "integrated" : "discrete")}, {d.TempSensors.Count} sysman temp sensors)");

                // Priming read: fills the Prev* counters (timestamp, activity, energy) so the FIRST real read from TempReader already
                // has Load% and Power W, not only from the second one (Δ-based).
                foreach (var d in _devices)
                {
                    var prime = new IgclGpuSample();
                    try { ReadDevice(d, prime); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* priming is not critical */ }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "IgclService.EnsureInit");
                ShutdownLocked();
            }
        }

        private static unsafe void ProbeDevice(nint handle)
        {
            // pDeviceID is [in]: the caller MUST allocate the buffer (LUID = 8 bytes), exactly as in Intel's official samples —
            // otherwise the driver returns an error.
            nint luidBuf = Marshal.AllocHGlobal(8);
            try
            {
                var props = new CtlDeviceAdapterProperties
                {
                    Size         = (uint)Marshal.SizeOf<CtlDeviceAdapterProperties>(),
                    Version      = 0,
                    PDeviceID    = luidBuf,
                    DeviceIdSize = 8,
                };
                int hr = _ctlGetDeviceProperties!(handle, ref props);
                if (hr != CTL_RESULT_SUCCESS) { AppLogger.Debug($"IGCL: GetDeviceProperties 0x{hr:X8} — sar adaptorul"); return; }
                if (props.DeviceType != CTL_DEVICE_TYPE_GRAPHICS) return;
                if (props.PciVendorId != INTEL_PCI_VENDOR_ID) return;

                var dev = new Device
                {
                    Handle       = handle,
                    IsIntegrated = (props.GraphicsAdapterProperties & CTL_ADAPTER_FLAG_INTEGRATED) != 0,
                };
                // The name: char[100] ASCII, NUL-terminated.
                byte* p = props.Name;
                int len = 0; while (len < 100 && p[len] != 0) len++;
                string name = System.Text.Encoding.ASCII.GetString(p, len).Trim();
                if (!string.IsNullOrEmpty(name)) dev.Name = name;

                EnumTempSensors(dev);
                _devices.Add(dev);
            }
            finally { Marshal.FreeHGlobal(luidBuf); }
        }

        private static void EnumTempSensors(Device dev)
        {
            if (_ctlEnumTempSensors == null || _ctlTempGetProps == null || _ctlTempGetState == null) return;
            try
            {
                uint n = 0;
                if (_ctlEnumTempSensors(dev.Handle, ref n, null) != CTL_RESULT_SUCCESS || n == 0) return;
                var hs = new nint[n];
                if (_ctlEnumTempSensors(dev.Handle, ref n, hs) != CTL_RESULT_SUCCESS) return;
                for (int i = 0; i < n; i++)
                {
                    var tp = new CtlTempProperties { Size = (uint)Marshal.SizeOf<CtlTempProperties>(), Version = 0 };
                    uint type = _ctlTempGetProps(hs[i], ref tp) == CTL_RESULT_SUCCESS ? tp.Type : CTL_TEMP_SENSORS_GLOBAL;
                    dev.TempSensors.Add((hs[i], type));
                }
            }
            catch (Exception ex) { AppLogger.Debug("IGCL EnumTempSensors: " + ex.Message); }
        }

        // ── Read — called by TempReader on every cycle (~1 s) ─────────────
        /// <summary>
        /// Reads telemetry for all Intel GPUs. Cheap after init (one ctlPowerTelemetryGet per device). Empty list if IGCL
        /// is not available. Load% and Power W appear from the SECOND read (Δ counters).
        /// </summary>
        public static IReadOnlyList<IgclGpuSample> ReadAll()
        {
            lock (_lock)
            {
                EnsureInit();
                if (!_available) return Array.Empty<IgclGpuSample>();

                var result = new List<IgclGpuSample>(_devices.Count);
                foreach (var dev in _devices)
                {
                    var s = new IgclGpuSample { Name = dev.Name, IsIntegrated = dev.IsIntegrated };
                    try { ReadDevice(dev, s); }
                    catch (Exception ex) { AppLogger.Debug($"IGCL read {dev.Name}: {ex.Message}"); }
                    result.Add(s);
                }
                return result;
            }
        }

        private static void ReadDevice(Device dev, IgclGpuSample s)
        {
            if (!dev.TelemetryUnsupported)
            {
                var t = new CtlPowerTelemetry { Size = 808, Version = 0 };
                int hr = _ctlPowerTelemetryGet!(dev.Handle, ref t);
                if (hr == CTL_RESULT_SUCCESS)
                {
                    // Temperatures + frequencies: instantaneous values, straight from the item.
                    double? v;
                    if ((v = ItemToDouble(t.GpuCurrentTemperature)) is > 0 and < 120) s.TempC       = (float)v;
                    if ((v = ItemToDouble(t.VramCurrentTemperature)) is > 0 and < 120) s.VramTempC  = (float)v;
                    if ((v = ItemToDouble(t.GpuCurrentClockFrequency)) is > 0)         s.FreqMHz    = (float)v;
                    if ((v = ItemToDouble(t.VramCurrentClockFrequency)) is > 0)        s.VramFreqMHz= (float)v;
                    s.ThermalThrottle = t.GpuTemperatureLimited != 0;

                    // Fans (discrete cards): one item per fan, RPM.
                    AddFan(s, dev.Name, 0, t.Fan0); AddFan(s, dev.Name, 1, t.Fan1);
                    AddFan(s, dev.Name, 2, t.Fan2); AddFan(s, dev.Name, 3, t.Fan3);
                    AddFan(s, dev.Name, 4, t.Fan4);

                    // Monotonic counters → Δ from the previous read.
                    double? now = ItemToDouble(t.TimeStamp);
                    if (now.HasValue)
                    {
                        double dt = now.Value - dev.PrevTimeStamp; // NaN on the first read
                        if (dt > 0.05 && dt < 60)
                        {
                            // Load%: the global counter is preferred; fallback render/compute (some iGPUs support only that one).
                            double? act = ItemToDouble(t.GlobalActivityCounter);
                            double  prevAct = dev.PrevGlobalAct;
                            if (!act.HasValue)
                            {
                                act = ItemToDouble(t.RenderComputeActivityCounter);
                                prevAct = dev.PrevRenderAct;
                            }
                            if (act.HasValue && !double.IsNaN(prevAct))
                            {
                                double pct = (act.Value - prevAct) / dt * 100.0;
                                if (pct >= 0) s.LoadPct = (float)Math.Min(100.0, pct);
                            }

                            // Putere: ΔEnergie(J) / Δt(s) = W.
                            double? e = ItemToDouble(t.GpuEnergyCounter);
                            if (e.HasValue && !double.IsNaN(dev.PrevGpuEnergy))
                            {
                                double w = (e.Value - dev.PrevGpuEnergy) / dt;
                                if (w is >= 0 and < 1000) s.PowerW = (float)w;
                            }
                        }
                        dev.PrevTimeStamp = now.Value;
                        dev.PrevGlobalAct = ItemToDouble(t.GlobalActivityCounter)        ?? double.NaN;
                        dev.PrevRenderAct = ItemToDouble(t.RenderComputeActivityCounter) ?? double.NaN;
                        dev.PrevGpuEnergy = ItemToDouble(t.GpuEnergyCounter)             ?? double.NaN;
                    }
                }
                else if (hr == 0x40000027) // CTL_RESULT_ERROR_DEVICE_UNAVAILABLE (D3/TDR)
                {
                    // GPU asleep — not a fault; retry on the next cycle.
                    AppLogger.Debug($"IGCL: {dev.Name} temporarily unavailable (D3)");
                }
                else
                {
                    // Telemetry is not implemented on this driver/GPU — stop trying on every cycle; the sysman sensors stay active.
                    dev.TelemetryUnsupported = true;
                    AppLogger.Info($"IGCL: telemetry not supported on {dev.Name} (0x{hr:X8}) — using only the sysman sensors");
                }
            }

            // Temperature fallback: the sysman sensors (GLOBAL, then GPU).
            if (!s.TempC.HasValue && dev.TempSensors.Count > 0 && _ctlTempGetState != null)
            {
                foreach (uint wanted in new[] { CTL_TEMP_SENSORS_GLOBAL, CTL_TEMP_SENSORS_GPU })
                {
                    foreach (var (h, type) in dev.TempSensors)
                    {
                        if (type != wanted) continue;
                        if (_ctlTempGetState(h, out double temp) == CTL_RESULT_SUCCESS && temp > 0 && temp < 120)
                        { s.TempC = (float)temp; break; }
                    }
                    if (s.TempC.HasValue) break;
                }
            }
        }

        private static void AddFan(IgclGpuSample s, string devName, int idx, in CtlTelemetryItem fan)
        {
            double? rpm = ItemToDouble(fan);
            if (rpm is > 0 and < 20000)
                s.Fans.Add(($"{devName} Fan #{idx + 1}", (float)rpm.Value));
        }

        /// <summary>Decodes the ctl_data_value_t union by ctl_data_type_t.</summary>
        private static double? ItemToDouble(in CtlTelemetryItem item)
        {
            if (item.BSupported == 0) return null;
            ulong raw = item.Value;
            return item.Type switch
            {
                DT_INT8   => (sbyte)raw,
                DT_UINT8  => (byte)raw,
                DT_INT16  => (short)raw,
                DT_UINT16 => (ushort)raw,
                DT_INT32  => (int)raw,
                DT_UINT32 => (uint)raw,
                DT_INT64  => (long)raw,
                DT_UINT64 => raw,
                DT_FLOAT  => BitConverter.Int32BitsToSingle(unchecked((int)(raw & 0xFFFFFFFF))),
                DT_DOUBLE => BitConverter.Int64BitsToDouble(unchecked((long)raw)),
                _         => null,
            };
        }

        private static string? FindControlLibInDriverStore()
        {
            try
            {
                string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                           @"System32\DriverStore\FileRepository");
                if (!Directory.Exists(repo)) return null;
                // Intel Graphics DCH drivers: iigd_dch.inf_amd64_*, iigd_dch_d.inf_amd64_* (the "d" variant on some OEMs).
                // The newest folder that contains the DLL is used.
                return Directory.EnumerateDirectories(repo, "iigd*")
                                .Select(d => Path.Combine(d, "ControlLib.dll"))
                                .Where(File.Exists)
                                .OrderByDescending(File.GetLastWriteTimeUtc)
                                .FirstOrDefault();
            }
            catch (Exception ex) { AppLogger.Debug("IGCL DriverStore scan: " + ex.Message); return null; }
        }

        private static bool TryGetExport<T>(string name, out T? fn) where T : Delegate
        {
            fn = null;
            if (_lib == 0) return false;
            if (!NativeLibrary.TryGetExport(_lib, name, out nint addr) || addr == 0) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(addr);
            return true;
        }

        /// <summary>Closes the IGCL context — called from TempReader.Dispose().</summary>
        public static void Shutdown()
        {
            lock (_lock) ShutdownLocked();
        }

        private static void ShutdownLocked()
        {
            try
            {
                if (_apiHandle != 0 && _ctlClose != null) { try { _ctlClose(_apiHandle); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                _apiHandle = 0;
                _devices.Clear();
                _available = false;
                // _lib is not unloaded (NativeLibrary.Free) — the driver may hold internal threads; the process releases it on exit anyway.
            }
            catch (Exception ex) { AppLogger.Debug("IGCL Shutdown: " + ex.Message); }
        }
    }
}
