
using System;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    public sealed class NtDiskReader
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        private const int SystemPerformanceInformation = 2;
        private const int BUFFER_SIZE = 512; // structura reala ~312 bytes, dam suficient

        // Offseturi in SYSTEM_PERFORMANCE_INFORMATION:
        // 0:   IdleProcessTime        (LARGE_INTEGER, 8 bytes)
        // 8:   IoReadTransferCount    (LARGE_INTEGER, 8 bytes) ← bytes cititi
        // 16:  IoWriteTransferCount   (LARGE_INTEGER, 8 bytes) ← bytes scrisi
        // 24:  IoOtherTransferCount   (LARGE_INTEGER, 8 bytes)
        // 32:  IoReadOperationCount   (ULONG, 4 bytes)
        // 36:  IoWriteOperationCount  (ULONG, 4 bytes)
        private const int OFFSET_READ  = 8;
        private const int OFFSET_WRITE = 16;
        private const int OFFSET_READ_OPS  = 32;
        private const int OFFSET_WRITE_OPS = 36;

        private long     _prevRead      = -1;
        private long     _prevWrite     = -1;
        private uint     _prevReadOps   = 0;
        private uint     _prevWriteOps  = 0;
        private DateTime _prevTime      = DateTime.MinValue;

        public float LastReadKBs   { get; private set; }
        public float LastWriteKBs  { get; private set; }
        public float LastActivePct { get; private set; }
        public float LastAvgMs     { get; private set; }
        public bool  IsAvailable   { get; private set; } = true;

        public void Update()
        {
            if (!IsAvailable) return;

            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(BUFFER_SIZE);

                int status = NtQuerySystemInformation(
                    SystemPerformanceInformation, buf, BUFFER_SIZE, out int needed);

                // STATUS_SUCCESS = 0, STATUS_INFO_LENGTH_MISMATCH = 0xC0000004
                if (status != 0)
                {
                    AppLogger.Debug($"NtDiskReader: NtQuery status=0x{status:X8} needed={needed}");
                    IsAvailable = false;
                    return;
                }

                long curRead  = Marshal.ReadInt64(buf, OFFSET_READ);
                long curWrite = Marshal.ReadInt64(buf, OFFSET_WRITE);
                uint curROps  = (uint)Marshal.ReadInt32(buf, OFFSET_READ_OPS);
                uint curWOps  = (uint)Marshal.ReadInt32(buf, OFFSET_WRITE_OPS);

                var now = DateTime.UtcNow;

                if (_prevRead >= 0 && _prevTime != DateTime.MinValue)
                {
                    double elapsed = (now - _prevTime).TotalSeconds;
                    if (elapsed > 0.01)
                    {
                        long dr = Math.Max(0, curRead  - _prevRead);
                        long dw = Math.Max(0, curWrite - _prevWrite);

                        LastReadKBs  = (float)(dr / 1024.0 / elapsed);
                        LastWriteKBs = (float)(dw / 1024.0 / elapsed);

                        if (LastReadKBs  > 10_000_000f) LastReadKBs  = 0f;
                        if (LastWriteKBs > 10_000_000f) LastWriteKBs = 0f;

                        uint dOps = (curROps - _prevReadOps) + (curWOps - _prevWriteOps);
                        LastActivePct = Math.Clamp(dOps / (float)elapsed, 0f, 100f);
                    }
                }
                else
                {
                    LastReadKBs = 0f; LastWriteKBs = 0f;
                }

                _prevRead    = curRead;
                _prevWrite   = curWrite;
                _prevReadOps = curROps;
                _prevWriteOps= curWOps;
                _prevTime    = now;
            }
            catch (Exception ex)
            {
                _ = System.Threading.Tasks.Task.Run(
                    () => AppLogger.Warning(ex, "NtDiskReader.Update"));
                IsAvailable = false;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        public void Reset()
        {
            _prevRead  = -1;
            _prevTime  = DateTime.MinValue;
            IsAvailable = true;
        }
    }
}
