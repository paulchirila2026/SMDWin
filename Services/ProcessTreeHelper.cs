using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    internal static class ProcessTreeHelper
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Returns PID → parent PID for all processes visible at this moment.
        /// Best effort — if the native call fails (very rare), returns an empty dictionary instead of throwing, so the rest of
        /// the process snapshot is not spoiled.
        /// </summary>
        public static Dictionary<int, int> GetParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = IntPtr.Zero;
            try
            {
                snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;

                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return map;
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32Next(snap, ref entry));
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"ProcessTreeHelper: GetParentMap failed ({ex.Message}) — parent info not available in this tick.");
            }
            finally
            {
                if (snap != IntPtr.Zero) CloseHandle(snap);
            }
            return map;
        }
    }
}
