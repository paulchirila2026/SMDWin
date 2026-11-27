using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>One finding of the channel analysis: how serious, and the text (English key of the language files + arguments).</summary>
    public sealed record WifiFinding(int Severity, string Text, object[] Args);   // 0 = good, 1 = info, 2 = watch, 3 = bad

    /// <summary>
    /// The calculations behind Network › Wi-Fi › Channel analysis: channels and frequencies, how much two networks overlap,
    /// how crowded each channel is, the best channel and width per band, and the findings (interference, security, signal).
    ///
    /// Overlap: two networks share air when their frequency blocks overlap. Same primary channel = co-channel: they hear each
    /// other as Wi-Fi and take turns (slower, but orderly). Partial overlap on another channel = adjacent-channel interference:
    /// they hear each other as noise and damage each other's frames — the worse case, typical on 2.4 GHz off channels 1 / 6 / 11.
    /// Crowding of a channel = the sum of the received power of the other networks that overlap it (partial overlap weighted
    /// ×1.6), so a loud neighbour counts more than many faint ones.
    /// </summary>
    public static class WifiAnalysis
    {
        public static readonly int[] Ch24 = Enumerable.Range(1, 13).ToArray();
        public static readonly int[] Ch5 = { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 };
        public static readonly int[] Ch6 = Enumerable.Range(0, 59).Select(i => 1 + 4 * i).ToArray();   // 1 … 233
        /// <summary>6 GHz preferred scanning channels (PSC): the ones phones look at first.</summary>
        public static readonly int[] Psc6 = { 5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229 };

        public static int[] Channels(string band) => band == "2.4" ? Ch24 : band == "5" ? Ch5 : Ch6;
        public static bool IsDfs(string band, int ch) => band == "5" && ch >= 52 && ch <= 144;

        public static (string band, int channel) ChannelOf(int mhz)
        {
            if (mhz == 2484) return ("2.4", 14);
            if (mhz >= 2401 && mhz <= 2495) return ("2.4", (mhz - 2407) / 5);
            if (mhz >= 5150 && mhz <= 5895) return ("5", (mhz - 5000) / 5);
            if (mhz >= 5925 && mhz <= 7125) return ("6", (mhz - 5950) / 5);
            return ("", 0);
        }

        public static int FrequencyOf(string band, int ch) => band switch
        {
            "2.4" => ch == 14 ? 2484 : 2407 + 5 * ch,
            "5" => 5000 + 5 * ch,
            "6" => 5950 + 5 * ch,
            _ => 0,
        };

        /// <summary>The highest PHY rate the access point advertises (max MCS, short guard interval), Mb/s.</summary>
        public static int MaxRate(string standard, int width, int streams)
        {
            double perStream = standard switch
            {
                "Wi-Fi 7" => width switch { >= 320 => 2882, 160 => 1441, 80 => 720.6, 40 => 344.1, _ => 172.1 },
                "Wi-Fi 6" or "Wi-Fi 6E" => width switch { >= 160 => 1201, 80 => 600.5, 40 => 286.8, _ => 143.4 },
                "Wi-Fi 5" => width switch { >= 160 => 866.7, 80 => 433.3, 40 => 200, _ => 86.7 },
                "Wi-Fi 4" => width >= 40 ? 150 : 72.2,
                "802.11b" => 11,
                _ => 54,
            };
            bool legacy = standard.StartsWith("802.11", StringComparison.Ordinal);
            return (int)Math.Round(perStream * (legacy ? 1 : Math.Max(1, streams)));
        }

        public static int OverlapMHz(WifiAp a, WifiAp b)
            => a.Band != b.Band ? 0 : Math.Max(0, Math.Min(a.HighMHz, b.HighMHz) - Math.Max(a.LowMHz, b.LowMHz));

        /// <summary>The same router / mesh as the connected network: the same name, or the same MAC apart from the last byte.</summary>
        public static bool IsOwn(WifiAp ap, WifiAp? connected)
        {
            if (connected == null) return false;
            if (ap.Ssid.Length > 0 && ap.Ssid == connected.Ssid) return true;
            return ap.Bssid.Length >= 14 && connected.Bssid.Length >= 14 && ap.Bssid[..14] == connected.Bssid[..14];
        }

        /// <summary>How loud the other networks are on a 20 MHz slot at a channel (0 = nothing there). Own networks are left out.</summary>
        public static double Crowding(IEnumerable<WifiAp> aps, string band, int channel, WifiAp? connected)
        {
            int f = FrequencyOf(band, channel), lo = f - 10, hi = f + 10;
            double s = 0;
            foreach (var a in aps)
            {
                if (a.Band != band || IsOwn(a, connected)) continue;
                int o = Math.Max(0, Math.Min(hi, a.HighMHz) - Math.Max(lo, a.LowMHz));
                if (o <= 0) continue;
                double p = Math.Pow(10, (a.Rssi + 100) / 20.0);   // −100 dBm → 1, −60 dBm → 100
                s += p * (a.Channel == channel ? 1.0 : 1.6) * (o / 20.0);
            }
            return s;
        }

        /// <summary>The channels worth choosing on a band, least crowded first: 2.4 GHz 1 / 6 / 11; 5 GHz without DFS first;
        /// 6 GHz the PSC channels.</summary>
        public static List<(int ch, double crowding)> Best(IList<WifiAp> aps, string band, WifiAp? connected, bool allowDfs = false)
        {
            var cand = band == "2.4" ? new[] { 1, 6, 11 } : band == "5" ? Ch5.Where(c => allowDfs || !IsDfs(band, c)).ToArray() : Psc6;
            return cand.Select(c => (c, Crowding(aps, band, c, connected))).OrderBy(x => x.Item2).ThenBy(x => x.c).ToList();
        }

        /// <summary>The width to use on a band: 20 MHz on 2.4; on 5 GHz 80 if the 80 MHz block of the best channel is quiet, else 40;
        /// on 6 GHz 160.</summary>
        public static int RecommendedWidth(IList<WifiAp> aps, string band, int bestChannel, WifiAp? connected)
        {
            if (band == "2.4") return 20;
            if (band == "6") return 160;
            int i = Array.IndexOf(Ch5, bestChannel);
            if (i < 0) return 40;
            int g = i / 4 * 4;
            double block = 0;
            for (int k = g; k < g + 4 && k < Ch5.Length; k++) block += Crowding(aps, band, Ch5[k], connected);
            return block < 400 ? 80 : 40;
        }

        /// <summary>The interference on the connected network's channel: co-channel and partially overlapping access points.</summary>
        public static (List<WifiAp> co, List<WifiAp> adj) Interference(IList<WifiAp> aps, WifiAp me)
        {
            var co = new List<WifiAp>(); var adj = new List<WifiAp>();
            foreach (var a in aps)
            {
                if (a == me || IsOwn(a, me) || OverlapMHz(a, me) <= 0) continue;
                (a.Channel == me.Channel ? co : adj).Add(a);
            }
            return (co.OrderByDescending(a => a.Rssi).ToList(), adj.OrderByDescending(a => a.Rssi).ToList());
        }

        /// <summary>What is worth knowing on a band: interference, wide channels on 2.4 GHz, weak security, the own signal and band.</summary>
        public static List<WifiFinding> Findings(IList<WifiAp> aps, string band, WifiAp? me)
        {
            var f = new List<WifiFinding>();
            var onBand = aps.Where(a => a.Band == band).ToList();
            if (band == "2.4")
            {
                foreach (var a in onBand.Where(a => a.Channel is not (1 or 6 or 11) && a.Channel <= 13 && a.Rssi >= -80).Take(4))
                    f.Add(new(2, "{0} uses channel {1}, between the non-overlapping channels 1 / 6 / 11 — it disturbs the networks on both sides.", new object[] { Name(a), a.Channel }));
                foreach (var a in onBand.Where(a => a.WidthMHz >= 40).Take(3))
                    f.Add(new(2, "{0} uses 40 MHz on 2.4 GHz — it takes almost half of the band (channels {1}–{2}).", new object[] { Name(a), ChannelOf(a.LowMHz + 10).channel, ChannelOf(a.HighMHz - 10).channel }));
            }
            if (me != null)
            {
                if (me.Security is "Open" or "WEP" || me.WeakSecurity)
                    f.Add(new(3, "Your network uses {0} — weak protection. Set WPA2-AES or WPA3 in the router.", new object[] { me.Security }));
                else if (me.Security.Contains("WPA3"))
                    f.Add(new(0, "Your network: {0} — correct.", new object[] { me.Security }));
                else
                    f.Add(new(1, "Your network: {0}. If all devices are recent, WPA3 (or WPA2/WPA3) is stronger.", new object[] { me.Security }));
                if (me.Wps) f.Add(new(2, "WPS is on on your router — the WPS PIN can be broken; it is better switched off.", Array.Empty<object>()));
                if (me.Rssi < -70) f.Add(new(2, "Your signal is weak ({0} dBm) — move closer, or add an access point / mesh unit.", new object[] { me.Rssi }));
                if (me.UtilizationPct is int u && u >= 50) f.Add(new(2, "Your router reports its channel busy {0}% of the time — expect slowdowns.", new object[] { u }));
                if (me.Band == "2.4")
                {
                    var better = aps.Where(a => a.Band != "2.4" && IsOwn(a, me) && a.Rssi >= -67).OrderByDescending(a => a.Rssi).FirstOrDefault();
                    if (better != null)
                        f.Add(new(2, "You are on 2.4 GHz although your network on {0} GHz has {1} dBm here — it would be several times faster. Band steering in the router does this by itself.", new object[] { better.Band, better.Rssi }));
                }
            }
            var open = onBand.Where(a => a.OpenNetwork && !(me != null && IsOwn(a, me))).ToList();
            if (open.Count > 0) f.Add(new(1, "{0} open network(s) nearby ({1}) — anyone close by can see their traffic.", new object[] { open.Count, string.Join(", ", open.Take(3).Select(Name)) }));
            var tkip = onBand.Where(a => a.Security.Contains("TKIP") || a.Security == "WEP").ToList();
            if (tkip.Count > 0) f.Add(new(1, "{0} network(s) with old encryption (TKIP / WEP): {1} — slower (max 54 Mb/s) and weak.", new object[] { tkip.Count, string.Join(", ", tkip.Take(3).Select(Name)) }));
            int wps = onBand.Count(a => a.Wps && !(me != null && IsOwn(a, me)));
            if (wps > 0) f.Add(new(1, "{0} router(s) nearby have WPS on.", new object[] { wps }));
            if (band == "5")
            {
                bool dfsFree = Ch5.Where(c => IsDfs("5", c)).All(c => Crowding(aps, "5", c, me) < 5);
                if (dfsFree) f.Add(new(1, "The DFS channels (52–144) are almost empty — good for a second access point, if a start-up pause of up to a minute is acceptable.", Array.Empty<object>()));
            }
            return f.OrderByDescending(x => x.Severity).ToList();
        }

        public static string Name(WifiAp a) => a.Ssid.Length > 0 ? a.Ssid : "(" + a.Bssid + ")";
    }
}
