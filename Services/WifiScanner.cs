using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SMDWin.Services
{
    /// <summary>One access point (one radio of a router) as Windows sees it in the last scan, with what it announces about itself.</summary>
    public sealed class WifiAp
    {
        public string Ssid = "";          // "" = hidden network
        public string Bssid = "";         // "a4:2b:b0:1c:7e:10"
        public string Vendor = "";        // from the MAC prefix (OuiLookupService)
        public int FrequencyMHz;          // the primary 20 MHz channel's centre
        public string Band = "";          // "2.4" / "5" / "6"
        public int Channel;               // the primary channel
        public int WidthMHz = 20;         // 20 / 40 / 80 / 160 / 320
        public int LowMHz, HighMHz;       // the whole block the network uses
        public int Rssi;                  // dBm
        public int Quality;               // 0–100 (Windows)
        public string Standard = "";      // "Wi-Fi 4" … "Wi-Fi 7", "802.11a/b/g"
        public int Streams = 1;
        public int MaxRateMbps;
        public string Security = "";      // "WPA2", "WPA2/WPA3", "WPA3", "WPA2-Enterprise", "WPA2 (TKIP)", "WEP", "Open", "OWE"
        public bool WeakSecurity;         // WEP / TKIP / open
        public bool OpenNetwork;
        public string Pmf = "";           // "required" / "optional" / ""
        public bool Wps;
        public int? Clients;              // BSS Load, when announced
        public int? UtilizationPct;       // BSS Load, when announced
        public string Country = "";
        public DateTime FirstSeen, LastSeen;
        public bool Connected;
        public bool Hidden => Ssid.Length == 0;
        public bool IsDfs => Band == "5" && Channel >= 52 && Channel <= 144;
    }

    /// <summary>The result of one read: the access points, which one the PC is connected to, the adapter.</summary>
    public sealed class WifiScanResult
    {
        public List<WifiAp> Aps = new();
        public string ConnectedBssid = "";
        public string Adapter = "";
        public DateTime At = DateTime.Now;
        public bool AccessDenied;   // Windows refused (location access off for SMD Win)
        public bool NoAdapter;
    }

    /// <summary>
    /// Wi-Fi scan through the WLAN API (WlanGetNetworkBssList), not netsh: one entry per access point (a mesh with three units =
    /// three entries), language-independent, with the information elements each router broadcasts in its beacons — channel
    /// width (HT / VHT / HE / EHT operation), standard and spatial streams, security (RSN / WPA / WPS), BSS Load (clients and
    /// channel use, when the router sends it) and country. Windows 11 treats this as location data: it is read only on the
    /// Channel analysis tab, which the user opens.
    /// </summary>
    public static class WifiScanner
    {
        private static readonly Dictionary<string, DateTime> FirstSeen = new();

        /// <summary>Asks the adapter for a fresh scan (results are ready a few seconds later).</summary>
        public static void TriggerScan()
        {
            IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) return;
                int n = Marshal.ReadInt32(list), size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                for (int i = 0; i < n; i++)
                {
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * size);
                    Guid g = info.InterfaceGuid;
                    WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"WifiScanner.TriggerScan: {ex.Message}"); }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }

        public static WifiScanResult Read()
        {
            var r = new WifiScanResult();
            IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) { r.NoAdapter = true; return r; }
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) { r.NoAdapter = true; return r; }
                int n = Marshal.ReadInt32(list), size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                if (n == 0) { r.NoAdapter = true; return r; }
                var now = DateTime.Now;
                for (int i = 0; i < n; i++)
                {
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * size);
                    Guid g = info.InterfaceGuid;
                    if (r.Adapter.Length == 0) r.Adapter = info.strInterfaceDescription;
                    if (info.isState == 1) { r.Adapter = info.strInterfaceDescription; r.ConnectedBssid = ConnectedBssid(h, g); }
                    uint ret = WlanGetNetworkBssList(h, ref g, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out IntPtr bss);
                    if (ret == 5) { r.AccessDenied = true; continue; }
                    if (ret != 0 || bss == IntPtr.Zero) continue;
                    try
                    {
                        int count = Marshal.ReadInt32(bss, 4);
                        for (int k = 0; k < count; k++)
                        {
                            var ap = ParseEntry(bss + 8 + k * EntrySize);
                            if (ap == null || r.Aps.Any(x => x.Bssid == ap.Bssid)) continue;
                            if (!FirstSeen.TryGetValue(ap.Bssid, out var first)) FirstSeen[ap.Bssid] = first = now;
                            ap.FirstSeen = first; ap.LastSeen = now;
                            r.Aps.Add(ap);
                        }
                    }
                    finally { WlanFreeMemory(bss); }
                }
                foreach (var ap in r.Aps) ap.Connected = ap.Bssid == r.ConnectedBssid;
            }
            catch (Exception ex) { AppLogger.Debug($"WifiScanner.Read: {ex.Message}"); }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
            return r;
        }

        // ── WLAN_BSS_ENTRY (wlanapi.h), 360 bytes ────────────────────────────────────────────────────────────────────────────
        private const int EntrySize = 360;

        private static WifiAp? ParseEntry(IntPtr e)
        {
            int ssidLen = Math.Clamp(Marshal.ReadInt32(e, 0), 0, 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(e + 4, ssidBytes, 0, ssidLen);
            var mac = new byte[6];
            Marshal.Copy(e + 40, mac, 0, 6);
            int phy = Marshal.ReadInt32(e, 52);
            int rssi = Marshal.ReadInt32(e, 56);
            int quality = Marshal.ReadInt32(e, 60);
            ushort capInfo = (ushort)Marshal.ReadInt16(e, 88);
            int freqKHz = Marshal.ReadInt32(e, 92);
            int ieOffset = Marshal.ReadInt32(e, 352), ieSize = Marshal.ReadInt32(e, 356);
            var ie = new byte[Math.Clamp(ieSize, 0, 8192)];
            if (ie.Length > 0) Marshal.Copy(e + ieOffset, ie, 0, ie.Length);

            var ap = new WifiAp
            {
                Ssid = ssidBytes.All(b => b == 0) ? "" : Encoding.UTF8.GetString(ssidBytes),
                Bssid = string.Join(":", mac.Select(b => b.ToString("x2"))),
                Rssi = rssi, Quality = quality, FrequencyMHz = freqKHz / 1000,
            };
            try { var v = OuiLookupService.Lookup(ap.Bssid) ?? ""; ap.Vendor = v.StartsWith("(") ? "" : v; }   /* a locally set MAC (guest network, mesh) has no maker */ catch (Exception ex) { AppLogger.Swallowed(ex); }
            (ap.Band, ap.Channel) = WifiAnalysis.ChannelOf(ap.FrequencyMHz);
            if (ap.Band.Length == 0) return null;
            ParseIes(ap, ie, capInfo, phy);
            return ap;
        }

        /// <summary>The information elements of a beacon / probe response → width, standard, streams, security, load, country.</summary>
        internal static void ParseIes(WifiAp ap, byte[] ie, ushort capInfo, int phyType)
        {
            bool ht = false, vht = false, he = false, eht = false, rsn = false, wpa1 = false;
            int htSec = 0; bool htAnyWidth = false;
            int vhtWidth = -1, vhtC0 = 0, vhtC1 = 0;
            int he6Width = -1, he6C0 = 0, he6C1 = 0;
            int ehtWidth = -1, ehtC0 = 0, ehtC1 = 0;
            int streams = 1;
            bool tkipOnly = false, sae = false, psk = false, enterprise = false, owe = false;
            for (int i = 0; i + 2 <= ie.Length;)
            {
                int id = ie[i], len = ie[i + 1];
                if (i + 2 + len > ie.Length) break;
                var d = new ReadOnlySpan<byte>(ie, i + 2, len);
                switch (id)
                {
                    case 7 when len >= 2: ap.Country = Encoding.ASCII.GetString(ie, i + 2, 2).Trim('\0', ' '); break;
                    case 11 when len >= 3:   // BSS Load
                        ap.Clients = d[0] | (d[1] << 8);
                        ap.UtilizationPct = (int)Math.Round(d[2] * 100 / 255.0);
                        break;
                    case 45 when len >= 7:   // HT capabilities: the Rx MCS bitmask, one byte per stream
                        ht = true;
                        { int sN = 0; for (int k = 3; k < 7 && k < len; k++) if (d[k] != 0) sN++; streams = Math.Max(streams, sN); }
                        break;
                    case 61 when len >= 2:   // HT operation
                        ht = true; htSec = d[1] & 3; htAnyWidth = (d[1] & 4) != 0; break;
                    case 191 when len >= 6:  // VHT capabilities: Rx MCS map, 2 bits per stream (3 = not supported)
                        vht = true; streams = Math.Max(streams, StreamsFromMap((ushort)(d[4] | (d[5] << 8)))); break;
                    case 192 when len >= 3:  // VHT operation
                        vht = true; vhtWidth = d[0]; vhtC0 = d[1]; vhtC1 = d[2]; break;
                    case 48 when len >= 2:   // RSN
                        rsn = true; ParseRsn(d, ap, ref tkipOnly, ref sae, ref psk, ref enterprise, ref owe); break;
                    case 221 when len >= 4 && d[0] == 0x00 && d[1] == 0x50 && d[2] == 0xF2:
                        if (d[3] == 1) wpa1 = true;
                        else if (d[3] == 4) ap.Wps = true;
                        break;
                    case 255 when len >= 1:
                        int ext = d[0];
                        var x = d.Slice(1);
                        if (ext == 35 && x.Length >= 19) { he = true; streams = Math.Max(streams, StreamsFromMap((ushort)(x[17] | (x[18] << 8)))); }
                        else if (ext == 36 && x.Length >= 6)   // HE operation (+ the 6 GHz operation information)
                        {
                            he = true;
                            int p = x[0] | (x[1] << 8) | (x[2] << 16);
                            int o = 6 + ((p & (1 << 14)) != 0 ? 3 : 0) + ((p & (1 << 15)) != 0 ? 1 : 0);
                            if ((p & (1 << 17)) != 0 && x.Length >= o + 4) { he6Width = x[o + 1] & 3; he6C0 = x[o + 2]; he6C1 = x[o + 3]; }
                        }
                        else if (ext == 106 && x.Length >= 1)   // EHT operation
                        {
                            eht = true;
                            if ((x[0] & 1) != 0 && x.Length >= 8) { ehtWidth = x[5] & 7; ehtC0 = x[6]; ehtC1 = x[7]; }
                        }
                        else if (ext == 108) eht = true;   // EHT capabilities
                        break;
                }
                i += 2 + len;
            }

            // the width and the block (centre) of the network
            int width = 20, centerCh = ap.Channel;
            if (ap.Band == "2.4")
            {
                if (htSec is 1 or 3 && htAnyWidth) { width = 40; centerCh = ap.Channel + (htSec == 1 ? 2 : -2); }
            }
            else if (ap.Band == "6" && (ehtWidth == 4 || he6Width >= 0))
            {
                if (ehtWidth == 4) { width = 320; centerCh = ehtC1 != 0 ? ehtC1 : ehtC0; }
                else (width, centerCh) = he6Width switch { 3 => (160, he6C1 != 0 ? he6C1 : he6C0), 2 => (80, he6C0), 1 => (40, he6C0), _ => (20, ap.Channel) };
            }
            else
            {
                if (ehtWidth == 4) { width = 320; centerCh = ehtC1 != 0 ? ehtC1 : ehtC0; }
                else if (vhtWidth == 1 && vhtC0 > 0)
                {
                    if (vhtC1 != 0 && Math.Abs(vhtC1 - vhtC0) == 8) { width = 160; centerCh = vhtC1; }
                    else if (vhtC1 != 0) { width = 160; centerCh = vhtC0; }   // 80+80: drawn as its first 80 + the second
                    else { width = 80; centerCh = vhtC0; }
                }
                else if (vhtWidth is 2 or 3 && vhtC0 > 0) { width = 160; centerCh = vhtC0; }   // the old 160 / 80+80 signalling
                else if (htSec is 1 or 3 && htAnyWidth) { width = 40; centerCh = ap.Channel + (htSec == 1 ? 2 : -2); }
            }
            ap.WidthMHz = width;
            int centerMHz = WifiAnalysis.FrequencyOf(ap.Band, centerCh);
            if (centerMHz <= 0) centerMHz = ap.FrequencyMHz;
            ap.LowMHz = centerMHz - width / 2; ap.HighMHz = centerMHz + width / 2;
            // the primary channel must lie inside the block (a wrong centre from a strange beacon falls back to 20 MHz)
            if (ap.FrequencyMHz < ap.LowMHz || ap.FrequencyMHz > ap.HighMHz) { ap.WidthMHz = 20; ap.LowMHz = ap.FrequencyMHz - 10; ap.HighMHz = ap.FrequencyMHz + 10; }

            ap.Streams = Math.Clamp(streams, 1, 8);
            ap.Standard = eht ? "Wi-Fi 7" : he ? (ap.Band == "6" ? "Wi-Fi 6E" : "Wi-Fi 6") : vht ? "Wi-Fi 5" : ht ? "Wi-Fi 4"
                        : ap.Band == "2.4" ? (phyType == 5 ? "802.11b" : "802.11g") : "802.11a";
            ap.MaxRateMbps = WifiAnalysis.MaxRate(ap.Standard, ap.WidthMHz, ap.Streams);

            bool privacy = (capInfo & 0x10) != 0;
            if (rsn)
            {
                string kind = owe ? "OWE" : sae && psk ? "WPA2/WPA3" : sae ? "WPA3" : enterprise ? "WPA2-Enterprise" : "WPA2";
                if (enterprise && sae) kind = "WPA3-Enterprise";
                if (tkipOnly) kind += " (TKIP)";
                ap.Security = kind;
                ap.WeakSecurity = tkipOnly;
            }
            else if (wpa1) { ap.Security = "WPA (TKIP)"; ap.WeakSecurity = true; }
            else if (privacy) { ap.Security = "WEP"; ap.WeakSecurity = true; }
            else { ap.Security = "Open"; ap.OpenNetwork = true; ap.WeakSecurity = true; }
        }

        private static int StreamsFromMap(ushort map)
        {
            int n = 0;
            for (int s = 0; s < 8; s++) if (((map >> (2 * s)) & 3) != 3) n = s + 1;
            return Math.Max(1, n);
        }

        private static void ParseRsn(ReadOnlySpan<byte> d, WifiAp ap, ref bool tkipOnly, ref bool sae, ref bool psk, ref bool enterprise, ref bool owe)
        {
            int o = 2 + 4;   // version + group cipher
            if (d.Length < o + 2) return;
            int pc = d[o] | (d[o + 1] << 8); o += 2;
            bool anyStrong = false, anyTkip = false;
            for (int k = 0; k < pc && o + 4 <= d.Length; k++, o += 4)
            {
                int t = d[o + 3];
                if (t == 2) anyTkip = true; else if (t is 4 or 8 or 9 or 10) anyStrong = true;
            }
            tkipOnly = anyTkip && !anyStrong;
            if (d.Length < o + 2) return;
            int ac = d[o] | (d[o + 1] << 8); o += 2;
            for (int k = 0; k < ac && o + 4 <= d.Length; k++, o += 4)
            {
                int t = d[o + 3];
                if (t is 2 or 4 or 6) psk = true;
                else if (t is 8 or 9 or 24 or 25) sae = true;
                else if (t is 1 or 3 or 5 or 11 or 12 or 13) enterprise = true;
                else if (t == 18) owe = true;
            }
            if (d.Length >= o + 2)
            {
                int caps = d[o] | (d[o + 1] << 8);
                ap.Pmf = (caps & 0x40) != 0 ? "required" : (caps & 0x80) != 0 ? "optional" : "";
            }
        }

        private static string ConnectedBssid(IntPtr h, Guid g)
        {
            IntPtr p = IntPtr.Zero;
            try
            {
                if (WlanQueryInterface(h, ref g, 7 /* current_connection */, IntPtr.Zero, out uint size, out p, IntPtr.Zero) != 0 || p == IntPtr.Zero || size < 566) return "";
                var mac = new byte[6];
                Marshal.Copy(p + 560, mac, 0, 6);   // isState 4 + mode 4 + profile name 512 + DOT11_SSID 36 + bss type 4
                return string.Join(":", mac.Select(b => b.ToString("x2")));
            }
            catch (Exception ex) { AppLogger.Debug($"WifiScanner.ConnectedBssid: {ex.Message}"); return ""; }
            finally { if (p != IntPtr.Zero) WlanFreeMemory(p); }
        }

        // ── interop ──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
            public int isState;
        }
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint ver, IntPtr r, out uint neg, out IntPtr h);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr h, ref Guid g, IntPtr ssid, IntPtr ie, IntPtr r);
        [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, int bssType,
            [MarshalAs(UnmanagedType.Bool)] bool secEnabled, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr h, ref Guid g, uint op, IntPtr r, out uint size, out IntPtr data, IntPtr type);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);
    }
}
