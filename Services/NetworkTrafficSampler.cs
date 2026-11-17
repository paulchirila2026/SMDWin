using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// Single data source for network traffic.
    ///
    /// This sampler runs at most once per second (no more often, however many consumers ask), publishes one result,
    /// and everyone reuses it. Also:
    ///   - GetIPStatistics() (IPv4 + IPv6), not just GetIPv4Statistics() — on connections with IPv6 active
    ///     (common with some providers), half of the traffic is no longer missing;
    ///   - the adapter list is also refreshed on NetworkChange.NetworkAddressChanged, not only every 30 s — an adapter
    ///     added or removed appears at once;
    ///   - virtual/tunnel adapters (Hyper-V, VMware, VirtualBox, VPN) are marked IsVirtual and excluded from the
    ///     aggregate total, so they do not double the real physical traffic.
    /// </summary>
    public static class NetworkTrafficSampler
    {
        private static readonly object _sampleLock = new();
        private static readonly Dictionary<string, (long sent, long recv, DateTime time)> _prev = new();
        private static volatile List<AdapterTrafficEntry> _latest = new();
        private static DateTime _lastSampleUtc = DateTime.MinValue;
        private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(900);

        private static NetworkInterface[] _nicCache = Array.Empty<NetworkInterface>();
        private static DateTime _nicCacheTime = DateTime.MinValue;
        private static volatile bool _nicListDirty = true;
        private static readonly TimeSpan NicCacheTtl = TimeSpan.FromSeconds(30);

        static NetworkTrafficSampler()
        {
            try { NetworkChange.NetworkAddressChanged += (_, _) => _nicListDirty = true; }
            catch (Exception ex) { AppLogger.Debug("NetworkTrafficSampler: NetworkAddressChanged subscribe: " + ex.Message); }
        }

        /// <summary>Returns the latest sample; recomputes only if at least MinSampleInterval has passed since the last real
        /// read — more frequent calls get the same result instead of redoing the work.</summary>
        public static List<AdapterTrafficEntry> GetCurrentTraffic()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSampleUtc) < MinSampleInterval) return _latest;

            lock (_sampleLock)
            {
                now = DateTime.UtcNow;
                if ((now - _lastSampleUtc) < MinSampleInterval) return _latest;

                if (_nicListDirty || (now - _nicCacheTime) > NicCacheTtl || _nicCache.Length == 0)
                {
                    try { _nicCache = NetworkInterface.GetAllNetworkInterfaces(); }
                    catch (Exception ex) { AppLogger.Warning(ex, "NetworkTrafficSampler.EnumNics"); }
                    _nicCacheTime = now;
                    _nicListDirty = false;
                }

                var result = new List<AdapterTrafficEntry>();
                var seenIds = new HashSet<string>();
                foreach (var nic in _nicCache)
                {
                    try
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        seenIds.Add(nic.Id);

                        var stats = nic.GetIPStatistics(); // IPv4 + IPv6
                        long sent = stats.BytesSent;
                        long recv = stats.BytesReceived;

                        double sentKBs = 0, recvKBs = 0;
                        if (_prev.TryGetValue(nic.Id, out var prev))
                        {
                            double secs = (now - prev.time).TotalSeconds;
                            // Counters that go backwards (32-bit rollover on some old NICs, or an adapter recreated with the same Id) are treated
                            // as "no delta", not as a negative speed artificially turned into a large 0.
                            if (secs > 0 && sent >= prev.sent && recv >= prev.recv)
                            {
                                sentKBs = (sent - prev.sent) / secs / 1024.0;
                                recvKBs = (recv - prev.recv) / secs / 1024.0;
                            }
                        }
                        _prev[nic.Id] = (sent, recv, now);

                        result.Add(new AdapterTrafficEntry
                        {
                            Name         = nic.Name,
                            SendKBs      = Math.Round(sentKBs, 2),
                            RecvKBs      = Math.Round(recvKBs, 2),
                            TotalSentMB  = Math.Round(sent / 1_048_576.0, 1),
                            TotalRecvMB  = Math.Round(recv / 1_048_576.0, 1),
                            IsVirtual    = IsVirtualOrTunnel(nic),
                        });
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "NetworkTrafficSampler"); }
                }

                // Vanished adapters — their "frozen" speed is no longer reported
                if (_prev.Count > seenIds.Count)
                {
                    foreach (var staleId in _prev.Keys.Where(k => !seenIds.Contains(k)).ToList())
                        _prev.Remove(staleId);
                }

                _latest = result.OrderByDescending(x => x.SendKBs + x.RecvKBs).ToList();
                _lastSampleUtc = now;
                AddHistory(now, result);
                return _latest;
            }
        }

        // ── History: the last 60 minutes of the physical adapters' total ──
        // Filled on every real sample (the sensor rhythm, on any page), so the Network page traffic chart already has the
        // data when the page opens.
        private static readonly object _histLock = new();
        private static readonly List<(DateTime Time, double Down, double Up)> _history = new();
        public static readonly TimeSpan HistorySpan = TimeSpan.FromMinutes(60);
        private const double SmoothTauSeconds = 3.0;
        private static double _smoothDown, _smoothUp;
        private static DateTime _smoothTime;
        private static bool _hasSmooth;

        private static void AddHistory(DateTime now, List<AdapterTrafficEntry> sample)
        {
            double down = 0, up = 0;
            foreach (var a in sample) { if (a.IsVirtual) continue; down += a.RecvKBs; up += a.SendKBs; }
            lock (_histLock)
            {
                // Smoothing (exponential average, time constant ~3 s): network adapter counters update in jumps (an almost empty
                // second followed by a double one), and the chart would show spikes. The average keeps the shape and total volume,
                // it only rounds the peaks.
                if (_hasSmooth)
                {
                    double dt = Math.Max(0.1, (now - _smoothTime).TotalSeconds);
                    double a = 1.0 - Math.Exp(-dt / SmoothTauSeconds);
                    _smoothDown += (down - _smoothDown) * a;
                    _smoothUp   += (up   - _smoothUp)   * a;
                }
                else { _smoothDown = down; _smoothUp = up; _hasSmooth = true; }
                _smoothTime = now;
                down = _smoothDown; up = _smoothUp;
                _history.Add((now, down, up));
                var cutoff = now - HistorySpan - TimeSpan.FromSeconds(5);
                int drop = 0;
                while (drop < _history.Count && _history[drop].Time < cutoff) drop++;
                if (drop > 0) _history.RemoveRange(0, drop);
            }
        }

        /// <summary>
        /// Per-second history for the last <paramref name="windowSeconds"/> (at most <see cref="HistorySpan"/>):
        /// one point per second (the value of the latest sample up to then), starting with the first second that has data.
        /// So the chart has the same time axis whatever the read rate.
        /// <c>EndSecond</c> = the second (Unix epoch) of the last point, to align the intervals when the series is reduced
        /// to fewer points (<see cref="SeriesRuns.Buckets"/>).
        /// </summary>
        public static (List<double> Down, List<double> Up, long EndSecond) HistoryPerSecond(int windowSeconds = 300)
        {
            var down = new List<double>(); var up = new List<double>();
            List<(DateTime Time, double Down, double Up)> h;
            lock (_histLock) h = new(_history);
            // the end is rounded to a whole second, so the points always fall on the same seconds
            long endSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (h.Count == 0) return (down, up, endSecond);
            int n = Math.Clamp(windowSeconds, 10, (int)HistorySpan.TotalSeconds);
            var end = DateTimeOffset.FromUnixTimeSeconds(endSecond).UtcDateTime.AddSeconds(1);
            int j = 0; bool started = false;
            for (int i = n - 1; i >= 0; i--)
            {
                var binEnd = end - TimeSpan.FromSeconds(i);
                while (j + 1 < h.Count && h[j + 1].Time <= binEnd) j++;
                if (h[j].Time > binEnd) continue;         // no data yet at this time
                started = true;
                down.Add(h[j].Down); up.Add(h[j].Up);
            }
            if (!started) { down.Add(h[^1].Down); up.Add(h[^1].Up); }
            return (down, up, endSecond);
        }

        /// <summary>Forces a refresh on the next request (used after resume from sleep, when Wi-Fi/Ethernet may reconnect in
        /// a different state before the NetworkAddressChanged event arrives).</summary>
        public static void InvalidateAdapterCache() => _nicListDirty = true;

        /// <summary>Sum over the physical adapters (excludes Hyper-V/VMware/VirtualBox/VPN/tunnel) — used for the "total
        /// traffic" card on the Dashboard, so the same traffic is not counted twice (once on the physical adapter, once on
        /// the associated virtual adapter).</summary>
        public static (double sendKBs, double recvKBs) GetPhysicalTotal()
        {
            double s = 0, r = 0;
            foreach (var a in GetCurrentTraffic())
            {
                if (a.IsVirtual) continue;
                s += a.SendKBs; r += a.RecvKBs;
            }
            return (s, r);
        }

        private static bool IsVirtualOrTunnel(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            string n = (nic.Name + " " + nic.Description).ToUpperInvariant();
            return n.Contains("VETHERNET") || n.Contains("HYPER-V") || n.Contains("VMWARE")
                || n.Contains("VIRTUALBOX") || n.Contains("VBOX") || n.Contains("VPN")
                || n.Contains("TAP-") || n.Contains("TUNNEL") || n.Contains("WIREGUARD")
                || n.Contains("NORDLYNX") || n.Contains("LOOPBACK ADAPTER");
        }
    }
}
