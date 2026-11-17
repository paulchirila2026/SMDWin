using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// The USB link of a USB disk or stick: what the device supports and how it is connected right now — read from
    /// the USB hub it is plugged into, not guessed from the model or the measured speed (that depends on the disk, not on USB).
    ///
    /// Path: the disk's PnP node (Win32_DiskDrive.PNPDeviceID) → its parents until the USB device itself ("USB\VID_…", not an
    /// "&amp;MI_" interface) → that device's port number (CM_DRP_ADDRESS) and its parent, the hub → the hub's device interface
    /// → IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX (speed + descriptor bcdUSB) and _EX_V2 (SuperSpeed / SuperSpeedPlus
    /// capable and operating flags). Works for USB mass storage and UASP enclosures. Any failure leaves the fields empty.
    /// </summary>
    internal static class UsbLinkInfo
    {
        public static void Fill(DiskHealthEntry d)
        {
            string? pnp = PnpIdOfDisk(d.DiskIndex);
            if (string.IsNullOrEmpty(pnp)) return;
            if (CM_Locate_DevNodeW(out uint node, pnp, 0) != 0) return;

            // up to the USB device (skip the USBSTOR / UASPStor / SCSI nodes and the interfaces of a composite device)
            uint dev = 0;
            string devId = "";
            uint cur = node;
            for (int i = 0; i < 8; i++)
            {
                if (CM_Get_Parent(out uint parent, cur, 0) != 0) return;
                string id = DeviceId(parent);
                if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) break;
                if (id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                { dev = parent; devId = id; break; }
                cur = parent;
            }
            if (dev == 0) return;

            int port = (int)(RegistryDword(dev, CM_DRP_ADDRESS) ?? 0);
            if (port <= 0 || CM_Get_Parent(out uint hub, dev, 0) != 0) return;
            string? hubPath = HubInterfacePath(DeviceId(hub));
            if (hubPath == null) return;

            using var h = CreateFileW(hubPath, GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { AppLogger.Debug($"UsbLinkInfo: cannot open the hub of {devId} ({Marshal.GetLastWin32Error()})"); return; }

            // 1) speed + bcdUSB
            var buf = new byte[512];
            BitConverter.GetBytes(port).CopyTo(buf, 0);
            if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero)) return;
            int bcdUsb = BitConverter.ToUInt16(buf, 6);    // ConnectionIndex (4) + bLength (1) + bDescriptorType (1)
            int speed  = buf[23];                          // + 18-byte descriptor + CurrentConfigurationValue (1)

            // 2) SuperSpeed flags (Windows 8 and later)
            uint flags = 0;
            var v2 = new byte[16];
            BitConverter.GetBytes(port).CopyTo(v2, 0);
            BitConverter.GetBytes(16).CopyTo(v2, 4);
            BitConverter.GetBytes(0x7).CopyTo(v2, 8);      // the caller understands USB 1.1, 2.0 and 3.x
            if (DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, v2, v2.Length, v2, v2.Length, out _, IntPtr.Zero))
                flags = BitConverter.ToUInt32(v2, 12);
            bool opSuper = (flags & 1) != 0, capSuper = (flags & 2) != 0, opPlus = (flags & 4) != 0, capPlus = (flags & 8) != 0;

            string Text(int level) => level switch
            {
                4 => "USB 3.1 · 10 Gb/s",
                3 => "USB 3.0 · 5 Gb/s",
                2 => "USB 2.0 · 480 Mb/s",
                _ => "USB 1.1",
            };
            int connected = opPlus ? 4 : opSuper || speed >= 3 ? 3 : speed == 2 ? 2 : 1;
            // a USB 3 device on a USB 2 link answers with a USB 2 descriptor, so the flags say more than bcdUSB
            int supported = capPlus ? 4 : capSuper ? 3 : bcdUsb >= 0x0310 ? 4 : bcdUsb >= 0x0300 ? 3 : bcdUsb >= 0x0200 ? 2 : 1;
            supported = Math.Max(supported, connected);

            d.UsbConnected = Text(connected);
            d.UsbSupported = Text(supported);
            d.UsbSlowLink = supported >= 3 && connected < supported;
            AppLogger.Debug($"UsbLinkInfo: disk #{d.DiskIndex} {devId} port {port}: speed {speed}, bcdUSB 0x{bcdUsb:X4}, flags 0x{flags:X} → {d.UsbLinkText}");
        }

        private static string? PnpIdOfDisk(int index)
        {
            try
            {
                using var s = WmiHelper.Searcher($"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = {index}");
                using var col = s.Get();
                foreach (ManagementObject o in col) using (o) return o["PNPDeviceID"]?.ToString();
            }
            catch (Exception ex) { AppLogger.Debug($"UsbLinkInfo PNPDeviceID: {ex.Message}"); }
            return null;
        }

        private static string DeviceId(uint node)
        {
            var sb = new StringBuilder(400);
            return CM_Get_Device_IDW(node, sb, sb.Capacity, 0) == 0 ? sb.ToString() : "";
        }

        private static uint? RegistryDword(uint node, uint prop)
        {
            var b = new byte[4];
            uint len = 4;
            return CM_Get_DevNode_Registry_PropertyW(node, prop, out _, b, ref len, 0) == 0 && len >= 4 ? BitConverter.ToUInt32(b, 0) : null;
        }

        private static string? HubInterfacePath(string hubId)
        {
            if (hubId.Length == 0) return null;
            var g = GUID_DEVINTERFACE_USB_HUB;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, hubId, 0) != 0 || len <= 1) return null;
            var chars = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, hubId, chars, len, 0) != 0) return null;
            string first = new string(chars).Split('\0').FirstOrDefault(x => x.Length > 0) ?? "";
            return first.Length > 0 ? first : null;
        }

        // ── interop ──────────────────────────────────────────────────────
        private static Guid GUID_DEVINTERFACE_USB_HUB = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
        private const uint CM_DRP_ADDRESS = 0x1D;
        private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX    = 0x220448;
        private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;
        private const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType,
            byte[] buffer, ref uint pulLength, uint ulFlags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid classGuid, string deviceId, uint ulFlags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_ListW(ref Guid classGuid, string deviceId, char[] buffer, uint bufferLen, uint ulFlags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inSize,
            byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    }
}
