
using System;
using System.Collections.Generic;
using System.IO;

namespace SMDWin.Services
{
    public class MiniDumpModule
    {
        public string Name = "";
        public ulong BaseOfImage;
        public uint SizeOfImage;
        public uint TimeDateStamp;
        public string Category => MiniDumpParser.CategorizeDriver(Name);
        public MiniDumpParser.DriverKind Kind => MiniDumpParser.GetDriverKind(Name);
        public bool IsMicrosoft => Name.StartsWith("nt", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("hal.", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("win32k", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("Wdf", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("msrpc", StringComparison.OrdinalIgnoreCase);
    }

    public static class MiniDumpParser
    {
        private const uint MinidumpSignature = 0x504D444D; // "MDMP" little-endian
        private const uint ModuleListStreamType = 4;

        /// <summary>Reads the list of loaded modules/drivers from a .dmp file.
        /// Throws only if the file is not a valid minidump at all — any other problem (missing stream, etc.) returns an empty
        /// list, not an exception.</summary>
        public static List<MiniDumpModule> GetModules(string dmpPath)
        {
            var result = new List<MiniDumpModule>();
            using var fs = new FileStream(dmpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            uint signature = br.ReadUInt32();
            if (signature != MinidumpSignature)
                throw new InvalidDataException("Nu e un fisier minidump valid (semnatura MDMP lipseste).");

            br.ReadUInt32();                              // Version — not needed
            uint numberOfStreams = br.ReadUInt32();
            uint streamDirectoryRva = br.ReadUInt32();
            // CheckSum(4) + Reserved/TimeDateStamp(4) + Flags(8) — jump directly to the directory

            fs.Seek(streamDirectoryRva, SeekOrigin.Begin);
            uint moduleListRva = 0;
            for (int i = 0; i < numberOfStreams; i++)
            {
                uint streamType = br.ReadUInt32();
                br.ReadUInt32();          // DataSize
                uint rva = br.ReadUInt32();
                if (streamType == ModuleListStreamType) { moduleListRva = rva; break; }
            }
            if (moduleListRva == 0) return result; // the stream is missing — an unusual dump, exit cleanly

            fs.Seek(moduleListRva, SeekOrigin.Begin);
            uint numberOfModules = br.ReadUInt32();
            // Protection against a corrupt/truncated file that would ask for an absurd number of modules
            if (numberOfModules > 2000) return result;

            const int ModuleStructSize = 108; // MINIDUMP_MODULE, layout documentat Microsoft
            for (int i = 0; i < numberOfModules; i++)
            {
                long moduleStart = fs.Position;
                if (moduleStart + ModuleStructSize > fs.Length) break;

                ulong baseOfImage = br.ReadUInt64();
                uint sizeOfImage  = br.ReadUInt32();
                br.ReadUInt32();                          // CheckSum
                uint timeDateStamp = br.ReadUInt32();
                uint moduleNameRva = br.ReadUInt32();
                // the rest of the struct (VersionInfo + CvRecord + MiscRecord + Reserved0/1) — not needed, skip straight to the next
                // module by the fixed size.
                fs.Seek(moduleStart + ModuleStructSize, SeekOrigin.Begin);

                string name = ReadMinidumpString(fs, br, moduleNameRva);
                // The full name is a path ("\SystemRoot\System32\drivers\x.sys") — only the file name is kept, that is what matters
                // for identification.
                string shortName = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;

                result.Add(new MiniDumpModule
                {
                    Name = shortName,
                    BaseOfImage = baseOfImage,
                    SizeOfImage = sizeOfImage,
                    TimeDateStamp = timeDateStamp,
                });
            }
            return result;
        }

        private static string ReadMinidumpString(FileStream fs, BinaryReader br, uint rva)
        {
            if (rva == 0 || rva >= fs.Length) return "(necunoscut)";
            long savedPos = fs.Position;
            try
            {
                fs.Seek(rva, SeekOrigin.Begin);
                uint lengthBytes = br.ReadUInt32(); // length in bytes, UTF-16, without a terminator
                if (lengthBytes > 1024) return "(nume prea lung)";
                var chars = new char[lengthBytes / 2];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = (char)br.ReadUInt16();
                return new string(chars);
            }
            catch { return "(necunoscut)"; }
            finally { fs.Position = savedPos; }
        }

        /// <summary>Finds which driver contains a given address — used to try to identify the faulting driver from the bugcheck
        /// parameters (see the comment at the top of the file for the limitations).</summary>
        public static MiniDumpModule? FindModuleContaining(List<MiniDumpModule> modules, ulong address)
        {
            if (address == 0) return null;
            foreach (var m in modules)
                if (address >= m.BaseOfImage && m.SizeOfImage > 0 && address < m.BaseOfImage + m.SizeOfImage)
                    return m;
            return null;
        }

        /// <summary>Tries to identify the faulting driver by testing each bugcheck parameter (already extracted from the Event Log
        /// by CrashService) as a possible memory address against the ranges of the loaded modules. Best effort — not every
        /// bugcheck code has a real address among its parameters.</summary>
        public static MiniDumpModule? GuessFaultingModule(List<MiniDumpModule> modules, string? bugcheckParams)
        {
            if (string.IsNullOrWhiteSpace(bugcheckParams) || modules.Count == 0) return null;
            foreach (var part in bugcheckParams.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) p = p[2..];
                if (ulong.TryParse(p, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out ulong addr))
                {
                    var found = FindModuleContaining(modules, addr);
                    // Ignore the hit if it is the Windows kernel itself (ntoskrnl.exe covers a huge range — almost any kernel address
                    // falls "inside" it, so reporting it as the "faulting driver" is not useful).
                    if (found != null && !found.IsMicrosoft) return found;
                }
            }
            return null;
        }

        /// <summary>Area a driver belongs to, independent of the interface language (the rules in <see cref="BsodAdvisor"/>
        /// and the colours on the Crash page work with it, not with the displayed text).</summary>
        public enum DriverKind { Other, Video, Network, Storage, Usb, Audio, Kernel }

        /// <summary>Approximate area from the file name — answers "was it graphics, network, storage or something else".
        /// Best effort, not authoritative.</summary>
        public static DriverKind GetDriverKind(string? moduleName)
        {
            string n = (moduleName ?? "").ToLowerInvariant();
            if (n.Length == 0) return DriverKind.Other;
            string[] video   = { "nvlddmkm", "atikmpag", "atikmdag", "igdkmd", "dxgkrnl", "dxgmms", "amdkmdag", "nvkflt" };
            string[] network = { "netwtw", "rtwlane", "rt640", "rt630", "athw", "iwlwifi", "bcmwl", "e1d", "e1r", "e1i", "l1c63", "ndis", "tcpip", "wfplwfs" };
            string[] storage = { "storahci", "stornvme", "storport", "iastor", "nvme", "disk.sys", "sdbus", "sdport",
                                 "ntfs.sys", "fastfat", "exfat", "volmgr", "partmgr", "classpnp" };
            string[] usb     = { "usbxhci", "usbhub", "usbport", "usbehci", "ucx01000", "hidclass", "hidusb" };
            string[] audio   = { "hdaudio", "hdaudbus", "rtkvhd", "portcls" };

            foreach (var k in video)   if (n.Contains(k)) return DriverKind.Video;
            foreach (var k in network) if (n.Contains(k)) return DriverKind.Network;
            foreach (var k in storage) if (n.Contains(k)) return DriverKind.Storage;
            foreach (var k in usb)     if (n.Contains(k)) return DriverKind.Usb;
            foreach (var k in audio)   if (n.Contains(k)) return DriverKind.Audio;
            if (n.StartsWith("nt") || n.StartsWith("hal.")) return DriverKind.Kernel;
            return DriverKind.Other;
        }

        /// <summary>The area name, in the interface language.</summary>
        public static string KindLabel(DriverKind kind) => kind switch
        {
            DriverKind.Video   => "Video / GPU",
            DriverKind.Network => LanguageService.S("DriverCat", "Network", "Network"),
            DriverKind.Storage => LanguageService.S("DriverCat", "Storage", "Storage"),
            DriverKind.Usb     => "USB",
            DriverKind.Audio   => "Audio",
            DriverKind.Kernel  => LanguageService.S("DriverCat", "WindowsKernel", "Windows Kernel"),
            _                  => LanguageService.S("DriverCat", "Other", "Other"),
        };

        public static string CategorizeDriver(string moduleName) => KindLabel(GetDriverKind(moduleName));
    }
}
