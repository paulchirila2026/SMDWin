using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    public class CpuSpec
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("model_token")] public string ModelToken { get; set; } = "";
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("series")] public string Series { get; set; } = "";
        [JsonPropertyName("cores")] public int? Cores { get; set; }
        [JsonPropertyName("threads")] public int? Threads { get; set; }
        [JsonPropertyName("base_clock_ghz")] public double? BaseClockGhz { get; set; }
        [JsonPropertyName("boost_clock_ghz")] public double? BoostClockGhz { get; set; }
        [JsonPropertyName("l2_cache_mb")] public double? L2CacheMb { get; set; }
        [JsonPropertyName("l3_cache_mb")] public double? L3CacheMb { get; set; }
        [JsonPropertyName("tdp_w")] public double? TdpW { get; set; }
        [JsonPropertyName("socket")] public string Socket { get; set; } = "";
        [JsonPropertyName("microarchitecture")] public string Microarchitecture { get; set; } = "";
        [JsonPropertyName("lithography")] public string Lithography { get; set; } = "";
        [JsonPropertyName("igpu_model")] public string IgpuModel { get; set; } = "";
        [JsonPropertyName("memory_types")] public List<string> MemoryTypes { get; set; } = new();
        [JsonPropertyName("memory_channels")] public int? MemoryChannels { get; set; }
        [JsonPropertyName("release_year")] public int? ReleaseYear { get; set; }
        [JsonPropertyName("tjmax_c")] public double? TjMaxC { get; set; }
    }

    public class GpuSpec
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("model_token")] public string ModelToken { get; set; } = "";
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("chipset_manufacturer")] public string ChipsetManufacturer { get; set; } = "";
        [JsonPropertyName("chipset")] public string Chipset { get; set; } = "";
        [JsonPropertyName("series")] public string Series { get; set; } = "";
        [JsonPropertyName("memory_gb")] public double? MemoryGb { get; set; }
        [JsonPropertyName("memory_type")] public string MemoryType { get; set; } = "";
        [JsonPropertyName("core_base_clock_mhz")] public double? CoreBaseClockMhz { get; set; }
        [JsonPropertyName("core_boost_clock_mhz")] public double? CoreBoostClockMhz { get; set; }
        [JsonPropertyName("memory_clock_mhz")] public double? MemoryClockMhz { get; set; }
        [JsonPropertyName("interface")] public string Interface { get; set; } = "";
        [JsonPropertyName("tdp_w")] public double? TdpW { get; set; }
        [JsonPropertyName("length_mm")] public double? LengthMm { get; set; }
        [JsonPropertyName("slot_width")] public double? SlotWidth { get; set; }
    }

    public class RamSpec
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("series")] public string Series { get; set; } = "";
        [JsonPropertyName("ram_type")] public string RamType { get; set; } = "";
        [JsonPropertyName("speed_mhz")] public double? SpeedMhz { get; set; }
        [JsonPropertyName("capacity_gb")] public double? CapacityGb { get; set; }
        [JsonPropertyName("modules_qty")] public int? ModulesQty { get; set; }
        [JsonPropertyName("module_capacity_gb")] public double? ModuleCapacityGb { get; set; }
        [JsonPropertyName("cas_latency")] public double? CasLatency { get; set; }
        [JsonPropertyName("ecc")] public string Ecc { get; set; } = "";
        [JsonPropertyName("form_factor")] public string FormFactor { get; set; } = "";
    }

    public class StorageSpec
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("series")] public string Series { get; set; } = "";
        [JsonPropertyName("capacity_gb")] public double? CapacityGb { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "";              // "SSD" / "HDD"
        [JsonPropertyName("storage_type")] public string StorageType { get; set; } = "";
        [JsonPropertyName("nvme")] public bool Nvme { get; set; }
        [JsonPropertyName("interface")] public string Interface { get; set; } = "";
        [JsonPropertyName("form_factor")] public string FormFactor { get; set; } = "";
    }

    public static class HardwareDatabaseService
    {
        private static List<CpuSpec>? _cpu;
        private static List<GpuSpec>? _gpu;
        private static List<RamSpec>? _ram;
        private static List<StorageSpec>? _storage;
        private static readonly object _lock = new();

        public static int CpuCount => Cpu.Count;
        public static int GpuCount => Gpu.Count;
        public static int RamCount => Ram.Count;
        public static int StorageCount => Storage.Count;

        public static List<CpuSpec> Cpu
        {
            get { lock (_lock) { return _cpu ??= LoadOrEmpty<CpuSpec>("cpu.json"); } }
        }
        public static List<GpuSpec> Gpu
        {
            get { lock (_lock) { return _gpu ??= LoadOrEmpty<GpuSpec>("gpu.json"); } }
        }
        public static List<RamSpec> Ram
        {
            get { lock (_lock) { return _ram ??= LoadOrEmpty<RamSpec>("ram.json"); } }
        }
        public static List<StorageSpec> Storage
        {
            get { lock (_lock) { return _storage ??= LoadOrEmpty<StorageSpec>("storage.json"); } }
        }

        public static void Reload()
        {
            lock (_lock) { _cpu = null; _gpu = null; _ram = null; _storage = null; }
        }

        private static string? FindEmbeddedResourceName(string fileName)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string suffix = "." + fileName;
            foreach (var name in asm.GetManifestResourceNames())
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name;
            return null;
        }

        private static List<T> LoadOrEmpty<T>(string fileName)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = FindEmbeddedResourceName(fileName);
                if (resourceName == null)
                {
                    AppLogger.Warning($"HardwareDatabaseService: {fileName} not found as embedded resource — lookups will return null (honest 'no data', never guessed).");
                    return new List<T>();
                }
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    AppLogger.Warning($"HardwareDatabaseService: {fileName} resource name found ({resourceName}) but stream is null — lookups will return null.");
                    return new List<T>();
                }
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var json = reader.ReadToEnd();
                var opts = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                };
                using var doc = JsonDocument.Parse(json);
                var result = new List<T>();
                int skipped = 0;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var item = element.Deserialize<T>(opts);
                        if (item != null) result.Add(item);
                    }
                    catch (Exception exItem)
                    {
                        skipped++;
                        AppLogger.Debug($"HardwareDatabaseService.Load({fileName}): skipped 1 malformed entry — {exItem.Message}");
                    }
                }
                if (skipped > 0)
                    AppLogger.Warning($"HardwareDatabaseService.Load({fileName}): loaded {result.Count}, skipped {skipped} malformed entries");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"HardwareDatabaseService.Load({fileName})");
                return new List<T>();
            }
        }

        public static string NormalizeKey(string name) =>
            Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", "");

        /// <summary>Extracts a short model "token" (e.g. "I5-4570", "RYZEN 5 5600X", "RX 570") from a long raw name
        /// (reported by WMI or from the database) — used as a fallback match when the full key is not found.</summary>
        public static string ExtractModelToken(string name)
        {
            var m = Regex.Match(name, @"\b([iI][3579]-[0-9]{4,5}[A-Za-z]{0,3})\b");
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            m = Regex.Match(name, @"\b(Ryzen\s+(?:3|5|7|9|Threadripper)\s*[A-Za-z]?\s*[0-9]{3,5}[A-Za-z]{0,3})\b", RegexOptions.IgnoreCase);
            if (m.Success) return Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", " ");
            m = Regex.Match(name, @"\b((?:RTX|GTX|RX|GT|Arc)\s*[0-9]{3,4}\s*[A-Za-z0-9]{0,4})\b", RegexOptions.IgnoreCase);
            if (m.Success) return Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", " ");
            return "";
        }

        /// <summary>Looks up a CPU by the name reported by WMI (e.g. "Intel(R) Core(TM) i5-4570 CPU @ 3.20GHz").
        /// Returns null if NOTHING is found — the caller must treat null as "no data", not as an error, and must NOT guess.</summary>
        public static CpuSpec? LookupCpu(string wmiName)
        {
            if (string.IsNullOrWhiteSpace(wmiName)) return null;
            var key = NormalizeKey(wmiName);
            var exact = Cpu.FirstOrDefault(c => c.Key == key);
            if (exact != null) return exact;

            var token = ExtractModelToken(wmiName);
            if (string.IsNullOrEmpty(token)) return null;
            var tokenNorm = NormalizeKey(token);
            return Cpu.FirstOrDefault(c => c.Key.Contains(tokenNorm) || NormalizeKey(c.ModelToken) == tokenNorm);
        }

        public static GpuSpec? LookupGpu(string wmiName)
        {
            if (string.IsNullOrWhiteSpace(wmiName)) return null;
            var key = NormalizeKey(wmiName);
            var exact = Gpu.FirstOrDefault(g => g.Key == key);
            if (exact != null) return exact;

            var token = ExtractModelToken(wmiName);
            if (string.IsNullOrEmpty(token)) return null;
            var tokenNorm = NormalizeKey(token);
            return Gpu.FirstOrDefault(g => g.Key.Contains(tokenNorm) || NormalizeKey(g.ModelToken) == tokenNorm);
        }

        /// <summary>Looks up a disk (SSD/HDD) by model — used as a 4th source of truth for SSD/NVMe/HDD detection (next to
        /// BusType, SpindleSpeed and the name heuristics in HardwareService.cs) — if the exact model is in the database, the
        /// answer is certain, not a guess.</summary>
        public static StorageSpec? LookupStorage(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            var key = NormalizeKey(model);
            var exact = Storage.FirstOrDefault(s => s.Key == key);
            if (exact != null) return exact;
            // Partial match — disk models reported by WMI often have the manufacturer and capacity glued to the model
            // ("KINGSTON SA400S37480G"), so a two-way "Contains" catches more real cases.
            return Storage.FirstOrDefault(s => key.Contains(s.Key) || s.Key.Contains(key));
        }
    }
}
