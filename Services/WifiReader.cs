// SMDWin — Services/WifiReader.cs
// Wi-Fi info through wlanapi.dll: SSID, RSSI (dBm), channel, security, link speed.
// No UWP, no NuGet — direct P/Invoke into the Windows WLAN API.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SMDWin.Services
{
    public sealed class WifiInfo
    {
        public bool   IsConnected   { get; set; }
        public string Ssid          { get; set; } = "";
        public int    Rssi          { get; set; }    // dBm (e.g. -65)
        public int    SignalPct     { get; set; }    // 0-100 (Windows quality)
        // the real channel needs WlanGetNetworkBssList (not implemented) — stays 0.
        public int    Channel         { get; set; }    // 1-14 (2.4GHz) or 36-177 (5GHz)
        public string Band          { get; set; } = "";    // "2.4 GHz" / "5 GHz" / "6 GHz"
        public string Security      { get; set; } = "";    // WPA2, WPA3, Open, etc.
        public int    LinkSpeedMbps   { get; set; }    // Rx link speed (Mbps)
        // read directly from the WLAN API (ulTxRate), independent of the Windows language.
        public int    TxLinkSpeedMbps { get; set; }    // Tx link speed (Mbps)
        public string AdapterName   { get; set; } = "";
        public string RssiBar       => Rssi >= -50 ? "▂▄▆█"
                                     : Rssi >= -65 ? "▂▄▆░"
                                     : Rssi >= -75 ? "▂▄░░"
                                     : Rssi >= -85 ? "▂░░░" : "░░░░";
        public string RssiLabel     => Rssi >= -50 ? "Excellent"
                                     : Rssi >= -65 ? "Good"
                                     : Rssi >= -75 ? "Fair"
                                     : Rssi >= -85 ? "Weak" : "Poor";
    }

    public static class WifiReader
    {
        // ── wlanapi.dll P/Invoke ──────────────────────────────────────────────
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanOpenHandle(uint dwClientVersion,
            IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanEnumInterfaces(IntPtr hClientHandle,
            IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanQueryInterface(IntPtr hClientHandle,
            ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode, IntPtr pReserved,
            out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern void WlanFreeMemory(IntPtr pMemory);

        private enum WLAN_INTF_OPCODE : uint
        {
            wlan_intf_opcode_current_connection = 7,
            wlan_intf_opcode_rssi               = 0x10000102,   // LONG, dBm — not on Microsoft's list of location-gated calls
        }

        private const uint ERROR_ACCESS_DENIED = 5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid   InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int    isState; // 0=not ready, 1=not connected, 2=ad hoc, 3=disconnected, 4=disconnecting, 5=connecting, 6=connected, 7=associating
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_INTERFACE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            // WLAN_INTERFACE_INFO[dwNumberOfItems] follows
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
            public string GetString() =>
                uSSIDLength > 0 && ucSSID != null
                    ? Encoding.UTF8.GetString(ucSSID, 0, (int)Math.Min(uSSIDLength, 32))
                    : "";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID  dot11Ssid;
            public uint        dot11BssType;      // 1=infra, 2=adhoc
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[]      dot11Bssid;
            public uint        dot11PhyType;      // PHY type
            public uint        uDot11PhyIndex;
            public uint        wlanSignalQuality; // 0-100 (Windows quality bar)
            public uint        ulRxRate;          // kbps receive
            public uint        ulTxRate;          // kbps transmit
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_SECURITY_ATTRIBUTES
        {
            public int    bSecurityEnabled;
            public int    bOneXEnabled;
            public uint   dot11AuthAlgorithm;    // 1=Open, 2=WPA, 6=WPA2, 7=WPA3
            public uint   dot11CipherAlgorithm;  // 0=None, 4=TKIP, 8=CCMP
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES
        {
            public uint                      isState;
            public uint                      wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string                    strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES  wlanSecurityAttributes;
        }

        private static WifiInfo? _cached;
        private static DateTime  _lastUpdate = DateTime.MinValue;
        private static readonly TimeSpan _cacheInterval = TimeSpan.FromSeconds(5);

        /// <summary>True when the last full read was refused by Windows (location access off for SMD Win).</summary>
        public static bool LocationDenied => SettingsService.Current.WifiLocationAccess == "denied";

        /// <summary>
        /// Windows 11 24H2 treats the connected network's details (SSID, band, security — WlanQueryInterface current_connection,
        /// the network list, netsh wlan) as location and asks the user the first time an app reads them. So:
        ///  • by default (Dashboard, tray, Network page) only calls that are not gated are used: the network name comes from the
        ///    network profile (Network List Manager — usually the same as the SSID) and the signal from the RSSI query;
        ///  • the full read runs only with <paramref name="allowLocationPrompt"/> (the Wi-Fi › Live tab, opened by the user) or after
        ///    the user has already allowed it once — then it no longer prompts.
        /// </summary>
        public static WifiInfo Read(bool forceRefresh = false, bool allowLocationPrompt = false)
        {
            bool full = allowLocationPrompt || SettingsService.Current.WifiLocationAccess == "granted";
            if (!full)
            {
                if (!forceRefresh && _cached != null && (DateTime.UtcNow - _lastUpdate) < _cacheInterval) return _cached;
                _cached = ReadLight();
                _lastUpdate = DateTime.UtcNow;
                return _cached;
            }
            if (!forceRefresh && _cached != null && _cachedFull &&
                (DateTime.UtcNow - _lastUpdate) < _cacheInterval)
                return _cached;
            _cachedFull = true;

            var result = new WifiInfo();
            IntPtr hClient = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;

            try
            {
                uint version = 0;
                uint ret = WlanOpenHandle(2, IntPtr.Zero, out version, out hClient);
                if (ret != 0) { _cached = result; return result; }

                ret = WlanEnumInterfaces(hClient, IntPtr.Zero, out pIfList);
                if (ret != 0 || pIfList == IntPtr.Zero) { _cached = result; return result; }

                uint count = (uint)Marshal.ReadInt32(pIfList);
                // Items start after dwNumberOfItems + dwIndex (8 bytes)
                int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

                for (int i = 0; i < count && !result.IsConnected; i++)
                {
                    IntPtr itemPtr = pIfList + 8 + i * itemSize;
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPtr);

                    if (info.isState != 1) continue; // 1 = connected (confirmat MSDN)

                    result.AdapterName = info.strInterfaceDescription;
                    Guid guid = info.InterfaceGuid;

                    IntPtr pConnData = IntPtr.Zero;
                    uint dataSize = 0;
                    ret = WlanQueryInterface(hClient, ref guid,
                        WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                        IntPtr.Zero, out dataSize, out pConnData, IntPtr.Zero);
                    RememberAccess(ret);
                    if (ret == ERROR_ACCESS_DENIED)
                    {
                        // location refused: what can be read without it
                        if (pIfList != IntPtr.Zero) { WlanFreeMemory(pIfList); pIfList = IntPtr.Zero; }
                        if (hClient != IntPtr.Zero) { WlanCloseHandle(hClient, IntPtr.Zero); hClient = IntPtr.Zero; }
                        _cachedFull = false;
                        _cached = ReadLight();
                        _lastUpdate = DateTime.UtcNow;
                        return _cached;
                    }

                    if (ret == 0 && pConnData != IntPtr.Zero)
                    {
                        try
                        {
                            var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(pConnData);
                            var assoc = conn.wlanAssociationAttributes;
                            var sec   = conn.wlanSecurityAttributes;

                            result.IsConnected   = true;
                            result.Ssid          = assoc.dot11Ssid.GetString();
                            result.SignalPct     = (int)assoc.wlanSignalQuality;
                            // Windows quality 0-100 → dBm: -100 + quality/2
                            result.Rssi          = -100 + (int)assoc.wlanSignalQuality / 2;
                            result.LinkSpeedMbps   = (int)(assoc.ulRxRate / 1000);
                            result.TxLinkSpeedMbps = (int)(assoc.ulTxRate / 1000);

                            // Security string
                            result.Security = sec.dot11AuthAlgorithm switch
                            {
                                1 => "Open",
                                2 => "WPA-TKIP",
                                3 => "WPA-AES",
                                6 => sec.dot11CipherAlgorithm == 4 ? "WPA2-TKIP" : "WPA2",
                                7 => "WPA3",
                                _ => $"Auth{sec.dot11AuthAlgorithm}"
                            };

                            // Channel from RSSI + PHY type heuristic
                            // Real channel needs WlanGetNetworkBssList — aproximam
                            result.Band = assoc.dot11PhyType switch
                            {
                                7 => "5 GHz",   // 802.11n/5GHz
                                8 => "2.4 GHz", // 802.11n/2.4GHz
                                9 => "5 GHz",   // 802.11ac
                                10 => "6 GHz",  // 802.11ax/6GHz
                                _ => assoc.dot11PhyType >= 9 ? "5 GHz" : "2.4 GHz"
                            };
                        }
                        finally { WlanFreeMemory(pConnData); }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"WifiReader: {ex.Message}");
            }
            finally
            {
                if (pIfList != IntPtr.Zero) WlanFreeMemory(pIfList);
                if (hClient != IntPtr.Zero) WlanCloseHandle(hClient, IntPtr.Zero);
            }

            _cached = result;
            _lastUpdate = DateTime.UtcNow;
            return result;
        }

        private static bool _cachedFull;

        private static void RememberAccess(uint ret)
        {
            string state = ret == 0 ? "granted" : ret == ERROR_ACCESS_DENIED ? "denied" : "";
            if (state.Length == 0 || SettingsService.Current.WifiLocationAccess == state) return;
            SettingsService.Current.WifiLocationAccess = state;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Debug($"WifiReader save access: {ex.Message}"); }
        }

        /// <summary>Connected or not, the network name (from the network profile) and the signal (RSSI) — no location prompt.</summary>
        private static WifiInfo ReadLight()
        {
            var result = new WifiInfo();
            IntPtr hClient = IntPtr.Zero, pIfList = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out hClient) != 0) return result;
                if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pIfList) != 0 || pIfList == IntPtr.Zero) return result;
                uint count = (uint)Marshal.ReadInt32(pIfList);
                int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(pIfList + 8 + i * itemSize);
                    if (info.isState != 1) continue;   // 1 = connected
                    result.IsConnected = true;
                    result.AdapterName = info.strInterfaceDescription;
                    Guid guid = info.InterfaceGuid;

                    IntPtr pData = IntPtr.Zero;
                    if (WlanQueryInterface(hClient, ref guid, WLAN_INTF_OPCODE.wlan_intf_opcode_rssi, IntPtr.Zero,
                            out uint size, out pData, IntPtr.Zero) == 0 && pData != IntPtr.Zero)
                    {
                        try
                        {
                            int rssi = Marshal.ReadInt32(pData);
                            if (rssi < 0 && rssi > -120)
                            {
                                result.Rssi = rssi;
                                // the inverse of the full read's conversion (dBm = -100 + quality/2)
                                result.SignalPct = Math.Clamp((rssi + 100) * 2, 0, 100);
                            }
                        }
                        finally { WlanFreeMemory(pData); }
                    }
                    result.Ssid = NetworkNameFor(guid);
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"WifiReader.ReadLight: {ex.Message}"); }
            finally
            {
                if (pIfList != IntPtr.Zero) WlanFreeMemory(pIfList);
                if (hClient != IntPtr.Zero) WlanCloseHandle(hClient, IntPtr.Zero);
            }
            return result;
        }

        private static string _nlmName = "";
        private static Guid _nlmAdapter;
        private static DateTime _nlmAt = DateTime.MinValue;

        /// <summary>
        /// The name of the network the adapter is connected to, from Windows' Network List Manager (the same name as in Settings ›
        /// Network). For Wi-Fi it is the SSID, sometimes with a number added ("Home 2"). Cached 30 s.
        /// </summary>
        private static string NetworkNameFor(Guid adapter)
        {
            if (adapter == _nlmAdapter && (DateTime.UtcNow - _nlmAt).TotalSeconds < 30) return _nlmName;
            string name = "";
            INetworkListManager? nlm = null;
            try
            {
                // typed COM interfaces: the dynamic version failed on Windows 11 ("Value does not fall within the expected range")
                var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                if (type != null && Activator.CreateInstance(type) is INetworkListManager mgr)
                {
                    nlm = mgr;
                    var en = mgr.GetNetworkConnections();
                    var one = new INetworkConnection[1];
                    while (name.Length == 0 && en.Next(1, one, out uint got) == 0 && got == 1)
                    {
                        var conn = one[0];
                        try
                        {
                            if (conn.GetAdapterId() == adapter)
                            {
                                var net = conn.GetNetwork();
                                try { name = net.GetName() ?? ""; } finally { Marshal.ReleaseComObject(net); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(conn); }
                    }
                    Marshal.ReleaseComObject(en);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"WifiReader.NetworkNameFor: {ex.Message}"); }
            finally { if (nlm != null) Marshal.ReleaseComObject(nlm); }
            _nlmName = name; _nlmAdapter = adapter; _nlmAt = DateTime.UtcNow;
            return name;
        }
    }

    // ── Network List Manager (netlistmgr.h) — only the members used, in vtable order ──────────────────────────
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNewEnum();
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] elts, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    }
}