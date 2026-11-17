using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// The saved Wi-Fi networks, read through the Windows WLAN API (WlanGetProfileList / WlanGetProfile) instead of the text
    /// of "netsh wlan show profiles" — that text is translated with the Windows language ("All User Profile" is
    /// "Profil Tous les utilisateurs" in French, "Profil pentru toți utilizatorii" in Romanian), so the list stayed empty on
    /// a non-English Windows. The profile XML has the same tags in every language.
    /// The password comes in clear only when the app runs as administrator (WLAN_PROFILE_GET_PLAINTEXT_KEY); otherwise it is
    /// encrypted and is not shown.
    /// </summary>
    public static class WlanProfiles
    {
        public sealed record Profile(string Name, string Ssid, string Password, string Auth, string Cipher);

        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint WlanGetProfile(IntPtr handle, ref Guid interfaceGuid, string profileName, IntPtr reserved,
                                                  out IntPtr profileXml, ref uint flags, out uint grantedAccess);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

        private const uint WLAN_PROFILE_GET_PLAINTEXT_KEY = 4;
        // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + state (4) = 532 bytes
        private const int InterfaceInfoSize = 532;
        // WLAN_PROFILE_INFO: WCHAR[256] name (512) + flags (4) = 516 bytes
        private const int ProfileInfoSize = 516;
        // both lists start with dwNumberOfItems + dwIndex
        private const int ListHeader = 8;

        /// <summary>The saved networks, or null when the WLAN service is not available (no Wi-Fi adapter, service stopped) —
        /// the caller can then fall back to netsh.</summary>
        public static List<Profile>? Read()
        {
            IntPtr handle = IntPtr.Zero, ifList = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return null;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out ifList) != 0) return null;

                var result = new List<Profile>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int ifCount = Marshal.ReadInt32(ifList, 0);
                for (int i = 0; i < ifCount; i++)
                {
                    var guid = Marshal.PtrToStructure<Guid>(ifList + ListHeader + i * InterfaceInfoSize);
                    if (WlanGetProfileList(handle, ref guid, IntPtr.Zero, out IntPtr profList) != 0) continue;
                    try
                    {
                        int count = Marshal.ReadInt32(profList, 0);
                        for (int p = 0; p < count; p++)
                        {
                            string name = Marshal.PtrToStringUni(profList + ListHeader + p * ProfileInfoSize) ?? "";
                            if (name.Length == 0 || !seen.Add(name)) continue;
                            result.Add(ReadProfile(handle, guid, name));
                        }
                    }
                    finally { WlanFreeMemory(profList); }
                }
                return result;
            }
            catch (DllNotFoundException) { return null; }     // Windows Server without the wireless feature
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "WlanProfiles.Read");
                return null;
            }
            finally
            {
                if (ifList != IntPtr.Zero) WlanFreeMemory(ifList);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
        }

        private static Profile ReadProfile(IntPtr handle, Guid guid, string name)
        {
            uint flags = WLAN_PROFILE_GET_PLAINTEXT_KEY;
            if (WlanGetProfile(handle, ref guid, name, IntPtr.Zero, out IntPtr xmlPtr, ref flags, out _) != 0)
                return new Profile(name, name, "—", "—", "—");
            try
            {
                return Parse(name, Marshal.PtrToStringUni(xmlPtr) ?? "");
            }
            finally { WlanFreeMemory(xmlPtr); }
        }

        /// <summary>Reads the profile XML (namespace http://www.microsoft.com/networking/WLAN/profile/v1). Public for tests.</summary>
        public static Profile Parse(string name, string xml)
        {
            string ssid = name, auth = "—", cipher = "—", password = "—";
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var ssidEl = doc.Root?.Element(ns + "SSIDConfig")?.Element(ns + "SSID");
                string? ssidName = ssidEl?.Element(ns + "name")?.Value;
                if (!string.IsNullOrWhiteSpace(ssidName)) ssid = ssidName;

                var security = doc.Root?.Element(ns + "MSM")?.Element(ns + "security");
                var authEnc = security?.Element(ns + "authEncryption");
                auth = FriendlyAuth(authEnc?.Element(ns + "authentication")?.Value);
                string enc = authEnc?.Element(ns + "encryption")?.Value ?? "";
                cipher = enc.Length == 0 ? "—" : enc.Equals("none", StringComparison.OrdinalIgnoreCase) ? "—" : enc.ToUpperInvariant() == "AES" ? "AES" : enc;

                var key = security?.Element(ns + "sharedKey");
                bool isProtected = string.Equals(key?.Element(ns + "protected")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                string? material = key?.Element(ns + "keyMaterial")?.Value;
                // encrypted (not running as administrator): the blob is not a password
                if (!isProtected && !string.IsNullOrEmpty(material)) password = material;
            }
            catch (Exception ex) { AppLogger.Debug($"WlanProfiles.Parse {name}: {ex.Message}"); }
            return new Profile(name, ssid, password, auth, cipher);
        }

        private static string FriendlyAuth(string? a) => (a ?? "").ToUpperInvariant() switch
        {
            "" => "—",
            "OPEN" => "Open",
            "SHARED" => "WEP",
            "WPA" => "WPA-Enterprise",
            "WPAPSK" => "WPA-Personal",
            "WPA2" => "WPA2-Enterprise",
            "WPA2PSK" => "WPA2-Personal",
            "WPA3" or "WPA3ENT" or "WPA3ENT192" => "WPA3-Enterprise",
            "WPA3SAE" => "WPA3-Personal",
            "OWE" => "Enhanced Open",
            _ => a!,
        };
    }
}
