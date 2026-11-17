using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    public static class CpuTopologyService
    {
        public enum CoreKind
        {
            /// <summary>Non-hybrid CPU (all cores in the same efficiency class) or API unavailable — the UI must not label anything.</summary>
            Unknown,
            Performance,
            Efficiency
        }

        private const int RelationProcessorCore  = 0;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, IntPtr buffer, ref uint returnedLength);

        private static List<CoreKind>? _cache;
        private static bool _queried = false;

        /// <summary>Type per physical core, in "CPU Core #1, #2, #3..." order — index 0 = Core #1 in LHM. Empty list if the API
        /// failed or the buffer could not be parsed — the UI then shows no labels, and must NOT throw.</summary>
        public static IReadOnlyList<CoreKind> GetPerCoreKinds()
        {
            if (_queried) return (IReadOnlyList<CoreKind>?)_cache ?? Array.Empty<CoreKind>();
            _queried = true;
            try
            {
                _cache = Query();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"CpuTopologyService: query failed — {ex.Message}");
                _cache = new List<CoreKind>();
            }
            return _cache;
        }

        private static List<CoreKind> Query()
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            int err1 = Marshal.GetLastWin32Error();
            if (len == 0 || err1 != ERROR_INSUFFICIENT_BUFFER)
            {
                AppLogger.Debug($"CpuTopologyService: the first GetLogicalProcessorInformationEx call did not ask for a buffer (err={err1}, len={len}) — API not available on this system, P/E labeling disabled.");
                return new List<CoreKind>();
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                bool ok = GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len);
                if (!ok)
                {
                    AppLogger.Debug($"CpuTopologyService: the second call failed (err={Marshal.GetLastWin32Error()}).");
                    return new List<CoreKind>();
                }

                // The buffer holds a sequence of SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX structures,
                // each: DWORD Relationship (4) + DWORD Size (4) + a union specific to the relationship.
                // For RelationProcessorCore the union is PROCESSOR_RELATIONSHIP:
                //   BYTE Flags(1) + BYTE EfficiencyClass(1) + BYTE Reserved[20] + WORD GroupCount(2)
                //   then an array of GROUP_AFFINITY (16 bytes each on x64:
                //     UInt64 Mask(8) + UInt16 Group(2) + UInt16 Reserved[3](6)).
                // Offsets (relative to the start of the entry):
                //   0:  Relationship (int)
                //   4:  Size (int)
                //   8:  Flags (byte)
                //   9:  EfficiencyClass (byte)
                //   10: Reserved[20]
                //   30: GroupCount (ushort)
                //   32: first GROUP_AFFINITY (already 8-byte aligned)
                var entries = new List<(int EfficiencyClass, long SortKey)>();
                long offset = 0;
                int guard = 0;
                while (offset < len && guard++ < 4096) // guard against an endless loop on an unexpected buffer
                {
                    IntPtr entryPtr = IntPtr.Add(buffer, (int)offset);
                    int relationship = Marshal.ReadInt32(entryPtr, 0);
                    int size = Marshal.ReadInt32(entryPtr, 4);
                    if (size <= 0) break; // unexpected format — better to stop than to read garbage

                    if (relationship == RelationProcessorCore)
                    {
                        byte efficiencyClass = Marshal.ReadByte(entryPtr, 9);
                        ushort groupCount = unchecked((ushort)Marshal.ReadInt16(entryPtr, 30));

                        long lowestGlobalLogicalProc = long.MaxValue;
                        for (int g = 0; g < groupCount && (32 + g * 16 + 16) <= size; g++)
                        {
                            int gaOffset = 32 + g * 16;
                            long mask = Marshal.ReadInt64(entryPtr, gaOffset);
                            ushort groupNum = unchecked((ushort)Marshal.ReadInt16(entryPtr, gaOffset + 8));
                            if (mask != 0)
                            {
                                int lowestBit = BitOperations.TrailingZeroCount(unchecked((ulong)mask));
                                long globalIdx = (long)groupNum * 64 + lowestBit;
                                if (globalIdx < lowestGlobalLogicalProc) lowestGlobalLogicalProc = globalIdx;
                            }
                        }

                        if (lowestGlobalLogicalProc != long.MaxValue)
                            entries.Add((efficiencyClass, lowestGlobalLogicalProc));
                    }

                    offset += size;
                }

                if (entries.Count == 0)
                {
                    AppLogger.Debug("CpuTopologyService: 0 RelationProcessorCore entries parsed from the buffer — P/E labeling disabled.");
                    return new List<CoreKind>();
                }

                entries.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
                int maxClass = entries.Max(e => e.EfficiencyClass);
                int minClass = entries.Min(e => e.EfficiencyClass);

                var result = new List<CoreKind>(entries.Count);
                foreach (var e in entries)
                {
                    if (minClass == maxClass)
                        result.Add(CoreKind.Unknown); // non-hybrid CPU — nothing to label
                    else
                        result.Add(e.EfficiencyClass == maxClass ? CoreKind.Performance : CoreKind.Efficiency);
                }

                if (minClass == maxClass)
                    AppLogger.Info($"CpuTopologyService: {entries.Count} physical cores, all in the same efficiency class ({minClass}) — non-hybrid CPU, no P/E labels.");
                else
                    AppLogger.Info($"CpuTopologyService: {entries.Count} core-uri fizice, clase eficienta {minClass}..{maxClass} — " +
                        $"{result.Count(k => k == CoreKind.Performance)} P-core, {result.Count(k => k == CoreKind.Efficiency)} E-core " +
                        "(order assumed to match \"CPU Core #1, #2, ...\" in LHM — check against Task Manager).");

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
