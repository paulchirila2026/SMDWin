using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>
    /// Per-process numbers measured the way Task Manager measures them.
    ///
    /// Memory: Task Manager's "Memory" column is the private working set — the RAM only that process uses. Process.WorkingSet64
    /// also counts pages shared with other processes (system DLLs, fonts), so it shows roughly twice as much. The private
    /// working set of every process comes from one NtQuerySystemInformation(SystemProcessInformation) call, cheap enough for
    /// every refresh.
    ///
    /// I/O: GetProcessIoCounters counts every read and write a process makes — files, but also network sockets, pipes and
    /// devices. It is not the same as disk activity (a camera program reading video over the network shows tens of MB/s here
    /// while the disk is idle), so the app labels it "I/O".
    /// </summary>
    internal static class ProcessMemoryInfo
    {
        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// The full path of the process's executable, or null. Asks Windows for the path directly with the lowest access right,
        /// so it is instant and also works for most protected processes — Process.MainModule enumerates the process's modules
        /// instead, which took up to 800 ms for explorer or TextInputHost.
        /// </summary>
        public static string? GetImagePath(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        private static int _bufferSize = 1 << 20;

        /// <summary>PID → private working set in bytes, for every process. Empty if the call fails.</summary>
        public static Dictionary<int, long> GetPrivateWorkingSets()
        {
            var result = new Dictionary<int, long>(512);
            // SYSTEM_PROCESS_INFORMATION: NextEntryOffset (ULONG) at 0, WorkingSetPrivateSize (LARGE_INTEGER) at 8,
            // UniqueProcessId (HANDLE) at 0x50 on 64-bit, 0x44 on 32-bit.
            int pidOffset = IntPtr.Size == 8 ? 0x50 : 0x44;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int size = _bufferSize;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        _bufferSize = Math.Max(size * 2, needed + (64 << 10));
                        continue;
                    }
                    if (status != 0) return result;

                    int offset = 0;
                    while (offset >= 0 && offset < size)
                    {
                        IntPtr entry = IntPtr.Add(buf, offset);
                        long privateWs = Marshal.ReadInt64(entry, 0x08);
                        int pid = (int)Marshal.ReadIntPtr(entry, pidOffset).ToInt64();
                        if (pid != 0) result[pid] = privateWs;
                        int next = Marshal.ReadInt32(entry, 0);
                        if (next <= 0) break;
                        offset += next;
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"ProcessMemoryInfo.GetPrivateWorkingSets: {ex.Message}");
                    return result;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return result;
        }

        /// <summary>The private working set of one process in bytes, or the full working set if it cannot be read.</summary>
        public static long GetPrivateWorkingSet(Process p)
        {
            var all = GetPrivateWorkingSets();
            if (all.TryGetValue(p.Id, out long v) && v > 0) return v;
            try { return p.WorkingSet64; } catch { return 0; }
        }

        /// <summary>Total bytes read and written by the process so far (files, network, pipes, devices).</summary>
        public static bool TryGetIo(Process p, out ulong readBytes, out ulong writeBytes)
        {
            readBytes = writeBytes = 0;
            try
            {
                if (!GetProcessIoCounters(p.Handle, out var io)) return false;
                readBytes = io.ReadTransferCount;
                writeBytes = io.WriteTransferCount;
                return true;
            }
            catch { return false; }   // access denied for protected processes, or the process has ended
        }
    }
}
