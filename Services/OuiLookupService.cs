using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    public static class OuiLookupService
    {
        private static Dictionary<string, string>? _table;
        private static readonly object _lock = new();

        private static Dictionary<string, string> GetTable()
        {
            if (_table != null) return _table;
            lock (_lock)
            {
                if (_table != null) return _table;
                var table = new Dictionary<string, string>(32000, StringComparer.Ordinal);
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string? resName = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith("oui_database.csv", StringComparison.OrdinalIgnoreCase));
                    if (resName != null)
                    {
                        using var rs = asm.GetManifestResourceStream(resName);
                        if (rs != null)
                        {
                            using var reader = new System.IO.StreamReader(rs, System.Text.Encoding.UTF8);
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                int comma = line.IndexOf(',');
                                if (comma != 6) continue; // "XXXXXX,Name" — the OUI is always exactly 6 hex characters
                                string oui = line[..6];
                                string name = line[(comma + 1)..];
                                table[oui] = name;
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"OuiLookupService.GetTable: {ex.Message}"); }
                _table = table;
                AppLogger.Info($"OuiLookupService: {table.Count} OUI entries loaded");
                return table;
            }
        }

        /// <summary>Looks up the manufacturer by MAC address (any format — with ":", "-", or no separators). Returns null if not
        /// found (NOT an empty string / "—"), so the caller decides clearly how to show a missing value.</summary>
        public static string? Lookup(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return null;
            string clean = mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
            if (clean.Length < 6) return null;
            string oui = clean[..6];
            // Locally administered MAC (the 2nd bit of the first byte = 1) — addresses set by hand or randomized (common on modern
            // phones, for privacy) — meaningless in an OUI database, the real manufacturer is hidden on purpose.
            if (clean.Length >= 2)
            {
                int firstByte = Convert.ToInt32(clean[..2], 16);
                if ((firstByte & 0x02) != 0) return "(randomized/private MAC — vendor hidden by design)";
            }
            return GetTable().TryGetValue(oui, out var name) ? name : null;
        }
    }
}
