using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Identifies the router: the gateway MAC from the ARP table (SendARP), the manufacturer from the OUI database and, when
    /// the router answers a UPnP search (SSDP), its description (manufacturer, model). Everything stays on the local network;
    /// the description is downloaded only from the gateway address.
    /// </summary>
    public static class RouterIdentityReader
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

        private static RouterIdentity? _cached;
        private static string _cachedGateway = "";
        private static DateTime _cachedAt = DateTime.MinValue;

        /// <summary>Router identity (remembered for 10 minutes for the same gateway); null when there is no gateway.</summary>
        public static async Task<RouterIdentity?> ReadAsync(CancellationToken ct = default)
        {
            string? gateway = NetworkDiagnosisService.GetDefaultGateway(out _);
            if (gateway == null) return null;
            if (_cached != null && _cachedGateway == gateway && (DateTime.UtcNow - _cachedAt).TotalMinutes < 10) return _cached;

            string mac = ReadMac(gateway);
            string vendor = mac.Length > 0 ? OuiLookupService.Lookup(mac) ?? "" : "";
            var (maker, model, friendly) = await ReadUpnpAsync(gateway, ct).ConfigureAwait(false);
            var identity = new RouterIdentity(gateway, mac, vendor, maker, model, friendly);
            _cached = identity; _cachedGateway = gateway; _cachedAt = DateTime.UtcNow;
            AppLogger.Debug($"RouterIdentity: {gateway} mac={mac} vendor={vendor} upnp={maker} {model}");
            return identity;
        }

        private static string ReadMac(string gateway)
        {
            try
            {
                if (!IPAddress.TryParse(gateway, out var ip)) return "";
                uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                var mac = new byte[6];
                uint len = (uint)mac.Length;
                if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return "";
                return string.Join(":", mac.Take((int)len).Select(b => b.ToString("X2")));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
            {
                AppLogger.Debug($"RouterIdentity ARP: {ex.Message}");
                return "";
            }
        }

        private static async Task<(string Manufacturer, string Model, string Friendly)> ReadUpnpAsync(string gateway, CancellationToken ct)
        {
            try
            {
                string? location = await FindUpnpLocationAsync(gateway, ct).ConfigureAwait(false);
                if (location == null || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttp || uri.Host != gateway)
                    return ("", "", "");     // downloaded only from the gateway itself

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2.5) };
                string xml = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                string Field(string name) => device?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
                return (Field("manufacturer"), Field("modelName"), Field("friendlyName"));
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException
                                          or System.Xml.XmlException or SocketException or InvalidOperationException)
            {
                AppLogger.Debug($"RouterIdentity UPnP: {ex.Message}");
                return ("", "", "");
            }
        }

        // SSDP search on the local network; returns the LOCATION field of the answer coming from the gateway
        private static async Task<string?> FindUpnpLocationAsync(string gateway, CancellationToken ct)
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: upnp:rootdevice\r\n\r\n");
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(1800);
            try
            {
                while (!limit.IsCancellationRequested)
                {
                    var answer = await udp.ReceiveAsync(limit.Token).ConfigureAwait(false);
                    if (answer.RemoteEndPoint.Address.ToString() != gateway) continue;
                    foreach (string line in Encoding.ASCII.GetString(answer.Buffer).Split('\n'))
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) return line[9..].Trim();
                }
            }
            catch (OperationCanceledException) { AppLogger.Debug("RouterIdentity SSDP: no answer from the gateway"); }
            return null;
        }
    }
}
