
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class NetworkDiagnosisResult
    {
        public string? Gateway { get; set; }
        public bool GatewayAmbiguous { get; set; } // several active gateways (VPN, multi-homed etc.)

        public bool   AdapterOk     { get; set; }
        public string AdapterDetail { get; set; } = "";

        public bool   RouterOk     { get; set; }
        public string RouterDetail { get; set; } = "";
        public bool   InternetOk     { get; set; }
        public string InternetDetail { get; set; } = "";
        public bool   DnsOk     { get; set; }
        public string DnsDetail { get; set; } = "";
        public bool   HttpOk     { get; set; }
        public string HttpDetail { get; set; } = "";

        /// <summary>Full explanation, shown in the Traceroute tool (manual).</summary>
        public string Summary { get; set; } = "";
        /// <summary>Short version, attached directly to the line in the Ping Monitor timeout log (automatic).</summary>
        public string ShortSummary { get; set; } = "";
    }

    public static class NetworkDiagnosisService
    {
        public static async Task<NetworkDiagnosisResult> RunAsync(CancellationToken ct = default)
        {
            var result = new NetworkDiagnosisResult();

            var activeAdapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                          && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                .ToList();
            var upAdapters = activeAdapters.Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up).ToList();
            var upWithIp = upAdapters.Where(ni => ni.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)).ToList();

            if (upAdapters.Count == 0)
            {
                result.AdapterOk = false;
                result.AdapterDetail = "No active network adapter (all down/disconnected)";
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "AdapterDown", "No active network adapter — the cable is unplugged or Wi-Fi is off.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortAdapterDown", "Network adapter down");
                return result;
            }
            if (upWithIp.Count == 0)
            {
                result.AdapterOk = false;
                result.AdapterDetail = "Adapter is up but has no IPv4 address (DHCP failure?)";
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "AdapterNoIp", "Network adapter is connected but has no IP address — likely a DHCP problem, try reconnecting.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortAdapterNoIp", "Adapter has no IP");
                return result;
            }
            result.AdapterOk = true;
            result.AdapterDetail = string.Join(", ", upWithIp.Select(ni => ni.Name));

            // ── Nivel 1: Router (gateway implicit) ──────────────────────────
            string? gateway = GetDefaultGateway(out bool ambiguous);
            result.Gateway = gateway;
            result.GatewayAmbiguous = ambiguous;

            if (gateway == null)
            {
                result.RouterDetail = "Could not detect a default gateway on any active network adapter.";
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "NoGateway", "Could not detect a default gateway. Check your network adapter.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortNoGateway", "No gateway detected");
                return result;
            }

            var (routerOk, routerMs) = await PingHostAsync(gateway, 1200, ct);
            result.RouterOk = routerOk;
            result.RouterDetail = routerOk ? $"{gateway} — {routerMs} ms" : $"{gateway} — no response";

            if (!routerOk)
            {
                result.Summary = string.Format(SMDWin.Services.LanguageService.S("NetDiag", "RouterDown", "Router ({0}) isn't responding — likely a local problem."), gateway);
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortRouterDown", "Router unreachable — local problem");
                return result;
            }

            // ── Nivel 2: Internet, ocolind DNS (IP public direct) ───────────
            var (internetOk, internetMs) = await PingHostAsync("8.8.8.8", 1500, ct);
            result.InternetOk = internetOk;
            result.InternetDetail = internetOk ? $"8.8.8.8 — {internetMs} ms" : "8.8.8.8 — no response";

            if (!internetOk)
            {
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "InternetDown", "Router responds, but nothing beyond it — likely an ISP/upstream problem.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortInternetDown", "ISP/upstream problem");
                return result;
            }

            // ── Nivel 3: DNS ─────────────────────────────────────────────────
            bool dnsOk = false;
            string dnsDetail;
            try
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync("google.com", ct)
                    .WaitAsync(TimeSpan.FromSeconds(2), ct);
                dnsOk = addrs.Length > 0;
                dnsDetail = dnsOk ? $"google.com → {addrs[0]}" : "No addresses returned";
            }
            catch (Exception ex) { dnsDetail = $"Failed: {ex.Message}"; }
            result.DnsOk = dnsOk;
            result.DnsDetail = dnsDetail;

            if (!dnsOk)
            {
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "DnsDown", "Internet works, but DNS isn't resolving names — try a different DNS server.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortDnsDown", "DNS not resolving (internet OK)");
                return result;
            }

            // ── Level 4: real HTTP (not just ping) ───────────────────────────
            bool httpOk = false;
            string httpDetail;
            try
            {
                using var cts = SMDWin.Services.SharedHttp.TimeoutCts(TimeSpan.FromSeconds(3), ct);
                var resp = await SMDWin.Services.SharedHttp.Instance.GetAsync("https://www.google.com/generate_204", cts.Token);
                httpOk = resp.IsSuccessStatusCode || (int)resp.StatusCode == 204;
                httpDetail = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex) { httpDetail = $"Failed: {ex.Message}"; }
            result.HttpOk = httpOk;
            result.HttpDetail = httpDetail;

            if (!httpOk)
            {
                result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "HttpDown", "Ping works everywhere, but a real web request failed — possibly a firewall, proxy, or captive portal.");
                result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortHttpDown", "HTTP blocked despite ping OK");
                return result;
            }

            result.Summary = SMDWin.Services.LanguageService.S("NetDiag", "AllOk", "No problem detected right now — router, internet, DNS, and web access all respond normally.");
            result.ShortSummary = SMDWin.Services.LanguageService.S("NetDiag", "ShortAllOk", "No problem detected");
            return result;
        }

        private static async Task<(bool ok, long ms)> PingHostAsync(string host, int timeoutMs, CancellationToken ct)
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 500), ct);
                return (reply.Status == System.Net.NetworkInformation.IPStatus.Success, reply.RoundtripTime);
            }
            catch { return (false, 0); }
        }

        /// <summary>Default gateway of the first active, non-loopback adapter with a real IPv4 gateway set. If several are
        /// active at once (VPN, multi-homed, dual NIC) — unusual, but the app does not assume a "classic" network — this is
        /// reported through <paramref name="ambiguous"/>, so the UI can warn (tooltip) that the result may be inexact,
        /// instead of silently picking a possibly wrong gateway.</summary>
        public static string? GetDefaultGateway(out bool ambiguous)
        {
            ambiguous = false;
            try
            {
                var gateways = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                              && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .SelectMany(ni => ni.GetIPProperties().GatewayAddresses)
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                             && !a.Equals(System.Net.IPAddress.Any))
                    .Distinct()
                    .ToList();

                if (gateways.Count == 0) return null;
                ambiguous = gateways.Count > 1;
                return gateways[0].ToString();
            }
            catch (Exception ex) { AppLogger.Debug($"GetDefaultGateway: {ex.Message}"); return null; }
        }
    }
}
