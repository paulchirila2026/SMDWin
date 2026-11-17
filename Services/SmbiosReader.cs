// SMDWin — Services/SmbiosReader.cs
// Reads hardware info directly from the SMBIOS firmware tables, without WMI.
// Instant (0 ms) vs WMI (2-3 s timeout on some systems).
// Pattern: System Informer → plugins/ExtendedTools/smbios.c

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class SmbiosMemorySlot
    {
        public int    SlotIndex       { get; set; }
        public int    SizeMB          { get; set; }    // 0 = slot gol
        public string Type            { get; set; } = ""; // DDR4, DDR3, etc.
        public int    SpeedMHz        { get; set; }
        public string Manufacturer    { get; set; } = "";
        public string PartNumber      { get; set; } = "";
        public string SerialNumber    { get; set; } = "";
        public string BankLocator     { get; set; } = "";
        public string DeviceLocator   { get; set; } = "";
        public bool   IsPopulated     => SizeMB > 0;
        public string DisplaySize     => SizeMB >= 1024 ? $"{SizeMB/1024} GB" : $"{SizeMB} MB";
    }

    public class SmbiosInfo
    {
        // BIOS (Type 0)
        public string BiosVendor      { get; set; } = "";
        public string BiosVersion     { get; set; } = "";
        public string BiosDate        { get; set; } = "";

        // Baseboard (Type 2)
        public string BoardManufacturer { get; set; } = "";
        public string BoardProduct    { get; set; } = "";
        public string BoardSerial     { get; set; } = "";
        public string BoardVersion    { get; set; } = "";

        // System (Type 1)
        public string SystemManufacturer { get; set; } = "";
        public string SystemProduct   { get; set; } = "";
        public string SystemVersion   { get; set; } = "";
        public string SystemSerial    { get; set; } = "";  // SMBIOS Type 1, offset 7

        // Processor (Type 4)
        public string CpuSocket       { get; set; } = "";
        public string CpuManufacturer { get; set; } = "";
        public int    CpuMaxSpeedMHz  { get; set; }
        public int    CpuCoreCount    { get; set; }
        public int    CpuThreadCount  { get; set; }

        // Memory (Type 17) — one entry per slot
        public List<SmbiosMemorySlot> MemorySlots { get; set; } = new();

        // Physical Memory Array (Type 16) — maximum capacity reported by the motherboard and the number of slots. NOTE: many BIOSes
        // report a lower value than the real limit (e.g. 8 GB on boards that take 16 GB) — shown as "reported by the board".
        public double MemoryArrayMaxGB  { get; set; }
        public int    MemoryArrayDevices { get; set; }

        // Derived
        public int TotalMemoryMB      => MemorySlots.Sum(s => s.SizeMB);
        public int PopulatedSlots     => MemorySlots.Count(s => s.IsPopulated);
        public int TotalSlots         => MemorySlots.Count;
        public bool IsAvailable       { get; set; }
    }

    public static class SmbiosReader
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        private const int SystemFirmwareTableInformation = 76;
        private const uint RSMB = 0x52534D42; // 'RSMB' provider

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_FIRMWARE_TABLE_INFORMATION
        {
            public uint ProviderSignature;  // 'RSMB'
            public uint Action;             // 0 = read
            public uint TableID;            // 0 for SMBIOS
            public uint TableBufferLength;
            // followed by variable-length buffer
        }

        private static SmbiosInfo? _cached;

        public static SmbiosInfo Read()
        {
            if (_cached != null) return _cached;

            var result = new SmbiosInfo();
            try
            {
                byte[]? raw = GetRawSmbios();
                if (raw == null || raw.Length < 8)
                {
                    result.IsAvailable = false;
                    return result;
                }

                // Raw SMBIOS buffer: 4 bytes unused, 2 bytes major, 2 bytes minor, then tables
                // Format: [4 bytes reserved][2 bytes major][2 bytes minor][tables...]
                int offset = 8; // skip SMBIOS header
                ParseTables(raw, offset, result);
                result.IsAvailable = true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SmbiosReader: {ex.Message}");
                result.IsAvailable = false;
            }

            _cached = result;
            return result;
        }

        private static bool _wmiSerialRequested = false;

        public static void RefreshSystemSerialFromWmiAsync()
        {
            if (_cached == null || _wmiSerialRequested) return;
            _wmiSerialRequested = true;

            Task.Run(() =>
            {
                try
                {
                    using var searcher = SMDWin.Services.WmiHelper.Searcher("SELECT SerialNumber FROM Win32_BIOS", System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (ManagementObject mo in __wmiTmp1)
                    {
                        string? wmiSerial = mo["SerialNumber"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(wmiSerial) && _cached != null)
                            _cached.SystemSerial = wmiSerial;
                        break;
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"SmbiosReader.WmiSerial: {ex.Message}"); }
            });
        }

        private static byte[]? GetRawSmbios()
        {
            // Step 1: get required size
            int headerSize = Marshal.SizeOf<SYSTEM_FIRMWARE_TABLE_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(headerSize + 4);
            try
            {
                var header = new SYSTEM_FIRMWARE_TABLE_INFORMATION
                {
                    ProviderSignature = RSMB,
                    Action            = 0,
                    TableID           = 0,
                    TableBufferLength = 0
                };
                Marshal.StructureToPtr(header, buf, false);

                int status = NtQuerySystemInformation(
                    SystemFirmwareTableInformation, buf, headerSize, out int needed);

                // STATUS_BUFFER_TOO_SMALL (0xC0000023) → needed has correct size
                if (status != 0 && status != unchecked((int)0xC0000023))
                    return null;

                int totalSize = needed > 0 ? needed : headerSize + 65536;
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(totalSize);

                header.TableBufferLength = (uint)(totalSize - headerSize);
                Marshal.StructureToPtr(header, buf, false);

                status = NtQuerySystemInformation(
                    SystemFirmwareTableInformation, buf, totalSize, out _);

                if (status != 0) return null;

                // Read TableBufferLength from response
                uint dataLen = (uint)Marshal.ReadInt32(buf, 12); // offset of TableBufferLength
                if (dataLen == 0 || dataLen > 1_000_000) return null;

                byte[] data = new byte[dataLen];
                Marshal.Copy(buf + headerSize, data, 0, (int)dataLen);
                return data;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        private static void ParseTables(byte[] data, int start, SmbiosInfo info)
        {
            int pos = start;
            int slotIndex = 0;

            while (pos < data.Length - 4)
            {
                if (pos + 4 > data.Length) break;
                byte type   = data[pos];
                byte length = data[pos + 1];

                if (type == 127) break; // End-of-Table marker

                if (length < 4 || pos + length > data.Length) break;

                // Find strings section (after fixed-size struct)
                int stringsStart = pos + length;
                int stringsEnd   = stringsStart;

                // Strings section: null-terminated strings, terminated by double null
                while (stringsEnd < data.Length - 1)
                {
                    if (data[stringsEnd] == 0 && data[stringsEnd + 1] == 0)
                    { stringsEnd += 2; break; }
                    stringsEnd++;
                }
                if (stringsEnd > data.Length) stringsEnd = data.Length;

                // Extract string by 1-based index
                string GetStr(int idx)
                {
                    if (idx <= 0) return "";
                    int cur = stringsStart, count = 1;
                    while (cur < stringsEnd)
                    {
                        int end = cur;
                        while (end < stringsEnd && data[end] != 0) end++;
                        if (count == idx)
                            return Encoding.ASCII.GetString(data, cur, end - cur).Trim();
                        cur = end + 1; count++;
                    }
                    return "";
                }

                int Get16(int off) => off + 1 < data.Length
                    ? data[pos + off] | (data[pos + off + 1] << 8) : 0;
                int Get32(int off) => off + 3 < data.Length
                    ? data[pos+off] | (data[pos+off+1]<<8) | (data[pos+off+2]<<16) | (data[pos+off+3]<<24) : 0;

                switch (type)
                {
                    case 0: // BIOS
                        if (length >= 9)
                        {
                            info.BiosVendor  = GetStr(data[pos + 4]);
                            info.BiosVersion = GetStr(data[pos + 5]);
                            info.BiosDate    = GetStr(data[pos + 8]);
                        }
                        break;

                    case 1: // System
                        if (length >= 8)
                        {
                            info.SystemManufacturer = GetStr(data[pos + 4]);
                            info.SystemProduct      = GetStr(data[pos + 5]);
                            info.SystemVersion      = GetStr(data[pos + 6]);
                            info.SystemSerial       = GetStr(data[pos + 7]); // Serial Number
                        }
                        break;

                    case 2: // Baseboard
                        if (length >= 8)
                        {
                            info.BoardManufacturer = GetStr(data[pos + 4]);
                            info.BoardProduct      = GetStr(data[pos + 5]);
                            info.BoardVersion      = GetStr(data[pos + 6]);
                            info.BoardSerial       = GetStr(data[pos + 7]);
                        }
                        break;

                    case 4: // Processor
                        if (length >= 28)
                        {
                            info.CpuSocket       = GetStr(data[pos + 4]);
                            info.CpuManufacturer = GetStr(data[pos + 7]);
                            info.CpuMaxSpeedMHz  = Get16(20);
                            if (length >= 46)
                            {
                                info.CpuCoreCount   = data[pos + 35];
                                info.CpuThreadCount = data[pos + 37];
                            }
                        }
                        break;

                    case 16: // Physical Memory Array
                        if (length >= 15)
                        {
                            int use = data[pos + 5];            // 0x03 = System memory
                            if (use == 0x03 || use == 0x00)
                            {
                                long maxKB = (uint)Get32(7);
                                if (maxKB == 0x80000000L && length >= 0x17)
                                {
                                    // Extended capacity (bytes, QWORD at offset 0x0F)
                                    long lo = (uint)Get32(0x0F), hi = (uint)Get32(0x13);
                                    double bytes = lo + hi * 4294967296.0;
                                    info.MemoryArrayMaxGB += bytes / 1073741824.0;
                                }
                                else if (maxKB > 0 && maxKB != 0x80000000L)
                                    info.MemoryArrayMaxGB += maxKB / 1048576.0;
                                info.MemoryArrayDevices += Get16(0x0D);
                            }
                        }
                        break;

                    case 17: // Memory Device
                        if (length >= 15)
                        {
                            var slot = new SmbiosMemorySlot { SlotIndex = slotIndex++ };

                            int sizeBits = Get16(12);
                            if (sizeBits == 0x7FFF && length >= 30)
                                slot.SizeMB = Get32(28); // extended size
                            else if (sizeBits > 0 && sizeBits != 0xFFFF)
                                slot.SizeMB = (sizeBits & 0x8000) != 0
                                    ? (sizeBits & 0x7FFF)         // KB units
                                    : (sizeBits & 0x7FFF) * 1;   // MB units (default)

                            if (length >= 17)
                            {
                                int memType = data[pos + 18];
                                slot.Type = memType switch
                                {
                                    0x1A => "DDR4",
                                    0x18 => "DDR3",
                                    0x17 => "DDR2",
                                    0x12 => "DDR",
                                    0x22 => "DDR5",
                                    0x0F => "SDRAM",
                                    _ => $"Type {memType:X2}"
                                };
                                slot.SpeedMHz = Get16(21);
                                // Some firmware (e.g. Apple Boot Camp) reports type 0x12 "DDR" for DDR3 modules. If the type does not match the speed,
                                // it is estimated from the speed (the same rule as HardwareService.GetMemoryType).
                                int sp = slot.SpeedMHz;
                                bool mismatch = (slot.Type == "DDR" && sp > 400) || (slot.Type == "DDR2" && sp > 1066)
                                             || (slot.Type == "DDR3" && sp > 2400);
                                if (mismatch || slot.Type.StartsWith("Type "))
                                    slot.Type = sp >= 4000 ? "DDR5" : sp >= 2400 ? "DDR4" : sp >= 800 ? "DDR3" : sp >= 400 ? "DDR2" : slot.Type;
                            }

                            if (length >= 28)
                            {
                                slot.DeviceLocator   = GetStr(data[pos + 16]);
                                slot.BankLocator     = GetStr(data[pos + 17]);
                                slot.Manufacturer    = GetStr(data[pos + 23]);
                                slot.SerialNumber    = GetStr(data[pos + 24]);
                                slot.PartNumber      = GetStr(data[pos + 26]);
                            }

                            info.MemorySlots.Add(slot);
                        }
                        break;
                }

                pos = stringsEnd;
            }
        }

        // Helper LINQ extensions for SmbiosInfo
        private static int Sum(this List<SmbiosMemorySlot> list, Func<SmbiosMemorySlot, int> sel)
        {
            int s = 0; foreach (var x in list) s += sel(x); return s;
        }
        private static int Count(this List<SmbiosMemorySlot> list, Func<SmbiosMemorySlot, bool> pred)
        {
            int c = 0; foreach (var x in list) if (pred(x)) c++; return c;
        }
    }
}
