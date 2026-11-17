using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// What a connection is most likely for, from its port: "HTTPS", "DNS", "Remote Desktop"… Only well-known ports with a clear
    /// meaning; anything else stays empty rather than guessed. The remote port decides first (the service is on the other side);
    /// the local one is used when this computer is the server (someone connected to it).
    /// </summary>
    public static class NetPortServices
    {
        private static readonly Dictionary<ushort, string> Tcp = new()
        {
            [20] = "FTP data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "Mail (SMTP)", [53] = "DNS",
            [80] = "Web (HTTP)", [110] = "Mail (POP3)", [119] = "News (NNTP)", [135] = "Windows RPC", [139] = "Windows sharing (NetBIOS)",
            [143] = "Mail (IMAP)", [389] = "Directory (LDAP)", [443] = "Web (HTTPS)", [445] = "Windows file sharing (SMB)",
            [465] = "Mail (SMTPS)", [587] = "Mail (submission)", [636] = "Directory (LDAPS)", [853] = "Encrypted DNS (DoT)",
            [993] = "Mail (IMAPS)", [995] = "Mail (POP3S)", [1194] = "VPN (OpenVPN)", [1433] = "SQL Server", [1723] = "VPN (PPTP)",
            [1883] = "IoT (MQTT)", [1935] = "Live streaming (RTMP)", [3074] = "Xbox Live", [3306] = "MySQL", [3389] = "Remote Desktop",
            [5222] = "Chat (XMPP)", [5228] = "Google push / Play", [5353] = "Local discovery (mDNS)", [5432] = "PostgreSQL",
            [5900] = "Remote control (VNC)", [5938] = "TeamViewer", [6568] = "AnyDesk", [7680] = "Windows Update (delivery optimisation)",
            [8080] = "Web (alternate)", [8443] = "Web (HTTPS alternate)", [8883] = "IoT (MQTT TLS)", [9100] = "Printer (raw)",
        };

        private static readonly Dictionary<ushort, string> Udp = new()
        {
            [53] = "DNS", [67] = "DHCP", [68] = "DHCP", [123] = "Time (NTP)", [137] = "Windows names (NetBIOS)",
            [138] = "Windows sharing (NetBIOS)", [443] = "Web (QUIC / HTTP/3)", [500] = "VPN (IPsec)", [1194] = "VPN (OpenVPN)",
            [1900] = "Device discovery (SSDP)", [3074] = "Xbox Live", [3478] = "Calls (STUN/TURN)", [3479] = "Calls (STUN/TURN)",
            [3480] = "Calls (STUN/TURN)", [3702] = "Device discovery (WS-Discovery)", [4500] = "VPN (IPsec NAT-T)",
            [5353] = "Local discovery (mDNS)", [5355] = "Local names (LLMNR)", [7680] = "Windows Update (delivery optimisation)",
            [51820] = "VPN (WireGuard)",
        };

        public static string Name(ushort remotePort, ushort localPort, bool udp)
        {
            var table = udp ? Udp : Tcp;
            if (table.TryGetValue(remotePort, out var s)) return s;
            if (udp && remotePort >= 19302 && remotePort <= 19309) return "Calls (Google STUN)";
            if (udp && remotePort >= 50000 && remotePort <= 59999 && localPort >= 50000) return "";   // media streams: too ambiguous
            if (remotePort >= 27015 && remotePort <= 27050) return "Games (Steam)";
            if (remotePort >= 6881 && remotePort <= 6889) return "File sharing (BitTorrent)";
            // this computer is the server: name the local port
            if (remotePort >= 49152 && table.TryGetValue(localPort, out var l)) return l + " (incoming)";
            return "";
        }
    }
}
