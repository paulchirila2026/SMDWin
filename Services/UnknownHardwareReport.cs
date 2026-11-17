using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// "Report unknown hardware": collects the components the internal database (Data/HardwareDB) does NOT recognise —
    /// CPU, GPU (with PCI IDs), disks — plus the RAM module part numbers, in a JSON saved next to the exe
    /// (unknown-hardware\PC-data.json). The files collected from repaired PCs are merged into the database from time to time.
    /// Contains no personal data (no user name, no license keys).
    /// </summary>
    public static class UnknownHardwareReport
    {
        public sealed record Result(string? Path, int UnknownCount, int TotalChecked);

        private static IEnumerable<ManagementObject> Query(string wql)
        {
            using var s = SMDWin.Services.WmiHelper.Searcher(wql, System.TimeSpan.FromSeconds(10));
            foreach (ManagementObject o in s.Get()) yield return o;
        }

        private static string Str(ManagementObject o, string p) { try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; } }

        public static Result Create()
        {
            var unknown = new List<Dictionary<string, string>>();
            var ram = new List<Dictionary<string, string>>();
            int checkedCount = 0;

            foreach (var o in Query("SELECT Name, Manufacturer, ProcessorId FROM Win32_Processor"))
            {
                checkedCount++;
                string name = Str(o, "Name");
                if (name.Length > 0 && HardwareDatabaseService.LookupCpu(name) == null)
                    unknown.Add(new() { ["type"] = "cpu", ["name"] = name, ["manufacturer"] = Str(o, "Manufacturer"), ["cpuid"] = Str(o, "ProcessorId") });
            }
            foreach (var o in Query("SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion FROM Win32_VideoController"))
            {
                string name = Str(o, "Name");
                if (name.Length == 0 || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                checkedCount++;
                if (HardwareDatabaseService.LookupGpu(name) == null)
                    unknown.Add(new() { ["type"] = "gpu", ["name"] = name, ["pci_id"] = Str(o, "PNPDeviceID"), ["driver"] = Str(o, "DriverVersion") });
            }
            foreach (var o in Query("SELECT Model, InterfaceType, Size, FirmwareRevision FROM Win32_DiskDrive"))
            {
                string model = Str(o, "Model");
                if (model.Length == 0) continue;
                checkedCount++;
                if (HardwareDatabaseService.LookupStorage(model) == null)
                    unknown.Add(new() { ["type"] = "storage", ["model"] = model, ["interface"] = Str(o, "InterfaceType"),
                                        ["size_bytes"] = Str(o, "Size"), ["firmware"] = Str(o, "FirmwareRevision") });
            }
            foreach (var o in Query("SELECT Manufacturer, PartNumber, Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                ram.Add(new() { ["manufacturer"] = Str(o, "Manufacturer"), ["part_number"] = Str(o, "PartNumber"),
                                ["capacity_bytes"] = Str(o, "Capacity"), ["speed"] = Str(o, "Speed"), ["smbios_type"] = Str(o, "SMBIOSMemoryType") });

            if (unknown.Count == 0 && ram.Count == 0) return new Result(null, 0, checkedCount);

            string dir = AppPaths.Data("unknown-hardware");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.json");
            var doc = new
            {
                created = DateTime.Now.ToString("s"),
                app_version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                unknown,
                ram_modules = ram,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            AppLogger.Info("UnknownHardwareReport: {0} unknown of {1}, saved {2}", unknown.Count, checkedCount, file);
            return new Result(file, unknown.Count, checkedCount);
        }
    }
}
