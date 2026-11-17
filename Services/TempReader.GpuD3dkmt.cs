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
        // layout per d3dkmthk.h (missing or reordered fields let the driver write past the buffer)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct D3DKMT_ADAPTER_PERFDATA
        {
            public uint PhysicalAdapterIndex;
            public ulong MemoryFrequency;          // Hz
            public ulong MaxMemoryFrequency;
            public ulong MaxMemoryFrequencyOC;
            public ulong MemoryBandwidth;
            public ulong PCIEBandwidth;
            public uint FanRPM;
            public uint Power;                     // a driver-dependent unit — unused
            public uint Temperature;               // tenths of °C
            public byte PowerStateOverride;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public uint Type;                      // KMTQAITYPE_ADAPTERPERFDATA = 70
            public System.IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO QueryAdapterInfo);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct D3DKMT_OPENADAPTERFROMHDC { public System.IntPtr hDc; public uint hAdapter; public long AdapterLuid; public uint VidPnSourceId; }  // LUID = 8 bytes (not a 16-byte Guid)

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC pData);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref uint hAdapter);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern System.IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, System.IntPtr lpInitData);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteDC(System.IntPtr hdc);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern System.IntPtr GetDC(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(System.IntPtr hWnd, System.IntPtr hDC);

        private uint _d3dAdapterHandle = 0;
        private bool _d3dAdapterOpened = false;

        private void TryGpuD3DKMT(TempSnapshot snap)
        {
            try
            {
                // Step 1: open the adapter handle (only once)
                if (!_d3dAdapterOpened)
                {
                    // DC on a single display (not the whole screen)
                    var hdc = CreateDC("\\\\.\\DISPLAY1", null, null, System.IntPtr.Zero);
                    bool ownDc = hdc != System.IntPtr.Zero;
                    if (!ownDc) hdc = GetDC(System.IntPtr.Zero);
                    if (hdc == System.IntPtr.Zero) { _d3dGpuPermFailed = true; return; }

                    var openData = new D3DKMT_OPENADAPTERFROMHDC { hDc = hdc };
                    int result = D3DKMTOpenAdapterFromHdc(ref openData);
                    if (ownDc) DeleteDC(hdc); else ReleaseDC(System.IntPtr.Zero, hdc);

                    if (result != 0)
                    {
                        AppLogger.Debug($"D3DKMT OpenAdapter failed: 0x{result:X8} — falling back to LHM");
                        _d3dGpuPermFailed = true;
                        return;
                    }
                    _d3dAdapterHandle = openData.hAdapter;
                    _d3dAdapterOpened = true;
                }

                // Step 2: read the performance data
                var perfData = new D3DKMT_ADAPTER_PERFDATA
                    { PhysicalAdapterIndex = 0 };

                // Marshal: no /unsafe needed — the struct is written to unmanaged memory
                int structSize = System.Runtime.InteropServices.Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
                System.IntPtr pPerf = System.Runtime.InteropServices.Marshal.AllocHGlobal(structSize);
                try
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(perfData, pPerf, false);
                    var queryInfo = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter              = _d3dAdapterHandle,
                        Type                  = 70, // KMTQAITYPE_ADAPTERPERFDATA
                        pPrivateDriverData    = pPerf,
                        PrivateDriverDataSize = (uint)structSize
                    };
                    int qResult = D3DKMTQueryAdapterInfo(ref queryInfo);
                    if (qResult == 0)
                        perfData = System.Runtime.InteropServices.Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(pPerf);
                    else return;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(pPerf);
                }

                // Step 3: convert and populate the snapshot
                float tempC = perfData.Temperature / 10.0f;
                if (tempC > 10f && tempC < 120f)
                {
                    snap.GpuTemp       = tempC;
                    snap.GpuSensorName = "D3DKMT";
                    snap.Backend       = "d3dkmt+" + (snap.Backend == "none" ? "" : snap.Backend);
                }

                // Power is not read from here: the unit differs by driver, and a wrong value is worse than none.

                // Fan RPM
                if (perfData.FanRPM > 0 && perfData.FanRPM < 20000)
                    snap.FanSpeeds.Add(("GPU Fan (D3DKMT)", (float)perfData.FanRPM));
            }
            catch (System.EntryPointNotFoundException)
            {
                // gdi32.dll has no D3DKMT on old systems (pre-Win8)
                _d3dGpuPermFailed = true;
                AppLogger.Debug("D3DKMT not available (pre-Win8 system)");
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"D3DKMT error: {ex.Message}");
                // Not marked PermFailed — it may be a transient error
            }
        }

        private void CloseD3DAdapter()
        {
            if (_d3dAdapterOpened && _d3dAdapterHandle != 0)
            {
                try { D3DKMTCloseAdapter(ref _d3dAdapterHandle); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                _d3dAdapterHandle = 0;
                _d3dAdapterOpened = false;
            }
        }
    }
}
