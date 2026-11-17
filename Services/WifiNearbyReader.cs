using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SMDWin.Services
{
    /// <summary>A Wi-Fi network seen by the adapter (one row per access point).</summary>
    public sealed record NearbyWifi(string Ssid, string Bssid, int SignalPct, int Channel)
    {
        /// <summary>The signal in dBm, by the Windows formula (percent = 2 × (dBm + 100)).</summary>
        public int Dbm => SignalPct / 2 - 100;
        public string Band => Channel >= 1 && Channel <= 14 ? "2.4 GHz" : Channel > 14 ? "5 GHz" : "";
    }

    /// <summary>
    /// Nearby networks, read with <c>netsh wlan show networks mode=bssid</c>. Windows rebuilds the list by itself every few
    /// tens of seconds, so the values do not change on every read. The netsh labels depend on the Windows language, so they
    /// are recognised by their shape (SSID n / BSSID n / a value with "%" / a label containing "chan", "canal" or "kanal").
    /// </summary>
    public static class WifiNearbyReader
    {
        public static List<NearbyWifi> Read()
        {
            var list = new List<NearbyWifi>();
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show networks mode=bssid")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return list;
                string text = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch (Exception ex) { AppLogger.Debug($"WifiNearbyReader kill: {ex.Message}"); } }

                string ssid = "", bssid = ""; int signal = -1, channel = 0;
                void Flush()
                {
                    if (bssid.Length > 0 && signal >= 0)
                        list.Add(new NearbyWifi(string.IsNullOrWhiteSpace(ssid) ? "(hidden)" : ssid, bssid, signal, channel));
                    bssid = ""; signal = -1; channel = 0;
                }
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = line[..colon].Trim(), val = line[(colon + 1)..].Trim();
                    if (key.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) { Flush(); bssid = val; continue; }
                    if (key.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)) { Flush(); ssid = val; continue; }
                    if (bssid.Length == 0) continue;
                    if (val.EndsWith('%') && int.TryParse(val.TrimEnd('%').Trim(), out int pct)) { signal = pct; continue; }
                    string k = key.ToLowerInvariant();
                    if ((k.Contains("chan") || k.Contains("canal") || k.Contains("kanal")) && int.TryParse(val, out int ch)) channel = ch;
                }
                Flush();
            }
            catch (Exception ex) { AppLogger.Debug($"WifiNearbyReader: {ex.Message}"); }
            return list;
        }
    }
}
