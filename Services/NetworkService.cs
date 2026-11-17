using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class NetworkService
    {
        private List<NetworkAdapterEntry>? _adapterCache;
        private DateTime _adapterCacheTime = DateTime.MinValue;
        private static readonly TimeSpan _adapterTtl = TimeSpan.FromSeconds(30);

        public async Task<List<NetworkAdapterEntry>> GetAdaptersAsync(bool forceRefresh = false)
        {
            // Return cached result if still fresh and not forced
            if (!forceRefresh
                && _adapterCache != null
                && (DateTime.Now - _adapterCacheTime) < _adapterTtl)
                return _adapterCache;

            var result = await GetAdaptersInternalAsync();
            _adapterCache    = result;
            _adapterCacheTime = DateTime.Now;
            return result;
        }

        private async Task<List<NetworkAdapterEntry>> GetAdaptersInternalAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<NetworkAdapterEntry>();

                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.NetConfigQuery);

                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp1)
                    {
                        var name = obj["Description"]?.ToString() ?? "";
                        var ips  = obj["IPAddress"] as string[];
                        var gws  = obj["DefaultIPGateway"] as string[];
                        var dns  = obj["DNSServerSearchOrder"] as string[];
                        var mac  = obj["MACAddress"]?.ToString() ?? "";

                        results.Add(new NetworkAdapterEntry
                        {
                            Name       = name,
                            Type       = DetectAdapterType(name),
                            Status     = "Up",
                            MacAddress = mac,
                            IpAddress  = ips?.Length > 0 ? ips[0] : "—",
                            Gateway    = gws?.Length > 0 ? gws[0] : "—",
                            Dns        = dns?.Length > 0 ? string.Join(", ", dns) : "—",
                        });
                    }
                    }

                    // Add speed info from NetworkInterface
                    var niAdapters = NetworkInterface.GetAllNetworkInterfaces();
                    string? wifiActualSpeed = null;
                    foreach (var ni in niAdapters)
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        var rawMac = ni.GetPhysicalAddress().ToString(); // e.g. "AABBCCDDEEFF"
                        if (rawMac.Length < 12) continue; // skip adapters with no/short MAC
                        var niMac = $"{rawMac[0..2]}-{rawMac[2..4]}-{rawMac[4..6]}-{rawMac[6..8]}-{rawMac[8..10]}-{rawMac[10..12]}";
                        var match = results.Find(r => r.MacAddress.Replace(":", "-")
                            .Equals(niMac, StringComparison.OrdinalIgnoreCase));
                        if (match == null) continue;

                        if (match.Type == "Wi-Fi")
                        {
                            wifiActualSpeed ??= GetWifiActualSpeedMbps(); // run only once, whatever the number of Wi-Fi adapters (rarely >1)
                            if (wifiActualSpeed != null) { match.Speed = wifiActualSpeed; continue; }
                        }
                        if (ni.Speed > 0)
                            match.Speed = FormatSpeed(ni.Speed);
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }

                // Also add offline adapters
                try
                {
                    using var searcher2 = WmiHelper.Searcher(
                        WmiHelper.NetAdapterAllQuery);

                    using (var __wmiTmp2 = searcher2.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp2)
                    {
                        var name    = obj["Name"]?.ToString() ?? "";
                        var enabled = obj["NetEnabled"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;

                        bool alreadyIn = results.Exists(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyIn)
                        {
                            bool isBt = name.ToLower().Contains("bluetooth") || DetectAdapterType(name) == "Bluetooth";
                            string status;
                            if (isBt)
                            {
                                // BT adapters: check bthserv service status
                                try
                                {
                                    var s2 = new System.ServiceProcess.ServiceController("bthserv");
                                    status = s2.Status == System.ServiceProcess.ServiceControllerStatus.Running ? "Up" : "Off";
                                }
                                catch { status = "Unknown"; }
                            }
                            else
                            {
                                status = enabled == "True" ? "Up" : "Down";
                            }
                            results.Add(new NetworkAdapterEntry
                            {
                                Name   = name,
                                Type   = DetectAdapterType(name),
                                Status = status,
                                MacAddress = obj["MACAddress"]?.ToString() ?? "—",
                                IpAddress  = "—",
                                Gateway    = "—",
                                Dns        = "—"
                            });
                        }
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }

                return results;
            });
        }


        public void OpenNetworkSettings() =>
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });

        public void OpenNetworkAdapters() =>
            Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });

        /// <summary>
        /// Scans the local subnet and returns all responding devices (like Wireless Network Watcher).
        /// Sends ICMP pings to all IPs in the /24 subnet, resolves hostnames, reads ARP cache for MACs.
        /// </summary>
        public async Task<List<SMDWin.Models.LocalNetworkDevice>> ScanLocalNetworkAsync(
            IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default)
        {
            // subnet and gateway chosen NOW from the SAME adapter — the one with the real default route (not the first active
            // adapter found, which could be VirtualBox/Hyper-V/VPN). The range is computed from the real mask (not always /24),
            // capped at /22 (1022 addresses) so a wrongly detected /16 network does not block the scan for hours.
            var scanNic = SelectScanAdapter();
            if (scanNic == null)
            {
                try { progress?.Report("No active network adapter found."); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                return new List<SMDWin.Models.LocalNetworkDevice>();
            }

            uint networkAddr = ToUInt32(scanNic.Value.address);
            int prefixLen = scanNic.Value.prefixLength;
            int hostBits = 32 - prefixLen;
            // the real mask is used to find the network address (baseNet) — independent of the scan cap. blockSize = all addresses
            // in the subnet (network + broadcast included); scanCount = how many host addresses are actually scanned, capped at
            // 1022 (~/22) so a wrongly detected /16 or larger network does not mean hours of scanning.
            uint blockSize = hostBits >= 31 ? 2u : (1u << hostBits);
            uint subnetMask = hostBits >= 32 ? 0u : (hostBits <= 0 ? 0xFFFFFFFFu : (0xFFFFFFFFu << hostBits));
            uint baseNet = networkAddr & subnetMask;
            uint hostCount = blockSize > 2 ? blockSize - 2 : 1;   // excludes the network and broadcast addresses
            if (hostCount > 1022) hostCount = 1022;               // plafon ~/22
            bool isClassicSlash24 = prefixLen == 24;
            string baseIp = $"{(baseNet >> 24) & 0xFF}.{(baseNet >> 16) & 0xFF}.{(baseNet >> 8) & 0xFF}";
            string? gatewayIp = scanNic.Value.gateway;

            try
            {
                var found = new System.Collections.Concurrent.ConcurrentBag<SMDWin.Models.LocalNetworkDevice>();

                try
                {
                    progress?.Report(isClassicSlash24
                        ? $"Scanning network {baseIp}.0/24 …"
                        : $"Scanning network {FromUInt32(baseNet)}/{prefixLen} ({hostCount} addresses) …");
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                // Throttle to 50 concurrent pings. Async reverse DNS with a 1 s timeout.
                var sem = new System.Threading.SemaphoreSlim(50, 50);
                var pingTasks = new List<Task>();
                for (uint h = 1; h <= hostCount; h++)
                {
                    if (ct.IsCancellationRequested) break;
                    uint hostOffset = h;
                    pingTasks.Add(Task.Run(async () =>
                    {
                        bool acquired = false;
                        try
                        {
                            await sem.WaitAsync(ct).ConfigureAwait(false);
                            acquired = true;
                            if (ct.IsCancellationRequested) return;
                            // baseIp = the fixed network octets; for any mask the scanned address is simply baseNet + offset
                            // (identical to {baseIp}.{offset} for /24).
                            string ip = FromUInt32(baseNet + hostOffset);
                            using var ping = new Ping();
                            var reply = await ping.SendPingAsync(ip, 500).ConfigureAwait(false);
                            if (reply?.Status == IPStatus.Success)
                            {
                                var dev = new SMDWin.Models.LocalNetworkDevice
                                {
                                    IpAddress = ip,
                                    PingMs    = reply.RoundtripTime.ToString(),
                                    Status    = "Online",
                                    IsRouter  = ip == gatewayIp,
                                };
                                try
                                {
                                    var dnsTask = System.Net.Dns.GetHostEntryAsync(ip);
                                    _ = dnsTask.ContinueWith(t => { var _ = t.Exception; },
                                        TaskContinuationOptions.OnlyOnFaulted);
                                    var hostEntry = await dnsTask
                                        .WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                                    dev.Hostname = hostEntry?.HostName ?? "—";
                                    if (dev.Hostname == ip) dev.Hostname = "—";
                                }
                                catch { dev.Hostname = "—"; }

                                if (dev.Hostname == "—")
                                {
                                    try
                                    {
                                        string? nb = await TryGetNetBiosNameAsync(ip, ct).ConfigureAwait(false);
                                        if (!string.IsNullOrEmpty(nb)) dev.Hostname = nb;
                                    }
                                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* keep "—" */ }
                                }

                                found.Add(dev);
                                try { progress?.Report($"Found: {ip}  ({found.Count} devices so far)"); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }
                        finally { if (acquired) { try { sem.Release(); } catch (ObjectDisposedException) { } } }
                    }, ct));
                }

                // await (not .Wait) — no thread pool thread is blocked for the scan. Tasks still running after 40 s (very slow device)
                // are abandoned — at most they add to the concurrent bag after the final list has been copied; they do not affect the
                // result returned here.
                var allPings = Task.WhenAll(pingTasks);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(40), ct);
                var finishedFirst = await Task.WhenAny(allPings, timeoutTask).ConfigureAwait(false);
                if (finishedFirst == timeoutTask)
                    AppLogger.Warning("NetworkService.ScanLocalNetworkAsync: 40s timeout — some pings still running, ignored.");

                // the cancellation request (Stop) is visible to the caller as such, not as a partial list returned "successfully" —
                // the caller can tell a stop from a normal completion.
                ct.ThrowIfCancellationRequested();

                // completion from the ARP table — devices that do not answer ping (many phones and Windows machines with the default
                // firewall) but communicated recently on the subnet still appear in ARP. They are added as "online, no ping reply".
                try
                {
                    var arpTable = GetArpTable();
                    foreach (var dev in found)
                    {
                        if (arpTable.TryGetValue(dev.IpAddress, out var mac))
                        {
                            dev.MacAddress = mac.ToUpperInvariant();
                            dev.Vendor = LookupVendor(mac);
                        }
                    }

                    var alreadyFound = new HashSet<string>(found.Select(d => d.IpAddress));
                    string noPingLabel = LanguageService.S("Network", "NoPingLabel");
                    foreach (var (arpIp, mac) in arpTable)
                    {
                        if (alreadyFound.Contains(arpIp)) continue;
                        if (isClassicSlash24) { if (!arpIp.StartsWith(baseIp + ".", StringComparison.Ordinal)) continue; }
                        else { if (!IsInRange(arpIp, baseNet, hostCount)) continue; }
                        found.Add(new SMDWin.Models.LocalNetworkDevice
                        {
                            IpAddress   = arpIp,
                            PingMs      = noPingLabel,
                            Status      = "Online",
                            IsRouter    = arpIp == gatewayIp,
                            MacAddress  = mac.ToUpperInvariant(),
                            Vendor      = LookupVendor(mac),
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkService.ArpMerge"); }

                try { progress?.Report($"Scan complete — {found.Count} device(s) found."); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                try
                {
                    return found.OrderBy(d =>
                    {
                        var parts = d.IpAddress.Split('.');
                        return parts.Length == 4 && int.TryParse(parts[3], out int n) ? n : 999;
                    }).ToList();
                }
                catch { return found.ToList(); }
            }
            finally
            {
                // The CancellationTokenSource is owned by the caller (the window) — nothing to do at this level.
            }
        }

        private static bool IsInRange(string ip, uint baseNet, uint hostCount)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var addr)) return false;
            uint v = ToUInt32(addr);
            return v > baseNet && v <= baseNet + hostCount;
        }

        private static uint ToUInt32(System.Net.IPAddress addr)
        {
            var b = addr.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static string FromUInt32(uint v) =>
            $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        /// <summary>
        /// Picks the adapter to scan by the presence of a real default route (gateway), NOT "the first active adapter" — on a
        /// machine with VirtualBox/Hyper-V/VPN, the first active adapter is usually not the one connected to the internet.
        /// Known virtual/tunnel adapters are excluded explicitly, even if they happen to have a gateway configured.
        /// </summary>
        private static (System.Net.IPAddress address, int prefixLength, string? gateway)? SelectScanAdapter()
        {
            try
            {
                var candidates = new List<(NetworkInterface nic, System.Net.IPAddress addr, int prefix, string gw)>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsVirtualOrTunnelAdapter(ni)) continue;

                    string? gw = null;
                    foreach (var g in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !g.Address.Equals(System.Net.IPAddress.Any))
                        { gw = g.Address.ToString(); break; }
                    }
                    if (gw == null) continue; // no gateway = not the internet adapter — ignored in this pass

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        int prefix = ua.PrefixLength > 0 && ua.PrefixLength < 32 ? ua.PrefixLength : 24;
                        candidates.Add((ni, ua.Address, prefix, gw));
                        break;
                    }
                }

                if (candidates.Count == 0)
                {
                    // Fallback: no adapter with a gateway found (isolated network / link-local only) — keep at least one active
                    // non-virtual adapter.
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (IsVirtualOrTunnelAdapter(ni)) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            int prefix = ua.PrefixLength > 0 && ua.PrefixLength < 32 ? ua.PrefixLength : 24;
                            return (ua.Address, prefix, null);
                        }
                    }
                    return null;
                }

                // Prefer Ethernet over other types (Wireless80211, etc.) when both exist — a rare case, but it avoids a needless
                // ambiguity; otherwise the first candidate found.
                var ethernet = candidates.Find(c => c.nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                var best = ethernet.nic != null ? ethernet : candidates[0];
                return (best.addr, best.prefix, best.gw);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkService.SelectScanAdapter"); return null; }
        }

        private static bool IsVirtualOrTunnelAdapter(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            string n = (nic.Name + " " + nic.Description).ToUpperInvariant();
            return n.Contains("VETHERNET") || n.Contains("HYPER-V") || n.Contains("VMWARE")
                || n.Contains("VIRTUALBOX") || n.Contains("VBOX") || n.Contains("VPN")
                || n.Contains("TAP-") || n.Contains("TUNNEL") || n.Contains("WIREGUARD")
                || n.Contains("NORDLYNX") || n.Contains("LOOPBACK ADAPTER");
        }


        private static Dictionary<string, string> GetArpTable()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo("arp", "-a")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return result;
                string output = ProcessRunner.ReadStdoutBounded(proc, 8000) ?? "";
                proc.WaitForExit(1000);

                // Parse "  192.168.1.1          aa-bb-cc-dd-ee-ff     dynamic"
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split(new char[]{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var ip  = parts[0];
                        var mac = parts[1];
                        if (mac.Length == 17 && mac.Contains('-'))
                            result[ip] = mac.Replace('-', ':');
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }
            return result;
        }

        private static async System.Threading.Tasks.Task<string?> TryGetNetBiosNameAsync(string ip, CancellationToken ct)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("nbtstat", $"-A {ip}")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var exitTask = proc.WaitForExitAsync(ct);
                var timeoutTask = System.Threading.Tasks.Task.Delay(1500, ct);
                var finished = await System.Threading.Tasks.Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
                if (finished == timeoutTask) { try { proc.Kill(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } return null; }

                string output = await outputTask.ConfigureAwait(false);
                foreach (var line in output.Split('\n'))
                {
                    // Format tipic: "MYPC           <00>  UNIQUE      Registered"
                    var trimmed = line.Trim();
                    if (trimmed.Contains("<00>") && trimmed.Contains("UNIQUE"))
                    {
                        string name = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                        if (!string.IsNullOrWhiteSpace(name) && name.Length > 1) return name;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static string LookupVendor(string mac)
        {
            return OuiLookupService.Lookup(mac) ?? "—";
        }

        private static string DetectAdapterType(string name)
        {
            var u = name.ToUpperInvariant();
            // the mobile broadband modem (SIM) before Wi-Fi: "Sierra Wireless" is not a Wi-Fi adapter
            if (u.Contains("MOBILE BROADBAND") || u.Contains("WWAN") || u.Contains("SIERRA WIRELESS") || u.Contains("FIBOCOM")
                || u.Contains(" LTE") || u.Contains("HSPA")) return "Mobile";
            if (u.Contains("WI-FI") || u.Contains("WIFI") || u.Contains("WIRELESS") || u.Contains("802.11")) return "Wi-Fi";
            if (u.Contains("BLUETOOTH") || u.Contains("BT ")) return "Bluetooth";
            if (u.Contains("VPN") || u.Contains("TUNNEL") || u.Contains("TAP")) return "VPN";
            if (u.Contains("ETHERNET") || u.Contains("GIGABIT") || u.Contains("LAN") || u.Contains("REALTEK")) return "Ethernet";
            return "Altul";
        }

        private static string FormatSpeed(long bps)
        {
            if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:F0} Gbps";
            if (bps >= 1_000_000)     return $"{bps / 1_000_000.0:F0} Mbps";
            return $"{bps / 1000.0:F0} Kbps";
        }

        /// <summary>
        /// Real Wi-Fi speed (Rx/Tx), read directly from the WLAN API (WifiReader), not by parsing the text of
        /// "netsh wlan show interfaces" — its labels ("Receive rate" / "Transmit rate") do not exist in that form on
        /// Windows in other languages.
        /// </summary>
        private static string? GetWifiActualSpeedMbps()
        {
            try
            {
                var wifi = WifiReader.Read(forceRefresh: true);
                if (!wifi.IsConnected) return null;
                // a single number (link speed); "1300/144 Mbps" did not say which is which
                if (wifi.LinkSpeedMbps > 0 && wifi.TxLinkSpeedMbps > 0)
                    return $"{Math.Max(wifi.LinkSpeedMbps, wifi.TxLinkSpeedMbps)} Mbps";
                if (wifi.LinkSpeedMbps > 0)
                    return $"{wifi.LinkSpeedMbps} Mbps";
                return null;
            }
            catch (Exception ex) { AppLogger.Debug("NetworkService.GetWifiActualSpeedMbps: " + ex.Message); return null; }
        }
    }
}
