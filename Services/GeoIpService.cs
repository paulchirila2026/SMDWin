using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;

namespace SMDWin.Services
{
    /// <summary>
    /// The country of a public IPv4 address, offline — nothing is sent anywhere.
    /// Data: DB-IP Lite "IP to Country" (CC BY 4.0, https://db-ip.com), packed into Assets/geo-country-v4.bin by
    /// _private/scripts/build-geo-country.py (about 1 MB, embedded in the exe). Loaded on the first lookup.
    /// Format (gzip): "SMDGEO1\0", uint32 LE entry count, byte country count, 2 ASCII bytes per country, then the entries
    /// sorted by start address: uint32 BE start + 1 byte country index (255 = not assigned). An address belongs to the last
    /// entry whose start is not above it.
    /// </summary>
    public static class GeoIpService
    {
        public const string Attribution = "IP to country data by DB-IP (db-ip.com), CC BY 4.0";

        private static readonly object _lock = new();
        private static uint[]? _starts;
        private static byte[]? _country;
        private static string[] _codes = Array.Empty<string>();
        private static bool _loadFailed;

        /// <summary>The two-letter country code (e.g. "DE"), or null for private, local or unknown addresses.</summary>
        public static string? CountryCode(IPAddress ip)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            var b = ip.GetAddressBytes();
            if (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] >= 224) return null;
            if (!EnsureLoaded()) return null;
            uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            int i = Array.BinarySearch(_starts!, v);
            if (i < 0) i = ~i - 1;
            if (i < 0) return null;
            byte c = _country![i];
            return c == 255 || c >= _codes.Length ? null : _codes[c];
        }

        /// <summary>"Germany (DE)" for an "address:port" endpoint; "" when there is nothing to show.</summary>
        public static string CountryDisplay(string endpoint)
        {
            try
            {
                string host = endpoint;
                int colon = endpoint.LastIndexOf(':');
                if (colon > 0) host = endpoint[..colon];
                if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return "";
                string? cc = CountryCode(ip);
                if (cc == null) return "";
                try { return $"{new System.Globalization.RegionInfo(cc).EnglishName} ({cc})"; }
                catch { return cc; }   // codes .NET does not know (e.g. EU, AP, ZZ)
            }
            catch { return ""; }
        }

        private static bool EnsureLoaded()
        {
            if (_starts != null) return true;
            if (_loadFailed) return false;
            lock (_lock)
            {
                if (_starts != null) return true;
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("geo-country-v4.bin", StringComparison.OrdinalIgnoreCase));
                    if (res == null) { _loadFailed = true; AppLogger.Warning("GeoIpService: geo-country-v4.bin is not embedded"); return false; }
                    using var rs = asm.GetManifestResourceStream(res)!;
                    using var gz = new GZipStream(rs, CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    gz.CopyTo(ms);
                    var d = ms.GetBuffer();
                    int len = (int)ms.Length;
                    if (len < 13 || System.Text.Encoding.ASCII.GetString(d, 0, 7) != "SMDGEO1") throw new InvalidDataException("bad header");
                    int count = BitConverter.ToInt32(d, 8);
                    int nCodes = d[12];
                    var codes = new string[nCodes];
                    int p = 13;
                    for (int i = 0; i < nCodes; i++, p += 2) codes[i] = System.Text.Encoding.ASCII.GetString(d, p, 2);
                    if (p + count * 5L > len) throw new InvalidDataException("truncated");
                    var starts = new uint[count];
                    var country = new byte[count];
                    for (int i = 0; i < count; i++, p += 5)
                    {
                        starts[i] = ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];
                        country[i] = d[p + 4];
                    }
                    _codes = codes; _country = country; _starts = starts;
                    AppLogger.Info($"GeoIpService: {count} ranges, {nCodes} countries loaded");
                    return true;
                }
                catch (Exception ex)
                {
                    _loadFailed = true;
                    AppLogger.Warning(ex, "GeoIpService: loading the country data failed");
                    return false;
                }
            }
        }
    }
}
